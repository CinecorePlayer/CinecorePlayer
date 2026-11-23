using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace CinecoreMac;

/// <summary>
/// Renderer "MPV (libmpv)" come su Windows (Engines/LibMpvPlaybackEngine.cs), ma nativo: libmpv nel processo,
/// decodifica VideoToolbox, nessun Wine. Disegna in una finestra senza bordi agganciata SOTTO quella del player
/// (finestra figlia di AppKit): l'HUD trasparente resta sopra e la finestra video segue da sola gli spostamenti.
/// Parla lo stesso protocollo del VideoHost (righe "ready", "pos", "tracks", "media"...) tramite <see cref="VideoHostClient"/>.
/// </summary>
internal sealed class MpvEngine : IDisposable
{
    // ------------------------------------------------------------------ libmpv
    private const string Lib = "libmpv";
    [DllImport(Lib)] private static extern IntPtr mpv_create();
    [DllImport(Lib)] private static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib)] private static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib)] private static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib)] private static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib)] private static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out double data);
    [DllImport(Lib, EntryPoint = "mpv_get_property")] private static extern int mpv_get_property_flag(IntPtr ctx, byte[] name, int format, out int data);
    [DllImport(Lib, EntryPoint = "mpv_get_property")] private static extern int mpv_get_property_long(IntPtr ctx, byte[] name, int format, out long data);
    [DllImport(Lib)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
    [DllImport(Lib)] private static extern void mpv_free(IntPtr data);
    [DllImport(Lib)] private static extern int mpv_command(IntPtr ctx, IntPtr[] args);
    [DllImport(Lib)] private static extern int mpv_command_async(IntPtr ctx, ulong reply, IntPtr[] args);
    [DllImport(Lib)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib)] private static extern void mpv_wakeup(IntPtr ctx);
    [DllImport(Lib)] private static extern IntPtr mpv_error_string(int error);

    private const int FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;
    /// <summary>Vero dall'apertura in pausa al primo fotogramma pronto; un "pause" del player nel frattempo lo annulla.</summary>
    private volatile bool _holdStart;
    private const int EventShutdown = 1, EventEndFile = 7, EventFileLoaded = 8, EventPlaybackRestart = 21;

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent { public int EventId; public int Error; public ulong ReplyUserdata; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEndFile { public int Reason; public int Error; }

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s + "\0");

    private static bool _resolverSet;
    private static readonly object ResolverLock = new();

#if CINECORE_WINDOWS
    /// <summary>libmpv-2.dll dei componenti di Windows (third-parties\mpv): x86_64-v3 se la CPU ha AVX2.</summary>
    public static string? LibraryPath()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_LIBMPV") is { Length: > 0 } forced && File.Exists(forced)) return forced;
        string flavor = UseV3Runtime && HasV3Runtime ? "x86_64-v3" : "x86_64";
        return WinNative.ThirdParty("mpv", flavor, "libmpv-2.dll") ?? WinNative.ThirdParty("mpv", "x86_64", "libmpv-2.dll");
    }

    /// <summary>Build x86-64-v3 di libmpv presente e processore con AVX2.</summary>
    public static bool HasV3Runtime => System.Runtime.Intrinsics.X86.Avx2.IsSupported && WinNative.ThirdParty("mpv", "x86_64-v3", "libmpv-2.dll") != null;

    /// <summary>"Versione MPV" come nel vecchio player: Standard di serie, V3 sperimentale a scelta (vale dal riavvio).</summary>
    public static bool UseV3Runtime
    {
        get => MacSettings.Get("mpvRuntimeV3", false);
        set => MacSettings.Set("mpvRuntimeV3", value);
    }

    public static bool Available => LibraryPath() != null;

    /// <summary>Rubber Band (Automix a tempo): third-parties, cartella rubberband.</summary>
    public static string? RubberBandPath() => WinNative.ThirdParty("rubberband", "rubberband.dll");

#else
    /// <summary>libmpv nel pacchetto (Contents/Frameworks) o quella di Homebrew.</summary>
    private static string BundledMpvPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libmpv.2.dylib"));

    public static string? LibraryPath()
    {
        foreach (var p in new[] { Environment.GetEnvironmentVariable("CINECORE_LIBMPV"),
                     NativeComponentUpdates.UseBrewMpv ? NativeComponentUpdates.BrewMpv : null, BundledMpvPath,
                     "/opt/homebrew/lib/libmpv.2.dylib", "/usr/local/lib/libmpv.2.dylib" })
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        return null;
    }

    public static bool Available => LibraryPath() != null;

    /// <summary>Rubber Band (Automix a tempo): nel pacchetto (Contents/Frameworks) o da Homebrew.</summary>
    public static string? RubberBandPath()
    {
        string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "librubberband.3.dylib"));
        foreach (var p in new[] { bundled, "/opt/homebrew/lib/librubberband.3.dylib", "/usr/local/lib/librubberband.3.dylib" })
            if (File.Exists(p)) return p;
        return null;
    }

#endif
#if CINECORE_WINDOWS
    /// <summary>Su Windows WASAPI non ha il costo di avvio di CoreAudio.</summary>
    public static void WarmUpAudio() { }

