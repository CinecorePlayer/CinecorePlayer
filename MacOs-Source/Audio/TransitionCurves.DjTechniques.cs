#nullable enable
using System;

namespace CinecorePlayer2025.Audio;

/// <summary>
/// [NOI, 2026-10-27] Tecniche da DJ costruite con gli attrezzi dei deck (loop, loop roll, riser, gate, brake, spinback, eco,
/// filtri). Ognuna risolve un caso diverso: brani a tempo che si possono sovrapporre, brani che non si agganciano, finali
/// netti, brani lenti. Le usano l'automix (AutomixPlanner.DjTechniques.cs) e il mix manuale (selettore "Effetto").
/// Posizioni normalizzate: 0 = inizio del passaggio, 1 = fine.
/// </summary>
internal sealed partial record TransitionCurves
{
    public const int BuildId = 40, RollDropId = 41, BrakeId = 42, SpinbackId = 43, GateOutId = 44, FilterRiserId = 45;

    /// <summary>Effetti del mix manuale oltre ai primi quattro (FxNone, FxEcho, FxReverb, FxLoop).</summary>
    public const int FxRoll = 4, FxBrake = 5, FxSpin = 6, FxGate = 7, FxRiser = 8, FxBuild = 9;
    public const int FxCount = 10;

    private static AutomationCurve Steps(params (double Start, double End, double Value)[] steps)
    {
        var segments = new CurveSegment[steps.Length];
        for (int i = 0; i < steps.Length; i++) segments[i] = Hold(steps[i].Start, steps[i].End, steps[i].Value);
        return Curve(steps.Length > 0 ? steps[0].Value : 1, segments);
    }

    /// <summary>Divisioni del loop roll da <paramref name="from"/> a <paramref name="to"/>: 1, 1/2, 1/4, 1/8, 1/16 (sempre piu' fitte).</summary>
    private static AutomationCurve RollSteps(double from, double to)
    {
        double span = to - from;
        return Steps((0, from, 1), (from, from + span * 0.25, 1), (from + span * 0.25, from + span * 0.5, 0.5),
            (from + span * 0.5, from + span * 0.75, 0.25), (from + span * 0.75, from + span * 0.875, 0.125), (from + span * 0.875, 1, 0.0625));
    }

    /// <summary>
    /// Costruzione (il "ponte" fatto a mano): i due brani a tempo. Nella prima meta' B entra con la sua prima battuta in
    /// loop, senza bassi; nella seconda meta' A ripete la sua battuta sempre piu' fitta (loop roll) mentre si assottiglia
    /// e sale il riser; sul colpo finale A sparisce e B riparte libero con i bassi: il drop.
    /// </summary>
    public static TransitionCurves Build(int bars, double beatA, double beatB)
    {
        bars = Math.Clamp(bars, 4, 32);
        double bar = 1.0 / bars, drop = 0.985;
        var outDeck = Deck(Curve(1, Hold(0, drop, 1), Line(drop, 1, 1, 0)),
            hp: Sweep(0.5, drop, 40, 2600, 12), hpq: Curve(0.7071, Line(0.5, 0.6, 0.7071, 1.15), Hold(0.6, 1, 1.15))) with
        {
            LoopOn = Curve(0, Hold(0, 0.5 - bar, 0), Hold(0.5 - bar, 1, 1)), LoopBeats = 4, BeatSeconds = beatA,
            LoopDivision = RollSteps(0.5, drop),
            Riser = Curve(0, Hold(0, 0.45, 0), Line(0.45, drop, 0, 1), Line(drop, 1, 1, 0)),
        };
        var inDeck = Deck(Curve(0, new CurveSegment(0, 0.3, new[] { (0d, 0d), (0.6, 0.5), (1d, 0.6) }), Line(0.3, drop, 0.6, 0.82), Line(drop, 1, 0.82, 1)),
            hp: Curve(0, Hold(0, drop - 0.005, 240), Line(drop - 0.005, 1, 240, 10))) with
        {
            LoopOn = Curve(1, Hold(0, drop, 1), Hold(drop, 1, 0)), LoopBeats = 4, BeatSeconds = beatB,
        };
        return new(BuildId, "build", outDeck, inDeck);
    }

