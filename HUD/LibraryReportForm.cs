#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Scheda "Rapporto della libreria": in alto i numeri, sotto l'elenco di cio' che merita
    // un'occhiata, a sezioni. Un clic su una riga apre la cartella con il file selezionato.
    internal sealed class LibraryReportForm : HudModalFormBase
    {
        private const int RowHeight = 34, HeaderHeight = 44, ListTop = 176, ListLeft = 30;
        private readonly bool _english;
        private readonly IReadOnlyList<LibraryReport.Source> _sources;
        private readonly CancellationTokenSource _cts = new();
        private LibraryReport.Result? _result;
        private double _progress;
        private int _scroll, _contentHeight;
        private Point _mouse = new(-1, -1);
        private bool _closeHover;
        private readonly List<(Rectangle Bounds, string Path)> _rowHits = new();

        private Rectangle CloseBounds => new(ClientSize.Width - 30 - 32, 18, 32, 32);
        private Rectangle ListBounds => new(ListLeft, ListTop, ClientSize.Width - ListLeft * 2, ClientSize.Height - ListTop - 28);

        public LibraryReportForm(IReadOnlyList<LibraryReport.Source> sources, bool english)
        {
            _sources = sources;
            _english = english;
            Text = T("Rapporto della libreria", "Library report");
            ClientSize = new Size(900, 660);
            MinimumSize = Size;
            MaximumSize = Size;
            Shown += async (_, _) =>
            {
                LibraryReport.Result? result = null;
                try
                {
                    result = await Task.Run(() => LibraryReport.Build(_sources, _english, progress =>
                    {
                        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { _progress = progress; Invalidate(new Rectangle(0, 80, ClientSize.Width, 12)); })); } catch { }
                    }, _cts.Token));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Dbg.Warn("[REPORT] build failed: " + ex.Message); }
                if (IsDisposed) return;
                _result = result ?? new LibraryReport.Result();
                Invalidate();
            };
        }

        private string T(string italian, string english) => _english ? english : italian;

        private string SectionTitle(string key) => key switch
        {
            "duplicates" => T("Doppioni", "Duplicates"),
            "hdr" => T("HDR con metadati di luminosità da controllare", "HDR with luminance metadata to check"),
            "bitrate" => T("Bitrate basso per la risoluzione", "Low bitrate for the resolution"),
            "resolution" => T("Risoluzione sotto il 720p", "Resolution below 720p"),
            "unreadable" => T("File che non si aprono", "Files that cannot be opened"),
            _ => key
        };

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * RowHeight * 2, 0, Math.Max(0, _contentHeight - ListBounds.Height));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            bool close = CloseBounds.Contains(e.Location);
            bool overRow = _rowHits.Any(hit => hit.Bounds.Contains(e.Location));
            Cursor = close || overRow ? Cursors.Hand : Cursors.Default;
            if (close != _closeHover) { _closeHover = close; Invalidate(Rectangle.Inflate(CloseBounds, 2, 2)); }
            Invalidate(ListBounds);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = new Point(-1, -1);
            _closeHover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && CloseBounds.Contains(e.Location)) { DialogResult = DialogResult.Cancel; Close(); return; }
            if (e.Button == MouseButtons.Left)
            {
                string? path = _rowHits.FirstOrDefault(hit => hit.Bounds.Contains(e.Location)).Path;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    // ArgumentList: il percorso arriva dai nomi dei file dell'utente e non deve
                    // poter chiudere l'argomento e aggiungerne altri.
                    try
                    {
                        var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                        explorer.ArgumentList.Add("/select," + path);
                        Process.Start(explorer)?.Dispose();
                    }
                    catch { }
                    return;
                }
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
            using var labelFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 7.4f, FontStyle.Regular, GraphicsUnit.Point);
            using var valueFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 16f, FontStyle.Regular, GraphicsUnit.Point);
            using var sectionFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            using var smallFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 8.6f, FontStyle.Regular, GraphicsUnit.Point);

            int width = ClientSize.Width;
            TextRenderer.DrawText(g, T("Rapporto della libreria", "Library report"), titleFont, new Rectangle(30, 14, width - 110, 42), Theme.Text, Line);
            Theme.DrawCloseButton(g, CloseBounds, _closeHover);
            _rowHits.Clear();

            var result = _result;
            if (result == null)
            {
                TextRenderer.DrawText(g, string.Format(T("Leggo {0} file…", "Reading {0} files…"), _sources.Count), bodyFont, new Rectangle(30, 54, width - 60, 22), Theme.Muted, Line);
                var track = new Rectangle(30, 84, width - 60, 2);
                using (var back = new SolidBrush(Color.FromArgb(Theme.IsLight ? 40 : 34, Theme.Text))) g.FillRectangle(back, track);
                using (var fill = new SolidBrush(Theme.Accent)) g.FillRectangle(fill, track.Left, track.Top, (int)(track.Width * Math.Clamp(_progress, 0, 1)), track.Height);
                return;
            }

            int findings = result.Sections.Sum(section => section.Findings.Count);
            TextRenderer.DrawText(g, findings == 0
                    ? T("Film e serie sul PC: niente da segnalare.", "Movies and series on this PC: nothing to report.")
                    : string.Format(T("Film e serie sul PC, {0} cose da guardare. Un clic su una riga apre la cartella del file.", "Movies and series on this PC, {0} things to look at. Click a row to open the file's folder."), findings),
                bodyFont, new Rectangle(30, 54, width - 60, 22), Theme.Muted, Line);

            var stats = new (string Label, string Value)[]
            {
                (T("Titoli", "Titles"), result.Titles.ToString("N0", CultureInfo.CurrentCulture)),
                (T("Spazio", "Size"), LibraryReport.Size(result.Bytes)),
                ("4K", result.Uhd.ToString("N0", CultureInfo.CurrentCulture)),
                ("1080p", result.FullHd.ToString("N0", CultureInfo.CurrentCulture)),
                (T("720p e meno", "720p and below"), (result.Hd + result.Sd).ToString("N0", CultureInfo.CurrentCulture)),
                ("HDR", result.Hdr.ToString("N0", CultureInfo.CurrentCulture)),
                ("Dolby Vision", result.DolbyVision.ToString("N0", CultureInfo.CurrentCulture))
            };
            int column = (width - 60) / stats.Length;
            // I numeri in alto: una riga sola, separati da barre verticali sottili.
            using var bar = new Pen(Color.FromArgb(Theme.IsLight ? 34 : 24, Theme.Text));
            using var statFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            for (int i = 0; i < stats.Length; i++)
            {
                int left = 30 + i * column + (i > 0 ? 18 : 0), room = column - 22 - (i > 0 ? 18 : 0);
                if (i > 0) g.DrawLine(bar, 30 + i * column, 100, 30 + i * column, 144);
                TextRenderer.DrawText(g, stats[i].Label, statFont, new Rectangle(left, 96, room, 18), Theme.Muted, Line);
                TextRenderer.DrawText(g, stats[i].Value, valueFont, new Rectangle(left, 114, room, 32), Theme.Text, Line);
            }

            Rectangle list = ListBounds;
            var state = g.Save();
            g.SetClip(list);
            const TextFormatFlags Clipped = Line | TextFormatFlags.PreserveGraphicsClipping;
            using var rule = new Pen(Color.FromArgb(Theme.IsLight ? 34 : 24, Theme.Text));
            int y = list.Top - _scroll;
            foreach (var section in result.Sections)
            {
                if (y + HeaderHeight > list.Top && y < list.Bottom)
                {
                    string heading = SectionTitle(section.Key);
                    int headingWidth = TextRenderer.MeasureText(g, heading, sectionFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, heading, sectionFont, new Rectangle(list.Left, y + 14, headingWidth + 4, 22), Theme.Text, Clipped);
                    TextRenderer.DrawText(g, section.Findings.Count.ToString(CultureInfo.CurrentCulture), sectionFont, new Rectangle(list.Left + headingWidth + 10, y + 14, 60, 22), Theme.Muted, Clipped);
                }
                y += HeaderHeight;
                foreach (var finding in section.Findings)
                {
                    if (y + RowHeight > list.Top && y < list.Bottom)
                    {
                        var row = new Rectangle(list.Left, y, list.Width - 14, RowHeight);
                        // Una riga sola per titolo, senza fili orizzontali: il titolo a sinistra, una barra
                        // verticale, poi cosa c'e' da guardare.
                        bool hot = row.Contains(_mouse) && list.Contains(_mouse);
                        int nameWidth = Math.Min(300, row.Width * 2 / 5);
                        TextRenderer.DrawText(g, finding.Title, nameFont, new Rectangle(row.Left, row.Top, nameWidth - 16, row.Height), hot ? Theme.Accent : Theme.Text, Clipped);
                        g.DrawLine(rule, row.Left + nameWidth, row.Top + 9, row.Left + nameWidth, row.Bottom - 9);
                        TextRenderer.DrawText(g, finding.Detail, smallFont, new Rectangle(row.Left + nameWidth + 16, row.Top, row.Width - nameWidth - 16, row.Height), Theme.SubtleText, Clipped);
                        _rowHits.Add((Rectangle.Intersect(row, list), finding.Path));
                    }
                    y += RowHeight;
                }
                y += 10;
            }
            g.Restore(state);
            _contentHeight = y + _scroll - list.Top;
            if (_contentHeight > list.Height)
            {
                var trackBounds = new Rectangle(list.Right - 3, list.Top, 3, list.Height);
                int thumb = Math.Max(28, (int)(trackBounds.Height * (list.Height / (double)_contentHeight)));
                int top = trackBounds.Top + (int)((trackBounds.Height - thumb) * (_scroll / (double)Math.Max(1, _contentHeight - list.Height)));
                ScrollbarChrome.Draw(g, trackBounds, top, thumb);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _cts.Cancel(); } catch { }
                _cts.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
