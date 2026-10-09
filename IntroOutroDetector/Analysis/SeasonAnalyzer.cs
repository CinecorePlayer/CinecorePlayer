namespace IntroOutroDetector.Analysis;

using System.Collections.Concurrent;
using System.Diagnostics;
using IntroOutroDetector.Audio;
using IntroOutroDetector.Models;

/// <summary>
/// Orchestrates the full season analysis pipeline:
///   1. Discover video files
///   2. Probe durations
///   3. Extract fingerprints in parallel
///   4. Match common intro/outro segments
///   5. Return per-episode results
/// </summary>
public class SeasonAnalyzer
{
    private readonly AnalysisOptions _options;
    private readonly AudioExtractor  _extractor;

    public SeasonAnalyzer(AnalysisOptions options)
    {
        _options   = options;
        _extractor = new AudioExtractor(options);
    }

    public async Task<SeasonResult> AnalyzeAsync(
        string seasonPath,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // ── 1. Discover episodes ───────────────────────────────────────────
        var episodes = DiscoverEpisodes(seasonPath);
        if (episodes.Count < 2)
            throw new InvalidOperationException(
                $"Found only {episodes.Count} episode(s) in '{seasonPath}'. Need at least 2.");

        Console.WriteLine($"Found {episodes.Count} episodes.");

        // ── 2. Probe durations ─────────────────────────────────────────────
        Console.WriteLine("Probing episode metadata...");
        await GetDurationsAsync(episodes, ct);

        var chapterOutroMatch = _options.DetectOutro
            ? BuildChapterOutroMatch(episodes)
            : null;

        // ── 3. Extract fingerprints in parallel ────────────────────────────
        var introData = new ConcurrentDictionary<int, (uint[] Fp, double RegionStart, double Duration, double SecondsPerFrame)>();
        var outroData = new ConcurrentDictionary<int, (uint[] Fp, double RegionStart, double Duration, double SecondsPerFrame)>();

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
            CancellationToken      = ct
        };

        Console.WriteLine($"Extracting fingerprints (×{_options.MaxDegreeOfParallelism} threads)...");
        if (chapterOutroMatch?.ConsensusSegment != null)
            Console.WriteLine("  Using chapter metadata for outro candidates; skipping outro audio fingerprinting.");
        var progress = 0;

        await Parallel.ForEachAsync(episodes, parallelOpts, async (episode, innerCt) =>
        {
            try
            {
                await ProcessEpisodeAsync(episode, introData, outroData, chapterOutroMatch != null, innerCt);
            }
            catch (Exception ex) when (!innerCt.IsCancellationRequested)
            {
                Console.Error.WriteLine($"  [WARN] Skipping {episode.FileName}: {ex.Message}");
                return;
            }

            int done = Interlocked.Increment(ref progress);
            Console.WriteLine($"  [{done}/{episodes.Count}] {episode.FileName}");
        });

        // ── 4. Match segments ──────────────────────────────────────────────
        Console.WriteLine("Matching common segments...");
        var matcher = new SegmentMatcher(_options);

        SegmentMatchResult? introMatch = null;
        SegmentMatchResult? outroMatch = null;

        if (_options.DetectIntro && introData.Count >= 2)
        {
            var fpList = introData
                .OrderBy(kv => kv.Key)
                .Select(kv => (kv.Key, kv.Value.Fp, kv.Value.RegionStart, kv.Value.Duration, kv.Value.SecondsPerFrame))
                .ToList();

            introMatch = matcher.FindCommonSegment(
                fpList,
                _options.MinIntroLengthSec,
                _options.MaxIntroLengthSec,
                reversed: false);
        }

        if (_options.DetectOutro && chapterOutroMatch?.ConsensusSegment != null)
        {
            outroMatch = chapterOutroMatch;
        }
        else if (_options.DetectOutro && outroData.Count >= 2)
        {
            var fpList = outroData
                .OrderBy(kv => kv.Key)
                .Select(kv => (kv.Key, kv.Value.Fp, kv.Value.RegionStart, kv.Value.Duration, kv.Value.SecondsPerFrame))
                .ToList();

            outroMatch = matcher.FindCommonSegment(
                fpList,
                _options.MinOutroLengthSec,
                _options.MaxOutroLengthSec,
                reversed: true);
        }
        // ── 5. Build per-episode results ───────────────────────────────────
        var episodeResults = episodes
            .Select(ep => BuildEpisodeResult(ep, introMatch, outroMatch))
            .ToList();

        sw.Stop();

