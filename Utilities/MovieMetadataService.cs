using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Servizio centralizzato per:
    ///  - ricavare un titolo "decente" e un anno dal path del file
    ///  - parlare con TMDb per recuperare poster
    ///  - mantenere una cache persistente (posterIndex.json) per velocizzare tutto
    /// </summary>
    internal static partial class MovieMetadataService
    {
        // API key TMDb integrata come fallback (cifrata, vedi SecretVault). L'utente puo' sovrascriverla dalla UI.
        private static string DefaultTmdbApiKey => SecretVault.BuiltIn("tmdb") ?? string.Empty;
        public static event Action? PostersChanged;
        private static readonly HttpClient _http = CreateTmdbHttpClient();

        private static HttpClient CreateTmdbHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            // Alcuni edge/CDN sono piu' affidabili con header da client reale.
            // Gli header valgono sia per api.themoviedb.org sia per image.tmdb.org.
            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "CinecorePlayer2025/1.0 (+https://www.themoviedb.org/)");
                // GDI+ is the renderer used by the WinForms UI. Advertising AVIF/WebP
                // made the CDN return bytes that were later cached with a .jpg suffix
                // but could not be decoded by the card renderer.
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json, image/jpeg, image/png, image/*;q=0.6, */*;q=0.2");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("it-IT,it;q=0.9,en-US;q=0.8,en;q=0.7");
            }
            catch { }

            return client;
        }

        private static readonly PosterIndexStore _posterIndex = new();
        private static readonly TmdbApiKeyStore _tmdbApiKeyStore = new();
        private static readonly SemaphoreSlim _tmdbRequestGate = new SemaphoreSlim(2, 2);
        private static readonly object _localizedTitleRefreshSync = new object();
        private static readonly HashSet<string> _localizedTitleRefreshCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _localizedTitleRefreshInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _preferredMetadataLanguage = AppLanguage.SystemDefault;
        private const int TmdbMaxAttempts = 4;

        private static string TmdbApiKey
        {
            get
            {
                var configured = _tmdbApiKeyStore.Get();
                if (!string.IsNullOrWhiteSpace(configured))
                    return configured!;
                return DefaultTmdbApiKey;
            }
        }

        public static string? GetUserTmdbApiKey() => _tmdbApiKeyStore.Get();

        public static bool HasCustomTmdbApiKey => !string.IsNullOrWhiteSpace(_tmdbApiKeyStore.Get());

        public static string GetTmdbStatusText()
        {
            if (HasCustomTmdbApiKey)
                return "Attivo (chiave personale)";

            return string.IsNullOrWhiteSpace(DefaultTmdbApiKey)
                ? "Non configurato"
                : "Attivo (chiave integrata)";
        }

        public static void SetUserTmdbApiKey(string? apiKey)
        {
            _tmdbApiKeyStore.Set(apiKey);
            PostersChanged?.Invoke();
        }

        public static void SetPreferredMetadataLanguage(string? language)
        {
            string normalized = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase)
                ? "en"
                : "it";

            if (string.Equals(_preferredMetadataLanguage, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _preferredMetadataLanguage = normalized;
            lock (_localizedTitleRefreshSync)
            {
                _localizedTitleRefreshCompleted.Clear();
                _localizedTitleRefreshInFlight.Clear();
            }
            PostersChanged?.Invoke();
        }

        private static string PreferredTmdbLanguage()
            => string.Equals(_preferredMetadataLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "it-IT";

        private static string[] PreferredTmdbLanguages()
        {
            string primary = PreferredTmdbLanguage();
            string secondary = string.Equals(primary, "en-US", StringComparison.OrdinalIgnoreCase) ? "it-IT" : "en-US";
            return new[] { primary, secondary };
        }

        private static string PreferredImageLanguageList()
            => string.Equals(PreferredTmdbLanguage(), "en-US", StringComparison.OrdinalIgnoreCase)
                ? "en,it,null"
                : "it,en,null";

        private static bool CacheLanguageMatches(string? language)
        {
            string current = PreferredTmdbLanguage();
            if (string.IsNullOrWhiteSpace(language))
                return string.Equals(current, "it-IT", StringComparison.OrdinalIgnoreCase);

            return string.Equals(NormalizeTmdbLanguage(language), current, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeTmdbLanguage(string? language)
        {
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                return "en-US";
            }

            return "it-IT";
        }

        private static IEnumerable<(string Language, int? Year)> BuildLanguageYearAttempts(int? year)
        {
            foreach (string language in PreferredTmdbLanguages())
            {
                if (year.HasValue)
                    yield return (language, year);
                yield return (language, null);
            }
        }

        private static HttpResponseMessage GetTmdbResponse(string url, CancellationToken ct)
        {
            Exception? lastError = null;

            for (int attempt = 1; attempt <= TmdbMaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                HttpResponseMessage? resp = null;

                _tmdbRequestGate.Wait(ct);
                try
                {
                    WaitForTmdbRequestSlot(ct);
                    resp = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
                finally
                {
                    try { _tmdbRequestGate.Release(); } catch { }
                }

                if (resp != null)
                {
                    if (!IsTransientTmdbStatus(resp.StatusCode) || attempt >= TmdbMaxAttempts)
                        return resp;

                    var retryDelay = GetTmdbRetryDelay(resp, attempt);
                    try { resp.Dispose(); } catch { }
                    DelayRespectingCancellation(retryDelay, ct);
                    continue;
                }

                if (attempt < TmdbMaxAttempts)
                {
                    DelayRespectingCancellation(GetTmdbRetryDelay(null, attempt), ct);
                    continue;
                }
            }

            throw new HttpRequestException(lastError?.Message ?? "Richiesta TMDb non riuscita.", lastError);
        }

        private static byte[] GetTmdbImageBytes(string imageUrl, CancellationToken ct)
        {
            Exception? lastError = null;

            // image.tmdb.org e' servito da CDN: un 403/5xx occasionale su un edge non deve
            // trasformarsi in "titolo risolto ma poster vuoto" per tutta la sessione.
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string url = imageUrl;
                    if (attempt > 1)
                    {
                        string sep = url.Contains('?') ? "&" : "?";
                        url += sep + "cinecore_retry=" + attempt.ToString(CultureInfo.InvariantCulture);
                    }

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Referrer = new Uri("https://www.themoviedb.org/");
                    req.Headers.Accept.ParseAdd("image/jpeg, image/png;q=0.9, image/*;q=0.4");

                    HttpResponseMessage resp;
                    _tmdbRequestGate.Wait(ct);
                    try
                    {
                        WaitForTmdbRequestSlot(ct);
                        resp = _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        try { _tmdbRequestGate.Release(); } catch { }
                    }

                    using (resp)
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                            if (IsGdiDecodableImagePayload(bytes))
                                return bytes;

                            lastError = new HttpRequestException("TMDb image response was not a valid image payload.");
                        }
                        else
                        {
                            int code = (int)resp.StatusCode;
                            lastError = new HttpRequestException($"TMDb image request failed: {code}");

                            // Per la CDN ritentiamo anche 403: in passato alcuni edge CloudFront
                            // hanno restituito 403 temporanei pur con poster validi.
                            bool retryable = code == 403 || code == 408 || code == 425 || code == 429 || code >= 500;
                            if (!retryable)
                                break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (attempt < 4)
                    DelayRespectingCancellation(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }

            throw new HttpRequestException(lastError?.Message ?? "TMDb image request failed.", lastError);
        }

        private static byte[] GetTmdbImageBytesForPath(string tmdbPath, CancellationToken ct, params string[] sizes)
        {
            if (string.IsNullOrWhiteSpace(tmdbPath))
                throw new ArgumentException("TMDb image path is empty.", nameof(tmdbPath));

            Exception? lastError = null;
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawSize in sizes ?? Array.Empty<string>())
            {
                string size = string.IsNullOrWhiteSpace(rawSize) ? "original" : rawSize.Trim();
                if (!attempted.Add(size))
                    continue;

                try
                {
                    return GetTmdbImageBytes("https://image.tmdb.org/t/p/" + size + tmdbPath, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastError = ex; }
            }

            throw new HttpRequestException(lastError?.Message ?? "TMDb image download failed for all sizes.", lastError);
        }

        private static bool LooksLikeImagePayload(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < 16)
                return false;

            // JPEG
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return true;

            // PNG
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
                return true;

            // WebP
            if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
                bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
                return true;

            return false;
        }

        private static bool IsGdiDecodableImagePayload(byte[]? bytes)
        {
            if (!LooksLikeImagePayload(bytes) || bytes == null)
                return false;

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                return image.Width >= 32 && image.Height >= 32;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTransientTmdbStatus(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code == 408 || code == 425 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
        }

        private static TimeSpan GetTmdbRetryDelay(HttpResponseMessage? resp, int attempt)
        {
            try
            {
                var ra = resp?.Headers?.RetryAfter;
                if (ra != null)
                {
                    if (ra.Delta.HasValue && ra.Delta.Value > TimeSpan.Zero)
                        return ra.Delta.Value;
                    if (ra.Date.HasValue)
                    {
                        var delta = ra.Date.Value - DateTimeOffset.UtcNow;
                        if (delta > TimeSpan.Zero)
                            return delta;
                    }
                }
            }
            catch { }

            double[] scheduleMs = { 450d, 900d, 1600d, 2600d };
            double ms = scheduleMs[Math.Max(0, Math.Min(scheduleMs.Length - 1, attempt - 1))];
            return TimeSpan.FromMilliseconds(ms);
        }

        private static readonly object _tmdbThrottleLock = new object();
        private static DateTime _tmdbNextAllowedUtc = DateTime.MinValue;
        private static readonly TimeSpan TmdbMinRequestSpacing = TimeSpan.FromMilliseconds(220);

        private static void DelayRespectingCancellation(TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero)
                return;

            Task.Delay(delay, ct).GetAwaiter().GetResult();
        }

        private static void WaitForTmdbRequestSlot(CancellationToken ct)
        {
            TimeSpan delay;
            lock (_tmdbThrottleLock)
            {
                var now = DateTime.UtcNow;
                if (_tmdbNextAllowedUtc <= now)
                {
                    _tmdbNextAllowedUtc = now + TmdbMinRequestSpacing;
                    return;
                }

                delay = _tmdbNextAllowedUtc - now;
                _tmdbNextAllowedUtc = _tmdbNextAllowedUtc + TmdbMinRequestSpacing;
            }

            DelayRespectingCancellation(delay, ct);
        }

        private static void MergeResolvedTitle(ref string? targetTitle, ref int? targetYear, string? sourceTitle, int? sourceYear)
        {
            if (string.IsNullOrWhiteSpace(targetTitle) && !string.IsNullOrWhiteSpace(sourceTitle))
                targetTitle = sourceTitle;

            if (!targetYear.HasValue && sourceYear.HasValue)
                targetYear = sourceYear;
        }

        public sealed class MediaTitleInfo
        {
            public string NormalizedTitle { get; set; } = string.Empty;
            public int? Year { get; set; }
            public bool IsTvEpisode { get; set; }
            public string? SeriesTitle { get; set; }
            public int? SeasonNumber { get; set; }
            public int? EpisodeNumber { get; set; }
            public string? EpisodeTitle { get; set; }
        }

        public sealed class RichMetadata
        {
            public string? Title { get; set; }
            public int? Year { get; set; }
            public string? Overview { get; set; }
            public List<string> Cast { get; set; } = new();
            public List<string> Genres { get; set; } = new();
            public string? Tagline { get; set; }
            public string? Director { get; set; }
            public double? Rating { get; set; }
            public List<RichCastMember> CastMembers { get; set; } = new();
            public string? MediaType { get; set; }
            public int? TmdbId { get; set; }
            public string? ImdbId { get; set; }
            public List<RichReview> Reviews { get; set; } = new();
        }

        public sealed class RichCastMember
        {
            public string Name { get; set; } = string.Empty;
            public string Character { get; set; } = string.Empty;
            public string? ProfilePath { get; set; }
        }

        public sealed class RichReview
        {
            public string Author { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
            public double? Rating { get; set; }
            /// <summary>Codice lingua ISO (es. "en") quando diversa da quella dell'interfaccia.</summary>
            public string? Language { get; set; }
            /// <summary>Piattaforma d'origine: TMDb, Letterboxd, Metacritic.</summary>
            public string Source { get; set; } = "TMDb";
            /// <summary>Fondo scala del voto (10 per TMDb, 5 stelle per Letterboxd, 100 per Metacritic).</summary>
            public double RatingScale { get; set; } = 10;
            /// <summary>Testata o dettaglio dell'autore (es. "Variety" per i critici Metacritic).</summary>
            public string? Detail { get; set; }
            /// <summary>Recensione della critica (vs pubblico).</summary>
            public bool Critic { get; set; }
        }

        public static string? CacheCastProfileImage(string? imageUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return null;

            try
            {
                string normalizedUrl = imageUrl.Trim();
                string cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025",
                    "cast-profiles");
                Directory.CreateDirectory(cacheDir);

                byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
                string key = Convert.ToHexString(keyBytes).ToLowerInvariant();
                string extension = ".jpg";
                try
                {
                    if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
                    {
                        string candidate = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                        if (candidate is ".jpg" or ".jpeg" or ".png" or ".bmp")
                            extension = candidate;
                    }
                }
                catch { }

                string cachedPath = Path.Combine(cacheDir, key + extension);
                if (IsValidCastProfileFile(cachedPath))
                    return cachedPath;
                try { if (File.Exists(cachedPath)) File.Delete(cachedPath); } catch { }

                // A cached .img from an older build may contain an HTML/error payload.
                // Never trust only its length; migrate it only if GDI+ can decode it.
                string legacyPath = Path.Combine(cacheDir, key + ".img");
                if (IsValidCastProfileFile(legacyPath))
                {
                    try
                    {
                        File.Copy(legacyPath, cachedPath, overwrite: true);
                        return cachedPath;
                    }
                    catch { return legacyPath; }
                }
                try { if (File.Exists(legacyPath)) File.Delete(legacyPath); } catch { }

                byte[]? bytes = null;
                foreach (string candidateUrl in CastProfileImageCandidates(normalizedUrl))
                {
                    try
                    {
                        byte[] downloaded = GetTmdbImageBytes(candidateUrl, ct);
                        if (IsDecodableCastProfile(downloaded))
                        {
                            bytes = downloaded;
                            break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                if (bytes == null)
                    return null;

                string tempPath = cachedPath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(tempPath, bytes);
                if (File.Exists(cachedPath))
                    File.Delete(cachedPath);
                File.Move(tempPath, cachedPath);
                return IsValidCastProfileFile(cachedPath) ? cachedPath : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> CastProfileImageCandidates(string url)
        {
            var candidates = new List<string> { url };
            if (url.Contains("/t/p/w342/", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Regex.Replace(url, "/t/p/w342/", "/t/p/w500/", RegexOptions.IgnoreCase));
                candidates.Add(Regex.Replace(url, "/t/p/w342/", "/t/p/original/", RegexOptions.IgnoreCase));
            }
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsValidCastProfileFile(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length < 128)
                    return false;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                return image.Width >= 32 && image.Height >= 32;
            }
            catch { return false; }
        }

        private static bool IsDecodableCastProfile(byte[] bytes)
        {
            try
            {
                if (!LooksLikeImagePayload(bytes))
                    return false;
                using var stream = new MemoryStream(bytes, writable: false);
                using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                return image.Width >= 32 && image.Height >= 32;
            }
            catch { return false; }
        }

        // --------------------------------------------------------------------
        // API pubblica
        // --------------------------------------------------------------------
        private static readonly object RichRequestGate = new();
        private static readonly Dictionary<string, Task<RichMetadata>> RichRequests = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> RichRetryAfter = new(StringComparer.OrdinalIgnoreCase);

        public static RichMetadata ResolveRichMetadata(
            string filePath,
            double? durationSeconds,
            string language,
            CancellationToken ct,
            string? mediaType = null,
            bool includeReviews = true)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return new RichMetadata();

            // Overview, genres and the detail sheet often ask for the same title
            // at once. Share the TMDb lookup so opening a detail does not launch
            // three identical search/credits requests.
            string normalizedMediaType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" :
                                         string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "auto";
            string key = string.Join("\u001f", filePath.Trim(), language, normalizedMediaType, includeReviews ? "reviews" : "summary",
                durationSeconds.HasValue ? Math.Round(durationSeconds.Value).ToString(CultureInfo.InvariantCulture) : "",
                TmdbApiKey);
            // Risposta salvata: subito, senza rete. Se e' vecchia si aggiorna in secondo piano.
            string diskKey = RichMetadataDiskStore.Key(filePath, language, normalizedMediaType);
            RichMetadata? saved = RichMetadataDiskStore.TryGet(diskKey, includeReviews, out bool savedIsStale);
            if (saved != null && !savedIsStale)
                return saved;

            Task<RichMetadata> request;
            lock (RichRequestGate)
            {
                if (RichRetryAfter.TryGetValue(key, out DateTime retryAfter) && retryAfter <= DateTime.UtcNow)
                {
                    RichRetryAfter.Remove(key);
                    RichRequests.Remove(key);
                }
                if (!RichRequests.TryGetValue(key, out request!))
                {
                    request = Task.Run(() =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        return ResolveRichMetadataCore(filePath, durationSeconds, language, normalizedMediaType, includeReviews, timeout.Token);
                    });
                    RichRequests[key] = request;
                    Task<RichMetadata> completedRequest = request;
                    _ = request.ContinueWith(finished =>
                    {
                        bool useful = finished.Status == TaskStatus.RanToCompletion &&
                            (finished.Result.TmdbId.HasValue ||
                             !string.IsNullOrWhiteSpace(finished.Result.Overview) ||
                             finished.Result.Genres.Count > 0);
                        if (useful) RichMetadataDiskStore.Put(diskKey, finished.Result, includeReviews);
                        lock (RichRequestGate)
                            if (RichRequests.TryGetValue(key, out var current) && ReferenceEquals(current, completedRequest))
                            {
                                if (useful) RichRetryAfter.Remove(key);
                                else RichRetryAfter[key] = DateTime.UtcNow.AddSeconds(30);
                            }
                    }, TaskScheduler.Default);
                    if (RichRequests.Count > 256)
                    {
                        foreach (string completedKey in RichRequests.Where(pair => pair.Value.IsCompleted).Take(64).Select(pair => pair.Key).ToArray())
                        {
                            RichRequests.Remove(completedKey);
                            RichRetryAfter.Remove(completedKey);
                        }
                    }
                }
            }
            // Dati vecchi ma validi: il chiamante li riceve subito, la richiesta appena avviata li rinfresca.
            if (saved != null)
                return saved;
            try
            {
                return request.WaitAsync(ct).GetAwaiter().GetResult();
            }
            catch (Exception) when (RichMetadataDiskStore.TryGet(diskKey, needReviews: false, out _) is { } offline)
            {
                // Rete assente o TMDb in errore: meglio i dettagli salvati (anche senza recensioni) che niente.
                return offline;
            }
        }

        private static RichMetadata ResolveRichMetadataCore(
            string filePath,
            double? durationSeconds,
            string language,
            string mediaType,
            bool includeReviews,
            CancellationToken ct)
        {
            var result = new RichMetadata();

            if (string.IsNullOrWhiteSpace(filePath))
                return result;

            try
            {
                result.Title = GetBestKnownDisplayTitle(filePath);
            }
            catch { }

            MediaTitleInfo parsed;
            try { parsed = ExtractMediaTitleInfoFromPath(filePath); }
            catch { parsed = new MediaTitleInfo { NormalizedTitle = result.Title ?? string.Empty }; }

            if (parsed.Year.HasValue)
                result.Year = parsed.Year;

            if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            string primaryLanguage = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase)
                ? "en-US"
                : "it-IT";
            string secondaryLanguage = primaryLanguage == "it-IT" ? "en-US" : "it-IT";
            var languages = new[] { primaryLanguage, secondaryLanguage };

            try
            {
                bool resolveAsTv = mediaType == "tv" || (mediaType == "auto" && parsed.IsTvEpisode);
                if (resolveAsTv)
                {
                    string searchTitle = parsed.IsTvEpisode && !string.IsNullOrWhiteSpace(parsed.SeriesTitle)
                        ? parsed.SeriesTitle!
                        : !string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.SeriesTitle! : parsed.NormalizedTitle;
                    MediaTitleInfo tvInfo = parsed;
                    if (mediaType == "tv" && !parsed.IsTvEpisode)
                    {
                        tvInfo = new MediaTitleInfo
                        {
                            NormalizedTitle = searchTitle,
                            SeriesTitle = searchTitle,
                            Year = parsed.Year
                        };
                    }

                    foreach (var titleVariant in BuildTvSearchTitleVariants(searchTitle))
                    {
                        foreach (var lang in languages)
                        {
                            if (TryResolveTvRichMetadata(titleVariant, parsed.Year, durationSeconds, lang, tvInfo, includeReviews, ct, out var tv))
                                return MergeRichFallbacks(tv, result);
                        }
                    }
                }
                else
                {
                    string searchTitle = !string.IsNullOrWhiteSpace(parsed.NormalizedTitle)
                        ? parsed.NormalizedTitle
                        : (result.Title ?? filePath);

                    foreach (var titleVariant in BuildMovieSearchTitleVariants(searchTitle))
                    {
                        foreach (var lang in languages)
                        {
                            if (TryResolveMovieRichMetadata(titleVariant, parsed.Year, durationSeconds, lang, includeReviews, ct, out var movie))
                                return MergeRichFallbacks(movie, result);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best-effort metadata: callers can still use cached title/artwork.
            }

            return result;
        }

        private static RichMetadata MergeRichFallbacks(RichMetadata primary, RichMetadata fallback)
        {
            if (string.IsNullOrWhiteSpace(primary.Title))
                primary.Title = fallback.Title;
            if (!primary.Year.HasValue)
                primary.Year = fallback.Year;
            if (string.IsNullOrWhiteSpace(primary.Overview))
                primary.Overview = fallback.Overview;
            if ((primary.Cast == null || primary.Cast.Count == 0) && fallback.Cast != null)
                primary.Cast = fallback.Cast;
            if ((primary.Genres == null || primary.Genres.Count == 0) && fallback.Genres != null)
                primary.Genres = fallback.Genres;
            if (string.IsNullOrWhiteSpace(primary.Tagline))
                primary.Tagline = fallback.Tagline;
            if (string.IsNullOrWhiteSpace(primary.Director))
                primary.Director = fallback.Director;
            if (!primary.Rating.HasValue)
                primary.Rating = fallback.Rating;
            if ((primary.CastMembers == null || primary.CastMembers.Count == 0) && fallback.CastMembers != null)
                primary.CastMembers = fallback.CastMembers;
            if (!primary.TmdbId.HasValue)
                primary.TmdbId = fallback.TmdbId;
            if (string.IsNullOrWhiteSpace(primary.ImdbId))
                primary.ImdbId = fallback.ImdbId;
            if ((primary.Reviews == null || primary.Reviews.Count == 0) && fallback.Reviews != null)
                primary.Reviews = fallback.Reviews;
            return primary;
        }

        private static bool TryResolveMovieRichMetadata(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            bool includeReviews,
            CancellationToken ct,
            out RichMetadata metadata)
        {
            metadata = new RichMetadata { MediaType = "movie" };
            if (string.IsNullOrWhiteSpace(searchTitle))
                return false;

            string query = Uri.EscapeDataString(searchTitle);
            string url = searchYear.HasValue
                ? $"https://api.themoviedb.org/3/search/movie?api_key={TmdbApiKey}&language={language}&query={query}&year={searchYear.Value}"
                : $"https://api.themoviedb.org/3/search/movie?api_key={TmdbApiKey}&language={language}&query={query}";

            using var resp = GetTmdbResponse(url, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
                return false;

            var ordered = OrderMovieSearchCandidates(
                searchTitle,
                CollectMovieSearchCandidates(results, searchTitle, searchYear, expectedDurationSeconds, language, ct));

            var selected = ordered.FirstOrDefault();
            if (selected == null)
                return false;

            metadata.Title = selected.Title ?? selected.OriginalTitle ?? searchTitle;
            metadata.Year = selected.Year;
            metadata.Overview = ReadJsonString(selected.Result, "overview");
            metadata.Genres = ReadGenreNamesFromSearchResult(selected.Result, "movie");
            if (selected.MovieId > 0)
            {
                metadata.TmdbId = selected.MovieId;
                EnrichTmdbPresentation("movie", selected.MovieId, language, metadata, includeReviews, ct);
                EnrichTmdbCredits("movie", selected.MovieId, metadata, ct);
                // Film senza trama nella lingua scelta (capita con i titoli poco distribuiti): meglio
                // quella inglese che il riquadro vuoto.
                if (string.IsNullOrWhiteSpace(metadata.Overview) && !language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var english = GetTmdbResponse($"https://api.themoviedb.org/3/movie/{selected.MovieId}?api_key={TmdbApiKey}&language=en-US", ct);
                        if (english.IsSuccessStatusCode)
                        {
                            using var englishDoc = JsonDocument.Parse(english.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                            string? overview = ReadJsonString(englishDoc.RootElement, "overview");
                            if (!string.IsNullOrWhiteSpace(overview)) metadata.Overview = overview;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }

            return true;
        }

        private static bool TryResolveTvRichMetadata(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            MediaTitleInfo info,
            bool includeReviews,
            CancellationToken ct,
            out RichMetadata metadata)
        {
            metadata = new RichMetadata { MediaType = "tv" };
            if (string.IsNullOrWhiteSpace(searchTitle))
                return false;

            string query = Uri.EscapeDataString(searchTitle);
            string url = $"https://api.themoviedb.org/3/search/tv?api_key={TmdbApiKey}&language={language}&query={query}";

            using var resp = GetTmdbResponse(url, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
                return false;

            int count = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (count++ >= 8)
                    break;

                string? candidateTitle = ReadJsonString(result, "name");
                string? candidateOriginalTitle = ReadJsonString(result, "original_name");
                if (string.IsNullOrWhiteSpace(candidateTitle))
                    candidateTitle = candidateOriginalTitle;

                int? candidateYear = ReadYearFromDate(result, "first_air_date");

                int seriesId = 0;
                if (result.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    idProp.TryGetInt32(out seriesId);

                // DLNA episode durations vary (and TMDb's series-level runtime is not
                // a reliable match). Fetching /tv/{id} for every search result added
                // up to eight serial HTTP requests before the actual metadata query.
                if (!IsAcceptableTvMatch(searchTitle, searchYear, null, candidateTitle, candidateOriginalTitle, candidateYear, null))
                    continue;

                if (searchYear.HasValue && candidateYear.HasValue && Math.Abs(candidateYear.Value - searchYear.Value) > 8)
                    continue;

                metadata.Title = seriesId > 0
                    ? ResolveTvDisplayTitle(seriesId, language, info, candidateTitle ?? candidateOriginalTitle ?? searchTitle, ct)
                    : candidateTitle ?? candidateOriginalTitle ?? searchTitle;
                metadata.Year = candidateYear;
                metadata.Overview = ReadJsonString(result, "overview");
                metadata.Genres = ReadGenreNamesFromSearchResult(result, "tv");
                if (seriesId > 0)
                {
                    metadata.TmdbId = seriesId;
                    TryEnrichWithEpisodeDetails(seriesId, language, info, metadata, ct);
                    EnrichTmdbPresentation("tv", seriesId, language, metadata, includeReviews, ct);
                    EnrichTmdbCredits("tv", seriesId, metadata, ct);
                }

                return true;
            }

            return false;
        }

        private static void TryEnrichWithEpisodeDetails(
            int seriesId,
            string language,
            MediaTitleInfo info,
            RichMetadata metadata,
            CancellationToken ct)
        {
            if (!info.SeasonNumber.HasValue || !info.EpisodeNumber.HasValue)
                return;

            try
            {
                string url = $"https://api.themoviedb.org/3/tv/{seriesId}/season/{info.SeasonNumber.Value}/episode/{info.EpisodeNumber.Value}?api_key={TmdbApiKey}&language={language}";
                using var resp = GetTmdbResponse(url, ct);
                if (!resp.IsSuccessStatusCode)
                    return;

                string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                string? overview = ReadJsonString(doc.RootElement, "overview");
                if (!string.IsNullOrWhiteSpace(overview))
                    metadata.Overview = overview;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }
        }

        private static void EnrichTmdbPresentation(string mediaType, int id, string language, RichMetadata metadata, bool includeReviews, CancellationToken ct)
        {
            if (id <= 0)
                return;

            try
            {
                string type = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
                string append = includeReviews ? "external_ids,reviews" : "external_ids";
                string url = $"https://api.themoviedb.org/3/{type}/{id}?api_key={TmdbApiKey}&language={language}&append_to_response={append}";
                using var resp = GetTmdbResponse(url, ct);
                if (!resp.IsSuccessStatusCode)
                    return;

                string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? tagline = ReadJsonString(root, "tagline");
                if (!string.IsNullOrWhiteSpace(tagline))
                    metadata.Tagline = tagline;
                if (root.TryGetProperty("vote_average", out var vote) && vote.ValueKind == JsonValueKind.Number && vote.TryGetDouble(out double rating) && rating > 0)
                    metadata.Rating = rating;

                var genres = new List<string>();
                if (root.TryGetProperty("genres", out var genreArray) && genreArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var genre in genreArray.EnumerateArray())
                    {
                        string? name = NormalizeGenreName(ReadJsonString(genre, "name"));
                        if (!string.IsNullOrWhiteSpace(name) && !genres.Contains(name, StringComparer.OrdinalIgnoreCase))
                            genres.Add(name);
                    }
                }
                if (genres.Count > 0)
                    metadata.Genres = genres;

                if (type == "tv" && root.TryGetProperty("created_by", out var creators) && creators.ValueKind == JsonValueKind.Array)
                {
                    metadata.Director = string.Join(", ", creators.EnumerateArray()
                        .Select(person => ReadJsonString(person, "name"))
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Take(2));
                }

                metadata.ImdbId = ReadJsonString(root, "imdb_id");
                if (string.IsNullOrWhiteSpace(metadata.ImdbId) &&
                    root.TryGetProperty("external_ids", out var externalIds) &&
                    externalIds.ValueKind == JsonValueKind.Object)
                {
                    metadata.ImdbId = ReadJsonString(externalIds, "imdb_id");
                }

                var snippets = new List<RichReview>();
                if (includeReviews && root.TryGetProperty("reviews", out var reviewsRoot) &&
                    reviewsRoot.ValueKind == JsonValueKind.Object &&
                    reviewsRoot.TryGetProperty("results", out var reviews) &&
                    reviews.ValueKind == JsonValueKind.Array)
                    AppendTmdbReviews(reviews, snippets, null);

                // TMDb filtra le recensioni per lingua: in italiano sono quasi sempre 0-3.
                // Si completa con quelle in inglese (segnate), le più votate per prime.
                if (includeReviews && snippets.Count < MaxReviews && !language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var english = GetTmdbResponse($"https://api.themoviedb.org/3/{type}/{id}/reviews?api_key={TmdbApiKey}&language=en-US&page=1", ct);
                        if (english.IsSuccessStatusCode)
                        {
                            using var englishDoc = JsonDocument.Parse(english.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                            if (englishDoc.RootElement.TryGetProperty("results", out var englishReviews) && englishReviews.ValueKind == JsonValueKind.Array)
                                AppendTmdbReviews(englishReviews, snippets, "en");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                metadata.Reviews = snippets;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        // Una pagina TMDb intera: con 6 i film molto recensiti ne mostravano solo una parte.
        private const int MaxReviews = 20;

        private static void AppendTmdbReviews(JsonElement reviews, List<RichReview> snippets, string? languageTag)
        {
            var candidates = new List<RichReview>();
            foreach (var review in reviews.EnumerateArray())
            {
                string content = Regex.Replace(ReadJsonString(review, "content") ?? string.Empty, @"\s+", " ").Trim();
                // Markdown/HTML leggero dei testi TMDb.
                content = Regex.Replace(content, @"<[^>]+>|\*\*|__", string.Empty).Trim();
                if (content.Length < 40)
                    continue;

                string author = ReadJsonString(review, "author") ?? "TMDb user";
                if (snippets.Any(s => string.Equals(s.Author, author, StringComparison.OrdinalIgnoreCase)))
                    continue;
                double? reviewRating = null;
                if (review.TryGetProperty("author_details", out var authorDetails) &&
                    authorDetails.ValueKind == JsonValueKind.Object &&
                    authorDetails.TryGetProperty("rating", out var ratingValue) &&
                    ratingValue.ValueKind == JsonValueKind.Number &&
                    ratingValue.TryGetDouble(out double parsedRating))
                {
                    reviewRating = parsedRating;
                }
                candidates.Add(new RichReview { Author = author, Content = content, Rating = reviewRating, Language = languageTag });
            }
            // Prima le recensioni con voto, poi le altre, mantenendo l'ordine TMDb.
            foreach (var review in candidates.OrderByDescending(r => r.Rating.HasValue))
            {
                if (snippets.Count >= MaxReviews) break;
                snippets.Add(review);
            }
        }

        private static List<string> ReadGenreNamesFromSearchResult(JsonElement result, string mediaType)
        {
            var genres = new List<string>();
            try
            {
                if (!result.TryGetProperty("genre_ids", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return genres;

                foreach (var idProp in arr.EnumerateArray())
                {
                    if (idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt32(out int id))
                        continue;

                    string? name = GenreNameFromTmdbId(mediaType, id);
                    name = NormalizeGenreName(name);
                    if (!string.IsNullOrWhiteSpace(name) && !genres.Contains(name, StringComparer.OrdinalIgnoreCase))
                        genres.Add(name);
                }
            }
            catch { }

            return genres;
        }

        private static string? GenreNameFromTmdbId(string mediaType, int id)
        {
            bool tv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
            return id switch
            {
                28 => "Azione",
                12 => "Avventura",
                16 => "Animazione",
                35 => "Commedia",
                80 => "Crime",
                99 => "Documentario",
                18 => "Dramma",
                10751 => "Famiglia",
                14 => "Fantasy",
                36 => "Storia",
                27 => "Horror",
                10402 => "Musica",
                9648 => "Mistero",
                10749 => "Romance",
                878 => "Fantascienza",
                10770 => "Film TV",
                53 => "Thriller",
                10752 => "Guerra",
                37 => "Western",
                10759 when tv => "Azione e avventura",
                10762 when tv => "Kids",
                10763 when tv => "News",
                10764 when tv => "Reality",
                10765 when tv => "Sci-Fi e fantasy",
                10766 when tv => "Soap",
                10767 when tv => "Talk",
                10768 when tv => "Guerra e politica",
                _ => null
            };
        }

        private static string? NormalizeGenreName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            string text = Regex.Replace(value.Trim(), @"\s+", " ");
            return text switch
            {
                "Science Fiction" => "Fantascienza",
                "Sci-Fi & Fantasy" => "Sci-Fi e fantasy",
                "Action & Adventure" => "Azione e avventura",
                "War & Politics" => "Guerra e politica",
                "Family" => "Famiglia",
                "History" => "Storia",
                "Mystery" => "Mistero",
                "Adventure" => "Avventura",
                "Action" => "Azione",
                "Drama" => "Dramma",
                "Comedy" => "Commedia",
                "Animation" => "Animazione",
                "Documentary" => "Documentario",
                "Music" => "Musica",
                "War" => "Guerra",
                _ => text
            };
        }

        private static void EnrichTmdbCredits(string mediaType, int id, RichMetadata metadata, CancellationToken ct)
        {
            if (id <= 0)
                return;

            try
            {
                string endpoint = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    ? $"https://api.themoviedb.org/3/tv/{id}/aggregate_credits?api_key={TmdbApiKey}"
                    : $"https://api.themoviedb.org/3/movie/{id}/credits?api_key={TmdbApiKey}";

                using var resp = GetTmdbResponse(endpoint, ct);
                if (!resp.IsSuccessStatusCode)
                    return;

                string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array)
                    return;

                var names = new List<string>();
                var members = new List<RichCastMember>();
                foreach (var person in cast.EnumerateArray())
                {
                    string? name = ReadJsonString(person, "name");
                    if (string.IsNullOrWhiteSpace(name) || names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;

                    names.Add(name!);
                    string character = ReadJsonString(person, "character") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(character) && person.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
                    {
                        var role = roles.EnumerateArray().FirstOrDefault();
                        character = ReadJsonString(role, "character") ?? string.Empty;
                    }
                    string? profile = ReadJsonString(person, "profile_path");
                    members.Add(new RichCastMember
                    {
                        Name = name!,
                        Character = character,
                        ProfilePath = string.IsNullOrWhiteSpace(profile) ? null : "https://image.tmdb.org/t/p/w342" + profile
                    });
                    if (names.Count >= 20)
                        break;
                }
                metadata.Cast = names.Take(6).ToList();
                metadata.CastMembers = members;

                if (string.IsNullOrWhiteSpace(metadata.Director) &&
                    doc.RootElement.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
                {
                    metadata.Director = string.Join(", ", crew.EnumerateArray()
                        .Where(person => string.Equals(ReadJsonString(person, "job"), "Director", StringComparison.OrdinalIgnoreCase))
                        .Select(person => ReadJsonString(person, "name"))
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(2));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }

        }

        private static string? ReadJsonString(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                    return prop.GetString();
            }
            catch { }
            return null;
        }

        private static int? ReadYearFromDate(JsonElement element, string propertyName)
        {
            string? value = ReadJsonString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value) && value!.Length >= 4 && int.TryParse(value.Substring(0, 4), out var year))
                return year;
            return null;
        }

        /// <summary>
        /// Versione "vecchia": non passa la durata.
        /// </summary>
        private static bool ShouldInvalidateResolvedMovieCache(MediaTitleInfo parsed, string? cachedTitle, int? cachedYear, bool cachedTitleResolved)
        {
            if (parsed == null || parsed.IsTvEpisode)
                return false;

            if (parsed.Year.HasValue && cachedYear.HasValue && Math.Abs(parsed.Year.Value - cachedYear.Value) >= 2)
                return true;

            if (string.IsNullOrWhiteSpace(parsed.NormalizedTitle) || string.IsNullOrWhiteSpace(cachedTitle))
                return false;

            if (!cachedTitleResolved)
            {
                string parsedComparison = NormalizeTitleForComparisonString(parsed.NormalizedTitle);
                string cachedComparison = NormalizeTitleForComparisonString(cachedTitle);
                return !string.IsNullOrWhiteSpace(parsedComparison) &&
                       !string.Equals(parsedComparison, cachedComparison, StringComparison.OrdinalIgnoreCase);
            }

            return HasSequelOrdinalConflict(parsed.NormalizedTitle, cachedTitle);
        }

        private static bool ShouldForceMovieArtworkRefresh(MediaTitleInfo parsed, bool cachedTitleResolved)
        {
            if (parsed == null || parsed.IsTvEpisode || cachedTitleResolved)
                return false;

            return TryExtractExplicitSequelOrdinal(parsed.NormalizedTitle, out _);
        }

        private static bool LooksLikeGenericLibraryTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            string normalized = NormalizeTitleCasing(title);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            if (IsGenericLibraryFolderName(normalized) || IsDriveLikeFolderName(normalized))
                return true;

            string comparison = NormalizeTitleForComparisonString(normalized);
            return comparison.StartsWith("filmdisco", StringComparison.OrdinalIgnoreCase)
                || comparison.StartsWith("disk", StringComparison.OrdinalIgnoreCase)
                || comparison.StartsWith("drive", StringComparison.OrdinalIgnoreCase)
                || comparison.StartsWith("downloads", StringComparison.OrdinalIgnoreCase)
                || comparison.StartsWith("media", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldInvalidateCachedTvEntry(MediaTitleInfo parsed, string? cachedTitle, int? cachedYear, bool cachedTitleResolved)
        {
            if (parsed == null || !parsed.IsTvEpisode)
                return false;

            if (string.IsNullOrWhiteSpace(cachedTitle))
                return false;

            if (LooksLikeGenericLibraryTitle(cachedTitle))
                return true;

            if (cachedYear.HasValue && parsed.Year.HasValue && Math.Abs(cachedYear.Value - parsed.Year.Value) >= 2 && !cachedTitleResolved)
                return true;

            if (cachedTitleResolved)
                return false;

            string parsedDisplay = NormalizeTitleForComparisonString(parsed.NormalizedTitle ?? string.Empty);
            string cachedDisplay = NormalizeTitleForComparisonString(cachedTitle ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(parsedDisplay) && !string.Equals(parsedDisplay, cachedDisplay, StringComparison.OrdinalIgnoreCase))
                return true;

            string parsedSeries = NormalizeTitleForComparisonString(parsed.SeriesTitle ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(parsedSeries) && !string.IsNullOrWhiteSpace(cachedDisplay) && !cachedDisplay.Contains(parsedSeries, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static string? TryReuseEquivalentPosterFromCache(string filePath, string? title, int? year, bool allowTvEpisodePrefix = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(title))
                    return null;

                var reused = _posterIndex.FindEquivalentPosterPath(filePath, title, year, allowTvEpisodePrefix);
                if (!string.IsNullOrWhiteSpace(reused) && File.Exists(reused))
                    return reused;
            }
            catch { }

            return null;
        }

        private static string? TryReuseEquivalentBackdropFromCache(string filePath, string? title, int? year, bool allowTvEpisodePrefix = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(title))
                    return null;

                var reused = _posterIndex.FindEquivalentBackdropPath(filePath, title, year, allowTvEpisodePrefix);
                if (!string.IsNullOrWhiteSpace(reused) && File.Exists(reused))
                    return reused;
            }
            catch { }

            return null;
        }


        public static (string? normalizedTitle, int? year, string? localPosterPath) ResolveTitleAndPoster(
            string filePath,
            CancellationToken ct)
            => ResolveTitleAndPoster(filePath, null, ct);

        /// <summary>
        /// Versione estesa:
        ///  - filePath: path del file
        ///  - durationSeconds: durata stimata in secondi (puoi leggerla da durationIndex.json)
        /// </summary>

        public static (string? normalizedTitle, int? year, string? localPosterPath) ResolveTitleAndPoster(
            string filePath,
            double? durationSeconds,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return (null, null, null);

            var parsed = ExtractMediaTitleInfoFromPath(filePath);

            var cached = _posterIndex.TryGet(filePath);
            string? cachedTitle = cached?.title;
            int? cachedYear = cached?.year;
            string? cachedPoster = cached?.localPosterPath;
            bool cachedTitleResolved = cached?.titleResolved ?? false;
            bool cachedLanguageMatches = !cachedTitleResolved || CacheLanguageMatches(cached?.language);
            if (!cachedLanguageMatches)
            {
                cachedPoster = null;
                cachedTitleResolved = false;
                cachedTitle = parsed.NormalizedTitle;
                cachedYear = parsed.Year;
            }

            if (ShouldInvalidateResolvedMovieCache(parsed, cachedTitle, cachedYear, cachedTitleResolved) ||
                ShouldInvalidateCachedTvEntry(parsed, cachedTitle, cachedYear, cachedTitleResolved))
            {
                try { _posterIndex.Reset(filePath, parsed.NormalizedTitle, parsed.Year); } catch { }
                cachedTitle = parsed.NormalizedTitle;
                cachedYear = parsed.Year;
                cachedPoster = null;
                cachedTitleResolved = false;
            }

            string? title = !string.IsNullOrWhiteSpace(cachedTitle) ? cachedTitle : parsed.NormalizedTitle;
            int? year = cachedYear ?? parsed.Year;

            if (!string.IsNullOrWhiteSpace(title) || year.HasValue || !string.IsNullOrWhiteSpace(cachedPoster))
            {
                _posterIndex.Update(filePath, title, year, cachedPoster, titleResolved: cachedTitleResolved);
            }

            bool forceArtworkRefresh = ShouldForceMovieArtworkRefresh(parsed, cachedTitleResolved);

            // Il poster in cache si mostra subito anche se il titolo non e' confermato: il titolo
            // localizzato si aggiorna in background (ScheduleCachedLocalizedMovieTitleRefresh).
            // Riprovare TMDb qui in modo sincrono rallentava la comparsa delle copertine.
            if (IsUsablePosterImage(cachedPoster) && !forceArtworkRefresh && cachedLanguageMatches)
            {
                if (parsed.IsTvEpisode)
                {
                    TryRefreshCachedTvDisplayTitleIfNeeded(filePath, parsed, durationSeconds, cachedPoster, null, ref title, ref year, ct);
                }
                else
                {
                    ScheduleCachedLocalizedMovieTitleRefresh(filePath, parsed, durationSeconds, cachedPoster, null);
                }

                return (title ?? parsed.NormalizedTitle, year ?? parsed.Year, cachedPoster);
            }

            string? equivalentLookupTitle = parsed.IsTvEpisode
                ? (!string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.SeriesTitle : title)
                : title;

            if (!forceArtworkRefresh)
            {
                var reusedPoster = TryReuseEquivalentPosterFromCache(filePath, equivalentLookupTitle, year, parsed.IsTvEpisode);
                if (IsUsablePosterImage(reusedPoster))
                {
                    _posterIndex.Update(filePath, title, year, reusedPoster, titleResolved: cachedTitleResolved || !string.IsNullOrWhiteSpace(title));
                    return (title ?? parsed.NormalizedTitle, year ?? parsed.Year, reusedPoster);
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = parsed.NormalizedTitle;
                year = parsed.Year;
            }

            if (string.IsNullOrWhiteSpace(title))
                return (parsed.NormalizedTitle, parsed.Year, null);

            string? tmdbTitle = null;
            int? tmdbYear = null;

            string? posterPath;
            if (parsed.IsTvEpisode)
            {
                posterPath = TryDownloadTvPoster(parsed, durationSeconds, ct, out tmdbTitle, out tmdbYear);
            }
            else
            {
                posterPath = TryDownloadPoster(title, year, durationSeconds, ct, out tmdbTitle, out tmdbYear);
                if (string.IsNullOrWhiteSpace(posterPath) &&
                    !string.IsNullOrWhiteSpace(parsed.NormalizedTitle) &&
                    !string.Equals(title, parsed.NormalizedTitle, StringComparison.OrdinalIgnoreCase))
                {
                    posterPath = TryDownloadPoster(
                        parsed.NormalizedTitle,
                        parsed.Year ?? year,
                        durationSeconds,
                        ct,
                        out string? fallbackTmdbTitle,
                        out int? fallbackTmdbYear);
                    if (!string.IsNullOrWhiteSpace(fallbackTmdbTitle))
                        tmdbTitle = fallbackTmdbTitle;
                    if (fallbackTmdbYear.HasValue)
                        tmdbYear = fallbackTmdbYear;
                }
            }

            if (!string.IsNullOrWhiteSpace(posterPath) && File.Exists(posterPath))
            {
                if (!string.IsNullOrWhiteSpace(tmdbTitle))
                    title = tmdbTitle;
                if (tmdbYear.HasValue)
                    year = tmdbYear;

                _posterIndex.Update(filePath, title, year, posterPath, titleResolved: !string.IsNullOrWhiteSpace(tmdbTitle));
                return (title, year, posterPath);
            }

            // Ricerca non riuscita (es. offline): resta valido il poster gia' in cache.
            if (IsUsablePosterImage(cachedPoster) && cachedLanguageMatches)
                return (title ?? parsed.NormalizedTitle, year ?? parsed.Year, cachedPoster);

            if (!string.IsNullOrWhiteSpace(tmdbTitle))
                title = tmdbTitle;
            if (tmdbYear.HasValue)
                year = tmdbYear;

            _posterIndex.Update(filePath, title, year, null, titleResolved: !string.IsNullOrWhiteSpace(tmdbTitle));
            return (title, year, null);
        }

        /// <summary>
        /// Variante per recuperare un backdrop 16:9 locale (best-effort) da usare nel placeholder.
        /// </summary>
        public static (string? normalizedTitle, int? year, string? localBackdropPath) ResolveTitleAndBackdrop(
            string filePath,
            CancellationToken ct)
            => ResolveTitleAndBackdrop(filePath, null, ct);


        public static (string? normalizedTitle, int? year, string? localBackdropPath) ResolveTitleAndBackdrop(
            string filePath,
            double? durationSeconds,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return (null, null, null);

            var parsed = ExtractMediaTitleInfoFromPath(filePath);

            var cached = _posterIndex.TryGetBackdrop(filePath);
            string? cachedTitle = cached?.title;
            int? cachedYear = cached?.year;
            string? cachedBackdrop = cached?.localBackdropPath;
            bool cachedTitleResolved = cached?.titleResolved ?? false;
            bool cachedLanguageMatches = !cachedTitleResolved || CacheLanguageMatches(cached?.language);
            if (!cachedLanguageMatches)
            {
                cachedBackdrop = null;
                cachedTitleResolved = false;
                cachedTitle = parsed.NormalizedTitle;
                cachedYear = parsed.Year;
            }

            if (ShouldInvalidateResolvedMovieCache(parsed, cachedTitle, cachedYear, cachedTitleResolved) ||
                ShouldInvalidateCachedTvEntry(parsed, cachedTitle, cachedYear, cachedTitleResolved))
            {
                try { _posterIndex.Reset(filePath, parsed.NormalizedTitle, parsed.Year); } catch { }
                cachedTitle = parsed.NormalizedTitle;
                cachedYear = parsed.Year;
                cachedBackdrop = null;
                cachedTitleResolved = false;
            }

            string? title = !string.IsNullOrWhiteSpace(cachedTitle) ? cachedTitle : parsed.NormalizedTitle;
            int? year = cachedYear ?? parsed.Year;

            if (!string.IsNullOrWhiteSpace(title) || year.HasValue || !string.IsNullOrWhiteSpace(cachedBackdrop))
            {
                _posterIndex.Update(filePath, title, year, null, cachedBackdrop, titleResolved: cachedTitleResolved);
            }

            bool forceArtworkRefresh = ShouldForceMovieArtworkRefresh(parsed, cachedTitleResolved);

            if (!string.IsNullOrWhiteSpace(cachedBackdrop) && File.Exists(cachedBackdrop) && !forceArtworkRefresh && cachedLanguageMatches)
            {
                if (IsBackdropFullResolution(cachedBackdrop))
                {
                    if (parsed.IsTvEpisode)
                    {
                        TryRefreshCachedTvDisplayTitleIfNeeded(filePath, parsed, durationSeconds, null, cachedBackdrop, ref title, ref year, ct);
                    }
                    else
                    {
                        ScheduleCachedLocalizedMovieTitleRefresh(filePath, parsed, durationSeconds, null, cachedBackdrop);
                    }

                    return (title ?? parsed.NormalizedTitle, year ?? parsed.Year, cachedBackdrop);
                }

                cachedBackdrop = null;
            }

            string? equivalentBackdropLookupTitle = parsed.IsTvEpisode
                ? (!string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.SeriesTitle : title)
                : title;

            if (!forceArtworkRefresh)
            {
                var reusedBackdrop = TryReuseEquivalentBackdropFromCache(filePath, equivalentBackdropLookupTitle, year, parsed.IsTvEpisode);
                if (!string.IsNullOrWhiteSpace(reusedBackdrop))
                {
                    _posterIndex.Update(filePath, title, year, null, reusedBackdrop, titleResolved: cachedTitleResolved || !string.IsNullOrWhiteSpace(title));
                    return (title ?? parsed.NormalizedTitle, year ?? parsed.Year, reusedBackdrop);
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = parsed.NormalizedTitle;
                year = parsed.Year;
            }

            if (string.IsNullOrWhiteSpace(title))
                return (parsed.NormalizedTitle, parsed.Year, null);

            string? tmdbTitle = null;
            int? tmdbYear = null;

            string? backdropPath;
            if (parsed.IsTvEpisode)
            {
                backdropPath = TryDownloadTvBackdrop(parsed, durationSeconds, ct, out tmdbTitle, out tmdbYear);
            }
            else
            {
                backdropPath = TryDownloadBackdrop(title, year, durationSeconds, ct, out tmdbTitle, out tmdbYear);
            }

            if (!string.IsNullOrWhiteSpace(backdropPath) && File.Exists(backdropPath))
            {
                if (!string.IsNullOrWhiteSpace(tmdbTitle))
                    title = tmdbTitle;
                if (tmdbYear.HasValue)
                    year = tmdbYear;

                _posterIndex.Update(filePath, title, year, null, backdropPath, titleResolved: !string.IsNullOrWhiteSpace(tmdbTitle));
                return (title, year, backdropPath);
            }

            if (!string.IsNullOrWhiteSpace(tmdbTitle))
                title = tmdbTitle;
            if (tmdbYear.HasValue)
                year = tmdbYear;

            _posterIndex.Update(filePath, title, year, null, null, titleResolved: !string.IsNullOrWhiteSpace(tmdbTitle));
            return (title, year, null);
        }

        public static string? ResolveEpisodeStillPath(string filePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            MediaTitleInfo parsed;
            try { parsed = ExtractMediaTitleInfoFromPath(filePath); }
            catch { return null; }

            if (!parsed.IsTvEpisode || !parsed.SeasonNumber.HasValue || !parsed.EpisodeNumber.HasValue)
                return null;

            if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                return null;

            string searchTitle = !string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.SeriesTitle! : parsed.NormalizedTitle;
            if (string.IsNullOrWhiteSpace(searchTitle))
                return null;

            try
            {
                foreach (var titleVariant in BuildTvSearchTitleVariants(searchTitle))
                {
                    foreach (var request in BuildLanguageYearAttempts(parsed.Year))
                    {
                        if (TryOneTmdbEpisodeStillCall(titleVariant, request.Year, request.Language, parsed, ct, out var localStillPath, out var tmdbTitle, out var tmdbYear) &&
                            IsLandscapeStillImage(localStillPath))
                        {
                            _posterIndex.Update(filePath, tmdbTitle ?? parsed.NormalizedTitle, tmdbYear ?? parsed.Year, null, localStillPath, titleResolved: !string.IsNullOrWhiteSpace(tmdbTitle));
                            return localStillPath;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }

            return null;
        }



        private static bool IsClearlyGenericTvDisplayTitle(string? cachedTitle)
        {
            if (string.IsNullOrWhiteSpace(cachedTitle))
                return false;

            try
            {
                string display = NormalizeTitleCasing(cachedTitle ?? string.Empty);
                string prefix = display;
                int sepIndex = display.IndexOf('•');
                if (sepIndex >= 0)
                    prefix = display.Substring(0, sepIndex).Trim();

                if (string.IsNullOrWhiteSpace(prefix))
                    return true;

                if (IsGenericLibraryFolderName(prefix) || ContainsReleaseNoise(prefix))
                    return true;

                if (Regex.IsMatch(prefix,
                    @"(?ix)\b(?:film|films|movie|movies|video|videos|media|download|downloads)(?:\s*(?:disco|disk|drive))?(?:\s*[a-z0-9]+)?\b"))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool ShouldIgnoreCachedMovieEntry(string filePath, string? cachedTitle, int? cachedYear, bool titleResolved)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return false;

                var parsed = ExtractMediaTitleInfoFromPath(filePath);
                if (parsed == null)
                    return false;

                if (parsed.IsTvEpisode)
                {
                    if (IsClearlyGenericTvDisplayTitle(cachedTitle))
                        return true;

                    if (!titleResolved)
                    {
                        if (!cachedYear.HasValue && parsed.Year.HasValue)
                            return true;

                        string parsedSeries = NormalizeTitleForComparisonString(parsed.SeriesTitle ?? string.Empty);
                        string cachedSeries = NormalizeTitleForComparisonString(cachedTitle ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(parsedSeries) &&
                            !string.IsNullOrWhiteSpace(cachedSeries) &&
                            !cachedSeries.Contains(parsedSeries, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                return ShouldInvalidateResolvedMovieCache(parsed, cachedTitle, cachedYear, titleResolved) ||
                       ShouldForceMovieArtworkRefresh(parsed, titleResolved);
            }
            catch
            {
                return false;
            }
        }

        public static string? GetCachedNormalizedTitle(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;
            if (TryGetManualTitle(filePath, out string manualTitle, out _)) return manualTitle;

            try
            {
                var poster = _posterIndex.TryGet(filePath);
                if (!string.IsNullOrWhiteSpace(poster?.title) &&
                    (!(poster?.titleResolved ?? false) || CacheLanguageMatches(poster?.language)) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, poster?.title, poster?.year, poster?.titleResolved ?? false))
                {
                    return poster?.title;
                }
            }
            catch { }

            try
            {
                var backdrop = _posterIndex.TryGetBackdrop(filePath);
                if (!string.IsNullOrWhiteSpace(backdrop?.title) &&
                    (!(backdrop?.titleResolved ?? false) || CacheLanguageMatches(backdrop?.language)) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, backdrop?.title, backdrop?.year, backdrop?.titleResolved ?? false))
                {
                    return backdrop?.title;
                }
            }
            catch { }

            return null;
        }

        public static int? GetCachedYear(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;
            if (TryGetManualTitle(filePath, out _, out int? manualYear) && manualYear.HasValue) return manualYear;

            static bool Plausible(int? year)
                => year.HasValue && year.Value is >= 1888 and <= 2100;

            try
            {
                var poster = _posterIndex.TryGet(filePath);
                if (Plausible(poster?.year) &&
                    (!(poster?.titleResolved ?? false) || CacheLanguageMatches(poster?.language)) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, poster?.title, poster?.year, poster?.titleResolved ?? false))
                {
                    return poster?.year;
                }
            }
            catch { }

            try
            {
                var backdrop = _posterIndex.TryGetBackdrop(filePath);
                if (Plausible(backdrop?.year) &&
                    (!(backdrop?.titleResolved ?? false) || CacheLanguageMatches(backdrop?.language)) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, backdrop?.title, backdrop?.year, backdrop?.titleResolved ?? false))
                {
                    return backdrop?.year;
                }
            }
            catch { }

            // Stesso film gia' riconosciuto sotto un altro percorso. Solo per un percorso vero:
            // le chiavi "titolo" passano da qui a ogni ricostruzione della griglia.
            try
            {
                if (EquivalentYearCache.TryGetValue(filePath, out int known))
                    return known;
                if (filePath.IndexOfAny(new[] { '\\', '/' }) >= 0)
                {
                    var parsed = ExtractMediaTitleInfoFromPath(filePath);
                    if (!parsed.IsTvEpisode && !parsed.Year.HasValue && !string.IsNullOrWhiteSpace(parsed.NormalizedTitle))
                    {
                        int? equivalent = _posterIndex.FindEquivalentYear(filePath, parsed.NormalizedTitle);
                        if (Plausible(equivalent))
                        {
                            EquivalentYearCache[filePath] = equivalent!.Value;
                            return equivalent;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> EquivalentYearCache = new(StringComparer.OrdinalIgnoreCase);

        public static string? GetCachedPosterPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;
            if (GetManualPoster(filePath) is string manualPoster) return manualPoster;

            try
            {
                var cached = _posterIndex.TryGet(filePath);
                if (cached is { } cachedEntry &&
                    !string.IsNullOrWhiteSpace(cachedEntry.localPosterPath) &&
                    IsUsablePosterImage(cachedEntry.localPosterPath) &&
                    (!cachedEntry.titleResolved || CacheLanguageMatches(cachedEntry.language)) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, cachedEntry.title, cachedEntry.year, cachedEntry.titleResolved))
                {
                    return cachedEntry.localPosterPath;
                }

                var parsed = ExtractMediaTitleInfoFromPath(filePath);
                string? lookupTitle = parsed.IsTvEpisode && !string.IsNullOrWhiteSpace(parsed.SeriesTitle)
                    ? parsed.SeriesTitle
                    : (!string.IsNullOrWhiteSpace(cached?.title) ? cached?.title : parsed.NormalizedTitle);
                int? lookupYear = cached?.year ?? parsed.Year;

                var reusedPoster = TryReuseEquivalentPosterFromCache(filePath, lookupTitle, lookupYear, parsed.IsTvEpisode);
                if (IsUsablePosterImage(reusedPoster))
                {
                    _posterIndex.Update(filePath, lookupTitle, lookupYear, reusedPoster, titleResolved: cached?.titleResolved ?? false);
                    return reusedPoster;
                }
            }
            catch { }

            return null;
        }

        public static string? GetCachedBackdropPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            try
            {
                var cached = _posterIndex.TryGetBackdrop(filePath);
                if (cached is { } cachedEntry &&
                    !string.IsNullOrWhiteSpace(cachedEntry.localBackdropPath) &&
                    File.Exists(cachedEntry.localBackdropPath) &&
                    (!cachedEntry.titleResolved || CacheLanguageMatches(cachedEntry.language)) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, cachedEntry.title, cachedEntry.year, cachedEntry.titleResolved))
                {
                    return cachedEntry.localBackdropPath;
                }
            }
            catch { }

            try
            {
                var parsed = ExtractMediaTitleInfoFromPath(filePath);
                string? lookupTitle = parsed.IsTvEpisode && !string.IsNullOrWhiteSpace(parsed.SeriesTitle)
                    ? parsed.SeriesTitle
                    : parsed.NormalizedTitle;
                int? lookupYear = parsed.Year;

                var reusedBackdrop = TryReuseEquivalentBackdropFromCache(filePath, lookupTitle, lookupYear, parsed.IsTvEpisode);
                if (!string.IsNullOrWhiteSpace(reusedBackdrop) && File.Exists(reusedBackdrop))
                    return reusedBackdrop;
            }
            catch { }

            return null;
        }

        public static bool IsCachedTitleResolved(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                var cached = _posterIndex.TryGet(filePath);
                if (cached.HasValue &&
                    (!cached.Value.titleResolved || CacheLanguageMatches(cached.Value.language)) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, cached.Value.title, cached.Value.year, cached.Value.titleResolved))
                {
                    return cached.Value.titleResolved;
                }
            }
            catch { }

            try
            {
                var cached = _posterIndex.TryGetBackdrop(filePath);
                if (cached.HasValue &&
                    (!cached.Value.titleResolved || CacheLanguageMatches(cached.Value.language)) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, cached.Value.title, cached.Value.year, cached.Value.titleResolved))
                {
                    return cached.Value.titleResolved;
                }
            }
            catch { }

            return false;
        }

        public static string GetBestKnownDisplayTitle(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return string.Empty;

            string? cachedTitle = null;
            try { cachedTitle = GetCachedNormalizedTitle(filePath); } catch { }
            if (!string.IsNullOrWhiteSpace(cachedTitle))
                return cachedTitle!;

            try
            {
                var parsed = ExtractMediaTitleInfoFromPath(filePath);
                if (!string.IsNullOrWhiteSpace(parsed.NormalizedTitle))
                {
                    TryCacheParsedDisplayTitle(filePath, parsed.NormalizedTitle, parsed.Year);
                    return parsed.NormalizedTitle;
                }
            }
            catch { }

            try { return Path.GetFileNameWithoutExtension(filePath) ?? filePath; }
            catch { return filePath; }
        }

        private static void TryCacheParsedDisplayTitle(string filePath, string? parsedTitle, int? parsedYear)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(parsedTitle))
                    return;

                var cached = _posterIndex.TryGet(filePath);
                if (!string.IsNullOrWhiteSpace(cached?.title))
                    return;

                _posterIndex.Update(filePath, parsedTitle, parsedYear, null, titleResolved: false);
            }
            catch
            {
            }
        }

        private static bool ShouldRefreshLocalizedMovieTitle(string? cachedTitle, MediaTitleInfo parsed)
        {
            if (parsed == null || parsed.IsTvEpisode || string.IsNullOrWhiteSpace(parsed.NormalizedTitle))
                return false;

            if (string.IsNullOrWhiteSpace(cachedTitle))
                return true;

            string normalizedCached = Regex.Replace(cachedTitle ?? string.Empty, @"\s+", " ").Trim();
            string normalizedParsed = Regex.Replace(parsed.NormalizedTitle ?? string.Empty, @"\s+", " ").Trim();
            return string.Equals(normalizedCached, normalizedParsed, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldAttemptLocalizedMovieTitleRefresh(string filePath, string? cachedTitle, MediaTitleInfo parsed, bool titleResolved)
        {
            if (parsed == null || parsed.IsTvEpisode || string.IsNullOrWhiteSpace(parsed.NormalizedTitle) || string.IsNullOrWhiteSpace(filePath))
                return false;

            if (titleResolved && !string.IsNullOrWhiteSpace(cachedTitle))
                return false;

            if (ShouldRefreshLocalizedMovieTitle(cachedTitle, parsed))
                return true;

            lock (_localizedTitleRefreshSync)
            {
                return !titleResolved && !_localizedTitleRefreshCompleted.Contains(filePath);
            }
        }

        private static bool ShouldRefreshCachedTvDisplayTitle(string? cachedTitle, MediaTitleInfo parsed)
        {
            if (parsed == null || !parsed.IsTvEpisode || string.IsNullOrWhiteSpace(parsed.NormalizedTitle))
                return false;

            if (string.IsNullOrWhiteSpace(cachedTitle))
                return true;

            string normalizedCached = Regex.Replace(cachedTitle ?? string.Empty, @"\s+", " ").Trim();
            string normalizedParsed = Regex.Replace(parsed.NormalizedTitle ?? string.Empty, @"\s+", " ").Trim();
            return string.Equals(normalizedCached, normalizedParsed, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryRefreshCachedTvDisplayTitleIfNeeded(
            string filePath,
            MediaTitleInfo parsed,
            double? durationSeconds,
            string? localPosterPath,
            string? localBackdropPath,
            ref string? title,
            ref int? year,
            CancellationToken ct)
        {
            try
            {
                var poster = _posterIndex.TryGet(filePath);
                var backdrop = _posterIndex.TryGetBackdrop(filePath);
                bool titleResolved =
                    (poster?.titleResolved == true && CacheLanguageMatches(poster?.language)) ||
                    (backdrop?.titleResolved == true && CacheLanguageMatches(backdrop?.language));
                if (titleResolved && !string.IsNullOrWhiteSpace(title))
                    return;

                if (!ShouldRefreshCachedTvDisplayTitle(title, parsed))
                    return;

                var refreshedTitle = TryResolveTvDisplayTitleOnly(parsed, durationSeconds, ct, out var refreshedYear);
                if (string.IsNullOrWhiteSpace(refreshedTitle))
                    return;

                title = refreshedTitle;
                if (refreshedYear.HasValue)
                    year = refreshedYear;

                _posterIndex.Update(filePath, title, year, localPosterPath, localBackdropPath, titleResolved: true);
            }
            catch
            {
            }
        }

        private static string? TryResolveTvDisplayTitleOnly(
            MediaTitleInfo info,
            double? durationSeconds,
            CancellationToken ct,
            out int? tmdbYear)
        {
            tmdbYear = null;
            string? tmdbTitle = null;

            try
            {
                string searchTitle = !string.IsNullOrWhiteSpace(info.SeriesTitle) ? info.SeriesTitle! : info.NormalizedTitle;
                if (string.IsNullOrWhiteSpace(searchTitle))
                    return null;

                if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                    TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                    return null;

                foreach (var attempt in BuildLanguageYearAttempts(info.Year))
                {
                    string? tmpTitle = null;
                    int? tmpYear = null;
                    if (TryOneTmdbTvTitleCall(searchTitle, attempt.Year, durationSeconds, attempt.Language, info, ct, ref tmpTitle, ref tmpYear))
                    {
                        tmdbYear = tmpYear;
                        return tmpTitle;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                }

                return tmdbTitle;
            }
            catch
            {
                return tmdbTitle;
            }
        }

        private static bool TryOneTmdbTvTitleCall(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            MediaTitleInfo info,
            CancellationToken ct,
            ref string? tmdbTitle,
            ref int? tmdbYear)
        {
            string query = Uri.EscapeDataString(searchTitle);
            string url = $"https://api.themoviedb.org/3/search/tv?api_key={TmdbApiKey}&language={language}&query={query}";

            using var resp = GetTmdbResponse(url, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
                return false;

            int count = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (count++ >= 18)
                    break;

                string? candidateTitle = null;
                if (result.TryGetProperty("name", out var titleProp))
                    candidateTitle = titleProp.GetString();

                string? candidateOriginalTitle = null;
                if (result.TryGetProperty("original_name", out var origProp))
                    candidateOriginalTitle = origProp.GetString();

                if (string.IsNullOrWhiteSpace(candidateTitle))
                    candidateTitle = candidateOriginalTitle;

                int? candidateYear = null;
                if (result.TryGetProperty("first_air_date", out var dateProp))
                {
                    var rd = dateProp.GetString();
                    if (!string.IsNullOrWhiteSpace(rd) && rd!.Length >= 4 && int.TryParse(rd.Substring(0, 4), out var y))
                        candidateYear = y;
                }

                int seriesId = 0;
                if (result.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    idProp.TryGetInt32(out seriesId);

                if (!IsAcceptableTvMatch(searchTitle, searchYear, null, candidateTitle, candidateOriginalTitle, candidateYear, null))
                    continue;

                if (searchYear.HasValue && candidateYear.HasValue && Math.Abs(candidateYear.Value - searchYear.Value) > 8)
                    continue;

                tmdbTitle = ResolveTvDisplayTitle(seriesId, language, info, candidateTitle ?? candidateOriginalTitle ?? searchTitle, ct);
                tmdbYear = candidateYear;
                return !string.IsNullOrWhiteSpace(tmdbTitle);
            }

            return false;
        }

        private static void ScheduleCachedLocalizedMovieTitleRefresh(
            string filePath,
            MediaTitleInfo parsed,
            double? durationSeconds,
            string? localPosterPath,
            string? localBackdropPath)
        {
            try
            {
                if (parsed == null || parsed.IsTvEpisode || string.IsNullOrWhiteSpace(parsed.NormalizedTitle) || string.IsNullOrWhiteSpace(filePath))
                    return;

                var poster = _posterIndex.TryGet(filePath);
                var backdrop = _posterIndex.TryGetBackdrop(filePath);
                bool titleResolved =
                    (poster?.titleResolved == true && CacheLanguageMatches(poster?.language)) ||
                    (backdrop?.titleResolved == true && CacheLanguageMatches(backdrop?.language));
                if (titleResolved)
                    return;

                lock (_localizedTitleRefreshSync)
                {
                    if (_localizedTitleRefreshCompleted.Contains(filePath) || _localizedTitleRefreshInFlight.Contains(filePath))
                        return;

                    _localizedTitleRefreshInFlight.Add(filePath);
                }

                _ = Task.Run(() =>
                {
                    string? refreshedTitle = null;
                    int? refreshedYear = null;
                    try
                    {
                        try
                        {
                            var cached = _posterIndex.TryGet(filePath);
                            refreshedTitle = cached?.title;
                            refreshedYear = cached?.year;
                        }
                        catch { }

                        TryRefreshCachedLocalizedMovieTitle(
                            filePath,
                            parsed,
                            durationSeconds,
                            localPosterPath,
                            localBackdropPath,
                            ref refreshedTitle,
                            ref refreshedYear,
                            CancellationToken.None);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        lock (_localizedTitleRefreshSync)
                        {
                            _localizedTitleRefreshInFlight.Remove(filePath);
                            _localizedTitleRefreshCompleted.Add(filePath);
                        }
                    }
                });
            }
            catch
            {
            }
        }

        private static void TryRefreshCachedLocalizedMovieTitle(
            string filePath,
            MediaTitleInfo parsed,
            double? durationSeconds,
            string? localPosterPath,
            string? localBackdropPath,
            ref string? title,
            ref int? year,
            CancellationToken ct)
        {
            try
            {
                var poster = _posterIndex.TryGet(filePath);
                var backdrop = _posterIndex.TryGetBackdrop(filePath);
                bool titleResolved =
                    (poster?.titleResolved == true && CacheLanguageMatches(poster?.language)) ||
                    (backdrop?.titleResolved == true && CacheLanguageMatches(backdrop?.language));
                if (!ShouldAttemptLocalizedMovieTitleRefresh(filePath, title, parsed, titleResolved))
                    return;

                var refreshedTitle = TryResolveMovieTitleOnly(parsed.NormalizedTitle, parsed.Year, durationSeconds, ct, out var refreshedYear);
                if (string.IsNullOrWhiteSpace(refreshedTitle))
                    return;

                title = refreshedTitle;
                if (refreshedYear.HasValue)
                    year = refreshedYear;

                _posterIndex.Update(filePath, title, year, localPosterPath, localBackdropPath, titleResolved: true);
            }
            catch
            {
                // best effort
            }
        }

        private static string? TryResolveMovieTitleOnly(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            CancellationToken ct,
            out int? tmdbYear)
        {
            tmdbYear = null;
            string? resolvedTitle = null;
            int? resolvedYear = null;

            bool TryPass(double? durationForMatch, out string? foundTitle, out int? foundYear)
            {
                foundTitle = null;
                foundYear = null;

                foreach (var titleVariant in BuildMovieSearchTitleVariants(searchTitle))
                {
                    foreach (var attempt in BuildLanguageYearAttempts(searchYear))
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;
                        if (TryOneTmdbTitleOnlyCall(titleVariant, attempt.Year, durationForMatch, attempt.Language, ct, ref tmpTitle, ref tmpYear))
                        {
                            foundTitle = tmpTitle;
                            foundYear = tmpYear;
                            return true;
                        }
                        MergeResolvedTitle(ref resolvedTitle, ref resolvedYear, tmpTitle, tmpYear);
                    }
                }

                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(searchTitle))
                    return null;

                if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                    TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (TryPass(expectedDurationSeconds, out var strictTitle, out var strictYear))
                {
                    tmdbYear = strictYear;
                    return strictTitle;
                }

                // Title refresh was too strict for some correct files: if the title/year are
                // strong, retry without runtime so alternate cuts and bad TMDb runtimes do not
                // block a valid localized title.
                if (expectedDurationSeconds.HasValue && TryPass(null, out var relaxedTitle, out var relaxedYear))
                {
                    tmdbYear = relaxedYear;
                    return relaxedTitle;
                }
            }
            catch
            {
                tmdbYear = resolvedYear;
                return resolvedTitle;
            }

            tmdbYear = resolvedYear;
            return resolvedTitle;
        }

        private const int MinBackdropWidthForPlaceholder = 3800;
        private const int MinBackdropHeightForPlaceholder = 1600;

        private static bool IsBackdropFullResolution(string path)
        {
            try
            {
                using var img = Image.FromFile(path);
                return img.Width >= MinBackdropWidthForPlaceholder && img.Height >= MinBackdropHeightForPlaceholder;
            }
            catch
            {
                return false;
            }
        }



        private static readonly Regex TvEpisodeRegex = new Regex(
            @"(?:\bS(?<season>\d{1,2})\s*[-_. ]?\s*E(?<episode>\d{1,3})\b)|(?:\b(?<season2>\d{1,2})x(?<episode2>\d{1,3})\b)|(?:\b(?:season|stagione)\s*(?<season3>\d{1,2})\s*(?:episode|episodio|ep)\s*(?<episode3>\d{1,3})\b)|(?:\b(?:season|stagione|s)\s*(?<season4>\d{1,2})\b.*?\b(?:episode|episodio|ep|e)?\s*(?<episode4>\d{1,3})\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static MediaTitleInfo ExtractMediaTitleInfoFromPath(string path)
        {
            if (TryGetManualTitle(path, out string manualTitle, out int? manualYear))
                return new MediaTitleInfo { NormalizedTitle = manualTitle, Year = manualYear };
            var info = new MediaTitleInfo();

            string rawName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                info.NormalizedTitle = rawName;
                return info;
            }

            string parseSource = rawName;
            string cleaned = NormalizeEpisodeSourceString(parseSource);
            var match = TvEpisodeRegex.Match(cleaned);
            if (!match.Success)
            {
                parseSource = BuildEpisodeParseSource(path, rawName);
                cleaned = NormalizeEpisodeSourceString(parseSource);
                match = TvEpisodeRegex.Match(cleaned);
            }
            int? year = ExtractLikelyYear(parseSource);
            if (match.Success)
            {
                int? season = ParseGroupInt(match, "season") ?? ParseGroupInt(match, "season2") ?? ParseGroupInt(match, "season3") ?? ParseGroupInt(match, "season4");
                int? episode = ParseGroupInt(match, "episode") ?? ParseGroupInt(match, "episode2") ?? ParseGroupInt(match, "episode3") ?? ParseGroupInt(match, "episode4");

                string before = cleaned.Substring(0, match.Index).Trim(' ', '-', '.', '_');
                string after = cleaned.Substring(match.Index + match.Length).Trim(' ', '-', '.', '_');

                var (seriesTitle, seriesYear) = ExtractMovieTitleAndYearFromPath(before);
                if (string.IsNullOrWhiteSpace(seriesTitle) ||
                    Regex.IsMatch(seriesTitle, @"(?i)\b(season|stagione)\s*\d{1,2}\b") ||
                    Regex.IsMatch(seriesTitle, @"(?i)\bS\d{1,2}\b") ||
                    ContainsReleaseNoise(seriesTitle) ||
                    IsGenericEpisodeFileName(rawName))
                {
                    seriesTitle = TryExtractSeriesTitleFromFolders(path) ?? seriesTitle ?? before;
                }
                if (string.IsNullOrWhiteSpace(seriesTitle))
                    seriesTitle = NormalizeTitleCasing(rawName);

                string? episodeTitle = null;
                if (!string.IsNullOrWhiteSpace(after))
                {
                    var (episodeTitleNorm, _) = ExtractMovieTitleAndYearFromPath(after);
                    if (!string.IsNullOrWhiteSpace(episodeTitleNorm) && !ContainsReleaseNoise(episodeTitleNorm))
                        episodeTitle = episodeTitleNorm;
                }

                info.IsTvEpisode = true;
                info.SeriesTitle = NormalizeTitleCasing(seriesTitle);
                info.SeasonNumber = season;
                info.EpisodeNumber = episode;
                info.EpisodeTitle = string.IsNullOrWhiteSpace(episodeTitle) ? null : NormalizeTitleCasing(episodeTitle);
                info.Year = seriesYear ?? year;
                info.NormalizedTitle = BuildTvDisplayTitle(info.SeriesTitle, season, episode, info.EpisodeTitle);
                return info;
            }

            var (title, movieYear) = ExtractMovieTitleAndYearFromPath(path);
            info.NormalizedTitle = title;
            info.Year = movieYear;
            return info;
        }

        private static string BuildEpisodeParseSource(string path, string rawName)
        {
            try
            {
                var parts = new List<string>();
                var dir = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty);
                if (dir != null)
                {
                    if (dir.Parent != null && !string.IsNullOrWhiteSpace(dir.Parent.Name))
                        parts.Add(dir.Parent.Name);
                    if (!string.IsNullOrWhiteSpace(dir.Name))
                        parts.Add(dir.Name);
                }
                if (!string.IsNullOrWhiteSpace(rawName))
                    parts.Add(rawName);

                return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            }
            catch
            {
                return rawName;
            }
        }

        private static string NormalizeEpisodeSourceString(string value)
        {
            string s = value ?? string.Empty;
            s = s.Replace('–', '-').Replace('—', '-');
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            s = Regex.Replace(s, @"\([^\)]*\)", " ");
            s = Regex.Replace(s, @"\{[^\}]*\}", " ");
            s = s.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '.', '_');
            return s;
        }

        private static int? ExtractLikelyYear(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            int? year = null;
            int currentYear = DateTime.Now.Year;
            var yearMatches = Regex.Matches(value, @"\b(19[0-9]{2}|20[0-9]{2})\b");
            var yearCandidates = new List<int>();

            foreach (Match m in yearMatches)
            {
                if (int.TryParse(m.Value, out var yy) && yy >= 1900 && yy <= currentYear + 1)
                    yearCandidates.Add(yy);
            }

            if (yearCandidates.Count == 1)
                year = yearCandidates[0];
            else if (yearCandidates.Count > 1)
            {
                int min = int.MaxValue;
                int max = int.MinValue;
                foreach (var yy in yearCandidates)
                {
                    if (yy < min) min = yy;
                    if (yy > max) max = yy;
                }
                year = Math.Abs(max - min) >= 10 ? min : yearCandidates[yearCandidates.Count - 1];
            }

            return year;
        }

        private static int? ParseGroupInt(Match match, string groupName)
        {
            try
            {
                if (!match.Groups[groupName].Success) return null;
                if (int.TryParse(match.Groups[groupName].Value, out var value)) return value;
            }
            catch { }
            return null;
        }

        private static bool IsDriveLikeFolderName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string candidate = value.Trim();
            return Regex.IsMatch(candidate, @"^[a-zA-Z]:?(?:\\)?$", RegexOptions.CultureInvariant);
        }

        private static bool IsGenericEpisodeFileName(string value)
        {
            string normalized = NormalizeEpisodeSourceString(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(normalized,
                @"^(?:\d{1,3}|E\d{1,3}|EP\d{1,3}|Episode\s*\d{1,3}|Episodio\s*\d{1,3})(?:\s*[-–—]\s*.+)?$",
                RegexOptions.IgnoreCase);
        }

        private static bool IsGenericLibraryFolderName(string value)
        {
            string normalized = NormalizeTitleForComparisonString(value ?? string.Empty);
            string collapsed = CollapseTitleForComparisonString(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(collapsed))
                return true;

            if (Regex.IsMatch(normalized,
                @"^(downloads?|download|desktop|videos?|video|movies?|movie|films?|film|tv|series?|anime|cartoons?|media|library|libreria|collection|raccolta|new\s*folder(?:\s*\d+)?)$",
                RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (Regex.IsMatch(collapsed,
                @"^(?:film|films|movie|movies|video|videos|tv|series|anime|media|download|downloads)(?:disco|disk|drive)?[a-z0-9]*$",
                RegexOptions.IgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string SanitizeSeriesFolderCandidate(string value)
        {
            string candidate = NormalizeEpisodeSourceString(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(candidate))
                return string.Empty;

            candidate = Regex.Replace(candidate,
                @"(?ix)\b(?:season|stagione)\s*\d{1,2}\b.*$",
                string.Empty);
            candidate = Regex.Replace(candidate,
                @"(?ix)\bS\d{1,2}\s*E\d{1,3}(?:\s*[-_. ]?\s*\d{1,3})?.*$",
                string.Empty);
            candidate = Regex.Replace(candidate,
                @"(?ix)\b\d{4}\b.*$",
                string.Empty);
            candidate = Regex.Replace(candidate, @"\s+", " ").Trim(' ', '-', '.', '_');
            return candidate;
        }

        private static string? TryExtractSeriesTitleFromFolders(string path)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty);
                int depth = 0;
                while (dir != null && depth < 4)
                {
                    string rawCandidate = NormalizeEpisodeSourceString(dir.Name);
                    string candidate = SanitizeSeriesFolderCandidate(rawCandidate);
                    depth++;

                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        !IsDriveLikeFolderName(candidate) &&
                        !IsGenericLibraryFolderName(candidate) &&
                        !Regex.IsMatch(candidate, @"(?i)^\s*(season|stagione|serie|series)\b") &&
                        !Regex.IsMatch(candidate, @"(?i)^\s*s\d{1,2}\b"))
                    {
                        var (title, _) = ExtractMovieTitleAndYearFromPath(candidate);
                        title = NormalizeTitleCasing(title);
                        if (!string.IsNullOrWhiteSpace(title) &&
                            !IsGenericLibraryFolderName(title) &&
                            !ContainsReleaseNoise(title))
                        {
                            return title;
                        }
                    }

                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static bool ContainsReleaseNoise(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return Regex.IsMatch(value,
                @"(?ix)\b(720p|1080p|2160p|480p|4k|uhd|hdr|dvd|remux|bluray|b[dr]rip|brrip|webrip|web[- ]?dl|hdtv|dvdrip|hdrip|microhd|x264|x265|h264|h265|hevc|xvid|ac3|dts|dtsx|truehd|atmos|multi|ita|eng|dual|sub|subs|subita|subeng|uncut|extended|proper|repack|internal|hsbs|sbs|fullsbs|3d|sample|trailer|teaser|promo|clip)\b");
        }

        private static string NormalizeTitleCasing(string value)
        {
            string title = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            if (title.Length == 0) return title;

            if (title.IndexOf(' ') < 0 && title.Count(ch => ch == '-') >= 2)
                title = title.Replace('-', ' ');

            try
            {
                var ti = CultureInfo.CurrentCulture.TextInfo;
                title = ti.ToTitleCase(title.ToLower());
            }
            catch
            {
                // tieni il titolo così com'è
            }

            title = Regex.Replace(title, @"(?<=\p{L})\s*'\s*(?=\p{L})", "'");
            title = Regex.Replace(title, @"\s+", " ").Trim();
            title = Regex.Replace(
                title,
                @"\b(?:i|ii|iii|iv|v|vi|vii|viii|ix|x|xi|xii)\b",
                m => m.Value.ToUpperInvariant(),
                RegexOptions.IgnoreCase);

            return title;
        }

        private static string StripPromotionalSuffixes(string value)
        {
            string s = NormalizeTitleCasing(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(s))
                return s;

            s = Regex.Replace(
                s,
                @"(?ix)\b(?:first\s+trailer|trailer(?:\s+ufficiale)?|teaser|sample|promo|clip)\b.*$",
                string.Empty);

            s = Regex.Replace(
                s,
                @"(?:\s|[-:])+(?:3d|sbs|hsbs|fullsbs|full\s*sbs)$",
                string.Empty,
                RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', ':');
            return NormalizeTitleCasing(s);
        }

        private static string StripSearchDecorators(string value)
        {
            string s = StripPromotionalSuffixes(value);
            if (string.IsNullOrWhiteSpace(s))
                return s;

            s = Regex.Replace(
                s,
                @"(?ix)\b(?:director'?s\s*cut|directors\s*cut|director\s*s\s*cut|final\s*cut|extended\s*cut|theatrical\s*cut|special\s*edition|collector'?s\s*edition|anniversary\s*edition|ultimate\s*edition|versione\s*estesa|versione\s*integrale|uncut|remastered)\b.*$",
                string.Empty);

            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', ':');
            return NormalizeTitleCasing(s);
        }

        private static bool ShouldPreferFolderMovieTitle(string rawName, string currentTitle)
        {
            if (string.IsNullOrWhiteSpace(rawName) && string.IsNullOrWhiteSpace(currentTitle))
                return false;

            string normalizedRaw = NormalizeEpisodeSourceString(rawName ?? string.Empty);
            if (Regex.IsMatch(normalizedRaw, @"(?i)\b(sample|trailer|teaser|promo|clip)\b"))
                return true;

            if (IsGenericEpisodeFileName(normalizedRaw))
                return true;

            if (!string.IsNullOrWhiteSpace(currentTitle) &&
                Regex.IsMatch(currentTitle, @"(?i)\b(sample|trailer|teaser|promo|clip)\b"))
                return true;

            return false;
        }

        private static string? TryExtractMovieTitleFromFolders(string path)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty);
                while (dir != null)
                {
                    string candidate = NormalizeEpisodeSourceString(dir.Name);
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        !IsDriveLikeFolderName(candidate) &&
                        !Regex.IsMatch(candidate, @"(?i)^\s*(season|stagione|serie|series)\b") &&
                        !Regex.IsMatch(candidate, @"(?i)^\s*s\d{1,2}\b") &&
                        !ContainsReleaseNoise(candidate))
                    {
                        var (title, _) = ExtractMovieTitleAndYearFromPath(candidate);
                        title = NormalizeTitleCasing(title);
                        if (!string.IsNullOrWhiteSpace(title) &&
                            !Regex.IsMatch(title, @"(?i)\b(sample|trailer|teaser|promo|clip)\b"))
                        {
                            return title;
                        }
                    }

                    dir = dir.Parent;
                }
            }
            catch { }

            return null;
        }

        private static List<string> ExtractTitleTokensFromCandidateSource(string source, int? year, ISet<string> noise)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(source))
                return result;

            var tokens = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string raw = tokens[i];
                string t = raw.Trim(' ', '-', '.', '_');
                if (t.Length == 0)
                    continue;

                string lower = t.ToLowerInvariant();

                if (lower.Length == 4 &&
                    year.HasValue &&
                    int.TryParse(lower, out var yyTok) &&
                    yyTok == year.Value)
                {
                    break;
                }

                if (lower.EndsWith("p", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(lower.AsSpan(0, lower.Length - 1), out _))
                {
                    break;
                }

                if (Regex.IsMatch(
                        lower,
                        @"(?:720p|1080p|2160p|480p|4k|uhd|uhdr|hdr|dvd|remux|bdremux|bdrmux|bluray|b[dr]rip|brrip|webrip|web[-]?dl|hdtv|dvdrip|hdrip|microhd|x264|x265|h264|h265|hevc|xvid|ac3|dts|dtsx|truehd|atmos|sbs|hsbs|fullsbs|3d|upscaled)",
                        RegexOptions.IgnoreCase))
                {
                    break;
                }

                if (noise.Contains(lower))
                    break;

                result.Add(t);
            }

            return result;
        }

        private static string BuildTvDisplayTitle(string seriesTitle, int? season, int? episode, string? episodeTitle)
        {
            string baseTitle = string.IsNullOrWhiteSpace(seriesTitle) ? "Serie Tv" : seriesTitle.Trim();
            if (season.HasValue && episode.HasValue)
            {
                string code = $"S{season.Value:00}E{episode.Value:00}";
                if (!string.IsNullOrWhiteSpace(episodeTitle))
                    return baseTitle + " • " + code + " - " + episodeTitle.Trim();
                return baseTitle + " • " + code;
            }
            return baseTitle;
        }

        // --------------------------------------------------------------------
        // Normalizzazione nome file → (titolo, anno)
        // --------------------------------------------------------------------

        /// <summary>
        /// Normalizza "soft" il nome del film a partire dal path:
        /// - sostituisce . _ + con spazi
        /// - rimuove blocchi tra [] () {}
        /// - prova a estrarre un anno (1999, 2014, 2022...)
        /// - tronca non appena incontra roba da release (1080p, HDR, x265, Ita, Eng, 3D, HSBS ecc.)
        /// </summary>
        public static (string normalizedTitle, int? year) ExtractMovieTitleAndYearFromPath(string path)
        {
            // Una correzione fatta a mano vale piu' del nome del file.
            if (TryGetManualTitle(path, out string manualTitle, out int? manualYear)) return (manualTitle, manualYear);
            string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return (name, null);

            int? year = ExtractLikelyYear(name);

            string s = name;
            s = s.Replace('–', '-').Replace('—', '-');
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            s = Regex.Replace(s, @"\([^\)]*\)", " ");
            s = Regex.Replace(s, @"\{[^\}]*\}", " ");
            s = s.Replace('.', ' ')
                 .Replace('_', ' ')
                 .Replace('+', ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '.', '_');

            if (s.Length == 0)
                return (NormalizeTitleCasing(name), year);

            var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "1080p", "720p", "2160p", "480p",
                "4k", "uhd", "uhdr", "hdr",
                "dvd", "remux", "bdremux", "bdrmux",
                "bluray", "bdrip", "brrip", "webrip", "webdl", "web-dl",
                "hdtv", "dvdrip", "hdrip", "microhd",
                "x264", "x265", "h264", "h265", "hevc", "xvid",
                "ac3", "dts", "dtsx", "truehd", "atmos",
                "multi", "ita", "eng", "dual",
                "sub", "subs", "subita", "subeng", "sub-eng", "sub-ita",
                "uncut", "extended", "proper", "repack", "internal",
                "hsbs", "sbs", "fullsbs", "full-sbs", "3d", "upscaled",
                "sample", "trailer", "teaser", "promo", "clip", "official", "ufficiale"
            };

            var titleTokens = ExtractTitleTokensFromCandidateSource(s, year, noise);
            if (titleTokens.Count == 0)
            {
                string secondary = Regex.Replace(s, @"(?<=\p{L}|\p{N})-(?=\p{L}|\p{N})", " ");
                if (!string.Equals(secondary, s, StringComparison.Ordinal))
                    titleTokens = ExtractTitleTokensFromCandidateSource(secondary, year, noise);
            }

            string title = titleTokens.Count > 0
                ? string.Join(" ", titleTokens)
                : s;

            title = StripPromotionalSuffixes(title);
            if (string.IsNullOrWhiteSpace(title))
                title = NormalizeTitleCasing(s);
            else
                title = NormalizeTitleCasing(title);

            bool canUseFolderFallback =
                path.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                path.IndexOf(Path.AltDirectorySeparatorChar) >= 0;

            if (canUseFolderFallback && ShouldPreferFolderMovieTitle(name, title))
            {
                var folderTitle = TryExtractMovieTitleFromFolders(path);
                if (!string.IsNullOrWhiteSpace(folderTitle))
                    title = folderTitle;
            }

            return (title, year);
        }

        // --------------------------------------------------------------------
        //                    IMPLEMENTAZIONE TMDb + CACHE
        // --------------------------------------------------------------------



        // --------------------------------------------------------------------
        //                       POSTER INDEX (JSON)
        // --------------------------------------------------------------------

    }
}
