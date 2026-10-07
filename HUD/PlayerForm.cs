#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using FFmpeg.AutoGen;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    // ======= UI principale =======
    public sealed partial class PlayerForm : Form
    {
        private Panel? _pairBanner;

        // Remote command scope: used to suppress HUD wake when commands arrive from the Web Remote.
        // (Exception: timeline scrub/seek is allowed to wake the HUD.)
        private int _remoteCommandDepth = 0;
        private bool IsRemoteCommandActive => Volatile.Read(ref _remoteCommandDepth) > 0;
        private void BeginRemoteCommandScope() => Interlocked.Increment(ref _remoteCommandDepth);
        private void EndRemoteCommandScope()
        {
            try { Interlocked.Decrement(ref _remoteCommandDepth); }
            catch { /* best-effort */ }
        }

        // Track whether the latest DPAD action came from the Web Remote (true)
        // or from the local keyboard arrows/enter (false). Used to gate features
        // like the on-screen keyboard.
        private bool _lastDpadFromRemote = false;

        // Nota: non usiamo un IMessageFilter globale per il mouse.
        // In alcune configurazioni ha causato flicker e scroll scattoso.

        private static Font SafeSemibold(float size)
        {
            try { return global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point); }
            catch { return global::CinecorePlayer2025.AppFonts.Create("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point); }
        }

        private static Font SafeRegular(float size)
        {
            try { return global::CinecorePlayer2025.AppFonts.Create("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point); }
            catch { return SystemFonts.DefaultFont; }
        }
        private Panel _stack = null!;
        private Panel _videoHost = null!;
        private Panel _audioMetersHost = null!;
        private HudOverlay _hud = null!;
        private RemoteOsdOverlay _remoteOsd = null!;
        private InfoOverlay _infoOverlay = null!;
        private NetflixModePage _netflixModePage = null!;
        private SettingsHudPage _settingsHudPage = null!;
        private Label _lblStatus = null!;
        private AudioOnlyOverlay _audioOnlyBanner = null!;
        // === Audio-only meters (LiveCharts) ===
        private AudioMetersLiveCharts? _audioMeters;
        private LoopbackSampler? _audioSampler;
        private BitstreamPcmAnalysisSampler? _bitstreamAnalysisSampler;
        private readonly MusicLyricsService _lyricsService = new();
        private CancellationTokenSource? _lyricsCts;
        private string? _musicPresentationPath;
        // Avvio ritardato dell'analisi PCM in bitstream: evita di far partire ffmpeg
        // mentre DirectShow/madVR sta ancora costruendo il graph.
        private System.Windows.Forms.Timer? _bitstreamAnalysisStartTimer;
        private int _bitstreamAnalysisStartSerial = 0;
        // Generazione del riavvio dei meter al rientro dal PiP. Impedisce che un
        // callback accodato da un vecchio rientro riattivi l'analisi sul media
        // successivo o dopo uno stop.
        private int _pipAudioMeterRestartSerial = 0;
        private System.Windows.Forms.Timer? _pipAudioMeterRestartTimer;

        private ContextMenuStrip _menu = null!;
        private Panel _rootLayout = null!;
        private IPlaybackEngine? _engine;
        private readonly object _engineTeardownSync = new();
        private Task _engineTeardownTask = Task.CompletedTask;
        private string? _currentPath;
        // Categoria libreria dell'ultimo media aperto (serve per il titolo HUD normalizzato).
        private string? _currentLibraryCategory;
        // URL audio separato (es. YouTube DASH) trovato dal WebMediaResolver
        private string? _currentWebAudioUrl;
        private int _currentWebVideoBitrateKbps;
        private int _currentWebAudioBitrateKbps;
        private int _currentWebWidth;
        private int _currentWebHeight;
        private double _currentWebFps;
        private string? _currentWebVideoCodec;
        private string? _currentWebAudioCodec;
        private MediaProbe.Result? _info;

        private string? _selectedAudioRendererName;
        private bool _selectedRendererLooksHdmi;
        private Stereo3DMode _stereo = Stereo3DMode.None;
        private bool _stereoAutoEnabled = true;
        private Stereo3DAutoDetector.DetectionResult? _lastStereoAutoDetection;
        private HDRMode _hdr = HDRMode.Auto;
        private bool _scrubActive = false;
        private double _scrubPending = -1;
        private double _timelineUiPositionOverride = -1;
        private DateTime _timelineUiOverrideUntilUtc = DateTime.MinValue;

        private double _duration;
        private double _lastKnownPlaybackPosition;
        private bool _paused;
        private bool _currentMediaHasVideo = false;

        // ===== Extras (Placeholder pre-film + Pre-roll demo) =====
        private PausePlaceholderOverlay _pausePlaceholder = null!;
        // Placeholder gate: se attivo, quando apri un NUOVO film (dopo il primo) prima mostra il placeholder.
        // Il film parte solo quando premi Play.
        private bool _pausePlaceholderEnabled = false;
        private List<NetflixModeItem>? _netflixModeItemsCache;
        private string _netflixModeItemsCacheSignature = "";
        private int _netflixModeWarmupRunning;
        private string _pausePlaceholderFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "PausePlaceholders");
        // Placeholder selezionato (se null → random)
        private string? _pausePlaceholderPath;

        // Placeholder gate state
        private bool _preOpenPlaceholderGateActive = false;
        private string? _pendingPathAfterPlaceholderGate;
        private double _pendingResumeAfterPlaceholderGate;
        private bool _pendingStartPausedAfterPlaceholderGate;

        // True after we've successfully opened at least one media in this session.
        // Used to interpret "film successivo".

        // Alcuni renderer (es. madVR) disegnano sopra agli overlay: per mostrare davvero
        // il placeholder in pausa dobbiamo “staccare” il video e riattaccarlo dopo.
        private Panel _videoDetachHost = null!;
        private bool _videoDetachedForPausePlaceholder = false;

        // === HUD visibility + cursor hide (mouse idle) ===
        private const int HUD_IDLE_HIDE_MS = 3000;
        private readonly System.Windows.Forms.Timer _uiIdleTimer = new() { Interval = 150 };
        private DateTime _lastMouseMoveUtc = DateTime.UtcNow;
        private DateTime _lastHudActivityUtc = DateTime.UtcNow;
        private bool _cursorHidden = false;
        private const int HUD_WAKE_POLL_DEADZONE_PX = 3;
        private const int HUD_WAKE_AFTER_AUTOHIDE_PX = 18;
        private Point _hudWakeAnchorPos = Point.Empty;
        private bool _hudWakeNeedsIntentionalMove = false;
        private DateTime _suppressHudWakeUntilUtc = DateTime.MinValue;

        private bool _preRollEnabled = false;
        private string? _preRollDemoPath;
        private string _preRollDemoFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Demos");
        private bool _playingPreRoll = false;
        private bool _suppressPreRollOnce = false;
        private string? _pendingMainPathAfterPreRoll;
        private double _pendingMainResumeAfterPreRoll;
        private bool _pendingMainStartPausedAfterPreRoll;

        // Passaggio demo → film: sopprimi loading “cinematografico” una sola volta.
        private bool _suppressVideoLoadingOnce = false;
        private int _suppressVideoLoadingSerial = 0;

        // Riferimenti menu (per aggiornare check state quando seleziono un file)
        private ToolStripMenuItem? _miPausePlaceholderEnable;
        private ToolStripMenuItem? _miPreRollEnable;
        private ToolStripMenuItem? _miCinemaMode;
        private ToolStripMenuItem? _miWledEnable;
        private ToolStripMenuItem? _miPausePlaceholderUseTmdbBackdrop;
        private bool _syncingCinemaModeUi = false;

        // ===== Cinema mode / WLED / backdrop automatico =====
        private bool _cinemaModeEnabled = false;
        private bool _pausePlaceholderUseTmdbBackdrop = false;
        private bool _wledEnabled = false;
        private string _wledBaseUrl = "http://wled.local";
        private const int WLED_FADE_MS = 900;
        private const int WLED_FADE_OUT_MS = 4700;
        private const int WLED_FADE_STEP_MS = 220;
        private const int WLED_DEFAULT_BRI = 255;
        private const int WLED_MIN_BRI = 1;
        private const int WLED_PAUSE_RESTORE_DELAY_MS = 2000;
        private static readonly HttpClient _wledHttp = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        private CancellationTokenSource? _wledPauseRestoreCts;
        private CancellationTokenSource? _wledTransitionCts;
        private DateTime _wledLastCommandUtc = DateTime.MinValue;
        private bool? _wledLastSentOn = null;
        private bool? _wledLastRequestedOn = null;
        private int _wledLastBrightness = WLED_DEFAULT_BRI;
        private bool _wledRestoreOnExit = false;
        private bool _wledInitialStateCaptured = false;
        private bool _wledInitialOn = true;
        private int _wledInitialBrightness = WLED_DEFAULT_BRI;
        private bool _suppressNextWledRestore = false;
        private CancellationTokenSource? _placeholderBackdropCts;
        private Image? _placeholderBrandLogo;
        private bool _suspendLibraryHoverAnchorUntilMouseMove = false;
        private Point _libraryHoverAnchorWakePos = new Point(int.MinValue, int.MinValue);

        // Menu contestuale aperto/in arrivo: blocca l'auto-hide HUD senza interferire con l'apertura del menu.
        private bool _contextMenuActive = false;
        private bool _contextMenuPending = false;
        private bool _mpcvrOverlayDemotedForContextMenu = false;
        private bool _contextMenuTopMostSuspended = false;
        private bool _contextMenuRestoreFormTopMost = false;
        private ContextOverlayMenuForm? _contextOverlayMenu;
        private bool _preparingCustomContextMenu = false;
        private DateTime _lastMpcvrContextMenuRequestUtc = DateTime.MinValue;
        private DateTime _lastPlaybackContextMenuShowUtc = DateTime.MinValue;
        private bool _openingPlaybackQueueEditor = false;

        // Fullscreen: guard contro re-entrancy (evita glitch grafici)
        private bool _fullscreenTransitioning = false;
        private DateTime _fullscreenTransitionStartedUtc = DateTime.MinValue;
        private int _suspendFullscreenActivationKeepAlive = 0;
        private int _prevWindowStyle = 0;
        private bool _prevControlBox = true;
        private bool _prevMinimizeBox = true;
        private bool _prevMaximizeBox = true;

        // DirectShow: notifica fine riproduzione (EC_COMPLETE) → ritorno libreria affidabile
        private IMediaEventEx? _graphEvents;
        private const int WM_APP = 0x8000;
        private const int WM_GRAPHNOTIFY = WM_APP + 0x1A15;
        private const int WM_MPCVR_SWITCH_FULLSCREEN = WM_APP + 4096;

        // Preferenza lingua per la selezione automatica dei sottotitoli forzati
        private string? _preferredSubtitleLangKey;
        private bool _subtitleAutoForcedMode = false;


        // Fallback: alcuni renderer (o finestre layered/click-through) non propagano MouseMove ai controlli WinForms.
        // Polliamo la posizione del mouse per riattivare l'HUD quando necessario (es. “Apri con…” da Esplora risorse).
        private readonly System.Windows.Forms.Timer _hudWakePollTimer = new() { Interval = 85 };
        private Point _hudWakeLastMousePos;

        // Auto ritorno alla libreria quando il contenuto termina (EOF)
        private bool _autoReturnToLibraryOnEnd = true;
        private bool _returnToCinematicHomeOnPlaybackExit = false;
        private bool _returnToSpotlightOnPlaybackExit;
        private string? _returnToCinematicWebSourceOnPlaybackExit;
        private bool _endTriggered = false;
        private DateTime _endCandidateSinceUtc = DateTime.MinValue;

        // Volume di backup per mute/unmute da telecomando remoto (web remote)
        private float _remoteVolBeforeMute = 1f;


        // Remote scan (long-press skip): 0.5x -> 1x -> 2x -> 4x
        private System.Windows.Forms.Timer? _remoteScanTimer;
        private int _remoteScanDir = 0; // -1 back, +1 forward
        private int _remoteScanSpeedIdx = 0;
        private static readonly double[] REMOTE_SCAN_SPEEDS = new[] { 0.5, 1.0, 2.0, 4.0 };
        private const double REMOTE_SCAN_BASE_SECS_PER_SEC = 10.0;
        private readonly Thumbnailer _thumb = new();
        private CancellationTokenSource? _thumbCts;
        private int _previewWorkerRunning;
        private bool _previewOverlayInitialized;
        // === Timeline preview (latest-wins + cache) ===
        private int _previewReqSerial = 0;
        private double _previewReqSeconds = 0;
        // Larghezza di un fotogramma dell'anteprima (la striscia ne ha cinque): prima 176, troppo piccoli.
        private const int TIMELINE_PREVIEW_W = 256;
        private const int TIMELINE_CACHE_CAPACITY = 240;
        // Stable storyboard slots are reusable while the pointer crosses nearby pixels.
        private const double TIMELINE_CACHE_QUANTUM_SEC = 2.0;
        private readonly PreviewCache _previewCache =
            new PreviewCache(TIMELINE_CACHE_CAPACITY, TIMELINE_CACHE_QUANTUM_SEC);

        private FormWindowState _prevState; private FormBorderStyle _prevBorder; private Rectangle _prevBounds;
        private readonly OverlayHostForm _overlayHost;
        private readonly PopupOverlayHost _mpcvrOverlayPopup;
        private InlineOverlayPanel? _overlayInlineHost;
        private InlineOverlayPanel? _mpcvrChildOverlayHost;
        private MpcvrBitmapOverlayForm? _mpcvrBitmapOverlayHost;
        private MpcvrOverlayStagingForm? _mpcvrOverlayStagingHost;
        private Region? _mpcvrChildOverlayRegion;
        private bool _useInlineOverlay;
        private readonly Color _mpcvrMixerOverlayKey = Color.FromArgb(1, 1, 1);
        private DateTime _lastMpcvrMixerOverlayUpdateUtc = DateTime.MinValue;
        private DateTime _mpcvrMixerOverlayRetryUtc = DateTime.MinValue;
        private bool _mpcvrMixerOverlayUpdateActive;
        private bool _mpcvrDedicatedLayeredClearedRenderer;

        private Rectangle _lastVideoDestInForm = Rectangle.Empty;

        private bool _enableUpscaling = false;
        private string _videoUpscalingBackend = "off";
        private int _mpcvrSuperResolutionMode = 3;
        private MadVrCategoryPreset _madVrUpscalePreset = MadVrCategoryPreset.RendererDefault;
        private int _targetFps = 0;
        private bool _preferBitstreamUi = true;
        private string _uiLanguage = global::CinecorePlayer2025.Utilities.AppLanguage.SystemDefault;
        private Color _uiAccentColor = Theme.Accent;
        private Color _uiAccentSoftColor = Theme.AccentSoft;
        private Color _uiSelectionColor = Theme.Selection;
        private Color _uiBorderAccentColor = Theme.BorderAccent;
        private Color _uiPanelColor = Theme.Panel;
        private Color _uiCardColor = Theme.Card;
        private Color _uiNavColor = Theme.Nav;
        private bool UiEnglish => string.Equals(_uiLanguage, "en", StringComparison.OrdinalIgnoreCase);
        private string Tx(string italian, string english) => UiEnglish ? english : italian;
        private string LocalizePlaybackStatus(string? status)
        {
            string value = status?.Trim() ?? string.Empty;
            if (!UiEnglish || value.Length == 0)
                return value;

            return value
                .Replace("Riproduzione", "Playing", StringComparison.OrdinalIgnoreCase)
                .Replace("Pausa", "Paused", StringComparison.OrdinalIgnoreCase)
                .Replace("Pronto", "Ready", StringComparison.OrdinalIgnoreCase)
                .Replace("Risoluzione stream", "Resolving stream", StringComparison.OrdinalIgnoreCase)
                .Replace("Caricamento", "Loading", StringComparison.OrdinalIgnoreCase)
                .Replace("Completato", "Completed", StringComparison.OrdinalIgnoreCase);
        }
        private readonly RationalDisplayModeSwitcher _refresh = new();
        // === Preferenza UI per PCM/Bitstream ===
        private enum AudioOutPref { Auto, ForcePcm }
        private AudioOutPref _audioOutPref = AudioOutPref.Auto; // default: Auto

        private static readonly VRChoice[] ORDER_HDR = { VRChoice.MADVR, VRChoice.MPCVR };
        private static readonly VRChoice[] ORDER_SDR = { VRChoice.EVR };

        private ToolStripMenuItem _mAudioLang = null!;
        private ToolStripMenuItem _mSubtitles = null!;
        private ToolStripMenuItem _mAudioOut = null!;
        private ToolStripMenuItem _mChapters = null!;
        private ToolStripMenuItem? _mQueueMenuItem;
        private ToolStripMenuItem? _mLoopTrack;
        private ToolStripMenuItem? _mPipMode;
        private PipMiniPlayerForm? _pipForm;
        private bool _pipVideoExclusiveSuppressed;
        private bool _pipModeActive;
        private DateTime _pipShownUtc = DateTime.MinValue;
        private Form? _playbackQueueEditorForm;
        private IMessageFilter? _playbackKeyboardFilter;
        private IMessageFilter? _inputModeFilter;
        private string? _singleTrackLoopPath;
        private bool _singleTrackLoopEnabled;

        private DateTime _vPrevWhen = DateTime.MinValue;
        private int _videoBitrateNowKbps = 0;

        private CinematicMediaLibraryPage? _cinematicLibraryPage;
        private readonly List<string> _playbackQueue = new();
        private readonly List<string> _playbackQueueHistory = new();
        private readonly Random _playbackQueueRandom = new();
        private int _playbackQueueIndex = -1;
        private bool _playbackQueueShuffleMode = false;
        private bool _playbackQueueSessionActive = false;
        private bool _asyncStopMetersOnce;
        private bool _nextOpenBelongsToPlaybackQueue = false;
        private bool _playbackQueueTransitionInProgress = false;
        private readonly List<string> _pendingExternalOpenPaths = new();
        private System.Windows.Forms.Timer? _externalOpenBatchTimer;
        private int _rendererSyntheticKeyForwarding = 0;
        private int _mpcvrChildWindowLogCount = 0;
        private int _mpcvrChildOverlayLogCount = 0;
        private readonly Dictionary<IntPtr, IntPtr> _mpcvrChildWndProcs = new();
        private readonly NativeWndProc _mpcvrChildWndProc;
        private readonly PlaybackQueueStateStore _playbackQueueStore = new();
        private const int HUD_HOTZONE_H = 160;
        private IntroOutroEpisodeMarkers? _currentIntroOutroMarkers;
        private bool _skipIntroPromptConsumed;
        private bool _nextEpisodePromptConsumed;
        private bool _lastIntroOutroPromptSkipVisible;
        private bool _lastIntroOutroPromptNextVisible;
        private DateTime _lastIntroOutroPromptLogUtc = DateTime.MinValue;
        private bool _mpcvrExclusiveMode;

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ShowCursor(bool bShow);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);
        private delegate IntPtr NativeWndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }
        private const int GWL_WNDPROC = -4;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WM_SETICON = 0x0080; private const int ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2;
        // ===== Process I/O (per bitrate container NOW) =====
        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        // === HDR profiles (UI) ===
        private enum HdrUiProfile { Auto, RtxVideoHdr, Passthrough, ToneMapSdr, LutSdr }
        private HdrUiProfile _hdrProfile = HdrUiProfile.Auto;
        private DateTime _lastRtxHdrProbeUtc = DateTime.MinValue;
        private bool _lastRtxHdrProbeResult;
        private bool _lutWarned = false;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

        private Icon? _iconBig; private Icon? _iconSmall;

        private Action<string>? _engineStatusHandler;
        private Action<double>? _engineProgressHandler;
        private Action? _engineUpdateHandler;
        private Action<bool>? _engineBitstreamHandler;

        private long _ioPrevBytes = 0;
        private DateTime _ioPrevWhen = DateTime.MinValue;
        private int _containerBitrateNowKbps = 0;
        private int _audioBitrateNowKbps = 0;
        private volatile bool _bitstreamNow = false;
        // Debug: ultimo stato loggato di IPlaybackEngine.IsBitstreamActive()
        private bool _lastIsBsLogged = false;
        // Usa SOLO l’engine per sapere se siamo in bitstream
        private bool IsBitstream() => _engine?.IsBitstreamActive() ?? false;

        // --- Packet-level bitrate sampler (FFmpeg) ---
        // Nota: su YouTube spesso audio e video arrivano su URL separati (DASH).
        // Campioniamo quindi:
        // - _pktRate       => stream principale (di solito video)
        // - _pktRateAudio  => eventuale stream audio separato
        private PacketRateSampler _pktRate = new();
        private PacketRateSampler _pktRateAudio = new();
        private bool _pktRateOk = false;
        private bool _pktRateAudioOk = false;
        private DateTime _lastPktSample = DateTime.MinValue;
        private DateTime _suppressPacketSamplesUntilUtc = DateTime.MinValue;
        private int _packetRateSampleActive = 0;
        private const int PACKET_RATE_SAMPLE_INTERVAL_MS = 900;
        // Timestamp ultimo campione valido “ora” (per non sovrascrivere col fallback)
        private DateTime _aNowTs = DateTime.MinValue, _vNowTs = DateTime.MinValue;

        // === Caricamento media (overlay nero + spinner) ===
        private VideoLoadingMask _videoLoading = null!;
        private readonly ThreadedVideoLoadingPresenter _threadedVideoLoading = new();
        private CancellationTokenSource? _openCts;
        private int _openSerial;

        // Web resolver (YouTube/URL) su thread STA dedicato: evita freeze UI e mantiene compatibilità
        // con componenti browser/COM che richiedono STA.
        private static readonly StaInvoker _resolverSta = new StaInvoker();

        // === Nome originale del media aperto (per progettini futuri) ===
        // - Per file locali: Path.GetFileName
        // - Per URL: best-effort (titolo non disponibile offline)
        private string? _originalVideoName;
        public string? OriginalVideoName => _originalVideoName;

        // === RUNNING AVERAGES (media live) ===
        private const int AVG_PUBLISH_SEC = 3;

        private DateTime _avgLastPublish = DateTime.MinValue;
        private DateTime _avgLastTs = DateTime.MinValue;   // ultimo timestamp campione
        private double _avgAudioBitSec = 0;               // somma (kbps * secondi)
        private double _avgVideoBitSec = 0;               // somma (kbps * secondi)
        private double _avgDurSec = 0;                    // somma dei Δt

        private double _audioAvgLiveKbps = 0;
        private double _videoAvgLiveKbps = 0;
        private DateTime _bitstreamLastTrue = DateTime.MinValue;

        // === TIMER per aggiornare le statistiche dell'overlay a cadenza fissa ===
        private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 500 };
        private bool _statsTimerInitialized = false;
        // --- controllo remoto web ---
        private RemoteServer? _remote;

        // ultimo snapshot di stato che mandiamo al telefono
        private RemoteState _remoteSnapshot = new RemoteState();
        private object _remoteCompanionSnapshot = new { queue = Array.Empty<object>(), library = Array.Empty<object>() };
        private object _remoteLibraryPayload = Array.Empty<object>();
        private volatile bool _remoteLibraryNetwork;
        private volatile bool _remoteCompanionSyncReady;
        private DateTime _remoteCompanionSnapshotUtc = DateTime.MinValue;
        private int _remoteCompanionRefreshRunning;
        private string _watchHistorySessionPath = string.Empty;
        private DateTime _watchHistoryNextCaptureUtc = DateTime.MinValue;

        // lettura "sicura" per il server (thread diverso)
        private RemoteState GetRemoteState() => System.Threading.Volatile.Read(ref _remoteSnapshot);

        private object GetRemoteCompanionData()
        {
            try
            {
                // RemoteServer.Start() viene avviato dal thread UI. Un telefono già
                // collegato può interrogare /api/companion prima che Start() ritorni:
                // un Invoke sincrono in quella finestra temporale bloccherebbe entrambi
                // i thread e impedirebbe perfino la comparsa della finestra principale.
                if (!_remoteCompanionSyncReady)
                    return Volatile.Read(ref _remoteCompanionSnapshot);

                // Building the full phone library walks every movie, season and album
                // on the UI thread. The catalogue changes rarely; polling it every few
                // seconds made the desktop app stutter whenever the remote was open.
                if (DateTime.UtcNow - _remoteCompanionSnapshotUtc < TimeSpan.FromSeconds(10))
                    return Volatile.Read(ref _remoteCompanionSnapshot);

                QueueRemoteCompanionRefresh();
                return Volatile.Read(ref _remoteCompanionSnapshot);
            }
            catch { return Volatile.Read(ref _remoteCompanionSnapshot); }
        }

        private void RefreshRemoteLibraryAfterSourceChange()
        {
            foreach (int delay in new[] { 0, 1500, 4000, 8000 })
            {
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    try
                    {
                        if (IsDisposed || !IsHandleCreated || _closingForExit) return;
                        BeginInvoke(new Action(() => { _remoteCompanionSnapshotUtc = DateTime.MinValue; QueueRemoteCompanionRefresh(); }));
                    }
                    catch { }
                }, TaskScheduler.Default);
            }
        }

        private void QueueRemoteCompanionRefresh()
        {
            if (!_remoteCompanionSyncReady || _closingForExit || IsDisposed)
                return;
            if (Interlocked.CompareExchange(ref _remoteCompanionRefreshRunning, 1, 0) != 0)
                return;

            _ = Task.Run(BuildRemoteLibraryPayload).ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    Volatile.Write(ref _remoteLibraryPayload, task.Result);
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try { RefreshRemoteCompanionSnapshot(); }
                            finally { Interlocked.Exchange(ref _remoteCompanionRefreshRunning, 0); }
                        }));
                        return;
                    }
                }
                catch { }
                Interlocked.Exchange(ref _remoteCompanionRefreshRunning, 0);
            }, TaskScheduler.Default);
        }

        private object BuildRemoteLibraryPayload()
        {
            object MapRemoteItem(RemoteLibraryItemView item) => new
            {
                path = item.Path,
                title = item.Title,
                subtitle = item.Subtitle,
                artPath = item.ArtPath,
                kind = item.Kind,
                seasonNumber = item.SeasonNumber,
                episodeNumber = item.EpisodeNumber,
                children = item.Children.Select(MapRemoteItem).ToList()
            };

            IReadOnlyList<RemoteLibraryCategoryView> rawLibrary = Array.Empty<RemoteLibraryCategoryView>();
            var page = _cinematicLibraryPage;
            if (_remoteLibraryNetwork && page != null)
            {
                // Il catalogo del server di rete (lo stesso di Spotlight in modalita' Rete). Se il server non
                // risponde l'elenco resta vuoto: non si mostrano i film del computer al posto di quelli di rete.
                try
                {
                    string key = _spotlightServerKey;
                    if (string.IsNullOrWhiteSpace(key))
                        key = page.GetSpotlightServerChoices().FirstOrDefault().Key ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        rawLibrary = page.BuildRemoteNetworkLibraryAsync(key, limit.Token).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex) { Dbg.Warn("[REMOTE] network library: " + ex.Message); }
            }
            else
                rawLibrary = page?.GetRemoteLibrarySnapshot() ?? Array.Empty<RemoteLibraryCategoryView>();
            return rawLibrary.Select(group => new
            {
                key = group.Key,
                label = group.Label,
                count = group.Count,
                items = group.Items.Select(MapRemoteItem).ToList()
            }).ToList();
        }

        private object RefreshRemoteCompanionSnapshot()
        {
            string MediaKind(string path)
            {
                string? category = PlaybackTitleHints.GetCategory(path) ?? _cinematicLibraryPage?.ResolvePlaybackCategoryForPath(path);
                if (category is "Music" or "Musica") return "music";
                if (category is "Photos" or "Foto") return "photo";
                if (System.Text.RegularExpressions.Regex.IsMatch(path, @"(?i)\.(mp3|flac|wav|m4a|aac|ogg|opus|wma|aiff)(?:[?#]|$)")) return "music";
                if (ImagePlaybackEngine.IsImageFile(path)) return "photo";
                return "movie";
            }
            try
            {
                var queue = GetPlaybackQueueSnapshotItems().Select(item => new
                {
                    path = item.Path,
                    title = item.Label,
                    current = item.IsCurrent,
                    kind = MediaKind(item.Path),
                    index = item.Index
                }).ToList();
                object library = Volatile.Read(ref _remoteLibraryPayload);

                var streams = _engine?.EnumerateStreams().ToList() ?? new List<DsStreamItem>();
                var audioTracks = streams.Where(stream => stream.IsAudio).Select((stream, ordinal) => new
                {
                    id = stream.GlobalIndex,
                    label = SubtitleNameNormalizer.NormalizeAudioTrackName(stream.Name, ordinal + 1),
                    selected = stream.Selected
                }).ToList();
                var subtitleTracks = streams.Where(stream => stream.IsSubtitle && !DirectShowUnifiedEngine.IsSubtitleOffOption(stream.Name)).Select((stream, ordinal) => new
                {
                    id = stream.GlobalIndex,
                    label = SubtitleNameNormalizer.NormalizeSubtitleTrackName(stream.Name, ordinal + 1),
                    selected = stream.Selected
                }).ToList();

                object snapshot = new
                {
                    queue,
                    library,
                    librarySource = _remoteLibraryNetwork ? "network" : "local",
                    audioTracks,
                    subtitleTracks,
                    subtitlesOff = !subtitleTracks.Any(track => track.selected)
                };
                Volatile.Write(ref _remoteCompanionSnapshot, snapshot);
                _remoteCompanionSnapshotUtc = DateTime.UtcNow;
                return snapshot;
            }
            catch { return Volatile.Read(ref _remoteCompanionSnapshot); }
        }

        private async void ScheduleRemoteTrackSnapshotRefresh(int openSerial, IPlaybackEngine playbackEngine)
        {
            int[] delaysMs = { 0, 180, 420, 850, 1500 };
            foreach (int delayMs in delaysMs)
            {
                if (delayMs > 0)
                {
                    try { await Task.Delay(delayMs); }
                    catch { return; }
                }

                if (_closingForExit || IsDisposed || _stopping ||
                    openSerial != Volatile.Read(ref _openSerial) ||
                    !ReferenceEquals(_engine, playbackEngine))
                    return;

                try { RefreshRemoteCompanionSnapshot(); } catch { }
            }
        }

        /// <summary>
        /// A title chosen on the phone opens exactly like a click on its card. Calling OpenPath
        /// directly skipped the library-to-playback transition: the library stayed in front of
        /// the video (sound without picture) and network titles lost their category and title.
        /// </summary>
        private void OpenFromRemoteLibrary(string path, string? title)
        {
            Dbg.Log($"[REMOTE] open '{path}' (spotlight={_netflixModePage?.Visible == true}, library={_cinematicLibraryPage?.Visible == true}, playing={_engine != null})");
            double? resume = null;
            try
            {
                var saved = PlaybackResumeStore.LoadAll().FirstOrDefault(entry => string.Equals(entry.MediaPath, path, StringComparison.OrdinalIgnoreCase));
                if (saved != null && saved.DurationSeconds > 0 && saved.PositionSeconds / saved.DurationSeconds is > 0.01 and < 0.95)
                    resume = saved.PositionSeconds;
            }
            catch { }

            if (_netflixModePage?.Visible == true)
            {
                if (!string.IsNullOrWhiteSpace(title)) PlaybackTitleHints.Set(path, title);
                try { HideSettingsHudPage(); } catch { }
                PlayLibraryRequestedPath(path, resume);
                return;
            }

            try { HideSettingsHudPage(); } catch { }
            if (_cinematicLibraryPage != null)
            {
                _cinematicLibraryPage.OpenFromRemote(path, title, resume);
                return;
            }
            if (!string.IsNullOrWhiteSpace(title)) PlaybackTitleHints.Set(path, title);
            PlayCinematicLibraryRequestedPath(path, resume);
        }

        internal void InvalidateRemoteLibrarySnapshot()
        {
            _remoteCompanionSnapshotUtc = DateTime.MinValue;
        }

        // pubblica uno snapshot nuovo (non muta proprietà sull'oggetto vecchio)
        private long _lastRemotePublishTick;

        private Func<string>? _computeHudTitle;
        private (string? Key, long Stamp, string Title, string? Subtitle, string? ArtPath) _displayInfoCache;

        /// <summary>
        /// Title, caption and artwork of the current media. They are metadata lookups and
        /// file checks, and were recomputed on every HUD paint and on every remote publish
        /// (four times a second during playback); they only change with the media or when
        /// late metadata arrives, so a short refresh interval is enough.
        /// </summary>
        private (string Title, string? Subtitle, string? ArtPath) CachedDisplayInfo()
        {
            string key = (_currentPath ?? string.Empty) + "|" + (_originalVideoName ?? string.Empty);
            long now = Environment.TickCount64;
            var cache = _displayInfoCache;
            if (cache.Key == key && now - cache.Stamp < 5000)
                return (cache.Title, cache.Subtitle, cache.ArtPath);

            string title = string.Empty;
            try { title = _computeHudTitle?.Invoke() ?? string.Empty; } catch { }
            string? subtitle = null, art = null;
            try { subtitle = _cinematicLibraryPage?.RemoteCaptionFor(_currentPath); } catch { }
            try
            {
                art = _cinematicLibraryPage?.RemoteArtworkFor(_currentPath);
                if (string.IsNullOrWhiteSpace(art) || !File.Exists(art))
                    art = ResolveRemoteArtworkPath(_currentPath);
            }
            catch { }
            _displayInfoCache = (key, now, title, subtitle, art);
            return (title, subtitle, art);
        }

        private void PublishRemoteState(double? positionOverride = null)
        {
            try
            {
                double pos = positionOverride ?? GetTimelinePositionForHud();
                double dur = GetTimelineDurationSeconds();

                // importantissimo: appena la durata diventa disponibile, aggiorna _duration
                if (_duration <= 0 && dur > 0) _duration = dur;

                // Senza nulla di aperto il telecomando non mostra un titolo: il titolo dell'HUD
                // ripiega sull'ultimo file letto dalla libreria (anteprime, analisi), e sul
                // telefono restava fisso il nome di un film mai avviato.
                bool hasMedia = _engine != null && !string.IsNullOrWhiteSpace(_currentPath);
                var display = hasMedia ? CachedDisplayInfo() : (Title: string.Empty, Subtitle: (string?)null, ArtPath: (string?)null);

                var st = new RemoteState
                {
                    Title = display.Title.Trim(),
                    HasPlayback = hasMedia && !IsPhotoMode,
                    Subtitle = display.Subtitle,
                    ArtPath = display.ArtPath,
                    Position = Math.Max(0, pos),
                    Duration = Math.Max(0, dur),
                    OutputHdr = IsRequestedOutputHdr(),
                    Is3D = _stereo != Stereo3DMode.None,
                    Renderer = RemoteRendererName,
                    RendererSelection = RemoteRendererSelection,
                    DefaultRenderer = RemoteDefaultRendererName,
                    HdrMode = RemoteHdrMode,
                    HdrAvailable = IsMadVrFeatureAvailable() || IsRtxVideoHdrAvailable(),
                    RtxHdrAvailable = IsRtxVideoHdrAvailable(),
                    StereoMode = RemoteStereoMode,
                    StereoAvailable = true,
                    Upscaling = _enableUpscaling,
                    UpscalingAvailable = IsMadVrFeatureAvailable() || (IsMpcVrFeatureAvailable() && HasNvidiaDriverPresent()),
                    UpscalingBackend = _videoUpscalingBackend,
                    UpscalingPreset = string.Equals(_videoUpscalingBackend, "mpcvr", StringComparison.OrdinalIgnoreCase)
                        ? _mpcvrSuperResolutionMode.ToString()
                        : ((int)_madVrUpscalePreset).ToString(),
                    MadVrAvailable = IsMadVrFeatureAvailable(),
                    MpcvrNvidiaUpscalingAvailable = IsMpcVrFeatureAvailable() && HasNvidiaDriverPresent(),
                    Bitstream = (_engine?.IsBitstreamActive() ?? false),
                    // Il tasto del telefono mostra "play" ogni volta che premerlo fa partire qualcosa:
                    // in pausa, senza riproduzione e davanti al placeholder pre-film.
                    Paused = _paused || !hasMedia || _preOpenPlaceholderGateActive,
                    Volume = Math.Clamp(_hud?.GetVolume() ?? 1f, 0f, 1f),
                    Muted = _hud?.IsMuted == true,
                    LightTheme = Theme.IsLight,
                    Language = _uiLanguage,
                    Spotlight = _netflixModePage?.Visible == true,
                    Accent = ColorToCss(Theme.Accent),
                    AccentSoft = ColorToCss(Theme.AccentSoft),
                    Panel = ColorToCss(Theme.Panel),
                    Card = ColorToCss(Theme.Card),
                    Nav = ColorToCss(Theme.Nav),
                    Text = ColorToCss(Theme.Text),
                    MutedText = ColorToCss(Theme.Muted)
                };

                System.Threading.Volatile.Write(ref _remoteSnapshot, st);
                _lastRemotePublishTick = Environment.TickCount64;
            }
            catch { }
        }

        private static string ColorToCss(Color color)
            => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        // --- Modalità foto ---
        private PhotoHudOverlay _photoHud = null!;
        private List<string> _imageFiles = new();
        private int _imageIndex = -1;
        private string? _imageFolderPath;
        private bool IsPhotoMode => _engine is ImagePlaybackEngine;
        // true se il media corrente è un file locale (non HTTP/stream)
        private bool _isLocalFile;

        private sealed class RendererHostPanel : Panel
        {
            public Action<Message>? OwnerMessage { get; set; }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_CLIPCHILDREN = 0x02000000;
                    const int WS_CLIPSIBLINGS = 0x04000000;

                    var cp = base.CreateParams;
                    cp.Style |= WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                try { OwnerMessage?.Invoke(m); } catch { }
            }
        }

        private AudioOnlyOverlay BuildAudioOnlyBanner() => new()
        {
            Dock = DockStyle.Fill,
            Visible = false,
            ImagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "audioOnly.png"),
            Caption = Tx("Solo audio", "Audio only")
        };

        private void LayoutAudioMetersHost()
        {
            try
            {
                if (_audioMetersHost == null)
                    return;

                int w = Math.Max(1, _audioMetersHost.ClientSize.Width);
                int h = Math.Max(1, _audioMetersHost.ClientSize.Height);
                _audioMetersHost.Padding = Padding.Empty;
                _audioMetersHost.BackColor = HUD.Theme.Panel;
                if (_audioMeters != null)
                {
                    _audioMeters.Dock = DockStyle.Fill;
                    if (_audioMetersHost.BackgroundImage != null)
                    {
                        var old = _audioMetersHost.BackgroundImage; _audioMetersHost.BackgroundImage=null; old.Dispose();
                    }
                }
            }
            catch { }
        }


        private static string GetWindowClassName(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                int len = GetClassName(hwnd, sb, sb.Capacity);
                if (len <= 0) return string.Empty;
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        private static IntPtr GetWindowLongPtrSafe(IntPtr hwnd, int index)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtr64(hwnd, index);
            return new IntPtr(GetWindowLong32(hwnd, index));
        }

        private static void SetWindowLongPtrSafe(IntPtr hwnd, int index, IntPtr value)
        {
            if (IntPtr.Size == 8)
                _ = SetWindowLongPtr64(hwnd, index, value);
            else
                _ = SetWindowLong32(hwnd, index, value.ToInt32());
        }

        private int GetCurrentWindowStyle()
        {
            try { return GetWindowLongPtrSafe(this.Handle, GWL_STYLE).ToInt32(); }
            catch { return 0; }
        }

        private void ApplyTrueBorderlessWindowStyle()
        {
            try
            {
                if (!IsHandleCreated) return;
                int style = GetCurrentWindowStyle();
                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
                style |= WS_POPUP;

                SetWindowLongPtrSafe(this.Handle, GWL_STYLE, new IntPtr(style));
            }
            catch { }
        }

        private void RestoreWindowedWindowStyle()
        {
            try
            {
                if (!IsHandleCreated) return;
                if (_prevWindowStyle != 0)
                    SetWindowLongPtrSafe(this.Handle, GWL_STYLE, new IntPtr(_prevWindowStyle));
            }
            catch { }
        }

        public PlayerForm() : this(null) { }

        public PlayerForm(Rectangle? startupArea)
        {
            _mpcvrChildWndProc = MpcvrChildNativeWndProc;

            Text = "Cinecore Player 2025";
            MinimumSize = new Size(1040, 600);
            // Colore identico alla home cinematica: evita il frame nero/grigio prima del primo paint.
            BackColor = Color.FromArgb(2, 9, 16);
            DoubleBuffered = true;

            StartPosition = FormStartPosition.Manual;
            _prevBorder = FormBorderStyle.Sizable;
            _prevState = FormWindowState.Normal;
            _prevBounds = new Rectangle(120, 80, 1280, 720);
            _prevControlBox = true;
            _prevMinimizeBox = true;
            _prevMaximizeBox = true;
            try
            {
                var screen = startupArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
                ControlBox = true;
                MinimizeBox = true;
                MaximizeBox = true;
                FormBorderStyle = FormBorderStyle.Sizable;
                TopMost = false;
                int w = Math.Min(1480, Math.Max(1120, screen.Width - 140));
                int h = Math.Min(900, Math.Max(680, screen.Height - 110));
                Bounds = new Rectangle(screen.Left + (screen.Width - w) / 2, screen.Top + (screen.Height - h) / 2, w, h);
                _prevBounds = Bounds;
            }
            catch { }

            HandleCreated += (_, __) =>
            {
                try
                {
                    if (FormBorderStyle == FormBorderStyle.None)
                    {
                        ApplyTrueBorderlessWindowStyle();
                        var screen = Screen.FromControl(this).Bounds;
                        Bounds = screen;
                        Win32.SetWindowPos(this.Handle, Win32.HWND_TOPMOST,
                            screen.X, screen.Y, screen.Width, screen.Height,
                            Win32.SWP_FRAMECHANGED);
                    }
                }
                catch { }
            };

            // Filtro tastiera/media keys + fallback click modal: il mouse resta leggero
            // e viene intercettato solo su click/wheel, non sui MouseMove continui.
            try
            {
                _playbackKeyboardFilter = new PlaybackKeyboardMessageFilter(this);
                Application.AddMessageFilter(_playbackKeyboardFilter);
                _inputModeFilter = new InputModeMessageFilter(this);
                Application.AddMessageFilter(_inputModeFilter);
            }
            catch { }

            _rootLayout = new Panel { Dock = DockStyle.Fill };
            _rootLayout.BackColor = Color.FromArgb(2, 9, 16);

            _stack = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(2, 9, 16) };
            _videoHost = new RendererHostPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
            if (_videoHost is RendererHostPanel rendererHost)
                rendererHost.OwnerMessage = ForwardVideoHostOwnerMessageToRenderer;
            _videoDetachHost = new Panel
            {
                Size = new Size(4, 4),
                Visible = false,
                BackColor = Color.Black,
                Location = new Point(-5000, -5000)
            };

            // HUD
            _hud = new HudOverlay { InteractionBlocked = () => _contextMenuPending || _contextMenuActive || IsPlaybackContextMenuVisible(), Dock = DockStyle.Fill, AutoHide = true, Visible = false, ExternalVignette = true };
            _hud.TimelineVisible = false;

            // OSD centrale (stile "Apple") mostrato SOLO per comandi che arrivano dal remote server
            _remoteOsd = new RemoteOsdOverlay
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = Color.Transparent
            };

            _infoOverlay = new InfoOverlay
            {
                Dock = DockStyle.None,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Visible = false,
                AutoHeight = true,
                MinCardHeight = 420,
                MaxCardHeight = 840
            };

            _overlayHost = new OverlayHostForm();
            _overlayHost.ContextMenuRequested += ShowMpcvrContextMenuFromOverlay;
            AddOwnedForm(_overlayHost);
            _overlayHost.Visible = false;
            _mpcvrOverlayPopup = new PopupOverlayHost();
            _mpcvrOverlayPopup.Surface.Resize += (_, __) => LayoutInfoOverlay();

            _hud.BackColor = Color.Transparent;
            _infoOverlay.BackColor = Color.Transparent;
            _infoOverlay.CloseRequested += () =>
            {
                try
                {
                    SetInfoPanelRequested(false);
                }
                catch { }
            };

            _netflixModePage = new NetflixModePage
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            _netflixModePage.OpenRequested += path =>
            {
                try { HideSettingsHudPage(); } catch { }
                PlayLibraryRequestedPath(path, PlaybackResumeStore.LoadAll().FirstOrDefault(entry => string.Equals(entry.MediaPath,path,StringComparison.OrdinalIgnoreCase))?.PositionSeconds);
            };
            _netflixModePage.RestartRequested += path => PlayLibraryRequestedPath(path, resumeSeconds: 0);
            _netflixModePage.QueueRequested += path =>
            {
                AppendToPlaybackQueue(new[] { path });
                try { _lblStatus.Text = Tx("Aggiunto alla coda", "Added to queue"); } catch { }
            };
            _netflixModePage.CloseRequested += () => HideNetflixMode(showHome: true);
            _netflixModePage.NetworkSource = _spotlightSource == "DLNA";
            _netflixModePage.SourceChangeRequested += source => { SetSpotlightSource(source); _netflixModePage.NetworkSource = _spotlightSource == "DLNA"; ReloadSpotlightSource(); };
            AttachMouseAnchorTracking(_netflixModePage);

            _settingsHudPage = new SettingsHudPage
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            _settingsHudPage.CloseRequested += HideSettingsHudPage;
            _settingsHudPage.ImageSizing = _imageSizing;
            _settingsHudPage.ImageSizingChanged += ImageSizingChanged;
            // Schermo intero <-> finestra: il dimensionamento dell'immagine vale solo a schermo intero.
            _videoHost.SizeChanged += (_, __) => { if (_engine != null && _imageSizing.Mode != ImageSizingMode.Fill) TryBeginInvokeOnUi(() => ApplyImageSizing()); };
            _settingsHudPage.MadVrHdrProfileChanged += profile =>
            {
                // Tiene allineato il menu Immagine / HDR con la pagina madVR.
                _hdrProfile = profile switch
                {
                    "passthrough" => HdrUiProfile.Passthrough,
                    "tonemap" => HdrUiProfile.ToneMapSdr,
                    "lut" => HdrUiProfile.LutSdr,
                    _ => HdrUiProfile.Auto
                };
                _hdr = _hdrProfile is HdrUiProfile.ToneMapSdr or HdrUiProfile.LutSdr ? HDRMode.Off : HDRMode.Auto;
                try { SaveExtrasConfig(); } catch { }
            };
            _settingsHudPage.ApplyRequested += () =>
            {
                try { SaveExtrasConfig(); } catch { }
                _lblStatus.Text = Tx("Impostazioni applicate", "Settings applied");
                SyncSettingsHudState();
            };
            _settingsHudPage.PreferBitstreamChanged += enabled =>
            {
                _preferBitstreamUi = enabled;
                _audioOutPref = enabled ? AudioOutPref.Auto : AudioOutPref.ForcePcm;
                try { SaveExtrasConfig(); } catch { }
                SyncSettingsHudState();
            };
            _settingsHudPage.LanguageChanged += language =>
            {
                ApplyUiLanguage(language, save: true);
            };
            _settingsHudPage.ThemeColorRequested += ShowThemeColorDialog;
            _settingsHudPage.ThemeResetRequested += ResetUiThemePalette;
            _settingsHudPage.TmdbApiKeyRequested += ConfigureTmdbApiKey;
            _settingsHudPage.CredentialsSaved += () => { SyncSettingsHudState(); _cinematicLibraryPage?.RefreshArtistArtworkAfterProviderChange(); };
            _settingsHudPage.AmplifierRequested += ShowAmplifierControl;
            _settingsHudPage.ThemeModeChanged += mode => SetUiThemeMode(mode, save: true);
            _settingsHudPage.UpdateCheckRequested += () => BeginInvoke(new Action(() => ShowUpdateDialog()));
            _settingsHudPage.ComponentsRequested += () => BeginInvoke(new Action(ShowComponentUpdatesDialog));
            _settingsHudPage.SpotifyCredentialsRequested += ConfigureSpotifyCredentials;
            _settingsHudPage.RendererChanged += renderer =>
            {
                ApplySettingsRendererChoice(renderer);
                SyncSettingsHudState();
            };
            _settingsHudPage.MpvRuntimeV3Changed += useV3 =>
            {
                ApplySettingsMpvRuntimeChoice(useV3);
                SyncSettingsHudState();
            };
            _settingsHudPage.MpvOptionCycleRequested += key =>
            {
                CycleSettingsMpvOption(key);
                SyncSettingsHudState();
            };
            _settingsHudPage.MpvOptionSelected += (key, value) =>
            {
                SetSettingsMpvOptionValue(key, value);
                SyncSettingsHudState();
            };
            _settingsHudPage.MpvToggleChanged += (key, enabled) =>
            {
                SetSettingsMpvToggle(key, enabled);
                SyncSettingsHudState();
            };
            _settingsHudPage.MpvAdvancedOptionsRequested += EditSettingsMpvAdvancedOptions;
            _settingsHudPage.ProviderPanelRequested += provider =>
            {
                OpenSettingsProviderPanel(provider);
                SyncSettingsHudState();
            };
            _settingsHudPage.NetflixOpenRequested += () =>
            {
                HideSettingsHudPage();
                ShowNetflixMode();
            };
            _settingsHudPage.NetflixHomeChanged += SetSpotlightAtStartup;
            _settingsHudPage.SpotlightAtWindowsStartupChanged += SetSpotlightAtWindowsStartup;
            _settingsHudPage.SpotlightSourceChanged += SetSpotlightSource;
            _settingsHudPage.SpotlightServerChanged += SetSpotlightServer;
            _settingsHudPage.SpotlightServerRefreshRequested += RefreshSpotlightServerChoices;
            AttachMouseAnchorTracking(_settingsHudPage);


            // Overlay di caricamento media (NO schermata iniziale): nero + spinner con colori tema
            _videoLoading = new VideoLoadingMask
            {
                Dock = DockStyle.Fill,
                Visible = false
            };

            _audioOnlyBanner = BuildAudioOnlyBanner();

            _audioMeters = new AudioMetersLiveCharts
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            _audioMetersHost = new Panel
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = Color.Black,
                Padding = new Padding(28, 24, 28, HUD_HOTZONE_H + 24)
            };
            _audioMetersHost.Controls.Add(_audioMeters);

            _audioMetersHost.MouseMove += (_, __) => NoteMouseActivity();
            _audioMetersHost.Resize += (_, __) => LayoutAudioMetersHost();
            LayoutAudioMetersHost();

            // importantissimo: spesso l'evento resta "intrappolato" nel controllo figlio
            if (_audioMeters != null)
            {
                _audioMeters.MouseMove += (_, __) => NoteMouseActivity();
            }

            _stack.Controls.Add(_videoDetachHost);
            _stack.Controls.Add(_videoHost);
            _stack.Controls.Add(_audioOnlyBanner);
            _stack.Controls.Add(_audioMetersHost);
            _stack.Controls.Add(_videoLoading);
            _stack.Controls.Add(_netflixModePage);
            _stack.Controls.Add(_settingsHudPage);

            // ===== Extras overlays =====
            _pausePlaceholder = new PausePlaceholderOverlay
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = _overlayHost.TransparencyKey
            };
            _pausePlaceholder.SetFolder(_pausePlaceholderFolder);
            _pausePlaceholder.MouseUp += (_, e) =>
            {
                // The custom context menu executes a command on mouse-down and may
                // close above this control before mouse-up. That release must never
                // start the film while changing window mode or another menu setting.
                if (e.Button == MouseButtons.Left && _preOpenPlaceholderGateActive &&
                    !_contextMenuPending && !_contextMenuActive &&
                    DateTime.UtcNow > _suppressContextMenuMouseUpUntilUtc)
                    TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: false);
            };
            _overlayHost.Surface.Controls.Add(_pausePlaceholder);

            _overlayHost.Surface.Controls.Add(_infoOverlay);
            _overlayHost.Surface.Controls.Add(_hud);
            _overlayHost.Surface.Controls.Add(_remoteOsd);
            _remoteOsd.Invalidated += (_, __) => { try { UpdateMpcvrMixerOverlayMirror(); } catch { } };
            _overlayHost.Surface.Resize += (_, __) =>
            {
                LayoutIntroOutroPromptPanel();
                LayoutInfoOverlay();
            };
            // Stop preview quando l'HUD si nasconde (auto-hide o uscite dalla timeline)
            _hud.VisibleChanged += (_, __) =>
            {
                if (!_hud.Visible)
                {
                    if (!_hud.RequestedVisible)
                    {
                        try
                        {
                            _hudWakeAnchorPos = Control.MousePosition;
                            _hudWakeLastMousePos = _hudWakeAnchorPos;
                            _hudWakeNeedsIntentionalMove = true;
                        }
                        catch { }
                    }
                    _scrubActive = false;

                    var old = Interlocked.Exchange(ref _thumbCts, null);
                    try { old?.Cancel(); } catch { }

                    Interlocked.Increment(ref _previewReqSerial);
                    _hud.SetPreview(null, _engine?.PositionSeconds ?? 0);
                    try { HideMpcvrOverlayHostWhenIdle(); } catch { }
                    try { UpdateMpcvrOverlayRegionMode(); } catch { }
                    try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                }
                else
                {
                    try { SetModalInputState(false); } catch { }
                    try { SafeShowOverlayHost(); } catch { }
                    try { SyncOverlayToVideoRect(); } catch { }
                    try { UpdateMpcvrOverlayRegionMode(); } catch { }
                    try { if (!_useInlineOverlay) _overlayHost?.RaiseAboveOwner(); } catch { }
                    try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                    try { RefreshNetworkVolume(); } catch { }
                }
            };
            _hud.Invalidated += (_, __) => { try { UpdateMpcvrMixerOverlayMirror(); } catch { } };
            _hud.HostFadeAvailable = HudHostFadeAvailable;
            _hud.HostAlphaChanged += ApplyHudHostAlpha;
            _hud.BringToFront();

            _infoOverlay.VisibleChanged += (_, __) => ApplyHudHostAlpha();
            _hud.VisibleChanged += (_, __) => ApplyHudHostAlpha();
            _infoOverlay.VisibleChanged += (_, __) =>
            {
                try
                {
                    if (_infoOverlay.Visible)
                    {
                        SafeShowOverlayHost();
                        SyncOverlayToVideoRect();
                        LayoutInfoOverlay();
                        SyncHudInfoOverlayMode();
                        if (_hud.Visible)
                            _hud.BringToFront();
                        _infoOverlay.BringToFront();
                    }
                    else
                    {
                        SyncHudInfoOverlayMode();
                        HideMpcvrOverlayHostWhenIdle();
                        UpdateMpcvrOverlayRegionMode();
                    }

                    UpdateMpcvrOverlayRegionMode();
                    if (!_useInlineOverlay) _overlayHost?.RaiseAboveOwner();
                    UpdateMpcvrMixerOverlayMirror(force: true);
                }
                catch { }
            };
            _infoOverlay.Invalidated += (_, __) => { try { UpdateMpcvrMixerOverlayMirror(); } catch { } };
            _remoteOsd.VisibleChanged += (_, __) =>
            {
                try
                {
                    if (_remoteOsd.Visible)
                    {
                        SafeShowOverlayHost();
                        SyncOverlayToVideoRect();
                        UpdateMpcvrOverlayRegionMode();
                        _remoteOsd.BringToFront();
                        if (!_useInlineOverlay) _overlayHost?.RaiseAboveOwner();
                    }
                    else
                    {
                        UpdateMpcvrOverlayRegionMode();
                    }

                    UpdateMpcvrMixerOverlayMirror(force: true);
                }
                catch { }
            };

            _audioOnlyBanner.BackColor = Color.Black;

            // HUD minimale per le foto (solo frecce sx/dx)
            _photoHud = new PhotoHudOverlay
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = _overlayHost.TransparencyKey
            };
            _photoHud.PrevRequested += () => ShowPrevImage();
            _photoHud.NextRequested += () => ShowNextImage();
            _photoHud.BackRequested += () => { StopPhotoSlideshow(); CloseCurrentToLibrary(); };
            _photoHud.ZoomRequested += direction =>
            {
                if (_engine is ImagePlaybackEngine image) image.ZoomBy(direction > 0 ? 1.2 : 1 / 1.2);
            };
            _photoHud.ViewModeRequested += () =>
            {
                if (_engine is not ImagePlaybackEngine imageEngine)
                    return;
                imageEngine.CycleViewMode();
            };
            _photoHud.RotateRequested += () =>
            {
                if (_engine is not ImagePlaybackEngine imageEngine)
                    return;
                imageEngine.RotateClockwise();
            };
            _photoHud.SlideshowRequested += TogglePhotoSlideshow;
            _photoHud.EditRequested += EditCurrentPhoto;
            _photoHud.DeleteRequested += DeleteCurrentPhoto;
            _photoHud.ActionRequested += RunPhotoBarAction;
            _photoHud.Invalidated += (_, __) => { try { UpdateMpcvrMixerOverlayMirror(); } catch { } };
            _overlayHost.Surface.Controls.Add(_photoHud);

            // ===== HUD wake + cursor auto-hide (3s mouse idle) =====
            // Criterio importante: reagiamo SOLO a movimento fisico del mouse in coordinate schermo.
            // Alcuni renderer/resize overlay generano MouseMove "sintetici" anche con puntatore fermo,
            // e finivano per riaprire subito l'HUD appena iniziava a nascondersi.
            void NoteMouseActivity()
            {
                if (_closingForExit || _nativeChromeInteraction) return;
                var now = DateTime.UtcNow;
                if (_contextMenuActive || _contextMenuPending)
                {
                    EnsureCursorVisible();
                    return;
                }
                if (now < _suppressHudWakeUntilUtc)
                    return;
                // Con una scheda aperta il mouse si muove sopra la scheda, non sul film: risvegliare l'HUD
                // sotto rimescolava le finestre a ogni movimento e la scheda andava a scatti.
                if (SheetPresenter.AnyOpen)
                    return;

                Point screenPos;
                try { screenPos = Control.MousePosition; }
                catch { return; }

                Rectangle screen;
                try { screen = RectangleToScreen(ClientRectangle); }
                catch { return; }

                bool overWindow = !screen.IsEmpty && screen.Contains(screenPos);
                if (!overWindow)
                {
                    _hudWakeLastMousePos = screenPos;
                    _hudWakeAnchorPos = screenPos;
                    _hudWakeNeedsIntentionalMove = false;
                    EnsureCursorVisible();
                    return;
                }

                bool physicallyMoved =
                    Math.Abs(screenPos.X - _hudWakeLastMousePos.X) >= HUD_WAKE_POLL_DEADZONE_PX ||
                    Math.Abs(screenPos.Y - _hudWakeLastMousePos.Y) >= HUD_WAKE_POLL_DEADZONE_PX;

                if (!physicallyMoved)
                    return;

                _hudWakeLastMousePos = screenPos;

                if (_hudWakeNeedsIntentionalMove)
                {
                    int wakeAfterHidePx = IsMpcvrActive ? HUD_WAKE_POLL_DEADZONE_PX : HUD_WAKE_AFTER_AUTOHIDE_PX;
                    bool movedEnough =
                        Math.Abs(screenPos.X - _hudWakeAnchorPos.X) >= wakeAfterHidePx ||
                        Math.Abs(screenPos.Y - _hudWakeAnchorPos.Y) >= wakeAfterHidePx;

                    if (!movedEnough)
                        return;

                    _hudWakeNeedsIntentionalMove = false;
                }

                _lastMouseMoveUtc = now;
                _lastHudActivityUtc = now;

                // Ripristina il cursore appena c'è attività reale
                EnsureCursorVisible();

                // I grafici audio devono restare un'area di lavoro indipendente:
                // il passaggio del mouse sulle chart non richiama l'HUD video.
                try
                {
                    if (IsAudioOnlyPlaybackUi() && ShouldPinAudioOnlyHud())
                    {
                        EnsureAudioOnlyHudPinned();
                        return;
                    }

                    if (_audioMetersHost?.Visible == true && _audioMeters?.Visible == true &&
                        _audioMeters.IsPointerOverAnalysisArea(screenPos))
                    {
                        if (_hud.Visible)
                        {
                            _hud.Visible = false;
                            _hud.TimelineVisible = false;
                        }
                        return;
                    }
                }
                catch { }

                // In modalità foto NON vogliamo mai far comparire l’HUD video
                if (IsPhotoMode)
                {
                    try { _photoHud?.Wake(); } catch { }
                    return;
                }

                if (_engine == null) return;
                if (IsPlaybackOverlayBlockedByLoading()) return;

                WakeHudFromPointer();
            }

            // MouseMove vero (quando disponibile)
            _overlayHost.Surface.MouseMove += (_, __) => NoteMouseActivity();
            _mpcvrOverlayPopup.Surface.MouseMove += (_, __) => NoteMouseActivity();
            _videoHost.MouseMove += (_, __) => NoteMouseActivity();

            // Fallback: alcuni renderer non propagano MouseMove ai controlli WinForms.
            // Polliamo la posizione del mouse e usiamo la stessa logica anti-falso-positivo.
            try
            {
                _hudWakeLastMousePos = Control.MousePosition;
                _hudWakeAnchorPos = _hudWakeLastMousePos;

                _hudWakePollTimer.Tick += (_, __) =>
                {
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        if (WindowState == FormWindowState.Minimized) return;
                        NoteMouseActivity();
                    }
                    catch { }
                };

                if (!_hudWakePollTimer.Enabled)
                    _hudWakePollTimer.Start();
            }
            catch { }

            // Idle tick: nascondi HUD + cursore dopo 3s
            try
            {
                _uiIdleTimer.Tick += (_, __) => TickUiIdle();
                if (!_uiIdleTimer.Enabled) _uiIdleTimer.Start();
            }
            catch { }

            // Non usare l'auto-hide interno dell'HUD (ci pensiamo noi)
            try { _hud.AutoHide = false; } catch { }

            IntroOutroScanService.StatusChanged += OnIntroOutroScanStatusChanged;

            BringOverlaysToFront();

            _hud.Visible = false;
            _infoOverlay.Visible = false;

            _rootLayout.Controls.Add(_stack);
            InitializeMusicWorkspace();
            InitializeDefaultAudioDeviceWatcher();
            RestoreJellyfinTitleHints();
            _lblStatus = new Label { Text = Tx("Pronto", "Ready") };

            Controls.Add(_rootLayout);
            Deactivate += (_, __) => SuppressPlaybackWindowsWhenInactive();
            Activated += (_, __) =>
            {
                ReleaseStaleCinecoreMouseCapture();
                RestorePlaybackWindowsWhenActive();
            };
            BringOverlaysToFront();
            try { LoadPlaybackQueueState(); } catch { }

            _hud.GetTime = () => (GetTimelinePositionForHud(), GetTimelineDurationSeconds());
            _hud.GetInfoLine = () => BuildHudInfoLine();
            _hud.GetTitle = () => CachedDisplayInfo().Title;
            _computeHudTitle = () =>
            {
                string? raw = !string.IsNullOrWhiteSpace(_originalVideoName) ? _originalVideoName
                          : (!string.IsNullOrWhiteSpace(_currentPath) ? _currentPath
                          : (!string.IsNullOrWhiteSpace(_thumb?.SourcePath) ? _thumb.SourcePath
                          : MediaProbe.LastProbedPath));

                if (string.IsNullOrWhiteSpace(raw))
                    return string.Empty;

                try
                {
                    string candidate = !string.IsNullOrWhiteSpace(_currentPath) ? _currentPath! : raw;
                    string bestTitle = BuildBestDisplayTitleForPath(candidate);
                    if (!string.IsNullOrWhiteSpace(bestTitle))
                        return bestTitle;
                }
                catch { }

                try
                {
                    if (IsCurrentYouTube() && !string.IsNullOrWhiteSpace(_originalVideoName))
                        return "YouTube • " + NormalizeDisplayTitle(_originalVideoName);
                }
                catch { }

                return NormalizeDisplayTitle(raw);
            };
            _hud.OpenClicked += () => OpenFile();
            _hud.PlayPauseClicked += () => TogglePlayPause();
            _hud.StopClicked += () => CloseCurrentToLibrary();
            _hud.FullscreenClicked += () => ToggleFullscreen();

            _hud.TopInfoClicked += () =>
            {
                // Il pulsante accendeva il pannello senza registrare la richiesta: il pannello
                // spariva insieme all'HUD e i suoi dati non venivano piu' aggiornati.
                SetInfoPanelRequested(!_infoRequested);
                try { UpdateMpcvrOverlayRegionMode(); } catch { }
                try { if (!_useInlineOverlay) _overlayHost?.RaiseAboveOwner(); } catch { }
            };
            _hud.TopSettingsClicked += () => ShowSettingsHudPage();

            _hud.CanChangeVolume = () => IsNetworkVolumeActive || !IsBitstream();
            _hud.ExternalVolumeRequested = value => TryNetworkVolume(value: value);
            _hud.ExternalVolumeStepRequested = direction => TryNetworkVolume(step: direction);
            _hud.ExternalMuteRequested = () => TryNetworkVolume(toggleMute: true);
            _hud.ExternalVolumeText = () => IsNetworkVolumeActive ? _networkVolumeText : null;
            _hud.VolumeChanged += v => ApplyVolume(v);

            _hud.SeekRequested += s =>
            {
                if (_engine == null) return;

                // La timeline usa anche la durata rilevata dal motore/probe. In alcuni
                // flussi (soprattutto URL) _duration non e ancora valorizzato, pur
                // avendo una timeline perfettamente valida e visibile.
                double seekDuration = GetTimelineDurationSeconds();
                if (seekDuration <= 0) return;

                _scrubPending = Math.Clamp(s, 0, Math.Max(0.01, seekDuration));
                SetTimelinePositionOverride(_scrubPending);
                PreparePlaybackSeek(clearTimelinePreview: false, previewSeconds: _scrubPending);
                try { _engine.PositionSeconds = _scrubPending; } catch { }
                try { PublishRemoteState(_scrubPending); } catch { }
                try { _hud.Invalidate(); } catch { }
                EndTimelinePreview();
                _hud.ShowOnce(1200);
            };

            _hud.PreviewRequested += (sec, pt) =>
            {
                _scrubActive = true;
                OnPreviewRequested(sec, pt);
            };
            _hud.PreviewDismissed += () => EndTimelinePreview();
            _hud.PreviewLayoutChanged += () => { try { UpdateMpcvrOverlayRegionMode(); } catch { } };

            _hud.SkipBack10Clicked += () => { SeekRelative(-10); _hud.ShowOnce(1200); };
            _hud.SkipForward10Clicked += () => { SeekRelative(10); _hud.ShowOnce(1200); };
            _hud.PrevChapterClicked += () => { SeekChapter(-1); _hud.ShowOnce(1200); };
            _hud.NextChapterClicked += () => { SeekChapter(+1); _hud.ShowOnce(1200); };
            _hud.SkipIntroPromptClicked += () => TrySkipDetectedIntro();
            _hud.NextEpisodePromptClicked += () => TryOpenNextEpisodeFromIntroOutroPrompt();
            // === AVVIO TELECOMANDO WEB (con PIN a schermo finché non abbini) ===
            _remoteSnapshot = new RemoteState(); // il tuo snapshot già esistente

            _remote = new RemoteServer(
                port: 9234,
                pin: null, // PIN dinamico autogenerato
                getState: GetRemoteState,
                handleCommand: (cmd, q) =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            BeginRemoteCommandScope();
                            try
                            {
                                switch (cmd)
                                {
                                    case "library_source":
                                        // Dal telefono: libreria del computer o del server di rete.
                                        // Vale per l'elenco del telefono, senza spostare la libreria sullo schermo del computer.
                                        _remoteLibraryNetwork = q.TryGetValue("value", out var wanted) && wanted == "network";
                                        // L'elenco per il telefono e' in cache per dieci secondi: dopo il cambio va rifatto
                                        // subito, e di nuovo quando il catalogo di rete ha finito di caricarsi.
                                        RefreshRemoteLibraryAfterSourceChange();
                                        break;
                                    case "toggle":
                                        StopRemoteScan();
                                        if (!TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: true))
                                            TogglePlayPause();
                                        ShowRemoteOsd(_paused ? _hud?.SvgPathPause : _hud?.SvgPathPlay, ms: 800);
                                        break;

                                    case "back10":
                                        if (IsRemoteScanActive) { StepRemoteScan(-1); break; }
                                        StopRemoteScan();
                                        SeekRelative(-10);
                                        ShowRemoteOsd(_hud?.SvgPathBack10, ms: 700);
                                        break;

                                    case "fwd10":
                                        if (IsRemoteScanActive) { StepRemoteScan(+1); break; }
                                        StopRemoteScan();
                                        SeekRelative(+10);
                                        ShowRemoteOsd(_hud?.SvgPathFwd10, ms: 700);
                                        break;

                                    case "scan_back":
                                        StepRemoteScan(-1);
                                        break;

                                    case "scan_fwd":
                                        StepRemoteScan(+1);
                                        break;

                                    case "prev":
                                        StopRemoteScan();
                                        if (IsPhotoMode) { ShowPrevImage(); }
                                        else { SeekChapter(-1); ShowRemoteOsd(_hud?.SvgPathPrevChapter, ms: 700); }
                                        break;

                                    case "next":
                                        StopRemoteScan();
                                        if (IsPhotoMode) { ShowNextImage(); }
                                        else { SeekChapter(+1); ShowRemoteOsd(_hud?.SvgPathNextChapter, ms: 700); }
                                        break;

                                    case "full":
                                        StopRemoteScan();
                                        ToggleFullscreen();
                                        ShowRemoteOsd(_hud?.SvgPathFullscreen, ms: 700);
                                        break;

                                    case "scrub":
                                        StopRemoteScan();
                                        double remoteScrubDuration = GetTimelineDurationSeconds();
                                        if (_engine != null && remoteScrubDuration > 0 &&
                                            q.TryGetValue("pos", out var sp) &&
                                            double.TryParse(sp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var posScrub))
                                        {
                                            double sec = Math.Clamp(posScrub, 0, Math.Max(0.01, remoteScrubDuration));
                                            // TIMELINE da remoto: apri l'overlay inferiore e mostra l'anteprima.
                                            // (Il resto dei comandi remoti NON deve far comparire l'HUD.)
                                            try { _scrubActive = true; } catch { }

                                            try
                                            {
                                                if (_hud != null)
                                                {
                                                    HudBump(HUD_IDLE_HIDE_MS, allowWhenRemote: true, showTimeline: true);
                                                    _hud.SetRemoteScrub(sec, lingerMs: 2500);
                                                }
                                            }
                                            catch { }

                                            try { OnPreviewRequested(sec, Point.Empty); } catch { }
                                            BringOverlaysToFront();
                                        }
                                        break;

                                    case "seek":
                                        StopRemoteScan();
                                        {
                                            double sec = 0;
                                            bool ok = false;
                                            double remoteSeekDuration = GetTimelineDurationSeconds();

                                            if (_engine != null && remoteSeekDuration > 0 &&
                                                q.TryGetValue("pos", out var s) &&
                                                double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pos))
                                            {
                                                sec = Math.Clamp(pos, 0, Math.Max(0.01, remoteSeekDuration));
                                                _scrubPending = sec;
                                                SetTimelinePositionOverride(sec);
                                                PreparePlaybackSeek(clearTimelinePreview: false, previewSeconds: sec);
                                                try { _engine.PositionSeconds = sec; } catch { }
                                                ok = true;
                                            }

                                            // clear preview state (latest-wins)
                                            _scrubActive = false;
                                            try { _thumbCts?.Cancel(); } catch { }
                                            Interlocked.Increment(ref _previewReqSerial);
                                            try { _hud?.SetPreview(null, sec); } catch { }
                                            try { _hud?.ClearRemoteScrub(); } catch { }
                                            // niente OSD centrale: la timeline deve essere l'overlay inferiore
                                            try { if (ok) HudBump(1200, allowWhenRemote: true, showTimeline: true); } catch { }
                                            BringOverlaysToFront();
                                        }
                                        break;

                                    case "vol":
                                        StopRemoteScan();
                                        if (q.TryGetValue("v", out var v) &&
                                            float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vf))
                                        {
                                            vf = Math.Clamp(vf, 0f, 1f);
                                            RemoteSetVolume(vf);
                                            try { _hud?.Pulse(HudOverlay.ButtonId.Volume); } catch { }
                                        }
                                        break;

                                    case "volup":
                                        StopRemoteScan();
                                        RemoteAdjustVolume(+0.05f);
                                        try { _hud?.Pulse(HudOverlay.ButtonId.Volume); } catch { }
                                        break;

                                    case "voldown":
                                        StopRemoteScan();
                                        RemoteAdjustVolume(-0.05f);
                                        try { _hud?.Pulse(HudOverlay.ButtonId.Volume); } catch { }
                                        break;

                                    case "mute":
                                        StopRemoteScan();
                                        RemoteToggleMute();
                                        try { _hud?.Pulse(HudOverlay.ButtonId.Volume); } catch { }
                                        break;

                                    case "left":
                                        StopRemoteScan();
                                        _lastDpadFromRemote = true;
                                        HandleDpadMove("left");
                                        break;

                                    case "right":
                                        StopRemoteScan();
                                        _lastDpadFromRemote = true;
                                        HandleDpadMove("right");
                                        break;

                                    case "up":
                                        StopRemoteScan();
                                        _lastDpadFromRemote = true;
                                        HandleDpadMove("up");
                                        break;

                                    case "down":
                                        StopRemoteScan();
                                        _lastDpadFromRemote = true;
                                        HandleDpadMove("down");
                                        break;

                                    case "ok":
                                        StopRemoteScan();
                                        _lastDpadFromRemote = true;
                                        HandleDpadOk();
                                        break;

                                    case "back":
                                        StopRemoteScan();
                                        _lastDpadFromRemote = true;
                                        HandleDpadBack();
                                        break;

                                    case "home":
                                        StopRemoteScan();
                                        ShowDefaultLibrary();
                                        break;

                                    case "info":
                                        StopRemoteScan();
                                        SetInfoPanelRequested(!_infoRequested);
                                        BringOverlaysToFront();
                                        try { _hud?.Pulse(HudOverlay.ButtonId.TopInfo); } catch { }
                                        break;

                                    case "hdr":
                                        StopRemoteScan();
                                        if (q.TryGetValue("mode", out var remoteHdrMode))
                                            SetRemoteHdrProfile(remoteHdrMode);
                                        else
                                            CycleHdrProfile();
                                        break;

                                    case "upscaling":
                                        StopRemoteScan();
                                        bool enableUpscaling = q.TryGetValue("mode", out var remoteUpscalingMode)
                                            ? string.Equals(remoteUpscalingMode, "on", StringComparison.OrdinalIgnoreCase)
                                            : !_enableUpscaling;
                                        SetVideoUpscaling(enableUpscaling, "remote");
                                        break;

                                    case "upscaling_profile":
                                        StopRemoteScan();
                                        q.TryGetValue("backend", out var remoteUpscalingBackend);
                                        q.TryGetValue("preset", out var remoteUpscalingPreset);
                                        SetRemoteUpscaling(remoteUpscalingBackend, remoteUpscalingPreset);
                                        break;

                                    case "renderer_current":
                                        StopRemoteScan();
                                        q.TryGetValue("value", out var currentRendererValue);
                                        SetRemoteRendererChoice(currentRendererValue, saveAsDefault: false);
                                        break;

                                    case "renderer_default":
                                        StopRemoteScan();
                                        q.TryGetValue("value", out var defaultRendererValue);
                                        SetRemoteRendererChoice(defaultRendererValue, saveAsDefault: true);
                                        break;

                                    case "stereo":
                                        StopRemoteScan();
                                        if (q.TryGetValue("mode", out var m))
                                        {
                                            if (string.Equals(m, "off", StringComparison.OrdinalIgnoreCase)) Disable3DRestoreRenderer();
                                            else if (string.Equals(m, "auto", StringComparison.OrdinalIgnoreCase)) Enable3DAuto("remote");
                                            else if (string.Equals(m, "sbs", StringComparison.OrdinalIgnoreCase)) Enable3D(Stereo3DMode.SBS);
                                            else if (string.Equals(m, "tab", StringComparison.OrdinalIgnoreCase)) Enable3D(Stereo3DMode.TAB);
                                        }
                                        else
                                        {
                                            if (!_stereoAutoEnabled && _stereo == Stereo3DMode.None) Enable3DAuto("remote-cycle");
                                            else if (_stereoAutoEnabled) Enable3D(Stereo3DMode.SBS);
                                            else if (_stereo == Stereo3DMode.SBS) Enable3D(Stereo3DMode.TAB);
                                            else Disable3DRestoreRenderer();
                                        }
                                        PublishRemoteState();
                                        break;

                                    case "pause":
                                        StopRemoteScan();
                                        // Se siamo nel placeholder gate, "pause" non deve far partire nulla.
                                        if (!_preOpenPlaceholderGateActive)
                                        {
                                            if (!_paused) TogglePlayPause();
                                        }
                                        ShowRemoteOsd(_hud?.SvgPathPause, ms: 800);
                                        break;

                                    case "play":
                                        StopRemoteScan();
                                        // Se è attivo il placeholder gate, "play" deve far partire il film.
                                        if (!TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: true))
                                        {
                                            if (_paused) TogglePlayPause();
                                        }
                                        ShowRemoteOsd(_hud?.SvgPathPlay, ms: 800);
                                        break;

                                    case "stop":
                                        StopRemoteScan();
                                        CloseCurrentToLibrary();
                                        break;

                                    case "library":
                                        StopRemoteScan();
                                        ShowDefaultLibrary();
                                        if (q.TryGetValue("category", out var remoteCategory) && !string.IsNullOrWhiteSpace(remoteCategory))
                                            _cinematicLibraryPage?.ShowCategoryFromRemote(remoteCategory);
                                        break;

                                    case "theme":
                                        StopRemoteScan();
                                        bool light = q.TryGetValue("mode", out var remoteTheme)
                                            ? string.Equals(remoteTheme, "light", StringComparison.OrdinalIgnoreCase)
                                            : !Theme.IsLight;
                                        ApplyUiToneMode(light);
                                        PublishRemoteState();
                                        break;

                                    case "language":
                                        StopRemoteScan();
                                        string language = q.TryGetValue("value", out var remoteLanguage) &&
                                                          string.Equals(remoteLanguage, "en", StringComparison.OrdinalIgnoreCase)
                                            ? "en" : "it";
                                        ApplyUiLanguage(language, save: true);
                                        PublishRemoteState();
                                        break;

                                    case "fullscreen_extended":
                                        ToggleExtendedFullscreen();
                                        break;

                                    case "spotlight":
                                        StopRemoteScan();
                                        if (_netflixModePage?.Visible == true) HideNetflixMode(showHome: true);
                                        else ShowNetflixMode();
                                        PublishRemoteState();
                                        break;

                                    case "text":
                                        StopRemoteScan();
                                        if (q.TryGetValue("value", out var remoteText))
                                        {
                                            if (_netflixModePage?.Visible == true)
                                                _netflixModePage.SetRemoteSearchText(remoteText);
                                            else if (_cinematicLibraryPage?.Visible == true)
                                                _cinematicLibraryPage.SetRemoteSearchText(remoteText);
                                            else
                                            {
                                                Control? active = ActiveControl;
                                                while (active is ContainerControl container && container.ActiveControl != null)
                                                    active = container.ActiveControl;
                                                if (active is TextBoxBase editor)
                                                {
                                                    editor.Text = remoteText;
                                                    editor.SelectionStart = editor.TextLength;
                                                }
                                            }
                                        }
                                        break;

                                    case "keyenter":
                                        StopRemoteScan();
                                        _lastDpadFromRemote = true;
                                        HandleDpadOk();
                                        break;

                                    case "subtitles":
                                        StopRemoteScan();
                                        // La selezione avviene nel pannello tracce del
                                        // telecomando; questo comando resta come toggle
                                        // compatibile per i client precedenti.
                                        if (_engine != null)
                                        {
                                            var subtitles = _engine.EnumerateStreams().Where(s => s.IsSubtitle && !DirectShowUnifiedEngine.IsSubtitleOffOption(s.Name)).ToList();
                                            bool active = subtitles.Any(s => s.Selected);
                                            if (active)
                                            {
                                                if (_engine.DisableSubtitlesIfPossible()) _subtitleAutoForcedMode = false;
                                            }
                                            else
                                            {
                                                _subtitleAutoForcedMode = TrySelectAutoForcedSubtitles(_engine, subtitles, ResolvePreferredAutoForcedLangKey(subtitles));
                                            }
                                            RefreshRemoteCompanionSnapshot();
                                        }
                                        break;

                                    case "audio_track":
                                        StopRemoteScan();
                                        if (_engine != null && q.TryGetValue("id", out var audioIdText) && int.TryParse(audioIdText, out int audioId))
                                        {
                                            var audio = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.GlobalIndex == audioId);
                                            if (audio == null) break;
                                            _engine.EnableByGlobalIndex(audioId);
                                            if (SubtitleLanguage(audio) is string remoteAudioLanguage) _preferredSubtitleLangKey = remoteAudioLanguage;
                                            if (_subtitleAutoForcedMode)
                                            {
                                                var subtitles = _engine.EnumerateStreams().Where(s => s.IsSubtitle).ToList();
                                                _subtitleAutoForcedMode = TrySelectAutoForcedSubtitles(_engine, subtitles, ResolvePreferredAutoForcedLangKey(subtitles));
                                            }
                                            RefreshRemoteCompanionSnapshot();
                                        }
                                        break;

                                    case "subtitle_track":
                                        StopRemoteScan();
                                        if (_engine != null && q.TryGetValue("id", out var subtitleIdText) && int.TryParse(subtitleIdText, out int subtitleId))
                                        {
                                            if (subtitleId < 0)
                                            {
                                                if (_engine.DisableSubtitlesIfPossible()) _subtitleAutoForcedMode = false;
                                            }
                                            else
                                            {
                                                if (!_engine.EnumerateStreams().Any(s => s.IsSubtitle && s.GlobalIndex == subtitleId)) break;
                                                _engine.EnableByGlobalIndex(subtitleId);
                                                _subtitleAutoForcedMode = false;
                                            }
                                            RefreshRemoteCompanionSnapshot();
                                        }
                                        break;

                                    case "open":
                                        StopRemoteScan();
                                        if (q.TryGetValue("url", out var u) && !string.IsNullOrWhiteSpace(u))
                                        {
                                            q.TryGetValue("title", out var remoteOpenTitle);
                                            OpenFromRemoteLibrary(u, remoteOpenTitle);
                                        }
                                        break;

                                    case "poweroff":
                                        StopRemoteScan();
                                        WinKeys.CloseWindow(this.Handle);
                                        break;
                                }
                            }
                            finally
                            {
                                PublishRemoteState();
                                EndRemoteCommandScope();
                            }
                        }));
                    }
                    catch { }
                },
                getCompanion: GetRemoteCompanionData
            );
            // Impostazioni dei componenti dal telecomando: lette e scritte sul thread UI,
            // attraverso la stessa pagina delle impostazioni del player.
            _remote.SettingsProvider = () => Invoke(new Func<object>(() => _settingsHudPage.DescribeForRemote()));
            _remote.SettingsWriter = (provider, key, value) => Invoke(new Func<object>(() => _settingsHudPage.ApplyRemoteSetting(provider, key, value)));

            // quando si abbina un device -> nascondi banner
            SheetPresenter.Opening += () => { try { HidePairingBanner(); } catch { } };
            _remote.Paired += _ => { try { BeginInvoke(new Action(HidePairingBanner)); } catch { } };
            _remote.RemoteConnected += _ => { try { BeginInvoke(new Action(HidePairingBanner)); } catch { } };

            // quando un device NON abbinato prova a collegarsi -> mostra PIN (solo se serve)
            _remote.PairingRequested += pin =>
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        bool libraryVisible = _cinematicLibraryPage?.Visible == true || _netflixModePage?.Visible == true;
                        if (_engine == null && libraryVisible && _videoLoading?.Visible != true)
                            ShowPairingBanner(pin);
                    }));
                }
                catch { }
            };

            // Il server remoto parte dopo il primo frame: rete e binding porte non devono
            // ritardare la comparsa della HUD iniziale.
            HidePairingBanner();

            void RelayoutVideo()
            {
                try
                {
                    if (WindowState == FormWindowState.Minimized || !Visible)
                    {
                        SuppressPlaybackWindowsWhenInactive();
                        return;
                    }

                    // Anche il placeholder pre-film vive nell'host overlay. Deve
                    // seguire la finestra prima che esista un engine di riproduzione.
                    if (_engine != null && _videoHost != null && !_videoHost.IsDisposed && _videoHost.IsHandleCreated)
                        UpdateVideoWindowForCurrentHost();
                }
                catch { /* best-effort */ }

                try { SyncOverlayToVideoRect(); } catch { }
                try { BringOverlaysToFront(); } catch { }
            }

            LocationChanged += (_, __) => RelayoutVideo();
            SizeChanged += (_, __) =>
            {
                if (WindowState == FormWindowState.Minimized)
                    SuppressPlaybackWindowsWhenInactive();
                else
                    RelayoutVideo();
            };

            _videoHost.SizeChanged += (_, __) => RelayoutVideo();
            DpiChanged += (_, __) =>
            {
                // Defer until WinForms has applied the destination monitor's bounds.
                TryBeginInvokeOnUi(() =>
                {
                    UpdateMusicTransportHeight();
                    _musicTransport?.PerformLayout();
                    _cinematicLibraryPage?.Invalidate(true);
                    RelayoutVideo();
                });
            };

            // Extras folders + persisted toggles
            try { Directory.CreateDirectory(_pausePlaceholderFolder); } catch { }
            try { Directory.CreateDirectory(_preRollDemoFolder); } catch { }
            try { LoadExtrasConfig(); } catch { }
            // Anche al primo avvio (nessuna configurazione): chiaro o scuro come Windows.
            try { FollowSystemUiTheme(); } catch { }
            global::CinecorePlayer2025.Utilities.AppLanguage.Current = _uiLanguage;
            try { MovieMetadataService.SetPreferredMetadataLanguage(_uiLanguage); } catch { }
            try { _netflixModePage.SetLanguage(_uiLanguage); } catch { }
            try { _cinematicLibraryPage?.SetLanguage(_uiLanguage); } catch { }
            try { _infoOverlay.SetLanguage(_uiLanguage); } catch { }
            try { _hud.SetLanguage(_uiLanguage); } catch { }
            try { _audioMeters?.SetLanguage(_uiLanguage); } catch { }
            try { _pausePlaceholder.SetFolder(_pausePlaceholderFolder); } catch { }

            BuildMenu();
            ContextMenuStrip = _menu;
            _stack.ContextMenuStrip = _menu;
            _hud.ContextMenuStrip = _menu;
            _infoOverlay.ContextMenuStrip = _menu;
            _remoteOsd.ContextMenuStrip = _menu;
            _videoHost.ContextMenuStrip = _menu;
            _netflixModePage.ContextMenuStrip = _menu;
            _settingsHudPage.ContextMenuStrip = _menu;
            if (_cinematicLibraryPage != null)
                _cinematicLibraryPage.ContextMenuStrip = _menu;
            try { _overlayHost.ContextMenuStrip = _menu; } catch { }
            try { _overlayHost.Surface.ContextMenuStrip = _menu; } catch { }
            try { _audioMetersHost.ContextMenuStrip = _menu; } catch { }
            AttachPlaybackContextMenuFallbacks();

            _menu.Opening += (_, e) =>
            {
                if (!HandlePlaybackContextMenuOpening(e))
                    return;
                RefreshMenuVisibility();
            };
            _menu.Opened += (_, __) =>
            {
                BeginContextMenuHudBlock();
            };
            _menu.Closed += (_, __) => EndContextMenuHudBlock();

            RefreshCinemaModeMenuState();
            _hud.SetExternalVolume(1f);
            Dbg.Level = Dbg.LogLevel.Info;

            try
            {
                var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
                var bigPath = Path.Combine(assets, "cinecore_icon_512.ico");
                var smallPath = Path.Combine(assets, "cinecore_icon.ico");
                if (File.Exists(bigPath)) _iconBig = new Icon(bigPath);
                if (File.Exists(smallPath)) _iconSmall = new Icon(smallPath);
                // Icona ufficiale (la stessa dell'exe e dei collegamenti), nelle due misure.
                var brandPath = Path.Combine(assets, "cinecore.ico");
                if (File.Exists(brandPath))
                {
                    _iconBig ??= new Icon(brandPath, 256, 256);
                    _iconSmall ??= new Icon(brandPath, 32, 32);
                }
                _iconBig ??= AssetIconService.CreateWindowIcon("movie", 64);
                _iconSmall ??= AssetIconService.CreateWindowIcon("play", 32);
                if (_iconBig != null) this.Icon = _iconBig;
                else if (_iconSmall != null) this.Icon = _iconSmall;
            }
            catch { }
            try { _placeholderBrandLogo ??= LoadBrandLogoImageCopy(160); } catch { }

            // Avvio normale: prepara e mostra subito la home della libreria PRIMA del primo paint.
            // Così il form non nasce nero aspettando OnShown/BeginInvoke.
            try
            {
                var startupArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
                bool hasStartupMedia = NormalizeCommandLineMediaPaths(startupArgs).Count > 0;
                if (!hasStartupMedia)
                    ShowDefaultLibrary(stopCurrent: false);
            }
            catch { }
        }
        private string BuildHudInfoLine()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_currentPath) && _engine != null)
                {
                    bool bitstream = IsBitstream();
                    if (_audioOutPref == AudioOutPref.ForcePcm)
                        bitstream = false;

                    var details = new List<string>(4);
                    if (_currentMediaHasVideo)
                    {
                        int width = _info?.Width ?? _currentWebWidth;
                        int height = _info?.Height ?? _currentWebHeight;
                        if (width > 0 && height > 0)
                            details.Add($"{width}×{height}");

                        string codec = !string.IsNullOrWhiteSpace(_currentWebVideoCodec)
                            ? _currentWebVideoCodec!.Trim().ToUpperInvariant()
                            : (_info?.VideoCodec is AVCodecID codecId && codecId != AVCodecID.AV_CODEC_ID_NONE
                                ? codecId.ToString().Replace("AV_CODEC_ID_", string.Empty, StringComparison.OrdinalIgnoreCase)
                                : string.Empty);
                        if (!string.IsNullOrWhiteSpace(codec))
                            details.Add(codec);
                    }
                    else
                    {
                        string audio = PrettyAudioInFromProbe(_info);
                        if (!string.IsNullOrWhiteSpace(audio) && !string.Equals(audio, "n/d", StringComparison.OrdinalIgnoreCase))
                            details.Add(audio);
                    }

                    details.Add(bitstream ? "Bitstream" : "PCM");
                    return string.Join(" • ", details.Distinct(StringComparer.OrdinalIgnoreCase));
                }
            }
            catch { }

            try
            {
                return _lblStatus?.Text ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void RestoreWledOnAppExit()
        {
            try
            {
                CancelPendingWledPauseRestore();
                CancelPendingWledTransition();
            }
            catch { }

            if (!_wledInitialStateCaptured && !_wledRestoreOnExit && _wledLastSentOn != false)
                return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
                var restoreTask = RestoreWledInitialStateAsync(Math.Min(WLED_FADE_MS, 250), cts.Token);
                Task.WhenAny(restoreTask, Task.Delay(950)).GetAwaiter().GetResult();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { FilmVolumeLeave(); } catch { }
            _closingForExit = true;
            _networkVolumeLifetime.Cancel();
            ReleaseDiscMount();
            try { _audioSyncJob?.Cancel.Cancel(); } catch { }
            ArmFastProcessExitWatchdog();

            try { Interlocked.Exchange(ref _openCts, null)?.Cancel(); } catch { }
            try { _threadedVideoLoading.Dispose(); } catch { }
            try { DetachMpcvrChildWindowHooks(); } catch { }
            try { ForceHidePlaybackOverlaySurfaces(); } catch { }

            try
            {
                _pipAudioMeterRestartTimer?.Stop();
                _pipAudioMeterRestartTimer?.Dispose();
                _pipAudioMeterRestartTimer = null;
                _externalOpenBatchTimer?.Stop();
                _externalOpenBatchTimer?.Dispose();
                _externalOpenBatchTimer = null;
                _pendingExternalOpenPaths.Clear();
                TopMost = false;
                Opacity = 0;
                ShowInTaskbar = false;
                Hide();
            }
            catch { }

            try { _refresh.RestoreIfChanged(); } catch { }
            try { if (_engine is DirectShowUnifiedEngine ds) ds.RestoreMadVrCadenceSettings(); } catch { }
            try { RestoreWledOnAppExit(); } catch { }
            try { IntroOutroScanService.StatusChanged -= OnIntroOutroScanStatusChanged; } catch { }
            try { IntroOutroScanService.Shutdown(); } catch { }

            try { CancelPlaceholderBackdropFetch(); } catch { }
            try
            {
                var cts = Interlocked.Exchange(ref _lyricsCts, null);
                cts?.Cancel();
                cts?.Dispose();
            }
            catch { }
            try { CancelPendingWledPauseRestore(); } catch { }
            try { CancelPendingWledTransition(); } catch { }

            try
            {
                FastDetachPlaybackForAppExit();
            }
            catch
            {
                try
                {
                    if (_engine is DirectShowUnifiedEngine directShowEngine)
                        directShowEngine.DisposeForAppExit();
                    else
                        _engine?.Dispose();
                    _engine = null;
                }
                catch { }
            }

            try
            {
                if (_remote is IDisposable remoteDisposable)
                    remoteDisposable.Dispose();
            }
            catch { }

            try
            {
                if (_playbackKeyboardFilter != null)
                {
                    Application.RemoveMessageFilter(_playbackKeyboardFilter);
                    _playbackKeyboardFilter = null;
                }

                if (_inputModeFilter != null)
                {
                    Application.RemoveMessageFilter(_inputModeFilter);
                    _inputModeFilter = null;
                }
            }
            catch { }

            try { _hudWakePollTimer.Stop(); } catch { }
            try { _hudWakePollTimer.Dispose(); } catch { }
            try { _mpcvrChildOverlayRegion?.Dispose(); } catch { }
            _mpcvrChildOverlayRegion = null;
            try { _videoVignette?.Dispose(); } catch { }
            try { _vignetteBitmap?.Dispose(); } catch { }
            try { _mpcvrBitmapOverlayHost?.Dispose(); } catch { }
            try { _mpcvrOverlayStagingHost?.Dispose(); } catch { }
            try { _mpcvrOverlayPopup.Dispose(); } catch { }

            try { _uiIdleTimer.Stop(); } catch { }
            try { _uiIdleTimer.Dispose(); } catch { }
            try { _lyricsService.Dispose(); } catch { }

            base.OnFormClosing(e);
        }

        private int _fastExitWatchdogArmed;
        private void ArmFastProcessExitWatchdog()
        {
            if (Interlocked.Exchange(ref _fastExitWatchdogArmed, 1) != 0)
                return;

            try
            {
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(900).ConfigureAwait(false); } catch { }
                    try { Environment.Exit(0); } catch { }
                });
            }
            catch { }
        }

        private void FastDetachPlaybackForAppExit()
        {
            _stopping = true;

            try { UnbindGraphNotify(); } catch { }
            try { StopAudioMeters(asyncStop: true); } catch { }

            var engine = _engine;
            _engine = null;

            if (engine != null)
            {
                try { engine.BindUpdateCallback(null); } catch { }
                try { if (_engineStatusHandler != null) engine.OnStatus -= _engineStatusHandler; } catch { }
                try { if (_engineProgressHandler != null) engine.OnProgressSeconds -= _engineProgressHandler; } catch { }
                try { if (_engineBitstreamHandler != null) engine.OnBitstreamChanged -= _engineBitstreamHandler; } catch { }

                try
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (engine is DirectShowUnifiedEngine directShowEngine)
                                directShowEngine.DisposeForAppExit();
                            else
                                engine.Dispose();
                        }
                        catch { }
                    });
                }
                catch
                {
                    try
                    {
                        if (engine is DirectShowUnifiedEngine directShowEngine)
                            directShowEngine.DisposeForAppExit();
                        else
                            engine.Dispose();
                    }
                    catch { }
                }
            }

            _engineStatusHandler = null;
            _engineProgressHandler = null;
            _engineUpdateHandler = null;
            _engineBitstreamHandler = null;
            try { _refresh.RestoreIfChanged(); } catch { }
            try { HidePipMode(dispose: true); } catch { }
            try { ResetAudioOverlayState(); } catch { }
            try { if (_videoLoading != null) _videoLoading.Visible = false; } catch { }
            try { _hud.Visible = false; } catch { }
            try { _infoOverlay.Visible = false; } catch { }
            try { _photoHud.Visible = false; } catch { }
            StopPhotoSlideshow();
            try { _thumb.Close(); } catch { }
            try { _previewCache.Clear(); } catch { }
            Interlocked.Increment(ref _previewReqSerial);
            Interlocked.Exchange(ref _previewWorkerRunning, 0);
            _previewOverlayInitialized = false;
        }
        private void OpenFileWithDialog()
        {
            using var ofd = new OpenFileDialog
            {
                Title = Tx("Apri file multimediale", "Open media file"),
                Filter =
                    Tx("Video, audio e immagini", "Video, audio, and images") + "|*.mkv;*.mp4;*.m2ts;*.ts;*.mov;*.avi;*.wmv;*.webm;*.mts;*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wav;*.mka;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|" +
                    Tx("Solo video", "Video only") + "|*.mkv;*.mp4;*.m2ts;*.ts;*.mov;*.avi;*.wmv;*.webm;*.mts|" +
                    Tx("Solo audio", "Audio only") + "|*.mka;*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wav|" +
                    Tx("Solo immagini", "Images only") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|" +
                    Tx("Tutti i file", "All files") + "|*.*",
                RestoreDirectory = true,
                Multiselect = false
            };

            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                SkipLoadingIfActive();      // come quando apri da cmd
                // Stessa strada di "Apri con": con OpenPath diretto la libreria restava davanti e il
                // film partiva sotto, invisibile.
                PlayLibraryRequestedPath(ofd.FileName, resumeSeconds: null);
            }
        }

        private static string SelectBestRemoteLanIp()
        {
            try
            {
                var ips = (RemoteServer.LocalIPv4List() ?? Array.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Where(s => !s.StartsWith("127.", StringComparison.OrdinalIgnoreCase))
                    .Where(s => !s.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                bool Is172Private(string s)
                {
                    var parts = s.Split('.');
                    return parts.Length >= 2 &&
                           string.Equals(parts[0], "172", StringComparison.OrdinalIgnoreCase) &&
                           int.TryParse(parts[1], out int b) && b >= 16 && b <= 31;
                }

                return ips.FirstOrDefault(s => s.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase))
                    ?? ips.FirstOrDefault(s => s.StartsWith("10.", StringComparison.OrdinalIgnoreCase))
                    ?? ips.FirstOrDefault(Is172Private)
                    ?? ips.FirstOrDefault()
                    ?? "localhost";
            }
            catch
            {
                return "localhost";
            }
        }

        /// <summary>
        /// Pairing card for the phone remote: a QR code that opens the remote already paired (the
        /// link carries the PIN), with the address and the PIN underneath for who types them.
        /// </summary>
        private MpcvrBitmapOverlayForm? _pairSheet;
        private Bitmap? _pairBitmap;
        private System.Windows.Forms.Timer? _pairFade;
        private PairEscapeFilter? _pairEscape;

        private sealed class PairEscapeFilter : IMessageFilter
        {
            private readonly Action _close;
            public PairEscapeFilter(Action close) => _close = close;
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != 0x0100 || (Keys)(int)m.WParam != Keys.Escape) return false;
                _close();
                return true;
            }
        }

        /// <summary>
        /// Abbinamento del telefono: l'interfaccia sotto si scurisce con una vignettatura (piu' fitta al
        /// centro) e sopra resta solo il codice, grande e senza riquadro; la X solita sta sulla
        /// vignettatura, in alto a destra. Un clic o Esc chiude. Il PIN viaggia nel collegamento.
        /// </summary>
        private void ShowPairingBanner(string pin)
        {
            if (_pairSheet != null) return;

            string ip = SelectBestRemoteLanIp();
            var remote = _remote;
            string directUrl = remote?.UrlForHost(ip) ?? $"http://{ip}:9234";
            string pairingUrl = directUrl.TrimEnd('/') + "/?pin=" + Uri.EscapeDataString(pin ?? string.Empty);
            Utilities.QrCode? code = null;
            try { code = Utilities.QrCode.Encode(pairingUrl); } catch { }
            if (code == null) return;

            Rectangle area = RectangleToScreen(ClientRectangle);
            if (area.Width < 200 || area.Height < 200) return;
            float scale = Math.Max(1f, DeviceDpi / 96f);
            var bitmap = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(150, 0, 0, 0));
                // Vignettatura: quasi nera dietro il codice, piu' leggera verso i bordi.
                int reach = (int)(Math.Min(area.Width, area.Height) * 0.62f);
                var glow = new Rectangle(area.Width / 2 - reach, area.Height / 2 - reach, reach * 2, reach * 2);
                using (var shape = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    shape.AddEllipse(glow);
                    using var brush = new System.Drawing.Drawing2D.PathGradientBrush(shape)
                    {
                        CenterColor = Color.FromArgb(246, 0, 0, 0),
                        SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) },
                        FocusScales = new PointF(0.46f, 0.46f)
                    };
                    g.FillPath(brush, shape);
                }
                // Il codice: moduli bianchi direttamente sulla vignettatura, a pixel interi.
                int side = (int)(Math.Min(area.Width, area.Height) * 0.50f);
                int module = Math.Max(2, side / code.Size);
                side = module * code.Size;
                int left = (area.Width - side) / 2, top = (area.Height - side) / 2;
                using (var white = new SolidBrush(Color.White))
                    for (int y = 0; y < code.Size; y++)
                        for (int x = 0; x < code.Size; x++)
                            if (code[x, y]) g.FillRectangle(white, left + x * module, top + y * module, module, module);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int closeSide = (int)(40 * scale), inset = (int)(28 * scale);
                HUD.Theme.DrawCloseButton(g, new Rectangle(area.Width - inset - closeSide, inset, closeSide, closeSide), true);
            }

            var overlay = new MpcvrBitmapOverlayForm { Cursor = Cursors.Hand };
            overlay.MouseUp += (_, _) => HidePairingBanner();
            _pairSheet = overlay;
            _pairBitmap = bitmap;
            _pairEscape = new PairEscapeFilter(HidePairingBanner);
            Application.AddMessageFilter(_pairEscape);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            overlay.ShowBitmap(this, area, bitmap, Color.Magenta, false, 0);
            var fade = new System.Windows.Forms.Timer { Interval = 15 };
            _pairFade = fade;
            fade.Tick += (_, _) =>
            {
                if (!ReferenceEquals(_pairSheet, overlay) || overlay.IsDisposed) { fade.Stop(); return; }
                double t = Math.Clamp(clock.Elapsed.TotalMilliseconds / 180d, 0, 1);
                overlay.ShowBitmap(this, area, bitmap, Color.Magenta, false, (byte)Math.Round(255 * (1 - Math.Pow(1 - t, 3))));
                if (t >= 1) fade.Stop();
            };
            fade.Start();
        }

        private void HidePairingBanner()
        {
            var overlay = _pairSheet;
            if (overlay == null) return;
            _pairSheet = null;
            try { _pairFade?.Stop(); _pairFade?.Dispose(); } catch { }
            _pairFade = null;
            try { if (_pairEscape != null) Application.RemoveMessageFilter(_pairEscape); } catch { }
            _pairEscape = null;
            var bitmap = _pairBitmap;
            _pairBitmap = null;
            void Finish()
            {
                try { overlay.HideOverlay(); overlay.Dispose(); } catch { }
                try { bitmap?.Dispose(); } catch { }
            }
            // Esce come e' entrato, in dissolvenza (prima spariva di colpo).
            if (bitmap == null || overlay.IsDisposed || !overlay.Visible || _closingForExit) { Finish(); return; }
            Rectangle area = overlay.Bounds;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var fade = new System.Windows.Forms.Timer { Interval = 15 };
            fade.Tick += (_, _) =>
            {
                double t = Math.Clamp(clock.Elapsed.TotalMilliseconds / 160d, 0, 1);
                try { if (!overlay.IsDisposed) overlay.ShowBitmap(this, area, bitmap, Color.Magenta, false, (byte)Math.Round(255 * (1 - t) * (1 - t))); } catch { t = 1; }
                if (t < 1) return;
                fade.Stop(); fade.Dispose();
                Finish();
            };
            fade.Start();
        }

        private static int ParseKbpsFromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            var m = Regex.Match(name, @"(\d{2,5})\s*(kb/s|kbps)", RegexOptions.IgnoreCase);
            return (m.Success && int.TryParse(m.Groups[1].Value, out int v)) ? v : 0;
        }

        private static string Fmt(double s)
        {
            if (double.IsNaN(s) || s < 0) s = 0;
            var ts = TimeSpan.FromSeconds(s);
            return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
        }

        private void ShowChaptersMenu()
        {
            if (_info == null || _info.Chapters.Count == 0) { _lblStatus.Text = Tx("Nessun capitolo rilevato", "No chapters detected"); return; }
            var menu = new ContextMenuStrip();
            ApplyDarkMenuTheme(menu);
            foreach (var (title, start) in _info.Chapters)
            {
                var it = new ToolStripMenuItem($"{Fmt(start)}  {title}"); double s = start;
                it.Click += (_, __) =>
                {
                    if (_engine != null)
                    {
                        PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: s);
                        _engine.PositionSeconds = s;
                    }
                    _hud.ShowOnce(1200);
                };
                menu.Items.Add(it);
            }
            menu.Show(Cursor.Position);
        }

        private void EndTimelinePreview()
        {
            try
            {
                _scrubActive = false;
                _scrubPending = -1;
                _previewOverlayInitialized = false;

                var old = Interlocked.Exchange(ref _thumbCts, null);
                try { old?.Cancel(); } catch { }
                try { old?.Dispose(); } catch { }

                Interlocked.Increment(ref _previewReqSerial);
                try { _hud.SetPreview(null, _engine?.PositionSeconds ?? 0); } catch { }
            }
            catch { }
        }

        private void SyncHudPlayingState()
        {
            try
            {
                bool playing = _engine != null && !_paused && !IsPhotoMode && !_preOpenPlaceholderGateActive;
                _lastKnownPlaybackPosition = Math.Max(0, _engine?.PositionSeconds ?? 0);
                _timelineSampleTimestamp = Stopwatch.GetTimestamp();
                _hud?.SetPlaying(playing);
            }
            catch { }
        }




    }
}
