#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// Tempo e battute di un tratto di brano: inviluppo degli attacchi (flusso spettrale), tempo per
    /// autocorrelazione fra 70 e 180 BPM, fase per somma a pettine. Serve all'automix per far cadere il
    /// primo battito del brano nuovo su un battito di quello che finisce.
    /// </summary>
    internal readonly record struct BeatGrid(double Bpm, double FirstBeat, double Confidence)
    {
        public double Period => Bpm > 0 ? 60.0 / Bpm : 0;
        public bool Reliable => double.IsFinite(Bpm) && Bpm >= 40 && Bpm <= 240 &&
            double.IsFinite(FirstBeat) && double.IsFinite(Confidence) && Confidence >= 1.35;

        /// <summary>Il battito della griglia piu' vicino a <paramref name="time"/>.</summary>
        public double NearestBeat(double time) => Period <= 0 ? time : FirstBeat + Math.Round((time - FirstBeat) / Period) * Period;
    }

    internal static class BeatTracker
    {
        private const int Fft = 1024, Hop = 512;

        /// <summary>
        /// Griglia del tratto <paramref name="samples"/> (mono, <paramref name="rate"/> Hz) che inizia a <paramref name="startSeconds"/>.
        /// Con <paramref name="expectBpm"/> il tempo si cerca solo attorno a quello (+-4%): serve a rileggere intro e coda con il
        /// tempo misurato nel corpo del brano, invece di lasciarle indovinare da sole.
        /// </summary>
        public static BeatGrid Analyse(float[] samples, int rate, double startSeconds, double expectBpm = 0)
        {
            if (rate <= 0 || samples.Length < Fft * 8) return default;
            // Stationary pads and noise still have tiny FFT fluctuations. The ratio between the
            // largest autocorrelation peak and other tiny peaks made those look very certain.
            // Require real envelope movement before attempting to lock another song to it.
            double rmsSum = 0, rmsSquares = 0; int rmsFrames = 0;
            for (int offset = 0; offset + Fft <= samples.Length; offset += Fft)
            {
                double energy = 0;
                for (int i = 0; i < Fft; i++) energy += samples[offset + i] * (double)samples[offset + i];
                double rms = Math.Sqrt(energy / Fft);
                rmsSum += rms; rmsSquares += rms * rms; rmsFrames++;
            }
            double rmsMean = rmsSum / rmsFrames;
            double rmsVariation = Math.Sqrt(Math.Max(0, rmsSquares / rmsFrames - rmsMean * rmsMean));
            if (rmsMean < 1e-6 || rmsVariation < rmsMean * 0.035) return default;
            var sharp = OnsetEnvelope(samples, out var low);
            if (sharp.Length < 64) return default;
            // Picchi ammorbiditi su qualche passo (filtro simmetrico, nessuno spostamento): con picchi larghi uno o
            // due passi, un periodo che cade fra due passi interi (es. 21,5 a 120 BPM) sfuggiva all'autocorrelazione.
            var onset = new double[sharp.Length];
            double[] kernel = { 0.25, 0.6, 1, 0.6, 0.25 };
            for (int i = 0; i < sharp.Length; i++)
            {
                double sum = 0;
                for (int k = -2; k <= 2; k++) { int j = i + k; if (j >= 0 && j < sharp.Length) sum += sharp[j] * kernel[k + 2]; }
                onset[i] = sum;
            }
            double frameSeconds = Hop / (double)rate;

            // Autocorrelazione dell'inviluppo (senza la media), pesata attorno a 120 BPM come fa l'orecchio.
            double mean = onset.Average();
            var centered = onset.Select(v => v - mean).ToArray();
            int minLag = (int)Math.Floor(60.0 / 180 / frameSeconds), maxLag = (int)Math.Ceiling(60.0 / 70 / frameSeconds);
            maxLag = Math.Min(maxLag, centered.Length / 3);
            if (maxLag <= minLag + 2) return default;
            // Autocorrelazione fino a quattro volte il periodo piu' lungo: un tempo vero si ripete anche
            // sulla battuta e sulla frase (2x, 4x). Con il solo periodo, cassa e rullante di timbro diverso
            // facevano vincere i 2/3 del tempo (120 BPM letti come 80).
            int maxAuto = Math.Min(centered.Length - 1, maxLag * 4);
            var auto = new double[maxAuto + 1];
            for (int lag = 1; lag <= maxAuto; lag++)
            {
                double sum = 0;
                for (int i = lag; i < centered.Length; i++) sum += centered[i] * centered[i - lag];
                auto[lag] = sum / (centered.Length - lag);
            }
            double At(int lag) => lag <= maxAuto ? Math.Max(0, auto[lag]) : 0;
            var scores = new double[maxLag + 1];
            double best = double.NegativeInfinity; int bestLag = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double bpm = 60.0 / (lag * frameSeconds);
                double weight = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120.0) / 0.9, 2));
                scores[lag] = (auto[lag] + 0.5 * At(lag * 2) + 0.25 * At(lag * 4)) * weight;
                bool allowed = expectBpm <= 0 || Math.Abs(bpm / expectBpm - 1) <= 0.04;
                if (allowed && scores[lag] > best) { best = scores[lag]; bestLag = lag; }
            }
            if (bestLag == 0 || best <= 0) return default;
            double variance = centered.Sum(v => v * v) / centered.Length;
            // A relative winning score alone cannot distinguish rhythm from a random maximum.
            if (variance <= 1e-12 || auto[bestLag] / variance < 0.12) return default;
            var positive = scores.Skip(minLag).Where(v => v > 0).ToList();
            double typical = positive.Count > 0 ? positive.Average() : 0;
            double confidence = typical > 0 ? best / typical : 0;

            // Periodo fine (frazione di frame) con la parabola sui tre valori attorno al massimo.
            double period = bestLag;
            if (bestLag > minLag && bestLag < maxLag)
            {
                double a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1];
                double denom = a - 2 * b + c;
                if (Math.Abs(denom) > 1e-12) period = bestLag + Math.Clamp(0.5 * (a - c) / denom, -0.5, 0.5);
            }

            // Periodo e fase insieme: attorno alla stima (+-2%, passi fini) il pettine che raccoglie piu' attacchi.
            // Un errore di pochi decimi di percento sul periodo, su 25 s, spostava la fase di decine di ms.
            double bestPhaseScore = double.NegativeInfinity; double bestPhase = 0, bestPeriod = period;
            for (double candidate = period * 0.98; candidate <= period * 1.02; candidate += period * 0.001)
            {
                int phases = Math.Max(1, (int)Math.Ceiling(candidate));
                for (int phase = 0; phase < phases; phase++)
                {
                    double sum = 0;
                    for (double t = phase; t < onset.Length; t += candidate)
                    {
                        int k = (int)Math.Round(t);
                        if (k < onset.Length) sum += onset[k];
                    }
                    if (sum > bestPhaseScore) { bestPhaseScore = sum; bestPhase = phase; bestPeriod = candidate; }
                }
            }
            period = bestPeriod;
            // Battere o levare? L'inviluppo somma tutte le frequenze: i charleston in levare (rumore su centinaia di
            // righe) pesano piu' della cassa (poche righe basse) e la griglia finiva a mezzo battito dal vero. Con
            // due brani presi uno sul battere e uno sul levare il mix manuale e l'Automix facevano il doppio colpo.
            // Si confrontano le due fasi solo sulle basse (cassa, corpo del rullante): vince quella con piu' attacchi.
            {
                double Comb(double[] env, double start)
                {
                    double sum = 0;
                    for (double t = start; t < env.Length; t += period)
                    {
                        int c = (int)Math.Round(t);
                        double peak = 0;
                        for (int k = -1; k <= 1; k++) { int idx = c + k; if (idx >= 0 && idx < env.Length) peak = Math.Max(peak, env[idx]); }
                        sum += peak;
                    }
                    return sum;
                }
                double half = bestPhase + period / 2;
                if (half >= period) half -= period;
                double onBeat = Comb(low, bestPhase), offBeat = Comb(low, half);
                if (offBeat > onBeat * 1.35 && offBeat > 0) bestPhase = Math.Round(half);
            }
            // Fase fine: media pesata degli attacchi attorno a ogni dente, in frazione di passo.
            double offsetSum = 0, weightSum = 0;
            for (double t = bestPhase; t < onset.Length; t += period)
            {
                int c = (int)Math.Round(t);
                for (int k = -2; k <= 2; k++)
                {
                    int idx = c + k;
                    if (idx < 0 || idx >= sharp.Length) continue;
                    offsetSum += (idx - t) * sharp[idx]; weightSum += sharp[idx];
                }
            }
            double finePhase = bestPhase + (weightSum > 0 ? Math.Clamp(offsetSum / weightSum, -1.5, 1.5) : 0);
            double bpmValue = 60.0 / (period * frameSeconds);
            // La finestra di analisi vede l'attacco gia' quando le entra dal bordo, prima del suo centro: la
            // posizione vera e' ~0,6 finestre dopo il passo (misurato su tracce con il battito in posizione nota).
            double firstBeat = startSeconds + finePhase * frameSeconds + 0.6 * Fft / (double)rate;
            if (firstBeat < startSeconds) firstBeat += period * frameSeconds;
            return new BeatGrid(Math.Round(bpmValue, 2), firstBeat, Math.Round(confidence, 2));
        }

        /// <summary>Flusso spettrale positivo (log-ampiezza), un valore ogni <see cref="Hop"/> campioni.</summary>
        private static double[] OnsetEnvelope(float[] samples, out double[] lowEnvelope)
        {
            int frames = (samples.Length - Fft) / Hop;
            var window = new double[Fft];
            for (int i = 0; i < Fft; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Fft - 1));
            var re = new double[Fft]; var im = new double[Fft];
            var previous = new double[Fft / 2];
            var onset = new double[Math.Max(0, frames)];
            var lowOnset = new double[Math.Max(0, frames)];
            // Basse: fino a ~150 Hz alla frequenza dell'analisi (22050 Hz: righe da 21,5 Hz).
            int lowTop = Math.Max(3, (int)Math.Round(150.0 * Fft / 22050));
            for (int f = 0; f < frames; f++)
            {
                int offset = f * Hop;
                for (int i = 0; i < Fft; i++) { re[i] = samples[offset + i] * window[i]; im[i] = 0; }
                Transform(re, im);
                double flux = 0, lowFlux = 0;
                // Fino a ~6 kHz (a 22050 Hz): li' stanno cassa, rullante e le note che danno il tempo.
                int top = Fft / 2 * 6000 / 11025;
                for (int k = 1; k < top; k++)
                {
                    double magnitude = Math.Log(1 + 100 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                    double rise = magnitude - previous[k];
                    if (rise > 0) { flux += rise; if (k <= lowTop) lowFlux += rise; }
                    previous[k] = magnitude;
                }
                onset[f] = f == 0 ? 0 : flux;
                lowOnset[f] = f == 0 ? 0 : lowFlux;
            }
            lowEnvelope = Detrend(lowOnset);
            return Detrend(onset);
        }

        private static double[] Detrend(double[] onset)
        {
            // Toglie l'andamento lento (crescendo, strofe piu' piene): restano gli attacchi.
            var result = new double[onset.Length];
            int half = 8;
            for (int i = 0; i < onset.Length; i++)
            {
                double local = 0; int n = 0;
                for (int j = Math.Max(0, i - half); j < Math.Min(onset.Length, i + half + 1); j++) { local += onset[j]; n++; }
                result[i] = Math.Max(0, onset[i] - local / n);
            }
            return result;
        }

        /// <summary>FFT radix-2 sul posto.</summary>
        internal static void Transform(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                double wr = Math.Cos(angle), wi = Math.Sin(angle);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int j = 0; j < len / 2; j++)
                    {
                        int a = i + j, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double next = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = next;
                    }
                }
            }
        }
    }
}
