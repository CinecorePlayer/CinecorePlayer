#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025.Audio;

/// <summary>Un punto musicale da cui uscire (A) o in cui entrare (B), con la sua forza (0-1).</summary>
internal sealed record MixCue(double Time, double Strength, string Origin);

/// <summary>
/// Planner a cue (artifacts/automix-cued/SPEC.md). Le regole marcate [COD] riproducono il planner Auto di Soloist
/// 1.3.10.12 verificato in artifacts/spotify-soloist-lab/claude-re/VERIFICA.md; quelle marcate [NOI] sono scelte nostre.
/// </summary>
internal static partial class AutomixPlanner
{
    internal sealed record CuedCandidate(int Bars, double A0, double A1, double B0, double B1, double Ratio,
        double Preservation, double Cue, double Vocal, double Key, double Genre, double Score, int VocalBins)
    {
        public double[] Components => new[] { Preservation, Cue, Vocal, Key, Genre };
    }

    /// <summary>"content" (scelta dal contenuto, [NOI]) o "spotify" (rotazione SHA-1 riprodotta, [COD]): per i confronti.</summary>
    internal static string CuedPresetMode { get; set; } = "content";

    /// <summary>Ultima decisione, per i log e per il banco di prova.</summary>
    internal static string LastCuedDecision { get; private set; } = "";

