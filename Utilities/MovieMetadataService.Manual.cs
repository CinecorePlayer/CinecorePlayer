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
    /// <summary>
    /// Correzioni fatte a mano dall'utente: titolo, anno e copertina di un file. TMDb a volte sceglie
    /// il film sbagliato, e di copertine ne ha piu' d'una. La correzione vale piu' di qualsiasi
    /// risultato automatico: il titolo corretto e' quello con cui si cerca, la copertina scelta e'
    /// quella che si mostra.
    /// </summary>
    internal static partial class MovieMetadataService
    {
        private sealed class ManualEntry
        {
            public string? Title { get; set; }
            public int? Year { get; set; }
            public string? PosterFile { get; set; }
        }

        private static readonly object ManualSync = new();
        private static Dictionary<string, ManualEntry>? _manual;
        private static string ManualPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "metadataOverrides.json");

        private static Dictionary<string, ManualEntry> Manual
        {
            get
            {
                lock (ManualSync)
                {
                    if (_manual != null) return _manual;
                    try
                    {
                        if (File.Exists(ManualPath))
                            _manual = new Dictionary<string, ManualEntry>(
                                JsonSerializer.Deserialize<Dictionary<string, ManualEntry>>(File.ReadAllText(ManualPath)) ?? new(), StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Exception ex) { Dbg.Warn("[METADATA] overrides: " + ex.Message); }
                    return _manual ??= new Dictionary<string, ManualEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        private static void SaveManual()
        {
            lock (ManualSync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ManualPath)!);
                    string temp = ManualPath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(Manual, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temp, ManualPath, true);
                }
                catch (Exception ex) { Dbg.Warn("[METADATA] overrides save: " + ex.Message); }
            }
        }

        public static bool TryGetManualTitle(string? path, out string title, out int? year)
        {
            title = string.Empty; year = null;
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (ManualSync)
            {
                if (!Manual.TryGetValue(path, out ManualEntry? entry) || string.IsNullOrWhiteSpace(entry.Title)) return false;
                title = entry.Title!.Trim(); year = entry.Year;
                return true;
            }
        }

        public static string? GetManualPoster(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            lock (ManualSync)
            {
                return Manual.TryGetValue(path, out ManualEntry? entry) && !string.IsNullOrWhiteSpace(entry.PosterFile) && File.Exists(entry.PosterFile)
                    ? entry.PosterFile : null;
            }
        }

        public static bool HasManualCorrection(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (ManualSync) return Manual.ContainsKey(path);
        }

        /// <summary>Titolo e anno decisi dall'utente: la ricerca dei dati riparte da questi.</summary>
        public static void SetManualTitle(string path, string title, int? year)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title)) return;
            lock (ManualSync)
            {
                if (!Manual.TryGetValue(path, out ManualEntry? entry)) Manual[path] = entry = new ManualEntry();
                entry.Title = title.Trim();
                entry.Year = year;
                SaveManual();
            }
            // Locandina e sfondo trovati con il titolo sbagliato si dimenticano: si ricercano con quello giusto.
            try { _posterIndex.Reset(path, title.Trim(), year); } catch { }
            try { PostersChanged?.Invoke(); } catch { }
        }

        /// <summary>Copertina scelta dall'utente (un file sul computer o una di TMDb gia' scaricata): viene copiata fra le locandine.</summary>
        public static bool SetManualPoster(string path, string imageFile)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(imageFile)) return false;
            try
            {
                using (var probe = System.Drawing.Image.FromFile(imageFile))
                    if (probe.Width < 80 || probe.Height < 80) return false;
                string target = Path.Combine(GetPosterFolder(), ComputeSha1(path + "|manual|" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)) + Path.GetExtension(imageFile).ToLowerInvariant());
                File.Copy(imageFile, target, true);
                lock (ManualSync)
                {
                    if (!Manual.TryGetValue(path, out ManualEntry? entry)) Manual[path] = entry = new ManualEntry();
                    string? previous = entry.PosterFile;
                    entry.PosterFile = target;
                    SaveManual();
                    try { if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, target, StringComparison.OrdinalIgnoreCase) && File.Exists(previous)) File.Delete(previous); } catch { }
                }
                try { _posterIndex.Update(path, null, null, target, null, titleResolved: null); } catch { }
                try { PostersChanged?.Invoke(); } catch { }
                return true;
            }
            catch (Exception ex) { Dbg.Warn("[METADATA] manual poster: " + ex.Message); return false; }
        }

        /// <summary>Toglie la correzione: tornano il titolo letto dal nome del file e la copertina automatica.</summary>
        public static void ClearManual(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (ManualSync)
            {
                if (!Manual.Remove(path, out ManualEntry? entry)) return;
                try { if (!string.IsNullOrWhiteSpace(entry.PosterFile) && File.Exists(entry.PosterFile)) File.Delete(entry.PosterFile); } catch { }
                SaveManual();
            }
            try
            {
                var parsed = ExtractMovieTitleAndYearFromPath(path);
                _posterIndex.Reset(path, parsed.normalizedTitle, parsed.year);
            }
            catch { }
            try { PostersChanged?.Invoke(); } catch { }
        }

        public sealed record TmdbMatch(int Id, string Title, int? Year, string? PosterPath);

        /// <summary>I titoli che TMDb propone per un nome (e un anno): fra questi l'utente riconosce quello giusto.</summary>
        public static List<TmdbMatch> SearchTmdbMovies(string title, int? year, string language, CancellationToken ct)
        {
            var found = new List<TmdbMatch>();
            if (string.IsNullOrWhiteSpace(TmdbApiKey) || string.IsNullOrWhiteSpace(title)) return found;
            string locale = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "it-IT";
            string url = $"https://api.themoviedb.org/3/search/movie?api_key={TmdbApiKey}&language={locale}&query={Uri.EscapeDataString(title.Trim())}" +
                         (year is > 1870 ? "&year=" + year.Value.ToString(CultureInfo.InvariantCulture) : "");
            using var response = GetTmdbResponse(url, ct);
            if (!response.IsSuccessStatusCode) return found;
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return found;
            foreach (var item in results.EnumerateArray().Take(8))
            {
                string? name = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !item.TryGetProperty("id", out var id) || !id.TryGetInt32(out int tmdbId)) continue;
                string? date = item.TryGetProperty("release_date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                int? when = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
                string? poster = item.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                found.Add(new TmdbMatch(tmdbId, name!, when, poster));
            }
            return found;
        }

        /// <summary>Le copertine che TMDb ha per un film: prima quelle nella lingua dell'interfaccia, poi le altre.</summary>
        public static List<string> ListTmdbPosters(int tmdbId, string language, CancellationToken ct)
        {
            var posters = new List<string>();
            if (string.IsNullOrWhiteSpace(TmdbApiKey) || tmdbId <= 0) return posters;
            using var response = GetTmdbResponse($"https://api.themoviedb.org/3/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}/images?api_key={TmdbApiKey}", ct);
            if (!response.IsSuccessStatusCode) return posters;
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            if (!doc.RootElement.TryGetProperty("posters", out var list) || list.ValueKind != JsonValueKind.Array) return posters;
            string wanted = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            var all = new List<(string Path, int Rank, double Vote)>();
            foreach (var item in list.EnumerateArray())
            {
                string? file = item.TryGetProperty("file_path", out var f) ? f.GetString() : null;
                if (string.IsNullOrWhiteSpace(file)) continue;
                string? iso = item.TryGetProperty("iso_639_1", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                double vote = item.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                int rank = string.Equals(iso, wanted, StringComparison.OrdinalIgnoreCase) ? 0 : string.Equals(iso, "en", StringComparison.OrdinalIgnoreCase) ? 1 : string.IsNullOrEmpty(iso) ? 2 : 3;
                all.Add((file!, rank, vote));
            }
            posters.AddRange(all.OrderBy(entry => entry.Rank).ThenByDescending(entry => entry.Vote).Select(entry => entry.Path).Take(18));
            return posters;
        }

        /// <summary>Scarica una copertina TMDb nella misura chiesta ("w185" per la scelta, "w780" per quella definitiva).</summary>
        public static bool DownloadTmdbImage(string tmdbPath, string size, string targetFile, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tmdbPath) || !tmdbPath.StartsWith('/') || tmdbPath.Contains("..")) return false;
                if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0) return true;
                byte[] bytes = GetTmdbImageBytes("https://image.tmdb.org/t/p/" + size + tmdbPath, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.WriteAllBytes(targetFile, bytes);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[TMDB] image: " + ex.Message); return false; }
        }
    }
}
