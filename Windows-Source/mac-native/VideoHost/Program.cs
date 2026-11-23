#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DirectShowLib;

namespace CinecoreVideoHost;

// Uso: VideoHost.exe --file <percorso> [--renderer madvr|mpcvr] [--rect x,y,w,h] [--d3d9] [--audio-delay-ms N]
// Comandi su stdin (una riga ciascuno): play | pause | seek <secondi> | rect x y w h | volume <0-100> | key ctrl+j | snap <file.ppm> | quit
// Stato su stdout:  ready <renderer> | frame | pos <s> <durata> <playing|paused> | info <testo> | error <testo> | ended
internal static class Program
{
    /// <summary>"chiave|nome|valore" in HKCU (DWORD o testo): le scelte delle Impostazioni dell'app Mac.</summary>
    private static void WriteRegistry(bool text, string spec)
    {
        var f = spec.Split('|', 3);
        if (f.Length != 3 || f[0].Length == 0) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(f[0]);
            if (text) key.SetValue(f[1], f[2], Microsoft.Win32.RegistryValueKind.String);
            else if (int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) key.SetValue(f[1], v, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) { Out.Line("info registro non scritto (" + f[0] + "\\" + f[1] + "): " + ex.Message); }
    }

    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] private static extern bool TerminateProcess(IntPtr process, uint code);

    /// <summary>Rete di sicurezza per la chiusura: se lo spegnimento del grafo resta appeso (visto sotto FEX
    /// quando l'app sparisce durante il quit), il processo continuava a decodificare in sottofondo al 100% di CPU.</summary>
    internal static void ExitAfter(int milliseconds) =>
        new System.Threading.Thread(() => { System.Threading.Thread.Sleep(milliseconds); TerminateProcess(GetCurrentProcess(), 0); })
            { IsBackground = true, Name = "uscita" }.Start();

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        string? file = null; string renderer = "madvr"; Rectangle rect = new(100, 100, 1280, 720); bool d3d11 = true; bool topLevel = false;
        int? audioDelayMs = null;
        double startSeconds = 0;
        var madVrSettings = new System.Collections.Generic.List<(string, string)>();
        bool standby = false;
        int Parse(string[] args)
        {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file": file = args[++i]; break;
                case "--renderer": renderer = args[++i].ToLowerInvariant(); break;
                case "--start": startSeconds = Math.Max(0, double.Parse(args[++i], CultureInfo.InvariantCulture)); break;
                case "--rect":
                    var p = args[++i].Split(',');
                    rect = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                    break;
                case "--d3d9": d3d11 = false; break;
                case "--toplevel": topLevel = true; break;
                case "--madvr-set":
                    // --madvr-set hdrMode=toneMapMath | displayBitdepth=10 | ... (impostazioni di madVR per questa sessione)
                    var kv = args[++i].Split('=', 2);
                    if (kv.Length == 2) madVrSettings.Add((kv[0], kv[1]));
                    break;
                case "--reg":
                case "--reg-sz":
                    // Impostazioni dei filtri scelte nell'app Mac: "chiave HKCU|nome|valore", scritte prima di creare il grafo.
                    WriteRegistry(args[i] == "--reg-sz", args[++i]);
                    break;
                case "--audio-device": VideoForm.AudioDevice = args[++i]; break;
                // Audio suonato dall'app Mac (bitstream con madVR/MPC-VR): qui solo il video, l'audio va a un renderer nullo.
                case "--no-audio": VideoForm.NoAudio = true; break;
                // Windows, come il player Windows: --bitstream = passthrough negoziato da LAV Audio (MPC Audio Renderer se
                // c'e'), --pcm = "Forza PCM" (LAV decodifica sempre, anche se nel registro il bitstream e' acceso).
                case "--bitstream": VideoForm.Bitstream = true; break;
                case "--pcm": VideoForm.ForcePcm = true; break;
                // Bitstream automatico con MPC Audio Renderer: i codec che l'uscita accetta (nomi di mpv: ac3,eac3,dts,dts-hd,truehd).
                case "--bitstream-codecs": VideoForm.BitstreamCodecs = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                // DVD con il DVD Navigator di Windows (menu, protezione CSS), come il player Windows.
                case "--dvd": VideoForm.DvdRoot = args[++i]; file ??= VideoForm.DvdRoot; break;
                // Audio in un flusso a parte (YouTube e simili: video e audio separati), come SetExternalAudioUrl di Windows.
                case "--audio-url": VideoForm.ExternalAudio = args[++i]; break;
                // Mac in risparmio energetico: GPU e core rallentati, madVR usa il profilo leggero anche a 24 fps.
                case "--low-power": VideoForm.LowPower = true; break;
                // Solo prove: opzione madVR per questa sessione, ripristinata alla chiusura (--madvr-set invece resta salvata).
                case "--madvr-try":
                    var tkv = args[++i].Split('=', 2);
                    if (tkv.Length == 2) VideoForm.MadVrTry.Add((tkv[0], tkv[1]));
                    break;
                case "--madvr-dump": VideoForm.MadVrDump = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--audio-delay-ms":
                    if (++i == args.Length || !int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay))
                    {
                        Out.Line("error --audio-delay-ms richiede un numero intero");
                        return 2;
                    }
                    audioDelayMs = delay;
                    break;
                // Processo pronto in anticipo: l'app lo avvia prima del film (libreria aperta, film chiuso) e gli manda
                // poi "go [argomenti]" su stdin. Wine, .NET, Direct3D, i ganci e le DLL dei filtri sono gia' caricati.
                case "--standby": standby = true; break;
                // Variabili che nascono col film (canale VideoToolbox) passate al processo pronto: le legge Win32.
                case "--env":
                    var ekv = args[++i].Split('=', 2);
                    if (ekv.Length == 2) Environment.SetEnvironmentVariable(ekv[0], ekv[1]);
                    break;
            }
        }
        return 0;
        }
        int parsed = Parse(args);
        if (parsed != 0) return parsed;
        void InstallGraphics()
        {
            // Windows vero: madVR e MPC-VR presentano da soli, i ganci per Wine (D3D9 -> D3D11, ponte) non servono.
            if (NativeHost.IsNative) return;
            if (renderer != "mpcvr" && Environment.GetEnvironmentVariable("CINECORE_D3D9_SHIM") != "0") D3D9Shim.Install();
            else if (Environment.GetEnvironmentVariable("CINECORE_D3D9_BRIDGE") != "0") D3D9SharedBridge.Install(); // cadenza DXGI misurata anche per MPC-VR
        }
        if (standby)
        {
            string standbyRenderer = renderer;
            InstallGraphics();
            VideoForm.Preload(renderer);
            Out.Line("standby");
            string? go = Console.In.ReadLine();
            if (go == null || !go.StartsWith("go ", StringComparison.Ordinal)) { Console.Out.Flush(); TerminateProcess(GetCurrentProcess(), 0); return 0; }
            Out.Line("info standby: film ricevuto");
            string[] goArgs = System.Text.Json.JsonSerializer.Deserialize<string[]>(go[3..]) ?? Array.Empty<string>();
            parsed = Parse(goArgs);
            if (parsed != 0) return parsed;
            // Il motore preparato e' quello del processo: il cambio di motore passa da un processo nuovo.
            if (renderer != standbyRenderer) { Out.Line("error motore diverso da quello preparato"); return 2; }
        }
        if (file == null) { Out.Line("error manca --file"); return 2; }
        if (!standby) InstallGraphics();
        Application.Run(new VideoForm(file, renderer, rect, d3d11, audioDelayMs) { TopLevelVideo = topLevel, MadVrSettings = madVrSettings, StartSeconds = startSeconds });
        // Grafo gia' fermato e rilasciato. I thread di madVR possono ancora chiamare i nostri ganci (vtable, IAT):
        // lo spegnimento del runtime .NET li farebbe finire su codice smontato (0xC0000005 in chiusura). Si esce subito.
        Console.Out.Flush();
        TerminateProcess(GetCurrentProcess(), 0);
        return 0;
    }
}

internal static class Out
{
    private static readonly object Sync = new();
    public static void Line(string s) { lock (Sync) { Console.Out.WriteLine(s); Console.Out.Flush(); } }

    // Diagnostica (CINECORE_DIAG_TIDS=1): id nativo del thread che entra per la prima volta in un gancio,
    // per leggere i messaggi di Wine "blocked by <tid>" sui blocchi.
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    private static readonly bool Tids = Environment.GetEnvironmentVariable("CINECORE_DIAG_TIDS") == "1";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint, string), byte> SeenTids = new();
    public static void Tid(string what)
    {
        if (!Tids) return;
        uint t = GetCurrentThreadId();
        if (SeenTids.TryAdd((t, what), 0)) Line($"info tid 0x{t:X4} {what}");
    }
}

internal sealed class VideoForm : Form
{
    private static readonly Guid CLSID_LavSplitterSource = new("B98D13E7-55DB-4385-A33D-09FD1BA26338");
    private static readonly Guid CLSID_LavVideo = new("EE30215D-164F-4A92-A4EB-9D4C13390F9F");
    private static readonly Guid CLSID_LavAudio = new("E8E73B6B-4CB3-44A4-BE99-4F7BCB96E491");
    private static readonly Guid CLSID_MadVR = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");
    private static readonly Guid CLSID_MpcVR = new("71F080AA-8661-4093-B15E-4F6903E77D0A");
    private static readonly Guid CLSID_DSoundRender = new("79376820-07D0-11CF-A24D-0020AFD79767");
    private static readonly Guid CLSID_XySubFilter = new("2DFCB782-EC20-4A7C-B530-4577ADB33F21");
    private static readonly Guid CLSID_NullRenderer = new("C1F400A4-3F08-11D3-9F0B-006008039E37");
    private IBaseFilter? _lavVideo, _lavAudio, _source, _xySub;
    private bool _externalSubLoaded;
    /// <summary>Sottotitoli spenti solo nascondendoli in XySubFilter (la traccia di LAV resta quella di prima).</summary>
    private bool _subsHidden;

    /// <summary>Traccia di sottotitoli (gruppo 2 di LAV) attiva nello splitter.</summary>
    private (int Index, string Name)? EnabledSubtitleStream()
    {
        if (_source is not IAMStreamSelect select || select.Count(out int count) != 0) return null;
        for (int i = 0; i < count; i++)
        {
            if (select.Info(i, out AMMediaType mt, out AMStreamSelectInfoFlags flags, out _, out int group, out string name, out object o1, out object o2) != 0) continue;
            if (mt != null) DsUtils.FreeAMMediaType(mt);
            if (o1 != null) try { Marshal.ReleaseComObject(o1); } catch { }
            if (o2 != null) try { Marshal.ReleaseComObject(o2); } catch { }
            if (group == 2 && (flags & (AMStreamSelectInfoFlags.Enabled | AMStreamSelectInfoFlags.Exclusive)) != 0) return (i, name ?? "");
        }
        return null;
    }

    private readonly string _file, _renderer; private readonly bool _d3d11;
    private readonly int? _audioDelayMs;
    private IGraphBuilder? _graph; private IMediaControl? _control; private IMediaSeeking? _seeking;
    private IVideoWindow? _window; private IBasicAudio? _audio; private IBaseFilter? _videoRenderer;
    private readonly System.Windows.Forms.Timer _status = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _stats = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _firstFrame = new() { Interval = 20 };
    private readonly System.Diagnostics.Stopwatch _firstFrameWait = new();
    private TimeSpan _lastCpu; private DateTime _lastCpuAt;
    private int _lastDrawn, _lastDropped;
    private bool _playing;
    private int _automaticAudioDelayMs;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VideoToolboxQuery();
    private VideoToolboxQuery? _videoToolbox;
    public bool TopLevelVideo { get; init; }
    internal static bool LowPower;
    internal static readonly System.Collections.Generic.List<(string Key, string Value)> MadVrTry = new();
    internal static string[] MadVrDump = Array.Empty<string>();
    public double StartSeconds { get; init; }
    public System.Collections.Generic.List<(string Key, string Value)> MadVrSettings { get; init; } = new();

    /// <summary>
    /// Sul Mac, in risparmio energetico, quando la finestra video di Wine e' grande quanto l'area visibile dello
    /// schermo (larghezza piena e altezza piena sotto la barra dei menu) macOS porta lo schermo a 30 aggiornamenti al
    /// secondo: il 24p esce su una griglia a 30 Hz (durate 2-4 vsync, scatti). Misurato il 12/11/2026 con
    /// NSScreen.maximumFramesPerSecond: 2940x1846 -> 30, 2940x1844 o 2938x1846 -> 60. Basta un punto in meno: a
    /// larghezza piena si tolgono 2 pixel in fondo (dietro c'e' il nero dell'app). CINECORE_FULLSCREEN_TRIM=0 lo spegne.
    /// </summary>
    internal static Rectangle TrimFullWidth(Rectangle r)
    {
        if (Environment.GetEnvironmentVariable("CINECORE_FULLSCREEN_TRIM") == "0") return r;
        try
        {
            var screen = Screen.FromRectangle(r).Bounds;
            if (r.Width >= screen.Width && r.Height >= screen.Height - 200 && r.Height > 4) r.Height -= 2;
        }
        catch { }
        return r;
    }

