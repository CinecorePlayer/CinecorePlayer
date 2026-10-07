using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Cinecore.AudioVideoSync;

internal static class SyncDetector
{
    private const int FrameMilliseconds = 20;
    private const int ConsensusToleranceFrames = 4; // 80 ms

    private sealed record CandidateWindow(
        int StartFrame,
        double DelayFrames,
        double Correlation,
        double Prominence,
        double Weight);

    public static SyncAnalysisResult Analyze(
        short[] referencePcm,
        short[] targetPcm,
        AnalysisOptions options, CancellationToken cancellationToken = default)
    {
        float[] reference = BuildActivityFeatures(referencePcm);
        float[] target = BuildActivityFeatures(targetPcm);
        int commonFrames = Math.Min(reference.Length, target.Length);
        int maximumLagFrames = Math.Max(1,
            (int)Math.Round(options.MaximumOffsetSeconds * 1000 / FrameMilliseconds));
        int windowFrames = Math.Min(commonFrames, 60_000 / FrameMilliseconds);
        int hopFrames = 45_000 / FrameMilliseconds;

        if (commonFrames < 15_000 / FrameMilliseconds)
            throw new InvalidOperationException("Servono almeno 15 secondi comuni fra le due tracce.");

        var candidates = new List<CandidateWindow>();
        int lastStart = Math.Max(0, commonFrames - windowFrames);
        for (int start = 0; start <= lastStart; start += hopFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateWindow? candidate = AnalyzeWindow(
                reference, target, start, windowFrames, maximumLagFrames);
            if (candidate != null)
                candidates.Add(candidate);

            if (start < lastStart && start + hopFrames > lastStart)
                start = lastStart - hopFrames;
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "Le tracce non contengono abbastanza variazioni utili per stimare il sincronismo.");

        var viable = candidates
            .Where(c => c.Correlation >= 0.06 && c.Prominence >= 0.002)
            .ToList();
        if (viable.Count == 0)
            viable.Add(candidates.OrderByDescending(c => c.Correlation).First());

        CandidateWindow clusterCenter = viable
            .OrderByDescending(center => viable
                .Where(other => Math.Abs(other.DelayFrames - center.DelayFrames) <= ConsensusToleranceFrames)
                .Sum(other => other.Weight))
            .First();

        var supporting = viable
            .Where(c => Math.Abs(c.DelayFrames - clusterCenter.DelayFrames) <= ConsensusToleranceFrames)
            .ToList();
        double supportingWeight = supporting.Sum(c => c.Weight);
        double totalWeight = viable.Sum(c => c.Weight);
        double consensusDelayFrames = supportingWeight > 0
            ? supporting.Sum(c => c.DelayFrames * c.Weight) / supportingWeight
            : clusterCenter.DelayFrames;
        int detectedDelayMs = (int)Math.Round(consensusDelayFrames * FrameMilliseconds);

        double agreement = totalWeight <= 0 ? 0 : supportingWeight / totalWeight;
        double meanCorrelation = WeightedMean(supporting, c => c.Correlation);
        double meanProminence = WeightedMean(supporting, c => c.Prominence);
        double correlationScore = Clamp01((meanCorrelation - 0.05) / 0.40);
        double prominenceScore = Clamp01((meanProminence - 0.002) / 0.055);
        double windowSupportScore = Clamp01(supporting.Count / 4.0);
        double confidence = Clamp01(
            agreement * 0.42 +
            correlationScore * 0.30 +
            prominenceScore * 0.16 +
            windowSupportScore * 0.12);

        // Agreement alone is not evidence: unrelated low-correlation windows
        // must never become high confidence just because one candidate wins.
        if (meanCorrelation < .18 || meanProminence < .008) confidence = Math.Min(confidence, .49);
        if (supporting.Count < 2) confidence = Math.Min(confidence, .69);
        ConfidenceLevel level = confidence >= 0.72
            ? ConfidenceLevel.High
            : confidence >= 0.50 ? ConfidenceLevel.Medium : ConfidenceLevel.Low;

        var evidence = candidates
            .Select(c => new WindowEvidence(
                options.StartSeconds + c.StartFrame * FrameMilliseconds / 1000.0,
                (int)Math.Round(c.DelayFrames * FrameMilliseconds),
                Math.Round(c.Correlation, 4),
                Math.Round(c.Prominence, 4),
                supporting.Contains(c)))
            .ToArray();

        return new SyncAnalysisResult
        {
            Input = options.Input,
            ReferenceAudioOrdinal = options.ReferenceAudioOrdinal,
            TargetAudioOrdinal = options.TargetAudioOrdinal,
            DetectedTargetDelayMs = detectedDelayMs,
            RecommendedCorrectionMs = -detectedDelayMs,
            Confidence = Math.Round(confidence, 4),
            ConfidenceLevel = level,
            SupportingWindows = supporting.Count,
            AnalyzedWindows = candidates.Count,
            AnalysisStartSeconds = options.StartSeconds,
            AnalysisDurationSeconds = commonFrames * FrameMilliseconds / 1000.0,
            Method = "multi-window voice/activity-envelope correlation v1",
            Windows = evidence
        };
    }

