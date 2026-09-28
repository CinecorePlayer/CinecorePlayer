#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private bool IsValidLibraryFocusTarget(Control? target)
        {
            try
            {
                if (target == null || target.IsDisposed || _cinematicLibraryPage == null)
                    return false;
                if (!target.Visible || target.Width <= 0 || target.Height <= 0)
                    return false;
                if (!IsDescendant(_cinematicLibraryPage, target))
                    return false;
                if (!IsDpadFocusable(target))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ShouldAutoAttachRequestedLibraryFocus(Control? target)
        {
            try
            {
                if (_cinematicLibraryPage == null || !_cinematicLibraryPage.Visible)
                    return false;
                if (!_cinematicLibraryPage.IsRemoteNavigationReady)
                    return false;
                if (!IsValidLibraryFocusTarget(target))
                    return false;
                return _cinematicLibraryPage.IsRemoteContentFocusCandidate(target);
            }
            catch
            {
                return false;
            }
        }

        private string? ResolveEffectiveLibraryCategoryForPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return _currentLibraryCategory;

            string normalizedPath = NormalizeMediaPathForDisplay(path);

            try
            {
                string? hinted = PlaybackTitleHints.GetCategory(path);
                if (string.IsNullOrWhiteSpace(hinted))
                    hinted = PlaybackTitleHints.GetCategory(normalizedPath);
                if (!string.IsNullOrWhiteSpace(hinted))
                    return hinted;
            }
            catch { }

            try
            {
                if (_cinematicLibraryPage != null)
                {
                    string resolved = _cinematicLibraryPage.ResolvePlaybackCategoryForPath(path);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        return resolved;
                }
            }
            catch { }

            return _currentLibraryCategory;
        }

        private bool ShouldUseCinemaFeaturesForPath(string? path)
        {
            return string.Equals(ResolveEffectiveLibraryCategoryForPath(path), "Film", StringComparison.OrdinalIgnoreCase);
        }


        private static readonly TimeSpan PREOPEN_TMDB_WAIT = TimeSpan.FromMilliseconds(1800);

        private string? ResolveDemoPath(string? fileOrPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileOrPath)) return null;
                if (Path.IsPathRooted(fileOrPath) && File.Exists(fileOrPath)) return fileOrPath;

                var p = Path.Combine(_preRollDemoFolder, fileOrPath);
                return File.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }

        private string? ResolvePausePlaceholderPath(string? fileOrPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileOrPath)) return null;
                if (Path.IsPathRooted(fileOrPath) && File.Exists(fileOrPath)) return fileOrPath;

                var p = Path.Combine(_pausePlaceholderFolder, fileOrPath);
                return File.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }

        private IEnumerable<string> EnumeratePausePlaceholderFiles()
        {
            try
            {
                Directory.CreateDirectory(_pausePlaceholderFolder);
                if (!Directory.Exists(_pausePlaceholderFolder)) return Array.Empty<string>();

                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"
                };

                return Directory.EnumerateFiles(_pausePlaceholderFolder)
                    .Where(f => exts.Contains(Path.GetExtension(f) ?? ""))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private void PopulatePausePlaceholderMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();

            var miRandom = new ToolStripMenuItem(Tx("Casuale", "Random"))
            {
                Checked = string.IsNullOrWhiteSpace(_pausePlaceholderPath)
            };
            miRandom.Click += (_, __) =>
            {
                _pausePlaceholderPath = null;

                // Se scelgo un placeholder, tipicamente voglio la feature attiva.
                if (!_pausePlaceholderEnabled)
                {
                    _pausePlaceholderEnabled = true;
                    if (_miPausePlaceholderEnable != null) _miPausePlaceholderEnable.Checked = true;
                }

                try { SaveExtrasConfig(); } catch { }
            };
            parent.DropDownItems.Add(miRandom);
            parent.DropDownItems.Add(new ToolStripSeparator());

            var files = EnumeratePausePlaceholderFiles().ToList();
            if (files.Count == 0)
            {
                parent.DropDownItems.Add(new ToolStripMenuItem(Tx("Nessun file nella cartella", "No files in the folder")) { Enabled = false });
                return;
            }

            string? current = ResolvePausePlaceholderPath(_pausePlaceholderPath) ?? _pausePlaceholderPath;

            foreach (var f in files)
            {
                string label = Path.GetFileNameWithoutExtension(f);
                var mi = new ToolStripMenuItem(label)
                {
                    Checked = current != null &&
                              string.Equals(Path.GetFileName(current), Path.GetFileName(f), StringComparison.OrdinalIgnoreCase)
                };

                mi.Click += (_, __) =>
                {
                    _pausePlaceholderPath = f;

                    if (!_pausePlaceholderEnabled)
                    {
                        _pausePlaceholderEnabled = true;
                        if (_miPausePlaceholderEnable != null) _miPausePlaceholderEnable.Checked = true;
                    }

                    try { SaveExtrasConfig(); } catch { }
                };

                parent.DropDownItems.Add(mi);
            }
        }

        private IEnumerable<string> EnumerateDemoFiles()
        {
            try
            {
                if (!Directory.Exists(_preRollDemoFolder)) return Array.Empty<string>();
                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".mkv", ".mp4", ".mov", ".avi", ".wmv", ".m2ts", ".ts", ".webm"
                };
                return Directory.EnumerateFiles(_preRollDemoFolder)
                    .Where(f => exts.Contains(Path.GetExtension(f) ?? ""))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private void PopulatePreRollDemoMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();

            var none = new ToolStripMenuItem(Tx("Nessuna", "None"))
            {
                Checked = string.IsNullOrWhiteSpace(_preRollDemoPath)
            };
            none.Click += (_, __) =>
            {
                _preRollDemoPath = null;
                try { SaveExtrasConfig(); } catch { }
            };
            parent.DropDownItems.Add(none);
            parent.DropDownItems.Add(new ToolStripSeparator());

            var demos = EnumerateDemoFiles().ToList();
            if (demos.Count == 0)
            {
                parent.DropDownItems.Add(new ToolStripMenuItem(Tx("(nessuna demo trovata)", "(no demo found)")) { Enabled = false });
                parent.DropDownItems.Add(new ToolStripMenuItem(Tx("(usa la cartella Assets\\Demos)", "(use the Assets\\Demos folder)")) { Enabled = false });
                return;
            }

            foreach (var f in demos)
            {
                var label = Path.GetFileNameWithoutExtension(f);
                var it = new ToolStripMenuItem(label)
                {
                    Checked = string.Equals(_preRollDemoPath, f, StringComparison.OrdinalIgnoreCase)
                };
                it.Click += (_, __) =>
                {
                    _preRollDemoPath = f;

                    // Se scelgo una demo, tipicamente voglio la feature attiva (evita “riapri menu per abilitarla”).
                    if (!_preRollEnabled)
                    {
                        _preRollEnabled = true;
                        if (_miPreRollEnable != null) _miPreRollEnable.Checked = true;
                    }

                    try { SaveExtrasConfig(); } catch { }
                };
                parent.DropDownItems.Add(it);
            }
        }

        private void OpenFolderInExplorer(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private bool ShouldRunPreRollForPath(string path)
        {
            try
            {
                if (!_preRollEnabled) return false;
                if (string.IsNullOrWhiteSpace(_preRollDemoPath)) return false;
                if (string.IsNullOrWhiteSpace(path)) return false;
                if (!ShouldUseCinemaFeaturesForPath(path)) return false;

                // Non fare pre-roll su URL
                if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return false;

                // Non fare pre-roll sulla demo stessa
                if (!string.IsNullOrWhiteSpace(_preRollDemoPath) && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_preRollDemoPath), StringComparison.OrdinalIgnoreCase))
                    return false;

                // Solo file video
                var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
                var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".mkv", ".mp4", ".mov", ".avi", ".wmv", ".m2ts", ".ts", ".webm"
                };
                if (!videoExts.Contains(ext)) return false;

                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private string? ResolveEffectiveAudioRendererNameForOpen()
        {
            if (!string.IsNullOrWhiteSpace(_selectedAudioRendererName))
                return _selectedAudioRendererName;

            string? defaultName = BitstreamReleasePulse.GetDefaultRenderDeviceName();
            if (!string.IsNullOrWhiteSpace(defaultName))
                return defaultName;

            return null;
        }


        private void WarmUpBitstreamAudioEndpointBeforeOpen(string? effectiveRendererName)
        {
            try
            {
                string selected = effectiveRendererName ?? _selectedAudioRendererName ?? "default";
                Dbg.Log($"[AUDIO] pre-open bitstream endpoint wake pulse: selected='{selected}'", Dbg.LogLevel.Info);
                CoreAudioSessionVolume.Reset();
                BitstreamReleasePulse.Pulse(effectiveRendererName, _selectedAudioRendererName, durationMs: 650);
                CoreAudioSessionVolume.Reset();
                Thread.Sleep(350);
                Dbg.Log("[AUDIO] pre-open bitstream endpoint wake settle complete", Dbg.LogLevel.Info);
            }
            catch (Exception ex)
            {
                Dbg.Warn("[AUDIO] pre-open bitstream endpoint wake pulse failed: " + ex.Message);
            }
        }



        private bool TryAdvancePlaybackQueue()
        {
            RemoveInvalidPlaybackQueueItems();
            if (_playbackQueue.Count == 0)
                return false;

            string? completedPath = _currentPath?.Trim();
            int currentIndex = FindCurrentPlaybackQueueIndex();
            if (currentIndex < 0 && !string.IsNullOrWhiteSpace(completedPath))
                currentIndex = _playbackQueue.FindIndex(p => string.Equals(p?.Trim(), completedPath, StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0 && _playbackQueueIndex >= 0 && _playbackQueueIndex < _playbackQueue.Count)
                currentIndex = _playbackQueueIndex;

            int nextIndex = currentIndex + 1;
            if (nextIndex < 0 || nextIndex >= _playbackQueue.Count)
            {
                _playbackQueueSessionActive = false;
                _playbackQueueIndex = _playbackQueue.Count > 0 ? Math.Max(0, Math.Min(_playbackQueue.Count - 1, Math.Max(currentIndex, _playbackQueueIndex))) : -1;
                RefreshPlaybackQueueUi();
                return false;
            }

            return OpenPlaybackQueueIndex(nextIndex);
        }



        private static bool LooksLikeVideoByExt(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return new[] { ".mkv", ".mp4", ".m2ts", ".ts", ".mov", ".avi", ".wmv", ".webm", ".mts" }.Contains(ext);
        }
        private static bool LooksLikePureAudioByExt(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();

            switch (ext)
            {
                case ".mp3":
                case ".flac":
                case ".wav":
                case ".ogg":
                case ".opus":
                case ".m4a":
                case ".aac":
                case ".wma":
                case ".alac":
                case ".ape":
                    return true;

                default:
                    return false;
            }
        }
    }
}
