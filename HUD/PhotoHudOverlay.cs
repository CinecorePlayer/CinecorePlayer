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
        public event Action PrevRequested;
        public event Action NextRequested;

        private readonly Timer _timer;
        private float _opacity = 0f;
        private DateTime _showUntilUtc = DateTime.MinValue;

        private Rectangle _rcPrev, _rcNext;
        private bool _hoverPrev, _hoverNext;

        // tuning (piu' rapido: in TV/remote non deve restare su troppo a lungo)
        private const int DefaultLingerMs = 1100; // quanto resta visibile dopo Wake()
        private const int FadeOutMs = 220;        // durata fade out
        private const int TickMs = 33;            // ~30fps

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

            _timer = new Timer { Interval = TickMs };
            _timer.Tick += (_, __) => OnTick();

            RecalcRects();
        }

        public void Wake(int lingerMs = DefaultLingerMs)
        {
            try
            {
                _showUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(200, lingerMs));
                if (_opacity < 1f) _opacity = 1f;
                if (!_timer.Enabled) _timer.Start();
                Invalidate();
            }
            catch { }
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
            int size = 56;
            int pad = 22;

            int cy = (Height - size) / 2;
            _rcPrev = new Rectangle(pad, cy, size, size);
            _rcNext = new Rectangle(Math.Max(pad, Width - pad - size), cy, size, size);
        }

        private void OnTick()
        {
            try
            {
                var now = DateTime.UtcNow;

                bool keep = now < _showUntilUtc || _hoverPrev || _hoverNext;

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

                Invalidate();
            }
            catch { }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool hp = _rcPrev.Contains(e.Location);
            bool hn = _rcNext.Contains(e.Location);

            if (hp != _hoverPrev || hn != _hoverNext)
            {
                _hoverPrev = hp;
                _hoverNext = hn;
                Invalidate();
            }

            if (hp || hn)
                Wake();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverPrev || _hoverNext)
            {
                _hoverPrev = false;
                _hoverNext = false;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

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
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            // Trasparente (BackColor dovrebbe coincidere con TransparencyKey del form overlay).
            using (var b = new SolidBrush(BackColor))
                pevent.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_opacity <= 0.01f) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawButton(g, _rcPrev, left: true, hover: _hoverPrev);
            DrawButton(g, _rcNext, left: false, hover: _hoverNext);
        }

        private void DrawButton(Graphics g, Rectangle rc, bool left, bool hover)
        {
            // Shadow (circolare)
            var shadowRc = new Rectangle(rc.X + 2, rc.Y + 3, rc.Width, rc.Height);
            using (var sb = new SolidBrush(Color.FromArgb((int)(70 * _opacity), 0, 0, 0)))
                g.FillEllipse(sb, shadowRc);

            // Base (cerchio, senza bordo "riquadrato")
            int baseA = hover ? 200 : 160;
            using (var bg = new SolidBrush(Color.FromArgb((int)(baseA * _opacity), 18, 18, 18)))
                g.FillEllipse(bg, rc);

            // chevron
            using (var pen = new Pen(Color.FromArgb((int)(230 * _opacity), 255, 255, 255), 3.2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                int cx = rc.X + rc.Width / 2;
                int cy = rc.Y + rc.Height / 2;
                int dx = 8;
                int dy = 10;

                if (left)
                {
                    g.DrawLine(pen, cx + dx, cy - dy, cx - dx, cy);
                    g.DrawLine(pen, cx - dx, cy, cx + dx, cy + dy);
                }
                else
                {
                    g.DrawLine(pen, cx - dx, cy - dy, cx + dx, cy);
                    g.DrawLine(pen, cx + dx, cy, cx - dx, cy + dy);
                }
            }
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
                    if (!_rcPrev.Contains(pt) && !_rcNext.Contains(pt))
                        m.Result = (IntPtr)HTTRANSPARENT;
                }
                catch { }
                return;
            }

            base.WndProc(ref m);
        }
    }
}
