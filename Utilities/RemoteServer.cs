// FILE: RemoteServer.cs — v9 TCP + fullscreen remote UI + PairingRequested + trusted.json + mDNS + FrontDoor :80 proxy
// - Server TCP Http-like (TcpListener) senza URLACL.
// - UI telecomando = fullscreen nero.
// - PairingRequested(pin) quando serve mostrare PIN sul player.
// - trusted.json persistente in %AppData%\CinecorePlayer2025
// - Persistenza dispositivo ROBUSTA: cookie HttpOnly + localStorage token + Authorization Bearer
// - FrontDoor best-effort su porta 80: reverse proxy TCP verso _port (URL pulito, niente :porta)
// - mDNS best-effort: cinecore-remote.local -> IP del player
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

internal sealed class RemoteServer : IDisposable
{
    // ====================== DNS / mDNS ======================
    private const string MdnsHostFqdn = "cinecore-remote.local";
    private static readonly IPAddress MdnsMulticast = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    // ====================== campi core ======================
    private readonly int _port;
    private readonly string _pin;
    private readonly Func<RemoteState> _getState;
    private readonly Action<string, Dictionary<string, string>> _handle;

    private TcpListener? _tcp;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private volatile bool _running;

    // ====================== FrontDoor :80 proxy -> _port ======================
    private TcpListener? _tcpFront;
    private Task? _frontLoop;
    private volatile bool _frontRunning;

    // ====================== mDNS cinecore-remote.local ======================
    private MdnsResponder? _mdns;

    // ====================== trusted store ======================
    private readonly string _rootDir;
    private readonly string _storePath;
    private readonly object _lock = new();

    public event Action<string>? Paired;
    public event Action<string>? PairingRequested;

