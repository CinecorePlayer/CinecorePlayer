#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Vista "Tempo reale" dell'analisi HDR: gli strumenti del fotogramma in riproduzione.
    // - forma d'onda: per ogni colonna dell'immagine, a che luminosita' stanno i tre canali
    //   (rosso, verde e blu sovrapposti: dove coincidono la traccia e' bianca);
    // - livelli: picco e media del fotogramma sulla stessa scala;
    // - cromaticita': dove cadono i colori sul diagramma CIE 1931, con i triangoli di
    //   Rec.709, P3 e BT.2020;
    // - falsi colori: l'immagine colorata per fasce di luminosita';
    // - ultimo minuto: picco e media nel tempo;
    // - vettorscopio: tinta e saturazione del segnale (Cb/Cr).
    // Gli strumenti hanno sempre il fondo scuro, anche nel tema chiaro: sono misure di luce.
    internal sealed partial class HdrAnalysisForm
    {
        private readonly System.Windows.Forms.Timer _scopeTimer = new() { Interval = 45 };
        private bool _liveView;
        private long _liveVersion = -1, _liveShownTick;
        private float _liveHoldPeak;
        private readonly List<(long Stamp, float Peak, float Average)> _liveHistory = new();
        private Bitmap? _waveBitmap, _chromaBitmap, _vectorBitmap, _pictureBitmap;
        private readonly int[] _wavePixels = new int[HdrAnalyzer.ScopeData.WaveColumns * HdrAnalyzer.ScopeData.WaveLevels];
        private readonly int[] _chromaPixels = new int[HdrAnalyzer.ScopeData.ChromaSize * HdrAnalyzer.ScopeData.ChromaSize];
        private readonly int[] _vectorPixels = new int[HdrAnalyzer.ScopeData.VectorSize * HdrAnalyzer.ScopeData.VectorSize];
        private readonly byte[] _waveTone = new byte[4096];
        private int _waveToneSamples = -1;
        private static int[]? _chromaColors, _vectorColors;

        // I grafici stanno direttamente sulla scheda, nei due temi: stesso fondo della scheda. Sul fondo
        // chiaro le tracce si scuriscono invece di accendersi (vedi WavePixel).
        private static Color ScopeBack => Theme.Sheet;
        private static Color ScopeText => Theme.Muted;
        private static Color ScopeLine => Color.FromArgb(Theme.IsLight ? 40 : 30, Theme.Text);

        // Un punto della forma d'onda: sul fondo scuro il segnale accende il suo canale; sul fondo chiaro
        // lo si disegna scurendo gli altri, cosi' il rosso resta rosso e il bianco diventa grigio scuro.
        private static int WavePixel(int toneR, int toneG, int toneB)
        {
            Color back = ScopeBack;
            int r, g, b;
            if (!Theme.IsLight)
            {
                r = Math.Max(back.R, toneR); g = Math.Max(back.G, toneG); b = Math.Max(back.B, toneB);
            }
            else
            {
                int strongest = Math.Max(toneR, Math.Max(toneG, toneB));
                r = Math.Clamp(back.R - strongest + toneR * 3 / 4, 0, 255);
                g = Math.Clamp(back.G - strongest + toneG * 3 / 4, 0, 255);
                b = Math.Clamp(back.B - strongest + toneB * 3 / 4, 0, 255);
            }
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        private Rectangle TabsBounds => new(ClientSize.Width - 30 - 32 - 20 - 240, 20, 240, 30);
        private Rectangle WavePanel => new(30, 322, 740, 282);
        private Rectangle BarsPanel => new(786, 322, 130, 282);
        private Rectangle ChromaPanel => new(932, 322, ClientSize.Width - 30 - 932, 282);
        private Rectangle PicturePanel => new(30, 652, 500, 264);
        private Rectangle LegendArea => new(546, 652, 116, 264);
        private Rectangle HistoryPanel => new(678, 652, 370, 264);
        private Rectangle VectorPanel => new(1064, 652, ClientSize.Width - 30 - 1064, 264);
        private bool TabsVisible => _position != null && _live != null;

        private async void StartLive()
        {
            HdrAnalyzer.LiveSession? session = null;
            try { session = await Task.Run(() => HdrAnalyzer.LiveSession.OpenContinuous(_path)); } catch { }
            if (session == null) return;
            if (IsDisposed || !session.CanMeasure) { _ = Task.Run(session.Dispose); return; }
            _live = session;
            session.Start();
            _scopeTimer.Tick += (_, _) => LiveTick();
            _scopeTimer.Start();
            Invalidate();
        }

        private void LiveTick()
        {
            var session = _live;
            if (session == null || _position == null) return;
            double now = 0;
            try { now = _position(); } catch { }
            session.SetPlayhead(now);
            if (now <= 0)
            {
                // Il film della scheda non e' (piu') quello in riproduzione.
                if (_liveStats != null) { _liveStats = null; Invalidate(); }
                return;
            }
            long version = session.Version;
            if (version == _liveVersion) return;
            long tick = Environment.TickCount64;
            // Nella vista del film intero bastano quattro aggiornamenti al secondo.
            if (!_liveView && tick - _liveShownTick < 250) return;
            _liveVersion = version;
            _liveShownTick = tick;
            var stats = session.Read(_liveView ? RenderScopes : null);
            if (stats == null) return;
            _liveStats = stats;
            if (stats.PeakNits > _liveHoldPeak) _liveHoldPeak = stats.PeakNits;
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _liveHistory.Add((stamp, stats.PeakNits, stats.AverageNits));
            while (_liveHistory.Count > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_liveHistory[0].Stamp, stamp).TotalSeconds > 62) _liveHistory.RemoveAt(0);
            Invalidate();
        }

        // ===== Dai contatori alle immagini =====

        private static void Write(ref Bitmap? bitmap, int width, int height, int[] pixels)
        {
            if (bitmap == null || bitmap.Width != width || bitmap.Height != height) { bitmap?.Dispose(); bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb); }
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, width * height); }
            finally { bitmap.UnlockBits(data); }
        }

        private static int Blend(int color, double amount)
        {
            int r = (int)(ScopeBack.R + (((color >> 16) & 255) - ScopeBack.R) * amount);
            int g = (int)(ScopeBack.G + (((color >> 8) & 255) - ScopeBack.G) * amount);
            int b = (int)(ScopeBack.B + ((color & 255) - ScopeBack.B) * amount);
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        // Colore di ogni casella del diagramma di cromaticita': la tinta di quel punto, piena.
        private static int[] ChromaColors()
        {
            if (_chromaColors != null) return _chromaColors;
            const int n = HdrAnalyzer.ScopeData.ChromaSize;
            var colors = new int[n * n];
            for (int cy = 0; cy < n; cy++)
                for (int cx = 0; cx < n; cx++)
                {
                    double x = (cx + .5) / n * HdrAnalyzer.ScopeData.ChromaMaxX, y = Math.Max(1e-4, (cy + .5) / n * HdrAnalyzer.ScopeData.ChromaMaxY);
                    double bigX = x / y, bigZ = (1 - x - y) / y;
                    double r = 3.2406 * bigX - 1.5372 - 0.4986 * bigZ, g = -0.9689 * bigX + 1.8758 + 0.0415 * bigZ, b = 0.0557 * bigX - 0.2040 + 1.0570 * bigZ;
                    r = Math.Max(0, r); g = Math.Max(0, g); b = Math.Max(0, b);
                    double max = Math.Max(1e-6, Math.Max(r, Math.Max(g, b)));
                    // Un po' di bianco: i colori fuori da Rec.709 resterebbero altrimenti tutti uguali al bordo.
                    int Channel(double v) => (int)(255 * Math.Pow(0.18 + 0.82 * v / max, 1 / 2.2));
                    colors[(n - 1 - cy) * n + cx] = (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
                }
            return _chromaColors = colors;
        }

        private static int[] VectorColors()
        {
            if (_vectorColors != null) return _vectorColors;
            const int n = HdrAnalyzer.ScopeData.VectorSize;
            const double kr = 0.2627, kb = 0.0593, kg = 1 - kr - kb, range = HdrAnalyzer.ScopeData.VectorRange;
            var colors = new int[n * n];
            for (int vy = 0; vy < n; vy++)
                for (int vx = 0; vx < n; vx++)
                {
                    double cb = ((vx + .5) / n * 2 - 1) * range, cr = (1 - (vy + .5) / n * 2) * range;
                    // Tinta della direzione, a saturazione piena: vicino al centro resterebbe grigia.
                    double length = Math.Max(1e-6, Math.Sqrt(cb * cb + cr * cr)), scale = Math.Min(1, length / range * 2.2) * 0.33 / length;
                    cb *= scale; cr *= scale;
                    double luma = 0.62, r = luma + cr * 2 * (1 - kr), b = luma + cb * 2 * (1 - kb), g = (luma - kr * r - kb * b) / kg;
                    int Channel(double v) => (int)(255 * Math.Clamp(v, 0, 1));
                    colors[vy * n + vx] = (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
                }
            return _vectorColors = colors;
        }

        private void RenderScopes(HdrAnalyzer.ScopeData data)
        {
            const int columns = HdrAnalyzer.ScopeData.WaveColumns, levels = HdrAnalyzer.ScopeData.WaveLevels;
            if (_waveToneSamples != data.SamplesPerColumn)
            {
                // Intensita' della traccia: radice del numero di pixel, cosi' si vedono sia le
                // zone piene sia i pochi pixel delle alte luci.
                _waveToneSamples = data.SamplesPerColumn;
                double gain = 20.0 / Math.Max(1, data.SamplesPerColumn);
                for (int i = 0; i < _waveTone.Length; i++) _waveTone[i] = (byte)(255 * Math.Min(1, Math.Sqrt(i * gain)));
            }
            byte[] tone = _waveTone;
            int[] waveR = data.WaveR, waveG = data.WaveG, waveB = data.WaveB, wave = _wavePixels;
            for (int column = 0; column < columns; column++)
            {
                int source = column * levels;
                for (int level = 0; level < levels; level++, source++)
                {
                    wave[(levels - 1 - level) * columns + column] = WavePixel(tone[Math.Min(4095, waveR[source])], tone[Math.Min(4095, waveG[source])], tone[Math.Min(4095, waveB[source])]);
                }
            }
            Write(ref _waveBitmap, columns, levels, wave);

            const int chromaN = HdrAnalyzer.ScopeData.ChromaSize;
            int[] chromaColors = ChromaColors(), chroma = data.Chroma, chromaPixels = _chromaPixels;
            int back = ScopeBack.ToArgb();
            double chromaGain = 400.0 / Math.Max(1, data.ChromaPixels);
            for (int cy = 0; cy < chromaN; cy++)
                for (int cx = 0; cx < chromaN; cx++)
                {
                    int count = chroma[cy * chromaN + cx], target = (chromaN - 1 - cy) * chromaN + cx;
                    chromaPixels[target] = count == 0 ? back : Blend(chromaColors[target], Math.Min(1, Math.Pow(count * chromaGain, 0.4)));
                }
            Write(ref _chromaBitmap, chromaN, chromaN, chromaPixels);

            const int vectorN = HdrAnalyzer.ScopeData.VectorSize;
            int[] vectorColors = VectorColors(), vector = data.Vector, vectorPixels = _vectorPixels;
            double vectorGain = 300.0 / Math.Max(1, data.Pixels);
            for (int i = 0; i < vector.Length; i++)
                vectorPixels[i] = vector[i] == 0 ? back : Blend(vectorColors[i], Math.Min(1, Math.Pow(vector[i] * vectorGain, 0.4)));
            Write(ref _vectorBitmap, vectorN, vectorN, vectorPixels);

            if (data.PictureWidth > 0 && data.PictureHeight > 0 && data.FalseColor.Length == data.PictureWidth * data.PictureHeight * 4)
            {
                if (_pictureBitmap == null || _pictureBitmap.Width != data.PictureWidth || _pictureBitmap.Height != data.PictureHeight)
                { _pictureBitmap?.Dispose(); _pictureBitmap = new Bitmap(data.PictureWidth, data.PictureHeight, PixelFormat.Format32bppArgb); }
                var locked = _pictureBitmap.LockBits(new Rectangle(0, 0, data.PictureWidth, data.PictureHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { Marshal.Copy(data.FalseColor, 0, locked.Scan0, data.FalseColor.Length); }
                finally { _pictureBitmap.UnlockBits(locked); }
            }
        }

        // ===== Disegno =====

        // Sotto il nero dello schermo di mastering il segnale non dice piu' nulla: la gamma
        // dinamica si misura da li' (0,005 nit se il file non lo dichiara).
        private double BlackFloor => _result?.MasteringMinNits is double min && min > 0 ? min : 0.005;

        private string Black(double nits) => nits < 0.001 ? "< " + 0.001.ToString("0.###", CultureInfo.CurrentCulture) : Nits(nits);

        private double LiveTopNits()
        {
            double need = Math.Max(Math.Max(_result?.MeasuredPeakNits ?? 0, _result?.DeclaredMaxCll ?? 0), _liveHoldPeak) * 1.04;
            return need <= 0 ? 1000 : AxisTops.FirstOrDefault(top => top >= need, 10000);
        }

        private void DrawTabs(Graphics g, Font font)
        {
            if (!TabsVisible) return;
            Rectangle box = TabsBounds;
            using (var shape = Rounded(box, 8))
            using (var fill = new SolidBrush(Theme.SheetRaised))
                g.FillPath(fill, shape);
            for (int i = 0; i < 2; i++)
            {
                var tab = new Rectangle(box.Left + i * box.Width / 2, box.Top, box.Width / 2, box.Height);
                bool active = (i == 1) == _liveView;
                if (active)
                {
                    using var shape = Rounded(Rectangle.Inflate(tab, -3, -3), 6);
                    using var fill = new SolidBrush(Color.FromArgb(Theme.IsLight ? 40 : 54, Theme.Accent));
                    g.FillPath(fill, shape);
                }
                TextRenderer.DrawText(g, i == 0 ? T("Film intero", "Whole film") : T("Tempo reale", "Real time"), font, tab,
                    active ? Theme.Text : tab.Contains(_mouse) ? Theme.SubtleText : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private static void ScopePanel(Graphics g, Rectangle panel)
        {
            if (!Theme.IsLight) return; // nessuna cornice: i grafici stanno direttamente sulla scheda
            using var shape = Rounded(panel, 8);
            using var fill = new SolidBrush(ScopeBack);
            g.FillPath(fill, shape);
        }

        private static void DrawScaled(Graphics g, Image image, RectangleF target, RectangleF source)
        {
            var state = g.Save();
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var edges = new ImageAttributes();
            edges.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(image, new[] { new PointF(target.Left, target.Top), new PointF(target.Right, target.Top), new PointF(target.Left, target.Bottom) },
                source, GraphicsUnit.Pixel, edges);
            g.Restore(state);
        }

        private void DrawLiveView(Graphics g, Font sectionFont, Font labelFont, Font valueFont, Font smallFont, Font bodyFont)
        {
            var live = _liveStats;
            if (live == null)
            {
                TextRenderer.DrawText(g, T("Avvia la riproduzione del film: gli strumenti seguono il fotogramma a schermo.", "Start playing the film: the scopes follow the frame on screen."),
                    bodyFont, new Rectangle(30, 300, ClientSize.Width - 60, 60), Theme.SubtleText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return;
            }

            // Numeri del fotogramma.
            double stops = live.PeakNits > 0 ? Math.Log2(live.PeakNits / Math.Max(BlackFloor, live.BlackNits)) : 0;
            var stats = new (string Label, string Value, string Caption)[]
            {
                (T("Picco", "Peak"), Nits(live.PeakNits) + " nit", T("99,99° percentile", "99.99th percentile")),
                (T("Massimo", "Maximum"), Nits(live.MaxNits) + " nit", T("pixel più luminoso", "brightest pixel")),
                (T("Media", "Average"), Nits(live.AverageNits) + " nit", T("come MaxFALL", "as MaxFALL")),
                (T("Luminanza", "Luminance"), Nits(live.LuminanceNits) + " nit", T("media di Y", "mean of Y")),
                (T("Mediana", "Median"), Nits(live.MedianNits) + " nit", T("99° perc. ", "99th pct ") + Nits(live.P99Nits) + " nit"),
                (T("Nero", "Black"), Black(live.BlackNits) + " nit", T("0,1° percentile", "0.1st percentile")),
                (T("Gamma dinamica", "Dynamic range"), stops.ToString("0.0", CultureInfo.CurrentCulture) + " stop", T("dal nero al picco", "black to peak"))
            };
            // Stesso riquadro dei valori della pagina "Film intero": qui e' centrato in altezza nella fascia alta.
            var card = new Rectangle(30, 96, ClientSize.Width - 60, 178);
            using var columnRule = new Pen(Color.FromArgb(Theme.IsLight ? 34 : 24, Theme.Text));
            using var bigFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 21f, FontStyle.Regular, GraphicsUnit.Point);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            int columnWidth = card.Width / stats.Length;
            for (int i = 0; i < stats.Length; i++)
            {
                int x = card.Left + i * columnWidth + (i > 0 ? 22 : 0), w = columnWidth - 28 - (i > 0 ? 22 : 0);
                if (i > 0) g.DrawLine(columnRule, card.Left + i * columnWidth, 140, card.Left + i * columnWidth, 220);
                TextRenderer.DrawText(g, stats[i].Label, nameFont, new Rectangle(x, 138, w, 18), Theme.Muted, Line);
                TextRenderer.DrawText(g, stats[i].Value, bigFont, new Rectangle(x, 158, w, 42), Theme.Text, Line);
                TextRenderer.DrawText(g, stats[i].Caption, smallFont, new Rectangle(x, 204, w, 18), Theme.Muted, Line);
            }

            double top = LiveTopNits(), topPq = HdrAnalyzer.NitsToPq(top);
            using var gridPen = new Pen(ScopeLine);
            using var titleBrush = new SolidBrush(Theme.Text);

            // --- Forma d'onda ---
            Rectangle wavePanel = WavePanel;
            TextRenderer.DrawText(g, T("Forma d'onda RGB", "RGB waveform"), sectionFont, new Rectangle(wavePanel.Left, wavePanel.Top - 26, 300, 22), Theme.Text, Line);
            TextRenderer.DrawText(g, Clock(live.Time) + (live.ActiveWidth > 0 ? "      " + T("area attiva ", "active area ") + live.ActiveWidth + "×" + live.ActiveHeight +
                    " (" + (live.ActiveWidth / (double)Math.Max(1, live.ActiveHeight)).ToString("0.00", CultureInfo.CurrentCulture) + ":1)" : ""),
                smallFont, new Rectangle(wavePanel.Right - 360, wavePanel.Top - 24, 360, 18), Theme.SubtleText, Line | TextFormatFlags.Right);
            ScopePanel(g, wavePanel);
            var wavePlot = new Rectangle(wavePanel.Left + 50, wavePanel.Top + 26, wavePanel.Width - 50 - 14, wavePanel.Height - 26 - 14);
            if (_waveBitmap != null)
            {
                float shown = (float)(topPq * HdrAnalyzer.ScopeData.WaveLevels);
                DrawScaled(g, _waveBitmap, wavePlot, new RectangleF(0, HdrAnalyzer.ScopeData.WaveLevels - shown, HdrAnalyzer.ScopeData.WaveColumns, shown));
            }
            foreach (double tick in NitTicks.Where(tick => tick < top).Append(top))
            {
                int y = YOf(wavePlot, tick, topPq);
                if (tick < top && y - wavePlot.Top < 16) continue;
                g.DrawLine(gridPen, wavePlot.Left, y, wavePlot.Right, y);
                TextRenderer.DrawText(g, Nits(tick), smallFont, new Rectangle(wavePanel.Left + 4, y - 9, 40, 18), ScopeText, ScopeBack, Line | TextFormatFlags.Right);
            }
            TextRenderer.DrawText(g, "nit", smallFont, new Rectangle(wavePanel.Left + 4, wavePanel.Top + 5, 40, 16), ScopeText, ScopeBack, Line | TextFormatFlags.Right);
            if (_result?.DeclaredMaxCll is int declared && declared < top)
            {
                int y = YOf(wavePlot, declared, topPq);
                using var dashed = new Pen(Color.FromArgb(130, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash };
                g.DrawLine(dashed, wavePlot.Left, y, wavePlot.Right, y);
            }

            // --- Livelli ---
            Rectangle barsPanel = BarsPanel;
            TextRenderer.DrawText(g, T("Livelli", "Levels"), sectionFont, new Rectangle(barsPanel.Left, barsPanel.Top - 26, 110, 22), Theme.Text, Line);
            ScopePanel(g, barsPanel);
            var barsPlot = new Rectangle(barsPanel.Left + 14, wavePlot.Top, barsPanel.Width - 28, wavePlot.Height);
            int barWidth = 26, barGap = (barsPlot.Width - barWidth * 2) / 3;
            var bars = new[] { (Name: T("Picco", "Peak"), Value: (double)live.PeakNits, Color: PeakColor), (Name: T("Media", "Avg"), Value: (double)live.AverageNits, Color: AverageColor) };
            for (int i = 0; i < bars.Length; i++)
            {
                int x = barsPlot.Left + barGap + i * (barWidth + barGap);
                using (var track = new SolidBrush(Color.FromArgb(18, 255, 255, 255))) g.FillRectangle(track, x, barsPlot.Top, barWidth, barsPlot.Height);
                int y = YOf(barsPlot, bars[i].Value, topPq);
                using (var fill = new SolidBrush(bars[i].Color)) g.FillRectangle(fill, x, y, barWidth, barsPlot.Bottom - y);
                TextRenderer.DrawText(g, bars[i].Name, smallFont, new Rectangle(x - 12, barsPanel.Top + 5, barWidth + 24, 16), ScopeText, ScopeBack, Line | TextFormatFlags.HorizontalCenter);
                if (i == 0 && _liveHoldPeak > 0)
                {
                    // Il picco piu' alto visto da quando la scheda e' aperta.
                    int hold = YOf(barsPlot, _liveHoldPeak, topPq);
                    using var holdPen = new Pen(Color.FromArgb(230, 255, 255, 255), 1f);
                    g.DrawLine(holdPen, x - 3, hold, x + barWidth + 3, hold);
                }
            }
            if (_result?.DeclaredMaxCll is int cll && cll < top)
            {
                int y = YOf(barsPlot, cll, topPq);
                using var dashed = new Pen(Color.FromArgb(130, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash };
                g.DrawLine(dashed, barsPlot.Left, y, barsPlot.Right, y);
            }

            DrawChromaticity(g, live, sectionFont, smallFont);
            DrawFalseColor(g, sectionFont, smallFont);
            DrawHistory(g, sectionFont, smallFont, top, topPq);
            DrawVectorscope(g, sectionFont, smallFont);

            var session = _live;
            string detail = session == null ? "" : session.Detail switch
            {
                0 => T("Ogni fotogramma", "Every frame"),
                1 => T("Solo fotogrammi di riferimento (decodifica più lenta della riproduzione)", "Reference frames only (decoding slower than playback)"),
                _ => T("Solo fotogrammi chiave (decodifica più lenta della riproduzione)", "Key frames only (decoding slower than playback)")
            };
            if (session != null && session.MeasuredRate > 0) detail += "      " + session.MeasuredRate.ToString("0", CultureInfo.CurrentCulture) + T(" misure al secondo", " measurements per second");
            if (_liveHoldPeak > 0) detail += "      " + T("picco più alto visto ", "highest peak seen ") + Nits(_liveHoldPeak) + " nit";
            TextRenderer.DrawText(g, detail, smallFont, new Rectangle(30, ClientSize.Height - 26, ClientSize.Width - 60, 18), Theme.Muted, Line | TextFormatFlags.Right);
        }

        // Luogo dello spettro CIE 1931 (x, y) da 380 a 700 nm.
        private static readonly (float X, float Y)[] SpectralLocus =
        {
            (.1741f, .0050f), (.1733f, .0048f), (.1714f, .0051f), (.1644f, .0109f), (.1566f, .0177f), (.1440f, .0297f), (.1241f, .0578f), (.0913f, .1327f),
            (.0687f, .2007f), (.0454f, .2950f), (.0235f, .4127f), (.0082f, .5384f), (.0039f, .6548f), (.0139f, .7502f), (.0389f, .8120f), (.0743f, .8338f),
            (.1142f, .8262f), (.1547f, .8059f), (.1929f, .7816f), (.2296f, .7543f), (.2658f, .7243f), (.3016f, .6923f), (.3373f, .6589f), (.3731f, .6245f),
            (.4087f, .5896f), (.4441f, .5547f), (.4788f, .5202f), (.5125f, .4866f), (.5448f, .4544f), (.5752f, .4242f), (.6029f, .3965f), (.6270f, .3725f),
            (.6658f, .3340f), (.6915f, .3083f), (.7079f, .2920f), (.7190f, .2809f), (.7260f, .2740f), (.7300f, .2700f), (.7334f, .2666f), (.7347f, .2653f)
        };

        private static readonly (string Name, (float X, float Y)[] Points)[] GamutTriangles =
        {
            ("Rec.709", new[] { (.640f, .330f), (.300f, .600f), (.150f, .060f) }),
            ("P3", new[] { (.680f, .320f), (.265f, .690f), (.150f, .060f) }),
            ("BT.2020", new[] { (.708f, .292f), (.170f, .797f), (.131f, .046f) })
        };

        private static readonly Color[] GamutColors = { Color.FromArgb(205, 210, 216), Color.FromArgb(226, 150, 60), Color.FromArgb(80, 150, 245) };

        private void DrawChromaticity(Graphics g, HdrAnalyzer.FrameStats live, Font sectionFont, Font smallFont)
        {
            Rectangle panel = ChromaPanel;
            TextRenderer.DrawText(g, T("Cromaticità CIE 1931", "CIE 1931 chromaticity"), sectionFont, new Rectangle(panel.Left, panel.Top - 26, 300, 22), Theme.Text, Line);
            ScopePanel(g, panel);
            int height = panel.Height - 24, width = (int)Math.Round(height * HdrAnalyzer.ScopeData.ChromaMaxX / HdrAnalyzer.ScopeData.ChromaMaxY);
            var plot = new Rectangle(panel.Left + 16, panel.Top + 12, width, height);
            if (_chromaBitmap != null)
                DrawScaled(g, _chromaBitmap, plot, new RectangleF(0, 0, _chromaBitmap.Width, _chromaBitmap.Height));
            PointF At((float X, float Y) p) => new(plot.Left + p.X / (float)HdrAnalyzer.ScopeData.ChromaMaxX * plot.Width, plot.Bottom - p.Y / (float)HdrAnalyzer.ScopeData.ChromaMaxY * plot.Height);

            using (var locus = new Pen(Color.FromArgb(120, 255, 255, 255), 1f) { LineJoin = LineJoin.Round })
                g.DrawPolygon(locus, SpectralLocus.Select(At).ToArray());
            for (int i = 0; i < GamutTriangles.Length; i++)
            {
                using var pen = new Pen(Color.FromArgb(i == 0 ? 150 : 220, GamutColors[i]), i == 2 ? 1.4f : 1.1f) { LineJoin = LineJoin.Round };
                g.DrawPolygon(pen, GamutTriangles[i].Points.Select(At).ToArray());
            }
            // Bianco D65.
            PointF white = At((.3127f, .3290f));
            using (var ring = new Pen(Color.FromArgb(230, 255, 255, 255), 1f)) g.DrawEllipse(ring, white.X - 3, white.Y - 3, 6, 6);

            // Legenda: i tre triangoli e quanta parte dell'immagine sta in ciascuno.
            int legendX = plot.Right + 20, legendY = panel.Top + 22;
            double[] shares = { live.GamutRec709, live.GamutP3, live.GamutRec2020 };
            string[] captions = { T("entro", "within"), T("fino a", "up to"), T("oltre, in", "beyond, in") };
            for (int i = 0; i < GamutTriangles.Length; i++)
            {
                int y = legendY + i * 46;
                using (var swatch = new Pen(GamutColors[i], 2f)) g.DrawLine(swatch, legendX, y + 9, legendX + 16, y + 9);
                TextRenderer.DrawText(g, captions[i] + " " + GamutTriangles[i].Name, smallFont, new Rectangle(legendX + 24, y, panel.Right - legendX - 30, 18), ScopeText, ScopeBack, Line);
                TextRenderer.DrawText(g, live.GamutMeasured ? Share(shares[i]) : "—", sectionFont, new Rectangle(legendX + 24, y + 18, panel.Right - legendX - 30, 20), Color.FromArgb(232, 236, 240), ScopeBack, Line);
            }
            using (var ring = new Pen(Color.FromArgb(230, 255, 255, 255), 1f)) g.DrawEllipse(ring, legendX + 5, legendY + 3 * 46 + 6, 6, 6);
            TextRenderer.DrawText(g, T("bianco D65", "D65 white"), smallFont, new Rectangle(legendX + 24, legendY + 3 * 46, panel.Right - legendX - 30, 18), ScopeText, ScopeBack, Line);
        }

        private void DrawFalseColor(Graphics g, Font sectionFont, Font smallFont)
        {
            Rectangle panel = PicturePanel;
            TextRenderer.DrawText(g, T("Falsi colori", "False colour"), sectionFont, new Rectangle(panel.Left, panel.Top - 26, 300, 22), Theme.Text, Line);
            ScopePanel(g, panel);
            if (_pictureBitmap != null)
            {
                float aspect = _pictureBitmap.Width / (float)_pictureBitmap.Height;
                float width = panel.Width, height = width / aspect;
                if (height > panel.Height) { height = panel.Height; width = height * aspect; }
                var target = new RectangleF(panel.Left + (panel.Width - width) / 2, panel.Top + (panel.Height - height) / 2, width, height);
                var state = g.Save();
                using (var clip = Rounded(panel, 8)) g.SetClip(clip, CombineMode.Intersect);
                DrawScaled(g, _pictureBitmap, target, new RectangleF(0, 0, _pictureBitmap.Width, _pictureBitmap.Height));
                g.Restore(state);
            }

            // Legenda delle fasce, dalla piu' luminosa.
            Rectangle legend = LegendArea;
            var bands = HdrAnalyzer.FalseColorBands;
            int rowHeight = legend.Height / bands.Length;
            for (int i = 0; i < bands.Length; i++)
            {
                var band = bands[bands.Length - 1 - i];
                int y = legend.Top + i * rowHeight;
                using (var swatch = new SolidBrush(Color.FromArgb(unchecked((int)band.Argb))))
                using (var shape = Rounded(new Rectangle(legend.Left, y + (rowHeight - 12) / 2, 12, 12), 3))
                    g.FillPath(swatch, shape);
                string text = band.FromNits <= 0 ? "< " + Nits(bands[1].FromNits) : "≥ " + Nits(band.FromNits);
                TextRenderer.DrawText(g, text + " nit", smallFont, new Rectangle(legend.Left + 20, y, legend.Width - 20, rowHeight), Theme.SubtleText, Line);
            }
        }

        private void DrawHistory(Graphics g, Font sectionFont, Font smallFont, double top, double topPq)
        {
            Rectangle panel = HistoryPanel;
            TextRenderer.DrawText(g, T("Ultimo minuto", "Last minute"), sectionFont, new Rectangle(panel.Left, panel.Top - 26, 200, 22), Theme.Text, Line);
            ScopePanel(g, panel);
            var plot = new Rectangle(panel.Left + 46, panel.Top + 14, panel.Width - 46 - 14, panel.Height - 14 - 28);
            using var gridPen = new Pen(ScopeLine);
            foreach (double tick in NitTicks.Where(tick => tick < top).Append(top))
            {
                int y = YOf(plot, tick, topPq);
                if (tick < top && y - plot.Top < 16) continue;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, Nits(tick), smallFont, new Rectangle(panel.Left + 4, y - 9, 36, 18), ScopeText, ScopeBack, Line | TextFormatFlags.Right);
            }
            TextRenderer.DrawText(g, T("60 s fa", "60 s ago"), smallFont, new Rectangle(plot.Left, plot.Bottom + 6, 80, 16), ScopeText, ScopeBack, Line);
            TextRenderer.DrawText(g, T("ora", "now"), smallFont, new Rectangle(plot.Right - 80, plot.Bottom + 6, 80, 16), ScopeText, ScopeBack, Line | TextFormatFlags.Right);
            if (_liveHistory.Count < 2) return;
            long now = _liveHistory[^1].Stamp;
            var peak = new List<PointF>(_liveHistory.Count);
            var average = new List<PointF>(_liveHistory.Count);
            foreach (var entry in _liveHistory)
            {
                double age = System.Diagnostics.Stopwatch.GetElapsedTime(entry.Stamp, now).TotalSeconds;
                if (age > 60) continue;
                float x = plot.Right - (float)(age / 60 * plot.Width);
                peak.Add(new PointF(x, YOf(plot, entry.Peak, topPq)));
                average.Add(new PointF(x, YOf(plot, entry.Average, topPq)));
            }
            if (peak.Count < 2) return;
            var state = g.Save();
            g.SetClip(Rectangle.Inflate(plot, 0, 1));
            using (var area = new GraphicsPath())
            {
                area.AddLines(average.ToArray());
                area.AddLine(average[^1].X, plot.Bottom, average[0].X, plot.Bottom);
                area.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(60, AverageColor));
                g.FillPath(fill, area);
            }
            using (var pen = new Pen(AverageColor, 1.8f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, average.ToArray());
            using (var pen = new Pen(PeakColor, 1.5f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, peak.ToArray());
            g.Restore(state);
        }

        private void DrawVectorscope(Graphics g, Font sectionFont, Font smallFont)
        {
            Rectangle panel = VectorPanel;
            TextRenderer.DrawText(g, T("Vettorscopio", "Vectorscope"), sectionFont, new Rectangle(panel.Left, panel.Top - 26, 200, 22), Theme.Text, Line);
            ScopePanel(g, panel);
            int diameter = Math.Min(panel.Width, panel.Height) - 24;
            var circle = new Rectangle(panel.Left + (panel.Width - diameter) / 2, panel.Top + (panel.Height - diameter) / 2, diameter, diameter);
            if (_vectorBitmap != null)
            {
                var state = g.Save();
                using (var clip = new GraphicsPath()) { clip.AddEllipse(circle); g.SetClip(clip, CombineMode.Intersect); }
                DrawScaled(g, _vectorBitmap, circle, new RectangleF(0, 0, _vectorBitmap.Width, _vectorBitmap.Height));
                g.Restore(state);
            }
            using var line = new Pen(ScopeLine);
            using var rim = new Pen(Color.FromArgb(90, 255, 255, 255));
            float cx = circle.Left + diameter / 2f, cy = circle.Top + diameter / 2f, radius = diameter / 2f;
            g.DrawEllipse(rim, circle);
            g.DrawEllipse(line, cx - radius / 2, cy - radius / 2, radius, radius);
            g.DrawLine(line, cx - radius, cy, cx + radius, cy);
            g.DrawLine(line, cx, cy - radius, cx, cy + radius);
            // Direzione delle sei tinte (BT.2020): una tacca sul bordo e la sigla.
            const double kr = 0.2627, kb = 0.0593, kg = 1 - kr - kb;
            var hues = new[] { ("R", 1.0, 0.0, 0.0), ("Y", 1.0, 1.0, 0.0), ("G", 0.0, 1.0, 0.0), ("C", 0.0, 1.0, 1.0), ("B", 0.0, 0.0, 1.0), ("M", 1.0, 0.0, 1.0) };
            foreach (var (name, r, gg, b) in hues)
            {
                double luma = kr * r + kg * gg + kb * b, cb = (b - luma) / (2 * (1 - kb)), cr = (r - luma) / (2 * (1 - kr));
                double length = Math.Max(1e-6, Math.Sqrt(cb * cb + cr * cr));
                float dx = (float)(cb / length), dy = (float)(-cr / length);
                using var tick = new Pen(Color.FromArgb(255, (int)(70 + 185 * r), (int)(70 + 185 * gg), (int)(70 + 185 * b)), 2f);
                g.DrawLine(tick, cx + dx * (radius - 7), cy + dy * (radius - 7), cx + dx * radius, cy + dy * radius);
                TextRenderer.DrawText(g, name, smallFont, new Rectangle((int)(cx + dx * (radius - 19)) - 8, (int)(cy + dy * (radius - 19)) - 8, 16, 16), ScopeText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private void DisposeLive()
        {
            _scopeTimer.Dispose();
            _waveBitmap?.Dispose(); _chromaBitmap?.Dispose(); _vectorBitmap?.Dispose(); _pictureBitmap?.Dispose();
        }
    }
}
