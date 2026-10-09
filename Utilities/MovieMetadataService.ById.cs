#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    internal static partial class MovieMetadataService
    {
        /// <summary>Titolo localizzato, titolo originale, anno e locandina di un titolo TMDb noto per identificativo.</summary>
        public sealed record TmdbSummary(string Title, string? OriginalTitle, int? Year, string? PosterPath);

        /// <param name="type">"movie" oppure "tv".</param>
        public static TmdbSummary? GetTmdbSummary(string type, int id, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(TmdbApiKey) || (type != "movie" && type != "tv")) return null;
            string locale = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "it-IT";
            using var response = GetTmdbResponse($"https://api.themoviedb.org/3/{type}/{id.ToString(CultureInfo.InvariantCulture)}?api_key={TmdbApiKey}&language={locale}", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            var root = doc.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            string? title = Text(type == "movie" ? "title" : "name");
            if (string.IsNullOrWhiteSpace(title)) return null;
            string? date = Text(type == "movie" ? "release_date" : "first_air_date");
            int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
            return new TmdbSummary(title!, Text(type == "movie" ? "original_title" : "original_name"), year, Text("poster_path"));
        }

        /// <summary>Scarica una locandina TMDb (percorso "/abc.jpg") nel file indicato, se non c'e' gia'.</summary>
        public static bool TryDownloadTmdbPoster(string posterPath, string targetFile, CancellationToken ct)
        {
            try
            {
                if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0) return true;
                if (string.IsNullOrWhiteSpace(posterPath) || !posterPath.StartsWith('/') || posterPath.Contains("..")) return false;
                byte[] bytes = GetTmdbImageBytes("https://image.tmdb.org/t/p/w342" + posterPath, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                string temp = targetFile + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, targetFile, true);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[TMDB] poster: " + ex.Message); return false; }
        }
    }
}
