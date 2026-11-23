using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CinecoreMac;

/// <summary>
/// Un libmpv nascosto (niente finestra ne' audio) che tiene un film aperto in pausa con il decoder della scheda video
/// pronto, e ne restituisce fotogrammi passati da un filtro (2026-11-21). Lo usano l'anteprima della barra del tempo
/// (GpuFramePreview) e l'analisi HDR/SDR (GpuFrameMeter). Non e' thread-safe: un oggetto per thread.
/// </summary>
internal sealed class MpvFrameGrabber : IDisposable
{
    private const string Lib = "libmpv";
    [DllImport(Lib)] private static extern IntPtr mpv_create();
    [DllImport(Lib)] private static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib)] private static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib)] private static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib)] private static extern int mpv_command(IntPtr ctx, IntPtr[] args);
    [DllImport(Lib)] private static extern int mpv_command_ret(IntPtr ctx, IntPtr[] args, out MpvNode result);
    [DllImport(Lib)] private static extern void mpv_free_node_contents(ref MpvNode node);
    [DllImport(Lib)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
    [DllImport(Lib)] private static extern void mpv_free(IntPtr data);

    [StructLayout(LayoutKind.Sequential)] private struct MpvEvent { public int EventId; public int Error; public ulong Reply; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct MpvNode { public IntPtr Value; public int Format; }
    [StructLayout(LayoutKind.Sequential)] private struct MpvNodeList { public int Count; public IntPtr Values; public IntPtr Keys; }
    [StructLayout(LayoutKind.Sequential)] private struct MpvByteArray { public IntPtr Data; public IntPtr Size; }

    private const int EventShutdown = 1, EventEndFile = 7, EventFileLoaded = 8, EventPlaybackRestart = 21;
    private const int FormatString = 1, FormatInt64 = 4, FormatNodeMap = 8, FormatByteArray = 9;

    /// <summary>Un fotogramma come lo restituisce mpv: valido solo dentro la funzione che lo legge.</summary>
    public readonly record struct Raw(int Width, int Height, long Stride, string Format, IntPtr Data, long Size);

    private IntPtr _mpv;
    public string File { get; }
    public string Filter { get; }

    private MpvFrameGrabber(IntPtr mpv, string file, string filter) { _mpv = mpv; File = file; Filter = filter; }

    /// <summary>
    /// Apre il film in pausa. <paramref name="fast"/>: decodifica approssimata (senza filtro di deblocking, un thread),
    /// buona per le anteprime; falso per le misure.
    /// </summary>
    public static MpvFrameGrabber? Open(string file, string filter, bool fast, int timeoutMs = 8000, bool untimed = false)
    {
        if (!MpvEngine.Available) return null;
        MpvEngine.EnsureResolver();
        IntPtr mpv = mpv_create();
        if (mpv == IntPtr.Zero) return null;
        var grabber = new MpvFrameGrabber(mpv, file, filter);
        var options = new List<(string, string)>
        {
            ("vo", "null"), ("ao", "null"), ("aid", "no"), ("sid", "no"), ("pause", "yes"), ("terminal", "no"),
            ("hwdec", Environment.GetEnvironmentVariable("CINECORE_GRAB_HWDEC") is { Length: > 0 } hw ? hw : "d3d11va-copy,nvdec-copy"), ("hr-seek", "no"), ("keep-open", "always"), ("cache", "no"),
            ("demuxer-readahead-secs", "0"), ("load-scripts", "no"), ("config", "no"), ("input-default-bindings", "no"),
            ("vf", filter),
        };
        // Senza attese fra un fotogramma e l'altro: frame-step costa solo la decodifica (StepTo).
        if (untimed) options.Add(("untimed", "yes"));
        if (fast) { options.Add(("vd-lavc-skiploopfilter", "all")); options.Add(("vd-lavc-threads", "1")); }
        else options.Add(("vd-lavc-threads", "2"));
        foreach (var (k, v) in options) mpv_set_option_string(mpv, U(k), U(v));
        if (mpv_initialize(mpv) < 0) { grabber.Dispose(); return null; }
        grabber.Command("loadfile", file);
        if (!grabber.WaitFor(EventFileLoaded, timeoutMs) || !grabber.WaitFor(EventPlaybackRestart, timeoutMs)) { grabber.Dispose(); return null; }
        return grabber;
    }

    public string? HwDecoder => Property("hwdec-current");

    /// <summary>
    /// Salta al punto: <paramref name="exact"/> il fotogramma esatto, altrimenti il fotogramma chiave li' o subito prima.
    /// Falso se il decoder non riparte: il fotogramma sarebbe ancora quello del punto precedente.
    /// </summary>
    public bool Seek(double seconds, bool exact, int timeoutMs = 3000)
    {
        Command("seek", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), exact ? "absolute+exact" : "absolute+keyframes");
        return WaitFor(EventPlaybackRestart, timeoutMs);
    }

    /// <summary>
    /// Avanza di fotogramma in fotogramma fino a <paramref name="seconds"/>, senza ripartire dal fotogramma chiave come
    /// un salto (serve un grabber aperto con untimed, altrimenti ogni passo aspetta l'ora del fotogramma). Falso se il
    /// punto e' indietro o piu' avanti di <paramref name="maxFrames"/> fotogrammi, o se mpv non avanza: allora si salta.
    /// </summary>
    public bool StepTo(double seconds, int maxFrames, int timeoutMs = 1000)
    {
        double? now = Position;
        if (now == null) return false;
        if (_frame <= 0)
            _frame = double.TryParse(Property("container-fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && f > 1 ? 1 / f : 1 / 24.0;
        double frame = _frame;
        if (seconds < now.Value - frame / 2 || seconds > now.Value + maxFrames * frame) return false;
        var clock = Stopwatch.StartNew();
        while (seconds >= now.Value + frame / 2)
        {
            double before = now.Value;
            Command("frame-step");
            while (true)
            {
                if (clock.ElapsedMilliseconds > timeoutMs) return false;
                mpv_wait_event(_mpv, 0.001);
                now = Position;
                if (now == null) return false;
                if (now.Value > before + frame / 4) break;
            }
        }
        return true;
    }
    private double _frame;

    /// <summary>La posizione del fotogramma mostrato, dall'inizio del film (come time-pos di mpv).</summary>
    public double? Position =>
        double.TryParse(Property("time-pos"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) ? t : null;

    /// <summary>
    /// Il fotogramma attuale dopo il filtro, nel formato chiesto (null: quello di mpv, bgr0). <paramref name="read"/>
    /// lo copia dove serve; la memoria di mpv si libera subito dopo.
    /// </summary>
    public T? Screenshot<T>(string? format, Func<Raw, T?> read) where T : class
    {
        var args = format == null ? Args("screenshot-raw", "video") : Args("screenshot-raw", "video", format);
        try
        {
            if (mpv_command_ret(_mpv, args.Pointers, out var node) < 0) return null;
            try
            {
                if (node.Format != FormatNodeMap) return null;
                var list = Marshal.PtrToStructure<MpvNodeList>(node.Value);
                long width = 0, height = 0, stride = 0; string? fmt = null; IntPtr data = IntPtr.Zero; long size = 0;
                int nodeSize = Marshal.SizeOf<MpvNode>();
                for (int i = 0; i < list.Count; i++)
                {
                    string key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list.Keys, i * IntPtr.Size)) ?? "";
                    var value = Marshal.PtrToStructure<MpvNode>(list.Values + i * nodeSize);
                    if (value.Format == FormatInt64)
                    {
                        long n = value.Value.ToInt64();
                        if (key == "w") width = n; else if (key == "h") height = n; else if (key == "stride") stride = n;
                    }
                    else if (value.Format == FormatString && key == "format") fmt = Marshal.PtrToStringUTF8(value.Value);
                    else if (value.Format == FormatByteArray && key == "data")
                    {
                        var bytes = Marshal.PtrToStructure<MpvByteArray>(value.Value);
                        data = bytes.Data; size = bytes.Size.ToInt64();
                    }
                }
                if (data == IntPtr.Zero || width <= 0 || height <= 0 || fmt == null) return null;
                return read(new Raw((int)width, (int)height, stride, fmt, data, size));
            }
            finally { mpv_free_node_contents(ref node); }
        }
        finally { args.Dispose(); }
    }

    public void Dispose()
    {
        if (_mpv != IntPtr.Zero) { try { mpv_terminate_destroy(_mpv); } catch { } }
        _mpv = IntPtr.Zero;
    }

    private bool WaitFor(int eventId, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            IntPtr p = mpv_wait_event(_mpv, Math.Max(.01, (ms - clock.ElapsedMilliseconds) / 1000.0));
            if (p == IntPtr.Zero) continue;
            var e = Marshal.PtrToStructure<MpvEvent>(p);
            if (e.EventId == eventId) return true;
            if (e.EventId is EventEndFile or EventShutdown) return false;
        }
        return false;
    }

    private void Command(params string[] parts)
    {
        var args = Args(parts);
        try { mpv_command(_mpv, args.Pointers); }
        finally { args.Dispose(); }
    }

    private string? Property(string name)
    {
        IntPtr p = mpv_get_property_string(_mpv, U(name));
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); } finally { mpv_free(p); }
    }

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s + "\0");

    private sealed class NativeArgs : IDisposable
    {
        public readonly IntPtr[] Pointers;
        public NativeArgs(string[] parts)
        {
            Pointers = new IntPtr[parts.Length + 1];
            for (int i = 0; i < parts.Length; i++)
            {
                byte[] b = U(parts[i]);
                Pointers[i] = Marshal.AllocHGlobal(b.Length);
                Marshal.Copy(b, 0, Pointers[i], b.Length);
            }
        }
        public void Dispose() { foreach (var p in Pointers) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
    }

    private static NativeArgs Args(params string[] parts) => new(parts);
}

