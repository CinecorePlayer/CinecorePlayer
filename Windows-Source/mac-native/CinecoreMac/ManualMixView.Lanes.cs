using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using CinecorePlayer2025.Audio;

namespace CinecoreMac;

/// <summary>
/// [NOI, 2026-11-22] Automazioni disegnate a mano nel mix manuale. Prima si potevano solo scegliere forme fisse (quattro
/// selettori); ora, scelta una corsia (Volume, Bassi, Filtro, Eco) sopra il pulsante del mix, su ciascun brano compare la
/// sua curva con i punti: si trascinano, un clic sulla curva ne aggiunge uno, il tasto destro lo toglie. I punti partono
/// dalla forma del preset scelto, quindi si ritocca un preset invece di ricominciare da zero. Il filtro e' una manopola
/// sola da DJ: sotto la linea di mezzo chiude il passa-basso, sopra apre il passa-alto.
/// </summary>
internal sealed partial class ManualMixView
{
    private static readonly string[] LaneLabels = { "Volume", "Bassi", "Filtro", "Eco" };
    private string? _editLane;
    private readonly Dictionary<string, List<(double X, double Y)>> _lanes = new();
    private readonly Rect[] _laneChips = new Rect[4];
    private Rect _laneReset;
    private bool _lanePoint; // trascinamento di un punto in corso
    private bool _laneOut;
    private int _laneIndex;

    private bool LanesAvailable => _ready && !(_vol == TransitionCurves.VolumeNone && !_auto);
    private bool LaneBipolar => _editLane == "filter";

