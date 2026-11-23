using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CinecorePlayer2025.Audio;

namespace CinecoreMac;

// Impostazioni › Cinecore Audio: editor della curva dell'equalizzatore (SettingsHudPage.CinecoreAudio.cs di Windows).
// La risposta vera della catena (bande compensate come le suona il motore) su scala logaritmica 20 Hz – 20 kHz,
// ±15 dB; i nodi si trascinano: nei grafici a 10 e 31 bande solo in altezza (il clic porta subito la banda li'),
// nel parametrico anche in frequenza. Rotella su un nodo del parametrico = Q, doppio clic in un punto vuoto = nuova banda.
// Il motore applica durante il trascinamento (Touch); il file si scrive al rilascio.
internal sealed partial class SettingsView
{
    private int _eqDragBand = -1;
    private Rect _eqPlot;
    private string _eqCurveKey = "";
    private (double Hz, double Db)[] _eqCurve = Array.Empty<(double, double)>();
    private double _eqMaxBoost, _eqWheel;

    private static double EqX(Rect plot, double hz) => plot.X + Math.Log10(Math.Clamp(hz, 20, 20000) / 20) / 3 * plot.Width;
    private static double EqY(Rect plot, double db) => plot.Y + (15 - Math.Clamp(db, -17, 17)) / 30 * plot.Height;
    private static double EqHz(Rect plot, double x) => 20 * Math.Pow(1000, Math.Clamp((x - plot.X) / plot.Width, 0, 1));
    private static double EqDb(Rect plot, double y) => 15 - (y - plot.Y) / plot.Height * 30;
    private static bool IsPassFilter(string type) => type is "LowPass" or "HighPass";
    private static string EqFrequencyLabel(double hz) => hz >= 1000
        ? (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " kHz"
        : hz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz";

    /// <summary>Nodi dell'editor: frequenza, guadagno mostrato e se la banda e' attiva.</summary>
    private static List<(double Hz, double Db, bool On)> EqNodes(CinecoreAudioSettings s) => s.Mode switch
    {
        EqMode.Graphic31 => AudioDspSettings.ThirdOctaveFrequencies.Select((f, i) => (f, i < s.Graphic31.Count ? s.Graphic31[i] : 0, true)).ToList(),
        EqMode.Parametric => s.Parametric.Select(b => (b.Frequency, IsPassFilter(b.Type) ? 0 : b.Gain, b.Enabled)).ToList(),
        _ => s.Bands.Select(b => (b.Frequency, b.Gain, true)).ToList(),
    };

    /// <summary>Curva e margine in cache: si ricalcolano solo quando cambiano le impostazioni.</summary>
    private void EnsureEqCurve(CinecoreAudioSettings s)
    {
        var dsp = s.ToDsp();
        string key = string.Join("|", s.EqMode, s.EqEnabled, s.PreampDb, s.Protection, s.Width, s.ReplayGain,
            string.Join(";", dsp.Bands.Select(b => $"{(int)b.Type},{b.FrequencyHz:0.##},{b.GainDb:0.##},{b.Q:0.###},{b.Enabled}")));
        if (key == _eqCurveKey) return;
        _eqCurveKey = key;
        var bands = dsp.EffectiveBands(48000).Where(b => b.Enabled).ToList();
        _eqCurve = new (double, double)[181];
        for (int i = 0; i <= 180; i++)
        {
            double hz = 20 * Math.Pow(1000, i / 180.0), sum = 0;
            foreach (var b in bands) sum += Biquad.ResponseDb(b, 48000, hz);
            _eqCurve[i] = (hz, sum);
        }
        _eqMaxBoost = AudioDspChain.MaxBoostDb(dsp with { EqEnabled = true }, 48000);
    }

    /// <summary>Disegna il grafico (coordinate del contenuto scorrevole) e restituisce l'altezza usata.</summary>
    private double DrawEqCurve(DrawingContext ctx, Rect area)
    {
        var settings = CinecoreAudioSettings.Current;
        EnsureEqCurve(settings);
        var plot = new Rect(area.X + 44, area.Y + 16, Math.Max(120, area.Width - 60), 220);
        _eqPlot = plot;

        var grid = new Pen(Brush(A(UiTheme.Text, UiTheme.IsLight ? 34 : 26)), 1);
        var zero = new Pen(Brush(A(UiTheme.Text, UiTheme.IsLight ? 80 : 60)), 1);
        foreach (int db in new[] { 12, 6, 0, -6, -12 })
        {
            double y = Math.Round(EqY(plot, db)) + .5;
            ctx.DrawLine(db == 0 ? zero : grid, new Point(plot.X, y), new Point(plot.Right, y));
            Text(ctx, (db > 0 ? "+" : "") + db.ToString(CultureInfo.InvariantCulture), 8.2, false, new Rect(area.X, y - 9, 38, 18), UiTheme.Muted, TextAlignment.Right);
        }
        foreach (double hz in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            double x = Math.Round(EqX(plot, hz)) + .5;
            ctx.DrawLine(grid, new Point(x, plot.Y), new Point(x, plot.Bottom));
            Text(ctx, EqFrequencyLabel(hz), 8.2, false, new Rect(x - 30, plot.Bottom + 4, 60, 16), UiTheme.Muted, TextAlignment.Center);
        }

        bool active = settings.EqEnabled;
        Color curveColor = active ? UiTheme.Accent : A(UiTheme.Muted, 120);
        var points = _eqCurve.Select(p => new Point(EqX(plot, p.Hz), EqY(plot, p.Db))).ToArray();
        double zeroY = EqY(plot, 0);
        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(points[0].X, zeroY), true);
            foreach (var p in points) g.LineTo(p);
            g.LineTo(new Point(points[^1].X, zeroY));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Brush(A(curveColor, active ? 40 : 16)), null, fill);
        ctx.DrawGeometry(null, new Pen(Brush(curveColor), 2.4, lineJoin: PenLineJoin.Round), new PolylineGeometry(points, false));