        return new SeasonResult
        {
            SeasonPath       = seasonPath,
            Episodes         = episodeResults,
            ConsensusIntro   = introMatch?.ConsensusSegment,
            ConsensusOutro   = outroMatch?.ConsensusSegment,
            AnalysisDuration = sw.Elapsed
        };
    }

    // ──────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────

    private async Task ProcessEpisodeAsync(
        EpisodeInfo episode,
        ConcurrentDictionary<int, (uint[], double, double, double)> introData,
        ConcurrentDictionary<int, (uint[], double, double, double)> outroData,
        bool skipOutroAudio,
        CancellationToken ct)
    {
        double dur = episode.DurationSeconds;
        if (dur <= 0)
        {
            Console.Error.WriteLine($"  [WARN] Could not get duration for {episode.FileName}");
            return;
        }

        // ── Intro region ──────────────────────────────────────────────────
        if (_options.DetectIntro)
        {
            try
            {
                var (introStart, introWindow) = GetIntroSearchRegion(episode);
                var (fp, secondsPerFrame) = await _extractor.GetFingerprintAsync(
                    episode.FilePath,
                    introStart,
                    introWindow,
                    ct);
                introData[episode.Index] = (fp, introStart, dur, secondsPerFrame);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"  [WARN] Intro skipped for {episode.FileName}: {ex.Message}");
            }
        }

        // ── Outro region ──────────────────────────────────────────────────
        if (_options.DetectOutro && !skipOutroAudio)
        {
            try
            {
                double outroWindow = Math.Min(_options.OutroSearchMinutes * 60.0, dur * 0.4);
                double outroStart  = dur - outroWindow;
                var (fp, secondsPerFrame) = await _extractor.GetFingerprintAsync(episode.FilePath, outroStart, outroWindow, ct);
                // Reverse the fingerprint so frame 0 = episode end (simplifies alignment)
                outroData[episode.Index] = (AudioFingerprinter.Reverse(fp), outroStart, dur, secondsPerFrame);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"  [WARN] Outro skipped for {episode.FileName}: {ex.Message}");
            }
        }
    }

    private static EpisodeResult BuildEpisodeResult(
        EpisodeInfo ep,
        SegmentMatchResult? introMatch,
        SegmentMatchResult? outroMatch)
    {
        var result = new EpisodeResult { Episode = ep };

        if (introMatch?.ConsensusSegment != null)
        {
            if (introMatch.PerEpisodeSegments.TryGetValue(ep.Index, out var exactIntro))
            {
                result.Intro = exactIntro;
            }
            else
            {
                double adj = introMatch.EpisodeStartOffsetSeconds.GetValueOrDefault(ep.Index, double.NaN);
                if (!double.IsNaN(adj))
                {
                    result.Intro = new TimeSegment
                    {
                        StartSeconds = Math.Max(0, introMatch.ConsensusSegment.StartSeconds + adj),
                        EndSeconds   = Math.Max(0, introMatch.ConsensusSegment.EndSeconds   + adj)
                    };
                }
            }
        }

        if (outroMatch?.ConsensusSegment != null)
        {
            if (outroMatch.PerEpisodeSegments.TryGetValue(ep.Index, out var exactOutro))
            {
                result.Outro = exactOutro;
            }
            else
            {
                double adj = outroMatch.EpisodeStartOffsetSeconds.GetValueOrDefault(ep.Index, double.NaN);
                if (!double.IsNaN(adj))
                {
                    result.Outro = new TimeSegment
                    {
                        StartSeconds = Math.Max(0, outroMatch.ConsensusSegment.StartSeconds + adj),
                        EndSeconds   = Math.Max(0, outroMatch.ConsensusSegment.EndSeconds   + adj)
                    };
                }
            }
        }

        result.Intro = ClampEpisodeSegment(result.Intro, ep.DurationSeconds);
        result.Outro = ClampEpisodeSegment(result.Outro, ep.DurationSeconds);
        return result;
    }

    private static TimeSegment? ClampEpisodeSegment(TimeSegment? segment, double episodeDuration)
    {
        if (segment == null || episodeDuration <= 0)
            return segment;

        double start = Math.Clamp(segment.StartSeconds, 0, episodeDuration);
        double end = Math.Clamp(segment.EndSeconds, 0, episodeDuration);
        if (end <= start + 0.5)
            return null;

        return new TimeSegment
        {
            StartSeconds = start,
            EndSeconds = end
        };
    }

    private List<EpisodeInfo> DiscoverEpisodes(string path)
    {
        var extensions = new HashSet<string>(
            _options.VideoExtensions,
            StringComparer.OrdinalIgnoreCase);

        var files = Directory
            .EnumerateFiles(path)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return files
            .Select((f, i) => new EpisodeInfo
            {
                Index    = i,
                FilePath = f,
                FileName = Path.GetFileName(f)
            })
            .ToList();
    }

    private async Task GetDurationsAsync(List<EpisodeInfo> episodes, CancellationToken ct)
    {
        // One ffprobe per file (it was three), a few at a time: on a network drive every open costs.
        var probeOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(_options.MaxDegreeOfParallelism * 2, 2, 6),
            CancellationToken = ct
        };
        await Parallel.ForEachAsync(episodes, probeOptions, async (ep, innerCt) =>
        {
            try
            {
                (ep.DurationSeconds, ep.Chapters) = await _extractor.ProbeAsync(ep.FilePath, innerCt);
            }
            catch (Exception ex) when (!innerCt.IsCancellationRequested)
            {
                Console.Error.WriteLine($"  [WARN] Could not probe {ep.FileName}: {ex.Message}");
            }
            if (_options.Verbose)
                Console.WriteLine($"  {ep.FileName}: {ep.DurationFormatted}, {ep.Chapters.Count} chapters");
        });
    }

    private SegmentMatchResult? BuildChapterOutroMatch(List<EpisodeInfo> episodes)
    {
        var candidates = episodes
            .Select(ep => (Episode: ep, Segment: TryGetOutroChapter(ep)))
            .Where(item => item.Segment != null)
            .Select(item => (item.Episode, Segment: item.Segment!))
            .ToList();

        int required = Math.Max(2, (int)Math.Ceiling(episodes.Count * _options.MinEpisodeMatchRatio));
        if (candidates.Count < required)
            return null;

        var lengths = candidates
            .Select(candidate => candidate.Segment.LengthSeconds)
            .OrderBy(length => length)
            .ToArray();
        double medianLength = lengths[lengths.Length / 2];

        var result = new SegmentMatchResult
        {
            ConsensusSegment = new TimeSegment
            {
                StartSeconds = Math.Max(0, Median(candidates.Select(c => c.Segment.StartSeconds))),
                EndSeconds = Math.Max(0, Median(candidates.Select(c => c.Segment.StartSeconds)) + medianLength)
            }
        };

        foreach (var (episode, segment) in candidates)
        {
            result.PerEpisodeSegments[episode.Index] = segment;
            result.EpisodeStartOffsetSeconds[episode.Index] =
                segment.StartSeconds - result.ConsensusSegment.StartSeconds;
        }

        return result;
    }

    private TimeSegment? TryGetOutroChapter(EpisodeInfo episode)
    {
        if (episode.Chapters.Count == 0 || episode.DurationSeconds <= 0)
            return null;

        for (int i = episode.Chapters.Count - 1; i >= 0; i--)
        {
            var chapter = episode.Chapters[i];
            double start = Math.Clamp(chapter.StartSeconds, 0, episode.DurationSeconds);
            double end = Math.Clamp(chapter.EndSeconds, 0, episode.DurationSeconds);
            double length = end - start;
            double tailLength = episode.DurationSeconds - start;

            if (tailLength > _options.MaxOutroLengthSec + 60)
                return null;

            if (length < _options.MinOutroLengthSec)
                continue;

            if (length > _options.MaxOutroLengthSec)
                continue;

            return new TimeSegment
            {
                StartSeconds = start,
                EndSeconds = end
            };
        }

        return null;
    }

    private (double start, double duration) GetIntroSearchRegion(EpisodeInfo episode)
    {
        double defaultDuration = Math.Min(_options.IntroSearchMinutes * 60.0, episode.DurationSeconds * 0.6);

        if (episode.Chapters.Count == 0)
            return (0, defaultDuration);

        double firstBoundary = episode.Chapters[0].EndSeconds;
        if (firstBoundary < _options.MinIntroLengthSec)
            return (0, defaultDuration);

        double maxUsefulBoundary = Math.Max(_options.IntroSearchMinutes * 60.0, 12 * 60.0);
        if (firstBoundary > maxUsefulBoundary)
            return (0, defaultDuration);

        double end = Math.Min(episode.DurationSeconds, firstBoundary + 5);
        double start = Math.Max(0, end - _options.ChapterIntroLookbackSeconds);
        double duration = Math.Max(_options.MinIntroLengthSec, end - start);

        return (start, duration);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
