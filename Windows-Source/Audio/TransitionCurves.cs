#nullable enable
using System;
using System.Collections.Generic;

namespace CinecorePlayer2025.Audio;

/// <summary>
/// Un tratto di curva d'automazione nella posizione normalizzata del passaggio (0 = inizio, 1 = fine).
/// I punti hanno x locale 0..1: 2 punti = retta, 3 = Bezier quadratica, 4 = Bezier cubica, di piu' = spezzata.
/// La lettura come Bezier e' quella dell'editor Mix desktop di Spotify (VERIFICA.md #24); i valori sono nostri.
/// </summary>
internal sealed record CurveSegment(double Start, double End, (double X, double Y)[] Points);

internal sealed class AutomationCurve
{
    private readonly CurveSegment[] _segments;
    public double Default { get; }
    public IReadOnlyList<CurveSegment> Segments => _segments;
    public bool IsEmpty => _segments.Length == 0;

    public AutomationCurve(double fallback, params CurveSegment[] segments)
    {
        Default = fallback;
        _segments = segments;
        Array.Sort(_segments, (a, b) => a.Start.CompareTo(b.Start));
    }

    public static AutomationCurve Constant(double value) => new(value);

    /// <summary>Valore alla posizione <paramref name="p"/> (0-1). Fuori dai tratti: si tiene l'ultimo valore raggiunto.</summary>
    public double ValueAt(double p)
    {
        if (_segments.Length == 0) return Default;
        if (p <= _segments[0].Start) return _segments[0].Points[0].Y;
        double held = _segments[0].Points[0].Y;
        foreach (var s in _segments)
        {
            if (p < s.Start) return held;
            if (p <= s.End)
            {
                double u = s.End > s.Start ? (p - s.Start) / (s.End - s.Start) : 1;
                return Evaluate(s.Points, u);
            }
            held = s.Points[^1].Y;
        }
        return held;
    }

    /// <summary>y del tratto alla x locale <paramref name="u"/>; per le Bezier si risolve x(t) = u per bisezione.</summary>
    internal static double Evaluate((double X, double Y)[] pts, double u)
    {
        u = Math.Clamp(u, 0, 1);
        switch (pts.Length)
        {
            case 0: return 0;
            case 1: return pts[0].Y;
            case 3:
            case 4:
            {
                double lo = 0, hi = 1;
                for (int i = 0; i < 40; i++)
                {
                    double mid = (lo + hi) / 2;
                    if (Bezier(pts, mid).X < u) lo = mid; else hi = mid;
                }
                return Bezier(pts, (lo + hi) / 2).Y;
            }
            default:
                for (int i = 1; i < pts.Length; i++)
                {
                    if (u <= pts[i].X || i == pts.Length - 1)
                    {
                        var (x0, y0) = pts[i - 1]; var (x1, y1) = pts[i];
                        return x1 > x0 ? y0 + (y1 - y0) * Math.Clamp((u - x0) / (x1 - x0), 0, 1) : y1;
                    }
                }
                return pts[^1].Y;
        }
    }

    private static (double X, double Y) Bezier((double X, double Y)[] p, double t)
    {
        double s = 1 - t;
        if (p.Length == 3)
            return (s * s * p[0].X + 2 * s * t * p[1].X + t * t * p[2].X, s * s * p[0].Y + 2 * s * t * p[1].Y + t * t * p[2].Y);
        double a = s * s * s, b = 3 * s * s * t, c = 3 * s * t * t, d = t * t * t;
        return (a * p[0].X + b * p[1].X + c * p[2].X + d * p[3].X, a * p[0].Y + b * p[1].Y + c * p[2].Y + d * p[3].Y);
    }
}

/// <summary>Le automazioni di un deck: volume (0-1, ampiezza), EQ a tre bande in dB, passa-alto e passa-basso (Hz, 0 = spento) con Q.</summary>
internal sealed record DeckCurves(AutomationCurve Volume, AutomationCurve LowDb, AutomationCurve MidDb, AutomationCurve HighDb,
    AutomationCurve HighPassHz, AutomationCurve HighPassQ, AutomationCurve LowPassHz, AutomationCurve LowPassQ)
{
    private static readonly AutomationCurve NoFx = new(0);
    public bool UsesEq => !LowDb.IsEmpty || !MidDb.IsEmpty || !HighDb.IsEmpty;
    public bool UsesFilters => !HighPassHz.IsEmpty || !LowPassHz.IsEmpty;

    // [NOI, 2026-10-26] Effetti da DJ. Le mandate si prendono dopo EQ e filtri e prima del volume: quando il volume del
    // deck scende, eco e riverbero continuano a suonare (l'"echo out" e il "wash out" dei DJ).
    /// <summary>Quanto segnale entra nell'eco (0-1).</summary>
    public AutomationCurve EchoSend { get; init; } = NoFx;
    /// <summary>Ritorno dell'eco su se stessa (0-0,85): piu' alto = piu' ripetizioni.</summary>
    public AutomationCurve EchoFeedback { get; init; } = NoFx;
    /// <summary>Quanto segnale entra nel riverbero (0-1).</summary>
    public AutomationCurve ReverbSend { get; init; } = NoFx;
    /// <summary>Loop (0 = spento, 1 = acceso): all'accensione registra <see cref="LoopBeats"/> battiti e poi li ripete.</summary>
    public AutomationCurve LoopOn { get; init; } = NoFx;
    /// <summary>Distanza fra le ripetizioni dell'eco, in battiti (0,75 = croma puntata).</summary>
    public double EchoBeats { get; init; } = 0.75;
    /// <summary>Durata di un battito del brano di questo deck, in secondi.</summary>
    public double BeatSeconds { get; init; } = 0.5;
    public double LoopBeats { get; init; } = 4;
    public bool UsesEcho => !EchoSend.IsEmpty;
    public bool UsesReverb => !ReverbSend.IsEmpty;
    public bool UsesLoop => !LoopOn.IsEmpty;

    // [NOI, 2026-10-27] Altri attrezzi da DJ.
    /// <summary>Loop roll: frazione del loop che si ripete (1 = tutta la battuta, 0,5 = mezza, 0,25...). Vuota = 1.</summary>
    public AutomationCurve LoopDivision { get; init; } = NoFx;
    /// <summary>Riser: rumore bianco che sale di altezza e di volume (0-1), sopra il deck.</summary>
    public AutomationCurve Riser { get; init; } = NoFx;
    /// <summary>Gate a tempo (trance gate): profondita' 0-1 del taglio ritmico.</summary>
    public AutomationCurve GateDepth { get; init; } = NoFx;
    /// <summary>Durata di un passo del gate in battiti (0,25 = sedicesimo).</summary>
    public double GateBeats { get; init; } = 0.25;
    /// <summary>Brake (stop del nastro): posizione 0-1 in cui il deck rallenta fino a fermarsi, -1 = mai.</summary>
    public double BrakeAt { get; init; } = -1;
    public double BrakeSeconds { get; init; } = 0.8;
    /// <summary>Spinback (il disco tirato indietro): posizione 0-1, -1 = mai.</summary>
    public double SpinAt { get; init; } = -1;
    public double SpinSeconds { get; init; } = 0.9;
    public bool UsesRiser => !Riser.IsEmpty;
    public bool UsesGate => !GateDepth.IsEmpty;
    public bool UsesTape => BrakeAt >= 0 || SpinAt >= 0;
}

