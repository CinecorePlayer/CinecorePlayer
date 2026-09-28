#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private static readonly object MusicApiIndexGate = new();
        private static Dictionary<string, MusicApiInfo>? MusicApiIndex;
        private static string MusicApiIndexPath => Path.Combine(AppDataDir, "musicApiIndex.json");

        private static bool MetadataRetryPending(Dictionary<string, DateTime> failures, string key)
        {
            if (!failures.TryGetValue(key, out DateTime until)) return false;
            if (until > DateTime.UtcNow) return true;
            failures.Remove(key);
            return false;
        }

        private static string MusicApiIndexKey((string Title, string Artist, string Album, double? DurationSeconds) query)
            => string.Join("\u001f", NormalizeMusicCompare(query.Title), NormalizeMusicCompare(query.Artist), NormalizeMusicCompare(query.Album));

        private static MusicApiInfo? ReadCachedMusicApiInfo(string key)
        {
            lock (MusicApiIndexGate)
            {
                if (MusicApiIndex == null)
                {
                    try
                    {
                        MusicApiIndex = File.Exists(MusicApiIndexPath)
                            ? JsonSerializer.Deserialize<Dictionary<string, MusicApiInfo>>(File.ReadAllText(MusicApiIndexPath, Encoding.UTF8))
                            : null;
                    }
                    catch { }
                    MusicApiIndex = new Dictionary<string, MusicApiInfo>(MusicApiIndex ?? new(), StringComparer.OrdinalIgnoreCase);
                }
                return MusicApiIndex.TryGetValue(key, out var cached) ? cached : null;
            }
        }

        private static void CacheMusicApiInfo(string key, MusicApiInfo info)
        {
            lock (MusicApiIndexGate)
            {
                MusicApiIndex ??= new Dictionary<string, MusicApiInfo>(StringComparer.OrdinalIgnoreCase);
                MusicApiIndex[key] = info;
                try
                {
                    Directory.CreateDirectory(AppDataDir);
                    string temporary = MusicApiIndexPath + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(MusicApiIndex), new UTF8Encoding(false));
                    File.Move(temporary, MusicApiIndexPath, overwrite: true);
                }
                catch { }
            }
        }

        private static string ResolveOverview(string path, string title, string category, string? metadataKey = null)
        {
            string? overview = TryGetOverviewFromIndex(path, title, metadataKey);
            if (string.IsNullOrWhiteSpace(overview))
                overview = TryReadSidecarOverview(path);

            overview = CleanLine(overview);
            if (!string.IsNullOrWhiteSpace(overview))
                return overview;

            return DefaultOverviewForCategory(category);
        }

        private void QueueOverviewResolve(LibraryItem? item)
        {
            if (item == null || item.IsGroup || !ShouldResolveRichOverview(item.Category))
                return;
            if (!NeedsOverviewFetch(item.Overview))
                return;

            string metadataKey = ResolveMetadataLookupKey(item.Path, item.Title);
            string? cached = TryGetOverviewFromIndex(item.Path, item.Title, metadataKey);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                item.Overview = cached;
                Invalidate();
                return;
            }

            string requestKey = NormalizeOverviewKey(item.Path);
            if (string.IsNullOrWhiteSpace(requestKey))
                return;
            if (_overviewRequests.Contains(requestKey) || MetadataRetryPending(_overviewFailures, requestKey))
                return;

            _overviewRequests.Add(requestKey);
            string language = UiEnglish ? "en-US" : "it-IT";
            string path = item.Path;
            double? durationSeconds = item.DurationMinutes.HasValue ? item.DurationMinutes.Value * 60.0 : null;

            Task.Run(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    string? overview = null;
                    foreach (string lookup in MetadataLookupCandidates(path, metadataKey, item.Title))
                    {
                        var rich = MovieMetadataService.ResolveRichMetadata(lookup, durationSeconds, language, cts.Token);
                        overview = CleanLine(rich.Overview);
                        if (!string.IsNullOrWhiteSpace(overview))
                            break;
                    }
                    if (string.IsNullOrWhiteSpace(overview))
                        return null;

                    SaveOverviewToIndex(overview, path, item.Title, metadataKey);
                    return overview;
                }
                catch
                {
                    return null;
                }
            }).ContinueWith(task =>
            {
                string? overview = null;
                try { overview = task.Result; } catch { }

                PostToUi(() =>
                {
                    _overviewRequests.Remove(requestKey);
                    if (string.IsNullOrWhiteSpace(overview))
                    {
                        _overviewFailures[requestKey] = DateTime.UtcNow.AddMinutes(1);
                        return;
                    }
                    item.Overview = overview;
                    foreach (var existing in _items.Where(existing => string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
                        existing.Overview = overview;
                    foreach (var group in _items.Where(existing => existing.IsGroup))
                        foreach (var child in group.Children.Where(child => string.Equals(child.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
                            child.Overview = overview;
                    Invalidate();
                });
            }, TaskScheduler.Default);
        }

        private void QueueGenreResolve(LibraryItem? item)
        {
            if (item == null || item.IsGroup || !ShouldResolveRichOverview(item.Category))
                return;

            if (CleanGenreList(item.Genres).Count > 0)
                return;

            string metadataKey = ResolveMetadataLookupKey(item.Path, item.Title);
            var cached = TryGetGenresFromIndex(item.Path, item.Title, metadataKey);
            if (cached != null && cached.Count > 0)
            {
                item.Genres = cached;
                Invalidate();
                return;
            }

            string requestKey = NormalizeOverviewKey(item.Path);
            if (string.IsNullOrWhiteSpace(requestKey))
                return;
            if (_genreRequests.Contains(requestKey) || MetadataRetryPending(_genreFailures, requestKey))
                return;

            _genreRequests.Add(requestKey);
            string language = UiEnglish ? "en-US" : "it-IT";
            string path = item.Path;
            double? durationSeconds = item.DurationMinutes.HasValue ? item.DurationMinutes.Value * 60.0 : null;

            Task.Run(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    List<string> genres = new();
                    foreach (string lookup in MetadataLookupCandidates(path, metadataKey, item.Title))
                    {
                        var rich = MovieMetadataService.ResolveRichMetadata(lookup, durationSeconds, language, cts.Token);
                        genres = CleanGenreList(rich.Genres);
                        if (genres.Count > 0)
                            break;
                    }

                    if (genres.Count == 0)
                        return null;

                    SaveGenresToIndex(genres, path, item.Title, metadataKey);
                    return genres;
                }
                catch
                {
                    return null;
                }
            }).ContinueWith(task =>
            {
                List<string>? genres = null;
                try { genres = task.Result; } catch { }

                PostToUi(() =>
                {
                    _genreRequests.Remove(requestKey);
                    if (genres == null || genres.Count == 0)
                    {
                        _genreFailures[requestKey] = DateTime.UtcNow.AddMinutes(1);
                        return;
                    }
                    ApplyGenresToItemCopies(item.Path, genres);
                    Invalidate();
                });
            }, TaskScheduler.Default);
        }

        private void ApplyGenresToItemCopies(string path, List<string> genres)
        {
            genres = CleanGenreList(genres);
            if (genres.Count == 0)
                return;

            foreach (var existing in _items.Where(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)))
                existing.Genres = new List<string>(genres);
            foreach (var group in _items.Where(existing => existing.IsGroup))
            {
                foreach (var child in group.Children.Where(child => string.Equals(child.Path, path, StringComparison.OrdinalIgnoreCase)))
                    child.Genres = new List<string>(genres);
            }
            if (_detailItem != null && string.Equals(_detailItem.Path, path, StringComparison.OrdinalIgnoreCase))
                _detailItem.Genres = new List<string>(genres);
        }

        private void QueueMusicInfoResolve(LibraryItem? item)
        {
            if (item == null || item.IsGroup || !string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase))
                return;

            string key = MusicInfoKey(item);
            if (string.IsNullOrWhiteSpace(key))
                return;
            if (_musicInfoFailures.TryGetValue(key, out DateTime retryAfter) && retryAfter <= DateTime.UtcNow)
                _musicInfoFailures.Remove(key);
            if (_musicInfoCache.ContainsKey(key) || _musicInfoRequests.Contains(key) || _musicInfoFailures.ContainsKey(key))
                return;

            if (!string.Equals(_musicInfoActiveKey, key, StringComparison.OrdinalIgnoreCase))
            {
                try { _musicInfoActiveRequest?.Cancel(); } catch (ObjectDisposedException) { }
            }
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _musicInfoActiveRequest = cts;
            _musicInfoActiveKey = key;
            _musicInfoRequests.Add(key);
            Task.Run(async () =>
            {
                try
                {
                    // Wheel navigation should not spend the iTunes request budget on
                    // every track the pointer passes before it settles.
                    await Task.Delay(140, cts.Token).ConfigureAwait(false);
                    return await FetchMusicInfoAsync(item, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return null; }
                catch
                {
                    return null;
                }
            }).ContinueWith(task =>
            {
                MusicApiInfo? info = null;
                try { info = task.Result; } catch { }
                PostToUi(() =>
                {
                    _musicInfoRequests.Remove(key);
                    bool superseded = !ReferenceEquals(_musicInfoActiveRequest, cts);
                    if (!superseded)
                    {
                        _musicInfoActiveRequest = null;
                        _musicInfoActiveKey = string.Empty;
                    }
                    if (info != null)
                        _musicInfoCache[key] = info;
                    else if (!superseded)
                        _musicInfoFailures[key] = DateTime.UtcNow.AddMinutes(3);
                    Invalidate();
                    cts.Dispose();
                });
            }, TaskScheduler.Default);
        }

        private static string MusicInfoKey(LibraryItem item)
            => NormalizeRootPath(item.Path);

        private void QueueMusicQualityResolve(LibraryItem? item)
        {
            if (item == null || item.IsGroup || !string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase))
                return;

            string key = NormalizeRootPath(item.Path);
            if (string.IsNullOrWhiteSpace(key) || _musicQualityCache.ContainsKey(key) || _musicQualityRequests.Contains(key))
                return;

            _musicQualityRequests.Add(key);
            string path = item.Path;
            Task.Run(() =>
            {
                try
                {
                    var probe = MediaProbe.Probe(path);
                    string codec = FirstRawNonEmpty(probe.AudioCodecDisplayName,
                        probe.AudioCodec.ToString().Replace("AV_CODEC_ID_", string.Empty));
                    string channels = AudioChannelsLabel(probe.AudioChannels, probe.AudioLooksObjectBased);
                    string rate = probe.AudioRate > 0 ? $"{probe.AudioRate / 1000.0:0.#} kHz" : string.Empty;
                    string bits = probe.AudioBits > 0 ? $"{probe.AudioBits}-bit" : string.Empty;
                    string bitrate = probe.AudioBitrateKbps > 0 ? $"{probe.AudioBitrateKbps:N0} kb/s" : string.Empty;
                    return string.Join(" · ", new[] { codec, channels, rate, bits, bitrate }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                }
                catch { return string.Empty; }
            }).ContinueWith(task =>
            {
                string quality = string.Empty;
                try { quality = task.Result; } catch { }
                PostToUi(() =>
                {
                    _musicQualityRequests.Remove(key);
                    _musicQualityCache[key] = string.IsNullOrWhiteSpace(quality)
                        ? L("Qualità non disponibile", "Quality unavailable")
                        : quality;
                    Invalidate();
                });
            }, TaskScheduler.Default);
        }

        private string MusicQualityLabel(LibraryItem item)
        {
            string key = NormalizeRootPath(item.Path);
            if (_musicQualityCache.TryGetValue(key, out string? quality) && !string.IsNullOrWhiteSpace(quality))
                return quality;
            if (!string.IsNullOrWhiteSpace(item.AudioLabel) &&
                !string.Equals(item.AudioLabel, "Audio", StringComparison.OrdinalIgnoreCase))
                return item.AudioLabel!;
            return _musicQualityRequests.Contains(key)
                ? L("Analisi in corso…", "Scanning…")
                : L("Analisi qualità…", "Scan quality…");
        }

        private static async Task<MusicApiInfo?> FetchMusicInfoAsync(LibraryItem item, CancellationToken ct)
        {
            var query = BuildMusicApiQuery(item);
            if (string.IsNullOrWhiteSpace(query.Title))
                return null;
            string cacheKey = MusicApiIndexKey(query);
            if (ReadCachedMusicApiInfo(cacheKey) is { } cached)
                return cached;

            MusicApiInfo? best = null;
            int bestScore = int.MinValue;
            foreach (string country in new[] { "IT", "US" })
            {
                foreach (string term in BuildItunesSearchTerms(query.Title, query.Artist, query.Album))
                {
                    string url = "https://itunes.apple.com/search"
                        + "?media=music&entity=song&limit=10"
                        + "&country=" + Uri.EscapeDataString(country)
                        + "&term=" + Uri.EscapeDataString(term);

                    try
                    {
                        using var response = await ItunesHttp.GetAsync(url, ct).ConfigureAwait(false);
                        if ((int)response.StatusCode == 429)
                            return bestScore >= 5 ? best : null;
                        if (!response.IsSuccessStatusCode)
                            continue;

                        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                        if (!doc.RootElement.TryGetProperty("results", out var results) ||
                            results.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var result in results.EnumerateArray())
                        {
                            string title = CleanMusicApiText(ReadString(result, "trackName"));
                            if (string.IsNullOrWhiteSpace(title))
                                continue;

                            string artist = CleanMusicApiText(ReadString(result, "artistName"));
                            string album = CleanMusicApiText(ReadString(result, "collectionName"));
                            long? ms = ReadMilliseconds(result, "trackTimeMillis");
                            double? durationSeconds = ms.HasValue && ms.Value > 0 ? ms.Value / 1000.0 : null;
                            int score = ScoreMusicApiResult(title, artist, album, durationSeconds, query);
                            if (score <= bestScore)
                                continue;

                            bestScore = score;
                            best = new MusicApiInfo
                            {
                                TrackName = title,
                                ArtistName = artist,
                                AlbumName = album,
                                Genre = CleanMusicApiText(ReadString(result, "primaryGenreName")),
                                ReleaseDate = FormatItunesDate(ReadString(result, "releaseDate")),
                                TrackNumber = ReadIntString(result, "trackNumber"),
                                TrackCount = ReadIntString(result, "trackCount"),
                                DiscNumber = ReadIntString(result, "discNumber"),
                                Duration = durationSeconds.HasValue ? FormatDuration(durationSeconds.Value / 60.0) : string.Empty,
                                Country = CleanMusicApiText(ReadString(result, "country"))
                            };
                        }

                        if (best != null && IsConfidentMusicApiResult(best, query))
                        {
                            CacheMusicApiInfo(cacheKey, best);
                            return best;
                        }
                    }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // An unreachable iTunes host times out every country/term;
                        // stop the batch so it cannot keep a metadata slot busy.
                        return bestScore >= 5 ? best : null;
                    }
                    catch (System.Net.Http.HttpRequestException) { return bestScore >= 5 ? best : null; }
                    catch (JsonException) { continue; }
                }
            }

            if (bestScore >= 5 && best != null)
                CacheMusicApiInfo(cacheKey, best);
            return bestScore >= 5 ? best : null;
        }

        private static (string Title, string Artist, string Album, double? DurationSeconds) BuildMusicApiQuery(LibraryItem item)
        {
            string title = CleanMusicTrackTitle(item.Title);
            string artist = CleanMusicApiText(FirstRawNonEmpty(item.ArtistName, item.AlbumArtist));
            string album = CleanMusicApiText(item.AlbumTitle);
            double? duration = item.DurationMinutes.HasValue && item.DurationMinutes.Value > 0
                ? item.DurationMinutes.Value * 60.0
                : null;

            if (!IsNetworkPath(item.Path) &&
                (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album)))
            {
                try
                {
                    var tags = MediaProbe.ReadAudioTags(item.Path);
                    title = CleanMusicTrackTitle(FirstRawNonEmpty(tags.Title, title, Path.GetFileNameWithoutExtension(item.Path)));
                    artist = CleanMusicApiText(FirstRawNonEmpty(tags.Artist, tags.AlbumArtist, artist));
                    album = CleanMusicApiText(FirstRawNonEmpty(tags.Album, album));
                }
                catch { }
            }

            if (!IsNetworkPath(item.Path) && (string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(artist)))
            {
                var inferred = InferAlbumInfo(item.Path);
                if (string.IsNullOrWhiteSpace(album))
                    album = inferred.Album;
                if (string.IsNullOrWhiteSpace(artist))
                    artist = inferred.Artist;
            }
            if (string.IsNullOrWhiteSpace(title))
                title = CleanMusicTrackTitle(Path.GetFileNameWithoutExtension(item.Path));

            return (title, artist, album, duration);
        }

        private static bool IsConfidentMusicApiResult(MusicApiInfo result,
            (string Title, string Artist, string Album, double? DurationSeconds) query)
        {
            if (!string.Equals(NormalizeMusicCompare(result.TrackName), NormalizeMusicCompare(query.Title), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(query.Artist) && !SimilarMusic(result.ArtistName, query.Artist))
                return false;
            if (!string.IsNullOrWhiteSpace(query.Album) && !SimilarMusic(result.AlbumName, query.Album))
                return false;
            return true;
        }

        private static IEnumerable<string> BuildItunesSearchTerms(string title, string artist, string album)
        {
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in new[]
            {
                string.Join(" ", new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s))),
                string.Join(" ", new[] { artist, album, title }.Where(s => !string.IsNullOrWhiteSpace(s))),
                string.Join(" ", new[] { album, title }.Where(s => !string.IsNullOrWhiteSpace(s))),
                title
            })
            {
                string normalized = Regex.Replace(term ?? string.Empty, @"\s+", " ").Trim();
                if (normalized.Length > 0 && yielded.Add(normalized))
                    yield return normalized;
            }
        }

        private static int ScoreMusicApiResult(string title, string artist, string album, double? durationSeconds, (string Title, string Artist, string Album, double? DurationSeconds) query)
        {
            int score = 0;
            if (SimilarMusic(title, query.Title)) score += 7;
            score += CountMusicTokenMatches(query.Title, title) * 2;

            if (!string.IsNullOrWhiteSpace(query.Artist))
            {
                if (SimilarMusic(artist, query.Artist)) score += 5;
                score += CountMusicTokenMatches(query.Artist, artist);
            }

            if (!string.IsNullOrWhiteSpace(query.Album))
            {
                if (SimilarMusic(album, query.Album)) score += 3;
                score += CountMusicTokenMatches(query.Album, album);
            }

            if (query.DurationSeconds is > 0 && durationSeconds is > 0)
            {
                double delta = Math.Abs(durationSeconds.Value - query.DurationSeconds.Value);
                if (delta <= 2.5) score += 4;
                else if (delta <= 7.0) score += 2;
                else if (delta > 30.0) score -= 2;
            }

            string joined = $"{title} {artist} {album}";
            if (Regex.IsMatch(joined, @"\b(karaoke|tribute|cover|instrumental)\b", RegexOptions.IgnoreCase))
                score -= 3;

            return score;
        }

        private static bool SimilarMusic(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;
            a = NormalizeMusicCompare(a);
            b = NormalizeMusicCompare(b);
            return a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeMusicCompare(string value)
            => Regex.Replace((value ?? string.Empty).ToLowerInvariant()
                    .Replace("feat.", " ")
                    .Replace("featuring", " ")
                    .Replace("ft.", " ")
                    .Replace("remastered", " ")
                    .Replace("remaster", " ")
                    .Replace("official", " ")
                    .Replace("lyrics", " "), @"\s+", " ").Trim();

        private static int CountMusicTokenMatches(string? needle, string? haystack)
        {
            var haystackTokens = TokenizeMusic(haystack).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (string token in TokenizeMusic(needle))
            {
                if (haystackTokens.Contains(token))
                    count++;
            }
            return count;
        }

        private static IEnumerable<string> TokenizeMusic(string? value)
            => Regex.Split((value ?? string.Empty).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+")
                .Where(token => token.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12);

        private static string CleanMusicApiText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return Regex.Replace(value.Trim(), @"\s+", " ");
        }

        private static string? ReadString(JsonElement item, string name)
        {
            try
            {
                if (item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                    return prop.GetString();
            }
            catch { }
            return null;
        }

        private static long? ReadMilliseconds(JsonElement item, string name)
        {
            try
            {
                if (item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
                {
                    if (prop.TryGetInt64(out long value) && value > 0)
                        return value;
                }
            }
            catch { }
            return null;
        }

        private static string ReadIntString(JsonElement item, string name)
        {
            try
            {
                if (item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int value) && value > 0)
                    return value.ToString(CultureInfo.InvariantCulture);
            }
            catch { }
            return string.Empty;
        }

        private static string FormatItunesDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
            return CleanMusicApiText(raw);
        }

        private static bool NeedsOverviewFetch(string? overview)
        {
            string value = CleanLine(overview);
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return value.StartsWith("Sinossi TMDb non ancora disponibile", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Episodio dalla tua libreria", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Video dalla tua libreria", StringComparison.OrdinalIgnoreCase);
        }

        private string GenreText(LibraryItem item)
        {
            var genres = GenreListFor(item);
            if (genres.Count > 0)
                return string.Join(", ", genres.Take(4));
            return ShouldResolveRichOverview(item.Category) ? L("TMDb in caricamento", "TMDb loading") : item.PlaybackCategory;
        }

        private List<string> GenreListFor(LibraryItem item)
        {
            var genres = CleanGenreList(item.Genres);
            if (genres.Count > 0)
                return genres;

            string metadataKey = ResolveMetadataLookupKey(item.Path, item.Title);
            var cached = TryGetGenresFromIndex(item.Path, item.Title, metadataKey);
            if (cached != null && cached.Count > 0)
            {
                item.Genres = cached;
                return cached;
            }

            return new List<string>();
        }

        private HashSet<string> GenreSetFor(LibraryItem item)
            => GenreListFor(item).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static int GenreOverlap(HashSet<string> currentGenres, HashSet<string> candidateGenres)
        {
            if (currentGenres.Count == 0 || candidateGenres.Count == 0)
                return 0;

            int score = 0;
            foreach (string genre in candidateGenres)
            {
                if (currentGenres.Contains(genre))
                    score++;
            }
            return score;
        }

        private static string? TryGetOverviewFromIndex(string path, string? title, string? metadataKey)
        {
            var index = LoadOverviewIndexCache();
            foreach (string key in OverviewKeys(path, title, metadataKey))
            {
                if (index.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static List<string>? TryGetGenresFromIndex(string path, string? title, string? metadataKey)
        {
            var index = LoadGenreIndexCache();
            foreach (string key in OverviewKeys(path, title, metadataKey))
            {
                if (index.TryGetValue(key, out var value))
                {
                    var genres = CleanGenreList(value);
                    if (genres.Count > 0)
                        return genres;
                }
            }
            return null;
        }

        private static void SaveOverviewToIndex(string overview, string path, string? title, string? metadataKey)
        {
            overview = CleanLine(overview);
            if (string.IsNullOrWhiteSpace(overview))
                return;

            lock (OverviewIndexCacheSync)
            {
                var index = LoadOverviewIndexCache();
                bool changed = false;
                foreach (string key in OverviewKeys(path, title, metadataKey))
                {
                    if (!index.TryGetValue(key, out string? existing) || !string.Equals(existing, overview, StringComparison.Ordinal))
                    {
                        index[key] = overview;
                        changed = true;
                    }
                }

                if (!changed)
                    return;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(OverviewIndexPath) ?? AppDataDir);
                    var model = new Dictionary<string, string>(index, StringComparer.OrdinalIgnoreCase);
                    string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(OverviewIndexPath, json, new UTF8Encoding(false));
                    OverviewIndexWriteUtc = File.GetLastWriteTimeUtc(OverviewIndexPath);
                    OverviewIndexCache = model;
                }
                catch { }
            }
        }

        private static void SaveGenresToIndex(IEnumerable<string> genres, string path, string? title, string? metadataKey)
        {
            var clean = CleanGenreList(genres);
            if (clean.Count == 0)
                return;

            lock (GenreIndexCacheSync)
            {
                var index = LoadGenreIndexCache();
                bool changed = false;
                foreach (string key in OverviewKeys(path, title, metadataKey))
                {
                    if (!index.TryGetValue(key, out var existing) || !clean.SequenceEqual(CleanGenreList(existing), StringComparer.OrdinalIgnoreCase))
                    {
                        index[key] = new List<string>(clean);
                        changed = true;
                    }
                }

                if (!changed)
                    return;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(GenreIndexPath) ?? AppDataDir);
                    var model = index.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
                    string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(GenreIndexPath, json, new UTF8Encoding(false));
                    GenreIndexWriteUtc = File.GetLastWriteTimeUtc(GenreIndexPath);
                    GenreIndexCache = model;
                }
                catch { }
            }
        }

        private static Dictionary<string, string> LoadOverviewIndexCache()
        {
            DateTime writeUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(OverviewIndexPath))
                    writeUtc = File.GetLastWriteTimeUtc(OverviewIndexPath);
            }
            catch { }

            lock (OverviewIndexCacheSync)
            {
                if (OverviewIndexCache != null && OverviewIndexWriteUtc == writeUtc)
                    return OverviewIndexCache;

                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(OverviewIndexPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(OverviewIndexPath, Encoding.UTF8));
                        JsonElement root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Object)
                            root = items;

                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    string? value = CleanLine(prop.Value.GetString());
                                    if (!string.IsNullOrWhiteSpace(value))
                                        result[prop.Name] = value!;
                                }
                            }
                        }
                    }
                }
                catch { }

                OverviewIndexCache = result;
                OverviewIndexWriteUtc = writeUtc;
                return OverviewIndexCache;
            }
        }

        private static Dictionary<string, List<string>> LoadGenreIndexCache()
        {
            DateTime writeUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(GenreIndexPath))
                    writeUtc = File.GetLastWriteTimeUtc(GenreIndexPath);
            }
            catch { }

            lock (GenreIndexCacheSync)
            {
                if (GenreIndexCache != null && GenreIndexWriteUtc == writeUtc)
                    return GenreIndexCache;

                var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(GenreIndexPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(GenreIndexPath, Encoding.UTF8));
                        JsonElement root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Object)
                            root = items;

                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in root.EnumerateObject())
                            {
                                var genres = new List<string>();
                                if (prop.Value.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var value in prop.Value.EnumerateArray())
                                    {
                                        if (value.ValueKind == JsonValueKind.String)
                                            genres.Add(value.GetString() ?? string.Empty);
                                    }
                                }
                                else if (prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    genres.AddRange((prop.Value.GetString() ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries));
                                }

                                genres = CleanGenreList(genres);
                                if (genres.Count > 0)
                                    result[prop.Name] = genres;
                            }
                        }
                    }
                }
                catch { }

                GenreIndexCache = result;
                GenreIndexWriteUtc = writeUtc;
                return GenreIndexCache;
            }
        }

        private static List<string> CleanGenreList(IEnumerable<string>? genres)
        {
            var clean = new List<string>();
            if (genres == null)
                return clean;

            foreach (string raw in genres)
            {
                string value = CleanGenreName(raw);
                if (string.IsNullOrWhiteSpace(value) || IsGenericGenreName(value))
                    continue;
                if (!clean.Contains(value, StringComparer.OrdinalIgnoreCase))
                    clean.Add(value);
                if (clean.Count >= 8)
                    break;
            }

            return clean;
        }

        private static string CleanGenreName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string cleaned = Regex.Replace(value.Trim(), @"\s+", " ");
            if (cleaned.Length == 0)
                return string.Empty;

            if (cleaned.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                cleaned = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned.ToLower(CultureInfo.CurrentCulture));
            return cleaned;
        }

        private static bool IsGenericGenreName(string value)
        {
            string key = Regex.Replace(value ?? string.Empty, @"[^\p{L}\p{N}]+", string.Empty).ToLowerInvariant();
            return key is "film" or "movie" or "movies" or "video" or "videos" or "tv" or "tvseries" or "serietv" or "series" or "show" or "shows";
        }

        private static IEnumerable<string> OverviewKeys(string path, string? title, string? metadataKey)
        {
            foreach (string? value in new[] { path, title, metadataKey, Path.GetFileNameWithoutExtension(path) })
            {
                string key = NormalizeOverviewKey(value);
                if (!string.IsNullOrWhiteSpace(key))
                    yield return key;
            }
        }

        private static string NormalizeOverviewKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string key = value.Trim();
            try
            {
                if (Path.IsPathRooted(key))
                    key = NormalizeRootPath(key);
            }
            catch { }

            key = Regex.Replace(key, @"\s+", " ");
            return key.ToLowerInvariant();
        }

        private static double? TryReadSidecarRuntimeMinutes(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name))
                    return null;

                foreach (string sidecar in new[]
                {
                    Path.Combine(dir, name + ".nfo"),
                    Path.Combine(dir, name + ".json"),
                    Path.Combine(dir, "movie.nfo")
                })
                {
                    if (!File.Exists(sidecar))
                        continue;

                    string text = File.ReadAllText(sidecar);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var xml = Regex.Match(text, @"<(runtime|duration)>\s*([^<]+)\s*</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (xml.Success)
                    {
                        double? v = TryParseDurationFromText(xml.Groups[2].Value);
                        if (v.HasValue) return v;
                    }

                    var jsonNumber = Regex.Match(text, @"""(runtime|runtimeMinutes|duration|durationMinutes)""\s*:\s*(?<num>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (jsonNumber.Success && double.TryParse(jsonNumber.Groups["num"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n))
                        return n > 1000 ? n / 60.0 : n;

                    var jsonText = Regex.Match(text, @"""(runtime|runtimeMinutes|duration|durationMinutes)""\s*:\s*""(?<text>(?:\\.|[^""\\])*)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (jsonText.Success)
                    {
                        double? v = TryParseDurationFromText(Regex.Unescape(jsonText.Groups["text"].Value));
                        if (v.HasValue) return v;
                    }
                }
            }
            catch { }

            return null;
        }

        private static double? TryParseDurationFromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string value = text.Trim();
            var hms = Regex.Match(value, @"(?<!\d)(?<h>\d{1,2})\s*[:h]\s*(?<m>\d{1,2})(?:\s*[:m]\s*(?<s>\d{1,2}))?", RegexOptions.IgnoreCase);
            if (hms.Success && int.TryParse(hms.Groups["h"].Value, out int h) && int.TryParse(hms.Groups["m"].Value, out int m))
                return h * 60 + m;

            var hm = Regex.Match(value, @"(?:(?<h>\d{1,2})\s*(?:h|hr|hrs|hour|hours|ore))?\s*(?<m>\d{1,3})\s*(?:m|min|mins|minute|minutes|minuti)\b", RegexOptions.IgnoreCase);
            if (hm.Success && int.TryParse(hm.Groups["m"].Value, out m))
            {
                int h2 = 0;
                if (hm.Groups["h"].Success)
                    int.TryParse(hm.Groups["h"].Value, out h2);
                return h2 * 60 + m;
            }

            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double raw) && raw > 0)
                return raw > 1000 ? raw / 60.0 : raw;

            return null;
        }

        private static string? TryReadSidecarOverview(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name))
                    return null;

                foreach (string sidecar in new[]
                {
                    Path.Combine(dir, name + ".nfo"),
                    Path.Combine(dir, name + ".json"),
                    Path.Combine(dir, "movie.nfo")
                })
                {
                    if (!File.Exists(sidecar))
                        continue;
                    string text = File.ReadAllText(sidecar);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var xml = Regex.Match(text, @"<(plot|outline|overview|description)>\s*(.*?)\s*</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (xml.Success)
                        return System.Net.WebUtility.HtmlDecode(Regex.Replace(xml.Groups[2].Value, @"\s+", " ").Trim());

                    var json = Regex.Match(text, @"""(overview|plot|description|tagline)""\s*:\s*""((?:\\.|[^""\\])*)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (json.Success)
                    {
                        try { return JsonSerializer.Deserialize<string>("\"" + json.Groups[2].Value + "\""); }
                        catch { return Regex.Unescape(json.Groups[2].Value); }
                    }
                }
            }
            catch { }

            return null;
        }


    }
}
