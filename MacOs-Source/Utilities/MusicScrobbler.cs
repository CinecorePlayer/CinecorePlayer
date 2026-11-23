#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Invio degli ascolti a Last.fm e ListenBrainz. Partono solo artista, titolo, album e durata
    /// del brano e l'ora di ascolto: mai nomi di file o percorsi. Le credenziali dell'utente
    /// (sessione Last.fm, token ListenBrainz) restano su questo PC cifrate con <see cref="SecretVault"/>.
    /// Gli ascolti che non partono (rete assente) restano in coda e vengono rimandati.
    /// </summary>
    internal static class MusicScrobbler
    {
        public sealed record Track(string Artist, string Title, string Album, double DurationSeconds);

        private sealed class Pending
        {
            public string Artist { get; set; } = "";
            public string Title { get; set; } = "";
            public string Album { get; set; } = "";
            public int Duration { get; set; }
            public long Timestamp { get; set; }
            public bool LastFm { get; set; }
            public bool ListenBrainz { get; set; }
        }

        private sealed class Stored
        {
            public string? LastFmSession { get; set; }       // cifrato
            public string? LastFmUser { get; set; }
            public string? LastFmPending { get; set; }
            public DateTime LastFmPendingUtc { get; set; }
            public bool LastFmAuthenticationRequired { get; set; }
            public string? ListenBrainzToken { get; set; }   // cifrato
            public string? ListenBrainzUser { get; set; }
            public bool ListenBrainzAuthenticationRequired { get; set; }
            public List<Pending> Queue { get; set; } = new();
        }

        private const string LastFmApi = "https://ws.audioscrobbler.com/2.0/";
        private const string ListenBrainzApi = "https://api.listenbrainz.org/1/";
        private const int QueueLimit = 400;
        private static HttpClient Http { get; set; } = CreateClient();
        private static readonly object Gate = new();
        private static readonly SemaphoreSlim FlushGate = new(1, 1);
        private static Stored? _stored;

        private static string StorePath { get; set; } = Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "scrobble.json");
        private static string LastFmKey => SecretVault.BuiltIn("lastfm.key") ?? "";
        private static string LastFmSecret => SecretVault.BuiltIn("lastfm.secret") ?? "";

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(45) }; // ListenBrainz a volte risponde dopo piu' di dieci secondi
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer/0.3");
            return client;
        }

        // Il file e' condiviso da tutte le copie aperte del player (quella installata e quella di prova).
        // Ognuna teneva in memoria la propria copia e a ogni ascolto la riscriveva intera: un collegamento
        // fatto in una finestra veniva cancellato dall'altra. Ora le credenziali cambiate altrove vengono
        // rilette, e si scrive solo cio' che questa copia ha davvero cambiato.
        private static bool _lastFmChangedHere, _listenBrainzChangedHere, _loadFailed;
        private static DateTime _diskStamp;
        private static long _diskCheckedTick;

        private static Stored? ReadDisk()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return JsonSerializer.Deserialize<Stored>(File.ReadAllText(StorePath)); }
                catch (IOException) { Thread.Sleep(40); }
                catch { return null; }
            }
            return null;
        }

        /// <summary>Prende dal disco le credenziali che questa copia non ha toccato.</summary>
        private static void AdoptDiskCredentials()
        {
            try
            {
                if (_stored == null || !File.Exists(StorePath)) return;
                DateTime stamp = File.GetLastWriteTimeUtc(StorePath);
                if (stamp == _diskStamp) return;
                Stored? disk = ReadDisk();
                if (disk == null) return;
                _diskStamp = stamp;
                if (!_lastFmChangedHere) { _stored.LastFmSession = disk.LastFmSession; _stored.LastFmUser = disk.LastFmUser; _stored.LastFmPending = disk.LastFmPending; _stored.LastFmPendingUtc = disk.LastFmPendingUtc; _stored.LastFmAuthenticationRequired = disk.LastFmAuthenticationRequired; }
                if (!_listenBrainzChangedHere) { _stored.ListenBrainzToken = disk.ListenBrainzToken; _stored.ListenBrainzUser = disk.ListenBrainzUser; _stored.ListenBrainzAuthenticationRequired = disk.ListenBrainzAuthenticationRequired; }
            }
            catch { }
        }

        private static Stored State()
        {
            lock (Gate)
            {
                if (_stored == null)
                {
                    if (File.Exists(StorePath))
                    {
                        _stored = ReadDisk();
                        // File presente ma illeggibile adesso: non lo si sostituisce con uno vuoto.
                        _loadFailed = _stored == null;
                        if (_loadFailed) Dbg.Warn("[SCROBBLE] settings unreadable: kept on disk, not overwritten");
                        else try { _diskStamp = File.GetLastWriteTimeUtc(StorePath); } catch { }
                    }
                    return _stored ??= new Stored();
                }
                long now = Environment.TickCount64;
                if (now - _diskCheckedTick > 2000) { _diskCheckedTick = now; AdoptDiskCredentials(); }
                return _stored;
            }
        }

        private static void Save()
        {
            lock (Gate)
            {
                try
                {
                    Stored state = State();
                    if (_loadFailed)
                    {
                        Stored? disk = File.Exists(StorePath) ? ReadDisk() : new Stored();
                        if (disk == null) { Dbg.Warn("[SCROBBLE] save skipped: settings still unreadable"); return; }
                        if (!_lastFmChangedHere) { state.LastFmSession = disk.LastFmSession; state.LastFmUser = disk.LastFmUser; state.LastFmPending = disk.LastFmPending; state.LastFmPendingUtc = disk.LastFmPendingUtc; state.LastFmAuthenticationRequired = disk.LastFmAuthenticationRequired; }
                        if (!_listenBrainzChangedHere) { state.ListenBrainzToken = disk.ListenBrainzToken; state.ListenBrainzUser = disk.ListenBrainzUser; state.ListenBrainzAuthenticationRequired = disk.ListenBrainzAuthenticationRequired; }
                        _loadFailed = false;
                    }
                    else AdoptDiskCredentials();
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    string temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temporary, StorePath, overwrite: true);
                    _diskStamp = File.GetLastWriteTimeUtc(StorePath);
                    _lastFmChangedHere = _listenBrainzChangedHere = false;
                }
                catch (Exception ex) { Dbg.Warn("[SCROBBLE] save failed: " + ex.Message); }
            }
        }

        public static bool LastFmAvailable => LastFmKey.Length > 0 && LastFmSecret.Length > 0;
        public static bool LastFmConnected => LastFmAvailable && !State().LastFmAuthenticationRequired && !string.IsNullOrEmpty(SecretVault.Unprotect(State().LastFmSession));
        public static string LastFmUser => State().LastFmUser ?? "";
        public static bool ListenBrainzConnected => !State().ListenBrainzAuthenticationRequired && !string.IsNullOrEmpty(SecretVault.Unprotect(State().ListenBrainzToken));
        public static string ListenBrainzUser => State().ListenBrainzUser ?? "";
        public static bool AnyConnected => LastFmConnected || ListenBrainzConnected;
        public static bool AnyEnabled => !string.IsNullOrEmpty(State().LastFmSession) || !string.IsNullOrEmpty(State().LastFmPending) || !string.IsNullOrEmpty(State().ListenBrainzToken);
        public static int QueuedCount { get { lock (Gate) return State().Queue.Count; } }

        public static string LastFmDeliveryStatus { get; private set; } = "";
        public static string ListenBrainzDeliveryStatus { get; private set; } = "";
        public static event Action? DeliveryChanged;
        private static Timer? _retryTimer;
        private static readonly CancellationTokenSource Lifetime = new();
        private static DateTime _nextLastFmRetryUtc, _nextListenBrainzRetryUtc;
        private static void NotifyDelivery() { try { DeliveryChanged?.Invoke(); } catch { } }

        public static void Start()
        {
            lock (Gate) _retryTimer ??= new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
            // Autorizzazione iniziata in una sessione precedente e non ancora conclusa: si continua ad aspettarla.
            if (!LastFmConnected && LastFmPendingToken is { } pending) WatchLastFmAuthorisation(pending);
        }
        public static void Stop()
        {
            lock (Gate) { _retryTimer?.Dispose(); _retryTimer = null; }
            Lifetime.Cancel();
            Save();
        }

        // ===== Last.fm =====

        private static string Sign(SortedDictionary<string, string> parameters)
        {
            var text = new StringBuilder();
            foreach (var pair in parameters) text.Append(pair.Key).Append(pair.Value);
            text.Append(LastFmSecret);
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
        }

        /// <summary>Chiamata firmata. Restituisce il documento della risposta e il codice d'errore di Last.fm (0 = riuscita).</summary>
        private static async Task<(JsonDocument? Document, int Error)> LastFmCallAsync(string method, IDictionary<string, string> arguments, bool post, CancellationToken ct)
        {
            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["method"] = method, ["api_key"] = LastFmKey };
            foreach (var pair in arguments) if (!string.IsNullOrEmpty(pair.Value)) parameters[pair.Key] = pair.Value;
            var form = new Dictionary<string, string>(parameters) { ["api_sig"] = Sign(parameters), ["format"] = "json" };
            using var request = post
                ? new HttpRequestMessage(HttpMethod.Post, LastFmApi) { Content = new FormUrlEncodedContent(form) }
                : new HttpRequestMessage(HttpMethod.Get, LastFmApi + "?" + string.Join("&", form.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonDocument document;
            try { document = JsonDocument.Parse(body); }
            catch { return (null, response.IsSuccessStatusCode ? -1 : (int)response.StatusCode); }
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Number)
            {
                int code = error.GetInt32();
                document.Dispose();
                return (null, code);
            }
            if (!response.IsSuccessStatusCode) { document.Dispose(); return (null, (int)response.StatusCode); }
            int number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n
                : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n) ? n : -1;
            if (method == "track.scrobble")
            {
                if (!document.RootElement.TryGetProperty("scrobbles", out var submitted) ||
                    !submitted.TryGetProperty("@attr", out var attributes) || !attributes.TryGetProperty("accepted", out var accepted))
                { document.Dispose(); return (null, -1); }
                if (number(accepted) < 1)
                {
                    int ignored = -1;
                    if (submitted.TryGetProperty("scrobble", out var entry) && entry.TryGetProperty("ignoredMessage", out var message) && message.TryGetProperty("code", out var code)) ignored = number(code);
                    document.Dispose(); return (null, ignored > 0 ? 1000 + ignored : -1);
                }
            }
            else if (method == "track.updateNowPlaying")
            {
                if (!document.RootElement.TryGetProperty("nowplaying", out var playing)) { document.Dispose(); return (null, -1); }
                if (playing.TryGetProperty("ignoredMessage", out var message) && message.TryGetProperty("code", out var code) && number(code) > 0)
                { int ignored = number(code); document.Dispose(); return (null, 1000 + ignored); }
            }
            return (document, 0);
        }

        /// <summary>Primo passo del collegamento: l'indirizzo da aprire nel browser e il gettone da confermare dopo.</summary>
        public static async Task<(string Url, string Token)?> LastFmBeginAsync(CancellationToken ct)
        {
            if (!LastFmAvailable) return null;
            var (document, _) = await LastFmCallAsync("auth.getToken", new Dictionary<string, string>(), post: false, ct).ConfigureAwait(false);
            using (document)
            {
                string token = document != null && document.RootElement.TryGetProperty("token", out var value) ? value.GetString() ?? "" : "";
                if (token.Length == 0) { Dbg.Warn("[SCROBBLE] Last.fm authorisation: no token from auth.getToken"); return null; }
                LastFmPendingToken = token;
                Dbg.Log("[SCROBBLE] Last.fm authorisation started: waiting for the browser", Dbg.LogLevel.Info);
                WatchLastFmAuthorisation(token);
                return ("https://www.last.fm/api/auth/?api_key=" + Uri.EscapeDataString(LastFmKey) + "&token=" + Uri.EscapeDataString(token), token);
            }
        }

        /// <summary>Il gettone in attesa di conferma (vale un'ora): resta qui anche se il foglio viene chiuso e riaperto.</summary>
        public static string? LastFmPendingToken
        {
            get => DateTime.UtcNow - State().LastFmPendingUtc < TimeSpan.FromHours(1) ? SecretVault.Unprotect(State().LastFmPending) : null;
            set
            {
                lock (Gate) { State().LastFmPending = value == null ? null : SecretVault.Protect(value); State().LastFmPendingUtc = DateTime.UtcNow; _lastFmChangedHere = true; }
                Save();
            }
        }

        /// <summary>Secondo passo, dopo che l'utente ha autorizzato nel browser. Vero se il collegamento e' riuscito.</summary>
        public static async Task<bool> LastFmCompleteAsync(string token, CancellationToken ct)
        {
            var (document, error) = await LastFmCallAsync("auth.getSession", new Dictionary<string, string> { ["token"] = token }, post: false, ct).ConfigureAwait(false);
            // Keep the browser authorisation across temporary failures; only an invalid token ends it.
            // 14 = "non ancora autorizzato nel browser": normale mentre si aspetta l'utente.
            if (error is not (0 or 14)) Dbg.Warn("[SCROBBLE] Last.fm authorisation not completed: error " + error);
            if (error is 4 or 15) LastFmPendingToken = null;
            using (document)
            {
                if (document == null || !document.RootElement.TryGetProperty("session", out var session)) return false;
                string key = session.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                string name = session.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (key.Length == 0) return false;
                lock (Gate) { State().LastFmSession = SecretVault.Protect(key); State().LastFmUser = name; State().LastFmAuthenticationRequired = false; _lastFmChangedHere = true; }
                Save();
                LastFmPendingToken = null;
                LastFmDeliveryStatus = ""; _nextLastFmRetryUtc = DateTime.MinValue; NotifyDelivery(); Flush();
                LastFmConnectedAtUtc = DateTime.UtcNow;
                Dbg.Log("[SCROBBLE] Last.fm connected as " + name, Dbg.LogLevel.Info);
                return true;
            }
        }

        // Il collegamento si chiudeva solo se si tornava sulla scheda e la si riattivava: chi autorizzava nel
        // browser e chiudeva la scheda (o tornava al player in un altro modo) restava scollegato, e dopo
        // un'ora il gettone scadeva. Ora, aperto il browser, il player chiede la sessione da solo ogni
        // pochi secondi finche' l'autorizzazione arriva (al massimo per l'ora di validita' del gettone).
        private static int _lastFmWatchSerial;
        private static void WatchLastFmAuthorisation(string token)
        {
            int serial = Interlocked.Increment(ref _lastFmWatchSerial);
            _ = Task.Run(async () =>
            {
                var started = DateTime.UtcNow;
                while (!Lifetime.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(58))
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(DateTime.UtcNow - started < TimeSpan.FromMinutes(5) ? 3 : 15), Lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    if (serial != Volatile.Read(ref _lastFmWatchSerial) || LastFmConnected || LastFmPendingToken != token) return;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        if (await LastFmCompleteAsync(token, timeout.Token).ConfigureAwait(false)) return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !Lifetime.IsCancellationRequested) { }
                }
            });
        }

        /// <summary>Quando e' arrivato l'ultimo collegamento: un clic subito dopo non deve scollegare.</summary>
        public static DateTime LastFmConnectedAtUtc { get; private set; }

        public static void LastFmDisconnect()
        {
            Dbg.Log("[SCROBBLE] Last.fm disconnected by the user", Dbg.LogLevel.Info);
            lock (Gate)
            {
                State().LastFmSession = null; State().LastFmUser = null; State().LastFmPending = null; State().LastFmAuthenticationRequired = false; _lastFmChangedHere = true;
                foreach (var item in State().Queue) item.LastFm = false;
                State().Queue.RemoveAll(item => !item.LastFm && !item.ListenBrainz);
            }
            Save();
        }

        // ===== ListenBrainz =====

        /// <summary>Verifica il token e lo salva. Restituisce il nome utente, oppure null se il token non e' valido.</summary>
        public static async Task<string?> ListenBrainzConnectAsync(string token, CancellationToken ct)
        {
            token = (token ?? "").Trim();
            if (token.Length < 8 || token.Any(char.IsWhiteSpace)) return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, ListenBrainzApi + "validate-token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("valid", out var valid) || valid.ValueKind != JsonValueKind.True) return null;
            string user = document.RootElement.TryGetProperty("user_name", out var name) ? name.GetString() ?? "" : "";
            lock (Gate) { State().ListenBrainzToken = SecretVault.Protect(token); State().ListenBrainzUser = user; State().ListenBrainzAuthenticationRequired = false; _listenBrainzChangedHere = true; }
            Save();
            ListenBrainzDeliveryStatus = ""; _nextListenBrainzRetryUtc = DateTime.MinValue; NotifyDelivery(); Flush();
            return user;
        }

        public static void ListenBrainzDisconnect()
        {
            lock (Gate)
            {
                State().ListenBrainzToken = null; State().ListenBrainzUser = null; State().ListenBrainzAuthenticationRequired = false; _listenBrainzChangedHere = true;
                foreach (var item in State().Queue) item.ListenBrainz = false;
                State().Queue.RemoveAll(item => !item.LastFm && !item.ListenBrainz);
            }
            Save();
        }

        private static async Task<HttpStatusCode> ListenBrainzSubmitAsync(string type, Pending listen, CancellationToken ct)
        {
            string? token = SecretVault.Unprotect(State().ListenBrainzToken);
            if (string.IsNullOrEmpty(token)) return HttpStatusCode.Unauthorized;
            var info = new Dictionary<string, object> { ["media_player"] = "Cinecore Player", ["submission_client"] = "Cinecore Player" };
            if (listen.Duration > 0) info["duration_ms"] = listen.Duration * 1000;
            var metadata = new Dictionary<string, object> { ["artist_name"] = listen.Artist, ["track_name"] = listen.Title, ["additional_info"] = info };
            if (listen.Album.Length > 0) metadata["release_name"] = listen.Album;
            var entry = new Dictionary<string, object> { ["track_metadata"] = metadata };
            if (type != "playing_now") entry["listened_at"] = listen.Timestamp;
            using var request = new HttpRequestMessage(HttpMethod.Post, ListenBrainzApi + "submit-listens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { ["listen_type"] = type, ["payload"] = new[] { entry } }), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string reason = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Dbg.Warn($"[SCROBBLE] ListenBrainz {type}: {(int)response.StatusCode} {(reason.Length > 160 ? reason[..160] : reason)}");
            }
            else
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (!document.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ok") return HttpStatusCode.BadGateway;
                }
                catch (JsonException) { return HttpStatusCode.BadGateway; }
                Dbg.Log($"[SCROBBLE] ListenBrainz {type}: sent", Dbg.LogLevel.Info);
            }
            return response.StatusCode;
        }

        // ===== uso dal player =====

        private static Pending ToPending(Track track, DateTime startedUtc) => new()
        {
            Artist = track.Artist.Trim(), Title = track.Title.Trim(), Album = track.Album.Trim(),
            Duration = double.IsFinite(track.DurationSeconds) ? (int)Math.Round(Math.Clamp(track.DurationSeconds, 0, 86400)) : 0,
            Timestamp = new DateTimeOffset(startedUtc).ToUnixTimeSeconds()
        };

        /// <summary>"In ascolto ora": un avviso senza conseguenze, se non parte non si riprova.</summary>
        public static void NowPlaying(Track track)
        {
            if (!AnyEnabled || string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title)) return;
            var listen = ToPending(track, DateTime.UtcNow);
            _ = Task.Run(() => NowPlayingCoreAsync(listen));
        }

        private static async Task NowPlayingCoreAsync(Pending listen)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(50));
            async Task LastFm()
            {
                if (!LastFmConnected) return;
                try
                {
                    var (document, error) = await LastFmCallAsync("track.updateNowPlaying", LastFmTrackArguments(listen, false), true, timeout.Token).ConfigureAwait(false);
                    document?.Dispose();
                    if (error != 0) { LastFmDeliveryStatus = AppLanguage.T("Brano in ascolto non inviato: errore ", "Now playing not sent: error ") + error; NotifyDelivery(); Dbg.Warn("[SCROBBLE] Last.fm now playing refused: error " + error); }
                    else Dbg.Log("[SCROBBLE] Last.fm now playing: sent to " + State().LastFmUser, Dbg.LogLevel.Info);
                }
                catch (Exception ex) { LastFmDeliveryStatus = AppLanguage.T("In attesa di connessione", "Waiting for connection"); NotifyDelivery(); Dbg.Warn("[SCROBBLE] Last.fm now playing: " + ex.GetType().Name); }
            }
            async Task ListenBrainz()
            {
                if (!ListenBrainzConnected) return;
                try
                {
                    var status = await ListenBrainzSubmitAsync("playing_now", listen, timeout.Token).ConfigureAwait(false);
                    if ((int)status >= 300) { ListenBrainzDeliveryStatus = AppLanguage.T("Brano in ascolto non inviato: errore ", "Now playing not sent: error ") + (int)status; NotifyDelivery(); }
                }
                catch (Exception ex) { ListenBrainzDeliveryStatus = AppLanguage.T("In attesa di connessione", "Waiting for connection"); NotifyDelivery(); Dbg.Warn("[SCROBBLE] ListenBrainz now playing: " + ex.GetType().Name); }
            }
            await Task.WhenAll(LastFm(), ListenBrainz()).ConfigureAwait(false);
        }

        /// <summary>Il brano e' stato ascoltato abbastanza: entra in coda e parte appena possibile.</summary>
        public static void Scrobble(Track track, DateTime startedUtc)
        {
            if (!AnyEnabled || string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title)) return;
            var listen = ToPending(track, startedUtc);
            listen.LastFm = !string.IsNullOrEmpty(State().LastFmSession) || !string.IsNullOrEmpty(State().LastFmPending);
            listen.ListenBrainz = !string.IsNullOrEmpty(State().ListenBrainzToken);
            lock (Gate)
            {
                var queue = State().Queue;
                queue.Add(listen);
                if (queue.Count > QueueLimit) queue.RemoveRange(0, queue.Count - QueueLimit);
            }
            Save();
            Flush();
        }

        /// <summary>Manda cio' che e' in coda (anche all'avvio, per gli ascolti rimasti indietro).</summary>
        public static void Flush()
        {
            if (!Lifetime.IsCancellationRequested && QueuedCount > 0) _ = Task.Run(FlushCoreAsync);
        }

        private static void CompleteDelivery(Pending item, bool lastFm)
        {
            lock (Gate)
            {
                if (lastFm) item.LastFm = false; else item.ListenBrainz = false;
                State().Queue.RemoveAll(queued => ReferenceEquals(queued, item) && !queued.LastFm && !queued.ListenBrainz);
            }
            Save(); // Persist each acknowledgement, even if the other provider stalls or the app exits.
            NotifyDelivery();
        }

        private static async Task FlushCoreAsync()
        {
            if (!await FlushGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                List<Pending> items;
                lock (Gate) items = State().Queue.OrderBy(x => x.Timestamp).ToList();
                async Task LastFm()
                {
                    if (DateTime.UtcNow < _nextLastFmRetryUtc || !LastFmConnected) return;
                    foreach (var item in items.Where(x => x.LastFm))
                    {
                        try
                        {
                            var (document, error) = await LastFmCallAsync("track.scrobble", LastFmTrackArguments(item, true), true, timeout.Token).ConfigureAwait(false);
                            document?.Dispose();
                            if (error == 0)
                            {
                                Dbg.Log("[SCROBBLE] Last.fm scrobble: sent to " + State().LastFmUser, Dbg.LogLevel.Info);
                                CompleteDelivery(item, true);
                                LastFmDeliveryStatus = AppLanguage.T("Ultimo ascolto inviato alle ", "Last listen sent at ") + DateTime.Now.ToString("HH:mm");
                                NotifyDelivery(); continue;
                            }
                            LastFmDeliveryStatus = AppLanguage.T("Ascolto non inviato: errore ", "Listen not sent: error ") + error;
                            Dbg.Warn("[SCROBBLE] Last.fm refused a listen: " + error);
                            if (error == 9)
                            {
                                lock (Gate) { State().LastFmAuthenticationRequired = true; _lastFmChangedHere = true; }
                                LastFmDeliveryStatus = AppLanguage.T("Accesso scaduto: ricollega Last.fm", "Session expired: reconnect Last.fm");
                                Save(); NotifyDelivery(); return; // Keep pending listens for reauthentication.
                            }
                            if (error is 11 or 16 or 29 or 403 or 408 or 429 or 1004 or 1005 || error < 0 || error >= 500 && error < 1000)
                            {
                                _nextLastFmRetryUtc = error == 1005 ? DateTime.UtcNow.Date.AddDays(1) : DateTime.UtcNow.AddMinutes(1);
                                NotifyDelivery(); return;
                            }
                            _nextLastFmRetryUtc = DateTime.UtcNow.AddMinutes(15);
                            NotifyDelivery(); return;
                        }
                        catch (Exception ex)
                        {
                            LastFmDeliveryStatus = AppLanguage.T("Invio in attesa di connessione", "Delivery waiting for connection");
                            _nextLastFmRetryUtc = DateTime.UtcNow.AddMinutes(1); NotifyDelivery();
                            Dbg.Warn("[SCROBBLE] Last.fm delivery: " + ex.GetType().Name); return;
                        }
                    }
                }
                async Task ListenBrainz()
                {
                    if (DateTime.UtcNow < _nextListenBrainzRetryUtc || !ListenBrainzConnected) return;
                    foreach (var item in items.Where(x => x.ListenBrainz))
                    {
                        try
                        {
                            var status = await ListenBrainzSubmitAsync("single", item, timeout.Token).ConfigureAwait(false);
                            if ((int)status is >= 200 and < 300)
                            {
                                CompleteDelivery(item, false);
                                ListenBrainzDeliveryStatus = AppLanguage.T("Ultimo ascolto inviato alle ", "Last listen sent at ") + DateTime.Now.ToString("HH:mm");
                                NotifyDelivery(); continue;
                            }
                            ListenBrainzDeliveryStatus = AppLanguage.T("Ascolto non inviato: errore ", "Listen not sent: error ") + (int)status;
                            if (status == HttpStatusCode.Unauthorized)
                            {
                                lock (Gate) { State().ListenBrainzAuthenticationRequired = true; _listenBrainzChangedHere = true; }
                                ListenBrainzDeliveryStatus = AppLanguage.T("Accesso scaduto: ricollega ListenBrainz", "Token expired: reconnect ListenBrainz");
                                Save(); NotifyDelivery(); return;
                            }
                            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.Forbidden || (int)status >= 500)
                            { _nextListenBrainzRetryUtc = DateTime.UtcNow.AddMinutes(1); NotifyDelivery(); return; }
                            _nextListenBrainzRetryUtc = DateTime.UtcNow.AddMinutes(15);
                            NotifyDelivery(); return;
                        }
                        catch (Exception ex)
                        {
                            ListenBrainzDeliveryStatus = AppLanguage.T("Invio in attesa di connessione", "Delivery waiting for connection");
                            _nextListenBrainzRetryUtc = DateTime.UtcNow.AddMinutes(1); NotifyDelivery();
                            Dbg.Warn("[SCROBBLE] ListenBrainz delivery: " + ex.GetType().Name); return;
                        }
                    }
                }
                await Task.WhenAll(LastFm(), ListenBrainz()).ConfigureAwait(false);
            }
            catch (Exception ex) { Dbg.Warn("[SCROBBLE] flush: " + ex.GetType().Name); }
            finally { Save(); FlushGate.Release(); }
        }

        private static Dictionary<string, string> LastFmTrackArguments(Pending listen, bool withTimestamp)
        {
            var arguments = new Dictionary<string, string>
            {
                ["sk"] = SecretVault.Unprotect(State().LastFmSession) ?? "",
                ["artist"] = listen.Artist,
                ["track"] = listen.Title,
                ["album"] = listen.Album
            };
            if (listen.Duration > 0) arguments["duration"] = listen.Duration.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (withTimestamp) arguments["timestamp"] = listen.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return arguments;
        }
    }
}
