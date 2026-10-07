#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private volatile bool _stopping;
        private volatile bool _closingForExit;
        private VRChoice? _manualRendererChoice = null;
        private bool _hasRuntimeRendererOverride;
        private VRChoice? _runtimeRendererChoiceOverride;
        private MpvRuntimeChoice _mpvRuntimeChoice = MpvRuntimeChoice.X64;
        private bool _mpvRuntimeV3Manual;
        private MpvPlaybackSettings _mpvSettings = new();
        private VRChoice? _activeRendererChoice = null;
        private bool IsMpcvrActive => _activeRendererChoice == VRChoice.MPCVR;
        // === 3D → EVR forcing state ===
        private bool _hasSavedRendererFor3D = false;
        private VRChoice? _savedRendererFor3D = null;
        private bool _hasSavedRuntimeRendererFor3D = false;
        private bool _savedHadRuntimeRendererOverrideFor3D = false;
        private VRChoice? _savedRuntimeRendererFor3D = null;

        private async void OpenPath(string path, double resume = 0, bool startPaused = false, bool allowPlaceholderGate = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // File locale sparito (disco scollegato, file spostato): prima mpv partiva comunque e
            // restava una barra "in riproduzione" senza titolo, copertina ne' durata.
            if (!path.Contains("://", StringComparison.Ordinal) && Path.IsPathRooted(path) && !File.Exists(path) && !Directory.Exists(path))
            {
                Dbg.Warn($"[OPEN] missing file: '{path}'");
                ShowRemoteOsd(null, null, 2600, Tx("File non trovato: ", "File not found: ") + Path.GetFileName(path));
                return;
            }

            _openingMusic = LooksLikePureAudioByExt(path);
            if (!_openingMusic)
            {
                // Anche cambio renderer, episodio successivo e fine pre-roll
                // costruiscono un nuovo graph: mostra il relativo caricamento.
                _suppressVideoLoadingOnce = false;
                _suppressVideoLoadingSerial = 0;
            }
            _lyricsService.SetVideoPlaybackActive(!_openingMusic && !ImagePlaybackEngine.IsImageFile(path));
            if (!_openingMusic && !_suppressVideoLoadingOnce)
            {
                ShowVideoLoading(Tx("Preparo la riproduzione…", "Preparing playback…"));
                _videoLoading?.Update();
            }
            if (!_openingMusic) DeactivateMusicWorkspace();
            int transportOpenSerial = Volatile.Read(ref _openSerial) + 1;
            _preserveMusicTransportOnOpen = _openingMusic && _musicWorkspaceActive;

            bool queueTransitionRequested = _playbackQueueTransitionInProgress;
            bool deferredLibraryTransition = CaptureDeferredLibraryTransition();

            try
            {
                bool queueInitiated = _nextOpenBelongsToPlaybackQueue;
                _nextOpenBelongsToPlaybackQueue = false;

                bool preserveExistingQueueSession = _playbackQueueSessionActive &&
                                                   !queueInitiated &&
                                                   !string.IsNullOrWhiteSpace(path) &&
                                                   _playbackQueue.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

                _playbackQueueSessionActive = queueInitiated || preserveExistingQueueSession;

                // Se l'apertura è stata richiesta dal telecomando, NON vogliamo far comparire l'HUD automaticamente.
                // (La timeline remota viene gestita separatamente.)
                bool openInitiatedByRemote = IsRemoteCommandActive;

                // Aperture e transizioni placeholder -> demo -> film devono restare pulite.
                SuppressHudForProgrammaticTransition(1200);

                // --- latest-wins: se l'utente apre un altro media mentre stiamo risolvendo/probando,
                // annulliamo la richiesta precedente e ignoriamo i risultati tardivi.
                int mySerial = Interlocked.Increment(ref _openSerial);
                var cts = new CancellationTokenSource();
                var prev = Interlocked.Exchange(ref _openCts, cts);
                try { prev?.Cancel(); prev?.Dispose(); } catch { }
                var ct = cts.Token;

                // Passaggio demo → film: sopprimi overlay di caricamento una sola volta.
                bool suppressLoadingUi = _suppressVideoLoadingOnce;
                if (suppressLoadingUi)
                {
                    _suppressVideoLoadingOnce = false;
                    _suppressVideoLoadingSerial = mySerial;
                }

                bool willUsePlaceholderGate = allowPlaceholderGate &&
                                              _pausePlaceholderEnabled &&
                                              !_preOpenPlaceholderGateActive &&
                                              !string.IsNullOrWhiteSpace(path) &&
                                              ShouldUseCinemaFeaturesForPath(path);

                // Se stiamo facendo un passaggio diretto player -> player (es. demo -> film o reopen),
                // non vogliamo un lampo di riaccensione LED durante lo stop intermedio.
                bool suppressWledRestoreDuringOpen = (_engine != null || _playingPreRoll) && !willUsePlaceholderGate;
                if (suppressLoadingUi)
                    suppressWledRestoreDuringOpen = true;
                if (suppressWledRestoreDuringOpen)
                    _suppressNextWledRestore = true;

                // ===== Placeholder gate pre-film (Extra) =====
                // Se attivo: quando apri un NUOVO film (dopo il primo) non parte subito.
                // Mostriamo il placeholder a schermo e aspettiamo il Play.
                if (willUsePlaceholderGate)
                {
                    // Stoppa eventuale playback attivo, ma NON tornare alla home.
                    SafeStop(returnToHome: false);
                    SkipLoadingIfActive();

                    await ShowPreOpenPlaceholderGateAsync(path, resume, startPaused, ct);
                    CompleteLibraryToPlaybackTransition(deferredLibraryTransition);

                    // Questa OpenPath è stata intenzionalmente "rimandata": rilascia CTS.
                    try { if (Interlocked.CompareExchange(ref _openCts, null, cts) == cts) { } } catch { }
                    try { cts.Dispose(); } catch { }
                    return;
                }

                // Durante l'apertura dalla libreria mantieni davanti la maschera opaca
                // già dipinta; pagina, host e fullscreen possono assestarsi dietro di lei.
                if (!_openingMusic && !suppressLoadingUi && _videoLoading?.Visible != true)
                {
                    ShowVideoLoading(Tx("Preparo la riproduzione…", "Preparing playback…"));
                }
                SafeStop(returnToHome: false, preserveLoading: !_openingMusic && !suppressLoadingUi);
                await WaitForPendingEngineTeardownAsync(ct);
                if (ct.IsCancellationRequested || mySerial != _openSerial) return;
                try { _videoHost.Visible = true; } catch { }
                if (suppressLoadingUi)
                {
                    try
                    {
                        _videoHost.Visible = true;
                    }
                    catch { }
                }
                if (!deferredLibraryTransition && (_openingMusic || suppressLoadingUi))
                    SkipLoadingIfActive();

                // ===== Pre-roll demo (opzionale): se abilitato, prima del film riproduciamo una demo =====
                // NOTA: facciamo lo swap “in-place” (stessa OpenPath) per non duplicare logica.
                if (_suppressPreRollOnce)
                {
                    // Questo OpenPath arriva da “fine demo → avvia film”: non deve rientrare nel pre-roll.
                    _suppressPreRollOnce = false;
                }
                else if (_preRollEnabled && resume <= 0.01 && !startPaused && !_playingPreRoll && _pendingMainPathAfterPreRoll == null && ShouldRunPreRollForPath(path))
                {
                    try
                    {
                        string? demo = ResolveDemoPath(_preRollDemoPath);
                        if (!string.IsNullOrWhiteSpace(demo) && File.Exists(demo))
                        {
                            _pendingMainPathAfterPreRoll = path;
                            _pendingMainResumeAfterPreRoll = resume;
                            _pendingMainStartPausedAfterPreRoll = startPaused;

                            _playingPreRoll = true;

                            // la demo non usa resume/startPaused
                            path = demo;
                            resume = 0;
                            startPaused = false;
                        }
                    }
                    catch { }
                }

                // reset EOF auto-return state
                _endTriggered = false;
                _endCandidateSinceUtc = DateTime.MinValue;


                _currentPath = path;
                ResetIntroOutroPlaybackState(path);
                _originalVideoName = ExtractOriginalVideoName(path);
                _bitstreamNow = false;
                _bitstreamLastTrue = DateTime.MinValue;
                _isLocalFile = false;
                _currentWebAudioUrl = null; // reset ad ogni nuova sorgente
                _currentWebVideoBitrateKbps = 0;
                _currentWebAudioBitrateKbps = 0;
                _currentWebWidth = 0;
                _currentWebHeight = 0;
                _currentWebFps = 0;
                _currentWebVideoCodec = null;
                _currentWebAudioCodec = null;
                _currentMediaHasVideo = false;
                _lastKnownPlaybackPosition = Math.Max(0, resume);
                ResetAudioOverlayState();

                // Foto locali: niente spinner e niente probe video.
                if ((ImagePlaybackEngine.IsImageFile(path) && (File.Exists(path) || ImagePlaybackEngine.IsRemoteImage(path))) ||
                    (ImagePlaybackEngine.IsRemoteImage(path) && _currentLibraryCategory is "Photos" or "Foto"))
                {
                    NotifyPlaybackStoppedForWled(forceRestore: true);
                    CompleteLibraryToPlaybackTransition(deferredLibraryTransition);
                    await OpenImageAsync(path);
                    HideVideoLoading();
                    return;
                }

                ShowVideoLoading(Tx("Caricamento…", "Loading…"));
                // lascia respirare la UI (paint + timer spinner)
                await Task.Yield();

                bool? forceHasVideo = null;
                bool isYouTubePlaybackUrl = false;
                if (Uri.TryCreate(path, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    bool isYouTubeUrl = IsYouTubeUrlForPlayback(u);
                    if (JellyfinClient.TryParseStream(path, out _, out _, out _))
                    {
                        // File originale servito da Jellyfin: nessuna risoluzione. Il token si aggiunge
                        // solo qui, in memoria; _currentPath (ripresa, cronologia, coda) resta senza.
                        path = JellyfinClient.PlaybackUrl(path);
                    }
                    else if (isYouTubeUrl)
                    {
                        isYouTubePlaybackUrl = true;
                        forceHasVideo = true;
                        _currentWebAudioUrl = null;

                        ShowVideoLoading(Tx("Risolvo YouTube…", "Resolving YouTube…"));
                        await Task.Yield();

                        try
                        {
                            var r = await _resolverSta.InvokeAsync(() => WebMediaResolver.Resolve(path), ct);

                            if (ct.IsCancellationRequested || mySerial != _openSerial) return;

                            if (r != null && !string.IsNullOrWhiteSpace(r.Value.Url))
                            {
                                Debug.WriteLine($"[Resolver] YouTube streaming -> {r.Value.Url} (audio={r.Value.AudioUrl ?? "-"})");
                                path = r.Value.Url;
                                forceHasVideo = true;
                                _currentWebAudioUrl = r.Value.AudioUrl;
                                _currentWebVideoBitrateKbps = Math.Max(0, r.Value.VideoBitrateKbps);
                                _currentWebAudioBitrateKbps = Math.Max(0, r.Value.AudioBitrateKbps);
                                _currentWebWidth = Math.Max(0, r.Value.Width);
                                _currentWebHeight = Math.Max(0, r.Value.Height);
                                _currentWebFps = Math.Max(0, r.Value.Fps);
                                _currentWebVideoCodec = r.Value.VideoCodec;
                                _currentWebAudioCodec = r.Value.AudioCodec;
                                if (!string.IsNullOrWhiteSpace(r.Value.Title))
                                {
                                    _originalVideoName = r.Value.Title;
                                    PlaybackTitleHints.Set(_currentPath, r.Value.Title, "YouTube", r.Value.Width, r.Value.Height);
                                }
                            }
                            else
                            {
                                Debug.WriteLine("[Resolver] YouTube: resolver diretto non disponibile.");
                                try { _lblStatus.Text = Tx("YouTube: impossibile risolvere il video", "YouTube: unable to resolve video"); } catch { }
                                HideVideoLoading();
                                NotifyPlaybackStoppedForWled(forceRestore: true);
                                return;
                            }
                        }
                        catch
                        {
                            if (ct.IsCancellationRequested || mySerial != _openSerial) return;
                            Debug.WriteLine("[Resolver] YouTube: risoluzione fallita.");
                            try { _lblStatus.Text = Tx("YouTube: errore durante la risoluzione", "YouTube: resolution failed"); } catch { }
                            HideVideoLoading();
                            NotifyPlaybackStoppedForWled(forceRestore: true);
                            return;
                        }
                    }
                    else
                    {
                        ShowVideoLoading(Tx("Risolvo l'URL…", "Resolving URL…"));
                        await Task.Yield();

                        try
                        {
                            var r = await _resolverSta.InvokeAsync(() => WebMediaResolver.Resolve(path), ct);

                            if (ct.IsCancellationRequested || mySerial != _openSerial) return;

                            if (r == null)
                            {
                                Debug.WriteLine($"[Resolver] {path} -> nessuna URL media diretta (resolver null)");
                                _lblStatus.Text = Tx("Impossibile trovare un flusso riproducibile per questo URL.", "No playable stream could be found for this URL.");
                                HideVideoLoading();
                                NotifyPlaybackStoppedForWled(forceRestore: true);
                                return;
                            }

                            Debug.WriteLine($"[Resolver] {path} -> {r.Value.Url} (audio={r.Value.AudioUrl ?? "-"}, forceVideo={r.Value.ForceHasVideo})");
                            path = r.Value.Url;
                            forceHasVideo = r.Value.ForceHasVideo;
                            _currentWebAudioUrl = r.Value.AudioUrl;   // <--- memorizza eventuale audio separato
                        }
                        catch (Exception ex)
                        {
                            if (ct.IsCancellationRequested || mySerial != _openSerial) return;
                            Debug.WriteLine($"[Resolver] {path} -> eccezione durante la risoluzione");
                            _lblStatus.Text = Tx("Risoluzione URL non riuscita: ", "URL resolution failed: ") + ex.Message;
                            HideVideoLoading();
                            NotifyPlaybackStoppedForWled(forceRestore: true);
                            return;
                        }
                    }
                }

                bool isLocalFile = false;
                if (Uri.TryCreate(path, UriKind.Absolute, out var uriLocal) && uriLocal.IsFile)
                    isLocalFile = true;
                else if (File.Exists(path))
                    isLocalFile = true;
                _isLocalFile = isLocalFile;

                // ==== RAMO IMMAGINI (file locali) ====
                // evitiamo di far partire DirectShow + MediaProbe per .jpg/.png ecc.
                if (ImagePlaybackEngine.IsImageFile(path) && File.Exists(path))
                {
                    NotifyPlaybackStoppedForWled(forceRestore: true);
                    CompleteLibraryToPlaybackTransition(deferredLibraryTransition);
                    await OpenImageAsync(path);
                    HideVideoLoading();
                    return;
                }

                // Disco (cartella BDMV / VIDEO_TS, unita' ottica, ISO): lo legge libbluray / dvdnav dentro
                // mpv. Il probe e DirectShow lavorano su un file e qui non hanno nulla da aprire.
                bool isDisc = DiscMedia.TryResolve(path, out var disc);
                if (isDisc)
                {
                    _info = null;
                    forceHasVideo = true;
                    _originalVideoName = DiscMedia.DisplayName(path);
                    // Una ISO montata da Windows diventa un'unita': la leggono tutti i renderer, non solo mpv.
                    if (disc.IsImage && disc.Kind == DiscKind.Bluray)
                    {
                        string image = disc.Device;
                        ReleaseDiscMount();
                        var mount = await Task.Run(() => IsoMount.TryMount(image), ct);
                        if (ct.IsCancellationRequested || mySerial != _openSerial) { mount?.Dispose(); return; }
                        _discMount = mount;
                        isDisc = DiscMedia.TryResolve(path, out disc);
                    }
                    string? refused =
                        !isDisc || !DiscMedia.HasPlayableTitle(disc)
                            ? Tx("Questo disco non contiene un titolo riproducibile.", "This disc has no playable title.")
                        : disc.Kind == DiscKind.Bluray && disc.Protected == true && DiscProtection.Available == DiscProtection.Provider.None
                            ? Tx("Disco protetto: installa MakeMKV oppure importa il tuo KEYDB.cfg (Impostazioni › Blu-ray protetti).",
                                 "Protected disc: install MakeMKV or import your KEYDB.cfg (Settings › Protected Blu-rays).")
                        : null;
                    if (refused != null)
                    {
                        Dbg.Warn("[DISC] not opened: " + refused);
                        _lblStatus.Text = refused;
                        HideVideoLoading();
                        NotifyPlaybackStoppedForWled(forceRestore: true);
                        CompleteLibraryToPlaybackTransition(deferredLibraryTransition);
                        ShowRemoteOsd(null, null, 6000, refused);
                        ReleaseDiscMount();
                        return;
                    }
                    // HDR, codec e durata servono a madVR e alla scelta del bitstream: ffmpeg li legge dal disco.
                    DiscProtection.Prepare();
                    foreach (string probeUrl in disc.ProbeUrls())
                    {
                        try { _info = await Task.Run(() => MediaProbe.Probe(probeUrl), ct); }
                        catch (Exception ex) { Dbg.Warn("[DISC] probe: " + ex.Message); _info = null; }
                        if (ct.IsCancellationRequested || mySerial != _openSerial) return;
                        if (_info != null) break;
                    }
                }
                else if (isYouTubePlaybackUrl)
                {
                    _info = null;
                }
                else
                {
                    try
                    {
                        ShowVideoLoading(Tx("Analizzo il contenuto…", "Analyzing media…"));
                        await Task.Yield();
                        _info = await Task.Run(() => MediaProbe.Probe(path), ct);
                    }
                    catch (Exception ex)
                    {
                        _lblStatus.Text = Tx("Analisi non riuscita: ", "Media analysis failed: ") + ex.Message;
                        _info = null;
                    }
                }

                if (ct.IsCancellationRequested || mySerial != _openSerial) return;

                // Estensione
                bool extLooksVideo = LooksLikeVideoByExt(path);
                bool extLooksPureAudio = LooksLikePureAudioByExt(path);

                // Se l’estensione è "solo audio", ignoriamo eventuali cover art / stream video strani
                bool hasVideo = forceHasVideo
                    ?? (extLooksVideo
                        ? true
                        : (!extLooksPureAudio && _info?.HasVideo == true));

                _currentMediaHasVideo = hasVideo;
                // Il probe conosce spesso la durata prima del renderer. Pubblicarla
                // qui evita che la timeline venga aggiunta un frame dopo il resto HUD.
                _duration = Math.Max(0, _info?.Duration ?? 0);
                SyncHudTimelineAvailability();
                try { EnsurePipModeStillValidForCurrentMedia(); } catch { }

                if (_stereoAutoEnabled && hasVideo)
                {
                    await PrepareAutoStereoForOpenAsync(path, _info, ct);
                    if (ct.IsCancellationRequested || mySerial != _openSerial) return;
                    if (_stereo != Stereo3DMode.None)
                    {
                        ShowVideoLoading(Tx("Configuro il video 3D…", "Configuring 3D video…"));
                        await Task.Yield();
                    }
                }

                bool fileHdr = _info?.IsHdr == true;
                string? effectiveAudioRendererName = ResolveEffectiveAudioRendererNameForOpen();
                bool bitstreamCapableOutput =
                    _selectedRendererLooksHdmi ||
                    LooksBitstreamCapableAudioOutput(effectiveAudioRendererName);
                // Di un disco non si conosce la traccia prima di aprirlo: Dolby e DTS sono la norma.
                bool passCandidate = isDisc || (_info != null && MediaProbe.IsPassthroughCandidate(_info.AudioCodec));

                // ⬅ se l’utente forza PCM, ignoriamo il flag "preferBitstream"
                bool wantBitstream = (_audioOutPref == AudioOutPref.Auto) && (_preferBitstreamUi && bitstreamCapableOutput && passCandidate);
                bool forcePcmToggle = _audioOutPref == AudioOutPref.ForcePcm;

                _openingMusic = !hasVideo;
                _lyricsService.SetVideoPlaybackActive(hasVideo);
                // Audio never needs the user's video renderer (madVR/MPCVR).
                var externalBinding = ExternalAudioStore.Get(path);
                if (externalBinding?.AudioPath is string audioFile && !File.Exists(audioFile))
                    throw new FileNotFoundException("La traccia audio esterna salvata non è disponibile.", audioFile);
                var order = BuildPlaybackRendererOrder(hasVideo, fileHdr);
                // Solo una traccia esterna richiede mpv: il ritardo e la scelta della traccia interna
                // funzionano anche con madVR / MPC VR / EVR (LAV Audio).
                if (externalBinding?.AudioPath != null) order = new[] { VRChoice.MPV };
                // Blu-ray visibile come cartella: tutti i renderer, con mpv di riserva. DVD e ISO non montate: solo mpv.
                else if (isDisc) order = disc.DirectShowPath == null ? new[] { VRChoice.MPV } : order.Where(c => c != VRChoice.MPV).Append(VRChoice.MPV).ToArray();
                // Musica locale in PCM: Cinecore Audio Engine (EQ, anti-clipping, misure dirette).
                // Se non si apre, il ciclo prosegue con mpv come prima.
                bool cinecoreAudioPending = !hasVideo && Audio.CinecoreAudioSettings.Current.Enabled && externalBinding == null
                    && string.IsNullOrEmpty(_currentWebAudioUrl) && !wantBitstream && File.Exists(path);
                if (cinecoreAudioPending) order = new[] { VRChoice.MPV }.Concat(order).ToArray();
                if (!hasVideo)
                    Dbg.Log($"[CAUDIO] engine={(cinecoreAudioPending ? "cinecore" : "mpv")}: enabled={Audio.CinecoreAudioSettings.Current.Enabled}, binding={externalBinding != null}, web={!string.IsNullOrEmpty(_currentWebAudioUrl)}, bitstream={wantBitstream}, exists={File.Exists(path)}");

                Dbg.Log($"OpenPath '{path}', HDR_File={fileHdr}, UI_HDR={_hdr}, UI_HDR_Profile={_hdrProfile}, playerHdrRequest={DescribeRequestedHdrOutput()}, hasVideo={hasVideo}, audioSelected='{_selectedAudioRendererName ?? "default"}', audioEffective='{effectiveAudioRendererName ?? "null"}', bitstreamCapableOutput={bitstreamCapableOutput}, passCandidate={passCandidate}, wantBitstream={wantBitstream}, order=[{string.Join(",", order)}]");

                if (wantBitstream)
                    WarmUpBitstreamAudioEndpointBeforeOpen(effectiveAudioRendererName);

                var pendingChoices = new List<VRChoice>(order);
                for (int choiceIndex = 0; choiceIndex < pendingChoices.Count; choiceIndex++)
                {
                    var choice = pendingChoices[choiceIndex];
                    try
                    {
                        ShowVideoLoading(Tx($"Apro il flusso ({choice})…", $"Opening stream ({choice})…"));
                        await Task.Yield();
                        if (ct.IsCancellationRequested || mySerial != _openSerial) return;

                        _stopping = false;
                        IPlaybackEngine playbackEngine;
                        if (choice == VRChoice.MPV && cinecoreAudioPending)
                        {
                            cinecoreAudioPending = false;
                            playbackEngine = new Audio.CinecoreAudioEngine(effectiveAudioRendererName);
                        }
                        else if (choice == VRChoice.MPV)
                        {
                            var mpvEngine = new LibMpvPlaybackEngine(
                                preferBitstream: wantBitstream,
                                forcePcmToggle: forcePcmToggle,
                                preferredAudioDeviceName: effectiveAudioRendererName,
                                fileIsHdr: fileHdr,
                                srcAudioCodec: _info?.AudioCodec ?? AVCodecID.AV_CODEC_ID_NONE,
                                runtimeChoice: GetEffectiveMpvRuntimeChoice(),
                                settings: _mpvSettings);

                            if (hasVideo)
                            {
                                try
                                {
                                    nint owner = _videoHost.Handle;
                                    mpvEngine.SetInitialVideoHost(owner, _videoHost.ClientRectangle);
                                }
                                catch { }
                            }

                            playbackEngine = mpvEngine;
                        }
                        else
                        {
                            var dsEngine = new DirectShowUnifiedEngine(
                                preferBitstream: wantBitstream,
                                forcePcmToggle: forcePcmToggle,
                                preferredRendererName: effectiveAudioRendererName,
                                choice: choice,
                                fileIsHdr: fileHdr,
                                srcAudioCodec: _info?.AudioCodec ?? AVCodecID.AV_CODEC_ID_NONE,
                                enableMpcvrRtxVideoHdr: choice == VRChoice.MPCVR && _hdrProfile == HdrUiProfile.RtxVideoHdr,
                                mpcvrSuperResolutionMode: choice == VRChoice.MPCVR &&
                                                          string.Equals(_videoUpscalingBackend, "mpcvr", StringComparison.OrdinalIgnoreCase)
                                    ? _mpcvrSuperResolutionMode
                                    : 0);

                            if (choice == VRChoice.MPCVR && hasVideo)
                            {
                                try
                                {
                                    nint owner = _videoHost.Handle;
                                    dsEngine.SetInitialVideoHost(owner, _videoHost.ClientRectangle);
                                }
                                catch { }
                            }

                            playbackEngine = dsEngine;
                        }

                        _engine = playbackEngine;
                        _activeRendererChoice = choice;
                        _mpcvrExclusiveMode = false;

                        _engineStatusHandler = s =>
                        {
                            if (_stopping || mySerial != Volatile.Read(ref _openSerial) || !ReferenceEquals(_engine, playbackEngine)) return;
                            TryBeginInvokeOnUi(() =>
                            {
                                if (_stopping || mySerial != Volatile.Read(ref _openSerial) || !ReferenceEquals(_engine, playbackEngine)) return;
                                _lblStatus.Text = LocalizePlaybackStatus(s);
                            });
                        };
                        double latestProgress = 0;
                        int progressQueued = 0;
                        int videoUpdateQueued = 0;
                        _engineProgressHandler = s =>
                        {
                            if (_stopping || mySerial != Volatile.Read(ref _openSerial) || !ReferenceEquals(_engine, playbackEngine)) return;
                            Volatile.Write(ref latestProgress, s);
                            if (Interlocked.Exchange(ref progressQueued, 1) != 0) return;
                            if (!TryBeginInvokeOnUi(() =>
                            {
                                Interlocked.Exchange(ref progressQueued, 0);
                                if (_stopping || mySerial != Volatile.Read(ref _openSerial) || !ReferenceEquals(_engine, playbackEngine)) return;
                                OnEngineProgress(Volatile.Read(ref latestProgress));
                            })) Interlocked.Exchange(ref progressQueued, 0);
                        };
                        _engineUpdateHandler = () =>
                        {
                            if (_stopping || mySerial != Volatile.Read(ref _openSerial) || !ReferenceEquals(_engine, playbackEngine)) return;
                            if (Interlocked.Exchange(ref videoUpdateQueued, 1) != 0) return;
                            if (!TryBeginInvokeOnUi(() =>
                            {
                                Interlocked.Exchange(ref videoUpdateQueued, 0);
                                if (_stopping || mySerial != Volatile.Read(ref _openSerial) || !ReferenceEquals(_engine, playbackEngine)) return;
                                UpdateVideoWindowForCurrentHost();
                                SyncOverlayToVideoRect();
                                if (_infoOverlay?.Visible == true && _engine != null)
                                {
                                    UpdateInfoOverlay(ResolveRendererForInfo(fileHdr), fileHdr);
                                }
                                BringOverlaysToFront();
                            })) Interlocked.Exchange(ref videoUpdateQueued, 0);
                        };

                        _engine.OnStatus += _engineStatusHandler;
                        _engine.OnProgressSeconds += _engineProgressHandler;
                        _engine.BindUpdateCallback(_engineUpdateHandler);

                        _engineBitstreamHandler = active => OnEngineBitstreamChanged(active, playbackEngine, mySerial);
                        _engine.OnBitstreamChanged += _engineBitstreamHandler;

                        UseOverlayInline(false);
                        if (!string.IsNullOrEmpty(_currentWebAudioUrl))
                        {
                            try
                            {
                                var tEngine = _engine!.GetType();
                                var mExtAudio = tEngine.GetMethod(
                                    "SetExternalAudioUrl",
                                    System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic);

                                if (mExtAudio != null)
                                {
                                    mExtAudio.Invoke(_engine, new object[] { _currentWebAudioUrl! });
                                    Debug.WriteLine($"[Resolver] external audio URL passata all'engine: {_currentWebAudioUrl}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine("[Resolver] SetExternalAudioUrl reflection failed: " + ex.Message);
                            }
                        }

                        if (externalBinding?.AudioPath is string externalPath && _engine is LibMpvPlaybackEngine externalMpv)
                            externalMpv.SetExternalAudioUrl(externalPath);
                        if (externalBinding?.AudioPath == null && externalBinding?.TargetOrdinal is int targetOrdinal && _engine is LibMpvPlaybackEngine internalMpv)
                            internalMpv.SetInitialInternalAudioOrdinal(targetOrdinal);
                        // Il 3D -> 2D ha bisogno delle dimensioni fisiche e del SAR del file.
                        if (_info != null && _info.Width > 0 && _info.Height > 0)
                        {
                            if (_engine is DirectShowUnifiedEngine stereoDs)
                            {
                                stereoDs.SourceFrameSize = new Size(_info.Width, _info.Height);
                                stereoDs.SourceSampleAspect = _info.SampleAspect;
                            }
                            else if (_engine is LibMpvPlaybackEngine stereoMpv)
                            {
                                stereoMpv.SourceFrameSize = new Size(_info.Width, _info.Height);
                                stereoMpv.SourceSampleAspect = _info.SampleAspect;
                            }
                        }
                        _engine.Open(path, hasVideo);
                        if (externalBinding?.HasCurve == true) StartAudioDelayFollower();
                        if (externalBinding != null && _engine is LibMpvPlaybackEngine syncMpv && !syncMpv.SetAudioDelayMilliseconds(externalBinding.DelayAt(resume)))
                            throw new InvalidOperationException("MPV non ha applicato il ritardo audio richiesto.");
                        if (_engine is DirectShowUnifiedEngine syncDs)
                        {
                            // LAV conserva il ritardo nel registro: ogni file riparte dal proprio (o da 0).
                            try { syncDs.SetAudioDelayMilliseconds(externalBinding?.DelayAt(resume) ?? 0); } catch { }
                            if (externalBinding?.TargetOrdinal is int dsTarget)
                            {
                                try
                                {
                                    var dsTrack = syncDs.EnumerateStreams().Where(x => x.IsAudio && !x.IsExternal).ElementAtOrDefault(dsTarget);
                                    if (dsTrack != null && !dsTrack.Selected) syncDs.EnableByGlobalIndex(dsTrack.GlobalIndex);
                                }
                                catch { }
                            }
                        }

                        // Notifica fine playback (EC_COMPLETE) per ritorno libreria anche dopo seek/skip.
                        try { TryBindGraphNotify(); } catch { }
                        try
                        {
                            bool isBsInit = _engine.IsBitstreamActive();
                            _lastIsBsLogged = isBsInit; // baseline per evitare doppio log al primo Tick
                            Debug.WriteLine($"[Cinecore] (init) IsBitstreamActive -> {(isBsInit ? "Bitstream" : "PCM")} @ {DateTime.Now:HH:mm:ss.fff}");
                        }
                        catch { /* best-effort */ }

                        // reset contatori bitrate / medie
                        _ioPrevBytes = 0;
                        _ioPrevWhen = DateTime.MinValue;
                        _containerBitrateNowKbps = 0;

                        _audioBitrateNowKbps = 0;
                        _videoBitrateNowKbps = 0;

                        _avgLastPublish = DateTime.MinValue;
                        _avgLastTs = DateTime.MinValue;
                        _avgAudioBitSec = 0;
                        _avgVideoBitSec = 0;
                        _avgDurSec = 0;
                        _audioAvgLiveKbps = 0;
                        _videoAvgLiveKbps = 0;

                        _duration = _engine.DurationSeconds > 0 ? _engine.DurationSeconds : (_info?.Duration ?? 0);

                        bool hasDisplay = _engine.HasDisplayControl();
                        if (!hasVideo)
                        {
                            ActivateMusicWorkspace();
                            StartAudioMetersIfPossible();
                            RefreshMusicPresentationForCurrentMedia();
                        }
                        else
                        {
                            StopAudioMeters();
                            ResetAudioOverlayState();
                            ResetMusicPresentation();
                        }

                        _duration = _engine.DurationSeconds > 0 ? _engine.DurationSeconds : (_info?.Duration ?? 0);
                        SyncHudTimelineAvailability();

                        BringOverlaysToFront();

                        try { _thumbCts?.Cancel(); } catch { }
                        try { _previewCache.Clear(); } catch { }
                        // Timeline preview opens its decoder on demand. Avoid a second
                        // decoder competing for disk and CPU during playback startup.
                        // Il campionatore di bitrate apre il file una seconda volta e
                        // avformat_find_stream_info può bloccare per secondi, soprattutto
                        // su dischi di rete. È telemetria: avvialo dopo il primo frame.
                        _pktRateOk = _pktRateAudioOk = false;
                        _lastPktSample = DateTime.MinValue;
                        Volatile.Write(ref _packetRateSampleActive, 0);
                        _aNowTs = _vNowTs = DateTime.MinValue;

                        bool keepBothEyes = _extendedFullscreen && Screen.AllScreens.Length > 1;
                        if (_engine is DirectShowUnifiedEngine keepDs) keepDs.StereoKeepBothEyes = keepBothEyes;
                        else if (_engine is LibMpvPlaybackEngine keepMpv) keepMpv.StereoKeepBothEyes = keepBothEyes;
                        _engine.SetStereo3D(_stereo);
                        _engine.SetUpscaling(_enableUpscaling);
                        if (choice == VRChoice.MADVR && _enableUpscaling &&
                            string.Equals(_videoUpscalingBackend, "madvr", StringComparison.OrdinalIgnoreCase) &&
                            _madVrUpscalePreset != MadVrCategoryPreset.RendererDefault)
                        {
                            try { _engine.SetMadVrImageUpscale(_madVrUpscalePreset); } catch { }
                        }
                        UpdateVideoWindowForCurrentHost();

                        SafeShowOverlayHost();
                        SyncOverlayToVideoRect();
                        BringOverlaysToFront();

                        AutoSelectDefaultStreams();
                        ScheduleRemoteTrackSnapshotRefresh(mySerial, playbackEngine);

                        UpdateInfoOverlay(choice, fileHdr);

                        // All'ingresso del contenuto non vogliamo HUD automatico:
                        // comparirà solo su input reale dell'utente / seek da remote.
                        SuppressHudForProgrammaticTransition(1200);

                        _paused = startPaused;

                        bool isMpcvr = choice == VRChoice.MPCVR;
                        bool isMadvr = choice == VRChoice.MADVR;
                        if (hasVideo && (isMpcvr || isMadvr) && FormBorderStyle != FormBorderStyle.None)
                            ToggleFullscreen();

                        if (isMpcvr)
                            UpdateVideoWindowForCurrentHost();

                        if (isMadvr)
                            TryApplyMadVrRefreshForPlayback(choice, hasVideo);

                        // Ripristina la posizione solo dopo selezione stream, impostazioni
                        // renderer e passaggio alla geometria fullscreen definitiva. Prima
                        // il seek veniva eseguito sul graph appena creato e poi invalidato da
                        // questi cambi, causando un secondo preroll visibile all'avvio.
                        if (resume > 0.01 && _duration > 0)
                        {
                            double restoredPosition = Math.Min(resume, Math.Max(0.01, _duration));
                            try { _engine.Pause(); } catch { }
                            try { _engine.PositionSeconds = restoredPosition; } catch { }
                            CaptureExplicitSeek(restoredPosition);

                            // Lascia decodificare il primo frame alla posizione ripristinata
                            // mentre il mask nero e' ancora visibile.
                            int prerollMs = (isMadvr || isMpcvr) ? 240 : 140;
                            try { await Task.Delay(prerollMs, ct); } catch (OperationCanceledException) { return; }
                            if (ct.IsCancellationRequested || mySerial != _openSerial || !ReferenceEquals(_engine, playbackEngine))
                                return;
                            UpdateVideoWindowForCurrentHost();
                        }

                        if (isMadvr && _engine is DirectShowUnifiedEngine prerollEngine)
                        {
                            // Let madVR and the audio renderer fill their queues in
                            // the final display mode before starting the graph clock.
                            prerollEngine.Pause();
                            var preroll = Stopwatch.StartNew();
                            while (preroll.ElapsedMilliseconds < 1500)
                            {
                                await Task.Delay(40, ct);
                                if (mySerial != _openSerial || !ReferenceEquals(_engine, playbackEngine)) return;
                                if (preroll.ElapsedMilliseconds >= 240 && prerollEngine.IsPrerollReady) break;
                            }
                            Dbg.Log($"[madVR] Initial preroll {preroll.ElapsedMilliseconds}ms, ready={prerollEngine.IsPrerollReady}");
                        }
                        // Tutto cio' che occupa il thread dell'interfaccia va fatto PRIMA di Run():
                        // il renderer presenta attraverso questa finestra, e ogni blocco subito dopo
                        // l'avvio (volume sul dispositivo audio che si sta aprendo, cambio pagina,
                        // luci) gli faceva buttare i fotogrammi del primo secondo.
                        float openVolume = FilmVolumeOnOpen(_currentPath, hasVideo);
                        // Con l'amplificatore collegato il cursore parte dal suo volume reale, non da 100%.
                        // ...e l'amplificatore non riparte mai piu' forte dell'ultima volta.
                        BeginAmplifierSafeStart();
                        ApplyImageSizing();
                        ApplyVolume(openVolume);
                        CompleteLibraryToPlaybackTransition(deferredLibraryTransition);
                        ApplyAmbientLightingForCurrentState();

                        var afterPlay = Stopwatch.StartNew(); long afterPlayMark = 0;
                        // Resta nel log solo quando un passo dopo l'avvio trattiene l'interfaccia.
                        void Mark(string step) { long now = afterPlay.ElapsedMilliseconds; if (now - afterPlayMark >= 60) Dbg.Warn($"[OPEN-TIMING] {step}: {now - afterPlayMark} ms on the UI thread right after start"); afterPlayMark = now; }
                        if (startPaused) _engine.Pause(); else if (_engine is DirectShowUnifiedEngine firstStart) firstStart.PlayFirstStart(); else _engine.Play();
                        afterPlayMark = afterPlay.ElapsedMilliseconds;
                        SyncHudPlayingState();
                        Mark("SyncHudPlayingState");

                        if (isMpcvr)
                        {
                            UpdateVideoWindowForCurrentHost();
                            ForceMpcvrChildWindowsVisible("after Play");
                            try { RefreshMpcvrChildWindowHooks(); } catch { }
                            try { UpdateMpcvrOverlayRegionMode(); } catch { }
                            try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                            if (!openInitiatedByRemote)
                                _suppressHudWakeUntilUtc = DateTime.MinValue;
                        }

                        Mark("renderer host");
                        NotifyAutomationPlaybackOpened(); Mark("NotifyAutomation");
                        JellyfinPlaybackOpened(); Mark("Jellyfin");
                        ReapplyAudioVolumeAfterGraphStart(openVolume);

                        if (hasVideo && !isMpcvr && !isMadvr && FormBorderStyle != FormBorderStyle.None) ToggleFullscreen();

                        var t = new System.Windows.Forms.Timer { Interval = 300 };
                        t.Tick += (_, __) =>
                        {
                            try
                            {
                                if (mySerial != _openSerial || !ReferenceEquals(_engine, playbackEngine)) return;
                                if (!hasVideo) { UpdateMusicTransport(); return; }
                                // Display cadence was settled before preroll; changing it after Run restarts presentation.
                                UpdateVideoWindowForCurrentHost();
                                ForceMpcvrChildWindowsVisible("startup timer");
                                if (isMpcvr)
                                {
                                    try { RefreshMpcvrChildWindowHooks(); } catch { }
                                }
                                SyncOverlayToVideoRect();
                                BringOverlaysToFront();
                                if (!isMpcvr)
                                    SuppressHudForProgrammaticTransition(600);
                                else if (!openInitiatedByRemote)
                                    _suppressHudWakeUntilUtc = DateTime.MinValue;
                            }
                            catch { }
                            finally { t.Stop(); t.Dispose(); }
                        };
                        t.Start();

                        Mark("fullscreen"); LogVideoPipelineDiagnostics("OpenPath ready", choice, fileHdr, hasVideo); Mark("LogVideoPipelineDiagnostics");
                        ScheduleMadVrHdrProfileApply(_engine!, choice, fileHdr, hasVideo);

                        bool okDisplay = _engine.HasDisplayControl();
                        if (!hasVideo || okDisplay || choice == VRChoice.MADVR || choice == VRChoice.MPCVR)
                        {
                            // madVR può iniziare a riprodurre audio mentre il suo child window/video
                            // non risulta ancora pronto a HasDisplayControl(). In quel caso non tenere
                            // la maschera di caricamento davanti al video: sembra un freeze, ma il graph gira.
                            string tag = fileHdr ? "HDR" : "SDR";
                            _lblStatus.Text = (!hasVideo)
                                ? Tx("Riproduzione (solo audio)", "Playing (audio only)")
                                : Tx($"Riproduzione ({choice} • {tag})", $"Playing ({choice} • {tag})");

                            // Non scoprire il renderer nello stesso istante in cui Run()
                            // avvia le code. Attendi un avanzamento reale del clock (o un
                            // breve preroll nel caso paused): evita frame parziali, flash e
                            // resize visibili tra maschera e primo frame stabile.
                            if (hasVideo)
                            {
                                bool presentationReady = await WaitForStableVideoPresentationAsync(
                                    playbackEngine, choice, startPaused, mySerial, ct);
                                if (!presentationReady)
                                {
                                    if (ct.IsCancellationRequested || mySerial != _openSerial ||
                                        !ReferenceEquals(_engine, playbackEngine)) return;
                                    throw new InvalidOperationException($"{choice}: nessuna finestra video disponibile dopo l'avvio del graph.");
                                }
                            }

                            HideVideoLoading();
                            StartPacketRateSamplersAfterOpen(path, _currentWebAudioUrl, playbackEngine, mySerial, ct);
                            if (hasVideo) Dbg.Log($"Presentation ready: ui={!InvokeRequired}, thread={Environment.CurrentManagedThreadId}, context={SynchronizationContext.Current?.GetType().Name}");
                            if (!hasVideo) ActivateMusicWorkspace();
                            UpdateIntroOutroPromptForPlayback();
                            return;
                        }

                        throw new Exception("Renderer non pronto (nessun display control) → fallback");
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn($"OpenPath: renderer {choice} EX: " + ex);
                        try { _engine?.Dispose(); } catch { }
                        _engine = null;
                        _activeRendererChoice = null;
                        _mpcvrExclusiveMode = false;

                        // Il file non offre video a DirectShow: nessun altro renderer del grafo puo'
                        // riuscirci, mpv si' (anche quando l'utente ha fissato madVR o MPC VR).
                        if (hasVideo && ex is VideoTrackUnavailableException)
                        {
                            pendingChoices.RemoveRange(choiceIndex + 1, pendingChoices.Count - choiceIndex - 1);
                            if (choice != VRChoice.MPV) pendingChoices.Add(VRChoice.MPV);
                            Dbg.Warn("[OPEN] DirectShow has no video track for this file: falling back to mpv.");
                        }

                        if (!_manualRendererChoice.HasValue &&
                            choice == VideoRendererChoice.MADVR &&
                            (ex.Message?.IndexOf("madVR non trovato", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            _lblStatus.Text = Tx("madVR non trovato: provo un renderer alternativo.", "madVR was not found; trying another renderer.");
                        }
                    }
                }

                // La demo pre-film non si e' aperta (file rovinato, codec mancante): il film parte
                // comunque. Prima restava tutto fermo con il film in attesa per sempre.
                if (_playingPreRoll && !string.IsNullOrWhiteSpace(_pendingMainPathAfterPreRoll))
                {
                    Dbg.Warn("[OPEN] pre-roll demo failed to open; starting the main title.");
                    TrySkipActivePreRollToMainContent();
                    return;
                }

                _lblStatus.Text = !hasVideo ? Tx("Impossibile riprodurre il brano", "Unable to play this track") : _manualRendererChoice.HasValue
                    ? Tx($"Impossibile presentare il video con {_manualRendererChoice.Value}", $"Unable to present video with {_manualRendererChoice.Value}")
                    : Tx("Impossibile presentare il video con i renderer selezionati", "Unable to present video with the selected renderers");
                HideVideoLoading();
                NotifyPlaybackStoppedForWled(forceRestore: true);
            }
            finally
            {
                if (_engine == null && transportOpenSerial == Volatile.Read(ref _openSerial)) _lyricsService.SetVideoPlaybackActive(false);
                if (transportOpenSerial == Volatile.Read(ref _openSerial)) _preserveMusicTransportOnOpen = false;
                UpdateMusicTransport();
                RestoreLibraryAfterFailedTransition(deferredLibraryTransition);
                if (queueTransitionRequested || _playbackQueueTransitionInProgress)
                    _playbackQueueTransitionInProgress = false;
            }
        }

        private void StartPacketRateSamplersAfterOpen(string path, string? audioUrl,
            IPlaybackEngine playbackEngine, int openSerial, CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                PacketRateSampler? main = null;
                PacketRateSampler? audio = null;
                try
                {
                    await Task.Delay(800, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested || openSerial != Volatile.Read(ref _openSerial))
                        return;

                    main = new PacketRateSampler();
                    bool mainReady = main.Open(path);
                    bool audioReady = false;
                    if (!string.IsNullOrEmpty(audioUrl))
                    {
                        audio = new PacketRateSampler();
                        audioReady = audio.Open(audioUrl);
                    }

                    PacketRateSampler publishedMain = main;
                    PacketRateSampler? publishedAudio = audio;
                    if (!TryBeginInvokeOnUi(() =>
                    {
                        if (cancellationToken.IsCancellationRequested || openSerial != Volatile.Read(ref _openSerial) ||
                            !ReferenceEquals(_engine, playbackEngine))
                        {
                            publishedMain.Dispose();
                            publishedAudio?.Dispose();
                            return;
                        }
                        try { _pktRate.Dispose(); } catch { }
                        try { _pktRateAudio.Dispose(); } catch { }
                        _pktRate = publishedMain;
                        _pktRateAudio = publishedAudio ?? new PacketRateSampler();
                        _pktRateOk = mainReady;
                        _pktRateAudioOk = audioReady;
                        _lastPktSample = DateTime.MinValue;
                    }))
                    {
                        publishedMain.Dispose();
                        publishedAudio?.Dispose();
                    }
                    main = null;
                    audio = null;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Dbg.Warn("Packet-rate sampler init failed: " + ex.Message); }
                finally
                {
                    try { main?.Dispose(); } catch { }
                    try { audio?.Dispose(); } catch { }
                }
            });
        }

        private async Task<bool> WaitForStableVideoPresentationAsync(
            IPlaybackEngine engine,
            VRChoice choice,
            bool startPaused,
            int openSerial,
            CancellationToken cancellationToken)
        {
            int minimumPrerollMs = (choice == VRChoice.MADVR || choice == VRChoice.MPCVR) ? 280 : 160;
            int clockFallbackMs = (choice == VRChoice.MADVR || choice == VRChoice.MPCVR) ? 720 : 460;
            double initialPosition = 0;
            try { initialPosition = Math.Max(0, engine.PositionSeconds); } catch { }

            // madVR assesta le sue code nel primo mezzo secondo (qualche fotogramma scartato): la
            // maschera resta finche' i fotogrammi scorrono senza nuovi scarti, cosi' il film
            // compare gia' fluido invece di partire a scatti.
            int steadySamples = 0, lastDrawn = -1, lastDropped = -1;
            bool MadVrFlowIsSteady()
            {
                if (choice != VRChoice.MADVR || startPaused || engine is not DirectShowUnifiedEngine ds) return true;
                try
                {
                    var report = ds.GetComponentReport();
                    int drawn = report.FramesDrawn, dropped = report.FramesDropped;
                    steadySamples = lastDrawn >= 0 && drawn > lastDrawn && dropped == lastDropped ? steadySamples + 1 : 0;
                    lastDrawn = drawn; lastDropped = dropped;
                    return steadySamples >= 5;
                }
                catch { return true; }
            }

            var stopwatch = Stopwatch.StartNew();
            int limitMs = choice == VRChoice.MADVR ? 1900 : 1200;
            while (stopwatch.ElapsedMilliseconds < limitMs)
            {
                try { await Task.Delay(40, cancellationToken); }
                catch (OperationCanceledException) { return false; }

                if (cancellationToken.IsCancellationRequested ||
                    openSerial != Volatile.Read(ref _openSerial) ||
                    !ReferenceEquals(_engine, engine))
                    return false;

                bool mpcvrWindowReady = choice == VRChoice.MPCVR && HasMpcvrVideoChildWindow();
                // madVR/EVR/MPV were already placed before Play. Reapplying their
                // native window geometry every 40 ms only adds startup work.
                if (choice == VRChoice.MPCVR && !mpcvrWindowReady)
                    try { UpdateVideoWindowForCurrentHost(); } catch { }

                if (stopwatch.ElapsedMilliseconds < minimumPrerollMs)
                    continue;

                if (choice == VRChoice.MPCVR && !mpcvrWindowReady && !engine.HasDisplayControl())
                    continue;

                if (startPaused)
                    return true;

                double position = initialPosition;
                try { position = Math.Max(0, engine.PositionSeconds); } catch { }
                bool clockAdvanced = position >= initialPosition + 0.025;
                bool steady = MadVrFlowIsSteady();
                if ((clockAdvanced || stopwatch.ElapsedMilliseconds >= clockFallbackMs) && steady)
                    return true;
            }

            return choice != VRChoice.MPCVR || HasMpcvrVideoChildWindow() || engine.HasDisplayControl();
        }

        private VRChoice[] BuildPlaybackRendererOrder(bool hasVideo, bool fileHdr)
            => hasVideo ? BuildRendererOpenOrder(fileHdr) : new[] { VRChoice.MPV, VRChoice.EVR };

        private VRChoice[] BuildRendererOpenOrder(bool fileHdr)
        {
            VRChoice? preferred;
            if (_hasRuntimeRendererOverride)
            {
                preferred = _runtimeRendererChoiceOverride;
            }
            else if (_enableUpscaling && string.Equals(_videoUpscalingBackend, "madvr", StringComparison.OrdinalIgnoreCase))
            {
                preferred = VRChoice.MADVR;
            }
            else if (_enableUpscaling && string.Equals(_videoUpscalingBackend, "mpcvr", StringComparison.OrdinalIgnoreCase))
            {
                preferred = VRChoice.MPCVR;
            }
            else
            {
                preferred = _manualRendererChoice;
            }
            if (preferred.HasValue)
                return new[] { preferred.Value };

            var result = new List<VRChoice>(fileHdr ? ORDER_HDR : ORDER_SDR);

            foreach (var fallback in new[] { VRChoice.MPCVR, VRChoice.EVR, VRChoice.MADVR, VRChoice.MPV })
            {
                if (!result.Contains(fallback))
                    result.Add(fallback);
            }

            return result.ToArray();
        }

        private VRChoice ResolveRendererForInfo(bool fileHdr)
        {
            return _activeRendererChoice ??
                   (_hasRuntimeRendererOverride ? _runtimeRendererChoiceOverride : _manualRendererChoice) ??
                   (fileHdr ? ORDER_HDR.First() : ORDER_SDR.First());
        }

        private int _imageOpenVersion;
        private void OpenImage(string path) => _ = OpenImageAsync(path);

        private async Task OpenImageAsync(string path)
        {
            int imageVersion = ++_imageOpenVersion;
            _currentPath = path;
            _info = null;
            _stopping = false;
            _duration = 0;
            _paused = true;
            SyncHudPlayingState();
            _currentMediaHasVideo = true;
            _currentLibraryCategory = "Foto";
            try { EnsurePipModeStillValidForCurrentMedia(); } catch { }

            // Playlist immagini: tutte le immagini nella stessa cartella
            try
            {
                var galleryPhotos = _cinematicLibraryPage?.GetPhotoPathsForViewer(path);
                var dir = ImagePlaybackEngine.IsRemoteImage(path) ? null : Path.GetDirectoryName(path);
                if (galleryPhotos is { Count: > 0 })
                {
                    _imageFiles = galleryPhotos.ToList();
                    _imageIndex = _imageFiles.FindIndex(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
                    _imageFolderPath = null;
                }
                else if (!string.IsNullOrEmpty(dir))
                {
                    if (!string.Equals(_imageFolderPath, dir, StringComparison.OrdinalIgnoreCase) || _imageFiles.Count == 0)
                    {
                        var files = await Task.Run(() => Directory.EnumerateFiles(dir)
                            .Where(ImagePlaybackEngine.IsImageFile)
                            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                            .ToList());
                        if (IsDisposed || _closingForExit || imageVersion != _imageOpenVersion || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
                        _imageFiles = files;
                        _imageFolderPath = dir;
                    }

                    _imageIndex = _imageFiles.FindIndex(f =>
                        string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                    if (_imageFiles.Count > 0 && _imageIndex < 0)
                    {
                        _imageFiles.Add(path);
                        _imageFiles.Sort(StringComparer.CurrentCultureIgnoreCase);
                        _imageIndex = _imageFiles.FindIndex(f =>
                            string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                        if (_imageIndex < 0)
                            _imageIndex = 0;
                    }
                }
                else
                {
                    var networkPhotos = _cinematicLibraryPage?.GetNetworkPhotoPaths(path);
                    _imageFiles = networkPhotos?.ToList() ?? new List<string> { path };
                    _imageIndex = Math.Max(0, _imageFiles.FindIndex(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase)));
                    _imageFolderPath = null;
                }
            }
            catch
            {
                if (IsDisposed || _closingForExit || imageVersion != _imageOpenVersion || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
                _imageFiles = new List<string> { path };
                _imageIndex = 0;
                _imageFolderPath = null;
            }

            // niente bitstream
            try { _thumbCts?.Cancel(); } catch { }
            try { _previewCache.Clear(); } catch { }
            // La timeline è disattivata in modalità Foto: aprire anche il decoder
            // FFmpeg per ogni JPG/PNG duplicava I/O e decode prima di ogni cambio.

            // Decode and fetch outside the UI thread; keep the previous frame
            // until this request is ready, including rapid photo navigation.
            bool browsingInPlace = _engine is ImagePlaybackEngine && _photoHud.Visible;
            var preparedEngine = _engine as ImagePlaybackEngine ?? new ImagePlaybackEngine();
            PreparedPhoto prepared;
            try { prepared = await preparedEngine.PrepareImageAsync(path); }
            catch (Exception ex)
            {
                if (!ReferenceEquals(preparedEngine, _engine)) preparedEngine.Dispose();
                if (!IsDisposed && imageVersion == _imageOpenVersion) { _lblStatus.Text = ex.Message; HideVideoLoading(); }
                return;
            }
            if (IsDisposed || _closingForExit || imageVersion != _imageOpenVersion || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                prepared.Dispose();
                if (!ReferenceEquals(preparedEngine, _engine)) preparedEngine.Dispose();
                return;
            }
            // riusa engine se già ImagePlaybackEngine, altrimenti creane uno
            if (_engine is ImagePlaybackEngine imgEngine)
            {
                imgEngine.OpenPrepared(path, prepared);
            }
            else
            {
                try { _engine?.Dispose(); } catch { }
                var imageEngine = preparedEngine;
                _engine = imageEngine;
                imageEngine.NavigateRequested += direction => { if (direction < 0) ShowPrevImage(); else ShowNextImage(); };
                imageEngine.Interaction += () => _photoHud?.Wake(2200);
                imageEngine.ViewChanged += UpdatePhotoHudInfo;

                _engineStatusHandler = s =>
                {
                    if (_stopping || !ReferenceEquals(_engine, imageEngine)) return;
                    if (!IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (_stopping || !ReferenceEquals(_engine, imageEngine)) return;
                            _lblStatus.Text = LocalizePlaybackStatus(s);
                        }));
                    }
                    catch { }
                };

                _engineUpdateHandler = () =>
                {
                    if (_stopping || !ReferenceEquals(_engine, imageEngine)) return;
                    if (!IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (_stopping || !ReferenceEquals(_engine, imageEngine)) return;
                            UpdateVideoWindowForCurrentHost();
                            SyncOverlayToVideoRect();
                            BringOverlaysToFront();
                        }));
                    }
                    catch { }
                };

                if (_engine != null)
                {
                    _engine.OnStatus += _engineStatusHandler;
                    _engine.BindUpdateCallback(_engineUpdateHandler);
                }

                UseOverlayInline(false);
                preparedEngine.OpenPrepared(path, prepared);
            }

            if (_engine is ImagePlaybackEngine preloadEngine && _imageFiles.Count > 1 && _imageIndex >= 0)
            {
                // Forward browsing is the common case: keep two ahead and one behind.
                int count = _imageFiles.Count;
                preloadEngine.QueuePreload(new[]
                {
                    _imageFiles[(_imageIndex + 1) % count],
                    _imageFiles[(_imageIndex - 1 + count) % count],
                    _imageFiles[(_imageIndex + 2) % count]
                });
            }

            if (browsingInPlace)
            {
                // Same viewer, next photo: the shell, overlay host and HUD are already
                // laid out, so only the caption changes.
                UpdatePhotoHudInfo();
                _lblStatus.Text = Tx("Immagine: ", "Image: ") + Path.GetFileName(path);
                return;
            }

            StopAudioMeters();
            _audioOnlyBanner.Visible = false;
            // Photos use the windowed transition too; that path intentionally keeps
            // the library visible for music. Reveal the decoded photo explicitly.
            _videoDetachedForPausePlaceholder = false;
            HideCinematicLibraryForPlayback();
            UpdateVideoWindowForCurrentHost();

            if (_engine is ImagePlaybackEngine photoEngine) photoEngine.AttachOverlay(_photoHud);

            SafeShowOverlayHost();
            SyncOverlayToVideoRect();

            // HUD classica completamente OFF in modalità foto
            _hud.Visible = false;
            _hud.TimelineVisible = false;
            _photoHud.Visible = true;
            UpdatePhotoHudInfo();
            try { _photoHud.Wake(); } catch { }
            BringOverlaysToFront();

            // Info overlay opzionale (lasciata, ma parte nascosta)
            try { UpdateInfoOverlay(VRChoice.EVR, fileHdr: false); } catch { }

            _lblStatus.Text = Tx("Immagine: ", "Image: ") + Path.GetFileName(path);
        }

        private void UpdatePhotoHudInfo()
        {
            if (_photoHud == null)
                return;

            var image = _engine as ImagePlaybackEngine;
            string mode = image?.ViewMode switch
            {
                ImageViewMode.Fill => Tx("Riempi", "Fill"),
                ImageViewMode.ActualSize => Tx("Originale", "Original"),
                _ => Tx("Adatta", "Fit")
            };
            string title = string.IsNullOrWhiteSpace(_currentPath)
                ? string.Empty
                : Path.GetFileName(ImagePlaybackEngine.IsRemoteImage(_currentPath) ? new Uri(_currentPath).AbsolutePath : _currentPath);
            string detail = string.Empty;
            string zoom = "100%";
            if (image != null)
            {
                var format = image.GetNegotiatedVideoFormat();
                if (format.width > 0) detail = $"{format.width} × {format.height}";
                zoom = $"{image.Zoom * 100:0}%";
            }
            _photoHud.SetPhotoInfo(title, detail, _imageIndex, _imageFiles.Count, mode, zoom);
        }


    }
}
