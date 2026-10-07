#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        // Recensioni senza schede: fonti in una riga di collegamenti, poi pagine da
        // quattro che scorrono di lato (rotella, frecce o puntini).
        private int _reviewsScrollMax;
        private Rectangle _reviewsViewport;

        private readonly Dictionary<string, ExternalRatingsService.Ratings?> _externalRatings = new(StringComparer.OrdinalIgnoreCase);

        private ExternalRatingsService.Ratings? ExternalRatingsFor(LibraryItem item, out bool pending)
        {
            pending = false;
            if (string.IsNullOrWhiteSpace(item.ImdbId))
            {
                pending = !item.RichDetailsResolved;
                return null;
            }
            string id = item.ImdbId!;
            if (_externalRatings.TryGetValue(id, out var known))
            {
                pending = known == null;
                return known;
            }
            _externalRatings[id] = null;
            pending = true;
            bool tv = string.Equals(item.ReviewMediaType, "tv", StringComparison.OrdinalIgnoreCase);
            _ = ExternalRatingsService.ResolveAsync(id, tv, item.Title, item.Year).ContinueWith(task =>
            {
                var result = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? task.Result : new ExternalRatingsService.Ratings(null, null, null, null, null, null);
                PostToUi(() => { _externalRatings[id] = result; Invalidate(); });
            }, System.Threading.Tasks.TaskScheduler.Default);
            return null;
        }

        private readonly Dictionary<string, List<MovieMetadataService.RichReview>?> _externalReviews = new(StringComparer.OrdinalIgnoreCase);
        private bool _reviewsCritic;
        private string _reviewsSource = "all";

        private List<MovieMetadataService.RichReview>? ExternalReviewsFor(LibraryItem item, ExternalRatingsService.Ratings? ratings, out bool pending)
        {
            pending = false;
            if (ratings == null || string.IsNullOrWhiteSpace(item.ImdbId)) { pending = ratings == null; return null; }
            string id = item.ImdbId!;
            if (_externalReviews.TryGetValue(id, out var known)) { pending = known == null; return known; }
            _externalReviews[id] = null;
            pending = true;
            _ = ExternalRatingsService.ResolveReviewsAsync(ratings).ContinueWith(task =>
            {
                var result = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? task.Result : new List<MovieMetadataService.RichReview>();
                PostToUi(() => { _externalReviews[id] = result; Invalidate(); });
            }, System.Threading.Tasks.TaskScheduler.Default);
            return null;
        }

        private void SetReviewsFilter(string key)
        {
            if (key == "aud:critic" || key == "aud:public")
            {
                bool critic = key == "aud:critic";
                if (critic == _reviewsCritic) return;
                _reviewsCritic = critic;
                _reviewsSource = "all";
            }
            else if (key.StartsWith("src:", StringComparison.Ordinal)) _reviewsSource = key[4..];
            ResetReviewsPaging();
            Invalidate();
        }

        private void DrawDetailReviews(Graphics g, Rectangle r, LibraryItem item, float scale)
        {
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(12f, 15f * scale));
            using var smallFont = LibraryFont("Segoe UI", Math.Max(7.1f, 8f * scale));
            var titleRect = new Rectangle(r.Left, r.Top, r.Width, S(scale, 28));
            TextRenderer.DrawText(g, L("Recensioni e valutazioni", "Reviews & ratings"), titleFont, titleRect, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            var external = ExternalRatingsFor(item, out bool pending);
            var all = item.Reviews.ToList();
            var externalReviews = ExternalReviewsFor(item, external, out bool reviewsPending);
            if (externalReviews != null) all.AddRange(externalReviews);
            bool hasCritic = all.Any(review => review.Critic);
            if (!hasCritic) _reviewsCritic = false;
            var audience = all.Where(review => review.Critic == _reviewsCritic).ToList();
            if (_reviewsSource != "all" && !audience.Any(review => review.Source == _reviewsSource)) _reviewsSource = "all";
            var shown = _reviewsSource == "all" ? audience : audience.Where(review => review.Source == _reviewsSource).ToList();

            // Riga del titolo: a destra Pubblico/Critica e, accanto, le fonti come semplici voci
            // di testo con il voto (prima una colonna a sinistra che spostava le recensioni).
            int switchLeft = hasCritic
                ? DrawAudienceSwitch(g, titleRect, all.Count(review => !review.Critic), all.Count(review => review.Critic), scale)
                : titleRect.Right;

            string Score(double? value, string format) => value.HasValue
                ? value.Value.ToString(format, CultureInfo.InvariantCulture)
                : pending ? "…" : string.Empty;
            // Critica: il Metascore; pubblico: il voto degli utenti Metacritic.
            string metaValue = _reviewsCritic || external?.MetaUserScore == null
                ? (external?.Metascore is int critic ? critic.ToString(CultureInfo.InvariantCulture) : Score(null, "0"))
                : external.MetaUserScore.Value.ToString("0.0", CultureInfo.InvariantCulture);
            var sources = new List<ScoreColumn>
            {
                new("all", string.Empty, string.Empty, audience.Count > 0),
                new("TMDb", item.Rating.HasValue ? item.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture) : (item.RichDetailsResolved ? string.Empty : "…"),
                    BuildReviewSourceUrl("tmdb", item), true),
                new("IMDb", Score(external?.Imdb, "0.0"), BuildReviewSourceUrl("imdb", item), false),
                new("Letterboxd", Score(external?.Letterboxd, "0.0"), external?.LetterboxdUrl ?? BuildReviewSourceUrl("letterboxd", item), true),
                new("Metacritic", metaValue, external?.MetacriticUrl ?? BuildReviewSourceUrl("metacritic", item), true)
            };
            int titleW = TextRenderer.MeasureText(g, L("Recensioni e valutazioni", "Reviews & ratings"), titleFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            int sourcesLeft = titleRect.Left + titleW + S(scale, 28);
            int sourcesRight = hasCritic ? switchLeft - S(scale, 34) : titleRect.Right;
            DrawReviewSourceSwitch(g, new Rectangle(sourcesLeft, titleRect.Top, Math.Max(1, sourcesRight - sourcesLeft), titleRect.Height), sources, audience, scale);
            if (hasCritic)
                using (var divider = new Pen(Color.FromArgb(40, TextMain)))
                    g.DrawLine(divider, switchLeft - S(scale, 17), titleRect.Top + S(scale, 7), switchLeft - S(scale, 17), titleRect.Bottom - S(scale, 5));

            // Recensioni centrate nella scheda, su una colonna di larghezza comoda da leggere.
            int bodyTop = r.Top + S(scale, 52);
            int gridW = Math.Min(r.Width, S(scale, 1120));
            var viewport = new Rectangle(r.Left + (r.Width - gridW) / 2, bodyTop, gridW, Math.Max(1, r.Bottom - bodyTop));
            _reviewsViewport = viewport;

            if (!item.RichDetailsResolved || (shown.Count == 0 && reviewsPending))
            {
                _reviewsScrollMax = 0;
                DrawCastLoadingState(g, viewport, L("Carico recensioni e collegamenti…", "Loading reviews and links…"), scale);
                return;
            }
            if (shown.Count == 0)
            {
                _reviewsScrollMax = 0;
                DrawReviewsEmptyState(g, viewport, smallFont, scale);
                return;
            }
            DrawReviewList(g, viewport, shown, scale);
        }

        private sealed record ScoreColumn(string Name, string Value, string Url, bool Selectable);

        /// <summary>Pubblico / Critica allineati a destra; restituisce il bordo sinistro del gruppo.</summary>
        private int DrawAudienceSwitch(Graphics g, Rectangle titleRow, int publicCount, int criticCount, float scale)
        {
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8.8f, 9.8f * scale));
            var entries = new[] { (L("Pubblico", "Audience") + "  " + publicCount, "aud:public", !_reviewsCritic), (L("Critica", "Critics") + "  " + criticCount, "aud:critic", _reviewsCritic) };
            int gap = S(scale, 24);
            int width = entries.Sum(entry => TextRenderer.MeasureText(g, entry.Item1, font, Size.Empty, TextFormatFlags.NoPadding).Width) + gap;
            int groupLeft = titleRow.Right - width;
            int x = groupLeft;
            foreach (var (text, key, selected) in entries)
            {
                Size size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
                var bounds = new Rectangle(x, titleRow.Top, size.Width, titleRow.Height);
                bool hover = bounds.Contains(_lastMouse);
                TextRenderer.DrawText(g, text, font, bounds, selected ? TextMain : hover ? Subtle : Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (selected)
                    using (var line = new Pen(Accent, Math.Max(2f, 2f * scale)))
                        g.DrawLine(line, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
                _hits.Add(new HitZone { Bounds = Rectangle.Inflate(bounds, S(scale, 8), S(scale, 4)), Kind = HitKind.DetailReviewsFilter, Key = key });
                x = bounds.Right + gap;
            }
            return groupLeft;
        }

        // Fonti, sobrie: una riga di voci di testo con il voto accanto, allineate a destra.
        // La fonte attiva ha la stessa sottolineatura di Pubblico/Critica e una freccia che apre
        // la sua pagina; le fonti senza recensioni (es. IMDb, solo voto) aprono direttamente il sito.
        private void DrawReviewSourceSwitch(Graphics g, Rectangle row, IReadOnlyList<ScoreColumn> sources, List<MovieMetadataService.RichReview> audience, float scale)
        {
            using var nameFont = LibraryFont("Segoe UI", Math.Max(8.4f, 9.2f * scale));
            using var valueFont = LibraryFont("Segoe UI", Math.Max(7.4f, 8.2f * scale));
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int gap = S(scale, 22), valueGap = S(scale, 5), arrow = S(scale, 16);

            bool withScores = true;
            List<(ScoreColumn Source, string Name, bool Selectable, bool Selected, int NameW, int ValueW, int Width)> Measure()
            {
                var list = new List<(ScoreColumn, string, bool, bool, int, int, int)>();
                foreach (var source in sources)
                {
                    bool isAll = source.Name == "all";
                    bool selectable = source.Selectable && (isAll || audience.Any(review => review.Source == source.Name));
                    bool selected = selectable && _reviewsSource == source.Name;
                    string name = isAll ? L("Tutte", "All") : source.Name;
                    int nameW = TextRenderer.MeasureText(g, name, nameFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    int valueW = withScores && source.Value.Length > 0 ? TextRenderer.MeasureText(g, source.Value, valueFont, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
                    int width = nameW + (valueW > 0 ? valueGap + valueW : 0) + (selected && !isAll && source.Url.Length > 0 ? arrow : 0);
                    list.Add((source, name, selectable, selected, nameW, valueW, width));
                }
                return list;
            }
            var entries = Measure();
            int Total() => entries.Sum(entry => entry.Width) + gap * Math.Max(0, entries.Count - 1);
            if (Total() > row.Width) { withScores = false; entries = Measure(); }
            if (Total() > row.Width) return;

            int x = row.Right - Total();
            foreach (var entry in entries)
            {
                var bounds = new Rectangle(x, row.Top, entry.Width, row.Height);
                bool isAll = entry.Source.Name == "all";
                bool opensSite = !entry.Selectable && !isAll && entry.Source.Url.Length > 0;
                bool hover = (entry.Selectable || opensSite) && Rectangle.Inflate(bounds, S(scale, 6), S(scale, 4)).Contains(_lastMouse);
                Color ink = entry.Selected ? TextMain : hover ? Subtle : Muted;
                TextRenderer.DrawText(g, entry.Name, nameFont, new Rectangle(x, row.Top, entry.NameW + 2, row.Height), ink, Line);
                int cursor = x + entry.NameW;
                if (entry.ValueW > 0)
                {
                    TextRenderer.DrawText(g, entry.Source.Value, valueFont, new Rectangle(cursor + valueGap, row.Top + S(scale, 1), entry.ValueW + 2, row.Height),
                        Color.FromArgb(entry.Selected ? 200 : 150, Muted), Line);
                    cursor += valueGap + entry.ValueW;
                }
                if (entry.Selected)
                    using (var line = new Pen(Accent, Math.Max(2f, 2f * scale)))
                        g.DrawLine(line, x, row.Bottom, x + entry.NameW, row.Bottom);

                if (entry.Selected && !isAll && entry.Source.Url.Length > 0)
                {
                    var link = new Rectangle(cursor + S(scale, 2), row.Top + (row.Height - arrow) / 2, arrow, arrow);
                    bool linkHover = Rectangle.Inflate(link, S(scale, 3), S(scale, 3)).Contains(_lastMouse);
                    int a = S(scale, 6), ax = link.Left + (link.Width - a) / 2, ay = link.Top + (link.Height + a) / 2;
                    using (var pen = new Pen(linkHover ? Accent : Color.FromArgb(170, Muted), Math.Max(1.2f, 1.3f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(pen, ax, ay, ax + a, ay - a);
                        g.DrawLine(pen, ax + a / 2, ay - a, ax + a, ay - a);
                        g.DrawLine(pen, ax + a, ay - a, ax + a, ay - a / 2);
                    }
                    _hits.Add(new HitZone { Bounds = new Rectangle(x, row.Top - S(scale, 4), cursor - x, row.Height + S(scale, 8)), Kind = HitKind.DetailReviewsFilter, Key = "src:all" });
                    _hits.Add(new HitZone { Bounds = Rectangle.Inflate(link, S(scale, 3), S(scale, 4)), Kind = HitKind.DetailReviewSource, Key = entry.Source.Url });
                }
                else if (entry.Selectable)
                    _hits.Add(new HitZone { Bounds = Rectangle.Inflate(bounds, S(scale, 6), S(scale, 4)), Kind = HitKind.DetailReviewsFilter, Key = "src:" + entry.Source.Name });
                else if (opensSite)
                    _hits.Add(new HitZone { Bounds = Rectangle.Inflate(bounds, S(scale, 6), S(scale, 4)), Kind = HitKind.DetailReviewSource, Key = entry.Source.Url });
                x += entry.Width + gap;
            }
        }

        private void DrawReviewList(Graphics g, Rectangle viewport, IReadOnlyList<MovieMetadataService.RichReview> reviews, float scale)
        {
            using var authorFont = LibraryFont("Segoe UI Semibold", Math.Max(8.6f, 9.6f * scale));
            using var bodyFont = LibraryFont("Segoe UI", Math.Max(8f, 9f * scale));
            using var tagFont = LibraryFont("Segoe UI Semibold", Math.Max(6.6f, 7.2f * scale));
            const TextFormatFlags bodyFlags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;

            // Pagine da quattro (due per riga) che scorrono di lato; solo linee sottili
            // separano le colonne e le righe, nessun riquadro.
            int pages = (reviews.Count + 3) / 4;
            _reviewsScrollMax = Math.Max(0, pages - 1);
            _reviewsPage = Math.Clamp(_reviewsPage, 0, _reviewsScrollMax);
            int footerH = pages > 1 ? S(scale, 30) : 0;
            var grid = new Rectangle(viewport.Left, viewport.Top, viewport.Width, Math.Max(1, viewport.Height - footerH));
            int colGap = S(scale, 56), rowGap = S(scale, 34);
            int cellW = Math.Max(1, (grid.Width - colGap) / 2);
            int cellH = Math.Max(1, (grid.Height - rowGap) / 2);
            int headerH = S(scale, 24), headerGap = S(scale, 6);
            double offset = _reviewsSlide * grid.Width;
            _reviewsSlideList = reviews;
            _reviewsSlideScale = scale;
            if (_reviewsCapturingBackdrop) return;

            var state = g.Save();
            g.SetClip(grid, CombineMode.Intersect);
            using var rule = new Pen(Color.FromArgb(40, TextMain));
            for (int page = 0; page < pages; page++)
            {
                int pageLeft = grid.Left + (int)Math.Round(page * grid.Width - offset);
                if (pageLeft >= grid.Right || pageLeft + grid.Width <= grid.Left) continue;
                int onPage = Math.Min(4, reviews.Count - page * 4);
                // Un filo verticale per riga fra le due colonne, esattamente a meta' dello spazio
                // e piu' corto della cella: fra la riga sopra e quella sotto resta un vuoto netto,
                // cosi' i due fili non sembrano mai un'unica linea.
                for (int row = 0; row * 2 + 1 < onPage; row++)
                {
                    int top = grid.Top + row * (cellH + rowGap);
                    int x = pageLeft + cellW + colGap / 2;
                    g.DrawLine(rule, x, top + S(scale, 8), x, top + cellH - S(scale, 12));
                }
                for (int slot = 0; slot < onPage; slot++)
                {
                    var review = reviews[page * 4 + slot];
                    var cell = new Rectangle(pageLeft + (slot % 2) * (cellW + colGap), grid.Top + (slot / 2) * (cellH + rowGap), cellW, cellH);
                    DrawReviewHeader(g, new Rectangle(cell.Left, cell.Top, cell.Width, headerH), review, authorFont, tagFont, scale);
                    var body = new Rectangle(cell.Left, cell.Top + headerH + headerGap, cell.Width, Math.Max(1, cell.Height - headerH - headerGap));
                    string fitted = FitReviewText(review.Content, bodyFont, body.Size, bodyFlags);
                    TextRenderer.DrawText(g, fitted, bodyFont, body, Color.FromArgb(214, TextMain), bodyFlags);
                }
            }
            g.Restore(state);

            if (pages > 1)
            {
                // Indicatore di pagina con frecce: stessa logica della rotella.
                int dot = S(scale, 6), dotGap = S(scale, 8), arrow = S(scale, 26);
                int dotsW = pages * dot + (pages - 1) * dotGap;
                int cy = viewport.Bottom - footerH / 2;
                int dotsLeft = viewport.Left + (viewport.Width - dotsW) / 2;
                for (int page = 0; page < pages; page++)
                {
                    var d = new Rectangle(dotsLeft + page * (dot + dotGap), cy - dot / 2, dot, dot);
                    using var brush = new SolidBrush(page == _reviewsPage ? Accent : Color.FromArgb(70, TextMain));
                    g.FillEllipse(brush, d);
                    _hits.Add(new HitZone { Bounds = Rectangle.Inflate(d, dotGap / 2, S(scale, 6)), Kind = HitKind.DetailReviewsPage, Index = page });
                }
                void Arrow(int x, int direction, bool enabled)
                {
                    var bounds = new Rectangle(x, cy - arrow / 2, arrow, arrow);
                    bool hover = enabled && bounds.Contains(_lastMouse);
                    using var pen = new Pen(Color.FromArgb(enabled ? (hover ? 255 : 190) : 60, hover ? Accent : TextMain), Math.Max(1.6f, 1.8f * scale))
                        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    int cx = bounds.Left + bounds.Width / 2, h = S(scale, 6);
                    g.DrawLines(pen, new[] { new Point(cx + direction * -h / 2, cy - h), new Point(cx + direction * h / 2, cy), new Point(cx + direction * -h / 2, cy + h) });
                    if (enabled) _hits.Add(new HitZone { Bounds = bounds, Kind = HitKind.DetailReviewsPage, Index = _reviewsPage + direction });
                }
                Arrow(dotsLeft - arrow - S(scale, 12), -1, _reviewsPage > 0);
                Arrow(dotsLeft + dotsW + S(scale, 12), 1, _reviewsPage < pages - 1);
            }
        }

        // Scorrimento laterale animato fra le pagine.
        private int _reviewsPage;
        private double _reviewsSlide;
        private System.Windows.Forms.Timer? _reviewsSlideTimer;
        private double _reviewsSlideFrom;
        private long _reviewsSlideStart;
        private const double ReviewsSlideMs = 260;

        // Testo già ritagliato per cella: misurarlo a ogni fotogramma rendeva lo scorrimento lento.
        private readonly Dictionary<(string Text, int Width, int Height, float Font), string> _reviewFitCache = new();

        private string FitReviewText(string text, Font font, Size size, TextFormatFlags flags)
        {
            var key = (text, size.Width, size.Height, font.Size);
            if (_reviewFitCache.TryGetValue(key, out var fitted)) return fitted;
            if (_reviewFitCache.Count > 64) _reviewFitCache.Clear();
            return _reviewFitCache[key] = FitTextWithEllipsis(text, font, size, flags);
        }

        // Durante lo scorrimento si ridisegna solo la griglia sopra uno sfondo catturato una
        // volta: ridipingere scheda, backdrop e gradienti costava ~80 ms a fotogramma.
        private Bitmap? _reviewsSlideBackdrop;
        private Rectangle _reviewsSlideArea;
        private IReadOnlyList<MovieMetadataService.RichReview>? _reviewsSlideList;
        private float _reviewsSlideScale = 1f;
        private bool _reviewsCapturingBackdrop;

        private void CaptureReviewsSlideBackdrop()
        {
            _reviewsSlideBackdrop?.Dispose();
            _reviewsSlideBackdrop = null;
            if (_reviewsViewport.IsEmpty || Width < 1 || Height < 1) return;
            var full = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                _reviewsCapturingBackdrop = true;
                using (var g = Graphics.FromImage(full)) PaintLibrary(g);
                _reviewsSlideArea = Rectangle.Intersect(ClientRectangle, Rectangle.Inflate(_reviewsViewport, 8, 4));
                _reviewsSlideBackdrop = full.Clone(_reviewsSlideArea, full.PixelFormat);
            }
            catch { _reviewsSlideBackdrop = null; }
            finally { _reviewsCapturingBackdrop = false; full.Dispose(); }
        }

        // Pagine gia' impaginate durante lo scorrimento: il testo si impagina una volta sola
        // all'inizio, poi ogni fotogramma sposta soltanto delle immagini (~1-2 ms).
        private readonly Dictionary<int, Bitmap> _reviewsPageCache = new();

        private void ClearReviewsPageCache()
        {
            foreach (var bitmap in _reviewsPageCache.Values) bitmap.Dispose();
            _reviewsPageCache.Clear();
        }

        private Bitmap RenderReviewsPage(int page)
        {
            var bitmap = new Bitmap(_reviewsSlideBackdrop!.Width, _reviewsSlideBackdrop.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var pg = Graphics.FromImage(bitmap);
            pg.DrawImageUnscaled(_reviewsSlideBackdrop, 0, 0);
            pg.TranslateTransform(-_reviewsSlideArea.X, -_reviewsSlideArea.Y);
            pg.SmoothingMode = SmoothingMode.AntiAlias;
            double slide = _reviewsSlide;
            bool previous = _darkSurfaceText;
            _reviewsSlide = page;
            _darkSurfaceText = true;
            try { DrawReviewList(pg, _reviewsViewport, _reviewsSlideList!, _reviewsSlideScale); }
            finally { _reviewsSlide = slide; _darkSurfaceText = previous; }
            return bitmap;
        }

        private bool TryPaintReviewsSlideFrame(PaintEventArgs e)
        {
            if (_reviewsSlideBackdrop == null || _reviewsSlideTimer?.Enabled != true || _reviewsSlideList == null ||
                !_detailReviewsVisible || !_reviewsSlideArea.Contains(e.ClipRectangle)) return false;
            var g = e.Graphics;
            int first = (int)Math.Floor(Math.Min(_reviewsSlideFrom, _reviewsPage));
            int last = (int)Math.Ceiling(Math.Max(_reviewsSlideFrom, _reviewsPage));
            _hits.RemoveAll(h => h.Kind == HitKind.DetailReviewsPage);
            for (int page = first; page <= last; page++)
                if (page != _reviewsPage && !_reviewsPageCache.ContainsKey(page))
                    _reviewsPageCache[page] = RenderReviewsPage(page);
            // La pagina di arrivo per ultima: i suoi punti/frecce restano come zone cliccabili.
            if (!_reviewsPageCache.ContainsKey(_reviewsPage))
                _reviewsPageCache[_reviewsPage] = RenderReviewsPage(_reviewsPage);

            int pages = (_reviewsSlideList.Count + 3) / 4;
            int footerH = pages > 1 ? S(_reviewsSlideScale, 30) : 0;
            var grid = new Rectangle(_reviewsViewport.Left, _reviewsViewport.Top, _reviewsViewport.Width, Math.Max(1, _reviewsViewport.Height - footerH));
            var target = _reviewsPageCache[_reviewsPage];
            g.DrawImageUnscaled(target, _reviewsSlideArea.Location); // sfondo + piè di pagina della pagina di arrivo
            using (var backdrop = new TextureBrush(_reviewsSlideBackdrop, WrapMode.Clamp))
            {
                backdrop.TranslateTransform(_reviewsSlideArea.X, _reviewsSlideArea.Y);
                g.FillRectangle(backdrop, grid);
            }
            var state = g.Save();
            g.SetClip(grid, CombineMode.Intersect);
            foreach (var (page, bitmap) in _reviewsPageCache)
            {
                if (page < first || page > last) continue;
                int dx = (int)Math.Round((page - _reviewsSlide) * grid.Width);
                if (Math.Abs(dx) >= grid.Width) continue;
                var source = new Rectangle(grid.X - _reviewsSlideArea.X, grid.Y - _reviewsSlideArea.Y, grid.Width, grid.Height);
                g.DrawImage(bitmap, new Rectangle(grid.X + dx, grid.Y, grid.Width, grid.Height), source, GraphicsUnit.Pixel);
            }
            g.Restore(state);
            return true;
        }

        private void GoToReviewsPage(int page)
        {
            int target = Math.Clamp(page, 0, _reviewsScrollMax);
            if (target == _reviewsPage && _reviewsSlide == target) return;
            if (_reviewsSlideTimer?.Enabled != true) { ClearReviewsPageCache(); CaptureReviewsSlideBackdrop(); }
            _reviewsSlideFrom = _reviewsSlide;
            _reviewsPage = target;
            _reviewsSlideStart = CinecorePlayer2025.Utilities.AnimationClock.NowMs;
            if (_reviewsSlideTimer == null)
            {
                _reviewsSlideTimer = new System.Windows.Forms.Timer { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
                _reviewsSlideTimer.Tick += (_, _) =>
                {
                    // Durata fissa con uscita morbida: prima l'avvicinamento esponenziale
                    // trascinava gli ultimi pixel per quasi mezzo secondo.
                    double t = Math.Clamp((CinecorePlayer2025.Utilities.AnimationClock.NowMs - _reviewsSlideStart) / ReviewsSlideMs, 0, 1);
                    double eased = 1 - Math.Pow(1 - t, 3);
                    _reviewsSlide = _reviewsSlideFrom + (_reviewsPage - _reviewsSlideFrom) * eased;
                    if (t >= 1)
                    {
                        _reviewsSlide = _reviewsPage; _reviewsSlideTimer!.Stop();
                        _reviewsSlideBackdrop?.Dispose(); _reviewsSlideBackdrop = null;
                        ClearReviewsPageCache();
                    }
                    Invalidate(_reviewsSlideArea.IsEmpty ? Rectangle.Inflate(_reviewsViewport, 8, 4) : _reviewsSlideArea);
                    Update();
                };
            }
            _reviewsSlideTimer.Start();
        }

        private void ResetReviewsPaging()
        {
            _reviewsPage = 0;
            _reviewsSlide = 0;
            _reviewsSlideTimer?.Stop();
            _reviewsSlideBackdrop?.Dispose();
            _reviewsSlideBackdrop = null;
        }

        private void DrawReviewHeader(Graphics g, Rectangle r, MovieMetadataService.RichReview review, Font authorFont, Font tagFont, float scale)
        {
            string author = string.IsNullOrWhiteSpace(review.Author) ? "TMDb user" : review.Author;
            Size authorSize = TextRenderer.MeasureText(g, author, authorFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
            int authorW = Math.Min(authorSize.Width + 2, Math.Max(40, r.Width / 2));
            TextRenderer.DrawText(g, author, authorFont, new Rectangle(r.Left, r.Top, authorW, r.Height), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
            int x = r.Left + authorW + S(scale, 12);
            if (review.Rating.HasValue)
            {
                int star = S(scale, 13);
                DrawIcon(g, new Rectangle(x, r.Top + (r.Height - star) / 2, star, star), "star", Color.FromArgb(255, 196, 64));
                x += star + S(scale, 5);
                string rating = review.Rating.Value.ToString("0.#", CultureInfo.InvariantCulture) + "/" + review.RatingScale.ToString("0", CultureInfo.InvariantCulture);
                int ratingW = TextRenderer.MeasureText(g, rating, authorFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping).Width;
                TextRenderer.DrawText(g, rating, authorFont, new Rectangle(x, r.Top, ratingW + 2, r.Height), Subtle,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
                x += ratingW + S(scale, 12);
            }
            // Fonte, testata e lingua come testo tenue: niente pillole/riquadri.
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(review.Detail)) meta.Add(review.Detail!);
            meta.Add(review.Source);
            if (!string.IsNullOrWhiteSpace(review.Language)) meta.Add(review.Language!.ToUpperInvariant());
            TextRenderer.DrawText(g, string.Join(" · ", meta), tagFont, new Rectangle(x, r.Top, Math.Max(1, r.Right - x), r.Height), Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }

        private void DrawReviewsEmptyState(Graphics g, Rectangle area, Font smallFont, float scale)
        {
            int blockH = S(scale, 132);
            var block = new Rectangle(area.Left, area.Top + Math.Max(0, (Math.Min(area.Height, S(scale, 220)) - blockH) / 2), area.Width, blockH);
            int badge = S(scale, 48);
            var circle = new Rectangle(block.Left + (block.Width - badge) / 2, block.Top, badge, badge);
            using (var fill = new SolidBrush(Color.FromArgb(34, Accent)))
                g.FillEllipse(fill, circle);
            HUD.MusicTransportBar.DrawSymbol(g, Rectangle.Inflate(circle, -S(scale, 13), -S(scale, 13)), "review", Accent);
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(9.4f, 10.5f * scale));
            TextRenderer.DrawText(g, L("Ancora nessuna recensione", "No reviews yet"), titleFont,
                new Rectangle(block.Left, circle.Bottom + S(scale, 14), block.Width, S(scale, 24)), TextMain,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L("Nessuno ha ancora scritto di questo titolo. Le fonti qui sopra aprono le rispettive pagine.",
                    "Nobody has reviewed this title yet. The sources above open their pages."), smallFont,
                new Rectangle(block.Left + S(scale, 24), circle.Bottom + S(scale, 42), block.Width - S(scale, 48), S(scale, 40)), Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        private static string BuildReviewSourceUrl(string source, LibraryItem item)
        {
            string title = Uri.EscapeDataString(item.Title ?? string.Empty);
            return source switch
            {
                "tmdb" when item.TmdbId.HasValue =>
                    $"https://www.themoviedb.org/{(string.Equals(item.ReviewMediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie")}/{item.TmdbId.Value}/reviews",
                "tmdb" => $"https://www.themoviedb.org/search?query={title}",
                "imdb" when !string.IsNullOrWhiteSpace(item.ImdbId) => $"https://www.imdb.com/title/{item.ImdbId}/reviews/",
                "imdb" => $"https://www.imdb.com/find/?q={title}",
                "letterboxd" => $"https://letterboxd.com/search/{title}/",
                "metacritic" => $"https://www.metacritic.com/search/{title}/",
                _ => string.Empty
            };
        }
    }
}
