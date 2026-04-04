#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using FFmpeg.AutoGen;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using SkiaSharp;
// ✅ SVG rendering (NuGet): Svg.Skia + SkiaSharp
using Svg.Skia;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    // ======= Splash overlay (home) =======
    internal sealed class SplashOverlay : Control
    {
        public event Action? OpenRequested;
        public event Action? SettingsRequested;
        public event Action? CreditsRequested;

        private Image? _img; // logo.png
        private Image? _icoOpen, _icoSettings, _icoCredits;

        private Rectangle _lastRcOpen, _lastRcSettings, _lastRcCredits;

        // ===== DPAD selection =====
        // 0 = Settings, 1 = Open, 2 = Credits
        private int _dpadSel = 1;

        // cache per-size
        private int _lastIconPx = -1;

        protected override bool ShowFocusCues => false;

        public SplashOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            Dock = DockStyle.Fill;
            BackColor = Color.Black;
            TabStop = false;
            Cursor = Cursors.Default;
            SetStyle(ControlStyles.Selectable, false);

            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(p))
            {
                using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var tmp = Image.FromStream(fs);
                _img = new Bitmap(tmp);
            }
        }
        public void DpadMove(string dir)
        {
            switch (dir)
            {
                case "left":
                    _dpadSel = (_dpadSel + 2) % 3;
                    Invalidate();
                    break;
                case "right":
                    _dpadSel = (_dpadSel + 1) % 3;
                    Invalidate();
                    break;

                // Per ora up/down non fanno nulla (3 bottoni in riga)
                case "up":
                case "down":
                default:
                    break;
            }
        }

        public void DpadOk()
        {
            switch (_dpadSel)
            {
                case 0: SettingsRequested?.Invoke(); break;
                case 1: OpenRequested?.Invoke(); break;
                case 2: CreditsRequested?.Invoke(); break;
            }
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            // Load icons once at the current size
            ReloadIconsIfNeeded(force: true);
        }

        private void ReloadIconsIfNeeded(bool force = false)
        {
            // size of the circular button
            int size = Math.Max(44, Math.Min(64, (int)Math.Round(Height * 0.058)));
            // icon inner area (same logic as your pad in DrawIcon)
            int pad = Math.Max(10, (int)Math.Round(size * 0.22));
            int iconPx = Math.Max(1, size - pad * 2);

            if (!force && iconPx == _lastIconPx)
                return;

            _lastIconPx = iconPx;

            // dispose old
            _icoOpen?.Dispose(); _icoOpen = null;
            _icoSettings?.Dispose(); _icoSettings = null;
            _icoCredits?.Dispose(); _icoCredits = null;

            // load SVG rendered to bitmap
            _icoOpen = TryLoadSvg("icons/library.svg", iconPx);
            _icoSettings = TryLoadSvg("icons/settings.svg", iconPx);
            _icoCredits = TryLoadSvg("icons/info-circle.svg", iconPx);
        }

        private void RecomputeButtonHitboxes()
        {
            if (_img == null)
            {
                _lastRcOpen = _lastRcSettings = _lastRcCredits = Rectangle.Empty;
                return;
            }

            int maxW = (int)(Width * 0.60);
            int maxH = (int)(Height * 0.60);
            double s = Math.Min(maxW / (double)_img.Width, maxH / (double)_img.Height);
            int w = Math.Max(1, (int)Math.Round(_img.Width * s));
            int h = Math.Max(1, (int)Math.Round(_img.Height * s));
            int x = (Width - w) / 2;
            int y = (Height - h) / 2;

            int size = Math.Max(44, Math.Min(64, (int)Math.Round(Height * 0.058)));
            int gap = Math.Max(14, Math.Min(28, (int)Math.Round(size * 0.35)));
            double t = Math.Clamp((Height - 800) / 600.0, 0, 1);
            int gapBelowLogo = (int)Math.Round(-40 + (-150 - (-40)) * t);
            int cy = y + h + gapBelowLogo;

            int bottomMargin = Math.Max(16, size / 2);
            cy = Math.Min(cy, Height - bottomMargin - size);

            _lastRcOpen = new Rectangle(Width / 2 - size / 2, cy, size, size);
            _lastRcSettings = new Rectangle(_lastRcOpen.X - size - gap, cy, size, size);
            _lastRcCredits = new Rectangle(_lastRcOpen.Right + gap, cy, size, size);
        }

        /// <summary>
        /// Render an SVG file to a System.Drawing.Image (Bitmap) at ~targetPx (max side).
        /// Requires NuGet: Svg.Skia + SkiaSharp
        /// </summary>
        private Image? TryLoadSvg(string name, int targetPx)
        {
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Assets", name);
                if (!File.Exists(p)) return null;

                var svg = new SKSvg();
                svg.Load(p);
                if (svg.Picture == null) return null;

                var bounds = svg.Picture.CullRect;
                float srcW = bounds.Width;
                float srcH = bounds.Height;
                if (srcW <= 0 || srcH <= 0) return null;

                float scale = targetPx / Math.Max(srcW, srcH);
                int outW = Math.Max(1, (int)Math.Round(srcW * scale));
                int outH = Math.Max(1, (int)Math.Round(srcH * scale));

                using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(scale);
                canvas.DrawPicture(svg.Picture);
                canvas.Flush();

                using var img = surface.Snapshot();
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var ms = new MemoryStream(data.ToArray());

                // Important: create a Bitmap that lives beyond the stream
                using var tmp = Image.FromStream(ms);
                return new Bitmap(tmp);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return null;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            g.Clear(Color.Black);
            if (_img == null) return;

            // ensure icons are rendered at current size
            ReloadIconsIfNeeded();

            int maxW = (int)(Width * 0.60);
            int maxH = (int)(Height * 0.60);
            double s = Math.Min(maxW / (double)_img.Width, maxH / (double)_img.Height);
            int w = Math.Max(1, (int)Math.Round(_img.Width * s));
            int h = Math.Max(1, (int)Math.Round(_img.Height * s));
            int x = (Width - w) / 2;
            int y = (Height - h) / 2;

            g.DrawImage(_img, x, y, w, h);

            int size = Math.Max(44, Math.Min(64, (int)Math.Round(Height * 0.058)));
            int gap = Math.Max(14, Math.Min(28, (int)Math.Round(size * 0.35)));
            double t = Math.Clamp((Height - 800) / 600.0, 0, 1);
            int gapBelowLogo = (int)Math.Round(-40 + (-150 - (-40)) * t);
            int cy = y + h + gapBelowLogo;

            int bottomMargin = Math.Max(16, size / 2);
            cy = Math.Min(cy, Height - bottomMargin - size);

            Rectangle rcOpen = new Rectangle(Width / 2 - size / 2, cy, size, size);
            Rectangle rcSettings = new Rectangle(rcOpen.X - size - gap, cy, size, size);
            Rectangle rcCredits = new Rectangle(rcOpen.Right + gap, cy, size, size);

            // ===== DPAD focus ring =====
            Rectangle rcSel = _dpadSel switch
            {
                0 => rcSettings,
                1 => rcOpen,
                2 => rcCredits,
                _ => rcOpen
            };

            var focus = Rectangle.Inflate(rcSel, 5, 5);
            using (var pen = new Pen(Color.FromArgb(220, Theme.Accent), 3f))
            {
                pen.Alignment = PenAlignment.Center;
                g.DrawEllipse(pen, focus);
            }

            static void DrawCircleSoft(Graphics gg, Rectangle r)
            {
                using var path = new GraphicsPath();
                path.AddEllipse(r);
                using var fill = new SolidBrush(Color.FromArgb(46, 255, 255, 255));
                gg.FillPath(fill, path);
            }

            DrawCircleSoft(g, rcSettings);
            DrawCircleSoft(g, rcOpen);
            DrawCircleSoft(g, rcCredits);

            void DrawIcon(Graphics gg, Rectangle r, Image? ico)
            {
                if (ico == null) return;
                int pad = Math.Max(10, (int)Math.Round(size * 0.22));
                gg.DrawImage(ico, new Rectangle(r.X + pad, r.Y + pad, r.Width - pad * 2, r.Height - pad * 2));
            }

            DrawIcon(g, rcSettings, _icoSettings);
            DrawIcon(g, rcOpen, _icoOpen);
            DrawIcon(g, rcCredits, _icoCredits);

            RecomputeButtonHitboxes();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);

            // reload icons when size changes (so svg scales cleanly)
            ReloadIconsIfNeeded(force: true);

            RecomputeButtonHitboxes();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (_lastRcSettings.Contains(e.Location)) { _dpadSel = 0; Invalidate(); SettingsRequested?.Invoke(); return; }
            if (_lastRcOpen.Contains(e.Location)) { _dpadSel = 1; Invalidate(); OpenRequested?.Invoke(); return; }
            if (_lastRcCredits.Contains(e.Location)) { _dpadSel = 2; Invalidate(); CreditsRequested?.Invoke(); return; }

            RecomputeButtonHitboxes();
            if (_lastRcOpen.Contains(e.Location)) { OpenRequested?.Invoke(); return; }
            if (_lastRcSettings.Contains(e.Location)) { SettingsRequested?.Invoke(); return; }
            if (_lastRcCredits.Contains(e.Location)) { CreditsRequested?.Invoke(); return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            RecomputeButtonHitboxes();

            bool over = _lastRcOpen.Contains(e.Location) ||
                        _lastRcSettings.Contains(e.Location) ||
                        _lastRcCredits.Contains(e.Location);

            Cursor = over ? Cursors.Hand : Cursors.Default;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _img?.Dispose();
                _icoOpen?.Dispose();
                _icoSettings?.Dispose();
                _icoCredits?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
