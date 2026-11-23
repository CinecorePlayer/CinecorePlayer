using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// Scheda di una serie (SeriesSheet.cs), gemella di quella dei film: in alto la serie (sfondo, locandina, titolo,
/// dati, trama, "Guarda l'episodio 01" o "Riprendi l'episodio 03"), sotto "Stagione 1 ˅" con il riepilogo e gli
/// episodi come card 16:9 che scorrono (foto, numero, titolo, durata, trama). Le frecce compaiono passando sulla riga.
/// </summary>
internal sealed partial class LibraryView
{
    private string? _seriesName;
    private List<LibraryItem> _seriesEpisodes = new();
    private int _season, _seriesFirst, _seriesSelected;
    private double _seriesScrollFrom;
    private DateTime _seriesScrollAt = DateTime.MinValue;
    private const double SeriesScrollMs = 380;
    private DateTime _seriesOpenedAt;
    // Il mouse conta solo se si e' mosso: mentre la riga scorre con le frecce le card passano sotto il puntatore
    // fermo e l'evidenziazione saltava da sola.
    private bool _seriesPointerLive;

    private static string SeriesKey(LibraryItem i) => (i.SeriesTitle ?? i.Title).Trim();

    private void OpenSeries(LibraryItem anyEpisode)
    {
        string name = SeriesKey(anyEpisode);
        var pool = ActiveServer is { } server ? NetworkStore.Catalog(server.Key) ?? (IReadOnlyList<LibraryItem>)_items : LibraryStore.Items("TV Series");
        _seriesEpisodes = pool.Where(i => i.Category == "TV Series" && string.Equals(SeriesKey(i), name, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(i => i.SeasonNumber ?? 1).ThenBy(i => i.EpisodeNumber ?? 0).ThenBy(i => i.Title).ToList();
        if (_seriesEpisodes.Count == 0) return;
        _seriesName = name;
        NativeDiagnostics.Context = "serie: " + name + " (" + _seriesEpisodes.Count + " episodi)";
        // Si parte dalla stagione dell'episodio da cui ripartire.
        var (target, _) = SeriesTarget(_seriesEpisodes);
        _season = _seriesEpisodes[target].SeasonNumber ?? 1;
        var season = SeasonEpisodes();
        if (ActiveServer is { IsJellyfin: false }) NetworkStore.QueueDlnaIntroOutro(_seriesEpisodes[target].Path);
        _seriesSelected = Math.Max(0, season.IndexOf(_seriesEpisodes[target]));
        _seriesFirst = 0; _seriesScrollFrom = 0; _seriesScrollAt = DateTime.MinValue;
        _detailInfo = false;
        _seriesOpenedAt = DateTime.UtcNow;
        var first = _seriesEpisodes[0];
        _ = System.Threading.Tasks.Task.Run(async () => { if (await NetworkStore.LoadSeriesAsync(first)) Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual); });
        LibraryStore.QueueArtwork(first, includeBackdrop: true);
        _frames.Start();
        InvalidateVisual();
    }

    private void CloseSeries() { NativeDiagnostics.Context = ""; _seriesName = null; _menu = null; _menuKey = null; InvalidateVisual(); }

    private List<int> Seasons() => _seriesEpisodes.Select(e => e.SeasonNumber ?? 1).Distinct().OrderBy(n => n).ToList();
    private List<LibraryItem> SeasonEpisodes() => _seriesEpisodes.Where(e => (e.SeasonNumber ?? 1) == _season).ToList();

    /// <summary>L'episodio da cui ripartire: l'ultimo iniziato, altrimenti il primo.</summary>
    private (int Index, bool Resume) SeriesTarget(List<LibraryItem> episodes)
    {
        int best = -1; DateTime bestAt = DateTime.MinValue;
        for (int i = 0; i < episodes.Count; i++)
            if (ResumeOf(episodes[i]) is { } r && r.Position > 0 && r.SavedAt >= bestAt) { best = i; bestAt = r.SavedAt; }
        return best >= 0 ? (best, true) : (0, false);
    }

    private static string Ordinal(LibraryItem e, int index) => (e.EpisodeNumber ?? index + 1).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Il titolo dell'episodio senza la serie e il numero davanti.</summary>
    private static string EpisodeName(LibraryItem e)
    {
        string t = e.Title;
        if (e.SeriesTitle != null && t.StartsWith(e.SeriesTitle, StringComparison.OrdinalIgnoreCase)) t = t[e.SeriesTitle.Length..];
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^\s*[-–:]?\s*(S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|E\d{1,3})?\s*[-–:]?\s*", "");
        return t.Length > 0 ? t : "Episodio " + (e.EpisodeNumber ?? 0);
    }

    private double SeriesScrollNow()
    {
        double t = (DateTime.UtcNow - _seriesScrollAt).TotalMilliseconds / SeriesScrollMs;
        if (t >= 1) return _seriesFirst;
        double eased = 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
        return _seriesScrollFrom + (_seriesFirst - _seriesScrollFrom) * eased;
    }

    private void ScrollSeriesTo(int first)
    {
        if (first == _seriesFirst) return;
        _seriesScrollFrom = SeriesScrollNow();
        _seriesScrollAt = DateTime.UtcNow;
        _seriesFirst = first;
        _frames.Start();
    }

    private void DrawSeries(DrawingContext ctx, Rect bounds)
    {
        if (_seriesName is not { } name || _seriesEpisodes.Count == 0) return;
        double scale = DetailScale(bounds);
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        var modal = DetailSheetBounds(bounds, scale);
        ctx.FillRectangle(B(Color.FromArgb(176, 0, 4, 8)), bounds);
        _hits.Add(new Hit(bounds, "series-outside"));
        double fade = Math.Clamp((DateTime.UtcNow - _seriesOpenedAt).TotalMilliseconds / 200, 0, 1);
        using var opacity = ctx.PushOpacity(1 - (1 - fade) * (1 - fade));
        var first = _seriesEpisodes[0];
        var info = NetworkStore.Series(first.SeriesId);
        // La serie come elemento a se': locandina e sfondo della serie, trama e cast dal server.
        var series = new LibraryItem
        {
            Path = first.Path, Title = name, Category = "TV Series", Year = first.Year,
            ArtPath = first.ArtPath, WideArtPath = first.WideArtPath,
            Overview = info?.Overview ?? "", Genres = info?.Genres ?? first.Genres,
            Director = info?.Director, DirectorPhoto = info?.DirectorPhoto, Cast = info?.Cast ?? new(),
        };
        using (ctx.PushClip(new RoundedRect(modal, D(10)))) DrawDetailBackdrop(ctx, modal, series);
        _hits.Add(new Hit(modal, "series-sheet"));
        var close = new Rect(modal.Right - D(62), modal.Y + D(20), D(40), D(40));
        double arm = Math.Max(8, close.Width * .42) / 2, cx = close.Center.X, cy = close.Center.Y;
        var xPen = new Pen(B(Hover(close) ? Colors.White : Color.FromArgb(165, 255, 255, 255)), Math.Max(1.7, close.Width * .062), lineCap: PenLineCap.Round);
        ctx.DrawLine(xPen, new Point(cx - arm, cy - arm), new Point(cx + arm, cy + arm));
        ctx.DrawLine(xPen, new Point(cx + arm, cy - arm), new Point(cx - arm, cy + arm));
        _hits.Add(new Hit(close, "series-close"));

        var seasons = Seasons();
        var season = SeasonEpisodes();
        var (target, resume) = SeriesTarget(season);
        var targetEpisode = season[Math.Clamp(target, 0, season.Count - 1)];
        string playText = (resume ? "Riprendi l'episodio " : "Guarda l'episodio ") + Ordinal(targetEpisode, target);
        var meta = new List<(string Icon, string Text, bool Badge)>
        {
            ("calendar", first.Year?.ToString(CultureInfo.InvariantCulture) ?? "", false),
            ("", seasons.Count > 1 ? $"{seasons.Count} stagioni" : $"{season.Count} episodi", false),
            ("", QualityBadge(first), true),
            ("", string.Join(", ", series.Genres.Take(2)), false),
        };
        var poster = DrawDetailHeader(ctx, modal, series, scale, name, meta, series.Overview, playText, "series-play", targetEpisode);

        double edge = D(34), sectionTop = poster.Bottom + D(30);
        var area = new Rect(modal.X + D(56), sectionTop, modal.Width - D(112), Math.Max(1, modal.Bottom - edge - sectionTop));
        if (_detailInfo) { DrawDetailSpecs(ctx, area, season[Math.Clamp(_seriesSelected, 0, season.Count - 1)], scale); return; }
        DrawSeriesEpisodes(ctx, area, season, seasons, scale);
    }

    private Point _seasonAnchor;

    private void DrawSeriesEpisodes(DrawingContext ctx, Rect r, List<LibraryItem> episodes, List<int> seasons, double scale)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        // "Stagione 1 ˅" come i menu della barra, sotto il riepilogo (episodi, durata totale, dove eri rimasto).
        double rowH = D(34), stagePt = Math.Max(11, 15 * scale);
        string label = "Stagione " + _season.ToString(CultureInfo.InvariantCulture);
        double lw = Measure(label, stagePt, DisplaySemibold);
        var seasonHit = new Rect(r.X, r.Y, lw + (seasons.Count > 1 ? D(30) : 0), rowH).Inflate(new Thickness(D(8), D(4)));
        bool seasonHot = seasons.Count > 1 && (Hover(seasonHit) || _menuKey == "Season");
        TextAt(ctx, label, stagePt, DisplaySemibold, seasonHot ? Colors.White : Color.FromRgb(244, 248, 252), new Rect(r.X, r.Y, lw + 2, rowH));
        if (seasons.Count > 1)
        {
            Chevron(ctx, r.X + lw + D(16), r.Y + rowH / 2 + D(2), D(6), seasonHot ? Colors.White : Color.FromArgb(200, 255, 255, 255), Math.Max(1.3, 1.6 * scale), _menuKey == "Season");
            _seasonAnchor = new Point(r.X, r.Y + rowH + D(8));
            _hits.Add(new Hit(seasonHit, "series-season"));
        }
        double minutes = episodes.Sum(e => e.DurationMinutes ?? 0);
        var summary = new List<string> { $"{episodes.Count} episodi" };
        if (minutes >= 1)
        {
            int h = (int)(minutes / 60), m = (int)Math.Round(minutes % 60);
            summary.Add(h > 0 ? $"{h} h {m:00} min in tutto" : $"{m} min in tutto");
        }
        for (int i = 0; i < episodes.Count; i++)
            if (ResumeOf(episodes[i]) is { } rp && rp.Position / Math.Max(1, rp.Duration) > 0.005) { summary.Add("in corso: " + Ordinal(episodes[i], i) + "  " + EpisodeName(episodes[i])); break; }
        double summaryTop = r.Y + rowH + D(6);
        TextAt(ctx, string.Join("   ·   ", summary), Math.Max(8.6, 10.8 * scale), DisplayRegular, Color.FromRgb(178, 192, 204), new Rect(r.X, summaryTop, r.Width, D(24)));

        // Le card prendono tutta la larghezza; le frecce stanno nei margini.
        double top = summaryTop + D(24) + D(28), gap = D(18), preferred = D(250);
        int visible = (int)Math.Clamp(Math.Floor((r.Width + gap) / Math.Max(1, preferred + gap)), 2, 6);
        visible = Math.Max(1, Math.Min(visible, episodes.Count));
        double cardW = Math.Floor((r.Width - gap * (visible - 1)) / visible), imageH = Math.Round(cardW * 9 / 16);
        int maxFirst = Math.Max(0, episodes.Count - visible);
        _seriesFirst = Math.Clamp(_seriesFirst, 0, maxFirst);
        double scroll = Math.Clamp(SeriesScrollNow(), 0, maxFirst);
        double bodyPt = Math.Max(8.2, 9.6 * scale), bodyLineH = Format("Ag", bodyPt, DisplayRegular, Colors.White).Height;
        int bodyLines = (int)Math.Clamp(Math.Floor((r.Bottom - (top + imageH + D(56))) / Math.Max(1, bodyLineH)), 0, 4);
        double textBlock = D(50) + bodyLines * bodyLineH;
        var rowArea = new Rect(r.X, top - D(12), r.Width, imageH + textBlock + D(24));
        bool pointerOnCard = false;
        using (ctx.PushClip(new Rect(r.X - D(8), top - D(12), r.Width + D(16), imageH + textBlock + D(24))))
        {
            int firstIndex = Math.Max(0, (int)Math.Floor(scroll)), lastIndex = Math.Min(episodes.Count - 1, (int)Math.Ceiling(scroll) + visible - 1);
            for (int i = firstIndex; i <= lastIndex; i++)
                if (_seriesPointerLive && Hover(new Rect(r.X + Math.Round((i - scroll) * (cardW + gap)), top, cardW, imageH + textBlock))) pointerOnCard = true;
            for (int i = firstIndex; i <= lastIndex; i++)
            {
                var e = episodes[i];
                double x = r.X + Math.Round((i - scroll) * (cardW + gap));
                var cardArea = new Rect(x, top, cardW, imageH + textBlock);
                bool hover = _seriesPointerLive && Hover(cardArea);
                // L'evidenziazione resta sull'ultimo episodio toccato: uscendo dalle card non torna sul primo.
                if (hover) _seriesSelected = i;
                bool lit = hover || (i == _seriesSelected && !pointerOnCard);
                // Un solo episodio acceso: la card sale appena, contorno pieno; gli altri restano tenui.
                var card = new Rect(x, top - (lit ? D(4) : 0), cardW, imageH);
                using (ctx.PushClip(new RoundedRect(card, D(8))))
                {
                    var bmp = Image(e.Still ?? EpisodeStill(e), card.Width);
                    if (bmp != null) DrawCover(ctx, bmp, card, .5, .4);
                    else
                    {
                        // Senza foto: fondo scuro sfumato e il numero dell'episodio.
                        ctx.FillRectangle(new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                            GradientStops = { new GradientStop(Color.FromRgb(34, 40, 50), 0), new GradientStop(Color.FromRgb(16, 19, 25), 1) },
                        }, card);
                        TextAt(ctx, Ordinal(e, i), Math.Max(18, 34 * scale), DisplaySemibold, Color.FromArgb(70, 255, 255, 255), card, TextAlignment.Center);
                    }
                    if (ResumeOf(e) is { } rp && rp.Duration > 0 && rp.Position / rp.Duration > 0.005)
                    {
                        var bar = new Rect(card.X, card.Bottom - D(4), card.Width, D(4));
                        ctx.FillRectangle(B(Color.FromRgb(68, 88, 106)), bar);
                        ctx.FillRectangle(B(UiTheme.Accent), new Rect(bar.X, bar.Y, Math.Round(bar.Width * Math.Clamp(rp.Position / rp.Duration, 0, 1)), bar.Height));
                    }
                    if (!lit) ctx.FillRectangle(B(Color.FromArgb(70, 0, 0, 0)), card);
                }
                if (lit) ctx.DrawRectangle(null, new Pen(Brushes.White, Math.Max(2, 2.4 * scale)), card.Deflate(1), D(8), D(8));
                else ctx.DrawRectangle(null, new Pen(B(Color.FromArgb(18, 255, 255, 255)), 1), card.Deflate(.5), D(8), D(8));
                double textTop = card.Bottom + D(10), titlePt = Math.Max(9.2, 11.2 * scale), metaPt = Math.Max(8.2, 10 * scale);
                string number = Ordinal(e, i);
                double nw = Measure(number, titlePt, DisplaySemibold);
                string dur = e.DurationMinutes is > 0 ? FormatMinutes(e.DurationMinutes.Value) : "";
                double dw = Measure(dur, metaPt, DisplayRegular) + 2;
                TextAt(ctx, number, titlePt, DisplaySemibold, Colors.White, new Rect(card.X, textTop, nw + 2, D(22)));
                TextAt(ctx, EpisodeName(e), titlePt, DisplaySemibold, Colors.White, new Rect(card.X + nw + D(10), textTop, Math.Max(1, card.Width - nw - D(18) - dw), D(22)));
                TextAt(ctx, dur, metaPt, DisplayRegular, Color.FromRgb(185, 196, 208), new Rect(card.Right - dw, textTop, dw, D(22)));
                if (bodyLines > 0 && !string.IsNullOrWhiteSpace(e.Overview))
                {
                    var body = UiText.Get(L10n.T(e.Overview!), DisplayRegular, Px(bodyPt), Color.FromRgb(190, 204, 216), card.Width, bodyLines, trimming: TextTrimming.WordEllipsis);
                    ctx.DrawText(body, new Point(card.X, textTop + D(30)));
                }
                _hits.Add(new Hit(cardArea.Intersect(new Rect(r.X, top - D(8), r.Width, cardArea.Height + D(8))), "series-episode", i.ToString(CultureInfo.InvariantCulture), e));
            }
        }
        // Le frecce compaiono passando sulla riga, nei margini, e spostano di un episodio alla volta.
        if (episodes.Count > visible && Hover(rowArea.Inflate(new Thickness(D(56), 0))))
        {
            double aw = D(38), ay = top + Math.Max(0, (imageH - aw) / 2);
            if (_seriesFirst > 0) DrawRoundArrow(ctx, new Rect(r.X - aw - D(8), ay, aw, aw), false, "series-prev", scale);
            if (_seriesFirst < maxFirst) DrawRoundArrow(ctx, new Rect(r.Right + D(8), ay, aw, aw), true, "series-next", scale);
        }
    }

    private void DrawRoundArrow(DrawingContext ctx, Rect r, bool right, string kind, double scale)
    {
        bool hover = Hover(r);
        ctx.DrawEllipse(B(hover ? Color.FromArgb(98, 14, 26, 38) : Color.FromArgb(70, 4, 12, 20)), null, r.Center, r.Width / 2, r.Height / 2);
        Chevron(ctx, r.Center.X, r.Center.Y, r.Width / 7, Colors.White, Math.Max(1.6, 2 * scale), left: !right, right: right);
        _hits.Add(new Hit(r, kind));
    }

    private bool SeriesActivate(Hit hit)
    {
        if (_seriesName == null) return false;
        switch (hit.Kind)
        {
            case "series-close": case "series-outside": CloseSeries(); return true;
            case "series-play": case "series-episode":
                if (hit.Item is { } e) { CloseSeries(); PlayRequested?.Invoke(e, ResumeOf(e)?.Position); }
                return true;
            case "series-prev": _seriesPointerLive = false; ScrollSeriesTo(Math.Max(0, _seriesFirst - 1)); return true;
            case "series-next": _seriesPointerLive = false; ScrollSeriesTo(_seriesFirst + 1); return true;
            case "series-season":
                OpenMenu("Season", Seasons().Select(n => new MenuItem("Stagione " + n, n == _season, () =>
                {
                    _season = n; _seriesFirst = 0; _seriesScrollAt = DateTime.MinValue; _seriesSelected = 0; InvalidateVisual();
                    if (ActiveServer is { IsJellyfin: false } && SeasonEpisodes().FirstOrDefault() is { } episode)
                        NetworkStore.QueueDlnaIntroOutro(episode.Path);
                })), _seasonAnchor);
                return true;
            case "detail-view":
                OpenMenu("DetailView", new[]
                {
                    new MenuItem("Panoramica", !_detailInfo, () => { _detailInfo = false; InvalidateVisual(); }),
                    new MenuItem("Dettagli", _detailInfo, () => { _detailInfo = true; InvalidateVisual(); }),
                }, _detailViewAnchor);
                return true;
            case "detail-favorite":
                if (_seriesEpisodes.Count > 0) { Favorites.Toggle(_seriesEpisodes[0].Path); InvalidateVisual(); }
                return true;
        }
        return true;
    }
}


