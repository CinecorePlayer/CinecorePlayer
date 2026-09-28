#nullable enable
using System;
using System.Drawing;
using System.Windows.Forms;
using CinecorePlayer2025.HUD;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private MusicTransportBar? _musicTransport;
        private bool _musicWorkspaceActive;
        private bool _openingMusic;
        private bool _preserveMusicTransportOnOpen;
        private int _musicWorkspaceView; // 0 library, 1 analysis, 2 lyrics
        private bool HasMusicPlayback => _engine != null && !_currentMediaHasVideo && !IsPhotoMode;

        private void InitializeMusicWorkspace()
        {
            // HUD fade invalidations already supply animation frames. A permanent
            // 30 Hz timer wakes the UI even throughout hidden video playback.
            _hud.Invalidated += (_, _) => UpdateVideoVignette();
            _hud.VisibleChanged += (_, _) => UpdateVideoVignette();
            _hud.Invalidated += (_, _) => UpdateHudInputSurface();
            _hud.VisibleChanged += (_, _) => UpdateHudInputSurface();
            Disposed += (_, _) => _hudInputSurface?.Dispose();
            _settingsHudPage.AccentSelected += color => ApplyUiAccentColor(color, save: true);
            _musicTransport = new MusicTransportBar { Visible = false };
            _stack.Dock = DockStyle.None;
            _rootLayout.Controls.Add(_musicTransport);
            _musicTransport.Command += command =>
            {
                if (!HasMusicPlayback) return;
                switch (command)
                {
                    case "play": TogglePlayPause(); break;
                    case "stop": CloseCurrentToLibrary(); break;
                    case "previous": if (!TrySkipPlaybackQueue(-1)) SeekMusic(0); break;
                    case "next": TrySkipPlaybackQueue(1); break;
                    case "shuffle": TogglePlaybackQueueShuffle(); break;
                    case "repeat": SetSingleTrackLoop(_currentPath, !IsSingleTrackLoopEnabledForPath(_currentPath)); break;
                    case "mute": _hud.ToggleMuteFromUser(); break;
                    case "fullscreen": ToggleFullscreen(); break;
                    case "pip": TogglePipMode(); break;
                    case "favorite": if (_currentPath != null) _cinematicLibraryPage?.ToggleMusicTrackFavorite(_currentPath); break;
                    case "more": ShowMusicTransportMenu(); break;
                    case "queue": ShowPlaybackQueueEditor(); break;
                    case "library": SetMusicWorkspaceView(0); break;
                    case "analysis": SetMusicWorkspaceView(_musicWorkspaceView == 1 ? 0 : 1); break;
                    case "lyrics": SetMusicWorkspaceView(_musicWorkspaceView == 2 ? 0 : 2); break;
                }
                UpdateMusicTransport();
            };
            _audioMeters!.BackRequested += () => SetMusicWorkspaceView(0);
            _musicTransport.Seek += SeekMusic;
            _musicTransport.Volume += RemoteSetVolume;
            _audioMetersHost.MouseDown += (_, e) => { if (_musicWorkspaceView == 1 && !_audioMeters!.Bounds.Contains(e.Location)) SetMusicWorkspaceView(0); };
            _rootLayout.SizeChanged += (_, _) => UpdateMusicTransportHeight();
            UpdateMusicTransportHeight();
        }

        private void ShowMusicTransportMenu()
        {
            string? path = _currentPath;
            ChoicePopup.Show(this, PointToClient(Control.MousePosition), new[] {
                new ChoicePopup.Option(Tx("Aggiungi a playlist", "Add to playlist"), () => { if(path != null) { SetMusicWorkspaceView(0); _cinematicLibraryPage?.AddMusicTrackToPlaylist(path); } }),
                new ChoicePopup.Option(Tx("Schermo intero", "Fullscreen"), ToggleFullscreen),
                new ChoicePopup.Option(Tx("Interrompi riproduzione", "Stop playback"), CloseCurrentToLibrary)
            });
        }

        private void SeekMusic(double seconds)
        {
            if (!HasMusicPlayback || GetTimelineDurationSeconds() <= 0) return;
            double position = Math.Clamp(seconds, 0, GetTimelineDurationSeconds());
            SetTimelinePositionOverride(position);
            PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: position);
            _engine!.PositionSeconds = position;
            PublishRemoteState(position);
            _audioMeters?.UpdateLyricsPosition(position);
        }

        private void ActivateMusicWorkspace()
        {
            if (!HasMusicPlayback) return;
            if (!_musicWorkspaceActive) _musicWorkspaceView = 0;
            _musicWorkspaceActive = true;
            SetMusicWorkspaceView(_musicWorkspaceView);
        }

        private void DeactivateMusicWorkspace()
        {
            _musicWorkspaceActive = false;
            _preserveMusicTransportOnOpen = false;
            _musicWorkspaceView = 0;
            _cinematicLibraryPage?.SetMusicWorkspaceContent(null);
            _audioMetersHost.Parent = _stack;
            _audioMetersHost.Dock = DockStyle.Fill;
            ResetAudioOverlayState();
            if (_musicTransport != null) _musicTransport.Visible = false;
            UpdateMusicTransportHeight();
        }

        private void SetMusicWorkspaceView(int view)
        {
            if (!HasMusicPlayback) return;
            // Commit the entire page switch in one paint, after final bounds and
            // transport inset have been calculated.
            SetRedraw(_rootLayout, false);
            _rootLayout.SuspendLayout();
            try
            {
                _musicWorkspaceView = Math.Clamp(view, 0, 2);
                HideSettingsHudPage();
                _hud.Visible = false;
                _infoOverlay.Visible = false;
                HideExternalPlaybackOverlayHosts();
                if (view == 0)
                {
                    _cinematicLibraryPage?.SetMusicWorkspaceContent(null);
                    ShowCinematicLibrary(stopCurrent: false);
                    _audioMetersHost.Visible = false;
                    _audioOnlyBanner.Visible = false;
                }
                else
                {
                    _audioOnlyBanner.Visible = false;
                    _audioMeters!.ShowWorkspace(view == 2);
                    _audioMeters.Visible = true;
                    EnsureCinematicLibraryPageCreated();
                    _cinematicLibraryPage!.Enabled = true;
                    _cinematicLibraryPage.Visible = true;
                    _cinematicLibraryPage.SetMusicWorkspaceContent(_audioMetersHost);
                    _cinematicLibraryPage.BringToFront();
                    LayoutAudioMetersHost();
                    EnsureAudioMetricsTimer();
                    if (_audioSampler == null || (!IsBitstream() && !_audioSampler.IsRunning)) StartAudioMetersIfPossible();
                    DrainAudioMetrics();
                }
                UpdateMusicTransport();
                EnsureCursorVisible();
            }
            finally
            {
                _rootLayout.ResumeLayout(true);
                SetRedraw(_rootLayout, true);
                UpdateMusicTransport();
                _rootLayout.Invalidate(true);
            }
        }

        private void UpdateMusicTransportHeight()
        {
            if (_musicTransport == null) return;
            // Visible includes the parent's state, and is false during a window resize/show.
            // Layout must use playback state so that a collapsed row can always reopen.
            bool show = (HasMusicPlayback || _preserveMusicTransportOnOpen) && _musicWorkspaceActive && !_pipModeActive;
            _musicTransport.Width = _rootLayout.ClientSize.Width;
            int height = show ? _musicTransport.PreferredBarHeight : 0;
            int width = _rootLayout.ClientSize.Width;
            int top = Math.Max(0, _rootLayout.ClientSize.Height - height);
            bool overLibrary = show && _cinematicLibraryPage?.Visible == true;
            Control parent = overLibrary ? _cinematicLibraryPage! : _rootLayout;
            if (_musicTransport.Parent != parent) _musicTransport.Parent = parent;
            if (_cinematicLibraryPage != null)
            {
                _cinematicLibraryPage.MusicTransportInset = overLibrary ? height : 0;
                _cinematicLibraryPage.GlassArtworkPath = show ? _musicTransport.ArtworkPath : null;
            }
            var content = new Rectangle(0, 0, width, overLibrary ? _rootLayout.ClientSize.Height : top);
            if (_stack.Bounds != content) _stack.Bounds = content;
            var transport = new Rectangle(0, Math.Max(0,parent.ClientSize.Height-height), parent.ClientSize.Width, height);
            if (_musicTransport.Bounds != transport) _musicTransport.Bounds = transport;
            if (show) _musicTransport.BringToFront();
        }

        private void UpdateMusicTransport()
        {
            if (_musicTransport == null) return;
            if (_engine != null && _currentMediaHasVideo) _musicWorkspaceActive = false;
            bool show = (HasMusicPlayback || _preserveMusicTransportOnOpen) && _musicWorkspaceActive && !_pipModeActive;
            _musicTransport.Expanded = _musicWorkspaceView == 0;
            UpdateMusicTransportHeight();
            _musicTransport.Visible = show;

            if (!show || _engine == null) return;
            _hud.Visible = false;
            _musicTransport.SetLanguage(_uiLanguage == "en");
            _musicTransport.SetFavorite(_currentPath != null && _cinematicLibraryPage?.IsMusicTrackFavorite(_currentPath) == true);
            UpdateMusicTransportProgress();
        }

        private void UpdateMusicTransportProgress()
        {
            if (_musicTransport == null || !HasMusicPlayback || !_musicWorkspaceActive || !_musicTransport.Visible)
                return;

            _musicTransport.UpdatePlayback(GetTimelinePositionForHud(), GetTimelineDurationSeconds(), !_paused,
                _hud.GetVolume(), _hud.IsMuted, IsBitstream() && !IsNetworkVolumeActive, _playbackQueueShuffleMode,
                IsSingleTrackLoopEnabledForPath(_currentPath), _musicWorkspaceView);
        }
    }
}
