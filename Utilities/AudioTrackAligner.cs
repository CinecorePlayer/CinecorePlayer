#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

/// <summary>
/// Finds how one audio track sits against another (a dub against the original, an external
/// file against the film) by listening to both.
///
/// Two dubs share music and effects but not the voices, so the loudness of the full mix is a
/// poor witness: the old analysis compared exactly that and landed 150-200 ms off. Here each
/// scene is turned into note/impact onsets in six frequency bands, measured twice: on the mono
/// mix and on the left-minus-right signal, where centre-panned dialogue cancels out and what
/// is left is almost only music and effects. Scenes are spread along the whole film; a line
/// fitted through their delays tells a constant offset from a speed difference (PAL 25 fps
/// against 23.976/24, or 24 against 23.976).
/// </summary>
internal static class AudioTrackAligner
{
    private const int SampleRate = 16000, Hop = 160, FftSize = 512, FramesPerSecond = SampleRate / Hop;
    private static readonly int[] BandEdges = { 2, 6, 13, 26, 51, 102, 205 }; // 60 Hz … 6.4 kHz in FFT bins
    private static readonly int BandCount = BandEdges.Length - 1;

    internal sealed record Scene(double Time, double Delay, double Peak, double Margin, bool Side, bool Valid);

    /// <param name="Delay">Seconds the target is late at the start of the film, once its speed is corrected.</param>
    /// <param name="Speed">How much faster than the reference the target runs (1 = same speed).</param>
    internal sealed record Fit(bool Reliable, double Delay, double Speed, double Confidence, int Inliers, int Valid, int Total, double ResidualMs, IReadOnlyList<Scene> Scenes, bool Varies = false, double SpreadMs = 0);

    private sealed record Features(float[][] Mix, float[][]? Side, int Frames);

    internal static double[] SceneStarts(double duration, double window, int sceneCount = 0)
    {
        if (duration < window * 2.5) return new[] { 0d };
        int count = sceneCount > 0 ? sceneCount : duration >= 1200 ? 8 : duration >= 240 ? 5 : 3;
        double first = duration * 0.06, last = Math.Max(first, duration * 0.90 - window);
        return Enumerable.Range(0, count).Select(i => Math.Round(first + (last - first) * i / Math.Max(1, count - 1), 2)).Distinct().ToArray();
    }

    internal static double WindowSeconds(double duration) => duration >= 240 ? 40 : Math.Max(12, Math.Min(40, duration / 3));

    /// <summary>
    /// Measures every scene under one speed hypothesis. <paramref name="reference"/> caches the
    /// reference side between hypotheses (reading it means reading the video file too).
    /// </summary>
    /// <param name="centreDelay">Delay around which the search is centred (seconds, target late).</param>
    internal static async Task<Fit> MeasureAsync(string ffmpeg, string referencePath, int referenceOrdinal, string targetPath, int targetOrdinal,
        double duration, double speed, double maxOffsetSeconds, Dictionary<double, object> reference, Action<int, int, double> progress, CancellationToken ct,
        int sceneCount = 0, double windowSeconds = 0, double centreDelay = 0)
    {
        double window = windowSeconds > 0 ? Math.Min(windowSeconds, Math.Max(12, duration / 3)) : WindowSeconds(duration);
        double[] starts = SceneStarts(duration, window, sceneCount);
        var scenes = new List<Scene>();
        for (int i = 0; i < starts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress(i, starts.Length, starts[i]);
            scenes.Add(await MeasureOneAsync(ffmpeg, referencePath, referenceOrdinal, targetPath, targetOrdinal, starts[i], window, speed, centreDelay, maxOffsetSeconds, reference, ct).ConfigureAwait(false));
        }
        progress(starts.Length, starts.Length, duration);
        return FitScenes(scenes, speed);
    }