/// <summary>
/// Un preset completo (uscita + entrata). Gli id e i mattoni ricalcano la struttura dei preset Spotify verificata nel
/// laboratorio (VERIFICA.md #17, #18, #26, #27); le forme e i numeri delle curve sono scritti qui, non copiati dal bundle.
/// </summary>
internal sealed partial record TransitionCurves(int PresetId, string Name, DeckCurves Out, DeckCurves In)
{
    public const double BassKillDb = -20, BandKillDb = -12;
    public const double LowCrossoverHz = 350, HighCrossoverHz = 4000;

    private static readonly AutomationCurve Zero = AutomationCurve.Constant(0), Q = AutomationCurve.Constant(0.7071), None = new(0);

    private static CurveSegment Line(double start, double end, double from, double to) => new(start, end, new[] { (0d, from), (1d, to) });
    private static CurveSegment Hold(double start, double end, double value) => Line(start, end, value, value);
    private static AutomationCurve Curve(double fallback, params CurveSegment[] s) => new(fallback, s);

    /// <summary>Un sedicesimo di battuta nella posizione normalizzata (Spotify: 1/128 del passaggio su 8 battute).</summary>
    private static double Sixteenth(int bars) => 1.0 / (16.0 * (bars > 0 ? bars : 8));

    // ----- volume -----
    private static AutomationCurve LinearOut() => Curve(1, Line(0, 1, 1, 0));
    private static AutomationCurve LinearIn() => Curve(0, Line(0, 1, 0, 1));
    // [NOI, 2026-10-24] S morbida a potenza costante: con la S in ampiezza i due deck valevano 0,5 a meta' e il passaggio
    // calava di ~3 dB (il volume "si perdeva e poi si rialzava").
    private static AutomationCurve SmoothOut() => Curve(1, new CurveSegment(0, 1, PowerS(true)));
    private static AutomationCurve SmoothIn() => Curve(0, new CurveSegment(0, 1, PowerS(false)));
    private static (double X, double Y)[] PowerS(bool outgoing)
    {
        var pts = new (double, double)[25];
        for (int i = 0; i < pts.Length; i++)
        {
            double x = i / (double)(pts.Length - 1), s = x * x * (3 - 2 * x);
            pts[i] = (x, outgoing ? Math.Cos(s * Math.PI / 2) : Math.Sin(s * Math.PI / 2));
        }
        return pts;
    }
    private static AutomationCurve SlowOut() => Curve(1, Hold(0, 0.5, 1), Line(0.5, 1, 1, 0));
    private static AutomationCurve SlowIn() => Curve(0, Line(0, 0.5, 0, 1), Hold(0.5, 1, 1));
    private static AutomationCurve HoldOut(double width) => Curve(1, Hold(0, 1 - width, 1), Line(1 - width, 1, 1, 0));
    private static AutomationCurve HoldIn(double width) => Curve(0, Line(0, width, 0, 1), Hold(width, 1, 1));
    private static AutomationCurve CenterCutOut(double w) => Curve(1, Hold(0, 0.5, 1), Line(0.5, 0.5 + w, 1, 0), Hold(0.5 + w, 1, 0));
    private static AutomationCurve CenterCutIn(double w) => Curve(0, Hold(0, 0.5 - w, 0), Line(0.5 - w, 0.5, 0, 1), Hold(0.5, 1, 1));

    // ----- EQ -----
    private static AutomationCurve BassOutAt(double at, double w) => Curve(0, Hold(0, at - w, 0), new CurveSegment(at - w, at, new[] { (0d, 0d), (0.5, 0d), (0.5, BassKillDb), (1d, BassKillDb) }), Hold(at, 1, BassKillDb));
    private static AutomationCurve BassInAt(double at, double w) => Curve(BassKillDb, Hold(0, at - w, BassKillDb), new CurveSegment(at - w, at, new[] { (0d, BassKillDb), (0.5, BassKillDb), (0.5, 0d), (1d, 0d) }), Hold(at, 1, 0));
    private static AutomationCurve BandOut(double at, double db) => Curve(0, Hold(0, at, 0), Line(at, Math.Min(1, at + 0.125), 0, db), Hold(Math.Min(1, at + 0.125), 1, db));
    private static AutomationCurve BandIn(double at, double db) => Curve(db, Hold(0, Math.Max(0, at - 0.125), db), Line(Math.Max(0, at - 0.125), at, db, 0), Hold(at, 1, 0));

    // ----- filtri (Hz esponenziali, Q con risonanza nel tratto attivo) -----
    private static AutomationCurve Sweep(double start, double end, double fromHz, double toHz, int steps = 8)
    {
        var segs = new List<CurveSegment>();
        for (int i = 0; i < steps; i++)
        {
            double a = start + (end - start) * i / steps, b = start + (end - start) * (i + 1) / steps;
            double ha = fromHz * Math.Pow(toHz / fromHz, i / (double)steps), hb = fromHz * Math.Pow(toHz / fromHz, (i + 1) / (double)steps);
            segs.Add(Line(a, b, ha, hb));
        }
        return Curve(0, segs.ToArray());
    }
    private static AutomationCurve Resonance(double start, double end) =>
        Curve(0.7071, Line(start, start + (end - start) * 0.1, 0.7071, 2.5), Hold(start + (end - start) * 0.1, start + (end - start) * 0.9, 2.5), Line(start + (end - start) * 0.9, end, 2.5, 0.7071));

    private static DeckCurves Deck(AutomationCurve volume, AutomationCurve? low = null, AutomationCurve? mid = null, AutomationCurve? high = null,
        AutomationCurve? hp = null, AutomationCurve? hpq = null, AutomationCurve? lp = null, AutomationCurve? lpq = null) =>
        new(volume, low ?? None, mid ?? None, high ?? None, hp ?? None, hpq ?? Q, lp ?? None, lpq ?? Q);

    /// <summary>Id dei preset descritti qui; gli altri id della tabella Auto di Spotify non hanno una definizione locale.</summary>
    public static readonly int[] KnownIds = { 0, 1, 2, 3, 4, 11, 17 };

    /// <summary>[NOI] Preset nostri, fuori dalla tabella di Spotify (non entrano nella rotazione "spotify").</summary>
    public const int FilterFadeId = 20;

