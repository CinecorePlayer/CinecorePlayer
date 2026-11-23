using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// Bitrate in tempo reale, come il PacketRateSampler della 0.3.6: ffprobe legge i pacchetti di un flusso nei due
/// secondi che seguono la posizione di riproduzione e ne somma le dimensioni. Vale per i file, per YouTube (video e
/// audio sono due indirizzi separati) e per i flussi di rete (DLNA, Jellyfin). Si misura solo a pannello Info aperto.
/// </summary>
internal static class LiveBitrate
{
    public const double WindowSeconds = 2.0;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// kbps del flusso <paramref name="selector"/> ("v:0", "a:1"…) di <paramref name="source"/> tra
    /// <paramref name="position"/> e due secondi dopo. 0 se non si puo' misurare (flusso non ricercabile, errore, tempo scaduto).
    /// </summary>
    public static async Task<int> SampleAsync(string source, string selector, double position, CancellationToken ct)
    {
        if (MacTools.Ffprobe is not { } exe || string.IsNullOrEmpty(source)) return 0;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        double from = Math.Max(0, position);
        // Un po' di margine oltre la finestra: i pacchetti arrivano in ordine di decodifica, non di presentazione.
        // HLS non si sposta: chiedere la posizione voleva dire scaricare tutto dall'inizio fin li'. Bastano i primi secondi.
        bool hls = source.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || source.Contains("hls_playlist", StringComparison.OrdinalIgnoreCase)
            || source.Contains("/manifest/hls", StringComparison.OrdinalIgnoreCase);
        // Fine assoluta, non una durata: su WebM ffprobe atterra sul punto di ricerca precedente (anche 10 s prima) e
        // "+3" contava da li', senza arrivare alla finestra voluta.
        string interval = hls ? "%+" + (WindowSeconds + 1).ToString("0.###", Inv)
            : from.ToString("0.###", Inv) + "%" + (from + WindowSeconds + 1).ToString("0.###", Inv);
        foreach (string arg in new[]
        {
            "-v", "error", "-select_streams", selector, "-read_intervals", interval,
            "-show_entries", "format=start_time:packet=pts_time,size", "-of", "csv", source,
        })
            psi.ArgumentList.Add(arg);
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // In rete (YouTube 4K) il solo salto alla posizione costa ~15 s: la misura arriva piu' di rado, ma arriva.
            bool network = source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            timeout.CancelAfter(TimeSpan.FromSeconds(network ? 30 : 10));
            var errors = proc.StandardError.ReadToEndAsync(timeout.Token); // svuotato, se no ffprobe si blocca
            string output;
            try
            {
                output = await proc.StandardOutput.ReadToEndAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { }
                return 0;
            }
            _ = errors;
            return Kbps(output, from);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>Somma i pacchetti presentati nella finestra; le posizioni dell'app partono da zero, quelle del file da start_time.</summary>
    internal static int Kbps(string csv, double from)
    {
        double start = 0;
        var packets = new System.Collections.Generic.List<(double Pts, long Size)>();
        foreach (string raw in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cells = raw.Split(',');
            if (cells[0] == "format" && cells.Length >= 2 && double.TryParse(cells[1], NumberStyles.Float, Inv, out double s)) start = s;
            else if (cells[0] == "packet" && cells.Length >= 3
                && double.TryParse(cells[1], NumberStyles.Float, Inv, out double pts)
                && long.TryParse(cells[2], NumberStyles.Integer, Inv, out long size))
                packets.Add((pts, size));
        }
        double lo = from + start, hi = lo + WindowSeconds;
        long bytes = 0;
        int counted = 0;
        foreach (var (pts, size) in packets)
            if (pts >= lo && pts < hi) { bytes += size; counted++; }
        // HLS (YouTube, alcuni server): ffprobe non salta alla posizione e legge dall'inizio della playlist. Si
        // misurano i primi due secondi letti: pacchetti veri dello stesso flusso, non la media dichiarata.
        if (counted < 3 && packets.Count >= 3)
        {
            double first = packets.Min(p => p.Pts);
            bytes = 0; counted = 0;
            foreach (var (pts, size) in packets)
                if (pts >= first && pts < first + WindowSeconds) { bytes += size; counted++; }
        }
        // Troppo pochi pacchetti (fine del file, flusso che non si sposta): nessun numero piuttosto che uno sbagliato.
        if (counted < 3) return 0;
        return (int)Math.Round(bytes * 8.0 / 1000.0 / WindowSeconds);
    }
}
