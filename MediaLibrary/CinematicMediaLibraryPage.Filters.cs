#nullable enable
using CinecorePlayer2025.Engines;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private IReadOnlyList<string> FilterOptionsForCurrentCategory()
        {
            return _category switch
            {
                "Music" => new[] { "All", "Lossless", "Atmos" },
                "Photos" => new[] { "All", "Recent" },
                "Videos" => new[] { "All", "Resume", "Recent" },
                "Playlists" => new[] { "All" },
                "Favourites" => new[] { "All", "4K", "Resume" },
                "WatchHistory" => new[] { "All", "Rated", "TopRated", "4K" },
                _ => new[] { "All", "4K", "Resume" }
            };
        }

        private void ShowFilterChoices()
        {
            var options = new List<HUD.ChoicePopup.Option>();
            string current = _filter;
            void Add(string label, string key) => options.Add(new(label, () => { _filter = key; ResetSmoothGridScroll(); if (_category == "Photos") RefreshContent(); else Invalidate(); }, current == key));
            foreach (string key in FilterOptionsForCurrentCategory()) { _filter = key; Add(ActiveFilterLabel(), key); }
            _filter = current;
            if (_category is "Music" or "Movies")
            {
                bool music = _category == "Music";
                options.Add(new(music ? L("Filtra per artista…", "Filter by artist…") : L("Filtra per regista…", "Filter by director…"), () => ShowPeopleFilter(music), current.StartsWith(music ? "Artist:" : "Director:")));
            }
            if (_category is "Movies" or "TV Series" or "Favourites" or "WatchHistory")
            {
                options.Add(new(L("Per decennio…", "By decade…"), () => ShowYearFilter(decades: true), current.StartsWith("Decade:")));
                options.Add(new(L("Per anno…", "By year…"), () => ShowYearFilter(decades: false), current.StartsWith("Year:")));
                options.Add(new(L("Per genere…", "By genre…"), ShowGenreFilter, current.StartsWith("Genre:")));
            }
            if (_category == "Photos" && !IsNetworkSourceActive())
                options.Add(new(L("Filtra per cartella…", "Filter by folder…"), ShowPhotoFolderFilter, current.StartsWith("Folder:")));
            HUD.ChoicePopup.Show(this, _lastMouse, options, 320);
        }

        private IEnumerable<LibraryItem> FilterableTitles()
            => _items.SelectMany(item => item.IsGroup ? item.Children.Prepend(item) : new[] { item });

        private void ShowYearFilter(bool decades)
        {
            var years = FilterableTitles().Where(item => item.Year is >= 1888 and <= 2100).Select(item => item.Year!.Value)
                .Select(year => decades ? year / 10 * 10 : year).Distinct().OrderByDescending(year => year).ToList();
            string prefix = decades ? "Decade:" : "Year:";
            var options = years.Select(year => new HUD.ChoicePopup.Option(
                decades ? L($"Anni {year}", $"{year}s") : year.ToString(CultureInfo.InvariantCulture),
                () => { _filter = prefix + year.ToString(CultureInfo.InvariantCulture); ResetSmoothGridScroll(); Invalidate(); },
                _filter == prefix + year.ToString(CultureInfo.InvariantCulture))).ToList();
            HUD.ChoicePopup.Show(this, _lastMouse, options, 280, searchable: !decades);
        }

        private void ShowGenreFilter()
        {
            var genres = FilterableTitles().SelectMany(item => item.Genres).Where(genre => !string.IsNullOrWhiteSpace(genre))
                .GroupBy(genre => genre.Trim(), StringComparer.CurrentCultureIgnoreCase).Select(group => group.Key)
                .OrderBy(genre => genre, StringComparer.CurrentCultureIgnoreCase);
            var options = genres.Select(genre => new HUD.ChoicePopup.Option(genre,
                () => { _filter = "Genre:" + genre; ResetSmoothGridScroll(); Invalidate(); }, _filter == "Genre:" + genre)).ToList();
            HUD.ChoicePopup.Show(this, _lastMouse, options, 320, searchable: true);
        }

        private void ShowPeopleFilter(bool music)
        {
                var people = _items.SelectMany(t => t.IsGroup ? t.Children : new List<LibraryItem> { t })
                    .SelectMany(t => music ? MusicArtists(t.ArtistName, t.AlbumArtist) : new[] { t.Director ?? "" })
                    .Where(n => !string.IsNullOrWhiteSpace(n)).GroupBy(MusicArtistArtworkService.Identity).Select(g => g.First()).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase);
                var options = people.Select(person => new HUD.ChoicePopup.Option(person,
                    () => { _filter = (music ? "Artist:" : "Director:") + person; ResetSmoothGridScroll(); Invalidate(); },
                    _filter == (music ? "Artist:" : "Director:") + person)).ToList();
                HUD.ChoicePopup.Show(this, _lastMouse, options, 360, searchable: true);
        }

        private void CycleSortMode()
        {
            var options = new List<HUD.ChoicePopup.Option>();
            int current = _sortMode;
            for (int mode = 0; mode < 4; mode++)
            {
                int selectedMode = mode;
                _sortMode = mode;
                options.Add(new(ActiveSortLabel(), () => { _sortMode = selectedMode; ResetSmoothGridScroll(); RefreshContent(); }, current == mode));
            }
            _sortMode = current;
            HUD.ChoicePopup.Show(this, _lastMouse, options, 280);
        }

        private void ShowPhotoFolderFilter()
        {
            var folders = AllPathsForCategory("Photos").Select(path => Path.GetDirectoryName(path) ?? "").Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);
            HUD.ChoicePopup.Show(this, _lastMouse, folders.Select(folder => new HUD.ChoicePopup.Option(folder,
                () => { _filter = "Folder:" + folder; _photoVisibleLimit = PhotoPageSize; ResetSmoothGridScroll(); RefreshContent(); }, _filter == "Folder:" + folder)), 480, searchable: true);
        }

        private string ActiveFilterLabel()
        {
            if (_filter.StartsWith("Artist:") || _filter.StartsWith("Director:")) return _filter.Substring(_filter.IndexOf(':')+1);
            if (_filter.StartsWith("Folder:")) return Path.GetFileName(_filter[7..]);
            if (_filter.StartsWith("Year:")) return _filter[5..];
            if (_filter.StartsWith("Decade:")) return L("Anni " + _filter[7..], _filter[7..] + "s");
            if (_filter.StartsWith("Genre:")) return _filter[6..];
            return _filter switch
            {
                "All" => L("Tutti", "All"),
                "4K" => "4K / HDR",
                "Resume" => L("In corso", "In progress"),
                "Recent" => L("Recenti", "Recent"),
                "Lossless" => "Lossless",
                "Atmos" => "Atmos",
                "Rated" => L("Valutati", "Rated"),
                "TopRated" => L("Classifica", "Ranking"),
                "Favourites" => L("Preferiti", "Favourites"),
                _ => L("Filtra", "Filter")
            };
        }

        private string ActiveSortLabel()
        {
            if (_category == "Photos")
                return _sortMode switch { 1 => L("Nome A–Z", "Name A–Z"), 2 => L("Meno recenti", "Oldest first"), 3 => L("Dimensione", "File size"), _ => L("Più recenti", "Newest first") };
            return _sortMode switch
            {
                1 => "A–Z",
                2 => L("Durata", "Duration"),
                3 => L("Anno", "Year"),
                _ => L("Recenti", "Recent")
            };
        }

        private IEnumerable<LibraryItem> ApplyActiveFilter(IEnumerable<LibraryItem> source)
        {
            IEnumerable<LibraryItem> items = source;
            if (_filter.StartsWith("Folder:"))
                return items.Where(item => string.Equals(Path.GetDirectoryName(item.Path), _filter[7..], StringComparison.OrdinalIgnoreCase));
            if(_filter.StartsWith("Artist:") || _filter.StartsWith("Director:"))
            {
                bool artist=_filter.StartsWith("Artist:"); string key=MusicArtistArtworkService.Identity(_filter.Substring(_filter.IndexOf(':')+1));
                bool Match(LibraryItem item) => artist?MusicArtists(item.ArtistName,item.AlbumArtist).Any(n=>MusicArtistArtworkService.Identity(n)==key):MusicArtistArtworkService.Identity(item.Director)==key;
                return items.Where(item=>Match(item)||item.Children.Any(Match));
            }
            if (_filter.StartsWith("Year:") || _filter.StartsWith("Decade:"))
            {
                bool decade = _filter.StartsWith("Decade:");
                if (!int.TryParse(_filter[(_filter.IndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted)) return items;
                bool Match(LibraryItem item) => item.Year.HasValue && (decade ? item.Year.Value / 10 * 10 == wanted : item.Year.Value == wanted);
                return items.Where(item => Match(item) || item.Children.Any(Match));
            }
            if (_filter.StartsWith("Genre:"))
            {
                string genre = _filter[6..];
                bool Match(LibraryItem item) => item.Genres.Any(value => string.Equals(value?.Trim(), genre, StringComparison.CurrentCultureIgnoreCase));
                return items.Where(item => Match(item) || item.Children.Any(Match));
            }
            if (string.Equals(_filter, "4K", StringComparison.OrdinalIgnoreCase))
                return items.Where(item => item.Is4K || item.IsHdr || item.Children.Any(child => child.Is4K || child.IsHdr));

            if (string.Equals(_filter, "Resume", StringComparison.OrdinalIgnoreCase))
            {
                var resumePaths = new HashSet<string>(_resumeItems.Select(r => r.Item.Path), StringComparer.OrdinalIgnoreCase);
                return items.Where(item => item.IsGroup
                    ? item.Children.Any(child => resumePaths.Contains(child.Path))
                    : resumePaths.Contains(item.Path));
            }

            if (string.Equals(_filter, "Favourites", StringComparison.OrdinalIgnoreCase))
            {
                var favourites = new HashSet<string>(GetFavoritePathsSnapshot(), StringComparer.OrdinalIgnoreCase);
                return items.Where(item => item.IsGroup
                    ? item.Children.Any(child => favourites.Contains(child.Path))
                    : favourites.Contains(item.Path));
            }

            if (string.Equals(_filter, "Recent", StringComparison.OrdinalIgnoreCase))
            {
                DateTime threshold = DateTime.UtcNow.AddDays(-45);
                var playbackDates = LoadPlaybackRecency();
                return items.Where(item => EffectiveRecentDate(item, playbackDates) >= threshold);
            }

            if (string.Equals(_filter, "Atmos", StringComparison.OrdinalIgnoreCase))
                return items.Where(item => item.HasAtmos || item.Children.Any(child => child.HasAtmos));

            if (string.Equals(_filter, "Lossless", StringComparison.OrdinalIgnoreCase))
            {
                static bool IsLossless(LibraryItem item)
                {
                    string extension = Path.GetExtension(item.Path) ?? string.Empty;
                    return extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".mka", StringComparison.OrdinalIgnoreCase) ||
                           (item.AudioLabel?.Contains("lossless", StringComparison.OrdinalIgnoreCase) ?? false);
                }
                return items.Where(item => IsLossless(item) || item.Children.Any(IsLossless));
            }

            if (string.Equals(_filter, "Rated", StringComparison.OrdinalIgnoreCase))
                return items.Where(item => GetDiaryRating(item.Path) > 0);

            if (string.Equals(_filter, "TopRated", StringComparison.OrdinalIgnoreCase))
                return items.Where(item => GetDiaryRating(item.Path) > 0)
                    .OrderByDescending(item => GetDiaryRating(item.Path))
                    .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase);

            return items;
        }

        private List<LibraryItem> ApplyActiveSort(IEnumerable<LibraryItem> source)
        {
            var playbackDates = LoadPlaybackRecency();
            if (_view == PageView.Home && _sortMode == 0)
                return source.OrderByDescending(item => EffectiveRecentDate(item, playbackDates)).ToList();
            if (_category == "Photos")
                return _sortMode switch
                {
                    1 => source.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    2 => source.OrderBy(x => x.SortDateUtc).ToList(),
                    3 => source.OrderByDescending(x => x.Bytes).ToList(),
                    _ => source.OrderByDescending(x => x.SortDateUtc).ToList()
                };
            if (string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(_filter, "Rated", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_filter, "TopRated", StringComparison.OrdinalIgnoreCase)))
            {
                return source.OrderByDescending(item => GetDiaryRating(item.Path))
                    .ThenByDescending(item => item.SortDateUtc)
                    .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return _sortMode switch
            {
                1 => source.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => source.OrderByDescending(item => item.DurationMinutes ?? 0).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                3 => source.OrderByDescending(item => item.Year ?? int.MinValue).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => source.OrderByDescending(item => EffectiveRecentDate(item, playbackDates)).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        private static Dictionary<string, DateTime> LoadPlaybackRecency()
        {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var entry in WatchHistoryStore.LoadAll())
                    if (!string.IsNullOrWhiteSpace(entry.MediaPath))
                        result[entry.MediaPath] = entry.LastWatchedAtUtc;
            }
            catch { }
            try
            {
                foreach (var entry in PlaybackResumeStore.LoadAll())
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.MediaPath)) continue;
                    if (!result.TryGetValue(entry.MediaPath, out DateTime current) || entry.SavedAt > current)
                        result[entry.MediaPath] = entry.SavedAt;
                }
            }
            catch { }
            return result;
        }

        private static DateTime EffectiveRecentDate(LibraryItem item, IReadOnlyDictionary<string, DateTime> playbackDates)
        {
            DateTime result = item.SortDateUtc;
            if (playbackDates.TryGetValue(item.Path, out DateTime watched) && watched > result)
                result = watched;
            foreach (LibraryItem child in item.Children)
                if (playbackDates.TryGetValue(child.Path, out watched) && watched > result)
                    result = watched;
            return result;
        }

        private double GetDiaryRating(string path)
        {
            if (_watchRatingsCache == null)
            {
                _watchRatingsCache = WatchHistoryStore.LoadAll()
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.MediaPath))
                    .GroupBy(entry => entry.MediaPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => Math.Clamp(group.First().Rating, 0, 5), StringComparer.OrdinalIgnoreCase);
            }

            return _watchRatingsCache.TryGetValue(path, out double value) ? Math.Clamp(value, 0, 5) : 0;
        }

        private void SetDiaryRating(string path, int requestedHalfSteps)
        {
            double current = GetDiaryRating(path);
            double requestedRating = Math.Clamp(requestedHalfSteps, 0, 10) / 2.0;
            double rating = Math.Abs(current - requestedRating) < 0.01 ? 0 : requestedRating;
            _watchRatingsCache ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _watchRatingsCache[path] = rating;
            WatchHistoryStore.SetRating(path, rating);
            RefreshContent();
        }

        private string FormatDiaryRating(double rating)
        {
            string value = rating.ToString(rating % 1 == 0 ? "0" : "0.0",
                string.Equals(_uiLanguage, "it", StringComparison.OrdinalIgnoreCase)
                    ? CultureInfo.GetCultureInfo("it-IT")
                    : CultureInfo.InvariantCulture);
            return value + " / 5";
        }
    }
}