    internal static MixPlan? PlanCued(TrackMixProfile a, TrackMixProfile b, double endAt, double position, AutomixPreferences s,
        string fromPath = "", string nextPath = "")
    {
        var rules = AutomixRuleSet.Default;
        if (!double.IsFinite(endAt) || !double.IsFinite(position) || !double.IsFinite(a.Duration) || !double.IsFinite(b.Duration) ||
            a.Duration < 8 || b.Duration < 8 || !double.IsFinite(b.LeadIn) || b.LeadIn >= b.Duration) return null;
        endAt = Math.Min(endAt, a.Duration);
        if (endAt <= position + 1) return null;
        bool dj = s.AutomixMode == "dj";
        double s0Base = s.AutomixTrimSilence ? Math.Max(0, b.LeadIn) : 0;
        // [NOI, 2026-11-22] "Solo dissolvenza": una dissolvenza a potenza costante sui punti musicali, nient'altro.
        if (s.AutomixIntensity == "crossfade") return PlanCrossfade(a, b, endAt, position, s0Base, s, fromPath, nextPath);
        // [NOI] DA = fine udibile di A; DB = durata di B (Spotify usa le durate dei brani).
        double da = endAt, db = b.Duration;
        var exits = ExitCues(a, endAt, position);
        var entries = EntryCues(b, s0Base);
        var bestExit = exits.OrderByDescending(c => c.Strength).FirstOrDefault();
        var bestEntry = entries.OrderByDescending(c => c.Strength).FirstOrDefault();

        var candidates = BarCandidates(a, b, endAt, position, s0Base, da, db, dj, s, bestExit, exits, bestEntry, entries, rules);
        bool forceFade = false;
        if (candidates.Count > 0)
        {
            // [COD #15] battute decrescenti e S; primi 5 per gruppo; fronti di Pareto; poi S, poi battute.
            var sorted = candidates.OrderByDescending(c => c.Bars).ThenByDescending(c => c.Score).ToList();
            var kept = AutomixRules.KeepPerConsecutiveGroup(sorted, c => c.Bars, rules.KeepPerGroup);
            var fronts = AutomixRules.ParetoFronts(kept.Select(c => c.Components).ToList());
            var ranked = kept.Select((c, i) => (c, f: fronts[i])).OrderBy(x => x.f).ThenByDescending(x => x.c.Score).ThenByDescending(x => x.c.Bars).Select(x => x.c).ToList();
            // [NOI] Primo in classifica che non lascia un vuoto di livello oltre 8 dB nemmeno con la rampa lineare.
            var linear = TransitionCurves.Preset(0);
            var holes = ranked.Select(c => PredictedHoleDb(a, b, c.A0, c.A1 - c.A0, c.B0, c.B1 - c.B0, linear)).ToList();
            int pick = holes.FindIndex(h => h <= 8);
            if (pick < 0) pick = holes.IndexOf(holes.Min());   // nessuno sotto 8 dB: il vuoto minore
            // [NOI] Fra i candidati senza vuoto e con punteggio vicino al migliore (>= 85%), il piu' lungo: la classifica
            // di Spotify premia la conservazione dei brani e finiva quasi sempre su 2 battute (4 s, un crossfade).
            if (holes[pick] <= 8)
            {
                double floor = ranked[pick].Score * 0.85;
                int maxBars = s.AutomixIntensity == "gentle" ? 8 : 16;
                for (int i = 0; i < ranked.Count; i++)
                    if (holes[i] <= 8 && ranked[i].Score >= floor && ranked[i].Bars <= maxBars && ranked[i].Bars > ranked[pick].Bars) pick = i;
            }
            var best = ranked[pick];
            // [NOI] Quando un mix a tempo non si puo' fare bene, niente mix: due voci che si sovrappongono (V bassa) o
            // tonalita' in conflitto su un passaggio lungo davano un passaggio "atroce" (Filthy Pop -> Glitter And Grease,
            // fusione di 4 battute con V 0,22). Si va per forza di sfumatura, breve.
            if (best.Vocal < 0.45 || best.Key < 0.5 && best.Bars >= 4)
            {
                forceFade = true;
                Dbg.Log($"[AUTOMIX] cued '{Path.GetFileName(fromPath)}' -> '{Path.GetFileName(nextPath)}': beat mix not safe (V {best.Vocal:0.00}, K {best.Key:0.00}, {best.Bars} bars) -> DJ cut", Dbg.LogLevel.Info);
                goto PlainFade;
            }
            if (!ReferenceEquals(best, ranked[0]))
                Dbg.Log($"[AUTOMIX] cued: top candidate skipped, predicted level hole {holes[0]:0.0} dB; using #{pick} ({holes[pick]:0.0} dB)", Dbg.LogLevel.Info);
            int preset = ChoosePreset(a, b, best, fromPath, nextPath, rules);
            var presetCurves = TransitionCurves.Preset(preset, best.Bars);
            // [NOI, 2026-10-26] Voce di A nella seconda meta' del passaggio: se la prima battuta e' strumentale, A ripete
            // quella (loop) mentre entra B, invece di portarsi dietro la voce sotto il brano nuovo.
            if (preset == 17 && CuedPresetMode != "spotify" && best.Bars >= 4 && best.Bars <= 16 && a.Tail.Reliable)
            {
                double barA = 4 * a.Tail.Period, voice = 0; int vn = 0;
                for (double t = best.A0; t < best.A0 + barA; t += 0.5) { double v = a.VoiceAt(t); if (double.IsFinite(v)) { voice += v; vn++; } }
                if (vn > 0 && voice / vn < rules.VocalThreshold * 0.7) presetCurves = TransitionCurves.LoopOut(best.Bars, a.Tail.Period);
            }
            // [NOI] Fusione, Ascendente e Onda tengono pieni entrambi i brani: va bene per pochi secondi, ma su 8 battute e
            // oltre (17 s in "the architect" -> Awakening) due brani interi sommati diventano assordanti, in cuffia
            // soprattutto. Oltre 4 battute il volume segue una dissolvenza a potenza costante; EQ e filtri restano.
            if (best.Bars > 4 && preset is 2 or 3 or 17 && presetCurves.PresetId != TransitionCurves.LoopOutId)
                presetCurves = preset switch
                {
                    2 => TransitionCurves.Compose(TransitionCurves.VolumeFade, TransitionCurves.EqThreeBand, TransitionCurves.FilterNone, best.Bars),
                    3 => TransitionCurves.Compose(TransitionCurves.VolumeFade, TransitionCurves.EqBassEnd, TransitionCurves.FilterRise, best.Bars),
                    _ => TransitionCurves.Compose(TransitionCurves.VolumeFade, TransitionCurves.EqBassMid, TransitionCurves.FilterLowPass, best.Bars),
                };
            var curves = AvoidHole(a, b, best.A0, best.A1 - best.A0, best.B0, best.B1 - best.B0, presetCurves, best.Bars);
            if (CuedPresetMode != "spotify") curves = ChooseMatchedTechnique(a, b, best, curves, s, fromPath, nextPath, rules);
            double fade = best.A1 - best.A0;
            double ratio = s.AutomixTempoSync ? best.Ratio : 1;
            double gain = LevelMatch(a, b, best.A0, best.B0 + fade * ratio, s);
            LastCuedDecision = $"bars {best.Bars} A {best.A0:0.00}-{best.A1:0.00}s B {best.B0:0.00}-{best.B1:0.00}s tempo {ratio:0.000} " +
                $"S {best.Score:0.00} (B {best.Preservation:0.00} C {best.Cue:0.00} V {best.Vocal:0.00} K {best.Key:0.00} G {best.Genre:0.00}) preset {curves.Name}, {candidates.Count} candidates";
            Dbg.Log($"[AUTOMIX] cued '{Path.GetFileName(fromPath)}' -> '{Path.GetFileName(nextPath)}': {LastCuedDecision}", Dbg.LogLevel.Info);
            return new MixPlan(TransitionStyle.Curves, best.A0, fade, fade, best.A0, best.B0, a.TailBpm, best.A0 + fade / 2, ratio, double.NaN,
                gain, $"cued {best.Bars} bars {curves.Name}", best.Score, Curves: curves);
        }

        PlainFade:
        // [NOI, 2026-10-26] Quando i due brani non si possono sovrapporre a tempo, un DJ non fa una dissolvenza: taglia A su
        // una battuta lasciandone solo l'eco e fa partire B sul suo battito (brani ritmati), oppure scioglie A in un
        // riverbero mentre entra B (brani lenti o senza battito). La dissolvenza resta solo come ultima possibilita'.
        // [NOI, 2026-11-22] Delicata: mai tagli ne' effetti, si va per dissolvenza.
        if (CuedPresetMode != "spotify" && s.AutomixIntensity != "gentle" && PlanDjCut(a, b, da, db, position, s0Base, exits, entries, s, dj, fromPath, nextPath) is { } cut)
            return cut;
        // [COD #19, #20] ripiego: cue d'uscita piu' tardo, cue d'ingresso piu' precoce.
        double? cueA = exits.Count > 0 ? exits.Max(c => c.Time) : null;
        double? cueB = entries.Count > 0 ? entries.Min(c => c.Time) : null;
        var fb = AutomixRules.Fallback(da, db, cueA, cueB, rules);
        // [NOI] in "Brani interi" il ripiego a cue vale solo se A non perde piu' di 2 s oltre la coda.
        if (fb.CueBased && !dj && da - fb.EndA > 2 + Math.Max(0, da - Math.Max(a.OutroStart, 0)))
            fb = AutomixRules.Fallback(da, db, null, null, rules);
        double length = Math.Max(1, fb.EndA - fb.StartA);           // [NOI] mai sotto 1 s
        double startA = Math.Min(fb.StartA, da - length);
        // B parte dal primo suono se il ripiego e' "normale" (Spotify: da 0).
        double startB = fb.CueBased ? fb.StartB : s0Base;
        // [NOI] Ripiego musicale: Spotify qui fa una dissolvenza di 5 s (preset 1), che a orecchio e' un crossfade.
        // Noi allunghiamo il passaggio attorno agli stessi punti (6 / 10 / 14 s secondo l'intensita', mai oltre il
        // 20% di un brano) e lo facciamo con i filtri, cosi' due ritmi diversi non si scontrano a piena banda.
        double target = forceFade ? 5 : s.AutomixIntensity == "gentle" ? 8 : s.AutomixIntensity == "dj" ? 12 : 10;
        double longer = Math.Min(target, Math.Min(0.2 * da, 0.2 * db));
        if (CuedPresetMode != "spotify" && longer > length + 0.5)
        {
            double centerA = fb.CueBased ? startA + length / 2 : da - longer / 2;
            double centerB = fb.CueBased ? startB + length / 2 : s0Base + longer / 2;
            double newA = Math.Min(centerA - longer / 2, da - longer);
            double newB = Math.Max(s0Base, centerB - longer / 2);
            if (newA > position + 0.5 && newB + longer < db) { startA = newA; startB = newB; length = longer; }
            // Atterraggio: se B ha un ingresso forte (cambio di sezione, entrata d'energia) poco dopo l'inizio, il passaggio
            // finisce li': A si chiude quando B "parte", invece di sovrapporsi alla sua esplosione.
            var landing = entries.Where(c => c.Origin is "section" or "energy" && c.Strength >= 0.7 &&
                                             c.Time >= s0Base + Math.Max(5, length * 0.6) && c.Time <= s0Base + length * 1.5)
                                 .OrderByDescending(c => c.Strength).ThenBy(c => Math.Abs(c.Time - (s0Base + length))).FirstOrDefault();
            if (landing != null && !forceFade)
            {
                // In "Brani interi" B non salta l'introduzione: il passaggio parte dal primo suono di B.
                double l = dj ? Math.Min(longer * 1.2, landing.Time - s0Base) : landing.Time - s0Base;
                double a0 = Math.Min(startA + length - l, da - l);
                if (l >= 5 && l <= longer * 1.2 + 1e-6 && a0 > position + 0.5) { startA = a0; length = l; startB = landing.Time - l; }
            }
        }
        if (startA <= position + 0.5) return null;
        // Sfumatura obbligata: dissolvenza a S semplice, nessun filtro ne' scambio di bande.
        int fbPreset = CuedPresetMode == "spotify" || forceFade ? 1 : ChooseFallbackPreset(a, b, startA, length, startB, rules, s, fromPath, nextPath);
        var fbCurves = fbPreset == CrossfadeId ? TransitionCurves.Crossfade() : TransitionCurves.Preset(fbPreset, 0);
        var fallbackCurves = AvoidHole(a, b, startA, length, startB, length, fbCurves, 0); // [COD #17] preset 1 / [NOI]
        LastCuedDecision = $"fallback {(fb.CueBased ? "cue" : "normal")} L {fb.Length:0.00}s -> {length:0.00}s {fallbackCurves.Name} A {startA:0.00}-{startA + length:0.00}s B from {startB:0.00}s " +
            $"(exit cues {exits.Count}, entry cues {entries.Count})";
        Dbg.Log($"[AUTOMIX] cued '{Path.GetFileName(fromPath)}' -> '{Path.GetFileName(nextPath)}': {LastCuedDecision}", Dbg.LogLevel.Info);
        return new MixPlan(TransitionStyle.Curves, startA, length, length, double.NaN, startB, 0, startA + length / 2, 1, double.NaN,
            LevelMatch(a, b, startA, startB + length, s), fb.CueBased ? "cued fallback" : "fallback crossfade", 0, Curves: fallbackCurves);
    }

