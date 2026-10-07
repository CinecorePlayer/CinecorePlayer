#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm : Form
    {
        private void OpenFile()
        {
            ShowDefaultLibrary();
        }

        private static bool IsQueuePlayablePathInternal(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                if (File.Exists(path))
                    return true;
            }
            catch { }

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.IsFile;

            return false;
        }

        private List<string> NormalizePlaybackQueuePaths(IEnumerable<string>? paths)
        {
            var result = new List<string>();
            if (paths == null)
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in paths)
            {
                var candidate = raw?.Trim();
                if (!IsQueuePlayablePathInternal(candidate))
                    continue;
                if (!seen.Add(candidate!))
                    continue;
                result.Add(candidate!);
            }
            return result;
        }

        private void OpenPlaybackQueueItem(string path, double resume = 0, bool startPaused = false)
        {
            _playbackQueueTransitionInProgress = true;
            _nextOpenBelongsToPlaybackQueue = true;
            OpenPath(path, resume: resume, startPaused: startPaused, allowPlaceholderGate: false);
        }

        private void SchedulePlaybackQueueOpen(string path)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(140).ConfigureAwait(false); }
                    catch { }

                    try
                    {
                        if (IsDisposed)
                            return;

                        BeginInvoke(new Action(() => OpenPlaybackQueueItem(path)));
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static void ShufflePlaybackQueueInPlace(IList<string> items, Random random)
        {
            if (items == null || items.Count <= 1)
                return;

            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                if (i == j)
                    continue;

                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        private void SetSingleTrackLoop(string? path, bool enabled)
        {
            string? normalized = path?.Trim();
            if (!enabled || !IsQueuePlayablePathInternal(normalized))
            {
                _singleTrackLoopEnabled = false;
                _singleTrackLoopPath = null;
                try { _lblStatus.Text = Tx("Loop brano disattivato", "Track repeat disabled"); } catch { }
                try { RefreshPlaybackQueueUi(); } catch { }
                return;
            }

            _singleTrackLoopPath = normalized;
            _singleTrackLoopEnabled = !string.IsNullOrWhiteSpace(_singleTrackLoopPath);
            try
            {
                if (_singleTrackLoopEnabled && !string.IsNullOrWhiteSpace(_singleTrackLoopPath))
                    _lblStatus.Text = Tx("Loop brano: ", "Track repeat: ") + BuildPlaybackQueueLabel(_singleTrackLoopPath!);
            }
            catch { }
            try { RefreshPlaybackQueueUi(); } catch { }
        }

        private bool IsSingleTrackLoopEnabledForPath(string? path)
        {
            if (!_singleTrackLoopEnabled || string.IsNullOrWhiteSpace(_singleTrackLoopPath) || string.IsNullOrWhiteSpace(path))
                return false;

            return string.Equals(_singleTrackLoopPath, path.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void TogglePlaybackQueueShuffle()
        {
            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count <= 1)
            {
                _playbackQueueShuffleMode = false;
                RefreshPlaybackQueueUi();
                return;
            }

            _playbackQueueShuffleMode = !_playbackQueueShuffleMode;
            _playbackQueueHistory.Clear();
            if (_playbackQueueShuffleMode)
            {
                int anchor = FindCurrentPlaybackQueueIndex();
                if (anchor < 0)
                    anchor = Math.Max(-1, Math.Min(_playbackQueueIndex - 1, _playbackQueue.Count - 1));

                int start = Math.Max(0, anchor + 1);
                if (start < _playbackQueue.Count - 1)
                {
                    var upcoming = _playbackQueue.Skip(start).ToList();
                    ShufflePlaybackQueueInPlace(upcoming, _playbackQueueRandom);
                    for (int i = 0; i < upcoming.Count; i++)
                        _playbackQueue[start + i] = upcoming[i];
                }
            }

            try { _lblStatus.Text = _playbackQueueShuffleMode ? Tx("Riproduzione casuale attiva", "Shuffle enabled") : Tx("Riproduzione casuale disattivata", "Shuffle disabled"); } catch { }
            RefreshPlaybackQueueUi();
        }

        private void LoadPlaybackQueueState()
        {
            try
            {
                var state = _playbackQueueStore.Load();
                _playbackQueue.Clear();
                _playbackQueue.AddRange(NormalizePlaybackQueuePaths(state.Items));
                _playbackQueueHistory.Clear();
                _playbackQueueShuffleMode = state.ShuffleMode;
                _playbackQueueSessionActive = state.SessionActive && _playbackQueue.Count > 0;
                _playbackQueueIndex = _playbackQueue.Count == 0
                    ? -1
                    : Math.Max(-1, Math.Min(state.CurrentIndex, _playbackQueue.Count));

                if (!_playbackQueueSessionActive && _playbackQueue.Count > 0)
                    _playbackQueueIndex = Math.Max(0, Math.Min(_playbackQueueIndex, _playbackQueue.Count - 1));
            }
            catch
            {
                _playbackQueue.Clear();
                _playbackQueueHistory.Clear();
                _playbackQueueIndex = -1;
                _playbackQueueShuffleMode = false;
                _playbackQueueSessionActive = false;
            }
        }

        private void SavePlaybackQueueState()
        {
            try
            {
                int index;
                if (_playbackQueue.Count == 0)
                {
                    index = -1;
                }
                else if (_playbackQueueSessionActive && FindCurrentPlaybackQueueIndex() < 0)
                {
                    index = Math.Max(-1, Math.Min(_playbackQueueIndex, _playbackQueue.Count));
                }
                else
                {
                    index = Math.Max(0, Math.Min(_playbackQueueIndex, _playbackQueue.Count - 1));
                }

                _playbackQueueStore.Save(_playbackQueue, index, _playbackQueueSessionActive, _playbackQueueShuffleMode);
            }
            catch { }
        }

        private void RemoveInvalidPlaybackQueueItems()
        {
            if (_playbackQueue.Count == 0)
                return;

            _playbackQueue.RemoveAll(path => !IsQueuePlayablePathInternal(path));
            _playbackQueueHistory.Clear();

            if (_playbackQueue.Count == 0)
            {
                _playbackQueueIndex = -1;
                _playbackQueueSessionActive = false;
                _playbackQueueShuffleMode = false;
                return;
            }

            int currentIndex = FindCurrentPlaybackQueueIndex();
            if (currentIndex >= 0)
            {
                _playbackQueueIndex = currentIndex;
                return;
            }

            if (_playbackQueueSessionActive)
            {
                if (_playbackQueueIndex < 0)
                    _playbackQueueIndex = 0;
                if (_playbackQueueIndex > _playbackQueue.Count)
                    _playbackQueueIndex = _playbackQueue.Count;
                return;
            }

            if (_playbackQueueIndex < 0 || _playbackQueueIndex >= _playbackQueue.Count)
                _playbackQueueIndex = 0;
        }

        private int FindQueuedPlaybackIndexForPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || _playbackQueue.Count == 0)
                return -1;

            string normalized = path.Trim();
            for (int i = 0; i < _playbackQueue.Count; i++)
            {
                if (string.Equals(_playbackQueue[i], normalized, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private int FindCurrentPlaybackQueueIndex()
        {
            string currentPath = _currentPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentPath))
                return -1;
            return FindQueuedPlaybackIndexForPath(currentPath);
        }

        private int GetPlaybackQueueHeadIndex()
        {
            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count == 0)
                return -1;

            int currentIndex = FindCurrentPlaybackQueueIndex();
            if (currentIndex >= 0)
                return currentIndex;

            if (_playbackQueueSessionActive)
            {
                if (_playbackQueueIndex >= 0 && _playbackQueueIndex < _playbackQueue.Count)
                    return _playbackQueueIndex;
                return -1;
            }

            if (_playbackQueueIndex < 0)
                _playbackQueueIndex = 0;
            if (_playbackQueueIndex >= _playbackQueue.Count)
                _playbackQueueIndex = _playbackQueue.Count - 1;
            return _playbackQueueIndex;
        }

        private int GetPlaybackQueueTargetIndexFromDelta(int delta)
        {
            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count == 0 || delta == 0)
                return -1;

            int currentIndex = FindCurrentPlaybackQueueIndex();
            if (currentIndex >= 0)
                return currentIndex + delta;

            if (_playbackQueueSessionActive)
            {
                int anchor = _playbackQueueIndex;
                return delta > 0 ? anchor + (delta - 1) : anchor + delta;
            }

            int baseIndex = Math.Max(0, Math.Min(_playbackQueueIndex, _playbackQueue.Count - 1));
            return baseIndex + delta;
        }

        private void NormalizeActivePlaybackQueueToCurrent()
        {
            RemoveInvalidPlaybackQueueItems();

            if (_playbackQueue.Count == 0)
            {
                _playbackQueueIndex = -1;
                _playbackQueueSessionActive = false;
                return;
            }

            int currentIndex = FindCurrentPlaybackQueueIndex();
            if (currentIndex >= 0)
            {
                _playbackQueueIndex = currentIndex;
                return;
            }

            if (_playbackQueueSessionActive)
            {
                if (_playbackQueueIndex < 0)
                    _playbackQueueIndex = 0;
                if (_playbackQueueIndex > _playbackQueue.Count)
                    _playbackQueueIndex = _playbackQueue.Count;
                return;
            }

            if (_playbackQueueIndex < 0 || _playbackQueueIndex >= _playbackQueue.Count)
                _playbackQueueIndex = 0;
        }

        private int GetPlaybackQueueCurrentIndex()
        {
            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count == 0)
                return -1;

            int currentIndex = FindCurrentPlaybackQueueIndex();
            if (currentIndex >= 0)
                return currentIndex;

            if (_playbackQueueSessionActive)
                return -1;

            if (_playbackQueueIndex < 0)
                _playbackQueueIndex = 0;
            if (_playbackQueueIndex >= _playbackQueue.Count)
                _playbackQueueIndex = _playbackQueue.Count - 1;
            return _playbackQueueIndex;
        }

        private bool IsPathQueued(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || _playbackQueue.Count == 0)
                return false;

            return _playbackQueue.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        private string BuildPlaybackQueueLabel(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string best = BuildBestDisplayTitleForPath(path);
            if (!string.IsNullOrWhiteSpace(best))
                return best;

            try
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
            catch { }

            return path;
        }

        private string? GetPlaybackQueuePathAtIndex(int index)
        {
            if (_playbackQueue.Count == 0 || index < 0 || index >= _playbackQueue.Count)
                return null;
            return _playbackQueue[index];
        }

        private void PromotePlaybackQueuePathToFront(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string normalizedPath = path.Trim();
            int index = _playbackQueue.FindIndex(existing => string.Equals(existing, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                _playbackQueue.Insert(0, normalizedPath);
            }
            else if (index > 0)
            {
                string existingPath = _playbackQueue[index];
                _playbackQueue.RemoveAt(index);
                _playbackQueue.Insert(0, existingPath);
            }

            _playbackQueueIndex = _playbackQueue.Count > 0 ? 0 : -1;
        }

        private void PromotePlaybackQueuePathAndOpen(string path, double resume = 0, bool startPaused = false, bool resetLibraryFocus = false)
        {
            if (!IsQueuePlayablePathInternal(path))
                return;

            NormalizeActivePlaybackQueueToCurrent();
            PromotePlaybackQueuePathToFront(path);
            _playbackQueueHistory.Clear();
            _playbackQueueSessionActive = _playbackQueue.Count > 0;

            string? nextPath = GetPlaybackQueuePathAtIndex(0);
            RefreshPlaybackQueueUi(nextPath);
            if (string.IsNullOrWhiteSpace(nextPath))
                return;

            _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(nextPath) ?? _cinematicLibraryPage?.SelectedCategory ?? _currentLibraryCategory;
            _suppressPreRollOnce = true;
            _suppressVideoLoadingOnce = true;
            if (resetLibraryFocus)
                ResetLibraryRemoteActivation(clearFocusRing: true);
            _endTriggered = true;
            _endCandidateSinceUtc = DateTime.MinValue;
            try { if (!LooksLikePureAudioByExt(nextPath)) HideLibrary(); } catch { }

            if (string.Equals(_currentPath?.Trim(), nextPath, StringComparison.OrdinalIgnoreCase) && resume <= 0 && !startPaused)
                return;

            OpenPlaybackQueueItem(nextPath, resume, startPaused);
        }

        private IReadOnlyList<PlaybackQueueViewItem> GetPlaybackQueueSnapshotItems()
        {
            NormalizeActivePlaybackQueueToCurrent();

            var snapshot = new List<PlaybackQueueViewItem>();
            int currentIndex = FindCurrentPlaybackQueueIndex();
            if (currentIndex < 0 && !_playbackQueueSessionActive && _playbackQueue.Count > 0)
                currentIndex = Math.Max(0, Math.Min(_playbackQueueIndex, _playbackQueue.Count - 1));

            for (int i = 0; i < _playbackQueue.Count; i++)
            {
                string path = _playbackQueue[i];
                snapshot.Add(new PlaybackQueueViewItem
                {
                    Path = path,
                    Label = BuildPlaybackQueueLabel(path),
                    Index = i,
                    IsCurrent = i == currentIndex
                });
            }
            return snapshot;
        }

        private bool CanSkipPlaybackQueue(int delta)
        {
            if (delta == 0)
                return false;

            int targetIndex = GetPlaybackQueueTargetIndexFromDelta(delta);
            return targetIndex >= 0 && targetIndex < _playbackQueue.Count;
        }

        private void RefreshPlaybackQueueUi(string? preferredSelectPath = null)
        {
            try
            {
                if (IsDisposed)
                    return;

                if (InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => RefreshPlaybackQueueUi(preferredSelectPath))); }
                    catch { }
                    return;
                }

                bool explicitTarget = !string.IsNullOrWhiteSpace(preferredSelectPath);
                if (!explicitTarget && !_playbackQueueTransitionInProgress && !_nextOpenBelongsToPlaybackQueue)
                    NormalizeActivePlaybackQueueToCurrent();

                SavePlaybackQueueState();

                if (_mQueueMenuItem != null && !_mQueueMenuItem.IsDisposed)
                {
                    string queueTitle = Tx("Coda", "Queue");
                    _mQueueMenuItem.Text = _playbackQueue.Count > 0 ? $"{queueTitle} ({_playbackQueue.Count})" : queueTitle;
                    try { PopulatePlaybackQueueMenu(_mQueueMenuItem); } catch { }
                    try
                    {
                        if (_mQueueMenuItem.DropDown != null && _mQueueMenuItem.DropDown.Visible)
                        {
                            _mQueueMenuItem.DropDown.PerformLayout();
                            _mQueueMenuItem.DropDown.Invalidate(true);
                            _mQueueMenuItem.DropDown.Update();
                        }
                    }
                    catch { }
                }

                try { _cinematicLibraryPage?.RefreshQueueEditorOverlay(); } catch { }
            }
            catch { }
        }



        private bool OpenPlaybackQueueIndex(int index, bool resetLibraryFocus = false)
        {
            RemoveInvalidPlaybackQueueItems();
            if (_playbackQueue.Count == 0)
            {
                _playbackQueueIndex = -1;
                _playbackQueueSessionActive = false;
                RefreshPlaybackQueueUi();
                return false;
            }

            if (index < 0 || index >= _playbackQueue.Count)
            {
                RefreshPlaybackQueueUi();
                return false;
            }

            string? nextPath = GetPlaybackQueuePathAtIndex(index);
            if (string.IsNullOrWhiteSpace(nextPath))
            {
                RefreshPlaybackQueueUi();
                return false;
            }

            _playbackQueueIndex = index;
            _playbackQueueSessionActive = true;
            _playbackQueueHistory.Clear();
            RefreshPlaybackQueueUi(nextPath);

            if (string.Equals(_currentPath?.Trim(), nextPath.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (!HasMusicPlayback) { try { if (!LooksLikePureAudioByExt(nextPath)) HideLibrary(); } catch { } }
                else if (_paused) TogglePlayPause();
                return true;
            }

            _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(nextPath) ?? _currentLibraryCategory;
            _suppressPreRollOnce = true;
            _suppressVideoLoadingOnce = true;
            if (resetLibraryFocus)
                ResetLibraryRemoteActivation(clearFocusRing: true);

            try { if (!LooksLikePureAudioByExt(nextPath)) HideLibrary(); } catch { }
            SchedulePlaybackQueueOpen(nextPath);
            return true;
        }



        private void OpenPlaybackQueueHead(bool resetLibraryFocus = false)
        {
            int index = GetPlaybackQueueHeadIndex();
            OpenPlaybackQueueIndex(index, resetLibraryFocus);
        }

        private void PlayQueuedPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || _playbackQueue.Count == 0)
                return;

            RemoveInvalidPlaybackQueueItems();
            int index = _playbackQueue.FindIndex(existing => string.Equals(existing?.Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            OpenPlaybackQueueIndex(index);
        }



        private void ReorderPlaybackQueuePath(string? path, int targetIndex)
        {
            if (string.IsNullOrWhiteSpace(path) || _playbackQueue.Count <= 1)
                return;

            NormalizeActivePlaybackQueueToCurrent();

            int index = _playbackQueue.FindIndex(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            targetIndex = Math.Max(0, Math.Min(_playbackQueue.Count - 1, targetIndex));
            if (targetIndex == index)
                return;

            int anchorIndex = FindCurrentPlaybackQueueIndex();
            if (anchorIndex < 0)
            {
                if (_playbackQueueSessionActive)
                    anchorIndex = Math.Max(0, Math.Min(_playbackQueueIndex, _playbackQueue.Count));
                else
                    anchorIndex = Math.Max(0, Math.Min(_playbackQueueIndex, _playbackQueue.Count - 1));
            }

            string movedPath = _playbackQueue[index];
            _playbackQueue.RemoveAt(index);
            if (targetIndex > _playbackQueue.Count)
                targetIndex = _playbackQueue.Count;
            _playbackQueue.Insert(targetIndex, movedPath);

            if (anchorIndex == index)
                _playbackQueueIndex = targetIndex;
            else if (index < anchorIndex && targetIndex >= anchorIndex)
                _playbackQueueIndex = anchorIndex - 1;
            else if (index > anchorIndex && targetIndex <= anchorIndex)
                _playbackQueueIndex = anchorIndex + 1;
            else
                _playbackQueueIndex = anchorIndex;

            RefreshPlaybackQueueUi(movedPath);
        }

        private void MovePlaybackQueuePath(string? path, int delta)
        {
            if (string.IsNullOrWhiteSpace(path) || delta == 0 || _playbackQueue.Count <= 1)
                return;

            int index = _playbackQueue.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            int targetIndex = index + delta;
            if (targetIndex < 0 || targetIndex >= _playbackQueue.Count)
                return;

            ReorderPlaybackQueuePath(path, targetIndex);
        }

        private void ClearPlaybackQueue()
        {
            bool shouldDisableLoop = _singleTrackLoopEnabled
                && !string.IsNullOrWhiteSpace(_singleTrackLoopPath)
                && (_playbackQueue.Any(path => string.Equals(path, _singleTrackLoopPath, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(_currentPath?.Trim(), _singleTrackLoopPath, StringComparison.OrdinalIgnoreCase));

            _playbackQueue.Clear();
            _playbackQueueHistory.Clear();
            _playbackQueueIndex = -1;
            _playbackQueueShuffleMode = false;
            _playbackQueueSessionActive = false;
            _nextOpenBelongsToPlaybackQueue = false;

            if (shouldDisableLoop)
                SetSingleTrackLoop(null, false);

            RefreshPlaybackQueueUi();
        }

        private void ClearPlaybackQueueOnPlaybackExit()
        {
            try
            {
                if (_playbackQueueTransitionInProgress || _nextOpenBelongsToPlaybackQueue)
                    return;

                ClearPlaybackQueue();
            }
            catch { }
        }

        private void PopulatePlaybackQueueMenu(ToolStripMenuItem root)
        {
            if (root == null)
                return;

            root.DropDownItems.Clear();

            var snapshot = GetPlaybackQueueSnapshotItems()
                .OrderBy(item => item.Index)
                .ToList();

            string queueTitle = Tx("Coda", "Queue");
            root.Text = snapshot.Count > 0 ? $"{queueTitle} ({snapshot.Count})" : queueTitle;

            if (snapshot.Count == 0)
            {
                root.DropDownItems.Add(new ToolStripMenuItem(Tx("Coda vuota", "Queue is empty")) { Enabled = false });
                ApplyDarkMenuThemeRecursive(root.DropDownItems);
                return;
            }

            var openEditorItem = new ToolStripMenuItem(Tx("Gestisci coda…", "Manage queue…"));
            openEditorItem.Click += (_, __) => RequestPlaybackQueueEditorFromMenu();
            root.DropDownItems.Add(openEditorItem);
            var clearItem = new ToolStripMenuItem(Tx("Svuota coda", "Clear queue"));
            clearItem.Click += (_, __) => ClearPlaybackQueue();
            root.DropDownItems.Add(clearItem);

            ApplyDarkMenuThemeRecursive(root.DropDownItems);
        }

        private void RequestPlaybackQueueEditorFromMenu()
        {
            try
            {
                try { _menu?.Close(ToolStripDropDownCloseReason.ItemClicked); } catch { }
                try { ResetPlaybackContextMenuState(); } catch { }

                BeginInvoke(new Action(() =>
                {
                    try { ResetPlaybackContextMenuState(); } catch { }
                    ShowPlaybackQueueEditor();
                }));
            }
            catch
            {
                try { ShowPlaybackQueueEditor(); } catch { }
            }
        }

        private void ShowPlaybackQueueEditor()
        {
            if (_openingPlaybackQueueEditor)
                return;

            _openingPlaybackQueueEditor = true;
            try
            {
                try { _menu?.Close(ToolStripDropDownCloseReason.CloseCalled); } catch { }
                try { ResetPlaybackContextMenuState(); } catch { }

                if (_playbackQueueEditorForm != null && !_playbackQueueEditorForm.IsDisposed)
                    _playbackQueueEditorForm.Close();
                _playbackQueueEditorForm = null;

                EnsureCinematicLibraryPageCreated();
                if (_cinematicLibraryPage == null)
                    return;

                _cinematicLibraryPage.Enabled = true;
                _cinematicLibraryPage.Visible = true;
                _cinematicLibraryPage.BringToFront();
                _hud.Visible = false;
                _infoOverlay.Visible = false;

                try { _cinematicLibraryPage.EnsureInitialContentPrepared(); } catch { }
                try { _cinematicLibraryPage.OpenQueueEditorOverlay(); } catch { }

                ResetLibraryRemoteActivation(clearFocusRing: true);
                SuspendLibraryHoverAnchorUntilMouseMove();
                try { _cinematicLibraryPage.SuspendInitialPointerHoverUntilMouseMove(); } catch { }
                // Pointer navigation must not attach the remote focus ring to the entire page.
                EnsureActive();
                try { _overlayHost?.SetClickThrough(true); } catch { }
                try { if (_overlayInlineHost != null) _overlayInlineHost.ClickThrough = true; } catch { }
                try { _cinematicLibraryPage.BringToFront(); } catch { }
            }
            catch (Exception ex)
            {
                try { Dbg.Warn("ShowPlaybackQueueEditor EX: " + ex.Message); } catch { }
                try
                {
                    MessageBox.Show(this,
                        Tx("Non riesco ad aprire il gestore coda.", "Unable to open the queue manager.") + Environment.NewLine + Environment.NewLine + ex.Message,
                        Tx("Coda", "Queue"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
            }
            finally
            {
                _openingPlaybackQueueEditor = false;
            }
        }





        private void AppendToPlaybackQueue(IEnumerable<string>? paths)
        {
            var additions = NormalizePlaybackQueuePaths(paths);
            if (additions.Count == 0)
                return;

            NormalizeActivePlaybackQueueToCurrent();

            string? firstAddedPath = null;
            foreach (var path in additions)
            {
                int existingIndex = _playbackQueue.FindIndex(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
                string normalizedPath = path;
                if (existingIndex >= 0)
                {
                    normalizedPath = _playbackQueue[existingIndex];
                    _playbackQueue.RemoveAt(existingIndex);
                    if (_playbackQueueIndex > existingIndex)
                        _playbackQueueIndex--;
                }

                _playbackQueue.Add(normalizedPath);
                if (firstAddedPath == null)
                    firstAddedPath = normalizedPath;
            }

            if (_playbackQueue.Count == 0)
                _playbackQueueIndex = -1;
            else if (!_playbackQueueSessionActive && (_playbackQueueIndex < 0 || _playbackQueueIndex >= _playbackQueue.Count))
                _playbackQueueIndex = 0;
            else if (_playbackQueueSessionActive && _playbackQueueIndex > _playbackQueue.Count)
                _playbackQueueIndex = _playbackQueue.Count;

            RefreshPlaybackQueueUi(firstAddedPath);
        }

        private void RemoveFromPlaybackQueue(IEnumerable<string>? paths)
        {
            if (_playbackQueue.Count == 0 || paths == null)
                return;

            var removal = new HashSet<string>(
                paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (removal.Count == 0)
                return;

            NormalizeActivePlaybackQueueToCurrent();

            int queuedCurrentIndex = FindCurrentPlaybackQueueIndex();
            int detachedUpcomingIndex = _playbackQueueSessionActive && queuedCurrentIndex < 0
                ? Math.Max(0, Math.Min(_playbackQueueIndex, _playbackQueue.Count))
                : -1;

            int removedBeforeCurrent = 0;
            bool removedCurrent = false;
            int removedBeforeUpcoming = 0;

            for (int i = 0; i < _playbackQueue.Count; i++)
            {
                if (!removal.Contains(_playbackQueue[i]))
                    continue;

                if (queuedCurrentIndex >= 0)
                {
                    if (i < queuedCurrentIndex)
                        removedBeforeCurrent++;
                    if (i == queuedCurrentIndex)
                        removedCurrent = true;
                }

                if (detachedUpcomingIndex >= 0 && i < detachedUpcomingIndex)
                    removedBeforeUpcoming++;
            }

            bool shouldDisableLoop = _singleTrackLoopEnabled
                && !string.IsNullOrWhiteSpace(_singleTrackLoopPath)
                && removal.Contains(_singleTrackLoopPath!);

            _playbackQueue.RemoveAll(p => removal.Contains(p));
            _playbackQueueHistory.Clear();

            if (shouldDisableLoop)
                SetSingleTrackLoop(null, false);

            if (_playbackQueue.Count == 0)
            {
                ClearPlaybackQueue();
                return;
            }

            if (queuedCurrentIndex >= 0)
            {
                if (removedCurrent)
                {
                    int upcomingIndex = queuedCurrentIndex - removedBeforeCurrent;
                    _playbackQueueIndex = Math.Max(0, Math.Min(_playbackQueue.Count, upcomingIndex));
                    _playbackQueueSessionActive = true;
                }
                else
                {
                    int adjustedCurrentIndex = queuedCurrentIndex - removedBeforeCurrent;
                    _playbackQueueIndex = Math.Max(0, Math.Min(_playbackQueue.Count - 1, adjustedCurrentIndex));
                }
            }
            else if (_playbackQueueSessionActive)
            {
                int adjustedUpcomingIndex = detachedUpcomingIndex - removedBeforeUpcoming;
                _playbackQueueIndex = Math.Max(0, Math.Min(_playbackQueue.Count, adjustedUpcomingIndex));
            }
            else
            {
                if (_playbackQueueIndex >= _playbackQueue.Count)
                    _playbackQueueIndex = _playbackQueue.Count - 1;
                if (_playbackQueueIndex < 0)
                    _playbackQueueIndex = 0;
            }

            RefreshPlaybackQueueUi();
        }

        private void StartPlaybackQueue(IEnumerable<string>? paths, int startIndex, bool shuffle, double resumeSeconds = 0)
        {
            var queue = NormalizePlaybackQueuePaths(paths);
            if (queue.Count == 0)
                return;
            bool sameMusicTrack = HasMusicPlayback && string.Equals(_currentPath, queue[Math.Clamp(startIndex, 0, queue.Count - 1)], StringComparison.OrdinalIgnoreCase);
            bool deferLibrarySwap = !sameMusicTrack && _cinematicLibraryPage?.Visible == true;
            if (deferLibrarySwap)
                BeginLibraryToPlaybackTransition(!LooksLikePureAudioByExt(queue[Math.Clamp(startIndex, 0, queue.Count - 1)]) && !LooksLikeImageByExt(queue[Math.Clamp(startIndex, 0, queue.Count - 1)]));

            if (startIndex < 0)
                startIndex = 0;
            if (startIndex >= queue.Count)
                startIndex = queue.Count - 1;

            if (shuffle && queue.Count > 1)
            {
                string startPath = queue[startIndex];
                queue.RemoveAt(startIndex);
                ShufflePlaybackQueueInPlace(queue, _playbackQueueRandom);
                queue.Insert(0, startPath);
                startIndex = 0;
            }

            _playbackQueue.Clear();
            _playbackQueue.AddRange(queue);
            _playbackQueueHistory.Clear();
            _playbackQueueIndex = _playbackQueue.Count > 0 ? startIndex : -1;
            _playbackQueueShuffleMode = shuffle;
            _playbackQueueSessionActive = _playbackQueue.Count > 0;

            if (_playbackQueue.Count == 0)
            {
                RefreshPlaybackQueueUi();
                return;
            }

            string nextPath = _playbackQueue[_playbackQueueIndex];
            RefreshPlaybackQueueUi(nextPath);
            _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(nextPath) ?? _cinematicLibraryPage?.SelectedCategory ?? _currentLibraryCategory;
            _suppressPreRollOnce = true;
            if (!deferLibrarySwap)
                _suppressVideoLoadingOnce = true;
            ResetLibraryRemoteActivation(clearFocusRing: true);

            if (string.Equals(_currentPath?.Trim(), nextPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!HasMusicPlayback) { try { if (!LooksLikePureAudioByExt(nextPath)) HideLibrary(); } catch { } }
                else if (_paused) TogglePlayPause();
                return;
            }

            _endTriggered = true;
            _endCandidateSinceUtc = DateTime.MinValue;
            if (!deferLibrarySwap)
            {
                try { if (!LooksLikePureAudioByExt(nextPath)) HideLibrary(); } catch { }
            }
            OpenPlaybackQueueItem(nextPath, resume: resumeSeconds);
        }

        private bool TrySkipPlaybackQueue(int delta)
        {
            int targetIndex = GetPlaybackQueueTargetIndexFromDelta(delta);
            if (targetIndex < 0 || targetIndex >= _playbackQueue.Count)
                return false;

            return OpenPlaybackQueueIndex(targetIndex);
        }

    }
}
