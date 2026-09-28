#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025
{
    internal sealed class InfoOverlay : Control
    {
        public event Action? CloseRequested;

        public struct Stats
        {
            public string Title;
            public string VideoIn, VideoOut, VideoCodec, VideoPrimaries, VideoTransfer, VideoBitrateNow, VideoBitrateAvg;
            public string AudioIn, AudioOut, AudioBitrateNow, AudioBitrateAvg;
            public string Renderer, HdrMode;
            public bool Upscaling, Bitstream, RtxHdr;
        }

        private Stats _s;
        private Rectangle _closeRect = Rectangle.Empty;
        private string _uiLanguage = "it";

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool AutoHeight { get; set; } = true;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MinCardHeight { get; set; } = 420;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MaxCardHeight { get; set; } = 700;

        private static Color CardTop => Color.FromArgb(232, HUD.Theme.Card);
        private static Color CardBottom => Color.FromArgb(224, HUD.Theme.Panel);
        private static Color Border => Color.FromArgb(50, HUD.Theme.Border);
        private static Color TextMain => HUD.Theme.Text;
        private static Color TextMuted => HUD.Theme.Muted;
        private static Color Accent => HUD.Theme.Accent;
        private bool UiEnglish => string.Equals(_uiLanguage, "en", StringComparison.OrdinalIgnoreCase);
        private string L(string italian, string english) => UiEnglish ? english : italian;

        private const int Pad = 24;
        private const int RowH = 22;

        public InfoOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = MinCardHeight;
            Cursor = Cursors.Default;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var host = FindForm();
            if (host != null && host.TransparencyKey != Color.Empty)
            {
                e.Graphics.Clear(host.TransparencyKey);
                return;
            }

            if (BackColor != Color.Transparent)
                base.OnPaintBackground(e);
        }

        public void SetStats(Stats s)
        {
            _s = s;
            if (AutoHeight) AdjustHeightToContent(Width);
            Invalidate();
        }

        public void SetLanguage(string? language)
        {
            _uiLanguage = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (AutoHeight) AdjustHeightToContent(Width);
        }

        public void AdjustHeightToContent(int availableWidth)
        {
            if (!AutoHeight || availableWidth <= 0) return;
            int next = Math.Max(MinCardHeight, Math.Min(MaxCardHeight, CalcPreferredHeight(availableWidth)));
            if (Math.Abs(Height - next) > 6)
                Height = next;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle card = new(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            if (card.Width <= 0 || card.Height <= 0)
                return;

            using (var path = RoundedRect(card, 10))
            using (var bg = new SolidBrush(Color.FromArgb(242, HUD.Theme.Panel)))
            using (var pen = new Pen(Border, 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }

            int x = card.X + Pad;
            int y = card.Y + 20;
            int w = card.Width - Pad * 2;

            using var titleFont = new Font("Segoe UI Semibold", 11.5f);
            using var sectionFont = new Font("Segoe UI Semibold", 11.0f);
            using var keyFont = new Font("Segoe UI", 9.0f);
            using var valueFont = new Font("Segoe UI", 9.0f);

            TextRenderer.DrawText(g, L("Info riproduzione", "Playback info"), titleFont, new Rectangle(x, y, w - 36, 28), TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _closeRect = new Rectangle(card.Right - 40, card.Top + 20, 22, 22);
            DrawCloseGlyph(g, _closeRect);
            y += 42;

            DrawSection(g, sectionFont, keyFont, valueFont, "Video", "video", ref y, x, w, new[]
            {
                (L("Ingresso", "Input"), _s.VideoIn),
                (L("Uscita", "Output"), _s.VideoOut),
                ("Codec", _s.VideoCodec),
                (L("Colore", "Color"), JoinInfo(_s.VideoPrimaries, _s.VideoTransfer, " / ")),
                ("Bitrate", Bitrates(_s.VideoBitrateNow, _s.VideoBitrateAvg)),
                ("HDR", _s.HdrMode)
            });

            DrawSection(g, sectionFont, keyFont, valueFont, "Audio", "audio", ref y, x, w, new[]
            {
                (L("Ingresso", "Input"), _s.AudioIn),
                (L("Uscita", "Output"), _s.AudioOut + (_s.Bitstream ? " · Bitstream" : " · PCM")),
                ("Bitrate", Bitrates(_s.AudioBitrateNow, _s.AudioBitrateAvg))
            });

            DrawSection(g, sectionFont, keyFont, valueFont, L("Sistema", "System"), "system", ref y, x, w, new[]
            {
                ("Renderer", _s.Renderer),
                (L("Elaborazione", "Processing"), _s.Upscaling || _s.RtxHdr ? JoinInfo(_s.Upscaling ? "Upscaling" : "", _s.RtxHdr ? "RTX HDR" : "", " · ") : L("Nessuna", "None"))
            });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = _closeRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _closeRect.Contains(e.Location))
            {
                CloseRequested?.Invoke();
                return;
            }
            base.OnMouseUp(e);
        }

        private void DrawSection(
            Graphics g,
            Font sectionFont,
            Font keyFont,
            Font valueFont,
            string title,
            string icon,
            ref int y,
            int x,
            int width,
            IReadOnlyList<(string Key, string Value)> rows)
        {
            if (y > Height - 30)
                return;

            DrawSectionIcon(g, new Rectangle(x, y + 6, 16, 16), icon);
            TextRenderer.DrawText(g, title, sectionFont, new Rectangle(x + 28, y, width - 28, 28), TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += 30;

            int labelW = Math.Min(132, Math.Max(102, width / 3));
            foreach (var row in rows)
            {
                if (y > Height - 30)
                    break;

                TextRenderer.DrawText(g, row.Key, keyFont, new Rectangle(x + 28, y, labelW, RowH), TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, CleanValue(row.Value), valueFont, new Rectangle(x + 28 + labelW, y, width - labelW - 28, RowH), TextMain,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                y += RowH;
            }

            y += 6;
            using var sep = new Pen(Color.FromArgb(32, 255, 255, 255));
            g.DrawLine(sep, x, y, x + width, y);
            y += 10;
        }

        private string Bitrates(string current, string average)
            => CleanValue(current) + " · " + L("media ", "avg ") + CleanValue(average);

        private static string JoinInfo(string a, string b, string sep)
        {
            a = CleanValue(a);
            b = CleanValue(b);
            if (a == "n/d") return b;
            if (b == "n/d") return a;
            return a + sep + b;
        }

        private static string CleanValue(string? value)
            => string.IsNullOrWhiteSpace(value) || string.Equals(value, "n/d", StringComparison.OrdinalIgnoreCase) ? "n/d" : value.Trim();

        private static void DrawCloseGlyph(Graphics g, Rectangle r)
        {
            if (AssetIconService.DrawCustom(g, r, "close", Color.FromArgb(215, 220, 230, 238))) return;
            using var pen = new Pen(Color.FromArgb(215, 220, 230, 238), 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, r.Left + 5, r.Top + 5, r.Right - 5, r.Bottom - 5);
            g.DrawLine(pen, r.Right - 5, r.Top + 5, r.Left + 5, r.Bottom - 5);
        }

        private static void DrawSectionIcon(Graphics g, Rectangle r, string icon)
        {
            if (AssetIconService.DrawCustom(g, r, icon, Accent)) return;
            using var pen = new Pen(Accent, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (icon == "audio")
            {
                g.DrawLine(pen, r.Left + 2, r.Top + 9, r.Left + 7, r.Top + 9);
                g.DrawLine(pen, r.Left + 7, r.Top + 5, r.Left + 12, r.Top + 2);
                g.DrawLine(pen, r.Left + 7, r.Bottom - 5, r.Left + 12, r.Bottom - 2);
                g.DrawLine(pen, r.Left + 12, r.Top + 2, r.Left + 12, r.Bottom - 2);
                g.DrawArc(pen, r.Left + 10, r.Top + 4, 8, 10, -45, 90);
                return;
            }

            if (icon == "system")
            {
                g.DrawRectangle(pen, r.Left + 3, r.Top + 2, r.Width - 6, r.Height - 5);
                g.DrawLine(pen, r.Left + 6, r.Bottom - 2, r.Right - 6, r.Bottom - 2);
                return;
            }

            g.DrawRectangle(pen, r.Left + 1, r.Top + 4, r.Width - 2, r.Height - 8);
            g.DrawLine(pen, r.Left + 5, r.Bottom - 3, r.Right - 5, r.Bottom - 3);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int rad = Math.Max(1, Math.Min(radius, Math.Min(Math.Max(1, r.Width), Math.Max(1, r.Height)) / 2));
            int d = rad * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private int CalcPreferredHeight(int availableWidth)
        {
            int rows = 6 + 3 + 2;
            return 24 + 42 + (30 * 3) + (rows * RowH) + (16 * 3) + 16;
        }
    }
}