    /// <summary>
    /// Roll e drop: A ripete la sua ultima battuta sempre piu' fitta, si assottiglia con il riser, e sul drop entra B secco
    /// sul suo battito. Non serve che i tempi siano uguali: i due brani non suonano mai insieme.
    /// </summary>
    /// <param name="length">Durata (s).</param><param name="dropAt">Drop, secondi dall'inizio.</param><param name="barSeconds">Battuta di A (s).</param>
    public static TransitionCurves RollDrop(double length, double dropAt, double barSeconds, double beat)
    {
        length = Math.Max(1, length);
        double pd = Math.Clamp(dropAt / length, 0.3, 0.97), pc = Math.Clamp(barSeconds / length, 0.05, pd - 0.05);
        double ramp = Math.Min(0.04 / length, (1 - pd) * 0.5);
        var outDeck = Deck(Curve(1, Line(0, pc, 1, 0.8), Hold(pc, pd - 0.004, 0.8), Line(pd - 0.004, pd, 0.8, 0), Hold(pd, 1, 0)),
            hp: Sweep(pc, pd, 40, 3200, 12), hpq: Curve(0.7071, Line(pc, pc + 0.1, 0.7071, 1.15), Hold(pc + 0.1, 1, 1.15))) with
        {
            LoopOn = Curve(1, Hold(0, 1, 1)), LoopBeats = 4, BeatSeconds = beat,
            LoopDivision = RollSteps(pc, pd),
            Riser = Curve(0, Line(0, pd, 0, 1), Line(pd, Math.Min(1, pd + 0.02), 1, 0), Hold(Math.Min(1, pd + 0.02), 1, 0)),
        };
        var inDeck = Deck(Curve(0, Hold(0, pd, 0), Line(pd, Math.Min(1, pd + ramp), 0, 1), Hold(Math.Min(1, pd + ramp), 1, 1)));
        return new(RollDropId, "roll-drop", outDeck, inDeck);
    }

    /// <summary>Brake: A si ferma come un giradischi spento (l'altezza scende in un paio di battiti); B parte mentre si ferma.</summary>
    public static TransitionCurves Brake(double length, double brakeAt, double brakeSeconds, double inAt)
    {
        length = Math.Max(0.5, length);
        double pb = Math.Clamp(brakeAt / length, 0, 0.9), pe = Math.Clamp((brakeAt + brakeSeconds) / length, pb + 0.01, 1);
        double pi = Math.Clamp(inAt / length, 0, 0.98), ramp = Math.Min(0.03 / length, (1 - pi) * 0.5);
        var outDeck = Deck(Curve(1, Hold(0, pe, 1), Hold(pe, 1, 0))) with { BrakeAt = pb, BrakeSeconds = brakeSeconds };
        var inDeck = Deck(Curve(0, Hold(0, pi, 0), Line(pi, Math.Min(1, pi + ramp), 0, 1), Hold(Math.Min(1, pi + ramp), 1, 1)));
        return new(BrakeId, "brake", outDeck, inDeck);
    }

    /// <summary>Spinback: il disco di A tirato indietro di colpo (va all'indietro e si spegne), B entra sul colpo.</summary>
    public static TransitionCurves Spinback(double length, double spinAt, double spinSeconds, double inAt)
    {
        length = Math.Max(0.5, length);
        double ps = Math.Clamp(spinAt / length, 0, 0.9), pe = Math.Clamp((spinAt + spinSeconds) / length, ps + 0.01, 1);
        double pi = Math.Clamp(inAt / length, 0, 0.98), ramp = Math.Min(0.02 / length, (1 - pi) * 0.5);
        var outDeck = Deck(Curve(1, Hold(0, pe, 1), Hold(pe, 1, 0))) with { SpinAt = ps, SpinSeconds = spinSeconds };
        var inDeck = Deck(Curve(0, Hold(0, pi, 0), Line(pi, Math.Min(1, pi + ramp), 0, 1), Hold(Math.Min(1, pi + ramp), 1, 1)));
        return new(SpinbackId, "spinback", outDeck, inDeck);
    }

    /// <summary>
    /// Gate in uscita (a tempo): A viene tagliato a sedicesimi sempre piu' profondamente e si assottiglia, B entra sotto e
    /// si prende i bassi a meta'. Il ritmo del gate tiene insieme i due brani.
    /// </summary>
    public static TransitionCurves GateOut(int bars, double beat)
    {
        double w = Sixteenth(bars);
        var outDeck = Deck(Curve(1, Hold(0, 0.4, 1), new CurveSegment(0.4, 1, PowerS(true))), BassOutAt(0.5, w),
            hp: Sweep(0.55, 1, 60, 1800, 10), hpq: AutomationCurve.Constant(0.9)) with
        {
            GateDepth = Curve(0, Hold(0, 0.12, 0), Line(0.12, 0.55, 0, 1), Hold(0.55, 1, 1)), GateBeats = 0.25, BeatSeconds = beat,
        };
        var inDeck = Deck(Curve(0, new CurveSegment(0, 0.6, PowerS(false)), Hold(0.6, 1, 1)), BassInAt(0.5, w));
        return new(GateOutId, "gate-out", outDeck, inDeck);
    }

