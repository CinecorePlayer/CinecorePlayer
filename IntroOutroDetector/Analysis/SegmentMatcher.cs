namespace IntroOutroDetector.Analysis;

using System.Numerics;
using IntroOutroDetector.Audio;
using IntroOutroDetector.Models;

/// <summary>
/// Returned by <see cref="SegmentMatcher.FindCommonSegment"/>.
/// </summary>
public class SegmentMatchResult
{
    /// <summary>Consensus segment timing (absolute seconds in the episode).</summary>
    public TimeSegment? ConsensusSegment { get; set; }

    /// <summary>
    /// Per-episode start-time offset (in seconds) relative to the consensus.
    /// Positive = episode's segment starts later than consensus.
    /// Missing key = episode did not match well (use consensus as fallback).
    /// </summary>
    public Dictionary<int, double> EpisodeStartOffsetSeconds { get; set; } = new();

    /// <summary>Exact per-episode segment timings when the detector has them.</summary>
    public Dictionary<int, TimeSegment> PerEpisodeSegments { get; set; } = new();
}

/// <summary>
/// Local-sequence common segment detector.
///
/// Unlike a global cross-correlation, this searches for a common run anywhere
/// inside each search window. That matters for TV episodes where the intro can
/// start at very different timestamps from episode to episode.
/// </summary>
public class SegmentMatcher
{
    private readonly AnalysisOptions _options;

    private const int HammingThreshold = 10; // out of 32 bits
    private const int MaxGapFrames = 4;
    private const int TopMatchesPerEpisode = 8;

    public SegmentMatcher(AnalysisOptions options) => _options = options;

    public SegmentMatchResult FindCommonSegment(
        IReadOnlyList<(int EpisodeIndex, uint[] Fingerprint, double RegionStartSeconds, double EpisodeDuration, double SecondsPerFrame)> data,
        double minLengthSec,
        double maxLengthSec,
        bool reversed = false)
    {
        var result = new SegmentMatchResult();
        if (data.Count < 2) return result;

        int refDataIdx = data.Count > 1 ? 1 : 0;
        var (refEpIdx, refFp, refRegionStart, refDuration, _) = data[refDataIdx];

        double secondsPerFrame = Math.Max(data[refDataIdx].SecondsPerFrame, 0.001);
        int minFrames = Math.Max(1, (int)Math.Round(minLengthSec / secondsPerFrame));
        int maxFrames = Math.Max(minFrames, (int)Math.Round(maxLengthSec / secondsPerFrame));
        int refLen = refFp.Length;
        if (refLen < minFrames) return result;

        var frameVotes = new float[refLen];
        var matchesByEpisode = new Dictionary<int, List<LocalMatch>>();

        foreach (var (epIdx, fp, _, _, _) in data)
        {
            if (epIdx == refEpIdx || fp.Length < minFrames) continue;

            var matches = FindLocalMatches(refFp, fp, minFrames, maxFrames);
            if (matches.Count == 0) continue;

            matchesByEpisode[epIdx] = matches;

            var episodeVotes = new float[refLen];
            foreach (var match in matches)
            {
                float quality = match.Score / Math.Max(1, match.RefEnd - match.RefStart);
                for (int i = match.RefStart; i < match.RefEnd; i++)
                    episodeVotes[i] = Math.Max(episodeVotes[i], quality);
            }

            for (int i = 0; i < refLen; i++)
            {
                if (episodeVotes[i] > 0)
                    frameVotes[i] += episodeVotes[i];
            }
        }

        int requiredEpisodes = Math.Max(2, (int)Math.Ceiling(data.Count * _options.MinEpisodeMatchRatio));
        float voteThreshold = Math.Max(0.75f, requiredEpisodes - 1.25f);

        var smoothed = SmoothVotes(frameVotes, radius: 2);
        var seg = FindBestContiguousSegment(smoothed, voteThreshold, minFrames, maxFrames);
        if (seg == null) return result;

        var (segStart, segEnd) = seg.Value;

        result.ConsensusSegment = ToAbsoluteSegment(
            segStart,
            segEnd,
            refRegionStart,
            refDuration,
            reversed,
            secondsPerFrame);

        result.EpisodeStartOffsetSeconds[refEpIdx] = 0.0;
        result.PerEpisodeSegments[refEpIdx] = result.ConsensusSegment;

        foreach (var (epIdx, matches) in matchesByEpisode)
        {
            var best = matches
                .Where(m => Overlap(m.RefStart, m.RefEnd, segStart, segEnd) >= minFrames * 0.6)
                .OrderByDescending(m => Overlap(m.RefStart, m.RefEnd, segStart, segEnd))
                .ThenByDescending(m => m.Score)
                .FirstOrDefault();

            if (best == null) continue;

            var epData = data.First(d => d.EpisodeIndex == epIdx);
            var epSegment = ToAbsoluteSegment(
                best.EpStart,
                best.EpEnd,
                epData.RegionStartSeconds,
                epData.EpisodeDuration,
                reversed,
                Math.Max(epData.SecondsPerFrame, 0.001));

            result.EpisodeStartOffsetSeconds[epIdx] =
                epSegment.StartSeconds - result.ConsensusSegment.StartSeconds;
            result.PerEpisodeSegments[epIdx] = epSegment;
        }

        return result;
    }

