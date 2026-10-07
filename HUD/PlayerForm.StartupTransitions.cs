#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private bool _deferLibraryHideUntilPlaybackReady;
        private bool _deferredPlaybackSourceWasNetflix;
        private bool _deferredPlaybackSourceWasCinematic;
        private bool _remoteServicesStarted;

        internal void PrepareFirstFrame()
        {
            try
            {
                CreateControl();
                PerformLayout();
                EnsureCinematicLibraryPageCreated();
                if (_cinematicLibraryPage != null)
                {
                    _cinematicLibraryPage.CreateControl();
                    _cinematicLibraryPage.PerformLayout();
                    _cinematicLibraryPage.PrepareInitialContentForFirstFrame();
                    _cinematicLibraryPage.Invalidate();
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("First-frame preparation failed: " + ex.Message);
            }
        }

        private void ScheduleRemoteServicesAfterFirstFrame()
        {
            if (_remoteServicesStarted || _closingForExit)
                return;

            var timer = new System.Windows.Forms.Timer { Interval = 180 };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                timer.Dispose();
                if (_remoteServicesStarted || _closingForExit)
                    return;

                _remoteServicesStarted = true;
                // Listener HTTP, probing delle porte e mDNS possono impiegare qualche
                // secondo. Non devono mai occupare il thread della finestra dopo che il
                // caricamento visivo è già terminato.
                _ = Task.Run(() =>
                {
                    try
                    {
                        _remote?.Start();
                        _remoteCompanionSyncReady = true;
                        if (!_closingForExit && !IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                // La libreria completa del telefono viene costruita su
                                // un worker. Il vecchio refresh sincrono bloccava tutti i
                                // click per alcuni secondi subito dopo il primo frame.
                                try { QueueRemoteCompanionRefresh(); } catch { }
                            }));
                        }
                    }
                    catch (Exception ex) { Dbg.Error("Remote.Start FAILED: " + ex.Message); }
                });
            };
            timer.Start();
        }

        private void BeginLibraryToPlaybackTransition(bool enterFullscreen = true)
        {
            if (!enterFullscreen)
            {
                _openingMusic = true;
                _deferLibraryHideUntilPlaybackReady = false;
                _deferredPlaybackSourceWasNetflix = _deferredPlaybackSourceWasCinematic = false;
                _hud.Visible = false;
                HideExternalPlaybackOverlayHosts();
                CancelVideoLoadingUiImmediate(releasePointerCapture: true);
                return;
            }
            bool wasMusicWorkspace = _musicWorkspaceActive;
            _openingMusic = false;
            DeactivateMusicWorkspace();
            _lyricsService.SetVideoPlaybackActive(true);
            try { HidePairingBanner(); } catch { }
            _deferredPlaybackSourceWasNetflix = _netflixModePage?.Visible == true;
            _deferredPlaybackSourceWasCinematic = _cinematicLibraryPage?.Visible == true || wasMusicWorkspace;
            _deferLibraryHideUntilPlaybackReady = _deferredPlaybackSourceWasNetflix || _deferredPlaybackSourceWasCinematic;
            ShowVideoLoading(Tx("Preparo la riproduzione…", "Preparing playback…"));
            if (_deferLibraryHideUntilPlaybackReady)
            {
                try
                {
                    // Present an opaque frame BEFORE changing the native window's
                    // geometry. DWM must never scale the outgoing library frame.
                    if (_videoLoading != null)
                    {
                        _videoLoading.Visible = true;
                        _videoLoading.BringToFront();
                        _videoLoading.Refresh();
                    }
                    // Usa una sola superficie figlia del PlayerForm. La precedente cover
                    // top-level si sovrapponeva al mask interno, poteva apparire traslata
                    // durante i cambi di geometria e, se rimasta viva, intercettava ogni
                    // click diretto alla libreria e perfino alla chrome della finestra.
                    if (_cinematicLibraryPage != null)
                    {
                        _cinematicLibraryPage.Enabled = false;
                        _cinematicLibraryPage.Visible = false;
                        _cinematicLibraryPage.SendToBack();
                    }
                    if (_netflixModePage != null)
                    {
                        _netflixModePage.Visible = false;
                        _netflixModePage.SuspendBackgroundWork();
                        _netflixModePage.SendToBack();
                    }

                    _videoHost.Visible = true;
                    _videoHost.BringToFront();
                    _videoLoading?.BringToFront();
                    _videoLoading?.Refresh();

                    // Il player video usa comunque la superficie borderless. Esegui il
                    // resize prima del primo paint dello spinner: farlo con il mask gia'
                    // animato lasciava a DWM per un frame sia il vecchio centro sia il
                    // nuovo, dando l'impressione di due caricamenti in diagonale.
                    if (enterFullscreen && FormBorderStyle != FormBorderStyle.None)
                        ToggleFullscreen();

                    ShowVideoLoading(Tx("Preparo la riproduzione…", "Preparing playback…"));
                    _videoLoading?.BringToFront();
                    _videoLoading?.Refresh();
                }
                catch { }
            }
        }

        private void ShowPlaybackTransitionCover(string message)
        {
            // Compatibilità con i vecchi call-site: la transizione ora è dipinta
            // esclusivamente dal VideoLoadingMask interno al form.
            try { _videoLoading?.SetMessage(message); } catch { }
        }

        private void UpdatePlaybackTransitionCover(string message)
        {
            try { _videoLoading?.SetMessage(message); } catch { }
        }

        private void HidePlaybackTransitionCover()
        {
            // Non esiste più una finestra di cover separata. Il metodo resta per non
            // duplicare la gestione del mask nei percorsi di apertura già esistenti.
        }

        private void RunAfterLoadingFrame(Action action)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || _closingForExit)
                        return;
                    // A second message-loop boundary guarantees that the opaque mask
                    // has actually been presented before resolver/renderer work starts.
                    BeginInvoke(new Action(() =>
                    {
                        if (!IsDisposed && !_closingForExit)
                            action();
                    }));
                }));
            }
            catch
            {
                action();
            }
        }

        private bool CaptureDeferredLibraryTransition()
        {
            bool defer = _deferLibraryHideUntilPlaybackReady;
            _deferLibraryHideUntilPlaybackReady = false;
            return defer;
        }

        private void CompleteLibraryToPlaybackTransition(bool deferred)
        {
            if (HasMusicPlayback)
            {
                _deferredPlaybackSourceWasNetflix = false;
                _deferredPlaybackSourceWasCinematic = false;
                return;
            }
            if (!deferred)
                return;

            try { HideCinematicLibraryForPlayback(); } catch { }
            try
            {
                _videoHost.Visible = true;
                _videoHost.BringToFront();
                UpdateVideoWindowForCurrentHost();
                if (_videoLoading?.Visible != true)
                {
                    SafeShowOverlayHost();
                    SyncOverlayToVideoRect();
                }
                BringOverlaysToFront();
            }
            catch { }

            _deferredPlaybackSourceWasNetflix = false;
            _deferredPlaybackSourceWasCinematic = false;
        }

        private void RestoreLibraryAfterFailedTransition(bool deferred)
        {
            if (!deferred || _engine != null)
                return;

            try
            {
                HideVideoLoading();
                if (_deferredPlaybackSourceWasNetflix)
                {
                    ShowNetflixMode();
                }
                else if (_deferredPlaybackSourceWasCinematic && _cinematicLibraryPage != null)
                {
                    _cinematicLibraryPage.Visible = true;
                    _cinematicLibraryPage.Enabled = true;
                    _cinematicLibraryPage.BringToFront();
                    _cinematicLibraryPage.Invalidate();
                }
            }
            catch { }
            finally
            {
                _deferredPlaybackSourceWasNetflix = false;
                _deferredPlaybackSourceWasCinematic = false;
            }
        }

    }
}
