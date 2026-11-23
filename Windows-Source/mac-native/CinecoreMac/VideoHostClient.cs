using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace CinecoreMac;

/// <summary>
/// Il processo video: VideoHost.exe sotto Wine (madVR o MPC Video Renderer con LAV). Comandi su stdin
/// (play, pause, seek, rect, volume, key, quit), stato su stdout (ready, pos, error). Stesso ambiente di
/// mac/cinecore-env.sh.
/// </summary>
public sealed class VideoHostClient : IDisposable
{
    private Process? _proc;
    private Channel<string>? _commands;
    public long SessionId { get; private set; }
    public int ProcessId => _proc is { HasExited: false } p ? p.Id : 0;
    public Task Stopped { get; private set; } = Task.CompletedTask;
    public event Action<double, double, bool>? Position;   // posizione, durata, in riproduzione
    public event Action<string>? Ready;
    /// <summary>Il renderer DirectShow ha disegnato il primo fotogramma, dopo la costruzione del grafo.</summary>
    public event Action? FirstFrame;
    public event Action<string>? Error;
    /// <summary>Riga "media" del VideoHost (decoder, formati negoziati, versione del renderer).</summary>
    public event Action<string>? Media;
    /// <summary>Fotogrammi persi dal renderer (dalle statistiche di ogni secondo).</summary>
    public event Action<int>? Dropped;
    public event Action? Exited;
    public event Action? Ended;
    /// <summary>Ogni riga del VideoHost (tranne le posizioni): fasi della schermata di caricamento.</summary>
    public event Action<string>? Progress;
    /// <summary>Flussi dello splitter: (indice, gruppo LAV, scelto, nome). Gruppo 1 = audio, 2 = sottotitoli.</summary>
    public event Action<IReadOnlyList<(int Index, int Group, bool Selected, string Name)>>? Tracks;
    public bool IsRunning => _mpv != null ? _mpv.IsRunning : _proc is { HasExited: false };
    // Renderer "mpv": libmpv nel processo al posto del VideoHost sotto Wine, stesso protocollo a righe.
    private MpvEngine? _mpv;
    /// <summary>Film senza audio (analisi HDR/SDR aperta): vale dal prossimo avvio del motore.</summary>
    public static bool Silent;
    // Bitstream con madVR/MPC-VR: l'audio lo suona una libmpv senza video che segue l'orologio del VideoHost.
    private MpvAudioSidecar? _sidecar;
    private Task<MacHdmiOverride?>? _hdmiOverride;
    private string? _hdmiOverrideUid;
    private Task _hdmiRestore = Task.CompletedTask;
    public bool ChangingHdmiOutput => _hdmiOverride is { IsCompleted: false } || !_hdmiRestore.IsCompleted;
    private List<(int Index, int Group, bool Selected, string Name)> _lastTracks = new();
    public bool NativeAudio => _sidecar != null;
    public bool BitstreamActive => _hostBitstream || (_mpv?.OutputFormat ?? _sidecar?.OutputFormat)?.StartsWith("spdif-", StringComparison.Ordinal) == true;
    // Windows: il VideoHost dice se LAV Audio manda il bitstream al renderer ("bitstream 1").
    private bool _hostBitstream;
#if CINECORE_WINDOWS
    /// <summary>DVD letto dal DVD Navigator del VideoHost (Windows): stato e comandi del disco.</summary>
    internal WinDvd? WinDvd { get; private set; }
#endif
    // Per accendere l'audio nativo a film avviato (cambio d'uscita senza riaprire): sorgente, ultimo stato noto.
    private string? _liveAudioSource;
    private bool _hostMuted, _lastPlaying;
    private double _lastPos;
    private int _lastVolume = 100, _lastOffsetMs, _lastAutoDelayMs;
    /// <summary>Frequenza dello schermo su cui sta il film, impostata dalla finestra prima di ogni avvio.</summary>
    public static double DisplayHz;
    public string NativeAudioState => _sidecar is { } a ? $"scarto={a.LastDiffMs:0}ms salti={a.Seeks}" : "spento";

