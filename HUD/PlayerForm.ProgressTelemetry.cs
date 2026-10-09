#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private long _timelineSampleTimestamp;
        private IPlaybackEngine? _madVrPerfEngine;
        private long _madVrPerfLastSample;
        private (int Drawn, int Dropped)? _madVrPerfPreviousFrames;
        private double _madVrPerfPreviousPosition;
        private bool _madVrPerfDisabled;
        private const double MadVrPerfIntervalSeconds = 30;
        private const double MadVrPerfMaxQueryMs = 25;
        private long _lastSlowProgressUiLog;
        private void OnEngineProgress(double cur)
        {
            if (_closingForExit || _stopping || !double.IsFinite(cur)) return;
            long started = Stopwatch.GetTimestamp();
            try { UpdatePlaybackProgress(cur); }
            finally
            {
                double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (_activeRendererChoice == VRChoice.MADVR && !_paused && elapsedMs > 30 &&
                    Stopwatch.GetElapsedTime(_lastSlowProgressUiLog).TotalSeconds >= 2)
                {
                    _lastSlowProgressUiLog = Stopwatch.GetTimestamp();
                    Dbg.Warn($"[VIDEO-PERF] Slow progress UI work: elapsedMs={elapsedMs:0.0}.");
                }
            }
        }

        private void UpdatePlaybackProgress(double cur)
        {
            _lastKnownPlaybackPosition = Math.Max(0, cur);
            _timelineSampleTimestamp = Stopwatch.GetTimestamp();
            SyncDvdState();
            SyncCastButton();
            if (HasMusicPlayback && !_paused && !string.IsNullOrWhiteSpace(_currentPath))
                MusicListeningStore.Record(_currentPath, cur, GetTimelineDurationSeconds());
            if (_duration <= 0)
            {
                double discoveredDuration = GetTimelineDurationSeconds();
                if (discoveredDuration > 0)
                    _duration = discoveredDuration;
            }
            SyncHudTimelineAvailability();
            if (_timelineUiPositionOverride >= 0 &&
                (DateTime.UtcNow >= _timelineUiOverrideUntilUtc || Math.Abs(cur - _timelineUiPositionOverride) <= 1.25))
            {
                _timelineUiPositionOverride = -1;
                _timelineUiOverrideUntilUtc = DateTime.MinValue;
            }
            UpdateTime(cur);
            if (HasMusicPlayback)
            {
                UpdateMusicTransportProgress();
                void PushLyrics() { FeedLyricsClock(cur); _audioMeters?.UpdateLyricsPosition(LyricsClockNow()); }
                if (InvokeRequired) TryBeginInvokeOnUi(PushLyrics);
                else PushLyrics();
            }
            CaptureWatchHistory(cur);
            UpdateIntroOutroPromptForPlayback();
            UpdateAutomationCredits(cur);
            JellyfinPlaybackProgress(cur);
            UpdateScrobble(cur);
            TryStartCrossfade(cur);
            TryStartGapless(cur);
            TryAutoReturnToLibraryOnEnd(cur);
            LogMadVrPlaybackPerformance(cur);
        }

        private void LogMadVrPlaybackPerformance(double position)
        {
            // Diagnostics only. The frame counters and timing are COM calls into madVR on
            // the UI thread and can wait on its render lock: real logs showed 1-6 s stalls
            // at the exact moment of a sample, and madVR's window lives on this thread.
            if (_madVrPerfDisabled || _activeRendererChoice != VRChoice.MADVR || _engine is not DirectShowUnifiedEngine ds) return;
            long now = Stopwatch.GetTimestamp();
            if (!ReferenceEquals(_madVrPerfEngine, ds))
            {
                _madVrPerfEngine = ds;
                _madVrPerfLastSample = now;
                _madVrPerfPreviousFrames = ds.GetMadVrFrameCounters();
                _madVrPerfPreviousPosition = position;
                return;
            }
            double elapsed = Stopwatch.GetElapsedTime(_madVrPerfLastSample).TotalSeconds;
            if (elapsed < MadVrPerfIntervalSeconds) return;
            long queryStart = Stopwatch.GetTimestamp();
            var frames = ds.GetMadVrFrameCounters();
            double queryMs = Stopwatch.GetElapsedTime(queryStart).TotalMilliseconds;
            if (frames is { } current && _madVrPerfPreviousFrames is { } previous)
                Dbg.Log($"[VIDEO-PERF] madVR interval={elapsed:0.00}s, progress={position - _madVrPerfPreviousPosition:0.00}s, " +
                    $"drawnDelta={current.Drawn - previous.Drawn}, droppedDelta={current.Dropped - previous.Dropped}, droppedTotal={current.Dropped}, " +
                    $"paused={_paused}, hud={_hud?.RequestedVisible}, overlay={_overlayHost?.Visible}, dpi={DeviceDpi}, queryMs={queryMs:0.0}.");
            if (queryMs > MadVrPerfMaxQueryMs)
            {
                _madVrPerfDisabled = true;
                Dbg.Warn($"[VIDEO-PERF] madVR statistics query took {queryMs:0}ms on the UI thread; sampling disabled for this session.");
            }
            _madVrPerfLastSample = now;
            _madVrPerfPreviousPosition = position;
            _madVrPerfPreviousFrames = frames;
        }

        private double GetTimelinePositionForHud()
        {
            if (_timelineUiPositionOverride >= 0 && DateTime.UtcNow < _timelineUiOverrideUntilUtc)
                return _timelineUiPositionOverride;
            if (_engine == null) return 0;
            // Use the progress sample instead of calling DirectShow from every paint.
            // Bound extrapolation so buffering or a stalled graph cannot run the HUD ahead.
            double elapsed = _timelineSampleTimestamp == 0 || _paused ? 0 :
                Math.Min(.5, Stopwatch.GetElapsedTime(_timelineSampleTimestamp).TotalSeconds);
            double position = Math.Max(0, _lastKnownPlaybackPosition + elapsed);
            double duration = GetTimelineDurationSeconds();
            return duration > 0 ? Math.Min(position, duration) : position;
        }

        private void SetTimelinePositionOverride(double seconds)
        {
            _timelineUiPositionOverride = Math.Max(0, seconds);
            _lastKnownPlaybackPosition = _timelineUiPositionOverride;
            _timelineSampleTimestamp = Stopwatch.GetTimestamp();
            _timelineUiOverrideUntilUtc = DateTime.UtcNow.AddSeconds(2.2);
        }

        private void CaptureExplicitSeek(double positionSeconds)
        {
            try
            {
                if (_playingPreRoll || IsPhotoMode || string.IsNullOrWhiteSpace(_currentPath)) return;
                double duration = GetTimelineDurationSeconds();
                if (duration <= 0 || positionSeconds < 0) return;
                string path = _currentPath!;
                string title = BuildBestDisplayTitleForPath(path);
                double position = Math.Clamp(positionSeconds, 0, duration);
                bool trackInDiary = ShouldTrackCurrentPlaybackInDiary();
                _ = Task.Run(() =>
                {
                    PlaybackResumeStore.SaveOrClear(path, position, duration);
                    if (trackInDiary)
                        WatchHistoryStore.RecordProgress(path, title, position, duration, force: true);
                });
            }
            catch { }
        }

        private void CaptureWatchHistory(double positionSeconds)
        {
            try
            {
                if (!ShouldTrackCurrentPlaybackInDiary())
                    return;

                string path = _currentPath!;
                string title = BuildBestDisplayTitleForPath(path);
                DateTime now = DateTime.UtcNow;
                if (!string.Equals(_watchHistorySessionPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _watchHistorySessionPath = path;
                    _watchHistoryNextCaptureUtc = now.AddSeconds(2);
                    _ = Task.Run(() => WatchHistoryStore.RecordStarted(path, title));
                }

                if (positionSeconds >= 1 && now >= _watchHistoryNextCaptureUtc)
                {
                    _watchHistoryNextCaptureUtc = now.AddSeconds(20);
                    double duration = GetTimelineDurationSeconds();
                    _ = Task.Run(() => WatchHistoryStore.RecordProgress(path, title, positionSeconds, duration));
                }
            }
            catch { }
        }

        private bool ShouldTrackCurrentPlaybackInDiary()
        {
            try
            {
                if (_playingPreRoll || IsPhotoMode || string.IsNullOrWhiteSpace(_currentPath))
                    return false;

                // Il Diario e' un registro di film/episodi: una cover incorporata non
                // deve trasformare una traccia musicale in un video, e i contenuti
                // YouTube restano confinati alla pagina web.
                if (!_currentMediaHasVideo || LooksLikePureAudioByExt(_currentPath!))
                    return false;
                if (IsCurrentYouTube())
                    return false;

                return true;
            }
            catch { return false; }
        }

        private void TryAutoReturnToLibraryOnEnd(double cur)
        {
            try
            {
                bool queueDrivenEndHandling = _playbackQueueSessionActive || (_playingPreRoll && !string.IsNullOrWhiteSpace(_pendingMainPathAfterPreRoll));
                if (!_autoReturnToLibraryOnEnd && !queueDrivenEndHandling) return;
                if (_endTriggered) return;
                if (_engine == null) return;
                // Un DVD con i menu decide da se' cosa viene dopo un titolo (un altro avviso, il menu, il film).
                if (DvdEngine != null) return;
                if (_stopping) return;
                if (_paused) { _endCandidateSinceUtc = DateTime.MinValue; return; }
                if (IsPhotoMode) return;

                // Se sto scrubbando, non considerare EOF (evita ritorni mentre trascini la timeline).
                if (Volatile.Read(ref _scrubActive))
                {
                    _endCandidateSinceUtc = DateTime.MinValue;
                    return;
                }

                if (_duration <= 0) { _endCandidateSinceUtc = DateTime.MinValue; return; }

                double remaining = _duration - cur;

                // Considera EOF solo nell'ultimo pezzetto, per un breve tempo continuativo
                // (alcuni decoder “oscillano” attorno alla fine).
                if (remaining <= 0.25 && cur > 0.5)
                {
                    if (_endCandidateSinceUtc == DateTime.MinValue)
                        _endCandidateSinceUtc = DateTime.UtcNow;

                    if ((DateTime.UtcNow - _endCandidateSinceUtc).TotalMilliseconds >= 650)
                    {
                        HandlePlaybackCompleted();
                        return;
                    }
                }
                else
                {
                    _endCandidateSinceUtc = DateTime.MinValue;
                }
            }
            catch { }
        }

        private void UpdateTime(double cur)
        {
            if (_hud?.Visible == true) _hud.InvalidateProgress();
            if (_hud?.Visible == true || _remoteOsd?.Visible == true || _infoOverlay?.Visible == true)
            {
                try { UpdateMpcvrMixerOverlayMirror(); } catch { }
            }
            PublishRemoteState(cur);

            // Alcuni demuxer pubblicano la durata solo dopo i primi callback di
            // avanzamento. Se l'HUD è già aperto, mostra la timeline nello stesso
            // frame in cui la durata diventa valida invece di aspettare un nuovo
            // movimento del mouse (succede soprattutto dopo apertura e ritorno dal PIP).
            try
            {
                if (_hud?.Visible == true && !_hud.TimelineVisible && GetTimelineDurationSeconds() > 0)
                    _hud.TimelineVisible = true;
            }
            catch { }

            // These COM stream/LAV probes and file queries serve the info panel.
            // Do not contend with the renderer four times per second when it is hidden.
            if (_currentMediaHasVideo && !IsInfoPanelShown())
            {
                _avgLastTs = DateTime.MinValue;
                return;
            }

            try
            {
                // --- SE PAUSA: non campionare nulla, congela i "now" e non aggiornare le medie ---
                if (_paused)
                {
                    // ferma il sampler FFmpeg
                    _lastPktSample = DateTime.MinValue;
                    Volatile.Write(ref _packetRateSampleActive, 0);

                    // decadi dolcemente i valori correnti per non avere salti brutti
                    _audioBitrateNowKbps = (int)(_audioBitrateNowKbps * 0.85);
                    _videoBitrateNowKbps = (int)(_videoBitrateNowKbps * 0.85);

                    // non accumulare nelle medie finché sei fermo
                    _avgLastTs = DateTime.MinValue;

                    // aggiorna solo l’overlay con i valori "freezati"
                    if (IsInfoPanelShown())
                        RefreshInfoOverlayNow();
                    return;
                }

                if (_engine != null)
                {
                    var now = DateTime.UtcNow;

                    // Bitrate medio del container (SOLO per file locali, usato solo come fallback)
                    int avgContainerKbpsLocal = 0;
                    try
                    {
                        if (!string.IsNullOrEmpty(_currentPath) &&
                            File.Exists(_currentPath) &&
                            _duration > 1)
                        {
                            var fi = new FileInfo(_currentPath);
                            avgContainerKbpsLocal = (int)Math.Round((fi.Length * 8.0 / 1000.0) / _duration);
                        }
                    }
                    catch { }

                    // Bitrate istantaneo (totale) basato su IO del processo:
                    // utile soprattutto per streaming (YouTube) quando i metadati non danno
                    // un bitrate affidabile.
                    try
                    {
                        if (!_isLocalFile && !IsCurrentYouTube() && !string.IsNullOrEmpty(_currentPath) &&
                            Uri.TryCreate(_currentPath, UriKind.Absolute, out var uio) &&
                            (uio.Scheme == Uri.UriSchemeHttp || uio.Scheme == Uri.UriSchemeHttps))
                        {
                            if (GetProcessIoCounters(System.Diagnostics.Process.GetCurrentProcess().Handle, out var io))
                            {
                                var now2 = DateTime.UtcNow;
                                long curBytes = (long)io.ReadTransferCount;
                                if (_ioPrevWhen != DateTime.MinValue)
                                {
                                    double dt = (now2 - _ioPrevWhen).TotalSeconds;
                                    long dBytes = curBytes - _ioPrevBytes;
                                    if (dt >= 0.25 && dBytes > 0)
                                    {
                                        int kbps = (int)Math.Round((dBytes * 8.0 / 1000.0) / dt);
                                        // smoothing leggero
                                        _containerBitrateNowKbps = (_containerBitrateNowKbps <= 0)
                                            ? kbps
                                            : (int)(_containerBitrateNowKbps * 0.4 + kbps * 0.6);
                                    }
                                }
                                _ioPrevBytes = curBytes;
                                _ioPrevWhen = now2;
                            }
                        }
                        else
                        {
                            // se non siamo su streaming, non aggiornare/mostrare bitrate container
                            _ioPrevWhen = DateTime.MinValue;
                            _containerBitrateNowKbps = 0;
                        }
                    }
                    catch { }

                    // === Campionamento reale FFmpeg (PacketRateSampler) per TUTTO, anche HTTP/HTTPS ===
                    try
                    {
                        if (IsInfoPanelShown() &&
                            now >= _suppressPacketSamplesUntilUtc &&
                            (now - _lastPktSample).TotalMilliseconds >= PACKET_RATE_SAMPLE_INTERVAL_MS &&
                            !_stopping &&
                            Interlocked.Exchange(ref _packetRateSampleActive, 1) == 0)
                        {
                            _lastPktSample = now;
                            double pos = _engine.PositionSeconds;
                            bool engineHasVideo = _engine.HasDisplayControl();
                            int audioOrdinal = SelectedInternalAudioOrdinal();

                            Task.Run(() =>
                            {
                                try
                                {
                                    // Finestra breve ma non istantanea: mostra le reali
                                    // variazioni del flusso senza trasformare il numero in rumore.
                                    int ak = 0;
                                    int vk = 0;

                                    try
                                    {
                                        if (_pktRateOk)
                                        {
                                            if (audioOrdinal >= 0) _pktRate.SelectAudioOrdinal(audioOrdinal);
                                            var rMain = _pktRate.Sample(pos, 1.0);
                                            ak = rMain.aKbps;
                                            vk = rMain.vKbps;
                                        }
                                    }
                                    catch { }

                                    // YouTube: spesso audio su URL separato
                                    try
                                    {
                                        if (_pktRateAudioOk)
                                        {
                                            var rA = _pktRateAudio.Sample(pos, 1.0);
                                            if (rA.aKbps > 0) ak = rA.aKbps; // override
                                        }
                                    }
                                    catch { }
                                    if (ak > 0 || (engineHasVideo && vk > 0))
                                    {
                                        BeginInvoke(new Action(() =>
                                        {
                                            var nowLocal = DateTime.UtcNow;

                                            if (ak > 0)
                                            {
                                                // Mantieni il dato leggibile, ma lascia emergere le variazioni
                                                // reali del flusso abbastanza rapidamente nell'overlay Info.
                                                _audioBitrateNowKbps = (_audioBitrateNowKbps <= 0)
                                                    ? ak
                                                    : (int)(_audioBitrateNowKbps * 0.25 + ak * 0.75);
                                                _aNowTs = nowLocal;
                                            }

                                            if (engineHasVideo && vk > 0)
                                            {
                                                _videoBitrateNowKbps = (_videoBitrateNowKbps <= 0)
                                                    ? vk
                                                    : (int)(_videoBitrateNowKbps * 0.25 + vk * 0.75);
                                                _vNowTs = nowLocal;
                                            }
                                        }));
                                    }
                                }
                                catch { /* best-effort */ }
                                finally
                                {
                                    Volatile.Write(ref _packetRateSampleActive, 0);
                                }
                            });
                        }
                    }
                    catch { Volatile.Write(ref _packetRateSampleActive, 0); }

                    // 2) Audio IN/OUT + flag bitstream (per overlay, non per i "now")
                    var sel = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
                    var lav = GetLavAudioIODetails(sel?.Name);
                    _bitstreamNow = IsBitstream();   // unica fonte di verità per la modalità di uscita

                    // 3) Video/Audio NOW (dinamico) + gestione solo-audio
                    bool hasVideo = _engine.HasDisplayControl();

                    // Campioni “freschi” dal sampler FFmpeg (<=1.5 s)
                    var sampleNow = DateTime.UtcNow;
                    double audioSampleAge = (sampleNow - _aNowTs).TotalSeconds;
                    double videoSampleAge = (sampleNow - _vNowTs).TotalSeconds;
                    bool recentAudio = audioSampleAge <= 1.5;
                    bool recentVideo = videoSampleAge <= 1.5;

                    // Un campione dinamico non deve restare esposto per sempre se il
                    // demuxer salta una finestra di lettura. Azzerandolo dopo una breve
                    // tolleranza permettiamo al fallback di subentrare e al campione
                    // successivo di ripartire da un valore reale, senza numeri congelati.
                    if (!recentAudio && _aNowTs != DateTime.MinValue && audioSampleAge > 2.5)
                    {
                        _audioBitrateNowKbps = 0;
                        _aNowTs = DateTime.MinValue;
                    }

                    if (!recentVideo && _vNowTs != DateTime.MinValue && videoSampleAge > 2.5)
                    {
                        _videoBitrateNowKbps = 0;
                        _vNowTs = DateTime.MinValue;
                    }

                    if (!hasVideo)
                    {
                        // solo audio → il video è 0 fisso
                        _videoBitrateNowKbps = 0;
                        _videoAvgLiveKbps = 0;
                        _vNowTs = DateTime.MinValue;

                        // fallback audio se il sampler non ha ancora dato nulla
                        if (!recentAudio && _audioBitrateNowKbps <= 0)
                        {
                            int kbps = 0;

                            // 1) LAV: su streaming preferiamo STIMARE dal media type IN (codec),
                            // evitando i valori PCM (enormi) dell'OUT.
                            if (_isLocalFile)
                            {
                                // Solo in bitstream: in PCM quel valore e' la portata dell'audio gia'
                                // decodificato (1.536 kbps per un AAC da 128), non il bitrate della traccia.
                                if (_bitstreamNow && lav.AudioNowKbps > 0) kbps = lav.AudioNowKbps;
                            }
                            else
                            {
                                if (TryGetLavInAvgBytesPerSec(out int inAvgBps))
                                {
                                    int est = (int)Math.Round(inAvgBps * 8.0 / 1000.0);
                                    if (est > 0 && est < 2500) kbps = est;
                                }
                            }

                            // 2) dal nome traccia (es. "640 kb/s")
                            if (kbps <= 0 && sel != null) kbps = ParseKbpsFromName(sel.Name);

                            // 3) fallback: container totale (locale) / throughput stimato (stream)
                            if (kbps <= 0)
                            {
                                if (_isLocalFile && avgContainerKbpsLocal > 0) kbps = avgContainerKbpsLocal;
                                else if (!_isLocalFile && _containerBitrateNowKbps > 0) kbps = (int)(_containerBitrateNowKbps * 0.25);
                            }

                            _audioBitrateNowKbps = kbps;
                        }
                    }
                    else
                    {
                        // AUDIO fallback (se FFmpeg non ha ancora campioni affidabili)
                        if (!recentAudio && _audioBitrateNowKbps <= 0)
                        {
                            if (_isLocalFile)
                            {
                                if (_bitstreamNow && lav.AudioNowKbps > 0)
                                    _audioBitrateNowKbps = lav.AudioNowKbps;
                                else if (sel != null)
                                    _audioBitrateNowKbps = ParseKbpsFromName(sel.Name);
                                else if (avgContainerKbpsLocal > 0)
                                    _audioBitrateNowKbps = (int)(avgContainerKbpsLocal * 0.30);
                            }
                            else
                            {
                                // streaming: prova dal media type IN (codec) o dal nome traccia
                                int kbps = 0;
                                if (TryGetLavInAvgBytesPerSec(out int inAvgBps))
                                {
                                    int est = (int)Math.Round(inAvgBps * 8.0 / 1000.0);
                                    if (est > 0 && est < 2500) kbps = est;
                                }
                                if (kbps <= 0 && sel != null) kbps = ParseKbpsFromName(sel.Name);
                                if (kbps <= 0 && _containerBitrateNowKbps > 0) kbps = (int)(_containerBitrateNowKbps * 0.25);
                                _audioBitrateNowKbps = kbps;
                            }
                        }

                        // VIDEO fallback: se non abbiamo campioni dal sampler, usa container:
                        // - locale: avg del file
                        // - streaming: throughput stimato (IO)
                        if (!recentVideo && _videoBitrateNowKbps <= 0)
                        {
                            int containerKbps = _isLocalFile ? avgContainerKbpsLocal : _containerBitrateNowKbps;
                            if (containerKbps > 0)
                            {
                                int audioGuess = _audioBitrateNowKbps > 0
                                    ? _audioBitrateNowKbps
                                    : (int)(containerKbps * 0.25);
                                _videoBitrateNowKbps = Math.Max(0, containerKbps - audioGuess);
                            }
                        }

                        // Bitstream: se ancora 0 e LAV ci dà il payload, usalo come ultima spiaggia
                        if (!recentAudio &&
                            _audioBitrateNowKbps <= 0 &&
                            _bitstreamNow &&
                            lav.AudioNowKbps > 0)
                        {
                            _audioBitrateNowKbps = lav.AudioNowKbps;
                        }
                    }

                    // piccolo floor assoluto (evita numeri ridicoli ma lascia 0 se sconosciuto)
                    if (_audioBitrateNowKbps > 0 && _audioBitrateNowKbps < 16)
                        _audioBitrateNowKbps = 16;

                    // 4) MEDIE LIVE — integrazione pesata + publish ogni 10s
                    var nowTs = now;

                    if (_avgLastTs != DateTime.MinValue)
                    {
                        double dt = (nowTs - _avgLastTs).TotalSeconds;
                        if (dt > 0 && dt < 5) // ignora outlier/jitter grossi
                        {
                            _avgAudioBitSec += Math.Max(0, _audioBitrateNowKbps) * dt;
                            _avgVideoBitSec += Math.Max(0, _videoBitrateNowKbps) * dt;
                            _avgDurSec += dt;
                        }
                    }
                    _avgLastTs = nowTs;

                    if (_avgLastPublish == DateTime.MinValue ||
                        (nowTs - _avgLastPublish).TotalSeconds >= AVG_PUBLISH_SEC)
                    {
                        if (_avgDurSec > 0)
                        {
                            _audioAvgLiveKbps = _avgAudioBitSec / _avgDurSec;
                            _videoAvgLiveKbps = _avgVideoBitSec / _avgDurSec;
                        }
                        _avgLastPublish = nowTs;
                    }
                }
            }
            catch { }

            if (IsInfoPanelShown())
                RefreshInfoOverlayNow();
        }
    }
}
