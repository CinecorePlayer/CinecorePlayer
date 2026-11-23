#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025.Audio;

/// <summary>
/// [NOI, 2026-10-27] Scelta della tecnica da DJ per ogni passaggio. Prima l'automix aveva un solo modo per caso (fusione a
/// tempo, eco quando non si poteva, riverbero per i brani lenti) e suonava sempre uguale. Ora ogni caso ha piu' tecniche
/// adatte, con pesi che dipendono dai brani e dall'intensita' scelta; la scelta e' stabile per la stessa coppia (il piano si
/// rifa' piu' volte prima del passaggio) e non ripete la tecnica del passaggio precedente.
/// </summary>
internal static partial class AutomixPlanner
{
    private static string _pairKey = "", _pairTechnique = "", _previousTechnique = "";

    /// <summary>Ultima tecnica scelta (per i log e per il banco di prova).</summary>
    internal static string LastTechnique => _pairTechnique;

    private static string Pick(string fromPath, string nextPath, List<(string Name, double Weight)> options)
    {
        options = options.Where(o => o.Weight > 0).ToList();
        if (options.Count == 0) return "";
        // Solo per il banco di prova: forza una tecnica (se adatta al caso).
        if (Environment.GetEnvironmentVariable("CINECORE_AUTOMIX_FORCE") is { Length: > 0 } forced && options.Any(o => o.Name == forced))
            return forced;
        string key = (Path.GetFileName(fromPath) + "|" + Path.GetFileName(nextPath)).ToLowerInvariant();
        string avoid = key == _pairKey ? _previousTechnique : _pairTechnique;
        if (options.Count > 1) options = options.Select(o => (o.Name, o.Weight * (o.Name == avoid ? 0.3 : 1))).ToList();
        // FNV-1a della coppia: stessa coppia, stessa scelta.
        uint hash = 2166136261;
        foreach (char ch in key) { hash ^= ch; hash *= 16777619; }
        double roll = (hash % 100000) / 100000.0 * options.Sum(o => o.Weight);
        string chosen = options[^1].Name;
        foreach (var (name, weight) in options) { if (roll < weight) { chosen = name; break; } roll -= weight; }
        if (key != _pairKey) { _previousTechnique = _pairTechnique; _pairKey = key; }
        _pairTechnique = chosen;
        return chosen;
    }

    /// <summary>
    /// [NOI, 2026-11-22] Brano "da ballo" alla fine (<paramref name="tail"/>) o all'inizio: battito molto stabile, tempo da
    /// pista (100-150) e percussioni in evidenza (media della mappa musicale). Misurato sulla libreria: le colonne sonore
    /// stanno sotto 0,35 di percussioni anche quando hanno un battito; "Uno Puncho" (128, 0,39) e simili sopra.
    /// </summary>
    internal static bool Dance(TrackMixProfile t, bool tail)
    {
        var grid = tail ? t.Tail : t.Head;
        double confidence = tail ? t.TailConfidence : t.HeadConfidence, bpm = tail ? t.TailBpm : t.HeadBpm;
        var map = tail ? t.TailMusic : t.HeadMusic;
        if (!grid.Reliable || confidence < 2.5 || bpm < 100 || bpm > 150 || map == null || map.Frames.Length == 0) return false;
        double sum = 0;
        foreach (var f in map.Frames) sum += f.Percussion;
        return sum / map.Frames.Length >= 0.36;
    }

