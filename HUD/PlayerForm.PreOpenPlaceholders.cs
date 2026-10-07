#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm : Form
    {
        private sealed class PreOpenPlaceholderVisual
        {
            public string? ImagePath { get; set; }
            public string? TitleText { get; set; }
            public bool UseCover { get; set; }
            public bool ShowBranding { get; set; }
        }

        private static string NormalizeMediaPathForDisplay(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
                    return uri.LocalPath;
            }
            catch { }

            return path;
        }

        private string ResolveMetadataLookupKey(string? pathToOpen)
        {
            string normalizedPath = NormalizeMediaPathForDisplay(pathToOpen ?? string.Empty);
            string? hintedTitle = PlaybackTitleHints.GetTitle(pathToOpen);
            if (string.IsNullOrWhiteSpace(hintedTitle))
                hintedTitle = PlaybackTitleHints.GetTitle(normalizedPath);

            // Un suggerimento che e' solo il nome del file ("Film 2006.mkv") non e' una chiave:
            // creerebbe una voce senza percorso che non si risolve mai. Usa il percorso.
            if (!string.IsNullOrWhiteSpace(hintedTitle) && !LooksLikeFileNameHint(hintedTitle!, normalizedPath))
                return hintedTitle!;

            return normalizedPath;
        }

        private static bool LooksLikeFileNameHint(string hint, string path)
        {
            try
            {
                string name = Path.GetFileName(path) ?? string.Empty;
                string bare = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                hint = hint.Trim();
                return string.Equals(hint, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(hint, bare, StringComparison.OrdinalIgnoreCase)
                    || Path.HasExtension(hint) && Path.GetExtension(hint).Length is > 2 and <= 5 && string.Equals(Path.GetExtension(hint), Path.GetExtension(path), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private string BuildBestDisplayTitleForPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string candidate = NormalizeMediaPathForDisplay(path);
            string? hintedTitle = PlaybackTitleHints.GetTitle(path);
            if (string.IsNullOrWhiteSpace(hintedTitle))
                hintedTitle = PlaybackTitleHints.GetTitle(candidate);
            if (!string.IsNullOrWhiteSpace(hintedTitle) && !LooksLikeFileNameHint(hintedTitle!, candidate))
                return LooksLikePureAudioByExt(candidate)
                    ? NormalizeMusicTrackDisplayTitle(hintedTitle!)
                    : hintedTitle!.Trim();

            try
            {
                var remembered = JellyfinClient.RememberedTitle(path) ?? JellyfinClient.RememberedTitle(candidate);
                if (remembered.HasValue && !string.IsNullOrWhiteSpace(remembered.Value.Title) &&
                    !LooksLikeFileNameHint(remembered.Value.Title, candidate))
                {
                    PlaybackTitleHints.Set(path, remembered.Value.Title, remembered.Value.Category);
                    return remembered.Value.Title.Trim();
                }
            }
            catch { }

            try
            {
                if (ShouldUseMovieMetadataTitleForPath(candidate))
                {
                    string best = MovieMetadataService.GetBestKnownDisplayTitle(ResolveMetadataLookupKey(candidate));
                    if (!string.IsNullOrWhiteSpace(best))
                        return best.Trim();
                }
            }
            catch { }

            try
            {
                string fallback = NormalizeDisplayTitle(Path.GetFileNameWithoutExtension(candidate) ?? candidate);
                return LooksLikePureAudioByExt(candidate) ? NormalizeMusicTrackDisplayTitle(fallback) : fallback;
            }
            catch
            {
                string fallback = NormalizeDisplayTitle(candidate);
                return LooksLikePureAudioByExt(candidate) ? NormalizeMusicTrackDisplayTitle(fallback) : fallback;
            }
        }

        private static string NormalizeMusicTrackDisplayTitle(string title)
        {
            return MusicTextIdentity.Title(title);
        }

        private void PlayLibraryRequestedPath(string path, double? resumeSeconds)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            // Chi parte da Spotlight ci ritorna quando chiude il film (anche a fine titolo).
            if (_netflixModePage?.Visible == true) _returnToSpotlightOnPlaybackExit = true;
            BeginLibraryToPlaybackTransition(!LooksLikePureAudioByExt(path) && !LooksLikeImageByExt(path));
            _returnToCinematicHomeOnPlaybackExit = false;
            _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(path) ?? _cinematicLibraryPage?.SelectedCategory;
            ResetLibraryRemoteActivation(clearFocusRing: true);

            bool resumeOpen = resumeSeconds.HasValue && resumeSeconds.Value > 0.01;
            if (!resumeOpen && IsPathQueued(path))
            {
                RunAfterLoadingFrame(() => PlayQueuedPath(path));
                return;
            }

            if (resumeOpen)
            {
                _suppressPreRollOnce = true;
                RunAfterLoadingFrame(() => OpenPath(path, resume: resumeSeconds ?? 0, allowPlaceholderGate: false));
            }
            else
            {
                RunAfterLoadingFrame(() => OpenPath(path, resume: resumeSeconds ?? 0));
            }
        }

        private void PlayCinematicLibraryRequestedPath(string path, double? resumeSeconds)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (HasMusicPlayback && string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                if (_paused) TogglePlayPause();
                if (resumeSeconds.HasValue) SeekMusic(resumeSeconds.Value);
                return;
            }

            _returnToSpotlightOnPlaybackExit = _netflixModePage?.Visible == true;
            BeginLibraryToPlaybackTransition(!LooksLikePureAudioByExt(path) && !LooksLikeImageByExt(path));
            _returnToCinematicWebSourceOnPlaybackExit = _cinematicLibraryPage?.IsWebInputView == true
                ? _cinematicLibraryPage.WebInputKind
                : null;
            _returnToCinematicHomeOnPlaybackExit = _returnToCinematicWebSourceOnPlaybackExit == null &&
                                                    _cinematicLibraryPage?.IsHomeView == true;
            try
            {
                if (_returnToCinematicWebSourceOnPlaybackExit != null)
                    _currentLibraryCategory = null;
                else
                    _currentLibraryCategory = _cinematicLibraryPage?.ResolvePlaybackCategoryForPath(path)
                        ?? ResolveEffectiveLibraryCategoryForPath(path)
                        ?? _currentLibraryCategory;
            }
            catch
            {
                _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(path) ?? _currentLibraryCategory;
            }

            ResetLibraryRemoteActivation(clearFocusRing: true);

            bool resumeOpen = resumeSeconds.HasValue && resumeSeconds.Value > 0.01;
            if (!resumeOpen && IsPathQueued(path))
            {
                RunAfterLoadingFrame(() => PlayQueuedPath(path));
                return;
            }

            if (resumeOpen)
            {
                _suppressPreRollOnce = true;
                RunAfterLoadingFrame(() => OpenPath(path, resume: resumeSeconds ?? 0, allowPlaceholderGate: false));
            }
            else
            {
                RunAfterLoadingFrame(() => OpenPath(path, resume: resumeSeconds ?? 0));
            }
        }

        private void HideCinematicLibraryForPlayback()
        {
            try
            {
                if (_cinematicLibraryPage != null)
                {
                    try { _focusRing.Attach(null); } catch { }
                    _focused = null;
                    _dpadRoot = null;
                    if (_cinematicLibraryPage.ContainsFocus)
                        ActiveControl = null;
                    _cinematicLibraryPage.Enabled = false;
                    _cinematicLibraryPage.Visible = false;
                    _cinematicLibraryPage.SendToBack();
                }
                _videoHost.Visible = true;
                _videoHost.BringToFront();
            }
            catch { }
        }

        private bool ShouldUseMovieMetadataTitleForPath(string pathToOpen)
        {
            string localPath = NormalizeMediaPathForDisplay(pathToOpen);
            string? hintedCategory = PlaybackTitleHints.GetCategory(pathToOpen);
            if (string.IsNullOrWhiteSpace(hintedCategory))
                hintedCategory = PlaybackTitleHints.GetCategory(localPath);

            bool hasHintTitle = !string.IsNullOrWhiteSpace(PlaybackTitleHints.GetTitle(pathToOpen))
                || !string.IsNullOrWhiteSpace(PlaybackTitleHints.GetTitle(localPath));

            if (string.Equals(hintedCategory, "Film", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrWhiteSpace(localPath))
                return hasHintTitle;

            if (!LooksLikeVideoByExt(localPath) && !hasHintTitle)
                return false;

            string? effectiveCategory = ResolveEffectiveLibraryCategoryForPath(localPath);
            if (string.Equals(effectiveCategory, "Film", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.IsNullOrWhiteSpace(effectiveCategory) || hasHintTitle;
        }

        private bool ShouldUseTmdbBackdropForPath(string pathToOpen)
        {
            return _pausePlaceholderUseTmdbBackdrop
                && ShouldUseMovieMetadataTitleForPath(pathToOpen);
        }

        private string? ResolvePausePlaceholderFallbackPath()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_pausePlaceholderPath) && File.Exists(_pausePlaceholderPath))
                    return _pausePlaceholderPath;
            }
            catch { }

            try { return PickRandomPausePlaceholderPath(); } catch { return null; }
        }

        private string BuildPreOpenPlaceholderTitle(string pathToOpen)
        {
            string best = BuildBestDisplayTitleForPath(pathToOpen);
            if (!string.IsNullOrWhiteSpace(best))
                return best;

            string candidate = NormalizeMediaPathForDisplay(pathToOpen);
            try { return NormalizeDisplayTitle(Path.GetFileNameWithoutExtension(candidate) ?? candidate); }
            catch { return Path.GetFileNameWithoutExtension(candidate) ?? candidate; }
        }

        private string? PickRandomPausePlaceholderPath()
        {
            try
            {
                var files = EnumeratePausePlaceholderFiles().ToList();
                if (files.Count == 0) return null;
                return files[new Random().Next(files.Count)];
            }
            catch { return null; }
        }

        private async Task<PreOpenPlaceholderVisual> BuildPreOpenPlaceholderVisualAsync(string pathToOpen, CancellationToken ct)
        {
            string targetPath = NormalizeMediaPathForDisplay(pathToOpen);
            string tmdbLookupKey = ResolveMetadataLookupKey(pathToOpen);
            bool wantsTmdbBackdrop = ShouldUseTmdbBackdropForPath(pathToOpen);
            string? fallbackImage = ResolvePausePlaceholderFallbackPath();

            var visual = new PreOpenPlaceholderVisual
            {
                TitleText = BuildPreOpenPlaceholderTitle(pathToOpen),
                ShowBranding = wantsTmdbBackdrop,
                UseCover = false,
                ImagePath = fallbackImage
            };

            if (!wantsTmdbBackdrop)
                return visual;

            bool tmdbResolved = false;
            bool queueAsyncTmdb = false;

            try
            {
                // Un solo tentativo breve: un backdrop gia' in cache arriva in pochi millisecondi.
                // Prima si aspettava fino a ~3 secondi con lo schermo vuoto; ora il placeholder
                // compare subito con titolo e fondo neutro, e il backdrop lo raggiunge appena
                // scaricato (QueueTmdbBackdropPlaceholder).
                var syncAttemptWindows = new[] { PREOPEN_TMDB_WAIT };

                for (int attempt = 0; attempt < syncAttemptWindows.Length && !tmdbResolved && !ct.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var resolveTask = Task.Run(() => MovieMetadataService.ResolveTitleAndBackdrop(tmdbLookupKey, fetchCts.Token), fetchCts.Token);
                        var completed = await Task.WhenAny(resolveTask, Task.Delay(syncAttemptWindows[attempt], ct));
                        if (completed == resolveTask)
                        {
                            try
                            {
                                var art = await resolveTask;
                                if (!string.IsNullOrWhiteSpace(art.normalizedTitle))
                                    visual.TitleText = art.normalizedTitle;
                                if (!string.IsNullOrWhiteSpace(art.localBackdropPath) && File.Exists(art.localBackdropPath))
                                {
                                    visual.ImagePath = art.localBackdropPath;
                                    visual.ShowBranding = true;
                                    visual.UseCover = true;
                                    tmdbResolved = true;
                                    queueAsyncTmdb = false;
                                    break;
                                }

                                queueAsyncTmdb = true;
                            }
                            catch (OperationCanceledException) { }
                            catch
                            {
                                queueAsyncTmdb = true;
                            }
                        }
                        else
                        {
                            try { fetchCts.Cancel(); } catch { }
                            queueAsyncTmdb = true;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        queueAsyncTmdb = true;
                    }

                    if (!tmdbResolved && attempt < syncAttemptWindows.Length - 1 && !ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(220 + (attempt * 180)), ct); } catch (OperationCanceledException) { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                queueAsyncTmdb = true;
            }

            if (!tmdbResolved)
            {
                visual.ShowBranding = true;
                visual.UseCover = false;
                visual.ImagePath = null;
            }

            if (!tmdbResolved && queueAsyncTmdb && !ct.IsCancellationRequested)
            {
                try { QueueTmdbBackdropPlaceholder(pathToOpen); } catch { }
            }

            return visual;
        }

        private void CancelPlaceholderBackdropFetch()
        {
            var old = Interlocked.Exchange(ref _placeholderBackdropCts, null);
            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }
        }

        private static TimeSpan GetTmdbPlaceholderRetryDelay(int attempt)
        {
            double[] scheduleMs = { 900d, 1500d, 2500d, 4000d, 6000d, 8500d };
            double ms = scheduleMs[Math.Max(0, Math.Min(scheduleMs.Length - 1, attempt - 1))];
            return TimeSpan.FromMilliseconds(ms);
        }

        private bool IsQueuedTmdbPlaceholderStillRelevant(CancellationTokenSource cts, string normalizedTargetPath)
        {
            if (cts == null || cts.IsCancellationRequested || IsDisposed)
                return false;

            if (!ReferenceEquals(_placeholderBackdropCts, cts))
                return false;

            if (!_preOpenPlaceholderGateActive)
                return false;

            string pendingPath = NormalizeMediaPathForDisplay(_pendingPathAfterPlaceholderGate ?? string.Empty);
            if (!string.Equals(pendingPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private void QueueTmdbBackdropPlaceholder(string pathToOpen)
        {
            CancelPlaceholderBackdropFetch();

            if (!ShouldUseTmdbBackdropForPath(pathToOpen)) return;
            if (string.IsNullOrWhiteSpace(pathToOpen)) return;

            string targetPath = NormalizeMediaPathForDisplay(pathToOpen);
            string tmdbLookupKey = ResolveMetadataLookupKey(pathToOpen);
            var cts = new CancellationTokenSource();
            _placeholderBackdropCts = cts;

            _ = Task.Run(async () =>
            {
                int attempt = 0;

                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        if (!IsQueuedTmdbPlaceholderStillRelevant(cts, targetPath))
                            return;

                        bool hasResolvedBackdrop = false;

                        try
                        {
                            var resolved = MovieMetadataService.ResolveTitleAndBackdrop(tmdbLookupKey, cts.Token);
                            hasResolvedBackdrop = !string.IsNullOrWhiteSpace(resolved.localBackdropPath) && File.Exists(resolved.localBackdropPath);

                            if (!cts.IsCancellationRequested)
                            {
                                try
                                {
                                    BeginInvoke(new Action(() =>
                                    {
                                        if (!IsQueuedTmdbPlaceholderStillRelevant(cts, targetPath))
                                            return;
                                        if (_pausePlaceholder == null || _pausePlaceholder.IsDisposed || !_pausePlaceholder.Visible)
                                            return;

                                        if (!string.IsNullOrWhiteSpace(resolved.normalizedTitle))
                                            _pausePlaceholder.TitleText = resolved.normalizedTitle!;

                                        _pausePlaceholder.ShowBranding = true;
                                        if (hasResolvedBackdrop)
                                        {
                                            _pausePlaceholder.UseCoverImage = true;
                                            _pausePlaceholder.ShowPlaceholder(resolved.localBackdropPath!);
                                        }
                                        else
                                        {
                                            _pausePlaceholder.UseCoverImage = false;
                                            _pausePlaceholder.Invalidate();
                                        }
                                    }));
                                }
                                catch { }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch
                        {
                        }

                        if (hasResolvedBackdrop)
                            return;

                        attempt++;
                        await Task.Delay(GetTmdbPlaceholderRetryDelay(attempt), cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    if (ReferenceEquals(_placeholderBackdropCts, cts))
                        _placeholderBackdropCts = null;
                    try { cts.Dispose(); } catch { }
                }
            });
        }

    }
}
