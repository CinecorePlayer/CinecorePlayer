// FILE: RemoteServer.cs — v9 TCP + fullscreen remote UI + PairingRequested + trusted.json + mDNS + FrontDoor :80 proxy
// - Server TCP Http-like (TcpListener) senza URLACL.
// - UI telecomando = fullscreen nero.
// - PairingRequested(pin) quando serve mostrare PIN sul player.
// - trusted.json persistente in %AppData%\CinecorePlayer2025
// - Persistenza dispositivo ROBUSTA: cookie HttpOnly + localStorage token + Authorization Bearer
// - FrontDoor best-effort su porta 80: reverse proxy TCP verso _port (URL pulito, niente :porta)
// - mDNS best-effort: hostname univoco per PC -> IP del player
//
// #nullable enable
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CinecorePlayer2025.Utilities;

internal sealed partial class RemoteServer : IDisposable
{
    // ====================== DNS / mDNS ======================
    private readonly string _mdnsHostFqdn;
    private static readonly IPAddress MdnsMulticast = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    // ====================== campi core ======================
    private int _port;
    private readonly string _pin;
    private readonly Func<RemoteState> _getState;
    private readonly Func<object>? _getCompanion;

    /// <summary>Descrive le impostazioni dei componenti (stesse pagine del player).</summary>
    public Func<object>? SettingsProvider { get; set; }
    /// <summary>Scrive un'impostazione: (componente, chiave, valore) -> esito.</summary>
    public Func<string, string, string, object>? SettingsWriter { get; set; }
    private readonly Action<string, Dictionary<string, string>> _handle;

    private TcpListener? _tcp;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private volatile bool _running;

    // ====================== FrontDoor :80 proxy -> _port ======================
    private TcpListener? _tcpFront;
    private Task? _frontLoop;
    private volatile bool _frontRunning;

    // ====================== hostname mDNS univoco ======================
    private MdnsResponder? _mdns;

    // ====================== trusted store ======================
    private readonly string _rootDir;
    private readonly string _storePath;
    private readonly object _lock = new();
    private readonly object _remotePresenceLock = new();
    private string _lastRemoteConnectedLabel = string.Empty;
    private DateTime _lastRemoteConnectedUtc = DateTime.MinValue;

    public event Action<string>? Paired;
    public event Action<string>? PairingRequested;
    public event Action<string>? RemoteConnected;

    public int TrustedCount { get { lock (_lock) return _trusted.Count; } }
    public string CurrentPin => _pin;
    public int CurrentPort => _port;
    public bool FrontDoorRunning => _frontRunning;
    public string PrimaryUrl => UrlForHost(_mdnsHostFqdn);

    public string UrlForHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            host = "localhost";

        string normalizedHost = host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
            ? "[" + host + "]"
            : host;

