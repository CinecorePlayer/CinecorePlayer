#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        /// <summary>Film e serie della libreria locale, con il titolo gia' riconosciuto quando c'e'.</summary>
        public IReadOnlyList<LibraryReport.Source> CollectVideoFilesForReport()
        {
            var result = new List<LibraryReport.Source>();
            try
            {
                foreach (string path in AllPathsForCategory("Movies").Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (ShouldIgnoreMediaPath(path) || IsRemoteStoragePath(path) && !_itemCache.ContainsKey(path)) continue;
                    string title;
                    int? year = null;
                    if (_itemCache.TryGetValue(path, out LibraryItem? cached))
                    {
                        title = cached.Title;
                        year = cached.Year;
                    }
                    else
                        title = MovieMetadataService.GetBestKnownDisplayTitle(path);
                    result.Add(new LibraryReport.Source(path, title, year));
                }
            }
            catch (Exception ex) { Dbg.Warn("[REPORT] collect failed: " + ex.Message); }
            return result;
        }

        /// <summary>Film e serie della libreria con i titoli utili a riconoscerli (quello mostrato e quello
        /// del nome del file): servono a ritrovare fra i propri file i consigli di Trakt. Una voce per serie.</summary>
        public IReadOnlyList<TraktLibrary.LibraryTitle> CollectTitlesForRecommendations()
        {
            var result = new List<TraktLibrary.LibraryTitle>();
            var series = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string path in AllPathsForCategory("Movies").Concat(AllPathsForCategory("TV Series"))
                    .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (ShouldIgnoreMediaPath(path)) continue;
                    MovieMetadataService.MediaTitleInfo parsed;
                    try { parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(path); }
                    catch { continue; }
                    if (parsed.IsTvEpisode)
                    {
                        string? name = string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.NormalizedTitle : parsed.SeriesTitle;
                        if (string.IsNullOrWhiteSpace(name) || !series.Add(TraktLibrary.Normalize(name))) continue;
                        result.Add(new TraktLibrary.LibraryTitle(path, name!, MovieMetadataService.GetCachedNormalizedTitle(path), null, true));
                        continue;
                    }
                    _itemCache.TryGetValue(path, out LibraryItem? cached);
                    result.Add(new TraktLibrary.LibraryTitle(path, cached?.Title ?? MovieMetadataService.GetBestKnownDisplayTitle(path), parsed.NormalizedTitle,
                        cached?.Year ?? parsed.Year ?? MovieMetadataService.GetCachedYear(path), false));
                }
            }
            catch (Exception ex) { Dbg.Warn("[TRAKT] collect failed: " + ex.Message); }
            return result;
        }
    }
}
