#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025
{
    internal sealed partial class AudioMetersLiveCharts
    {
        private readonly WorkspaceHeader _workspaceHeader = new();
        public event Action? BackRequested { add => _workspaceHeader.BackRequested += value; remove => _workspaceHeader.BackRequested -= value; }
        public void SetTrackPosition(int index, int count) { _workspaceHeader.Position = index > 0 && count >= index ? (_english ? $"Track {index} of {count}" : $"Traccia {index} di {count}") : ""; _workspaceHeader.Invalidate(); }

        private sealed class WorkspaceHeader : Control
        {
            private string _title = "", _subtitle = "", _quality = "";
            private Image? _cover;
            public string Position = "";
            public bool Lyrics, English;
            public int Section; // scheda attiva: il titolo la segue invece di restare "Panoramica"
            public event Action? BackRequested;
            public WorkspaceHeader() { DoubleBuffered = true; BackColor = MeterBack; SetStyle(ControlStyles.ResizeRedraw, true); }
            public void SetTrack(string title, string subtitle, string quality) { _title = title; _subtitle = subtitle; _quality = quality; Invalidate(); }
            public void SetArtwork(string? path)
            {
                _cover?.Dispose(); _cover = null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) { using var source = Image.FromFile(path); _cover = new Bitmap(source); }
                Invalidate();
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return; if (e.X < 40 * DeviceDpi / 96f) BackRequested?.Invoke();  }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(MeterBack); g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = DeviceDpi / 96f; int D(double n) => (int)Math.Round(n * scale);
                using var pen = new Pen(TextMain, 1.5f);
                g.DrawLines(pen, new[] { new Point(D(18), D(28)), new Point(D(11), D(36)), new Point(D(18), D(44)) });
                int titleWidth = Math.Min(D(400), Width / 3);
                using var title = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", Width < D(900) ? 20 : 25, FontStyle.Bold);
                using var normal = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10);
                using var bold = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10, FontStyle.Bold);
                using var small = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 8);
                TextRenderer.DrawText(g, Lyrics ? (English ? "Lyrics" : "Testo") : MusicAnalysisTabs.Label(Section, English), title, new Rectangle(D(40), D(12), titleWidth-D(40), D(42)), TextMain, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Lyrics ? "" : (English ? "Real-time audio analysis" : "Analisi audio in tempo reale"), normal, new Rectangle(D(42), D(58), titleWidth-D(42), D(22)), TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                var strip = new Rectangle(titleWidth+D(12), D(18), Math.Max(1, Width-titleWidth-D(12)-1), Math.Min(D(72), Height-D(26)));
                if (strip.Width < D(130) || strip.Height < 1) return;
                using var shape = RoundedRect(strip, D(10));
                using var fill = new SolidBrush(HUD.Theme.IsLight ? HUD.Theme.Panel : Color.FromArgb(11, 23, 32));
                using var border = new Pen(Color.FromArgb(65, HUD.Theme.Border)); g.FillPath(fill, shape); g.DrawPath(border, shape);
                int coverSize = Math.Min(D(48), strip.Height-D(16));
                var cover = new Rectangle(strip.Left+D(12), strip.Top+(strip.Height-coverSize)/2, coverSize, coverSize);
                if (_cover != null) g.DrawImage(_cover, cover);
                else AnalysisIconPainter.Draw(g, cover, 1, TextMuted);
                int x = cover.Right+D(12), available = strip.Right-x-D(18);
                bool wide = available > D(550);
                int textWidth = wide ? available-D(265) : available;
                TextRenderer.DrawText(g, _title, bold, new Rectangle(x, strip.Top+D(wide ? 12 : 5), textWidth, D(23)), TextMain, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                string metadata = string.Join(" · ", Array.FindAll(new[] { _subtitle, Position }, v => !string.IsNullOrEmpty(v)));
                TextRenderer.DrawText(g, metadata, normal, new Rectangle(x, strip.Top+D(wide ? 37 : 28), textWidth, D(22)), TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (wide) TextRenderer.DrawText(g, _quality, small, new Rectangle(strip.Right-D(268), strip.Top, D(250), strip.Height), TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                else TextRenderer.DrawText(g, _quality, small, new Rectangle(x, strip.Bottom-D(22), available, D(20)), TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            protected override void Dispose(bool disposing) { if (disposing) _cover?.Dispose(); base.Dispose(disposing); }
        }

        private sealed class MusicAnalysisTabs : BufferedPanel
        {
            private readonly string[] _labels;
            private readonly int[] _sections = { 0, 1, 2, 3, 5, 6, 7 };
            private readonly int _selected;
            public event Action<int>? SectionClicked;
            private static readonly int[] Sections = { 0, 1, 2, 3, 5, 6, 7 };
            private static readonly string[] En = { "Overview", "Waveform", "Spectrum", "Loudness", "Phase", "Levels", "Meters" };
            private static readonly string[] It = { "Panoramica", "Forma d'onda", "Spettro", "Loudness", "Fase", "Livelli", "Strumenti" };
            public static string Label(int section, bool english) { int i = Math.Max(0, Array.IndexOf(Sections, section)); return (english ? En : It)[i]; }
            public MusicAnalysisTabs(int section, bool english = false)
            {
                _selected = section; Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = MeterBack; Cursor = Cursors.Hand;
                _labels = english ? En : It;
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) SectionClicked?.Invoke(_sections[Math.Min(_sections.Length - 1, e.X * _sections.Length / Math.Max(1, Width))]); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new Rectangle(0, 0, Width-1, Height-1);
                if (box.Width < 1 || box.Height < 1) return;
                using var shape = RoundedRect(box, 10);
                using var fill = new SolidBrush(HUD.Theme.IsLight ? HUD.Theme.Panel : Color.FromArgb(10, 22, 31));
                using var border = new Pen(Color.FromArgb(80, HUD.Theme.Border)); g.FillPath(fill, shape); g.DrawPath(border, shape);
                using var font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f);
                int n = _sections.Length;
                for (int i = 0; i < n; i++)
                {
                    // Integer boundaries share the remainder: every section differs by at most one device pixel.
                    var tab = Rectangle.FromLTRB(i*Width/n, 1, (i+1)*Width/n, Height-2);
                    bool active = _sections[i] == _selected;
                    if (active) { using var path = RoundedRect(Rectangle.Inflate(tab,-1,0),9); using var brush = new SolidBrush(Color.FromArgb(25, HUD.Theme.Accent)); using var edge = new Pen(Color.FromArgb(100, HUD.Theme.Accent)); g.FillPath(brush,path); g.DrawPath(edge,path); }
                    if (i > 0 && !active) g.DrawLine(border, tab.Left, Height/3, tab.Left, Height*2/3);
                    int icon = Math.Max(16, (int)(22*DeviceDpi/96f));
                    int label = TextRenderer.MeasureText(_labels[i],font).Width;
                    bool text = tab.Width > icon+label+16;
                    int x = tab.Left+(tab.Width-(text ? icon+10+label : icon))/2;
                    AnalysisIconPainter.Draw(g,new Rectangle(x,(Height-icon)/2,icon,icon),_sections[i] == 7 ? 20 : _sections[i],active ? HUD.Theme.Accent : TextMuted);
                    if (text) TextRenderer.DrawText(g,_labels[i],font,new Rectangle(x+icon+10,0,label,Height),TextMain,TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
                }
            }
        }
    }
}
