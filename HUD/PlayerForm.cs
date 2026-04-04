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
    public sealed class PlayerForm : Form
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
            try { return new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point); }
            catch { return new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point); }
        }
        private Panel _stack = null!;
        private Panel _videoHost = null!;
        private Panel _audioMetersHost = null!;
        private HudOverlay _hud = null!;
        private RemoteOsdOverlay _remoteOsd = null!;
        private InfoOverlay _infoOverlay = null!;
        private SplashOverlay _splash = null!;
        private Label _lblStatus = null!;
        private AudioOnlyOverlay _audioOnlyBanner = null!;
        private LoadingOverlay _loading = null!;
        // === Audio-only meters (LiveCharts) ===
        private AudioMetersLiveCharts? _audioMeters;
        private LoopbackSampler? _audioSampler;

        private ContextMenuStrip _menu = null!;
        private TableLayoutPanel _rootLayout = null!;
        private IPlaybackEngine? _engine;
        private string? _currentPath;
        // Categoria libreria dell'ultimo media aperto (serve per il titolo HUD normalizzato).
        private string? _currentLibraryCategory;
        // URL audio separato (es. YouTube DASH) trovato dal WebMediaResolver
        private string? _currentWebAudioUrl;
        private MediaProbe.Result? _info;

        private string? _selectedAudioRendererName;
        private bool _selectedRendererLooksHdmi;
        private Stereo3DMode _stereo = Stereo3DMode.None;
        private HDRMode _hdr = HDRMode.Auto;
        private bool _scrubActive = false;
        private double _scrubPending = -1;

        private double _duration;
        private bool _paused;
        private bool _currentMediaHasVideo = false;

        // ===== Extras (Placeholder pre-film + Pre-roll demo) =====
        private PausePlaceholderOverlay _pausePlaceholder = null!;
        // Placeholder gate: se attivo, quando apri un NUOVO film (dopo il primo) prima mostra il placeholder.
        // Il film parte solo quando premi Play.
        private bool _pausePlaceholderEnabled = false;
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
        private bool _hasOpenedMediaOnce = false;

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

        // Passaggio demo → film: sopprimi splash/loading “cinematografico” una sola volta.
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
        private bool _libraryRemoteActivationArmed = false;
        private bool _libraryHighlightNeedsReactivation = false;

        // Menu contestuale aperto/in arrivo: blocca l'auto-hide HUD senza interferire con l'apertura del menu.
        private bool _contextMenuActive = false;
        private bool _contextMenuPending = false;

        // Fullscreen: guard contro re-entrancy (evita glitch grafici)
        private bool _fullscreenTransitioning = false;
        private int _suspendFullscreenActivationKeepAlive = 0;
        private int _prevWindowStyle = 0;
        private bool _prevControlBox = true;
        private bool _prevMinimizeBox = true;
        private bool _prevMaximizeBox = true;

        // DirectShow: notifica fine riproduzione (EC_COMPLETE) → ritorno libreria affidabile
        private IMediaEventEx? _graphEvents;
        private const int WM_APP = 0x8000;
        private const int WM_GRAPHNOTIFY = WM_APP + 0x1A15;

        // preferenza lingua sottotitoli (solo per “Disattiva sottotitoli → Auto Forced”)
        private string? _preferredSubtitleLangKey;
        private bool _subtitleAutoForcedMode = false;


        // Fallback: alcuni renderer (o finestre layered/click-through) non propagano MouseMove ai controlli WinForms.
        // Polliamo la posizione del mouse per riattivare l'HUD quando necessario (es. “Apri con…” da Esplora risorse).
        private readonly System.Windows.Forms.Timer _hudWakePollTimer = new() { Interval = 85 };
        private Point _hudWakeLastMousePos;

        // Auto ritorno alla libreria quando il contenuto termina (EOF)
        private bool _autoReturnToLibraryOnEnd = true;
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
        private volatile bool _previewBusy;
        // === Timeline preview (latest-wins + cache) ===
        private int _previewReqSerial = 0;
        private double _previewReqSeconds = 0;
        private const int TIMELINE_PREVIEW_W = 240;
        private const int TIMELINE_CACHE_CAPACITY = 160;
        private const double TIMELINE_CACHE_QUANTUM_SEC = 1.0 / 120.0;
        private readonly PreviewCache _previewCache =
            new PreviewCache(TIMELINE_CACHE_CAPACITY, TIMELINE_CACHE_QUANTUM_SEC);

        private FormWindowState _prevState; private FormBorderStyle _prevBorder; private Rectangle _prevBounds;
        private readonly OverlayHostForm _overlayHost;
        private InlineOverlayPanel? _overlayInlineHost;

        private Rectangle _lastVideoDestInForm = Rectangle.Empty;

        private bool _enableUpscaling = false;
        private int _targetFps = 0;
        private bool _preferBitstreamUi = true;
        private readonly DisplayModeSwitcher _refresh = new();
        // === Preferenza UI per PCM/Bitstream ===
        private enum AudioOutPref { Auto, ForcePcm }
        private AudioOutPref _audioOutPref = AudioOutPref.Auto; // default: Auto

        private SettingsModal _settingsModal = null!;
        private CreditsModal _creditsModal = null!;

        private static readonly VRChoice[] ORDER_HDR = { VRChoice.MADVR, VRChoice.MPCVR };
        private static readonly VRChoice[] ORDER_SDR = { VRChoice.EVR };

        private ToolStripMenuItem _mAudioLang = null!;
        private ToolStripMenuItem _mSubtitles = null!;
        private ToolStripMenuItem _mAudioOut = null!;
        private ToolStripMenuItem _mChapters = null!;
        private ToolStripMenuItem? _mQueueMenuItem;
        private ToolStripMenuItem? _mLoopTrack;
        private WeakReference<PlaybackQueueEditorForm>? _playbackQueueEditorRef;
        private string? _singleTrackLoopPath;
        private bool _singleTrackLoopEnabled;

        private long _vPrevBytes = 0;
        private DateTime _vPrevWhen = DateTime.MinValue;
        private int _videoBitrateNowKbps = 0;

        private MediaLibraryPage? _libraryPage;
        private readonly List<string> _playbackQueue = new();
        private readonly List<string> _playbackQueueHistory = new();
        private readonly Random _playbackQueueRandom = new();
        private int _playbackQueueIndex = -1;
        private bool _playbackQueueShuffleMode = false;
        private bool _playbackQueueSessionActive = false;
        private bool _nextOpenBelongsToPlaybackQueue = false;
        private bool _playbackQueueTransitionInProgress = false;
        private bool _libraryPrewarmScheduled = false;
        private const int HUD_HOTZONE_H = 160;

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int ShowCursor(bool bShow);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
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
        private const int GWL_STYLE = -16;
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
        private enum HdrUiProfile { Auto, Passthrough, ToneMapSdr, LutSdr }
        private HdrUiProfile _hdrProfile = HdrUiProfile.Auto;
        private bool _lutWarned = false;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

        private Icon? _iconBig; private Icon? _iconSmall;

        private Action<string>? _engineStatusHandler;
        private Action<double>? _engineProgressHandler;
        private Action? _engineUpdateHandler;

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
        // Timestamp ultimo campione valido “ora” (per non sovrascrivere col fallback)
        private DateTime _aNowTs = DateTime.MinValue, _vNowTs = DateTime.MinValue;

        // === Caricamento media (overlay nero + spinner) ===
        private VideoLoadingMask _videoLoading = null!;
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
        private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 250 };
        private bool _statsTimerInitialized = false;
        // --- controllo remoto web ---
        private RemoteServer? _remote;

        // ultimo snapshot di stato che mandiamo al telefono
        private volatile RemoteState _remoteSnapshot = new RemoteState();

        // lettura "sicura" per il server (thread diverso)
        private RemoteState GetRemoteState() => System.Threading.Volatile.Read(ref _remoteSnapshot);

        // pubblica uno snapshot nuovo (non muta proprietà sull'oggetto vecchio)
        private void PublishRemoteState(double? positionOverride = null)
        {
            try
            {
                var eng = _engine;

                double pos = positionOverride ?? (eng?.PositionSeconds ?? 0);

                double dur = 0;
                try { dur = eng?.DurationSeconds ?? 0; } catch { dur = 0; }

                if (dur <= 0) dur = _duration;

                // importantissimo: appena la durata diventa disponibile, aggiorna _duration
                if (_duration <= 0 && dur > 0) _duration = dur;

                string? title = null;
                try
                {
                    title = _hud?.GetTitle?.Invoke()?.Trim();
                    if (pos == 0) title = "";
                }
                catch { }

                var st = new RemoteState
                {
                    Title = title,
                    Position = Math.Max(0, pos),
                    Duration = Math.Max(0, dur),
                    OutputHdr = (_info?.IsHdr == true) && (_hdr != HDRMode.Off),
                    Is3D = _stereo != Stereo3DMode.None,
                    Bitstream = (_engine?.IsBitstreamActive() ?? false)
                };

                System.Threading.Volatile.Write(ref _remoteSnapshot, st);
            }
            catch { }
        }

        // --- Modalità foto ---
        private PhotoHudOverlay _photoHud = null!;
        private List<string> _imageFiles = new();
        private int _imageIndex = -1;
        private bool IsPhotoMode => _engine is ImagePlaybackEngine;
        // true se il media corrente è un file locale (non HTTP/stream)
        private bool _isLocalFile;

        private AudioOnlyOverlay BuildAudioOnlyBanner() => new()
        {
            Dock = DockStyle.Fill,
            Visible = false,
            ImagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "audioOnly.png"),
            Caption = "Audio Only"
        };

        private void RedrawHome()
        {
            if (_splash == null) return;
            _splash.Invalidate(true);
            _splash.Refresh();
            _stack?.Invalidate(true);
            _stack?.Update();
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

        public PlayerForm()
        {
            Text = "Cinecore Player 2025";
            MinimumSize = new Size(1040, 600);
            BackColor = Color.FromArgb(18, 18, 18);
            DoubleBuffered = true;

            // Niente IMessageFilter globale: la gestione mouse/DPAD viene fatta
            // in modo leggero solo su click (vedi WndProc) per evitare flicker.

            // In fullscreen (borderless) manteniamo il focus, MA senza rompere i dialog di Windows
            // (OpenFileDialog / FolderBrowserDialog) che altrimenti vengono “coperti”/perdono il focus.
            Deactivate += (_, __) =>
            {
                if (FormBorderStyle != FormBorderStyle.None)
                    return;
                if (_suspendFullscreenActivationKeepAlive > 0)
                    return;

                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var fg = GetForegroundWindow();
                        if (fg != IntPtr.Zero)
                        {
                            GetWindowThreadProcessId(fg, out var pid);
                            if (pid == (uint)Process.GetCurrentProcess().Id)
                            {
                                // #32770 = common dialog (file/folder picker ecc.)
                                var cls = GetWindowClassName(fg);
                                if (string.Equals(cls, "#32770", StringComparison.Ordinal))
                                    return;
                            }
                        }
                    }
                    catch { }

                    try { Activate(); } catch { }
                }));
            };

            _rootLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _rootLayout.BackColor = Color.Black;

            _stack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _videoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _videoDetachHost = new Panel
            {
                Size = new Size(4, 4),
                Visible = false,
                BackColor = Color.Black,
                Location = new Point(-5000, -5000)
            };

            // HUD
            _hud = new HudOverlay { Dock = DockStyle.Fill, AutoHide = true, Visible = false };
            _hud.TimelineVisible = false;

            // OSD centrale (stile "Apple") mostrato SOLO per comandi che arrivano dal remote server
            _remoteOsd = new RemoteOsdOverlay
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = Color.Transparent
            };

            _infoOverlay = new InfoOverlay { Dock = DockStyle.Top, Visible = false, AutoHeight = true, MaxCardHeight = 420 };

            _overlayHost = new OverlayHostForm();
            AddOwnedForm(_overlayHost);
            _overlayHost.Visible = false;

            _hud.BackColor = Color.Transparent;
            _infoOverlay.BackColor = Color.Transparent;

            _splash = new SplashOverlay { Dock = DockStyle.Fill, Visible = true };
            _splash.OpenRequested += OpenFile;
            _splash.SettingsRequested += ShowSettingsModal;
            _splash.CreditsRequested += ShowCreditsModal;

            _loading = new LoadingOverlay { Dock = DockStyle.Fill, Visible = true };
            _loading.Completed += () =>
            {
                _loading.Visible = false;
                _splash.Visible = (_currentPath == null);
                _hud.Visible = false;
                _hud.TimelineVisible = false;
                BringOverlaysToFront();
                try { ScheduleLibraryPrewarm(); } catch { }
            };

            // Overlay di caricamento media (NO splash): nero + spinner con colori tema
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

            // importantissimo: spesso l'evento resta "intrappolato" nel controllo figlio
            if (_audioMeters != null)
            {
                _audioMeters.MouseMove += (_, __) => NoteMouseActivity();
            }

            _stack.Controls.Add(_videoDetachHost);
            _stack.Controls.Add(_videoHost);
            _stack.Controls.Add(_audioMetersHost);
            _stack.Controls.Add(_videoLoading);
            _stack.Controls.Add(_loading);
            _stack.Controls.Add(_splash);

            // ===== Extras overlays =====
            _pausePlaceholder = new PausePlaceholderOverlay
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = _overlayHost.TransparencyKey
            };
            _pausePlaceholder.SetFolder(_pausePlaceholderFolder);
            _overlayHost.Surface.Controls.Add(_pausePlaceholder);

            _overlayHost.Surface.Controls.Add(_audioOnlyBanner);
            _overlayHost.Surface.Controls.Add(_infoOverlay);
            _overlayHost.Surface.Controls.Add(_hud);
            _overlayHost.Surface.Controls.Add(_remoteOsd);
            // Stop preview quando l'HUD si nasconde (auto-hide o uscite dalla timeline)
            _hud.VisibleChanged += (_, __) =>
            {
                if (!_hud.Visible)
                {
                    _scrubActive = false;

                    var old = Interlocked.Exchange(ref _thumbCts, null);
                    try { old?.Cancel(); } catch { }

                    Interlocked.Increment(ref _previewReqSerial);
                    _hud.SetPreview(null, _engine?.PositionSeconds ?? 0);
                }
            };
            _hud.BringToFront();

            _audioOnlyBanner.BackColor = _overlayHost.TransparencyKey;

            // HUD minimale per le foto (solo frecce sx/dx)
            _photoHud = new PhotoHudOverlay
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = _overlayHost.TransparencyKey
            };
            _photoHud.PrevRequested += () => ShowPrevImage();
            _photoHud.NextRequested += () => ShowNextImage();
            _overlayHost.Surface.Controls.Add(_photoHud);

            // ===== HUD wake + cursor auto-hide (3s mouse idle) =====
            // Criterio importante: reagiamo SOLO a movimento fisico del mouse in coordinate schermo.
            // Alcuni renderer/resize overlay generano MouseMove "sintetici" anche con puntatore fermo,
            // e finivano per riaprire subito l'HUD appena iniziava a nascondersi.
            void NoteMouseActivity()
            {
                var now = DateTime.UtcNow;
                if (_contextMenuActive || _contextMenuPending)
                {
                    EnsureCursorVisible();
                    return;
                }
                if (now < _suppressHudWakeUntilUtc)
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
                    bool movedEnough =
                        Math.Abs(screenPos.X - _hudWakeAnchorPos.X) >= HUD_WAKE_AFTER_AUTOHIDE_PX ||
                        Math.Abs(screenPos.Y - _hudWakeAnchorPos.Y) >= HUD_WAKE_AFTER_AUTOHIDE_PX;

                    if (!movedEnough)
                        return;

                    _hudWakeNeedsIntentionalMove = false;
                }

                _lastMouseMoveUtc = now;
                _lastHudActivityUtc = now;

                // Ripristina il cursore appena c'è attività reale
                EnsureCursorVisible();

                // In modalità foto NON vogliamo mai far comparire l’HUD video
                if (IsPhotoMode)
                {
                    try { _photoHud?.Wake(); } catch { }
                    return;
                }

                if (_engine == null) return;
                if (_splash.Visible || _loading.Visible) return;

                HudBump(HUD_IDLE_HIDE_MS, allowWhenRemote: false, showTimeline: true);
            }

            // MouseMove vero (quando disponibile)
            _overlayHost.Surface.MouseMove += (_, __) => NoteMouseActivity();
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

            _settingsModal = new SettingsModal { Visible = false, Dock = DockStyle.Fill };
            AttachMouseAnchorTracking(_settingsModal);
            _settingsModal.ApplyClicked += (fps, upscale, preferBitstream) =>
            {
                _targetFps = fps;
                _enableUpscaling = upscale;
                _preferBitstreamUi = preferBitstream;

                if (_targetFps == 0) { try { _refresh.RestoreIfChanged(); } catch { } }
                if (_enableUpscaling && _manualRendererChoice != VRChoice.MADVR) _manualRendererChoice = VRChoice.MADVR;

                try { _engine?.SetUpscaling(_enableUpscaling); } catch { }

                if (_info != null && _engine != null)
                    UpdateInfoOverlay(_manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First()), _info.IsHdr);

                ReopenSame();
            };
            _settingsModal.Closed += () =>
            {
                _settingsModal.Visible = false;
                if (_settingsModal.Tag is bool wasInline && !wasInline) UseOverlayInline(false);
                if (_splash.Visible) RedrawHome();
                BringOverlaysToFront();
            };

            _creditsModal = new CreditsModal { Visible = false, Dock = DockStyle.Fill };
            AttachMouseAnchorTracking(_creditsModal);
            _creditsModal.Closed += () =>
            {
                _creditsModal.Visible = false;
                _overlayHost.SetInteractive(false);
                if (_creditsModal.Tag is bool wasInline && wasInline) UseOverlayInline(true);
                if (_overlayInlineHost != null) _overlayInlineHost.Visible = false;
                if (_splash.Visible) RedrawHome();
                BringOverlaysToFront();
            };

            _overlayHost.Surface.Controls.Add(_settingsModal);
            _overlayHost.Surface.Controls.Add(_creditsModal);

            BringOverlaysToFront();

            _splash.Visible = false;
            _hud.Visible = false;
            _infoOverlay.Visible = false;
            _loading.Start();

            _rootLayout.Controls.Add(_stack, 0, 0);
            _lblStatus = new Label { Text = "Pronto" };

            Controls.Add(_rootLayout);
            BringOverlaysToFront();

            _hud.GetTime = () => (_engine?.PositionSeconds ?? 0, _duration);
            _hud.GetInfoLine = () => BuildHudInfoLine();
            _hud.GetTitle = () =>
            {
                // Priorità titolo:
                // 1) nome "originale" estratto (utile per URL/YouTube)
                // 2) path corrente
                // 3) sorgente thumbnailer / ultima probe
                string? raw = !string.IsNullOrWhiteSpace(_originalVideoName) ? _originalVideoName
                          : (!string.IsNullOrWhiteSpace(_currentPath) ? _currentPath
                          : (!string.IsNullOrWhiteSpace(_thumb?.SourcePath) ? _thumb.SourcePath
                          : MediaProbe.LastProbedPath));

                if (string.IsNullOrWhiteSpace(raw)) return "";

                // In categoria Film mostriamo il titolo normalizzato del contenuto,
                // incluse serie TV con stagione/episodio.
                try
                {
                    string candidate = !string.IsNullOrWhiteSpace(_currentPath) ? _currentPath! : raw;
                    candidate = NormalizeMediaPathForDisplay(candidate);

                    if (ShouldUseMovieMetadataTitleForPath(candidate))
                    {
                        string bestTitle = MovieMetadataService.GetBestKnownDisplayTitle(candidate);
                        if (!string.IsNullOrWhiteSpace(bestTitle))
                            return bestTitle;
                    }
                }
                catch { }

                // Se è YouTube e non abbiamo il titolo reale, almeno non mostrare l'URL.
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

            _hud.TopSettingsClicked += () => ShowSettingsModal();
            _hud.TopInfoClicked += () =>
            {
                _infoOverlay.Visible = !_infoOverlay.Visible;
                if (_infoOverlay.Visible)
                    _infoOverlay.BringToFront();
            };

            _hud.VolumeChanged += v => ApplyVolume(v);

            _hud.SeekRequested += s =>
            {
                if (_engine == null || _duration <= 0) return;

                // NB: su alcune implementazioni dell'HUD l'evento SeekRequested viene sparato
                // continuamente mentre trascini la timeline. Se qui annulliamo l'anteprima
                // (come facevamo prima), la preview si aggiorna solo quando ti fermi.
                // Quindi: aggiorniamo solo la posizione, ma NON stoppare/cancellare il preview worker.
                _scrubPending = Math.Clamp(s, 0, Math.Max(0.01, _duration));
                try { _engine.PositionSeconds = _scrubPending; } catch { }

                // Mantieni lo stato di scrub attivo: la PreviewRequested lo setta comunque,
                // ma così evitiamo finestre in cui risulta false e scarta i frame pronti.
                _scrubActive = true;

                // Usa SeekRequested anche come driver per la preview: garantisce aggiornamento continuo
                // anche se l'HUD non genera PreviewRequested ad ogni mouse-move.
                try { OnPreviewRequested(_scrubPending, Point.Empty); } catch { }

                _hud.ShowOnce(1200);
            };

            _hud.PreviewRequested += (sec, pt) =>
            {
                _scrubActive = true;
                OnPreviewRequested(sec, pt);
            };

            _hud.SkipBack10Clicked += () => { SeekRelative(-10); _hud.ShowOnce(1200); };
            _hud.SkipForward10Clicked += () => { SeekRelative(10); _hud.ShowOnce(1200); };
            _hud.PrevChapterClicked += () => { SeekChapter(-1); _hud.ShowOnce(1200); };
            _hud.NextChapterClicked += () => { SeekChapter(+1); _hud.ShowOnce(1200); };
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
                                        if (_engine != null && _duration > 0 &&
                                            q.TryGetValue("pos", out var sp) &&
                                            double.TryParse(sp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var posScrub))
                                        {
                                            double sec = Math.Clamp(posScrub, 0, Math.Max(0.01, _duration));
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

                                            if (_engine != null && _duration > 0 &&
                                                q.TryGetValue("pos", out var s) &&
                                                double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pos))
                                            {
                                                sec = Math.Clamp(pos, 0, Math.Max(0.01, _duration));
                                                _scrubPending = sec;
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
                                        ShowLibrary();
                                        break;

                                    case "settings":
                                        StopRemoteScan();
                                        ShowSettingsModal();
                                        try { _hud?.Pulse(HudOverlay.ButtonId.TopSettings); } catch { }
                                        break;

                                    case "info":
                                        StopRemoteScan();
                                        _infoOverlay.Visible = !_infoOverlay.Visible;
                                        BringOverlaysToFront();
                                        try { _hud?.Pulse(HudOverlay.ButtonId.TopInfo); } catch { }
                                        break;

                                    case "hdr":
                                        StopRemoteScan();
                                        CycleHdrProfile();
                                        break;

                                    case "stereo":
                                        StopRemoteScan();
                                        if (q.TryGetValue("mode", out var m))
                                        {
                                            if (string.Equals(m, "off", StringComparison.OrdinalIgnoreCase)) Disable3DRestoreRenderer();
                                            else if (string.Equals(m, "sbs", StringComparison.OrdinalIgnoreCase)) Enable3D(Stereo3DMode.SBS);
                                            else if (string.Equals(m, "tab", StringComparison.OrdinalIgnoreCase)) Enable3D(Stereo3DMode.TAB);
                                        }
                                        else
                                        {
                                            if (_stereo == Stereo3DMode.None) Enable3D(Stereo3DMode.SBS);
                                            else if (_stereo == Stereo3DMode.SBS) Enable3D(Stereo3DMode.TAB);
                                            else Disable3DRestoreRenderer();
                                        }
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
                                        ShowLibrary();
                                        break;

                                    case "open":
                                        StopRemoteScan();
                                        if (q.TryGetValue("url", out var u) && !string.IsNullOrWhiteSpace(u))
                                        {
                                            _currentLibraryCategory = null;
                                            OpenPath(u);
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
                                EndRemoteCommandScope();
                            }
                        }));
                    }
                    catch { }
                }
            );

            // quando si abbina un device -> nascondi banner
            _remote.Paired += _ => { try { BeginInvoke(new Action(HidePairingBanner)); } catch { } };

            // quando un device NON abbinato prova a collegarsi -> mostra PIN (solo se serve)
            _remote.PairingRequested += pin =>
            {
                try { BeginInvoke(new Action(() => ShowPairingBanner(pin))); } catch { }
            };

            // avvio server
            try { _remote.Start(); } catch (Exception ex) { Debug.WriteLine("Remote.Start FAILED: " + ex.Message); }
            HidePairingBanner();

            void RelayoutVideo()
            {
                try
                {
                    if (_engine == null) return;
                    if (_videoHost == null || _videoHost.IsDisposed) return;
                    if (!_videoHost.IsHandleCreated) return;

                    UpdateVideoWindowForCurrentHost();
                }
                catch { /* best-effort */ }

                try { SyncOverlayToVideoRect(); } catch { }
            }

            LocationChanged += (_, __) => RelayoutVideo();
            SizeChanged += (_, __) => RelayoutVideo();

            _videoHost.SizeChanged += (_, __) => RelayoutVideo();

            // Extras folders + persisted toggles
            try { Directory.CreateDirectory(_pausePlaceholderFolder); } catch { }
            try { Directory.CreateDirectory(_preRollDemoFolder); } catch { }
            try { LoadExtrasConfig(); } catch { }
            try { _pausePlaceholder.SetFolder(_pausePlaceholderFolder); } catch { }

            BuildMenu();
            ContextMenuStrip = _menu;
            _stack.ContextMenuStrip = _menu;
            _hud.ContextMenuStrip = _menu;
            _videoHost.ContextMenuStrip = _menu;
            _splash.ContextMenuStrip = _menu;
            try { _overlayHost.ContextMenuStrip = _menu; } catch { }
            try { _overlayHost.Surface.ContextMenuStrip = _menu; } catch { }
            try { _audioMetersHost.ContextMenuStrip = _menu; } catch { }

            _menu.Opening += (_, e) =>
            {
                if (_loading.Visible)
                {
                    EndContextMenuHudBlock();
                    e.Cancel = true;
                    return;
                }
                BeginContextMenuHudBlock();
                RefreshMenuVisibility();
            };
            _menu.Opened += (_, __) => BeginContextMenuHudBlock();
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
                if (_iconBig != null) this.Icon = _iconBig;
                else if (_iconSmall != null) this.Icon = _iconSmall;
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

                    return bitstream ? "Audio Bitstream" : "Audio PCM";
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

            if (!_wledEnabled && !_wledRestoreOnExit && _wledLastSentOn != false)
                return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(650));
                var restoreTask = SendWledPowerAsync(true, Math.Min(WLED_FADE_MS, 250), cts.Token);
                Task.WhenAny(restoreTask, Task.Delay(700)).GetAwaiter().GetResult();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { RestoreWledOnAppExit(); } catch { }

            try { Interlocked.Exchange(ref _openCts, null)?.Cancel(); } catch { }
            try { CancelPlaceholderBackdropFetch(); } catch { }
            try { CancelPendingWledPauseRestore(); } catch { }
            try { CancelPendingWledTransition(); } catch { }

            try
            {
                SafeStop(toSplash: false);
            }
            catch
            {
                try
                {
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

            try { _hudWakePollTimer.Stop(); } catch { }
            try { _hudWakePollTimer.Dispose(); } catch { }

            try { _uiIdleTimer.Stop(); } catch { }
            try { _uiIdleTimer.Dispose(); } catch { }

            base.OnFormClosing(e);
        }
        private void OpenFileWithDialog()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Apri file multimediale",
                Filter =
                    "Video, audio e immagini|*.mkv;*.mp4;*.m2ts;*.ts;*.mov;*.avi;*.wmv;*.webm;*.mts;*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wav;*.mka;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|" +
                    "Solo video|*.mkv;*.mp4;*.m2ts;*.ts;*.mov;*.avi;*.wmv;*.webm;*.mts|" +
                    "Solo audio|*.mka;*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wav|" +
                    "Solo immagini|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|" +
                    "Tutti i file|*.*",
                RestoreDirectory = true,
                Multiselect = false
            };

            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                SkipLoadingIfActive();      // come quando apri da cmd
                _currentLibraryCategory = null;
                OpenPath(ofd.FileName);     // usa già tutta la tua logica HDR/renderer ecc.
            }
        }

        private void ShowPairingBanner(string pin)
        {
            HidePairingBanner(); // idempotente

            // Scegli un IP LAN “bello”
            string ip = "localhost";
            try
            {
                var ips = RemoteServer.LocalIPv4List();
                ip = ips.FirstOrDefault(s => s.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase))
                     ?? ips.FirstOrDefault(s => s.StartsWith("10.", StringComparison.OrdinalIgnoreCase))
                     ?? ips.FirstOrDefault(s => s.StartsWith("172.16.", StringComparison.OrdinalIgnoreCase))
                     ?? ips.FirstOrDefault()
                     ?? "localhost";
            }
            catch { }

            _pairBanner = new Panel
            {
                Width = 380,
                Height = 92,
                BackColor = Color.FromArgb(220, 15, 15, 17), // scuro traslucido
            };

            // The pairing banner is clickable, but must not be part of DPAD focus/navigation
            // (otherwise it steals focus and gets highlighted).
            _pairBanner.Tag = "nodpad";

            // bordo sottile
            _pairBanner.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, _pairBanner.Width - 1, _pairBanner.Height - 1));
            };

            var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var pic = new PictureBox
            {
                Size = new Size(56, 56),
                SizeMode = PictureBoxSizeMode.Zoom
            };

            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(logoPath))
            {
                try
                {
                    pic.Image = Image.FromFile(logoPath);
                    _placeholderBrandLogo?.Dispose();
                    _placeholderBrandLogo = pic.Image != null ? new Bitmap(pic.Image) : null;
                }
                catch { }
            }

            var lbl = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(235, 235, 235),
                Font = SafeSemibold(10f),
                Text = $"Telecomando:  http://cinecore-remote.local\nPIN: {pin}",
            };

            inner.Controls.Add(pic, 0, 0);
            inner.Controls.Add(lbl, 1, 0);
            _pairBanner.Controls.Add(inner);

            // posiziona in alto a destra
            _pairBanner.Left = this.ClientSize.Width - _pairBanner.Width - 12;
            _pairBanner.Top = 12;
            _pairBanner.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            Control parent = _stack;
            parent.Controls.Add(_pairBanner);
            BringOverlaysToFront();
            try { _pairBanner.BringToFront(); } catch { }

            // opzionale: click per copiare l'URL
            _pairBanner.Cursor = Cursors.Hand;
            _pairBanner.Click += (s, e) =>
            {
                try { Clipboard.SetText($"http://{ip}:9234"); } catch { }
                // feedback minimale sulla label
                lbl.Text = $"Telecomando:  http://{ip}:9234  (copiato)\nPIN: {pin}";
            };
        }

        private void HidePairingBanner()
        {
            if (_pairBanner == null) return;
            try
            {
                var parent = _pairBanner.Parent;
                _pairBanner.Dispose();
                parent?.Invalidate();
            }
            catch { }
            finally { _pairBanner = null; }
        }
        private void CycleHdrProfile()
        {
            // profili già presenti: Auto / Passthrough / ToneMapSdr / LutSdr
            if (_hdrProfile == HdrUiProfile.Auto) { _hdrProfile = HdrUiProfile.Passthrough; _hdr = HDRMode.Auto; _lblStatus.Text = "HDR: Passthrough (display HDR)"; }
            else if (_hdrProfile == HdrUiProfile.Passthrough) { _hdrProfile = HdrUiProfile.ToneMapSdr; _hdr = HDRMode.Off; _lblStatus.Text = "HDR: Tone-map → SDR (madVR)"; }
            else if (_hdrProfile == HdrUiProfile.ToneMapSdr) { _hdrProfile = HdrUiProfile.LutSdr; _hdr = HDRMode.Off; _lblStatus.Text = "HDR: 3DLUT → SDR (madVR)"; }
            else { _hdrProfile = HdrUiProfile.Auto; _hdr = HDRMode.Auto; _lblStatus.Text = "HDR: Auto (let madVR decide)"; }

            ReopenSame();
        }

        private int ProbeAudioAvgKbps()
        {
            try
            {
                if (_info == null) return 0;
                var t = _info.GetType();
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                object? GetMemberValue(string name)
                {
                    try
                    {
                        var p = t.GetProperty(name, flags);
                        if (p != null) return p.GetValue(_info);
                    }
                    catch { }

                    try
                    {
                        var f = t.GetField(name, flags);
                        if (f != null) return f.GetValue(_info);
                    }
                    catch { }

                    return null;
                }

                foreach (var name in new[] { "AudioBitrateKbps", "AudioAvgKbps" })
                {
                    var v = GetMemberValue(name);
                    if (v is int ik && ik > 0) return ik;
                    if (v is long lk && lk > 0) return (int)lk;
                    if (v is double dk && dk > 0) return (int)Math.Round(dk);
                }

                foreach (var name in new[] { "AudioBitrate", "AudioAvgBitrate" })
                {
                    var v = GetMemberValue(name);
                    if (v is long lbps && lbps > 0) return (int)Math.Round(lbps / 1000.0);
                    if (v is int ibps && ibps > 0) return (int)Math.Round(ibps / 1000.0);
                    if (v is double dbps && dbps > 0) return (int)Math.Round(dbps / 1000.0);
                }
            }
            catch { }
            return 0;
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool blockingOverlayVisible = _loading.Visible || (_videoLoading?.Visible == true);

            // =========================
            // 0) Media / remote keys (validi ovunque, anche in UI)
            // =========================
            if ((keyData & Keys.Modifiers) == Keys.None)
            {
                // Volume keys (molti telecomandi HW li mandano così)
                if (keyData == Keys.VolumeUp)
                {
                    _hud?.PerformVolumeDelta(+0.05f, ApplyVolume);
                    return true;
                }
                if (keyData == Keys.VolumeDown)
                {
                    _hud?.PerformVolumeDelta(-0.05f, ApplyVolume);
                    return true;
                }
                if (keyData == Keys.VolumeMute)
                {
                    try { _hud?.ToggleMuteFromUser(); } catch { }
                    return true;
                }

                // Media keys
                if (keyData == Keys.MediaPlayPause)
                {
                    TogglePlayPause();
                    return true;
                }
                if (keyData == Keys.MediaStop)
                {
                    CloseCurrentToLibrary();
                    return true;
                }
                if (keyData == Keys.MediaNextTrack)
                {
                    if (TrySkipPlaybackQueue(+1))
                        return true;
                    SeekChapter(+1);
                    _hud.Pulse(HudOverlay.ButtonId.NextChapter);
                    return true;
                }
                if (keyData == Keys.MediaPreviousTrack)
                {
                    if (TrySkipPlaybackQueue(-1))
                        return true;
                    SeekChapter(-1);
                    _hud.Pulse(HudOverlay.ButtonId.PrevChapter);
                    return true;
                }
            }

            if (blockingOverlayVisible)
            {
                if ((keyData & Keys.Modifiers) == Keys.None)
                {
                    if (keyData == Keys.F)
                    {
                        ToggleFullscreen();
                        try { _hud.Pulse(HudOverlay.ButtonId.Fullscreen); } catch { }
                        return true;
                    }

                    if (keyData == Keys.Enter || keyData == Keys.Space)
                    {
                        if (_preOpenPlaceholderGateActive)
                        {
                            TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: false);
                            return true;
                        }
                        return true;
                    }

                    if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                    {
                        if (FormBorderStyle == FormBorderStyle.None)
                        {
                            ToggleFullscreen();
                            return true;
                        }

                        if (_preOpenPlaceholderGateActive)
                        {
                            TryConsumePreOpenPlaceholderGate(startNow: false, fromRemote: false);
                            return true;
                        }

                        return true;
                    }
                }

                return true;
            }

            // =========================
            // 1) Modalità foto: solo frecce + ESC
            // =========================
            if (IsPhotoMode)
            {
                if (keyData == Keys.Left) { ShowPrevImage(); return true; }
                if (keyData == Keys.Right) { ShowNextImage(); return true; }
                if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                {
                    CloseCurrentToLibrary();
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            // =========================
            // 2) UI navigabile (Splash / Settings / Credits / Libreria)
            // =========================
            bool splashUi = (_splash?.Visible == true) && (_libraryPage?.Visible != true) && (_settingsModal?.Visible != true) && (_creditsModal?.Visible != true);
            bool modalUi = (_settingsModal?.Visible == true) || (_creditsModal?.Visible == true) || (_libraryPage?.Visible == true);

            if (splashUi || modalUi)
            {
                // Allow normal text-edit keys when a TextBox is focused (es. search header in Libreria)
                try
                {
                    Control? ac = this.ActiveControl;
                    while (ac is ContainerControl cc && cc.ActiveControl != null)
                        ac = cc.ActiveControl;

                    if (ac is TextBoxBase tb)
                    {
                        // Libreria: se stiamo editando il campo di ricerca,
                        // ESC (e BrowserBack) deve solo uscire dall'editing (e NON chiudere la libreria).
                        try
                        {
                            if (_libraryPage?.Visible == true && ResolveDpadRoot() is MediaLibraryPage libX && libX.IsSearchEditor(tb))
                            {
                                // NB: Backspace/Delete devono restare "proprietari" del TextBox (cancella caratteri).
                                if (keyData == Keys.Escape || keyData == Keys.BrowserBack)
                                {
                                    if (libX.TryRemoteExitSearchEdit(tb, out var next))
                                    {
                                        _dpadRoot = ResolveDpadRoot();
                                        if (next != null && !next.IsDisposed)
                                        {
                                            _focused = next;
                                            try { next.Focus(); } catch { }
                                            EnsureDpadVisible(next);
                                            _focusRing.Attach(next);
                                        }
                                        return true;
                                    }
                                }

                                // Durante l'editing: lascia gestire al TextBox (niente DPAD)
                                return base.ProcessCmdKey(ref msg, keyData);
                            }
                        }
                        catch { }

                        // Altri TextBox: lascia almeno i tasti di testo base
                        var kc = keyData & Keys.KeyCode;
                        if (kc == Keys.Space || kc == Keys.Back || kc == Keys.Delete)
                            return base.ProcessCmdKey(ref msg, keyData);
                    }
                }
                catch { }

                // DPAD
                if (keyData == Keys.Left) { _lastDpadFromRemote = false; HandleDpadMove("left"); return true; }
                if (keyData == Keys.Right) { _lastDpadFromRemote = false; HandleDpadMove("right"); return true; }
                if (keyData == Keys.Up) { _lastDpadFromRemote = false; HandleDpadMove("up"); return true; }
                if (keyData == Keys.Down) { _lastDpadFromRemote = false; HandleDpadMove("down"); return true; }

                // OK
                if (keyData == Keys.Enter || keyData == Keys.Space)
                {
                    _lastDpadFromRemote = false;
                    HandleDpadOk();
                    return true;
                }

                // BACK
                if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                {
                    _lastDpadFromRemote = false;
                    HandleDpadBack();
                    return true;
                }

                return base.ProcessCmdKey(ref msg, keyData);
            }

            // =========================
            // 3) Playback hotkeys del player (solo senza modifier)
            // =========================
            if ((keyData & Keys.Modifiers) == Keys.None)
            {
                // OK in playback: mostra HUD (NO modalità DPAD con selezione)
                if (keyData == Keys.Enter)
                {
                    if (_hud != null)
                    {
                        if (!_hud.Visible) _hud.Visible = true;
                        try { if (_hud.DpadMode) _hud.DpadDeactivate(); } catch { }
                        _hud.ShowOnce(2200);
                        return true;
                    }
                }

                if (keyData == Keys.Space) { TogglePlayPause(); return true; }
                if (keyData == Keys.S) { CloseCurrentToLibrary(); return true; }

                if (keyData == Keys.F) { ToggleFullscreen(); _hud.Pulse(HudOverlay.ButtonId.Fullscreen); return true; }
                if (keyData == Keys.O) { OpenFile(); _hud.Pulse(HudOverlay.ButtonId.Open); return true; }

                if (keyData == Keys.Left) { SeekRelative(-10); _hud.Pulse(HudOverlay.ButtonId.Back10); return true; }
                if (keyData == Keys.Right) { SeekRelative(10); _hud.Pulse(HudOverlay.ButtonId.Fwd10); return true; }

                if (keyData == Keys.Up) { _hud.PerformVolumeDelta(+0.05f, ApplyVolume); _hud.ShowOnce(1200); return true; }
                if (keyData == Keys.Down) { _hud.PerformVolumeDelta(-0.05f, ApplyVolume); _hud.ShowOnce(1200); return true; }

                if (keyData == Keys.PageUp) { SeekChapter(+1); _hud.Pulse(HudOverlay.ButtonId.NextChapter); return true; }
                if (keyData == Keys.PageDown) { SeekChapter(-1); _hud.Pulse(HudOverlay.ButtonId.PrevChapter); return true; }

                // Back in playback: prima chiudi overlay (Info/HDR/HUD dpad), poi (se serve) torna alla libreria
                // (ESC si comporta come BACK).
                if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                {
                    _lastDpadFromRemote = false;
                    HandleDpadBack();
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnAnyMouseActivity(IntPtr hwnd, int msg)
        {
            try
            {
                // Passaggio DPAD -> mouse: non facciamo invalidazioni globali (causano flicker)
                // e non resettiamo lo stato DPAD (causa "scorrimento rotto" / focus che salta).
                // Qui ci limitiamo a:
                //  - nascondere la focus ring (se visibile)
                //  - ricordare il controllo cliccato (così il remote riparte da lì)

                bool inDpadUi = (_libraryPage != null && _libraryPage.Visible)
                             || (_settingsModal != null && _settingsModal.Visible)
                             || (_creditsModal != null && _creditsModal.Visible);

                if (!inDpadUi)
                    return;

                // Hide only the DPAD focus ring (l'adorner invalida da solo l'area precedente).
                try
                {
                    if (_focusRing != null && _focusRing.Visible)
                        _focusRing.Attach(null);
                }
                catch { }

                _lastDpadFromRemote = false;
                try { _libraryPage?.SetDpadInputIsRemote(false); } catch { }
                try
                {
                    if (_libraryPage != null && _libraryPage.Visible)
                    {
                        _libraryRemoteActivationArmed = false;
                        _libraryHighlightNeedsReactivation = true;
                    }
                }
                catch { }

                // Se clicca, aggiorna il controllo "focused" (utile per riprendere con remote).
                const int WM_LBUTTONDOWN = 0x0201;
                const int WM_RBUTTONDOWN = 0x0204;
                const int WM_MBUTTONDOWN = 0x0207;
                const int WM_XBUTTONDOWN = 0x020B;
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN)
                {
                    try
                    {
                        var c = Control.FromHandle(hwnd);
                        var f = GetDpadFocusableAncestor(c);
                        if (f != null) _focused = f;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static Control? GetDpadFocusableAncestor(Control? c)
        {
            for (var p = c; p != null; p = p.Parent)
            {
                if (IsDpadFocusable(p)) return p;
            }
            return null;
        }

        private void AttachMouseAnchorTracking(Control? root)
        {
            if (root == null) return;

            try
            {
                root.MouseEnter -= MouseAnchorTracking_MouseEnter;
                root.MouseEnter += MouseAnchorTracking_MouseEnter;
                root.MouseDown -= MouseAnchorTracking_MouseDown;
                root.MouseDown += MouseAnchorTracking_MouseDown;
                root.ControlAdded -= MouseAnchorTracking_ControlAdded;
                root.ControlAdded += MouseAnchorTracking_ControlAdded;
            }
            catch { }

            try
            {
                foreach (Control child in root.Controls)
                    AttachMouseAnchorTracking(child);
            }
            catch { }
        }

        private void MouseAnchorTracking_ControlAdded(object? sender, ControlEventArgs e)
        {
            try { AttachMouseAnchorTracking(e.Control); } catch { }
        }

        private void MouseAnchorTracking_MouseEnter(object? sender, EventArgs e)
        {
            UpdateMouseFocusAnchor(sender as Control);
        }

        private void MouseAnchorTracking_MouseDown(object? sender, MouseEventArgs e)
        {
            UpdateMouseFocusAnchor(sender as Control);
        }

        private void UpdateMouseFocusAnchor(Control? control)
        {
            try
            {
                bool inDpadUi = (_libraryPage != null && _libraryPage.Visible)
                             || (_settingsModal != null && _settingsModal.Visible)
                             || (_creditsModal != null && _creditsModal.Visible);
                if (!inDpadUi)
                    return;

                var focusTarget = GetDpadFocusableAncestor(control);
                if (focusTarget == null || focusTarget.IsDisposed)
                    return;

                try
                {
                    if (_focusRing != null && _focusRing.Visible)
                        _focusRing.Attach(null);
                }
                catch { }

                _focused = focusTarget;
                _lastDpadFromRemote = false;
                try { _libraryPage?.SetDpadInputIsRemote(false); } catch { }
                try
                {
                    if (_libraryPage != null && _libraryPage.Visible)
                    {
                        _libraryRemoteActivationArmed = false;
                        _libraryPage.SyncRemoteZoneFromExternalFocus(focusTarget);
                    }
                }
                catch { }
            }
            catch { }
        }

        private sealed class InputModeMessageFilter : IMessageFilter
        {
            private readonly PlayerForm _owner;
            public InputModeMessageFilter(PlayerForm owner) { _owner = owner; }

            public bool PreFilterMessage(ref Message m)
            {
                // NOTE: non intercettiamo WM_MOUSEMOVE: su alcune macchine arrivano micro-movimenti
                // continui (touchpad/jitter) che facevano sparire il focus ring DPAD mentre si naviga.
                // Ci interessano solo azioni "intenzionali" del mouse: click e wheel.
                const int WM_LBUTTONDOWN = 0x0201;
                const int WM_RBUTTONDOWN = 0x0204;
                const int WM_MBUTTONDOWN = 0x0207;
                const int WM_XBUTTONDOWN = 0x020B;
                const int WM_MOUSEWHEEL = 0x020A;
                const int WM_MOUSEHWHEEL = 0x020E;

                if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_MBUTTONDOWN
                    || m.Msg == WM_XBUTTONDOWN || m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL)
                {
                    try { _owner.OnAnyMouseActivity(m.HWnd, m.Msg); } catch { }
                }

                return false; // never swallow messages
            }
        }


        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_CHAR = 0x0102;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_SYSCHAR = 0x0106;

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private IntPtr FindBestRendererHwnd()
        {
            if (_videoHost == null || _videoHost.IsDisposed || !_videoHost.IsHandleCreated) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;

            try
            {
                EnumChildWindows(_videoHost.Handle, (hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;

                    if (GetWindowRect(hwnd, out var r))
                    {
                        long w = Math.Max(0, r.Right - r.Left);
                        long h = Math.Max(0, r.Bottom - r.Top);
                        long area = w * h;

                        // prende la finestra figlia più “grande” (di solito è il renderer)
                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = hwnd;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return best;
        }

        private bool IsPlayerReservedKey(Keys keyData)
        {
            // Le hotkey del player (quelle che NON devono andare a madVR)
            if ((keyData & Keys.Modifiers) != Keys.None) return false;

            return keyData == Keys.Space ||
                   keyData == Keys.Enter ||
                   keyData == Keys.S ||
                   keyData == Keys.F ||
                   keyData == Keys.O ||
                   keyData == Keys.Left ||
                   keyData == Keys.Right ||
                   keyData == Keys.Up ||
                   keyData == Keys.Down ||
                   keyData == Keys.PageUp ||
                   keyData == Keys.PageDown ||
                   keyData == Keys.BrowserBack ||
                   keyData == Keys.Back ||
                   keyData == Keys.VolumeUp ||
                   keyData == Keys.VolumeDown ||
                   keyData == Keys.VolumeMute ||
                   keyData == Keys.MediaPlayPause ||
                   keyData == Keys.MediaStop ||
                   keyData == Keys.MediaNextTrack ||
                   keyData == Keys.MediaPreviousTrack;
        }

        private bool TryForwardKeyToRenderer(ref Message m)
        {
            // inoltra solo se c’è un renderer “windowed” e siamo in playback video
            if (_engine == null) return false;
            if (!(_engine.HasDisplayControl())) return false;

            bool inUi = (_settingsModal?.Visible == true) || (_creditsModal?.Visible == true) || (_libraryPage?.Visible == true);
            if (_hud?.DpadMode == true) return false;
            if (inUi || IsPhotoMode) return false;

            Keys keyData = (Keys)m.WParam.ToInt32() | ModifierKeys;

            // NON inoltrare se è una hotkey del player
            if (IsPlayerReservedKey(keyData)) return false;

            // target
            var hwnd = FindBestRendererHwnd();
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                SendMessage(hwnd, m.Msg, m.WParam, m.LParam);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Blocca tasto destro + ascolta hot-plug audio per ri-verifica PCM/Bitstream
        protected override void WndProc(ref Message m)
        {
            // --- MEGA FIX: forward tasti non-gestiti a madVR/MPCVR ---
            if (m.Msg == WM_KEYDOWN || m.Msg == WM_KEYUP || m.Msg == WM_CHAR ||
                m.Msg == WM_SYSKEYDOWN || m.Msg == WM_SYSKEYUP || m.Msg == WM_SYSCHAR)
            {
                if (TryForwardKeyToRenderer(ref m))
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
            }

            // DirectShow graph notify (EC_COMPLETE) → ritorno libreria affidabile
            if (m.Msg == WM_GRAPHNOTIFY)
            {
                try { DrainGraphEvents(); } catch { }
                m.Result = IntPtr.Zero;
                return;
            }

            const int WM_CONTEXTMENU = 0x007B;
            const int WM_DEVICECHANGE = 0x0219;
            const int DBT_DEVNODES_CHANGED = 0x0007;
            const int DBT_DEVICEARRIVAL = 0x8000;
            const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

            // Passaggio DPAD -> mouse:
            // Non sganciamo/azzera... su ogni mousemove o wheel (causava flicker e "sparizioni"),
            // ma SOLO quando l'utente clicca davvero con il mouse dentro UI DPAD.
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_MBUTTONDOWN = 0x0207;
            const int WM_XBUTTONDOWN = 0x020B;

            bool inDpadUi = (_libraryPage != null && _libraryPage.Visible)
                         || (_settingsModal != null && _settingsModal.Visible)
                         || (_creditsModal != null && _creditsModal.Visible);

            if (inDpadUi && (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_MBUTTONDOWN || m.Msg == WM_XBUTTONDOWN))
            {
                // L'utente sta usando il mouse: disattacca solo la ring (niente reset aggressivo).
                try { _focusRing.Attach(null); } catch { }

                // Aggiorna "focused" in base al controllo cliccato: quando riprendi il remote
                // riparti da li' e non da un riquadro vecchio.
                try { UpdateMouseFocusAnchor(Control.FromHandle(m.HWnd)); } catch { }
            }

            if (m.Msg == WM_CONTEXTMENU)
            {
                if (_loading.Visible) { m.Result = IntPtr.Zero; return; }
                MarkContextMenuPending();
            }

            if (m.Msg == WM_DEVICECHANGE)
            {
                int ev = m.WParam.ToInt32();
                if (ev == DBT_DEVNODES_CHANGED || ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { RecheckAudioNow(); } catch { }
                    }));
                }
            }

            base.WndProc(ref m);
        }

        private void RecheckAudioNow()
        {
            if (_engine == null) return;

            // Media container avg per fallback
            int avgContainerKbpsLocal = 0;
            try
            {
                if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath) && _duration > 1)
                {
                    var fi = new FileInfo(_currentPath);
                    avgContainerKbpsLocal = (int)Math.Round((fi.Length * 8.0 / 1000.0) / _duration);
                }
            }
            catch { }

            var sel = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
            var lav = GetLavAudioIODetails(sel?.Name);
            _bitstreamNow = IsBitstream();

            int kbps = 0;
            if (lav.AudioNowKbps > 0) kbps = lav.AudioNowKbps;
            if (kbps <= 0 && sel != null) kbps = ParseKbpsFromName(sel.Name);
            if (kbps <= 0 && avgContainerKbpsLocal > 0) kbps = (int)(avgContainerKbpsLocal * 0.30);
            _audioBitrateNowKbps = kbps;

            // refresh immediato dell’overlay info se presente
            if (_info != null)
            {
                var chosen = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                UpdateInfoOverlay(chosen, _info.IsHdr);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (_iconBig != null) SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_BIG, _iconBig.Handle);
                if (_iconSmall != null)
                {
                    SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL, _iconSmall.Handle);
                    SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL2, _iconSmall.Handle);
                }
            }
            catch { }
        }
        private bool IsValidLibraryFocusTarget(Control? target)
        {
            try
            {
                if (target == null || target.IsDisposed || _libraryPage == null)
                    return false;
                if (!target.Visible || target.Width <= 0 || target.Height <= 0)
                    return false;
                if (!IsDescendant(_libraryPage, target))
                    return false;
                if (!IsDpadFocusable(target))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ShouldAutoAttachRequestedLibraryFocus(Control? target)
        {
            try
            {
                if (_libraryPage == null || !_libraryPage.Visible)
                    return false;
                if (!_libraryPage.IsRemoteNavigationReady)
                    return false;
                if (!IsValidLibraryFocusTarget(target))
                    return false;
                return _libraryPage.IsRemoteContentFocusCandidate(target);
            }
            catch
            {
                return false;
            }
        }

        private string? ResolveEffectiveLibraryCategoryForPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return _currentLibraryCategory;

            try
            {
                if (_libraryPage != null)
                {
                    string resolved = _libraryPage.ResolveEffectiveCategoryForPath(path);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        return resolved;
                }
            }
            catch { }

            return _currentLibraryCategory;
        }

        private bool ShouldUseCinemaFeaturesForPath(string? path)
        {
            return string.Equals(ResolveEffectiveLibraryCategoryForPath(path), "Film", StringComparison.OrdinalIgnoreCase);
        }

        private void ScheduleLibraryPrewarm()
        {
            if (_libraryPrewarmScheduled)
                return;

            _libraryPrewarmScheduled = true;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed)
                            return;

                        EnsureLibraryPageCreated();
                        if (_libraryPage == null)
                            return;

                        _libraryPage.Visible = false;
                        _libraryPage.SendToBack();
                        _libraryPage.CreateControl();
                        _libraryPage.PerformLayout();
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void EnsureLibraryPageCreated()
        {
            if (_libraryPage != null)
            {
                try
                {
                    if (!_stack.Controls.Contains(_libraryPage))
                        _stack.Controls.Add(_libraryPage);
                }
                catch { }
                return;
            }

            var page = new MediaLibraryPage
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            AttachMouseAnchorTracking(page);

            page.OpenRequested += path =>
            {
                _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(path) ?? _libraryPage?.SelectedCategory;
                ResetLibraryRemoteActivation(clearFocusRing: true);
                HideLibrary();
                OpenPath(path);
            };

            page.OpenWithResumeRequested += (path, resumeSeconds) =>
            {
                _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(path) ?? _libraryPage?.SelectedCategory;
                ResetLibraryRemoteActivation(clearFocusRing: true);
                HideLibrary();
                OpenPath(path, resume: resumeSeconds ?? 0);
            };

            page.QueuePlayRequested += (paths, startIndex, shuffle) =>
            {
                StartPlaybackQueue(paths, startIndex, shuffle);
            };

            page.QueueAppendRequested += paths =>
            {
                AppendToPlaybackQueue(paths);
            };

            page.QueueRemoveRequested += paths =>
            {
                RemoveFromPlaybackQueue(paths);
            };

            page.QueueClearRequested += () =>
            {
                ClearPlaybackQueue();
            };

            page.QueuePlayPathRequested += path =>
            {
                PlayQueuedPath(path);
            };

            page.QueueMoveRequested += (path, delta) =>
            {
                MovePlaybackQueuePath(path, delta);
            };

            page.QueueEditorRequested += () =>
            {
                ShowPlaybackQueueEditor();
            };

            page.QueueContainsPathResolver = path => IsPathQueued(path);
            page.QueueSnapshotResolver = () => GetPlaybackQueueSnapshotItems();

            page.CloseRequested += () => HideLibrary();
            page.RemoteNavigationResetRequested += () =>
            {
                try
                {
                    if (IsDisposed)
                        return;

                    void RefreshLibraryFocusAnchor()
                    {
                        if (_libraryPage == null || !_libraryPage.Visible)
                        {
                            ResetLibraryRemoteActivation(clearFocusRing: false);
                            return;
                        }

                        Control? focusTarget = null;
                        try
                        {
                            focusTarget = _libraryPage.CoerceRemoteFocus(_focused);
                        }
                        catch
                        {
                            focusTarget = _focused;
                        }

                        focusTarget = GetDpadFocusableAncestor(focusTarget) ?? focusTarget;

                        if (!IsValidLibraryFocusTarget(focusTarget))
                        {
                            ResetLibraryRemoteActivation(clearFocusRing: false);
                            return;
                        }

                        _libraryRemoteActivationArmed = false;
                        _focused = focusTarget;

                        try { _libraryPage.SyncRemoteZoneFromExternalFocus(focusTarget); } catch { }
                        try { _focusRing.Attach(focusTarget); } catch { }
                    }

                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(RefreshLibraryFocusAnchor));
                        return;
                    }

                    RefreshLibraryFocusAnchor();
                }
                catch { }
            };

            page.RemoteFocusRequested += c =>
            {
                try
                {
                    if (c == null || c.IsDisposed) return;
                    if (_libraryPage == null || !_libraryPage.Visible) return;

                    Control? focusTarget = GetDpadFocusableAncestor(c) ?? c;
                    if (!IsValidLibraryFocusTarget(focusTarget))
                    {
                        try { focusTarget = _libraryPage.GetRemoteDefaultFocusTarget(); }
                        catch { focusTarget = null; }
                        focusTarget = GetDpadFocusableAncestor(focusTarget) ?? focusTarget;
                    }

                    if (!IsValidLibraryFocusTarget(focusTarget))
                    {
                        _focused = null;
                        try { _focusRing.Attach(null); } catch { }
                        return;
                    }

                    bool autoAttach = ShouldAutoAttachRequestedLibraryFocus(focusTarget);
                    if (autoAttach)
                        _libraryRemoteActivationArmed = true;

                    _dpadRoot = ResolveDpadRoot();
                    _focused = focusTarget;

                    if ((_libraryPage.IsRemoteNavigationReady && _libraryRemoteActivationArmed) || autoAttach)
                    {
                        try { focusTarget!.Focus(); } catch { }
                        try { EnsureDpadVisible(focusTarget!); } catch { }
                        try { _focusRing.Attach(focusTarget!); } catch { }
                    }
                    else
                    {
                        try { _focusRing.Attach(null); } catch { }
                    }
                }
                catch { }
            };

            _libraryPage = page;
            _stack.Controls.Add(_libraryPage);
            _libraryPage.SendToBack();
        }

        private void ShowLibrary()
        {
            _playbackQueueSessionActive = false;
            _nextOpenBelongsToPlaybackQueue = false;
            _libraryHighlightNeedsReactivation = false;

            if (_engine != null)
            {
                try { SafeStop(toSplash: false); } catch { }
            }

            EnsureLibraryPageCreated();
            if (_libraryPage == null)
                return;

            _libraryPage.Visible = true;
            _libraryPage.BringToFront();

            _splash.Visible = false;
            _hud.Visible = false;
            _infoOverlay.Visible = false;

            ResetLibraryRemoteActivation(clearFocusRing: true);
            RemoteAttachRoot(_libraryPage, forceReset: true);
            EnsureActive();

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try { _libraryPage?.EnsureInitialContentPrepared(); } catch { }
                    try
                    {
                        if (_libraryPage != null &&
                            !string.Equals(_libraryPage.SelectedCategory, "Playlist", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(_libraryPage.SelectedCategory, "Preferiti", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(_libraryPage.SelectedCategory, "Foto", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(_libraryPage.SelectedSource, "URL", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(_libraryPage.SelectedSource, "YouTube", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(_libraryPage.SelectedSource, "Rete domestica", StringComparison.OrdinalIgnoreCase))
                        {
                            _libraryPage.ForceCarouselRefresh();
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void HideLibrary()
        {
            if (_libraryPage == null) return;

            _libraryHighlightNeedsReactivation = false;
            try { ResetLibraryRemoteActivation(clearFocusRing: true); } catch { }

            _focused = null;
            _dpadRoot = null;

            try
            {
                _libraryPage.Visible = false;
                _libraryPage.SendToBack();
            }
            catch { }

            _splash.Visible = string.IsNullOrEmpty(_currentPath);
            EnsureActive();
            BringOverlaysToFront();
        }

        // “Apri con …” da Esplora
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // IMPORTANT:
            // Quando l'app viene avviata via "Apri con…" (argomento da riga di comando),
            // alcuni renderer creano/portano in primo piano la loro finestra dopo l'inizializzazione,
            // facendo finire l'OverlayHost sotto al video. Risultato: HUD/timeline spariscono finché non premi un tasto.
            //
            // Quindi: aspetta il primo layout, assicurati che l'overlay sia visibile e sincronizzato,
            // poi apri il media.
            BeginInvoke(new Action(() =>
            {
                try { EnsureActive(); } catch { }
                try { SafeShowOverlayHost(); } catch { }
                try { SyncOverlayToVideoRect(); } catch { }
                try { BringOverlaysToFront(); } catch { }

                TryOpenFromCommandLineSafe();

                // FIX: allo splash (avvio/home) assicuriamoci che la finestra sia attiva e fullscreen
                // (altrimenti Windows può “deselezionare” la finestra e le frecce non fanno nulla).
                try
                {
                    if (_engine == null && (_splash?.Visible == true || string.IsNullOrEmpty(_currentPath)))
                    {
                        if (FormBorderStyle != FormBorderStyle.None)
                            ToggleFullscreen();
                    }
                }
                catch { }

                try { EnsureActive(); } catch { }
            }));
        }


        private void TryOpenFromCommandLineSafe()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1)
                {
                    var p = args.Skip(1).FirstOrDefault(File.Exists);
                    if (!string.IsNullOrEmpty(p))
                    {
                        SkipLoadingIfActive();
                        OpenPath(p!);
                    }
                }
            }
            catch { }
        }

        private void SkipLoadingIfActive()
        {
            try
            {
                if (_loading?.Visible == true)
                {
                    _loading.Visible = false;
                    _loading.Invalidate();
                }

                if (_videoLoading?.Visible == true)
                {
                    _videoLoading.Visible = false;
                    _videoLoading.Invalidate();
                }

                _splash.Visible = false;
                BringOverlaysToFront();
            }
            catch { }
        }

        private void ShowVideoLoading(string message = "Caricamento…")
        {
            try
            {
                // Passaggio demo → film: niente overlay di caricamento (effetto “cinema”).
                if (_suppressVideoLoadingSerial != 0 && _suppressVideoLoadingSerial == _openSerial)
                {
                    _loading.Visible = false;
                    _splash.Visible = false;
                    if (_videoLoading != null) _videoLoading.Visible = false;
                    return;
                }

                _loading.Visible = false;
                _splash.Visible = false;

                if (_videoLoading != null)
                {
                    _videoLoading.SetMessage(message);
                    _videoLoading.Visible = true;
                    _videoLoading.BringToFront();
                }
            }
            catch { }

            try { BringOverlaysToFront(); } catch { }
        }

        private void HideVideoLoading()
        {
            try
            {
                if (_videoLoading != null)
                {
                    _videoLoading.Visible = false;
                    _videoLoading.Invalidate();
                }

                // consumata la soppressione (se era per questo open)
                if (_suppressVideoLoadingSerial != 0 && _suppressVideoLoadingSerial == _openSerial)
                    _suppressVideoLoadingSerial = 0;
            }
            catch { }

            try { BringOverlaysToFront(); } catch { }
        }

        private void UseOverlayInline(bool enable)
        {
            // Manteniamo SEMPRE l'host layered
            enable = false;

            if (_overlayInlineHost == null)
            {
                _overlayInlineHost = new InlineOverlayPanel { Dock = DockStyle.Fill };
                _stack.Controls.Add(_overlayInlineHost);
                _overlayInlineHost.Visible = false;
            }

            Control target = _overlayHost.Surface;

            if (_audioOnlyBanner.Parent != target) _audioOnlyBanner.Parent = target;
            if (_hud.Parent != target) _hud.Parent = target;
            if (_infoOverlay.Parent != target) _infoOverlay.Parent = target;
            if (_settingsModal.Parent != target) _settingsModal.Parent = target;
            if (_creditsModal.Parent != target) _creditsModal.Parent = target;

            if (!_overlayHost.Visible) SafeShowOverlayHost();
            SyncOverlayToVideoRect();
            try { _overlayHost.BringToFront(); } catch { }

            BringOverlaysToFront();
            UpdateVideoWindowForCurrentHost();
        }

        private bool _pendingShowOverlayOnHandleCreated;
        private void SafeShowOverlayHost()
        {
            if (_overlayHost == null || _overlayHost.IsDisposed) return;
            if (_overlayHost.Visible) return;

            if (!IsHandleCreated)
            {
                if (_pendingShowOverlayOnHandleCreated) return;
                _pendingShowOverlayOnHandleCreated = true;

                void Handler(object? s, EventArgs e)
                {
                    try { this.HandleCreated -= Handler; }
                    catch { }
                    finally
                    {
                        _pendingShowOverlayOnHandleCreated = false;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (_overlayHost == null || _overlayHost.IsDisposed || _overlayHost.Visible) return;
                                    _overlayHost.Show();
                                    try { SyncOverlayToVideoRect(); } catch { }
                                    try { _overlayHost.BringToFront(); } catch { }
                                }
                                catch
                                {
                                    try { if (_overlayHost != null && !_overlayHost.IsDisposed) { _overlayHost.Hide(); _overlayHost.Show(); } } catch { }
                                }
                            }));
                        }
                        catch
                        {
                            try { if (_overlayHost != null && !_overlayHost.IsDisposed && !_overlayHost.Visible) _overlayHost.Show(); } catch { }
                        }
                    }
                }

                this.HandleCreated += Handler;
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_overlayHost == null || _overlayHost.IsDisposed || _overlayHost.Visible) return;
                        _overlayHost.Show();
                        try { SyncOverlayToVideoRect(); } catch { }
                        try { _overlayHost.BringToFront(); } catch { }
                    }
                    catch (InvalidOperationException)
                    {
                        try { _overlayHost.Hide(); _overlayHost.Show(); } catch { }
                    }
                    catch { /* best-effort */ }
                }));
            }
            catch (InvalidOperationException)
            {
                SafeShowOverlayHost();
            }

            if (!_statsTimerInitialized)
            {
                _statsTimerInitialized = true;

                _statsTimer.Tick += (_, __) =>
                {
                    try
                    {
                        if (_engine == null)
                            return;

                        UpdateTime(_engine.PositionSeconds);
                        PublishRemoteState();

                        // Snapshot per telecomando web (/api/state)
                        // NB: Duration usa _duration (fallback robusto) e non _engine.DurationSeconds (spesso 0 su alcuni stream).
                        string title;
                        try { title = _hud?.GetTitle?.Invoke() ?? _hud?.NowPlayingTitle ?? string.Empty; }
                        catch { title = _hud?.NowPlayingTitle ?? string.Empty; }

                        bool bit = false;
                        try { bit = IsBitstream(); } catch { }
                        if (_audioOutPref == AudioOutPref.ForcePcm) bit = false;

                        _remoteSnapshot = new RemoteState
                        {
                            Title = title,
                            Position = _engine?.PositionSeconds ?? 0,
                            Duration = Math.Max(0, _duration),
                            Bitstream = bit,
                            Is3D = _stereo != Stereo3DMode.None,
                            OutputHdr = (_info?.IsHdr == true) && (_hdr != HDRMode.Off)
                        };
                    }
                    catch { /* best-effort */ }
                };
            }

            if (!_statsTimer.Enabled)
                _statsTimer.Start();
        }

        private void BringOverlaysToFront()
        {
            _videoHost.SendToBack();

            // In playback (o sullo splash) non vogliamo mai una focus-ring "appesa".
            // Se non siamo in una UI DPAD (libreria/settings/credits), nascondila.
            try
            {
                bool dpadUiVisible = (_libraryPage?.Visible == true)
                                  || (_settingsModal?.Visible == true)
                                  || (_creditsModal?.Visible == true);
                if (!dpadUiVisible)
                {
                    if (_focusRing != null && _focusRing.Visible)
                        _focusRing.Attach(null);
                }
            }
            catch { }

            // Ordine overlay sullo stack (sotto all'OverlayHost trasparente):
            // 1) Splash
            // 2) Loading iniziale
            // 3) Loading media (nero + spinner)
            if (_splash.Visible) _splash.BringToFront();
            if (_loading?.Visible == true) _loading.BringToFront();
            if (_videoLoading?.Visible == true) _videoLoading.BringToFront();
            if (_overlayHost != null)
            {
                if (!_overlayHost.Visible) SafeShowOverlayHost();
                _overlayHost.BringToFront();
            }

            bool modalVisible = (_settingsModal?.Visible ?? false) || (_creditsModal?.Visible ?? false);

            _infoOverlay.BringToFront();

            // Placeholder in pausa: deve stare sopra il video ma sotto HUD/OSD
            if (_pausePlaceholder != null && _pausePlaceholder.Visible)
            {
                try
                {
                    _pausePlaceholder.BringToFront();
                    _infoOverlay.BringToFront();
                }
                catch { }
            }


            if (modalVisible)
            {
                _hud.Visible = false;
                if (_settingsModal?.Visible == true) _settingsModal.BringToFront();
                if (_creditsModal?.Visible == true) _creditsModal.BringToFront();
            }
            else
            {
                if (_settingsModal != null) _settingsModal.SendToBack();
                if (_creditsModal != null) _creditsModal.SendToBack();

                // HUD: NON forzare la visibilità solo perché esiste un engine.
                // Deve comparire solo su interazione (mouse / scrub timeline da remoto / ecc.).
                bool hudAllowed = _engine != null && !_splash.Visible && !IsPhotoMode;
                if (!hudAllowed)
                {
                    _hud.Visible = false;
                    _hud.TimelineVisible = false;
                }
                else
                {
                    // Mantieni lo stato corrente (se già attiva/visibile)
                    _hud.TimelineVisible = _hud.Visible;
                    if (_hud.Visible) _hud.BringToFront();
                }

                // OSD remoto: deve stare sopra tutto durante la riproduzione
                if (_remoteOsd != null && _remoteOsd.Visible)
                    _remoteOsd.BringToFront();
            }

            // In solo-audio: se i meters o il banner sono visibili, tienili sotto all'HUD
            if (_audioMetersHost?.Visible == true)
            {
                _audioMetersHost.BringToFront();
                if (_hud.Visible) _hud.BringToFront();
            }
            else if (_audioOnlyBanner.Visible)
            {
                // Banner audio-only sotto l'HUD
                _audioOnlyBanner.SendToBack();
                if (_hud.Visible) _hud.BringToFront();
            }

            // --- Modalità foto: HUD classica OFF, solo PhotoHUD ---
            if (IsPhotoMode)
            {
                _hud.Visible = false;
                _hud.TimelineVisible = false;
                if (_photoHud != null)
                {
                    _photoHud.Visible = true;
                    _photoHud.BringToFront();
                }
            }
            else
            {
                if (_photoHud != null) _photoHud.Visible = false;
            }

            if (_pairBanner != null && !_pairBanner.IsDisposed)
            {
                try { _pairBanner.BringToFront(); } catch { }
            }

            // In caso di modal/HDR/photo, assicuriamoci comunque che l'OSD remoto non venga nascosto.
            if (_remoteOsd != null && _remoteOsd.Visible)
            {
                try { _remoteOsd.BringToFront(); } catch { }
            }
        }


        private void ForceOverlayRepaint()
        {
            try
            {
                if (_overlayHost != null && !_overlayHost.IsDisposed)
                {
                    _overlayHost.Surface.Invalidate(true);
                    _overlayHost.Surface.Update();
                }
            }
            catch { }

            try
            {
                if (_hud != null && !_hud.IsDisposed)
                {
                    _hud.Invalidate(true);
                    _hud.Update();
                }
            }
            catch { }
        }

        private void HudBump(int ms, bool allowWhenRemote, bool showTimeline)
        {
            try
            {
                if (_contextMenuActive || _contextMenuPending)
                    return;

                var now = DateTime.UtcNow;
                _lastHudActivityUtc = now;

                if (!allowWhenRemote && IsRemoteCommandActive)
                    return;

                if (_engine == null) return;
                if (_splash.Visible || _loading.Visible) return;
                if (IsPhotoMode) return;

                bool wasHidden = !_hud.Visible;
                _hud.Visible = true;
                try { _hud.TimelineVisible = showTimeline && _duration > 0; } catch { }

                if (wasHidden)
                {
                    try { SafeShowOverlayHost(); } catch { }
                    try { SyncOverlayToVideoRect(); } catch { }
                    try { BringOverlaysToFront(); } catch { }
                }
            }
            catch { }
        }

        private void TickUiIdle()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (WindowState == FormWindowState.Minimized) return;
                if (_contextMenuActive || _contextMenuPending)
                {
                    EnsureCursorVisible();
                    return;
                }

                // Applica l'auto-hide solo nel player (non in libreria / modals / photo mode)
                bool uiBlocked = (_libraryPage?.Visible == true)
                              || (_settingsModal?.Visible == true)
                              || (_creditsModal?.Visible == true)
                              || IsPhotoMode
                              || _splash.Visible;

                if (uiBlocked)
                {
                    EnsureCursorVisible();
                    return;
                }

                // Se non c'è playback né gate attivo, non nascondere nulla.
                if (_engine == null && !_preOpenPlaceholderGateActive)
                {
                    EnsureCursorVisible();
                    return;
                }

                var now = DateTime.UtcNow;
                var mouseIdleMs = (now - _lastMouseMoveUtc).TotalMilliseconds;
                var hudIdleMs = (now - _lastHudActivityUtc).TotalMilliseconds;

                // Cursore: lo nascondiamo solo se il mouse è sopra la finestra.
                try
                {
                    var p = Control.MousePosition;
                    Rectangle screen;
                    try { screen = RectangleToScreen(ClientRectangle); }
                    catch { screen = Rectangle.Empty; }
                    bool mouseOverWindow = !screen.IsEmpty && screen.Contains(p);

                    if (mouseOverWindow)
                    {
                        if (mouseIdleMs >= HUD_IDLE_HIDE_MS) HideCursorNow();
                        else EnsureCursorVisible();
                    }
                    else
                    {
                        // Fuori dalla finestra: non forziamo il cursore invisibile.
                        EnsureCursorVisible();
                    }
                }
                catch { }

                if (hudIdleMs >= HUD_IDLE_HIDE_MS)
                {
                    if (_hud.Visible)
                    {
                        _hud.Visible = false;
                        try { _hud.TimelineVisible = false; } catch { }
                        try
                        {
                            _hudWakeAnchorPos = Control.MousePosition;
                            _hudWakeLastMousePos = _hudWakeAnchorPos;
                            _hudWakeNeedsIntentionalMove = true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static readonly Cursor _invisibleCursor = CreateInvisibleCursor();

        private static Cursor CreateInvisibleCursor()
        {
            // Crea un cursore trasparente 16x16
            Bitmap bmp = new Bitmap(16, 16);
            IntPtr ptr = bmp.GetHicon();
            Cursor cur = new Cursor(ptr);
            return cur;
        }

        private void SetSystemCursorVisible(bool visible)
        {
            try
            {
                int guard = 0;
                if (visible)
                {
                    while (ShowCursor(true) < 0 && guard++ < 8) { }
                }
                else
                {
                    while (ShowCursor(false) >= 0 && guard++ < 8) { }
                }
            }
            catch { }
        }

        private void SuppressHudForProgrammaticTransition(int suppressMs = 900)
        {
            try
            {
                var until = DateTime.UtcNow.AddMilliseconds(Math.Max(0, suppressMs));
                if (until > _suppressHudWakeUntilUtc)
                    _suppressHudWakeUntilUtc = until;

                _lastHudActivityUtc = DateTime.UtcNow;

                if (_hud != null)
                {
                    try { _hud.Visible = false; } catch { }
                    try { _hud.TimelineVisible = false; } catch { }
                    try { _hud.ClearRemoteScrub(); } catch { }
                    try { _hud.SetPreview(null, _engine?.PositionSeconds ?? 0); } catch { }
                }

                try
                {
                    _hudWakeAnchorPos = Control.MousePosition;
                    _hudWakeLastMousePos = _hudWakeAnchorPos;
                    _hudWakeNeedsIntentionalMove = true;
                }
                catch { }
            }
            catch { }
        }

        private void HideCursorNow()
        {
            if (_cursorHidden) return;
            _cursorHidden = true;
            try { Cursor = _invisibleCursor; } catch { }
            try { _videoHost.Cursor = _invisibleCursor; } catch { }
            try { _audioMetersHost.Cursor = _invisibleCursor; } catch { }
            try { if (_overlayHost != null) { _overlayHost.Cursor = _invisibleCursor; _overlayHost.Surface.Cursor = _invisibleCursor; } } catch { }
            try { SetSystemCursorVisible(false); } catch { }
        }

        private void EnsureCursorVisible()
        {
            if (!_cursorHidden) return;
            _cursorHidden = false;
            try { Cursor = Cursors.Default; } catch { }
            try { _videoHost.Cursor = Cursors.Default; } catch { }
            try { _audioMetersHost.Cursor = Cursors.Default; } catch { }
            try { if (_overlayHost != null) { _overlayHost.Cursor = Cursors.Default; _overlayHost.Surface.Cursor = Cursors.Default; } } catch { }
            try { SetSystemCursorVisible(true); } catch { }
        }

        private void MarkContextMenuPending()
        {
            _contextMenuPending = true;
            _lastHudActivityUtc = DateTime.UtcNow;
            _lastMouseMoveUtc = DateTime.UtcNow;
            SuppressHudForProgrammaticTransition(800);
            try { EnsureCursorVisible(); } catch { }
        }

        private void BeginContextMenuHudBlock()
        {
            _contextMenuPending = false;
            _contextMenuActive = true;
            _lastHudActivityUtc = DateTime.UtcNow;
            _lastMouseMoveUtc = DateTime.UtcNow;
            SuppressHudForProgrammaticTransition(1200);
            try { EnsureCursorVisible(); } catch { }
        }

        private void EndContextMenuHudBlock()
        {
            _contextMenuPending = false;
            _contextMenuActive = false;
            _lastHudActivityUtc = DateTime.UtcNow;
            _lastMouseMoveUtc = DateTime.UtcNow;
            _suppressHudWakeUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            try
            {
                _hudWakeAnchorPos = Control.MousePosition;
                _hudWakeLastMousePos = _hudWakeAnchorPos;
                _hudWakeNeedsIntentionalMove = true;
            }
            catch { }
        }

        // Overlay nero + spinner per il caricamento del media (al posto dello splash)
        private sealed class VideoLoadingMask : Control
        {
            private readonly System.Windows.Forms.Timer _t = new() { Interval = 85 };
            private int _angle;
            private string _message = "Caricamento…";

            public VideoLoadingMask()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw, true);

                BackColor = Color.Black;

                _t.Tick += (_, __) =>
                {
                    _angle = (_angle + 30) % 360;
                    Invalidate();
                };

                // Parte subito: quando la UI non è bloccata (async OpenPath) gira correttamente.
                _t.Start();
            }

            public void SetMessage(string message)
            {
                _message = string.IsNullOrWhiteSpace(message) ? "Caricamento…" : message;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int w = Width;
                int h = Height;
                if (w <= 0 || h <= 0) return;

                // sfondo nero pieno (coerente con player)
                g.Clear(Color.Black);

                int cx = w / 2;
                int cy = h / 2 - 12;
                int size = 44;
                var rect = new Rectangle(cx - size / 2, cy - size / 2, size, size);

                // spinner ad arco in colore "accent" del tema
                using (var p = new Pen(Theme.Accent, 4)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                {
                    g.DrawArc(p, rect, _angle, 290);
                }

                using var f = new Font("Segoe UI", 12f, FontStyle.Regular);
                var sz = TextRenderer.MeasureText(_message, f);

                var textRect = new Rectangle(
                    cx - sz.Width / 2,
                    cy + size / 2 + 12,
                    sz.Width,
                    sz.Height);

                TextRenderer.DrawText(
                    g,
                    _message,
                    f,
                    textRect,
                    Theme.SubtleText,
                    TextFormatFlags.HorizontalCenter
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }

        // Esegue chiamate su un thread STA con message-loop.
        // Serve per componenti browser/COM che possono richiedere STA (es. resolver YouTube).
        private sealed class StaInvoker : IDisposable
        {
            private readonly Thread _thread;
            private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Control? _invoker;

            public StaInvoker()
            {
                _thread = new Thread(ThreadMain)
                {
                    IsBackground = true,
                    Name = "Cinecore.STA.Invoker"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            private void ThreadMain()
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    _invoker = new Control();
                    _invoker.CreateControl();
                    _ready.TrySetResult(true);
                    Application.Run();
                }
                catch (Exception ex)
                {
                    _ready.TrySetException(ex);
                }
            }

            public async Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct)
            {
                await _ready.Task.ConfigureAwait(false);

                var inv = _invoker;
                if (inv == null || inv.IsDisposed)
                    throw new ObjectDisposedException(nameof(StaInvoker));

                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

                void Run()
                {
                    try { tcs.TrySetResult(func()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }

                try { inv.BeginInvoke((Action)Run); }
                catch (Exception ex) { tcs.TrySetException(ex); }

                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }

            public void Dispose()
            {
                try
                {
                    var inv = _invoker;
                    if (inv != null && !inv.IsDisposed)
                    {
                        inv.BeginInvoke((Action)(() => Application.ExitThread()));
                        inv.Dispose();
                    }
                }
                catch { }
            }
        }

        // === Focus adorner (bordo visibile attorno al controllo a fuoco)
        private sealed class FocusAdorner : Control
        {
            // Un border overlay disegnato senza “coprire” il controllo sottostante.
            // Nota: usiamo una Region a “ciambella” per evitare il bug dove gli item
            // (carousel / griglia / menu sinistro) diventano neri quando evidenziati.
            private const int RingThickness = 3; // px

            private Control? _target;
            private Control? _host;
            private bool _repositionPending;
            private Region? _donutRegion;
            private readonly List<Control> _ancestors = new();

            public FocusAdorner()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;
                Margin = Padding.Empty;
                TabStop = false;
                Enabled = false;
                Visible = false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _donutRegion?.Dispose(); } catch { }
                    _donutRegion = null;
                }
                base.Dispose(disposing);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // Intenzionalmente vuoto: la Region “a ciambella” fa sì che
                // l’adorner NON copra l’interno del target.
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x84;
                const int HTTRANSPARENT = -1;
                if (m.Msg == WM_NCHITTEST)
                {
                    m.Result = (IntPtr)HTTRANSPARENT;
                    return;
                }
                base.WndProc(ref m);
            }

            private static bool IsBadHost(Control c)
                => c is FlowLayoutPanel
                   || c is TableLayoutPanel
                   || c is MediaLibraryPage;

            private static Control? FindHost(Control target)
            {
                // Preferisci SEMPRE la Form che contiene il target: così l’adorner
                // non viene disposto insieme a pannelli transitori (es. Library chiusa).
                try
                {
                    var f = target.FindForm();
                    if (f != null && !f.IsDisposed)
                        return f;
                }
                catch { }

                // Fallback: risali al primo ancestor “neutro”.
                Control? p = target.Parent;
                while (p != null && IsBadHost(p))
                    p = p.Parent;

                return p ?? target.Parent;
            }

            public void Attach(Control? target)
            {
                if (IsDisposed) return;
                if (ReferenceEquals(_target, target)) return;

                Unwire();

                _target = target;
                _host = null;

                if (_target == null || _target.IsDisposed)
                {
                    HideAndClear();
                    return;
                }

                // NON mostrare il bordo sui bottoncini piccoli (es. frecce del carosello)
                if (_target is ButtonBase btn && btn.Width <= 40 && btn.Height <= 40)
                {
                    HideAndClear();
                    return;
                }

                var host = FindHost(_target);
                if (host == null || host.IsDisposed)
                {
                    HideAndClear();
                    return;
                }

                _host = host;

                if (Parent != _host)
                    _host.Controls.Add(this);

                Wire();

                Visible = true;
                Reposition();
            }

            private void HideAndClear()
            {
                var oldHost = _host;
                var oldBounds = Bounds;
                var oldTarget = _target;

                Visible = false;
                _target = null;
                _host = null;
                ClearRegion();

                // Evita artefatti: invalida in modo piu' mirato per non innescare repaint profondi
                // dell'intera UI, che visivamente fanno "spaghettificare" i pannelli durante i cambi focus.
                try
                {
                    if (oldTarget != null && !oldTarget.IsDisposed)
                        oldTarget.Invalidate();
                }
                catch { }

                try
                {
                    if (oldHost != null && !oldHost.IsDisposed && !oldBounds.IsEmpty)
                        oldHost.Invalidate(oldBounds, false);
                }
                catch { }
            }

            private void ClearRegion()
            {
                try { _donutRegion?.Dispose(); } catch { }
                _donutRegion = null;
                try { Region = null; } catch { }
            }

            private void Wire()
            {
                if (_target != null)
                {
                    _target.LocationChanged += TargetChanged;
                    _target.SizeChanged += TargetChanged;
                    _target.ParentChanged += TargetParentChanged;
                    _target.VisibleChanged += TargetChanged;
                    _target.HandleCreated += TargetChanged;
                    _target.HandleDestroyed += TargetChanged;
                    _target.Disposed += TargetDisposed;
                }

                if (_host != null)
                {
                    _host.LocationChanged += HostChanged;
                    _host.SizeChanged += HostChanged;
                    _host.ParentChanged += HostChanged;
                    _host.Layout += HostLayout;
                    if (_host is ScrollableControl sc)
                        sc.Scroll += HostScroll;
                }

                WireAncestors();
            }

            private void Unwire()
            {
                UnwireAncestors();

                if (_target != null)
                {
                    _target.LocationChanged -= TargetChanged;
                    _target.SizeChanged -= TargetChanged;
                    _target.ParentChanged -= TargetParentChanged;
                    _target.VisibleChanged -= TargetChanged;
                    _target.HandleCreated -= TargetChanged;
                    _target.HandleDestroyed -= TargetChanged;
                    _target.Disposed -= TargetDisposed;
                }

                if (_host != null)
                {
                    _host.LocationChanged -= HostChanged;
                    _host.SizeChanged -= HostChanged;
                    _host.ParentChanged -= HostChanged;
                    _host.Layout -= HostLayout;
                    if (_host is ScrollableControl sc)
                        sc.Scroll -= HostScroll;
                }
            }

            private void WireAncestors()
            {
                UnwireAncestors();

                if (_target == null || _target.IsDisposed) return;
                if (_host == null || _host.IsDisposed) return;

                // IMPORTANTISSIMO: quando un ancestor si sposta (es. CarouselViewport che scrolla
                // muovendo la FlowLayoutPanel), il target NON riceve LocationChanged, quindi
                // l'adorner rischia di rimanere "in aria" su un riquadro inesistente.
                try
                {
                    for (Control? p = _target.Parent; p != null && !ReferenceEquals(p, _host); p = p.Parent)
                    {
                        _ancestors.Add(p);
                        p.LocationChanged += AncestorChanged;
                        p.SizeChanged += AncestorChanged;
                        p.VisibleChanged += AncestorChanged;
                        p.Layout += AncestorLayout;

                        if (p is ScrollableControl sc)
                            sc.Scroll += AncestorScroll;
                    }
                }
                catch { }
            }

            private void UnwireAncestors()
            {
                if (_ancestors.Count == 0) return;
                foreach (var p in _ancestors)
                {
                    try
                    {
                        p.LocationChanged -= AncestorChanged;
                        p.SizeChanged -= AncestorChanged;
                        p.VisibleChanged -= AncestorChanged;
                        p.Layout -= AncestorLayout;

                        if (p is ScrollableControl sc)
                            sc.Scroll -= AncestorScroll;
                    }
                    catch { }
                }
                _ancestors.Clear();
            }

            private void AncestorChanged(object? sender, EventArgs e) => Reposition();
            private void AncestorLayout(object? sender, LayoutEventArgs e) => Reposition();
            private void AncestorScroll(object? sender, ScrollEventArgs e) => Reposition();

            private void TargetDisposed(object? sender, EventArgs e) => HideAndClear();

            private void TargetChanged(object? sender, EventArgs e) => Reposition();
            private void HostChanged(object? sender, EventArgs e) => Reposition();
            private void HostLayout(object? sender, LayoutEventArgs e) => Reposition();
            private void HostScroll(object? sender, ScrollEventArgs e) => Reposition();

            private void TargetParentChanged(object? sender, EventArgs e)
            {
                if (_target == null || _target.IsDisposed)
                {
                    HideAndClear();
                    return;
                }

                // chain cambiata → ri-aggancia gli ancestor
                UnwireAncestors();

                var newHost = FindHost(_target);
                if (newHost == null || newHost.IsDisposed)
                {
                    HideAndClear();
                    return;
                }

                if (!ReferenceEquals(_host, newHost))
                {
                    // sgancia eventi host precedente
                    if (_host != null)
                    {
                        _host.LocationChanged -= HostChanged;
                        _host.SizeChanged -= HostChanged;
                        _host.ParentChanged -= HostChanged;
                        _host.Layout -= HostLayout;
                        if (_host is ScrollableControl sc)
                            sc.Scroll -= HostScroll;
                    }

                    _host = newHost;
                    if (Parent != _host)
                        _host.Controls.Add(this);

                    _host.LocationChanged += HostChanged;
                    _host.SizeChanged += HostChanged;
                    _host.ParentChanged += HostChanged;
                    _host.Layout += HostLayout;
                    if (_host is ScrollableControl sc2)
                        sc2.Scroll += HostScroll;
                }

                Reposition();

                WireAncestors();
            }

            private void ScheduleReposition()
            {
                if (_repositionPending) return;
                if (_target == null || _target.IsDisposed) return;

                _repositionPending = true;
                try
                {
                    _target.BeginInvoke(new Action(() =>
                    {
                        _repositionPending = false;
                        Reposition();
                    }));
                }
                catch
                {
                    _repositionPending = false;
                }
            }

            private void Reposition()
            {
                if (_target == null || _target.IsDisposed || _host == null || _host.IsDisposed)
                {
                    Visible = false;
                    return;
                }

                // Se il target (o un suo parent) è invisibile: nascondi.
                if (!_target.Visible)
                {
                    Visible = false;
                    return;
                }

                // Su prime aperture può arrivare un Attach prima della creazione dell’handle.
                if (!_target.IsHandleCreated || !_host.IsHandleCreated)
                {
                    Visible = false;
                    ScheduleReposition();
                    return;
                }

                try
                {
                    var rc = _target.RectangleToScreen(_target.ClientRectangle);
                    var tl = _host.PointToClient(new Point(rc.Left, rc.Top));

                    if (Parent != _host)
                        _host.Controls.Add(this);

                    var old = Bounds;

                    Bounds = new Rectangle(tl, rc.Size);
                    UpdateDonutRegion();

                    Visible = true;

                    try
                    {
                        if (_target != null && !_target.IsDisposed)
                            _target.Invalidate();

                        // invalidazioni piu' leggere: il controllo target si ridisegna da solo,
                        // evitiamo di forzare repaint profondi dell'intero form ad ogni spostamento del focus.
                        if (!old.IsEmpty) _host.Invalidate(old, false);
                        _host.Invalidate(Bounds, false);
                    }
                    catch { }

                    BringToFront();
                    Invalidate();
                }
                catch
                {
                    Visible = false;
                }
            }

            private void UpdateDonutRegion()
            {
                int t = RingThickness;
                if (Width <= t * 2 || Height <= t * 2)
                {
                    ClearRegion();
                    return;
                }

                try
                {
                    var old = _donutRegion;

                    using var gp = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
                    gp.AddRectangle(new Rectangle(0, 0, Width, Height));
                    gp.AddRectangle(new Rectangle(t, t, Width - 2 * t, Height - 2 * t));

                    _donutRegion = new Region(gp);
                    Region = _donutRegion;

                    try { old?.Dispose(); } catch { }
                }
                catch
                {
                    // Meglio nessuna region che coprire tutto il target.
                    ClearRegion();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var p = new Pen(Color.FromArgb(220, 0, 140, 255), 2f);

                // disegna sul bordo esterno (dentro Bounds)
                var r = new Rectangle(1, 1, Width - 3, Height - 3);
                g.DrawRectangle(p, r);
            }
        }

        // === Remote OSD (indicatori centrali mostrati SOLO per input dal remote server) ===
        private sealed class RemoteOsdOverlay : Control
        {
            private const int MinShowMs = 250;

            private string? _svgPath;
            private float? _bar01;
            private string? _text;

            private DateTime _showUntil = DateTime.MinValue;
            private DateTime _fadeAt = DateTime.MinValue;
            private float _opacity = 0f;

            private readonly System.Windows.Forms.Timer _timer;

            private readonly Dictionary<(string path, int sizePx, int argb), Bitmap> _svgCache =
                new();

            public RemoteOsdOverlay()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;
                TabStop = false;
                Enabled = false;
                Visible = false;

                _timer = new System.Windows.Forms.Timer { Interval = 30 };
                _timer.Tick += (_, __) => Tick();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _timer.Stop(); } catch { }
                    try { _timer.Dispose(); } catch { }

                    foreach (var kv in _svgCache)
                    {
                        try { kv.Value?.Dispose(); } catch { }
                    }
                    _svgCache.Clear();
                }
                base.Dispose(disposing);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // Stesso trucco dell'HUD: pulisci con la TransparencyKey del form host.
                var host = FindForm();
                if (host != null && host.TransparencyKey != Color.Empty)
                {
                    e.Graphics.Clear(host.TransparencyKey);
                    return;
                }
                base.OnPaintBackground(e);
            }

            public void Show(string? svgPath, float? bar01 = null, int ms = 900, string? text = null)
            {
                if (IsDisposed) return;

                if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                    svgPath = null;

                _svgPath = svgPath;
                _text = text;

                if (string.IsNullOrWhiteSpace(_svgPath) && bar01 == null && string.IsNullOrWhiteSpace(_text))
                    return;
                _bar01 = bar01.HasValue ? Math.Clamp(bar01.Value, 0f, 1f) : (float?)null;

                var now = DateTime.UtcNow;
                int total = Math.Max(MinShowMs, ms);

                // fade finale (max 250ms, ~1/3 del totale)
                int fadeMs = Math.Min(250, Math.Max(120, total / 3));
                _showUntil = now.AddMilliseconds(total);
                _fadeAt = now.AddMilliseconds(total - fadeMs);

                _opacity = 1f;

                Visible = true;
                BringToFront();
                Invalidate();

                if (!_timer.Enabled) _timer.Start();
            }

            private void Tick()
            {
                var now = DateTime.UtcNow;

                if (now >= _showUntil)
                {
                    Visible = false;
                    _opacity = 0f;
                    try { _timer.Stop(); } catch { }
                    Invalidate();
                    return;
                }

                if (_fadeAt != DateTime.MinValue && now >= _fadeAt)
                {
                    double t = (now - _fadeAt).TotalMilliseconds / Math.Max(1, (_showUntil - _fadeAt).TotalMilliseconds);
                    _opacity = (float)(1.0 - Math.Clamp(t, 0, 1));
                    Invalidate();
                }
                else
                {
                    if (_opacity != 1f)
                    {
                        _opacity = 1f;
                        Invalidate();
                    }
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                if (!Visible || _opacity <= 0.01f) return;
                if (Width <= 0 || Height <= 0) return;

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                bool hasBar = _bar01.HasValue;
                bool hasText = !string.IsNullOrWhiteSpace(_text);
                int boxW = Math.Min(360, Math.Max(220, Width / 3));
                int boxH = hasBar ? (hasText ? 170 : 140) : (hasText ? 150 : 110);

                var rc = new Rectangle((Width - boxW) / 2, (Height - boxH) / 2, boxW, boxH);

                using (var gp = RoundRect(rc, 22))
                using (var br = new SolidBrush(Color.FromArgb((int)(170 * _opacity), 0, 0, 0)))
                    g.FillPath(br, gp);

                // Icon
                int iconSize = hasBar ? 62 : 70;
                var iconRect = new Rectangle(rc.X + (rc.Width - iconSize) / 2, rc.Y + 18, iconSize, iconSize);

                if (!string.IsNullOrWhiteSpace(_svgPath) && File.Exists(_svgPath))
                {
                    try
                    {
                        using var bmp = GetSvgBitmap(_svgPath, iconSize, Color.White);
                        DrawImageAlpha(g, bmp, iconRect, _opacity);
                    }
                    catch { }
                }

                // Text
                if (hasText)
                {
                    try
                    {
                        using var f = new Font("Segoe UI Semibold", 12f, FontStyle.Regular);
                        var tr = new Rectangle(rc.X + 18, iconRect.Bottom + 10, rc.Width - 36, 24);
                        var c = Color.FromArgb((int)(230 * _opacity), 255, 255, 255);
                        TextRenderer.DrawText(g, _text, f, tr, c,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    }
                    catch { }
                }

                // Volume bar
                if (hasBar)
                {
                    float v = _bar01 ?? 0f;
                    int trackW = Math.Min(220, rc.Width - 60);
                    int trackH = 10;
                    int trackX = rc.X + (rc.Width - trackW) / 2;
                    int trackY = rc.Bottom - 28;

                    var track = new Rectangle(trackX, trackY, trackW, trackH);
                    using (var gpTrack = RoundRect(track, trackH / 2))
                    using (var brTrack = new SolidBrush(Color.FromArgb((int)(80 * _opacity), 255, 255, 255)))
                        g.FillPath(brTrack, gpTrack);

                    int fillW = (int)Math.Round(trackW * v);
                    if (fillW > 0)
                    {
                        var fill = new Rectangle(trackX, trackY, fillW, trackH);
                        using (var gpFill = RoundRect(fill, trackH / 2))
                        using (var brFill = new SolidBrush(Color.FromArgb((int)(210 * _opacity), 255, 255, 255)))
                            g.FillPath(brFill, gpFill);
                    }
                }
            }

            private static void DrawImageAlpha(Graphics g, Image img, Rectangle dest, float opacity)
            {
                opacity = Math.Clamp(opacity, 0f, 1f);

                if (opacity >= 0.995f)
                {
                    g.DrawImage(img, dest);
                    return;
                }

                using var ia = new ImageAttributes();
                var cm = new ColorMatrix
                {
                    Matrix00 = 1f,
                    Matrix11 = 1f,
                    Matrix22 = 1f,
                    Matrix33 = opacity,
                    Matrix44 = 1f
                };
                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(img, dest, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }

            private static GraphicsPath RoundRect(Rectangle r, int radius)
            {
                int rr = Math.Max(2, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
                int d = rr * 2;

                var gp = new GraphicsPath();
                gp.AddArc(r.Left, r.Top, d, d, 180, 90);
                gp.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                gp.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                gp.CloseFigure();
                return gp;
            }

            private Bitmap GetSvgBitmap(string svgPath, int sizePx, Color tint)
            {
                int argb = tint.ToArgb();

                if (_svgCache.TryGetValue((svgPath, sizePx, argb), out var cached) && cached != null)
                    return (Bitmap)cached.Clone();

                Bitmap rendered = RenderSvgSkia(svgPath, sizePx, tint);

                try
                {
                    if (_svgCache.TryGetValue((svgPath, sizePx, argb), out var old) && old != null)
                        old.Dispose();
                    _svgCache[(svgPath, sizePx, argb)] = (Bitmap)rendered.Clone();
                }
                catch { }

                return rendered;
            }

            private static Bitmap RenderSvgSkia(string svgPath, int targetPx, Color tint)
            {
                var svg = new SKSvg();
                svg.Load(svgPath);
                if (svg.Picture == null) throw new InvalidOperationException("SVG Picture null: " + svgPath);

                var bounds = svg.Picture.CullRect;
                float srcW = bounds.Width;
                float srcH = bounds.Height;
                if (srcW <= 0 || srcH <= 0) throw new InvalidOperationException("SVG bounds invalid: " + svgPath);

                float scale = targetPx / Math.Max(srcW, srcH);
                int outW = Math.Max(1, (int)Math.Round(srcW * scale));
                int outH = Math.Max(1, (int)Math.Round(srcH * scale));

                using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(scale);

                using var paint = new SKPaint
                {
                    ColorFilter = SKColorFilter.CreateBlendMode(
                        new SKColor(tint.R, tint.G, tint.B, 255),
                        SKBlendMode.SrcIn)
                };
                canvas.SaveLayer(paint);
                canvas.DrawPicture(svg.Picture);
                canvas.Restore();
                canvas.Flush();

                using var img = surface.Snapshot();
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var ms = new MemoryStream(data.ToArray());
                using var tmp = Image.FromStream(ms);
                return new Bitmap(tmp);
            }
        }

        private readonly FocusAdorner _focusRing = new FocusAdorner();

        // helper per allargare rettangolo 
        private Control? _dpadRoot; // radice attuale (Libreria/Settings/Credits/Overlay)
        private Control? _focused;

        private static bool IsDescendant(Control root, Control c)
        {
            for (var p = c; p != null; p = p.Parent)
                if (ReferenceEquals(p, root)) return true;
            return false;
        }

        private static bool IsCursorOnlyDpadContainer(Control c)
        {
            if (c == null)
                return false;

            // I contenitori generici con Cursor.Hand finiscono spesso per occupare tutto il canvas
            // e producono una focus ring enorme al primo input tastiera.
            // Se non sono selezionabili esplicitamente (TabStop) li trattiamo come layout, non come target DPAD.
            return c is Panel
                || c is UserControl
                || c is FlowLayoutPanel
                || c is TableLayoutPanel;
        }

        private static bool LooksLikeOversizedCursorOnlySurface(Control c)
        {
            try
            {
                if (c == null || c.Parent == null)
                    return false;

                if (c.TabStop || c is ButtonBase || c is CheckBox || c is ComboBox)
                    return false;

                int parentW = Math.Max(1, c.Parent.ClientSize.Width);
                int parentH = Math.Max(1, c.Parent.ClientSize.Height);
                if (parentW <= 1 || parentH <= 1 || c.Width <= 0 || c.Height <= 0)
                    return false;

                double coverage = (double)(c.Width * c.Height) / (double)(parentW * parentH);
                return coverage >= 0.65d;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDpadFocusable(Control c)
        {
            if (c == null) return false;
            if (!c.Visible || !c.Enabled) return false;

            // Explicit opt-out for DPAD navigation (e.g. pairing banner, decorative panels)
            try
            {
                if (c.Tag is string tag && string.Equals(tag, "nodpad", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch { }

            // evita roba "tecnica" che spacca il movimento (scrollbar, picturebox interne, adorners)
            if (c is FocusAdorner) return false;
            if (c is PictureBox) return false;

            string tn = c.GetType().Name;
            if (string.Equals(tn, "ThemedVScroll", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(tn, "SkinnedFlow", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(tn, "LoadingMask", StringComparison.OrdinalIgnoreCase)) return false;

            if (c.TabStop || c is ButtonBase || c is CheckBox || c is ComboBox)
                return true;

            if (c.Cursor == Cursors.Hand)
                return !IsCursorOnlyDpadContainer(c) && !LooksLikeOversizedCursorOnlySurface(c);

            return false;
        }

        private Control ResolveDpadRoot()
        {
            if (_settingsModal?.Visible == true) return _settingsModal;
            if (_creditsModal?.Visible == true) return _creditsModal;

            if (_libraryPage?.Visible == true)
            {
                try { return _libraryPage.GetRemoteFocusRoot(); }
                catch { return _libraryPage; }
            }

            return this;
        }

        private void EnsureDpadRoot()
        {
            var root = ResolveDpadRoot();
            if (!ReferenceEquals(root, _dpadRoot))
            {
                RemoteAttachRoot(root, forceReset: true);
            }
        }

        private void ResetLibraryRemoteActivation(bool clearFocusRing)
        {
            _libraryRemoteActivationArmed = false;
            if (clearFocusRing)
                _libraryHighlightNeedsReactivation = false;

            if (!clearFocusRing)
            {
                try
                {
                    if (_focused != null && (_focused.IsDisposed || (_libraryPage != null && IsDescendant(_libraryPage, _focused) && !_focused.Visible)))
                    {
                        _focused = null;
                        try { _focusRing.Attach(null); } catch { }
                    }
                }
                catch
                {
                    _focused = null;
                    try { _focusRing.Attach(null); } catch { }
                }
                return;
            }

            try
            {
                if (_focused != null && _libraryPage != null && !_focused.IsDisposed && IsDescendant(_libraryPage, _focused))
                    _focused = null;
            }
            catch { _focused = null; }

            try { _focusRing.Attach(null); } catch { }
        }

        private bool ConsumeLibraryActivationInputIfNeeded()
        {
            if (_dpadRoot is not MediaLibraryPage lib)
                return false;

            if (!lib.IsRemoteNavigationReady)
            {
                try
                {
                    Control? loadingTarget = lib.CoerceRemoteFocus(_focused);
                    loadingTarget = GetDpadFocusableAncestor(loadingTarget) ?? loadingTarget;

                    if (IsValidLibraryFocusTarget(loadingTarget) && !lib.IsRemoteContentFocusCandidate(loadingTarget))
                        return false; // menu sinistro / shell ancora navigabili mentre i contenuti caricano
                }
                catch { }

                return true;
            }

            if (_libraryRemoteActivationArmed)
                return false;

            Control? focusTarget = null;
            try
            {
                focusTarget = lib.CoerceRemoteFocus(_focused);
            }
            catch
            {
                focusTarget = _focused;
            }

            focusTarget = GetDpadFocusableAncestor(focusTarget) ?? focusTarget;

            try
            {
                if (focusTarget == null || focusTarget.IsDisposed || !IsDescendant(lib, focusTarget) || !IsDpadFocusable(focusTarget))
                    focusTarget = lib.GetRemoteDefaultFocusTarget();
            }
            catch { }

            focusTarget = GetDpadFocusableAncestor(focusTarget) ?? focusTarget;

            _libraryRemoteActivationArmed = true;
            bool mustConsumeThisActivationInput = _libraryHighlightNeedsReactivation;
            _libraryHighlightNeedsReactivation = false;

            if (focusTarget != null && !focusTarget.IsDisposed)
            {
                _focused = focusTarget;
                try { focusTarget.Focus(); } catch { }
                EnsureDpadVisible(focusTarget);
                try { _focusRing.Attach(focusTarget); } catch { }
            }
            else
            {
                try { _focusRing.Attach(null); } catch { }
            }

            // Comportamento richiesto:
            // - dopo il mouse il primo input riattiva solo l'evidenziazione;
            // - negli altri casi, se esiste gia' un target valido, l'input resta attivo subito.
            return mustConsumeThisActivationInput || focusTarget == null || focusTarget.IsDisposed;
        }

        private void RemoteAttachRoot(Control root, bool forceReset = false)
        {
            if (root == null) return;

            bool keep = !forceReset
                        && ReferenceEquals(_dpadRoot, root)
                        && _focused != null
                        && !_focused.IsDisposed
                        && IsDescendant(root, _focused)
                        && IsDpadFocusable(_focused);

            _dpadRoot = root;

            if (!keep)
            {
                if (root is MediaLibraryPage lib)
                    _focused = lib.GetRemoteDefaultFocusTarget() ?? FindFirstFocusable(root);
                else
                    _focused = FindFirstFocusable(root);
            }

            MediaLibraryPage? libRoot = root as MediaLibraryPage;
            bool shouldAttach = !(libRoot is MediaLibraryPage) || (_libraryRemoteActivationArmed && libRoot != null && libRoot.IsRemoteNavigationReady);

            if (_focused != null && shouldAttach)
            {
                try { _focused.Focus(); } catch { }
                EnsureDpadVisible(_focused);
                _focusRing.Attach(_focused);
            }
            else
            {
                try { _focusRing.Attach(null); } catch { }
            }

            if (!shouldAttach)
                return;

            try
            {
                var f0 = _focused;
                BeginInvoke(new Action(() =>
                {
                    if (f0 == null || f0.IsDisposed) return;
                    if (!ReferenceEquals(_focused, f0)) return;
                    EnsureDpadVisible(f0);
                    _focusRing.Attach(f0);
                }));
            }
            catch { }
        }

        private void EnsureDpadVisible(Control target)
        {
            if (target == null || target.IsDisposed) return;
            if (!target.Visible || target.Width <= 0 || target.Height <= 0) return;
            try
            {
                if (target.FindForm() != this)
                    return;
            }
            catch { return; }

            // 1) Se siamo dentro un carosello con scroll manuale, chiedi al viewport di portarlo in vista (reflection)
            try
            {
                for (Control? p = target.Parent; p != null; p = p.Parent)
                {
                    var mi = p.GetType().GetMethod(
                        "EnsureChildVisible",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic,
                        binder: null,
                        types: new[] { typeof(Control) },
                        modifiers: null);

                    if (mi != null)
                    {
                        mi.Invoke(p, new object[] { target });
                        break;
                    }
                }
            }
            catch { }

            // 2) AutoScroll containers: scrolla per far apparire il controllo
            try
            {
                for (Control? p = target.Parent; p != null; p = p.Parent)
                {
                    if (p is ScrollableControl sc && sc.AutoScroll)
                    {
                        var toShow = target;
                        while (toShow.Parent != null && toShow.Parent != sc) toShow = toShow.Parent;
                        sc.ScrollControlIntoView(toShow);
                        break;
                    }
                }
            }
            catch { }
        }

        private void RemoteMove(string dir)
        {
            EnsureDpadRoot();
            if (_dpadRoot == null) return;

            if (ConsumeLibraryActivationInputIfNeeded())
                return;

            if (_dpadRoot is MediaLibraryPage lib)
            {
                try { lib.SetDpadInputIsRemote(_lastDpadFromRemote); } catch { }

                var curLib = lib.CoerceRemoteFocus(_focused);
                if (curLib == null) return;

                if (lib.TryRemoteMove(curLib, dir, out var nextLib) && nextLib != null)
                {
                    var focusTarget = GetDpadFocusableAncestor(nextLib) ?? nextLib;
                    if (focusTarget == null || focusTarget.IsDisposed)
                        return;
                    if (ReferenceEquals(_focused, focusTarget))
                        return;
                    _focused = focusTarget;
                    try { focusTarget.Focus(); } catch { }
                    EnsureDpadVisible(focusTarget);
                    _focusRing.Attach(focusTarget);
                }
                return;
            }

            var cur = (_focused != null && !_focused.IsDisposed && IsDescendant(_dpadRoot, _focused))
                ? _focused
                : FindFirstFocusable(_dpadRoot);

            if (cur == null) return;

            var next = FindNextByDirection(_dpadRoot, cur, dir);
            if (next != null)
            {
                if (ReferenceEquals(_focused, next))
                    return;
                _focused = next;
                try { next.Focus(); } catch { }
                EnsureDpadVisible(next);
                _focusRing.Attach(next);
            }
        }

        private void RemoteOk()
        {
            EnsureDpadRoot();
            if (ConsumeLibraryActivationInputIfNeeded())
                return;

            Control? t = _focused;
            if (_dpadRoot is MediaLibraryPage libRoot)
            {
                try
                {
                    t = libRoot.CoerceRemoteFocus(_focused);
                }
                catch
                {
                    t = _focused;
                }

                t = GetDpadFocusableAncestor(t) ?? t;
                if (t != null && !t.IsDisposed && !ReferenceEquals(_focused, t))
                {
                    _focused = t;
                    try { t.Focus(); } catch { }
                    EnsureDpadVisible(t);
                    try { _focusRing.Attach(t); } catch { }
                }
            }

            if (t == null || t.IsDisposed) return;

            // Propagate the latest DPAD input source to the library page (used e.g. to gate OSK).
            try
            {
                if (_dpadRoot is MediaLibraryPage lib0)
                    lib0.SetDpadInputIsRemote(_lastDpadFromRemote);
            }
            catch { }

            // CheckBox → toggle
            if (t is CheckBox cb)
            {
                cb.Checked = !cb.Checked;
                return;
            }

            // ComboBox → apri tendina
            if (t is ComboBox cmb)
            {
                try { cmb.DroppedDown = true; } catch { }
                return;
            }

            // MediaLibraryPage: OK sul search (o componenti speciali) può essere gestito senza "click"
            try
            {
                if (_dpadRoot is MediaLibraryPage lib0 && lib0.TryRemoteOk(t, out var nextOk) && nextOk != null)
                {
                    var focusTarget = GetDpadFocusableAncestor(nextOk) ?? nextOk;
                    _focused = focusTarget;
                    try { focusTarget.Focus(); } catch { }
                    EnsureDpadVisible(focusTarget);
                    _focusRing.Attach(focusTarget);
                    return;
                }
            }
            catch { }

            // Qualsiasi altro controllo (inclusi i tuoi bottoni custom): invoca il Click protetto via reflection
            try
            {
                var mi = t.GetType().GetMethod(
                    "OnClick",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic
                );
                mi?.Invoke(t, new object[] { EventArgs.Empty });
            }
            catch { }

            // MediaLibraryPage: dopo un click nel menu sinistro porta il focus ai contenuti (carosello/griglia)
            try
            {
                EnsureDpadRoot();
                if (_dpadRoot is MediaLibraryPage libPre)
                {
                    try { libPre.SetDpadInputIsRemote(_lastDpadFromRemote); } catch { }
                }
                if (_dpadRoot is MediaLibraryPage lib && lib.TryRemotePostOkFocus(t, out var post) && post != null)
                {
                    var focusTarget = GetDpadFocusableAncestor(post) ?? post;
                    _focused = focusTarget;
                    try { focusTarget.Focus(); } catch { }
                    EnsureDpadVisible(focusTarget);
                    _focusRing.Attach(focusTarget);
                }
            }
            catch { }
        }

        // Trova un primo controllo "sensato"
        private static Control? FindFirstFocusable(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (!IsDpadFocusable(c))
                {
                    var deep0 = FindFirstFocusable(c);
                    if (deep0 != null) return deep0;
                    continue;
                }

                return c;
            }
            return null;
        }

        // Heuristica DPAD: scegli il controllo più vicino nella direzione
        private static Control? FindNextByDirection(Control root, Control from, string dir)
        {
            var list = new List<Control>();

            void Collect(Control k)
            {
                foreach (Control c in k.Controls)
                {
                    if (!IsDpadFocusable(c))
                    {
                        Collect(c);
                        continue;
                    }

                    if (!ReferenceEquals(c, from))
                        list.Add(c);

                    Collect(c);
                }
            }

            Collect(root);

            if (list.Count == 0) return null;

            var src = from.RectangleToScreen(from.ClientRectangle);
            var sc = new Point(src.Left + src.Width / 2, src.Top + src.Height / 2);

            static bool Vertical(string d) => d == "up" || d == "down";
            static int Sign(string d) => (d == "right" || d == "down") ? +1 : -1;

            Control? best = null;
            double bestScore = double.MaxValue;

            foreach (var c in list)
            {
                var rc = c.RectangleToScreen(c.ClientRectangle);
                var cc = new Point(rc.Left + rc.Width / 2, rc.Top + rc.Height / 2);

                var dx = cc.X - sc.X;
                var dy = cc.Y - sc.Y;

                // vincolo direzione
                if (Vertical(dir))
                {
                    if (Sign(dir) * dy <= 0) continue; // deve stare "sopra" o "sotto"
                }
                else
                {
                    if (Sign(dir) * dx <= 0) continue; // deve stare "a sx" o "a dx"
                }

                // penalizza l’angolo
                double primary = Vertical(dir) ? Math.Abs(dy) : Math.Abs(dx);
                double secondary = Vertical(dir) ? Math.Abs(dx) : Math.Abs(dy);
                double score = primary * 1.0 + secondary * 0.6;

                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        // =========================
        // DPAD routing (Splash / Modal UI / HUD)
        // =========================
        private bool HandleDpadMove(string dir)
        {
            // Splash (home)
            if (_splash?.Visible == true && (_libraryPage?.Visible != true) && (_settingsModal?.Visible != true) && (_creditsModal?.Visible != true))
            {
                try { _splash.DpadMove(dir); } catch { }
                return true;
            }

            // Modal UI (Settings / Credits / Libreria)
            if ((_settingsModal?.Visible == true) || (_creditsModal?.Visible == true) || (_libraryPage?.Visible == true))
            {
                RemoteMove(dir);
                return true;
            }

            return false;
        }

        private bool HandleDpadOk()
        {
            // Splash (home)
            if (_splash?.Visible == true && (_libraryPage?.Visible != true) && (_settingsModal?.Visible != true) && (_creditsModal?.Visible != true))
            {
                try { _splash.DpadOk(); } catch { }
                return true;
            }

            // Modal UI (Settings / Credits / Libreria)
            if ((_settingsModal?.Visible == true) || (_creditsModal?.Visible == true) || (_libraryPage?.Visible == true))
            {
                RemoteOk();
                return true;
            }

            // Playback: OK mostra solo HUD (niente selezione DPAD)
            if (_hud != null)
            {
                if (!_hud.Visible) _hud.Visible = true;
                try { if (_hud.DpadMode) _hud.DpadDeactivate(); } catch { }
                _hud.ShowOnce(2200);
                return true;
            }

            return false;
        }

        private bool HandleDpadBack()
        {
            // HUD: esci dalla modalità DPAD
            if (_hud?.DpadMode == true)
            {
                _hud.DpadDeactivate();
                return true;
            }

            // Overlay informativi: chiudili prima di uscire dal playback
            if (_infoOverlay?.Visible == true)
            {
                _infoOverlay.Visible = false;
                BringOverlaysToFront();
                return true;
            }

            // Settings
            if (_settingsModal?.Visible == true)
            {
                _settingsModal.Visible = false;

                // Nascondi il focus ring e resetta lo stato DPAD (evita ring “fantasma”)
                try { _focusRing.Attach(null); } catch { }
                _focused = null;
                _dpadRoot = null;

                BringOverlaysToFront();
                return true;
            }

            // Credits
            if (_creditsModal?.Visible == true)
            {
                HideCreditsModal();
                return true;
            }

            // Libreria
            if (_libraryPage?.Visible == true)
            {
                try
                {
                    // Coerenza richiesta:
                    // - ENTER/OK: entra nella zona contenuti
                    // - ESC/BACK: torna al menu sinistro (se eri nei contenuti)
                    // - ESC/BACK dal menu sinistro: chiude la libreria
                    try { _libraryPage.SetDpadInputIsRemote(_lastDpadFromRemote); } catch { }

                    if (_libraryPage.TryRemoteBack(_focused, out var next))
                    {
                        // Root potrebbe cambiare (es. chiusura overlay root) → riallinea senza resettare
                        _dpadRoot = ResolveDpadRoot();

                        if (next != null && !next.IsDisposed)
                        {
                            _focused = next;
                            try { _focused.Focus(); } catch { }
                            EnsureDpadVisible(_focused);
                            _focusRing.Attach(_focused);
                        }
                        else if (!_libraryPage.IsRemoteNavigationReady)
                        {
                            _focused = null;
                            try { _focusRing.Attach(null); } catch { }
                        }
                        else
                        {
                            // fallback: resta coerente con il default focus target
                            _focused = _libraryPage.GetRemoteDefaultFocusTarget();
                            if (_focused != null)
                            {
                                try { _focused.Focus(); } catch { }
                                EnsureDpadVisible(_focused);
                            }
                            _focusRing.Attach(_focused);
                        }

                        return true;
                    }
                }
                catch { }

                HideLibrary();
                return true;
            }

            // Playback → libreria
            if (_engine != null)
            {
                CloseCurrentToLibrary();
                return true;
            }

            return false;
        }

        private void HideCreditsModal()
        {
            try
            {
                _creditsModal.Visible = false;

                // Nascondi il focus ring e resetta lo stato DPAD
                try { _focusRing.Attach(null); } catch { }
                _focused = null;
                _dpadRoot = null;

                _overlayHost.SetInteractive(false);
                if (_creditsModal.Tag is bool wasInline && wasInline) UseOverlayInline(true);
                if (_overlayInlineHost != null) _overlayInlineHost.Visible = false;
                if (_splash.Visible) RedrawHome();
            }
            catch { }
            BringOverlaysToFront();
        }

        private void ShowSettingsModal()
        {
            UseOverlayInline(false);
            _settingsModal.Tag = false;

            _settingsModal.SyncFromState(_targetFps, _enableUpscaling, _preferBitstreamUi);
            _settingsModal.Visible = true;
            _settingsModal.BringToFront();
            _settingsModal.EnsureHostsLoaded();

            // NEW: attacca DPAD ai controlli della finestra impostazioni
            RemoteAttachRoot(_settingsModal);

            BeginInvoke(new Action(() => _settingsModal.FocusApply()));
            BringOverlaysToFront();
        }

        private void ShowCreditsModal()
        {
            UseOverlayInline(false);
            _creditsModal.Tag = false;

            _creditsModal.Visible = true;
            _creditsModal.BringToFront();

            // NEW: attacca DPAD ai controlli della finestra crediti
            RemoteAttachRoot(_creditsModal);

            BringOverlaysToFront();
        }

        private void EnsureActive()
        {
            try { if (!Focused) Activate(); } catch { }
        }

        // =========================
        // Remote server → OSD centrale (solo durante la riproduzione)
        // =========================
        private bool CanShowRemotePlaybackOsd()
        {
            if (_remoteOsd == null || _remoteOsd.IsDisposed) return false;
            if (_engine == null) return false;

            // SOLO durante la riproduzione (non nel menu/splash/modal)
            if (_splash?.Visible == true) return false;
            if (_libraryPage?.Visible == true) return false;
            if (_settingsModal?.Visible == true) return false;
            if (_creditsModal?.Visible == true) return false;

            // La richiesta era specifica per la riproduzione (HUD classica, non modalità foto)
            if (IsPhotoMode) return false;

            return true;
        }

        private void SuppressHudWakeForRemoteOsd(int ms)
        {
            try
            {
                var until = DateTime.UtcNow.AddMilliseconds(Math.Max(700, ms + 250));
                if (until > _suppressHudWakeUntilUtc)
                    _suppressHudWakeUntilUtc = until;

                _scrubActive = false;

                var old = Interlocked.Exchange(ref _thumbCts, null);
                try { old?.Cancel(); } catch { }
                try { old?.Dispose(); } catch { }

                if (_hud != null)
                {
                    try { _hud.Visible = false; } catch { }
                    try { _hud.TimelineVisible = false; } catch { }
                    try { _hud.ClearRemoteScrub(); } catch { }
                    try { _hud.SetPreview(null, _engine?.PositionSeconds ?? 0); } catch { }
                }

                try
                {
                    _hudWakeAnchorPos = Control.MousePosition;
                    _hudWakeLastMousePos = _hudWakeAnchorPos;
                    _hudWakeNeedsIntentionalMove = true;
                }
                catch { }
            }
            catch { }
        }

        private void ShowRemoteOsd(string? svgPath, float? bar01 = null, int ms = 900, string? text = null)
        {
            if (!CanShowRemotePlaybackOsd()) return;

            SuppressHudWakeForRemoteOsd(ms);

            try { _remoteOsd.Show(svgPath, bar01, ms, text); } catch { }
            BringOverlaysToFront();
        }

        private string? GetVolumeOsdSvg(float vol01, bool muted)
        {
            if (_hud == null) return null;

            if (muted) return _hud.SvgPathVolMute;
            if (vol01 <= 0.001f) return _hud.SvgPathVolZero;
            if (vol01 < 0.35f) return _hud.SvgPathVolLow;
            return _hud.SvgPathVolHigh;
        }

        private void RemoteSetVolume(float vol01)
        {
            if (_hud == null) return;

            vol01 = Math.Clamp(vol01, 0f, 1f);

            // Unmute se serve
            if (_hud.IsMuted && vol01 > 0.0001f)
                _hud.SetMuted(false);

            // aggiorna backup per unmute
            if (!_hud.IsMuted && vol01 > 0.0001f)
                _remoteVolBeforeMute = vol01;

            ApplyVolume(vol01);
            try { _hud.SetExternalVolume(vol01); } catch { }

            ShowRemoteOsd(GetVolumeOsdSvg(vol01, _hud.IsMuted), bar01: _hud.IsMuted ? 0f : vol01, ms: 900);
        }

        private void RemoteAdjustVolume(float delta)
        {
            if (_hud == null) return;

            float cur = _hud.IsMuted ? 0f : _hud.GetVolume();
            float next = Math.Clamp(cur + delta, 0f, 1f);
            RemoteSetVolume(next);
        }

        private void RemoteToggleMute()
        {
            if (_hud == null) return;

            if (_hud.IsMuted)
            {
                float v = Math.Clamp(_remoteVolBeforeMute, 0.05f, 1f);
                _hud.SetMuted(false);
                RemoteSetVolume(v);
            }
            else
            {
                float cur = _hud.GetVolume();
                if (cur > 0.0001f) _remoteVolBeforeMute = cur;

                _hud.SetMuted(true);
                ApplyVolume(0f);
                try { _hud.SetExternalVolume(0f); } catch { }

                ShowRemoteOsd(GetVolumeOsdSvg(0f, muted: true), bar01: 0f, ms: 900);
            }
        }

        private void SeekRelative(double delta)
        {
            if (_engine == null || _duration <= 0) return;
            double t = Math.Clamp(_engine.PositionSeconds + delta, 0, Math.Max(0.01, _duration));
            _engine.PositionSeconds = t;
        }

        // ---------------- Remote scan (long-press skip) ----------------
        private bool IsRemoteScanActive => _remoteScanTimer?.Enabled == true;

        private void StopRemoteScan()
        {
            try { _remoteScanTimer?.Stop(); } catch { }
            _remoteScanDir = 0;
            _remoteScanSpeedIdx = 0;
        }

        /// <summary>
        /// Avvia o incrementa lo "scan" (skip continuo) da telecomando.
        /// - Primo trigger: parte a x0,5
        /// - Trigger successivi: x1 -> x2 -> x4 (max)
        /// </summary>
        private void StepRemoteScan(int dir)
        {
            if (_engine == null || _duration <= 0) return;

            if (_remoteScanTimer == null)
            {
                _remoteScanTimer = new System.Windows.Forms.Timer { Interval = 100 };
                _remoteScanTimer.Tick += (_, __) => RemoteScanTick();
            }

            // se non era attivo o cambio direzione: riparti da x0,5
            if (!_remoteScanTimer.Enabled || _remoteScanDir != dir)
            {
                _remoteScanDir = dir;
                _remoteScanSpeedIdx = 0;
                _remoteScanTimer.Start();
            }
            else
            {
                // già in scan nella stessa direzione: step velocità
                if (_remoteScanSpeedIdx < REMOTE_SCAN_SPEEDS.Length - 1)
                    _remoteScanSpeedIdx++;
            }

            ShowRemoteScanOsd();
        }

        private void RemoteScanTick()
        {
            if (_engine == null || _duration <= 0)
            {
                StopRemoteScan();
                return;
            }

            int idx = Math.Clamp(_remoteScanSpeedIdx, 0, REMOTE_SCAN_SPEEDS.Length - 1);
            double speed = REMOTE_SCAN_SPEEDS[idx];
            double dt = (_remoteScanTimer?.Interval ?? 100) / 1000.0;
            double delta = REMOTE_SCAN_BASE_SECS_PER_SEC * speed * dt * _remoteScanDir;
            if (Math.Abs(delta) < 0.0001) return;

            SeekRelative(delta);

            // stop automatico ai bordi
            try
            {
                if (_engine.PositionSeconds <= 0.0001 && _remoteScanDir < 0)
                    StopRemoteScan();
                else if (_engine.PositionSeconds >= _duration - 0.0001 && _remoteScanDir > 0)
                    StopRemoteScan();
            }
            catch { }
        }

        private void ShowRemoteScanOsd()
        {
            if (_hud == null) return;

            int idx = Math.Clamp(_remoteScanSpeedIdx, 0, REMOTE_SCAN_SPEEDS.Length - 1);
            double speed = REMOTE_SCAN_SPEEDS[idx];
            string txt = speed == 0.5 ? "x0,5" : ("x" + speed.ToString("0.#")).Replace('.', ',');

            string svg = _remoteScanDir < 0 ? _hud.SvgPathBack10 : _hud.SvgPathFwd10;
            ShowRemoteOsd(svg, bar01: null, ms: 900, text: txt);
        }

        private void SeekChapter(int dir)
        {
            if (_engine == null || _info == null || _info.Chapters.Count == 0) return;
            double cur = _engine.PositionSeconds;
            if (dir > 0)
            {
                var next = _info.Chapters.Select(c => c.start).FirstOrDefault(s => s > cur + 0.5);
                if (next > 0) _engine.PositionSeconds = Math.Min(next, Math.Max(0.01, _duration));
            }
            else
            {
                var prev = _info.Chapters.Select(c => c.start).Where(s => s < cur - 0.5).DefaultIfEmpty(0).Max();
                _engine.PositionSeconds = Math.Max(0, prev);
            }
        }
        private void ShowNextImage()
        {
            if (!IsPhotoMode || _imageFiles.Count == 0) return;

            try { _photoHud?.Wake(); } catch { }

            if (_imageIndex < 0 || _imageIndex >= _imageFiles.Count - 1)
                _imageIndex = 0;
            else
                _imageIndex++;

            OpenImage(_imageFiles[_imageIndex]);
        }

        private void ShowPrevImage()
        {
            if (!IsPhotoMode || _imageFiles.Count == 0) return;

            try { _photoHud?.Wake(); } catch { }

            if (_imageIndex <= 0)
                _imageIndex = _imageFiles.Count - 1;
            else
                _imageIndex--;

            OpenImage(_imageFiles[_imageIndex]);
        }

        private void Enable3D(Stereo3DMode mode)
        {
            if (mode == Stereo3DMode.None) return;

            _stereo = mode;

            // Se non siamo già su EVR, salviamo il renderer corrente e forziamo EVR
            if (_manualRendererChoice != VRChoice.EVR)
            {
                _savedRendererFor3D = _manualRendererChoice; // può essere anche null (Auto)
                _hasSavedRendererFor3D = true;

                // EVR non ha il nostro upscaling madVR: spegni eventuale upscaling
                _enableUpscaling = false;
                try { _engine?.SetUpscaling(false); } catch { }

                _manualRendererChoice = VRChoice.EVR;
                _lblStatus.Text = "3D→2D: forzato EVR (renderer precedente salvato)";
                ReopenSame(); // ricrea il graph con EVR e applica _stereo in OpenPath
            }
            else
            {
                // già EVR: basta settare il 3D
                try
                {
                    _engine?.SetStereo3D(_stereo);
                    UpdateVideoWindowForCurrentHost();
                }
                catch { }
            }

            _hud.ShowOnce(1200);
        }

        private void Disable3DRestoreRenderer()
        {
            _stereo = Stereo3DMode.None;

            if (_hasSavedRendererFor3D)
            {
                // Ripristina il renderer che avevamo prima di forzare EVR (anche Auto=null)
                _manualRendererChoice = _savedRendererFor3D;
                _savedRendererFor3D = null;
                _hasSavedRendererFor3D = false;

                _lblStatus.Text = "3D disattivato: ripristino renderer precedente";
                ReopenSame(); // ricrea il graph e torna all’immagine doppia
            }
            else
            {
                // Non avevamo forzato nulla: solo togli il 3D
                try
                {
                    _engine?.SetStereo3D(_stereo);
                    UpdateVideoWindowForCurrentHost();
                }
                catch { }
            }

            _hud.ShowOnce(1200);
        }

        // ===== Overlay "Audio Only" =====
        internal sealed class AudioOnlyOverlay : Control
        {
            private Image? _png;
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string? ImagePath { get; set; }
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string Caption { get; set; } = "Audio Only";

            public AudioOnlyOverlay()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var key = this.FindForm()?.TransparencyKey ?? Color.Black;
                e.Graphics.Clear(key);
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                var candidates = new[]
                {
                    ImagePath,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "AudioOnly.png"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "audioOnly.jpg"),
                }.Where(p => !string.IsNullOrWhiteSpace(p));

                string? found = candidates.FirstOrDefault(File.Exists);
                if (found != null)
                {
                    try
                    {
                        using var fs = new FileStream(found, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var bmp = Image.FromStream(fs);
                        _png = new Bitmap(bmp);
                        Dbg.Log("AudioOnlyOverlay: caricato PNG da " + found, Dbg.LogLevel.Info);
                    }
                    catch (Exception ex) { Dbg.Warn("AudioOnlyOverlay: errore caricando '" + found + "': " + ex.Message); }
                }
                else
                {
                    Dbg.Warn("AudioOnlyOverlay: PNG non trovato.");
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                if (_png != null)
                {
                    var maxW = (int)(Width * 0.4);
                    var maxH = (int)(Height * 0.4);
                    double s = Math.Min(maxW / (double)_png.Width, maxH / (double)_png.Height);
                    int w = Math.Max(1, (int)Math.Round(_png.Width * s));
                    int h = Math.Max(1, (int)Math.Round(_png.Height * s));
                    int x = (Width - w) / 2;
                    int y = (Height - h) / 2 - 24;

                    using (var glow = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
                        g.FillEllipse(glow, x - w * 0.08f, y - h * 0.08f, w * 1.16f, h * 1.16f);

                    g.DrawImage(_png, new Rectangle(x, y, w, h));
                }

                using var f = new Font("Segoe UI", 16, FontStyle.Bold);
                var sz = g.MeasureString(Caption, f);
                using var sh = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                using var fg = new SolidBrush(Color.FromArgb(230, 230, 230));
                float cx = (Width - sz.Width) / 2f;
                float cy = Height * 0.65f;
                g.DrawString(Caption, f, sh, cx + 1, cy + 1);
                g.DrawString(Caption, f, fg, cx, cy);
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x84;
                const int HTTRANSPARENT = -1;
                if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
                base.WndProc(ref m);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _png?.Dispose(); }
                base.Dispose(disposing);
            }
        }

        // ===== Overlay “Placeholder” (usato come gate pre-film) =====
        internal sealed class PausePlaceholderOverlay : Control
        {
            private readonly Random _rng = new();
            private Image? _img;
            private Image? _brandLogo;
            private string? _folder;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string Caption { get; set; } = "PAUSA";

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool DrawCaptionAlways { get; set; } = false;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string TitleText { get; set; } = string.Empty;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string SubtitleText { get; set; } = string.Empty;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool ShowBranding { get; set; } = false;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool UseCoverImage { get; set; } = false;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool HasImage => _img != null;

            public PausePlaceholderOverlay()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            public void SetFolder(string folder)
            {
                _folder = folder;
            }

            public void SetBrandLogo(Image? logo)
            {
                try { _brandLogo?.Dispose(); } catch { }
                _brandLogo = null;

                if (logo != null)
                {
                    try { _brandLogo = new Bitmap(logo); } catch { _brandLogo = null; }
                }

                try { Invalidate(); } catch { }
            }

            public void ClearDisplayedImage()
            {
                ClearImage();
            }

            public void ShowPlaceholder(string? filePath)
            {
                // Se non specificato → random dalla cartella.
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    ShowRandomPlaceholder();
                    return;
                }

                try
                {
                    if (!File.Exists(filePath))
                    {
                        ShowRandomPlaceholder();
                        return;
                    }

                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var bmp = Image.FromStream(fs);
                    _img?.Dispose();
                    _img = new Bitmap(bmp);
                }
                catch
                {
                    // best-effort
                    ClearImage();
                }

                try { Invalidate(); } catch { }
            }

            public void ShowRandomPlaceholder()
            {
                try
                {
                    var folder = _folder;
                    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    {
                        ClearImage();
                        return;
                    }

                    var files = Directory.EnumerateFiles(folder)
                        .Where(f =>
                        {
                            var ext = Path.GetExtension(f)?.ToLowerInvariant();
                            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
                        })
                        .ToList();

                    if (files.Count == 0)
                    {
                        ClearImage();
                        return;
                    }

                    var pick = files[_rng.Next(files.Count)];
                    using var fs = new FileStream(pick, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var bmp = Image.FromStream(fs);
                    _img?.Dispose();
                    _img = new Bitmap(bmp);
                }
                catch
                {
                    // best-effort
                }
                try { Invalidate(); } catch { }
            }

            private void ClearImage()
            {
                try { _img?.Dispose(); } catch { }
                _img = null;
                try { Invalidate(); } catch { }
            }

            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // Fondo nero pieno: nasconde il frame quando si pausa.
                e.Graphics.Clear(Color.Black);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                if (_img != null)
                {
                    Rectangle dst;
                    if (UseCoverImage)
                    {
                        double s = Math.Max(Width / (double)_img.Width, Height / (double)_img.Height);
                        int w = Math.Max(1, (int)Math.Round(_img.Width * s));
                        int h = Math.Max(1, (int)Math.Round(_img.Height * s));
                        int x = (Width - w) / 2;
                        int y = (Height - h) / 2;
                        dst = new Rectangle(x, y, w, h);
                    }
                    else
                    {
                        double s = Math.Min(Width / (double)_img.Width, Height / (double)_img.Height);
                        int w = Math.Max(1, (int)Math.Round(_img.Width * s));
                        int h = Math.Max(1, (int)Math.Round(_img.Height * s));
                        int x = (Width - w) / 2;
                        int y = (Height - h) / 2;
                        dst = new Rectangle(x, y, w, h);
                    }

                    g.DrawImage(_img, dst);

                    if (DrawCaptionAlways && !string.IsNullOrWhiteSpace(Caption))
                    {
                        using var f = new Font("Segoe UI", 18, FontStyle.Bold);
                        var txt = Caption;
                        var sz = g.MeasureString(txt, f);
                        using var sh = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                        using var fg = new SolidBrush(Color.FromArgb(235, 235, 235));
                        float cx = (Width - sz.Width) / 2f;
                        float cy = Height * 0.78f;
                        g.DrawString(txt, f, sh, cx + 2, cy + 2);
                        g.DrawString(txt, f, fg, cx, cy);
                    }
                }
                else if (!ShowBranding)
                {
                    using var f = new Font("Segoe UI", 22, FontStyle.Bold);
                    var txt = string.IsNullOrWhiteSpace(Caption) ? "PAUSA" : Caption;
                    var sz = g.MeasureString(txt, f);
                    using var sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                    using var fg = new SolidBrush(Color.FromArgb(235, 235, 235));
                    float cx = (Width - sz.Width) / 2f;
                    float cy = (Height - sz.Height) / 2f;
                    g.DrawString(txt, f, sh, cx + 2, cy + 2);
                    g.DrawString(txt, f, fg, cx, cy);
                }
                else
                {
                    using var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Color.FromArgb(16, 20, 28), Color.FromArgb(4, 6, 10), 16f);
                    g.FillRectangle(bg, new Rectangle(0, 0, Width, Height));

                    using var glowPath = new GraphicsPath();
                    glowPath.AddEllipse(Width - 340, -90, 400, 400);
                    using var glow = new PathGradientBrush(glowPath)
                    {
                        CenterColor = Color.FromArgb(88, 62, 114, 255),
                        SurroundColors = new[] { Color.FromArgb(0, 62, 114, 255) }
                    };
                    g.FillPath(glow, glowPath);
                }

                if (ShowBranding)
                {
                    int bandWidth = Math.Max(360, (int)Math.Round(Width * 0.42));
                    using var grad = new LinearGradientBrush(
                        new Rectangle(0, 0, bandWidth, Height),
                        Color.FromArgb(230, 0, 0, 0),
                        Color.FromArgb(0, 0, 0, 0),
                        0f);
                    g.FillRectangle(grad, new Rectangle(0, 0, bandWidth, Height));

                    float left = Math.Max(54f, Width * 0.065f);
                    float maxTextWidth = Math.Max(220f, Width * 0.32f);
                    using var titleFont = new Font("Segoe UI", Math.Max(22f, Height * 0.050f), FontStyle.Bold, GraphicsUnit.Pixel);
                    using var subFont = new Font("Segoe UI", Math.Max(13f, Height * 0.020f), FontStyle.Regular, GraphicsUnit.Pixel);

                    string title = string.IsNullOrWhiteSpace(TitleText) ? string.Empty : TitleText.Trim();
                    string subtitle = string.IsNullOrWhiteSpace(SubtitleText) ? string.Empty : SubtitleText.Trim();

                    var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };
                    float titleHeight = string.IsNullOrWhiteSpace(title) ? 0f : g.MeasureString(title, titleFont, new SizeF(maxTextWidth, Height), sf).Height;
                    float subtitleHeight = string.IsNullOrWhiteSpace(subtitle) ? 0f : g.MeasureString(subtitle, subFont, new SizeF(maxTextWidth, Height), sf).Height;
                    float logoHeight = (_brandLogo != null) ? Math.Min(48f, Height * 0.09f) : 0f;
                    float gap1 = (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(subtitle)) ? 12f : 0f;
                    float gap2 = (_brandLogo != null && (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(subtitle))) ? 18f : 0f;
                    float total = titleHeight + gap1 + subtitleHeight + gap2 + logoHeight;
                    float y = Math.Max(36f, (Height - total) / 2f);

                    using var titleShadow = new SolidBrush(Color.FromArgb(185, 0, 0, 0));
                    using var titleBrush = new SolidBrush(Color.FromArgb(245, 245, 245));
                    using var subBrush = new SolidBrush(Color.FromArgb(210, 210, 210));

                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        var rect = new RectangleF(left, y, maxTextWidth, Height - y);
                        var shadowRect = rect; shadowRect.Offset(2f, 2f);
                        g.DrawString(title, titleFont, titleShadow, shadowRect, sf);
                        g.DrawString(title, titleFont, titleBrush, rect, sf);
                        y += titleHeight + gap1;
                    }

                    if (!string.IsNullOrWhiteSpace(subtitle))
                    {
                        var rect = new RectangleF(left, y, maxTextWidth, Height - y);
                        g.DrawString(subtitle, subFont, subBrush, rect, sf);
                        y += subtitleHeight + gap2;
                    }

                    if (_brandLogo != null)
                    {
                        float aspect = Math.Max(0.1f, _brandLogo.Width / (float)Math.Max(1, _brandLogo.Height));
                        float h = logoHeight;
                        float w = Math.Min(maxTextWidth * 0.55f, h * aspect);
                        g.DrawImage(_brandLogo, new RectangleF(left, y, w, h));
                    }
                }
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x84;
                const int HTTRANSPARENT = -1;
                if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
                base.WndProc(ref m);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _img?.Dispose(); } catch { }
                    try { _brandLogo?.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }
        }

        // ===== Menu scuro (ContextMenuStrip) =====
        private static readonly ToolStripRenderer _darkMenuRenderer = new DarkMenuRenderer();

        private static void ApplyDarkMenuTheme(ContextMenuStrip? menu)
        {
            if (menu == null) return;
            try
            {
                menu.RenderMode = ToolStripRenderMode.Professional;
                menu.Renderer = _darkMenuRenderer;
                menu.BackColor = Color.FromArgb(26, 26, 26);
                menu.ForeColor = Color.Gainsboro;
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = true;
            }
            catch { }
        }

        private static void ApplyDarkMenuTheme(ToolStripDropDownMenu? menu)
        {
            if (menu == null) return;
            try
            {
                menu.RenderMode = ToolStripRenderMode.Professional;
                menu.Renderer = _darkMenuRenderer;
                menu.BackColor = Color.FromArgb(26, 26, 26);
                menu.ForeColor = Color.Gainsboro;
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = true;
            }
            catch { }
        }

        private static void ApplyDarkMenuThemeRecursive(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripMenuItem mi)
                {
                    try
                    {
                        if (mi.DropDown is ToolStripDropDownMenu dd)
                            ApplyDarkMenuTheme(dd);
                    }
                    catch { }

                    try
                    {
                        if (mi.HasDropDownItems)
                            ApplyDarkMenuThemeRecursive(mi.DropDownItems);
                    }
                    catch { }
                }
            }
        }

        private sealed class DarkMenuColorTable : ProfessionalColorTable
        {
            // sfondo
            public override Color ToolStripDropDownBackground => Color.FromArgb(26, 26, 26);

            // selezione
            public override Color MenuItemSelected => Color.FromArgb(52, 52, 52);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(52, 52, 52);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(52, 52, 52);

            // bordi / separatori
            public override Color MenuItemBorder => Color.FromArgb(72, 72, 72);
            public override Color SeparatorDark => Color.FromArgb(55, 55, 55);
            public override Color SeparatorLight => Color.FromArgb(55, 55, 55);

            // margini immagine (disattivati, ma teniamo coerente)
            public override Color ImageMarginGradientBegin => Color.FromArgb(26, 26, 26);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(26, 26, 26);
            public override Color ImageMarginGradientEnd => Color.FromArgb(26, 26, 26);
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkMenuColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                try { e.Graphics.Clear(Color.FromArgb(26, 26, 26)); }
                catch { base.OnRenderToolStripBackground(e); }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                try
                {
                    var r = new Rectangle(Point.Empty, e.Item.Size);
                    var mi = e.Item as ToolStripMenuItem;

                    bool selected = e.Item.Selected;
                    bool checkedItem = mi?.Checked == true;

                    Color bg = Color.FromArgb(26, 26, 26);
                    if (checkedItem) bg = Color.FromArgb(40, 40, 40);
                    if (selected) bg = Color.FromArgb(52, 52, 52);

                    using var b = new SolidBrush(bg);
                    e.Graphics.FillRectangle(b, r);

                    if (selected)
                    {
                        using var p = new Pen(Color.FromArgb(80, 80, 80));
                        e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
                    }
                }
                catch
                {
                    base.OnRenderMenuItemBackground(e);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Color.Gainsboro : Color.FromArgb(140, 140, 140);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.Enabled ? Color.Gainsboro : Color.FromArgb(140, 140, 140);
                base.OnRenderArrow(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                try
                {
                    int y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
                    using var p = new Pen(Color.FromArgb(55, 55, 55));
                    e.Graphics.DrawLine(p, e.Item.ContentRectangle.Left + 8, y, e.Item.ContentRectangle.Right - 8, y);
                }
                catch
                {
                    base.OnRenderSeparator(e);
                }
            }
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Segoe UI", 10f);
            _menu.Padding = new Padding(6);
            ApplyDarkMenuTheme(_menu);

            // comprime eventuali separatori doppi in base alla visibilità attuale
            void CollapseSeparators()
            {
                if (_menu == null) return;

                bool lastVisibleWasSep = false;

                foreach (ToolStripItem item in _menu.Items)
                {
                    if (item is ToolStripSeparator sep)
                    {
                        // se il precedente VISTO era già un separatore, questo lo nascondo
                        if (lastVisibleWasSep)
                        {
                            sep.Visible = false;
                        }
                        else
                        {
                            sep.Visible = true;
                            lastVisibleWasSep = true;
                        }
                    }
                    else
                    {
                        // reset solo se l’item è visibile
                        if (item.Visible)
                            lastVisibleWasSep = false;
                    }
                }
            }

            // --- FILE ---
            var mOpen = new ToolStripMenuItem("Apri file…", null, (_, __) => OpenFileWithDialog());
            var mOpenLib = new ToolStripMenuItem("Apri libreria…", null, (_, __) => ShowLibrary());

            // --- RIPRODUZIONE ---
            var mPlay = new ToolStripMenuItem("Play / pausa", null, (_, __) => TogglePlayPause());
            var mStop = new ToolStripMenuItem("Chiudi file", null, (_, __) => CloseCurrentToLibrary());
            var mQueue = new ToolStripMenuItem("Coda");
            _mQueueMenuItem = mQueue;
            mQueue.DropDownOpening += (_, __) => PopulatePlaybackQueueMenu(mQueue);
            var mLoopTrack = new ToolStripMenuItem("Loop brano", null, (_, __) =>
            {
                SetSingleTrackLoop(_currentPath, !IsSingleTrackLoopEnabledForPath(_currentPath));
            });
            _mLoopTrack = mLoopTrack;
            var mFull = new ToolStripMenuItem("Schermo intero", null, (_, __) => ToggleFullscreen());

            // --- IMMAGINE / HDR ---
            var mHdr = new ToolStripMenuItem("Immagine / HDR");

            var hAuto = new ToolStripMenuItem("Auto (lascia decidere a madVR)", null, (_, __) =>
            {
                _hdrProfile = HdrUiProfile.Auto;
                _hdr = HDRMode.Auto;
                _lblStatus.Text = "HDR: Auto (let madVR decide)";
                ReopenSame();
            });

            var hPass = new ToolStripMenuItem("Passthrough HDR al display", null, (_, __) =>
            {
                _hdrProfile = HdrUiProfile.Passthrough;
                _hdr = HDRMode.Auto;
                _lblStatus.Text = "HDR: Passthrough (display HDR)";
                ReopenSame();
            });

            var hToneSdr = new ToolStripMenuItem("Tone-map HDR → SDR (pixel shaders)", null, (_, __) =>
            {
                _hdrProfile = HdrUiProfile.ToneMapSdr;
                _hdr = HDRMode.Off;
                _lblStatus.Text = "HDR: Tone-map → SDR (madVR)";
                ReopenSame();
            });

            var hLutSdr = new ToolStripMenuItem("HDR → SDR (via 3DLUT) — avanzato…", null, (_, __) =>
            {
                if (!_lutWarned)
                {
                    _lutWarned = true;
                    MessageBox.Show(
                        "Questo profilo richiede una 3DLUT HDR→SDR configurata in madVR (Devices → calibration).",
                        "3DLUT richiesta", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                _hdrProfile = HdrUiProfile.LutSdr;
                _hdr = HDRMode.Off;
                _lblStatus.Text = "HDR: 3DLUT → SDR (madVR)";
                ReopenSame();
            });

            mHdr.DropDownItems.AddRange(new[] { hAuto, hPass, hToneSdr, hLutSdr });

            mHdr.DropDownOpening += (_, __) =>
            {
                hAuto.Checked = _hdrProfile == HdrUiProfile.Auto;
                hPass.Checked = _hdrProfile == HdrUiProfile.Passthrough;
                hToneSdr.Checked = _hdrProfile == HdrUiProfile.ToneMapSdr;
                hLutSdr.Checked = _hdrProfile == HdrUiProfile.LutSdr;
            };

            // --- 3D ---
            var m3D = new ToolStripMenuItem("3D");

            var m3Native = new ToolStripMenuItem("Nativo (immagine doppia)", null, (_, __) =>
            {
                // niente conversione 3D→2D, immagine così com’è
                Disable3DRestoreRenderer();
            });

            var m3SBS = new ToolStripMenuItem("SBS → 2D (usa EVR)", null, (_, __) =>
            {
                Enable3D(Stereo3DMode.SBS);
            });

            var m3TAB = new ToolStripMenuItem("TAB → 2D (usa EVR)", null, (_, __) =>
            {
                Enable3D(Stereo3DMode.TAB);
            });

            m3D.DropDownItems.AddRange(new[] { m3Native, m3SBS, m3TAB });

            m3D.DropDownOpening += (_, __) =>
            {
                m3Native.Checked = _stereo == Stereo3DMode.None;
                m3SBS.Checked = _stereo == Stereo3DMode.SBS;
                m3TAB.Checked = _stereo == Stereo3DMode.TAB;
            };

            // --- Upscaling (madVR) ---
            var mUpscale = new ToolStripMenuItem("Upscaling (oltre nativo)")
            {
                CheckOnClick = true,
                Checked = _enableUpscaling
            };

            mUpscale.Click += (_, __) =>
            {
                _enableUpscaling = mUpscale.Checked;

                try { _engine?.SetUpscaling(_enableUpscaling); } catch { }
                _lblStatus.Text = "Upscaling: " + (_enableUpscaling ? "ON" : "OFF");

                if (_enableUpscaling && _manualRendererChoice != VRChoice.MADVR)
                {
                    _manualRendererChoice = VRChoice.MADVR;
                    _lblStatus.Text += " • Renderer → madVR";
                    ReopenSame();
                }
                else
                {
                    _hud.ShowOnce(1200);
                }
            };

            // === Risoluzione per flussi web (YouTube) ===
            var mWebRes = new ToolStripMenuItem("Risoluzione web (YouTube)");

            void ApplyYtMax(int maxH, string msg)
            {
                WebMediaResolver.MaxYouTubeHeight = maxH;
                _lblStatus.Text = msg;
                if (IsCurrentYouTube())
                {
                    // defer: lascia chiudere il menu e riduce "impalli" percepiti
                    BeginInvoke(new Action(() => ReopenSame()));
                }
            }

            var qAuto = new ToolStripMenuItem("Auto (nessun limite)", null, (_, __) => ApplyYtMax(0, "YouTube: risoluzione Auto"));
            var q4320 = new ToolStripMenuItem("Limita a 4320p (8K)", null, (_, __) => ApplyYtMax(4320, "YouTube: risoluzione max 4320p"));
            var q2160 = new ToolStripMenuItem("Limita a 2160p (4K)", null, (_, __) => ApplyYtMax(2160, "YouTube: risoluzione max 2160p"));
            var q1440 = new ToolStripMenuItem("Limita a 1440p", null, (_, __) => ApplyYtMax(1440, "YouTube: risoluzione max 1440p"));
            var q1080 = new ToolStripMenuItem("Limita a 1080p", null, (_, __) => ApplyYtMax(1080, "YouTube: risoluzione max 1080p"));
            var q720 = new ToolStripMenuItem("Limita a 720p", null, (_, __) => ApplyYtMax(720, "YouTube: risoluzione max 720p"));
            var q480 = new ToolStripMenuItem("Limita a 480p", null, (_, __) => ApplyYtMax(480, "YouTube: risoluzione max 480p"));
            var q360 = new ToolStripMenuItem("Limita a 360p", null, (_, __) => ApplyYtMax(360, "YouTube: risoluzione max 360p"));
            var q240 = new ToolStripMenuItem("Limita a 240p", null, (_, __) => ApplyYtMax(240, "YouTube: risoluzione max 240p"));
            var q144 = new ToolStripMenuItem("Limita a 144p", null, (_, __) => ApplyYtMax(144, "YouTube: risoluzione max 144p"));

            mWebRes.DropDownItems.AddRange(new ToolStripItem[]
            {
                qAuto,
                new ToolStripSeparator(),
                q4320,
                q2160,
                q1440,
                q1080,
                q720,
                q480,
                q360,
                q240,
                q144
            });

            mWebRes.DropDownOpening += (_, __) =>
            {
                int max = WebMediaResolver.MaxYouTubeHeight;
                qAuto.Checked = (max <= 0);
                q4320.Checked = (max == 4320);
                q2160.Checked = (max == 2160);
                q1440.Checked = (max == 1440);
                q1080.Checked = (max == 1080);
                q720.Checked = (max == 720);
                q480.Checked = (max == 480);
                q360.Checked = (max == 360);
                q240.Checked = (max == 240);
                q144.Checked = (max == 144);
            };

            // --- AUDIO: lingue / sottotitoli / uscita ---
            _mAudioLang = new ToolStripMenuItem("Traccia audio");
            // placeholder per mostrare sempre il triangolino (menu popolato dinamicamente)
            _mAudioLang.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            _mAudioLang.DropDownOpening += (_, __) => PopulateAudioLangMenu();

            _mSubtitles = new ToolStripMenuItem("Sottotitoli");
            // placeholder per mostrare sempre il triangolino (menu popolato dinamicamente)
            _mSubtitles.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            _mSubtitles.DropDownOpening += (_, __) => PopulateSubtitlesMenu();

            _mAudioOut = new ToolStripMenuItem("Uscita audio");
            // placeholder per mostrare sempre il triangolino (menu popolato dinamicamente)
            _mAudioOut.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            _mAudioOut.DropDownOpening += (_, __) => PopulateAudioOutputMenu(_mAudioOut);

            // --- Capitoli (submenu vero, con triangolino) ---
            _mChapters = new ToolStripMenuItem("Capitoli");
            // placeholder per mostrare sempre il triangolino (menu popolato dinamicamente)
            _mChapters.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            _mChapters.DropDownOpening += (_, __) => PopulateChaptersMenu(_mChapters);

            // --- Info overlay ---
            var mShowInfo = new ToolStripMenuItem("Mostra / nascondi info", null,
                (_, __) => { _infoOverlay.Visible = !_infoOverlay.Visible; });

            // --- EXTRA: cinema mode, WLED, placeholder pre-film + demo pre-film ---
            var mExtras = new ToolStripMenuItem("Extra");

            var mCinemaMode = new ToolStripMenuItem("Modalità cinema")
            {
                CheckOnClick = true,
                Checked = _cinemaModeEnabled
            };
            _miCinemaMode = mCinemaMode;
            mCinemaMode.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;
                ApplyCinemaModeFromMenu(mCinemaMode.Checked);
            };

            var mWled = new ToolStripMenuItem("WLED");
            var miWledEnable = new ToolStripMenuItem("Abilita controllo LED")
            {
                CheckOnClick = true,
                Checked = _wledEnabled
            };
            _miWledEnable = miWledEnable;
            miWledEnable.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;

                bool wasEnabled = _wledEnabled;
                _wledEnabled = miWledEnable.Checked;
                try { SaveExtrasConfig(); } catch { }
                RefreshCinemaModeMenuState();

                if (!_wledEnabled)
                {
                    CancelPendingWledPauseRestore();
                    _ = SendWledPowerAsync(true, WLED_FADE_MS);
                }
                else if (!wasEnabled)
                {
                    ApplyAmbientLightingForCurrentState();
                }
            };

            var miWledConfigure = new ToolStripMenuItem("Configura dispositivo…", null,
                (_, __) => BeginInvoke(new Action(() => ConfigureWledFromMenu())));

            mWled.DropDownItems.AddRange(new ToolStripItem[]
            {
                miWledEnable,
                miWledConfigure
            });

            var miPausePlaceholder = new ToolStripMenuItem("Placeholder pre-film")
            {
                CheckOnClick = true,
                Checked = _pausePlaceholderEnabled
            };
            _miPausePlaceholderEnable = miPausePlaceholder;
            miPausePlaceholder.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;

                _pausePlaceholderEnabled = miPausePlaceholder.Checked;
                try { SaveExtrasConfig(); } catch { }
                RefreshCinemaModeMenuState();

                // Nuovo comportamento: il placeholder si usa SOLO come gate pre-film.
                // Se lo disattivo mentre è attivo il gate, lo chiudo.
                try
                {
                    if (!_pausePlaceholderEnabled)
                        HidePreOpenPlaceholderGate(clearPending: true);
                }
                catch { }
            };

            var miPausePlaceholderBackdrop = new ToolStripMenuItem("Usa backdrop automatico TMDb (film/serie TV)")
            {
                CheckOnClick = true,
                Checked = _pausePlaceholderUseTmdbBackdrop
            };
            _miPausePlaceholderUseTmdbBackdrop = miPausePlaceholderBackdrop;
            miPausePlaceholderBackdrop.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;
                _pausePlaceholderUseTmdbBackdrop = miPausePlaceholderBackdrop.Checked;
                try { SaveExtrasConfig(); } catch { }
            };

            var miChoosePausePlaceholder = new ToolStripMenuItem("Scegli placeholder")
            {
                // placeholder per triangolino
            };
            miChoosePausePlaceholder.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            miChoosePausePlaceholder.DropDownOpening += (_, __) => PopulatePausePlaceholderMenu(miChoosePausePlaceholder);

            var miOpenPauseFolder = new ToolStripMenuItem("Apri cartella placeholder…", null,
                (_, __) => OpenFolderInExplorer(_pausePlaceholderFolder));

            var mPreRoll = new ToolStripMenuItem("Demo pre-film");
            var miPreRollEnable = new ToolStripMenuItem("Abilita")
            {
                CheckOnClick = true,
                Checked = _preRollEnabled
            };
            _miPreRollEnable = miPreRollEnable;
            miPreRollEnable.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;
                _preRollEnabled = miPreRollEnable.Checked;
                try { SaveExtrasConfig(); } catch { }
                RefreshCinemaModeMenuState();
            };

            var miPreRollChoose = new ToolStripMenuItem("Scegli demo")
            {
                // placeholder per triangolino
            };
            miPreRollChoose.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            miPreRollChoose.DropDownOpening += (_, __) => PopulatePreRollDemoMenu(miPreRollChoose);

            var miOpenDemoFolder = new ToolStripMenuItem("Apri cartella demo…", null,
                (_, __) => OpenFolderInExplorer(_preRollDemoFolder));

            mPreRoll.DropDownItems.AddRange(new ToolStripItem[]
            {
                miPreRollEnable,
                miPreRollChoose,
                new ToolStripSeparator(),
                miOpenDemoFolder
            });

            mExtras.DropDownItems.AddRange(new ToolStripItem[]
            {
                mWled,
                new ToolStripSeparator(),
                miPausePlaceholder,
                miPausePlaceholderBackdrop,
                miChoosePausePlaceholder,
                miOpenPauseFolder,
                new ToolStripSeparator(),
                mPreRoll
            });

            // --- Renderer video ---
            var mRenderer = new ToolStripMenuItem("Renderer video");
            void SetRenderer(VRChoice? c)
            {
                if (_stereo != Stereo3DMode.None && c != VRChoice.EVR)
                {
                    _hasSavedRendererFor3D = true;
                    _savedRendererFor3D = c;
                    _lblStatus.Text = "3D→2D attivo: EVR obbligatorio. Preferenza renderer memorizzata per dopo.";
                    _hud.ShowOnce(1400);
                    return;
                }

                _manualRendererChoice = c;
                _lblStatus.Text = c.HasValue ? $"Renderer video: {c}" : "Renderer video: Auto";

                if (c.HasValue && c.Value != VRChoice.MADVR)
                {
                    _enableUpscaling = false;
                    try { _engine?.SetUpscaling(false); } catch { }
                }

                ReopenSame();
            }

            var mPcmPref = new ToolStripMenuItem("Preferenza uscita audio");
            var miPcmAuto = new ToolStripMenuItem("Auto (bitstream se conviene)", null, (_, __) =>
            {
                _audioOutPref = AudioOutPref.Auto;
                _lblStatus.Text = "Uscita: Auto (bitstream se conviene)";
                ReopenSame();
            });
            var miPcmForce = new ToolStripMenuItem("Forza PCM (disabilita bitstream)", null, (_, __) =>
            {
                _audioOutPref = AudioOutPref.ForcePcm;
                _lblStatus.Text = "Uscita: Forza PCM";
                ReopenSame();
            });
            mPcmPref.DropDownItems.AddRange(new[] { miPcmAuto, miPcmForce });
            mPcmPref.DropDownOpening += (_, __) =>
            {
                miPcmAuto.Checked = _audioOutPref == AudioOutPref.Auto;
                miPcmForce.Checked = _audioOutPref == AudioOutPref.ForcePcm;
            };

            var miMadvr = new ToolStripMenuItem("madVR", null, (_, __) => SetRenderer(VRChoice.MADVR));
            var miMpcvr = new ToolStripMenuItem("MPCVR", null, (_, __) => SetRenderer(VRChoice.MPCVR));
            var miEvr = new ToolStripMenuItem("EVR", null, (_, __) => SetRenderer(VRChoice.EVR));
            var miAuto = new ToolStripMenuItem("Auto (ordine preferito)", null, (_, __) => SetRenderer(null));

            mRenderer.DropDownItems.AddRange(new ToolStripItem[]
            {
                miMadvr, miMpcvr, miEvr, new ToolStripSeparator(), miAuto
            });
            mRenderer.DropDownOpening += (_, __) =>
            {
                miMadvr.Checked = _manualRendererChoice == VideoRendererChoice.MADVR;
                miMpcvr.Checked = _manualRendererChoice == VideoRendererChoice.MPCVR;
                miEvr.Checked = _manualRendererChoice == VideoRendererChoice.EVR;
                miAuto.Checked = _manualRendererChoice == null;
            };

            // --- Telecomando web ---
            var mShowPin = new ToolStripMenuItem("Telecomando (mostra PIN)", null, (_, __) =>
            {
                if (_remote != null)
                    ShowPairingBanner(_remote.CurrentPin);
            });

            // --- Menu più ordinato: azioni rapide in alto, avanzate nei sottomenu ---
            var mSettings = new ToolStripMenuItem("Impostazioni…", null, (_, __) => ShowSettingsModal());

            var mVideoRoot = new ToolStripMenuItem("Video");
            mVideoRoot.DropDownItems.AddRange(new ToolStripItem[]
            {
                m3D,
                mHdr,
                mUpscale,
                new ToolStripSeparator(),
                mRenderer,
                mWebRes
            });

            var mAudioRoot = new ToolStripMenuItem("Audio");
            mAudioRoot.DropDownItems.AddRange(new ToolStripItem[]
            {
                _mAudioLang,
                _mSubtitles,
                new ToolStripSeparator(),
                _mAudioOut,
                mPcmPref
            });

            var mRemoteRoot = new ToolStripMenuItem("Telecomando");
            mRemoteRoot.DropDownItems.Add(mShowPin);

            var mExitApp = new ToolStripMenuItem("Esci dal programma", null, (_, __) => BeginInvoke(new Action(Close)));

            _menu.Items.AddRange(new ToolStripItem[]
            {
                // Apertura
                mOpen,
                mOpenLib,
                new ToolStripSeparator(),

                // Riproduzione / cinema
                mPlay,
                mStop,
                mQueue,
                mLoopTrack,
                mFull,
                mCinemaMode,
                mShowInfo,
                new ToolStripSeparator(),

                // Contenuto / avanzate
                _mChapters,
                mAudioRoot,
                mVideoRoot,
                mExtras,
                mRemoteRoot,
                new ToolStripSeparator(),

                // App
                mSettings,
                mExitApp
            });

            // sincronia check stato upscaling + cleanup separatori
            _menu.Opening += (_, e) =>
            {
                // il controllo "loading" viene già gestito anche nel costruttore,
                // ma lo ripetiamo qui per sicurezza
                if (_loading.Visible)
                {
                    e.Cancel = true;
                    return;
                }

                mUpscale.Checked = _enableUpscaling;

                bool hasEngine = _engine != null;
                bool canStartPending = _preOpenPlaceholderGateActive;

                mPlay.Text = canStartPending ? "Avvia film" : (_paused ? "Riprendi" : "Pausa");
                mStop.Text = canStartPending ? "Annulla avvio" : "Chiudi file";
                mFull.Text = FormBorderStyle == FormBorderStyle.None ? "Esci da schermo intero" : "Schermo intero";
                mShowInfo.Text = _infoOverlay.Visible ? "Nascondi info" : "Mostra info";
                RefreshCinemaModeMenuState();

                // Azioni rapide: abilitate se c'è playback oppure placeholder gate attivo
                mPlay.Enabled = hasEngine || canStartPending;
                mStop.Enabled = hasEngine || canStartPending;
                mQueue.Enabled = true;
                mFull.Enabled = true;
                mShowInfo.Enabled = hasEngine;

                bool canToggleTrackLoop = hasEngine && !_currentMediaHasVideo && !string.IsNullOrWhiteSpace(_currentPath);
                mLoopTrack.Visible = canToggleTrackLoop;
                mLoopTrack.Enabled = canToggleTrackLoop;
                mLoopTrack.Checked = canToggleTrackLoop && IsSingleTrackLoopEnabledForPath(_currentPath);

                mVideoRoot.Enabled = hasEngine;
                mAudioRoot.Enabled = hasEngine;
                _mChapters.Enabled = hasEngine;

                // assicura visibilità corretta delle voci dipendenti dal media
                RefreshMenuVisibility();

                // e poi elimina eventuali linee doppie
                CollapseSeparators();
            };

            // Assicura che anche i dropdown (sottomenu) ereditino il tema scuro.
            try { ApplyDarkMenuThemeRecursive(_menu.Items); } catch { }
        }

        private void RefreshMenuVisibility()
        {
            if (_menu == null) return;

            bool hasEngine = _engine != null;
            bool hasInfo = _info != null;

            // MOSTRA SEMPRE se c’è un engine: evita che “sparisca” la voce Lingua
            if (_mAudioLang != null) _mAudioLang.Visible = hasEngine;
            if (_mSubtitles != null) _mSubtitles.Visible = hasEngine;

            if (_mChapters != null)
                _mChapters.Visible = hasInfo && _info!.Chapters.Count > 0;
            if (_mLoopTrack != null)
                _mLoopTrack.Visible = hasEngine && !_currentMediaHasVideo && !string.IsNullOrWhiteSpace(_currentPath);
        }

        private void PopulateChaptersMenu(ToolStripMenuItem root)
        {
            root.DropDownItems.Clear();

            if (_info == null || _info.Chapters.Count == 0)
            {
                var empty = new ToolStripMenuItem("Nessun capitolo") { Enabled = false };
                root.DropDownItems.Add(empty);
                return;
            }

            foreach (var (title, start) in _info.Chapters)
            {
                var text = $"{Fmt(start)}  {title}";
                double s = start;
                var it = new ToolStripMenuItem(text);
                it.Click += (_, __) =>
                {
                    if (_engine != null)
                        _engine.PositionSeconds = s;
                    _hud.ShowOnce(1200);
                };
                root.DropDownItems.Add(it);
            }
        }

        // ======= Uscita audio raggruppata =======
        private void PopulateAudioOutputMenu(ToolStripMenuItem root)
        {
            root.DropDownItems.Clear();

            List<DsDevice> all;
            try { all = DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory).ToList(); }
            catch { all = new List<DsDevice>(); }

            var grpDefault = new ToolStripMenuItem("Predefinito di sistema");
            var grpWasapi = new ToolStripMenuItem("WASAPI");
            var grpDs = new ToolStripMenuItem("DirectSound");
            var grpMpc = new ToolStripMenuItem("MPC Audio Renderer");

            foreach (var dev in all.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ToolStripMenuItem(dev.Name)
                {
                    Checked = string.Equals(dev.Name, _selectedAudioRendererName, StringComparison.OrdinalIgnoreCase)
                };
                string captured = dev.Name;
                item.Click += (_, __) =>
                {
                    _selectedAudioRendererName = captured;
                    _selectedRendererLooksHdmi = LooksHdmi(captured);
                    _lblStatus.Text = "Uscita audio: " + captured;
                    ReopenSame();
                };

                var nl = dev.Name.ToLowerInvariant();
                if (nl.Contains("mpc audio renderer")) grpMpc.DropDownItems.Add(item);
                else if (nl.Contains("wasapi")) grpWasapi.DropDownItems.Add(item);
                else if (nl.Contains("directsound")) grpDs.DropDownItems.Add(item);
                else grpDefault.DropDownItems.Add(item);
            }

            void sortItems(ToolStripMenuItem m)
            {
                var list = m.DropDownItems.OfType<ToolStripMenuItem>()
                    .OrderBy(i => i.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
                m.DropDownItems.Clear();
                foreach (var it in list) m.DropDownItems.Add(it);
            }
            sortItems(grpDefault);
            sortItems(grpWasapi);
            sortItems(grpDs);
            sortItems(grpMpc);

            if (grpDefault.DropDownItems.Count == 0)
            {
                var miDefault = new ToolStripMenuItem("Usa dispositivo predefinito") { Checked = string.IsNullOrWhiteSpace(_selectedAudioRendererName) };
                miDefault.Click += (_, __) =>
                {
                    _selectedAudioRendererName = null;
                    _selectedRendererLooksHdmi = false;
                    _lblStatus.Text = "Uscita audio: predefinito di sistema";
                    ReopenSame();
                };
                grpDefault.DropDownItems.Add(miDefault);
            }

            if (grpDefault.DropDownItems.Count > 0) root.DropDownItems.Add(grpDefault);
            if (grpWasapi.DropDownItems.Count > 0) root.DropDownItems.Add(grpWasapi);
            if (grpDs.DropDownItems.Count > 0) root.DropDownItems.Add(grpDs);
            if (grpMpc.DropDownItems.Count > 0) root.DropDownItems.Add(grpMpc);
        }

        private static bool LooksHdmi(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            string[] hdmi = { "hdmi", "display audio", "avr", "denon", "marantz", "onkyo", "yamaha", "nvidia high definition audio", "intel(r) display audio", "amd high definition audio" };
            return hdmi.Any(n.Contains);
        }

        private static string? ExtractOriginalVideoName(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;

            try
            {
                // File URI (file:///C:/...)
                if (Uri.TryCreate(requested, UriKind.Absolute, out var u) && u.IsFile)
                {
                    var lp = u.LocalPath;
                    return string.IsNullOrWhiteSpace(lp) ? requested : Path.GetFileName(lp);
                }
            }
            catch { }

            try
            {
                if (File.Exists(requested)) return Path.GetFileName(requested);
            }
            catch { }

            // Best-effort per YouTube: video id
            try
            {
                if (Uri.TryCreate(requested, UriKind.Absolute, out var u2))
                {
                    var host = (u2.Host ?? "").ToLowerInvariant();
                    if (host.Contains("youtu.be"))
                    {
                        var seg = u2.AbsolutePath.Trim('/');
                        if (!string.IsNullOrWhiteSpace(seg)) return seg;
                    }
                    if (host.Contains("youtube.com"))
                    {
                        var v = TryGetQueryParam(u2.Query, "v");
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                }
            }
            catch { }

            // fallback: l'ultima parte (o l'intera stringa)
            try
            {
                return Path.GetFileName(requested.TrimEnd('/'));
            }
            catch
            {
                return requested;
            }
        }

        private static string NormalizeDisplayTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string s = raw.Trim();

            // URL: evita di mostrare query/parametri. Per YouTube, meglio un id (se disponibile).
            try
            {
                if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    var host = (u.Host ?? string.Empty).Trim();
                    var h = host.ToLowerInvariant();
                    if (h.Contains("youtube.com") || h.Contains("youtu.be"))
                    {
                        var id = ExtractOriginalVideoName(s);
                        if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, "watch", StringComparison.OrdinalIgnoreCase))
                            return id.Trim();
                        return "YouTube";
                    }

                    if (!string.IsNullOrWhiteSpace(host))
                        return host;
                }
            }
            catch { }

            // File / path / nome file
            string name = s;
            try
            {
                if (Uri.TryCreate(s, UriKind.Absolute, out var uf) && uf.IsFile)
                    name = uf.LocalPath;

                // Rimuovi estensione, lascia solo nome
                name = Path.GetFileNameWithoutExtension(name);
            }
            catch { name = s; }

            if (string.IsNullOrWhiteSpace(name)) name = s;

            // Normalizzazione base: punti/underscore → spazi, collassa spazi multipli.
            name = name.Replace('_', ' ').Replace('.', ' ');

            try { name = Regex.Replace(name, @"\s+", " ").Trim(); }
            catch { name = name.Trim(); }

            return name;
        }

        private static string? TryGetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            if (query.Length > 0 && query[0] == '?') query = query.Substring(1);
            foreach (var part in query.Split('&'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                var kv = part.Split('=');
                if (kv.Length == 0) continue;
                var k = Uri.UnescapeDataString(kv[0] ?? "");
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                var val = kv.Length > 1 ? kv[1] : "";
                return Uri.UnescapeDataString(val ?? "");
            }
            return null;
        }
        private void PopulateAudioLangMenu()
        {
            _mAudioLang.DropDownItems.Clear();

            if (_engine == null)
            {
                var it = new ToolStripMenuItem("(nessuna traccia)") { Enabled = false };
                _mAudioLang.DropDownItems.Add(it);
                return;
            }

            var streams = _engine.EnumerateStreams().Where(s => s.IsAudio).ToList();
            if (streams.Count == 0)
            {
                var it = new ToolStripMenuItem("(nessuna traccia)") { Enabled = false };
                _mAudioLang.DropDownItems.Add(it);
                return;
            }

            int ordinal = 0;
            foreach (var s in streams)
            {
                ordinal++;
                var name = SubtitleNameNormalizer.NormalizeSubtitleTrackName(s.Name, ordinal);
                var it = new ToolStripMenuItem(name) { Checked = s.Selected };
                int idx = s.GlobalIndex;
                string? audioLangKey = DetectLangKeyFromName(s.Name);

                it.Click += (_, __) =>
                {
                    if (_engine == null) return;

                    _engine.EnableByGlobalIndex(idx);

                    if (!string.IsNullOrWhiteSpace(audioLangKey))
                        _preferredSubtitleLangKey = audioLangKey;

                    if (_subtitleAutoForcedMode)
                    {
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (_engine == null) return;
                                    var latestSubs = _engine.EnumerateStreams().Where(x => x.IsSubtitle).ToList();
                                    if (!TrySelectAutoForcedSubtitles(_engine, latestSubs, ResolvePreferredAutoForcedLangKey(latestSubs)))
                                        _subtitleAutoForcedMode = false;
                                }
                                catch { }
                            }));
                        }
                        catch { }
                    }

                    _lblStatus.Text = $"Audio: {name}";
                    _hud.ShowOnce(1200);
                    if (_info != null)
                    {
                        var r = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                        UpdateInfoOverlay(r, _info.IsHdr);
                    }
                };
                _mAudioLang.DropDownItems.Add(it);
            }
        }

        private void PopulateSubtitlesMenu()
        {
            _mSubtitles.DropDownItems.Clear();

            if (_engine == null)
            {
                var it = new ToolStripMenuItem("(nessuna traccia)") { Enabled = false };
                _mSubtitles.DropDownItems.Add(it);
                return;
            }

            var streams = _engine.EnumerateStreams().Where(s => s.IsSubtitle).ToList();
            if (streams.Count == 0)
            {
                _subtitleAutoForcedMode = false;
                var it = new ToolStripMenuItem("(nessuna traccia)") { Enabled = false };
                _mSubtitles.DropDownItems.Add(it);
                return;
            }

            bool selectedAutoForced = streams.Any(s => s.Selected && IsAutoForcedSubtitleName(s.Name));
            _subtitleAutoForcedMode = selectedAutoForced;

            if (string.IsNullOrWhiteSpace(_preferredSubtitleLangKey))
                _preferredSubtitleLangKey = ResolvePreferredAutoForcedLangKey(streams);

            var off = new ToolStripMenuItem("Disattiva (Auto Forced)")
            {
                Checked = selectedAutoForced
            };
            off.Click += (_, __) =>
            {
                if (_engine == null) return;

                var preferredLang = ResolvePreferredAutoForcedLangKey(streams);
                _subtitleAutoForcedMode = TrySelectAutoForcedSubtitles(_engine, streams, preferredLang);
                if (_subtitleAutoForcedMode)
                {
                    _preferredSubtitleLangKey = preferredLang;
                    _lblStatus.Text = "Sottotitoli: disattivati";
                }
                else
                {
                    _lblStatus.Text = "Sottotitoli: Auto Forced non disponibile";
                }

                _hud.ShowOnce(1200);
            };
            _mSubtitles.DropDownItems.Add(off);
            _mSubtitles.DropDownItems.Add(new ToolStripSeparator());

            var selectable = streams
                .Where(s => !IsAutoForcedSubtitleName(s.Name))
                .ToList();

            if (selectable.Count == 0)
            {
                var it0 = new ToolStripMenuItem("(nessuna traccia selezionabile)") { Enabled = false };
                _mSubtitles.DropDownItems.Add(it0);
                return;
            }

            var groups = selectable
                .GroupBy(s =>
                        DetectLangKeyFromName(s.Name)
                        ?? SubtitleNameNormalizer.TryDetectLanguageLabel(s.Name)
                        ?? (s.Name ?? ""),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            groups.Sort((a, b) =>
            {
                int Rank(string k)
                {
                    if (string.Equals(k, "it", StringComparison.OrdinalIgnoreCase)) return 0;
                    if (string.Equals(k, "en", StringComparison.OrdinalIgnoreCase)) return 1;
                    return 9;
                }

                int ra = Rank(a.Key);
                int rb = Rank(b.Key);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            int ordinal = 0;
            foreach (var g in groups)
            {
                ordinal++;

                var best = g
                    .OrderByDescending(s => SubtitleMenuScore(s))
                    .FirstOrDefault();
                if (best == null) continue;

                var key = DetectLangKeyFromName(best.Name);
                string label;

                if (!string.IsNullOrWhiteSpace(key))
                {
                    label = LanguageLabelFromKey(key!);
                }
                else
                {
                    label = SubtitleNameNormalizer.TryDetectLanguageLabel(best.Name)
                            ?? SubtitleNameNormalizer.NormalizeSubtitleTrackName(best.Name, ordinal);

                    int bullet = label.IndexOf('•');
                    if (bullet >= 0) label = label.Substring(0, bullet).Trim();
                    label = label.Trim(' ', '.', '-', '_');
                }

                if (IsAutoForcedSubtitleName(best.Name))
                    label += " (Auto Forced)";

                bool anySelected = g.Any(s => s.Selected);
                var it = new ToolStripMenuItem(label) { Checked = anySelected };
                int idx = best.GlobalIndex;

                it.Click += (_, __) =>
                {
                    _engine?.EnableByGlobalIndex(idx);
                    _subtitleAutoForcedMode = false;

                    var lk = DetectLangKeyFromName(best.Name);
                    if (!string.IsNullOrWhiteSpace(lk))
                        _preferredSubtitleLangKey = lk;

                    _lblStatus.Text = $"Sottotitoli: {label}";
                    _hud.ShowOnce(1200);
                };

                _mSubtitles.DropDownItems.Add(it);
            }
        }

        // ===== Subtitles helpers (Auto Forced = OFF) =====
        private static bool IsAutoForcedSubtitleName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return Regex.IsMatch(name, @"\bforced\b|\bforzat[oi]\b|\bforz\b", RegexOptions.IgnoreCase);
        }

        private string? GetSelectedAudioLangKey()
        {
            try
            {
                if (_engine == null) return null;
                var sel = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
                return DetectLangKeyFromName(sel?.Name);
            }
            catch
            {
                return null;
            }
        }

        private string? ResolvePreferredAutoForcedLangKey(List<DsStreamItem>? subtitleStreams = null)
        {
            var audioKey = GetSelectedAudioLangKey();
            if (!string.IsNullOrWhiteSpace(audioKey))
                return audioKey;

            if (!string.IsNullOrWhiteSpace(_preferredSubtitleLangKey))
                return _preferredSubtitleLangKey;

            try
            {
                var streams = subtitleStreams ?? _engine?.EnumerateStreams().Where(s => s.IsSubtitle).ToList();
                var curSel = streams?.FirstOrDefault(s => s.Selected && !IsAutoForcedSubtitleName(s.Name))
                          ?? streams?.FirstOrDefault(s => s.Selected);
                return DetectLangKeyFromName(curSel?.Name);
            }
            catch
            {
                return null;
            }
        }

        // Ritorna un key semplice per la lingua (it/en/…)
        private static string? DetectLangKeyFromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var normalized = SubtitleNameNormalizer.TryDetectLanguageKey(name);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;

            var s = name.ToLowerInvariant();

            if (Regex.IsMatch(s, @"\b(it|ita|italian|italiano)\b", RegexOptions.IgnoreCase)) return "it";
            if (Regex.IsMatch(s, @"\b(en|eng|english|inglese)\b", RegexOptions.IgnoreCase)) return "en";
            if (Regex.IsMatch(s, @"\b(es|spa|spanish|spagnolo|espanol|español)\b", RegexOptions.IgnoreCase)) return "es";
            if (Regex.IsMatch(s, @"\b(fr|fra|fre|french|francese|français|francais)\b", RegexOptions.IgnoreCase)) return "fr";
            if (Regex.IsMatch(s, @"\b(de|ger|deu|german|tedesco|deutsch)\b", RegexOptions.IgnoreCase)) return "de";
            if (Regex.IsMatch(s, @"\b(pt|por|portuguese|portoghese)\b", RegexOptions.IgnoreCase)) return "pt";
            if (Regex.IsMatch(s, @"\b(ru|rus|russian|russo)\b", RegexOptions.IgnoreCase)) return "ru";
            if (Regex.IsMatch(s, @"\b(ja|jpn|japanese|giapponese)\b", RegexOptions.IgnoreCase)) return "ja";
            if (Regex.IsMatch(s, @"\b(zh|chi|zho|chinese|cinese)\b", RegexOptions.IgnoreCase)) return "zh";
            return null;
        }

        private static string LanguageLabelFromKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "Sottotitoli";
            key = key.Trim().ToLowerInvariant();
            return key switch
            {
                "it" => "Italiano",
                "en" => "Inglese",
                "es" => "Spagnolo",
                "fr" => "Francese",
                "de" => "Tedesco",
                "pt" => "Portoghese",
                "ru" => "Russo",
                "ja" => "Giapponese",
                "zh" => "Cinese",
                _ => key.ToUpperInvariant()
            };
        }

        private static int SubtitleMenuScore(DsStreamItem s)
        {
            int score = 0;
            if (s.Selected) score += 10_000;
            var name = (s.Name ?? "").ToLowerInvariant();
            if (name.Contains("sdh") || name.Contains("hearing") || name.Contains("hi")) score -= 50;
            if (name.Contains("comment")) score -= 20;
            if (name.Contains("forced")) score -= 10;
            score -= Math.Min(200, name.Length / 2);
            return score;
        }

        private static bool TrySelectAutoForcedSubtitles(IPlaybackEngine engine, List<DsStreamItem> subtitleStreams, string? preferredLangKey)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(preferredLangKey))
                {
                    var best = subtitleStreams.FirstOrDefault(s =>
                        IsAutoForcedSubtitleName(s.Name) && string.Equals(DetectLangKeyFromName(s.Name), preferredLangKey, StringComparison.OrdinalIgnoreCase));
                    if (best != null)
                    {
                        engine.EnableByGlobalIndex(best.GlobalIndex);
                        return true;
                    }
                }

                var any = subtitleStreams.FirstOrDefault(s => IsAutoForcedSubtitleName(s.Name));
                if (any != null)
                {
                    engine.EnableByGlobalIndex(any.GlobalIndex);
                    return true;
                }

                return engine.DisableSubtitlesIfPossible();
            }
            catch
            {
                return false;
            }
        }

        // ===== Extras persistence =====
        private sealed class ExtrasConfig
        {
            public bool PausePlaceholderEnabled { get; set; }
            public string? PausePlaceholderFile { get; set; }
            public bool PausePlaceholderUseTmdbBackdrop { get; set; }
            public bool PreRollEnabled { get; set; }
            public string? PreRollDemoFile { get; set; }
            public bool WledEnabled { get; set; }
            public string? WledBaseUrl { get; set; }
            public bool CinemaModeEnabled { get; set; }
        }

        private string ExtrasConfigPath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
                return Path.Combine(dir, "extras.json");
            }
        }

        private void LoadExtrasConfig()
        {
            try
            {
                if (!File.Exists(ExtrasConfigPath)) return;
                var json = File.ReadAllText(ExtrasConfigPath);
                var cfg = JsonSerializer.Deserialize<ExtrasConfig>(json);
                if (cfg == null) return;

                _pausePlaceholderEnabled = cfg.PausePlaceholderEnabled;
                _pausePlaceholderPath = ResolvePausePlaceholderPath(cfg.PausePlaceholderFile);
                _pausePlaceholderUseTmdbBackdrop = cfg.PausePlaceholderUseTmdbBackdrop;
                _preRollEnabled = cfg.PreRollEnabled;
                _preRollDemoPath = ResolveDemoPath(cfg.PreRollDemoFile);
                _wledEnabled = cfg.WledEnabled;
                _wledBaseUrl = NormalizeWledBaseUrl(cfg.WledBaseUrl) ?? _wledBaseUrl;
                _cinemaModeEnabled = cfg.CinemaModeEnabled || (_wledEnabled && _pausePlaceholderEnabled && _preRollEnabled);
            }
            catch
            {
                // ignore
            }
        }

        private void SaveExtrasConfig()
        {
            string? tempFile = null;
            try
            {
                var dir = Path.GetDirectoryName(ExtrasConfigPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                var cfg = new ExtrasConfig
                {
                    PausePlaceholderEnabled = _pausePlaceholderEnabled,
                    PausePlaceholderFile = _pausePlaceholderPath != null ? Path.GetFileName(_pausePlaceholderPath) : null,
                    PausePlaceholderUseTmdbBackdrop = _pausePlaceholderUseTmdbBackdrop,
                    PreRollEnabled = _preRollEnabled,
                    PreRollDemoFile = _preRollDemoPath != null ? Path.GetFileName(_preRollDemoPath) : null,
                    WledEnabled = _wledEnabled,
                    WledBaseUrl = _wledBaseUrl,
                    CinemaModeEnabled = _cinemaModeEnabled
                };
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });

                tempFile = ExtrasConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tempFile, json, new UTF8Encoding(false));

                if (File.Exists(ExtrasConfigPath))
                    File.Replace(tempFile, ExtrasConfigPath, null, true);
                else
                    File.Move(tempFile, ExtrasConfigPath);
            }
            catch
            {
                // ignore
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempFile))
                {
                    try
                    {
                        if (File.Exists(tempFile))
                            File.Delete(tempFile);
                    }
                    catch { }
                }
            }
        }


        private void RefreshCinemaModeMenuState()
        {
            bool cinema = _wledEnabled && _pausePlaceholderEnabled && _preRollEnabled;
            _cinemaModeEnabled = cinema;

            _syncingCinemaModeUi = true;
            try
            {
                if (_miCinemaMode != null) _miCinemaMode.Checked = cinema;
                if (_miWledEnable != null) _miWledEnable.Checked = _wledEnabled;
                if (_miPausePlaceholderEnable != null) _miPausePlaceholderEnable.Checked = _pausePlaceholderEnabled;
                if (_miPreRollEnable != null) _miPreRollEnable.Checked = _preRollEnabled;
                if (_miPausePlaceholderUseTmdbBackdrop != null) _miPausePlaceholderUseTmdbBackdrop.Checked = _pausePlaceholderUseTmdbBackdrop;
            }
            finally
            {
                _syncingCinemaModeUi = false;
            }
        }

        private void ApplyCinemaModeFromMenu(bool enabled)
        {
            _cinemaModeEnabled = enabled;
            _wledEnabled = enabled;
            _pausePlaceholderEnabled = enabled;
            _preRollEnabled = enabled;

            if (!enabled)
            {
                bool startedPendingFromGate = false;
                try
                {
                    if (_preOpenPlaceholderGateActive)
                        startedPendingFromGate = TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: false);
                }
                catch { }

                try { CancelPlaceholderBackdropFetch(); } catch { }
                if (!startedPendingFromGate)
                {
                    try { HidePreOpenPlaceholderGate(clearPending: true); } catch { }
                }

                try { HidePausePlaceholderNow(); } catch { }
                TrySkipActivePreRollToMainContent();
                CancelPendingWledPauseRestore();
                _ = SendWledPowerAsync(true, WLED_FADE_MS);
            }
            else
            {
                ApplyAmbientLightingForCurrentState();
            }

            RefreshCinemaModeMenuState();
            try { SaveExtrasConfig(); } catch { }
        }

        private void TrySkipActivePreRollToMainContent()
        {
            try
            {
                if (!_playingPreRoll || string.IsNullOrWhiteSpace(_pendingMainPathAfterPreRoll))
                    return;

                var next = _pendingMainPathAfterPreRoll;
                var nextResume = _pendingMainResumeAfterPreRoll;
                var nextPaused = _pendingMainStartPausedAfterPreRoll;

                _pendingMainPathAfterPreRoll = null;
                _pendingMainResumeAfterPreRoll = 0;
                _pendingMainStartPausedAfterPreRoll = false;
                _playingPreRoll = false;
                _suppressPreRollOnce = true;
                _suppressVideoLoadingOnce = true;

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { OpenPath(next!, nextResume, nextPaused, allowPlaceholderGate: false); } catch { }
                    }));
                }
                catch { }
            }
            catch { }
        }

        private string? NormalizeWledBaseUrl(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                s = "http://" + s;
            }
            return s.TrimEnd('/');
        }

        private void ConfigureWledFromMenu()
        {
            try
            {
                string current = NormalizeWledBaseUrl(_wledBaseUrl) ?? "http://wled.local";
                string? value = PromptForTextValue(
                    "Configura WLED",
                    "Inserisci host o URL del dispositivo WLED (es. http://wled.local o 192.168.1.50)",
                    current);
                if (value == null) return;

                string? normalized = NormalizeWledBaseUrl(value);
                if (string.IsNullOrWhiteSpace(normalized)) return;

                _wledBaseUrl = normalized;
                try { SaveExtrasConfig(); } catch { }
                _lblStatus.Text = "WLED: " + _wledBaseUrl;

                if (_wledEnabled)
                    ApplyAmbientLightingForCurrentState();
            }
            catch { }
        }

        private string? PromptForTextValue(string title, string label, string initialValue)
        {
            using var form = new Form
            {
                Text = title,
                StartPosition = (FormBorderStyle == FormBorderStyle.None) ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                ClientSize = new Size(560, 132)
            };

            var lbl = new Label
            {
                Left = 12,
                Top = 12,
                Width = 536,
                Height = 28,
                Text = label
            };

            var tb = new TextBox
            {
                Left = 12,
                Top = 42,
                Width = 536,
                Text = initialValue ?? string.Empty
            };

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Width = 86,
                Left = 370,
                Top = 86
            };
            var cancel = new Button
            {
                Text = "Annulla",
                DialogResult = DialogResult.Cancel,
                Width = 92,
                Left = 462,
                Top = 86
            };

            form.Controls.Add(lbl);
            form.Controls.Add(tb);
            form.Controls.Add(ok);
            form.Controls.Add(cancel);
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            bool restoreTopMost = false;
            bool prevTopMost = false;
            bool suspendKeepAlive = false;
            try
            {
                if (FormBorderStyle == FormBorderStyle.None)
                {
                    _suspendFullscreenActivationKeepAlive++;
                    suspendKeepAlive = true;
                }

                if (FormBorderStyle == FormBorderStyle.None && TopMost)
                {
                    restoreTopMost = true;
                    prevTopMost = TopMost;
                    TopMost = false;
                    try { _overlayHost.TopMost = false; } catch { }
                }

                var result = form.ShowDialog(this);
                return result == DialogResult.OK ? tb.Text.Trim() : null;
            }
            finally
            {
                if (restoreTopMost)
                {
                    try { TopMost = prevTopMost; } catch { }
                    try { _overlayHost.TopMost = prevTopMost; } catch { }
                    try { Activate(); } catch { }
                }

                if (suspendKeepAlive && _suspendFullscreenActivationKeepAlive > 0)
                    _suspendFullscreenActivationKeepAlive--;
            }
        }

        private void CancelPendingWledPauseRestore()
        {
            var old = Interlocked.Exchange(ref _wledPauseRestoreCts, null);
            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }
        }

        private void CancelPendingWledTransition()
        {
            var old = Interlocked.Exchange(ref _wledTransitionCts, null);
            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }
        }

        private static int ClampWledBrightness(int bri)
            => Math.Max(WLED_MIN_BRI, Math.Min(WLED_DEFAULT_BRI, bri));

        private void RememberWorkingWledBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return;

            if (!string.Equals(_wledBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                _wledBaseUrl = baseUrl;
                try { SaveExtrasConfig(); } catch { }
            }
        }

        private async Task<(bool ok, bool on, int bri)> TryGetWledStateAsync(string baseUrl, CancellationToken ct)
        {
            try
            {
                using var resp = await _wledHttp.GetAsync(baseUrl + "/json/state", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return (false, false, WLED_DEFAULT_BRI);

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool on = true;
                if (root.TryGetProperty("on", out var onProp) &&
                    (onProp.ValueKind == JsonValueKind.True || onProp.ValueKind == JsonValueKind.False))
                {
                    on = onProp.GetBoolean();
                }

                int bri = WLED_DEFAULT_BRI;
                if (root.TryGetProperty("bri", out var briProp) && briProp.ValueKind == JsonValueKind.Number)
                    bri = ClampWledBrightness(briProp.GetInt32());

                return (true, on, bri);
            }
            catch (OperationCanceledException) { throw; }
            catch { return (false, false, WLED_DEFAULT_BRI); }
        }

        private async Task EnsureWledRestoreStateAsync(CancellationToken ct)
        {
            if (_wledInitialStateCaptured)
                return;

            foreach (var baseUrl in GetWledBaseUrlCandidates())
            {
                ct.ThrowIfCancellationRequested();

                var state = await TryGetWledStateAsync(baseUrl, ct).ConfigureAwait(false);
                if (!state.ok)
                    continue;

                _wledInitialOn = state.on;
                _wledInitialBrightness = ClampWledBrightness(state.bri);
                _wledLastBrightness = state.on ? ClampWledBrightness(state.bri) : _wledInitialBrightness;
                _wledLastSentOn = state.on;
                _wledInitialStateCaptured = true;
                _wledRestoreOnExit = true;
                RememberWorkingWledBaseUrl(baseUrl);
                return;
            }

            _wledInitialOn = true;
            _wledInitialBrightness = WLED_DEFAULT_BRI;
            _wledLastBrightness = WLED_DEFAULT_BRI;
            _wledInitialStateCaptured = true;
            _wledRestoreOnExit = true;
        }

        private void ApplyAmbientLightingForCurrentState()
        {
            if (!_wledEnabled) return;

            if (_engine == null || _paused || _preOpenPlaceholderGateActive)
            {
                CancelPendingWledPauseRestore();
                _ = SendWledPowerAsync(true, WLED_FADE_MS);
                return;
            }

            NotifyPlaybackStartedForWled();
        }

        private void NotifyPlaybackStartedForWled()
        {
            if (!_wledEnabled) return;
            CancelPendingWledPauseRestore();
            _ = SendWledPowerAsync(false, WLED_FADE_OUT_MS);
        }

        private void NotifyPlaybackPausedForWled()
        {
            if (!_wledEnabled) return;

            CancelPendingWledPauseRestore();
            var cts = new CancellationTokenSource();
            _wledPauseRestoreCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(WLED_PAUSE_RESTORE_DELAY_MS, cts.Token).ConfigureAwait(false);
                    await SendWledPowerAsync(true, WLED_FADE_MS, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    if (ReferenceEquals(_wledPauseRestoreCts, cts))
                        _wledPauseRestoreCts = null;
                    try { cts.Dispose(); } catch { }
                }
            });
        }

        private void NotifyPlaybackStoppedForWled(bool forceRestore = false)
        {
            CancelPendingWledPauseRestore();

            if (_suppressNextWledRestore && !forceRestore)
            {
                _suppressNextWledRestore = false;
                return;
            }

            _suppressNextWledRestore = false;
            if (_wledEnabled || forceRestore)
                _ = SendWledPowerAsync(true, WLED_FADE_MS);
        }

        private List<string> GetWledBaseUrlCandidates()
        {
            var list = new List<string>();

            void AddCandidate(string? raw)
            {
                string? normalized = NormalizeWledBaseUrl(raw);
                if (string.IsNullOrWhiteSpace(normalized)) return;
                if (list.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase))) return;
                list.Add(normalized);
            }

            AddCandidate(_wledBaseUrl);

            string? configured = NormalizeWledBaseUrl(_wledBaseUrl);
            bool looksDefault =
                string.IsNullOrWhiteSpace(configured) ||
                string.Equals(configured, "http://wled.local", StringComparison.OrdinalIgnoreCase);

            if (looksDefault)
            {
                AddCandidate("http://127.0.0.1:9090");
                AddCandidate("http://localhost:9090");
                AddCandidate("http://127.0.0.1:8080");
                AddCandidate("http://localhost:8080");
                AddCandidate("http://127.0.0.1");
                AddCandidate("http://localhost");
                AddCandidate("http://wled.local");
            }

            return list;
        }

        private async Task<bool> TryPostWledStateAsync(string url, string payload, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await _wledHttp.SendAsync(req, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private async Task<bool> TrySendLegacyWledStateAsync(string baseUrl, bool on, int? bri, int? transitionDeciseconds, CancellationToken ct)
        {
            try
            {
                var url = new StringBuilder();
                url.Append(baseUrl).Append("/win&T=").Append(on ? "1" : "0");
                if (bri.HasValue)
                    url.Append("&A=").Append(Math.Max(0, Math.Min(255, bri.Value)));
                if (transitionDeciseconds.HasValue)
                    url.Append("&TT=").Append(Math.Max(0, transitionDeciseconds.Value));

                using var resp = await _wledHttp.GetAsync(url.ToString(), ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private async Task<bool> TrySendWledPayloadAsync(Dictionary<string, object> state, CancellationToken ct)
        {
            string payload = JsonSerializer.Serialize(state);

            bool desiredOn = true;
            int? desiredBri = null;
            int? desiredTransition = null;
            try
            {
                if (state.TryGetValue("on", out var onObj) && onObj is bool onBool)
                    desiredOn = onBool;
                if (state.TryGetValue("bri", out var briObj))
                    desiredBri = Convert.ToInt32(briObj);
                if (state.TryGetValue("tt", out var ttObj))
                    desiredTransition = Convert.ToInt32(ttObj);
            }
            catch { }

            foreach (var baseUrl in GetWledBaseUrlCandidates())
            {
                ct.ThrowIfCancellationRequested();

                bool ok = await TryPostWledStateAsync(baseUrl + "/json/state", payload, ct).ConfigureAwait(false);
                if (!ok)
                    ok = await TryPostWledStateAsync(baseUrl + "/json", payload, ct).ConfigureAwait(false);
                if (!ok)
                    ok = await TrySendLegacyWledStateAsync(baseUrl, desiredOn, desiredBri, desiredTransition, ct).ConfigureAwait(false);

                if (!ok)
                    continue;

                RememberWorkingWledBaseUrl(baseUrl);
                return true;
            }

            return false;
        }

        private async Task<bool> SendImmediateWledStateAsync(bool on, int? bri, CancellationToken ct, int? transitionDeciseconds = null)
        {
            var state = new Dictionary<string, object>
            {
                ["on"] = on
            };

            if (transitionDeciseconds.HasValue)
                state["tt"] = Math.Max(0, transitionDeciseconds.Value);
            else
                state["tt"] = 0;

            int? normalizedBri = null;
            if (bri.HasValue)
            {
                normalizedBri = on
                    ? ClampWledBrightness(Math.Max(WLED_MIN_BRI, bri.Value))
                    : Math.Max(0, Math.Min(WLED_DEFAULT_BRI, bri.Value));

                state["bri"] = normalizedBri.Value;
            }

            bool ok = await TrySendWledPayloadAsync(state, ct).ConfigureAwait(false);
            if (ok)
            {
                _wledLastSentOn = on;
                if (normalizedBri.HasValue)
                    _wledLastBrightness = normalizedBri.Value;
                else if (!on)
                    _wledLastBrightness = 0;
            }

            return ok;
        }

        private async Task PerformWledFadeAsync(bool on, int fadeMs, CancellationToken ct)
        {
            await EnsureWledRestoreStateAsync(ct).ConfigureAwait(false);

            if (on)
            {
                int targetBri = WLED_DEFAULT_BRI;
                int fadeDs = Math.Max(0, (int)Math.Round(Math.Max(0, fadeMs) / 100.0));

                if (_wledLastSentOn != true)
                {
                    if (!await SendImmediateWledStateAsync(true, WLED_MIN_BRI, ct, 0).ConfigureAwait(false))
                        return;
                }

                await SendImmediateWledStateAsync(true, targetBri, ct, fadeDs).ConfigureAwait(false);
                return;
            }

            int offFadeMs = Math.Max(0, fadeMs);
            int currentBri = ClampWledBrightness(_wledLastBrightness > 0 ? _wledLastBrightness : _wledInitialBrightness);
            int fadeDsOff = Math.Max(1, (int)Math.Round(Math.Max(1, offFadeMs) / 100.0));

            if (_wledLastSentOn != true)
            {
                if (!await SendImmediateWledStateAsync(true, currentBri, ct, 0).ConfigureAwait(false))
                    return;
            }

            if (!await SendImmediateWledStateAsync(true, WLED_MIN_BRI, ct, fadeDsOff).ConfigureAwait(false))
                return;

            await Task.Delay(Math.Max(offFadeMs + 150, 300), ct).ConfigureAwait(false);
            if (!await SendImmediateWledStateAsync(false, 0, ct, 0).ConfigureAwait(false))
                return;

            await Task.Delay(120, ct).ConfigureAwait(false);
            await SendImmediateWledStateAsync(false, 0, ct, 0).ConfigureAwait(false);
        }

        private async Task SendWledPowerAsync(bool on, int fadeMs, CancellationToken ct = default)
        {
            CancellationTokenSource? linked = null;
            CancellationTokenSource? previous = null;

            try
            {
                var now = DateTime.UtcNow;
                if (_wledLastRequestedOn == on && (now - _wledLastCommandUtc).TotalMilliseconds < 300)
                    return;

                _wledLastRequestedOn = on;
                _wledLastCommandUtc = now;

                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                previous = Interlocked.Exchange(ref _wledTransitionCts, linked);
                try { previous?.Cancel(); } catch { }
                try { previous?.Dispose(); } catch { }

                await PerformWledFadeAsync(on, fadeMs, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                if (linked != null)
                {
                    if (ReferenceEquals(_wledTransitionCts, linked))
                        _wledTransitionCts = null;
                    try { linked.Dispose(); } catch { }
                }
            }
        }

        private static readonly TimeSpan PREOPEN_TMDB_WAIT = TimeSpan.FromMilliseconds(1800);

        private sealed class PreOpenPlaceholderVisual
        {
            public string? ImagePath { get; set; }
            public string? TitleText { get; set; }
            public bool UseCover { get; set; }
            public bool ShowBranding { get; set; }
        }

        private static string NormalizeMediaPathForDisplay(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
                    return uri.LocalPath;
            }
            catch { }

            return path;
        }

        private bool ShouldUseMovieMetadataTitleForPath(string pathToOpen)
        {
            string localPath = NormalizeMediaPathForDisplay(pathToOpen);
            if (string.IsNullOrWhiteSpace(localPath) || !LooksLikeVideoByExt(localPath))
                return false;

            string? effectiveCategory = ResolveEffectiveLibraryCategoryForPath(localPath);
            if (string.Equals(effectiveCategory, "Film", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.IsNullOrWhiteSpace(effectiveCategory);
        }

        private bool ShouldUseTmdbBackdropForPath(string pathToOpen)
        {
            return _pausePlaceholderUseTmdbBackdrop
                && ShouldUseMovieMetadataTitleForPath(pathToOpen);
        }

        private string? ResolvePausePlaceholderFallbackPath()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_pausePlaceholderPath) && File.Exists(_pausePlaceholderPath))
                    return _pausePlaceholderPath;
            }
            catch { }

            try { return PickRandomPausePlaceholderPath(); } catch { return null; }
        }

        private string BuildPreOpenPlaceholderTitle(string pathToOpen)
        {
            string candidate = NormalizeMediaPathForDisplay(pathToOpen);

            try
            {
                if (ShouldUseMovieMetadataTitleForPath(candidate))
                {
                    var title = MovieMetadataService.GetBestKnownDisplayTitle(candidate);
                    if (!string.IsNullOrWhiteSpace(title))
                        return title;
                }
            }
            catch { }

            try { return NormalizeDisplayTitle(Path.GetFileNameWithoutExtension(candidate) ?? candidate); }
            catch { return Path.GetFileNameWithoutExtension(candidate) ?? candidate; }
        }

        private string? PickRandomPausePlaceholderPath()
        {
            try
            {
                var files = EnumeratePausePlaceholderFiles().ToList();
                if (files.Count == 0) return null;
                return files[new Random().Next(files.Count)];
            }
            catch { return null; }
        }

        private async Task<PreOpenPlaceholderVisual> BuildPreOpenPlaceholderVisualAsync(string pathToOpen, CancellationToken ct)
        {
            string targetPath = NormalizeMediaPathForDisplay(pathToOpen);
            bool wantsTmdbBackdrop = ShouldUseTmdbBackdropForPath(pathToOpen);
            string? fallbackImage = ResolvePausePlaceholderFallbackPath();

            var visual = new PreOpenPlaceholderVisual
            {
                TitleText = BuildPreOpenPlaceholderTitle(pathToOpen),
                ShowBranding = wantsTmdbBackdrop,
                UseCover = false,
                ImagePath = fallbackImage
            };

            if (!wantsTmdbBackdrop)
                return visual;

            bool tmdbResolved = false;
            bool queueAsyncTmdb = false;

            try
            {
                var syncAttemptWindows = new[]
                {
                    PREOPEN_TMDB_WAIT,
                    TimeSpan.FromMilliseconds(1100)
                };

                for (int attempt = 0; attempt < syncAttemptWindows.Length && !tmdbResolved && !ct.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var resolveTask = Task.Run(() => MovieMetadataService.ResolveTitleAndBackdrop(targetPath, fetchCts.Token), fetchCts.Token);
                        var completed = await Task.WhenAny(resolveTask, Task.Delay(syncAttemptWindows[attempt], ct));
                        if (completed == resolveTask)
                        {
                            try
                            {
                                var art = await resolveTask;
                                if (!string.IsNullOrWhiteSpace(art.normalizedTitle))
                                    visual.TitleText = art.normalizedTitle;
                                if (!string.IsNullOrWhiteSpace(art.localBackdropPath) && File.Exists(art.localBackdropPath))
                                {
                                    visual.ImagePath = art.localBackdropPath;
                                    visual.ShowBranding = true;
                                    visual.UseCover = true;
                                    tmdbResolved = true;
                                    queueAsyncTmdb = false;
                                    break;
                                }

                                queueAsyncTmdb = true;
                            }
                            catch (OperationCanceledException) { }
                            catch
                            {
                                queueAsyncTmdb = true;
                            }
                        }
                        else
                        {
                            try { fetchCts.Cancel(); } catch { }
                            queueAsyncTmdb = true;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        queueAsyncTmdb = true;
                    }

                    if (!tmdbResolved && attempt < syncAttemptWindows.Length - 1 && !ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(220 + (attempt * 180)), ct); } catch (OperationCanceledException) { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                queueAsyncTmdb = true;
            }

            if (!tmdbResolved)
            {
                visual.ShowBranding = true;
                visual.UseCover = false;
                visual.ImagePath = null;
            }

            if (!tmdbResolved && queueAsyncTmdb && !ct.IsCancellationRequested)
            {
                try { QueueTmdbBackdropPlaceholder(pathToOpen); } catch { }
            }

            return visual;
        }

        private void CancelPlaceholderBackdropFetch()
        {
            var old = Interlocked.Exchange(ref _placeholderBackdropCts, null);
            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }
        }

        private static TimeSpan GetTmdbPlaceholderRetryDelay(int attempt)
        {
            double[] scheduleMs = { 900d, 1500d, 2500d, 4000d, 6000d, 8500d };
            double ms = scheduleMs[Math.Max(0, Math.Min(scheduleMs.Length - 1, attempt - 1))];
            return TimeSpan.FromMilliseconds(ms);
        }

        private bool IsQueuedTmdbPlaceholderStillRelevant(CancellationTokenSource cts, string normalizedTargetPath)
        {
            if (cts == null || cts.IsCancellationRequested || IsDisposed)
                return false;

            if (!ReferenceEquals(_placeholderBackdropCts, cts))
                return false;

            if (!_preOpenPlaceholderGateActive)
                return false;

            string pendingPath = NormalizeMediaPathForDisplay(_pendingPathAfterPlaceholderGate ?? string.Empty);
            if (!string.Equals(pendingPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private void QueueTmdbBackdropPlaceholder(string pathToOpen)
        {
            CancelPlaceholderBackdropFetch();

            if (!ShouldUseTmdbBackdropForPath(pathToOpen)) return;
            if (string.IsNullOrWhiteSpace(pathToOpen)) return;

            string targetPath = NormalizeMediaPathForDisplay(pathToOpen);
            var cts = new CancellationTokenSource();
            _placeholderBackdropCts = cts;

            _ = Task.Run(async () =>
            {
                int attempt = 0;

                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        if (!IsQueuedTmdbPlaceholderStillRelevant(cts, targetPath))
                            return;

                        bool hasResolvedBackdrop = false;

                        try
                        {
                            var resolved = MovieMetadataService.ResolveTitleAndBackdrop(targetPath, cts.Token);
                            hasResolvedBackdrop = !string.IsNullOrWhiteSpace(resolved.localBackdropPath) && File.Exists(resolved.localBackdropPath);

                            if (!cts.IsCancellationRequested)
                            {
                                try
                                {
                                    BeginInvoke(new Action(() =>
                                    {
                                        if (!IsQueuedTmdbPlaceholderStillRelevant(cts, targetPath))
                                            return;
                                        if (_pausePlaceholder == null || _pausePlaceholder.IsDisposed || !_pausePlaceholder.Visible)
                                            return;

                                        if (!string.IsNullOrWhiteSpace(resolved.normalizedTitle))
                                            _pausePlaceholder.TitleText = resolved.normalizedTitle!;

                                        _pausePlaceholder.ShowBranding = true;
                                        if (hasResolvedBackdrop)
                                        {
                                            _pausePlaceholder.UseCoverImage = true;
                                            _pausePlaceholder.ShowPlaceholder(resolved.localBackdropPath!);
                                        }
                                        else
                                        {
                                            _pausePlaceholder.UseCoverImage = false;
                                            _pausePlaceholder.Invalidate();
                                        }
                                    }));
                                }
                                catch { }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch
                        {
                        }

                        if (hasResolvedBackdrop)
                            return;

                        attempt++;
                        await Task.Delay(GetTmdbPlaceholderRetryDelay(attempt), cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    if (ReferenceEquals(_placeholderBackdropCts, cts))
                        _placeholderBackdropCts = null;
                    try { cts.Dispose(); } catch { }
                }
            });
        }

        private string? ResolveDemoPath(string? fileOrPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileOrPath)) return null;
                if (Path.IsPathRooted(fileOrPath) && File.Exists(fileOrPath)) return fileOrPath;

                var p = Path.Combine(_preRollDemoFolder, fileOrPath);
                return File.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }

        private string? ResolvePausePlaceholderPath(string? fileOrPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileOrPath)) return null;
                if (Path.IsPathRooted(fileOrPath) && File.Exists(fileOrPath)) return fileOrPath;

                var p = Path.Combine(_pausePlaceholderFolder, fileOrPath);
                return File.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }

        private IEnumerable<string> EnumeratePausePlaceholderFiles()
        {
            try
            {
                Directory.CreateDirectory(_pausePlaceholderFolder);
                if (!Directory.Exists(_pausePlaceholderFolder)) return Array.Empty<string>();

                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"
                };

                return Directory.EnumerateFiles(_pausePlaceholderFolder)
                    .Where(f => exts.Contains(Path.GetExtension(f) ?? ""))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private void PopulatePausePlaceholderMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();

            var miRandom = new ToolStripMenuItem("Casuale")
            {
                Checked = string.IsNullOrWhiteSpace(_pausePlaceholderPath)
            };
            miRandom.Click += (_, __) =>
            {
                _pausePlaceholderPath = null;

                // Se scelgo un placeholder, tipicamente voglio la feature attiva.
                if (!_pausePlaceholderEnabled)
                {
                    _pausePlaceholderEnabled = true;
                    if (_miPausePlaceholderEnable != null) _miPausePlaceholderEnable.Checked = true;
                }

                try { SaveExtrasConfig(); } catch { }
            };
            parent.DropDownItems.Add(miRandom);
            parent.DropDownItems.Add(new ToolStripSeparator());

            var files = EnumeratePausePlaceholderFiles().ToList();
            if (files.Count == 0)
            {
                parent.DropDownItems.Add(new ToolStripMenuItem("Nessun file nella cartella") { Enabled = false });
                return;
            }

            string? current = ResolvePausePlaceholderPath(_pausePlaceholderPath) ?? _pausePlaceholderPath;

            foreach (var f in files)
            {
                string label = Path.GetFileNameWithoutExtension(f);
                var mi = new ToolStripMenuItem(label)
                {
                    Checked = current != null &&
                              string.Equals(Path.GetFileName(current), Path.GetFileName(f), StringComparison.OrdinalIgnoreCase)
                };

                mi.Click += (_, __) =>
                {
                    _pausePlaceholderPath = f;

                    if (!_pausePlaceholderEnabled)
                    {
                        _pausePlaceholderEnabled = true;
                        if (_miPausePlaceholderEnable != null) _miPausePlaceholderEnable.Checked = true;
                    }

                    try { SaveExtrasConfig(); } catch { }
                };

                parent.DropDownItems.Add(mi);
            }
        }

        private IEnumerable<string> EnumerateDemoFiles()
        {
            try
            {
                if (!Directory.Exists(_preRollDemoFolder)) return Array.Empty<string>();
                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".mkv", ".mp4", ".mov", ".avi", ".wmv", ".m2ts", ".ts", ".webm"
                };
                return Directory.EnumerateFiles(_preRollDemoFolder)
                    .Where(f => exts.Contains(Path.GetExtension(f) ?? ""))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private void PopulatePreRollDemoMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();

            var none = new ToolStripMenuItem("Nessuna")
            {
                Checked = string.IsNullOrWhiteSpace(_preRollDemoPath)
            };
            none.Click += (_, __) =>
            {
                _preRollDemoPath = null;
                try { SaveExtrasConfig(); } catch { }
            };
            parent.DropDownItems.Add(none);
            parent.DropDownItems.Add(new ToolStripSeparator());

            var demos = EnumerateDemoFiles().ToList();
            if (demos.Count == 0)
            {
                parent.DropDownItems.Add(new ToolStripMenuItem("(nessuna demo trovata)") { Enabled = false });
                parent.DropDownItems.Add(new ToolStripMenuItem("(usa la cartella Assets\\Demos)") { Enabled = false });
                return;
            }

            foreach (var f in demos)
            {
                var label = Path.GetFileNameWithoutExtension(f);
                var it = new ToolStripMenuItem(label)
                {
                    Checked = string.Equals(_preRollDemoPath, f, StringComparison.OrdinalIgnoreCase)
                };
                it.Click += (_, __) =>
                {
                    _preRollDemoPath = f;

                    // Se scelgo una demo, tipicamente voglio la feature attiva (evita “riapri menu per abilitarla”).
                    if (!_preRollEnabled)
                    {
                        _preRollEnabled = true;
                        if (_miPreRollEnable != null) _miPreRollEnable.Checked = true;
                    }

                    try { SaveExtrasConfig(); } catch { }
                };
                parent.DropDownItems.Add(it);
            }
        }

        private void OpenFolderInExplorer(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private bool ShouldRunPreRollForPath(string path)
        {
            try
            {
                if (!_preRollEnabled) return false;
                if (string.IsNullOrWhiteSpace(_preRollDemoPath)) return false;
                if (string.IsNullOrWhiteSpace(path)) return false;
                if (!ShouldUseCinemaFeaturesForPath(path)) return false;

                // Non fare pre-roll su URL
                if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return false;

                // Non fare pre-roll sulla demo stessa
                if (!string.IsNullOrWhiteSpace(_preRollDemoPath) && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_preRollDemoPath), StringComparison.OrdinalIgnoreCase))
                    return false;

                // Solo file video
                var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
                var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".mkv", ".mp4", ".mov", ".avi", ".wmv", ".m2ts", ".ts", ".webm"
                };
                if (!videoExts.Contains(ext)) return false;

                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private void OpenFile()
        {
            ShowLibrary();
        }

        private static bool IsQueuePlayablePathInternal(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                if (File.Exists(path))
                    return true;
            }
            catch { }

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.IsFile;

            return false;
        }

        private List<string> NormalizePlaybackQueuePaths(IEnumerable<string>? paths)
        {
            var result = new List<string>();
            if (paths == null)
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in paths)
            {
                var candidate = raw?.Trim();
                if (!IsQueuePlayablePathInternal(candidate))
                    continue;
                if (!seen.Add(candidate!))
                    continue;
                result.Add(candidate!);
            }
            return result;
        }

        private void OpenPlaybackQueueItem(string path, double resume = 0, bool startPaused = false)
        {
            _playbackQueueTransitionInProgress = true;
            _nextOpenBelongsToPlaybackQueue = true;
            OpenPath(path, resume: resume, startPaused: startPaused, allowPlaceholderGate: false);
        }

        private void SchedulePlaybackQueueOpen(string path)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(140).ConfigureAwait(false); }
                    catch { }

                    try
                    {
                        if (IsDisposed)
                            return;

                        BeginInvoke(new Action(() => OpenPlaybackQueueItem(path)));
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static void ShufflePlaybackQueueInPlace(IList<string> items, Random random)
        {
            if (items == null || items.Count <= 1)
                return;

            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                if (i == j)
                    continue;

                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        private void SetSingleTrackLoop(string? path, bool enabled)
        {
            string? normalized = path?.Trim();
            if (!enabled || !IsQueuePlayablePathInternal(normalized))
            {
                _singleTrackLoopEnabled = false;
                _singleTrackLoopPath = null;
                try { _lblStatus.Text = "Loop brano disattivato"; } catch { }
                try { RefreshPlaybackQueueUi(); } catch { }
                return;
            }

            _singleTrackLoopPath = normalized;
            _singleTrackLoopEnabled = !string.IsNullOrWhiteSpace(_singleTrackLoopPath);
            try
            {
                if (_singleTrackLoopEnabled && !string.IsNullOrWhiteSpace(_singleTrackLoopPath))
                    _lblStatus.Text = $"Loop brano: {BuildPlaybackQueueLabel(_singleTrackLoopPath!)}";
            }
            catch { }
            try { RefreshPlaybackQueueUi(); } catch { }
        }

        private bool IsSingleTrackLoopEnabledForPath(string? path)
        {
            if (!_singleTrackLoopEnabled || string.IsNullOrWhiteSpace(_singleTrackLoopPath) || string.IsNullOrWhiteSpace(path))
                return false;

            return string.Equals(_singleTrackLoopPath, path.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void RemoveInvalidPlaybackQueueItems()
        {
            if (_playbackQueue.Count == 0)
                return;

            _playbackQueue.RemoveAll(path => !IsQueuePlayablePathInternal(path));
            if (_playbackQueue.Count == 0)
            {
                _playbackQueueIndex = -1;
                _playbackQueueSessionActive = false;
                _playbackQueueShuffleMode = false;
            }
        }

        private void NormalizeActivePlaybackQueueToCurrent()
        {
            RemoveInvalidPlaybackQueueItems();
            _playbackQueueHistory.RemoveAll(path => !IsQueuePlayablePathInternal(path));

            if (_playbackQueue.Count == 0)
            {
                _playbackQueueIndex = -1;
                _playbackQueueSessionActive = false;
                return;
            }

            if (_playbackQueueSessionActive)
            {
                string currentPath = _currentPath?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(currentPath) && IsQueuePlayablePathInternal(currentPath))
                {
                    int currentIndex = _playbackQueue.FindIndex(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
                    if (currentIndex > 0)
                    {
                        string currentItem = _playbackQueue[currentIndex];
                        _playbackQueue.RemoveAt(currentIndex);
                        _playbackQueue.Insert(0, currentItem);
                    }
                    else if (currentIndex < 0)
                    {
                        _playbackQueue.Insert(0, currentPath);
                    }
                }
                else if (_playbackQueueIndex > 0 && _playbackQueueIndex < _playbackQueue.Count)
                {
                    string indexedItem = _playbackQueue[_playbackQueueIndex];
                    _playbackQueue.RemoveAt(_playbackQueueIndex);
                    _playbackQueue.Insert(0, indexedItem);
                }

                _playbackQueueIndex = _playbackQueue.Count > 0 ? 0 : -1;
                return;
            }

            if (_playbackQueueIndex < 0 || _playbackQueueIndex >= _playbackQueue.Count)
                _playbackQueueIndex = 0;
        }

        private int GetPlaybackQueueCurrentIndex()
        {
            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count == 0)
                return -1;

            if (_playbackQueueSessionActive)
                return 0;

            string currentPath = _currentPath?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                int byPath = _playbackQueue.FindIndex(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
                if (byPath >= 0)
                    return byPath;
            }

            if (_playbackQueueIndex >= 0 && _playbackQueueIndex < _playbackQueue.Count)
                return _playbackQueueIndex;

            return _playbackQueue.Count > 0 ? 0 : -1;
        }

        private bool IsPathQueued(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || _playbackQueue.Count == 0)
                return false;

            return _playbackQueue.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildPlaybackQueueLabel(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                string title = MovieMetadataService.GetBestKnownDisplayTitle(path);
                if (!string.IsNullOrWhiteSpace(title))
                    return title.Trim();
            }
            catch { }

            try
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
            catch { }

            return path;
        }

        private IReadOnlyList<MediaLibraryPage.PlaybackQueueViewItem> GetPlaybackQueueSnapshotItems()
        {
            NormalizeActivePlaybackQueueToCurrent();

            var snapshot = new List<MediaLibraryPage.PlaybackQueueViewItem>();
            int currentIndex = GetPlaybackQueueCurrentIndex();
            for (int i = 0; i < _playbackQueue.Count; i++)
            {
                string path = _playbackQueue[i];
                snapshot.Add(new MediaLibraryPage.PlaybackQueueViewItem
                {
                    Path = path,
                    Label = BuildPlaybackQueueLabel(path),
                    Index = i,
                    IsCurrent = i == currentIndex
                });
            }
            return snapshot;
        }

        private bool CanSkipPlaybackQueue(int delta)
        {
            if (delta == 0)
                return false;

            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count == 0)
                return false;

            if (_playbackQueueSessionActive)
                return delta > 0 ? _playbackQueue.Count > 1 : _playbackQueueHistory.Count > 0;

            int index = GetPlaybackQueueCurrentIndex();
            if (index < 0)
                index = 0;

            int targetIndex = index + delta;
            return targetIndex >= 0 && targetIndex < _playbackQueue.Count;
        }

        private void RefreshPlaybackQueueUi(string? preferredSelectPath = null)
        {
            try
            {
                if (IsDisposed)
                    return;

                if (InvokeRequired)
                {
                    try
                    {
                        BeginInvoke(new Action(() => RefreshPlaybackQueueUi(preferredSelectPath)));
                    }
                    catch { }
                    return;
                }

                NormalizeActivePlaybackQueueToCurrent();

                if (_mQueueMenuItem != null && !_mQueueMenuItem.IsDisposed)
                {
                    _mQueueMenuItem.Text = _playbackQueue.Count > 0 ? $"Coda ({_playbackQueue.Count})" : "Coda";
                    try { PopulatePlaybackQueueMenu(_mQueueMenuItem); } catch { }
                    try
                    {
                        if (_mQueueMenuItem.DropDown != null && _mQueueMenuItem.DropDown.Visible)
                        {
                            _mQueueMenuItem.DropDown.PerformLayout();
                            _mQueueMenuItem.DropDown.Invalidate(true);
                            _mQueueMenuItem.DropDown.Update();
                        }
                    }
                    catch { }
                }

                if (_playbackQueueEditorRef != null && _playbackQueueEditorRef.TryGetTarget(out var editor) && editor != null && !editor.IsDisposed)
                    editor.RefreshSnapshotExternal(preferredSelectPath);
            }
            catch { }
        }

        private void OpenPlaybackQueueHead(bool resetLibraryFocus = false)
        {
            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count == 0)
            {
                RefreshPlaybackQueueUi();
                return;
            }

            string nextPath = _playbackQueue[0];
            _playbackQueueIndex = 0;
            _playbackQueueSessionActive = true;
            RefreshPlaybackQueueUi(nextPath);

            if (string.Equals(_currentPath?.Trim(), nextPath, StringComparison.OrdinalIgnoreCase))
            {
                try { HideLibrary(); } catch { }
                return;
            }

            _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(nextPath) ?? _currentLibraryCategory;
            _suppressPreRollOnce = true;
            _suppressVideoLoadingOnce = true;
            if (resetLibraryFocus)
                ResetLibraryRemoteActivation(clearFocusRing: true);
            _endTriggered = true;
            _endCandidateSinceUtc = DateTime.MinValue;
            try { HideLibrary(); } catch { }
            OpenPlaybackQueueItem(nextPath);
        }

        private void PlayQueuedPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || _playbackQueue.Count == 0)
                return;

            NormalizeActivePlaybackQueueToCurrent();

            int index = _playbackQueue.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            if (_playbackQueueSessionActive)
            {
                string currentHead = _playbackQueue.Count > 0 ? _playbackQueue[0] : string.Empty;
                if (index > 0 && !string.IsNullOrWhiteSpace(currentHead) && !string.Equals(currentHead, path, StringComparison.OrdinalIgnoreCase))
                    _playbackQueueHistory.Add(currentHead);
            }
            else
            {
                _playbackQueueHistory.Clear();
            }

            if (index > 0)
                _playbackQueue.RemoveRange(0, index);

            _playbackQueueIndex = _playbackQueue.Count > 0 ? 0 : -1;
            _playbackQueueSessionActive = _playbackQueue.Count > 0;

            OpenPlaybackQueueHead();
        }

        private void ReorderPlaybackQueuePath(string? path, int targetIndex)
        {
            if (string.IsNullOrWhiteSpace(path) || _playbackQueue.Count <= 1)
                return;

            NormalizeActivePlaybackQueueToCurrent();

            int index = _playbackQueue.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            int minIndex = 0;
            if (_playbackQueueSessionActive)
            {
                if (index == 0)
                    return;
                minIndex = 1;
            }

            targetIndex = Math.Max(minIndex, Math.Min(_playbackQueue.Count - 1, targetIndex));
            if (targetIndex == index)
                return;

            string movedPath = _playbackQueue[index];
            _playbackQueue.RemoveAt(index);
            if (targetIndex > _playbackQueue.Count)
                targetIndex = _playbackQueue.Count;
            _playbackQueue.Insert(targetIndex, movedPath);

            if (!_playbackQueueSessionActive)
            {
                if (_playbackQueueIndex == index)
                    _playbackQueueIndex = targetIndex;
                else if (_playbackQueueIndex > index && _playbackQueueIndex <= targetIndex)
                    _playbackQueueIndex--;
                else if (_playbackQueueIndex < index && _playbackQueueIndex >= targetIndex)
                    _playbackQueueIndex++;
            }
            else
            {
                _playbackQueueIndex = 0;
            }

            RefreshPlaybackQueueUi(movedPath);
        }

        private void MovePlaybackQueuePath(string? path, int delta)
        {
            if (string.IsNullOrWhiteSpace(path) || delta == 0 || _playbackQueue.Count <= 1)
                return;

            int index = _playbackQueue.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            int targetIndex = index + delta;
            if (targetIndex < 0 || targetIndex >= _playbackQueue.Count)
                return;

            ReorderPlaybackQueuePath(path, targetIndex);
        }

        private void ClearPlaybackQueue()
        {
            _playbackQueue.Clear();
            _playbackQueueHistory.Clear();
            _playbackQueueIndex = -1;
            _playbackQueueShuffleMode = false;
            _playbackQueueSessionActive = false;
            _nextOpenBelongsToPlaybackQueue = false;
            RefreshPlaybackQueueUi();
        }

        private void PopulatePlaybackQueueMenu(ToolStripMenuItem root)
        {
            if (root == null)
                return;

            root.DropDownItems.Clear();

            var snapshot = GetPlaybackQueueSnapshotItems()
                .OrderBy(item => item.Index)
                .ToList();

            root.Text = snapshot.Count > 0 ? $"Coda ({snapshot.Count})" : "Coda";

            var openEditorItem = new ToolStripMenuItem(snapshot.Count > 0 ? $"Apri editor coda ({snapshot.Count})…" : "Apri editor coda…");
            openEditorItem.Click += (_, __) => ShowPlaybackQueueEditor();
            root.DropDownItems.Add(openEditorItem);

            if (snapshot.Count == 0)
            {
                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(new ToolStripMenuItem("Coda vuota") { Enabled = false });
                ApplyDarkMenuThemeRecursive(root.DropDownItems);
                return;
            }

            var current = snapshot.FirstOrDefault(item => item.IsCurrent);
            if (current != null)
            {
                string currentLabel = string.IsNullOrWhiteSpace(current.Label)
                    ? Path.GetFileName(current.Path)
                    : current.Label;

                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(new ToolStripMenuItem($"In riproduzione: {currentLabel}")
                {
                    Enabled = false
                });
            }

            root.DropDownItems.Add(new ToolStripSeparator());

            var prevItem = new ToolStripMenuItem("Elemento precedente")
            {
                Enabled = CanSkipPlaybackQueue(-1)
            };
            prevItem.Click += (_, __) => { try { TrySkipPlaybackQueue(-1); } catch { } };
            root.DropDownItems.Add(prevItem);

            var nextItem = new ToolStripMenuItem("Elemento successivo")
            {
                Enabled = CanSkipPlaybackQueue(1)
            };
            nextItem.Click += (_, __) => { try { TrySkipPlaybackQueue(1); } catch { } };
            root.DropDownItems.Add(nextItem);

            root.DropDownItems.Add(new ToolStripSeparator());
            foreach (var item in snapshot.Take(8))
            {
                string label = string.IsNullOrWhiteSpace(item.Label) ? Path.GetFileName(item.Path) : item.Label;
                string prefix = item.IsCurrent ? "▶ " : string.Empty;
                var queueItem = new ToolStripMenuItem(prefix + label)
                {
                    Checked = item.IsCurrent
                };
                string capturedPath = item.Path;
                queueItem.Click += (_, __) => PlayQueuedPath(capturedPath);
                root.DropDownItems.Add(queueItem);
            }

            if (snapshot.Count > 8)
                root.DropDownItems.Add(new ToolStripMenuItem($"… altri {snapshot.Count - 8} elementi") { Enabled = false });

            root.DropDownItems.Add(new ToolStripSeparator());
            var clearQueueItem = new ToolStripMenuItem("Svuota coda");
            clearQueueItem.Click += (_, __) => ClearPlaybackQueue();
            root.DropDownItems.Add(clearQueueItem);

            ApplyDarkMenuThemeRecursive(root.DropDownItems);
        }

        private void ShowPlaybackQueueEditor()
        {
            try
            {
                if (_playbackQueueEditorRef != null && _playbackQueueEditorRef.TryGetTarget(out var openEditor) && openEditor != null && !openEditor.IsDisposed)
                {
                    try { openEditor.Activate(); } catch { }
                    try { openEditor.BringToFront(); } catch { }
                    return;
                }

                var editor = new PlaybackQueueEditorForm(
                    () => GetPlaybackQueueSnapshotItems(),
                    path =>
                    {
                        PlayQueuedPath(path);
                    },
                    path =>
                    {
                        RemoveFromPlaybackQueue(new[] { path });
                    },
                    () =>
                    {
                        ClearPlaybackQueue();
                    },
                    (path, targetIndex) =>
                    {
                        ReorderPlaybackQueuePath(path, targetIndex);
                    });

                _playbackQueueEditorRef = new WeakReference<PlaybackQueueEditorForm>(editor);
                editor.FormClosed += (_, __) =>
                {
                    _playbackQueueEditorRef = null;
                    try { _overlayHost?.SetInteractive(false); } catch { }
                    try { BringOverlaysToFront(); } catch { }
                };

                editor.TopMost = FormBorderStyle == FormBorderStyle.None;
                editor.StartPosition = FormStartPosition.CenterParent;

                try { _overlayHost?.SetInteractive(false); } catch { }
                editor.Show(this);
                editor.BringToFront();
                try { BringOverlaysToFront(); } catch { }
            }
            catch { }
        }

        private sealed class PlaybackQueueEditorForm : Form
        {
            private sealed class QueueRow
            {
                public string Path { get; set; } = string.Empty;
                public string Label { get; set; } = string.Empty;
                public bool IsCurrent { get; set; }
                public override string ToString() => Label;
            }

            private readonly Func<IReadOnlyList<MediaLibraryPage.PlaybackQueueViewItem>> _snapshotProvider;
            private readonly Action<string> _playNow;
            private readonly Action<string> _removeOne;
            private readonly Action _clearAll;
            private readonly Action<string, int> _reorder;
            private readonly ListBox _list;
            private readonly Label _subtitle;
            private int _dragIndex = -1;
            private Point _dragStart = Point.Empty;

            public PlaybackQueueEditorForm(
                Func<IReadOnlyList<MediaLibraryPage.PlaybackQueueViewItem>> snapshotProvider,
                Action<string> playNow,
                Action<string> removeOne,
                Action clearAll,
                Action<string, int> reorder)
            {
                _snapshotProvider = snapshotProvider;
                _playNow = playNow;
                _removeOne = removeOne;
                _clearAll = clearAll;
                _reorder = reorder;

                Text = "Coda di riproduzione";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                ClientSize = new Size(760, 560);
                BackColor = Color.FromArgb(18, 18, 18);
                ForeColor = Color.Gainsboro;

                var title = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 42,
                    Margin = new Padding(0),
                    Padding = new Padding(18, 12, 18, 0),
                    Text = "Coda di riproduzione",
                    Font = new Font("Segoe UI Semibold", 13f),
                    ForeColor = Color.White,
                    BackColor = Color.Transparent
                };

                _subtitle = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 44,
                    Margin = new Padding(0),
                    Padding = new Padding(18, 2, 18, 8),
                    Text = "Trascina gli elementi per riordinarli. Doppio clic per riprodurre.",
                    Font = new Font("Segoe UI", 9.5f),
                    ForeColor = Color.FromArgb(182, 188, 196),
                    BackColor = Color.Transparent
                };

                var bottomBar = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 72,
                    Margin = new Padding(0),
                    Padding = new Padding(18, 14, 18, 18),
                    BackColor = Color.FromArgb(18, 18, 18)
                };

                _list = new ListBox
                {
                    Dock = DockStyle.Fill,
                    IntegralHeight = false,
                    BorderStyle = BorderStyle.FixedSingle,
                    DrawMode = DrawMode.OwnerDrawFixed,
                    ItemHeight = 42,
                    BackColor = Color.FromArgb(21, 24, 31),
                    ForeColor = Color.Gainsboro,
                    Font = new Font("Segoe UI", 10f),
                    SelectionMode = SelectionMode.One,
                    AllowDrop = true,
                    Margin = new Padding(18, 0, 18, 0)
                };
                _list.DrawItem += OnDrawQueueItem;
                _list.MouseDown += OnQueueMouseDown;
                _list.MouseMove += OnQueueMouseMove;
                _list.DragOver += OnQueueDragOver;
                _list.DragDrop += OnQueueDragDrop;
                _list.DoubleClick += (_, __) => PlaySelectedAndClose();
                _list.KeyDown += OnQueueKeyDown;

                var listHost = new Panel
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0),
                    Padding = new Padding(18, 0, 18, 0),
                    BackColor = Color.Transparent
                };
                listHost.Controls.Add(_list);

                Button MakeButton(string text, Action onClick)
                {
                    var btn = new Button
                    {
                        Text = text,
                        Width = text.StartsWith("Svuota", StringComparison.OrdinalIgnoreCase) ? 128 : 112,
                        Height = 34,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Color.FromArgb(36, 48, 78),
                        ForeColor = Color.White,
                        Margin = new Padding(0),
                        TabStop = true,
                        Anchor = AnchorStyles.Right | AnchorStyles.Top
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(74, 90, 126);
                    btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(28, 38, 64);
                    btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(44, 58, 96);
                    btn.Click += (_, __) => onClick();
                    return btn;
                }

                var btnClose = MakeButton("Chiudi", () => Close());
                var btnClear = MakeButton("Svuota coda", () =>
                {
                    _clearAll();
                    ReloadSnapshot();
                });
                var btnRemove = MakeButton("Rimuovi", () =>
                {
                    var row = GetSelectedRow();
                    if (row == null)
                        return;

                    _removeOne(row.Path);
                    ReloadSnapshot();
                });
                var btnPlay = MakeButton("Riproduci", PlaySelectedAndClose);

                AcceptButton = btnPlay;
                CancelButton = btnClose;

                int y = 14;
                btnClose.Location = new Point(bottomBar.ClientSize.Width - btnClose.Width - 18, y);
                btnClear.Location = new Point(btnClose.Left - 12 - btnClear.Width, y);
                btnRemove.Location = new Point(btnClear.Left - 12 - btnRemove.Width, y);
                btnPlay.Location = new Point(btnRemove.Left - 12 - btnPlay.Width, y);

                void LayoutButtons()
                {
                    int x = bottomBar.ClientSize.Width - 18;
                    foreach (var btn in new[] { btnClose, btnClear, btnRemove, btnPlay })
                    {
                        x -= btn.Width;
                        btn.Location = new Point(Math.Max(18, x), y);
                        x -= 12;
                    }
                }

                bottomBar.Resize += (_, __) => LayoutButtons();
                bottomBar.Controls.Add(btnClose);
                bottomBar.Controls.Add(btnClear);
                bottomBar.Controls.Add(btnRemove);
                bottomBar.Controls.Add(btnPlay);
                LayoutButtons();

                Controls.Add(listHost);
                Controls.Add(bottomBar);
                Controls.Add(_subtitle);
                Controls.Add(title);
                try { bottomBar.BringToFront(); } catch { }
                try { _subtitle.BringToFront(); } catch { }
                try { title.BringToFront(); } catch { }

                ReloadSnapshot();
            }

            public void RefreshSnapshotExternal(string? selectPath = null)
            {
                if (IsDisposed)
                    return;

                if (InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => RefreshSnapshotExternal(selectPath))); } catch { }
                    return;
                }

                ReloadSnapshot(selectPath);
            }

            private QueueRow? GetSelectedRow() => _list.SelectedItem as QueueRow;

            private void ReloadSnapshot(string? selectPath = null)
            {
                var snapshot = (_snapshotProvider?.Invoke() ?? Array.Empty<MediaLibraryPage.PlaybackQueueViewItem>())
                    .OrderBy(item => item.Index)
                    .ToList();

                _list.BeginUpdate();
                try
                {
                    _list.Items.Clear();
                    foreach (var item in snapshot)
                    {
                        _list.Items.Add(new QueueRow
                        {
                            Path = item.Path,
                            Label = string.IsNullOrWhiteSpace(item.Label) ? Path.GetFileName(item.Path) : item.Label,
                            IsCurrent = item.IsCurrent
                        });
                    }
                }
                finally
                {
                    _list.EndUpdate();
                }

                if (_list.Items.Count == 0)
                {
                    _subtitle.Text = "La coda è vuota.";
                    return;
                }

                _subtitle.Text = "Trascina gli elementi per riordinarli. Doppio clic per riprodurre.";

                int selectedIndex = -1;
                if (!string.IsNullOrWhiteSpace(selectPath))
                {
                    for (int i = 0; i < _list.Items.Count; i++)
                    {
                        if (_list.Items[i] is QueueRow row && string.Equals(row.Path, selectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }

                if (selectedIndex < 0)
                {
                    for (int i = 0; i < _list.Items.Count; i++)
                    {
                        if (_list.Items[i] is QueueRow row && row.IsCurrent)
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }

                if (selectedIndex < 0)
                    selectedIndex = 0;

                if (selectedIndex >= 0 && selectedIndex < _list.Items.Count)
                    _list.SelectedIndex = selectedIndex;
            }

            private void PlaySelectedAndClose()
            {
                var row = GetSelectedRow();
                if (row == null)
                    return;

                _playNow(row.Path);
                DialogResult = DialogResult.OK;
                Close();
            }

            private void OnQueueKeyDown(object? sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete)
                {
                    var row = GetSelectedRow();
                    if (row == null)
                        return;

                    _removeOne(row.Path);
                    ReloadSnapshot();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    PlaySelectedAndClose();
                    e.Handled = true;
                }
            }

            private void OnDrawQueueItem(object? sender, DrawItemEventArgs e)
            {
                e.DrawBackground();
                if (e.Index < 0 || e.Index >= _list.Items.Count)
                    return;

                if (_list.Items[e.Index] is not QueueRow row)
                    return;

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

                Color fill = row.IsCurrent
                    ? Color.FromArgb(selected ? 56 : 44, 72, 126)
                    : (selected ? Color.FromArgb(34, 44, 68) : (e.Index % 2 == 0 ? Color.FromArgb(23, 26, 34) : Color.FromArgb(20, 23, 30)));

                using (var br = new SolidBrush(fill))
                    g.FillRectangle(br, e.Bounds);

                if (row.IsCurrent)
                {
                    using var accent = new SolidBrush(Color.FromArgb(86, 146, 255));
                    g.FillRectangle(accent, e.Bounds.Left + 8, e.Bounds.Top + 8, 4, Math.Max(10, e.Bounds.Height - 16));
                }

                using var titleFont = new Font("Segoe UI Semibold", 10f);
                using var subFont = new Font("Segoe UI", 8.75f);
                var titleRect = new Rectangle(e.Bounds.Left + 22, e.Bounds.Top + 7, Math.Max(40, e.Bounds.Width - 44), 18);
                var metaRect = new Rectangle(e.Bounds.Left + 22, e.Bounds.Top + 22, Math.Max(40, e.Bounds.Width - 44), 16);

                TextRenderer.DrawText(
                    g,
                    row.Label,
                    titleFont,
                    titleRect,
                    Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                TextRenderer.DrawText(
                    g,
                    row.IsCurrent ? "In riproduzione" : "Trascina per riordinare",
                    subFont,
                    metaRect,
                    Color.FromArgb(185, 192, 201),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                if (selected)
                {
                    using var pen = new Pen(Color.FromArgb(104, 132, 196));
                    g.DrawRectangle(pen, e.Bounds.Left, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 1), Math.Max(1, e.Bounds.Height - 1));
                }

                e.DrawFocusRectangle();
            }

            private void OnQueueMouseDown(object? sender, MouseEventArgs e)
            {
                _dragIndex = _list.IndexFromPoint(e.Location);
                _dragStart = e.Location;
            }

            private void OnQueueMouseMove(object? sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left)
                    return;

                if (_dragIndex < 0 || _dragIndex >= _list.Items.Count)
                    return;

                if (Math.Abs(e.X - _dragStart.X) < SystemInformation.DragSize.Width / 2 &&
                    Math.Abs(e.Y - _dragStart.Y) < SystemInformation.DragSize.Height / 2)
                {
                    return;
                }

                if (_list.Items[_dragIndex] is QueueRow row)
                    _list.DoDragDrop(row, DragDropEffects.Move);
            }

            private void OnQueueDragOver(object? sender, DragEventArgs e)
            {
                e.Effect = e.Data?.GetDataPresent(typeof(QueueRow)) == true
                    ? DragDropEffects.Move
                    : DragDropEffects.None;
            }

            private void OnQueueDragDrop(object? sender, DragEventArgs e)
            {
                if (e.Data?.GetData(typeof(QueueRow)) is not QueueRow row)
                    return;

                Point clientPoint = _list.PointToClient(new Point(e.X, e.Y));
                int targetIndex = _list.IndexFromPoint(clientPoint);
                int sourceIndex = _dragIndex;

                if (sourceIndex < 0)
                    sourceIndex = _list.Items.IndexOf(row);

                if (targetIndex < 0)
                {
                    targetIndex = _list.Items.Count - 1;
                }
                else
                {
                    try
                    {
                        var bounds = _list.GetItemRectangle(targetIndex);
                        bool dropAfter = clientPoint.Y > bounds.Top + (bounds.Height / 2);
                        if (dropAfter)
                            targetIndex++;
                    }
                    catch { }
                }

                if (sourceIndex >= 0 && sourceIndex < targetIndex)
                    targetIndex--;

                _reorder(row.Path, targetIndex);
                ReloadSnapshot(row.Path);
            }
        }

        private void AppendToPlaybackQueue(IEnumerable<string>? paths)
        {
            var additions = NormalizePlaybackQueuePaths(paths);
            if (additions.Count == 0)
                return;

            NormalizeActivePlaybackQueueToCurrent();

            int insertIndex = (_playbackQueueSessionActive && _playbackQueue.Count > 0) ? 1 : 0;
            string? firstAddedPath = null;

            foreach (var path in additions)
            {
                int existingIndex = _playbackQueue.FindIndex(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
                if (existingIndex == 0 && _playbackQueueSessionActive)
                    continue;

                string normalizedPath = path;
                if (existingIndex >= 0)
                {
                    normalizedPath = _playbackQueue[existingIndex];
                    _playbackQueue.RemoveAt(existingIndex);
                    if (existingIndex < insertIndex)
                        insertIndex = Math.Max(0, insertIndex - 1);
                }

                int historyIndex = _playbackQueueHistory.FindIndex(existing => string.Equals(existing, normalizedPath, StringComparison.OrdinalIgnoreCase));
                if (historyIndex >= 0)
                    _playbackQueueHistory.RemoveAt(historyIndex);

                if (insertIndex < 0)
                    insertIndex = 0;
                if (insertIndex > _playbackQueue.Count)
                    insertIndex = _playbackQueue.Count;

                _playbackQueue.Insert(insertIndex, normalizedPath);
                if (firstAddedPath == null)
                    firstAddedPath = normalizedPath;
                insertIndex++;
            }

            if (_playbackQueue.Count == 0)
                _playbackQueueIndex = -1;
            else if (!_playbackQueueSessionActive)
                _playbackQueueIndex = 0;

            RefreshPlaybackQueueUi(firstAddedPath);
        }

        private void RemoveFromPlaybackQueue(IEnumerable<string>? paths)
        {
            if ((_playbackQueue.Count == 0 && _playbackQueueHistory.Count == 0) || paths == null)
                return;

            var removal = new HashSet<string>(
                paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (removal.Count == 0)
                return;

            NormalizeActivePlaybackQueueToCurrent();
            bool removedCurrentHead = _playbackQueueSessionActive && _playbackQueue.Count > 0 && removal.Contains(_playbackQueue[0]);
            bool shouldDisableLoop = _singleTrackLoopEnabled
                && !string.IsNullOrWhiteSpace(_singleTrackLoopPath)
                && removal.Contains(_singleTrackLoopPath!);
            _playbackQueue.RemoveAll(p => removal.Contains(p));
            _playbackQueueHistory.RemoveAll(p => removal.Contains(p));

            if (shouldDisableLoop)
                SetSingleTrackLoop(null, false);

            if (_playbackQueue.Count == 0)
            {
                if (!_playbackQueueSessionActive || removedCurrentHead)
                {
                    ClearPlaybackQueue();
                    return;
                }

                _playbackQueueIndex = -1;
                _playbackQueueSessionActive = false;
                RefreshPlaybackQueueUi();
                return;
            }

            if (removedCurrentHead)
            {
                _playbackQueueIndex = 0;
                _playbackQueueSessionActive = true;
                RefreshPlaybackQueueUi(_playbackQueue[0]);
                return;
            }

            if (!_playbackQueueSessionActive)
            {
                if (_playbackQueueIndex < 0 || _playbackQueueIndex >= _playbackQueue.Count)
                    _playbackQueueIndex = 0;
            }
            else
            {
                _playbackQueueIndex = 0;
            }

            RefreshPlaybackQueueUi();
        }

        private void StartPlaybackQueue(IEnumerable<string>? paths, int startIndex, bool shuffle)
        {
            var queue = NormalizePlaybackQueuePaths(paths);
            if (queue.Count == 0)
                return;

            if (startIndex < 0)
                startIndex = 0;
            if (startIndex >= queue.Count)
                startIndex = queue.Count - 1;

            if (shuffle && queue.Count > 1)
            {
                string startPath = queue[startIndex];
                var tail = new List<string>(queue.Count - 1);
                for (int i = 0; i < queue.Count; i++)
                {
                    if (i == startIndex)
                        continue;
                    tail.Add(queue[i]);
                }

                ShufflePlaybackQueueInPlace(tail, _playbackQueueRandom);
                queue.Clear();
                queue.Add(startPath);
                queue.AddRange(tail);
            }
            else if (startIndex > 0)
            {
                queue = queue.Skip(startIndex).ToList();
            }

            _playbackQueue.Clear();
            _playbackQueue.AddRange(queue);
            _playbackQueueHistory.Clear();
            _playbackQueueIndex = _playbackQueue.Count > 0 ? 0 : -1;
            _playbackQueueShuffleMode = shuffle;
            _playbackQueueSessionActive = _playbackQueue.Count > 0;

            if (_playbackQueue.Count == 0)
            {
                RefreshPlaybackQueueUi();
                return;
            }

            string nextPath = _playbackQueue[0];
            RefreshPlaybackQueueUi(nextPath);
            _currentLibraryCategory = ResolveEffectiveLibraryCategoryForPath(nextPath) ?? _libraryPage?.SelectedCategory ?? _currentLibraryCategory;
            _suppressPreRollOnce = true;
            _suppressVideoLoadingOnce = true;
            ResetLibraryRemoteActivation(clearFocusRing: true);

            if (string.Equals(_currentPath?.Trim(), nextPath, StringComparison.OrdinalIgnoreCase))
            {
                try { HideLibrary(); } catch { }
                return;
            }

            _endTriggered = true;
            _endCandidateSinceUtc = DateTime.MinValue;
            try { HideLibrary(); } catch { }
            OpenPlaybackQueueItem(nextPath);
        }

        private bool TrySkipPlaybackQueue(int delta)
        {
            if (delta == 0 || _playbackQueue.Count == 0)
                return false;

            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count == 0)
                return false;

            if (_playbackQueueSessionActive)
            {
                if (delta > 0)
                {
                    string? currentHead = _playbackQueue.Count > 0 ? _playbackQueue[0] : null;
                    if (_playbackQueue.Count <= 1)
                    {
                        if (!string.IsNullOrWhiteSpace(currentHead))
                            _playbackQueueHistory.Add(currentHead);
                        _playbackQueue.Clear();
                        _playbackQueueIndex = -1;
                        _playbackQueueSessionActive = false;
                        RefreshPlaybackQueueUi();
                        try { CloseCurrentToLibrary(); } catch { }
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(currentHead))
                        _playbackQueueHistory.Add(currentHead);
                    _playbackQueue.RemoveAt(0);
                    _playbackQueueIndex = 0;
                    _playbackQueueSessionActive = true;
                    OpenPlaybackQueueHead();
                    return true;
                }

                if (_playbackQueueHistory.Count == 0)
                    return false;

                string previousPath = _playbackQueueHistory[_playbackQueueHistory.Count - 1];
                _playbackQueueHistory.RemoveAt(_playbackQueueHistory.Count - 1);
                _playbackQueue.RemoveAll(p => string.Equals(p, previousPath, StringComparison.OrdinalIgnoreCase));
                _playbackQueue.Insert(0, previousPath);
                _playbackQueueIndex = 0;
                _playbackQueueSessionActive = true;
                OpenPlaybackQueueHead();
                return true;
            }

            int currentIndex = GetPlaybackQueueCurrentIndex();
            if (currentIndex < 0)
                currentIndex = 0;

            int targetIndex = currentIndex + delta;
            if (targetIndex < 0 || targetIndex >= _playbackQueue.Count)
                return false;

            if (targetIndex > 0)
                _playbackQueue.RemoveRange(0, targetIndex);

            _playbackQueueHistory.Clear();
            _playbackQueueIndex = _playbackQueue.Count > 0 ? 0 : -1;
            _playbackQueueSessionActive = _playbackQueue.Count > 0;
            OpenPlaybackQueueHead();
            return true;
        }

        private bool TryAdvancePlaybackQueue()
        {
            if (!_playbackQueueSessionActive || _playbackQueue.Count == 0)
                return false;

            NormalizeActivePlaybackQueueToCurrent();
            if (_playbackQueue.Count == 0)
                return false;

            string currentHead = _playbackQueue[0];
            _playbackQueueHistory.Add(currentHead);
            _playbackQueue.RemoveAt(0);
            RemoveInvalidPlaybackQueueItems();
            if (_playbackQueue.Count == 0)
            {
                _playbackQueueSessionActive = false;
                _playbackQueueShuffleMode = false;
                _playbackQueueIndex = -1;
                RefreshPlaybackQueueUi();
                return false;
            }

            _playbackQueueIndex = 0;
            _playbackQueueSessionActive = true;
            OpenPlaybackQueueHead();
            return true;
        }

        private volatile bool _stopping;
        private VRChoice? _manualRendererChoice = VRChoice.MADVR;
        // === 3D → EVR forcing state ===
        private bool _hasSavedRendererFor3D = false;
        private VRChoice? _savedRendererFor3D = null;

        private async void OpenPath(string path, double resume = 0, bool startPaused = false, bool allowPlaceholderGate = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            bool queueTransitionRequested = _playbackQueueTransitionInProgress;

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

                // Passaggio demo → film: sopprimi splash + overlay di caricamento una sola volta.
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
                    // Stoppa eventuale playback attivo, ma NON tornare allo splash.
                    SafeStop(toSplash: false);
                    SkipLoadingIfActive();

                    await ShowPreOpenPlaceholderGateAsync(path, resume, startPaused, ct);

                    // Questa OpenPath è stata intenzionalmente "rimandata": rilascia CTS.
                    try { if (Interlocked.CompareExchange(ref _openCts, null, cts) == cts) { } } catch { }
                    try { cts.Dispose(); } catch { }
                    return;
                }

                SafeStop(toSplash: !suppressLoadingUi);
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
                _originalVideoName = ExtractOriginalVideoName(path);
                _bitstreamNow = false;
                _bitstreamLastTrue = DateTime.MinValue;
                _isLocalFile = false;
                _currentWebAudioUrl = null; // reset ad ogni nuova sorgente
                _currentMediaHasVideo = false;
                ResetAudioOverlayState();

                ShowVideoLoading("Caricamento…");
                // lascia respirare la UI (paint + timer spinner)
                await Task.Yield();

                bool? forceHasVideo = null;
                if (Uri.TryCreate(path, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    ShowVideoLoading("Risolvo URL…");
                    await Task.Yield();

                    try
                    {
                        var r = await _resolverSta.InvokeAsync(() => WebMediaResolver.Resolve(path), ct);

                        if (ct.IsCancellationRequested || mySerial != _openSerial) return;

                        if (r == null)
                        {
                            Debug.WriteLine($"[Resolver] {path} -> nessuna URL media diretta (resolver null)");
                            HideVideoLoading();
                            NotifyPlaybackStoppedForWled(forceRestore: true);
                            return;
                        }

                        Debug.WriteLine($"[Resolver] {path} -> {r.Value.Url} (audio={r.Value.AudioUrl ?? "-"}, forceVideo={r.Value.ForceHasVideo})");
                        path = r.Value.Url;
                        forceHasVideo = r.Value.ForceHasVideo;
                        _currentWebAudioUrl = r.Value.AudioUrl;   // <--- memorizza eventuale audio separato
                    }
                    catch
                    {
                        if (ct.IsCancellationRequested || mySerial != _openSerial) return;
                        Debug.WriteLine($"[Resolver] {path} -> eccezione durante la risoluzione");
                        HideVideoLoading();
                        NotifyPlaybackStoppedForWled(forceRestore: true);
                        return;
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
                    HideVideoLoading();
                    NotifyPlaybackStoppedForWled(forceRestore: true);
                    OpenImage(path);
                    return;
                }

                try
                {
                    ShowVideoLoading("Analizzo media…");
                    await Task.Yield();
                    _info = await Task.Run(() => MediaProbe.Probe(path), ct);
                }
                catch (Exception ex)
                {
                    _lblStatus.Text = "Probe fallito: " + ex.Message;
                    _info = null;
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

                bool fileHdr = _info?.IsHdr == true;
                bool hdmi = _selectedRendererLooksHdmi;
                bool passCandidate = _info != null && MediaProbe.IsPassthroughCandidate(_info.AudioCodec);

                // ⬅ se l’utente forza PCM, ignoriamo il flag "preferBitstream"
                bool wantBitstream = (_audioOutPref == AudioOutPref.Auto) && (_preferBitstreamUi && hdmi && passCandidate);
                bool forcePcmToggle = _audioOutPref == AudioOutPref.ForcePcm;

                var order =
                    _manualRendererChoice.HasValue
                    ? new[] { _manualRendererChoice.Value }
                    : (fileHdr ? ORDER_HDR : ORDER_SDR);

                Dbg.Log($"OpenPath '{path}', HDR_File={fileHdr}, UI_HDR={_hdr}, hasVideo={hasVideo}, wantBitstream={wantBitstream}, order=[{string.Join(",", order)}]");

                foreach (var choice in order)
                {
                    try
                    {
                        ShowVideoLoading($"Apro stream ({choice})…");
                        await Task.Yield();
                        if (ct.IsCancellationRequested || mySerial != _openSerial) return;

                        _stopping = false;
                        _engine = new DirectShowUnifiedEngine(
                            preferBitstream: wantBitstream,
                            forcePcmToggle: forcePcmToggle,
                            preferredRendererName: _selectedAudioRendererName,
                            choice: choice,
                            fileIsHdr: fileHdr,
                            srcAudioCodec: _info?.AudioCodec ?? AVCodecID.AV_CODEC_ID_NONE);

                        _engineStatusHandler = s => { if (_stopping) return; BeginInvoke(new Action(() => _lblStatus.Text = s)); };
                        _engineProgressHandler = s => { if (_stopping) return; BeginInvoke(new Action(() => OnEngineProgress(s))); };
                        _engineUpdateHandler = () =>
                        {
                            if (_stopping) return;
                            if (IsHandleCreated) BeginInvoke(new Action(() =>
                            {
                                UpdateVideoWindowForCurrentHost();
                                SyncOverlayToVideoRect();
                                if (_info != null && _engine != null)
                                {
                                    var chosen = _manualRendererChoice ?? (fileHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                                    UpdateInfoOverlay(chosen, fileHdr);
                                }
                                BringOverlaysToFront();
                            }));
                        };

                        _engine.OnStatus += _engineStatusHandler;
                        _engine.OnProgressSeconds += _engineProgressHandler;
                        _engine.BindUpdateCallback(_engineUpdateHandler);

                        _engine.OnBitstreamChanged += OnEngineBitstreamChanged;

                        UseOverlayInline(false); // SEMPRE host layered

                        if (!string.IsNullOrEmpty(_currentWebAudioUrl))
                        {
                            try
                            {
                                var tEngine = _engine.GetType();
                                var mExtAudio = tEngine.GetMethod(
                                    "SetExternalAudioUrl",
                                    System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic);

                                if (mExtAudio != null)
                                {
                                    mExtAudio.Invoke(_engine, new object[] { _currentWebAudioUrl! });
                                    Debug.WriteLine($"[Resolver] Passata external audio URL all'engine: {_currentWebAudioUrl}");
                                }
                            }
                            catch
                            { }
                        }

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

                        _engine.Open(path, hasVideo);

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

                        if (resume > 0 && _duration > 0)
                        {
                            try { _engine.PositionSeconds = Math.Min(resume, Math.Max(0.01, _duration)); } catch { }
                        }

                        bool hasDisplay = _engine.HasDisplayControl();
                        if (!hasVideo)
                        {
                            StartAudioMetersIfPossible();
                        }
                        else
                        {
                            StopAudioMeters();
                            ResetAudioOverlayState();
                        }

                        _duration = _engine.DurationSeconds > 0 ? _engine.DurationSeconds : (_info?.Duration ?? 0);

                        _splash.Visible = false;
                        BringOverlaysToFront();

                        try { _thumbCts?.Cancel(); } catch { }
                        try { _previewCache.Clear(); } catch { }
                        try { _thumb.Open(path); } catch { }
                        try
                        {
                            // Ora usiamo sempre PacketRateSampler anche per HTTP/HTTPS.
                            // Nota: su YouTube (DASH) audio può essere separato.
                            _pktRateOk = _pktRate.Open(path);
                            _pktRateAudioOk = false;

                            if (!string.IsNullOrEmpty(_currentWebAudioUrl))
                                _pktRateAudioOk = _pktRateAudio.Open(_currentWebAudioUrl!);

                            _lastPktSample = DateTime.MinValue;
                            _aNowTs = DateTime.MinValue;
                            _vNowTs = DateTime.MinValue;
                        }
                        catch
                        {
                            try { _pktRate.Dispose(); } catch { }
                            _pktRate = new PacketRateSampler(); // ✅ evita riuso di un oggetto disposed
                            try { _pktRateAudio.Dispose(); } catch { }
                            _pktRateAudio = new PacketRateSampler();
                            _pktRateOk = false;
                            _pktRateAudioOk = false;
                            _lastPktSample = DateTime.MinValue;
                        }

                        _engine.SetStereo3D(_stereo);
                        UpdateVideoWindowForCurrentHost();

                        SafeShowOverlayHost();
                        SyncOverlayToVideoRect();
                        BringOverlaysToFront();

                        AutoSelectDefaultStreams();

                        UpdateInfoOverlay(choice, fileHdr);

                        // All'ingresso del contenuto non vogliamo HUD automatico:
                        // comparirà solo su input reale dell'utente / seek da remote.
                        SuppressHudForProgrammaticTransition(1200);

                        _paused = startPaused;

                        _hasOpenedMediaOnce = true;

                        try { if (!startPaused) _engine.Play(); else _engine.Pause(); } catch { }
                        ApplyAmbientLightingForCurrentState();

                        ApplyVolume(1f);

                        if (FormBorderStyle != FormBorderStyle.None) ToggleFullscreen();

                        var t = new System.Windows.Forms.Timer { Interval = 300 };
                        t.Tick += (_, __) =>
                        {
                            try
                            {
                                UpdateVideoWindowForCurrentHost();
                                SyncOverlayToVideoRect();
                                BringOverlaysToFront();
                                SuppressHudForProgrammaticTransition(600);
                            }
                            catch { }
                            finally { t.Stop(); t.Dispose(); }
                        };
                        t.Start();

                        bool okDisplay = _engine.HasDisplayControl();
                        if (!hasVideo || okDisplay)
                        {
                            string tag = fileHdr ? "HDR" : "SDR";
                            _lblStatus.Text = (!hasVideo)
                                ? "Riproduzione (solo audio)"
                                : $"Riproduzione ({choice} • {tag})";
                            HideVideoLoading();
                            return;
                        }

                        throw new Exception("Renderer non pronto (nessun display control) → fallback");
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn($"OpenPath: renderer {choice} EX: " + ex.Message);
                        try { _engine?.Dispose(); } catch { }
                        _engine = null;

                        if (_manualRendererChoice == VideoRendererChoice.MADVR &&
                            (ex.Message?.IndexOf("madVR non trovato", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            _lblStatus.Text = "madVR non installato. Esegui 'install.bat' come Amministratore nella cartella di madVR, poi riprova.";
                        }
                    }
                }

                _lblStatus.Text = "Impossibile presentare il video con i renderer selezionati";
                HideVideoLoading();
                NotifyPlaybackStoppedForWled(forceRestore: true);
            }
            finally
            {
                if (queueTransitionRequested || _playbackQueueTransitionInProgress)
                    _playbackQueueTransitionInProgress = false;
            }
        }

        private void OpenImage(string path)
        {
            _currentPath = path;
            _info = null;
            _stopping = false;
            _duration = 0;
            _paused = true;

            // Playlist immagini: tutte le immagini nella stessa cartella
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _imageFiles = Directory.EnumerateFiles(dir)
                        .Where(ImagePlaybackEngine.IsImageFile)
                        .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    _imageIndex = _imageFiles.FindIndex(f =>
                        string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                    if (_imageFiles.Count > 0 && _imageIndex < 0)
                    {
                        _imageIndex = 0;
                    }
                }
                else
                {
                    _imageFiles = new List<string> { path };
                    _imageIndex = 0;
                }
            }
            catch
            {
                _imageFiles = new List<string> { path };
                _imageIndex = 0;
            }

            // niente bitstream
            try { _thumbCts?.Cancel(); } catch { }
            try { _previewCache.Clear(); } catch { }
            try { _thumb.Open(path); } catch { }

            // riusa engine se già ImagePlaybackEngine, altrimenti creane uno
            if (_engine is ImagePlaybackEngine imgEngine)
            {
                imgEngine.Open(path, hasVideo: true);
            }
            else
            {
                try { _engine?.Dispose(); } catch { }
                _engine = new ImagePlaybackEngine();

                _engineStatusHandler = s =>
                {
                    if (_stopping) return;
                    if (!IsHandleCreated) return;
                    try { BeginInvoke(new Action(() => _lblStatus.Text = s)); } catch { }
                };

                _engineUpdateHandler = () =>
                {
                    if (_stopping) return;
                    if (!IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
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
                _engine!.Open(path, hasVideo: true);
            }

            _splash.Visible = false;
            StopAudioMeters();
            _audioOnlyBanner.Visible = false;

            SafeShowOverlayHost();
            SyncOverlayToVideoRect();

            // HUD classica completamente OFF in modalità foto
            _hud.Visible = false;
            _hud.TimelineVisible = false;
            _photoHud.Visible = true;
            try { _photoHud.Wake(); } catch { }
            BringOverlaysToFront();

            _hasOpenedMediaOnce = true;

            // Info overlay opzionale (lasciata, ma parte nascosta)
            try { UpdateInfoOverlay(VRChoice.EVR, fileHdr: false); } catch { }

            _lblStatus.Text = "Immagine: " + Path.GetFileName(path);
        }

        private static bool LooksLikeVideoByExt(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return new[] { ".mkv", ".mp4", ".m2ts", ".ts", ".mov", ".avi", ".wmv", ".webm", ".mts" }.Contains(ext);
        }
        private static bool LooksLikePureAudioByExt(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();

            switch (ext)
            {
                case ".mp3":
                case ".flac":
                case ".wav":
                case ".ogg":
                case ".opus":
                case ".m4a":
                case ".aac":
                case ".wma":
                case ".alac":
                case ".ape":
                    return true;

                default:
                    return false;
            }
        }

        private void AutoSelectDefaultStreams()
        {
            if (_engine == null) return;
            try
            {
                var streams = _engine.EnumerateStreams().ToList();
                var audioStreams = streams.Where(s => s.IsAudio).ToList();
                var subtitleStreams = streams.Where(s => s.IsSubtitle).ToList();

                var selAudio = audioStreams.FirstOrDefault(s => s.Selected)
                               ?? audioStreams.FirstOrDefault(s => string.Equals(DetectLangKeyFromName(s.Name), "it", StringComparison.OrdinalIgnoreCase))
                               ?? audioStreams.FirstOrDefault();
                if (selAudio != null)
                    _engine.EnableByGlobalIndex(selAudio.GlobalIndex);

                var preferredLang = DetectLangKeyFromName(selAudio?.Name) ?? _preferredSubtitleLangKey;
                if (!string.IsNullOrWhiteSpace(preferredLang))
                    _preferredSubtitleLangKey = preferredLang;

                _subtitleAutoForcedMode = false;

                if (subtitleStreams.Count == 0)
                    return;

                bool forcedFound = TrySelectAutoForcedSubtitles(_engine, subtitleStreams, preferredLang)
                    && _engine.EnumerateStreams().Any(s => s.IsSubtitle && s.Selected && IsAutoForcedSubtitleName(s.Name));
                if (forcedFound)
                {
                    _subtitleAutoForcedMode = true;
                    return;
                }

                var explicitChoice = subtitleStreams.FirstOrDefault(s => s.Selected && !IsAutoForcedSubtitleName(s.Name))
                    ?? subtitleStreams.FirstOrDefault(s => !IsAutoForcedSubtitleName(s.Name) && string.Equals(DetectLangKeyFromName(s.Name), preferredLang, StringComparison.OrdinalIgnoreCase))
                    ?? subtitleStreams.FirstOrDefault(s => !IsAutoForcedSubtitleName(s.Name) && string.Equals(DetectLangKeyFromName(s.Name), "it", StringComparison.OrdinalIgnoreCase))
                    ?? subtitleStreams.FirstOrDefault(s => !IsAutoForcedSubtitleName(s.Name) && string.Equals(DetectLangKeyFromName(s.Name), "en", StringComparison.OrdinalIgnoreCase))
                    ?? subtitleStreams.FirstOrDefault(s => !IsAutoForcedSubtitleName(s.Name));

                if (explicitChoice != null)
                {
                    _engine.EnableByGlobalIndex(explicitChoice.GlobalIndex);
                    var explicitLang = DetectLangKeyFromName(explicitChoice.Name);
                    if (!string.IsNullOrWhiteSpace(explicitLang))
                        _preferredSubtitleLangKey = explicitLang;
                }
                else
                {
                    _engine.DisableSubtitlesIfPossible();
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("AutoSelectDefaultStreams: " + ex.Message);
            }
        }

        private static (int W, int H, string Label) NormalizeViewport(int w, int h)
        {
            if (w <= 0 || h <= 0) return (w, h, $"{w}x{h}");
            var cand = new (int W, int H, string Label)[]
            {
                (3840,2160,"3840x2160"), (4096,2160,"4096x2160"), (2560,1440,"2560x1440"),
                (1920,1080,"1920x1080"), (1600,900,"1600x900"), (1280,720,"1280x720"),
            };
            foreach (var c in cand)
            {
                double dw = Math.Abs(w - c.W) / (double)c.W;
                double dh = Math.Abs(h - c.H) / (double)c.H;
                if (dw <= 0.02 && dh <= 0.02) return c;
            }
            return (w, h, $"{w}x{h}");
        }
        private static string FmtKbps(int kbps) => kbps > 0 ? $"{kbps:n0} kbps" : "n/d";
        private static double GetVideoFps(MediaProbe.Result? info)
        {
            if (info == null) return 0;

            try
            {
                var t = info.GetType();

                object? GetMemberValue(string[] names)
                {
                    const BindingFlags flags =
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.IgnoreCase;

                    foreach (var name in names)
                    {
                        var p = t.GetProperty(name, flags);
                        if (p != null)
                            return p.GetValue(info);

                        var f = t.GetField(name, flags);
                        if (f != null)
                            return f.GetValue(info);
                    }
                    return null;
                }

                // 1) proprietà/field numerici "classici"
                var numNames = new[]
                {
                    "VideoFps", "VideoFPS",
                    "Fps", "FPS",
                    "VideoFrameRate", "FrameRate",
                    "FrameRateDouble"
                };

                var rawNum = GetMemberValue(numNames);
                double val = ToDouble(rawNum);
                if (val > 0.1 && val < 1000) return val;

                // 2) stringhe tipo "24000/1001" / "23.976"
                var strNames = new[]
                {
                    "AvgFrameRate", "AverageFrameRate",
                    "RFrameRate",
                    "VideoAvgFrameRate", "VideoAverageFrameRate",
                    "VideoRFrameRate"
                };

                var rawStr = GetMemberValue(strNames);
                if (rawStr is string s)
                {
                    double f = ParseFpsString(s);
                    if (f > 0.1 && f < 1000) return f;
                }

                // 3) coppie numeratore/denominatore
                double num = 0, den = 0;

                var numProps = new[] { "FrameRateNum", "FrameRateNumerator", "RFrameRateNum", "VideoFrameRateNum" };
                var denProps = new[] { "FrameRateDen", "FrameRateDenominator", "RFrameRateDen", "VideoFrameRateDen" };

                var rawNum2 = GetMemberValue(numProps);
                var rawDen2 = GetMemberValue(denProps);

                num = ToDouble(rawNum2);
                den = ToDouble(rawDen2);

                if (num > 0 && den > 0)
                {
                    double f = num / den;
                    if (f > 0.1 && f < 1000) return f;
                }
            }
            catch
            {
                // best-effort, niente eccezioni da qui
            }

            return 0;

            static double ToDouble(object? v)
            {
                if (v == null) return 0;

                // gestisce anche int, long, float, double, ecc.
                if (v is IConvertible conv)
                {
                    try { return conv.ToDouble(System.Globalization.CultureInfo.InvariantCulture); }
                    catch { }
                }

                if (v is double d) return d;
                if (v is float f) return f;
                if (v is int i) return i;
                if (v is long l) return l;
                return 0;
            }

            static double ParseFpsString(string s)
            {
                s = s.Trim();
                if (s.Length == 0) return 0;

                // tipo "24000/1001"
                int slash = s.IndexOf('/');
                if (slash > 0)
                {
                    var numStr = s[..slash];
                    var denStr = s[(slash + 1)..];

                    if (double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out var num) &&
                        double.TryParse(denStr, System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out var den) &&
                        den != 0)
                    {
                        return num / den;
                    }
                }

                // tipo "23.976"
                if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var val))
                    return val;

                return 0;
            }
        }

        // ======= INFO OVERLAY =======
        private void UpdateInfoOverlay(VRChoice renderer, bool fileHdr)
        {
            if (_engine == null) return;

            // Video OUT (negoziato) + fps
            int outW = 0, outH = 0;

            try
            {
                var negotiated = _engine.GetNegotiatedVideoFormat(); // (int width, int height, string subtype)
                outW = negotiated.Item1;
                outH = negotiated.Item2;
            }
            catch
            {
                // se l'engine non fornisce ancora il formato negoziato, lasceremo i default
            }

            // se madVR/MPCVR non ci dà w/h (windowed) → usa la risoluzione del monitor
            if (outW <= 0 || outH <= 0)
            {
                try
                {
                    var screen = Screen.FromControl(this);
                    outW = screen.Bounds.Width;
                    outH = screen.Bounds.Height;
                }
                catch
                {
                    // fallback finale: viewport del controllo video (solo se proprio non c'è altro)
                    outW = _videoHost.ClientSize.Width;
                    outH = _videoHost.ClientSize.Height;
                }
            }

            // fps dal probe (container)
            double fps = GetVideoFps(_info);
            string fpsStr = fps > 0
                ? (Math.Abs(fps - 23.976) < 0.01 ? "23.976" :
                   Math.Abs(fps - 29.97) < 0.01 ? "29.970" :
                   fps.ToString("0.###"))
                : "n/d";

            var norm = NormalizeViewport(outW, outH);
            outW = norm.W;
            outH = norm.H;
            string outStr = $"{norm.Label} • {fpsStr} fps";

            // Stima bitrate medio dal container (fallback)
            int avgContainerKbps = 0;
            try
            {
                if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath) && _duration > 1)
                {
                    var fi = new FileInfo(_currentPath);
                    avgContainerKbps = (int)Math.Round((fi.Length * 8.0 / 1000.0) / _duration);
                }
            }
            catch { }

            // ===== Audio da LAV Audio (IN/OUT + bitstream + kbpsNow) =====
            var selAudio = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
            string selName = selAudio?.Name ?? "";
            var lav = GetLavAudioIODetails(selName);
            bool bitstream = IsBitstream();
            if (_audioOutPref == AudioOutPref.ForcePcm)
                bitstream = false;
            bool engineHasVideo = _engine.HasDisplayControl();

            // "ora" audio: SOLO misura live (niente fallback su probe per evitare numeri statici)
            int audioNowKbps = _audioBitrateNowKbps > 0
                ? _audioBitrateNowKbps
                : ParseKbpsFromName(selName);

            // === MEDIE: usa la media live pubblicata ogni 10s; se 0, fallback ai metadata ===
            int audioAvgKbps = (_audioAvgLiveKbps > 0) ? (int)Math.Round(_audioAvgLiveKbps) : 0;
            int videoAvgKbps = (_videoAvgLiveKbps > 0) ? (int)Math.Round(_videoAvgLiveKbps) : 0;

            if (audioAvgKbps <= 0)
            {
                if (lav.AudioNowKbps > 0) audioAvgKbps = lav.AudioNowKbps;
                if (audioAvgKbps <= 0 && !string.IsNullOrWhiteSpace(selName))
                    audioAvgKbps = ParseKbpsFromName(selName);
                if (audioAvgKbps <= 0)
                    audioAvgKbps = ProbeAudioAvgKbps();
                if (audioAvgKbps <= 0 && avgContainerKbps > 0)
                    audioAvgKbps = (int)(avgContainerKbps * 0.30);
            }

            if (videoAvgKbps <= 0)
            {
                if (avgContainerKbps > 0 && audioAvgKbps > 0)
                    videoAvgKbps = Math.Max(0, avgContainerKbps - audioAvgKbps);
                else if (avgContainerKbps > 0)
                    videoAvgKbps = (int)(avgContainerKbps * 0.70);
            }

            // Se non abbiamo ancora campioni live, usa i fallback esistenti
            if (audioAvgKbps <= 0)
            {
                // 1) dal graph corrente (PCM calcolato o payload stimato da LAV)
                if (lav.AudioNowKbps > 0) audioAvgKbps = lav.AudioNowKbps;

                // 2) dal nome traccia ("xxx kb/s")
                if (audioAvgKbps <= 0 && !string.IsNullOrWhiteSpace(selName))
                    audioAvgKbps = ParseKbpsFromName(selName);

                // 3) dal probe (se disponibile)
                if (audioAvgKbps <= 0)
                    audioAvgKbps = ProbeAudioAvgKbps();

                // 4) heuristico dal container
                if (audioAvgKbps <= 0 && avgContainerKbps > 0)
                    audioAvgKbps = (int)(avgContainerKbps * 0.30);
            }

            if (videoAvgKbps <= 0)
            {
                if (avgContainerKbps > 0 && audioAvgKbps > 0)
                    videoAvgKbps = Math.Max(0, avgContainerKbps - audioAvgKbps);
                else if (avgContainerKbps > 0)
                    videoAvgKbps = (int)(avgContainerKbps * 0.70);
            }

            // Video "ora": calcolato altrove come residuo container-now – audio-now
            int videoNowKbps = _videoBitrateNowKbps > 0 ? _videoBitrateNowKbps : 0;

            if (!engineHasVideo)
            {
                videoNowKbps = 0;
                videoAvgKbps = 0;
            }

            string hdrTag = fileHdr ? "HDR" : "SDR";

            // Audio IN/OUT prettificato
            string audioIn = !string.IsNullOrWhiteSpace(lav.InDetail) && lav.InDetail != "n/d"
                ? lav.InDetail
                : PrettyAudioInFromProbe(_info);

            var s = new InfoOverlay.Stats
            {
                Title = Path.GetFileName(_currentPath ?? "") ?? "—",

                VideoIn = _info != null
                    ? $"{_info.Width}x{_info.Height} • {fpsStr} fps"
                      + $" • {CodecName(_info.VideoCodec)} • {(_info.VideoBits > 0 ? _info.VideoBits + "-bit" : "8-bit?")}"
                    : "n/d",
                VideoOut = outStr,
                VideoCodec = _info != null ? CodecName(_info.VideoCodec) : "n/d",
                VideoPrimaries = _info != null ? PrimName(_info.Primaries) : "n/d",
                VideoTransfer = _info != null ? TrcName(_info.Transfer) : "n/d",

                VideoBitrateNow = videoNowKbps > 0 ? FmtKbps(videoNowKbps) : "n/d",
                VideoBitrateAvg = videoAvgKbps > 0 ? FmtKbps(videoAvgKbps) : "n/d",

                AudioIn = string.IsNullOrWhiteSpace(audioIn) ? "n/d" : audioIn,
                AudioOut = lav.OutDetail,
                AudioBitrateNow = audioNowKbps > 0 ? FmtKbps(audioNowKbps) : "n/d",
                AudioBitrateAvg = audioAvgKbps > 0 ? FmtKbps(audioAvgKbps) : "n/d",

                Renderer = renderer.ToString() + ((_enableUpscaling && renderer == VRChoice.MADVR) ? " (madVR upscaler)" : ""),
                HdrMode = hdrTag,
                Upscaling = _enableUpscaling && renderer == VRChoice.MADVR,
                Bitstream = bitstream,
                RtxHdr = false
            };

            _infoOverlay.SetStats(s);

            static string CodecName(AVCodecID id) => id switch
            {
                AVCodecID.AV_CODEC_ID_HEVC => "HEVC",
                AVCodecID.AV_CODEC_ID_H264 => "H.264",
                AVCodecID.AV_CODEC_ID_VP9 => "VP9",
                AVCodecID.AV_CODEC_ID_AV1 => "AV1",
                AVCodecID.AV_CODEC_ID_TRUEHD => "Dolby TrueHD",
                AVCodecID.AV_CODEC_ID_EAC3 => "Dolby Digital Plus",
                AVCodecID.AV_CODEC_ID_AC3 => "Dolby Digital",
                AVCodecID.AV_CODEC_ID_DTS => "DTS",
                _ => id.ToString().Replace("AV_CODEC_ID_", "")
            };
            static string PrimName(AVColorPrimaries p) =>
                p == AVColorPrimaries.AVCOL_PRI_BT2020 ? "BT.2020" :
                p == AVColorPrimaries.AVCOL_PRI_BT709 ? "BT.709" :
                p == AVColorPrimaries.AVCOL_PRI_SMPTE170M ? "SMPTE 170M" :
                p.ToString().Replace("AVCOL_PRI_", "");
            static string TrcName(AVColorTransferCharacteristic t) =>
                t == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 ? "PQ" :
                t == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 ? "HLG" :
                t == AVColorTransferCharacteristic.AVCOL_TRC_BT709 ? "BT.709" :
                t.ToString().Replace("AVCOL_TRC_", "");
        }

        // --- helper locali ---
        private static string PrettyChannels(int ch)
        {
            return ch switch
            {
                1 => "1.0",
                2 => "2.0",
                3 => "2.1",
                4 => "4.0",
                5 => "4.1",
                6 => "5.1",
                7 => "6.1",
                8 => "7.1",
                _ => $"{ch}ch"
            };
        }

        private static string LocalCodecName(AVCodecID id) => id switch
        {
            AVCodecID.AV_CODEC_ID_TRUEHD => "Dolby TrueHD",
            AVCodecID.AV_CODEC_ID_EAC3 => "Dolby Digital Plus",
            AVCodecID.AV_CODEC_ID_AC3 => "Dolby Digital",
            AVCodecID.AV_CODEC_ID_DTS => "DTS",
            AVCodecID.AV_CODEC_ID_FLAC => "FLAC",
            AVCodecID.AV_CODEC_ID_AAC => "AAC",
            _ => id.ToString().Replace("AV_CODEC_ID_", "")
        };

        private string PrettyAudioInFromProbe(MediaProbe.Result? r)
        {
            if (r == null || r.AudioCodec == 0) return "n/d";
            string c = !string.IsNullOrWhiteSpace(r.AudioCodecDisplayName)
                ? r.AudioCodecDisplayName
                : LocalCodecName(r.AudioCodec);
            string ch = (!r.AudioLooksObjectBased && r.AudioChannels > 0)
                ? " • " + PrettyChannels(r.AudioChannels)
                : "";
            string sr = r.AudioRate > 0 ? $" • {r.AudioRate / 1000.0:0.#} kHz" : "";
            return c + ch + sr;
        }
        private bool TryGetLavInAvgBytesPerSec(out int avgBps)
        {
            avgBps = 0;
            try
            {
                if (!TryGetFilterGraph(out var fg) || fg == null) return false;
                if (!TryFindFilter(fg, "LAV Audio", out var lav) || lav == null) return false;

                if (lav.EnumPins(out IEnumPins? ep) != 0 || ep == null) return false;
                var pins = new IPin[1];

                while (ep.Next(1, pins, IntPtr.Zero) == 0)
                {
                    var p = pins[0];
                    p.QueryPinInfo(out var pi);
                    try
                    {
                        if (pi.dir == PinDirection.Input)
                        {
                            var mt = new AMMediaType();
                            if (p.ConnectionMediaType(mt) == 0)
                            {
                                try
                                {
                                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                                    {
                                        var wfx = Marshal.PtrToStructure<Engines.WaveFormatEx>(mt.formatPtr);
                                        if (wfx.nAvgBytesPerSec > 0)
                                        {
                                            avgBps = (int)wfx.nAvgBytesPerSec;
                                            return true;
                                        }
                                    }
                                }
                                finally { DsUtils.FreeAMMediaType(mt); }
                            }
                        }
                    }
                    finally
                    {
                        if (pi.filter != null) Marshal.ReleaseComObject(pi.filter);
                        Marshal.ReleaseComObject(p);
                    }
                }
            }
            catch { }
            return false;
        }

        // ======= LAV Audio I/O Details (unica fonte per overlay audio) =======
        private (string InDetail, string OutDetail, bool Bitstream, int AudioNowKbps) GetLavAudioIODetails(string? selectedStreamName)
        {
            string inStr = "n/d";
            string outStr = "n/d";
            bool bitstream = IsBitstream(); // unica fonte di verità
            int kbpsNow = 0;

            try
            {
                // 1) prova a ottenere direttamente il filtro LAV Audio dall’engine
                IBaseFilter? lavAudio = null;
                try
                {
                    var t = _engine?.GetType();
                    if (t != null)
                    {
                        var pLav = t.GetProperty("LavAudioFilter",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (pLav?.GetValue(_engine) is IBaseFilter lav) lavAudio = lav;
                    }
                }
                catch { /* best-effort */ }

                // 2) fallback: cerca "LAV Audio" nel graph
                IFilterGraph2? fg = null;
                if (lavAudio == null)
                {
                    if (!TryGetFilterGraph(out fg) || fg == null) return (inStr, outStr, bitstream, kbpsNow);
                    if (!TryFindFilter(fg, "LAV Audio", out lavAudio) || lavAudio == null) return (inStr, outStr, bitstream, kbpsNow);
                }

                // 3) pin in/out connessi
                if (!TryGetLavPinsConnected(lavAudio, out var pinIn, out var pinOut))
                    return (inStr, outStr, bitstream, kbpsNow);

                AMMediaType mtIn = new AMMediaType();
                AMMediaType mtOut = new AMMediaType();
                AMMediaType mtDown = new AMMediaType();

                try
                {
                    // IN (a LAV)
                    if (pinIn != null && pinIn.ConnectionMediaType(mtIn) == 0)
                        inStr = PrettyFromIn(mtIn, selectedStreamName);

                    // OUT (da LAV) e DOWNSTREAM (ingresso renderer)
                    string detail = "n/d";
                    AMMediaType? mtChosen = null;

                    bool haveOut = (pinOut != null && pinOut.ConnectionMediaType(mtOut) == 0);
                    (bool? outIsPcm, string? outPretty) = (null, null);
                    if (haveOut)
                    {
                        var clsOut = ClassifyByWave(mtOut);
                        outIsPcm = clsOut?.isPcm;
                        outPretty = clsOut?.pretty;
                        (_, string detailOut) = PrettyOutFromLav(mtOut, selectedStreamName);
                        detail = detailOut;
                        mtChosen = mtOut;
                    }

                    (bool haveDown, bool? downIsPcm, string? downPretty) = (false, null, null);
                    if (pinOut != null && pinOut.ConnectedTo(out IPin? rIn) == 0 && rIn != null)
                    {
                        try
                        {
                            if (rIn.ConnectionMediaType(mtDown) == 0)
                            {
                                haveDown = true;
                                var clsDown = ClassifyByWave(mtDown);
                                downIsPcm = clsDown?.isPcm;
                                downPretty = clsDown?.pretty;

                                (_, string detailDown) = PrettyOutFromLav(mtDown, selectedStreamName);

                                // Se siamo in PCM: preferisci SEMPRE il downstream (canali reali del device)
                                if (downIsPcm == true)
                                {
                                    detail = detailDown;
                                    mtChosen = mtDown;
                                }
                                else
                                {
                                    // Bitstream o caso non PCM → scegli il più specifico come prima
                                    detail = PreferMoreSpecific(detailDown, detail);
                                    mtChosen = (detail == detailDown) ? mtDown : mtChosen;
                                }
                            }
                        }
                        finally { Marshal.ReleaseComObject(rIn); }
                    }

                    // Componi OutDetail coerente col flag dell’engine
                    if (bitstream)
                    {
                        string pretty = detail;
                        if (string.IsNullOrWhiteSpace(pretty) || pretty.Equals("n/d", StringComparison.OrdinalIgnoreCase))
                            pretty = "IEC61937";
                        if (!pretty.StartsWith("Bitstream", StringComparison.OrdinalIgnoreCase))
                            outStr = $"Bitstream ({pretty})";
                        else
                            outStr = pretty;
                    }
                    else
                    {
                        // Se non abbiamo ancora scelto, prova a preferire mtDown se PCM, altrimenti mtOut
                        if (mtChosen == null)
                        {
                            if (haveDown && downIsPcm == true) mtChosen = mtDown;
                            else if (haveOut && outIsPcm == true) mtChosen = mtOut;
                        }

                        if (mtChosen != null)
                        {
                            var (_, rate, ch, bps, vbits, _) = ReadWave(mtChosen);
                            int validBits = vbits > 0 ? vbits : bps;

                            string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                            string chStr = ch > 0 ? $"{ch}ch" : "n/d";

                            string bitStr = "n/d";
                            if (validBits > 0)
                            {
                                bitStr = (vbits > 0 && bps > 0 && vbits != bps)
                                    ? $"{vbits}-bit (in {bps}-bit)"
                                    : $"{validBits}-bit";
                            }

                            outStr = $"PCM {rateStr} • {bitStr} • {chStr}";
                        }
                        else
                        {
                            outStr = "PCM";
                        }
                    }

                    // ===== Bitrate "ora" =====
                    if (!bitstream && mtChosen != null)
                    {
                        var (tag, rate, ch, bps, _, avgBytes) = ReadWave(mtChosen);

                        if (avgBytes > 0)
                        {
                            // ✅ data rate reale
                            kbpsNow = (int)Math.Round(avgBytes * 8 / 1000.0);
                        }
                        else
                        {
                            // fallback: usa container bits (non valid bits)
                            int containerBits = bps;
                            if (tag == 3 && containerBits <= 0) containerBits = 32; // float
                            if (rate > 0 && ch > 0 && containerBits > 0)
                                kbpsNow = (int)Math.Round(rate * containerBits * ch / 1000.0);
                        }
                    }
                    else
                    {
                        // BITSTREAM: stima il payload (non il trasporto IEC61937)
                        kbpsNow = ProbeAudioAvgKbps();

                        if (kbpsNow <= 0)
                        {
                            // se è AC-3/E-AC3/DTS core prova nAvgBytesPerSec dell’IN di LAV
                            bool likelyCore =
                                (inStr.IndexOf("Dolby Digital Plus", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (inStr.IndexOf("Dolby Digital", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (inStr.IndexOf("DTS-HD", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 inStr.IndexOf("DTS", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (likelyCore && TryGetLavInAvgBytesPerSec(out int avgBps) && avgBps > 0)
                                kbpsNow = (int)Math.Round(avgBps * 8 / 1000.0);
                        }

                        if (kbpsNow <= 0)
                            kbpsNow = ParseKbpsFromName(selectedStreamName);
                    }
                }
                finally
                {
                    try { DsUtils.FreeAMMediaType(mtIn); } catch { }
                    try { DsUtils.FreeAMMediaType(mtOut); } catch { }
                    try { DsUtils.FreeAMMediaType(mtDown); } catch { }
                    try { if (pinIn != null) Marshal.ReleaseComObject(pinIn); } catch { }
                    try { if (pinOut != null) Marshal.ReleaseComObject(pinOut); } catch { }
                }
            }
            catch { /* lascia n/d */ }

            return (inStr, outStr, bitstream, kbpsNow);

            // ----------------- Helpers locali -----------------

            static bool TryGetLavPinsConnected(IBaseFilter lav, out IPin? pinIn, out IPin? pinOut)
            {
                pinIn = null; pinOut = null;
                if (lav.EnumPins(out IEnumPins? ep) != 0 || ep == null) return false;
                var pins = new IPin[1];
                while (ep.Next(1, pins, IntPtr.Zero) == 0)
                {
                    var p = pins[0];
                    p.QueryPinInfo(out var pi);
                    try
                    {
                        if (p.ConnectedTo(out IPin? other) == 0 && other != null)
                        {
                            if (pi.dir == PinDirection.Input && pinIn == null) pinIn = p;
                            if (pi.dir == PinDirection.Output && pinOut == null) pinOut = p;
                            Marshal.ReleaseComObject(other);
                            if (pinIn != null && pinOut != null) return true;
                        }
                        else
                        {
                            Marshal.ReleaseComObject(p);
                        }
                    }
                    finally
                    {
                        if (pi.filter != null) Marshal.ReleaseComObject(pi.filter);
                    }
                }
                return pinIn != null || pinOut != null;
            }

            static string PrettyFromIn(AMMediaType mtIn, string? selectedName)
            {
                string? pretty = PrettyFromWaveOrSubtype(mtIn);
                string? prettyFromName = PrettyFromName(selectedName);

                if (!string.IsNullOrWhiteSpace(prettyFromName)
                    && (string.IsNullOrWhiteSpace(pretty)
                        || pretty.Equals("IEC61937", StringComparison.OrdinalIgnoreCase)
                        || LooksObjectBasedFromName(selectedName)))
                {
                    pretty = prettyFromName;
                }

                if (string.IsNullOrEmpty(pretty))
                    pretty = prettyFromName;

                var (_, rate, ch, _, _, _) = ReadWave(mtIn);
                bool objectBased = LooksObjectBasedFromName(selectedName)
                    || (!string.IsNullOrWhiteSpace(pretty)
                        && (pretty.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0
                            || pretty.IndexOf("DTS:X", StringComparison.OrdinalIgnoreCase) >= 0));
                string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "";
                string chStr = (!objectBased && ch > 0) ? $"{ch}ch" : "";
                string extra = string.Join(" • ", new[] { rateStr, chStr }.Where(s => !string.IsNullOrEmpty(s)));

                return string.IsNullOrEmpty(extra) ? (pretty ?? "n/d") : $"{(pretty ?? "n/d")} • {extra}";
            }

            // Ritorna (isPcmStimato, dettaglioHuman); il flag PCM qui è solo “descrittivo”.
            static (bool isPcm, string detail) PrettyOutFromLav(AMMediaType mtOut, string? selectedName)
            {
                (ushort tag, int rate, int ch, int bps, int vbits, int _) = ReadWave(mtOut);

                // 1) priorità a WaveEx/WaveExtensible
                var waveClass = ClassifyByWave(mtOut);
                if (waveClass.HasValue)
                {
                    bool isPcmWave = waveClass.Value.isPcm;
                    string? prettyWave = waveClass.Value.pretty;
                    string? prettyFromName = PrettyFromName(selectedName);

                    if (!string.IsNullOrWhiteSpace(prettyFromName)
                        && (string.IsNullOrWhiteSpace(prettyWave)
                            || prettyWave.Equals("IEC61937", StringComparison.OrdinalIgnoreCase)
                            || LooksObjectBasedFromName(selectedName)))
                    {
                        prettyWave = prettyFromName;
                    }

                    prettyWave ??= PrettyFromWaveOrSubtype(mtOut)
                                   ?? prettyFromName
                                   ?? "IEC61937";

                    if (isPcmWave)
                    {
                        int validBitsW = vbits > 0 ? vbits : bps;
                        string rateStrW = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                        string chStrW = ch > 0 ? $"{ch}ch" : "n/d";
                        string bitStrW = validBitsW > 0 ? $"{validBitsW}-bit" : "n/d";
                        return (true, $"PCM {rateStrW} • {bitStrW} • {chStrW}");
                    }

                    return (false, $"Bitstream ({prettyWave})");
                }

                // 2) fallback: subType/tag
                bool isPcmBySubtype = (mtOut.subType == MediaSubType.PCM || mtOut.subType == MediaSubType.IEEE_FLOAT);
                bool isPcmByTag = (tag == 1 /*PCM*/ || tag == 3 /*IEEE_FLOAT*/);

                string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                string chStr = ch > 0 ? $"{ch}ch" : "n/d";
                int validBits = vbits > 0 ? vbits : bps;
                string bitStr = validBits > 0 ? $"{validBits}-bit" : "n/d";

                if (isPcmBySubtype || isPcmByTag)
                    return (true, $"PCM {rateStr} • {bitStr} • {chStr}");

                string? pretty = PrettyFromWaveOrSubtype(mtOut);
                string? prettyFromFallbackName = PrettyFromName(selectedName);
                if (!string.IsNullOrWhiteSpace(prettyFromFallbackName)
                    && (string.IsNullOrWhiteSpace(pretty)
                        || pretty.Equals("IEC61937", StringComparison.OrdinalIgnoreCase)
                        || LooksObjectBasedFromName(selectedName)))
                {
                    pretty = prettyFromFallbackName;
                }
                pretty ??= "IEC61937";
                return (false, $"Bitstream ({pretty})");
            }

            static (bool isPcm, string? pretty)? ClassifyByWave(AMMediaType mt)
            {
                try
                {
                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<Engines.WaveFormatEx>(mt.formatPtr);

                        if (wfex.wFormatTag == 1 || wfex.wFormatTag == 3)
                            return (true, "PCM");

                        if (wfex.wFormatTag == 0x0092) // WAVE_FORMAT_DOLBY_AC3_SPDIF
                            return (false, "Dolby Digital");

                        if (wfex.wFormatTag == 0xFFFE && wfex.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensibleLocal>(mt.formatPtr);

                            var subStr = ext.SubFormat.ToString().ToUpperInvariant();
                            if (subStr.Contains("61937") || subStr.Contains("SPDIF"))
                                return (false, "IEC61937");

                            var s = ext.SubFormat.ToString().ToUpperInvariant();
                            string? pretty =
                                s.Contains("TRUEHD") || s.Contains("MLP") ? "Dolby TrueHD" :
                                s.Contains("EAC3") || s.Contains("DDPLUS") || s.Contains("DD+") ? "Dolby Digital Plus" :
                                s.Contains("AC3") || s.Contains("DOLBY_AC3") ? "Dolby Digital" :
                                (s.Contains("DTS_HD") && s.Contains("MA")) ? "DTS-HD MA" :
                                (s.Contains("DTS_HD") && (s.Contains("HRA") || s.Contains("HIGH"))) ? "DTS-HD HRA" :
                                s.Contains("DTS") ? "DTS" : null;

                            return (false, pretty);
                        }

                        return (false, null);
                    }
                }
                catch { }
                return null;
            }

            static (ushort wFormatTag, int nSamplesPerSec, int nChannels, int wBitsPerSample, int validBitsPerSample, int avgBytesPerSec) ReadWave(AMMediaType mt)
            {
                ushort tag = 0; int rate = 0; int ch = 0; int bps = 0; int vbits = 0; int avg = 0;
                try
                {
                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<Engines.WaveFormatEx>(mt.formatPtr);
                        tag = wfex.wFormatTag;
                        rate = unchecked((int)wfex.nSamplesPerSec);
                        ch = unchecked((int)wfex.nChannels);
                        bps = wfex.wBitsPerSample;
                        avg = unchecked((int)wfex.nAvgBytesPerSec);

                        if (tag == 0xFFFE /*WAVE_FORMAT_EXTENSIBLE*/ && wfex.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensibleLocal>(mt.formatPtr);
                            if (ext.wValidBitsPerSample != 0) vbits = ext.wValidBitsPerSample;
                        }
                    }
                }
                catch { }
                return (tag, rate, ch, bps, vbits, avg);
            }

            static string? PrettyFromWaveOrSubtype(AMMediaType mt)
            {
                try
                {
                    var sub = mt.subType;
                    string g = sub.ToString().ToUpperInvariant();
                    if (g.Contains("AC3") || g.Contains("DOLBY_AC3")) return "Dolby Digital";
                    if (g.Contains("EAC3") || g.Contains("DDPLUS") || g.Contains("DD+")) return "Dolby Digital Plus";
                    if (g.Contains("TRUEHD") || g.Contains("MLP")) return "Dolby TrueHD";
                    if (g.Contains("DTS_HD") && g.Contains("MA")) return "DTS-HD MA";
                    if (g.Contains("DTS_HD") && (g.Contains("HRA") || g.Contains("HIGH"))) return "DTS-HD HRA";
                    if (g.Contains("DTS")) return "DTS";
                    if (g.Contains("AAC")) return "AAC";
                    if (g.Contains("OPUS")) return "Opus";
                    if (g.Contains("FLAC")) return "FLAC";
                    if (g.Contains("PCM")) return "PCM";
                    if (g.Contains("IEEE_FLOAT")) return "PCM (float)";

                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<Engines.WaveFormatEx>(mt.formatPtr);
                        ushort tag = wfex.wFormatTag;
                        if (tag == 1) return "PCM";
                        if (tag == 3) return "PCM (float)";
                        if (tag == 0x0092) return "IEC61937";
                        if (tag == 0x2000) return "Dolby/DTS (compresso)";
                    }

                    var sg = mt.subType.ToString().ToUpperInvariant();
                    if (sg.Contains("61937") || sg.Contains("SPDIF"))
                        return "IEC61937";
                }
                catch { }
                return null;
            }

            static string? PrettyFromName(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                string n = name.ToUpperInvariant();
                if (n.Contains("TRUEHD")) return n.Contains("ATMOS") || n.Contains("JOC") ? "Dolby TrueHD (Atmos)" : "Dolby TrueHD";
                if (n.Contains("E-AC3") || n.Contains("EAC3") || n.Contains("DDP") || n.Contains("DD+"))
                    return n.Contains("ATMOS") || n.Contains("JOC") ? "Dolby Digital Plus (Atmos)" : "Dolby Digital Plus";
                if (n.Contains("AC3") || n.Contains("DOLBY DIGITAL")) return "Dolby Digital";
                if (n.Contains("DTS:X") || n.Contains("DTS X")) return "DTS:X";
                if (n.Contains("DTS-HD MA") || n.Contains("DTS HD MA") || n.Contains("MASTER AUDIO")) return "DTS-HD MA";
                if (n.Contains("DTS-HD HRA") || n.Contains("HIGH RES")) return "DTS-HD HRA";
                if (n.Contains("DTS")) return "DTS";
                if (n.Contains("AAC")) return "AAC";
                if (n.Contains("OPUS")) return "Opus";
                if (n.Contains("FLAC")) return "FLAC";
                if (n.Contains("PCM")) return "PCM";
                return null;
            }

            static bool LooksObjectBasedFromName(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                string n = name.ToUpperInvariant();
                return n.Contains("ATMOS") || n.Contains("JOC") || n.Contains("DTS:X") || n.Contains("DTS X");
            }

            // preferisci stringhe non generiche (es. "DTS-HD MA" batte "IEC61937")
            static string PreferMoreSpecific(string a, string b)
            {
                bool AIsGeneric = a.IndexOf("IEC61937", StringComparison.OrdinalIgnoreCase) >= 0;
                bool BIsGeneric = b.IndexOf("IEC61937", StringComparison.OrdinalIgnoreCase) >= 0;
                if (AIsGeneric && !BIsGeneric) return b;
                if (BIsGeneric && !AIsGeneric) return a;
                return a.Length >= b.Length ? a : b;
            }
        }


        // Graph helpers
        private bool TryGetFilterGraph(out IFilterGraph2? fg)
        {
            fg = null;
            if (_engine == null) return false;
            try
            {
                if (_engine is IFilterGraph2 direct) { fg = direct; return true; }

                var t = _engine.GetType();
                var p1 = t.GetProperty("Graph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p1 != null && p1.GetValue(_engine) is IFilterGraph2 g1) { fg = g1; return true; }

                var p2 = t.GetProperty("FilterGraph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p2 != null && p2.GetValue(_engine) is IFilterGraph2 g2) { fg = g2; return true; }

                var m1 = t.GetMethod("GetGraph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (m1 != null && (m1.Invoke(_engine, null) is IFilterGraph2 g3)) { fg = g3; return true; }
            }
            catch { }
            return false;
        }

        private void TryBindGraphNotify()
        {
            try
            {
                UnbindGraphNotify();

                if (!IsHandleCreated) return;
                if (!TryGetFilterGraph(out var fg) || fg == null) return;

                if (fg is IMediaEventEx ev)
                {
                    _graphEvents = ev;
                    ev.SetNotifyWindow(this.Handle, WM_GRAPHNOTIFY, IntPtr.Zero);
                }
            }
            catch
            {
                _graphEvents = null;
            }
        }

        private void UnbindGraphNotify()
        {
            try { _graphEvents?.SetNotifyWindow(IntPtr.Zero, 0, IntPtr.Zero); } catch { }
            _graphEvents = null;
        }

        private void DrainGraphEvents()
        {
            var ev = _graphEvents;
            if (ev == null) return;

            while (true)
            {
                int hr;
                EventCode code;
                IntPtr p1;
                IntPtr p2;

                try
                {
                    hr = ev.GetEvent(out code, out p1, out p2, 0);
                }
                catch
                {
                    break;
                }

                if (hr != 0) break;

                try
                {
                    if (code == EventCode.Complete)
                    {
                        HandlePlaybackCompleted();
                    }
                }
                finally
                {
                    try { ev.FreeEventParams(code, p1, p2); } catch { }
                }
            }
        }

        private void HandlePlaybackCompleted()
        {
            if (_stopping || _endTriggered || _playbackQueueTransitionInProgress) return;

            _endTriggered = true;
            _endCandidateSinceUtc = DateTime.MinValue;

            // Se è una demo pre-roll, avvia il film “senza ricaricamenti” visibili.
            if (_playingPreRoll && !string.IsNullOrWhiteSpace(_pendingMainPathAfterPreRoll))
            {
                var next = _pendingMainPathAfterPreRoll;
                var nextResume = _pendingMainResumeAfterPreRoll;
                var nextPaused = _pendingMainStartPausedAfterPreRoll;

                _pendingMainPathAfterPreRoll = null;
                _pendingMainResumeAfterPreRoll = 0;
                _pendingMainStartPausedAfterPreRoll = false;
                _playingPreRoll = false;

                _suppressPreRollOnce = true;
                _suppressVideoLoadingOnce = true;

                try { BeginInvoke(new Action(() => OpenPath(next!, nextResume, nextPaused, allowPlaceholderGate: false))); } catch { }
                return;
            }

            if (IsSingleTrackLoopEnabledForPath(_currentPath))
            {
                string? loopPath = _currentPath;
                try
                {
                    if (_engine != null && !string.IsNullOrWhiteSpace(loopPath) && _duration > 0)
                    {
                        _engine.PositionSeconds = 0;
                        _engine.Play();
                        _paused = false;
                        _endTriggered = false;
                        _endCandidateSinceUtc = DateTime.MinValue;
                        try { PublishRemoteState(0); } catch { }
                        try { UpdateTime(0); } catch { }
                        return;
                    }
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(loopPath))
                {
                    _suppressPreRollOnce = true;
                    _suppressVideoLoadingOnce = true;
                    try { BeginInvoke(new Action(() => OpenPath(loopPath!, 0, false, allowPlaceholderGate: false))); } catch { }
                    return;
                }
            }

            if (TryAdvancePlaybackQueue())
                return;

            // Fine contenuto → torna alla libreria
            try
            {
                SafeStop(toSplash: false);
                ShowLibrary();
            }
            catch { }
        }
        private static bool TryFindFilter(IFilterGraph2 fg, string nameContains, out IBaseFilter? filter)
        {
            filter = null;
            if (fg.EnumFilters(out IEnumFilters? enumF) != 0 || enumF == null) return false;

            var arr = new IBaseFilter[1];
            while (enumF.Next(1, arr, IntPtr.Zero) == 0)
            {
                var f = arr[0];
                f.QueryFilterInfo(out var info);
                try
                {
                    if (!string.IsNullOrWhiteSpace(info.achName) &&
                        info.achName.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    { filter = f; return true; }
                }
                finally
                {
                    if (info.pGraph != null) Marshal.ReleaseComObject(info.pGraph);
                }
                Marshal.ReleaseComObject(f);
            }
            return false;
        }

        private void ReopenSame()
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            double pos = _engine?.PositionSeconds ?? 0; bool paused = _paused;
            // Evita pre-roll quando ricarichiamo il renderer/engine (cambio lingua, setting madVR, ecc.)
            _suppressPreRollOnce = true;
            if (_engine != null)
                _suppressNextWledRestore = true;
            OpenPath(_currentPath!, resume: pos, startPaused: paused, allowPlaceholderGate: false);
        }

        private bool IsCurrentYouTube()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentPath)) return false;
                if (!Uri.TryCreate(_currentPath, UriKind.Absolute, out var u)) return false;
                var h = u.Host;
                return h.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0
                    || h.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

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
            }
            catch { }
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
                if (!_pausePlaceholderEnabled || _engine == null || _splash.Visible || IsPhotoMode || _audioOnlyBanner.Visible || !ShouldUseCinemaFeaturesForPath(_currentPath))
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
                    _pausePlaceholder.SubtitleText = "powered by madVR";
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
                        _pausePlaceholder.Caption = "PAUSA";
                        _pausePlaceholder.DrawCaptionAlways = false;
                        _pausePlaceholder.TitleText = string.Empty;
                        _pausePlaceholder.SubtitleText = string.Empty;
                        _pausePlaceholder.ShowBranding = false;
                        _pausePlaceholder.UseCoverImage = false;
                        _pausePlaceholder.SetBrandLogo(null);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private bool TryConsumePreOpenPlaceholderGate(bool startNow, bool fromRemote)
        {
            if (!_preOpenPlaceholderGateActive) return false;

            string? p = _pendingPathAfterPlaceholderGate;
            double resume = _pendingResumeAfterPlaceholderGate;

            HidePreOpenPlaceholderGate(clearPending: true);

            if (!startNow) return true;

            if (!string.IsNullOrWhiteSpace(p))
            {
                // Placeholder -> demo/film: stesso trattamento "invisibile" già usato tra demo e film.
                _suppressVideoLoadingOnce = true;
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
            _paused = !_paused;

            if (_paused)
            {
                // ferma il sampler, azzera l'integrazione
                _lastPktSample = DateTime.MinValue;
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

            _hud.TimelineVisible = _duration > 0;

            // Da remoto NON deve comparire l'HUD (eccetto timeline, gestita altrove)
            if (!IsRemoteCommandActive)
                HudBump(1200, allowWhenRemote: false, showTimeline: true);

            BringOverlaysToFront();
            EnsureActive();
        }

        private void SafeStop(bool toSplash = true)
        {
            _stopping = true;

            // ferma eventuali notifiche DirectShow (EC_COMPLETE)
            try { UnbindGraphNotify(); } catch { }

            if (_engine != null)
            {
                try { _engine.BindUpdateCallback(null); } catch { }
                try { if (_engineStatusHandler != null) _engine.OnStatus -= _engineStatusHandler; } catch { }
                try { if (_engineProgressHandler != null) _engine.OnProgressSeconds -= _engineProgressHandler; } catch { }
                try { _engine.OnBitstreamChanged -= OnEngineBitstreamChanged; } catch { }
            }
            _engineStatusHandler = null;
            _engineProgressHandler = null;
            _engineUpdateHandler = null;
            try { _engine?.Stop(); } catch { }
            try { _engine?.Dispose(); } catch { }
            try { _refresh.RestoreIfChanged(); } catch { }
            try { _pktRate.Dispose(); } catch { }
            try { _pktRateAudio.Dispose(); } catch { }
            _pktRate = new PacketRateSampler();      // ✅ nuova istanza per la prossima riproduzione
            _pktRateAudio = new PacketRateSampler(); // ✅ nuova istanza per la prossima riproduzione
            _pktRateOk = false;
            _pktRateAudioOk = false;
            _lastPktSample = DateTime.MinValue;
            _engine = null;
            _currentMediaHasVideo = false;
            _imageFiles.Clear();
            _imageIndex = -1;
            if (_photoHud != null) _photoHud.Visible = false;
            _duration = 0; _paused = false;
            try { HidePreOpenPlaceholderGate(clearPending: true); } catch { }

            // reset pre-roll state (se interrompiamo mentre una demo è in corso)
            _playingPreRoll = false;
            _pendingMainPathAfterPreRoll = null;
            _pendingMainResumeAfterPreRoll = 0;
            _pendingMainStartPausedAfterPreRoll = false;
            NotifyPlaybackStoppedForWled();
            PublishRemoteState(0);
            var oldThumbCts = Interlocked.Exchange(ref _thumbCts, null);
            try { oldThumbCts?.Cancel(); } catch { }
            try { oldThumbCts?.Dispose(); } catch { }
            try { _thumb.Close(); } catch { }
            try { _previewCache.Clear(); } catch { }
            Interlocked.Increment(ref _previewReqSerial);
            _previewBusy = false;
            StopAudioMeters();
            ResetAudioOverlayState();
            _infoOverlay.Visible = false;
            _hud.Visible = false;
            try { if (_videoLoading != null) _videoLoading.Visible = false; } catch { }
            _currentWebAudioUrl = null;
            _currentPath = null;
            _statsTimer.Stop();

            // Quando si stoppa tutto e si torna allo splash, NON devono restare UI "sotto"
            // (es. libreria o modali) ne' evidenziazioni appese.
            try { if (_settingsModal?.Visible == true) _settingsModal.Visible = false; } catch { }
            try { if (_creditsModal?.Visible == true) HideCreditsModal(); } catch { }
            try { if (_libraryPage?.Visible == true) HideLibrary(); } catch { }
            try { _focusRing.Attach(null); } catch { }
            _focused = null;
            _dpadRoot = null;

            _splash.Visible = toSplash;
            try { _videoLoading.Visible = false; } catch { }
            _overlayHost?.SyncTo(this);
            BringOverlaysToFront();
            EnsureActive();
        }

        private void CloseCurrentToLibrary()
        {
            try
            {
                // stop pulito senza “rimbalzare” allo splash
                SafeStop(toSplash: false);
            }
            catch { }

            try
            {
                ShowLibrary();
            }
            catch { }
        }

        private void ToggleFullscreen()
        {
            if (_fullscreenTransitioning) return;
            _fullscreenTransitioning = true;

            try
            {
                var screen = Screen.FromControl(this);
                if (FormBorderStyle != FormBorderStyle.None)
                {
                    _prevBorder = FormBorderStyle;
                    _prevState = WindowState;
                    _prevBounds = Bounds;
                    _prevControlBox = ControlBox;
                    _prevMinimizeBox = MinimizeBox;
                    _prevMaximizeBox = MaximizeBox;
                    _prevWindowStyle = GetCurrentWindowStyle();

                    try { SendMessage(this.Handle, 0x000B /*WM_SETREDRAW*/, IntPtr.Zero, IntPtr.Zero); } catch { }
                    try { SuspendLayout(); } catch { }
                    try
                    {
                        if (WindowState != FormWindowState.Normal)
                            WindowState = FormWindowState.Normal;

                        try
                        {
                            ControlBox = false;
                            MinimizeBox = false;
                            MaximizeBox = false;
                        }
                        catch { }

                        FormBorderStyle = FormBorderStyle.None;
                        ApplyTrueBorderlessWindowStyle();
                        TopMost = true;

                        Bounds = screen.Bounds;
                        Win32.SetWindowPos(this.Handle, Win32.HWND_TOPMOST,
                            Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height,
                            Win32.SWP_FRAMECHANGED);
                    }
                    finally
                    {
                        try { ResumeLayout(true); } catch { }
                        try { SendMessage(this.Handle, 0x000B /*WM_SETREDRAW*/, new IntPtr(1), IntPtr.Zero); } catch { }
                        try { Invalidate(true); Update(); } catch { }
                    }
                }
                else
                {
                    try { SendMessage(this.Handle, 0x000B /*WM_SETREDRAW*/, IntPtr.Zero, IntPtr.Zero); } catch { }
                    try { SuspendLayout(); } catch { }
                    try
                    {
                        TopMost = false;
                        FormBorderStyle = _prevBorder;
                        try
                        {
                            ControlBox = _prevControlBox;
                            MinimizeBox = _prevMinimizeBox;
                            MaximizeBox = _prevMaximizeBox;
                        }
                        catch { }
                        RestoreWindowedWindowStyle();

                        if (_prevState == FormWindowState.Normal && _prevBounds.Width > 0 && _prevBounds.Height > 0)
                            Bounds = _prevBounds;

                        WindowState = _prevState;
                        Win32.SetWindowPos(this.Handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_FRAMECHANGED);
                        _hud.AutoHide = false;
                    }
                    finally
                    {
                        try { ResumeLayout(true); } catch { }
                        try { SendMessage(this.Handle, 0x000B /*WM_SETREDRAW*/, new IntPtr(1), IntPtr.Zero); } catch { }
                        try { Invalidate(true); Update(); } catch { }
                    }
                }
            }
            finally
            {
                _fullscreenTransitioning = false;
            }

            try
            {
                if (_overlayHost != null)
                    _overlayHost.TopMost = this.TopMost;
            }
            catch { }

            try
            {
                SafeShowOverlayHost();
                SyncOverlayToVideoRect();
                BringOverlaysToFront();
                if (_pausePlaceholder != null && _pausePlaceholder.Visible)
                    _pausePlaceholder.BringToFront();
            }
            catch { }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed) return;
                        SafeShowOverlayHost();
                        SyncOverlayToVideoRect();
                        BringOverlaysToFront();
                        if (_pausePlaceholder != null && _pausePlaceholder.Visible)
                            _pausePlaceholder.BringToFront();
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void SyncOverlayToVideoRect()
        {
            if (_overlayHost == null) return;

            Rectangle formClientScreen = this.RectangleToScreen(this.ClientRectangle);
            _overlayHost.SyncToScreen(formClientScreen);
            _overlayHost.SetClickThrough(false);

            Rectangle destClient = _videoHost.ClientRectangle;
            try
            {
                if (_engine != null)
                    destClient = _engine.GetLastDestRectAsClient(_videoHost.ClientRectangle);
            }
            catch { destClient = _videoHost.ClientRectangle; }

            destClient.Offset(_videoHost.Left, _videoHost.Top);
            _lastVideoDestInForm = destClient;
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


        private void OnEngineProgress(double cur)
        {
            UpdateTime(cur);
            TryAutoReturnToLibraryOnEnd(cur);
        }

        private void TryAutoReturnToLibraryOnEnd(double cur)
        {
            try
            {
                bool queueDrivenEndHandling = _playbackQueueSessionActive || (_playingPreRoll && !string.IsNullOrWhiteSpace(_pendingMainPathAfterPreRoll));
                if (!_autoReturnToLibraryOnEnd && !queueDrivenEndHandling) return;
                if (_endTriggered) return;
                if (_engine == null) return;
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
            _hud?.Invalidate();
            PublishRemoteState(cur);

            try
            {
                // --- SE PAUSA: non campionare nulla, congela i "now" e non aggiornare le medie ---
                if (_paused)
                {
                    // ferma il sampler FFmpeg
                    _lastPktSample = DateTime.MinValue;

                    // decadi dolcemente i valori correnti per non avere salti brutti
                    _audioBitrateNowKbps = (int)(_audioBitrateNowKbps * 0.85);
                    _videoBitrateNowKbps = (int)(_videoBitrateNowKbps * 0.85);

                    // non accumulare nelle medie finché sei fermo
                    _avgLastTs = DateTime.MinValue;

                    // aggiorna solo l’overlay con i valori "freezati"
                    if (_infoOverlay.Visible && _info != null && _engine != null)
                    {
                        var chosen = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                        UpdateInfoOverlay(chosen, _info.IsHdr);
                    }
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
                        if (!_isLocalFile && !string.IsNullOrEmpty(_currentPath) &&
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
                        if ((_hud.Visible || _infoOverlay.Visible) &&
                            (DateTime.UtcNow - _lastPktSample).TotalMilliseconds >= 600 &&
                            !_stopping)
                        {
                            _lastPktSample = DateTime.UtcNow;
                            double pos = _engine.PositionSeconds;
                            bool engineHasVideo = _engine.HasDisplayControl();

                            Task.Run(() =>
                            {
                                try
                                {
                                    // finestra 0.5s: reattivo ma ancora stabile
                                    int ak = 0;
                                    int vk = 0;

                                    try
                                    {
                                        if (_pktRateOk)
                                        {
                                            var rMain = _pktRate.Sample(pos, 0.5);
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
                                            var rA = _pktRateAudio.Sample(pos, 0.5);
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
                                                // smoothing: 40% vecchio, 60% nuovo
                                                _audioBitrateNowKbps = (_audioBitrateNowKbps <= 0)
                                                    ? ak
                                                    : (int)(_audioBitrateNowKbps * 0.4 + ak * 0.6);
                                                _aNowTs = nowLocal;
                                            }

                                            if (engineHasVideo && vk > 0)
                                            {
                                                _videoBitrateNowKbps = (_videoBitrateNowKbps <= 0)
                                                    ? vk
                                                    : (int)(_videoBitrateNowKbps * 0.4 + vk * 0.6);
                                                _vNowTs = nowLocal;
                                            }
                                        }));
                                    }
                                }
                                catch { /* best-effort */ }
                            });
                        }
                    }
                    catch { /* best-effort */ }

                    // 2) Audio IN/OUT + flag bitstream (per overlay, non per i "now")
                    var sel = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
                    var lav = GetLavAudioIODetails(sel?.Name);
                    _bitstreamNow = IsBitstream();   // unica fonte di verità per la modalità di uscita

                    // 3) Video/Audio NOW (dinamico) + gestione solo-audio
                    bool hasVideo = _engine.HasDisplayControl();

                    // Campioni “freschi” dal sampler FFmpeg (<=1.5 s)
                    bool recentAudio = (DateTime.UtcNow - _aNowTs).TotalSeconds <= 1.5;
                    bool recentVideo = (DateTime.UtcNow - _vNowTs).TotalSeconds <= 1.5;

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
                                if (lav.AudioNowKbps > 0) kbps = lav.AudioNowKbps;
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
                                if (lav.AudioNowKbps > 0)
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

            if (_infoOverlay.Visible && _info != null && _engine != null)
            {
                var chosen = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                UpdateInfoOverlay(chosen, _info.IsHdr);
            }
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
            if (_info == null || _info.Chapters.Count == 0) { _lblStatus.Text = "Nessun capitolo rilevato"; return; }
            var menu = new ContextMenuStrip();
            ApplyDarkMenuTheme(menu);
            foreach (var (title, start) in _info.Chapters)
            {
                var it = new ToolStripMenuItem($"{Fmt(start)}  {title}"); double s = start;
                it.Click += (_, __) => { if (_engine != null) _engine.PositionSeconds = s; _hud.ShowOnce(1200); };
                menu.Items.Add(it);
            }
            menu.Show(Cursor.Position);
        }

        private void OnPreviewRequested(double seconds, Point _)
        {
            // Se non stiamo scrubbando (o HUD nascosto), annulla eventuali job e pulisci.
            if (!_hud.Visible || !Volatile.Read(ref _scrubActive))
            {
                var old0 = Interlocked.Exchange(ref _thumbCts, null);
                try { old0?.Cancel(); } catch { }
                try { old0?.Dispose(); } catch { }

                Interlocked.Increment(ref _previewReqSerial);
                try { _hud.SetPreview(null, seconds); } catch { }
                return;
            }

            _hud.Visible = true;
            _hud.BringToFront();
            _overlayHost?.BringToFront();

            if (string.IsNullOrEmpty(_currentPath) || _info == null || !_info.HasVideo)
            {
                _hud.SetPreview(null, seconds);
                return;
            }

            // latest-wins: registra richiesta
            _previewReqSeconds = seconds;
            Interlocked.Increment(ref _previewReqSerial);

            // Realtime: cancella subito l'eventuale decode in corso e tieni solo l'ultima richiesta.
            // Questo evita "coda" e preview che arriva in ritardo/col frame sbagliato.
            var oldCts = Interlocked.Exchange(ref _thumbCts, new CancellationTokenSource());
            try { oldCts?.Cancel(); } catch { }
            try { oldCts?.Dispose(); } catch { }

            // Se un worker è già in corso, non ne avviamo un altro: userà la request aggiornata.
            if (_previewBusy) return;

            _previewBusy = true;
            Task.Run(PreviewWorkerLoop);
        }

        private void PreviewWorkerLoop()
        {
            try
            {
                while (true)
                {
                    if (!Volatile.Read(ref _scrubActive))
                        return;

                    int req = Volatile.Read(ref _previewReqSerial);
                    double sec = _previewReqSeconds;

                    var cts = _thumbCts;
                    if (cts == null) return;
                    var tk = cts.Token;

                    Bitmap? bmp = null;

                    try
                    {
                        // 1) cache (ritorna clone)
                        if (_previewCache.TryGet(sec, out var cached))
                        {
                            bmp = cached;
                        }
                        else
                        {
                            // 2) thumbnailer locale (cancellabile)
                            try { bmp = _thumb.Get(sec, TIMELINE_PREVIEW_W, tk, realtime: true); } catch { bmp = null; }

                            if (tk.IsCancellationRequested) { bmp?.Dispose(); continue; }

                            // 3) fallback (thumbnailer interno all'engine)
                            if (bmp == null)
                            {
                                try { bmp = _engine?.GetPreviewFrame(sec, TIMELINE_PREVIEW_W); } catch { bmp = null; }
                            }

                            if (bmp != null)
                                _previewCache.Put(sec, bmp); // store clone
                        }
                    }
                    catch
                    {
                        bmp?.Dispose();
                        bmp = null;
                    }

                    if (tk.IsCancellationRequested)
                    {
                        bmp?.Dispose();
                        continue;
                    }

                    // catture locali per evitare problemi con closure
                    var bmpLocal = bmp;
                    double secLocal = sec;
                    int reqLocal = req;
                    var ctsLocal = cts;

                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            // Latest-wins anche lato UI: non mostrare frame vecchi.
                            if (!_scrubActive || !_hud.Visible || ctsLocal != _thumbCts || reqLocal != Volatile.Read(ref _previewReqSerial))
                            {
                                bmpLocal?.Dispose();
                                return;
                            }
                            _hud.SetPreview(bmpLocal, secLocal);
                        }));
                    }
                    catch
                    {
                        bmpLocal?.Dispose();
                    }

                    // Se nel frattempo è arrivata una richiesta più nuova, ripeti subito.
                    if (reqLocal != Volatile.Read(ref _previewReqSerial))
                        continue;

                    break;
                }
            }
            finally
            {
                _previewBusy = false;
            }
        }

        private sealed class PreviewCache : IDisposable
        {
            private sealed class Entry
            {
                public long Key;
                public Bitmap Bmp = null!;
            }

            private readonly object _lock = new();
            private readonly int _capacity;
            private readonly double _quantumSec;
            private readonly Dictionary<long, LinkedListNode<Entry>> _map = new();
            private readonly LinkedList<Entry> _lru = new();

            public PreviewCache(int capacity, double quantumSec)
            {
                _capacity = Math.Max(8, capacity);
                _quantumSec = Math.Max(0.01, quantumSec);
            }

            private long KeyOf(double seconds)
                => (long)Math.Round(seconds / _quantumSec);

            public bool TryGet(double seconds, out Bitmap? bmp)
            {
                long key = KeyOf(seconds);
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        bmp = (Bitmap)node.Value.Bmp.Clone();
                        return true;
                    }
                }

                bmp = null;
                return false;
            }

            public void Put(double seconds, Bitmap bmp)
            {
                long key = KeyOf(seconds);
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        try { node.Value.Bmp.Dispose(); } catch { }
                        node.Value.Bmp = (Bitmap)bmp.Clone();
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        return;
                    }

                    var entry = new Entry { Key = key, Bmp = (Bitmap)bmp.Clone() };
                    var newNode = new LinkedListNode<Entry>(entry);
                    _lru.AddFirst(newNode);
                    _map[key] = newNode;

                    while (_map.Count > _capacity)
                    {
                        var last = _lru.Last;
                        if (last == null) break;
                        _lru.RemoveLast();
                        _map.Remove(last.Value.Key);
                        try { last.Value.Bmp.Dispose(); } catch { }
                    }
                }
            }

            public void Clear()
            {
                lock (_lock)
                {
                    foreach (var e in _lru)
                    {
                        try { e.Bmp.Dispose(); } catch { }
                    }
                    _lru.Clear();
                    _map.Clear();
                }
            }

            public void Dispose() => Clear();
        }

        private void ResetAudioOverlayState()
        {
            try { if (_audioMeters != null) _audioMeters.Visible = false; } catch { }
            try { if (_audioMetersHost != null) _audioMetersHost.Visible = false; } catch { }
            try { if (_audioOnlyBanner != null) _audioOnlyBanner.Visible = false; } catch { }
        }

        private void OnEngineBitstreamChanged(bool bitstreamActive)
        {
            if (_audioOutPref == AudioOutPref.ForcePcm)
                bitstreamActive = false;

            _bitstreamNow = bitstreamActive;

            try
            {
                if (!IsHandleCreated || IsDisposed)
                    return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || _stopping)
                        return;

                    if (_info != null)
                    {
                        var chosen = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                        UpdateInfoOverlay(chosen, _info.IsHdr);
                    }

                    if (_currentMediaHasVideo)
                    {
                        StopAudioMeters();
                        ResetAudioOverlayState();
                    }
                    else if (bitstreamActive)
                    {
                        StopAudioMeters();
                        ResetAudioOverlayState();
                        _audioOnlyBanner.Visible = true;
                        _audioOnlyBanner.BringToFront();
                        BringOverlaysToFront();
                    }
                    else
                    {
                        StartAudioMetersIfPossible();
                    }

                    if (bitstreamActive)
                    {
                        try { _engine?.SetVolume(1f); } catch { }
                        try { _hud?.SetExternalVolume(1f); } catch { }
                    }
                }));
            }
            catch { }
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

                if (bit)
                {
                    _audioMeters?.SetInfoMessage("Bitstream attivo: misure disabilitate");
                    if (_audioMeters != null) _audioMeters.Visible = false;
                    if (_audioMetersHost != null) _audioMetersHost.Visible = false;
                    _audioOnlyBanner.Visible = true;
                    _audioOnlyBanner.BringToFront();
                    BringOverlaysToFront();
                    return;
                }

                _audioMeters?.SetInfoMessage(null);

                _audioSampler ??= new LoopbackSampler();
                bool ok = _audioSampler.Start();
                Dbg.Log($"[Meters] LoopbackSampler.Start() → {ok}", Dbg.LogLevel.Info);

                if (ok)
                {
                    _audioSampler.OnMetrics -= OnSamplerMetrics;
                    _audioSampler.OnMetrics += OnSamplerMetrics;

                    _audioSampler.OnLevels -= OnSamplerLevels;
                    _audioSampler.OnLevels += OnSamplerLevels;

                    if (_audioMeters != null) _audioMeters.Visible = true;
                    _audioOnlyBanner.Visible = false;
                    if (_audioMetersHost != null) _audioMetersHost.Visible = true;

                    _audioMetersHost.BringToFront();
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
        }

        private void OnSamplerMetrics(LoopbackSampler.AudioMetrics m)
        {
            if (_audioMeters == null) return;
            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_audioMeters.Visible) _audioMeters.Update(m);
                    }));
                }
            }
            catch { }
        }

        private void StopAudioMeters()
        {
            try
            {
                try { if (_audioSampler != null) _audioSampler.OnMetrics -= OnSamplerMetrics; } catch { }
                try { if (_audioSampler != null) _audioSampler.OnLevels -= OnSamplerLevels; } catch { }
                _audioSampler?.Stop();
            }
            catch { }
            _audioMeters?.SetInfoMessage(null);
            if (_audioMeters != null) _audioMeters.Visible = false;
            if (_audioMetersHost != null) _audioMetersHost.Visible = false;
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
        private void ApplyVolume(float v)
        {
            bool isBt = IsBitstream();

            // ⬇️ anche qui: se l’utente ha forzato PCM, ignoriamo il bitstream
            if (_audioOutPref == AudioOutPref.ForcePcm)
                isBt = false;

            if (isBt)
            {
                try { _engine?.SetVolume(1f); } catch { }
                try { CoreAudioSessionVolume.Set(1f); } catch { }
                try { _hud?.SetExternalVolume(1f); } catch { }
                return;
            }

            try { _engine?.SetVolume(v); } catch { }
            try { CoreAudioSessionVolume.Set(v); } catch { }
        }
    }
}