    public int TrustedCount { get { lock (_lock) return _trusted.Count; } }
    public string CurrentPin => _pin;

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
        public List<(string Key, string Val)> ExtraHeaders = new();
    }

    // ====================== ctor ======================
    public RemoteServer(int port,
                        string? pin,
                        Func<RemoteState> getState,
                        Action<string, Dictionary<string, string>> handleCommand)
    {
        _port = port;
        _pin = string.IsNullOrWhiteSpace(pin) ? MakePin() : pin.Trim();
        _getState = getState;
        _handle = handleCommand;

        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CinecorePlayer2025");
        Directory.CreateDirectory(_rootDir);

        _storePath = Path.Combine(_rootDir, "trusted.json");
        LoadTrusted();
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

    // ====================== start/stop ======================
    public void Start()
    {
        if (_running) return;

        _tcp = new TcpListener(IPAddress.Any, _port);
        try { _tcp.Start(); }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Impossibile avviare il server remoto sulla porta {_port}. " +
                $"Motivi tipici: firewall, porta occupata. Dettagli: {ex.Message}", ex);
        }

        _cts = new CancellationTokenSource();
        _running = true;
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));

        // Best-effort: front door su :80 (reverse proxy) per URL pulito (cinecore-remote.local senza :porta)
        StartFrontDoor80();

        // Best-effort: mDNS cinecore-remote.local
        StartMdns();
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
        byte[] tmp = new byte[4096];
        var buf = new List<byte>(8192);

        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n;
            try { n = await ns.ReadAsync(tmp, 0, tmp.Length); }
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
                int n = await ns.ReadAsync(bodyBytes, copied, need);
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

        string remoteIp = ((IPEndPoint?)cli.Client.RemoteEndPoint)?.Address.ToString() ?? "?";
        if (headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrWhiteSpace(xff))
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
        byte[] bodyBytes = Encoding.UTF8.GetBytes(resp.BodyText ?? "");

        await w.WriteLineAsync($"HTTP/1.1 {resp.StatusCode} {reason}");
        await w.WriteLineAsync("Access-Control-Allow-Origin: *");
        await w.WriteLineAsync("Access-Control-Allow-Headers: Content-Type, Authorization, X-Device-Name, X-Device-Id");
        await w.WriteLineAsync("Access-Control-Allow-Methods: GET,POST,DELETE,OPTIONS");
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
        401 => "Unauthorized",
        404 => "Not Found",
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
            File.WriteAllText(_storePath, json);
        }
        catch { }
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
                    tokObj.LastSeen = DateTime.UtcNow;
                    tokObj.LastIp = req.RemoteIp;

                    // best-effort MAC update
                    string? mac = TryGetMac(req.RemoteIp);
                    if (!string.IsNullOrWhiteSpace(mac))
                        tokObj.Mac = mac;

                    if (!string.IsNullOrWhiteSpace(deviceId))
                        tokObj.DeviceId = deviceId;

                    SaveTrusted();
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
                    tokObj.LastSeen = DateTime.UtcNow;
                    tokObj.LastIp = req.RemoteIp;

                    string? mac = TryGetMac(req.RemoteIp);
                    if (!string.IsNullOrWhiteSpace(mac))
                        tokObj.Mac = mac;

                    SaveTrusted();
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

        // UI sempre servita (le API restano protette)
        if (req.Path == "/remote" && req.Method == "GET")
        {
            if (IsAuthed(req, out var tokRemote) && tokRemote != null)
            {
                var respRemote = HtmlResp(RemoteHtml());
                AddAuthCookie(respRemote, tokRemote.Token);
                AddDeviceCookie(respRemote, tokRemote.DeviceId);
                return respRemote;
            }

            return HtmlResp(RemoteHtml());
        }

        if (req.Path == "/" || req.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            var qdict = ParseQuery(string.IsNullOrEmpty(req.Query) ? null : "?" + req.Query);

            // pairing via ?pin=
            if (qdict.TryGetValue("pin", out var pinQ) && pinQ == _pin)
            {
                req.Headers.TryGetValue("X-Device-Name", out var devName);
                if (!req.Headers.TryGetValue("User-Agent", out var ua)) ua = null;

                var deviceId = ReadDeviceId(req);
                var newTok = CreateTrustedToken(devName ?? ua, req.RemoteIp, deviceId);
                var respOk = HtmlResp(RemoteHtml());
                AddAuthCookie(respOk, newTok.Token);
                AddDeviceCookie(respOk, newTok.DeviceId ?? deviceId);
                return respOk;
            }

            // già autenticato -> telecomando
            if (IsAuthed(req, out var tokRoot) && tokRoot != null)
            {
                var respRoot = HtmlResp(RemoteHtml());
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
            if (pinBody == _pin)
            {
                var nameBody = ReadJsonPropFromBody(req.Body, "name");
                var deviceIdBody = ReadJsonPropFromBody(req.Body, "deviceId");
                var deviceId = string.IsNullOrWhiteSpace(deviceIdBody) ? ReadDeviceId(req) : deviceIdBody;
                if (!req.Headers.TryGetValue("User-Agent", out var ua)) ua = null;

                var newTok = CreateTrustedToken(
                    string.IsNullOrWhiteSpace(nameBody) ? ua : nameBody,
                    req.RemoteIp,
                    deviceId);

                var okResp = JsonResp(new { ok = true, token = newTok.Token }, 200);
                AddAuthCookie(okResp, newTok.Token);
                AddDeviceCookie(okResp, newTok.DeviceId ?? deviceId);
                return okResp;
            }

            RaisePairingRequested();
            return JsonResp(new { ok = false, error = "bad pin", pair = true, pin = _pin }, 401);
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
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
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
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
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
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
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
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
            }

            var st = _getState();
            return JsonResp(st, 200);
        }

        // GET /api/cmd
        if (req.Path == "/api/cmd")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
            }

            var q = ParseQuery(string.IsNullOrEmpty(req.Query) ? null : "?" + req.Query);
            string cmd = q.TryGetValue("cmd", out var c) ? c : "";
            _handle(cmd, q);

            return JsonResp(new { ok = true }, 200);
        }

        return JsonResp(new { ok = false, error = "not found" }, 404);
    }

    // ====================== pagina PIN ======================
    private static string PinHtml() => @"<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no'>
