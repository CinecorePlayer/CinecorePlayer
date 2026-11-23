using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Scheda del titolo › Recensioni e Cast (CinematicMediaLibraryPage.Rendering.Reviews e DetailPanorama di Windows).
/// Recensioni: in alto "Recensioni e valutazioni" con due menu (fonte con il voto, pubblico/critica); sotto le
/// recensioni di TMDb, Letterboxd e Metacritic a testo intero, in due colonne su schede larghe, che scorrono.
/// Cast: i volti con nome e personaggio in griglia.
/// </summary>
internal sealed partial class LibraryView
{
    private bool _detailReviews, _detailCast;
    private string _reviewsSource = "all";
    private bool _reviewsCritic;
    private double _reviewsScroll, _reviewsScrollMax;
    // "Leggi tutto" (Windows): la recensione intera in una colonna che scorre, con il ritorno all'elenco.
    private MovieMetadataService.RichReview? _reviewReading;
    private List<MovieMetadataService.RichReview> _reviewsShown = new();
    private const int ReviewPreviewLines = 8;

    private sealed class ReviewData
    {
        public bool Done;
        public double? Tmdb;
        public ExternalRatingsService.Ratings? Ratings;
        public List<MovieMetadataService.RichReview> Reviews = new();
        public string? ImdbId;
        public bool Tv;
    }
    private readonly Dictionary<string, ReviewData> _reviewData = new(StringComparer.OrdinalIgnoreCase);

    private string DetailViewLabel() => _detailInfo ? "Dettagli" : _detailReviews ? "Recensioni" : _detailCast ? "Cast" : "Panoramica";

    private void SetDetailView(bool info, bool reviews, bool cast)
    {
        _detailInfo = info; _detailReviews = reviews; _detailCast = cast;
        _reviewsScroll = 0; _reviewsSource = "all"; _reviewReading = null;
        InvalidateVisual();
    }