        var nodes = EqNodes(settings);
        bool parametric = settings.Mode == EqMode.Parametric;
        bool dense = settings.Mode == EqMode.Graphic31;
        double nodeSize = dense ? 9 : 14;
        int selected = parametric ? Math.Clamp(settings.SelectedBand, 0, Math.Max(0, nodes.Count - 1)) : -1;
        int hotIndex = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            var (hz, db, on) = nodes[i];
            double x = EqX(plot, hz), y = EqY(plot, db);
            var node = new Rect(x - nodeSize / 2, y - nodeSize / 2, nodeSize, nodeSize);
            var hit = parametric ? node.Inflate(8) : new Rect(x - (dense ? 5 : 13), plot.Y - 6, dense ? 10 : 26, plot.Height + 12);
            bool hot = _eqDragBand == i || (_eqDragBand < 0 && Hover(hit));
            if (hot) hotIndex = i;
            bool sel = i == selected;
            Color fillColor = !on ? A(UiTheme.Muted, 90) : hot || sel ? Colors.White : curveColor;
            var ring = new Pen(Brush(A(UiTheme.Accent, active && on ? 255 : 120)), sel ? 3 : dense ? 1.6 : 2);
            ctx.DrawEllipse(Brush(fillColor), ring, node.Center, nodeSize / 2, nodeSize / 2);
            if (parametric)
                Text(ctx, (i + 1).ToString(CultureInfo.InvariantCulture), 7.4, true, new Rect(x - 10, node.Bottom + 3, 20, 14), UiTheme.Muted, TextAlignment.Center);
            _hits.Add(new Hit(hit, "eqnode", i.ToString(CultureInfo.InvariantCulture)));
        }
        if (hotIndex >= 0)
        {
            var (hz, db, _) = nodes[hotIndex];
            double x = EqX(plot, hz), y = EqY(plot, db);
            string label = $"{EqFrequencyLabel(hz)}  {(db > 0 ? "+" : "")}{db.ToString("0.#", CultureInfo.InvariantCulture)} dB";
            if (parametric) label += $"  Q {settings.Parametric[hotIndex].Q.ToString("0.##", CultureInfo.InvariantCulture)}";
            double top = y - nodeSize / 2 - 24 < plot.Y ? y + nodeSize / 2 + 4 : y - nodeSize / 2 - 22;
            Text(ctx, label, 8.2, false, new Rect(x - 100, top, 200, 18), UiTheme.Text, TextAlignment.Center);
        }

        double boost = _eqMaxBoost;
        string b1 = boost.ToString("0.0", CultureInfo.CurrentCulture);
        string note = !active ? "Equalizzatore spento: la curva è solo un'anteprima."
            : settings.ToDsp().Protection == ClipProtection.Off ? $"Boost massimo +{b1} dB · protezione disattivata: possibile clipping."
            : boost > 0.05 ? $"Margine automatico −{b1} dB: nessun campione oltre il soffitto."
            : "Nessun boost: il livello resta invariato.";
        Text(ctx, note, 8.2, false, new Rect(plot.X, plot.Bottom + 24, plot.Width, 18), UiTheme.Muted);
        if (parametric)
            Text(ctx, "Doppio clic per aggiungere una banda, rotella per il Q.", 8.2, false, new Rect(plot.X, plot.Bottom + 42, plot.Width, 18), UiTheme.Muted);
        return plot.Bottom + (parametric ? 70 : 52) - area.Y;
    }

    /// <summary>Punto della pagina nelle coordinate del contenuto scorrevole (dove sta il grafico).</summary>
    private Point EqPoint(Point page) => new(page.X, page.Y + _scroll);

    private bool EqVisible => _tab == "Cinecore Audio" && _eqPlot.Width > 0 && _menuItems == null && _picker == null;

    private void BeginEqDrag(int band, Point page)
    {
        var s = CinecoreAudioSettings.Current;
        if (s.Mode == EqMode.Parametric && band < s.Parametric.Count) s.SelectedBand = band;
        _eqDragBand = band;
        // Nel grafico a bande fisse il clic porta subito il punto li'; nel parametrico no (selezionare non sposta).
        if (s.Mode != EqMode.Parametric) UpdateEqDrag(page);
        InvalidateVisual();
    }

    private bool UpdateEqDrag(Point page)
    {
        if (_eqDragBand < 0 || _eqPlot.Width <= 0) return false;
        var p = EqPoint(page);
        var c = CinecoreAudioSettings.Current;
        double db = Math.Round(EqDb(_eqPlot, p.Y) * 2) / 2;
        bool changed = false;
        switch (c.Mode)
        {
            case EqMode.Graphic31:
                db = Math.Clamp(db, -12, 12);
                if (_eqDragBand < c.Graphic31.Count && Math.Abs(c.Graphic31[_eqDragBand] - db) > 0.01) { c.Graphic31[_eqDragBand] = db; changed = true; }
                break;
            case EqMode.Parametric:
                if (_eqDragBand < c.Parametric.Count)
                {
                    var b = c.Parametric[_eqDragBand];
                    double hz = EqHz(_eqPlot, p.X);
                    double rounded = Math.Clamp(hz < 100 ? Math.Round(hz) : hz < 1000 ? Math.Round(hz / 5) * 5 : Math.Round(hz / 50) * 50, 20, 20000);
                    if (Math.Abs(b.Frequency - rounded) > 0.5) { b.Frequency = rounded; changed = true; }
                    if (!IsPassFilter(b.Type))
                    {
                        db = Math.Clamp(db, -15, 15);
                        if (Math.Abs(b.Gain - db) > 0.01) { b.Gain = db; changed = true; }
                    }
                }
                break;
            default:
                db = Math.Clamp(db, -12, 12);
                if (_eqDragBand < c.Bands.Count && Math.Abs(c.Bands[_eqDragBand].Gain - db) > 0.01) { c.Bands[_eqDragBand].Gain = db; changed = true; }
                break;
        }
        if (changed)
        {
            if (c.Mode != EqMode.Parametric) c.Preset = "custom";
            c.EqEnabled = true;
            CinecoreAudioSettings.Touch(); // il motore applica subito; il file si scrive al rilascio
        }
        InvalidateVisual();
        return true;
    }

    private bool EndEqDrag()
    {
        if (_eqDragBand < 0) return false;
        _eqDragBand = -1;
        CinecoreAudioSettings.Save();
        InvalidateVisual();
        return true;
    }

    /// <summary>Rotella sul grafico del parametrico: regola il Q del nodo piu' vicino (lontano dai nodi scorre la pagina).</summary>
    private bool EqWheel(Point page, double delta)
    {
        var c = CinecoreAudioSettings.Current;
        if (!EqVisible || c.Mode != EqMode.Parametric || c.Parametric.Count == 0 || Math.Abs(delta) < 0.01) return false;
        var p = EqPoint(page);
        if (!_eqPlot.Contains(p)) return false;
        int index = -1; double best = 22;
        for (int i = 0; i < c.Parametric.Count; i++)
        {
            var b = c.Parametric[i];
            double dx = EqX(_eqPlot, b.Frequency) - p.X, dy = EqY(_eqPlot, IsPassFilter(b.Type) ? 0 : b.Gain) - p.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < best) { best = d; index = i; }
        }
        if (index < 0) { _eqWheel = 0; return false; }
        // Trackpad: tanti piccoli spostamenti; un passo di Q ogni unita' accumulata, come uno scatto della rotella.
        _eqWheel += delta;
        if (Math.Abs(_eqWheel) < 1) return true;
        bool up = _eqWheel > 0;
        _eqWheel = 0;
        var band = c.Parametric[index];
        band.Q = Math.Clamp(Math.Round(band.Q * (up ? 1.15 : 1 / 1.15), 2), 0.1, 24);
        c.SelectedBand = index; c.EqEnabled = true;
        CinecoreAudioSettings.Save();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Prove: "banda,dB[,Hz]" trascina il nodo come col mouse (premi, sposta, rilascia) nelle coordinate della pagina,
    /// poi restituisce il valore salvato. Serve la scheda Cinecore Audio aperta e disegnata.
    /// </summary>
    internal string TestEqDrag(string argument)
    {
        var parts = argument.Split(',');
        if (_eqPlot.Width <= 0 || parts.Length < 2 || !int.TryParse(parts[0], out int band)) return "grafico non disegnato";
        double db = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var c = CinecoreAudioSettings.Current;
        double hz = parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture)
            : EqNodes(c) is var nodes && band < nodes.Count ? nodes[band].Hz : 1000;
        var page = new Point(EqX(_eqPlot, hz), EqY(_eqPlot, db) - _scroll);
        BeginEqDrag(band, page);
        UpdateEqDrag(page);
        EndEqDrag();
        var after = EqNodes(CinecoreAudioSettings.Current);
        return band < after.Count
            ? $"modo={c.EqMode} banda {band}: {after[band].Hz:0} Hz {after[band].Db:+0.0;-0.0;0} dB preset={c.Preset} attivo={c.EqEnabled} margine={_eqMaxBoost:0.0}"
            : "banda inesistente";
    }

    /// <summary>Doppio clic su un punto vuoto del grafico parametrico: nuova banda li' (al massimo 10).</summary>
    private bool EqDoubleClick(Point page)
    {
        var c = CinecoreAudioSettings.Current;
        if (!EqVisible || c.Mode != EqMode.Parametric || c.Parametric.Count >= 10) return false;
        var p = EqPoint(page);
        if (!_eqPlot.Contains(p)) return false;
        foreach (var b in c.Parametric)
            if (Math.Abs(EqX(_eqPlot, b.Frequency) - p.X) < 12 && Math.Abs(EqY(_eqPlot, IsPassFilter(b.Type) ? 0 : b.Gain) - p.Y) < 12) return false;
        double hz = EqHz(_eqPlot, p.X), db = Math.Clamp(Math.Round(EqDb(_eqPlot, p.Y) * 2) / 2, -15, 15);
        c.Parametric.Add(new CinecoreAudioSettings.BandDto { Type = "Peak", Frequency = Math.Round(hz), Gain = db, Q = 1.0 });
        c.SelectedBand = c.Parametric.Count - 1; c.EqEnabled = true;
        CinecoreAudioSettings.Save();
        InvalidateVisual();
        return true;
    }
}
