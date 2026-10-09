#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Una regola: quando nel player succede <see cref="Event"/>, invia un comando a un
    /// dispositivo. Due trasporti: una richiesta HTTP (Home Assistant, Shelly, bridge Hue,
    /// qualunque cosa esponga un indirizzo) o un messaggio MQTT.
    /// </summary>
    internal sealed class AutomationRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        /// <summary>start, pause, resume, stop, credits.</summary>
        public string Event { get; set; } = "start";
        /// <summary>http-get, http-post, mqtt.</summary>
        public string Kind { get; set; } = "http-post";
        /// <summary>URL per HTTP; "host" o "host:porta" del broker per MQTT.</summary>
        public string Target { get; set; } = "";
        public string Topic { get; set; } = "";
        /// <summary>Corpo della richiesta POST o contenuto del messaggio MQTT.</summary>
        public string Payload { get; set; } = "";
        public string User { get; set; } = "";
        /// <summary>Password MQTT cifrata con DPAPI per l'utente di Windows (mai in chiaro su disco).</summary>
        public string ProtectedPassword { get; set; } = "";
        /// <summary>Solo durante i video: la musica non spegne le luci del salotto.</summary>
        public bool VideoOnly { get; set; } = true;

        public AutomationRule Clone() => (AutomationRule)MemberwiseClone();
    }

    internal sealed record AutomationContext(string Title, bool IsVideo, double PositionSeconds, double DurationSeconds);

    internal static class DeviceAutomations
    {
        public static readonly string[] Events = { "start", "pause", "resume", "stop", "credits" };
        public static readonly string[] Kinds = { "http-post", "http-get", "mqtt" };

        private static readonly object Sync = new();
        private static List<AutomationRule>? _rules;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "automations.json");

        public static IReadOnlyList<AutomationRule> Rules
        {
            get
            {
                lock (Sync)
                {
                    _rules ??= Load();
                    return _rules.Select(rule => rule.Clone()).ToList();
                }
            }
        }

        public static void Save(IEnumerable<AutomationRule> rules)
        {
            lock (Sync)
            {
                _rules = rules.Select(rule => rule.Clone()).ToList();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    string temporary = StorePath + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(_rules, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temporary, StorePath, overwrite: true);
                }
                catch (Exception ex) { Dbg.Warn("[AUTOMATION] save failed: " + ex.Message); }
            }
        }

        private static List<AutomationRule> Load()
        {
            try
            {
                if (File.Exists(StorePath))
                    return JsonSerializer.Deserialize<List<AutomationRule>>(File.ReadAllText(StorePath)) ?? new();
            }
            catch (Exception ex) { Dbg.Warn("[AUTOMATION] load failed: " + ex.Message); }
            return new();
        }

        public static string Protect(string password)
        {
            if (string.IsNullOrEmpty(password)) return "";
            try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        /// <summary>Esegue, senza attendere, tutte le regole attive per l'evento.</summary>
        public static void Fire(string eventName, AutomationContext context)
        {
            List<AutomationRule> matching;
            lock (Sync)
            {
                _rules ??= Load();
                matching = _rules.Where(rule => rule.Enabled && rule.Event == eventName && (context.IsVideo || !rule.VideoOnly)).Select(rule => rule.Clone()).ToList();
            }
            foreach (var rule in matching)
            {
                _ = Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var (ok, detail) = await RunAsync(rule, context, timeout.Token).ConfigureAwait(false);
                    Dbg.Log($"[AUTOMATION] {eventName} -> '{rule.Name}' ({rule.Kind}): {(ok ? "ok" : "failed")} {detail}", ok ? Dbg.LogLevel.Info : Dbg.LogLevel.Warn);
                });
            }
        }

        /// <summary>Esegue una regola e dice com'e' andata (usato anche dal tasto Prova).</summary>
        public static async Task<(bool Ok, string Detail)> RunAsync(AutomationRule rule, AutomationContext context, CancellationToken ct)
        {
            try
            {
                if (rule.Kind == "mqtt")
                {
                    if (string.IsNullOrWhiteSpace(rule.Target) || string.IsNullOrWhiteSpace(rule.Topic))
                        return (false, "broker/topic");
                    await PublishMqttAsync(rule.Target.Trim(), rule.Topic.Trim(), Fill(rule.Payload, rule, context, FillMode.Plain),
                        rule.User, Unprotect(rule.ProtectedPassword), ct).ConfigureAwait(false);
                    return (true, "MQTT");
                }

                string url = Fill(rule.Target.Trim(), rule, context, FillMode.Url);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return (false, "URL");
                using var request = new HttpRequestMessage(rule.Kind == "http-get" ? HttpMethod.Get : HttpMethod.Post, uri);
                if (rule.Kind != "http-get")
                {
                    string trimmed = rule.Payload.TrimStart();
                    bool json = trimmed.StartsWith('{') || trimmed.StartsWith('[');
                    request.Content = new StringContent(Fill(rule.Payload, rule, context, json ? FillMode.Json : FillMode.Plain), Encoding.UTF8, json ? "application/json" : "text/plain");
                }
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                return (response.IsSuccessStatusCode, "HTTP " + (int)response.StatusCode);
            }
            catch (OperationCanceledException) { return (false, "timeout"); }
            catch (Exception ex) { return (false, ex.GetType().Name + ": " + ex.Message); }
        }

        private enum FillMode { Plain, Url, Json }

        // Segnaposto: {title} {event} {position} {duration}. Il titolo arriva dai file
        // dell'utente: in un URL o in un JSON va codificato, altrimenti lo romperebbe.
        private static string Fill(string template, AutomationRule rule, AutomationContext context, FillMode mode)
        {
            if (string.IsNullOrEmpty(template) || !template.Contains('{')) return template ?? "";
            string Encode(string value) => mode switch
            {
                FillMode.Url => Uri.EscapeDataString(value),
                FillMode.Json => JsonEncodedText.Encode(value).ToString(),
                _ => value
            };
            return template
                .Replace("{title}", Encode(context.Title))
                .Replace("{event}", Encode(rule.Event))
                .Replace("{position}", ((int)Math.Round(context.PositionSeconds)).ToString(CultureInfo.InvariantCulture))
                .Replace("{duration}", ((int)Math.Round(context.DurationSeconds)).ToString(CultureInfo.InvariantCulture));
        }

        // ===== MQTT 3.1.1: CONNECT, PUBLISH (QoS 0), DISCONNECT =====
        // Una sola pubblicazione per connessione: non serve una libreria ne' una sessione
        // sempre aperta. Niente TLS: pensato per un broker sulla rete di casa.
        private static async Task PublishMqttAsync(string broker, string topic, string payload, string user, string password, CancellationToken ct)
        {
            string host = broker;
            int port = 1883;
            if (host.StartsWith("mqtt://", StringComparison.OrdinalIgnoreCase)) host = host["mqtt://".Length..];
            host = host.TrimEnd('/');
            int colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host[(colon + 1)..], out int parsed) && parsed is > 0 and < 65536)
            {
                port = parsed;
                host = host[..colon];
            }

            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var stream = client.GetStream();

            var connect = new List<byte>();
            WriteString(connect, "MQTT");
            connect.Add(4); // livello di protocollo 3.1.1
            bool hasUser = !string.IsNullOrEmpty(user);
            bool hasPassword = hasUser && !string.IsNullOrEmpty(password);
            connect.Add((byte)(0x02 | (hasUser ? 0x80 : 0) | (hasPassword ? 0x40 : 0))); // sessione pulita
            connect.Add(0); connect.Add(20); // keep-alive 20 s
            WriteString(connect, "cinecore-" + Guid.NewGuid().ToString("N")[..10]);
            if (hasUser) WriteString(connect, user);
            if (hasPassword) WriteString(connect, password);
            await stream.WriteAsync(Packet(0x10, connect), ct).ConfigureAwait(false);

            var ack = new byte[4];
            int read = 0;
            while (read < ack.Length)
            {
                int n = await stream.ReadAsync(ack.AsMemory(read, ack.Length - read), ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException("broker closed the connection");
                read += n;
            }
            if (ack[0] != 0x20 || ack[3] != 0)
                throw new IOException(ack[3] switch { 4 => "bad user name or password", 5 => "not authorised", _ => "connection refused (" + ack[3] + ")" });

            var publish = new List<byte>();
            WriteString(publish, topic);
            publish.AddRange(Encoding.UTF8.GetBytes(payload ?? ""));
            await stream.WriteAsync(Packet(0x30, publish), ct).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 0xE0, 0x00 }, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static void WriteString(List<byte> buffer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue) throw new ArgumentException("string too long");
            buffer.Add((byte)(bytes.Length >> 8));
            buffer.Add((byte)(bytes.Length & 0xFF));
            buffer.AddRange(bytes);
        }

        private static byte[] Packet(byte header, List<byte> body)
        {
            var packet = new List<byte>(body.Count + 5) { header };
            int length = body.Count;
            do
            {
                byte digit = (byte)(length % 128);
                length /= 128;
                if (length > 0) digit |= 0x80;
                packet.Add(digit);
            } while (length > 0);
            packet.AddRange(body);
            return packet.ToArray();
        }
    }
}
