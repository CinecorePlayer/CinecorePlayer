namespace IntroOutroDetector.Models;

public class EpisodeResult
{
    public EpisodeInfo Episode { get; set; } = new();
    public TimeSegment? Intro { get; set; }
    public TimeSegment? Outro { get; set; }
    public bool HasIntro => Intro != null && Intro.LengthSeconds > 0;
    public bool HasOutro => Outro != null && Outro.LengthSeconds > 0;
}

public class SeasonResult
{
    public string SeasonPath { get; set; } = "";
    public List<EpisodeResult> Episodes { get; set; } = [];
    public TimeSegment? ConsensusIntro { get; set; }
    public TimeSegment? ConsensusOutro { get; set; }
    public TimeSpan AnalysisDuration { get; set; }

    public int TotalEpisodes => Episodes.Count;
    public int EpisodesWithIntro => Episodes.Count(e => e.HasIntro);
    public int EpisodesWithOutro => Episodes.Count(e => e.HasOutro);
}