    /// <summary>Le corsie da salvare o da passare alle curve (null se non ce n'e' nessuna).</summary>
    private Dictionary<string, double[]>? LanesForSave()
    {
        if (_lanes.Count == 0) return null;
        return _lanes.ToDictionary(kv => kv.Key, kv => kv.Value.SelectMany(p => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4) }).ToArray());
    }

    private void LoadLanes(Dictionary<string, double[]>? saved)
    {
        _lanes.Clear();
        if (saved == null) return;
        foreach (var (key, flat) in saved)
            if (flat is { Length: >= 4 }) _lanes[key] = TransitionCurves.LanePoints(flat);
    }

    private void ClearLanes(params string[] lanes)
    {
        foreach (var lane in lanes) { _lanes.Remove("out." + lane); _lanes.Remove("in." + lane); }
    }

    /// <summary>I punti della corsia: quelli disegnati, o se non ce ne sono quelli ricavati dalle curve del preset.</summary>
    private List<(double X, double Y)> LanePointsFor(bool outgoing, string lane)
    {
        if (_lanes.TryGetValue((outgoing ? "out." : "in.") + lane, out var drawn)) return drawn;
        var curves = BaseCurves;
        const int n = 33;
        var dense = new List<(double X, double Y)>(n);
        for (int i = 0; i < n; i++) { double p = i / (double)(n - 1); dense.Add((p, curves.LaneValue(outgoing, lane, p))); }
        return Simplify(dense, lane == "filter" ? 0.04 : 0.025);
    }

    /// <summary>Douglas-Peucker: tiene i punti che servono alla forma, cosi' se ne trascinano pochi.</summary>
    private static List<(double X, double Y)> Simplify(List<(double X, double Y)> pts, double tolerance)
    {
        if (pts.Count <= 2) return new(pts);
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        void Step(int a, int b)
        {
            if (b <= a + 1) return;
            double best = -1; int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                double t = (pts[i].X - pts[a].X) / Math.Max(1e-9, pts[b].X - pts[a].X);
                double y = pts[a].Y + (pts[b].Y - pts[a].Y) * t, d = Math.Abs(pts[i].Y - y);
                if (d > best) { best = d; at = i; }
            }
            if (best > tolerance) { keep[at] = true; Step(a, at); Step(at, b); }
        }
        Step(0, pts.Count - 1);
        var result = new List<(double X, double Y)>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    // ------------------------------------------------------------------ disegno

    private Rect LaneOf(bool outgoing) => outgoing ? _laneA : _laneB;
    private double LaneTop(Rect lane) => lane.Top + P(14);
    private double LaneBottom(Rect lane) => lane.Bottom - P(14);
    private double LaneY(Rect lane, double v)
    {
        double top = LaneTop(lane), bottom = LaneBottom(lane);
        double u = LaneBipolar ? (Math.Clamp(v, -1, 1) + 1) / 2 : Math.Clamp(v, 0, 1);
        return bottom - u * (bottom - top);
    }
    private double LaneValueAtY(Rect lane, double y)
    {
        double top = LaneTop(lane), bottom = LaneBottom(lane);
        double u = Math.Clamp((bottom - y) / Math.Max(1, bottom - top), 0, 1);
        if (!LaneBipolar) return u;
        double v = u * 2 - 1;
        return Math.Abs(v) < 0.05 ? 0 : v; // la linea di mezzo (filtro aperto) attira
    }
    private Point LanePoint(bool outgoing, (double X, double Y) p) => new(X(p.X * _fade), LaneY(LaneOf(outgoing), p.Y));

    private void LayoutLaneChips()
    {
        double h = P(26), y = _playMix.Center.Y - h / 2, x = _editor.Left + P(4);
        var face = new Typeface(new FontFamily(AppAssets.Root + "Assets/Fonts#Inter"), FontStyle.Normal, FontWeight.SemiBold);
        for (int i = 0; i < 4; i++)
        {
            double w = P(22) + UiText.Get(LaneLabels[i], face, P(12.5), Colors.White).Width;
            _laneChips[i] = new Rect(x, y, w, h);
            x += w + P(6);
        }
        _laneReset = _lanes.Count > 0 ? new Rect(x + P(6), y, P(92), h) : default;
    }

    private static string LaneKey(int i) => TransitionCurves.LaneNames[i];

    private void DrawLaneChips(DrawingContext ctx)
    {
        if (!LanesAvailable) { Array.Clear(_laneChips); _laneReset = default; return; }
        LayoutLaneChips();
        for (int i = 0; i < 4; i++)
        {
            var r = _laneChips[i];
            bool on = _editLane == LaneKey(i), hot = _hover == "lane" + i;
            bool drawn = _lanes.ContainsKey("out." + LaneKey(i)) || _lanes.ContainsKey("in." + LaneKey(i));
            ctx.DrawRectangle(B(on ? Hi : A(UiTheme.Text, hot ? 34 : 18)), null, r, r.Height / 2, r.Height / 2);
            var ink = on ? Sheet : UiTheme.Text;
            TextIn(ctx, LaneLabels[i], P(12.5), UiSemibold, ink, r, TextAlignment.Center);
            if (drawn) ctx.DrawEllipse(B(on ? Sheet : Hi), null, new Point(r.Right - P(7), r.Top + P(7)), P(2.5), P(2.5));
        }
        if (_laneReset.Width > 0)
        {
            bool hot = _hover == "laneReset";
            TextIn(ctx, "Ripristina", P(12.5), UiRegular, hot ? UiTheme.Text : UiTheme.Muted, _laneReset, TextAlignment.Left);
        }
    }

    /// <summary>La corsia scelta su un deck: linea, area e punti da trascinare (sopra le curve del preset).</summary>
    private void DrawLane(DrawingContext ctx, bool outgoing)
    {
        if (_editLane is not { } lane || !LanesAvailable) return;
        var r = LaneOf(outgoing);
        var pts = LanePointsFor(outgoing, lane);
        if (pts.Count == 0) return;
        double x0 = X(0), x1 = X(_fade);
        var ink = outgoing ? TintA : TintB;
        // Guida: la linea di mezzo del filtro (aperto) o il fondo.
        double guideY = LaneBipolar ? LaneY(r, 0) : LaneY(r, 0);
        ctx.DrawLine(new Pen(B(A(UiTheme.Text, 50)), Math.Max(1, K), DashStyle.Dash), new Point(x0, guideY), new Point(x1, guideY));
        if (LaneBipolar)
        {
            TextIn(ctx, "passa-alto", P(10.5), UiRegular, UiTheme.Muted, new Rect(x1 + P(6), LaneTop(r) - P(6), P(90), P(14)));
            TextIn(ctx, "passa-basso", P(10.5), UiRegular, UiTheme.Muted, new Rect(x1 + P(6), LaneBottom(r) - P(8), P(90), P(14)));
        }
        var line = new List<Point> { new(x0, LanePoint(outgoing, pts[0]).Y) };
        line.AddRange(pts.Select(p => LanePoint(outgoing, p)));
        line.Add(new Point(x1, LanePoint(outgoing, pts[^1]).Y));
        var area = line.Concat(new[] { new Point(x1, guideY), new Point(x0, guideY) }).ToList();
        // Area velata nel colore del brano; linea chiara con alone scuro: si legge sopra l'onda di qualunque colore.
        ctx.DrawGeometry(B(A(Sheet, 120)), null, Polyline(area, closed: true));
        var geo = Polyline(line);
        ctx.DrawGeometry(null, new Pen(B(A(Sheet, 220)), Math.Max(5, 7 * K), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
        ctx.DrawGeometry(null, new Pen(B(Hi), Math.Max(2, 2.6 * K), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
        for (int i = 0; i < pts.Count; i++)
        {
            var c = LanePoint(outgoing, pts[i]);
            bool hot = _hover == $"pt{(outgoing ? 'A' : 'B')}{i}" || _lanePoint && _laneOut == outgoing && _laneIndex == i;
            double rad = hot ? P(7) : P(5);
            ctx.DrawEllipse(B(ink), new Pen(B(Hi), Math.Max(1.6, 2.2 * K)), c, rad, rad);
            if (hot) TextIn(ctx, LaneText(lane, pts[i].Y), P(11.5), UiSemibold, UiTheme.Text,
                new Rect(c.X - P(60), c.Y < r.Center.Y ? c.Y + P(10) : c.Y - P(26), P(120), P(16)), TextAlignment.Center);
        }
    }

    private static string LaneText(string lane, double v) => lane switch
    {
        "vol" => v <= 0.001 ? "muto" : (20 * Math.Log10(v)).ToString("0.0", CultureInfo.CurrentCulture) + " dB",
        "bass" => v >= 0.999 ? "bassi pieni" : (TransitionCurves.BassKillDb * (1 - v)).ToString("0", CultureInfo.CurrentCulture) + " dB",
        "echo" => (v * 100).ToString("0", CultureInfo.CurrentCulture) + " % eco",
        _ => Math.Abs(v) < 0.01 ? "aperto" : v > 0
            ? "passa-alto " + Hz(20 * Math.Pow(6000 / 20.0, v)) : "passa-basso " + Hz(20000 * Math.Pow(120 / 20000.0, -v)),
    };
    private static string Hz(double hz) => hz >= 1000 ? (hz / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " kHz" : hz.ToString("0", CultureInfo.CurrentCulture) + " Hz";

    // ------------------------------------------------------------------ input

    /// <summary>Colpo sulle corsie: chip, "Ripristina", punto ("ptA3"), curva da arricchire ("addA").</summary>
    private string? LaneHit(Point p)
    {
        if (!LanesAvailable) return null;
        for (int i = 0; i < 4; i++) if (_laneChips[i].Contains(p)) return "lane" + i;
        if (_laneReset.Width > 0 && _laneReset.Contains(p)) return "laneReset";
        if (_editLane is not { } lane || _preview.Playing) return null;
        foreach (bool outgoing in new[] { true, false })
        {
            var r = LaneOf(outgoing);
            if (!r.Contains(p)) continue;
            var pts = LanePointsFor(outgoing, lane);
            int best = -1; double bestD = P(10);
            for (int i = 0; i < pts.Count; i++)
            {
                var c = LanePoint(outgoing, pts[i]);
                double d = Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y));
                if (d <= bestD) { bestD = d; best = i; }
            }
            if (best >= 0) return $"pt{(outgoing ? 'A' : 'B')}{best}";
            if (p.X >= X(0) - P(2) && p.X <= X(_fade) + P(2)) return outgoing ? "addA" : "addB";
        }
        return null;
    }

    /// <summary>Prima modifica a mano: le curve automatiche diventano il preset Dissolvenza, da cui si parte.</summary>
    private void LeaveAuto()
    {
        if (!_auto) return;
        _auto = false;
        _vol = TransitionCurves.VolumeFade; _eq = TransitionCurves.EqBassMid; _filter = TransitionCurves.FilterNone; _fx = TransitionCurves.FxNone;
    }

    private List<(double X, double Y)> Materialize(bool outgoing, string lane)
    {
        string key = (outgoing ? "out." : "in.") + lane;
        if (!_lanes.TryGetValue(key, out var pts)) _lanes[key] = pts = new List<(double X, double Y)>(LanePointsFor(outgoing, lane));
        return pts;
    }

    /// <summary>Vero se il clic era sulle corsie (gestito qui).</summary>
    private bool LanePressed(string hit, Point pos, bool right, IPointer pointer)
    {
        if (hit.StartsWith("lane", StringComparison.Ordinal) && hit.Length == 5 && char.IsDigit(hit[4]))
        {
            string lane = LaneKey(hit[4] - '0');
            _editLane = _editLane == lane ? null : lane;
            InvalidateVisual();
            return true;
        }
        if (hit == "laneReset")
        {
            StopPreview();
            if (_editLane != null && (_lanes.ContainsKey("out." + _editLane) || _lanes.ContainsKey("in." + _editLane))) ClearLanes(_editLane);
            else _lanes.Clear();
            _userChanged = true;
            InvalidateVisual();
            return true;
        }
        if (_editLane is not { } edit) return false;
        bool isPoint = hit.StartsWith("pt", StringComparison.Ordinal), isAdd = hit is "addA" or "addB";
        if (!isPoint && !isAdd) return false;
        StopPreview();
        LeaveAuto();
        bool outgoing = isPoint ? hit[2] == 'A' : hit == "addA";
        var pts = Materialize(outgoing, edit);
        _userChanged = true;
        if (isPoint)
        {
            int index = int.Parse(hit[3..], CultureInfo.InvariantCulture);
            if (right)
            {
                if (pts.Count > 2 && index < pts.Count) pts.RemoveAt(index);
                InvalidateVisual();
                return true;
            }
            _laneIndex = index;
        }
        else
        {
            if (right) return true;
            double x = Math.Clamp(Tau(pos.X) / Math.Max(0.01, _fade), 0, 1);
            double y = LaneValueAtY(LaneOf(outgoing), pos.Y);
            int at = pts.FindIndex(p => p.X > x);
            if (at < 0) at = pts.Count;
            pts.Insert(at, (x, y));
            _laneIndex = at;
        }
        _lanePoint = true; _laneOut = outgoing;
        _drag = "lanePoint"; _dragStart = pos; _dragMoved = true;
        pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    private void LaneDragged(Point pos)
    {
        if (!_lanePoint || _editLane is not { } lane) return;
        var pts = Materialize(_laneOut, lane);
        if (_laneIndex < 0 || _laneIndex >= pts.Count) return;
        double lo = _laneIndex > 0 ? pts[_laneIndex - 1].X + 0.002 : 0, hi = _laneIndex < pts.Count - 1 ? pts[_laneIndex + 1].X - 0.002 : 1;
        double x = Math.Clamp(Tau(pos.X) / Math.Max(0.01, _fade), Math.Min(lo, hi), Math.Max(lo, hi));
        // Sulle battute del brano che esce, se ci sono: tenendo Maiuscole si muove libero (vedi LaneSnap).
        x = LaneSnap(x);
        pts[_laneIndex] = (Math.Clamp(x, Math.Min(lo, hi), Math.Max(lo, hi)), LaneValueAtY(LaneOf(_laneOut), pos.Y));
        InvalidateVisual();
    }

    private bool _laneFree;
    private double LaneSnap(double x)
    {
        if (_laneFree || !_snap || _from?.Tail is not { Reliable: true } grid || grid.Period <= 0 || _fade <= 0) return x;
        // Mezzo battito: abbastanza fitto per i tagli a tempo, abbastanza largo da sentirsi.
        double step = grid.Period / 2 / _fade;
        if (step < 0.01) return x;
        double t = _outAt + x * _fade, first = grid.FirstBeat;
        double snapped = (first + Math.Round((t - first) / (grid.Period / 2)) * (grid.Period / 2) - _outAt) / _fade;
        return Math.Abs(snapped - x) * _fade / Math.Max(1e-6, SecondsPerPixel) <= P(8) ? Math.Clamp(snapped, 0, 1) : x;
    }

    private void LaneReleased()
    {
        _lanePoint = false;
        InvalidateVisual();
    }

    private string? LaneTip(string hover)
    {
        if (hover.StartsWith("lane", StringComparison.Ordinal) && hover.Length == 5 && char.IsDigit(hover[4]))
            return (hover[4] - '0') switch
            {
                0 => "Disegna il volume dei due brani",
                1 => "Disegna quando entrano e escono i bassi",
                2 => "Disegna il filtro: sopra la linea il passa-alto, sotto il passa-basso",
                _ => "Disegna quanto brano finisce nell'eco (a tempo)",
            };
        if (hover == "laneReset") return "Torna alle curve del preset (della corsia scelta, o di tutte)";
        if (hover.StartsWith("pt", StringComparison.Ordinal)) return "Trascina il punto (Maiuscole: senza aggancio). Tasto destro: toglilo";
        if (hover is "addA" or "addB") return "Clic per aggiungere un punto qui";
        return null;
    }

    internal void QaLane(string arg)
    {
        // "vol" | "off" | "set:out.filter:0,0,0.5,1,1,0" | "clear"
        if (arg == "off") _editLane = null;
        else if (arg == "clear") _lanes.Clear();
        else if (arg.StartsWith("set:", StringComparison.Ordinal))
        {
            var parts = arg[4..].Split(':');
            var flat = parts[1].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            LeaveAuto();
            _lanes[parts[0]] = TransitionCurves.LanePoints(flat);
            _userChanged = true;
        }
        else _editLane = arg;
        InvalidateVisual();
    }
}
