using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// Fotogrammi dell'anteprima della barra del tempo decodificati dalla scheda video (2026-11-21).
/// Prima ogni passaggio sulla barra avviava un FFmpeg nuovo che apriva il film e decodificava con un core della CPU:
/// su un 4K HEVC 10 bit 300-450 ms per fotogramma e circa 550 ms per la striscia (sul Mac lo stesso lavoro dura un
/// terzo, un core di Apple Silicon e' molto piu' veloce). Qui un libmpv nascosto (niente finestra ne' audio) tiene il
/// film aperto in pausa con il decoder NVDEC/D3D11 gia' pronto: a ogni punto salta al fotogramma chiave e lo restituisce.
/// 4K HEVC circa 50 ms, 720p H.264 circa 15 ms. Il filtro di scala e' lo stesso di FFmpeg (matrice, gamma e livelli
/// del file) e l'HDR passa dallo stesso MapToSdr: i fotogrammi SDR sono identici al pixel a quelli di prima.
/// Se libmpv o la decodifica non vanno, si torna a FFmpeg (TimelinePreview).
/// </summary>
internal sealed class GpuFramePreview
{
    public static readonly GpuFramePreview Shared = new();

    private sealed record Job(string File, double Time, int Width, ProbeData Probe, CancellationToken Token, TaskCompletionSource<MenuAmbient.Frame?> Done);

    private static readonly bool Qa = Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null;
    private readonly BlockingCollection<Job> _jobs = new();
    private Thread? _worker;
    private MpvFrameGrabber? _grabber;
    private int _failures;

    /// <summary>Falso dopo errori ripetuti (libmpv mancante, file che mpv non apre): da li' solo FFmpeg.</summary>
    public bool Usable { get; private set; } = MpvEngine.Available && Environment.GetEnvironmentVariable("CINECORE_PREVIEW_FFMPEG") != "1";

    /// <summary>Il fotogramma chiave al tempo indicato, largo <paramref name="width"/>; null se non riesce.</summary>
    public Task<MenuAmbient.Frame?> GrabAsync(string file, double seconds, int width, ProbeData probe, CancellationToken token)
    {
        if (!Usable) return Task.FromResult<MenuAmbient.Frame?>(null);
        var job = new Job(file, seconds, width, probe, token, new TaskCompletionSource<MenuAmbient.Frame?>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_jobs)
        {
            if (_worker == null)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "Cinecore anteprima GPU", Priority = ThreadPriority.BelowNormal };
                _worker.Start();
            }
        }
        _jobs.Add(job);
        return job.Done.Task;
    }

    private void Run()
    {
        while (true)
        {
            // Due minuti senza richieste: il decoder si chiude e libera la memoria della scheda video.
            if (!_jobs.TryTake(out var job, TimeSpan.FromMinutes(2))) { Close(); continue; }
            if (job.Token.IsCancellationRequested) { job.Done.TrySetResult(null); continue; }
            MenuAmbient.Frame? frame = null;
            var clock = Stopwatch.StartNew();
            try { frame = Grab(job); }
            catch (Exception ex) { Console.Error.WriteLine("[anteprima GPU] " + ex.Message); Close(); }
            if (frame == null && !job.Token.IsCancellationRequested && ++_failures >= 6)
            {
                Usable = false;
                Console.Error.WriteLine("[anteprima GPU] troppi errori: torno a FFmpeg");
                Close();
            }
            else if (frame != null) _failures = 0;
            if (Qa) Console.Error.WriteLine($"[anteprima GPU] {job.Time:0.0} s: {(frame != null ? "ok" : "niente")} in {clock.ElapsedMilliseconds} ms");
            job.Done.TrySetResult(frame);
        }
    }

    private MenuAmbient.Frame? Grab(Job job)
    {
        var probe = job.Probe;
        if (probe is not { Width: > 0, Height: > 0 }) return null;
        int w = job.Width, h = Math.Max(2, (int)Math.Round((double)w * probe.Height / probe.Width / 2) * 2);
        // Come MenuAmbient.GrabFramesAsync: matrice e livelli dichiarati dal file, poi RGB pieno. setparams marca il
        // risultato come sRGB: senza, mpv convertiva di nuovo i colori di un file HDR prima di restituirli.
        string matrix = probe.ColorSpace switch
        {
            "bt2020nc" or "bt2020c" => "bt2020",
            "bt709" => "bt709",
            "fcc" => "fcc",
            "smpte240m" => "smpte240m",
            "bt470bg" or "smpte170m" => "bt601",
            _ => probe.Primaries == "bt2020" ? "bt2020" : probe.Width >= 1280 ? "bt709" : "bt601",
        };
        string range = probe.ColorRange == "pc" ? "pc" : "tv";
        string filter = $"lavfi=[scale=w={w}:h={h}:flags=bicubic:in_color_matrix={matrix}:in_range={range}:out_range=pc,format=bgra," +
                        "setparams=range=pc:color_primaries=bt709:color_trc=iec61966-2-1:colorspace=gbr]";

        if (_grabber == null || !string.Equals(_grabber.File, job.File, StringComparison.Ordinal) || _grabber.Filter != filter)
        {
            Close();
            var clock = Stopwatch.StartNew();
            _grabber = MpvFrameGrabber.Open(job.File, filter, fast: true);
            if (_grabber == null) return null;
            Console.Error.WriteLine($"[anteprima GPU] film aperto in {clock.ElapsedMilliseconds} ms ({_grabber.HwDecoder ?? "cpu"})");
        }
        if (job.Token.IsCancellationRequested) return null;

        if (!_grabber.Seek(job.Time, exact: false)) return null;
        var frame = _grabber.Screenshot(null, raw =>
        {
            if (raw.Width != w || raw.Height != h || raw.Format is not ("bgr0" or "bgra") || raw.Stride < w * 4 || raw.Size < raw.Stride * (h - 1) + w * 4)
            {
                Console.Error.WriteLine($"[anteprima GPU] fotogramma inatteso {raw.Width}x{raw.Height} {raw.Format}");
                return null;
            }
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Marshal.Copy(raw.Data + (int)(y * raw.Stride), px, y * w * 4, w * 4);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return new MenuAmbient.Frame(px, w, h);
        });
        if (frame == null) return null;
        bool hdr = probe.Transfer is "smpte2084" or "arib-std-b67", wide = probe.Primaries == "bt2020";
        if (hdr || wide) MenuAmbient.MapToSdr(frame.Bgra, frame.Width, frame.Height, probe.Transfer, wide);
        return frame;
    }

    private void Close() { _grabber?.Dispose(); _grabber = null; }
}