        return (_frontRunning || _port == 80)
            ? $"http://{normalizedHost}"
            : $"http://{normalizedHost}:{_port}";
    }

    private sealed class TrustedToken
    {
        public string Token { get; set; } = "";
        public string? Name { get; set; }
        public string? LastIp { get; set; }
        public string? Mac { get; set; } // best-effort (vedi note: se passi da proxy 80 spesso sarà null)
        public string? DeviceId { get; set; } // persistenza robusta lato browser/client
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    private readonly List<TrustedToken> _trusted = new();

    // ====================== HTTP req/resp struct ======================
    private sealed class SimpleRequest
    {
        public string Method = "";
        public string Path = "";
        public string Query = "";
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Cookies = new(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
        public string RemoteIp = "";
    }

    private sealed class SimpleResponse
    {
        public int StatusCode = 200;
        public string ContentType = "text/plain; charset=utf-8";
        public string BodyText = "";
        public byte[]? BodyBytes;
        public List<(string Key, string Val)> ExtraHeaders = new();
    }

    // ====================== ctor ======================
    public RemoteServer(int port,
                        string? pin,
                        Func<RemoteState> getState,
                        Action<string, Dictionary<string, string>> handleCommand,
                        Func<object>? getCompanion = null)
    {
        _port = port;
        _pin = string.IsNullOrWhiteSpace(pin) ? MakePin() : pin.Trim();
        _getState = getState;
        _getCompanion = getCompanion;
        _handle = handleCommand;

        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CinecorePlayer2025");
        Directory.CreateDirectory(_rootDir);

        // A shared cinecore-remote.local name makes phones resolve one of two
        // players unpredictably when multiple PCs are on the same LAN. Keep one
        // per-device ID under LocalApplicationData so roaming profiles do not
        // copy the same mDNS hostname to another PC.
        _mdnsHostFqdn = CreateMdnsHostFqdn();

        _storePath = Path.Combine(_rootDir, "trusted.json");
        LoadTrusted();
    }

    private static string CreateMdnsHostFqdn()
    {
        string machineName = Environment.MachineName;
        string machineIdentity = machineName + "|" + Environment.UserName;
        try
        {
            string? machineGuid = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
            if (!string.IsNullOrWhiteSpace(machineGuid))
                machineIdentity = machineGuid;
        }
        catch { }

        string machine = new string(machineName.ToLowerInvariant()
            .Select(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' ? character : '-')
            .ToArray()).Trim('-');
        while (machine.Contains("--", StringComparison.Ordinal))
            machine = machine.Replace("--", "-", StringComparison.Ordinal);
        if (machine.Length == 0) machine = "player";
        if (machine.Length > 24) machine = machine[..24].TrimEnd('-');

        byte[] identityHash = SHA256.HashData(Encoding.UTF8.GetBytes(machineIdentity));
        string stableSuffix = Convert.ToHexString(identityHash).ToLowerInvariant()[..10];
        return $"cinecore-{machine}-{stableSuffix}.local";
    }

    private static string MakePin()
    {
        using var rng = RandomNumberGenerator.Create();
        Span<byte> b = stackalloc byte[4];
        rng.GetBytes(b);
        var n = BitConverter.ToUInt32(b) % 1_000_000u;
        return n.ToString("000000");
    }

    private void RaisePairingRequested()
    {
        try { PairingRequested?.Invoke(_pin); } catch { }
    }

    private void RaiseRemoteConnected(TrustedToken tok, string source)
    {
        string? label = tok.Name;
        if (string.IsNullOrWhiteSpace(label))
            label = tok.LastIp;
        if (string.IsNullOrWhiteSpace(label))
            label = "remote";
        string connectedLabel = label ?? "remote";

        bool notify;
        lock (_remotePresenceLock)
        {
            DateTime now = DateTime.UtcNow;
            notify = !string.Equals(_lastRemoteConnectedLabel, connectedLabel, StringComparison.OrdinalIgnoreCase) ||
                     now - _lastRemoteConnectedUtc > TimeSpan.FromMinutes(5);
            if (notify)
            {
                _lastRemoteConnectedLabel = connectedLabel;
                _lastRemoteConnectedUtc = now;
            }
        }
        if (notify)
            try { RemoteConnected?.Invoke(connectedLabel); } catch { }
        try { Dbg.Log($"Remote server: client connected via {source} ({connectedLabel}).", Dbg.LogLevel.Verbose); } catch { }
    }

    // ====================== start/stop ======================
    public void Start()
    {
        if (_running) return;

        int preferredPort = _port;
        List<string> bindErrors = new();

        foreach (int candidatePort in RemotePortCandidates(preferredPort))
        {
            _tcp = new TcpListener(IPAddress.Any, candidatePort);
            try
            {
                _tcp.Start();
                _port = candidatePort;
                break;
            }
            catch (SocketException ex)
            {
                bindErrors.Add($"{candidatePort}: {ex.SocketErrorCode} - {ex.Message}");
                try { _tcp.Stop(); } catch { }
                _tcp = null;
            }
        }

        if (_tcp == null)
        {
            throw new InvalidOperationException(
                global::CinecorePlayer2025.Utilities.AppLanguage.T("Impossibile avviare il server remoto. Porte provate: ", "Could not start the remote server. Ports tried: ") +
                string.Join("; ", bindErrors));
        }

        _cts = new CancellationTokenSource();
        _running = true;
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));

        if (_port != preferredPort)
            Dbg.Warn($"Remote server: porta {preferredPort} non disponibile, uso {_port}.");
        else
            Dbg.Log($"Remote server: in ascolto su porta {_port}.");

        // Best-effort: front door su :80 (reverse proxy) per URL pulito.
        StartFrontDoor80();

        // Best-effort: hostname mDNS univoco per questo PC.
        StartMdns();
    }

    private static IEnumerable<int> RemotePortCandidates(int preferredPort)
    {
        var seen = new HashSet<int>();

        bool Valid(int port) => port > 0 && port <= 65535 && seen.Add(port);

        if (Valid(preferredPort))
            yield return preferredPort;

        foreach (int port in new[] { 9384, 18080, 28080, 38080, 49152 })
        {
            if (Valid(port))
                yield return port;
        }
    }

    public void Stop()
    {
        _running = false;

        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Stop(); } catch { }

        StopFrontDoor80();
        StopMdns();
    }

    public void Dispose() => Stop();

    // ====================== loop accettazione server core ======================
    private async Task AcceptLoop(CancellationToken token)
    {
        if (_tcp == null) return;

        while (_running && !token.IsCancellationRequested)
        {
            TcpClient? cli = null;
            try { cli = await _tcp.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch
            {
                if (!_running) break;
                continue;
            }

            if (cli != null)
                _ = Task.Run(() => HandleClient(cli));
        }
    }

    // ====================== gestione client core ======================
    private async Task HandleClient(TcpClient cli)
    {
        using (cli)
        using (var ns = cli.GetStream())
        using (var writer = new StreamWriter(ns, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true })
        {
            SimpleRequest? req = await ReadRequest(ns, cli);
            if (req == null) return;

            SimpleResponse resp;
            try
            {
                resp = ProcessRequest(req);
            }
            catch (Exception ex)
            {
                resp = JsonResp(new
                {
                    ok = false,
                    error = ex.GetType().Name,
                    message = ex.Message
                }, 500);
            }

            await WriteResponse(writer, resp);
        }
    }

    // ====================== parsing HTTP (robusto, Content-Length in bytes) ======================
    private static async Task<SimpleRequest?> ReadRequest(NetworkStream ns, TcpClient cli)
    {
        // Leggo fino a \r\n\r\n
        const int MaxHeader = 64 * 1024;
        // Il telecomando invia solo piccoli JSON: un corpo dichiarato enorme o una connessione
        // lasciata aperta a meta' non devono poter occupare memoria e thread del player.
        const int MaxBody = 256 * 1024;
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        byte[] tmp = new byte[4096];
        var buf = new List<byte>(8192);

        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n;
            try { n = await ns.ReadAsync(tmp.AsMemory(0, tmp.Length), readTimeout.Token); }
            catch { return null; }

            if (n <= 0) return null;

            buf.AddRange(tmp.Take(n));
            if (buf.Count > MaxHeader) return null;

            headerEnd = IndexOfSequence(buf, new byte[] { 13, 10, 13, 10 }); // \r\n\r\n
        }

        int headerLen = headerEnd + 4;
        byte[] headerBytes = buf.Take(headerLen).ToArray();
        byte[] remaining = buf.Skip(headerLen).ToArray();

        string headerText = Encoding.ASCII.GetString(headerBytes);
        string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return null;

        string startLine = lines[0];
        if (string.IsNullOrWhiteSpace(startLine)) return null;

        string[] parts = startLine.Split(' ');
        if (parts.Length < 2) return null;

        string method = parts[0].Trim().ToUpperInvariant();
        string urlPart = parts[1].Trim();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break;

            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                string name = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                if (headers.TryGetValue(name, out var prev))
                    headers[name] = prev + ", " + value;
                else
                    headers[name] = value;
            }
        }

        int contentLen = 0;
        if (headers.TryGetValue("Content-Length", out var clStr))
            int.TryParse(clStr, out contentLen);
        if (contentLen > MaxBody) return null;

        byte[] bodyBytes = Array.Empty<byte>();
        if (contentLen > 0)
        {
            bodyBytes = new byte[contentLen];
            int copied = 0;

            // Copio i byte già letti dopo header
            int take = Math.Min(contentLen, remaining.Length);
            if (take > 0)
            {
                Buffer.BlockCopy(remaining, 0, bodyBytes, 0, take);
                copied = take;
            }

            // Leggo il resto dal network stream
            while (copied < contentLen)
            {
                int need = contentLen - copied;
                int n;
                try { n = await ns.ReadAsync(bodyBytes.AsMemory(copied, need), readTimeout.Token); }
                catch { return null; }
                if (n <= 0) break;
                copied += n;
            }

            if (copied < contentLen)
            {
                // body incompleto
                Array.Resize(ref bodyBytes, copied);
            }
        }

        string body = bodyBytes.Length > 0 ? Encoding.UTF8.GetString(bodyBytes) : "";

        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers.TryGetValue("Cookie", out var cookieHeader))
        {
            var cookieParts = cookieHeader.Split(';');
            foreach (var cpart in cookieParts)
            {
                var cp = cpart.Trim();
                if (cp.Length == 0) continue;

                int eq = cp.IndexOf('=');
                if (eq >= 0)
                {
                    var cname = cp[..eq].Trim();
                    var cval = cp[(eq + 1)..].Trim();
                    if (!string.IsNullOrEmpty(cname))
                        cookies[cname] = cval;
                }
                else
                {
                    cookies[cp] = "";
                }
            }
        }

        string path;
        string query = "";
        int qm = urlPart.IndexOf('?');
        if (qm >= 0)
        {
            path = urlPart[..qm];
            query = urlPart[(qm + 1)..];
        }
        else
        {
            path = urlPart;
        }

        IPAddress? peer = ((IPEndPoint?)cli.Client.RemoteEndPoint)?.Address;
        string remoteIp = peer?.ToString() ?? "?";
        // X-Forwarded-For vale solo se arriva dal proxy locale (front door sulla porta 80).
        // Accettarlo da chiunque permetteva di dichiarare l'IP di un dispositivo gia' abbinato
        // e farsi riconoscere al suo posto (il riconoscimento per MAC parte dall'IP).
        if (peer != null && IPAddress.IsLoopback(peer) &&
            headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrWhiteSpace(xff))
        {
            var first = xff.Split(',').Select(s => s.Trim()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (!string.IsNullOrWhiteSpace(first))
                remoteIp = first;
        }

        return new SimpleRequest
        {
            Method = method,
            Path = path,
            Query = query,
            Headers = headers,
            Cookies = cookies,
            Body = body,
            RemoteIp = remoteIp
        };
    }

    private static int IndexOfSequence(List<byte> haystack, byte[] needle)
    {
        if (needle.Length == 0) return -1;
        for (int i = 0; i <= haystack.Count - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    // ====================== scrittura risposta ======================
    private static async Task WriteResponse(StreamWriter w, SimpleResponse resp)
    {
        string reason = ReasonPhrase(resp.StatusCode);
        byte[] bodyBytes = resp.BodyBytes ?? Encoding.UTF8.GetBytes(resp.BodyText ?? "");

        await w.WriteLineAsync($"HTTP/1.1 {resp.StatusCode} {reason}");
        // Nessuna intestazione CORS: la pagina del telecomando chiama solo la propria origine.
        // Con "Access-Control-Allow-Origin: *" qualunque sito aperto su un dispositivo della
        // rete poteva leggere le risposte del player.
        await w.WriteLineAsync("X-Content-Type-Options: nosniff");
        await w.WriteLineAsync("Connection: close");

        // NO CACHE (evita riapertura con pagina PIN cached)
        await w.WriteLineAsync("Cache-Control: no-store, no-cache, must-revalidate, max-age=0");
        await w.WriteLineAsync("Pragma: no-cache");
        await w.WriteLineAsync("Expires: 0");

        await w.WriteLineAsync($"Content-Type: {resp.ContentType}");
        await w.WriteLineAsync($"Content-Length: {bodyBytes.Length}");

        foreach (var (k, v) in resp.ExtraHeaders)
            await w.WriteLineAsync($"{k}: {v}");

        await w.WriteLineAsync();
        await w.FlushAsync();

        await w.BaseStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
        await w.BaseStream.FlushAsync();
    }

    private static string ReasonPhrase(int code) => code switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "OK"
    };

    // ====================== helpers risposta ======================
    private static SimpleResponse HtmlResp(string html) => new SimpleResponse
    {
        StatusCode = 200,
        ContentType = "text/html; charset=utf-8",
        BodyText = html
    };

    private static SimpleResponse JsonResp(object obj, int statusCode = 200) => new SimpleResponse
    {
        StatusCode = statusCode,
        ContentType = "application/json; charset=utf-8",
        BodyText = JsonSerializer.Serialize(obj)
    };

    private static void AddAuthCookie(SimpleResponse resp, string token)
    {
        // Nota: niente Secure perché HTTP. SameSite=Lax per compatibilità.
        resp.ExtraHeaders.Add((
            "Set-Cookie",
            $"ccp_token={token}; Path=/; HttpOnly; Max-Age=31536000; SameSite=Lax"
        ));
    }

    private static void ExpireAuthCookie(SimpleResponse resp)
    {
        resp.ExtraHeaders.Add((
            "Set-Cookie",
            "ccp_token=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT; SameSite=Lax"
        ));
    }

    private static void AddDeviceCookie(SimpleResponse resp, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        resp.ExtraHeaders.Add((
            "Set-Cookie",
            $"ccp_device={Uri.EscapeDataString(deviceId)}; Path=/; Max-Age=31536000; SameSite=Lax"
        ));
    }

    private static void ExpireDeviceCookie(SimpleResponse resp)
    {
        resp.ExtraHeaders.Add((
            "Set-Cookie",
            "ccp_device=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT; SameSite=Lax"
        ));
    }

    private static string? ReadJsonPropFromBody(string body, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty(prop, out var v))
                return v.GetString();
        }
        catch { }
        return null;
    }

    private static string? ReadDeviceId(SimpleRequest req)
    {
        string? deviceId = null;

        if (req.Headers.TryGetValue("X-Device-Id", out var didHeader) &&
            !string.IsNullOrWhiteSpace(didHeader))
        {
            deviceId = didHeader.Trim();
        }

        if (string.IsNullOrWhiteSpace(deviceId) &&
            req.Cookies.TryGetValue("ccp_device", out var didCookie) &&
            !string.IsNullOrWhiteSpace(didCookie))
        {
            try { deviceId = Uri.UnescapeDataString(didCookie.Trim()); }
            catch { deviceId = didCookie.Trim(); }
        }

        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        return deviceId.Length > 128 ? deviceId[..128] : deviceId;
    }

    // ====================== trusted management ======================
    private void LoadTrusted()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var json = File.ReadAllText(_storePath);
            // I token dei dispositivi abbinati stanno cifrati sul disco. Un file ancora in
            // chiaro (versioni precedenti) viene letto e riscritto subito cifrato.
            bool legacyPlain = !CinecorePlayer2025.Utilities.SecretVault.IsProtected(json.Trim());
            if (!legacyPlain)
                json = CinecorePlayer2025.Utilities.SecretVault.Unprotect(json.Trim()) ?? "[]";
            using var migrate = new TrustedMigration(legacyPlain ? SaveTrusted : null);

            var list = JsonSerializer.Deserialize<List<TrustedToken>>(json);
            if (list != null)
            {
                _trusted.Clear();
                _trusted.AddRange(list);
                return;
            }

            var arr = JsonSerializer.Deserialize<string[]>(json);
            if (arr != null)
            {
                _trusted.Clear();
                foreach (var t in arr)
                {
                    _trusted.Add(new TrustedToken
                    {
                        Token = t,
                        FirstSeen = DateTime.UtcNow,
                        LastSeen = DateTime.UtcNow
                    });
                }
            }
        }
        catch { }
    }

    private void SaveTrusted()
    {
        try
        {
            var json = JsonSerializer.Serialize(_trusted,
                new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(_rootDir);
            File.WriteAllText(_storePath, CinecorePlayer2025.Utilities.SecretVault.Protect(json));
        }
        catch { }
    }

    // Riscrive il file cifrato una volta finito il caricamento di un file in chiaro.
    private sealed class TrustedMigration : IDisposable
    {
        private readonly Action? _save;
        public TrustedMigration(Action? save) => _save = save;
        public void Dispose() => _save?.Invoke();
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int phyAddrLen);

    private static string? TryGetMac(string ip)
    {
        try
        {
            var addr = IPAddress.Parse(ip);
            if (addr.IsIPv4MappedToIPv6)
                addr = addr.MapToIPv4();

            var bytes = addr.GetAddressBytes();
            if (bytes.Length != 4) return null;

            int dest = BitConverter.ToInt32(bytes, 0);
            var mac = new byte[6];
            int len = mac.Length;
            if (SendARP(dest, 0, mac, ref len) == 0 && len == 6)
                return string.Join(":", mac.Select(b => b.ToString("X2")));
        }
        catch { }
        return null;
    }

    private TrustedToken CreateTrustedToken(string? devName, string remoteIp, string? deviceId)
    {
        string? mac = TryGetMac(remoteIp); // best-effort
        string newTokVal = NewToken();

        lock (_lock)
        {
            TrustedToken? existing = null;

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                existing = _trusted.Find(t =>
                    string.Equals(t.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            }

            if (existing == null && !string.IsNullOrWhiteSpace(mac))
            {
                existing = _trusted.Find(t =>
                    string.Equals(t.Mac, mac, StringComparison.OrdinalIgnoreCase));
            }

            if (existing == null)
            {
                var t = new TrustedToken
                {
                    Token = newTokVal,
                    Name = string.IsNullOrWhiteSpace(devName) ? null : devName,
                    FirstSeen = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                    LastIp = remoteIp,
                    Mac = mac,
                    DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId
                };
                _trusted.Add(t);
                SaveTrusted();
                try { Paired?.Invoke(t.Name ?? t.Token); } catch { }
                return t;
            }
            else
            {
                existing.Token = newTokVal;
                if (!string.IsNullOrWhiteSpace(devName))
                    existing.Name = devName;
                existing.LastSeen = DateTime.UtcNow;
                existing.LastIp = remoteIp;
                if (!string.IsNullOrWhiteSpace(mac))
                    existing.Mac = mac;
                if (!string.IsNullOrWhiteSpace(deviceId))
                    existing.DeviceId = deviceId;
                SaveTrusted();
                try { Paired?.Invoke(existing.Name ?? existing.Token); } catch { }
                return existing;
            }
        }
    }

    private DateTime _lastTrustedRefreshSave;
    private void TouchTrusted(TrustedToken trusted, SimpleRequest request, string? deviceId)
    {
        var now = DateTime.UtcNow;
        bool changed = trusted.LastIp != request.RemoteIp ||
            (!string.IsNullOrWhiteSpace(deviceId) && trusted.DeviceId != deviceId);
        trusted.LastSeen = now;
        trusted.LastIp = request.RemoteIp;
        if (!string.IsNullOrWhiteSpace(deviceId)) trusted.DeviceId = deviceId;
        // An authenticated state poll must not wait for ARP or write JSON to disk.
        // MAC discovery belongs to pairing; LastSeen is retained in memory here.
        if (changed || (now - _lastTrustedRefreshSave).TotalSeconds >= 60)
        {
            _lastTrustedRefreshSave = now;
            SaveTrusted();
        }
    }

    private bool IsAuthed(SimpleRequest req, out TrustedToken? tokObj)
    {
        tokObj = null;
        string? token = null;
        string? deviceId = ReadDeviceId(req);

        if (req.Cookies.TryGetValue("ccp_token", out var cookieTok)
            && !string.IsNullOrWhiteSpace(cookieTok))
        {
            token = cookieTok;
        }

        if (token == null &&
            req.Headers.TryGetValue("Authorization", out var auth) &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = auth.Substring(7).Trim();
        }

        lock (_lock)
        {
            if (!string.IsNullOrEmpty(token))
            {
                tokObj = _trusted.Find(t => t.Token == token);
                if (tokObj != null)
                {
                    TouchTrusted(tokObj, req, deviceId);
                    return true;
                }
            }

            // Fallback robusto: se il browser mantiene il device id ma perde token/cookie,
            // continuiamo a considerarlo trusted senza richiedere di nuovo il PIN.
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                tokObj = _trusted.Find(t =>
                    string.Equals(t.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (tokObj != null)
                {
                    TouchTrusted(tokObj, req, deviceId);
                    return true;
                }
            }

            // Fallback finale: riconosci il device dal MAC sul segmento LAN.
            // Serve soprattutto quando il browser perde token/storage ma il device è già trusted.
            string? reqMac = TryGetMac(req.RemoteIp);
            if (!string.IsNullOrWhiteSpace(reqMac))
            {
                tokObj = _trusted.Find(t =>
                    string.Equals(t.Mac, reqMac, StringComparison.OrdinalIgnoreCase));
                if (tokObj != null)
                {
                    tokObj.LastSeen = DateTime.UtcNow;
                    tokObj.LastIp = req.RemoteIp;
                    tokObj.Mac = reqMac;
                    if (!string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(tokObj.DeviceId))
                        tokObj.DeviceId = deviceId;

                    SaveTrusted();
                    return true;
                }
            }
        }

        return false;
    }

    // ====================== querystring parser ======================
    private static Dictionary<string, string> ParseQuery(string? q)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(q)) return dict;
        if (q.StartsWith("?")) q = q[1..];

        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            var val = kv.Length > 1
                ? Uri.UnescapeDataString(kv[1].Replace('+', ' '))
                : "";
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    // ====================== tentativi PIN ======================
    // Il PIN ha sei cifre: senza un limite si prova tutto in pochi minuti dalla rete locale.
    private const int PinAttemptsBeforeLock = 5;
    private static readonly TimeSpan PinLock = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, (int Failures, DateTime LockedUntilUtc)> _pinAttempts = new(StringComparer.Ordinal);

    /// <summary>true = PIN corretto, false = sbagliato, null = troppi tentativi (in attesa).</summary>
    private bool? TryPin(string remoteIp, string? pin)
    {
        lock (_pinAttempts)
        {
            DateTime now = DateTime.UtcNow;
            _pinAttempts.TryGetValue(remoteIp, out var state);
            if (state.LockedUntilUtc > now) return null;
            if (!string.IsNullOrEmpty(pin) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes(_pin)))
            {
                _pinAttempts.Remove(remoteIp);
                return true;
            }
            int failures = state.Failures + 1;
            _pinAttempts[remoteIp] = failures >= PinAttemptsBeforeLock ? (0, now + PinLock) : (failures, default);
            if (_pinAttempts.Count > 512) _pinAttempts.Clear();
            return false;
        }
    }

    /// <summary>Le pagine del telecomando inviano sempre X-Device-Id (e il token, se c'e'):
    /// intestazioni che un altro sito non puo' aggiungere a una richiesta verso il player.
    /// Senza, un sito aperto su un telefono gia' abbinato poteva inviare comandi al suo posto.</summary>
    private static bool HasRemotePageHeader(SimpleRequest req) =>
        (req.Headers.TryGetValue("X-Device-Id", out var id) && !string.IsNullOrWhiteSpace(id)) ||
        (req.Headers.TryGetValue("Authorization", out var auth) && !string.IsNullOrWhiteSpace(auth));

    // ====================== routing ======================
    private SimpleResponse ProcessRequest(SimpleRequest req)
    {
        if (req.Method == "OPTIONS")
        {
            return new SimpleResponse
            {
                StatusCode = 200,
                ContentType = "text/plain; charset=utf-8",
                BodyText = ""
            };
        }

        if (req.Path == "/health")
            return JsonResp(new { ok = true, online = true }, 200);

        // Icona e manifesto: salvando il telecomando sulla schermata Home del telefono compare l'icona di Cinecore.
        if (req.Method == "GET" && (req.Path == "/icon.png" || req.Path == "/apple-touch-icon.png" || req.Path == "/apple-touch-icon-precomposed.png" || req.Path == "/favicon.ico"))
        {
            byte[]? icon = RemoteIconPng();
            return icon == null
                ? JsonResp(new { ok = false }, 404)
                : new SimpleResponse { StatusCode = 200, ContentType = "image/png", BodyBytes = icon };
        }
        if (req.Method == "GET" && req.Path == "/manifest.webmanifest")
            return new SimpleResponse
            {
                StatusCode = 200,
                ContentType = "application/manifest+json; charset=utf-8",
                BodyText = "{\"name\":\"Cinecore Remote\",\"short_name\":\"Cinecore\",\"start_url\":\"/remote\",\"display\":\"standalone\",\"background_color\":\"#05080b\",\"theme_color\":\"#0a0e13\",\"icons\":[{\"src\":\"/icon.png\",\"sizes\":\"512x512\",\"type\":\"image/png\",\"purpose\":\"any\"}]}"
            };

        // UI sempre servita (le API restano protette)
        if (req.Path == "/remote" && req.Method == "GET")
        {
            if (IsAuthed(req, out var tokRemote) && tokRemote != null)
            {
                RaiseRemoteConnected(tokRemote, "/remote");
                var respRemote = HtmlResp(RemoteHtmlV2());
                AddAuthCookie(respRemote, tokRemote.Token);
                AddDeviceCookie(respRemote, tokRemote.DeviceId);
                return respRemote;
            }

            return HtmlResp(RemoteHtmlV2());
        }

        if (req.Path == "/" || req.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            var qdict = ParseQuery(string.IsNullOrEmpty(req.Query) ? null : "?" + req.Query);

            // pairing via ?pin=
            if (qdict.TryGetValue("pin", out var pinQ) && TryPin(req.RemoteIp, pinQ) == true)
            {
                req.Headers.TryGetValue("X-Device-Name", out var devName);
                if (!req.Headers.TryGetValue("User-Agent", out var ua)) ua = null;

                var deviceId = ReadDeviceId(req);
                var newTok = CreateTrustedToken(devName ?? ua, req.RemoteIp, deviceId);
                RaiseRemoteConnected(newTok, "/?pin");
                var respOk = HtmlResp(RemoteHtmlV2());
                AddAuthCookie(respOk, newTok.Token);
                AddDeviceCookie(respOk, newTok.DeviceId ?? deviceId);
                return respOk;
            }

            // già autenticato -> telecomando
            if (IsAuthed(req, out var tokRoot) && tokRoot != null)
            {
                RaiseRemoteConnected(tokRoot, "/");
                var respRoot = HtmlResp(RemoteHtmlV2());
                AddAuthCookie(respRoot, tokRoot.Token);
                AddDeviceCookie(respRoot, tokRoot.DeviceId);
                return respRoot;
            }

            // NON autenticato -> pagina PIN e alzo PairingRequested
            RaisePairingRequested();
            return HtmlResp(PinHtml());
        }

        // POST /api/auth
        if (req.Path == "/api/auth" && req.Method == "POST")
        {
            var pinBody = ReadJsonPropFromBody(req.Body, "pin");
            bool? pinOk = TryPin(req.RemoteIp, pinBody);
            if (pinOk == null)
                return JsonResp(new { ok = false, error = "too many attempts", pair = true }, 429);
            if (pinOk == true)
            {
                var nameBody = ReadJsonPropFromBody(req.Body, "name");
                var deviceIdBody = ReadJsonPropFromBody(req.Body, "deviceId");
                var deviceId = string.IsNullOrWhiteSpace(deviceIdBody) ? ReadDeviceId(req) : deviceIdBody;
                if (!req.Headers.TryGetValue("User-Agent", out var ua)) ua = null;

                var newTok = CreateTrustedToken(
                    string.IsNullOrWhiteSpace(nameBody) ? ua : nameBody,
                    req.RemoteIp,
                    deviceId);

                RaiseRemoteConnected(newTok, "/api/auth");
                var okResp = JsonResp(new { ok = true, token = newTok.Token }, 200);
                AddAuthCookie(okResp, newTok.Token);
                AddDeviceCookie(okResp, newTok.DeviceId ?? deviceId);
                return okResp;
            }

            RaisePairingRequested();
            return JsonResp(new { ok = false, error = "bad pin", pair = true }, 401);
        }

        // POST /api/logout
        if (req.Path == "/api/logout" && req.Method == "POST")
        {
            if (IsAuthed(req, out var tok) && tok != null)
            {
                lock (_lock)
                {
                    _trusted.RemoveAll(x => x.Token == tok.Token);
                    SaveTrusted();
                }
            }

            var outResp = JsonResp(new { ok = true }, 200);
            ExpireAuthCookie(outResp);
            ExpireDeviceCookie(outResp);
            return outResp;
        }

        // GET /api/trusted
        if (req.Path == "/api/trusted" && req.Method == "GET")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true }, 401);
            }

            List<object> view;
            lock (_lock)
            {
                view = new List<object>(_trusted.Count);
                foreach (var t in _trusted)
                {
                    string shortTok = t.Token.Length > 6 ? t.Token[..6] + "…" : t.Token;
                    view.Add(new
                    {
                        token = shortTok,
                        name = t.Name,
                        lastIp = t.LastIp,
                        mac = t.Mac,
                        firstSeen = t.FirstSeen,
                        lastSeen = t.LastSeen
                    });
                }
            }
            return JsonResp(new { ok = true, devices = view }, 200);
        }

        // POST /api/trusted/rename
        if (req.Path == "/api/trusted/rename" && req.Method == "POST")
        {
            if (!IsAuthed(req, out var tok) || tok == null)
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true }, 401);
            }

            var newName = ReadJsonPropFromBody(req.Body, "name");
            lock (_lock)
            {
                tok.Name = string.IsNullOrWhiteSpace(newName) ? null : newName;
                SaveTrusted();
            }

            return JsonResp(new { ok = true }, 200);
        }

        // DELETE /api/trusted
        if (req.Path == "/api/trusted" && req.Method == "DELETE")
        {
            if (!IsAuthed(req, out var tok) || tok == null)
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true }, 401);
            }

            lock (_lock)
            {
                _trusted.RemoveAll(x => x.Token == tok.Token);
                SaveTrusted();
            }

            var delResp = JsonResp(new { ok = true }, 200);
            ExpireAuthCookie(delResp);
            ExpireDeviceCookie(delResp);
            return delResp;
        }

        // GET /api/state
        if (req.Path == "/api/state")
        {
            if (!IsAuthed(req, out var tokState) || tokState == null)
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true }, 401);
            }

            RaiseRemoteConnected(tokState, "/api/state");
            var st = _getState();
            return JsonResp(st, 200);
        }

        // Dati leggeri per le pagine Coda e Libreria del telecomando.
        if (req.Path == "/api/companion")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true }, 401);
            }

            try { return JsonResp(_getCompanion?.Invoke() ?? new { queue = Array.Empty<object>(), library = Array.Empty<object>() }, 200); }
            catch { return JsonResp(new { queue = Array.Empty<object>(), library = Array.Empty<object>() }, 200); }
        }

        // Artwork locale per la mini-libreria del telecomando. L'endpoint è
        // autenticato e accetta esclusivamente file immagine già presenti sul PC.
        if (req.Path == "/api/art")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true }, 401);
            }

            var q = ParseQuery(string.IsNullOrEmpty(req.Query) ? null : "?" + req.Query);
            string path = q.TryGetValue("path", out string? artPath) ? artPath : string.Empty;
            try
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                string contentType = extension switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    ".jpeg" or ".jpg" => "image/jpeg",
                    _ => string.Empty
                };
                if (!string.IsNullOrWhiteSpace(contentType) && File.Exists(path))
                {
                    return new SimpleResponse
                    {
                        StatusCode = 200,
                        ContentType = contentType,
                        BodyBytes = File.ReadAllBytes(path)
                    };
                }
            }
            catch { }
            return JsonResp(new { ok = false, error = "art not found" }, 404);
        }

        // Impostazioni dei componenti (madVR, LAV, MPC, sottotitoli): le stesse del player.
        if (req.Path == "/api/settings" || req.Path == "/api/settings/set")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true }, 401);
            }
            if (req.Path == "/api/settings/set" && !HasRemotePageHeader(req))
                return JsonResp(new { ok = false, error = "forbidden" }, 403);
            try
            {
                if (req.Path == "/api/settings")
                    return JsonResp(SettingsProvider?.Invoke() ?? new { providers = Array.Empty<object>() }, 200);
                var q = ParseQuery(string.IsNullOrEmpty(req.Query) ? null : "?" + req.Query);
                string provider = q.TryGetValue("provider", out var pv) ? pv : "";
                string key = q.TryGetValue("key", out var kv) ? kv : "";
                string value = q.TryGetValue("value", out var vv) ? vv : "";
                if (SettingsWriter == null || provider.Length == 0 || key.Length == 0)
                    return JsonResp(new { ok = false, error = "bad request" }, 400);
                return JsonResp(SettingsWriter(provider, key, value), 200);
            }
            catch (Exception ex)
            {
                return JsonResp(new { ok = false, error = ex.Message }, 500);
            }
        }

        // GET /api/cmd
        if (req.Path == "/api/cmd")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true }, 401);
            }

            if (!HasRemotePageHeader(req))
                return JsonResp(new { ok = false, error = "forbidden" }, 403);

            var q = ParseQuery(string.IsNullOrEmpty(req.Query) ? null : "?" + req.Query);
            string cmd = q.TryGetValue("cmd", out var c) ? c : "";
            _handle(cmd, q);

            return JsonResp(new { ok = true }, 200);
        }

        return JsonResp(new { ok = false, error = "not found" }, 404);
    }

    private static byte[]? _remoteIconPng;

    /// <summary>L'icona del programma su fondo scuro, 512 px, come PNG (creata una volta).</summary>
    private static byte[]? RemoteIconPng()
    {
        if (_remoteIconPng != null) return _remoteIconPng;
        try
        {
            const int side = 512;
            using var canvas = new System.Drawing.Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(canvas))
            {
                g.Clear(System.Drawing.Color.FromArgb(5, 8, 11));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "cinecore.ico");
                using System.Drawing.Icon? icon = File.Exists(ico)
                    ? new System.Drawing.Icon(ico, 256, 256)
                    : System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
                if (icon == null) return null;
                using var picture = icon.ToBitmap();
                int inner = (int)(side * 0.68);
                g.DrawImage(picture, (side - inner) / 2, (side - inner) / 2, inner, inner);
            }
            using var stream = new MemoryStream();
            canvas.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return _remoteIconPng = stream.ToArray();
        }
        catch { return null; }
    }

    // ====================== pagina PIN ======================
    private static string PinHtml() => @"<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no'>