<title>Cinecore Remote – PIN</title>
<style>
:root{
  --bg:#000;
  --txt:#fff;
  --dim:#8a8a8a;
  --border:#2a2a2a;
  --danger:#ff4f6a;
  --btn:#1a1a1a;
  --btnborder:#3a3a3a;
  --font:system-ui,'Segoe UI',Roboto,Arial,sans-serif;
}
*{box-sizing:border-box;margin:0;padding:0;-webkit-tap-highlight-color:transparent}
body{
  background:#000;
  color:var(--txt);
  font-family:var(--font);
  min-height:100vh;
  padding:24px 16px 32px;
  display:flex;
  align-items:flex-start;
  justify-content:center;
}
.wrap{
  width:min(400px,100%);
  display:flex;
  flex-direction:column;
  gap:20px;
}
.hdr{
  text-align:center;
}
.logo-row{
  display:flex;
  justify-content:center;
  align-items:center;
  gap:8px;
  font-size:14px;
  font-weight:600;
  color:#fff;
  text-transform:uppercase;
  letter-spacing:.05em;
}
.subtitle{
  font-size:13px;
  color:var(--dim);
  line-height:1.4;
  margin-top:4px;
}
.pinbox{
  border-top:1px solid var(--border);
  padding-top:20px;
  display:flex;
  flex-direction:column;
  gap:14px;
}
input{
  width:100%;
  font-size:24px;
  padding:14px 12px;
  background:#0c0c0c;
  color:#fff;
  border-radius:10px;
  border:1px solid var(--border);
  text-align:center;
  letter-spacing:.35em;
  font-weight:600;
  outline:none;
}
.keypad{
  display:grid;
  grid-template-columns:repeat(3,1fr);
  gap:10px;
}
.keypad button,
#go{
  appearance:none;
  background:var(--btn);
  border:1px solid var(--btnborder);
  border-radius:10px;
  color:#fff;
  font-size:18px;
  font-weight:600;
  padding:14px 0;
}
#go{
  width:100%;
  font-size:15px;
  text-transform:uppercase;
  letter-spacing:.04em;
}
.keypad button:active,#go:active{ transform:translateY(1px); }
.err{
  min-height:20px;
  font-size:13px;
  font-weight:500;
  color:var(--danger);
  text-align:center;
}
.note{
  font-size:12px;
  text-align:center;
  color:var(--dim);
  line-height:1.4;
}
</style>
</head>
<body>
<div class='wrap'>
  <div class='hdr'>
    <div class='logo-row'>
      <span>CinecorePlayer2025</span>
      <span>•</span>
      <span>Remote Pair</span>
    </div>
    <div class='subtitle'>Guarda il PIN sul player e inseriscilo qui. Dopo l'abbinamento questo dispositivo resta autorizzato.</div>
  </div>

  <div class='pinbox'>
    <input id='pin' inputmode='numeric' pattern='[0-9]*' maxlength='8' autofocus placeholder='••••••'>
    <div class='keypad'>
      <button data-d='1'>1</button><button data-d='2'>2</button><button data-d='3'>3</button>
      <button data-d='4'>4</button><button data-d='5'>5</button><button data-d='6'>6</button>
      <button data-d='7'>7</button><button data-d='8'>8</button><button data-d='9'>9</button>
      <button data-d='clr'>C</button><button data-d='0'>0</button><button data-d='del'>⌫</button>
    </div>
    <button id='go'>Abbina</button>
    <div class='err' id='err'></div>
    <div class='note'>Se esci senza abbinarlo il player continua a mostrare il PIN.</div>
  </div>
</div>

<script>
const pin=document.getElementById('pin');
const err=document.getElementById('err');

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
  }catch(_){
    return '';
  }
}
const deviceId = ensureDeviceId();

// Se ho già un token salvato, salto il PIN (persistenza dispositivo)
try{
  const t = localStorage.getItem('ccp_token');
  if(t && t.length>10){
    location.href='/remote';
  }
}catch(_){}

