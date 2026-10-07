#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    public sealed partial class MusicLyricsService
    {
        public event Action? SynchronizationStatusChanged;
        private readonly SemaphoreSlim _recognitionSlot = new(1, 1);
        private readonly object _jobGate = new();
        private readonly Channel<string> _libraryQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentDictionary<string, byte> _libraryQueued = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, StoredLyrics> _storedLyrics = new(StringComparer.OrdinalIgnoreCase);
        private string _lyricsStore = string.Empty;
        private string? _synchronizingPath;
        private int _recognitionPercent;
        private volatile bool _videoPlaybackActive;
        private TaskCompletionSource<bool>? _recognitionResume;
        private CancellationTokenSource? _recognitionRun;

        /// <summary>True while a film is opening or playing: background file probes wait, the disk is for the video.</summary>
        public static volatile bool VideoPlaybackBusy;

        public void SetVideoPlaybackActive(bool active)
        {
            VideoPlaybackBusy = active;
            CancellationTokenSource? running = null;
            TaskCompletionSource<bool>? resume = null;
            lock (_jobGate)
            {
                if (_videoPlaybackActive == active) return;
                _videoPlaybackActive = active;
                if (active)
                {
                    _recognitionResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    running = _recognitionRun;
                }
                else { resume = _recognitionResume; _recognitionResume = null; }
            }
            // Cancellation terminates our recognizer child process and releases its GPU.
            try { running?.Cancel(); } catch (ObjectDisposedException) { }
            resume?.TrySetResult(true);
            NotifySynchronizationStatus();
        }

        private async Task<LyricsSynchronizationResult> SynchronizeWhenVideoIsIdleAsync(
            string mediaPath, double? durationSeconds, LyricsResult providerResult)
        {
            while (true)
            {
                _backgroundSyncCts.Token.ThrowIfCancellationRequested();
                Task? wait = null;
                CancellationTokenSource? run = null;
                lock (_jobGate)
                {
                    if (_videoPlaybackActive) wait = _recognitionResume!.Task;
                    else _recognitionRun = run = CancellationTokenSource.CreateLinkedTokenSource(_backgroundSyncCts.Token);
                }
                if (wait != null) { await wait.WaitAsync(_backgroundSyncCts.Token).ConfigureAwait(false); continue; }
                using (run!)
                {
                    try
                    {
                        Volatile.Write(ref _synchronizingPath, mediaPath);
                        ReportRecognitionProgress(0);
                        StoreLyrics(mediaPath, providerResult, "running");
                        var result = await _lyricsSynchronizer.SynchronizeAsync(new LyricsSynchronizationRequest(
                            mediaPath, providerResult.Text, durationSeconds, providerResult.Provider,
                            fraction => ReportRecognitionProgress(fraction)), run!.Token).ConfigureAwait(false);
                        run.Token.ThrowIfCancellationRequested();
                        return result;
                    }
                    catch (OperationCanceledException) when (!_backgroundSyncCts.IsCancellationRequested && run!.IsCancellationRequested)
                    {
                        // Keep the job and its observers; retry after the film exits.
                        StoreLyrics(mediaPath, providerResult, "queued");
                    }
                    finally
                    {
                        Volatile.Write(ref _synchronizingPath, null);
                        lock (_jobGate) { if (ReferenceEquals(_recognitionRun, run)) _recognitionRun = null; }
                    }
                }
            }
        }

        private async Task WaitForVideoIdleAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task? resume;
                lock (_jobGate) resume = _videoPlaybackActive ? _recognitionResume!.Task : null;
                if (resume == null) return;
                await resume.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        private void ReportRecognitionProgress(double fraction)
        {
            int percent = (int)Math.Clamp(fraction * 100, 0, 100);
            if (Interlocked.Exchange(ref _recognitionPercent, percent) != percent) NotifySynchronizationStatus();
        }
        private sealed record StoredLyrics(string Path, string Identity, LyricsResult? Result, string State, DateTime UpdatedUtc);

        // "Titolo - Artista" del provider, senza gli stati di sincronizzazione accodati.
        internal static string ProviderNote(string? note) => (note ?? string.Empty).Split(';')[0].Trim();

        private static string ArtistKey(string? value)
        {
            var builder = new StringBuilder();
            foreach (char c in (value ?? string.Empty).Normalize(NormalizationForm.FormD))
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToUpperInvariant(c));
            return builder.ToString();
        }

        /// <summary>
        /// Un testo salvato per un brano senza tag poteva appartenere a un omonimo di un altro
        /// artista (es. "Awakening" di Eloy nella cartella di AURORA). Se l'artista atteso è noto
        /// e quello del provider non gli somiglia, la voce va rifatta.
        /// </summary>
        private static bool HasMismatchedArtist(StoredLyrics stored)
        {
            try
            {
                string note = ProviderNote(stored.Result?.Note);
                int dash = note.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dash < 0) return false;
                string provided = ArtistKey(note[(dash + 3)..]);
                if (provided.Length == 0) return false;
                var tags = MediaProbe.ReadAudioTags(stored.Path);
                string expectedRaw = FirstNonEmpty(tags.Artist, tags.AlbumArtist);
                if (string.IsNullOrWhiteSpace(expectedRaw))
                {
                    string title = MusicTextIdentity.Title(System.IO.Path.GetFileNameWithoutExtension(stored.Path), filename: true);
                    expectedRaw = InferArtistFromPath(stored.Path, ref title) ?? string.Empty;
                }
                string expected = ArtistKey(expectedRaw);
                if (expected.Length < 2) return false;
                return !provided.Contains(expected, StringComparison.Ordinal) && !expected.Contains(provided, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static StoredLyrics CompactNote(StoredLyrics stored)
        {
            if (stored.Result is not LyricsResult result || string.IsNullOrEmpty(result.Note) || result.Note.Count(c => c == ';') < 2) return stored;
            var parts = result.Note.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            string compact = parts.Count <= 2 ? result.Note : parts[0] + "; " + parts[^1];
            return stored with { Result = result with { Note = compact } };
        }

        private static StoredLyrics UpgradeSynchronization(StoredLyrics stored)
        {
            stored = CompactNote(stored);
            if (stored.Result is { IsSynced: true, SynchronizationRevision: < 3 } result &&
                (result.SynchronizationDiagnostics != null || result.Note.Contains("auto-synchronized", StringComparison.Ordinal)))
                return stored with { State = "queued", Result = result with { IsSynced = false, Lines = Array.Empty<LyricsLine>(), HasReliableSynchronization = false } };
            return stored;
        }

        /// <summary>
        /// Allineamento gia' tentato e scartato con l'algoritmo attuale: il riconoscimento ha
        /// ascoltato il brano e le parole non coincidono abbastanza. Stesso audio e stesso testo
        /// danno lo stesso esito, quindi non si riprova (prima ripartiva a ogni avvio e a ogni
        /// ingresso in Musica, per ogni brano).
        /// </summary>
        private static bool IsFinalRejection(LyricsResult? result) =>
            result is { IsSynced: false, SynchronizationRevision: >= 3, SynchronizationDiagnostics: { RecognizedWordCount: > 0 } };

        // Un testo non trovato o un errore si ritentano dopo un po', non a ogni occasione.
        private static readonly TimeSpan RetryOnMusicEntry = TimeSpan.FromDays(1), RetryInBackground = TimeSpan.FromDays(7);

        private static string MediaIdentity(string path)
        {
            var file = new FileInfo(path);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                file.FullName.ToUpperInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks)));
        }

        private void StartLibraryWorker(string? directory)
        {
            _lyricsStore = directory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "LyricsLibrary");
            _ = Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(_lyricsStore);
                    foreach (string file in Directory.EnumerateFiles(_lyricsStore, "*.json"))
                    {
                        try
                        {
                            var stored = JsonSerializer.Deserialize<StoredLyrics>(await File.ReadAllTextAsync(file, _backgroundSyncCts.Token).ConfigureAwait(false), JsonOptions);
                            if (stored == null || !File.Exists(stored.Path) || MediaIdentity(stored.Path) != stored.Identity) continue;
                            if (HasMismatchedArtist(stored))
                            {
                                Dbg.Log($"[LYRICS] stored lyrics discarded (other artist: '{ProviderNote(stored.Result?.Note)}') for '{stored.Path}'", Dbg.LogLevel.Info);
                                try { File.Delete(file); } catch { }
                                continue;
                            }
                            stored = UpgradeSynchronization(stored);
                            // Rimasto "in coda" da una sessione chiusa a meta', ma l'esito c'e' gia'.
                            if (stored.State is "queued" or "running" && IsFinalRejection(stored.Result))
                                stored = stored with { State = "failed" };
                            _storedLyrics.TryAdd(stored.Path, stored);
                            if (stored.State is "queued" or "running") QueueLibrary(new[] { stored.Path });
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                    NotifySynchronizationStatus();
                    await foreach (string path in _libraryQueue.Reader.ReadAllAsync(_backgroundSyncCts.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            // Library scans also probe audio files, contact providers
                            // and write caches. Let the video keep disk/CPU priority.
                            await WaitForVideoIdleAsync(_backgroundSyncCts.Token).ConfigureAwait(false);
                            var result = await FindForMediaAsync(path, _backgroundSyncCts.Token).ConfigureAwait(false);
                            // Provider lookups proceed while recognition processes audio.
                            // Long tracks must not hold up already synchronized lyrics.
                            if (result == null) StoreLyrics(path, null, "unavailable");
                        }
                        catch (OperationCanceledException) when (_backgroundSyncCts.IsCancellationRequested) { break; }
                        catch (Exception ex) { Dbg.Warn("[LYRICS] library lookup failed: " + ex.Message); StoreLyrics(path, null, "failed"); }
                        finally { _libraryQueued.TryRemove(path, out _); NotifySynchronizationStatus(); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Dbg.Warn("[LYRICS] library worker: " + ex.Message); }
            });
        }

        public void QueueLibrary(IEnumerable<string> paths, bool retryFailed = false)
        {
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_backgroundSyncCts.IsCancellationRequested || !File.Exists(path)) continue;
                var stored = ReadStoredLyrics(path);
                if (stored?.Result?.IsSynced == true) continue;
                if (IsFinalRejection(stored?.Result)) continue;
                if (stored?.State is ("failed" or "unavailable") &&
                    DateTime.UtcNow - stored.UpdatedUtc < (retryFailed ? RetryOnMusicEntry : RetryInBackground)) continue;
                string key = MediaIdentity(path);
                if (stored?.State is "queued" or "running" && _backgroundSyncJobs.ContainsKey(key)) continue;
                if (!_libraryQueued.TryAdd(path, 0)) continue;
                // A deliberate entry into Music retries a previous provider or
                // recognition failure, even when its in-memory lookup was null.
                if (retryFailed && stored?.State is ("failed" or "unavailable"))
                    _memory.TryRemove(key, out _);
                StoreLyrics(path, stored?.Result, "queued");
                _libraryQueue.Writer.TryWrite(path);
            }
            NotifySynchronizationStatus();
        }

        private StoredLyrics? ReadStoredLyrics(string path)
        {
            try
            {
                string key = MediaIdentity(path);
                if (_storedLyrics.TryGetValue(path, out var item) && item.Identity == key) return item;
                string file = System.IO.Path.Combine(_lyricsStore, key + ".json");
                if (!File.Exists(file)) return null;
                item = JsonSerializer.Deserialize<StoredLyrics>(File.ReadAllText(file), JsonOptions);
                if (item == null || item.Identity != key) return null;
                if (HasMismatchedArtist(item))
                {
                    Dbg.Log($"[LYRICS] stored lyrics discarded (other artist: '{ProviderNote(item.Result?.Note)}') for '{path}'", Dbg.LogLevel.Info);
                    try { File.Delete(file); } catch { }
                    return null;
                }
                item = UpgradeSynchronization(item);
                _storedLyrics[path] = item;
                return item;
            }
            catch { return null; }
        }

        private void StoreLyrics(string path, LyricsResult? result, string state)
        {
            try
            {
                lock (_jobGate)
                {
                    string key = MediaIdentity(path);
                    // A delayed provider lookup or library enqueue must never downgrade
                    // an alignment that has already completed for this recording.
                    if (_storedLyrics.TryGetValue(path, out var previous) && previous.Identity == key)
                    {
                        if (previous.Result?.IsSynced == true && result?.IsSynced != true) return;
                        if (previous.Result != null && result == null) return;
                    }
                    var stored = new StoredLyrics(path, key, result, state, DateTime.UtcNow);
                    _storedLyrics[path] = stored;
                    Directory.CreateDirectory(_lyricsStore);
                    string file = System.IO.Path.Combine(_lyricsStore, key + ".json"), temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions));
                    File.Move(temp, file, overwrite: true);
                }
            }
            catch (Exception ex) { Dbg.Warn("[LYRICS] cannot persist lyrics: " + ex.Message); }
            NotifySynchronizationStatus();
        }

        private void NotifySynchronizationStatus()
        {
            try { SynchronizationStatusChanged?.Invoke(); } catch { }
        }

        public string GetSynchronizationStatus(bool english = false)
        {
            int synced = _storedLyrics.Values.Count(x => x.Result?.IsSynced == true);
            int pending = _storedLyrics.Values.Count(x => x.State is "queued" or "running");
            int failed = _storedLyrics.Values.Count(x => x.State == "failed");
            if (_videoPlaybackActive && pending > 0)
                return english ? $"Lyrics · {pending} queued · paused during video playback" : $"Testi · {pending} in coda · sospesi durante il video";
            string? active = Volatile.Read(ref _synchronizingPath);
            if (active != null)
                return (english ? "Lyrics" : "Testi") + $" · {synced} " + (english ? "synced" : "sincronizzati") + $" · Sync {Volatile.Read(ref _recognitionPercent)}% · {Math.Max(0, pending - 1)} " + (english ? "queued" : "in coda");
            if (pending > 0) return (english ? "Lyrics · " : "Testi · ") + pending + (english ? " tracks queued" : " brani in coda");
            if (failed > 0) return (english ? $"Lyrics · {synced} synced · {failed} not aligned" : $"Testi · {synced} sincronizzati · {failed} non allineati");
            return english ? $"Lyrics · {synced} synced" : $"Testi · {synced} sincronizzati";
        }
    }
}