<meta name='theme-color' content='#05090f'>
<link rel='apple-touch-icon' href='/apple-touch-icon.png'>
<link rel='icon' type='image/png' href='/icon.png'>
<link rel='manifest' href='/manifest.webmanifest'>
<title>Cinecore Remote</title>
<style>
:root{--bg:#05090f;--card:#0d141d;--txt:#f2f5f8;--dim:#8a97a6;--line:#1d2733;--accent:#23a4ff;--danger:#ff5d73;--font:system-ui,'Segoe UI',Roboto,Arial,sans-serif}
*{box-sizing:border-box;margin:0;padding:0;-webkit-tap-highlight-color:transparent}
html,body{height:100%}
body{background:var(--bg);color:var(--txt);font-family:var(--font);display:flex;justify-content:center;padding:max(28px,env(safe-area-inset-top)) 20px 28px}
.wrap{width:min(380px,100%);display:flex;flex-direction:column;gap:22px}
.brand{font-size:12px;font-weight:700;letter-spacing:.14em;color:var(--dim);text-transform:uppercase}
h1{font-size:26px;font-weight:650;line-height:1.15;margin-top:6px}
.lead{font-size:15px;color:var(--dim);line-height:1.45;margin-top:10px}
.cells{display:grid;grid-template-columns:repeat(6,1fr);gap:8px;position:relative}
.cell{height:56px;border-radius:12px;background:var(--card);border:1px solid var(--line);display:flex;align-items:center;justify-content:center;font-size:26px;font-weight:650;font-variant-numeric:tabular-nums}
.cell.on{border-color:var(--accent)}
.cells.bad .cell{border-color:var(--danger);animation:shake .28s}
@keyframes shake{25%{transform:translateX(-4px)}75%{transform:translateX(4px)}}
#pin{position:absolute;inset:0;width:100%;height:100%;opacity:0;font-size:16px;border:0;background:transparent;color:transparent;caret-color:transparent}
.keypad{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}
.keypad button{appearance:none;background:var(--card);border:1px solid var(--line);border-radius:14px;color:var(--txt);font-size:22px;font-weight:600;height:58px;font-family:inherit}
.keypad button:active{background:#152030}
.keypad .ghost{background:transparent;border-color:transparent;color:var(--dim);font-size:17px}
.msg{min-height:20px;font-size:14px;text-align:center;color:var(--dim)}
.msg.bad{color:var(--danger)}
</style>
</head>
<body>
<div class='wrap'>
  <div>
    <div class='brand'>Cinecore Player</div>
    <h1 data-it='Abbina questo telefono' data-en='Pair this phone'></h1>
    <div class='lead' data-it='Inquadra il codice QR mostrato dal player: si apre il telecomando già abbinato. Oppure scrivi qui il PIN che vedi sotto al codice.' data-en='Scan the QR code shown by the player: the remote opens already paired. Or type here the PIN you see under the code.'></div>
  </div>
  <div class='cells' id='cells'>
    <div class='cell'></div><div class='cell'></div><div class='cell'></div><div class='cell'></div><div class='cell'></div><div class='cell'></div>
    <input id='pin' inputmode='numeric' pattern='[0-9]*' maxlength='6' autocomplete='one-time-code' autofocus>
  </div>
  <div class='msg' id='msg'></div>
  <div class='keypad'>
    <button data-d='1'>1</button><button data-d='2'>2</button><button data-d='3'>3</button>
    <button data-d='4'>4</button><button data-d='5'>5</button><button data-d='6'>6</button>
    <button data-d='7'>7</button><button data-d='8'>8</button><button data-d='9'>9</button>
    <button class='ghost' data-d='clr' data-it='Cancella' data-en='Clear'></button><button data-d='0'>0</button><button class='ghost' data-d='del'>⌫</button>
  </div>
</div>

<script>
const italian=(navigator.language||'it').toLowerCase().startsWith('it');
document.querySelectorAll('[data-it]').forEach(e=>{e.textContent=italian?e.dataset.it:e.dataset.en;});
const T=(it,en)=>italian?it:en;
const pin=document.getElementById('pin'), cells=document.getElementById('cells'), msg=document.getElementById('msg');
const boxes=[...cells.querySelectorAll('.cell')];
let busy=false;

function makeDeviceId(){
  try{ if(window.crypto && typeof window.crypto.randomUUID==='function') return window.crypto.randomUUID(); }catch(_){ }
  return 'ccp-'+Date.now().toString(36)+'-'+Math.random().toString(36).slice(2,12);
}
function ensureDeviceId(){
  try{
    let id = localStorage.getItem('ccp_device_id');
    if(!id){ id = makeDeviceId(); localStorage.setItem('ccp_device_id', id); }
    document.cookie = 'ccp_device='+encodeURIComponent(id)+'; Path=/; Max-Age=31536000; SameSite=Lax';
    return id;
  }catch(_){ return ''; }
}
const deviceId = ensureDeviceId();

// Se ho già un token salvato, salto il PIN (persistenza dispositivo)
try{
  const t = localStorage.getItem('ccp_token');
  if(t && t.length>10){ location.href='/remote'; }
}catch(_){}

function show(){
  const v=pin.value.replace(/\D/g,'').slice(0,6);
  if(v!==pin.value) pin.value=v;
  boxes.forEach((b,i)=>{ b.textContent=v[i]||''; b.classList.toggle('on', i===v.length && v.length<6); });
  cells.classList.remove('bad');
  if(v.length===6 && !busy) auth(v);
}
pin.addEventListener('input',show);
document.querySelectorAll('.keypad button').forEach(b=>{
  b.onclick=()=>{
    const d=b.dataset.d;
    if(busy) return;
    if(d==='clr') pin.value='';
    else if(d==='del') pin.value=pin.value.slice(0,-1);
    else if(pin.value.length<6) pin.value+=d;
    show();
  };
});

async function auth(p){
  busy=true; msg.className='msg'; msg.textContent=T('Abbinamento…','Pairing…');
  try{
    const r=await fetch('/api/auth',{
      method:'POST', credentials:'same-origin',
      headers:{'Content-Type':'application/json','X-Device-Id': deviceId},
      body:JSON.stringify({pin:p, name:navigator.userAgent, deviceId: deviceId})
    });
    if(r.ok){
      try{ const j = await r.json(); if(j && j.token){ localStorage.setItem('ccp_token', j.token); } }catch(_){}
      location.href='/remote';
      return;
    }
    msg.className='msg bad';
    msg.textContent = r.status===429 ? T('Troppi tentativi: attendi un minuto.','Too many attempts: wait a minute.') : T('PIN non corretto: riprova.','Wrong PIN: try again.');
  }catch(_){
    msg.className='msg bad'; msg.textContent=T('Player non raggiungibile.','Player not reachable.');
  }
  cells.classList.add('bad'); pin.value=''; busy=false;
  boxes.forEach(b=>{ b.textContent=''; b.classList.remove('on'); }); boxes[0].classList.add('on');
}
show();
</script>
</body>
</html>";

    private static string RemoteHtml()
    {
        string logoFallbackSvg =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 520 120'>" +
            "<text x='0' y='85' font-size='72' font-family='Segoe UI,Roboto,Arial' font-weight='800' fill='#fff'>CinecorePlayer2025</text>" +
            "</svg>";

        static string SvgDataUriFromFile(string fileName, string fallbackSvg)
        {
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", fileName);
                if (File.Exists(p))
                {
                    var bytes = File.ReadAllBytes(p);
                    return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
                }
            }
            catch { }

            var fb = Encoding.UTF8.GetBytes(fallbackSvg);
            return "data:image/svg+xml;base64," + Convert.ToBase64String(fb);
        }

        static string SvgInline(string svgMarkup) => svgMarkup;

        // ===== Logo =====
        string logoDataUri;
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(p))
            {
                var bytes = File.ReadAllBytes(p);
                logoDataUri = "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
            else
            {
                logoDataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(logoFallbackSvg));
            }
        }
        catch
        {
            logoDataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(logoFallbackSvg));
        }

        // ===== Icone DAL TUO PLAYER (Assets/icons) =====
        string icoBack = SvgDataUriFromFile("arrow-back.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M15 18l-6-6 6-6' stroke='#fff' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>");

        string icoHome = SvgDataUriFromFile("home-2.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M4 10.5 12 4l8 6.5' stroke='#fff' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/><path d='M7 10v10h10V10' stroke='#fff' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>");

        string icoLibrary = SvgDataUriFromFile("library.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M5 6h4v14H5z' fill='none' stroke='#fff' stroke-width='2'/><path d='M10 6h4v14h-4z' fill='none' stroke='#fff' stroke-width='2'/><path d='M15 6h4v14h-4z' fill='none' stroke='#fff' stroke-width='2'/></svg>");

        string icoInfo = SvgDataUriFromFile("info-circle.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><circle cx='12' cy='12' r='9' stroke='#fff' stroke-width='2' fill='none'/><path d='M12 10v7' stroke='#fff' stroke-width='2'/><path d='M12 7h.01' stroke='#fff' stroke-width='3'/></svg>");

        string icoFull = SvgDataUriFromFile("maximize.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M8 3H3v5M16 3h5v5M3 16v5h5M21 16v5h-5' stroke='#fff' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>");

        // Volume
        string icoMute = SvgDataUriFromFile("volume-off.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M11 5 6 9H3v6h3l5 4z' fill='none' stroke='#fff' stroke-width='2'/><path d='M16 9l5 6M21 9l-5 6' stroke='#fff' stroke-width='2' stroke-linecap='round'/></svg>");

        string icoVolMinus = SvgDataUriFromFile("minus.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M11 5 6 9H3v6h3l5 4z' fill='none' stroke='#fff' stroke-width='2'/></svg>");

        string icoVolPlus = SvgDataUriFromFile("plus.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M11 5 6 9H3v6h3l5 4z' fill='none' stroke='#fff' stroke-width='2'/><path d='M16 8a4 4 0 0 1 0 8' stroke='#fff' stroke-width='2' fill='none'/><path d='M18 6a7 7 0 0 1 0 12' stroke='#fff' stroke-width='2' fill='none'/></svg>");

        // Playback
        string icoStop = SvgDataUriFromFile("player-stop.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect x='7' y='7' width='10' height='10' fill='#fff'/></svg>");

        string icoPlay = SvgDataUriFromFile("player-play.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M9 7l10 5-10 5z' fill='#fff'/></svg>");

        string icoPause = SvgDataUriFromFile("player-pause.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M8 6v12' stroke='#fff' stroke-width='3' stroke-linecap='round'/><path d='M16 6v12' stroke='#fff' stroke-width='3' stroke-linecap='round'/></svg>");

        string icoPrev = SvgDataUriFromFile("player-track-prev.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M6 6v12' stroke='#fff' stroke-width='3' stroke-linecap='round'/><path d='M18 7l-8 5 8 5z' fill='#fff'/></svg>");

        string icoNext = SvgDataUriFromFile("player-track-next.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M18 6v12' stroke='#fff' stroke-width='3' stroke-linecap='round'/><path d='M6 7l8 5-8 5z' fill='#fff'/></svg>");

        string icoBack10 = SvgDataUriFromFile("player-skip-back.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M11 19l-7-7 7-7' stroke='#fff' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/><path d='M20 19V5' stroke='#fff' stroke-width='2' stroke-linecap='round'/></svg>");

        string icoFwd10 = SvgDataUriFromFile("player-skip-forward.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M13 5l7 7-7 7' stroke='#fff' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/><path d='M4 5v14' stroke='#fff' stroke-width='2' stroke-linecap='round'/></svg>");

        // HDR / 3D
        string icoHdr = SvgDataUriFromFile("hdr.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>" +
            "<rect x='3.5' y='6.5' width='17' height='11' rx='3' fill='none' stroke='#fff' stroke-width='2'/>" +
            "<text x='12' y='14.2' text-anchor='middle' font-size='7' font-family='Segoe UI,Roboto,Arial' font-weight='800' fill='#fff'>HDR</text>" +
            "</svg>");

        string ico3d = SvgDataUriFromFile("3d.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>" +
            "<rect x='3.5' y='6.5' width='17' height='11' rx='3' fill='none' stroke='#fff' stroke-width='2'/>" +
            "<text x='12' y='14.2' text-anchor='middle' font-size='7' font-family='Segoe UI,Roboto,Arial' font-weight='800' fill='#fff'>3D</text>" +
            "</svg>");

        // POWER OFF
        string icoPowerOff = SvgDataUriFromFile("power.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M12 2v10' stroke='#fff' stroke-width='2' stroke-linecap='round'/><path d='M7 5a8 8 0 1 0 10 0' stroke='#fff' stroke-width='2' fill='none' stroke-linecap='round'/></svg>");

        // DPAD inline
        string dpadUpSvg = SvgInline("<svg class='icosvg' viewBox='0 0 24 24'><path d='M12 7l-6 6M12 7l6 6'/></svg>");
        string dpadDownSvg = SvgInline("<svg class='icosvg' viewBox='0 0 24 24'><path d='M12 17l-6-6M12 17l6-6'/></svg>");
        string dpadLeftSvg = SvgInline("<svg class='icosvg' viewBox='0 0 24 24'><path d='M7 12l6-6M7 12l6 6'/></svg>");
        string dpadRightSvg = SvgInline("<svg class='icosvg' viewBox='0 0 24 24'><path d='M17 12l-6-6M17 12l-6 6'/></svg>");
        string dpadOkSvg = SvgInline("<svg class='icosvg' viewBox='0 0 24 24'><path d='M5 13l4 4L19 7'/></svg>");

        return @"<!doctype html>
