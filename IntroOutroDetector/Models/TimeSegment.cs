namespace IntroOutroDetector.Models;

public class TimeSegment
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double LengthSeconds => EndSeconds - StartSeconds;

    public string StartFormatted => FormatTime(StartSeconds);
    public string EndFormatted => FormatTime(EndSeconds);
    public string LengthFormatted => $"{LengthSeconds:F0}s";

    public static string FormatTime(double totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public override string ToString() =>
        $"{StartFormatted} → {EndFormatted}  ({LengthFormatted})";
}
