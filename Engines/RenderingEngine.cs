#nullable enable
using DirectShowLib;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using CinecorePlayer2025.Utilities;
using CinecorePlayer2025.HUD;

namespace CinecorePlayer2025.Engines
{
    /// <summary>
    /// Dimensionamento dell'immagine (altezza/area costante): il motore riduce o ingrandisce il video
    /// attorno al centro rispetto a "riempi lo schermo". 1 = comportamento normale.
    /// </summary>
    public interface IVideoScaleEngine
    {
        /// <summary>Scala massima che il motore sa presentare (oltre 1 l'immagine esce dalla finestra).</summary>
        double MaxVideoScale { get; }
        void SetVideoScale(double scale);
        /// <summary>Formato della parte attiva secondo il renderer (bande nere rilevate da madVR), se lo sa.</summary>
        double? RendererActiveAspect();
    }

    // ======= DirectShow unified engine – API =======
    public interface IPlaybackEngine : IDisposable
    {
        void Open(string mediaPath, bool hasVideo);
        void Play(); void Pause(); void Stop();
        double DurationSeconds { get; }
        double PositionSeconds { get; set; }
        void SetVolume(float volume);
        void UpdateVideoWindow(nint ownerHwnd, Rectangle ownerClient);
        Rectangle GetLastDestRectAsClient(Rectangle ownerClient);
        void SetStereo3D(Stereo3DMode mode);
        void SetUpscaling(bool enable);
        void BindUpdateCallback(Action? cb);
        bool IsBitstreamActive();
        bool HasDisplayControl();

        (string text, DateTime when) GetLastVideoMTDump();
        (int width, int height, string subtype) GetNegotiatedVideoFormat();
        (int bytes, DateTime when) GetLastSnapshotInfo();

        event Action<double>? OnProgressSeconds;
        event Action<string>? OnStatus;
        event Action<bool>? OnBitstreamChanged;
        List<DsStreamItem> EnumerateStreams();

        bool EnableByGlobalIndex(int globalIndex);
        bool DisableSubtitlesIfPossible();
        bool TrySnapshot(out int byteCount);

        // ===== anteprima overlay (thumbnail FFmpeg) =====
        Bitmap? GetPreviewFrame(double seconds, int maxW = 360);

        // ===== madVR hotkey bridge =====
        void SetMadVrChroma(MadVrCategoryPreset preset);
        void SetMadVrImageUpscale(MadVrCategoryPreset preset);
        void SetMadVrImageDownscale(MadVrCategoryPreset preset);
        void SetMadVrRefinement(MadVrCategoryPreset preset);
        void SetMadVrFps(MadVrFpsChoice choice);
        void SetMadVrHdrMode(MadVrHdrMode mode);
        bool TrySendRendererKey(Keys keyData);
        bool TrySetMadVrExclusiveModeDisabled(bool disabled);
        string GetRendererDiagnosticSnapshot();
    }

    // ======= Store JSON per punti di ripresa =======
    public sealed class DsStreamItem
    {
        public int GlobalIndex;
        public int NativeIndex;
        public string? LanguageKey;
        public bool IsForced;
        public int Group;
        public bool IsAudio;
        public bool IsExternal;
        public bool IsSubtitle;
        public string Name = "";
        public bool Selected;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    // ======= DirectShow unified engine =======
    /// <summary>The DirectShow splitter opened the file but offers no video: only mpv can still play it.</summary>
    public sealed class VideoTrackUnavailableException : ApplicationException
    {
        public VideoTrackUnavailableException(string message) : base(message) { }
    }

    public sealed partial class DirectShowUnifiedEngine : IPlaybackEngine, IVideoScaleEngine
    {
        private readonly bool _preferBitstream;
        private readonly bool _forcePcmToggle;   // true solo se l’utente ha scelto "Forza PCM" nel menu
        private readonly string? _preferredAudioRendererName;
        private readonly VideoRendererChoice _choice;
        private readonly bool _fileIsHdr;
        private readonly AVCodecID _srcAudioCodec;
        private readonly bool _enableMpcvrRtxVideoHdr;
        private readonly int _mpcvrSuperResolutionMode;
        private string _mpcvrUpscalingStatus = "not-applied";

        private IGraphBuilder? _graph;
        private IMediaControl? _control;
        private IMediaSeeking? _seek;
        private IBasicAudio? _basicAudio;

        private IBaseFilter? _lavSource, _lavSourceAudio, _lavVideo, _lavAudio, _videoRenderer, _audioRenderer, _xySubFilter;
        private string? _externalAudioUrl;
        private MpcvrRegistryOverride? _mpcvrRegistryOverride;

        // ======= YouTube (yt-dlp) support =======
        // Cache ultimo resolve: serve per poter costruire un menu "Qualità" (es. tasto destro)
        // e per ridurre i tempi di avvio su riproduzioni ripetute.
        private string? _lastYouTubeUrl;
        private int? _lastYouTubeSelectedHeight;
        private readonly List<int> _lastYouTubeHeights = new();

        /// <summary>
        /// Altezza preferita (es. 720/1080/2160). Se null, prende la migliore disponibile.
        /// Usata SOLO per YouTube.
        /// </summary>
        public int? PreferredYouTubeHeight { get; set; }

        private IMFVideoDisplayControl? _mfDisplay; // EVR/windowless
        private IVideoWindow? _videoWindow;         // madVR e MPCVR windowed
        private IMFVideoMixerBitmap? _mpcvrMixerBitmap;
        private MpcvrMixerBitmapOverlay? _mpcvrMixerOverlay;
        private bool _mpcvrMixerBitmapAttachAttempted;
        private int _mpcvrMixerBitmapLogCount;
        private MFRect _lastDest;

        private System.Windows.Forms.Timer? _timer;
        private bool _hasVideo;
        private Stereo3DMode _stereo = Stereo3DMode.None;

        private volatile bool _bitstreamActive;
        private bool _allowUpscaling = true;
        private bool IsWindowedRenderer => _videoWindow != null && _mfDisplay == null;
        private string _audioRendererName = "?";
        private string _audioRendererDeviceName = "?";
        internal string ActiveAudioRendererDeviceName => _audioRendererDeviceName;

        // === Hotkey bridge: finestra owner da mettere in foreground e drenare messaggi ===
        private nint _lastOwnerHwnd = nint.Zero;
        // Reparenting or restyling madVR's child window restarts presentation work.
        // Repeated layout notifications must not reconfigure an unchanged presenter.
        private readonly record struct VideoPlacementKey(nint Owner, int Width, int Height,
            Stereo3DMode Stereo, bool Upscaling, int SourceWidth, int SourceHeight,
            object? Display, object? Window);
        private VideoPlacementKey? _appliedMadVrPlacement;
        private VideoPlacementKey? _appliedMpcvrPlacement;
        private object? _configuredMadVrWindow;
        private nint _configuredMadVrOwner;

        // Dimensionamento dell'immagine: frazione dell'area del video rispetto alla finestra.
        private double _videoScale = 1;
        public double MaxVideoScale => 1;

        public void SetVideoScale(double scale)
        {
            scale = Math.Clamp(double.IsFinite(scale) ? scale : 1, 0.3, 1);
            if (Math.Abs(scale - _videoScale) < 0.0005) return;
            _videoScale = scale;
            _appliedMadVrPlacement = null;
            _appliedMpcvrPlacement = null;
        }

        private Rectangle ScaledVideoClient(int w, int h)
        {
            if (_videoScale >= 0.9995) return new Rectangle(0, 0, w, h);
            int sw = Math.Max(2, (int)Math.Round(w * _videoScale)), sh = Math.Max(2, (int)Math.Round(h * _videoScale));
            return new Rectangle((w - sw) / 2, (h - sh) / 2, sw, sh);
        }

        private void InvalidateVideoPlacement()
        {
            _appliedMadVrPlacement = null;
            _appliedMpcvrPlacement = null;
            _configuredMadVrWindow = null;
            _configuredMadVrOwner = nint.Zero;
        }
        private nint _initialVideoOwnerHwnd = nint.Zero;
        private Rectangle _initialVideoOwnerClient = Rectangle.Empty;

        // Debug state
        private string _lastVmtDump = "";
        private DateTime _lastVmtAt = DateTime.MinValue;
        private int _lastSnapshotBytes;
        private DateTime _lastSnapshotAt;
        private int _mpcvrWindowUpdateLogCount;

        // === Thumbnailer per anteprime overlay ===
        private Thumbnailer? _thumb;
        private string? _currentMediaPath;
        private bool _graphOpenCompleted;
        private bool _graphPlaybackStarted;
        private bool _fastAppExitDispose;

        // === cache informativa robusta (wrapper immutabile per uso con 'volatile') ===
        private sealed class CachedFmt
        {
            public readonly int W;
            public readonly int H;
            public readonly string Sub;
            public CachedFmt(int w, int h, string sub) { W = w; H = h; Sub = sub; }
        }
        private volatile CachedFmt _cachedFmt = new CachedFmt(0, 0, "?");
        private CancellationTokenSource? _mtPollCts;
        private System.Windows.Forms.Timer? _mtPollTimer;
        private int _mtPollTickCount;
        private System.Windows.Forms.Timer? _audioProbeTimer;
        private System.Windows.Forms.Timer? _audioStartWatchdogTimer;
        private int _audioStartWatchdogTick;
        private bool _clientPaused = true;
        private bool _audioPathConnected;

        // === DEBUG / INTROSPECTION HOOKS (per InfoOverlay / LAV Audio) ===
        public IFilterGraph2? FilterGraph
        {
            get { try { return _graph as IFilterGraph2; } catch { return null; } }
        }
        public IFilterGraph2? GetGraph()
        {
            try
            {
                return _graph as IFilterGraph2;
            }
            catch (InvalidComObjectException)
            {
                // grafo già smontato → per l’overlay è come se non ci fosse
                return null;
            }
            catch
            {
                return null;
            }
        }

        public IBaseFilter? LavAudioFilter => _lavAudio;

