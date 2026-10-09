#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Jellyfin, parlando direttamente con il server (non via DLNA, che in Jellyfin e' un plugin
    /// a parte): ricerca dei server in rete, accesso con nome e password oppure con Quick
    /// Connect, catalogo con i metadati del server, riproduzione diretta del file originale e
    /// segnalazione dell'avanzamento, cosi' "riprendi" e "visto" restano allineati con gli
    /// altri dispositivi.
    /// Lo stesso client parla anche con Emby, da cui Jellyfin deriva: le chiamate usate qui sono comuni
    /// ai due server; cambiano la ricerca in rete, l'intestazione di accesso e Quick Connect (solo Jellyfin).
    /// Il token di accesso sta solo in <c>jellyfin.json</c>, cifrato per l'utente Windows, e
    /// viene aggiunto all'indirizzo del flusso solo in memoria, al momento di aprirlo: gli
    /// indirizzi che il player salva (ripresa, cronologia, coda) e scrive nei registri non lo
    /// contengono.
    /// </summary>
    internal static class JellyfinClient
    {
        public const string Jellyfin = "Jellyfin", Emby = "Emby";

        /// <param name="Product">"Jellyfin" o "Emby"; vuoto se il server non e' ancora stato interrogato.</param>
        public sealed record Server(string Id, string Name, string Address, string Version, string Product = Jellyfin);

        public sealed class Account
        {
            public string ServerId { get; set; } = "";
            public string ServerName { get; set; } = "";
            public string Address { get; set; } = "";
            public string UserId { get; set; } = "";
            public string UserName { get; set; } = "";
            /// <summary>"Jellyfin" o "Emby" (gli account salvati prima di Emby non hanno il campo: Jellyfin).</summary>
            public string Product { get; set; } = Jellyfin;
            /// <summary>Token cifrato (SecretVault): mai in chiaro su disco.</summary>
            public string Token { get; set; } = "";
        }

        public sealed record QuickConnectRequest(string Secret, string Code);

        /// <summary>Il server non accetta (piu') il token: l'accesso va rifatto.</summary>
        public sealed class UnauthorizedException : Exception
        {
            public UnauthorizedException() : base("Jellyfin: access denied") { }
        }

        public sealed class Item
        {
            public string Id { get; init; } = "";
            /// <summary>Movie, Episode, Audio, MusicVideo, Video.</summary>
            public string Type { get; init; } = "";
            public string Name { get; init; } = "";
            public string? SeriesName { get; init; }
            public string? SeriesId { get; init; }
            public int? SeasonNumber { get; init; }
            public int? EpisodeNumber { get; init; }
            public int? Year { get; init; }
            public double DurationSeconds { get; init; }
            public string? Overview { get; init; }
            public List<string> Genres { get; init; } = new();
            public string? Tagline { get; init; }
            public double? Rating { get; init; }
            public int? TmdbId { get; init; }
            public string? ImdbId { get; init; }
            public DateTime DateCreatedUtc { get; init; }
            public string MediaSourceId { get; init; } = "";
            public string Extension { get; init; } = "";
            /// <summary>Nome del file sul server (solo il nome, per la scheda dei dettagli).</summary>
            public string FileName { get; init; } = "";
            public long Bytes { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public bool Hdr { get; init; }
            public bool DolbyVision { get; init; }
            public string? AudioCodec { get; init; }
            /// <summary>Variante del codec quando conta: "DTS-HD MA", "DTS-HD HRA".</summary>
            public string? AudioProfile { get; init; }
            public int AudioChannels { get; init; }
            public bool ObjectAudio { get; init; }
            public int AudioSampleRate { get; init; }
            public int AudioBitDepth { get; init; }
            public string? Album { get; init; }
            public string? AlbumId { get; init; }
            public string? AlbumArtist { get; init; }
            public string? Artist { get; init; }
            public int? TrackNumber { get; init; }
            public bool HasPrimaryImage { get; init; }
            public bool HasBackdrop { get; init; }
            public bool SeriesHasPrimaryImage { get; init; }
            public string? BackdropItemId { get; init; }
            public bool AlbumHasPrimaryImage { get; init; }
            public double ResumeSeconds { get; init; }
            public bool Played { get; init; }
            public DateTime? LastPlayedUtc { get; init; }
        }

        private sealed class Store
        {
            public string DeviceId { get; set; } = "";
            public List<Account> Accounts { get; set; } = new();
            /// <summary>Titolo e categoria degli elementi visti nell'ultimo catalogo, per indirizzo del flusso:
            /// servono a dare un nome a cio' che si riprende dalla cronologia prima di ricollegarsi al server.</summary>
            public Dictionary<string, string[]> Titles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private const int DiscoveryPort = 7359;
        private static readonly object Sync = new();
        private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
        private static readonly Regex StreamPath = new(@"^/(?<kind>Videos|Audio)/(?<id>[0-9A-Za-z_\-]{1,64})/stream(\.[A-Za-z0-9]{1,6})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static Store? _store;

        private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "jellyfin.json");

        private static Store State
        {
            get
            {
                lock (Sync)
                {
                    if (_store != null) return _store;
                    try { _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath)); } catch { }
                    _store ??= new Store();
                    _store.Accounts ??= new List<Account>();
                    _store.Titles = new Dictionary<string, string[]>(_store.Titles ?? new(), StringComparer.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(_store.DeviceId))
                    {
                        _store.DeviceId = Guid.NewGuid().ToString("N");
                        SaveLocked();
                    }
                    return _store;
                }
            }
        }

        private static void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                string temp = StorePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_store));
                File.Move(temp, StorePath, overwrite: true);
            }
            catch (Exception ex) { Dbg.Warn("[JELLYFIN] cannot save accounts: " + ex.Message); }
        }

        // ----- Account -----

        public static IReadOnlyList<Account> Accounts
        {
            get { var state = State; lock (Sync) return state.Accounts.ToList(); }
        }

        public static Account? FindAccount(string? serverId)
        {
            if (string.IsNullOrWhiteSpace(serverId)) return null;
            var state = State;
            lock (Sync) return state.Accounts.FirstOrDefault(a => string.Equals(a.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
        }

        public static void RemoveAccount(string? serverId)
        {
            var state = State;
            lock (Sync)
            {
                if (state.Accounts.RemoveAll(a => string.Equals(a.ServerId, serverId, StringComparison.OrdinalIgnoreCase)) > 0)
                    SaveLocked();
            }
        }

        /// <summary>Il server ha cambiato indirizzo (DHCP): l'account lo segue.</summary>
        public static void UpdateAddress(string serverId, string address)
        {
            var state = State;
            lock (Sync)
            {
                var account = state.Accounts.FirstOrDefault(a => string.Equals(a.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
                if (account == null || string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase)) return;
                account.Address = address;
                SaveLocked();
            }
        }

        private static Account StoreAccount(Server server, JsonElement authentication)
        {
            string token = authentication.GetProperty("AccessToken").GetString() ?? "";
            var user = authentication.GetProperty("User");
            var account = new Account
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Address = server.Address,
                UserId = user.GetProperty("Id").GetString() ?? "",
                UserName = user.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                Product = server.Product == Emby ? Emby : Jellyfin,
                Token = SecretVault.Protect(token)
            };
            if (token.Length == 0 || account.UserId.Length == 0) throw new InvalidOperationException("Jellyfin: incomplete sign-in answer");
            var state = State;
            lock (Sync)
            {
                state.Accounts.RemoveAll(a => string.Equals(a.ServerId, server.Id, StringComparison.OrdinalIgnoreCase));
                state.Accounts.Add(account);
                SaveLocked();
            }
            return account;
        }

        // ----- Richieste -----

        private static string Quote(string value) => value.Replace("\\", "").Replace("\"", "");

        private static string Authorization(string? token)
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            string device = Quote(Environment.MachineName);
            return $"MediaBrowser Client=\"Cinecore Player\", Device=\"{device}\", DeviceId=\"{State.DeviceId}\", Version=\"{version}\"" +
                   (string.IsNullOrEmpty(token) ? "" : $", Token=\"{token}\"");
        }

        private static async Task<JsonDocument?> SendAsync(HttpMethod method, string address, string path, string? token, object? body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, address.TrimEnd('/') + path);
            request.Headers.TryAddWithoutValidation("Authorization", Authorization(token));
            // Emby legge l'intestazione con il suo nome; Jellyfin la ignora.
            request.Headers.TryAddWithoutValidation("X-Emby-Authorization", Authorization(token));
            request.Headers.Accept.ParseAdd("application/json");
            if (body != null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UnauthorizedException();
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length == 0 ? null : JsonDocument.Parse(bytes);
        }

        private static string? TokenOf(Account account) => SecretVault.Unprotect(account.Token);

        /// <summary>"Jellyfin" o "Emby" per il server di un flusso; "Jellyfin" se non e' un flusso di questi server.</summary>
        public static string ProductOfStream(string? url) =>
            TryParseStream(url, out Account account, out _, out _) && account.Product == Emby ? Emby : Jellyfin;

        /// <summary>Il prodotto scritto in una descrizione come "Emby 4.10.1" (quella mostrata accanto ai server).</summary>
        public static string ProductOfLabel(string? label) =>
            (label ?? "").TrimStart().StartsWith(Emby, StringComparison.OrdinalIgnoreCase) ? Emby : Jellyfin;

        // ----- Server -----

        /// <summary>"192.168.1.10", "nas:8096", "https://casa.example/jellyfin" -> indirizzo completo.</summary>
        public static string NormalizeAddress(string input)
        {
            string text = (input ?? "").Trim().TrimEnd('/');
            if (text.Length == 0) return "";
            if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Host.Length == 0)
                return "";
            // Senza porta, in http, quella di Jellyfin (ed Emby). Un indirizzo IPv6 fra parentesi ha i due punti anche senza porta.
            bool explicitPort = Regex.IsMatch(text, @"^[a-z]+://(\[[^\]]*\]|[^/:\[]*):\d+", RegexOptions.IgnoreCase);
            var builder = new UriBuilder(uri) { Query = "", Fragment = "" };
            if (!explicitPort && uri.Scheme == Uri.UriSchemeHttp) builder.Port = 8096;
            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        /// <summary>Chiede al server chi e'. Lancia un'eccezione se all'indirizzo non risponde un server Jellyfin o Emby.</summary>
        public static async Task<Server> GetServerAsync(string address, CancellationToken ct)
        {
            string normalized = NormalizeAddress(address);
            if (normalized.Length == 0) throw new ArgumentException("address");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var doc = await SendAsync(HttpMethod.Get, normalized, "/System/Info/Public", null, null, timeout.Token).ConfigureAwait(false);
            var root = doc!.RootElement;
            string id = root.TryGetProperty("Id", out var i) ? i.GetString() ?? "" : "";
            if (id.Length == 0) throw new InvalidOperationException("not a Jellyfin server");
            // Jellyfin si presenta con ProductName; Emby non lo dichiara.
            string product = root.TryGetProperty("ProductName", out var p) && (p.GetString() ?? "").Contains("Jellyfin", StringComparison.OrdinalIgnoreCase) ? Jellyfin : Emby;
            return new Server(id,
                root.TryGetProperty("ServerName", out var n) ? n.GetString() ?? product : product,
                normalized,
                root.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "",
                product);
        }

        /// <summary>Server Jellyfin che rispondono nella rete locale (annuncio UDP sulla porta 7359).</summary>
        public static async Task<List<Server>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            var found = new Dictionary<string, Server>(StringComparer.OrdinalIgnoreCase);
            var clients = new List<UdpClient>();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(timeout);
            try
            {
                // Stessa porta e stessa risposta per i due server: cambia solo la domanda.
                byte[][] questions = { Encoding.UTF8.GetBytes("who is JellyfinServer?"), Encoding.UTF8.GetBytes("who is EmbyServer?") };
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                        {
                            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                            byte[] ip = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
                            var broadcast = new IPAddress(ip.Select((b, index) => (byte)(b | ~mask[index])).ToArray());
                            var udp = new UdpClient(new IPEndPoint(unicast.Address, 0)) { EnableBroadcast = true };
                            clients.Add(udp);
                            foreach (byte[] question in questions)
                                foreach (IPAddress target in new[] { broadcast, IPAddress.Broadcast })
                                    try { await udp.SendAsync(question, question.Length, new IPEndPoint(target, DiscoveryPort)).ConfigureAwait(false); } catch { }
                        }
                    }
                    catch { }
                }

                async Task ReceiveAsync(UdpClient udp)
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            UdpReceiveResult answer = await udp.ReceiveAsync(stop.Token).ConfigureAwait(false);
                            using var doc = JsonDocument.Parse(answer.Buffer);
                            var root = doc.RootElement;
                            string id = root.TryGetProperty("Id", out var i) ? i.GetString() ?? "" : "";
                            string address = NormalizeAddress(root.TryGetProperty("Address", out var a) ? a.GetString() ?? "" : "");
                            if (id.Length == 0 || address.Length == 0) continue;
                            string name = root.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                            lock (found) if (found.ContainsKey(id)) continue;
                            // L'annuncio non dice quale dei due server e': lo si chiede all'indirizzo annunciato. Se li'
                            // risponde un altro server (due server sulla stessa porta), l'annuncio non e' utilizzabile.
                            Server? asked = null;
                            try { asked = await GetServerAsync(address, stop.Token).ConfigureAwait(false); } catch { }
                            if (asked != null && !string.Equals(asked.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                            lock (found) found[id] = asked ?? new Server(id, name.Length > 0 ? name : "Server", address, "", "");
                        }
                        catch (OperationCanceledException) { break; }
                        catch (ObjectDisposedException) { break; }
                        catch { }
                    }
                }

                await Task.WhenAll(clients.Select(ReceiveAsync)).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                foreach (UdpClient udp in clients)
                    try { udp.Dispose(); } catch { }
            }
            lock (found) return found.Values.ToList();
        }

        // ----- Accesso -----

        public static async Task<Account> LoginAsync(Server server, string userName, string password, CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Post, server.Address, "/Users/AuthenticateByName", null,
                new Dictionary<string, string> { ["Username"] = userName, ["Pw"] = password }, ct).ConfigureAwait(false);
            return StoreAccount(server, doc!.RootElement);
        }

        public static async Task<bool> QuickConnectEnabledAsync(Server server, CancellationToken ct)
        {
            try
            {
                using var doc = await SendAsync(HttpMethod.Get, server.Address, "/QuickConnect/Enabled", null, null, ct).ConfigureAwait(false);
                return doc != null && doc.RootElement.ValueKind == JsonValueKind.True;
            }
            catch (UnauthorizedException) { throw; }
            catch { return false; }
        }

        /// <summary>Chiede un codice da inserire in Jellyfin (Impostazioni, Quick Connect) su un dispositivo gia' collegato.</summary>
        public static async Task<QuickConnectRequest> StartQuickConnectAsync(Server server, CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Post, server.Address, "/QuickConnect/Initiate", null, null, ct).ConfigureAwait(false);
            var root = doc!.RootElement;
            return new QuickConnectRequest(root.GetProperty("Secret").GetString() ?? "", root.GetProperty("Code").GetString() ?? "");
        }

        /// <summary>Null finche' il codice non e' stato approvato; poi l'account, gia' salvato.</summary>
        public static async Task<Account?> TryFinishQuickConnectAsync(Server server, QuickConnectRequest request, CancellationToken ct)
        {
            using (var state = await SendAsync(HttpMethod.Get, server.Address, "/QuickConnect/Connect?secret=" + Uri.EscapeDataString(request.Secret), null, null, ct).ConfigureAwait(false))
            {
                if (state == null || !state.RootElement.TryGetProperty("Authenticated", out var done) || done.ValueKind != JsonValueKind.True)
                    return null;
            }
            using var doc = await SendAsync(HttpMethod.Post, server.Address, "/Users/AuthenticateWithQuickConnect", null,
                new Dictionary<string, string> { ["Secret"] = request.Secret }, ct).ConfigureAwait(false);
            return StoreAccount(server, doc!.RootElement);
        }

        /// <summary>Esce dal server (il token viene revocato) e dimentica l'account.</summary>
        public static async Task SignOutAsync(Account account, CancellationToken ct)
        {
            try { (await SendAsync(HttpMethod.Post, account.Address, "/Sessions/Logout", TokenOf(account), null, ct).ConfigureAwait(false))?.Dispose(); }
            catch { }
            RemoveAccount(account.ServerId);
        }

        // ----- Catalogo -----

        /// <summary>Film, episodi, video e brani a cui l'utente ha accesso. <paramref name="progress"/> riceve l'elenco man mano che cresce.</summary>
        public static async Task<List<Item>> LoadItemsAsync(Account account, Action<IReadOnlyList<Item>>? progress, CancellationToken ct)
        {
            string? token = TokenOf(account);
            if (string.IsNullOrEmpty(token)) throw new UnauthorizedException();
            const int Page = 300;
            const string Fields = "Overview,Genres,Taglines,ProviderIds,DateCreated,MediaSources,Path";
            var items = new List<Item>();
            for (int start = 0; ; start += Page)
            {
                ct.ThrowIfCancellationRequested();
                string path = $"/Items?userId={account.UserId}&Recursive=true&IncludeItemTypes=Movie,Episode,Audio,MusicVideo,Video" +
                              $"&Fields={Fields}&EnableUserData=true&EnableImageTypes=Primary,Backdrop&SortBy=SortName&StartIndex={start}&Limit={Page}";
                using var doc = await SendAsync(HttpMethod.Get, account.Address, path, token, null, ct).ConfigureAwait(false);
                if (doc == null || !doc.RootElement.TryGetProperty("Items", out var page)) break;
                int count = 0;
                foreach (JsonElement element in page.EnumerateArray())
                {
                    count++;
                    try
                    {
                        Item? item = ParseItem(element);
                        if (item != null) items.Add(item);
                    }
                    catch (Exception ex) { Dbg.Warn("[JELLYFIN] item skipped: " + ex.Message); }
                }
                if (count > 0) progress?.Invoke(items.ToList());
                int total = doc.RootElement.TryGetProperty("TotalRecordCount", out var t) && t.TryGetInt32(out int n) ? n : 0;
                if (count < Page || start + count >= total) break;
            }
            return items;
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static int? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : null;

        private static Item? ParseItem(JsonElement e)
        {
            string id = Text(e, "Id") ?? "";
            string type = Text(e, "Type") ?? "";
            if (id.Length == 0 || type.Length == 0) return null;
            if (e.TryGetProperty("IsFolder", out var folder) && folder.ValueKind == JsonValueKind.True) return null;
            // File segnaposto (.strm, dischi virtuali) e canali: non sono un file da riprodurre direttamente.
            string? location = Text(e, "LocationType");
            if (location != null && !location.Equals("FileSystem", StringComparison.OrdinalIgnoreCase)) return null;

            JsonElement source = default;
            bool hasSource = e.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0;
            if (hasSource) source = sources[0];

            string extension = "", fileName = "";
            string? filePath = (hasSource ? Text(source, "Path") : null) ?? Text(e, "Path");
            if (!string.IsNullOrWhiteSpace(filePath))
                try
                {
                    // Il server puo' girare su Linux: il nome e' cio' che segue l'ultima barra, in un verso o nell'altro.
                    fileName = filePath[(filePath.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
                    extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
                }
                catch { }
            if (extension.Length == 0)
                extension = ((hasSource ? Text(source, "Container") : null) ?? Text(e, "Container") ?? "").Split(',')[0].Trim().ToLowerInvariant();
            if (extension == "mov" && (Text(e, "Container") ?? "").Contains("mp4", StringComparison.OrdinalIgnoreCase)) extension = "mp4";
            if (extension.Length == 0 || extension.Length > 5 || !extension.All(char.IsLetterOrDigit)) return null;
            if (extension is "strm" or "iso") return null;

            int width = 0, height = 0, channels = 0, sampleRate = 0, bitDepth = 0;
            bool hdr = false, dolbyVision = false, objectAudio = false;
            string? audioCodec = null, audioProfile = null;
            if (hasSource && source.TryGetProperty("MediaStreams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                bool video = false, audio = false;
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    string kind = Text(stream, "Type") ?? "";
                    if (kind == "Video" && !video)
                    {
                        video = true;
                        width = Number(stream, "Width") ?? 0;
                        height = Number(stream, "Height") ?? 0;
                        string range = (Text(stream, "VideoRange") ?? "") + " " + (Text(stream, "VideoRangeType") ?? "");
                        hdr = range.Contains("HDR", StringComparison.OrdinalIgnoreCase) || range.Contains("HLG", StringComparison.OrdinalIgnoreCase) || range.Contains("DOVI", StringComparison.OrdinalIgnoreCase);
                        dolbyVision = range.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || Number(stream, "DvProfile") != null;
                    }
                    else if (kind == "Audio" && (!audio || (stream.TryGetProperty("IsDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True)))
                    {
                        audio = true;
                        audioCodec = Text(stream, "Codec");
                        audioProfile = Text(stream, "Profile");
                        channels = Number(stream, "Channels") ?? 0;
                        sampleRate = Number(stream, "SampleRate") ?? 0;
                        bitDepth = Number(stream, "BitDepth") ?? 0;
                        string detail = (Text(stream, "Profile") ?? "") + " " + (Text(stream, "DisplayTitle") ?? "") + " " + (Text(stream, "Title") ?? "");
                        objectAudio = detail.Contains("Atmos", StringComparison.OrdinalIgnoreCase) || detail.Contains("DTS:X", StringComparison.OrdinalIgnoreCase) || detail.Contains("DTS-X", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            int? tmdb = null;
            string? imdb = null;
            if (e.TryGetProperty("ProviderIds", out var providers) && providers.ValueKind == JsonValueKind.Object)
            {
                if (int.TryParse(Text(providers, "Tmdb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) tmdb = parsed;
                imdb = Text(providers, "Imdb");
            }

            double resume = 0;
            bool played = false;
            DateTime? lastPlayed = null;
            if (e.TryGetProperty("UserData", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                if (user.TryGetProperty("PlaybackPositionTicks", out var ticks) && ticks.TryGetInt64(out long position)) resume = position / 10_000_000.0;
                played = user.TryGetProperty("Played", out var p) && p.ValueKind == JsonValueKind.True;
                if (DateTime.TryParse(Text(user, "LastPlayedDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when)) lastPlayed = when;
            }

            bool Tagged(string group, string name) => e.TryGetProperty(group, out var tags) && tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out _);
            bool ownBackdrop = e.TryGetProperty("BackdropImageTags", out var backdrops) && backdrops.ValueKind == JsonValueKind.Array && backdrops.GetArrayLength() > 0;
            bool parentBackdrop = e.TryGetProperty("ParentBackdropImageTags", out var parents) && parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() > 0;

            long runTime = e.TryGetProperty("RunTimeTicks", out var run) && run.TryGetInt64(out long r) ? r : 0;
            DateTime.TryParse(Text(e, "DateCreated"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime created);
            string? artist = e.TryGetProperty("Artists", out var artists) && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0 ? artists[0].GetString() : null;

            return new Item
            {
                Id = id,
                Type = type,
                Name = Text(e, "Name") ?? "",
                SeriesName = Text(e, "SeriesName"),
                SeriesId = Text(e, "SeriesId"),
                SeasonNumber = Number(e, "ParentIndexNumber"),
                EpisodeNumber = type == "Episode" ? Number(e, "IndexNumber") : null,
                Year = Number(e, "ProductionYear"),
                DurationSeconds = runTime / 10_000_000.0,
                Overview = Text(e, "Overview"),
                Genres = e.TryGetProperty("Genres", out var genres) && genres.ValueKind == JsonValueKind.Array
                    ? genres.EnumerateArray().Select(g => g.GetString() ?? "").Where(g => g.Length > 0).ToList() : new List<string>(),
                Tagline = e.TryGetProperty("Taglines", out var taglines) && taglines.ValueKind == JsonValueKind.Array && taglines.GetArrayLength() > 0 ? taglines[0].GetString() : null,
                Rating = e.TryGetProperty("CommunityRating", out var rating) && rating.TryGetDouble(out double score) ? score : null,
                TmdbId = tmdb,
                ImdbId = imdb,
                DateCreatedUtc = created,
                MediaSourceId = (hasSource ? Text(source, "Id") : null) ?? id,
                Extension = extension,
                FileName = fileName,
                Bytes = hasSource && source.TryGetProperty("Size", out var size) && size.TryGetInt64(out long bytes) ? bytes : 0,
                Width = width,
                Height = height,
                Hdr = hdr,
                DolbyVision = dolbyVision,
                AudioCodec = audioCodec,
                AudioProfile = audioProfile,
                AudioChannels = channels,
                ObjectAudio = objectAudio,
                AudioSampleRate = sampleRate,
                AudioBitDepth = bitDepth,
                Album = Text(e, "Album"),
                AlbumId = Text(e, "AlbumId"),
                AlbumArtist = Text(e, "AlbumArtist"),
                Artist = artist,
                TrackNumber = type == "Audio" ? Number(e, "IndexNumber") : null,
                HasPrimaryImage = Tagged("ImageTags", "Primary"),
                HasBackdrop = ownBackdrop || parentBackdrop,
                BackdropItemId = ownBackdrop ? id : parentBackdrop ? Text(e, "ParentBackdropItemId") : null,
                SeriesHasPrimaryImage = Text(e, "SeriesPrimaryImageTag") != null,
                AlbumHasPrimaryImage = Text(e, "AlbumPrimaryImageTag") != null,
                ResumeSeconds = resume,
                Played = played,
                LastPlayedUtc = lastPlayed
            };
        }

        // ----- Indirizzi -----

        /// <summary>Indirizzo del file originale (nessuna transcodifica), senza token. L'ultimo parametro
        /// ripete l'estensione perche' il player riconosce audio e video da come finisce l'indirizzo.</summary>
        public static string StreamUrl(string address, Item item)
        {
            string root = address.TrimEnd('/');
            return item.Type == "Audio"
                ? $"{root}/Audio/{item.Id}/stream.{item.Extension}?static=true&ext=.{item.Extension}"
                : $"{root}/Videos/{item.Id}/stream.{item.Extension}?static=true&mediaSourceId={item.MediaSourceId}&ext=.{item.Extension}";
        }

        public static string ImageUrl(string address, string itemId, string kind, int maxWidth) =>
            $"{address.TrimEnd('/')}/Items/{itemId}/Images/{kind}?maxWidth={maxWidth}&quality=90";

        /// <summary>Scarica un'immagine da un endpoint immagini di un account Jellyfin noto,
        /// aggiungendo il token sul server Cinecore senza esporlo al browser del telecomando.</summary>
        public static async Task<byte[]?> TryDownloadArtworkAsync(string imageUrl, CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri) ||
                (imageUri.Scheme != Uri.UriSchemeHttp && imageUri.Scheme != Uri.UriSchemeHttps))
                return null;

            Account? matchedAccount = null;
            string? relativePath = null;
            foreach (Account account in Accounts)
            {
                if (!Uri.TryCreate(NormalizeAddress(account.Address), UriKind.Absolute, out Uri? root) ||
                    !string.Equals(root.Scheme, imageUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(root.Authority, imageUri.Authority, StringComparison.OrdinalIgnoreCase))
                    continue;

                string basePath = root.AbsolutePath.TrimEnd('/');
                if (basePath.Length > 0 && !imageUri.AbsolutePath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string endpoint = basePath.Length > 0 ? imageUri.AbsolutePath[basePath.Length..] : imageUri.AbsolutePath;
                if (!Regex.IsMatch(endpoint, @"^/Items/[0-9a-z_\-]{1,64}/Images/(?:Primary|Backdrop)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;
                if (!string.IsNullOrEmpty(imageUri.Query) &&
                    !Regex.IsMatch(imageUri.Query, @"^\?maxWidth=\d+(?:&quality=\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;

                matchedAccount = account;
                relativePath = endpoint + imageUri.Query;
                break;
            }

            if (matchedAccount == null || string.IsNullOrWhiteSpace(relativePath))
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, matchedAccount.Address.TrimEnd('/') + relativePath);
                request.Headers.TryAddWithoutValidation("Authorization", Authorization(TokenOf(matchedAccount)));
                request.Headers.Accept.ParseAdd("image/*");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                    response.Content.Headers.ContentLength is long length && length > 12 * 1024 * 1024)
                    return null;

                byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                return bytes.Length is >= 64 and <= 12 * 1024 * 1024 ? bytes : null;
            }
            catch { return null; }
        }

        /// <summary>True se l'indirizzo e' un flusso di un server Jellyfin a cui il player e' collegato.</summary>
        public static bool TryParseStream(string? url, out Account account, out string itemId, out string mediaSourceId)
        {
            account = null!;
            itemId = mediaSourceId = "";
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
            foreach (Account candidate in Accounts)
            {
                if (!Uri.TryCreate(candidate.Address, UriKind.Absolute, out Uri? root)) continue;
                if (!string.Equals(root.Authority, uri.Authority, StringComparison.OrdinalIgnoreCase) || root.Scheme != uri.Scheme) continue;
                string basePath = root.AbsolutePath.TrimEnd('/');
                if (!uri.AbsolutePath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) continue;
                Match match = StreamPath.Match(uri.AbsolutePath[basePath.Length..]);
                if (!match.Success) continue;
                account = candidate;
                itemId = match.Groups["id"].Value;
                Match source = Regex.Match(uri.Query, @"[?&]mediaSourceId=([0-9A-Za-z_\-]{1,64})");
                mediaSourceId = source.Success ? source.Groups[1].Value : itemId;
                return true;
            }
            return false;
        }

        /// <summary>L'indirizzo da dare al motore di riproduzione: con il token, solo in memoria.</summary>
        public static string WithAccess(string url)
        {
            if (!TryParseStream(url, out Account account, out _, out _)) return url;
            string? token = TokenOf(account);
            if (string.IsNullOrEmpty(token) || url.Contains("api_key=", StringComparison.OrdinalIgnoreCase)) return url;
            // Il parametro con l'estensione deve restare l'ultimo (vedi StreamUrl).
            string key = "api_key=" + Uri.EscapeDataString(token);
            int ext = url.LastIndexOf("&ext=", StringComparison.OrdinalIgnoreCase);
            return ext >= 0 ? url[..ext] + "&" + key + url[ext..] : url + "&" + key;
        }

        // ----- Transcodifica -----

        /// <summary>Limiti proposti nelle impostazioni (bit al secondo); 0 = file originale.</summary>
        public static readonly int[] TranscodeChoices = { 0, 40_000_000, 20_000_000, 10_000_000, 6_000_000, 3_000_000, 1_500_000 };

        private static int? _transcodeBitrate;
        private static string _lastTranscodeSession = "";
        private static string TranscodePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "jellyfin-quality.txt");

        /// <summary>
        /// Bitrate video massimo chiesto al server (bit/s). 0: si riceve il file originale, com'e'.
        /// Con un limite il server converte al volo (H.264 + AAC, in HLS): serve quando la linea
        /// verso il server non regge il file intero, tipicamente fuori casa.
        /// </summary>
        public static int TranscodeBitrate
        {
            get
            {
                if (_transcodeBitrate.HasValue) return _transcodeBitrate.Value;
                try { _transcodeBitrate = File.Exists(TranscodePath) && int.TryParse(File.ReadAllText(TranscodePath).Trim(), out int saved) ? Math.Max(0, saved) : 0; }
                catch { _transcodeBitrate = 0; }
                return _transcodeBitrate.Value;
            }
            set
            {
                _transcodeBitrate = Math.Max(0, value);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(TranscodePath)!);
                    File.WriteAllText(TranscodePath, _transcodeBitrate.Value.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { Dbg.Warn("[JELLYFIN] quality save failed: " + ex.Message); }
            }
        }

        public static string TranscodeLabel(int bitrate, bool english) =>
            bitrate <= 0 ? (english ? "Original file" : "File originale") : (bitrate / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + " Mbit/s";

        /// <summary>
        /// L'indirizzo da aprire per un flusso Jellyfin: il file originale, oppure (video, con un limite
        /// scelto) lo stream convertito dal server. Il token entra solo qui, in memoria.
        /// </summary>
        public static string PlaybackUrl(string url)
        {
            int bitrate = TranscodeBitrate;
            if (bitrate <= 0 || !TryParseStream(url, out Account account, out string itemId, out string sourceId)) return WithAccess(url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.AbsolutePath.Contains("/Audio/", StringComparison.OrdinalIgnoreCase)) return WithAccess(url);
            string? token = TokenOf(account);
            if (string.IsNullOrEmpty(token)) return WithAccess(url);
            // La risoluzione scende con il bitrate: 10 Mbit/s in 4K sarebbero solo artefatti.
            int width = bitrate >= 30_000_000 ? 3840 : bitrate >= 5_000_000 ? 1920 : bitrate >= 2_500_000 ? 1280 : 854;
            int audio = bitrate >= 5_000_000 ? 384_000 : 192_000;
            string session = Guid.NewGuid().ToString("N");
            _lastTranscodeSession = session;
            return account.Address.TrimEnd('/') + $"/Videos/{itemId}/master.m3u8?MediaSourceId={sourceId}&api_key={Uri.EscapeDataString(token)}" +
                   $"&DeviceId={State.DeviceId}&PlaySessionId={session}&VideoCodec=h264&AudioCodec=aac&VideoBitrate={Math.Max(300_000, bitrate - audio)}&AudioBitrate={audio}" +
                   $"&MaxWidth={width}&TranscodingMaxAudioChannels={(bitrate >= 5_000_000 ? 6 : 2)}&SegmentContainer=ts&MinSegments=1&BreakOnNonKeyFrames=True&ext=.m3u8";
        }

        /// <summary>Fine della visione: il server smette di convertire (altrimenti continua per qualche minuto).</summary>
        public static async Task StopTranscodeAsync(Account account, CancellationToken ct)
        {
            string session = _lastTranscodeSession;
            if (session.Length == 0) return;
            _lastTranscodeSession = "";
            try { (await SendAsync(HttpMethod.Delete, account.Address, $"/Videos/ActiveEncodings?deviceId={State.DeviceId}&playSessionId={session}", TokenOf(account), null, ct).ConfigureAwait(false))?.Dispose(); }
            catch (Exception ex) { Dbg.Warn("[JELLYFIN] stop transcode: " + ex.Message); }
        }

        // ----- Titoli ricordati -----

        /// <summary>Titolo (e categoria) noti per un flusso: dall'ultimo catalogo letto, anche dopo un riavvio.</summary>
        public static (string Title, string Category)? RememberedTitle(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var state = State;
            lock (Sync)
                return state.Titles.TryGetValue(url, out string[]? entry) && entry.Length >= 2 ? (entry[0], entry[1]) : null;
        }

        /// <summary>Nome del file sul server per un flusso, se noto.</summary>
        public static string? RememberedFileName(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var state = State;
            lock (Sync)
                return state.Titles.TryGetValue(url, out string[]? entry) && entry.Length >= 3 && entry[2].Length > 0 ? entry[2] : null;
        }

        /// <summary>Identificativo TMDb di un film Jellyfin, come lo dichiara il server (per il diario su Trakt).</summary>
        public static int? RememberedMovieTmdbId(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var state = State;
            lock (Sync)
                return state.Titles.TryGetValue(url, out string[]? entry) && entry.Length >= 4 && int.TryParse(entry[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0 ? id : null;
        }

        public static void RememberTitles(string address, IEnumerable<(string Url, string Title, string Category, string FileName, int? MovieTmdbId)> titles)
        {
            var state = State;
            string prefix = address.TrimEnd('/') + "/";
            lock (Sync)
            {
                foreach (string stale in state.Titles.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                    state.Titles.Remove(stale);
                foreach (var (url, title, category, fileName, tmdb) in titles)
                    state.Titles[url] = new[] { title, category, fileName, tmdb?.ToString(CultureInfo.InvariantCulture) ?? "" };
                SaveLocked();
            }
        }

        public static IReadOnlyList<(string Url, string Title, string Category)> RememberedTitles()
        {
            var state = State;
            lock (Sync) return state.Titles.Where(pair => pair.Value.Length >= 2).Select(pair => (pair.Key, pair.Value[0], pair.Value[1])).ToList();
        }

        // ----- Avanzamento -----

        /// <param name="phase">"start", "progress" oppure "stop".</param>
        public static async Task ReportPlaybackAsync(Account account, string phase, string itemId, string mediaSourceId, string playSessionId,
            double positionSeconds, bool paused, CancellationToken ct)
        {
            string? token = TokenOf(account);
            if (string.IsNullOrEmpty(token)) return;
            string path = phase switch { "start" => "/Sessions/Playing", "stop" => "/Sessions/Playing/Stopped", _ => "/Sessions/Playing/Progress" };
            var body = new Dictionary<string, object>
            {
                ["ItemId"] = itemId,
                ["MediaSourceId"] = mediaSourceId,
                ["PlaySessionId"] = playSessionId,
                ["PositionTicks"] = (long)(Math.Max(0, positionSeconds) * 10_000_000),
                ["IsPaused"] = paused,
                ["CanSeek"] = true,
                ["PlayMethod"] = "DirectPlay"
            };
            (await SendAsync(HttpMethod.Post, account.Address, path, token, body, ct).ConfigureAwait(false))?.Dispose();
        }

        /// <summary>Punto di ripresa e stato "visto" di un elemento, come li conosce il server adesso.</summary>
        public static async Task<(double ResumeSeconds, bool Played)?> GetUserDataAsync(Account account, string itemId, CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Get, account.Address, $"/Users/{account.UserId}/Items/{itemId}", TokenOf(account), null, ct).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("UserData", out var user)) return null;
            double resume = user.TryGetProperty("PlaybackPositionTicks", out var ticks) && ticks.TryGetInt64(out long position) ? position / 10_000_000.0 : 0;
            return (resume, user.TryGetProperty("Played", out var played) && played.ValueKind == JsonValueKind.True);
        }
    }
}
