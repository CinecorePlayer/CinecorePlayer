#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    // Il cast del film in riproduzione, come in Spotlight: una fascia in basso con fotografie, nomi e
    // personaggi. Mentre e' aperta prende il posto di timeline e comandi; si richiude con la freccia in
    // alto al centro, con il tasto del cast, con Esc o freccia giu'.
    internal sealed partial class HudOverlay
    {
        private bool _castOpen;
        private string _castTitle = "", _castMeta = "";
        private readonly List<MovieMetadataService.RichCastMember> _castMembers = new();
        private readonly Dictionary<int, Image> _castPhotos = new();
        private CancellationTokenSource? _castStop;
        private int _castFirst, _castVisibleCount;
        private Rectangle _castCloseRect, _castLeftRect, _castRightRect;

        public bool CastOpen => _castOpen;

        private int CastD(float value) => (int)Math.Round(value * HudLayoutScale);
        private Font CastFont(string family, float points) => global::CinecorePlayer2025.AppFonts.Create(family, points * 96f / 72f * HudLayoutScale, FontStyle.Regular, GraphicsUnit.Pixel);
        private Rectangle CastSheetBounds
        {
            get
            {
                int height = Math.Min(CastD(340), Math.Max(1, Height - CastD(150)));
                // Sopra la fascia restano la dissolvenza e la freccia di chiusura.
                return new Rectangle(0, Math.Max(0, Height - height - CastD(52)), Width, height + CastD(52));
            }
        }

        public void OpenCast(string title, string meta, IEnumerable<MovieMetadataService.RichCastMember> members)
        {
            CloseCast();
            _castTitle = title ?? "";
            _castMeta = meta ?? "";
            _castMembers.AddRange(members.Where(m => !string.IsNullOrWhiteSpace(m.Name)).Take(20));
            if (_castMembers.Count == 0) return;
            _castOpen = true;
            _castFirst = 0;
            _castStop = new CancellationTokenSource();
            LoadCastPhotos(_castStop.Token);
            ShowOnce(2000);
            Invalidate();
        }

        public void CloseCast()
        {
            if (!_castOpen && _castMembers.Count == 0) return;
            _castOpen = false;
            try { _castStop?.Cancel(); _castStop?.Dispose(); } catch { }
            _castStop = null;
            _castMembers.Clear();
            foreach (var image in _castPhotos.Values) { try { image.Dispose(); } catch { } }
            _castPhotos.Clear();
            _castCloseRect = _castLeftRect = _castRightRect = Rectangle.Empty;
            if (IsHandleCreated && !IsDisposed) { ShowOnce(2000); Invalidate(); }
        }

        public void MoveCast(int delta)
        {
            if (!_castOpen || delta == 0) return;
            int maximum = Math.Max(0, _castMembers.Count - Math.Max(1, _castVisibleCount));
            int next = Math.Clamp(_castFirst + Math.Sign(delta) * Math.Max(1, _castVisibleCount - 1), 0, maximum);
            if (next == _castFirst) return;
            _castFirst = next;
            Invalidate();
        }

        private void CastMouseDown(Point p)
        {
            if (_castCloseRect.Contains(p)) { CloseCast(); return; }
            if (_castLeftRect.Contains(p)) { MoveCast(-1); return; }
            if (_castRightRect.Contains(p)) MoveCast(+1);
        }

        private void LoadCastPhotos(CancellationToken stop)
        {
            for (int i = 0; i < _castMembers.Count; i++)
            {
                int index = i;
                string? source = _castMembers[i].ProfilePath;
                if (string.IsNullOrWhiteSpace(source)) continue;
                Task.Run(() =>
                {
                    try
                    {
                        string? local = File.Exists(source) ? source : MovieMetadataService.CacheCastProfileImage(source, stop);
                        if (local == null || !File.Exists(local) || stop.IsCancellationRequested) return;
                        using var original = Image.FromFile(local);
                        // Gia' ritagliata nelle proporzioni del ritratto, con il volto in alto.
                        var small = new Bitmap(300, 360, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            double scale = Math.Max(small.Width / (double)original.Width, small.Height / (double)original.Height);
                            int w = (int)Math.Ceiling(original.Width * scale), h = (int)Math.Ceiling(original.Height * scale);
                            g.DrawImage(original, new Rectangle((small.Width - w) / 2, (int)((small.Height - h) * .18), w, h));
                        }
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed || stop.IsCancellationRequested) { small.Dispose(); return; }
                            _castPhotos[index] = small;
                            Invalidate(CastSheetBounds);
                        }));
                    }
                    catch { }
                });
            }
        }

        private void DrawCastSheet(Graphics g)
        {
            Rectangle bounds = CastSheetBounds;
            Rectangle fadeArea = new(0, bounds.Top, Width, CastD(52));
            Rectangle sheet = new(0, fadeArea.Bottom, Width, Height - fadeArea.Bottom);
            Color ground = Color.FromArgb(3, 9, 16);
            using (var fade = new LinearGradientBrush(new Rectangle(fadeArea.X, fadeArea.Y - 1, fadeArea.Width, fadeArea.Height + 2), Color.FromArgb(0, ground), ground, LinearGradientMode.Vertical))
                g.FillRectangle(fade, fadeArea);
            using (var fill = new SolidBrush(ground))
                g.FillRectangle(fill, sheet);

            int left = CastD(54);
            int top = sheet.Top + CastD(8);
            using var titleFont = CastFont("Segoe UI Semibold", 13f);
            using var metaFont = CastFont("Segoe UI", 8.5f);
            const TextFormatFlags line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, _castTitle.Length > 0 ? "Cast  ·  " + _castTitle : "Cast", titleFont,
                new Rectangle(left, top, Width - left * 2, CastD(32)), Color.White, ground, line);
            if (_castMeta.Length > 0)
                TextRenderer.DrawText(g, _castMeta, metaFont, new Rectangle(left, top + CastD(29), Width - left * 2, CastD(22)), Color.FromArgb(205, 217, 226), ground, line);

            // Maniglia di chiusura al centro del bordo superiore: freccia verso il basso, come in Spotlight.
            _castCloseRect = new Rectangle((Width - CastD(64)) / 2, fadeArea.Top + CastD(10), CastD(64), CastD(40));
            using (var pen = new Pen(Color.FromArgb(230, 248, 251, 253), Math.Max(1.35f, 1.48f * HudLayoutScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                int cx = _castCloseRect.Left + _castCloseRect.Width / 2, cy = _castCloseRect.Top + _castCloseRect.Height / 2, dx = Math.Max(8, CastD(9)), dy = Math.Max(4, CastD(5));
                g.DrawLines(pen, new[] { new Point(cx - dx, cy - dy), new Point(cx, cy + dy), new Point(cx + dx, cy - dy) });
            }

            Rectangle content = new(CastD(12), top + CastD(62), Width - CastD(24), Math.Max(1, Height - (top + CastD(62)) - CastD(18)));
            int arrowW = CastD(42);
            int viewportLeft = content.Left + arrowW + CastD(14), viewportRight = content.Right - arrowW - CastD(14);
            int viewportWidth = Math.Max(1, viewportRight - viewportLeft);
            int gap = CastD(15), preferredWidth = CastD(142);
            int visible = Math.Clamp((viewportWidth + gap) / Math.Max(60, preferredWidth + gap), 2, 12);
            visible = Math.Min(visible, _castMembers.Count);
            _castVisibleCount = visible;
            int maxFirst = Math.Max(0, _castMembers.Count - visible);
            _castFirst = Math.Clamp(_castFirst, 0, maxFirst);
            int cardWidth = Math.Min(preferredWidth, (viewportWidth - gap * Math.Max(0, visible - 1)) / Math.Max(1, visible));
            int totalWidth = cardWidth * visible + gap * Math.Max(0, visible - 1);
            int cardX = viewportLeft + Math.Max(0, (viewportWidth - totalWidth) / 2);
            int portraitTop = content.Top + CastD(4);
            int portraitHeight = Math.Max(CastD(60), Math.Min(content.Height - CastD(70), (int)Math.Round(cardWidth * 1.20)));

            using var nameFont = CastFont("Segoe UI Semibold", 9.2f);
            using var roleFont = CastFont("Segoe UI", 8f);
            using var initialsFont = CastFont("Segoe UI Semibold", 20f);
            var oldInterpolation = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int slot = 0; slot < visible; slot++)
            {
                int index = _castFirst + slot;
                var person = _castMembers[index];
                Rectangle portrait = new(cardX + slot * (cardWidth + gap), portraitTop, cardWidth, portraitHeight);
                using (var shape = CastRounded(portrait, Math.Max(4, CastD(5))))
                {
                    var state = g.Save();
                    g.SetClip(shape, CombineMode.Intersect);
                    if (_castPhotos.TryGetValue(index, out Image? photo))
                    {
                        // Ritaglio che riempie il riquadro senza deformare.
                        double scale = Math.Max(portrait.Width / (double)photo.Width, portrait.Height / (double)photo.Height);
                        int sw = (int)Math.Round(portrait.Width / scale), sh = (int)Math.Round(portrait.Height / scale);
                        g.DrawImage(photo, portrait, new Rectangle((photo.Width - sw) / 2, (int)((photo.Height - sh) * .18), sw, sh), GraphicsUnit.Pixel);
                    }
                    else
                    {
                        using var fallback = new LinearGradientBrush(portrait, Color.FromArgb(33, 47, 59), Color.FromArgb(7, 14, 21), LinearGradientMode.Vertical);
                        g.FillRectangle(fallback, portrait);
                        string initials = string.Concat(person.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => char.ToUpperInvariant(part[0])));
                        using var initialsBrush = new SolidBrush(Color.FromArgb(172, 202, 216, 227));
                        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(initials, initialsFont, initialsBrush, portrait, centered);
                    }
                    g.Restore(state);
                    using var frame = new Pen(Color.FromArgb(42, 221, 229, 235), Math.Max(1f, HudLayoutScale));
                    g.DrawPath(frame, shape);
                }
                TextRenderer.DrawText(g, person.Name, nameFont, new Rectangle(portrait.Left, portrait.Bottom + CastD(8), portrait.Width, CastD(23)), Color.White, ground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(person.Character) ? L("Interprete", "Cast") : person.Character, roleFont,
                    new Rectangle(portrait.Left + CastD(3), portrait.Bottom + CastD(31), portrait.Width - CastD(6), CastD(32)), Color.FromArgb(194, 209, 220), ground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            g.InterpolationMode = oldInterpolation;

            _castLeftRect = new Rectangle(content.Left, portraitTop + Math.Max(0, (portraitHeight - arrowW) / 2), arrowW, arrowW);
            _castRightRect = new Rectangle(content.Right - arrowW, _castLeftRect.Top, arrowW, arrowW);
            DrawCastChevron(g, _castLeftRect, false, _castFirst > 0);
            DrawCastChevron(g, _castRightRect, true, _castFirst < maxFirst);
        }

        private void DrawCastChevron(Graphics g, Rectangle rect, bool right, bool enabled)
        {
            using var pen = new Pen(enabled ? Color.FromArgb(214, 248, 251, 253) : Color.FromArgb(42, 186, 202, 214), Math.Max(1.35f, 1.48f * HudLayoutScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int cx = rect.Left + rect.Width / 2, cy = rect.Top + rect.Height / 2, dx = Math.Max(4, CastD(5)), dy = Math.Max(8, CastD(9));
            int sign = right ? 1 : -1;
            g.DrawLines(pen, new[] { new Point(cx - sign * dx, cy - dy), new Point(cx + sign * dx, cy), new Point(cx - sign * dx, cy + dy) });
        }

        private static GraphicsPath CastRounded(Rectangle r, int radius)
        {
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
