using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Grafici del Cinecore Audio Engine nel brano a schermo intero: copia di HUD/MusicFullscreenView.Charts.cs di Windows.
// Copertina a sinistra, a destra un grafico alla volta disegnato in bianco sullo sfondo, senza riquadri. In alto il nome
// del grafico con la freccia (al clic gli altri scendono sotto, senza riquadri), la riga della legenda con la lettura dal
// vivo a destra, il grafico, e sotto (tutti tranne Info) i quattro numeri che contano. L'analisi (FFT, BS.1770 / EBU R128,
// true peak 4x) e' LoopbackSampler di Windows alimentato dal motore; i dati si raccolgono a ogni misura nuova come in
// FeedMetrics (salite e discese a tempo, letture calme due volte al secondo).
internal sealed partial class LibraryView
{
    private static readonly string[] FsTabs = { "Spettro", "VU", "Spettrogramma", "Onda", "Loudness", "Dinamica", "Stereo", "Livelli", "Info" };
    private const int TabSpectrum = 0, TabVu = 1, TabGram = 2, TabWave = 3, TabLoudness = 4, TabDynamics = 5, TabStereo = 6, TabLevels = 7, TabInfo = 8;
    private int _fsTab;

    // ---------------------------------------------------------------- dati (FeedMetrics di Windows)
    private LoopbackSampler.AudioMetrics? _fedMetrics;
    private double[]? _specSmooth, _specPeak;
    private const int SpectrumPoints = 180;
    private readonly Queue<(float Peak, float Rms)> _waveHistory = new();
    private readonly Queue<(float M, float S)> _loudHistory = new();
    private readonly Queue<(float L, float R, float Plr)> _dynHistory = new();
    private float[] _scopeL = Array.Empty<float>(), _scopeR = Array.Empty<float>();
    private double _rmsL = -90, _rmsR = -90, _peakL = -90, _peakR = -90, _holdL = -90, _holdR = -90;
    private double _corr, _balance, _lufsI = double.NaN, _lufsS = double.NaN, _lufsM = double.NaN, _lra = double.NaN, _tp = double.NaN, _plr = double.NaN;
    private double _crestL = double.NaN, _crestR = double.NaN, _dominantHz = double.NaN;
    private double _rmsPowL, _rmsPowR;
    private bool _silent = true;
    private long _lastMetricsMs, _lastLoudSample;
    private const int GramColumns = 420;
    private Avalonia.Media.Imaging.WriteableBitmap? _gram;

    private static double Db(double linear) => linear <= 1e-6 ? -120 : 20 * Math.Log10(linear);
    private static string Num(double v, string format = "0.0") => double.IsFinite(v) && v > -99 ? v.ToString(format, CultureInfo.CurrentCulture) : "—";

    /// <summary>Raccoglie l'ultima misura del campionatore (una volta per misura, non per fotogramma).</summary>
    private void FeedCharts()
    {
        var m = _audio.Analysis;
        if (m == null || ReferenceEquals(m, _fedMetrics)) return;
        _fedMetrics = m;
        long now = Environment.TickCount64;
        double dt = _lastMetricsMs > 0 ? Math.Clamp((now - _lastMetricsMs) / 1000.0, 0.005, 0.2) : 0.033;
        _lastMetricsMs = now;
        _silent = m.IsSilent;
        // Spettro: punti in scala logaritmica 20 Hz - 20 kHz, salita immediata e discesa a 24 dB/s, picchi a 6 dB/s.
        var spectrum = m.SpectrumDb;
        if (spectrum.Length > 4 && m.SampleRate > 0)
        {
            _specSmooth ??= Enumerable.Repeat(-120.0, SpectrumPoints).ToArray();
            _specPeak ??= Enumerable.Repeat(-120.0, SpectrumPoints).ToArray();
            double binHz = m.SampleRate / 2.0 / (spectrum.Length - 1);
            for (int p = 0; p < SpectrumPoints; p++)
            {
                double f0 = 20 * Math.Pow(1000, p / (double)SpectrumPoints), f1 = 20 * Math.Pow(1000, (p + 1) / (double)SpectrumPoints);
                int b0 = Math.Clamp((int)Math.Floor(f0 / binHz), 1, spectrum.Length - 1), b1 = Math.Clamp((int)Math.Ceiling(f1 / binHz), b0, spectrum.Length - 1);
                double v = -120;
                if (f1 - f0 < binHz * 2)
                {
                    // Sui bassi i punti sono piu' fitti delle righe dell'analisi: si interpola, niente gradini.
                    double pos = Math.Sqrt(f0 * f1) / binHz;
                    int i0 = Math.Clamp((int)Math.Floor(pos), 1, spectrum.Length - 2);
                    double frac = Math.Clamp(pos - i0, 0, 1);
                    v = spectrum[i0] + (spectrum[i0 + 1] - spectrum[i0]) * frac;
                }
                else for (int b = b0; b <= b1; b++) v = Math.Max(v, spectrum[b]);
                if (!double.IsFinite(v)) v = -120;
                _specSmooth[p] = v > _specSmooth[p] ? v : Math.Max(v, _specSmooth[p] - 24 * dt);
                _specPeak[p] = _specSmooth[p] > _specPeak[p] ? _specSmooth[p] : _specPeak[p] - 6 * dt;
            }
            PushGramColumn(_specSmooth);
        }
        // Balistica da misuratore: l'RMS di un blocco (~30 ms) tremava; ora media di potenza su 300 ms. I picchi salgono
        // subito e scendono a 20 dB/s, la correlazione si media su 300 ms.
        double k = 1 - Math.Exp(-dt / 0.3);
        _rmsPowL += (m.RmsL * m.RmsL - _rmsPowL) * k; _rmsPowR += (m.RmsR * m.RmsR - _rmsPowR) * k;
        _rmsL = Db(Math.Sqrt(_rmsPowL)); _rmsR = Db(Math.Sqrt(_rmsPowR));
        _peakL = Math.Max(Db(m.PeakL), _peakL - 20 * dt); _peakR = Math.Max(Db(m.PeakR), _peakR - 20 * dt);
        _holdL = Math.Max(_peakL, _holdL - 10 * dt); _holdR = Math.Max(_peakR, _holdR - 10 * dt);
        FeedVu(m, now);
        _corr += ((double.IsFinite(m.Correlation) ? m.Correlation : 0) - _corr) * k;
        _balance += ((double.IsFinite(m.Balance) ? m.Balance : 0) - _balance) * k;
        // True peak: il massimo del brano (R128). Prima il picco dell'ultimo blocco: il numero cambiava ~30 volte al secondo.
        _lufsI = m.LufsI; _lufsS = m.LufsS; _lufsM = m.LufsM; _lra = m.Lra; _tp = m.ProgrammeDbTp; _plr = m.PlrDb;
        _crestL = m.CrestL_dB; _crestR = m.CrestR_dB; _dominantHz = m.DominantHz;
        if (m.ScopeL.Length > 0) { _scopeL = (float[])m.ScopeL.Clone(); _scopeR = (float[])m.ScopeR.Clone(); }
        _waveHistory.Enqueue(((float)Math.Max(m.PeakL, m.PeakR), (float)Math.Max(m.RmsL, m.RmsR)));
        while (_waveHistory.Count > 300) _waveHistory.Dequeue();               // ~10 s
        if (now - _lastLoudSample >= 100)
        {
            _lastLoudSample = now;
            _loudHistory.Enqueue(((float)m.LufsM, (float)m.LufsS));
            while (_loudHistory.Count > 600) _loudHistory.Dequeue();          // 60 s
            _dynHistory.Enqueue((m.IsSilent ? float.NaN : (float)m.CrestL_dB, m.IsSilent ? float.NaN : (float)m.CrestR_dB, (float)m.PlrDb));
            while (_dynHistory.Count > 600) _dynHistory.Dequeue();
        }
        UpdateReadouts(now);
    }

