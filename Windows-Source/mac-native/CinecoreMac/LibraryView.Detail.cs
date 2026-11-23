using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Scheda dei dettagli di un film o di un episodio (Rendering.Details.cs, DetailHeader.cs, DetailPanorama.cs):
/// foto in alto sfumata, locandina a sinistra, titolo, riga di dati con la qualita' in un riquadro, trama, Riproduci,
/// il menu della vista (Panoramica, Dettagli), preferiti; sotto la panoramica con regia e cast, poi i Consigliati.
/// La scheda e' sempre scura, come su Windows (_darkSurfaceText).
/// </summary>
internal sealed partial class LibraryView
{
    private LibraryItem? _detail;
    private IReadOnlyList<LibraryItem> _detailCandidates = Array.Empty<LibraryItem>();
    private bool _detailInfo;
    private DateTime _detailOpenedAt;

    private void OpenDetail(LibraryItem item)
    {
        _detail = item;
        _detailCandidates = ActiveServer is { } server ? NetworkStore.Catalog(server.Key) ?? _items : LibraryStore.Items(item.Category);
        _detailInfo = _detailReviews = _detailCast = false; _detailCastFirst = 0;
        _reviewsScroll = 0; _reviewsSource = "all";
        _detailOpenedAt = DateTime.UtcNow;
        LibraryStore.QueueArtwork(item, includeBackdrop: true);
        // Regia e cast: dal server per i titoli Jellyfin; compaiono appena arrivano.
        _ = System.Threading.Tasks.Task.Run(async () => { if (await NetworkStore.LoadPeopleAsync(item)) Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual); });
        _frames.Start();
        InvalidateVisual();
    }

    private void CloseDetail() { _detail = null; _detailCandidates = Array.Empty<LibraryItem>(); _menu = null; _menuKey = null; InvalidateVisual(); }

    private double DetailScale(Rect bounds) =>
        Math.Max(.45, Math.Min(Math.Clamp(Math.Min(bounds.Width / 1460, bounds.Height / 950) * Density, .45, 4), bounds.Height / 900));

    private Rect DetailSheetBounds(Rect bounds, double scale)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        double maxW = Math.Max(1, bounds.Width - D(36)), maxH = Math.Max(1, bounds.Height - D(36));
        double w = Math.Max(Math.Min(maxW, D(1040)), Math.Min(maxW, D(1320)));
        double h = Math.Max(Math.Min(maxH, D(700)), Math.Min(maxH, D(860)));
        return new Rect(bounds.X + Math.Round((bounds.Width - w) / 2), bounds.Y + Math.Round((bounds.Height - h) / 2), w, h);
    }

    private static readonly Color DarkBack = UiTheme.DefaultPanel;

    private void DrawDetail(DrawingContext ctx, Rect bounds)
    {
        if (_detail is not { } item) return;
        double scale = DetailScale(bounds);
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        var modal = DetailSheetBounds(bounds, scale);
        // Velatura solo attorno alla scheda; sotto la scheda e' opaca. La scheda e' modale: tutto il resto la chiude.
        ctx.FillRectangle(B(Color.FromArgb(176, 0, 4, 8)), bounds);
        _hits.Add(new Hit(bounds, "detail-outside"));
        double fade = Math.Clamp((DateTime.UtcNow - _detailOpenedAt).TotalMilliseconds / 200, 0, 1);
        using var opacity = ctx.PushOpacity(1 - (1 - fade) * (1 - fade));
        using (ctx.PushClip(new RoundedRect(modal, D(10))))
            DrawDetailBackdrop(ctx, modal, item);
        _hits.Add(new Hit(modal, "detail-sheet"));

        // X di chiusura in alto a destra (solo la X, chiara anche sulle foto).
        var close = new Rect(modal.Right - D(62), modal.Y + D(20), D(40), D(40));
        bool closeHot = Hover(close);
        double arm = Math.Max(8, close.Width * .42) / 2, cx = close.Center.X, cy = close.Center.Y;
        var xPen = new Pen(B(closeHot ? Colors.White : Color.FromArgb(165, 255, 255, 255)), Math.Max(1.7, close.Width * .062), lineCap: PenLineCap.Round);
        ctx.DrawLine(xPen, new Point(cx - arm, cy - arm), new Point(cx + arm, cy + arm));
        ctx.DrawLine(xPen, new Point(cx + arm, cy - arm), new Point(cx - arm, cy + arm));
        _hits.Add(new Hit(close, "detail-close"));

        var poster = DrawDetailHeader(ctx, modal, item, scale);
        double edge = D(34), bottom = modal.Bottom - edge, below = poster.Bottom + D(34);
        var lower = new Rect(modal.X + D(56), below, modal.Width - D(112), Math.Max(1, bottom - below));
        if (_detailInfo) { DrawDetailSpecs(ctx, lower, item, scale); return; }
        if (_detailReviews) { DrawDetailReviews(ctx, lower, item, scale); return; }
        if (_detailCast) { DrawDetailCast(ctx, lower, item, scale); return; }
        double used = DrawDetailPanorama(ctx, new Rect(lower.X, lower.Y, lower.Width, D(100)), item, scale);
        // Senza regia ne' cast la fascia sparisce e i Consigliati salgono.
        double recTop = used > 0 ? lower.Y + used + D(22) : lower.Y;
        var recommended = Recommendations(item);
        if (recommended.Count > 0 && bottom - recTop >= D(82))
            DrawRecommendations(ctx, new Rect(lower.X, recTop, lower.Width, bottom - recTop), recommended, scale);
    }

    /// <summary>La foto orizzontale solo in alto, sfumata da sinistra (li' sta la locandina) e verso il basso.</summary>
    private void DrawDetailBackdrop(DrawingContext ctx, Rect modal, LibraryItem item)
    {
        ctx.FillRectangle(B(DarkBack), modal);
        var hero = new Rect(modal.X, modal.Y, modal.Width, Math.Round(modal.Height * 0.62));
        var bmp = Image(item.WideArtPath ?? item.ArtPath, hero.Width);
        if (bmp != null) DrawCover(ctx, bmp, hero, .5, .3);
        else DrawGeneratedArt(ctx, hero, item.Title, hero: true, item.Category);
        ctx.FillRectangle(B(A(DarkBack, 70)), hero);
        ctx.FillRectangle(Horizontal(DarkBack, (0, 250), (0.16, 238), (0.3, 200), (0.46, 132), (0.62, 64), (0.8, 18), (1, 0)), hero);
        ctx.FillRectangle(Vertical(DarkBack, (0, 0), (0.35, 10), (0.6, 70), (0.8, 170), (0.92, 232), (1, 255)), hero);
    }

    private Rect DrawDetailHeader(DrawingContext ctx, Rect modal, LibraryItem item, double scale,
        string? titleText = null, List<(string Icon, string Text, bool Badge)>? metaRow = null, string? overviewText = null,
        string? playLabel = null, string playKind = "detail-play", LibraryItem? playItem = null, bool viewMenu = true)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        var poster = new Rect(modal.X + D(56), modal.Y + D(76), D(236), D(354));
        using (ctx.PushClip(new RoundedRect(poster, D(8))))
        {
            var art = Image(item.ArtPath, poster.Width);
            if (art != null) DrawCover(ctx, art, poster);
            else DrawGeneratedArt(ctx, poster, titleText ?? DetailTitle(item), false, item.Category);
        }
        double x = poster.Right + D(40);
        double textW = Math.Max(D(360), Math.Min(D(720), modal.Right - x - D(80)));
        double y = poster.Y + D(22);
        y += D(40) + D(12); // spazio del logo della casa di produzione (arriva da TMDb)

        // Titolo: grande, al massimo due righe; il carattere si riduce invece di tagliare.
        CachedText title;
        for (double size = Math.Max(20, 34 * scale); ; size *= 0.92)
        {
            title = UiText.Get(L10n.T(titleText ?? DetailTitle(item)), DisplaySemibold, Px(size), Colors.White, textW, 0, trimming: TextTrimming.None);
            double one = Format("Ag", size, DisplaySemibold, Colors.White).Height;
            if (title.Height <= one * 2.1 || size < 18) break;
        }
        ctx.DrawText(title, new Point(x, y));
        y += title.Height + D(14);

        // Riga dei dati con le icone; la qualita' in un riquadro.
        double rowH = D(28), icon = D(20), mx = x;
        var meta = metaRow ?? new List<(string Icon, string Text, bool Badge)>
        {
            ("calendar", item.Year?.ToString(CultureInfo.InvariantCulture) ?? "", false),
            ("time", item.DurationMinutes is > 0 ? FormatMinutes(item.DurationMinutes.Value) : "", false),
            ("", QualityBadge(item), true),
            ("", string.Join(", ", item.Genres.Take(2)), false),
        };
        foreach (var m in meta.Where(m => !string.IsNullOrWhiteSpace(m.Text)))
        {
            if (mx > x + textW - D(60)) break;
            if (m.Badge)
            {
                double pt = Math.Max(7.4, 9.4 * scale);
                double bw = Measure(m.Text, pt, DisplaySemibold) + D(16);
                var badge = new Rect(mx, y + (rowH - D(24)) / 2, bw, D(24));
                ctx.DrawRectangle(B(Color.FromArgb(34, 255, 255, 255)), null, badge, D(5), D(5));
                TextAt(ctx, m.Text, pt, DisplaySemibold, Colors.White, badge, TextAlignment.Center);
                mx = badge.Right + D(24);
                continue;
            }
            if (m.Icon.Length > 0)
            {
                SvgIcon.Get(m.Icon)?.Draw(ctx, new Rect(mx, y + (rowH - icon) / 2, icon, icon), Color.FromRgb(225, 232, 238));
                mx += icon + D(9);
            }
            double mpt = Math.Max(8, 10.6 * scale);
            double tw = Math.Min(Measure(m.Text, mpt, DisplayRegular) + 2, x + textW - mx);
            TextAt(ctx, m.Text, mpt, DisplayRegular, Color.FromRgb(232, 238, 244), new Rect(mx, y, tw, rowH));
            mx += tw + D(24);
        }
        y += rowH + D(18);

        // Trama: tre righe al massimo.
        double bodyPt = Math.Max(8.2, 10.2 * scale);
        string overview = overviewText ?? HeroTaglineFull(item);
        var body = UiText.Get(L10n.T(overview), DisplayRegular, Px(bodyPt), Color.FromRgb(222, 230, 238), Math.Min(textW, D(600)), 3, trimming: TextTrimming.WordEllipsis);
        double lineH = Format("Ag", bodyPt, DisplayRegular, Colors.White).Height;
        if (overview.Length > 0) ctx.DrawText(body, new Point(x, y));
        y += Math.Ceiling(lineH * 3) + 2 + D(26);
        y = Math.Min(y, poster.Bottom - D(48));

        // Azioni: Riproduci (o Riprendi), la vista in una pillola con il bordo, la stella dei preferiti.
        double h = D(48), glyph = D(14), playPt = Math.Max(8.4, 10.4 * scale);
        bool resume = ResumeOf(item) is { } r0 && r0.Position > 0;
        string playText = playLabel ?? (resume ? "Riprendi" : "Riproduci");
        double pw = Measure(playText, playPt, DisplaySemibold) + glyph + D(66);
        var play = new Rect(x, y, pw, h);
        ctx.DrawRectangle(B(Color.FromArgb((byte)(Hover(play) ? 228 : 250), 255, 255, 255)), null, play, h / 2, h / 2);
        double gx = play.X + D(28), gy = play.Y + (h - glyph) / 2;
        ctx.DrawGeometry(B(Color.FromRgb(14, 16, 20)), null, new PolylineGeometry(new[] { new Point(gx + 1, gy), new Point(gx + glyph, gy + glyph / 2), new Point(gx + 1, gy + glyph) }, true));
        TextAt(ctx, playText, playPt, DisplaySemibold, Color.FromRgb(14, 16, 20), new Rect(gx + glyph + D(12), play.Y, pw, h));
        _hits.Add(new Hit(play, playKind, (playItem ?? item).Path, playItem ?? item));

        string viewLabel = DetailViewLabel();
        double viewPt = Math.Max(8.2, 9.8 * scale), lw = Measure(viewLabel, viewPt, DisplaySemibold);
        var view = new Rect(play.Right + D(22), y, lw + D(70), h);
        bool viewHot = Hover(view) || _menuKey == "DetailView";
        if (viewHot) ctx.DrawRectangle(B(Color.FromArgb(26, 255, 255, 255)), null, view.Deflate(1), h / 2, h / 2);
        ctx.DrawRectangle(null, new Pen(B(Color.FromArgb((byte)(viewHot ? 200 : 140), 255, 255, 255)), Math.Max(1.2, 1.4 * scale)), view.Deflate(1), h / 2, h / 2);
        TextAt(ctx, viewLabel, viewPt, DisplaySemibold, Colors.White, new Rect(view.X + D(28), view.Y, lw + 2, h));
        Chevron(ctx, view.X + D(28) + lw + D(16), view.Y + h / 2 + D(1), D(5), Colors.White, Math.Max(1.3, 1.6 * scale), _menuKey == "DetailView");
        _detailViewAnchor = new Point(view.X + D(14), view.Bottom + D(8));
        _hits.Add(new Hit(view, "detail-view"));

        var star = new Rect(view.Right + D(16), y, h, h);
        bool favorite = Favorites.Contains(item.Path);
        Color starInk = favorite ? Color.FromRgb(255, 214, 92) : Hover(star) ? Colors.White : Color.FromRgb(225, 232, 238);
        SvgIcon.Get("star")?.Draw(ctx, star.Deflate(D(12)), starInk);
        _hits.Add(new Hit(star, "detail-favorite", item.Path, item));
        // Come DetailHeader di Windows: accanto alla stella "Metti in coda" e "Aggiungi a una playlist".
        var queue = new Rect(star.Right + D(6), y, h, h);
        (SvgIcon.Get("queue-play") ?? SvgIcon.Get("queue"))?.Draw(ctx, queue.Deflate(D(12)), Hover(queue) ? Colors.White : Color.FromRgb(225, 232, 238));
        _hits.Add(new Hit(queue, "detail-queue", item.Path, item));
        var list = new Rect(queue.Right + D(6), y, h, h);
        SvgIcon.Get("collection-add")?.Draw(ctx, list.Deflate(D(12)), Hover(list) ? Colors.White : Color.FromRgb(225, 232, 238));
        _hits.Add(new Hit(list, "detail-playlist", item.Path, item));
        _detailPlaylistAnchor = new Point(list.X, list.Bottom + D(8));
        string? extraTip = Hover(queue) ? "Metti in coda" : Hover(list) ? "Aggiungi a una playlist" : null;
        if (extraTip != null)
        {
            var anchor = Hover(queue) ? queue : list;
            double tpt = Math.Max(7.6, 8.8 * scale), tw = Measure(extraTip, tpt, DisplayRegular);
            TextAt(ctx, extraTip, tpt, DisplayRegular, Color.FromArgb(220, 232, 238, 244), new Rect(anchor.Center.X - tw / 2 - 2, anchor.Bottom + D(4), tw + 4, D(20)), TextAlignment.Center);
        }
        if (Hover(star))
        {
            string tip = favorite ? "Togli dai preferiti" : "Aggiungi ai preferiti";
            double tpt = Math.Max(7.6, 8.8 * scale), tw = Measure(tip, tpt, DisplayRegular);
            TextAt(ctx, tip, tpt, DisplayRegular, Color.FromArgb(220, 232, 238, 244), new Rect(star.Center.X - tw / 2 - 2, star.Bottom + D(4), tw + 4, D(20)), TextAlignment.Center);
        }
        return poster;
    }

    private Point _detailViewAnchor, _detailPlaylistAnchor;
    /// <summary>"Metti in coda" dalla scheda: il titolo va in fondo alla coda dei video del player.</summary>
    public event Action<LibraryItem>? QueueAddRequested;

    private static string DetailTitle(LibraryItem item) => item.Category == "TV Series" && item.SeriesTitle != null ? EpisodeTitle(item) : item.Title;

    private static string HeroTaglineFull(LibraryItem item)
    {
        string text = (item.Overview ?? "").Trim();
        if (text.Length > 0) return text;
        return item.Category == "TV Series"
            ? "Episodio dalla tua libreria. Aggiorna i metadati TMDb per mostrare una sinossi completa."
            : "Sinossi TMDb non ancora disponibile per questo titolo. Aggiorna i metadati per mostrare qui la descrizione completa.";
    }

    /// <summary>ShortQualityBadge: risoluzione e gamma dinamica in breve (4K HDR, Full HD SDR...).</summary>
    private static string QualityBadge(LibraryItem item)
    {
        string res = item.Is4K ? "4K" : item.ResolutionLabel ?? "";
        if (res.Contains("1080", StringComparison.Ordinal)) res = "Full HD";
        else if (res.Contains("720", StringComparison.Ordinal)) res = "HD";
        else if (res.Contains("2160", StringComparison.Ordinal)) res = "4K";
        string range = item.DolbyVision ? "Dolby Vision" : item.IsHdr ? "HDR" : res.Length > 0 ? "SDR" : "";
        return (res + " " + range).Trim();
    }

    /// <summary>
    /// Panoramica: a sinistra REGIA con il nome, a destra CAST PRINCIPALE con quattro volti. Cio' che manca non compare:
    /// senza regia il cast parte da sinistra, senza nessuno dei due la fascia non c'e' (restituisce 0).
    /// </summary>
    private double DrawDetailPanorama(DrawingContext ctx, Rect r, LibraryItem item, double scale)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        static string Spaced(string text) => string.Join(" ", text.ToUpperInvariant().ToCharArray());
        double headPt = Math.Max(6.8, 7.8 * scale), namePt = Math.Max(8.4, 10.4 * scale), rolePt = Math.Max(7.6, 9.2 * scale);
        bool hasDirector = !string.IsNullOrWhiteSpace(item.Director);
        var cast = item.Cast.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Take(4).ToList();
        if (!hasDirector && cast.Count == 0) return 0;
        double directorW = hasDirector ? D(330) : 0;
        double face = D(64);
        if (hasDirector)
        {
            TextAt(ctx, Spaced("Regia"), headPt, DisplaySemibold, Color.FromRgb(160, 172, 184), new Rect(r.X, r.Y, directorW, D(20)));
            var photo = new Rect(r.X, r.Y + D(28), face, face);
            // Film in locale: la foto del regista non sta nei metadati salvati, si prende dalla troupe TMDb (cache su disco).
            string? directorPhoto = item.DirectorPhoto ?? CrewFor(item)?.FirstOrDefault(c => c.Job == "Director" &&
                string.Equals(c.Name, item.Director, StringComparison.OrdinalIgnoreCase) && c.ProfileUrl != null)?.ProfileUrl
                ?? CrewFor(item)?.FirstOrDefault(c => c.Job == "Director" && c.ProfileUrl != null)?.ProfileUrl;
            DrawFace(ctx, photo, directorPhoto, item.Director!, namePt);
            TextAt(ctx, item.Director!, namePt, DisplaySemibold, Colors.White, new Rect(photo.Right + D(16), photo.Y + face / 2 - D(24), directorW - face - D(36), D(24)));
        }
        if (cast.Count == 0) return D(104);
        double castLeft = hasDirector ? r.X + directorW + D(30) : r.X;
        TextAt(ctx, Spaced("Cast principale"), headPt, DisplaySemibold, Color.FromRgb(160, 172, 184), new Rect(castLeft, r.Y, D(300), D(20)));
        double each = (r.Right - castLeft) / 4;
        for (int i = 0; i < cast.Count; i++)
        {
            var photo = new Rect(castLeft + i * each, r.Y + D(28), face, face);
            DrawFace(ctx, photo, cast[i].Photo, cast[i].Name, namePt);
            double tx = photo.Right + D(16), tw = Math.Max(10, each - face - D(24));
            TextAt(ctx, cast[i].Name, namePt, DisplaySemibold, Colors.White, new Rect(tx, photo.Y + face / 2 - D(24), tw, D(24)));
            TextAt(ctx, cast[i].Role, rolePt, DisplayRegular, Color.FromArgb(170, 200, 210, 220), new Rect(tx, photo.Y + face / 2 + D(2), tw, D(22)));
        }
        return D(104);
    }

    /// <summary>
    /// Sezione della scheda senza contenuto (cast, recensioni): titolo breve e una riga di spiegazione, centrati e
    /// sobri, al posto della frase di servizio appoggiata in alto.
    /// </summary>
    private void DrawDetailEmpty(DrawingContext ctx, Rect area, string title, string? body, double scale)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        double w = Math.Min(area.Width, D(520));
        double titlePt = Math.Max(10, 12.4 * scale), bodyPt = Math.Max(8, 9.4 * scale);
        var t = Format(L10n.T(title), titlePt, DisplaySemibold, Color.FromArgb(235, 255, 255, 255), w, 1, TextAlignment.Center);
        var b = body == null ? null : Format(L10n.T(body), bodyPt, DisplayRegular, Color.FromArgb(150, 225, 232, 240), w, 2, TextAlignment.Center);
        double blockH = t.Height + (b != null ? D(8) + b.Height : 0);
        double y = area.Y + Math.Max(D(12), Math.Min(D(70), (area.Height - blockH) / 2));
        double x = area.X + (area.Width - w) / 2;
        ctx.DrawText(t, new Point(x, y));
        if (b != null) ctx.DrawText(b, new Point(x, y + t.Height + D(8)));
    }

    /// <summary>Volto tondo; senza foto le iniziali su un disco scuro.</summary>
    private void DrawFace(DrawingContext ctx, Rect photo, string? path, string name, double pt)
    {
        var bmp = Image(path, photo.Width);
        using (ctx.PushClip(new RoundedRect(photo, photo.Width / 2)))
        {
            if (bmp != null) DrawCover(ctx, bmp, photo, .5, .25);
            else
            {
                ctx.FillRectangle(B(Color.FromRgb(34, 40, 48)), photo);
                string initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => w[0]));
                TextAt(ctx, initials, pt, DisplaySemibold, Color.FromRgb(200, 210, 220), photo, TextAlignment.Center);
            }
        }
    }

    // Film affini secondo Trakt, gia' ridotti a quelli in libreria: percorso -> percorsi affini (null mentre si chiede).
    // Come Windows (TraktRelatedFor); non serve un account Trakt collegato, la risposta resta in cache un mese.
    private readonly Dictionary<string, List<string>?> _traktRelated = new(StringComparer.OrdinalIgnoreCase);

    private List<string>? TraktRelatedFor(LibraryItem current)
    {
        if (current.TmdbId is not int tmdbId || tmdbId <= 0 || current.Category != "Movies") return null;
        if (_traktRelated.TryGetValue(current.Path, out var known)) return known;
        _traktRelated[current.Path] = null;
        string path = current.Path;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            List<string> related = new();
            try
            {
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                related = await CinecorePlayer2025.Utilities.TraktLibrary.RelatedInLibraryAsync(tmdbId, CollectTitlesForRecommendations(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Console.Error.WriteLine("[TRAKT] affini: " + ex.Message); }
            Dispatcher.UIThread.Post(() => { _traktRelated[path] = related; if (related.Count > 0) InvalidateVisual(); });
        });
        return null;
    }

    /// <summary>
    /// BuildDetailRecommendations: prima i film che Trakt considera affini (nel suo ordine), poi stessa categoria,
    /// generi in comune, anni vicini; niente doppioni della stessa edizione.
    /// </summary>
    private List<LibraryItem> Recommendations(LibraryItem current)
    {
        var traktRelated = TraktRelatedFor(current);
        var genres = new HashSet<string>(current.Genres, StringComparer.OrdinalIgnoreCase);
        // Fuori anche le altre copie dello stesso titolo (stesso nome, un'altra edizione o un altro disco).
        return _detailCandidates.Where(c => c.Path != current.Path && c.Category == current.Category
                && !string.Equals(c.Title.Trim(), current.Title.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .GroupBy(c => c.Title.Trim() + "|" + c.Year, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(c => (Item: c, Score: (traktRelated?.FindIndex(p => string.Equals(p, c.Path, StringComparison.OrdinalIgnoreCase)) is int ri && ri >= 0 ? 1000 - ri * 5 : 0)
                + c.Genres.Count(genres.Contains) * 100
                + (current.Year.HasValue && c.Year.HasValue ? Math.Max(0, 18 - Math.Abs(current.Year.Value - c.Year.Value) * 2) : 0)
                + (c.Is4K == current.Is4K ? 3 : 0)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Item).Take(12).ToList();
    }

    private void DrawRecommendations(DrawingContext ctx, Rect r, List<LibraryItem> items, double scale)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        double label = D(34), gap = D(16);
        double cardW = Math.Floor(Math.Min((r.Width - gap * 3) / 4, (r.Height - label) * 16 / 9));
        if (cardW < D(72)) return;
        double cardH = Math.Floor(cardW * 9 / 16);
        TextAt(ctx, "Consigliati", Math.Max(11, 15 * scale), DisplaySemibold, Colors.White, new Rect(r.X, r.Y - D(6), r.Width, D(32)));
        double y = r.Y + label;
        for (int i = 0; i < Math.Min(4, items.Count); i++)
        {
            var card = new Rect(r.X + i * (cardW + gap), y, cardW, cardH);
            var item = items[i];
            bool hover = Hover(card);
            using (ctx.PushClip(new RoundedRect(card, D(8))))
            {
                var bmp = Image(item.WideArtPath ?? item.ArtPath, card.Width);
                if (bmp != null) DrawCover(ctx, bmp, card, .5, .3);
                else DrawGeneratedArt(ctx, card, item.Title, hero: true, item.Category);
                ctx.FillRectangle(new RadialGradientBrush
                {
                    RadiusX = new RelativeScalar(0.72, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.82, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.6), new GradientStop(Color.FromArgb(150, 0, 0, 0), 1) },
                }, card);
                var lower = new Rect(card.X, card.Y + card.Height * .3, card.Width, card.Height * .7 + 1);
                ctx.FillRectangle(Vertical(Color.FromRgb(6, 10, 16), (0, 0), (0.25, 70), (0.55, 170), (0.8, 230), (1, 250)), lower);
            }
            if (hover) ctx.DrawRectangle(null, new Pen(B(Color.FromArgb(215, 255, 255, 255)), 2), card.Deflate(1), D(8), D(8));
            double pad = D(12);
            TextAt(ctx, item.Title, Math.Max(8.2, 9.6 * scale), DisplaySemibold, Colors.White, new Rect(card.X + pad, card.Bottom - D(34), card.Width - pad * 2, D(24)));
            _hits.Add(new Hit(card, "detail-rec", item.Path, item));
        }
    }

    /// <summary>Dettagli: dati del film, video, audio e file in colonne (DrawDetailSpecs).</summary>
    private void DrawDetailSpecs(DrawingContext ctx, Rect r, LibraryItem item, double scale)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        string Or(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v!;
        string size = item.Bytes > 0 ? FormatBytes(item.Bytes) : !item.IsNetwork && File.Exists(item.Path) ? FormatBytes(new FileInfo(item.Path).Length) : "—";
        string location = item.IsNetwork ? (NetworkStore.Servers.FirstOrDefault(s => s.Key == ActiveServer?.Key)?.Name is { } server ? "Jellyfin   " + server : "Jellyfin")
            : Path.GetDirectoryName(item.Path) ?? "";
        var columns = new (string Title, (string Label, string Value)[] Rows)[]
        {
            ("Film", new[] { ("Titolo", item.Title), ("Generi", string.Join(", ", item.Genres)), ("Anno", item.Year?.ToString(CultureInfo.InvariantCulture) ?? ""), ("Durata", item.DurationMinutes is > 0 ? FormatMinutes(item.DurationMinutes.Value) : ""), ("Voto", item.Rating is > 0 ? item.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') : "") }),
            ("Video", new[] { ("Risoluzione", item.ResolutionLabel ?? ""), ("Gamma dinamica", item.DolbyVision ? "Dolby Vision" : item.IsHdr ? "HDR" : "SDR"), ("Qualità", QualityBadge(item)) }),
            ("Audio", new[] { ("Traccia", item.AudioLabel ?? ""), ("Audio a oggetti", item.HasAtmos ? "Sì (Atmos / DTS:X)" : "No") }),
            ("File", new[] { ("Nome", item.FileName ?? Path.GetFileName(item.Path)), ("Posizione", location), ("Dimensione", size) }),
        };
        double gap = D(40), colW = (r.Width - gap * (columns.Length - 1)) / columns.Length;
        for (int c = 0; c < columns.Length; c++)
        {
            double x = r.X + c * (colW + gap), y = r.Y;
            TextAt(ctx, string.Join(" ", columns[c].Title.ToUpperInvariant().ToCharArray()), Math.Max(7, 8.2 * scale), DisplaySemibold, Color.FromRgb(150, 166, 180), new Rect(x, y, colW, D(20)));
            y += D(32);
            foreach (var (l, v) in columns[c].Rows)
            {
                if (y + D(46) > r.Bottom) break;
                TextAt(ctx, l, Math.Max(7.2, 8.4 * scale), DisplayRegular, Color.FromRgb(140, 156, 172), new Rect(x, y, colW, D(20)));
                TextAt(ctx, Or(v), Math.Max(8, 9.8 * scale), DisplaySemibold, Color.FromRgb(238, 242, 246), new Rect(x, y + D(20), colW, D(24)));
                y += D(56);
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        double v = bytes; string[] units = { "B", "KB", "MB", "GB", "TB" }; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u >= 3 ? v.ToString("0.0", CultureInfo.InvariantCulture) : v.ToString("0", CultureInfo.InvariantCulture)).Replace('.', ',') + " " + units[u];
    }

    private void OpenDetailViewMenu()
    {
        var entries = new List<MenuItem>
        {
            new MenuItem("Panoramica", !_detailInfo && !_detailReviews && !_detailCast, () => SetDetailView(false, false, false)),
            new MenuItem("Dettagli", _detailInfo, () => SetDetailView(true, false, false)),
            new MenuItem("Recensioni", _detailReviews, () => SetDetailView(false, true, false)),
            new MenuItem("Cast", _detailCast, () => SetDetailView(false, false, true)),
        };
        OpenMenu("DetailView", entries, _detailViewAnchor);
    }

    /// <summary>I clic quando la scheda e' aperta. True se li ha gestiti.</summary>
    private bool DetailActivate(Hit hit)
    {
        if (_detail == null) return false;
        switch (hit.Kind)
        {
            case "detail-close": case "detail-outside": CloseDetail(); return true;
            case "detail-play":
                var item = _detail; CloseDetail();
                PlayRequested?.Invoke(item, ResumeOf(item)?.Position);
                return true;
            case "detail-view": OpenDetailViewMenu(); return true;
            case "reviews-source": OpenReviewsMenu(false); return true;
            case "reviews-audience": OpenReviewsMenu(true); return true;
            case "reviews-list": return true;
            case "review-read" when int.TryParse(hit.Key, out int ri) && ri >= 0 && ri < _reviewsShown.Count:
                _reviewReading = _reviewsShown[ri]; _reviewsScroll = 0; InvalidateVisual(); return true;
            case "review-back": _reviewReading = null; _reviewsScroll = 0; InvalidateVisual(); return true;
            case "detail-favorite": Favorites.Toggle(_detail.Path); if (_category == "Favourites") Reload(); InvalidateVisual(); return true;
            case "detail-queue": if (hit.Item != null) QueueAddRequested?.Invoke(hit.Item); return true;
            case "detail-playlist":
                if (hit.Item != null && AddToPlaylistMenu(new[] { hit.Item }).Children is { } choices)
                    ShowTextMenu(choices, new Point(_detailPlaylistAnchor.X, _detailPlaylistAnchor.Y));
                return true;
            case "detail-rec": if (hit.Item != null) OpenDetail(hit.Item); return true;
            case "detail-sheet": return true;
            case "detail-cast-prev": _detailCastFirst = Math.Max(0, _detailCastFirst - 1); InvalidateVisual(); return true;
            case "detail-cast-next": _detailCastFirst++; InvalidateVisual(); return true;
        }
        return true;
    }
}