    /// <summary>
    /// [NOI] Passaggio da DJ senza sovrapposizione a tempo. A ritmato: echo out sul cue d'uscita piu' forte (messo sulla
    /// battuta), B secco sul suo primo battito. A senza battito: wash out (riverbero) lungo, B che si apre sotto.
    /// </summary>
    private static MixPlan? PlanDjCut(TrackMixProfile a, TrackMixProfile b, double da, double db, double position, double s0Base,
        List<MixCue> exits, List<MixCue> entries, AutomixPreferences s, bool dj, string fromPath, string nextPath)
    {
        bool rhythmic = a.Tail.Reliable && a.TailConfidence >= 1.6 && a.Tail.Period is > 0.25 and < 1.2;
        // [NOI, 2026-11-22] I tagli da DJ (eco, roll, riser, brake, spinback) solo se A e' un brano da ballo; in DJ l'eco
        // anche sugli altri. Prima bastava un battito stabile: le colonne sonore finivano in spinback e tape stop.
        bool danceA = Dance(a, true);
        if (rhythmic && !danceA && s.AutomixIntensity != "dj") return null;
        // In DJ, con A non da ballo, l'eco e' una scelta fra le altre (prima era sempre lei: 86 passaggi su 120).
        if (rhythmic && !danceA && Pick(fromPath, nextPath, new() { ("echo", 1), ("fade", 1.8) }) == "fade") return null;
        if (!rhythmic && Pick(fromPath, nextPath, new() { ("wash", s.AutomixIntensity == "dj" ? 1.4 : 1), ("fade", 1.3) }) == "fade") return null;
        if (rhythmic)
        {
            double beat = a.Tail.Period, bar = 4 * beat;
            // Dove tagliare: in DJ edit fra gli ultimi 40 s; con i brani interi solo nella coda (A si sente quasi tutto).
            double lo = dj ? Math.Max(position + 3, da - 40) : Math.Max(position + 3, Math.Min(a.OutroStart > 0 ? a.OutroStart : da - 8, da - 2) - 0.5);
            double hi = da - 0.3;
            var cue = exits.Where(c => c.Time >= lo && c.Time <= hi)
                           .OrderByDescending(c => c.Strength + (c.Origin == "outro" ? 0.15 : 0) + (dj ? 0 : (c.Time - lo) / Math.Max(1, hi - lo) * 0.2))
                           .FirstOrDefault();
            double tc = cue?.Time ?? Math.Max(lo, da - bar);
            double grid = a.TailDownbeatConfidence >= 1.05 ? a.TailDownbeat : a.TailBeat, step = a.TailDownbeatConfidence >= 1.05 ? bar : beat;
            double snapped = grid + Math.Round((tc - grid) / step) * step;
            if (Math.Abs(snapped - tc) <= step / 2 && snapped <= hi && snapped >= position + 2) tc = snapped;
            if (tc <= position + 1.5) return null;
            // B entra sul primo battito (o sul primo suono): mai nel mezzo del brano.
            double tb = s0Base;
            if (b.Head.Reliable && b.HeadDownbeatConfidence >= 1.05)
            {
                double barB = 4 * b.Head.Period;
                double first = b.HeadDownbeat + Math.Ceiling((s0Base - 0.05 - b.HeadDownbeat) / barB) * barB;
                if (first >= s0Base - 0.05 && first <= s0Base + barB + 0.05) tb = first;
            }
            // Oltre all'eco: roll e drop, filtro con riser, brake, spinback (scelti secondo il caso, vedi DjTechniques).
            if (PlanCutTechnique(a, b, tc, tb, beat, bar, db, position, s0Base, s, dj, fromPath, nextPath, cue?.Origin ?? "end") is { } technique)
                return technique;
            double echo = (beat >= 0.45 ? 0.75 : 1.5) * beat;
            double pre = Math.Max(0.12, Math.Min(beat, tc - position - 1));
            double tail = Math.Clamp(echo * 6.5, 1.6, 3.4);
            double start = tc - pre, length = pre + tail;
            double s0 = Math.Max(0, tb - pre), inAt = tb - s0;
            if (start <= position + 0.5 || s0 + length >= db) return null;
            var curves = TransitionCurves.EchoOut(length, pre, inAt, beat);
            double gain = LevelMatch(a, b, tc, tb + 1, s);
            LastCuedDecision = $"echo out: A cut at {tc:0.00}s ({cue?.Origin ?? "end"}, beat {beat:0.000}s, echo {echo:0.000}s), B from {tb:0.00}s, length {length:0.00}s";
            Dbg.Log($"[AUTOMIX] cued '{Path.GetFileName(fromPath)}' -> '{Path.GetFileName(nextPath)}': {LastCuedDecision}", Dbg.LogLevel.Info);
            return new MixPlan(TransitionStyle.Curves, start, length, length, double.NaN, s0, 60 / beat, tc, 1, double.NaN,
                gain, "echo out", 0, Curves: curves);
        }
        else
        {
            // Wash out: A cala nel primo 60% del passaggio (finisce dove finirebbe), B sale da sotto.
            double target = s.AutomixIntensity == "gentle" ? 7 : s.AutomixIntensity == "dj" ? 12 : 9;
            double length = Math.Min(target, Math.Min(0.25 * da, 0.25 * db));
            if (length < 4) return null;
            double start = da - length * 0.62;
            if (!dj && a.OutroStart > 0 && a.OutroStart < da - 1) start = Math.Max(start, Math.Min(a.OutroStart, da - length * 0.3));
            if (start <= position + 0.5) start = position + 0.6;
            if (start + length * 0.3 >= da) return null;
            double s0 = s0Base;
            if (s0 + length >= db) return null;
            var curves = TransitionCurves.WashOut();
            double gain = LevelMatch(a, b, start + length * 0.3, s0 + length, s);
            LastCuedDecision = $"wash out: A {start:0.00}-{start + length * 0.62:0.00}s into reverb, B from {s0:0.00}s, length {length:0.00}s";
            Dbg.Log($"[AUTOMIX] cued '{Path.GetFileName(fromPath)}' -> '{Path.GetFileName(nextPath)}': {LastCuedDecision}", Dbg.LogLevel.Info);
            return new MixPlan(TransitionStyle.Curves, start, length, length, double.NaN, s0, 0, start + length / 2, 1, double.NaN,
                gain, "wash out", 0, Curves: curves);
        }
    }