    /// <summary>Preset per id. <paramref name="bars"/> serve per i tagli veloci (un sedicesimo di battuta).</summary>
    public static TransitionCurves Preset(int id, int bars = 8)
    {
        double w = Sixteenth(bars), edge = Math.Min(0.02, w * 2);
        return id switch
        {
            // [OSS] Il passaggio misurato sull'uscita reale (ramo fade API): due rampe lineari simultanee.
            0 => new(0, "observed-linear", Deck(LinearOut()), Deck(LinearIn())),
            // Fade: volume a S morbida, scambio dei bassi al centro.
            1 => new(1, "fade", Deck(SmoothOut(), BassOutAt(0.5, w)), Deck(SmoothIn(), BassInAt(0.5, w))),
            // Blend: entrambi pieni, dissolvenza a tre bande (alti a 1/4, medi e bassi a meta').
            2 => new(2, "blend", Deck(HoldOut(edge), BassOutAt(0.5, w), BandOut(0.5, BandKillDb), BandOut(0.25, BandKillDb)),
                                 Deck(HoldIn(edge), BassInAt(0.5, w), BandIn(0.5, BandKillDb), BandIn(0.25, BandKillDb))),
            // Rise: bassi scambiati alla fine, passa-alto che sale su A e passa-basso che si apre su B.
            3 => new(3, "rise", Deck(HoldOut(edge), BassOutAt(1, w), hp: Sweep(0.5, 1, 120, 9000), hpq: Resonance(0.5, 1)),
                                Deck(HoldIn(edge), BassInAt(1, w), lp: Sweep(0, 0.5, 120, 16000), lpq: Resonance(0, 0.5))),
            // Melt: dissolvenza lenta, bassi al centro, passa-alto su entrambi.
            4 => new(4, "melt", Deck(SlowOut(), BassOutAt(0.5, w), hp: Sweep(0.5, 1, 120, 9000), hpq: Resonance(0.5, 1)),
                                Deck(SlowIn(), BassInAt(0.5, w), hp: Sweep(0, 0.49, 9000, 120), hpq: Resonance(0, 0.49))),
            // Slam: taglio al centro in un sedicesimo di battuta.
            11 => new(11, "slam", Deck(CenterCutOut(w)), Deck(CenterCutIn(w))),
            // Wave: bassi al centro, passa-basso in uscita e in entrata.
            17 => new(17, "wave", Deck(HoldOut(edge), BassOutAt(0.5, w), lp: Sweep(0.5, 1, 16000, 120), lpq: Resonance(0.5, 1)),
                                  Deck(HoldIn(edge), BassInAt(0.5, w), lp: Sweep(0, 0.5, 120, 16000), lpq: Resonance(0, 0.5))),
            // [NOI] Filter fade, per brani che non si agganciano a tempo: A si assottiglia (passa-alto che sale) e cala
            // nella seconda meta'; B entra chiuso (passa-basso che si apre) e senza bassi fino a meta'. I due ritmi
            // non si scontrano mai a piena banda, e il passaggio dura abbastanza da sentirsi.
            FilterFadeId => new(FilterFadeId, "filter-fade",
                Deck(Curve(1, Hold(0, 0.4, 1), new CurveSegment(0.4, 1, new[] { (0d, 1d), (0.45, 0.92), (1d, 0d) })),
                     BassOutAt(0.5, w), hp: Sweep(0.1, 0.95, 40, 2200, 12), hpq: Curve(0.7071, Line(0.1, 0.3, 0.7071, 1.4), Hold(0.3, 0.95, 1.4))),
                Deck(Curve(0, new CurveSegment(0, 0.6, new[] { (0d, 0d), (0.55, 0.92), (1d, 1d) }), Hold(0.6, 1, 1)),
                     BassInAt(0.5, w), lp: Sweep(0, 0.75, 500, 21000, 12), lpq: Curve(1.2, Hold(0, 0.6, 1.2), Line(0.6, 0.75, 1.2, 0.7071)))),
            _ => Preset(1, bars) with { PresetId = id, Name = "fade(missing-" + id + ")" },
        };
    }

    // ----- passaggio composto a mano (i tre selettori dell'editor del mix manuale) -----
    /// <summary>Volume: sovrapposizione, dissolvenza, lineare, lenta, taglio al centro, nessuna transizione.</summary>
    public const int VolumeOverlap = 0, VolumeFade = 1, VolumeLinear = 2, VolumeSlow = 3, VolumeCut = 4, VolumeNone = 5;
    /// <summary>EQ: nessuno, scambio dei bassi al centro, scambio dei bassi finale, tre bande.</summary>
    public const int EqNone = 0, EqBassMid = 1, EqBassEnd = 2, EqThreeBand = 3;
    /// <summary>Filtri: nessuno, passa-basso in entrata e passa-alto in uscita, passa-alto su entrambi, passa-basso su entrambi.</summary>
    public const int FilterNone = 0, FilterRise = 1, FilterHighPass = 2, FilterLowPass = 3;

    /// <summary>
    /// Passaggio composto da volume, EQ e filtri scelti separatamente. I preset dell'editor ne sono combinazioni:
    /// Dissolvenza = (1,1,0), Ascendente = (0,2,1), Sfumatura = (3,1,2), Onda = (0,1,3), Fusione = (0,3,0), Passaggio
    /// brusco = (4,0,0), Lineare = (2,0,0); combinazioni uguali ai preset danno le stesse curve.
    /// </summary>
    public static TransitionCurves Compose(int volume, int eq, int filter, int bars = 8)
    {
        double w = Sixteenth(bars), edge = Math.Min(0.02, w * 2);
        AutomationCurve outVol = volume switch { VolumeFade => SmoothOut(), VolumeLinear => LinearOut(), VolumeSlow => SlowOut(), VolumeCut => CenterCutOut(w), _ => HoldOut(edge) };
        AutomationCurve inVol = volume switch { VolumeFade => SmoothIn(), VolumeLinear => LinearIn(), VolumeSlow => SlowIn(), VolumeCut => CenterCutIn(w), _ => HoldIn(edge) };
        AutomationCurve? outLow = null, inLow = null, outMid = null, inMid = null, outHigh = null, inHigh = null;
        switch (eq)
        {
            case EqBassMid: outLow = BassOutAt(0.5, w); inLow = BassInAt(0.5, w); break;
            case EqBassEnd: outLow = BassOutAt(1, w); inLow = BassInAt(1, w); break;
            case EqThreeBand:
                outLow = BassOutAt(0.5, w); inLow = BassInAt(0.5, w);
                outMid = BandOut(0.5, BandKillDb); inMid = BandIn(0.5, BandKillDb);
                outHigh = BandOut(0.25, BandKillDb); inHigh = BandIn(0.25, BandKillDb); break;
        }
        AutomationCurve? outHp = null, outHpQ = null, outLp = null, outLpQ = null, inHp = null, inHpQ = null, inLp = null, inLpQ = null;
        switch (filter)
        {
            case FilterRise:
                outHp = Sweep(0.5, 1, 120, 9000); outHpQ = Resonance(0.5, 1);
                inLp = Sweep(0, 0.5, 120, 16000); inLpQ = Resonance(0, 0.5); break;
            case FilterHighPass:
                outHp = Sweep(0.5, 1, 120, 9000); outHpQ = Resonance(0.5, 1);
                inHp = Sweep(0, 0.49, 9000, 120); inHpQ = Resonance(0, 0.49); break;
            case FilterLowPass:
                outLp = Sweep(0.5, 1, 16000, 120); outLpQ = Resonance(0.5, 1);
                inLp = Sweep(0, 0.5, 120, 16000); inLpQ = Resonance(0, 0.5); break;
        }
        return new(100 + volume * 16 + eq * 4 + filter, $"manual-{volume}{eq}{filter}",
            Deck(outVol, outLow, outMid, outHigh, outHp, outHpQ, outLp, outLpQ),
            Deck(inVol, inLow, inMid, inHigh, inHp, inHpQ, inLp, inLpQ));
    }