#else
    // ------------------------------------------------------------------ CoreAudio caldo
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioComponentDescription { public uint Type, SubType, Manufacturer, Flags, FlagsMask; }
    [DllImport(AudioToolbox)] private static extern IntPtr AudioComponentFindNext(IntPtr component, ref AudioComponentDescription description);
    [DllImport(AudioToolbox)] private static extern int AudioComponentInstanceNew(IntPtr component, out IntPtr instance);
    [DllImport(AudioToolbox)] private static extern int AudioUnitInitialize(IntPtr unit);
    [DllImport(AudioToolbox)] private static extern int AudioUnitUninitialize(IntPtr unit);
    [DllImport(AudioToolbox)] private static extern int AudioComponentInstanceDispose(IntPtr unit);
    private static int _audioWarm;

    /// <summary>
    /// La prima uscita audio aperta nel processo costa 2-4 s (CoreAudio carica dispositivi e componenti): la si apre e
    /// richiude subito all'avvio dell'app, in background, cosi' il primo film non aspetta (poi ~0,2 s).
    /// </summary>
    public static void WarmUpAudio()
    {
        if (Interlocked.Exchange(ref _audioWarm, 1) != 0) return;
        try
        {
            static uint Code(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);
            var description = new AudioComponentDescription { Type = Code("auou"), SubType = Code("def "), Manufacturer = Code("appl") };
            IntPtr component = AudioComponentFindNext(IntPtr.Zero, ref description);
            if (component == IntPtr.Zero || AudioComponentInstanceNew(component, out IntPtr unit) != 0 || unit == IntPtr.Zero) return;
            if (AudioUnitInitialize(unit) == 0) AudioUnitUninitialize(unit);
            AudioComponentInstanceDispose(unit);
        }
        catch { }
    }

#endif
    internal static void EnsureResolver()
    {
        lock (ResolverLock)
        {
            if (_resolverSet) return;
            _resolverSet = true;
            // Un solo resolver per assembly: anche Rubber Band (Automix, codice condiviso con DllImport "rubberband.dll").
            NativeLibrary.SetDllImportResolver(typeof(MpvEngine).Assembly, (name, _, _) =>
                name == Lib && LibraryPath() is { } path ? Load(path)
                : name == "rubberband.dll" && RubberBandPath() is { } rb ? NativeLibrary.Load(rb) : IntPtr.Zero);
        }
    }

#if CINECORE_WINDOWS
    private static IntPtr Load(string path)
    {
        // Le DLL accanto a libmpv (se ce ne sono) si cercano nella sua cartella.
        return NativeLibrary.Load(path, typeof(MpvEngine).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
    }

#else
    [DllImport("libc")] private static extern int setenv(string name, string value, int overwrite);

    /// <summary>
    /// La libmpv del pacchetto disegna con Vulkan sopra MoltenVK (Contents/Frameworks): il caricatore Vulkan va
    /// indirizzato al file di registrazione incluso, altrimenti cerca quello di Homebrew. Environment.Set... non
    /// basta: cambia solo la copia gestita, il caricatore legge l'ambiente del processo (setenv).
    /// </summary>
    private static IntPtr Load(string path)
    {
        if (!NativeLibrary.TryLoad(path, out var loaded))
        {
            if (path != NativeComponentUpdates.BrewMpv || path == BundledMpvPath) return NativeLibrary.Load(path);
            path = BundledMpvPath;
            loaded = NativeLibrary.Load(path);
        }
        string? icd = NativeComponentUpdates.UseBrewVulkan ? NativeComponentUpdates.BrewVulkan : null;
        icd ??= Path.Combine(Path.GetDirectoryName(path)!, "MoltenVK_icd.json");
        if (File.Exists(icd))
        {
            setenv("VK_DRIVER_FILES", icd, 1);
            setenv("VK_ICD_FILENAMES", icd, 1);
        }
        return loaded;
    }

#endif
#if CINECORE_WINDOWS
    // ------------------------------------------------------------------ finestra video (Win32)
    /// <summary>La finestra del player: la finestra video le sta subito sotto.</summary>
    public static TopLevel? Owner { get; set; }

    private IntPtr _window;
    private readonly string _videoWindowTitle = $"Cinecore MPV Video {Guid.NewGuid():N}";

    /// <summary>La finestra video in uso (per il fotogramma della transizione a schermo intero), o zero.</summary>
    internal static IntPtr CurrentWindow { get; private set; }

    /// <summary>La finestra video copre sempre l'area client del player, subito sotto di lui.</summary>
    public void SyncFrame()
    {
        if (_stopping || _window == IntPtr.Zero || Owner is not { } owner) return;
        WinVideoStack.KeepBelow(owner, _window, cover: true, async: false);
    }

    private void DestroyVideoWindow()
    {
        if (_window == IntPtr.Zero) return;
        // La finestra la usa ancora il vo di mpv finche' non e' distrutto: la si nasconde subito e la si chiude dopo.
        IntPtr window = _window;
        _window = IntPtr.Zero;
        if (CurrentWindow == window) CurrentWindow = IntPtr.Zero;
        try { WinNative.ShowWindow(window, 0); } catch { }
        _ = Stopped.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => WinVideoStack.Destroy(window)));
    }