    /// <summary>Una colonna nuova a destra dello spettrogramma; il resto scorre di un pixel a sinistra.</summary>
    private void PushGramColumn(double[] db)
    {
        try
        {
            _gram ??= new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(GramColumns, SpectrumPoints), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using var fb = _gram.Lock();
            var accent = UiTheme.Accent;
            unsafe
            {
                for (int y = 0; y < SpectrumPoints; y++)
                {
                    uint* row = (uint*)((byte*)fb.Address + y * fb.RowBytes);
                    Buffer.MemoryCopy(row + 1, row, (GramColumns - 1) * 4L, (GramColumns - 1) * 4L);
                    // Riga 0 = acuti (in alto).
                    int p = SpectrumPoints - 1 - y;
                    double t = Math.Clamp((db[Math.Clamp(p, 0, SpectrumPoints - 1)] + 90) / 80.0, 0, 1);
                    double a = Math.Pow(t, 1.5), w = Math.Pow(t, 2.2);
                    // Dal colore d'accento (deboli) al bianco (forti).
                    double r = accent.R + (255 - accent.R) * w, g = accent.G + (255 - accent.G) * w, b = accent.B + (255 - accent.B) * w;
                    uint A = (uint)Math.Round(a * 235);
                    row[GramColumns - 1] = (A << 24) | ((uint)(r * A / 255) << 16) | ((uint)(g * A / 255) << 8) | (uint)(b * A / 255);
                }
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- letture calme
    // I valori scritti cambiano due volte al secondo e la frequenza e' la mediana degli ultimi istanti (non tremola).
    private readonly Queue<double> _dominantRecent = new();
    private long _readoutAt;
    private double _shownDominant = double.NaN, _shownCrest = double.NaN, _shownShort = double.NaN, _shownRms = double.NaN;
    private double _shownRmsL = double.NaN, _shownRmsR = double.NaN, _shownCorr, _shownBalance;

    private void UpdateReadouts(long now)
    {
        if (double.IsFinite(_dominantHz) && _dominantHz > 0 && !_silent)
        {
            _dominantRecent.Enqueue(_dominantHz);
            while (_dominantRecent.Count > 24) _dominantRecent.Dequeue();
        }
        if (now - _readoutAt < 500) return;
        _readoutAt = now;
        if (_silent) { _shownDominant = double.NaN; _shownCrest = double.NaN; _shownRms = double.NaN; _shownRmsL = _shownRmsR = double.NaN; _dominantRecent.Clear(); }
        else
        {
            if (_dominantRecent.Count > 0)
            {
                var sorted = _dominantRecent.OrderBy(v => v).ToArray();
                double median = sorted[sorted.Length / 2];
                _shownDominant = median >= 1000 ? Math.Round(median / 100) * 100 : Math.Max(10, Math.Round(median / 10) * 10);
            }
            _shownCrest = double.IsFinite(_crestL) && double.IsFinite(_crestR) ? (_crestL + _crestR) / 2 : double.NaN;
            _shownRms = Math.Max(_rmsL, _rmsR);
            _shownRmsL = _rmsL; _shownRmsR = _rmsR;
        }
        _shownCorr = _corr; _shownBalance = _balance;
        _shownShort = _lufsS;
    }

    /// <summary>Entrando nei grafici si parte dalla prima scheda, con i dati azzerati.</summary>
    private void ResetCharts()
    {
        _fsTab = TabSpectrum; _specSmooth = null; _specPeak = null; _rmsPowL = _rmsPowR = 0; _peakL = _peakR = -90; _corr = _balance = 0; _fsMenuOpen = false; _fsMenuStart = -1; _chartFadeStart = -1;
    }

    // ---------------------------------------------------------------- testo in pixel
    private static FormattedText Ft(string text, double px, Typeface face, Color color) =>
        new(L10n.T(text), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, px, B(color));
    private static double TextW(string text, double px, Typeface face) => Ft(text, px, face, Colors.White).WidthIncludingTrailingWhitespace;
    private static double LineH(double px) => Math.Ceiling(px * 1.22);
    private static void Txt(DrawingContext ctx, string text, double px, Typeface face, Color color, double x, double y)
    {
        if (!string.IsNullOrEmpty(text)) ctx.DrawText(Ft(text, px, face, color), new Point(x, y));
    }
    private static Color W(byte a) => Color.FromArgb(a, 255, 255, 255);

    // ---------------------------------------------------------------- disegno
    private long _chartFadeStart = -1;
    private double ChartFade => _chartFadeStart < 0 ? 1 : Math.Clamp((Environment.TickCount64 - _chartFadeStart) / 260.0, 0, 1);
    private bool ChartsAnimating => _fsMenuStart >= 0 || (_chartFadeStart >= 0 && ChartFade < 1);

    private void DrawFsAnalysis(DrawingContext ctx, Rect column, double alpha)
    {
        FeedCharts();
        _audio.AnalysisEnabled = _fsTab != TabInfo;
        if (_chartFadeStart >= 0 && ChartFade >= 1) _chartFadeStart = -1;
        alpha *= ChartFade;
        double menuAlpha = alpha;
        byte AMenu(double v) => (byte)Math.Clamp(Math.Round(v * menuAlpha), 0, 255);
        double small = Math.Max(10, FU(17)), medium = Math.Max(12, FU(26)), big = Math.Max(16, FU(40));
        double smallH = LineH(small), mediumH = LineH(medium), bigH = LineH(big);

        // Una voce sola: il grafico aperto e una freccia. Al clic le altre schede scendono sotto, senza riquadri.
        double tabTop = column.Y + column.Height * .06;
        double pickerPx = Math.Max(14, FU(30)), listPx = Math.Max(12, FU(23));
        double pickerW = TextW(FsTabs[_fsTab], pickerPx, DisplaySemibold), pickerH = LineH(pickerPx);
        var pickerRect = new Rect(column.X - FU(8), tabTop - FU(8), pickerW + FU(48), pickerH + FU(16));
        bool pickerHot = _fsMenuOpen || Hover(pickerRect);
        Txt(ctx, FsTabs[_fsTab], pickerPx, DisplaySemibold, W(AMenu(255)), column.X, tabTop);
        {
            double cx = column.X + pickerW + FU(18), cy = tabTop + pickerH / 2 + FU(1), z = Math.Max(4, FU(7));
            double open = FsMenuProgress(), dy = z / 2 * (1 - 2 * open);
            var pen = new Pen(B(W(AMenu(pickerHot ? 255 : 170))), Math.Max(1.6, FU(2.4)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var chevron = new StreamGeometry();
            using (var g = chevron.Open()) { g.BeginFigure(new Point(cx - z, cy - dy), false); g.LineTo(new Point(cx, cy + dy)); g.LineTo(new Point(cx + z, cy - dy)); g.EndFigure(false); }
            ctx.DrawGeometry(null, pen, chevron);
        }
        _hits.Add(new Hit(pickerRect, "musicfs-picker"));
        // Elenco aperto: il grafico sotto si attenua, cosi' le voci si leggono senza velo ne' riquadri.
        alpha *= 1 - 0.9 * FsMenuProgress();
        byte A(double v) => (byte)Math.Clamp(Math.Round(v * alpha), 0, 255);

        double numbersH = bigH + smallH + FU(6);
        bool info = _fsTab == TabInfo;
        // Riga della legenda (tutta sua: non tocca mai il grafico) e grafico sotto.
        var legendRow = new Rect(column.X, tabTop + FU(64), column.Width, Math.Max(mediumH, smallH) + FU(4));
        double chartBottom = info ? column.Bottom : column.Bottom - numbersH - FU(44);
        var chart = new Rect(column.X, legendRow.Bottom + FU(26), column.Width, Math.Max(0, chartBottom - legendRow.Bottom - FU(26)));
        if (chart.Height >= FU(60))
        {
            var whole = new Rect(column.X, legendRow.Y, column.Width, Math.Max(1, chart.Bottom - legendRow.Y));
            // Info centrato in altezza su tutta la colonna (come la copertina a fianco), mai sopra il menu delle schede.
            if (info) DrawChartsInfo(ctx, new Rect(column.X, legendRow.Y, column.Width, column.Bottom - legendRow.Y), A, small, medium, column.Y + column.Height / 2);
            else if (_fedMetrics == null)
                FsText(ctx, _audio.IsPlaying ? "Analisi in corso…" : "Avvia il brano per vedere i grafici", Math.Max(12, FU(22)), DisplayRegular, W(A(170)),
                    new Rect(chart.X, chart.Y + chart.Height / 2 - FU(20), chart.Width, FU(40)), TextAlignment.Center);
            else switch (_fsTab)
            {
                case TabSpectrum: DrawChartSpectrum(ctx, legendRow, chart, A, small, medium); break;
                case TabVu: DrawVuPair(ctx, whole, alpha); break;
                case TabGram: DrawChartSpectrogram(ctx, legendRow, chart, A, small, medium, alpha); break;
                case TabWave: DrawChartWave(ctx, legendRow, chart, A, small, medium); break;
                case TabLoudness: DrawChartLoudness(ctx, legendRow, chart, A, small, medium); break;
                case TabDynamics: DrawChartDynamics(ctx, legendRow, chart, A, small, medium); break;
                case TabStereo: DrawChartStereo(ctx, whole, A, small, medium); break;
                default: DrawChartLevels(ctx, whole, A, small, medium); break;
            }
            if (!info)
            {
                // I quattro numeri distribuiti su tutta la larghezza: il primo sul bordo sinistro, l'ultimo su quello destro.
                var numbers = new (string Value, string Label)[]
                {
                    (Num(_lufsI), "LUFS integrato"), (Num(_tp), "True peak (dBTP)"), (Num(_lra), "Gamma (LU)"), (Num(_plr), "Dinamica (PLR)"),
                };
                var widths = numbers.Select(n => Math.Max(TextW(n.Value, big, DisplaySemibold), TextW(n.Label, small, DisplayRegular))).ToArray();
                double spare = Math.Max(0, column.Width - widths.Sum()) / Math.Max(1, numbers.Length - 1);
                double nx = column.X, ny = column.Bottom - numbersH;
                for (int i = 0; i < numbers.Length; i++)
                {
                    Txt(ctx, numbers[i].Value, big, DisplaySemibold, W(A(245)), nx, ny);
                    Txt(ctx, numbers[i].Label, small, DisplayRegular, W(A(150)), nx, ny + bigH + FU(4));
                    nx += widths[i] + spare;
                }
            }
        }
        DrawFsTabMenu(ctx, column, tabTop + pickerH + FU(18), listPx, AMenu);
    }

    private enum Swatch { Line, Faint, Accent, Dots, Ramp }

    /// <summary>
    /// Legenda sopra il grafico, sempre sulla stessa riga: a sinistra le voci (segno + nome), a destra una lettura
    /// (valore in grassetto e nome piccolo davanti). Le voci si spaziano in modo uniforme e non coprono mai il grafico.
    /// </summary>
    private void DrawLegend(DrawingContext ctx, Rect row, Func<double, byte> A, double small, double medium,
        (string Label, Swatch Kind)[] items, string? value = null, string? valueLabel = null)
    {
        double baseline = row.Bottom - FU(2);
        double smallTop = baseline - LineH(small), mediumTop = baseline - LineH(medium);
        double x = row.X;
        var accent = UiTheme.Accent;
        foreach (var (text, kind) in items)
        {
            double sw = FU(22), sy = smallTop + LineH(small) * .55;
            switch (kind)
            {
                case Swatch.Ramp:
                    var ramp = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(Color.FromArgb(A(60), accent.R, accent.G, accent.B), 0), new GradientStop(W(A(250)), 1) },
                    };
                    ctx.DrawRectangle(ramp, null, new Rect(x, sy - FU(4), sw, FU(8)), FU(4), FU(4));
                    break;
                case Swatch.Dots:
                    for (int i = 0; i < 3; i++)
                        ctx.DrawEllipse(B(Color.FromArgb(A(220), accent.R, accent.G, accent.B)), null, new Point(x + i * sw / 2.6 + FU(2.5), sy), FU(2.5), FU(2.5));
                    break;
                default:
                    var color = kind == Swatch.Accent ? Color.FromArgb(A(240), accent.R, accent.G, accent.B) : W(A(kind == Swatch.Faint ? 110 : 240));
                    ctx.DrawRectangle(B(color), null, new Rect(x, Math.Round(sy - FU(1.5)), sw, Math.Max(2, FU(3))), FU(2), FU(2));
                    break;
            }
            x += sw + FU(10);
            Txt(ctx, text, small, DisplayRegular, W(A(165)), x, smallTop);
            x += TextW(text, small, DisplayRegular) + FU(34);
        }
        if (!string.IsNullOrEmpty(value))
        {
            double vw = TextW(value, medium, DisplaySemibold), vx = row.Right - vw;
            Txt(ctx, value, medium, DisplaySemibold, W(A(245)), vx, mediumTop);
            if (!string.IsNullOrEmpty(valueLabel))
                Txt(ctx, valueLabel, small, DisplayRegular, W(A(165)), vx - FU(12) - TextW(valueLabel, small, DisplayRegular), smallTop);
        }
    }

    private static double FrequencyX(Rect r, double hz) => r.X + Math.Log(hz / 20) / Math.Log(1000) * r.Width;
    private static string Hz(double hz) => hz >= 1000 ? (hz / 1000).ToString(hz % 1000 == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + "k" : hz.ToString("0", CultureInfo.CurrentCulture);

    /// <summary>Spline cardinale come DrawCurve di GDI+ (tensione 0,4): passa per i punti senza spigoli.</summary>
    private static void CurveThrough(StreamGeometryContext g, Point[] pts, double tension)
    {
        double k = tension / 3;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(pts.Length - 1, i + 2)];
            g.CubicBezierTo(new Point(p1.X + (p2.X - p0.X) * k, p1.Y + (p2.Y - p0.Y) * k), new Point(p2.X - (p3.X - p1.X) * k, p2.Y - (p3.Y - p1.Y) * k), p2);
        }
    }

    private void DrawChartSpectrum(DrawingContext ctx, Rect legend, Rect r, Func<double, byte> A, double small, double medium)
    {
        string dominant = double.IsFinite(_shownDominant)
            ? (_shownDominant >= 1000 ? (_shownDominant / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " kHz" : _shownDominant.ToString("0", CultureInfo.CurrentCulture) + " Hz") : "—";
        DrawLegend(ctx, legend, A, small, medium, new[] { ("livello", Swatch.Line), ("picchi", Swatch.Dots) }, dominant, "frequenza dominante");
        // Scala delle frequenze sotto il grafico, fuori dall'area disegnata.
        double axisH = LineH(small) + FU(12);
        var plot = new Rect(r.X, r.Y, r.Width, Math.Max(1, r.Height - axisH));
        const double floor = -90;
        double Y(double db) => plot.Bottom - Math.Clamp((db - floor) / -floor, 0, 1) * plot.Height;
        var guide = new Pen(B(W(A(30))), 1);
        foreach (double hz in new[] { 20.0, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
        {
            double gx = FrequencyX(plot, hz);
            if (hz is 100 or 1000 or 10000) ctx.DrawLine(guide, new Point(gx, plot.Y), new Point(gx, plot.Bottom));
            string text = Hz(hz);
            double w = TextW(text, small, DisplayRegular);
            Txt(ctx, text, small, DisplayRegular, W(A(130)), Math.Clamp(gx - w / 2, plot.X, plot.Right - w), plot.Bottom + FU(10));
        }
        ctx.DrawLine(guide, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));
        if (_specSmooth == null) return;
        var pts = new Point[SpectrumPoints];
        for (int p = 0; p < SpectrumPoints; p++) pts[p] = new Point(plot.X + p * plot.Width / (SpectrumPoints - 1), Y(_specSmooth[p]));
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(plot.X, plot.Bottom), true);
            g.LineTo(pts[0]);
            CurveThrough(g, pts, 0.4);
            g.LineTo(new Point(plot.Right, plot.Bottom));
            g.EndFigure(true);
        }
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(W(A(110)), 0), new GradientStop(W(A(8)), 1) },
        };
        using (ctx.PushClip(plot.Inflate(new Thickness(0, 2, 0, 0)))) ctx.DrawGeometry(fill, null, area);
        var line = new StreamGeometry();
        using (var g = line.Open()) { g.BeginFigure(pts[0], false); CurveThrough(g, pts, 0.4); g.EndFigure(false); }
        ctx.DrawGeometry(null, new Pen(B(W(A(235))), Math.Max(1.5, FU(2.2)), lineJoin: PenLineJoin.Round), line);
        if (_specPeak != null)
        {
            var accent = UiTheme.Accent;
            var peak = B(Color.FromArgb(A(170), accent.R, accent.G, accent.B));
            for (int p = 0; p < SpectrumPoints; p += 2)
                ctx.DrawEllipse(peak, null, new Point(plot.X + p * plot.Width / (SpectrumPoints - 1), Y(_specPeak[p])), FU(2), FU(2));
        }
    }

