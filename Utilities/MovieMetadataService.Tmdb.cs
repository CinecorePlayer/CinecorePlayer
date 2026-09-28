#nullable enable
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

namespace CinecorePlayer2025.Utilities
{
    internal static partial class MovieMetadataService
    {
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
                    foreach (var attempt in BuildLanguageYearAttempts(info.Year))
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;
                        if (TryOneTmdbTvPosterCall(titleVariant, attempt.Year, durationSeconds, attempt.Language, info, ct, ref tmpTitle, ref tmpYear, out localPosterPath))
                        {
                            tmdbTitle = tmpTitle;
                            tmdbYear = tmpYear;
                            return localPosterPath;
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
                    foreach (var attempt in BuildLanguageYearAttempts(info.Year))
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;
                        if (TryOneTmdbTvBackdropCall(titleVariant, attempt.Year, durationSeconds, attempt.Language, info, ct, ref tmpTitle, ref tmpYear, out localBackdropPath))
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

        private static bool TryOneTmdbEpisodeStillCall(
            string searchTitle,
            int? searchYear,
            string language,
            MediaTitleInfo info,
            CancellationToken ct,
            out string? localStillPath,
            out string? tmdbTitle,
            out int? tmdbYear)
        {
            localStillPath = null;
            tmdbTitle = null;
            tmdbYear = null;

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

                string? candidateTitle = ReadJsonString(result, "name") ?? ReadJsonString(result, "original_name");
                string? candidateOriginalTitle = ReadJsonString(result, "original_name");
                int? candidateYear = ReadYearFromDate(result, "first_air_date");

                int seriesId = 0;
                if (result.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    idProp.TryGetInt32(out seriesId);

                if (seriesId <= 0)
                    continue;

                if (!IsAcceptableTvMatch(searchTitle, searchYear, null, candidateTitle, candidateOriginalTitle, candidateYear, null))
                    continue;

                tmdbTitle = ResolveTvDisplayTitle(seriesId, language, info, candidateTitle ?? candidateOriginalTitle ?? searchTitle, ct);
                tmdbYear = candidateYear;

                if (TryDownloadEpisodeStill(seriesId, candidateTitle ?? candidateOriginalTitle ?? searchTitle, candidateYear, info, language, ct, out localStillPath))
                    return true;
            }

            return false;
        }

        private static bool TryDownloadEpisodeStill(
            int seriesId,
            string candidateTitle,
            int? candidateYear,
            MediaTitleInfo info,
            string language,
            CancellationToken ct,
            out string? localStillPath)
        {
            localStillPath = null;
            if (!info.SeasonNumber.HasValue || !info.EpisodeNumber.HasValue || seriesId <= 0)
                return false;

            try
            {
                string url = $"https://api.themoviedb.org/3/tv/{seriesId}/season/{info.SeasonNumber.Value}/episode/{info.EpisodeNumber.Value}?api_key={TmdbApiKey}&language={language}";
                using var resp = GetTmdbResponse(url, ct);
                if (!resp.IsSuccessStatusCode)
                    return false;

                string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                string? stillPath = ReadJsonString(doc.RootElement, "still_path");
                if (string.IsNullOrWhiteSpace(stillPath))
                    return false;

                var bytes = GetTmdbImageBytesForPath(stillPath, ct, "original", "w1280");
                string hashSrc = (candidateTitle ?? string.Empty) + "|tv-episode-still|" +
                                 (candidateYear?.ToString() ?? string.Empty) + "|" +
                                 (info.SeasonNumber?.ToString() ?? string.Empty) + "|" +
                                 (info.EpisodeNumber?.ToString() ?? string.Empty) + "|" + stillPath;
                string fullPath = Path.Combine(GetEpisodeStillFolder(), ComputeSha1(hashSrc) + ".jpg");
                File.WriteAllBytes(fullPath, bytes);

                if (!IsLandscapeStillImage(fullPath))
                {
                    try { File.Delete(fullPath); } catch { }
                    return false;
                }

                localStillPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
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
                // w500 e' ideale per la UI; se quell'edge CDN fallisce proviamo l'originale.
                var bytes = GetTmdbImageBytesForPath(posterPathTmdb, ct, "w500", "original");

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
            string preferred = string.Equals(PreferredTmdbLanguage(), "en-US", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            string secondary = string.Equals(preferred, "en", StringComparison.OrdinalIgnoreCase) ? "it" : "en";

            if (string.Equals(iso639, preferred, StringComparison.OrdinalIgnoreCase))
                return 1300d;

            if (string.IsNullOrWhiteSpace(iso639))
                return 900d;

            if (string.Equals(iso639, secondary, StringComparison.OrdinalIgnoreCase))
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

            string url = $"https://api.themoviedb.org/3/tv/{seriesId}/images?api_key={TmdbApiKey}&include_image_language={PreferredImageLanguageList()}";
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
                var bytes = GetTmdbImageBytesForPath(bestPath, ct, "original", "w1280");

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
                string fallbackLanguage = PreferredTmdbLanguages().FirstOrDefault(l => !string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) ?? "en-US";
                var epTitle = GetTvEpisodeTitle(seriesId, info.SeasonNumber.Value, info.EpisodeNumber.Value, language, ct)
                           ?? GetTvEpisodeTitle(seriesId, info.SeasonNumber.Value, info.EpisodeNumber.Value, fallbackLanguage, ct);
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
                if (count++ >= 20)
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

                bool acceptable = IsAcceptableMatch(
                    searchTitle,
                    searchYear,
                    expectedDurationSeconds,
                    candidateTitle,
                    candidateOriginalTitle,
                    candidateYear,
                    candidateRuntimeMinutes);

                bool yearCompatible = !searchYear.HasValue || !candidateYear.HasValue ||
                                      Math.Abs(candidateYear.Value - searchYear.Value) <= 1;
                bool alternativeTitleMatch = !acceptable && yearCompatible && movieId > 0 &&
                                             HasTmdbAlternativeTitleMatch(movieId, searchTitle, ct);

                if (!acceptable && !alternativeTitleMatch)
                {
                    // Runtime data in TMDb is often different for alternate cuts, remuxes,
                    // restored editions, or files with intros. Do not throw away a result
                    // when title affinity and year are strong enough.
                    var searchTokens = searchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    bool shortTitle = searchTokens.Length <= 2 && searchTitle.Length <= 8;
                    int relaxedScore = GetTitleMatchScore(searchTitle, candidateTitle, candidateOriginalTitle);
                    string collapsedSearch = CollapseTitleForComparisonString(searchTitle);
                    string collapsedCandidate = CollapseTitleForComparisonString(candidateTitle ?? candidateOriginalTitle ?? string.Empty);
                    bool exactCollapsed = !string.IsNullOrWhiteSpace(collapsedSearch) &&
                                          string.Equals(collapsedSearch, collapsedCandidate, StringComparison.OrdinalIgnoreCase);
                    bool strongRelaxedTitle = HasStrongTitleAffinity(searchTitle, candidateTitle, candidateOriginalTitle) ||
                                              exactCollapsed ||
                                              relaxedScore >= (shortTitle ? 1540 : 1300);
                    if (!strongRelaxedTitle || !yearCompatible)
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

        private static bool HasTmdbAlternativeTitleMatch(int movieId, string searchTitle, CancellationToken ct)
        {
            if (movieId <= 0 || string.IsNullOrWhiteSpace(searchTitle))
                return false;

            try
            {
                string url = $"https://api.themoviedb.org/3/movie/{movieId}/alternative_titles?api_key={TmdbApiKey}";
                using var response = GetTmdbResponse(url, ct);
                if (!response.IsSuccessStatusCode)
                    return false;

                string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("titles", out var titles) ||
                    titles.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                foreach (var item in titles.EnumerateArray())
                {
                    string? alternative = ReadJsonString(item, "title");
                    if (HasAcceptableTitleMatch(searchTitle, alternative))
                        return true;
                }
            }
            catch { }

            return false;
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

            string dePromoted = StripSearchDecorators(StripPromotionalSuffixes(normalized));
            Add(dePromoted);
            Add(Regex.Replace(dePromoted, @"\s*[\-:]\s*", " ").Trim());
            Add(Regex.Replace(stripped, @"\s*[\(\[][^\)\]]+[\)\]]\s*", " ").Trim());
            Add(Regex.Replace(stripped, @"(?ix)\b(?:director'?s\s*cut|directors\s*cut|final\s*cut|extended(?:\s*edition|\s*cut)?|theatrical\s*cut|special\s*edition|collector'?s\s*edition|anniversary\s*edition|ultimate\s*edition|versione\s*estesa|versione\s*integrale|uncut|remastered|restored)\b.*$", "").Trim());
            Add(Regex.Replace(stripped, "['\\u2019`]", "").Trim());
            Add(stripped.Replace("&", "and"));
            Add(stripped.Replace("&", "e"));
            Add(Regex.Replace(stripped, @"[._]+", " ").Trim());

            string compare = NormalizeTitleForComparisonString(stripped);

            if (compare.Contains("james camerons deepsea challenge", StringComparison.OrdinalIgnoreCase))
                Add("James Cameron's Deepsea Challenge");

            if (compare.Contains("apocalypse now final cut", StringComparison.OrdinalIgnoreCase))
                Add("Apocalypse Now");

            if (compare.Contains("quiet girl", StringComparison.OrdinalIgnoreCase) ||
                compare.Contains("an cailin ciuin", StringComparison.OrdinalIgnoreCase) ||
                compare.Contains("ragazza silenziosa", StringComparison.OrdinalIgnoreCase))
            {
                Add("The Quiet Girl");
                Add("An Cailín Ciúin");
                Add("La ragazza silenziosa");
            }

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
                    foreach (var attempt in BuildLanguageYearAttempts(searchYear))
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;

                        if (TryOneTmdbCall(titleVariant, attempt.Year, expectedDurationSeconds, attempt.Language, ct,
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
                        foreach (var attempt in BuildLanguageYearAttempts(searchYear))
                        {
                            string? tmpTitle = null;
                            int? tmpYear = null;

                            if (TryOneTmdbCall(titleVariant, attempt.Year, null, attempt.Language, ct,
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
                    foreach (var attempt in BuildLanguageYearAttempts(searchYear))
                    {
                        string? tmpTitle = null;
                        int? tmpYear = null;
                        if (TryOneTmdbBackdropCall(titleVariant, attempt.Year, expectedDurationSeconds, attempt.Language, ct,
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

            string url = $"https://api.themoviedb.org/3/movie/{movieId}/images?api_key={TmdbApiKey}&include_image_language={PreferredImageLanguageList()}";
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
                var bytes = GetTmdbImageBytesForPath(bestPath, ct, "original", "w1280");

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
                @"(?ix)\b(?:director'?s\s*cut|directors\s*cut|director\s*s\s*cut|final\s*cut|extended(?:\s*edition|\s*cut)?|theatrical\s*cut|special\s*edition|collector'?s\s*edition|anniversary\s*edition|ultimate\s*edition|versione\s*estesa|versione\s*integrale|uncut|remastered|restored)\b");
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

        private static string GetEpisodeStillFolder()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "episode-stills");

            Directory.CreateDirectory(folder);
            return folder;
        }

        private static bool IsLandscapeStillImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var img = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
                if (img.Width < 600 || img.Height < 320)
                    return false;

                double aspect = img.Height > 0 ? img.Width / (double)img.Height : 0d;
                return aspect >= 1.35d && aspect <= 2.35d;
            }
            catch
            {
                return false;
            }
        }

        private readonly struct PosterValidationEntry
        {
            public PosterValidationEntry(long length, long writeTicks, bool valid)
            {
                Length = length;
                WriteTicks = writeTicks;
                Valid = valid;
            }

            public long Length { get; }
            public long WriteTicks { get; }
            public bool Valid { get; }
        }

        private static readonly object PosterValidationSync = new();
        private static readonly Dictionary<string, PosterValidationEntry> PosterValidationCache =
            new(StringComparer.OrdinalIgnoreCase);

        internal static bool IsUsablePosterImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0)
                    return false;

                long length = info.Length;
                long writeTicks = info.LastWriteTimeUtc.Ticks;
                lock (PosterValidationSync)
                {
                    if (PosterValidationCache.TryGetValue(path, out PosterValidationEntry cached) &&
                        cached.Length == length && cached.WriteTicks == writeTicks)
                    {
                        return cached.Valid;
                    }
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var image = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: true);
                double aspect = image.Width / (double)Math.Max(1, image.Height);
                bool valid = image.Width >= 120 && image.Height >= 170 && aspect >= 0.38d && aspect <= 1.05d;
                lock (PosterValidationSync)
                {
                    if (PosterValidationCache.Count > 1024)
                        PosterValidationCache.Clear();
                    PosterValidationCache[path] = new PosterValidationEntry(length, writeTicks, valid);
                }
                return valid;
            }
            catch
            {
                try
                {
                    var info = new FileInfo(path);
                    lock (PosterValidationSync)
                        PosterValidationCache[path] = new PosterValidationEntry(info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc.Ticks : 0, false);
                }
                catch { }
                return false;
            }
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

    }
}