/// <summary>
/// Fotogrammi per l'analisi HDR/SDR dalla scheda video (2026-11-21). Su Windows ogni campione avviava un ffmpeg nuovo
/// che riapriva il film; a film fermo chiedeva pure il decoder del Mac (videotoolbox), falliva sempre e l'analisi
/// finiva senza misure; a film in riproduzione decodificava il 4K HEVC con la CPU, tre alla volta, contro la
/// riproduzione. Qui il film resta aperto in un decoder NVDEC/D3D11 e ogni campione costa ~30-45 ms. Il fotogramma
/// e' identico al pixel a quello di ffmpeg (stessa scala, gbrp16 a gamma piena).
/// </summary>
internal sealed class GpuFrameMeter : IDisposable
{
    private static int _failures;
    /// <summary>Falso dopo errori ripetuti o con CINECORE_HDR_FFMPEG=1: da li' le misure tornano a ffmpeg.</summary>
    public static bool Usable => MpvEngine.Available && _failures < 6 && Environment.GetEnvironmentVariable("CINECORE_HDR_FFMPEG") != "1";

    private readonly string _file;
    private readonly int _w, _h;
    private readonly object _gate = new();
    private MpvFrameGrabber? _grabber;
    private bool _disposed;

    public GpuFrameMeter(string file, int width, int height) { _file = file; _w = width; _h = height; }