document.querySelectorAll('.keypad button').forEach(b=>{
  b.onclick=()=>{
    const d=b.dataset.d;
    if(d==='clr') pin.value='';
    else if(d==='del') pin.value=pin.value.slice(0,-1);
    else pin.value+=d;
    pin.focus();
  };
});

async function auth(p, name){
  err.textContent='';
  const r=await fetch('/api/auth',{
    method:'POST',
    credentials:'same-origin',
    headers:{
      'Content-Type':'application/json',
      'X-Device-Id': deviceId
    },
    body:JSON.stringify({pin:p, name:name||navigator.userAgent, deviceId: deviceId})
  });

  if(r.ok){
    try{
      const j = await r.json();
      if(j && j.token){
        localStorage.setItem('ccp_token', j.token);
      }
    }catch(_){}
    location.href='/remote';
  }else{
    err.textContent='PIN errato';
  }
}

document.getElementById('go').onclick=()=>auth(pin.value.trim(), navigator.userAgent);
pin.addEventListener('keydown',e=>{
  if(e.key==='Enter') document.getElementById('go').click();
});
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

        string icoSettings = SvgDataUriFromFile("settings.svg",
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><circle cx='12' cy='12' r='3' stroke='#fff' stroke-width='2' fill='none'/><path d='M12 2v3M12 19v3M2 12h3M19 12h3' stroke='#fff' stroke-width='2' stroke-linecap='round'/></svg>");

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
</style>
</head>
<body>

<div class='stage'>
  <div class='remote'>

    <!-- TOP -->
    <div class='topbar'>
      <div class='topspacer'></div>
      <div class='brandcenter'>
        <img src='" + logoDataUri + @"' alt='logo'>
      </div>
      <button class='pwroff' onclick='cmd(""poweroff"")' title='Power off'>
        <img class='icoimg' src='" + icoPowerOff + @"' alt='Power off'>
      </button>
    </div>

    <!-- STATO -->
    <div class='panel'>
      <div class='statepill'>
        <span class='chip' id='hdrChip'>SDR</span>
        <span class='chip' id='audioChip' title='Se BITSTREAM, volume fisso 100%'>PCM</span>
        <span class='chip' id='dimChip'>2D</span>
      </div>
      <div class='nowtitle' id='title'></div>
    </div>

    <!-- MODI -->
    <div class='panel'>
      <div class='grid2'>
        <button class='btn' id='hdrBtn' onclick='cmd(""hdr"")' title='HDR / SDR'><img class='icoimg' src='" + icoHdr + @"' alt='HDR'></button>
        <button class='btn' id='stereoBtn' onclick='cmd(""stereo"")' title='3D / 2D'><img class='icoimg' src='" + ico3d + @"' alt='3D'></button>
      </div>
    </div>

    <!-- PLAYBACK -->
    <div class='panel'>
      <div class='grid3' style='margin-bottom:10px;'>
        <button class='btn' onclick='cmd(""prev"")' title='Cap -'><img class='icoimg' src='" + icoPrev + @"' alt='Prev'></button>
        <button class='btn main' id='playBtn' onclick='cmd(""play"")' title='Play'><img class='icoimg' src='" + icoPlay + @"' alt='Play'></button>
        <button class='btn' onclick='cmd(""next"")' title='Cap +'><img class='icoimg' src='" + icoNext + @"' alt='Next'></button>
      </div>

      <div class='grid3' style='margin-bottom:10px;'>
        <button class='btn' id='btnBack10' title='-10s'><img class='icoimg' src='" + icoBack10 + @"' alt='-10s'></button>
        <button class='btn main' id='pauseBtn' onclick='cmd(""pause"")' title='Pausa'><img class='icoimg' src='" + icoPause + @"' alt='Pausa'></button>
        <button class='btn' id='btnFwd10' title='+10s'><img class='icoimg' src='" + icoFwd10 + @"' alt='+10s'></button>
      </div>

      <!-- STOP grande come gli altri -->
      <div class='grid3'>
        <div></div>
        <button class='btn warn' onclick='cmd(""stop"")' title='Stop'><img class='icoimg' src='" + icoStop + @"' alt='Stop'></button>
        <div></div>
      </div>
    </div>

    <!-- NAV -->
    <div class='panel'>
      <div class='navrow'>

        <!-- SINISTRA: 2 e 2 -->
        <div class='sidecol'>
          <button class='btn sidebtn' onclick='cmd(""mute"")' title='Mute'>
            <img class='icoimg' src='" + icoMute + @"' alt='Mute'>
          </button>
          <button class='btn sidebtn' onclick='cmd(""full"")' title='Fullscreen'>
            <img class='icoimg' src='" + icoFull + @"' alt='Fullscreen'>
          </button>
        </div>

        <div class='dpadwrap'>
          <div class='dpad'>
            <button class='btn round up'    onclick='cmd(""up"")' title='Su'>" + dpadUpSvg + @"</button>
            <button class='btn round down'  onclick='cmd(""down"")' title='Giu'>" + dpadDownSvg + @"</button>
            <button class='btn round left'  onclick='cmd(""left"")' title='Sinistra'>" + dpadLeftSvg + @"</button>
            <button class='btn round right' onclick='cmd(""right"")' title='Destra'>" + dpadRightSvg + @"</button>
            <button class='btn ok'          onclick='cmd(""ok"")' title='OK'>" + dpadOkSvg + @"</button>
          </div>
        </div>

        <!-- DESTRA: 2 e 2 -->
        <div class='sidecol'>
          <button class='btn sidebtn' onclick='cmd(""volup"")' title='Volume +'>
            <img class='icoimg' src='" + icoVolPlus + @"' alt='Volume +'>
          </button>
          <button class='btn sidebtn' onclick='cmd(""voldown"")' title='Volume -'>
            <img class='icoimg' src='" + icoVolMinus + @"' alt='Volume -'>
          </button>
        </div>

      </div>

      <!-- SOTTO DPAD: 3 tasti -->
      <div class='grid3' style='margin-top:12px;'>
        <button class='btn' onclick='cmd(""info"")' title='Info'><img class='icoimg' src='" + icoInfo + @"' alt='Info'></button>
        <button class='btn' onclick='cmd(""settings"")' title='Impostazioni'><img class='icoimg' src='" + icoSettings + @"' alt='Impostazioni'></button>
        <button class='btn' onclick='cmd(""back"")' title='Back'><img class='icoimg' src='" + icoBack + @"' alt='Back'></button>
      </div>

      <!-- (extra utili) Libreria (sx) + Home (dx) -->
        <div class='grid2' style='margin-top:10px;'>
          <button class='btn' onclick='cmd(""library"")' title='Libreria'>
            <img class='icoimg' src='" + icoLibrary + @"' alt='Libreria'>
          </button>
          <button class='btn' onclick='cmd(""home"")' title='Home'>
            <img class='icoimg' src='" + icoHome + @"' alt='Home'>
          </button>
        </div>
    </div>

    <!-- TEMPO -->
    <div class='panel'>
      <div class='time-title'>TEMPO</div>
      <input id='seek' type='range' min='0' max='0' value='0' step='0.1'
             oninput='onSeekInput(this.value)'
             onchange='onSeekCommit(this.value)' />
      <div class='time-row'>
        <span id='tcur'>00:00</span>
        <span id='tdur'>00:00</span>
      </div>
    </div>

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

    // 🔍 LOG DATI RICEVUTI
    console.log(""[STATE]"", s);

    const titleEl = qs('#title');
    if(titleEl){
      const t = (s.Title || '').trim();
      console.log(""[TITLE]"", t);
      titleEl.textContent = t;
    }

    const isHdr = !!s.OutputHdr;
    const hdrText = isHdr ? 'HDR' : 'SDR';

    const audioText = s.Bitstream ? 'BITSTREAM' : 'PCM';
    const is3d = !!s.Is3D;
    const dimText = is3d ? '3D' : '2D';

    console.log(""[FLAGS]"", { HDR: hdrText, Audio: audioText, Dim: dimText });

    const hdrC = qs('#hdrChip'); if(hdrC) hdrC.textContent = hdrText;
    const audC = qs('#audioChip'); if(audC) audC.textContent = audioText;
    const dimC = qs('#dimChip'); if(dimC) dimC.textContent = dimText;

    const seek = qs('#seek');
    if(seek){
      if(s.Duration > 0){
        if(!seek._drag){
          seek.max = Number(s.Duration).toFixed(1);
          seek.value = Number(s.Position).toFixed(1);

          console.log(""[TIME]"", {
            position: s.Position,
            duration: s.Duration
          });

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
  console.log(""[CMD]"", c);
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
        }
        catch
        {
            _tcpFront = null;
            _frontRunning = false;
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
                        if (!string.IsNullOrWhiteSpace(clientIp) &&
                            !headerText.Contains("\r\nX-Forwarded-For:", StringComparison.OrdinalIgnoreCase))
                        {
                            headerText += "\r\nX-Forwarded-For: " + clientIp;
                        }

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

    // ====================== mDNS cinecore-remote.local ======================
    private void StartMdns()
    {
        try
        {
            _mdns ??= new MdnsResponder(MdnsHostFqdn, LocalIPv4List);
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
                _udp.ExclusiveAddressUse = false;
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                _udp.JoinMulticastGroup(MdnsMulticast);

                _cts = new CancellationTokenSource();
                _running = true;
                _loop = Task.Run(() => Loop(_cts.Token));
            }
            catch
            {
                try { _udp?.Dispose(); } catch { }
                _udp = null;
                _cts = null;
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

                try { HandlePacket(rx.Buffer); }
                catch { }
            }
        }

        private void HandlePacket(byte[] msg)
        {
            if (_udp == null) return;
            if (msg.Length < 12) return;

            ushort qd = ReadU16(msg, 4);
            if (qd == 0) return;

            int off = 12;
            bool match = false;

            for (int i = 0; i < qd; i++)
            {
                if (!TryReadName(msg, ref off, out var qname)) return;
                if (off + 4 > msg.Length) return;

                ushort qtype = ReadU16(msg, off); off += 2;
                ushort qclass = ReadU16(msg, off); off += 2;
                _ = qclass;

                string qn = NormalizeName(qname);
                if (qn == _hostFqdnLower && (qtype == 1 || qtype == 255))
                    match = true;
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

            _udp.Send(resp, resp.Length, new IPEndPoint(MdnsMulticast, MdnsPort));
        }

        private static byte[]? BuildResponse(string hostFqdnLower, List<byte[]> ipBytes)
        {
            var buf = new List<byte>(256);

            // ID
            buf.Add(0); buf.Add(0);
            // FLAGS response+authoritative
            buf.Add(0x84); buf.Add(0x00);
            // QDCOUNT=1
            buf.Add(0x00); buf.Add(0x01);
            // ANCOUNT = N
            WriteU16(buf, (ushort)ipBytes.Count);
            // NS=0
            buf.Add(0x00); buf.Add(0x00);
            // AR=0
            buf.Add(0x00); buf.Add(0x00);

            // Question
            WriteQName(buf, hostFqdnLower);
            WriteU16(buf, 0x0001); // A
            WriteU16(buf, 0x0001); // IN

            // Answers (name ptr 0xC00C)
            for (int i = 0; i < ipBytes.Count; i++)
            {
                buf.Add(0xC0); buf.Add(0x0C);
                WriteU16(buf, 0x0001); // A
                WriteU16(buf, 0x0001); // IN
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
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var props = ni.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(ua.Address.ToString());
            }
        }
        return list.ToArray();
    }
}

// ====================== stato player esposto su /api/state ======================
internal sealed class RemoteState
{
    public string? Title { get; set; }
    public double Position { get; set; }
    public double Duration { get; set; }
    public bool OutputHdr { get; set; }
    public bool Is3D { get; set; }
    public bool Bitstream { get; set; }
}


