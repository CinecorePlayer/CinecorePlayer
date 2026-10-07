#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm : Form
    {
        private int _lyricsStatusRefreshPending;
        private void RefreshLyricsLibraryStatus()
        {
            if (IsDisposed || !IsHandleCreated || _closingForExit) return;
            if (Interlocked.Exchange(ref _lyricsStatusRefreshPending, 1) != 0) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    Interlocked.Exchange(ref _lyricsStatusRefreshPending, 0);
                    if (IsDisposed) return;
                    string status = _lyricsService.GetSynchronizationStatus(_uiLanguage == "en");
                    _cinematicLibraryPage?.SetLyricsLibraryStatus(status);
                    _audioMeters?.SetSynchronizationStatus(status);
                }));
            }
            catch { Interlocked.Exchange(ref _lyricsStatusRefreshPending, 0); }
        }
        private void ResetAudioOverlayState()
        {
            if (_preserveMusicTransportOnOpen) return;
            try { if (_audioMeters != null) _audioMeters.Visible = false; } catch { }
            try { if (_audioMetersHost != null) _audioMetersHost.Visible = false; } catch { }
            try { if (_audioOnlyBanner != null) _audioOnlyBanner.Visible = false; } catch { }
        }

        private void OnEngineBitstreamChanged(bool bitstreamActive, IPlaybackEngine? sourceEngine = null, int openSerial = -1)
        {
            if ((sourceEngine != null && !ReferenceEquals(_engine, sourceEngine)) ||
                (openSerial >= 0 && openSerial != Volatile.Read(ref _openSerial)))
                return;

            if (_audioOutPref == AudioOutPref.ForcePcm)
                bitstreamActive = false;

            _bitstreamNow = bitstreamActive;

            try
            {
                if (!IsHandleCreated || IsDisposed)
                    return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || _stopping ||
                        (sourceEngine != null && !ReferenceEquals(_engine, sourceEngine)) ||
                        (openSerial >= 0 && openSerial != Volatile.Read(ref _openSerial)))
                        return;

                    RefreshInfoOverlayNow();

                    if (_currentMediaHasVideo)
                    {
                        StopAudioMeters();
                        ResetAudioOverlayState();
                    }
                    else
                    {
                        // Anche in bitstream vogliamo mostrare i grafici, non il PNG audio-only.
                        // StartAudioMetersIfPossible avvia l'analisi PCM in modo ritardato, così
                        // non disturba madVR/DirectShow durante il caricamento.
                        StartAudioMetersIfPossible();
                    }

                    _hud?.Invalidate();
                    if (bitstreamActive)
                    {
                        try { _engine?.SetVolume(1f); } catch { }
                    }
                }));
            }
            catch { }
        }

        // ---- PCM dal Cinecore Audio Engine verso l'analisi ----
        // Il callback audio non deve mai aspettare l'analisi (FFT, loudness): copia in una coda
        // e un thread dedicato la consuma. Se l'analisi resta indietro i blocchi vecchi si scartano.
        private System.Collections.Concurrent.BlockingCollection<(float[] Data, int Samples, int Rate, int Channels)>? _enginePcmQueue;
        private Action<float[], int, int, int>? _enginePcmHandler;

        private void StartEnginePcmForwarding(LoopbackSampler sampler)
        {
            StopEnginePcmForwarding();
            var queue = new System.Collections.Concurrent.BlockingCollection<(float[] Data, int Samples, int Rate, int Channels)>(64);
            _enginePcmQueue = queue;
            _enginePcmHandler = (data, frames, channels, rate) =>
            {
                int samples = frames * channels;
                var copy = System.Buffers.ArrayPool<float>.Shared.Rent(samples);
                Array.Copy(data, copy, samples);
                if (!queue.TryAdd((copy, samples, rate, channels))) System.Buffers.ArrayPool<float>.Shared.Return(copy);
            };
            Audio.CinecoreAudioEngine.PcmTap += _enginePcmHandler;
            var worker = new Thread(() =>
            {
                try
                {
                    foreach (var block in queue.GetConsumingEnumerable())
                    {
                        try { sampler.PushPcm(block.Data, block.Samples, block.Rate, block.Channels); } catch { }
                        System.Buffers.ArrayPool<float>.Shared.Return(block.Data);
                    }
                }
                catch { }
            }) { IsBackground = true, Name = "Cinecore Audio analysis" };
            worker.Start();
        }

        private void StopEnginePcmForwarding()
        {
            if (_enginePcmHandler != null) Audio.CinecoreAudioEngine.PcmTap -= _enginePcmHandler;
            _enginePcmHandler = null;
            try { _enginePcmQueue?.CompleteAdding(); } catch { }
            _enginePcmQueue = null;
        }

        private void StartAudioMetersIfPossible()
        {
            try
            {
                if (_currentMediaHasVideo)
                {
                    StopAudioMeters();
                    ResetAudioOverlayState();
                    return;
                }

                bool bit = IsBitstream();
                if (_audioOutPref == AudioOutPref.ForcePcm)
                    bit = false;

                Dbg.Log($"[Meters] StartAudioMetersIfPossible: bitstream={bit}, forcePcm={_audioOutPref}", Dbg.LogLevel.Info);

                EnsureAudioMetricsTimer();
                _audioSampler ??= new LoopbackSampler();
                _audioSampler.OnMetrics -= OnSamplerMetrics;
                _audioSampler.OnMetrics += OnSamplerMetrics;
                _audioSampler.OnLevels -= OnSamplerLevels;

                if (bit)
                {
                    // Il percorso Bitstream alimenta direttamente PushPcm tramite ffmpeg:
                    // non deve restare in parallelo una vecchia cattura loopback.
                    try { _audioSampler.Stop(); } catch { }
                    bool analysisOk = StartBitstreamPcmAnalysis();
                    if (analysisOk)
                    {
                        _audioMeters?.SetInfoMessage(null);
                        if (_audioMeters != null) _audioMeters.Visible = true;
                        _audioOnlyBanner.Visible = false;
                        if (_audioMetersHost != null)
                        {
                            _audioMetersHost.Visible = true;
                            _audioMetersHost.BringToFront();
                        }
                        BringOverlaysToFront();
                    }
                    else
                    {
                        _audioMeters?.SetInfoMessage(Tx("Bitstream attivo: analisi PCM non disponibile", "Bitstream active: PCM analysis unavailable"));
                        if (_audioMeters != null) _audioMeters.Visible = false;
                        if (_audioMetersHost != null) _audioMetersHost.Visible = false;
                        _audioOnlyBanner.Visible = true;
                        _audioOnlyBanner.BringToFront();
                        BringOverlaysToFront();
                    }
                    return;
                }

                StopBitstreamPcmAnalysis();
                _audioMeters?.SetInfoMessage(null);

                // Cinecore Audio Engine: i grafici ricevono il PCM esatto inviato al DAC (dopo EQ e
                // protezione), non la cattura di sistema che mescola anche gli altri programmi.
                bool ok;
                if (_engine is Audio.CinecoreAudioEngine)
                {
                    try { _audioSampler.Stop(); } catch { }
                    StartEnginePcmForwarding(_audioSampler);
                    ok = true;
                }
                else
                {
                    StopEnginePcmForwarding();
                    ok = _audioSampler.Start();
                }
                Dbg.Log($"[Meters] LoopbackSampler.Start() → {ok}", Dbg.LogLevel.Info);

                if (ok)
                {
                    _audioSampler.OnMetrics -= OnSamplerMetrics;
                    _audioSampler.OnMetrics += OnSamplerMetrics;

                    // Non collegare anche OnLevels: e' il vecchio percorso ridotto e
                    // sovrascriverebbe subito metriche complete come scope, loudness e correlazione.
                    _audioSampler.OnLevels -= OnSamplerLevels;

                    if (_audioMeters != null) _audioMeters.Visible = true;
                    _audioOnlyBanner.Visible = false;
                    if (_audioMetersHost != null)
                    {
                        _audioMetersHost.Visible = true;
                        _audioMetersHost.BringToFront();
                    }
                    BringOverlaysToFront();
                }
                else
                {
                    if (_audioMeters != null) _audioMeters.Visible = false;
                    if (_audioMetersHost != null) _audioMetersHost.Visible = false;
                    _audioOnlyBanner.Visible = true;
                    _audioOnlyBanner.BringToFront();
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("[Meters] StartAudioMetersIfPossible EX: " + ex.Message);
                if (_audioMeters != null) _audioMeters.Visible = false;
                if (_audioMetersHost != null) _audioMetersHost.Visible = false;
                _audioOnlyBanner.Visible = true;
                _audioOnlyBanner.BringToFront();
            }
            finally
            {
                if (!_currentMediaHasVideo && !_stopping && (_openingMusic || _musicWorkspaceActive))
                {
                    _audioOnlyBanner.Visible = false;
                    if (_audioMetersHost != null) _audioMetersHost.Visible = _musicWorkspaceView != 0;
                    if (_musicWorkspaceView != 0) _audioMeters?.ShowWorkspace(_musicWorkspaceView == 2);
                }
            }
        }

        private bool StartBitstreamPcmAnalysis()
        {
            try
            {
                if (_engine == null || string.IsNullOrWhiteSpace(_currentPath))
                    return false;

                // Se questo media ha video, non avviare MAI il decoder parallelo:
                // i meter servono solo nella schermata audio-only, e così madVR resta intoccato.
                if (_currentMediaHasVideo)
                    return false;

                StopBitstreamPcmAnalysis();

                _bitstreamAnalysisSampler ??= new BitstreamPcmAnalysisSampler();
                string path = _currentPath!;
                int serial = unchecked(++_bitstreamAnalysisStartSerial);
                DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(8);

                _audioMeters?.SetInfoMessage(Tx("Bitstream attivo: avvio analisi PCM…", "Bitstream active: starting PCM analysis…"));
                if (_audioMeters != null) _audioMeters.Visible = true;
                if (_audioMetersHost != null) _audioMetersHost.Visible = true;
                _audioOnlyBanner.Visible = false;
                _audioMetersHost?.BringToFront();
                BringOverlaysToFront();

                var timer = new System.Windows.Forms.Timer { Interval = 650 };
                _bitstreamAnalysisStartTimer = timer;

                timer.Tick += (_, __) =>
                {
                    try
                    {
                        if (serial != _bitstreamAnalysisStartSerial)
                        {
                            timer.Stop();
                            timer.Dispose();
                            return;
                        }

                        if (IsDisposed || _stopping || _engine == null || _currentMediaHasVideo || !IsBitstream())
                        {
                            timer.Stop();
                            timer.Dispose();
                            if (ReferenceEquals(_bitstreamAnalysisStartTimer, timer))
                                _bitstreamAnalysisStartTimer = null;
                            return;
                        }

                        // Aspetta il renderer, ma al timeout avvia comunque: l'analizzatore
                        // usa un processo ffmpeg indipendente e non deve restare bloccato per sempre.
                        bool loadingVisible = false;
                        try { loadingVisible = (_videoLoading?.Visible == true); } catch { }
                        if (loadingVisible && DateTime.UtcNow < deadlineUtc)
                            return;

                        timer.Stop();
                        timer.Dispose();
                        if (ReferenceEquals(_bitstreamAnalysisStartTimer, timer))
                            _bitstreamAnalysisStartTimer = null;

                        bool ok = _bitstreamAnalysisSampler.Start(
                            path,
                            getPositionSeconds: () =>
                            {
                                try { return _engine?.PositionSeconds ?? 0; }
                                catch { return 0; }
                            },
                            isPaused: () =>
                            {
                                try { return _paused || _engine == null || _stopping || _currentMediaHasVideo; }
                                catch { return true; }
                            },
                            pushPcm: PushBitstreamAnalysisPcm);

                        Dbg.Log($"[Meters] BitstreamPcmAnalysisSampler delayed Start() → {ok}", Dbg.LogLevel.Info);

                        if (ok)
                        {
                            _audioMeters?.SetInfoMessage(null);
                            AudioMetersLiveCharts.AnalysisSource = Tx("Bitstream|analisi PCM parallela|downmix stereo", "Bitstream|parallel PCM analysis|stereo downmix");
                        }
                        else
                            _audioMeters?.SetInfoMessage(Tx("Bitstream attivo: ffmpeg non disponibile per l'analisi PCM", "Bitstream active: ffmpeg is unavailable for PCM analysis"));
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            timer.Stop();
                            timer.Dispose();
                            if (ReferenceEquals(_bitstreamAnalysisStartTimer, timer))
                                _bitstreamAnalysisStartTimer = null;
                        }
                        catch { }

                        Dbg.Warn("[Meters] delayed StartBitstreamPcmAnalysis EX: " + ex.Message);
                        _audioMeters?.SetInfoMessage(Tx("Bitstream attivo: analisi PCM non disponibile", "Bitstream active: PCM analysis unavailable"));
                    }
                };

                timer.Start();
                Dbg.Log("[Meters] BitstreamPcmAnalysisSampler scheduled after renderer startup.", Dbg.LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Dbg.Warn("[Meters] StartBitstreamPcmAnalysis EX: " + ex.Message);
                StopBitstreamPcmAnalysis();
                return false;
            }
        }

        private void StopBitstreamPcmAnalysis(bool asyncStop = false)
        {
            AudioMetersLiveCharts.AnalysisSource = null;
            unchecked { _bitstreamAnalysisStartSerial++; }

            try
            {
                if (_bitstreamAnalysisStartTimer != null)
                {
                    _bitstreamAnalysisStartTimer.Stop();
                    _bitstreamAnalysisStartTimer.Dispose();
                    _bitstreamAnalysisStartTimer = null;
                }
            }
            catch { }

            var sampler = _bitstreamAnalysisSampler;
            if (asyncStop)
                _bitstreamAnalysisSampler = null;

            void StopSampler()
            {
                try { sampler?.Stop(); }
                catch { }
            }

            if (asyncStop)
            {
                try { Task.Run(StopSampler); } catch { StopSampler(); }
            }
            else
            {
                StopSampler();
            }
        }

        private void PushBitstreamAnalysisPcm(float[] samples, int sampleCount, int sampleRate, int channels)
        {
            if (samples == null || sampleCount <= 0 || channels <= 0 || _audioMeters == null || _currentMediaHasVideo || _stopping || IsDisposed)
                return;

            try
            {
                _audioSampler?.PushPcm(samples, sampleCount, sampleRate, channels);
            }
            catch { }
        }

        private LoopbackSampler.AudioMetrics? _latestAudioMetrics;
        private System.Windows.Forms.Timer? _audioMetricsTimer;

        private void EnsureAudioMetricsTimer()
        {
            if (_audioMetricsTimer == null)
            {
                _audioMetricsTimer = new System.Windows.Forms.Timer { Interval = 33 };
                _audioMetricsTimer.Tick += (_, _) => DrainAudioMetrics();
                Disposed += (_, _) => { _audioMetricsTimer?.Dispose(); _audioMetricsTimer = null; };
            }
            _audioMetricsTimer.Start();
        }

        private void OnSamplerMetrics(LoopbackSampler.AudioMetrics m)
        {
            // Capture threads publish only data. They never inspect inherited
            // WinForms visibility or depend on a queued window-handle callback.
            Interlocked.Exchange(ref _latestAudioMetrics, m);
        }

        private void DrainAudioMetrics()
        {
            if (_currentMediaHasVideo || _stopping || IsDisposed || _audioMeters?.Visible != true) return;
            var latest = Interlocked.Exchange(ref _latestAudioMetrics, null);
            if (latest != null) _audioMeters.Update(latest);
        }

        private void StopAudioMeters(bool asyncStop = false, bool resetPresentation = true)
        {
            _audioMetricsTimer?.Stop();
            Interlocked.Exchange(ref _latestAudioMetrics, null);
            if (resetPresentation && !_preserveMusicTransportOnOpen)
            {
                try { _audioMeters?.PrepareForPlaybackExit(); } catch { }
            }
            _audioMeters?.SetInfoMessage(null);
            if (!_preserveMusicTransportOnOpen && _audioMeters != null) _audioMeters.Visible = false;
            if (!_preserveMusicTransportOnOpen && _audioMetersHost != null) _audioMetersHost.Visible = false;

            try { StopBitstreamPcmAnalysis(asyncStop); } catch { }
            StopEnginePcmForwarding();

            LoopbackSampler? sampler = null;
            try
            {
                sampler = _audioSampler;
                try { if (sampler != null) sampler.OnMetrics -= OnSamplerMetrics; } catch { }
                try { if (sampler != null) sampler.OnLevels -= OnSamplerLevels; } catch { }
                if (asyncStop)
                    _audioSampler = null;
            }
            catch { }

            void StopSampler()
            {
                try { sampler?.Stop(); } catch { }
            }

            if (asyncStop)
            {
                try { Task.Run(StopSampler); } catch { StopSampler(); }
            }
            else
            {
                StopSampler();
            }
        }

        private void RecreateAudioMetersSurfaceAfterPip()
        {
            if (_audioMetersHost == null || _audioMetersHost.IsDisposed)
                return;

            AudioMetersLiveCharts? previous = _audioMeters;
            var replacement = new AudioMetersLiveCharts
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            replacement.SetLanguage(_uiLanguage);
            replacement.BackRequested += () => SetMusicWorkspaceView(0);

            try
            {
                _audioMetersHost.SuspendLayout();
                if (previous != null)
                    _audioMetersHost.Controls.Remove(previous);

                _audioMeters = replacement;
                _audioMetersHost.Controls.Add(replacement);
                replacement.BringToFront();
                _audioMetersHost.ResumeLayout(true);
                LayoutAudioMetersHost();
            }
            catch
            {
                try { replacement.Dispose(); } catch { }
                _audioMeters = previous;
                try { _audioMetersHost.ResumeLayout(true); } catch { }
                return;
            }

            // Il vecchio canvas Skia puo' rimanere sospeso dopo una finestra
            // minimizzata. Smaltirlo qui evita di riutilizzare quel device state.
            try { previous?.Dispose(); } catch { }
            _musicPresentationPath = null;
        }

        private void RefreshMusicPresentationForCurrentMedia()
        {
            try
            {
                string? path = _currentPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !LooksLikePureAudioByExt(path))
                {
                    ResetMusicPresentation();
                    return;
                }

                string normalized = path.Trim();
                if (string.Equals(_musicPresentationPath, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateAudioOnlyTrackPresentation(normalized, BuildBestDisplayTitleForPath(normalized));
                    SetAudioOnlyArtwork(ResolvePipArtworkPath(normalized));
                    return;
                }

                _musicPresentationPath = normalized;
                var previous = Interlocked.Exchange(ref _lyricsCts, new CancellationTokenSource());
                try { previous?.Cancel(); previous?.Dispose(); } catch { }
                var cts = _lyricsCts;
                var token = cts?.Token ?? CancellationToken.None;

                string title = BuildBestDisplayTitleForPath(normalized);
                UpdateAudioOnlyTrackPresentation(normalized, title);
                SetAudioOnlyArtwork(ResolvePipArtworkPath(normalized));
                try { _audioMeters?.SetLyricsLoading(title); } catch { }

                _ = Task.Run(async () =>
                {
                    string? artworkPath = null;
                    MusicLyricsService.LyricsResult? lyrics = null;
                    bool providerResultPublished = false;
                    Dbg.Log($"[LYRICS-UI] lookup task started for '{normalized}'", Dbg.LogLevel.Info);
                    try
                    {
                        // Artwork downloads do not gate lyrics or recognition.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                string? art = await MusicArtworkService.ResolveArtworkAsync(normalized, token).ConfigureAwait(false);
                                if (art != null && !token.IsCancellationRequested && !IsDisposed && IsHandleCreated)
                                    BeginInvoke(new Action(() => { if (!token.IsCancellationRequested && !IsDisposed && _currentPath == normalized) SetAudioOnlyArtwork(art); }));
                            }
                            catch (Exception ex) { Dbg.Warn("[LYRICS-UI] artwork lookup failed: " + ex.Message); }
                        }, token);
                        try
                        {
                            lyrics = await _lyricsService.FindForMediaAsync(
                                normalized,
                                token,
                                providerResult =>
                                {
                                    providerResultPublished = true;
                                    PublishLyricsResultToUi(
                                        normalized,
                                        title,
                                        artworkPath,
                                        providerResult,
                                        token,
                                        interim: !providerResult.IsSynced);
                                },
                                synchronizedResult => PublishLyricsResultToUi(
                                    normalized,
                                    title,
                                    artworkPath,
                                    synchronizedResult,
                                    token,
                                    interim: false)).ConfigureAwait(false);
                        }
                        catch (Exception ex) { Dbg.Error($"[LYRICS-UI] lyrics service failed: {ex.GetType().Name}: {ex.Message}"); }
                    }
                    catch (OperationCanceledException)
                    {
                        Dbg.Log($"[LYRICS-UI] lookup canceled for '{normalized}'", Dbg.LogLevel.Info);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Dbg.Error($"[LYRICS-UI] lookup task failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    if (token.IsCancellationRequested || IsDisposed || !IsHandleCreated)
                        return;

                    Dbg.Log(lyrics == null
                        ? $"[LYRICS-UI] service returned no lyrics for '{normalized}'"
                        : $"[LYRICS-UI] service result: provider='{lyrics.Provider}', synced={lyrics.IsSynced}, lines={lyrics.Lines.Count}, timedWords={lyrics.Lines.Sum(line => line.Words?.Count ?? 0)}, note='{lyrics.Note}'", Dbg.LogLevel.Info);
                    // For plain lyrics the provider callback already published
                    // an immediate fallback. The detached synchronization job
                    // will publish the final accepted/rejected result, so do
                    // not overwrite the useful progress state here.
                    if (!providerResultPublished || lyrics?.IsSynced == true)
                        PublishLyricsResultToUi(normalized, title, artworkPath, lyrics, token, interim: false);
                }, token);
            }
            catch (Exception ex)
            {
                Dbg.Error($"[LYRICS-UI] RefreshMusicPresentation failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private string? _lastSyncedLyricsPath;
        private void PublishLyricsResultToUi(
            string normalized,
            string title,
            string? artworkPath,
            MusicLyricsService.LyricsResult? lyrics,
            CancellationToken token,
            bool interim)
        {
            if (token.IsCancellationRequested || IsDisposed || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || token.IsCancellationRequested)
                        return;
                    if (!string.Equals(_currentPath, normalized, StringComparison.OrdinalIgnoreCase))
                        return;
                    if (interim && lyrics?.IsSynced != true && string.Equals(_lastSyncedLyricsPath, normalized, StringComparison.OrdinalIgnoreCase))
                        return;

                    if (!string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath))
                        SetAudioOnlyArtwork(artworkPath);

                    if (lyrics != null)
                    {
                        string state = lyrics.IsSynced
                            ? "Synced LRC"
                            : interim
                                ? Tx("Testo | sincronizzazione automatica in corso...", "Lyrics | automatic synchronization in progress...")
                                : Tx("Testo", "Lyrics");
                        string status = lyrics.IsSynced
                            ? Tx("Testi sincronizzati", "Synchronized lyrics") + " · " + lyrics.Provider
                            : interim ? Tx("Testi disponibili · sincronizzazione in background", "Lyrics available · syncing in background")
                            : Tx("Testi disponibili · sincronizzazione da riprovare", "Lyrics available · synchronization needs retry");
                        Dbg.Log($"[LYRICS-UI] passing {(interim ? "interim provider" : "final")} result to AudioMetersLiveCharts: synced={lyrics.IsSynced}, lines={lyrics.Lines.Count}, timedWords={lyrics.Lines.Sum(line => line.Words?.Count ?? 0)}", Dbg.LogLevel.Info);
                        _audioMeters?.SetLyricsResult(title, status, lyrics.Text, lyrics.Lines);
                        if (lyrics.IsSynced) _lastSyncedLyricsPath = normalized;
                        _audioMeters?.UpdateLyricsPosition(LyricsClockNow());
                        EnsureLyricsFrameTimer();
                    }
                    else
                    {
                        Dbg.Warn($"[LYRICS-UI] displaying unavailable-lyrics state for '{normalized}'");
                        _audioMeters?.SetLyricsResult(title, Tx("Testi non disponibili.", "Lyrics unavailable."), null);
                    }
                }));
            }
            catch (Exception ex)
            {
                Dbg.Error($"[LYRICS-UI] failed handing lyrics result to UI: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SetAudioOnlyArtwork(string? artworkPath)
        {
            _musicTransport?.SetArtwork(artworkPath);
            try { _audioOnlyBanner?.SetArtworkPath(artworkPath); } catch { }
            try { _audioMeters?.SetPlaceholderArtwork(artworkPath); } catch { }
        }

        private void UpdateAudioOnlyTrackPresentation(string path, string title)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), "Audio", StringComparison.OrdinalIgnoreCase))
                    title = Path.GetFileNameWithoutExtension(path) ?? Tx("Traccia audio", "Audio track");

                string subtitle = string.Empty;
                int trackNumber = 0, trackCount = 0;
                bool taggedTitle = false;
                try
                {
                    var tags = MediaProbe.ReadAudioTags(path);
                    trackNumber = tags.TrackNumber ?? 0; trackCount = tags.TrackCount ?? 0;
                    if (!string.IsNullOrWhiteSpace(tags.Title)) { title = RepairAudioDisplayText(tags.Title); taggedTitle = true; }
                    string artist = !string.IsNullOrWhiteSpace(tags.Artist) ? tags.Artist : tags.AlbumArtist;
                    subtitle = string.Join(" · ", new[] { artist, tags.Album }
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => RepairAudioDisplayText(Regex.Replace(v.Trim(), @"\s+", " ")))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                }
                catch { }

                // File senza tag nominati "Artista - Titolo" (o "Artista_ titolo"): il titolo non deve
                // ripetere l'artista e l'artista non deve diventare il nome della cartella.
                string? fileArtist = null;
                if (!taggedTitle)
                {
                    var split = Regex.Match(title, @"^\s*(?<a>[^-–_]{1,40}?)\s*(?:[-–]|_)\s+(?<t>.+)$");
                    if (split.Success && !Regex.IsMatch(split.Groups["a"].Value, @"^\d+$"))
                    {
                        fileArtist = split.Groups["a"].Value.Trim();
                        title = split.Groups["t"].Value.Trim();
                    }
                }
                if (string.IsNullOrWhiteSpace(subtitle))
                {
                    string inferredTitle = title;
                    subtitle = fileArtist ?? MusicLyricsService.InferArtistFromPath(path, ref inferredTitle) ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(subtitle))
                {
                    try
                    {
                        string? folder = Path.GetDirectoryName(path);
                        string folderName = string.IsNullOrWhiteSpace(folder) ? string.Empty : new DirectoryInfo(folder).Name;
                        if (!string.IsNullOrWhiteSpace(folderName))
                            subtitle = folderName;
                    }
                    catch { }
                }

                string quality = PrettyAudioInFromProbe(_info);
                if (_info != null)
                {
                    var bits = _info.AudioBits > 0 ? $"{_info.AudioBits}-bit" : string.Empty;
                    var bitrate = _info.AudioBitrateKbps > 0 ? FmtKbps(_info.AudioBitrateKbps) : string.Empty;
                    quality = string.Join(" - ", new[] { quality, bits, bitrate }
                        .Where(v => !string.IsNullOrWhiteSpace(v) && !string.Equals(v, "n/d", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                }

                if (string.IsNullOrWhiteSpace(quality) || string.Equals(quality, "n/d", StringComparison.OrdinalIgnoreCase))
                {
                    string ext = (Path.GetExtension(path) ?? string.Empty).TrimStart('.').ToUpperInvariant();
                    quality = string.IsNullOrWhiteSpace(ext)
                        ? Tx("Audio - analisi in tempo reale", "Audio - live analysis")
                        : ext + " - " + Tx("analisi in tempo reale", "live analysis");
                }

                _audioMeters?.SetTrackPresentation(title, subtitle, quality);
                if (trackNumber <= 0 || trackCount < trackNumber)
                {
                    var albumPosition = _cinematicLibraryPage?.GetMusicTrackPosition(path) ?? (0, 0);
                    trackNumber = albumPosition.Item1; trackCount = albumPosition.Item2;
                }
                if (trackCount == 0)
                {
                    int index = _playbackQueue.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) { trackNumber = index+1; trackCount = _playbackQueue.Count; }
                }
                _audioMeters?.SetTrackPosition(trackNumber, trackCount);
                _musicTransport?.SetTrack(title, subtitle);
            }
            catch { }
        }

        private static string RepairAudioDisplayText(string text)
        {
            if (!text.Contains('Ã') && !text.Contains('Â') && !text.Contains("â€")) return text;
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                var bytes = System.Text.Encoding.GetEncoding(1252, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback).GetBytes(text);
                string repaired = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                return repaired;
            }
            catch { return text; }
        }

        private void ResetMusicPresentation()
        {
            _musicPresentationPath = null;
            try
            {
                var old = Interlocked.Exchange(ref _lyricsCts, null);
                old?.Cancel();
                old?.Dispose();
            }
            catch { }
            if (_preserveMusicTransportOnOpen) return;
            SetAudioOnlyArtwork(null);
            try { _audioMeters?.SetTrackPresentation(Tx("Statistiche audio", "Audio statistics"), string.Empty, Tx("Analisi in tempo reale", "Live analysis")); } catch { }
            try { _audioMeters?.SetLyricsResult(Tx("Testi", "Lyrics"), Tx("In attesa del brano.", "Waiting for a track."), Tx("I testi compariranno qui quando disponibili.", "Lyrics will appear here when available.")); } catch { }
        }

        private void OnSamplerLevels(float rmsL, float rmsR, float peakHoldL, float peakHoldR, double[] spectrumDb)
        {
            if (_audioMeters == null) return;

            try
            {
                // Esegui sul thread UI
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!_audioMeters.Visible) return;
                        _audioMeters.UpdateLevels(rmsL, rmsR, peakHoldL, peakHoldR,
                            (spectrumDb != null && spectrumDb.Length > 0) ? spectrumDb : null);
                    }));
                }
            }
            catch { /* best-effort */ }
        }
        // Amplificazione oltre il 100% (dB), chiesta continuando ad alzare il volume a fondo scala.
        private int _volumeBoostDb;
        private string? _volumeBoostPath;

        private bool ApplyVolumeBoost(int db)
        {
            db = Math.Clamp(db, 0, 12);
            bool ok;
            try
            {
                ok = _engine switch
                {
                    LibMpvPlaybackEngine mpv => mpv.SetVolumeBoostDb(db),
                    Audio.CinecoreAudioEngine music => music.SetVolumeBoostDb(db),
                    _ => db <= 0
                };
            }
            catch { ok = db <= 0; }
            if (ok) { _volumeBoostDb = db; _volumeBoostPath = _currentPath; }
            return ok;
        }

        /// <summary>
        /// Volume su a fondo scala: ogni pressione aggiunge 1 dB fino a +12 (volume giu' li toglie
        /// prima di scendere sotto il 100%). Vero se il tasto e' stato usato per l'amplificazione.
        /// </summary>
        private bool TryStepVolumeBoost(int direction)
        {
            if (_engine == null || _hud == null || IsPhotoMode || IsNetworkVolumeActive) return false;
            bool bitstream = IsBitstream() && _audioOutPref != AudioOutPref.ForcePcm;
            if (direction > 0)
            {
                if (_hud.IsMuted || _hud.GetVolume() < 0.999f) return false;
                if (bitstream)
                {
                    ShowRemoteOsd(null, null, 2400, Tx("Audio in bitstream: il volume lo decide l’amplificatore", "Bitstream audio: the receiver sets the volume"));
                    return true;
                }
                if (_volumeBoostDb < 12 && !ApplyVolumeBoost(_volumeBoostDb + 1))
                {
                    ShowRemoteOsd(null, null, 2800, Tx("Oltre il 100% serve il motore MPV (film) o il motore Cinecore non in uscita diretta (musica)", "Above 100% needs the MPV engine (films) or the Cinecore engine not in direct output (music)"));
                    return true;
                }
            }
            else
            {
                if (_volumeBoostDb <= 0) return false;
                ApplyVolumeBoost(_volumeBoostDb - 1);
            }
            ShowRemoteOsd(GetVolumeOsdSvg(1f, false), 1f, 1300, _volumeBoostDb > 0 ? $"100% +{_volumeBoostDb} dB" : "100%");
            return true;
        }

        private void ApplyVolume(float v)
        {
            // L'amplificazione vale per il titolo in cui e' stata chiesta e solo a fondo scala:
            // un altro film riparte senza, il cursore abbassato la toglie.
            if (_volumeBoostDb > 0)
            {
                if (v < 0.999f || !string.Equals(_volumeBoostPath, _currentPath, StringComparison.OrdinalIgnoreCase)) ApplyVolumeBoost(0);
                else ApplyVolumeBoost(_volumeBoostDb);
            }
            // The receiver owns volume on the linked output, including PCM.
            // Reopening a graph must not apply the receiver level to the software mixer.
            if (IsNetworkVolumeActive) v = 1f;
            bool isBt = IsBitstream();

            // ⬇️ anche qui: se l’utente ha forzato PCM, ignoriamo il bitstream
            if (_audioOutPref == AudioOutPref.ForcePcm)
                isBt = false;

            if (isBt)
            {
                try { _engine?.SetVolume(1f); } catch { }
                try { CoreAudioSessionVolume.Set(1f); } catch { }
                return;
            }

            try { _engine?.SetVolume(v); } catch { }
            try { CoreAudioSessionVolume.Set(v); } catch { }
        }

        private void ReapplyAudioVolumeAfterGraphStart(float volume)
        {
            // Alcuni renderer audio ricreano la sessione CoreAudio qualche frame dopo Run().
            // Riapplicare il volume a piccoli ritardi evita che il secondo elemento della
            // coda erediti una sessione muta dal grafo appena smontato.
            int[] delays = { 150, 450, 900 };
            foreach (int delay in delays)
            {
                try
                {
                    var timer = new System.Windows.Forms.Timer { Interval = delay };
                    timer.Tick += (_, __) =>
                    {
                        try
                        {
                            timer.Stop();
                            if (IsDisposed || !IsHandleCreated)
                                return;
                            ApplyVolume(volume);
                        }
                        catch { }
                        finally
                        {
                            try { timer.Dispose(); } catch { }
                        }
                    };
                    timer.Start();
                }
                catch { }
            }
        }
    }
}
