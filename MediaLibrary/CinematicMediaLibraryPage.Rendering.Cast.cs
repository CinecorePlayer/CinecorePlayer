#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private void DrawDetailCast(Graphics g, Rectangle r, LibraryItem item, float scale)
        {
            if (r.Width <= 2 || r.Height <= 2)
                return;

            int side = S(scale, 8);
            int headerH = S(scale, 42);
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(13.2f, 15.5f * scale));
            using var metaFont = LibraryFont("Segoe UI", Math.Max(7.8f, 8.6f * scale));
            TextRenderer.DrawText(g, L("Cast e troupe", "Cast & crew"), titleFont,
                new Rectangle(r.Left + side, r.Top, r.Width / 2, headerH), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            string credits = string.Join("   •   ", new[]
            {
                string.IsNullOrWhiteSpace(item.Director) ? null : L("Regia  ", "Director  ") + item.Director,
                item.Rating is > 0 ? $"TMDb  {item.Rating.Value:0.0}/10" : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            TextRenderer.DrawText(g, credits, metaFont,
                new Rectangle(r.Left + r.Width / 2, r.Top, r.Width / 2 - side, headerH), Color.FromArgb(194, 211, 224),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            using (var divider = new Pen(Color.FromArgb(32, 176, 200, 218), 1f))
                g.DrawLine(divider, r.Left + side, r.Top + headerH, r.Right - side, r.Top + headerH);

            var allCast = item.CastMembers.Where(person => !string.IsNullOrWhiteSpace(person.Name)).ToList();
            if (allCast.Count == 0)
            {
                string status = item.RichDetailsResolved
                    ? L("Cast non disponibile per questo titolo", "Cast is not available for this title")
                    : L("Sto caricando cast e fotografie…", "Loading cast and photographs…");
                DrawCastLoadingState(g, new Rectangle(r.Left + side, r.Top + headerH + S(scale, 16), r.Width - side * 2, Math.Max(S(scale, 70), r.Height - headerH - S(scale, 16))), status, scale);
                _detailCastVisibleCount = 0;
                return;
            }

            int arrowW = S(scale, 38);
            int viewportLeft = r.Left + arrowW + S(scale, 10);
            int viewportRight = r.Right - arrowW - S(scale, 10);
            int viewportW = Math.Max(1, viewportRight - viewportLeft);
            int gap = S(scale, 14);
            int preferred = S(scale, 166);
            int visible = Math.Clamp((viewportW + gap) / Math.Max(1, preferred + gap), 4, 7);
            visible = Math.Min(visible, allCast.Count);
            _detailCastVisibleCount = visible;
            int maxFirst = Math.Max(0, allCast.Count - visible);
            _detailCastFirst = Math.Clamp(_detailCastFirst, 0, maxFirst);

            int cardW = Math.Max(S(scale, 104), (viewportW - gap * Math.Max(0, visible - 1)) / Math.Max(1, visible));
            int totalW = cardW * visible + gap * Math.Max(0, visible - 1);
            int startX = viewportLeft + Math.Max(0, (viewportW - totalW) / 2);
            int top = r.Top + headerH + S(scale, 14);
            int labelH = S(scale, 58);
            int availableImageH = Math.Max(S(scale, 124), r.Bottom - top - labelH - S(scale, 18));
            int imageH = Math.Min(availableImageH, Math.Max(S(scale, 150), (int)Math.Round(cardW * 1.43)));

            using var nameFont = LibraryFont("Segoe UI Semibold", Math.Max(8.0f, 8.9f * scale));
            using var roleFont = LibraryFont("Segoe UI", Math.Max(7.0f, 7.6f * scale));
            for (int slot = 0; slot < visible; slot++)
            {
                var person = allCast[_detailCastFirst + slot];
                Rectangle portrait = new(startX + slot * (cardW + gap), top, cardW, imageH);
                bool hover = portrait.Contains(_lastMouse);

                using (var shadowPath = Round(new Rectangle(portrait.X + S(scale, 3), portrait.Y + S(scale, 5), portrait.Width, portrait.Height), S(scale, 8)))
                using (var shadow = new SolidBrush(Color.FromArgb(86, 0, 0, 0)))
                    g.FillPath(shadow, shadowPath);

                using Region oldClip = g.Clip.Clone();
                using (var portraitPath = Round(portrait, S(scale, 8)))
                    g.SetClip(portraitPath, CombineMode.Intersect);
                bool drawn = DrawImagePath(g, portrait, person.ProfilePath, requireLandscape: false, tint: false,
                    preserveAspectWhenWide: false, horizontalFocus: 0.5f, verticalFocus: 0.18f, destinationBleed: 1);
                if (!drawn)
                    DrawCastPlaceholder(g, portrait, scale);
                Rectangle lowerFade = new(portrait.Left, portrait.Bottom - Math.Min(portrait.Height, S(scale, 58)), portrait.Width, Math.Min(portrait.Height, S(scale, 58)));
                using (var fade = new LinearGradientBrush(lowerFade, Color.Transparent, Color.FromArgb(104, 0, 5, 9), LinearGradientMode.Vertical))
                    g.FillRectangle(fade, lowerFade);
                g.Clip = oldClip;

                using (var outline = new Pen(hover ? Color.FromArgb(164, 235, 240, 244) : Color.FromArgb(70, 208, 222, 233), hover ? 1.5f : 1f))
                using (var portraitPath = Round(portrait, S(scale, 8)))
                    g.DrawPath(outline, portraitPath);

                Rectangle nameRect = new(portrait.Left, portrait.Bottom + S(scale, 7), portrait.Width, S(scale, 20));
                Rectangle roleRect = new(portrait.Left + S(scale, 4), nameRect.Bottom + S(scale, 1), portrait.Width - S(scale, 8), S(scale, 30));
                TextRenderer.DrawText(g, person.Name, nameFont, nameRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(person.Character) ? L("Interprete", "Cast") : person.Character, roleFont, roleRect,
                    Color.FromArgb(174, 197, 214), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            Rectangle leftArrow = new(r.Left + S(scale, 1), top + Math.Max(0, (imageH - arrowW) / 2), arrowW, arrowW);
            Rectangle rightArrow = new(r.Right - arrowW - S(scale, 1), leftArrow.Top, arrowW, arrowW);
            bool canPrevious = _detailCastFirst > 0;
            bool canNext = _detailCastFirst < maxFirst;
            DrawCastArrow(g, leftArrow, right: false, enabled: canPrevious, scale);
            DrawCastArrow(g, rightArrow, right: true, enabled: canNext, scale);
            if (canPrevious)
                _hits.Add(new HitZone { Bounds = leftArrow, Kind = HitKind.DetailCastPrevious });
            if (canNext)
                _hits.Add(new HitZone { Bounds = rightArrow, Kind = HitKind.DetailCastNext });

            if (allCast.Count > visible)
            {
                int pageCount = maxFirst + 1;
                int dotGap = S(scale, 8);
                int dotsW = pageCount * S(scale, 5) + Math.Max(0, pageCount - 1) * dotGap;
                int dotsX = r.Left + (r.Width - dotsW) / 2;
                int dotsY = Math.Min(r.Bottom - S(scale, 7), top + imageH + labelH + S(scale, 7));
                for (int i = 0; i < pageCount; i++)
                {
                    int d = i == _detailCastFirst ? S(scale, 6) : S(scale, 4);
                    using var dot = new SolidBrush(i == _detailCastFirst ? Accent : Color.FromArgb(92, 194, 210, 222));
                    g.FillEllipse(dot, dotsX + i * (S(scale, 5) + dotGap), dotsY - d / 2, d, d);
                }
            }
        }

        private void MoveDetailCastCarousel(int delta)
        {
            if (_detailItem == null || !_detailCastVisible || delta == 0)
                return;
            int count = _detailItem.CastMembers.Count(person => !string.IsNullOrWhiteSpace(person.Name));
            int visible = Math.Max(1, _detailCastVisibleCount);
            int maxFirst = Math.Max(0, count - visible);
            int next = Math.Clamp(_detailCastFirst + Math.Sign(delta), 0, maxFirst);
            if (next == _detailCastFirst)
                return;
            _detailCastFirst = next;
            Invalidate();
        }

        private void DrawCastLoadingState(Graphics g, Rectangle r, string text, float scale)
        {
            int dot = S(scale, 6);
            int gap = S(scale, 12);
            int dotsWidth = dot * 3 + gap * 2;
            int dotsX = r.Left + (r.Width - dotsWidth) / 2;
            int dotsY = r.Top + Math.Max(0, (r.Height - S(scale, 72)) / 2);
            int phase = (int)((Environment.TickCount64 / 130) % 3);
            for (int i = 0; i < 3; i++)
            {
                int distance = (i - phase + 3) % 3;
                int alpha = distance == 0 ? 255 : distance == 1 ? 142 : 72;
                int pulse = distance == 0 ? S(scale, 2) : 0;
                using var brush = new SolidBrush(Color.FromArgb(alpha, Accent));
                g.FillEllipse(brush, dotsX + i * (dot + gap) - pulse / 2, dotsY - pulse / 2, dot + pulse, dot + pulse);
            }
            using var font = LibraryFont("Segoe UI", Math.Max(8.4f, 9.1f * scale));
            TextRenderer.DrawText(g, text, font, new Rectangle(r.Left + S(scale, 8), dotsY + S(scale, 18), Math.Max(1, r.Width - S(scale, 16)), S(scale, 42)),
                Color.FromArgb(190, 210, 224), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private static void DrawCastArrow(Graphics g, Rectangle r, bool right, bool enabled, float scale)
        {
            Color color = enabled ? Color.FromArgb(232, 242, 248) : Color.FromArgb(48, 170, 190, 206);
            if (enabled)
            {
                using var hoverFill = new SolidBrush(Color.FromArgb(42, 9, 22, 34));
                using var path = Round(r, r.Width / 2);
                g.FillPath(hoverFill, path);
            }
            using var pen = new Pen(color, Math.Max(1.4f, 1.8f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            int dx = S(scale, 5);
            int dy = S(scale, 8);
            if (right)
                g.DrawLines(pen, new[] { new Point(cx - dx, cy - dy), new Point(cx + dx, cy), new Point(cx - dx, cy + dy) });
            else
                g.DrawLines(pen, new[] { new Point(cx + dx, cy - dy), new Point(cx - dx, cy), new Point(cx + dx, cy + dy) });
        }

        private static void DrawCastPlaceholder(Graphics g, Rectangle r, float scale)
        {
            using var background = new LinearGradientBrush(r, Color.FromArgb(27, 53, 72), Color.FromArgb(4, 15, 24), LinearGradientMode.Vertical);
            g.FillRectangle(background, r);
            int cx = r.Left + r.Width / 2;
            int head = Math.Max(S(scale, 20), Math.Min(S(scale, 34), r.Width / 5));
            int headY = r.Top + Math.Max(S(scale, 28), r.Height / 4);
            using var silhouette = new SolidBrush(Color.FromArgb(74, 176, 199, 216));
            g.FillEllipse(silhouette, cx - head / 2, headY, head, head);
            Rectangle shoulders = new(r.Left + r.Width / 4, headY + head + S(scale, 13), r.Width / 2, Math.Max(S(scale, 38), r.Height / 3));
            using var body = Round(shoulders, shoulders.Width / 2);
            g.FillPath(silhouette, body);
        }
    }
}
