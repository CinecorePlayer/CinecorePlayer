#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace CinecorePlayer2025.Audio;

/// <summary>
/// Costanti del pianificatore. I valori marcati "recuperato" vengono dai default compilati nel ricevitore
/// Spotify Soloist 1.3.10.12 (artifacts/spotify-soloist-lab, config-defaults.json); gli altri sono scelte nostre.
/// Tutto e' configurabile: un valore recuperato non e' una prova che Spotify lo usi in ogni playlist.
/// </summary>
internal sealed record AutomixRuleSet
{
    public static AutomixRuleSet Default { get; } = new();

    // Limiti del segmento candidato (recuperati: 35 s, 200 permille, 16 battute, 10%).
    public double MaxSegmentSeconds { get; init; } = 35;
    public double MaxSegmentFraction { get; init; } = 0.20;
    public int MaxBars { get; init; } = 16;
    public double MaxSpeedDifference { get; init; } = 0.10;
    // Recuperato come "40": la scala non e' certa. Lo leggiamo come 0,40 sul fattore di conservazione (0-1).
    public double BaseScoreThreshold { get; init; } = 0.40;
    // Dissolvenza di ripiego e durata senza aggancio di tempo (recuperati: 5000 ms entrambi).
    public double FallbackSeconds { get; init; } = 5;
    public double BeatmatchOptOutSeconds { get; init; } = 5;

    // Qualita' dei cue (recuperati: 800/800/1000 permille, margine 500 ms, cue non migliore 950 permille).
    // Ipotesi nostra: start/end/middle = dove cade il cue nel passaggio (inizio, fine, centro).
    public double CueStartScore { get; init; } = 0.8;
    public double CueEndScore { get; init; } = 0.8;
    public double CueMiddleScore { get; init; } = 1.0;
    public double CueMarginSeconds { get; init; } = 0.5;
    public double NonBestCueFactor { get; init; } = 0.95;
    // Spotify lo tiene spento (i suoi cue sono umani o di un modello addestrato). I nostri vengono da un'analisi
    // piu' rumorosa: la loro forza misurata conta.
    public bool UseCueConfidence { get; init; } = true;

    // Conservazione del brano (recuperati: ginocchio 150 permille / 90000 ms).
    public double KneeFraction { get; init; } = 0.15;
    public double KneeSeconds { get; init; } = 90;

    // Voci (recuperati: esponente 1000 permille, soglia 200 permille sul modello Spotify). Il nostro rivelatore
    // ha un'altra scala: 0,5 e' il suo "c'e' una voce".
    public int VocalBins { get; init; } = 32;
    public double VocalThreshold { get; init; } = 0.5;
    public double VocalExponent { get; init; } = 1;
    // Scelta nostra (Spotify non lo fa): due voci insieme per oltre meta' di un passaggio lungo = scartato.
    public double VocalVetoFraction { get; init; } = 0.5;
    public double VocalVetoMinSeconds { get; init; } = 6;

    // Pesi (recuperati: 1000 permille ciascuno).
    public double CueWeight { get; init; } = 1;
    public double VocalWeight { get; init; } = 1;
    public double KeyWeight { get; init; } = 1;
    public double GenreWeight { get; init; } = 1;

    // Tonalita' (recuperati: 3 livelli compatibili; 900/800/600 permille per passaggi corti/medi/lunghi).
    public int CompatibleKeyTiers { get; init; } = 3;
    public double IncompatibleKeyShort { get; init; } = 0.9;
    public double IncompatibleKeyMedium { get; init; } = 0.8;
    public double IncompatibleKeyLong { get; init; } = 0.6;

    // "Genere" (recuperati: 1000/800/600 permille). Non abbiamo le classi di Spotify: lo sostituiamo con la
    // compatibilita' ritmica (vedi AutomixRules.GenreScore).
    public double GenreIdeal { get; init; } = 1.0;
    public double GenreAcceptable { get; init; } = 0.8;
    public double GenrePoor { get; init; } = 0.6;

    // Candidati tenuti per gruppo di lunghezza dopo l'ordinamento (recuperato: 5).
    public int KeepPerGroup { get; init; } = 5;
}

/// <summary>Lunghezza del passaggio come la usano le regole: corta (fino a 4 battute), media (8), lunga (16).</summary>
internal enum MixLength { Short = 0, Medium = 1, Long = 2 }

/// <summary>Dove cade il cue dentro il passaggio.</summary>
internal enum CuePlacement { Start, Middle, End }

