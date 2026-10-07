#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CinecorePlayer2025.Utilities;

internal enum AmplifierProtocol { DenonMarantz, YamahaMusicCast, OnkyoIntegraPioneer, Upnp, Nad, Anthem }
internal sealed record AmplifierDevice(string Name, string Address, AmplifierProtocol Protocol, int Port = 0, string? AudioEndpointId = null)
{
    public override string ToString() => Name;
}
internal sealed record AmplifierStatus(string Volume, bool Muted, float Level = 0f);

// All commands address the main zone. No automatic power/input changes; the only automatic
// volume change is the per-film level (PlayerForm.FilmVolume), which can only lower it.
// Network control is independent of the PCM mixer, including during passthrough.
internal sealed class AmplifierControl : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AmplifierDevice _device;
    private Uri? _upnpControl;
    private string _upnpService = "";
    private int _upnpMin, _upnpMax = 100, _upnpStep = 1;
    private bool _disposed;
    private static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "amplifier.json");
    public AmplifierControl(AmplifierDevice device)
    {
        _device = device;
        if (!Enum.IsDefined(device.Protocol)) throw new ArgumentException("Protocollo non valido.");
        if (device.Port < 0 || device.Port > 65535) throw new ArgumentException("Porta non valida.");
        if (device.Protocol == AmplifierProtocol.Upnp) ValidateHttpUri(device.Address);
        else if (Uri.CheckHostName(device.Address.Trim()) == UriHostNameType.Unknown) throw new ArgumentException("Inserisci un indirizzo IP o un nome host valido.");
    }
    public static AmplifierDevice? Load()
    {
        try
        {
            var device = JsonSerializer.Deserialize<AmplifierDevice>(File.ReadAllText(ConfigPath));
            if (device == null) return null;
            using var validation = new AmplifierControl(device);
            return device;
        }
        catch { return null; }
    }
    public static void Save(AmplifierDevice device)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        string temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(device));
        File.Move(temp, ConfigPath, true);
    }
    private int Port => _device.Port > 0 ? _device.Port : _device.Protocol switch { AmplifierProtocol.DenonMarantz or AmplifierProtocol.Nad => 23, AmplifierProtocol.Anthem => 14999, AmplifierProtocol.OnkyoIntegraPioneer => 60128, _ => 80 };
    public Task<AmplifierStatus> ReadAsync(CancellationToken token = default) => ExecuteAsync(0, null, token);
    public Task<AmplifierStatus> StepAsync(int direction, CancellationToken token = default) => ExecuteAsync(Math.Sign(direction), null, token);
    public Task<AmplifierStatus> MuteAsync(bool muted, CancellationToken token = default) => ExecuteAsync(0, muted, token);
    public Task<AmplifierStatus> SetVolumeAsync(float level, CancellationToken token = default) => ExecuteAsync(0, null, token, Math.Clamp(level, 0f, 1f));
    private async Task<AmplifierStatus> ExecuteAsync(int step, bool? mute, CancellationToken token, float? level = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            token = deadline.Token;
            switch (_device.Protocol)
            {
                case AmplifierProtocol.YamahaMusicCast:
                    if (level.HasValue)
                    {
                        var limits = await YamahaAsync("main/getStatus", token).ConfigureAwait(false);
                        int maximum = limits.GetProperty("max_volume").GetInt32();
                        await YamahaAsync("main/setVolume?volume=" + (int)Math.Round(level.Value * maximum), token).ConfigureAwait(false);
                    }
                    if (step != 0) await YamahaAsync("main/setVolume?volume=" + (step > 0 ? "up" : "down"), token).ConfigureAwait(false);
                    if (mute.HasValue) await YamahaAsync("main/setMute?enable=" + (mute.Value ? "true" : "false"), token).ConfigureAwait(false);
                    var status = await YamahaAsync("main/getStatus", token).ConfigureAwait(false);
                    return new(status.GetProperty("volume").ToString(), status.GetProperty("mute").GetBoolean(), Math.Clamp(status.GetProperty("volume").GetSingle() / Math.Max(1, status.GetProperty("max_volume").GetInt32()), 0, 1));
                case AmplifierProtocol.Upnp:
                    await EnsureUpnpAsync(token).ConfigureAwait(false);
                    if (level.HasValue)
                    {
                        int desired = _upnpMin + (int)Math.Round(level.Value * (_upnpMax - _upnpMin) / _upnpStep) * _upnpStep;
                        await SoapAsync("SetVolume", new XElement("DesiredVolume", Math.Clamp(desired, _upnpMin, _upnpMax)), token).ConfigureAwait(false);
                    }
                    if (step != 0)
                    {
                        var current = await SoapAsync("GetVolume", null, token).ConfigureAwait(false);
                        int volume = int.Parse(Value(current, "CurrentVolume"), CultureInfo.InvariantCulture);
                        await SoapAsync("SetVolume", new XElement("DesiredVolume", Math.Clamp(volume + step * _upnpStep, _upnpMin, _upnpMax)), token).ConfigureAwait(false);
                    }
                    if (mute.HasValue) await SoapAsync("SetMute", new XElement("DesiredMute", mute.Value ? 1 : 0), token).ConfigureAwait(false);
                    var vol = await SoapAsync("GetVolume", null, token).ConfigureAwait(false);
                    var muted = await SoapAsync("GetMute", null, token).ConfigureAwait(false);
                    return new(Value(vol, "CurrentVolume"), Value(muted, "CurrentMute") is "1" or "true", Math.Clamp((float.Parse(Value(vol, "CurrentVolume"), CultureInfo.InvariantCulture) - _upnpMin) / Math.Max(1, _upnpMax - _upnpMin), 0, 1));
                default:
                    return await TcpAsync(step, mute, token, level).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }
    private async Task<JsonElement> YamahaAsync(string action, CancellationToken token)
    {
        var url = new UriBuilder("http", _device.Address, Port, "/YamahaExtendedControl/v1/" + action.Split('?')[0]);
        if (action.Contains('?')) url.Query = action[(action.IndexOf('?') + 1)..];
        using var response = await _http.GetAsync(url.Uri, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (json.RootElement.GetProperty("response_code").GetInt32() != 0) throw new IOException("Il dispositivo Yamaha ha rifiutato il comando.");
        return json.RootElement.Clone();
    }
    internal static byte[] EiscpPacket(string command)
    {
        byte[] payload = Encoding.ASCII.GetBytes("!1" + command + "\r");
        byte[] packet = new byte[16 + payload.Length];
        Encoding.ASCII.GetBytes("ISCP").CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), 16);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), payload.Length);
        packet[12] = 1;
        payload.CopyTo(packet, 16);
        return packet;
    }
    private async Task<AmplifierStatus> TcpAsync(int step, bool? mute, CancellationToken token, float? desiredLevel = null)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(_device.Address, Port, token).ConfigureAwait(false);
        using var stream = client.GetStream();
        bool eiscp = _device.Protocol == AmplifierProtocol.OnkyoIntegraPioneer;
        bool nad = _device.Protocol == AmplifierProtocol.Nad;
        bool anthem = _device.Protocol == AmplifierProtocol.Anthem;
        async Task Send(string command)
        {
            byte[] bytes = eiscp ? EiscpPacket(command) : Encoding.ASCII.GetBytes((nad ? "\r" : "") + command + (anthem ? ";" : "\r"));
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        }
        if (desiredLevel.HasValue)
        {
            // Absolute sliders stop at reference level (0 dB); relative buttons
            // retain the receiver's own step and maximum-volume configuration.
            float value = desiredLevel.Value;
            int halfSteps = (int)Math.Round(value * 160);
            string denon = halfSteps % 2 == 0 ? (halfSteps / 2).ToString("D2", CultureInfo.InvariantCulture) : (halfSteps * 5).ToString("D3", CultureInfo.InvariantCulture);
            await Send(nad ? "Main.Volume=" + (-80 + value * 80).ToString("0.0", CultureInfo.InvariantCulture)
                : anthem ? "Z1VOL" + (-90 + value * 90).ToString("0.0", CultureInfo.InvariantCulture)
                : eiscp ? "MVL" + ((int)Math.Round(value * 80)).ToString("X2", CultureInfo.InvariantCulture) : "MV" + denon);
        }
        if (step != 0)
        {
            await Send(nad ? (step > 0 ? "Main.Volume+" : "Main.Volume-") : anthem ? (step > 0 ? "Z1VUP" : "Z1VDN") : (eiscp ? "MVL" : "MV") + (step > 0 ? "UP" : "DOWN"));
            await Task.Delay(80, token).ConfigureAwait(false);
        }
        if (mute.HasValue)
        {
            await Send(nad ? "Main.Mute=" + (mute.Value ? "On" : "Off") : anthem ? "Z1MUT" + (mute.Value ? "1" : "0") : eiscp ? (mute.Value ? "AMT01" : "AMT00") : (mute.Value ? "MUON" : "MUOFF"));
            await Task.Delay(80, token).ConfigureAwait(false);
        }
        await Send(nad ? "Main.Volume?" : anthem ? "Z1VOL?" : eiscp ? "MVLQSTN" : "MV?");
        await Task.Delay(60, token).ConfigureAwait(false);
        await Send(nad ? "Main.Mute?" : anthem ? "Z1MUT?" : eiscp ? "AMTQSTN" : "MU?");
        string? volume = null; bool? muted = null;
        float normalized = 0;
        var text = new StringBuilder(); var buffer = new byte[1024];
        while (volume == null || muted == null)
        {
            string message;
            if (eiscp)
            {
                byte[] header = new byte[16];
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                int headerSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
                int size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
                if (Encoding.ASCII.GetString(header, 0, 4) != "ISCP" || headerSize != 16 || size < 3 || size > 4096) throw new IOException("Risposta eISCP non valida.");
                byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
                message = Encoding.ASCII.GetString(body).TrimEnd('\r', '\n', '\x1a', '\0');
                if (!message.StartsWith("!1")) continue;
                message = message[2..];
                if (message.StartsWith("MVL") && int.TryParse(message.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int level)) { volume = level.ToString(CultureInfo.InvariantCulture); normalized = level / 80f; }
                if (message is "AMT00" or "AMT01") muted = message == "AMT01";
            }
            else
            {
                int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                text.Append(Encoding.ASCII.GetString(buffer, 0, count));
                if (text.Length > 16384) throw new IOException("Risposta troppo lunga.");
                string pending = text.ToString().Replace('\n', '\r'); int end;
                while ((end = pending.IndexOf(anthem ? ';' : '\r')) >= 0)
                {
                    message = pending[..end].Trim(); pending = pending[(end + 1)..];
                    if (nad || anthem)
                    {
                        string prefix = nad ? "Main.Volume=" : "Z1VOL";
                        if (message.StartsWith(prefix) && double.TryParse(message.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double db) && double.IsFinite(db)) { volume = db.ToString("0.#", CultureInfo.InvariantCulture) + " dB"; normalized = (float)((db + (nad ? 80 : 90)) / (nad ? 80 : 90)); }
                        if (nad && message is "Main.Mute=On" or "Main.Mute=Off") muted = message.EndsWith("=On");
                        if (anthem && message is "Z1MUT0" or "Z1MUT1") muted = message == "Z1MUT1";
                        continue;
                    }
                    if (message.StartsWith("MV") && message.Length is 4 or 5 && int.TryParse(message.AsSpan(2), out int level))
                        { volume = message == "MV00" ? "MIN" : ((message.Length == 5 ? level / 10.0 : level) - 80).ToString("0.#", CultureInfo.InvariantCulture) + " dB"; normalized = (message.Length == 5 ? level / 10f : level) / 80f; }
                    if (message is "MUON" or "MUOFF") muted = message == "MUON";
                }
                text.Clear().Append(pending);
            }
        }
        return new(volume, muted.Value, Math.Clamp(normalized, 0f, 1f));
    }
    private static Uri ValidateHttpUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Inserisci l'URL HTTP della descrizione UPnP.");
        return uri;
    }
    private async Task EnsureUpnpAsync(CancellationToken token)
    {
        if (_upnpControl != null) return;
        var location = ValidateHttpUri(_device.Address);
        var xml = XDocument.Parse(await _http.GetStringAsync(location, token).ConfigureAwait(false));
        var service = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "service" && Value(e, "serviceType").StartsWith("urn:schemas-upnp-org:service:RenderingControl:"))
            ?? throw new IOException("Il dispositivo non espone RenderingControl UPnP.");
        var baseText = xml.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;
        var baseUri = string.IsNullOrWhiteSpace(baseText) ? location : ValidateHttpUri(baseText);
        string serviceType = Value(service, "serviceType");
        var control = new Uri(baseUri, Value(service, "controlURL"));
        ValidateHttpUri(control.ToString());
        var scpd = XDocument.Parse(await _http.GetStringAsync(new Uri(baseUri, Value(service, "SCPDURL")), token).ConfigureAwait(false));
        foreach (string action in new[] { "GetVolume", "SetVolume", "GetMute", "SetMute" })
            if (!scpd.Descendants().Any(e => e.Name.LocalName == "action" && Value(e, "name") == action)) throw new IOException("Controllo volume/mute non disponibile su questo dispositivo.");
        var state = scpd.Descendants().FirstOrDefault(e => e.Name.LocalName == "stateVariable" && Value(e, "name") == "Volume");
        if (state != null)
        {
            if (int.TryParse(Value(state, "minimum"), out int min)) _upnpMin = min;
            if (int.TryParse(Value(state, "maximum"), out int max)) _upnpMax = max;
            if (int.TryParse(Value(state, "step"), out int step)) _upnpStep = Math.Max(1, step);
        }
        if (_upnpMax < _upnpMin) throw new IOException("Intervallo volume UPnP non valido.");
        _upnpService = serviceType; _upnpControl = control;
    }
    private static string Value(XContainer xml, string name) => xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
    private async Task<XDocument> SoapAsync(string action, XElement? parameter, CancellationToken token)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var body = new XElement((XNamespace)_upnpService + action, new XElement("InstanceID", 0), new XElement("Channel", "Master"));
        if (parameter != null) body.Add(parameter);
        var envelope = new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", soap), new XAttribute(soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"), new XElement(soap + "Body", body));
        using var request = new HttpRequestMessage(HttpMethod.Post, _upnpControl) { Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPACTION", "\"" + _upnpService + "#" + action + "\"");
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = XDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (result.Descendants().Any(e => e.Name.LocalName == "Fault")) throw new IOException("Il dispositivo ha rifiutato il comando UPnP.");
        return result;
    }
    public static async Task<List<AmplifierDevice>> DiscoverAsync(CancellationToken token)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(4200);
        var clients = new List<UdpClient>();
        try
        {
            var localAddresses = new HashSet<IPAddress>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                        !adapter.SupportsMulticast)
                        continue;
                    foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                            localAddresses.Add(address.Address);
                }
                catch { }
            }

            foreach (IPAddress localAddress in localAddresses)
            {
                try
                {
                    var client = new UdpClient(new IPEndPoint(localAddress, 0)) { EnableBroadcast = true };
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
                    clients.Add(client);
                }
                catch { }
            }
            if (clients.Count == 0)
            {
                var fallback = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                fallback.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                clients.Add(fallback);
            }

            var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            string[] targets =
            {
                "urn:schemas-upnp-org:device:MediaRenderer:1",
                "urn:schemas-upnp-org:service:RenderingControl:1",
                "upnp:rootdevice",
                "ssdp:all"
            };
            foreach (UdpClient client in clients)
                foreach (string target in targets)
                {
                    byte[] request = Encoding.ASCII.GetBytes(
                        "M-SEARCH * HTTP/1.1\r\n" +
                        "HOST: 239.255.255.250:1900\r\n" +
                        "MAN: \"ssdp:discover\"\r\n" +
                        "MX: 3\r\n" +
                        "ST: " + target + "\r\n\r\n");
                    try { await client.SendAsync(request, request.Length, endpoint).ConfigureAwait(false); } catch { }
                }

            async Task ReceiveAsync(UdpClient client)
            {
                while (!timeout.IsCancellationRequested)
                {
                    try
                    {
                        UdpReceiveResult reply = await client.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(700), timeout.Token).ConfigureAwait(false);
                        string response = Encoding.ASCII.GetString(reply.Buffer);
                        foreach (string line in response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int colon = line.IndexOf(':');
                            if (colon <= 0 || !string.Equals(line[..colon].Trim(), "LOCATION", StringComparison.OrdinalIgnoreCase))
                                continue;
                            string location = line[(colon + 1)..].Trim();
                            if (Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp)
                                lock (locations) locations.Add(uri.AbsoluteUri);
                        }
                    }
                    catch (TimeoutException) { }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }

            await Task.WhenAll(clients.Select(ReceiveAsync)).ConfigureAwait(false);
        }
        finally
        {
            foreach (UdpClient client in clients)
                try { client.Dispose(); } catch { }
        }

        token.ThrowIfCancellationRequested();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(3) };
        var found = await Task.WhenAll(locations.Take(40).Select(async address =>
        {
            try
            {
                var uri = ValidateHttpUri(address);
                var xml = XDocument.Parse(await http.GetStringAsync(uri, token).ConfigureAwait(false));
                string friendlyName = Value(xml, "friendlyName");
                string identity = string.Join(" ", Value(xml, "manufacturer"), Value(xml, "manufacturerURL"),
                    Value(xml, "modelName"), Value(xml, "modelDescription"), friendlyName).ToLowerInvariant();
                var protocol = identity.Contains("denon") || identity.Contains("marantz") ? AmplifierProtocol.DenonMarantz
                    : identity.Contains("yamaha") || identity.Contains("musiccast") ? AmplifierProtocol.YamahaMusicCast
                    : identity.Contains("onkyo") || identity.Contains("integra") || identity.Contains("pioneer") ? AmplifierProtocol.OnkyoIntegraPioneer
                    : AmplifierProtocol.Upnp;
                bool rendering = xml.Descendants().Any(e => e.Name.LocalName == "serviceType" && e.Value.Contains(":RenderingControl:"));
                if (protocol == AmplifierProtocol.Upnp && !rendering) return null;
                string name = string.IsNullOrWhiteSpace(friendlyName) ? Value(xml, "modelName") : friendlyName;
                if (string.IsNullOrWhiteSpace(name)) name = uri.Host;
                return new AmplifierDevice(name, protocol == AmplifierProtocol.Upnp ? address : uri.Host, protocol);
            }
            catch { return null; }
        })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return found.OfType<AmplifierDevice>().DistinctBy(d => d.Address).OrderBy(d => d.Name).ToList();
    }
    public void Dispose() { _disposed = true; _http.Dispose(); }
}
