using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Ricerca della barra (Shell.TopBar.cs, GlobalSearch.cs, Filters.cs): la lente si apre in una casella di vetro con
/// ordinamento e filtro accanto; dalla Home la ricerca copre tutta la libreria e porta alla sezione con i risultati,
/// con "Anche in" per le altre. Un clic fuori dalla ricerca vuota la richiude.
/// </summary>
internal sealed partial class LibraryView
{
    private bool _searchOpen;
    private DateTime _searchAnimAt = DateTime.MinValue;
    private double _searchFrom;
    private const double SearchAnimMs = 220;
    private TextBox? _searchBox;
    private GlassSearchField? _searchField;
    private Rect _searchPill, _searchGroup, _filtersAnchor;
    private string _query = "";
    private int _sortMode;            // 0 Recenti, 1 A–Z, 2 Durata, 3 Anno
    private string _filter = "All";
    private List<(string Category, int Count, string First)> _alsoIn = new();

    internal void TestFilters() => OpenFilterMenu();

    internal void TestSearch(string text)
    {
        SetSearchOpen(true);
        EnsureSearchBox();
        DispatcherTimer.RunOnce(() => { if (_searchBox != null) { _searchBox.Text = text; _searchBox.Focus(); _searchBox.CaretIndex = text.Length; } InvalidateVisual(); }, TimeSpan.FromMilliseconds(400));
    }

    internal void ApplyRemoteSearch(string text)
    {
        if (text.Length == 0 && !_searchOpen) return;
        SetSearchOpen(true);
        EnsureSearchBox().Text = text;
    }

    private double SearchProgress()
    {
        double target = _searchOpen ? 1 : 0;
        double t = Math.Clamp((DateTime.UtcNow - _searchAnimAt).TotalMilliseconds / SearchAnimMs, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);
        return _searchFrom + (target - _searchFrom) * e;
    }

    private void SetSearchOpen(bool open)
    {
        if (_searchOpen == open) return;
        _searchFrom = SearchProgress();
        _searchOpen = open;
        _searchAnimAt = DateTime.UtcNow;
        if (!open && _searchBox != null) { _searchBox.IsVisible = false; Focus(); }
        _frames.Start();
        InvalidateVisual();
    }