    private void DrawChartSpectrogram(DrawingContext ctx, Rect legend, Rect r, Func<double, byte> A, double small, double medium, double alpha)
    {
        DrawLegend(ctx, legend, A, small, medium, new[] { ("da debole a forte", Swatch.Ramp) }, "14 s", "ultimi");
        // Frequenze in una colonna a sinistra, fuori dall'immagine.
        double gutter = FU(52);
        var plot = new Rect(r.X + gutter, r.Y, Math.Max(1, r.Width - gutter), r.Height);
        if (_gram != null)
            using (ctx.PushOpacity(alpha))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality }))
                ctx.DrawImage(_gram, new Rect(0, 0, GramColumns, SpectrumPoints), plot);
        var guide = new Pen(B(W(A(26))), 1);
        foreach (double hz in new[] { 20.0, 100, 1000, 10000, 20000 })
        {
            double gy = plot.Bottom - Math.Log(hz / 20) / Math.Log(1000) * plot.Height;
            if (hz is 100 or 1000 or 10000) ctx.DrawLine(guide, new Point(plot.X, gy), new Point(plot.Right, gy));
            string text = Hz(hz);
            double h = LineH(small);
            Txt(ctx, text, small, DisplayRegular, W(A(130)), plot.X - FU(12) - TextW(text, small, DisplayRegular), Math.Clamp(gy - h / 2, plot.Y, plot.Bottom - h));
        }
    }

    private void DrawChartWave(DrawingContext ctx, Rect legend, Rect r, Func<double, byte> A, double small, double medium)
    {
        DrawLegend(ctx, legend, A, small, medium, new[] { ("livello medio", Swatch.Line), ("picco", Swatch.Faint) }, Num(_shownRms) + " dB", "ora");
        double mid = r.Y + r.Height / 2;
        ctx.DrawLine(new Pen(B(W(A(40))), 1), new Point(r.X, mid), new Point(r.Right, mid));
        var items = _waveHistory.ToArray();
        if (items.Length == 0) return;
        double step = r.Width / 300;
        var peak = new Pen(B(W(A(90))), Math.Max(1, step * .7));
        var rms = new Pen(B(W(A(235))), Math.Max(1, step * .7));
        for (int i = 0; i < items.Length; i++)
        {
            double x = r.Right - (items.Length - i) * step;
            double hp = Math.Clamp(items[i].Peak, 0, 1) * r.Height / 2, hr = Math.Clamp(items[i].Rms * 1.4, 0, 1) * r.Height / 2;
            ctx.DrawLine(peak, new Point(x, mid - hp), new Point(x, mid + hp));
            ctx.DrawLine(rms, new Point(x, mid - hr), new Point(x, mid + hr));
        }
    }

    private static Point[] HistoryLine<T>(T[] items, Rect r, Func<T, float> pick, Func<double, double> y, float minimum)
    {
        double step = r.Width / 600;
        var list = new List<Point>(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            float v = pick(items[i]);
            if (!float.IsFinite(v) || v < minimum) continue;
            list.Add(new Point(r.Right - (items.Length - i) * step, y(v)));
        }
        return list.ToArray();
    }

    private static void Polyline(DrawingContext ctx, Pen pen, Point[] pts)
    {
        if (pts.Length < 2) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open()) { g.BeginFigure(pts[0], false); for (int i = 1; i < pts.Length; i++) g.LineTo(pts[i]); g.EndFigure(false); }
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>Linee guida orizzontali con il valore in una colonna a destra, fuori dal grafico.</summary>
    private void DrawRightScale(DrawingContext ctx, Rect plot, Func<double, byte> A, double small, Func<double, double> y, params (double Value, string Text)[] marks)
    {
        var guide = new Pen(B(W(A(42))), 1, new DashStyle(new double[] { 4, 3 }, 0));
        foreach (var (value, text) in marks)
        {
            double gy = y(value);
            ctx.DrawLine(guide, new Point(plot.X, gy), new Point(plot.Right, gy));
            Txt(ctx, text, small, DisplayRegular, W(A(130)), plot.Right + FU(14), gy - LineH(small) / 2);
        }
    }

    private void DrawChartLoudness(DrawingContext ctx, Rect legend, Rect r, Func<double, byte> A, double small, double medium)
    {
        DrawLegend(ctx, legend, A, small, medium, new[] { ("breve (3 s)", Swatch.Line), ("momentanea (0,4 s)", Swatch.Faint) }, Num(_shownShort) + " LUFS", "ora");
        var plot = new Rect(r.X, r.Y, Math.Max(1, r.Width - FU(96)), r.Height);
        const double top = 0, bottom = -40;
        double Y(double v) => plot.Y + Math.Clamp((top - v) / (top - bottom), 0, 1) * plot.Height;
        DrawRightScale(ctx, plot, A, small, Y, (-14, "−14 LUFS"), (-23, "−23 LUFS"));
        var items = _loudHistory.ToArray();
        if (items.Length < 2) return;
        Polyline(ctx, new Pen(B(W(A(110))), Math.Max(1, FU(1.4))), HistoryLine(items, plot, i => i.M, Y, -70));
        Polyline(ctx, new Pen(B(W(A(240))), Math.Max(1.5, FU(2.6)), lineJoin: PenLineJoin.Round), HistoryLine(items, plot, i => i.S, Y, -70));
    }

    /// <summary>Fattore di cresta (picco - medio) dei due canali e PLR nel tempo: alto = dinamico, basso = compresso.</summary>
    private void DrawChartDynamics(DrawingContext ctx, Rect legend, Rect r, Func<double, byte> A, double small, double medium)
    {
        DrawLegend(ctx, legend, A, small, medium, new[] { ("cresta L", Swatch.Line), ("cresta R", Swatch.Faint), ("PLR", Swatch.Accent) }, Num(_shownCrest) + " dB", "cresta ora");
        var plot = new Rect(r.X, r.Y, Math.Max(1, r.Width - FU(150)), r.Height);
        const double top = 24, bottom = 0;
        double Y(double v) => plot.Y + Math.Clamp((top - v) / (top - bottom), 0, 1) * plot.Height;
        DrawRightScale(ctx, plot, A, small, Y, (14, "14 dB  dinamico"), (8, "8 dB  compresso"));
        var items = _dynHistory.ToArray();
        if (items.Length < 2) return;
        var accent = UiTheme.Accent;
        Polyline(ctx, new Pen(B(Color.FromArgb(A(200), accent.R, accent.G, accent.B)), Math.Max(1.5, FU(2.2)), lineJoin: PenLineJoin.Round), HistoryLine(items, plot, i => i.Plr, Y, 0));
        Polyline(ctx, new Pen(B(W(A(120))), Math.Max(1, FU(1.6))), HistoryLine(items, plot, i => i.R, Y, 0));
        Polyline(ctx, new Pen(B(W(A(240))), Math.Max(1.5, FU(2.4)), lineJoin: PenLineJoin.Round), HistoryLine(items, plot, i => i.L, Y, 0));
    }

    private void DrawChartStereo(DrawingContext ctx, Rect r, Func<double, byte> A, double small, double medium)
    {
        // Goniometro al centro e, sotto, due letture in testo (valore e significato): niente barrette a cursore.
        double readH = LineH(medium) + LineH(small) + FU(8);
        double side = Math.Max(FU(80), Math.Min(r.Height - readH - FU(40), r.Width * .52));
        double groupH = side + FU(40) + readH;
        double top = r.Y + Math.Max(0, (r.Height - groupH) / 2);
        var box = new Rect(r.X + (r.Width - side) / 2, top, side, side);
        double cx = box.Center.X, cy = box.Center.Y, rad = side / 2, d45 = rad * .7071;
        var guide = new Pen(B(W(A(34))), 1);
        ctx.DrawLine(guide, new Point(cx, box.Y), new Point(cx, box.Bottom));
        ctx.DrawLine(guide, new Point(box.X, cy), new Point(box.Right, cy));
        ctx.DrawLine(guide, new Point(cx - d45, cy - d45), new Point(cx + d45, cy + d45));
        ctx.DrawLine(guide, new Point(cx - d45, cy + d45), new Point(cx + d45, cy - d45));
        ctx.DrawEllipse(null, guide, new Point(cx, cy), rad, rad);
        Txt(ctx, "L", small, DisplayRegular, W(A(120)), cx - d45 - FU(18), cy - d45 - FU(20));
        Txt(ctx, "R", small, DisplayRegular, W(A(120)), cx + d45 + FU(6), cy - d45 - FU(20));
        Txt(ctx, "M", small, DisplayRegular, W(A(120)), cx + FU(6), box.Y - FU(2));
        int n = Math.Min(_scopeL.Length, _scopeR.Length);
        if (n > 0)
        {
            var dot = B(W(A(120)));
            double d = Math.Max(1.5, FU(2.4));
            int stride = Math.Max(1, n / 1200);
            for (int i = 0; i < n; i += stride)
            {
                double l = _scopeL[i], rr = _scopeR[i];
                ctx.FillRectangle(dot, new Rect(cx + (l - rr) * .7071 * rad, cy - (l + rr) * .7071 * rad, d, d));
            }
        }
        string corrValue = _shownCorr.ToString("+0.00;−0.00;0.00", CultureInfo.CurrentCulture);
        // Numeri e parole cambiano due volte al secondo (UpdateReadouts), il disegno segue la musica.
        string corrText = _shownCorr > .9 ? "quasi mono" : _shownCorr > .25 ? "correlazione stereo" : _shownCorr >= 0 ? "stereo largo" : "fuori fase";
        double balancePct = Math.Clamp(_shownBalance, -1, 1) * 100;
        string balValue = Math.Abs(balancePct) < 2 ? "0%" : Math.Abs(balancePct).ToString("0", CultureInfo.CurrentCulture) + (balancePct > 0 ? "% R" : "% L");
        string balText = Math.Abs(balancePct) < 2 ? "bilanciamento centrato" : "bilanciamento";
        double y0 = box.Bottom + FU(40);
        void Read(double centerX, string value, string text, bool warn)
        {
            Txt(ctx, value, medium, DisplaySemibold, warn ? Color.FromArgb(A(245), 255, 130, 120) : W(A(245)), centerX - TextW(value, medium, DisplaySemibold) / 2, y0);
            Txt(ctx, text, small, DisplayRegular, W(A(150)), centerX - TextW(text, small, DisplayRegular) / 2, y0 + LineH(medium) + FU(4));
        }
        Read(cx - side * .32, corrValue, corrText, _shownCorr < 0);
        Read(cx + side * .32, balValue, balText, false);
    }

    /// <summary>Scala dei livelli non lineare (come i misuratori veri): la parte alta, quella che conta, e' piu' larga.</summary>
    private static double LevelPosition(double db)
    {
        ReadOnlySpan<double> marks = stackalloc double[] { -60, -40, -24, -12, -6, -3, 0 };
        ReadOnlySpan<double> pos = stackalloc double[] { 0, .14, .34, .58, .76, .88, 1 };
        if (db <= marks[0]) return 0;
        for (int i = 1; i < marks.Length; i++)
            if (db <= marks[i]) return pos[i - 1] + (pos[i] - pos[i - 1]) * (db - marks[i - 1]) / (marks[i] - marks[i - 1]);
        return 1;
    }

    /// <summary>
    /// Livelli come Windows (misuratore da studio): due barre sottili (L sopra, R sotto) con la scala in mezzo e il livello
    /// medio a destra; sotto, nello stesso stile, la correlazione di fase (-1 controfase, 0, +1 mono). Gruppo centrato.
    /// </summary>
    private void DrawChartLevels(DrawingContext ctx, Rect r, Func<double, byte> A, double small, double medium)
    {
        double barH = Math.Max(FU(8), FU(14));
        double valueW = Math.Ceiling(TextW("−00.0", medium, DisplaySemibold)) + FU(20);
        double nameW = FU(40), scaleH = LineH(small) + FU(26);
        double corrGap = FU(56), corrH = barH + scaleH;
        double groupH = barH * 2 + scaleH + corrGap + corrH;
        double top = r.Y + Math.Max(0, (r.Height - groupH) / 2);
        double left = r.X + nameW, right = r.Right - valueW - FU(24);
        double width = Math.Max(FU(60), right - left);
        double X(double db) => left + LevelPosition(db) * width;
        var accent = UiTheme.Accent;
        var tick = B(W(A(60)));
        void Bar(double y, string name, double rms, double peak, double hold, double shown)
        {
            Txt(ctx, name, medium, DisplaySemibold, W(A(160)), r.X, y + barH / 2 - LineH(medium) / 2);
            double radius = barH / 2;
            ctx.DrawRectangle(B(W(A(30))), null, new Rect(left, y, width, barH), radius, radius);
            void Fill(double db, byte ink)
            {
                double w = Math.Round(LevelPosition(db) * width);
                if (w < barH) return;
                ctx.DrawRectangle(B(W(A(ink))), null, new Rect(left, y, w, barH), radius, radius);
            }
            Fill(peak, 75);
            Fill(rms, 240);
            if (hold > -60)
            {
                double hx = X(hold);
                var color = hold > -1 ? Color.FromArgb(A(255), 255, 110, 100) : Color.FromArgb(A(255), accent.R, accent.G, accent.B);
                ctx.DrawRectangle(B(color), null, new Rect(Math.Round(hx - FU(2)), y - FU(3), Math.Max(3, FU(4)), barH + FU(6)), FU(2), FU(2));
            }
            // Il numero cambia due volte al secondo (UpdateReadouts); la barra segue la musica.
            string value = Num(shown);
            Txt(ctx, value, medium, DisplaySemibold, W(A(245)), r.Right - TextW(value, medium, DisplaySemibold), y + barH / 2 - LineH(medium) / 2);
        }
        Bar(top, "L", _rmsL, _peakL, _holdL, _shownRmsL);
        double ty = top + barH + scaleH / 2;
        foreach (int db in new[] { -60, -40, -24, -12, -6, -3, 0 })
        {
            double gx = X(db);
            string text = db.ToString(CultureInfo.CurrentCulture).Replace("-", "−");
            double w = TextW(text, small, DisplayRegular);
            Txt(ctx, text, small, DisplayRegular, W(A(130)), Math.Clamp(gx - w / 2, left - w / 2, right), ty - LineH(small) / 2);
            ctx.FillRectangle(tick, new Rect(gx - .5, top + barH + FU(4), 1, FU(5)));
            ctx.FillRectangle(tick, new Rect(gx - .5, top + barH + scaleH - FU(9), 1, FU(5)));
        }
        Bar(top + barH + scaleH, "R", _rmsR, _peakR, _holdR, _shownRmsR);

        // Correlazione di fase: stessa pista, riempimento dal centro al valore, scala sotto, valore a destra.
        double cy = top + barH * 2 + scaleH + corrGap;
        double cr = barH / 2, center = left + width / 2, cx = left + (Math.Clamp(_corr, -1, 1) + 1) / 2 * width;
        bool warn = _shownCorr < 0;
        var hotC = Color.FromArgb(A(240), 255, 110, 100);
        Txt(ctx, "φ", medium, DisplaySemibold, W(A(160)), r.X, cy + barH / 2 - LineH(medium) / 2);
        ctx.DrawRectangle(B(W(A(30))), null, new Rect(left, cy, width, barH), cr, cr);
        double fx = Math.Min(center, cx), fw = Math.Max(barH, Math.Abs(cx - center));
        ctx.DrawRectangle(B(warn ? hotC : W(A(240))), null, new Rect(cx < center ? cx : center, cy, fw, barH), cr, cr);
        string corrValue = _shownCorr.ToString("+0.00;−0.00;0.00", CultureInfo.CurrentCulture);
        Txt(ctx, corrValue, medium, DisplaySemibold, warn ? hotC : W(A(245)), r.Right - TextW(corrValue, medium, DisplaySemibold), cy + barH / 2 - LineH(medium) / 2);
        double cty = cy + barH + scaleH / 2;
        foreach (var (v, text) in new[] { (-1.0, "−1  controfase"), (0.0, "0"), (1.0, "mono  +1") })
        {
            double gx = left + (v + 1) / 2 * width, w = TextW(text, small, DisplayRegular);
            Txt(ctx, text, small, DisplayRegular, W(A(130)), v < 0 ? left : v > 0 ? left + width - w : gx - w / 2, cty - LineH(small) / 2);
            ctx.FillRectangle(tick, new Rect(gx - .5, cy + barH + FU(4), 1, FU(5)));
        }
        Txt(ctx, "correlazione di fase", small, DisplayRegular, W(A(110)), center + FU(14), cty - LineH(small) / 2);
    }

    /// <summary>Formato del file e percorso del suono, su due colonne: nome piccolo, valore sotto. Blocco centrato in verticale.</summary>
    private void DrawChartsInfo(DrawingContext ctx, Rect r, Func<double, byte> A, double small, double medium, double centerY = double.NaN)
    {
        var rows = _infoRows;
        if (rows.Count == 0)
        {
            FsText(ctx, "Nessuna informazione sul brano.", medium, DisplaySemibold, W(A(160)), new Rect(r.X, (double.IsNaN(centerY) ? r.Y + r.Height / 2 : centerY) - FU(20), r.Width, FU(40)), TextAlignment.Left);
            return;
        }
        const int columns = 2;
        double colW = r.Width / columns, rowH = LineH(small) + LineH(medium) + FU(30);
        int perColumn = (rows.Count + columns - 1) / columns;
        int maxRows = Math.Max(1, (int)((r.Height + FU(30)) / rowH));
        perColumn = Math.Min(perColumn, maxRows);
        double blockH = perColumn * rowH - FU(30);
        double top = double.IsNaN(centerY) ? r.Y + Math.Max(0, (r.Height - blockH) / 2)
            : Math.Clamp(centerY - blockH / 2, r.Y, Math.Max(r.Y, r.Bottom - blockH));
        for (int i = 0; i < rows.Count; i++)
        {
            int col = i / perColumn, row = i % perColumn;
            if (col >= columns) break;
            double x = r.X + col * colW, y = top + row * rowH;
            FsText(ctx, rows[i].Label, small, DisplayRegular, W(A(140)), new Rect(x, y, colW - FU(24), LineH(small)), TextAlignment.Left);
            FsText(ctx, rows[i].Value.Replace("  ·  ", "   ").Replace(" · ", "   "), medium, DisplaySemibold, W(A(240)),
                new Rect(x, y + LineH(small) + FU(6), colW - FU(24), LineH(medium)), TextAlignment.Left);
        }
    }

    // ---------------------------------------------------------------- elenco delle schede
    private bool _fsMenuOpen;
    private long _fsMenuStart = -1;

    private void SetFsMenu(bool open)
    {
        if (_fsMenuOpen == open) return;
        _fsMenuOpen = open;
        _fsMenuStart = Environment.TickCount64;
        StartFsFrames(); InvalidateVisual();
    }

    private void ChooseFsTab(int tab)
    {
        SetFsMenu(false);
        if (tab == _fsTab || tab < 0 || tab >= FsTabs.Length) return;
        _fsTab = tab; _chartFadeStart = Environment.TickCount64;
        StartFsFrames(); InvalidateVisual();
    }

    private double FsMenuProgress()
    {
        if (_fsMenuStart < 0) return _fsMenuOpen ? 1 : 0;
        long elapsed = Environment.TickCount64 - _fsMenuStart;
        double t = Math.Clamp(elapsed / (_fsMenuOpen ? 200.0 + 22.0 * 8 : 140.0), 0, 1);
        if (t >= 1) { _fsMenuStart = -1; return _fsMenuOpen ? 1 : 0; }
        double e = 1 - Math.Pow(1 - Math.Clamp(elapsed / (_fsMenuOpen ? 200.0 : 140.0), 0, 1), 3);
        return _fsMenuOpen ? e : 1 - e;
    }

    private void DrawFsTabMenu(DrawingContext ctx, Rect column, double top, double px, Func<double, byte> A)
    {
        long started = _fsMenuStart;
        double p = FsMenuProgress();
        if (p <= 0.001) return;
        double row = Math.Max(26, FU(46));
        var others = Enumerable.Range(0, FsTabs.Length).Where(i => i != _fsTab).ToList();
        double width = others.Max(i => TextW(FsTabs[i], px, DisplayRegular));
        // Un clic fuori dalle voci chiude soltanto l'elenco.
        if (_fsMenuOpen) _hits.Add(new Hit(new Rect(0, 0, Bounds.Width, Bounds.Height), "musicfs-menu-close"));
        long elapsed = started < 0 ? long.MaxValue : Environment.TickCount64 - started;
        for (int k = 0; k < others.Count; k++)
        {
            int i = others[k];
            double itemP = _fsMenuOpen ? (elapsed == long.MaxValue ? 1 : Math.Clamp((elapsed - k * 22) / 200.0, 0, 1)) : p;
            double e = 1 - Math.Pow(1 - itemP, 3);
            if (e <= 0.01) continue;
            double y = top + k * row - FU(10) * (1 - e);
            var r = new Rect(column.X - FU(10), y, width + FU(20), row);
            bool hot = _fsMenuOpen && Hover(r);
            Txt(ctx, FsTabs[i], px, DisplayRegular, W(A(Math.Round((hot ? 255 : 185) * e))), column.X, y + (row - LineH(px)) / 2);
            if (_fsMenuOpen) _hits.Add(new Hit(r, "musicfs-tab", i.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
