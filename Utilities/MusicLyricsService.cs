#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    public sealed partial class MusicLyricsService : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, Task<LyricsResult?>> _memory = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task> _backgroundSyncJobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentBag<Action<LyricsResult>>> _backgroundSyncObservers = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _backgroundSyncCts = new();
        private readonly LyricsSynchronizer _lyricsSynchronizer;

        public MusicLyricsService(ILyricsRecognitionBackend? recognitionBackend = null, string? cacheDirectory = null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer2025/1.0");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _lyricsSynchronizer = new LyricsSynchronizer(recognitionBackend);
            StartLibraryWorker(cacheDirectory);
            Dbg.Log($"[LYRICS] service initialized; recognitionStrategy='{_lyricsSynchronizer.RecognitionBackendName}'", Dbg.LogLevel.Info);
        }

        public Task<LyricsResult?> FindForMediaAsync(string? mediaPath, CancellationToken ct)
            => FindForMediaAsync(mediaPath, ct, null, null);

        /// <summary>
        /// Finds the provider lyrics and optionally publishes that result before
        /// the potentially long audio-recognition fallback completes. The
        /// callback is informational/UI-facing only; the returned task still
        /// completes with the final synchronized-or-plain result.
        /// </summary>
        public Task<LyricsResult?> FindForMediaAsync(
            string? mediaPath,
            CancellationToken ct,
            Action<LyricsResult>? onProviderResult)
            => FindForMediaAsync(mediaPath, ct, onProviderResult, null);

        /// <summary>
        /// Looks up lyrics for a music track. Provider lyrics are returned as
        /// soon as they are available; plain lyrics also start a detached
        /// synchronization job so model download/recognition never blocks track
        /// changes or application shutdown. The final callback receives either
        /// accepted synchronized lyrics or an explicit plain-lyrics rejection.
        /// </summary>
        public Task<LyricsResult?> FindForMediaAsync(
            string? mediaPath,
            CancellationToken ct,
            Action<LyricsResult>? onProviderResult,
            Action<LyricsResult>? onSynchronizationResult)
        {
            if (!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath))
            {
                foreach (string lrcPath in new[] { Path.ChangeExtension(mediaPath, ".lrc"), mediaPath + ".lrc" })
                {
                    try
                    {
                        if (!File.Exists(lrcPath)) continue;
                        string localText = File.ReadAllText(lrcPath);
                        var localLines = ParseSyncedLyrics(localText);
                        if (localLines.Count > 0)
                        {
                            var local = new LyricsResult("LRC locale", localText, true, lrcPath, localLines, true);
                            StoreLyrics(mediaPath, local, "synced");
                            return Task.FromResult<LyricsResult?>(local);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                var stored = ReadStoredLyrics(mediaPath);
                if (stored?.Result is LyricsResult saved)
                {
                    if (!saved.IsSynced && !IsFinalRejection(saved) &&
                        (stored.State is "queued" or "running" || DateTime.UtcNow - stored.UpdatedUtc > TimeSpan.FromMinutes(10)))
                    {
                        try { onProviderResult?.Invoke(saved); } catch { }
                        QueueBackgroundSynchronization(MediaIdentity(mediaPath), mediaPath, null, saved, onSynchronizationResult);
                    }
                    return Task.FromResult<LyricsResult?>(saved);
                }
            }
            var query = BuildQuery(mediaPath);
            Dbg.Log($"[LYRICS] request path='{mediaPath ?? string.Empty}' title='{query.Title}' artist='{query.Artist}' album='{query.Album}' duration={(query.DurationSeconds?.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}s", Dbg.LogLevel.Info);
            if (string.IsNullOrWhiteSpace(query.Title))
            {
                Dbg.Warn($"[LYRICS] skipped lookup: no usable title for '{mediaPath ?? string.Empty}'");
                return Task.FromResult<LyricsResult?>(null);
            }

            string key = !string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath)
                ? MediaIdentity(mediaPath) : string.Join("|", query.Title, query.Artist, query.Album, Math.Round(query.DurationSeconds ?? 0));
            if (_memory.TryGetValue(key, out var cached))
            {
                Dbg.Log($"[LYRICS] memory cache hit: key='{key}'", Dbg.LogLevel.Info);
                return ReturnMemoryCachedResultAsync(key, cached, ct, onProviderResult, onSynchronizationResult);
            }

            return FindAndCacheAsync(key, query, mediaPath, ct, onProviderResult, onSynchronizationResult);
        }

        private async Task<LyricsResult?> ReturnMemoryCachedResultAsync(
            string key,
            Task<LyricsResult?> cached,
            CancellationToken ct,
            Action<LyricsResult>? onProviderResult,
            Action<LyricsResult>? onSynchronizationResult)
        {
            var result = await cached.WaitAsync(ct).ConfigureAwait(false);
            if (result == null || result.IsSynced || !_backgroundSyncJobs.ContainsKey(key))
                return result;

            // A previous playback instance may have left a plain result cached
            // while its detached recognizer is still running. Subscribe the new
            // UI instance to that same job instead of starting a duplicate.
            try { onProviderResult?.Invoke(result); }
            catch (Exception ex) { Dbg.Warn($"[LYRICS] cached provider-result callback failed: {ex.GetType().Name}: {ex.Message}"); }
            if (onSynchronizationResult != null)
            {
                lock (_jobGate)
                {
                    if (_memory.TryGetValue(key, out var latest) && !ReferenceEquals(latest, cached) && latest.IsCompletedSuccessfully)
                        result = latest.Result ?? result;
                    else
                        _backgroundSyncObservers.GetOrAdd(key, _ => new ConcurrentBag<Action<LyricsResult>>()).Add(onSynchronizationResult);
                }
                if (result.IsSynced) onSynchronizationResult(result);
            }
            return result;
        }

        private async Task<LyricsResult?> FindAndCacheAsync(
            string key,
            LyricsQuery query,
            string? mediaPath,
            CancellationToken ct,
            Action<LyricsResult>? onProviderResult,
            Action<LyricsResult>? onSynchronizationResult)
        {
            // File metadata and duration describe this recording. Query it first;
            // catalog normalization may refer to another edit or live version.
            var normalizedQuery = query;
            var result = await FindLyricsAsync(query, ct).ConfigureAwait(false);
            if (result == null)
            {
                normalizedQuery = await NormalizeWithItunesAsync(query, ct).ConfigureAwait(false);
                normalizedQuery = normalizedQuery with { DurationSeconds = query.DurationSeconds ?? normalizedQuery.DurationSeconds };
                if (normalizedQuery != query) result = await FindLyricsAsync(normalizedQuery, ct).ConfigureAwait(false);
            }
            // Publish the provider cache before starting a possibly immediate cached
            // alignment, otherwise a plain result can overwrite the completed sync.
            if (result != null)
            {
                if (!string.IsNullOrWhiteSpace(mediaPath) && ReadStoredLyrics(mediaPath)?.Result is { IsSynced: true } completed)
                    result = completed;
                _memory[key] = Task.FromResult<LyricsResult?>(result);
                if (!string.IsNullOrWhiteSpace(mediaPath))
                    StoreLyrics(mediaPath, result, result.IsSynced ? "synced" : "queued");
            }

            if (result == null)
                Dbg.Warn($"[LYRICS] no provider returned lyrics for title='{normalizedQuery.Title}' artist='{normalizedQuery.Artist}'");
            else
            {
                try
                {
                    onProviderResult?.Invoke(result);
                }
                catch (Exception ex)
                {
                    Dbg.Warn($"[LYRICS] provider-result callback failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Provider-supplied synchronization remains authoritative. Plain
            // lyrics are published immediately and synchronization is detached
            // from the track lifetime so it can finish and populate the disk
            // cache even after the user changes tracks.
            if (result != null && !result.IsSynced && !string.IsNullOrWhiteSpace(mediaPath))
            {
                Dbg.Log($"[LYRICS] plain provider text received from '{result.Provider}' ({result.Text.Length} chars); queued background synchronization for '{mediaPath}'", Dbg.LogLevel.Info);
                QueueBackgroundSynchronization(
                    key,
                    mediaPath,
                    normalizedQuery.DurationSeconds,
                    result,
                    onSynchronizationResult);
            }
            else if (result != null)
                Dbg.Log($"[LYRICS] provider result: provider='{result.Provider}', synced={result.IsSynced}, lines={result.Lines.Count}, textChars={result.Text.Length}", Dbg.LogLevel.Info);

            return result;
        }

        private void QueueBackgroundSynchronization(
            string key,
            string mediaPath,
            double? durationSeconds,
            LyricsResult providerResult,
            Action<LyricsResult>? observer)
        {
            Task job;
            lock (_jobGate)
            {
                if (observer != null)
                    _backgroundSyncObservers.GetOrAdd(key, _ => new ConcurrentBag<Action<LyricsResult>>()).Add(observer);
                if (_backgroundSyncJobs.ContainsKey(key)) return;
                Dbg.Log($"[LYRICS] background synchronization queued: key='{key}', audio='{mediaPath}'", Dbg.LogLevel.Info);
                job = Task.Run(
                    () => SynchronizeProviderResultInBackgroundAsync(key, mediaPath, durationSeconds, providerResult),
                    _backgroundSyncCts.Token);
                _backgroundSyncJobs[key] = job;
            }

            _ = job.ContinueWith(
                _ => _backgroundSyncJobs.TryRemove(new KeyValuePair<string, Task>(key, job)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task SynchronizeProviderResultInBackgroundAsync(
            string key,
            string mediaPath,
            double? durationSeconds,
            LyricsResult providerResult)
        {
            Dbg.Log($"[LYRICS] background synchronization started: audio='{mediaPath}', provider='{providerResult.Provider}'", Dbg.LogLevel.Info);
            LyricsResult finalResult = providerResult;
            bool acquired = false;
            try
            {
                await _recognitionSlot.WaitAsync(_backgroundSyncCts.Token).ConfigureAwait(false);
                acquired = true;
                var synchronization = await SynchronizeWhenVideoIsIdleAsync(mediaPath, durationSeconds, providerResult).ConfigureAwait(false);

                if (synchronization.IsReliable)
                {
                    int timedWords = synchronization.Lines.Sum(line => line.Words?.Count ?? 0);
                    Dbg.Log($"[LYRICS] background synchronization accepted: backend='{synchronization.Diagnostics.Backend}', confidence={synchronization.Confidence:0.000}, lines={synchronization.Lines.Count}, timedWords={timedWords}, cacheHit={synchronization.Diagnostics.CacheHit}", Dbg.LogLevel.Info);
                    finalResult = providerResult with
                    {
                        IsSynced = true,
                        Lines = synchronization.Lines,
                        Note = $"{ProviderNote(providerResult.Note)}; auto-synchronized ({synchronization.Confidence:P0})",
                        HasReliableSynchronization = true,
                        SynchronizationConfidence = synchronization.Confidence,
                        SynchronizationDiagnostics = synchronization.Diagnostics,
                        SynchronizationRevision = 3
                    };
                }
                else
                {
                    string reason = synchronization.FailureReason ?? synchronization.Diagnostics.FailureReason ?? "unknown";
                    Dbg.Warn($"[LYRICS] background synchronization rejected: backend='{synchronization.Diagnostics.Backend}', reason='{reason}', confidence={synchronization.Confidence:0.000}, recognized={synchronization.Diagnostics.RecognizedWordCount}, matched={synchronization.Diagnostics.MatchedWordCount}, processingMs={synchronization.Diagnostics.ProcessingMilliseconds:0}");
                    finalResult = providerResult with
                    {
                        Note = $"{ProviderNote(providerResult.Note)}; auto-sync rejected: {reason}",
                        SynchronizationConfidence = synchronization.Confidence,
                        SynchronizationDiagnostics = synchronization.Diagnostics,
                        SynchronizationRevision = 3
                    };
                }
            }
            catch (OperationCanceledException) when (_backgroundSyncCts.IsCancellationRequested)
            {
                Dbg.Log($"[LYRICS] background synchronization canceled: audio='{mediaPath}'", Dbg.LogLevel.Info);
                return;
            }
            catch (Exception ex)
            {
                Dbg.Error($"[LYRICS] background synchronization failed: audio='{mediaPath}', {ex.GetType().Name}: {ex.Message}");
                finalResult = providerResult with
                {
                    Note = $"{ProviderNote(providerResult.Note)}; auto-sync failed: {ex.GetType().Name}: {ex.Message}"
                };
            }
            finally
            {
                if (acquired)
                {
                    Volatile.Write(ref _synchronizingPath, null);
                    _recognitionSlot.Release();
                }
            }

            ConcurrentBag<Action<LyricsResult>>? observers;
            lock (_jobGate)
            {
                _memory[key] = Task.FromResult<LyricsResult?>(finalResult);
                _backgroundSyncObservers.TryRemove(key, out observers);
            }
            StoreLyrics(mediaPath, finalResult, finalResult.IsSynced ? "synced" : "failed");
            if (observers != null)
            {
                foreach (var observer in observers)
                {
                    try { observer(finalResult); }
                    catch (Exception ex) { Dbg.Warn($"[LYRICS] background synchronization callback failed: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
        }

        private async Task<LyricsResult?> FindLyricsAsync(LyricsQuery query, CancellationToken ct)
        {
            var providers = new (string Name, Func<LyricsQuery, CancellationToken, Task<LyricsResult?>> Query)[]
            {
                ("LRCLIB", TryLrcLibAsync),
                ("Genius", TryGeniusAsync),
                ("lyrics.ovh", TryLyricsOvhAsync)
            };
            foreach (var provider in providers)
            {
                Dbg.Log($"[LYRICS] provider attempt: {provider.Name}", Dbg.LogLevel.Info);
                try
                {
                    var result = await provider.Query(query, ct).ConfigureAwait(false);
                    if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        Dbg.Log($"[LYRICS] provider success: {result.Provider}, synchronized={result.IsSynced}, lines={result.Lines.Count}, textChars={result.Text.Length}", Dbg.LogLevel.Info);
                        return result;
                    }
                    Dbg.Log($"[LYRICS] provider returned no usable text: {provider.Name}", Dbg.LogLevel.Info);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Dbg.Warn($"[LYRICS] provider failed: {provider.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return null;
        }

        // Cartelle di contenuto da saltare (non sono né artista né album).
        private static readonly Regex GenericFolder = new(
            @"^(?:un?rel\w*|inediti|albums?|singles?|songs?|brani|tracks?|misc|various|various artists|va|unknown|sconosciuto|" +
            @"new folder|nuova cartella|(?:cd|disc|disk|disco)\s*\d+|flac|mp3|aac|wav|lossless|hi-?res|\d{4}|" +
            @"soundtracks?|ost|bonus|demos?|live|covers?|remix(?:es)?|leaks?|extras?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Cartelle di sistema/radice: sopra di loro non c'è più un artista.
        private static readonly Regex RootFolder = new(
            @"^(?:downloads?|scaricati|music|musica|my music|la mia musica|desktop|documents?|documenti|users?|utenti|" +
            @"onedrive|dropbox|media|libreria|library|audio)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string? InferArtistFromPath(string mediaPath, ref string title)
        {
            try
            {
                // "Artista - Titolo.mp3"
                string file = Path.GetFileNameWithoutExtension(mediaPath);
                var dash = Regex.Match(file, @"^\s*(?:\d{1,3}[\s._-]+)?(?<artist>[^-]{2,60}?)\s+[-–]\s+(?<title>.+)$");
                if (dash.Success && !Regex.IsMatch(dash.Groups["artist"].Value, @"^\d+$"))
                {
                    string candidateTitle = MusicTextIdentity.Title(dash.Groups["title"].Value, filename: true);
                    if (string.Equals(MusicTextIdentity.Title(file, filename: true), title, StringComparison.OrdinalIgnoreCase) && candidateTitle.Length > 0)
                        title = candidateTitle;
                    return MusicTextIdentity.Artist(dash.Groups["artist"].Value);
                }
                // Artista\Album\brano: tra le cartelle significative sopra il file vince la più
                // alta (l'artista); ci si ferma a radici come Downloads o Musica.
                var candidates = new List<string>();
                var dir = new DirectoryInfo(Path.GetDirectoryName(mediaPath) ?? string.Empty);
                for (int depth = 0; dir != null && dir.Parent != null && depth < 4 && candidates.Count < 2; depth++, dir = dir.Parent)
                {
                    string name = MusicTextIdentity.Artist(dir.Name);
                    if (RootFolder.IsMatch(name)) break;
                    if (name.Length < 2 || GenericFolder.IsMatch(name)) continue;
                    // "Artista - Album (2020)" contiene già l'artista.
                    var albumFolder = Regex.Match(name, @"^(?<artist>.+?)\s+[-–]\s+.+$");
                    if (albumFolder.Success) return albumFolder.Groups["artist"].Value.Trim();
                    candidates.Add(name);
                }
                if (candidates.Count > 0) return candidates[^1];
            }
            catch { }
            return null;
        }

        private static LyricsQuery BuildQuery(string? mediaPath)
        {
            string title = string.Empty;
            string artist = string.Empty;
            string album = string.Empty;
            double? duration = null;

            if (!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath))
            {
                try
                {
                    var tags = MediaProbe.ReadAudioTags(mediaPath);
                    title = tags.Title ?? string.Empty;
                    artist = FirstNonEmpty(tags.Artist, tags.AlbumArtist);
                    album = tags.Album ?? string.Empty;
                    Dbg.Log($"[LYRICS] file metadata: title='{title}', artist='{artist}', album='{album}'", Dbg.LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Dbg.Warn($"[LYRICS] metadata read failed for '{mediaPath}': {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    var info = MediaProbe.Probe(mediaPath);
                    if (info.Duration > 0)
                        duration = info.Duration;
                    Dbg.Log($"[LYRICS] probed duration: {(duration?.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}s", Dbg.LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Dbg.Warn($"[LYRICS] duration probe failed for '{mediaPath}': {ex.GetType().Name}: {ex.Message}");
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    try
                    {
                        title = MusicTextIdentity.Title(Path.GetFileNameWithoutExtension(mediaPath), filename: true);
                        Dbg.Log($"[LYRICS] metadata title fallback from filename: '{title}'", Dbg.LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn($"[LYRICS] filename title fallback failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            // File senza tag (es. Downloads\AURORA\UNRELASED\Awakening.mp3): senza artista la
            // ricerca per solo titolo sceglieva un omonimo ("Awakening" di Eloy).
            if (string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(mediaPath))
            {
                string? inferred = InferArtistFromPath(mediaPath, ref title);
                if (!string.IsNullOrWhiteSpace(inferred))
                {
                    artist = inferred;
                    Dbg.Log($"[LYRICS] artist inferred from path: '{artist}'", Dbg.LogLevel.Info);
                }
            }

            string rawTitle = title;
            string rawArtist = artist;
            string rawAlbum = album;
            title = CleanTrackTitle(title);
            artist = CleanArtist(artist);
            album = CleanTrackTitle(album);

            if (!string.Equals(rawTitle, title, StringComparison.Ordinal) ||
                !string.Equals(rawArtist, artist, StringComparison.Ordinal) ||
                !string.Equals(rawAlbum, album, StringComparison.Ordinal))
            {
                Dbg.Log($"[LYRICS] cleaned metadata: title='{title}', artist='{artist}', album='{album}'", Dbg.LogLevel.Info);
            }

            return new LyricsQuery(title, artist, album, duration);
        }

        private async Task<LyricsQuery> NormalizeWithItunesAsync(LyricsQuery query, CancellationToken ct)
        {
            try
            {
                var normalized = await TryNormalizeWithItunesAsync(query, ct).ConfigureAwait(false);
                if (normalized == null)
                {
                    Dbg.Log("[LYRICS] iTunes normalization: no confident match; retaining file metadata.", Dbg.LogLevel.Info);
                    return query;
                }

                bool changed = !string.Equals(query.Title, normalized.Title, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(query.Artist, normalized.Artist, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(query.Album, normalized.Album, StringComparison.OrdinalIgnoreCase) ||
                    query.DurationSeconds != normalized.DurationSeconds;
                Dbg.Log(changed
                    ? $"[LYRICS] iTunes normalization changed query: '{query.Title}'/'{query.Artist}'/{query.DurationSeconds:0.000}s -> '{normalized.Title}'/'{normalized.Artist}'/{normalized.DurationSeconds:0.000}s"
                    : "[LYRICS] iTunes normalization matched existing query without changes.", Dbg.LogLevel.Info);
                return normalized;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Dbg.Warn($"[LYRICS] iTunes normalization failed: {ex.GetType().Name}: {ex.Message}; retaining file metadata.");
                return query;
            }
        }

        private async Task<LyricsQuery?> TryNormalizeWithItunesAsync(LyricsQuery query, CancellationToken ct)
        {
            string? bestTitle = null;
            string? bestArtist = null;
            string? bestAlbum = null;
            double? bestDuration = null;
            int bestScore = int.MinValue;

            foreach (var country in new[] { "IT", "US" })
            {
                foreach (var term in BuildItunesSearchTerms(query))
                {
                    string url = "https://itunes.apple.com/search"
                        + "?media=music&entity=song&limit=8"
                        + "&country=" + Uri.EscapeDataString(country)
                        + "&term=" + Uri.EscapeDataString(term);

                    using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    if (!doc.RootElement.TryGetProperty("results", out var results) ||
                        results.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var item in results.EnumerateArray())
                    {
                        string title = CleanTrackTitle(ReadString(item, "trackName"));
                        string artist = CleanArtist(ReadString(item, "artistName"));
                        string album = CleanTrackTitle(ReadString(item, "collectionName"));
                        if (string.IsNullOrWhiteSpace(title))
                            continue;

                        double? duration = ReadDurationSeconds(item, "trackTimeMillis");
                        int score = ScoreItunesItem(title, artist, album, duration, query);
                        bool titleMatches = string.IsNullOrWhiteSpace(query.Artist)
                            ? StrongTitleMatch(title, query.Title)
                            : Similar(title, query.Title);
                        if (titleMatches && (string.IsNullOrWhiteSpace(query.Artist) || Similar(artist, query.Artist)) && score > bestScore)
                        {
                            bestScore = score;
                            bestTitle = title;
                            bestArtist = artist;
                            bestAlbum = album;
                            bestDuration = duration;
                        }
                    }
                }
            }

            if (bestScore < 5 || string.IsNullOrWhiteSpace(bestTitle))
                return null;

            return new LyricsQuery(
                bestTitle,
                string.IsNullOrWhiteSpace(bestArtist) ? query.Artist : bestArtist,
                string.IsNullOrWhiteSpace(bestAlbum) ? query.Album : bestAlbum,
                query.DurationSeconds ?? bestDuration);
        }

        private static IEnumerable<string> BuildItunesSearchTerms(LyricsQuery query)
        {
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in new[]
            {
                string.Join(" ", new[] { query.Artist, query.Title }.Where(s => !string.IsNullOrWhiteSpace(s))),
                string.Join(" ", new[] { query.Artist, query.Album, query.Title }.Where(s => !string.IsNullOrWhiteSpace(s))),
                query.Title
            })
            {
                string normalized = Regex.Replace(term ?? string.Empty, @"\s+", " ").Trim();
                if (normalized.Length > 0 && yielded.Add(normalized))
                    yield return normalized;
            }
        }

        private static int ScoreItunesItem(string title, string artist, string album, double? duration, LyricsQuery query)
        {
            int score = 0;
            if (Similar(title, query.Title)) score += 7;
            score += CountTokenMatches(query.Title, title) * 2;

            if (!string.IsNullOrWhiteSpace(query.Artist))
            {
                if (Similar(artist, query.Artist)) score += 5;
                score += CountTokenMatches(query.Artist, artist);
            }

            if (!string.IsNullOrWhiteSpace(query.Album))
            {
                if (Similar(album, query.Album)) score += 2;
                score += CountTokenMatches(query.Album, album);
            }

            if (query.DurationSeconds is > 0 && duration is > 0)
            {
                double delta = Math.Abs(duration.Value - query.DurationSeconds.Value);
                if (delta <= 2.5) score += 4;
                else if (delta <= 7.0) score += 2;
                else if (delta > 25.0) score -= 2;
            }

            string joined = $"{title} {artist} {album}";
            if (Regex.IsMatch(joined, @"\b(karaoke|tribute|cover|instrumental)\b", RegexOptions.IgnoreCase))
                score -= 3;

            return score;
        }

        private async Task<LyricsResult?> TryLrcLibAsync(LyricsQuery query, CancellationToken ct)
        {
            var args = new List<string>
            {
                "track_name=" + Uri.EscapeDataString(query.Title)
            };
            if (!string.IsNullOrWhiteSpace(query.Artist))
                args.Add("artist_name=" + Uri.EscapeDataString(query.Artist));
            if (!string.IsNullOrWhiteSpace(query.Album))
                args.Add("album_name=" + Uri.EscapeDataString(query.Album));
            if (query.DurationSeconds is > 0)
                args.Add("duration=" + Uri.EscapeDataString(Math.Round(query.DurationSeconds.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)));

            string url = "https://lrclib.net/api/search?" + string.Join("&", args);
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return null;
            res.EnsureSuccessStatusCode();

            string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<LrcLibItem>>(json, JsonOptions) ?? new List<LrcLibItem>();
            var best = items
                .Where(x => (!string.IsNullOrWhiteSpace(x.SyncedLyrics) || !string.IsNullOrWhiteSpace(x.PlainLyrics)) &&
                    (string.IsNullOrWhiteSpace(query.Artist) || Similar(x.ArtistName, query.Artist)))
                .OrderByDescending(x => ScoreLrcLibItem(x, query))
                .FirstOrDefault();

            if (best == null)
                return null;

            bool titleMatches = string.IsNullOrWhiteSpace(query.Artist)
                ? StrongTitleMatch(FirstNonEmpty(best.TrackName, best.Name), query.Title)
                : Similar(FirstNonEmpty(best.TrackName, best.Name), query.Title);
            if (ScoreLrcLibItem(best, query) < 6 || !titleMatches) return null;
            bool durationMatches = query.DurationSeconds is not > 0 || best.Duration is not > 0 || Math.Abs(query.DurationSeconds.Value - best.Duration.Value) <= 4;
            bool hasSyncedSource = !string.IsNullOrWhiteSpace(best.SyncedLyrics) && durationMatches;
            string? text = hasSyncedSource ? best.SyncedLyrics : best.PlainLyrics;
            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(best.SyncedLyrics))
                text = string.Join("\n", ParseSyncedLyrics(best.SyncedLyrics).Select(line => line.Text));
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var syncedLines = hasSyncedSource
                ? ParseSyncedLyrics(text)
                : Array.Empty<LyricsLine>();

            return new LyricsResult(
                "LRCLIB",
                text.Trim(),
                syncedLines.Count > 0,
                $"{FirstNonEmpty(best.TrackName, best.Name, query.Title)} - {FirstNonEmpty(best.ArtistName, query.Artist, "Artista sconosciuto")}",
                syncedLines,
                HasReliableSynchronization: syncedLines.Count > 0);
        }

        private static int ScoreLrcLibItem(LrcLibItem item, LyricsQuery query)
        {
            int score = 0;
            if (Similar(FirstNonEmpty(item.TrackName, item.Name), query.Title)) score += 6;
            if (!string.IsNullOrWhiteSpace(query.Artist) && Similar(item.ArtistName, query.Artist)) score += 5;
            if (!string.IsNullOrWhiteSpace(query.Album) && Similar(item.AlbumName, query.Album)) score += 3;
            if (query.DurationSeconds is > 0 && item.Duration is > 0)
            {
                double delta = Math.Abs(item.Duration.Value - query.DurationSeconds.Value);
                if (delta <= 2.5) score += 4;
                else if (delta <= 7.0) score += 2;
            }
            if (!string.IsNullOrWhiteSpace(item.SyncedLyrics)) score += 2;
            return score;
        }

        private async Task<LyricsResult?> TryLyricsOvhAsync(LyricsQuery query, CancellationToken ct)
        {
            string? artist = query.Artist;
            string title = query.Title;

            if (string.IsNullOrWhiteSpace(artist))
            {
                string suggestUrl = $"https://api.lyrics.ovh/suggest/{Uri.EscapeDataString(title)}";
                using var suggestRes = await _http.GetAsync(suggestUrl, ct).ConfigureAwait(false);
                if (!suggestRes.IsSuccessStatusCode)
                    return null;

                string suggestJson = await suggestRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var suggest = JsonSerializer.Deserialize<LyricsOvhSuggestResponse>(suggestJson, JsonOptions);
                var first = suggest?.Data?.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x.Title) &&
                    !string.IsNullOrWhiteSpace(x.Artist?.Name) &&
                    StrongTitleMatch(x.Title, query.Title));
                if (first == null)
                    return null;

                title = first.Title!;
                artist = first.Artist!.Name!;
            }

            string url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist!)}/{Uri.EscapeDataString(title)}";
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return null;
            res.EnsureSuccessStatusCode();

            string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<LyricsOvhResponse>(json, JsonOptions);
            if (string.IsNullOrWhiteSpace(payload?.Lyrics))
                return null;

            return new LyricsResult("lyrics.ovh", payload.Lyrics.Trim(), false, $"{title} - {artist}", Array.Empty<LyricsLine>());
        }

        private static string? FindLyricsBrowseId(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("browseEndpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.Object &&
                    endpoint.TryGetProperty("browseId", out var browseIdElement))
                {
                    string? browseId = browseIdElement.GetString();
                    string raw = endpoint.GetRawText();
                    if (!string.IsNullOrWhiteSpace(browseId) &&
                        (raw.Contains("MUSIC_PAGE_TYPE_TRACK_LYRICS", StringComparison.OrdinalIgnoreCase) ||
                         browseId.StartsWith("MPLYt", StringComparison.OrdinalIgnoreCase) ||
                         browseId.Contains("lyrics", StringComparison.OrdinalIgnoreCase)))
                    {
                        return browseId;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    string? found = FindLyricsBrowseId(property.Value);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    string? found = FindLyricsBrowseId(item);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            return null;
        }

        private static string? ExtractLikelyLyricsText(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "musicDescriptionShelfRenderer", "descriptionShelfRenderer" })
                {
                    if (element.TryGetProperty(key, out var shelf) && TryExtractDescriptionText(shelf, out var shelfText))
                        return CleanYouTubeLyrics(shelfText);
                }

                if (element.TryGetProperty("description", out var description) && TryExtractRunsText(description, out var textFromDescription))
                {
                    string cleaned = CleanYouTubeLyrics(textFromDescription);
                    if (LooksLikeLyrics(cleaned))
                        return cleaned;
                }

                foreach (var property in element.EnumerateObject())
                {
                    string? found = ExtractLikelyLyricsText(property.Value);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    string? found = ExtractLikelyLyricsText(item);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            return null;
        }

        private static bool TryExtractDescriptionText(JsonElement shelf, out string text)
        {
            text = string.Empty;
            return shelf.ValueKind == JsonValueKind.Object &&
                   shelf.TryGetProperty("description", out var description) &&
                   TryExtractRunsText(description, out text) &&
                   LooksLikeLyrics(text);
        }

        private static bool TryExtractRunsText(JsonElement element, out string text)
        {
            text = string.Empty;
            if (element.ValueKind == JsonValueKind.String)
            {
                text = element.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(text);
            }
            if (element.ValueKind != JsonValueKind.Object)
                return false;
            if (element.TryGetProperty("simpleText", out var simpleText))
            {
                text = simpleText.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(text);
            }
            if (element.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var run in runs.EnumerateArray())
                    if (run.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
                text = sb.ToString();
                return !string.IsNullOrWhiteSpace(text);
            }
            return false;
        }

        private static string? FindFirstStringProperty(JsonElement element, string propertyName, Func<string, bool>? predicate = null)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    string? s = value.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && (predicate == null || predicate(s)))
                        return s;
                }
                foreach (var property in element.EnumerateObject())
                {
                    string? found = FindFirstStringProperty(property.Value, propertyName, predicate);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    string? found = FindFirstStringProperty(item, propertyName, predicate);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            return null;
        }

        private static string CleanYouTubeLyrics(string text)
            => (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\u00a0", " ").Trim();

        private static bool LooksLikeLyrics(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string cleaned = text.Trim();
            return cleaned.Length > 40 && (cleaned.Contains('\n') || cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 12);
        }

        // [mm:ss], [mm:ss.xx], [mm:ss.xxx] and the common [mm:ss:xx] / [mm:ss,xx] variants.
        private static readonly Regex LrcLineStamp = new(@"\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:,](?<f>\d{1,3}))?\]", RegexOptions.Compiled);
        // Enhanced LRC word stamps: <mm:ss.xx>word
        private static readonly Regex LrcWordStamp = new(@"<(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:,](?<f>\d{1,3}))?>", RegexOptions.Compiled);

        private static double? ParseLrcStamp(Match match)
        {
            if (!int.TryParse(match.Groups["m"].Value, out int minutes) ||
                !int.TryParse(match.Groups["s"].Value, out int seconds))
                return null;
            string frac = match.Groups["f"].Value;
            // "5" is tenths, "50" hundredths, "500" milliseconds: all are a decimal fraction.
            double fraction = frac.Length == 0 ? 0 : int.Parse(frac, System.Globalization.CultureInfo.InvariantCulture) / Math.Pow(10, frac.Length);
            return minutes * 60.0 + seconds + fraction;
        }

        private static IReadOnlyList<LyricsLine> ParseSyncedLyrics(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<LyricsLine>();

            var lines = new List<LyricsLine>();
            double offset = 0;
            var offsetMatch = Regex.Matches(text, @"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase).Cast<Match>().LastOrDefault();
            if (offsetMatch != null && double.TryParse(offsetMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)) offset = milliseconds / 1000;
            foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                // Line stamps are a prefix; a "[..]" inside the lyric text is not one.
                var stamps = new List<double>();
                int cursor = 0;
                for (Match match = LrcLineStamp.Match(line); match.Success && match.Index == cursor; match = match.NextMatch())
                {
                    if (ParseLrcStamp(match) is double stamp) stamps.Add(stamp);
                    cursor = match.Index + match.Length;
                    while (cursor < line.Length && line[cursor] == ' ') cursor++;
                }
                if (stamps.Count == 0)
                    continue;

                // Empty timed lines mark instrumental gaps; preserve them to clear the active lyric.
                string body = line.Substring(Math.Min(cursor, line.Length)).Trim();
                var (lyricText, words) = ParseEnhancedWords(body, offset);

                foreach (double stamp in stamps)
                {
                    double time = Math.Max(0, stamp - offset);
                    // Word stamps belong to the first occurrence only; repeated
                    // choruses ([00:10][01:10]) share the text but not the timing.
                    var lineWords = words != null && Math.Abs(stamp - stamps[0]) < 0.001 ? words : null;
                    lines.Add(new LyricsLine(time, lyricText, Words: lineWords));
                }
            }

            var ordered = lines.OrderBy(x => x.TimeSeconds).DistinctBy(x => (x.TimeSeconds, x.Text)).ToList();
            for (int i = 0; i < ordered.Count - 1; i++)
                ordered[i] = ordered[i] with { EndTimeSeconds = ordered[i + 1].TimeSeconds };
            return ordered;
        }

        private static (string Text, IReadOnlyList<LyricsWord>? Words) ParseEnhancedWords(string body, double offset)
        {
            var stamps = LrcWordStamp.Matches(body);
            if (stamps.Count == 0)
                return (body, null);

            var words = new List<LyricsWord>();
            for (int i = 0; i < stamps.Count; i++)
            {
                int from = stamps[i].Index + stamps[i].Length;
                int to = i + 1 < stamps.Count ? stamps[i + 1].Index : body.Length;
                string word = body.Substring(from, to - from).Trim();
                if (word.Length == 0 || ParseLrcStamp(stamps[i]) is not double start)
                    continue;
                // A trailing stamp closes the previous word; otherwise the next word does.
                double end = i + 1 < stamps.Count && ParseLrcStamp(stamps[i + 1]) is double next ? next : start + 0.6;
                words.Add(new LyricsWord(word, Math.Max(0, start - offset), Math.Max(0, Math.Max(end, start) - offset)));
            }
            string text = Regex.Replace(LrcWordStamp.Replace(body, " "), @"\s+", " ").Trim();
            return (text, words.Count > 0 ? words : null);
        }

        private static string CleanTrackTitle(string? value)
        {
            return MusicTextIdentity.Title(value);
        }

        private static string CleanArtist(string? value)
        {
            string s = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            s = Regex.Replace(s, @"\s+(feat|ft|featuring)\.?\s+.+$", " ", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static string FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

        private static bool Similar(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;
            a = NormalizeForCompare(a);
            b = NormalizeForCompare(b);
            return a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
        }

        private static bool StrongTitleMatch(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            static string Key(string value) => Regex.Replace(NormalizeForCompare(value), @"[^\p{L}\p{N}]+", " ").Trim();
            return string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeForCompare(string value)
            => Regex.Replace(value.ToLowerInvariant()
                    .Replace("feat.", "")
                    .Replace("featuring", "")
                    .Replace("ft.", "")
                    .Replace("remastered", "")
                    .Replace("remaster", "")
                    .Replace("official", "")
                    .Replace("lyrics", ""), @"\s+", " ").Trim();

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

        private static double? ReadDurationSeconds(JsonElement item, string name)
        {
            try
            {
                if (item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
                {
                    if (prop.TryGetInt64(out long ms) && ms > 0)
                        return ms / 1000.0;
                    if (prop.TryGetDouble(out double value) && value > 0)
                        return value / 1000.0;
                }
            }
            catch { }
            return null;
        }

        private static int CountTokenMatches(string? needle, string? haystack)
        {
            var haystackTokens = Tokenize(haystack).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (var token in Tokenize(needle))
            {
                if (haystackTokens.Contains(token))
                    count++;
            }
            return count;
        }

        private static IEnumerable<string> Tokenize(string? value)
            => Regex.Split((value ?? string.Empty).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+")
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12);

        public void Dispose()
        {
            // Do not wait for model download/inference here. Cancellation is
            // propagated to the child recognizer, allowing application exit to
            // remain fast while preserving completed cache files.
            try { _backgroundSyncCts.Cancel(); } catch { }
            try { _http.Dispose(); } catch { }
        }

        private sealed record LyricsQuery(string Title, string Artist, string Album, double? DurationSeconds);

        public sealed record LyricsWord(
            string Text,
            double StartSeconds,
            double EndSeconds,
            double Confidence = 1.0);

        public sealed record LyricsLine(
            double TimeSeconds,
            string Text,
            double? EndTimeSeconds = null,
            IReadOnlyList<LyricsWord>? Words = null,
            double Confidence = 1.0);

        public sealed record LyricsResult(
            string Provider,
            string Text,
            bool IsSynced,
            string Note,
            IReadOnlyList<LyricsLine> Lines,
            bool HasReliableSynchronization = false,
            double SynchronizationConfidence = 0,
            LyricsSyncDiagnostics? SynchronizationDiagnostics = null,
            int SynchronizationRevision = 0);

        private sealed class LrcLibItem
        {
            public int? Id { get; set; }
            public string? Name { get; set; }
            public string? TrackName { get; set; }
            public string? ArtistName { get; set; }
            public string? AlbumName { get; set; }
            public double? Duration { get; set; }
            public string? PlainLyrics { get; set; }
            public string? SyncedLyrics { get; set; }
        }

        private sealed class LyricsOvhResponse
        {
            public string? Lyrics { get; set; }
        }

        private sealed class LyricsOvhSuggestResponse
        {
            public List<LyricsOvhSuggestItem>? Data { get; set; }
        }

        private sealed class LyricsOvhSuggestItem
        {
            public string? Title { get; set; }
            public LyricsOvhArtist? Artist { get; set; }
        }

        private sealed class LyricsOvhArtist
        {
            public string? Name { get; set; }
        }
    }
}