    /// <summary>Cue d'uscita di A: inizio della coda, frasi e confini di sezione degli ultimi 60 s.</summary>
    internal static List<MixCue> ExitCues(TrackMixProfile a, double endAt, double position)
    {
        var cues = new List<MixCue>();
        double from = Math.Max(position + 2, a.TailStart), to = endAt - 1;
        void Add(double t, double strength, string origin)
        {
            if (!double.IsFinite(t) || t < from || t > to || strength <= 0) return;
            if (cues.Any(c => Math.Abs(c.Time - t) < 0.25)) return;
            cues.Add(new MixCue(t, Math.Clamp(strength, 0, 1), origin));
        }
        if (a.OutroStart > 0 && a.OutroStart < endAt && endAt - a.OutroStart < 60) Add(a.OutroStart, 0.9, "outro");
        if (a.TailPhrases is { } p && a.TailPhraseStrength is { } ps)
            for (int i = 0; i < p.Length && i < ps.Length; i++) Add(p[i], ps[i], "phrase");
        if (a.TailMusic is { } map)
            foreach (var bnd in map.Boundaries) Add(bnd.Time, bnd.Strength * (bnd.Drop > 0 ? 1 : 0.8), "section");
        return cues.OrderBy(c => c.Time).ToList();
    }

    /// <summary>Cue d'ingresso di B: primo suono, primo downbeat, frasi, ingressi d'energia, griglie d'ingresso, confini.</summary>
    internal static List<MixCue> EntryCues(TrackMixProfile b, double s0Base)
    {
        var cues = new List<MixCue>();
        double to = Math.Min(60, b.Duration * 0.5);
        void Add(double t, double strength, string origin)
        {
            if (!double.IsFinite(t) || t < s0Base - 0.05 || t > to || strength <= 0) return;
            if (cues.Any(c => Math.Abs(c.Time - t) < 0.25)) return;
            cues.Add(new MixCue(Math.Max(t, 0), Math.Clamp(strength, 0, 1), origin));
        }
        if (b.Head.Reliable && b.HeadDownbeatConfidence > 0)
        {
            double bar = 4 * b.Head.Period;
            double first = b.HeadDownbeat + Math.Ceiling((s0Base - 0.05 - b.HeadDownbeat) / bar) * bar;
            Add(first, Math.Clamp(0.5 + (b.HeadDownbeatConfidence - 1) * 2, 0.3, 0.9), "downbeat");
        }
        Add(s0Base, 0.6, "first sound");
        if (b.HeadPhrases is { } p && b.HeadPhraseStrength is { } ps)
            for (int i = 0; i < p.Length && i < ps.Length; i++) Add(p[i], ps[i], "phrase");
        if (b.HeadEntries is { } en) foreach (double t in en) Add(t, 0.7, "energy");
        if (b.EntryGrids is { } grids) foreach (var g in grids) Add(g.Cue, Math.Clamp(g.DownbeatConfidence - 0.4, 0.3, 0.9), "grid");
        if (b.HeadMusic is { } map) foreach (var bnd in map.Boundaries) Add(bnd.Time, bnd.Strength * (bnd.Rise > 0 ? 1 : 0.8), "section");
        return cues.OrderBy(c => c.Time).ToList();
    }