    public VideoForm(string file, string renderer, Rectangle rect, bool d3d11, int? audioDelayMs)
    {
        _file = file; _renderer = renderer; _d3d11 = d3d11;
        _audioDelayMs = audioDelayMs;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = TrimFullWidth(rect);
        BackColor = Color.Black;
        Text = "Cinecore Video";
        _status.Tick += (_, _) => ReportPosition();
        _stats.Tick += (_, _) => ReportStats();
        _firstFrame.Tick += (_, _) => ReportFirstFrame();
        FormClosed += (_, _) => _firstFrame.Dispose();
    }

    // Sul Mac il video e' una finestra a parte sotto quella di Cinecore (HUD, schede, caricamento). Se si attivava,
    // Wine la portava davanti: schermo nero sopra la schermata di caricamento e HUD/schede coperti a tratti.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    // Windows: una finestra che compare va in cima anche senza attivarsi. Il video nero copriva per un attimo il player
    // (schermata di caricamento compresa) finche' il player non lo rimetteva sotto di se'. Nasce in fondo: il player
    // poi lo porta subito sotto la sua finestra.
    private bool _firstShow = true;
    protected override void SetVisibleCore(bool value)
    {
        if (value && _firstShow && NativeHost.IsNative)
        {
            _firstShow = false;
            try
            {
                const uint noMoveSizeActivate = 0x0002 | 0x0001 | 0x0010, showWindow = 0x0040;
                SetWindowPos(Handle, (IntPtr)1 /* HWND_BOTTOM */, 0, 0, 0, 0, noMoveSizeActivate | showWindow);
            }
            catch { }
        }
        base.SetVisibleCore(value);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { BuildGraph(); }
        catch (Exception ex) { Out.Line("error " + ex.GetBaseException().Message.Replace('\n', ' ')); }
        var reader = new Thread(ReadCommands) { IsBackground = true, Name = "stdin" };
        reader.Start();
        _status.Start();
        _lastCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime; _lastCpuAt = DateTime.UtcNow;
        _stats.Start();
        if (HookTrace.On) LoaderProbe.Init();
    }

    private int _frozenSecs, _lastDrawnSeen = -1, _moduleRefresh;
    private long _lastStallPos;

    private void ReportStats()
    {
        try
        {
            var now = DateTime.UtcNow; var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            double cpuPct = (cpu - _lastCpu).TotalMilliseconds / Math.Max(1, (now - _lastCpuAt).TotalMilliseconds) * 100;
            _lastCpu = cpu; _lastCpuAt = now;
            string q = "";
            if (_videoRenderer is IQualProp qp)
            {
                qp.get_FramesDrawn(out int drawn); qp.get_FramesDroppedInRenderer(out int dropped); qp.get_AvgFrameRate(out int fps);
                qp.get_Jitter(out int jitter); qp.get_AvgSyncOffset(out int sync); qp.get_DevSyncOffset(out int dev);
                q = $"disegnati={drawn}(+{drawn - _lastDrawn}) persi={dropped}(+{dropped - _lastDropped}) fps={fps / 100.0:0.00} jitter={jitter}ms sync={sync}ms devSync={dev}ms";
                _lastDrawn = drawn; _lastDropped = dropped;
            }
            long cur = 0; _seeking?.GetCurrentPosition(out cur);
            Out.Line(string.Create(CultureInfo.InvariantCulture, $"stats pos={cur / 1e7:0.0} cpu={cpuPct:0}% {q} | {D3D9SharedBridge.TakeMetrics()}"));
            if (HookTrace.On)
            {
                // Stallo: la posizione avanza (audio) ma madVR non disegna piu'. Elenco una volta a 6 s e una a 20 s.
                bool frozen = _lastDrawnSeen == _lastDrawn && cur > _lastStallPos;
                _frozenSecs = frozen ? _frozenSecs + 1 : 0;
                _lastDrawnSeen = _lastDrawn; _lastStallPos = cur;
                if (_frozenSecs == 0 && ++_moduleRefresh % 5 == 0) LoaderProbe.RefreshModules();
                if (_frozenSecs == 6 || _frozenSecs == 20) Out.Line("diag stallo " + _frozenSecs + "s:" + D3D9SharedBridge.StallDump() + LoaderProbe.Dump());
                if (_frozenSecs == 8) Out.Line("diag thread:" + LoaderProbe.AllThreads(0));
            }
        }
        catch (Exception ex) { Out.Line("info stats " + ex.Message); }
    }

    /// <summary>
    /// madVR in pausa prepara la coda di presentazione: si aspetta che abbia disegnato il primo fotogramma (al massimo
    /// fino a 2,5 s dall'inizio della pausa). -1 se non si sa.
    /// </summary>
    private int PrerollQueue(System.Diagnostics.Stopwatch cue)
    {
        if (_videoRenderer is not IQualProp qp) return -1;
        int drawn = 0;
        while (cue.ElapsedMilliseconds < 2500)
        {
            if (qp.get_FramesDrawn(out drawn) >= 0 && drawn > 0) break;
            Application.DoEvents();
            Thread.Sleep(10);
        }
        return drawn;
    }

    private int _cueGeneration;
    private bool _cueing;

    /// <summary>Dopo un salto in riproduzione: Run quando il grafo e' pronto (S_OK di GetState), al massimo dopo 3 s.</summary>
    private void StartCue()
    {
        int generation = ++_cueGeneration;
        _cueing = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += (_, _) =>
        {
            if (generation != _cueGeneration || _control == null) { timer.Stop(); timer.Dispose(); return; }
            int hr = _control.GetState(0, out _);
            // 0x40237 VFW_S_STATE_INTERMEDIATE: il renderer aspetta ancora il primo campione.
            if (hr == 0x40237 && clock.ElapsedMilliseconds < 3000) return;
            timer.Stop(); timer.Dispose();
            _cueing = false;
            if (_playing) _control.Run();
            if (clock.ElapsedMilliseconds > 250) Out.Line($"info salto pronto in {clock.ElapsedMilliseconds} ms (hr=0x{hr:X8})");
        };
        timer.Start();
    }

    private void ReportFirstFrame()
    {
        // Run() e il primo Present DXGI (anche su swapchain provvisorie da 8x8) non attestano un film a schermo.
        // IQualProp e' fornita sia da madVR sia da MPC-VR. La sonda si ferma subito, senza costo a regime.
        try
        {
            bool bridgeReady = NativeHost.IsNative || _renderer != "madvr" || !_d3d11 || D3D9SharedBridge.HasVideoFrame;
            if (bridgeReady && _videoRenderer is IQualProp qp && qp.get_FramesDrawn(out int drawn) >= 0 && drawn > 0)
            {
                _firstFrame.Stop();
                Out.Line("frame");
            }
        }
        catch { }
        if (_firstFrameWait.ElapsedMilliseconds >= 15000) _firstFrame.Stop();
    }

    /// <summary>
    /// Uscita audio scelta nel menu (--audio-device "nome di CoreAudio"): il renderer "DirectSound: nome" di Wine.
    /// Senza corrispondenza, il DirectSound predefinito (segue l'uscita di macOS).
    /// </summary>
    internal static string? AudioDevice;
    internal static bool Bitstream, ForcePcm;
    internal static string[]? BitstreamCodecs;
    internal static string? DvdRoot;
    private DvdNavigatorMode? _dvd;
    private static readonly Guid CLSID_MpcAudioRenderer = new("601D2A2B-9CDE-40BD-8650-0485E3522727");
    private static readonly string[] LavBitstreamValues = { "Bitstreaming_ac3", "Bitstreaming_eac3", "Bitstreaming_truehd", "Bitstreaming_dts", "Bitstreaming_dtshd" };

    /// <summary>
    /// LAV Audio legge il registro quando nasce: per "Forza PCM" il bitstream si spegne solo per quell'istante e torna
    /// subito come l'aveva l'utente (le impostazioni di LAV restano le sue).
    /// </summary>
    private static IBaseFilter CreateLavAudio(Func<IBaseFilter> create)
    {
        // [2026-11-22] "Auto (bitstream se conviene)" con MPC Audio Renderer: LAV passa in bitstream i codec che l'uscita
        // accetta, sempre solo per l'istante della creazione (il registro di LAV resta come l'ha impostato l'utente).
        var wanted = ForcePcm ? null : BitstreamCodecs;
        if (!NativeHost.IsNative || (!ForcePcm && wanted == null)) return create();
        var saved = new System.Collections.Generic.Dictionary<string, object?>();
        Microsoft.Win32.RegistryKey? key = null;
        try
        {
            key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\LAV\Audio");
            foreach (string name in LavBitstreamValues)
            {
                saved[name] = key.GetValue(name);
                key.SetValue(name, wanted != null && LavCodecWanted(name, wanted) ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            if (wanted != null) Out.Line("info bitstream automatico: " + string.Join(",", wanted));
        }
        catch (Exception ex) { Out.Line("info PCM forzato: registro di LAV non scritto: " + ex.Message); }
        try { return create(); }
        finally
        {
            try
            {
                if (key != null)
                    foreach (var (name, value) in saved)
                        if (value == null) key.DeleteValue(name, false); else key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
            key?.Dispose();
        }
    }

    /// <summary>Renderer audio come il player Windows: con il bitstream MPC Audio Renderer (WASAPI esclusivo) se c'e'.</summary>
    private static IBaseFilter? PreferredAudioRenderer()
    {
        if (NativeHost.IsNative && Bitstream && string.IsNullOrWhiteSpace(AudioDevice))
        {
            try
            {
                var mpc = (IBaseFilter?)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MpcAudioRenderer, true)!);
                if (mpc != null) { Out.Line("info audio: MPC Audio Renderer"); return mpc; }
            }
            catch (Exception ex) { Out.Line("info MPC Audio Renderer non disponibile: " + ex.GetBaseException().Message); }
        }
        return AudioRendererByName(AudioDevice);
    }
    private static bool LavCodecWanted(string lavValue, string[] codecs) => lavValue switch
    {
        "Bitstreaming_ac3" => codecs.Contains("ac3"),
        "Bitstreaming_eac3" => codecs.Contains("eac3"),
        "Bitstreaming_truehd" => codecs.Contains("truehd"),
        "Bitstreaming_dts" => codecs.Contains("dts") || codecs.Contains("dts-hd"),
        "Bitstreaming_dtshd" => codecs.Contains("dts-hd"),
        _ => false,
    };
    internal static bool NoAudio;
    internal static string? ExternalAudio;
    private IBaseFilter? _audioSource;
    private static IBaseFilter? AudioRendererByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            foreach (var dev in DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory))
            {
                string n = dev.Name ?? "";
                if (!n.StartsWith("DirectSound", StringComparison.OrdinalIgnoreCase) || n.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Guid id = typeof(IBaseFilter).GUID;
                dev.Mon.BindToObject(null!, null!, ref id, out object o);
                Out.Line("info audio: " + n);
                return (IBaseFilter)o;
            }
            Out.Line("info audio: nessun DirectSound per '" + name + "', uso il predefinito");
        }
        catch (Exception ex) { Out.Line("info audio " + ex.Message); }
        return null;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    // Come COM (CoCreateInstance): le dipendenze si cercano accanto alla DLL del filtro.
    private static IntPtr LoadLibraryW(string path) => LoadLibraryExW(path, IntPtr.Zero, 0x8 /* LOAD_WITH_ALTERED_SEARCH_PATH */);

    /// <summary>
    /// Pronto in anticipo: carica le DLL dei filtri (DllMain, rilocazioni, codice x86 tradotto da FEX la prima volta) e
    /// quartz con un grafo vuoto. Le istanze restano da creare col film: LAV legge le impostazioni dal registro, che
    /// l'app scrive con gli argomenti del film.
    /// </summary>
    internal static void Preload(string renderer)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var clsids = new System.Collections.Generic.List<Guid> { CLSID_LavSplitterSource, CLSID_LavVideo, CLSID_LavAudio };
        if (renderer == "madvr") clsids.Add(CLSID_MadVR);
        if (renderer == "mpcvr") clsids.Add(CLSID_MpcVR);
        if (renderer is "madvr" or "mpcvr") clsids.Add(CLSID_XySubFilter);
        int loaded = 0;
        foreach (var clsid in clsids)
        {
            try
            {
                // Prima la copia dei componenti di Cinecore (quella che Create usera' col film), poi il registro: una
                // registrazione di sistema vecchia o spostata lasciava madVR da caricare solo all'arrivo del film.
                if (NativeHost.BundledPath(clsid) is { } bundled && LoadLibraryW(bundled) != IntPtr.Zero) { loaded++; continue; }
                using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
                if (key?.GetValue(null) is string path && path.Length > 0 && LoadLibraryW(path) != IntPtr.Zero) loaded++;
                else Out.Line($"info standby: filtro {clsid} non precaricato ({key?.GetValue(null) ?? "non registrato"})");
            }
            catch (Exception ex) { Out.Line($"info standby: filtro {clsid}: {ex.Message}"); }
        }
        try { var graph = new FilterGraph(); Marshal.ReleaseComObject(graph); } catch { }
        Out.Line($"info standby: {loaded}/{clsids.Count} filtri caricati in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
    }