    private static CandidateWindow? AnalyzeWindow(
        float[] reference,
        float[] target,
        int start,
        int length,
        int maximumLagFrames)
    {
        double refMean = 0;
        for (int i = start; i < start + length; i++)
            refMean += reference[i];
        refMean /= length;

        double refVariance = 0;
        for (int i = start; i < start + length; i++)
        {
            double delta = reference[i] - refMean;
            refVariance += delta * delta;
        }
        if (refVariance / length < 0.004)
            return null;

        int lagCount = maximumLagFrames * 2 + 1;
        var scores = new double[lagCount];
        double bestScore = double.NegativeInfinity;
        int bestLag = 0;

        for (int lag = -maximumLagFrames; lag <= maximumLagFrames; lag++)
        {
            int from = Math.Max(start, -lag);
            int to = Math.Min(start + length, target.Length - lag);
            int count = to - from;
            if (count < length * 0.80)
            {
                scores[lag + maximumLagFrames] = double.NegativeInfinity;
                continue;
            }

            double meanA = 0;
            double meanB = 0;
            for (int i = from; i < to; i++)
            {
                meanA += reference[i];
                meanB += target[i + lag];
            }
            meanA /= count;
            meanB /= count;

            double covariance = 0;
            double varianceA = 0;
            double varianceB = 0;
            for (int i = from; i < to; i++)
            {
                double a = reference[i] - meanA;
                double b = target[i + lag] - meanB;
                covariance += a * b;
                varianceA += a * a;
                varianceB += b * b;
            }

            double score = varianceA <= 1e-9 || varianceB <= 1e-9
                ? double.NegativeInfinity
                : covariance / Math.Sqrt(varianceA * varianceB);
            scores[lag + maximumLagFrames] = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (!double.IsFinite(bestScore))
            return null;

        double secondScore = double.NegativeInfinity;
        for (int lag = -maximumLagFrames; lag <= maximumLagFrames; lag++)
        {
            if (Math.Abs(lag - bestLag) <= 5)
                continue;
            secondScore = Math.Max(secondScore, scores[lag + maximumLagFrames]);
        }
        if (!double.IsFinite(secondScore))
            secondScore = bestScore;

        double prominence = Math.Max(0, bestScore - secondScore);
        double weight = Math.Max(0.001, bestScore + 0.10) * Math.Max(0.002, prominence + 0.004);

        // A 20 ms frame is close to the lip-sync threshold: refine the peak between
        // frames with a parabola through the best lag and its two neighbours.
        double refinedLag = bestLag;
        int bestIndex = bestLag + maximumLagFrames;
        if (bestIndex > 0 && bestIndex < lagCount - 1)
        {
            double left = scores[bestIndex - 1], right = scores[bestIndex + 1];
            double curvature = left - 2 * bestScore + right;
            if (double.IsFinite(left) && double.IsFinite(right) && curvature < -1e-9)
                refinedLag += Math.Clamp(0.5 * (left - right) / curvature, -0.5, 0.5);
        }
        return new CandidateWindow(start, refinedLag, bestScore, prominence, weight);
    }

    private static float[] BuildActivityFeatures(short[] pcm)
    {
        int samplesPerFrame = FfmpegAudioExtractor.SampleRate * FrameMilliseconds / 1000;
        int frameCount = pcm.Length / samplesPerFrame;
        var energy = new float[frameCount];

        for (int frame = 0; frame < frameCount; frame++)
        {
            int from = frame * samplesPerFrame;
            double sumSquares = 0;
            for (int i = 0; i < samplesPerFrame; i++)
            {
                double sample = pcm[from + i] / 32768.0;
                sumSquares += sample * sample;
            }
            double rms = Math.Sqrt(sumSquares / samplesPerFrame);
            energy[frame] = (float)(20 * Math.Log10(rms + 1e-7));
        }

        float[] sorted = (float[])energy.Clone();
        Array.Sort(sorted);
        float low = Percentile(sorted, 0.28);
        float high = Percentile(sorted, 0.82);
        float span = Math.Max(5f, high - low);
        var raw = new float[frameCount];
        float previous = frameCount > 0 ? energy[0] : low;
        for (int i = 0; i < frameCount; i++)
        {
            float activity = Math.Clamp((energy[i] - low) / span, 0f, 1f);
            float onset = Math.Clamp((energy[i] - previous) / (span * 0.45f), 0f, 1f);
            raw[i] = activity * 0.78f + onset * 0.22f;
            previous = energy[i];
        }

        // 100 ms di smoothing riducono le differenze fonetiche fra doppiaggi ma
        // conservano pause, attacchi e cambi scena utili alla correlazione.
        var smoothed = new float[frameCount];
        const int radius = 2;
        for (int i = 0; i < frameCount; i++)
        {
            int from = Math.Max(0, i - radius);
            int to = Math.Min(frameCount - 1, i + radius);
            float sum = 0;
            for (int j = from; j <= to; j++)
                sum += raw[j];
            smoothed[i] = sum / (to - from + 1);
        }
        return smoothed;
    }

    private static float Percentile(float[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;
        int index = (int)Math.Round(Math.Clamp(percentile, 0, 1) * (sorted.Length - 1));
        return sorted[index];
    }

    private static double WeightedMean(
        IReadOnlyCollection<CandidateWindow> windows,
        Func<CandidateWindow, double> selector)
    {
        double weights = windows.Sum(c => c.Weight);
        return weights <= 0 ? 0 : windows.Sum(c => selector(c) * c.Weight) / weights;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}