    public static string RepoRoot
    {
        get
        {
            // L'app gira da mac-native/CinecoreMac/bin/... oppure da dentro il bundle: si risale fino a trovare dist/.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (; dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "dist", "mac-native", "videohost-current", "VideoHost.exe"))
                    || Directory.Exists(Path.Combine(dir.FullName, "dist", "mac", "runtime-wine11")))
                    return dir.FullName;
            return Environment.GetEnvironmentVariable("CINECORE_ROOT") ?? AppContext.BaseDirectory;
        }
    }

    private static Process? _wineServer;
    private static string? _wineServerPath, _wineServerPrefix;

    /// <summary>
    /// Wine con il VideoHost (madVR, MPC-VR) c'e' solo accanto al progetto (dist/mac/runtime-wine11): l'app
    /// autonoma senza Wine usa mpv e non prova ad avviarlo.
    /// </summary>
    // Ricordato per 5 s: lo stato del telecomando lo chiede ogni 500 ms dal thread dell'interfaccia e ogni volta
    // ricostruiva i parametri d'avvio cercando le cartelle sul disco (~4 ms; di piu' su un disco lento).
    public static bool WineAvailable
    {
        get
        {
            long now = Environment.TickCount64;
            if (now - _wineCheckedAt > 5000) { _wineAvailable = File.Exists(WineStartInfo(createCache: false).FileName); _wineCheckedAt = now; }
            return _wineAvailable;
        }
    }
    private static bool _wineAvailable;
    private static long _wineCheckedAt = long.MinValue / 2;

    /// <summary>
    /// Server di Wine tenuto acceso mentre l'app e' aperta (wineserver -p). Senza, si spegneva alla fine di ogni film e
    /// ogni avvio lo rifaceva: 12 secondi solo per Wine (misurato: "wine cmd /c exit" 12 s, con il server acceso 0,44 s).
    /// Un primo processo vuoto scalda anche il resto (traduzione Rosetta, prefisso) mentre si sceglie il film.
    /// </summary>
    public static void WarmUpWine()
    {
#if CINECORE_WINDOWS
        return; // Su Windows il VideoHost parte da solo, senza server da tenere acceso.
#else
        if (_wineServer is { HasExited: false } || !WineAvailable) return;
        DisableWineAppNap();
        try
        {
            var server = WineStartInfo();
            _wineServerPrefix = server.Environment["WINEPREFIX"];
            server.FileName = _wineServerPath = Path.Combine(Path.GetDirectoryName(server.FileName)!, "wineserver");
            server.ArgumentList.Add("-p");
            server.RedirectStandardOutput = server.RedirectStandardError = false;
            _wineServer = Process.Start(server);
            var warm = WineStartInfo();
            warm.RedirectStandardOutput = warm.RedirectStandardError = false;
            foreach (string a in new[] { "cmd", "/c", "exit" }) warm.ArgumentList.Add(a);
            Process.Start(warm)?.Dispose();
        }
        catch (Exception ex) { Console.Error.WriteLine("wine: " + ex.Message); }
#endif
    }

    /// <summary>Alla chiusura dell'app il server di Wine si spegne (con -p resterebbe acceso per sempre).</summary>
    /// <summary>L'app si sta chiudendo (anche per un segnale): niente riavvii automatici del VideoHost.</summary>
    public static volatile bool ShuttingDown;

    public static void StopWine()
    {
        DiscardStandby();
        // wineserver si stacca subito dal processo che lo avvia: Kill non lo raggiungeva e restava acceso per
        // sempre (con winedevice.exe). "wineserver -k" spegne quello del prefisso e i suoi processi.
        if (_wineServer == null) return;
        try
        {
            var stop = WineStartInfo(createCache: false);
            // Lo stesso runtime e prefisso dell'avvio, anche se intanto è cambiata la scelta FEX/Rosetta.
            stop.FileName = _wineServerPath ?? Path.Combine(Path.GetDirectoryName(stop.FileName)!, "wineserver");
            if (_wineServerPrefix != null) stop.Environment["WINEPREFIX"] = _wineServerPrefix;
            stop.ArgumentList.Add("-k");
            stop.RedirectStandardOutput = stop.RedirectStandardError = false;
            using var p = Process.Start(stop);
            p?.WaitForExit(3000);
        }
        catch { }
        _wineServer = null;
    }

    /// <summary>
    /// madVR avvia da solo madHcCtrl.exe (icona e pannello, 32 bit). Sotto FEX/WoW64 gira a vuoto in
    /// RtlVirtualUnwind: un core intero per tutta la sessione e oltre. Il rendering non lo usa.
    /// </summary>
    private static void DisableMadVrTray(string prefix)
    {
        try
        {
            string exe = Path.Combine(prefix, "drive_c", "Cinecore", "madVR", "madHcCtrl.exe");
            if (File.Exists(exe)) File.Move(exe, exe + ".off", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Wine ARM64 con FEX (MetalSharp Wine 6.0 + ponte IOSurface) se presente; altrimenti null e si usa il runtime
    /// x86-64 sotto Rosetta, rimasto solo come riserva (la scelta nelle Impostazioni e' stata tolta il 12/11/2026).
    /// CINECORE_WINE_ARM64 lo impone (mac/run-madvr-arm64.sh).
    /// </summary>
    public static string? Arm64Bundle
    {
        get
        {
#if CINECORE_WINDOWS
            return null;
#else
            if (Environment.GetEnvironmentVariable("CINECORE_WINE_ARM64") is { Length: > 0 } forced) return forced;
            // Il motore scaricato (MacEngine) o, in sviluppo, la copia del progetto.
            string bundle = MacEngine.WineApp;
            return File.Exists(Path.Combine(bundle, "Contents", "MacOS", "wine")) && File.Exists(MacEngine.VideoHostExe) ? bundle : null;
#endif
        }
    }

    /// <summary>Esiste il runtime FEX accanto a quello Rosetta: la scelta compare nelle Impostazioni.</summary>
    public static bool Arm64Installed =>
        File.Exists(Path.Combine(MacEngine.WineApp, "Contents", "MacOS", "wine"));

    /// <summary>Processo di Wine con lo stesso ambiente di mac/cinecore-env.sh (VideoHost, ffprobe).</summary>
    public static ProcessStartInfo WineStartInfo(bool createCache = true)
    {
#if CINECORE_WINDOWS
        // Windows: niente Wine, si avvia direttamente VideoHost.exe (il primo argomento aggiunto dai chiamanti, il
        // percorso dell'eseguibile, il VideoHost lo ignora). I filtri arrivano dai file dei componenti di Cinecore.
        var native = new ProcessStartInfo(HostPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        native.Environment["CINECORE_D3D9_SHIM"] = "0";
        native.Environment["CINECORE_D3D9_BRIDGE"] = "0";
        if (WinNative.ThirdParty("madVR09217", "madVR64.ax") is { } madVr) native.Environment["CINECORE_MADVR_AX"] = madVr;
        if (WinNative.ThirdParty("MpcVideoRenderer-0.9.7.2387", "MpcVideoRenderer64.ax") is { } mpcVr) native.Environment["CINECORE_MPCVR_AX"] = mpcVr;
        if (WinNative.ThirdParty("XySubFilter_3.1.0.752_x64", "XySubFilter.dll") is { } xySub) native.Environment["CINECORE_XYSUB_DLL"] = xySub;
        return native;
#else
        string root = RepoRoot;
        string? arm64 = Arm64Bundle;
        bool useArm64 = !string.IsNullOrEmpty(arm64);
        string runtime = Environment.GetEnvironmentVariable("CINECORE_RUNTIME") ?? Path.Combine(root, "dist", "mac", "runtime-wine11");
        string wineRoot = Path.Combine(runtime, "wine");
        string bin = useArm64 ? Path.Combine(arm64!, "Contents", "MacOS") : Path.Combine(wineRoot, "bin");
        string data = global::CinecoreMac.PlatformPaths.CinecoreData;
        var psi = new ProcessStartInfo(Path.Combine(bin, "wine"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var env = psi.Environment;
        string cache;
        if (useArm64)
        {
            // Il runtime IOSurface usa il suo prefisso (stessi filtri, preparato da mac/run-madvr-arm64.sh --iosurface).
            string prefix = Environment.GetEnvironmentVariable("CINECORE_ARM64_PREFIX") ?? (arm64!.EndsWith("IOSurface.app", StringComparison.Ordinal)
                ? MacEngine.Prefix : Path.Combine(root, "dist", "mac", "runtime-arm64", "prefix"));
            if (arm64.EndsWith("IOSurface.app", StringComparison.Ordinal)) env["GRAPHICS_BACKEND"] = "mullion";
            foreach (string name in new[] { "WINEARCH", "WINELOADER", "WINESERVER", "WINEDLLPATH", "DYLD_FALLBACK_LIBRARY_PATH" })
                env.Remove(name);
            env["WINEPREFIX"] = prefix;
            DisableMadVrTray(prefix);
            env["WINEDLLOVERRIDES"] = "quartz=n";
            // WineD3D ARM64X può leggere un comando non valido dalla coda D3D9 sotto madVR.
            env["WINE_D3D_CONFIG"] = "csmt=0";
            // Il percorso WineMetalGL altera i colori SDR. OpenGL di macOS richiede la copia
            // esplicita delle texture tra D3D9 e D3D11.
            env["OPENGL_BACKEND"] = Environment.GetEnvironmentVariable("CINECORE_QA_OPENGL_BACKEND") ?? "wine";
            env["CINECORE_BRIDGE_ZEROCOPY"] = "0";
            // La copia inversa completa della superficie può bloccare XySubFilter sul driver ARM64: resta solo quella
            // dell'area dell'OSD di madVR (senza, le cifre nuove si sovrappongono alle vecchie).
            env["CINECORE_BRIDGE_REVERSE"] = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_REVERSE") ?? "osd";
            cache = Path.Combine(prefix, "cinecore-shaders");
        }
        else
        {
            env["WINEPREFIX"] = Environment.GetEnvironmentVariable("CINECORE_PREFIX") ?? Path.Combine(data, "wineprefix-w11");
            env["DYLD_FALLBACK_LIBRARY_PATH"] = Path.Combine(wineRoot, "lib") + ":/usr/lib:/usr/local/lib";
            env["WINEARCH"] = "win64";
            cache = System.IO.Path.Combine(global::CinecoreMac.PlatformPaths.CinecoreCaches, "dxmt");
        }
        env["PATH"] = bin + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin");
        env["WINEDEBUG"] = Environment.GetEnvironmentVariable("WINEDEBUG") ?? "-all";
        env["WINEMSYNC"] = "1";
        env["DXMT_LOG_LEVEL"] = "none";
        env["DXMT_SHADER_CACHE_PATH"] = cache;
        if (createCache) Directory.CreateDirectory(cache);
        return psi;
#endif
    }

    /// <summary>Audio separato del prossimo avvio (YouTube e simili): video e audio arrivano da due indirizzi.</summary>
    public string? ExternalAudio { get; set; }

    public async Task StartAsync(string file, string renderer, int x, int y, int w, int h, double startSeconds = 0)
    {
        if (ShuttingDown) return;
        long session = ++SessionId;
        // Dischi (cartella, ISO montata, disco inserito): ogni motore riceve l'indirizzo che sa aprire (MacDisc).
        MacDisc.Source? disc = MacDisc.TryResolve(file, out var resolved) ? resolved : null;
        // libdvdnav apre il disco (IFO, eventuali chiavi): mai sul thread dell'interfaccia.
        await Task.Run(() => AttachDvdNav(disc));
        if (session != SessionId) return;
        // Menu dei DVD con madVR / MPC-VR: menu, avvertenze e trailer con MPV (rispondono subito ai tasti), il film col
        // motore scelto, che legge il gruppo di titoli dal punto in cui e' arrivato il disco (ricerca veloce).
        DvdStreaming = false; DvdMenuOnMpv = false; DvdFeatureOn = null; _featureAnnounced = false; _sentClut = -1;
        int featureSet = 0;
        if (DvdNav is { } dvdNav && renderer != "mpv")
        {
            if (dvdNav.InFeature && dvdNav.TitleSet > 0) { featureSet = dvdNav.TitleSet; startSeconds = dvdNav.GenerationStartTitle >= 0 ? dvdNav.GenerationStartTitle : dvdNav.CurrentTitleTime; DvdFeatureOn = renderer; }
            else { DvdMenuOnMpv = true; DvdRequestedRenderer = renderer; renderer = "mpv"; }
        }
#if CINECORE_WINDOWS
        // Windows: i DVD li legge il DVD Navigator nel VideoHost (menu, protezione CSS), come il player Windows; con
        // "MPV" scelto il disco va a MPC Video Renderer (mpv non puo' usare il Navigator).
        WinDvd = null;
        bool winDvd = disc is { Kind: DiscKind.Dvd } && DvdNav == null;
        if (winDvd && renderer == "mpv") renderer = "mpcvr";
#endif
        DvdStreaming = DvdNav != null && renderer == "mpv";
        if (DvdStreaming && DvdNav is { } nav) { file = nav.Url; _navGen = MacDvdNav.GenerationOf(file); }
        else if (disc != null && renderer == "mpv") file = MacDisc.MpvUrl(disc);
        string outputUid = MacAudioDevices.ConfiguredOutputUid;
        if ((renderer == "mpv" || disc == null && string.IsNullOrWhiteSpace(ExternalAudio)) &&
            MacHdmiOverride.Target(outputUid) is { } target)
        {
            _hdmiOverride = MacHdmiOverride.TryStartAsync(target);
            var hdmi = await _hdmiOverride;
            if (session != SessionId) return;
            if (hdmi != null) _hdmiOverrideUid = outputUid = target;
        }
        if (renderer == "mpv")
        {
            MpvEngine? engine = null;
            engine = new MpvEngine(line => { if (session == SessionId && ReferenceEquals(_mpv, engine)) Dispatch(line); },
                () => { if (session == SessionId && ReferenceEquals(_mpv, engine)) Exited?.Invoke(); });
            _mpv = engine;
            engine.ExternalAudio = ExternalAudio;
            engine.Start(file, startSeconds, outputUid);
            return;
        }
        string root = RepoRoot;
        // Scelta FEX/Rosetta cambiata nelle Impostazioni: il server di Wine acceso è dell'altro runtime.
        if (_wineServer != null && _wineServerPath != null
            && !string.Equals(_wineServerPath, Path.Combine(Path.GetDirectoryName(WineStartInfo(createCache: false).FileName)!, "wineserver"), StringComparison.Ordinal))
        {
            await Task.Run(StopWine);
            WarmUpWine();
        }
        string host = HostPath;
        var psi = WineStartInfo();
        psi.RedirectStandardInput = true;
        psi.ArgumentList.Add(host);
        // Flussi di rete (Jellyfin, Emby): l'indirizzo con il token, solo in memoria; i file locali come percorso di Wine.
        bool stream = file.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || file.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        string playbackSource = stream ? CinecorePlayer2025.Utilities.JellyfinClient.PlaybackUrl(file) : file;
        bool watchLocalMadVr = renderer == "madvr" && !stream && Arm64Bundle != null;
#if CINECORE_WINDOWS
        if (winDvd)
        {
            psi.ArgumentList.Add("--dvd"); psi.ArgumentList.Add(disc!.Root);
            WinDvd = new WinDvd(Send);
        }
        else
#endif
        { psi.ArgumentList.Add("--file"); psi.ArgumentList.Add(disc != null ? (featureSet > 0 ? MacDisc.VideoHostPathForTitleSet(disc, featureSet) : MacDisc.VideoHostPath(disc)) : stream ? playbackSource : ToWinePath(file)); }
        psi.ArgumentList.Add("--renderer"); psi.ArgumentList.Add(renderer);
        psi.ArgumentList.Add("--start"); psi.ArgumentList.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--rect"); psi.ArgumentList.Add($"{x},{y},{w},{h}");
        if (!string.IsNullOrWhiteSpace(ExternalAudio))
        {
            bool web = ExternalAudio!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || ExternalAudio.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            psi.ArgumentList.Add("--audio-url"); psi.ArgumentList.Add(web ? ExternalAudio : ToWinePath(ExternalAudio));
        }
        // Scelte dei componenti (Impostazioni › Componenti, Sottotitoli): il VideoHost le scrive nel registro di Wine.
        foreach (var arg in ComponentSettings.HostArguments()) psi.ArgumentList.Add(arg);
        // Prove: nessuna uscita audio aperta (ne' DirectSound nel VideoHost ne' mpv nativo), per non toccare l'amplificatore.
        bool qaNoAudio = Environment.GetEnvironmentVariable("CINECORE_QA_NO_AUDIO") == "1" || Silent;
        bool nativeAudio = !qaNoAudio && MpvAudioSidecar.WantedFor(stream) && disc == null && string.IsNullOrWhiteSpace(ExternalAudio);
        _liveAudioSource = disc == null && string.IsNullOrWhiteSpace(ExternalAudio) && MpvEngine.Available ? playbackSource : null;
        _hostMuted = false; _lastPos = startSeconds; _lastPlaying = false; _lastOffsetMs = 0; _lastAutoDelayMs = 0;
        _hostBitstream = false;
#if CINECORE_WINDOWS
        if (!qaNoAudio) psi.ArgumentList.Add(MacSettings.Bitstream ? "--bitstream" : "--pcm");
        // Bitstream automatico con MPC Audio Renderer (uscita predefinita): i codec che l'uscita accetta, come per mpv.
        if (!qaNoAudio && !nativeAudio && MacSettings.Bitstream && MacAudioDevices.ConfiguredOutputUid.Length == 0
            && MpvAudioSidecar.MpcAudioRendererInstalled
            && MacAudioDevices.Passthrough("").Codecs is { Length: > 0 } codecs)
        { psi.ArgumentList.Add("--bitstream-codecs"); psi.ArgumentList.Add(codecs); }
#endif
        if (nativeAudio || qaNoAudio) psi.ArgumentList.Add("--no-audio");
        else if (MacAudioDevices.ConfiguredOutputUid.Length > 0 && MacSettings.AudioDeviceName is { Length: > 0 } audioName)
        { psi.ArgumentList.Add("--audio-device"); psi.ArgumentList.Add(audioName); }
        // Risparmio energetico: profilo leggero di madVR (Impostazioni > Riproduzione). Misure 11/11 su schermo virtuale a
        // 60 Hz, Interstellar 4K HDR: NGU very high + Jinc ~8,8 fotogrammi persi/s, profilo leggero ~1/s (5 prove).
        // Gli originali tornano alla chiusura, o all'avvio successivo dopo un blocco (file di ripristino del VideoHost).
        // Risparmio energetico con madVR sul runtime ARM64: consegna in catena (il fotogramma successivo parte senza
        // aspettare la GPU) e, nel VideoHost, le riduzioni del traffico di memoria che la accompagnano. Misurato il
        // 12/11/2026 su Interstellar 4K HDR con NGU molto alto + Jinc: da 3-6 fotogrammi persi al secondo a zero.
        // CINECORE_PIPELINE=0 nell'ambiente torna al percorso precedente.
        if (renderer == "madvr" && Arm64Bundle != null && MacPower.LowPowerMode && Environment.GetEnvironmentVariable("CINECORE_PIPELINE") == null)
            psi.Environment["CINECORE_PIPELINE"] = "1";
        if (renderer == "madvr" && MacSettings.MadVrLowPowerLight && MacPower.LowPowerMode)
            psi.ArgumentList.Add("--low-power");
        // Solo per le prove: argomenti in piu' per il VideoHost (es. "--madvr-set cpuQueueSize=24").
        if (Environment.GetEnvironmentVariable("CINECORE_QA_HOST_ARGS") is { Length: > 0 } extra)
            foreach (string arg in extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(arg);
        // Presentazione a vsync esatto (runtime ARM64 con il modulo cinecore_present in wined3d.so): il modulo pubblica in
        // questo file l'orologio dei vsync veri e il VideoHost vi scrive a quale vsync deve apparire ogni fotogramma.
        // Senza modulo il file non nasce e il VideoHost presenta come prima.
        string? vsyncFile = null;
        if (Arm64Bundle != null && Environment.GetEnvironmentVariable("CINECORE_VSYNC_FILE") is not { Length: > 0 })
        {
            vsyncFile = Path.Combine(Path.GetTempPath(), $"cinecore-vsync-{Environment.ProcessId}-{session}.bin");
            try { File.Delete(vsyncFile); } catch { }
            psi.Environment["CINECORE_VSYNC_FILE"] = vsyncFile;
        }
        // Frequenza dello schermo del film (il ponte di madVR altrimenti legge quella dello schermo principale).
        if (DisplayHz is >= 20 and <= 480 && Environment.GetEnvironmentVariable("CINECORE_REFRESH_HZ") == null)
            psi.Environment["CINECORE_REFRESH_HZ"] = DisplayHz.ToString("0.###", CultureInfo.InvariantCulture);
        // Processo pronto in anticipo (stesso motore, stesso ambiente): riceve gli argomenti del film su stdin.
        var standby = TakeStandby(renderer, host, psi);
        VideoToolboxSession? decoder = null;
        Process? proc = null;
        string? ownVsync = vsyncFile;
        if (standby != null)
        {
            var vtEnv = new ProcessStartInfo();
            var vtClock = Stopwatch.StartNew();
            decoder = await VideoToolboxSession.TryStartAsync(vtEnv, root, renderer);
            if (vtClock.ElapsedMilliseconds > 150) Console.Error.WriteLine($"[video] canale VideoToolbox pronto in {vtClock.ElapsedMilliseconds} ms");
            if (session != SessionId)
            {
                if (decoder != null) _ = Task.Run(decoder.Dispose);
                standby.Discard();
                return;
            }
            var goArgs = psi.ArgumentList.Skip(1).ToList();
            foreach (var kv in vtEnv.Environment)
                if (kv.Key.StartsWith("CINECORE_VT_", StringComparison.Ordinal) && kv.Value != null) { goArgs.Add("--env"); goArgs.Add(kv.Key + "=" + kv.Value); }
            try
            {
                await standby.Proc.StandardInput.WriteLineAsync("go " + System.Text.Json.JsonSerializer.Serialize(goArgs));
                await standby.Proc.StandardInput.FlushAsync();
                proc = standby.Proc;
                vsyncFile = standby.VsyncFile;
                if (ownVsync != null) { try { File.Delete(ownVsync); } catch { } }
                Console.Error.WriteLine($"[video] processo pronto in anticipo ({renderer}, preparato da {standby.AgeMs} ms)");
            }
            catch (Exception ex)
            {
                // Il processo pronto e' caduto nel frattempo: avvio normale.
                Console.Error.WriteLine("[video] processo pronto non utilizzabile: " + ex.Message);
                if (decoder != null) _ = Task.Run(decoder.Dispose);
                decoder = null;
                standby.Discard();
                standby = null;
            }
        }
        if (proc == null)
        {
            decoder = await VideoToolboxSession.TryStartAsync(psi, root, renderer);
            if (session != SessionId)
            {
                if (decoder != null) _ = Task.Run(decoder.Dispose);
                return;
            }
            try { proc = Process.Start(psi) ?? throw new InvalidOperationException("Wine non avviato"); }
            catch
            {
                if (decoder != null) _ = Task.Run(decoder.Dispose);
                throw;
            }
        }
        _proc = proc;
        if (nativeAudio)
        {
            var sidecar = _sidecar = new MpvAudioSidecar(line => { if (session == SessionId) Console.Error.WriteLine("[audio] " + line); });
            sidecar.Start(playbackSource, startSeconds, 0, outputUid);
        }
        var commands = _commands = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        Task.Run(async () =>
        {
            try
            {
                await foreach (string command in commands.Reader.ReadAllAsync())
                {
                    await proc.StandardInput.WriteLineAsync(command);
                    await proc.StandardInput.FlushAsync();
                }
            }
            catch { }
        });
        // Solo le uscite non richieste (Dispose chiude di proposito e non deve sembrare un crash).
        proc.Exited += (_, _) =>
        {
            if (vsyncFile != null) { try { File.Delete(vsyncFile); } catch { } }
            if (decoder != null) _ = Task.Run(decoder.Dispose);
            if (!ReferenceEquals(_proc, proc) || ReferenceEquals(_closing, proc)) return;
            Console.Error.WriteLine($"[video] uscita VideoHost codice=0x{proc.ExitCode:X8}");
            Exited?.Invoke();
        };
        proc.EnableRaisingEvents = true;
        Task.Run(() => ReadLoop(proc, watchLocalMadVr));
        if (standby == null) DrainStandardError(proc);
    }

    /// <summary>Errori di Wine e del VideoHost: letti sempre (una pipe piena fermerebbe il processo), su file nelle prove.</summary>
    private static void DrainStandardError(Process proc) => Task.Run(async () =>
    {
        try
        {
            using StreamWriter? log = Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST_LOG") is { Length: > 0 } path ? new StreamWriter(path + ".stderr", append: true) { AutoFlush = true } : null;
            while (await proc.StandardError.ReadLineAsync() is { } line)
                log?.WriteLine(System.Text.RegularExpressions.Regex.Replace(line, @"api_key=[^&\s]+", "api_key=…"));
        }
        catch { }
    });

    // ------------------------------------------------------------------ processo pronto in anticipo
    /// <summary>
    /// VideoHost avviato prima del film (libreria aperta, film appena chiuso): Wine, .NET, Direct3D, i ganci e le DLL dei
    /// filtri sono gia' caricati e aspetta "go [argomenti]" su stdin. Misurato il 18/11/2026 (madVR, 1080p, banco):
    /// dall'apertura al primo fotogramma ~3,2 s contro ~1 s. Si usa solo se motore, eseguibile e ambiente coincidono con
    /// quelli che servirebbero adesso (risparmio energetico, frequenza dello schermo…); altrimenti avvio normale.
    /// CINECORE_STANDBY=0 lo spegne.
    /// </summary>
    private sealed class StandbyHost
    {
        public required Process Proc { get; init; }
        public required string Renderer { get; init; }
        public required string Host { get; init; }
        public required Dictionary<string, string?> Env { get; init; }
        public string? VsyncFile { get; init; }
        public long Created { get; } = Environment.TickCount64;
        public long AgeMs => Environment.TickCount64 - Created;
        public void Discard()
        {
            try { if (!Proc.HasExited) { Proc.StandardInput.Close(); if (!Proc.WaitForExit(1500)) Proc.Kill(true); } } catch { }
            try { Proc.Dispose(); } catch { }
            if (VsyncFile != null) { try { File.Delete(VsyncFile); } catch { } }
        }
    }

    private static StandbyHost? _standby;
    private static readonly object StandbyLock = new();
    private static int _standbyCount;
    public static bool StandbyEnabled => Environment.GetEnvironmentVariable("CINECORE_STANDBY") != "0";

#if CINECORE_WINDOWS
    private static string HostPath => Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST") ?? Path.Combine(AppContext.BaseDirectory, "VideoHost", "VideoHost.exe");
#else
    private static string HostPath => Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST") ?? (Arm64Bundle != null ? MacEngine.VideoHostExe : Path.Combine(RepoRoot, "dist", "mac-native", "videohost-current", "VideoHost.exe"));
#endif

    /// <summary>L'ambiente che conta per il VideoHost (il file dei vsync e il canale VideoToolbox cambiano a ogni avvio).</summary>
    private static Dictionary<string, string?> EnvKey(ProcessStartInfo psi)
    {
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var kv in psi.Environment)
            if (kv.Key != "CINECORE_VSYNC_FILE" && !kv.Key.StartsWith("CINECORE_VT_", StringComparison.Ordinal)
#if CINECORE_WINDOWS
                // VideoHost nativo: la frequenza serve solo al ponte D3D9 di Wine. Prima il cambio 165 -> 100 Hz prima del
                // film (DVD a 25 fps) faceva scartare il processo pronto: secondi di nero e un avvio a freddo.
                && kv.Key != "CINECORE_REFRESH_HZ"
#endif
                ) d[kv.Key] = kv.Value;
        return d;
    }

    private static bool SameEnv(Dictionary<string, string?> a, Dictionary<string, string?> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Ambiente del VideoHost per questo motore (come StartAsync): catena di madVR, vsync, frequenza dello schermo.</summary>
    private static ProcessStartInfo HostStartInfo(string renderer, double displayHz, string tag, out string? vsyncFile)
    {
        var psi = WineStartInfo();
        psi.RedirectStandardInput = true;
        psi.ArgumentList.Add(HostPath);
        if (renderer == "madvr" && Arm64Bundle != null && MacPower.LowPowerMode && Environment.GetEnvironmentVariable("CINECORE_PIPELINE") == null)
            psi.Environment["CINECORE_PIPELINE"] = "1";
        vsyncFile = null;
        if (Arm64Bundle != null && Environment.GetEnvironmentVariable("CINECORE_VSYNC_FILE") is not { Length: > 0 })
        {
            vsyncFile = Path.Combine(Path.GetTempPath(), $"cinecore-vsync-{Environment.ProcessId}-{tag}.bin");
            try { File.Delete(vsyncFile); } catch { }
            psi.Environment["CINECORE_VSYNC_FILE"] = vsyncFile;
        }
        if (displayHz is >= 20 and <= 480 && Environment.GetEnvironmentVariable("CINECORE_REFRESH_HZ") == null)
            psi.Environment["CINECORE_REFRESH_HZ"] = displayHz.ToString("0.###", CultureInfo.InvariantCulture);
        return psi;
    }

    /// <summary>Prepara (o tiene) il processo pronto per questo motore. Da chiamare a libreria aperta, mai durante un film.</summary>
    public static void PrepareStandby(string renderer, double displayHz)
    {
        if (!StandbyEnabled || ShuttingDown || renderer is not ("madvr" or "mpcvr") || !WineAvailable) return;
        lock (StandbyLock)
        {
            var want = HostStartInfo(renderer, displayHz, "prova", out _);
            if (_standby is { } current)
            {
                bool alive = false;
                try { alive = !current.Proc.HasExited; } catch { }
                if (alive && current.Renderer == renderer && current.Host == HostPath && SameEnv(current.Env, EnvKey(want))) return;
                _standby = null;
                Task.Run(current.Discard);
            }
            try
            {
                var psi = HostStartInfo(renderer, displayHz, "pronto-" + Interlocked.Increment(ref _standbyCount), out string? vsync);
                psi.ArgumentList.Add("--renderer"); psi.ArgumentList.Add(renderer);
                psi.ArgumentList.Add("--standby");
                var proc = Process.Start(psi);
                if (proc == null) return;
                DrainStandardError(proc);
                _standby = new StandbyHost { Proc = proc, Renderer = renderer, Host = HostPath, Env = EnvKey(psi), VsyncFile = vsync };
                Console.Error.WriteLine($"[video] processo pronto in preparazione ({renderer})");
            }
            catch (Exception ex) { Console.Error.WriteLine("[video] processo pronto non avviato: " + ex.Message); }
        }
    }

    /// <summary>Il processo pronto, se va bene per questo avvio (lo toglie dalla riserva); altrimenti lo scarta.</summary>
    private static StandbyHost? TakeStandby(string renderer, string host, ProcessStartInfo psi)
    {
        lock (StandbyLock)
        {
            if (_standby is not { } current) return null;
            _standby = null;
            bool alive = false;
            try { alive = !current.Proc.HasExited; } catch { }
            if (alive && current.Renderer == renderer && current.Host == host && SameEnv(current.Env, EnvKey(psi))) return current;
            Console.Error.WriteLine($"[video] processo pronto scartato (motore {current.Renderer}, vivo={alive})");
            Task.Run(current.Discard);
            return null;
        }
    }

    public static void DiscardStandby()
    {
        StandbyHost? current;
        lock (StandbyLock) { current = _standby; _standby = null; }
        current?.Discard();
    }

    private void ReadLoop(Process proc, bool watchLocalMadVr)
    {
        long lastReport = 0;
        // Il timer WinForms invia la posizione anche in pausa. Se si ferma dopo il primo fotogramma,
        // il VideoHost può restare bloccato durante il fullscreen senza mai generare un evento di uscita.
        using Timer? watchdog = watchLocalMadVr ? new Timer(_ =>
        {
            long report = Volatile.Read(ref lastReport);
            if (report <= 0 || Environment.TickCount64 - report < 8000
                || !ReferenceEquals(_proc, proc) || ReferenceEquals(_closing, proc)
                || Interlocked.CompareExchange(ref lastReport, -1, report) != report) return;
            try
            {
                if (proc.HasExited) return;
                Console.Error.WriteLine("[video] madVR non risponde: riavvio del VideoHost");
                proc.Kill(true);
                KillHostProcess();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }, null, 1000, 1000) : null;

        bool playing = false;
        long frozenSince = 0;
        try
        {
            string? line;
            // Solo per le prove: le righe del VideoHost su file (senza le posizioni, mai indirizzi con il token).
            using StreamWriter? log = Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST_LOG") is { Length: > 0 } logPath ? new StreamWriter(logPath, append: true) { AutoFlush = true } : null;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                bool positionReport = line.StartsWith("pos ", StringComparison.Ordinal);
                if (watchLocalMadVr)
                {
                    long report = Volatile.Read(ref lastReport);
                    if (line == "frame" || (positionReport && report > 0))
                        Interlocked.CompareExchange(ref lastReport, Environment.TickCount64, report);
                    if (positionReport) playing = line.EndsWith(" playing", StringComparison.Ordinal);
                    else if (report > 0 && line.StartsWith("stats ", StringComparison.Ordinal) && RenderingFrozen(line, playing, ref frozenSince))
                    {
                        // 11/11: madVR può smettere di presentare mentre l'audio (e quindi la posizione) va avanti, dopo
                        // aver ricreato finestra e dispositivi. Il controllo sulla posizione non lo vede: si riavvia qui.
                        Console.Error.WriteLine("[video] madVR non presenta piu' fotogrammi: riavvio del VideoHost");
                        try { proc.Kill(true); KillHostProcess(); } catch { }
                    }
                }
                if (log != null && !positionReport)
                    log.WriteLine(System.Text.RegularExpressions.Regex.Replace(line, @"api_key=[^&\s]+", "api_key=…"));
                if (!ReferenceEquals(_proc, proc) || ReferenceEquals(_closing, proc)) continue;
                Dispatch(line);
            }
        }
        catch { }
    }

    /// <summary>
    /// Riga "stats" del VideoHost (una al secondo): vero quando, in riproduzione, madVR non ha né disegnato né
    /// presentato fotogrammi per 12 secondi di fila. In pausa o con fotogrammi nuovi il conteggio riparte.
    /// </summary>
    internal static bool RenderingFrozen(string stats, bool playing, ref long frozenSince)
    {
        bool idle = playing && stats.Contains("(+0)", StringComparison.Ordinal) && stats.Contains("| present=0 ", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.IsMatch(stats, @" disegnati=\d+\(\+0\)");
        if (!idle) { frozenSince = 0; return false; }
        long now = Environment.TickCount64;
        if (frozenSince == 0) { frozenSince = now; return false; }
        if (now - frozenSince < 12000) return false;
        frozenSince = 0;
        return true;
    }

    /// <summary>Una riga del protocollo (dal VideoHost o da mpv) diventa l'evento corrispondente.</summary>
    // ----- DVD con i menu (MacDvdNav): il navigatore produce il flusso, il motore lo ricarica a ogni salto -----
    /// <summary>Navigatore del DVD in riproduzione (null per file, Blu-ray, DVD con un titolo scelto o menu spenti).</summary>
    internal MacDvdNav? DvdNav { get; private set; }
    private int _navGen;
    private double _navEnginePos;
    /// <summary>Posizione del motore nel flusso del navigatore (per sincronizzare i pulsanti disegnati dall'app).</summary>
    public double DvdEnginePosition => _navEnginePos;
    public event Action? DvdChanged;

    private void AttachDvdNav(MacDisc.Source? disc)
    {
        MacDvdNav? nav = null;
        if (disc is { Kind: DiscKind.Dvd } && MacDisc.ChosenTitle(disc) == null && MacDvdNav.Enabled && MacDvdNav.Available)
        {
            var vobs = MacDisc.MainTitleVobs(disc);
            nav = MacDvdNav.For(disc.Root, vobs.Count > 0 && MacDisc.IsScrambled(vobs[0]));
        }
        if (ReferenceEquals(nav, DvdNav)) return;
        if (DvdNav != null) { DvdNav.Restart -= OnNavRestart; DvdNav.Changed -= OnNavChanged; }
        DvdNav = nav;
        if (nav != null) { nav.Restart += OnNavRestart; nav.Changed += OnNavChanged; }
    }

    /// <summary>Navigatore che il motore sta leggendo (null se il film del DVD e' passato a madVR / MPC-VR).</summary>
    internal MacDvdNav? ActiveDvdNav => DvdStreaming ? DvdNav : null;
    /// <summary>Il motore legge il flusso del navigatore (menu, o tutto il disco con MPV).</summary>
    public bool DvdStreaming { get; private set; }
    /// <summary>MPV mostra i menu al posto di madVR / MPC-VR (DvdRequestedRenderer), che riprendono col film.</summary>
    public bool DvdMenuOnMpv { get; private set; }
    public string? DvdRequestedRenderer { get; private set; }
    /// <summary>Il film del DVD e' passato a madVR / MPC-VR (nome del motore), fuori dal navigatore.</summary>
    public string? DvdFeatureOn { get; private set; }
    /// <summary>Nei menu con MPV e' partito il film: va passato al motore scelto.</summary>
    public event Action? DvdFeatureStarted;
    /// <summary>Il film passato a madVR / MPC-VR e' finito: si torna ai menu del disco.</summary>
    public event Action? DvdFeatureEnded;
    private bool _featureAnnounced;

    private int _sentClut = -1;

    private void OnNavChanged()
    {
        // Sottotitoli del DVD con MPV: i colori veri del disco (senza tavolozza FFmpeg li disegna in grigio).
        if (DvdStreaming && _mpv != null && DvdNav is { ClutVersion: > 0 } clutNav && clutNav.ClutVersion != _sentClut)
        {
            _sentClut = clutNav.ClutVersion;
            _mpv.Send("sub-palette " + clutNav.SubtitlePalette());
        }
        if (DvdMenuOnMpv && DvdNav is { InFeature: true, TitleSet: > 0 } && !_featureAnnounced) { _featureAnnounced = true; DvdFeatureStarted?.Invoke(); }
        DvdChanged?.Invoke();
    }

    private void OnNavRestart(string url)
    {
        _navGen = MacDvdNav.GenerationOf(url);
        if (_mpv != null) _mpv.Send("load " + url);
    }

    private void Dispatch(string line)
    {
        // Il VideoHost e' andato in crash e il debugger di Wine (AeDebug) l'ha preso: resterebbe appeso per sempre con
        // il film "in pausa". Lo si chiude: l'uscita fa ripartire il film dal punto in cui era (OnVideoExited).
        if (line.StartsWith("WineDbg attached", StringComparison.Ordinal) && _proc is { } crashed)
        {
            Console.Error.WriteLine("[video] crash del VideoHost: chiuso");
            try { crashed.Kill(true); } catch { }
            return;
        }
        if (!line.StartsWith("pos ", StringComparison.Ordinal)) Progress?.Invoke(line);
        if (line.StartsWith("pos ", StringComparison.Ordinal))
        {
            var p = line.Split(' ');
            if (p.Length >= 4
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pos)
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
            {
                _lastPos = pos; _lastPlaying = p[3] == "playing";
                // "playing cue": salto in corso, il video e' fermo finche' arriva il primo fotogramma (l'audio aspetta).
                _sidecar?.VideoPosition(pos, p[3] == "playing" && p.Length < 5);
                if (DvdStreaming && DvdNav is { } nav) { _navEnginePos = pos; nav.EnginePosition = pos; pos = nav.TitleTime(pos); dur = nav.InMenu ? 0 : nav.Duration; }
                Position?.Invoke(pos, dur, p[3] == "playing");
            }
        }
        else if (line.StartsWith("ready ", StringComparison.Ordinal)) Ready?.Invoke(line[6..]);
        else if (line == "frame") FirstFrame?.Invoke();
        else if (line.StartsWith("audiodelay ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(11), out int autoDelay)) { _lastAutoDelayMs = autoDelay; _sidecar?.AutoDelay(autoDelay); }
        else if (line.StartsWith("bitstream ", StringComparison.Ordinal)) _hostBitstream = line.EndsWith('1');
#if CINECORE_WINDOWS
        else if (line.StartsWith("dvd ", StringComparison.Ordinal)) WinDvd?.Update(line);
#endif
        else if (line.StartsWith("error ", StringComparison.Ordinal)) Error?.Invoke(line[6..]);
        else if (line == "ended" && DvdFeatureOn != null) DvdFeatureEnded?.Invoke();
        else if (line == "ended" && DvdStreaming && DvdNav is { } nav && nav.CurrentGeneration != _navGen && _mpv != null)
        {
            // Fine di un tratto del disco (avvertenze -> menu, menu -> film): si prosegue con il tratto successivo.
            _navGen = nav.CurrentGeneration;
            _mpv.Send("load " + nav.Url);
        }
        else if (line == "ended") Ended?.Invoke();
        else if (line.StartsWith("media ", StringComparison.Ordinal)) Media?.Invoke(line[6..]);
        else if (line == "tracks" || line.StartsWith("tracks ", StringComparison.Ordinal))
        {
            var list = new List<(int, int, bool, string)>();
            foreach (var entry in (line.Length > 7 ? line[7..] : "").Split('\t', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split('\u001f');
                if (f.Length == 4 && int.TryParse(f[0], out int i) && int.TryParse(f[1], out int g)) list.Add((i, g, f[2] == "1", f[3]));
            }
            _lastTracks = list;
            Tracks?.Invoke(list);
        }
        else if (line.StartsWith("stats ", StringComparison.Ordinal))
        {
            int at = line.IndexOf(" persi=", StringComparison.Ordinal);
            if (at > 0)
            {
                int end = at + 7; while (end < line.Length && char.IsDigit(line[end])) end++;
                if (int.TryParse(line.AsSpan(at + 7, end - at - 7), out int dropped)) Dropped?.Invoke(dropped);
            }
        }
    }

    public void Send(string command)
    {
        if (command.IndexOfAny(new[] { '\r', '\n' }) >= 0) return;
        if (DvdStreaming && DvdNav is { } dvd && command.StartsWith("seek ", StringComparison.Ordinal)
            && double.TryParse(command.AsSpan(5), NumberStyles.Float, CultureInfo.InvariantCulture, out double dvdSeek)) { dvd.Seek(dvdSeek); return; }
        if (_mpv != null) { _mpv.Send(command); return; }
        if (command.StartsWith("volume ", StringComparison.Ordinal) && int.TryParse(command.AsSpan(7), out int vol)) _lastVolume = vol;
        else if (command.StartsWith("audio-offset ", StringComparison.Ordinal) && int.TryParse(command.AsSpan(13), NumberStyles.Integer, CultureInfo.InvariantCulture, out int off)) _lastOffsetMs = off;
        if (_sidecar != null) ForwardAudio(command);
        // Audio passato all'mpv nativo a film avviato: il VideoHost resta muto.
        if (_hostMuted && command.StartsWith("volume ", StringComparison.Ordinal)) return;
        _commands?.Writer.TryWrite(command);
    }

    /// <summary>Comandi che riguardano l'audio, girati anche all'audio nativo (il VideoHost li riceve comunque).</summary>
    private void ForwardAudio(string command)
    {
        var p = command.Split(' ');
        var inv = CultureInfo.InvariantCulture;
        switch (p[0])
        {
            case "play": _sidecar!.Play(); break;
            case "pause": _sidecar!.Pause(); break;
            case "seek" when p.Length > 1 && double.TryParse(p[1], NumberStyles.Float, inv, out double t): _sidecar!.Seek(t); break;
            case "volume" when p.Length > 1 && int.TryParse(p[1], out int v): _sidecar!.Volume(v); break;
            case "audio-offset" when p.Length > 1 && int.TryParse(p[1], NumberStyles.Integer, inv, out int ms): _sidecar!.Offset(ms); break;
            case "select" when p.Length == 2 && int.TryParse(p[1], out int index):
            {
                // Indice dello splitter -> posizione fra le tracce audio (stesso ordine dei flussi in mpv).
                int order = 0, k = 0;
                foreach (var t2 in _lastTracks) { if (t2.Group != 1) continue; k++; if (t2.Index == index) { order = k; break; } }
                if (order > 0) _sidecar!.SelectAudio(order);
                break;
            }
        }
    }

    /// <summary>
    /// Uscita audio cambiata nel menu: si applica dal vivo, senza riaprire il film. Con mpv e con l'audio nativo affiancato
    /// cambia solo l'uscita; con l'audio del VideoHost (DirectSound di Wine, che suona sempre sul predefinito del Mac) si
    /// accende l'audio nativo al punto in corso e il VideoHost resta muto. Falso: serve riaprire (cambia il bitstream, disco).
    /// </summary>
    public bool ChangeAudioDevice(string? selectedUid = null, bool routeChanged = false)
    {
        string uid = selectedUid ?? MacAudioDevices.ConfiguredOutputUid;
        string? target = MacHdmiOverride.Target(uid);
        if (_hdmiOverrideUid != target && (_hdmiOverrideUid != null ||
            target != null && !MacAudioDevices.SupportsCompressed(target))) return false;
        string codecs = MacAudioDevices.PassthroughKey(uid);
        if (_mpv != null)
        {
            if (codecs != _mpv.SpdifCodecs) return false;
            if (routeChanged && uid.Length == 0) return false;
            _mpv.Send("prop audio-device " + (uid.Length > 0 ? "coreaudio/" + uid : "auto"));
            return true;
        }
        if (_proc is not { HasExited: false }) return false;
        if (_sidecar != null)
        {
            if (codecs != _sidecar.SpdifCodecs || routeChanged && uid.Length == 0) return false;
            _sidecar.Device(uid);
            return true;
        }
        if (uid.Length == 0) return !routeChanged;
        return StartLiveNativeAudio();
    }

    /// <summary>
    /// Accende l'audio nativo (mpv su CoreAudio) a film avviato, al punto in corso, e lascia muto il VideoHost.
    /// Falso se la sorgente non lo permette (dischi, traccia audio esterna).
    /// </summary>
    private bool StartLiveNativeAudio()
    {
        if (_liveAudioSource is not { } source) return false;
        var sidecar = new MpvAudioSidecar(line => Console.Error.WriteLine("[audio] " + line));
        sidecar.Start(source, _lastPos, _lastOffsetMs);
        sidecar.Volume(_lastVolume);
        if (_lastAutoDelayMs != 0) sidecar.AutoDelay(_lastAutoDelayMs);
        int order = 0, k = 0;
        foreach (var t in _lastTracks) { if (t.Group != 1) continue; k++; if (t.Selected) { order = k; break; } }
        if (order > 0) sidecar.SelectAudio(order);
        sidecar.VideoPosition(_lastPos, _lastPlaying);
        if (_lastPlaying) sidecar.Play();
        _sidecar = sidecar;
        _hostMuted = true;
        _commands?.Writer.TryWrite("volume 0");
        return true;
    }

    /// <summary>
    /// Amplificazione oltre il 100% (fino a +12 dB con limitatore) con tutti i motori: MPV la fa da se'; con madVR e
    /// MPC-VR l'audio passa all'mpv nativo affiancato (acceso al volo se serve) che applica lo stesso filtro.
    /// Falso se con questa sorgente non si puo' (dischi o traccia esterna con madVR / MPC-VR).
    /// </summary>
    public bool SetBoost(int db)
    {
        db = Math.Clamp(db, 0, 12);
        if (_mpv != null) { _mpv.Send("boost " + db); return true; }
        if (_proc is not { HasExited: false }) return false;
        if (_sidecar == null)
        {
            if (db == 0) return true;
            if (!StartLiveNativeAudio()) return false;
        }
        _sidecar!.Boost(db);
        return true;
    }

    public void Play() => Send("play");
    public void Pause() => Send("pause");
    public void Seek(double seconds) => Send("seek " + seconds.ToString("0.###", CultureInfo.InvariantCulture));
    public void SetVolume(int percent) => Send("volume " + Math.Clamp(percent, 0, 100));
    public void SetRect(int x, int y, int w, int h, bool show = false) =>
        Send($"rect {x} {y} {w} {h}" + (show ? " show" : ""));

    /// <summary>
    /// App Nap per i processi di Wine (MetalSharp, com.metalsharp.wine): il VideoHost non e' mai l'app in primo piano e
    /// macOS poteva rallentarne timer e thread (finestra coperta, audio spento, risparmio energetico). Una volta sola.
    /// </summary>
    private static bool _appNapChecked;
    private static void DisableWineAppNap()
    {
        if (_appNapChecked || Arm64Bundle == null) return;
        _appNapChecked = true;
        try
        {
            var write = new ProcessStartInfo("/usr/bin/defaults") { UseShellExecute = false };
            foreach (string a in new[] { "write", "com.metalsharp.wine", "NSAppSleepDisabled", "-bool", "YES" }) write.ArgumentList.Add(a);
            using var p = Process.Start(write);
            p?.WaitForExit(2000);
        }
        catch { }
    }

    /// <summary>
    /// Kill del processo "wine" lanciato raggiunge solo il lanciatore: il VideoHost vero e' un altro processo macOS e
    /// restava vivo, anche al 100% di CPU se bloccato (11/11: crash nel thread Cocoa di winemac sotto FEX, ciclo di
    /// eccezioni). Lo si termina per percorso dell'eseguibile: l'app ne avvia uno alla volta.
    /// </summary>
    private static void KillHostProcess()
    {
#if CINECORE_WINDOWS
        // Su Windows il processo avviato e' gia' il VideoHost: Kill(true) lo raggiunge. Restano solo eventuali orfani.
        try
        {
            foreach (var p in Process.GetProcessesByName("VideoHost"))
                using (p)
                    try { if (string.Equals(p.MainModule?.FileName, HostPath, StringComparison.OrdinalIgnoreCase)) p.Kill(true); } catch { }
        }
        catch { }
#else
        try
        {
            string host = Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST") ?? (Arm64Bundle != null ? MacEngine.VideoHostExe : Path.Combine(RepoRoot, "dist", "mac-native", "videohost-current", "VideoHost.exe"));
            var kill = new ProcessStartInfo("/usr/bin/pkill") { UseShellExecute = false };
            kill.ArgumentList.Add("-9"); kill.ArgumentList.Add("-f"); kill.ArgumentList.Add(host);
            using var p = Process.Start(kill);
            p?.WaitForExit(2000);
        }
        catch { }
#endif
    }

    /// <summary>Cartella "AppData\Roaming" dell'utente nel prefisso Wine in uso (dove la libaacs di Windows cerca le chiavi).</summary>
    public static string? WinePrefixAppData()
    {
#if CINECORE_WINDOWS
        return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#else
        try
        {
            string? prefix = WineStartInfo(createCache: false).Environment.TryGetValue("WINEPREFIX", out var p) ? p : null;
            if (prefix == null || !Directory.Exists(Path.Combine(prefix, "drive_c", "users"))) return null;
            string user = Directory.EnumerateDirectories(Path.Combine(prefix, "drive_c", "users")).Select(Path.GetFileName).FirstOrDefault(n => n is not null and not "Public") ?? Environment.UserName;
            return Path.Combine(prefix, "drive_c", "users", user, "AppData", "Roaming");
        }
        catch { return null; }
#endif
    }

#if CINECORE_WINDOWS
    public static string ToWinePath(string path) => Path.GetFullPath(path);
#else
    public static string ToWinePath(string unixPath) => "Z:" + Path.GetFullPath(unixPath).Replace('/', '\\');
#endif

    private Process? _closing;

    public void Dispose()
    {
        SessionId++;
        Task audioStopped = Task.CompletedTask;
        if (_mpv != null) { var engine = _mpv; _mpv = null; engine.Dispose(); audioStopped = engine.Stopped; }
        if (_sidecar != null) { var audio = _sidecar; _sidecar = null; audio.Dispose(); audioStopped = audio.Stopped; }
        var hdmi = _hdmiOverride;
        _hdmiOverride = null;
        _hdmiOverrideUid = null;
        var nativeStopped = StopNativeAudioAsync(audioStopped, hdmi);
        if (hdmi != null) _hdmiRestore = nativeStopped;
        Stopped = Task.WhenAll(Stopped, nativeStopped);
        if (_proc == null) return;
        var proc = _proc;
        _closing = proc;
        _commands?.Writer.TryWrite("quit");
        _commands?.Writer.TryComplete();
        _commands = null;
        _proc = null;
        // La chiusura del grafo puo' attendere rete/GPU: mai farlo sul thread dell'HUD.
        Stopped = Task.WhenAll(Stopped, Task.Run(async () =>
        {
            try
            {
                using var timeout = new System.Threading.CancellationTokenSource(4000);
                try { await proc.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!proc.HasExited) { proc.Kill(true); KillHostProcess(); } }
            }
            catch { }
            finally { proc.Dispose(); }
        }));
    }

    private static async Task StopNativeAudioAsync(Task audio, Task<MacHdmiOverride?>? hdmi)
    {
        await audio.ConfigureAwait(false);
        if (hdmi != null && await hdmi.ConfigureAwait(false) is { } lease)
            await lease.DisposeAsync().ConfigureAwait(false);
    }
}
