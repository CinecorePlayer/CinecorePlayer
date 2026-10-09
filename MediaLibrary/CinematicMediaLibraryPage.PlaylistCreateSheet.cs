#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private static readonly string[] PlaylistCreateBuckets = { "Music", "Movies", "TV", "Videos", "Photos", "Mixed" };

        private PlaylistCreateSheet? PlaylistSheet => _playlistCreateOverlay as PlaylistCreateSheet;

        private void ShowCreatePlaylistOverlay(string? initialItemPath = null, List<string>? albumPaths = null)
        {
            _playlistCreatePendingAlbum = albumPaths;
            if (_playlistCreateOverlay == null)
            {
                var created = new PlaylistCreateSheet(this) { Dock = DockStyle.Fill, Visible = false };
                _playlistCreateOverlay = created;
                Controls.Add(created);
            }
            var sheet = PlaylistSheet!;

            _playlistCreateCoverPath = null;
            _playlistCreatePendingItemPath = string.IsNullOrWhiteSpace(initialItemPath) ? null : initialItemPath;
            string initialBucket = PlaylistBucketForItem(string.IsNullOrWhiteSpace(initialItemPath) ? null : ToItem(initialItemPath));
            if (_category == "Music" && string.IsNullOrWhiteSpace(initialItemPath)) initialBucket = "Music";
            _playlistCreateBucketValue = initialBucket;

            // Everything is laid out and the backdrop captured before the sheet is
            // shown: the first frame on screen is already the finished one.
            sheet.HideInputsForEntrance();
            sheet.Prepare(CaptureLibrarySnapshot());
            sheet.Visible = true;
            sheet.BringToFront();
            sheet.BeginEntrance();
        }

        private void HideCreatePlaylistOverlay()
        {
            var sheet = PlaylistSheet;
            if (sheet == null) return;
            sheet.Visible = false;
            sheet.ReleaseImages();
            _playlistCreateCoverPath = null;
            _playlistCreatePendingItemPath = null;
            _playlistCreatePendingAlbum = null;
            try { Focus(); } catch { }
            Invalidate();
        }

        private void LayoutPlaylistCreateOverlay() => PlaylistSheet?.LayoutSheet();

        private Bitmap? CaptureLibrarySnapshot()
        {
            if (Width < 1 || Height < 1) return null;
            // Bitmap opaca: su una superficie con alfa il testo GDI perde il ClearType e la
            // libreria dietro al foglio appariva sfocata rispetto a quella a schermo.
            var snapshot = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
            try
            {
                using var g = Graphics.FromImage(snapshot);
                g.Clear(HUD.Theme.Panel);
                PaintLibrary(g);
                // PaintLibrary non include le finestre figlie: senza questo la ricerca
                // dell'header appariva vuota dietro al foglio "Crea playlist".
                foreach (Control child in Controls)
                {
                    if (child is not TextBoxBase box || !box.Visible || box.Width < 1 || box.Height < 1) continue;
                    try
                    {
                        using var layer = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppPArgb);
                        box.DrawToBitmap(layer, new Rectangle(Point.Empty, box.Size));
                        g.DrawImageUnscaled(layer, box.Location);
                        if (box.TextLength == 0 && box is TextBox { PlaceholderText.Length: > 0 } placeholder)
                            TextRenderer.DrawText(g, placeholder.PlaceholderText, box.Font, box.Bounds, Muted,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    }
                    catch { }
                }
                return snapshot;
            }
            catch
            {
                snapshot.Dispose();
                return null;
            }
        }

        private void ChoosePlaylistCreateCover()
        {
            using var dlg = new OpenFileDialog
            {
                Title = L("Scegli copertina playlist", "Choose playlist cover"),
                Filter = L("Immagini|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Tutti i file|*.*", "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files|*.*"),
                CheckFileExists = true,
                Multiselect = false
            };
            var owner = FindForm();
            if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != DialogResult.OK)
                return;
            SetPlaylistCreateCover(dlg.FileName);
        }

        private void SetPlaylistCreateCover(string? path)
        {
            _playlistCreateCoverPath = string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
            PlaylistSheet?.SetCover(_playlistCreateCoverPath);
        }

        private string PlaylistCreatePendingSummary()
        {
            int count = _playlistCreatePendingAlbum?.Count ?? (string.IsNullOrWhiteSpace(_playlistCreatePendingItemPath) ? 0 : 1);
            if (count == 0) return string.Empty;
            if (count > 1) return L($"{count} elementi verranno aggiunti", $"{count} items will be added");
            string path = _playlistCreatePendingAlbum?.FirstOrDefault() ?? _playlistCreatePendingItemPath ?? string.Empty;
            string title = ToItem(path)?.Title ?? Path.GetFileNameWithoutExtension(path);
            return L("Verrà aggiunto: ", "Will be added: ") + title;
        }

        /// <summary>
        /// The "new playlist" sheet, painted as one surface over a frosted snapshot of the
        /// library. Only the two text inputs are child windows; they are placed before the
        /// sheet appears and stay hidden during the entrance, which is drawn from a cached
        /// frame. Nothing is repainted piecemeal, so the sheet opens without artifacts.
        /// </summary>
        private sealed class PlaylistCreateSheet : Panel
        {
            private readonly CinematicMediaLibraryPage _owner;
            private readonly TextBox _name;
            private readonly TextBox _description;
            private readonly System.Windows.Forms.Timer _animation = new() { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
            private readonly Stopwatch _clock = new();
            private Bitmap? _backdrop;
            private Bitmap? _cover;
            private Bitmap? _cardFrame;
            private float _appear = 1f;
            private float _scale = 1f;
            private string _hover = string.Empty;
            private bool _nameError;

            private Rectangle _card, _close, _coverTile, _coverRemove, _nameShell, _descriptionShell, _create, _cancel;
            private readonly List<(Rectangle Bounds, string Bucket)> _chips = new();

            private static readonly Color CardFill = Color.FromArgb(13, 20, 29);
            private static readonly Color FieldFill = CardFill;
            private static readonly Color LabelInk = Color.FromArgb(150, 168, 188);
            private static readonly Color MutedInk = Color.FromArgb(120, 138, 158);

            public PlaylistCreateSheet(CinematicMediaLibraryPage owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
                TabStop = false;
                AllowDrop = true;

                _name = CreateInput(false);
                _name.MaxLength = 80;
                _name.PlaceholderText = owner.L("Es. Serata cinema, Road trip…", "E.g. Movie night, Road trip…");
                _name.TextChanged += (_, _) => { _nameError = false; Invalidate(_create); Invalidate(_nameShell); };
                _name.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
                };
                _description = CreateInput(true);
                _description.PlaceholderText = owner.L("Aggiungi una descrizione", "Add a description");
                _description.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter && e.Control) { Commit(); e.SuppressKeyPress = true; }
                };
                owner._playlistCreateNameBox = _name;
                owner._playlistCreateDescriptionBox = _description;
                Controls.Add(_name);
                Controls.Add(_description);

                _animation.Tick += (_, _) => OnAnimationFrame();
            }

            private TextBox CreateInput(bool multiline)
            {
                var box = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    BackColor = FieldFill,
                    ForeColor = Color.White,
                    Multiline = multiline,
                    ScrollBars = ScrollBars.None,
                    AcceptsReturn = false,
                    Visible = false,
                    TabStop = true
                };
                box.GotFocus += (_, _) => Invalidate();
                box.LostFocus += (_, _) => Invalidate();
                box.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Escape) { _owner.HideCreatePlaylistOverlay(); e.SuppressKeyPress = true; }
                };
                return box;
            }

            public void Prepare(Bitmap? snapshot)
            {
                _name.Text = string.Empty;
                _description.Text = string.Empty;
                _nameError = false;
                _hover = string.Empty;
                SetCover(null);
                _backdrop?.Dispose();
                // Sharp snapshot, dimmed progressively: an instant switch to a blurred copy read as a jump.
                _backdrop = snapshot;
                LayoutSheet();
            }

            public void ReleaseImages()
            {
                _animation.Stop();
                _backdrop?.Dispose(); _backdrop = null;
                _cover?.Dispose(); _cover = null;
                _cardFrame?.Dispose(); _cardFrame = null;
            }

            public void SetCover(string? path)
            {
                _cover?.Dispose();
                _cover = null;
                if (path != null)
                {
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var source = Utilities.ImageAssetDecoder.Read(stream);
                        _cover = CoverThumbnail(source, 520);
                    }
                    catch { _owner._playlistCreateCoverPath = null; }
                }
                Invalidate();
            }

            private static Bitmap CoverThumbnail(Image source, int side)
            {
                var thumb = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(thumb);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = Math.Max(side / (float)source.Width, side / (float)source.Height);
                float w = source.Width * scale, h = source.Height * scale;
                g.DrawImage(source, (side - w) / 2f, (side - h) / 2f, w, h);
                return thumb;
            }

            private int U(float value) => Math.Max(1, (int)Math.Round(value * _scale));

            private Font UiFont(float px, FontStyle style = FontStyle.Regular)
                => new(style.HasFlag(FontStyle.Bold) ? "Segoe UI Semibold" : "Segoe UI", Math.Max(9f, px * _scale), style & ~FontStyle.Bold, GraphicsUnit.Pixel);

            public void LayoutSheet()
            {
                if (Width < 10 || Height < 10) return;
                _scale = Math.Clamp(Math.Min(Width / 1920f, Height / 1080f), .72f, 2.4f);
                int cardW = Math.Min(Width - U(40), U(940));
                int cardH = Math.Min(Height - U(40), U(560));
                _card = new Rectangle((Width - cardW) / 2, (Height - cardH) / 2, cardW, cardH);
                int pad = U(40);
                _close = new Rectangle(_card.Right - U(24) - U(36), _card.Top + U(24), U(36), U(36));

                int top = _card.Top + U(128);
                int coverSide = Math.Min(U(250), _card.Height - U(128) - U(110));
                _coverTile = new Rectangle(_card.Left + pad, top, coverSide, coverSide);
                _coverRemove = new Rectangle(_coverTile.Right - U(38), _coverTile.Top + U(10), U(28), U(28));

                int x = _coverTile.Right + U(36);
                int fieldW = _card.Right - pad - x;
                _nameShell = new Rectangle(x, top + U(26), fieldW, U(48));
                _descriptionShell = new Rectangle(x, _nameShell.Bottom + U(44), fieldW, U(64));

                // Type chips flow under the description.
                _chips.Clear();
                using (var chipFont = UiFont(15f, FontStyle.Bold))
                {
                    int cx = x, cy = _descriptionShell.Bottom + U(40), chipH = U(34), gap = U(18);
                    foreach (string bucket in PlaylistCreateBuckets)
                    {
                        int w = TextRenderer.MeasureText(_owner.PlaylistBucketLabel(bucket), chipFont, Size.Empty, TextFormatFlags.NoPadding).Width + U(14);
                        if (cx + w > x + fieldW) { cx = x; cy += chipH + gap; }
                        _chips.Add((new Rectangle(cx, cy, w, chipH), bucket));
                        cx += w + gap;
                    }
                }

                int footerY = _card.Bottom - U(28) - U(46);
                _create = new Rectangle(_card.Right - pad - U(200), footerY, U(200), U(46));
                _cancel = new Rectangle(_create.Left - U(12) - U(124), footerY, U(124), U(46));

                using var inputFont = UiFont(17f);
                _name.Font = inputFont.Clone() as Font;
                _description.Font = inputFont.Clone() as Font;
                _name.Bounds = new Rectangle(_nameShell.Left, _nameShell.Bottom - _name.PreferredHeight - U(8), _nameShell.Width, _name.PreferredHeight);
                _description.Bounds = new Rectangle(_descriptionShell.Left, _descriptionShell.Top + U(10), _descriptionShell.Width, _descriptionShell.Height - U(18));
                Invalidate();
            }

            public void HideInputsForEntrance() => _name.Visible = _description.Visible = false;

            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

            public void BeginEntrance()
            {
                _name.Visible = _description.Visible = false;
                _cardFrame?.Dispose();
                _cardFrame = null;
                _appear = 0f;
                if (!SystemInformation.IsMenuAnimationEnabled || _card.Width < 2)
                {
                    FinishEntrance();
                    return;
                }
                // Rendered at sheet coordinates: TextRenderer ignores Graphics transforms,
                // so an offset frame would misplace every caption during the entrance.
                _cardFrame = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(_cardFrame))
                    PaintCard(g, drawInputText: true);
                _clock.Restart();
                _animation.Start();
                Invalidate();
            }

            private void OnAnimationFrame()
            {
                double t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / 150.0, 0, 1);
                _appear = (float)(1 - Math.Pow(1 - t, 3));
                Invalidate();
                if (t >= 1) FinishEntrance();
            }

            private void FinishEntrance()
            {
                _animation.Stop();
                _appear = 1f;
                // Campi di testo nativi: mostrati con il ridisegno sospeso e poi un unico
                // aggiornamento, cosi' non compaiono "dopo" sopra la card gia' disegnata.
                const int WM_SETREDRAW = 0x000B;
                bool suspended = IsHandleCreated;
                if (suspended) SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                try
                {
                    _cardFrame?.Dispose();
                    _cardFrame = null;
                    _name.Visible = _description.Visible = true;
                }
                finally
                {
                    if (suspended)
                    {
                        SendMessage(Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                        RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100); // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
                    }
                    else Invalidate();
                }
                try { _name.Focus(); } catch { }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (Visible) LayoutSheet();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                if (_backdrop != null) g.DrawImageUnscaled(_backdrop, 0, 0);
                else g.Clear(Color.FromArgb(5, 9, 14));
                using (var dim = new SolidBrush(Color.FromArgb((int)(115 * _appear), 0, 0, 0)))
                    g.FillRectangle(dim, ClientRectangle);

                if (_cardFrame != null)
                {
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = _appear });
                    int offset = 0; // fades in place: no slide
                    var source = Rectangle.Intersect(Rectangle.Inflate(_card, U(30), U(30)), new Rectangle(0, 0, _cardFrame.Width, _cardFrame.Height));
                    var dest = new Rectangle(source.X, source.Y + offset, source.Width, source.Height);
                    g.DrawImage(_cardFrame, dest, source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                    return;
                }
                PaintCard(g, drawInputText: false);
            }

            private void PaintCard(Graphics g, bool drawInputText)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Soft shadow, then the card itself with an anti-aliased outline.
                for (int i = 6; i >= 1; i--)
                {
                    var shadow = Rectangle.Inflate(_card, U(i * 3), U(i * 3));
                    shadow.Offset(0, U(i * 2));
                    using var path = Rounded(shadow, U(22 + i * 3));
                    using var brush = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
                    g.FillPath(brush, path);
                }
                using (var path = Rounded(_card, U(22)))
                {
                    using var fill = new SolidBrush(CardFill);
                    g.FillPath(fill, path);
                    using var edge = new Pen(Color.FromArgb(14, 255, 255, 255));
                    { } // senza contorno
                }

                int pad = U(40);
                using (var title = UiFont(30f, FontStyle.Bold))
                    TextRenderer.DrawText(g, _owner.L("Nuova playlist", "New playlist"), title, new Rectangle(_card.Left + pad, _card.Top + U(34), _card.Width - pad * 2 - U(50), U(42)), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                string pending = _owner.PlaylistCreatePendingSummary();
                using (var subtitle = UiFont(15f))
                {
                    string text = pending.Length > 0 ? pending : _owner.L("Raccogli film, serie, musica o foto in un'unica lista.", "Gather films, series, music or photos in one list.");
                    TextRenderer.DrawText(g, text, subtitle, new Rectangle(_card.Left + pad, _card.Top + U(78), _card.Width - pad * 2, U(24)), pending.Length > 0 ? Accent : MutedInk, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }

                // Close
                HUD.Theme.DrawCloseButton(g, _close, _hover == "close", onDark: true); // il foglio resta scuro anche nel tema chiaro

                PaintCover(g);

                using var labelFont = UiFont(12.5f, FontStyle.Bold);
                void Label(string text, Rectangle shell) => TextRenderer.DrawText(g, text.ToUpperInvariant(), labelFont,
                    new Rectangle(shell.Left, shell.Top - U(26), shell.Width, U(20)), LabelInk, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                Label(_owner.L("Nome", "Name"), _nameShell);
                Label(_owner.L("Descrizione · facoltativa", "Description · optional"), _descriptionShell);
                if (_chips.Count > 0) Label(_owner.L("Tipo", "Type"), new Rectangle(_chips[0].Bounds.Left, _chips[0].Bounds.Top, _nameShell.Width, 1));

                PaintField(g, _nameShell, _name, drawInputText, _nameError);
                PaintField(g, _descriptionShell, _description, drawInputText, false);
                if (_nameError)
                {
                    using var small = UiFont(13f);
                    TextRenderer.DrawText(g, _owner.L("Dai un nome alla playlist", "Give the playlist a name"), small,
                        new Rectangle(_nameShell.Left, _nameShell.Bottom + U(4), _nameShell.Width, U(18)), Color.FromArgb(255, 110, 120), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                }

                using (var chipFont = UiFont(15f, FontStyle.Bold))
                {
                    foreach (var (bounds, bucket) in _chips)
                    {
                        bool selected = string.Equals(bucket, _owner._playlistCreateBucketValue, StringComparison.OrdinalIgnoreCase);
                        bool hover = _hover == "chip:" + bucket;
                        // Text-only choices: the selected one is accent-coloured and underlined.
                        Color ink = selected ? ControlPaint.Light(Accent, .25f) : hover ? Color.White : MutedInk;
                        TextRenderer.DrawText(g, _owner.PlaylistBucketLabel(bucket), chipFont, bounds, ink,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        if (selected)
                        {
                            Size text = TextRenderer.MeasureText(g, _owner.PlaylistBucketLabel(bucket), chipFont, Size.Empty, TextFormatFlags.NoPadding);
                            using var under = new SolidBrush(Accent);
                            g.FillRectangle(under, bounds.Left + (bounds.Width - text.Width) / 2, bounds.Bottom - U(3), text.Width, Math.Max(2, U(2)));
                        }
                    }
                }

                // Footer
                using (var hint = UiFont(13.5f))
                    TextRenderer.DrawText(g, _owner.L("Invio per creare · Esc per chiudere", "Enter to create · Esc to close"), hint,
                        new Rectangle(_card.Left + pad, _create.Top, Math.Max(1, _cancel.Left - _card.Left - pad - U(12)), _create.Height), MutedInk,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                using var buttonFont = UiFont(16f, FontStyle.Bold);
                TextRenderer.DrawText(g, _owner.L("Annulla", "Cancel"), buttonFont, _cancel, _hover == "cancel" ? Color.White : MutedInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                bool ready = _name.Text.Trim().Length > 0;
                using (var path = Rounded(_create, _create.Height / 2))
                {
                    Color accent = Accent;
                    Color fillColor = !ready ? Color.FromArgb(90, accent) : _hover == "create" ? ControlPaint.Light(accent, .15f) : accent;
                    using var fill = new SolidBrush(fillColor);
                    g.FillPath(fill, path);
                }
                using (var pen = new Pen(Color.FromArgb(ready ? 255 : 150, 255, 255, 255), Math.Max(1.6f, 2.2f * _scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    Size textSize = TextRenderer.MeasureText(g, _owner.L("Crea playlist", "Create playlist"), buttonFont, Size.Empty, TextFormatFlags.NoPadding);
                    int total = U(14) + U(10) + textSize.Width;
                    int px = _create.Left + (_create.Width - total) / 2, py = _create.Top + _create.Height / 2;
                    g.DrawLine(pen, px, py, px + U(14), py);
                    g.DrawLine(pen, px + U(7), py - U(7), px + U(7), py + U(7));
                    TextRenderer.DrawText(g, _owner.L("Crea playlist", "Create playlist"), buttonFont, new Rectangle(px + U(24), _create.Top, textSize.Width + 4, _create.Height),
                        Color.FromArgb(ready ? 255 : 170, 255, 255, 255), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }

            private void PaintField(Graphics g, Rectangle shell, TextBox box, bool drawText, bool error)
            {
                bool focused = box.Focused;
                Color border = error ? Color.FromArgb(255, 96, 108) : focused ? Accent : Color.FromArgb(46, 255, 255, 255);
                using (var pen = new Pen(border, focused || error ? Math.Max(1.5f, 2f * _scale) : 1f))
                    g.DrawLine(pen, shell.Left, shell.Bottom - 1, shell.Right, shell.Bottom - 1);
                if (!drawText) return;
                // Entrance frame: stand-in for the TextBox, pixel-aligned with it. The name
                // field receives focus when it appears, which hides its placeholder.
                string text = box.Text.Length > 0 ? box.Text : ReferenceEquals(box, _name) ? string.Empty : box.PlaceholderText;
                Color ink = box.Text.Length > 0 ? Color.White : SystemColors.GrayText;
                var flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis |
                            (box.Multiline ? TextFormatFlags.WordBreak | TextFormatFlags.Top : TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, text, box.Font, box.Bounds, ink, flags);
            }

            private void PaintCover(Graphics g)
            {
                using var path = Rounded(_coverTile, U(18));
                bool hover = _hover == "cover";
                if (_cover != null)
                {
                    var band = new Rectangle(_coverTile.Left, _coverTile.Bottom - U(64), _coverTile.Width, U(64));
                    using (var smooth = CinecorePlayer2025.Utilities.SmoothClip.Begin(g, path))
                    {
                        var sg = smooth.Graphics;
                        sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        sg.DrawImage(_cover, _coverTile);
                        using (var shade = new LinearGradientBrush(Rectangle.Inflate(band, 0, 1), Color.Transparent, Color.FromArgb(hover ? 200 : 150, 0, 0, 0), 90f))
                            sg.FillRectangle(shade, band);
                    }
                    using (var font = UiFont(14f, FontStyle.Bold))
                        TextRenderer.DrawText(g, _owner.L("Cambia copertina", "Change cover"), font, new Rectangle(band.Left, band.Bottom - U(38), band.Width, U(28)), Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    using (var chip = new SolidBrush(Color.FromArgb(_hover == "coverRemove" ? 220 : 150, 0, 0, 0)))
                        g.FillEllipse(chip, _coverRemove);
                    using var x = new Pen(Color.White, Math.Max(1.4f, 1.8f * _scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    var c = Rectangle.Inflate(_coverRemove, -U(9), -U(9));
                    g.DrawLine(x, c.Left, c.Top, c.Right, c.Bottom);
                    g.DrawLine(x, c.Right, c.Top, c.Left, c.Bottom);
                    return;
                }

                using (var fill = new SolidBrush(Color.FromArgb(hover ? 16 : 8, 255, 255, 255)))
                    g.FillPath(fill, path);

                int cx = _coverTile.Left + _coverTile.Width / 2, cy = _coverTile.Top + _coverTile.Height / 2 - U(22);
                var disc = new Rectangle(cx - U(28), cy - U(28), U(56), U(56));
                using (var discFill = new SolidBrush(Color.FromArgb(hover ? 60 : 34, Accent)))
                    g.FillEllipse(discFill, disc);
                using (var plus = new Pen(hover ? Color.White : Color.FromArgb(200, 225, 240), Math.Max(1.8f, 2.4f * _scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(plus, cx - U(10), cy, cx + U(10), cy);
                    g.DrawLine(plus, cx, cy - U(10), cx, cy + U(10));
                }
                using (var font = UiFont(15f, FontStyle.Bold))
                    TextRenderer.DrawText(g, _owner.L("Aggiungi copertina", "Add a cover"), font, new Rectangle(_coverTile.Left + U(12), disc.Bottom + U(16), _coverTile.Width - U(24), U(24)), Color.FromArgb(225, 235, 245),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                using (var font = UiFont(13f))
                    TextRenderer.DrawText(g, _owner.L("Clic o trascina un'immagine", "Click or drop an image"), font, new Rectangle(_coverTile.Left + U(12), disc.Bottom + U(42), _coverTile.Width - U(24), U(20)), MutedInk,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            private string HitTest(Point p)
            {
                if (_close.Contains(p)) return "close";
                if (_cover != null && _coverRemove.Contains(p)) return "coverRemove";
                if (_coverTile.Contains(p)) return "cover";
                if (_create.Contains(p)) return "create";
                if (_cancel.Contains(p)) return "cancel";
                foreach (var (bounds, bucket) in _chips)
                    if (bounds.Contains(p)) return "chip:" + bucket;
                return string.Empty;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_cardFrame != null) return;
                string hover = HitTest(e.Location);
                if (hover == _hover) return;
                _hover = hover;
                Cursor = hover.Length > 0 ? Cursors.Hand : Cursors.Default;
                Invalidate(Rectangle.Inflate(_card, U(4), U(4)));
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_hover.Length == 0) return;
                _hover = string.Empty;
                Invalidate(_card);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || _cardFrame != null) return;
                string hit = HitTest(e.Location);
                switch (hit)
                {
                    case "close":
                    case "cancel":
                        _owner.HideCreatePlaylistOverlay();
                        return;
                    case "coverRemove":
                        _owner.SetPlaylistCreateCover(null);
                        return;
                    case "cover":
                        _owner.ChoosePlaylistCreateCover();
                        return;
                    case "create":
                        Commit();
                        return;
                }
                if (hit.StartsWith("chip:", StringComparison.Ordinal))
                {
                    _owner._playlistCreateBucketValue = hit.Substring(5);
                    Invalidate(_card);
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e) { /* modal: the library must not scroll */ }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == Keys.Escape) { _owner.HideCreatePlaylistOverlay(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            private void Commit()
            {
                if (_name.Text.Trim().Length == 0)
                {
                    _nameError = true;
                    Invalidate(_card);
                    try { _name.Focus(); } catch { }
                    return;
                }
                _owner.CommitCreatePlaylistOverlay();
            }

            private static bool IsImagePath(string path)
                => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp";

            protected override void OnDragEnter(DragEventArgs e)
            {
                base.OnDragEnter(e);
                bool image = e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && IsImagePath(files[0]);
                e.Effect = image ? DragDropEffects.Copy : DragDropEffects.None;
                if (image && _hover != "cover") { _hover = "cover"; Invalidate(_coverTile); }
            }

            protected override void OnDragLeave(EventArgs e)
            {
                base.OnDragLeave(e);
                if (_hover == "cover") { _hover = string.Empty; Invalidate(_coverTile); }
            }

            protected override void OnDragDrop(DragEventArgs e)
            {
                base.OnDragDrop(e);
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && IsImagePath(files[0]))
                    _owner.SetPlaylistCreateCover(files[0]);
            }

            private static GraphicsPath Rounded(Rectangle r, int radius)
            {
                int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
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
                    _animation.Dispose();
                    ReleaseImages();
                }
                base.Dispose(disposing);
            }
        }
    }
}
