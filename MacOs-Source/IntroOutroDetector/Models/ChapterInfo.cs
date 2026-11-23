namespace IntroOutroDetector.Models;

public class ChapterInfo
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string Title { get; set; } = "";

    public double LengthSeconds => EndSeconds - StartSeconds;
}
