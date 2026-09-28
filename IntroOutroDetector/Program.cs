using IntroOutroDetector.Analysis;
using IntroOutroDetector.Audio;
using IntroOutroDetector.Models;
using IntroOutroDetector.Output;

// ── Banner ─────────────────────────────────────────────────────────────────
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║   TV Intro & Outro Detector  v1.0                ║");
Console.WriteLine("║   Fast audio fingerprint matching — .NET 8       ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();

// ── Argument parsing ───────────────────────────────────────────────────────
var options    = new AnalysisOptions();
string? seasonPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--ffmpeg" when i + 1 < args.Length:
            options.FfmpegPath = args[++i]; break;

        case "--ffprobe" when i + 1 < args.Length:
            options.FfprobePath = args[++i]; break;

        case "--audio-stream" when i + 1 < args.Length:
            options.AudioStream = args[++i]; break;

        case "--intro-window" when i + 1 < args.Length:
            options.IntroSearchMinutes = ParseInt(args[++i], "--intro-window"); break;

        case "--outro-window" when i + 1 < args.Length:
            options.OutroSearchMinutes = ParseInt(args[++i], "--outro-window"); break;

        case "--threads" when i + 1 < args.Length:
            options.MaxDegreeOfParallelism = ParseInt(args[++i], "--threads"); break;

        case "--extract-timeout" when i + 1 < args.Length:
            options.ExtractTimeoutSeconds = ParseInt(args[++i], "--extract-timeout"); break;

        case "--output" when i + 1 < args.Length:
            options.OutputFile = args[++i]; break;

        case "--no-intro":
            options.DetectIntro = false; break;

        case "--no-outro":
            options.DetectOutro = false; break;

        case "--min-match" when i + 1 < args.Length:
            options.MinEpisodeMatchRatio = ParseDouble(args[++i], "--min-match"); break;

        case "--min-intro" when i + 1 < args.Length:
            options.MinIntroLengthSec = ParseInt(args[++i], "--min-intro"); break;

        case "--chapter-intro-lookback" when i + 1 < args.Length:
            options.ChapterIntroLookbackSeconds = ParseInt(args[++i], "--chapter-intro-lookback"); break;

        case "--min-outro" when i + 1 < args.Length:
            options.MinOutroLengthSec = ParseInt(args[++i], "--min-outro"); break;

        case "--max-shift" when i + 1 < args.Length:
            options.MaxShiftFrames = ParseInt(args[++i], "--max-shift"); break;

        case "-v":
        case "--verbose":
            options.Verbose = true; break;

        case "-h":
        case "--help":
            PrintHelp();
            return 0;

        default:
            if (!args[i].StartsWith('-') && seasonPath is null)
                seasonPath = args[i];
            else
                Console.Error.WriteLine($"[WARN] Unknown option: {args[i]}");
            break;
    }
}

seasonPath ??= Directory.GetCurrentDirectory();

if (!Directory.Exists(seasonPath))
{
    Console.Error.WriteLine($"Error: directory not found: {seasonPath}");
    return 1;
}

// ── Dependency check ───────────────────────────────────────────────────────
if (!AudioExtractor.CheckFfmpeg(options.FfmpegPath))
{
    Console.Error.WriteLine($"Error: ffmpeg not found at '{options.FfmpegPath}'.");
    Console.Error.WriteLine("Install FFmpeg and ensure it is on PATH, or pass --ffmpeg <path>.");
    return 1;
}

if (!AudioExtractor.CheckFfprobe(options.FfprobePath))
{
    Console.Error.WriteLine($"Error: ffprobe not found at '{options.FfprobePath}'.");
    Console.Error.WriteLine("ffprobe ships with FFmpeg — check your installation.");
    return 1;
}

