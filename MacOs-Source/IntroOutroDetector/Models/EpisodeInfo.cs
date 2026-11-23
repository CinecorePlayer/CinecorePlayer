namespace IntroOutroDetector.Models;

public class EpisodeInfo
{
    public int Index { get; set; }
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public double DurationSeconds { get; set; }
    public List<ChapterInfo> Chapters { get; set; } = [];

    public string DurationFormatted
    {
        get
        {
            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m"
                : $"{ts.Minutes}m {ts.Seconds:D2}s";
        }
    }
}