#else
    // ------------------------------------------------------------------ finestra video (AppKit)
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr obj, IntPtr sel, IntPtr a, long b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgBool(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool MsgBoolValue(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgLong(IntPtr obj, IntPtr sel, long a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgFrame(IntPtr obj, IntPtr sel, Rect rect, [MarshalAs(UnmanagedType.I1)] bool display);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Rect MsgRect(IntPtr obj, IntPtr sel);
    private static IntPtr S(string n) => sel_registerName(n);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public double X, Y, W, H; }

    /// <summary>La finestra del player: la finestra video le sta attaccata sotto.</summary>
    public static TopLevel? Owner { get; set; }

    private static IntPtr OwnerWindow()
    {
        if (Owner?.TryGetPlatformHandle() is not { } handle) return IntPtr.Zero;
        return handle.HandleDescriptor == "NSWindow" ? handle.Handle : Msg(handle.Handle, S("window"));
    }

    private IntPtr _window, _parent;

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong MsgULong(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgIndex(IntPtr obj, IntPtr sel, ulong index);

    // Un avvio puo' sovrapporsi alla chiusura della finestra del film precedente.
    private readonly string _videoWindowTitle = $"Cinecore MPV Video {Guid.NewGuid():N}";

    private IntPtr FindVideoWindow()
    {
        IntPtr windows = Msg(Msg(objc_getClass("NSApplication"), S("sharedApplication")), S("windows"));
        ulong n = MsgULong(windows, S("count"));
        for (ulong i = 0; i < n; i++)
        {
            IntPtr window = MsgIndex(windows, S("objectAtIndex:"), i);
            IntPtr title = Msg(Msg(window, S("title")), S("UTF8String"));
            if (Marshal.PtrToStringUTF8(title) == _videoWindowTitle) return window;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// La libmpv di macOS non si incorpora in una vista (--wid): crea una finestra sua, nel nostro processo.
    /// La si "adotta": senza bordi, trasparente al mouse, figlia della finestra del player e sotto di lei.
    /// </summary>
    private bool AdoptWindow()
    {
        if (_window != IntPtr.Zero) return true;
        _parent = OwnerWindow();
        if (_parent == IntPtr.Zero) return false;
        _window = FindVideoWindow();
        if (_window == IntPtr.Zero) return false;
        // Stessa forma della finestra del player (angoli arrotondati di sistema): finestra "titolata" con il contenuto
        // a tutta altezza, barra trasparente e senza pulsanti. Prima era senza bordi, con gli angoli vivi sotto
        // quelli arrotondati del player.
        MsgLong(_window, S("setStyleMask:"), 1 | (1 << 15)); // Titled | FullSizeContentView
        MsgBool(_window, S("setTitlebarAppearsTransparent:"), true);
        MsgLong(_window, S("setTitleVisibility:"), 1); // Hidden
        for (long b = 0; b <= 2; b++)
        {
            IntPtr button = Msg(_window, S("standardWindowButton:"), (IntPtr)b);
            if (button != IntPtr.Zero) MsgBool(button, S("setHidden:"), true);
        }
        MsgBool(_window, S("setHasShadow:"), false);
        MsgBool(_window, S("setIgnoresMouseEvents:"), true);
        MsgBool(_window, S("setExcludedFromWindowsMenu:"), true);
        MsgLong(_window, S("setCollectionBehavior:"), 1 << 8); // FullScreenAuxiliary
        Msg(_parent, S("addChildWindow:ordered:"), _window, -1); // NSWindowBelow
        // mpv rende chiave la propria finestra durante la creazione, anche con focus-on=never.
        if (MsgBoolValue(_window, S("isKeyWindow"))) Msg(_parent, S("makeKeyWindow"));
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null)
            Console.Error.WriteLine("[windows] finestra MPV agganciata");
        // libmpv mette la sua icona all'app quando crea la finestra: si rimette quella di Cinecore (anche poco dopo,
        // perche' mpv puo' reimpostarla al primo fotogramma).
        MacAppIcon.Restore();
        Avalonia.Threading.DispatcherTimer.RunOnce(MacAppIcon.Restore, TimeSpan.FromSeconds(1));
        Avalonia.Threading.DispatcherTimer.RunOnce(MacAppIcon.Restore, TimeSpan.FromSeconds(4));
        return true;
    }

    /// <summary>La finestra video copre sempre tutta quella del player (anche dopo ridimensionamenti e schermo intero).</summary>
    private bool _wasFullscreen;
    private long _forceOrderUntil;

    public void SyncFrame()
    {
        if (_stopping || !AdoptWindow()) return;
        // AppKit puo' riordinare le finestre figlie alla fine dell'animazione fullscreen, anche se erano state
        // aggiunte con NSWindowBelow. Nello Space fullscreen abbassa il video, lasciando invariato il livello
        // dell'HUD: cambiarlo durante l'animazione puo' annullare l'ingresso nello Space.
        bool fullscreen = Owner is Window owner &&
            (owner.WindowState == WindowState.FullScreen || MacWindowChrome.IsNativeFullscreen(owner));
        long videoLevel = fullscreen ? -1 : 0;
        if ((long)MsgULong(_window, S("level")) != videoLevel) MsgLong(_window, S("setLevel:"), videoLevel);
        // Cambio finestra/schermo intero: per 2 s il video si rimette sempre subito sotto il player. Prima lo si
        // riordinava solo se era davanti al player: se all'uscita restava dietro ad altre finestre o al desktop,
        // l'HUD trasparente mostrava quelle e la finestra sembrava semitrasparente.
        long tick = Environment.TickCount64;
        if (fullscreen != _wasFullscreen) { _wasFullscreen = fullscreen; _forceOrderUntil = tick + 2000; }

        if (!fullscreen)
        {
            IntPtr application = Msg(objc_getClass("NSApplication"), S("sharedApplication"));
            IntPtr ordered = Msg(application, S("orderedWindows"));
            long videoIndex = Msg(ordered, S("indexOfObjectIdenticalTo:"), _window).ToInt64();
            long parentIndex = Msg(ordered, S("indexOfObjectIdenticalTo:"), _parent).ToInt64();
            bool inFront = videoIndex < parentIndex && parentIndex < (long)MsgULong(ordered, S("count")) && MsgBoolValue(application, S("isActive"));
            if (inFront || tick < _forceOrderUntil)
                Msg(_window, S("orderWindow:relativeTo:"), new IntPtr(-1), (long)MsgULong(_parent, S("windowNumber")));
        }
        var frame = MsgRect(_parent, S("frame"));
        var now = MsgRect(_window, S("frame"));
        if (Math.Abs(now.X - frame.X) < .5 && Math.Abs(now.Y - frame.Y) < .5 && Math.Abs(now.W - frame.W) < .5 && Math.Abs(now.H - frame.H) < .5) return;
        MsgFrame(_window, S("setFrame:display:"), frame, true);
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null && _syncLogs++ < 6)
        {
            var after = MsgRect(_window, S("frame"));
            Console.Error.WriteLine($"[windows] mpv da {now.X:0},{now.Y:0} a {frame.X:0},{frame.Y:0} {frame.W:0}x{frame.H:0} -> {after.X:0},{after.Y:0}");
        }
    }
    private int _syncLogs;

    private void DestroyVideoWindow()
    {
        if (_window == IntPtr.Zero) return;
        try
        {
            // La finestra e' di mpv: la chiude lui alla distruzione. Intanto la stacchiamo e la nascondiamo.
            if (_parent != IntPtr.Zero) Msg(_parent, S("removeChildWindow:"), _window);
            Msg(_window, S("orderOut:"), IntPtr.Zero);
        }
        catch { }
        _window = IntPtr.Zero;
    }

#endif
    // ------------------------------------------------------------------ riproduzione
    private IntPtr _ctx;
    private Thread? _events;
    private volatile bool _stopping;
    private readonly Action<string> _line;
    private readonly Action _exited;
    private Timer? _poll;
    private bool _readySent;

    /// <param name="line">Righe del protocollo del VideoHost (ready, pos, ended, error, tracks, media, stats, info).</param>
    public MpvEngine(Action<string> line, Action exited) { _line = line; _exited = exited; }

    public bool IsRunning => (_ctx != IntPtr.Zero || _starting) && !_stopping;

    // Tutte le chiamate a libmpv su un thread suo: su macOS il vo crea e aggiorna la vista passando dal thread
    // principale (dispatch sincrono), e una chiamata bloccante fatta da li' lo bloccherebbe per sempre.
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private Thread? _worker;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Stopped => _stopped.Task;
    private bool _disposing;

    /// <summary>Indirizzo dell'audio separato (video e audio divisi, come su YouTube), aggiunto e scelto all'apertura.</summary>
    public string? ExternalAudio { get; set; }

    public void Start(string file, double startSeconds = 0, string? outputUid = null)
    {
        EnsureResolver();
        _line("info mpv: avvio");
#if CINECORE_WINDOWS
        string geometry = "";
        if (Owner != null)
        {
            _window = WinVideoStack.Surface(_videoWindowTitle);
            CurrentWindow = _window;
            SyncFrame();
        }
#else
        _parent = OwnerWindow();
        // mpv apre la sua finestra gia' dove sta il player (punti, origine in alto a sinistra), poi la si adotta.
        string geometry = "";
        if (_parent != IntPtr.Zero)
        {
            var f = MsgRect(_parent, S("frame"));
            var screen = MsgRect(Msg(_parent, S("screen")), S("frame"));
            geometry = string.Create(CultureInfo.InvariantCulture, $"{(int)f.W}x{(int)f.H}+{(int)(f.X - screen.X)}+{(int)(screen.Y + screen.H - f.Y - f.H)}");
            // Lo schermo del player: senza, mpv tiene la sua finestra sullo schermo principale (AppKit la riporta li'
            // a ogni spostamento) e il video finiva su un altro schermo o un'altra scrivania.
            IntPtr screens = Msg(objc_getClass("NSScreen"), S("screens"));
            long index = Msg(screens, S("indexOfObject:"), Msg(_parent, S("screen"))).ToInt64();
            if (index >= 0 && index < (long)MsgULong(screens, S("count"))) _screenIndex = (int)index;
        }
#endif
        _worker = new Thread(() =>
        {
            foreach (var job in _work.GetConsumingEnumerable())
            {
                try { job(); } catch (Exception ex) { _line("info mpv: " + ex.Message); }
            }
        }) { IsBackground = true, Name = "mpv commands" };
        _worker.Start();
        _starting = true;
        _work.Add(() =>
        {
            try { Init(file, geometry, startSeconds, outputUid); }
            catch (Exception ex) { _line("error " + ex.Message); }
            finally { _starting = false; }
        });
    }

    private volatile bool _starting;
    private int _screenIndex = -1;
    // Una sola libmpv viva alla volta: la parte macOS di mpv (finestra, app) e' unica per processo, e una nuova
    // apertura mentre la precedente si chiude falliva ("loading failed").
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private bool _holding;
    private Avalonia.Threading.DispatcherTimer? _follow;

    private void Init(string file, string geometry, double startSeconds, string? outputUid)
    {
        if (!OneAtATime.Wait(TimeSpan.FromSeconds(8))) throw new InvalidOperationException("mpv occupato");
        _holding = true;
        if (_stopping) return;
        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("libmpv non disponibile");
        void Opt(string k, string v) => mpv_set_option_string(_ctx, U(k), U(v));
#if CINECORE_WINDOWS
        if (_window != IntPtr.Zero) Opt("wid", ((long)_window).ToString(CultureInfo.InvariantCulture));
#endif
        if (geometry.Length > 0) Opt("geometry", geometry);
        if (_screenIndex >= 0) { Opt("screen", _screenIndex.ToString(CultureInfo.InvariantCulture)); Opt("fs-screen", _screenIndex.ToString(CultureInfo.InvariantCulture)); }
        Opt("title", _videoWindowTitle);
        // La finestra la comanda il player: niente ridimensionamenti alle dimensioni del video, bordi, trascinamenti.
        Opt("auto-window-resize", "no");
        Opt("keepaspect-window", "no");
        Opt("border", "no");
        Opt("title-bar", "no");
        Opt("window-dragging", "no");
        Opt("focus-on", "never");
        Opt("native-fs", "no");
        Opt("hidpi-window-scale", "yes"); // La geometria iniziale e' espressa in punti AppKit.
        Opt("config", "no");
        Opt("input-default-bindings", "no");
        Opt("input-vo-keyboard", "no");
        Opt("input-cursor", "no");
        Opt("cursor-autohide", "no");
        Opt("osc", "no");
        Opt("osd-level", "0");
        Opt("ytdl", "no");
        Opt("keep-open", "no");
        Opt("idle", "yes");
        Opt("force-window", "yes");
        // Server spento o irraggiungibile: errore dopo 12 s invece di 30 (resta la schermata di caricamento).
        Opt("curl-connect-timeout", "12");
        Opt("network-timeout", "12");
        // Impostazioni › MPV (come Windows, MpvPlaybackSettings): vo, hwdec, scaler, HDR, deband, audio, sottotitoli.
        // Predefiniti: vo=gpu-next, hwdec=videotoolbox, target-colorspace-hint=yes (HDR in EDR), cache 400 MiB/30 s.
        foreach (var (name, value) in ComponentSettings.MpvOptions()) Opt(name, value);
        // Analisi HDR/SDR aperta: il film gira senza audio (nessuna traccia, quindi niente suono anche in bitstream).
        if (VideoHostClient.Silent) Opt("aid", "no");
        // Audio › Uscita audio: un dispositivo preciso invece della predefinita di macOS.
        string audioUid = outputUid ?? MacAudioDevices.ConfiguredOutputUid;
#if CINECORE_WINDOWS
        Opt("ao", "wasapi");
        if (audioUid.Length > 0) Opt("audio-device", "wasapi/" + audioUid);
#else
        if (audioUid.Length > 0) Opt("audio-device", "coreaudio/" + audioUid);
#endif
        // Niente slang (come Windows): con slang=it mpv accendeva i sottotitoli italiani completi a ogni film anche con
        // l'audio italiano. E subs-fallback=no: altrimenti mpv accende comunque la traccia marcata "default" (spesso i
        // sottotitoli completi). All'avvio restano solo i forzati nella lingua dell'audio (subs-fallback-forced).
        Opt("alang", "it,ita,en,eng");
        Opt("subs-fallback", "no");
        Opt("terminal", "no");
        // Shader della GPU compilati una volta sola (la prima apertura spendeva ~2,5 s a crearli).
        string shaders = System.IO.Path.Combine(global::CinecoreMac.PlatformPaths.CinecoreCaches, "mpv-shaders");
        try { Directory.CreateDirectory(shaders); Opt("gpu-shader-cache-dir", shaders); } catch { }
        // Bitstream (Preferenza uscita audio › Auto): Dolby e DTS originali al ricevitore. CoreAudio lo fa solo in uscita
        // esclusiva; se il dispositivo non accetta il formato compresso mpv torna da solo al PCM.
        // EDID con solo PCM (adattatore USB-C/HDMI): i formati provati dall'utente passano dentro il PCM.
        var (codecs, carrier) = MacAudioDevices.Passthrough(audioUid);
        if (codecs.Length > 0)
        {
            Opt("audio-spdif", codecs);
#if CINECORE_WINDOWS
            // Bitstream su Windows: WASAPI in esclusiva (mpv torna da solo al PCM se il formato non passa).
            Opt("audio-exclusive", "yes");
#else
            Opt("ao", "coreaudio_exclusive,coreaudio");
            if (carrier) Opt("coreaudio-spdif-hack", "yes");
#endif
            SpdifCodecs = MacAudioDevices.PassthroughKey(audioUid);
        }
        if (Environment.GetEnvironmentVariable("CINECORE_MPV_AO") is { Length: > 0 } ao) Opt("ao", ao);
        if (Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST_MUTE") == "1") Opt("mute", "yes");
        // Solo per le prove: il registro di mpv su file.
        if (Environment.GetEnvironmentVariable("CINECORE_MPV_LOG") is { Length: > 0 } log) { Opt("log-file", log); Opt("msg-level", "all=v"); }
        if (startSeconds > 0) Opt("start", startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        // [2026-11-22] Avvio in pausa: mpv prepara uscita video e primo fotogramma, poi parte (EventPlaybackRestart).
        // Partendo subito perdeva 2 fotogrammi nel primo secondo, come madVR senza "delay playback start".
        _holdStart = Environment.GetEnvironmentVariable("CINECORE_MPV_HOLD") != "0";
        if (_holdStart) Opt("pause", "yes");
        // DVD con i menu: una diretta locale prodotta dal navigatore. Niente cache lunga (i menu devono rispondere
        // subito) e formato MPEG-PS dichiarato (l'analisi del formato non aspetta dati che arrivano solo coi pulsanti).
        if (file.Contains("/dvdnav/", StringComparison.Ordinal))
        {
            Opt("cache", "no");
            Opt("demuxer-readahead-secs", "1");
            Opt("demuxer-lavf-format", "mpeg");
            Opt("demuxer-lavf-probesize", "65536");
            Opt("network-timeout", "0");
        }
        int init = mpv_initialize(_ctx);
        MacAppIcon.RestoreSoon(); // libmpv mette la sua icona all'app
        if (init < 0) throw new InvalidOperationException("mpv: " + Marshal.PtrToStringUTF8(mpv_error_string(init)));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // La finestra video segue quella del player anche durante le animazioni (schermo intero, ridimensionamenti).
            _follow = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100), Avalonia.Threading.DispatcherPriority.Background, (_, _) => SyncFrame());
            if (!_stopping) _follow.Start();
        });
        _events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _events.Start();
        bool stream = (file.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || file.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) && !file.Contains("/dvdnav/", StringComparison.Ordinal);
        _line("info mpv: apro il file");
        // Blu-ray (MacDisc.MpvUrl): libbluray dal disco o dalla cartella, titolo principale.
        if (file.StartsWith("cinecore-bd:", StringComparison.Ordinal))
        {
            var parts = file["cinecore-bd:".Length..].Split('|', 2);
            SetProperty("bluray-device", parts[0]);
            file = "bd://" + (parts.Length > 1 ? parts[1] : ""); // bd://mpls/N per un titolo scelto
        }
        Command("loadfile", stream ? CinecorePlayer2025.Utilities.JellyfinClient.PlaybackUrl(file) : file);
        _poll = new Timer(_ => Poll(), null, 250, 250);
    }

    private void Command(params string[] args) => CommandResult(args);

    private int CommandResult(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return -1;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            return mpv_command(_ctx, ptrs);
        }
        finally { foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>I codec ammessi in passthrough per l’uscita aperta.</summary>
    public string SpdifCodecs { get; private set; } = "";
    public string? OutputFormat { get; private set; }

    private void SetProperty(string name, string value) { if (_ctx != IntPtr.Zero) mpv_set_property_string(_ctx, U(name), U(value)); }

    private double Double(string name) => _ctx != IntPtr.Zero && mpv_get_property(_ctx, U(name), FormatDouble, out double v) >= 0 ? v : 0;
    private bool Flag(string name) => _ctx != IntPtr.Zero && mpv_get_property_flag(_ctx, U(name), FormatFlag, out int v) >= 0 && v != 0;
    private long Long(string name) => _ctx != IntPtr.Zero && mpv_get_property_long(_ctx, U(name), FormatInt64, out long v) >= 0 ? v : 0;
    private string? Text(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        IntPtr p = mpv_get_property_string(_ctx, U(name));
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); } finally { mpv_free(p); }
    }

    private int _pollCount;
    private bool _hwRetried;

    private void Poll()
    {
        if (_stopping || !_readySent) return;
        try
        {
            OutputFormat = Text("audio-out-params/format");
            double pos = Double("time-pos"), dur = Double("duration");
            bool paused = Flag("pause");
            _line(string.Create(CultureInfo.InvariantCulture, $"pos {pos:0.000} {dur:0.000} {(paused ? "paused" : "playing")}"));
            if (++_pollCount % 4 == 0)
            {
                string stats = "stats persi=" + Long("frame-drop-count").ToString(CultureInfo.InvariantCulture);
                if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null)
                {
                    stats += string.Create(CultureInfo.InvariantCulture,
                        $" pos={pos:0.000} sorgenteFps={Double("container-fps"):0.###} videoFps={Double("estimated-vf-fps"):0.###} schermoHz={Double("display-fps"):0.###} decoderPersi={Long("decoder-frame-drop-count")} ritardati={Long("vo-delayed-frame-count")} hwdec={Text("hwdec-current") ?? "no"}");
                    Console.Error.WriteLine("[qa-mpv] " + stats);
                }
                _line(stats);
            }
            // VideoToolbox a volte non parte alla prima apertura (sessione rifiutata) e mpv resta in software, con 5-7
            // volte la CPU: un secondo tentativo, una volta sola, poco dopo l'avvio.
            // [2026-11-22] Su Windows il secondo tentativo usa il decoder scelto in Componenti (prima chiedeva videotoolbox,
            // che li' non esiste); se l'utente ha scelto la CPU ("no") non si ritenta.
            string retryHwdec = OperatingSystem.IsWindows() ? ComponentSettings.MpvHwdec() : "videotoolbox";
            if (_pollCount == 6 && !_hwRetried && retryHwdec != "no" && (Text("hwdec-current") ?? "no") is "no" or ""
                && (Text("video-codec") ?? "").ToLowerInvariant() is var codec && (codec.Contains("hevc") || codec.Contains("h.264") || codec.Contains("av1") || codec.Contains("vp9") || codec.Contains("prores")))
            {
                _hwRetried = true;
                if (!_work.IsAddingCompleted)
                    try
                    {
                        _work.Add(() =>
                        {
                            SetProperty("hwdec", "no");
                            SetProperty("hwdec", retryHwdec);
                            Thread.Sleep(800);
                            _line("info mpv: decodifica " + (Text("hwdec-current") ?? "no"));
                            ReportMedia();
                        });
                    }
                    catch (InvalidOperationException) { }
            }
        }
        catch { }
    }

    private void EventLoop()
    {
        IntPtr ctx = _ctx;
        while (!_stopping)
        {
            IntPtr p;
            try { p = mpv_wait_event(ctx, 1); } catch { return; }
            if (p == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(p);
            switch (ev.EventId)
            {
                case EventShutdown:
                    if (!_stopping) { _stopping = true; _exited(); }
                    return;
                case EventFileLoaded:
                    _line("info mpv: file aperto");
                    // Audio in un flusso a parte (YouTube): come SetExternalAudioUrl di Windows.
                    if (!string.IsNullOrWhiteSpace(ExternalAudio))
                    {
                        int added = CommandResult("audio-add", ExternalAudio!, "select", "Audio esterno");
                        _line($"info mpv: audio separato {(added >= 0 ? "aggiunto" : "non aggiunto: " + Marshal.PtrToStringUTF8(mpv_error_string(added)))}");
                        ReportTracks();
                    }
                    break;
                case EventPlaybackRestart:
                    if (_holdStart) { _holdStart = false; SetProperty("pause", "no"); }
                    if (!_readySent)
                    {
                        _readySent = true;
                        _line("ready mpv");
                        ReportTracks();
                        ReportMedia();
                    }
                    break;
                case EventEndFile:
                    var end = ev.Data != IntPtr.Zero ? Marshal.PtrToStructure<MpvEndFile>(ev.Data) : default;
                    if (end.Reason == 0) _line("ended");                       // EOF
                    else if (end.Reason == 4) _line("error " + (Marshal.PtrToStringUTF8(mpv_error_string(end.Error)) ?? "apertura non riuscita"));
                    break;
            }
        }
    }

    // Indici come quelli di LAV, unici fra i gruppi: 1xx audio, 2xx sottotitoli (e 299 = "Nessun sottotitolo").
    private const int AudioBase = 100, SubBase = 200, SubOff = 299;

    private void ReportTracks()
    {
        try
        {
            string? json = Text("track-list");
            if (json == null) { _line("tracks"); return; }
            var parts = new List<string>();
            bool anySub = false, subOn = false;
            foreach (var t in JsonDocument.Parse(json).RootElement.EnumerateArray())
            {
                string type = t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                int id = t.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
                bool selected = t.TryGetProperty("selected", out var se) && se.GetBoolean();
                string title = t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                string lang = t.TryGetProperty("lang", out var la) ? la.GetString() ?? "" : "";
                string codec = t.TryGetProperty("codec", out var co) ? co.GetString() ?? "" : "";
                string name = string.Join(" ", new[] { title, lang.Length > 0 ? $"[{lang}]" : "" }.Where(s => s.Length > 0));
                if (type == "audio")
                {
                    int ch = t.TryGetProperty("demux-channel-count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                    string detail = codec.ToUpperInvariant() + (ch > 0 ? $" {(ch == 6 ? "5.1" : ch == 8 ? "7.1" : ch == 2 ? "2.0" : ch + "ch")}" : "");
                    parts.Add(Entry(AudioBase + id, 1, selected, $"A: {(name.Length > 0 ? name : "Traccia " + id)} ({detail.Trim()})"));
                }
                else if (type == "sub")
                {
                    anySub = true; subOn |= selected;
                    bool forced = t.TryGetProperty("forced", out var f) && f.GetBoolean();
                    bool external = t.TryGetProperty("external", out var ex) && ex.GetBoolean();
                    parts.Add(Entry(SubBase + id, 2, selected, $"S: {(name.Length > 0 ? name : "Traccia " + id)}{(forced ? " [Forced]" : "")}{(external ? " (esterno)" : "")}"));
                }
            }
            if (anySub) parts.Add(Entry(SubOff, 2, !subOn, "No subtitles"));
            _line("tracks " + string.Join('\t', parts));
        }
        catch (Exception ex) { _line("info tracks " + ex.Message); _line("tracks"); }

        static string Entry(int index, int group, bool on, string name) =>
            $"{index}\u001f{group}\u001f{(on ? 1 : 0)}\u001f{name.Replace('\t', ' ').Replace('\u001f', ' ')}";
    }

    private void ReportMedia()
    {
        try
        {
            string hw = Text("hwdec-current") ?? "no";
            string codec = Text("video-codec") ?? "";
            var parts = new List<string>
            {
                "vdec=" + (codec.Length > 0 ? codec : "mpv") + (hw is "no" or "" ? " (software)" : " (" + hw + ")"),
                "vout=mpv (libplacebo)",
                "rver=" + (Text("mpv-version") ?? "mpv"),
            };
            long w = Long("dwidth"), h = Long("dheight");
            if (w > 0 && h > 0) parts.Add($"voutsize={w}x{h}");
            long ch = Long("audio-params/channel-count"), rate = Long("audio-params/samplerate");
            if (ch > 0) parts.Add("aout=" + ch);
            if (rate > 0) parts.Add("arate=" + rate);
            string fmt = Text("audio-params/format") ?? "";
            if (fmt.Length > 0) { parts.Add("afloat=" + (fmt.StartsWith("float", StringComparison.Ordinal) ? "1" : "0")); parts.Add("abits=" + (fmt.Contains("32") || fmt.StartsWith("float") ? "32" : fmt.Contains("24") ? "24" : "16")); }
            _line("media " + string.Join(";", parts));
        }
        catch { }
    }

    /// <summary>Comandi del protocollo del VideoHost tradotti per mpv.</summary>
    public void Send(string command)
    {
        if (!IsRunning) return;
        if (command.StartsWith("rect ", StringComparison.Ordinal)) { SyncFrame(); return; }
        if (!_work.IsAddingCompleted) try { _work.Add(() => Execute(command)); } catch (InvalidOperationException) { }
    }

    private void Execute(string command)
    {
        if (_ctx == IntPtr.Zero || _stopping) return;
        var p = command.Split(' ', 2);
        string arg = p.Length > 1 ? p[1] : "";
        try
        {
            switch (p[0])
            {
                case "play": SetProperty("pause", "no"); break;
#if CINECORE_WINDOWS
                // La finestra del video arriva sotto la barra del titolo di Windows: l'immagine parte sotto.
                case "inset":
                    if (int.TryParse(arg, out int inset) && _window != IntPtr.Zero && WinNative.GetWindowRect(_window, out var wr) && wr.Height > 0)
                        SetProperty("video-margin-ratio-top", (inset / (double)wr.Height).ToString("0.#####", CultureInfo.InvariantCulture));
                    break;
#endif
                // DVD con i menu (MacDvdNav): nuova generazione del flusso, al posto di quella che c'era.
                case "load": Command("loadfile", arg, "replace"); break;
                case "sub-palette": SetProperty("sub-lavc-o", "palette=" + arg); break;
                case "pause": _holdStart = false; SetProperty("pause", "yes"); break;
                case "seek": Command("seek", arg, "absolute+exact"); break;
                case "volume": SetProperty("volume", arg); break;
                case "audio-offset":
                    if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                        SetProperty("audio-delay", (ms / 1000).ToString("0.###", CultureInfo.InvariantCulture));
                    break;
                case "subtitle":
                    string path = arg.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ? arg[2..].Replace('\\', '/') : arg;
                    Command("sub-add", path, "select");
                    ReportTracks();
                    break;
                case "subsoff": SetProperty("sid", "no"); ReportTracks(); break;
                case "select":
                    var s = arg.Split(' ');
                    if (int.TryParse(s[0], out int index))
                    {
                        if (index == SubOff) SetProperty("sid", "no");
                        else if (index >= SubBase) SetProperty("sid", (index - SubBase).ToString(CultureInfo.InvariantCulture));
                        else if (index >= AudioBase) SetProperty("aid", (index - AudioBase).ToString(CultureInfo.InvariantCulture));
                    }
                    ReportTracks();
                    break;
                case "tracks": ReportTracks(); break;
                case "boost":
                    // Amplificazione oltre il 100% (SetVolumeBoostDb di Windows): volume in dB con un limitatore.
                    if (int.TryParse(arg, out int db))
                    {
                        if (db <= 0) Command("af", "remove", "@cineboost");
                        else { Command("af", "remove", "@cineboost"); Command("af", "add", "@cineboost:lavfi=[volume=" + db + "dB,alimiter=level_in=1:level_out=1:limit=0.97:attack=5:release=60:level=disabled]"); }
                    }
                    break;
                case "prop":
                {
                    // Una proprieta' di mpv cambiata dal vivo (es. Immagine / HDR): "prop nome valore".
                    var kv = arg.Split(' ', 2);
                    if (kv.Length == 2) SetProperty(kv[0], kv[1]);
                    break;
                }
#if CINECORE_WINDOWS
                case "margins":
                {
                    // PiP di Windows: il video nel solo riquadro della finestra del PiP (sinistra, sopra, destra, sotto).
                    var m = arg.Split(' ');
                    if (m.Length == 4)
                    {
                        SetProperty("video-margin-ratio-left", m[0]); SetProperty("video-margin-ratio-top", m[1]);
                        SetProperty("video-margin-ratio-right", m[2]); SetProperty("video-margin-ratio-bottom", m[3]);
                    }
                    break;
                }
#endif
                case "margin":
                    // PiP: il video a sinistra, a destra lo spazio dei comandi.
                    SetProperty("video-margin-ratio-right", arg);
                    break;
                case "scale":
                    // Dimensionamento a schermo intero: video-zoom e' in log2 della scala.
                    if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && scale > 0)
                        SetProperty("video-zoom", Math.Log2(scale).ToString("0.#####", CultureInfo.InvariantCulture));
                    break;
                case "stretch":
                    // Schermo intero esteso 3D: il fotogramma doppio riempie tutti gli schermi, un occhio per schermo.
                    SetProperty("keepaspect", arg == "1" ? "no" : "yes");
                    break;
                case "stereo":
                {
                    // 3D → 2D (Enable3D di Windows): un occhio solo (meta' sinistra o alta), con le proporzioni dell'occhio.
                    double w = Double("video-params/w"), h = Double("video-params/h");
                    if (arg == "none" || w < 2 || h < 2) { SetProperty("video-crop", ""); SetProperty("video-aspect-override", "-1"); break; }
                    // Formato del fotogramma senza la forzatura precedente (video-aspect-override vale sul fotogramma intero).
                    SetProperty("video-aspect-override", "-1");
                    double par = Double("video-params/par") is > 0 and var pixel ? pixel : 1;
                    double frame = w * par / h;
                    bool sbs = arg == "sbs";
                    int cw = sbs ? (int)(w / 2) & ~1 : (int)w, ch = sbs ? (int)h : (int)(h / 2) & ~1;
                    SetProperty("video-crop", cw + "x" + ch + "+0+0");
                    double eye = sbs ? (frame >= 2.6 ? frame / 2 : frame) : (frame <= 1.2 ? frame * 2 : frame);
                    // L'override riguarda il fotogramma intero: perche' l'occhio ritagliato abbia il formato "eye".
                    SetProperty("video-aspect-override", (eye * (w / cw) / (h / ch)).ToString("0.#####", CultureInfo.InvariantCulture));
                    break;
                }
                case "crop":
                {
                    // Ritaglio (SetCropAspect di Windows): il centro del fotogramma nel formato scelto, 0 = nessuno.
                    double aspect = double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ? a : 0;
                    double w = Double("video-params/w"), h = Double("video-params/h");
                    if (aspect <= 0 || w < 2 || h < 2) { SetProperty("video-crop", ""); break; }
                    double frame = Double("video-params/aspect") is > 0 and var shown ? shown : w / h;
                    double keepW = aspect > frame ? 1 : aspect / frame, keepH = aspect > frame ? frame / aspect : 1;
                    // Misure e posizione pari: con valori dispari i piani di colore (4:2:0) si spostano.
                    int cw = Math.Max(2, (int)Math.Round(w * keepW) & ~1), ch = Math.Max(2, (int)Math.Round(h * keepH) & ~1);
                    SetProperty("video-crop", cw + "x" + ch + "+" + ((int)((w - cw) / 2) & ~1) + "+" + ((int)((h - ch) / 2) & ~1));
                    break;
                }
                case "key":
                    // Le statistiche del renderer (Ctrl+J su madVR): quelle di mpv.
                    if (arg.Equals("ctrl+j", StringComparison.OrdinalIgnoreCase)) Command("script-binding", "stats/display-stats-toggle");
                    break;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        _stopping = true;
        _follow?.Stop();
        _poll?.Dispose(); _poll = null;
        // La distruzione aspetta la fine del vo, che usa la vista e il thread principale: la si fa sul thread di mpv,
        // e la finestra video si chiude solo dopo, di nuovo sul thread principale. Fino ad allora resta nascosta.
        DestroyVideoWindow();
        if (_worker == null) { _stopped.TrySetResult(); return; }
        try
        {
            _work.Add(() =>
            {
                IntPtr ctx = _ctx;
                _ctx = IntPtr.Zero;
                if (ctx != IntPtr.Zero)
                {
                    try { mpv_wakeup(ctx); } catch { }
                    try { _events?.Join(1500); } catch { }
                    try { mpv_terminate_destroy(ctx); } catch { }
                }
                if (_holding) { _holding = false; OneAtATime.Release(); }
                _stopped.TrySetResult();
            });
        }
        catch (InvalidOperationException) { }
        _work.CompleteAdding();
    }
}