/// <summary>
/// Le regole numeriche verificate nel laboratorio Soloist (emulazione del codice originale su metadati sintetici),
/// riscritte in modo indipendente. Le formule sono documentate in
/// artifacts/spotify-soloist-lab/consegna-claude/DOSSIER-PER-CLAUDE.md; dove scegliamo diversamente lo diciamo.
/// </summary>
internal static class AutomixRules
{
    public static double Clamp01(double x) => Math.Min(1, Math.Max(0, x));

    /// <summary>
    /// Conservazione del contenuto: favorisce la fine di A e l'inizio di B. 0 se un segmento supera 35 s o il
    /// 20% del suo brano, o se una durata non e' positiva. Tempi in secondi, nella linea del proprio brano.
    /// </summary>
    public static double Preservation(double durationA, double durationB, double startA, double endA, double startB, double endB, AutomixRuleSet? rules = null)
    {
        rules ??= AutomixRuleSet.Default;
        if (!(durationA > 0) || !(durationB > 0)) return 0;
        if (endA - startA > Math.Min(rules.MaxSegmentSeconds, rules.MaxSegmentFraction * durationA)) return 0;
        if (endB - startB > Math.Min(rules.MaxSegmentSeconds, rules.MaxSegmentFraction * durationB)) return 0;
        double kA = Math.Min(rules.KneeFraction, rules.KneeSeconds / durationA);
        double kB = Math.Min(rules.KneeFraction, rules.KneeSeconds / durationB);
        double uA = 0.5 + 15 / durationA;
        double uB = Math.Max(kB + 0.01, 0.5 - 15 / durationB);
        double denA = 1 - kA - uA, denB = kB - uB;
        // Brani cortissimi: il ginocchio supera la meta' e la formula degenera. Lo trattiamo come "tutto ammesso".
        double fa = denA > 1e-9 ? Clamp01((startA / durationA - uA) / denA) : 1;
        double fb = denB < -1e-9 ? Clamp01((endB / durationB - uB) / denB) : 1;
        return fa * fb;
    }

    /// <summary>Penalita' per voci contemporanee: N bin dove entrambe le medie raggiungono la soglia, V = (1 - N/32)^p.</summary>
    public static double VocalScore(IReadOnlyList<double> averagesA, IReadOnlyList<double> averagesB, out int both, AutomixRuleSet? rules = null)
    {
        rules ??= AutomixRuleSet.Default;
        int n = Math.Min(averagesA.Count, averagesB.Count);
        both = 0;
        for (int i = 0; i < n; i++)
            if (averagesA[i] >= rules.VocalThreshold && averagesB[i] >= rules.VocalThreshold) both++;
        return n == 0 ? 1 : Math.Pow(1 - both / (double)n, rules.VocalExponent);
    }

    /// <summary>
    /// Livello di compatibilita' Camelot: 0 stesso numero o adiacente con la stessa lettera, 1 relativa maggiore/minore,
    /// 2 lettera diversa e numero adiacente, 3 due passi con la stessa lettera, 4 il resto. Tonalita' sconosciuta = 0
    /// (neutro, non "compatibile accertato"). Usa la distanza circolare normale sulla ruota: il codice originale
    /// confronta (x+1)%12 con numeri 1..12 e sbaglia 12 coppie attorno al 12 (vedi CamelotTierLiteral).
    /// </summary>
    public static int CamelotTier(int numberA, char letterA, int numberB, char letterB)
    {
        if (numberA < 1 || numberA > 12 || numberB < 1 || numberB > 12) return 0;
        int step = Math.Min((numberA - numberB + 12) % 12, (numberB - numberA + 12) % 12);
        bool same = letterA == letterB;
        if (same && step <= 1) return 0;
        if (!same && step == 0) return 1;
        if (!same && step == 1) return 2;
        if (same && step == 2) return 3;
        return 4;
    }

    /// <summary>Livello Camelot con le chiavi di KeyDetector (0-23, -1 sconosciuta).</summary>
    public static int CamelotTier(int keyA, int keyB)
    {
        if (keyA < 0 || keyB < 0) return 0;
        var (na, la) = KeyDetector.Camelot(keyA); var (nb, lb) = KeyDetector.Camelot(keyB);
        return CamelotTier(na, la, nb, lb);
    }

    /// <summary>Il classificatore originale alla lettera, compreso il difetto attorno al 12. Solo per i test.</summary>
    public static int CamelotTierLiteral(int x, char letterA, int y, char letterB)
    {
        if (x < 1 || x > 12 || y < 1 || y > 12) return 0;
        bool adjacent = (x + 1) % 12 == y || (y + 1) % 12 == x;
        bool second = (x + 2) % 12 == y || (y + 2) % 12 == x;
        bool same = letterA == letterB;
        if (same && (x == y || adjacent)) return 0;
        if (!same && x == y) return 1;
        if (!same && adjacent) return 2;
        if (same && second) return 3;
        return 4;
    }