    /// <summary>Recensioni di TMDb (con la chiave dell'utente), poi i voti e le recensioni dei siti (IMDb, Letterboxd, Metacritic).</summary>
    private ReviewData ReviewsFor(LibraryItem item)
    {
        if (_reviewData.TryGetValue(item.Path, out var known)) return known;
        var data = new ReviewData { Tmdb = item.Rating };
        _reviewData[item.Path] = data;
        if (item.IsNetwork) { data.Done = true; return data; }
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var rich = MovieMetadataService.ResolveRichMetadata(item.Path, item.DurationMinutes * 60, AppLanguage.TmdbLocale(L10n.Language), cts.Token, includeReviews: true);
                data.Reviews = rich?.Reviews?.ToList() ?? new();
                data.Tmdb = rich?.Rating ?? data.Tmdb;
                data.ImdbId = rich?.ImdbId;
                data.Tv = string.Equals(rich?.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
                Dispatcher.UIThread.Post(InvalidateVisual);
                if (!string.IsNullOrWhiteSpace(data.ImdbId))
                {
                    var ratings = await ExternalRatingsService.ResolveAsync(data.ImdbId!, data.Tv, item.Title, item.Year);
                    data.Ratings = ratings;
                    Dispatcher.UIThread.Post(InvalidateVisual);
                    var external = await ExternalRatingsService.ResolveReviewsAsync(ratings);
                    lock (data) data.Reviews = data.Reviews.Concat(external).ToList();
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("[REVIEWS] " + ex.Message); }
            data.Done = true;
            Dispatcher.UIThread.Post(InvalidateVisual);
        });
        return data;
    }

    private Rect _reviewsSourceAnchor, _reviewsAudienceAnchor;

    private void DrawDetailReviews(DrawingContext ctx, Rect r, LibraryItem item, double scale)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        var data = ReviewsFor(item);
        if (!data.Done) _frames.Start(); // i puntini d'attesa respirano
        List<MovieMetadataService.RichReview> all;
        lock (data) all = data.Reviews.ToList();
        // La stessa recensione arrivata da due fonti (TMDb e il sito) compariva due volte.
        all = all.GroupBy(rv => (rv.Author ?? "").Trim().ToLowerInvariant() + "|" + new string((rv.Content ?? "").Where(char.IsLetterOrDigit).Take(80).ToArray()).ToLowerInvariant())
            .Select(g => g.First()).ToList();
        bool hasCritic = all.Any(rv => rv.Critic);
        if (!hasCritic) _reviewsCritic = false;
        var audience = all.Where(rv => rv.Critic == _reviewsCritic).ToList();
        var shown = _reviewsSource == "all" ? audience : audience.Where(rv => rv.Source == _reviewsSource).ToList();
        var ext = data.Ratings;
        string Score(double? value, string format) => value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture).Replace('.', ',') : data.Done ? "" : "…";
        string meta = _reviewsCritic || ext?.MetaUserScore == null ? (ext?.Metascore is int critic ? critic.ToString(CultureInfo.InvariantCulture) : Score(null, "0"))
            : ext.MetaUserScore.Value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        var sources = new List<(string Name, string Value)> { ("TMDb", Score(data.Tmdb, "0.0")), ("IMDb", Score(ext?.Imdb, "0.0")), ("Letterboxd", Score(ext?.Letterboxd, "0.0")), ("Metacritic", meta) };

        double pillH = D(40);
        var row = new Rect(r.X, r.Y, r.Width, pillH);
        double right = row.Right;
        double pt = Math.Max(8, 9.4 * scale);
        Rect Pill(string label, string kind)
        {
            double lw = Measure(label, pt, DisplaySemibold), w = lw + D(30);
            var pill = new Rect(right - w, row.Y, w, pillH);
            Color ink = Hover(pill) ? Colors.White : Color.FromArgb(205, 255, 255, 255);
            TextAt(ctx, label, pt, DisplaySemibold, ink, new Rect(pill.X, pill.Y, lw + 2, pillH));
            Chevron(ctx, pill.X + lw + D(16), pill.Center.Y + D(1), D(5), ink, Math.Max(1.3, 1.6 * scale));
            _hits.Add(new Hit(pill, kind));
            right = pill.X - D(34);
            return pill;
        }
        if (hasCritic) _reviewsAudienceAnchor = Pill(_reviewsCritic ? "Critica" : "Pubblico", "reviews-audience");
        var selected = sources.FirstOrDefault(s => s.Name == _reviewsSource);
        _reviewsSourceAnchor = Pill(_reviewsSource == "all" ? "Tutte le fonti" : selected.Name + (selected.Value.Length > 0 ? "  " + selected.Value : ""), "reviews-source");
        TextAt(ctx, "Recensioni e valutazioni", Math.Max(12, 15 * scale), DisplaySemibold, Colors.White, row);

        // I voti in una riga, come le colonne dei punteggi di Windows.
        double sy = row.Bottom + D(14), sx = r.X;
        foreach (var (name, value) in sources)
        {
            if (value.Length == 0) continue;
            double nw = Measure(name, Math.Max(7.6, 8.8 * scale), DisplayRegular), vw = Measure(value, Math.Max(10, 13 * scale), DisplaySemibold);
            TextAt(ctx, name, Math.Max(7.6, 8.8 * scale), DisplayRegular, Color.FromArgb(170, 255, 255, 255), new Rect(sx, sy, nw + 2, D(18)));
            TextAt(ctx, value, Math.Max(10, 13 * scale), DisplaySemibold, Colors.White, new Rect(sx, sy + D(18), vw + 2, D(26)));
            sx += Math.Max(nw, vw) + D(36);
        }

        double bodyTop = sy + D(58);
        var viewport = new Rect(r.X, bodyTop, r.Width, Math.Max(1, r.Bottom - bodyTop));
        _reviewsShown = shown;
        if (_reviewReading != null && !shown.Contains(_reviewReading)) _reviewReading = null;
        if (_reviewReading is { } reading) { DrawReviewReading(ctx, viewport, reading, scale); return; }
        if (shown.Count == 0)
        {
            _reviewsScrollMax = 0;
            if (!data.Done) DrawDetailEmpty(ctx, viewport, "Carico le recensioni…", null, scale);
            else if (item.IsNetwork) DrawDetailEmpty(ctx, viewport, "Recensioni non disponibili", "Si cercano solo per i film sul Mac.", scale);
            else DrawDetailEmpty(ctx, viewport, "Nessuna recensione", _reviewsSource == "all" ? null : "Prova a scegliere un'altra fonte.", scale);
            return;
        }
        // Due colonne su schede larghe: ogni recensione va nella colonna piu' corta.
        int columns = viewport.Width > D(820) ? 2 : 1;
        double gap = D(40), colW = (viewport.Width - gap * (columns - 1)) / columns;
        var heights = new double[columns];
        double bodyPt = Math.Max(8.4, 9.8 * scale);
        _reviewsScrollMax = 0;
        using (ctx.PushClip(viewport))
        {
            foreach (var review in shown)
            {
                int col = Array.IndexOf(heights, heights.Min());
                double x = viewport.X + col * (colW + gap), y = viewport.Y + heights[col] - _reviewsScroll;
                string head = (string.IsNullOrWhiteSpace(review.Author) ? "Anonimo" : review.Author.Trim()) + (string.IsNullOrWhiteSpace(review.Detail) ? "" : "  ·  " + review.Detail);
                string tag = review.Source + (review.Rating is double rating ? "  " + (rating % 1 == 0 ? rating.ToString("0", CultureInfo.InvariantCulture) : rating.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',')) + "/" + review.RatingScale.ToString("0", CultureInfo.InvariantCulture) : "");
                string content = (review.Content ?? "").Trim();
                var body = UiText.Get(L10n.T(content), DisplayRegular, Px(bodyPt), Color.FromArgb(225, 255, 255, 255), colW, ReviewPreviewLines, trimming: TextTrimming.WordEllipsis, lineHeight: Px(bodyPt) * 1.45);
                // Tagliata se il testo intero e' piu' alto delle righe mostrate.
                var whole = UiText.Get(L10n.T(content), DisplayRegular, Px(bodyPt), Colors.White, colW, 0, trimming: TextTrimming.None, lineHeight: Px(bodyPt) * 1.45);
                bool cut = whole.Height > body.Height + 1;
                double linkH = cut ? D(30) : 0;
                double blockH = D(26) + D(22) + body.Height + linkH + D(34);
                if (y + blockH > viewport.Y && y < viewport.Bottom)
                {
                    TextAt(ctx, head, Math.Max(8.6, 10.2 * scale), DisplaySemibold, Colors.White, new Rect(x, y, colW, D(24)));
                    TextAt(ctx, tag, Math.Max(7.4, 8.4 * scale), DisplayRegular, Color.FromArgb(160, 255, 255, 255), new Rect(x, y + D(24), colW, D(20)));
                    ctx.DrawText(body, new Point(x, y + D(48)));
                    if (cut)
                    {
                        double lpt = Math.Max(8, 9.2 * scale), lw = Measure("Leggi tutto", lpt, DisplaySemibold);
                        var link = new Rect(x, y + D(48) + body.Height + D(6), lw + D(18), D(22));
                        Color ink = Hover(link) ? Colors.White : Color.FromArgb(215, 255, 255, 255);
                        TextAt(ctx, "Leggi tutto", lpt, DisplaySemibold, ink, new Rect(link.X, link.Y, lw + 2, link.Height));
                        Chevron(ctx, link.X + lw + D(10), link.Center.Y, D(4), ink, Math.Max(1.3, 1.5 * scale), right: true);
                        _readLinks.Add((link.Inflate(D(4)), review));
                    }
                    ctx.FillRectangle(B(Color.FromArgb(24, 255, 255, 255)), new Rect(x, y + blockH - D(17), colW, 1));
                }
                heights[col] += blockH;
            }
        }
        _reviewsScrollMax = Math.Max(0, heights.Max() - viewport.Height);
        _reviewsScroll = Math.Clamp(_reviewsScroll, 0, _reviewsScrollMax);
        _hits.Add(new Hit(viewport, "reviews-list"));
        // Dopo la superficie dell'elenco: i collegamenti stanno sopra e ricevono il clic.
        for (int i = 0; i < _readLinks.Count; i++)
            if (_readLinks[i].Bounds.Intersects(viewport)) _hits.Add(new Hit(_readLinks[i].Bounds, "review-read", _reviewsShown.IndexOf(_readLinks[i].Review).ToString(CultureInfo.InvariantCulture)));
        _readLinks.Clear();
    }

    private readonly List<(Rect Bounds, MovieMetadataService.RichReview Review)> _readLinks = new();

    /// <summary>La recensione intera: autore, fonte e voto, il testo in una colonna leggibile che scorre.</summary>
    private void DrawReviewReading(DrawingContext ctx, Rect viewport, MovieMetadataService.RichReview review, double scale)
    {
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        double lpt = Math.Max(8, 9.2 * scale), lw = Measure("Tutte le recensioni", lpt, DisplaySemibold);
        var back = new Rect(viewport.X, viewport.Y, lw + D(22), D(24));
        Color ink = Hover(back) ? Colors.White : Color.FromArgb(215, 255, 255, 255);
        Chevron(ctx, back.X + D(3), back.Center.Y, D(4), ink, Math.Max(1.3, 1.5 * scale), left: true);
        TextAt(ctx, "Tutte le recensioni", lpt, DisplaySemibold, ink, new Rect(back.X + D(16), back.Y, lw + 2, back.Height));
        double colW = Math.Min(viewport.Width, D(760));
        var area = new Rect(viewport.X, back.Bottom + D(16), viewport.Width, Math.Max(1, viewport.Bottom - back.Bottom - D(16)));
        double bodyPt = Math.Max(8.8, 10.4 * scale);
        string head = (string.IsNullOrWhiteSpace(review.Author) ? "Anonimo" : review.Author.Trim()) + (string.IsNullOrWhiteSpace(review.Detail) ? "" : "  ·  " + review.Detail);
        string tag = review.Source + (review.Rating is double rating ? "  " + (rating % 1 == 0 ? rating.ToString("0", CultureInfo.InvariantCulture) : rating.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',')) + "/" + review.RatingScale.ToString("0", CultureInfo.InvariantCulture) : "");
        var body = UiText.Get(L10n.T((review.Content ?? "").Trim()), DisplayRegular, Px(bodyPt), Color.FromArgb(230, 255, 255, 255), colW, 0, trimming: TextTrimming.None, lineHeight: Px(bodyPt) * 1.55);
        double total = D(26) + D(26) + body.Height + D(20);
        _reviewsScrollMax = Math.Max(0, total - area.Height);
        _reviewsScroll = Math.Clamp(_reviewsScroll, 0, _reviewsScrollMax);
        using (ctx.PushClip(area))
        {
            double y = area.Y - _reviewsScroll;
            TextAt(ctx, head, Math.Max(9, 11 * scale), DisplaySemibold, Colors.White, new Rect(area.X, y, colW, D(26)));
            TextAt(ctx, tag, Math.Max(7.6, 8.8 * scale), DisplayRegular, Color.FromArgb(160, 255, 255, 255), new Rect(area.X, y + D(26), colW, D(22)));
            ctx.DrawText(body, new Point(area.X, y + D(56)));
        }
        _hits.Add(new Hit(area, "reviews-list"));
        _hits.Add(new Hit(back.Inflate(D(4)), "review-back"));
    }

    private void OpenReviewsMenu(bool audience)
    {
        if (_detail is not { } item) return;
        var data = ReviewsFor(item);
        List<MovieMetadataService.RichReview> all;
        lock (data) all = data.Reviews.ToList();
        var entries = new List<MenuItem>();
        if (audience)
        {
            entries.Add(new MenuItem("Pubblico   " + all.Count(r => !r.Critic), !_reviewsCritic, () => { _reviewsCritic = false; _reviewsSource = "all"; _reviewsScroll = 0; InvalidateVisual(); }));
            entries.Add(new MenuItem("Critica   " + all.Count(r => r.Critic), _reviewsCritic, () => { _reviewsCritic = true; _reviewsSource = "all"; _reviewsScroll = 0; InvalidateVisual(); }));
            OpenMenu("ReviewsAudience", entries, new Point(_reviewsAudienceAnchor.X - 12, _reviewsAudienceAnchor.Bottom + 4));
            return;
        }
        var audienceReviews = all.Where(r => r.Critic == _reviewsCritic).ToList();
        entries.Add(new MenuItem("Tutte le fonti", _reviewsSource == "all", () => { _reviewsSource = "all"; _reviewsScroll = 0; InvalidateVisual(); }));
        foreach (string name in new[] { "TMDb", "IMDb", "Letterboxd", "Metacritic" })
        {
            int count = audienceReviews.Count(r => r.Source == name);
            string key = name;
            entries.Add(new MenuItem(name + (count > 0 ? "   " + count : ""), _reviewsSource == name, () => { _reviewsSource = key; _reviewsScroll = 0; InvalidateVisual(); }));
        }
        OpenMenu("ReviewsSource", entries, new Point(_reviewsSourceAnchor.X - 12, _reviewsSourceAnchor.Bottom + 4));
    }

    /// <summary>Cast: i volti in griglia con nome e personaggio.</summary>
    private int _detailCastFirst;

    /// <summary>Prova: una scheda con un cast finto (foto locali) sulla vista Cast.</summary>
    internal void TestCastDemo()
    {
        string img = System.IO.Path.Combine(VideoHostClient.RepoRoot, "Splash", "logo.png");
        var item = new LibraryItem { Path = "qa-cast", Title = "Film di prova", Category = "Movies", Director = "Christopher Nolan", Rating = 8.4,
            Cast = Enumerable.Range(1, 12).Select(i => ($"Attore {i}", $"Personaggio {i}", i % 3 == 0 ? null : (string?)img)).ToList() };
        OpenDetail(item);
        SetDetailView(false, false, true);
    }

    /// <summary>
    /// Cast e troupe come Windows (CinematicMediaLibraryPage.Rendering.Cast.cs): titolo a sinistra, regia e voto TMDb a
    /// destra, un carosello di ritratti (angoli arrotondati, ombra, sfumatura in basso) con nome e personaggio sotto,
    /// frecce ai lati e i puntini della pagina.
    /// </summary>
    private void DrawDetailCast(DrawingContext ctx, Rect r, LibraryItem item, double scale)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        double D(double v) => Math.Max(1, Math.Round(v * scale));
        double side = D(8), headerH = D(42);
        TextAt(ctx, "Cast e troupe", Math.Max(13.2, 15.5 * scale), DisplaySemibold, Colors.White, new Rect(r.X + side, r.Y, r.Width / 2, headerH));
        string credits = string.Join("   •   ", new[]
        {
            string.IsNullOrWhiteSpace(item.Director) ? null : L10n.T("Regia") + "  " + item.Director,
            item.Rating is > 0 ? $"TMDb  {item.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)}/10" : null,
        }.Where(v => !string.IsNullOrWhiteSpace(v)));
        TextAt(ctx, credits, Math.Max(7.8, 8.6 * scale), DisplayRegular, Color.FromRgb(194, 211, 224), new Rect(r.X + r.Width / 2, r.Y, r.Width / 2 - side, headerH), TextAlignment.Right);

        var cast = item.Cast.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        if (cast.Count == 0)
        {
            DrawDetailEmpty(ctx, new Rect(r.X, r.Y + headerH, r.Width, Math.Max(1, r.Height - headerH)), "Cast non disponibile",
                item.IsNetwork ? "Il server non ha il cast di questo titolo." : "Arriva insieme ai metadati del film.", scale);
            return;
        }
        double arrowW = D(38), viewportLeft = r.X + arrowW + D(10), viewportRight = r.Right - arrowW - D(10);
        double viewportW = Math.Max(1, viewportRight - viewportLeft), gap = D(14), preferred = D(166);
        int visible = Math.Clamp((int)((viewportW + gap) / Math.Max(1, preferred + gap)), 4, 7);
        visible = Math.Min(visible, cast.Count);
        int maxFirst = Math.Max(0, cast.Count - visible);
        _detailCastFirst = Math.Clamp(_detailCastFirst, 0, maxFirst);
        double cardW = Math.Max(D(104), (viewportW - gap * Math.Max(0, visible - 1)) / Math.Max(1, visible));
        double totalW = cardW * visible + gap * Math.Max(0, visible - 1);
        double startX = viewportLeft + Math.Max(0, (viewportW - totalW) / 2);
        double top = r.Y + headerH + D(14), labelH = D(58);
        double availableImageH = Math.Max(D(124), r.Bottom - top - labelH - D(18));
        double imageH = Math.Min(availableImageH, Math.Max(D(150), Math.Round(cardW * 1.43)));
        double radius = D(8);
        for (int slot = 0; slot < visible; slot++)
        {
            var (name, role, photo) = cast[_detailCastFirst + slot];
            var portrait = new Rect(startX + slot * (cardW + gap), top, cardW, imageH);
            ctx.DrawRectangle(B(Color.FromArgb(86, 0, 0, 0)), null, portrait.Translate(new Vector(D(3), D(5))), radius, radius);
            using (ctx.PushClip(new RoundedRect(portrait, radius)))
            {
                if (photo != null && Image(photo, cardW) is { } bmp) DrawCover(ctx, bmp, portrait, .5, .18);
                else
                {
                    ctx.FillRectangle(B(Color.FromRgb(30, 36, 44)), portrait);
                    string initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(n => n[0])).ToUpperInvariant();
                    TextAt(ctx, initials, Math.Max(12, cardW / 6), DisplaySemibold, Color.FromArgb(150, 255, 255, 255), portrait, TextAlignment.Center);
                }
                double fadeH = Math.Min(portrait.Height, D(58));
                ctx.FillRectangle(Vertical(Color.FromRgb(0, 5, 9), (0, 0), (1, 104)), new Rect(portrait.X, portrait.Bottom - fadeH, portrait.Width, fadeH));
            }
            var nameRect = new Rect(portrait.X, portrait.Bottom + D(7), portrait.Width, D(20));
            TextAt(ctx, name, Math.Max(8.0, 8.9 * scale), DisplaySemibold, Colors.White, nameRect, TextAlignment.Center);
            var ft = Format(string.IsNullOrWhiteSpace(role) ? L10n.T("Interprete") : role, Math.Max(7.0, 7.6 * scale), DisplayRegular, Color.FromRgb(174, 197, 214), portrait.Width - D(8), 2, TextAlignment.Center);
            ctx.DrawText(ft, new Point(portrait.X + D(4), nameRect.Bottom + D(1)));
        }
        var leftArrow = new Rect(r.X + D(1), top + Math.Max(0, (imageH - arrowW) / 2), arrowW, arrowW);
        var rightArrow = new Rect(r.Right - arrowW - D(1), leftArrow.Y, arrowW, arrowW);
        bool canPrevious = _detailCastFirst > 0, canNext = _detailCastFirst < maxFirst;
        foreach (var (arrow, right, enabled) in new[] { (leftArrow, false, canPrevious), (rightArrow, true, canNext) })
        {
            if (enabled) ctx.DrawEllipse(B(Color.FromArgb((byte)(Hover(arrow) ? 70 : 42), 9, 22, 34)), null, arrow.Center, arrow.Width / 2, arrow.Height / 2);
            var ink = enabled ? Color.FromRgb(232, 242, 248) : Color.FromArgb(48, 170, 190, 206);
            Chevron(ctx, arrow.Center.X, arrow.Center.Y, D(6), ink, Math.Max(1.4, 1.8 * scale), left: !right, right: right);
            if (enabled) _hits.Add(new Hit(arrow, right ? "detail-cast-next" : "detail-cast-prev"));
        }
        // Puntini della pagina: quello in corso pieno e piu' grande; al massimo nove, quelli ai bordi piu' piccoli.
        int count = maxFirst + 1;
        if (count > 1)
        {
            const int MaxDots = 9;
            int shown = Math.Min(count, MaxDots), first = Math.Clamp(_detailCastFirst - shown / 2, 0, count - shown);
            double step = D(5) + D(8), cy = Math.Min(r.Bottom - D(7), top + imageH + labelH + D(7)), sx = r.X + r.Width / 2 - (shown - 1) * step / 2;
            for (int k = 0; k < shown; k++)
            {
                int i = first + k; bool on = i == _detailCastFirst;
                double dd = on ? D(6) : D(4);
                if (!on && ((k == 0 && first > 0) || (k == shown - 1 && first + shown < count))) dd = Math.Max(2, D(4) * .55);
                else if (!on && ((k == 1 && first > 0) || (k == shown - 2 && first + shown < count))) dd = Math.Max(2.5, D(4) * .8);
                ctx.DrawEllipse(B(on ? Colors.White : Color.FromArgb(92, 194, 210, 222)), null, new Point(sx + k * step, cy), dd / 2, dd / 2);
            }
        }
    }

    private static readonly Dictionary<string, string> CrewJobNames = new()
    {
        ["Director"] = "Regia", ["Screenplay"] = "Sceneggiatura", ["Writer"] = "Sceneggiatura",
        ["Director of Photography"] = "Fotografia", ["Original Music Composer"] = "Musiche", ["Editor"] = "Montaggio",
    };
    private readonly Dictionary<string, List<MovieMetadataService.CrewMember>> _crews = new();
    private readonly HashSet<string> _crewRequests = new();

    /// <summary>Troupe da TMDb (cache su disco, poi rete in sottofondo); null finche' non c'e'.</summary>
    private List<MovieMetadataService.CrewMember>? CrewFor(LibraryItem item)
    {
        if (item.TmdbId is not int id || item.IsNetwork) return null;
        string type = item.Category == "TV Series" || LibraryStore.IsTvEpisodePath(item.Path) ? "tv" : "movie";
        string key = type + ":" + id.ToString(CultureInfo.InvariantCulture);
        if (_crews.TryGetValue(key, out var known)) return known;
        if (MovieMetadataService.GetCachedCrew(type, id) is { } cached) { _crews[key] = cached; return cached; }
        if (!_crewRequests.Add(key)) return null;
        _ = Task.Run(() =>
        {
            List<MovieMetadataService.CrewMember>? crew = null;
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); crew = MovieMetadataService.ResolveCrew(type, id, timeout.Token); } catch { }
            Dispatcher.UIThread.Post(() => { _crews[key] = crew ?? new(); InvalidateVisual(); });
        });
        return null;
    }

    private void DrawPersonFace(DrawingContext ctx, Rect circle, string name, string? photo)
    {
        using (ctx.PushGeometryClip(new EllipseGeometry(circle)))
        {
            if (photo != null && Image(photo, circle.Width) is { } bmp) DrawCover(ctx, bmp, circle, .5, .2);
            else
            {
                ctx.FillRectangle(B(Color.FromRgb(30, 36, 44)), circle);
                string initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(n => n[0])).ToUpperInvariant();
                TextAt(ctx, initials, Math.Max(12, circle.Width / 5 * .7), DisplaySemibold, Color.FromArgb(170, 255, 255, 255), circle, TextAlignment.Center);
            }
        }
    }

    internal void TestReadReview(int index) { if (index >= 0 && index < _reviewsShown.Count) { _reviewReading = _reviewsShown[index]; _reviewsScroll = 0; InvalidateVisual(); } }

    internal void TestDetailView(string view)
    {
        // "vista|titolo": il titolo (parte del nome) sceglie il film da aprire.
        string find = view.Contains('|') ? view[(view.IndexOf('|') + 1)..] : "colori";
        if (view.Contains('|')) view = view[..view.IndexOf('|')];
        var item = LibraryStore.Items("Movies").FirstOrDefault(i => i.Title.Contains(find, StringComparison.OrdinalIgnoreCase)) ?? LibraryStore.Items("Movies").FirstOrDefault();
        if (item == null) return;
        OpenDetail(item);
        SetDetailView(view == "info", view == "reviews", view == "cast");
    }
}