<html lang='it'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no'>
<title>Cinecore Remote</title>
<style>
:root{
  --bg:#000;
  --txt:#fff;

  --remoteA:#0f0f10;
  --remoteB:#060607;
  --edge:#2a2a2d;

  --panel:#101012;
  --panel2:#17171a;

  --accent:#39a8ff;
  --accent2:#8a6bff;

  --r:44px;
  --btnr:18px;

  --font:system-ui,'Segoe UI',Roboto,Arial,sans-serif;
}
*{
  box-sizing:border-box;
  margin:0;
  padding:0;
  -webkit-tap-highlight-color:transparent;
  -webkit-touch-callout:none;
  -webkit-user-select:none;
  user-select:none;
}
html,body{
  height:100%;
  background:var(--bg);
  color:var(--txt);
  font-family:var(--font);
  overflow-x:hidden;   
  touch-action:pan-y;   
}

body{
  display:flex;
  justify-content:center;
  align-items:flex-start;
  padding:12px; 
  overflow-y:auto;
  overflow-x:hidden;         
  overscroll-behavior-x:none; 
}

.stage{
  width:min(420px, 100%); /* FIX: non obeso */
  height:auto;
  display:flex;
  justify-content:center;
}
.remote{
  width:100%;
  height:auto;
  position:relative;
  border-radius:var(--r);
  background:
    radial-gradient(1200px 600px at 30% 0%, rgba(255,255,255,.09), transparent 55%),
    linear-gradient(180deg, var(--remoteA), var(--remoteB));
  border:1px solid var(--edge);
  box-shadow:
    0 28px 70px rgba(0,0,0,.85),
    inset 0 1px 0 rgba(255,255,255,.06),
    inset 0 -1px 0 rgba(0,0,0,.6);
  padding:16px 14px 16px; /* FIX: non obeso */
  display:flex;
  flex-direction:column;
  gap:12px;
  overflow:hidden;
}
.remote:before{
  content:'';
  position:absolute;
  inset:-2px;
  border-radius:calc(var(--r) + 2px);
  pointer-events:none;
  box-shadow: inset 0 0 0 1px rgba(255,255,255,.04);
}

