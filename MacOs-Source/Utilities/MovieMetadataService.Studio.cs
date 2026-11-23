#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    internal static partial class MovieMetadataService
    {
        /// <summary>La casa di produzione principale di un titolo TMDb: nome e logo (PNG trasparente) scaricato in cache.</summary>
        public sealed record Studio(string Name, string? LogoFile);

        private static string StudioFolder => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "studios");

        /// <summary>Dalla cache su disco, senza rete: null se non ancora chiesto.</summary>
        public static Studio? GetCachedStudio(string type, int id)
        {
            try
            {
                string file = Path.Combine(StudioFolder, type + "-" + id.ToString(CultureInfo.InvariantCulture) + ".json");
                if (!File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string? logo = doc.RootElement.TryGetProperty("logo", out var l) ? l.GetString() : null;
                if (logo != null && !File.Exists(logo)) logo = null;
                return new Studio(name, logo);
            }
            catch { return null; }
        }

        /// <param name="type">"movie" oppure "tv" (per le serie conta la rete o la casa).</param>
        public static Studio? ResolveStudio(string type, int id, CancellationToken ct)
        {
            if (GetCachedStudio(type, id) is { } cached) return cached;
            if (string.IsNullOrWhiteSpace(TmdbApiKey) || (type != "movie" && type != "tv")) return null;
            try
            {
                using var response = GetTmdbResponse($"https://api.themoviedb.org/3/{type}/{id.ToString(CultureInfo.InvariantCulture)}?api_key={TmdbApiKey}", ct);
                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
                var root = doc.RootElement;
                // Prima una casa con il logo; per le serie anche la rete (HBO, Netflix...).
                JsonElement? pick = null;
                foreach (string key in type == "tv" ? new[] { "networks", "production_companies" } : new[] { "production_companies" })
                {
                    if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    var items = list.EnumerateArray().ToList();
                    pick = items.FirstOrDefault(c => c.TryGetProperty("logo_path", out var lp) && lp.ValueKind == JsonValueKind.String);
                    if (pick is { ValueKind: JsonValueKind.Object }) break;
                    if (items.Count > 0) { pick = items[0]; break; }
                }
                string name = "";
                string? logoFile = null;
                if (pick is { ValueKind: JsonValueKind.Object } company)
                {
                    name = company.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (company.TryGetProperty("logo_path", out var lp) && lp.GetString() is { Length: > 1 } logoPath && logoPath.StartsWith('/') && !logoPath.Contains(".."))
                    {
                        string target = Path.Combine(StudioFolder, "logo" + logoPath.Replace('/', '-'));
                        if (!target.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) target += ".png";
                        try
                        {
                            if (!File.Exists(target))
                            {
                                byte[] bytes = GetTmdbImageBytes("https://image.tmdb.org/t/p/w300" + logoPath, ct);
                                Directory.CreateDirectory(StudioFolder);
                                File.WriteAllBytes(target, bytes);
                            }
                            logoFile = target;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { logoFile = null; }
                    }
                }
                Directory.CreateDirectory(StudioFolder);
                File.WriteAllText(Path.Combine(StudioFolder, type + "-" + id.ToString(CultureInfo.InvariantCulture) + ".json"),
                    JsonSerializer.Serialize(new { name, logo = logoFile }));
                return new Studio(name, logoFile);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[TMDB] studio: " + ex.Message); return null; }
        }
    }
}
