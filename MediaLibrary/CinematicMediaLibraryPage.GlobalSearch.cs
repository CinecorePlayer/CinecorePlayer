#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // A search typed in Home covers the whole library. The results are shown in the page they
    // belong to (Movies, TV Series, Music, Videos); when other sections match too, they are
    // listed next to the count so a film and an album with similar names are both reachable.
    internal sealed partial class CinematicMediaLibraryPage
    {
        private sealed record GlobalSearchMatch(string Category, int Count, string FirstTitle);

        private static readonly string[] GlobalSearchCategories = { "Movies", "TV Series", "Music", "Videos" };
        private bool _routingHomeSearch;
        private bool _globalSearch;
        private List<GlobalSearchMatch> _globalSearchOther = new();

        private void EndGlobalSearch()
        {
            if (_routingHomeSearch) return;
            _globalSearch = false;
            _globalSearchOther = new List<GlobalSearchMatch>();
        }

        // Testo su cui cercare, gruppo e titolo di ogni elemento, per sezione. Prepararli costa
        // (percorsi, metadati, minuscole) e chi scrive li riusa a ogni lettera: valgono qualche
        // secondo, o finche' non arrivano nuovi metadati.
        private Dictionary<string, List<(string Hay, string Group, string Title)>>? _globalSearchIndex;
        private long _globalSearchIndexBuiltAt;
        private int _globalSearchIndexItems = -1;

        private List<(string Hay, string Group, string Title)> GlobalSearchEntries(string key)
        {
            if (_globalSearchIndex == null || _globalSearchIndexItems != _itemCache.Count || Environment.TickCount64 - _globalSearchIndexBuiltAt > 4000)
            {
                _globalSearchIndex = new Dictionary<string, List<(string, string, string)>>(StringComparer.OrdinalIgnoreCase);
                _globalSearchIndexItems = _itemCache.Count;
                _globalSearchIndexBuiltAt = Environment.TickCount64;
            }
            if (_globalSearchIndex.TryGetValue(key, out var ready)) return ready;
            var entries = new List<(string Hay, string Group, string Title)>();
            bool grouped = key is "Music" or "TV Series";
            foreach (string path in AllPathsForCategory(key).Where(path => !string.IsNullOrWhiteSpace(path) && PathBelongsToCategory(path, key)))
            {
                string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
                _itemCache.TryGetValue(path, out LibraryItem? known);
                string hay = (Path.GetFileNameWithoutExtension(path) + " " + folder + " " + (known != null ? SearchHaystack(known) : string.Empty)).ToLowerInvariant();
                string title = key == "Music" ? (string.IsNullOrWhiteSpace(known?.AlbumTitle) ? folder : known!.AlbumTitle!)
                    : !string.IsNullOrWhiteSpace(known?.Title) ? known!.Title : Path.GetFileNameWithoutExtension(path);
                // Album e stagioni contano una volta sola: si raggruppa per cartella.
                entries.Add((hay, grouped ? Path.GetDirectoryName(path) ?? path : path, title));
            }
            return _globalSearchIndex[key] = entries;
        }

        private List<GlobalSearchMatch> GlobalSearchMatches(string query)
        {
            var result = new List<GlobalSearchMatch>();
            string[] tokens = Regex.Split(query.Trim().ToLowerInvariant(), "\\s+").Where(token => token.Length > 0).ToArray();
            if (tokens.Length == 0) return result;
            foreach (string key in GlobalSearchCategories)
            {
                try
                {
                    var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string first = string.Empty;
                    foreach (var entry in GlobalSearchEntries(key))
                    {
                        if (!tokens.All(entry.Hay.Contains) || !groups.Add(entry.Group)) continue;
                        if (first.Length == 0) first = entry.Title;
                    }
                    if (groups.Count > 0) result.Add(new GlobalSearchMatch(key, groups.Count, Regex.Replace(first.Replace('_', ' '), "\\s+", " ").Trim()));
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// Called at every change of the search text. Returns true when it moved to another page
        /// (which refreshes by itself); false when the current page only has to filter.
        /// </summary>
        private bool RouteGlobalSearch()
        {
            if (_routingHomeSearch) return false;
            bool hasText = !string.IsNullOrWhiteSpace(_searchBox.Text);
            if (_view == PageView.Home) { if (!hasText) return false; }
            else if (!_globalSearch || _view != PageView.Collection) return false;
            if (!hasText) { _globalSearchOther = new List<GlobalSearchMatch>(); return false; }

            var matches = GlobalSearchMatches(_searchBox.Text);
            // Finche' la pagina aperta ha risultati si resta li': saltare di sezione a ogni lettera disorienta.
            string? current = _view == PageView.Collection ? _category : null;
            string target = matches.Any(m => m.Category == current) ? current!
                : matches.FirstOrDefault()?.Category ?? current ?? "Movies";
            _globalSearchOther = matches.Where(m => m.Category != target).ToList();
            _globalSearch = true;
            if (_view == PageView.Collection && string.Equals(_category, target, StringComparison.OrdinalIgnoreCase)) return false;
            _routingHomeSearch = true;
            try { ShowCategoryPage(target, clearSearch: false, keepSearchFocus: _searchBox.Focused); }
            finally { _routingHomeSearch = false; }
            return true;
        }

        private void OpenGlobalSearchSection(string category)
        {
            if (string.IsNullOrWhiteSpace(_searchBox.Text)) return;
            var matches = GlobalSearchMatches(_searchBox.Text);
            _routingHomeSearch = true;
            try
            {
                _globalSearch = true;
                _globalSearchOther = matches.Where(m => m.Category != category).ToList();
                ShowCategoryPage(category, clearSearch: false, keepSearchFocus: false);
            }
            finally { _routingHomeSearch = false; }
        }

        /// <summary>"Also in: Music · The Witcher 3 (2)" links, drawn from x on the given row. Returns the x after the last one.</summary>
        private int DrawGlobalSearchLinks(Graphics g, int x, Rectangle row, float scale)
        {
            if (!_globalSearch || _globalSearchOther.Count == 0 || string.IsNullOrWhiteSpace(_searchBox.Text)) return x;
            using var font = LibraryFont("Segoe UI", Math.Max(8.8f, 10.2f * scale));
            using var bold = LibraryFont("Segoe UI Semibold", Math.Max(8.8f, 10.2f * scale));
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            string lead = L("Anche in", "Also in");
            int leadW = TextRenderer.MeasureText(g, lead, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            if (x + leadW + S(scale, 80) > row.Right) return x;
            TextRenderer.DrawText(g, lead, font, new Rectangle(x, row.Top, leadW, row.Height), Muted, flags);
            x += leadW + S(scale, 10);
            foreach (var match in _globalSearchOther)
            {
                string text = LocalizedCategoryName(match.Category) + " · " + match.FirstTitle + (match.Count > 1 ? $" (+{match.Count - 1})" : string.Empty);
                // Solo testo, come il resto dell'intestazione: nessun riquadro. Il colore d'accento al
                // passaggio del mouse dice che si puo' cliccare.
                int width = Math.Min(S(scale, 360), TextRenderer.MeasureText(g, text, bold, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
                if (x + width > row.Right) break;
                var link = new Rectangle(x, row.Top, width, row.Height);
                var touch = Rectangle.Inflate(link, S(scale, 6), S(scale, 4));
                TextRenderer.DrawText(g, text, bold, link, touch.Contains(_lastMouse) ? Accent : TextMain, flags);
                _hits.Add(new HitZone { Bounds = touch, Kind = HitKind.Header, Key = "also-in:" + match.Category });
                x = link.Right + S(scale, 22);
            }
            return x;
        }
    }
}
