#nullable enable
using System;
using System.Collections.Generic;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// Tonalita' di un brano: cromagramma (energia per nota, 65 Hz - 2 kHz) confrontato con i profili di
    /// Krumhansl-Kessler delle 24 tonalita'. Basta per il missaggio armonico (ruota di Camelot): due brani in
    /// tonalita' vicine si sovrappongono senza stonare.
    /// </summary>
    internal static class KeyDetector
    {
        private const int Fft = 8192, Hop = 4096;
        private static readonly double[] Major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        private static readonly double[] Minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
        private static readonly string[] Names = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

        /// <summary>Tonalita' (0-11 maggiori da Do, 12-23 minori da Do) e sicurezza (0-1), o (-1, 0).</summary>
        public static (int Key, double Confidence) Detect(IReadOnlyList<float> samples, int rate)
        {
            if (rate <= 0 || samples.Count < Fft * 4) return (-1, 0);
            var chroma = new double[12];
            var window = new double[Fft];
            for (int i = 0; i < Fft; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Fft - 1));
            // Classe di nota per ogni riga della FFT nella banda utile (-1 fuori banda).
            var pitchClass = new int[Fft / 2];
            var bandwidth = new int[12];
            for (int k = 0; k < pitchClass.Length; k++)
            {
                double hz = k * rate / (double)Fft;
                pitchClass[k] = hz < 65 || hz > 2100 ? -1 : ((int)Math.Round(12 * Math.Log2(hz / 440.0) + 69) % 12 + 12) % 12;
                if (pitchClass[k] >= 0) bandwidth[pitchClass[k]]++;
            }
            var re = new double[Fft]; var im = new double[Fft];
            var frame = new double[12];
            double tonalSum = 0; int frames = 0;
            for (int offset = 0; offset + Fft <= samples.Count; offset += Hop)
            {
                for (int i = 0; i < Fft; i++) { re[i] = samples[offset + i] * window[i]; im[i] = 0; }
                BeatTracker.Transform(re, im);
                Array.Clear(frame);
                double total = 0, logMagnitude = 0; int bins = 0;
                for (int k = 1; k < pitchClass.Length; k++)
                {
                    int pc = pitchClass[k];
                    if (pc < 0) continue;
                    double magnitude = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    frame[pc] += magnitude; total += magnitude;
                    logMagnitude += Math.Log(Math.Max(1e-12, magnitude)); bins++;
                }
                if (total < 1e-3) continue; // silenzio
                double flatness = Math.Exp(logMagnitude / Math.Max(1, bins)) / (total / Math.Max(1, bins));
                tonalSum += Math.Clamp((0.72 - flatness) / 0.4, 0, 1); frames++;
                // Different pitch classes occupy different numbers of linear FFT bins. Without
                // this correction even broadband noise has a repeatable, highly confident key.
                double weightedTotal = 0;
                for (int pc = 0; pc < 12; pc++) { frame[pc] /= Math.Max(1, bandwidth[pc]); weightedTotal += frame[pc]; }
                // Ogni finestra pesa uguale: i passaggi forti non decidono da soli la tonalita'.
                for (int pc = 0; pc < 12; pc++) chroma[pc] += frame[pc] / weightedTotal;
            }
            double best = double.NegativeInfinity, second = double.NegativeInfinity; int bestKey = -1;
            for (int key = 0; key < 24; key++)
            {
                var profile = key < 12 ? Major : Minor;
                int tonic = key % 12;
                double r = Correlation(chroma, profile, tonic);
                if (r > best) { second = best; best = r; bestKey = key; }
                else if (r > second) second = r;
            }
            if (bestKey < 0 || best < 0.3) return (-1, 0);
            double tonalEvidence = frames > 0 ? tonalSum / frames : 0;
            return (bestKey, Math.Round(Math.Clamp((best - second) * 5 + (best - 0.3), 0, 1) * tonalEvidence, 2));
        }

        private static double Correlation(double[] chroma, double[] profile, int tonic)
        {
            double meanC = 0, meanP = 0;
            for (int i = 0; i < 12; i++) { meanC += chroma[i]; meanP += profile[i]; }
            meanC /= 12; meanP /= 12;
            double num = 0, dc = 0, dp = 0;
            for (int i = 0; i < 12; i++)
            {
                double c = chroma[(i + tonic) % 12] - meanC, p = profile[i] - meanP;
                num += c * p; dc += c * c; dp += p * p;
            }
            return dc > 0 && dp > 0 ? num / Math.Sqrt(dc * dp) : 0;
        }

        /// <summary>Numero della ruota di Camelot (1-12) e lettera (A minore, B maggiore).</summary>
        public static (int Number, char Letter) Camelot(int key)
        {
            int major = key < 12 ? key : (key % 12 + 3) % 12; // il relativo maggiore ha lo stesso numero
            return ((major * 7 % 12 + 7) % 12 + 1, key < 12 ? 'B' : 'A');
        }

        public static string CamelotText(int key) { if (key < 0) return ""; var (n, l) = Camelot(key); return n + l.ToString(); }

        public static string Name(int key) => key < 0 ? "" : Names[key % 12] + (key < 12 ? "" : "m");

        /// <summary>Quanto stanno bene insieme due tonalita': 2 uguale o adiacente sulla ruota, 1 accettabile, 0 stonano.</summary>
        public static int Compatibility(int a, int b)
        {
            if (a < 0 || b < 0) return 1;
            var (na, la) = Camelot(a); var (nb, lb) = Camelot(b);
            int step = Math.Min((na - nb + 12) % 12, (nb - na + 12) % 12);
            if (la == lb && step <= 1) return 2;
            if (la != lb && step == 0) return 2;          // relativa maggiore/minore
            if (la == lb && step == 2) return 1;          // due passi: ancora accettabile in un passaggio breve
            return 0;
        }
    }
}
