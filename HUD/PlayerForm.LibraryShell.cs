#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private void EnsureCinematicLibraryPageCreated()
        {
            if (_cinematicLibraryPage != null)
            {
                try
                {
                    if (!_stack.Controls.Contains(_cinematicLibraryPage))
                        _stack.Controls.Add(_cinematicLibraryPage);
                }
                catch { }
                return;
            }

            var page = new CinematicMediaLibraryPage
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            try { page.SetLanguage(_uiLanguage); } catch { }
            try { page.ContextMenuStrip = _menu; } catch { }
            try { AttachPlaybackContextMenuFallback(page); } catch { }
            AttachMouseAnchorTracking(page);
            page.InitialContentReady += (_, __) => SignalStartupReady();

            page.MusicWorkspaceNavigationRequested += () => SetMusicWorkspaceView(0);
            page.OpenRequested += path =>
            {
                PlayCinematicLibraryRequestedPath(path, resumeSeconds: null);
            };
            page.MusicPlayRequested += (paths, index, resume) => StartPlaybackQueue(paths, index, shuffle: false, resumeSeconds: resume ?? 0);

            page.OpenWithResumeRequested += (path, resumeSeconds) =>
            {
                PlayCinematicLibraryRequestedPath(path, resumeSeconds);
            };

            page.QueueAddRequested += paths =>
            {
                AppendToPlaybackQueue(paths);
                page.ShowQueueAddedFeedback(paths);
            };

            page.QueueRemoveRequested += paths =>
            {
                RemoveFromPlaybackQueue(paths);
            };

            page.QueueClearRequested += () =>
            {
                ClearPlaybackQueue();
            };

            page.QueuePlayPathRequested += path =>
            {
                PlayQueuedPath(path);
            };

            page.QueueMoveRequested += (path, delta) =>
            {
                MovePlaybackQueuePath(path, delta);
            };

            page.PlaylistPlayRequested += paths =>
            {
                StartPlaybackQueue(paths, startIndex: 0, shuffle: false);
            };

            page.ThemeModeRequested += light => ApplyUiToneMode(light);

            page.QueueSnapshotResolver = () => GetPlaybackQueueSnapshotItems();
            void QueueMusicLyrics(bool retryFailed) => _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!_closingForExit)
                        _lyricsService.QueueLibrary(CinematicMediaLibraryPage.MusicPathsForLyrics(), retryFailed);
                }
                catch (Exception ex) { Dbg.Warn("Music lyrics queue failed: " + ex.Message); }
            });
            page.MusicEntered += () => QueueMusicLyrics(retryFailed: true);
            page.MusicLibraryChanged += () => QueueMusicLyrics(retryFailed: false);
            _lyricsService.SynchronizationStatusChanged += RefreshLyricsLibraryStatus;
            RefreshLyricsLibraryStatus();

            page.CloseRequested += () =>
            {
                if (_engine == null && string.IsNullOrEmpty(_currentPath))
                    ShowDefaultLibrary(stopCurrent: false);
                else
                    HideCinematicLibrary();
            };
            page.ExternalNavigationRequested += key => ShowExternalNavigationFromCinematic(key);
            page.ExternalUrlRequested += url =>
            {
                try
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? target) &&
                        (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
                    }
                }
                catch { }
            };

            _cinematicLibraryPage = page;
            _stack.Controls.Add(_cinematicLibraryPage);
            _cinematicLibraryPage.SendToBack();
        }

        private void ShowDefaultLibrary(bool stopCurrent = true)
        {
            ShowCinematicLibrary(stopCurrent);
        }

        private void ShowLibraryForCurrentCategory(bool stopCurrent = true)
        {
            string? category = _currentLibraryCategory;
            if (ShouldUseCinematicLibraryForCategory(category))
            {
                ShowCinematicLibrary(stopCurrent);
                try { _cinematicLibraryPage?.NavigateToCategory(category); } catch { }
                return;
            }

            ShowCinematicLibrary(stopCurrent);
        }

        private static bool ShouldUseCinematicLibraryForCategory(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return true;

            string value = category.Trim();
            return value.Equals("Film", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Movies", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Video", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Videos", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Musica", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Music", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Foto", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Photos", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Serie", StringComparison.OrdinalIgnoreCase)
                || value.Equals("TV Series", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldUseCinematicLibraryForCurrentContext()
        {
            string? path = _currentPath;
            if (string.IsNullOrWhiteSpace(path))
                return true;

            try
            {
                string localPath = NormalizeMediaPathForDisplay(path);
                if (!LooksLikeVideoByExt(localPath))
                    return false;

                var parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(localPath);
                if (parsed.IsTvEpisode)
                    return false;
            }
            catch { }

            return true;
        }

        private void ShowExternalNavigationFromCinematic(string key)
        {
            if (key is "Movies" or "Videos" or "TV Series" or "Music" or "Photos" or "Favourites" or "Playlists")
            {
                try { _cinematicLibraryPage?.NavigateToCategory(key); } catch { }
                return;
            }

            if (string.Equals(key, "Settings", StringComparison.OrdinalIgnoreCase))
            {
                ShowSettingsHudPage();
                return;
            }

            string? source = key switch
            {
                "Computer" => "Il mio computer",
                "Network" => "Rete domestica",
                "YouTube" => "YouTube",
                "URL" => "URL",
                _ => null
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(source))
                {
                    EnsureCinematicLibraryPageCreated();
                    _cinematicLibraryPage?.NavigateToSource(source);
                    ShowCinematicLibrary(stopCurrent: false);
                }
            }
            catch { }
        }

        private void NotePlaybackPointerAction(int msg)
        {
            try
            {
                const int WM_LBUTTONDOWN = 0x0201;
                const int WM_RBUTTONDOWN = 0x0204;
                const int WM_MBUTTONDOWN = 0x0207;
                const int WM_XBUTTONDOWN = 0x020B;
                const int WM_MOUSEWHEEL = 0x020A;
                const int WM_MOUSEHWHEEL = 0x020E;

                bool pointerAction =
                    msg == WM_LBUTTONDOWN ||
                    msg == WM_RBUTTONDOWN ||
                    msg == WM_MBUTTONDOWN ||
                    msg == WM_XBUTTONDOWN ||
                    msg == WM_MOUSEWHEEL ||
                    msg == WM_MOUSEHWHEEL;

                if (!pointerAction)
                    return;

                var now = DateTime.UtcNow;
                _lastMouseMoveUtc = now;
                _lastHudActivityUtc = now;
                _suppressHudWakeUntilUtc = DateTime.MinValue;

                try
                {
                    var p = Control.MousePosition;
                    _hudWakeLastMousePos = p;
                    _hudWakeAnchorPos = p;
                    _hudWakeNeedsIntentionalMove = false;
                }
                catch { }

                try { EnsureCursorVisible(); } catch { }

                if (msg == WM_RBUTTONDOWN)
                    return;

                if (_contextMenuActive || _contextMenuPending)
                    return;
                if (_engine == null) return;
                if (IsPlaybackOverlayBlockedByLoading()) return;
                if (IsPhotoMode) return;

                if (IsAudioOnlyPlaybackUi() && !ShouldPinAudioOnlyHud())
                {
                    if (_hud.Visible)
                    {
                        _hud.Visible = false;
                        _hud.TimelineVisible = false;
                    }
                    return;
                }

                HudBump(HUD_IDLE_HIDE_MS, allowWhenRemote: false, showTimeline: true);
            }
            catch { }
        }

        private void ShowLibrary(bool stopCurrent = true)
        {
            ShowCinematicLibrary(stopCurrent);
        }

        private void HideLibrary()
        {
            HideCinematicLibrary();
        }

        private void TryShowCinematicLibraryShellBeforeStop()
        {
            try
            {
                EnsureCinematicLibraryPageCreated();
                if (_cinematicLibraryPage == null)
                    return;

                if (!_stack.Controls.Contains(_cinematicLibraryPage))
                    _stack.Controls.Add(_cinematicLibraryPage);

                _cinematicLibraryPage.Enabled = true;
                _cinematicLibraryPage.Visible = true;
                _cinematicLibraryPage.BringToFront();
                _hud.Visible = false;
                _infoOverlay.Visible = false;
                _videoLoading.Visible = false;
                _cinematicLibraryPage.Invalidate(true);
                _cinematicLibraryPage.Update();
                _stack.Update();
            }
            catch { }
        }

        private void ShowCinematicLibrary(bool stopCurrent = true)
        {
            try { HideNetflixMode(showHome: false); } catch { }

            if (stopCurrent && _engine != null)
                ClearPlaybackQueueOnPlaybackExit();

            if (stopCurrent) _nextOpenBelongsToPlaybackQueue = false;

            if (stopCurrent && _engine != null)
            {
                try { SafeStop(returnToHome: false); } catch { }
            }

            EnsureCinematicLibraryPageCreated();
            if (_cinematicLibraryPage == null)
                return;

            try
            {
                if (FormBorderStyle != FormBorderStyle.None)
                {
                    if (!ControlBox) ControlBox = true;
                    if (!MinimizeBox) MinimizeBox = true;
                    if (!MaximizeBox) MaximizeBox = true;
                    if (TopMost) TopMost = false;
                    if (_overlayHost != null && _overlayHost.TopMost)
                        _overlayHost.TopMost = false;
                }
            }
            catch { }

            try
            {
                if (!_stack.Controls.Contains(_cinematicLibraryPage))
                    _stack.Controls.Add(_cinematicLibraryPage);
            }
            catch { }

            _cinematicLibraryPage.Enabled = true;
            _cinematicLibraryPage.Visible = true;
            _cinematicLibraryPage.BringToFront();
            try { _cinematicLibraryPage.Invalidate(true); } catch { }
            try { _cinematicLibraryPage.Update(); } catch { }

            _hud.Visible = false;
            _infoOverlay.Visible = false;

            try
            {
                _cinematicLibraryPage.CreateControl();
                _cinematicLibraryPage.PerformLayout();
                _cinematicLibraryPage.EnsureInitialContentPrepared();
            }
            catch { }

            ResetLibraryRemoteActivation(clearFocusRing: true);
            _focused = null;
            _dpadRoot = _cinematicLibraryPage;
            try { _focusRing.Attach(null); } catch { }
            EnsureActive();
        }

        private void HideCinematicLibrary()
        {
            if (_cinematicLibraryPage == null) return;

            try { _focusRing.Attach(null); } catch { }
            _focused = null;
            _dpadRoot = null;

            try
            {
                if (_cinematicLibraryPage.ContainsFocus)
                    ActiveControl = null;
                _cinematicLibraryPage.Visible = false;
                _cinematicLibraryPage.SendToBack();
                _cinematicLibraryPage.Enabled = false;
                // Keep the page attached: removing and re-adding the full custom HUD
                // forced handle/layout recreation on every library/player transition.
            }
            catch { }

            try
            {
                _videoHost.Visible = true;
                _videoHost.BringToFront();
                _hud.BringToFront();
                _infoOverlay.BringToFront();
            }
            catch { }

            EnsureActive();
            BringOverlaysToFront();
        }

        private void ShowNetflixMode()
        {
            if (_netflixModePage == null || _netflixModePage.IsDisposed)
                return;

            if (_videoLoading?.Visible == true)
                return;

            // Spotlight è una superficie esclusiva: nessuna pagina impostazioni
            // deve poter rimanere davanti o riapparire all'avvio di un titolo.
            try { HideSettingsHudPage(); } catch { }

            try { SaveExtrasConfig(); } catch { }

            // Prepara il modello mentre la libreria è ancora visibile. In precedenza
            // HideCinematicLibrary veniva eseguito prima di GetNetflixModeItems e il
            // risultato era un intervallo nero che consumava l'inizio dell'animazione.
            // Paint the entry frame before enumerating roots and cached metadata.
            _netflixModePage.SetLanguage(_uiLanguage);
            _netflixModePage.Visible = true;
            _netflixModePage.BringToFront();
            _netflixModePage.Refresh();
            var items = GetNetflixModeItems();
            if (_cinematicLibraryPage != null)
            {
                foreach (NetflixModeItem item in items)
                    _cinematicLibraryPage.PopulateSpotlightItemFromCache(item);
            }
            _netflixModePage.SetLanguage(_uiLanguage);
            _netflixModePage.ApplyTheme();
            _netflixModePage.SetItems(items);
            // Model preparation can block the UI longer than the reveal duration.
            // Restart once it is ready, so the actual entrance is always visible.
            _netflixModePage.StartEntryAnimation();
            try { _hud.Visible = false; _hud.TimelineVisible = false; } catch { }
            try { _infoOverlay.Visible = false; } catch { }
            try { _remoteOsd.Visible = false; } catch { }
            try { HideMpcvrOverlayHostWhenIdle(); } catch { }
            try { _mpcvrOverlayPopup?.Hide(); } catch { }
            try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }

            // La pagina Spotlight e la libreria devono cambiare nello stesso layout
            // transaction. HideCinematicLibrary portava prima in primo piano il video
            // host: per un frame si vedeva la vecchia superficie/il nero e l'ingresso
            // lampeggiava. Qui Spotlight diventa il layer opaco prima che la libreria
            // venga nascosta alle sue spalle.
            _stack.SuspendLayout();
            try
            {
                _netflixModePage.Visible = true;
                _netflixModePage.BringToFront();
                HideCinematicLibraryBehindSpotlight();
                try { _videoHost.SendToBack(); } catch { }
            }
            finally
            {
                _stack.ResumeLayout(true);
            }

            // Forza il primo frame della dissolvenza prima di restituire il
            // controllo al message loop; i frame successivi restano timer-driven.
            try
            {
                _netflixModePage.Invalidate();
                _netflixModePage.Update();
            }
            catch { }

            try { _focusRing.Attach(null); } catch { }
            _focused = null;
            _dpadRoot = null;

            try { _netflixModePage.Focus(); } catch { }
            EnsureActive();
            BringOverlaysToFront();
        }

        private void HideCinematicLibraryBehindSpotlight()
        {
            if (_cinematicLibraryPage == null)
                return;

            try
            {
                if (_cinematicLibraryPage.ContainsFocus)
                    ActiveControl = null;
                _cinematicLibraryPage.Visible = false;
                _cinematicLibraryPage.Enabled = false;
                _cinematicLibraryPage.SendToBack();
            }
            catch { }
        }

        private void HideNetflixMode(bool showHome)
        {
            if (_netflixModePage == null)
                return;

            bool wasVisible = false;
            try
            {
                wasVisible = _netflixModePage.Visible;
                _netflixModePage.Visible = false;
                // Conserva modello, metadati e immagini tra una visita e l'altra.
                try { _netflixModePage.SuspendBackgroundWork(); } catch { }
                _netflixModePage.SendToBack();
            }
            catch { }

            if (wasVisible)
            {
                try { SaveExtrasConfig(); } catch { }
            }

            if (showHome && _engine == null && !_closingForExit)
            {
                try
                {
                    _cinematicLibraryPage?.NavigateHome();
                    ShowDefaultLibrary(stopCurrent: false);
                }
                catch { }
            }

            EnsureActive();
            BringOverlaysToFront();
        }

        private List<NetflixModeItem> GetNetflixModeItems()
        {
            string signature = GetNetflixModeItemsSignature();
            if (_netflixModeItemsCache != null &&
                string.Equals(_netflixModeItemsCacheSignature, signature, StringComparison.Ordinal))
            {
                var positions = PlaybackResumeStore.LoadAll().GroupBy(x => x.MediaPath, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().PositionSeconds, StringComparer.OrdinalIgnoreCase);
                foreach (var item in _netflixModeItemsCache) item.ResumePositionSeconds = positions.GetValueOrDefault(item.Path);
                return _netflixModeItemsCache;
            }

            _netflixModeItemsCache = BuildNetflixModeItems();
            _netflixModeItemsCacheSignature = signature;
            return _netflixModeItemsCache;
        }

        private void WarmNetflixModeItemsInBackground()
        {
            string signature = GetNetflixModeItemsSignature();
            if (_netflixModeItemsCache != null &&
                string.Equals(_netflixModeItemsCacheSignature, signature, StringComparison.Ordinal))
                return;
            if (Interlocked.CompareExchange(ref _netflixModeWarmupRunning, 1, 0) != 0)
                return;

            _ = Task.Run(BuildNetflixModeItems).ContinueWith(task =>
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated)
                    {
                        Interlocked.Exchange(ref _netflixModeWarmupRunning, 0);
                        return;
                    }

                    BeginInvoke(new Action(() =>
                    {
                        Interlocked.Exchange(ref _netflixModeWarmupRunning, 0);
                        if (task.Status != TaskStatus.RanToCompletion)
                            return;
                        string currentSignature = GetNetflixModeItemsSignature();
                        if (!string.Equals(signature, currentSignature, StringComparison.Ordinal))
                            return;
                        _netflixModeItemsCache = task.Result;
                        _netflixModeItemsCacheSignature = signature;
                    }));
                }
                catch
                {
                    Interlocked.Exchange(ref _netflixModeWarmupRunning, 0);
                }
            }, TaskScheduler.Default);
        }

        private static string GetNetflixLibraryIndexSignature()
        {
            try
            {
                string indexPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025",
                    "cinematicLibraryIndex.json");

                if (!File.Exists(indexPath))
                    return "missing";

                var info = new FileInfo(indexPath);
                return info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                       info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return "unknown";
            }
        }

        private string GetNetflixModeItemsSignature()
        {
            string resumePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "resume.json");

            return GetNetflixLibraryIndexSignature() + "|" +
                   GetFileSignature(resumePath) + "|" +
                   _uiLanguage;
        }

        private static string GetFileSignature(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return "missing";

                var info = new FileInfo(path);
                return info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                       info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return "unknown";
            }
        }

        private List<NetflixModeItem> BuildNetflixModeItems()
        {
            var result = new List<NetflixModeItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, PlaybackResumeStore.Entry> resumeLookup;
            try
            {
                resumeLookup = PlaybackResumeStore.LoadAll()
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.MediaPath))
                    .GroupBy(entry => entry.MediaPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.SavedAt).First(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                resumeLookup = new Dictionary<string, PlaybackResumeStore.Entry>(StringComparer.OrdinalIgnoreCase);
            }

            void AddCandidate(string? rawPath, string? rawCategory)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                    return;

                if (!IsNetflixLibraryCategory(rawCategory))
                    return;

                string path = NormalizeMediaPathForDisplay(rawPath.Trim());
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (!LooksLikeVideoByExt(path))
                    return;

                try
                {
                    if (!File.Exists(path))
                        return;
                }
                catch { return; }

                MovieMetadataService.MediaTitleInfo parsed;
                try { parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(path); }
                catch { parsed = new MovieMetadataService.MediaTitleInfo { NormalizedTitle = Path.GetFileNameWithoutExtension(path) ?? path }; }

                string category = string.IsNullOrWhiteSpace(rawCategory) ? ResolveEffectiveLibraryCategoryForPath(path) ?? string.Empty : rawCategory!.Trim();
                bool seriesLike = parsed.IsTvEpisode ||
                                  category.IndexOf("serie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  category.IndexOf("tv", StringComparison.OrdinalIgnoreCase) >= 0;

                string titleHint = seriesLike && !string.IsNullOrWhiteSpace(parsed.SeriesTitle)
                    ? parsed.SeriesTitle!
                    : MovieMetadataService.GetBestKnownDisplayTitle(ResolveMetadataLookupKey(path));

                if (string.IsNullOrWhiteSpace(titleHint))
                    titleHint = Path.GetFileNameWithoutExtension(path) ?? path;

                string groupKey = seriesLike
                    ? "series:" + NormalizeNetflixKey(titleHint, parsed.Year)
                    : "movie:" + path;
                if (!seen.Add(groupKey))
                    return;

                string metadataKey = seriesLike ? titleHint : ResolveMetadataLookupKey(path);
                string displayCategory = seriesLike ? Tx("Serie TV", "TV Series") : Tx("Film", "Movie");
                string? backdrop = TryGetCachedBackdropForNetflix(metadataKey, path);
                string? poster = TryGetCachedPosterForNetflix(metadataKey, path);
                resumeLookup.TryGetValue(path, out var resume);

                result.Add(new NetflixModeItem
                {
                    Path = path,
                    MetadataKey = metadataKey,
                    FormatLabel = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                    Category = displayCategory,
                    Title = titleHint,
                    YearText = parsed.Year?.ToString(),
                    Overview = seriesLike
                        ? Tx("Serie TV dalla tua libreria. Dettagli TMDb in caricamento.", "TV series from your library. Loading TMDb details.")
                        : Tx("Film dalla tua libreria. Dettagli TMDb in caricamento.", "Movie from your library. Loading TMDb details."),
                    BackdropPath = backdrop,
                    PosterPath = poster,
                    ResumePositionSeconds = resume?.PositionSeconds ?? 0,
                    DurationSeconds = resume?.DurationSeconds ?? 0
                });
            }

            try
            {
                foreach (var entry in ReadNetflixLibraryEntries())
                    AddCandidate(entry.Path, entry.Category);
            }
            catch { }

            return result;
        }

        private IEnumerable<(string Category, string Path)> ReadNetflixLibraryEntries()
        {
            var entries = new List<(string Category, string Path, int Priority, int Order)>();

            try
            {
                string indexPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025",
                    "cinematicLibraryIndex.json");

                if (!File.Exists(indexPath))
                    return Enumerable.Empty<(string Category, string Path)>();

                using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                if (!doc.RootElement.TryGetProperty("Categories", out var categories) ||
                    categories.ValueKind != JsonValueKind.Object)
                    return Enumerable.Empty<(string Category, string Path)>();

                int order = 0;
                foreach (var cat in categories.EnumerateObject())
                {
                    if (cat.Value.ValueKind != JsonValueKind.Array)
                        continue;

                    int priority = NetflixCategoryPriority(cat.Name);
                    if (priority > 1)
                        continue;

                    foreach (var item in cat.Value.EnumerateArray())
                    {
                        string? path = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                        if (string.IsNullOrWhiteSpace(path))
                            continue;

                        entries.Add((cat.Name, path!, priority, order++));
                    }
                }
            }
            catch { }

            return entries
                .OrderBy(e => e.Priority)
                .ThenBy(e => e.Order)
                .Select(e => (e.Category, e.Path))
                .ToList();
        }

        private static int NetflixCategoryPriority(string category)
        {
            if (string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase) ||
                category.IndexOf("movie", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (category.IndexOf("serie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                category.IndexOf("series", StringComparison.OrdinalIgnoreCase) >= 0 ||
                category.IndexOf("tv", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            return 5;
        }

        private static bool IsNetflixLibraryCategory(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return false;

            return string.Equals(category.Trim(), "Film", StringComparison.OrdinalIgnoreCase)
                || category.IndexOf("movie", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("serie", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("series", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("tv", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeNetflixKey(string title, int? year)
        {
            string key = Regex.Replace(title ?? string.Empty, @"[^\p{L}\p{N}]+", " ").Trim().ToLowerInvariant();
            key = Regex.Replace(key, @"\s+", " ");
            return year.HasValue ? key + "|" + year.Value.ToString() : key;
        }

        private static string? TryGetCachedBackdropForNetflix(string metadataKey, string path)
        {
            try
            {
                var value = MovieMetadataService.GetCachedBackdropPath(metadataKey);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch { }

            try
            {
                var value = MovieMetadataService.GetCachedBackdropPath(path);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch { }

            return null;
        }

        private static string? TryGetCachedPosterForNetflix(string metadataKey, string path)
        {
            try
            {
                var value = MovieMetadataService.GetCachedPosterPath(metadataKey);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch { }

            try
            {
                var value = MovieMetadataService.GetCachedPosterPath(path);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch { }

            return null;
        }

        // “Apri con …” da Esplora

    }
}