    public static double KeyScore(int tier, MixLength length, AutomixRuleSet? rules = null)
    {
        rules ??= AutomixRuleSet.Default;
        if (tier < rules.CompatibleKeyTiers) return 1;
        return length switch { MixLength.Short => rules.IncompatibleKeyShort, MixLength.Medium => rules.IncompatibleKeyMedium, _ => rules.IncompatibleKeyLong };
    }

    /// <summary>
    /// Spotify assegna 1/0,8/0,6 per classi di genere che preferiscono passaggi corti, o 0,6/0,8/1 per quelle che li
    /// preferiscono lunghi. Noi non abbiamo le sue classi: un passaggio a tempo fra due griglie nette preferisce
    /// la lunghezza, una dissolvenza senza aggancio la brevita' (scelta nostra, documentata).
    /// </summary>
    public static double GenreScore(bool prefersLong, MixLength length, AutomixRuleSet? rules = null)
    {
        rules ??= AutomixRuleSet.Default;
        return (prefersLong ? 2 - (int)length : (int)length) switch { 0 => rules.GenreIdeal, 1 => rules.GenreAcceptable, _ => rules.GenrePoor };
    }

    /// <summary>S = B_eff * (wC*C + wV*V + wK*K + wG*G), B_eff = 0 sotto la soglia. Non e' una media: arriva a 4.</summary>
    public static double Combine(double preservation, double cue, double vocal, double key, double genre, AutomixRuleSet? rules = null)
    {
        rules ??= AutomixRuleSet.Default;
        double b = preservation < rules.BaseScoreThreshold ? 0 : preservation;
        return b * (rules.CueWeight * cue + rules.VocalWeight * vocal + rules.KeyWeight * key + rules.GenreWeight * genre);
    }

    /// <summary>|durata segmento B / durata segmento A - 1|, in float32 come l'originale.</summary>
    public static double SpeedDifference(double segmentA, double segmentB)
    {
        if (!(segmentA > 0)) return double.PositiveInfinity;
        return Math.Abs((float)segmentB / (float)segmentA - 1f);
    }

    public static double PlacementScore(CuePlacement placement, AutomixRuleSet? rules = null)
    {
        rules ??= AutomixRuleSet.Default;
        return placement switch { CuePlacement.Middle => rules.CueMiddleScore, CuePlacement.Start => rules.CueStartScore, _ => rules.CueEndScore };
    }

    public static MixLength LengthOfBars(int bars) => bars <= 4 ? MixLength.Short : bars <= 8 ? MixLength.Medium : MixLength.Long;
    public static MixLength LengthOfSeconds(double seconds) => seconds <= 6 ? MixLength.Short : seconds <= 12 ? MixLength.Medium : MixLength.Long;

    /// <summary>
    /// Punteggio dei cue di un segmento [start, end] (Soloist 0x16f4220, VERIFICA.md #12). Il cue migliore vale 1/0,8/0,8 se
    /// entro il margine dal centro/fine/inizio (in quest'ordine); poi gli altri cue, nell'ordine dato, con il fattore 0,95;
    /// vince il primo che cade in una finestra. Con <see cref="AutomixRuleSet.UseCueConfidence"/> il fattore e' la forza del cue.
    /// </summary>
    public static double CueScore(double start, double end, MixCue? best, IReadOnlyList<MixCue> others, AutomixRuleSet? rules = null)
    {
        rules ??= AutomixRuleSet.Default;
        double mid = start + (end - start) * 0.5, m = rules.CueMarginSeconds;
        double Place(double t, double factor)
        {
            if (Math.Abs(t - mid) < m) return factor * rules.CueMiddleScore;
            if (Math.Abs(t - end) < m) return factor * rules.CueEndScore;
            if (Math.Abs(t - start) < m) return factor * rules.CueStartScore;
            return -1;
        }
        if (best != null)
        {
            double s = Place(best.Time, rules.UseCueConfidence ? Clamp01(best.Strength) : 1);
            if (s >= 0) return s;
        }
        foreach (var cue in others)
        {
            if (best != null && cue.Time == best.Time) continue;
            double s = Place(cue.Time, rules.UseCueConfidence ? Clamp01(cue.Strength) : rules.NonBestCueFactor);
            if (s >= 0) return s;
        }
        return 0;
    }

