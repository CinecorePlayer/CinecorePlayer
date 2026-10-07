#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Modifica di una foto, a tutta finestra come il visualizzatore: la foto al centro, in alto
    // "indietro" e "salva copia", in basso una barra di icone (strumenti) e sopra di essa le opzioni
    // dello strumento scelto (colori, spessore, filtri). L'originale non si tocca mai: si salva
    // sempre una copia accanto (o dove si sceglie, se la cartella non e' scrivibile).
    internal sealed class PhotoEditForm : Form
    {
        internal enum Tool { Pen, Marker, Line, Arrow, Box, Ellipse, Text, Picker, Filter, Rotate, Undo }
        private sealed class Mark
        {
            public Tool Kind; public List<PointF> Points = new(); public Color Color; public float Width; public string Text = ""; public float Size;
        }

        internal static readonly (string It, string En, float[][]? Matrix)[] Filters =
        {
            ("Originale", "Original", null),
            ("Bianco e nero", "Black & white", new[] { new[] { .299f, .299f, .299f, 0, 0 }, new[] { .587f, .587f, .587f, 0, 0 }, new[] { .114f, .114f, .114f, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } }),
            ("Seppia", "Sepia", new[] { new[] { .393f, .349f, .272f, 0, 0 }, new[] { .769f, .686f, .534f, 0, 0 }, new[] { .189f, .168f, .131f, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } }),
            ("Caldo", "Warm", new[] { new float[] { 1.08f, 0, 0, 0, 0 }, new float[] { 0, 1.02f, 0, 0, 0 }, new float[] { 0, 0, .90f, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { .03f, .01f, -.02f, 0, 1 } }),
            ("Freddo", "Cool", new[] { new float[] { .92f, 0, 0, 0, 0 }, new float[] { 0, 1.0f, 0, 0, 0 }, new float[] { 0, 0, 1.10f, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { -.02f, 0, .03f, 0, 1 } }),
            ("Vivido", "Vivid", Saturation(1.45f)),
            ("Tenue", "Soft", Saturation(.6f)),
            ("Contrasto", "Contrast", new[] { new float[] { 1.25f, 0, 0, 0, 0 }, new float[] { 0, 1.25f, 0, 0, 0 }, new float[] { 0, 0, 1.25f, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { -.125f, -.125f, -.125f, 0, 1 } }),
            ("Luminoso", "Bright", new[] { new float[] { 1, 0, 0, 0, 0 }, new float[] { 0, 1, 0, 0, 0 }, new float[] { 0, 0, 1, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { .09f, .09f, .09f, 0, 1 } }),
        };

        private static float[][] Saturation(float s)
        {
            float r = .299f * (1 - s), g = .587f * (1 - s), b = .114f * (1 - s);
            return new[] { new[] { r + s, r, r, 0, 0 }, new[] { g, g + s, g, 0, 0 }, new[] { b, b, b + s, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } };
        }

        private static readonly Color[] Inks = { Color.White, Color.Black, Color.FromArgb(232, 60, 50), Color.FromArgb(250, 205, 40), Color.FromArgb(62, 190, 120), Color.FromArgb(50, 128, 235) };
        private static readonly Tool[] Bar = { Tool.Pen, Tool.Marker, Tool.Line, Tool.Arrow, Tool.Box, Tool.Ellipse, Tool.Text, Tool.Filter, Tool.Rotate, Tool.Undo };
        private static int[]? _customColors;

        private readonly bool _english;
        private readonly string _path;
        private Bitmap _image;
        private readonly List<Mark> _marks = new();
        private readonly List<(Rectangle Bounds, Action Run, string Tip)> _hits = new();
        private readonly ToolTip _tip = new();
        private Tool _tool = Tool.Pen, _toolBeforePicker = Tool.Pen;
        private Color _color = Inks[2];
        private int _filter, _size = 1;
        private Mark? _drawing, _typing;
        private Point _mouse = new(-1, -1);
        private string _note = "", _lastTip = "";

        public string? SavedPath { get; private set; }

        /// <param name="area">Rettangolo dello schermo da coprire (la finestra del player).</param>
        public PhotoEditForm(string path, bool english, Rectangle? area = null)
        {
            _english = english;
            _path = path;
            // Copia in memoria: il file non resta bloccato mentre l'editor e' aperto.
            using (var source = Image.FromFile(path))
            {
                try { if (Array.IndexOf(source.PropertyIdList, 0x0112) >= 0) source.RotateFlip(source.GetPropertyItem(0x0112)!.Value![0] switch { 3 => RotateFlipType.Rotate180FlipNone, 6 => RotateFlipType.Rotate90FlipNone, 8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone }); } catch { }
                _image = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(_image);
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }
            Text = T("Modifica foto", "Edit photo");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = area ?? new Rectangle(60, 60, 1280, 800);
            BackColor = Color.FromArgb(8, 10, 13);
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        private string T(string italian, string english) => _english ? english : italian;
        private float S => Math.Max(1f, DeviceDpi / 96f) * Math.Clamp(ClientSize.Height / 900f / Math.Max(1f, DeviceDpi / 96f), 1f, 1.35f);
        private int Px(float value) => (int)Math.Round(value * S);
        private float StrokeWidth => new[] { .003f, .006f, .012f }[_size] * Math.Max(_image.Width, _image.Height);
        private float TextSize => new[] { .03f, .05f, .08f }[_size] * Math.Min(_image.Width, _image.Height);
        private bool DrawsWithColor => _tool is not (Tool.Filter or Tool.Rotate or Tool.Undo);

        // ---- foto <-> finestra ----
        private Rectangle Canvas => new(Px(48), Px(84), ClientSize.Width - Px(96), ClientSize.Height - Px(84) - Px(168));
        private RectangleF Shown()
        {
            Rectangle canvas = Canvas;
            float scale = Math.Min(canvas.Width / (float)_image.Width, canvas.Height / (float)_image.Height);
            float w = _image.Width * scale, h = _image.Height * scale;
            return new RectangleF(canvas.Left + (canvas.Width - w) / 2, canvas.Top + (canvas.Height - h) / 2, w, h);
        }

        private PointF ToImage(Point p)
        {
            RectangleF shown = Shown();
            return new PointF(Math.Clamp((p.X - shown.Left) / shown.Width, 0, 1) * _image.Width, Math.Clamp((p.Y - shown.Top) / shown.Height, 0, 1) * _image.Height);
        }

        // ---- azioni ----
        private void Choose(Tool tool)
        {
            FinishTyping();
            switch (tool)
            {
                case Tool.Undo: if (_marks.Count > 0) _marks.RemoveAt(_marks.Count - 1); break;
                case Tool.Rotate: RotateRight(); break;
                case Tool.Picker: _toolBeforePicker = _tool == Tool.Picker ? _toolBeforePicker : _tool; _tool = Tool.Picker; break;
                default: _tool = tool; break;
            }
            Invalidate();
        }

        private void RotateRight()
        {
            int height = _image.Height;
            _image.RotateFlip(RotateFlipType.Rotate90FlipNone);
            foreach (var mark in _marks)
                for (int i = 0; i < mark.Points.Count; i++) mark.Points[i] = new PointF(height - mark.Points[i].Y, mark.Points[i].X);
        }

        private void PickSystemColor()
        {
            // Il selettore di colore di Windows, con i colori personalizzati ricordati finche' il player e' aperto.
            using var dialog = new ColorDialog { Color = _color, FullOpen = true, AnyColor = true, CustomColors = _customColors };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _customColors = dialog.CustomColors;
            SetColor(dialog.Color);
        }

        private void SetColor(Color color)
        {
            _color = Color.FromArgb(255, color);
            if (_typing != null) _typing.Color = _color;
            Invalidate();
        }

        private void FinishTyping()
        {
            if (_typing == null) return;
            if (_typing.Text.Trim().Length == 0) _marks.Remove(_typing);
            _typing = null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var hit in _hits)
                if (hit.Bounds.Contains(e.Location)) { hit.Run(); return; }
            if (!Shown().Contains(e.Location)) return;
            Focus();
            PointF at = ToImage(e.Location);
            if (_tool == Tool.Picker)
            {
                // Il colore si prende dalla foto com'e' nel file (senza il filtro), nel punto cliccato.
                SetColor(_image.GetPixel(Math.Clamp((int)at.X, 0, _image.Width - 1), Math.Clamp((int)at.Y, 0, _image.Height - 1)));
                _tool = _toolBeforePicker;
            }
            else if (_tool == Tool.Text)
            {
                FinishTyping();
                _typing = new Mark { Kind = Tool.Text, Color = _color, Size = TextSize };
                _typing.Points.Add(at);
                _marks.Add(_typing);
            }
            else if (_tool is Tool.Pen or Tool.Marker or Tool.Line or Tool.Arrow or Tool.Box or Tool.Ellipse)
            {
                _drawing = new Mark { Kind = _tool, Color = _color, Width = StrokeWidth };
                _drawing.Points.Add(at);
                _drawing.Points.Add(at);
                _marks.Add(_drawing);
                Capture = true;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            if (_drawing != null)
            {
                PointF at = ToImage(e.Location);
                if (_drawing.Kind is Tool.Pen or Tool.Marker) _drawing.Points.Add(at);
                else _drawing.Points[1] = at;
                Invalidate(Canvas);
                return;
            }
            string tip = _hits.FirstOrDefault(hit => hit.Bounds.Contains(e.Location)).Tip ?? "";
            Cursor = tip.Length > 0 ? Cursors.Hand : Shown().Contains(e.Location) ? (_tool == Tool.Text ? Cursors.IBeam : Cursors.Cross) : Cursors.Default;
            if (tip != _lastTip) { _lastTip = tip; _tip.SetToolTip(this, tip); }
            Invalidate(new Rectangle(0, 0, ClientSize.Width, Px(72)));
            Invalidate(new Rectangle(0, ClientSize.Height - Px(168), ClientSize.Width, Px(168)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drawing == null) return;
            // Un clic senza trascinare lascia un punto con la penna e niente con le forme.
            if (_drawing.Kind is not (Tool.Pen or Tool.Marker) && Distance(_drawing.Points[0], _drawing.Points[1]) < _drawing.Width) _marks.Remove(_drawing);
            _drawing = null;
            Capture = false;
            Invalidate();
        }

        private static float Distance(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_typing != null)
            {
                if (keyData == Keys.Enter) { FinishTyping(); Invalidate(); return true; }
                if (keyData == Keys.Escape) { _marks.Remove(_typing); _typing = null; Invalidate(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { Choose(Tool.Undo); return true; }
            if (keyData == (Keys.Control | Keys.S)) { Save(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (_typing == null) return;
            if (e.KeyChar == '\b') { if (_typing.Text.Length > 0) _typing.Text = _typing.Text[..^1]; }
            else if (!char.IsControl(e.KeyChar) && _typing.Text.Length < 200) _typing.Text += e.KeyChar;
            e.Handled = true;
            Invalidate(Canvas);
        }

        // ---- disegno della foto: lo stesso a schermo e nel file ----
        internal void Render(Graphics g, RectangleF target, bool caret)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using (var attributes = new ImageAttributes())
            {
                if (Filters[_filter].Matrix is { } matrix) attributes.SetColorMatrix(new ColorMatrix(matrix));
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(_image, Rectangle.Round(target), 0, 0, _image.Width, _image.Height, GraphicsUnit.Pixel, attributes);
            }
            float k = target.Width / _image.Width;
            PointF Map(PointF p) => new(target.Left + p.X * k, target.Top + p.Y * k);
            foreach (var mark in _marks)
            {
                if (mark.Kind == Tool.Text)
                {
                    string text = mark.Text + (caret && ReferenceEquals(mark, _typing) ? "|" : "");
                    if (text.Length == 0) continue;
                    using var font = new Font("Segoe UI Semibold", Math.Max(6f, mark.Size * k), FontStyle.Regular, GraphicsUnit.Pixel);
                    using var brush = new SolidBrush(mark.Color);
                    // Un'ombra sottile tiene leggibile la scritta su qualsiasi foto.
                    using var shadow = new SolidBrush(Color.FromArgb(140, mark.Color.GetBrightness() > .5f ? Color.Black : Color.White));
                    PointF at = Map(mark.Points[0]);
                    float offset = Math.Max(1f, font.Size / 22f);
                    g.DrawString(text, font, shadow, at.X + offset, at.Y - font.Size / 2 + offset);
                    g.DrawString(text, font, brush, at.X, at.Y - font.Size / 2);
                    continue;
                }
                if (mark.Points.Count < 2) continue;
                bool marker = mark.Kind == Tool.Marker;
                using var pen = new Pen(marker ? Color.FromArgb(105, mark.Color) : mark.Color, Math.Max(1f, mark.Width * k * (marker ? 4f : 1f)))
                    { StartCap = marker ? LineCap.Flat : LineCap.Round, EndCap = marker ? LineCap.Flat : LineCap.Round, LineJoin = LineJoin.Round };
                PointF a = Map(mark.Points[0]), b = Map(mark.Points[^1]);
                var box = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                switch (mark.Kind)
                {
                    case Tool.Pen: case Tool.Marker: g.DrawLines(pen, mark.Points.Select(Map).ToArray()); break;
                    case Tool.Line: g.DrawLine(pen, a, b); break;
                    case Tool.Box: g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height); break;
                    case Tool.Ellipse: g.DrawEllipse(pen, box); break;
                    case Tool.Arrow:
                        float length = Distance(a, b);
                        if (length < 1) break;
                        float head = Math.Min(length * .45f, pen.Width * 4.5f + 6), ux = (b.X - a.X) / length, uy = (b.Y - a.Y) / length;
                        var neck = new PointF(b.X - ux * head, b.Y - uy * head);
                        g.DrawLine(pen, a, neck);
                        using (var tip = new SolidBrush(mark.Color))
                            g.FillPolygon(tip, new[] { b, new PointF(neck.X - uy * head * .5f, neck.Y + ux * head * .5f), new PointF(neck.X + uy * head * .5f, neck.Y - ux * head * .5f) });
                        break;
                }
            }
        }

        // ---- interfaccia ----
        private static GraphicsPath Pill(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void Glass(Graphics g, Rectangle r)
        {
            // Ombra morbida sotto e superficie scura semitrasparente: la barra "galleggia" sulla foto.
            for (int i = 4; i >= 1; i--)
            {
                using var shade = Pill(RectangleF.Inflate(r, i * 2, i * 2 - 1), r.Height / 2f + i * 2);
                using var soft = new SolidBrush(Color.FromArgb(14, 0, 0, 0));
                g.FillPath(soft, shade);
            }
            using var shape = Pill(r, r.Height / 2f);
            using var fill = new SolidBrush(Color.FromArgb(232, 22, 26, 32));
            using var edge = new Pen(Color.FromArgb(34, 255, 255, 255));
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }

        // Icone disegnate a tratto, tutte con lo stesso spessore.
        private void Glyph(Graphics g, Tool tool, Rectangle r, Color color)
        {
            float u = r.Width / 24f;
            using var pen = new Pen(color, Math.Max(1.4f, 1.7f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);
            PointF P(float x, float y) => new(r.Left + x * u, r.Top + y * u);
            switch (tool)
            {
                case Tool.Pen: g.DrawLines(pen, new[] { P(4, 20), P(5, 15.5f), P(16, 4.5f), P(19.5f, 8), P(8.5f, 19), P(4, 20) }); g.DrawLine(pen, P(13.5f, 7), P(17, 10.5f)); break;
                case Tool.Marker: g.DrawPolygon(pen, new[] { P(8, 4), P(16, 4), P(16, 13), P(13.5f, 16.5f), P(10.5f, 16.5f), P(8, 13) }); g.DrawLine(pen, P(12, 16.5f), P(12, 20)); g.DrawLine(pen, P(6, 20.5f), P(18, 20.5f)); break;
                case Tool.Line: g.DrawLine(pen, P(5, 19), P(19, 5)); break;
                case Tool.Arrow: g.DrawLine(pen, P(5, 19), P(19, 5)); g.DrawLines(pen, new[] { P(11, 5), P(19, 5), P(19, 13) }); break;
                case Tool.Box: g.DrawRectangle(pen, r.Left + 4.5f * u, r.Top + 6 * u, 15 * u, 12 * u); break;
                case Tool.Ellipse: g.DrawEllipse(pen, r.Left + 4 * u, r.Top + 5.5f * u, 16 * u, 13 * u); break;
                case Tool.Text: g.DrawLine(pen, P(6, 6), P(18, 6)); g.DrawLine(pen, P(12, 6), P(12, 19)); g.DrawLine(pen, P(9.5f, 19), P(14.5f, 19)); break;
                case Tool.Picker: g.DrawLine(pen, P(5, 19), P(13, 11)); g.DrawLine(pen, P(11, 9), P(15, 13)); g.FillEllipse(brush, r.Left + 13.5f * u, r.Top + 4 * u, 6.5f * u, 6.5f * u); break;
                case Tool.Filter:
                    g.DrawEllipse(pen, r.Left + 7 * u, r.Top + 4 * u, 10 * u, 10 * u); g.DrawEllipse(pen, r.Left + 3.5f * u, r.Top + 10 * u, 10 * u, 10 * u); g.DrawEllipse(pen, r.Left + 10.5f * u, r.Top + 10 * u, 10 * u, 10 * u); break;
                case Tool.Rotate: g.DrawArc(pen, r.Left + 5 * u, r.Top + 5 * u, 14 * u, 14 * u, -60, 300); g.DrawLines(pen, new[] { P(14.5f, 3.5f), P(18.5f, 5.5f), P(16, 9.5f) }); break;
                case Tool.Undo: g.DrawArc(pen, r.Left + 6 * u, r.Top + 8 * u, 12 * u, 11 * u, 180, 230); g.DrawLines(pen, new[] { P(5, 8.5f), P(5.5f, 13.5f), P(10.5f, 13) }); break;
            }
        }

        private string ToolName(Tool tool) => tool switch
        {
            Tool.Pen => T("Penna", "Pen"), Tool.Marker => T("Evidenziatore", "Highlighter"), Tool.Line => T("Linea", "Line"), Tool.Arrow => T("Freccia", "Arrow"),
            Tool.Box => T("Rettangolo", "Rectangle"), Tool.Ellipse => T("Ellisse", "Ellipse"), Tool.Text => T("Testo", "Text"), Tool.Picker => T("Prendi un colore dalla foto", "Pick a colour from the photo"),
            Tool.Filter => T("Filtri", "Filters"), Tool.Rotate => T("Ruota a destra", "Rotate right"), _ => T("Annulla ultimo (Ctrl+Z)", "Undo last (Ctrl+Z)")
        };

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            _hits.Clear();
            int width = ClientSize.Width, height = ClientSize.Height;

            // Foto, con un'ombra larga attorno: stacca dal fondo senza una cornice.
            RectangleF shown = Shown();
            for (int i = 6; i >= 1; i--)
            {
                using var shade = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
                g.FillRectangle(shade, RectangleF.Inflate(shown, i * Px(3), i * Px(3)));
            }
            var state = g.Save();
            g.SetClip(Rectangle.Ceiling(shown));
            Render(g, shown, caret: true);
            g.Restore(state);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // In alto: indietro, nome del file, salva copia.
            using var titleFont = new Font("Segoe UI Semibold", Px(15), FontStyle.Regular, GraphicsUnit.Pixel);
            using var smallFont = new Font("Segoe UI", Px(12.5f), FontStyle.Regular, GraphicsUnit.Pixel);
            using var buttonFont = new Font("Segoe UI Semibold", Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
            var back = new Rectangle(Px(24), Px(18), Px(40), Px(40));
            if (back.Contains(_mouse)) { using var hover = new SolidBrush(Color.FromArgb(30, 255, 255, 255)); g.FillEllipse(hover, back); }
            using (var arrow = new Pen(Color.White, Px(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                int cx = back.Left + back.Width / 2, cy = back.Top + back.Height / 2;
                g.DrawLine(arrow, cx - Px(8), cy, cx + Px(8), cy);
                g.DrawLines(arrow, new[] { new Point(cx - Px(2), cy - Px(6)), new Point(cx - Px(8), cy), new Point(cx - Px(2), cy + Px(6)) });
            }
            _hits.Add((back, () => { DialogResult = DialogResult.Cancel; Close(); }, T("Chiudi senza salvare (Esc)", "Close without saving (Esc)")));
            TextRenderer.DrawText(g, Path.GetFileName(_path), titleFont, new Rectangle(Px(120), Px(16), width - Px(240), Px(26)), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _note.Length > 0 ? _note : ToolName(_tool), smallFont, new Rectangle(Px(120), Px(42), width - Px(240), Px(20)), Color.FromArgb(150, 165, 182),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            var save = new Rectangle(width - Px(24) - Px(132), Px(20), Px(132), Px(38));
            using (var shape = Pill(save, save.Height / 2f))
            using (var fill = new SolidBrush(save.Contains(_mouse) ? Theme.AccentSoft : Theme.Accent)) g.FillPath(fill, shape);
            TextRenderer.DrawText(g, T("Salva copia", "Save a copy"), buttonFont, save, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            _hits.Add((save, Save, T("Salva una copia accanto all’originale (Ctrl+S)", "Save a copy next to the original (Ctrl+S)")));

            // Barra degli strumenti: solo icone, quella scelta e' accesa.
            int cell = Px(46), barHeight = Px(52);
            int barWidth = cell * Bar.Length + Px(16) + Px(18) * 2;
            var bar = new Rectangle((width - barWidth) / 2, height - Px(36) - barHeight, barWidth, barHeight);
            Glass(g, bar);
            int x = bar.Left + Px(8);
            foreach (Tool tool in Bar)
            {
                if (tool is Tool.Filter or Tool.Undo)
                {
                    using var divider = new Pen(Color.FromArgb(40, 255, 255, 255));
                    g.DrawLine(divider, x + Px(9), bar.Top + Px(14), x + Px(9), bar.Bottom - Px(14));
                    x += Px(18);
                }
                var slot = new Rectangle(x, bar.Top + (barHeight - cell) / 2 + Px(2), cell, cell - Px(4));
                bool active = tool == _tool, hover = slot.Contains(_mouse);
                if (active || hover)
                {
                    using var shape = Pill(Rectangle.Inflate(slot, -Px(3), -Px(3)), Px(10));
                    using var fill = new SolidBrush(active ? Color.FromArgb(215, Theme.Accent) : Color.FromArgb(28, 255, 255, 255));
                    g.FillPath(fill, shape);
                }
                int icon = Px(22);
                bool dim = tool == Tool.Undo && _marks.Count == 0;
                Glyph(g, tool, new Rectangle(slot.Left + (slot.Width - icon) / 2, slot.Top + (slot.Height - icon) / 2, icon, icon), Color.FromArgb(dim ? 80 : 255, 255, 255, 255));
                Tool chosen = tool;
                _hits.Add((slot, () => Choose(chosen), ToolName(tool)));
                x += cell;
            }

            // Sopra la barra, le opzioni dello strumento: colori e spessore, oppure i filtri.
            int optionsHeight = Px(42);
            if (_tool == Tool.Filter)
            {
                var widths = Filters.Select(filter => TextRenderer.MeasureText(g, _english ? filter.En : filter.It, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width + Px(26)).ToList();
                var strip = new Rectangle((width - widths.Sum() - Px(12)) / 2, bar.Top - Px(14) - optionsHeight, widths.Sum() + Px(12), optionsHeight);
                Glass(g, strip);
                int fx = strip.Left + Px(6);
                for (int i = 0; i < Filters.Length; i++)
                {
                    var chip = new Rectangle(fx, strip.Top + Px(5), widths[i], optionsHeight - Px(10));
                    if (i == _filter) { using var shape = Pill(chip, chip.Height / 2f); using var fill = new SolidBrush(Color.FromArgb(215, Theme.Accent)); g.FillPath(fill, shape); }
                    TextRenderer.DrawText(g, _english ? Filters[i].En : Filters[i].It, smallFont, chip, i == _filter || chip.Contains(_mouse) ? Color.White : Color.FromArgb(185, 196, 210),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    int index = i;
                    _hits.Add((chip, () => { _filter = index; Invalidate(); }, ""));
                    fx += widths[i];
                }
            }
            else if (DrawsWithColor)
            {
                int dot = Px(22), gap = Px(12);
                int stripWidth = Px(16) * 2 + (Inks.Length + 2) * (dot + gap) + Px(14) + 3 * Px(30);
                var strip = new Rectangle((width - stripWidth) / 2, bar.Top - Px(14) - optionsHeight, stripWidth, optionsHeight);
                Glass(g, strip);
                int cx = strip.Left + Px(16), cy = strip.Top + (optionsHeight - dot) / 2;
                void Ring(Rectangle r) { using var ring = new Pen(Color.White, Px(1.6f)); g.DrawEllipse(ring, Rectangle.Inflate(r, Px(3), Px(3))); }
                bool preset = false;
                foreach (Color ink in Inks)
                {
                    var r = new Rectangle(cx, cy, dot, dot);
                    using (var fill = new SolidBrush(ink)) g.FillEllipse(fill, r);
                    using (var edge = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawEllipse(edge, r);
                    if (ink.ToArgb() == _color.ToArgb()) { Ring(r); preset = true; }
                    Color chosen = ink;
                    _hits.Add((Rectangle.Inflate(r, Px(5), Px(8)), () => SetColor(chosen), ""));
                    cx += dot + gap;
                }
                // Colore a scelta: il selettore di Windows. Mostra il colore in uso quando non e' uno dei sei.
                var custom = new Rectangle(cx, cy, dot, dot);
                using (var wheel = new LinearGradientBrush(custom, Color.Red, Color.Blue, 45f))
                {
                    wheel.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(255, 80, 80), Color.FromArgb(255, 210, 60), Color.FromArgb(70, 200, 120), Color.FromArgb(70, 140, 255), Color.FromArgb(190, 90, 230) }, Positions = new[] { 0f, .25f, .5f, .75f, 1f } };
                    if (preset) g.FillEllipse(wheel, custom);
                    else { using var fill = new SolidBrush(_color); g.FillEllipse(fill, custom); Ring(custom); }
                }
                _hits.Add((Rectangle.Inflate(custom, Px(5), Px(8)), PickSystemColor, T("Altri colori…", "More colours…")));
                cx += dot + gap;
                var picker = new Rectangle(cx - Px(2), cy - Px(2), dot + Px(4), dot + Px(4));
                if (_tool == Tool.Picker) { using var shape = Pill(Rectangle.Inflate(picker, Px(4), Px(4)), Px(9)); using var fill = new SolidBrush(Color.FromArgb(215, Theme.Accent)); g.FillPath(fill, shape); }
                Glyph(g, Tool.Picker, picker, Color.White);
                _hits.Add((Rectangle.Inflate(picker, Px(5), Px(8)), () => Choose(Tool.Picker), ToolName(Tool.Picker)));
                cx += dot + gap + Px(6);
                using (var divider = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawLine(divider, cx, strip.Top + Px(11), cx, strip.Bottom - Px(11));
                cx += Px(8);
                for (int i = 0; i < 3; i++)
                {
                    var slot = new Rectangle(cx, strip.Top, Px(30), optionsHeight);
                    int d = Px(new[] { 5f, 9f, 14f }[i]);
                    using var fill = new SolidBrush(Color.FromArgb(i == _size ? 255 : slot.Contains(_mouse) ? 190 : 110, 255, 255, 255));
                    g.FillEllipse(fill, slot.Left + (slot.Width - d) / 2, slot.Top + (slot.Height - d) / 2, d, d);
                    int index = i;
                    _hits.Add((slot, () => { _size = index; if (_typing != null) _typing.Size = TextSize; Invalidate(); }, T("Spessore", "Thickness")));
                    cx += Px(30);
                }
            }
        }

        internal Bitmap RenderFull()
        {
            var result = new Bitmap(_image.Width, _image.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(result);
            Render(g, new RectangleF(0, 0, _image.Width, _image.Height), caret: false);
            return result;
        }

        private void Save()
        {
            FinishTyping();
            try
            {
                string folder = Path.GetDirectoryName(_path) ?? "", name = Path.GetFileNameWithoutExtension(_path);
                bool png = string.Equals(Path.GetExtension(_path), ".png", StringComparison.OrdinalIgnoreCase);
                string extension = png ? ".png" : ".jpg";
                string target = Path.Combine(folder, name + T(" (modificata)", " (edited)") + extension);
                for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, name + T(" (modificata ", " (edited ") + n + ")" + extension);
                using var output = RenderFull();
                try { WriteImage(output, target, png); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Runtime.InteropServices.ExternalException)
                {
                    // Cartella di sola lettura (disco di rete, libreria montata): si sceglie dove salvarla.
                    using var dialog = new SaveFileDialog
                    {
                        Title = T("Salva la copia modificata", "Save the edited copy"),
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        FileName = Path.GetFileName(target),
                        Filter = png ? "PNG|*.png" : "JPEG|*.jpg"
                    };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    target = dialog.FileName;
                    WriteImage(output, target, png);
                }
                SavedPath = target;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Dbg.Warn("[PHOTO] save: " + ex.Message);
                _note = T("Salvataggio non riuscito: ", "Saving failed: ") + ex.Message;
                Invalidate();
            }
        }

        internal static void WriteImage(Bitmap image, string path, bool png)
        {
            if (png) { image.Save(path, ImageFormat.Png); return; }
            var codec = ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 93L);
            image.Save(path, codec, parameters);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _image.Dispose(); _tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
