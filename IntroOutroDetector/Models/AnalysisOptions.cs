namespace IntroOutroDetector.Models;

public class AnalysisOptions
{
    /// <summary>Minutes from episode start to search for intro.</summary>
    public int IntroSearchMinutes { get; set; } = 5;

    /// <summary>Minutes from episode end to search for outro.</summary>
    public int OutroSearchMinutes { get; set; } = 3;

    /// <summary>Minimum intro length in seconds.</summary>
    public int MinIntroLengthSec { get; set; } = 25;

    /// <summary>Maximum intro length in seconds.</summary>
    public int MaxIntroLengthSec { get; set; } = 300;

    /// <summary>When chapters exist, search this many seconds before the first chapter boundary for the intro.</summary>
    public int ChapterIntroLookbackSeconds { get; set; } = 180;

    /// <summary>Minimum outro length in seconds.</summary>
    public int MinOutroLengthSec { get; set; } = 20;

    /// <summary>Maximum outro length in seconds.</summary>
    public int MaxOutroLengthSec { get; set; } = 360;

    /// <summary>Max parallel episode extraction tasks.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>Maximum wall-clock seconds allowed for one FFmpeg extraction.</summary>
    public int ExtractTimeoutSeconds { get; set; } = 90;

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string AudioStream { get; set; } = "";

    /// <summary>Folder where per-episode fingerprints are kept: a later scan of the same season (a new
    /// episode arrived) reads only the files it has not seen. Empty = no cache.</summary>
    public string CacheDirectory { get; set; } = "";

    public bool DetectIntro { get; set; } = true;
    public bool DetectOutro { get; set; } = true;
    public bool Verbose { get; set; } = false;

    /// <summary>Optional JSON/CSV output file path.</summary>
    public string OutputFile { get; set; } = "";

    /// <summary>Video file extensions to consider.</summary>
    public string[] VideoExtensions { get; set; } =
        [".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts"];

    /// <summary>Fraction of episodes that must agree on a segment (0.0–1.0).</summary>
    public double MinEpisodeMatchRatio { get; set; } = 0.5;

    /// <summary>Max allowed time shift between episodes when aligning (in fingerprint frames).</summary>
    public int MaxShiftFrames { get; set; } = 120; // ~60 seconds at 0.5s/frame
}
