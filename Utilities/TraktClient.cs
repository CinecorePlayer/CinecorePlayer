#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Trakt: collegamento dell'account con un codice (nessuna password passa dal player),
    /// consigli personali, invio di cio' che e' stato visto. A Trakt arrivano solo identificativi
    /// pubblici dei titoli (TMDb), date di visione e voti: mai nomi di file o percorsi.
    /// </summary>
    internal static class TraktClient
    {
        public sealed record DeviceCode(string UserCode, string VerificationUrl, string Code, int IntervalSeconds, int ExpiresSeconds);
        public sealed record Title(string Kind, string Name, int? Year, int TmdbId, string? Slug);
        public sealed record WatchedEpisode(int ShowTmdbId, int Season, int Episode);
        public sealed record HistoryItem(bool IsEpisode, int TmdbId, int Season, int Episode, DateTime WatchedAtUtc, int Rating);
        public sealed record SyncResult(int Movies, int Episodes, int Ratings);

        private sealed class Stored
        {
            public string? Tokens { get; set; }           // cifrato (SecretVault): access|refresh|scadenza
            public string? User { get; set; }
            public bool MarkWatched { get; set; } = true;
            public DateTime ConnectedAtUtc { get; set; }
        }

        private const string Api = "https://api.trakt.tv";
        private static readonly HttpClient Http = CreateClient();
        private static readonly SemaphoreSlim RefreshGate = new(1, 1);
        private static readonly object Gate = new();
        private static Stored? _stored;
        private static string? _access, _refresh;
        private static DateTime _expiresUtc;

        private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "trakt.json");
        private static string ClientId => SecretVault.BuiltIn("trakt.id") ?? "";
        private static string ClientSecret => SecretVault.BuiltIn("trakt.secret") ?? "";

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer/0.2");
            return client;
        }

        public static bool IsAvailable => ClientId.Length > 0 && ClientSecret.Length > 0;

        private static Stored State()
        {
            lock (Gate)
            {
                if (_stored != null) return _stored;
                try { _stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(StorePath)); } catch { }
                _stored ??= new Stored();
                string[] parts = (SecretVault.Unprotect(_stored.Tokens) ?? "").Split('|');
                if (parts.Length == 3 && long.TryParse(parts[2], out long ticks))
                {
                    _access = parts[0];
                    _refresh = parts[1];
                    _expiresUtc = new DateTime(ticks, DateTimeKind.Utc);
                }
                return _stored;
            }
        }

        private static void Save()
        {
            lock (Gate)
            {
                var stored = _stored ??= new Stored();
                stored.Tokens = _access == null || _refresh == null ? null : SecretVault.Protect(_access + "|" + _refresh + "|" + _expiresUtc.Ticks);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    string temp = StorePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(stored));
                    File.Move(temp, StorePath, true);
                }
                catch (Exception ex) { Dbg.Warn("[TRAKT] save failed: " + ex.Message); }
            }
        }

        public static bool IsConnected { get { State(); lock (Gate) return _access != null; } }
        public static string UserName => State().User ?? "";
        public static DateTime ConnectedAtUtc => State().ConnectedAtUtc;
        public static bool MarkWatched
        {
            get => State().MarkWatched;
            set { State().MarkWatched = value; Save(); }
        }

        private static HttpRequestMessage Request(HttpMethod method, string path, object? body, bool authorized)
        {
            var request = new HttpRequestMessage(method, Api + path);
            request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
            request.Headers.TryAddWithoutValidation("trakt-api-key", ClientId);
            if (authorized)
                lock (Gate) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
            if (body != null) request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return request;
        }

        // ----- Collegamento -----

        public static async Task<DeviceCode> BeginConnectAsync(CancellationToken ct)
        {
            using var request = Request(HttpMethod.Post, "/oauth/device/code", new { client_id = ClientId }, authorized: false);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            return new DeviceCode(root.GetProperty("user_code").GetString()!, root.GetProperty("verification_url").GetString()!,
                root.GetProperty("device_code").GetString()!, Math.Max(1, root.GetProperty("interval").GetInt32()), root.GetProperty("expires_in").GetInt32());
        }

        /// <summary>Aspetta che l'utente inserisca il codice sul sito. False se il codice scade o viene rifiutato.</summary>
        public static async Task<bool> WaitForConnectAsync(DeviceCode code, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresSeconds);
            int interval = code.IntervalSeconds;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
                using var request = Request(HttpMethod.Post, "/oauth/device/token", new { code = code.Code, client_id = ClientId, client_secret = ClientSecret }, authorized: false);
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    StoreTokens(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    State().ConnectedAtUtc = DateTime.UtcNow;
                    try { State().User = await ReadUserNameAsync(ct).ConfigureAwait(false); } catch (Exception ex) { Dbg.Warn("[TRAKT] user: " + ex.Message); }
                    Save();
                    return true;
                }
                switch ((int)response.StatusCode)
                {
                    case 400: break;                    // l'utente non ha ancora inserito il codice
                    case 429: interval++; break;        // troppo veloce
                    default: return false;              // 404 non valido, 409 gia' usato, 410 scaduto, 418 rifiutato
                }
            }
            return false;
        }

        private static void StoreTokens(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            lock (Gate)
            {
                _access = root.GetProperty("access_token").GetString();
                _refresh = root.GetProperty("refresh_token").GetString();
                long created = root.TryGetProperty("created_at", out var c) && c.TryGetInt64(out long value) ? value : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int seconds) ? seconds : 86400;
                _expiresUtc = DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime.AddSeconds(expires);
            }
            Save();
        }

        private static async Task<string?> ReadUserNameAsync(CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "/users/settings", null, ct).ConfigureAwait(false));
            var user = doc.RootElement.GetProperty("user");
            string? name = user.TryGetProperty("name", out var n) ? n.GetString() : null;
            return string.IsNullOrWhiteSpace(name) ? user.GetProperty("username").GetString() : name;
        }

        public static async Task DisconnectAsync(CancellationToken ct)
        {
            string? token;
            lock (Gate) token = _access;
            if (token != null)
            {
                try
                {
                    using var request = Request(HttpMethod.Post, "/oauth/revoke", new { token, client_id = ClientId, client_secret = ClientSecret }, authorized: false);
                    using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex) { Dbg.Warn("[TRAKT] revoke: " + ex.Message); }
            }
            lock (Gate) { _access = _refresh = null; State().User = null; }
            Save();
        }

        private static async Task<bool> RefreshAsync(CancellationToken ct)
        {
            await RefreshGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string? refresh;
                lock (Gate)
                {
                    if (_access != null && _expiresUtc - DateTime.UtcNow > TimeSpan.FromHours(1)) return true; // rinnovato da un'altra richiesta
                    refresh = _refresh;
                }
                if (refresh == null) return false;
                using var request = Request(HttpMethod.Post, "/oauth/token", new
                {
                    refresh_token = refresh, client_id = ClientId, client_secret = ClientSecret,
                    redirect_uri = "urn:ietf:wg:oauth:2.0:oob", grant_type = "refresh_token"
                }, authorized: false);
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // Accesso revocato dal sito: il player torna "non collegato".
                    if ((int)response.StatusCode is 400 or 401) { lock (Gate) _access = _refresh = null; Save(); }
                    return false;
                }
                StoreTokens(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return true;
            }
            finally { RefreshGate.Release(); }
        }

        private static async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            State();
            bool nearExpiry;
            lock (Gate)
            {
                if (_access == null) throw new InvalidOperationException("Trakt non collegato");
                nearExpiry = _expiresUtc - DateTime.UtcNow < TimeSpan.FromHours(1);
            }
            if (nearExpiry) await RefreshAsync(ct).ConfigureAwait(false);
            string? payload = body == null ? null : body as string ?? JsonSerializer.Serialize(body);
            for (int attempt = 0; ; attempt++)
            {
                using var request = Request(method, path, payload, authorized: true);
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && await RefreshAsync(ct).ConfigureAwait(false)) continue;
                if ((int)response.StatusCode == 429 && attempt < 2)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }

        // ----- Consigli -----

        /// <param name="kind">"movies" oppure "shows".</param>
        public static async Task<List<Title>> RecommendationsAsync(string kind, int limit, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, $"/recommendations/{kind}?limit={limit}&ignore_collected=false&ignore_watchlisted=false", null, ct).ConfigureAwait(false));
            var result = new List<Title>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // La risposta puo' essere l'oggetto del titolo oppure averlo sotto "movie"/"show".
                var title = item.TryGetProperty(kind == "movies" ? "movie" : "show", out var nested) ? nested : item;
                if (!title.TryGetProperty("ids", out var ids) || !ids.TryGetProperty("tmdb", out var tmdb) || !tmdb.TryGetInt32(out int id)) continue;
                result.Add(new Title(kind == "movies" ? "movie" : "tv", title.GetProperty("title").GetString() ?? "",
                    title.TryGetProperty("year", out var year) && year.TryGetInt32(out int y) ? y : null, id,
                    ids.TryGetProperty("slug", out var slug) ? slug.GetString() : null));
            }
            return result;
        }

        /// <summary>Film affini a un film dato (per identificativo TMDb). Non serve un account collegato.</summary>
        public static async Task<List<Title>> RelatedMoviesAsync(int tmdbId, CancellationToken ct)
        {
            var result = new List<Title>();
            async Task<string> Get(string path)
            {
                using var request = Request(HttpMethod.Get, path, null, authorized: false);
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            int traktId;
            using (var found = JsonDocument.Parse(await Get($"/search/tmdb/{tmdbId}?type=movie").ConfigureAwait(false)))
            {
                if (found.RootElement.GetArrayLength() == 0 || !found.RootElement[0].TryGetProperty("movie", out var movie) ||
                    !movie.GetProperty("ids").TryGetProperty("trakt", out var id) || !id.TryGetInt32(out traktId)) return result;
            }
            using var doc = JsonDocument.Parse(await Get($"/movies/{traktId}/related?limit=40").ConfigureAwait(false));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("ids", out var ids) || !ids.TryGetProperty("tmdb", out var tmdb) || !tmdb.TryGetInt32(out int related)) continue;
                result.Add(new Title("movie", item.GetProperty("title").GetString() ?? "",
                    item.TryGetProperty("year", out var year) && year.TryGetInt32(out int y) ? y : null, related,
                    ids.TryGetProperty("slug", out var slug) ? slug.GetString() : null));
            }
            return result;
        }

        // ----- Visti -----

        public static async Task<HashSet<int>> WatchedMoviesAsync(CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "/sync/watched/movies", null, ct).ConfigureAwait(false));
            var result = new HashSet<int>();
            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.TryGetProperty("movie", out var movie) && movie.GetProperty("ids").TryGetProperty("tmdb", out var tmdb) && tmdb.TryGetInt32(out int id))
                    result.Add(id);
            return result;
        }

        public static async Task<HashSet<WatchedEpisode>> WatchedEpisodesAsync(CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "/sync/watched/shows", null, ct).ConfigureAwait(false));
            var result = new HashSet<WatchedEpisode>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("show", out var show) || !show.GetProperty("ids").TryGetProperty("tmdb", out var tmdb) || !tmdb.TryGetInt32(out int id)) continue;
                if (!item.TryGetProperty("seasons", out var seasons)) continue;
                foreach (var season in seasons.EnumerateArray())
                    foreach (var episode in season.GetProperty("episodes").EnumerateArray())
                        result.Add(new WatchedEpisode(id, season.GetProperty("number").GetInt32(), episode.GetProperty("number").GetInt32()));
            }
            return result;
        }

        /// <summary>Aggiunge visioni e voti. Chi chiama ha gia' tolto cio' che su Trakt c'e' gia'.</summary>
        public static async Task<SyncResult> AddHistoryAsync(IReadOnlyCollection<HistoryItem> items, CancellationToken ct)
        {
            if (items.Count == 0) return new SyncResult(0, 0, 0);
            static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.000Z");
            var movies = new JsonArray();
            var ratedMovies = new JsonArray();
            foreach (var item in items.Where(i => !i.IsEpisode))
            {
                movies.Add(new JsonObject { ["watched_at"] = Stamp(item.WatchedAtUtc), ["ids"] = new JsonObject { ["tmdb"] = item.TmdbId } });
                if (item.Rating is >= 1 and <= 10)
                    ratedMovies.Add(new JsonObject { ["rating"] = item.Rating, ["rated_at"] = Stamp(item.WatchedAtUtc), ["ids"] = new JsonObject { ["tmdb"] = item.TmdbId } });
            }
            var shows = new JsonArray();
            foreach (var show in items.Where(i => i.IsEpisode).GroupBy(i => i.TmdbId))
            {
                var seasons = new JsonArray();
                foreach (var season in show.GroupBy(i => i.Season))
                {
                    var episodes = new JsonArray();
                    foreach (var episode in season) episodes.Add(new JsonObject { ["number"] = episode.Episode, ["watched_at"] = Stamp(episode.WatchedAtUtc) });
                    seasons.Add(new JsonObject { ["number"] = season.Key, ["episodes"] = episodes });
                }
                shows.Add(new JsonObject { ["ids"] = new JsonObject { ["tmdb"] = show.Key }, ["seasons"] = seasons });
            }

            int addedMovies = 0, addedEpisodes = 0, rated = 0;
            using (var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Post, "/sync/history", new JsonObject { ["movies"] = movies, ["shows"] = shows }.ToJsonString(), ct).ConfigureAwait(false)))
                if (doc.RootElement.TryGetProperty("added", out var added))
                {
                    addedMovies = added.TryGetProperty("movies", out var m) ? m.GetInt32() : 0;
                    addedEpisodes = added.TryGetProperty("episodes", out var e) ? e.GetInt32() : 0;
                }
            if (ratedMovies.Count > 0)
                using (var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Post, "/sync/ratings", new JsonObject { ["movies"] = ratedMovies }.ToJsonString(), ct).ConfigureAwait(false)))
                    if (doc.RootElement.TryGetProperty("added", out var added) && added.TryGetProperty("movies", out var m)) rated = m.GetInt32();
            return new SyncResult(addedMovies, addedEpisodes, rated);
        }
    }
}