    /// <summary>Dissolvenza pura a potenza costante: niente EQ, filtri o effetti ("Solo dissolvenza").</summary>
    public static TransitionCurves Crossfade() => Compose(VolumeFade, EqNone, FilterNone) with { Name = "crossfade" };

    // ----- [NOI, 2026-11-22] automazioni disegnate a mano nell'editor del mix manuale -----
    /// <summary>Corsie disegnabili per deck: volume, bassi, filtro (manopola unica da DJ) ed eco.</summary>
    public static readonly string[] LaneNames = { "vol", "bass", "filter", "echo" };

    /// <summary>
    /// Valori normalizzati di una corsia alla posizione p: volume 0-1 (ampiezza); bassi 0 (tagliati, -20 dB) - 1 (pieni);
    /// filtro -1 (passa-basso chiuso a ~120 Hz) .. 0 (aperto) .. 1 (passa-alto a ~6 kHz); eco 0-1 di mandata.
    /// </summary>
    public double LaneValue(bool outgoing, string lane, double p)
    {
        var d = outgoing ? Out : In;
        switch (lane)
        {
            case "vol": return Math.Clamp(d.Volume.ValueAt(p), 0, 1);
            case "bass": return d.LowDb.IsEmpty ? 1 : Math.Clamp(1 - d.LowDb.ValueAt(p) / BassKillDb, 0, 1);
            case "echo": return d.EchoSend.IsEmpty ? 0 : Math.Clamp(d.EchoSend.ValueAt(p), 0, 1);
            case "filter":
                double hp = d.HighPassHz.IsEmpty ? 0 : d.HighPassHz.ValueAt(p), lp = d.LowPassHz.IsEmpty ? 0 : d.LowPassHz.ValueAt(p);
                if (hp >= 25) return Math.Clamp(Math.Log(hp / 20) / Math.Log(FilterHpTop / 20), 0, 1);
                if (lp >= 20 && lp < 20000) return -Math.Clamp(Math.Log(FilterLpOpen / lp) / Math.Log(FilterLpOpen / FilterLpBottom), 0, 1);
                return 0;
        }
        return 0;
    }

    private const double FilterHpTop = 6000, FilterLpOpen = 20000, FilterLpBottom = 120;

    /// <summary>Le corsie presenti in <paramref name="lanes"/> ("out.vol", "in.filter"...: x,y,x,y... con x 0-1) prendono
    /// il posto delle curve del preset; le altre restano come sono.</summary>
    public TransitionCurves WithLanes(IReadOnlyDictionary<string, double[]>? lanes, double beatA, double beatB)
    {
        if (lanes == null || lanes.Count == 0) return this;
        DeckCurves Apply(DeckCurves d, string side, double beat)
        {
            foreach (var lane in LaneNames)
            {
                if (!lanes.TryGetValue(side + "." + lane, out var flat) || flat == null || flat.Length < 4) continue;
                var pts = LanePoints(flat);
                switch (lane)
                {
                    case "vol": d = d with { Volume = Dense(pts, v => Math.Clamp(v, 0, 1)) }; break;
                    case "bass": d = d with { LowDb = Dense(pts, v => BassKillDb * (1 - Math.Clamp(v, 0, 1))) }; break;
                    case "filter":
                        // Una manopola sola: sotto zero chiude il passa-basso, sopra apre il passa-alto. Spenti restano a
                        // 20 Hz e 20 kHz (trasparenti) cosi' il passaggio dall'uno all'altro e' continuo.
                        d = d with
                        {
                            HighPassHz = Dense(pts, v => v > 0 ? 20 * Math.Pow(FilterHpTop / 20, Math.Min(1, v)) : 20),
                            LowPassHz = Dense(pts, v => v < 0 ? FilterLpOpen * Math.Pow(FilterLpBottom / FilterLpOpen, Math.Min(1, -v)) : FilterLpOpen),
                            HighPassQ = Dense(pts, v => 0.7071 + 1.3 * Math.Clamp(Math.Abs(v) * 2, 0, 1)),
                            LowPassQ = Dense(pts, v => 0.7071 + 1.3 * Math.Clamp(Math.Abs(v) * 2, 0, 1)),
                        }; break;
                    case "echo":
                        d = d with
                        {
                            EchoSend = Dense(pts, v => Math.Clamp(v, 0, 1)),
                            EchoFeedback = d.EchoFeedback.IsEmpty ? AutomationCurve.Constant(0.5) : d.EchoFeedback,
                            BeatSeconds = beat > 0 ? beat : d.BeatSeconds,
                            EchoBeats = beat >= 0.45 ? 0.75 : 1.5,
                        }; break;
                }
            }
            return d;
        }
        return this with { Out = Apply(Out, "out", beatA), In = Apply(In, "in", beatB), Name = Name + "+lanes" };
    }

    internal static List<(double X, double Y)> LanePoints(double[] flat)
    {
        var pts = new List<(double X, double Y)>(flat.Length / 2);
        for (int i = 0; i + 1 < flat.Length; i += 2)
            if (double.IsFinite(flat[i]) && double.IsFinite(flat[i + 1])) pts.Add((Math.Clamp(flat[i], 0, 1), flat[i + 1]));
        pts.Sort((a, b) => a.X.CompareTo(b.X));
        return pts;
    }

    /// <summary>Spezzata fra i punti, campionata fitta (con 3-4 punti un tratto sarebbe letto come Bezier).</summary>
    private static AutomationCurve Dense(List<(double X, double Y)> pts, Func<double, double> map)
    {
        const int n = 129;
        var outPts = new (double, double)[n];
        for (int i = 0; i < n; i++)
        {
            double x = i / (double)(n - 1), y;
            if (x <= pts[0].X) y = pts[0].Y;
            else if (x >= pts[^1].X) y = pts[^1].Y;
            else
            {
                int k = 1; while (k < pts.Count - 1 && pts[k].X < x) k++;
                var (x0, y0) = pts[k - 1]; var (x1, y1) = pts[k];
                y = x1 > x0 ? y0 + (y1 - y0) * (x - x0) / (x1 - x0) : y1;
            }
            outPts[i] = (x, map(y));
        }
        return new AutomationCurve(map(pts[0].Y), new CurveSegment(0, 1, outPts));
    }

    public double OutVolumeAt(double p) => Out.Volume.ValueAt(p);
    public double InVolumeAt(double p) => In.Volume.ValueAt(p);

