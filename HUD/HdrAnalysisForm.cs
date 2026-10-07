#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Scheda "Analisi HDR": cosa dichiara il file e cosa contiene davvero.
    // - andamento nel film: picco e media di ogni fotogramma campionato, in nit su scala PQ
    //   (la scala con cui l'HDR e' codificato: passi uguali = differenze uguali per l'occhio);
    // - distribuzione: quanta parte dell'immagine cade in ogni fascia di luminosita';
    // - colori: quanta parte sta in Rec.709, quanta usa P3, quanta arriva a BT.2020.
    internal sealed partial class HdrAnalysisForm : HudModalFormBase
    {
        private const int SampleCount = 480;
        private readonly string _path, _title;
        private readonly bool _english;
        private readonly Func<double>? _position;
        private readonly Action<double>? _seek;
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = 500 };
        private HdrAnalyzer.Result? _result;
        private double _progress;
        private bool _running;
        private Point _mouse = new(-1, -1);
        private bool _closeHover;
        // In tempo reale: il fotogramma nel punto in riproduzione, misurato mentre il film va
        // (anche prima che l'analisi dell'intero film sia finita). Vedi HdrAnalysisForm.Live.cs.
        private HdrAnalyzer.LiveSession? _live;
        private HdrAnalyzer.FrameStats? _liveStats;

        // Curve: picco (ambra) e media (blu), verificate per daltonismo sui due fondi.
        private static Color PeakColor => Theme.IsLight ? Color.FromArgb(189, 103, 42) : Color.FromArgb(196, 126, 46);
        private static Color AverageColor => Theme.IsLight ? Color.FromArgb(28, 95, 186) : Color.FromArgb(50, 128, 235);

        private Rectangle CloseBounds => new(ClientSize.Width - 30 - 32, 18, 32, 32);
        private Rectangle TimePlot => new(78, 322, ClientSize.Width - 78 - 34, 296);
        private Rectangle HistogramPlot => new(78, 690, ClientSize.Width - 78 - 34, 140);

        // Niente da misurare (video non HDR, Dolby Vision profilo 5, file illeggibile): la scheda grande
        // restava vuota con una riga al centro. Si riduce a una scheda bassa con il messaggio a sinistra.
        private void FitToResult()
        {
            if (_result is not { } result || (result.IsHdr && result.Error == null && !result.BaseLayerNotMeasurable)) return;
            var compact = new Size(640, 164);
            if (ClientSize == compact) return;
            var before = Size;
            MinimumSize = MaximumSize = Size.Empty;
            ClientSize = compact;
            MinimumSize = MaximumSize = Size;
            if (IsHandleCreated) Location = new Point(Left + (before.Width - Width) / 2, Top + (before.Height - Height) / 2);
        }

        public HdrAnalysisForm(string path, string title, bool english, Func<double>? position = null, Action<double>? seek = null)
        {
            _path = path;
            _title = title;
            _english = english;
            _position = position;
            _seek = seek;
            Text = T("Analisi HDR", "HDR analysis");
            ClientSize = new Size(1440, 950);
            MinimumSize = Size;
            MaximumSize = Size;
            _positionTimer.Tick += (_, _) => { if (_position != null && _result?.Samples.Count > 0) Invalidate(Rectangle.Inflate(TimePlot, 4, 4)); };
            Shown += (_, _) => Start();
        }

        private string T(string italian, string english) => _english ? english : italian;

        private async void Start()
        {
            if (_position != null) StartLive();
            var cached = await Task.Run(() => HdrAnalyzer.TryLoadCached(_path, SampleCount));
            if (IsDisposed) return;
            if (cached != null) { _result = cached; _progress = 1; FitToResult(); Invalidate(); if (_position != null) _positionTimer.Start(); return; }

            _running = true;
            Invalidate();
            HdrAnalyzer.Result? final = null;
            try
            {
                final = await Task.Run(() => HdrAnalyzer.Analyze(_path, SampleCount, (progress, partial) =>
                {
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new Action(() => { if (IsDisposed) return; _progress = progress; _result = partial; FitToResult(); Invalidate(); }));
                    }
                    catch { }
                }, _cts.Token));
            }
            catch (OperationCanceledException) { return; }
            catch { }
            if (IsDisposed) return;
            _running = false;
            _progress = 1;
            if (final != null) { _result = final; FitToResult(); }
            else _result ??= new HdrAnalyzer.Result { Error = "failed" };
            Invalidate();
            if (_position != null) _positionTimer.Start();
        }

        // ===== Scala =====
        private static readonly double[] AxisTops = { 100, 200, 400, 1000, 2000, 4000, 10000 };

        private double AxisTopNits()
        {
            double need = Math.Max(_result?.MeasuredPeakNits ?? 0, _result?.DeclaredMaxCll ?? 0) * 1.04;
            return AxisTops.FirstOrDefault(top => top >= need, 10000);
        }

        private static int YOf(Rectangle plot, double nits, double topPq) =>
            plot.Bottom - (int)Math.Round(Math.Clamp(HdrAnalyzer.NitsToPq(nits) / topPq, 0, 1) * plot.Height);

        private string Nits(double value) =>
            value >= 100 ? value.ToString("N0", CultureInfo.CurrentCulture)
            : value >= 10 ? value.ToString("0", CultureInfo.CurrentCulture)
            : value >= 1 ? value.ToString("0.#", CultureInfo.CurrentCulture)
            : value.ToString("0.###", CultureInfo.CurrentCulture);

        private static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private string Share(double value) =>
            value <= 0 ? "0%" : value < 0.0005 ? "<0,1%".Replace(',', CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0])
            : (value * 100).ToString(value < 0.1 ? "0.#" : "0", CultureInfo.CurrentCulture) + "%";

        // ===== Mouse =====
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            bool close = CloseBounds.Contains(e.Location);
            bool overTabs = (TabsVisible && TabsBounds.Contains(e.Location)) || (CanExport && ExportBounds.Contains(e.Location));
            bool overExport = CanExport && ExportBounds.Contains(e.Location);
            if (overExport != _exportHover) { _exportHover = overExport; Invalidate(Rectangle.Inflate(ExportBounds, 2, 2)); }
            bool overTime = !_liveView && TimePlot.Contains(e.Location) && _result?.Samples.Count > 0;
            Cursor = close || overTabs || (overTime && _seek != null) ? Cursors.Hand : Cursors.Default;
            if (close != _closeHover) { _closeHover = close; Invalidate(Rectangle.Inflate(CloseBounds, 2, 2)); }
            if (TabsVisible) Invalidate(Rectangle.Inflate(TabsBounds, 2, 2));
            if (_liveView) return;
            Invalidate(Rectangle.Inflate(TimePlot, 6, 26));
            Invalidate(Rectangle.Inflate(HistogramPlot, 6, 26));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = new Point(-1, -1);
            _closeHover = false;
            _exportHover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && CloseBounds.Contains(e.Location)) { DialogResult = DialogResult.Cancel; Close(); return; }
            if (e.Button == MouseButtons.Left && CanExport && ExportBounds.Contains(e.Location)) { ExportResults(); return; }
            if (e.Button == MouseButtons.Left && TabsVisible && TabsBounds.Contains(e.Location))
            {
                bool live = e.X >= TabsBounds.Left + TabsBounds.Width / 2;
                if (live != _liveView) { _liveView = live; _liveVersion = -1; _liveShownTick = 0; Invalidate(); }
                return;
            }
            if (e.Button == MouseButtons.Left && !_liveView && _seek != null && TimePlot.Contains(e.Location) && _result is { Duration: > 0 } result && result.Samples.Count > 0)
            {
                // Clic sul grafico: si salta a quel punto del film.
                double seconds = (e.X - TimePlot.Left) / (double)TimePlot.Width * result.Duration;
                try { _seek(Math.Clamp(seconds, 0, result.Duration)); } catch { }
                Invalidate(Rectangle.Inflate(TimePlot, 4, 4));
                return;
            }
            base.OnMouseDown(e);
        }

        // ===== Disegno =====
        private const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var sectionFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var labelFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 7.4f, FontStyle.Regular, GraphicsUnit.Point);
            using var valueFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 16f, FontStyle.Regular, GraphicsUnit.Point);
            using var smallFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 8.2f, FontStyle.Regular, GraphicsUnit.Point);

            int width = ClientSize.Width;
            TextRenderer.DrawText(g, T("Analisi HDR", "HDR analysis"), titleFont, new Rectangle(30, 14, 300, 42), Theme.Text, Line);
            if (!_exporting)
            {
                // Nell'immagine esportata non servono i pulsanti.
                Theme.DrawCloseButton(g, CloseBounds, _closeHover);
                DrawTabs(g, bodyFont);
                DrawExportButton(g, bodyFont);
                DrawExportNote(g, smallFont);
            }

            var result = _result;
            string format = result == null ? T("Leggo il file…", "Reading the file…") : FormatLine(result);
            TextRenderer.DrawText(g, _title + (format.Length > 0 ? "      " + format : ""), bodyFont, new Rectangle(30, 54, width - 60, 22), Theme.Muted, Line);

            if (_running)
            {
                // Avanzamento: un filo sotto l'intestazione.
                var track = new Rectangle(30, 84, width - 60, 2);
                using (var back = new SolidBrush(Color.FromArgb(Theme.IsLight ? 40 : 34, Theme.Text))) g.FillRectangle(back, track);
                using (var fill = new SolidBrush(Theme.Accent)) g.FillRectangle(fill, track.Left, track.Top, (int)(track.Width * Math.Clamp(_progress, 0, 1)), track.Height);
            }

            if (result == null) return;
            if (!result.IsHdr || result.Error != null || result.BaseLayerNotMeasurable)
            {
                string message = !result.IsHdr ? T("Questo video non è HDR: non c'è una curva di luminosità da misurare.", "This video is not HDR: there is no luminance curve to measure.")
                    : result.BaseLayerNotMeasurable ? T("Dolby Vision profilo 5: l'immagine di base non è HDR10 e i nit non si possono misurare da qui.", "Dolby Vision profile 5: the base image is not HDR10 and nits cannot be measured from here.")
                    : T("Non sono riuscito a leggere i fotogrammi di questo file.", "The frames of this file could not be read.");
                TextRenderer.DrawText(g, message, bodyFont, new Rectangle(30, 96, width - 60, 44), Theme.SubtleText,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return;
            }

            if (_liveView)
            {
                DrawLiveView(g, sectionFont, labelFont, valueFont, smallFont, bodyFont);
                return;
            }

            DrawStats(g, result, labelFont, valueFont, smallFont);
            if (result.Samples.Count == 0) return;
            DrawTimeChart(g, result, sectionFont, smallFont);
            DrawHistogram(g, result, sectionFont, smallFont);
            DrawGamut(g, result, labelFont, smallFont);

            string footer = _running
                ? string.Format(T("Analisi in corso… {0}%", "Analysing… {0}%"), (int)Math.Round(_progress * 100))
                : string.Format(T("Stima su {0} fotogrammi campionati", "Estimate from {0} sampled frames"), result.Samples.Count);
            TextRenderer.DrawText(g, footer, smallFont, new Rectangle(width - 30 - 320, ClientSize.Height - 38, 320, 20), Theme.Muted, Line | TextFormatFlags.Right);
        }

        private string FormatLine(HdrAnalyzer.Result result)
        {
            var parts = new List<string>();
            if (!result.IsHdr) parts.Add("SDR");
            else parts.Add(result.Transfer == "HLG" ? "HLG" : "HDR10 (PQ)");
            if (result.DolbyVisionProfile is int profile)
                parts.Add("Dolby Vision " + T("profilo ", "profile ") + profile + (result.DolbyVisionCompatibility is int compat and > 0 ? "." + compat : ""));
            if (result.Hdr10Plus) parts.Add("HDR10+");
            if (result.Primaries.Length > 0) parts.Add(result.Primaries);
            if (result.BitDepth > 0) parts.Add(result.BitDepth + " bit");
            if (result.Width > 0) parts.Add(result.Width + "×" + result.Height);
            return string.Join("     ", parts);
        }

        private void DrawStats(Graphics g, HdrAnalyzer.Result result, Font labelFont, Font valueFont, Font smallFont)
        {
            string none = "—";
            var stats = new List<(string Label, string Value, string Caption)>
            {
                (T("Picco misurato", "Measured peak"), result.Samples.Count > 0 ? Nits(result.MeasuredPeakNits) + " nit" : none,
                    result.Samples.Count > 0 ? T("a ", "at ") + Clock(result.MeasuredPeakTime) + "     " + T("tipico ", "typical ") + Nits(result.MedianPeakNits) + " nit" : ""),
                (T("MaxCLL dichiarato", "Declared MaxCLL"), result.DeclaredMaxCll is int cll ? Nits(cll) + " nit" : none,
                    result.DeclaredMaxCll == null ? T("non presente nel file", "not in the file") : ""),
                (T("Media più alta", "Highest average"), result.Samples.Count > 0 ? Nits(result.MeasuredMaxAverageNits) + " nit" : none,
                    result.DeclaredMaxFall is int fall ? T("MaxFALL dichiarato ", "declared MaxFALL ") + Nits(fall) + " nit" : T("MaxFALL non dichiarato", "MaxFALL not declared")),
                (T("Schermo di mastering", "Mastering display"), result.MasteringMaxNits is double max ? Nits(max) + " nit" : none,
                    result.MasteringMinNits is double min && result.MasteringMaxNits != null ? T("nero ", "black ") + min.ToString("0.####", CultureInfo.CurrentCulture) + " nit" : T("non dichiarato", "not declared"))
            };
            // Film in riproduzione: prima colonna, il fotogramma di questo momento.
            if (_liveStats is { } live)
                stats.Insert(0, (T("Picco ora", "Peak now"), Nits(live.PeakNits) + " nit",
                    T("media ", "average ") + Nits(live.AverageNits) + " nit     " + Clock(live.Time)));
            // Prima fascia: i numeri che contano, grandi, uno per colonna. Niente maiuscole ne' fili:
            // l'ordine lo da' la griglia (stesse colonne della fascia sotto).
            using var rule = new Pen(Color.FromArgb(Theme.IsLight ? 34 : 24, Theme.Text));
            using var bigFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 21f, FontStyle.Regular, GraphicsUnit.Point);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var figureFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
            // Nessun riquadro: i valori stanno sul fondo della scheda, separati da barre verticali sottili
            // (come nelle altre pagine); i grafici sotto sono liberi.
            var card = new Rectangle(30, 96, ClientSize.Width - 60, 178);
            const int inset = 0;
            int columnWidth = (card.Width - inset * 2) / stats.Count;
            for (int i = 0; i < stats.Count; i++)
            {
                int x = card.Left + inset + i * columnWidth + (i > 0 ? 22 : 0), w = columnWidth - 28 - (i > 0 ? 22 : 0);
                if (i > 0) g.DrawLine(rule, card.Left + inset + i * columnWidth, 114, card.Left + inset + i * columnWidth, 192);
                TextRenderer.DrawText(g, stats[i].Label, nameFont, new Rectangle(x, 112, w, 18), Theme.Muted, Line);
                TextRenderer.DrawText(g, stats[i].Value, bigFont, new Rectangle(x, 132, w, 42), Theme.Text, Line);
                TextRenderer.DrawText(g, stats[i].Caption, smallFont, new Rectangle(x, 176, w, 18), Theme.Muted, Line);
            }

            // Seconda riga: le altre misure dell'intero film, in piccolo.
            if (result.Samples.Count > 0)
            {
                // Seconda fascia, sotto un filo: le misure di contorno in una griglia regolare (etichetta
                // sopra, valore sotto) invece di una riga unica di testo separata da punti.
                var more = new List<(string Label, string Value)>
                {
                    (T("Massimo assoluto", "Absolute maximum"), Nits(result.MeasuredMaxNits) + " nit"),
                    (T("99° percentile", "99th percentile"), Nits(result.P99Nits) + " nit"),
                    (T("Mediana", "Median"), Nits(result.MedianNits) + " nit"),
                    (T("Nero", "Black"), Black(result.BlackNits) + " nit"),
                    (T("Luminanza media", "Mean luminance"), Nits(result.LuminanceAverageNits) + " nit"),
                    (T("Gamma dinamica", "Dynamic range"), Math.Log2(Math.Max(1e-6, result.MeasuredPeakNits) / Math.Max(BlackFloor, result.BlackNits)).ToString("0.0", CultureInfo.CurrentCulture) + " stop")
                };
                if (result.ActiveWidth > 0 && result.ActiveHeight > 0)
                    more.Add((T("Area attiva", "Active area"), result.ActiveWidth + "×" + result.ActiveHeight + "     " + (result.ActiveWidth / (double)result.ActiveHeight).ToString("0.00", CultureInfo.CurrentCulture) + ":1"));
                int cell = (card.Width - inset * 2) / more.Count;
                for (int i = 0; i < more.Count; i++)
                {
                    int left = card.Left + inset + i * cell + (i > 0 ? 16 : 0), width = cell - 16 - (i > 0 ? 16 : 0);
                    if (i > 0) g.DrawLine(rule, card.Left + inset + i * cell, 220, card.Left + inset + i * cell, 254);
                    TextRenderer.DrawText(g, more[i].Label, smallFont, new Rectangle(left, 218, width, 16), Theme.Muted, Line);
                    TextRenderer.DrawText(g, more[i].Value, figureFont, new Rectangle(left, 236, width, 20), Theme.SubtleText, Line);
                }
            }
        }

        private static readonly double[] NitTicks = { 1, 10, 100, 1000, 10000 };

        private void DrawTimeChart(Graphics g, HdrAnalyzer.Result result, Font sectionFont, Font smallFont)
        {
            Rectangle plot = TimePlot;
            TextRenderer.DrawText(g, T("Luminosità lungo il film", "Luminance through the film"), sectionFont, new Rectangle(30, plot.Top - 32, 320, 22), Theme.Text, Line);

            // Legenda (due serie): un tratto e il nome, accanto al titolo.
            int legendX = plot.Right;
            foreach (var (name, color) in new[] { (T("Media del fotogramma", "Frame average"), AverageColor), (T("Picco del fotogramma", "Frame peak"), PeakColor) })
            {
                int textWidth = TextRenderer.MeasureText(g, name, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                legendX -= textWidth;
                TextRenderer.DrawText(g, name, smallFont, new Rectangle(legendX, plot.Top - 30, textWidth + 2, 18), Theme.SubtleText, Line);
                legendX -= 22;
                using (var swatch = new Pen(color, 2f)) g.DrawLine(swatch, legendX, plot.Top - 21, legendX + 16, plot.Top - 21);
                legendX -= 20;
            }

            double top = AxisTopNits(), topPq = HdrAnalyzer.NitsToPq(top);
            using var grid = new Pen(Color.FromArgb(Theme.IsLight ? 34 : 26, Theme.Text));
            using var axis = new Pen(Color.FromArgb(Theme.IsLight ? 80 : 64, Theme.Text));
            foreach (double tick in NitTicks.Where(tick => tick < top).Append(top))
            {
                int y = YOf(plot, tick, topPq);
                // Una tacca troppo vicina al massimo dell'asse si salta: le etichette si toccherebbero.
                if (tick < top && y - plot.Top < 18) continue;
                g.DrawLine(grid, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, Nits(tick), smallFont, new Rectangle(plot.Left - 60, y - 9, 52, 18), Theme.Muted, Line | TextFormatFlags.Right);
            }
            TextRenderer.DrawText(g, "nit", smallFont, new Rectangle(plot.Left - 60, plot.Bottom - 9, 52, 18), Theme.Muted, Line | TextFormatFlags.Right);
            g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);

            // Tempo: una tacca ogni 15 o 30 minuti.
            double duration = Math.Max(1, result.Duration);
            double step = duration > 5400 ? 1800 : duration > 2400 ? 900 : duration > 600 ? 300 : 60;
            for (double t = step; t < duration - step * .3; t += step)
            {
                int x = plot.Left + (int)Math.Round(t / duration * plot.Width);
                g.DrawLine(axis, x, plot.Bottom, x, plot.Bottom + 4);
                TextRenderer.DrawText(g, Clock(t), smallFont, new Rectangle(x - 30, plot.Bottom + 6, 60, 16), Theme.Muted, Line | TextFormatFlags.HorizontalCenter);
            }

            var samples = result.Samples;
            var state = g.Save();
            g.SetClip(Rectangle.Inflate(plot, 0, 1));
            var peak = new PointF[samples.Count];
            var average = new PointF[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                float x = plot.Left + (float)(samples[i].Time / duration * plot.Width);
                peak[i] = new PointF(x, YOf(plot, samples[i].PeakNits, topPq));
                average[i] = new PointF(x, YOf(plot, samples[i].AverageNits, topPq));
            }
            if (samples.Count > 1)
            {
                // Media: area tenue fino allo zero piu' linea; picco: sola linea, sopra.
                using (var area = new GraphicsPath())
                {
                    area.AddLines(average);
                    area.AddLine(average[^1].X, plot.Bottom, average[0].X, plot.Bottom);
                    area.CloseFigure();
                    using var fill = new SolidBrush(Color.FromArgb(Theme.IsLight ? 46 : 56, AverageColor));
                    g.FillPath(fill, area);
                }
                using (var pen = new Pen(AverageColor, 2f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, average);
                using (var pen = new Pen(PeakColor, 1.6f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, peak);
            }
            g.Restore(state);

            // MaxCLL dichiarato: una linea tratteggiata di riferimento, con l'etichetta.
            if (result.DeclaredMaxCll is int declared && declared < top)
            {
                int y = YOf(plot, declared, topPq);
                using var dashed = new Pen(Color.FromArgb(Theme.IsLight ? 150 : 130, Theme.Text), 1f) { DashStyle = DashStyle.Dash };
                g.DrawLine(dashed, plot.Left, y, plot.Right, y);
                string text = "MaxCLL " + Nits(declared);
                int textWidth = TextRenderer.MeasureText(g, text, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, text, smallFont, new Rectangle(plot.Left + 6, y - 18, textWidth + 4, 16), Theme.SubtleText, Line);
            }

            // Posizione di riproduzione.
            if (_position != null)
            {
                double now = 0;
                try { now = _position(); } catch { }
                if (now > 0 && now <= duration)
                {
                    int x = plot.Left + (int)Math.Round(now / duration * plot.Width);
                    using var marker = new Pen(Color.FromArgb(200, Theme.Text), 1f);
                    g.DrawLine(marker, x, plot.Top, x, plot.Bottom);
                    // Misura in tempo reale: media e picco del fotogramma attuale, sulla linea.
                    if (_liveStats is { } live)
                        foreach (var (nits, color) in new[] { (live.AverageNits, AverageColor), (live.PeakNits, PeakColor) })
                        {
                            int y = YOf(plot, nits, topPq);
                            using var ring = new SolidBrush(Theme.Sheet);
                            using var dot = new SolidBrush(color);
                            g.FillEllipse(ring, x - 6, y - 6, 12, 12);
                            g.FillEllipse(dot, x - 4, y - 4, 8, 8);
                        }
                }
            }

            // Passaggio del mouse: linea verticale, punti sulle due curve e valori.
            if (plot.Contains(_mouse) && samples.Count > 0)
            {
                double hoverTime = (_mouse.X - plot.Left) / (double)plot.Width * duration;
                int nearest = 0;
                for (int i = 1; i < samples.Count; i++)
                    if (Math.Abs(samples[i].Time - hoverTime) < Math.Abs(samples[nearest].Time - hoverTime)) nearest = i;
                using (var cross = new Pen(Color.FromArgb(Theme.IsLight ? 120 : 110, Theme.Text), 1f))
                    g.DrawLine(cross, peak[nearest].X, plot.Top, peak[nearest].X, plot.Bottom);
                foreach (var (point, color) in new[] { (average[nearest], AverageColor), (peak[nearest], PeakColor) })
                {
                    using var ring = new SolidBrush(Theme.Sheet);
                    using var dot = new SolidBrush(color);
                    g.FillEllipse(ring, point.X - 6, point.Y - 6, 12, 12);
                    g.FillEllipse(dot, point.X - 4, point.Y - 4, 8, 8);
                }
                DrawTooltip(g, new Point((int)peak[nearest].X, plot.Top + 8), plot, smallFont, Clock(samples[nearest].Time),
                    (T("Picco", "Peak"), Nits(samples[nearest].PeakNits) + " nit", PeakColor),
                    (T("Media", "Average"), Nits(samples[nearest].AverageNits) + " nit", AverageColor));
            }
        }

        private void DrawHistogram(Graphics g, HdrAnalyzer.Result result, Font sectionFont, Font smallFont)
        {
            Rectangle plot = HistogramPlot;
            TextRenderer.DrawText(g, T("Distribuzione della luminosità", "Luminance distribution"), sectionFont, new Rectangle(30, plot.Top - 32, 320, 22), Theme.Text, Line);
            string shares = T("Sopra 100 nit ", "Above 100 nit ") + Share(result.ShareAbove100) + "      " + T("sopra 400 ", "above 400 ") + Share(result.ShareAbove400) +
                "      " + T("sopra 1000 ", "above 1000 ") + Share(result.ShareAbove1000);
            TextRenderer.DrawText(g, shares, smallFont, new Rectangle(plot.Right - 520, plot.Top - 30, 520, 18), Theme.SubtleText, Line | TextFormatFlags.Right);

            double top = AxisTopNits(), topPq = HdrAnalyzer.NitsToPq(top);
            using var axis = new Pen(Color.FromArgb(Theme.IsLight ? 80 : 64, Theme.Text));
            g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            foreach (double tick in NitTicks.Where(tick => tick < top).Append(top))
            {
                int x = plot.Left + (int)Math.Round(HdrAnalyzer.NitsToPq(tick) / topPq * plot.Width);
                g.DrawLine(axis, x, plot.Bottom, x, plot.Bottom + 4);
                TextRenderer.DrawText(g, Nits(tick), smallFont, new Rectangle(x - 30, plot.Bottom + 6, 60, 16), Theme.Muted, Line | TextFormatFlags.HorizontalCenter);
            }
            TextRenderer.DrawText(g, "nit", smallFont, new Rectangle(plot.Left - 60, plot.Bottom + 6, 52, 16), Theme.Muted, Line | TextFormatFlags.Right);

            // Altezze in scala radice: in lineare le alte luci (frazioni di punto percentuale)
            // sparirebbero accanto alle ombre. Il valore esatto e' nell'etichetta al passaggio.
            var histogram = result.Histogram;
            double max = histogram.Max();
            if (max <= 0) return;
            int bins = histogram.Length, hoverBin = -1;
            double binsShown = topPq * bins;
            float barWidth = (float)(plot.Width / binsShown);
            using var fill = new SolidBrush(AverageColor);
            using var hot = new SolidBrush(ControlPaint.Light(AverageColor, .35f));
            if (plot.Contains(_mouse)) hoverBin = Math.Clamp((int)((_mouse.X - plot.Left) / barWidth), 0, bins - 1);
            for (int i = 0; i < bins && i < Math.Ceiling(binsShown); i++)
            {
                if (histogram[i] <= 0) continue;
                float height = Math.Max(1.5f, (float)(Math.Sqrt(histogram[i] / max) * plot.Height));
                float x = plot.Left + i * barWidth;
                float right = Math.Min(plot.Right, x + barWidth - 2);
                if (right - x < 1) continue;
                g.FillRectangle(i == hoverBin ? hot : fill, x, plot.Bottom - height, right - x, height);
            }

            var liveStats = _liveStats;
            if (liveStats != null)
            {
                // Fotogramma in riproduzione: profilo a gradini sopra le barre dell'intero film,
                // sulla stessa scala (un fotogramma solo e' piu' concentrato: si ferma al bordo).
                var outline = new List<PointF>();
                for (int i = 0; i < bins && i < Math.Ceiling(binsShown); i++)
                {
                    float x = plot.Left + i * barWidth, right = Math.Min(plot.Right, x + barWidth);
                    if (right <= x) break;
                    float height = liveStats.Histogram[i] <= 0 ? 0 : Math.Min(plot.Height, Math.Max(1.5f, (float)(Math.Sqrt(liveStats.Histogram[i] / max) * plot.Height)));
                    outline.Add(new PointF(x, plot.Bottom - height));
                    outline.Add(new PointF(right, plot.Bottom - height));
                }
                if (outline.Count > 1)
                {
                    using var pen = new Pen(PeakColor, 1.6f) { LineJoin = LineJoin.Round };
                    g.DrawLines(pen, outline.ToArray());
                }

                // Legenda accanto al titolo: barre = film intero, linea = questo momento.
                int legendX = 30 + TextRenderer.MeasureText(g, T("Distribuzione della luminosità", "Luminance distribution"), sectionFont, Size.Empty, TextFormatFlags.NoPadding).Width + 26;
                g.FillRectangle(fill, legendX, plot.Top - 26, 10, 10);
                legendX += 16;
                string whole = T("Film intero", "Whole film"), current = T("Ora", "Now");
                int wholeWidth = TextRenderer.MeasureText(g, whole, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, whole, smallFont, new Rectangle(legendX, plot.Top - 30, wholeWidth + 2, 18), Theme.SubtleText, Line);
                legendX += wholeWidth + 18;
                using (var swatch = new Pen(PeakColor, 2f)) g.DrawLine(swatch, legendX, plot.Top - 21, legendX + 16, plot.Top - 21);
                legendX += 22;
                TextRenderer.DrawText(g, current, smallFont, new Rectangle(legendX, plot.Top - 30, 60, 18), Theme.SubtleText, Line);
            }

            if (hoverBin >= 0 && hoverBin < Math.Ceiling(binsShown))
            {
                double from = HdrAnalyzer.PqToNits(hoverBin / (double)bins), to = HdrAnalyzer.PqToNits((hoverBin + 1) / (double)bins);
                string Percent(double share) => (share * 100).ToString(share < 0.001 ? "0.0000" : "0.00", CultureInfo.CurrentCulture) + "%";
                var rows = new List<(string Name, string Value, Color Color)> { (T("Parte dell'immagine", "Share of the picture"), Percent(histogram[hoverBin]), AverageColor) };
                if (liveStats != null) rows.Add((T("In questo momento", "Right now"), Percent(liveStats.Histogram[hoverBin]), PeakColor));
                DrawTooltip(g, new Point(plot.Left + (int)((hoverBin + .5f) * barWidth), plot.Top - 4), plot, smallFont,
                    Nits(from) + " – " + Nits(to) + " nit", rows.ToArray());
            }
        }

        private void DrawGamut(Graphics g, HdrAnalyzer.Result result, Font labelFont, Font smallFont)
        {
            int y = ClientSize.Height - 62;
            TextRenderer.DrawText(g, T("Colori", "Colours"), smallFont, new Rectangle(30, y, 60, 18), Theme.Muted, Line);
            if (!result.GamutMeasured)
            {
                TextRenderer.DrawText(g, T("Gamut non misurato per questo formato", "Gamut not measured for this format"), smallFont, new Rectangle(96, y, 400, 18), Theme.Muted, Line);
                return;
            }
            // Una barra divisa in tre: stessa tinta, dal piu' tenue (Rec.709) al pieno (BT.2020).
            var bar = new Rectangle(96, y + 6, 300, 6);
            var parts = new[] { (Name: "Rec.709", Value: result.GamutRec709, Alpha: 90), (Name: "P3", Value: result.GamutP3, Alpha: 170), (Name: "BT.2020", Value: result.GamutRec2020, Alpha: 255) };
            float x = bar.Left;
            foreach (var part in parts)
            {
                float w = (float)(bar.Width * part.Value);
                if (w >= 1)
                {
                    using var fill = new SolidBrush(Color.FromArgb(part.Alpha, AverageColor));
                    g.FillRectangle(fill, x, bar.Top, Math.Max(1, w - 2), bar.Height);
                }
                x += w;
            }
            string text = string.Join("      ", parts.Select(part => (part.Name == "Rec.709" ? T("entro ", "within ") : part.Name == "P3" ? T("fino a ", "up to ") : T("oltre, in ", "beyond, in ")) + part.Name + " " + Share(part.Value)));
            TextRenderer.DrawText(g, text, smallFont, new Rectangle(bar.Right + 18, y, 460, 18), Theme.SubtleText, Line);
        }

        private void DrawTooltip(Graphics g, Point anchor, Rectangle plot, Font font, string heading, params (string Name, string Value, Color Color)[] rows)
        {
            int nameWidth = rows.Max(row => TextRenderer.MeasureText(g, row.Name, font, Size.Empty, TextFormatFlags.NoPadding).Width);
            int valueWidth = rows.Max(row => TextRenderer.MeasureText(g, row.Value, font, Size.Empty, TextFormatFlags.NoPadding).Width);
            int headingWidth = TextRenderer.MeasureText(g, heading, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int width = Math.Max(headingWidth, 14 + nameWidth + 14 + valueWidth) + 24;
            int height = 12 + 18 + rows.Length * 18 + 8;
            int x = anchor.X + 14;
            if (x + width > plot.Right) x = anchor.X - 14 - width;
            x = Math.Max(plot.Left, x);
            var box = new Rectangle(x, anchor.Y, width, height);
            using (var shape = Rounded(box, 6))
            using (var fill = new SolidBrush(Theme.SheetRaised))
            using (var edge = new Pen(Color.FromArgb(Theme.IsLight ? 60 : 50, Theme.Text)))
            {
                g.FillPath(fill, shape);
                g.DrawPath(edge, shape);
            }
            TextRenderer.DrawText(g, heading, font, new Rectangle(box.Left + 12, box.Top + 8, width - 24, 18), Theme.Muted, Line);
            for (int i = 0; i < rows.Length; i++)
            {
                int rowY = box.Top + 8 + 18 + i * 18;
                using (var dot = new SolidBrush(rows[i].Color)) g.FillEllipse(dot, box.Left + 12, rowY + 5, 8, 8);
                TextRenderer.DrawText(g, rows[i].Name, font, new Rectangle(box.Left + 26, rowY, nameWidth + 4, 18), Theme.SubtleText, Line);
                TextRenderer.DrawText(g, rows[i].Value, font, new Rectangle(box.Right - 12 - valueWidth - 2, rowY, valueWidth + 4, 18), Theme.Text, Line | TextFormatFlags.Right);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _cts.Cancel(); } catch { }
                DisposeLive();
                // La chiusura aspetta l'eventuale misura in corso: fuori dal thread dell'interfaccia.
                var live = _live;
                _live = null;
                if (live != null) _ = Task.Run(live.Dispose);
                _positionTimer.Dispose();
                _cts.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