    private static IBaseFilter Create(Guid clsid, string name)
    {
        // Windows: madVR, MPC-VR e XySubFilter dai file dei componenti di Cinecore (come l'app Windows), senza
        // dipendere dalla registrazione di sistema (che puo' puntare a una copia spostata o cancellata).
        if (NativeHost.BundledPath(clsid) is { } bundled)
        {
            try { return NativeHost.CreateFromFile(bundled, clsid); }
            catch (Exception ex) { Out.Line($"info {name}: copia dei componenti non usata ({ex.Message}), provo quella registrata"); }
        }
        var type = Type.GetTypeFromCLSID(clsid, true)!;
        return (IBaseFilter)(Activator.CreateInstance(type) ?? throw new InvalidOperationException(name + " non creato"));
    }

    private static bool IsAudioPin(IPin pin)
    {
        try
        {
            if (pin.EnumMediaTypes(out IEnumMediaTypes types) != 0 || types == null) return false;
            var mt = new AMMediaType[1];
            try
            {
                while (types.Next(1, mt, IntPtr.Zero) == 0)
                {
                    bool audio = mt[0].majorType == MediaType.Audio;
                    DsUtils.FreeAMMediaType(mt[0]);
                    if (audio) return true;
                }
            }
            finally { Marshal.ReleaseComObject(types); }
        }
        catch { }
        return false;
    }

    private void BuildGraph()
    {
        _graph = (IGraphBuilder)new FilterGraph();
        IBaseFilter source;
        if (DvdRoot != null)
        {
            _dvd = DvdNavigatorMode.Open(_graph, DvdRoot);
            _dvd.MapScreenPoint = ScreenToDvdFrame;
            source = _source = _dvd.Navigator;
        }
        else
        {
        source = _source = Create(CLSID_LavSplitterSource, "LAV Splitter Source");
        // Come RenderingEngine di Windows: prima nel grafo, poi il caricamento (per gli indirizzi di rete conta).
        DsError.ThrowExceptionForHR(_graph.AddFilter(source, "LAV Splitter Source"));
        int loadHr = ((IFileSourceFilter)source).Load(_file, null);
        if (loadHr < 0) Out.Line($"info LAV Splitter Source: caricamento hr=0x{loadHr:X8}" + (_file.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? " (flusso di rete)" : ""));
        DsError.ThrowExceptionForHR(loadHr);
        }
        // Audio separato: un secondo LAV Splitter Source nello stesso grafo (lo stesso orologio e la stessa ricerca).
        if (!string.IsNullOrWhiteSpace(ExternalAudio))
        {
            try
            {
                var audioSource = Create(CLSID_LavSplitterSource, "LAV Splitter Source (audio)");
                DsError.ThrowExceptionForHR(_graph.AddFilter(audioSource, "LAV Splitter Source (audio)"));
                DsError.ThrowExceptionForHR(((IFileSourceFilter)audioSource).Load(ExternalAudio, null));
                _audioSource = audioSource;
                Out.Line("info audio separato caricato");
            }
            catch (Exception ex)
            {
                Out.Line("info audio separato non caricato, resta quello del file: " + ex.Message);
                _audioSource = null;
            }
        }
        _lavVideo = Create(CLSID_LavVideo, "LAV Video");
        if (_renderer is "madvr" or "mpcvr") TryInstallVideoToolbox();
        DsError.ThrowExceptionForHR(_graph.AddFilter(_lavVideo, "LAV Video Decoder"));
        var lavAudio = _lavAudio = CreateLavAudio(() => Create(CLSID_LavAudio, "LAV Audio"));
        DsError.ThrowExceptionForHR(_graph.AddFilter(lavAudio, "LAV Audio Decoder"));
        bool autoDelay = _audioDelayMs == null && _renderer == "madvr" && _d3d11 && !NativeHost.IsNative
            && Environment.GetEnvironmentVariable("CINECORE_D3D9_SHIM") != "0"
            && D3D9SharedBridge.UsesFrameDelay;
        var lavSettings = autoDelay || _audioDelayMs.HasValue ? lavAudio as ILavAudioSettings : null;
        if (lavSettings != null)
            DsError.ThrowExceptionForHR(lavSettings.SetRuntimeConfig(1));

        bool nullMode = _renderer == "null";
        if (_renderer == "mpcvr" && !NativeHost.IsNative) ConfigureMpcVr();
        _videoRenderer = Create(nullMode ? CLSID_NullRenderer : _renderer == "mpcvr" ? CLSID_MpcVR : CLSID_MadVR, _renderer);
        if (_renderer == "madvr")
        {
            if (!NativeHost.IsNative) { MadVrImports.Install(); SafeTerminate.Install("madVR64.ax"); MadVrThreadPool.Install(); }
            ConfigureMadVr(_videoRenderer);
            // Profilo leggero gia' prima della connessione (formato dichiarato dallo splitter): niente riconfigurazione
            // di madVR a video avviato, che costava una trentina di fotogrammi all'inizio.
            var (w0, d0) = _dvd != null ? (0, 0L) : SourceVideoFormat(source);
            if (d0 > 0 && w0 > 0) { ApplyLightProfile(_videoRenderer, d0, w0); _lightApplied = true; }
        }
        DsError.ThrowExceptionForHR(_graph.AddFilter(_videoRenderer, nullMode ? "Null Renderer" : _renderer == "mpcvr" ? "MPC Video Renderer" : "madVR"));
        // Modalita' "null": sola decodifica, senza audio ne' orologio (misura la velocita' massima di LAV).
        // Con l'audio suonato dall'app nessun renderer audio: le uscite audio restano scollegate (un renderer nullo
        // accetterebbe anche il video) e l'orologio del grafo e' quello di sistema.
        var audioRenderer = nullMode ? Create(CLSID_NullRenderer, "Null audio") : NoAudio ? null : (PreferredAudioRenderer() ?? Create(CLSID_DSoundRender, "DirectSound"));
        if (audioRenderer != null) DsError.ThrowExceptionForHR(_graph.AddFilter(audioRenderer, nullMode ? "Null Audio" : "DirectSound Audio Renderer"));
        else Out.Line("info audio: suonato dall'app");

        // Sottotitoli come su Windows: con madVR e MPC-VR li disegna XySubFilter (ISubRenderConsumer del renderer).
        if (_renderer is "madvr" or "mpcvr" && DvdRoot == null)
        {
            try
            {
                _xySub = Create(CLSID_XySubFilter, "XySubFilter");
                DsError.ThrowExceptionForHR(_graph.AddFilter(_xySub, "XySubFilter"));
                Out.Line("info XySubFilter aggiunto");
                SafeTerminate.Install("XySubFilter.dll");
            }
            catch (Exception ex)
            {
                Out.Line("info XySubFilter non disponibile: " + ex.Message);
                if (_xySub != null) { try { _graph.RemoveFilter(_xySub); } catch { } _xySub = null; }
            }
        }

        // MPC-VR su Windows come la release Windows (RenderingEngine.ApplyMpcvrFilterConfig e
        // TryApplyInitialMpcvrVideoHost): opzioni del filtro e finestra PRIMA della connessione. Collegando i pin senza
        // finestra, con RTX Video HDR attivo il driver NVIDIA andava in ricorsione (0xC00000FD in nvwgf2umx.dll).
        if (_renderer == "mpcvr" && NativeHost.IsNative && _videoRenderer is IExFilterConfig mpcvrConfig)
        {
            int hrRedraws = mpcvrConfig.Flt_SetBool("lessRedraws", true);
            int hrFullscreen = mpcvrConfig.Flt_SetBool("d3dFullscreenControl", false);
            int hrDeep = mpcvrConfig.Flt_SetBool("allowDeepColorBitmaps", true);
            Out.Line($"info mpcvr: lessRedraws=0x{hrRedraws:X8} d3dFullscreenControl=0x{hrFullscreen:X8} allowDeepColorBitmaps=0x{hrDeep:X8}");
        }

        // Come Cinecore: madVR (e MPC-VR su Windows) ricevono la finestra PRIMA della connessione.
        if ((_renderer == "madvr" || _renderer == "mpcvr" && NativeHost.IsNative) && _videoRenderer is IVideoWindow pre)
        {
            pre.put_Owner(Handle);
            pre.put_WindowStyle(WindowStyle.Child | WindowStyle.ClipSiblings | WindowStyle.ClipChildren);
            pre.put_MessageDrain(Handle);
            pre.SetWindowPosition(0, _inset, ClientSize.Width, Math.Max(2, ClientSize.Height - _inset));
            Out.Line($"info {_renderer}: finestra collegata prima della connessione");
        }

        // Ogni uscita dello splitter verso i renderer gia' nel grafo (niente filtri di sistema a caso). Con l'audio
        // separato le uscite audio del file restano scollegate e l'audio arriva dal secondo splitter.
        var graph2 = (IFilterGraph2)_graph;
        var outputs = new System.Collections.Generic.List<IPin>();
        // DVD: collegamenti diretti Navigator -> LAV (video, sottoimmagini, audio), poi le uscite di LAV ai renderer.
        if (_dvd != null) outputs.AddRange(_dvd.Connect(_graph, _lavVideo!, lavAudio, NoAudio || audioRenderer == null));
        else
        foreach (var filter in _audioSource != null ? new[] { source, _audioSource } : new[] { source })
        {
            DsError.ThrowExceptionForHR(filter.EnumPins(out IEnumPins list));
            var next = new IPin[1];
            while (list.Next(1, next, IntPtr.Zero) == 0)
            {
                bool audio = IsAudioPin(next[0]);
                if (_audioSource != null && (filter == source ? audio : !audio)) { Marshal.ReleaseComObject(next[0]); continue; }
                if (NoAudio && audio) { Marshal.ReleaseComObject(next[0]); continue; }
                outputs.Add(next[0]);
            }
        }
        var one = new IPin[1];
        foreach (var pin in outputs)
        {
            one[0] = pin;
            one[0].QueryDirection(out PinDirection dir);
            if (dir != PinDirection.Output) continue;
            int hr = graph2.RenderEx(one[0], AMRenderExFlags.RenderToExistingRenderers, IntPtr.Zero);
            // Uscita dei sottotitoli non collegata da sola: diretta al primo ingresso libero di XySubFilter.
            if (hr < 0 && _xySub != null && DsFindPin.ByConnectionStatus(_xySub, PinConnectedStatus.Unconnected, 0) is { } xyIn)
            {
                int direct = _graph.Connect(one[0], xyIn);
                if (direct >= 0) hr = direct;
                Marshal.ReleaseComObject(xyIn);
            }
            one[0].QueryPinInfo(out PinInfo info);
            Out.Line($"info pin '{info.name}' render hr=0x{hr:X8}");
            DsUtils.FreePinInfo(info);
        }

        _window = _graph as IVideoWindow;
        if (_renderer != "mpcvr") { /* gia' collegata */ }
        else if (_window != null && TopLevelVideo)
        {
            // Prova: il renderer usa la sua finestra principale invece di una figlia della nostra.
            _window.put_WindowStyle(WindowStyle.Popup | WindowStyle.Visible);
            _window.SetWindowPosition(Left, Top + (Environment.GetEnvironmentVariable("CINECORE_TOPLEVEL_BELOW") == "1" ? Height + 20 : 0), Width, Height);
        }
        else if (_window != null)
        {
            _window.put_Owner(Handle);
            _window.put_WindowStyle(WindowStyle.Child | WindowStyle.ClipSiblings | WindowStyle.ClipChildren);
            _window.put_MessageDrain(Handle);
            try { _window.put_AutoShow(OABool.True); } catch { }
            FitVideo();
        }
        _control = (IMediaControl)_graph;
        _seeking = (IMediaSeeking)_graph;
        _audio = _graph as IBasicAudio;
        long frameDuration = _renderer == "madvr" && _d3d11 ? FrameDuration(_videoRenderer) : 0;
        if (frameDuration > 0 && !NativeHost.IsNative)
            D3D9Shim.SetFrameDuration(frameDuration);
        if (_renderer == "madvr" && frameDuration > 0 && !_lightApplied) ApplyLightProfile(_videoRenderer, frameDuration, VideoWidth(_videoRenderer));
        if (Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST_MUTE") == "1"
            && audioRenderer != null && HasConnectedPin(audioRenderer, PinDirection.Input))
            DsError.ThrowExceptionForHR((_audio ?? throw new InvalidOperationException("Volume audio non disponibile")).put_Volume(-10000));
        _automaticAudioDelayMs = _audioDelayMs ?? (autoDelay && frameDuration > 0 ? (int)Math.Round(frameDuration * D3D9SharedBridge.FrameDelayFrames / 10000.0) : 0);
        if ((autoDelay || _audioDelayMs.HasValue) && HasConnectedPin(lavAudio, PinDirection.Output))
            ApplyAudioDelay(lavSettings, _automaticAudioDelayMs);
        // L'audio dell'app deve avere lo stesso ritardo che qui compensa i fotogrammi trattenuti dal ponte di madVR.
        // Sempre: se l'app accende l'audio nativo a film avviato (cambio d'uscita) le serve lo stesso ritardo.
        Out.Line($"audiodelay {_automaticAudioDelayMs}");
        DescribeGraph();
        if (_videoRenderer is IMadVRCommand command)
            Out.Line($"info madVR disableExclusiveMode hr=0x{command.SendCommandBool("disableExclusiveMode", true):X8}");
        if (_renderer == "null") DsError.ThrowExceptionForHR(((IMediaFilter)_graph).SetSyncSource(null));
        if (StartSeconds > 0)
            DsError.ThrowExceptionForHR(_seeking.SetPositions((long)(StartSeconds * 1e7), AMSeekingSeekingFlags.AbsolutePositioning, null, AMSeekingSeekingFlags.NoPositioning));
        // Prima in pausa: il renderer riceve i primi fotogrammi e riempie le code prima che l'orologio parta (con Run
        // diretto madVR perdeva 5-16 fotogrammi nel primo secondo). Al massimo 2 s (flussi di rete lenti), poi Run.
        if (_renderer is "madvr" or "mpcvr" && Environment.GetEnvironmentVariable("CINECORE_PREROLL") != "0")
        {
            var cue = System.Diagnostics.Stopwatch.StartNew();
            int pauseHr = _control.Pause();
            int stateHr = pauseHr == 1 ? _control.GetState(2000, out _) : pauseHr;
            int queued = PrerollQueue(cue);
            Out.Line($"info avvio in pausa: {cue.ElapsedMilliseconds} ms (hr=0x{stateHr:X8}{(queued >= 0 ? ", coda " + queued : "")})");
        }
        DsError.ThrowExceptionForHR(_control.Run());
        _playing = true;
        try { _window?.put_Visible(OABool.True); } catch { }
        FitVideo();
        var probe = new System.Windows.Forms.Timer { Interval = 3000 };
        probe.Tick += (_, _) =>
        {
            probe.Stop(); DescribeChildren();
            if (_lavVideo is ILAVVideoStatus st)
                try { Out.Line("info LAV Video decoder attivo: " + Marshal.PtrToStringUni(st.GetActiveDecoderName())); } catch (Exception ex) { Out.Line("info LAV status " + ex.Message); }
            else Out.Line("info LAV Video senza ILAVVideoStatus");
            ReportMedia();
        };
        probe.Start();
        ThreadCensus.Install();
        Out.Line("ready " + _renderer + (_renderer == "madvr" ? (_d3d11 ? " d3d11" : " d3d9") : ""));
        Out.Tid("main (WinForms)");
        if (Environment.GetEnvironmentVariable("CINECORE_DIAG_TIDS") == "1")
            try
            {
                foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                    Out.Line($"info modulo 0x{(long)m.BaseAddress:X} +0x{m.ModuleMemorySize:X} {m.ModuleName}");
            }
            catch (Exception ex) { Out.Line("info moduli " + ex.Message); }
        _firstFrameWait.Restart();
        _firstFrame.Start();
        ReportTracks();
    }