    private static double MeanVoice(TrackMixProfile t, double from, double to)
    {
        double sum = 0; int n = 0;
        for (double x = from; x < to; x += 0.5) { double v = t.VoiceAt(x); if (double.IsFinite(v)) { sum += v; n++; } }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>
    /// Passaggio a tempo gia' deciso (finestra e battute): resta la fusione scelta, oppure diventa una Costruzione (loop
    /// di B, roll di A, riser e drop) o un Gate in uscita.
    /// </summary>
    private static TransitionCurves ChooseMatchedTechnique(TrackMixProfile a, TrackMixProfile b, CuedCandidate best, TransitionCurves chosen,
        AutomixPreferences s, string fromPath, string nextPath, AutomixRuleSet rules)
    {
        if (!s.AutomixEffects && s.AutomixIntensity == "gentle") { Pick(fromPath, nextPath, new() { ("keep", 1) }); return chosen; }
        double len = best.A1 - best.A0, barA = 4 * a.Tail.Period, barB = 4 * b.Head.Period;
        bool dj = s.AutomixIntensity == "dj", gentle = s.AutomixIntensity == "gentle";
        var options = new List<(string, double)> { ("keep", gentle ? 5 : dj ? 2 : 3) };
        // [NOI, 2026-11-22] Costruzione e gate sono tecniche da pista: solo fra brani da ballo.
        bool danceA = Dance(a, true), danceB = Dance(b, false);
        if (danceA && danceB && best.Bars >= 8 && best.Bars <= 16 && barA > 0 && barB > 0)
        {
            double w = gentle ? 0.4 : dj ? 3.2 : 2.2;
            // Il loop di A ripete la battuta prima della seconda meta'; quello di B la sua prima battuta: meglio senza voce.
            double half = best.A0 + len / 2;
            if (MeanVoice(a, half - barA, half) >= rules.VocalThreshold) w *= 0.4;
            if (MeanVoice(b, best.B0, best.B0 + barB) >= rules.VocalThreshold) w *= 0.4;
            options.Add(("build", w));
        }
        if (danceA && best.Bars >= 4 && best.Bars <= 16) options.Add(("gate", gentle ? 0.2 : dj ? 1.6 : 1.1));
        string pick = Pick(fromPath, nextPath, options);
        var result = pick switch
        {
            "build" => TransitionCurves.Build(best.Bars, a.Tail.Period, b.Head.Period),
            "gate" => TransitionCurves.GateOut(best.Bars, a.Tail.Period),
            _ => chosen,
        };
        if (!ReferenceEquals(result, chosen))
            Dbg.Log($"[AUTOMIX] technique '{pick}' instead of {chosen.Name} ({best.Bars} bars)", Dbg.LogLevel.Info);
        return result;
    }

    /// <summary>
    /// Passaggio senza sovrapposizione a tempo (A ritmato): oltre all'eco, roll e drop, filtro con riser, brake e spinback.
    /// <paramref name="tc"/> = taglio/drop sul tempo di A (gia' sulla battuta), <paramref name="tb"/> = ingresso di B sul suo
    /// battito. Null = si usa l'eco (codice gia' esistente).
    /// </summary>
    private static MixPlan? PlanCutTechnique(TrackMixProfile a, TrackMixProfile b, double tc, double tb, double beat, double bar,
        double db, double position, double s0Base, AutomixPreferences s, bool dj, string fromPath, string nextPath, string origin)
    {
        bool gentle = s.AutomixIntensity == "gentle", hard = s.AutomixIntensity == "dj";
        var options = new List<(string, double)> { ("echo", gentle ? 3 : hard ? 1.4 : 2) };
        // B al drop deve essere al suo battito: se B dovrebbe partire prima del suo inizio, in DJ edit si salta al battito
        // seguente; con i brani interi la tecnica lunga si scarta.
        double? EntryFor(double before)
        {
            double s0 = tb - before;
            if (s0 >= 0) return s0;
            if (!dj || !b.Head.Reliable || b.HeadDownbeatConfidence < 1.05) return null;
            double barB = 4 * b.Head.Period;
            double next = b.HeadDownbeat + Math.Ceiling((before - b.HeadDownbeat) / barB) * barB;
            return next - before >= 0 && next < Math.Min(40, db * 0.4) ? next - before : null;
        }
        // [NOI, 2026-11-22] Roll e riser portano a un drop di B: servono due brani da ballo. Brake e spinback solo in DJ e
        // con A da ballo (prima uscivano su colonne sonore, piano, violino). In "Brani interi" niente spinback.
        bool danceA = Dance(a, true), danceB = Dance(b, false);
        bool rollOk = danceA && danceB && tc - 3 * bar > position + 0.6 && EntryFor(3 * bar) != null;
        bool riserOk = danceA && danceB && tc - 2 * bar > position + 0.6 && EntryFor(2 * bar) != null;
        if (rollOk) options.Add(("roll", gentle ? 0.6 : hard ? 2.2 : 1.6));
        if (riserOk) options.Add(("riser", gentle ? 1.4 : 1.5));
        if (hard && danceA) options.Add(("brake", 0.9));
        if (hard && danceA && dj) options.Add(("spin", 0.7));
        string pick = Pick(fromPath, nextPath, options);
        double gain = LevelMatch(a, b, tc, tb + 1, s);
        MixPlan? Make(string kind, double start, double length, double s0, TransitionCurves curves, string detail)
        {
            if (start <= position + 0.5 || s0 < 0 || s0 + length >= db) return null;
            LastCuedDecision = $"{kind}: {detail}, A cut at {tc:0.00}s ({origin}), B from {s0 + (tc - start):0.00}s, length {length:0.00}s";
            Dbg.Log($"[AUTOMIX] cued '{Path.GetFileName(fromPath)}' -> '{Path.GetFileName(nextPath)}': {LastCuedDecision}", Dbg.LogLevel.Info);
            return new MixPlan(TransitionStyle.Curves, start, length, length, double.NaN, s0, 60 / beat, tc, 1, double.NaN, gain, kind, 0, Curves: curves);
        }
        switch (pick)
        {
            case "roll":
            {
                double drop = 3 * bar, length = drop + 0.35;
                double s0 = EntryFor(drop)!.Value;
                return Make("roll drop", tc - drop, length, s0, TransitionCurves.RollDrop(length, drop, bar, beat), "loop roll over 2 bars, riser");
            }
            case "riser":
            {
                double drop = 2 * bar, length = drop + 0.35;
                double s0 = EntryFor(drop)!.Value;
                return Make("filter riser", tc - drop, length, s0, TransitionCurves.FilterRiser(length, drop), "high-pass sweep with riser over 2 bars");
            }
            case "brake":
            {
                double pre = 0.1, brake = Math.Clamp(beat * 2, 0.6, 1.4), inAt = pre + brake * 0.6, length = pre + brake + 0.25;
                return Make("brake", tc - pre, length, Math.Max(0, tb - inAt), TransitionCurves.Brake(length, pre, brake, inAt), $"tape stop {brake:0.00}s");
            }
            case "spin":
            {
                double pre = 0.1, spin = Math.Clamp(beat * 2, 0.7, 1.2), inAt = pre + 0.12, length = pre + spin + 0.2;
                return Make("spinback", tc - pre, length, Math.Max(0, tb - inAt), TransitionCurves.Spinback(length, pre, spin, inAt), $"spinback {spin:0.00}s");
            }
            default:
                return null;
        }
    }
}