    private TextBox EnsureSearchBox()
    {
        if (_searchBox != null) return _searchBox;
        var box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily(AppAssets.Root + "Assets/Fonts#Inter"),
            Padding = new Thickness(0),
            MinHeight = 0,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Watermark = "",
            IsVisible = false,
        };
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[k] = Brushes.Transparent;
        box.TextChanged += (_, _) => { OnQueryChanged(box.Text ?? ""); InvalidateVisual(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && YouTubeOpen) { YouTubeSearch(box.Text); e.Handled = true; return; }
            if (e.Key == Key.Escape && YouTubeOpen && (box.Text ?? "").Length == 0) { Navigate("Home"); e.Handled = true; return; }
            if (e.Key == Key.Escape)
            {
                if ((box.Text ?? "").Length > 0) box.Text = "";
                else SetSearchOpen(false);
                e.Handled = true;
            }
        };
        InputLayer?.Children.Add(box);
        return _searchBox = box;
    }

    /// <summary>La casella nativa segue il vetro quando e' tutto aperto.</summary>
    private void PlaceSearchBox()
    {
        if (_searchBox == null) return;
        bool show = IsVisible && _searchOpen && SearchProgress() >= 0.999 && _searchPill.Width > 0 && _signIn == null && _detail == null && _seriesName == null && !_foldersOpen;
        if (show)
        {
            double h = Px(Math.Max(8.6, 10.6 * _s)) * 1.7;
            Canvas.SetLeft(_searchBox, _searchPill.X + S(40));
            Canvas.SetTop(_searchBox, _searchPill.Y + (_searchPill.Height - h) / 2);
            _searchBox.Width = Math.Max(10, _searchPill.Width - S(54));
            _searchBox.Height = h;
            _searchBox.FontSize = Px(Math.Max(8.6, 10.6 * _s));
            // Sul vetro vero testo e cursore li disegna GlassSearchField sopra il vetro: la casella resta invisibile.
            bool glass = _searchField?.On == true;
            _searchBox.Foreground = glass ? Brushes.Transparent : B(Text); _searchBox.CaretBrush = glass ? Brushes.Transparent : B(Text);
            _searchBox.Watermark = "";
            _searchBox.SelectionBrush = glass ? Brushes.Transparent : null;
        }
        if (_searchBox.IsVisible != show) _searchBox.IsVisible = show;
    }

    internal void HideSearchSurface()
    {
        _searchField?.Hide();
        if (_searchBox != null) _searchBox.IsVisible = false;
        _ytTyping?.Stop();
    }

    internal string SearchSurfaceState => $"glass={_searchField?.On == true} input={_searchBox?.IsVisible == true}";

    /// <summary>DrawShellSearch: la lente si allarga nella pillola di vetro; filtro e ordinamento compaiono accanto.</summary>
    private void DrawShellSearch(DrawingContext ctx, Rect bar, double right)
    {
        double h = S(40), full = Math.Min(S(320), Math.Max(S(200), bar.Width / 5));
        // Ordinamento e filtro accanto devono starci sempre: se fra le voci della barra e la ricerca non c'e' posto
        // (finestra stretta, barra in alto ingrandita) si restringe la casella, non si tolgono i filtri.
        if (_view == "Collection" && _category is "Movies" or "TV Series" or "Photos" or "Videos" or "Music")
        {
            double need = S(22) + h;
            double room = right - (_navRight + S(28)) - need;
            full = Math.Max(S(150), Math.Min(full, room));
        }
        double p = SearchProgress();
        double w = Math.Round(h + (full - h) * p);
        var r = new Rect(right - w, bar.Y + (bar.Height - h) / 2, w, h);
        // Vetro vero di macOS (come la barra della musica); se qualcosa lo copre, il velo disegnato di prima.
        _searchField ??= new GlassSearchField(this);
        double lensIcon = S(18), lensXg = (r.X + (h - lensIcon) / 2) + (S(14) - (h - lensIcon) / 2) * p;
        var shifted = r.Translate(new Vector(0, Math.Round(TopShift)));
        bool capture = _glassCapture || _pageTransition.Capturing;
        bool nativeSearch = !capture && p > 0.01 && _searchField.ToWindow(shifted) is { } sw && !NativeBlocked(sw) && _searchField.Show(shifted, Math.Min(1, p * 1.4), _searchBox,
            new Rect(shifted.X + S(40), shifted.Y, Math.Max(10, shifted.Width - S(54)), h),
            new Rect(lensXg, shifted.Y + (h - lensIcon) / 2, lensIcon, lensIcon), p > 0.9 ? (YouTubeOpen ? "Cerca su YouTube" : "Cerca") : "",
            new FontFamily(AppAssets.Root + "Assets/Fonts#Inter"), Px(Math.Max(8.6, 10.6 * _s)), Text, A(Text, 120));
        if (!capture && !nativeSearch) _searchField.Hide();
        if (p > 0.01 && !nativeSearch)
        {
            // Vetro dal vivo come la barra della musica (sfoca la pagina sotto); il velo pieno solo nelle foto della pagina.
            bool dark = !UiTheme.IsLight;
            double a = Math.Min(1, p * 1.4);
            Color fallback = dark ? Color.FromRgb(24, 27, 33) : Color.FromRgb(246, 247, 249);
            if (capture || QaNoGlass) ctx.DrawRectangle(B(A(fallback, 255 * a)), null, r, h / 2, h / 2);
#if CINECORE_WINDOWS
            // Il vetro liquid del vecchio player Windows (NetflixModePage.Search), senza filo di luce.
            else ctx.Custom(BackdropGlassOp.Liquid(r, h / 2, a, .3));
#else
            else ctx.Custom(new BackdropGlassOp(r, h / 2, 30, dark ? .8 : .85, dark ? .6 : .38, dark ? .045 : .6, fallback, a));
            using (ctx.PushOpacity(a)) DrawGlassRim(ctx, r, h / 2, !dark);
#endif
        }
        double icon = S(18);
        double lensX = (r.X + (h - icon) / 2) + (S(14) - (h - icon) / 2) * p;
        bool hover = _menu == null && Hover(r);
        if (!nativeSearch) SvgIcon.Get("search")?.Draw(ctx, new Rect(lensX, r.Y + (h - icon) / 2, icon, icon), p > 0.5 ? A(Text, 200) : A(Text, hover ? 255 : 190));
        // Scritta d'invito disegnata qui (come sul vetro del Mac), non quella della casella: la casella compare solo a
        // pillola aperta e col fuoco la scritta lampeggiava e spariva.
        if (!nativeSearch && p > 0.9 && string.IsNullOrEmpty(_searchBox?.Text))
            TextAt(ctx, YouTubeOpen ? "Cerca su YouTube" : "Cerca", Math.Max(8.6, 10.6 * _s), DisplayRegular, A(Text, 120 * Math.Clamp((p - 0.9) * 10, 0, 1)),
                new Rect(r.X + S(40), r.Y, Math.Max(10, r.Width - S(54)), h));
        var group = r;
        if (_view == "Collection" && p > 0.02 && _category is "Movies" or "TV Series" or "Photos" or "Videos" or "Music")
        {
            // Un solo tasto per ordinamento e filtri (prima due scritte con la freccia che toglievano spazio alla casella):
            // apre la lista di testo dei menu della libreria. Acceso quando un filtro e' attivo.
            var item = new Rect(r.X - S(12) - h, r.Y, h, h);
            if (item.X >= _navRight + S(28))
            {
                _filtersAnchor = _sortAnchor = item;
                bool hot = _menu == null && Hover(item) || _menuKey == "Filters";
                bool active = _filter != "All";
                double fade = Math.Clamp((p - 0.3) / 0.7, 0, 1);
                if (hot || active) ctx.DrawEllipse(B(A(Text, (active ? 30 : 18) * fade)), null, item);
                double ic = S(18);
                SvgIcon.Named("sliders-horizontal")?.Draw(ctx, new Rect(item.Center.X - ic / 2, item.Center.Y - ic / 2, ic, ic), A(Text, (hot || active ? 255 : 185) * fade));
                if (p > 0.6) _hits.Add(new Hit(item, "header", "filter"));
                group = group.Union(item);
            }
        }
        _searchGroup = group;
        _searchPill = _searchOpen && p >= 0.999 ? r : default;
        if (!_searchOpen) _hits.Add(new Hit(r, "nav", "SearchOpen"));
        else _hits.Add(new Hit(r, "search-field"));
    }

    private string SortLabel() => SortName(_sortMode);
    private Rect _sortAnchor;

    // Come Windows: le foto si ordinano per data, nome o dimensione; gli altri per data, titolo, durata o anno.
    private string SortName(int mode) => _category == "Photos"
        ? mode switch { 1 => "Nome A–Z", 2 => "Meno recenti", 3 => "Dimensione", _ => "Più recenti" }
        : mode switch { 1 => "A–Z", 2 => "Durata", 3 => "Anno", _ => "Recenti" };

    private void OpenSortMenu()
    {
        var entries = Enumerable.Range(0, 4).Select(mode => new MenuEntry(SortName(mode), null, () => { _sortMode = mode; _scroll = _scrollTarget = 0; InvalidateVisual(); }) { Checked = _sortMode == mode }).ToList<MenuEntry>();
        ShowTextMenu(entries, new Point(_sortAnchor.X, _sortAnchor.Bottom + S(10)), "Filters");
    }

    /// <summary>ActiveFilterLabel di Windows.</summary>
    private string FilterLabel()
    {
        if (_filter.StartsWith("Artist:") || _filter.StartsWith("Director:")) return _filter[(_filter.IndexOf(':') + 1)..];
        if (_filter.StartsWith("Folder:")) return System.IO.Path.GetFileName(_filter[7..]);
        if (_filter.StartsWith("Year:")) return _filter[5..];
        if (_filter.StartsWith("Decade:")) return "Anni " + _filter[7..];
        if (_filter.StartsWith("Genre:")) return _filter[6..];
        return FilterName(_filter);
    }

    private static string FilterName(string key) => key switch
    {
        "All" => "Tutti", "4K" => "4K / HDR", "Resume" => "In corso", "Recent" => "Recenti", "Lossless" => "Lossless", "Atmos" => "Atmos",
        "Rated" => "Valutati", "TopRated" => "Classifica", _ => "Filtra",
    };

    /// <summary>FilterOptionsForCurrentCategory di Windows: le scelte rapide di ogni sezione.</summary>
    private string[] FilterOptions() => _category switch
    {
        "Music" => new[] { "All", "Lossless", "Atmos" },
        "Photos" => new[] { "All", "Recent" },
        "Videos" => new[] { "All", "Resume", "Recent" },
        "Playlists" => new[] { "All" },
        "WatchHistory" => new[] { "All", "Rated", "TopRated", "4K" },
        _ => new[] { "All", "4K", "Resume" },
    };

    /// <summary>Menu dei filtri come Windows: le scelte rapide, poi artista o regista, decennio, anno, genere (e cartella per le foto).</summary>
    private void OpenFilterMenu()
    {
        var entries = FilterOptions().Select(key => new MenuEntry(FilterName(key), null, () => SetFilter(key)) { Checked = _filter == key }).ToList<MenuEntry>();
        var titles = _items.Where(i => _category is "Favourites" or "WatchHistory" or "Playlists" || i.Category == _category).ToList();
        MenuEntry Sub(string label, string prefix, IEnumerable<(string Label, string Value)> values)
        {
            var children = values.Select(v => new MenuEntry(v.Label, null, () => SetFilter(prefix + v.Value)) { Checked = _filter == prefix + v.Value }).ToList<MenuEntry>();
            return new MenuEntry(label) { Children = children.Count > 0 ? children : new() { new MenuEntry("Nessun valore") { Enabled = false } }, Checked = _filter.StartsWith(prefix) };
        }
        if (_category is "Music" or "Movies" or "TV Series" or "Favourites" or "WatchHistory")
        {
            bool music = _category == "Music";
            var people = music ? MusicArtists(titles).Where(a => !IsVariousArtists(a.Name)).Select(a => a.Name)
                : titles.Select(i => i.Director).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!.Trim());
            entries.Add(Sub(music ? "Filtra per artista…" : "Filtra per regista…", music ? "Artist:" : "Director:",
                people.Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).Take(60).Select(n => (n, n))));
        }
        if (_category is "Movies" or "TV Series" or "Favourites" or "WatchHistory")
        {
            var years = titles.Where(i => i.Year is >= 1888 and <= 2100).Select(i => i.Year!.Value).ToList();
            entries.Add(Sub("Per decennio…", "Decade:", years.Select(y => y / 10 * 10).Distinct().OrderByDescending(y => y).Select(y => ("Anni " + y, y.ToString(CultureInfo.InvariantCulture)))));
            entries.Add(Sub("Per anno…", "Year:", years.Distinct().OrderByDescending(y => y).Take(60).Select(y => (y.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)))));
            entries.Add(Sub("Per genere…", "Genre:", titles.SelectMany(i => i.Genres).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).Select(g => (g, g))));
        }
        if (_category == "Photos" && ActiveServer == null)
            entries.Add(Sub("Filtra per cartella…", "Folder:", titles.Select(i => System.IO.Path.GetDirectoryName(i.Path) ?? "").Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).Select(f => (System.IO.Path.GetFileName(f), f))));
        // Ordinamento in cima, come prima voce con la sua lista.
        var sort = new MenuEntry("Ordina: " + SortLabel()) { Children = Enumerable.Range(0, 4)
            .Select(mode => new MenuEntry(SortName(mode), null, () => { _sortMode = mode; _scroll = _scrollTarget = 0; InvalidateVisual(); }) { Checked = _sortMode == mode }).ToList<MenuEntry>() };
        entries.Insert(0, sort);
        ShowTextMenu(entries, new Point(Math.Max(S(12), _filtersAnchor.Right - S(260)), _filtersAnchor.Bottom + S(10)), "Filters");
    }

    private void SetFilter(string filter) { _filter = filter; _scroll = _scrollTarget = 0; InvalidateVisual(); }

    private Dictionary<string, double>? _diaryRatings;
    private long _diaryRatingsVersion = -1;

    private double DiaryRating(string path)
    {
        long version = CinecorePlayer2025.Engines.WatchHistoryStore.StoreVersionTicks;
        if (_diaryRatings == null || version != _diaryRatingsVersion)
        {
            _diaryRatingsVersion = version;
            _diaryRatings = CinecorePlayer2025.Engines.WatchHistoryStore.LoadAll().Where(e => !string.IsNullOrWhiteSpace(e.MediaPath))
                .GroupBy(e => e.MediaPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Math.Clamp(g.First().Rating, 0, 5), StringComparer.OrdinalIgnoreCase);
        }
        return _diaryRatings.TryGetValue(path, out double v) ? v : 0;
    }

    private static bool IsLossless(LibraryItem item)
    {
        string ext = System.IO.Path.GetExtension(item.Path) ?? "";
        return ext.Equals(".flac", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".mka", StringComparison.OrdinalIgnoreCase) || ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase) ||
               (item.AudioLabel?.Contains("lossless", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>ApplyActiveFilter + ricerca + ordinamento, sulla raccolta aperta.</summary>
    private IEnumerable<LibraryItem> ApplyFilterSortSearch(IEnumerable<LibraryItem> source)
    {
        var tokens = Tokens(_query);
        if (tokens.Length > 0) source = source.Where(i => tokens.All(Haystack(i).Contains));
        source = _filter switch
        {
            "4K" => source.Where(i => i.Is4K || i.IsHdr),
            "Resume" => source.Where(i => ResumeOf(i) != null),
            "Recent" => source.Where(i => i.SortDateUtc > DateTime.UtcNow.AddDays(-30)),
            "Lossless" => source.Where(IsLossless),
            "Atmos" => source.Where(i => i.HasAtmos),
            "Rated" => source.Where(i => DiaryRating(i.Path) > 0),
            "TopRated" => source.Where(i => DiaryRating(i.Path) > 0),
            _ when _filter.StartsWith("Genre:", StringComparison.Ordinal) => source.Where(i => i.Genres.Any(g => string.Equals(g?.Trim(), _filter[6..], StringComparison.CurrentCultureIgnoreCase))),
            _ when _filter.StartsWith("Decade:", StringComparison.Ordinal) && int.TryParse(_filter[7..], out int d) => source.Where(i => i.Year >= d && i.Year < d + 10),
            _ when _filter.StartsWith("Year:", StringComparison.Ordinal) && int.TryParse(_filter[5..], out int y) => source.Where(i => i.Year == y),
            _ when _filter.StartsWith("Director:", StringComparison.Ordinal) => source.Where(i => string.Equals(i.Director?.Trim(), _filter[9..], StringComparison.CurrentCultureIgnoreCase)),
            _ when _filter.StartsWith("Artist:", StringComparison.Ordinal) => source.Where(i => MusicArtistArtworkService.Identity(AlbumArtist(i)) == MusicArtistArtworkService.Identity(_filter[7..])
                || MusicArtistArtworkService.Identity(i.ArtistName) == MusicArtistArtworkService.Identity(_filter[7..])),
            _ when _filter.StartsWith("Folder:", StringComparison.Ordinal) => source.Where(i => string.Equals(System.IO.Path.GetDirectoryName(i.Path), _filter[7..], StringComparison.OrdinalIgnoreCase)),
            _ => source,
        };
        // Classifica: dal voto del diario, il piu' alto per primo (come Windows).
        if (_filter == "TopRated")
        {
            var ranked = source.OrderByDescending(i => DiaryRating(i.Path)).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
            _diaryRank = ranked.Select((item, index) => (item.Path, index + 1)).GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase);
            return ranked;
        }
        if (_category == "Photos")
            return _sortMode switch
            {
                1 => source.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
                2 => source.OrderBy(i => i.SortDateUtc),
                3 => source.OrderByDescending(i => i.Bytes),
                _ => source.OrderByDescending(i => i.SortDateUtc),
            };
        return _sortMode switch
        {
            1 => source.OrderBy(i => i.SeriesTitle ?? i.Title, StringComparer.CurrentCultureIgnoreCase),
            2 => source.OrderByDescending(i => i.DurationMinutes ?? 0),
            3 => source.OrderByDescending(i => i.Year ?? 0),
            _ => source.OrderByDescending(i => i.SortDateUtc),
        };
    }

    // Testo su cui si cerca: titolo, serie, file, anno e generi, senza accenti e in minuscolo (TextFold).
    private static string Haystack(LibraryItem i) =>
        Fold(string.Join(" ", i.Title, i.SeriesTitle, i.ArtistName, i.AlbumArtist, i.AlbumTitle,
            i.FileName ?? System.IO.Path.GetFileNameWithoutExtension(i.Path), i.Year?.ToString(CultureInfo.InvariantCulture), string.Join(" ", i.Genres)));

    private static string[] Tokens(string query) => Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Minuscole senza accenti (l'app lavora senza ICU: niente Normalize, una tabella delle lettere latine).</summary>
    private static string Fold(string value)
    {
        const string from = "àáâãäåèéêëìíîïòóôõöùúûüýÿñçÀÁÂÃÄÅÈÉÊËÌÍÎÏÒÓÔÕÖÙÚÛÜÝÑÇ’‘`´";
        const string to = "aaaaaaeeeeiiiiooooouuuuyyncaaaaaaeeeeiiiiooooouuuuync''''";
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            int k = from.IndexOf(c);
            sb.Append(k >= 0 ? to[k] : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>RouteGlobalSearch: dalla Home (o con una ricerca gia' globale) si va alla sezione che ha risultati.</summary>
    private void OnQueryChanged(string text)
    {
        if (YouTubeOpen) { YouTubeQueryChanged(text); return; }
        _query = text;
        _scroll = _scrollTarget = 0;
        var tokens = Tokens(text);
        if (tokens.Length == 0) { _alsoIn.Clear(); InvalidateVisual(); return; }
        var matches = new List<(string Category, int Count, string First)>();
        foreach (var cat in new[] { "Movies", "TV Series" })
        {
            var pool = ActiveServer is { } server ? (NetworkStore.Catalog(server.Key) ?? Array.Empty<LibraryItem>()).Where(i => i.Category == cat) : LibraryStore.Items(cat);
            var hits = pool.Where(i => tokens.All(Haystack(i).Contains)).ToList();
            int count = cat == "TV Series" ? hits.Select(i => i.SeriesTitle ?? i.Title).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() : hits.Count;
            if (count > 0) matches.Add((cat, count, cat == "TV Series" ? hits[0].SeriesTitle ?? hits[0].Title : hits[0].Title));
        }
        // Finche' la pagina aperta ha risultati si resta li': saltare di sezione a ogni lettera disorienta.
        string? current = _view == "Collection" ? _category : null;
        string target = matches.Any(m => m.Category == current) ? current! : matches.FirstOrDefault().Category ?? current ?? "Movies";
        _alsoIn = matches.Where(m => m.Category != target).ToList();
        if (_view != "Collection" || _category != target) { _view = "Collection"; _category = target; Reload(); }
        InvalidateVisual();
    }

    /// <summary>"Anche in: Serie TV   Severance (+1)": solo testo, d'accento al passaggio.</summary>
    private void DrawAlsoIn(DrawingContext ctx, Rect row)
    {
        if (_alsoIn.Count == 0 || _query.Trim().Length == 0) return;
        double pt = Math.Max(8.8, 10.2 * _s), x = row.X;
        double leadW = Measure("Anche in", pt, DisplayRegular);
        TextAt(ctx, "Anche in", pt, DisplayRegular, UiTheme.Muted, new Rect(x, row.Y, leadW + 2, row.Height));
        x += leadW + S(10);
        foreach (var m in _alsoIn)
        {
            string text = (m.Category == "TV Series" ? "Serie TV" : "Film") + "   " + m.First + (m.Count > 1 ? $" (+{m.Count - 1})" : "");
            double w = Math.Min(S(360), Measure(text, pt, DisplaySemibold));
            if (x + w > row.Right) break;
            var link = new Rect(x, row.Y, w, row.Height);
            var touch = link.Inflate(new Thickness(S(6), S(4)));
            TextAt(ctx, text, pt, DisplaySemibold, Hover(touch) ? UiTheme.Accent : Text, link);
            _hits.Add(new Hit(touch, "also-in", m.Category));
            x = link.Right + S(22);
        }
    }

    private bool SearchActivate(Hit hit)
    {
        switch (hit.Kind)
        {
            case "header" when hit.Key == "sort": OpenSortMenu(); return true;
            case "header" when hit.Key == "filter": OpenFilterMenu(); return true;
            case "also-in": _view = "Collection"; _category = hit.Key; Reload(); OnQueryChanged(_query); return true;
            case "search-field": EnsureSearchBox().Focus(); return true;
            case "nav" when hit.Key == "SearchOpen":
                EnsureSearchBox();
                SetSearchOpen(true);
                DispatcherTimer.RunOnce(() => { PlaceSearchBox(); _searchBox?.Focus(); }, TimeSpan.FromMilliseconds(SearchAnimMs + 30));
                return true;
        }
        return false;
    }

    /// <summary>Un clic fuori dalla ricerca vuota la richiude.</summary>
    private void CloseSearchIfOutside(Point p)
    {
        if (_searchOpen && _query.Length == 0 && !_searchGroup.Contains(p) && _menuKey != "Filters" && !YouTubeOpen) SetSearchOpen(false);
    }
}