        // LAV Audio (LAVAudioSettings.h): l'ordine dei metodi deve coincidere con la vtable.
        [ComImport, Guid("4158A22B-6553-45D0-8069-24716F8FF171"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ILAVAudioSettings
        {
            [PreserveSig] int SetRuntimeConfig([MarshalAs(UnmanagedType.Bool)] bool runtime);
            [PreserveSig] int GetDRC(out int enabled, out int level);
            [PreserveSig] int SetDRC(int enabled, int level);
            [PreserveSig] int GetFormatConfiguration(int codec);
            [PreserveSig] int SetFormatConfiguration(int codec, int enabled);
            [PreserveSig] int GetBitstreamConfig(int codec);
            [PreserveSig] int SetBitstreamConfig(int codec, int enabled);
            [PreserveSig] int GetDTSHDFraming();
            [PreserveSig] int SetDTSHDFraming(int enabled);
            [PreserveSig] int GetAutoAVSync();
            [PreserveSig] int SetAutoAVSync(int enabled);
            [PreserveSig] int GetOutputStandardLayout();
            [PreserveSig] int SetOutputStandardLayout(int enabled);
            [PreserveSig] int GetExpandMono();
            [PreserveSig] int SetExpandMono(int enabled);
            [PreserveSig] int GetExpand61();
            [PreserveSig] int SetExpand61(int enabled);
            [PreserveSig] int GetAllowRawSPDIFInput();
            [PreserveSig] int SetAllowRawSPDIFInput(int allow);
            [PreserveSig] int GetSampleFormat(int format);
            [PreserveSig] int SetSampleFormat(int format, int enabled);
            [PreserveSig] int GetAudioDelay(out int enabled, out int delay);
            [PreserveSig] int SetAudioDelay(int enabled, int delay);
        }

        /// <summary>
        /// Ritardo audio dal vivo tramite LAV Audio: sposta i tempi dei campioni in uscita, il
        /// renderer audio li rispetta subito (anche con madVR, MPC VR ed EVR). LAV lo salva nel
        /// registro, quindi va reimpostato a ogni apertura (0 per i file senza allineamento).
        /// </summary>
        public bool SetAudioDelayMilliseconds(int milliseconds)
        {
            try
            {
                if (_lavAudio is not ILAVAudioSettings lav) return false;
                if (lav.SetAudioDelay(milliseconds != 0 ? 1 : 0, milliseconds) < 0) return false;
                return lav.GetAudioDelay(out int enabled, out int delay) >= 0 && (milliseconds == 0 ? enabled == 0 || delay == 0 : enabled != 0 && delay == milliseconds);
            }
            catch (Exception ex) { Dbg.Warn("[LAV] SetAudioDelay: " + ex.Message); return false; }
        }

        public event Action<double>? OnProgressSeconds;
        public event Action<string>? OnStatus;
        // NOTIFICA cambi PCM/Bitstream
        public event Action<bool>? OnBitstreamChanged;

        public bool IsBitstreamActive() => _bitstreamActive;
        public bool HasDisplayControl()
        {
            return _mfDisplay != null || _videoWindow != null;
        }
        public DirectShowUnifiedEngine(bool preferBitstream, bool forcePcmToggle, string? preferredRendererName, VideoRendererChoice choice, bool fileIsHdr, AVCodecID srcAudioCodec, bool enableMpcvrRtxVideoHdr, int mpcvrSuperResolutionMode = 0)
        {
            _preferBitstream = preferBitstream;
            _forcePcmToggle = forcePcmToggle;
            _preferredAudioRendererName = preferredRendererName;
            _choice = choice;
            _fileIsHdr = fileIsHdr;
            _srcAudioCodec = srcAudioCodec;
            _enableMpcvrRtxVideoHdr = enableMpcvrRtxVideoHdr;
            _mpcvrSuperResolutionMode = Math.Clamp(mpcvrSuperResolutionMode, 0, 4);
            Dbg.Log($"Engine ctor: preferBitstream={preferBitstream}, forcePcmToggle={forcePcmToggle}, preferredAR={preferredRendererName ?? "null"}, choice={choice}, fileIsHdr={fileIsHdr}, mpcvrRtxHdr={enableMpcvrRtxVideoHdr}, mpcvrSuperRes={_mpcvrSuperResolutionMode}");
        }

        public double DurationSeconds
        {
            get
            {
                if (DvdMode) return DvdDuration();
                try
                {
                    var s = _seek;
                    if (s == null) return 0;
                    s.GetDuration(out long d);
                    return d / 10_000_000.0;
                }
                catch (InvalidComObjectException)
                {
                    _seek = null;
                    return 0;
                }
                catch (COMException)
                {
                    return 0;
                }
            }
        }

        public double PositionSeconds
        {
            get
            {
                if (DvdMode) return DvdPosition();
                try
                {
                    var s = _seek;
                    if (s == null) return 0;
                    s.GetCurrentPosition(out long p);
                    return p / 10_000_000.0;
                }
                catch (InvalidComObjectException)
                {
                    _seek = null;
                    return 0;
                }
                catch (COMException)
                {
                    return 0;
                }
            }
            set
            {
                if (DvdMode) { DvdSeek(value); return; }
                try
                {
                    var s = _seek;
                    if (s == null) return;
                    long t = (long)(value * 10_000_000.0);
                    s.SetPositions(t, AMSeekingSeekingFlags.AbsolutePositioning, t, AMSeekingSeekingFlags.NoPositioning);
                }
                catch (InvalidComObjectException)
                {
                    _seek = null;
                }
                catch (COMException)
                {

                }
            }
        }

        public void Open(string mediaPath, bool hasVideo)
        {
            string originalInput = mediaPath;

            string? externalAudioUrl = _externalAudioUrl;
            _externalAudioUrl = null;

            // YouTube: risolviamo watch/embed/shorts → stream diretto (video + eventuale audio separato)
            if (IsYouTubeUrl(mediaPath))
            {
                SafeStatus("YouTube: risoluzione stream...");

                var ytUrl = NormalizeYouTubeUrl(mediaPath);

                if (!TryResolveYouTubeStreams(ytUrl, PreferredYouTubeHeight, out var yt, out var ytErr))
                    throw new ApplicationException("YouTube: impossibile risolvere lo stream. " + ytErr);

                _lastYouTubeUrl = ytUrl;
                _lastYouTubeSelectedHeight = yt.SelectedHeight;

                _lastYouTubeHeights.Clear();
                if (yt.AvailableHeights != null && yt.AvailableHeights.Count > 0)
                    _lastYouTubeHeights.AddRange(yt.AvailableHeights);

                mediaPath = yt.VideoUrl;
                externalAudioUrl = yt.AudioUrl;
                hasVideo = true;

                SafeStatus(string.Empty);
            }
            else
            {
                // reset cache info (serve a non mostrare vecchie "Qualità" quando si apre un file locale)
                _lastYouTubeUrl = null;
                _lastYouTubeSelectedHeight = null;
                _lastYouTubeHeights.Clear();
            }

            // Auto-detect: molti chiamanti passano hasVideo=false per URL senza estensione.
            // Se sembra un URL video, forziamo hasVideo=true.
            if (!hasVideo && LooksLikeVideoUrl(mediaPath))
                hasVideo = true;

            _hasVideo = hasVideo;
            Dbg.Log($"Open(mediaPath='{mediaPath}', hasVideo={hasVideo})  (original='{originalInput}')");

            DisposeGraph(releaseAudioEndpoint: false);
            _graphOpenCompleted = false;
            _graphPlaybackStarted = false;

            _graph = (IGraphBuilder)new FilterGraph();
            _control = (IMediaControl)_graph;
            _seek = (IMediaSeeking)_graph;
            _basicAudio = (IBasicAudio)_graph;

            bool usingLavSource = false;

            // Tracce video marcate "non abilitate" nel file: LAV Splitter non le esporrebbe.
            // Blu-ray (cartella, unita' o ISO montata): LAV Splitter lo apre dal suo index.bdmv e sceglie
            // il titolo principale. DVD e ISO non montate restano a mpv.
            bool isDisc = DiscMedia.TryResolve(mediaPath, out var disc);
            // DVD (unita' o cartella): lo apre il motore DVD di Windows, con menu e navigazione.
            bool dvd = isDisc && hasVideo && IsDvdForNavigator(disc);
            if (dvd)
            {
                OpenDvdNavigator(disc);
                usingLavSource = true;
            }
            else if (isDisc)
            {
                if (disc.DirectShowPath == null) throw new ApplicationException("Questo disco si apre solo con mpv.");
                Dbg.Log($"[DISC] DirectShow opening '{disc.DirectShowPath}', protected={disc.Protected?.ToString() ?? "unknown"}, provider={DiscProtection.Prepare()}", Dbg.LogLevel.Info);
            }
            _disabledTrackView = hasVideo && !isDisc ? MkvDisabledTrackView.TryCreate(mediaPath) : null;
            string sourcePath = dvd ? mediaPath : isDisc ? disc.DirectShowPath! : _disabledTrackView?.Url ?? mediaPath;

            // Prova LAV Splitter Source (source filter)
            if (!dvd) try
            {
                _lavSource = CreateFilterByName("LAV Splitter Source");
                if (_lavSource != null)
                {
                    DsError.ThrowExceptionForHR(_graph!.AddFilter(_lavSource, "LAV Splitter Source"));
                    var ifs = (IFileSourceFilter)_lavSource;
                    DsError.ThrowExceptionForHR(ifs.Load(sourcePath, null));
                    usingLavSource = true;
                }
            }
            catch
            {
                // pulizia best-effort
                try { if (_lavSource != null) _graph!.RemoveFilter(_lavSource); } catch { }
                try { if (_lavSource != null) Marshal.ReleaseComObject(_lavSource); } catch { }
                _lavSource = null;
            }

            // Fallback: File Source (URL) → LAV Splitter (NON-source)
            if (!usingLavSource)
            {
                var urlSrc = CreateFilterByName("File Source (URL)")
                             ?? throw new ApplicationException("File Source (URL) non trovato");
                DsError.ThrowExceptionForHR(_graph!.AddFilter(urlSrc, "File Source (URL)"));
                var ifs2 = (IFileSourceFilter)urlSrc;
                DsError.ThrowExceptionForHR(ifs2.Load(sourcePath, null));

                _lavSource = CreateFilterByName("LAV Splitter")
                             ?? throw new ApplicationException("LAV Splitter non trovato");
                DsError.ThrowExceptionForHR(_graph.AddFilter(_lavSource, "LAV Splitter"));

                var outPin = FindPin(urlSrc, PinDirection.Output, null)
                             ?? throw new ApplicationException("Pin OUT URL mancante");
                var inPin = FindPin(_lavSource, PinDirection.Input, null)
                             ?? throw new ApplicationException("Pin IN LAV mancante");
                DsError.ThrowExceptionForHR(_graph.Connect(outPin, inPin));
            }

            // Se c'è un URL audio separato (DASH, YouTube, ecc.), crea un secondo splitter per l'audio.
            if (!string.IsNullOrWhiteSpace(externalAudioUrl))
            {
                try
                {
                    bool usingLavSourceAudio = false;

                    _lavSourceAudio = CreateFilterByName("LAV Splitter Source");
                    if (_lavSourceAudio != null)
                    {
                        DsError.ThrowExceptionForHR(_graph!.AddFilter(_lavSourceAudio, "LAV Splitter Source (audio)"));
                        var ifsA = (IFileSourceFilter)_lavSourceAudio;
                        DsError.ThrowExceptionForHR(ifsA.Load(externalAudioUrl, null));
                        usingLavSourceAudio = true;
                    }

                    // Fallback: File Source (URL) → LAV Splitter (audio)
                    if (!usingLavSourceAudio)
                    {
                        var urlSrcA = CreateFilterByName("File Source (URL)")
                                      ?? throw new ApplicationException("File Source (URL) non trovato (audio)");
                        DsError.ThrowExceptionForHR(_graph!.AddFilter(urlSrcA, "File Source (URL) (audio)"));
                        var ifs2A = (IFileSourceFilter)urlSrcA;
                        DsError.ThrowExceptionForHR(ifs2A.Load(externalAudioUrl, null));

                        _lavSourceAudio = CreateFilterByName("LAV Splitter")
                                          ?? throw new ApplicationException("LAV Splitter non trovato (audio)");
                        DsError.ThrowExceptionForHR(_graph.AddFilter(_lavSourceAudio, "LAV Splitter (audio)"));

                        var outPinA = FindPin(urlSrcA, PinDirection.Output, null)
                                      ?? throw new ApplicationException("Pin OUT URL audio mancante");
                        var inPinA = FindPin(_lavSourceAudio, PinDirection.Input, null)
                                      ?? throw new ApplicationException("Pin IN LAV audio mancante");
                        DsError.ThrowExceptionForHR(_graph.Connect(outPinA, inPinA));
                    }

                    Dbg.Log("External audio URL aggiunto al grafo tramite splitter dedicato.", Dbg.LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Dbg.Warn("External audio path fallito, uso audio della sorgente principale. EX=" + ex.Message);
                    try { if (_lavSourceAudio != null) _graph!.RemoveFilter(_lavSourceAudio); } catch { }
                    ReleaseCom(ref _lavSourceAudio);
                }
            }

            _lavAudio = CreateFilterByName("LAV Audio Decoder")
                        ?? throw new ApplicationException("LAV Audio Decoder non trovato");
            DsError.ThrowExceptionForHR(_graph.AddFilter(_lavAudio, "LAV Audio"));

            // Keep preview decoding lazy. Opening a second decoder here competes with
            // madVR and LAV while their graph is still negotiating its first frame.
            _currentMediaPath = mediaPath;
            try { _thumb?.Dispose(); } catch { }
            _thumb = null;

            if (_hasVideo)
            {
                if (_lavVideo == null)
                    _lavVideo = CreateFilterByName("LAV Video Decoder")
                                ?? throw new ApplicationException("LAV Video Decoder non trovato");
                ConfigureLavVideoForMpcvr();
                if (_videoRenderer == null)
                    if (_choice == VideoRendererChoice.MADVR) Utilities.MadVrSettingsStore.FlushPending();
                    _videoRenderer = CreateVideoRendererByChoice(_choice)
                                     ?? throw new ApplicationException("Renderer video non disponibile");
                if (_choice == VideoRendererChoice.MADVR)
                    Utilities.MadVrSettingsStore.LiveRenderer = _videoRenderer;

                DsError.ThrowExceptionForHR(_graph.AddFilter(_lavVideo, "LAV Video"));
                DsError.ThrowExceptionForHR(_graph.AddFilter(_videoRenderer, "Video Renderer"));
                if (_choice == VideoRendererChoice.MPCVR)
                    ApplyMpcvrFilterConfig(_videoRenderer);

                // NEW: prova ad aggiungere XySubFilter quando usiamo madVR
                // (non con un DVD: sottotitoli e pulsanti dei menu sono sottoimmagini che disegna LAV Video)
                if (!DvdMode) TryAddXySubFilter();

                AttachDisplayInterfaces(initial: true);
                TryApplyInitialMpcvrVideoHost();
            }

            ConnectAudioPath();
            if (_hasVideo)
            {
                ConnectVideoPath();
                if (!DvdMode)
                {
                    ConnectSubtitlePath(); // NEW: collega il pin dei sottotitoli al renderer madVR via XySubFilter
                    LoadExternalSubtitles(mediaPath);
                }
            }

            PinAudioReferenceClock();
            StartTimer();
            _cachedFmt = new CachedFmt(0, 0, "?");
            TryDetectBitstream();
            try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }

            var audioMode = _bitstreamActive ? "Bitstream" : "PCM";
            var msg = $"Grafo pronto ({audioMode}{(_hasVideo ? ", video" : ", solo audio")}).";
            _graphOpenCompleted = true;
            Dbg.Log(msg);
            SafeStatus(msg);
        }

        /// <summary>
        /// Without an explicit choice the graph picks a clock at Run() by its own search
        /// order, and a splitter or video renderer exposing IReferenceClock can win it: video
        /// is then paced by a clock that drifts from the audio device and lip sync slowly
        /// degrades. The audio renderer is the only clock that tracks the real audio output
        /// (including bitstream), so it is pinned while the graph is still stopped.
        /// </summary>
        private void PinAudioReferenceClock()
        {
            if (_graph is not IMediaFilter graphFilter) return;
            try
            {
                if (_audioRenderer is IReferenceClock audioClock && IsAnyPinConnected(_audioRenderer))
                {
                    int hr = graphFilter.SetSyncSource(audioClock);
                    Dbg.Log($"A/V clock: audio renderer '{_audioRendererName}' (hr=0x{hr:X8}).", Dbg.LogLevel.Info);
                }
                else
                {
                    // No audio path: let the graph fall back to the system clock.
                    int hr = _graph.SetDefaultSyncSource();
                    Dbg.Log($"A/V clock: default graph clock (hr=0x{hr:X8}).", Dbg.LogLevel.Info);
                }
            }
            catch (Exception ex) { Dbg.Warn("A/V clock selection: " + ex.Message); }
        }

        private static bool IsAnyPinConnected(IBaseFilter filter)
        {
            IEnumPins? pins = null;
            try
            {
                if (filter.EnumPins(out pins) < 0 || pins == null) return false;
                var pin = new IPin[1];
                while (pins.Next(1, pin, IntPtr.Zero) == 0)
                {
                    try
                    {
                        if (pin[0].ConnectedTo(out IPin other) == 0 && other != null)
                        {
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(other);
                            return true;
                        }
                    }
                    finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(pin[0]); }
                }
                return false;
            }
            finally { if (pins != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(pins); }
        }

        private void ConfigureLavVideoForMpcvr()
        {
            if (_choice != VideoRendererChoice.MPCVR) return;
            if (_lavVideo == null) return;

            try
            {
                if (_lavVideo is not ILAVVideoSettings lav)
                {
                    Dbg.Warn("MPCVR: ILAVVideoSettings non disponibile su LAV Video.");
                    return;
                }

                int oldHw = 0;
                try { oldHw = lav.GetHWAccel(); } catch { oldHw = -1; }

                int hrRuntime = lav.SetRuntimeConfig(true);
                ApplyLavVideoRegistryToRuntime(lav);

                // MPC-BE non forza un formato LAV "safe": lascia che MPCVR negozi
                // il formato migliore. Qui abilitiamo solo i formati utili, inclusi
                // P010/P016 per HDR/deep color, senza cambiare l'accelerazione hardware.
                int hrP010 = lav.SetPixelFormat((int)LavOutPixFmt.P010, true);
                int hrP016 = lav.SetPixelFormat((int)LavOutPixFmt.P016, true);
                int hrNV12 = lav.SetPixelFormat((int)LavOutPixFmt.NV12, true);
                int hrYV12 = lav.SetPixelFormat((int)LavOutPixFmt.YV12, true);
                int hrYUY2 = lav.SetPixelFormat((int)LavOutPixFmt.YUY2, true);
                int hrRGB32 = lav.SetPixelFormat((int)LavOutPixFmt.RGB32, true);

                Dbg.Log(
                    $"MPCVR LAV Video MPC-BE style config: runtime=0x{hrRuntime:X8}, hwKept={oldHw}, " +
                    $"P010on=0x{hrP010:X8}, P016on=0x{hrP016:X8}, " +
                    $"NV12on=0x{hrNV12:X8}, YV12on=0x{hrYV12:X8}, YUY2on=0x{hrYUY2:X8}, RGB32on=0x{hrRGB32:X8}",
                    Dbg.LogLevel.Info);
            }
            catch (Exception ex)
            {
                Dbg.Warn("MPCVR LAV Video runtime config EX: " + ex.Message);
            }
        }

        private void HdrTrace(string text) { if (_fileIsHdr) Dbg.Log("[HDR] " + text, Dbg.LogLevel.Info); }

        public void SetExternalAudioUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            _externalAudioUrl = url;
            Dbg.Log("External audio URL set: " + url, Dbg.LogLevel.Verbose);
        }


        public IReadOnlyList<int> GetYouTubeAvailableHeights()
        {
            // ritorna l'elenco delle altezze disponibili (es. 360/480/720/1080...)
            return _lastYouTubeHeights;
        }

        public int? GetYouTubeSelectedHeight()
        {
            return _lastYouTubeSelectedHeight;
        }

        public bool TryReopenYouTubeAtHeight(int height)
        {
            if (height <= 0) return false;
            if (string.IsNullOrWhiteSpace(_lastYouTubeUrl)) return false;

            try
            {
                PreferredYouTubeHeight = height;
                Open(_lastYouTubeUrl!, hasVideo: true);
                return true;
            }
            catch { return false; }
        }



        // -------------------- YouTube resolve (yt-dlp) --------------------

        private bool ConnectAudioPath()
        {
            bool sourceHasAudio = false;
            _audioPathConnected = false;

            try
            {
                if (_audioRenderer == null)
                {
                    _audioRenderer = PickAudioRenderer(_preferredAudioRendererName);
                    if (_audioRenderer != null)
                    {
                        DsError.ThrowExceptionForHR(_graph!.AddFilter(_audioRenderer, "Audio Renderer"));
                        _audioRendererName = FilterFriendlyName(_audioRenderer);
                        Dbg.Log($"Audio renderer selected: filter='{_audioRendererName}', device='{_audioRendererDeviceName}', preferred='{_preferredAudioRendererName ?? "null"}', preferBitstream={_preferBitstream}, forcePcm={_forcePcmToggle}", Dbg.LogLevel.Info);
                    }
                }

                var srcSplitter = _lavSourceAudio ?? _lavSource;
                if (srcSplitter == null)
                {
                    Dbg.Error("Audio: nessun splitter disponibile.");
                    return false;
                }

                var srcA = DvdMode ? DvdAudioPin()
                           : FindPin(srcSplitter, PinDirection.Output, MediaType.Audio)
                           ?? FindPin(srcSplitter, PinDirection.Output, null);
                // Un file senza audio ha solo il pin video (e magari i sottotitoli), e FindPin ripiega sul
                // primo pin d'uscita: collegarlo al decoder audio lasciava l'apertura bloccata per sempre.
                if (srcA != null && !PinHasType(srcA, MediaType.Audio) &&
                    (PinHasType(srcA, MediaType.Video) || PinHasType(srcA, new Guid("E487EB08-6B26-4BE9-9DD3-993434D313FD"))))
                    srcA = null;

                if (srcA == null) { Dbg.Warn("Audio: nessun pin AUDIO dallo splitter."); return false; }
                sourceHasAudio = true;
                if (_audioRenderer == null) throw new ApplicationException("Audio renderer non disponibile.");

                var rIn = FindPin(_audioRenderer, PinDirection.Input, null);
                var aIn = FindPin(_lavAudio!, PinDirection.Input, MediaType.Audio)
                          ?? FindPin(_lavAudio!, PinDirection.Input, null);
                var aOut = FindPin(_lavAudio!, PinDirection.Output, null);

                int hr;
                // Forziamo il graph PCM solo se:
                //  - NON vogliamo bitstream (_preferBitstream == false)
                //  - e il codec sorgente è uno di quelli bitstreamabili (AC3/DTS/TrueHD/EAC3).
                // Per FLAC, WAV, MP3, AAC, ecc. lasciamo negoziare liberamente.
                bool forcePcmGraph = _forcePcmToggle && !_preferBitstream && MediaProbe.IsPassthroughCandidate(_srcAudioCodec);

                // ===== BLOCCO PER FORZARE PCM se richiesto =====

                if (_forcePcmToggle && rIn != null)
                {
                    try
                    {
                        var mpc = CreateFilterByName("MPC Audio Decoder");
                        if (mpc != null)
                        {
                            Dbg.Log("Audio: Forza PCM attivo → provo percorso con 'MPC Audio Decoder'.", Dbg.LogLevel.Info);

                            DsError.ThrowExceptionForHR(_graph!.AddFilter(mpc, "MPC Audio Decoder"));

                            var mpcIn = FindPin(mpc, PinDirection.Input, null);
                            var mpcOut = FindPin(mpc, PinDirection.Output, null);

                            if (mpcIn != null && mpcOut != null)
                            {
                                // LAV Splitter → MPC Audio Decoder
                                hr = _graph.Connect(srcA, mpcIn);
                                DsError.ThrowExceptionForHR(hr);
                                Dbg.Log("Audio: LAV Splitter → MPC Audio Decoder", Dbg.LogLevel.Verbose);

                                // MPC Audio Decoder → Audio Renderer
                                hr = _graph.Connect(mpcOut, rIn);
                                DsError.ThrowExceptionForHR(hr);
                                Dbg.Log("Audio: MPC Audio Decoder → Renderer (PCM)", Dbg.LogLevel.Verbose);

                                _bitstreamActive = false; // siamo sicuramente in PCM
                                _audioPathConnected = true;
                                Dbg.Log("Audio path negoziato: PCM/Decode (MPC Audio Decoder)");
                                _updateCb?.Invoke();
                                return true; // IMPORTANTE: non proseguire con il ramo LAV Audio
                            }
                            else
                            {
                                Dbg.Warn("Audio: MPC Audio Decoder trovato ma pin input/output null → fallback a LAV Audio.");
                                try { _graph.RemoveFilter(mpc); } catch { }
                                Marshal.ReleaseComObject(mpc);
                            }
                        }
                        else
                        {
                            Dbg.Warn("Audio: filtro 'MPC Audio Decoder' non trovato → fallback a LAV Audio.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn("Audio: Forza PCM con MPC Audio Decoder fallito → fallback a LAV Audio. EX=" + ex.Message);
                    }
                }

                // ===== FINE BLOCCO FORZATURA PCM =====

                if (aIn != null && aOut != null && rIn != null)
                {
                    // Sempre: splitter → LAV Audio
                    hr = _graph!.Connect(srcA, aIn);
                    if (hr != 0)
                    {
                        Dbg.Warn($"Audio: LAV Splitter -> LAV Audio fallita hr=0x{hr:X8}; provo collegamento diretto al renderer.");
                        try
                        {
                            ConnectAudioOutToRendererOrFallback(srcA, "Audio: LAV Splitter -> Renderer (direct fallback)");
                            _audioPathConnected = true;
                            TryDetectBitstream();
                            Dbg.Log("Audio path negoziato senza LAV Audio: " + (_bitstreamActive ? "Bitstream (IEC61937)" : "PCM/Decode"));
                            _updateCb?.Invoke();
                            return true;
                        }
                        catch (Exception directEx)
                        {
                            Dbg.Warn("Audio: direct fallback fallito. EX=" + directEx.Message);
                        }

                        DsError.ThrowExceptionForHR(hr);
                    }
                    Dbg.Log("Audio: LAV Splitter → LAV Audio", Dbg.LogLevel.Verbose);

                    if (forcePcmGraph)
                    {
                        bool forced = false;

                        try
                        {
                            // Cerco un media type PCM/IEEE_FLOAT dall’output di LAV Audio
                            aOut.EnumMediaTypes(out var e);
                            try
                            {
                                var arr = new AMMediaType[1];
                                while (e.Next(1, arr, nint.Zero) == 0)
                                {
                                    var mt = arr[0];
                                    if (mt.subType == MediaSubType.PCM || mt.subType == MediaSubType.IEEE_FLOAT)
                                    {
                                        hr = _graph.ConnectDirect(aOut, rIn, mt);
                                        DsError.ThrowExceptionForHR(hr);
                                        Dbg.Log("Audio: LAV Audio → Renderer (PCM forced)", Dbg.LogLevel.Verbose);
                                        forced = true;
                                        _audioPathConnected = true;
                                        DsUtils.FreeAMMediaType(mt);
                                        break;
                                    }

                                    DsUtils.FreeAMMediaType(mt);
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(e);
                            }
                        }
                        catch (Exception ex)
                        {
                            Dbg.Warn("ConnectAudioPath: forcing PCM failed, fallback to default connect. EX=" + ex.Message);
                        }

                        if (!forced)
                        {
                            // Fallback: come prima
                            ConnectAudioOutToRendererOrFallback(aOut, "Audio: LAV Audio → Renderer (fallback)");
                            _audioPathConnected = true;
                        }
                    }
                    else
                    {
                        // Modalità normale: lascia negoziare liberamente (bitstream se configurato)
                        ConnectAudioOutToRendererOrFallback(aOut, "Audio: LAV Audio → Renderer");
                        _audioPathConnected = true;
                    }
                }
                else if (rIn != null)
                {
                    {
                        ConnectAudioOutToRendererOrFallback(srcA, "Audio: LAV Splitter → Renderer (direct)");
                        _audioPathConnected = true;
                    }
                }
                else
                {
                    throw new ApplicationException("Pin input audio renderer non trovato.");
                }

                TryDetectBitstream();
                Dbg.Log("Audio path negoziato: " + (_bitstreamActive ? "Bitstream (IEC61937)" : "PCM/Decode"));
                _updateCb?.Invoke();
                return _audioPathConnected;
            }
            catch (Exception ex)
            {
                Dbg.Error("ConnectAudioPath EX: " + ex);
                if (sourceHasAudio && _hasVideo)
                {
                    Dbg.Warn("Audio path non disponibile: continuo la riproduzione video senza bloccare il file.");
                    _audioPathConnected = false;
                    _bitstreamActive = false;
                    _updateCb?.Invoke();
                    return false;
                }
                if (sourceHasAudio)
                    throw new ApplicationException("Connessione audio fallita: " + ex.Message, ex);
                return false;
            }
        }

        /// <summary>
        /// A dual-layer Dolby Vision remux carries two video tracks: the 4K base layer and a
        /// 1080p enhancement layer that no decoder accepts by itself. When the splitter has the
        /// wrong one enabled the decoder refuses the connection and the film never opens, so
        /// with more than one video track the largest picture is the one to play.
        /// </summary>
        private MkvDisabledTrackView? _disabledTrackView;

        private void SelectMainVideoStream()
        {
            try
            {
                if (_lavSource is not IAMStreamSelect sel) return;
                sel.Count(out int count);
                int best = -1, enabled = -1, videoStreams = 0;
                long bestArea = -1, enabledArea = -1;
                var seen = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    AMMediaType? mt = null;
                    object? a = null, b = null;
                    try
                    {
                        if (sel.Info(i, out mt, out AMStreamSelectInfoFlags flags, out _, out int group, out string name, out a, out b) < 0) continue;
                        if (mt == null || mt.majorType != MediaType.Video || group != 0) continue;
                        videoStreams++;
                        long area = VideoStreamArea(mt, name);
                        bool on = (flags & (AMStreamSelectInfoFlags.Enabled | AMStreamSelectInfoFlags.Exclusive)) != 0;
                        seen.Add($"#{i} '{name}' area={area}{(on ? " (on)" : "")}");
                        if (on) { enabled = i; enabledArea = area; }
                        if (area > bestArea) { bestArea = area; best = i; }
                    }
                    finally
                    {
                        if (mt != null) DsUtils.FreeAMMediaType(mt);
                        if (a != null && Marshal.IsComObject(a)) Marshal.ReleaseComObject(a);
                        if (b != null && Marshal.IsComObject(b)) Marshal.ReleaseComObject(b);
                    }
                }
                if (videoStreams < 2) return;
                Dbg.Log("Video tracks: " + string.Join("; ", seen));
                if (best < 0 || best == enabled || bestArea <= enabledArea) return;
                sel.Enable(best, AMStreamSelectEnableFlags.Enable);
                Dbg.Log($"Video track #{best} selected (largest picture) instead of #{enabled}.");
            }
            catch (Exception ex) { Dbg.Warn("SelectMainVideoStream EX: " + ex.Message); }
        }

        /// <summary>What a filter offers on each pin, written once when the video chain cannot be built.</summary>
        private static void LogPinsForDiagnostics(string label, IBaseFilter? filter)
        {
            if (filter == null) return;
            try
            {
                filter.EnumPins(out var pins);
                try
                {
                    var one = new IPin[1];
                    while (pins.Next(1, one, nint.Zero) == 0)
                    {
                        try
                        {
                            one[0].QueryPinInfo(out var info);
                            if (info.filter != null) Marshal.ReleaseComObject(info.filter);
                            bool connected = one[0].ConnectedTo(out var peer) == 0 && peer != null;
                            if (peer != null) Marshal.ReleaseComObject(peer);
                            var types = new List<string>();
                            if (one[0].EnumMediaTypes(out var e) == 0 && e != null)
                            {
                                var mt = new AMMediaType[1];
                                while (types.Count < 4 && e.Next(1, mt, nint.Zero) == 0)
                                {
                                    types.Add($"{mt[0].majorType:D}/{mt[0].subType:D} area={VideoStreamArea(mt[0], null)}");
                                    DsUtils.FreeAMMediaType(mt[0]);
                                }
                                Marshal.ReleaseComObject(e);
                            }
                            Dbg.Warn($"[GRAPH] {label} pin '{info.name}' {info.dir}{(connected ? " connected" : "")}: {(types.Count == 0 ? "no types" : string.Join(" | ", types))}");
                        }
                        finally { Marshal.ReleaseComObject(one[0]); }
                    }
                }
                finally { Marshal.ReleaseComObject(pins); }
            }
            catch (Exception ex) { Dbg.Warn($"[GRAPH] {label} pins EX: " + ex.Message); }
        }

        private static long VideoStreamArea(AMMediaType mt, string? name)
        {
            try
            {
                // VIDEOINFOHEADER2 (also the head of MPEG2VIDEOINFO) keeps its BITMAPINFOHEADER at 72, VIDEOINFOHEADER at 48.
                int header = mt.formatType == FormatType.VideoInfo2 || mt.formatType == FormatType.Mpeg2Video ? 72
                           : mt.formatType == FormatType.VideoInfo || mt.formatType == FormatType.MpegVideo ? 48 : -1;
                if (header > 0 && mt.formatPtr != nint.Zero && mt.formatSize >= header + 12)
                {
                    long w = Math.Abs(Marshal.ReadInt32(mt.formatPtr, header + 4));
                    long h = Math.Abs(Marshal.ReadInt32(mt.formatPtr, header + 8));
                    if (w > 0 && h > 0) return w * h;
                }
            }
            catch { }
            var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"(\d{3,5})\s*x\s*(\d{3,5})");
            return m.Success ? long.Parse(m.Groups[1].Value) * long.Parse(m.Groups[2].Value) : 0;
        }

        private bool SplitterOffersVideo()
        {
            if (_lavSource == null) return false;
            _lavSource.EnumPins(out var pins);
            try
            {
                var one = new IPin[1];
                while (pins.Next(1, one, nint.Zero) == 0)
                {
                    try
                    {
                        one[0].QueryDirection(out var dir);
                        if (dir == PinDirection.Output && PinHasType(one[0], MediaType.Video)) return true;
                    }
                    finally { Marshal.ReleaseComObject(one[0]); }
                }
                return false;
            }
            finally { Marshal.ReleaseComObject(pins); }
        }

        private void ConnectVideoPath()
        {
            // Some files (dual-track Dolby Vision remuxes with parameter sets only in-band) leave
            // LAV Splitter without a video pin at all: no DirectShow renderer can show them.
            bool offersVideo = true;
            try { offersVideo = DvdMode || SplitterOffersVideo(); } catch { }
            if (!offersVideo)
            {
                LogPinsForDiagnostics("splitter", _lavSource);
                throw new VideoTrackUnavailableException("LAV Splitter non espone la traccia video di questo file.");
            }
            try
            {
                if (DvdMode) ConnectDvdVideoPins();
                else
                {
                    SelectMainVideoStream();
                    ConnectByType(_lavSource!, _lavVideo!, MediaType.Video);
                    Dbg.Log("Video: LAV Splitter → LAV Video", Dbg.LogLevel.Verbose);
                }

                var vOut = FindPin(_lavVideo!, PinDirection.Output, null) ?? throw new ApplicationException("Pin out LAV Video non trovato");
                var rIn = FindPin(_videoRenderer!, PinDirection.Input, null) ?? throw new ApplicationException("Pin in renderer non trovato");

                if (_choice == VideoRendererChoice.MPCVR)
                {
                    int hr = _graph!.Connect(vOut, rIn);
                    if (hr == 0)
                    {
                        Dbg.Log("Video: LAV Video -> MPCVR (Connect) OK", Dbg.LogLevel.Verbose);
                    }
                    else if (TryConnectMpcvrPreferredVideoSubtype(vOut, rIn))
                    {
                        Dbg.Log("Video: LAV Video -> MPCVR connected with explicit fallback media type.", Dbg.LogLevel.Info);
                    }
                    else
                    {
                        DsError.ThrowExceptionForHR(hr);
                    }

                    if (hr == 0)
                        Dbg.Log("Video: LAV Video → Renderer (Connect) OK", Dbg.LogLevel.Verbose);
                }
                else
                {
                    int hr = _graph!.ConnectDirect(vOut, rIn, null);
                    if (hr == 0)
                    {
                        Dbg.Log("Video: LAV Video → Renderer (ConnectDirect) OK", Dbg.LogLevel.Verbose);
                    }
                    else
                    {
                        Dbg.Warn($"ConnectDirect fallito (hr=0x{hr:X8}). Se 0x80040217, abilita NV12/P010 in LAV Video → Output Formats. Provo Connect()…");
                        DsError.ThrowExceptionForHR(_graph.Connect(vOut, rIn));
                        Dbg.Log("Video: LAV Video → Renderer (Connect) OK", Dbg.LogLevel.Verbose);
                    }
                }

                bool keepPreWindowed = _videoWindow != null && _choice == VideoRendererChoice.MADVR;
                if (!keepPreWindowed) AttachDisplayInterfaces(initial: false);
                TryApplyInitialMpcvrVideoHost();
                try { _mfDisplay?.SetAspectRatioMode((int)MFVideoARMode.PreservePicture); } catch { }

                if (_hasVideo && _mfDisplay == null && _videoWindow == null)
                    Dbg.Warn("Renderer connesso ma nessun display control – ritento dopo Run().");

                // snapshot MT già qui, ma alcuni renderer negoziano solo dopo Run()
                TryDumpNegotiatedVideoMT();
                return;
            }
            catch (Exception firstEx)
            {
                Dbg.Warn("ConnectVideoPath primo tentativo EX: " + firstEx.Message);
                LogPinsForDiagnostics("splitter", _lavSource);
                LogPinsForDiagnostics("LAV Video", _lavVideo);

                try
                {
                    var csc = CreateFilterByClsid(new Guid("1643E180-90F5-11CE-97D5-00AA0055595A"), "Color Space Converter")
                              ?? throw new ApplicationException("Color Space Converter non disponibile");
                    int hrAdd = _graph!.AddFilter(csc, "Color Space Converter");
                    DsError.ThrowExceptionForHR(hrAdd);

                    var vOut = FindPin(_lavVideo!, PinDirection.Output, null) ?? throw new ApplicationException("Pin out LAV Video non trovato");
                    var cIn = FindPin(csc, PinDirection.Input, MediaType.Video) ?? FindPin(csc, PinDirection.Input, null) ?? throw new ApplicationException("Pin in CSC non trovato");
                    var cOut = FindPin(csc, PinDirection.Output, MediaType.Video) ?? FindPin(csc, PinDirection.Output, null) ?? throw new ApplicationException("Pin out CSC non trovato");
                    var rIn = FindPin(_videoRenderer!, PinDirection.Input, null) ?? throw new ApplicationException("Pin in renderer non trovato");

                    DsError.ThrowExceptionForHR(_graph.Connect(vOut, cIn));
                    Dbg.Log("Video: LAV Video → Color Space Converter", Dbg.LogLevel.Verbose);

                    int hr2 = _graph.ConnectDirect(cOut, rIn, null);
                    if (hr2 != 0)
                    {
                        Dbg.Warn($"CSC → Renderer ConnectDirect fallito (hr=0x{hr2:X8}), riprovo Connect()…");
                        DsError.ThrowExceptionForHR(_graph.Connect(cOut, rIn));
                    }
                    Dbg.Log("Video: Color Space Converter → Renderer OK", Dbg.LogLevel.Verbose);

                    AttachDisplayInterfaces(initial: false);
                    TryApplyInitialMpcvrVideoHost();
                    if (_hasVideo && _mfDisplay == null && _videoWindow == null)
                        throw new ApplicationException("No display control after CSC fallback");
                    try { _mfDisplay?.SetAspectRatioMode((int)MFVideoARMode.PreservePicture); } catch { }
                    TryDumpNegotiatedVideoMT();
                    return;
                }
                catch (Exception cscEx) { Dbg.Error("ConnectVideoPath fallback CSC EX: " + cscEx.Message); throw; }
            }
        }

        private bool TryConnectMpcvrPreferredVideoSubtype(IPin videoOut, IPin rendererIn)
        {
            if (_graph == null) return false;

            var p010 = new Guid("30313050-0000-0010-8000-00AA00389B71");
            var p016 = new Guid("36313050-0000-0010-8000-00AA00389B71");
            var rank = _fileIsHdr
                ? new Dictionary<Guid, int>
                {
                    [p010] = 0,
                    [p016] = 1,
                    [MediaSubType.NV12] = 2,
                    [MediaSubType.YV12] = 3,
                    [MediaSubType.YUY2] = 4,
                    [MediaSubType.RGB32] = 5
                }
                : new Dictionary<Guid, int>
                {
                    [MediaSubType.NV12] = 0,
                    [MediaSubType.YV12] = 1,
                    [MediaSubType.YUY2] = 2,
                    [MediaSubType.RGB32] = 3,
                    [p010] = 8,
                    [p016] = 9
                };

            IEnumMediaTypes? enumTypes = null;
            var candidates = new List<(AMMediaType Mt, string Name, int Rank, int W, int H)>();

            try
            {
                int hrEnum = videoOut.EnumMediaTypes(out enumTypes);
                if (hrEnum != 0 || enumTypes == null)
                {
                    Dbg.Warn($"Video: LAV Video -> MPCVR EnumMediaTypes failed hr=0x{hrEnum:X8}");
                    return false;
                }

                var arr = new AMMediaType[1];
                while (enumTypes.Next(1, arr, nint.Zero) == 0)
                {
                    var mt = arr[0];
                    if (mt == null) continue;

                    try
                    {
                        if (mt.majorType != MediaType.Video || !rank.TryGetValue(mt.subType, out int r))
                        {
                            DsUtils.FreeAMMediaType(mt);
                            continue;
                        }

                        int w = 0, h = 0;
                        TryReadVideoSizeFromMediaType(mt, out w, out h);
                        candidates.Add((mt, GuidToCodecName(mt.subType), r, w, h));
                    }
                    catch
                    {
                        try { DsUtils.FreeAMMediaType(mt); } catch { }
                    }
                }

                if (candidates.Count == 0)
                {
                    Dbg.Warn("Video: LAV Video -> MPCVR no preferred enumerated candidates.");
                    return false;
                }

                var failures = new List<string>();
                foreach (var candidate in candidates.OrderBy(c => c.Rank))
                {
                    int hr = _graph.ConnectDirect(videoOut, rendererIn, candidate.Mt);
                    if (hr == 0)
                    {
                        Dbg.Log($"Video: LAV Video -> MPCVR enumerated subtype {candidate.Name} OK ({candidate.W}x{candidate.H})", Dbg.LogLevel.Info);
                        return true;
                    }

                    failures.Add($"{candidate.Name}({candidate.W}x{candidate.H})=0x{hr:X8}");
                }

                Dbg.Warn("Video: LAV Video -> MPCVR enumerated subtype failed; fallback auto. " + string.Join(", ", failures));
                return false;
            }
            catch (Exception ex)
            {
                Dbg.Warn("Video: LAV Video -> MPCVR enumerated subtype EX: " + ex.Message);
                return false;
            }
            finally
            {
                foreach (var candidate in candidates)
                    try { DsUtils.FreeAMMediaType(candidate.Mt); } catch { }
                if (enumTypes != null) try { Marshal.ReleaseComObject(enumTypes); } catch { }
            }
        }

        // === XySubFilter: add & connect subtitles ===
        private void TryAddXySubFilter()
        {
            // Solo con video + madVR ha senso usare XySubFilter
            if (!_hasVideo) return;
            if (_choice != VideoRendererChoice.MADVR) return;
            if (_graph == null) return;
            if (_xySubFilter != null) return;

            try
            {
                _xySubFilter = CreateFilterByName("XySubFilter")
                    ?? CreateFilterByName("XySubFilterAutoLoader")
                    ?? DsHelpers.CreateBundledXySubFilter();
                if (_xySubFilter == null)
                {
                    Dbg.Log("XySubFilter non trovato – sottotitoli madVR non disponibili.", Dbg.LogLevel.Verbose);
                    return;
                }

                DsError.ThrowExceptionForHR(_graph.AddFilter(_xySubFilter, "XySubFilter"));
                Dbg.Log("XySubFilter aggiunto al grafo (madVR subtitle renderer).");
            }
            catch (Exception ex)
            {
                Dbg.Warn("TryAddXySubFilter EX: " + ex.Message);
                if (_xySubFilter != null)
                {
                    try { _graph!.RemoveFilter(_xySubFilter); } catch { }
                    ReleaseCom(ref _xySubFilter);
                }
            }
        }

        private void ConnectSubtitlePath()
        {
            IPin? lavSubPin = null;
            IPin? xyIn = null;

            try
            {
                if (!_hasVideo) return;
                if (_choice != VideoRendererChoice.MADVR) return;
                if (_graph == null || _lavSource == null || _xySubFilter == null) return;

                lavSubPin = FindFirstSubtitlePin(_lavSource);
                if (lavSubPin == null)
                {
                    Dbg.Log("ConnectSubtitlePath: nessun pin subtitles dal LAV Splitter.", Dbg.LogLevel.Verbose);
                    return;
                }

                xyIn = FindFirstFreeInputPin(_xySubFilter);
                if (xyIn == null)
                {
                    Dbg.Log("ConnectSubtitlePath: nessun pin input libero su XySubFilter.", Dbg.LogLevel.Verbose);
                    return;
                }

                int hr = _graph.Connect(lavSubPin, xyIn);
                if (hr != 0)
                {
                    Dbg.Warn($"ConnectSubtitlePath: Connect LAV → XySubFilter hr=0x{hr:X8}");
                }
                else
                {
                    Dbg.Log("ConnectSubtitlePath: LAV Splitter (sub) → XySubFilter connessi.");
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("ConnectSubtitlePath EX: " + ex.Message);
            }
            finally
            {
                if (lavSubPin != null) Marshal.ReleaseComObject(lavSubPin);
                if (xyIn != null) Marshal.ReleaseComObject(xyIn);
            }
        }

        private static IPin? FindFirstSubtitlePin(IBaseFilter f)
        {
            f.EnumPins(out var e);
            try
            {
                var arr = new IPin[1];
                while (e.Next(1, arr, nint.Zero) == 0)
                {
                    var pin = arr[0];
                    pin.QueryDirection(out var dir);
                    if (dir != PinDirection.Output)
                    {
                        Marshal.ReleaseComObject(pin);
                        continue;
                    }

                    // già collegato? allora salta
                    pin.ConnectedTo(out var connected);
                    if (connected != null)
                    {
                        Marshal.ReleaseComObject(connected);
                        Marshal.ReleaseComObject(pin);
                        continue;
                    }

                    if (LooksLikeSubtitlePin(pin))
                    {
                        return pin; // il chiamante rilascia
                    }

                    Marshal.ReleaseComObject(pin);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(e);
            }

            return null;
        }

        private static bool LooksLikeSubtitlePin(IPin pin)
        {
            try
            {
                pin.EnumMediaTypes(out var e);
                try
                {
                    var arr = new AMMediaType[1];
                    while (e.Next(1, arr, nint.Zero) == 0)
                    {
                        var mt = arr[0];
                        try
                        {
                            if (mt.majorType == MediaType.Texts)
                                return true;

                            if (mt.majorType != MediaType.Audio &&
                                mt.majorType != MediaType.Video &&
                                mt.majorType != Guid.Empty)
                            {
                                // non audio/video → spesso sottotitoli / subpicture
                                return true;
                            }

                            var st = mt.subType.ToString().ToUpperInvariant();
                            if (st.Contains("SUB") ||
                                st.Contains("PGS") ||
                                st.Contains("SSA") ||
                                st.Contains("ASS") ||
                                st.Contains("S_TEXT") ||
                                st.Contains("HDMV") ||
                                st.Contains("VOBSUB"))
                            {
                                return true;
                            }
                        }
                        finally
                        {
                            DsUtils.FreeAMMediaType(mt);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(e);
                }
            }
            catch
            {
                // in dubbio: meglio "no" che connettere roba a caso
            }
            return false;
        }

        private static IPin? FindFirstFreeInputPin(IBaseFilter f)
        {
            f.EnumPins(out var e);
            try
            {
                var arr = new IPin[1];
                while (e.Next(1, arr, nint.Zero) == 0)
                {
                    var pin = arr[0];
                    pin.QueryDirection(out var dir);
                    if (dir != PinDirection.Input)
                    {
                        Marshal.ReleaseComObject(pin);
                        continue;
                    }

                    pin.ConnectedTo(out var connected);
                    if (connected != null)
                    {
                        Marshal.ReleaseComObject(connected);
                        Marshal.ReleaseComObject(pin);
                        continue;
                    }

                    return pin; // il chiamante rilascia
                }
            }
            finally
            {
                Marshal.ReleaseComObject(e);
            }

            return null;
        }

        private IBaseFilter? CreateFilterByClsid(Guid clsid, string friendlyForLog)
        {
            for (int attempt = 0; attempt < ComCreateMaxAttempts; attempt++)
            {
                try
                {
                    if (attempt > 0)
                        PrepareComCreateRetry(attempt);

                    // Prima prova CoCreateInstance esplicito in-proc: con MPC Video Renderer evita
                    // alcuni casi in cui Activator/registration wrapper ritorna E_ABORT senza
                    // consegnare un IBaseFilter valido.
                    var byCoCreate = TryCreateFilterByClsidInProc(clsid, friendlyForLog);
                    if (byCoCreate != null)
                        return byCoCreate;

                    var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
                    var obj = Activator.CreateInstance(type);
                    if (obj is IBaseFilter f)
                    {
                        Dbg.Log($"CreateFilterByClsid: {friendlyForLog} → OK", Dbg.LogLevel.Verbose);
                        return f;
                    }

                    if (obj != null && Marshal.IsComObject(obj))
                    {
                        try { Marshal.ReleaseComObject(obj); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    bool retryable = IsAbortComFailure(ex);
                    Dbg.Warn($"CreateFilterByClsid: {friendlyForLog} attempt {attempt + 1}/{ComCreateMaxAttempts} EX: {ex.Message}");
                    if (!retryable)
                        break;
                }
            }
            return null;
        }

        private static IBaseFilter? TryCreateFilterByClsidInProc(Guid clsid, string friendlyForLog)
        {
            IntPtr unk = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IBaseFilter).GUID;
                Guid cls = clsid;
                int hr = CoCreateInstance(ref cls, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out unk);
                if (hr != 0 || unk == IntPtr.Zero)
                {
                    if (hr != unchecked((int)0x80040154))
                        Dbg.Warn($"CoCreateInstance: {friendlyForLog} hr=0x{hr:X8}");
                    return null;
                }

                var obj = Marshal.GetObjectForIUnknown(unk);
                if (obj is IBaseFilter f)
                {
                    Dbg.Log($"CoCreateInstance: {friendlyForLog} → OK", Dbg.LogLevel.Verbose);
                    return f;
                }

                if (obj != null && Marshal.IsComObject(obj))
                    try { Marshal.ReleaseComObject(obj); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                Dbg.Warn($"CoCreateInstance: {friendlyForLog} EX: {ex.Message}");
                return null;
            }
            finally
            {
                if (unk != IntPtr.Zero)
                    try { Marshal.Release(unk); } catch { }
            }
        }

        private static bool IsAbortComFailure(Exception ex)
        {
            return ex is COMException com && unchecked((uint)com.HResult) == 0x80004004;
        }

        private static void PrepareComCreateRetry(int attempt)
        {
            try { GC.Collect(); } catch { }
            try { GC.WaitForPendingFinalizers(); } catch { }
            try { CoFreeUnusedLibraries(); } catch { }
            try { Thread.Sleep(attempt <= 0 ? 160 : Math.Min(900, 220 + attempt * 170)); } catch { }
        }

        public void SetStereo3D(Stereo3DMode mode)
        {
            if (_stereo == mode) return;
            _stereo = mode;
            InvalidateVideoPlacement();
            _updateCb?.Invoke();
            Dbg.Log("Stereo3D set to " + mode, Dbg.LogLevel.Info);
        }

        /// <summary>Rapporto d'aspetto dei pixel della sorgente (SAR), impostato dal player dal probe.</summary>
        public double SourceSampleAspect { get; set; } = 1;

        private bool _stereoKeepBothEyes;
        /// <summary>
        /// Schermo intero esteso (due proiettori / due monitor): il 3D non viene convertito,
        /// il fotogramma intero prende le proporzioni di due occhi affiancati (o sovrapposti)
        /// cosi' ogni occhio riempie il proprio schermo.
        /// </summary>
        public bool StereoKeepBothEyes
        {
            get => _stereoKeepBothEyes;
            set { if (_stereoKeepBothEyes == value) return; _stereoKeepBothEyes = value; InvalidateVideoPlacement(); _updateCb?.Invoke(); }
        }

        private bool CropStereo => _stereo != Stereo3DMode.None && !_stereoKeepBothEyes;

        // Proporzioni da dare all'area video: l'occhio (2D) o il fotogramma con due occhi pieni.
        private double StereoDisplayAspect(int width, int height)
        {
            double eye = StereoEyeAspect(_stereo, width, height, SourceSampleAspect);
            if (!_stereoKeepBothEyes) return eye;
            return _stereo == Stereo3DMode.SBS ? eye * 2 : eye / 2;
        }

        // Proporzioni di un solo occhio. Half-SBS/Half-TAB (es. 1920x1080 16:9) contengono
        // due occhi "schiacciati": l'occhio va mostrato con l'aspetto dell'intero frame.
        // Full-SBS (3840x1080, oppure 1920x1080 con pixel 2:1) e Full-TAB (1920x2160)
        // hanno occhi a risoluzione piena: l'aspetto e' meta' (o il doppio) del frame.
        internal static double StereoEyeAspect(Stereo3DMode mode, int width, int height, double sampleAspect)
        {
            double frame = Math.Max(1, width) * (sampleAspect > 0.1 && sampleAspect < 10 ? sampleAspect : 1) / Math.Max(1, height);
            return mode switch
            {
                Stereo3DMode.SBS => frame >= 2.6 ? frame / 2 : frame,
                Stereo3DMode.TAB => frame <= 1.2 ? frame * 2 : frame,
                _ => frame
            };
        }
        public void SetUpscaling(bool enable) { _allowUpscaling = enable; Dbg.Log("Upscaling " + (enable ? "ON" : "OFF"), Dbg.LogLevel.Info); _updateCb?.Invoke(); }

        private Action? _updateCb;
        public void BindUpdateCallback(Action? cb) => _updateCb = cb;

        public void SetInitialVideoHost(nint ownerHwnd, Rectangle ownerClient)
        {
            if (_choice != VideoRendererChoice.MPCVR) return;
            if (ownerHwnd == nint.Zero) return;

            int w = Math.Max(0, ownerClient.Width);
            int h = Math.Max(0, ownerClient.Height);
            if (w < 2 || h < 2) return;

            _initialVideoOwnerHwnd = ownerHwnd;
            _initialVideoOwnerClient = new Rectangle(0, 0, w, h);
        }

        private void TryApplyInitialMpcvrVideoHost()
        {
            if (_choice != VideoRendererChoice.MPCVR) return;
            if (!_hasVideo) return;
            if (_initialVideoOwnerHwnd == nint.Zero) return;
            if (_initialVideoOwnerClient.Width < 2 || _initialVideoOwnerClient.Height < 2) return;

            try { UpdateVideoWindow(_initialVideoOwnerHwnd, _initialVideoOwnerClient); }
            catch (Exception ex) { Dbg.Warn("MPCVR initial video host attach EX: " + ex.Message); }
        }

        /// <summary>
        /// First start after opening. The very first Run() of a graph can block for hundreds of
        /// milliseconds while the audio device opens, yet stream time starts counting at once:
        /// the video renderer finds itself late and throws away up to a second of frames to
        /// catch up (the stutter that a manual pause/resume used to cure). When that happens the
        /// graph is paused again, rewound to the start point and started a second time with the
        /// devices already warm, silently and still behind the loading mask.
        /// </summary>
        private object? _basicVolumeGraph;
        private int _basicVolumeDb;

        public void PlayFirstStart()
        {
            if (_control == null || _seek == null || !_hasVideo || _bitstreamActive || DvdMode)
            {
                Play();
                return;
            }

            long startPosition = 0;
            bool hasStart = false;
            try { hasStart = _seek.GetCurrentPosition(out startPosition) == 0; } catch { }
            int previousVolume = 0;
            bool muted = false;
            try
            {
                if (_basicAudio != null && _basicAudio.get_Volume(out previousVolume) == 0)
                    muted = _basicAudio.put_Volume(-10000) == 0;
            }
            catch { }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            long firstRunMs = -1, rewindMs = -1;
            try
            {
                int hr = _control.Run(); DsError.ThrowExceptionForHR(hr);
                firstRunMs = clock.ElapsedMilliseconds;
                if (firstRunMs >= 100 && hasStart)
                {
                    _control.Pause();
                    var start = new DsLong(startPosition);
                    _seek.SetPositions(start, AMSeekingSeekingFlags.AbsolutePositioning, null, AMSeekingSeekingFlags.NoPositioning);
                    // Attende che i renderer abbiano di nuovo il primo fotogramma in coda.
                    try { _control.GetState(1500, out _); } catch { }
                    rewindMs = clock.ElapsedMilliseconds - firstRunMs;
                }
            }
            finally
            {
                if (muted) { try { _basicAudio!.put_Volume(previousVolume); } catch { } }
            }

            clock.Restart();
            Play();
            Dbg.Log($"[VIDEO] First start: Run() blocked {firstRunMs} ms" +
                    (rewindMs >= 0 ? $", warm restart (rewind {rewindMs} ms, second Run {clock.ElapsedMilliseconds} ms)." : ", no restart needed."));
        }

        public void Play()
        {
            _clientPaused = false;
            int hr = _control?.Run() ?? 0; DsError.ThrowExceptionForHR(hr);
            _graphPlaybackStarted = true;
            try
            {
                if (_mfDisplay == null && _videoWindow == null)
                    AttachDisplayInterfaces(initial: false);
            }
            catch { }
            TryApplyInitialMpcvrVideoHost();

            try
            {
                _mfDisplay?.RepaintVideo();
                if (_choice == VideoRendererChoice.MPCVR) RequestMpcvrRedraw();
                _videoWindow?.put_Visible(OABool.True);
            }
            catch { }

            // Poll robusto per ottenere MediaType negoziato anche con renderer capricciosi
            StartMediaTypePolling();

            SafeStatus("Riproduzione.");
            _updateCb?.Invoke();
            if (_fileIsHdr) HdrTrace("Play() → Run + RepaintVideo");
            // Dopo la Run, i formati audio possono cambiare: ricontrolla e notifica (UI thread).
            // Deve essere un timer di istanza: quando si cambia file/render engine lo fermiamo prima
            // di rilasciare i COM, altrimenti un tick tardivo puo' toccare un grafo gia' smontato.
            StopAudioProbeTimer();
            _audioProbeTimer = new System.Windows.Forms.Timer { Interval = 180 };
            _audioProbeTimer.Tick += (_, __) =>
            {
                try
                {
                    StopAudioProbeTimer();
                    TryDetectBitstream(); // gira sull'UI thread (STA)
                    SafeStatus("Audio: " + (_bitstreamActive ? "Bitstream" : "PCM"));
                }
                catch { /* best-effort */ }
            };
            _audioProbeTimer.Start();

            StartAudioStartWatchdog();
        }
        public void Pause() { _clientPaused = true; StopAudioStartWatchdog(); int hr = _control?.Pause() ?? 0; DsError.ThrowExceptionForHR(hr); if (_choice == VideoRendererChoice.MPCVR) RequestMpcvrRedraw(); SafeStatus("Pausa."); if (_fileIsHdr) HdrTrace("Pause()"); }
        public void Stop() { _clientPaused = true; StopAudioStartWatchdog(); try { _control?.Stop(); } catch { } if (_choice == VideoRendererChoice.MPCVR) RequestMpcvrRedraw(); SafeStatus("Stop."); if (_fileIsHdr) HdrTrace("Stop()"); }
        public void SetVolume(float v)
        {
            if (!_bitstreamActive && _basicAudio != null)
            {
                try
                {
                    int ds = v <= 0.0001f ? -10000
                        : (int)Math.Round(Math.Clamp(20.0 * Math.Log10(v) * 100.0, -10000.0, 0.0));
                    // put_Volume waits for the audio device while it is starting (up to half a
                    // second on the UI thread): the same level is not sent twice to one graph.
                    if (!ReferenceEquals(_basicVolumeGraph, _graph) || _basicVolumeDb != ds)
                    {
                        _basicAudio.put_Volume(ds);
                        _basicVolumeGraph = _graph;
                        _basicVolumeDb = ds;
                    }
                }
                catch { }
            }
            try { CoreAudioSessionVolume.Set(v); } catch { }
        }

        private static string? StreamLanguageFromLcid(int lcid)
        {
            try { return lcid > 0 ? System.Globalization.CultureInfo.GetCultureInfo(lcid).TwoLetterISOLanguageName : null; }
            catch { return null; }
        }

        public List<DsStreamItem> EnumerateStreams()
        {
            if (DvdMode) return DvdStreams();
            var list = new List<DsStreamItem>();
            try
            {
                if (_lavSource is IAMStreamSelect sel)
                {
                    sel.Count(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        AMMediaType? mt = null;
                        object? a = null;
                        object? b = null;
                        try
                        {
                            sel.Info(i, out mt, out AMStreamSelectInfoFlags flags, out int lcid, out int group, out string name, out a, out b);
                            bool isAudio = mt?.majorType == MediaType.Audio;
                            bool isSub = mt?.majorType == MediaType.Texts || LooksLikeSubtitleName(name) || (mt != null && LooksLikeSubtitleSubtype(mt));
                            list.Add(new DsStreamItem
                            {
                                GlobalIndex = i,
                                Group = group,
                                LanguageKey = StreamLanguageFromLcid(lcid),
                                IsAudio = isAudio,
                                IsSubtitle = (isSub || IsSubtitleOffOption(name)) && !isAudio,
                                Name = name ?? "",
                                Selected = (flags & AMStreamSelectInfoFlags.Enabled) != 0
                            });
                        }
                        finally
                        {
                            if (mt != null) DsUtils.FreeAMMediaType(mt);
                            if (a != null && Marshal.IsComObject(a)) Marshal.ReleaseComObject(a);
                            if (b != null && Marshal.IsComObject(b)) Marshal.ReleaseComObject(b);
                        }
                    }
                }
            }
            catch (Exception ex) { Dbg.Warn("EnumerateStreams EX: " + ex.Message); }
            try { AppendExternalSubtitleStreams(list); }
            catch (Exception ex) { Dbg.Warn("EnumerateStreams external EX: " + ex.Message); }
            return list;

            static bool LooksLikeSubtitleName(string? n)
            {
                if (string.IsNullOrEmpty(n)) return false;
                n = n.ToLowerInvariant();
                string[] keys = { "sub", "subtitle", "srt", "ass", "ssa", "pgs", "vobsub", "hdmv", "dvb", "idx", "forced", "ita", "eng", "spa", "fra", "ger", "deu" };
                return keys.Any(k => n.Contains(k));
            }
            static bool LooksLikeSubtitleSubtype(AMMediaType mt)
            {
                try
                {
                    // true se non Audio/Video
                    if (mt.majorType != MediaType.Audio &&
                        mt.majorType != MediaType.Video &&
                        mt.majorType != Guid.Empty)
                        return true;

                    // fallback per subtype testuale/PGS/ASS/SSA/VobSub ecc.
                    var st = mt.subType.ToString().ToUpperInvariant();
                    if (st.Contains("HDMV") || st.Contains("PGS") || st.Contains("SUBPICTURE") ||
                        st.Contains("SSA") || st.Contains("ASS") || st.Contains("S_TEXT") ||
                        st.Contains("DVB") || st.Contains("VOBSUB"))
                        return true;

                    return false;
                }
                catch { return false; }
            }
        }

        public bool EnableByGlobalIndex(int globalIndex)
        {
            if (DvdMode) return DvdEnableStream(globalIndex);
            try
            {
                if (globalIndex >= ExternalSubtitleIndexBase)
                    return SelectExternalSubtitle(globalIndex - ExternalSubtitleIndexBase);
                if (_lavSource is IAMStreamSelect sel)
                {
                    sel.Enable(globalIndex, AMStreamSelectEnableFlags.Enable);
                    // Scelta una traccia interna dei sottotitoli: il filtro torna a mostrare quella.
                    if (_externalSubtitleSelected && IsLavSubtitleStream(sel, globalIndex)) DeselectExternalSubtitle();
                    _updateCb?.Invoke();
                    Dbg.Log("Stream abilitato idx=" + globalIndex, Dbg.LogLevel.Verbose);
                    return true;
                }
            }
            catch (Exception ex) { SafeStatus("IAMStreamSelect: " + ex.Message); Dbg.Warn("EnableByGlobalIndex EX: " + ex); }
            return false;
        }

        private static bool IsLavSubtitleStream(IAMStreamSelect selector, int index)
        {
            AMMediaType? mediaType = null;
            object? first = null, second = null;
            try
            {
                if (selector.Info(index, out mediaType, out _, out _, out int group, out string name, out first, out second) < 0) return false;
                // LAV: gruppo 0 video, 1 audio, 2 sottotitoli (compresa la voce "No subtitles").
                return group == 2 || IsSubtitleOffOption(name);
            }
            catch { return false; }
            finally
            {
                if (mediaType != null) DsUtils.FreeAMMediaType(mediaType);
                if (first != null && Marshal.IsComObject(first)) Marshal.ReleaseComObject(first);
                if (second != null && Marshal.IsComObject(second)) Marshal.ReleaseComObject(second);
            }
        }

        public bool DisableSubtitlesIfPossible()
        {
            if (DvdMode) return DvdSubtitlesOff();
            // Con un sottotitolo esterno attivo, "spento" vale anche per lui.
            bool externalOff = false;
            try { externalOff = DeselectExternalSubtitle(); } catch { }
            try
            {
                if (_lavSource is not IAMStreamSelect selector) return externalOff;
                selector.Count(out int count);
                for (int i = 0; i < count; i++)
                {
                    AMMediaType? mediaType = null;
                    object? first = null, second = null;
                    try
                    {
                        int info = selector.Info(i, out mediaType, out _, out _, out _, out string name, out first, out second);
                        if (info < 0 || !IsSubtitleOffOption(name)) continue;
                        // LAV's dedicated No subtitles entry can have no media type.
                        // Auto Forced is an active subtitle stream, never the Off entry.
                        int result = selector.Enable(i, AMStreamSelectEnableFlags.Enable);
                        if (result < 0) continue;
                        _updateCb?.Invoke();
                        return true;
                    }
                    finally
                    {
                        if (mediaType != null) DsUtils.FreeAMMediaType(mediaType);
                        if (first != null && Marshal.IsComObject(first)) Marshal.ReleaseComObject(first);
                        if (second != null && Marshal.IsComObject(second)) Marshal.ReleaseComObject(second);
                    }
                }
            }
            catch (Exception ex) { Dbg.Warn("DisableSubtitlesIfPossible: " + ex.Message); }
            return externalOff;
        }

        // LAV Splitter names its entry "S: No subtitles".
        internal static bool IsSubtitleOffOption(string? name) =>
            !string.IsNullOrWhiteSpace(name) && System.Text.RegularExpressions.Regex.IsMatch(name.Trim(),
                @"^(?:S:\s*)?(?:\[|\()?\s*(?:no subtitles?|subtitles? off|off|none|disabled|nessun[oi] sottotitol[oi]|sottotitoli disattivati|disattivati)\s*(?:\]|\))?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public bool IsPrerollReady
        {
            get
            {
                try { return _control != null && _control.GetState(0, out var state) == 0 && state == FilterState.Paused; }
                catch { return false; }
            }
        }

        private void StartTimer()
        {
            StopTimer();
            long lastSlowLog = 0;
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += (_, __) =>
            {
                try
                {
                    if (_seek == null || OnProgressSeconds == null) return;
                    long started = Stopwatch.GetTimestamp();
                    _seek.GetCurrentPosition(out long pos);
                    // Un DVD ha il suo tempo (quello del titolo), non quello del grafo.
                    if (DvdMode) pos = (long)(DvdPosition() * 10_000_000.0);
                    double pollMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    started = Stopwatch.GetTimestamp();
                    OnProgressSeconds(pos / 10_000_000.0);
                    double callbackMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    long now = Environment.TickCount64;
                    if (_choice == VideoRendererChoice.MADVR && !_clientPaused &&
                        (pollMs > 30 || callbackMs > 30) && now - lastSlowLog > 2000)
                    {
                        lastSlowLog = now;
                        Dbg.Warn($"[VIDEO-PERF] Slow progress callback: graphPollMs={pollMs:0.0}, eventDispatchMs={callbackMs:0.0}.");
                    }
                }
                catch { }
            };
            _timer.Start();
        }
        private void StopTimer()
        {
            if (_timer == null) return;
            _timer.Stop(); _timer.Dispose(); _timer = null;
        }

        public void Dispose()
        {
            StopTimer();
            StopMediaTypePolling();
            StopAudioProbeTimer();
            StopAudioStartWatchdog();
            DisposeGraph(releaseAudioEndpoint: true);
        }

        public void DisposeForAppExit()
        {
            _fastAppExitDispose = true;
            Dispose();
        }
        private void SaveResumePointIfNeeded()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentMediaPath))
                    return;

                // Se Open() e' fallito prima del Play() (es. MPCVR E_ABORT), non toccare
                // la posizione salvata del file precedente/attuale: pos=0 + dur valida
                // cancellava il resume anche se il file non era mai partito.
                if (!_graphOpenCompleted || !_graphPlaybackStarted)
                    return;

                double dur = DurationSeconds;
                double pos = PositionSeconds;

                PlaybackResumeStore.SaveOrClear(_currentMediaPath, pos, dur);
            }
            catch (Exception ex)
            {
                Dbg.Warn("SaveResumePointIfNeeded EX: " + ex.Message);
            }
        }
        private void DisposeGraph(bool releaseAudioEndpoint = false)
        {
            try { _disabledTrackView?.Dispose(); } catch { }
            _disabledTrackView = null;
            InvalidateVideoPlacement();
            Dbg.Log("DisposeGraph()", Dbg.LogLevel.Verbose);

            StopMediaTypePolling();
            StopAudioProbeTimer();
            StopAudioStartWatchdog();

            RestoreMadVrCadenceSettings();

            // NEW: salva eventuale punto di ripresa prima di smontare il grafo
            SaveResumePointIfNeeded();

            bool fastExit = _fastAppExitDispose;
            bool wasBitstreamLike = _bitstreamActive || _preferBitstream;
            string? releasePulsePreferredName = _preferredAudioRendererName;
            string? releasePulseNegotiatedName = _audioRendererDeviceName;
            bool rendererSensitive = _choice == VideoRendererChoice.MPCVR || _choice == VideoRendererChoice.MADVR || _videoRenderer != null || _videoWindow != null || _mfDisplay != null;
            if (!fastExit)
            {
                try { _control?.StopWhenReady(); } catch { }
            }
            try { _control?.Stop(); } catch { }

            // Bitstream/IEC61937: fra due elementi consecutivi della coda l'AVR deve vedere
            // una disconnessione reale del ramo audio, non solo un nuovo file che parte subito.
            // Se non stacchiamo i pin e non lasciamo un piccolo drain, alcuni AVR restano in
            // uno stato DTS:X/Atmos instabile dal secondo elemento in poi.
            if (wasBitstreamLike)
            {
                Dbg.Log($"[AUDIO] bitstream teardown: disconnect graph pins, preferred='{releasePulsePreferredName ?? "null"}', negotiated='{releasePulseNegotiatedName ?? "null"}'", Dbg.LogLevel.Info);
                try { DisconnectFilterPins(_audioRenderer); } catch { }
                try { DisconnectFilterPins(_lavAudio); } catch { }
                try { DisconnectFilterPins(_lavSourceAudio); } catch { }
                try { DisconnectFilterPins(_lavSource); } catch { }
                if (!fastExit)
                {
                    try { Thread.Sleep(650); } catch { }
                }
            }

            // Non forzare il vecchio grafo a -10000 dB: alcuni audio renderer/CoreAudio
            // mantengono quello stato anche quando il file successivo della coda crea un
            // nuovo grafo, lasciando muto tutto cio' che viene dopo il primo elemento.
            _bitstreamActive = false;
            try { OnBitstreamChanged?.Invoke(false); } catch { }

            try { _videoWindow?.put_Visible(OABool.False); } catch { }
            try { _videoWindow?.put_Owner(IntPtr.Zero); } catch { }
            try { _videoWindow?.put_MessageDrain(IntPtr.Zero); } catch { }
            try { _mfDisplay?.SetVideoWindow(IntPtr.Zero); } catch { }
            try { _mpcvrMixerOverlay?.Dispose(); } catch { }

            try { RemoveFilterIfPresent(_audioRenderer); } catch { }
            try { RemoveFilterIfPresent(_lavAudio); } catch { }
            try { RemoveFilterIfPresent(_lavSourceAudio); } catch { }
            try { RemoveFilterIfPresent(_xySubFilter); } catch { }
            try { RemoveFilterIfPresent(_videoRenderer); } catch { }
            try { RemoveFilterIfPresent(_lavVideo); } catch { }
            try { RemoveFilterIfPresent(_lavSource); } catch { }

            ReleaseCom(ref _mfDisplay);
            ReleaseCom(ref _videoWindow);
            ReleaseDvd();
            ReleaseCom(ref _lavSource);
            ReleaseCom(ref _lavSourceAudio);
            ReleaseCom(ref _lavVideo);
            ReleaseCom(ref _lavAudio);
            _madVrArOverride = 0;
            DestroyStereoClipWindow();
            if (ReferenceEquals(Utilities.MadVrSettingsStore.LiveRenderer, _videoRenderer))
                Utilities.MadVrSettingsStore.LiveRenderer = null;
            ReleaseCom(ref _videoRenderer);
            ReleaseCom(ref _audioRenderer);
            ReleaseCom(ref _xySubFilter);
            ReleaseCom(ref _seek);
            ReleaseCom(ref _basicAudio);
            ReleaseCom(ref _control);
            ReleaseCom(ref _graph);
            _mpcvrMixerOverlay = null;
            _mpcvrMixerBitmap = null;
            _mpcvrMixerBitmapAttachAttempted = false;
            _mpcvrMixerBitmapLogCount = 0;
            try { _mpcvrRegistryOverride?.Dispose(); } catch { }
            _mpcvrRegistryOverride = null;
            _externalAudioUrl = null;
            _lastOwnerHwnd = nint.Zero;
            _madvrHwnd = nint.Zero;
            _audioRendererDeviceName = "?";
            try { CoreAudioSessionVolume.Reset(); } catch { }

            try { _thumb?.Dispose(); } catch { }
            _thumb = null;
            _currentMediaPath = null;
            _graphOpenCompleted = false;
            _graphPlaybackStarted = false;

            if (rendererSensitive)
                PrepareComCreateRetry(0);

            if (releaseAudioEndpoint && wasBitstreamLike && !fastExit)
            {
                BitstreamReleasePulse.Pulse(releasePulsePreferredName, releasePulseNegotiatedName);
                try { Thread.Sleep(350); } catch { }
                try { CoreAudioSessionVolume.Reset(); } catch { }
                Dbg.Log("[AUDIO] bitstream endpoint release settle complete", Dbg.LogLevel.Info);
            }
        }

        private void RemoveFilterIfPresent(IBaseFilter? filter)
        {
            if (_graph == null || filter == null)
                return;

            try { DisconnectFilterPins(filter); } catch { }
            try { _graph.RemoveFilter(filter); } catch { }
        }

        private void DisconnectFilterPins(IBaseFilter? filter)
        {
            if (_graph == null || filter == null)
                return;

            IEnumPins? enumPins = null;
            try
            {
                filter.EnumPins(out enumPins);
                if (enumPins == null) return;

                var pins = new IPin[1];
                while (enumPins.Next(1, pins, nint.Zero) == 0)
                {
                    var pin = pins[0];
                    pins[0] = null!;
                    try
                    {
                        pin.ConnectedTo(out var other);
                        if (other != null)
                        {
                            try { _graph.Disconnect(pin); } catch { }
                            try { _graph.Disconnect(other); } catch { }
                            try { Marshal.ReleaseComObject(other); } catch { }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { Marshal.ReleaseComObject(pin); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                if (enumPins != null)
                    try { Marshal.ReleaseComObject(enumPins); } catch { }
            }
        }

        private void StopAudioProbeTimer()
        {
            try
            {
                if (_audioProbeTimer != null)
                {
                    _audioProbeTimer.Stop();
                    _audioProbeTimer.Dispose();
                    _audioProbeTimer = null;
                }
            }
            catch { _audioProbeTimer = null; }
        }

        private void StartAudioStartWatchdog()
        {
            StopAudioStartWatchdog();

            if (!_audioPathConnected || _control == null)
                return;

            // madVR e' molto sensibile ai Run() ripetuti durante il preroll: il grafo
            // puo' sembrare andare avanti e tornare indietro mentre il renderer negozia.
            // L'audio viene gia' ricontrollato dall'audio probe, quindi qui restiamo fermi.
            if (_choice == VideoRendererChoice.MADVR)
                return;

            _audioStartWatchdogTick = 0;
            _audioStartWatchdogTimer = new System.Windows.Forms.Timer { Interval = 420 };
            _audioStartWatchdogTimer.Tick += (_, __) =>
            {
                try
                {
                    if (_clientPaused)
                    {
                        StopAudioStartWatchdog();
                        return;
                    }

                    _audioStartWatchdogTick++;

                    string stateText = "unknown";
                    bool graphAlreadyRunning = false;
                    try
                    {
                        FilterState state = default;
                        int hrState = _control != null ? _control.GetState(0, out state) : unchecked((int)0x80004005);
                        stateText = hrState == 0 ? state.ToString() : $"hr=0x{hrState:X8}";
                        graphAlreadyRunning = hrState == 0 && state == FilterState.Running;
                    }
                    catch (Exception ex)
                    {
                        stateText = "EX:" + ex.Message;
                    }

                    Dbg.Log($"[AUDIO] start watchdog tick={_audioStartWatchdogTick}, state={stateText}, renderer='{_audioRendererName}', device='{_audioRendererDeviceName}', bitstream={_bitstreamActive}, preferBitstream={_preferBitstream}, forcePcm={_forcePcmToggle}", Dbg.LogLevel.Info);

                    try
                    {
                        if (!graphAlreadyRunning)
                        {
                            int hrRun = _control?.Run() ?? 0;
                            if (hrRun != 0)
                                Dbg.Warn($"[AUDIO] start watchdog Run() nudge hr=0x{hrRun:X8}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn("[AUDIO] start watchdog Run() nudge EX: " + ex.Message);
                    }

                    try { TryDetectBitstream(); } catch { }

                    if (_audioStartWatchdogTick >= 3)
                    {
                        StopAudioStartWatchdog();
                    }
                    else if (_audioStartWatchdogTimer != null)
                    {
                        _audioStartWatchdogTimer.Interval = _audioStartWatchdogTick == 1 ? 850 : 1400;
                    }
                }
                catch (Exception ex)
                {
                    Dbg.Warn("[AUDIO] start watchdog EX: " + ex.Message);
                    StopAudioStartWatchdog();
                }
            };
            _audioStartWatchdogTimer.Start();
        }

        private void StopAudioStartWatchdog()
        {
            try
            {
                if (_audioStartWatchdogTimer != null)
                {
                    _audioStartWatchdogTimer.Stop();
                    _audioStartWatchdogTimer.Dispose();
                    _audioStartWatchdogTimer = null;
                }
            }
            catch { _audioStartWatchdogTimer = null; }
            _audioStartWatchdogTick = 0;
        }

        private void StopMediaTypePolling()
        {
            try { _mtPollCts?.Cancel(); } catch { }
            _mtPollCts = null;
            try
            {
                if (_mtPollTimer != null)
                {
                    _mtPollTimer.Stop();
                    _mtPollTimer.Dispose();
                    _mtPollTimer = null;
                }
            }
            catch { _mtPollTimer = null; }
            _mtPollTickCount = 0;
        }

        private static void ReleaseCom<T>(ref T? obj) where T : class
        {
            if (obj == null)
                return;

            try
            {
                if (Marshal.IsComObject(obj))
                {
                    // Non usare FinalReleaseComObject/loop aggressivi: IGraphBuilder, IMediaControl,
                    // IMediaSeeking e IBasicAudio possono condividere lo stesso RCW. Final-release su
                    // uno di questi mentre timer/UI stanno ancora drenando il cambio renderer puo'
                    // invalidare RCW fratelli e, con alcuni filtri video, portare a crash nativi.
                    try { Marshal.ReleaseComObject(obj); } catch { }
                }
                else if (obj is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch { }
            finally
            {
                obj = null;
            }
        }

        private static readonly Guid CLSID_MPCVR = new("71F080AA-8661-4093-B15E-4F6903E77D0A");
        private static readonly Guid CLSID_MADVR = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");
        private const int ComCreateMaxAttempts = 5;
        private static readonly object MpcvrCreateSync = new();
        private static readonly object MadVrCreateSync = new();

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;
        private static IntPtr _mpcvrModuleHandle = IntPtr.Zero;
        private static IntPtr _madVrModuleHandle = IntPtr.Zero;

        [DllImport("ole32.dll")]
        private static extern void CoFreeUnusedLibraries();

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DllGetClassObjectDelegate(ref Guid rclsid, ref Guid riid, out IntPtr ppv);

        [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IClassFactory
        {
            [PreserveSig]
            int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

            [PreserveSig]
            int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
        }

        [ComImport, Guid("37CBDF10-D65E-4E5A-8F37-40E0C8EA1695"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IExFilterConfig
        {
            [PreserveSig] int Flt_GetBool([MarshalAs(UnmanagedType.LPStr)] string field, [MarshalAs(UnmanagedType.I1)] out bool value);
            [PreserveSig] int Flt_GetInt([MarshalAs(UnmanagedType.LPStr)] string field, out int value);
            [PreserveSig] int Flt_GetInt64([MarshalAs(UnmanagedType.LPStr)] string field, out long value);
            [PreserveSig] int Flt_GetDouble([MarshalAs(UnmanagedType.LPStr)] string field, out double value);
            [PreserveSig] int Flt_GetString([MarshalAs(UnmanagedType.LPStr)] string field, out IntPtr value, out uint chars);
            [PreserveSig] int Flt_GetBin([MarshalAs(UnmanagedType.LPStr)] string field, out IntPtr value, out uint size);
            [PreserveSig] int Flt_SetBool([MarshalAs(UnmanagedType.LPStr)] string field, [MarshalAs(UnmanagedType.I1)] bool value);
            [PreserveSig] int Flt_SetInt([MarshalAs(UnmanagedType.LPStr)] string field, int value);
            [PreserveSig] int Flt_SetInt64([MarshalAs(UnmanagedType.LPStr)] string field, long value);
            [PreserveSig] int Flt_SetDouble([MarshalAs(UnmanagedType.LPStr)] string field, double value);
            [PreserveSig] int Flt_SetString([MarshalAs(UnmanagedType.LPStr)] string field, [MarshalAs(UnmanagedType.LPWStr)] string value, int chars);
            [PreserveSig] int Flt_SetBin([MarshalAs(UnmanagedType.LPStr)] string field, IntPtr value, int size);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpcvrVpFormats
        {
            [MarshalAs(UnmanagedType.I1)] public bool Nv12;
            [MarshalAs(UnmanagedType.I1)] public bool P01x;
            [MarshalAs(UnmanagedType.I1)] public bool Yuy2;
            [MarshalAs(UnmanagedType.I1)] public bool Other;
        }

        // ABI ufficiale di MPC Video Renderer (IVideoRenderer.h). Usare questa
        // interfaccia consente di applicare le opzioni al filtro vivo e rileggerle,
        // invece di dedurre il successo dalla sola scrittura nel registro.
        [StructLayout(LayoutKind.Sequential)]
        private struct MpcvrSettings
        {
            [MarshalAs(UnmanagedType.I1)] public bool UseD3D11;
            [MarshalAs(UnmanagedType.I1)] public bool ShowStats;
            public int ResizeStats;
            public int TextureFormat;
            public MpcvrVpFormats VpFormats;
            public int VpDeinterlacing;
            [MarshalAs(UnmanagedType.I1)] public bool DeintDouble;
            [MarshalAs(UnmanagedType.I1)] public bool VpScaling;
            public int VpSuperResolution;
            [MarshalAs(UnmanagedType.I1)] public bool VpRtxVideoHdr;
            public int ChromaScaling;
            public int Upscaling;
            public int Downscaling;
            [MarshalAs(UnmanagedType.I1)] public bool InterpolateAt50Pct;
            [MarshalAs(UnmanagedType.I1)] public bool UseDither;
            [MarshalAs(UnmanagedType.I1)] public bool DeintBlend;
            public int SwapEffect;
            [MarshalAs(UnmanagedType.I1)] public bool ExclusiveFs;
            [MarshalAs(UnmanagedType.I1)] public bool VBlankBeforePresent;
            [MarshalAs(UnmanagedType.I1)] public bool AdjustPresentTime;
            [MarshalAs(UnmanagedType.I1)] public bool ReinitByDisplay;
            [MarshalAs(UnmanagedType.I1)] public bool HdrPreferDovi;
            [MarshalAs(UnmanagedType.I1)] public bool HdrPassthrough;
            public int HdrToggleDisplay;
            public int HdrOsdBrightness;
            [MarshalAs(UnmanagedType.I1)] public bool ConvertToSdr;
            public int SdrDisplayNits;
            [MarshalAs(UnmanagedType.I1)] public bool HdrLocalToneMapping;
            public int HdrLocalToneMappingType;
            public int HdrDisplayMaxNits;
        }

        [ComImport, Guid("1AB00F10-5F55-42AC-B53F-38649F11BE3E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMpcVideoRendererSettings
        {
            [PreserveSig] int GetVideoProcessorInfo(IntPtr value);
            [PreserveSig]
            [return: MarshalAs(UnmanagedType.I1)]
            bool GetActive();
            [PreserveSig] void GetSettings(out MpcvrSettings settings);
            [PreserveSig] void SetSettings(ref MpcvrSettings settings);
            [PreserveSig] int SaveSettings();
        }

        [ComImport, Guid("FA40D6E9-4D38-4761-ADD2-71A9EC5FD32F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ILAVVideoSettings
        {
            [PreserveSig] int SetRuntimeConfig([MarshalAs(UnmanagedType.Bool)] bool bRuntimeConfig);
            [PreserveSig] int GetFormatConfiguration(int vCodec);
            [PreserveSig] int SetFormatConfiguration(int vCodec, [MarshalAs(UnmanagedType.Bool)] bool bEnabled);
            [PreserveSig] int SetNumThreads(uint dwNum);
            [PreserveSig] uint GetNumThreads();
            [PreserveSig] int SetStreamAR(uint bStreamAR);
            [PreserveSig] uint GetStreamAR();
            [PreserveSig] int GetPixelFormat(int pixFmt);
            [PreserveSig] int SetPixelFormat(int pixFmt, [MarshalAs(UnmanagedType.Bool)] bool bEnabled);
            [PreserveSig] int SetRGBOutputRange(uint dwRange);
            [PreserveSig] uint GetRGBOutputRange();
            [PreserveSig] int SetDeintFieldOrder(int fieldOrder);
            [PreserveSig] int GetDeintFieldOrder();
            [PreserveSig] int SetDeintAggressive([MarshalAs(UnmanagedType.Bool)] bool bAggressive);
            [PreserveSig] int GetDeintAggressive();
            [PreserveSig] int SetDeintForce([MarshalAs(UnmanagedType.Bool)] bool bForce);
            [PreserveSig] int GetDeintForce();
            [PreserveSig] uint CheckHWAccelSupport(int hwAccel);
            [PreserveSig] int SetHWAccel(int hwAccel);
            [PreserveSig] int GetHWAccel();
            [PreserveSig] int SetHWAccelCodec(int hwAccelCodec, [MarshalAs(UnmanagedType.Bool)] bool bEnabled);
            [PreserveSig] int GetHWAccelCodec(int hwAccelCodec);
        }

        // Con la runtime config LAV riparte dai valori predefiniti e ignora il registro:
        // riportiamo le scelte fatte nelle impostazioni del player (pagina LAV Video).
        private static void ApplyLavVideoRegistryToRuntime(ILAVVideoSettings lav)
        {
            try
            {
                using var video = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\LAV\Video");
                using var hw = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\LAV\Video\HWAccel");
                int Value(Microsoft.Win32.RegistryKey? key, string name, int fallback) => key?.GetValue(name) is int v ? v : fallback;
                int accel = Value(hw, "HWAccel", (int)LavHwAccel.D3D11);
                lav.SetHWAccel(accel);
                foreach (var (name, codec) in new[] { ("h264", 0), ("vc1", 1), ("mpeg2", 2), ("hevc", 5), ("vp9", 6), ("av1", 8) })
                    lav.SetHWAccelCodec(codec, Value(hw, name, 1) != 0);
                lav.SetNumThreads((uint)Math.Max(0, Value(video, "NumThreads", 0)));
                lav.SetRGBOutputRange((uint)Math.Clamp(Value(video, "RGBRange", 0), 0, 2));
                int deint = Value(video, "DeintMode", 0);
                lav.SetDeintAggressive(deint == 1);
                lav.SetDeintForce(deint == 2);
                Dbg.Log($"LAV Video runtime config from player settings: hwAccel={accel}, deint={deint}", Dbg.LogLevel.Info);
            }
            catch (Exception ex) { Dbg.Warn("LAV Video registry → runtime: " + ex.Message); }
        }

        private enum LavHwAccel
        {
            None = 0,
            D3D11 = 5
        }

        private enum LavOutPixFmt
        {
            YV12 = 0,
            NV12 = 1,
            YUY2 = 2,
            P010 = 5,
            P016 = 8,
            RGB32 = 11
        }

        private static IExFilterConfig? TryGetMpcvrConfigFrom(object? target)
        {
            if (target == null)
                return null;

            try { return (IExFilterConfig)target; }
            catch { return null; }
        }

        private IExFilterConfig? TryGetMpcvrConfig()
        {
            if (_choice != VideoRendererChoice.MPCVR)
                return null;

            return TryGetMpcvrConfigFrom(_videoRenderer);
        }

        private static int MpcvrSetBool(IExFilterConfig config, string field, bool value)
        {
            try { return config.Flt_SetBool(field, value); }
            catch { return unchecked((int)0x80004005); }
        }

        private static string ReadMpcvrInt64(IExFilterConfig config, string field)
        {
            try
            {
                int hr = config.Flt_GetInt64(field, out long value);
                return hr == 0 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"n/d(hr={HrText(hr)})";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        private static string ReadMpcvrInt(IExFilterConfig config, string field)
        {
            try
            {
                int hr = config.Flt_GetInt(field, out int value);
                return hr == 0 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"n/d(hr={HrText(hr)})";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        private static string ReadMpcvrBool(IExFilterConfig config, string field)
        {
            try
            {
                int hr = config.Flt_GetBool(field, out bool value);
                return hr == 0 ? value.ToString() : $"n/d(hr={HrText(hr)})";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        private void ApplyMpcvrFilterConfig(IBaseFilter renderer)
        {
            var config = TryGetMpcvrConfigFrom(renderer);
            if (config == null)
            {
                Dbg.Warn("MPCVR IExFilterConfig non disponibile: provo comunque IVideoRenderer.");
            }
            else
            {
                int hrLessRedraws = MpcvrSetBool(config, "lessRedraws", true);
                int hrFullscreen = MpcvrSetBool(config, "d3dFullscreenControl", false);
                int hrDeepColor = MpcvrSetBool(config, "allowDeepColorBitmaps", true);

                Dbg.Log(
                    "MPCVR IExFilterConfig: " +
                    $"version={ReadMpcvrInt64(config, "version")}, " +
                    $"lessRedraws=0x{hrLessRedraws:X8}, " +
                    $"d3dFullscreenControl(false)=0x{hrFullscreen:X8}, " +
                    $"allowDeepColorBitmaps=0x{hrDeepColor:X8}",
                    Dbg.LogLevel.Info);
            }

            ApplyAndVerifyMpcvrVideoSettings(renderer);
        }

        private void ApplyAndVerifyMpcvrVideoSettings(IBaseFilter renderer)
        {
            int requestedSuperResolution = Math.Clamp(_mpcvrSuperResolutionMode, 0, 4);
            bool wantsSuperResolution = requestedSuperResolution > 0;
            bool needsVideoProcessor = wantsSuperResolution || _enableMpcvrRtxVideoHdr;

            // A plain MPCVR session should use its own known-good video processor
            // defaults. Rewriting the full COM settings struct during graph creation
            // can reinitialize the presenter before its input pin is connected.
            if (!needsVideoProcessor)
            {
                _mpcvrUpscalingStatus = "off";
                return;
            }

            try
            {
                var rendererSettings = (IMpcVideoRendererSettings)renderer;
                rendererSettings.GetSettings(out MpcvrSettings settings);
                settings.UseD3D11 = needsVideoProcessor || settings.UseD3D11;
                settings.TextureFormat = _enableMpcvrRtxVideoHdr ? 0 : settings.TextureFormat;
                settings.VpFormats.Nv12 = needsVideoProcessor || settings.VpFormats.Nv12;
                settings.VpFormats.P01x = needsVideoProcessor || settings.VpFormats.P01x;
                settings.VpFormats.Yuy2 = needsVideoProcessor || settings.VpFormats.Yuy2;
                settings.VpFormats.Other = needsVideoProcessor || settings.VpFormats.Other;
                settings.VpScaling = wantsSuperResolution;
                settings.VpSuperResolution = requestedSuperResolution;
                settings.VpRtxVideoHdr = _enableMpcvrRtxVideoHdr;
                settings.ExclusiveFs = false;
                settings.HdrToggleDisplay = 0;

                rendererSettings.SetSettings(ref settings);
                rendererSettings.GetSettings(out MpcvrSettings applied);

                bool verified = applied.UseD3D11 == settings.UseD3D11
                    && applied.VpScaling == wantsSuperResolution
                    && applied.VpSuperResolution == requestedSuperResolution
                    && applied.VpRtxVideoHdr == _enableMpcvrRtxVideoHdr;
                _mpcvrUpscalingStatus = verified
                    ? $"verified(sr={applied.VpSuperResolution},vp={applied.VpScaling},d3d11={applied.UseD3D11},rtxHdr={applied.VpRtxVideoHdr})"
                    : $"readback-mismatch(requested={requestedSuperResolution},actual={applied.VpSuperResolution},vp={applied.VpScaling},d3d11={applied.UseD3D11})";

                if (verified)
                    Dbg.Log("[VIDEO] MPCVR Super Resolution verificata tramite IVideoRenderer: " + _mpcvrUpscalingStatus, Dbg.LogLevel.Info);
                else
                    Dbg.Warn("[VIDEO] MPCVR Super Resolution non confermata dal renderer: " + _mpcvrUpscalingStatus);
            }
            catch (Exception ex)
            {
                _mpcvrUpscalingStatus = "registry-fallback:" + ex.GetType().Name;
                Dbg.Warn("[VIDEO] MPCVR IVideoRenderer settings non disponibile; resta attivo il fallback registro: " + ex.Message);
            }
        }

        private bool RequestMpcvrRedraw()
        {
            var config = TryGetMpcvrConfig();
            if (config == null)
                return false;

            int hr = MpcvrSetBool(config, "cmd_redraw", true);
            if (hr != 0)
            {
                Dbg.Warn($"MPCVR cmd_redraw failed hr={HrText(hr)}");
                return false;
            }

            return true;
        }

        private IMFVideoMixerBitmap? TryGetMpcvrMixerBitmap()
        {
            if (_choice != VideoRendererChoice.MPCVR)
                return null;

            if (_mpcvrMixerBitmap != null)
                return _mpcvrMixerBitmap;

            if (_videoRenderer == null)
                return null;

            if (_mpcvrMixerBitmapAttachAttempted)
                return null;

            _mpcvrMixerBitmapAttachAttempted = true;

            try
            {
                if (_videoRenderer is IMFVideoMixerBitmap direct)
                {
                    _mpcvrMixerBitmap = direct;
                    Dbg.Log("[VIDEO] MPCVR IMFVideoMixerBitmap attached directly from renderer.", Dbg.LogLevel.Info);
                    return _mpcvrMixerBitmap;
                }
            }
            catch { }

            try
            {
                if (_videoRenderer is IMFGetService rendererService)
                {
                    int hr = rendererService.GetService(MR_VIDEO_RENDER_SERVICE, typeof(IMFVideoMixerBitmap).GUID, out object obj);
                    if (hr == 0 && obj is IMFVideoMixerBitmap serviceBitmap)
                    {
                        _mpcvrMixerBitmap = serviceBitmap;
                        Dbg.Log("[VIDEO] MPCVR IMFVideoMixerBitmap attached through renderer service.", Dbg.LogLevel.Info);
                        return _mpcvrMixerBitmap;
                    }
                }
            }
            catch { }

            try
            {
                var inPin = _videoRenderer != null ? FindPin(_videoRenderer, PinDirection.Input, null) : null;
                try
                {
                    if (inPin is IMFGetService pinService)
                    {
                        int hr = pinService.GetService(MR_VIDEO_RENDER_SERVICE, typeof(IMFVideoMixerBitmap).GUID, out object obj);
                        if (hr == 0 && obj is IMFVideoMixerBitmap pinBitmap)
                        {
                            _mpcvrMixerBitmap = pinBitmap;
                            Dbg.Log("[VIDEO] MPCVR IMFVideoMixerBitmap attached through input pin service.", Dbg.LogLevel.Info);
                            return _mpcvrMixerBitmap;
                        }
                    }
                }
                finally
                {
                    try { if (inPin != null && Marshal.IsComObject(inPin)) Marshal.ReleaseComObject(inPin); } catch { }
                }
            }
            catch { }

            try
            {
                if (_graph != null)
                {
                    var builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
                    try
                    {
                        int hrSet = builder.SetFiltergraph(_graph);
                        Guid iid = typeof(IMFVideoMixerBitmap).GUID;
                        int hr = builder.FindInterface(DsGuid.Empty, DsGuid.Empty, _videoRenderer, iid, out object obj);
                        if (hr == 0 && obj is IMFVideoMixerBitmap graphBitmap)
                        {
                            _mpcvrMixerBitmap = graphBitmap;
                            Dbg.Log("[VIDEO] MPCVR IMFVideoMixerBitmap attached through CaptureGraphBuilder renderer search.", Dbg.LogLevel.Info);
                            return _mpcvrMixerBitmap;
                        }

                        hr = builder.FindInterface(DsGuid.Empty, new DsGuid(MediaType.Video), null!, iid, out obj);
                        if (hr == 0 && obj is IMFVideoMixerBitmap graphVideoBitmap)
                        {
                            _mpcvrMixerBitmap = graphVideoBitmap;
                            Dbg.Log("[VIDEO] MPCVR IMFVideoMixerBitmap attached through CaptureGraphBuilder graph video search.", Dbg.LogLevel.Info);
                            return _mpcvrMixerBitmap;
                        }

                        if (_mpcvrMixerBitmapLogCount < 4)
                        {
                            _mpcvrMixerBitmapLogCount++;
                            Dbg.Warn($"[VIDEO] MPCVR IMFVideoMixerBitmap CaptureGraphBuilder unavailable set=0x{hrSet:X8}, find=0x{hr:X8}.");
                        }
                    }
                    finally
                    {
                        try { if (Marshal.IsComObject(builder)) Marshal.ReleaseComObject(builder); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_mpcvrMixerBitmapLogCount < 4)
                {
                    _mpcvrMixerBitmapLogCount++;
                    Dbg.Warn("[VIDEO] MPCVR IMFVideoMixerBitmap CaptureGraphBuilder EX: " + ex.Message);
                }
            }

            Dbg.Warn("[VIDEO] MPCVR IMFVideoMixerBitmap unavailable; using only HWND overlay fallback.");
            return null;
        }

        public bool TrySetMpcvrOverlayBitmap(Bitmap? bitmap, Color transparentKey)
        {
            if (_choice != VideoRendererChoice.MPCVR)
                return false;

            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                ClearMpcvrOverlayBitmap();
                return true;
            }

            var mixer = TryGetMpcvrMixerBitmap();
            if (mixer == null)
                return false;

            _mpcvrMixerOverlay ??= new MpcvrMixerBitmapOverlay(mixer);
            bool ok = _mpcvrMixerOverlay.SetBitmap(bitmap, transparentKey);
            if (ok)
            {
                RequestMpcvrRedraw();
            }
            else if (_mpcvrMixerBitmapLogCount < 4)
            {
                _mpcvrMixerBitmapLogCount++;
                Dbg.Warn("[VIDEO] MPCVR mixer bitmap update failed.");
            }

            return ok;
        }

        public void ClearMpcvrOverlayBitmap()
        {
            if (_choice != VideoRendererChoice.MPCVR)
                return;

            try { _mpcvrMixerOverlay?.Clear(); } catch { }
            try { RequestMpcvrRedraw(); } catch { }
        }

        private static readonly Guid MR_VIDEO_RENDER_SERVICE = new("1092A86C-AB1A-459A-A336-831FBC4D11FF");

        private void AttachDisplayInterfaces(bool initial)
        {
            InvalidateVideoPlacement();
            if (!_hasVideo)
            {
                Dbg.Log("AttachDisplayInterfaces: skip (no video).", Dbg.LogLevel.Verbose);
                return;
            }

            bool preserveWindowedRenderer = !initial && _videoWindow != null;

            if (_choice == VideoRendererChoice.MPCVR)
            {
                if (TryAttachVideoWindowFromRenderer(initial, preserveWindowedRenderer))
                    return;

                // Recupero solo se una build espone davvero il servizio windowless.
                // Il percorso primario di MPCVR, come in MPC-BE, resta IVideoWindow.
                TryAttachMfDisplayControl(initial);
                return;
            }

            if (TryAttachMfDisplayControl(initial))
                return;

            if (TryAttachVideoWindowFromRenderer(initial, preserveWindowedRenderer))
                return;

            TryAttachVideoWindowFromGraph(initial);
        }

        private bool TryAttachMfDisplayControl(bool initial)
        {
            try
            {
                IPin? inPin = _videoRenderer != null ? FindPin(_videoRenderer, PinDirection.Input, null) : null;
                try
                {
                    if (inPin is IMFGetService gs0)
                    {
                        int hr0 = gs0.GetService(MR_VIDEO_RENDER_SERVICE, typeof(IMFVideoDisplayControl).GUID, out object obj);
                        if (hr0 == 0 && obj is IMFVideoDisplayControl d0)
                        {
                            _mfDisplay = d0;
                            _videoWindow = null;
                            _mfDisplay.SetAspectRatioMode((int)MFVideoARMode.PreservePicture);
                            try { _mfDisplay.SetFullscreen(false); } catch { }
                            Dbg.Log($"{(initial ? "(pre)" : "(post)")} IMFVideoDisplayControl ottenuto dal pin.");
                            return true;
                        }
                        else Dbg.Warn($"{(initial ? "(pre)" : "(post)")} IMFGetService(pin) hr=0x{hr0:X8}");
                    }
                }
                finally
                {
                    if (inPin != null) try { Marshal.ReleaseComObject(inPin); } catch { }
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (pin) EX: " + ex.Message); }

            try
            {
                if (_videoRenderer is IMFGetService gs1)
                {
                    int hr1 = gs1.GetService(MR_VIDEO_RENDER_SERVICE, typeof(IMFVideoDisplayControl).GUID, out object obj);
                    if (hr1 == 0 && obj is IMFVideoDisplayControl d1)
                    {
                        _mfDisplay = d1;
                        _videoWindow = null;
                        _mfDisplay.SetAspectRatioMode((int)MFVideoARMode.PreservePicture);
                        try { _mfDisplay.SetFullscreen(false); } catch { }
                        Dbg.Log($"{(initial ? "(pre)" : "(post)")} IMFVideoDisplayControl ottenuto dal filtro.");
                        return true;
                    }
                    else Dbg.Warn($"{(initial ? "(pre)" : "(post)")} IMFGetService(filter) hr=0x{hr1:X8}");
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (filter) EX: " + ex.Message); }

            return false;
        }

        private bool TryAttachVideoWindowFromRenderer(bool initial, bool preserveWindowedRenderer)
        {
            try
            {
                if (preserveWindowedRenderer && _videoWindow != null)
                {
                    Dbg.Log($"{(initial ? "(pre)" : "(post)")} IVideoWindow windowed renderer preservato.");
                    return true;
                }

                if (_videoRenderer != null)
                {
                    _videoWindow = (IVideoWindow)_videoRenderer;
                    _mfDisplay = null;
                    Dbg.Log($"{(initial ? "(pre)" : "(post)")} IVideoWindow acquisito dal renderer.");
                    return true;
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (IVideoWindow/Renderer) EX: " + ex.Message); }

            return false;
        }

        private bool TryAttachVideoWindowFromGraph(bool initial)
        {
            try
            {
                if (_graph != null && _videoWindow == null)
                {
                    _videoWindow = (IVideoWindow)_graph;
                    Dbg.Log($"{(initial ? "(pre)" : "(post)")} IVideoWindow acquisito dal FilterGraph.");
                    return true;
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (IVideoWindow/Graph) EX: " + ex.Message); }

            return false;
        }


        private void CalcSizesForStereo(out MFVideoNormalizedRect? src, out MFRect dest, Rectangle ownerClient)
        {
            src = null;

            int natW = 0, natH = 0;
            // Per i frame 3D packed (Full-SBS/Full-TAB) la "native size" esposta
            // dall'EVR puo' contenere gia' il display aspect ratio corretto. Dimezzarla
            // di nuovo produce l'immagine schiacciata. La dimensione negoziata sul pin,
            // invece, e' quella fisica del frame (es. 3840x1080) ed e' la base corretta
            // per ritagliare un singolo occhio a 1920x1080.
            if (_stereo != Stereo3DMode.None)
                TryGetVideoSizeForLayout(out natW, out natH);

            try
            {
                if ((natW <= 0 || natH <= 0) && _mfDisplay != null)
                {
                    _mfDisplay.GetNativeVideoSize(out var nat, out var shown);
                    natW = Math.Max(1, nat.Width);
                    natH = Math.Max(1, nat.Height);
                    // Video anamorfico (DVD: 720x576 per un quadro 16:9): le proporzioni vere sono quelle
                    // "di visualizzazione". Con i soli pixel il riquadro veniva 5:4 e l'immagine restava piu'
                    // piccola dello schermo, con bande su tutti i lati.
                    if (_stereo == Stereo3DMode.None && shown.Width > 0 && shown.Height > 0 &&
                        Math.Abs(shown.Width / (double)shown.Height - natW / (double)natH) > 0.01)
                    {
                        natW = shown.Width;
                        natH = shown.Height;
                    }
                }
            }
            catch { }

            if (natW <= 0 || natH <= 0) { natW = 1920; natH = 1080; }

            int cropW = natW, cropH = natH;
            switch (CropStereo ? _stereo : Stereo3DMode.None)
            {
                case Stereo3DMode.SBS:
                    src = new MFVideoNormalizedRect(0f, 0f, 0.5f, 1f);
                    cropW = natW / 2;
                    break;
                case Stereo3DMode.TAB:
                    src = new MFVideoNormalizedRect(0f, 0f, 1f, 0.5f);
                    cropH = natH / 2;
                    break;
            }

            if (_stereo == Stereo3DMode.None)
            {
                var keep = CropKeep(natW / (double)natH);
                if (keep.W < 1 || keep.H < 1)
                {
                    src = new MFVideoNormalizedRect((float)((1 - keep.W) / 2), (float)((1 - keep.H) / 2), (float)((1 + keep.W) / 2), (float)((1 + keep.H) / 2));
                    cropW = Math.Max(1, (int)Math.Round(natW * keep.W));
                    cropH = Math.Max(1, (int)Math.Round(natH * keep.H));
                }
            }
            double ar = _stereo == Stereo3DMode.None ? cropW / (double)cropH : StereoDisplayAspect(natW, natH);

            int dstW, dstH;
            dstW = ownerClient.Width;
            dstH = (int)Math.Round(dstW / ar);
            if (dstH > ownerClient.Height)
            {
                dstH = ownerClient.Height;
                dstW = (int)Math.Round(dstH * ar);
            }

            int dx = ownerClient.Left + (ownerClient.Width - dstW) / 2;
            int dy = ownerClient.Top + (ownerClient.Height - dstH) / 2;
            dest = new MFRect(dx, dy, dx + dstW, dy + dstH);
        }

        private IBaseFilter? PickAudioRenderer(string? preferredName)
        {
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                // For a physical render endpoint, prefer its DirectSound wrapper. Binding
                // the bare endpoint can expose a generic "Audio Renderer" filter that
                // reports Running while the external decoder never locks to the stream.
                bool preferDirectSoundEndpoint =
                    preferredName.IndexOf(':') < 0 &&
                    !preferredName.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) &&
                    !preferredName.Contains("MPC Audio", StringComparison.OrdinalIgnoreCase);
                var ex = CreateAudioRendererByName(preferredName, out var matchedPreferred, preferDirectSound: preferDirectSoundEndpoint);
                if (ex != null)
                {
                    _audioRendererDeviceName = matchedPreferred ?? preferredName;
                    return ex;
                }

                ex = CreateFilterByName(preferredName);
                if (ex != null) _audioRendererDeviceName = preferredName;
                if (ex != null) return ex;
            }

            bool skipMpcAudioRenderer = _choice == VideoRendererChoice.MPCVR && string.IsNullOrWhiteSpace(preferredName);
            if (!skipMpcAudioRenderer)
            {
                var mpcAr = CreateFilterByName("MPC Audio Renderer");
                if (mpcAr != null)
                {
                    _audioRendererDeviceName = "MPC Audio Renderer";
                    return mpcAr;
                }
            }

            var dsByName = CreateAudioRendererByName("Default DirectSound Device", out var matchedDefaultDs);
            if (dsByName != null)
            {
                _audioRendererDeviceName = matchedDefaultDs ?? "Default DirectSound Device";
                return dsByName;
            }

            dsByName = CreateFilterByName("Default DirectSound Device");
            if (dsByName != null)
            {
                _audioRendererDeviceName = "Default DirectSound Device";
                return dsByName;
            }

            var dsByClsid = CreateFilterByClsid(new Guid("79376820-07D0-11CF-A24D-0020AFD79767"), "Default DirectSound Device (CLSID)");
            if (dsByClsid != null)
            {
                _audioRendererDeviceName = "Default DirectSound Device (CLSID)";
                return dsByClsid;
            }

            var any = DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory).FirstOrDefault();
            if (any == null) return null;
            var anyFilter = CreateAudioRendererByDevice(any, any.Name);
            if (anyFilter != null) _audioRendererDeviceName = any.Name;
            return anyFilter;
        }

        private IBaseFilter? CreateAudioRendererByName(string? friendlyName, out string? matchedDeviceName, bool preferDirectSound = false)
        {
            matchedDeviceName = null;
            if (string.IsNullOrWhiteSpace(friendlyName)) return null;

            string requested = friendlyName.Trim();
            List<DsDevice> devices;
            try { devices = DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory).ToList(); }
            catch { return null; }

            var matches = devices
                .Where(d => AudioRendererNameMatches(d.Name, requested))
                .OrderByDescending(d => ScoreAudioRendererMatch(d.Name, requested, preferDirectSound))
                .ToList();

            foreach (var dev in matches)
            {
                var filter = CreateAudioRendererByDevice(dev, requested);
                if (filter != null)
                {
                    matchedDeviceName = dev.Name;
                    return filter;
                }
            }

            Dbg.Log($"CreateAudioRendererByName: '{friendlyName}' -> NOT FOUND", Dbg.LogLevel.Verbose);
            return null;
        }

        private IBaseFilter? CreateAudioRendererByDevice(DsDevice device, string requestedName)
        {
            for (int attempt = 0; attempt < ComCreateMaxAttempts; attempt++)
            {
                try
                {
                    if (attempt > 0)
                        PrepareComCreateRetry(attempt);

                    var iid = typeof(IBaseFilter).GUID;
                    device.Mon.BindToObject(null!, null, ref iid, out object obj);
                    Dbg.Log($"CreateAudioRendererByName: '{requestedName}' -> '{device.Name}' OK", Dbg.LogLevel.Verbose);
                    return (IBaseFilter)obj;
                }
                catch (Exception ex)
                {
                    bool retryable = IsAbortComFailure(ex);
                    Dbg.Warn($"CreateAudioRendererByName: '{requestedName}' su '{device.Name}' attempt {attempt + 1}/{ComCreateMaxAttempts} EX: {ex.Message}");
                    if (!retryable)
                        break;
                }
            }

            return null;
        }

        private static bool AudioRendererNameMatches(string candidate, string requested)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(requested))
                return false;

            string c = candidate.Trim();
            string r = requested.Trim();
            string cn = CleanAudioRendererName(c);
            string rn = CleanAudioRendererName(r);

            return c.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith(r, StringComparison.OrdinalIgnoreCase) ||
                   cn.Equals(rn, StringComparison.OrdinalIgnoreCase) ||
                   ContainsEitherWay(cn, rn) ||
                   ContainsEitherWay(c, r);
        }

        private static int ScoreAudioRendererMatch(string candidate, string requested, bool preferDirectSound)
        {
            string c = candidate.Trim();
            string r = requested.Trim();
            string cn = CleanAudioRendererName(c);
            string rn = CleanAudioRendererName(r);
            int score = 0;

            if (c.Equals(r, StringComparison.OrdinalIgnoreCase)) score += 1000;
            if (cn.Equals(rn, StringComparison.OrdinalIgnoreCase)) score += 800;
            if (c.StartsWith(r, StringComparison.OrdinalIgnoreCase)) score += 500;
            if (ContainsEitherWay(cn, rn)) score += 250;
            if (preferDirectSound && c.Contains("DirectSound", StringComparison.OrdinalIgnoreCase)) score += 3000;
            if (preferDirectSound &&
                c.Equals(r, StringComparison.OrdinalIgnoreCase) &&
                !c.Contains("DirectSound", StringComparison.OrdinalIgnoreCase))
            {
                score -= 600;
            }
            if (c.Contains("MPC Audio Renderer", StringComparison.OrdinalIgnoreCase)) score -= 200;
            if (c.Contains("Default DirectSound Device", StringComparison.OrdinalIgnoreCase)) score -= 50;

            return score;
        }

        private static string CleanAudioRendererName(string name)
        {
            string clean = (name ?? "").Trim();
            int colon = clean.IndexOf(':');
            if (colon >= 0 && colon + 1 < clean.Length)
                clean = clean[(colon + 1)..].Trim();
            return clean;
        }

        private static bool ContainsEitherWay(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;
            return a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
                   b.Contains(a, StringComparison.OrdinalIgnoreCase);
        }

        private IBaseFilter? PickFallbackAudioRendererAfterConnectFailure()
        {
            // Fallback volutamente conservativo: per MPCVR soprattutto, MPC Audio Renderer puo'
            // rifiutare certi media type durante i cambi renderer. DirectSound e' meno elegante,
            // ma molto piu' tollerante come ponte di emergenza.
            var dsByName = CreateFilterByName("Default DirectSound Device");
            if (dsByName != null) { _audioRendererDeviceName = "Default DirectSound Device"; return dsByName; }

            var dsByClsid = CreateFilterByClsid(new Guid("79376820-07D0-11CF-A24D-0020AFD79767"), "Default DirectSound Device (CLSID)");
            if (dsByClsid != null) { _audioRendererDeviceName = "Default DirectSound Device"; return dsByClsid; }

            foreach (var dev in DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory))
            {
                try
                {
                    if (dev.Name.Contains("MPC Audio", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var f = CreateFilterByName(dev.Name);
                    if (f != null) { _audioRendererDeviceName = dev.Name; return f; }
                }
                catch { }
            }

            return null;
        }

        private bool TrySwitchToFallbackAudioRendererAndConnect(IPin sourceOut, out int hr)
        {
            hr = unchecked((int)0x80004005);
            if (_graph == null || sourceOut == null)
                return false;

            string previousName = _audioRendererName;
            IBaseFilter? oldRenderer = _audioRenderer;
            _audioRenderer = null;

            try
            {
                if (oldRenderer != null)
                {
                    try { DisconnectFilterPins(oldRenderer); } catch { }
                    try { _graph.RemoveFilter(oldRenderer); } catch { }
                    try { if (Marshal.IsComObject(oldRenderer)) Marshal.ReleaseComObject(oldRenderer); } catch { }
                }
            }
            catch { }

            var fallback = PickFallbackAudioRendererAfterConnectFailure();
            if (fallback == null)
            {
                Dbg.Warn("Audio fallback: nessun renderer alternativo disponibile dopo fallimento di " + previousName + ".");
                return false;
            }

            try
            {
                _audioRenderer = fallback;
                DsError.ThrowExceptionForHR(_graph.AddFilter(_audioRenderer, "Audio Renderer Fallback"));
                _audioRendererName = FilterFriendlyName(_audioRenderer);
                var rIn = FindPin(_audioRenderer, PinDirection.Input, null);
                if (rIn == null)
                {
                    Dbg.Warn("Audio fallback: input pin nullo su " + _audioRendererName + ".");
                    return false;
                }

                try
                {
                    hr = _graph.Connect(sourceOut, rIn);
                }
                finally
                {
                    try { Marshal.ReleaseComObject(rIn); } catch { }
                }

                if (hr == 0)
                {
                    Dbg.Log("Audio: fallback renderer attivo dopo fallimento di " + previousName + " → " + _audioRendererName, Dbg.LogLevel.Info);
                    return true;
                }

                Dbg.Warn($"Audio fallback: connect verso {_audioRendererName} fallita hr=0x{hr:X8}");
                return false;
            }
            catch (Exception ex)
            {
                Dbg.Warn("Audio fallback EX: " + ex.Message);
                return false;
            }
        }

        private void ConnectAudioOutToRendererOrFallback(IPin sourceOut, string label)
        {
            if (_graph == null) throw new ObjectDisposedException(nameof(DirectShowUnifiedEngine));
            if (_audioRenderer == null) throw new ApplicationException("Audio renderer mancante.");

            var rIn = FindPin(_audioRenderer, PinDirection.Input, null);
            if (rIn == null)
                throw new ApplicationException("Pin input audio renderer mancante.");

            int hr;
            try
            {
                hr = _graph.Connect(sourceOut, rIn);
            }
            finally
            {
                try { Marshal.ReleaseComObject(rIn); } catch { }
            }

            if (hr == 0)
            {
                Dbg.Log(label, Dbg.LogLevel.Verbose);
                return;
            }

            Dbg.Warn($"{label}: connect fallita su {_audioRendererName} hr=0x{hr:X8}; provo renderer audio fallback.");
            if (TrySwitchToFallbackAudioRendererAndConnect(sourceOut, out int hrFallback))
                return;

            DsError.ThrowExceptionForHR(hrFallback != 0 ? hrFallback : hr);
        }

        private IBaseFilter? CreateFilterByName(string? friendlyName)
        {
            if (string.IsNullOrWhiteSpace(friendlyName)) return null;
            Guid[] cats =
            {
                FilterCategory.LegacyAmFilterCategory,
                FilterCategory.AudioRendererCategory,
                FilterCategory.VideoCompressorCategory,
                FilterCategory.AudioCompressorCategory,
                FilterCategory.VideoInputDevice,
                FilterCategory.AudioInputDevice
            };
            foreach (var cat in cats)
            {
                foreach (var d in DsDevice.GetDevicesOfCat(cat))
                {
                    if (d.Name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.StartsWith(friendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        for (int attempt = 0; attempt < ComCreateMaxAttempts; attempt++)
                        {
                            try
                            {
                                if (attempt > 0)
                                    PrepareComCreateRetry(attempt);

                                var iid = typeof(IBaseFilter).GUID;
                                d.Mon.BindToObject(null!, null, ref iid, out object obj);
                                Dbg.Log($"CreateFilterByName: '{friendlyName}' → OK", Dbg.LogLevel.Verbose);
                                return (IBaseFilter)obj;
                            }
                            catch (Exception ex)
                            {
                                bool retryable = IsAbortComFailure(ex);
                                Dbg.Warn($"CreateFilterByName: '{friendlyName}' su '{d.Name}' attempt {attempt + 1}/{ComCreateMaxAttempts} EX: {ex.Message}");
                                if (!retryable)
                                    break;
                            }
                        }
                    }
                }
            }
            Dbg.Log($"CreateFilterByName: '{friendlyName}' → NOT FOUND", Dbg.LogLevel.Verbose);
            return null;
        }

        private IBaseFilter? CreateMpcVideoRenderer()
        {
            lock (MpcvrCreateSync)
            {
                PrepareComCreateRetry(1);

                try { _mpcvrRegistryOverride?.Dispose(); } catch { }
                _mpcvrRegistryOverride = MpcvrRegistryOverride.ApplyForHostedPlayback(
                    _enableMpcvrRtxVideoHdr,
                    _mpcvrSuperResolutionMode);

                for (int pass = 0; pass < 2; pass++)
                {
                    // Prefer the version shipped with Cinecore: an unrelated system
                    // registration can expose a different IVideoWindow implementation
                    // and leave the video surface unattached even when frames advance.
                    var renderer = CreateBundledMpcVideoRenderer()
                        ?? CreateFilterByName("MPC Video Renderer")
                        ?? CreateFilterByName("MPCVR")
                        ?? CreateFilterByName("MPC Video Renderer (MPCVR)")
                        ?? CreateFilterByClsid(CLSID_MPCVR, "MPC Video Renderer (CLSID)");

                    if (renderer != null)
                    {
                        return renderer;
                    }

                    // MPCVR può rispondere E_ABORT subito dopo il rilascio di un grafo/renderer.
                    // Libera le COM server inutilizzate e aspetta un giro prima dell'ultimo tentativo.
                    PrepareComCreateRetry(pass + 4);
                }

                Dbg.Warn("MPCVR non instanziabile: verifica registrazione x64 e dipendenze VC++ del renderer.");
                try { _mpcvrRegistryOverride?.Dispose(); } catch { }
                _mpcvrRegistryOverride = null;
                return null;
            }
        }

        private IBaseFilter? CreateMadVrRenderer()
        {
            lock (MadVrCreateSync)
            {
                return CreateBundledMadVrRenderer()
                    ?? CreateFilterByName("madVR Renderer")
                    ?? CreateFilterByName("madVR")
                    ?? CreateFilterByClsid(CLSID_MADVR, "madVR Renderer (CLSID)");
            }
        }

        private static string? FindBundledMadVrRendererPath()
        {
            try
            {
                string fileName = Environment.Is64BitProcess ? "madVR64.ax" : "madVR.ax";

                string direct = Path.Combine(AppContext.BaseDirectory, fileName);
                if (File.Exists(direct))
                    return direct;

                string thirdParty = Path.Combine(AppContext.BaseDirectory, "third-parties", "madVR09217", fileName);
                if (File.Exists(thirdParty))
                    return thirdParty;

                string thirdPartiesRoot = Path.Combine(AppContext.BaseDirectory, "third-parties");
                if (Directory.Exists(thirdPartiesRoot))
                {
                    foreach (var dir in Directory.EnumerateDirectories(thirdPartiesRoot, "madVR*", SearchOption.TopDirectoryOnly))
                    {
                        string candidate = Path.Combine(dir, fileName);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }

                foreach (var dir in Directory.EnumerateDirectories(AppContext.BaseDirectory, "madVR*", SearchOption.TopDirectoryOnly))
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }

            return null;
        }

        internal static IBaseFilter? CreateBundledMadVrRenderer()
        {
            string? axPath = FindBundledMadVrRendererPath();
            if (string.IsNullOrWhiteSpace(axPath))
                return null;

            IntPtr factoryPtr = IntPtr.Zero;
            IntPtr filterPtr = IntPtr.Zero;
            object? factoryObj = null;

            try
            {
                if (_madVrModuleHandle == IntPtr.Zero)
                {
                    _madVrModuleHandle = LoadLibraryEx(axPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                    if (_madVrModuleHandle == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        Dbg.Warn($"madVR bundled LoadLibraryEx fallito err={err}, path='{axPath}'");
                        return null;
                    }
                    Dbg.Log($"madVR bundled module caricato: {axPath}", Dbg.LogLevel.Info);
                }

                IntPtr proc = GetProcAddress(_madVrModuleHandle, "DllGetClassObject");
                if (proc == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Dbg.Warn($"madVR bundled DllGetClassObject non trovato err={err}, path='{axPath}'");
                    return null;
                }

                var getClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(proc);
                Guid clsid = CLSID_MADVR;
                Guid iidClassFactory = new("00000001-0000-0000-C000-000000000046");
                int hrFactory = getClassObject(ref clsid, ref iidClassFactory, out factoryPtr);
                if (hrFactory != 0 || factoryPtr == IntPtr.Zero)
                {
                    Dbg.Warn($"madVR bundled DllGetClassObject hr=0x{hrFactory:X8}");
                    return null;
                }

                factoryObj = Marshal.GetObjectForIUnknown(factoryPtr);
                if (factoryObj is not IClassFactory factory)
                    return null;

                Guid iidBaseFilter = typeof(IBaseFilter).GUID;
                int hrCreate = factory.CreateInstance(IntPtr.Zero, ref iidBaseFilter, out filterPtr);
                if (hrCreate != 0 || filterPtr == IntPtr.Zero)
                {
                    Dbg.Warn($"madVR bundled IClassFactory.CreateInstance hr=0x{hrCreate:X8}");
                    return null;
                }

                var obj = Marshal.GetObjectForIUnknown(filterPtr);
                if (obj is IBaseFilter filter)
                {
                    Dbg.Log("madVR creato dal modulo bundled (reg-free).", Dbg.LogLevel.Info);
                    return filter;
                }

                if (obj != null && Marshal.IsComObject(obj))
                    try { Marshal.ReleaseComObject(obj); } catch { }
            }
            catch (Exception ex)
            {
                Dbg.Warn("CreateBundledMadVrRenderer EX: " + ex.Message);
            }
            finally
            {
                if (filterPtr != IntPtr.Zero) try { Marshal.Release(filterPtr); } catch { }
                if (factoryObj != null && Marshal.IsComObject(factoryObj)) try { Marshal.ReleaseComObject(factoryObj); } catch { }
                if (factoryPtr != IntPtr.Zero) try { Marshal.Release(factoryPtr); } catch { }
            }

            return null;
        }

        private static string? FindBundledMpcVideoRendererPath()
        {
            try
            {
                string fileName = Environment.Is64BitProcess ? "MpcVideoRenderer64.ax" : "MpcVideoRenderer.ax";

                string direct = Path.Combine(AppContext.BaseDirectory, fileName);
                if (File.Exists(direct))
                    return direct;

                string thirdPartiesRoot = Path.Combine(AppContext.BaseDirectory, "third-parties");
                if (Directory.Exists(thirdPartiesRoot))
                {
                    foreach (var dir in Directory.EnumerateDirectories(thirdPartiesRoot, "MpcVideoRenderer-*", SearchOption.TopDirectoryOnly))
                    {
                        string candidate = Path.Combine(dir, fileName);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }

                foreach (var dir in Directory.EnumerateDirectories(AppContext.BaseDirectory, "MpcVideoRenderer-*", SearchOption.TopDirectoryOnly))
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }

            return null;
        }

        private static IBaseFilter? CreateBundledMpcVideoRenderer()
        {
            string? axPath = FindBundledMpcVideoRendererPath();
            if (string.IsNullOrWhiteSpace(axPath))
                return null;

            IntPtr factoryPtr = IntPtr.Zero;
            IntPtr filterPtr = IntPtr.Zero;
            object? factoryObj = null;

            try
            {
                if (_mpcvrModuleHandle == IntPtr.Zero)
                {
                    _mpcvrModuleHandle = LoadLibraryEx(axPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                    if (_mpcvrModuleHandle == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        Dbg.Warn($"MPCVR bundled LoadLibraryEx fallito err={err}, path='{axPath}'");
                        return null;
                    }
                    Dbg.Log($"MPCVR bundled module caricato: {axPath}", Dbg.LogLevel.Info);
                }

                IntPtr proc = GetProcAddress(_mpcvrModuleHandle, "DllGetClassObject");
                if (proc == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Dbg.Warn($"MPCVR bundled DllGetClassObject non trovato err={err}, path='{axPath}'");
                    return null;
                }

                var getClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(proc);
                Guid clsid = CLSID_MPCVR;
                Guid iidClassFactory = new("00000001-0000-0000-C000-000000000046");
                int hrFactory = getClassObject(ref clsid, ref iidClassFactory, out factoryPtr);
                if (hrFactory != 0 || factoryPtr == IntPtr.Zero)
                {
                    Dbg.Warn($"MPCVR bundled DllGetClassObject hr=0x{hrFactory:X8}");
                    return null;
                }

                factoryObj = Marshal.GetObjectForIUnknown(factoryPtr);
                if (factoryObj is not IClassFactory factory)
                    return null;

                Guid iidBaseFilter = typeof(IBaseFilter).GUID;
                int hrCreate = factory.CreateInstance(IntPtr.Zero, ref iidBaseFilter, out filterPtr);
                if (hrCreate != 0 || filterPtr == IntPtr.Zero)
                {
                    Dbg.Warn($"MPCVR bundled IClassFactory.CreateInstance hr=0x{hrCreate:X8}");
                    return null;
                }

                var obj = Marshal.GetObjectForIUnknown(filterPtr);
                if (obj is IBaseFilter filter)
                {
                    Dbg.Log("MPCVR creato dal modulo bundled (reg-free).", Dbg.LogLevel.Info);
                    return filter;
                }

                if (obj != null && Marshal.IsComObject(obj))
                    try { Marshal.ReleaseComObject(obj); } catch { }
            }
            catch (Exception ex)
            {
                Dbg.Warn("CreateBundledMpcVideoRenderer EX: " + ex.Message);
            }
            finally
            {
                if (filterPtr != IntPtr.Zero) try { Marshal.Release(filterPtr); } catch { }
                if (factoryObj != null && Marshal.IsComObject(factoryObj)) try { Marshal.ReleaseComObject(factoryObj); } catch { }
                if (factoryPtr != IntPtr.Zero) try { Marshal.Release(factoryPtr); } catch { }
            }

            return null;
        }

        private IBaseFilter? CreateVideoRendererByChoice(VideoRendererChoice c)
        {
            try
            {
                Dbg.Log($"[VIDEO] CreateVideoRendererByChoice: requested={c}, fileIsHdr={_fileIsHdr}");
                return c switch
                {
                    VideoRendererChoice.MADVR => CreateMadVrRenderer()
                                              ?? throw new ApplicationException("madVR non trovato. Esegui 'install.bat' come Amministratore nella cartella di madVR."),
                    VideoRendererChoice.MPCVR => CreateMpcVideoRenderer(),
                    VideoRendererChoice.EVR => CreateFilterByName("Enhanced Video Renderer"),
                    _ => null
                };
            }
            catch (Exception ex) { Dbg.Warn("CreateVideoRendererByChoice EX: " + ex.Message); throw; }
        }

        private void ConnectByType(IBaseFilter src, IBaseFilter dst, Guid? majorType)
        {
            if (_graph == null) throw new ObjectDisposedException(nameof(DirectShowUnifiedEngine));
            var outPin = FindPin(src, PinDirection.Output, majorType) ?? FindPin(src, PinDirection.Output, null);
            var inPin = FindPin(dst, PinDirection.Input, majorType) ?? FindPin(dst, PinDirection.Input, null);
            if (outPin == null || inPin == null) throw new ApplicationException("Pin non trovati per la connessione.");
            int hr = _graph.Connect(outPin, inPin); DsError.ThrowExceptionForHR(hr);
            Dbg.Log($"ConnectByType: {FilterFriendlyName(src)} → {FilterFriendlyName(dst)} major={majorType}", Dbg.LogLevel.Verbose);
        }

        private static IPin? FindPin(IBaseFilter f, PinDirection dir, Guid? major)
        {
            f.EnumPins(out var e);
            try
            {
                var arr = new IPin[1]; IPin? fallback = null;
                while (e.Next(1, arr, nint.Zero) == 0)
                {
                    arr[0].QueryDirection(out var d);
                    if (d == dir)
                    {
                        if (major == null) return arr[0];
                        if (PinHasType(arr[0], major.Value)) { return arr[0]; }
                        if (fallback == null) fallback = arr[0]; else Marshal.ReleaseComObject(arr[0]);
                    }
                    else Marshal.ReleaseComObject(arr[0]);
                }
                return fallback;
            }
            finally { Marshal.ReleaseComObject(e); }
        }

        private static bool PinHasType(IPin p, Guid major)
        {
            p.EnumMediaTypes(out var e);
            try
            {
                var arr = new AMMediaType[1];
                while (e.Next(1, arr, nint.Zero) == 0)
                {
                    bool ok = arr[0].majorType == major;
                    DsUtils.FreeAMMediaType(arr[0]);
                    if (ok) return true;
                }
                return false;
            }
            finally { Marshal.ReleaseComObject(e); }
        }

        private enum MFVideoARMode { None = 0, PreservePicture = 1, PreservePixel = 2, NonLinearStretch = 4, Mask = 0x7 }

        private static string FilterFriendlyName(IBaseFilter f)
        {
            try
            {
                f.QueryFilterInfo(out var fi);
                var n = fi.achName;
                if (fi.pGraph != null) Marshal.ReleaseComObject(fi.pGraph);
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
            catch { }

            try
            {
                if (f is IPersist p) { p.GetClassID(out var cls); return cls.ToString(); }
            }
            catch { }

            return f.GetType().Name;
        }

        [ComImport, Guid("0000010c-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersist { [PreserveSig] int GetClassID(out Guid pClassID); }

        private void TryDetectBitstream()
        {
            bool old = _bitstreamActive;
            bool now = old; // ← fallback conservativo: in caso di errore NON cambiare stato

            // NEW: se sto forzando PCM non ha senso interrogare LAV Audio.
            // Sappiamo già che verso l'Audio Renderer stiamo mandando PCM decodificato
            // (LAV Splitter → MPC Audio Decoder → Renderer).
            if (_forcePcmToggle)
            {
                _bitstreamActive = false;
                Dbg.Log("Audio bitstreamActive=" + _bitstreamActive + " (forced PCM)");

                if (_bitstreamActive != old)
                {
                    try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }
                    try { _updateCb?.Invoke(); } catch { }
                }
                return;
            }

            if (_lavAudio == null || _graph == null)
            {
                // grafo smontato: riportiamo a false e notifichiamo
                _bitstreamActive = false;
                Dbg.Log("Audio bitstreamActive=" + _bitstreamActive);
                if (_bitstreamActive != old)
                {
                    try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }
                    try { _updateCb?.Invoke(); } catch { }
                }
                return;
            }

            try
            {
                var aOut = FindPin(_lavAudio, PinDirection.Output, null);
                if (aOut == null)
                {
                    // Non troviamo proprio l'output di LAV Audio: è comunque una situazione
                    // in cui non stiamo facendo bitstream → assumiamo PCM e notifichiamo.
                    Dbg.Warn("TryDetectBitstream: LAV Audio out pin null → assumo PCM.");
                    _bitstreamActive = false;
                    Dbg.Log("Audio bitstreamActive=" + _bitstreamActive);
                    if (_bitstreamActive != old)
                    {
                        try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }
                        try { _updateCb?.Invoke(); } catch { }
                    }
                    return;
                }

                aOut.ConnectedTo(out var rIn);
                if (rIn == null)
                {
                    // Questo è esattamente il caso "Forza PCM con MPC Audio Decoder":
                    // LAV Audio è nel grafo ma scollegato. Anche qui, niente bitstream.
                    Dbg.Warn("TryDetectBitstream: downstream audio pin null → assumo PCM.");
                    _bitstreamActive = false;
                    Dbg.Log("Audio bitstreamActive=" + _bitstreamActive);
                    if (_bitstreamActive != old)
                    {
                        try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }
                        try { _updateCb?.Invoke(); } catch { }
                    }
                    return;
                }

                var mt = new AMMediaType();
                int hr = rIn.ConnectionMediaType(mt);
                if (hr != 0)
                {
                    Dbg.Warn($"TryDetectBitstream: ConnectionMediaType hr=0x{hr:X8} → assumo PCM.");
                    DsUtils.FreeAMMediaType(mt);
                    Marshal.ReleaseComObject(rIn);

                    _bitstreamActive = false;
                    Dbg.Log("Audio bitstreamActive=" + _bitstreamActive);
                    if (_bitstreamActive != old)
                    {
                        try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }
                        try { _updateCb?.Invoke(); } catch { }
                    }
                    return;
                }

                try
                {
                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != nint.Zero)
                    {
                        var wfx = Marshal.PtrToStructure<WaveFormatEx>(mt.formatPtr);

                        if (wfx.wFormatTag == 1 || wfx.wFormatTag == 3)
                            now = false; // PCM / FLOAT
                        else if (wfx.wFormatTag == 0x0092)
                            now = true;  // AC-3 IEC61937
                        else if (wfx.wFormatTag == 0xFFFE && wfx.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensible>(mt.formatPtr);
                            now = !(ext.SubFormat == MediaSubType.PCM || ext.SubFormat == MediaSubType.IEEE_FLOAT);
                        }
                        else
                            now = true;  // altri tag → considera bitstream
                    }
                    else
                    {
                        now = !(mt.subType == MediaSubType.PCM || mt.subType == MediaSubType.IEEE_FLOAT);
                    }
                }
                finally
                {
                    DsUtils.FreeAMMediaType(mt);
                    Marshal.ReleaseComObject(rIn);
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("TryDetectBitstream EX: " + ex.Message);
                // in caso di errore di introspezione lasciamo il valore invariato
                return;
            }

            _bitstreamActive = now;
            Dbg.Log("Audio bitstreamActive=" + _bitstreamActive);
            if (_bitstreamActive != old)
            {
                try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }
                try { _updateCb?.Invoke(); } catch { }
            }
        }

        private static bool TryReadVideoSizeFromMediaType(AMMediaType mt, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                if (mt.formatPtr == nint.Zero)
                    return false;

                if (mt.formatType == FormatType.VideoInfo2 ||
                    mt.formatType == FormatType.Mpeg2Video)
                {
                    var vih = (VideoInfoHeader2)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader2))!;
                    return TryResolveVideoHeaderSize(vih.BmiHeader, vih.SrcRect, vih.TargetRect, out width, out height);
                }

                if (mt.formatType == FormatType.VideoInfo ||
                    mt.formatType == FormatType.MpegVideo)
                {
                    var vih = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader))!;
                    return TryResolveVideoHeaderSize(vih.BmiHeader, vih.SrcRect, vih.TargetRect, out width, out height);
                }
            }
            catch { }

            return false;
        }

        private static bool TryResolveVideoHeaderSize(BitmapInfoHeader bmi, DsRect srcRect, DsRect targetRect, out int width, out int height)
        {
            width = Math.Abs(bmi.Width);
            height = Math.Abs(bmi.Height);
            if (width > 0 && height > 0)
                return true;

            width = Math.Abs(srcRect.right - srcRect.left);
            height = Math.Abs(srcRect.bottom - srcRect.top);
            if (width > 0 && height > 0)
                return true;

            width = Math.Abs(targetRect.right - targetRect.left);
            height = Math.Abs(targetRect.bottom - targetRect.top);
            return width > 0 && height > 0;
        }

        private bool TryReadVideoSizeFromBasicVideo(out int width, out int height)
        {
            width = 0;
            height = 0;

            static bool TryRead(object? obj, out int w, out int h)
            {
                w = 0;
                h = 0;
                try
                {
                    if (obj is not IBasicVideo bv)
                        return false;

                    bv.GetVideoSize(out w, out h);
                    w = Math.Abs(w);
                    h = Math.Abs(h);
                    if (w > 0 && h > 0)
                        return true;

                    bv.get_VideoWidth(out w);
                    bv.get_VideoHeight(out h);
                    w = Math.Abs(w);
                    h = Math.Abs(h);
                    return w > 0 && h > 0;
                }
                catch { return false; }
            }

            return TryRead(_videoRenderer, out width, out height) ||
                   TryRead(_graph, out width, out height);
        }

        private void TryDumpNegotiatedVideoMT()
        {
            try
            {
                if (_graph == null || _videoRenderer == null) return;

                // madVR resta renderer windowed puro: non tocchiamo i pin.
                // MPCVR invece espone il media type sul pin input e ci serve per evitare 0x0.
                if (IsWindowedRenderer && _choice != VideoRendererChoice.MPCVR)
                {
                    Dbg.Log("TryDumpNegotiatedVideoMT: madVR windowed renderer -> skip", Dbg.LogLevel.Verbose);
                    return;
                }

                var inPin = FindPin(_videoRenderer, PinDirection.Input, null);
                if (inPin == null) { Dbg.Warn("TryDumpNegotiatedVideoMT: inPin null"); return; }

                var mt = new AMMediaType();
                try
                {
                    inPin.ConnectionMediaType(mt);
                    var sb = new StringBuilder();
                    string sub = GuidToCodecName(mt.subType);
                    sb.AppendLine("=== NEGOTIATED VIDEO MT ===");
                    sb.AppendLine($"majorType: {mt.majorType}");
                    sb.AppendLine($"subType:   {Dbg.Hex(mt.subType)} ({sub})");
                    sb.AppendLine($"formatType:{mt.formatType}");

                    if (TryReadVideoSizeFromMediaType(mt, out int w, out int h))
                    {
                        sb.AppendLine($"  size: {w}x{h}");
                        _cachedFmt = new CachedFmt(w, h, sub);
                    }
                    else if (_choice == VideoRendererChoice.MPCVR &&
                             TryReadVideoSizeFromBasicVideo(out w, out h))
                    {
                        sb.AppendLine($"  size: {w}x{h} (IBasicVideo fallback)");
                        _cachedFmt = new CachedFmt(w, h, sub);
                    }

                    _lastVmtDump = sb.ToString();
                    _lastVmtAt = DateTime.Now;
                    Dbg.Log(_lastVmtDump.Replace("\r\n", " | "), Dbg.LogLevel.Verbose);
                }
                finally
                {
                    if (mt != null) DsUtils.FreeAMMediaType(mt);
                    try { Marshal.ReleaseComObject(inPin); } catch { }
                }
            }
            catch (Exception ex) { Dbg.Warn("TryDumpNegotiatedVideoMT EX: " + ex.Message); }
        }

        private void StartMediaTypePolling()
        {
            // DirectShow/renderer COM vanno interrogati dal thread UI/STA. Il vecchio Task.Run
            // generava TaskCanceledException a raffica e, nei cambi madVR↔MPCVR, poteva toccare
            // COM gia' rilasciati. Timer WinForms = stesso thread della UI e stop sincrono in DisposeGraph.
            StopMediaTypePolling();

            // madVR is a pure windowed renderer here and TryDumpNegotiatedVideoMT() deliberately
            // skips it. Starting a 220 ms UI timer only to perform ten no-op probes adds needless
            // activity during preroll/startup, so do not create the polling timer for madVR.
            if (_choice == VideoRendererChoice.MADVR)
                return;

            _mtPollTickCount = 0;
            _mtPollTimer = new System.Windows.Forms.Timer { Interval = 220 };
            _mtPollTimer.Tick += (_, __) =>
            {
                try
                {
                    _mtPollTickCount++;
                    TryDumpNegotiatedVideoMT();
                    var cf = _cachedFmt;
                    if ((cf.W > 0 && cf.H > 0) || _mtPollTickCount >= 10)
                    {
                        StopMediaTypePolling();
                        try { _updateCb?.Invoke(); } catch { }
                    }
                }
                catch
                {
                    StopMediaTypePolling();
                }
            };
            _mtPollTimer.Start();
        }

        public (string text, DateTime when) GetLastVideoMTDump() => (_lastVmtDump, _lastVmtAt);
        public (int width, int height, string subtype) GetNegotiatedVideoFormat()
        {
            var cached = _cachedFmt;

            // madVR windowed non va interrogato via pin: usa l'ultimo valore valido se esiste.
            // MPCVR invece va interrogato, altrimenti resta bloccato su 0x0.
            if (IsWindowedRenderer && _choice != VideoRendererChoice.MPCVR)
            {
                if (cached.W > 0 && cached.H > 0)
                    return (cached.W, cached.H, cached.Sub);
                return (0, 0, "madVR (windowed)");
            }

            int w = 0, h = 0; string sub = "?";
            try
            {
                if (_graph == null || _videoRenderer == null)
                    return cached.W > 0 && cached.H > 0 ? (cached.W, cached.H, cached.Sub) : (0, 0, "?");

                var inPin = FindPin(_videoRenderer, PinDirection.Input, null);
                if (inPin == null)
                    return cached.W > 0 && cached.H > 0 ? (cached.W, cached.H, cached.Sub) : (0, 0, "?");

                var mt = new AMMediaType();
                try
                {
                    inPin.ConnectionMediaType(mt);
                    sub = GuidToCodecName(mt.subType);
                    if (TryReadVideoSizeFromMediaType(mt, out w, out h))
                        _cachedFmt = new CachedFmt(w, h, sub);
                }
                finally
                {
                    if (mt != null) DsUtils.FreeAMMediaType(mt);
                    try { Marshal.ReleaseComObject(inPin); } catch { }
                }
            }
            catch (Exception ex) { Dbg.Warn("GetNegotiatedVideoFormat EX: " + ex.Message); }

            if (w <= 0 || h <= 0)
            {
                cached = _cachedFmt;
                if (cached.W > 0 && cached.H > 0)
                    return (cached.W, cached.H, cached.Sub);

                if (_choice == VideoRendererChoice.MPCVR &&
                    TryReadVideoSizeFromBasicVideo(out w, out h))
                {
                    sub = string.IsNullOrWhiteSpace(sub) || sub == "?" ? "MPCVR/IBasicVideo" : sub;
                    _cachedFmt = new CachedFmt(w, h, sub);
                    return (w, h, sub);
                }
            }

            return (w, h, sub);
        }

        public bool TrySnapshot(out int byteCount)
        {
            byteCount = 0;
            if (_choice == VideoRendererChoice.MPCVR)
            {
                if (TryMpcvrDisplayedImageSnapshot(out byteCount))
                    return true;

                if (TryBasicVideoSnapshot(out byteCount))
                    return true;
            }

            if (_mfDisplay != null)
            {
                nint pDib = nint.Zero;
                try
                {
                    _mfDisplay.GetCurrentImage(out pDib, out int cb, out long _);
                    if (pDib != nint.Zero && cb > 0)
                    {
                        byteCount = cb;
                        _lastSnapshotBytes = cb; _lastSnapshotAt = DateTime.Now;
                        return true;
                    }
                }
                catch { }
                finally { if (pDib != nint.Zero) try { Marshal.FreeCoTaskMem(pDib); } catch { } }
            }
            return false;
        }

        private bool TryMpcvrDisplayedImageSnapshot(out int byteCount)
        {
            byteCount = 0;
            var config = TryGetMpcvrConfig();
            if (config == null)
                return false;

            IntPtr dib = IntPtr.Zero;
            try
            {
                int hr = config.Flt_GetBin("displayedImage", out dib, out uint size);
                if (hr == 0 && dib != IntPtr.Zero && size > 0 && size <= int.MaxValue)
                {
                    byteCount = (int)size;
                    _lastSnapshotBytes = byteCount;
                    _lastSnapshotAt = DateTime.Now;
                    return true;
                }
            }
            catch { }
            finally
            {
                if (dib != IntPtr.Zero)
                {
                    try { LocalFree(dib); } catch { }
                }
            }

            return false;
        }

        private bool TryBasicVideoSnapshot(out int byteCount)
        {
            byteCount = 0;
            var basicVideo = TryGetBasicVideoController();
            if (basicVideo == null)
                return false;

            try
            {
                int size = 0;
                int hr = basicVideo.GetCurrentImage(ref size, IntPtr.Zero);
                if (hr == 0 && size > 0)
                {
                    byteCount = size;
                    _lastSnapshotBytes = byteCount;
                    _lastSnapshotAt = DateTime.Now;
                    return true;
                }
            }
            catch { }

            return false;
        }

        public (int bytes, DateTime when) GetLastSnapshotInfo() => (_lastSnapshotBytes, _lastSnapshotAt);

        private static string GuidToCodecName(Guid sub)
        {
            if (sub == MediaSubType.PCM) return "PCM";
            if (sub == MediaSubType.IEEE_FLOAT) return "PCM Float";
            if (sub == MediaSubType.DolbyAC3) return "Dolby Digital";
            if (sub == MediaSubType.H264) return "H.264";
            if (sub == MediaSubType.NV12) return "NV12";
            var s = sub.ToString().ToUpperInvariant();
            if (s.Contains("30313050")) return "P010";
            if (s.Contains("36313050") || s.Contains("50313130") || s.Contains("P016")) return "P016";
            return sub.ToString();
        }

        public void UpdateVideoWindow(nint ownerHwnd, Rectangle ownerClient)
        {
            if (!_hasVideo) return;
            int w = Math.Max(0, ownerClient.Width);
            int h = Math.Max(0, ownerClient.Height);
            if (w < 2 || h < 2) return;

            var localClient = new Rectangle(0, 0, w, h);

            if (_mfDisplay == null && _videoWindow == null)
            {
                try { AttachDisplayInterfaces(initial: false); } catch { }
            }

            var videoArea = ScaledVideoClient(w, h);
            var placement = new VideoPlacementKey(ownerHwnd, videoArea.Width, videoArea.Height, _stereo, _allowUpscaling,
                _cachedFmt.W, _cachedFmt.H, _mfDisplay, _videoWindow);
            if (_choice == VideoRendererChoice.MADVR && _appliedMadVrPlacement == placement)
                return;
            if (_choice == VideoRendererChoice.MPCVR && _appliedMpcvrPlacement == placement)
                return;

            // MPCVR: usa IVideoWindow se disponibile; se la RCW e' diventata invalida,
            // ritenta subito l'attach e lascia spazio al fallback IMFVideoDisplayControl.
            if (_choice == VideoRendererChoice.MPCVR && _videoWindow != null)
            {
                if (TryUpdateIVideoWindow(ownerHwnd, w, h))
                {
                    _appliedMpcvrPlacement = placement;
                    return;
                }

                if (_videoWindow == null && _mfDisplay == null)
                {
                    try { AttachDisplayInterfaces(initial: false); } catch { }
                }
            }

            try
            {
                if (_mfDisplay != null)
                {
                    bool configure = _choice != VideoRendererChoice.MADVR ||
                        !ReferenceEquals(_configuredMadVrWindow, _mfDisplay) || _configuredMadVrOwner != ownerHwnd;
                    if (configure)
                    {
                        _mfDisplay.SetVideoWindow(ownerHwnd);
                        try { _mfDisplay.SetFullscreen(false); } catch { }
                        try { _mfDisplay.SetBorderColor(0x000000); } catch { }
                        if (_choice == VideoRendererChoice.MADVR)
                        {
                            _configuredMadVrWindow = _mfDisplay;
                            _configuredMadVrOwner = ownerHwnd;
                        }
                    }
                    _lastOwnerHwnd = ownerHwnd;
                    // EVR usa questo colore per le bande e per il bordo residuo del
                    // destination rectangle. Il valore predefinito puo' ereditare il
                    // colore di accento di Windows e comparire come due righe blu sui
                    // lati delle scene scure.

                    CalcSizesForStereo(out var src, out var dest, videoArea);
                    // Con il 3D la destinazione ha gia' le proporzioni corrette dell'occhio:
                    // se EVR conservasse quelle del ritaglio (960x1080) lo mostrerebbe stretto.
                    try { _mfDisplay.SetAspectRatioMode((int)(_stereo != Stereo3DMode.None ? MFVideoARMode.None : MFVideoARMode.PreservePicture)); } catch { }
                    if (dest.right <= dest.left || dest.bottom <= dest.top)
                        dest = new MFRect(0, 0, w, h);

                    if (src.HasValue)
                    {
                        unsafe { var s = src.Value; _mfDisplay.SetVideoPosition((nint)(&s), ref dest); }
                    }
                    else
                    {
                        // Sorgente intera detta in modo esplicito: con null EVR tiene l'ultimo ritaglio.
                        unsafe { var whole = new MFVideoNormalizedRect(0f, 0f, 1f, 1f); _mfDisplay.SetVideoPosition((nint)(&whole), ref dest); }
                    }

                    if (_cropRepaintPending)
                    {
                        _cropRepaintPending = false;
                        try { RedrawWindow(ownerHwnd, 0, 0, 0x0001 | 0x0004 | 0x0100); } catch { }
                    }
                    _mfDisplay.RepaintVideo();
                    _lastDest = dest;
                    if (_choice == VideoRendererChoice.MADVR) _appliedMadVrPlacement = placement;
                    if (_choice == VideoRendererChoice.MPCVR) _appliedMpcvrPlacement = placement;
                    return;
                }
            }
            catch (System.Runtime.InteropServices.InvalidComObjectException)
            {
                _mfDisplay = null;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Dbg.Warn("UpdateVideoWindow (windowless) COM EX: " + ex.Message);
                _mfDisplay = null;
            }
            catch (Exception ex)
            {
                Dbg.Warn("UpdateVideoWindow (windowless) EX: " + ex.Message);
            }

            if (TryUpdateIVideoWindow(ownerHwnd, w, h))
            {
                if (_choice == VideoRendererChoice.MADVR) _appliedMadVrPlacement = placement;
                if (_choice == VideoRendererChoice.MPCVR) _appliedMpcvrPlacement = placement;
            }
        }

        public void DetachVideoWindowForUiTransition()
        {
            InvalidateVideoPlacement();
            // Deve essere chiamato dal thread UI prima che il teardown del grafo venga
            // spostato in background. In questo modo la finestra nativa del renderer
            // non puo' restare davanti alla libreria e intercettarne i click.
            try { _videoWindow?.put_Visible(OABool.False); } catch { }
            DestroyStereoClipWindow();
            try { _videoWindow?.put_MessageDrain(IntPtr.Zero); } catch { }
            try { _videoWindow?.put_Owner(IntPtr.Zero); } catch { }
            try { _mfDisplay?.SetVideoWindow(IntPtr.Zero); } catch { }
            _lastOwnerHwnd = IntPtr.Zero;
            _lastDest = new MFRect(0, 0, 0, 0);
        }

        private IBasicVideo? TryGetBasicVideoController()
        {
            try
            {
                if (_videoRenderer is IBasicVideo rendererBasicVideo)
                    return rendererBasicVideo;
            }
            catch { }

            try
            {
                if (_graph is IBasicVideo graphBasicVideo)
                    return graphBasicVideo;
            }
            catch { }

            return null;
        }

        /// <summary>Dimensioni reali del fotogramma lette dal file (probe), prima fonte per il ritaglio 3D.</summary>
        public Size SourceFrameSize { get; set; }

        // ----- Ritaglio a un formato scelto (16:9, 2,39:1...): si tiene il centro del fotogramma -----
        private double _cropAspect;
        private bool _basicVideoCropApplied;
        private bool _mpcvrCropWindow;
        private bool _mpcvrRegionApplied;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowRgn(nint hWnd, nint region, bool redraw);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        private bool _cropRepaintPending;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(nint hWnd, nint updateRect, nint updateRegion, uint flags);

        /// <summary>Formato a cui ritagliare l'immagine (larghezza / altezza); 0 = nessun ritaglio.</summary>
        public double CropAspect
        {
            get => _cropAspect;
            set
            {
                value = Math.Max(0, value);
                if (Math.Abs(_cropAspect - value) < 0.0005) return;
                _cropAspect = value;
                _cropRepaintPending = true;
                _appliedMadVrPlacement = null;
                _appliedMpcvrPlacement = null;
            }
        }

        /// <summary>Quanta parte del fotogramma resta, in larghezza e in altezza, dato il suo formato a schermo.</summary>
        private (double W, double H) CropKeep(double frameAspect)
        {
            if (_cropAspect <= 0 || _stereo != Stereo3DMode.None || frameAspect <= 0) return (1, 1);
            return _cropAspect > frameAspect ? (1, frameAspect / _cropAspect) : (_cropAspect / frameAspect, 1);
        }

        /// <summary>Ritaglio alla sorgente per i renderer che lo accettano da IBasicVideo (MPC VR, madVR).</summary>
        private void ApplyBasicVideoCrop(IBasicVideo basicVideo, double frameAspect)
        {
            try
            {
                var keep = CropKeep(frameAspect);
                if (keep.W >= 1 && keep.H >= 1)
                {
                    if (_basicVideoCropApplied)
                    {
                        basicVideo.SetDefaultSourcePosition();
                        try { basicVideo.GetVideoSize(out int fullW, out int fullH); if (fullW != 0 && fullH != 0) basicVideo.SetSourcePosition(0, 0, Math.Abs(fullW), Math.Abs(fullH)); } catch { }
                        _basicVideoCropApplied = false;
                    }
                    return;
                }
                basicVideo.GetVideoSize(out int sourceW, out int sourceH);
                sourceW = Math.Abs(sourceW); sourceH = Math.Abs(sourceH);
                if (sourceW < 2 || sourceH < 2) return;
                int w = Math.Max(2, (int)Math.Round(sourceW * keep.W)), h = Math.Max(2, (int)Math.Round(sourceH * keep.H));
                int hr = basicVideo.SetSourcePosition((sourceW - w) / 2, (sourceH - h) / 2, w, h);
                _basicVideoCropApplied = hr >= 0;
                if (hr < 0) Dbg.Warn($"[CROP] source position refused by {_choice}: {HrText(hr)}");
            }
            catch (Exception ex) { Dbg.Warn("[CROP] " + ex.Message); }
        }

        private bool TryGetVideoSizeForLayout(out int width, out int height)
        {
            // Per il 3D serve la dimensione fisica del fotogramma: madVR via IBasicVideo e il
            // formato negoziato possono riportare valori gia' adattati (es. 3840x2160 per 3840x1080).
            if (_stereo != Stereo3DMode.None && SourceFrameSize.Width > 1 && SourceFrameSize.Height > 1)
            {
                width = SourceFrameSize.Width;
                height = SourceFrameSize.Height;
                return true;
            }
            var cached = _cachedFmt;
            if (cached.W > 0 && cached.H > 0)
            {
                width = cached.W;
                height = cached.H;
                if (DvdMode && _dvdDisplayAspect > 0) width = Math.Max(1, (int)Math.Round(height * _dvdDisplayAspect));
                return true;
            }

            if (TryReadVideoSizeFromBasicVideo(out width, out height))
            {
                if (DvdMode && _dvdDisplayAspect > 0) width = Math.Max(1, (int)Math.Round(height * _dvdDisplayAspect));
                return true;
            }

            width = 0;
            height = 0;
            return false;
        }

        private Rectangle CalculateWindowedVideoDestination(int ownerWidth, int ownerHeight, int videoWidth, int videoHeight)
        {
            ownerWidth = Math.Max(2, ownerWidth);
            ownerHeight = Math.Max(2, ownerHeight);
            videoWidth = Math.Max(1, videoWidth);
            videoHeight = Math.Max(1, videoHeight);

            int cropW = videoWidth;
            int cropH = videoHeight;
            if (CropStereo && _stereo == Stereo3DMode.SBS)
                cropW = Math.Max(1, videoWidth / 2);
            else if (CropStereo && _stereo == Stereo3DMode.TAB)
                cropH = Math.Max(1, videoHeight / 2);

            if (_stereo == Stereo3DMode.None)
            {
                var keep = CropKeep(videoWidth / (double)videoHeight);
                cropW = Math.Max(1, (int)Math.Round(cropW * keep.W));
                cropH = Math.Max(1, (int)Math.Round(cropH * keep.H));
            }
            double ar = _stereo == Stereo3DMode.None ? cropW / (double)cropH : StereoDisplayAspect(videoWidth, videoHeight);
            int dstW;
            int dstH;

            // _allowUpscaling selects the processing/scaling features, not the
            // presentation size. Limiting the destination to source pixels left
            // a 720p MPCVR video surrounded by huge bars on a 1440p display.
            dstW = ownerWidth;
            dstH = (int)Math.Round(dstW / ar);
            if (dstH > ownerHeight)
            {
                dstH = ownerHeight;
                dstW = (int)Math.Round(dstH * ar);
            }

            dstW = Math.Max(2, Math.Min(ownerWidth, dstW));
            dstH = Math.Max(2, Math.Min(ownerHeight, dstH));
            int x = (ownerWidth - dstW) / 2;
            int y = (ownerHeight - dstH) / 2;
            return new Rectangle(x, y, dstW, dstH);
        }

        private bool TryUpdateMpcvrBasicVideoPosition(int ownerWidth, int ownerHeight, out Rectangle videoDest)
        {
            videoDest = new Rectangle(0, 0, Math.Max(2, ownerWidth), Math.Max(2, ownerHeight));

            var basicVideo = TryGetBasicVideoController();
            if (basicVideo == null)
                return false;

            int videoWidth = 0;
            int videoHeight = 0;
            TryGetVideoSizeForLayout(out videoWidth, out videoHeight);
            if (videoWidth <= 0 || videoHeight <= 0)
            {
                videoWidth = Math.Max(2, ownerWidth);
                videoHeight = Math.Max(2, ownerHeight);
            }

            videoDest = CalculateWindowedVideoDestination(ownerWidth, ownerHeight, videoWidth, videoHeight);

            try
            {
                int hrSrc = basicVideo.SetDefaultSourcePosition();
                // MPC VR non accetta un rettangolo sorgente: il ritaglio si ottiene dando al video una destinazione piu'
                // grande della sua finestra, che viene stretta sul riquadro visibile (vedi TryUpdateIVideoWindow).
                var cropKeep = CropKeep(videoWidth / (double)videoHeight);
                _mpcvrCropWindow = cropKeep.W < 1 || cropKeep.H < 1;

                if (hrSrc < 0)
                    Dbg.Warn($"MPCVR IBasicVideo source position failed hr={HrText(hrSrc)}");

                int hrDest;
                if (_mpcvrCropWindow)
                {
                    int fullW = (int)Math.Round(videoDest.Width / cropKeep.W), fullH = (int)Math.Round(videoDest.Height / cropKeep.H);
                    hrDest = basicVideo.SetDestinationPosition(videoDest.Left - (fullW - videoDest.Width) / 2, videoDest.Top - (fullH - videoDest.Height) / 2, fullW, fullH);
                }
                else hrDest = basicVideo.SetDestinationPosition(videoDest.Left, videoDest.Top, videoDest.Width, videoDest.Height);
                if (hrDest < 0)
                {
                    Dbg.Warn($"MPCVR IBasicVideo destination position failed hr={HrText(hrDest)}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Dbg.Warn("MPCVR IBasicVideo position EX: " + ex.Message);
                return false;
            }
        }

        public void NotifyVideoOwnerMessage(nint ownerHwnd, int msg, nint wParam, nint lParam)
        {
            if (_choice != VideoRendererChoice.MPCVR) return;
            if (_videoWindow == null) return;

            try { _videoWindow.NotifyOwnerMessage(ownerHwnd, msg, wParam, lParam); }
            catch (InvalidComObjectException) { _videoWindow = null; }
            catch (COMException ex)
            {
                if ((uint)ex.HResult != 0x80004005)
                    Dbg.Warn($"MPCVR NotifyOwnerMessage msg=0x{msg:X} COM EX: {ex.Message}");
            }
            catch { }
        }

        private bool TryUpdateIVideoWindow(nint ownerHwnd, int w, int h)
        {
            try
            {
                if (_videoWindow == null)
                    return false;
                if (TryPlaceMadVrStereo(ownerHwnd, w, h))
                    return true;

                bool configure = _choice != VideoRendererChoice.MADVR ||
                    !ReferenceEquals(_configuredMadVrWindow, _videoWindow) || _configuredMadVrOwner != ownerHwnd;
                if (configure)
                {
                    CheckWindowControlResult(_videoWindow.put_Owner(ownerHwnd), "put_Owner");
                    CheckWindowControlResult(_videoWindow.put_MessageDrain(ownerHwnd), "put_MessageDrain");
                    _lastOwnerHwnd = ownerHwnd;
                    try { _videoWindow.put_BorderColor(0x000000); } catch { }

                    const int WS_CHILD = 0x40000000;
                    const int WS_CLIPSIBLINGS = 0x04000000;
                    const int WS_CLIPCHILDREN = 0x02000000;
                    CheckWindowControlResult(_videoWindow.put_WindowStyle((WindowStyle)(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN)), "put_WindowStyle");
                    try { _videoWindow.put_FullScreenMode(OABool.False); } catch { }
                    try { _videoWindow.put_AutoShow(OABool.False); } catch { }
                    if (_choice == VideoRendererChoice.MADVR)
                    {
                        _configuredMadVrWindow = _videoWindow;
                        _configuredMadVrOwner = ownerHwnd;
                    }
                }

                // Con il dimensionamento dell'immagine la finestra del renderer e' piu' piccola dell'host e centrata.
                var area = ScaledVideoClient(Math.Max(2, w), Math.Max(2, h));
                Rectangle videoDest = new Rectangle(0, 0, area.Width, area.Height);
                if (_choice == VideoRendererChoice.MPCVR)
                    TryUpdateMpcvrBasicVideoPosition(area.Width, area.Height, out videoDest);
                else if (_choice == VideoRendererChoice.MADVR && (_cropAspect > 0 || _basicVideoCropApplied) && TryGetBasicVideoController() is { } madVrVideo &&
                         TryGetVideoSizeForLayout(out int cropFrameW, out int cropFrameH) && cropFrameW > 0 && cropFrameH > 0)
                {
                    ApplyBasicVideoCrop(madVrVideo, cropFrameW / (double)cropFrameH);
                    try
                    {
                        if (_basicVideoCropApplied)
                        {
                            var fitted = CalculateWindowedVideoDestination(area.Width, area.Height, cropFrameW, cropFrameH);
                            madVrVideo.SetDestinationPosition(fitted.Left, fitted.Top, fitted.Width, fitted.Height);
                        }
                        else { madVrVideo.SetDefaultDestinationPosition(); madVrVideo.SetDestinationPosition(0, 0, area.Width, area.Height); }
                    }
                    catch (Exception ex) { Dbg.Warn("[CROP] madVR destination: " + ex.Message); }
                }
                videoDest.Offset(area.Location);
                w = area.Width; h = area.Height;

                // La finestra resta grande quanto l'area: con MPC VR il ritaglio si ottiene mascherandola sul riquadro
                // visibile (stringerla non basta, il renderer si riallarga da solo).
                var windowRect = area;
                if (_choice == VideoRendererChoice.MPCVR && (_mpcvrCropWindow || _mpcvrRegionApplied))
                {
                    try
                    {
                        nint child = FindWindowEx(ownerHwnd, 0, null, null);
                        if (child != 0)
                        {
                            if (_mpcvrCropWindow)
                            {
                                var visible = videoDest; visible.Offset(-area.Left, -area.Top);
                                SetWindowRgn(child, CreateRectRgn(visible.Left, visible.Top, visible.Right, visible.Bottom), true);
                                _mpcvrRegionApplied = true;
                            }
                            else { SetWindowRgn(child, 0, true); _mpcvrRegionApplied = false; }
                        }
                    }
                    catch (Exception ex) { Dbg.Warn("[CROP] MPC VR mask: " + ex.Message); }
                }
                CheckWindowControlResult(_videoWindow.SetWindowPosition(windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height), "SetWindowPosition");
                if (configure || _appliedMadVrPlacement == null)
                    CheckWindowControlResult(_videoWindow.put_Visible(OABool.True), "put_Visible");
                if (_choice == VideoRendererChoice.MPCVR)
                {
                    const int WM_SIZE = 0x0005;
                    const int WM_PAINT = 0x000F;
                    try { _videoWindow.NotifyOwnerMessage(ownerHwnd, WM_SIZE, nint.Zero, PackSizeLParam(Math.Max(2, windowRect.Width), Math.Max(2, windowRect.Height))); } catch { }
                    try { _videoWindow.NotifyOwnerMessage(ownerHwnd, WM_PAINT, nint.Zero, nint.Zero); } catch { }
                    RequestMpcvrRedraw();
                }
                _lastDest = new MFRect(videoDest.Left, videoDest.Top, videoDest.Right, videoDest.Bottom);
                if (_choice == VideoRendererChoice.MPCVR && _mpcvrWindowUpdateLogCount < 4)
                {
                    _mpcvrWindowUpdateLogCount++;
                    Dbg.Log($"MPCVR IVideoWindow host set: owner=0x{ownerHwnd.ToInt64():X}, window={w}x{h}, videoDest={videoDest.Width}x{videoDest.Height}@{videoDest.Left},{videoDest.Top}", Dbg.LogLevel.Info);
                }
                return true;
            }
            catch (System.Runtime.InteropServices.InvalidComObjectException)
            {
                _videoWindow = null;
                return false;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Dbg.Warn("UpdateVideoWindow (IVideoWindow) COM EX: " + ex.Message);
                if (_choice == VideoRendererChoice.MPCVR)
                    _videoWindow = null;
                return false;
            }
            catch (Exception ex)
            {
                Dbg.Warn("UpdateVideoWindow (IVideoWindow) EX: " + ex.Message);
                if (_choice == VideoRendererChoice.MPCVR)
                    _videoWindow = null;
                return false;
            }
        }

        private void CheckWindowControlResult(int hr, string operation)
        {
            // MPCVR exposes IVideoWindow but leaves some setters unimplemented.
            // They were intentionally best-effort in the working hosted path;
            // treating E_NOTIMPL as fatal prevented later positioning/redraw.
            if (_choice == VideoRendererChoice.MPCVR && hr == unchecked((int)0x80004001))
            {
                if (_mpcvrWindowUpdateLogCount < 4)
                    Dbg.Log($"MPCVR IVideoWindow {operation}: E_NOTIMPL ignored.", Dbg.LogLevel.Verbose);
                return;
            }

            DsError.ThrowExceptionForHR(hr);
        }

        private static nint PackSizeLParam(int width, int height)
        {
            unchecked
            {
                return (nint)((width & 0xFFFF) | ((height & 0xFFFF) << 16));
            }
        }
    }
}


