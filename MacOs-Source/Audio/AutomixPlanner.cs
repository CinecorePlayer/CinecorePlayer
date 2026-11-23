#nullable enable
using System;

namespace CinecorePlayer2025.Audio;

internal sealed record AutomixPreferences(string AutomixMode, string AutomixIntensity, bool AutomixTempoSync, bool AutomixTrimSilence, string ReplayGain)
{
    public bool AutomixEffects => AutomixIntensity is not ("gentle" or "crossfade");
    public static AutomixPreferences From(CinecoreAudioSettings settings) => new(settings.AutomixMode, settings.AutomixIntensity, settings.AutomixTempoSync, settings.AutomixTrimSilence, settings.ReplayGain);
}

/// <summary>
/// Automix a cue (artifacts/automix-cued/SPEC.md). Il planner e' in AutomixPlanner.Cued.cs, le regole verificate in
/// AutomixRules.cs, le curve dei preset in TransitionCurves.cs. Il planner storico e' stato rimosso (copia in
/// artifacts/automix-cued/removed).
/// </summary>
internal static partial class AutomixPlanner
{
    /// <summary>
    /// Un passaggio deciso: inizio (tempo di A), durata, salita di B, aggancio (t0 di A, s0 di B), tempo di B, guadagno di B e curve.
    /// Swap e Cut restano per gli stili del renderer usati anche dal salto manuale.
    /// </summary>
    internal sealed record MixPlan(TransitionStyle Style, double Start, double Fade, double FadeIn, double SyncT0, double S0,
        double Bpm, double Swap, double Ratio, double Cut, double GainDb = 0, string Kind = "", double Score = 0,
        double IncomingAtSwap = 0.7071067811865476, double OutgoingRatio = 1, double TempoRelation = 0,
        double TempoReturnSeconds = 8, TransitionCurves? Curves = null);

    internal static MixPlan? Plan(TrackMixProfile current, TrackMixProfile next, double endAt, double position, AutomixPreferences settings,
        string fromPath = "", string nextPath = "") =>
        PlanCued(current, next, endAt, position, settings, fromPath, nextPath);

    /// <summary>Livello medio (dB) dei secondi misurati fra <paramref name="from"/> e <paramref name="to"/>, NaN se non misurati.</summary>
    private static double MeanLevel(TrackMixProfile profile, double from, double to)
    {
        double sum = 0; int count = 0;
        for (double t = Math.Floor(from); t < to; t += 1)
        {
            double level = profile.LevelAt(t);
            if (!double.IsNaN(level) && level > -80) { sum += level; count++; }
        }
        return count > 0 ? sum / count : double.NaN;
    }
}
