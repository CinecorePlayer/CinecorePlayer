#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private void UpdateVideoWindowForCurrentHost()
        {
            if (_engine == null) return;
            try
            {
                if (_videoDetachedForPausePlaceholder)
                {
                    // Mantieni il dest rect “reale” (videoHost) per non rompere l’allineamento HUD.
                    if (!_videoDetachHost.IsHandleCreated) _videoDetachHost.CreateControl();
                    _engine.UpdateVideoWindow(_videoDetachHost.Handle, _videoHost.ClientRectangle);
                }
                else
                {
                    _engine.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                }

                ForceMpcvrChildWindowsVisible("UpdateVideoWindow");
                RaiseMpcvrChildOverlayHost();
            }
            catch { }
        }

        private void RefreshMpcvrHostAfterWindowModeChange(string reason)
        {
            if (!IsMpcvrActive || _engine == null || !_currentMediaHasVideo) return;

            void Refresh(string phase)
            {
                if (!IsMpcvrActive || _engine == null || !_currentMediaHasVideo) return;
                try { UpdateVideoWindowForCurrentHost(); } catch { }
                try { ForceMpcvrChildWindowsVisible(phase); } catch { }
                try { SyncOverlayToVideoRect(); } catch { }
                try { BringOverlaysToFront(); } catch { }

            }

            Refresh(reason);

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try { Refresh(reason + " deferred"); } catch { }
                }));
            }
            catch { }

            var settle = new System.Windows.Forms.Timer { Interval = 140 };
            settle.Tick += (_, __) =>
            {
                try { Refresh(reason + " settle"); }
                catch { }
                finally { settle.Stop(); settle.Dispose(); }
            };
            settle.Start();
        }

        private void DetachVideoForPausePlaceholder(bool detach)
        {
            if (_engine == null)
            {
                _videoDetachedForPausePlaceholder = false;
                return;
            }

            if (detach)
            {
                if (_videoDetachedForPausePlaceholder) return;
                _videoDetachedForPausePlaceholder = true;

                try { if (!_videoDetachHost.IsHandleCreated) _videoDetachHost.CreateControl(); } catch { }
                try { _videoHost.Visible = false; } catch { }

                // Re-parent su host invisibile (così il placeholder resta sopra anche con renderer aggressivi)
                UpdateVideoWindowForCurrentHost();
            }
            else
            {
                if (!_videoDetachedForPausePlaceholder) return;
                _videoDetachedForPausePlaceholder = false;

                try { _videoHost.Visible = true; } catch { }
                UpdateVideoWindowForCurrentHost();
            }
        }

        private void ShowPausePlaceholderNow()
        {
            try
            {
                if (!_pausePlaceholderEnabled || _engine == null || IsPhotoMode || _audioOnlyBanner.Visible || !ShouldUseCinemaFeaturesForPath(_currentPath))
                {
                    HidePausePlaceholderNow();
                    return;
                }

                // refresh immagine ad ogni pausa
                if (!string.IsNullOrWhiteSpace(_pausePlaceholderPath) && File.Exists(_pausePlaceholderPath))
                    _pausePlaceholder.ShowPlaceholder(_pausePlaceholderPath);
                else
                    _pausePlaceholder.ShowRandomPlaceholder();

                _pausePlaceholder.Visible = true;
                DetachVideoForPausePlaceholder(true);
                BringOverlaysToFront();
            }
            catch { }
        }

        private void HidePausePlaceholderNow()
        {
            try { if (_pausePlaceholder != null) _pausePlaceholder.Visible = false; } catch { }
            try { DetachVideoForPausePlaceholder(false); } catch { }
        }

        private async Task ShowPreOpenPlaceholderGateAsync(string pathToOpen, double resume, bool startPaused, CancellationToken ct)
        {
            bool gateShown = false;
            try
            {
                _preOpenPlaceholderGateActive = true;
                _pendingPathAfterPlaceholderGate = pathToOpen;
                _pendingResumeAfterPlaceholderGate = resume;
                _pendingStartPausedAfterPlaceholderGate = startPaused;

                try { CancelPlaceholderBackdropFetch(); } catch { }

                PreOpenPlaceholderVisual visual;
                try
                {
                    visual = await BuildPreOpenPlaceholderVisualAsync(pathToOpen, ct);
                }
                catch (OperationCanceledException)
                {
                    HidePreOpenPlaceholderGate(clearPending: true);
                    return;
                }

                if (ct.IsCancellationRequested)
                {
                    HidePreOpenPlaceholderGate(clearPending: true);
                    return;
                }

                try
                {
                    _pausePlaceholder.Caption = string.Empty;
                    _pausePlaceholder.DrawCaptionAlways = false;
                    _pausePlaceholder.SetFolder(_pausePlaceholderFolder);
                    _pausePlaceholder.TitleText = visual.TitleText ?? string.Empty;
                    // La dicitura compare solo se il film partira' davvero con madVR.
                    bool madVrFirst = false;
                    try { madVrFirst = BuildRendererOpenOrder(fileHdr: false).FirstOrDefault() == VRChoice.MADVR; } catch { }
                    _pausePlaceholder.SubtitleText = madVrFirst ? "powered by madVR" : string.Empty;
                    // Disco visto in parte: la schermata chiede se riprendere, come un lettore da tavolo.
                    if (_gateDiscResumeSeconds > 0)
                    {
                        _pausePlaceholder.SubtitleText = Tx("Ti eri fermato a ", "You stopped at ") + Fmt(_gateDiscResumeSeconds);
                        _pausePlaceholder.Choices = new[] { Tx("Riprendi", "Resume"), Tx("Ricomincia", "Start over") };
                        _pausePlaceholder.SelectedChoice = 0;
                    }
                    else _pausePlaceholder.Choices = null;
                    _pausePlaceholder.ShowBranding = visual.ShowBranding;
                    _pausePlaceholder.UseCoverImage = visual.UseCover;
                    _pausePlaceholder.SetBrandLogo(_placeholderBrandLogo);

                    if (!string.IsNullOrWhiteSpace(visual.ImagePath) && File.Exists(visual.ImagePath))
                        _pausePlaceholder.ShowPlaceholder(visual.ImagePath);
                    else
                        _pausePlaceholder.ClearDisplayedImage();
                }
                catch { }

                try { _pausePlaceholder.Visible = true; gateShown = true; } catch { }

                // HUD e Info overlay non devono stare sopra al placeholder
                try { _hud.Visible = false; _hud.TimelineVisible = false; } catch { }
                try { _infoOverlay.Visible = false; } catch { }
                SuppressHudForProgrammaticTransition(1200);

                try { SafeShowOverlayHost(); } catch { }
                try { SyncOverlayToVideoRect(); } catch { }
                try { BringOverlaysToFront(); } catch { }
            }
            catch
            {
                if (!gateShown)
                {
                    try { HidePreOpenPlaceholderGate(clearPending: true); } catch { }
                }
            }
        }

        private void HidePreOpenPlaceholderGate(bool clearPending)
        {
            try
            {
                try { CancelPlaceholderBackdropFetch(); } catch { }

                _preOpenPlaceholderGateActive = false;

                if (clearPending)
                {
                    _pendingPathAfterPlaceholderGate = null;
                    _pendingResumeAfterPlaceholderGate = 0;
                    _pendingStartPausedAfterPlaceholderGate = false;
                }

                if (_pausePlaceholder != null)
                {
                    try { _pausePlaceholder.Visible = false; } catch { }
                    try
                    {
                        _pausePlaceholder.Caption = Tx("PAUSA", "PAUSED");
                        _pausePlaceholder.DrawCaptionAlways = false;
                        _pausePlaceholder.TitleText = string.Empty;
                        _pausePlaceholder.SubtitleText = string.Empty;
                        _pausePlaceholder.Choices = null;
                        _pausePlaceholder.Cursor = Cursors.Default;
                        _pausePlaceholder.ShowBranding = false;
                        _pausePlaceholder.UseCoverImage = false;
                        _pausePlaceholder.SetBrandLogo(null);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Secondi salvati del disco che la schermata sta proponendo di riprendere; 0 se non chiede nulla.
        private double _gateDiscResumeSeconds;
        // Risposta data sulla schermata: vero riprendi, falso ricomincia, null se non e' stato chiesto.
        private bool? _discResumeAnswer;

        /// <summary>Frecce sulla schermata che chiede Riprendi o Ricomincia. Vero se c'era una scelta da spostare.</summary>
        private bool MoveGateChoice(int delta)
        {
            if (!_preOpenPlaceholderGateActive || _pausePlaceholder?.Choices is not { Length: > 0 } choices) return false;
            _pausePlaceholder.SelectedChoice = Math.Clamp(_pausePlaceholder.SelectedChoice + delta, 0, choices.Length - 1);
            _pausePlaceholder.Invalidate();
            return true;
        }

        private bool TryConsumePreOpenPlaceholderGate(bool startNow, bool fromRemote)
        {
            if (!_preOpenPlaceholderGateActive) return false;

            string? p = _pendingPathAfterPlaceholderGate;
            double resume = _pendingResumeAfterPlaceholderGate;
            Dbg.Log($"[VIDEO] Pre-open placeholder gate: {(startNow ? "start" : "cancel")}, remote={fromRemote}, pending={!string.IsNullOrWhiteSpace(p)}");
            // La risposta alla domanda sul disco viaggia con l'apertura vera e propria.
            _discResumeAnswer = startNow && _gateDiscResumeSeconds > 0 ? _pausePlaceholder?.SelectedChoice != 1 : null;
            _gateDiscResumeSeconds = 0;

            HidePreOpenPlaceholderGate(clearPending: true);

            if (!startNow) return true;

            if (!string.IsNullOrWhiteSpace(p))
            {
                // Mantieni la libreria esclusa durante l'apertura effettiva e usa la
                // normale maschera nera: niente ritorni intermedi alla home.
                _deferLibraryHideUntilPlaybackReady = true;
                _deferredPlaybackSourceWasNetflix = false;
                _deferredPlaybackSourceWasCinematic = false;
                SuppressHudForProgrammaticTransition(1200);

                // Quando parte davvero: bypassa il gate per evitare loop.
                OpenPath(p, resume, startPaused: false, allowPlaceholderGate: false);
            }

            return true;
        }

        private void TogglePlayPause()
        {
            // Se è attivo il placeholder gate pre-film, Play deve far partire il contenuto pendente.
            if (TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: IsRemoteCommandActive))
                return;

            if (_engine == null) return;

            // Play chiude la scansione veloce: se il film stava andando riprende e basta, senza mettersi in pausa.
            if (IsRemoteScanActive)
            {
                StopRemoteScan();
                if (!_paused) return;
            }
            _paused = !_paused;

            if (_paused)
            {
                // ferma il sampler, azzera l'integrazione
                _lastPktSample = DateTime.MinValue;
                Volatile.Write(ref _packetRateSampleActive, 0);
                _avgLastTs = DateTime.MinValue;

                // fai decadere gentilmente i valori per non vedere "tagli" netti
                _audioBitrateNowKbps = (int)(_audioBitrateNowKbps * 0.5);
                _videoBitrateNowKbps = (int)(_videoBitrateNowKbps * 0.5);
            }

            if (_paused)
            {
                _engine.Pause();
                NotifyPlaybackPausedForWled();
            }
            else
            {
                _engine.Play();
                NotifyPlaybackStartedForWled();
            }
            NotifyAutomationPauseChanged(_paused);
            JellyfinPlaybackProgress(_lastKnownPlaybackPosition, force: true);
            SyncHudPlayingState();
            // Pausa da tastiera o mouse: il telefono deve cambiare icona subito, non al prossimo avanzamento.
            PublishRemoteState();

            _hud.TimelineVisible = _duration > 0;

            // Da remoto NON deve comparire l'HUD (eccetto timeline, gestita altrove)
            if (!IsRemoteCommandActive)
                HudBump(1200, allowWhenRemote: false, showTimeline: true);

            BringOverlaysToFront();
            EnsureActive();
        }

        private void SafeStop(bool returnToHome = true, bool suppressActivation = false, bool disposeEngineAsync = false, bool preserveLoading = false)
        {
            if (returnToHome && !_playbackQueueTransitionInProgress)
            {
                _musicWorkspaceActive = false;
                _musicWorkspaceView = 0;
            }
            _stopping = true;
            bool asyncMetersStop = _closingForExit || _asyncStopMetersOnce;
            _asyncStopMetersOnce = false;

            try
            {
                if (_engine != null && ShouldTrackCurrentPlaybackInDiary())
                {
                    string historyPath = _currentPath!;
                    string historyTitle = BuildBestDisplayTitleForPath(historyPath);
                    double historyPosition = disposeEngineAsync
                        ? Math.Max(0, _lastKnownPlaybackPosition)
                        : _engine.PositionSeconds;
                    double historyDuration = GetTimelineDurationSeconds();
                    _ = System.Threading.Tasks.Task.Run(() => WatchHistoryStore.RecordProgress(historyPath, historyTitle, historyPosition, historyDuration));
                }
            }
            catch { }

            try { ResetPlaybackContextMenuState(); } catch { }
            try { ForceHidePlaybackOverlaySurfaces(preserveLoading); } catch { }
            try { DetachMpcvrChildWindowHooks(); } catch { }

            // ferma eventuali notifiche DirectShow (EC_COMPLETE)
            try { UnbindGraphNotify(); } catch { }
            try { StopAudioMeters(asyncStop: asyncMetersStop); } catch { }
            if (returnToHome && !_closingForExit)
                TryShowCinematicLibraryShellBeforeStop();

            SaveDiscPosition();
            IPlaybackEngine? engineToDispose = _engine;
            if (engineToDispose != null)
            {
                try { QuarantineRendererWindowBeforeStop(engineToDispose); } catch { }
                try { engineToDispose.BindUpdateCallback(null); } catch { }
                try { if (_engineStatusHandler != null) engineToDispose.OnStatus -= _engineStatusHandler; } catch { }
                try { if (_engineProgressHandler != null) engineToDispose.OnProgressSeconds -= _engineProgressHandler; } catch { }
                try { if (_engineBitstreamHandler != null) engineToDispose.OnBitstreamChanged -= _engineBitstreamHandler; } catch { }
            }
            _engineStatusHandler = null;
            _engineProgressHandler = null;
            _engineUpdateHandler = null;
            _engineBitstreamHandler = null;
            _engine = null;
            if (engineToDispose != null && TryReleaseEngineForCrossfade(engineToDispose, _lastKnownPlaybackPosition))
            {
                // Il motore resta a suonare da solo mentre sfuma, poi si chiude.
            }
            else if (engineToDispose != null)
            {
                if (disposeEngineAsync && !_closingForExit)
                    QueueDetachedEngineTeardown(engineToDispose);
                else
                    DisposeDetachedEngine(engineToDispose, _closingForExit);
            }
            try { _refresh.RestoreIfChanged(); } catch { }
            try { _pktRate.Dispose(); } catch { }
            try { _pktRateAudio.Dispose(); } catch { }
            _pktRate = new PacketRateSampler();      // ✅ nuova istanza per la prossima riproduzione
            _pktRateAudio = new PacketRateSampler(); // ✅ nuova istanza per la prossima riproduzione
            _pktRateOk = false;
            _pktRateAudioOk = false;
            _lastPktSample = DateTime.MinValue;
            Volatile.Write(ref _packetRateSampleActive, 0);
            _activeRendererChoice = null;
            _mpcvrExclusiveMode = false;
            _currentMediaHasVideo = false;
            _currentIntroOutroMarkers = null;
            _skipIntroPromptConsumed = false;
            _nextEpisodePromptConsumed = false;
            SetIntroOutroPromptVisibility(false, false);
            _imageFiles.Clear();
            _imageIndex = -1;
            if (_photoHud != null) _photoHud.Visible = false;
            _duration = 0; _paused = false;
            _lastKnownPlaybackPosition = 0;
            _timelineUiPositionOverride = -1;
            _timelineUiOverrideUntilUtc = DateTime.MinValue;
            SyncHudPlayingState();
            try { HidePreOpenPlaceholderGate(clearPending: true); } catch { }

            // reset pre-roll state (se interrompiamo mentre una demo è in corso)
            _playingPreRoll = false;
            _pendingMainPathAfterPreRoll = null;
            _pendingMainResumeAfterPreRoll = 0;
            _pendingMainStartPausedAfterPreRoll = false;
            FilmVolumeLeave();
            RememberAmplifierLevelAtStop();
            ReleaseDiscMountLater();
            StopImageSizing();
            JellyfinPlaybackStopped(waitBriefly: _closingForExit);
            ScheduleTraktDiarySync();
            NotifyPlaybackStoppedForWled();
            PublishRemoteState(0);
            var oldThumbCts = Interlocked.Exchange(ref _thumbCts, null);
            try { oldThumbCts?.Cancel(); } catch { }
            try { oldThumbCts?.Dispose(); } catch { }
            try { _thumb.Close(); } catch { }
            ReleaseMenuAmbient();
            try { _previewCache.Clear(); } catch { }
            Interlocked.Increment(ref _previewReqSerial);
            Interlocked.Exchange(ref _previewWorkerRunning, 0);
            _previewOverlayInitialized = false;
            StopAudioMeters(asyncStop: asyncMetersStop);
            ResetAudioOverlayState();
            ResetMusicPresentation();
            // La richiesta del pannello info resta: lo chiude solo l'utente. Qui si nasconde
            // perche' non c'e' piu' nulla in riproduzione; torna con il prossimo film.
            _infoOverlay.Visible = false;
            _hud.Visible = false;
            if (!preserveLoading) try { if (_videoLoading != null) _videoLoading.Visible = false; } catch { }
            try { ForceHidePlaybackOverlaySurfaces(preserveLoading); } catch { }
            _currentWebAudioUrl = null;
            _currentWebVideoBitrateKbps = 0;
            _currentWebAudioBitrateKbps = 0;
            _currentWebWidth = 0;
            _currentWebHeight = 0;
            _currentWebFps = 0;
            _currentWebVideoCodec = null;
            _currentWebAudioCodec = null;
            _currentPath = null;
            _statsTimer.Stop();
            if (!preserveLoading && !_closingForExit) _lyricsService.SetVideoPlaybackActive(false);

            // Quando si stoppa tutto e si torna alla home, NON devono restare
            // evidenziazioni appese.
            try { _focusRing.Attach(null); } catch { }
            _focused = null;
            _dpadRoot = null;

            if (!preserveLoading) try { if (_videoLoading != null) _videoLoading.Visible = false; } catch { }
            if (!_closingForExit)
            {
                if (returnToHome)
                {
                    try { ShowDefaultLibrary(stopCurrent: false); } catch { }
                }

                if (!suppressActivation)
                {
                    if (ShouldSuppressExternalPlaybackOverlays())
                        HideExternalPlaybackOverlayHosts();
                    else
                        _overlayHost?.SyncTo(this);
                    BringOverlaysToFront();
                    EnsureActive();
                    try { RestoreLibraryContextMenuAfterPlayback(); } catch { }
                }
            }
        }

        private void QueueDetachedEngineTeardown(IPlaybackEngine engine)
        {
            lock (_engineTeardownSync)
            {
                Task previous = _engineTeardownTask;
                _engineTeardownTask = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    DisposeDetachedEngine(engine, appExit: false);
                });
            }
        }

        private static void DisposeDetachedEngine(IPlaybackEngine engine, bool appExit)
        {
            try { engine.Stop(); } catch { }
            try
            {
                if (appExit && engine is DirectShowUnifiedEngine directShowEngine)
                    directShowEngine.DisposeForAppExit();
                else
                    engine.Dispose();
            }
            catch { }
        }

        private async Task WaitForPendingEngineTeardownAsync(CancellationToken cancellationToken)
        {
            Task pending;
            lock (_engineTeardownSync)
                pending = _engineTeardownTask;

            if (pending.IsCompleted)
                return;

            try
            {
                Task timeout = Task.Delay(3000, cancellationToken);
                await Task.WhenAny(pending, timeout).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { }
        }

        private void ShowLibraryAfterPlaybackExit()
        {
            // Fine riproduzione: lo schermo torna alla frequenza che aveva prima del film.
            try { _refresh.RestoreIfChanged(); } catch { }
            if (!HasMusicPlayback) { try { _cinematicLibraryPage?.SetMusicWorkspaceContent(null); } catch { } }
            bool returnHome = _returnToCinematicHomeOnPlaybackExit;
            string? returnWebSource = _returnToCinematicWebSourceOnPlaybackExit;
            _returnToCinematicHomeOnPlaybackExit = false;
            _returnToCinematicWebSourceOnPlaybackExit = null;
            bool returnSpotlight = _returnToSpotlightOnPlaybackExit;
            _returnToSpotlightOnPlaybackExit = false;

            if (returnSpotlight && _netflixModePage != null && !_netflixModePage.IsDisposed)
            {
                // ShowNetflixMode non fa nulla con la maschera di caricamento ancora visibile.
                try { CancelVideoLoadingUiImmediate(releasePointerCapture: true); } catch { }
                ShowNetflixMode();
                if (_netflixModePage.Visible) return;
            }

            if (!string.IsNullOrWhiteSpace(returnWebSource))
            {
                ShowCinematicLibrary(stopCurrent: false);
                try { _cinematicLibraryPage?.NavigateToSource(returnWebSource); } catch { }
                return;
            }

            if (returnHome)
            {
                ShowCinematicLibrary(stopCurrent: false);
                try { _cinematicLibraryPage?.NavigateHome(); } catch { }
                return;
            }

            ShowLibraryForCurrentCategory(stopCurrent: false);
            try { RestoreLibraryContextMenuAfterPlayback(); } catch { }
        }

        private void CloseCurrentToLibrary()
        {
            bool libraryRestored = false;
            bool musicInLibrary = MusicPlayingInLibrary;
            // Musica fermata mentre si guarda Spotlight: si resta in Spotlight, non si torna alla libreria.
            bool spotlightOnScreen = false;
            try { spotlightOnScreen = _netflixModePage?.Visible == true && !_currentMediaHasVideo; } catch { }
            // Va campionato prima di nascondere renderer e owned windows: dopo il
            // teardown GetForegroundWindow puo' puntare per un istante al desktop e
            // il primo click sulla libreria viene usato soltanto per riattivare il form.
            bool restoreForegroundAfterExit = false;
            try { restoreForegroundAfterExit = IsCinecoreForeground(); } catch { }
            try
            {
                // Un resolver/probe ancora in volo non deve poter terminare dopo lo
                // stop e ricreare loader, host o renderer sopra la libreria ripristinata.
                CancelPendingMediaOpenForPlaybackExit();

                // Web resolvers and native renderers can leave the popup's modal flags
                // latched. Clear them before the library receives its first mouse event.
                try { ResetPlaybackContextMenuState(); } catch { }
                ForceHidePlaybackOverlaySurfaces();
                ClearPlaybackQueueOnPlaybackExit();
                try { ResetMusicPresentation(); } catch { }
                try { if (_audioOnlyBanner != null) _audioOnlyBanner.Visible = false; } catch { }
                try { StopAudioMeters(asyncStop: true); } catch { }
                try { if (_audioMetersHost != null) _audioMetersHost.Visible = false; } catch { }
                try { if (_audioMeters != null) _audioMeters.Visible = false; } catch { }
                try { if (_audioMetersHost != null) _audioMetersHost.Visible = false; } catch { }
                try { if (_audioMeters != null) _audioMeters.Visible = false; } catch { }

                // Prima sgancia completamente renderer e finestre native; mostrare la
                // libreria mentre VRWindow era ancora sopra il form lasciava il destro
                // operativo ma intercettava ogni click sinistro, inclusa la title bar.
                _asyncStopMetersOnce = true;
                SafeStop(returnToHome: false, suppressActivation: true, disposeEngineAsync: true);
                ForceHidePlaybackOverlaySurfaces();
                // Il pannello musicale (analisi/testi) va sganciato dalla libreria: se resta
                // agganciato la libreria si considera ancora in modalita' analisi e non
                // disegna nulla (schermo vuoto dopo "Interrompi" dalla vista analisi).
                try { DeactivateMusicWorkspace(); } catch { }
                if (musicInLibrary) RestoreLibraryInPlaceAfterMusicStop();
                else ShowLibraryAfterPlaybackExit();
                libraryRestored = true;
                if (spotlightOnScreen && _netflixModePage?.Visible != true) { try { ShowNetflixMode(); } catch { } }
                RestoreLibraryInteractivityAfterPlayback(restoreForegroundAfterExit);

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || _closingForExit) return;
                        RestoreLibraryInteractivityAfterPlayback(restoreForegroundAfterExit);
                    }));
                }
                catch { }
            }
            catch (Exception ex)
            {
                Dbg.Error("[UI] CloseCurrentToLibrary failed: " + ex.Message);
            }
            finally
            {
                // Nessun errore di stop/renderer può lasciare un mask, una cattura o
                // il flag _stopping davanti alla libreria.
                try { CancelVideoLoadingUiImmediate(releasePointerCapture: true); } catch { }
                if (!libraryRestored && !_closingForExit && !IsDisposed)
                {
                    try { ShowLibraryAfterPlaybackExit(); } catch { }
                }
                try { RestoreLibraryInteractivityAfterPlayback(restoreForegroundAfterExit); } catch { _stopping = false; }
            }
        }

        private void CancelPendingMediaOpenForPlaybackExit()
        {
            _openingMusic = false;
            _preserveMusicTransportOnOpen = false;
            _musicWorkspaceActive = false;
            _musicWorkspaceView = 0;
            UpdateMusicTransport();
            Interlocked.Increment(ref _openSerial);
            CancellationTokenSource? pending = Interlocked.Exchange(ref _openCts, null);
            try { pending?.Cancel(); } catch { }
            try { pending?.Dispose(); } catch { }

            _deferLibraryHideUntilPlaybackReady = false;
            _deferredPlaybackSourceWasNetflix = false;
            _deferredPlaybackSourceWasCinematic = false;
            _suppressVideoLoadingSerial = 0;
            _suppressVideoLoadingOnce = false;
            try { HidePreOpenPlaceholderGate(clearPending: true); } catch { }
        }

        private void RestoreLibraryInteractivityAfterPlayback(bool wasForegroundBeforeExit)
        {
            // Memorizza prima di nascondere le owned windows: se una di esse è la
            // foreground window, dopo Hide Windows non restituisce automaticamente il
            // focus al form principale e il primo click viene soltanto consumato.
            bool restoreForeground = false;
            try { restoreForeground = IsCinecoreForeground(); } catch { }
            // Al ritorno in libreria il titolo sotto il vecchio punto del mouse non deve restare acceso.
            try { BeginInvoke(new Action(() => { try { _cinematicLibraryPage?.SyncHoverWithPointer(); } catch { } })); } catch { }
            restoreForeground |= wasForegroundBeforeExit;

            try { ResetPlaybackContextMenuState(); } catch { }
            try { Enabled = true; } catch { }
            try { if (IsHandleCreated) EnableWindowAfterPlayback(Handle, true); } catch { }
            try { if (_rootLayout != null) _rootLayout.Enabled = true; } catch { }
            try { if (_stack != null) _stack.Enabled = true; } catch { }
            try { SetModalInputState(false); } catch { }
            try { if (_videoHost != null) _videoHost.Visible = false; } catch { }
            try { if (_pausePlaceholder != null) _pausePlaceholder.Visible = false; } catch { }
            HideExternalPlaybackOverlayHosts();

            try
            {
                if (_cinematicLibraryPage?.Visible == true)
                {
                    _cinematicLibraryPage.Enabled = true;
                    _cinematicLibraryPage.BringToFront();
                    _dpadRoot = _cinematicLibraryPage;
                    ResetLibraryRemoteActivation(clearFocusRing: true);
                }
                else if (_netflixModePage?.Visible == true)
                {
                    _netflixModePage.Enabled = true;
                    _netflixModePage.BringToFront();
                    _dpadRoot = _netflixModePage;
                }
            }
            catch { }

            // BringToFront/Layout possono attivare callback del renderer ancora in
            // smontaggio. Richiudiamo le host top-level dopo aver ripristinato la pagina.
            HideExternalPlaybackOverlayHosts();

            _stopping = false;
            // A questo punto nessun drag legittimo puo' essere ancora attivo. La
            // cattura puo' appartenere soltanto a HUD, menu o renderer smontati e va
            // rilasciata anche se WinForms conserva uno stato pulsante obsoleto.
            try { ReleaseStaleCinecoreMouseCapture(force: true); } catch { }
            try { RestoreLibraryContextMenuAfterPlayback(); } catch { }

            // Non rubare mai il focus a un'altra applicazione (per esempio quando lo
            // stop arriva dal remote). Lo trasferiamo soltanto da una finestra Cinecore
            // owned al PlayerForm che era già in primo piano.
            if (restoreForeground && Visible && WindowState != FormWindowState.Minimized)
            {
                try { SetForegroundWindow(Handle); } catch { }
                try { Activate(); } catch { }
                try { Focus(); } catch { }
                try
                {
                    Control? target = _cinematicLibraryPage?.Visible == true
                        ? _cinematicLibraryPage
                        : _netflixModePage?.Visible == true ? _netflixModePage : null;
                    if (target != null && target.IsHandleCreated)
                    {
                        target.Select();
                        target.Focus();
                        SetFocus(target.Handle);
                    }
                }
                catch { }
            }
        }

        [DllImport("user32.dll", EntryPoint = "EnableWindow")]
        private static extern bool EnableWindowAfterPlayback(IntPtr hWnd, bool enable);

        private void RefreshPhotoImageAfterWindowModeChange(string reason)
        {
            if (!IsPhotoMode || _engine == null || IsDisposed)
                return;

            void Refresh()
            {
                try
                {
                    if (IsDisposed || !IsPhotoMode || _engine == null)
                        return;

                    UpdateVideoWindowForCurrentHost();
                    SyncOverlayToVideoRect();
                    if (_photoHud != null)
                    {
                        _photoHud.Visible = true;
                        _photoHud.Wake(900);
                        _photoHud.BringToFront();
                    }
                }
                catch { }
            }

            Refresh();
            try { BeginInvoke(new Action(Refresh)); } catch { }
            try
            {
                _ = Task.Delay(90).ContinueWith(_ =>
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke(new Action(Refresh));
                    }
                    catch { }
                });
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void SetWindowTransitionAnimation(bool enabled)
        {
            try
            {
                int disabled = enabled ? 0 : 1;
                DwmSetWindowAttribute(Handle, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref disabled, sizeof(int));
            }
            catch { }
        }

        // Schermo intero esteso (come PotPlayer): la finestra copre tutti i monitor.
        // Con un film 3D ogni occhio riempie il proprio schermo (due proiettori o due
        // monitor affiancati), senza conversione in 2D.
        private bool _extendedFullscreen;

        private void ToggleExtendedFullscreen()
        {
            if (FormBorderStyle == FormBorderStyle.None)
            {
                if (_extendedFullscreen)
                {
                    ToggleFullscreen(); // esce
                    return;
                }
                // Da schermo intero normale: allarga soltanto.
                _extendedFullscreen = true;
                var span = SystemInformation.VirtualScreen;
                Bounds = span;
                Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, span.X, span.Y, span.Width, span.Height, Win32.SWP_FRAMECHANGED);
                ApplyStereoOutputMode();
                Dbg.Log($"[VIDEO] Extended fullscreen: {span.Width}x{span.Height}@{span.X},{span.Y}, screens={Screen.AllScreens.Length}");
                return;
            }
            _extendedFullscreen = true;
            ToggleFullscreen();
        }

        private void ApplyStereoOutputMode()
        {
            bool keepBoth = _extendedFullscreen && Screen.AllScreens.Length > 1;
            try
            {
                if (_engine is DirectShowUnifiedEngine ds) ds.StereoKeepBothEyes = keepBoth;
                else if (_engine is LibMpvPlaybackEngine mpv && mpv.StereoKeepBothEyes != keepBoth)
                {
                    mpv.StereoKeepBothEyes = keepBoth;
                    mpv.SetStereo3D(_stereo);
                }
                UpdateVideoWindowForCurrentHost();
            }
            catch { }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FullscreenPlacement { public int Length, Flags, ShowCmd, MinX, MinY, MaxX, MaxY, Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPlacement")]
        private static extern bool SetFullscreenPlacement(IntPtr hwnd, ref FullscreenPlacement placement);

        // Uscendo dallo schermo intero la finestra cambia stile e misura in piu' passaggi, e per un attimo
        // lasciava vedere cio' che c'e' dietro (desktop o altre finestre). Un fondo nero messo subito sotto
        // il player copre lo schermo per la durata del passaggio.
        private sealed class FullscreenExitCover : Form
        {
            public FullscreenExitCover()
            {
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                BackColor = Color.Black; Enabled = false;
            }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x00000080 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x00000020 /* TRANSPARENT */; return cp; }
            }
        }
        private FullscreenExitCover? _fullscreenExitCover;
        private int _fullscreenExitCoverSerial;

        private void ShowFullscreenExitCover()
        {
            try
            {
                Rectangle area = _extendedFullscreen ? SystemInformation.VirtualScreen : Screen.FromControl(this).Bounds;
                _fullscreenExitCover ??= new FullscreenExitCover();
                int serial = ++_fullscreenExitCoverSerial;
                Win32.SetWindowPos(_fullscreenExitCover.Handle, Handle, area.X, area.Y, area.Width, area.Height, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);
                _ = Task.Delay(420).ContinueWith(_ =>
                {
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (serial != _fullscreenExitCoverSerial || _fullscreenExitCover == null || _fullscreenExitCover.IsDisposed) return;
                                Win32.SetWindowPos(_fullscreenExitCover.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0080 /* HIDEWINDOW */);
                            }
                            catch { }
                        }));
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void ToggleFullscreen()
        {
            if (_fullscreenTransitioning &&
                _fullscreenTransitionStartedUtc != DateTime.MinValue &&
                DateTime.UtcNow - _fullscreenTransitionStartedUtc < TimeSpan.FromSeconds(1.5))
                return;
            _fullscreenTransitioning = true;
            _fullscreenTransitionStartedUtc = DateTime.UtcNow;
            SetWindowTransitionAnimation(false);
            bool restoreHudAfterTransition = _hud?.Visible == true || ShouldPinAudioOnlyHud();
            // Il comando fullscreen parte quasi sempre dall'HUD: chiudilo in modo
            // intenzionale prima del cambio non-client, invece di ridisegnarlo nella
            // vecchia e nella nuova geometria (il lampo denunciato dall'utente).
            SuppressHudForProgrammaticTransition(750);
            try { HideMpcvrOverlayHostWhenIdle(); } catch { }
            if (!IsPhotoMode)
            {
                // Gli overlay vivono anche in HWND separati. Nascondere soltanto il
                // controllo HUD lasciava quei layer trasparenti attraversare due resize
                // consecutivi e produceva il flicker visibile all'uscita dal fullscreen.
                try { if (_overlayHost?.Visible == true) _overlayHost.Hide(); } catch { }
                try { if (_overlayInlineHost != null) _overlayInlineHost.Visible = false; } catch { }
                try { HideMpcvrDedicatedOverlay(); } catch { }
                try { HideMpcvrChildOverlayHost(); } catch { }
                try { _mpcvrOverlayPopup?.Hide(); } catch { }
                try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }
                try { ClearMpcvrMixerOverlayMirror(); } catch { }
            }

            try
            {
                var screen = Screen.FromControl(this);
                string fromMode = FormBorderStyle == FormBorderStyle.None ? "fullscreen-borderless" : "windowed";
                Dbg.Log($"[VIDEO] ToggleFullscreen start: from={fromMode}, screen={screen.DeviceName} {screen.Bounds.Width}x{screen.Bounds.Height}@{screen.Bounds.X},{screen.Bounds.Y}, playerHdrProfile={_hdrProfile}, request={DescribeRequestedHdrOutput()}");
                if (FormBorderStyle != FormBorderStyle.None)
                {
                    _prevBorder = FormBorderStyle;
                    _prevState = WindowState;
                    _prevBounds = Bounds;
                    _prevControlBox = ControlBox;
                    _prevMinimizeBox = MinimizeBox;
                    _prevMaximizeBox = MaximizeBox;
                    _prevWindowStyle = GetCurrentWindowStyle();

                    try { SuspendLayout(); } catch { }
                    try
                    {
                        // Da finestra massimizzata non si passa piu' dalla finestra piccola "ripristinata" (si vedeva
                        // rimpicciolire e poi allargarsi): tolto il bordo, un solo comando la porta allo schermo intero.
                        Rectangle fullscreenTarget = _extendedFullscreen ? SystemInformation.VirtualScreen : screen.Bounds;
                        bool fromMaximized = WindowState == FormWindowState.Maximized;
                        if (WindowState == FormWindowState.Minimized)
                            WindowState = FormWindowState.Normal;

                        // FormBorderStyle.None rimuove gia' caption e pulsanti. Cambiare
                        // separatamente ControlBox/Minimize/Maximize generava tre frame
                        // non-client intermedi visibili anche nell'overlay.
                        FormBorderStyle = FormBorderStyle.None;
                        ApplyTrueBorderlessWindowStyle();
                        TopMost = true;

                        if (fromMaximized)
                        {
                            try
                            {
                                var placement = new FullscreenPlacement { Length = System.Runtime.InteropServices.Marshal.SizeOf<FullscreenPlacement>(), ShowCmd = 1 /* SW_SHOWNORMAL */,
                                    MinX = -1, MinY = -1, MaxX = -1, MaxY = -1,
                                    Left = fullscreenTarget.Left, Top = fullscreenTarget.Top, Right = fullscreenTarget.Right, Bottom = fullscreenTarget.Bottom };
                                SetFullscreenPlacement(Handle, ref placement);
                            }
                            catch { }
                            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                        }
                        Bounds = fullscreenTarget;
                        Win32.SetWindowPos(this.Handle, Win32.HWND_TOPMOST,
                            Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height,
                            Win32.SWP_FRAMECHANGED);
                    }
                    finally
                    {
                        try { ResumeLayout(true); } catch { }
                    }
                }
                else
                {
                    try { _refresh.RestoreIfChanged(); } catch { }
                    _extendedFullscreen = false;
                    try { SuspendLayout(); } catch { }
                    try
                    {
                        TopMost = false;
                        ShowFullscreenExitCover();
                        // Prima la finestra va, ancora senza bordo, dove deve finire; il bordo arriva dopo, sul posto.
                        // Nell'ordine inverso si vedeva per un attimo una finestra con la barra del titolo grande
                        // quanto lo schermo, e subito dopo il salto alla misura vera.
                        try
                        {
                            if (_prevState == FormWindowState.Maximized) Bounds = Screen.FromControl(this).WorkingArea;
                            else if (_prevState == FormWindowState.Normal && _prevBounds.Width > 0 && _prevBounds.Height > 0) Bounds = _prevBounds;
                        }
                        catch { }
                        FormBorderStyle = _prevBorder;
                        RestoreWindowedWindowStyle();

                        if (_prevState == FormWindowState.Normal && _prevBounds.Width > 0 && _prevBounds.Height > 0)
                            Bounds = _prevBounds;

                        WindowState = _prevState;
                        Win32.SetWindowPos(this.Handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_FRAMECHANGED);
                        if (_hud != null) _hud.AutoHide = false;
                    }
                    finally
                    {
                        try { ResumeLayout(true); } catch { }
                    }
                }
            }
            catch
            {
                try
                {
                    if (_hud != null)
                    {
                        _hud.Visible = restoreHudAfterTransition;
                        _hud.TimelineVisible = restoreHudAfterTransition && GetTimelineDurationSeconds() > 0;
                    }
                }
                catch { }
                _fullscreenTransitioning = false;
                SetWindowTransitionAnimation(true);
                return;
            }

            try
            {
                Dbg.Log($"[VIDEO] ToggleFullscreen complete: mode={(FormBorderStyle == FormBorderStyle.None ? "fullscreen-borderless" : "windowed")}, topMost={TopMost}, bounds={Bounds.Width}x{Bounds.Height}@{Bounds.X},{Bounds.Y}");
            }
            catch { }

            // A non-client style change can recreate the form HWND. Keep the
            // independently animated loader bound to the current player window
            // while a native graph is still opening.
            if (_videoLoading?.Visible == true)
            {
                try { _threadedVideoLoading.Show(Handle, _loadingLastMessage.Length > 0 ? _loadingLastMessage : Tx("Preparo la riproduzione…", "Preparing playback…"), _loadingArtPath, _loadingArtTitle, _loadingArtProgress); } catch { }
            }

            // Il cambio di stile e bounds è già terminato. Non tenere bloccato il
            // comando fino ai ridisegni differiti dei renderer: in quel caso il tasto
            // per uscire dal fullscreen poteva restare ignorato.
            _fullscreenTransitioning = false;
            _fullscreenTransitionStartedUtc = DateTime.MinValue;
            ApplyStereoOutputMode();

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed) return;
                        SetWindowTransitionAnimation(true);
                        if (_activeRendererChoice == VRChoice.MADVR && _videoLoading?.Visible != true)
                            TryApplyMadVrRefreshForPlayback(VRChoice.MADVR, _currentMediaHasVideo);
                        try { ApplyOverlayTopMostPolicy(); } catch { }
                        SyncOverlayToVideoRect();
                        BringOverlaysToFront();
                        // Un solo ciclo di assestamento: il vecchio percorso ne
                        // accodava due (ognuno con refresh immediato/deferred/timer),
                        // ridisegnando video e HUD fino a sei volte.
                        RefreshMpcvrHostAfterWindowModeChange("ToggleFullscreen settled");
                        RefreshPhotoImageAfterWindowModeChange("ToggleFullscreen settled");
                        if (_pausePlaceholder != null && _pausePlaceholder.Visible)
                            _pausePlaceholder.BringToFront();
                    }
                    catch { }
                    finally
                    {
                        try
                        {
                            if (_hud != null)
                            {
                                _hud.Visible = restoreHudAfterTransition;
                                _hud.TimelineVisible = restoreHudAfterTransition && GetTimelineDurationSeconds() > 0;
                                if (restoreHudAfterTransition)
                                {
                                    SafeShowOverlayHost();
                                    SyncOverlayToVideoRect();
                                    _hud.BringToFront();
                                }
                            }
                        }
                        catch { }
                        _fullscreenTransitioning = false;
                        _fullscreenTransitionStartedUtc = DateTime.MinValue;
                    }
                }));
            }
            catch
            {
                try
                {
                    if (_hud != null)
                    {
                        _hud.Visible = restoreHudAfterTransition;
                        _hud.TimelineVisible = restoreHudAfterTransition && GetTimelineDurationSeconds() > 0;
                    }
                }
                catch { }
                _fullscreenTransitioning = false;
                _fullscreenTransitionStartedUtc = DateTime.MinValue;
            }
        }

        private void SyncOverlayToVideoRect()
        {
            if (_overlayHost == null) return;
            try { RefreshMpcvrChildWindowHooks(); } catch { }

            if (_useInlineOverlay)
            {
                HideMpcvrDedicatedOverlay();
                HideMpcvrChildOverlayHost();
                try { _overlayHost.SetClickThrough(true); } catch { }
                try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                try { _mpcvrOverlayPopup.SetClickThrough(true); } catch { }
                try { _mpcvrOverlayPopup.Hide(); } catch { }
                return;
            }

            if (ShouldUseMpcvrDedicatedOverlay())
            {
                SyncMpcvrDedicatedOverlay();
                return;
            }

            if (ShouldUseMpcvrChildOverlayHost())
            {
                SyncMpcvrChildOverlayHost();
                return;
            }

            HideMpcvrDedicatedOverlay();
            HideMpcvrChildOverlayHost();

            Rectangle destClient = _videoHost.ClientRectangle;
            try
            {
                if (_engine != null)
                    destClient = _engine.GetLastDestRectAsClient(_videoHost.ClientRectangle);
            }
            catch { destClient = _videoHost.ClientRectangle; }

            destClient.Offset(_videoHost.Left, _videoHost.Top);
            _lastVideoDestInForm = destClient;

            if (ShouldKeepMpcvrOverlayHostHidden())
            {
                HideMpcvrOverlayHostWhenIdle();
                return;
            }

            if (ShouldUseMpcvrPopupOverlay())
            {
                Rectangle mpcvrOverlayScreen = GetMpcvrOverlayScreenRect();
                try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                try { _overlayHost.SetClickThrough(true); } catch { }
                try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                MoveOverlayControlsTo(_mpcvrOverlayPopup.Surface);
                _mpcvrOverlayPopup.SetClickThrough(false);
                _mpcvrOverlayPopup.SyncToScreen(mpcvrOverlayScreen, showIfHidden: true);
                _mpcvrOverlayPopup.SetVisibleRegion(BuildMpcvrOverlayVisibleRegions(_mpcvrOverlayPopup.Surface));
                _mpcvrOverlayPopup.BringToFront();
                return;
            }

            try { _mpcvrOverlayPopup.Hide(); } catch { }
            UpdateMpcvrOverlayRegionMode();
            ApplyOverlayTopMostPolicy();
            Rectangle formClientScreen = GetOverlayHostScreenRect();
            _overlayHost.SyncToScreen(formClientScreen);
            _overlayHost.SetClickThrough(false);
            UpdateMpcvrOverlayRegionMode();
            _overlayHost.RaiseAboveOwner();
        }
        private bool IsMouseOverHud()
        {
            var pt = _hud.PointToClient(Control.MousePosition);

            var hot = new Rectangle(
                0,
                Math.Max(0, _hud.Height - HUD_HOTZONE_H),
                _hud.Width,
                Math.Min(HUD_HOTZONE_H, _hud.Height)
            );

            return hot.Contains(pt);
        }
    }
}
