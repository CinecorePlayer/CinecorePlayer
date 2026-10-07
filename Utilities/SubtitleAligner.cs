#nullable enable
using Cinecore.AudioVideoSync;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Riallinea un file di sottotitoli al film ascoltandone l'audio.
    ///
    /// Dall'audio (il canale centrale nei 5.1: il dialogo sta li') si ricava, ogni 10 ms, quanto
    /// il livello sta sopra la media della scena; dai sottotitoli, quando c'e' una battuta a
    /// schermo. Le due sequenze si fanno scorrere una sull'altra e si tiene lo spostamento in
    /// cui vanno piu' d'accordo (correlazione), provando anche le velocita' sbagliate tipiche:
    /// sottotitoli fatti per 25 fps su un film a 23,976/24 e viceversa, 24 contro 23,976.
    ///
    /// La versione precedente riduceva l'audio a "parla / non parla" con una soglia, su sei
    /// tratti da 45 secondi: con musica sotto i dialoghi sbagliava anche su sottotitoli gia'
    /// in sincrono. Misurato su film veri, il livello continuo su otto tratti da 90 secondi
    /// distingue il punto giusto con un margine doppio.
    /// </summary>
    internal static class SubtitleAligner
    {
        private const int Rate = 100;          // campioni al secondo delle due sequenze (10 ms)
        private const int Coarse = 5;          // la ricerca larga si fa a passi di 50 ms
        private const int AudioRate = 8000;
        private const double MaxOffsetSeconds = 90;

        public sealed record Result(bool Reliable, double OffsetSeconds, double Speed, double Score, double Margin, string Detail)
        {
            // Sotto un terzo di secondo non si tocca: i sottotitoli fatti bene entrano un attimo prima
            // della voce, e quello scarto non e' un errore da correggere.
            public bool Changed => Math.Abs(OffsetSeconds) >= 0.35 || Math.Abs(Speed - 1) > 1e-6;
            public double Map(double seconds) => seconds * Speed + OffsetSeconds;
        }

        private static readonly (double Speed, string Name)[] Speeds =
        {
            (1.0, "1:1"),
            (25.0 / 23.976, "25→23,976"), (23.976 / 25.0, "23,976→25"),
            (24.0 / 23.976, "24→23,976"), (23.976 / 24.0, "23,976→24"),
            (25.0 / 24.0, "25→24"), (24.0 / 25.0, "24→25")
        };

        /// <summary>Un tratto di film: per ogni passo, di quanti dB il livello sta sopra la media del tratto.</summary>
        private sealed record Window(int Start, float[] Fine, float[] Rough);

        public static async Task<Result> AlignAsync(string videoPath, IReadOnlyList<SubtitleFile.Cue> cues, IProgress<double>? progress, CancellationToken ct)
        {
            if (cues.Count < 8) return new Result(false, 0, 1, 0, 0, "few-cues");
            string ffmpeg = ExternalToolLocator.FindFfmpeg(null);

            MediaProbe.Result probe;
            try { probe = MediaProbe.Probe(videoPath); }
            catch { return new Result(false, 0, 1, 0, 0, "probe"); }
            double duration = probe.Duration;
            if (duration < 60) return new Result(false, 0, 1, 0, 0, "duration");
            bool hasCentre = probe.AudioChannels >= 6;

            // Otto tratti da 90 secondi sparsi nel film (dodici minuti di audio): lontani fra loro,
            // distinguono anche una velocita' diversa. Per avere l'audio di un remux 4K bisogna
            // leggere dal disco anche il video di quei tratti, quindi non tutto il film.
            int count = duration >= 1200 ? 8 : duration >= 300 ? 4 : 2;
            double windowSeconds = Math.Min(90, duration * 0.8 / count);
            var clock = Stopwatch.StartNew();
            var windows = new List<Window>();
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(i / (double)count * 0.85);
                double start = duration * (0.06 + 0.84 * i / Math.Max(1, count - 1)) - (i == count - 1 ? windowSeconds : 0);
                if (count == 1) start = duration * 0.1;
                short[]? pcm = await ExtractAsync(ffmpeg, videoPath, start, windowSeconds, hasCentre, ct).ConfigureAwait(false);
                if (pcm == null && hasCentre) pcm = await ExtractAsync(ffmpeg, videoPath, start, windowSeconds, false, ct).ConfigureAwait(false);
                if (pcm == null || pcm.Length < AudioRate * 20) continue;
                if (Levels(start, pcm) is { } window) windows.Add(window);
            }
            if (windows.Count == 0) return new Result(false, 0, 1, 0, 0, "no-audio");
            progress?.Report(0.87);
            double extractSeconds = clock.Elapsed.TotalSeconds;

            int maxLag = (int)(MaxOffsetSeconds * Rate / Coarse);
            var tried = new List<(double Speed, string Name, double Score, double Offset, double Margin, double Z, int[] Prefix)>();
            foreach (var (speed, name) in Speeds)
            {
                ct.ThrowIfCancellationRequested();
                int[] prefix = BuildPrefix(cues, speed, duration + MaxOffsetSeconds + 10);
                double[] scores = RoughScores(windows, prefix, maxLag);
                int peak = 0;
                for (int i = 1; i < scores.Length; i++) if (scores[i] > scores[peak]) peak = i;
                // Il resto della curva, lontano dal picco (oltre 1,5 s): dice quanto il picco spicca.
                double second = double.MinValue, sum = 0, squares = 0;
                int others = 0;
                for (int i = 0; i < scores.Length; i++)
                {
                    if (Math.Abs(i - peak) <= 1.5 * Rate / Coarse) continue;
                    if (scores[i] > second) second = scores[i];
                    sum += scores[i]; squares += scores[i] * scores[i]; others++;
                }
                double mean = others > 0 ? sum / others : 0, deviation = others > 1 ? Math.Sqrt(Math.Max(1e-12, squares / others - mean * mean)) : 1;
                tried.Add((speed, name, scores[peak], (peak - maxLag) * (double)Coarse / Rate, second > 1e-6 ? scores[peak] / second : 9, (scores[peak] - mean) / deviation, prefix));
            }
            Dbg.Log("[SUBS] align: " + string.Join(" · ", tried.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Name} {entry.Score:0.000} at {entry.Offset:+0.00;-0.00}s z{entry.Z:0.0}"))) +
                    " · windows at " + string.Join(",", windows.Select(window => (window.Start / Rate).ToString(CultureInfo.InvariantCulture))), Dbg.LogLevel.Info);
            var winner = tried.OrderByDescending(entry => entry.Score).First();
            // Velocita' quasi uguali (24 contro 23,976: lo 0,1%) su un video corto danno lo stesso
            // risultato: a parita' si tiene quella piu' vicina a "nessuna correzione".
            foreach (var entry in tried.Where(entry => Math.Abs(entry.Speed / winner.Speed - 1) < 0.002 && entry.Score >= winner.Score * 0.97).OrderBy(entry => Math.Abs(entry.Speed - 1)).Take(1))
                winner = entry;
            double best = winner.Score, bestOffset = winner.Offset, bestSpeed = winner.Speed, bestMargin = winner.Margin, bestZ = winner.Z;
            string bestName = winner.Name;
            int[]? bestPrefix = winner.Prefix;
            // Quanto la velocita' scelta batte le altre famiglie (quelle che differiscono di oltre l'1%)
            // e, dentro la sua famiglia, le vicine.
            double otherFamilies = tried.Where(entry => Math.Abs(entry.Speed / bestSpeed - 1) >= 0.002).Select(entry => entry.Score).DefaultIfEmpty(0).Max();
            double runnerUp = otherFamilies;
            progress?.Report(0.96);

            // Dal passo di 50 ms a quello di 10 ms, attorno al punto trovato.
            if (bestPrefix != null && best > 0)
            {
                int centre = (int)Math.Round(bestOffset * Rate), fineBest = centre;
                double fineScore = double.MinValue;
                for (int lag = centre - Coarse * 2; lag <= centre + Coarse * 2; lag++)
                {
                    double score = FineScore(windows, bestPrefix, lag);
                    if (score > fineScore) { fineScore = score; fineBest = lag; }
                }
                bestOffset = fineBest / (double)Rate;
            }
            progress?.Report(1);

            // Affidabile: il punto trovato stacca nettamente tutto il resto (misurato in deviazioni
            // dalla media della curva e rispetto al secondo miglior punto) e la velocita' scelta
            // batte le altre. Sotto queste soglie e' meglio non toccare il file.
            double speedLead = runnerUp > 1e-6 ? best / runnerUp : 9;
            bool reliable = best >= 0.03 && bestZ >= 4.5 && bestMargin >= 1.3 && speedLead >= 1.12;
            string detail = string.Format(CultureInfo.InvariantCulture, "speed {0}, score {1:0.000}, z {2:0.0}, margin {3:0.00}, speed lead {4:0.00}, windows {5}, audio {6:0.0}s, match {7:0.0}s",
                bestName, best, bestZ, bestMargin, speedLead, windows.Count, extractSeconds, clock.Elapsed.TotalSeconds - extractSeconds);
            return new Result(reliable, bestOffset, bestSpeed, best, bestMargin, detail);
        }

        public static List<SubtitleFile.Cue> Apply(IReadOnlyList<SubtitleFile.Cue> cues, Result result) =>
            cues.Select(cue => cue with { Start = result.Map(cue.Start), End = result.Map(cue.End) }).ToList();

        // Presenza di una battuta a schermo, in somma cumulata: quanti passi da 10 ms con un
        // sottotitolo ci sono prima di ogni istante.
        private static int[] BuildPrefix(IReadOnlyList<SubtitleFile.Cue> cues, double speed, double totalSeconds)
        {
            int length = (int)(totalSeconds * Rate) + 2;
            var active = new bool[length];
            foreach (var cue in cues)
            {
                int from = Math.Clamp((int)Math.Round(cue.Start * speed * Rate), 0, length - 1);
                int to = Math.Clamp((int)Math.Round(cue.End * speed * Rate), 0, length - 1);
                for (int i = from; i < to; i++) active[i] = true;
            }
            var prefix = new int[length + 1];
            for (int i = 0; i < length; i++) prefix[i + 1] = prefix[i] + (active[i] ? 1 : 0);
            return prefix;
        }

        private static int At(int[] prefix, int index) => prefix[Math.Clamp(index, 0, prefix.Length - 1)];

        /// <summary>
        /// Correlazione fra livello dell'audio e battute spostate, per ogni spostamento da -90 a
        /// +90 secondi a passi di 50 ms (0 = nessun legame, 1 = coincidenza perfetta).
        /// </summary>
        private static double[] RoughScores(List<Window> windows, int[] prefix, int maxLag)
        {
            // Battute a passi di 50 ms: quota di ogni passo coperta da un sottotitolo, con le
            // somme cumulate (e dei quadrati) per normalizzare ogni tratto senza ripercorrerlo.
            int length = (prefix.Length - 1) / Coarse;
            var cue = new float[length];
            var sums = new double[length + 1];
            var squares = new double[length + 1];
            for (int i = 0; i < length; i++)
            {
                cue[i] = (prefix[(i + 1) * Coarse] - prefix[i * Coarse]) / (float)Coarse;
                sums[i + 1] = sums[i] + cue[i];
                squares[i + 1] = squares[i] + cue[i] * cue[i];
            }
            double audioEnergy = windows.Sum(window => window.Rough.Sum(value => (double)value * value));
            double density = length > 0 ? sums[length] / length : 0;
            double overall = windows.Sum(window => window.Rough.Length) * density * (1 - density);
            var scores = new double[2 * maxLag + 1];
            Parallel.For(0, scores.Length, index =>
            {
                int lag = index - maxLag;
                double dot = 0, cueEnergy = 0;
                foreach (var window in windows)
                {
                    float[] level = window.Rough;
                    int origin = window.Start / Coarse - lag;
                    int from = Math.Max(0, -origin), to = Math.Min(level.Length, length - origin);
                    if (to - from < level.Length / 2) continue;
                    float partial = 0;
                    for (int i = from; i < to; i++) partial += level[i] * cue[origin + i];
                    dot += partial;
                    double s = sums[origin + to] - sums[origin + from], q = squares[origin + to] - squares[origin + from];
                    cueEnergy += q - s * s / (to - from);
                }
                // Normalizzata con la densita' media delle battute in tutto il film, non con quella dei
                // soli tratti a quello spostamento: dove cadono poche battute il rapporto si gonfiava
                // e uno spostamento sbagliato poteva battere quello giusto.
                scores[index] = overall > 1e-6 && audioEnergy > 1e-6 ? dot / Math.Sqrt(audioEnergy * overall) : 0;
            });
            return scores;
        }

        private static double FineScore(List<Window> windows, int[] prefix, int lag)
        {
            double dot = 0;
            foreach (var window in windows)
            {
                float[] level = window.Fine;
                int origin = window.Start - lag;
                for (int i = 0; i < level.Length; i++)
                    if (At(prefix, origin + i + 1) - At(prefix, origin + i) > 0) dot += level[i];
            }
            return dot;
        }

        // Livello per passi da 10 ms (energia della derivata: toglie il rimbombo sotto la voce),
        // riferito alla media del tratto e limitato, cosi' un'esplosione non pesa piu' di una frase.
        private static Window? Levels(double startSeconds, short[] pcm)
        {
            int frame = AudioRate / Rate;
            int count = pcm.Length / frame;
            if (count < Rate * 20) return null;
            var level = new float[count];
            double mean = 0;
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                double sum = 0;
                int offset = i * frame;
                for (int k = 0; k < frame; k++) { int sample = pcm[offset + k]; double d = sample - previous; previous = sample; sum += d * d; }
                level[i] = (float)(10 * Math.Log10(sum / frame + 1));
                mean += level[i];
            }
            mean /= count;
            // Si toglie l'andamento lento (media mobile di 5 secondi): quanto e' forte la scena non
            // dice nulla sulle battute, e lasciato dentro creava false coincidenze larghe decine di
            // secondi che sui film con molta musica battevano quella vera.
            const int span = 5 * Rate;
            var running = new double[count + 1];
            for (int i = 0; i < count; i++) running[i + 1] = running[i] + level[i];
            double centre = 0;
            for (int i = 0; i < count; i++)
            {
                int from = Math.Max(0, i - span / 2), to = Math.Min(count, i + span / 2);
                double local = (running[to] - running[from]) / Math.Max(1, to - from);
                level[i] = (float)Math.Clamp(level[i] - local, -12, 18);
                centre += level[i];
            }
            centre /= count;
            double energy = 0;
            for (int i = 0; i < count; i++) { level[i] -= (float)centre; energy += level[i] * level[i]; }
            if (energy / count < 1) return null; // tratto piatto: silenzio o rumore costante
            var rough = new float[count / Coarse];
            for (int i = 0; i < rough.Length; i++)
            {
                float sum = 0;
                for (int k = 0; k < Coarse; k++) sum += level[i * Coarse + k];
                rough[i] = sum / Coarse;
            }
            return new Window((int)Math.Round(startSeconds * Rate), level, rough);
        }

        private static async Task<short[]?> ExtractAsync(string ffmpeg, string input, double start, double seconds, bool centre, CancellationToken ct)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", input,
                "-map", "0:a:0", "-vn", "-sn", "-dn",
                "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture)
            };
            // Nei multicanale solo il centrale, dove sta il dialogo.
            if (centre) { arguments.Add("-af"); arguments.Add("pan=mono|c0=FC"); }
            arguments.AddRange(new[] { "-ac", "1", "-ar", AudioRate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1" });
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            try { if (!process.Start()) return null; }
            catch { return null; }
            using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
            using var bytes = new MemoryStream();
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(bytes, ct);
            Task<string> errors = process.StandardError.ReadToEndAsync(ct);
            try { await Task.WhenAll(copy, process.WaitForExitAsync(ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) return null;
            byte[] raw = bytes.ToArray();
            var samples = new short[raw.Length / 2];
            Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 2);
            return samples;
        }
    }
}