    private void TryInstallVideoToolbox()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_VT_DLL") is not { Length: > 0 } path) return;
        IntPtr bridge = IntPtr.Zero;
        try
        {
            bridge = NativeLibrary.Load(path);
            var install = Marshal.GetDelegateForFunctionPointer<VideoToolboxQuery>(NativeLibrary.GetExport(bridge, "cinecore_vt_install"));
            var active = Marshal.GetDelegateForFunctionPointer<VideoToolboxQuery>(NativeLibrary.GetExport(bridge, "cinecore_vt_active"));
            if (install() == 1)
            {
                // I ganci di LAV restano attivi fino alla chiusura del processo.
                _videoToolbox = active;
                Out.Line("info VideoToolbox bridge installato");
                return;
            }
            Out.Line("info VideoToolbox bridge incompatibile con LAV");
        }
        catch (Exception ex) { Out.Line("info VideoToolbox bridge " + ex.GetType().Name); }
        if (bridge != IntPtr.Zero) NativeLibrary.Free(bridge);
    }

    /// <summary>
    /// Flussi dello splitter (IAMStreamSelect di LAV Splitter): una riga "tracks" con le voci separate da TAB, ognuna
    /// "indice US gruppo US scelta US nome" (US = 0x1F). Gruppi di LAV: 0 video, 1 audio, 2 sottotitoli, 18 edizioni.
    /// </summary>
    internal static bool IsSubtitleOffOption(string? name) =>
        !string.IsNullOrWhiteSpace(name) && System.Text.RegularExpressions.Regex.IsMatch(name.Trim(),
            @"^(?:S:\s*)?(?:\[|\()?\s*(?:no subtitles?|subtitles? off|off|none|disabled|nessun[oi] sottotitol[oi]|sottotitoli disattivati|disattivati)\s*(?:\]|\))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void ReportTracks()
    {
        if (_source is not IAMStreamSelect select) { Out.Line("tracks"); return; }
        try
        {
            DsError.ThrowExceptionForHR(select.Count(out int count));
            var parts = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (select.Info(i, out AMMediaType mt, out AMStreamSelectInfoFlags flags, out int lcid, out int group, out string name, out object o1, out object o2) != 0) continue;
                if (mt != null) DsUtils.FreeAMMediaType(mt);
                if (o1 != null) try { Marshal.ReleaseComObject(o1); } catch { }
                if (o2 != null) try { Marshal.ReleaseComObject(o2); } catch { }
                bool on = (flags & (AMStreamSelectInfoFlags.Enabled | AMStreamSelectInfoFlags.Exclusive)) != 0;
                // Sottotitoli solo nascosti: per l'app risulta scelta la voce "No subtitles".
                if (_subsHidden && group == 2) on = IsSubtitleOffOption(name ?? "");
                // Con l'audio separato le tracce audio del file restano scollegate: si sente quella esterna.
                if (_audioSource != null && group == 1) on = false;
                string clean = (name ?? "").Replace('\t', ' ').Replace('\u001f', ' ').Replace('\n', ' ').Replace('\r', ' ');
                parts.Add($"{i}\u001f{group}\u001f{(on ? 1 : 0)}\u001f{clean}");
            }
            if (_audioSource != null) parts.Add($"900\u001f1\u001f1\u001fA: Audio esterno");
            Out.Line("tracks " + string.Join('\t', parts));
        }
        catch (Exception ex) { Out.Line("info tracks " + ex.Message); Out.Line("tracks"); }
    }

    /// <summary>
    /// Dati per la scheda Info che conosce solo il grafo: decoder video attivo, formato tra LAV Video e il renderer,
    /// formato PCM tra LAV Audio e l'uscita. Una riga "media chiave=valore;chiave=valore".
    /// </summary>
    private void ReportMedia()
    {
        // [2026-11-22] Il bitstream si sa davvero solo qui: LAV Audio cambia formato al primo pacchetto (con MPC Audio
        // Renderer il DTS-HD diventa 8 canali a 192 kHz dopo il Run), la riga detta alla costruzione del grafo diceva 0.
        ReportBitstream();
        var parts = new System.Collections.Generic.List<string>();
        try
        {
            if (_videoToolbox?.Invoke() == 1) parts.Add("vdec=VideoToolbox");
            else if (_lavVideo is ILAVVideoStatus st) parts.Add("vdec=" + Marshal.PtrToStringUni(st.GetActiveDecoderName()));
        }
        catch { }
        try
        {
            if (_lavVideo != null && ConnectionType(_lavVideo, PinDirection.Output) is { } v)
            {
                parts.Add("vout=" + SubtypeName(v.Subtype));
                if (v.Width > 0) parts.Add($"voutsize={v.Width}x{v.Height}");
            }
        }
        catch { }
        try
        {
            var pin = _lavAudio == null ? null : DsFindPin.ByDirection(_lavAudio, PinDirection.Output, 0);
            if (pin != null)
            {
                var mt = new AMMediaType();
                try
                {
                    if (pin.ConnectionMediaType(mt) == 0 && mt.formatPtr != IntPtr.Zero && mt.formatSize >= 18)
                    {
                        short tag = Marshal.ReadInt16(mt.formatPtr, 0);
                        int ch = Marshal.ReadInt16(mt.formatPtr, 2), rate = Marshal.ReadInt32(mt.formatPtr, 4), bits = Marshal.ReadInt16(mt.formatPtr, 14);
                        bool isFloat = tag == 3;
                        if (tag == unchecked((short)0xFFFE) && mt.formatSize >= 40)
                        {
                            // WAVEFORMATEXTENSIBLE: bit validi e tipo (PCM o float) dal sottoformato.
                            int valid = Marshal.ReadInt16(mt.formatPtr, 18);
                            if (valid > 0) bits = valid;
                            isFloat = Marshal.ReadInt32(mt.formatPtr, 24) == 3;
                        }
                        parts.Add(string.Create(CultureInfo.InvariantCulture, $"aout={ch};arate={rate};abits={bits};afloat={(isFloat ? 1 : 0)}"));
                    }
                }
                finally { DsUtils.FreeAMMediaType(mt); Marshal.ReleaseComObject(pin); }
            }
        }
        catch { }
        try
        {
            // Versione del renderer dal suo file (come la scheda Info di Windows: "madVR 0.92.17").
            Guid clsid = _renderer == "mpcvr" ? CLSID_MpcVR : CLSID_MadVR;
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid.ToString().ToUpperInvariant()}}}\InprocServer32");
            if (key?.GetValue(null) is string path && System.IO.File.Exists(path))
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string ver = info.FileMajorPart > 0 || info.FileMinorPart > 0
                    ? $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}" + (info.FilePrivatePart > 0 ? "." + info.FilePrivatePart : "")
                    : info.FileVersion ?? "";
                if (ver.Length > 0) parts.Add("rver=" + ver);
            }
        }
        catch { }
        parts.Add("adev=DirectSound");
        Out.Line("media " + string.Join(";", parts));
    }

    private static (Guid Subtype, int Width, int Height)? ConnectionType(IBaseFilter filter, PinDirection direction)
    {
        var pin = DsFindPin.ByDirection(filter, direction, 0);
        if (pin == null) return null;
        var mt = new AMMediaType();
        try
        {
            if (pin.ConnectionMediaType(mt) != 0) return null;
            int w = 0, h = 0;
            if (mt.formatType == FormatType.VideoInfo2 && mt.formatSize >= Marshal.SizeOf<VideoInfoHeader2>())
            {
                // L'immagine e' rcSource; BmiHeader.Width puo' essere il passo di memoria (4096 per un 3840).
                var vi = Marshal.PtrToStructure<VideoInfoHeader2>(mt.formatPtr)!;
                w = vi.SrcRect.right - vi.SrcRect.left; h = vi.SrcRect.bottom - vi.SrcRect.top;
                if (w <= 0 || h <= 0) { w = vi.BmiHeader.Width; h = Math.Abs(vi.BmiHeader.Height); }
            }
            else if (mt.formatType == FormatType.VideoInfo && mt.formatSize >= Marshal.SizeOf<VideoInfoHeader>())
            { var vi = Marshal.PtrToStructure<VideoInfoHeader>(mt.formatPtr)!; w = vi.BmiHeader.Width; h = Math.Abs(vi.BmiHeader.Height); }
            return (mt.subType, w, h);
        }
        finally { DsUtils.FreeAMMediaType(mt); Marshal.ReleaseComObject(pin); }
    }

    /// <summary>Proporzioni dichiarate nel formato del collegamento (VIDEOINFOHEADER2), senza interrogare i filtri.</summary>
    private static double? PictureAspect(IBaseFilter renderer)
    {
        var pin = DsFindPin.ByDirection(renderer, PinDirection.Input, 0);
        if (pin == null) return null;
        var mt = new AMMediaType();
        try
        {
            if (pin.ConnectionMediaType(mt) != 0 || mt.formatType != FormatType.VideoInfo2 || mt.formatSize < Marshal.SizeOf<VideoInfoHeader2>()) return null;
            var vi = Marshal.PtrToStructure<VideoInfoHeader2>(mt.formatPtr)!;
            return vi.PictAspectRatioX > 0 && vi.PictAspectRatioY > 0 ? vi.PictAspectRatioX / (double)vi.PictAspectRatioY : null;
        }
        catch { return null; }
        finally { DsUtils.FreeAMMediaType(mt); Marshal.ReleaseComObject(pin); }
    }

    /// <summary>Sottotipi video DirectShow: i primi 4 byte del GUID sono il FOURCC (NV12, P010, ...).</summary>
    private static string SubtypeName(Guid subtype)
    {
        var b = subtype.ToByteArray();
        string fourcc = Encoding.ASCII.GetString(b, 0, 4);
        return fourcc.All(c => c >= 32 && c < 127) ? fourcc.Trim() : subtype.ToString();
    }

    private static bool HasConnectedPin(IBaseFilter filter, PinDirection direction)
    {
        var pin = DsFindPin.ByDirection(filter, direction, 0);
        if (pin == null) return false;
        try
        {
            if (pin.ConnectedTo(out IPin downstream) != 0) return false;
            Marshal.ReleaseComObject(downstream);
            return true;
        }
        finally { Marshal.ReleaseComObject(pin); }
    }

    private static long FrameDuration(IBaseFilter renderer)
    {
        var pin = DsFindPin.ByDirection(renderer, PinDirection.Input, 0);
        if (pin == null) return 0;
        var format = new AMMediaType();
        try
        {
            if (pin.ConnectionMediaType(format) < 0 || format.formatPtr == IntPtr.Zero) return 0;
            if (format.formatType == FormatType.VideoInfo2 && format.formatSize >= Marshal.SizeOf<VideoInfoHeader2>())
                return Marshal.PtrToStructure<VideoInfoHeader2>(format.formatPtr)!.AvgTimePerFrame;
            if (format.formatType == FormatType.VideoInfo && format.formatSize >= Marshal.SizeOf<VideoInfoHeader>())
                return Marshal.PtrToStructure<VideoInfoHeader>(format.formatPtr)!.AvgTimePerFrame;
            return 0;
        }
        finally
        {
            DsUtils.FreeAMMediaType(format);
            Marshal.ReleaseComObject(pin);
        }
    }

    // Profilo leggero per le cadenze alte o, se scelto, in risparmio energetico. Si sceglie prima di avviare
    // il grafo, cosi' il passaggio a schermo intero non cambia anche gli algoritmi di madVR durante il film.
    private readonly System.Collections.Generic.List<(string Key, string Value)> _restoreSettings = new();
    private static readonly (string Key, string Value)[] LightProfile =
        { ("chromaUp", "Bicubic60"), ("lumaUp", "Bicubic60"), ("lumaDown", "Bicubic150"), ("superChromaRes", "false") };

    private bool _lightApplied;

    private static (int Width, long FrameDuration) SourceVideoFormat(IBaseFilter source)
    {
        if (source.EnumPins(out IEnumPins pins) != 0) return (0, 0);
        var one = new IPin[1];
        try
        {
            while (pins.Next(1, one, IntPtr.Zero) == 0)
            {
                try
                {
                    if (one[0].EnumMediaTypes(out IEnumMediaTypes types) != 0) continue;
                    var mt = new AMMediaType[1];
                    while (types.Next(1, mt, IntPtr.Zero) == 0)
                    {
                        try
                        {
                            if (mt[0].majorType != MediaType.Video || mt[0].formatPtr == IntPtr.Zero) continue;
                            if (mt[0].formatType == FormatType.VideoInfo2 && mt[0].formatSize >= Marshal.SizeOf<VideoInfoHeader2>())
                            {
                                var v = Marshal.PtrToStructure<VideoInfoHeader2>(mt[0].formatPtr)!;
                                return (v.BmiHeader.Width, v.AvgTimePerFrame);
                            }
                            if (mt[0].formatType == FormatType.VideoInfo && mt[0].formatSize >= Marshal.SizeOf<VideoInfoHeader>())
                            {
                                var v = Marshal.PtrToStructure<VideoInfoHeader>(mt[0].formatPtr)!;
                                return (v.BmiHeader.Width, v.AvgTimePerFrame);
                            }
                        }
                        finally { DsUtils.FreeAMMediaType(mt[0]); }
                    }
                    Marshal.ReleaseComObject(types);
                }
                finally { Marshal.ReleaseComObject(one[0]); }
            }
        }
        finally { Marshal.ReleaseComObject(pins); }
        return (0, 0);
    }

    private void ApplyLightProfile(IBaseFilter renderer, long frameDuration, int width)
    {
        if (Environment.GetEnvironmentVariable("CINECORE_MADVR_LIGHT") == "0" || renderer is not IMadVRSettings s) return;
        // Risparmio energetico (11/11, MacBook Air M4, Interstellar 4K HDR a 24 fps): con Jinc per ridurre il 4K alla
        // finestra madVR impiegava ~35 ms a fotogramma su 41,7 e perdeva 5-10 fotogrammi/s; con Bicubic150 0-0,5/s.
        // Solo cadenze alte (oltre 40 fps) o risparmio energetico con il profilo leggero scelto dall'utente. Fino al
        // 12/11/2026 scattava anche per i video piccoli (ingrandimento di almeno 2x): Bicubic al posto di NGU proprio
        // dove l'upscaling conta di piu' (segnalato dall'utente: qualita' peggiore che su Windows).
        bool force = frameDuration < 250_000 || LowPower;
        if (!force) return;
        foreach (var (key, value) in LightProfile)
        {
            if (!force && MadVrSettings.Any(setting => setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                continue;
            string? old = value is "true" or "false" ? (s.SettingsGetBoolean(key, out bool b) ? (b ? "true" : "false") : null) : ReadString(s, key);
            if (old == null || old == value) continue;
            bool ok = value is "true" or "false" ? s.SettingsSetBoolean(key, value == "true") : s.SettingsSetString(key, value);
            if (ok) RememberOriginal(key, old);
            Out.Line($"info madVR profilo leggero ({width}px {10_000_000.0 / frameDuration:0.##} fps{(LowPower ? ", risparmio energetico" : "")}): {key} {old} -> {value} {(ok ? "ok" : "non accettato")}");
        }
    }

    // Valori originali delle impostazioni cambiate solo per questa sessione (profilo leggero, --madvr-try). madVR salva
    // subito ogni modifica, ma il ripristino in chiusura a volte non arrivava su disco (processo chiuso subito dopo) e
    // dopo un blocco o una chiusura forzata non avveniva affatto: restavano i valori temporanei (11/11). Il file rimette
    // gli originali all'avvio successivo, prima di tutto il resto.
    private static string RestoreFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinecore", "madvr-ripristino.txt");

    private void RememberOriginal(string key, string old)
    {
        if (_restoreSettings.Any(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) return;
        _restoreSettings.Add((key, old));
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RestoreFile)!);
            // Il primo originale vince: se il file c'e' gia' (ripristino non ancora applicato) non lo si sovrascrive.
            var lines = System.IO.File.Exists(RestoreFile) ? System.IO.File.ReadAllLines(RestoreFile).ToList() : new System.Collections.Generic.List<string>();
            if (!lines.Any(l => l.StartsWith(key + "\t", StringComparison.OrdinalIgnoreCase))) lines.Add(key + "\t" + old);
            System.IO.File.WriteAllLines(RestoreFile, lines);
        }
        catch (Exception ex) { Out.Line("info madVR: file di ripristino non scritto: " + ex.Message); }
    }

    private static void ApplyPendingRestore(IMadVRSettings s)
    {
        try
        {
            if (!System.IO.File.Exists(RestoreFile)) return;
            int n = 0;
            foreach (string line in System.IO.File.ReadAllLines(RestoreFile))
            {
                var kv = line.Split('\t', 2);
                if (kv.Length != 2) continue;
                SetTyped(s, kv[0], kv[1]); n++;
            }
            System.IO.File.Delete(RestoreFile);
            Out.Line($"info madVR: rimesse {n} impostazioni originali lasciate da una sessione precedente");
        }
        catch (Exception ex) { Out.Line("info madVR: ripristino in sospeso fallito: " + ex.Message); }
    }

    private static void SetTyped(IMadVRSettings s, string key, string value)
    {
        if (value is "true" or "false") s.SettingsSetBoolean(key, value == "true");
        else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) s.SettingsSetInteger(key, n);
        else s.SettingsSetString(key, value);
    }

    private void RestoreSettings()
    {
        if (_restoreSettings.Count == 0 || _videoRenderer is not IMadVRSettings s) return;
        foreach (var (key, old) in _restoreSettings) SetTyped(s, key, old);
        // Il file resta: se madVR non fa in tempo a salvare, l'avvio successivo li rimette comunque.
        Out.Line($"info madVR: ripristinate {_restoreSettings.Count} impostazioni originali");
        _restoreSettings.Clear();
    }

    private static int VideoWidth(IBaseFilter renderer)
    {
        var pin = DsFindPin.ByDirection(renderer, PinDirection.Input, 0);
        if (pin == null) return 0;
        var format = new AMMediaType();
        try
        {
            if (pin.ConnectionMediaType(format) < 0 || format.formatPtr == IntPtr.Zero) return 0;
            if (format.formatType == FormatType.VideoInfo2 && format.formatSize >= Marshal.SizeOf<VideoInfoHeader2>())
                return Marshal.PtrToStructure<VideoInfoHeader2>(format.formatPtr)!.BmiHeader.Width;
            if (format.formatType == FormatType.VideoInfo && format.formatSize >= Marshal.SizeOf<VideoInfoHeader>())
                return Marshal.PtrToStructure<VideoInfoHeader>(format.formatPtr)!.BmiHeader.Width;
            return 0;
        }
        finally { DsUtils.FreeAMMediaType(format); Marshal.ReleaseComObject(pin); }
    }

    private static void ApplyAudioDelay(ILavAudioSettings? settings, int? delayMs)
    {
        if (delayMs is not int delay)
        {
            Out.Line("info ritardo audio automatico: durata fotogramma sconosciuta");
            return;
        }
        int enabled = delay == 0 ? 0 : 1;
        if (settings == null || settings.SetAudioDelay(enabled, delay) < 0
            || settings.GetAudioDelay(out int readEnabled, out int readDelay) < 0
            || readEnabled != enabled || readDelay != delay)
        {
            Out.Line("info ritardo audio: LAV non ha confermato l'impostazione");
            return;
        }
        Out.Line($"info ritardo audio LAV: {delay} ms (configurazione temporanea)");
    }

    /// <summary>
    /// MPC Video Renderer usa gli shader D3D11 perché il driver non espone ID3D11VideoDevice.
    /// Mullion su ARM64 usa la modalità flip per ridimensionare il backbuffer durante il fullscreen.
    /// Il runtime Intel usa discard per evitare il video nero.
    /// </summary>
    private static void ConfigureMpcVr()
    {
        try
        {
            bool usesArm64Mullion = Environment.GetEnvironmentVariable("GRAPHICS_BACKEND") == "mullion"
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CINECORE_WINE_ARM64"));
            int defaultSwapEffect = usesArm64Mullion ? 1 : 0;
            int swapEffect = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_MPCVR_SWAP"), out int requested)
                ? requested : defaultSwapEffect;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\MPC-BE Filters\MPC Video Renderer");
            foreach (var (name, value) in new[] { ("UseD3D11", 1), ("VPEnableNV12", 0), ("VPEnableP01x", 0), ("VPEnableYUY2", 0), ("VPEnableOther", 0), ("SwapEffect", swapEffect), ("ShowStatistics", 0) })
                key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
            Out.Line("info MPC-VR: impostazioni D3D11 scritte");
        }
        catch (Exception ex) { Out.Line("info MPC-VR: impostazioni non scritte: " + ex.Message); }
    }

    private void ConfigureMadVr(IBaseFilter filter)
    {
        if (filter is not IMadVRSettings s) { Out.Line("info madVR senza IMadVRSettings"); return; }
        ApplyPendingRestore(s);
        // Overlay ed esclusivo sono funzioni di Windows: sotto Wine si lavora in finestra. Su Windows restano le scelte dell'utente.
        foreach (var (name, value) in NativeHost.IsNative ? Array.Empty<(string, bool)>() : new[] { ("useD3d11", _d3d11), ("enableOverlay", false), ("enableExclusive", false) })
        {
            bool ok = s.SettingsSetBoolean(name, value);
            Out.Line($"info madVR {name}={value} {(ok ? "ok" : "non accettato")}");
        }
        // Statistiche (Ctrl+J): madVR ricorda se erano accese (HKCU\Software\madshi\madVR, ShowOsd_), come su Windows.
        // Serve saperlo per nasconderle in pausa (con le statistiche accese il ponte in pausa consegna nero).
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\madshi\madVR");
            _madVrStats = key?.GetValue("ShowOsd_") is int shown && shown != 0;
            Out.Line($"info madVR statistiche all'avvio={_madVrStats}");
        }
        catch (Exception ex) { Out.Line("info madVR statistiche illeggibili: " + ex.Message); }
        foreach (var (name, value) in MadVrSettings) SetMadVr(s, name, value);
        foreach (string name in MadVrDump)
        {
            string now = s.SettingsGetBoolean(name, out bool bv) ? (bv ? "true" : "false")
                : s.SettingsGetInteger(name, out int iv) ? iv.ToString(CultureInfo.InvariantCulture) : ReadString(s, name) ?? "?";
            Out.Line($"info madVR valore {name}={now}");
        }
        foreach (var (name, value) in MadVrTry)
        {
            bool isBool = value is "true" or "false";
            string? old = isBool ? (s.SettingsGetBoolean(name, out bool b) ? (b ? "true" : "false") : null)
                : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? (s.SettingsGetInteger(name, out int oi) ? oi.ToString(CultureInfo.InvariantCulture) : null)
                : ReadString(s, name);
            if (old == null) { Out.Line($"info madVR prova {name}: valore attuale illeggibile, non cambiato"); continue; }
            RememberOriginal(name, old);
            SetMadVr(s, name, value);
        }
    }

    private static void SetMadVr(IMadVRSettings s, string name, string value)
    {
        // Ogni scrittura costa a madVR ~400 ms (salva le impostazioni): con una decina di voci l'avvio del film
        // aspettava ~4,5 s. Se il valore e' gia' quello giusto (quasi sempre, dalla seconda volta) non si scrive.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? before = Current(s, value);
        if (before != null && string.Equals(before, value, StringComparison.OrdinalIgnoreCase))
        {
            Out.Line($"info madVR {name}={value} gia' impostato ({clock.ElapsedMilliseconds} ms)");
            return;
        }
        bool ok = value is "true" or "false" ? s.SettingsSetBoolean(name, value == "true")
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? s.SettingsSetInteger(name, n)
            : s.SettingsSetString(name, value);
        string now = s.SettingsGetInteger(name, out int iv) ? iv.ToString(CultureInfo.InvariantCulture)
            : s.SettingsGetBoolean(name, out bool bv) ? bv.ToString() : ReadString(s, name) ?? "?";
        Out.Line($"info madVR {name}={value} {(ok ? "ok" : "non accettato")} (letto: {now}, {clock.ElapsedMilliseconds} ms)");

        // Il valore attuale nello stesso formato di quello voluto (booleano, numero o testo); null se illeggibile.
        string? Current(IMadVRSettings st, string wanted)
        {
            if (wanted is "true" or "false") return st.SettingsGetBoolean(name, out bool b) ? (b ? "true" : "false") : null;
            if (int.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return st.SettingsGetInteger(name, out int i) ? i.ToString(CultureInfo.InvariantCulture) : null;
            return ReadString(st, name);
        }
    }

    private static string? ReadString(IMadVRSettings s, string name)
    {
        var sb = new StringBuilder(256); int len = sb.Capacity;
        return s.SettingsGetString(name, sb, ref len) ? sb.ToString() : null;
    }

    /// <summary>Formato tra LAV Audio e il renderer: IEC 61937 / S/PDIF = bitstream (come TryDetectBitstream di Windows).</summary>
    private void ReportBitstream()
    {
        if (_lavAudio == null) return;
        bool bitstream = false;
        try
        {
            var output = DsFindPin.ByDirection(_lavAudio, PinDirection.Output, 0);
            if (output != null)
            {
                var mt = new AMMediaType();
                if (output.ConnectionMediaType(mt) == 0)
                {
                    uint tag = mt.subType.ToByteArray() is var g ? BitConverter.ToUInt32(g, 0) : 0;
                    bitstream = tag == 0x92 || tag == 0x2000 || tag == 0x2001;
                    if (!bitstream && mt.formatPtr != IntPtr.Zero && mt.formatSize >= 40 && (ushort)Marshal.ReadInt16(mt.formatPtr) == 0xFFFE)
                    {
                        var sub = new byte[16];
                        Marshal.Copy(mt.formatPtr + 24, sub, 0, 16);
                        var guid = new Guid(sub);
                        string s = guid.ToString();
                        bitstream = s.EndsWith("-0cea-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase)
                            || s.StartsWith("00000092-", StringComparison.Ordinal) || s.StartsWith("00000008-", StringComparison.Ordinal);
                    }
                    DsUtils.FreeAMMediaType(mt);
                }
                Marshal.ReleaseComObject(output);
            }
        }
        catch { }
        Out.Line("bitstream " + (bitstream ? "1" : "0"));
    }

    private void DescribeGraph()
    {
        ReportBitstream();
        if (_graph == null) return;
        _graph.EnumFilters(out IEnumFilters filters);
        var one = new IBaseFilter[1];
        var sb = new StringBuilder("info filtri:");
        while (filters.Next(1, one, IntPtr.Zero) == 0)
        {
            one[0].QueryFilterInfo(out FilterInfo fi);
            sb.Append(' ').Append('[').Append(fi.achName).Append(']');
            if (fi.pGraph != null) Marshal.ReleaseComObject(fi.pGraph);
        }
        Out.Line(sb.ToString());
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lp);
    [DllImport("user32")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    // Di serie arrotondata (finestra normale del player): prima restava squadrata finche' il film non era pronto.
    private bool _rounded = Environment.GetEnvironmentVariable("CINECORE_VIDEO_SQUARE") != "1";

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCorners();
    }
    private int _inset;

    /// <summary>
    /// Windows 11: la finestra del video ha la forma di quella del player (angoli di 8 px per la scala dello schermo in
    /// finestra, squadrati a schermo intero) e nessuna ombra o bordo suo: si vedono solo quelli del player.
    /// </summary>
    private void ApplyCorners()
    {
        try
        {
            // Stessa forma della finestra del player: angoli di Windows 11 arrotondati da DWM (antialiasing), senza
            // regione; squadrati a finestra ingrandita o a schermo intero.
            SetWindowRgn(Handle, IntPtr.Zero, true);
            int corner = _rounded ? 2 /* DWMWCP_ROUND */ : 1 /* DWMWCP_DONOTROUND */;
            DwmSetWindowAttribute(Handle, 33, ref corner, 4);
            int none = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(Handle, 34 /* DWMWA_BORDER_COLOR: nessuno */, ref none, 4);
        }
        catch { }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsHandleCreated) ApplyCorners();
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hwnd, string text);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private void DescribeChildren()
    {
        int n = 0;
        EnumChildWindows(Handle, (h, _) =>
        {
            GetWindowRect(h, out RECT r); var c = new StringBuilder(128); GetClassName(h, c, 128);
            Out.Line($"info child {h} cls={c} rect={r.L},{r.T} {r.R - r.L}x{r.B - r.T} visible={IsWindowVisible(h)}");
            n++; return true;
        }, IntPtr.Zero);
        Out.Line($"info children={n} form={Bounds}");
        try { _window!.GetWindowPosition(out int x, out int y, out int w, out int h); _window.get_Visible(out OABool v); Out.Line($"info ivideowindow {x},{y} {w}x{h} visible={v}"); } catch (Exception ex) { Out.Line("info ivideowindow " + ex.Message); }
    }

    // Come in Cinecore (RenderingEngine.TryUpdateIVideoWindow): MPC VR lascia la destinazione a 0x0 finche' non gliela
    // si da' con IBasicVideo, e ridisegna solo quando riceve WM_SIZE / WM_PAINT dal proprietario.
    // madVR in pausa, dopo un cambio di destinazione, presenta nero finche' non arriva un fotogramma nuovo: un salto
    // sullo stesso punto gli fa rielaborare quello fermo (un solo salto anche per piu' cambi ravvicinati).
    private System.Windows.Forms.Timer? _repaint;
    private void RepaintPausedSoon()
    {
        if (_seeking == null) return;
        if (_repaint == null)
        {
            _repaint = new System.Windows.Forms.Timer { Interval = 120 };
            _repaint.Tick += (_, _) =>
            {
                _repaint!.Stop();
                if (_playing || _seeking == null) return;
                try
                {
                    _seeking.GetCurrentPosition(out long now);
                    _seeking.SetPositions(now, AMSeekingSeekingFlags.AbsolutePositioning, null, AMSeekingSeekingFlags.NoPositioning);
                }
                catch { }
            };
        }
        _repaint.Stop(); _repaint.Start();
    }

    private bool _madVrStats, _statsHiddenForPause, _stretch;

    private void MadVrKey(string key)
    {
        if (_videoRenderer is not IMadVRCommand mc) return;
        int packed = 0;
        foreach (var part in key.ToLowerInvariant().Split('+'))
        {
            if (part == "ctrl") packed |= 1 << 16;
            else if (part == "shift") packed |= 1 << 8;
            else if (part == "alt") packed |= 1 << 24;
            else packed |= (int)Enum.Parse<Keys>(part, true) & 0xFF;
        }
        Out.Line($"info key {key} hr=0x{mc.SendCommandInt("keyPress", packed):X8}");
    }

    /// <summary>
    /// Punto dello schermo nel fotogramma del DVD: dentro la finestra video, poi dentro la destinazione dell'immagine
    /// (sotto la barra del titolo, bande nere escluse), poi in pixel nativi del video. Null fuori dall'immagine.
    /// </summary>
    private System.Drawing.Point? ScreenToDvdFrame(System.Drawing.Point screen)
    {
        try
        {
            if (_graph is not IBasicVideo basic) return null;
            System.Drawing.Point client = System.Drawing.Point.Empty;
            Invoke(() => client = PointToClient(screen));
            // Formato dell'immagine (gia' corretto per l'anamorfico) dentro la finestra video sotto la barra del titolo:
            // il rettangolo di destinazione dei renderer non e' affidabile (madVR lo riporta di serie).
            int hrV = basic.GetVideoSize(out int vw, out int vh);
            System.Drawing.Size area = System.Drawing.Size.Empty;
            Invoke(() => area = ClientSize);
            double aw = area.Width, ah = area.Height - _inset;
            if (hrV < 0 || vw <= 0 || vh <= 0 || aw <= 0 || ah <= 0) return null;
            double k = Math.Min(aw / vw, ah / vh) * _scale;
            double dw = vw * k, dh = vh * k, dx = (aw - dw) / 2, dy = (ah - dh) / 2;
            double x = client.X - dx, y = client.Y - _inset - dy;
            if (x < 0 || y < 0 || x >= dw || y >= dh) return null;
            // Coordinate nel formato di visualizzazione (es. 1024x576 per un 16:9 PAL): quelle che vuole il Navigator
            // (con 720x576 un clic su "Lingue" finiva su "Scene", il 57% diventava il 40%).
            return new System.Drawing.Point((int)(x * vw / dw), (int)(y * vh / dh));
        }
        catch { return null; }
    }

    private void FitVideo()
    {
        // Dimensionamento a schermo intero (scale). Sotto Wine conta solo la destinazione (la finestra del video
        // ristretta non cambia nulla): MPC-VR la riceve ridotta attorno al centro. madVR in questa catena presenta
        // sempre a tutta finestra (ne' destinazione ne' setZoomFactorX/Y cambiano l'immagine): per lui niente scala.
        // _inset: in alto la finestra video sta sotto la barra del titolo di Windows del player (stessa forma, angoli
        // arrotondati da DWM): l'immagine si disegna solo nella parte sotto.
        int fullW = Math.Max(2, ClientSize.Width), fullH = Math.Max(2, ClientSize.Height - _inset);
        bool madVr = _videoRenderer is IMadVRCommand;
        double scale = _scale;
        int w = Math.Max(2, (int)Math.Round(fullW * scale)), h = Math.Max(2, (int)Math.Round(fullH * scale));
        _videoOrigin = new Point((fullW - w) / 2, (fullH - h) / 2);
        try { _window?.SetWindowPosition(0, _inset, fullW, fullH); } catch { }
        if (_graph is IBasicVideo basic)
        {
            try
            {
                var dest = ApplyCrop(basic, w, h);
                dest.Offset(_videoOrigin);
                int hr = basic.SetDestinationPosition(dest.X, dest.Y, dest.Width, dest.Height);
                Out.Line($"info destinazione {dest} hr=0x{hr:X8}");
                // In pausa madVR non ridisegna da solo dopo un cambio di sorgente o destinazione.
                if (madVr && _videoRenderer is IMadVRCommand redraw) redraw.SendCommand("redraw");
                if (madVr && !_playing) RepaintPausedSoon();
            }
            catch (Exception ex) { Out.Line("info IBasicVideo " + ex.Message); }
        }
        if (_window != null)
        {
            const int WM_SIZE = 0x0005, WM_PAINT = 0x000F;
            try { _window.NotifyOwnerMessage(Handle, WM_SIZE, IntPtr.Zero, (IntPtr)((fullW & 0xFFFF) | ((fullH & 0xFFFF) << 16))); } catch { }
            try { _window.NotifyOwnerMessage(Handle, WM_PAINT, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
    }

    // Ritaglio dal menu Video (crop <formato>, 0 = nessuno): come ApplyBasicVideoCrop di Windows, la sorgente si
    // restringe al centro nel formato scelto e la destinazione prende le sue proporzioni.
    private double _cropAspect;
    private double _scale = 1;
    private Point _videoOrigin;

    private Rectangle ApplyCrop(IBasicVideo basic, int w, int h)
    {
        try
        {
            basic.GetVideoSize(out int vw, out int vh);
            if (_stereo == 0 && _cropAspect <= 0)
            {
                basic.SetDefaultSourcePosition();
                // Schermo intero esteso 3D: il fotogramma doppio si stira su tutti gli schermi (un occhio per schermo).
                return _stretch ? new Rectangle(0, 0, w, h) : Letterbox(basic, w, h);
            }
            // Formato a schermo del fotogramma (pixel non quadrati nei DVD e nei 3D a meta' risoluzione).
            double frame = vh > 0 ? vw / (double)vh : 0;
            try { if (_graph is IBasicVideo2 b2 && b2.GetPreferredAspectRatio(out int ax, out int ay) == 0 && ax > 0 && ay > 0) frame = ax / (double)ay; } catch { }
            // madVR dichiara la misura gia' corretta per il formato (3840x1080 per un SBS 1920x1080 in 32:9), ma la
            // sorgente la vuole in pixel veri del fotogramma: si usa la misura del collegamento.
            if (_videoRenderer != null && ConnectionType(_videoRenderer, PinDirection.Input) is { Width: > 0, Height: > 0 } native)
            { vw = native.Width; vh = native.Height; }
            if (_stereo != 0 && vw > 0 && vh > 0) return ApplyStereo(basic, vw, vh, frame, w, h);
            if (_cropAspect <= 0 || vw <= 0 || vh <= 0)
            {
                basic.SetDefaultSourcePosition();
                return Letterbox(basic, w, h);
            }
            double keepW = _cropAspect > frame ? 1 : _cropAspect / frame, keepH = _cropAspect > frame ? frame / _cropAspect : 1;
            int cw = Math.Max(2, (int)Math.Round(vw * keepW) & ~1), ch = Math.Max(2, (int)Math.Round(vh * keepH) & ~1);
            // madVR accetta la sorgente ridotta (hr 0) ma la ignora: con lui si usa subito l'ingrandimento.
            int hr = _videoRenderer is IMadVRCommand ? unchecked((int)0x80004001) : basic.SetSourcePosition((vw - cw) / 2 & ~1, (vh - ch) / 2 & ~1, cw, ch);
            double shown = _cropAspect;
            double s = Math.Min(w / shown, (double)h);
            int dw = (int)Math.Round(s * shown), dh = (int)Math.Round(s);
            dw = Math.Min(w, dw); dh = Math.Min(h, dh);
            Out.Line($"info ritaglio {_cropAspect:0.###} sorgente {cw}x{ch} hr=0x{hr:X8}");
            if (hr >= 0) return new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh);
            // MPC Video Renderer non accetta una sorgente ridotta: il fotogramma intero si ingrandisce oltre la finestra,
            // centrato, e ne resta visibile solo la parte scelta.
            // La finestra video si restringe al formato scelto (il resto e' il nero dell'app) e dentro il fotogramma intero
            // si ingrandisce, centrato: ne resta visibile solo la parte scelta, con le bande giuste ai lati.
            basic.SetDefaultSourcePosition();
            int fw = (int)Math.Round(dw / keepW), fh = (int)Math.Round(dh / keepH);
            var visible = new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh);
            try { _window?.SetWindowPosition(_videoOrigin.X + visible.X, _inset + _videoOrigin.Y + visible.Y, visible.Width, visible.Height); } catch { }
            _videoOrigin = Point.Empty;
            return new Rectangle((dw - fw) / 2, (dh - fh) / 2, fw, fh);
        }
        catch (Exception ex) { Out.Line("info ritaglio " + ex.Message); basic.SetDefaultSourcePosition(); return Letterbox(basic, w, h); }
    }

    // 3D → 2D (stereo sbs|tab|none): la sorgente e' l'occhio sinistro (o quello in alto), mostrato con le proporzioni
    // dell'occhio; dove la sorgente ridotta non e' accettata (MPC-VR) si ingrandisce il fotogramma come per il ritaglio.
    private int _stereo; // 0 nessuno, 1 SBS, 2 TAB

    private Rectangle ApplyStereo(IBasicVideo basic, int vw, int vh, double frame, int w, int h)
    {
        if (frame <= 0) frame = vw / (double)vh;
        bool sbs = _stereo == 1;
        int sw = sbs ? vw / 2 & ~1 : vw, sh = sbs ? vh : vh / 2 & ~1;
        double eye = sbs ? (frame >= 2.6 ? frame / 2 : frame) : (frame <= 1.2 ? frame * 2 : frame);
        double s = Math.Min(w / eye, (double)h);
        int dw = Math.Min(w, (int)Math.Round(s * eye)), dh = Math.Min(h, (int)Math.Round(s));
        int hr = _videoRenderer is IMadVRCommand ? unchecked((int)0x80004001) : basic.SetSourcePosition(0, 0, sw, sh);
        Out.Line($"info 3D {(sbs ? "SBS" : "TAB")} sorgente {sw}x{sh} occhio {eye:0.###} hr=0x{hr:X8}");
        if (hr >= 0) return new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh);
        basic.SetDefaultSourcePosition();
        var visible = new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh);
        try { _window?.SetWindowPosition(_videoOrigin.X + visible.X, _inset + _videoOrigin.Y + visible.Y, visible.Width, visible.Height); } catch { }
        _videoOrigin = Point.Empty;
        // Il fotogramma intero, con l'occhio scelto che riempie la zona visibile.
        return new Rectangle(0, 0, sbs ? dw * 2 : dw, sbs ? dh : dh * 2);
    }

    private Rectangle Letterbox(IBasicVideo basic, int w, int h)
    {
        try
        {
            basic.GetVideoSize(out int vw, out int vh);
            if (vw > 0 && vh > 0)
            {
                // Pixel non quadrati (DVD 720x576 in 16:9, file anamorfici): MPC-VR da' la misura in pixel, le
                // proporzioni vere sono quelle dichiarate. madVR la da' gia' corretta (e chiederglielo presto lo blocca).
                double aspect = vw / (double)vh;
                if (_videoRenderer is not IMadVRCommand && _videoRenderer != null && PictureAspect(_videoRenderer) is double declared) aspect = declared;
                double s = Math.Min(w / aspect, (double)h);
                int dw = (int)Math.Round(aspect * s), dh = (int)Math.Round(s);
                return new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh);
            }
        }
        catch { }
        return new Rectangle(0, 0, w, h);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // I messaggi del proprietario che MPC VR vuole conoscere (ridisegno, spostamenti, cambio di schermo).
        if (_window != null && m.Msg is 0x0005 or 0x000F or 0x0003 or 0x007E or 0x0047)
            try { _window.NotifyOwnerMessage(m.HWnd, m.Msg, m.WParam, m.LParam); } catch { }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); FitVideo(); }

    private void ReportPosition()
    {
        if (_seeking == null) return;
        try
        {
            if (_videoToolbox?.Invoke() < 0)
            {
                Out.Line("error collegamento VideoToolbox interrotto");
                Close();
                return;
            }
            bool ended = false;
            if (_graph is IMediaEvent events)
                while (events.GetEvent(out EventCode code, out IntPtr p1, out IntPtr p2, 0) == 0)
                {
                    try
                    {
                        _dvd?.HandleEvent(code, p1, p2);
                        if (code == EventCode.Complete && _playing && _dvd == null) { _playing = false; ended = true; }
                    }
                    finally { events.FreeEventParams(code, p1, p2); }
                }
            long cur, dur;
            if (_dvd != null)
            {
                _dvd.ReportState();
                cur = (long)(_dvd.Position(_playing) * 1e7); dur = (long)(_dvd.Duration() * 1e7);
            }
            else { _seeking.GetCurrentPosition(out cur); _seeking.GetDuration(out dur); }
            Out.Line(string.Create(CultureInfo.InvariantCulture, $"pos {cur / 1e7:0.000} {dur / 1e7:0.000} {(_playing ? (_cueing ? "playing cue" : "playing") : "paused")}"));
            if (ended) Out.Line("ended");
        }
        catch { }
    }

    private void ReadCommands()
    {
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            string cmd = line.Trim();
            if (cmd.Length == 0) continue;
            try { BeginInvoke(new Action(() => Run(cmd))); } catch { return; }
        }
        Program.ExitAfter(3000);
        try { BeginInvoke(new Action(Close)); } catch { }
    }

    private void Run(string cmd)
    {
        string[] p = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (p[0])
            {
                case "play":
                    _cueGeneration++; _cueing = false;
                    _control?.Run(); _playing = true;
                    if (_statsHiddenForPause) { _statsHiddenForPause = false; MadVrKey("ctrl+j"); }
                    break;
                case "pause":
                    // Con le statistiche accese madVR in pausa ridisegna di continuo e il ponte consegna nero: si nascondono
                    // finche' dura la pausa e tornano alla ripresa.
                    if (_madVrStats && _videoRenderer is IMadVRCommand) { MadVrKey("ctrl+j"); _statsHiddenForPause = true; }
                    _cueGeneration++; _cueing = false;
                    _control?.Pause(); _playing = false;
                    break;
                case "dvd":
                    _dvd?.Command(p);
                    break;
                case "inset":
                    _inset = Math.Max(0, int.Parse(p[1], CultureInfo.InvariantCulture));
                    FitVideo();
                    break;
                case "corners":
                    // Windows 11: angoli come la finestra del player (arrotondati in finestra, squadrati a schermo intero).
                    _rounded = p.Length > 1 && p[1] == "1";
                    ApplyCorners();
                    break;
                case "seek" when _dvd != null:
                    _dvd.Seek(double.Parse(p[1], CultureInfo.InvariantCulture));
                    break;
                case "seek":
                    long t = (long)(double.Parse(p[1], CultureInfo.InvariantCulture) * 1e7);
                    // In riproduzione: salto in pausa e ripartenza quando il renderer ha il primo fotogramma (con i flussi
                    // di rete madVR perdeva ~60 fotogrammi nei 4 s dopo un salto, i dati arrivavano a orologio gia' ripartito).
                    bool cue = _playing && _control != null && _renderer is ("madvr" or "mpcvr") && Environment.GetEnvironmentVariable("CINECORE_SEEK_CUE") != "0";
                    if (cue) _control!.Pause();
                    _seeking?.SetPositions(t, AMSeekingSeekingFlags.AbsolutePositioning, null, AMSeekingSeekingFlags.NoPositioning);
                    if (cue) StartCue();
                    break;
                case "rect":
                    var rect = TrimFullWidth(new Rectangle(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4])));
                    if (p.Length > 5 && p[5] == "show")
                    {
                        // Dopo un cambio di Space riporta il video dietro l'HUD senza attivare il processo Wine.
                        const uint showWithoutActivation = 0x0040 | 0x0010;
                        if (SetWindowPos(Handle, IntPtr.Zero /* HWND_TOP */, rect.X, rect.Y, rect.Width, rect.Height, showWithoutActivation)) break;
                    }
                    Bounds = rect;
                    break;
                case "stack":
                    if (p.Length >= 2 && int.TryParse(p[1], out int owner) && owner > 0)
                    {
                        string mode = p.Length == 3 && p[2] is "fullscreen" or "transition" ? $":{p[2]}" : "";
                        SetWindowText(Handle, $"Cinecore Video owner:{owner}{mode}");
                    }
                    break;
                case "volume":
                    int v = Math.Clamp(int.Parse(p[1]), 0, 100);
                    _audio?.put_Volume(v == 0 ? -10000 : (int)(2000 * Math.Log10(v / 100.0)));
                    break;
                case "audio-offset":
                    int offset = Math.Clamp(int.Parse(p[1], CultureInfo.InvariantCulture), -10000, 10000);
                    ApplyAudioDelay(_lavAudio as ILavAudioSettings, _automaticAudioDelayMs + offset);
                    break;
                case "subtitle":
                    if (_xySub is not IDirectVobSub external) { Out.Line("error sottotitoli: XySubFilter non disponibile"); break; }
                    DsError.ThrowExceptionForHR(external.put_FileName(cmd[9..]));
                    DsError.ThrowExceptionForHR(external.put_SelectedLanguage(0));
                    DsError.ThrowExceptionForHR(external.put_HideSubtitles(false));
                    _externalSubLoaded = true; _subsHidden = false;
                    Out.Line("info sottotitoli esterni caricati");
                    break;
                case "key":
                    // key ctrl+j | key shift+alt+f1 ... : tasto inoltrato a madVR come fa il player (IMadVRCommand keyPress).
                    // MPC Video Renderer: Ctrl+J accende e spegne le sue statistiche (statsEnable, come il player Windows).
                    if (_videoRenderer is not IMadVRCommand && _videoRenderer is IExFilterConfig mpcvr)
                    {
                        if (p[1].Equals("ctrl+j", StringComparison.OrdinalIgnoreCase))
                        {
                            bool shown = mpcvr.Flt_GetBool("statsEnable", out bool now) == 0 && now;
                            Out.Line($"info stats mpcvr {(!shown ? "on" : "off")} hr=0x{mpcvr.Flt_SetBool("statsEnable", !shown):X8}");
                        }
                        else Out.Line("info key " + p[1] + ": MPC Video Renderer non ha questo tasto");
                        break;
                    }
                    if (_videoRenderer is not IMadVRCommand) { Out.Line("error key: renderer senza IMadVRCommand"); break; }
                    if (p[1].Equals("ctrl+j", StringComparison.OrdinalIgnoreCase))
                    {
                        // Statistiche chieste in pausa: si mostrano subito solo se non le avevamo nascoste noi.
                        if (_statsHiddenForPause) { _statsHiddenForPause = false; _madVrStats = false; Out.Line("info stats madvr off"); break; }
                        // In pausa madVR con le statistiche consegna nero (vedi "pause"): si accendono alla ripresa.
                        if (!_playing && !_madVrStats) { _madVrStats = true; _statsHiddenForPause = true; Out.Line("info stats madvr alla ripresa"); break; }
                        _madVrStats = !_madVrStats;
                    }
                    MadVrKey(p[1]);
                    break;
                case "stretch":
                    _stretch = p.Length > 1 && p[1] == "1";
                    FitVideo();
                    break;
                case "stereo":
                    _stereo = p[1] switch { "sbs" => 1, "tab" => 2, _ => 0 };
                    FitVideo();
                    break;
                case "scale":
                    _scale = Math.Clamp(double.Parse(p[1], CultureInfo.InvariantCulture), 0.3, 1);
                    FitVideo();
                    break;
                case "crop":
                    _cropAspect = Math.Max(0, double.Parse(p[1], CultureInfo.InvariantCulture));
                    FitVideo();
                    // In pausa madVR non ridisegna da solo: lo stesso punto riletto rifa' il fotogramma fermo.
                    if (!_playing && _seeking != null && _seeking.GetCurrentPosition(out long here) == 0)
                        _seeking.SetPositions(here, AMSeekingSeekingFlags.AbsolutePositioning, null, AMSeekingSeekingFlags.NoPositioning);
                    break;
                case "tracks": ReportTracks(); break;
                case "subsoff":
                    // DisableSubtitlesIfPossible: XySubFilter nasconde e LAV passa alla sua voce "No subtitles".
                    if (_xySub is IDirectVobSub hide) hide.put_HideSubtitles(true);
                    // Con una traccia vera attiva basta nasconderla: il cambio di flusso in LAV rilegge il file e
                    // ferma l'immagine (~2 s in streaming). La traccia resta pronta per la riaccensione.
                    if (_xySub is IDirectVobSub && EnabledSubtitleStream() is { } live && !IsSubtitleOffOption(live.Name))
                    {
                        _subsHidden = true;
                        ReportTracks();
                        break;
                    }
                    if (_source is IAMStreamSelect offSel && offSel.Count(out int n) == 0)
                        for (int i = 0; i < n; i++)
                        {
                            if (offSel.Info(i, out AMMediaType mt, out _, out _, out _, out string name, out object o1, out object o2) != 0) continue;
                            if (mt != null) DsUtils.FreeAMMediaType(mt);
                            if (o1 != null) try { Marshal.ReleaseComObject(o1); } catch { }
                            if (o2 != null) try { Marshal.ReleaseComObject(o2); } catch { }
                            if (IsSubtitleOffOption(name)) { offSel.Enable(i, AMStreamSelectEnableFlags.Enable); break; }
                        }
                    ReportTracks();
                    break;
                case "select":
                    // Scelta di un flusso dello splitter (traccia audio): LAV ricollega da solo il decoder.
                    bool isSub = p.Length > 2 && p[2] == "sub";
                    if (isSub && EnabledSubtitleStream() is { } cur && cur.Index == int.Parse(p[1]))
                        Out.Line($"info select {p[1]} gia' attiva: solo riaccesa");
                    else if (_source is IAMStreamSelect sel)
                        Out.Line($"info select {p[1]} hr=0x{sel.Enable(int.Parse(p[1]), AMStreamSelectEnableFlags.Enable):X8}");
                    if (isSub) _subsHidden = false;
                    // Una traccia di sottotitoli scelta torna visibile (XySubtitleBridge.Select).
                    // put_FileName ricarica tutti i sottotitoli (lo scatto all'accensione): serve solo per togliere un
                    // file esterno caricato prima, come XySubtitleBridge.Select su Windows negli altri casi.
                    if (p.Length > 2 && p[2] == "sub" && _xySub is IDirectVobSub show)
                    {
                        if (_externalSubLoaded) { show.put_FileName(_file); _externalSubLoaded = false; }
                        show.put_HideSubtitles(false);
                    }
                    ReportTracks();
                    break;
                case "snap": D3D9SharedBridge.SnapPath = cmd.Substring(5).Trim(); break;
                case "madvr":
                {
                    // Scelta di madVR dalle Impostazioni, applicata al video in riproduzione: "madvr nome=valore".
                    var kv = cmd.Substring(6).Trim().Split('=', 2);
                    if (kv.Length == 2 && _videoRenderer is IMadVRSettings ms) SetMadVr(ms, kv[0], kv[1]);
                    break;
                }
                case "quit":
                    // Nascoste solo per la pausa: madVR deve ricordarle accese per il prossimo film.
                    if (_statsHiddenForPause) { _statsHiddenForPause = false; MadVrKey("ctrl+j"); }
                    Program.ExitAfter(3000); Close(); break;
                default: Out.Line("error comando sconosciuto: " + cmd); break;
            }
        }
        catch (Exception ex) { Out.Line("error " + cmd + ": " + ex.GetBaseException().Message); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _status.Stop();
        try { _control?.Stop(); } catch { }
        try { RestoreSettings(); } catch { }
        try { _window?.put_Visible(OABool.False); _window?.put_Owner(IntPtr.Zero); } catch { }
        if (_graph != null) try { Marshal.ReleaseComObject(_graph); } catch { }
        base.OnFormClosing(e);
    }
}