    private static List<CuedCandidate> BarCandidates(TrackMixProfile a, TrackMixProfile b, double endAt, double position, double s0Base,
        double da, double db, bool dj, AutomixPreferences s, MixCue? bestExit, List<MixCue> exits, MixCue? bestEntry, List<MixCue> entries,
        AutomixRuleSet rules)
    {
        var result = new List<CuedCandidate>();
        // [NOI] griglie affidabili e downbeat riconoscibili al posto della soglia 0,40 e della varianza delle battute (#8, #11).
        if (!a.Tail.Reliable || !b.Head.Reliable || Math.Min(a.TailConfidence, b.HeadConfidence) < 1.8) return result;
        if (a.TailDownbeatConfidence < 1.05 || b.HeadDownbeatConfidence < 1.05) return result;
        // [COD #13] stesse battute (octave correction spenta), tolleranza 10%; [NOI] non oltre il limite di time-stretch.
        double limit = !s.AutomixTempoSync ? 0.004 : s.AutomixIntensity == "gentle" ? 0.03 : s.AutomixIntensity == "dj" ? 0.06 : 0.04;
        double tolerance = Math.Min(rules.MaxSpeedDifference, limit);
        double ratio = a.TailBpm / b.HeadBpm;
        if (Math.Abs(ratio - 1) > tolerance) return result;
        if (Math.Abs(ratio - 1) < 0.002) ratio = 1;
        double barA = 4 * a.Tail.Period, barB = 4 * b.Head.Period;
        int keyA = a.KeyConfidence >= 0.55 ? a.Key : -1, keyB = b.KeyConfidence >= 0.55 ? b.Key : -1;
        int tier = AutomixRules.CamelotTier(keyA, keyB);
        bool prefersLong = Math.Min(a.TailConfidence, b.HeadConfidence) >= 2.4;
        double aFrom = Math.Max(position + 1.5, a.TailStart);
        double bTo = Math.Min(60, db * 0.5);
        double firstA = a.TailDownbeat + Math.Ceiling((aFrom - a.TailDownbeat) / barA) * barA;
        double firstB = b.HeadDownbeat + Math.Ceiling((s0Base - 0.05 - b.HeadDownbeat) / barB) * barB;
        foreach (int bars in new[] { 32, 16, 8, 4, 2 })
        {
            if (bars > rules.MaxBars) continue;                       // [COD #8]
            double lenA = bars * barA, lenB = bars * barB;
            if (lenA > Math.Min(rules.MaxSegmentSeconds, rules.MaxSegmentFraction * da) || lenB > Math.Min(rules.MaxSegmentSeconds, rules.MaxSegmentFraction * db)) continue;
            if (AutomixRules.SpeedDifference(lenA * ratio, lenB) > tolerance) continue;
            var length = AutomixRules.LengthOfBars(bars);
            double key = AutomixRules.KeyScore(tier, length, rules), genre = AutomixRules.GenreScore(prefersLong, length, rules);
            for (double a0 = firstA; a0 + lenA <= endAt + 0.05; a0 += barA)
            {
                double a1 = a0 + lenA;
                if (!dj && a1 < endAt - 2) continue;                   // [NOI] brani interi: A finisce alla fine
                double cueA = AutomixRules.CueScore(a0, a1, bestExit, exits, rules);
                for (double b0 = firstB; b0 + lenB <= bTo; b0 += barB)
                {
                    if (!dj && b0 > s0Base + barB + 0.05) break;       // [NOI] brani interi: niente intro saltata
                    double b1 = b0 + lenB;
                    double pres = AutomixRules.Preservation(da, db, a0, a1, b0, b1, rules);
                    if (pres < rules.BaseScoreThreshold) continue;    // B_eff = 0 => S = 0, scartato
                    double cue = (cueA + AutomixRules.CueScore(b0, b1, bestEntry, entries, rules)) / 2;
                    var va = new double[rules.VocalBins]; var vb = new double[rules.VocalBins];
                    for (int i = 0; i < rules.VocalBins; i++)
                    {
                        double u = (i + 0.5) / rules.VocalBins;
                        double x = a.VoiceAt(a0 + u * lenA), y = b.VoiceAt(b0 + u * lenB);
                        va[i] = double.IsFinite(x) ? x : 0; vb[i] = double.IsFinite(y) ? y : 0;
                    }
                    double vocal = AutomixRules.VocalScore(va, vb, out int both, rules);
                    // [NOI, 2026-10-24] veto voci: due voci insieme nel quarto centrale di un passaggio lungo. Con le voci
                    // su gran parte del passaggio si usa lo scambio dei medi (blend): la voce di A esce mentre entra
                    // quella di B, e si sovrappongono solo al centro. Il veto sull'intero passaggio scartava ogni
                    // passaggio lungo fra due brani cantati e restavano 2 battute (4 s, un crossfade).
                    if (lenA >= rules.VocalVetoMinSeconds)
                    {
                        int lo = rules.VocalBins * 3 / 8, hi = rules.VocalBins * 5 / 8, center = 0;
                        for (int i = lo; i < hi; i++) if (va[i] >= rules.VocalThreshold && vb[i] >= rules.VocalThreshold) center++;
                        if (center > rules.VocalVetoFraction * (hi - lo)) continue;
                    }
                    // [NOI, 2026-10-24] In DJ edit B non entra nel pieno del brano: oltre la prima battuta utile, il punto
                    // d'ingresso deve essere piu' basso del corpo del brano (intro, strofa). Entrando nel ritornello a 16 s
                    // "partiva a palla".
                    if (dj && b0 > firstB + barB * 0.5)
                    {
                        double entryLevel = MeanLevel(b, b0, b0 + Math.Max(barB, lenB * 0.5));
                        if (double.IsFinite(entryLevel) && double.IsFinite(b.BodyDb) && entryLevel > b.BodyDb - 2) continue;
                    }
                    double score = AutomixRules.Combine(pres, cue, vocal, key, genre, rules);
                    if (score <= 0) continue;
                    result.Add(new CuedCandidate(bars, a0, a1, b0, b1, ratio, pres, cue, vocal, key, genre, score, both));
                }
            }
        }
        return result;
    }

