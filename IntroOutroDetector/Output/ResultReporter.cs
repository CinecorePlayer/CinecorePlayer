namespace IntroOutroDetector.Output;

using System.Text;
using System.Text.Json;
using IntroOutroDetector.Models;

public static class ResultReporter
{
    // ── Console output ─────────────────────────────────────────────────────

    public static void Print(SeasonResult result, AnalysisOptions options)
    {
        Console.WriteLine();
        PrintSeparator('═', 80);
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  RESULTS — {result.TotalEpisodes} episodes  " +
                          $"(analysis: {result.AnalysisDuration.TotalSeconds:F1}s)");
        Console.ResetColor();
        PrintSeparator('═', 80);
        Console.WriteLine();

        // Consensus summary
        if (result.ConsensusIntro != null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("  Intro (consensus): ");
            Console.ResetColor();
            Console.WriteLine(result.ConsensusIntro);
        }
        else if (options.DetectIntro)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  Intro: not detected (not enough matching episodes)");
            Console.ResetColor();
        }

        if (result.ConsensusOutro != null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("  Outro (consensus): ");
            Console.ResetColor();
            Console.WriteLine(result.ConsensusOutro);
        }
        else if (options.DetectOutro)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  Outro: not detected (not enough matching episodes)");
            Console.ResetColor();
        }

        Console.WriteLine();
        PrintSeparator('─', 80);

        // Per-episode table header
        const int col0 = 32, col1 = 20, col2 = 20;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            $"  {"EPISODE",-col0}  {"INTRO",-col1}  {"OUTRO",-col2}");
        Console.ResetColor();
        PrintSeparator('─', 80);

        foreach (var ep in result.Episodes)
        {
            string name = TruncateName(ep.Episode.FileName, col0);
            string intro = ep.HasIntro
                ? $"{ep.Intro!.StartFormatted}→{ep.Intro.EndFormatted} ({ep.Intro.LengthFormatted})"
                : "—";
            string outro = ep.HasOutro
                ? $"{ep.Outro!.StartFormatted}→{ep.Outro.EndFormatted} ({ep.Outro.LengthFormatted})"
                : "—";

            // Colour episodes that had no match
            if (!ep.HasIntro && options.DetectIntro)
                Console.ForegroundColor = ConsoleColor.DarkGray;

            Console.WriteLine($"  {name,-col0}  {intro,-col1}  {outro,-col2}");
            Console.ResetColor();
        }

        PrintSeparator('─', 80);
        Console.WriteLine();

        // Stats
        if (options.DetectIntro)
            Console.WriteLine($"  Intro found in {result.EpisodesWithIntro}/{result.TotalEpisodes} episodes.");
        if (options.DetectOutro)
            Console.WriteLine($"  Outro found in {result.EpisodesWithOutro}/{result.TotalEpisodes} episodes.");

        Console.WriteLine();
    }

    // ── File output ────────────────────────────────────────────────────────

    public static void WriteToFile(SeasonResult result, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        string content = ext switch
        {
            ".csv"  => BuildCsv(result),
            ".json" => BuildJson(result),
            _       => BuildJson(result) // default to JSON
        };

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, content, Encoding.UTF8);
        Console.WriteLine($"  Results saved → {path}");
    }

    // ── Builders ───────────────────────────────────────────────────────────

    private static string BuildCsv(SeasonResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Episode,IntroStart,IntroEnd,IntroLength,OutroStart,OutroEnd,OutroLength");

        foreach (var ep in result.Episodes)
        {
            string introStart = ep.HasIntro ? ep.Intro!.StartSeconds.ToString("F2") : "";
            string introEnd   = ep.HasIntro ? ep.Intro!.EndSeconds.ToString("F2")   : "";
            string introLen   = ep.HasIntro ? ep.Intro!.LengthSeconds.ToString("F2"): "";
            string outroStart = ep.HasOutro ? ep.Outro!.StartSeconds.ToString("F2") : "";
            string outroEnd   = ep.HasOutro ? ep.Outro!.EndSeconds.ToString("F2")   : "";
            string outroLen   = ep.HasOutro ? ep.Outro!.LengthSeconds.ToString("F2"): "";

            sb.AppendLine($"\"{ep.Episode.FileName}\",{introStart},{introEnd},{introLen},{outroStart},{outroEnd},{outroLen}");
        }

        return sb.ToString();
    }

    private static string BuildJson(SeasonResult result)
    {
        var obj = new
        {
            season_path    = result.SeasonPath,
            analysis_time_seconds = result.AnalysisDuration.TotalSeconds,
            consensus_intro = result.ConsensusIntro != null ? new
            {
                start   = result.ConsensusIntro.StartSeconds,
                end     = result.ConsensusIntro.EndSeconds,
                length  = result.ConsensusIntro.LengthSeconds,
                display = result.ConsensusIntro.ToString()
            } : null,
            consensus_outro = result.ConsensusOutro != null ? new
            {
                start   = result.ConsensusOutro.StartSeconds,
                end     = result.ConsensusOutro.EndSeconds,
                length  = result.ConsensusOutro.LengthSeconds,
                display = result.ConsensusOutro.ToString()
            } : null,
            episodes = result.Episodes.Select(ep => new
            {
                filename = ep.Episode.FileName,
                duration = ep.Episode.DurationSeconds,
                intro = ep.HasIntro ? new
                {
                    start   = ep.Intro!.StartSeconds,
                    end     = ep.Intro!.EndSeconds,
                    length  = ep.Intro!.LengthSeconds,
                    display = ep.Intro!.ToString()
                } : null,
                outro = ep.HasOutro ? new
                {
                    start   = ep.Outro!.StartSeconds,
                    end     = ep.Outro!.EndSeconds,
                    length  = ep.Outro!.LengthSeconds,
                    display = ep.Outro!.ToString()
                } : null
            }).ToArray()
        };

        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void PrintSeparator(char c, int width) =>
        Console.WriteLine(new string(c, width));

    private static string TruncateName(string name, int max) =>
        name.Length <= max ? name : "…" + name[^(max - 1)..];
}
