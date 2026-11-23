#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace CinecorePlayer2025.Audio;

// Local observations, not labels such as "chorus" inferred from a fixed 32-beat grid.
internal sealed record MusicalFrame(double Db, double Percussion, double Bass, double Tonal, double[] Chroma);
internal sealed record MusicalBoundary(double Time, double Strength, double Drop, double Rise);
internal sealed record MusicalAttack(double Time, double Strength);
internal sealed record MusicalMap(double Start, double Step, MusicalFrame[] Frames, MusicalBoundary[] Boundaries, MusicalAttack[]? Attacks = null)
{
    internal MusicalFrame? At(double time)
    {
        int i = (int)Math.Floor((time - Start) / Step);
        return i >= 0 && i < Frames.Length ? Frames[i] : null;
    }
}

internal static class MusicalSections
{
    // Harmonic/percussive soft masks: a sustained frequency has a large temporal median;
    // a broadband transient has a large spectral median. Retain chroma only from the
    // harmonic mask so drum noise cannot invent a compatible chord.
    internal static MusicalMap Analyse(float[] input, int rate, double start)
    {
        const int size = 1024, hop = 256, bins = size / 2;
        int stride = Math.Max(1, rate / 11025), sampleRate = rate / stride;
        int length = input.Length / stride, count = Math.Max(0, (length - size) / hop + 1);
        var spectrum = new float[count][];
        var flux = new double[count];
        var re = new double[size]; var im = new double[size];
        for (int f = 0; f < count; f++)
        {
            for (int i = 0; i < size; i++)
            { re[i] = input[(f * hop + i) * stride] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (size - 1))); im[i] = 0; }
            BeatTracker.Transform(re, im);
            spectrum[f] = new float[bins];
            for (int k = 1; k < bins; k++) spectrum[f][k] = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            if (f > 0)
            {
                double rise = 0, sum = 0;
                for (int k = 2; k < bins; k++) { rise += Math.Max(0,spectrum[f][k]-spectrum[f-1][k]);sum += spectrum[f][k]; }
                flux[f] = sum > .05 ? rise / sum : 0;
            }
        }
        const double step = .25;
        int n = (int)(length / (double)sampleRate / step);
        var power = new double[n]; var percussive = new double[n]; var bass = new double[n];
        var harmonic = new double[n]; var total = new double[n];
        var chroma = Enumerable.Range(0, n).Select(_ => new double[12]).ToArray();
        for (int i = 0; i < length; i++)
        {
            int s = Math.Min(n - 1, (int)(i / (double)sampleRate / step));
            if (s >= 0) power[s] += input[i * stride] * (double)input[i * stride];
        }
        var median = new float[17];
        for (int f = 0; f < count; f++)
        {
            int s = Math.Min(n - 1, (int)((f * hop + size / 2.0) / sampleRate / step));
            for (int k = 2; k < bins - 2; k++)
            {
                int c = 0;
                for (int t = Math.Max(0, f - 8); t <= Math.Min(count - 1, f + 8); t++) median[c++] = spectrum[t][k];
                Array.Sort(median, 0, c); double h = median[c / 2];
                c = 0;
                for (int b = Math.Max(1, k - 8); b <= Math.Min(bins - 1, k + 8); b++) median[c++] = spectrum[f][b];
                Array.Sort(median, 0, c); double p = median[c / 2];
                double mask = p * p / (h * h + p * p + 1e-15);
                double energy = spectrum[f][k] * (double)spectrum[f][k];
                total[s] += energy; percussive[s] += energy * mask; harmonic[s] += energy * (1 - mask);
                double hz = k * (double)sampleRate / size;
                if (hz < 180) bass[s] += energy;
                if (hz < 125 || hz > 3500) continue;
                // Spectral peaks reduce chroma smearing and octave overtones of broadband sounds.
                if (spectrum[f][k] < spectrum[f][k - 1] || spectrum[f][k] < spectrum[f][k + 1]) continue;
                int note = (int)Math.Round(69 + 12 * Math.Log2(hz / 440));
                chroma[s][((note % 12) + 12) % 12] += Math.Sqrt(energy) * (1 - mask);
            }
        }
        var frames = new MusicalFrame[n];
        for (int s = 0; s < n; s++)
        {
            double norm = Math.Sqrt(chroma[s].Sum(x => x * x));
            if (norm > 0) for (int k = 0; k < 12; k++) chroma[s][k] /= norm;
            frames[s] = new(10 * Math.Log10(power[s] / (sampleRate * step) + 1e-12),
                percussive[s] / Math.Max(1e-12, total[s]), bass[s] / Math.Max(1e-12, total[s]),
                harmonic[s] / Math.Max(1e-12, total[s]), chroma[s]);
        }
        var boundaries = new List<MusicalBoundary>();
        for (int s = 8; s < n - 8; s++)
        {
            double before = PowerDb(frames, s - 8, s), after = PowerDb(frames, s, s + 8);
            double oldPerc = frames.Skip(s - 8).Take(8).Average(x => x.Percussion);
            double newPerc = frames.Skip(s).Take(8).Average(x => x.Percussion);
            double tonalChange = 1 - Similarity(AverageChroma(frames, s - 8, s), AverageChroma(frames, s, s + 8));
            double strength = Math.Clamp(Math.Abs(after - before) / 12 + Math.Abs(newPerc - oldPerc) * 1.5 + tonalChange * .4, 0, 1);
            if (strength < .28) continue;
            var boundary = new MusicalBoundary(start + s * step, strength, Math.Max(0, before - after), Math.Max(0, after - before));
            if (boundaries.Count > 0 && boundary.Time - boundaries[^1].Time < 2)
            { if (strength > boundaries[^1].Strength) boundaries[^1] = boundary; }
            else boundaries.Add(boundary);
        }
        var attacks = new List<MusicalAttack>();
        for (int f = 2; f < count - 2; f++)
        {
            if (flux[f] < .20 || flux[f] < flux[f-1] || flux[f] < flux[f+1]) continue;
            double at = (f * hop + size / 2.0) / sampleRate;
            // Refine the STFT observation on a 5 ms energy envelope: retain the start of
            // the rising attack, not the centre of the 93 ms spectral window.
            int centre=(int)(at*sampleRate), bin=Math.Max(1,sampleRate/200);
            double bestRise=0;int onset=centre;
            for(int i=Math.Max(bin,centre-size/2);i<Math.Min(length-bin,centre+size/2);i+=bin)
            {
                double before=0,after=0;
                for(int j=0;j<bin;j++){before+=input[(i-bin+j)*stride]*(double)input[(i-bin+j)*stride];after+=input[(i+j)*stride]*(double)input[(i+j)*stride];}
                double rise=after-before;
                if(rise>bestRise){bestRise=rise;onset=i;}
            }
            double time=start+onset/(double)sampleRate;
            if(attacks.Count>0 && time-attacks[^1].Time<.12)
            { if(flux[f]>attacks[^1].Strength)attacks[^1]=new(time,flux[f]); }
            else attacks.Add(new(time,flux[f]));
        }
        return new(start, step, frames, boundaries.ToArray(), attacks.ToArray());
    }

    internal static double Similarity(double[] a, double[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < 12; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa > 1e-8 && bb > 1e-8 ? dot / Math.Sqrt(aa * bb) : .5;
    }
    private static double[] AverageChroma(MusicalFrame[] f, int from, int to)
    {
        var result = new double[12];
        for (int i = from; i < to; i++) for (int k = 0; k < 12; k++) result[k] += f[i].Chroma[k];
        return result;
    }
    private static double PowerDb(MusicalFrame[] f, int from, int to) =>
        10 * Math.Log10(f.Skip(from).Take(to - from).Average(x => Math.Pow(10, x.Db / 10)) + 1e-12);
}
