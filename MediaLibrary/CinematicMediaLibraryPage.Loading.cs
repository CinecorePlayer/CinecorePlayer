#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private DateTime _homeTransitionStartedUtc;
        private bool _homeTransitionContentReady;

        private void SetContentLoading(bool loading, string? categoryLabel = null)
        {
            if (_view == PageView.Home)
                categoryLabel = "Home";
            if (!loading && _view == PageView.Home && _homeTransitionStartedUtc != default)
            {
                _homeTransitionContentReady = true;
                return;
            }
            if (loading && _view == PageView.Home && _homeTransitionStartedUtc != default)
                _homeTransitionContentReady = false;
            _contentLoading = loading;
            if (!string.IsNullOrWhiteSpace(categoryLabel))
                _contentLoadingLabel = categoryLabel!;

            try
            {
                if (loading && Visible)
                    _contentLoadingTimer.Start();
                else
                    _contentLoadingTimer.Stop();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task FinishHomeTransitionAsync(int version)
        {
            while (!IsDisposed && !Disposing && version == _categoryTransitionVersion && _view == PageView.Home)
            {
                await System.Threading.Tasks.Task.Delay(75);
                if (!_homeTransitionContentReady) continue;
                double elapsed = (DateTime.UtcNow - _homeTransitionStartedUtc).TotalMilliseconds;
                bool imagesPending;
                lock (_imageCacheSync) imagesPending = _imageLoadRequests.Count > 0;
                // Reveal as soon as the model and visible artwork settle. The old
                // 650 ms floor made a cached Home feel like a second startup.
                if (imagesPending && elapsed < 650) continue;
                _homeTransitionStartedUtc = default;
                SetContentLoading(false);
                Invalidate();
                return;
            }
        }

        private string LoadingCategoryLabel(string category)
        {
            return NormalizeCategoryKey(category) switch
            {
                "Movies" => L("film", "movies"),
                "TV Series" => L("serie TV", "TV series"),
                "Videos" => L("video", "videos"),
                "Music" => L("musica", "music"),
                "Photos" => L("foto", "photos"),
                "Favourites" => L("preferiti", "favourites"),
                "WatchHistory" => L("diario", "diary"),
                "Playlists" => L("playlist", "playlists"),
                _ => L("libreria", "library")
            };
        }

        private void DrawContentLoading(Graphics g, Rectangle main)
        {
            float scale = LayoutScale(main);
            int padX = Math.Max(S(scale, 32), (int)Math.Round(main.Width * 0.024));
            Rectangle pageContent = new Rectangle(main.Left + padX, main.Top + S(scale, 18),
                Math.Max(1, main.Width - padX * 2), Math.Max(1, main.Height - S(scale, 38)));
            Rectangle content;
            if (_view == PageView.Home)
            {
                int bodyTop = pageContent.Top + S(scale, 50);
                content = new Rectangle(pageContent.Left, bodyTop, pageContent.Width,
                    Math.Max(1, pageContent.Bottom - bodyTop));
            }
            else if (string.Equals(_category, "Music", StringComparison.OrdinalIgnoreCase))
            {
                int bodyTop = pageContent.Top + S(scale, _musicTab == 3 ? 58 + 144 + 18 - 48 + 55 + 74 : 58 + 144 + 18 + 39);
                content = new Rectangle(pageContent.Left, bodyTop,
                    Math.Max(1, pageContent.Width - (_musicTab == 3 ? 0 : S(scale, 64))),
                    Math.Max(1, pageContent.Bottom - bodyTop - S(scale, _musicTab == 3 ? 24 : 18)));
            }
            else
                content = CollectionEmptyViewport(pageContent, scale);
            if (content.Width <= 0 || content.Height <= 0)
                return;

            using (var cleanBody = new SolidBrush(Back))
                g.FillRectangle(cleanBody, new Rectangle(main.Left, content.Top, main.Width, Math.Max(1, main.Bottom - content.Top)));
            const int dotCount = 3;
            int phase = (int)((Environment.TickCount64 / 160) % dotCount);
            int dotSize = S(scale, 9);
            int dotGap = S(scale, 12);
            int dotsWidth = dotCount * dotSize + (dotCount - 1) * dotGap;
            int dotsX = content.Left + (content.Width - dotsWidth) / 2;
            int dotsY = content.Top + (content.Height - S(scale, 64)) / 2;
            for (int i = 0; i < dotCount; i++)
            {
                int alpha = (phase + i) % dotCount == 0 ? 255 : 86;
                using var dot = new SolidBrush(Color.FromArgb(alpha, Accent));
                g.FillEllipse(dot, dotsX + i * (dotSize + dotGap), dotsY, dotSize, dotSize);
            }

            string category = string.IsNullOrWhiteSpace(_contentLoadingLabel) ? LoadingCategoryLabel(_category) : _contentLoadingLabel;
            string message = L($"Caricamento {category}...", $"Loading {category}...");
            using var font = LibraryFont("Segoe UI Semibold", 11.2f, FontStyle.Regular);
            TextRenderer.DrawText(g, message, font,
                new Rectangle(content.Left + S(scale, 20), dotsY + S(scale, 24), Math.Max(1, content.Width - S(scale, 40)), S(scale, 40)),
                TextMain,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}