    /// <summary>[NOI] Preset dal contenuto; con <see cref="CuedPresetMode"/> = "spotify" la rotazione SHA-1 riprodotta (#16, #17).</summary>
    private static int ChoosePreset(TrackMixProfile a, TrackMixProfile b, CuedCandidate c, string fromPath, string nextPath, AutomixRuleSet rules)
    {
        if (CuedPresetMode == "spotify")
        {
            int id = AutomixRules.SpotifyPreset(AutomixRules.PresetSeed(fromPath, nextPath, c.Bars, c.A0, c.B0), c.Bars, true);
            return Array.IndexOf(TransitionCurves.KnownIds, id) >= 0 ? id : 1; // id senza definizione locale => Fade
        }
        if (c.Bars <= 2) return 1;
        // [NOI] Voci su oltre un quarto del passaggio: scambio dei medi a meta' (blend), le due voci non si coprono.
        if (c.VocalBins > rules.VocalBins / 4) return 2;
        double Mean(MusicalMap? map, double from, double to, Func<MusicalFrame, double> f)
        {
            if (map == null) return double.NaN;
            var values = new List<double>();
            for (double t = from; t < to; t += map.Step) if (map.At(t) is { } frame) values.Add(f(frame));
            return values.Count > 0 ? values.Average() : double.NaN;
        }
        // Voce in A nella seconda meta' del passaggio: passa-basso in uscita la allontana.
        double voiceLate = 0; int n = 0;
        for (double t = c.A0 + (c.A1 - c.A0) / 2; t < c.A1; t += 0.5) { double v = a.VoiceAt(t); if (double.IsFinite(v)) { voiceLate += v; n++; } }
        if (n > 0 && voiceLate / n >= rules.VocalThreshold) return 17;
        // Tonalita' compatibili e passaggio lungo: dissolvenza a tre bande.
        int keyA = a.KeyConfidence >= 0.55 ? a.Key : -1, keyB = b.KeyConfidence >= 0.55 ? b.Key : -1;
        if (keyA >= 0 && keyB >= 0 && AutomixRules.CamelotTier(keyA, keyB) <= 1 && c.Bars >= 8) return 2;
        // Ingresso di B piu' percussivo che tonale: passa-alto su A, passa-basso che si apre su B.
        double perc = Mean(b.HeadMusic, c.B0, c.B1, f => f.Percussion), tonal = Mean(b.HeadMusic, c.B0, c.B1, f => f.Tonal);
        if (double.IsFinite(perc) && double.IsFinite(tonal) && perc > tonal) return 3;
        return 1;
    }