// madVR SDK: i metodi restituiscono BOOL di Win32.
[ComImport, Guid("6F8A566C-4E19-439E-8F07-20E46ED06DEE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMadVRSettings
{
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetRevision(out long revision);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsExport(out IntPtr buffer, out int size);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsImport(IntPtr buffer, int size);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetString([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetInteger([MarshalAs(UnmanagedType.LPWStr)] string path, int value);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetBoolean([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] bool value);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetString([MarshalAs(UnmanagedType.LPWStr)] string path, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, ref int length);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetInteger([MarshalAs(UnmanagedType.LPWStr)] string path, out int value);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetBoolean([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] out bool value);
}

[ComImport, Guid("5E9599D1-C5DB-4A84-98A9-09BC5F8F1B79"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMadVRCommand
{
    [PreserveSig] int SendCommand([MarshalAs(UnmanagedType.LPStr)] string command);
    [PreserveSig] int SendCommandBool([MarshalAs(UnmanagedType.LPStr)] string command, [MarshalAs(UnmanagedType.I1)] bool parameter);
    [PreserveSig] int SendCommandInt([MarshalAs(UnmanagedType.LPStr)] string command, int parameter);
    // Il resto della tabella di IMadVRCommand (mvrInterfaces.h), nell'ordine: serve SendCommandDouble.
    [PreserveSig] int SendCommandSize([MarshalAs(UnmanagedType.LPStr)] string command, long parameter);
    [PreserveSig] int SendCommandRect([MarshalAs(UnmanagedType.LPStr)] string command, IntPtr parameter);
    [PreserveSig] int SendCommandULONGLONG([MarshalAs(UnmanagedType.LPStr)] string command, ulong parameter);
    [PreserveSig] int SendCommandDouble([MarshalAs(UnmanagedType.LPStr)] string command, double parameter);
}

// MPC Video Renderer: opzioni del filtro vivo per nome (IExFilterConfig, come RenderingEngine di Windows).
[ComImport, Guid("37CBDF10-D65E-4E5A-8F37-40E0C8EA1695"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExFilterConfig
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

// LAV Video: decoder in uso (LAVVideoSettings.h).
[ComImport, Guid("1CC2385F-36FA-41B1-9942-5024CE0235DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ILAVVideoStatus
{
    [PreserveSig] IntPtr GetActiveDecoderName();
}

// Prime voci di IDirectVobSub (VSFilter / XySubFilter), nell'ordine dell'interfaccia nativa (Engines/XySubtitleBridge.cs).
[ComImport, Guid("EBE1FB08-3957-47ca-AF13-5827E5442E56"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectVobSub
{
    [PreserveSig] int get_FileName(IntPtr buffer);
    [PreserveSig] int put_FileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    [PreserveSig] int get_LanguageCount(out int count);
    [PreserveSig] int get_LanguageName(int index, out IntPtr name);
    [PreserveSig] int get_SelectedLanguage(out int index);
    [PreserveSig] int put_SelectedLanguage(int index);
    [PreserveSig] int get_HideSubtitles([MarshalAs(UnmanagedType.U1)] out bool hidden);
    [PreserveSig] int put_HideSubtitles([MarshalAs(UnmanagedType.U1)] bool hidden);
}

/// <summary>
/// VideoHost avviato su Windows vero (non sotto Wine): niente ganci per Wine, filtri dai file dei componenti.
/// </summary>
internal static class NativeHost
{
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    public static readonly bool IsNative = Environment.GetEnvironmentVariable("CINECORE_FORCE_WINE_PATH") != "1" && !IsWine();

    private static bool IsWine()
    {
        try { IntPtr nt = GetModuleHandleW("ntdll.dll"); return nt != IntPtr.Zero && GetProcAddress(nt, "wine_get_version") != IntPtr.Zero; }
        catch { return false; }
    }

    private static readonly Guid MadVR = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");
    private static readonly Guid MpcVR = new("71F080AA-8661-4093-B15E-4F6903E77D0A");
    private static readonly Guid XySub = new("2DFCB782-EC20-4A7C-B530-4577ADB33F21");

    /// <summary>File del filtro passato dall'app (CINECORE_MADVR_AX, CINECORE_MPCVR_AX, CINECORE_XYSUB_DLL).</summary>
    public static string? BundledPath(Guid clsid)
    {
        if (!IsNative) return null;
        string? env = clsid == MadVR ? "CINECORE_MADVR_AX" : clsid == MpcVR ? "CINECORE_MPCVR_AX" : clsid == XySub ? "CINECORE_XYSUB_DLL" : null;
        string? path = env == null ? null : Environment.GetEnvironmentVariable(env);
        return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? path : null;
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance([MarshalAs(UnmanagedType.IUnknown)] object? outer, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int LockServer(bool lockServer);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObject(ref Guid clsid, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    public static IBaseFilter CreateFromFile(string path, Guid clsid)
    {
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x8 /* LOAD_WITH_ALTERED_SEARCH_PATH */);
        if (module == IntPtr.Zero) throw new InvalidOperationException("LoadLibrary " + Marshal.GetLastWin32Error());
        IntPtr proc = GetProcAddress(module, "DllGetClassObject");
        if (proc == IntPtr.Zero) throw new InvalidOperationException("DllGetClassObject assente");
        var get = Marshal.GetDelegateForFunctionPointer<DllGetClassObject>(proc);
        Guid factoryId = typeof(IClassFactory).GUID;
        int hr = get(ref clsid, ref factoryId, out object factoryObject);
        if (hr < 0) throw new InvalidOperationException($"DllGetClassObject 0x{hr:X8}");
        var factory = (IClassFactory)factoryObject;
        Guid filterId = typeof(IBaseFilter).GUID;
        hr = factory.CreateInstance(null, ref filterId, out object filter);
        if (hr < 0) throw new InvalidOperationException($"CreateInstance 0x{hr:X8}");
        Out.Line("info filtro da file: " + System.IO.Path.GetFileName(path));
        return (IBaseFilter)filter;
    }
}