    // ----- [NOI, 2026-10-26] passaggi con effetti da DJ -----
    public const int EchoOutId = 30, WashOutId = 31, LoopOutId = 32;
    /// <summary>Effetti del mix manuale: nessuno, eco in uscita, riverbero in uscita, loop di una battuta in uscita.</summary>
    public const int FxNone = 0, FxEcho = 1, FxReverb = 2, FxLoop = 3;

    /// <summary>
    /// Echo out: A suona fino al taglio (su una battuta); nel battito prima del taglio alimenta un'eco a tempo e al taglio
    /// sparisce, restano le ripetizioni sempre piu' deboli e sottili. B entra secco sul suo battito. E' il passaggio dei DJ
    /// quando due brani non si possono sovrapporre (tempi diversi, voci che si scontrerebbero).
    /// </summary>
    /// <param name="length">Durata del passaggio (s).</param><param name="cutAt">Taglio di A, secondi dall'inizio.</param>
    /// <param name="inAt">Ingresso udibile di B, secondi dall'inizio.</param><param name="beat">Battito di A (s).</param>
    public static TransitionCurves EchoOut(double length, double cutAt, double inAt, double beat, double inRamp = 0.06)
    {
        length = Math.Max(0.5, length);
        double pc = Math.Clamp(cutAt / length, 0.02, 0.9), tiny = Math.Min(0.03 / length, pc * 0.5);
        double feed = Math.Clamp((cutAt - Math.Max(0.12, Math.Min(beat, cutAt))) / length, 0, pc - tiny);
        double pi = Math.Clamp(inAt / length, 0, 0.95), ramp = Math.Clamp(inRamp / length, 0.002, 0.2);
        var outDeck = Deck(Curve(1, Hold(0, pc - tiny, 1), Line(pc - tiny, pc, 1, 0), Hold(pc, 1, 0))) with
        {
            EchoSend = Curve(0, Hold(0, feed, 0), Line(feed, Math.Min(pc, feed + tiny), 0, 1), Hold(Math.Min(pc, feed + tiny), pc, 1), Line(pc, Math.Min(1, pc + tiny), 1, 0), Hold(Math.Min(1, pc + tiny), 1, 0)),
            EchoFeedback = Curve(0.55, Hold(0, pc, 0.55), Line(pc, 0.85, 0.55, 0.42), Line(0.85, 1, 0.42, 0)),
            EchoBeats = beat >= 0.45 ? 0.75 : 1.5, BeatSeconds = beat,
        };
        var inDeck = Deck(Curve(0, Hold(0, pi, 0), Line(pi, Math.Min(1, pi + ramp), 0, 1), Hold(Math.Min(1, pi + ramp), 1, 1)));
        return new(EchoOutId, "echo-out", outDeck, inDeck);
    }

    /// <summary>
    /// Wash out: A si scioglie in un riverbero (mandata che sale, passa-alto che lo assottiglia) mentre cala; B entra
    /// chiuso e si apre. Per brani lenti, orchestrali o senza un battito da agganciare.
    /// </summary>
    public static TransitionCurves WashOut()
    {
        var fade = new CurveSegment(0.12, 0.62, PowerS(true));
        var outDeck = Deck(Curve(1, Hold(0, 0.12, 1), fade, Hold(0.62, 1, 0)),
            hp: Sweep(0.15, 0.62, 30, 900, 10), hpq: AutomationCurve.Constant(0.7071)) with
        {
            ReverbSend = Curve(0, Line(0, 0.35, 0.1, 1), Hold(0.35, 0.6, 1), Line(0.6, 0.66, 1, 0), Hold(0.66, 1, 0)),
        };
        // B sale da subito (piano, chiuso) e si apre: il suo inizio non va perso sotto il volume a zero.
        var inDeck = Deck(Curve(0, new CurveSegment(0, 0.75, PowerS(false)), Hold(0.75, 1, 1)),
            lp: Sweep(0.05, 0.8, 500, 21000, 10), lpq: AutomationCurve.Constant(0.7071));
        return new(WashOutId, "wash-out", outDeck, inDeck);
    }

    /// <summary>
    /// Loop out (a tempo): A ripete la prima battuta del passaggio (scelta senza voce) mentre B entra; i bassi passano a
    /// meta' e il loop si assottiglia e sparisce. Cosi' la voce di A non si sovrappone a B.
    /// </summary>
    public static TransitionCurves LoopOut(int bars, double beat)
    {
        double w = Sixteenth(bars);
        var outDeck = Deck(SmoothOut(), BassOutAt(0.5, w), hp: Sweep(0.5, 1, 120, 3000), hpq: AutomationCurve.Constant(0.9)) with
        {
            LoopOn = Curve(1, Hold(0, 1, 1)), LoopBeats = 4, BeatSeconds = beat,
        };
        var inDeck = Deck(SmoothIn(), BassInAt(0.5, w));
        return new(LoopOutId, "loop-out", outDeck, inDeck);
    }

    /// <summary>Aggiunge un effetto d'uscita (mix manuale) a un passaggio composto.</summary>
    public TransitionCurves WithFx(int fx, double beat)
    {
        if (fx == FxNone) return this;
        beat = double.IsFinite(beat) && beat > 0.2 && beat < 1.5 ? beat : 0.5;
        var o = Out;
        switch (fx)
        {
            case FxEcho:
                // Le ripetizioni partono dalla seconda meta' e restano dopo che A e' sceso.
                o = o with
                {
                    EchoSend = Curve(0, Hold(0, 0.45, 0), Line(0.45, 0.55, 0, 0.8), Hold(0.55, 0.75, 0.8), Line(0.75, 0.82, 0.8, 0), Hold(0.82, 1, 0)),
                    EchoFeedback = Curve(0.5, Hold(0, 0.82, 0.5), Line(0.82, 1, 0.5, 0)),
                    EchoBeats = beat >= 0.45 ? 0.75 : 1.5, BeatSeconds = beat,
                };
                break;
            case FxReverb:
                o = o with { ReverbSend = Curve(0, Hold(0, 0.3, 0), Line(0.3, 0.7, 0, 1), Hold(0.7, 0.85, 1), Line(0.85, 0.9, 1, 0), Hold(0.9, 1, 0)) };
                break;
            case FxLoop:
                o = o with { LoopOn = Curve(1, Hold(0, 1, 1)), LoopBeats = 4, BeatSeconds = beat };
                break;
        }
        return this with { Out = o, PresetId = PresetId + 1000 * fx, Name = Name + "+" + (fx == FxEcho ? "echo" : fx == FxReverb ? "reverb" : "loop") };
    }
}