.panel{
  border-radius:24px;
  background:linear-gradient(180deg, rgba(255,255,255,.05), rgba(255,255,255,.02));
  border:1px solid rgba(255,255,255,.07);
  box-shadow:
    inset 0 1px 0 rgba(255,255,255,.06),
    0 18px 45px rgba(0,0,0,.55);
  padding:12px;
}

/* TOP: solo logo centrato grande + power separato (non tocca lo stato) */
.topbar{
  display:grid;
  grid-template-columns:44px 1fr 44px;
  align-items:center;
  gap:10px;
  margin-bottom:6px;
}
.topspacer{ width:44px; height:44px; }
.brandcenter{
  display:flex;
  align-items:center;
  justify-content:center;
}
.brandcenter img{
  height:70px; /* FIX: ingrandisci logo, non il telecomando */
  width:auto;
  object-fit:contain;
  filter: drop-shadow(0 2px 10px rgba(0,0,0,.65));
}

.pwroff{
  appearance:none;
  width:44px;
  height:44px;
  border-radius:999px;
  border:1px solid rgba(255,120,120,.55);
  background:
    radial-gradient(circle at 50% 25%, rgba(255,140,140,.95), rgba(170,0,0,.55) 55%, rgba(60,0,0,.8) 100%);
  box-shadow:
    0 14px 28px rgba(0,0,0,.75),
    0 0 18px rgba(255,0,0,.30),
    inset 0 1px 0 rgba(255,255,255,.15);
  display:flex;
  align-items:center;
  justify-content:center;
}
.pwroff:active{ transform:scale(.97); }

.icoimg{width:20px;height:20px;display:block;object-fit:contain;}
.icosvg{
  width:20px;height:20px;display:block;
  fill:none;stroke:currentColor;stroke-width:2;
  stroke-linecap:round;stroke-linejoin:round;
}
img{ -webkit-user-drag:none; user-drag:none; }
.icoimg, .icosvg{ pointer-events:none; } 

/* Stato */
.statepill{
  display:flex;
  justify-content:center;
  gap:10px;
  padding:10px 10px;
  border-radius:999px;
  background:rgba(0,0,0,.30);
  border:1px solid rgba(255,255,255,.10);
}
.chip{
  font-size:11px;
  font-weight:900;
  padding:6px 12px;
  border-radius:999px;
  background:rgba(255,255,255,.06);
  border:1px solid rgba(255,255,255,.10);
  min-width:74px;
  text-align:center;
}
.nowtitle{
  margin-top:10px;
  text-align:center;
  font-size:14px;
  font-weight:900;
  white-space:nowrap;
  overflow:hidden;
  text-overflow:ellipsis;
  color:rgba(255,255,255,.92);
}
.nowtitle:empty{ display:none; } /* FIX: niente “trattino” se vuoto */

/* Bottoni */
.btn{
  appearance:none;
  border-radius:var(--btnr);
  border:1px solid rgba(255,255,255,.09);
  background:
    radial-gradient(circle at 40% 15%, rgba(255,255,255,.11), rgba(255,255,255,.03) 60%),
    linear-gradient(180deg, var(--panel2), var(--panel));
  color:#fff;
  font-weight:900;
  box-shadow:
    0 14px 24px rgba(0,0,0,.55),
    inset 0 1px 0 rgba(255,255,255,.08);
  display:flex;
  align-items:center;
  justify-content:center;
  padding:12px 10px;
  min-height:56px;
  user-select:none;
}
.btn:active{transform:translateY(1px);}

