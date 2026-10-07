#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm : Form
    {
        private void TogglePipMode()
        {
            if (!ShouldAllowPipForCurrentMedia())
            {
                try { HidePipMode(dispose: false); } catch { }
                ExitPipDesktopFriendlyMode(restoreTopMost: true);
                return;
            }

            if (_pipForm?.Visible == true)
                RestoreStandardFromPip();
            else
                ShowPipMode();
        }

        private void ShowPipMode()
        {
            if (_engine == null || !ShouldAllowPipForCurrentMedia())
                return;

            if (_pipForm == null || _pipForm.IsDisposed)
            {
                _pipForm = new PipMiniPlayerForm(
                    stateProvider: BuildPipMiniState,
                    togglePlayPause: () =>
                    {
                        try { RunPipActionSilently(TogglePlayPause); } catch { }
                    },
                    previous: () =>
                    {
                        try
                        {
                            RunPipActionSilently(() =>
                            {
                                if (_info?.Chapters.Count > 0)
                                    SeekChapter(-1);
                                else
                                    TrySkipPlaybackQueue(-1);
                            });
                        }
                        catch { }
                    },
                    back10: () => RunPipActionSilently(() => SeekRelative(-10)),
                    next: () =>
                    {
                        try
                        {
                            RunPipActionSilently(() =>
                            {
                                if (_info?.Chapters.Count > 0)
                                    SeekChapter(1);
                                else
                                    TrySkipPlaybackQueue(1);
                            });
                        }
                        catch { }
                    },
                    forward10: () => RunPipActionSilently(() => SeekRelative(10)),
                    stopPlayback: () =>
                    {
                        try { StopPlaybackFromPip(); } catch { }
                    },
                    toggleShuffle: () => RunPipActionSilently(TogglePlaybackQueueShuffle),
                    toggleLoop: () => RunPipActionSilently(() =>
                        SetSingleTrackLoop(_currentPath, !IsSingleTrackLoopEnabledForPath(_currentPath))),
                    restoreStandard: () =>
                    {
                        try { RestoreStandardFromPip(); } catch { }
                    },
                    seekTo: seconds =>
                    {
                        try { SeekFromPip(seconds); } catch { }
                    },
                    toggleMute: () => RunPipActionSilently(() =>
                    {
                        if (TryNetworkVolume(toggleMute: true)) return;
                        if (_hud == null || IsBitstream()) return;
                        if (_hud.IsMuted)
                        {
                            float value = Math.Clamp(_remoteVolBeforeMute, .05f, 1f);
                            _hud.SetMuted(false);
                            ApplyVolume(value);
                            _hud.SetExternalVolume(value);
                        }
                        else
                        {
                            float value = _hud.GetVolume();
                            if (value > .001f) _remoteVolBeforeMute = value;
                            _hud.SetMuted(true);
                            ApplyVolume(0f);
                            _hud.SetExternalVolume(0f);
                        }
                    }),
                    setVolume: value => RunPipActionSilently(() =>
                    {
                        if (TryNetworkVolume(value: value)) return;
                        if (_hud == null || IsBitstream()) return;
                        value = Math.Clamp(value, 0f, 1f);
                        if (_hud.IsMuted && value > 0f) _hud.SetMuted(false);
                        if (value > .001f) _remoteVolBeforeMute = value;
                        ApplyVolume(value);
                        _hud.SetExternalVolume(value);
                    }),
                    syncVideoSurface: SyncPipVideoSurface,
                    forwardVideoSurfaceMessage: ForwardPipVideoSurfaceMessageToRenderer,
                    restoreVideoSurface: RestoreVideoFromPipSurface,
                    english: UiEnglish,
                    showLyrics: () => { RestoreStandardFromPip(); SetMusicWorkspaceView(2); });
            }

            try
            {
                EnterPipDesktopFriendlyMode();
                _pipShownUtc = DateTime.UtcNow;
                PrimePipArtworkLookup();
                PreparePipVideoMode();
                _pipForm.RefreshState();
                _pipForm.ShowIndependent(this);
                _pipForm.RefreshState();
                if (WindowState != FormWindowState.Minimized)
                    WindowState = FormWindowState.Minimized;
            }
            catch { }
            try { if (_mPipMode != null) _mPipMode.Checked = true; } catch { }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (!_pipModeActive || _pipForm?.Visible != true ||
                WindowState == FormWindowState.Minimized ||
                DateTime.UtcNow - _pipShownUtc < TimeSpan.FromMilliseconds(550))
                return;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (_pipModeActive && _pipForm?.Visible == true)
                        RestoreStandardFromPip();
                }));
            }
            catch { }
        }

        private void RunPipActionSilently(Action action)
        {
            try
            {
                BeginRemoteCommandScope();
                action();
            }
            finally
            {
                EndRemoteCommandScope();
            }
        }

        private void EnterPipDesktopFriendlyMode()
        {
            try
            {
                _pipModeActive = true;
                try { TopMost = false; } catch { }
                try
                {
                    _overlayHost?.SetClickThrough(true);
                    if (_overlayHost?.Visible == true)
                        _overlayHost.Hide();
                    if (_overlayHost != null)
                        _overlayHost.TopMost = false;
                }
                catch { }
                try { if (_overlayInlineHost != null) _overlayInlineHost.ClickThrough = true; } catch { }
            }
            catch { }
        }

        private void ExitPipDesktopFriendlyMode(bool restoreTopMost)
        {
            try
            {
                if (!_pipModeActive)
                    return;

                _pipModeActive = false;
                // Non ripristinare uno stato TopMost catturato prima del PiP: può
                // provenire dal fullscreen e sopravvivere al ritorno sul desktop.
                // La normale logica fullscreen potrà riapplicarlo solo su un nuovo
                // comando esplicito dell'utente.
                try { TopMost = false; } catch { }
                try { if (_overlayHost != null) _overlayHost.TopMost = false; } catch { }
            }
            catch { }
        }

        private void SeekFromPip(double seconds)
        {
            try
            {
                double duration = GetTimelineDurationSeconds();
                if (_engine == null || duration <= 0)
                    return;

                double target = Math.Max(0, Math.Min(seconds, Math.Max(0.01, duration)));
                PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: target);
                _engine.PositionSeconds = target;
                try { _hud.SetPreview(null, target); } catch { }
                try { PublishRemoteState(target); } catch { }
            }
            catch { }
        }

        private void StopPlaybackFromPip()
        {
            // Mantieni l'azione confinata nel PiP: stop non deve richiamare né
            // attivare la finestra principale.
            try { SafeStop(returnToHome: false, suppressActivation: true, disposeEngineAsync: true); } catch { }
            try { _pipForm?.RefreshState(); } catch { }
        }

        private void PreparePipVideoMode()
        {
            try
            {
                if (!ShouldUseVideoSurfaceInPip())
                    return;

                if (!_pipVideoExclusiveSuppressed)
                {
                    try { _engine?.TrySetMadVrExclusiveModeDisabled(true); } catch { }
                    _pipVideoExclusiveSuppressed = true;
                }
                try { _videoHost.Visible = false; } catch { }
            }
            catch { }
        }

        private void RestoreStandardFromPip()
        {
            try { HidePipMode(dispose: false); } catch { }
            // Il PiP deve restituire la superficie al player, non ripristinare una
            // vecchia politica TopMost. Ripristinarla qui faceva riemergere Cinecore
            // sopra finestre estranee anche molti secondi dopo l'uscita dal PiP.
            ExitPipDesktopFriendlyMode(restoreTopMost: false);
            try
            {
                TopMost = false;
                try { if (_overlayHost != null) _overlayHost.TopMost = false; } catch { }
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Show();
                Activate();
                if (_overlayHost != null && !_useInlineOverlay)
                    _overlayHost.SyncTo(this);
                SyncHudTimelineAvailability();
                BringOverlaysToFront();
                ResumePresentationAfterPip();
                if (_currentMediaHasVideo && !IsPhotoMode) HudBump(2500, true, true);
                SetModalInputState(false);
            }
            catch { }
        }

        private void ResumePresentationAfterPip()
        {
            try
            {
                if (_engine == null || _stopping)
                    return;

                if (_currentMediaHasVideo)
                {
                    UpdateVideoWindowForCurrentHost();
                    SyncOverlayToVideoRect();
                    return;
                }

                RefreshMusicPresentationForCurrentMedia();
                RestartAudioMetersAfterPip();
            }
            catch { }
        }

        private void RestartAudioMetersAfterPip()
        {
            int serial = Interlocked.Increment(ref _pipAudioMeterRestartSerial);

            try
            {
                _pipAudioMeterRestartTimer?.Stop();
                _pipAudioMeterRestartTimer?.Dispose();
                _pipAudioMeterRestartTimer = null;
            }
            catch { }

            // Non fidarti né dello stato "running" di WASAPI né del canvas Skia
            // conservato durante la minimizzazione: entrambi possono restare
            // formalmente validi senza produrre più frame.
            try { StopAudioMeters(asyncStop: false, resetPresentation: false); } catch { }
            try
            {
                _audioSampler?.Dispose();
                _audioSampler = null;
            }
            catch { }

            void Restart()
            {
                try
                {
                    if (serial != Volatile.Read(ref _pipAudioMeterRestartSerial) ||
                        IsDisposed || _stopping || _engine == null || _currentMediaHasVideo ||
                        _pipForm?.Visible == true)
                        return;

                    RecreateAudioMetersSurfaceAfterPip();
                    _audioMeters?.ResumeAfterHostRestore();
                    StartAudioMetersIfPossible();
                    RefreshMusicPresentationForCurrentMedia();
                    try { _audioMeters?.ResumeAfterHostRestore(); } catch { }
                }
                catch { }
            }

            // BeginInvoke da solo parte troppo presto: il Form puo' essere gia'
            // Normal ma i canvas figli non hanno ancora ricevuto il nuovo layout.
            // Un breve tick UI avvia la ricostruzione solo a ripristino concluso.
            try
            {
                var timer = new System.Windows.Forms.Timer { Interval = 240 };
                _pipAudioMeterRestartTimer = timer;
                timer.Tick += (_, __) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    if (ReferenceEquals(_pipAudioMeterRestartTimer, timer))
                        _pipAudioMeterRestartTimer = null;
                    Restart();
                };
                timer.Start();
            }
            catch { Restart(); }
        }

        private void HidePipMode(bool dispose)
        {
            try
            {
                if (_pipForm == null)
                    return;

                if (dispose)
                {
                    _pipForm.DestroyForAppExit();
                    _pipForm = null;
                }
                else
                {
                    _pipForm.Hide();
                }
            }
            catch { }
            _pipVideoExclusiveSuppressed = false;
            if (dispose)
                ExitPipDesktopFriendlyMode(restoreTopMost: false);
            try { if (_mPipMode != null) _mPipMode.Checked = false; } catch { }
        }

        private bool ShouldAllowPipForCurrentMedia()
        {
            try
            {
                if (_engine == null || string.IsNullOrWhiteSpace(_currentPath))
                    return false;

                if (IsPhotoMode || LooksLikeImageByExt(_currentPath))
                    return false;

                if (!_currentMediaHasVideo || LooksLikePureAudioByExt(_currentPath))
                    return true;

                // Film, episodi, video e musica sono tutti media validi per il PiP.
                // Solo foto/immagini non hanno una superficie di riproduzione utile.
                string? category = ResolveEffectiveLibraryCategoryForPath(_currentPath);
                if (string.Equals(category, "Foto", StringComparison.OrdinalIgnoreCase))
                    return false;

                return _currentMediaHasVideo || LooksLikeVideoByExt(_currentPath) || LooksLikePureAudioByExt(_currentPath);
            }
            catch { return false; }
        }

        private bool ShouldUseVideoSurfaceInPip()
        {
            try
            {
                return ShouldAllowPipForCurrentMedia() &&
                       _currentMediaHasVideo &&
                       !LooksLikePureAudioByExt(_currentPath ?? string.Empty) &&
                       !IsPhotoMode;
            }
            catch { return false; }
        }

        private static bool LooksLikeImageByExt(string path)
        {
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            return new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" }.Contains(ext);
        }

        private void SyncPipVideoSurface(nint ownerHwnd, Rectangle clientRect)
        {
            try
            {
                if (_engine == null || !ShouldUseVideoSurfaceInPip())
                    return;

                if (!_pipVideoExclusiveSuppressed)
                {
                    try { _engine.TrySetMadVrExclusiveModeDisabled(true); } catch { }
                    _pipVideoExclusiveSuppressed = true;
                }
                _engine.UpdateVideoWindow(ownerHwnd, clientRect);
                try { if (_videoHost.Visible) _videoHost.Visible = false; } catch { }
            }
            catch { }
        }

        private void ForwardPipVideoSurfaceMessageToRenderer(Message m)
        {
            if (!ShouldUseVideoSurfaceInPip()) return;
            if (!IsMpcvrActive) return;
            if (_engine is not DirectShowUnifiedEngine ds) return;

            const int WM_MOVE = 0x0003;
            const int WM_SIZE = 0x0005;
            const int WM_PAINT = 0x000F;
            const int WM_ERASEBKGND = 0x0014;
            const int WM_SHOWWINDOW = 0x0018;
            const int WM_WINDOWPOSCHANGED = 0x0047;
            const int WM_DISPLAYCHANGE = 0x007E;

            switch (m.Msg)
            {
                case WM_MOVE:
                case WM_SIZE:
                case WM_PAINT:
                case WM_ERASEBKGND:
                case WM_SHOWWINDOW:
                case WM_WINDOWPOSCHANGED:
                case WM_DISPLAYCHANGE:
                    try { ds.NotifyVideoOwnerMessage(m.HWnd, m.Msg, m.WParam, m.LParam); } catch { }
                    break;
            }
        }

        private void RestoreVideoFromPipSurface()
        {
            try
            {
                if (_engine == null || IsPhotoMode)
                    return;

                if (!_currentMediaHasVideo)
                {
                    ResumePresentationAfterPip();
                    return;
                }

                if (!_videoHost.Visible)
                    _videoHost.Visible = true;
                UpdateVideoWindowForCurrentHost();
                SyncOverlayToVideoRect();
            }
            catch { }
        }

        private void EnsurePipModeStillValidForCurrentMedia()
        {
            try
            {
                if (_pipForm?.Visible != true)
                    return;

                if (!ShouldAllowPipForCurrentMedia())
                    RestoreStandardFromPip();
            }
            catch { }
        }

        private PipMiniPlayerState BuildPipMiniState()
        {
            string title = "Cinecore Player";
            try
            {
                if (_preOpenPlaceholderGateActive && !string.IsNullOrWhiteSpace(_pendingPathAfterPlaceholderGate))
                    title = BuildBestDisplayTitleForPath(_pendingPathAfterPlaceholderGate!);
                else if (!string.IsNullOrWhiteSpace(_currentPath))
                    title = BuildBestDisplayTitleForPath(_currentPath!);

                if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(_currentPath))
                    title = Path.GetFileNameWithoutExtension(_currentPath) ?? "Cinecore Player";
            }
            catch { }

            double pos = 0;
            try { pos = _engine?.PositionSeconds ?? 0; } catch { }

            string status = _engine == null
                ? Tx("Nessuna riproduzione", "Nothing playing")
                : (_paused ? Tx("In pausa", "Paused") : Tx("In riproduzione", "Playing"));

            string subtitle = status;
            if (!_currentMediaHasVideo && _musicTransport != null)
            {
                if (!string.IsNullOrWhiteSpace(_musicTransport.TrackTitle)) title = _musicTransport.TrackTitle;
                subtitle = _musicTransport.TrackArtist;
            }

            int videoWidth = 0;
            int videoHeight = 0;
            try
            {
                videoWidth = _info?.Width ?? 0;
                videoHeight = _info?.Height ?? 0;
                if ((videoWidth <= 0 || videoHeight <= 0) && !string.IsNullOrWhiteSpace(_currentPath))
                {
                    var hinted = PlaybackTitleHints.GetDimensions(_currentPath);
                    videoWidth = hinted.Width;
                    videoHeight = hinted.Height;
                }
            }
            catch { }

            return new PipMiniPlayerState
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Cinecore Player" : title,
                Subtitle = subtitle,
                MediaPath = _currentPath,
                ArtworkPath = ResolvePipArtworkPath(_currentPath),
                IsPaused = _paused,
                HasPlayback = _engine != null,
                CanPrevious = _engine != null && (_playbackQueue.Count > 1 || _duration > 0),
                CanNext = _engine != null && (_playbackQueue.Count > 1 || _duration > 0),
                ShowVideo = ShouldUseVideoSurfaceInPip(),
                PositionSeconds = pos,
                DurationSeconds = Math.Max(0, GetTimelineDurationSeconds()),
                VideoWidth = videoWidth,
                VideoHeight = videoHeight,
                Volume = Math.Clamp(_hud?.GetVolume() ?? 1f, 0f, 1f),
                VolumeEditable = IsNetworkVolumeActive || !IsBitstream(),
                Muted = _hud?.IsMuted == true,
                ShuffleActive = _playbackQueueShuffleMode,
                LoopActive = IsSingleTrackLoopEnabledForPath(_currentPath)
            };
        }

        private void PrimePipArtworkLookup()
        {
            try
            {
                string? path = _currentPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !LooksLikePureAudioByExt(path))
                    return;

                if (!string.IsNullOrWhiteSpace(MusicArtworkService.GetCachedArtworkPath(path)))
                    return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await MusicArtworkService.ResolveArtworkAsync(path, CancellationToken.None).ConfigureAwait(false);
                        if (IsDisposed || _pipForm == null || _pipForm.IsDisposed)
                            return;
                        try { BeginInvoke(new Action(() => _pipForm?.RefreshState())); } catch { }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private string? ResolvePipArtworkPath(string? mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return null;

            try
            {
                if (File.Exists(mediaPath))
                {
                    string ext = Path.GetExtension(mediaPath).ToLowerInvariant();
                    if (new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(ext))
                        return mediaPath;

                    if (LooksLikePureAudioByExt(mediaPath))
                    {
                        string? musicArtwork = MusicArtworkService.GetCachedArtworkPath(mediaPath);
                        if (!string.IsNullOrWhiteSpace(musicArtwork) && File.Exists(musicArtwork))
                            return musicArtwork;
                    }

                    string? poster = MovieMetadataService.GetCachedPosterPath(mediaPath);
                    if (!string.IsNullOrWhiteSpace(poster) && File.Exists(poster))
                        return poster;

                    string? folder = Path.GetDirectoryName(mediaPath);
                    if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    {
                        foreach (var name in new[] { "cover.jpg", "cover.png", "folder.jpg", "folder.png", "album.jpg", "album.png" })
                        {
                            string candidate = Path.Combine(folder, name);
                            if (File.Exists(candidate))
                                return candidate;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private string? ResolveRemoteArtworkPath(string? mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return null;

            try
            {
                if (!LooksLikePureAudioByExt(mediaPath))
                {
                    string? backdrop = MovieMetadataService.GetCachedBackdropPath(mediaPath);
                    if (!string.IsNullOrWhiteSpace(backdrop) && File.Exists(backdrop))
                        return backdrop;
                }
            }
            catch { }

            return ResolvePipArtworkPath(mediaPath);
        }


    }
}