/// <summary>
/// Esegue le curve di un deck sul segnale: volume, EQ a tre bande (crossover Linkwitz-Riley a 4 poli 350 / 4000 Hz con
/// compensazione di fase della banda bassa), passa-alto e passa-basso RBJ del secondo ordine.
/// Coefficienti e guadagni ogni 32 campioni; guadagni interpolati campione per campione.
/// </summary>
internal sealed class CurveDeckProcessor
{
    private const int Block = 32;
    private readonly DeckCurves _curves;
    private readonly bool _outgoing;
    private readonly int _rate, _channels;
    private readonly Bq[] _lp350a, _lp350b, _hp350a, _hp350b, _lp4ka, _lp4kb, _hp4ka, _hp4kb, _apLa, _apLb, _apHa, _apHb, _hp, _lp;
    private readonly bool _eq, _filters;
    private int _countdown;
    private double _gain, _gainStep, _low = 1, _lowStep, _mid = 1, _midStep, _high = 1, _highStep;
    // [NOI] Guadagni di banda seguiti con una costante di 12 ms: lo scambio dei bassi e' un gradino nella curva e, rifatto
    // in 32 campioni (0,7 ms), dava un "toc" a ogni cambio di brano.
    private double _lowSm = 1, _midSm = 1, _highSm = 1;
    private readonly double _smooth;
    private bool _hpOn, _lpOn;
    private double _release;
    // Effetti (vedi DeckCurves.EchoSend e seguenti).
    private readonly float[]? _echoBuf; private readonly int _echoLen; private int _echoPos;
    private readonly Bq[] _echoHp, _echoLp;
    private double _echoSend, _echoSendStep, _echoFb;
    private readonly Wash? _reverb; private double _revSend, _revSendStep;
    private readonly float[]? _loopBuf; private readonly int _loopLen, _loopX;
    private int _loopState, _loopWrite, _loopRead, _loopFade; private bool _loopWanted;
    private int _loopSpan; // lunghezza del giro attuale (loop roll)
    // Riser: rumore filtrato per canale.
    private readonly Bq[] _riserHp, _riserLp; private uint _noise = 0x9E3779B9u; private double _riserLevel, _riserStep;
    // Gate.
    private readonly double _gatePeriod; private double _gateDepth, _gateDepthStep;
    // Brake e spinback: nastro circolare degli ultimi secondi, letto a velocita' variabile.
    private readonly float[]? _tape; private readonly int _tapeLen; private int _tapeWrite;
    private int _tapeMode; private double _tapeRead; private long _tapeStart;

    public bool Done => _release >= 1;

    public CurveDeckProcessor(DeckCurves curves, bool outgoing, int rate, int channels)
    {
        _curves = curves; _outgoing = outgoing; _rate = rate; _channels = channels;
        _eq = curves.UsesEq; _filters = curves.UsesFilters;
        Bq[] Make() { var a = new Bq[channels]; for (int i = 0; i < channels; i++) a[i] = new Bq(); return a; }
        _lp350a = Make(); _lp350b = Make(); _hp350a = Make(); _hp350b = Make();
        _lp4ka = Make(); _lp4kb = Make(); _hp4ka = Make(); _hp4kb = Make();
        _apLa = Make(); _apLb = Make(); _apHa = Make(); _apHb = Make(); _hp = Make(); _lp = Make();
        for (int c = 0; c < channels; c++)
        {
            foreach (var f in new[] { _lp350a[c], _lp350b[c] }) f.LowPass(TransitionCurves.LowCrossoverHz, rate, 0.70710678);
            foreach (var f in new[] { _hp350a[c], _hp350b[c] }) f.HighPass(TransitionCurves.LowCrossoverHz, rate, 0.70710678);
            foreach (var f in new[] { _lp4ka[c], _lp4kb[c], _apLa[c], _apLb[c] }) f.LowPass(TransitionCurves.HighCrossoverHz, rate, 0.70710678);
            foreach (var f in new[] { _hp4ka[c], _hp4kb[c], _apHa[c], _apHb[c] }) f.HighPass(TransitionCurves.HighCrossoverHz, rate, 0.70710678);
        }
        if (curves.UsesEcho)
        {
            _echoLen = Math.Max(64, (int)Math.Round(Math.Clamp(curves.EchoBeats * curves.BeatSeconds, 0.08, 1.5) * rate));
            _echoBuf = new float[_echoLen * channels];
            _echoHp = Make(); _echoLp = Make();
            for (int c = 0; c < channels; c++) { _echoHp[c].HighPass(320, rate, 0.7071); _echoLp[c].LowPass(6500, rate, 0.7071); }
        }
        else { _echoHp = Array.Empty<Bq>(); _echoLp = Array.Empty<Bq>(); }
        if (curves.UsesReverb) _reverb = new Wash(rate, channels);
        if (curves.UsesLoop)
        {
            _loopLen = Math.Max(256, (int)Math.Round(Math.Clamp(curves.LoopBeats * curves.BeatSeconds, 0.1, 6) * rate));
            _loopX = Math.Max(16, (int)(0.008 * rate));
            _loopBuf = new float[(_loopLen + _loopX) * channels];
        }
        if (curves.UsesRiser) { _riserHp = Make(); _riserLp = Make(); }
        else { _riserHp = Array.Empty<Bq>(); _riserLp = Array.Empty<Bq>(); }
        _gatePeriod = curves.UsesGate ? Math.Max(32, Math.Clamp(curves.GateBeats * curves.BeatSeconds, 0.04, 2) * rate) : 0;
        if (curves.UsesTape)
        {
            _tapeLen = Math.Max(1024, rate * 4);
            _tape = new float[_tapeLen * channels];
        }
        _loopSpan = _loopLen;
        _gain = Math.Clamp(curves.Volume.ValueAt(0), 0, 1.5);
        // Il deck che entra puo' cominciare con i bassi gia' tolti: si parte dal valore della curva, non da 0 dB.
        _low = Db(curves.LowDb.ValueAt(0)); _mid = Db(curves.MidDb.ValueAt(0)); _high = Db(curves.HighDb.ValueAt(0));
        _lowSm = _low; _midSm = _mid; _highSm = _high;
        _smooth = 1 - Math.Exp(-1.0 / (0.012 * rate));
    }

    private static double Db(double db) => Math.Pow(10, db / 20);

