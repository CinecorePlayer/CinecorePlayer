using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace CinecoreMac;

/// <summary>
/// Secondo canale audio per i grafici (BitstreamPcmAnalysisSampler di Windows): quando l'uscita non e' PCM prodotto da
/// noi (bitstream verso il ricevitore: il suono compresso passa intatto e non si puo' misurare), un secondo FFmpeg
/// decodifica lo stesso brano in PCM stereo, a tempo con la riproduzione, e lo manda all'analisi. Segue da solo il
/// brano che si sente, i salti e le pause; non tocca mai l'uscita audio.
/// </summary>
internal sealed class ParallelPcmAnalysis : IDisposable
{
    public const int Rate = 48000, Channels = 2;
    private const int ChunkFrames = 512;               // ~11 ms: stessa cadenza dell'analisi del motore

    private readonly Func<(string? Path, double Position, bool Playing)> _state;
    private readonly Action<float[], int, int, int> _push;
    private readonly Action _reset;
    private Thread? _thread;
    private volatile bool _stop;
    private Process? _process;

    /// <param name="state">Brano che si sente, posizione in secondi, se sta suonando.</param>
    /// <param name="push">Campioni (interleaved, numero, frequenza, canali) verso l'analisi.</param>
    /// <param name="reset">Brano cambiato o salto: l'analisi riparte (loudness, VU).</param>
    public ParallelPcmAnalysis(Func<(string? Path, double Position, bool Playing)> state, Action<float[], int, int, int> push, Action reset)
    {
        _state = state; _push = push; _reset = reset;
    }

    public bool Running => _thread != null;

    public void Start()
    {
        if (_thread != null || MacTools.Ffmpeg == null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "musica analisi parallela", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        Kill();
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Kill()
    {
        var p = Interlocked.Exchange(ref _process, null);
        try { if (p is { HasExited: false }) p.Kill(); } catch { }
        try { p?.Dispose(); } catch { }
    }

    private void Run()
    {
        while (!_stop)
        {
            var (path, position, playing) = _state();
            if (path == null || !playing) { Thread.Sleep(100); continue; }
            try { Decode(path, position); }
            catch (Exception ex) { Console.Error.WriteLine("[musica] analisi parallela: " + ex.Message); Thread.Sleep(300); }
        }
        Kill();
    }

    /// <summary>Decodifica da <paramref name="start"/> e consegna ogni pezzo quando si sente; esce a cambio di brano,
    /// salto (scarto oltre 0,4 s), pausa o fine.</summary>
    private void Decode(string path, double start)
    {
        string input = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? CinecorePlayer2025.Utilities.JellyfinClient.PlaybackUrl(path) : path;
        var psi = new ProcessStartInfo(MacTools.Ffmpeg!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false, CreateNoWindow = true };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "2", "-ar", Rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1" })
            psi.ArgumentList.Add(a);
        var process = Process.Start(psi);
        if (process == null) return;
        _process = process;
        _reset();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var stream = process.StandardOutput.BaseStream;
        var bytes = new byte[ChunkFrames * Channels * 4];
        long delivered = 0;
        while (!_stop)
        {
            int got = 0;
            while (got < bytes.Length)
            {
                int n = stream.Read(bytes, got, bytes.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < bytes.Length) break; // fine del brano
            double chunkEnd = start + (delivered + ChunkFrames) / (double)Rate;
            // Si consegna quando il pezzo si sente: si aspetta la riproduzione (al massimo 0,6 s per giro).
            while (!_stop)
            {
                var (now, position, playing) = _state();
                if (now != path || !playing) { Kill(); return; }
                double ahead = chunkEnd - position;
                if (ahead < -0.4 || ahead > 1.5) { Kill(); return; } // salto: si riparte dal punto nuovo
                if (ahead <= 0.003) break;
                Thread.Sleep((int)Math.Clamp(ahead * 1000, 1, 15));
            }
            var samples = new float[ChunkFrames * Channels];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            _push(samples, samples.Length, Rate, Channels);
            delivered += ChunkFrames;
        }
        Kill();
        // Brano finito: si aspetta il successivo senza girare a vuoto.
        while (!_stop && _state().Path == path) Thread.Sleep(80);
    }
}