    private static List<LocalMatch> FindLocalMatches(
        uint[] refFp,
        uint[] fp,
        int minFrames,
        int maxFrames)
    {
        var matches = new List<LocalMatch>();

        for (int delta = -(refFp.Length - 1); delta <= fp.Length - 1; delta++)
        {
            int refStart = Math.Max(0, -delta);
            int epStart = refStart + delta;
            int len = Math.Min(refFp.Length - refStart, fp.Length - epStart);
            if (len < minFrames) continue;

            int runRefStart = -1;
            int runEpStart = -1;
            int lastGoodRef = -1;
            int lastGoodEp = -1;
            int gap = 0;
            float score = 0;

            for (int i = 0; i < len; i++)
            {
                int r = refStart + i;
                int e = epStart + i;
                int hamming = BitOperations.PopCount(refFp[r] ^ fp[e]);

                if (hamming <= HammingThreshold)
                {
                    if (runRefStart < 0)
                    {
                        runRefStart = r;
                        runEpStart = e;
                    }

                    lastGoodRef = r;
                    lastGoodEp = e;
                    gap = 0;
                    score += 1f - hamming / (float)(HammingThreshold + 1);
                }
                else if (runRefStart >= 0)
                {
                    gap++;
                    if (gap > MaxGapFrames || r - runRefStart >= maxFrames)
                    {
                        AddMatch(matches, runRefStart, lastGoodRef + 1, runEpStart, lastGoodEp + 1, score, minFrames);
                        runRefStart = -1;
                        runEpStart = -1;
                        lastGoodRef = -1;
                        lastGoodEp = -1;
                        gap = 0;
                        score = 0;
                    }
                }
            }

            if (runRefStart >= 0)
                AddMatch(matches, runRefStart, lastGoodRef + 1, runEpStart, lastGoodEp + 1, score, minFrames);
        }

        return matches
            .Where(m => m.Score >= minFrames * 0.45f)
            .OrderByDescending(m => m.Score)
            .Take(TopMatchesPerEpisode)
            .OrderBy(m => m.RefStart)
            .ToList();
    }

    private static void AddMatch(
        List<LocalMatch> matches,
        int refStart,
        int refEnd,
        int epStart,
        int epEnd,
        float score,
        int minFrames)
    {
        if (refEnd - refStart >= minFrames)
            matches.Add(new LocalMatch(refStart, refEnd, epStart, epEnd, score));
    }

    private static TimeSegment ToAbsoluteSegment(
        int startFrame,
        int endFrame,
        double regionStart,
        double episodeDuration,
        bool reversed,
        double secondsPerFrame)
    {
        if (!reversed)
        {
            return ClampSegment(
                regionStart + startFrame * secondsPerFrame,
                regionStart + endFrame * secondsPerFrame,
                episodeDuration);
        }

        return ClampSegment(
            episodeDuration - endFrame * secondsPerFrame,
            episodeDuration - startFrame * secondsPerFrame,
            episodeDuration);
    }

    private static TimeSegment ClampSegment(double startSeconds, double endSeconds, double episodeDuration)
    {
        double duration = Math.Max(0, episodeDuration);
        double start = Math.Clamp(startSeconds, 0, duration);
        double end = Math.Clamp(endSeconds, 0, duration);

        return new TimeSegment
        {
            StartSeconds = Math.Min(start, end),
            EndSeconds = Math.Max(start, end)
        };
    }

    private static int Overlap(int startA, int endA, int startB, int endB) =>
        Math.Max(0, Math.Min(endA, endB) - Math.Max(startA, startB));

    private static float[] SmoothVotes(float[] votes, int radius)
    {
        int n = votes.Length;
        var output = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            int count = 0;
            for (int j = Math.Max(0, i - radius); j <= Math.Min(n - 1, i + radius); j++)
            {
                sum += votes[j];
                count++;
            }

            output[i] = count > 0 ? sum / count : 0f;
        }

        return output;
    }

    private static (int start, int end)? FindBestContiguousSegment(
        float[] votes,
        float threshold,
        int minFrames,
        int maxFrames)
    {
        int n = votes.Length;
        int? bestStart = null;
        int bestEnd = 0;
        float bestScore = 0f;

        int i = 0;
        while (i < n)
        {
            if (votes[i] < threshold)
            {
                i++;
                continue;
            }

            int runStart = i;
            float runScore = 0f;
            int gapLen = 0;

            while (i < n)
            {
                if (votes[i] >= threshold)
                {
                    runScore += votes[i];
                    gapLen = 0;
                }
                else
                {
                    gapLen++;
                    if (gapLen > MaxGapFrames) break;
                }

                i++;
                if (i - runStart >= maxFrames) break;
            }

            int runEnd = i - gapLen;
            int runLen = runEnd - runStart;

            if (runLen >= minFrames && runScore > bestScore)
            {
                bestScore = runScore;
                bestStart = runStart;
                bestEnd = runEnd;
            }
        }

        return bestStart.HasValue ? (bestStart.Value, bestEnd) : null;
    }

    private sealed record LocalMatch(int RefStart, int RefEnd, int EpStart, int EpEnd, float Score);
}
