#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CinecorePlayer2025.Utilities;

internal sealed record ExternalAudioBinding(string? AudioPath, int DelayMs = 0, int ReferenceOrdinal = 0, int? TargetOrdinal = null,
    string Status = SyncStatus.Manual, double Confidence = 0, DateTime? CheckedUtc = null, double[]? CurveTimes = null, int[]? CurveOffsets = null)
{
    /// <summary>The delay is not one value: it follows the film (offsets from DelayMs at given positions).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCurve => CurveTimes is { Length: > 1 } && CurveOffsets != null && CurveOffsets.Length == CurveTimes.Length;

    /// <summary>Delay to apply at a position of the film (seconds).</summary>
    public int DelayAt(double position)
    {
        if (!HasCurve) return DelayMs;
        double[] times = CurveTimes!;
        int[] offsets = CurveOffsets!;
        if (position <= times[0]) return DelayMs + offsets[0];
        if (position >= times[^1]) return DelayMs + offsets[^1];
        int i = Array.BinarySearch(times, position);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, times.Length - 2);
        int a = offsets[i], b = offsets[i + 1];
        // A real change (another reel, a cut) happens at one point; a few milliseconds are a slow slide.
        if (Math.Abs(a - b) > 120) return DelayMs + (position < (times[i] + times[i + 1]) / 2 ? a : b);
        double fraction = (position - times[i]) / Math.Max(1e-6, times[i + 1] - times[i]);
        return DelayMs + (int)Math.Round(a + (b - a) * fraction);
    }
}

/// <summary>Stato della sincronizzazione audio di un file (mostrato nel pannello e nel player).</summary>
internal static class SyncStatus
{
    public const string Verified = "verified";   // ritardo applicato = proposta affidabile dell'analisi
    public const string Manual = "manual";       // impostato a mano, non verificato
    public const string Uncertain = "uncertain"; // analisi non concorde fra le scene
    public const string Speed = "speed";         // velocita' diversa (PAL/deriva): nessun ritardo fisso basta

    public static (string Text, System.Drawing.Color Color) Describe(ExternalAudioBinding? binding)
    {
        if (binding == null) return (AppLanguage.T("Sincronizzazione originale del file", "File's original synchronization"), System.Drawing.Color.FromArgb(142, 160, 180));
        string delay = binding.DelayMs == 0 ? "0 ms" : $"{binding.DelayMs:+0;-0} ms";
        if (binding.HasCurve) delay = AppLanguage.T($"variabile {binding.DelayMs + binding.CurveOffsets!.Min():+0;-0;0}…{binding.DelayMs + binding.CurveOffsets!.Max():+0;-0;0} ms", $"variable {binding.DelayMs + binding.CurveOffsets!.Min():+0;-0;0}…{binding.DelayMs + binding.CurveOffsets!.Max():+0;-0;0} ms");
        return binding.Status switch
        {
            Verified => (AppLanguage.T($"Verificata, {delay}, affidabilita' {binding.Confidence:P0}", $"Verified, {delay}, confidence {binding.Confidence:P0}"), System.Drawing.Color.FromArgb(62, 196, 120)),
            Uncertain => (AppLanguage.T($"Da verificare, {delay}", $"Needs checking, {delay}"), System.Drawing.Color.FromArgb(240, 176, 60)),
            Speed => (AppLanguage.T("Velocita' diversa: ritardo fisso insufficiente", "Different speed: a fixed delay is not enough"), System.Drawing.Color.FromArgb(236, 96, 92)),
            _ => (AppLanguage.T($"Manuale, {delay}, non verificata", $"Manual, {delay}, not verified"), System.Drawing.Color.FromArgb(96, 160, 240)),
        };
    }
}
internal static class ExternalAudioStore
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "external-audio.json");
    private static readonly Dictionary<string, ExternalAudioBinding> Entries = Load();
    private static Dictionary<string, ExternalAudioBinding> Load()
    {
        try { return new(JsonSerializer.Deserialize<Dictionary<string, ExternalAudioBinding>>(File.ReadAllText(FilePath)) ?? new(), StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }
    internal static ExternalAudioBinding? Get(string path)
    {
        lock (Entries) return Entries.TryGetValue(path, out var value) ? value : null;
    }
    internal static void Set(string path, ExternalAudioBinding? binding)
    {
        lock (Entries)
        {
            if (binding == null) Entries.Remove(path); else Entries[path] = binding;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Entries));
            File.Move(temp, FilePath, true);
        }
    }
}
