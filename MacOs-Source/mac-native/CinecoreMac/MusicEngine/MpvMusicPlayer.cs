using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CinecoreMac;

/// <summary>Lettore che il motore della musica usa al posto della propria catena (AVPlayer o bitstream).</summary>
internal interface IMusicFallback : IDisposable
{
    string? Source { get; }
    void Open(string pathOrUrl);
    void SetOutput(string uid);
    float Volume { get; set; }
    bool Muted { get; set; }
    void Play();
    void Pause();
    bool IsPlaying { get; }
    double Position { get; }
    double Duration { get; }
    bool Failed { get; }
    void Seek(double seconds);
    void Stop();
}

/// <summary>
/// Brani Dolby / DTS / Atmos con l'uscita in bitstream (come Windows: la musica PCM passa dal Cinecore Audio Engine,
/// bit perfect compreso, ma un brano AC3, E-AC3/Atmos, TrueHD o DTS va al ricevitore originale). mpv con audio-spdif in
/// uscita esclusiva; la decisione per formato e' la stessa dei film (MacAudioDevices.Passthrough / IsPlainDts).
/// </summary>
internal sealed class MpvMusicPlayer : IMusicFallback
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

    private IntPtr _ctx;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private Timer? _poll;
    private volatile bool _disposed;
    // Stato letto dal thread di mpv (mai chiamate a libmpv dal thread principale).
    private volatile bool _paused, _eof, _failed, _loaded;
    private double _position, _duration;
    private float _volume = 1;
    private bool _muted;
    private string _codecs = "";

    public string? Source { get; private set; }

    public MpvMusicPlayer()
    {
        _worker = new Thread(() => { foreach (var a in _work.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { Console.Error.WriteLine("[musica bitstream] " + ex.Message); } } })
        { IsBackground = true, Name = "mpv music" };
        _worker.Start();
    }

    /// <summary>
    /// Codec da mandare in bitstream per questo brano, o null se va decodificato (PCM, bit perfect compreso).
    /// Legge il primo flusso audio con ffprobe: solo con "Uscita audio: Bitstream" e un'uscita che accetta il formato.
    /// </summary>
    public static string? BitstreamCodecs(string path)
    {
        if (!MacSettings.Bitstream || !MpvEngine.Available) return null;
        string uid = MacAudioDevices.ConfiguredOutputUid;
        string passthrough = MacAudioDevices.Passthrough(uid);
        if (passthrough.Length == 0) return null;
        try
        {
            if (MacTools.Ffprobe is not { } probe || path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            var (exit, stdout, _) = ProcessLauncher.Run(probe, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,profile", "-of", "json", path }, false, 5000, CancellationToken.None);
            if (exit != 0 || stdout.Length == 0) return null;
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return null;
            string codec = streams[0].TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
            string profile = streams[0].TryGetProperty("profile", out var p) ? p.GetString() ?? "" : "";
            var list = passthrough.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (Array.IndexOf(list, codec) >= 0) return passthrough;
            if (MacAudioDevices.CanCarryDts(passthrough) && MacAudioDevices.IsPlainDts(codec, profile)) return passthrough + ",dts";
        }
        catch { }
        return null;
    }

    /// <summary>Da chiamare prima di Open con il risultato di <see cref="BitstreamCodecs"/>.</summary>
    public void Configure(string codecs) => _codecs = codecs;

    public void Open(string pathOrUrl)
    {
        Source = pathOrUrl;
        _eof = _failed = _loaded = false; _paused = false; _position = 0; _duration = 0;
        string codecs = _codecs;
        _work.Add(() =>
        {
            if (_ctx == IntPtr.Zero)
            {
                MpvEngine.EnsureResolver();
                _ctx = mpv_create();
                if (_ctx == IntPtr.Zero) { _failed = true; return; }
                void Opt(string k, string v) => mpv_set_option_string(_ctx, U(k), U(v));
                Opt("vid", "no"); Opt("sid", "no"); Opt("audio-display", "no"); Opt("terminal", "no");
                Opt("idle", "yes"); Opt("keep-open", "yes");
                string uid = MacAudioDevices.ConfiguredOutputUid;
                if (uid.Length > 0) Opt("audio-device", "coreaudio/" + uid);
                Opt("ao", "coreaudio_exclusive,coreaudio");
                Opt("audio-spdif", codecs);
                if (Environment.GetEnvironmentVariable("CINECORE_MPV_AO") is { Length: > 0 } qaAo) Opt("ao", qaAo);
                if (Environment.GetEnvironmentVariable("CINECORE_QA_MUTE") == "1") Opt("mute", "yes");
                if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null && Environment.GetEnvironmentVariable("CINECORE_MPV_LOG") is { Length: > 0 } log)
                { Opt("log-file", log); Opt("msg-level", "all=v"); }
                if (mpv_initialize(_ctx) < 0) { _failed = true; return; }
                MacAppIcon.RestoreSoon(); // libmpv mette la sua icona all'app
            }
            else Set("audio-spdif", codecs);
            Set("pause", "no");
            Command("loadfile", pathOrUrl, "replace");
            Console.Error.WriteLine($"[musica] bitstream ({codecs}): {System.IO.Path.GetFileName(pathOrUrl)}");
        });
        _poll ??= new Timer(_ => { if (!_disposed) try { _work.Add(Poll); } catch (InvalidOperationException) { } }, null, 200, 200);
    }

    private void Poll()
    {
        if (_ctx == IntPtr.Zero) return;
        if (mpv_get_property(_ctx, U("time-pos"), FormatDouble, out double pos) >= 0) { _position = pos; _loaded = true; }
        if (mpv_get_property(_ctx, U("duration"), FormatDouble, out double dur) >= 0) _duration = dur;
        _paused = Get("pause") == "yes";
        _eof = Get("eof-reached") == "yes";
        // Apertura fallita: mpv torna inattivo senza mai dare una posizione.
        if (!_loaded && Get("idle-active") == "yes" && Source != null) _failed = true;
    }

    private string? Get(string name)
    {
        IntPtr p = mpv_get_property_string(_ctx, U(name));
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); } finally { mpv_free(p); }
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

    public void SetOutput(string uid) => _work.Add(() => Set("audio-device", uid.Length > 0 ? "coreaudio/" + uid : "auto"));

    // In bitstream il volume lo decide il ricevitore: mpv lo ignora. Si tiene per il passaggio al PCM.
    public float Volume { get => _volume; set { _volume = Math.Clamp(value, 0f, 1f); ApplyVolume(); } }
    public bool Muted { get => _muted; set { _muted = value; ApplyVolume(); } }
    private void ApplyVolume()
    {
        string v = (_muted ? 0 : _volume * 100).ToString("0.#", CultureInfo.InvariantCulture);
        _work.Add(() => Set("volume", v));
    }

    public void Play() { _paused = false; _work.Add(() => Set("pause", "no")); }
    public void Pause() { _paused = true; _work.Add(() => Set("pause", "yes")); }
    public bool IsPlaying => Source != null && !_paused && !_eof && !_failed;
    public double Position => _position;
    public double Duration => _duration;
    public bool Failed => _failed;
    public void Seek(double seconds)
    {
        _position = Math.Max(0, seconds); _eof = false;
        string s = _position.ToString("0.###", CultureInfo.InvariantCulture);
        _work.Add(() => Command("seek", s, "absolute+exact"));
    }

    public void Stop()
    {
        Source = null; _paused = true;
        _work.Add(() => Command("stop"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll?.Dispose();
        try
        {
            _work.Add(() => { if (_ctx != IntPtr.Zero) { mpv_terminate_destroy(_ctx); _ctx = IntPtr.Zero; } });
            _work.CompleteAdding();
        }
        catch (InvalidOperationException) { }
    }
}
