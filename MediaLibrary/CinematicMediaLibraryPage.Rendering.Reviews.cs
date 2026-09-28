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
        private void DrawDetailReviews(Graphics g, Rectangle r, LibraryItem item, float scale)
        {
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(12f, 15f * scale));
            using var sourceFont = LibraryFont("Segoe UI Semibold", Math.Max(8f, 9f * scale));
            using var smallFont = LibraryFont("Segoe UI", Math.Max(7.1f, 8f * scale));
            TextRenderer.DrawText(g, L("Recensioni e valutazioni", "Reviews & ratings"), titleFont,
                new Rectangle(r.Left, r.Top, r.Width, S(scale, 28)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int sourceTop = r.Top + S(scale, 38);
            int sourceGap = S(scale, 10);
            int sourceW = Math.Max(S(scale, 132), (r.Width - sourceGap * 3) / 4);
            string tmdbScore = item.Rating.HasValue
                ? item.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture) + "/10"
                : L("Scheda", "Details");

            var sources = new List<(string Name, string Value, string Url)>
            {
                ("TMDb", tmdbScore, BuildReviewSourceUrl("tmdb", item)),
                ("IMDb", L("Scheda", "Details"), BuildReviewSourceUrl("imdb", item)),
                ("Letterboxd", L("Scheda", "Details"), BuildReviewSourceUrl("letterboxd", item)),
                ("Metacritic", L("Scheda", "Details"), BuildReviewSourceUrl("metacritic", item))
            };

            for (int i = 0; i < sources.Count; i++)
            {
                Rectangle source = new(r.Left + i * (sourceW + sourceGap), sourceTop, sourceW, S(scale, 66));
                DrawReviewSource(g, source, sources[i].Name, sources[i].Value, scale);
                if (!string.IsNullOrWhiteSpace(sources[i].Url))
                    _hits.Add(new HitZone { Bounds = source, Kind = HitKind.DetailReviewSource, Key = sources[i].Url });
            }

            int reviewsTop = sourceTop + S(scale, 84);
            int reviewsH = Math.Max(1, r.Bottom - reviewsTop);
            if (!item.RichDetailsResolved)
            {
                DrawCastLoadingState(g, new Rectangle(r.Left, reviewsTop, r.Width, reviewsH),
                    L("Carico recensioni e collegamenti…", "Loading reviews and links…"), scale);
                return;
            }

            if (item.Reviews.Count == 0)
            {
                Rectangle empty = new(r.Left, reviewsTop + S(scale, 8), r.Width, Math.Min(reviewsH - S(scale, 8), S(scale, 112)));
                int iconSize = S(scale, 28);
                Rectangle icon = new(empty.Left + S(scale, 4), empty.Top + (empty.Height - iconSize) / 2, iconSize, iconSize);
                using (var ring = new Pen(Color.FromArgb(145, HUD.Theme.SubtleText), Math.Max(1.2f, 1.4f * scale))) g.DrawEllipse(ring, icon);
                using (var dot = new SolidBrush(Color.FromArgb(170, HUD.Theme.SubtleText)))
                {
                    int d = Math.Max(2, S(scale, 3));
                    g.FillEllipse(dot, icon.Left + (icon.Width - d) / 2, icon.Top + S(scale, 6), d, d);
                    g.FillRectangle(dot, icon.Left + (icon.Width - d) / 2, icon.Top + S(scale, 12), d, S(scale, 9));
                }
                int textLeft = icon.Right + S(scale, 14);
                using var emptyTitle = LibraryFont("Segoe UI Semibold", Math.Max(8.6f, 9.5f * scale));
                TextRenderer.DrawText(g, L("Nessun estratto disponibile", "No excerpts available"), emptyTitle,
                    new Rectangle(textLeft, empty.Top + S(scale, 23), empty.Right - textLeft, S(scale, 24)), TextMain,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, L("I collegamenti alle fonti restano disponibili qui sopra.", "Source links remain available above."), smallFont,
                    new Rectangle(textLeft, empty.Top + S(scale, 50), empty.Right - textLeft, S(scale, 24)), HUD.Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                return;
            }

            int count = Math.Min(2, item.Reviews.Count);
            int cardGap = S(scale, 12);
            int cardW = Math.Max(1, (r.Width - cardGap * (count - 1)) / count);
            int cardH = Math.Max(S(scale, 112), Math.Min(reviewsH, S(scale, 178)));
            for (int i = 0; i < count; i++)
            {
                Rectangle card = new(r.Left + i * (cardW + cardGap), reviewsTop, cardW, cardH);
                DrawReviewSnippet(g, card, item.Reviews[i], sourceFont, smallFont, scale);
            }
        }

        private void DrawReviewSource(Graphics g, Rectangle r, string source, string value, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            using var path = Round(r, S(scale, 8));
            using var fill = new SolidBrush(hover ? Color.FromArgb(102, 27, 39, 52) : Color.FromArgb(84, 13, 23, 33));
            using var border = new Pen(hover ? Color.FromArgb(138, BorderAccent) : Color.FromArgb(58, BorderAccent), 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            using var nameFont = LibraryFont("Segoe UI Semibold", Math.Max(7.7f, 8.5f * scale));
            using var valueFont = LibraryFont("Segoe UI Semibold", Math.Max(9.4f, 11f * scale));
            TextRenderer.DrawText(g, source.ToUpperInvariant(), nameFont,
                new Rectangle(r.Left + S(scale, 13), r.Top + S(scale, 8), r.Width - S(scale, 26), S(scale, 18)),
                Color.FromArgb(142, 164, 181), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, value, valueFont,
                new Rectangle(r.Left + S(scale, 13), r.Top + S(scale, 27), r.Width - S(scale, 26), S(scale, 29)),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawReviewSnippet(Graphics g, Rectangle r, MovieMetadataService.RichReview review, Font authorFont, Font bodyFont, float scale)
        {
            using var path = Round(r, S(scale, 8));
            using var fill = new SolidBrush(Color.FromArgb(98, 5, 14, 23));
            using var border = new Pen(Color.FromArgb(48, 126, 148, 165), 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            string author = string.IsNullOrWhiteSpace(review.Author) ? "TMDb user" : review.Author;
            string rating = review.Rating.HasValue ? review.Rating.Value.ToString("0.#", CultureInfo.InvariantCulture) + "/10" : "TMDb";
            TextRenderer.DrawText(g, author, authorFont,
                new Rectangle(r.Left + S(scale, 14), r.Top + S(scale, 10), r.Width - S(scale, 90), S(scale, 22)),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, rating, authorFont,
                new Rectangle(r.Right - S(scale, 76), r.Top + S(scale, 10), S(scale, 62), S(scale, 22)),
                Color.FromArgb(169, 204, 232), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            Rectangle body = new(r.Left + S(scale, 14), r.Top + S(scale, 39), r.Width - S(scale, 28), r.Height - S(scale, 51));
            string fitted = FitTextWithEllipsis(review.Content, bodyFont, body.Size,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, fitted, bodyFont, body, Color.FromArgb(202, 216, 226),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
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
