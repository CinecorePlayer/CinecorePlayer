#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Ricerca e scaricamento di sottotitoli.
    /// - Film: YIFY Subtitles, per ID IMDb (nessun account; e' la lettura delle pagine del sito,
    ///   quindi puo' rompersi se il sito cambia aspetto: per questo ci sono piu' indirizzi).
    /// - Serie: Gestdown, che espone il catalogo Addic7ed con una vera API, senza chiave.
    /// - OpenSubtitles: solo se l'utente inserisce la propria chiave (e, volendo, il proprio
    ///   account): i limiti di scaricamento sono i suoi.
    /// Si inviano solo ID e titoli, mai percorsi o nomi di file locali.
    /// </summary>
    internal static class SubtitleSources
    {
        public sealed record Query(string Title, int? Year, string? ImdbId, int? TmdbId, bool IsEpisode, string? SeriesTitle, int? Season, int? Episode, string Language);

        public sealed record Candidate(string Source, string Name, string Language, string Detail, int Rank, string Token);

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CinecorePlayer/0.2");
            return client;
        }

        /// <summary>
        /// Languages offered by the search. Code: what OpenSubtitles expects; English: the name
        /// YIFY and Addic7ed use. Any language outside the old five used to be searched as English.
        /// </summary>
        public static readonly (string Code, string Italian, string English)[] AllLanguages =
        {
            ("it", "Italiano", "Italian"), ("en", "Inglese", "English"), ("fr", "Francese", "French"), ("es", "Spagnolo", "Spanish"),
            ("de", "Tedesco", "German"), ("pt-pt", "Portoghese", "Portuguese"), ("pt-br", "Portoghese (Brasile)", "Brazilian Portuguese"),
            ("nl", "Olandese", "Dutch"), ("pl", "Polacco", "Polish"), ("ru", "Russo", "Russian"), ("uk", "Ucraino", "Ukrainian"),
            ("ja", "Giapponese", "Japanese"), ("ko", "Coreano", "Korean"), ("zh-cn", "Cinese", "Chinese"), ("ar", "Arabo", "Arabic"),
            ("he", "Ebraico", "Hebrew"), ("tr", "Turco", "Turkish"), ("el", "Greco", "Greek"), ("sv", "Svedese", "Swedish"),
            ("no", "Norvegese", "Norwegian"), ("da", "Danese", "Danish"), ("fi", "Finlandese", "Finnish"), ("cs", "Ceco", "Czech"),
            ("sk", "Slovacco", "Slovak"), ("hu", "Ungherese", "Hungarian"), ("ro", "Rumeno", "Romanian"), ("bg", "Bulgaro", "Bulgarian"),
            ("hr", "Croato", "Croatian"), ("sr", "Serbo", "Serbian"), ("sl", "Sloveno", "Slovenian"), ("hi", "Hindi", "Hindi"),
            ("th", "Thailandese", "Thai"), ("vi", "Vietnamita", "Vietnamese"), ("id", "Indonesiano", "Indonesian")
        };

        public static string LanguageName(string code)
        {
            foreach (var language in AllLanguages)
                if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)) return language.English;
            return "English";
        }

        public static string LanguageLabel(string code, bool english)
        {
            foreach (var language in AllLanguages)
                if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)) return english ? language.English : language.Italian;
            return code;
        }

        private static string DefaultLanguagePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "subtitle-language.txt");
        private static string? _defaultLanguage;

        /// <summary>Language the subtitle search opens on (Settings › General); empty until chosen: the interface language.</summary>
        public static string DefaultLanguage
        {
            get
            {
                if (_defaultLanguage == null)
                {
                    try { _defaultLanguage = System.IO.File.Exists(DefaultLanguagePath) ? System.IO.File.ReadAllText(DefaultLanguagePath).Trim() : string.Empty; }
                    catch { _defaultLanguage = string.Empty; }
                    if (!AllLanguages.Any(language => language.Code == _defaultLanguage)) _defaultLanguage = string.Empty;
                }
                return _defaultLanguage;
            }
            set
            {
                _defaultLanguage = AllLanguages.Any(language => language.Code == value) ? value : string.Empty;
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DefaultLanguagePath)!);
                    System.IO.File.WriteAllText(DefaultLanguagePath, _defaultLanguage);
                }
                catch (Exception ex) { Dbg.Warn("[SUBS] default language save failed: " + ex.Message); }
            }
        }

        public static async Task<List<Candidate>> SearchAsync(Query query, CancellationToken ct)
        {
            var tasks = new List<Task<List<Candidate>>>
            {
                Guard(query.IsEpisode ? GestdownSearchAsync(query, ct) : YifySearchAsync(query, ct))
            };
            if (OpenSubtitlesAccount.Load() is { } account) tasks.Add(Guard(OpenSubtitlesSearchAsync(query, account, ct)));
            var all = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(list => list).ToList();
            return all.OrderByDescending(candidate => candidate.Rank).ToList();

            static async Task<List<Candidate>> Guard(Task<List<Candidate>> task)
            {
                try { return await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Dbg.Warn("[SUBS] search failed: " + ex.Message); return new List<Candidate>(); }
            }
        }

        /// <summary>Scarica il sottotitolo scelto e ne restituisce il testo (.srt).</summary>
        public static async Task<string> DownloadAsync(Candidate candidate, CancellationToken ct)
        {
            byte[] bytes = candidate.Source switch
            {
                "YIFY" => await YifyDownloadAsync(candidate.Token, ct).ConfigureAwait(false),
                "Addic7ed" => await Http.GetByteArrayAsync("https://api.gestdown.info" + candidate.Token, ct).ConfigureAwait(false),
                "OpenSubtitles" => await OpenSubtitlesDownloadAsync(candidate.Token, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException("unknown source")
            };
            if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K') bytes = ExtractSrt(bytes);
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("subtitle too large");
            string text = SubtitleFile.Decode(bytes);
            if (SubtitleFile.Parse(text).Count < 4) throw new InvalidDataException("not a subtitle file");
            return text;
        }

        // L'archivio arriva da fuori: si legge in memoria solo la voce .srt piu' grande,
        // senza estrarre nulla su disco (nessun nome di file dell'archivio viene usato come percorso).
        private static byte[] ExtractSrt(byte[] zip)
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entry = archive.Entries.Where(e => e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) && e.Length is > 0 and < 8 * 1024 * 1024)
                .OrderByDescending(e => e.Length).FirstOrDefault() ?? throw new InvalidDataException("no .srt in archive");
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        // ===== YIFY Subtitles (film) =====
        private static readonly string[] YifyHosts = { "https://yifysubtitles.ch", "https://yts-subs.com", "https://yifysubtitles.me" };

        private static async Task<List<Candidate>> YifySearchAsync(Query query, CancellationToken ct)
        {
            var list = new List<Candidate>();
            if (string.IsNullOrWhiteSpace(query.ImdbId) || !Regex.IsMatch(query.ImdbId, @"^tt\d{5,10}$")) return list;
            string wanted = LanguageName(query.Language);
            foreach (string host in YifyHosts)
            {
                string html;
                try { html = await Http.GetStringAsync(host + "/movie-imdb/" + query.ImdbId, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                foreach (Match row in Regex.Matches(html, @"<tr data-id=""\d+"">(.*?)</tr>", RegexOptions.Singleline))
                {
                    string body = row.Groups[1].Value;
                    var language = Regex.Match(body, @"class=""sub-lang"">([^<]+)<");
                    var link = Regex.Match(body, @"href=""(/subtitles/[a-z0-9\-]+)""");
                    if (!language.Success || !link.Success || !language.Groups[1].Value.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    int rating = int.TryParse(Regex.Match(body, @"rating-cell"">\s*<span[^>]*>(-?\d+)<").Groups[1].Value, out int parsed) ? parsed : 0;
                    string name = WebUtility.HtmlDecode(Regex.Replace(Regex.Match(body, @"<a href=""/subtitles/[^""]+"">(.*?)</a>", RegexOptions.Singleline).Groups[1].Value, "<[^>]+>", " "));
                    name = Regex.Replace(name, @"\s+", " ").Trim();
                    if (name.StartsWith("subtitle ", StringComparison.OrdinalIgnoreCase)) name = name[9..];
                    string uploader = WebUtility.HtmlDecode(Regex.Match(body, @"uploader-cell"">\s*<a[^>]*>([^<]*)<").Groups[1].Value).Trim();
                    bool impaired = body.Contains("hi-subtitle", StringComparison.OrdinalIgnoreCase);
                    list.Add(new Candidate("YIFY", name.Length > 0 ? name : query.Title, query.Language,
                        (rating != 0 ? "voto " + rating.ToString("+0;-0", CultureInfo.InvariantCulture) : "senza voti") + (uploader.Length > 0 ? "  ·  " + uploader : "") + (impaired ? "  ·  non udenti" : ""),
                        1000 + rating * 10 - (impaired ? 15 : 0), host + link.Groups[1].Value));
                }
                if (list.Count > 0 || html.Contains("movie-imdb", StringComparison.OrdinalIgnoreCase) || html.Contains("sub-lang", StringComparison.OrdinalIgnoreCase)) break;
            }
            return list;
        }

        private static async Task<byte[]> YifyDownloadAsync(string pageUrl, CancellationToken ct)
        {
            if (!YifyHosts.Any(host => pageUrl.StartsWith(host + "/subtitles/", StringComparison.Ordinal))) throw new InvalidOperationException("unexpected address");
            // Il sito consegna l'archivio solo a chi arriva dalla pagina del sottotitolo.
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl.Replace("/subtitles/", "/subtitle/") + ".zip");
            request.Headers.Referrer = new Uri(pageUrl);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        // ===== Gestdown / Addic7ed (serie) =====
        private static async Task<List<Candidate>> GestdownSearchAsync(Query query, CancellationToken ct)
        {
            var list = new List<Candidate>();
            string series = string.IsNullOrWhiteSpace(query.SeriesTitle) ? query.Title : query.SeriesTitle!;
            if (string.IsNullOrWhiteSpace(series) || query.Season is not int season || query.Episode is not int episode) return list;

            // Ricerca per nome; fra i risultati si preferisce quello con lo stesso ID TMDb.
            string? showId = await FirstShowIdAsync("https://api.gestdown.info/shows/search/" + Uri.EscapeDataString(series), query.TmdbId, ct).ConfigureAwait(false);
            if (showId == null) return list;

            using var response = await Http.GetAsync($"https://api.gestdown.info/subtitles/get/{showId}/{season}/{episode}/{LanguageName(query.Language)}", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return list;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("matchingSubtitles", out var items) || items.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in items.EnumerateArray())
            {
                string uri = item.TryGetProperty("downloadUri", out var u) ? u.GetString() ?? "" : "";
                if (!Regex.IsMatch(uri, @"^/subtitles/download/[0-9a-fA-F\-]{36}$")) continue;
                bool completed = !item.TryGetProperty("completed", out var c) || c.ValueKind != JsonValueKind.False;
                if (!completed) continue;
                string version = item.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                int downloads = item.TryGetProperty("downloadCount", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
                bool impaired = item.TryGetProperty("hearingImpaired", out var h) && h.ValueKind == JsonValueKind.True;
                list.Add(new Candidate("Addic7ed", $"{series} S{season:00}E{episode:00}" + (version.Length > 0 ? "  ·  " + version : ""), query.Language,
                    downloads.ToString("N0", CultureInfo.CurrentCulture) + " download" + (impaired ? "  ·  non udenti" : ""),
                    1000 + Math.Min(400, downloads) - (impaired ? 15 : 0), uri));
            }
            return list;
        }

        private static async Task<string?> FirstShowIdAsync(string url, int? preferTmdb, CancellationToken ct)
        {
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("shows", out var shows) || shows.ValueKind != JsonValueKind.Array) return null;
            string? first = null;
            foreach (var show in shows.EnumerateArray())
            {
                string? id = show.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (id == null || !Regex.IsMatch(id, @"^[0-9a-fA-F\-]{36}$")) continue;
                first ??= id;
                if (preferTmdb is int wanted && show.TryGetProperty("tmdbId", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == wanted) return id;
            }
            return first;
        }

        // ===== OpenSubtitles (facoltativo, con la chiave dell'utente) =====
        private static string? _openSubtitlesToken;
        private static string _openSubtitlesTokenFor = "";

        private static HttpRequestMessage OpenSubtitlesRequest(HttpMethod method, string path, OpenSubtitlesAccount account)
        {
            var request = new HttpRequestMessage(method, "https://api.opensubtitles.com/api/v1/" + path);
            request.Headers.TryAddWithoutValidation("Api-Key", account.ApiKey);
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("CinecorePlayer v0.2");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }

        private static async Task<List<Candidate>> OpenSubtitlesSearchAsync(Query query, OpenSubtitlesAccount account, CancellationToken ct)
        {
            var list = new List<Candidate>();
            var parameters = new List<string> { "languages=" + Uri.EscapeDataString(query.Language) };
            string digits = Regex.Match(query.ImdbId ?? "", @"\d+").Value.TrimStart('0');
            if (query.IsEpisode && query.Season.HasValue && query.Episode.HasValue)
            {
                if (query.TmdbId.HasValue) parameters.Add("parent_tmdb_id=" + query.TmdbId.Value);
                else parameters.Add("query=" + Uri.EscapeDataString(query.SeriesTitle ?? query.Title));
                parameters.Add("season_number=" + query.Season.Value);
                parameters.Add("episode_number=" + query.Episode.Value);
            }
            else if (digits.Length > 0) parameters.Add("imdb_id=" + digits);
            else
            {
                parameters.Add("query=" + Uri.EscapeDataString(query.Title));
                if (query.Year.HasValue) parameters.Add("year=" + query.Year.Value);
            }
            using var request = OpenSubtitlesRequest(HttpMethod.Get, "subtitles?" + string.Join("&", parameters.OrderBy(p => p, StringComparer.Ordinal)), account);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return list;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("attributes", out var a)) continue;
                if (!a.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) continue;
                var file = files[0];
                if (!file.TryGetProperty("file_id", out var fileId) || fileId.ValueKind != JsonValueKind.Number) continue;
                string release = a.TryGetProperty("release", out var r) ? r.GetString() ?? "" : "";
                int downloads = a.TryGetProperty("download_count", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
                bool impaired = a.TryGetProperty("hearing_impaired", out var h) && h.ValueKind == JsonValueKind.True;
                list.Add(new Candidate("OpenSubtitles", release.Length > 0 ? release : query.Title, query.Language,
                    downloads.ToString("N0", CultureInfo.CurrentCulture) + " download" + (impaired ? "  ·  non udenti" : ""),
                    900 + Math.Min(500, downloads / 50) - (impaired ? 15 : 0), fileId.GetInt64().ToString(CultureInfo.InvariantCulture)));
                if (list.Count >= 20) break;
            }
            return list;
        }

        private static async Task<byte[]> OpenSubtitlesDownloadAsync(string fileId, CancellationToken ct)
        {
            var account = OpenSubtitlesAccount.Load() ?? throw new InvalidOperationException("OpenSubtitles non configurato");
            // Con nome utente e password i download contano sul piano dell'utente (es. VIP).
            if (!string.IsNullOrWhiteSpace(account.User) && !string.IsNullOrEmpty(account.Password) &&
                (_openSubtitlesToken == null || _openSubtitlesTokenFor != account.User))
            {
                using var login = OpenSubtitlesRequest(HttpMethod.Post, "login", account);
                login.Content = new StringContent(JsonSerializer.Serialize(new { username = account.User, password = account.Password }), Encoding.UTF8, "application/json");
                using var loginResponse = await Http.SendAsync(login, ct).ConfigureAwait(false);
                if (loginResponse.IsSuccessStatusCode)
                {
                    using var loginDoc = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    _openSubtitlesToken = loginDoc.RootElement.TryGetProperty("token", out var token) ? token.GetString() : null;
                    _openSubtitlesTokenFor = account.User;
                }
            }
            using var request = OpenSubtitlesRequest(HttpMethod.Post, "download", account);
            if (_openSubtitlesToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openSubtitlesToken);
            request.Content = new StringContent(JsonSerializer.Serialize(new { file_id = long.Parse(fileId, CultureInfo.InvariantCulture), sub_format = "srt" }), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("OpenSubtitles: " + (int)response.StatusCode);
            using var doc = JsonDocument.Parse(body);
            string link = doc.RootElement.TryGetProperty("link", out var l) ? l.GetString() ?? "" : "";
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("OpenSubtitles: link");
            return await Http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Chiave e account OpenSubtitles dell'utente, cifrati con DPAPI per l'utente di Windows.</summary>
    internal sealed record OpenSubtitlesAccount(string ApiKey, string User, string Password)
    {
        private sealed record Stored(string ProtectedKey, string User, string ProtectedPassword);
        private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "opensubtitles.config.json");

        public static OpenSubtitlesAccount? Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return null;
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(StorePath));
                string key = Unprotect(stored?.ProtectedKey);
                return key.Length == 0 ? null : new OpenSubtitlesAccount(key, stored!.User ?? "", Unprotect(stored.ProtectedPassword));
            }
            catch { return null; }
        }

        public static void Save(string apiKey, string user, string password)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(apiKey)) { if (File.Exists(StorePath)) File.Delete(StorePath); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(new Stored(Protect(apiKey.Trim()), user.Trim(), Protect(password))));
            }
            catch (Exception ex) { Dbg.Warn("[SUBS] account save failed: " + ex.Message); }
        }

        private static string Protect(string value) => string.IsNullOrEmpty(value) ? "" :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

        private static string Unprotect(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
    }
}