    /// <summary>
    /// <paramref name="position"/>/<paramref name="total"/>: fotogrammi del passaggio gia' trascorsi e in tutto.
    /// Dopo la fine (posizione oltre total) il deck che entra torna al segnale diretto in 100 ms.
    /// </summary>
    public void Process(float[] target, int samples, long position, long total)
    {
        int ch = _channels, frames = samples / ch;
        for (int f = 0; f < frames; f++)
        {
            long at = position + f;
            if (--_countdown <= 0)
            {
                _countdown = Block;
                double p = Math.Clamp(at / (double)Math.Max(1, total), 0, 1);
                double pNext = Math.Clamp((at + Block) / (double)Math.Max(1, total), 0, 1);
                double g1 = Math.Clamp(_curves.Volume.ValueAt(pNext), 0, 1.5);
                _gainStep = (g1 - _gain) / Block;
                if (_eq)
                {
                    _lowStep = (Db(_curves.LowDb.ValueAt(pNext)) - _low) / Block;
                    _midStep = (Db(_curves.MidDb.ValueAt(pNext)) - _mid) / Block;
                    _highStep = (Db(_curves.HighDb.ValueAt(pNext)) - _high) / Block;
                }
                if (_echoBuf != null)
                {
                    _echoSendStep = (Math.Clamp(_curves.EchoSend.ValueAt(pNext), 0, 1) - _echoSend) / Block;
                    _echoFb = Math.Clamp(_curves.EchoFeedback.ValueAt(p), 0, 0.85);
                }
                if (_reverb != null) _revSendStep = (Math.Clamp(_curves.ReverbSend.ValueAt(pNext), 0, 1) - _revSend) / Block;
                if (_loopBuf != null)
                {
                    _loopWanted = _curves.LoopOn.ValueAt(p) >= 0.5;
                    double div = _curves.LoopDivision.IsEmpty ? 1 : Math.Clamp(_curves.LoopDivision.ValueAt(p), 1 / 64.0, 1);
                    int span = Math.Clamp((int)Math.Round(_loopLen * div), 64, _loopLen);
                    if (span != _loopSpan) { _loopSpan = span; if (_loopState == 2) _loopRead %= span; }
                }
                if (_riserHp.Length > 0)
                {
                    double level = Math.Clamp(_curves.Riser.ValueAt(pNext), 0, 1);
                    _riserStep = (level - _riserLevel) / Block;
                    // Altezza che sale con il livello: da 350 Hz a ~9 kHz.
                    double centre = 350 * Math.Pow(26, Math.Clamp(_curves.Riser.ValueAt(p), 0, 1));
                    foreach (var b in _riserHp) b.HighPass(centre * 0.55, _rate, 1.1);
                    foreach (var b in _riserLp) b.LowPass(Math.Min(_rate * 0.45, centre * 1.9), _rate, 1.1);
                }
                if (_gatePeriod > 0) _gateDepthStep = (Math.Clamp(_curves.GateDepth.ValueAt(pNext), 0, 1) - _gateDepth) / Block;
                if (_filters)
                {
                    // [NOI] I filtri girano sempre: spenti diventano trasparenti (passa-alto a 12 Hz, passa-basso in cima alla
                    // banda) invece di ripartire da zero quando si accendono, che dava uno scatto nel segnale.
                    double hz = _curves.HighPassHz.ValueAt(p), q = Math.Clamp(_curves.HighPassQ.ValueAt(p), 0.3, 8);
                    bool on = hz >= 20 && hz < _rate * 0.45;
                    _hpOn = true;
                    foreach (var b in _hp) b.HighPass(on ? hz : 12, _rate, on ? q : 0.7071);
                    hz = _curves.LowPassHz.ValueAt(p); q = Math.Clamp(_curves.LowPassQ.ValueAt(p), 0.3, 8);
                    double top = Math.Min(20000, _rate * 0.45);
                    on = hz >= 20 && hz < top;
                    _lpOn = true;
                    foreach (var b in _lp) b.LowPass(on ? hz : top, _rate, on ? q : 0.7071);
                }
            }
            _gain += _gainStep;
            if (_eq)
            {
                _low += _lowStep; _mid += _midStep; _high += _highStep;
                _lowSm += (_low - _lowSm) * _smooth; _midSm += (_mid - _midSm) * _smooth; _highSm += (_high - _highSm) * _smooth;
            }
            bool after = at >= total;
            if (!_outgoing && after && _release < 1) _release = Math.Min(1, _release + 1.0 / (0.1 * _rate));
            // Il crossover sfasa: si entra nel filtrato in 50 ms dall'inizio (uscita) e se ne esce in 100 ms (entrata).
            double wet = _outgoing ? Math.Clamp(at / (0.05 * _rate), 0, 1) : 1 - _release;
            int i = f * ch;
            if (_tape != null) TapeFrame(target, i, at, total);
            if (_loopBuf != null) LoopFrame(target, i);
            double gate = 1;
            if (_gatePeriod > 0)
            {
                _gateDepth += _gateDepthStep;
                double ph = (at % (long)_gatePeriod) / _gatePeriod, edge = Math.Min(0.2, 0.0015 * _rate / _gatePeriod);
                double open = ph < 0.5 ? Math.Clamp(ph / edge, 0, 1) * Math.Clamp((0.5 - ph) / edge, 0, 1) : 0;
                gate = 1 - Math.Clamp(_gateDepth, 0, 1) * (1 - open);
            }
            double riser = 0;
            if (_riserHp.Length > 0) { _riserLevel += _riserStep; riser = Math.Clamp(_riserLevel, 0, 1); }
            _echoSend += _echoSendStep; _revSend += _revSendStep;
            // Code di eco e riverbero: si spengono negli ultimi 80 ms del passaggio (dopo, il deck che esce tace).
            double tail = _outgoing ? Math.Clamp((total - at) / (0.08 * _rate), 0, 1) : 1;
            for (int c = 0; c < ch; c++)
            {
                double x = target[i + c], y = x;
                if (_eq)
                {
                    double low = _lp350b[c].Run(_lp350a[c].Run(x));
                    low = _apLb[c].Run(_apLa[c].Run(low)) + _apHb[c].Run(_apHa[c].Run(low)); // stessa fase delle altre due bande
                    double rest = _hp350b[c].Run(_hp350a[c].Run(x));
                    double mid = _lp4kb[c].Run(_lp4ka[c].Run(rest)), high = _hp4kb[c].Run(_hp4ka[c].Run(rest));
                    y = low * _lowSm + mid * _midSm + high * _highSm;
                }
                if (_filters)
                {
                    if (_hpOn) y = _hp[c].Run(y);
                    if (_lpOn) y = _lp[c].Run(y);
                }
                y = x + (y - x) * wet;
                double g = after && !_outgoing ? 1 : after ? 0 : _gain;
                double outSample = y * g * gate;
                if (_echoBuf != null)
                {
                    int at2 = _echoPos * ch + c;
                    double delayed = _echoBuf[at2];
                    double thinner = _echoLp[c].Run(_echoHp[c].Run(delayed));
                    // Mandata dopo il volume: quando A e' sceso non entra piu' nulla, restano solo le ripetizioni.
                    _echoBuf[at2] = (float)(y * g * Math.Max(0, _echoSend) + thinner * _echoFb);
                    if (!after) outSample += delayed * 0.85 * tail;
                }
                if (_reverb != null && !after) outSample += _reverb.Run((float)(y * g * Math.Max(0, _revSend)), c) * 0.7 * tail;
                if (riser > 0.001 && !after)
                {
                    _noise ^= _noise << 13; _noise ^= _noise >> 17; _noise ^= _noise << 5;
                    double white = (_noise & 0xFFFFFF) / (double)0xFFFFFF * 2 - 1;
                    double shaped = _riserLp[c].Run(_riserHp[c].Run(white));
                    outSample += shaped * riser * riser * 0.14 * tail;
                }
                target[i + c] = (float)outSample;
            }
            if (_echoBuf != null) _echoPos = (_echoPos + 1) % _echoLen;
        }
    }