// ── Print configuration ────────────────────────────────────────────────────
Console.WriteLine($"  Season path  : {seasonPath}");
Console.WriteLine($"  Detect intro : {options.DetectIntro} (search first {options.IntroSearchMinutes} min)");
Console.WriteLine($"  Detect outro : {options.DetectOutro} (search last  {options.OutroSearchMinutes} min)");
Console.WriteLine($"  Threads      : {options.MaxDegreeOfParallelism}");
Console.WriteLine($"  Min match    : {options.MinEpisodeMatchRatio:P0} of episodes");
Console.WriteLine($"  Audio stream : {(string.IsNullOrWhiteSpace(options.AudioStream) ? "auto" : options.AudioStream)}");
Console.WriteLine($"  Timeout      : {options.ExtractTimeoutSeconds}s per extraction");
Console.WriteLine();

// ── Run analysis ───────────────────────────────────────────────────────────
try
{
    var analyzer = new SeasonAnalyzer(options);
    var result   = await analyzer.AnalyzeAsync(seasonPath);

    ResultReporter.Print(result, options);

    if (!string.IsNullOrWhiteSpace(options.OutputFile))
        ResultReporter.WriteToFile(result, options.OutputFile);

    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    if (options.Verbose) Console.Error.WriteLine(ex.StackTrace);
    return 1;
}

// ── Local helpers ──────────────────────────────────────────────────────────
static int ParseInt(string s, string opt)
{
    if (!int.TryParse(s, out int v) || v <= 0)
    {
        Console.Error.WriteLine($"[WARN] Invalid value for {opt}: '{s}'. Using default.");
        return 1;
    }
    return v;
}

static double ParseDouble(string s, string opt)
{
    if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v)
        || v is < 0 or > 1)
    {
        Console.Error.WriteLine($"[WARN] Invalid value for {opt}: '{s}'. Using default.");
        return 0.5;
    }
    return v;
}

static void PrintHelp()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  IntroOutroDetector [<season_dir>] [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  <season_dir>            Directory with episode files (default: current dir)");
    Console.WriteLine("  --ffmpeg  <path>         Path to ffmpeg binary        (default: ffmpeg)");
    Console.WriteLine("  --ffprobe <path>         Path to ffprobe binary       (default: ffprobe)");
    Console.WriteLine("  --audio-stream <map>     FFmpeg audio stream map, e.g. 0:a:1 or 0:2 (default: auto)");
    Console.WriteLine("  --intro-window <min>     Search window for intro, minutes  (default: 5)");
    Console.WriteLine("  --outro-window <min>     Search window for outro, minutes  (default: 3)");
    Console.WriteLine("  --threads <n>            Parallel extraction threads       (default: 1)");
    Console.WriteLine("  --extract-timeout <sec>  Max seconds per FFmpeg extraction (default: 90)");
    Console.WriteLine("  --output  <file>         Save results to .json or .csv file");
    Console.WriteLine("  --no-intro               Skip intro detection");
    Console.WriteLine("  --no-outro               Skip outro detection");
    Console.WriteLine("  --min-match <0.0-1.0>    Fraction of episodes that must agree (default: 0.5)");
    Console.WriteLine("  --min-intro <sec>        Minimum intro length in seconds     (default: 25)");
    Console.WriteLine("  --chapter-intro-lookback <sec>  Chapter-guided intro search lookback (default: 180)");
    Console.WriteLine("  --min-outro <sec>        Minimum outro length in seconds     (default: 20)");
    Console.WriteLine("  --max-shift <frames>     Max time shift between episodes     (default: 120)");
    Console.WriteLine("  -v, --verbose            Verbose output");
    Console.WriteLine("  -h, --help               Show this help");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  IntroOutroDetector /media/ShowName/Season01");
    Console.WriteLine("  IntroOutroDetector /media/ShowName/Season01 --output results.json --threads 8");
    Console.WriteLine("  IntroOutroDetector /media/ShowName/Season01 --no-outro --min-match 0.6");
    Console.WriteLine();
    Console.WriteLine("Requirements:");
    Console.WriteLine("  FFmpeg (with ffprobe) must be installed and available on PATH.");
    Console.WriteLine("  Supports: .mkv .mp4 .avi .m4v .mov .wmv .ts .m2ts");
}