    private const int CrossfadeId = -2;

    /// <summary>
    /// [NOI, 2026-11-22] Preset del ripiego (tempi non agganciati). Prima era sempre il filter fade (o il passa-basso
    /// con la voce): su due brani senza battito un filtro che sale non serve, e Delicata deve restare una dissolvenza.
    /// Ora: se uno dei due non ha un battito (A alla fine, B all'inizio) dissolvenza pura; con due ritmi che non si
    /// agganciano Delicata usa il filter fade (i due ritmi non si scontrano a piena banda), Bilanciata e DJ scelgono
    /// fra filter fade, onda (passa-basso) e dissolvenza con lo scambio dei bassi.
    /// </summary>
    private static int ChooseFallbackPreset(TrackMixProfile a, TrackMixProfile b, double startA, double length, double startB, AutomixRuleSet rules,
        AutomixPreferences s, string fromPath, string nextPath)
    {
        if (length < 4) return 1;
        bool beatA = a.Tail.Reliable && a.TailConfidence >= 1.8, beatB = b.Head.Reliable && b.HeadConfidence >= 1.8;
        if (!beatA || !beatB) { Pick(fromPath, nextPath, new() { ("crossfade", 1) }); return CrossfadeId; }
        // Delicata: dissolvenza con lo scambio dei bassi a meta' (i due ritmi non si sommano), senza filtri.
        if (s.AutomixIntensity == "gentle") return 1;
        string pick = Pick(fromPath, nextPath, new() { ("filter-fade", 2), ("wave", 1), ("fade", 1.2) });
        return pick switch { "wave" => 17, "fade" => 1, _ => TransitionCurves.FilterFadeId };
    }

    /// <summary>
    /// [NOI, 2026-11-22] "Solo dissolvenza": A sfuma dove sfuma da se' (coda naturale fra 3 e 14 s) o negli ultimi 6 s
    /// udibili; B parte dal primo suono; curva a potenza costante, livello pareggiato, niente tempo ne' filtri.
    /// </summary>
    private static MixPlan? PlanCrossfade(TrackMixProfile a, TrackMixProfile b, double endAt, double position, double s0Base,
        AutomixPreferences s, string fromPath, string nextPath)
    {
        double da = endAt, db = b.Duration;
        double natural = a.OutroStart > 0 ? da - a.OutroStart : 0;
        double length = natural is >= 3 and <= 14 ? natural : 6;
        length = Math.Clamp(length, 1, Math.Max(1, Math.Min(14, Math.Min(0.25 * da, 0.25 * db))));
        double startA = da - length;
        if (startA <= position + 0.5) { startA = position + 0.6; length = da - startA; }
        if (length < 1 || s0Base + length >= db) return null;
        var curves = TransitionCurves.Crossfade();
        LastCuedDecision = $"crossfade: A {startA:0.00}-{da:0.00}s ({(natural is >= 3 and <= 14 ? "natural fade" : "last seconds")}), B from {s0Base:0.00}s, length {length:0.00}s";
        Dbg.Log($"[AUTOMIX] cued '{Path.GetFileName(fromPath)}' -> '{Path.GetFileName(nextPath)}': {LastCuedDecision}", Dbg.LogLevel.Info);
        return new MixPlan(TransitionStyle.Curves, startA, length, length, double.NaN, s0Base, 0, startA + length / 2, 1, double.NaN,
            LevelMatch(a, b, startA, s0Base + length, s), "crossfade", 0, Curves: curves);
    }

