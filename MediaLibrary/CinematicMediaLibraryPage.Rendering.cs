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

            PaintLibrary(e.Graphics);
        }

        private void PaintLibrary(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool scrolling = _gridScrollAnimationTimer.Enabled;
            g.InterpolationMode = scrolling ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = scrolling ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            _hits.Clear();

            Rectangle b = ClientRectangle;
            if (b.Width <= 1 || b.Height <= 1)
                return;

            using (var fill = new SolidBrush(Back))
                g.FillRectangle(fill, b);

            // Sidebar leggermente piÃ¹ presente: prima a 1920px finiva intorno a 215px,
            // troppo sottile rispetto al blocco principale giÃ  scalato.
            int sidebarW = SidebarWidth(b);
            Rectangle sidebar = new Rectangle(0, 0, sidebarW, b.Height);
            Rectangle main = new Rectangle(sidebar.Right, 0, b.Width - sidebar.Right, b.Height);

            DrawMainBackground(g, main);
            DrawSidebar(g, sidebar);
            DrawMain(g, main);
            if (_contentLoading)
                DrawContentLoading(g, main);
            if (_detailItem != null)
                DrawDetailOverlay(g, b, _detailItem);
            if (_activeGroup != null)
                DrawGroupPicker(g, b);
            if (_diaryRatingItem != null)
                DrawDiaryRatingOverlay(g, b, _diaryRatingItem);
            DrawSurfaceEntrance(g);
            DrawQueueToast(g, b);
            LayoutSearchBox();
            LayoutWebInputBox();
        }

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

        private void DrawMainBackground(Graphics g, Rectangle main)
        {
            // Sfondo principale identico alla base del menu laterale: niente fasce/glow separati.
            using (var mainFill = new SolidBrush(NavBottom))
                g.FillRectangle(mainFill, main);
        }

        private void DrawSidebar(Graphics g, Rectangle r)
        {
            float scale = SidebarScale(r);

            using (var nav = new SolidBrush(Back))
                g.FillRectangle(nav, r);

            using var divider = new Pen(Color.FromArgb(92, HUD.Theme.Border));
            g.DrawLine(divider, r.Right - 1, r.Top, r.Right - 1, r.Bottom);

            DrawLogo(g, new Rectangle(r.Left + S(scale, 13), r.Top + S(scale, 10), r.Width - S(scale, 26), S(scale, 56)));
            int y = r.Top + S(scale, 80);
            DrawSectionLabel(g, r, L("LIBRERIA", "LIBRARY"), ref y);
            DrawNav(g, r, "Home", "Home", "home", ref y, selected: _view == PageView.Home);
            DrawNav(g, r, "Movies", L("Film", "Movies"), "movie", ref y, selected: _view == PageView.Collection && string.Equals(_category, "Movies", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "TV Series", L("Serie TV", "TV Series"), "tv", ref y, selected: _view == PageView.Collection && string.Equals(_category, "TV Series", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Videos", L("Video", "Videos"), "video", ref y, selected: _view == PageView.Collection && string.Equals(_category, "Videos", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Music", L("Musica", "Music"), "music", ref y, selected: _view != PageView.Home && string.Equals(_category, "Music", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Photos", L("Foto", "Photos"), "photo", ref y, selected: _view != PageView.Home && string.Equals(_category, "Photos", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Favourites", L("Preferiti", "Favourites"), "star", ref y, selected: _view != PageView.Home && string.Equals(_category, "Favourites", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "WatchHistory", L("Diario", "Diary"), "time", ref y, selected: _view != PageView.Home && string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Playlists", "Playlist", "playlist", ref y, selected: _view != PageView.Home && string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase));

            y += S(scale, 12);
            DrawRule(g, r, y);
            y += S(scale, 26);
            DrawSectionLabel(g, r, L("DISPOSITIVI", "DEVICES"), ref y);
            DrawNav(g, r, "Computer", "Computer", "computer", ref y, selected: string.Equals(_source, "Computer", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "Network", L("Rete", "Network"), "network", ref y, selected: _view == PageView.Network || string.Equals(_source, "Network", StringComparison.OrdinalIgnoreCase));

            y += S(scale, 12);
            DrawRule(g, r, y);
            y += S(scale, 26);
            DrawSectionLabel(g, r, "ONLINE", ref y);
            DrawNav(g, r, "YouTube", "YouTube", "youtube", ref y, selected: _view == PageView.WebInput && string.Equals(_webInputKind, "YouTube", StringComparison.OrdinalIgnoreCase));
            DrawNav(g, r, "URL", "URL", "link", ref y, selected: _view == PageView.WebInput && string.Equals(_webInputKind, "URL", StringComparison.OrdinalIgnoreCase));

            y += S(scale, 12);
            DrawRule(g, r, y);
            y += S(scale, 26);
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

            DrawHeader(g, content, scale);
            if (_view == PageView.Network)
            {
                DrawNetworkPage(g, content, scale);
                return;
            }

            if (_view == PageView.Collection)
            {
                DrawCollectionPage(g, content, scale);
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

        private void DrawCollectionGrid(Graphics g, Rectangle viewport, IReadOnlyList<LibraryItem> visible, float scale)
        {
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
            Rectangle info = new Rectangle(r.Left, media.Bottom, r.Width, infoH);
            int bleedH = Math.Max(S(scale, 50), Math.Min(S(scale, 86), (int)Math.Round(media.Height * 0.34)));

            using var path = Round(r, S(scale, 7));
            using (var fill = new LinearGradientBrush(r, Color.FromArgb(232, 4, 13, 21), Color.FromArgb(214, 1, 6, 11), LinearGradientMode.Vertical))
            using (var softBorder = new Pen(Color.FromArgb(34, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                g.DrawPath(softBorder, path);
            }

            using Region previousClip = g.Clip.Clone();
            g.SetClip(path, CombineMode.Intersect);

            string? art = photo ? item.ArtPath : (item.WideArtPath ?? item.ArtPath);
            bool mediaDrawn;
            if (photo)
            {
                mediaDrawn = DrawMediaContainBlack(g, media, art);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(art) && IsVideoPath(item.Path))
                    art = GetCachedVideoThumbnailPath(item.Path);
                mediaDrawn = DrawMediaContainBlack(g, media, art);
            }

            if (!mediaDrawn)
            {
                if (!photo && IsVideoPath(item.Path))
                    QueueVideoThumbnail(item.Path);
                DrawGeneratedArt(g, media, item.Title, hero: false, category: item.Category);
            }

            DrawPosterInfoBand(g, media, info, bleedH);

            g.Clip = previousClip;

            using (var border = new Pen(hover ? Accent : Color.FromArgb(72, 42, 56, 68), hover ? 2f : 1f))
                g.DrawPath(border, path);

            using var title = LibraryFont("Segoe UI Semibold", Math.Max(8.0f, 9.0f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(7.0f, 7.9f * scale));
            Rectangle titleRect = new Rectangle(info.Left + S(scale, 12), info.Top + S(scale, 9), info.Width - S(scale, 24), S(scale, 20));
            TextRenderer.DrawText(g, item.Title, title, titleRect, TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            string right = photo ? string.Empty : QualityLabel(item);
            int rightW = string.IsNullOrWhiteSpace(right)
                ? 0
                : Math.Min(info.Width / 2, Math.Max(S(scale, 42), TextRenderer.MeasureText(right, meta, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + S(scale, 6)));
            Rectangle metaRect = new Rectangle(titleRect.Left, titleRect.Bottom + S(scale, 2), Math.Max(S(scale, 20), titleRect.Width - rightW - S(scale, 10)), S(scale, 17));
            TextRenderer.DrawText(g, TemporalMetaText(item), meta, metaRect, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (!string.IsNullOrWhiteSpace(right))
            {
                TextRenderer.DrawText(g, right, meta, new Rectangle(info.Right - rightW - S(scale, 12), metaRect.Top, rightW, metaRect.Height), TextMain, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Poster, Item = item, Key = item.Path });
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
                return;

            int trackW = Math.Max(3, S(LayoutScale(viewport), 4));
            int offset = string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase)
                ? S(LayoutScale(viewport), 22)
                : S(LayoutScale(viewport), 28);
            Rectangle track = new Rectangle(viewport.Right + offset, viewport.Top, trackW, viewport.Height);
            int thumbH = Math.Max(28, (int)Math.Round(viewport.Height * (viewport.Height / (double)Math.Max(viewport.Height, contentH))));
            int thumbY = viewport.Top + (int)Math.Round((viewport.Height - thumbH) * (_gridScroll / (double)Math.Max(1, _gridScrollMax)));
            ScrollbarChrome.Draw(g, track, thumbY, thumbH);
        }

        private void DrawLogo(Graphics g, Rectangle r)
        {
            Image? assetLogo = HUD.SettingsHudPage.LoadBrandLogo() ?? LoadFirstLogoImage();
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
                g.DrawPath(border, path);
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
            using var font = LibraryFont("Segoe UI Semibold", 9.8f * scale);
            TextRenderer.DrawText(g, text, font, new Rectangle(sidebar.Left + S(scale, 30), y, sidebar.Width - S(scale, 60), S(scale, 21)), Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            y += S(scale, 31);
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

        private void DrawNav(Graphics g, Rectangle sidebar, string key, string label, string icon, ref int y, bool? selected = null)
        {
            float scale = SidebarScale(sidebar);
            bool isSelected = selected ?? string.Equals(_category, key, StringComparison.OrdinalIgnoreCase);
            Rectangle row = new Rectangle(sidebar.Left + S(scale, 14), y, sidebar.Width - S(scale, 28), S(scale, 40));
            bool hover = row.Contains(_lastMouse);
            if (isSelected || hover)
            {
                using var path = Round(row, S(scale, 3));
                using var fill = new SolidBrush(isSelected ? Color.FromArgb(58, Selection) : Color.FromArgb(20, TextMain));
                using var border = new Pen(isSelected ? Color.FromArgb(112, HUD.Theme.SubtleText) : Color.FromArgb(52, HUD.Theme.Border));
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            bool hasIcon = HasIconAsset(icon);
            int iconSize = S(scale, 20);
            if (hasIcon)
                DrawIcon(g, new Rectangle(row.Left + S(scale, 11), row.Top + (row.Height - iconSize) / 2, iconSize, iconSize), icon, isSelected ? Accent : HUD.Theme.SubtleText);

            using var font = LibraryFont("Segoe UI", 10.2f * scale);
            int textLeft = hasIcon ? row.Left + S(scale, 42) : row.Left + S(scale, 14);
            int textWidth = row.Right - textLeft - S(scale, 12);
            TextRenderer.DrawText(g, label, font, new Rectangle(textLeft, row.Top, textWidth, row.Height), isSelected ? TextMain : HUD.Theme.SubtleText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            _hits.Add(new HitZone { Bounds = row, Kind = HitKind.Nav, Key = key });
            y += S(scale, 44);
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
            int buttonsW = (playlists
                ? filterW + sortW + addW + buttonGap * 2
                : (favourites ? filterW + sortW + buttonGap : diary
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
                float searchFontSize = Math.Max(8.2f, Math.Min(12.0f, 9.0f * scale));
                if (Math.Abs(_searchBox.Font.Size - searchFontSize * (96f / 72f)) > 0.1f)
                { var old = _searchBox.Font; _searchBox.Font = LibraryFont("Segoe UI", searchFontSize); old.Dispose(); }
            }
            catch { }

            using (var path = Round(_searchShellRect, S(scale, 7)))
            using (var fill = new SolidBrush(Chrome))
            using (var border = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 150 : 82, HUD.Theme.Border)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            DrawIcon(g, new Rectangle(_searchShellRect.Left + S(scale, 13), _searchShellRect.Top + (_searchShellRect.Height - S(scale, 15)) / 2, S(scale, 15), S(scale, 15)), "search", Muted);

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
            else if (!playlists && !favourites)
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

            using var font = LibraryFont("Segoe UI Semibold", Math.Max(7.2f, 8.4f * scale));
            Size textSize = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            bool hasIcon = HasIconAsset(icon);
            int iconSize = S(scale, 14);
            int gap = hasIcon ? S(scale, 6) : 0;
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
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 342f));
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

            int left = r.Left + Math.Max(S(scale, 34), (int)Math.Round(r.Width * 0.042));
            int textW = Math.Min(S(scale, 640), Math.Max(S(scale, 300), (int)Math.Round(r.Width * 0.43)));
            textW = Math.Min(textW, Math.Max(S(scale, 220), r.Right - left - S(scale, 28)));
            int actionH = S(scale, 42);
            int actionTop = r.Bottom - S(scale, 21) - actionH;
            using var eyebrowFont = LibraryFont("Segoe UI Semibold", Math.Max(7.6f, 8.2f * scale));
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(22f, Math.Min(39f, 29f * scale)));
            int titleLineH = titleFont.Height + S(scale, 2);
            bool titleWraps = TextRenderer.MeasureText(g, _heroItem.Title, titleFont, new Size(8192, titleLineH), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width > textW;
            int titleH = titleWraps ? titleLineH * 2 : titleLineH;
            int contentEstimate = S(scale, 16 + 5 + 12 + 25 + 11 + 52 + 24) + titleH;
            int top = Math.Max(r.Top + S(scale, 17), r.Top + (actionTop - r.Top - contentEstimate) / 2);
            TextRenderer.DrawText(g, L("IN PRIMO PIANO", "FEATURED"), eyebrowFont,
                new Rectangle(left, top, textW, S(scale, 16)),
                Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int titleTop = top + S(scale, 21);
            Rectangle titleRect = new Rectangle(left, titleTop, textW, titleH);
            string fittedTitle = FitTextWithEllipsis(_heroItem.Title, titleFont, titleRect.Size,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, fittedTitle, titleFont, titleRect, TextMain,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int chipY = titleRect.Bottom + S(scale, 12);
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
            int castHeight = string.IsNullOrWhiteSpace(heroCast) || r.Height < S(scale, 270) ? 0 : S(scale, 20);
            int bodyTop = chipY + S(scale, 36);
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
            Rectangle moreButton = new Rectangle(detailsButton.Right + S(scale, 12), actionTop, S(scale, 46), actionH);
            DrawAction(g, resumeButton, hasResume ? L("Riprendi", "Resume") : L("Riproduci", "Play"), "play", true, HitKind.HeroResume, _heroItem);
            if (!hasResume) DrawAction(g, detailsButton, L("Dettagli", "Details"), string.Empty, false, HitKind.HeroDetails, _heroItem);
            if (hasResume)
            {
                var restart = new Rectangle(detailsButton.Left, detailsButton.Top, S(scale, 196), detailsButton.Height);
                DrawAction(g, restart, L("Ricomincia dall’inizio", "Start over"), "", false, HitKind.HeroRestart, _heroItem);
                detailsButton.X = restart.Right + S(scale, 12);
                DrawAction(g, detailsButton, L("Dettagli", "Details"), "", false, HitKind.HeroDetails, _heroItem);
                moreButton.X = detailsButton.Right + S(scale, 12);
            }
            DrawMoreButton(g, moreButton, _heroItem);
        }

        private void DrawHeroImageOverlays(Graphics g, Rectangle r)
        {
            Color heroBack = Back;
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
            using (var path = Round(r, S(scale, 7)))
            using (var fill = new SolidBrush(primary ? (hover ? Color.FromArgb(110, 0, 93, 150) : Color.FromArgb(86, 0, 72, 116)) : (hover ? Color.FromArgb(186, 27, 39, 52) : Color.FromArgb(152, 17, 26, 36))))
            using (var border = new Pen(primary ? Accent : Color.FromArgb(62, 54, 75, 90)))
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
                DrawIcon(g, new Rectangle(x, r.Top + (r.Height - iconSize) / 2, iconSize, iconSize), icon, primary ? Color.FromArgb(70, 202, 255) : Color.White);
                x += iconSize + gap;
            }
            int textW = Math.Max(S(scale, 20), Math.Min(textSize.Width + 2, r.Right - x - S(scale, 8)));
            TextRenderer.DrawText(g, text, font, new Rectangle(x, r.Top, textW, r.Height), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = kind, Item = item, Key = item.Path });
        }

        private void DrawMoreButton(Graphics g, Rectangle r, LibraryItem item)
        {
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 42f));
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, S(scale, 7)))
            using (var fill = new SolidBrush(hover ? Color.FromArgb(176, 27, 39, 52) : Color.FromArgb(142, 17, 26, 36)))
            using (var border = new Pen(Color.FromArgb(58, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            using var font = LibraryFont("Segoe UI Semibold", Math.Max(12f, 15f * scale));
            TextRenderer.DrawText(g, "...", font, new Rectangle(r.Left, r.Top - S(scale, 4), r.Width, r.Height), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.HeroMore, Item = item, Key = item.Path });
        }

        private static Font LibraryFont(string family, float size, FontStyle style = FontStyle.Regular)
            => new Font(family, size * (96f / 72f), style, GraphicsUnit.Pixel);

        private void DrawTitle(Graphics g, string text, Rectangle r, float size)
        {
            using var font = LibraryFont("Segoe UI Semibold", size);
            TextRenderer.DrawText(g, text, font, r, TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawSeeAll(Graphics g, Rectangle r)
        {
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, 7))
            using (var fill = new SolidBrush(hover ? Color.FromArgb(42, 13, 75, 112) : Color.FromArgb(22, 9, 32, 48)))
            using (var border = new Pen(hover ? Accent : Color.FromArgb(56, 44, 67, 82)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var font = LibraryFont("Segoe UI Semibold", 8.3f);
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

        private void DrawResumeCard(Graphics g, Rectangle r, ResumeItem resume)
        {
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 144f));
            bool hover = r.Contains(_lastMouse);
            Color shade = Back;
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
            g.ResetClip();
            using (var border = new Pen(hover ? Accent : Color.FromArgb(52, 255, 255, 255), hover ? 2f : 1f))
                g.DrawPath(border, path);

            using var title = LibraryFont("Segoe UI Semibold", Math.Max(7.8f, 8.5f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(7.3f, 8f * scale));
            TextRenderer.DrawText(g, resume.Item.Title, title, new Rectangle(r.Left + S(scale, 12), r.Bottom - S(scale, 52), r.Width - S(scale, 24), S(scale, 20)), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string left = L($"{FormatDuration(Math.Max(0, (resume.DurationSeconds - resume.PositionSeconds) / 60.0))} rimanenti", $"{FormatDuration(Math.Max(0, (resume.DurationSeconds - resume.PositionSeconds) / 60.0))} left");
            string pct = $"{resume.Progress * 100:0}%";
            Rectangle metaRect = new Rectangle(r.Left + S(scale, 12), r.Bottom - S(scale, 31), r.Width - S(scale, 24), S(scale, 17));
            TextRenderer.DrawText(g, left, meta, metaRect, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, pct, meta, metaRect, Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            DrawProgress(g, new Rectangle(r.Left + S(scale, 12), r.Bottom - S(scale, 11), r.Width - S(scale, 24), Math.Max(3, S(scale, 3))), resume.Progress);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Continue, Resume = resume, Key = resume.Item.Path });
        }

        private void DrawArrow(Graphics g, Rectangle r, bool left, bool enabled, HitKind? action = null)
        {
            float scale = Math.Max(0.88f, Math.Min(1.90f, r.Height / 46f));
            bool hover = enabled && r.Contains(_lastMouse);
            using var pen = new Pen(!enabled ? Color.FromArgb(55, 68, 80) : hover ? Accent : Color.White, Math.Max(1.7f, 1.9f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
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

            using Region previousClip = g.Clip.Clone();
            g.SetClip(path, CombineMode.Intersect);
            if (!DrawPosterArt(g, poster, item))
                DrawGeneratedArt(g, poster, item.Title, hero: false, category: item.Category);
            DrawPosterInfoBand(g, poster, info, bleedH);
            g.Clip = previousClip;

            using (var border = new Pen(hover ? Accent : Color.FromArgb(64, 42, 56, 68), hover ? 2f : 1f))
                g.DrawPath(border, path);

            if (rankingPosition.HasValue)
                DrawRankingPosition(g, poster, rankingPosition.Value, scale);

            using var title = LibraryFont("Segoe UI Semibold", Math.Max(7.8f, 8.8f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(7.2f, 8.1f * scale));
            Rectangle titleRect = new Rectangle(info.Left + S(scale, 12), info.Top + S(scale, 8), info.Width - S(scale, 24), S(scale, 20));
            TextRenderer.DrawText(g, item.Title, title, titleRect, TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            string quality = QualityLabel(item);
            int qW = Math.Min(info.Width - S(scale, 38), Math.Max(S(scale, 42), TextRenderer.MeasureText(quality, meta, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + S(scale, 4)));
            Rectangle metaRect = new Rectangle(info.Left + S(scale, 12), titleRect.Bottom + S(scale, 3), Math.Max(S(scale, 20), info.Width - qW - S(scale, 28)), S(scale, 17));
            TextRenderer.DrawText(g, CardMetaText(item), meta, metaRect, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, quality, meta, new Rectangle(info.Right - qW - S(scale, 12), titleRect.Bottom + S(scale, 3), qW, S(scale, 17)), TextMain, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Poster, Item = item, Key = item.Path });

            if (!item.IsGroup)
                DrawPosterQuickActions(g, r, item, scale, hover);
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
                using var border = new Pen(active ? BorderAccent : Color.FromArgb(92, 220, 232, 242));
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

        private void DrawPosterInfoBand(Graphics g, Rectangle poster, Rectangle info, int bleedH)
        {
            // Fascia dati separata dalla locandina, ma fusa con una vignettatura lunga.
            // Non c'Ã¨ uno stacco netto: il nero/blu sale dentro la cover in modo progressivo.
            Color band = Panel;
            Color deep = Back;

            int clampedBleed = Math.Min(Math.Max(bleedH, 1), poster.Height);
            Rectangle posterFade = new Rectangle(poster.Left, poster.Bottom - clampedBleed, poster.Width, clampedBleed);
            using (var fade = new LinearGradientBrush(posterFade, Color.Transparent, band, LinearGradientMode.Vertical))
            {
                fade.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.12f, 0.26f, 0.42f, 0.58f, 0.74f, 0.88f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(0, band),
                        Color.FromArgb(12, band),
                        Color.FromArgb(34, band),
                        Color.FromArgb(72, band),
                        Color.FromArgb(122, band),
                        Color.FromArgb(176, band),
                        Color.FromArgb(226, band),
                        Color.FromArgb(248, band)
                    }
                };
                g.FillRectangle(fade, posterFade);
            }

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

            using (var fill = new SolidBrush(band))
                g.FillRectangle(fill, info);

            int seamH = Math.Min(Math.Max(22, clampedBleed / 3), clampedBleed);
            Rectangle seam = new Rectangle(info.Left, info.Top - seamH, info.Width, seamH + info.Height);
            using (var seamFade = new LinearGradientBrush(seam, Color.FromArgb(0, band), band, LinearGradientMode.Vertical))
            {
                seamFade.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.30f, 0.58f, 0.80f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(0, band),
                        Color.FromArgb(70, band),
                        Color.FromArgb(160, band),
                        Color.FromArgb(224, band),
                        band
                    }
                };
                g.FillRectangle(seamFade, seam);
            }
            VisualDither.Overlay(g, Rectangle.Union(posterFade, info), HUD.Theme.IsLight ? 2 : 3);
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
                _ => "grid"
            };
            string titleText = IsNetworkSourceActive()
                ? L("Nessun contenuto in questa categoria", "No content in this category")
                : string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase)
                    ? L("Nessuna visione registrata", "No viewing history yet")
                    : L("Questa sezione è vuota", "This section is empty");
            string bodyText = IsNetworkSourceActive()
                ? L("Connetti un server dalla pagina Network o scegli un'altra categoria.", "Connect a server from Network or choose another category.")
                : string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase)
                    ? L("I film e gli episodi riprodotti compariranno qui automaticamente.", "Movies and episodes you play will appear here automatically.")
                    : L("Aggiungi una sorgente o avvia una scansione per popolare la libreria.", "Add a source or scan the library to populate it.");
            DrawMinimalEmptyState(g, r, iconName, titleText, bodyText);
        }

        private void DrawMinimalEmptyState(Graphics g, Rectangle r, string icon, string title, string body)
        {
            float scale = Math.Clamp(LayoutScale(ClientRectangle), .75f, 1.5f);
            int width = Math.Max(1, Math.Min(S(scale, 540), r.Width - 32));
            int x = r.Left + (r.Width - width) / 2, y = r.Top + Math.Max(0, (r.Height - S(scale, 120)) / 2);
            int side = S(scale, 28);
            DrawIcon(g, new Rectangle(r.Left + (r.Width - side) / 2, y, side, side), icon, Muted);
            using var titleFont = LibraryFont("Segoe UI Semibold", 12f * scale);
            using var bodyFont = LibraryFont("Segoe UI", 9.5f * scale);
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(x, y + S(scale, 44), width, S(scale, 27)), TextMain, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, body, bodyFont, new Rectangle(x, y + S(scale, 76), width, S(scale, 44)), Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        private void DrawStats(Graphics g, Rectangle r)
        {
            float scale = Math.Clamp(r.Height / 58f, .5f, 2f);
            Color statsText = Muted;
            Color statsDivider = Color.FromArgb(45, HUD.Theme.Border);
            using (var rule = new Pen(statsDivider)) g.DrawLine(rule, r.Left, r.Top, r.Right, r.Top);

            var cells = _statsCells.Count > 0
                ? _statsCells.ToList()
                : new List<(string Icon, string Text)> { ("grid", $"{_items.Count:N0} Items") };

            int w = Math.Max(1, r.Width / cells.Count);
            using var font = LibraryFont("Segoe UI", Math.Max(8.8f, 9.8f * scale));
            for (int i = 0; i < cells.Count; i++)
            {
                Rectangle cell = new Rectangle(r.Left + i * w, r.Top, i == cells.Count - 1 ? r.Right - (r.Left + i * w) : w, r.Height);
                bool hasIcon = HasIconAsset(cells[i].Icon);
                Size textSize = TextRenderer.MeasureText(cells[i].Text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                int iconSize = S(scale, 20);
                int gap = hasIcon ? S(scale, 10) : 0;
                int totalW = Math.Min(cell.Width - S(scale, 20), textSize.Width + (hasIcon ? iconSize + gap : 0));
                int x = cell.Left + (cell.Width - totalW) / 2;
                if (hasIcon)
                {
                    DrawIcon(g, new Rectangle(x, cell.Top + (cell.Height - iconSize) / 2, iconSize, iconSize), cells[i].Icon, Color.FromArgb(150, Accent));
                    x += iconSize + gap;
                }
                TextRenderer.DrawText(g, cells[i].Text, font, new Rectangle(x, cell.Top, Math.Max(S(scale, 20), cell.Right - x - S(scale, 8)), cell.Height), statsText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (i > 0)
                {
                    using var pen = new Pen(statsDivider);
                    g.DrawLine(pen, cell.Left, r.Top + S(scale, 10), cell.Left, r.Bottom - S(scale, 10));
                }
            }
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
            // Never stat every indexed file while the splash waits for Home.
            // A sleeping network share could otherwise hold startup for tens of
            // seconds just to render a storage statistic.
            long bytes = 0;
            int measured = 0;
            foreach (string path in all)
            {
                if (_itemCache.TryGetValue(path, out LibraryItem? cached) && cached.Bytes > 0)
                {
                    bytes += cached.Bytes;
                    measured++;
                }
            }

            var cells = new List<(string Icon, string Text)>
            {
                ("movie", L($"{movies:N0} film", $"{movies:N0} movies")),
                ("tv", L($"{tv:N0} serie TV", $"{tv:N0} TV shows"))
            };
            if (artists > 0)
                cells.Add(("music", L($"{artists:N0} artisti", $"{artists:N0} artists")));
            cells.Add(("storage", measured == all.Count && bytes > 0
                ? FormatBytes(bytes)
                : L("Dimensione non disponibile", "Size unavailable")));
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
            foreach (string? path in paths)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    string value = path.Trim();
                    if (File.Exists(value) || IsHttpUrl(value))
                        return value;
                }
                catch { }
            }
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

        private bool DrawWideArt(Graphics g, Rectangle r, LibraryItem item, bool hero)
        {
            if (hero)
            {
                // Hero: l'immagine parte sotto la vignettatura, non dal bordo sinistro.
                // Il bordo d'ingresso viene poi mascherato da una sfumatura dedicata:
                // cosÃ¬ non compare mai lo stacco netto anche con backdrop molto luminosi.
                Rectangle art = HeroArtworkRect(r);
                bool drawn = DrawImagePath(g, art, item.WideArtPath, requireLandscape: false, tint: true, preserveAspectWhenWide: false, horizontalFocus: 0.52f, verticalFocus: 0.23f);
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
            using (var entry = new LinearGradientBrush(fade, Back, Color.Transparent, LinearGradientMode.Horizontal))
            {
                entry.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.18f, 0.38f, 0.62f, 0.82f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(255, Back),
                        Color.FromArgb(244, Back),
                        Color.FromArgb(204, Back),
                        Color.FromArgb(128, Back),
                        Color.FromArgb(46, Back),
                        Color.FromArgb(0, Back)
                    }
                };
                g.FillRectangle(entry, fade);
            }

            // Piccola ombra verticale sopra/sotto l'area immagine per legarla al banner.
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

            // TileFlipXY evita che il campionamento bicubico prenda pixel trasparenti/chiari
            // fuori dall'immagine. Insieme al bleed elimina le righe sottili ai bordi.
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            {
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

        private void DrawGeneratedArt(Graphics g, Rectangle r, string seed, bool hero, string? category = null)
        {
            using var surface = new SolidBrush(HUD.Theme.Card);
            g.FillRectangle(surface, r);
            string icon = category ?? _category;
            int size = Math.Max(20, Math.Min(r.Width, r.Height) / (hero ? 5 : 3));
            DrawIcon(g, new Rectangle(r.Left + (r.Width-size)/2, r.Top + (r.Height-size)/2, size, size), icon, Color.FromArgb(95, 117, 136));
        }
    }
}