.btn.main{
  background:
    radial-gradient(circle at 40% 15%, rgba(255,255,255,.14), rgba(255,255,255,.04) 60%),
    linear-gradient(180deg, #1e1e22, #0e0e10);
  border:1px solid rgba(255,255,255,.12);
}
.btn.warn{
  border:1px solid rgba(255,120,120,.55);
  background:
    radial-gradient(circle at 40% 15%, rgba(255,255,255,.10), rgba(255,0,0,.18) 60%),
    linear-gradient(180deg, rgba(130,0,0,.95), rgba(40,0,0,.95));
  box-shadow:
    0 18px 30px rgba(0,0,0,.65),
    0 0 16px rgba(255,0,0,.25),
    inset 0 1px 0 rgba(255,255,255,.12);
}
.btn.active{
  box-shadow:
    0 16px 28px rgba(0,0,0,.60),
    0 0 16px rgba(57,168,255,.25),
    inset 0 1px 0 rgba(255,255,255,.10);
  border:1px solid rgba(57,168,255,.35);
}

.btn.round{border-radius:999px; width:56px; min-height:56px; padding:0;}
.btn.sround{border-radius:999px; width:52px; min-height:52px; padding:0;}

.grid3{display:grid; grid-template-columns:repeat(3, 1fr); gap:10px;}
.grid3 .btn{min-height:62px;}

.grid2{display:grid; grid-template-columns:repeat(2, 1fr); gap:10px;}
.grid2 .btn{min-height:62px;}

/* DPAD con lati 2+2 */
.navrow{
  display:grid;
  grid-template-columns:64px 1fr 64px;
  gap:10px;
  align-items:center;
}
.sidecol{
  display:flex;
  flex-direction:column;
  gap:10px;
}
.sidebtn{
  width:64px;
  min-height:64px;
  padding:0;
  border-radius:16px;
}

.dpadwrap{display:flex; justify-content:center; align-items:center;}
.dpad{
  width:170px;  /* FIX: non obeso */
  height:170px; /* FIX: non obeso */
  border-radius:999px;
  background:
    radial-gradient(circle at 35% 20%, rgba(255,255,255,.08), transparent 55%),
    linear-gradient(180deg, rgba(255,255,255,.05), rgba(0,0,0,.12));
  border:1px solid rgba(255,255,255,.10);
  box-shadow:
    0 22px 42px rgba(0,0,0,.65),
    inset 0 2px 0 rgba(255,255,255,.06);
  position:relative;
}
.dpad .btn{position:absolute; min-height:56px;}
.dpad .up   {top:10px; left:50%; transform:translateX(-50%);}
.dpad .down {bottom:10px; left:50%; transform:translateX(-50%);}
.dpad .left {left:10px; top:50%; transform:translateY(-50%);}
.dpad .right{right:10px; top:50%; transform:translateY(-50%);}
.dpad .ok{
  top:50%; left:50%; transform:translate(-50%,-50%);
  width:76px; min-height:76px;
  border-radius:18px;
  background:
    radial-gradient(circle at 40% 15%, rgba(255,255,255,.12), rgba(255,255,255,.04) 60%),
    linear-gradient(180deg, #222228, #0b0b0d);
}

/* TEMPO */
.time-title{
  text-align:center;
  font-size:12px;
  font-weight:900;
  letter-spacing:.12em;
  color:rgba(255,255,255,.75);
  margin-bottom:10px;
}
.time-row{
  display:flex;
  justify-content:space-between;
  font-family:ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono','Courier New', monospace;
  font-size:11px;
  color:rgba(255,255,255,.65);
  margin-top:8px;
}
input[type=range]{
  -webkit-appearance:none;
  appearance:none;
  width:100%;
  background:transparent;
  margin:0;
  height:34px;
  touch-action:pan-x; 
}
input[type=range]::-webkit-slider-runnable-track{
  height:7px;
  background:linear-gradient(90deg,var(--accent) 0%,var(--accent2) 100%);
  border-radius:999px;
  box-shadow:
    inset 0 0 0 1px rgba(255,255,255,.10),
    0 0 14px rgba(57,168,255,.35);
}
input[type=range]::-webkit-slider-thumb{
  -webkit-appearance:none;
  appearance:none;
  width:22px;
  height:22px;
  border-radius:50%;
  background:#fff;
  border:1px solid rgba(0,0,0,.25);
  box-shadow:0 3px 8px rgba(0,0,0,.7), 0 0 12px rgba(255,255,255,.35);
  margin-top:-7.5px;
}

@media (max-height: 720px){
  .remote{gap:10px; padding:14px;} /* FIX: non obeso */
  .dpad{width:160px;height:160px;} /* FIX: non obeso */
  .grid3 .btn{min-height:58px;}
  .grid2 .btn{min-height:58px;}
}

/* Cinecore glass remote: same visual language as the desktop HUD. */
html,body{
  min-height:100%;
  background:
    radial-gradient(800px 520px at 50% -8%, rgba(38,151,235,.22), transparent 62%),
    linear-gradient(180deg,#06101a 0%,#02070c 58%,#010305 100%);
}
body{padding:max(14px,env(safe-area-inset-top)) 12px max(18px,env(safe-area-inset-bottom));}
.stage{width:min(430px,100%);}
.remote{
  border-radius:30px;
  padding:16px;
  gap:11px;
  background:linear-gradient(160deg,rgba(15,33,49,.94),rgba(4,12,20,.97) 52%,rgba(2,7,12,.98));
  border:1px solid rgba(118,196,255,.18);
  box-shadow:0 32px 90px rgba(0,0,0,.72),inset 0 1px 0 rgba(255,255,255,.08);
}
.remote:after{
  content:'';position:absolute;left:22%;right:22%;top:0;height:2px;
  background:linear-gradient(90deg,transparent,var(--accent),transparent);
  opacity:.8;
}
.panel{
  position:relative;
  border-radius:19px;
  padding:12px;
  background:linear-gradient(145deg,rgba(255,255,255,.065),rgba(255,255,255,.018));
  border:1px solid rgba(171,216,248,.105);
  box-shadow:inset 0 1px 0 rgba(255,255,255,.055),0 12px 30px rgba(0,0,0,.24);
  backdrop-filter:blur(18px);
}
.topbar{grid-template-columns:42px 1fr 42px;margin:0 2px 2px;}
.brandcenter img{height:48px;filter:drop-shadow(0 5px 14px rgba(0,0,0,.52));}
.pwroff{width:40px;height:40px;border-color:rgba(255,104,117,.42);background:rgba(112,22,34,.58);box-shadow:inset 0 1px rgba(255,255,255,.10),0 8px 22px rgba(0,0,0,.32);}
.statepill{padding:5px;background:rgba(1,8,14,.44);border-color:rgba(255,255,255,.07);}
.chip{padding:7px 8px;min-width:0;flex:1;color:rgba(228,238,247,.88);background:rgba(85,158,214,.08);border-color:rgba(126,194,243,.12);letter-spacing:.04em;}
.nowtitle{margin:11px 8px 1px;font-size:13px;letter-spacing:.01em;}
.btn{
  border-radius:15px;
  border-color:rgba(157,207,244,.12);
  background:linear-gradient(155deg,rgba(34,59,78,.74),rgba(11,24,36,.78));
  box-shadow:inset 0 1px rgba(255,255,255,.075),0 8px 20px rgba(0,0,0,.24);
  transition:transform .13s ease,border-color .13s ease,background .13s ease,box-shadow .13s ease;
}
.btn:hover{border-color:rgba(91,182,247,.34);background:linear-gradient(155deg,rgba(43,76,101,.78),rgba(12,31,46,.82));}
.btn:active{transform:scale(.965);border-color:rgba(82,188,255,.65);box-shadow:inset 0 0 0 1px rgba(82,188,255,.18);}
.btn.main{background:linear-gradient(155deg,rgba(42,114,164,.72),rgba(12,49,74,.9));border-color:rgba(87,190,255,.32);}
.btn.warn{background:linear-gradient(155deg,rgba(111,31,42,.78),rgba(53,14,22,.9));border-color:rgba(255,112,126,.34);box-shadow:inset 0 1px rgba(255,255,255,.07),0 8px 20px rgba(0,0,0,.24);}
.dpad{
  width:174px;height:174px;
  background:radial-gradient(circle at 50% 50%,rgba(38,82,112,.44) 0 37%,rgba(10,27,40,.82) 39% 63%,rgba(79,157,210,.12) 64% 65%,rgba(3,12,20,.74) 66%);
  border-color:rgba(125,196,244,.18);
  box-shadow:inset 0 1px rgba(255,255,255,.08),0 18px 40px rgba(0,0,0,.35);
}
.dpad .ok{width:70px;min-height:70px;border-radius:999px;background:linear-gradient(150deg,rgba(54,139,198,.92),rgba(19,70,105,.95));border-color:rgba(116,211,255,.42);}
.time-title{color:rgba(180,207,227,.72);font-size:10px;letter-spacing:.18em;}
input[type=range]::-webkit-slider-runnable-track{height:5px;background:linear-gradient(90deg,var(--accent),#6fd3ff);box-shadow:none;}
input[type=range]::-webkit-slider-thumb{width:19px;height:19px;margin-top:-7px;box-shadow:0 3px 10px rgba(0,0,0,.55),0 0 0 5px rgba(57,168,255,.12);}
@media (max-width:360px){.remote{padding:12px;border-radius:24px}.panel{padding:10px}.navrow{grid-template-columns:56px 1fr 56px}.sidebtn{width:56px;min-height:58px}.dpad{width:156px;height:156px}}

/* Remote dashboard: layout funzionale, non una semplice ricolorazione. */
:root{--bg:#0b0a09;--txt:#f5f1e9;--panel:#171613;--panel2:#201e1a;--accent:#e7b96b;--accent2:#d67b68;--edge:rgba(244,232,211,.12)}
html,body{background:radial-gradient(700px 440px at 50% -12%,rgba(231,185,107,.13),transparent 68%),linear-gradient(180deg,#0d0c0a,#080706);color:var(--txt)}
body{padding:max(16px,env(safe-area-inset-top)) 12px max(22px,env(safe-area-inset-bottom))}
.stage{width:min(470px,100%)}
.remote{padding:0;gap:12px;border:0;border-radius:0;background:transparent;box-shadow:none;overflow:visible}
.remote:before,.remote:after{display:none}
.remote-head{display:flex;align-items:center;justify-content:space-between;padding:2px 3px 5px}
.identity{display:flex;align-items:center;gap:13px;min-width:0}
.identity img{width:96px;height:36px;object-fit:contain;object-position:left center}
.connection{display:flex;align-items:center;gap:7px;color:rgba(245,241,233,.56);font-size:10px;font-weight:800;letter-spacing:.13em;text-transform:uppercase}
.connection:before{content:'';width:7px;height:7px;border-radius:50%;background:#7fd398;box-shadow:0 0 0 4px rgba(127,211,152,.09)}
.pwroff{width:42px;height:42px;background:rgba(158,53,48,.16);border-color:rgba(226,112,100,.28);box-shadow:none}
.panel{padding:15px;border-radius:22px;background:rgba(26,24,21,.86);border:1px solid var(--edge);box-shadow:0 15px 36px rgba(0,0,0,.19);backdrop-filter:blur(16px)}
.now-panel{padding:17px 17px 14px;background:linear-gradient(145deg,rgba(39,35,29,.94),rgba(20,19,17,.92))}
.now-kicker,.section-title{font-size:10px;line-height:1;font-weight:850;letter-spacing:.16em;text-transform:uppercase;color:rgba(245,241,233,.47)}
.nowtitle{margin:10px 0 13px;text-align:left;font-size:18px;line-height:1.25;font-weight:750;color:var(--txt)}
.nowtitle:empty:after{content:'Nessun contenuto in riproduzione';color:rgba(245,241,233,.4);font-weight:600}
.statepill{justify-content:flex-start;gap:6px;padding:0;background:transparent;border:0;margin-bottom:12px}
.chip{flex:0 0 auto;min-width:0;padding:5px 9px;border:1px solid rgba(231,185,107,.13);background:rgba(231,185,107,.07);color:rgba(247,239,225,.75);font-size:9px;letter-spacing:.08em}
.time-title{display:none}
input[type=range]{height:28px}
input[type=range]::-webkit-slider-runnable-track{height:4px;background:linear-gradient(90deg,var(--accent),#f1d39d);box-shadow:none}
input[type=range]::-webkit-slider-thumb{width:18px;height:18px;margin-top:-7px;background:#f8f0df;border:3px solid #30291f;box-shadow:0 2px 8px rgba(0,0,0,.45)}
.time-row{margin-top:3px;color:rgba(245,241,233,.5);font-size:10px}
.section-head{display:flex;align-items:center;justify-content:space-between;margin:0 2px 11px}
.section-note{font-size:10px;color:rgba(245,241,233,.4)}
.btn{min-height:54px;border-radius:16px;border:1px solid rgba(244,232,211,.09);background:#211f1b;box-shadow:none;color:var(--txt);transition:transform .11s ease,background .11s ease,border-color .11s ease}
.btn:hover{background:#29261f;border-color:rgba(231,185,107,.22)}
.btn:active{transform:scale(.96);background:#302a21;border-color:rgba(231,185,107,.48)}
.btn.main{background:var(--accent);border-color:rgba(255,238,201,.38);color:#21190f}
.btn.main .icoimg{filter:brightness(.18)}
.btn.warn{background:rgba(171,59,51,.13);border-color:rgba(226,112,100,.2);box-shadow:none}
.transport-primary{display:grid;grid-template-columns:1fr 1.28fr 1fr;gap:9px;margin-bottom:9px}
.transport-primary .btn{min-height:66px}
.transport-secondary{display:grid;grid-template-columns:1fr 1fr 1fr;gap:9px}
.transport-stop{display:flex;justify-content:center;margin-top:9px}
.transport-stop .btn{width:74px;min-height:42px}
.control-panel{padding:15px}
.dpadwrap{margin-top:3px}
.dpad{width:100%;height:auto;display:grid;grid-template-columns:repeat(3,1fr);grid-template-rows:repeat(3,58px);gap:7px;padding:7px;border-radius:20px;background:#11100e;border:1px solid rgba(244,232,211,.08);box-shadow:none}
.dpad .btn,.dpad .up,.dpad .down,.dpad .left,.dpad .right,.dpad .ok{position:static;transform:none;width:auto;min-height:0;border-radius:13px}
.dpad .up{grid-column:2;grid-row:1}.dpad .left{grid-column:1;grid-row:2}.dpad .ok{grid-column:2;grid-row:2;background:rgba(231,185,107,.16);border-color:rgba(231,185,107,.28)}.dpad .right{grid-column:3;grid-row:2}.dpad .down{grid-column:2;grid-row:3}
.quick-grid{display:grid;grid-template-columns:repeat(5,1fr);gap:7px;margin-top:12px}
.quick-grid .btn{min-width:0;min-height:54px;padding:9px 4px;flex-direction:column;gap:5px;border-radius:13px}
.quick-grid .icoimg{width:18px;height:18px}
.quick-grid small{font-size:8px;font-weight:750;color:rgba(245,241,233,.55);white-space:nowrap}
.quick-grid .btn:active small{color:rgba(245,241,233,.85)}
.icoimg,.icosvg{width:21px;height:21px}
@media(max-width:360px){.panel{padding:12px}.quick-grid{grid-template-columns:repeat(4,1fr)}.dpad{grid-template-rows:repeat(3,52px)}.identity img{width:84px}}
</style>
</head>
<body>

<div class='stage'>
  <div class='remote'>
    <header class='remote-head'>
      <div class='identity'>
        <img src='" + logoDataUri + @"' alt='Cinecore'>
        <span class='connection'>Connesso</span>
      </div>
      <button class='pwroff' onclick='cmd(""poweroff"")' title='Power off'>
        <img class='icoimg' src='" + icoPowerOff + @"' alt='Power off'>
      </button>
    </header>

    <section class='panel now-panel'>
      <div class='now-kicker'>In riproduzione</div>
      <div class='nowtitle' id='title'></div>
      <div class='statepill'>
        <span class='chip' id='hdrChip'>SDR</span>
        <span class='chip' id='audioChip' title='Se BITSTREAM, volume fisso 100%'>PCM</span>
        <span class='chip' id='dimChip'>2D</span>
      </div>
      <input id='seek' type='range' min='0' max='0' value='0' step='0.1'
             oninput='onSeekInput(this.value)'
             onchange='onSeekCommit(this.value)' />
      <div class='time-row'><span id='tcur'>00:00</span><span id='tdur'>00:00</span></div>
    </section>

    <section class='panel'>
      <div class='section-head'><span class='section-title'>Trasporto</span><span class='section-note'>tieni premuto ±10 per scansione</span></div>
      <div class='transport-primary'>
        <button class='btn' onclick='cmd(""prev"")' title='Cap -'><img class='icoimg' src='" + icoPrev + @"' alt='Prev'></button>
        <button class='btn main' id='playBtn' onclick='cmd(""play"")' title='Play'><img class='icoimg' src='" + icoPlay + @"' alt='Play'></button>
        <button class='btn' onclick='cmd(""next"")' title='Cap +'><img class='icoimg' src='" + icoNext + @"' alt='Next'></button>
      </div>
      <div class='transport-secondary'>
        <button class='btn' id='btnBack10' title='-10s'><img class='icoimg' src='" + icoBack10 + @"' alt='-10s'></button>
        <button class='btn' id='pauseBtn' onclick='cmd(""pause"")' title='Pausa'><img class='icoimg' src='" + icoPause + @"' alt='Pausa'></button>
        <button class='btn' id='btnFwd10' title='+10s'><img class='icoimg' src='" + icoFwd10 + @"' alt='+10s'></button>
      </div>
      <div class='transport-stop'>
        <button class='btn warn' onclick='cmd(""stop"")' title='Stop'><img class='icoimg' src='" + icoStop + @"' alt='Stop'></button>
      </div>
    </section>

    <section class='panel control-panel'>
      <div class='section-head'><span class='section-title'>Navigazione</span><span class='section-note'>menu e volume</span></div>
      <div class='dpadwrap'>
        <div class='dpad'>
          <button class='btn up' onclick='cmd(""up"")' title='Su'>" + dpadUpSvg + @"</button>
          <button class='btn left' onclick='cmd(""left"")' title='Sinistra'>" + dpadLeftSvg + @"</button>
          <button class='btn ok' onclick='cmd(""ok"")' title='OK'>" + dpadOkSvg + @"</button>
          <button class='btn right' onclick='cmd(""right"")' title='Destra'>" + dpadRightSvg + @"</button>
          <button class='btn down' onclick='cmd(""down"")' title='Giu'>" + dpadDownSvg + @"</button>
        </div>
      </div>
      <div class='quick-grid'>
        <button class='btn' onclick='cmd(""back"")'><img class='icoimg' src='" + icoBack + @"' alt='Back'><small>Indietro</small></button>
        <button class='btn' onclick='cmd(""home"")'><img class='icoimg' src='" + icoHome + @"' alt='Home'><small>Home</small></button>
        <button class='btn' onclick='cmd(""library"")'><img class='icoimg' src='" + icoLibrary + @"' alt='Libreria'><small>Libreria</small></button>
        <button class='btn' onclick='cmd(""info"")'><img class='icoimg' src='" + icoInfo + @"' alt='Info'><small>Info</small></button>
        <button class='btn' onclick='cmd(""full"")'><img class='icoimg' src='" + icoFull + @"' alt='Fullscreen'><small>Schermo</small></button>
        <button class='btn' onclick='cmd(""mute"")'><img class='icoimg' src='" + icoMute + @"' alt='Mute'><small>Mute</small></button>
        <button class='btn' onclick='cmd(""voldown"")'><img class='icoimg' src='" + icoVolMinus + @"' alt='Volume -'><small>Vol −</small></button>
        <button class='btn' onclick='cmd(""volup"")'><img class='icoimg' src='" + icoVolPlus + @"' alt='Volume +'><small>Vol +</small></button>
        <button class='btn' id='hdrBtn' onclick='cmd(""hdr"")'><img class='icoimg' src='" + icoHdr + @"' alt='HDR'><small>HDR</small></button>
        <button class='btn' id='stereoBtn' onclick='cmd(""stereo"")'><img class='icoimg' src='" + ico3d + @"' alt='3D'><small>3D</small></button>
      </div>
    </section>

  </div>
</div>

<script>
function makeDeviceId(){
  try{
    if(window.crypto && typeof window.crypto.randomUUID==='function') return window.crypto.randomUUID();
  }catch(_){ }
  return 'ccp-'+Date.now().toString(36)+'-'+Math.random().toString(36).slice(2,12);
}
function ensureDeviceId(){
  try{
    let id = localStorage.getItem('ccp_device_id');
    if(!id){
      id = makeDeviceId();
      localStorage.setItem('ccp_device_id', id);
    }
    document.cookie = 'ccp_device='+encodeURIComponent(id)+'; Path=/; Max-Age=31536000; SameSite=Lax';
    return id;
  }catch(e){
    return '';
  }
}
function getTok(){
  try{ return localStorage.getItem('ccp_token')||''; }catch(e){ return ''; }
}
function authHeaders(){
  const h = {};
  const deviceId = ensureDeviceId();
  if(deviceId) h['X-Device-Id'] = deviceId;
  const t=getTok();
  if(t) h['Authorization'] = 'Bearer '+t;
  return h;
}
function fmt(sec){
  sec = Math.max(0, Math.floor(sec || 0));
  let h = Math.floor(sec/3600);
  let m = Math.floor((sec%3600)/60);
  let s = sec%60;
  return (h>0? (''+h).padStart(2,'0')+':' : '')
        +(''+m).padStart(2,'0')+':'
        +(''+s).padStart(2,'0');
}
function qs(sel){ return document.querySelector(sel); }

async function poll(){
  try{
    const r = await fetch('/api/state', { credentials:'same-origin', headers: authHeaders() });
    if(r.status===401){ location.href='/'; return; }

    const s = await r.json();

    const titleEl = qs('#title');
    if(titleEl){
      const t = (s.Title || '').trim();
      titleEl.textContent = t;
    }

    const isHdr = !!s.OutputHdr;
    const hdrText = isHdr ? 'HDR' : 'SDR';

    const audioText = s.Bitstream ? 'BITSTREAM' : 'PCM';
    const is3d = !!s.Is3D;
    const dimText = is3d ? '3D' : '2D';

    const hdrC = qs('#hdrChip'); if(hdrC) hdrC.textContent = hdrText;
    const audC = qs('#audioChip'); if(audC) audC.textContent = audioText;
    const dimC = qs('#dimChip'); if(dimC) dimC.textContent = dimText;

    const seek = qs('#seek');
    if(seek){
      if(s.Duration > 0){
        if(!seek._drag){
          seek.max = Number(s.Duration).toFixed(1);
          seek.value = Number(s.Position).toFixed(1);

          const tcur = qs('#tcur'); if(tcur) tcur.textContent = fmt(s.Position);
          const tdur = qs('#tdur'); if(tdur) tdur.textContent = fmt(s.Duration);
        }
      }else{
        seek.max = '0';
        seek.value = '0';
        const tcur = qs('#tcur'); if(tcur) tcur.textContent = '00:00';
        const tdur = qs('#tdur'); if(tdur) tdur.textContent = '00:00';
      }
    }
  }catch(e){
    console.error(""[POLL ERROR]"", e);
  }
}
setInterval(poll,700);
poll();

function cmd(c){
  fetch('/api/cmd?cmd='+encodeURIComponent(c), { credentials:'same-origin', headers: authHeaders() })
    .then(r=>{ if(r.status===401) location.href='/'; })
    .catch(err=>{ console.error(""[CMD ERROR]"", err); });
}

let _scrubTs = 0;

function onSeekInput(v){
  let e = qs('#seek');
  if(!e) return;
  e._drag = true;

  const tcur = qs('#tcur');
  if(tcur) tcur.textContent = fmt(parseFloat(v||'0'));

  const now = Date.now();
  if(now - _scrubTs < 90) return;
  _scrubTs = now;

  console.log(""[SCRUB LIVE]"", v);

  fetch('/api/cmd?cmd=scrub&pos='+encodeURIComponent(v), { credentials:'same-origin', headers: authHeaders() })
    .then(r=>{ if(r.status===401) location.href='/'; })
    .catch(err=>{ console.error(""[SCRUB ERROR]"", err); });
}

function onSeekCommit(v){
  let e = qs('#seek');
  if(e) e._drag = false;

  console.log(""[SEEK COMMIT]"", v);

  fetch('/api/cmd?cmd=seek&pos='+encodeURIComponent(v), { credentials:'same-origin', headers: authHeaders() })
    .then(r=>{ if(r.status===401) location.href='/'; })
    .catch(err=>{ console.error(""[SEEK ERROR]"", err); });
}
// Hardening: no zoom + no long-press preview/context menu
document.addEventListener('contextmenu', e => e.preventDefault());
document.addEventListener('dragstart', e => e.preventDefault());

// iOS Safari pinch-zoom events
document.addEventListener('gesturestart', e => e.preventDefault());
document.addEventListener('gesturechange', e => e.preventDefault());
document.addEventListener('gestureend', e => e.preventDefault());

document.addEventListener('DOMContentLoaded', () => {
  document.querySelectorAll('img').forEach(img => img.setAttribute('draggable','false'));

  // Long-press su +/-10s: manda scan_back/scan_fwd (avanzamento continuo). Tap = skip +/-10s.
  function bindSkipHold(btnId, shortCmd, holdCmd) {
    const el = document.getElementById(btnId);
    if (!el) return;
    let timer = null;
    let held = false;
    const clearT = () => { if (timer) { clearTimeout(timer); timer = null; } };

    el.addEventListener('pointerdown', (e) => {
      held = false;
      clearT();
      timer = setTimeout(() => {
        held = true;
        cmd(holdCmd);
      }, 420);
    });

    el.addEventListener('pointerup', (e) => {
      clearT();
      if (!held) cmd(shortCmd);
    });

    el.addEventListener('pointercancel', () => { clearT(); held = false; });
    // Se trascini fuori mentre sei premuto, annulla il tap
    el.addEventListener('pointerleave', () => { if (!held) { clearT(); held = true; } });
  }

  bindSkipHold('btnBack10', 'back10', 'scan_back');
  bindSkipHold('btnFwd10', 'fwd10', 'scan_fwd');
});

</script>
</body>
</html>";
    }

    // ====================== FrontDoor :80 proxy -> _port ======================
    private void StartFrontDoor80()
    {
        if (_port == 80) return; // già serviamo direttamente su 80

        try
        {
            _tcpFront = new TcpListener(IPAddress.Any, 80);
            _tcpFront.Start();
            _frontRunning = true;

            var token = _cts?.Token ?? CancellationToken.None;
            _frontLoop = Task.Run(() => FrontAcceptLoop(token));
            Dbg.Log("Remote server: front door attiva su porta 80.");
        }
        catch (Exception ex)
        {
            _tcpFront = null;
            _frontRunning = false;
            Dbg.Warn($"Remote server: front door porta 80 non disponibile ({ex.Message}). Usa {PrimaryUrl}.");
        }
    }

    private void StopFrontDoor80()
    {
        _frontRunning = false;
        try { _tcpFront?.Stop(); } catch { }
        _tcpFront = null;
    }

    private async Task FrontAcceptLoop(CancellationToken token)
    {
        if (_tcpFront == null) return;

        while (_frontRunning && !token.IsCancellationRequested)
        {
            TcpClient? cli = null;
            try { cli = await _tcpFront.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch
            {
                if (!_frontRunning) break;
                continue;
            }

            if (cli != null)
                _ = Task.Run(() => HandleFrontProxyClient(cli));
        }
    }

    private async Task HandleFrontProxyClient(TcpClient downstream)
    {
        using (downstream)
        using (var down = downstream.GetStream())
        {
            TcpClient? upstream = null;
            try
            {
                upstream = new TcpClient(AddressFamily.InterNetwork);
                await upstream.ConnectAsync(IPAddress.Loopback, _port);
            }
            catch
            {
                // fallback: rispondo 502
                try
                {
                    using var w = new StreamWriter(down, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                    var resp = new SimpleResponse
                    {
                        StatusCode = 500,
                        ContentType = "text/plain; charset=utf-8",
                        BodyText = "FrontDoor proxy error"
                    };
                    await WriteResponse(w, resp);
                }
                catch { }
                try { upstream?.Dispose(); } catch { }
                return;
            }

            using (upstream)
            using (var up = upstream.GetStream())
            {
                try
                {
                    string clientIp = ((IPEndPoint?)downstream.Client.RemoteEndPoint)?.Address.ToString() ?? string.Empty;
                    byte[] marker = Encoding.ASCII.GetBytes("\r\n\r\n");
                    var headerBuffer = new List<byte>(4096);
                    var tmp = new byte[1024];
                    int headerEnd = -1;

                    while (headerEnd < 0 && headerBuffer.Count < 65536)
                    {
                        int n = await down.ReadAsync(tmp, 0, tmp.Length);
                        if (n <= 0) break;
                        for (int i = 0; i < n; i++)
                            headerBuffer.Add(tmp[i]);
                        headerEnd = IndexOfSequence(headerBuffer, marker);
                    }

                    if (headerBuffer.Count == 0)
                        return;

                    byte[] firstChunk = headerBuffer.ToArray();
                    if (headerEnd < 0)
                    {
                        await up.WriteAsync(firstChunk, 0, firstChunk.Length);
                    }
                    else
                    {
                        string headerText = Encoding.ASCII.GetString(firstChunk, 0, headerEnd);
                        // L'indirizzo del client lo dichiara solo il proxy: un X-Forwarded-For
                        // inviato dal client stesso viene scartato (altrimenti poteva spacciarsi
                        // per l'IP di un dispositivo gia' abbinato).
                        headerText = string.Join("\r\n", headerText.Split(new[] { "\r\n" }, StringSplitOptions.None)
                            .Where((line, index) => index == 0 || !line.StartsWith("X-Forwarded-For:", StringComparison.OrdinalIgnoreCase)));
                        if (!string.IsNullOrWhiteSpace(clientIp))
                            headerText += "\r\nX-Forwarded-For: " + clientIp;

                        byte[] outHeader = Encoding.ASCII.GetBytes(headerText + "\r\n\r\n");
                        await up.WriteAsync(outHeader, 0, outHeader.Length);

                        int bodyOffset = headerEnd + marker.Length;
                        if (bodyOffset < firstChunk.Length)
                            await up.WriteAsync(firstChunk, bodyOffset, firstChunk.Length - bodyOffset);
                    }

                    await up.FlushAsync();

                    var t1 = down.CopyToAsync(up);
                    var t2 = up.CopyToAsync(down);
                    await Task.WhenAny(t1, t2);
                }
                catch
                {
                    // best-effort proxy
                }
                finally
                {
                    try { upstream.Close(); } catch { }
                    try { downstream.Close(); } catch { }
                }
            }
        }
    }

    // ====================== mDNS hostname univoco ======================
    private void StartMdns()
    {
        try
        {
            _mdns ??= new MdnsResponder(_mdnsHostFqdn, LocalIPv4List);
            _mdns.Start();
        }
        catch
        {
            _mdns = null;
        }
    }

    private void StopMdns()
    {
        try { _mdns?.Dispose(); } catch { }
        _mdns = null;
    }

    private sealed class MdnsResponder : IDisposable
    {
        private readonly string _hostFqdnLower;
        private readonly Func<string[]> _getIps;

        private UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private volatile bool _running;
        private int _joinedInterfaceCount;

        public MdnsResponder(string hostFqdn, Func<string[]> getIps)
        {
            _hostFqdnLower = NormalizeName(hostFqdn);
            _getIps = getIps;
        }

        public void Start()
        {
            if (_running) return;

            try
            {
                _udp = new UdpClient(AddressFamily.InterNetwork);
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                _udp.ExclusiveAddressUse = false;
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

                _joinedInterfaceCount = 0;
                var ips = _getIps?.Invoke() ?? Array.Empty<string>();
                foreach (var ip in ips)
                {
                    if (!IPAddress.TryParse(ip, out var local) ||
                        local.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    try
                    {
                        _udp.JoinMulticastGroup(MdnsMulticast, local);
                        _joinedInterfaceCount++;
                    }
                    catch { }
                }

                if (_joinedInterfaceCount == 0)
                {
                    _udp.JoinMulticastGroup(MdnsMulticast);
                    _joinedInterfaceCount = 1;
                }

                _cts = new CancellationTokenSource();
                _running = true;
                _loop = Task.Run(() => Loop(_cts.Token));
                Dbg.Log($"Remote mDNS: attivo host={_hostFqdnLower}, ip=[{string.Join(", ", ips)}], interfaces={_joinedInterfaceCount}");
            }
            catch (Exception ex)
            {
                try { Dbg.Warn($"Remote mDNS: avvio fallito ({ex.Message})."); } catch { }
                try { _udp?.Dispose(); } catch { }
                _udp = null;
                _cts = null;
                _joinedInterfaceCount = 0;
                _running = false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _udp?.DropMulticastGroup(MdnsMulticast); } catch { }
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            _cts = null;
            _joinedInterfaceCount = 0;
        }

        public void Dispose() => Stop();

        private async Task Loop(CancellationToken token)
        {
            if (_udp == null) return;

            while (_running && !token.IsCancellationRequested)
            {
                UdpReceiveResult rx;
                try { rx = await _udp.ReceiveAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { continue; }

                try { HandlePacket(rx); }
                catch { }
            }
        }

        private void HandlePacket(UdpReceiveResult rx)
        {
            if (_udp == null) return;
            var msg = rx.Buffer;
            if (msg.Length < 12) return;

            ushort flags = ReadU16(msg, 2);
            if ((flags & 0x8000) != 0) return; // ignora risposte, evita loop su multicast

            ushort qd = ReadU16(msg, 4);
            if (qd == 0) return;

            int off = 12;
            bool match = false;
            bool wantsUnicast = rx.RemoteEndPoint.Port != MdnsPort;

            for (int i = 0; i < qd; i++)
            {
                if (!TryReadName(msg, ref off, out var qname)) return;
                if (off + 4 > msg.Length) return;

                ushort qtype = ReadU16(msg, off); off += 2;
                ushort qclass = ReadU16(msg, off); off += 2;
                ushort qclassBase = (ushort)(qclass & 0x7FFF);
                if ((qclass & 0x8000) != 0)
                    wantsUnicast = true;

                string qn = NormalizeName(qname);
                if (qn == _hostFqdnLower &&
                    (qclassBase == 1 || qclassBase == 255) &&
                    (qtype == 1 || qtype == 28 || qtype == 255))
                {
                    match = true;
                }
            }

            if (!match) return;

            var ips = _getIps?.Invoke() ?? Array.Empty<string>();
            var ipBytes = new List<byte[]>(8);
            foreach (var ip in ips)
            {
                if (IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork)
                {
                    var b = a.GetAddressBytes();
                    if (b.Length == 4) ipBytes.Add(b);
                }
            }
            if (ipBytes.Count == 0) return;

            var resp = BuildResponse(_hostFqdnLower, ipBytes);
            if (resp == null || resp.Length == 0) return;

            var target = wantsUnicast
                ? rx.RemoteEndPoint
                : new IPEndPoint(MdnsMulticast, MdnsPort);

            _udp.Send(resp, resp.Length, target);
            Dbg.Log($"Remote mDNS: {rx.RemoteEndPoint.Address}:{rx.RemoteEndPoint.Port} -> {target.Address}:{target.Port} host={_hostFqdnLower}, ips={ipBytes.Count}", Dbg.LogLevel.Verbose);
        }

        private static byte[]? BuildResponse(string hostFqdnLower, List<byte[]> ipBytes)
        {
            var buf = new List<byte>(256);

            // ID
            buf.Add(0); buf.Add(0);
            // FLAGS response+authoritative
            buf.Add(0x84); buf.Add(0x00);
            // QDCOUNT=0. mDNS responses can be cacheable answer-only packets.
            buf.Add(0x00); buf.Add(0x00);
            // ANCOUNT = N
            WriteU16(buf, (ushort)ipBytes.Count);
            // NS=0
            buf.Add(0x00); buf.Add(0x00);
            // AR=0
            buf.Add(0x00); buf.Add(0x00);

            // Answers
            for (int i = 0; i < ipBytes.Count; i++)
            {
                WriteQName(buf, hostFqdnLower);
                WriteU16(buf, 0x0001); // A
                WriteU16(buf, 0x8001); // IN + cache-flush
                WriteU32(buf, 120);
                WriteU16(buf, 4);
                buf.AddRange(ipBytes[i]);
            }

            return buf.ToArray();
        }

        private static string NormalizeName(string name)
        {
            name = name.Trim();
            if (name.EndsWith(".", StringComparison.Ordinal)) name = name[..^1];
            return name.ToLowerInvariant();
        }

        private static ushort ReadU16(byte[] b, int off)
        {
            if (off + 1 >= b.Length) return 0;
            return (ushort)((b[off] << 8) | b[off + 1]);
        }

        private static void WriteU16(List<byte> buf, ushort v)
        {
            buf.Add((byte)((v >> 8) & 0xFF));
            buf.Add((byte)(v & 0xFF));
        }

        private static void WriteU32(List<byte> buf, uint v)
        {
            buf.Add((byte)((v >> 24) & 0xFF));
            buf.Add((byte)((v >> 16) & 0xFF));
            buf.Add((byte)((v >> 8) & 0xFF));
            buf.Add((byte)(v & 0xFF));
        }

        private static void WriteQName(List<byte> buf, string fqdnLower)
        {
            var parts = fqdnLower.Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var bytes = Encoding.ASCII.GetBytes(p);
                if (bytes.Length == 0 || bytes.Length > 63) continue;
                buf.Add((byte)bytes.Length);
                buf.AddRange(bytes);
            }
            buf.Add(0);
        }

        private static bool TryReadName(byte[] msg, ref int offset, out string name)
        {
            name = "";
            var sb = new StringBuilder(64);

            int orig = offset;
            bool jumped = false;
            int guard = 0;

            while (offset < msg.Length)
            {
                if (guard++ > 255) return false;

                byte len = msg[offset++];

                if (len == 0)
                    break;

                if ((len & 0xC0) == 0xC0)
                {
                    if (offset >= msg.Length) return false;
                    int ptr = ((len & 0x3F) << 8) | msg[offset++];
                    if (ptr < 0 || ptr >= msg.Length) return false;

                    if (!jumped)
                    {
                        orig = offset;
                        jumped = true;
                    }

                    offset = ptr;
                    continue;
                }

                if (offset + len > msg.Length) return false;

                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.ASCII.GetString(msg, offset, len));
                offset += len;
            }

            if (jumped) offset = orig;

            name = sb.ToString();
            return name.Length > 0;
        }
    }

    // ====================== util rete ======================
    public static string[] LocalIPv4List()
    {
        var scored = new List<(string Address, int Score)>();

        static bool Is172Private(string address)
        {
            var parts = address.Split('.');
            return parts.Length >= 2 &&
                   parts[0] == "172" &&
                   int.TryParse(parts[1], out int b) &&
                   b >= 16 && b <= 31;
        }

        static bool IsPrivateLan(string address)
        {
            return address.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase) ||
                   address.StartsWith("10.", StringComparison.OrdinalIgnoreCase) ||
                   Is172Private(address);
        }

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                string nicText = ((ni.Name ?? string.Empty) + " " + (ni.Description ?? string.Empty)).ToLowerInvariant();
                bool virtualNic = nicText.Contains("virtual") || nicText.Contains("vmware") ||
                                  nicText.Contains("hyper-v") || nicText.Contains("vbox") ||
                                  nicText.Contains("virtualbox") || nicText.Contains("docker") ||
                                  nicText.Contains("wsl") || nicText.Contains("vpn") ||
                                  nicText.Contains("tailscale") || nicText.Contains("zerotier") ||
                                  nicText.Contains("tap") || nicText.Contains("tun");

                int nicScore = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 300 :
                               ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 260 : 120;
                if (virtualNic) nicScore -= 220;

                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    string address = ua.Address.ToString();
                    if (address.StartsWith("127.", StringComparison.OrdinalIgnoreCase) ||
                        address.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase) ||
                        address == "0.0.0.0")
                        continue;

                    int score = nicScore;
                    if (address.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase)) score += 90;
                    else if (address.StartsWith("10.", StringComparison.OrdinalIgnoreCase)) score += 70;
                    else if (Is172Private(address)) score += 60;
                    else if (IsPrivateLan(address)) score += 40;

                    scored.Add((address, score));
                }
            }
            catch { }
        }

        return scored
            .GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .Select(x => x.Address)
            .ToArray();
    }
}

// ====================== stato player esposto su /api/state ======================
internal sealed class RemoteState
{
    public string? Subtitle { get; set; }
    public string? Title { get; set; }
    public bool HasPlayback { get; set; }
    public string? ArtPath { get; set; }
    public double Position { get; set; }
    public double Duration { get; set; }
    public bool OutputHdr { get; set; }
    public bool Is3D { get; set; }
    public string Renderer { get; set; } = "Auto";
    public string RendererSelection { get; set; } = "Auto";
    public string DefaultRenderer { get; set; } = "Auto";
    public string HdrMode { get; set; } = "auto";
    public bool HdrAvailable { get; set; }
    public bool RtxHdrAvailable { get; set; }
    public string StereoMode { get; set; } = "off";
    public bool StereoAvailable { get; set; } = true;
    public bool Upscaling { get; set; }
    public bool UpscalingAvailable { get; set; }
    public string UpscalingBackend { get; set; } = "off";
    public string UpscalingPreset { get; set; } = "0";
    public bool MadVrAvailable { get; set; }
    public bool MpcvrNvidiaUpscalingAvailable { get; set; }
    public bool Bitstream { get; set; }
    public bool Paused { get; set; }
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public string Accent { get; set; } = "#0084ff";
    public string AccentSoft { get; set; } = "#0054be";
    public string Panel { get; set; } = "#030a12";
    public string Card { get; set; } = "#07121f";
    public string Nav { get; set; } = "#050f1a";
    public string Text { get; set; } = "#ffffff";
    public string MutedText { get; set; } = "#8ea0b4";
    public bool LightTheme { get; set; }
    public string Language { get; set; } = global::CinecorePlayer2025.Utilities.AppLanguage.SystemDefault;
    public bool Spotlight { get; set; }
}


