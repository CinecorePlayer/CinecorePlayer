#nullable enable
using CinecorePlayer2025.Utilities;
using System;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// [NOI] Controllo del passaggio sul segnale vero: confronta i colpi di cassa (sotto ~130 Hz) dei due brani nel tratto
    /// in cui suonano insieme e dice di quanto spostare l'ingresso del secondo perche' le casse cadano insieme. La griglia
    /// stimata dall'analizzatore a volte sbaglia la fase di mezzo battito (Filthy Pop -> Glitter And Grease: -258 ms a
    /// 106 BPM): le due casse si alternavano e il passaggio sembrava "scollato".
    /// </summary>
    internal static class AutomixPhaseAligner
    {
        private const int Rate = 11025;
        private const double Hop = 0.005; // risoluzione della misura: 5 ms

        /// <summary>
        /// Secondi da aggiungere al punto d'ingresso del brano che entra (positivo = partire piu' avanti), oppure null se
        /// la misura non e' affidabile. <paramref name="syncT0"/>/<paramref name="s0"/>: istante del brano che esce e
        /// secondo del brano che entra che coincidono; <paramref name="ratio"/>: velocita' del brano che entra.
        /// </summary>
        public static double? MeasureLag(string fromPath, string toPath, double syncT0, double s0, double period, double ratio = 1, double seconds = 10)
        {
            if (period <= 0.2 || period > 1.5 || !double.IsFinite(syncT0) || !double.IsFinite(s0)) return null;
            try
            {
                // Qualche secondo prima del punto: la cassa di A di solito e' ancora piena, quella di B parte da S0.
                double lead = Math.Min(2, Math.Min(syncT0, s0));
                var a = Onsets(fromPath, syncT0 - lead, seconds, 1);
                var b = Onsets(toPath, s0 - lead, seconds * ratio, ratio);
                int n = Math.Min(a.Length, b.Length);
                if (n < 200) return null;
                int maxLag = (int)Math.Round(period * 0.55 / Hop);
                double best = double.NegativeInfinity, sum = 0; int bestLag = 0, count = 0;
                var scores = new double[maxLag * 2 + 1];
                for (int lag = -maxLag; lag <= maxLag; lag++)
                {
                    double acc = 0; int used = 0;
                    for (int k = 0; k < n; k++)
                    {
                        int j = k + lag;
                        if (j < 0 || j >= n) continue;
                        acc += a[k] * b[j]; used++;
                    }
                    double score = used > 0 ? acc / used : 0;
                    scores[lag + maxLag] = score;
                    sum += score; count++;
                    if (score > best) { best = score; bestLag = lag; }
                }
                double mean = sum / Math.Max(1, count);
                // Il picco deve staccarsi bene dalla media, e dal valore a mezzo battito di distanza (l'errore che cerchiamo).
                int halfAway = bestLag + (bestLag <= 0 ? 1 : -1) * (int)Math.Round(period * 0.5 / Hop);
                double opposite = halfAway >= -maxLag && halfAway <= maxLag ? scores[halfAway + maxLag] : mean;
                if (best <= 0 || best < mean * 1.25 || best < opposite * 1.15)
                {
                    Dbg.Log($"[AUTOMIX] phase check: unreliable (peak {best:0.000}, mean {mean:0.000}, half-beat {opposite:0.000})", Dbg.LogLevel.Info);
                    return null;
                }
                double lagSeconds = bestLag * Hop;
                Dbg.Log($"[AUTOMIX] phase check: kick lag {lagSeconds * 1000:0} ms (peak {best:0.000}, mean {mean:0.000}, half-beat {opposite:0.000})", Dbg.LogLevel.Info);
                return lagSeconds;
            }
            catch (Exception ex) { Dbg.Warn("[AUTOMIX] phase check: " + ex.Message); return null; }
        }

        /// <summary>Inviluppo degli attacchi della cassa, un valore ogni 5 ms, nel tempo del brano che esce.</summary>
        private static float[] Onsets(string path, double from, double seconds, double ratio)
        {
            using var decoder = new CinecoreAudioDecoder(path, Rate, 1);
            if (from > 0.05) decoder.Seek(from);
            long total = (long)(seconds * Rate);
            var signal = new float[total];
            long read = 0;
            var buffer = new float[4096];
            while (read < total)
            {
                int got = decoder.Read(buffer, out _);
                if (got <= 0) break;
                int copy = (int)Math.Min(got, total - read);
                Array.Copy(buffer, 0, signal, read, copy);
                read += copy;
            }
            // Passa-basso a due poli (~130 Hz), raddrizzato e lisciato: rimane la cassa.
            double k = 1 - Math.Exp(-2 * Math.PI * 130.0 / Rate), smooth = 1 - Math.Exp(-1.0 / (0.008 * Rate));
            double l1 = 0, l2 = 0, env = 0;
            int hop = (int)Math.Round(Hop * Rate);
            int frames = (int)(read / hop);
            var level = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                double peak = 0;
                for (int i = f * hop; i < (f + 1) * hop; i++)
                {
                    l1 += (signal[i] - l1) * k; l2 += (l1 - l2) * k;
                    env += (Math.Abs(l2) - env) * smooth;
                    if (env > peak) peak = env;
                }
                level[f] = (float)peak;
            }
            // Solo le salite (gli attacchi), e nel tempo del brano che esce quando quello che entra va a un'altra velocita'.
            int outFrames = (int)(frames / Math.Max(0.5, ratio));
            var onsets = new float[Math.Max(0, outFrames)];
            for (int f = 1; f < outFrames; f++)
            {
                int src = (int)Math.Round(f * ratio);
                if (src <= 0 || src >= frames) break;
                onsets[f] = Math.Max(0, level[src] - level[src - 1]);
            }
            return onsets;
        }
    }
}