    /// <summary>One scene: where the target has the audio that the reference plays from <paramref name="start"/>.</summary>
    internal static async Task<Scene> MeasureOneAsync(string ffmpeg, string referencePath, int referenceOrdinal, string targetPath, int targetOrdinal,
        double start, double window, double speed, double centreDelay, double maxOffsetSeconds, Dictionary<double, object>? reference, CancellationToken ct)
    {
        try
        {
            Task<Features?> referenceTask = reference != null && reference.TryGetValue(start, out object? cached)
                ? Task.FromResult((Features?)cached)
                : ExtractFeaturesAsync(ffmpeg, referencePath, referenceOrdinal, start, window, ct);
            // The same scene in the target, widened by the largest offset searched on both sides.
            double targetStart = Math.Max(0, (start + centreDelay - maxOffsetSeconds) / speed);
            double targetLength = (start + centreDelay + window + maxOffsetSeconds) / speed - targetStart;
            Task<Features?> targetTask = ExtractFeaturesAsync(ffmpeg, targetPath, targetOrdinal, targetStart, targetLength, ct);
            await Task.WhenAll(referenceTask, targetTask).ConfigureAwait(false);
            Features? a = referenceTask.Result, b = targetTask.Result;
            if (a != null && reference != null) reference[start] = a;
            if (a == null || b == null) return new Scene(start + window / 2, 0, 0, 0, false, false);
            return await Task.Run(() => MeasureScene(a, b, start, targetStart, window, speed, centreDelay, maxOffsetSeconds, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Dbg.Warn($"[AUDIOSYNC] scene at {start:0}s: {ex.Message}");
            return new Scene(start + window / 2, 0, 0, 0, false, false);
        }
    }

    private static Scene MeasureScene(Features reference, Features target, double start, double targetStart, double window, double speed, double centreDelay, double maxOffset, CancellationToken ct)
    {
        // Target frames are re-timed to the reference clock: frame j is reference time speed*targetStart + j/fps.
        int centre = (int)Math.Round(centreDelay * FramesPerSecond);
        int origin = (int)Math.Round((start - speed * targetStart) * FramesPerSecond) + centre;
        int maxLag = (int)Math.Round(maxOffset * FramesPerSecond);
        var mix = Correlate(reference.Mix, Retime(target.Mix, speed), origin, maxLag, ct);
        var best = (mix.Delay, mix.Peak, mix.Margin, Side: false);
        if (reference.Side != null && target.Side != null)
        {
            var side = Correlate(reference.Side, Retime(target.Side, speed), origin, maxLag, ct);
            // Without the voices the peak is cleaner: prefer it whenever it stands out at least as much.
            if (side.Peak * Math.Min(side.Margin, 3) >= best.Peak * Math.Min(best.Margin, 3)) best = (side.Delay, side.Peak, side.Margin, true);
        }
        bool valid = best.Peak >= 0.10 && best.Margin >= 1.30;
        return new Scene(start + window / 2, (best.Delay + centre) / FramesPerSecond, best.Peak, best.Margin, best.Side, valid);
    }

    /// <summary>
    /// A delay that changes along the film (a dub synchronised reel by reel, or a different edit):
    /// the scenes become a curve. Wherever two neighbouring scenes disagree, more scenes are
    /// measured in between until the point where the delay changes is known within a few seconds:
    /// the player switches from one delay to the other halfway between two points, so the gap
    /// between them is the error on the position of the change.
    /// </summary>
    internal static async Task<List<Scene>> RefineCurveAsync(string ffmpeg, string referencePath, int referenceOrdinal, string targetPath, int targetOrdinal,
        IEnumerable<Scene> measured, Action<int, int> progress, CancellationToken ct)
    {
        var points = measured.Where(scene => scene.Valid).OrderBy(scene => scene.Time).ToList();
        // A lone scene that disagrees with two neighbours that agree with each other is a false match.
        for (int i = points.Count - 2; i >= 1; i--)
            if (Math.Abs(points[i - 1].Delay - points[i + 1].Delay) <= 0.15 &&
                Math.Abs(points[i].Delay - points[i - 1].Delay) > 0.25 && Math.Abs(points[i].Delay - points[i + 1].Delay) > 0.25)
                points.RemoveAt(i);

        const int budget = 72;
        var unresolved = new HashSet<double>();
        int done = 0;
        while (done < budget)
        {
            ct.ThrowIfCancellationRequested();
            int at = -1;
            double widest = 0;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double gap = points[i + 1].Time - points[i].Time, step = Math.Abs(points[i + 1].Delay - points[i].Delay);
                // A real change (the player treats more than 120 ms as one) is pinned down to 16 s between
                // points, 8 s of error at most; a slow slide only needs a point every couple of minutes.
                double wanted = step > 0.12 ? 16 : 120;
                if (step > 0.1 && gap > wanted && gap > widest && !unresolved.Contains(points[i].Time)) { widest = gap; at = i; }
            }
            if (at < 0) break;
            progress(done, budget);
            Scene a = points[at], b = points[at + 1];
            double middle = (a.Time + b.Time) / 2;
            // Close to the change the window shrinks, or it would contain both delays at once.
            double window = widest > 90 ? 30 : widest > 40 ? 20 : 12;
            var scene = await MeasureOneAsync(ffmpeg, referencePath, referenceOrdinal, targetPath, targetOrdinal, middle - window / 2, window, 1,
                (a.Delay + b.Delay) / 2, Math.Abs(a.Delay - b.Delay) / 2 + 1.5, null, ct).ConfigureAwait(false);
            done++;
            // The new scene must sit on one of the two sides: anything else is a false match.
            if (scene.Valid && (Math.Abs(scene.Delay - a.Delay) <= 0.15 || Math.Abs(scene.Delay - b.Delay) <= 0.15)) points.Insert(at + 1, scene);
            else unresolved.Add(a.Time);
        }
        progress(budget, budget);
        // The two sets of scenes share their first and last position: one point per place.
        for (int i = points.Count - 1; i >= 1; i--)
            if (points[i].Time - points[i - 1].Time < 5) points.RemoveAt(points[i].Peak >= points[i - 1].Peak ? i - 1 : i);
        return points;
    }

    private static float[][] Retime(float[][] bands, double speed)
    {
        if (Math.Abs(speed - 1) < 1e-9) return bands;
        int length = (int)(bands[0].Length * speed);
        var result = new float[bands.Length][];
        for (int b = 0; b < bands.Length; b++)
        {
            float[] source = bands[b], scaled = new float[length];
            for (int j = 0; j < length; j++)
            {
                double position = j / speed;
                int index = (int)position;
                if (index >= source.Length - 1) { scaled[j] = source[^1]; continue; }
                float fraction = (float)(position - index);
                scaled[j] = source[index] * (1 - fraction) + source[index + 1] * fraction;
            }
            result[b] = scaled;
        }
        return result;
    }

    /// <summary>Normalised cross-correlation over all bands; the delay is in frames (positive: target late).</summary>
    private static (double Delay, double Peak, double Margin) Correlate(float[][] reference, float[][] target, int origin, int maxLag, CancellationToken ct)
    {
        int n = reference[0].Length, m = target[0].Length;
        if (n < FramesPerSecond * 8 || m < FramesPerSecond * 8) return (0, 0, 0);
        // A wide search (minutes) at full resolution costs billions of products: first at a quarter
        // of the resolution to find where the scene is, then at full resolution around that point.
        if (maxLag > FramesPerSecond * 25)
        {
            const int factor = 4;
            var coarse = Correlate(Decimate(reference, factor), Decimate(target, factor), origin / factor, maxLag / factor, ct, minimumFrames: FramesPerSecond * 2);
            if (coarse.Peak <= 0) return (0, 0, 0);
            int centre = (int)Math.Round(coarse.Delay * factor);
            var fine = Correlate(reference, target, origin + centre, FramesPerSecond, ct);
            // How much the peak stands out is judged on the whole search, not on the last second.
            return fine.Peak <= 0 ? (0, 0, 0) : (centre + fine.Delay, fine.Peak, Math.Min(fine.Margin, coarse.Margin));
        }
        return Correlate(reference, target, origin, maxLag, ct, minimumFrames: FramesPerSecond * 8);
    }

    private static float[][] Decimate(float[][] bands, int factor)
    {
        var result = new float[bands.Length][];
        for (int b = 0; b < bands.Length; b++)
        {
            float[] source = bands[b], small = new float[source.Length / factor];
            for (int i = 0; i < small.Length; i++)
            {
                float sum = 0;
                for (int k = 0; k < factor; k++) sum += source[i * factor + k];
                small[i] = sum / factor;
            }
            result[b] = small;
        }
        return result;
    }

    private static (double Delay, double Peak, double Margin) Correlate(float[][] reference, float[][] target, int origin, int maxLag, CancellationToken ct, int minimumFrames)
    {
        int n = reference[0].Length, m = target[0].Length;
        if (n < minimumFrames || m < minimumFrames) return (0, 0, 0);
        int bands = reference.Length;
        // "Away from the peak" is 0.3 s at any resolution.
        int apart = Math.Max(3, (int)Math.Round(0.3 * FramesPerSecond * n / Math.Max(1.0, reference[0].Length)));
        if (minimumFrames < FramesPerSecond * 8) apart = Math.Max(2, FramesPerSecond * 3 / 10 / 4);
        var a = new float[bands][];
        var b = new float[bands][];
        // Running sums of the squares, to normalise any stretch without walking it again.
        var squaresA = new double[n + 1];
        var squaresB = new double[m + 1];
        for (int band = 0; band < bands; band++)
        {
            a[band] = Centered(reference[band]);
            b[band] = Centered(target[band]);
        }
        for (int i = 0; i < n; i++) { double sum = 0; for (int band = 0; band < bands; band++) sum += a[band][i] * a[band][i]; squaresA[i + 1] = squaresA[i] + sum; }
        for (int i = 0; i < m; i++) { double sum = 0; for (int band = 0; band < bands; band++) sum += b[band][i] * b[band][i]; squaresB[i + 1] = squaresB[i] + sum; }
        if (squaresA[n] <= 1e-9 || squaresB[m] <= 1e-9) return (0, 0, 0);

        var scores = new double[2 * maxLag + 1];
        Parallel.For(0, scores.Length, new ParallelOptions { CancellationToken = ct }, index =>
        {
            int shift = origin + index - maxLag;              // target index of reference frame 0
            int from = Math.Max(0, -shift), to = Math.Min(n, m - shift);
            if (to - from < n * 0.6) { scores[index] = double.NegativeInfinity; return; }
            double dot = 0;
            for (int band = 0; band < bands; band++)
            {
                float[] x = a[band], y = b[band];
                float partial = 0;
                for (int i = from; i < to; i++) partial += x[i] * y[i + shift];
                dot += partial;
            }
            double energy = (squaresA[to] - squaresA[from]) * (squaresB[to + shift] - squaresB[from + shift]);
            scores[index] = energy > 1e-12 ? dot / Math.Sqrt(energy) : double.NegativeInfinity;
        });

        int peak = -1;
        for (int i = 0; i < scores.Length; i++) if (double.IsFinite(scores[i]) && (peak < 0 || scores[i] > scores[peak])) peak = i;
        if (peak < 0 || scores[peak] <= 0) return (0, 0, 0);
        // Best value away from the peak (over 0.3 s): how much the peak stands out.
        double second = 0;
        for (int i = 0; i < scores.Length; i++)
            if (Math.Abs(i - peak) > apart && double.IsFinite(scores[i]) && scores[i] > second) second = scores[i];
        double refined = peak;
        if (peak > 0 && peak < scores.Length - 1 && double.IsFinite(scores[peak - 1]) && double.IsFinite(scores[peak + 1]))
        {
            double curvature = scores[peak - 1] - 2 * scores[peak] + scores[peak + 1];
            if (curvature < -1e-12) refined += Math.Clamp(0.5 * (scores[peak - 1] - scores[peak + 1]) / curvature, -0.5, 0.5);
        }
        return (refined - maxLag, scores[peak], second > 1e-6 ? scores[peak] / second : 9);
    }

    private static float[] Centered(float[] values)
    {
        double mean = 0;
        foreach (float value in values) mean += value;
        mean /= Math.Max(1, values.Length);
        var result = new float[values.Length];
        for (int i = 0; i < values.Length; i++) result[i] = (float)(values[i] - mean);
        return result;
    }

    /// <summary>
    /// Line through the scene delays, tolerant of scenes that disagree (a different edit, a
    /// stretch of dialogue only). Delay in reference time: D(t) = A + B·t.
    ///
    /// Many dubs were synchronised reel by reel and sit 50-150 ms apart from one scene to the
    /// next: no single delay fits them exactly. When the scenes do not agree tightly but stay
    /// inside a narrow band, the answer is the middle of the band, flagged as "varies".
    /// </summary>
    internal static Fit FitScenes(IReadOnlyList<Scene> scenes, double speed)
    {
        var valid = scenes.Where(scene => scene.Valid).ToList();
        if (valid.Count == 0) return new Fit(false, 0, speed, 0, 0, 0, scenes.Count, 0, scenes);
        int needed = scenes.Count >= 5 ? Math.Max(3, (int)Math.Ceiling(scenes.Count * 0.5)) : Math.Max(2, scenes.Count);
        if (scenes.Count == 1) needed = 1;

        (double A, double B, List<Scene> Inliers) Cluster(double tolerance)
        {
            (double A, double B, List<Scene> Inliers) best = (valid[0].Delay, 0, new List<Scene> { valid[0] });
            void Try(double a, double b)
            {
                var inliers = valid.Where(scene => Math.Abs(scene.Delay - (a + b * scene.Time)) <= tolerance).ToList();
                // More scenes win; with the same number a constant delay is preferred to a slope.
                if (inliers.Count > best.Inliers.Count || (inliers.Count == best.Inliers.Count && b == 0 && best.B != 0)) best = (a, b, inliers);
            }
            foreach (var scene in valid) Try(scene.Delay, 0);
            for (int i = 0; i < valid.Count; i++)
                for (int j = i + 1; j < valid.Count; j++)
                {
                    double dt = valid[j].Time - valid[i].Time;
                    if (dt < 30) continue;
                    double slope = (valid[j].Delay - valid[i].Delay) / dt;
                    if (Math.Abs(slope) <= 0.004) Try(valid[i].Delay - slope * valid[i].Time, slope);
                }
            return best;
        }

        var tight = Cluster(0.035);
        // "Constant" needs nearly every recognised scene on one value: a few scenes agreeing on
        // another value are a second stretch of the film, not noise.
        bool varies = tight.Inliers.Count < needed || tight.Inliers.Count < valid.Count * 0.85;
        var set = (varies ? Cluster(0.15) : tight).Inliers;
        double span = set.Max(scene => scene.Time) - set.Min(scene => scene.Time);
        double slopeFit = 0;
        if (set.Count >= 3 && span > 60)
        {
            double mt = set.Average(scene => scene.Time), md = set.Average(scene => scene.Delay);
            double sxx = set.Sum(scene => (scene.Time - mt) * (scene.Time - mt));
            double slope = sxx > 0 ? set.Sum(scene => (scene.Time - mt) * (scene.Delay - md)) / sxx : 0;
            // A slope counts only when it moves the delay clearly more than the scenes scatter.
            if (Math.Abs(slope) * span > (varies ? 0.30 : 0.06)) slopeFit = slope;
        }
        // Middle value of what is left once the slope is taken out.
        var levels = set.Select(scene => scene.Delay - slopeFit * scene.Time).OrderBy(value => value).ToList();
        double level = levels.Count % 2 == 1 ? levels[levels.Count / 2] : (levels[levels.Count / 2 - 1] + levels[levels.Count / 2]) / 2;
        double residual = levels.Max(value => Math.Abs(value - level)) * 1000;
        double spread = (levels[^1] - levels[0]) * 1000;
        bool reliable = set.Count >= needed && (scenes.Count > 1 || (set[0].Peak >= 0.2 && set[0].Margin >= 1.6));
        // Scenes left out that agree with each other on another delay: a second stretch of the film
        // with its own offset (different edit). Majority is not proof then.
        var others = valid.Where(scene => !set.Contains(scene)).ToList();
        if (others.Any(a => Math.Abs(a.Delay - slopeFit * a.Time - level) > 0.3 && others.Count(b => Math.Abs(a.Delay - slopeFit * a.Time - (b.Delay - slopeFit * b.Time)) <= 0.13) >= 2))
            reliable = false;
        double quality = set.Average(scene => Math.Clamp((scene.Peak - 0.05) / 0.35, 0, 1) * 0.5 + Math.Clamp((scene.Margin - 1) / 1.5, 0, 1) * 0.5);
        double confidence = Math.Clamp((double)set.Count / scenes.Count * (0.55 + 0.45 * quality), 0, 1);
        if (varies) confidence *= 0.85;
        if (!reliable) confidence = Math.Min(confidence, 0.49);
        return new Fit(reliable, level / (1 + slopeFit), speed / (1 + slopeFit), confidence, set.Count, valid.Count, scenes.Count, residual, scenes, varies && reliable, spread);
    }

    internal sealed record Segment(double From, double To, double Delay, int Count);

    /// <summary>
    /// Runs of consecutive scenes sharing one delay. Two or more runs covering most of the scenes
    /// mean the tracks are different edits: each stretch is in sync with its own delay.
    /// </summary>
    internal static List<Segment> Segments(IReadOnlyList<Scene> scenes)
    {
        var valid = scenes.Where(scene => scene.Valid).OrderBy(scene => scene.Time).ToList();
        var runs = new List<List<Scene>>();
        foreach (var scene in valid)
        {
            if (runs.Count > 0 && Math.Abs(runs[^1].Average(s => s.Delay) - scene.Delay) <= 0.3) runs[^1].Add(scene);
            else runs.Add(new List<Scene> { scene });
        }
        var solid = runs.Where(run => run.Count >= 2).ToList();
        if (solid.Count < 2 || solid.Sum(run => run.Count) < Math.Max(4, valid.Count * 0.6)) return new List<Segment>();
        return solid.Select(run => new Segment(run[0].Time, run[^1].Time, run.Average(s => s.Delay), run.Count)).ToList();
    }

    private static async Task<Features?> ExtractFeaturesAsync(string ffmpeg, string path, int ordinal, double start, double seconds, CancellationToken ct)
    {
        if (seconds < 8) return null;
        var startInfo = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // Channel 0: mono downmix. Channel 1: left minus right of the stereo downmix, where the
        // centre channel (dialogue) cancels. first_pts=0 keeps a late-starting track in place.
        string graph = $"[0:a:{ordinal}]aresample=async=1:min_hard_comp=0.02:first_pts=0,asplit[a][b];" +
                       "[a]aformat=sample_fmts=s16:channel_layouts=mono[m];" +
                       "[b]aformat=sample_fmts=s16:channel_layouts=stereo,pan=mono|c0=0.5*c0-0.5*c1[s];" +
                       "[m][s]amerge=inputs=2[o]";
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (start > 0) { arguments.Add("-ss"); arguments.Add(start.ToString("0.###", CultureInfo.InvariantCulture)); }
        arguments.AddRange(new[]
        {
            "-i", path, "-filter_complex", graph, "-map", "[o]", "-vn", "-sn", "-dn",
            "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"
        });
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg");
        using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        using var bytes = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(bytes, ct);
        Task<string> errors = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, process.WaitForExitAsync(ct)).ConfigureAwait(false);
        string stderr = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Trim());
        byte[] raw = bytes.ToArray();
        int samples = raw.Length / 4;
        if (samples < SampleRate * 8) return null;
        var mix = new float[samples];
        var side = new float[samples];
        double mixEnergy = 0, sideEnergy = 0;
        for (int i = 0; i < samples; i++)
        {
            mix[i] = BitConverter.ToInt16(raw, i * 4) / 32768f;
            side[i] = BitConverter.ToInt16(raw, i * 4 + 2) / 32768f;
            mixEnergy += mix[i] * mix[i];
            sideEnergy += side[i] * side[i];
        }
        if (mixEnergy / samples < 1e-9) return null; // digital silence
        // A mono (or dual-mono) track has nothing left once left and right are subtracted.
        bool hasSide = sideEnergy > mixEnergy * 0.002;
        float[][] mixBands = OnsetBands(mix);
        return new Features(mixBands, hasSide ? OnsetBands(side) : null, mixBands[0].Length);
    }

    /// <summary>Per band, how much the level rises from one 10 ms frame to the next: attacks of notes, hits, cuts.</summary>
    private static float[][] OnsetBands(float[] pcm)
    {
        int frames = Math.Max(0, (pcm.Length - FftSize) / Hop + 1);
        var level = new float[BandCount][];
        for (int band = 0; band < BandCount; band++) level[band] = new float[frames];
        var window = new float[FftSize];
        for (int i = 0; i < FftSize; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        Parallel.For(0, frames, () => (Re: new float[FftSize], Im: new float[FftSize]), (frame, _, buffers) =>
        {
            int offset = frame * Hop;
            for (int i = 0; i < FftSize; i++) { buffers.Re[i] = pcm[offset + i] * window[i]; buffers.Im[i] = 0; }
            Fft(buffers.Re, buffers.Im);
            for (int band = 0; band < BandCount; band++)
            {
                double energy = 0;
                for (int bin = BandEdges[band]; bin < BandEdges[band + 1]; bin++) energy += buffers.Re[bin] * buffers.Re[bin] + buffers.Im[bin] * buffers.Im[bin];
                level[band][frame] = (float)Math.Log(1 + 2000 * energy);
            }
            return buffers;
        }, _ => { });

        var result = new float[BandCount][];
        for (int band = 0; band < BandCount; band++)
        {
            float[] source = level[band], rise = new float[frames], smooth = new float[frames];
            for (int i = 2; i < frames; i++) rise[i] = Math.Max(0, source[i] - source[i - 2]);
            for (int i = 1; i < frames - 1; i++) smooth[i] = 0.25f * rise[i - 1] + 0.5f * rise[i] + 0.25f * rise[i + 1];
            result[band] = smooth;
        }
        return result;
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            float wRe = (float)Math.Cos(angle), wIm = (float)Math.Sin(angle);
            for (int i = 0; i < n; i += length)
            {
                float curRe = 1, curIm = 0;
                for (int k = 0; k < length / 2; k++)
                {
                    int u = i + k, v = i + k + length / 2;
                    float tRe = re[v] * curRe - im[v] * curIm, tIm = re[v] * curIm + im[v] * curRe;
                    re[v] = re[u] - tRe; im[v] = im[u] - tIm;
                    re[u] += tRe; im[u] += tIm;
                    float nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