    /// <summary>
    /// Filtro con riser (tempi diversi): A si assottiglia con un passa-alto risonante che sale e un riser sopra; sul colpo
    /// A sparisce e B parte secco. Due ritmi che non si sopportano non si sentono mai insieme.
    /// </summary>
    public static TransitionCurves FilterRiser(double length, double dropAt)
    {
        length = Math.Max(1, length);
        double pd = Math.Clamp(dropAt / length, 0.3, 0.97), ramp = Math.Min(0.04 / length, (1 - pd) * 0.5);
        var outDeck = Deck(Curve(1, Line(0, pd * 0.5, 1, 0.8), Hold(pd * 0.5, pd - 0.004, 0.8), Line(pd - 0.004, pd, 0.8, 0), Hold(pd, 1, 0)),
            hp: Sweep(0, pd, 40, 3600, 14), hpq: Curve(0.7071, Line(0, 0.2, 0.7071, 1.2), Hold(0.2, 1, 1.2))) with
        {
            Riser = Curve(0, Line(0, pd, 0, 1), Line(pd, Math.Min(1, pd + 0.02), 1, 0), Hold(Math.Min(1, pd + 0.02), 1, 0)),
        };
        var inDeck = Deck(Curve(0, Hold(0, pd, 0), Line(pd, Math.Min(1, pd + ramp), 0, 1), Hold(Math.Min(1, pd + ramp), 1, 1)));
        return new(FilterRiserId, "filter-riser", outDeck, inDeck);
    }

    /// <summary>Effetti nuovi del mix manuale sopra le curve scelte (volume, EQ, filtro): restano dentro il passaggio.</summary>
    internal TransitionCurves WithDjFx(int fx, int bars, double beatA, double beatB)
    {
        beatA = double.IsFinite(beatA) && beatA > 0.2 && beatA < 1.5 ? beatA : 0.5;
        beatB = double.IsFinite(beatB) && beatB > 0.2 && beatB < 1.5 ? beatB : beatA;
        bars = Math.Max(1, bars);
        var o = Out; var i = In;
        switch (fx)
        {
            case FxRoll:
                o = o with { LoopOn = Curve(0, Hold(0, 0.25, 0), Hold(0.25, 1, 1)), LoopBeats = 4, BeatSeconds = beatA, LoopDivision = RollSteps(0.25 + 1.0 / bars, 1) };
                break;
            case FxBrake:
                o = o with { BrakeAt = 0.6, BrakeSeconds = Math.Clamp(beatA * 2, 0.6, 1.4) };
                break;
            case FxSpin:
                o = o with { SpinAt = 0.6, SpinSeconds = Math.Clamp(beatA * 2, 0.7, 1.2) };
                break;
            case FxGate:
                o = o with { GateDepth = Curve(0, Hold(0, 0.1, 0), Line(0.1, 0.5, 0, 1), Hold(0.5, 1, 1)), GateBeats = 0.25, BeatSeconds = beatA };
                break;
            case FxRiser:
                o = o with { Riser = Curve(0, Line(0, 0.85, 0, 1), Line(0.85, 0.9, 1, 0), Hold(0.9, 1, 0)) };
                break;
            case FxBuild:
                var build = Build(bars, beatA, beatB);
                return build with { PresetId = PresetId + 1000 * fx };
            default:
                return this;
        }
        return this with { Out = o, In = i, PresetId = PresetId + 1000 * fx, Name = Name + "+" + FxName(fx) };
    }

    /// <summary>Qualunque effetto del mix manuale (0..FxCount-1) sopra le curve scelte.</summary>
    internal TransitionCurves ApplyFx(int fx, int bars, double beatA, double beatB)
        => fx <= FxLoop ? WithFx(Math.Max(0, fx), beatA) : WithDjFx(fx, bars, beatA, beatB);

    public static string FxName(int fx) => fx switch
    {
        FxEcho => "echo", FxReverb => "reverb", FxLoop => "loop", FxRoll => "roll", FxBrake => "brake", FxSpin => "spinback",
        FxGate => "gate", FxRiser => "riser", FxBuild => "build", _ => "none",
    };
}
