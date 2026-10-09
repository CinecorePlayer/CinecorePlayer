#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private void AutoSelectDefaultStreams()
        {
            if (_engine == null) return;
            try
            {
                var streams = _engine.EnumerateStreams().ToList();
                var audioStreams = streams.Where(s => s.IsAudio).ToList();
                var subtitleStreams = streams.Where(s => s.IsSubtitle).ToList();

                var selAudio = audioStreams.FirstOrDefault(s => s.Selected)
                               ?? audioStreams.FirstOrDefault(s => string.Equals(SubtitleLanguage(s), "it", StringComparison.OrdinalIgnoreCase))
                               ?? audioStreams.FirstOrDefault();
                if (selAudio != null)
                    _engine.EnableByGlobalIndex(selAudio.GlobalIndex);

                var preferredLang = SubtitleLanguage(selAudio) ?? _preferredSubtitleLangKey;
                if (!string.IsNullOrWhiteSpace(preferredLang))
                    _preferredSubtitleLangKey = preferredLang;

                _subtitleAutoForcedMode = false;

                // Un sottotitolo esterno scelto dall'utente per questo film vince sulla regola "solo forzati".
                if (TrySelectRememberedExternalSubtitle(retryLater: true))
                    return;

                if (subtitleStreams.Count == 0)
                    return;

                bool forcedFound = TrySelectAutoForcedSubtitles(_engine, subtitleStreams, preferredLang)
                    && _engine.EnumerateStreams().Any(s => s.IsSubtitle && s.Selected && IsForcedSubtitle(s));
                if (forcedFound)
                {
                    _subtitleAutoForcedMode = true;
                    return;
                }

                // No matching forced track: keep subtitles off. Falling back to
                // arbitrary full subtitles contradicts the forced-only default.
                _engine.DisableSubtitlesIfPossible();
            }
            catch (Exception ex)
            {
                Dbg.Warn("AutoSelectDefaultStreams: " + ex.Message);
            }
        }

        private bool TryGetFilterGraph(out IFilterGraph2? fg)
        {
            fg = null;
            if (_engine == null) return false;
            try
            {
                if (_engine is IFilterGraph2 direct) { fg = direct; return true; }

                var t = _engine.GetType();
                var p1 = t.GetProperty("Graph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p1 != null && p1.GetValue(_engine) is IFilterGraph2 g1) { fg = g1; return true; }

                var p2 = t.GetProperty("FilterGraph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p2 != null && p2.GetValue(_engine) is IFilterGraph2 g2) { fg = g2; return true; }

                var m1 = t.GetMethod("GetGraph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (m1 != null && (m1.Invoke(_engine, null) is IFilterGraph2 g3)) { fg = g3; return true; }
            }
            catch { }
            return false;
        }

        private void TryBindGraphNotify()
        {
            try
            {
                UnbindGraphNotify();

                if (!IsHandleCreated) return;
                if (!TryGetFilterGraph(out var fg) || fg == null) return;

                if (fg is IMediaEventEx ev)
                {
                    _graphEvents = ev;
                    ev.SetNotifyWindow(this.Handle, WM_GRAPHNOTIFY, IntPtr.Zero);
                }
            }
            catch
            {
                _graphEvents = null;
            }
        }

        private void UnbindGraphNotify()
        {
            try { _graphEvents?.SetNotifyWindow(IntPtr.Zero, 0, IntPtr.Zero); } catch { }
            _graphEvents = null;
        }

        private void DrainGraphEvents()
        {
            var ev = _graphEvents;
            if (ev == null) return;

            while (true)
            {
                int hr;
                EventCode code;
                IntPtr p1;
                IntPtr p2;

                try
                {
                    hr = ev.GetEvent(out code, out p1, out p2, 0);
                }
                catch
                {
                    break;
                }

                if (hr != 0) break;

                try
                {
                    if (code == EventCode.Complete)
                    {
                        HandlePlaybackCompleted();
                    }
                }
                finally
                {
                    try { ev.FreeEventParams(code, p1, p2); } catch { }
                }
            }
        }

        private void HandlePlaybackCompleted()
        {
            if (_stopping || _playbackQueueTransitionInProgress)
                return;
            if (_endTriggered)
                return;

            _endTriggered = true;
            _endCandidateSinceUtc = DateTime.MinValue;

            if (_playingPreRoll && !string.IsNullOrWhiteSpace(_pendingMainPathAfterPreRoll))
            {
                var next = _pendingMainPathAfterPreRoll;
                var nextResume = _pendingMainResumeAfterPreRoll;
                var nextPaused = _pendingMainStartPausedAfterPreRoll;

                _pendingMainPathAfterPreRoll = null;
                _pendingMainResumeAfterPreRoll = 0;
                _pendingMainStartPausedAfterPreRoll = false;
                _playingPreRoll = false;

                _suppressPreRollOnce = true;
                _suppressVideoLoadingOnce = true;
                try { BeginInvoke(new Action(() => OpenPath(next!, nextResume, nextPaused, allowPlaceholderGate: false))); } catch { }
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(_currentPath))
                {
                    string completedPath = _currentPath!;
                    string completedTitle = BuildBestDisplayTitleForPath(completedPath);
                    double completedDuration = GetTimelineDurationSeconds();
                    _ = System.Threading.Tasks.Task.Run(() => WatchHistoryStore.RecordCompleted(completedPath, completedTitle, completedDuration));
                }
            }
            catch { }

            if (IsSingleTrackLoopEnabledForPath(_currentPath))
            {
                string? loopPath = _currentPath;
                try
                {
                    if (_engine != null && !string.IsNullOrWhiteSpace(loopPath) && _duration > 0)
                    {
                        _engine.PositionSeconds = 0;
                        _engine.Play();
                        _paused = false;
                        SyncHudPlayingState();
                        _endTriggered = false;
                        _endCandidateSinceUtc = DateTime.MinValue;
                        try { PublishRemoteState(0); } catch { }
                        try { UpdateTime(0); } catch { }
                        return;
                    }
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(loopPath))
                {
                    _suppressPreRollOnce = true;
                    _suppressVideoLoadingOnce = true;
                    try { BeginInvoke(new Action(() => OpenPath(loopPath!, 0, false, allowPlaceholderGate: false))); } catch { }
                    return;
                }
            }

            // Handoff atomico al prossimo elemento della coda. Non passare da SafeStop/ShowLibrary.
            if (TryAdvancePlaybackQueue())
                return;

            try
            {
                bool musicInLibrary = MusicPlayingInLibrary;
                ClearPlaybackQueueOnPlaybackExit();
                SafeStop(returnToHome: false);
                if (musicInLibrary) RestoreLibraryInPlaceAfterMusicStop();
                else ShowLibraryAfterPlaybackExit();
            }
            catch { }
        }


        private static bool TryFindFilter(IFilterGraph2 fg, string nameContains, out IBaseFilter? filter)
        {
            filter = null;
            if (fg.EnumFilters(out IEnumFilters? enumF) != 0 || enumF == null) return false;

            var arr = new IBaseFilter[1];
            while (enumF.Next(1, arr, IntPtr.Zero) == 0)
            {
                var f = arr[0];
                f.QueryFilterInfo(out var info);
                try
                {
                    if (!string.IsNullOrWhiteSpace(info.achName) &&
                        info.achName.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    { filter = f; return true; }
                }
                finally
                {
                    if (info.pGraph != null) Marshal.ReleaseComObject(info.pGraph);
                }
                Marshal.ReleaseComObject(f);
            }
            return false;
        }

        private void ReopenSame()
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            double pos = _engine?.PositionSeconds ?? 0; bool paused = _paused;
            // Evita pre-roll quando ricarichiamo il renderer/engine (cambio lingua, setting madVR, ecc.)
            _suppressPreRollOnce = true;
            if (_engine != null)
                _suppressNextWledRestore = true;
            _suppressVideoLoadingOnce = true;
            OpenPath(_currentPath!, resume: pos, startPaused: paused, allowPlaceholderGate: false);
        }

        private bool IsCurrentYouTube()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentPath)) return false;
                if (!Uri.TryCreate(_currentPath, UriKind.Absolute, out var u)) return false;
                var h = u.Host;
                return h.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0
                    || h.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsYouTubeUrlForPlayback(Uri uri)
        {
            try
            {
                string h = uri.Host ?? string.Empty;
                return h.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0
                    || h.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0
                    || h.IndexOf("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }
}
