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
    internal static class MovieMetadataService
    {
        // API key TMDb integrata come fallback. L'utente puo' sovrascriverla dalla UI.
        private const string DefaultTmdbApiKey = "daf98548f41dd2a9aa6eca965798a463";
        public static event Action? PostersChanged;
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static readonly PosterIndexStore _posterIndex = new();
        private static readonly TmdbApiKeyStore _tmdbApiKeyStore = new();
        private static readonly SemaphoreSlim _tmdbRequestGate = new SemaphoreSlim(2, 2);
        private static readonly object _localizedTitleRefreshSync = new object();
        private static readonly HashSet<string> _localizedTitleRefreshCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _localizedTitleRefreshInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        public static void SetUserTmdbApiKey(string? apiKey)
        {
            _tmdbApiKeyStore.Set(apiKey);
            PostersChanged?.Invoke();
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

        private static byte[] GetTmdbBytes(string url, CancellationToken ct)
        {
            using var resp = GetTmdbResponse(url, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"TMDb image request failed: {(int)resp.StatusCode}");
            return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
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

        // --------------------------------------------------------------------
        // API pubblica
        // --------------------------------------------------------------------

        /// <summary>
        /// Versione "vecchia": non passa la durata.
        /// </summary>
        private static bool ShouldInvalidateResolvedMovieCache(MediaTitleInfo parsed, string? cachedTitle, int? cachedYear, bool cachedTitleResolved)
        {
            if (parsed == null || parsed.IsTvEpisode)
                return false;

            if (parsed.Year.HasValue && cachedYear.HasValue && Math.Abs(parsed.Year.Value - cachedYear.Value) >= 2)
                return true;

            if (!cachedTitleResolved)
                return false;

            if (string.IsNullOrWhiteSpace(parsed.NormalizedTitle) || string.IsNullOrWhiteSpace(cachedTitle))
                return false;

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

        private static string? TryReuseEquivalentPosterFromCache(string filePath, string? title, int? year)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(title))
                    return null;

                var reused = _posterIndex.FindEquivalentPosterPath(filePath, title, year);
                if (!string.IsNullOrWhiteSpace(reused) && File.Exists(reused))
                    return reused;
            }
            catch { }

            return null;
        }

        private static string? TryReuseEquivalentBackdropFromCache(string filePath, string? title, int? year)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(title))
                    return null;

                var reused = _posterIndex.FindEquivalentBackdropPath(filePath, title, year);
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

            if (!string.IsNullOrWhiteSpace(cachedPoster) && File.Exists(cachedPoster) && !forceArtworkRefresh)
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
                var reusedPoster = TryReuseEquivalentPosterFromCache(filePath, equivalentLookupTitle, year);
                if (!string.IsNullOrWhiteSpace(reusedPoster) && File.Exists(reusedPoster))
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

            if (!string.IsNullOrWhiteSpace(cachedBackdrop) && File.Exists(cachedBackdrop) && !forceArtworkRefresh)
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
                var reusedBackdrop = TryReuseEquivalentBackdropFromCache(filePath, equivalentBackdropLookupTitle, year);
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
                    @"(?ix)(?:film|films|movie|movies|video|videos|media|download|downloads)(?:\s*(?:disco|disk|drive))?(?:\s*[a-z0-9]+)?"))
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

            try
            {
                var poster = _posterIndex.TryGet(filePath);
                if (!string.IsNullOrWhiteSpace(poster?.title) &&
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
                    !ShouldIgnoreCachedMovieEntry(filePath, backdrop?.title, backdrop?.year, backdrop?.titleResolved ?? false))
                {
                    return backdrop?.title;
                }
            }
            catch { }

            return null;
        }

        public static string? GetCachedPosterPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            try
            {
                var cached = _posterIndex.TryGet(filePath);
                if (!string.IsNullOrWhiteSpace(cached?.localPosterPath) &&
                    File.Exists(cached.Value.localPosterPath) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, cached?.title, cached?.year, cached?.titleResolved ?? false))
                {
                    return cached.Value.localPosterPath;
                }

                var parsed = ExtractMediaTitleInfoFromPath(filePath);
                string? lookupTitle = !string.IsNullOrWhiteSpace(cached?.title)
                    ? cached?.title
                    : (parsed.IsTvEpisode && !string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.SeriesTitle : parsed.NormalizedTitle);
                int? lookupYear = cached?.year ?? parsed.Year;

                var reusedPoster = TryReuseEquivalentPosterFromCache(filePath, lookupTitle, lookupYear);
                if (!string.IsNullOrWhiteSpace(reusedPoster) && File.Exists(reusedPoster))
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
                if (!string.IsNullOrWhiteSpace(cached?.localBackdropPath) &&
                    File.Exists(cached.Value.localBackdropPath) &&
                    !ShouldIgnoreCachedMovieEntry(filePath, cached?.title, cached?.year, cached?.titleResolved ?? false))
                {
                    return cached.Value.localBackdropPath;
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

                var reusedBackdrop = TryReuseEquivalentBackdropFromCache(filePath, lookupTitle, lookupYear);
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
                bool titleResolved = _posterIndex.TryGet(filePath)?.titleResolved ?? _posterIndex.TryGetBackdrop(filePath)?.titleResolved ?? false;
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

                string? tmpTitle = null;
                int? tmpYear = null;

                if (TryOneTmdbTvTitleCall(searchTitle, info.Year, durationSeconds, "it-IT", info, ct, ref tmpTitle, ref tmpYear))
                {
                    tmdbYear = tmpYear;
                    return tmpTitle;
                }
                MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                tmpTitle = null;
                tmpYear = null;
                if (TryOneTmdbTvTitleCall(searchTitle, null, durationSeconds, "it-IT", info, ct, ref tmpTitle, ref tmpYear))
                {
                    tmdbYear = tmpYear;
                    return tmpTitle;
                }
                MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                tmpTitle = null;
                tmpYear = null;
                if (TryOneTmdbTvTitleCall(searchTitle, info.Year, durationSeconds, "en-US", info, ct, ref tmpTitle, ref tmpYear))
                {
                    tmdbYear = tmpYear;
                    return tmpTitle;
                }
                MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                tmpTitle = null;
                tmpYear = null;
                if (TryOneTmdbTvTitleCall(searchTitle, null, durationSeconds, "en-US", info, ct, ref tmpTitle, ref tmpYear))
                {
                    tmdbYear = tmpYear;
                    return tmpTitle;
                }
                MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

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

                int? candidateRuntimeMinutes = null;
                if (expectedDurationSeconds.HasValue && seriesId > 0)
                    candidateRuntimeMinutes = GetTvRuntimeMinutes(seriesId, language, ct);

                if (!IsAcceptableTvMatch(searchTitle, searchYear, expectedDurationSeconds, candidateTitle, candidateOriginalTitle, candidateYear, candidateRuntimeMinutes))
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

                bool titleResolved = _posterIndex.TryGet(filePath)?.titleResolved ?? _posterIndex.TryGetBackdrop(filePath)?.titleResolved ?? false;
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
                bool titleResolved = _posterIndex.TryGet(filePath)?.titleResolved ?? _posterIndex.TryGetBackdrop(filePath)?.titleResolved ?? false;
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

            try
            {
                if (string.IsNullOrWhiteSpace(searchTitle))
                    return null;

                if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                    TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                    return null;

                foreach (var titleVariant in BuildMovieSearchTitleVariants(searchTitle))
                {
                    string? tmpTitle = null;
                    int? tmpYear = null;

                    if (TryOneTmdbTitleOnlyCall(titleVariant, searchYear, expectedDurationSeconds, "it-IT", ct, ref tmpTitle, ref tmpYear))
                    {
                        tmdbYear = tmpYear;
                        return tmpTitle;
                    }
                    MergeResolvedTitle(ref resolvedTitle, ref resolvedYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTitleOnlyCall(titleVariant, null, expectedDurationSeconds, "it-IT", ct, ref tmpTitle, ref tmpYear))
                    {
                        tmdbYear = tmpYear;
                        return tmpTitle;
                    }
                    MergeResolvedTitle(ref resolvedTitle, ref resolvedYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTitleOnlyCall(titleVariant, searchYear, expectedDurationSeconds, "en-US", ct, ref tmpTitle, ref tmpYear))
                    {
                        tmdbYear = tmpYear;
                        return tmpTitle;
                    }
                    MergeResolvedTitle(ref resolvedTitle, ref resolvedYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTitleOnlyCall(titleVariant, null, expectedDurationSeconds, "en-US", ct, ref tmpTitle, ref tmpYear))
                    {
                        tmdbYear = tmpYear;
                        return tmpTitle;
                    }
                    MergeResolvedTitle(ref resolvedTitle, ref resolvedYear, tmpTitle, tmpYear);
                }
            }
            catch
            {
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
                @"(?ix)\b(720p|1080p|2160p|480p|4k|uhd|hdr|remux|bluray|b[dr]rip|brrip|webrip|web[- ]?dl|hdtv|dvdrip|hdrip|microhd|x264|x265|h264|h265|hevc|xvid|ac3|dts|dtsx|truehd|atmos|multi|ita|eng|dual|sub|subs|subita|subeng|uncut|extended|proper|repack|internal|hsbs|sbs|fullsbs|3d|sample|trailer|teaser|promo|clip)\b");
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
                        @"(?:720p|1080p|2160p|480p|4k|uhd|uhdr|hdr|remux|bdremux|bdrmux|bluray|b[dr]rip|brrip|webrip|web[-]?dl|hdtv|dvdrip|hdrip|microhd|x264|x265|h264|h265|hevc|xvid|ac3|dts|dtsx|truehd|atmos|sbs|hsbs|fullsbs|3d|upscaled)",
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
                "remux", "bdremux", "bdrmux",
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



        private static List<string> BuildTvSearchTitleVariants(string? searchTitle)
        {
            var variants = new List<string>();

            void Add(string? candidate)
            {
                candidate = NormalizeTitleCasing(candidate ?? string.Empty);
                if (string.IsNullOrWhiteSpace(candidate))
                    return;

                if (!variants.Any(v => string.Equals(v, candidate, StringComparison.OrdinalIgnoreCase)))
                    variants.Add(candidate);
            }

            string normalized = NormalizeTitleCasing(searchTitle ?? string.Empty);
            Add(searchTitle);
            Add(StripSearchDecorators(normalized));
            Add(Regex.Replace(normalized, @"(?ix)\bS\d{1,2}\s*E\d{1,3}(?:\s*[-_. ]?\s*\d{1,3})?.*$", string.Empty).Trim(' ', '-', '.', '_'));
            Add(Regex.Replace(normalized, @"\b(19\d{2}|20\d{2})\b", string.Empty).Trim(' ', '-', '.', '_'));

            string compare = NormalizeTitleForComparisonString(normalized);
            if (compare.Contains("cyberpunk edgerunners", StringComparison.OrdinalIgnoreCase))
                Add("Cyberpunk: Edgerunners");

            return variants;
        }

        private static string? TryDownloadTvPoster(
            MediaTitleInfo info,
            double? durationSeconds,
            CancellationToken ct,
            out string? tmdbTitle,
            out int? tmdbYear)
        {
            tmdbTitle = null;
            tmdbYear = null;

            try
            {
                string searchTitle = !string.IsNullOrWhiteSpace(info.SeriesTitle) ? info.SeriesTitle! : info.NormalizedTitle;
                if (string.IsNullOrWhiteSpace(searchTitle))
                    return null;

                if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                    TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                    return null;

                foreach (var titleVariant in BuildTvSearchTitleVariants(searchTitle))
                {
                    string? localPosterPath;
                    string? tmpTitle = null;
                    int? tmpYear = null;

                    if (TryOneTmdbTvPosterCall(titleVariant, info.Year, durationSeconds, "it-IT", info, ct, ref tmpTitle, ref tmpYear, out localPosterPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localPosterPath;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTvPosterCall(titleVariant, null, durationSeconds, "it-IT", info, ct, ref tmpTitle, ref tmpYear, out localPosterPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localPosterPath;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTvPosterCall(titleVariant, info.Year, durationSeconds, "en-US", info, ct, ref tmpTitle, ref tmpYear, out localPosterPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localPosterPath;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTvPosterCall(titleVariant, null, durationSeconds, "en-US", info, ct, ref tmpTitle, ref tmpYear, out localPosterPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localPosterPath;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? TryDownloadTvBackdrop(
            MediaTitleInfo info,
            double? durationSeconds,
            CancellationToken ct,
            out string? tmdbTitle,
            out int? tmdbYear)
        {
            tmdbTitle = null;
            tmdbYear = null;

            try
            {
                string searchTitle = !string.IsNullOrWhiteSpace(info.SeriesTitle) ? info.SeriesTitle! : info.NormalizedTitle;
                if (string.IsNullOrWhiteSpace(searchTitle))
                    return null;

                if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                    TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                    return null;

                foreach (var titleVariant in BuildTvSearchTitleVariants(searchTitle))
                {
                    string? localBackdropPath;
                    string? tmpTitle = null;
                    int? tmpYear = null;

                    if (TryOneTmdbTvBackdropCall(titleVariant, info.Year, durationSeconds, "it-IT", info, ct, ref tmpTitle, ref tmpYear, out localBackdropPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localBackdropPath;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTvBackdropCall(titleVariant, null, durationSeconds, "it-IT", info, ct, ref tmpTitle, ref tmpYear, out localBackdropPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localBackdropPath;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTvBackdropCall(titleVariant, info.Year, durationSeconds, "en-US", info, ct, ref tmpTitle, ref tmpYear, out localBackdropPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localBackdropPath;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);

                    tmpTitle = null;
                    tmpYear = null;
                    if (TryOneTmdbTvBackdropCall(titleVariant, null, durationSeconds, "en-US", info, ct, ref tmpTitle, ref tmpYear, out localBackdropPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localBackdropPath;
                    }
                    MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryOneTmdbTvPosterCall(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            MediaTitleInfo info,
            CancellationToken ct,
            ref string? tmdbTitle,
            ref int? tmdbYear,
            out string? localPosterPath)
        {
            localPosterPath = null;

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

                int? candidateRuntimeMinutes = null;
                if (expectedDurationSeconds.HasValue && seriesId > 0)
                    candidateRuntimeMinutes = GetTvRuntimeMinutes(seriesId, language, ct);

                if (!IsAcceptableTvMatch(searchTitle, searchYear, expectedDurationSeconds, candidateTitle, candidateOriginalTitle, candidateYear, candidateRuntimeMinutes))
                    continue;

                if (searchYear.HasValue && candidateYear.HasValue && Math.Abs(candidateYear.Value - searchYear.Value) > 8)
                    continue;

                tmdbTitle = ResolveTvDisplayTitle(seriesId, language, info, candidateTitle ?? candidateOriginalTitle ?? searchTitle, ct);
                tmdbYear = candidateYear;

                string? posterPathTmdb = null;
                if (result.TryGetProperty("poster_path", out var posterProp))
                    posterPathTmdb = posterProp.GetString();

                if (TryDownloadPosterFromTmdbPath("tv", candidateTitle ?? searchTitle, candidateYear, posterPathTmdb, ct, out localPosterPath))
                    return true;

                if (seriesId > 0 &&
                    TryDownloadBestPosterForTv(seriesId, candidateTitle ?? candidateOriginalTitle ?? searchTitle, candidateYear, ct, out localPosterPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryOneTmdbTvBackdropCall(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            MediaTitleInfo info,
            CancellationToken ct,
            ref string? tmdbTitle,
            ref int? tmdbYear,
            out string? localBackdropPath)
        {
            localBackdropPath = null;

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

                int? candidateRuntimeMinutes = null;
                if (expectedDurationSeconds.HasValue && seriesId > 0)
                    candidateRuntimeMinutes = GetTvRuntimeMinutes(seriesId, language, ct);

                if (!IsAcceptableTvMatch(searchTitle, searchYear, expectedDurationSeconds, candidateTitle, candidateOriginalTitle, candidateYear, candidateRuntimeMinutes))
                    continue;

                if (searchYear.HasValue && candidateYear.HasValue && Math.Abs(candidateYear.Value - searchYear.Value) > 8)
                    continue;

                tmdbTitle = ResolveTvDisplayTitle(seriesId, language, info, candidateTitle ?? candidateOriginalTitle ?? searchTitle, ct);
                tmdbYear = candidateYear;

                if (seriesId <= 0)
                    continue;

                if (!TryDownloadBest4kBackdropForTv(seriesId, candidateTitle ?? searchTitle, candidateYear, ct, out localBackdropPath))
                    continue;

                return !string.IsNullOrWhiteSpace(localBackdropPath);
            }

            return false;
        }

        private static bool TryDownloadPosterFromTmdbPath(
            string posterKind,
            string candidateTitle,
            int? candidateYear,
            string? posterPathTmdb,
            CancellationToken ct,
            out string? localPosterPath)
        {
            localPosterPath = null;

            if (string.IsNullOrWhiteSpace(posterPathTmdb))
                return false;

            try
            {
                string imageUrl = "https://image.tmdb.org/t/p/w500" + posterPathTmdb;
                var bytes = GetTmdbBytes(imageUrl, ct);

                string hashSrc = (candidateTitle ?? string.Empty) + "|" + (posterKind ?? string.Empty) + "|" +
                                 (candidateYear?.ToString() ?? string.Empty) + "|" + posterPathTmdb;
                string fileName = ComputeSha1(hashSrc) + ".jpg";
                string folder = GetPosterFolder();
                string fullPath = Path.Combine(folder, fileName);
                File.WriteAllBytes(fullPath, bytes);

                localPosterPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double GetPosterLanguageBonus(string? iso639)
        {
            if (string.IsNullOrWhiteSpace(iso639))
                return 900d;

            if (string.Equals(iso639, "it", StringComparison.OrdinalIgnoreCase))
                return 1300d;

            if (string.Equals(iso639, "en", StringComparison.OrdinalIgnoreCase))
                return 1100d;

            return 220d;
        }

        private static bool TryDownloadBestPosterForTv(
            int seriesId,
            string candidateTitle,
            int? candidateYear,
            CancellationToken ct,
            out string? localPosterPath)
        {
            localPosterPath = null;

            string url = $"https://api.themoviedb.org/3/tv/{seriesId}/images?api_key={TmdbApiKey}&include_image_language=it,en,null";
            using var resp = GetTmdbResponse(url, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("posters", out var posters) ||
                posters.ValueKind != JsonValueKind.Array ||
                posters.GetArrayLength() == 0)
            {
                return false;
            }

            string? bestPath = null;
            double bestScore = double.MinValue;

            foreach (var poster in posters.EnumerateArray())
            {
                if (!poster.TryGetProperty("file_path", out var pathProp))
                    continue;

                var filePath = pathProp.GetString();
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                int width = 0;
                if (poster.TryGetProperty("width", out var widthProp) && widthProp.ValueKind == JsonValueKind.Number)
                    widthProp.TryGetInt32(out width);

                int height = 0;
                if (poster.TryGetProperty("height", out var heightProp) && heightProp.ValueKind == JsonValueKind.Number)
                    heightProp.TryGetInt32(out height);

                if (width < 220 || height < 320)
                    continue;

                double aspect = height > 0 ? (double)width / height : 0d;
                if (aspect > 0d && (aspect < 0.45d || aspect > 0.82d))
                    continue;

                string? iso639 = null;
                if (poster.TryGetProperty("iso_639_1", out var langProp) && langProp.ValueKind != JsonValueKind.Null)
                    iso639 = langProp.GetString();

                double voteAverage = 0d;
                if (poster.TryGetProperty("vote_average", out var voteAvgProp) && voteAvgProp.ValueKind == JsonValueKind.Number)
                    voteAvgProp.TryGetDouble(out voteAverage);

                int voteCount = 0;
                if (poster.TryGetProperty("vote_count", out var voteCountProp) && voteCountProp.ValueKind == JsonValueKind.Number)
                    voteCountProp.TryGetInt32(out voteCount);

                double score = GetPosterLanguageBonus(iso639) + (voteAverage * 100d) + voteCount + (height / 12d) + (width / 18d);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestPath = filePath;
            }

            if (string.IsNullOrWhiteSpace(bestPath))
                return false;

            return TryDownloadPosterFromTmdbPath("tv", candidateTitle, candidateYear, bestPath, ct, out localPosterPath);
        }

        private static bool TryDownloadBest4kBackdropForTv(
            int seriesId,
            string candidateTitle,
            int? candidateYear,
            CancellationToken ct,
            out string? localBackdropPath)
        {
            localBackdropPath = null;

            string url = $"https://api.themoviedb.org/3/tv/{seriesId}/images?api_key={TmdbApiKey}";
            using var resp = GetTmdbResponse(url, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("backdrops", out var backdrops) ||
                backdrops.ValueKind != JsonValueKind.Array ||
                backdrops.GetArrayLength() == 0)
                return false;

            string? bestPath = null;
            double bestScore = double.MinValue;

            foreach (var backdrop in backdrops.EnumerateArray())
            {
                if (!backdrop.TryGetProperty("file_path", out var pathProp))
                    continue;

                var filePath = pathProp.GetString();
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                int width = 0;
                if (backdrop.TryGetProperty("width", out var widthProp) && widthProp.ValueKind == JsonValueKind.Number)
                    widthProp.TryGetInt32(out width);

                int height = 0;
                if (backdrop.TryGetProperty("height", out var heightProp) && heightProp.ValueKind == JsonValueKind.Number)
                    heightProp.TryGetInt32(out height);

                if (width < MinBackdropWidthForPlaceholder || height < MinBackdropHeightForPlaceholder)
                    continue;

                double aspect = height > 0 ? (double)width / height : 0d;
                if (aspect > 0d && (aspect < 1.55d || aspect > 2.15d))
                    continue;

                string? iso639 = null;
                if (backdrop.TryGetProperty("iso_639_1", out var langProp) && langProp.ValueKind != JsonValueKind.Null)
                    iso639 = langProp.GetString();

                double voteAverage = 0d;
                if (backdrop.TryGetProperty("vote_average", out var voteAvgProp) && voteAvgProp.ValueKind == JsonValueKind.Number)
                    voteAvgProp.TryGetDouble(out voteAverage);

                int voteCount = 0;
                if (backdrop.TryGetProperty("vote_count", out var voteCountProp) && voteCountProp.ValueKind == JsonValueKind.Number)
                    voteCountProp.TryGetInt32(out voteCount);

                bool prefersNoLanguage = string.IsNullOrWhiteSpace(iso639);
                double languageBonus = prefersNoLanguage ? 1000d : 0d;
                double score = languageBonus + (voteAverage * 100d) + voteCount + (width / 10d);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestPath = filePath;
            }

            if (string.IsNullOrWhiteSpace(bestPath))
                return false;

            try
            {
                string imageUrl = "https://image.tmdb.org/t/p/original" + bestPath;
                var bytes = GetTmdbBytes(imageUrl, ct);

                string hashSrc = (candidateTitle ?? string.Empty) + "|tv|" + (candidateYear?.ToString() ?? string.Empty) + "|backdrop-4k|" + bestPath;
                string fileName = ComputeSha1(hashSrc) + ".jpg";
                string folder = GetBackdropFolder();
                string fullPath = Path.Combine(folder, fileName);
                File.WriteAllBytes(fullPath, bytes);

                if (!IsBackdropFullResolution(fullPath))
                {
                    try { File.Delete(fullPath); } catch { }
                    return false;
                }

                localBackdropPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int? GetTvRuntimeMinutes(int seriesId, string language, CancellationToken ct)
        {
            try
            {
                string url = $"https://api.themoviedb.org/3/tv/{seriesId}?api_key={TmdbApiKey}&language={language}";
                using var resp = GetTmdbResponse(url, ct);
                if (!resp.IsSuccessStatusCode)
                    return null;

                string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("episode_run_time", out var rtProp) && rtProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rtProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var minutes) && minutes > 0)
                            return minutes;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveTvDisplayTitle(int seriesId, string language, MediaTitleInfo info, string fallbackSeriesTitle, CancellationToken ct)
        {
            string seriesTitle = !string.IsNullOrWhiteSpace(fallbackSeriesTitle)
                ? NormalizeTitleCasing(fallbackSeriesTitle)
                : NormalizeTitleCasing(info.SeriesTitle ?? info.NormalizedTitle);

            string? episodeTitle = info.EpisodeTitle;
            if (info.SeasonNumber.HasValue && info.EpisodeNumber.HasValue)
            {
                var epTitle = GetTvEpisodeTitle(seriesId, info.SeasonNumber.Value, info.EpisodeNumber.Value, language, ct)
                           ?? GetTvEpisodeTitle(seriesId, info.SeasonNumber.Value, info.EpisodeNumber.Value, "en-US", ct);
                if (!string.IsNullOrWhiteSpace(epTitle))
                    episodeTitle = NormalizeTitleCasing(epTitle);
            }

            return BuildTvDisplayTitle(seriesTitle, info.SeasonNumber, info.EpisodeNumber, episodeTitle);
        }

        private static string? GetTvEpisodeTitle(int seriesId, int seasonNumber, int episodeNumber, string language, CancellationToken ct)
        {
            try
            {
                string url = $"https://api.themoviedb.org/3/tv/{seriesId}/season/{seasonNumber}/episode/{episodeNumber}?api_key={TmdbApiKey}&language={language}";
                using var resp = GetTmdbResponse(url, ct);
                if (!resp.IsSuccessStatusCode)
                    return null;

                string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                {
                    var value = nameProp.GetString();
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsAcceptableTvMatch(
            string searchTitle,
            int? originalYear,
            double? expectedDurationSeconds,
            string? candidateTitle,
            string? candidateOriginalTitle,
            int? candidateYear,
            int? candidateRuntimeMinutes)
        {
            if (string.IsNullOrWhiteSpace(candidateTitle) && string.IsNullOrWhiteSpace(candidateOriginalTitle))
                return false;

            if (!HasAcceptableTitleMatch(searchTitle, candidateTitle, candidateOriginalTitle))
                return false;

            if (expectedDurationSeconds.HasValue && candidateRuntimeMinutes.HasValue)
            {
                double expectedMinutes = expectedDurationSeconds.Value / 60.0;
                double diffMinutes = Math.Abs(candidateRuntimeMinutes.Value - expectedMinutes);
                if (diffMinutes > 25.0)
                    return false;
            }

            if (originalYear.HasValue && candidateYear.HasValue)
            {
                if (Math.Abs(candidateYear.Value - originalYear.Value) > 8)
                    return false;
            }

            return true;
        }

        private static string NormalizeTitleForComparisonString(string value)
        {
            string normalized = NormalizeEpisodeSourceString(value ?? string.Empty).ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static string CollapseTitleForComparisonString(string value)
        {
            string normalized = NormalizeTitleForComparisonString(value);
            return Regex.Replace(normalized, @"[^\p{L}\p{N}]+", string.Empty);
        }

        private static bool TryGetKnownFranchiseBaseKey(string? value, out string baseKey)
        {
            baseKey = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = NormalizeTitleForComparisonString(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            string collapsed = CollapseTitleForComparisonString(value);

            bool looksLikeHowToTrainYourDragon =
                normalized.IndexOf("how to train your dragon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                collapsed.StartsWith("dragontrainer", StringComparison.OrdinalIgnoreCase);

            if (looksLikeHowToTrainYourDragon)
            {
                baseKey = "howtotrainyourdragon";
                return true;
            }

            return false;
        }

        private static bool TryExtractKnownFranchiseOrdinal(string? value, out int ordinal)
        {
            ordinal = 0;
            if (!TryGetKnownFranchiseBaseKey(value, out var baseKey) || string.IsNullOrWhiteSpace(baseKey))
                return false;

            string normalized = NormalizeTitleForComparisonString(value ?? string.Empty);
            string collapsed = CollapseTitleForComparisonString(value ?? string.Empty);

            if (string.Equals(baseKey, "howtotrainyourdragon", StringComparison.Ordinal))
            {
                if (normalized.IndexOf("hidden world", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    normalized.IndexOf("mondo nascosto", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ordinal = 3;
                    return true;
                }

                if (string.Equals(collapsed, "howtotrainyourdragon", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(collapsed, "dragontrainer", StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = 1;
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractExplicitSequelOrdinal(string? value, out int ordinal)
        {
            ordinal = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = NormalizeTitleForComparisonString(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            var match = Regex.Match(
                normalized,
                @"(?:^| )(?:part|parte|pt|chapter|capitolo|episodio|episode)?\s*(?<n>[1-9]|ix|iv|viii|vii|vi|v|iii|ii|i|x)$",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                string raw = match.Groups["n"].Value;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                    {
                        ordinal = parsedInt;
                        return ordinal >= 1 && ordinal <= 9;
                    }

                    ordinal = raw.Trim().ToUpperInvariant() switch
                    {
                        "I" => 1,
                        "II" => 2,
                        "III" => 3,
                        "IV" => 4,
                        "V" => 5,
                        "VI" => 6,
                        "VII" => 7,
                        "VIII" => 8,
                        "IX" => 9,
                        "X" => 10,
                        _ => 0
                    };

                    if (ordinal >= 1 && ordinal <= 9)
                        return true;
                }
            }

            return TryExtractKnownFranchiseOrdinal(value, out ordinal);
        }

        private static string StripExplicitSequelOrdinal(string? value)
        {
            if (TryGetKnownFranchiseBaseKey(value, out var knownBaseKey) && !string.IsNullOrWhiteSpace(knownBaseKey))
                return knownBaseKey;

            string normalized = NormalizeTitleForComparisonString(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            normalized = Regex.Replace(
                normalized,
                @"(?:^| )(?:part|parte|pt|chapter|capitolo|episodio|episode)\s+(?:[1-9]|ix|iv|viii|vii|vi|v|iii|ii|i|x)$",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();

            normalized = Regex.Replace(
                normalized,
                @"(?:^| )(?:[1-9]|ix|iv|viii|vii|vi|v|iii|ii|i|x)$",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();

            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static bool HasSequelOrdinalConflict(string searchTitle, string? cachedTitle)
        {
            if (!TryExtractExplicitSequelOrdinal(searchTitle, out var searchOrdinal))
                return false;

            if (!TryExtractExplicitSequelOrdinal(cachedTitle, out var cachedOrdinal))
                return false;

            return cachedOrdinal != searchOrdinal;
        }

        private static bool HasFranchiseBaseMatch(string searchTitle, params string?[] candidateTitles)
        {
            if (TryGetKnownFranchiseBaseKey(searchTitle, out var knownSearchKey) && !string.IsNullOrWhiteSpace(knownSearchKey))
            {
                foreach (var candidateTitle in candidateTitles)
                {
                    if (string.IsNullOrWhiteSpace(candidateTitle))
                        continue;

                    if (TryGetKnownFranchiseBaseKey(candidateTitle, out var knownCandidateKey) &&
                        string.Equals(knownSearchKey, knownCandidateKey, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            var searchTokens = TokenizeForComparison(StripExplicitSequelOrdinal(searchTitle));
            if (searchTokens.Count == 0)
                return false;

            foreach (var candidateTitle in candidateTitles)
            {
                if (string.IsNullOrWhiteSpace(candidateTitle))
                    continue;

                var candidateTokens = TokenizeForComparison(StripExplicitSequelOrdinal(candidateTitle));
                if (candidateTokens.Count == 0)
                    continue;

                int overlap = searchTokens.Count(t => candidateTokens.Contains(t));
                int minOverlap = searchTokens.Count <= 1 ? 1 : Math.Max(2, searchTokens.Count - 1);
                if (overlap >= minOverlap)
                    return true;
            }

            return false;
        }

        private sealed class MovieSearchCandidate
        {
            public JsonElement Result { get; set; }
            public string? Title { get; set; }
            public string? OriginalTitle { get; set; }
            public int? Year { get; set; }
            public int MovieId { get; set; }
            public int? RuntimeMinutes { get; set; }
            public int SelectionScore { get; set; }
        }

        private static int GetMovieCandidateSelectionScore(
            string searchTitle,
            int? originalYear,
            double? expectedDurationSeconds,
            string? candidateTitle,
            string? candidateOriginalTitle,
            int? candidateYear,
            int? candidateRuntimeMinutes)
        {
            int score = GetTitleMatchScore(searchTitle, candidateTitle, candidateOriginalTitle);

            if (originalYear.HasValue && candidateYear.HasValue)
                score += Math.Max(0, 120 - (Math.Abs(candidateYear.Value - originalYear.Value) * 24));

            if (expectedDurationSeconds.HasValue && candidateRuntimeMinutes.HasValue)
            {
                double diff = Math.Abs((expectedDurationSeconds.Value / 60.0) - candidateRuntimeMinutes.Value);
                score += Math.Max(0, 160 - (int)Math.Round(diff * 18.0));
            }

            if (TryExtractExplicitSequelOrdinal(searchTitle, out var searchOrdinal))
            {
                int candidateOrdinal = 0;
                bool hasCandidateOrdinal =
                    TryExtractExplicitSequelOrdinal(candidateTitle, out candidateOrdinal) ||
                    TryExtractExplicitSequelOrdinal(candidateOriginalTitle, out candidateOrdinal);

                if (hasCandidateOrdinal)
                    score += candidateOrdinal == searchOrdinal ? 420 : -420;
            }

            return score;
        }

        private static List<MovieSearchCandidate> CollectMovieSearchCandidates(
            JsonElement results,
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            CancellationToken ct)
        {
            var list = new List<MovieSearchCandidate>();
            int count = 0;

            foreach (var result in results.EnumerateArray())
            {
                if (count++ >= 8)
                    break;

                string? candidateTitle = null;
                if (result.TryGetProperty("title", out var titleProp))
                    candidateTitle = titleProp.GetString();

                string? candidateOriginalTitle = null;
                if (result.TryGetProperty("original_title", out var origProp))
                    candidateOriginalTitle = origProp.GetString();

                if (string.IsNullOrWhiteSpace(candidateTitle))
                    candidateTitle = candidateOriginalTitle;

                int? candidateYear = null;
                if (result.TryGetProperty("release_date", out var rdProp))
                {
                    var rd = rdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(rd) && rd!.Length >= 4 &&
                        int.TryParse(rd.Substring(0, 4), out var y))
                    {
                        candidateYear = y;
                    }
                }

                int movieId = 0;
                if (result.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    idProp.TryGetInt32(out movieId);

                int? candidateRuntimeMinutes = null;
                if (expectedDurationSeconds.HasValue && movieId > 0)
                    candidateRuntimeMinutes = GetMovieRuntimeMinutes(movieId, language, ct);

                if (!IsAcceptableMatch(
                        searchTitle,
                        searchYear,
                        expectedDurationSeconds,
                        candidateTitle,
                        candidateOriginalTitle,
                        candidateYear,
                        candidateRuntimeMinutes))
                {
                    continue;
                }

                list.Add(new MovieSearchCandidate
                {
                    Result = result.Clone(),
                    Title = candidateTitle,
                    OriginalTitle = candidateOriginalTitle,
                    Year = candidateYear,
                    MovieId = movieId,
                    RuntimeMinutes = candidateRuntimeMinutes,
                    SelectionScore = GetMovieCandidateSelectionScore(
                        searchTitle,
                        searchYear,
                        expectedDurationSeconds,
                        candidateTitle,
                        candidateOriginalTitle,
                        candidateYear,
                        candidateRuntimeMinutes)
                });
            }

            return list;
        }

        private static List<MovieSearchCandidate> OrderMovieSearchCandidates(string searchTitle, List<MovieSearchCandidate> candidates)
        {
            var ordered = candidates
                .OrderByDescending(c => c.SelectionScore)
                .ThenByDescending(c => c.Year ?? 0)
                .ToList();

            if (ordered.Count == 0)
                return ordered;

            if (TryExtractExplicitSequelOrdinal(searchTitle, out var searchOrdinal) && searchOrdinal >= 1)
            {
                var franchiseOrdered = candidates
                    .Where(c => HasFranchiseBaseMatch(searchTitle, c.Title, c.OriginalTitle))
                    .OrderBy(c => c.Year ?? int.MaxValue)
                    .ThenByDescending(c => c.SelectionScore)
                    .ToList();

                if (franchiseOrdered.Count >= searchOrdinal)
                {
                    var preferred = franchiseOrdered[searchOrdinal - 1];
                    ordered = ordered
                        .Where(c => c.MovieId != preferred.MovieId)
                        .ToList();
                    ordered.Insert(0, preferred);
                }
            }

            return ordered;
        }

        private static int GetTitleMatchScore(string searchTitle, params string?[] candidateTitles)
        {
            if (string.IsNullOrWhiteSpace(searchTitle) || candidateTitles == null || candidateTitles.Length == 0)
                return 0;

            string normalizedSearch = NormalizeTitleForComparisonString(searchTitle);
            string collapsedSearch = CollapseTitleForComparisonString(searchTitle);
            var searchTokens = TokenizeForComparison(searchTitle);

            int bestScore = 0;
            foreach (var candidateTitle in candidateTitles)
            {
                if (string.IsNullOrWhiteSpace(candidateTitle))
                    continue;

                string normalizedCandidate = NormalizeTitleForComparisonString(candidateTitle!);
                string collapsedCandidate = CollapseTitleForComparisonString(candidateTitle!);
                var candidateTokens = TokenizeForComparison(candidateTitle!);

                int overlap = 0;
                if (searchTokens.Count > 0 && candidateTokens.Count > 0)
                    overlap = searchTokens.Count(t => candidateTokens.Contains(t));

                double searchCoverage = searchTokens.Count > 0 ? (double)overlap / searchTokens.Count : 0d;
                double candidateCoverage = candidateTokens.Count > 0 ? (double)overlap / candidateTokens.Count : 0d;

                int score = (int)Math.Round((searchCoverage * 700d) + (candidateCoverage * 200d));

                if (!string.IsNullOrWhiteSpace(normalizedSearch) &&
                    string.Equals(normalizedSearch, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    score += 350;
                }
                else if (!string.IsNullOrWhiteSpace(normalizedSearch) &&
                         !string.IsNullOrWhiteSpace(normalizedCandidate) &&
                         (normalizedSearch.StartsWith(normalizedCandidate + " ", StringComparison.OrdinalIgnoreCase) ||
                          normalizedCandidate.StartsWith(normalizedSearch + " ", StringComparison.OrdinalIgnoreCase)))
                {
                    score += 100;
                }

                if (!string.IsNullOrWhiteSpace(collapsedSearch) &&
                    string.Equals(collapsedSearch, collapsedCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    score += 900;
                }

                if (score > bestScore)
                    bestScore = score;
            }

            return bestScore;
        }

        private static bool HasAcceptableTitleMatch(string searchTitle, params string?[] candidateTitles)
        {
            var searchTokens = TokenizeForComparison(searchTitle);
            int bestScore = GetTitleMatchScore(searchTitle, candidateTitles);

            if (searchTokens.Count <= 1)
                return bestScore >= 820;

            if (bestScore < 760)
                return false;

            if (searchTokens.Count >= 4)
            {
                foreach (var candidateTitle in candidateTitles)
                {
                    if (string.IsNullOrWhiteSpace(candidateTitle))
                        continue;

                    var candidateTokens = TokenizeForComparison(candidateTitle!);
                    if (candidateTokens.Count == 0)
                        continue;

                    int overlap = searchTokens.Count(t => candidateTokens.Contains(t));
                    int missing = searchTokens.Count - overlap;
                    if (missing <= 1)
                        return true;
                }

                return false;
            }

            return true;
        }

        private static HashSet<string> TokenizeForComparison(string value)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value))
                return set;

            var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "a", "an", "and", "of", "la", "il", "lo", "gli", "le", "i", "un", "una", "di", "da",
                "to", "your", "my", "me", "you", "how", "in", "on", "for", "with", "at", "by", "from", "into",
                "de", "del", "della", "dei", "degli", "delle", "e", "y"
            };

            foreach (Match m in Regex.Matches(NormalizeTitleForComparisonString(value), @"[\p{L}\p{N}]+"))
            {
                var token = m.Value.Trim();
                if (token.Length <= 1 && !token.All(char.IsDigit)) continue;
                if (stop.Contains(token)) continue;
                set.Add(token);
            }

            return set;
        }


        private static List<string> BuildMovieSearchTitleVariants(string? searchTitle)
        {
            var variants = new List<string>();

            void Add(string? candidate)
            {
                candidate = NormalizeTitleCasing(candidate ?? string.Empty);
                if (string.IsNullOrWhiteSpace(candidate))
                    return;

                if (!variants.Any(v => string.Equals(v, candidate, StringComparison.OrdinalIgnoreCase)))
                    variants.Add(candidate);
            }

            string normalized = NormalizeTitleCasing(searchTitle ?? string.Empty);
            Add(searchTitle);
            Add(StripPromotionalSuffixes(normalized));
            string stripped = StripSearchDecorators(normalized);
            Add(stripped);
            Add(stripped.Replace(" - ", ": "));
            Add(stripped.Replace(": ", " - "));
            Add(Regex.Replace(stripped, @"\s*[\-:]\s*", " ").Trim());

            string compare = NormalizeTitleForComparisonString(stripped);

            if (compare.Contains("james camerons deepsea challenge", StringComparison.OrdinalIgnoreCase))
                Add("James Cameron's Deepsea Challenge");

            if (compare.Contains("apocalypse now final cut", StringComparison.OrdinalIgnoreCase))
                Add("Apocalypse Now");

            if (compare.Contains("avatar fire and ash", StringComparison.OrdinalIgnoreCase) ||
                compare.Contains("avatar fuoco e cenere", StringComparison.OrdinalIgnoreCase))
            {
                Add("Avatar: Fire and Ash");
                Add("Avatar Fuoco e Cenere");
            }

            if (compare.Contains("star wars", StringComparison.OrdinalIgnoreCase) &&
                (compare.Contains("risveglio della forza", StringComparison.OrdinalIgnoreCase) ||
                 compare.Contains("force awakens", StringComparison.OrdinalIgnoreCase) ||
                 compare.Contains("episodio vii", StringComparison.OrdinalIgnoreCase) ||
                 compare.Contains("episode vii", StringComparison.OrdinalIgnoreCase)))
            {
                Add("Star Wars: Il risveglio della forza");
                Add("Star Wars: The Force Awakens");
            }

            if (compare.Contains("signore degli anelli", StringComparison.OrdinalIgnoreCase) &&
                compare.Contains("compagnia", StringComparison.OrdinalIgnoreCase))
            {
                Add("Il Signore degli Anelli - La Compagnia dell'Anello");
                Add("The Lord of the Rings: The Fellowship of the Ring");
            }

            if (string.Equals(compare, "il gladiatore", StringComparison.OrdinalIgnoreCase))
                Add("Gladiator");

            if (compare.Contains("hobbit", StringComparison.OrdinalIgnoreCase) &&
                compare.Contains("viaggio inaspettato", StringComparison.OrdinalIgnoreCase))
            {
                Add("Lo Hobbit: Un viaggio inaspettato");
                Add("The Hobbit: An Unexpected Journey");
            }

            if (TryGetKnownFranchiseBaseKey(stripped, out var baseKey) &&
                string.Equals(baseKey, "howtotrainyourdragon", StringComparison.Ordinal))
            {
                if (TryExtractExplicitSequelOrdinal(stripped, out var ordinal))
                {
                    switch (ordinal)
                    {
                        case 1:
                            Add("How to Train Your Dragon");
                            Add("Dragon Trainer");
                            break;
                        case 2:
                            Add("How to Train Your Dragon 2");
                            Add("Dragon Trainer 2");
                            break;
                        case 3:
                            Add("How to Train Your Dragon: The Hidden World");
                            Add("Dragon Trainer 3");
                            break;
                    }
                }
                else
                {
                    Add("How to Train Your Dragon");
                    Add("Dragon Trainer");
                }
            }

            return variants;
        }

        /// <summary>
        /// Usa TMDb con vari fallback (it/en, con/senza anno) e,
        /// se durationSeconds è valorizzata, confronta anche il runtime TMDb
        /// entro una tolleranza per evitare match sbagliati.
        /// </summary>
        private static string? TryDownloadPoster(
            string searchTitle,
            int? searchYear,
            double? durationSeconds,
            CancellationToken ct,
            out string? tmdbTitle,
            out int? tmdbYear)
        {
            tmdbTitle = null;
            tmdbYear = null;

            try
            {
                if (string.IsNullOrWhiteSpace(searchTitle))
                    return null;

                if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                    TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                    return null;

                string? localPosterPath;
                var expectedDurationSeconds = durationSeconds;

                foreach (var titleVariant in BuildMovieSearchTitleVariants(searchTitle))
                {
                    if (searchYear.HasValue)
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;

                        if (TryOneTmdbCall(titleVariant, searchYear, expectedDurationSeconds, "it-IT", ct,
                                ref tmpTitle, ref tmpYear, out localPosterPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localPosterPath;
                        }

                        MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                    }

                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;

                        if (TryOneTmdbCall(titleVariant, null, expectedDurationSeconds, "it-IT", ct,
                                ref tmpTitle, ref tmpYear, out localPosterPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localPosterPath;
                        }

                        MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                    }

                    if (searchYear.HasValue)
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;

                        if (TryOneTmdbCall(titleVariant, searchYear, expectedDurationSeconds, "en-US", ct,
                                ref tmpTitle, ref tmpYear, out localPosterPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localPosterPath;
                        }

                        MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                    }

                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;

                        if (TryOneTmdbCall(titleVariant, null, expectedDurationSeconds, "en-US", ct,
                                ref tmpTitle, ref tmpYear, out localPosterPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localPosterPath;
                        }

                        MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                    }
                }

                if (expectedDurationSeconds.HasValue)
                {
                    foreach (var titleVariant in BuildMovieSearchTitleVariants(searchTitle))
                    {
                        if (searchYear.HasValue)
                        {
                            string? tmpTitle = null;
                            int? tmpYear = null;

                            if (TryOneTmdbCall(titleVariant, searchYear, null, "it-IT", ct,
                                    ref tmpTitle, ref tmpYear, out localPosterPath))
                            {
                                tmdbTitle = tmpTitle;
                                tmdbYear = tmpYear;
                                return localPosterPath;
                            }

                            MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                        }

                        {
                            string? tmpTitle = null;
                            int? tmpYear = null;

                            if (TryOneTmdbCall(titleVariant, null, null, "it-IT", ct,
                                    ref tmpTitle, ref tmpYear, out localPosterPath))
                            {
                                tmdbTitle = tmpTitle;
                                tmdbYear = tmpYear;
                                return localPosterPath;
                            }

                            MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                        }

                        if (searchYear.HasValue)
                        {
                            string? tmpTitle = null;
                            int? tmpYear = null;

                            if (TryOneTmdbCall(titleVariant, searchYear, null, "en-US", ct,
                                    ref tmpTitle, ref tmpYear, out localPosterPath))
                            {
                                tmdbTitle = tmpTitle;
                                tmdbYear = tmpYear;
                                return localPosterPath;
                            }

                            MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                        }

                        {
                            string? tmpTitle = null;
                            int? tmpYear = null;

                            if (TryOneTmdbCall(titleVariant, null, null, "en-US", ct,
                                    ref tmpTitle, ref tmpYear, out localPosterPath))
                            {
                                tmdbTitle = tmpTitle;
                                tmdbYear = tmpYear;
                                return localPosterPath;
                            }

                            MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? TryDownloadBackdrop(
            string searchTitle,
            int? searchYear,
            double? durationSeconds,
            CancellationToken ct,
            out string? tmdbTitle,
            out int? tmdbYear)
        {
            tmdbTitle = null;
            tmdbYear = null;

            try
            {
                if (string.IsNullOrWhiteSpace(searchTitle))
                    return null;

                if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                    TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                    return null;

                string? localBackdropPath;
                var expectedDurationSeconds = durationSeconds;

                foreach (var titleVariant in BuildMovieSearchTitleVariants(searchTitle))
                {
                    if (searchYear.HasValue)
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;
                        if (TryOneTmdbBackdropCall(titleVariant, searchYear, expectedDurationSeconds, "it-IT", ct,
                                ref tmpTitle, ref tmpYear, out localBackdropPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localBackdropPath;
                        }

                        MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                    }

                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;
                        if (TryOneTmdbBackdropCall(titleVariant, null, expectedDurationSeconds, "it-IT", ct,
                                ref tmpTitle, ref tmpYear, out localBackdropPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localBackdropPath;
                        }

                        MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                    }

                    if (searchYear.HasValue)
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;
                        if (TryOneTmdbBackdropCall(titleVariant, searchYear, expectedDurationSeconds, "en-US", ct,
                                ref tmpTitle, ref tmpYear, out localBackdropPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localBackdropPath;
                        }

                        MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                    }

                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;
                        if (TryOneTmdbBackdropCall(titleVariant, null, expectedDurationSeconds, "en-US", ct,
                                ref tmpTitle, ref tmpYear, out localBackdropPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localBackdropPath;
                        }

                        MergeResolvedTitle(ref tmdbTitle, ref tmdbYear, tmpTitle, tmpYear);
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Singola chiamata a search/movie TMDb in una lingua specifica
        /// (eventuale filtro per anno). Scorriamo alcuni risultati e
        /// accettiamo il primo che passa i controlli di anno/durata.
        /// </summary>
        private static bool TryOneTmdbCall(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            CancellationToken ct,
            ref string? tmdbTitle,
            ref int? tmdbYear,
            out string? localPosterPath)
        {
            localPosterPath = null;

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

            var orderedCandidates = OrderMovieSearchCandidates(
                searchTitle,
                CollectMovieSearchCandidates(results, searchTitle, searchYear, expectedDurationSeconds, language, ct));

            if (orderedCandidates.Count == 0)
                return false;

            foreach (var candidate in orderedCandidates)
            {
                tmdbTitle = candidate.Title ?? candidate.OriginalTitle ?? searchTitle;
                tmdbYear = candidate.Year;

                var result = candidate.Result;
                string? posterPathTmdb = null;
                if (result.TryGetProperty("poster_path", out var posterProp))
                    posterPathTmdb = posterProp.GetString();

                if (TryDownloadPosterFromTmdbPath("movie", candidate.Title ?? candidate.OriginalTitle ?? searchTitle, candidate.Year, posterPathTmdb, ct, out localPosterPath))
                    return true;

                if (candidate.MovieId > 0 &&
                    TryDownloadBestPosterForMovie(candidate.MovieId, candidate.Title ?? candidate.OriginalTitle ?? searchTitle, candidate.Year, ct, out localPosterPath))
                {
                    return true;
                }
            }

            tmdbTitle = orderedCandidates[0].Title ?? orderedCandidates[0].OriginalTitle ?? searchTitle;
            tmdbYear = orderedCandidates[0].Year;
            return false;
        }

        private static bool TryOneTmdbTitleOnlyCall(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            CancellationToken ct,
            ref string? tmdbTitle,
            ref int? tmdbYear)
        {
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

            var orderedCandidates = OrderMovieSearchCandidates(
                searchTitle,
                CollectMovieSearchCandidates(results, searchTitle, searchYear, expectedDurationSeconds, language, ct));

            var selected = orderedCandidates.FirstOrDefault();
            if (selected == null)
                return false;

            tmdbTitle = selected.Title ?? selected.OriginalTitle ?? searchTitle;
            tmdbYear = selected.Year;
            return true;
        }

        private static bool TryDownloadBestPosterForMovie(
            int movieId,
            string candidateTitle,
            int? candidateYear,
            CancellationToken ct,
            out string? localPosterPath)
        {
            localPosterPath = null;

            string url = $"https://api.themoviedb.org/3/movie/{movieId}/images?api_key={TmdbApiKey}&include_image_language=it,en,null";
            using var resp = GetTmdbResponse(url, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("posters", out var posters) ||
                posters.ValueKind != JsonValueKind.Array ||
                posters.GetArrayLength() == 0)
            {
                return false;
            }

            string? bestPath = null;
            double bestScore = double.MinValue;

            foreach (var poster in posters.EnumerateArray())
            {
                if (!poster.TryGetProperty("file_path", out var pathProp))
                    continue;

                var filePath = pathProp.GetString();
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                int width = 0;
                if (poster.TryGetProperty("width", out var widthProp) && widthProp.ValueKind == JsonValueKind.Number)
                    widthProp.TryGetInt32(out width);

                int height = 0;
                if (poster.TryGetProperty("height", out var heightProp) && heightProp.ValueKind == JsonValueKind.Number)
                    heightProp.TryGetInt32(out height);

                if (width < 220 || height < 320)
                    continue;

                double aspect = height > 0 ? (double)width / height : 0d;
                if (aspect > 0d && (aspect < 0.45d || aspect > 0.82d))
                    continue;

                string? iso639 = null;
                if (poster.TryGetProperty("iso_639_1", out var langProp) && langProp.ValueKind != JsonValueKind.Null)
                    iso639 = langProp.GetString();

                double voteAverage = 0d;
                if (poster.TryGetProperty("vote_average", out var voteAvgProp) && voteAvgProp.ValueKind == JsonValueKind.Number)
                    voteAvgProp.TryGetDouble(out voteAverage);

                int voteCount = 0;
                if (poster.TryGetProperty("vote_count", out var voteCountProp) && voteCountProp.ValueKind == JsonValueKind.Number)
                    voteCountProp.TryGetInt32(out voteCount);

                double score = GetPosterLanguageBonus(iso639) + (voteAverage * 100d) + voteCount + (height / 12d) + (width / 18d);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestPath = filePath;
            }

            if (string.IsNullOrWhiteSpace(bestPath))
                return false;

            return TryDownloadPosterFromTmdbPath("movie", candidateTitle, candidateYear, bestPath, ct, out localPosterPath);
        }

        private static bool TryDownloadBest4kBackdropForMovie(
            int movieId,
            string candidateTitle,
            int? candidateYear,
            CancellationToken ct,
            out string? localBackdropPath)
        {
            localBackdropPath = null;

            string url = $"https://api.themoviedb.org/3/movie/{movieId}/images?api_key={TmdbApiKey}";
            using var resp = GetTmdbResponse(url, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("backdrops", out var backdrops) ||
                backdrops.ValueKind != JsonValueKind.Array ||
                backdrops.GetArrayLength() == 0)
                return false;

            string? bestPath = null;
            double bestScore = double.MinValue;

            foreach (var backdrop in backdrops.EnumerateArray())
            {
                if (!backdrop.TryGetProperty("file_path", out var pathProp))
                    continue;

                var filePath = pathProp.GetString();
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                int width = 0;
                if (backdrop.TryGetProperty("width", out var widthProp) && widthProp.ValueKind == JsonValueKind.Number)
                    widthProp.TryGetInt32(out width);

                int height = 0;
                if (backdrop.TryGetProperty("height", out var heightProp) && heightProp.ValueKind == JsonValueKind.Number)
                    heightProp.TryGetInt32(out height);

                if (width < MinBackdropWidthForPlaceholder || height < MinBackdropHeightForPlaceholder)
                    continue;

                double aspect = height > 0 ? (double)width / height : 0d;
                if (aspect > 0d && (aspect < 1.55d || aspect > 2.15d))
                    continue;

                string? iso639 = null;
                if (backdrop.TryGetProperty("iso_639_1", out var langProp) && langProp.ValueKind != JsonValueKind.Null)
                    iso639 = langProp.GetString();

                double voteAverage = 0d;
                if (backdrop.TryGetProperty("vote_average", out var voteAvgProp) && voteAvgProp.ValueKind == JsonValueKind.Number)
                    voteAvgProp.TryGetDouble(out voteAverage);

                int voteCount = 0;
                if (backdrop.TryGetProperty("vote_count", out var voteCountProp) && voteCountProp.ValueKind == JsonValueKind.Number)
                    voteCountProp.TryGetInt32(out voteCount);

                bool prefersNoLanguage = string.IsNullOrWhiteSpace(iso639);
                double languageBonus = prefersNoLanguage ? 1000d : 0d;
                double score = languageBonus + (voteAverage * 100d) + voteCount + (width / 10d);

                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestPath = filePath;
            }

            if (string.IsNullOrWhiteSpace(bestPath))
                return false;

            try
            {
                string imageUrl = "https://image.tmdb.org/t/p/original" + bestPath;
                var bytes = GetTmdbBytes(imageUrl, ct);

                string hashSrc = (candidateTitle ?? string.Empty) + "|" +
                                 (candidateYear?.ToString() ?? string.Empty) + "|backdrop-4k|" + bestPath;
                string fileName = ComputeSha1(hashSrc) + ".jpg";

                string folder = GetBackdropFolder();
                string fullPath = Path.Combine(folder, fileName);
                File.WriteAllBytes(fullPath, bytes);

                if (!IsBackdropFullResolution(fullPath))
                {
                    try { File.Delete(fullPath); } catch { }
                    return false;
                }

                localBackdropPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryOneTmdbBackdropCall(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            CancellationToken ct,
            ref string? tmdbTitle,
            ref int? tmdbYear,
            out string? localBackdropPath)
        {
            localBackdropPath = null;

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

            var orderedCandidates = OrderMovieSearchCandidates(
                searchTitle,
                CollectMovieSearchCandidates(results, searchTitle, searchYear, expectedDurationSeconds, language, ct));

            if (orderedCandidates.Count == 0)
                return false;

            foreach (var candidate in orderedCandidates)
            {
                tmdbTitle = candidate.Title ?? candidate.OriginalTitle ?? searchTitle;
                tmdbYear = candidate.Year;

                if (candidate.MovieId <= 0)
                    continue;

                if (!TryDownloadBest4kBackdropForMovie(candidate.MovieId, candidate.Title ?? searchTitle, candidate.Year, ct, out localBackdropPath))
                    continue;

                return !string.IsNullOrWhiteSpace(localBackdropPath);
            }

            tmdbTitle = orderedCandidates[0].Title ?? orderedCandidates[0].OriginalTitle ?? searchTitle;
            tmdbYear = orderedCandidates[0].Year;
            return false;
        }

        private static bool LooksLikeAlternateCutTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return Regex.IsMatch(value,
                @"(?ix)(?:director'?s\s*cut|directors\s*cut|director\s*s\s*cut|final\s*cut|extended(?:\s*edition|\s*cut)?|theatrical\s*cut|special\s*edition|collector'?s\s*edition|anniversary\s*edition|ultimate\s*edition|versione\s*estesa|versione\s*integrale|uncut|remastered|restored)");
        }

        private static bool HasStrongTitleAffinity(string searchTitle, params string?[] candidateTitles)
        {
            if (!HasAcceptableTitleMatch(searchTitle, candidateTitles))
                return false;

            int score = GetTitleMatchScore(searchTitle, candidateTitles);
            if (score >= 1180)
                return true;

            string collapsedSearch = CollapseTitleForComparisonString(searchTitle);
            foreach (var candidateTitle in candidateTitles)
            {
                if (string.IsNullOrWhiteSpace(candidateTitle))
                    continue;

                string collapsedCandidate = CollapseTitleForComparisonString(candidateTitle!);
                if (!string.IsNullOrWhiteSpace(collapsedSearch) &&
                    string.Equals(collapsedSearch, collapsedCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Controlla se il risultato TMDb è "credibile" rispetto a:
        ///  - anno ricavato dal filename
        ///  - durata locale (expectedDurationSeconds) vs runtime TMDb
        /// Soprattutto per titoli molto corti (Flow, Her, Up...) siamo severi.
        /// </summary>
        private static bool IsAcceptableMatch(
            string searchTitle,
            int? originalYear,
            double? expectedDurationSeconds,
            string? candidateTitle,
            string? candidateOriginalTitle,
            int? candidateYear,
            int? candidateRuntimeMinutes)
        {
            if (string.IsNullOrWhiteSpace(candidateTitle) && string.IsNullOrWhiteSpace(candidateOriginalTitle))
                return false;

            if (!HasAcceptableTitleMatch(searchTitle, candidateTitle, candidateOriginalTitle))
                return false;

            var searchTokens = searchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool shortTitle = searchTokens.Length <= 2 && searchTitle.Length <= 8;
            bool strongTitleAffinity = HasStrongTitleAffinity(searchTitle, candidateTitle, candidateOriginalTitle);
            bool alternateCut = LooksLikeAlternateCutTitle(searchTitle) ||
                                LooksLikeAlternateCutTitle(candidateTitle) ||
                                LooksLikeAlternateCutTitle(candidateOriginalTitle);

            if (expectedDurationSeconds.HasValue && candidateRuntimeMinutes.HasValue)
            {
                double expectedMinutes = expectedDurationSeconds.Value / 60.0;
                double diffMinutes = Math.Abs(candidateRuntimeMinutes.Value - expectedMinutes);

                double maxDiff = shortTitle ? 3.0 : 7.0;
                if (strongTitleAffinity && originalYear.HasValue && candidateYear.HasValue && Math.Abs(candidateYear.Value - originalYear.Value) <= 1)
                    maxDiff = Math.Max(maxDiff, 18.0);
                else if (strongTitleAffinity)
                    maxDiff = Math.Max(maxDiff, 12.0);

                if (alternateCut)
                    maxDiff = Math.Max(maxDiff, 42.0);

                if (diffMinutes > maxDiff)
                    return false;
            }

            if (originalYear.HasValue && candidateYear.HasValue)
            {
                int diffYear = Math.Abs(candidateYear.Value - originalYear.Value);

                int maxDiffYear = shortTitle ? 2 : 5;
                if (alternateCut && strongTitleAffinity)
                    maxDiffYear = Math.Max(maxDiffYear, 8);

                if (diffYear > maxDiffYear)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Recupera il runtime (in minuti) da TMDb per un dato movieId.
        /// </summary>
        private static int? GetMovieRuntimeMinutes(int movieId, string language, CancellationToken ct)
        {
            try
            {
                string url = $"https://api.themoviedb.org/3/movie/{movieId}?api_key={TmdbApiKey}&language={language}";

                using var resp = GetTmdbResponse(url, ct);
                if (!resp.IsSuccessStatusCode)
                    return null;

                string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("runtime", out var rtProp) &&
                    rtProp.ValueKind == JsonValueKind.Number)
                {
                    return rtProp.GetInt32();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string GetPosterFolder()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "posters");

            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string GetBackdropFolder()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "backdrops");

            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string ComputeSha1(string input)
        {
            using var sha = SHA1.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // --------------------------------------------------------------------
        //                       POSTER INDEX (JSON)
        // --------------------------------------------------------------------

        private sealed class TmdbApiKeyStore
        {
            private readonly string _file;
            private readonly object _lock = new();
            private string? _value;

            private sealed class Model
            {
                public string? ApiKey { get; set; }
            }

            public TmdbApiKeyStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);
                _file = Path.Combine(folder, "tmdb.config.json");
                _value = Load();
            }

            public string? Get()
            {
                lock (_lock)
                    return _value;
            }

            public void Set(string? apiKey)
            {
                var normalized = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
                lock (_lock)
                {
                    if (string.Equals(_value, normalized, StringComparison.Ordinal))
                        return;

                    _value = normalized;
                    SaveNoLock();
                }
            }

            private string? Load()
            {
                try
                {
                    if (!File.Exists(_file))
                        return null;

                    var json = File.ReadAllText(_file, Encoding.UTF8);
                    var model = JsonSerializer.Deserialize<Model>(json);
                    return string.IsNullOrWhiteSpace(model?.ApiKey) ? null : model!.ApiKey!.Trim();
                }
                catch
                {
                    return null;
                }
            }

            private void SaveNoLock()
            {
                string? tempFile = null;
                try
                {
                    var json = JsonSerializer.Serialize(new Model { ApiKey = _value }, new JsonSerializerOptions { WriteIndented = true });
                    tempFile = _file + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(tempFile, json, new UTF8Encoding(false));

                    if (File.Exists(_file))
                        File.Replace(tempFile, _file, null, true);
                    else
                        File.Move(tempFile, _file);
                }
                catch
                {
                    // best-effort
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(tempFile))
                    {
                        try
                        {
                            if (File.Exists(tempFile))
                                File.Delete(tempFile);
                        }
                        catch { }
                    }
                }
            }
        }

        private sealed class PosterIndexStore
        {
            private sealed class PosterEntry
            {
                public string? NormalizedTitle { get; set; }
                public int? Year { get; set; }
                public string? LocalPosterPath { get; set; }
                public string? LocalBackdropPath { get; set; }
                public bool TitleResolved { get; set; }
            }

            private sealed class Model
            {
                public Dictionary<string, PosterEntry> Items { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private readonly object _lock = new();
            private Model _data;

            public PosterIndexStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "posterIndex.json");
                _data = Load(out var normalizedOnLoad);

                if (normalizedOnLoad)
                {
                    try { ScheduleSave(); } catch { }
                }

                try
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                    {
                        try { FlushPendingSave(); } catch { }
                    };
                }
                catch { }
            }

            /// <summary>
            /// Ritorna (titolo normalizzato, anno, path poster, stato risoluzione titolo) se esiste.
            /// </summary>
            public (string? title, int? year, string? localPosterPath, bool titleResolved)? TryGet(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                lock (_lock)
                {
                    string key = NormalizeIndexPathKey(path, _data.Items);
                    if (_data.Items.TryGetValue(key, out var e))
                        return (e.NormalizedTitle, e.Year, e.LocalPosterPath, e.TitleResolved);
                }

                return null;
            }

            public (string? title, int? year, string? localBackdropPath, bool titleResolved)? TryGetBackdrop(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                lock (_lock)
                {
                    string key = NormalizeIndexPathKey(path, _data.Items);
                    if (_data.Items.TryGetValue(key, out var e))
                        return (e.NormalizedTitle, e.Year, e.LocalBackdropPath, e.TitleResolved);
                }

                return null;
            }

            public void Reset(string path, string? title, int? year)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                bool changed = false;
                lock (_lock)
                {
                    string key = NormalizeIndexPathKey(path, _data.Items);
                    if (!_data.Items.TryGetValue(key, out var e))
                    {
                        e = new PosterEntry();
                        _data.Items[key] = e;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(title) &&
                        !string.Equals(e.NormalizedTitle, title, StringComparison.Ordinal))
                    {
                        e.NormalizedTitle = title;
                        changed = true;
                    }

                    if (year.HasValue && e.Year != year)
                    {
                        e.Year = year;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(e.LocalPosterPath))
                    {
                        e.LocalPosterPath = null;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(e.LocalBackdropPath))
                    {
                        e.LocalBackdropPath = null;
                        changed = true;
                    }

                    if (e.TitleResolved)
                    {
                        e.TitleResolved = false;
                        changed = true;
                    }

                    if (changed)
                        ScheduleSave();
                }

                if (changed)
                    PostersChanged?.Invoke();
            }

            /// <summary>
            /// Aggiorna o crea l'entry relativa a quel path.
            /// </summary>
            public void Update(string path, string? title, int? year, string? localPosterPath, string? localBackdropPath = null, bool? titleResolved = null)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                bool changed = false;
                bool shouldNotify = false;

                lock (_lock)
                {
                    string key = NormalizeIndexPathKey(path, _data.Items);
                    if (!_data.Items.TryGetValue(key, out var e))
                    {
                        e = new PosterEntry();
                        _data.Items[key] = e;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(title) &&
                        !string.Equals(e.NormalizedTitle, title, StringComparison.Ordinal))
                    {
                        bool hadPreviousTitle = !string.IsNullOrWhiteSpace(e.NormalizedTitle);
                        e.NormalizedTitle = title;
                        changed = true;
                        if (hadPreviousTitle || (titleResolved.HasValue && titleResolved.Value))
                            shouldNotify = true;
                    }

                    if (year.HasValue && e.Year != year)
                    {
                        e.Year = year;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(localPosterPath) &&
                        !string.Equals(e.LocalPosterPath, localPosterPath, StringComparison.OrdinalIgnoreCase))
                    {
                        e.LocalPosterPath = localPosterPath;
                        changed = true;
                        shouldNotify = true;
                    }

                    if (!string.IsNullOrWhiteSpace(localBackdropPath) &&
                        !string.Equals(e.LocalBackdropPath, localBackdropPath, StringComparison.OrdinalIgnoreCase))
                    {
                        e.LocalBackdropPath = localBackdropPath;
                        changed = true;
                        shouldNotify = true;
                    }

                    if (titleResolved.HasValue && e.TitleResolved != titleResolved.Value)
                    {
                        e.TitleResolved = titleResolved.Value;
                        changed = true;
                        if (titleResolved.Value)
                            shouldNotify = true;
                    }
                }

                if (changed)
                {
                    ScheduleSave();
                    if (shouldNotify)
                        PostersChanged?.Invoke();
                }
            }

            private static string NormalizeIndexPathKey(string path, IDictionary<string, PosterEntry>? existing = null)
            {
                string key = (path ?? string.Empty).Trim();
                if (key.Length == 0)
                    return string.Empty;

                key = key.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

                if (Path.IsPathRooted(key))
                {
                    try { key = Path.GetFullPath(key); } catch { }
                    if (key.Length >= 2 && key[1] == ':')
                        key = char.ToUpperInvariant(key[0]) + key.Substring(1);

                    return key.TrimEnd(Path.DirectorySeparatorChar);
                }

                if (existing != null)
                {
                    try
                    {
                        string fileName = Path.GetFileName(key);
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            var matches = existing.Keys
                                .Where(k => !string.IsNullOrWhiteSpace(k) &&
                                            string.Equals(Path.GetFileName(k), fileName, StringComparison.OrdinalIgnoreCase))
                                .Take(2)
                                .ToList();

                            if (matches.Count == 1)
                                return matches[0];
                        }
                    }
                    catch { }
                }

                return key;
            }

            private static PosterEntry CloneEntry(PosterEntry? source)
            {
                return new PosterEntry
                {
                    NormalizedTitle = source?.NormalizedTitle,
                    Year = source?.Year,
                    LocalPosterPath = source?.LocalPosterPath,
                    LocalBackdropPath = source?.LocalBackdropPath,
                    TitleResolved = source?.TitleResolved ?? false
                };
            }

            private static void MergeEntries(PosterEntry target, PosterEntry? incoming)
            {
                if (target == null || incoming == null)
                    return;

                bool incomingHasMoreReliableTitle =
                    !string.IsNullOrWhiteSpace(incoming.NormalizedTitle) &&
                    (string.IsNullOrWhiteSpace(target.NormalizedTitle) ||
                     (!target.TitleResolved && incoming.TitleResolved));

                if (incomingHasMoreReliableTitle)
                    target.NormalizedTitle = incoming.NormalizedTitle;
                else if (string.IsNullOrWhiteSpace(target.NormalizedTitle) && !string.IsNullOrWhiteSpace(incoming.NormalizedTitle))
                    target.NormalizedTitle = incoming.NormalizedTitle;

                if (!target.Year.HasValue && incoming.Year.HasValue)
                    target.Year = incoming.Year;
                else if (incoming.Year.HasValue && incoming.TitleResolved && !target.TitleResolved)
                    target.Year = incoming.Year;

                if (string.IsNullOrWhiteSpace(target.LocalPosterPath) && !string.IsNullOrWhiteSpace(incoming.LocalPosterPath))
                    target.LocalPosterPath = incoming.LocalPosterPath;

                if (string.IsNullOrWhiteSpace(target.LocalBackdropPath) && !string.IsNullOrWhiteSpace(incoming.LocalBackdropPath))
                    target.LocalBackdropPath = incoming.LocalBackdropPath;

                if (!target.TitleResolved && incoming.TitleResolved)
                    target.TitleResolved = true;
            }

            public string? FindEquivalentPosterPath(string path, string? title, int? year)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title))
                    return null;

                string lookupTitle = NormalizeTitleForComparisonString(title);
                if (string.IsNullOrWhiteSpace(lookupTitle))
                    return null;

                lock (_lock)
                {
                    string currentKey = NormalizeIndexPathKey(path, _data.Items);

                    foreach (var kvp in _data.Items)
                    {
                        if (string.Equals(kvp.Key, currentKey, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var entry = kvp.Value;
                        if (entry == null || string.IsNullOrWhiteSpace(entry.LocalPosterPath) || !File.Exists(entry.LocalPosterPath))
                            continue;

                        if (string.IsNullOrWhiteSpace(entry.NormalizedTitle))
                            continue;

                        if (HasSequelOrdinalConflict(title, entry.NormalizedTitle))
                            continue;

                        string otherTitle = NormalizeTitleForComparisonString(entry.NormalizedTitle);
                        if (!string.Equals(lookupTitle, otherTitle, StringComparison.Ordinal))
                            continue;

                        if (year.HasValue && entry.Year.HasValue && Math.Abs(year.Value - entry.Year.Value) > 1)
                            continue;

                        return entry.LocalPosterPath;
                    }
                }

                return null;
            }

            public string? FindEquivalentBackdropPath(string path, string? title, int? year)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title))
                    return null;

                string lookupTitle = NormalizeTitleForComparisonString(title);
                if (string.IsNullOrWhiteSpace(lookupTitle))
                    return null;

                lock (_lock)
                {
                    string currentKey = NormalizeIndexPathKey(path, _data.Items);

                    foreach (var kvp in _data.Items)
                    {
                        if (string.Equals(kvp.Key, currentKey, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var entry = kvp.Value;
                        if (entry == null || string.IsNullOrWhiteSpace(entry.LocalBackdropPath) || !File.Exists(entry.LocalBackdropPath))
                            continue;

                        if (string.IsNullOrWhiteSpace(entry.NormalizedTitle))
                            continue;

                        if (HasSequelOrdinalConflict(title, entry.NormalizedTitle))
                            continue;

                        string otherTitle = NormalizeTitleForComparisonString(entry.NormalizedTitle);
                        if (!string.Equals(lookupTitle, otherTitle, StringComparison.Ordinal))
                            continue;

                        if (year.HasValue && entry.Year.HasValue && Math.Abs(year.Value - entry.Year.Value) > 1)
                            continue;

                        return entry.LocalBackdropPath;
                    }
                }

                return null;
            }

            private Model Load(out bool normalizedOnLoad)
            {
                normalizedOnLoad = false;

                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m?.Items != null)
                        {
                            var rebuilt = new Dictionary<string, PosterEntry>(StringComparer.OrdinalIgnoreCase);

                            foreach (var kvp in m.Items)
                            {
                                string key = NormalizeIndexPathKey(kvp.Key, rebuilt);
                                if (string.IsNullOrWhiteSpace(key))
                                    continue;

                                if (!rebuilt.TryGetValue(key, out var entry))
                                {
                                    rebuilt[key] = CloneEntry(kvp.Value);
                                }
                                else
                                {
                                    MergeEntries(entry, kvp.Value);
                                    normalizedOnLoad = true;
                                }

                                if (!string.Equals(key, kvp.Key?.Trim(), StringComparison.Ordinal))
                                    normalizedOnLoad = true;
                            }

                            m.Items = rebuilt;
                            return m;
                        }
                    }
                }
                catch
                {
                    // se fallisce, partiamo da pulito
                }

                return new Model();
            }

            private CancellationTokenSource? _saveCts;

            private void FlushPendingSave()
            {
                string json;
                CancellationTokenSource? toDispose = null;
                lock (_lock)
                {
                    toDispose = _saveCts;
                    _saveCts = null;
                    json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                }

                try { toDispose?.Cancel(); } catch { }
                try { toDispose?.Dispose(); } catch { }

                string tmp = _file + ".tmp";
                try
                {
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));

                    if (File.Exists(_file))
                    {
                        try
                        {
                            File.Replace(tmp, _file, null, true);
                        }
                        catch
                        {
                            File.Copy(tmp, _file, true);
                            File.Delete(tmp);
                        }
                    }
                    else
                    {
                        File.Move(tmp, _file);
                    }
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tmp))
                            File.Delete(tmp);
                    }
                    catch { }
                }
            }

            private void ScheduleSave()
            {
                FlushPendingSave();
            }
        }
    }
}
