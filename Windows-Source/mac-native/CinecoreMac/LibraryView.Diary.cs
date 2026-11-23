using System;
using Avalonia;
using Avalonia.Media;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Diario (WatchHistory di Windows): i film e gli episodi visti, dal piu' recente, dallo stesso watchHistory.json e con
// le stesse regole (DiaryMediaPolicy: niente musica, foto, video personali o YouTube).
internal sealed partial class LibraryView
{
    private readonly Dictionary<string, WatchHistoryStore.Entry> _diary = new(StringComparer.OrdinalIgnoreCase);
    // Titoli del diario che non sono in libreria: si riusano fra un ricaricamento e l'altro. Ricreati da zero perdevano
    // copertina e dati di TMDb scaricati dopo (eliminando una voce gli altri restavano senza metadati).
    private readonly Dictionary<string, LibraryItem> _diaryOrphans = new(StringComparer.OrdinalIgnoreCase);
    private bool _diaryRatingMode;
    private Dictionary<string, int> _diaryRank = new(StringComparer.OrdinalIgnoreCase);

    private List<LibraryItem> DiaryItems()
    {
        _diary.Clear();
        // Ogni titolo si mostra con i dati gia' noti (locandina, trama): libreria del Mac e cataloghi dei server.
        var known = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (string category in new[] { "Movies", "TV Series", "Videos" })
            foreach (var item in LibraryStore.Items(category)) known.TryAdd(item.Path, item);
        foreach (var server in NetworkStore.Servers)
            if (NetworkStore.Catalog(server.Key) is { } catalog)
                foreach (var item in catalog) known.TryAdd(item.Path, item);
        var result = new List<LibraryItem>();
        foreach (var entry in WatchHistoryStore.LoadAll().OrderByDescending(e => e.LastWatchedAtUtc))
        {
            if (!DiaryMediaPolicy.IsEligible(entry.MediaPath, entry.MediaCategory)) continue;
            _diary[entry.MediaPath] = entry;
            if (known.TryGetValue(entry.MediaPath, out var item)) { result.Add(item); continue; }
            if (_diaryOrphans.TryGetValue(entry.MediaPath, out var orphan)) { result.Add(orphan); continue; }
            bool series = (entry.MediaCategory ?? "").Contains("serie", StringComparison.OrdinalIgnoreCase) || LibraryStore.IsTvEpisodePath(entry.MediaPath);
            result.Add(_diaryOrphans[entry.MediaPath] = new LibraryItem
            {
                Path = entry.MediaPath,
                Title = string.IsNullOrWhiteSpace(entry.DisplayName) ? Path.GetFileNameWithoutExtension(entry.MediaPath) : entry.DisplayName,
                Category = series ? "TV Series" : "Movies",
                SortDateUtc = entry.LastWatchedAtUtc,
                IsNetwork = entry.MediaPath.StartsWith("http", StringComparison.OrdinalIgnoreCase),
            });
        }
        return result;
    }

    // Voto personale (DrawDiaryRating di Windows): cinque stelle sotto ogni titolo, mezze stelle a sinistra di ciascuna;
    // ripetere lo stesso voto lo toglie. Lo stesso watchHistory.json di Windows (WatchHistoryStore.SetRating).
    private void DrawDiaryStars(DrawingContext ctx, Rect row, LibraryItem item)
    {
        double rating = _diary.TryGetValue(item.Path, out var entry) ? entry.Rating : 0;
        double star = Math.Min(row.Height, Math.Max(12, (row.Width - 40) / 7)), gap = Math.Max(3, star * .25), x = row.X + 1;
        for (int i = 1; i <= 5; i++)
        {
            var box = new Rect(x, row.Y, star, star);
            double fill = Math.Clamp(rating - (i - 1), 0, 1);
            var shape = StarGeometry(box);
            ctx.DrawGeometry(B(Color.FromArgb(40, 255, 255, 255)), null, shape);
            if (fill > 0) using (ctx.PushClip(new Rect(box.X, box.Y, box.Width * fill, box.Height))) ctx.DrawGeometry(B(Color.FromRgb(244, 208, 126)), null, shape);
            _hits.Add(new Hit(new Rect(box.X, box.Y, box.Width / 2, box.Height), "diary-rate:" + (i * 2 - 1), item.Path, item));
            _hits.Add(new Hit(new Rect(box.X + box.Width / 2, box.Y, box.Width / 2, box.Height), "diary-rate:" + i * 2, item.Path, item));
            x += star + gap;
        }
        if (rating <= 0) return; // senza voto solo le stelle (la scritta "Valuta" accanto era di troppo)
        string score = rating.ToString(rating % 1 == 0 ? "0" : "0.0", MacTools.Italian);
        TextAt(ctx, score, Math.Max(8, 9 * PosterCardScale(row.Width)), DisplayRegular, rating > 0 ? Color.FromRgb(244, 208, 126) : A(Text, 150), new Rect(x + 4, row.Y, Math.Max(1, row.Right - x - 4), star));
    }

