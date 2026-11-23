using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// Audio nativo per madVR e MPC-VR (sotto Wine il DirectSound di Wine accetta solo PCM: niente bitstream).
/// Con "Auto (bitstream se conviene)" e un'uscita che accetta Dolby/DTS il VideoHost tiene solo il video (audio su
/// un renderer nullo) e l'audio lo suona questa libmpv senza video, con lo stesso bitstream del motore MPV
/// (coreaudio_exclusive + audio-spdif). L'orologio e' quello del video: l'audio lo segue con piccole correzioni di
/// velocita' (PCM) o, in bitstream dove la velocita' non si puo' toccare, con un salto quando lo scarto supera 80 ms.
/// </summary>
internal sealed class MpvAudioSidecar : IDisposable
{
    private const string Lib = "libmpv";
    [DllImport(Lib)] private static extern IntPtr mpv_create();
    [DllImport(Lib)] private static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib)] private static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib)] private static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib)] private static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib)] private static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out double data);
    [DllImport(Lib)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
    [DllImport(Lib)] private static extern void mpv_free(IntPtr data);
    [DllImport(Lib)] private static extern int mpv_command(IntPtr ctx, IntPtr[] args);
    private const int FormatDouble = 5;
    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>
    /// Si usa al posto dell'audio del VideoHost? Bitstream (uscita che accetta i formati compressi) oppure un'uscita scelta
    /// nel menu: il DirectSound di Wine suona sempre sul predefinito del Mac e ignorava la scelta (film sulle cuffie anche
    /// con "Altoparlanti" scelto).
    /// </summary>
    /// <remarks>
    /// Flussi di rete: l'audio affiancato riapre lo stesso indirizzo e scarica di nuovo tutto il file (video compreso:
    /// Interstellar 4K 63 Mb/s due volte) e restava indietro a scatti (18/11/2026). Se l'uscita scelta e' gia' quella
    /// predefinita del Mac e non serve il bitstream, l'audio lo suona il VideoHost.
    /// </remarks>
    public static bool WantedFor(bool networkStream)
    {
#if CINECORE_WINDOWS
        // Windows: l'audio dei film lo suona il VideoHost con LAV Audio, come il player Windows. Solo per il bitstream
        // senza MPC Audio Renderer x64 (DirectSound non porta Dolby TrueHD / DTS-HD) lo suona mpv in WASAPI esclusivo.
        // MPC Audio Renderer suona solo sull'uscita predefinita (il VideoHost lo usa senza --audio-device): con un'uscita
        // scelta nel menu il bitstream resta a mpv.
        if (!MpvEngine.Available || !MacSettings.Bitstream) return false;
        if (MpcAudioRendererInstalled && MacAudioDevices.ConfiguredOutputUid.Length == 0) return false;
        return MacAudioDevices.Passthrough(MacAudioDevices.ConfiguredOutputUid).Codecs.Length > 0;
#endif
        if (!MpvEngine.Available) return false;
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SIDECAR") == "1") return true;
        string uid = MacAudioDevices.ConfiguredOutputUid;
        if (MacAudioDevices.Passthrough(uid).Codecs.Length > 0) return true;
        if (uid.Length == 0) return false;
        return !networkStream || uid != MacAudioDevices.ResolveUid("");
    }

    public static bool Wanted => WantedFor(false);

#if CINECORE_WINDOWS
    /// <summary>MPC Audio Renderer a 64 bit registrato (l'installer di Cinecore lo installa come opzione).</summary>
    public static bool MpcAudioRendererInstalled
    {
        get
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.ClassesRoot, Microsoft.Win32.RegistryView.Registry64);
                using var key = root.OpenSubKey(@"CLSID\{601D2A2B-9CDE-40BD-8650-0485E3522727}\InprocServer32");
                return key?.GetValue(null) is string path && Is64BitModule(Environment.ExpandEnvironmentVariables(path));
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Vero se la DLL e' a 64 bit (intestazione PE, macchina x64 o ARM64). [2026-11-22] Prima si scartava ogni percorso in
    /// SysWOW64 come "32 bit": l'installer pero' copia la versione x64 proprio li' (C:\Windows\SysWOW64\MpcAudioRenderer.ax,
    /// registrata nella vista a 64 bit) e il player la credeva assente: con il bitstream l'audio passava sempre a mpv.
    /// </summary>
    private static bool Is64BitModule(string path)
    {
        try
        {
            using var file = System.IO.File.OpenRead(path);
            var header = new byte[4096];
            int read = file.Read(header, 0, header.Length);
            if (read < 0x40 || header[0] != (byte)'M' || header[1] != (byte)'Z') return false;
            int pe = BitConverter.ToInt32(header, 0x3C);
            if (pe < 0 || pe + 6 > read || header[pe] != (byte)'P' || header[pe + 1] != (byte)'E') return false;
            ushort machine = BitConverter.ToUInt16(header, pe + 4);
            return machine is 0x8664 or 0xAA64;
        }
        catch { return false; }
    }
#endif

    private IntPtr _ctx;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action<string> _log;
    private Timer? _sync;
    private volatile bool _disposed;
    private bool _spdif;
    public string SpdifCodecs { get; private set; } = "";
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Stopped => _stopped.Task;
    public string? OutputFormat { get; private set; }

    // Ultima posizione del video (VideoHost) e quando e' arrivata.
    private double _videoPos = -1; private long _videoAt; private bool _videoPlaying;
    private bool _audioPlaying;
    private double _userOffset, _autoOffset; // ritardo scelto nel menu e quello che compensa il ponte di madVR (positivo = audio dopo)
    private double _offsetSeconds => _userOffset + _autoOffset;
    private double _speed = 1;
    private readonly double[] _diffs = new double[5]; private int _diffCount;
    private long _lastSeekAt = -10000;
    public double LastDiffMs { get; private set; }
    public int Seeks { get; private set; }

    public MpvAudioSidecar(Action<string> log)
    {
        _log = log;
        _worker = new Thread(() => { foreach (var a in _work.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { _log("info audio nativo: " + ex.Message); } } })
        { IsBackground = true, Name = "mpv audio" };
        _worker.Start();
    }

    public void Start(string file, double startSeconds, double offsetMs, string? outputUid = null)
    {
        _userOffset = offsetMs / 1000.0;
        _work.Add(() =>
        {
            MpvEngine.EnsureResolver();
            _ctx = mpv_create();
            if (_ctx == IntPtr.Zero) throw new InvalidOperationException("libmpv non disponibile");
            void Opt(string k, string v) => mpv_set_option_string(_ctx, U(k), U(v));
            Opt("vid", "no"); Opt("sid", "no"); Opt("audio-display", "no"); Opt("terminal", "no");
            Opt("idle", "yes"); Opt("pause", "yes"); Opt("keep-open", "yes");
            Opt("alang", "it,ita,en,eng");
            Opt("audio-pitch-correction", "no"); // le correzioni di velocita' sono minime: niente scaletempo
            string uid = outputUid ?? MacAudioDevices.ConfiguredOutputUid;
            if (uid.Length > 0) Opt("audio-device", MpvAudioNames.DevicePrefix + uid);
            var (codecs, carrier) = MacAudioDevices.Passthrough(uid);
            SpdifCodecs = MacAudioDevices.PassthroughKey(uid);
            _spdif = codecs.Length > 0;
            if (_spdif)
            {
                Opt("audio-spdif", codecs);
                MpvAudioNames.Exclusive(Opt);
                if (carrier) Opt("coreaudio-spdif-hack", "yes");
            }
            // Solo prove: uscita diversa (pcm su file), come CINECORE_MPV_AO del motore MPV.
            if (Environment.GetEnvironmentVariable("CINECORE_MPV_AO") is { Length: > 0 } qaAo)
            {
                Opt("ao", qaAo);
                if (Environment.GetEnvironmentVariable("CINECORE_QA_SIDECAR_PCM_FILE") is { Length: > 0 } pcmFile) { Opt("ao-pcm-file", pcmFile); Opt("ao-pcm-waveheader", "no"); }
            }
            if (Environment.GetEnvironmentVariable("CINECORE_QA_MUTE") == "1" || Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST_MUTE") == "1") Opt("mute", "yes");
            if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null && System.IO.File.Exists(file) &&
                Environment.GetEnvironmentVariable("CINECORE_MPV_LOG") is { Length: > 0 } log)
            {
                Opt("log-file", log);
                Opt("msg-level", "all=v");
            }
            if (startSeconds > 0) Opt("start", startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            int init = mpv_initialize(_ctx);
            MacAppIcon.RestoreSoon(); // libmpv mette la sua icona all'app
            if (init < 0) throw new InvalidOperationException("mpv audio non inizializzato");
            Command("loadfile", file);
            _log($"info audio nativo: avviato ({(_spdif ? "passthrough automatico" : "PCM")})");
        });
        _sync = new Timer(_ => _work.Add(SyncStep), null, 500, 200);
    }

    /// <summary>Posizione del video (righe "pos" del VideoHost): e' l'orologio che l'audio segue.</summary>
    public void VideoPosition(double seconds, bool playing)
    {
        _videoPos = seconds; _videoAt = _clock.ElapsedMilliseconds; _videoPlaying = playing;
    }

    /// <summary>Uscita audio cambiata dal vivo (mpv riapre solo l'uscita, la posizione resta).</summary>
    public void Device(string uid) => _work.Add(() => Set("audio-device", uid.Length > 0 ? MpvAudioNames.DevicePrefix + uid : "auto"));
    public void Play() => _work.Add(() => { Set("pause", "no"); _audioPlaying = true; });
    public void Pause() => _work.Add(() => { Set("pause", "yes"); _audioPlaying = false; });
    public void Seek(double seconds) => _work.Add(() => { _resyncPending = false; SeekExact(seconds - _offsetSeconds); });
    public void Volume(int percent) => _work.Add(() => Set("volume", Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture)));
    /// <summary>Ritardo audio (menu Audio): l'audio atteso e' "video - ritardo"; lo scarto lo riallinea il passo successivo.</summary>
    public void Offset(int ms) => _work.Add(() =>
    {
        double old = _offsetSeconds;
        _userOffset = ms / 1000.0;
        if (Math.Abs(old - _offsetSeconds) > 0.02 && _videoPos >= 0)
            SeekExact(_videoPos + (_clock.ElapsedMilliseconds - _videoAt) / 1000.0 * (_videoPlaying ? 1 : 0) - _offsetSeconds);
    });
    /// <summary>Ritardo automatico del VideoHost (fotogrammi trattenuti dal ponte di madVR).</summary>
    public void AutoDelay(int ms) => _work.Add(() => { _autoOffset = ms / 1000.0; _log($"info audio nativo: ritardo automatico {ms} ms"); });

    /// <summary>
    /// Amplificazione oltre il 100% con madVR / MPC-VR (lo stesso filtro del motore MPV): volume in dB e limitatore.
    /// </summary>
    public void Boost(int db) => _work.Add(() =>
    {
        Command("af", "remove", "@cineboost");
        if (db > 0) Command("af", "add", "@cineboost:lavfi=[volume=" + db + "dB,alimiter=level_in=1:level_out=1:limit=0.97:attack=5:release=60:level=disabled]");
        string af = "";
        if (_ctx != IntPtr.Zero && mpv_get_property_string(_ctx, U("af")) is var p && p != IntPtr.Zero) { af = Marshal.PtrToStringUTF8(p) ?? ""; mpv_free(p); }
        _log($"info audio nativo: amplificazione +{db} dB (filtri: {(af.Length > 0 ? af : "nessuno")})");
    });

    /// <summary>Traccia audio per posizione fra quelle del file (1 = la prima), come l'ordine dei flussi di LAV.</summary>
    public void SelectAudio(int order) => _work.Add(() => Set("aid", Math.Max(1, order).ToString(CultureInfo.InvariantCulture)));

    private double _seekLead;
    private bool _resyncPending;

    private void SeekExact(double seconds)
    {
        Command("seek", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
        _lastSeekAt = _clock.ElapsedMilliseconds; _diffCount = 0;
        Seeks++;
    }

    private void SyncStep()
    {
        if (_ctx == IntPtr.Zero || _disposed || _videoPos < 0) return;
        IntPtr formatPtr = mpv_get_property_string(_ctx, U("audio-out-params/format"));
        if (formatPtr != IntPtr.Zero)
        {
            string? format = Marshal.PtrToStringUTF8(formatPtr);
            mpv_free(formatPtr);
            if (format != OutputFormat) { OutputFormat = format; _log("info audio nativo: formato di uscita " + format); }
        }
        bool bitstream = OutputFormat?.StartsWith("spdif-", StringComparison.Ordinal) == true;
        long now = _clock.ElapsedMilliseconds;
        // Pausa e ripresa seguono il video (anche quelle decise dal VideoHost, es. fine file).
        if (_videoPlaying != _audioPlaying) { Set("pause", _videoPlaying ? "no" : "yes"); _audioPlaying = _videoPlaying; }
        if (!_videoPlaying || now - _lastSeekAt < 700) return;
        double video = _videoPos + (now - _videoAt) / 1000.0;
        if (mpv_get_property(_ctx, U("time-pos"), FormatDouble, out double audio) < 0) return;
        double diff = audio - (video - _offsetSeconds);
        _diffs[_diffCount++ % _diffs.Length] = diff;
        if (_diffCount < _diffs.Length) return;
        double avg = 0; foreach (double d in _diffs) avg += d; avg /= _diffs.Length;
        LastDiffMs = avg * 1000;
        if (Math.Abs(avg) > (bitstream ? 0.08 : 0.3))
        {
            // Un salto a riproduzione in corso arriva in ritardo (tempo di lettura e decodifica: ~0,3 s da file, ~1 s in
            // streaming) e l'audio restava indietro, riallineo dopo riallineo. Se un riallineo non e' bastato si impara
            // l'anticipo che serve e il salto successivo punta piu' avanti.
            if (_resyncPending) _seekLead = Math.Clamp(_seekLead - avg, 0, 3);
            _resyncPending = true;
            _log($"info audio nativo: scarto {avg * 1000:0} ms, riallineo (anticipo {_seekLead * 1000:0} ms)");
            SeekExact(video - _offsetSeconds + _seekLead);
            if (_speed != 1) { _speed = 1; Set("speed", "1"); }
            return;
        }
        _resyncPending = false;
        if (_spdif) return; // A subsequent track may open in bitstream and needs speed=1.
        // PCM: velocita' leggermente diversa finche' lo scarto rientra (max 0,5%, impercettibile).
        double target = 1 - Math.Clamp(avg * 0.25, -0.005, 0.005);
        if (Math.Abs(target - _speed) > 0.0005) { _speed = target; Set("speed", target.ToString("0.#####", CultureInfo.InvariantCulture)); }
    }

    private void Set(string name, string value) { if (_ctx != IntPtr.Zero) mpv_set_property_string(_ctx, U(name), U(value)); }

    private void Command(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            mpv_command(_ctx, ptrs);
        }
        finally { foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sync?.Dispose();
        _work.Add(() =>
        {
            try { if (_ctx != IntPtr.Zero) { mpv_terminate_destroy(_ctx); _ctx = IntPtr.Zero; } }
            finally { _stopped.TrySetResult(); }
        });
        _work.CompleteAdding();
    }
}
