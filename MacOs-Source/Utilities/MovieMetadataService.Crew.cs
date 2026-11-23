#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    internal static partial class MovieMetadataService
    {
        /// <summary>Una persona della troupe: nome, mestiere TMDb (Director, Director of Photography...) e foto.</summary>
        public sealed record CrewMember(string Name, string Job, string? ProfileUrl);

        // I mestieri che interessano, nell'ordine in cui si mostrano.
        public static readonly string[] CrewJobs =
        {
            "Director", "Screenplay", "Writer", "Director of Photography", "Original Music Composer", "Editor",
            "Producer", "Production Design", "Costume Design"
        };

        private static string CrewFolder => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "crew");

        private static string CrewFile(string type, int id) => Path.Combine(CrewFolder, type + "-" + id.ToString(CultureInfo.InvariantCulture) + ".json");

        /// <summary>Dalla cache su disco, senza rete: null se non ancora chiesta.</summary>
        public static List<CrewMember>? GetCachedCrew(string type, int id)
        {
            try
            {
                string file = CrewFile(type, id);
                if (!File.Exists(file)) return null;
                return JsonSerializer.Deserialize<List<CrewMember>>(File.ReadAllText(file)) ?? new List<CrewMember>();
            }
            catch { return null; }
        }

        /// <param name="type">"movie" oppure "tv".</param>
        public static List<CrewMember>? ResolveCrew(string type, int id, CancellationToken ct)
        {
            if (GetCachedCrew(type, id) is { } cached) return cached;
            if (string.IsNullOrWhiteSpace(TmdbApiKey) || (type != "movie" && type != "tv")) return null;
            try
            {
                string endpoint = type == "tv"
                    ? $"https://api.themoviedb.org/3/tv/{id.ToString(CultureInfo.InvariantCulture)}/aggregate_credits?api_key={TmdbApiKey}"
                    : $"https://api.themoviedb.org/3/movie/{id.ToString(CultureInfo.InvariantCulture)}/credits?api_key={TmdbApiKey}";
                using var response = GetTmdbResponse(endpoint, ct);
                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
                var found = new List<(CrewMember Member, int Weight)>();
                if (doc.RootElement.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
                {
                    foreach (var person in crew.EnumerateArray())
                    {
                        string? name = ReadJsonString(person, "name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string? profile = ReadJsonString(person, "profile_path");
                        string? url = string.IsNullOrWhiteSpace(profile) ? null : "https://image.tmdb.org/t/p/w185" + profile;
                        // Serie: i mestieri stanno in "jobs" con il numero di episodi; conta chi ne ha fatti di piu'.
                        if (person.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var job in jobs.EnumerateArray())
                            {
                                string? title = ReadJsonString(job, "job");
                                int episodes = job.TryGetProperty("episode_count", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0;
                                if (title != null && CrewJobs.Contains(title)) found.Add((new CrewMember(name!, title, url), episodes));
                            }
                        }
                        else if (ReadJsonString(person, "job") is { } title && CrewJobs.Contains(title))
                            found.Add((new CrewMember(name!, title, url), 0));
                    }
                }
                var result = found
                    .OrderBy(entry => Array.IndexOf(CrewJobs, entry.Member.Job))
                    .ThenByDescending(entry => entry.Weight)
                    .Select(entry => entry.Member)
                    .GroupBy(member => member.Job + "|" + member.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .GroupBy(member => member.Job)
                    .SelectMany(group => group.Take(3))
                    .ToList();
                Directory.CreateDirectory(CrewFolder);
                File.WriteAllText(CrewFile(type, id), JsonSerializer.Serialize(result));
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[TMDB] crew: " + ex.Message); return null; }
        }
    }
}