    private static StreamGeometry StarGeometry(Rect box)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        var c = box.Center; double outer = box.Width / 2, inner = outer * .45;
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5, radius = i % 2 == 0 ? outer : inner;
            var p = new Point(c.X + Math.Cos(angle) * radius, c.Y + Math.Sin(angle) * radius);
            if (i == 0) g.BeginFigure(p, true); else g.LineTo(p);
        }
        g.EndFigure(true);
        return geo;
    }

    private void SetDiaryRating(string path, int halfSteps)
    {
        double current = _diary.TryGetValue(path, out var entry) ? entry.Rating : 0, requested = Math.Clamp(halfSteps, 0, 10) / 2.0;
        double rating = Math.Abs(current - requested) < 0.01 ? 0 : requested;
        try { WatchHistoryStore.SetRating(path, rating); } catch { }
        if (entry != null) entry.Rating = rating;
        InvalidateVisual();
    }

    private void RemoveFromDiary(LibraryItem item)
    {
        try { WatchHistoryStore.Remove(item.Path); } catch { }
        _diaryOrphans.Remove(item.Path);
        Reload();
    }

    // ----- in alto a destra: Valuta e Classifica (DrawHeader di Windows) -----
    private void DrawDiaryHeader(DrawingContext ctx, Rect row)
    {
        bool ranking = _filter == "TopRated";
        var items = new[] { (_diaryRatingMode ? "Scegli un titolo" : "Valuta", "rate", _diaryRatingMode), (ranking ? "Tutto il diario" : "Classifica", "ranking", ranking) };
        double pt = Math.Max(9, 11.4 * _s), gap = S(26);
        double x = row.Right - items.Sum(i => Measure(i.Item1, pt, DisplayRegular)) - gap * (items.Length - 1);
        foreach (var (text, key, active) in items)
        {
            double w = Measure(text, pt, DisplayRegular);
            var r = new Rect(x, row.Y, w + 2, row.Height);
            var hit = r.Inflate(new Thickness(gap / 2 - 2, 0));
            bool hover = _menu == null && Hover(hit);
            TextAt(ctx, text, pt, DisplayRegular, active ? UiTheme.Accent : A(Text, hover ? 255 : 175), r);
            _hits.Add(new Hit(hit, "diary-header", key));
            x += w + gap;
        }
    }

    internal void TestDiary(string key) => DiaryHeaderActivate(key);

    private void DiaryHeaderActivate(string key)
    {
        if (key == "rate") _diaryRatingMode = !_diaryRatingMode;
        else { _diaryRatingMode = false; _filter = _filter == "TopRated" ? "All" : "TopRated"; _scroll = _scrollTarget = 0; }
        InvalidateVisual();
    }

    /// <summary>Valuta: dopo aver scelto il titolo, il voto da una lista di testo (come i menu della libreria).</summary>
    private void ShowDiaryRatingChoices(LibraryItem item, Point at)
    {
        double current = _diary.TryGetValue(item.Path, out var entry) ? entry.Rating : 0;
        var items = new List<MenuItem>();
        for (int half = 10; half >= 1; half--)
        {
            double value = half / 2.0;
            string label = new string('★', (int)value) + (half % 2 == 1 ? "½" : "") + "   " + value.ToString(half % 2 == 0 ? "0" : "0.0", MacTools.Italian);
            items.Add(new MenuItem(label, Math.Abs(current - value) < 0.01, () => { try { WatchHistoryStore.SetRating(item.Path, value); } catch { } if (entry != null) entry.Rating = value; InvalidateVisual(); }));
        }
        if (current > 0) items.Add(new MenuItem("Togli il voto", false, () => { try { WatchHistoryStore.SetRating(item.Path, 0); } catch { } if (entry != null) entry.Rating = 0; InvalidateVisual(); }));
        OpenMenu("Item", items, at);
    }

    /// <summary>Classifica: la posizione in un riquadro di vetro nell'angolo della locandina (non il blu di Windows).</summary>
    private void DrawDiaryRank(DrawingContext ctx, Rect poster, LibraryItem item)
    {
        if (_filter != "TopRated" || !_diaryRank.TryGetValue(item.Path, out int rank)) return;
        string text = rank.ToString(System.Globalization.CultureInfo.InvariantCulture);
        double pt = Math.Max(10, 13 * PosterCardScale(poster.Width)), h = Math.Round(pt * 2.4), w = Math.Max(h, Measure(text, pt, DisplaySemibold) + h * .7);
        var r = new Rect(poster.X + h * .3, poster.Y + h * .3, w, h);
        double radius = h * .32;
        ctx.DrawRectangle(B(Color.FromArgb(70, 0, 0, 0)), null, r.Translate(new Vector(0, 1.5)).Inflate(1), radius + 1, radius + 1);
        var body = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(120, 70, 74, 82), 0), new GradientStop(Color.FromArgb(150, 24, 26, 32), 1) },
        };
        ctx.DrawRectangle(body, null, r, radius, radius);
        DrawGlassRim(ctx, r, radius, false);
        TextAt(ctx, text, pt, DisplaySemibold, Colors.White, r, TextAlignment.Center);
    }
}