    /// <summary>Tre piani G, B, R a 16 bit (gbrp16le) e il tempo del fotogramma; null se non riesce.</summary>
    public (byte[] Planes, double Time)? Grab(double seconds, bool exact, System.Threading.CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed || ct.IsCancellationRequested || !Usable) return null;
            try
            {
                if (_grabber == null)
                {
                    string filter = $"lavfi=[scale=w={_w}:h={_h}:flags=neighbor+accurate_rnd+full_chroma_int+full_chroma_inp:out_range=full," +
                                    "format=rgba64le,setsar=1,setparams=range=pc:color_primaries=bt709:color_trc=iec61966-2-1:colorspace=gbr]";
                    var clock = Stopwatch.StartNew();
                    _grabber = MpvFrameGrabber.Open(_file, filter, fast: false, untimed: true);
                    if (_grabber == null) { Fail("apertura"); return null; }
                    Console.Error.WriteLine($"[analisi GPU] film aperto in {clock.ElapsedMilliseconds} ms ({_grabber.HwDecoder ?? "cpu"})");
                }
                // Poco avanti (il film che scorre, una misura dopo l'altra): avanti a passi, ~3 ms, invece di un salto
                // esatto che ridecodifica dal fotogramma chiave (40-65 ms in un 4K HEVC: a "24 fps" se ne facevano 16).
                bool stepped = exact && _grabber.StepTo(seconds, 24);
                if (!stepped && !_grabber.Seek(seconds, exact)) return null;
                double time = _grabber.Position ?? seconds;
                int w = _w, h = _h;
                var planes = _grabber.Screenshot("rgba64", raw =>
                {
                    if (raw.Width != w || raw.Height != h || raw.Format != "rgba64" || raw.Stride < w * 8 || raw.Size < raw.Stride * (h - 1) + w * 8)
                    {
                        Console.Error.WriteLine($"[analisi GPU] fotogramma inatteso {raw.Width}x{raw.Height} {raw.Format}");
                        return null;
                    }
                    return ToPlanes(raw, w, h);
                });
                if (planes == null) { Fail("fotogramma"); return null; }
                _failures = 0;
                return (planes, time);
            }
            catch (Exception ex) { Fail(ex.Message); return null; }
        }
    }

    private void Fail(string why)
    {
        Console.Error.WriteLine("[analisi GPU] " + why);
        _grabber?.Dispose(); _grabber = null;
        if (++_failures >= 6) Console.Error.WriteLine("[analisi GPU] troppi errori: torno a ffmpeg");
    }

    private static unsafe byte[] ToPlanes(MpvFrameGrabber.Raw raw, int w, int h)
    {
        var planes = new byte[w * h * 6];
        fixed (byte* outBytes = planes)
        {
            ushort* g = (ushort*)outBytes, b = g + w * h, r = b + w * h;
            for (int y = 0; y < h; y++)
            {
                ushort* src = (ushort*)((byte*)raw.Data + y * raw.Stride);
                int row = y * w;
                for (int x = 0; x < w; x++, src += 4)
                {
                    r[row + x] = src[0]; g[row + x] = src[1]; b[row + x] = src[2];
                }
            }
        }
        return planes;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _grabber?.Dispose(); _grabber = null; }
    }
}