    /// <summary>
    /// [NOI] Vuoto di livello previsto (dB sotto la retta fra il livello prima e dopo), dai livelli misurati al secondo e dai fader.
    /// Spotify non lo controlla: con una coda che si spegne da sola e B ancora basso, il passaggio resta quasi muto.
    /// </summary>
    internal static double PredictedHoleDb(TrackMixProfile a, TrackMixProfile b, double a0, double lenA, double b0, double lenB, TransitionCurves curves)
    {
        double before = MeanLevel(a, a0 - 4, a0), after = MeanLevel(b, b0 + lenB, b0 + lenB + 4);
        if (double.IsNaN(before) || double.IsNaN(after)) return 0;
        double worst = 0;
        // Livelli al secondo interpolati: a passo intero un finale che muore in mezzo secondo sfuggirebbe.
        static double Level(TrackMixProfile t, double time)
        {
            double x = time - 0.5, f = Math.Floor(x), l0 = t.LevelAt(f + 0.5), l1 = t.LevelAt(f + 1.5);
            if (!double.IsFinite(l0)) return l1; if (!double.IsFinite(l1)) return l0;
            return l0 + (l1 - l0) * (x - f);
        }
        for (double p = 0.025; p < 0.976; p += 0.025)
        {
            double la = Level(a, a0 + p * lenA), lb = Level(b, b0 + p * lenB);
            if (!double.IsFinite(la) || !double.IsFinite(lb)) continue;
            // Fader per l'attenuazione media delle tre bande (stima grossolana: bande di peso uguale).
            static double Eq(DeckCurves d, double p) =>
                (Math.Pow(10, d.LowDb.ValueAt(p) / 10) + Math.Pow(10, d.MidDb.ValueAt(p) / 10) + Math.Pow(10, d.HighDb.ValueAt(p) / 10)) / 3;
            double va = curves.OutVolumeAt(p) * Math.Sqrt(Eq(curves.Out, p)), vb = curves.InVolumeAt(p) * Math.Sqrt(Eq(curves.In, p));
            double mixed = 10 * Math.Log10(Math.Max(1e-12, Math.Pow(10, la / 10) * va * va + Math.Pow(10, lb / 10) * vb * vb));
            worst = Math.Max(worst, before + (after - before) * p - mixed);
        }
        return worst;
    }

    private static TransitionCurves AvoidHole(TrackMixProfile a, TrackMixProfile b, double a0, double lenA, double b0, double lenB, TransitionCurves chosen, int bars)
    {
        double hole = PredictedHoleDb(a, b, a0, lenA, b0, lenB, chosen);
        if (hole <= 6) return chosen;
        var best = chosen; double bestHole = hole;
        foreach (int id in new[] { 0, 2 })
        {
            var alt = id == 2 && bars > 4
                ? TransitionCurves.Compose(TransitionCurves.VolumeFade, TransitionCurves.EqThreeBand, TransitionCurves.FilterNone, bars)
                : TransitionCurves.Preset(id, bars);
            double h = PredictedHoleDb(a, b, a0, lenA, b0, lenB, alt);
            if (h < bestHole - 1) { best = alt; bestHole = h; }
        }
        if (!ReferenceEquals(best, chosen)) Dbg.Log($"[AUTOMIX] cued: {chosen.Name} would leave a {hole:0.0} dB hole, using {best.Name} ({bestHole:0.0} dB)", Dbg.LogLevel.Info);
        return best;
    }

    /// <summary>[NOI] Pareggio di livello: meta' della differenza fra la fine di A e l'arrivo di B, entro -4..+3 dB.</summary>
    private static double LevelMatch(TrackMixProfile a, TrackMixProfile b, double exitA, double arrivalB, AutomixPreferences s)
    {
        double la = MeanLevel(a, exitA - 8, exitA), lb = MeanLevel(b, arrivalB, arrivalB + 8);
        if (double.IsNaN(la) || double.IsNaN(lb)) return 0;
        double diff = s.ReplayGain != "off" ? (lb - b.BodyDb) - (la - a.BodyDb) : lb - la;
        double gain = Math.Clamp(-diff * 0.5, -4, 1.5); // [NOI] mai piu' di +1,5 dB sul brano che entra
        return Math.Abs(gain) < 0.5 ? 0 : gain;
    }
}