    /// <summary>
    /// Brake e spinback. Il nastro registra sempre gli ultimi 4 s; al momento dell'effetto si legge da li' a velocita'
    /// variabile (interpolazione lineare): il brake rallenta fino a fermarsi (l'altezza scende come un giradischi spento),
    /// lo spinback va all'indietro veloce e si spegne.
    /// </summary>
    private void TapeFrame(float[] target, int i, long at, long total)
    {
        int ch = _channels;
        for (int c = 0; c < ch; c++) _tape![_tapeWrite * ch + c] = target[i + c];
        if (_tapeMode == 0)
        {
            double p = at / (double)Math.Max(1, total);
            if (_curves.BrakeAt >= 0 && p >= _curves.BrakeAt) { _tapeMode = 1; _tapeRead = _tapeWrite; _tapeStart = at; }
            else if (_curves.SpinAt >= 0 && p >= _curves.SpinAt) { _tapeMode = 2; _tapeRead = _tapeWrite; _tapeStart = at; }
        }
        if (_tapeMode != 0)
        {
            double seconds = (at - _tapeStart) / (double)_rate;
            double len = _tapeMode == 1 ? Math.Max(0.15, _curves.BrakeSeconds) : Math.Max(0.2, _curves.SpinSeconds);
            double u = Math.Clamp(seconds / len, 0, 1);
            double speed, level;
            if (_tapeMode == 1) { speed = Math.Max(0, 1 - u); level = Math.Clamp((1 - u) * 3, 0, 1); }
            else
            {
                // Spinback: in 60 ms da +1 a -3,2, poi si ferma piano; il volume cala con il quadrato.
                double kick = Math.Min(1, seconds / 0.06);
                speed = u < 1 ? (1 - kick) + kick * (-3.2 * Math.Pow(1 - u, 1.6)) : 0;
                level = Math.Pow(1 - u, 2);
            }
            double pos = _tapeRead;
            int i0 = ((int)Math.Floor(pos) % _tapeLen + _tapeLen) % _tapeLen, i1 = (i0 + 1) % _tapeLen;
            double frac = pos - Math.Floor(pos);
            for (int c = 0; c < ch; c++)
            {
                double v = _tape![i0 * ch + c] + (_tape[i1 * ch + c] - _tape[i0 * ch + c]) * frac;
                target[i + c] = (float)(v * level);
            }
            _tapeRead += speed;
        }
        _tapeWrite = (_tapeWrite + 1) % _tapeLen;
    }

    /// <summary>
    /// Loop: all'accensione registra una battuta (piu' 8 ms) mentre la lascia passare, poi la ripete. L'inizio di ogni
    /// giro si sfuma con il seguito registrato oltre la fine (giunzione senza scatti); entrata e uscita dal loop in 8 ms.
    /// </summary>
    private void LoopFrame(float[] target, int i)
    {
        int ch = _channels;
        switch (_loopState)
        {
            case 0:
                if (_loopWanted) { _loopState = 1; _loopWrite = 0; goto case 1; }
                return;
            case 1:
                for (int c = 0; c < ch; c++) _loopBuf![_loopWrite * ch + c] = target[i + c];
                if (!_loopWanted) { _loopState = 0; return; }
                if (++_loopWrite >= _loopLen + _loopX) { _loopState = 2; _loopRead = _loopX; _loopFade = 0; }
                return;
            default:
            {
                // Uscita dal loop: si torna al segnale vivo in 8 ms.
                if (!_loopWanted) { if (--_loopFade <= 0) { _loopState = 0; return; } }
                else if (_loopFade < _loopX) _loopFade++;
                double k = _loopFade / (double)_loopX;
                for (int c = 0; c < ch; c++)
                {
                    double v = _loopBuf![_loopRead * ch + c];
                    if (_loopRead < _loopX)
                    {
                        // Giunzione: l'inizio del giro si sfuma con quanto registrato subito dopo la fine del giro.
                        double s = _loopRead / (double)_loopX;
                        v = v * s + _loopBuf[(_loopSpan + _loopRead) * ch + c] * (1 - s);
                    }
                    target[i + c] = (float)(target[i + c] * (1 - k) + v * k);
                }
                if (++_loopRead >= _loopSpan) _loopRead = 0;
                return;
            }
        }
    }

    /// <summary>Riverbero diffuso: otto ritardi smorzati e decorrelati con una matrice di Householder (coda lunga, senza ripetizioni nette).</summary>
    private sealed class Wash
    {
        private readonly float[][][] _lines;
        private readonly int[][] _positions;
        private readonly float[][] _damped;
        private readonly Bq[] _hp;
        private readonly float[] _values = new float[8];
        internal Wash(int rate, int channels)
        {
            _lines = new float[channels][][]; _positions = new int[channels][]; _damped = new float[channels][]; _hp = new Bq[channels];
            int[] lengths = { 1493, 1601, 1747, 1867, 1999, 2131, 2281, 2423 };
            for (int c = 0; c < channels; c++)
            {
                _lines[c] = new float[8][]; _positions[c] = new int[8]; _damped[c] = new float[8]; _hp[c] = new Bq(); _hp[c].HighPass(250, rate, 0.7071);
                for (int j = 0; j < 8; j++) _lines[c][j] = new float[Math.Max(1, (int)((lengths[j] + c * 73) * 1.6 * rate / 44100.0))];
            }
        }
        internal float Run(float input, int channel)
        {
            float x = (float)_hp[channel].Run(input), sum = 0;
            var lines = _lines[channel]; var pos = _positions[channel]; var damped = _damped[channel];
            for (int j = 0; j < 8; j++)
            {
                float v = lines[j][pos[j]];
                _values[j] = damped[j] = .6f * v + .4f * damped[j]; sum += _values[j];
            }
            float output = 0;
            for (int j = 0; j < 8; j++)
            {
                output += _values[j] * ((j & 1) == 0 ? 1 : -1);
                lines[j][pos[j]] = x * .353553f + .84f * (_values[j] - sum * .25f);
                pos[j] = (pos[j] + 1) % lines[j].Length;
            }
            return output * .353553f;
        }
    }

    /// <summary>Biquad RBJ (forma diretta trasposta II).</summary>
    private sealed class Bq
    {
        private double _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;
        public void Reset() { _z1 = _z2 = 0; }
        public void LowPass(double hz, int rate, double q)
        {
            hz = Math.Min(hz, rate * 0.45);
            double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            _b0 = (1 - cos) / 2 / a0; _b1 = (1 - cos) / a0; _b2 = _b0; _a1 = -2 * cos / a0; _a2 = (1 - alpha) / a0;
        }
        public void HighPass(double hz, int rate, double q)
        {
            hz = Math.Min(hz, rate * 0.45);
            double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            _b0 = (1 + cos) / 2 / a0; _b1 = -(1 + cos) / a0; _b2 = _b0; _a1 = -2 * cos / a0; _a2 = (1 - alpha) / a0;
        }
        public double Run(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
