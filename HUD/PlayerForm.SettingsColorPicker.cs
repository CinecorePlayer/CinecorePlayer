#nullable enable
using CinecorePlayer2025.HUD;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private sealed class AccentColorPickerForm : HudModalFormBase
        {
            private readonly Button _okButton;
            private readonly Button _cancelButton;
            private readonly string _title;
            private readonly bool _english;
            private Rectangle _wheelRect;
            private Rectangle _sliderRect;
            private Rectangle _previewRect;
            private double _hue;
            private double _sat;
            private double _val;
            private bool _dragWheel;
            private bool _dragSlider;
            private bool _pickerControlsReady;
            private Bitmap? _wheelBitmap;

            public Color SelectedColor { get; private set; }

            public AccentColorPickerForm(Color initial, string title = "Colore interfaccia", bool english = false)
            {
                _english = english;
                _title = string.IsNullOrWhiteSpace(title) ? (_english ? "Interface color" : "Colore interfaccia") : title;
                SelectedColor = Color.FromArgb(255, initial.R, initial.G, initial.B);
                RgbToHsv(SelectedColor, out _hue, out _sat, out _val);

                Text = _title;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                _okButton = CreateModalButton(_english ? "Apply" : "Applica", DialogResult.OK, primary: true);
                _cancelButton = CreateModalButton(_english ? "Cancel" : "Annulla", DialogResult.Cancel, primary: false);

                Controls.Add(_okButton);
                Controls.Add(_cancelButton);
                AcceptButton = _okButton;
                CancelButton = _cancelButton;
                _pickerControlsReady = true;
                ClientSize = new Size(520, 390);
                UpdateButtonColors();
                LayoutPicker();
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                InvalidateWheelBitmap();
                LayoutPicker();
                Invalidate();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    InvalidateWheelBitmap();
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                try
                {
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                    using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 13.4f);
                    using var textFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.2f);
                    TextRenderer.DrawText(g, _title, titleFont, new Rectangle(24, 16, ClientSize.Width - 48, 28), Color.White,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, _english ? "Choose hue and saturation from the wheel." : "Scegli tinta e saturazione dalla ruota.", textFont, new Rectangle(24, 42, ClientSize.Width - 48, 22), Color.FromArgb(170, 184, 198),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                    DrawWheel(g);
                    DrawSlider(g);
                    DrawPreview(g);
                }
                catch
                {
                    using var font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.2f);
                    TextRenderer.DrawText(e.Graphics, _english ? "The color picker is unavailable." : "Il selettore colore non è disponibile.", font, ClientRectangle, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left)
                    return;

                if (_wheelRect.Contains(e.Location))
                {
                    _dragWheel = true;
                    UpdateWheel(e.Location);
                }
                else if (_sliderRect.Contains(e.Location))
                {
                    _dragSlider = true;
                    UpdateSlider(e.Location);
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_dragWheel)
                    UpdateWheel(e.Location);
                else if (_dragSlider)
                    UpdateSlider(e.Location);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                _dragWheel = false;
                _dragSlider = false;
                base.OnMouseUp(e);
            }

            private void LayoutPicker()
            {
                if (!_pickerControlsReady || _cancelButton == null || _okButton == null || _cancelButton.IsDisposed || _okButton.IsDisposed)
                    return;

                int wheel = Math.Min(238, Math.Max(176, ClientSize.Height - 142));
                _wheelRect = new Rectangle(24, 74, wheel, wheel);
                _sliderRect = new Rectangle(_wheelRect.Right + 22, _wheelRect.Top, 26, _wheelRect.Height);
                _previewRect = new Rectangle(_sliderRect.Right + 28, _wheelRect.Top + 12, 92, 76);

                int y = ClientSize.Height - 52;
                _cancelButton.SetBounds(ClientSize.Width - 224, y, 96, 38);
                _okButton.SetBounds(ClientSize.Width - 118, y, 94, 38);
            }

            private void DrawWheel(Graphics g)
            {
                if (_wheelBitmap == null || _wheelBitmap.Size != _wheelRect.Size)
                    _wheelBitmap = BuildWheelBitmap(_wheelRect.Size);

                Bitmap bmp = _wheelBitmap;
                g.DrawImageUnscaled(bmp, _wheelRect.Location);
                float cx = (_wheelRect.Width - 1) / 2f;
                float cy = (_wheelRect.Height - 1) / 2f;
                float radius = Math.Min(cx, cy);
                using var border = new Pen(Color.FromArgb(86, 122, 150, 178), 1.2f);
                g.DrawEllipse(border, _wheelRect);

                double angle = _hue * Math.PI / 180.0;
                int mx = _wheelRect.Left + (int)Math.Round(cx + Math.Cos(angle) * _sat * radius);
                int my = _wheelRect.Top + (int)Math.Round(cy + Math.Sin(angle) * _sat * radius);
                Rectangle marker = new Rectangle(mx - 6, my - 6, 12, 12);
                using var outer = new Pen(Color.Black, 3f);
                using var inner = new Pen(Color.White, 1.5f);
                g.DrawEllipse(outer, marker);
                g.DrawEllipse(inner, marker);
            }

            private void DrawSlider(Graphics g)
            {
                using var path = RoundRect(_sliderRect, 8);
                for (int y = 0; y < _sliderRect.Height; y++)
                {
                    double value = 1.0 - (y / (double)Math.Max(1, _sliderRect.Height - 1));
                    using var pen = new Pen(ColorFromHsv(_hue, _sat, value));
                    g.DrawLine(pen, _sliderRect.Left, _sliderRect.Top + y, _sliderRect.Right, _sliderRect.Top + y);
                }

                using var border = new Pen(Color.FromArgb(86, 122, 150, 178), 1.2f);
                g.DrawPath(border, path);

                int markerY = _sliderRect.Top + (int)Math.Round((1.0 - _val) * _sliderRect.Height);
                markerY = Math.Max(_sliderRect.Top, Math.Min(_sliderRect.Bottom, markerY));
                using var p = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(p, _sliderRect.Left - 5, markerY, _sliderRect.Right + 5, markerY);

                using var font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 8.5f);
                TextRenderer.DrawText(g, _english ? "Brightness" : "Luminosità", font, new Rectangle(_sliderRect.Left - 18, _sliderRect.Bottom + 6, 74, 18), Color.FromArgb(170, 184, 198),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            private void DrawPreview(Graphics g)
            {
                using (var path = RoundRect(_previewRect, 9))
                using (var fill = new LinearGradientBrush(_previewRect, SelectedColor, Color.FromArgb(255, Math.Max(0, SelectedColor.R - 42), Math.Max(0, SelectedColor.G - 42), Math.Max(0, SelectedColor.B - 42)), LinearGradientMode.ForwardDiagonal))
                using (var border = new Pen(Color.FromArgb(118, SelectedColor), 1.4f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                string hex = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
                using var font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10.2f);
                TextRenderer.DrawText(g, hex, font, new Rectangle(_previewRect.Left, _previewRect.Bottom + 12, _previewRect.Width + 38, 24), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            private void UpdateWheel(Point p)
            {
                double cx = _wheelRect.Left + (_wheelRect.Width - 1) / 2.0;
                double cy = _wheelRect.Top + (_wheelRect.Height - 1) / 2.0;
                double radius = Math.Min(_wheelRect.Width, _wheelRect.Height) / 2.0;
                double dx = p.X - cx;
                double dy = p.Y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                _hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (_hue < 0) _hue += 360.0;
                _sat = Math.Max(0.0, Math.Min(1.0, dist / radius));
                UpdateSelectedColor();
            }

            private void UpdateSlider(Point p)
            {
                double y = Math.Max(_sliderRect.Top, Math.Min(_sliderRect.Bottom, p.Y));
                _val = 1.0 - ((y - _sliderRect.Top) / Math.Max(1.0, _sliderRect.Height));
                _val = Math.Max(0.0, Math.Min(1.0, _val));
                InvalidateWheelBitmap();
                UpdateSelectedColor();
            }

            private Bitmap BuildWheelBitmap(Size size)
            {
                var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
                float cx = (bmp.Width - 1) / 2f;
                float cy = (bmp.Height - 1) / 2f;
                float radius = Math.Min(cx, cy);
                for (int y = 0; y < bmp.Height; y++)
                {
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        double dx = x - cx;
                        double dy = y - cy;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist > radius)
                        {
                            bmp.SetPixel(x, y, Color.Transparent);
                            continue;
                        }

                        double hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                        if (hue < 0) hue += 360.0;
                        bmp.SetPixel(x, y, ColorFromHsv(hue, Math.Min(1.0, dist / radius), _val));
                    }
                }
                return bmp;
            }

            private void InvalidateWheelBitmap()
            {
                try { _wheelBitmap?.Dispose(); } catch { }
                _wheelBitmap = null;
            }

            private void UpdateSelectedColor()
            {
                SelectedColor = ColorFromHsv(_hue, _sat, _val);
                UpdateButtonColors();
                Invalidate();
            }

            private void UpdateButtonColors()
            {
                _okButton.BackColor = SelectedColor;
                _okButton.FlatAppearance.BorderColor = SelectedColor;
            }

            private static GraphicsPath RoundRect(Rectangle r, int radius)
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

            private static Color ColorFromHsv(double hue, double saturation, double value)
            {
                hue %= 360.0;
                if (hue < 0) hue += 360.0;
                saturation = Math.Max(0.0, Math.Min(1.0, saturation));
                value = Math.Max(0.0, Math.Min(1.0, value));

                int hi = Convert.ToInt32(Math.Floor(hue / 60.0)) % 6;
                double f = hue / 60.0 - Math.Floor(hue / 60.0);
                value *= 255.0;
                int v = Convert.ToInt32(value);
                int p = Convert.ToInt32(value * (1.0 - saturation));
                int q = Convert.ToInt32(value * (1.0 - f * saturation));
                int t = Convert.ToInt32(value * (1.0 - (1.0 - f) * saturation));

                return hi switch
                {
                    0 => Color.FromArgb(255, v, t, p),
                    1 => Color.FromArgb(255, q, v, p),
                    2 => Color.FromArgb(255, p, v, t),
                    3 => Color.FromArgb(255, p, q, v),
                    4 => Color.FromArgb(255, t, p, v),
                    _ => Color.FromArgb(255, v, p, q)
                };
            }

            private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
            {
                double r = color.R / 255.0;
                double g = color.G / 255.0;
                double b = color.B / 255.0;
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double delta = max - min;

                hue = color.GetHue();
                saturation = max <= 0 ? 0 : delta / max;
                value = max;
            }
        }

    }
}
