#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025.HUD
{
    /// <summary>Il cast del film in riproduzione: fotografia, nome e personaggio. Si apre dal tasto nell'overlay.</summary>
    internal sealed class CastForm : HudModalFormBase
    {
        private readonly bool _english;
        private readonly string _film;
        private readonly List<MovieMetadataService.RichCastMember> _members;
        private readonly Dictionary<int, Image> _photos = new();
        private readonly CancellationTokenSource _stop = new();
        private const int Columns = 3, Rows = 4, CellH = 104, PhotoW = 62, PhotoH = 92, Side = 30, Top = 66;

        public CastForm(bool english, string film, IEnumerable<MovieMetadataService.RichCastMember> members)
        {
            _english = english;
            _film = film ?? "";
            _members = members.Where(m => !string.IsNullOrWhiteSpace(m.Name)).Take(Columns * Rows).ToList();
            Text = "Cast";
            int rows = Math.Max(1, (_members.Count + Columns - 1) / Columns);
            ClientSize = new Size(840, Top + rows * CellH + 76);
            MinimumSize = MaximumSize = Size;
            Button close = CreateModalButton(english ? "Close" : "Chiudi", DialogResult.Cancel, primary: true);
            close.SetBounds(ClientSize.Width - Side - 120, ClientSize.Height - 58, 120, 38);
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
            LoadPhotos();
        }

        private void LoadPhotos()
        {
            for (int i = 0; i < _members.Count; i++)
            {
                int index = i;
                string? source = _members[i].ProfilePath;
                if (string.IsNullOrWhiteSpace(source)) continue;
                Task.Run(() =>
                {
                    try
                    {
                        string? local = File.Exists(source) ? source : MovieMetadataService.CacheCastProfileImage(source, _stop.Token);
                        if (local == null || !File.Exists(local) || _stop.IsCancellationRequested) return;
                        using var original = Image.FromFile(local);
                        var small = new Bitmap(PhotoW * 2, PhotoH * 2);
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            double scale = Math.Max(small.Width / (double)original.Width, small.Height / (double)original.Height);
                            int w = (int)Math.Ceiling(original.Width * scale), h = (int)Math.Ceiling(original.Height * scale);
                            g.DrawImage(original, new Rectangle((small.Width - w) / 2, (int)((small.Height - h) * .2), w, h));
                        }
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed) { small.Dispose(); return; }
                            _photos[index] = small;
                            Invalidate();
                        }));
                    }
                    catch { }
                });
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point);
            using var roleFont = global::CinecorePlayer2025.AppFonts.Create(global::CinecorePlayer2025.AppFonts.Reading, 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            const TextFormatFlags line = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            string heading = _film.Length > 0 ? "Cast | " + _film : "Cast";
            TextRenderer.DrawText(g, heading, titleFont, new Rectangle(Side, 14, ClientSize.Width - Side * 2, 42), Theme.Text, line | TextFormatFlags.VerticalCenter);

            int cellW = (ClientSize.Width - Side * 2) / Columns;
            for (int i = 0; i < _members.Count; i++)
            {
                var cell = new Rectangle(Side + i % Columns * cellW, Top + i / Columns * CellH, cellW - 14, CellH);
                var photo = new Rectangle(cell.Left, cell.Top + 4, PhotoW, PhotoH);
                using (var shape = Rounded(photo, 5))
                {
                    if (_photos.TryGetValue(i, out Image? image))
                    {
                        var state = g.Save();
                        g.SetClip(shape);
                        g.DrawImage(image, photo);
                        g.Restore(state);
                    }
                    else
                    {
                        using var empty = new SolidBrush(Color.FromArgb(26, Theme.Text));
                        g.FillPath(empty, shape);
                    }
                }
                int x = photo.Right + 14, w = Math.Max(20, cell.Right - x);
                TextRenderer.DrawText(g, _members[i].Name, nameFont, new Rectangle(x, cell.Top + 18, w, 24), Theme.Text, line);
                if (!string.IsNullOrWhiteSpace(_members[i].Character))
                    TextRenderer.DrawText(g, _members[i].Character, roleFont, new Rectangle(x, cell.Top + 44, w, 40), Theme.SubtleText, line | TextFormatFlags.WordBreak);
            }
            if (_members.Count == 0)
                TextRenderer.DrawText(g, _english ? "No cast is available for this title." : "Il cast non e' disponibile per questo titolo.", roleFont,
                    new Rectangle(Side, Top, ClientSize.Width - Side * 2, 30), Theme.SubtleText, line);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _stop.Cancel(); } catch { }
                foreach (var image in _photos.Values) { try { image.Dispose(); } catch { } }
                _photos.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
