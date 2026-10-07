#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (TryPaintReviewsSlideFrame(e)) return;
            _paintClip = e.ClipRectangle;
            try { PaintLibrary(e.Graphics); }
            finally { _paintClip = Rectangle.Empty; }
        }

        // OnPaint copre sempre tutta l'area ridisegnata: il riempimento di fondo di WinForms
        // era lavoro doppio a ogni fotogramma (qualche millisecondo a 1440p).
        protected override void OnPaintBackground(PaintEventArgs e) { }

        // Area being repainted (empty = everything, e.g. snapshots). The double-buffered
        // Graphics does not expose it through its clip.
        private Rectangle _paintClip = Rectangle.Empty;
        private bool NeedsPaint(Rectangle area) => _paintClip.IsEmpty || _paintClip.IntersectsWith(area);

        private void PaintLibrary(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool scrolling = _gridScrollAnimationTimer.Enabled;
            g.InterpolationMode = scrolling ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = scrolling ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            _hits.Clear();
            _gridViewport = Rectangle.Empty;

            Rectangle b = ClientRectangle;
            if (b.Width <= 1 || b.Height <= 1)
                return;
            // Dissolvenza fra due pagine in corso: la pagina nuova e' gia' dipinta in un livello.
            if (TryPaintPageCrossfadeFrame(g))
                return;

            // Con una scheda aperta (o in entrata/uscita) la pagina dietro e' ferma e coperta:
            // si dipinge una volta in un livello e poi si copia soltanto. Ridipingerla a ogni
            // ridisegno costava ~40 ms anche solo per il passaggio del mouse su un pulsante
            // della scheda. A scheda ferma il livello si rinfresca ogni secondo e mezzo, cosi'
            // le locandine che finiscono di caricarsi dietro compaiono comunque.
            bool sheetOpen = SheetAnimating || _detailItem != null || _activeGroup != null;
            bool wholePage = _paintClip.IsEmpty || _paintClip.Contains(b);
            bool reusable = _sheetPage != null && _sheetPage.Size == b.Size &&
                (SheetAnimating || !wholePage || Environment.TickCount64 - _sheetPageTick < 1500);
            if (sheetOpen && (reusable || wholePage))
            {
                if (!reusable)
                {
                    DropSheetPage();
                    var pageLayer = new GdiLayer(this, b.Size);
                    try
                    {
                        var pg = pageLayer.Graphics;
                        pg.SmoothingMode = g.SmoothingMode;
                        pg.InterpolationMode = g.InterpolationMode;
                        pg.PixelOffsetMode = g.PixelOffsetMode;
                        pg.TextRenderingHint = g.TextRenderingHint;
                        PaintPageBehindSheets(pg, b);
                    }
                    catch { pageLayer.Dispose(); throw; }
                    _sheetPage = pageLayer;
                    _sheetPageTick = Environment.TickCount64;
                    _sheetPageHits.Clear();
                    _sheetPageHits.AddRange(_hits);
                    _sheetPageViewport = _gridViewport;
                }
                else
                {
                    _hits.AddRange(_sheetPageHits);
                    _gridViewport = _sheetPageViewport;
                }
                _sheetPage!.DrawTo(g, b, Point.Empty);
            }
            else
            {
                if (!sheetOpen) DropSheetPage();
                PaintPageBehindSheets(g, b);
            }

            // Scheda appena chiusa (in dissolvenza) sotto, quella aperta sopra.
            DrawSheetExit(g, b);
            if (_detailItem is { } detail)
            {
                if (_sheetEntrance < 1 && _activeGroup == null) DrawSheetEntrance(g, b, DetailSheetBounds(SheetBounds(b)), layer => DrawDetailOverlay(layer, b, detail));
                else DrawDetailOverlay(g, b, detail);
            }
            if (_activeGroup is { } group)
            {
                if (_sheetEntrance < 1) DrawSheetEntrance(g, b, GroupPanelBounds(SheetBounds(b), group), layer => DrawGroupPicker(layer, b));
                else DrawGroupPicker(g, b);
            }
            if (_diaryRatingItem != null)
                DrawDiaryRatingOverlay(g, b, _diaryRatingItem);
            DrawSurfaceEntrance(g);
            DrawQueueToast(g, b);
            DrawPressedFeedback(g);
            DrawKeyboardFocus(g);
            // "Tutta la pagina" esclude la fascia della barra musicale: e' una finestra figlia, ritagliata
            // fuori dall'area da ridisegnare, e con la musica in corso un ridisegno non la copre mai.
            HoldOrStartPageCrossfade(g, _paintClip.IsEmpty || _paintClip.Contains(new Rectangle(b.X, b.Y, b.Width, Math.Max(1, b.Height - MusicTransportInset))));
            LayoutSearchBox();
            LayoutWebInputBox();
        }

        /// <summary>La pagina senza le schede sopra: fondo, barra laterale e contenuto.</summary>
        private void PaintPageBehindSheets(Graphics g, Rectangle b)
        {
            using (var fill = new SolidBrush(SidebarSurface))
                g.FillRectangle(fill, b);

            // Sidebar leggermente piÃ¹ presente: prima a 1920px finiva intorno a 215px,
            // troppo sottile rispetto al blocco principale giÃ  scalato.
            int sidebarW = SidebarWidth(b);
            Rectangle sidebar = new Rectangle(0, 0, sidebarW, b.Height);
            // Il contenuto e' una superficie sollevata: sidebar e cornice un filo piu' scure,
            // pannello arrotondato con un piccolo margine. La divisione nasce dal cambio di
            // piano, senza linee.
            int surfaceMargin = S(SidebarScale(sidebar), 10);
            Rectangle main = new Rectangle(sidebar.Right, surfaceMargin, Math.Max(1, b.Width - sidebar.Right - surfaceMargin), Math.Max(1, b.Height - surfaceMargin * 2));
            using var mainPath = Round(main, S(SidebarScale(sidebar), 14));
            _lastMainArea = main;

            DrawMainBackground(g, mainPath);
            // Grid scrolling invalidates only the grid: the sidebar is outside the clip,
            // so it is not redrawn, but its click zones from the last real draw stay valid.
            if (NeedsPaint(sidebar) || _sidebarHits.Count == 0 || _sidebarHitsBounds != sidebar)
            {
                int firstSidebarHit = _hits.Count;
                DrawSidebar(g, sidebar);
                _sidebarHits.Clear();
                _sidebarHits.AddRange(_hits.GetRange(firstSidebarHit, _hits.Count - firstSidebarHit));
                _sidebarHitsBounds = sidebar;
            }
            else
                _hits.AddRange(_sidebarHits);
            var surfaceState = g.Save();
            g.SetClip(mainPath, CombineMode.Intersect);
            DrawMain(g, main);
            g.Restore(surfaceState);
            DrawSurfaceEdge(g, mainPath);
            if (_contentLoading)
                DrawContentLoading(g, main);
        }

        private readonly List<HitZone> _sidebarHits = new();
        private Rectangle _sidebarHitsBounds;

        private static float LayoutScale(Rectangle area)
        {
            if (area.Width <= 0 || area.Height <= 0)
                return 1f;

            // 1920x1080 Ã¨ la taglia base del concept. Sopra questa dimensione non
            // ingrandiamo una singola sezione: scala tutta la pagina in modo uniforme.
            float widthScale = area.Width / 1688f;
            float heightScale = area.Height / 1080f;
            float scale = Math.Min(widthScale, heightScale);
            return Math.Clamp(scale, 0.45f, 4f);
        }

        private static int SidebarWidth(Rectangle viewport)
        {
            float scale = Math.Clamp(Math.Min(viewport.Width / 1920f, viewport.Height / 1080f), .45f, 4f);
            return Math.Min(Math.Max(1, viewport.Width / 3), S(scale, 242));
        }

        private static int S(float scale, double value)
        {
            return Math.Max(1, (int)Math.Round(value * scale));
        }

        // Sidebar e cornice: il colore del pannello appena piu' scuro.
        private static Color SidebarSurface => HUD.Theme.SidebarSurface;

        private void DrawMainBackground(Graphics g, GraphicsPath main)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var mainFill = new SolidBrush(NavBottom))
                g.FillPath(mainFill, main);
            g.SmoothingMode = mode;
        }

        // Bordo della superficie: angoli puliti (antialias sul ritaglio) e un filo di luce.
        private void DrawSurfaceEdge(Graphics g, GraphicsPath main)
        {
            SmoothClipEdge(g, main, SidebarSurface);
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var rim = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 36 : 20, HUD.Theme.IsLight ? Color.Black : Color.White), 1f);
            g.DrawPath(rim, main);
            g.SmoothingMode = mode;
        }

        private void DrawSidebar(Graphics g, Rectangle r)
        {
            float scale = SidebarScale(r);

            using (var nav = new SolidBrush(SidebarSurface))
                g.FillRectangle(nav, r);


            DrawLogo(g, new Rectangle(r.Left + S(scale, 13), r.Top + S(scale, 10), r.Width - S(scale, 26), S(scale, 56)));
            int y = r.Top + S(scale, 80);
            DrawSectionLabel(g, r, L("LIBRERIA", "LIBRARY"), ref y);
            DrawNav(g, r, "Home", "Home", "home", ref y, selected: _view == PageView.Home);
            DrawNav(g, r, "Movies", L("Film", "Movies"), "movie", ref y, selected: _view == PageView.Collection && string.Equals(_category, "Movies", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "TV Series", L("Serie TV", "TV Series"), "tv", ref y, selected: _view == PageView.Collection && string.Equals(_category, "TV Series", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Videos", L("Video", "Videos"), "video", ref y, selected: _view == PageView.Collection && string.Equals(_category, "Videos", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Music", L("Musica", "Music"), "music", ref y, selected: _view is not (PageView.Home or PageView.Network or PageView.WebInput) && string.Equals(_category, "Music", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Photos", L("Foto", "Photos"), "photo", ref y, selected: _view is not (PageView.Home or PageView.Network or PageView.WebInput) && string.Equals(_category, "Photos", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Favourites", L("Preferiti", "Favourites"), "star", ref y, selected: _view is not (PageView.Home or PageView.Network or PageView.WebInput) && string.Equals(_category, "Favourites", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "WatchHistory", L("Diario", "Diary"), "time", ref y, selected: _view is not (PageView.Home or PageView.Network or PageView.WebInput) && string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Playlists", "Playlist", "playlist", ref y, selected: _view is not (PageView.Home or PageView.Network or PageView.WebInput) && string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase));

            y += S(scale, 22);
            DrawSectionLabel(g, r, L("DISPOSITIVI", "DEVICES"), ref y);
            // Computer e Rete dicono quale sorgente e' in uso, sempre con lo stesso segno (anche nella pagina dei server).
            DrawNav(g, r, "Computer", "Computer", "computer", ref y, selected: string.Equals(_source, "Computer", StringComparison.OrdinalIgnoreCase) && _view != PageView.Network, sourceMarker: true);
            DrawNav(g, r, "Network", L("Rete", "Network"), "network", ref y, selected: _view == PageView.Network || string.Equals(_source, "Network", StringComparison.OrdinalIgnoreCase), sourceMarker: true);

            y += S(scale, 22);
            DrawSectionLabel(g, r, "ONLINE", ref y);
            DrawNav(g, r, "YouTube", "YouTube", "youtube", ref y, selected: _view == PageView.WebInput && string.Equals(_webInputKind, "YouTube", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "URL", "URL", "link", ref y, selected: _view == PageView.WebInput && string.Equals(_webInputKind, "URL", StringComparison.OrdinalIgnoreCase));

            y += S(scale, 22);
            DrawSectionLabel(g, r, L("IMPOSTAZIONI", "SETTINGS"), ref y);
            DrawNav(g, r, "Settings", L("Impostazioni", "Settings"), "settings", ref y, selected: false);
            DrawThemeModeSwitch(g, r, scale);
        }

        private void DrawMain(Graphics g, Rectangle main)
        {
            float scale = LayoutScale(main);
            int padX = Math.Max(S(scale, 32), (int)Math.Round(main.Width * 0.024));
            int top = main.Top + S(scale, 18);
            Rectangle content = new Rectangle(main.Left + padX, top, Math.Max(1, main.Width - padX * 2), Math.Max(1, main.Height - S(scale, 38)));

            if (_musicWorkspaceContent != null)
            {
                DrawHeader(g, content, scale);
                return;
            }

            if (_view == PageView.WebInput)
            {
                DrawWebInputPage(g, content, scale);
                return;
            }

            if (_view == PageView.Network)
            {
                _searchShellRect = Rectangle.Empty;
                DrawNetworkPage(g, content, scale);
                return;
            }

            // Anche il catalogo di un server (Jellyfin/DLNA) ha ricerca, filtro e ordinamento;
            // DrawHeader toglie solo le azioni che valgono per le cartelle locali (scan/add).
            DrawHeader(g, content, scale);

            if (_view == PageView.Collection)
            {
                DrawCollectionPage(g, content, scale);
                return;
            }

            if (_items.Count == 0 && _resumeItems.Count == 0 && _heroItem == null)
            {
                DrawEmptyHome(g, new Rectangle(content.Left, content.Top + S(scale, 62), content.Width,
                    Math.Max(1, content.Height - S(scale, 86))), scale);
                return;
            }

            // Layout proporzionale: a 1440p/4K non si allunga solo Movies,
            // si scala tutto il blocco centrale mantenendo gli stessi rapporti del concept 16:9.
            int heroY = content.Top + S(scale, 50);
            int heroH = S(scale, 342);
            int continueTitleGap = S(scale, 14);
            int titleH = S(scale, 28);
            int continueTitleToRow = S(scale, 34);
            int continueH = S(scale, 144);
            int moviesGap = S(scale, 18);
            int moviesTitleToGrid = S(scale, 38);
            int statsGap = S(scale, 20);
            int statsH = S(scale, 58);
            int bottomMargin = S(scale, 28);

            int moviesTitleY = heroY + heroH + continueTitleGap + continueTitleToRow + continueH + moviesGap;
            int gridTop = moviesTitleY + moviesTitleToGrid;
            int availableGridH = content.Bottom - gridTop - statsGap - statsH - bottomMargin;
            int desiredGridH = EstimatePosterCardHeight(content.Width, scale, VisibleItems().Count());

            if (availableGridH < S(scale, 218))
            {
                int missing = S(scale, 218) - availableGridH;
                heroH = Math.Max(S(scale, 270), heroH - missing / 2);
                continueH = Math.Max(S(scale, 112), continueH - missing / 3);
                moviesTitleY = heroY + heroH + continueTitleGap + continueTitleToRow + continueH + moviesGap;
                gridTop = moviesTitleY + moviesTitleToGrid;
                availableGridH = content.Bottom - gridTop - statsGap - statsH - bottomMargin;
            }

            int gridH = Math.Max(S(scale, 190), Math.Min(desiredGridH, Math.Max(1, availableGridH)));
            if (availableGridH > gridH)
            {
                // Distribuisce l'aria rimanente allungando proporzionalmente le card,
                // ma senza deformarle in modo eccessivo.
                gridH = Math.Min(availableGridH, gridH + Math.Min(availableGridH - gridH, S(scale, 64)));
            }

            // Reserve breathing room for carousel dots without changing poster geometry.
            heroH -= S(scale, 36);
            continueTitleGap += S(scale, 16);
            continueTitleToRow += S(scale, 8);
            moviesGap += S(scale, 12);
            Rectangle hero = new Rectangle(content.Left, heroY, content.Width, heroH);
            DrawHero(g, hero);

            int continueTitleY = hero.Bottom + continueTitleGap;
            DrawTitle(g, L("Continua a guardare", "Continue watching"), new Rectangle(content.Left, continueTitleY, content.Width, titleH), 13.2f * scale);

            int continueY = continueTitleY + continueTitleToRow;
            Rectangle resumeRect = new Rectangle(content.Left, continueY, content.Width, continueH);
            DrawResumeRow(g, resumeRect);

            moviesTitleY = resumeRect.Bottom + moviesGap;
            Rectangle gridTitleRect = new Rectangle(content.Left, moviesTitleY, content.Width, titleH);
            DrawTitle(g, GridTitle(), gridTitleRect, 13.2f * scale);
            if (string.Equals(_category, "Movies", StringComparison.OrdinalIgnoreCase))
            {
                Rectangle seeAll = new Rectangle(gridTitleRect.Right - S(scale, 82), gridTitleRect.Top - S(scale, 1), S(scale, 82), S(scale, 28));
                DrawSeeAll(g, seeAll);
            }

            gridTop = moviesTitleY + moviesTitleToGrid;
            Rectangle grid = new Rectangle(content.Left, gridTop, content.Width, gridH);
            DrawGrid(g, grid);

            int statsTop = grid.Bottom + statsGap;
            int maxStatsTop = content.Bottom - statsH - bottomMargin;
            if (statsTop > maxStatsTop)
                statsTop = maxStatsTop;

            Rectangle stats = new Rectangle(
                content.Left + Math.Max(0, (content.Width - Math.Min(content.Width, S(scale, 1180))) / 2),
                statsTop,
                Math.Min(content.Width, S(scale, 1180)),
                statsH);

            DrawStats(g, stats);
        }

        private void DrawEmptyHome(Graphics g, Rectangle area, float scale)
        {
            int maxWidth = Math.Min(area.Width, S(scale, 840));
            int maxHeight = Math.Min(area.Height, S(scale, 360));
            Rectangle card = new(
                area.Left + (area.Width - maxWidth) / 2,
                area.Top + Math.Max(0, (area.Height - maxHeight) / 2 - S(scale, 18)),
                maxWidth,
                maxHeight);

            // Primo avvio: niente riquadro, solo icona, un messaggio e le due azioni.

            int iconSize = S(scale, 48);
            Rectangle icon = new(card.Left + (card.Width - iconSize) / 2, card.Top + S(scale, 58), iconSize, iconSize);
            DrawIcon(g, icon, "folder", Color.FromArgb(210, Accent));
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(17f, 20f * scale));
            using var body = LibraryFont("Segoe UI", Math.Max(9f, 10.2f * scale));
            Rectangle titleRect = new(card.Left + S(scale, 42), icon.Bottom + S(scale, 22), card.Width - S(scale, 84), S(scale, 36));
            Rectangle bodyRect = new(titleRect.Left, titleRect.Bottom + S(scale, 8), titleRect.Width, S(scale, 50));
            TextRenderer.DrawText(g, L("La tua libreria è pronta", "Your library is ready"), title, titleRect, TextMain,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g,
                L("Aggiungi una cartella multimediale oppure avvia una scansione per iniziare.",
                  "Add a media folder or start a scan to begin."),
                body, bodyRect, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int buttonW = S(scale, 142);
            int buttonGap = S(scale, 14);
            int buttonsX = card.Left + (card.Width - buttonW * 2 - buttonGap) / 2;
            int buttonsY = card.Bottom - S(scale, 70);
            DrawHeaderButton(g, new Rectangle(buttonsX, buttonsY, buttonW, S(scale, 38)), L("Aggiungi", "Add Source"), "add", "add");
            DrawHeaderButton(g, new Rectangle(buttonsX + buttonW + buttonGap, buttonsY, buttonW, S(scale, 38)), L("Scansiona", "Scan"), "scan", "scan");
        }

        private void DrawCollectionGrid(Graphics g, Rectangle viewport, IReadOnlyList<LibraryItem> visible, float scale)
        {
            _gridViewport = viewport;
            if (string.Equals(_category, "Favourites", StringComparison.OrdinalIgnoreCase))
            {
                DrawCategorizedGrid(g, viewport, visible, scale);
                return;
            }

            if (UsesTemporalSections(_category))
            {
                DrawTemporalGrid(g, viewport, visible, scale);
                return;
            }

            DrawFullGrid(g, viewport, visible, scale);
        }

        private void DrawCategorizedGrid(Graphics g, Rectangle viewport, IReadOnlyList<LibraryItem> visible, float scale)
        {
            if (visible.Count == 0)
            {
                _gridScroll = 0;
                _gridScrollMax = 0;
                DrawEmptyGrid(g, viewport);
                return;
            }

            var order = new[] { "Movies", "TV Series", "Videos", "Music", "Photos" };
            var sections = visible
                .GroupBy(item => item.Category)
                .OrderBy(group =>
                {
                    int i = Array.FindIndex(order, value => string.Equals(value, group.Key, StringComparison.OrdinalIgnoreCase));
                    return i < 0 ? 99 : i;
                })
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Title = CategorySectionTitle(group.Key), Items = group.OrderByDescending(item => item.SortDateUtc).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList() })
                .ToList();

            int gap = Math.Max(S(scale, 22), (int)Math.Round(viewport.Width * 0.012));
            int headerH = S(scale, 32);
            int sectionGap = S(scale, 30);
            int targetCardW = S(scale, 176);
            int columns = Math.Max(2, Math.Min(12, (viewport.Width + gap) / Math.Max(1, targetCardW + gap)));
            if (viewport.Width < S(scale, 520))
                columns = Math.Max(1, Math.Min(columns, (viewport.Width + gap) / Math.Max(1, S(scale, 150) + gap)));

            int cardW = Math.Max(1, (viewport.Width - gap * (columns - 1)) / columns);
            int cardH = Math.Max(S(scale, 230), (int)Math.Round(cardW * 1.58));
            int contentH = 0;
            foreach (var section in sections)
            {
                int rows = (int)Math.Ceiling(section.Items.Count / (double)columns);
                contentH += headerH + S(scale, 12) + rows * cardH + Math.Max(0, rows - 1) * gap + sectionGap;
            }
            contentH = Math.Max(0, contentH - sectionGap);

            _gridScrollMax = Math.Max(0, contentH - viewport.Height);
            _gridScroll = Math.Max(0, Math.Min(_gridScroll, _gridScrollMax));

            using Region previousClip = g.Clip.Clone();
            using Region clip = new Region(viewport);
            g.SetClip(clip, CombineMode.Intersect);

            int y = viewport.Top - _gridScroll;
            foreach (var section in sections)
            {
                var header = new Rectangle(viewport.Left, y, viewport.Width, headerH);
                if (header.Bottom >= viewport.Top && header.Top <= viewport.Bottom)
                    DrawTemporalSectionHeader(g, header, section.Title, scale);
                y += headerH + S(scale, 12);

                int rows = (int)Math.Ceiling(section.Items.Count / (double)columns);
                for (int row = 0; row < rows; row++)
                {
                    int x = viewport.Left;
                    int remainder = Math.Max(0, viewport.Width - (cardW * columns + gap * (columns - 1)));
                    for (int col = 0; col < columns; col++)
                    {
                        int index = row * columns + col;
                        if (index >= section.Items.Count)
                            break;

                        int extra = col < remainder ? 1 : 0;
                        int w = cardW + extra;
                        Rectangle card = new Rectangle(x, y, w, cardH);
                        if (card.Bottom >= viewport.Top && card.Top <= viewport.Bottom)
                            DrawPosterCard(g, card, section.Items[index]);
                        x += w + gap;
                        g.SetClip(clip, CombineMode.Replace);
                    }
                    y += cardH + gap;
                }

                y += sectionGap - gap;
            }

            g.Clip = previousClip;
            DrawGridScrollbar(g, viewport, contentH);
        }

        private void DrawTemporalGrid(Graphics g, Rectangle viewport, IReadOnlyList<LibraryItem> visible, float scale)
        {
            if (visible.Count == 0)
            {
                _gridScroll = 0;
                _gridScrollMax = 0;
                DrawEmptyGrid(g, viewport);
                return;
            }

            var sections = BuildTemporalSections(visible);
            bool photos = string.Equals(_category, "Photos", StringComparison.OrdinalIgnoreCase);
            int totalAvailable = photos ? Math.Max(_photoTotalAvailable, visible.Count) : visible.Count;
            bool hasMore = photos && visible.Count < totalAvailable;
            int gap = Math.Max(S(scale, 20), (int)Math.Round(viewport.Width * 0.010));
            int rowGap = S(scale, 20);
            int sectionGap = S(scale, 30);
            int headerH = S(scale, 32);
            int loadMoreH = S(scale, 48);
            int loadMoreGap = S(scale, 18);
            int targetCardW = S(scale, 248);
            int columns = Math.Max(2, Math.Min(8, (viewport.Width + gap) / Math.Max(1, targetCardW + gap)));
            if (viewport.Width < S(scale, 620))
                columns = Math.Max(1, Math.Min(columns, (viewport.Width + gap) / Math.Max(1, S(scale, 218) + gap)));
            columns = Math.Max(1, columns);

            int cardW = Math.Max(1, (viewport.Width - gap * (columns - 1)) / columns);
            int mediaH = Math.Max(S(scale, 140), (int)Math.Round(cardW * 9 / 16.0));
            int infoH = S(scale, 62);
            int cardH = mediaH + infoH;

            int contentH = 0;
            foreach (var section in sections)
            {
                int rows = (int)Math.Ceiling(section.Items.Count / (double)columns);
                contentH += headerH + S(scale, 12) + rows * cardH + Math.Max(0, rows - 1) * rowGap + sectionGap;
            }
            contentH = Math.Max(0, contentH - sectionGap);
            if (hasMore)
                contentH += loadMoreGap + loadMoreH;

            _gridScrollMax = Math.Max(0, contentH - viewport.Height);
            _gridScroll = Math.Max(0, Math.Min(_gridScroll, _gridScrollMax));

            using Region previousClip = g.Clip.Clone();
            using Region clip = new Region(viewport);
            g.SetClip(clip, CombineMode.Intersect);

            int y = viewport.Top - _gridScroll;
            foreach (var section in sections)
            {
                var header = new Rectangle(viewport.Left, y, viewport.Width, headerH);
                if (header.Bottom >= viewport.Top && header.Top <= viewport.Bottom)
                    DrawTemporalSectionHeader(g, header, section.Title, scale);
                y += headerH + S(scale, 12);

                int rows = (int)Math.Ceiling(section.Items.Count / (double)columns);
                for (int row = 0; row < rows; row++)
                {
                    int x = viewport.Left;
                    int remainder = Math.Max(0, viewport.Width - (cardW * columns + gap * (columns - 1)));
                    for (int col = 0; col < columns; col++)
                    {
                        int index = row * columns + col;
                        if (index >= section.Items.Count)
                            break;

                        int extra = col < remainder ? 1 : 0;
                        int w = cardW + extra;
                        var card = new Rectangle(x, y, w, cardH);
                        if (card.Bottom >= viewport.Top && card.Top <= viewport.Bottom)
                            DrawTemporalCard(g, card, section.Items[index], photos, scale);
                        x += w + gap;
                        g.SetClip(clip, CombineMode.Replace);
                    }
                    y += cardH + rowGap;
                }

                y += sectionGap - rowGap;
            }

            if (hasMore)
            {
                y += loadMoreGap;
                var loadMore = new Rectangle(
                    viewport.Left,
                    y,
                    Math.Min(viewport.Width, S(scale, 392)),
                    loadMoreH);
                if (loadMore.Bottom >= viewport.Top && loadMore.Top <= viewport.Bottom)
                    DrawLoadMoreBanner(g, loadMore, totalAvailable - visible.Count, scale);
            }

            g.Clip = previousClip;
            DrawGridScrollbar(g, viewport, contentH);
        }

        private void DrawLoadMoreBanner(Graphics g, Rectangle r, int remaining, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, S(scale, 7)))
            using (var fill = new SolidBrush(hover ? ChromeHover : Chrome))
            using (var border = new Pen(hover ? Color.FromArgb(184, Accent) : Color.FromArgb(130, HUD.Theme.Border)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            int toLoad = Math.Min(PhotoPageSize, Math.Max(0, remaining));
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(8.8f, 9.8f * scale));
            using var sub = LibraryFont("Segoe UI", Math.Max(7.2f, 7.9f * scale));
            string titleText = L($"Mostra altre {toLoad}", $"Show {toLoad} more");
            string subText = L($"{remaining:N0} rimanenti", $"{remaining:N0} remaining");
            Rectangle plus = new Rectangle(r.Left + S(scale, 14), r.Top + (r.Height - S(scale, 24)) / 2, S(scale, 24), S(scale, 24));
            using (var plusPath = Round(plus, plus.Width / 2))
            using (var plusFill = new SolidBrush(Color.FromArgb(54, Accent)))
            using (var plusBorder = new Pen(Color.FromArgb(156, Accent)))
            {
                g.FillPath(plusFill, plusPath);
                g.DrawPath(plusBorder, plusPath);
            }
            using (var plusPen = new Pen(Color.White, Math.Max(1.3f, 1.5f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int cx = plus.Left + plus.Width / 2;
                int cy = plus.Top + plus.Height / 2;
                g.DrawLine(plusPen, cx - S(scale, 4), cy, cx + S(scale, 4), cy);
                g.DrawLine(plusPen, cx, cy - S(scale, 4), cx, cy + S(scale, 4));
            }
            Rectangle titleRect = new Rectangle(plus.Right + S(scale, 11), r.Top, Math.Max(1, r.Width - plus.Width - S(scale, 132)), r.Height);
            Rectangle subRect = new Rectangle(r.Right - S(scale, 116), r.Top, S(scale, 100), r.Height);
            TextRenderer.DrawText(g, titleText, title, titleRect, TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, subText, sub, subRect, Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.LoadMore, Key = "load-more" });
        }

        private void DrawTemporalSectionHeader(Graphics g, Rectangle r, string title, float scale)
        {
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(10.5f, 12.8f * scale));
            using var textBrush = new SolidBrush(TextMain);
            g.DrawString(title, font, textBrush, r.Left, r.Top + S(scale, 3));

            int lineY = r.Top + r.Height / 2;
            int textW = TextRenderer.MeasureText(title, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            using var pen = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 130 : 72, HUD.Theme.Border));
            g.DrawLine(pen, r.Left + textW + S(scale, 18), lineY, r.Right, lineY);
        }

        private void DrawTemporalCard(Graphics g, Rectangle r, LibraryItem item, bool photo, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            int infoH = Math.Max(S(scale, 56), Math.Min(S(scale, 70), (int)Math.Round(r.Height * 0.27)));
            Rectangle media = new Rectangle(r.Left, r.Top, r.Width, Math.Max(1, r.Height - infoH));
            int bleedH = Math.Max(S(scale, 50), Math.Min(S(scale, 86), (int)Math.Round(media.Height * 0.34)));
            using var path = Round(r, S(scale, 7));

            string? art = photo ? item.ArtPath : (item.WideArtPath ?? item.ArtPath);
            if (!photo && string.IsNullOrWhiteSpace(art) && IsVideoPath(item.Path))
                art = GetCachedVideoThumbnailPath(item.Path);
            string right = photo ? string.Empty : QualityLabel(item);
            string metaText = TemporalMetaText(item);

            // Come per le locandine: foto e video vengono composti una volta in una bitmap e
            // poi solo copiati. Ridisegnare sfumature, dithering e testo di ogni card a ogni
            // fotogramma rendeva lo scorrimento di Foto e Video pesante (40-60 ms a 1440p).
            string cardKey = $"t|{item.Path}|{art}|{r.Width}x{r.Height}|{item.Title}|{metaText}|{right}|{Panel.ToArgb()}|{TextMain.ToArgb()}|{Muted.ToArgb()}";
            Bitmap? cardBitmap = GetPosterCardBitmap(cardKey, r.Size, local =>
            {
                Rectangle lMedia = new Rectangle(0, 0, local.Width, media.Height);
                Rectangle lInfo = new Rectangle(0, lMedia.Bottom, local.Width, infoH);
                using var lPath = Round(new Rectangle(Point.Empty, local.Size), S(scale, 7));
                using var cg = Graphics.FromImage(local);
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                bool drawn = PaintTemporalCardBody(cg, new Rectangle(Point.Empty, local.Size), lMedia, lInfo, lPath, item, photo, art, right, metaText, bleedH, scale, gdiPlusText: true);
                ApplyRoundedAlphaMask(local, lPath);
                return drawn && IsDisplayImageExact(ResolveDisplayImagePath(art), Math.Max(lMedia.Width, lMedia.Height));
            });
            if (cardBitmap != null)
            {
                var mode = g.InterpolationMode; var offset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(cardBitmap, r.X, r.Y, r.Width, r.Height);
                g.InterpolationMode = mode; g.PixelOffsetMode = offset;
            }
            else
            {
                PaintTemporalCardBody(g, r, media, new Rectangle(r.Left, media.Bottom, r.Width, infoH), path, item, photo, art, right, metaText, bleedH, scale, gdiPlusText: false);
            }

            // Solo il bordo in hover: niente contorno grigio a riposo.
            if (hover)
                using (var border = new Pen(Accent, 2f))
                    g.DrawPath(border, path);

            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Poster, Item = item, Key = item.Path });
        }

        private bool PaintTemporalCardBody(Graphics g, Rectangle r, Rectangle media, Rectangle info, GraphicsPath path, LibraryItem item, bool photo, string? art, string right, string metaText, int bleedH, float scale, bool gdiPlusText)
        {
            using (var fill = new LinearGradientBrush(r, Color.FromArgb(232, 4, 13, 21), Color.FromArgb(214, 1, 6, 11), LinearGradientMode.Vertical))
                g.FillPath(fill, path);

            using Region previousClip = g.Clip.Clone();
            g.SetClip(path, CombineMode.Intersect);
            bool mediaDrawn = DrawMediaContainBlack(g, media, art);
            if (!mediaDrawn)
            {
                if (!photo && IsVideoPath(item.Path))
                    QueueVideoThumbnail(item.Path);
                DrawGeneratedArt(g, media, item.Title, hero: false, category: item.Category);
            }
            DrawPosterInfoBand(g, media, info, bleedH);
            g.Clip = previousClip;

            using var title = LibraryFont("Segoe UI Semibold", Math.Max(8.0f, 9.0f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(7.0f, 7.9f * scale));
            Rectangle titleRect = new Rectangle(info.Left + S(scale, 12), info.Top + S(scale, 9), info.Width - S(scale, 24), S(scale, 20));
            int rightW = string.IsNullOrWhiteSpace(right)
                ? 0
                : Math.Min(info.Width / 2, Math.Max(S(scale, 42), TextRenderer.MeasureText(right, meta, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + S(scale, 6)));
            Rectangle metaRect = new Rectangle(titleRect.Left, titleRect.Bottom + S(scale, 2), Math.Max(S(scale, 20), titleRect.Width - rightW - S(scale, 10)), S(scale, 17));
            Rectangle rightRect = new Rectangle(info.Right - rightW - S(scale, 12), metaRect.Top, rightW, metaRect.Height);
            if (gdiPlusText)
            {
                CardText(g, item.Title, title, titleRect, TextMain, StringAlignment.Near);
                CardText(g, metaText, meta, metaRect, Muted, StringAlignment.Near);
                if (rightW > 0) CardText(g, right, meta, rightRect, TextMain, StringAlignment.Far);
            }
            else
            {
                TextRenderer.DrawText(g, item.Title, title, titleRect, TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, metaText, meta, metaRect, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (rightW > 0)
                    TextRenderer.DrawText(g, right, meta, rightRect, TextMain, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            return mediaDrawn;
        }

        private void DrawFullGrid(Graphics g, Rectangle viewport, IReadOnlyList<LibraryItem> visible, float scale)
        {
            if (visible.Count == 0)
            {
                _gridScroll = 0;
                _gridScrollMax = 0;
                DrawEmptyGrid(g, viewport);
                return;
            }

            int gap = Math.Max(S(scale, 22), (int)Math.Round(viewport.Width * 0.012));
            int targetCardW = S(scale, 176);
            int columns = Math.Max(2, Math.Min(12, (viewport.Width + gap) / Math.Max(1, targetCardW + gap)));
            if (viewport.Width < S(scale, 520))
                columns = Math.Max(1, Math.Min(columns, (viewport.Width + gap) / Math.Max(1, S(scale, 150) + gap)));
            columns = Math.Max(1, columns);

            int cardW = Math.Max(1, (viewport.Width - gap * (columns - 1)) / columns);
            int cardH = Math.Max(S(scale, 230), (int)Math.Round(cardW * 1.58));
            int rows = (int)Math.Ceiling(visible.Count / (double)columns);
            int contentH = rows * cardH + Math.Max(0, rows - 1) * gap;
            _gridScrollMax = Math.Max(0, contentH - viewport.Height);
            _gridScroll = Math.Max(0, Math.Min(_gridScroll, _gridScrollMax));

            int rowStride = cardH + gap;
            int startRow = Math.Max(0, _gridScroll / Math.Max(1, rowStride));
            int endRow = Math.Min(rows - 1, (_gridScroll + viewport.Height) / Math.Max(1, rowStride) + 1);

            using Region previousClip = g.Clip.Clone();
            using Region clip = new Region(viewport);
            g.SetClip(clip, CombineMode.Intersect);

            for (int row = startRow; row <= endRow; row++)
            {
                int y = viewport.Top + row * rowStride - _gridScroll;
                int x = viewport.Left;
                int remainder = Math.Max(0, viewport.Width - (cardW * columns + gap * (columns - 1)));

                for (int col = 0; col < columns; col++)
                {
                    int index = row * columns + col;
                    if (index >= visible.Count)
                        break;

                    int extra = col < remainder ? 1 : 0;
                    int w = cardW + extra;
                    Rectangle card = new Rectangle(x, y, w, cardH);
                    if (card.Bottom >= viewport.Top && card.Top <= viewport.Bottom)
                        DrawPosterCard(g, card, visible[index], rankingPosition: IsDiaryRankingView ? index + 1 : null);
                    x += w + gap;
                    g.SetClip(clip, CombineMode.Replace);
                }
            }

            g.Clip = previousClip;
            DrawGridScrollbar(g, viewport, contentH);
        }

        private void DrawGridScrollbar(Graphics g, Rectangle viewport, int contentH)
        {
            if (_gridScrollMax <= 0 || contentH <= 0 || viewport.Height <= 0)
            {
                _gridScrollbarTrack = Rectangle.Empty;
                _gridScrollbarThumb = Rectangle.Empty;
                return;
            }

            int trackW = Math.Max(3, S(LayoutScale(viewport), 4));
            int offset = string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase)
                ? S(LayoutScale(viewport), 22)
                : S(LayoutScale(viewport), 28);
            Rectangle track = new Rectangle(viewport.Right + offset, viewport.Top, trackW, viewport.Height);
            int thumbH = ScrollbarChrome.ThumbLength(viewport.Height, viewport.Height / (double)Math.Max(viewport.Height, contentH));
            int thumbY = viewport.Top + (int)Math.Round((viewport.Height - thumbH) * (_gridScroll / (double)Math.Max(1, _gridScrollMax)));
            _gridScrollbarTrack = Rectangle.Inflate(track, 8, 0);
            _gridScrollbarThumb = new Rectangle(_gridScrollbarTrack.Left, thumbY, _gridScrollbarTrack.Width, thumbH);
            ScrollbarChrome.Draw(g, track, thumbY, thumbH);
        }

        private void DrawKeyboardFocus(Graphics g)
        {
            HitZone? hit = FindKeyboardFocusedHit();
            if (hit == null)
                return;
            Rectangle focus = Rectangle.Inflate(hit.Bounds, 2, 2);
            focus = Rectangle.Intersect(ClientRectangle, focus);
            if (_view == PageView.Collection && !_gridViewport.IsEmpty)
                focus = Rectangle.Intersect(_gridViewport, focus);
            if (focus.Width < 4 || focus.Height < 4)
                return;
            using var path = Round(new Rectangle(focus.Left, focus.Top, focus.Width - 1, focus.Height - 1), 8);
            using var glow = new Pen(Color.FromArgb(70, Accent), 5f);
            using var edge = new Pen(Color.FromArgb(238, Accent), 2f);
            g.DrawPath(glow, path);
            g.DrawPath(edge, path);
        }

        private void DrawLogo(Graphics g, Rectangle r)
        {
            Image? assetLogo = HUD.SettingsHudPage.LoadBrandLogoForTheme() ?? LoadFirstLogoImage();
            if (assetLogo != null)
            {
                Rectangle dest = ContainDestinationLeft(assetLogo.Size, r);
                g.DrawImage(assetLogo, dest);
                return;
            }

            int iconSize = Math.Clamp(r.Height - 18, 24, 30);
            Rectangle icon = new Rectangle(r.Left, r.Top + (r.Height - iconSize) / 2, iconSize, iconSize);
            DrawIcon(g, icon, "cinema", Accent);
            int textLeft = icon.Right + 10;
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(8.6f, 10.6f * Math.Min(1f, r.Height / 46f)));
            using var subFont = LibraryFont("Segoe UI", Math.Max(6.8f, 7.5f * Math.Min(1f, r.Height / 46f)));
            Rectangle title = new Rectangle(textLeft, r.Top + 3, Math.Max(1, r.Right - textLeft), Math.Max(18, r.Height / 2));
            Rectangle sub = new Rectangle(title.Left, title.Bottom - 1, title.Width, Math.Max(14, r.Bottom - title.Bottom));
            TextRenderer.DrawText(g, "CINECORE", titleFont, title, TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "PLAYER 2025", subFont, sub, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private Image? LoadFirstLogoImage()
        {
            foreach (string path in CandidateLogoPaths())
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    Image? img = LoadImage(path);
                    if (img != null)
                        return img;
                }
                catch { }
            }

            return null;
        }

        private static Rectangle ContainDestination(Size source, Rectangle bounds)
        {
            if (source.Width <= 0 || source.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
                return bounds;

            float scale = Math.Min(bounds.Width / (float)source.Width, bounds.Height / (float)source.Height);
            int w = Math.Max(1, (int)Math.Round(source.Width * scale));
            int h = Math.Max(1, (int)Math.Round(source.Height * scale));
            return new Rectangle(bounds.Left + (bounds.Width - w) / 2, bounds.Top + (bounds.Height - h) / 2, w, h);
        }

        private static Rectangle ContainDestinationLeft(Size source, Rectangle bounds)
        {
            Rectangle contained = ContainDestination(source, bounds);
            contained.X = bounds.Left;
            return contained;
        }

        private static IEnumerable<string> CandidateLogoPaths()
        {
            string baseDir = AppContext.BaseDirectory;
            string assets = Path.Combine(baseDir, "Assets");
            string icons = Path.Combine(assets, "Icons");
            foreach (string name in new[]
            {
                "cinecore-logo.png",
                "cinecore-logo-full.png",
                "cinecore-player-logo.png",
                "brand-logo.png",
                "logo.png",
                "Logo.png",
                "app-logo.png"
            })
            {
                yield return Path.Combine(assets, name);
                yield return Path.Combine(icons, name);
                yield return Path.Combine(baseDir, name);
            }
        }

        private static float SidebarScale(Rectangle sidebar)
        {
            if (sidebar.Width <= 0)
                return 1f;

            // La sidebar ha una scala propria, leggermente piÃ¹ generosa del main,
            // cosÃ¬ voci, padding e logo non sembrano miniaturizzati.
            float widthScale = sidebar.Width / 242f;
            float heightScale = sidebar.Height / 1080f;
            return Math.Clamp(Math.Min(widthScale, heightScale), 0.45f, 4f);
        }

        private void DrawSidebarProfile(Graphics g, Rectangle sidebar)
        {
            if (sidebar.Height < 700)
                return;

            Rectangle row = new Rectangle(sidebar.Left + 24, sidebar.Bottom - 88, sidebar.Width - 48, 56);
            using (var rule = new Pen(Color.FromArgb(38, 255, 255, 255)))
                g.DrawLine(rule, sidebar.Left + 28, row.Top - 18, sidebar.Right - 28, row.Top - 18);

            Rectangle avatar = new Rectangle(row.Left, row.Top + 8, 40, 40);
            using (var path = Round(avatar, 20))
            using (var fill = new LinearGradientBrush(avatar, Color.FromArgb(28, 49, 70), Color.FromArgb(15, 28, 42), LinearGradientMode.ForwardDiagonal))
            using (var border = new Pen(Color.FromArgb(60, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                { } // senza contorno
            }

            using var avatarFont = LibraryFont("Segoe UI Semibold", 10.5f);
            TextRenderer.DrawText(g, "CP", avatarFont, avatar, Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            using var nameFont = LibraryFont("Segoe UI", 9.2f);
            using var premiumFont = LibraryFont("Segoe UI Semibold", 8.4f);
            Rectangle name = new Rectangle(avatar.Right + 14, row.Top + 9, row.Right - avatar.Right - 14, 20);
            Rectangle premium = new Rectangle(name.Left, name.Bottom - 1, name.Width, 18);
            TextRenderer.DrawText(g, "Cinecore Player", nameFont, name, Color.FromArgb(218, 226, 234), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Premium", premiumFont, premium, Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawSectionLabel(Graphics g, Rectangle sidebar, string text, ref int y)
        {
            float scale = SidebarScale(sidebar);
            using var font = LibraryFont("Segoe UI Semibold", 8.2f * scale);
            TextRenderer.DrawText(g, text, font, new Rectangle(sidebar.Left + S(scale, 28), y, sidebar.Width - S(scale, 56), S(scale, 18)),
                Color.FromArgb(HUD.Theme.IsLight ? 200 : 170, Muted), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            y += S(scale, 26);
        }

        private void DrawRule(Graphics g, Rectangle sidebar, int y)
        {
            float scale = SidebarScale(sidebar);
            using var pen = new Pen(Color.FromArgb(76, HUD.Theme.Border));
            g.DrawLine(pen, sidebar.Left + S(scale, 30), y, sidebar.Right - S(scale, 30), y);
        }

        private void DrawThemeModeSwitch(Graphics g, Rectangle sidebar, float scale)
        {
            if (sidebar.Height < S(scale, 610))
                return;

            int h = S(scale, 34);
            int gap = S(scale, 5);
            int w = S(scale, 34);
            int groupW = w * 2 + gap;
            int x = sidebar.Left + (sidebar.Width - groupW) / 2;
            int y = sidebar.Bottom - MusicTransportInset - h - S(scale, 18);
            DrawThemeModeButton(g, new Rectangle(x, y, w, h), false);
            DrawThemeModeButton(g, new Rectangle(x + w + gap, y, w, h), true);
        }

        private void DrawThemeModeButton(Graphics g, Rectangle rect, bool light)
        {
            bool selected = HUD.Theme.IsLight == light;
            bool hover = rect.Contains(_lastMouse);
            int cx = rect.Left + rect.Width / 2;
            int cy = rect.Top + rect.Height / 2;
            Color glyphColor = selected ? Accent : (hover ? HUD.Theme.SubtleText : Muted);
            var glyphBounds = new Rectangle(cx - S(1f, 10), cy - S(1f, 10), S(1f, 20), S(1f, 20));
            if (DrawIcon(g, glyphBounds, light ? "sun" : "moon", glyphColor))
            {
                _hits.Add(new HitZone { Bounds = rect, Kind = HitKind.Nav, Key = light ? "ThemeLight" : "ThemeDark" });
                return;
            }
            using var glyph = new Pen(glyphColor, selected ? 1.8f : 1.4f);
            if (light)
            {
                g.DrawEllipse(glyph, cx - 4, cy - 4, 8, 8);
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI * i / 4d;
                    g.DrawLine(glyph,
                        cx + (int)Math.Round(Math.Cos(a) * 7), cy + (int)Math.Round(Math.Sin(a) * 7),
                        cx + (int)Math.Round(Math.Cos(a) * 9), cy + (int)Math.Round(Math.Sin(a) * 9));
                }
            }
            else
            {
                g.DrawArc(glyph, cx - 6, cy - 7, 13, 14, 72, 216);
                g.DrawArc(glyph, cx - 2, cy - 7, 9, 14, 92, 178);
            }
            _hits.Add(new HitZone { Bounds = rect, Kind = HitKind.Nav, Key = light ? "ThemeLight" : "ThemeDark" });
        }

        // Voce della sidebar: pillola morbida senza bordo, barretta d'accento sulla voce
        // attiva, hover come velatura. Le sorgenti (Computer/Rete) usano un puntino.
        private void DrawNav(Graphics g, Rectangle sidebar, string key, string label, string icon, ref int y, bool? selected = null, bool sourceMarker = false)
        {
            float scale = SidebarScale(sidebar);
            bool isSelected = selected ?? string.Equals(_category, key, StringComparison.OrdinalIgnoreCase);
            bool pageSelected = isSelected && !sourceMarker;
            // Computer / Rete: la sorgente in uso si riconosce sempre (icona nel colore d'accento,
            // nome a tinta piena e un punto a destra), anche quando la pagina aperta e' un'altra.
            bool sourceActive = isSelected && sourceMarker;
            Rectangle row = new Rectangle(sidebar.Left + S(scale, 14), y, sidebar.Width - S(scale, 28), S(scale, 38));
            bool hover = row.Contains(_lastMouse);
            if (pageSelected || hover)
                HUD.Theme.DrawHighlight(g, row, pageSelected, S(scale, 3));

            bool hasIcon = HasIconAsset(icon);
            int iconSize = S(scale, 20);
            Color iconInk = pageSelected || sourceActive ? Accent : hover ? TextMain : HUD.Theme.SubtleText;
            if (hasIcon)
                DrawIcon(g, new Rectangle(row.Left + S(scale, 11), row.Top + (row.Height - iconSize) / 2, iconSize, iconSize), icon, iconInk);

            using var font = LibraryFont("Segoe UI", 10.2f * scale);
            int textLeft = hasIcon ? row.Left + S(scale, 42) : row.Left + S(scale, 14);
            int textWidth = row.Right - textLeft - S(scale, 20);
            TextRenderer.DrawText(g, label, font, new Rectangle(textLeft, row.Top, textWidth, row.Height),
                pageSelected || sourceActive || hover ? TextMain : HUD.Theme.SubtleText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (sourceActive)
            {
                int dot = S(scale, 6);
                using var mark = new SolidBrush(Accent);
                g.FillEllipse(mark, row.Right - S(scale, 14) - dot, row.Top + (row.Height - dot) / 2, dot, dot);
            }

            _hits.Add(new HitZone { Bounds = row, Kind = HitKind.Nav, Key = key });
            y += S(scale, 42);
        }

        private void DrawHeader(Graphics g, Rectangle content, float scale)
        {
            bool compact = content.Width < S(scale, 980);
            int buttonGap = compact ? S(scale, 6) : S(scale, 12);
            int headerH = S(scale, 34);
            bool playlists = string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase);
            bool favourites = string.Equals(_category, "Favourites", StringComparison.OrdinalIgnoreCase);
            bool diary = string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase);
            bool networkLibrary = string.Equals(_source, "Network", StringComparison.OrdinalIgnoreCase) && _view != PageView.Network;
            int filterW = compact ? S(scale, 48) : S(scale, 112);
            int sortW = compact ? S(scale, 48) : S(scale, 104);
            int scanW = compact ? S(scale, 48) : S(scale, 124);
            int addW = compact ? S(scale, 48) : (playlists ? S(scale, 138) : S(scale, 126));
            int serverW = compact ? S(scale, 48) : S(scale, 96);
            int diaryRateW = compact ? S(scale, 48) : S(scale, 94);
            int diaryRankW = compact ? S(scale, 48) : S(scale, 112);
            int controlsGap = compact ? S(scale, 8) : S(scale, 20);
            // Scansione e cartelle riguardano solo la libreria locale.
            bool localSources = !playlists && !favourites && !diary && !networkLibrary;
            int buttonsW = (playlists
                ? filterW + sortW + addW + buttonGap * 2
                : (favourites || (networkLibrary && !diary) ? filterW + sortW + buttonGap : diary
                    ? filterW + sortW + diaryRateW + diaryRankW + buttonGap * 3
                    : filterW + sortW + scanW + addW + buttonGap * 3))
                + (networkLibrary ? serverW + buttonGap : 0);
            int maxSearchW = Math.Max(S(scale, 72), content.Width - buttonsW - controlsGap);
            int searchW = compact
                ? maxSearchW
                : Math.Min(S(scale, 700), Math.Max(S(scale, 280), (int)Math.Round(content.Width * 0.38)));
            searchW = Math.Min(searchW, maxSearchW);
            int searchX = compact ? content.Left : content.Left + Math.Max(0, (content.Width - buttonsW - searchW - controlsGap) / 2);
            _searchShellRect = new Rectangle(searchX, content.Top + S(scale, 1), searchW, headerH);

            try
            {
                _searchBox.BackColor = Chrome;
                _searchBox.ForeColor = TextMain;
                float searchFontSize = Math.Max(8.2f, Math.Min(30.0f, 9.0f * scale)); // nessun tetto a 12: a 4K il testo restava minuscolo nel campo
                if (Math.Abs(_searchBox.Font.Size - global::CinecorePlayer2025.AppFonts.EffectiveSize("Segoe UI", searchFontSize * (96f / 72f))) > 0.1f)
                    global::CinecorePlayer2025.AppFonts.ReplaceFont(_searchBox, LibraryFont("Segoe UI", searchFontSize));
            }
            catch { }

            using (var path = Round(_searchShellRect, S(scale, 7)))
            using (var fill = new SolidBrush(Chrome))
            using (var border = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 150 : 82, HUD.Theme.Border)))
            {
                g.FillPath(fill, path);
                { } // senza contorno
            }
            DrawIcon(g, new Rectangle(_searchShellRect.Left + S(scale, 13), _searchShellRect.Top + (_searchShellRect.Height - S(scale, 15)) / 2, S(scale, 15), S(scale, 15)), "search", Muted);
            DrawSearchShellText(g);

            int x = _searchShellRect.Right + controlsGap;
            if (networkLibrary)
            {
                DrawHeaderButton(g, new Rectangle(x, content.Top + S(scale, 1), serverW, headerH), "Server", "network", "network-servers");
                x += serverW + buttonGap;
            }
            DrawHeaderButton(g, new Rectangle(x, content.Top + S(scale, 1), filterW, headerH), ActiveFilterLabel(), "filter", "filter");
            x += filterW + buttonGap;
            DrawHeaderButton(g, new Rectangle(x, content.Top + S(scale, 1), sortW, headerH), ActiveSortLabel(), "sort", "sort");
            x += sortW + buttonGap;
            if (diary)
            {
                DrawHeaderButton(g, new Rectangle(x, content.Top + S(scale, 1), diaryRateW, headerH),
                    _diaryRatingMode ? L("Scegli", "Choose") : L("Valuta", "Rate"), "star", "diary-rate");
                x += diaryRateW + buttonGap;
                DrawHeaderButton(g, new Rectangle(x, content.Top + S(scale, 1), diaryRankW, headerH),
                    string.Equals(_filter, "TopRated", StringComparison.OrdinalIgnoreCase)
                        ? L("Tutto il diario", "Full diary")
                        : L("Classifica", "Ranking"),
                    "list", "diary-ranking");
            }
            else if (localSources)
            {
                DrawHeaderButton(g, new Rectangle(x, content.Top + S(scale, 1), scanW, headerH), L("Scansiona", "Scan Library"), "scan", "scan");
                x += scanW + buttonGap;
                DrawHeaderButton(g, new Rectangle(x, content.Top + S(scale, 1), addW, headerH), L("Aggiungi", "Add Source"), "add", "add");
            }
            else if (playlists)
            {
                DrawHeaderButton(g, new Rectangle(x, content.Top + S(scale, 1), addW, headerH), L("Nuova playlist", "New Playlist"), "add", "new-playlist");
            }
        }

        private void DrawHeaderButton(Graphics g, Rectangle r, string text, string icon, string key)
        {
            float scale = Math.Max(0.74f, Math.Min(1.90f, r.Height / 44f));
            bool hover = r.Contains(_lastMouse);
            bool active = (key == "diary-ranking" && string.Equals(_filter, "TopRated", StringComparison.OrdinalIgnoreCase)) ||
                          (key == "diary-rate" && _diaryRatingMode);
            using var fill = new SolidBrush(active
                ? Color.FromArgb(HUD.Theme.IsLight ? 34 : 40, Accent)
                : hover ? ChromeHover : Chrome);
            using (var path = Round(r, S(scale, 7)))
            using (var border = new Pen(active || hover
                ? Color.FromArgb(active ? 224 : 190, Accent)
                : Color.FromArgb(HUD.Theme.IsLight ? 175 : 82, HUD.Theme.Border)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            using var font = LibraryFont("Segoe UI Semibold", Math.Max(7.2f, 9.6f * scale));
            Size textSize = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            bool hasIcon = HasIconAsset(icon);
            int iconSize = S(scale, 14);
            int gap = hasIcon ? S(scale, 6) : 0;
            // Pulsante troppo stretto per icona e testo interi (finestra piccola): solo l'icona,
            // invece di una parola troncata tipo "Scansi...".
            if (hasIcon && textSize.Width + iconSize + gap + S(scale, 16) > r.Width)
            {
                DrawIcon(g, new Rectangle(r.Left + (r.Width - iconSize) / 2, r.Top + (r.Height - iconSize) / 2, iconSize, iconSize), icon, TextMain);
                _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Header, Key = key });
                return;
            }
            int totalW = Math.Min(r.Width - S(scale, 16), textSize.Width + (hasIcon ? iconSize + gap : 0));
            int x = r.Left + (r.Width - totalW) / 2;
            if (hasIcon)
            {
                DrawIcon(g, new Rectangle(x, r.Top + (r.Height - iconSize) / 2, iconSize, iconSize), icon, TextMain);
                x += iconSize + gap;
            }
            TextRenderer.DrawText(g, text, font, new Rectangle(x, r.Top, Math.Max(S(scale, 20), r.Right - x - S(scale, 8)), r.Height), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Header, Key = key });
        }

        private void DrawHero(Graphics g, Rectangle r)
        {
            // Nessun minimo alto: a finestra piccola anche testi e pulsanti dell'hero si riducono.
            float scale = Math.Max(0.66f, Math.Min(1.90f, r.Height / 342f));
            Color heroBack = Back;
            g.SetClip(r);

            // La foto non parte piÃ¹ dal bordo sinistro del riquadrone.
            // Parte appena prima della zona in cui la vignettatura comincia ad aprirsi:
            // resta sotto la sfumatura, ma il rettangolo utile Ã¨ meno panoramico
            // e quindi il crop verticale Ã¨ meno aggressivo.
            using (var baseFill = new SolidBrush(heroBack))
                g.FillRectangle(baseFill, r);

            bool hasBackdrop = _heroItem != null && DrawWideArt(g, r, _heroItem, hero: true);
            if (!hasBackdrop)
                DrawGeneratedArt(g, r, _heroItem?.Title ?? "Cinecore", hero: true, category: _heroItem?.Category);
            DrawHeroImageOverlays(g, r);

            Rectangle textShade = new Rectangle(r.Left, r.Top, Math.Max(1, (int)Math.Round(r.Width * 0.64)), r.Height);
            using (var shade = new LinearGradientBrush(textShade, Color.FromArgb(236, heroBack), Color.FromArgb(0, heroBack), LinearGradientMode.Horizontal))
            {
                shade.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.28f, 0.54f, 0.78f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(248, heroBack),
                        Color.FromArgb(232, heroBack),
                        Color.FromArgb(150, heroBack),
                        Color.FromArgb(46, heroBack),
                        Color.FromArgb(0, heroBack)
                    }
                };
                g.FillRectangle(shade, textShade);
            }

            g.ResetClip();


            if (_heroItem == null)
            {
                DrawHeroEmpty(g, r);
                return;
            }

            // Il bordo sinistro dell'hero sfuma nello sfondo: il testo parte sullo stesso filo
            // di "Continua a guardare" e delle card, così l'hero risulta simmetrico
            // rispetto al bordo destro dell'immagine.
            int left = r.Left;
            int textW = Math.Min(S(scale, 640), Math.Max(S(scale, 300), (int)Math.Round(r.Width * 0.43)));
            textW = Math.Min(textW, Math.Max(S(scale, 220), r.Right - left - S(scale, 28)));
            int actionH = S(scale, 42);
            int actionTop = r.Bottom - S(scale, 21) - actionH;
            using var eyebrowFont = LibraryFont("Segoe UI Semibold", Math.Max(7.6f, 8.2f * scale));
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(17f, Math.Min(39f, 29f * scale)));
            int titleLineH = titleFont.Height + S(scale, 2);
            bool titleWraps = TextRenderer.MeasureText(g, _heroItem.Title, titleFont, new Size(8192, titleLineH), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width > textW;
            int titleH = titleWraps ? titleLineH * 2 : titleLineH;
            int contentEstimate = S(scale, 16 + 5 + 12 + 25 + 11 + 52 + 24) + titleH;
            int top = Math.Max(r.Top + S(scale, 17), r.Top + (actionTop - r.Top - contentEstimate) / 2);
            // Finestra bassa: l'hero si accorcia ma titolo e pulsanti no, e la trama spariva del
            // tutto. Prima si rinuncia all'etichetta e si stringono gli spazi, poi la trama resta
            // almeno su una riga.
            using var bodyMeasure = LibraryFont("Segoe UI", Math.Max(9.3f, 10.4f * scale));
            int fullBodyRoom = actionTop - S(scale, 13) - (top + S(scale, 21) + titleH + S(scale, 12) + S(scale, 36));
            bool compactHero = fullBodyRoom < bodyMeasure.Height * 2;
            int chipGap = compactHero ? S(scale, 8) : S(scale, 12);
            int bodyGap = compactHero ? S(scale, 31) : S(scale, 36);
            if (compactHero)
            {
                int needed = titleH + chipGap + bodyGap + bodyMeasure.Height * 2;
                top = Math.Max(r.Top + S(scale, 12), Math.Min(top, actionTop - S(scale, 10) - needed));
            }
            else
                TextRenderer.DrawText(g, L("IN PRIMO PIANO", "FEATURED"), eyebrowFont,
                    new Rectangle(left, top, textW, S(scale, 16)),
                    Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int titleTop = compactHero ? top : top + S(scale, 21);
            Rectangle titleRect = new Rectangle(left, titleTop, textW, titleH);
            string fittedTitle = FitTextWithEllipsis(_heroItem.Title, titleFont, titleRect.Size,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, fittedTitle, titleFont, titleRect, TextMain,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int chipY = titleRect.Bottom + chipGap;
            int chipX = left;
            using var chipMeasureFont = LibraryFont("Segoe UI", Math.Max(8.2f, 8.7f * scale));
            foreach (string chip in HeroChips(_heroItem))
            {
                int w = Math.Min(S(scale, 150), Math.Max(S(scale, 56), TextRenderer.MeasureText(chip, chipMeasureFont).Width + S(scale, 22)));
                if (chipX + w > left + textW) break;
                DrawPill(g, new Rectangle(chipX, chipY, w, S(scale, 25)), chip, IsQualityChip(chip) ? Accent : Muted);
                chipX += w + S(scale, 10);
            }

            string heroText = HeroText(_heroItem);
            string heroCast = string.Join(", ", _heroItem.CastMembers.Select(member => member.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Take(4));
            int castHeight = string.IsNullOrWhiteSpace(heroCast) || r.Height < S(scale, 270) || compactHero ? 0 : S(scale, 20);
            int bodyTop = chipY + bodyGap;
            int bodyBottom = actionTop - S(scale, 13) - (castHeight > 0 ? castHeight + S(scale, 5) : 0);
            int bodyContentBottom = bodyTop;
            using (var bodyFont = LibraryFont("Segoe UI", Math.Max(9.3f, 10.4f * scale)))
            {
                int bodyHeight = Math.Max(0, Math.Min(S(scale, 66), bodyBottom - bodyTop));
                Rectangle bodyRect = new Rectangle(left, bodyTop, textW, bodyHeight);
                if (bodyHeight >= bodyFont.Height)
                {
                    string fitted = FitTextWithEllipsis(heroText, bodyFont, bodyRect.Size, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, fitted, bodyFont, bodyRect, TextMain, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    bodyContentBottom = bodyTop + Math.Min(bodyHeight, TextRenderer.MeasureText(g, fitted, bodyFont,
                        new Size(textW, bodyHeight), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
                }
            }

            if (castHeight > 0)
            {
                using var castFont = LibraryFont("Segoe UI", Math.Max(8.2f, 9f * scale));
                TextRenderer.DrawText(g, "Cast: " + heroCast, castFont, new Rectangle(left, Math.Min(bodyBottom, bodyContentBottom) + S(scale, 5), textW, castHeight),
                    Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            ResumeItem? resume = _resumeItems.FirstOrDefault(x => string.Equals(x.Item.Path, _heroItem.Path, StringComparison.OrdinalIgnoreCase));
            bool hasResume = resume != null && resume.PositionSeconds > 0;
            Rectangle resumeButton = new Rectangle(left, actionTop, S(scale, 148), actionH);
            Rectangle detailsButton = new Rectangle(resumeButton.Right + S(scale, 12), actionTop, S(scale, 126), actionH);
            DrawAction(g, resumeButton, hasResume ? L("Riprendi", "Resume") : L("Riproduci", "Play"), "play", true, HitKind.HeroResume, _heroItem);
            if (!hasResume) DrawAction(g, detailsButton, L("Dettagli", "Details"), string.Empty, false, HitKind.HeroDetails, _heroItem);
            if (hasResume)
            {
                var restart = new Rectangle(detailsButton.Left, detailsButton.Top, S(scale, 196), detailsButton.Height);
                DrawAction(g, restart, L("Ricomincia dall’inizio", "Start over"), "", false, HitKind.HeroRestart, _heroItem);
                detailsButton.X = restart.Right + S(scale, 12);
                DrawAction(g, detailsButton, L("Dettagli", "Details"), "", false, HitKind.HeroDetails, _heroItem);
            }
            // Accanto a Dettagli: accesso diretto a cast e recensioni.
            if (HeroSupportsCast(_heroItem))
            {
                var more = new Rectangle(detailsButton.Right + S(scale, 12), actionTop, actionH, actionH);
                DrawAction(g, more, string.Empty, string.Empty, false, HitKind.HeroMore, _heroItem);
                var glyph = Rectangle.Inflate(more, -(more.Width - S(scale, 20)) / 2, -(more.Height - S(scale, 20)) / 2);
                HUD.MusicTransportBar.DrawSymbol(g, glyph, "more", HUD.Theme.IsLight ? TextMain : Color.White);
            }
        }

        private void DrawHeroImageOverlays(Graphics g, Rectangle r)
        {
            Color heroBack = Back;
            if (HUD.Theme.IsLight && !_darkSurfaceText)
            {
                // In chiaro: il testo sta a sinistra, quindi basta una sfumatura orizzontale
                // morbida; niente velo bianco sopra e sotto, che appiattiva tutta la foto.
                int fadeW = Math.Max(1, (int)Math.Round(r.Width * 0.62));
                Rectangle side = new Rectangle(r.Left, r.Top, fadeW, r.Height);
                using (var horizontal = new LinearGradientBrush(Rectangle.Inflate(side, 1, 0), heroBack, Color.Transparent, LinearGradientMode.Horizontal))
                {
                    horizontal.InterpolationColors = EasedBlend(heroBack, 255, 0, 0.85f);
                    g.FillRectangle(horizontal, side);
                }
                int bottomH = Math.Max(1, r.Height / 4);
                Rectangle bottom = new Rectangle(r.Left, r.Bottom - bottomH, r.Width, bottomH);
                using (var vertical = new LinearGradientBrush(Rectangle.Inflate(bottom, 0, 1), Color.Transparent, heroBack, LinearGradientMode.Vertical))
                {
                    vertical.InterpolationColors = EasedBlend(heroBack, 0, 150, 1.3f);
                    g.FillRectangle(vertical, bottom);
                }
                VisualDither.Overlay(g, r, 4);
                return;
            }
            // Vignettatura piena sul banner: la foto parte sotto questa zona scura,
            // quindi l'inizio dell'immagine non si vede come uno stacco netto.
            using (var horizontal = new LinearGradientBrush(r, heroBack, Color.Transparent, LinearGradientMode.Horizontal))
            {
                horizontal.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.18f, 0.34f, 0.52f, 0.74f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(255, heroBack),
                        Color.FromArgb(250, heroBack),
                        Color.FromArgb(218, heroBack),
                        Color.FromArgb(122, heroBack),
                        Color.FromArgb(34, heroBack),
                        Color.FromArgb(8, heroBack)
                    }
                };
                g.FillRectangle(horizontal, r);
            }

            using (var vertical = new LinearGradientBrush(r, Color.Transparent, heroBack, LinearGradientMode.Vertical))
            {
                vertical.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.38f, 0.72f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(70, heroBack),
                        Color.FromArgb(6, heroBack),
                        Color.FromArgb(62, heroBack),
                        Color.FromArgb(194, heroBack)
                    }
                };
                g.FillRectangle(vertical, r);
            }

            Rectangle topRect = new Rectangle(r.Left, r.Top, r.Width, Math.Max(1, r.Height / 4));
            using (var top = new LinearGradientBrush(topRect, Color.FromArgb(70, heroBack), Color.FromArgb(0, heroBack), LinearGradientMode.Vertical))
                g.FillRectangle(top, topRect);
            VisualDither.Overlay(g, r, HUD.Theme.IsLight ? 2 : 3);
        }

        private void DrawHeroEmpty(Graphics g, Rectangle r)
        {
            int left = r.Left + Math.Max(42, (int)Math.Round(r.Width * 0.07));
            int top = r.Top + Math.Max(34, (int)Math.Round(r.Height * 0.13));
            using var title = LibraryFont("Segoe UI Semibold", 22f);
            using var sub = LibraryFont("Segoe UI", 10f);
            TextRenderer.DrawText(g, L("Libreria Cinecore", "Cinecore Library"), title, new Rectangle(left, top, 520, 42), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, L("Aggiungi una sorgente o avvia una scansione per popolare questa categoria.", "Add a source or start a scan to populate this category."), sub, new Rectangle(left, top + 52, 560, 50), Muted, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            DrawHeaderButton(g, new Rectangle(left, r.Bottom - 58, 132, 38), L("Aggiungi", "Add Source"), "add", "add");
        }

        private void DrawAction(Graphics g, Rectangle r, string text, string icon, bool primary, HitKind kind, LibraryItem item)
        {
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 42f));
            bool hover = r.Contains(_lastMouse);
            bool light = HUD.Theme.IsLight && !_darkSurfaceText;
            // Tema chiaro: principale pieno d'accento, secondari su superficie chiara;
            // i riempimenti scuri traslucidi del tema scuro diventavano grigi e illeggibili.
            Color fillColor = light
                ? (primary ? (hover ? ControlPaint.Dark(Accent, .05f) : Accent) : (hover ? HUD.Theme.Nav : HUD.Theme.Card))
                : primary ? (hover ? Color.FromArgb(110, 0, 93, 150) : Color.FromArgb(86, 0, 72, 116)) : (hover ? Color.FromArgb(186, 27, 39, 52) : Color.FromArgb(152, 17, 26, 36));
            Color borderColor = light ? (primary ? Accent : Color.FromArgb(120, HUD.Theme.Border)) : primary ? Accent : Color.FromArgb(62, 54, 75, 90);
            Color ink = light && !primary ? TextMain : Color.White;
            using (var path = Round(r, S(scale, 7)))
            using (var fill = new SolidBrush(fillColor))
            using (var border = new Pen(borderColor))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8.4f, 9.3f * scale));
            Size textSize = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            bool hasIcon = HasIconAsset(icon);
            int iconSize = S(scale, 18);
            int gap = hasIcon ? S(scale, 9) : 0;
            int totalW = Math.Min(r.Width - S(scale, 16), textSize.Width + (hasIcon ? iconSize + gap : 0));
            int x = r.Left + (r.Width - totalW) / 2;
            if (hasIcon)
            {
                DrawIcon(g, new Rectangle(x, r.Top + (r.Height - iconSize) / 2, iconSize, iconSize), icon, primary ? (light ? Color.White : Color.FromArgb(70, 202, 255)) : ink);
                x += iconSize + gap;
            }
            int textW = Math.Max(S(scale, 20), Math.Min(textSize.Width + 2, r.Right - x - S(scale, 8)));
            TextRenderer.DrawText(g, text, font, new Rectangle(x, r.Top, textW, r.Height), ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = kind, Item = item, Key = item.Path });
        }

        private static Font LibraryFont(string family, float size, FontStyle style = FontStyle.Regular)
            => global::CinecorePlayer2025.AppFonts.Create(family, size * (96f / 72f), style, GraphicsUnit.Pixel);

        private void DrawTitle(Graphics g, string text, Rectangle r, float size)
        {
            using var font = LibraryFont("Segoe UI Semibold", size);
            TextRenderer.DrawText(g, text, font, r, TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawSeeAll(Graphics g, Rectangle r)
        {
            // Pillola alta 28 a scala 1: il testo la segue (a 4K restava di 11 pixel in un riquadro di 60).
            using var font = LibraryFont("Segoe UI Semibold", 8.3f * Math.Max(1f, r.Height / 28f));
            // La pillola si allarga verso sinistra quanto serve: mai "Vedi t...".
            int needW = TextRenderer.MeasureText(L("Vedi tutto", "See all"), font, new Size(int.MaxValue, r.Height), TextFormatFlags.NoPadding).Width + 22;
            if (needW > r.Width) r = new Rectangle(r.Right - needW, r.Top, needW, r.Height);
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, 7))
            using (var fill = new SolidBrush(hover ? Color.FromArgb(42, 13, 75, 112) : Color.FromArgb(22, 9, 32, 48)))
            using (var border = new Pen(hover ? Accent : Color.FromArgb(56, 44, 67, 82)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            TextRenderer.DrawText(g, L("Vedi tutto", "See all"), font, r, TextMain, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.SeeAll, Key = "movies" });
        }

        private void DrawResumeRow(Graphics g, Rectangle r)
        {
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 144f));
            if (_resumeItems.Count == 0)
            {
                using var font = LibraryFont("Segoe UI", Math.Max(8.4f, 9f * scale));
                TextRenderer.DrawText(g, L("Nessun elemento da riprendere in questa categoria.", "Nothing to continue in this category."), font, r, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                return;
            }

            int arrow = S(scale, 46);
            int gap = Math.Max(S(scale, 18), (int)Math.Round(r.Width * 0.012));
            int sidePad = S(scale, 54);
            int desiredCardW = S(scale, 216);
            int maxCards = Math.Max(1, (r.Width - sidePad * 2 + gap) / Math.Max(1, desiredCardW + gap));
            int cardW = Math.Max(1, (r.Width - sidePad * 2 - gap * Math.Max(0, maxCards - 1)) / maxCards);
            int start = Math.Min(_resumeOffset, Math.Max(0, _resumeItems.Count - maxCards));
            int x = r.Left + (r.Width - (maxCards * cardW + (maxCards - 1) * gap)) / 2;

            DrawArrow(g, new Rectangle(r.Left - S(scale, 8), r.Top + (r.Height - arrow) / 2, arrow, arrow), true, start > 0);
            DrawArrow(g, new Rectangle(r.Right - arrow + S(scale, 8), r.Top + (r.Height - arrow) / 2, arrow, arrow), false, start + maxCards < _resumeItems.Count);

            for (int i = 0; i < maxCards && start + i < _resumeItems.Count; i++)
            {
                var item = _resumeItems[start + i];
                Rectangle card = new Rectangle(x, r.Top, cardW, r.Height);
                DrawResumeCard(g, card, item);
                x += cardW + gap;
            }
            int pages = (_resumeItems.Count + maxCards - 1) / maxCards;
            if (pages > 1)
            {
                int dotGap = S(scale, 18), dotsX = r.Left + (r.Width - pages * dotGap) / 2;
                for (int page = 0; page < pages; page++)
                {
                    var dot = new Rectangle(dotsX + page * dotGap, r.Bottom + S(scale, 20), S(scale, 7), S(scale, 7));
                    bool active = page == Math.Min(pages - 1, (_resumeOffset + (start + maxCards >= _resumeItems.Count ? maxCards - 1 : 0)) / maxCards);
                    using var fill = new SolidBrush(active ? Accent : Color.FromArgb(63, 80, 98)); g.FillEllipse(fill, dot);
                    _hits.Add(new HitZone { Bounds = Rectangle.Inflate(dot, 5, 5), Kind = HitKind.HomeResumePage, Index = Math.Min(page * maxCards, Math.Max(0, _resumeItems.Count - maxCards)) });
                }
            }
        }

        // Il clip di GDI+ non ha antialias: dopo un ritaglio arrotondato si ripassa il bordo
        // con un tratto sottile del colore esterno, che copre gli scalini.
        private static void SmoothClipEdge(Graphics g, GraphicsPath path, Color outside)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(outside, 2f);
            g.DrawPath(pen, path);
            g.SmoothingMode = mode;
        }

        // Per le card composte in bitmap trasparenti: maschera alfa arrotondata con
        // antialias, applicata una volta sola quando la card entra in cache.
        private static unsafe void ApplyRoundedAlphaMask(Bitmap bitmap, GraphicsPath path)
        {
            using var mask = new Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var mg = Graphics.FromImage(mask))
            {
                mg.SmoothingMode = SmoothingMode.AntiAlias;
                mg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var white = new SolidBrush(Color.White);
                mg.FillPath(white, path);
            }
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var target = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var source = mask.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < rect.Height; y++)
                {
                    byte* t = (byte*)target.Scan0 + (long)y * target.Stride;
                    byte* m = (byte*)source.Scan0 + (long)y * source.Stride;
                    for (int x = 0; x < rect.Width; x++, t += 4, m += 4)
                    {
                        int a = m[3];
                        if (a == 255) continue;
                        t[0] = (byte)(t[0] * a / 255); t[1] = (byte)(t[1] * a / 255);
                        t[2] = (byte)(t[2] * a / 255); t[3] = (byte)(t[3] * a / 255);
                    }
                }
            }
            finally { bitmap.UnlockBits(target); mask.UnlockBits(source); }
        }

        private void DrawResumeCard(Graphics g, Rectangle r, ResumeItem resume)
        {
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 144f));
            bool hover = r.Contains(_lastMouse);
            // Base sempre scura (anche nel tema chiaro): testo chiaro leggibile su qualsiasi immagine.
            Color shade = Color.FromArgb(6, 10, 16);
            using var path = Round(r, S(scale, 7));
            g.SetClip(path);
            if (!DrawWideArt(g, r, resume.Item, hero: false))
                DrawGeneratedArt(g, r, resume.Item.Title, hero: false, category: resume.Item.Category);

            Rectangle lower = new Rectangle(r.Left, r.Top + (int)Math.Round(r.Height * 0.18), r.Width, Math.Max(1, (int)Math.Round(r.Height * 0.82)));
            using (var fade = new LinearGradientBrush(lower, Color.Transparent, shade, LinearGradientMode.Vertical))
            {
                fade.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.18f, 0.36f, 0.54f, 0.72f, 0.88f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(0, shade),
                        Color.FromArgb(24, shade),
                        Color.FromArgb(70, shade),
                        Color.FromArgb(128, shade),
                        Color.FromArgb(184, shade),
                        Color.FromArgb(228, shade),
                        Color.FromArgb(250, shade)
                    }
                };
                g.FillRectangle(fade, lower);
            }

            Rectangle floor = new Rectangle(r.Left, r.Bottom - Math.Max(S(scale, 58), (int)Math.Round(r.Height * 0.46)), r.Width, Math.Max(S(scale, 58), (int)Math.Round(r.Height * 0.46)));
            using (var floorBrush = new LinearGradientBrush(floor, Color.FromArgb(0, shade), Color.FromArgb(252, shade), LinearGradientMode.Vertical))
            {
                floorBrush.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.20f, 0.42f, 0.66f, 0.84f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(0, shade),
                        Color.FromArgb(30, shade),
                        Color.FromArgb(96, shade),
                        Color.FromArgb(172, shade),
                        Color.FromArgb(226, shade),
                        Color.FromArgb(252, shade)
                    }
                };
                g.FillRectangle(floorBrush, floor);
            }
            // Vignettatura solo in basso, dove ci sono titolo e avanzamento: una base scura
            // che rende il testo leggibile anche su immagini chiare.
            Rectangle band = new Rectangle(r.Left, r.Top + r.Height * 2 / 5, r.Width, r.Height - r.Height * 2 / 5 + 1);
            using (var bottomShade = new LinearGradientBrush(band, Color.Transparent, Color.FromArgb(215, 0, 0, 0), LinearGradientMode.Vertical))
            {
                bottomShade.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, .35f, .7f, 1f },
                    Colors = new[] { Color.FromArgb(0, 0, 0, 0), Color.FromArgb(70, 0, 0, 0), Color.FromArgb(160, 0, 0, 0), Color.FromArgb(215, 0, 0, 0) }
                };
                g.FillRectangle(bottomShade, band);
            }
            g.ResetClip();
            SmoothClipEdge(g, path, Back);
            if (hover)
                using (var border = new Pen(Accent, 2f))
                    g.DrawPath(border, path);

            using var title = LibraryFont("Segoe UI Semibold", Math.Max(7.8f, 8.5f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(7.3f, 8f * scale));
            TextRenderer.DrawText(g, resume.Item.Title, title, new Rectangle(r.Left + S(scale, 12), r.Bottom - S(scale, 52), r.Width - S(scale, 24), S(scale, 20)), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string left = L($"{FormatDuration(Math.Max(0, (resume.DurationSeconds - resume.PositionSeconds) / 60.0))} rimanenti", $"{FormatDuration(Math.Max(0, (resume.DurationSeconds - resume.PositionSeconds) / 60.0))} left");
            string pct = $"{resume.Progress * 100:0}%";
            Rectangle metaRect = new Rectangle(r.Left + S(scale, 12), r.Bottom - S(scale, 31), r.Width - S(scale, 24), S(scale, 17));
            TextRenderer.DrawText(g, left, meta, metaRect, Color.FromArgb(196, 206, 216), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, pct, meta, metaRect, Color.FromArgb(196, 206, 216), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            DrawProgress(g, new Rectangle(r.Left + S(scale, 12), r.Bottom - S(scale, 11), r.Width - S(scale, 24), Math.Max(3, S(scale, 3))), resume.Progress);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Continue, Resume = resume, Key = resume.Item.Path });
        }

        private void DrawArrow(Graphics g, Rectangle r, bool left, bool enabled, HitKind? action = null)
        {
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 46f));
            bool hover = enabled && r.Contains(_lastMouse);
            if (hover)
            {
                // Cerchio morbido al passaggio: la sola variazione di colore non si notava.
                int d = Math.Min(r.Width, r.Height);
                var circle = new Rectangle(r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d);
                using var halo = new SolidBrush(Color.FromArgb(HUD.Theme.IsLight ? 22 : 30, TextMain));
                g.FillEllipse(halo, circle);
            }
            Color ink = !enabled ? Color.FromArgb(HUD.Theme.IsLight ? 70 : 60, TextMain) : hover ? Accent : TextMain;
            using var pen = new Pen(ink, Math.Max(1.7f, 1.9f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            int z = S(scale, 8);
            if (left)
                g.DrawLines(pen, new[] { new Point(cx + S(scale, 4), cy - z), new Point(cx - S(scale, 4), cy), new Point(cx + S(scale, 4), cy + z) });
            else
                g.DrawLines(pen, new[] { new Point(cx - S(scale, 4), cy - z), new Point(cx + S(scale, 4), cy), new Point(cx - S(scale, 4), cy + z) });

            if (enabled)
                _hits.Add(new HitZone { Bounds = r, Kind = action ?? (left ? HitKind.Prev : HitKind.Next), Key = left ? "prev" : "next" });
        }

        private void DrawGrid(Graphics g, Rectangle r)
        {
            var visible = VisibleItems().ToList();
            if (visible.Count == 0)
            {
                DrawEmptyGrid(g, r);
                return;
            }

            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 286f));
            int gap = Math.Max(S(scale, 22), (int)Math.Round(r.Width * 0.012));
            int targetCardW = S(scale, 176);
            int columns = Math.Max(4, Math.Min(12, (r.Width + gap) / Math.Max(1, targetCardW + gap)));
            columns = Math.Max(1, columns);
            int renderCount = Math.Min(columns, visible.Count);

            int cardW = Math.Max(1, (r.Width - gap * (columns - 1)) / columns);
            int cardH = Math.Min(r.Height, Math.Max(S(scale, 230), (int)Math.Round(cardW * 1.58)));
            int y = r.Top;
            int x = r.Left;
            int remainder = Math.Max(0, r.Width - (cardW * columns + gap * (columns - 1)));

            for (int i = 0; i < renderCount; i++)
            {
                int extra = i < remainder ? 1 : 0;
                int w = cardW + extra;
                Rectangle card = new Rectangle(x, y, w, cardH);
                DrawPosterCard(g, card, visible[i], rankingPosition: IsDiaryRankingView ? i + 1 : null);
                x += w + gap;
            }
        }

        private static int EstimatePosterCardHeight(int width)
        {
            return EstimatePosterCardHeight(width, 1f, 12);
        }

        private static int EstimatePosterCardHeight(int width, float scale, int itemCount)
        {
            int gap = Math.Max(S(scale, 22), (int)Math.Round(width * 0.012));
            int targetCardW = S(scale, 176);
            int columns = Math.Max(4, Math.Min(12, (width + gap) / Math.Max(1, targetCardW + gap)));
            columns = Math.Max(1, columns);
            int cardW = Math.Max(1, (width - gap * (columns - 1)) / columns);
            return Math.Max(S(scale, 230), (int)Math.Round(cardW * 1.58));
        }

        private bool IsDiaryRankingView =>
            string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_filter, "TopRated", StringComparison.OrdinalIgnoreCase);

        private void DrawPosterCard(Graphics g, Rectangle r, LibraryItem item, bool modalCard = false, int? rankingPosition = null)
        {
            // Do not perform request bookkeeping while the wheel animation is painting
            // at 60 fps. The final frame queues missing artwork immediately.
            if (!_gridScrollAnimationTimer.Enabled)
                QueueTmdbArtworkResolve(item);
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 286f));
            bool hover = r.Contains(_lastMouse) && (_detailItem == null || modalCard);
            int infoH = Math.Max(S(scale, 58), Math.Min(S(scale, 74), (int)Math.Round(r.Height * 0.22)));
            int bleedH = Math.Max(S(scale, 74), Math.Min(S(scale, 112), (int)Math.Round(r.Height * 0.31)));
            Rectangle poster = new Rectangle(r.Left, r.Top, r.Width, Math.Max(1, r.Height - infoH));
            Rectangle info = new Rectangle(r.Left, poster.Bottom, r.Width, infoH);
            using var path = Round(r, S(scale, 7));

            // Poster, gradients, dither and captions do not change while scrolling: they
            // are rendered once per card into a bitmap and copied on every frame after.
            string quality = QualityLabel(item);
            string metaText = CardMetaText(item);
            string cardKey = $"{item.Path}|{item.ArtPath}|{r.Width}x{r.Height}|{item.Title}|{metaText}|{quality}|{Panel.ToArgb()}|{Back.ToArgb()}|{TextMain.ToArgb()}|{Muted.ToArgb()}";
            Bitmap? cardBitmap = GetPosterCardBitmap(cardKey, r.Size, local =>
            {
                Rectangle lPoster = new Rectangle(0, 0, local.Width, poster.Height);
                Rectangle lInfo = new Rectangle(0, lPoster.Bottom, local.Width, infoH);
                using var lPath = Round(new Rectangle(Point.Empty, local.Size), S(scale, 7));
                using var cg = Graphics.FromImage(local);
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                cg.SetClip(lPath);
                bool artOk = DrawPosterArt(cg, lPoster, item);
                if (!artOk)
                    DrawGeneratedArt(cg, lPoster, item.Title, hero: false, category: item.Category);
                DrawPosterInfoBand(cg, lPoster, lInfo, bleedH);
                DrawPosterCaptions(cg, lInfo, item.Title, metaText, quality, scale);
                cg.ResetClip();
                ApplyRoundedAlphaMask(local, lPath);
                // Cache only the final artwork: a smaller fallback shown while the right size
                // decodes would otherwise stay frozen in the card.
                var bled = Rectangle.Inflate(lPoster, 2, 2);
                return artOk && IsDisplayImageExact(ResolveDisplayImagePath(item.ArtPath), Math.Max(bled.Width, bled.Height));
            });
            if (cardBitmap != null)
            {
                var mode = g.InterpolationMode; var offset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(cardBitmap, r.X, r.Y, r.Width, r.Height);
                g.InterpolationMode = mode; g.PixelOffsetMode = offset;
            }
            else
            {
                using Region previousClip = g.Clip.Clone();
                g.SetClip(path, CombineMode.Intersect);
                if (!DrawPosterArt(g, poster, item))
                    DrawGeneratedArt(g, poster, item.Title, hero: false, category: item.Category);
                DrawPosterInfoBand(g, poster, info, bleedH);
                g.Clip = previousClip;
                DrawPosterCaptions(g, info, item.Title, metaText, quality, scale);
            }

            using (var border = new Pen(hover ? Accent : Color.FromArgb(64, 42, 56, 68), hover ? 2f : 1f))
                g.DrawPath(border, path);

            if (rankingPosition.HasValue)
                DrawRankingPosition(g, poster, rankingPosition.Value, scale);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Poster, Item = item, Key = item.Path });

            if (!item.IsGroup)
                DrawPosterQuickActions(g, r, item, scale, hover);
        }

        private void DrawPosterCaptions(Graphics g, Rectangle info, string titleText, string metaText, string quality, float scale)
        {
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(7.8f, 8.8f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(7.2f, 8.1f * scale));
            Rectangle titleRect = new Rectangle(info.Left + S(scale, 12), info.Top + S(scale, 8), info.Width - S(scale, 24), S(scale, 20));
            CardText(g, titleText, title, titleRect, TextMain, StringAlignment.Near);
            int qW = Math.Min(info.Width - S(scale, 38), Math.Max(S(scale, 42), TextRenderer.MeasureText(quality, meta, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + S(scale, 4)));
            Rectangle metaRect = new Rectangle(info.Left + S(scale, 12), titleRect.Bottom + S(scale, 3), Math.Max(S(scale, 20), info.Width - qW - S(scale, 28)), S(scale, 17));
            CardText(g, metaText, meta, metaRect, Muted, StringAlignment.Near);
            CardText(g, quality, meta, new Rectangle(info.Right - qW - S(scale, 12), titleRect.Bottom + S(scale, 3), qW, S(scale, 17)), TextMain, StringAlignment.Far);
        }

        // Le card vengono composte in bitmap PArgb (angoli trasparenti): il testo GDI non
        // gestisce l'alfa e nel tema chiaro i caratteri risultavano impastati. GDI+ in scala
        // di grigi compone correttamente sia sulla bitmap sia a schermo.
        private static void CardText(Graphics g, string text, Font font, Rectangle bounds, Color color, StringAlignment alignment)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
            var hint = g.TextRenderingHint;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var format = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, bounds, format);
            g.TextRenderingHint = hint;
        }

        private readonly Dictionary<string, (Bitmap Bitmap, LinkedListNode<string> Node)> _posterCards = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _posterCardOrder = new();
        // Budget in memoria invece di un numero fisso: a 1440p in Brani ci sono oltre 100
        // tessere a schermo e un limite di 120 svuotava la cache durante lo scorrimento.
        private const long PosterCardCacheBytes = 160L * 1024 * 1024;
        private const int PosterCardCacheMinimum = 120;
        private long _posterCardBytes;

        /// <summary>Card bitmap for <paramref name="key"/>; null (drawn directly) until its poster is loaded.</summary>
        private readonly Dictionary<string, (Bitmap Bitmap, long RetryAt)> _provisionalCards = new(StringComparer.Ordinal);
        private int _provisionalScrollValue = int.MinValue;
        private long _provisionalScrollMs;

        private Bitmap? GetPosterCardBitmap(string key, Size size, Func<Bitmap, bool> render)
        {
            if (size.Width < 4 || size.Height < 4) return null;
            if (_posterCards.TryGetValue(key, out var hit))
            {
                _posterCardOrder.Remove(hit.Node);
                _posterCardOrder.AddFirst(hit.Node);
                return hit.Bitmap;
            }
            long now = Environment.TickCount64;
            if (_gridScroll != _provisionalScrollValue) { _provisionalScrollValue = _gridScroll; _provisionalScrollMs = now; }
            // Riuso solo mentre si scorre: a pagina ferma ogni ridisegno ritenta, cosi' una
            // copertina appena caricata compare subito (prima restava il segnaposto).
            bool scrolling = _gridScrollAnimationTimer.Enabled || now - _provisionalScrollMs < 350;
            if (_provisionalCards.TryGetValue(key, out var provisional))
            {
                if (scrolling && now < provisional.RetryAt) return provisional.Bitmap;
                _provisionalCards.Remove(key);
                provisional.Bitmap.Dispose();
            }
            var bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            bool complete;
            try { complete = render(bitmap); }
            catch { bitmap.Dispose(); return null; }
            if (!complete)
            {
                // Miniatura provvisoria (artista senza foto, copertina non ancora a piena
                // risoluzione): prima veniva composta e buttata a ogni fotogramma, e poi ridisegnata
                // dal vivo. Si riusa per un attimo e si ritenta dopo, per l'immagine definitiva.
                if (_provisionalCards.Count >= 240)
                {
                    foreach (var old in _provisionalCards.OrderBy(entry => entry.Value.RetryAt).Take(60).ToList())
                    { _provisionalCards.Remove(old.Key); old.Value.Bitmap.Dispose(); }
                }
                _provisionalCards[key] = (bitmap, now + 250);
                return bitmap;
            }
            _posterCards[key] = (bitmap, _posterCardOrder.AddFirst(key));
            _posterCardBytes += (long)size.Width * size.Height * 4;
            while (_posterCards.Count > PosterCardCacheMinimum && _posterCardBytes > PosterCardCacheBytes && _posterCardOrder.Last != null)
            {
                string victim = _posterCardOrder.Last.Value;
                _posterCardOrder.RemoveLast();
                if (_posterCards.Remove(victim, out var old))
                {
                    _posterCardBytes -= (long)old.Bitmap.Width * old.Bitmap.Height * 4;
                    old.Bitmap.Dispose();
                }
            }
            return bitmap;
        }

        private void DrawRankingPosition(Graphics g, Rectangle poster, int position, float scale)
        {
            string label = position.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(10.5f, 12.5f * scale));
            Size measured = TextRenderer.MeasureText(label, font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int height = Math.Max(S(scale, 31), measured.Height + S(scale, 10));
            int width = Math.Max(height, measured.Width + S(scale, 18));
            Rectangle badge = new(poster.Left + S(scale, 10), poster.Top + S(scale, 10), width, height);

            using var path = Round(badge, S(scale, 5));
            using var fill = new SolidBrush(Color.FromArgb(224, HUD.Theme.IsLight ? 244 : 5,
                HUD.Theme.IsLight ? 247 : 12, HUD.Theme.IsLight ? 250 : 20));
            using var border = new Pen(Color.FromArgb(205, Accent));
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            TextRenderer.DrawText(g, label, font, badge, HUD.Theme.IsLight ? Color.FromArgb(20, 29, 38) : Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawDiaryRating(Graphics g, Rectangle info, LibraryItem item, float scale)
        {
            double rating = GetDiaryRating(item.Path);
            int star = Math.Clamp(S(scale, 16), 14, 21);
            int gap = Math.Max(3, S(scale, 4));
            int total = star * 5 + gap * 4;
            int x = info.Left + S(scale, 11);
            int y = info.Bottom - star - S(scale, 7);
            if (total > info.Width - S(scale, 22))
            {
                star = Math.Max(12, (info.Width - S(scale, 22) - gap * 4) / 5);
                total = star * 5 + gap * 4;
            }

            for (int i = 1; i <= 5; i++)
            {
                Rectangle hit = new Rectangle(x, y, star, star);
                DrawDiaryStarFill(g, hit, Math.Clamp(rating - (i - 1), 0, 1));
                _hits.Add(new HitZone { Bounds = Rectangle.Inflate(hit, 2, 2), Kind = HitKind.DiaryRate, Key = item.Path, Index = i * 2, Item = item });
                x += star + gap;
            }

            using var scoreFont = LibraryFont("Segoe UI Semibold", Math.Max(7.0f, 7.8f * scale));
            string score = rating > 0 ? FormatDiaryRating(rating) : L("Valuta", "Rate");
            TextRenderer.DrawText(g, score, scoreFont,
                new Rectangle(info.Left + S(scale, 11) + total + S(scale, 8), y - 1,
                    Math.Max(1, info.Right - (info.Left + S(scale, 11) + total + S(scale, 12))), star + 2),
                rating > 0 ? Color.FromArgb(238, 205, 130) : Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawPosterQuickActions(Graphics g, Rectangle card, LibraryItem item, float scale, bool cardHovered)
        {
            int size = Math.Clamp(S(scale, 30), 27, 38);
            int gap = Math.Max(5, S(scale, 6));
            int pad = Math.Max(7, S(scale, 9));
            Rectangle favourite = new(card.Right - pad - size, card.Top + pad, size, size);
            Rectangle playlist = new(favourite.Left - gap - size, favourite.Top, size, size);
            bool selected = IsFavorite(item.Path);
            int alpha = cardHovered || selected ? 228 : 148;

            void DrawQuick(Rectangle bounds, string icon, bool active, HitKind kind)
            {
                using var path = Round(bounds, bounds.Width / 2);
                using var fill = new SolidBrush(active
                    ? Color.FromArgb(alpha, Selection)
                    : Color.FromArgb(alpha, 4, 12, 20));
                using var border = new Pen(active ? BorderAccent : Color.Transparent);
                g.FillPath(fill, path);
                g.DrawPath(border, path);
                int inset = Math.Max(6, bounds.Width / 4);
                DrawIcon(g, Rectangle.Inflate(bounds, -inset, -inset), icon,
                    active ? Color.White : Color.FromArgb(225, 232, 241));
                _hits.Add(new HitZone { Bounds = bounds, Kind = kind, Item = item, Key = item.Path });
            }

            DrawQuick(playlist, "playlist", false, HitKind.PosterPlaylist);
            DrawQuick(favourite, "star", selected, HitKind.PosterFavorite);
        }

        /// <summary>
        /// Rampa smoothstep campionata fitta: con pochi punti lineari l'occhio vede un "gomito" a
        /// ogni punto (bande di Mach), molto evidenti quando si sfuma verso il bianco.
        /// gamma &gt; 1 tiene libera piu' a lungo la parte iniziale (l'immagine resta nitida).
        /// </summary>
        private static ColorBlend EasedBlend(Color color, int fromAlpha, int toAlpha, float gamma = 1f, int steps = 24)
        {
            var positions = new float[steps + 1];
            var colors = new Color[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float p = i / (float)steps;
                float t = MathF.Pow(p, gamma);
                float s = t * t * (3 - 2 * t);
                positions[i] = p;
                colors[i] = Color.FromArgb(Math.Clamp((int)MathF.Round(fromAlpha + (toAlpha - fromAlpha) * s), 0, 255), color);
            }
            return new ColorBlend { Positions = positions, Colors = colors };
        }

        private void DrawPosterInfoBand(Graphics g, Rectangle poster, Rectangle info, int bleedH)
        {
            bool light = HUD.Theme.IsLight && !_darkSurfaceText;
            // In chiaro la nebbia bianca lunga lavava la locandina: sfumatura piu' corta e che
            // parte piano, cosi' l'immagine resta contrastata fin quasi alla fascia dati.
            if (light) bleedH = Math.Max(1, (int)Math.Round(bleedH * 0.55));
            // Fascia dati separata dalla locandina, ma fusa con una vignettatura lunga.
            // Non c'Ã¨ uno stacco netto: il nero/blu sale dentro la cover in modo progressivo.
            Color band = Panel;
            Color deep = Back;

            int clampedBleed = Math.Min(Math.Max(bleedH, 1), poster.Height);
            Rectangle posterFade = new Rectangle(poster.Left, poster.Bottom - clampedBleed, poster.Width, clampedBleed);
            // Rettangolo del pennello un pixel piu' alto: GDI+ ripete il gradiente oltre i bordi.
            using (var fade = new LinearGradientBrush(Rectangle.Inflate(posterFade, 0, 1), Color.Transparent, band, LinearGradientMode.Vertical))
            {
                fade.InterpolationColors = EasedBlend(band, 0, 248, light ? 1.35f : 1f);
                g.FillRectangle(fade, posterFade);
            }

            // Ombra laterale bassa: in chiaro aveva un bordo superiore netto (una riga a due
            // terzi della locandina) e non serve, il bianco non ha bisogno di profondita'.
            if (!light)
            {
            Rectangle lowerSide = new Rectangle(poster.Left, poster.Top + (int)Math.Round(poster.Height * 0.68), poster.Width, poster.Height - (int)Math.Round(poster.Height * 0.68));
            using (var side = new LinearGradientBrush(lowerSide, Color.FromArgb(76, deep), Color.FromArgb(0, deep), LinearGradientMode.Horizontal))
            {
                side.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.24f, 0.72f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(18, deep),
                        Color.FromArgb(8, deep),
                        Color.FromArgb(3, deep),
                        Color.FromArgb(12, deep)
                    }
                };
                g.FillRectangle(side, lowerSide);
            }
            }

            using (var fill = new SolidBrush(band))
                g.FillRectangle(fill, info);

            int seamH = Math.Min(Math.Max(22, clampedBleed / 3), clampedBleed);
            Rectangle seam = new Rectangle(info.Left, info.Top - seamH, info.Width, seamH + info.Height);
            using (var seamFade = new LinearGradientBrush(Rectangle.Inflate(seam, 0, 1), Color.FromArgb(0, band), band, LinearGradientMode.Vertical))
            {
                seamFade.InterpolationColors = EasedBlend(band, 0, 255, 1.2f);
                g.FillRectangle(seamFade, seam);
            }
            VisualDither.Overlay(g, Rectangle.Union(posterFade, info), light ? 4 : 3);
        }

        private void DrawEmptyGrid(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0)
                return;

            string iconName = IsNetworkSourceActive() ? "network" : _category switch
            {
                "WatchHistory" => "time",
                "Favourites" => "star",
                "Playlists" => "playlist",
                "Music" => "music",
                "Photos" => "photo",
                "TV Series" => "tv",
                "Videos" => "video",
                _ => "movie"
            };
            // Un messaggio pertinente per categoria invece dello stesso testo ovunque.
            (string titleText, string bodyText) = IsNetworkSourceActive()
                ? (L("Nessun contenuto in questa categoria", "No content in this category"),
                   L("Connetti un server dalla pagina Rete o scegli un'altra categoria.", "Connect a server from Network or choose another category."))
                : _category switch
                {
                    "WatchHistory" => (L("Nessuna visione registrata", "No viewing history yet"),
                        L("I film e gli episodi che guardi compariranno qui.", "Movies and episodes you watch will appear here.")),
                    "Favourites" => (L("Nessun preferito", "No favourites yet"),
                        L("Tocca la stella su un titolo per ritrovarlo qui.", "Tap the star on a title to find it here.")),
                    "Playlists" => (L("Nessuna playlist", "No playlists yet"),
                        L("Crea una playlist con “Nuova playlist” in alto.", "Create one with “New playlist” above.")),
                    "Music" => (L("Nessun brano", "No music yet"),
                        L("Aggiungi una cartella con la tua musica.", "Add a folder with your music.")),
                    "Photos" => (L("Nessuna foto", "No photos yet"),
                        L("Aggiungi una cartella di immagini.", "Add a folder of pictures.")),
                    "TV Series" => (L("Nessuna serie TV", "No TV series yet"),
                        L("Aggiungi la cartella delle tue serie.", "Add the folder with your series.")),
                    "Videos" => (L("Nessun video", "No videos yet"),
                        L("Aggiungi una cartella di video.", "Add a folder of videos.")),
                    _ => (L("Nessun film", "No movies yet"),
                        L("Aggiungi la cartella dei tuoi film.", "Add the folder with your movies."))
                };
            DrawMinimalEmptyState(g, r, iconName, titleText, bodyText);
        }

        private void DrawMinimalEmptyState(Graphics g, Rectangle r, string icon, string title, string body)
        {
            float scale = Math.Clamp(LayoutScale(ClientRectangle), .75f, 1.5f);
            int width = Math.Max(1, Math.Min(S(scale, 540), r.Width - 32));
            int x = r.Left + (r.Width - width) / 2, y = r.Top + Math.Max(0, (r.Height - S(scale, 120)) / 2);
            int side = S(scale, 28);
            // Il segnaposto non resta mai senza icona: se il file SVG non si carica, il glifo disegnato a mano.
            var iconRect = new Rectangle(r.Left + (r.Width - side) / 2, y, side, side);
            if (!DrawIcon(g, iconRect, icon, Muted))
                DrawLibraryFallbackGlyph(g, iconRect, icon, Muted, Math.Max(.8f, side / 24f));
            using var titleFont = LibraryFont("Segoe UI Semibold", 12f * scale);
            using var bodyFont = LibraryFont("Segoe UI", 9.5f * scale);
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(x, y + S(scale, 44), width, S(scale, 27)), TextMain, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, body, bodyFont, new Rectangle(x, y + S(scale, 76), width, S(scale, 44)), Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        // Riepilogo della libreria in colonne: icona neutra, numero in chiaro ed etichetta
        // attenuata; divisori corti che sfumano alle estremita', nessuna linea sopra.
        // Le voci a zero (es. "0 serie TV") non occupano una colonna.
        private void DrawStats(Graphics g, Rectangle r)
        {
            float scale = Math.Clamp(r.Height / 58f, .5f, 2f);
            var source = _statsCells.Count > 0
                ? _statsCells.ToList()
                : new List<(string Icon, string Text)> { ("grid", L($"{_items.Count:N0} elementi", $"{_items.Count:N0} items")) };

            var cells = new List<(string Icon, string Value, string Label)>();
            foreach (var (icon, text) in source)
            {
                string label = text.Trim(), value = string.Empty;
                var match = System.Text.RegularExpressions.Regex.Match(label, @"^([\d.,]+(?:\s?(?:TB|GB|MB|KB|B))?)(?:\s+(.*))?$");
                if (match.Success)
                {
                    value = match.Groups[1].Value;
                    label = match.Groups[2].Value;
                    if (value.Trim('0', '.', ',').Length == 0) continue;
                }
                cells.Add((icon, value, label));
            }
            if (cells.Count == 0) return;

            using var valueFont = LibraryFont("Segoe UI Semibold", Math.Max(8.8f, 9.6f * scale));
            using var labelFont = LibraryFont("Segoe UI", Math.Max(8.8f, 9.6f * scale));
            const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Color divider = Color.FromArgb(HUD.Theme.IsLight ? 60 : 46, HUD.Theme.IsLight ? Color.Black : Color.White);
            int w = Math.Max(1, r.Width / cells.Count);
            int iconSize = S(scale, 16);
            for (int i = 0; i < cells.Count; i++)
            {
                var (icon, value, label) = cells[i];
                Rectangle cell = new Rectangle(r.Left + i * w, r.Top, i == cells.Count - 1 ? r.Right - (r.Left + i * w) : w, r.Height);
                bool hasIcon = HasIconAsset(icon);
                int gap = S(scale, 9);
                Size valueSize = value.Length > 0 ? TextRenderer.MeasureText(g, value, valueFont, Size.Empty, Flags) : Size.Empty;
                Size labelSize = label.Length > 0 ? TextRenderer.MeasureText(g, label, labelFont, Size.Empty, Flags) : Size.Empty;
                int spacing = value.Length > 0 && label.Length > 0 ? S(scale, 5) : 0;
                int totalW = Math.Min(cell.Width - S(scale, 16), (hasIcon ? iconSize + gap : 0) + valueSize.Width + spacing + labelSize.Width);
                int x = cell.Left + (cell.Width - totalW) / 2;
                if (hasIcon)
                {
                    DrawIcon(g, new Rectangle(x, cell.Top + (cell.Height - iconSize) / 2, iconSize, iconSize), icon, Color.FromArgb(170, Muted));
                    x += iconSize + gap;
                }
                if (value.Length > 0)
                {
                    TextRenderer.DrawText(g, value, valueFont, new Rectangle(x, cell.Top, valueSize.Width + 2, cell.Height), Subtle, Flags);
                    x += valueSize.Width + spacing;
                }
                if (label.Length > 0)
                    TextRenderer.DrawText(g, label, labelFont, new Rectangle(x, cell.Top, Math.Max(1, cell.Right - x - S(scale, 6)), cell.Height), Muted, Flags | TextFormatFlags.EndEllipsis);
                if (i > 0)
                {
                    int h = Math.Max(8, cell.Height / 2);
                    var line = new Rectangle(cell.Left, cell.Top + (cell.Height - h) / 2, 1, h);
                    using var fade = new LinearGradientBrush(new Rectangle(line.X, line.Y - 1, 1, line.Height + 2), divider, divider, LinearGradientMode.Vertical)
                    {
                        InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, divider), divider, Color.FromArgb(0, divider) }, Positions = new[] { 0f, .5f, 1f } }
                    };
                    g.FillRectangle(fade, line);
                }
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> KnownFileSizes = new(StringComparer.OrdinalIgnoreCase);
        private int _storageMeasureVersion;

        /// <summary>
        /// Total size of <paramref name="paths"/>. Returns how many files are still
        /// unmeasured because they are remote (and <paramref name="includeRemote"/> is off)
        /// or because <paramref name="budget"/> ran out.
        /// </summary>
        private (long Bytes, int Pending) MeasureLibraryBytes(IReadOnlyList<string> paths, TimeSpan budget, bool includeRemote)
        {
            long bytes = 0;
            int pending = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (string path in paths)
            {
                if (_itemCache.TryGetValue(path, out LibraryItem? cached) && cached.Bytes > 0)
                {
                    KnownFileSizes[path] = cached.Bytes;
                    bytes += cached.Bytes;
                    continue;
                }
                if (KnownFileSizes.TryGetValue(path, out long known))
                {
                    bytes += known;
                    continue;
                }
                if ((!includeRemote && IsRemoteStoragePath(path)) || watch.Elapsed > budget)
                {
                    pending++;
                    continue;
                }
                long length = 0;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) length = info.Length;
                }
                catch { }
                // Missing files count as measured (0 bytes): they must not block the total.
                KnownFileSizes[path] = length;
                bytes += length;
            }
            return (bytes, pending);
        }

        private void CompleteStorageMeasureInBackground(IReadOnlyList<string> paths)
        {
            int version = Interlocked.Increment(ref _storageMeasureVersion);
            _ = Task.Run(() =>
            {
                var (bytes, _) = MeasureLibraryBytes(paths, TimeSpan.MaxValue, includeRemote: true);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || version != Volatile.Read(ref _storageMeasureVersion)) return;
                        int index = _statsCells.FindIndex(cell => cell.Icon == "storage");
                        if (index < 0) return;
                        _statsCells[index] = ("storage", bytes > 0 ? FormatBytes(bytes) : L("Dimensione non disponibile", "Size unavailable"));
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        private List<(string Icon, string Text)> BuildStatsCells(IReadOnlyList<LibraryItem> loadedItems)
        {
            if (IsNetworkSourceActive())
                return BuildNetworkStatsCells(loadedItems);

            if (string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase))
            {
                var entries = WatchHistoryStore.LoadAll().Where(entry => IsDiaryEligiblePath(entry.MediaPath)).ToList();
                var rated = entries.Where(entry => entry.Rating > 0).ToList();
                double average = rated.Count > 0 ? rated.Average(entry => entry.Rating) : 0;
                int fiveStars = rated.Count(entry => entry.Rating == 5);
                return new List<(string Icon, string Text)>
                {
                    ("time", L($"{entries.Count:N0} titoli visti", $"{entries.Count:N0} watched")),
                    ("star", rated.Count > 0 ? L($"Media {average:0.0}/5", $"Average {average:0.0}/5") : L("Nessun voto", "No ratings")),
                    ("movie", L($"{fiveStars:N0} da 5 stelle", $"{fiveStars:N0} five-star"))
                };
            }

            var all = AllKnownPaths()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !ShouldIgnoreMediaPath(p))
                .ToList();

            var moviePaths = AllPathsForCategory("Movies")
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !ShouldIgnoreMediaPath(path) && IsMoviePath(path))
                .ToList();

            var tvPaths = AllPathsForCategory("TV Series")
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !ShouldIgnoreMediaPath(path) && IsTvEpisodePath(path) && IsVideoPath(path))
                .ToList();

            int movies = moviePaths.Count;
            int tv = tvPaths
                .Select(TvSeriesKey)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int artists = all.Where(IsMusicPath)
                .Select(ArtistKey)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            // Never stat every indexed file while the splash waits for Home: sizes
            // already known are reused, local files are measured within a small budget
            // and network shares (which may be asleep) are finished in the background.
            var (bytes, pending) = MeasureLibraryBytes(all, TimeSpan.FromMilliseconds(350), includeRemote: false);

            var cells = new List<(string Icon, string Text)>
            {
                ("movie", L($"{movies:N0} film", $"{movies:N0} movies")),
                ("tv", L($"{tv:N0} serie TV", $"{tv:N0} TV shows"))
            };
            if (artists > 0)
                cells.Add(("music", L($"{artists:N0} artisti", $"{artists:N0} artists")));
            cells.Add(("storage", pending == 0
                ? (bytes > 0 ? FormatBytes(bytes) : L("Dimensione non disponibile", "Size unavailable"))
                : L("Calcolo dimensione…", "Measuring size…")));
            if (pending > 0)
                CompleteStorageMeasureInBackground(all);
            cells.Add(("cloud", L("Metadati TMDb", "TMDb metadata")));
            cells.Add(("grid", L($"{all.Count:N0} elementi", $"{all.Count:N0} items")));
            return cells;
        }

        private static string TvSeriesKey(string path)
        {
            try
            {
                var info = MovieMetadataService.ExtractMediaTitleInfoFromPath(path);
                if (!string.IsNullOrWhiteSpace(info.SeriesTitle))
                    return CleanTitle(info.SeriesTitle!);
                if (!string.IsNullOrWhiteSpace(info.NormalizedTitle))
                    return CleanTitle(info.NormalizedTitle);
            }
            catch { }

            try
            {
                string? dir = Path.GetDirectoryName(path);
                string? parent = !string.IsNullOrWhiteSpace(dir) ? Path.GetFileName(dir) : null;
                if (!string.IsNullOrWhiteSpace(parent))
                    return CleanTitle(Regex.Replace(parent!, @"\b(?:season|stagione)\s*\d{1,2}\b", string.Empty, RegexOptions.IgnoreCase));
            }
            catch { }

            return Path.GetFileNameWithoutExtension(path) ?? path;
        }

        private static string ArtistKey(string path)
        {
            try
            {
                string? albumDir = Path.GetDirectoryName(path);
                string? artistDir = !string.IsNullOrWhiteSpace(albumDir) ? Path.GetDirectoryName(albumDir) : null;
                string? value = !string.IsNullOrWhiteSpace(artistDir) ? Path.GetFileName(artistDir) : Path.GetFileName(albumDir);
                return CleanTitle(value ?? string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static MovieMetadataService.MediaTitleInfo? TryExtractMediaTitleInfo(string path)
        {
            try { return MovieMetadataService.ExtractMediaTitleInfoFromPath(path); }
            catch { return null; }
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return CleanTitle(value!);
            }
            return string.Empty;
        }

        private static string FirstRawNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return Regex.Replace(value!.Trim(), @"\s+", " ");
            }
            return string.Empty;
        }

        private static string NormalizeGroupKey(string? value)
        {
            string clean = Regex.Replace(value ?? string.Empty, @"[^\p{L}\p{N}]+", " ").Trim().ToLowerInvariant();
            return Regex.Replace(clean, @"\s+", " ");
        }

        private string BuildSeasonGroupTitle(string series, int season)
        {
            string name = string.IsNullOrWhiteSpace(series) ? L("Serie TV", "TV Series") : CleanTitle(series);
            return UiEnglish ? $"{name} - Season {season:00}" : $"{name} - Stagione {season:00}";
        }

        private static string? ExtractSeriesTitleFromDisplay(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string value = Regex.Replace(title!, @"\s+", " ").Trim();
            var match = Regex.Match(value, @"^(?<title>.+?)\s*(?:[-:]\s*)?(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|E\d{1,3})\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string head = match.Groups["title"].Value.Trim(' ', '-', ':');
                if (!string.IsNullOrWhiteSpace(head))
                    return CleanTitle(head);
            }

            match = Regex.Match(value, @"^(?<title>.+?)\s*[-:]\s*(?:Stagione|Season)\s*\d{1,2}\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string head = match.Groups["title"].Value.Trim(' ', '-', ':');
                if (!string.IsNullOrWhiteSpace(head))
                    return CleanTitle(head);
            }

            return null;
        }

        private static int? TryParseSeasonNumber(string path)
        {
            foreach (string sample in MediaPathSegmentsFromLeaf(path))
            {
                var match = Regex.Match(sample, @"\bS(?<n>\d{1,2})\s*[-_. ]?\s*E\d{1,3}\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(sample, @"\b(?<n>\d{1,2})x\d{1,3}\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(sample, @"\b(?:season|stagione)\s*(?<n>\d{1,2})\b", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups["n"].Value, out int value))
                    return value;
            }
            return null;
        }

        private static int? TryParseEpisodeNumber(string path)
        {
            foreach (string sample in MediaPathSegmentsFromLeaf(path))
            {
                var match = Regex.Match(sample, @"\bS\d{1,2}\s*[-_. ]?\s*E(?<n>\d{1,3})\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(sample, @"\b\d{1,2}x(?<n>\d{1,3})\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(sample, @"\b(?:episode|episodio|ep)\s*(?<n>\d{1,3})\b", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups["n"].Value, out int value))
                    return value;
            }
            return null;
        }

        private static IEnumerable<string> MediaPathSegmentsFromLeaf(string path)
        {
            string file = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(file))
                yield return file;

            string? directory = Path.GetDirectoryName(path);
            int depth = 0;
            while (!string.IsNullOrWhiteSpace(directory) && depth++ < 5)
            {
                string segment = Path.GetFileName(directory) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(segment))
                    yield return segment;
                directory = Path.GetDirectoryName(directory);
            }
        }

        private static int? TryParseTrackNumber(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var match = Regex.Match(name, @"^\s*(?:disc\s*\d+\s*)?(?<n>\d{1,3})(?:\s*[-_.\)]|\s+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(name, @"^\s*\d{1,2}\s*[-_.]\s*(?<n>\d{1,3})(?:\s*[-_.\)]|\s+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups["n"].Value, out int value) ? value : null;
        }

        private static (string Album, string Artist, string DirectoryKey) InferAlbumInfo(string path)
        {
            try
            {
                string? albumDir = Path.GetDirectoryName(path);
                if (Regex.IsMatch(Path.GetFileName(albumDir) ?? "", @"^(?:cd|disc|disk|disco)\s*[-_. ]*\d+$", RegexOptions.IgnoreCase))
                    albumDir = Path.GetDirectoryName(albumDir);
                string album = CleanMusicTitle(Path.GetFileName(albumDir) ?? string.Empty);
                string? artistDir = !string.IsNullOrWhiteSpace(albumDir) ? Path.GetDirectoryName(albumDir) : null;
                string artist = CleanMusicTitle(Path.GetFileName(artistDir) ?? string.Empty);
                string directoryKey = !string.IsNullOrWhiteSpace(albumDir)
                    ? NormalizeRootPath(albumDir)
                    : NormalizeGroupKey(artist + "|" + album);
                return (album, artist, directoryKey);
            }
            catch
            {
                return (string.Empty, string.Empty, string.Empty);
            }
        }

        private static string CleanMusicTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string clean = Regex.Replace(value.Trim(), @"[_\.]+", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '_', '.');
            return clean;
        }

        private static string CleanMusicTrackTitle(string? value)
        {
            string clean = MusicTextIdentity.Title(value);
            return string.IsNullOrWhiteSpace(clean) ? "Untitled" : clean;
        }

        private static string? FirstExistingPath(IEnumerable<string?> paths)
        {
            string?[] candidates = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            // Cached TMDb/local artwork has better resolution than most server-provided
            // DLNA thumbnails, so prefer existing files and keep URLs as fallbacks.
            foreach (string? path in candidates)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    string value = path.Trim();
                    if (File.Exists(value))
                        return value;
                }
                catch { }
            }
            foreach (string? path in candidates)
                if (!string.IsNullOrWhiteSpace(path) && IsHttpUrl(path)) return path.Trim();
            return null;
        }

        private static bool IsHttpUrl(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static bool ShouldResolveRichOverview(string category)
        {
            return string.Equals(category, "Movies", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "TV Series", StringComparison.OrdinalIgnoreCase);
        }

        private static string DefaultOverviewForCategory(string category)
        {
            // I fallback sono localizzati al momento del rendering da HeroText.
            // Non salvarli nel modello, altrimenti restano nella lingua attiva al scan.
            return string.Empty;
        }

        private static bool IsTmdbSyncedPath(string path)
        {
            if (!IsVideoPath(path))
                return false;

            try
            {
                if (MovieMetadataService.IsCachedTitleResolved(path))
                    return true;
                if (!string.IsNullOrWhiteSpace(MovieMetadataService.GetCachedPosterPath(path)))
                    return true;
                if (!string.IsNullOrWhiteSpace(MovieMetadataService.GetCachedBackdropPath(path)))
                    return true;
            }
            catch { }

            return false;
        }

        private void DrawPill(Graphics g, Rectangle r, string text, Color textColor)
            => HUD.MediaTechnicalPill.Draw(g, r, text, textColor, Accent, IsQualityChip(text));

        private void DrawProgress(Graphics g, Rectangle r, double progress)
        {
            using (var bg = new SolidBrush(Color.FromArgb(68, 88, 106)))
                g.FillRectangle(bg, r);
            int w = (int)Math.Round(r.Width * Math.Max(0, Math.Min(1, progress)));
            if (w > 0)
            {
                using var fg = new SolidBrush(Accent);
                g.FillRectangle(fg, new Rectangle(r.Left, r.Top, w, r.Height));
            }
        }

        private string? _heroLastDrawnArt;

        private bool DrawWideArt(Graphics g, Rectangle r, LibraryItem item, bool hero)
        {
            if (hero)
            {
                // Hero: l'immagine parte sotto la vignettatura, non dal bordo sinistro.
                // Il bordo d'ingresso viene poi mascherato da una sfumatura dedicata:
                // cosÃ¬ non compare mai lo stacco netto anche con backdrop molto luminosi.
                Rectangle art = HeroArtworkRect(r);
                bool drawn = DrawImagePath(g, art, item.WideArtPath, requireLandscape: false, tint: true, preserveAspectWhenWide: false, horizontalFocus: 0.52f, verticalFocus: 0.23f);
                if (drawn) _heroLastDrawnArt = item.WideArtPath;
                else if (!string.IsNullOrWhiteSpace(item.WideArtPath) && !string.IsNullOrWhiteSpace(_heroLastDrawnArt))
                {
                    // La foto del nuovo titolo esiste ma non e' ancora in memoria: per quell'attimo resta
                    // quella di prima. Prima compariva per un istante il disegno di ripiego "senza immagine".
                    drawn = DrawImagePath(g, art, _heroLastDrawnArt, requireLandscape: false, tint: true, preserveAspectWhenWide: false, horizontalFocus: 0.52f, verticalFocus: 0.23f);
                }
                if (drawn)
                    DrawHeroArtworkEntryFade(g, r, art);
                return drawn;
            }

            // Le card Continue watching restano wide vere e proprie.
            return DrawImagePath(g, r, item.WideArtPath, requireLandscape: true, tint: false, preserveAspectWhenWide: false, verticalFocus: 0.46f);
        }

        private static Rectangle HeroArtworkRect(Rectangle r)
        {
            // L'immagine entra un filo prima rispetto alla versione precedente.
            // Non deve essere un blocco separato: deve stare giÃ  sotto la parte scura
            // e poi fondersi con la vignettatura principale.
            int start = r.Left + (int)Math.Round(r.Width * 0.20);
            int minW = Math.Max(360, (int)Math.Round(r.Height * 2.45));
            if (r.Right - start < minW)
                start = Math.Max(r.Left + r.Width / 4, r.Right - minW);
            return new Rectangle(start, r.Top, Math.Max(1, r.Right - start), r.Height);
        }

        private void DrawHeroArtworkEntryFade(Graphics g, Rectangle hero, Rectangle art)
        {
            if (art.Width <= 1 || art.Height <= 1)
                return;

            // Maschera morbida solo sul punto in cui inizia la foto.
            // Ãˆ piÃ¹ larga del bordo reale dell'immagine, quindi anche le cover chiare
            // si fondono col nero/blu invece di mostrare una riga verticale.
            int fadeW = Math.Min(Math.Max(96, (int)Math.Round(hero.Width * 0.24)), Math.Max(1, art.Width));
            Rectangle fade = new Rectangle(art.Left, art.Top, fadeW, art.Height);
            using (var entry = new LinearGradientBrush(Rectangle.Inflate(fade, 1, 0), Back, Color.Transparent, LinearGradientMode.Horizontal))
            {
                entry.InterpolationColors = EasedBlend(Back, 255, 0, 0.9f);
                g.FillRectangle(entry, fade);
            }

            // Piccola ombra verticale sopra/sotto l'area immagine per legarla al banner
            // (solo in scuro: in chiaro sarebbe un velo bianco sulla foto).
            if (HUD.Theme.IsLight && !_darkSurfaceText) return;
            Rectangle top = new Rectangle(art.Left, hero.Top, art.Width, Math.Max(1, hero.Height / 5));
            using (var topFade = new LinearGradientBrush(top, Color.FromArgb(86, Back), Color.FromArgb(0, Back), LinearGradientMode.Vertical))
                g.FillRectangle(topFade, top);
        }

        private bool DrawPosterArt(Graphics g, Rectangle r, LibraryItem item)
        {
            bool centeredArt = string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase);

            // Nei riquadri Movies la locandina resta ancorata in alto, ma viene disegnata
            // con un piccolo bleed esterno. Questo evita micro-gap da interpolazione e
            // impedisce che compaiano righe chiare sul bordo superiore della card.
            return DrawImagePath(
                g,
                r,
                item.ArtPath,
                requireLandscape: false,
                tint: false,
                preserveAspectWhenWide: false,
                verticalFocus: centeredArt ? 0.5f : 0.0f,
                destinationBleed: 2,
                sourceTopTrimPx: centeredArt ? 0 : 2);
        }

        private bool DrawImagePath(Graphics g, Rectangle r, string? artPath, bool requireLandscape, bool tint, bool preserveAspectWhenWide, float horizontalFocus = 0.5f, float verticalFocus = 0.5f, int destinationBleed = 0, int sourceTopTrimPx = 0)
        {
            string? displayPath = ResolveDisplayImagePath(artPath);
            if (string.IsNullOrWhiteSpace(displayPath))
                return false;

            Rectangle dest = destinationBleed > 0
                ? Rectangle.Inflate(r, destinationBleed, destinationBleed)
                : r;

            Image? img = LoadImageForDisplay(displayPath, Math.Max(dest.Width, dest.Height));
            if (img == null)
                return false;

            if (requireLandscape && img.Width < img.Height * 1.12)
                return false;

            Rectangle src = CoverSource(img.Size, r.Size, horizontalFocus, verticalFocus);

            if (sourceTopTrimPx > 0 && src.Height > sourceTopTrimPx + 1)
            {
                int trim = Math.Min(sourceTopTrimPx, src.Height - 1);
                src = new Rectangle(src.X, src.Y + trim, src.Width, src.Height - trim);
            }

            // Scrolling repainted every visible poster with a high-quality resample on each
            // frame (~150 ms per page). The resampled card-sized bitmap is cached, so a frame
            // only copies pixels; it is rebuilt when the size, crop or source image changes.
            Bitmap? rendered = GetRenderedArtwork(img, displayPath, dest.Size, src);
            if (rendered != null)
            {
                var previousMode = g.InterpolationMode;
                var previousOffset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(rendered, dest.X, dest.Y, dest.Width, dest.Height);
                g.InterpolationMode = previousMode;
                g.PixelOffsetMode = previousOffset;
            }
            else
            {
                // TileFlipXY evita che il campionamento bicubico prenda pixel trasparenti/chiari
                // fuori dall'immagine. Insieme al bleed elimina le righe sottili ai bordi.
                using var attrs = new System.Drawing.Imaging.ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, dest, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }

            if (tint)
            {
                using var accentWash = new SolidBrush(Color.FromArgb(10, Accent));
                g.FillRectangle(accentWash, r);
            }
            return true;
        }

        private sealed class RenderedArtwork
        {
            public required Bitmap Bitmap;
            public required Image Source;
            public LinkedListNode<string>? Node;
        }

        private readonly Dictionary<string, RenderedArtwork> _renderedArtwork = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _renderedArtworkOrder = new();
        private long _renderedArtworkPixels;
        private const long RenderedArtworkBudgetPixels = 16_000_000; // ~64 MB at most

        private Bitmap? GetRenderedArtwork(Image source, string path, Size size, Rectangle src)
        {
            if (size.Width < 2 || size.Height < 2 || size.Width * (long)size.Height > 4_000_000)
                return null; // hero-sized art is drawn once, caching it would only cost memory
            // Rendered once, so always at full quality (also while scrolling).
            string key = $"{path}|{size.Width}x{size.Height}|{src.X},{src.Y},{src.Width},{src.Height}";
            if (_renderedArtwork.TryGetValue(key, out var hit) && ReferenceEquals(hit.Source, source))
            {
                _renderedArtworkOrder.Remove(hit.Node!);
                _renderedArtworkOrder.AddFirst(hit.Node!);
                return hit.Bitmap;
            }
            if (hit != null) RemoveRenderedArtwork(key);

            Bitmap bitmap;
            try
            {
                bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(bitmap);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var attrs = new System.Drawing.Imaging.ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(source, new Rectangle(Point.Empty, size), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            catch { return null; }

            var entry = new RenderedArtwork { Bitmap = bitmap, Source = source };
            entry.Node = _renderedArtworkOrder.AddFirst(key);
            _renderedArtwork[key] = entry;
            _renderedArtworkPixels += size.Width * (long)size.Height;
            while (_renderedArtworkPixels > RenderedArtworkBudgetPixels && _renderedArtworkOrder.Last != null)
                RemoveRenderedArtwork(_renderedArtworkOrder.Last.Value);
            return bitmap;
        }

        /// <summary>Drops the per-card and per-artwork bitmaps (content reload, disposal).</summary>
        private void ClearRenderedCaches()
        {
            foreach (var entry in _renderedArtwork.Values) entry.Bitmap.Dispose();
            _renderedArtwork.Clear();
            _renderedArtworkOrder.Clear();
            _renderedArtworkPixels = 0;
            foreach (var entry in _posterCards.Values) entry.Bitmap.Dispose();
            _posterCards.Clear(); _posterCardBytes = 0; foreach (var card in _provisionalCards.Values) card.Bitmap.Dispose(); _provisionalCards.Clear();
            _posterCardOrder.Clear();
        }

        private void RemoveRenderedArtwork(string key)
        {
            if (!_renderedArtwork.Remove(key, out var entry)) return;
            if (entry.Node != null) _renderedArtworkOrder.Remove(entry.Node);
            _renderedArtworkPixels -= entry.Bitmap.Width * (long)entry.Bitmap.Height;
            entry.Bitmap.Dispose();
        }

        private bool DrawMediaContainBlack(Graphics g, Rectangle r, string? artPath)
        {
            string? displayPath = ResolveDisplayImagePath(artPath);
            if (string.IsNullOrWhiteSpace(displayPath))
                return false;

            Image? img = LoadImageForDisplay(displayPath, Math.Max(r.Width, r.Height));
            if (img == null)
                return false;

            Rectangle src = CoverSource(img.Size, r.Size, 0.5f, 0.5f);
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(img, r, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return true;
        }

        private static bool ShouldPreserveHeroAspect(Size source, Size target)
        {
            if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0)
                return false;

            float sourceAspect = source.Width / (float)source.Height;
            float targetAspect = target.Width / (float)target.Height;
            return targetAspect > sourceAspect * 1.55f;
        }

        // Segnaposto per i titoli senza immagine: fondo sobrio con una leggera tinta stabile
        // (ricavata dal titolo) e al centro l'icona della categoria, come nella sidebar.
        // Nessuna scritta: il titolo e' gia' nella card.
        private void DrawGeneratedArt(Graphics g, Rectangle r, string seed, bool hero, string? category = null)
        {
            if (r.Width <= 2 || r.Height <= 2) return;
            string text = string.IsNullOrWhiteSpace(seed) ? "?" : seed.Trim();
            uint hash = 2166136261;
            foreach (char c in text.ToLowerInvariant()) hash = (hash ^ c) * 16777619;
            double hue = hash % 360;
            Color Hsl(double h, double sat, double lum)
            {
                h = ((h % 360) + 360) % 360 / 360.0;
                double q = lum < .5 ? lum * (1 + sat) : lum + sat - lum * sat, p2 = 2 * lum - q;
                double Channel(double t) { t = t < 0 ? t + 1 : t > 1 ? t - 1 : t; return t < 1 / 6.0 ? p2 + (q - p2) * 6 * t : t < .5 ? q : t < 2 / 3.0 ? p2 + (q - p2) * (2 / 3.0 - t) * 6 : p2; }
                return Color.FromArgb((int)(Channel(h + 1 / 3.0) * 255), (int)(Channel(h) * 255), (int)(Channel(h - 1 / 3.0) * 255));
            }
            bool light = HUD.Theme.IsLight && !_darkSurfaceText;
            // Tinta unita: la sfumatura verticale su un fondo cosi' scuro si vedeva a gradini.
            using (var fill = new SolidBrush(light ? Hsl(hue, .10, .88) : Hsl(hue, .10, .13)))
                g.FillRectangle(fill, r);

            string icon = category switch
            {
                "Movies" => "movie",
                "TV Series" => "tv",
                "Music" => "music",
                "Photos" => "photo",
                "Videos" => "video",
                _ => "movie"
            };
            bool wide = r.Width > r.Height * 1.15f;
            int iconSize = Math.Max(14, (int)Math.Round(Math.Min(r.Width, r.Height) * (wide ? .30f : .24f)));
            // Locandine: icona sopra il centro (in basso c'e' la fascia con i dati della card).
            int cx = r.Left + r.Width / 2;
            int cy = wide || category == "Music" ? r.Top + r.Height / 2 : r.Top + (int)Math.Round(r.Height * .40f);
            int halo = (int)Math.Round(iconSize * 2.1f);
            var previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var disc = new SolidBrush(light ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(14, 255, 255, 255)))
                g.FillEllipse(disc, cx - halo / 2, cy - halo / 2, halo, halo);
            g.SmoothingMode = previous;
            Color ink = light ? Color.FromArgb(150, Hsl(hue, .20, .30)) : Color.FromArgb(120, 255, 255, 255);
            var iconRect = new Rectangle(cx - iconSize / 2, cy - iconSize / 2, iconSize, iconSize);
            if (!DrawIcon(g, iconRect, icon, ink))
                DrawLibraryFallbackGlyph(g, iconRect, icon, ink, Math.Max(.8f, iconSize / 24f));
        }
    }
}
