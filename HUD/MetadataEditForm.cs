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
    /// <summary>
    /// Correzione a mano di un film: titolo, anno e copertina. Si scrive il titolo giusto, "Cerca"
    /// propone i film che TMDb conosce con quel nome; scelto il film compaiono le sue copertine.
    /// In alternativa la copertina puo' essere un'immagine presa dal computer.
    /// </summary>
    internal sealed class MetadataEditForm : HudModalFormBase
    {
        private readonly bool _english;
        private readonly string _path;
        private readonly TextBox _title, _year;
        private readonly Button _save;
        private readonly CancellationTokenSource _lifetime = new();
        private List<MovieMetadataService.TmdbMatch> _matches = new();
        private int _match = -1, _poster = -1, _hoverMatch = -1, _hoverPoster = -1;
        private List<string> _posterPaths = new();
        private readonly Dictionary<string, Bitmap> _thumbs = new();
        private string? _localPoster;
        private string _status = "";
        private bool _busy;
        private string _hoverLink = "";

        private static readonly Rectangle TitleField = new(30, 92, 520, 38), YearField = new(566, 92, 164, 38);
        private const int MatchTop = 186, MatchHeight = 28, GridTop = 348, ThumbW = 92, ThumbH = 138, ThumbGap = 9;
        private Rectangle SearchLink => new(30, 144, 150, 26);
        private Rectangle FileLink => new(30, ClientSize.Height - 52, 210, 26);
        private Rectangle ResetLink => new(250, ClientSize.Height - 52, 110, 26);

        /// <summary>Vero se qualcosa e' stato salvato o ripristinato: la libreria va riletta.</summary>
        public bool Changed { get; private set; }

        public MetadataEditForm(string path, string title, int? year, bool english)
        {
            _english = english;
            _path = path;
            Text = T("Correggi titolo e copertina", "Fix title and cover");
            ClientSize = new Size(760, 716);
            MinimumSize = MaximumSize = Size;

            _title = Field(TitleField, title);
            _year = Field(YearField, year?.ToString() ?? "");
            _year.MaxLength = 4;
            _year.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            _title.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Search(); } };
            _year.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Search(); } };

            Button cancel = CreateModalButton(T("Annulla", "Cancel"), DialogResult.Cancel, primary: false);
            _save = CreateModalButton(T("Salva", "Save"), DialogResult.None, primary: true);
            _save.SetBounds(ClientSize.Width - 30 - 110, ClientSize.Height - 58, 110, 38);
            cancel.SetBounds(_save.Left - 10 - 100, ClientSize.Height - 58, 100, 38);
            _save.Click += (_, _) => Save();
            Controls.Add(cancel);
            Controls.Add(_save);
            CancelButton = cancel;
            Shown += (_, _) => { try { _title.Focus(); _title.SelectionStart = _title.TextLength; _title.SelectionLength = 0; } catch { } Search(); };
        }

        private string T(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);

        private TextBox Field(Rectangle bounds, string text)
        {
            var shell = new Panel { BackColor = Theme.SheetRaised, Location = new Point(bounds.Left + 12, bounds.Top + 1), Size = new Size(bounds.Width - 24, bounds.Height - 2), Padding = new Padding(0, 8, 0, 6) };
            var box = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = shell.BackColor, ForeColor = Theme.Text, Dock = DockStyle.Fill, Text = text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point)
            };
            box.GotFocus += (_, _) => Invalidate();
            box.LostFocus += (_, _) => Invalidate();
            shell.Controls.Add(box);
            Controls.Add(shell);
            return box;
        }

        private int? YearValue => int.TryParse(_year.Text.Trim(), out int year) && year is > 1870 and < 2200 ? year : null;

        private void Say(string text) { _status = text; Invalidate(); }

        private async void Search()
        {
            string title = _title.Text.Trim();
            if (title.Length == 0 || _busy) return;
            _busy = true;
            Say(T("Cerco su TMDb…", "Searching TMDb…"));
            try
            {
                var token = _lifetime.Token;
                int? year = YearValue;
                string language = _english ? "en" : "it";
                var matches = await Task.Run(() =>
                {
                    var found = MovieMetadataService.SearchTmdbMovies(title, year, language, token);
                    // Con l'anno sbagliato TMDb non trova nulla: si riprova senza.
                    return found.Count == 0 && year.HasValue ? MovieMetadataService.SearchTmdbMovies(title, null, language, token) : found;
                }, token);
                if (IsDisposed) return;
                _matches = matches;
                _match = -1; _poster = -1; _posterPaths = new List<string>();
                Say(matches.Count == 0 ? T("Nessun film con questo titolo", "No film with this title") : "");
                if (matches.Count > 0) SelectMatch(0, fillFields: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) Say(ex.Message); }
            finally { _busy = false; }
        }

        private async void SelectMatch(int index, bool fillFields)
        {
            if (index < 0 || index >= _matches.Count) return;
            _match = index; _poster = -1; _localPoster = null;
            var match = _matches[index];
            if (fillFields) { _title.Text = match.Title; _year.Text = match.Year?.ToString() ?? ""; }
            _posterPaths = new List<string>();
            Invalidate();
            try
            {
                var token = _lifetime.Token;
                string language = _english ? "en" : "it";
                var posters = await Task.Run(() => MovieMetadataService.ListTmdbPosters(match.Id, language, token), token);
                if (IsDisposed || _match != index) return;
                _posterPaths = posters;
                Invalidate();
                string folder = Path.Combine(Path.GetTempPath(), "cinecore-covers");
                foreach (string poster in posters)
                {
                    if (_thumbs.ContainsKey(poster)) continue;
                    string file = Path.Combine(folder, poster.Trim('/').Replace('/', '_'));
                    bool ok = await Task.Run(() => MovieMetadataService.DownloadTmdbImage(poster, "w185", file, token), token);
                    if (IsDisposed || _match != index) return;
                    if (!ok) continue;
                    try { using var loaded = Image.FromFile(file); _thumbs[poster] = new Bitmap(loaded); } catch { }
                    Invalidate(new Rectangle(0, GridTop - 4, ClientSize.Width, ThumbH * 2 + ThumbGap + 8));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) Say(ex.Message); }
        }

        private Rectangle MatchBounds(int index) => new(30, MatchTop + index * MatchHeight, ClientSize.Width - 60, MatchHeight);
        private Rectangle ThumbBounds(int index) => new(30 + index % 7 * (ThumbW + ThumbGap), GridTop + index / 7 * (ThumbH + ThumbGap), ThumbW, ThumbH);

        private async void Save()
        {
            if (_busy) return;
            string title = _title.Text.Trim();
            if (title.Length == 0) { Say(T("Scrivi il titolo", "Enter the title")); return; }
            _busy = true; _save.Enabled = false;
            try
            {
                var token = _lifetime.Token;
                MovieMetadataService.SetManualTitle(_path, title, YearValue);
                Changed = true;
                if (_localPoster != null)
                {
                    if (!MovieMetadataService.SetManualPoster(_path, _localPoster)) { Say(T("Immagine non utilizzabile", "This image cannot be used")); return; }
                }
                else if (_poster >= 0 && _poster < _posterPaths.Count)
                {
                    Say(T("Scarico la copertina…", "Downloading the cover…"));
                    string chosen = _posterPaths[_poster];
                    string file = Path.Combine(Path.GetTempPath(), "cinecore-covers", "full_" + chosen.Trim('/').Replace('/', '_'));
                    bool ok = await Task.Run(() => MovieMetadataService.DownloadTmdbImage(chosen, "w780", file, token), token);
                    if (IsDisposed) return;
                    if (!ok || !MovieMetadataService.SetManualPoster(_path, file)) { Say(T("Copertina non scaricata", "The cover could not be downloaded")); return; }
                }
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) Say(ex.Message); }
            finally { _busy = false; if (!IsDisposed) _save.Enabled = true; }
        }

        private void PickFile()
        {
            using var picker = new OpenFileDialog { Title = T("Scegli la copertina", "Choose the cover"), Filter = T("Immagini", "Images") + "|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            _localPoster = picker.FileName;
            _poster = -1;
            Say(T("Copertina: ", "Cover: ") + Path.GetFileName(_localPoster));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int match = -1, poster = -1;
            for (int i = 0; i < Math.Min(_matches.Count, 5); i++) if (MatchBounds(i).Contains(e.Location)) match = i;
            for (int i = 0; i < Math.Min(_posterPaths.Count, 14); i++) if (ThumbBounds(i).Contains(e.Location)) poster = i;
            string link = SearchLink.Contains(e.Location) ? "search" : FileLink.Contains(e.Location) ? "file"
                : ResetLink.Contains(e.Location) && MovieMetadataService.HasManualCorrection(_path) ? "reset" : "";
            Cursor = match >= 0 || poster >= 0 || link.Length > 0 ? Cursors.Hand : Cursors.Default;
            if (match != _hoverMatch || poster != _hoverPoster || link != _hoverLink) { _hoverMatch = match; _hoverPoster = poster; _hoverLink = link; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (SearchLink.Contains(e.Location)) { Search(); return; }
                if (FileLink.Contains(e.Location)) { PickFile(); return; }
                if (ResetLink.Contains(e.Location) && MovieMetadataService.HasManualCorrection(_path))
                {
                    MovieMetadataService.ClearManual(_path);
                    Changed = true;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                for (int i = 0; i < Math.Min(_matches.Count, 5); i++)
                    if (MatchBounds(i).Contains(e.Location)) { SelectMatch(i, fillFields: true); return; }
                for (int i = 0; i < Math.Min(_posterPaths.Count, 14); i++)
                    if (ThumbBounds(i).Contains(e.Location)) { _poster = i; _localPoster = null; Say(""); return; }
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var rowFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            using var linkFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

            TextRenderer.DrawText(g, Text, titleFont, new Rectangle(30, 14, ClientSize.Width - 60, 42), Theme.Text, Line);
            TextRenderer.DrawText(g, T("Titolo", "Title"), bodyFont, new Rectangle(TitleField.Left, 66, 200, 20), Theme.Muted, Line);
            TextRenderer.DrawText(g, T("Anno", "Year"), bodyFont, new Rectangle(YearField.Left, 66, 120, 20), Theme.Muted, Line);
            foreach (var (bounds, box) in new[] { (TitleField, _title), (YearField, _year) })
                using (var shape = Rounded(bounds, 8))
                using (var fill = new SolidBrush(Theme.SheetRaised))
                {
                    g.FillPath(fill, shape);
                    if (box.Focused) { using var ring = new Pen(Color.FromArgb(150, Theme.Accent)); g.DrawPath(ring, shape); }
                }

            Color Link(string key) => _hoverLink == key ? ControlPaint.Light(Theme.Accent, .45f) : Theme.Accent;
            TextRenderer.DrawText(g, T("Cerca su TMDb", "Search TMDb"), linkFont, SearchLink, Link("search"), Line);
            if (_status.Length > 0)
                TextRenderer.DrawText(g, _status, bodyFont, new Rectangle(SearchLink.Right + 10, SearchLink.Top, ClientSize.Width - SearchLink.Right - 40, SearchLink.Height), Theme.SubtleText, Line);

            // I film trovati: un clic sceglie quello giusto e ne mostra le copertine.
            for (int i = 0; i < Math.Min(_matches.Count, 5); i++)
            {
                var row = MatchBounds(i);
                var match = _matches[i];
                bool chosen = i == _match;
                if (chosen)
                {
                    using var mark = new SolidBrush(Theme.Accent);
                    using var markPath = Rounded(new Rectangle(row.Left, row.Top + 7, 3, row.Height - 14), 2);
                    g.FillPath(mark, markPath);
                }
                TextRenderer.DrawText(g, match.Title, rowFont, new Rectangle(row.Left + 14, row.Top, row.Width - 90, row.Height),
                    chosen ? Theme.Text : i == _hoverMatch ? Theme.Text : Theme.SubtleText, Line);
                if (match.Year.HasValue)
                    TextRenderer.DrawText(g, match.Year.Value.ToString(), rowFont, new Rectangle(row.Right - 70, row.Top, 70, row.Height), Theme.Muted, Line | TextFormatFlags.Right);
            }

            // Le copertine del film scelto.
            for (int i = 0; i < Math.Min(_posterPaths.Count, 14); i++)
            {
                var cell = ThumbBounds(i);
                using var shape = Rounded(cell, 7);
                using (var back = new SolidBrush(Theme.SheetRaised)) g.FillPath(back, shape);
                if (_thumbs.TryGetValue(_posterPaths[i], out Bitmap? thumb))
                {
                    var state = g.Save();
                    g.SetClip(shape);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(thumb, cell);
                    g.Restore(state);
                }
                if (i == _poster) { using var ring = new Pen(Theme.Accent, 3f); g.DrawPath(ring, shape); }
                else if (i == _hoverPoster) { using var ring = new Pen(Color.FromArgb(150, Theme.Text), 1.5f); g.DrawPath(ring, shape); }
            }
            if (_match >= 0 && _posterPaths.Count == 0 && _status.Length == 0)
                TextRenderer.DrawText(g, T("Cerco le copertine…", "Looking for covers…"), bodyFont, new Rectangle(30, GridTop, 400, 24), Theme.Muted, Line);

            TextRenderer.DrawText(g, T("Immagine dal computer…", "Image from the computer…"), linkFont, FileLink, Link("file"), Line);
            if (MovieMetadataService.HasManualCorrection(_path))
                TextRenderer.DrawText(g, T("Ripristina", "Restore"), linkFont, ResetLink, Link("reset"), Line);
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
                try { _lifetime.Cancel(); } catch { }
                _lifetime.Dispose();
                foreach (var thumb in _thumbs.Values) try { thumb.Dispose(); } catch { }
                _thumbs.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
