using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    /// <summary>
    /// Overlay minimale per modalità Foto: frecce PREV/NEXT con auto-hide e stile moderno.
    /// - Le frecce ricompaiono quando si muove il mouse (PlayerForm richiama Wake()).
    /// - Click-through fuori dai bottoni, per non bloccare eventuali interazioni sulla foto.
    /// </summary>
    public sealed class PhotoHudOverlay : Control
    {
        public event Action? PrevRequested;
        public event Action? NextRequested;
        public event Action? ViewModeRequested;
        public event Action? RotateRequested;
        public event Action<int>? ZoomRequested;
        public event Action? BackRequested;
        private Rectangle _rcZoomOut, _rcZoomIn, _rcBack;

        private readonly System.Windows.Forms.Timer _timer;
        private float _opacity = 0f;
        private DateTime _showUntilUtc = DateTime.MinValue;

        private Rectangle _rcPrev, _rcNext, _rcViewMode, _rcRotate, _rcInfo;
        private bool _hoverPrev, _hoverNext, _hoverViewMode, _hoverRotate;
        private string _title = string.Empty;
        private string _counter = string.Empty;
        private string _viewMode = "Adatta";
        private string _rotateText = "Ruota";

        // tuning (piu' rapido: in TV/remote non deve restare su troppo a lungo)
        private const int DefaultLingerMs = 2200;
        private const int FadeOutMs = 220;        // durata fade out
        private const int TickMs = 50;            // 20fps: evita repaint inutili del viewport fotografico

        public PhotoHudOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            // verrà sovrascritto dal parent (TransparencyKey del form overlay)
            BackColor = Color.Magenta;
            TabStop = false;

            _timer = new System.Windows.Forms.Timer { Interval = TickMs };
            _timer.Tick += (_, __) => OnTick();

            RecalcRects();
        }

        public void Wake(int lingerMs = DefaultLingerMs)
        {
            try
            {
                _showUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(200, lingerMs));
                bool changed = _opacity < 1f;
                _opacity = 1f;
                if (!_timer.Enabled) _timer.Start();
                if (changed) InvalidateVisuals();
            }
            catch { }
        }

        public void SetPhotoInfo(string? title, int index, int total, string? viewMode, string? rotateText)
        {
            _title = title ?? string.Empty;
            _counter = total > 0 ? $"{Math.Max(1, index + 1)} / {total}" : string.Empty;
            _viewMode = string.IsNullOrWhiteSpace(viewMode) ? "Adatta" : viewMode!;
            _rotateText = string.IsNullOrWhiteSpace(rotateText) ? "Ruota" : rotateText!;
            InvalidateVisuals();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (Visible)
            {
                // quando entri in photo mode: mostrale subito
                Wake();
            }
            else
            {
                try { _timer.Stop(); } catch { }
                _opacity = 0f;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecalcRects();
            Invalidate();
        }

        private void RecalcRects()
        {
            // Cerchi moderni (niente riquadro)
            int size = 52;
            int pad = 26;

            int cy = (Height - size) / 2;
            _rcPrev = new Rectangle(pad, cy, size, size);
            _rcNext = new Rectangle(Math.Max(pad, Width - pad - size), cy, size, size);

            int toolW = Math.Min(124, Math.Max(64, (Width - 100) / 4));
            int toolH = 42;
            int gap = 8;
            int x = Math.Max(12, (Width - (toolW * 4 + gap * 3)) / 2);
            int y = Math.Max(12, Height - toolH - 24);
            _rcViewMode = new Rectangle(x, y, toolW, toolH);
            _rcRotate = new Rectangle(x + toolW + gap, y, toolW, toolH);
            _rcZoomOut = new Rectangle(x + (toolW + gap) * 2, y, toolW, toolH);
            _rcZoomIn = new Rectangle(x + (toolW + gap) * 3, y, toolW, toolH);
            _rcBack = new Rectangle(20, 22, 108, 42);

            int infoW = Math.Max(1, Math.Min(620, Width - 280));
            _rcInfo = new Rectangle((Width - infoW) / 2, 22, infoW, 50);
        }

        private void InvalidateVisuals()
        {
            Invalidate();
            static Rectangle Dirty(Rectangle r) => Rectangle.Inflate(r, 8, 8);
            if (!_rcPrev.IsEmpty) Invalidate(Dirty(_rcPrev));
            if (!_rcNext.IsEmpty) Invalidate(Dirty(_rcNext));
            if (!_rcInfo.IsEmpty) Invalidate(Dirty(_rcInfo));
            if (!_rcViewMode.IsEmpty) Invalidate(Dirty(_rcViewMode));
            if (!_rcRotate.IsEmpty) Invalidate(Dirty(_rcRotate));
            Invalidate(Dirty(_rcZoomOut)); Invalidate(Dirty(_rcZoomIn)); Invalidate(Dirty(_rcBack));
        }

        private void OnTick()
        {
            try
            {
                var now = DateTime.UtcNow;

                float previous = _opacity;
                bool keep = now < _showUntilUtc;

                if (keep)
                {
                    if (_opacity < 1f) _opacity = 1f;
                }
                else
                {
                    // fade out lineare
                    float step = TickMs / (float)FadeOutMs;
                    _opacity = Math.Max(0f, _opacity - step);
                    if (_opacity <= 0f)
                        _timer.Stop();
                }

                if (previous != _opacity) InvalidateVisuals();
            }
            catch { }
        }

        private bool IsOverButton(Point p) => _rcPrev.Contains(p) || _rcNext.Contains(p) || _rcViewMode.Contains(p) || _rcRotate.Contains(p) || _rcZoomIn.Contains(p) || _rcZoomOut.Contains(p) || _rcBack.Contains(p);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool hp = _rcPrev.Contains(e.Location);
            bool hn = _rcNext.Contains(e.Location);
            bool hv = _rcViewMode.Contains(e.Location);
            bool hr = _rcRotate.Contains(e.Location);

            if (hp != _hoverPrev || hn != _hoverNext || hv != _hoverViewMode || hr != _hoverRotate)
            {
                _hoverPrev = hp;
                _hoverNext = hn;
                _hoverViewMode = hv;
                _hoverRotate = hr;
                InvalidateVisuals();
            }

            Wake();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverPrev || _hoverNext || _hoverViewMode || _hoverRotate)
            {
                _hoverPrev = false;
                _hoverNext = false;
                _hoverViewMode = false;
                _hoverRotate = false;
                InvalidateVisuals();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_rcZoomIn.Contains(e.Location)) { Wake(); ZoomRequested?.Invoke(1); return; }
            if (_rcZoomOut.Contains(e.Location)) { Wake(); ZoomRequested?.Invoke(-1); return; }
            if (_rcBack.Contains(e.Location)) { BackRequested?.Invoke(); return; }

            if (_rcPrev.Contains(e.Location))
            {
                Wake();
                try { PrevRequested?.Invoke(); } catch { }
            }
            else if (_rcNext.Contains(e.Location))
            {
                Wake();
                try { NextRequested?.Invoke(); } catch { }
            }
            else if (_rcViewMode.Contains(e.Location))
            {
                Wake();
                try { ViewModeRequested?.Invoke(); } catch { }
            }
            else if (_rcRotate.Contains(e.Location))
            {
                Wake();
                try { RotateRequested?.Invoke(); } catch { }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            if (BackColor == Color.Transparent)
            {
                base.OnPaintBackground(pevent);
                return;
            }
            if (string.Equals(Parent?.GetType().Name, "InlineOverlayPanel", StringComparison.Ordinal))
                return;

            using (var b = new SolidBrush(BackColor))
                pevent.Graphics.FillRectangle(b, ClientRectangle);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_opacity <= 0.01f) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int fadeHeight = Math.Min(Height / 3, 150);
            if (fadeHeight > 0)
            {
                var top = new Rectangle(0, 0, Width, fadeHeight);
                var bottom = new Rectangle(0, Height - fadeHeight, Width, fadeHeight);
                using var upper = new LinearGradientBrush(top, Color.FromArgb((int)(185 * _opacity), Color.Black), Color.Transparent, LinearGradientMode.Vertical);
                using var lower = new LinearGradientBrush(bottom, Color.Transparent, Color.FromArgb((int)(210 * _opacity), Color.Black), LinearGradientMode.Vertical);
                upper.WrapMode = lower.WrapMode = WrapMode.TileFlipXY;
                g.FillRectangle(upper, top); g.FillRectangle(lower, bottom);
            }
            DrawButton(g, _rcPrev, left: true, hover: _hoverPrev);
            DrawButton(g, _rcNext, left: false, hover: _hoverNext);
            DrawPhotoInfo(g);
            DrawToolButton(g, _rcViewMode, _viewMode, "fit", _hoverViewMode);
            DrawToolButton(g, _rcRotate, _rotateText, "rotate", _hoverRotate);
            DrawToolButton(g, _rcZoomOut, "−", "zoom", false);
            DrawToolButton(g, _rcZoomIn, "+", "zoom", false);
            DrawToolButton(g, _rcBack, "←", "back", false);
        }

        private void DrawButton(Graphics g, Rectangle rc, bool left, bool hover)
        {
            using (var pen = new Pen(Color.FromArgb((int)((hover ? 255 : 220) * _opacity), 255, 255, 255), 3.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                float cx = rc.X + rc.Width / 2f + (left ? -1.5f : 1.5f);
                float cy = rc.Y + rc.Height / 2f;
                float dx = rc.Width * 0.15f;
                float dy = rc.Height * 0.19f;

                if (left)
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx + dx, cy - dy),
                        new PointF(cx - dx, cy),
                        new PointF(cx + dx, cy + dy)
                    });
                }
                else
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - dx, cy - dy),
                        new PointF(cx + dx, cy),
                        new PointF(cx - dx, cy + dy)
                    });
                }
            }
        }

        private void DrawPhotoInfo(Graphics g)
        {
            if (string.IsNullOrWhiteSpace(_title) && string.IsNullOrWhiteSpace(_counter))
                return;

            Rectangle r = _rcInfo;

            using var title = new Font("Segoe UI Semibold", 10f);
            using var meta = new Font("Segoe UI", 8.4f);
            DrawFadingText(g, _title, title, new Rectangle(r.Left + 14, r.Top + 5, r.Width - 28, 22),
                Color.FromArgb((int)(245 * _opacity), Color.White), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            DrawFadingText(g, _counter, meta, new Rectangle(r.Left + 14, r.Top + 27, r.Width - 28, 17),
                Color.FromArgb((int)(178 * _opacity), 190, 208, 222), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawToolButton(Graphics g, Rectangle r, string text, string glyph, bool hover)
        {
            Rectangle icon = new(r.Left + 12, r.Top + 11, 20, 20);
            using var pen = new Pen(Color.FromArgb((int)(230 * _opacity), Color.White), 1.8f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (glyph == "rotate")
            {
                g.DrawArc(pen, icon, 35, 285);
                g.DrawLines(pen, new[] { new Point(icon.Right - 1, icon.Top + 2), new Point(icon.Right - 1, icon.Top + 9), new Point(icon.Right - 8, icon.Top + 7) });
            }
            else if (glyph == "fit")
            {
                int m = 2, l = 6;
                g.DrawLine(pen, icon.Left + m, icon.Top + l, icon.Left + m, icon.Top + m);
                g.DrawLine(pen, icon.Left + m, icon.Top + m, icon.Left + l, icon.Top + m);
                g.DrawLine(pen, icon.Right - m, icon.Top + l, icon.Right - m, icon.Top + m);
                g.DrawLine(pen, icon.Right - m, icon.Top + m, icon.Right - l, icon.Top + m);
                g.DrawLine(pen, icon.Left + m, icon.Bottom - l, icon.Left + m, icon.Bottom - m);
                g.DrawLine(pen, icon.Left + m, icon.Bottom - m, icon.Left + l, icon.Bottom - m);
                g.DrawLine(pen, icon.Right - m, icon.Bottom - l, icon.Right - m, icon.Bottom - m);
                g.DrawLine(pen, icon.Right - m, icon.Bottom - m, icon.Right - l, icon.Bottom - m);
            }

            using var font = new Font("Segoe UI Semibold", glyph is "zoom" or "back" ? 20f : 10f);
            DrawFadingText(g, text, font, glyph is "zoom" or "back" ? r : new Rectangle(icon.Right + 8, r.Top, r.Right - icon.Right - 14, r.Height),
                Color.FromArgb((int)(238 * _opacity), Color.White), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        private static void DrawFadingText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
        {
            using var brush = new SolidBrush(color);
            using var format = new StringFormat { Alignment = flags.HasFlag(TextFormatFlags.HorizontalCenter) ? StringAlignment.Center : StringAlignment.Near,
                LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(text, font, brush, bounds, format);
        }
        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Stop(); _timer.Dispose(); }
            base.Dispose(disposing);
        }

        // Click-through fuori dai bottoni
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTTRANSPARENT = -1;

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                try
                {
                    if (_opacity <= 0.02f)
                    {
                        m.Result = (IntPtr)HTTRANSPARENT;
                        return;
                    }

                    int x = (short)((int)m.LParam & 0xFFFF);
                    int y = (short)(((int)m.LParam >> 16) & 0xFFFF);
                    var pt = PointToClient(new Point(x, y));
                    if (!_rcPrev.Contains(pt) && !_rcNext.Contains(pt) && !_rcViewMode.Contains(pt) && !_rcRotate.Contains(pt)
                        && !_rcZoomOut.Contains(pt) && !_rcZoomIn.Contains(pt) && !_rcBack.Contains(pt))
                        m.Result = (IntPtr)HTTRANSPARENT;
                }
                catch { }
                return;
            }

            base.WndProc(ref m);
        }
    }
}
