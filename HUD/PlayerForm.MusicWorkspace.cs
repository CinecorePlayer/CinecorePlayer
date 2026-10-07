#nullable enable
using System;
using System.Drawing;
using System.Linq;
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
            _musicTransport.Volume += value => RemoteSetVolume(value, osd: false);
            _audioMetersHost.MouseDown += (_, e) => { if (_musicWorkspaceView == 1 && !_audioMeters!.Bounds.Contains(e.Location)) SetMusicWorkspaceView(0); };
            _rootLayout.SizeChanged += (_, _) => UpdateMusicTransportHeight();
            UpdateMusicTransportHeight();
        }

        private void ShowMusicTransportMenu()
        {
            string? path = _currentPath;
            bool favorite = path != null && _cinematicLibraryPage?.IsMusicTrackFavorite(path) == true;
            bool fullscreen = FormBorderStyle == FormBorderStyle.None;
            // La dissolvenza tra i brani la fa il Cinecore Audio Engine in uscita condivisa: con un altro
            // motore (musica da rete, mpv) o con l'uscita esclusiva la voce non compare.
            bool crossfadePossible = _engine is Audio.CinecoreAudioEngine { ExclusiveActive: false };
            ChoicePopup.Show(this, PointToClient(Control.MousePosition), new ChoicePopup.Option?[] {
                new ChoicePopup.Option(Tx("Aggiungi a playlist", "Add to playlist"), () => { if(path != null) { SetMusicWorkspaceView(0); _cinematicLibraryPage?.AddMusicTrackToPlaylist(path); } }, Icon: "playlist-add"),
                new ChoicePopup.Option(favorite ? Tx("Rimuovi dai preferiti", "Remove from favorites") : Tx("Aggiungi ai preferiti", "Add to favorites"),
                    () => { if (path != null) { _cinematicLibraryPage?.ToggleMusicTrackFavorite(path); UpdateMusicTransport(); } }, Icon: "star"),
                new ChoicePopup.Option(Tx("Gestisci coda", "Manage queue"), ShowPlaybackQueueEditor, Icon: "queue"),
                // Radio: a coda finita il player prosegue con brani simili della libreria.
                new ChoicePopup.Option(Tx("Radio: brani simili a fine coda", "Radio: similar tracks after the queue"),
                    () =>
                    {
                        _musicRadioEnabled = !_musicRadioEnabled;
                        try { SaveExtrasConfig(); } catch { }
                        _lblStatus.Text = _musicRadioEnabled ? Tx("Radio attiva", "Radio on") : Tx("Radio disattivata", "Radio off");
                    }, Selected: _musicRadioEnabled, Icon: "wave"),
                crossfadePossible ? new ChoicePopup.Option(CrossfadeMenuLabel(), CycleCrossfade, Selected: Audio.CinecoreAudioSettings.Current.CrossfadeSeconds > 0, Icon: "wave") : null,
                new ChoicePopup.Option(Tx("Mini player", "Mini player"), TogglePipMode, Icon: "pip"),
                new ChoicePopup.Option(fullscreen ? Tx("Esci da schermo intero", "Exit fullscreen") : Tx("Schermo intero", "Fullscreen"), ToggleFullscreen, Icon: "maximize"),
                new ChoicePopup.Option(Tx("Interrompi riproduzione", "Stop playback"), CloseCurrentToLibrary, Icon: "stop", Danger: true)
            }.Where(option => option != null).Select(option => option!).ToArray(), width: 300, above: true);
        }

        private void SeekMusic(double seconds)
        {
            if (!HasMusicPlayback || GetTimelineDurationSeconds() <= 0) return;
            double position = Math.Clamp(seconds, 0, GetTimelineDurationSeconds());
            SetTimelinePositionOverride(position);
            PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: position);
            _engine!.PositionSeconds = position;
            PublishRemoteState(position);
            ResetLyricsClock(position);
            _audioMeters?.UpdateLyricsPosition(position);
        }

        private void ActivateMusicWorkspace()
        {
            if (!HasMusicPlayback) return;
            if (!_musicWorkspaceActive) _musicWorkspaceView = 0;
            _musicWorkspaceActive = true;
            // Chiamata a ogni brano che parte: se l'utente e' nelle impostazioni ci resta
            // (prima il cambio di brano lo riportava alla pagina in cui era prima).
            SetMusicWorkspaceView(_musicWorkspaceView, keepSettings: _settingsHudPage?.Visible == true);
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
            if (_musicTransport != null) HideMusicTransportAnimated();
            UpdateMusicTransportHeight();
        }

        private void SetMusicWorkspaceView(int view, bool keepSettings = false)
        {
            if (!HasMusicPlayback) return;
            // Commit the entire page switch in one paint, after final bounds and
            // transport inset have been calculated.
            SetRedraw(_rootLayout, false);
            _rootLayout.SuspendLayout();
            try
            {
                _musicWorkspaceView = Math.Clamp(view, 0, 2);
                if (!keepSettings) HideSettingsHudPage();
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
                if (keepSettings && _settingsHudPage?.Visible == true) _settingsHudPage.BringToFront();
                UpdateMusicTransport();
                EnsureCursorVisible();
            }
            finally
            {
                _rootLayout.ResumeLayout(true);
                SetRedraw(_rootLayout, true);
                UpdateMusicTransport();
                _rootLayout.Invalidate(true);
                // La coda aperta resta sopra la nuova vista e il vetro riprende la pagina nuova.
                _cinematicLibraryPage?.KeepQueueEditorOnTop(refreshBackdrop: true);
            }
        }

        /// <summary>La barra esce con la sua animazione invece di sparire di colpo.</summary>
        private void HideMusicTransportAnimated()
        {
            if (_musicTransport == null || _musicTransport.IsHiding) return;
            // La pagina riprende subito tutto lo spazio: lo sfondo che la barra cattura per
            // l'uscita e' gia' quello definitivo e alla fine non cambia nulla sotto di lei.
            if (_cinematicLibraryPage != null) _cinematicLibraryPage.MusicTransportInset = 0;
            _musicTransport.PlayHide(() =>
            {
                if (_musicTransport == null) return;
                bool show = (HasMusicPlayback || _preserveMusicTransportOnOpen) && _musicWorkspaceActive && !_pipModeActive;
                if (!show) _musicTransport.Visible = false;
                UpdateMusicTransportHeight();
            });
        }

        private void UpdateMusicTransportHeight()
        {
            if (_musicTransport == null) return;
            if (_musicTransport.IsHiding)
            {
                // Durante l'uscita la barra resta dov'e' (stessa posizione e genitore); la pagina
                // riprende subito tutto lo spazio.
                if (_cinematicLibraryPage != null) _cinematicLibraryPage.MusicTransportInset = 0;
                var full = new Rectangle(0, 0, _rootLayout.ClientSize.Width, _rootLayout.ClientSize.Height);
                if (_stack.Bounds != full) _stack.Bounds = full;
                _musicTransport.BringToFront();
                return;
            }
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

        // Fine della musica con la libreria gia' a schermo: l'utente resta dov'e'. Rinavigare
        // la categoria azzerava scorrimento e filtri e ridisegnava tutta la pagina.
        private void RestoreLibraryInPlaceAfterMusicStop()
        {
            _returnToCinematicHomeOnPlaybackExit = false;
            _returnToCinematicWebSourceOnPlaybackExit = null;
            var page = _cinematicLibraryPage;
            if (page == null || page.IsDisposed) { ShowLibraryAfterPlaybackExit(); return; }
            try { page.SetMusicWorkspaceContent(null); } catch { }
            page.Enabled = true;
            if (!page.Visible) page.Visible = true;
            _hud.Visible = false;
            _infoOverlay.Visible = false;
            _dpadRoot = page;
            UpdateMusicTransport();
            try { RestoreLibraryContextMenuAfterPlayback(); } catch { }
        }

        private bool MusicPlayingInLibrary => HasMusicPlayback && _musicWorkspaceActive && !_pipModeActive &&
            _cinematicLibraryPage?.Visible == true && _settingsHudPage?.Visible != true;

        private bool _musicTransportRevealed;

        private void UpdateMusicTransport()
        {
            if (_musicTransport == null) return;
            if (_engine != null && _currentMediaHasVideo) _musicWorkspaceActive = false;
            bool show = (HasMusicPlayback || _preserveMusicTransportOnOpen) && _musicWorkspaceActive && !_pipModeActive;
            _musicTransport.Expanded = _musicWorkspaceView == 0;
            // L'uscita va avviata prima del layout: il layout di una barra non piu' richiesta
            // la porta ad altezza zero e l'animazione non avrebbe piu' nulla da mostrare
            // (la barra spariva di colpo).
            if (!show && _musicTransport.Visible) HideMusicTransportAnimated();
            UpdateMusicTransportHeight();
            bool appearing = show && !_musicTransport.Visible && !_musicTransportRevealed;
            if (show)
            {
                _musicTransport.CancelHide();
                if (!_musicTransport.Visible) _musicTransport.Visible = true;
            }
            else if (_musicTransport.Visible) HideMusicTransportAnimated();
            if (appearing) { _musicTransportRevealed = true; _musicTransport.PlayReveal(); }
            // Ogni volta che la barra sparisce, alla prossima comparsa rientra con la sua animazione
            // (prima solo al primo brano: poi compariva di colpo).
            if (!show) _musicTransportRevealed = false;

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