internal sealed partial class LibraryView
{
    // Foto degli episodi in locale (QueueEpisodeStillResolve di Windows): cache dell'indice TMDb, poi rete in sottofondo.
    private readonly Dictionary<string, string?> _episodeStills = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Threading.SemaphoreSlim EpisodeStillGate = new(3);

    private string? EpisodeStill(LibraryItem episode)
    {
        if (episode.IsNetwork || episode.Path.Contains("://", StringComparison.Ordinal)) return null;
        if (_episodeStills.TryGetValue(episode.Path, out var known)) return known;
        _episodeStills[episode.Path] = null;
        string path = episode.Path;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            string? found = null;
            await EpisodeStillGate.WaitAsync();
            try
            {
                found = CinecorePlayer2025.Utilities.MovieMetadataService.GetCachedBackdropPath(path);
                if (string.IsNullOrWhiteSpace(found) || !System.IO.File.Exists(found))
                {
                    found = null;
                    // La chiave (anche quella incorporata) la controlla ResolveEpisodeStillPath.
                    {
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                        found = CinecorePlayer2025.Utilities.MovieMetadataService.ResolveEpisodeStillPath(path, cts.Token);
                    }
                }
            }
            catch { found = null; }
            finally { EpisodeStillGate.Release(); }
            if (found == null || !System.IO.File.Exists(found)) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _episodeStills[path] = found; InvalidateVisual(); });
        });
        return null;
    }
}
