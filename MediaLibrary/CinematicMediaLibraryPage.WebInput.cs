#nullable enable
using CinecorePlayer2025.Utilities;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private sealed class YouTubeBrowseItem
        {
            public string Id { get; init; } = string.Empty;
            public string Url { get; init; } = string.Empty;
            public string Title { get; init; } = string.Empty;
            public string Channel { get; init; } = string.Empty;
            public string Duration { get; init; } = string.Empty;
            public long? ViewCount { get; init; }
            public bool IsLive { get; init; }
            public string ThumbnailUrl { get; init; } = string.Empty;
            public string? ThumbnailPath { get; set; }
        }

        private static readonly HttpClient YouTubeBrowseHttp = CreateYouTubeBrowseHttp();
        private static readonly SemaphoreSlim YouTubeThumbnailGate = new(4, 4);
        private readonly List<YouTubeBrowseItem> _youtubeItems = new();
        private CancellationTokenSource? _youtubeBrowseCts;
        private int _youtubeBrowseGeneration;
        private int _youtubeScroll;
        private int _youtubeScrollMax;
        private bool _youtubeLoading;
        private string _youtubeStatus = string.Empty;

        private bool IsNativeYouTubeView =>
            _view == PageView.WebInput &&
            string.Equals(_webInputKind, "YouTube", StringComparison.OrdinalIgnoreCase);

        public bool IsWebAddressEditor(Control? control)
            => control != null && (ReferenceEquals(control, _webAddressBox) ||
                                   (IsNativeYouTubeView && ReferenceEquals(control, _searchBox)));

        private void ShowWebInputPage(string kind)
        {
            CloseGroupPicker(invalidate: false);
            _view = PageView.WebInput;
            _source = "Computer";
            _webInputKind = string.Equals(kind, "YouTube", StringComparison.OrdinalIgnoreCase) ? "YouTube" : "URL";
            _webInputStatus = string.Empty;
            SetContentLoading(false);

            if (IsNativeYouTubeView)
            {
                _webAddressBox.Visible = false;
                _searchBox.PlaceholderText = L("Cerca su YouTube", "Search YouTube");
                _searchBox.TabStop = true;
                _searchBox.Visible = true;
                _youtubeScroll = 0;
                if (_youtubeItems.Count == 0)
                    StartYouTubeSearchNow(string.Empty);
                try { Focus(); } catch { }
            }
            else
            {
                CancelYouTubeWork();
                _searchBox.Visible = false;
                _webAddressBox.PlaceholderText = L("https://esempio.it/video.m3u8", "https://example.com/video.m3u8");
                _webAddressBox.Clear();
                _webAddressBox.Visible = true;
                try { _webAddressBox.Focus(); } catch { }
            }
            Invalidate();
        }

        private void LayoutWebInputBox()
        {
            bool show = _musicWorkspaceContent == null && _view == PageView.WebInput && !IsNativeYouTubeView && _detailItem == null && _activeGroup == null &&
                        !_queueEditorVisible && _playlistCreateOverlay?.Visible != true;
            if (!show)
            {
                if (_webAddressBox.Visible)
                    _webAddressBox.Visible = false;
                return;
            }

            if (!_webAddressBox.Visible)
                _webAddressBox.Visible = true;
            if (_webInputShellRect.Width <= 0)
                return;

            float scale = _webInputShellRect.Height / 58f;
            float fontSize = Math.Max(8f, 11.5f * scale);
            if (Math.Abs(_webAddressBox.Font.SizeInPoints - fontSize) > .1f)
            { var old = _webAddressBox.Font; _webAddressBox.Font = LibraryFont("Segoe UI", fontSize); old.Dispose(); }
            _webAddressBox.BackColor = Chrome;
            _webAddressBox.ForeColor = TextMain;
            _webAddressBox.BorderStyle = BorderStyle.None;
            _webAddressBox.AutoSize = false;
            int textHeight = _webAddressBox.Font.Height + 1;
            int leftInset = S(scale, 52);
            Rectangle bounds = new(
                _webInputShellRect.Left + leftInset,
                _webInputShellRect.Top + (_webInputShellRect.Height - textHeight) / 2,
                Math.Max(40, _webInputShellRect.Width - leftInset - 18),
                textHeight);
            if (_webAddressBox.Bounds != bounds)
                _webAddressBox.Bounds = bounds;
        }

        private void PasteWebInputFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsText())
                    _webAddressBox.Text = Clipboard.GetText().Trim();
                _webAddressBox.Focus();
                _webAddressBox.SelectionStart = _webAddressBox.TextLength;
                _webInputStatus = string.Empty;
            }
            catch
            {
                _webInputStatus = L("Impossibile leggere gli appunti.", "Could not read the clipboard.");
            }
            Invalidate();
        }

        private void OpenWebInput()
        {
            string value = (_webAddressBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                _webInputStatus = L("Inserisci prima un indirizzo.", "Enter an address first.");
                Invalidate();
                return;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            {
                if (Uri.TryCreate("https://" + value, UriKind.Absolute, out uri))
                    value = uri.AbsoluteUri;
                else
                {
                    _webInputStatus = L("Il link non è valido.", "The link is not valid.");
                    Invalidate();
                    return;
                }
            }

            _webInputStatus = L("Apro il flusso…", "Opening stream…");
            Invalidate();
            OpenRequested?.Invoke(value);
        }

        private void DrawWebInputPage(Graphics g, Rectangle content, float scale)
        {
            if (IsNativeYouTubeView)
            {
                DrawYouTubePage(g, content, scale);
                return;
            }

            _searchShellRect = Rectangle.Empty;
            int top = content.Top + S(scale, 200);
            int maxW = Math.Min(content.Width, S(scale, 1320));
            int x = content.Left + (content.Width - maxW) / 2;

            using var eyebrow = LibraryFont("Segoe UI Semibold", Math.Max(9f, 10f * scale));
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(25f, 34f * scale));
            using var body = LibraryFont("Segoe UI", Math.Max(10f, 11.5f * scale));
            TextRenderer.DrawText(g, "VIDEO ONLINE", eyebrow,
                new Rectangle(x, top, maxW, S(scale, 24)), Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L("Riproduci da un link", "Play from a link"),
                title, new Rectangle(x, top + S(scale, 30), maxW, S(scale, 58)), TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g,
                L("Incolla il link di un video o di una diretta e guardalo in Cinecore.", "Paste a video or live stream link to watch it in Cinecore."),
                body, new Rectangle(x, top + S(scale, 94), maxW, S(scale, 38)), Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int gap = S(scale, 14);
            int openW = S(scale, 184);
            bool stacked = maxW < S(scale, 520);
            _webInputShellRect = new Rectangle(x, top + S(scale, 152),
                Math.Max(1, maxW - (stacked ? 0 : openW + gap)), S(scale, 58));
            using (var shell = Round(_webInputShellRect, S(scale, 9)))
            using (var shellFill = new SolidBrush(Chrome))
            using (var shellBorder = new Pen(Color.FromArgb(72, HUD.Theme.Border)))
            { g.FillPath(shellFill, shell); g.DrawPath(shellBorder, shell); }
            DrawIcon(g, new Rectangle(x + S(scale, 17), _webInputShellRect.Top + S(scale, 19), S(scale, 20), S(scale, 20)), "link", Accent);
            Rectangle open = new(stacked ? x : _webInputShellRect.Right + gap,
                stacked ? _webInputShellRect.Bottom + gap : _webInputShellRect.Top, openW, S(scale, 58));
            DrawWebAction(g, open, L("Riproduci", "Play"), "play", true, HitKind.WebOpen, scale);
            if (!string.IsNullOrWhiteSpace(_webInputStatus))
                TextRenderer.DrawText(g, _webInputStatus, body,
                    new Rectangle(x, open.Bottom + S(scale, 8), maxW, S(scale, 30)), Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            DrawWebNotes(g, new Rectangle(x, open.Bottom + S(scale, 62), Math.Max(_webInputShellRect.Right, open.Right) - x, S(scale, 54)),
                new[] { "HLS / DASH / HTTP", L("Risoluzione automatica", "Automatic resolving"), L("Riproduzione in Cinecore", "Playback in Cinecore") }, scale);
        }

        private void DrawYouTubePage(Graphics g, Rectangle content, float scale)
        {
            _webInputShellRect = Rectangle.Empty;
            int headerTop = content.Top + S(scale, 4);
            int titleH = S(scale, 42);
            int iconSize = S(scale, 30);
            Rectangle youtubeIcon = new(content.Left, headerTop + (titleH - iconSize) / 2, iconSize, iconSize);
            DrawIcon(g, youtubeIcon, "youtube", Color.FromArgb(239, 68, 68));

            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(18f, 23f * scale));
            using var sectionFont = LibraryFont("Segoe UI Semibold", Math.Max(11f, 13.2f * scale));
            using var bodyFont = LibraryFont("Segoe UI", Math.Max(8.5f, 9.3f * scale));
            TextRenderer.DrawText(g, "YouTube", titleFont,
                new Rectangle(youtubeIcon.Right + S(scale, 12), headerTop, S(scale, 260), titleH), TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L("Ricerca e riproduzione native", "Native search and playback"), bodyFont,
                new Rectangle(content.Right - S(scale, 320), headerTop, S(scale, 320), titleH), Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int searchTop = headerTop + titleH + S(scale, 14);
            int refreshW = S(scale, 112);
            int gap = S(scale, 10);
            _searchShellRect = new Rectangle(content.Left, searchTop, Math.Max(S(scale, 260), content.Width - refreshW - gap), S(scale, 42));
            using (var path = Round(_searchShellRect, S(scale, 7)))
            using (var fill = new SolidBrush(Chrome))
            using (var border = new Pen(_searchShellRect.Contains(_lastMouse) ? Color.FromArgb(150, Accent) : Color.FromArgb(HUD.Theme.IsLight ? 155 : 72, HUD.Theme.Border)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            DrawIcon(g, new Rectangle(_searchShellRect.Left + S(scale, 14), _searchShellRect.Top + (_searchShellRect.Height - S(scale, 17)) / 2, S(scale, 17), S(scale, 17)), "search", Muted);
            Rectangle refresh = new(_searchShellRect.Right + gap, searchTop, refreshW, _searchShellRect.Height);
            DrawYouTubeRefresh(g, refresh, scale);
            _searchBox.PlaceholderText = L("Cerca video, canali o argomenti", "Search videos, channels or topics");
            _searchBox.BackColor = Chrome;
            _searchBox.ForeColor = TextMain;
            LayoutSearchBox();

            int sectionTop = _searchShellRect.Bottom + S(scale, 28);
            string sectionTitle = string.IsNullOrWhiteSpace(_searchBox.Text)
                ? L("In evidenza", "Featured")
                : L($"Risultati per “{_searchBox.Text.Trim()}”", $"Results for “{_searchBox.Text.Trim()}”");
            TextRenderer.DrawText(g, sectionTitle, sectionFont,
                new Rectangle(content.Left, sectionTop, Math.Max(100, content.Width - S(scale, 180)), S(scale, 30)), TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (_youtubeItems.Count > 0 && !_youtubeLoading)
                TextRenderer.DrawText(g, L($"{_youtubeItems.Count} video", $"{_youtubeItems.Count} videos"), bodyFont,
                    new Rectangle(content.Right - S(scale, 170), sectionTop, S(scale, 170), S(scale, 30)), Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Keep a real gutter between the cards and the scroll indicator.  The old
            // layout used the very same right edge for both, making the thumb look as
            // though it were glued to the last thumbnail.
            int scrollGutter = S(scale, 44);
            Rectangle viewport = new(content.Left, sectionTop + S(scale, 40), Math.Max(1, content.Width - scrollGutter),
                Math.Max(1, content.Bottom - sectionTop - S(scale, 44)));
            Rectangle scrollViewport = new(viewport.Left, viewport.Top, content.Width, viewport.Height);
            if (_youtubeLoading && _youtubeItems.Count == 0)
            {
                DrawYouTubeSkeletons(g, viewport, scale);
                _youtubeScrollMax = 0;
                return;
            }
            if (_youtubeItems.Count == 0)
            {
                DrawYouTubeEmpty(g, viewport, scale);
                _youtubeScrollMax = 0;
                return;
            }

            int minCardW = S(scale, 250);
            int gapX = S(scale, 20);
            int columns = Math.Clamp((viewport.Width + gapX) / Math.Max(1, minCardW + gapX), 2, 5);
            int cardW = (viewport.Width - gapX * (columns - 1)) / columns;
            int imageH = Math.Max(S(scale, 126), (int)Math.Round(cardW * 9d / 16d));
            int cardH = imageH + S(scale, 78);
            int gapY = S(scale, 28);
            int rows = (int)Math.Ceiling(_youtubeItems.Count / (double)columns);
            int totalHeight = Math.Max(0, rows * cardH + Math.Max(0, rows - 1) * gapY);
            _youtubeScrollMax = Math.Max(0, totalHeight - viewport.Height);
            _youtubeScroll = Math.Clamp(_youtubeScroll, 0, _youtubeScrollMax);

            GraphicsState state = g.Save();
            g.SetClip(viewport);
            for (int i = 0; i < _youtubeItems.Count; i++)
            {
                int row = i / columns;
                int column = i % columns;
                Rectangle card = new(
                    viewport.Left + column * (cardW + gapX),
                    viewport.Top + row * (cardH + gapY) - _youtubeScroll,
                    cardW,
                    cardH);
                if (card.Bottom < viewport.Top - gapY || card.Top > viewport.Bottom + gapY)
                    continue;
                DrawYouTubeCard(g, card, imageH, _youtubeItems[i], scale);
            }
            g.Restore(state);

            if (_youtubeLoading)
            {
                Rectangle loading = new(viewport.Right - S(scale, 118), viewport.Top + S(scale, 5), S(scale, 110), S(scale, 26));
                DrawYouTubeStatusPill(g, loading, L("Aggiorno…", "Updating…"), scale);
            }
            DrawYouTubeScrollIndicator(g, scrollViewport, totalHeight, scale);
        }

        private void DrawYouTubeRefresh(Graphics g, Rectangle bounds, float scale)
        {
            bool hover = bounds.Contains(_lastMouse);
            using (var path = Round(bounds, S(scale, 7)))
            using (var fill = new SolidBrush(hover ? ChromeHover : Chrome))
            using (var border = new Pen(hover ? Color.FromArgb(155, Accent) : Color.FromArgb(HUD.Theme.IsLight ? 155 : 72, HUD.Theme.Border)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            int icon = S(scale, 15);
            Rectangle iconRect = new(bounds.Left + S(scale, 14), bounds.Top + (bounds.Height - icon) / 2, icon, icon);
            DrawIcon(g, iconRect, "scan", TextMain);
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8f, 8.5f * scale));
            TextRenderer.DrawText(g, L("Aggiorna", "Refresh"), font,
                new Rectangle(iconRect.Right + S(scale, 8), bounds.Top, bounds.Right - iconRect.Right - S(scale, 10), bounds.Height), TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = bounds, Kind = HitKind.YouTubeRefresh });
        }

        private void DrawYouTubeCard(Graphics g, Rectangle card, int imageH, YouTubeBrowseItem item, float scale)
        {
            Rectangle imageRect = new(card.Left, card.Top, card.Width, imageH);
            bool hover = card.Contains(_lastMouse);
            using var imagePath = Round(imageRect, S(scale, 7));
            GraphicsState state = g.Save();
            // Preserve the page viewport clip while applying the rounded thumbnail
            // mask. Replacing it allowed scrolled images to paint over the search bar.
            g.SetClip(imagePath, CombineMode.Intersect);
            Image? thumbnail = !string.IsNullOrWhiteSpace(item.ThumbnailPath)
                ? LoadImageForDisplay(item.ThumbnailPath!, Math.Max(imageRect.Width, imageRect.Height))
                : null;
            if (thumbnail != null)
            {
                Rectangle source = CoverSourceForYouTube(thumbnail.Size, imageRect.Size);
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(thumbnail, imageRect, source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
            }
            else
            {
                using var placeholder = new LinearGradientBrush(imageRect, Color.FromArgb(30, 38, 47), Color.FromArgb(10, 14, 20), LinearGradientMode.ForwardDiagonal);
                g.FillRectangle(placeholder, imageRect);
                int logo = Math.Max(28, S(scale, 34));
                DrawIcon(g, new Rectangle(imageRect.Left + (imageRect.Width - logo) / 2, imageRect.Top + (imageRect.Height - logo) / 2, logo, logo), "youtube", Color.FromArgb(172, 239, 68, 68));
            }
            if (hover)
            {
                using var hoverVeil = new SolidBrush(Color.FromArgb(20, 255, 255, 255));
                g.FillRectangle(hoverVeil, imageRect);
                int play = S(scale, 42);
                Rectangle playRect = new(imageRect.Left + (imageRect.Width - play) / 2, imageRect.Top + (imageRect.Height - play) / 2, play, play);
                using var disc = new SolidBrush(Color.FromArgb(220, 7, 12, 18));
                g.FillEllipse(disc, playRect);
                using var triangle = new SolidBrush(Color.White);
                g.FillPolygon(triangle, new[]
                {
                    new PointF(playRect.Left + play * .42f, playRect.Top + play * .30f),
                    new PointF(playRect.Left + play * .42f, playRect.Bottom - play * .30f),
                    new PointF(playRect.Right - play * .27f, playRect.Top + play * .50f)
                });
            }
            g.Restore(state);

            using (var border = new Pen(hover ? Color.FromArgb(195, Accent) : Color.FromArgb(HUD.Theme.IsLight ? 118 : 52, HUD.Theme.Border), hover ? 1.5f : 1f))
                g.DrawPath(border, imagePath);

            string duration = item.IsLive ? "LIVE" : item.Duration;
            if (!string.IsNullOrWhiteSpace(duration))
            {
                using var durationFont = LibraryFont("Segoe UI Semibold", Math.Max(7.2f, 7.8f * scale));
                int width = TextRenderer.MeasureText(duration, durationFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(scale, 12);
                Rectangle chip = new(imageRect.Right - width - S(scale, 7), imageRect.Bottom - S(scale, 27), width, S(scale, 21));
                using var chipPath = Round(chip, S(scale, 4));
                using var chipFill = new SolidBrush(item.IsLive ? Color.FromArgb(224, 210, 36, 48) : Color.FromArgb(215, 3, 8, 12));
                g.FillPath(chipFill, chipPath);
                TextRenderer.DrawText(g, duration, durationFont, chip, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            int textTop = imageRect.Bottom + S(scale, 10);
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(8.7f, 9.4f * scale));
            using var metaFont = LibraryFont("Segoe UI", Math.Max(7.8f, 8.2f * scale));
            TextRenderer.DrawText(g, item.Title, titleFont,
                new Rectangle(card.Left, textTop, card.Width, S(scale, 37)), TextMain,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string meta = item.Channel;
            string views = FormatYouTubeViews(item.ViewCount);
            if (!string.IsNullOrWhiteSpace(views))
                meta = string.IsNullOrWhiteSpace(meta) ? views : meta + "  ·  " + views;
            TextRenderer.DrawText(g, meta, metaFont,
                new Rectangle(card.Left, textTop + S(scale, 43), card.Width, S(scale, 24)), Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            _hits.Add(new HitZone { Bounds = card, Kind = HitKind.YouTubeVideo, Key = item.Url });
        }

        private void DrawYouTubeSkeletons(Graphics g, Rectangle viewport, float scale)
        {
            int gap = S(scale, 20);
            int columns = viewport.Width > S(scale, 1080) ? 4 : viewport.Width > S(scale, 720) ? 3 : 2;
            int width = (viewport.Width - gap * (columns - 1)) / columns;
            int imageH = (int)Math.Round(width * 9d / 16d);
            int pulse = 22 + (int)Math.Round((Math.Sin(Environment.TickCount64 / 280d) + 1d) * 10d);
            for (int i = 0; i < columns * 2; i++)
            {
                int row = i / columns, column = i % columns;
                Rectangle image = new(viewport.Left + column * (width + gap), viewport.Top + row * (imageH + S(scale, 96)), width, imageH);
                using var path = Round(image, S(scale, 7));
                using var fill = new SolidBrush(Color.FromArgb(pulse, TextMain));
                g.FillPath(fill, path);
                using var line = new SolidBrush(Color.FromArgb(Math.Max(12, pulse - 7), TextMain));
                g.FillRectangle(line, image.Left, image.Bottom + S(scale, 11), image.Width * 4 / 5, S(scale, 9));
                g.FillRectangle(line, image.Left, image.Bottom + S(scale, 27), image.Width * 3 / 5, S(scale, 7));
            }
        }

        private void DrawYouTubeEmpty(Graphics g, Rectangle viewport, float scale)
        {
            int icon = S(scale, 42);
            DrawIcon(g, new Rectangle(viewport.Left + (viewport.Width - icon) / 2, viewport.Top + S(scale, 42), icon, icon), "youtube", Color.FromArgb(155, 239, 68, 68));
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(12f, 14f * scale));
            using var body = LibraryFont("Segoe UI", Math.Max(8.5f, 9.4f * scale));
            string heading = string.IsNullOrWhiteSpace(_youtubeStatus)
                ? L("Nessun video trovato", "No videos found")
                : _youtubeStatus;
            TextRenderer.DrawText(g, heading, title,
                new Rectangle(viewport.Left, viewport.Top + S(scale, 98), viewport.Width, S(scale, 34)), TextMain,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L("Prova un'altra ricerca o aggiorna la pagina.", "Try another search or refresh the page."), body,
                new Rectangle(viewport.Left, viewport.Top + S(scale, 134), viewport.Width, S(scale, 30)), Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawYouTubeScrollIndicator(Graphics g, Rectangle viewport, int totalHeight, float scale)
        {
            if (totalHeight <= viewport.Height || _youtubeScrollMax <= 0)
                return;
            int trackH = Math.Max(S(scale, 80), viewport.Height - S(scale, 18));
            Rectangle track = new(viewport.Right - S(scale, 20), viewport.Top + S(scale, 9), Math.Max(3, S(scale, 4)), trackH);
            int thumbH = Math.Max(S(scale, 32), (int)Math.Round(track.Height * viewport.Height / (double)totalHeight));
            int thumbY = track.Top + (int)Math.Round((track.Height - thumbH) * _youtubeScroll / (double)_youtubeScrollMax);
            ScrollbarChrome.Draw(g, track, thumbY, thumbH);
        }

        private void DrawYouTubeStatusPill(Graphics g, Rectangle bounds, string text, float scale)
        {
            using var path = Round(bounds, bounds.Height / 2);
            using var fill = new SolidBrush(Color.FromArgb(210, Chrome));
            g.FillPath(fill, path);
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(7.2f, 7.6f * scale));
            TextRenderer.DrawText(g, text, font, bounds, TextMain,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void QueueYouTubeSearch(string? query)
        {
            if (!IsNativeYouTubeView)
                return;
            CancelYouTubeWork();
            int generation = ++_youtubeBrowseGeneration;
            var cts = new CancellationTokenSource();
            _youtubeBrowseCts = cts;
            string value = (query ?? string.Empty).Trim();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(360, cts.Token).ConfigureAwait(false);
                    await LoadYouTubeItemsAsync(value, generation, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            });
        }

        private void StartYouTubeSearchNow(string? query)
        {
            if (!IsNativeYouTubeView)
                return;
            string value = (query ?? string.Empty).Trim();
            if (TryGetYouTubeWatchUrl(value, out string directUrl))
            {
                OpenRequested?.Invoke(directUrl);
                return;
            }
            CancelYouTubeWork();
            int generation = ++_youtubeBrowseGeneration;
            var cts = new CancellationTokenSource();
            _youtubeBrowseCts = cts;
            _ = Task.Run(async () =>
            {
                try { await LoadYouTubeItemsAsync(value, generation, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            });
        }

        private async Task LoadYouTubeItemsAsync(string query, int generation, CancellationToken cancellationToken)
        {
            string effectiveQuery = string.IsNullOrWhiteSpace(query)
                ? (UiEnglish ? "new movie trailers 4K" : "trailer film italiani 4K")
                : query.Trim();
            PostYouTubeUi(generation, () =>
            {
                _youtubeLoading = true;
                _youtubeStatus = string.Empty;
                _youtubeScroll = 0;
                _contentLoadingTimer.Start();
                Invalidate();
            });

            string? ytDlp = YtDlpLocator.Find();
            if (string.IsNullOrWhiteSpace(ytDlp))
            {
                PostYouTubeFailure(generation, L("Motore YouTube non disponibile", "YouTube engine unavailable"));
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(28));
            string output;
            string error;
            try
            {
                string search = "ytsearch24:" + effectiveQuery;
                string args = "--no-warnings --skip-download --flat-playlist --playlist-end 24 --dump-json " + QuoteYouTubeArgument(search);
                using var process = new Process { StartInfo = YtDlpLocator.CreateProcessStartInfo(ytDlp, args), EnableRaisingEvents = true };
                if (!process.Start())
                    throw new InvalidOperationException("yt-dlp non avviato");
                using var registration = timeout.Token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                });
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                output = await stdout.ConfigureAwait(false);
                error = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                    throw new InvalidOperationException(CleanYouTubeError(error));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                PostYouTubeFailure(generation, L("YouTube non ha risposto in tempo", "YouTube took too long to respond"));
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                PostYouTubeFailure(generation, string.IsNullOrWhiteSpace(ex.Message)
                    ? L("Ricerca YouTube non riuscita", "YouTube search failed")
                    : ex.Message);
                return;
            }

            List<YouTubeBrowseItem> items = ParseYouTubeItems(output);
            cancellationToken.ThrowIfCancellationRequested();
            PostYouTubeUi(generation, () =>
            {
                _youtubeItems.Clear();
                _youtubeItems.AddRange(items);
                _youtubeLoading = false;
                _youtubeStatus = items.Count == 0
                    ? L("Nessun video trovato", "No videos found")
                    : string.Empty;
                Invalidate();
            });

            if (items.Count > 0)
                await DownloadYouTubeThumbnailsAsync(items, generation, cancellationToken).ConfigureAwait(false);
        }

        private async Task DownloadYouTubeThumbnailsAsync(IEnumerable<YouTubeBrowseItem> items, int generation, CancellationToken cancellationToken)
        {
            var tasks = items.Where(item => !string.IsNullOrWhiteSpace(item.ThumbnailUrl)).Select(async item =>
            {
                string destination = YouTubeThumbnailCachePath(item);
                if (File.Exists(destination) && IsGdiThumbnail(destination))
                {
                    item.ThumbnailPath = destination;
                    return;
                }
                await YouTubeThumbnailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using HttpResponseMessage response = await YouTubeBrowseHttp.GetAsync(item.ThumbnailUrl, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    string temporary = destination + ".tmp";
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (IsWebP(bytes))
                    {
                        using SKBitmap bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Miniatura WebP non valida");
                        using SKImage image = SKImage.FromBitmap(bitmap);
                        using SKData jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 91);
                        await using var converted = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                        jpeg.SaveTo(converted);
                        await converted.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(temporary, destination, overwrite: true);
                    item.ThumbnailPath = destination;
                    PostYouTubeUi(generation, Invalidate);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                finally { YouTubeThumbnailGate.Release(); }
            });
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        private void PostYouTubeFailure(int generation, string status)
        {
            PostYouTubeUi(generation, () =>
            {
                _youtubeItems.Clear();
                _youtubeLoading = false;
                _youtubeStatus = status;
                Invalidate();
            });
        }

        private void PostYouTubeUi(int generation, Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated)
                    return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || generation != _youtubeBrowseGeneration || !IsNativeYouTubeView)
                        return;
                    action();
                }));
            }
            catch { }
        }

        private void CancelYouTubeWork()
        {
            CancellationTokenSource? old = Interlocked.Exchange(ref _youtubeBrowseCts, null);
            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }
            _youtubeLoading = false;
        }

        private static List<YouTubeBrowseItem> ParseYouTubeItems(string output)
        {
            var result = new List<YouTubeBrowseItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    string id = JsonString(root, "id");
                    if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                        continue;
                    string title = JsonString(root, "title");
                    if (string.IsNullOrWhiteSpace(title))
                        continue;
                    string url = JsonString(root, "webpage_url");
                    if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        url = "https://www.youtube.com/watch?v=" + id;
                    string thumbnail = BestYouTubeThumbnail(root);
                    double duration = JsonDouble(root, "duration") ?? 0;
                    long? views = JsonLong(root, "view_count");
                    string liveStatus = JsonString(root, "live_status");
                    result.Add(new YouTubeBrowseItem
                    {
                        Id = id,
                        Url = url,
                        Title = title.Trim(),
                        Channel = FirstNonEmpty(JsonString(root, "channel"), JsonString(root, "uploader")),
                        Duration = duration > 0 ? FormatYouTubeDuration(duration) : string.Empty,
                        ViewCount = views,
                        IsLive = string.Equals(liveStatus, "is_live", StringComparison.OrdinalIgnoreCase),
                        ThumbnailUrl = thumbnail
                    });
                }
                catch { }
            }
            return result;
        }

        private static string BestYouTubeThumbnail(JsonElement root)
        {
            if (!root.TryGetProperty("thumbnails", out JsonElement thumbs) || thumbs.ValueKind != JsonValueKind.Array)
                return JsonString(root, "thumbnail");
            string best = string.Empty;
            long score = -1;
            foreach (JsonElement thumb in thumbs.EnumerateArray())
            {
                string url = JsonString(thumb, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                long width = JsonLong(thumb, "width") ?? 0;
                long height = JsonLong(thumb, "height") ?? 0;
                long next = width * Math.Max(1, height);
                if (next >= score) { score = next; best = url; }
            }
            return best;
        }

        private static string JsonString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return string.Empty;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        }

        private static double? JsonDouble(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double parsed) ? parsed : null;

        private static long? JsonLong(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long parsed) ? parsed : null;

        private static string FormatYouTubeDuration(double seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes}:{time.Seconds:00}";
        }

        private string FormatYouTubeViews(long? views)
        {
            if (!views.HasValue || views.Value < 0) return string.Empty;
            double value = views.Value;
            string suffix = UiEnglish ? " views" : " visualizzazioni";
            if (value >= 1_000_000_000) return (value / 1_000_000_000d).ToString(value >= 10_000_000_000 ? "0" : "0.#") + " Mld" + suffix;
            if (value >= 1_000_000) return (value / 1_000_000d).ToString(value >= 10_000_000 ? "0" : "0.#") + " Mln" + suffix;
            if (value >= 1_000) return (value / 1_000d).ToString(value >= 10_000 ? "0" : "0.#") + " mila" + suffix;
            return views.Value + suffix;
        }

        private static Rectangle CoverSourceForYouTube(Size source, Size target)
        {
            if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0)
                return new Rectangle(Point.Empty, source);
            double sourceRatio = source.Width / (double)source.Height;
            double targetRatio = target.Width / (double)target.Height;
            if (sourceRatio > targetRatio)
            {
                int width = Math.Max(1, (int)Math.Round(source.Height * targetRatio));
                return new Rectangle((source.Width - width) / 2, 0, width, source.Height);
            }
            int height = Math.Max(1, (int)Math.Round(source.Width / targetRatio));
            return new Rectangle(0, (source.Height - height) / 2, source.Width, height);
        }

        private static string QuoteYouTubeArgument(string value)
            => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static bool TryGetYouTubeWatchUrl(string value, out string url)
        {
            url = string.Empty;
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            if (!host.Contains("youtube.com") && !host.Contains("youtu.be")) return false;
            url = uri.AbsoluteUri;
            return true;
        }

        private static string CleanYouTubeError(string error)
        {
            string line = (error ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) line = line[6..].Trim();
            return string.IsNullOrWhiteSpace(line) ? "Ricerca YouTube non riuscita" : line;
        }

        private static string YouTubeThumbnailCachePath(YouTubeBrowseItem item)
        {
            string key = string.IsNullOrWhiteSpace(item.Id) ? item.ThumbnailUrl : item.Id;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "youtube-cache");
            return Path.Combine(root, hash[..24] + ".jpg");
        }

        private static bool IsWebP(byte[] bytes)
            => bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
               bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';

        private static bool IsGdiThumbnail(string path)
        {
            try
            {
                Span<byte> header = stackalloc byte[12];
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Read(header) < 4) return false;
                return (header[0] == 0xFF && header[1] == 0xD8) ||
                       (header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G') ||
                       (header[0] == (byte)'B' && header[1] == (byte)'M') ||
                       (header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F');
            }
            catch { return false; }
        }

        private static HttpClient CreateYouTubeBrowseHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(14) };
            try { client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer2025/1.0"); } catch { }
            return client;
        }

        private void DrawWebNotes(Graphics g, Rectangle bounds, string[] notes, float scale)
        {
            int gap = S(scale, 12);
            int width = bounds.Width / Math.Max(1, notes.Length);
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8.2f, 9.2f * scale));
            for (int i = 0; i < notes.Length; i++)
            {
                int cellLeft = bounds.Left + (int)Math.Round(i * bounds.Width / (double)notes.Length);
                int cellRight = bounds.Left + (int)Math.Round((i + 1) * bounds.Width / (double)notes.Length);
                Rectangle note = new(cellLeft, bounds.Top, cellRight - cellLeft, bounds.Height);
                int textWidth = TextRenderer.MeasureText(g, notes[i], font, Size.Empty, TextFormatFlags.NoPadding).Width;
                int iconSize = S(scale, 24), textGap = S(scale, 12);
                int groupWidth = Math.Min(note.Width - gap * 2, iconSize + textGap + textWidth);
                int groupX = note.Left + (note.Width - groupWidth) / 2;
                DrawIcon(g, new Rectangle(groupX, note.Top + (note.Height - iconSize) / 2, iconSize, iconSize),
                    i == 0 ? "network" : i == 1 ? "scan" : "play", Color.FromArgb(130, 185, 255));
                if (i > 0)
                { using var divider = new Pen(Color.FromArgb(65, HUD.Theme.Border)); g.DrawLine(divider, note.Left, note.Top, note.Left, note.Bottom); }
                TextRenderer.DrawText(g, notes[i], font, new Rectangle(groupX + iconSize + textGap, note.Top, Math.Max(1, groupWidth - iconSize - textGap), note.Height),
                    Color.FromArgb(204, TextMain), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        private void DrawWebAction(Graphics g, Rectangle bounds, string text, string icon, bool primary, HitKind kind, float scale)
        {
            bool hover = bounds.Contains(_lastMouse);
            using (var path = Round(bounds, S(scale, 8)))
            using (var fill = new SolidBrush(primary ? Accent : (hover ? ChromeHover : Chrome)))
            using (var border = new Pen(primary ? BorderAccent : Color.FromArgb(72, HUD.Theme.Border)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            int iconSize = S(scale, 17);
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8f, 9f * scale));
            int textWidth = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int groupWidth = Math.Min(bounds.Width - S(scale, 16), iconSize + S(scale, 9) + textWidth);
            Rectangle iconRect = new(bounds.Left + (bounds.Width - groupWidth) / 2, bounds.Top + (bounds.Height - iconSize) / 2, iconSize, iconSize);
            if (!DrawIcon(g, iconRect, icon, Color.White))
                DrawLibraryFallbackGlyph(g, iconRect, icon, Color.White, scale);
            TextRenderer.DrawText(g, text, font,
                new Rectangle(iconRect.Right + S(scale, 9), bounds.Top, groupWidth - iconSize - S(scale, 9), bounds.Height),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = bounds, Kind = kind });
        }
    }
}
