using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    /// <summary>
    /// Photo viewer HUD. The controls sit on an edge vignette that is part of the HUD
    /// itself, so photo and chrome separate cleanly: when the HUD fades out, the vignette
    /// fades with it and the photo is shown untouched.
    /// - PlayerForm calls Wake() on mouse/keyboard activity; the HUD fades out after a delay.
    /// - Click-through outside the controls, so drag, wheel and double click reach the photo.
    /// </summary>
    public sealed class PhotoHudOverlay : Control
    {
        public event Action? PrevRequested;
        public event Action? NextRequested;
        public event Action? ViewModeRequested;
        public event Action? RotateRequested;
        public event Action<int>? ZoomRequested;
        public event Action? BackRequested;
        public event Action? SlideshowRequested;
        public event Action? EditRequested;
        public event Action? DeleteRequested;
        /// <summary>Azione diretta sulla foto: "share", "openwith", "print", "copy", "folder".</summary>
        public event Action<string>? ActionRequested;
        private static readonly string[] ActionKeys = { "share", "openwith", "print", "copy", "folder" };
        private readonly Rectangle[] _rcActions = new Rectangle[5];
        private int _hoverAction = -1, _shownAction = -1;

        private enum Part { None, Prev, Next, Back, ViewMode, Rotate, ZoomOut, ZoomIn, Slideshow, Edit, Delete, More }

        private readonly System.Windows.Forms.Timer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private float _opacity;
        private float _fadeFrom;
        private float _fadeTo;
        private double _fadeStartMs;
        private double _fadeDurationMs;
        private DateTime _showUntilUtc = DateTime.MinValue;

        private Rectangle _rcPrev, _rcNext, _rcBack, _rcInfo, _rcBar;
        private Rectangle _rcViewMode, _rcRotate, _rcZoomOut, _rcZoomText, _rcZoomIn, _rcSlideshow, _rcEdit, _rcDelete, _rcMore;
        private Part _hover = Part.None;

        private string _title = string.Empty;
        private string _detail = string.Empty;
        private string _viewMode = "Adatta";
        private string _zoomText = "100%";
        private bool _slideshow;
        private bool _multiple = true;

        private const int DefaultLingerMs = 2600;
        private const double FadeInMs = 140;
        private const double FadeOutMs = 380;
        private const int AnimationTickMs = 15;
        private const int IdleTickMs = 120;

        public PhotoHudOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            // Clicking a control must not steal focus from the photo surface (wheel/keys).
            SetStyle(ControlStyles.Selectable, false);

            // verrà sovrascritto dal parent (TransparencyKey del form overlay)
            BackColor = Color.Magenta;
            TabStop = false;

            _timer = new System.Windows.Forms.Timer { Interval = AnimationTickMs };
            _timer.Tick += (_, __) => OnTick();

            RecalcRects();
        }

        private float S => DeviceDpi / 96f;

        public void Wake(int lingerMs = DefaultLingerMs)
        {
            try
            {
                _showUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(200, lingerMs));
                if (_fadeTo < 1f) StartFade(1f, FadeInMs);
                else if (_opacity >= 1f) EnsureTimer(IdleTickMs);
            }
            catch { }
        }

        /// <summary>Hides the HUD immediately (e.g. when a slideshow advances).</summary>
        public void Sleep()
        {
            _showUntilUtc = DateTime.MinValue;
            if (_opacity > 0f) StartFade(0f, FadeOutMs);
        }

        public void SetPhotoInfo(string? title, string? detail, int index, int total, string? viewMode, string? zoomText)
        {
            _title = title ?? string.Empty;
            string counter = total > 0 ? $"{Math.Max(1, index + 1)} / {total}" : string.Empty;
            _detail = string.IsNullOrWhiteSpace(detail) ? counter
                : string.IsNullOrEmpty(counter) ? detail! : counter + "   ·   " + detail;
            _viewMode = string.IsNullOrWhiteSpace(viewMode) ? "Adatta" : viewMode!;
            _zoomText = string.IsNullOrWhiteSpace(zoomText) ? "100%" : zoomText!;
            _multiple = total > 1;
            if (_opacity > 0f) Invalidate();
        }

        public void SetSlideshow(bool running)
        {
            if (_slideshow == running) return;
            _slideshow = running;
            if (_opacity > 0f) Invalidate(Rectangle.Inflate(_rcSlideshow, 4, 4));
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
                _opacity = _fadeTo = 0f;
                _hover = Part.None;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecalcRects();
            Invalidate();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            RecalcRects();
            Invalidate();
        }

        private void RecalcRects()
        {
            float s = S;
            int Px(float v) => (int)Math.Round(v * s);

            int margin = Px(24);
            int navW = Px(72), navH = Px(132);
            int cy = (Height - navH) / 2;
            _rcPrev = new Rectangle(Px(8), cy, navW, navH);
            _rcNext = new Rectangle(Math.Max(Px(8), Width - Px(8) - navW), cy, navW, navH);

            int back = Px(44);
            _rcBack = new Rectangle(margin - Px(8), margin - Px(6), back, back);

            int infoW = Math.Max(1, Math.Min(Px(680), Width - 2 * (margin + back + Px(24))));
            _rcInfo = new Rectangle((Width - infoW) / 2, margin - Px(2), infoW, Px(48));

            // Bottom tool bar: [mode] | [rotate] | [-] 100% [+] | [slideshow] | [edit] [delete] [more]
            int h = Px(46), icon = Px(44), gap = Px(10);
            int modeW = Px(120), zoomTextW = Px(58);
            int barW = Px(8) + modeW + gap + icon + gap + icon + zoomTextW + icon + gap + icon + gap + icon * 2 + gap + icon * 5 + Px(8);
            int x = (Width - barW) / 2;
            int y = Height - margin - h;
            _rcBar = new Rectangle(x, y, barW, h);
            int cx = x + Px(8);
            Rectangle Next(int w) { var r = new Rectangle(cx, y, w, h); cx += w; return r; }
            _rcViewMode = Next(modeW); cx += gap;
            _rcRotate = Next(icon); cx += gap;
            _rcZoomOut = Next(icon);
            _rcZoomText = Next(zoomTextW);
            _rcZoomIn = Next(icon); cx += gap;
            _rcSlideshow = Next(icon); cx += gap;
            _rcEdit = Next(icon);
            _rcDelete = Next(icon); cx += gap;
            _rcMore = Rectangle.Empty;
            for (int i = 0; i < _rcActions.Length; i++) _rcActions[i] = Next(icon);
        }

        private void StartFade(float target, double durationMs)
        {
            _fadeFrom = _opacity;
            _fadeTo = target;
            _fadeStartMs = _clock.Elapsed.TotalMilliseconds;
            _fadeDurationMs = Math.Max(1, durationMs * Math.Abs(target - _opacity));
            EnsureTimer(AnimationTickMs);
        }

        private void EnsureTimer(int interval)
        {
            if (_timer.Interval != interval) _timer.Interval = interval;
            if (!_timer.Enabled) _timer.Start();
        }

        private void OnTick()
        {
            try
            {
                if (_fadeTo > 0f && DateTime.UtcNow >= _showUntilUtc && _hover == Part.None)
                    StartFade(0f, FadeOutMs);

                if (Math.Abs(_opacity - _fadeTo) > 0.001f)
                {
                    double t = Math.Clamp((_clock.Elapsed.TotalMilliseconds - _fadeStartMs) / _fadeDurationMs, 0, 1);
                    double eased = 1 - Math.Pow(1 - t, 3);
                    _opacity = (float)(_fadeFrom + (_fadeTo - _fadeFrom) * eased);
                    if (t >= 1) _opacity = _fadeTo;
                    Invalidate();
                    return;
                }

                if (_opacity <= 0f) _timer.Stop();
                else EnsureTimer(IdleTickMs); // fully shown: only watch the linger deadline
            }
            catch { }
        }

        private Part HitTest(Point p)
        {
            if (_opacity <= 0.05f) return Part.None;
            if (_multiple && _rcPrev.Contains(p)) return Part.Prev;
            if (_multiple && _rcNext.Contains(p)) return Part.Next;
            if (_rcBack.Contains(p)) return Part.Back;
            if (_rcViewMode.Contains(p)) return Part.ViewMode;
            if (_rcRotate.Contains(p)) return Part.Rotate;
            if (_rcZoomOut.Contains(p)) return Part.ZoomOut;
            if (_rcZoomIn.Contains(p)) return Part.ZoomIn;
            if (_multiple && _rcSlideshow.Contains(p)) return Part.Slideshow;
            if (_rcEdit.Contains(p)) return Part.Edit;
            if (_rcDelete.Contains(p)) return Part.Delete;
            _hoverAction = Array.FindIndex(_rcActions, r => r.Contains(p));
            if (_hoverAction >= 0) return Part.More;
            return Part.None;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHover(HitTest(e.Location));
            Wake();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(Part.None);
        }

        private void SetHover(Part part)
        {
            int action = part == Part.More ? _hoverAction : -1;
            if (_hover == part && action == _shownAction) return;
            _hover = part;
            _shownAction = action;
            Cursor = part == Part.None ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Part part = HitTest(e.Location);
            if (part == Part.None) return;
            Wake();
            try
            {
                switch (part)
                {
                    case Part.Prev: PrevRequested?.Invoke(); break;
                    case Part.Next: NextRequested?.Invoke(); break;
                    case Part.Back: BackRequested?.Invoke(); break;
                    case Part.ViewMode: ViewModeRequested?.Invoke(); break;
                    case Part.Rotate: RotateRequested?.Invoke(); break;
                    case Part.ZoomOut: ZoomRequested?.Invoke(-1); break;
                    case Part.ZoomIn: ZoomRequested?.Invoke(1); break;
                    case Part.Slideshow: SlideshowRequested?.Invoke(); break;
                    case Part.Edit: EditRequested?.Invoke(); break;
                    case Part.Delete: DeleteRequested?.Invoke(); break;
                    case Part.More: if (_hoverAction >= 0) ActionRequested?.Invoke(ActionKeys[_hoverAction]); break;
                }
            }
            catch { }
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

            if (_opacity <= 0.005f) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float s = S;

            DrawVignette(g, s);

            DrawIconCentered(g, _rcBack, "arrow-left", (int)(26 * s), _hover == Part.Back, shadow: true);
            if (_multiple)
            {
                // Bare chevrons on the vignette: no discs. Hover brightens and nudges the
                // arrow towards its direction.
                int nudge = (int)(3 * s);
                var prev = _rcPrev; if (_hover == Part.Prev) prev.Offset(-nudge, 0);
                var next = _rcNext; if (_hover == Part.Next) next.Offset(nudge, 0);
                DrawIconCentered(g, prev, "chevron-left", (int)(46 * s), _hover == Part.Prev, shadow: true);
                DrawIconCentered(g, next, "chevron-right", (int)(46 * s), _hover == Part.Next, shadow: true);
            }
            DrawPhotoInfo(g, s);
            DrawToolbar(g, s);
        }

        private int A(int alpha) => Math.Clamp((int)Math.Round(alpha * _opacity), 0, 255);

        private void DrawVignette(Graphics g, float s)
        {
            int top = Math.Min(Height / 3, (int)(170 * s));
            int bottom = Math.Min(Height / 3, (int)(200 * s));
            int side = Math.Min(Width / 4, (int)(190 * s));
            FillEdge(g, new Rectangle(0, 0, Width, top), 90f, A(185));
            FillEdge(g, new Rectangle(0, Height - bottom, Width, bottom), 270f, A(215));
            if (_multiple)
            {
                FillEdge(g, new Rectangle(0, 0, side, Height), 0f, A(120));
                FillEdge(g, new Rectangle(Width - side, 0, side, Height), 180f, A(120));
            }
        }

        /// <summary>Dark at the edge the angle points away from, eased to clear.</summary>
        private static void FillEdge(Graphics g, Rectangle r, float angle, int alpha)
        {
            if (r.Width <= 0 || r.Height <= 0 || alpha <= 0) return;
            // Brush rectangle one pixel larger avoids GDI+ wrapping a line of the far color.
            var brushRect = Rectangle.Inflate(r, 1, 1);
            using var brush = new LinearGradientBrush(brushRect, Color.Black, Color.Transparent, angle);
            brush.InterpolationColors = new ColorBlend
            {
                Positions = new[] { 0f, 0.3f, 0.6f, 1f },
                Colors = new[]
                {
                    Color.FromArgb(alpha, 0, 0, 0),
                    Color.FromArgb(alpha * 58 / 100, 0, 0, 0),
                    Color.FromArgb(alpha * 18 / 100, 0, 0, 0),
                    Color.FromArgb(0, 0, 0, 0)
                }
            };
            g.FillRectangle(brush, r);
        }

        private readonly System.Collections.Generic.Dictionary<(string, int, bool), Bitmap> _icons = new();

        /// <summary>Lucide icon (Assets/Icons.Lucide), rendered once per size and faded with the HUD.</summary>
        private Bitmap? Icon(string name, int size, bool shadow)
        {
            if (_icons.TryGetValue((name, size, shadow), out var cached)) return cached;
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Icons.Lucide", name + ".svg");
            // Modifica, cestino e "altro" stanno nell'altra serie di icone.
            if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Icons.Uniform", name + ".svg");
            if (!System.IO.File.Exists(path)) return null;
            try
            {
                var bitmap = CinecorePlayer2025.Utilities.AssetIconService.RenderSvg(path, size, shadow ? Color.Black : Color.White);
                _icons[(name, size, shadow)] = bitmap;
                return bitmap;
            }
            catch { return null; }
        }

        private void DrawIconCentered(Graphics g, Rectangle host, string name, int size, bool hot, bool shadow = false)
        {
            var icon = Icon(name, size, false);
            if (icon == null) return;
            int x = host.Left + (host.Width - icon.Width) / 2, y = host.Top + (host.Height - icon.Height) / 2;
            if (shadow && Icon(name, size, true) is { } dark)
                DrawFaded(g, dark, x, y + Math.Max(1, size / 24), 0.45f * _opacity);
            DrawFaded(g, icon, x, y, (hot ? 1f : 0.86f) * _opacity);
        }

        private static void DrawFaded(Graphics g, Bitmap bitmap, int x, int y, float alpha)
        {
            if (alpha <= 0.005f) return;
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = Math.Clamp(alpha, 0f, 1f) });
            g.DrawImage(bitmap, new Rectangle(x, y, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
        }

        private void DrawPhotoInfo(Graphics g, float s)
        {
            if (string.IsNullOrWhiteSpace(_title) && string.IsNullOrWhiteSpace(_detail))
                return;

            Rectangle r = _rcInfo;
            using var title = global::CinecorePlayer2025.AppFonts.CreateForDpi("Segoe UI Semibold", 11f, FontStyle.Regular, DeviceDpi);
            using var meta = global::CinecorePlayer2025.AppFonts.CreateForDpi("Segoe UI", 8.8f, FontStyle.Regular, DeviceDpi);
            int titleH = (int)(24 * s);
            DrawText(g, _title, title, new Rectangle(r.Left, r.Top + (int)(2 * s), r.Width, titleH), Color.FromArgb(A(246), 255, 255, 255), center: true);
            DrawText(g, _detail, meta, new Rectangle(r.Left, r.Top + titleH + (int)(2 * s), r.Width, (int)(18 * s)), Color.FromArgb(A(190), 206, 214, 224), center: true);
        }

        private void DrawToolbar(Graphics g, float s)
        {
            using (var path = RoundRect(_rcBar, _rcBar.Height / 2))
            {
                using var fill = new SolidBrush(Color.FromArgb(A(150), 18, 20, 24));
                g.FillPath(fill, path);
                using var border = new Pen(Color.FromArgb(A(46), 255, 255, 255), 1f);
                g.DrawPath(border, path);
            }

            DrawToolHover(g, _rcViewMode, Part.ViewMode, s);
            DrawToolHover(g, _rcRotate, Part.Rotate, s);
            DrawToolHover(g, _rcZoomOut, Part.ZoomOut, s);
            DrawToolHover(g, _rcZoomIn, Part.ZoomIn, s);
            if (_multiple) DrawToolHover(g, _rcSlideshow, Part.Slideshow, s);
            DrawToolHover(g, _rcEdit, Part.Edit, s);
            DrawToolHover(g, _rcDelete, Part.Delete, s);
            if (_hover == Part.More && _shownAction >= 0)
            {
                var inner = Rectangle.Inflate(_rcActions[_shownAction], -(int)(2 * s), -(int)(6 * s));
                using var path = RoundRect(inner, inner.Height / 2);
                using var fill = new SolidBrush(Color.FromArgb(A(52), 255, 255, 255));
                g.FillPath(fill, path);
            }

            using var label = global::CinecorePlayer2025.AppFonts.CreateForDpi("Segoe UI Semibold", 9.5f, FontStyle.Regular, DeviceDpi);
            int iconPx = (int)(20 * s);

            // View mode: icon + label
            var modeIcon = new Rectangle(_rcViewMode.Left + (int)(10 * s), _rcViewMode.Top, iconPx, _rcViewMode.Height);
            DrawIconCentered(g, modeIcon, "scan", iconPx, _hover == Part.ViewMode);
            int labelLeft = modeIcon.Right + (int)(8 * s);
            DrawText(g, _viewMode, label, new Rectangle(labelLeft, _rcViewMode.Top, _rcViewMode.Right - labelLeft - (int)(6 * s), _rcViewMode.Height),
                Color.FromArgb(A(236), 255, 255, 255), center: false);

            Separator(g, _rcViewMode.Right + (int)(5 * s), s);
            DrawIconCentered(g, _rcRotate, "rotate-cw", iconPx, _hover == Part.Rotate);
            Separator(g, _rcRotate.Right + (int)(5 * s), s);

            DrawIconCentered(g, _rcZoomOut, "zoom-out", iconPx, _hover == Part.ZoomOut);
            DrawText(g, _zoomText, label, _rcZoomText, Color.FromArgb(A(226), 255, 255, 255), center: true);
            DrawIconCentered(g, _rcZoomIn, "zoom-in", iconPx, _hover == Part.ZoomIn);

            Separator(g, _rcZoomIn.Right + (int)(5 * s), s);
            if (_multiple) DrawIconCentered(g, _rcSlideshow, _slideshow ? "pause" : "play", iconPx, _hover == Part.Slideshow);
            Separator(g, _rcSlideshow.Right + (int)(5 * s), s);
            DrawIconCentered(g, _rcEdit, "edit", iconPx, _hover == Part.Edit);
            DrawIconCentered(g, _rcDelete, "trash", iconPx, _hover == Part.Delete);
            Separator(g, _rcDelete.Right + (int)(5 * s), s);
            for (int i = 0; i < _rcActions.Length; i++)
            {
                if (ActionKeys[i] == "folder") { DrawIconCentered(g, _rcActions[i], "folder", iconPx, _hover == Part.More && _shownAction == i); continue; }
                DrawActionGlyph(g, ActionKeys[i], new Rectangle(_rcActions[i].Left + (_rcActions[i].Width - iconPx) / 2, _rcActions[i].Top + (_rcActions[i].Height - iconPx) / 2, iconPx, iconPx),
                    Color.FromArgb(A(_hover == Part.More && _shownAction == i ? 255 : 220), 255, 255, 255));
            }
        }



        // Condividi, apri con, stampa, copia: disegnate a tratto come le altre icone della barra.
        private static void DrawActionGlyph(Graphics g, string key, Rectangle r, Color color)
        {
            float u = r.Width / 24f;
            using var pen = new Pen(color, Math.Max(1.3f, 1.6f * u)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            PointF P(float x, float y) => new(r.Left + x * u, r.Top + y * u);
            var state = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            switch (key)
            {
                case "share":
                    g.DrawLine(pen, P(12, 4), P(12, 15)); g.DrawLines(pen, new[] { P(8, 8), P(12, 4), P(16, 8) });
                    g.DrawLines(pen, new[] { P(5, 12.5f), P(5, 20), P(19, 20), P(19, 12.5f) });
                    break;
                case "openwith":
                    g.DrawLines(pen, new[] { P(11, 5), P(5, 5), P(5, 19), P(19, 19), P(19, 13) });
                    g.DrawLine(pen, P(12.5f, 11.5f), P(20, 4)); g.DrawLines(pen, new[] { P(15, 4), P(20, 4), P(20, 9) });
                    break;
                case "print":
                    g.DrawLines(pen, new[] { P(8, 9), P(8, 4), P(16, 4), P(16, 9) });
                    g.DrawLines(pen, new[] { P(8, 16.5f), P(4.5f, 16.5f), P(4.5f, 9), P(19.5f, 9), P(19.5f, 16.5f), P(16, 16.5f) });
                    g.DrawRectangle(pen, r.Left + 8 * u, r.Top + 13.5f * u, 8 * u, 6.5f * u);
                    break;
                case "copy":
                    g.DrawRectangle(pen, r.Left + 9 * u, r.Top + 8.5f * u, 10.5f * u, 11.5f * u);
                    g.DrawLines(pen, new[] { P(6, 15.5f), P(4.5f, 15.5f), P(4.5f, 4), P(15, 4), P(15, 5.5f) });
                    break;
            }
            g.SmoothingMode = state;
        }

        private void DrawToolHover(Graphics g, Rectangle r, Part part, float s)
        {
            if (_hover != part) return;
            var inner = Rectangle.Inflate(r, -(int)(2 * s), -(int)(6 * s));
            using var path = RoundRect(inner, inner.Height / 2);
            using var fill = new SolidBrush(Color.FromArgb(A(52), 255, 255, 255));
            g.FillPath(fill, path);
        }

        private void Separator(Graphics g, int x, float s)
        {
            using var pen = new Pen(Color.FromArgb(A(52), 255, 255, 255), 1f);
            int inset = (int)(14 * s);
            g.DrawLine(pen, x, _rcBar.Top + inset, x, _rcBar.Bottom - inset);
        }

        private static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, bool center)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0) return;
            using var brush = new SolidBrush(color);
            using var format = new StringFormat
            {
                Alignment = center ? StringAlignment.Center : StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            // Soft shadow keeps captions legible on bright photos while the vignette fades.
            using var shadow = new SolidBrush(Color.FromArgb(color.A * 45 / 100, 0, 0, 0));
            var shifted = bounds; shifted.Offset(0, 1);
            g.DrawString(text, font, shadow, shifted, format);
            g.DrawString(text, font, brush, bounds, format);
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
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
            if (disposing) { _timer.Stop(); _timer.Dispose(); foreach (var icon in _icons.Values) icon.Dispose(); _icons.Clear(); }
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
                    int x = (short)((long)m.LParam & 0xFFFF);
                    int y = (short)(((long)m.LParam >> 16) & 0xFFFF);
                    if (HitTest(PointToClient(new Point(x, y))) == Part.None)
                    {
                        m.Result = (IntPtr)HTTRANSPARENT;
                        if (_hover != Part.None) SetHover(Part.None);
                    }
                }
                catch { }
                return;
            }

            base.WndProc(ref m);
        }
    }
}