    /// <summary>
    /// Fronti di Pareto (Soloist 0x16f8370 + 0x16f88e0, VERIFICA.md #15): fronte 0 = record non dominati; si tolgono e si
    /// ripete. x domina y se e' >= in ogni componente e > in almeno una.
    /// </summary>
    public static int[] ParetoFronts(IReadOnlyList<double[]> components)
    {
        int n = components.Count;
        var front = new int[n];
        var left = new List<int>();
        for (int i = 0; i < n; i++) { front[i] = -1; left.Add(i); }
        static bool Dominates(double[] x, double[] y)
        {
            bool strict = false;
            for (int k = 0; k < x.Length; k++) { if (x[k] < y[k]) return false; if (x[k] > y[k]) strict = true; }
            return strict;
        }
        for (int level = 0; left.Count > 0; level++)
        {
            var current = left.Where(i => !left.Any(j => j != i && Dominates(components[j], components[i]))).ToList();
            foreach (int i in current) front[i] = level;
            left.RemoveAll(current.Contains);
        }
        return front;
    }

    /// <summary>Ripiego del planner (Soloist 0x16f52f0, VERIFICA.md #19): durata e segmenti, tempi in secondi.</summary>
    internal readonly record struct FallbackPlan(double Length, bool CueBased, double StartA, double EndA, double StartB, double EndB);

    public static FallbackPlan Fallback(double durationA, double durationB, double? cueA, double? cueB, AutomixRuleSet? rules = null)
    {
        rules ??= AutomixRuleSet.Default;
        double xA = Math.Max(0, durationA - 30), xB = Math.Max(0, durationB - 30);
        double l = new[] { 0.5 * xA, 0.5 * xB, rules.MaxSegmentFraction * durationA, rules.MaxSegmentFraction * durationB, rules.MaxSegmentSeconds, rules.FallbackSeconds }.Min();
        l = Math.Max(0, l);
        double half = l / 2;
        // Soglia di A: oltre DA/2 + 15 + L/2. Soglia di B: il codice originale sottrae L/2 secondi a un valore in
        // millisecondi, quindi in pratica vale (DB - 30)/2 (riprodotto cosi').
        if (cueA is double a && cueB is double b && a > 0 && b > 0 &&
            Math.Round(a * 1000) >= Math.Round((durationA - xA * 0.5 + half) * 1000) && Math.Round(b * 1000) <= Math.Round(xB * 0.5 * 1000 - half))
            return new(l, true, Math.Max(0, a - half), Math.Min(a + half, durationA), Math.Max(0, b - half), Math.Min(b + half, durationB));
        return new(l, false, Math.Max(0, durationA - l), durationA, 0, l);
    }

    /// <summary>Seme del preset (Soloist 0x16f6140, VERIFICA.md #16): SHA-1 standard, primi 8 byte little-endian.</summary>
    public static ulong PresetSeed(string playableA, string playableB, int bars, double startA, double startB)
    {
        var data = new List<byte>();
        data.AddRange(System.Text.Encoding.UTF8.GetBytes(playableA));
        data.AddRange(System.Text.Encoding.UTF8.GetBytes(playableB));
        data.AddRange(BitConverter.GetBytes(bars));
        data.AddRange(BitConverter.GetBytes((int)((float)startA * 1000f)));
        data.AddRange(BitConverter.GetBytes((int)((float)startB * 1000f)));
        byte[] hash = System.Security.Cryptography.SHA1.HashData(data.ToArray());
        return BitConverter.ToUInt64(hash, 0);
    }

    private static readonly int[] LongPresets = { 1, 2, 3, 4, 5, 17, 18, 8, 9, 10, 19 }, ShortPresets = { 1, 10, 19 };

    /// <summary>Preset scelto dalla tabella Auto (Soloist 0x16f5ed0, VERIFICA.md #17); il ripiego usa sempre 1.</summary>
    public static int SpotifyPreset(ulong seed, int bars, bool fromPlanner) =>
        !fromPlanner ? 1 : bars < 4 ? ShortPresets[seed % 3] : LongPresets[seed % 11];

    /// <summary>Dopo l'ordinamento: i primi <c>limit</c> record di ogni gruppo consecutivo (non i "migliori cinque" globali).</summary>
    public static List<T> KeepPerConsecutiveGroup<T>(IEnumerable<T> records, Func<T, int> group, int limit)
    {
        var result = new List<T>();
        int? previous = null; int count = 0;
        foreach (var row in records)
        {
            int g = group(row);
            count = g == previous ? count + 1 : 1;
            previous = g;
            if (count <= limit) result.Add(row);
        }
        return result;
    }
}
