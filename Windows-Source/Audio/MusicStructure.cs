#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// Struttura musicale di un tratto di brano, a partire dalla griglia dei battiti: quale battito e' il "uno" della
    /// battuta (dove picchiano cassa e bassi), dove cominciano le frasi (ogni 8 battute, piu' forti dove l'energia cambia),
    /// quanto c'e' voce secondo per secondo e quanto e' forte. Tutto con un'affidabilita': il planner non prende decisioni
    /// rischiose su stime incerte.
    /// </summary>
    internal static class MusicStructure
    {
        // Unmetered introductions can have a clear entrance without a reliable beat grid.
        // Find a sustained entrance after a quiet passage, and retain a little attack context.
        // These are energy boundaries, kept separate from the metrical phrase guesses.
        internal static double[] EntryCues(IReadOnlyList<double> levels, double step, double bodyDb)
        {
            var cues = new List<double>();
            double Mean(double from, double seconds)
            {
                int a = Math.Max(0, (int)(from / step)), b = Math.Min(levels.Count, (int)((from + seconds) / step));
                double power = 0;
                for (int i = a; i < b; i++) power += Math.Pow(10, levels[i] / 10);
                return b > a ? 10 * Math.Log10(Math.Max(1e-12, power / (b - a))) : -120;
            }
            for (double t = 2; t <= Math.Min(32, levels.Count * step - 5); t += step)
            {
                double before = Mean(t - 2, 2), after = Mean(t, 1.5), sustained = Mean(t + 1.5, 3);
                if (before > bodyDb - 9 || after - before < 5 || after < bodyDb - 10 || sustained < bodyDb - 10) continue;
                double cue = Math.Max(0, t - 0.15);
                if (cues.Count == 0 || cue - cues[^1] > 4) cues.Add(Math.Round(cue, 3));
            }
            return cues.ToArray();
        }

        /// <summary>Il primo "uno" (battito forte) della griglia e quanto e' netto (1 = indistinto, >1.3 = chiaro).</summary>
        public static (double Downbeat, double Confidence) Downbeat(float[] samples, int rate, double startSeconds, BeatGrid grid)
        {
            if (!grid.Reliable || samples.Length < rate * 4) return (grid.FirstBeat, 0);
            // Inviluppo dei bassi (passa-basso a un polo, ~150 Hz): la cassa e il basso segnano il "uno".
            double a = Math.Exp(-2 * Math.PI * 150.0 / rate), y = 0;
            var low = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++) { y = (1 - a) * samples[i] + a * y; low[i] = (float)Math.Abs(y); }
            double period = grid.Period;
            var accents = new double[4];
            var counts = new int[4];
            int beat = 0;
            for (double t = grid.FirstBeat; t - startSeconds < samples.Length / (double)rate - 0.1; t += period, beat++)
            {
                int i0 = (int)((t - startSeconds) * rate), i1 = Math.Min(low.Length, i0 + (int)(0.08 * rate));
                if (i0 < 0) continue;
                double sum = 0;
                for (int i = i0; i < i1; i++) sum += low[i];
                accents[beat % 4] += sum; counts[beat % 4]++;
            }
            for (int p = 0; p < 4; p++) accents[p] = counts[p] > 0 ? accents[p] / counts[p] : 0;
            int best = Array.IndexOf(accents, accents.Max());
            double mean = accents.Average();
            double confidence = mean > 0 ? accents[best] / mean : 0;
            return (grid.FirstBeat + best * period, Math.Round(confidence, 2));
        }

        /// <summary>
        /// Inizi delle frasi (ogni 8 battute dal "uno"), con un punteggio: piu' alto dove l'energia della battuta cambia
        /// di piu' (entrata di un elemento, break, drop). Tempi assoluti nel brano.
        /// </summary>
        public static List<(double Time, double Strength)> Phrases(float[] samples, int rate, double startSeconds, double downbeat, BeatGrid grid)
        {
            var result = new List<(double, double)>();
            if (!grid.Reliable) return result;
            double bar = grid.Period * 4, end = startSeconds + samples.Length / (double)rate;
            // Energia per battuta (dB).
            var bars = new List<(double Time, double Db)>();
            double first = downbeat;
            while (first - bar >= startSeconds) first -= bar;
            for (double t = first; t + bar <= end; t += bar)
            {
                int i0 = (int)((t - startSeconds) * rate), i1 = (int)((t + bar - startSeconds) * rate);
                if (i0 < 0) continue;
                double sum = 0;
                for (int i = i0; i < i1 && i < samples.Length; i++) sum += samples[i] * (double)samples[i];
                bars.Add((t, 10 * Math.Log10(sum / Math.Max(1, i1 - i0) + 1e-12)));
            }
            if (bars.Count < 2) return result;
            for (int k = 0; k < bars.Count; k++)
            {
                double change = k == 0 ? 0 : Math.Abs(bars[k].Db - bars[k - 1].Db);
                int fromDownbeat = (int)Math.Round((bars[k].Time - downbeat) / bar);
                bool phraseGrid = ((fromDownbeat % 8) + 8) % 8 == 0;
                bool halfPhrase = ((fromDownbeat % 4) + 4) % 4 == 0;
                double strength = (phraseGrid ? 0.6 : halfPhrase ? 0.3 : 0) + Math.Min(0.6, change / 6.0);
                if (strength >= 0.3) result.Add((Math.Round(bars[k].Time, 3), Math.Round(Math.Min(1, strength), 2)));
            }
            return result;
        }

        /// <summary>Livello per secondo (dB RMS), a partire da <paramref name="startSeconds"/>.</summary>
        public static int[] LevelsPerSecond(float[] samples, int rate)
        {
            int seconds = samples.Length / rate;
            var levels = new int[seconds];
            for (int s = 0; s < seconds; s++)
            {
                double sum = 0;
                for (int i = s * rate; i < (s + 1) * rate; i++) sum += samples[i] * (double)samples[i];
                levels[s] = (int)Math.Round(Math.Max(-90, 10 * Math.Log10(sum / rate + 1e-12)));
            }
            return levels;
        }

        /// <summary>
        /// Probabilita' di voce per secondo (0-100). Indizi: energia concentrata nella banda della voce (300-3400 Hz),
        /// periodicita' nella gamma dell'intonazione cantata (90-500 Hz) e modulazione sillabica (3-8 Hz) di quella banda.
        /// E' una stima: serve a evitare due voci sovrapposte, non a decidere da sola.
        /// </summary>
        public static byte[] VoicePerSecond(float[] samples, int rate)
        {
            int seconds = samples.Length / rate;
            var result = new byte[seconds];
            const int Fft = 2048;
            var window = new double[Fft];
            for (int i = 0; i < Fft; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Fft - 1));
            var re = new double[Fft]; var im = new double[Fft];
            int lo = (int)(300.0 * Fft / rate), hi = (int)(3400.0 * Fft / rate);
            int minLag = rate / 500, maxLag = rate / 90;
            var band = new float[Fft];
            for (int s = 0; s < seconds; s++)
            {
                double ratioSum = 0, periodicSum = 0, peakSum = 0; int frames = 0;
                var envelope = new List<double>();
                for (int off = s * rate; off + Fft <= (s + 1) * rate && off + Fft <= samples.Length; off += Fft / 2)
                {
                    for (int i = 0; i < Fft; i++) { re[i] = samples[off + i] * window[i]; im[i] = 0; }
                    BeatTracker.Transform(re, im);
                    double total = 1e-12, mid = 0;
                    for (int k = 1; k < Fft / 2; k++)
                    {
                        double e = re[k] * re[k] + im[k] * im[k];
                        total += e; if (k >= lo && k <= hi) mid += e;
                    }
                    ratioSum += mid / total;
                    envelope.Add(mid);
                    // Quanto della banda sta in un solo picco: una nota pura (sintetizzatore, click) si', la voce no (armoniche).
                    int top = lo; double topE = 0;
                    for (int k = lo; k <= hi; k++) { double e = re[k] * re[k] + im[k] * im[k]; if (e > topE) { topE = e; top = k; } }
                    double peak = 0;
                    for (int k = Math.Max(lo, top - 2); k <= Math.Min(hi, top + 2); k++) peak += re[k] * re[k] + im[k] * im[k];
                    peakSum += peak / Math.Max(1e-12, mid);
                    // Banda della voce ricostruita (filtro grezzo nel dominio della frequenza) e sua periodicita'.
                    for (int k = 0; k < Fft; k++)
                    {
                        int kk = k <= Fft / 2 ? k : Fft - k;
                        if (kk < lo || kk > hi) { re[k] = 0; im[k] = 0; }
                    }
                    for (int k = 0; k < Fft; k++) im[k] = -im[k];
                    BeatTracker.Transform(re, im);
                    for (int i = 0; i < Fft; i++) band[i] = (float)(re[i] / Fft);
                    double energy = 0;
                    for (int i = 0; i < Fft - maxLag; i++) energy += band[i] * band[i];
                    double bestCorr = 0;
                    for (int lag = minLag; lag <= maxLag; lag += 2)
                    {
                        double c = 0;
                        for (int i = 0; i < Fft - maxLag; i += 2) c += band[i] * band[i + lag];
                        bestCorr = Math.Max(bestCorr, c / Math.Max(1e-12, energy / 2));
                    }
                    periodicSum += Math.Clamp(bestCorr, 0, 1);
                    frames++;
                }
                if (frames == 0) continue;
                double ratio = ratioSum / frames, periodic = periodicSum / frames, single = peakSum / frames;
                // Modulazione: quanto varia la banda della voce da una finestra all'altra (le sillabe la fanno variare).
                double modulation = 0;
                if (envelope.Count > 2)
                {
                    double mean = envelope.Average();
                    modulation = Math.Sqrt(envelope.Average(v => (v - mean) * (v - mean))) / Math.Max(1e-12, mean);
                }
                double p = Math.Clamp((ratio - 0.35) / 0.4, 0, 1) * 0.4 + Math.Clamp((periodic - 0.35) / 0.4, 0, 1) * 0.4 + Math.Clamp(modulation / 0.8, 0, 1) * 0.2;
                p *= Math.Clamp((0.85 - single) / 0.4, 0, 1);
                result[s] = (byte)Math.Round(100 * Math.Clamp(p, 0, 1));
            }
            return result;
        }

        /// <summary>
        /// Probabilita' di voce per secondo (0-100) da un tratto stereo. Indizi, ciascuno rispetto al resto del brano:
        /// quanto la banda della voce (250 Hz - 4 kHz) sta al centro dello stereo, quanto pesa sul totale e quanto varia,
        /// l'armonicita' (picchi sulle armoniche di una nota fra 110 e 700 Hz), le sillabe (attacchi nella banda) e il
        /// livello. Pesi stimati su 17 brani con testo sincronizzato (un brano escluso alla volta): coglie l'81% dei
        /// secondi cantati, contro il 52% della stima precedente su segnale mono.
        /// </summary>
        public static byte[] VoicePerSecondStereo(float[] left, float[] right, int rate)
        {
            int seconds = Math.Min(left.Length, right.Length) / rate;
            var result = new byte[seconds];
            if (seconds == 0) return result;
            const int Fft = 2048;
            var window = new double[Fft];
            for (int i = 0; i < Fft; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Fft - 1));
            var mr = new double[Fft]; var mi = new double[Fft]; var sr = new double[Fft]; var si = new double[Fft];
            int lo = (int)(250.0 * Fft / rate), hi = (int)(4000.0 * Fft / rate);
            var raw = new double[seconds, 7]; // centro, banda, modulazione, armonicita', sua variazione, attacchi, livello
            var previous = new double[hi - lo];
            var spectrum = new double[hi];
            for (int s = 0; s < seconds; s++)
            {
                double sumMid = 0, sumSide = 0, sumAll = 0, flux = 0; int frames = 0, fluxFrames = 0;
                var bandFrames = new List<double>(); var harmonic = new List<double>();
                for (int off = s * rate; off + Fft <= (s + 1) * rate; off += Fft / 2)
                {
                    for (int i = 0; i < Fft; i++)
                    {
                        double l = left[off + i], r = right[off + i];
                        mr[i] = (l + r) * 0.5 * window[i]; mi[i] = 0;
                        sr[i] = (l - r) * 0.5 * window[i]; si[i] = 0;
                    }
                    BeatTracker.Transform(mr, mi); BeatTracker.Transform(sr, si);
                    double band = 0, side = 0, all = 0, f = 0;
                    for (int k = 1; k < Fft / 2; k++)
                    {
                        double em = mr[k] * mr[k] + mi[k] * mi[k];
                        all += em;
                        if (k < hi) spectrum[k] = Math.Sqrt(em);
                        if (k >= lo && k < hi)
                        {
                            band += em; side += sr[k] * sr[k] + si[k] * si[k];
                            double mag = Math.Log(1 + 100 * Math.Sqrt(em));
                            if (frames > 0) f += Math.Max(0, mag - previous[k - lo]);
                            previous[k - lo] = mag;
                        }
                    }
                    if (frames > 0) { flux += f / (hi - lo); fluxFrames++; }
                    sumMid += band; sumSide += side; sumAll += all; bandFrames.Add(band);
                    // Armonicita': la nota (110-700 Hz) le cui prime sette armoniche raccolgono piu' energia, rispetto alla media.
                    double mean = 1e-9; for (int k = 0; k < hi; k++) mean += spectrum[k]; mean /= hi;
                    double best = 0;
                    for (double f0 = 110; f0 < 700; f0 += 7)
                    {
                        double sum = 0; int n = 0;
                        for (int h = 1; h <= 7; h++) { int bin = (int)(h * f0 * Fft / rate); if (bin < hi) { sum += spectrum[bin]; n++; } }
                        if (n > 0) best = Math.Max(best, sum / n);
                    }
                    harmonic.Add(best / mean);
                    frames++;
                }
                if (frames == 0) continue;
                double bandMean = bandFrames.Average();
                raw[s, 0] = sumMid / (sumMid + sumSide + 1e-12);
                raw[s, 1] = sumMid / (sumAll + 1e-12);
                raw[s, 2] = Math.Sqrt(bandFrames.Average(v => (v - bandMean) * (v - bandMean))) / (bandMean + 1e-12);
                double hMean = harmonic.Average();
                raw[s, 3] = hMean;
                raw[s, 4] = Math.Sqrt(harmonic.Average(v => (v - hMean) * (v - hMean)));
                raw[s, 5] = fluxFrames > 0 ? flux / fluxFrames : 0;
                raw[s, 6] = 10 * Math.Log10(sumAll / frames + 1e-12);
            }
            // Ogni indizio rispetto alla mediana del tratto (missaggi e volumi diversi da brano a brano).
            var median = new double[7];
            for (int c = 0; c < 7; c++)
            {
                var column = new double[seconds];
                for (int s = 0; s < seconds; s++) column[s] = raw[s, c];
                Array.Sort(column);
                median[c] = column[seconds / 2];
            }
            double Smoothed(int c, int s)
            {
                double sum = 0; int n = 0;
                for (int k = Math.Max(0, s - 1); k <= Math.Min(seconds - 1, s + 1); k++) { sum += raw[k, c] - median[c]; n++; }
                return sum / n;
            }
            // Media, scarto e peso di ciascun indizio (regressione logistica), nell'ordine: centro, centro relativo, banda,
            // modulazione, armonicita' (media su 3 s), sua variazione, attacchi (media su 3 s), livello.
            double[] mu = { 0.8004, -0.0176, 0.0081, 0.0619, 0.3403, 0.1341, -0.0083, -2.2588 };
            double[] sd = { 0.1714, 0.1156, 0.2548, 0.2965, 1.5233, 0.6744, 0.0568, 10.6387 };
            double[] w = { 0.260, 0.016, 0.234, 0.168, 0.179, -0.078, 0.925, 0.498 };
            // Bias spostato di logit(0,6): il planner considera "voce" da 0,5, che cosi' corrisponde alla soglia scelta (0,6).
            const double Bias = 0.7452 - 0.4055;
            var probability = new double[seconds];
            for (int s = 0; s < seconds; s++)
            {
                double[] x =
                {
                    raw[s, 0], raw[s, 0] - median[0], raw[s, 1] - median[1], raw[s, 2] - median[2],
                    Smoothed(3, s), raw[s, 4] - median[4], Smoothed(5, s), raw[s, 6] - median[6]
                };
                double z = Bias;
                for (int i = 0; i < x.Length; i++) z += w[i] * (x[i] - mu[i]) / sd[i];
                probability[s] = 1 / (1 + Math.Exp(-z));
            }
            for (int s = 0; s < seconds; s++)
            {
                double sum = 0; int n = 0;
                for (int k = Math.Max(0, s - 1); k <= Math.Min(seconds - 1, s + 1); k++) { sum += probability[k]; n++; }
                // Un secondo di silenzio non e' voce, qualunque cosa dicano le proporzioni.
                result[s] = raw[s, 6] < -60 ? (byte)0 : (byte)Math.Round(100 * Math.Clamp(sum / n, 0, 1));
            }
            return result;
        }
    }
}
