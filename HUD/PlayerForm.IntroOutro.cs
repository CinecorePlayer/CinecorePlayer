#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm : Form
    {
        private void LayoutIntroOutroPromptPanel()
        {
            try { _hud?.Invalidate(); } catch { }
        }

        private void ResetIntroOutroPlaybackState(string? path)
        {
            _currentIntroOutroMarkers = IntroOutroScanService.TryGetEpisodeMarkers(path);
            _skipIntroPromptConsumed = false;
            _nextEpisodePromptConsumed = false;
            SetIntroOutroPromptVisibility(false, false);

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && _currentIntroOutroMarkers != null)
                    Dbg.Log($"[INTROOUTRO] markers loaded for '{Path.GetFileName(path)}': intro={_currentIntroOutroMarkers.HasIntro}, outro={_currentIntroOutroMarkers.HasOutro}", Dbg.LogLevel.Info);
            }
            catch { }
        }

        private void UpdateIntroOutroPromptForPlayback()
        {
            try
            {
                if (_engine == null || _currentIntroOutroMarkers == null)
                {
                    SetIntroOutroPromptVisibility(false, false);
                    return;
                }

                bool blocked = IsAnyLibraryVisible()
                            || (_videoLoading?.Visible == true)
                            || IsPhotoMode
                            || _playingPreRoll
                            || _preOpenPlaceholderGateActive;

                if (blocked)
                {
                    SetIntroOutroPromptVisibility(false, false);
                    return;
                }

                double pos = Math.Max(0, _engine.PositionSeconds);
                var intro = _currentIntroOutroMarkers.Intro;
                var outro = _currentIntroOutroMarkers.Outro;
                double duration = ResolveIntroOutroPromptDuration(intro, outro);

                if (intro != null && pos < Math.Max(0, intro.StartSeconds - 5))
                    _skipIntroPromptConsumed = false;

                if (outro != null && pos < Math.Max(0, outro.StartSeconds - 10))
                    _nextEpisodePromptConsumed = false;

                double introPromptStart = intro != null ? Math.Max(0, intro.StartSeconds - 2.0) : double.MaxValue;

                bool showSkipIntro = intro != null
                    && !_skipIntroPromptConsumed
                    && intro.EndSeconds > intro.StartSeconds + 1
                    && pos >= introPromptStart
                    && pos < intro.EndSeconds - 0.25;

                bool hasNextEpisode = HasIntroOutroNextEpisode();
                double nextPromptStart = outro != null ? Math.Max(0, outro.StartSeconds - 2.0) : double.MaxValue;
                double nextPromptEnd = duration > 0
                    ? Math.Max(0, duration - 0.25)
                    : (outro?.EndSeconds ?? double.MaxValue);

                bool showNextEpisode = outro != null
                    && hasNextEpisode
                    && !_nextEpisodePromptConsumed
                    && outro.EndSeconds > outro.StartSeconds + 1
                    && pos >= nextPromptStart
                    && pos < nextPromptEnd;

                SetIntroOutroPromptVisibility(showSkipIntro, showNextEpisode);
            }
            catch
            {
                SetIntroOutroPromptVisibility(false, false);
            }
        }

        private double ResolveIntroOutroPromptDuration(IntroOutroSegment? intro, IntroOutroSegment? outro)
        {
            try
            {
                if (_duration > 0)
                    return _duration;

                double engineDuration = _engine?.DurationSeconds ?? 0;
                if (engineDuration > 0)
                    return engineDuration;
            }
            catch { }

            double markerMax = 0;
            if (intro != null)
                markerMax = Math.Max(markerMax, intro.EndSeconds);
            if (outro != null)
                markerMax = Math.Max(markerMax, outro.EndSeconds);
            return markerMax;
        }

        private bool HasIntroOutroNextEpisode()
        {
            try
            {
                if (CanSkipPlaybackQueue(1))
                    return true;

                string? nextPath = IntroOutroScanService.TryGetNextEpisodePath(_currentPath);
                return !string.IsNullOrWhiteSpace(nextPath) && File.Exists(nextPath);
            }
            catch
            {
                return false;
            }
        }

        private void SetIntroOutroPromptVisibility(bool showSkipIntro, bool showNextEpisode)
        {
            try
            {
                bool visible = showSkipIntro || showNextEpisode;
                try { _hud.SetIntroOutroPrompts(showSkipIntro, showNextEpisode); } catch { }

                if (visible)
                {
                    try { LogIntroOutroPromptState(showSkipIntro, showNextEpisode); } catch { }

                    try
                    {
                        _lastHudActivityUtc = DateTime.UtcNow;
                    }
                    catch { }

                    if (_useInlineOverlay && _overlayInlineHost != null)
                    {
                        _overlayInlineHost.Visible = true;
                        _overlayInlineHost.BringToFront();
                    }
                    else
                    {
                        try { _overlayHost.SetClickThrough(false); } catch { }
                        SafeShowOverlayHost();
                    }

                    try
                    {
                        _hud.TimelineVisible = _duration > 0;
                        _hud.Visible = true;
                        _hud.ShowOnce(3500);
                    }
                    catch { }

                    LayoutIntroOutroPromptPanel();
                    try { _hud.BringToFront(); } catch { }
                    try { _hud.Invalidate(true); } catch { }
                    try { _hud.Update(); } catch { }
                    if (_remoteOsd?.Visible == true)
                        _remoteOsd.BringToFront();
                }
                else
                {
                    try { LogIntroOutroPromptState(false, false); } catch { }

                    try
                    {
                        if (_useInlineOverlay && _overlayInlineHost != null)
                            _overlayInlineHost.Visible = HasVisibleOverlayHostContent();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void LogIntroOutroPromptState(bool showSkipIntro, bool showNextEpisode)
        {
            bool changed = showSkipIntro != _lastIntroOutroPromptSkipVisible ||
                           showNextEpisode != _lastIntroOutroPromptNextVisible;
            if (!changed)
                return;

            _lastIntroOutroPromptSkipVisible = showSkipIntro;
            _lastIntroOutroPromptNextVisible = showNextEpisode;

            var now = DateTime.UtcNow;
            if (now - _lastIntroOutroPromptLogUtc < TimeSpan.FromMilliseconds(350))
                return;

            _lastIntroOutroPromptLogUtc = now;

            double pos = 0;
            try { pos = _engine?.PositionSeconds ?? 0; } catch { }

            string parentName = string.Empty;
            try { parentName = _hud?.Parent?.GetType().Name ?? "none"; } catch { }

            Dbg.Log(
                $"[INTROOUTRO] HUD prompt skip={showSkipIntro}, next={showNextEpisode}, pos={pos:0.0}s, file='{Path.GetFileName(_currentPath ?? string.Empty)}', parent={parentName}, inline={_useInlineOverlay}, hudVisible={_hud?.Visible == true}",
                Dbg.LogLevel.Info);
        }

        private bool TrySkipDetectedIntro()
        {
            try
            {
                if (_engine == null || _currentIntroOutroMarkers?.Intro == null)
                    return false;

                double maxSeek = _duration > 0 ? _duration : Math.Max(_currentIntroOutroMarkers.Intro.EndSeconds + 1, 0.01);
                double target = Math.Clamp(_currentIntroOutroMarkers.Intro.EndSeconds + 0.25, 0, Math.Max(0.01, maxSeek));
                PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: target);
                _engine.PositionSeconds = target;
                _skipIntroPromptConsumed = true;
                SetIntroOutroPromptVisibility(false, false);
                HudBump(1200, allowWhenRemote: false, showTimeline: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryOpenNextEpisodeFromIntroOutroPrompt()
        {
            try
            {
                _nextEpisodePromptConsumed = true;
                SetIntroOutroPromptVisibility(false, false);

                if (TrySkipPlaybackQueue(1))
                    return true;

                string? nextPath = IntroOutroScanService.TryGetNextEpisodePath(_currentPath);
                if (string.IsNullOrWhiteSpace(nextPath) || !File.Exists(nextPath))
                    return false;

                _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(nextPath) ?? _currentLibraryCategory;
                _suppressPreRollOnce = true;
                _suppressVideoLoadingOnce = true;
                _endTriggered = true;
                _endCandidateSinceUtc = DateTime.MinValue;

                try { HideLibrary(); } catch { }
                OpenPath(nextPath, allowPlaceholderGate: false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void OnIntroOutroScanStatusChanged()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated)
                    return;

                TryBeginInvokeOnUi(() =>
                {
                    if (!string.IsNullOrWhiteSpace(_currentPath))
                        _currentIntroOutroMarkers = IntroOutroScanService.TryGetEpisodeMarkers(_currentPath);

                    try { UpdateIntroOutroPromptForPlayback(); } catch { }
                });
            }
            catch { }
        }

    }
}
