#nullable enable
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Voti di IMDb, Letterboxd e Metacritic per la scheda recensioni. Nessuna chiave:
    /// IMDb dal dataset pubblico title.ratings (uso personale, scaricato e aggiornato ogni
    /// settimana), Letterboxd dalla pagina del film raggiunta tramite ID IMDb (che fornisce
    /// anche titolo inglese e anno), Metacritic dalla ricerca pubblica con quel titolo.
    /// Si inviano solo ID e titoli, mai percorsi locali.
    /// </summary>
    internal static class ExternalRatingsService
    {
        internal sealed record Ratings(
            double? Imdb, int? ImdbVotes,
            double? Letterboxd, string? LetterboxdUrl,
            int? Metascore, string? MetacriticUrl,
            double? MetaUserScore = null);

        private static readonly HttpClient Http = CreateClient();
        private static readonly ConcurrentDictionary<string, Lazy<Task<Ratings>>> Requests = new();
        private static readonly SemaphoreSlim DatasetGate = new(1, 1);
        private static readonly SemaphoreSlim ScanGate = new(1, 1);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CinecorePlayer2025/1.0");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            return client;
        }

        private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "ratings");

        public static Task<Ratings> ResolveAsync(string imdbId, bool tv, string? fallbackTitle, int? year)
        {
            imdbId = (imdbId ?? string.Empty).Trim();
            if (!Regex.IsMatch(imdbId, @"^tt\d{5,10}$")) return Task.FromResult(new Ratings(null, null, null, null, null, null));
            return Requests.GetOrAdd(imdbId, id => new Lazy<Task<Ratings>>(() => ResolveCoreAsync(id, tv, fallbackTitle, year))).Value;
        }

        private static async Task<Ratings> ResolveCoreAsync(string imdbId, bool tv, string? fallbackTitle, int? year)
        {
            string cache = Path.Combine(Root, imdbId + ".json");
            try
            {
                if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(3) &&
                    JsonSerializer.Deserialize<Ratings>(await File.ReadAllTextAsync(cache).ConfigureAwait(false)) is { } saved)
                {
                    // Voci salvate prima del doppio voto: si aggiunge solo il pubblico Metacritic.
                    if (saved.MetacriticUrl != null && saved.MetaUserScore == null &&
                        await MetacriticUserScoreAsync(saved.MetacriticUrl).ConfigureAwait(false) is double user)
                    {
                        saved = saved with { MetaUserScore = user };
                        try { await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(saved)).ConfigureAwait(false); } catch { }
                    }
                    return saved;
                }
            }
            catch { }

            var imdbTask = ImdbAsync(imdbId);
            var letterboxd = tv ? (Rating: (double?)null, Url: (string?)null, Title: (string?)null, Year: (int?)null) : await LetterboxdAsync(imdbId).ConfigureAwait(false);
            var metacritic = await MetacriticAsync(letterboxd.Title ?? fallbackTitle, letterboxd.Year ?? year, tv).ConfigureAwait(false);
            var imdb = await imdbTask.ConfigureAwait(false);
            double? metaUser = await MetacriticUserScoreAsync(metacritic.Url).ConfigureAwait(false);
            var result = new Ratings(imdb.Rating, imdb.Votes, letterboxd.Rating, letterboxd.Url, metacritic.Score, metacritic.Url, metaUser);
            try
            {
                Directory.CreateDirectory(Root);
                await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(result)).ConfigureAwait(false);
            }
            catch { }
            // Un risultato vuoto (rete assente) si ritenta alla prossima apertura.
            if (result.Imdb == null && result.Letterboxd == null && result.Metascore == null)
                Requests.TryRemove(imdbId, out _);
            return result;
        }

        // ===== IMDb: dataset ufficiale title.ratings.tsv.gz =====
        private static async Task<(double? Rating, int? Votes)> ImdbAsync(string imdbId)
        {
            try
            {
                string dataset = Path.Combine(Root, "title.ratings.tsv.gz");
                await DatasetGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!File.Exists(dataset) || DateTime.UtcNow - File.GetLastWriteTimeUtc(dataset) > TimeSpan.FromDays(7))
                    {
                        Directory.CreateDirectory(Root);
                        string temp = dataset + ".download";
                        using (var response = await Http.GetAsync("https://datasets.imdbws.com/title.ratings.tsv.gz", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            await using var target = File.Create(temp);
                            await source.CopyToAsync(target).ConfigureAwait(false);
                        }
                        File.Move(temp, dataset, overwrite: true);
                    }
                }
                catch { if (!File.Exists(dataset)) return (null, null); }
                finally { DatasetGate.Release(); }

                await ScanGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await Task.Run(() =>
                    {
                        string prefix = imdbId + "\t";
                        using var file = File.OpenRead(dataset);
                        using var gzip = new GZipStream(file, CompressionMode.Decompress);
                        using var reader = new StreamReader(gzip, Encoding.UTF8, false, 1 << 16);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                            var parts = line.Split('\t');
                            double? rating = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : null;
                            int? votes = parts.Length > 2 && int.TryParse(parts[2], out int v) ? v : null;
                            return (rating, votes);
                        }
                        return ((double?)null, (int?)null);
                    }).ConfigureAwait(false);
                }
                finally { ScanGate.Release(); }
            }
            catch { return (null, null); }
        }

        // ===== Letterboxd: pagina del film via ID IMDb =====
        private static async Task<(double? Rating, string? Url, string? Title, int? Year)> LetterboxdAsync(string imdbId)
        {
            try
            {
                using var response = await Http.GetAsync("https://letterboxd.com/imdb/" + imdbId + "/").ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return (null, null, null, null);
                string url = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
                if (!url.Contains("/film/", StringComparison.OrdinalIgnoreCase)) return (null, null, null, null);
                string html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                double? rating = null;
                var data = Regex.Match(html, @"name=""twitter:data2""\s+content=""([0-9.]+) out of 5""");
                if (data.Success && double.TryParse(data.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) rating = value;
                string? title = null; int? year = null;
                var og = Regex.Match(html, @"property=""og:title""\s+content=""([^""]+)""");
                if (og.Success)
                {
                    string text = WebUtility.HtmlDecode(og.Groups[1].Value);
                    var split = Regex.Match(text, @"^(.*)\s\((\d{4})\)\s*$");
                    title = split.Success ? split.Groups[1].Value.Trim() : text.Trim();
                    if (split.Success) year = int.Parse(split.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                return (rating, url, title, year);
            }
            catch { return (null, null, null, null); }
        }

        // ===== Metacritic: ricerca pubblica, scelta per titolo e anno =====
        private static async Task<(int? Score, string? Url)> MetacriticAsync(string? title, int? year, bool tv)
        {
            if (string.IsNullOrWhiteSpace(title)) return (null, null);
            try
            {
                string query = Uri.EscapeDataString(title.Trim());
                string json = await Http.GetStringAsync($"https://backend.metacritic.com/finder/metacritic/search/{query}/web?offset=0&limit=10&mcoTypeId={(tv ? 1 : 2)}").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return (null, null);
                string wanted = Key(title);
                JsonElement? best = null; int bestScore = int.MinValue;
                foreach (var item in items.EnumerateArray())
                {
                    string candidate = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    int points = Key(candidate) == wanted ? 100 : Key(candidate).StartsWith(wanted, StringComparison.Ordinal) ? 20 : 0;
                    if (points == 0) continue;
                    if (year.HasValue && item.TryGetProperty("premiereYear", out var py) && py.ValueKind == JsonValueKind.Number)
                        points += Math.Abs(py.GetInt32() - year.Value) switch { 0 => 50, 1 => 30, _ => -80 };
                    if (points > bestScore) { bestScore = points; best = item; }
                }
                if (best is not JsonElement match || bestScore < 60) return (null, null);
                int? score = match.TryGetProperty("criticScoreSummary", out var summary) && summary.ValueKind == JsonValueKind.Object &&
                             summary.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
                string? slug = match.TryGetProperty("slug", out var sl) ? sl.GetString() : null;
                string? url = slug == null ? null : $"https://www.metacritic.com/{(tv ? "tv" : "movie")}/{slug}/";
                return (score, url);
            }
            catch { return (null, null); }
        }

        // Voto del pubblico Metacritic (0-10): con quello della critica forma il doppio voto.
        private static async Task<double?> MetacriticUserScoreAsync(string? pageUrl)
        {
            var match = Regex.Match(pageUrl ?? "", @"metacritic\.com/(movie|tv)/([^/]+)/");
            if (!match.Success) return null;
            try
            {
                string type = match.Groups[1].Value == "tv" ? "shows" : "movies";
                string json = await Http.GetStringAsync($"https://backend.metacritic.com/reviews/metacritic/user/{type}/{match.Groups[2].Value}/stats/web").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("item", out var item) &&
                       item.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number && item.TryGetProperty("reviewCount", out var count) &&
                       count.ValueKind == JsonValueKind.Number && count.GetInt32() > 0 ? score.GetDouble() : null;
            }
            catch { return null; }
        }

        // ===== Recensioni: Letterboxd (popolari) e Metacritic (critici) =====
        private static readonly ConcurrentDictionary<string, Lazy<Task<System.Collections.Generic.List<MovieMetadataService.RichReview>>>> ReviewRequests = new();

        public static Task<System.Collections.Generic.List<MovieMetadataService.RichReview>> ResolveReviewsAsync(Ratings ratings)
        {
            string key = (ratings.LetterboxdUrl ?? "") + "|" + (ratings.MetacriticUrl ?? "");
            return ReviewRequests.GetOrAdd(key, k => new Lazy<Task<System.Collections.Generic.List<MovieMetadataService.RichReview>>>(async () =>
            {
                var letterboxd = LetterboxdReviewsAsync(ratings.LetterboxdUrl);
                var metacritic = MetacriticReviewsAsync(ratings.MetacriticUrl);
                var all = new System.Collections.Generic.List<MovieMetadataService.RichReview>();
                all.AddRange(await letterboxd.ConfigureAwait(false));
                all.AddRange(await metacritic.ConfigureAwait(false));
                if (all.Count == 0) ReviewRequests.TryRemove(key, out _);
                return all;
            })).Value;
        }

        private static async Task<System.Collections.Generic.List<MovieMetadataService.RichReview>> LetterboxdReviewsAsync(string? filmUrl)
        {
            var list = new System.Collections.Generic.List<MovieMetadataService.RichReview>();
            if (string.IsNullOrWhiteSpace(filmUrl) || !filmUrl.StartsWith("https://letterboxd.com/film/", StringComparison.OrdinalIgnoreCase)) return list;
            try
            {
                string html = await Http.GetStringAsync(filmUrl.TrimEnd('/') + "/reviews/by/activity/").ConfigureAwait(false);
                var blocks = html.Split("js-review-body");
                for (int i = 1; i < blocks.Length && list.Count < 12; i++)
                {
                    string before = blocks[i - 1].Length > 6000 ? blocks[i - 1][^6000..] : blocks[i - 1];
                    var author = Regex.Matches(before, @"class=""displayname"">([^<]+)<").LastOrDefault();
                    var stars = Regex.Matches(before, @"aria-label=""(★+½?)""").LastOrDefault();
                    var body = Regex.Match(blocks[i], @">(.*?)</div>", RegexOptions.Singleline);
                    if (!body.Success) continue;
                    string text = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(body.Groups[1].Value, @"<br\s*/?>|</p>\s*<p>", " "), "<[^>]+>", " "));
                    text = Regex.Replace(text, @"\s+", " ").Trim();
                    // Spoiler nascosti e recensioni di una riga sono poco utili nella scheda.
                    if (text.Length < 40 || text.Contains("This review may contain spoilers", StringComparison.OrdinalIgnoreCase)) continue;
                    double? rating = null;
                    if (stars != null) { string s = stars.Groups[1].Value; rating = s.Count(c => c == '★') + (s.EndsWith("½", StringComparison.Ordinal) ? .5 : 0); }
                    list.Add(new MovieMetadataService.RichReview
                    {
                        Author = WebUtility.HtmlDecode(author?.Groups[1].Value ?? "Letterboxd"),
                        Content = text, Rating = rating, RatingScale = 5, Source = "Letterboxd", Language = "en"
                    });
                }
            }
            catch { }
            return list;
        }

        private static async Task<System.Collections.Generic.List<MovieMetadataService.RichReview>> MetacriticReviewsAsync(string? pageUrl)
        {
            var list = new System.Collections.Generic.List<MovieMetadataService.RichReview>();
            var match = Regex.Match(pageUrl ?? "", @"metacritic\.com/(movie|tv)/([^/]+)/");
            if (!match.Success) return list;
            string type = match.Groups[1].Value == "tv" ? "shows" : "movies";
            async Task Load(string audience, bool critic)
            {
                try
                {
                    string json = await Http.GetStringAsync($"https://backend.metacritic.com/reviews/metacritic/{audience}/{type}/{match.Groups[2].Value}/web?offset=0&limit=16&sort=score").ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;
                    int added = 0;
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("spoiler", out var spoiler) && spoiler.ValueKind == JsonValueKind.True) continue;
                        string quote = item.TryGetProperty("quote", out var q) ? q.GetString() ?? "" : "";
                        if (quote.Length < 40) continue;
                        string author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                        string outlet = item.TryGetProperty("publicationName", out var p) ? p.GetString() ?? "" : "";
                        double? score = item.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : null;
                        list.Add(new MovieMetadataService.RichReview
                        {
                            Author = string.IsNullOrWhiteSpace(author) ? (string.IsNullOrWhiteSpace(outlet) ? "Metacritic" : outlet) : author,
                            Detail = critic && !string.IsNullOrWhiteSpace(author) ? outlet : null,
                            Content = WebUtility.HtmlDecode(Regex.Replace(quote, @"\s+", " ").Trim()),
                            Rating = score, RatingScale = critic ? 100 : 10, Source = "Metacritic", Language = "en", Critic = critic
                        });
                        if (++added >= 12) break;
                    }
                }
                catch { }
            }
            await Task.WhenAll(Load("critic", true), Load("user", false)).ConfigureAwait(false);
            return list;
        }

        private static string Key(string? value)
        {
            var builder = new StringBuilder();
            foreach (char c in (value ?? string.Empty).Normalize(NormalizationForm.FormD))
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToUpperInvariant(c));
            return builder.ToString();
        }
    }
}
