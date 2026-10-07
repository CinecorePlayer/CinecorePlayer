#nullable enable
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
        public void OpenDetailsForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            LibraryItem? item = ToItem(path);
            if (item == null)
                return;
            ShowDetailOverlay(item);
        }

        public void OpenCastForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            LibraryItem? item = ToItem(path);
            if (item != null)
                ShowDetailCastOverlay(item);
        }

        private static bool HeroSupportsCast(LibraryItem? item)
            => item != null && !item.IsGroup && item.Category is "Movies" or "TV Series" or "Videos";

        private void ShowHeroMoreMenu(LibraryItem item, Rectangle anchor)
        {
            var options = new List<HUD.ChoicePopup.Option>
            {
                new HUD.ChoicePopup.Option(L("Cast", "Cast"), () => ShowDetailCastOverlay(item), Icon: "person"),
                new HUD.ChoicePopup.Option(L("Recensioni", "Reviews"), () => ShowDetailReviewsOverlay(item), Icon: "review")
            };
            if (CanEditMetadata(item))
                options.Add(new HUD.ChoicePopup.Option(L("Correggi titolo e copertina…", "Fix title and cover…"), () => EditItemMetadata(item), Icon: "edit"));
            HUD.ChoicePopup.Show(this, new Point(anchor.Left, anchor.Bottom + 6), options.ToArray(), width: 280);
        }

        // La correzione a mano vale per i film e i video sul computer: i titoli di un server di rete
        // arrivano dal server, gli episodi seguono la loro serie.
        private static bool CanEditMetadata(LibraryItem? item) =>
            item != null && !item.IsGroup && !string.IsNullOrWhiteSpace(item.Path) && !IsNetworkPath(item.Path) &&
            item.Category is "Movies" or "Videos";

        private void EditItemMetadata(LibraryItem item)
        {
            if (!CanEditMetadata(item)) return;
            bool changed;
            using (SheetPresenter.Layout(this))
            using (var dialog = new HUD.MetadataEditForm(item.Path, item.Title, item.Year, AppLanguage.English))
            {
                SheetPresenter.ShowDialog(dialog, FindForm());
                changed = dialog.Changed;
            }
            if (!changed) return;
            // Il titolo in elenco viene dalla scheda tenuta in memoria: va riletta con la correzione.
            _itemCache.TryRemove(item.Path, out _);
            if (_view == PageView.Home) ShowHomePage(); else ShowCategoryPage(_category, clearSearch: false);
        }

        private void ShowDetailReviewsOverlay(LibraryItem item)
        {
            ShowDetailOverlay(item);
            if (_detailItem == null)
                return;
            _detailReviewsVisible = true;
            _detailCastVisible = false;
            ResetReviewsPaging();
            QueueRichDetailResolve(_detailItem);
            if (!_detailItem.RichDetailsResolved)
                _contentLoadingTimer.Start();
            Invalidate();
        }

        private void ShowDetailCastOverlay(LibraryItem item)
        {
            ShowDetailOverlay(item);
            if (_detailItem == null)
                return;
            _detailCastVisible = true;
            _detailCastFirst = 0;
            QueueRichDetailResolve(_detailItem);
            QueueCastProfileDownloads(_detailItem.Path, _detailItem.CastMembers);
            if (!_detailItem.RichDetailsResolved)
                _contentLoadingTimer.Start();
            Invalidate();
        }

        private void ShowDetailOverlay(LibraryItem item)
        {
            if (item.IsGroup && item.Children.Count > 0)
            {
                ShowGroupPicker(item);
                return;
            }

            bool alreadyOpen = _detailItem != null;
            _detailItem = item;
            _surfaceEntranceTimer?.Stop();
            _surfaceEntrance = 1;
            // Stessa entrata della scheda album; passando da un consigliato all'altro la scheda resta ferma.
            if (!alreadyOpen) BeginSheetEntrance();
            _detailCastVisible = false;
            _detailReviewsVisible = false;
            _detailCastFirst = 0;
            QueueTmdbArtworkResolve(item, includeBackdrop: true);
            QueueRichDetailResolve(item);
            QueueVideoThumbnail(item.Path);
            QueueVideoQualityProbe(item.Path);
            try { _searchBox.Visible = false; } catch { }
            try { Focus(); } catch { }
            Invalidate();
        }

        private void CloseDetailOverlay(bool animate = true)
        {
            if (animate && _detailItem is { } closing)
            {
                var area = ClientRectangle;
                BeginSheetExit(DetailSheetBounds(SheetBounds(area)), layer => DrawDetailOverlay(layer, area, closing));
            }
            else
                EndSheetEntrance();
            _detailItem = null;
            DropDetailBackdrop();
            _detailCastVisible = false;
            _detailReviewsVisible = false;
            _detailCastFirst = 0;
            try { _searchBox.Visible = true; } catch { }
            Invalidate();
        }

        // "Riproduci" nella scheda parte dall'inizio (con la schermata pre-film): riprendere dal punto
        // lasciato e' compito della fila "Continua a guardare" e del pulsante Riprendi in alto.
        private void PlayDetailItem(bool resume)
        {
            if (_detailItem == null)
                return;

            var item = _detailItem;
            CloseDetailOverlay(animate: false);
            OpenItem(item, resume ? ResumeSecondsFor(item.Path) : null);
        }

        private static float DetailSheetScale(Rectangle bounds) =>
            Math.Clamp(Math.Min(bounds.Width / 1460f, bounds.Height / 950f), .45f, 4f);

        private static Rectangle DetailSheetBounds(Rectangle bounds)
        {
            float scale = DetailSheetScale(bounds);
            int maxModalW = Math.Max(1, bounds.Width - S(scale, 36));
            int maxModalH = Math.Max(1, bounds.Height - S(scale, 36));
            int modalW = Math.Min(maxModalW, S(scale, 1320));
            int modalH = Math.Min(maxModalH, S(scale, 860));
            modalW = Math.Max(Math.Min(maxModalW, S(scale, 1040)), modalW);
            modalH = Math.Max(Math.Min(maxModalH, S(scale, 700)), modalH);
            return new Rectangle(bounds.Left + (bounds.Width - modalW) / 2,
                bounds.Top + (bounds.Height - modalH) / 2, modalW, modalH);
        }

        private void DrawDetailOverlay(Graphics g, Rectangle bounds, LibraryItem item)
        {
            bool previousDarkText = _darkSurfaceText;
            _darkSurfaceText = true;
            try { DrawDetailOverlayCore(g, bounds, item); }
            finally { _darkSurfaceText = previousDarkText; }
        }

        private void DrawDetailOverlayCore(Graphics g, Rectangle bounds, LibraryItem item)
        {
            bounds.Height = Math.Max(1, bounds.Height - MusicTransportInset);
            bool musicDetail = string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase);
            if (musicDetail)
                QueueMusicQualityResolve(item);
            float scale = DetailSheetScale(bounds);
            Rectangle modal = DetailSheetBounds(bounds);
            // La velatura serve solo attorno alla scheda: sotto, la scheda e' opaca.
            using (var shade = new SolidBrush(Color.FromArgb(176, 0, 4, 8)))
            using (var around = new Region(bounds))
            {
                int corner = S(scale, 12);
                if (modal.Width > corner * 2 && modal.Height > corner * 2) around.Exclude(Rectangle.Inflate(modal, -corner, -corner));
                g.FillRegion(shade, around);
            }
            // The detail sheet is modal: this catch-all zone is added after the page
            // hits and before the sheet controls, so input can never reach the library below.
            _hits.Add(new HitZone { Bounds = bounds, Kind = HitKind.None, Key = "detail-modal-blocker" });

            using var modalPath = Round(modal, S(scale, 10));
            using Region oldClip = g.Clip.Clone();
            g.SetClip(modalPath, CombineMode.Intersect);
            DrawDetailBackdrop(g, modal, item);
            g.Clip = oldClip;
            using (var border = new Pen(Color.FromArgb(68, 205, 215, 224)))
                { } // senza contorno

            int margin = S(scale, 34);
            Rectangle close = new Rectangle(modal.Right - S(scale, 62), modal.Top + S(scale, 20), S(scale, 40), S(scale, 40));
            DrawModalClose(g, close, HitKind.DetailBack, scale);

            int posterW = S(scale, 204);
            int posterH = S(scale, 342);
            // Stesso margine sopra e sotto (prima 44 sopra e 24 sotto: tutto il contenuto
            // pendeva verso il basso). Se sotto avanza spazio (pochi o nessun consigliato)
            // il blocco intero si centra nella scheda.
            int edge = S(scale, 34);
            int modalBottom = modal.Bottom - edge;
            int cardGapRec = S(scale, 18);
            int posterTop = modal.Top + edge;
            bool lowerPanel = _detailCastVisible || _detailReviewsVisible;
            List<LibraryItem> recommended = lowerPanel ? new List<LibraryItem>() : BuildDetailRecommendations(item);
            int cardH = 0, recH = 0;
            if (!lowerPanel)
            {
                int room = modalBottom - (posterTop + posterH + S(scale, 18));
                // Minimo per i consigliati: etichetta + una riga di locandine.
                int minRecH = S(scale, 226);
                if (recommended.Count > 0 && room >= S(scale, 180) + cardGapRec + minRecH)
                {
                    cardH = S(scale, 180);
                    recH = room - cardH - cardGapRec;
                }
                else if (recommended.Count > 0 && room >= S(scale, 104) + cardGapRec + minRecH)
                {
                    cardH = room - cardGapRec - minRecH;
                    recH = minRecH;
                }
                else
                    cardH = Math.Min(S(scale, 180), Math.Max(S(scale, 96), room));
                int recUsed = recH > 0 ? MeasureDetailRecommendations(modal.Width - S(scale, 68), recH, scale).Used : 0;
                if (recUsed <= 0) recH = 0;
                int used = cardH + (recUsed > 0 ? cardGapRec + recUsed : 0);
                posterTop += Math.Max(0, (room - used) / 2);
            }
            Rectangle poster = new Rectangle(modal.Left + S(scale, 36), posterTop, posterW, posterH);
            using (var posterPath = Round(poster, S(scale, 6)))
            {
                using Region old = g.Clip.Clone();
                g.SetClip(posterPath, CombineMode.Intersect);
                if (!DrawPosterArt(g, poster, item))
                    DrawGeneratedArt(g, poster, item.Title, hero: false, category: item.Category);
                g.Clip = old;
                using var pen = new Pen(Color.FromArgb(86, 220, 232, 244), 1f);
                { } // senza contorno
            }

            int textX = poster.Right + S(scale, 32);
            int textW = Math.Max(S(scale, 360), modal.Right - textX - S(scale, 58));
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(22f, 27f * scale));
            using var subFont = LibraryFont("Segoe UI", Math.Max(8.7f, 9.6f * scale));
            using var bodyFont = LibraryFont("Segoe UI", Math.Max(8.4f, 9.2f * scale));
            string subtitle = CleanDetailSubtitle(item);
            bool hasSubtitle = !string.IsNullOrWhiteSpace(subtitle) && !string.Equals(subtitle, item.Title, StringComparison.OrdinalIgnoreCase);
            string overview = HeroText(item);
            int overviewW = Math.Min(textW, S(scale, 760));
            int overviewH = string.IsNullOrWhiteSpace(overview) ? 0 : Math.Min(S(scale, 88),
                TextRenderer.MeasureText(g, overview, bodyFont, new Size(overviewW, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
            // Il blocco di testo e' centrato sulla locandina: niente vuoto in alto, niente peso in basso.
            int blockH = S(scale, 42) + (hasSubtitle ? S(scale, 28) : 0) + S(scale, 42) + overviewH + S(scale, 26) + S(scale, 44);
            int y = poster.Top + Math.Max(0, (posterH - blockH) / 2);
            TextRenderer.DrawText(g, item.Title, titleFont, new Rectangle(textX, y, textW, S(scale, 46)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += S(scale, 42);

            if (hasSubtitle)
            {
                TextRenderer.DrawText(g, subtitle, subFont, new Rectangle(textX, y, textW, S(scale, 22)), Color.FromArgb(204, 216, 226),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                y += S(scale, 28);
            }

            DrawDetailChips(g, textX, y, textW, item, scale);
            y += S(scale, 42);

            Rectangle overviewRect = new Rectangle(textX, y, overviewW, S(scale, 88));
            TextRenderer.DrawText(g, FitTextWithEllipsis(overview, bodyFont, overviewRect.Size, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding),
                bodyFont, overviewRect, Color.FromArgb(214, 225, 234), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            y += overviewH + S(scale, 26);

            int actionGap = S(scale, 8);
            int iconButtonW = S(scale, 44);
            int playW = Math.Min(S(scale, 146), Math.Max(S(scale, 106), textW / 3));
            int reviewsW = S(scale, 150);
            int castW = S(scale, 126);
            int queueW = S(scale, 188);
            Rectangle play = new Rectangle(textX, y, playW, S(scale, 44));
            Rectangle queue = new Rectangle(play.Right + actionGap, y, queueW, S(scale, 44));
            Rectangle reviewsButton = new Rectangle(queue.Right + actionGap, y, reviewsW, S(scale, 44));
            Rectangle castButton = new Rectangle(reviewsButton.Right + actionGap, y, castW, S(scale, 44));
            Rectangle playlistButton = new Rectangle(castButton.Right + actionGap, y, iconButtonW, S(scale, 44));
            Rectangle fav = new Rectangle(playlistButton.Right + actionGap, y, iconButtonW, S(scale, 44));
            DrawDetailButton(g, play, L("Riproduci", "Play"), "play", true, HitKind.DetailPlay, scale);
            DrawDetailButton(g, queue, L("Aggiungi alla coda", "Add to queue"), "add", false, HitKind.DetailQueue, scale);
            DrawDetailButton(g, reviewsButton, L("Vedi recensioni", "View reviews"), "info", _detailReviewsVisible, HitKind.DetailReviews, scale);
            DrawDetailButton(g, castButton, L("Vedi cast", "View cast"), "person", _detailCastVisible, HitKind.DetailCast, scale);
            DrawDetailButton(g, playlistButton, string.Empty, "playlist", false, HitKind.DetailPlaylist, scale);
            DrawDetailButton(g, fav, string.Empty, "star", IsFavorite(item.Path), HitKind.DetailFavorite, scale);

            int actionBottom = y + S(scale, 44);

            int cardTop = Math.Max(poster.Bottom + S(scale, 18), actionBottom + S(scale, 20));
            int gap = S(scale, 12);

            if (_detailCastVisible)
            {
                DrawDetailCast(g, new Rectangle(modal.Left + S(scale, 34), cardTop, modal.Width - S(scale, 68), Math.Max(1, modalBottom - cardTop)), item, scale);
                return;
            }

            if (_detailReviewsVisible)
            {
                DrawDetailReviews(g, new Rectangle(modal.Left + S(scale, 34), cardTop, modal.Width - S(scale, 68), Math.Max(1, modalBottom - cardTop)), item, scale);
                return;
            }

            // Altezze di schede e consigliati gia' decise in alto (servono a centrare il blocco).
            cardH = Math.Max(S(scale, 96), Math.Min(cardH, modalBottom - cardTop));
            int recTop = cardTop + cardH + cardGapRec;
            recH = Math.Min(recH, modalBottom - recTop);

            int cardW = Math.Max(S(scale, 190), (modal.Width - S(scale, 68) - gap * 3) / 4);
            int cardX = modal.Left + S(scale, 34);
            DrawDetailInfoCard(g, new Rectangle(cardX, cardTop, cardW, cardH), "movie", item.PlaybackCategory,
                new[] { (L("Titolo", "Title"), item.Title), (L("Generi", "Genres"), GenreText(item)), (L("Anno", "Year"), item.Year?.ToString(CultureInfo.InvariantCulture) ?? FormatItemDate(item.SortDateUtc)), (L("Categoria", "Category"), item.PlaybackCategory), (L("Durata", "Duration"), DurationText(item)) }, scale);
            DrawDetailInfoCard(g, new Rectangle(cardX + (cardW + gap), cardTop, cardW, cardH), "info", "Info",
                new[] { (L("Risoluzione", "Resolution"), FirstRawNonEmpty(item.ResolutionLabel, L("In analisi", "Scanning"))), (L("Formato", "Format"), VideoFormatLabel(item.Path)), ("HDR", item.IsHdr ? "HDR" : "SDR"), (L("Qualita", "Quality"), QualityLabel(item)), (L("Dimensione", "Size"), ItemSizeText(item)) }, scale);
            DrawDetailInfoCard(g, new Rectangle(cardX + (cardW + gap) * 2, cardTop, cardW, cardH), "audio", L("Audio", "Audio"),
                new[]
                {
                    (L("Qualità", "Quality"), musicDetail ? MusicQualityLabel(item) : FirstRawNonEmpty(item.AudioLabel, L("In analisi", "Scanning"))),
                    (L("Audio a oggetti", "Object audio"), item.HasAtmos ? L("Rilevato", "Detected") : L("No", "No")),
                    (L("Durata", "Duration"), DurationText(item))
                }, scale);
            DrawDetailInfoCard(g, new Rectangle(cardX + (cardW + gap) * 3, cardTop, cardW, cardH), "folder", "File",
                new[] { (L("Nome", "Name"), ItemFileName(item)), (L("Percorso", "Path"), ItemLocation(item)), (L("Dimensione", "Size"), ItemSizeText(item)), (L("Modificato", "Modified"), FormatFileModified(item.Path)) }, scale);

            if (recH >= S(scale, 82))
            {
                Rectangle rec = new Rectangle(modal.Left + S(scale, 34), recTop, modal.Width - S(scale, 68), recH);
                DrawDetailRecommendations(g, rec, recommended, scale);
            }
        }

        // Fondo della scheda (immagine, velature e grana): non cambia finche' la scheda resta
        // aperta, ma ridisegnarlo costava circa 50 ms a ogni ridisegno (anche solo per il
        // passaggio del mouse su un pulsante). Si compone una volta e poi si copia.
        private Bitmap? _detailBackdrop;
        private (string Item, Size Size, Image? Source, bool Light) _detailBackdropKey;

        private void DropDetailBackdrop()
        {
            _detailBackdrop?.Dispose();
            _detailBackdrop = null;
        }

        private void DrawDetailBackdrop(Graphics g, Rectangle modal, LibraryItem item)
        {
            string art = FirstRawNonEmpty(item.WideArtPath, item.ArtPath);
            string? display = ResolveDisplayImagePath(art);
            // La stessa immagine che DrawImagePath userebbe: quando finisce di caricarsi cambia e il fondo si rifa'.
            Image? source = string.IsNullOrWhiteSpace(display) ? null : LoadImageForDisplay(display, Math.Max(modal.Width, modal.Height));
            var key = (item.Path + "|" + item.Title, modal.Size, source, HUD.Theme.IsLight);
            if (_detailBackdrop == null || !key.Equals(_detailBackdropKey))
            {
                DropDetailBackdrop();
                var composed = new Bitmap(modal.Width, modal.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    using var bg = Graphics.FromImage(composed);
                    bg.SmoothingMode = g.SmoothingMode;
                    bg.InterpolationMode = g.InterpolationMode;
                    bg.PixelOffsetMode = g.PixelOffsetMode;
                    bg.TextRenderingHint = g.TextRenderingHint;
                    bg.TranslateTransform(-modal.X, -modal.Y);
                    PaintDetailBackdrop(bg, modal, item, art);
                }
                catch { composed.Dispose(); throw; }
                _detailBackdrop = composed;
                _detailBackdropKey = key;
            }
            var mode = g.InterpolationMode;
            var offset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_detailBackdrop, modal.X, modal.Y, modal.Width, modal.Height);
            g.InterpolationMode = mode;
            g.PixelOffsetMode = offset;
        }

        private void PaintDetailBackdrop(Graphics g, Rectangle modal, LibraryItem item, string art)
        {
            bool backdrop = DrawImagePath(g, modal, art, requireLandscape: false, tint: false, preserveAspectWhenWide: false, verticalFocus: 0.34f);
            if (!backdrop)
                DrawGeneratedArt(g, modal, item.Title, hero: true, category: item.Category);

            using (var dim = new SolidBrush(Color.FromArgb(204, 0, 5, 10)))
                g.FillRectangle(dim, modal);
            using (var left = new LinearGradientBrush(modal, Color.FromArgb(244, 0, 5, 10), Color.FromArgb(18, 0, 5, 10), LinearGradientMode.Horizontal))
            {
                left.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.22f, 0.48f, 0.72f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(244, 0, 5, 10),
                        Color.FromArgb(226, 0, 5, 10),
                        Color.FromArgb(156, 0, 5, 10),
                        Color.FromArgb(76, 0, 5, 10),
                        Color.FromArgb(18, 0, 5, 10)
                    }
                };
                g.FillRectangle(left, modal);
            }
            using (var bottom = new LinearGradientBrush(modal, Color.Transparent, Color.FromArgb(226, 0, 4, 8), LinearGradientMode.Vertical))
            {
                bottom.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.50f, 0.78f, 1f },
                    Colors = new[] { Color.FromArgb(8, 0, 4, 8), Color.FromArgb(34, 0, 4, 8), Color.FromArgb(148, 0, 4, 8), Color.FromArgb(226, 0, 4, 8) }
                };
                g.FillRectangle(bottom, modal);
            }
            VisualDither.Overlay(g, modal, HUD.Theme.IsLight ? 2 : 3);
        }

        private void DrawDetailChips(Graphics g, int x, int y, int maxW, LibraryItem item, float scale)
        {
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(7.2f, 8.0f * scale));
            var chips = new List<(string Text, string Icon)>
            {
                (item.Year?.ToString(CultureInfo.InvariantCulture) ?? FormatItemDate(item.SortDateUtc), "calendar"),
                (DurationText(item), "time"),
                (QualityLabel(item), "quality")
            };
            int cx = x;
            foreach (var chip in chips.Where(c => !string.IsNullOrWhiteSpace(c.Text)))
            {
                int w = Math.Min(S(scale, 126), Math.Max(S(scale, 58), TextRenderer.MeasureText(chip.Text, font).Width + S(scale, 30)));
                if (cx + w > x + maxW)
                    break;
                DrawDetailChip(g, new Rectangle(cx, y, w, S(scale, 22)), chip.Icon, chip.Text, scale);
                cx += w + S(scale, 10);
            }
        }

        private void DrawDiaryDetailRating(Graphics g, Rectangle r, LibraryItem item, float scale)
        {
            double rating = GetDiaryRating(item.Path);
            using var labelFont = LibraryFont("Segoe UI Semibold", Math.Max(7.6f, 8.4f * scale));
            TextRenderer.DrawText(g, L("La tua valutazione", "Your rating"), labelFont,
                new Rectangle(r.Left, r.Top, S(scale, 118), r.Height), Color.FromArgb(188, 201, 212),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int x = r.Left + S(scale, 128);
            int gap = S(scale, 7);
            int star = Math.Min(r.Height - S(scale, 8), S(scale, 25));
            for (int value = 1; value <= 5; value++)
            {
                Rectangle choice = new Rectangle(x, r.Top + (r.Height - star) / 2, star, star);
                DrawDiaryStarFill(g, choice, Math.Clamp(rating - (value - 1), 0, 1));
                int half = Math.Max(1, choice.Width / 2);
                _hits.Add(new HitZone { Bounds = new Rectangle(choice.Left, choice.Top, half, choice.Height), Kind = HitKind.DiaryRate, Key = item.Path, Index = value * 2 - 1, Item = item });
                _hits.Add(new HitZone { Bounds = new Rectangle(choice.Left + half, choice.Top, choice.Width - half, choice.Height), Kind = HitKind.DiaryRate, Key = item.Path, Index = value * 2, Item = item });
                x += star + gap;
            }

            using var valueFont = LibraryFont("Segoe UI Semibold", Math.Max(8.0f, 9.0f * scale));
            TextRenderer.DrawText(g, rating > 0 ? FormatDiaryRating(rating) : L("Non valutato", "Not rated"), valueFont,
                new Rectangle(x + S(scale, 5), r.Top, Math.Max(1, r.Right - x - S(scale, 5)), r.Height),
                rating > 0 ? Color.FromArgb(244, 208, 126) : Color.FromArgb(148, 168, 185),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawDiaryRatingOverlay(Graphics g, Rectangle bounds, LibraryItem item)
        {
            float scale = Math.Max(.82f, Math.Min(1.45f, Math.Min(bounds.Width / 1680f, bounds.Height / 900f)));
            using (var veil = new SolidBrush(Color.FromArgb(214, 0, 4, 8)))
                g.FillRectangle(veil, bounds);

            int w = Math.Min(bounds.Width - S(scale, 48), S(scale, 620));
            int h = Math.Min(bounds.Height - S(scale, 48), S(scale, 300));
            Rectangle modal = new(bounds.Left + (bounds.Width - w) / 2, bounds.Top + (bounds.Height - h) / 2, w, h);
            using (var fill = new SolidBrush(Color.FromArgb(250, 7, 15, 23)))
            using (var border = new Pen(Color.FromArgb(76, HUD.Theme.Border), 1f))
            using (var path = Round(modal, S(scale, 8)))
            { g.FillPath(fill, path); }

            int pad = S(scale, 28);
            using var eyebrow = LibraryFont("Segoe UI Semibold", Math.Max(7.8f, 8.5f * scale));
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(15f, 18f * scale));
            TextRenderer.DrawText(g, L("VALUTA", "RATE"), eyebrow, new Rectangle(modal.Left + pad, modal.Top + S(scale, 24), modal.Width - pad * 2, S(scale, 20)), Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, item.Title, title, new Rectangle(modal.Left + pad, modal.Top + S(scale, 48), modal.Width - pad * 2 - S(scale, 38), S(scale, 44)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            Rectangle close = new(modal.Right - pad - S(scale, 40), modal.Top + S(scale, 20), S(scale, 40), S(scale, 40));
            DrawModalClose(g, close, HitKind.DiaryClose, scale);

            double current = GetDiaryRating(item.Path);
            int gap = S(scale, 12);
            int star = S(scale, 58);
            int total = star * 5 + gap * 4;
            int x = modal.Left + (modal.Width - total) / 2;
            int y = modal.Top + S(scale, 122);
            double preview = current;
            bool previewing = false;
            for (int value = 1; value <= 5; value++)
            {
                Rectangle probe = new(x + (value - 1) * (star + gap), y, star, star);
                if (!probe.Contains(_lastMouse))
                    continue;
                preview = value - 1 + (_lastMouse.X < probe.Left + probe.Width / 2 ? .5 : 1.0);
                previewing = true;
                break;
            }
            double displayRating = previewing ? preview : current;
            for (int value = 1; value <= 5; value++)
            {
                Rectangle choice = new(x + (value - 1) * (star + gap), y, star, star);
                bool hover = choice.Contains(_lastMouse);
                if (hover)
                {
                    using var halo = new SolidBrush(Color.FromArgb(24, Accent));
                    g.FillEllipse(halo, Rectangle.Inflate(choice, -S(scale, 3), -S(scale, 3)));
                }
                Rectangle icon = Rectangle.Inflate(choice, -S(scale, 8), -S(scale, 8));
                DrawDiaryStarFill(g, icon, Math.Clamp(displayRating - (value - 1), 0, 1));
                int half = Math.Max(1, choice.Width / 2);
                _hits.Add(new HitZone { Bounds = new Rectangle(choice.Left, choice.Top, half, choice.Height), Kind = HitKind.DiaryRate, Key = item.Path, Index = value * 2 - 1, Item = item });
                _hits.Add(new HitZone { Bounds = new Rectangle(choice.Left + half, choice.Top, choice.Width - half, choice.Height), Kind = HitKind.DiaryRate, Key = item.Path, Index = value * 2, Item = item });
            }

            using var scoreFont = LibraryFont("Segoe UI Semibold", Math.Max(10f, 11.5f * scale));
            string score = displayRating > 0 ? FormatDiaryRating(displayRating) : L("Nessuna valutazione", "No rating");
            TextRenderer.DrawText(g, score, scoreFont,
                new Rectangle(modal.Left + pad, y + star + S(scale, 13), modal.Width - pad * 2, S(scale, 28)),
                displayRating > 0 ? Color.FromArgb(245, 211, 133) : Color.FromArgb(155, 175, 192),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            Rectangle clear = new(modal.Left + pad, modal.Bottom - S(scale, 58), S(scale, 150), S(scale, 32));
            using var clearFont = LibraryFont("Segoe UI", Math.Max(7.8f, 8.6f * scale));
            TextRenderer.DrawText(g, L("Rimuovi valutazione", "Clear rating"), clearFont, clear, Color.FromArgb(160, 186, 203, 216),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = clear, Kind = HitKind.DiaryRate, Key = item.Path, Index = 0, Item = item });
        }

        private void DrawDiaryStarFill(Graphics g, Rectangle bounds, double fill)
        {
            // Le sole frazioni valide sono vuota, mezza e piena: niente residui
            // di antialias provenienti dalla sovrapposizione di due SVG diversi.
            fill = fill >= .75 ? 1d : fill >= .25 ? .5d : 0d;
            using GraphicsPath star = CreateDiaryStarPath(bounds);
            Color empty = Color.FromArgb(54, 151, 169, 184);
            using (var emptyBrush = new SolidBrush(empty))
                g.FillPath(emptyBrush, star);

            if (fill > 0)
            {
                GraphicsState state = g.Save();
                try
                {
                    int width = fill >= 1d ? bounds.Width : Math.Max(1, bounds.Width / 2);
                    g.SetClip(new Rectangle(bounds.Left, bounds.Top, width, bounds.Height), CombineMode.Intersect);
                    using var selectedBrush = new SolidBrush(Color.FromArgb(247, 197, 78));
                    g.FillPath(selectedBrush, star);
                }
                finally { g.Restore(state); }
            }

            using var outline = new Pen(fill > 0 ? Color.FromArgb(224, 223, 178, 72) : Color.FromArgb(126, 170, 187, 201), 1.15f);
            g.DrawPath(outline, star);
        }

        private static GraphicsPath CreateDiaryStarPath(Rectangle bounds)
        {
            var path = new GraphicsPath();
            float cx = bounds.Left + bounds.Width / 2f;
            float cy = bounds.Top + bounds.Height / 2f;
            float outer = Math.Max(2f, Math.Min(bounds.Width, bounds.Height) / 2f - 1.2f);
            float inner = outer * .45f;
            var points = new PointF[10];
            for (int i = 0; i < points.Length; i++)
            {
                double angle = -Math.PI / 2d + i * Math.PI / 5d;
                float radius = (i & 1) == 0 ? outer : inner;
                points[i] = new PointF(cx + (float)Math.Cos(angle) * radius, cy + (float)Math.Sin(angle) * radius);
            }
            path.AddPolygon(points);
            path.CloseFigure();
            return path;
        }

        private void DrawDetailChip(Graphics g, Rectangle r, string icon, string text, float scale)
        {
            Rectangle iconRect = new Rectangle(r.Left, r.Top + S(scale, 3), S(scale, 15), S(scale, 15));
            if (!DrawIcon(g, iconRect, icon, Color.FromArgb(178, 198, 218)))
                DrawLibraryFallbackGlyph(g, iconRect, icon, Color.FromArgb(178, 198, 218), scale);
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(7.2f, 8.0f * scale));
            TextRenderer.DrawText(g, text, font, new Rectangle(r.Left + S(scale, 20), r.Top, r.Width - S(scale, 20), r.Height), Color.FromArgb(224, 232, 240),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawDetailButton(Graphics g, Rectangle r, string text, string icon, bool primary, HitKind kind, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, S(scale, 6)))
            using (Brush fill = primary
                ? new LinearGradientBrush(r, hover ? Color.FromArgb(0, 126, 232) : Color.FromArgb(0, 102, 210), hover ? Color.FromArgb(0, 78, 178) : Color.FromArgb(0, 70, 156), LinearGradientMode.Vertical)
                : new SolidBrush(hover ? Color.FromArgb(82, 16, 32, 48) : Color.FromArgb(52, 8, 20, 32)))
            using (var border = new Pen(primary ? Color.FromArgb(190, 0, 154, 255) : Color.Transparent))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            int iconSize = S(scale, 17);
            if (string.IsNullOrWhiteSpace(text))
            {
                int iconX = r.Left + (r.Width - iconSize) / 2;
                Rectangle iconRect = new Rectangle(iconX, r.Top + (r.Height - iconSize) / 2, iconSize, iconSize);
                if (!DrawIcon(g, iconRect, icon, Color.White))
                    DrawLibraryFallbackGlyph(g, iconRect, icon, Color.White, scale);
            }
            else
            {
                using var font = LibraryFont("Segoe UI Semibold", Math.Max(8.2f, 9.5f * scale));
                int gap = S(scale, 9);
                int textWidth = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
                int groupWidth = iconSize + gap + textWidth;
                int iconX = r.Left + Math.Max(S(scale, 8), (r.Width - groupWidth) / 2);
                Rectangle iconRect = new Rectangle(iconX, r.Top + (r.Height - iconSize) / 2, iconSize, iconSize);
                if (!DrawIcon(g, iconRect, icon, Color.White))
                    DrawLibraryFallbackGlyph(g, iconRect, icon, Color.White, scale);
                TextRenderer.DrawText(g, text, font, new Rectangle(iconRect.Right + gap, r.Top, Math.Max(1, r.Right - iconRect.Right - gap - S(scale, 8)), r.Height),
                    TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            _hits.Add(new HitZone { Bounds = r, Kind = kind });
        }

        private void DrawDetailRoundIconButton(Graphics g, Rectangle r, string icon, HitKind kind, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, r.Width / 2))
            using (var fill = new SolidBrush(hover ? Color.FromArgb(98, 14, 26, 38) : Color.FromArgb(70, 4, 12, 20)))
            using (var border = new Pen(Color.FromArgb(52, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                { } // senza contorno
            }
            DrawIcon(g, Rectangle.Inflate(r, -S(scale, 12), -S(scale, 12)), icon, Color.White);
            if (kind != HitKind.None)
                _hits.Add(new HitZone { Bounds = r, Kind = kind });
        }

        private void DrawModalClose(Graphics g, Rectangle bounds, HitKind kind, float scale)
        {
            HUD.Theme.DrawCloseButton(g, bounds, bounds.Contains(_lastMouse), _darkSurfaceText);
            _hits.Add(new HitZone { Bounds = bounds, Kind = kind });
        }

        private void DrawDetailInfoCard(Graphics g, Rectangle r, string icon, string title, IReadOnlyList<(string Label, string Value)> rows, float scale)
        {
            using (var divider = new Pen(Color.FromArgb(55, 116,142,164)))
                g.DrawLine(divider,r.Left,r.Top,r.Right,r.Top);
            int pad = S(scale, 14);
            Rectangle iconRect = new Rectangle(r.Left + pad, r.Top + S(scale, 14), S(scale, 18), S(scale, 18));
            if (!DrawIcon(g, iconRect, icon, Color.FromArgb(82, 164, 255)))
                DrawLibraryFallbackGlyph(g, iconRect, icon, Color.FromArgb(82, 164, 255), scale);
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(8.6f, 9.4f * scale));
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(r.Left + pad + S(scale, 28), r.Top + S(scale, 10), r.Width - pad * 2 - S(scale, 28), S(scale, 28)),
                TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            using var rowFont = LibraryFont("Segoe UI", Math.Max(6.9f, 7.7f * scale));
            int y = r.Top + S(scale, 46);
            int rowH = S(scale, 22);
            int rowGap = S(scale, 3);
            int labelW = Math.Min(S(scale, 94), Math.Max(S(scale, 70), (int)Math.Round(r.Width * 0.36)));
            int valueX = r.Left + pad + labelW + S(scale, 12);
            int valueW = Math.Max(1, r.Right - pad - valueX);
            using Region oldClip = g.Clip.Clone();
            using Region clip = new Region(r);
            g.SetClip(clip, CombineMode.Intersect);
            foreach (var row in rows)
            {
                if (y + rowH > r.Bottom - S(scale, 10))
                    break;

                TextRenderer.DrawText(g, row.Label, rowFont, new Rectangle(r.Left + pad, y, labelW, rowH), Color.FromArgb(146, 162, 178),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, FirstRawNonEmpty(row.Value, "-"), rowFont, new Rectangle(valueX, y, valueW, rowH), Color.FromArgb(220, 229, 238),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                y += rowH + rowGap;
            }
            g.Clip = oldClip;
        }

        private static bool AudioLooksPassthrough(string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;
            return Regex.IsMatch(label, @"\b(truehd|dts[- ]?hd|dts:x|atmos|e-ac-3|dolby digital plus)\b", RegexOptions.IgnoreCase);
        }

        private static string RecommendationFamily(LibraryItem item)
        {
            if (string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.PlaybackCategory, "Musica", StringComparison.OrdinalIgnoreCase))
                return "audio";
            if (string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.PlaybackCategory, "Foto", StringComparison.OrdinalIgnoreCase))
                return "photo";
            return "video";
        }

        /// <summary>Misura della riga dei consigliati in un'area data: larghezza e altezza delle
        /// locandine e altezza usata (etichetta compresa); Used = 0 se non c'e' posto.</summary>
        private static (int CardW, int CardH, int Used) MeasureDetailRecommendations(int width, int height, float scale)
        {
            int label = S(scale, 32);
            int availableH = height - label;
            if (width <= S(scale, 120) || availableH < S(scale, 150))
                return (0, 0, 0);
            int gap = S(scale, 14);
            int maxCardWByHeight = Math.Max(S(scale, 92), (int)Math.Floor(availableH / 1.58f));
            int widthFit = Math.Max(S(scale, 100), (width - gap * 5) / 6);
            int cardW = Math.Min(S(scale, 176), Math.Min(widthFit, maxCardWByHeight));
            cardW = Math.Max(S(scale, 112), cardW);
            int cardH = Math.Min(availableH, Math.Max(S(scale, 178), (int)Math.Round(cardW * 1.58)));
            return (cardW, cardH, label + cardH);
        }

        // Film affini secondo Trakt, gia' ridotti a quelli presenti in libreria: percorso del film -> percorsi affini
        // (null mentre la richiesta e' in corso). Non serve un account Trakt collegato.
        private readonly Dictionary<string, List<string>?> _traktRelated = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<TraktLibrary.LibraryTitle>? _traktRelatedLibrary;

        private List<string>? TraktRelatedFor(LibraryItem current)
        {
            if (current.TmdbId is not int tmdbId || !string.Equals(current.Category, "Movies", StringComparison.OrdinalIgnoreCase))
                return null;
            if (_traktRelated.TryGetValue(current.Path, out var known)) return known;
            _traktRelated[current.Path] = null;
            var library = _traktRelatedLibrary ??= CollectTitlesForRecommendations();
            string path = current.Path;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                List<string> related = new();
                try
                {
                    using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                    related = await TraktLibrary.RelatedInLibraryAsync(tmdbId, library, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { Dbg.Warn("[TRAKT] related: " + ex.Message); }
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        _traktRelated[path] = related;
                        if (related.Count > 0 && _detailItem != null && string.Equals(_detailItem.Path, path, StringComparison.OrdinalIgnoreCase)) Invalidate();
                    }));
                }
                catch { }
            });
            return null;
        }

        private List<LibraryItem> BuildDetailRecommendations(LibraryItem current)
        {
            QueueGenreResolve(current);
            var traktRelated = TraktRelatedFor(current);
            var currentGenres = GenreSetFor(current);
            var flattened = _items
                .Concat(_itemCache.Values)
                .SelectMany(candidate => candidate.IsGroup ? candidate.Children.AsEnumerable() : Enumerable.Repeat(candidate, 1))
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Path))
                .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                // La stessa edizione può comparire da cartelle/sorgenti diverse: non
                // deve occupare più slot nei consigli.
                .GroupBy(candidate => $"{candidate.Title.Trim()}|{candidate.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var candidates = flattened
                .Where(candidate => !string.Equals(candidate.Path, current.Path, StringComparison.OrdinalIgnoreCase))
                .Where(candidate => string.Equals(candidate.Category, current.Category, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(candidate.PlaybackCategory, current.PlaybackCategory, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                string currentFamily = RecommendationFamily(current);
                candidates = flattened
                    .Where(candidate => !string.Equals(candidate.Path, current.Path, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => string.Equals(RecommendationFamily(candidate), currentFamily, StringComparison.Ordinal))
                    .ToList();
            }

            foreach (var candidate in candidates
                .OrderByDescending(candidate => string.Equals(candidate.PlaybackCategory, current.PlaybackCategory, StringComparison.OrdinalIgnoreCase))
                .ThenBy(candidate => current.Year.HasValue && candidate.Year.HasValue ? Math.Abs(current.Year.Value - candidate.Year.Value) : int.MaxValue)
                .Take(24))
                QueueGenreResolve(candidate);

            // I generi arrivano anche in asincrono. Non filtriamo via i candidati
            // ancora privi di metadati: il ranking usa un fallback stabile e si
            // raffina automaticamente quando QueueGenreResolve invalida la pagina.
            return candidates
                .Select(candidate =>
                {
                    int genreScore = GenreOverlap(currentGenres, GenreSetFor(candidate)) * 100;
                    int categoryScore = string.Equals(candidate.PlaybackCategory, current.PlaybackCategory, StringComparison.OrdinalIgnoreCase) ? 36 :
                        string.Equals(candidate.Category, current.Category, StringComparison.OrdinalIgnoreCase) ? 22 : 0;
                    int yearScore = current.Year.HasValue && candidate.Year.HasValue
                        ? Math.Max(0, 18 - Math.Abs(current.Year.Value - candidate.Year.Value) * 2)
                        : 0;
                    int formatScore = candidate.Is4K == current.Is4K ? 3 : 0;
                    // I titoli che Trakt considera affini a questo film vengono prima, nel suo ordine;
                    // generi, categoria e anno riempiono i posti che restano.
                    int relatedIndex = traktRelated?.FindIndex(path => string.Equals(path, candidate.Path, StringComparison.OrdinalIgnoreCase)) ?? -1;
                    int relatedScore = relatedIndex >= 0 ? 1000 - relatedIndex * 5 : 0;
                    return new { Item = candidate, Score = relatedScore + genreScore + categoryScore + yearScore + formatScore };
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Item.Year == current.Year)
                .ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Item)
                .Take(6)
                .ToList();
        }

        private void DrawDetailRecommendations(Graphics g, Rectangle r, List<LibraryItem> recommended, float scale)
        {
            var (cardW, cardH, used) = MeasureDetailRecommendations(r.Width, r.Height, scale);
            if (recommended.Count == 0 || used <= 0)
                return;

            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(9.4f, 10.8f * scale));
            TextRenderer.DrawText(g, L("Consigliati", "Recommended"), titleFont,
                new Rectangle(r.Left, r.Top, r.Width, S(scale, 24)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int y = r.Top + S(scale, 32);
            int gap = S(scale, 14);
            int x = r.Left;
            foreach (var item in recommended)
            {
                if (x + cardW > r.Right)
                    break;

                Rectangle card = new Rectangle(x, y, cardW, cardH);
                DrawDetailRecommendationCard(g, card, item, scale);
                x += cardW + gap;
            }
        }

        private void DrawDetailRecommendationCard(Graphics g, Rectangle r, LibraryItem item, float scale)
        {
            DrawPosterCard(g, r, item, modalCard: true);
        }

        private void ShowGroupPicker(LibraryItem group)
        {
            if (group == null || !group.IsGroup || group.Children.Count == 0)
                return;

            _activeGroup = group;
            _surfaceEntranceTimer?.Stop();
            _surfaceEntrance = 1;
            _groupSelectionIndex = 0;
            _groupListScroll = 0;
            _groupWheelRemainder = 0;
            _groupVisibleRows = 0;
            try { _searchBox.Visible = false; } catch { }
            QueueOverviewResolve(SelectedGroupChild());
            QueueMusicInfoResolve(SelectedGroupChild());
            BeginSheetEntrance();
            Invalidate();
        }

        private void CloseGroupPicker(bool invalidate = true)
        {
            // Chiusa dall'utente: la scheda esce in dissolvenza. Nei cambi di pagina (invalidate: false) no.
            if (invalidate && _activeGroup is { } closing)
            {
                var area = ClientRectangle;
                BeginSheetExit(GroupPanelBounds(SheetBounds(area), closing), layer => DrawGroupPicker(layer, area));
            }
            else
                EndSheetEntrance();
            _activeGroup = null;
            _groupSelectionIndex = 0;
            _groupListScroll = 0;
            _groupWheelRemainder = 0;
            _groupVisibleRows = 0;
            try { _searchBox.Visible = true; } catch { }
            if (invalidate)
                Invalidate();
        }

        private LibraryItem? SelectedGroupChild()
        {
            if (_activeGroup == null || _activeGroup.Children.Count == 0)
                return null;
            int index = Math.Max(0, Math.Min(_activeGroup.Children.Count - 1, _groupSelectionIndex));
            return _activeGroup.Children[index];
        }

        private void SelectGroupChild(int index)
        {
            if (_activeGroup == null || index < 0 || index >= _activeGroup.Children.Count)
                return;

            _groupSelectionIndex = index;
            EnsureGroupSelectionVisible();
            QueueOverviewResolve(SelectedGroupChild());
            QueueMusicInfoResolve(SelectedGroupChild());
            Invalidate();
        }

        private void MoveGroupSelection(int delta)
        {
            if (_activeGroup == null || _activeGroup.Children.Count == 0)
                return;

            _groupSelectionIndex = Math.Max(0, Math.Min(_activeGroup.Children.Count - 1, _groupSelectionIndex + delta));
            EnsureGroupSelectionVisible();
            QueueOverviewResolve(SelectedGroupChild());
            QueueMusicInfoResolve(SelectedGroupChild());
            Invalidate();
        }

        private void EnsureGroupSelectionVisible()
        {
            int visibleRows = Math.Max(1, _groupVisibleRows);
            if (_groupSelectionIndex < _groupListScroll)
                _groupListScroll = _groupSelectionIndex;
            else if (_groupSelectionIndex >= _groupListScroll + visibleRows)
                _groupListScroll = Math.Max(0, _groupSelectionIndex - visibleRows + 1);
        }

        private void OpenSelectedGroupChild()
        {
            var selected = SelectedGroupChild();
            if (selected == null)
                return;

            CloseGroupPicker(invalidate: false);
            OpenItem(selected, ResumeSecondsFor(selected.Path));
        }

        private void DrawGroupPicker(Graphics g, Rectangle bounds)
        {
            bounds.Height = Math.Max(1,bounds.Height - MusicTransportInset);
            var group = _activeGroup;
            if (group == null)
                return;

            float scale = LayoutScale(bounds);
            using (var dim = new SolidBrush(Color.FromArgb(178, 0, 0, 0)))
                g.FillRectangle(dim, bounds);

            bool albumGroup = IsMusicSheetGroup(group);
            Rectangle panel = GroupPanelBounds(bounds, group);
            if (albumGroup) scale = DetailSheetScale(bounds);
            float shellScale = scale;
            Rectangle layoutPanel = panel;
            if (albumGroup)
            {
                // The album picker was designed on a 1000 x 568 canvas. Keep its
                // internal proportions and visible track count inside the film-sized sheet.
                int layoutW = Math.Min(panel.Width, (int)Math.Floor(panel.Height * 1000d / 568d));
                int layoutH = Math.Min(panel.Height, (int)Math.Round(layoutW * 568d / 1000d));
                layoutPanel = new Rectangle(panel.Left + (panel.Width - layoutW) / 2,
                    panel.Top + (panel.Height - layoutH) / 2, layoutW, layoutH);
                scale = layoutW / 1000f;
            }

            using (var path = Round(panel, S(shellScale, 8)))
            using (var fill = new LinearGradientBrush(panel, Color.FromArgb(252, Back), Color.FromArgb(252, Back), LinearGradientMode.Vertical))
            using (var border = new Pen(Color.FromArgb(56, 120, 145, 165)))
            {
                g.FillPath(fill, path);
                { } // senza contorno
            }

            if(albumGroup)
            {
                using var rounded = Round(panel, S(shellScale,8));
                using (var smooth = CinecorePlayer2025.Utilities.SmoothClip.Begin(g, rounded))
                {
                var sg = smooth.Graphics;
                DrawImagePath(sg,panel,MusicBannerArtwork(group),true,false,false,verticalFocus:.3f);
                using var shade=new LinearGradientBrush(panel,Color.FromArgb(255, Back),Color.Transparent,0f);
                shade.InterpolationColors = new ColorBlend {Positions=new[]{0f,.50f,.72f,1f},Colors=new[]{Color.FromArgb(255, Back),Color.FromArgb(238, Back),Color.FromArgb(76, Back),Color.FromArgb(30, Back)}};
                sg.FillRectangle(shade,panel);
                using var bottom=new LinearGradientBrush(panel,Color.Transparent,Color.FromArgb(255, Back),90f);
                bottom.InterpolationColors = new ColorBlend {Positions=new[]{0f,.35f,.72f,1f},Colors=new[]{Color.Transparent,Color.FromArgb(12, Back),Color.FromArgb(180, Back),Color.FromArgb(255, Back)}};
                sg.FillRectangle(bottom,panel);
                }
            }
            Rectangle close = new Rectangle(panel.Right - S(shellScale, 58), panel.Top + S(shellScale, albumGroup ? 12 : 22), S(shellScale, 40), S(shellScale, 40));
            DrawModalClose(g, close, HitKind.GroupClose, shellScale);

            int pad = S(scale, albumGroup ? 24 : 42);
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(13f, (albumGroup ? 17f : 25f) * scale));
            using var subFont = LibraryFont("Segoe UI", Math.Max(7f, (albumGroup ? 11f : 11f) * scale));
            TextRenderer.DrawText(g, group.Title, titleFont, new Rectangle(layoutPanel.Left + pad, layoutPanel.Top + S(scale, albumGroup ? 14 : 24), layoutPanel.Width - pad * 2 - S(scale, 62), S(scale, albumGroup ? 34 : 42)), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, group.GroupSubtitle ?? string.Empty, subFont, new Rectangle(layoutPanel.Left + pad, layoutPanel.Top + S(scale, albumGroup ? 48 : 66), layoutPanel.Width - pad * 2, S(scale, albumGroup ? 22 : 24)), Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int bodyTop = layoutPanel.Top + S(scale, albumGroup ? 82 : 116);
            int gap = S(scale, albumGroup ? 56 : 48);
            int leftW = albumGroup
                ? (int)Math.Round(layoutPanel.Width * 0.565)
                : Math.Max(S(scale, 560), (int)Math.Round(layoutPanel.Width * 0.45));
            Rectangle list = new Rectangle(layoutPanel.Left + pad, bodyTop, leftW, layoutPanel.Bottom - bodyTop - pad);
            Rectangle details = new Rectangle(list.Right + gap, albumGroup ? bodyTop - S(scale,7) : bodyTop, layoutPanel.Right - list.Right - gap - pad, list.Height + (albumGroup ? S(scale,5) : 0));

            DrawGroupList(g, list, group, scale);
            int trackW = Math.Max(3, S(scale, 4));
            var listScrollTrack = new Rectangle(list.Right + S(scale, albumGroup ? 22 : 18), list.Top, trackW, Math.Max(1, list.Height));
            DrawGroupListScrollbar(g, listScrollTrack, group.Children.Count, _groupVisibleRows, scale);
            DrawGroupDetails(g, details, group, SelectedGroupChild(), scale);
        }

        /// <summary>Pannello della scheda gruppo; <paramref name="bounds"/> gia' senza la barra musica.</summary>
        private Rectangle GroupPanelBounds(Rectangle bounds, LibraryItem group)
        {
            if (IsMusicSheetGroup(group))
                return DetailSheetBounds(bounds);
            float scale = LayoutScale(bounds);
            int panelW = Math.Min(S(scale, 1480), Math.Max(S(scale, 940), bounds.Width - S(scale, 74)));
            int panelH = Math.Min(S(scale, 820), Math.Max(S(scale, 600), bounds.Height - S(scale, 74)));
            return new Rectangle(bounds.Left + (bounds.Width - panelW) / 2, bounds.Top + (bounds.Height - panelH) / 2, panelW, panelH);
        }

        /// <summary>Album e artisti si aprono nella stessa scheda musicale (copertina, elenco brani, dettagli):
        /// l'artista usava la cornice delle serie TV con dentro le righe dei brani, e sembrava una copia venuta male.</summary>
        private static bool IsMusicSheetGroup(LibraryItem? group) =>
            group != null && (string.Equals(group.GroupKind, "Album", StringComparison.OrdinalIgnoreCase) || string.Equals(group.GroupKind, "Artist", StringComparison.OrdinalIgnoreCase));

        private Rectangle _groupRowViewport;

        private void DrawGroupList(Graphics g, Rectangle list, LibraryItem group, float scale)
        {
            _groupRowViewport = list;
            bool album = IsMusicSheetGroup(group);
            int pad = 0;
            int rowH = album ? S(scale, 50) : S(scale, 74);
            int rowGap = 0;
            int bottomPadding = album ? S(scale, 10) : 0;
            int visibleRows = Math.Max(1, (list.Height - pad * 2 - bottomPadding + rowGap) / Math.Max(1, rowH + rowGap));
            _groupVisibleRows = visibleRows;
            int maxScroll = Math.Max(0, group.Children.Count - visibleRows);
            _groupListScroll = Math.Max(0, Math.Min(_groupListScroll, maxScroll));
            EnsureGroupSelectionVisible();

            using Region previousClip = g.Clip.Clone();
            using Region clip = new Region(list);
            g.SetClip(clip, CombineMode.Intersect);

            // Solo righe intere: lo scorrimento va di riga in riga, una riga tagliata a meta' in
            // fondo non serve e sbilancia la scheda.
            int end = Math.Min(group.Children.Count, _groupListScroll + visibleRows);
            int y = list.Top + pad;
            for (int i = _groupListScroll; i < end; i++)
            {
                Rectangle row = new Rectangle(list.Left + pad, y, list.Width - pad * 2, rowH);
                DrawGroupRow(g, row, group.Children[i], i, i == _groupSelectionIndex, scale);
                y += rowH + rowGap;
            }

            g.Clip = previousClip;
        }

        private void DrawGroupListScrollbar(Graphics g, Rectangle track, int count, int visibleRows, float scale)
        {
            if (count <= visibleRows || visibleRows <= 0)
            {
                _groupScrollbarTrack = Rectangle.Empty;
                _groupScrollbarThumb = Rectangle.Empty;
                return;
            }

            int maxScroll = Math.Max(1, count - visibleRows);
            int thumbH = ScrollbarChrome.ThumbLength(track.Height, visibleRows / (double)Math.Max(visibleRows, count));
            int thumbY = track.Top + (int)Math.Round((track.Height - thumbH) * (_groupListScroll / (double)maxScroll));
            _groupScrollbarTrack = Rectangle.Inflate(track, 8, 0);
            _groupScrollbarThumb = new Rectangle(_groupScrollbarTrack.Left, thumbY, _groupScrollbarTrack.Width, thumbH);
            ScrollbarChrome.Draw(g, track, thumbY, thumbH);
        }

        private void DrawGroupRow(Graphics g, Rectangle row, LibraryItem child, int index, bool selected, float scale)
        {
            bool hover = row.Contains(_lastMouse);
            bool album = string.Equals(child.Category, "Music", StringComparison.OrdinalIgnoreCase);
            if (selected || hover)
                HUD.Theme.DrawHighlight(g, Rectangle.Inflate(row, -S(scale, 2), -S(scale, album ? 3 : 4)), selected, S(scale, 3));

            using var numberFont = LibraryFont("Segoe UI Semibold", Math.Max(7f, (album ? 8.2f : 16.0f) * scale));
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(7f, (album ? 10f : 11.0f) * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(6f, (album ? 8.5f : 8.2f) * scale));
            int iconW = S(scale, album ? 38 : 62);
            Rectangle numberRect = new Rectangle(row.Left + S(scale, 18), row.Top, iconW, row.Height);
            // Numero sempre visibile (niente play al passaggio del mouse).
            // PreserveGraphicsClipping: senza, TextRenderer ignora il ritaglio dell'elenco e
            // l'ultima riga parziale scriveva sotto il bordo (la scheda sembrava pendere in basso).
            const TextFormatFlags RowText = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
            TextRenderer.DrawText(g, GroupChildOrdinal(child, index), numberFont, numberRect, selected ? TextMain : Muted, TextFormatFlags.HorizontalCenter | RowText);

            int x = row.Left + S(scale, album ? 70 : 94);
            int y = row.Top + S(scale, album ? 6 : 15);
            int side = S(scale, album ? 104 : 54);
            TextRenderer.DrawText(g, album ? MusicTextIdentity.Title(child.Title) : GroupChildTitle(child), title, new Rectangle(x, y, row.Width - (x - row.Left) - side, S(scale, album ? 21 : 24)), TextMain, TextFormatFlags.Left | RowText);
            TextRenderer.DrawText(g, album ? TrackArtistLabel(child) : GroupChildMeta(child), meta, new Rectangle(x, y + S(scale, album ? 21 : 24), row.Width - (x - row.Left) - side, S(scale, album ? 18 : 20)), Muted, TextFormatFlags.Left | RowText);
            if (album)
            {
                string duration = child.DurationMinutes.HasValue ? TimeSpan.FromMinutes(child.DurationMinutes.Value).ToString(@"m\:ss") : "";
                TextRenderer.DrawText(g,duration,meta,new Rectangle(row.Right-S(scale,62),row.Top,S(scale,46),row.Height),Muted,MusicText|TextFormatFlags.Right);
                
            }
            if (selected && !album)
            {
                    DrawTinyPlayCircle(g, new Rectangle(row.Right - S(scale, 58), row.Top + (row.Height - S(scale, 34)) / 2, S(scale, 34), S(scale, 34)), Accent);
            }
            else if (!album)
            {
                using var arrowPen = new Pen(Color.FromArgb(176, 194, 210), Math.Max(1.6f, 1.8f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                int cx = row.Right - S(scale, 46);
                int cy = row.Top + row.Height / 2;
                int d = S(scale, 7);
                g.DrawLine(arrowPen, cx - d / 2, cy - d, cx + d / 2, cy);
                g.DrawLine(arrowPen, cx + d / 2, cy, cx - d / 2, cy + d);
            }
            _hits.Add(new HitZone { Bounds = Rectangle.Intersect(row, _groupRowViewport), Kind = HitKind.GroupSelect, Item = child, Index = index, Key = child.Path });
        }

        private void DrawGroupDetails(Graphics g, Rectangle r, LibraryItem group, LibraryItem? selected, float scale)
        {
            if (selected == null)
                return;

            if (string.Equals(group.GroupKind, "Album", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(selected.Category, "Music", StringComparison.OrdinalIgnoreCase))
            {
                DrawAlbumGroupDetails(g, r, group, selected, scale);
                return;
            }

            DrawEpisodeGroupDetails(g, r, group, selected, scale);
        }

        private void DrawEpisodeGroupDetails(Graphics g, Rectangle r, LibraryItem group, LibraryItem selected, float scale)
        {
            QueueOverviewResolve(selected);
            QueueEpisodeStillResolve(selected);

            int pad = S(scale, 4);
            int artH = Math.Min(S(scale, 330), Math.Max(S(scale, 220), (int)Math.Round((r.Width - pad * 2) * 9 / 16.0)));
            Rectangle art = new Rectangle(r.Left + pad, r.Top + S(scale, 4), Math.Max(1, r.Width - pad * 2), artH);
            string? artPath = EpisodeWideImagePath(selected);
            using var artPathRound = Round(art, S(scale, 8));
            using (var smooth = CinecorePlayer2025.Utilities.SmoothClip.Begin(g, artPathRound))
            {
                if (!DrawMediaContainBlack(smooth.Graphics, art, artPath))
                    DrawGeneratedArt(smooth.Graphics, art, selected.Title, hero: false, category: selected.Category);
            }

            int y = art.Bottom + S(scale, 28);
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(19f, 25.0f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(9.0f, 10.4f * scale));
            TextRenderer.DrawText(g, GroupChildTitle(selected), title, new Rectangle(r.Left + pad, y, r.Width - pad * 2, S(scale, 42)), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += S(scale, 44);
            TextRenderer.DrawText(g, GroupChildMeta(selected), meta, new Rectangle(r.Left + pad, y, r.Width - pad * 2, S(scale, 24)), Color.FromArgb(150, 190, 224), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += S(scale, 34);

            string overview = HeroText(selected);
            using var body = LibraryFont("Segoe UI", Math.Max(9.2f, 10.2f * scale));
            Rectangle overviewRect = new Rectangle(r.Left + pad, y, r.Width - pad * 2, Math.Min(S(scale, 112), Math.Max(S(scale, 72), r.Bottom - y - S(scale, 172))));
            string fitted = FitTextWithEllipsis(overview, body, overviewRect.Size, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, fitted, body, overviewRect, Color.FromArgb(224, 233, 240), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int statY = r.Bottom - S(scale, 134);
            int statW = Math.Max(S(scale, 142), (r.Width - pad * 2 - S(scale, 28)) / 3);
            DrawDetailStat(g, new Rectangle(r.Left + pad, statY, statW, S(scale, 64)), "year", L("Anno", "Year"), selected.Year?.ToString(CultureInfo.InvariantCulture) ?? FormatItemDate(selected.SortDateUtc), scale);
            DrawDetailStat(g, new Rectangle(r.Left + pad + statW + S(scale, 14), statY, statW, S(scale, 64)), "time", L("Durata", "Duration"), DurationText(selected), scale);
            DrawDetailStat(g, new Rectangle(r.Left + pad + (statW + S(scale, 14)) * 2, statY, statW, S(scale, 64)), "quality", L("Qualita", "Quality"), QualityLabel(selected), scale);

            Rectangle play = new Rectangle(r.Left + (r.Width - S(scale, 240)) / 2, r.Bottom - S(scale, 56), S(scale, 240), S(scale, 52));
            DrawAction(g, play, L("Guarda", "Watch"), "play", true, HitKind.GroupPlay, selected);
        }

        private void DrawAlbumGroupDetails(Graphics g, Rectangle r, LibraryItem group, LibraryItem selected, float scale)
        {
            QueueMusicInfoResolve(selected); QueueMusicQualityResolve(selected);
            // Scheda di un artista: copertina e album sono quelli del brano scelto, non il nome dell'artista ripetuto.
            bool byArtist = string.Equals(group.GroupKind, "Artist", StringComparison.OrdinalIgnoreCase);
            int pad = S(scale, 10);
            int contentLeft = r.Left + pad;
            int contentWidth = Math.Max(1, r.Width - pad * 2);
            int coverSide = Math.Max(1, Math.Min(contentWidth, r.Height - S(scale, 220)));
            // Copertina, testi e azioni formano un blocco unico centrato in verticale nel
            // riquadro (prima la copertina stava in alto e le azioni incollate al fondo).
            int blockH = coverSide + S(scale, 18) + S(scale, 31) + S(scale, 20) * 4 + S(scale, 35) + S(scale, 39);
            int blockTop = r.Top + Math.Max(0, (r.Height - blockH) / 2);
            var cover = new Rectangle(contentLeft + (contentWidth - coverSide) / 2,
                blockTop, coverSide, coverSide);
            using (var oldClip = g.Clip.Clone())
            {
                using var frame = Round(cover, S(scale, 7));
                g.SetClip(frame, CombineMode.Intersect);
                // Album non riconosciuti: stesso placeholder delle card della griglia musica.
                if (!DrawImagePath(g, cover, byArtist ? selected.ArtPath ?? group.ArtPath : group.ArtPath ?? selected.ArtPath, false, false, false) &&
                    !DrawPosterArt(g, cover, group) && !DrawPosterArt(g, cover, selected))
                    DrawGeneratedArt(g, cover, group.Title, false, category: "Music");
                g.Clip = oldClip;
            }
            using(var outline = new Pen(Color.FromArgb(90,148,190,215),Math.Max(1,scale)))
            using(var frame = Round(cover,S(scale,7))) { } // senza contorno
            _musicInfoCache.TryGetValue(MusicInfoKey(selected),out var info);
            int y=cover.Bottom+S(scale,18), textW=contentWidth;
            using var title=LibraryFont("Segoe UI Semibold",Math.Max(11,15.5f*scale));
            using var meta=LibraryFont("Segoe UI",Math.Max(6.5f,8f*scale));
            TextRenderer.DrawText(g,MusicTextIdentity.Title(selected.Title),title,new Rectangle(contentLeft,y,textW,S(scale,28)),TextMain,MusicText);y+=S(scale,31);
            foreach(string text in new[]{selected.ArtistName??info?.ArtistName??"",byArtist&&!string.IsNullOrWhiteSpace(selected.AlbumTitle)?selected.AlbumTitle!:group.Title,(selected.Year??group.Year)?.ToString()??"",string.Join(" · ",new[]{DurationText(selected),MusicQualityLabel(selected)}.Where(t=>!string.IsNullOrWhiteSpace(t)))})
            {
                TextRenderer.DrawText(g,text,meta,new Rectangle(contentLeft,y,textW,S(scale,20)),Muted,MusicText); y+=S(scale,20);
            }
            int h=S(scale,39), actionY=Math.Min(y+S(scale,35),r.Bottom-h);
            using(var rule=new Pen(Color.FromArgb(35,125,151,171)))g.DrawLine(rule,contentLeft,actionY-S(scale,19),contentLeft+contentWidth,actionY-S(scale,19));
            int iconSize=S(scale,38), gap=S(scale,12), playW=Math.Max(72,textW-(iconSize+gap)*3);
            var play=new Rectangle(contentLeft,actionY,playW,h);
            using(var pill=Round(play,h/2)) using(var fill=new SolidBrush(Accent)) g.FillPath(fill,pill);
            string playLabel = L("Riproduci", "Play");
            int glyph = S(scale, 13), labelWidth = TextRenderer.MeasureText(playLabel, meta, Size.Empty, TextFormatFlags.NoPadding).Width;
            int start = play.Left + (play.Width - glyph - S(scale, 8) - labelWidth) / 2;
            DrawIcon(g,new Rectangle(start,play.Top+(play.Height-glyph)/2,glyph,glyph),"play",Color.White);
            TextRenderer.DrawText(g,playLabel,meta,new Rectangle(start+glyph+S(scale,8),play.Top,labelWidth,play.Height),Color.White,MusicText);
            _hits.Add(new HitZone {Bounds=play,Kind=HitKind.GroupPlay,Item=selected,Key=selected.Path});
            int x=play.Right+gap;
            foreach(var action in new[]{("star",HitKind.GroupFavorite),("queue-add",HitKind.GroupQueue),("more",HitKind.GroupMore)})
            {
                var button=new Rectangle(x,actionY+(h-iconSize)/2,iconSize,iconSize);
                using(var fill=new SolidBrush(Color.FromArgb(button.Contains(_lastMouse)?28:10,HUD.Theme.Text)))g.FillEllipse(fill,button);
                using(var outline=new Pen(Color.FromArgb(60,148,172,190))){ } // senza contorno
                HUD.MusicTransportBar.DrawSymbol(g,Rectangle.Inflate(button,-S(scale,10),-S(scale,10)),action.Item1,action.Item2==HitKind.GroupFavorite&&IsFavorite(selected.Path)?Accent:TextMain);
                _hits.Add(new HitZone {Bounds=button,Kind=action.Item2,Item=selected,Key=selected.Path});x+=iconSize+gap;
            }
        }

        private string? EpisodeWideImagePath(LibraryItem item)
        {
            if (item == null)
                return null;

            if (_episodeStillCache.TryGetValue(item.Path, out var cached) && LooksLikeUsableBackdrop(cached))
                return cached;

            foreach (string? candidate in new[]
            {
                item.WideArtPath,
                SafeCachedBackdropPath(item.Path),
                ResolveBackdropPath(item.Path, ResolveMetadataLookupKey(item.Path, item.Title, item.Year), item.Title)
            })
            {
                if (LooksLikeUsableBackdrop(candidate))
                    return candidate;
            }

            return null;
        }

        private static string? SafeCachedBackdropPath(string path)
        {
            try { return MovieMetadataService.GetCachedBackdropPath(path); }
            catch { return null; }
        }

        private void QueueEpisodeStillResolve(LibraryItem item)
        {
            if (item == null || !IsTvEpisodePath(item.Path))
                return;
            if (_episodeStillCache.ContainsKey(item.Path) || _episodeStillRequests.Contains(item.Path) || _episodeStillFailures.Contains(item.Path))
                return;

            _episodeStillRequests.Add(item.Path);
            string path = item.Path;
            Task.Run(() =>
            {
                try
                {
                    return MovieMetadataService.ResolveEpisodeStillPath(path, CancellationToken.None);
                }
                catch
                {
                    return null;
                }
            }).ContinueWith(task =>
            {
                PostToUi(() =>
                {
                    try
                    {
                        _episodeStillRequests.Remove(path);
                        string? result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                        if (LooksLikeUsableBackdrop(result))
                            _episodeStillCache[path] = result!;
                        else
                            _episodeStillFailures.Add(path);
                        Invalidate();
                    }
                    catch { }
                });
            }, TaskScheduler.Default);
        }

        private void PostToUi(Action action)
        {
            try
            {
                if (IsDisposed)
                    return;

                if (IsHandleCreated)
                    BeginInvoke(action);
            }
            catch { }
        }

        private void DrawDetailStat(Graphics g, Rectangle r, string icon, string label, string value, float scale)
        {
            if (string.IsNullOrWhiteSpace(value))
                value = "-";

            using (var path = Round(r, S(scale, 7)))
            using (var fill = new SolidBrush(Color.FromArgb(34, 6, 18, 29)))
            using (var border = new Pen(Color.FromArgb(22, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                { } // senza contorno
            }

            Rectangle iconRect = new Rectangle(r.Left + S(scale, 14), r.Top + (r.Height - S(scale, 24)) / 2, S(scale, 24), S(scale, 24));
            DrawIcon(g, iconRect, icon, Color.FromArgb(150, 174, 196));

            using var labelFont = LibraryFont("Segoe UI", Math.Max(7.2f, 8.0f * scale));
            using var valueFont = LibraryFont("Segoe UI Semibold", Math.Max(8.4f, 9.4f * scale));
            int textX = iconRect.Right + S(scale, 12);
            TextRenderer.DrawText(g, label, labelFont, new Rectangle(textX, r.Top + S(scale, 10), r.Right - textX - S(scale, 8), S(scale, 18)), Color.FromArgb(142, 158, 174), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, value, valueFont, new Rectangle(textX, r.Top + S(scale, 30), r.Right - textX - S(scale, 8), S(scale, 20)), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// Artist line of an album track: the track tag, else the album artist, else what
        /// the album grouping or the folder layout says. Never empty for some tracks only.
        /// </summary>
        private string TrackArtistLabel(LibraryItem child)
        {
            string artist = FirstRawNonEmpty(child.ArtistName, child.AlbumArtist);
            if (string.IsNullOrWhiteSpace(artist) && _musicInfoCache.TryGetValue(MusicInfoKey(child), out var info))
                artist = info?.ArtistName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(artist) && _activeGroup != null &&
                IsMusicSheetGroup(_activeGroup))
            {
                string groupArtist = _activeGroup.AlbumArtist ?? string.Empty;
                if (!string.Equals(groupArtist, L("Artisti vari", "Various artists"), StringComparison.OrdinalIgnoreCase))
                    artist = groupArtist;
            }
            if (string.IsNullOrWhiteSpace(artist))
                artist = InferAlbumInfo(child.Path).Artist;
            return MusicTextIdentity.Artist(artist);
        }

        private static string GroupChildTitle(LibraryItem child)
        {
            if (string.Equals(child.Category, "TV Series", StringComparison.OrdinalIgnoreCase))
            {
                var info = TryExtractMediaTitleInfo(child.Path);
                string title = FirstNonEmpty(info?.EpisodeTitle, ExtractEpisodeTitleFromDisplay(child.Title), child.Title);
                if (info?.EpisodeNumber.HasValue == true)
                    return $"E{info.EpisodeNumber.Value:00} - {title}";
                int? episode = TryParseEpisodeNumber(child.Path);
                return episode.HasValue ? $"E{episode.Value:00} - {title}" : title;
            }

            int? track = TryParseTrackNumber(child.Path);
            return track.HasValue ? $"{track.Value:00}. {child.Title}" : child.Title;
        }

        private static string GroupChildOrdinal(LibraryItem child, int index)
        {
            if (string.Equals(child.Category, "TV Series", StringComparison.OrdinalIgnoreCase))
            {
                int? episode = TryExtractMediaTitleInfo(child.Path)?.EpisodeNumber ?? TryParseEpisodeNumber(child.Path);
                return (episode ?? index + 1).ToString("00", CultureInfo.InvariantCulture);
            }

            // Album: posizione nell'elenco. I tag/nomi dei file possono partire da numeri
            // diversi (dischi, compilation) e mostravano "21" sulla prima traccia.
            return (index + 1).ToString("00", CultureInfo.InvariantCulture);
        }

        private static void DrawTinyPlayGlyph(Graphics g, Rectangle r, Color color)
        {
            if (AssetIconService.DrawCustom(g, r, "play", color)) return;
            Point[] pts =
            {
                new Point(r.Left + r.Width / 3, r.Top + r.Height / 4),
                new Point(r.Left + r.Width / 3, r.Bottom - r.Height / 4),
                new Point(r.Right - r.Width / 4, r.Top + r.Height / 2)
            };
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, pts);
        }

        private static void DrawTinyPlayCircle(Graphics g, Rectangle r, Color color)
        {
            using var pen = new Pen(Color.FromArgb(165, color), 1.4f);
            g.DrawEllipse(pen, r);
            DrawTinyPlayGlyph(g, Rectangle.Inflate(r, -7, -7), color);
        }

        private static void DrawTinyEqualizer(Graphics g, Rectangle r, Color color)
        {
            using var pen = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int baseY = r.Bottom - 4;
            int gap = Math.Max(4, r.Width / 5);
            int x = r.Left + 4;
            int[] heights = { 10, 16, 7, 19 };
            for (int i = 0; i < heights.Length; i++)
            {
                int h = Math.Min(r.Height - 4, heights[i]);
                g.DrawLine(pen, x + i * gap, baseY, x + i * gap, baseY - h);
            }
        }

        private string GroupChildMeta(LibraryItem child)
        {
            string duration = DurationText(child);
            string quality = QualityLabel(child);
            string date = FormatItemDate(child.SortDateUtc);
            if (string.Equals(child.Category, "Music", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(duration) ? Path.GetExtension(child.Path).Trim('.').ToUpperInvariant() : duration;

            string yearOrDate = child.Year.HasValue ? child.Year.Value.ToString(CultureInfo.InvariantCulture) : date;
            return string.Join(" - ", new[] { yearOrDate, duration, quality }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string? ExtractEpisodeTitleFromDisplay(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string value = Regex.Replace(title!, @"\s+", " ").Trim();
            var match = Regex.Match(value, @"(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|E\d{1,3})\s*[-:]\s*(?<title>.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
                return CleanTitle(match.Groups["title"].Value);

            int dash = value.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash + 3 < value.Length)
                return CleanTitle(value.Substring(dash + 3));

            return null;
        }

    }
}
