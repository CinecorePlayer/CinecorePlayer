using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>Raccolte per Trakt (CollectTitlesForRecommendations di Windows) e apertura di una scheda dal suo percorso.</summary>
internal sealed partial class LibraryView
{
    /// <summary>Film e serie della libreria con i titoli utili a riconoscerli (mostrato e del nome del file). Una voce per serie.</summary>
    public static IReadOnlyList<TraktLibrary.LibraryTitle> CollectTitlesForRecommendations()
    {
        var result = new List<TraktLibrary.LibraryTitle>();
        var series = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series"))
                         .Where(i => !string.IsNullOrWhiteSpace(i.Path)).GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                MovieMetadataService.MediaTitleInfo parsed;
                try { parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(item.Path); }
                catch { continue; }
                if (parsed.IsTvEpisode || item.Category == "TV Series")
                {
                    string? name = !string.IsNullOrWhiteSpace(item.SeriesTitle) ? item.SeriesTitle : string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.NormalizedTitle : parsed.SeriesTitle;
                    if (string.IsNullOrWhiteSpace(name) || !series.Add(TraktLibrary.Normalize(name))) continue;
                    result.Add(new TraktLibrary.LibraryTitle(item.Path, name!, MovieMetadataService.GetCachedNormalizedTitle(item.Path), null, true));
                    continue;
                }
                result.Add(new TraktLibrary.LibraryTitle(item.Path, item.Title, parsed.NormalizedTitle, item.Year ?? parsed.Year, false));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[TRAKT] collect: " + ex.Message); }
        return result;
    }

    /// <summary>Apre la scheda del titolo (o della serie) che sta in quel file.</summary>
    public void OpenDetailsForPath(string path)
    {
        var item = LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series"))
            .FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        if (item.Category == "TV Series") OpenSeries(item); else OpenDetail(item);
    }
}
