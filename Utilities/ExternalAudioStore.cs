#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CinecorePlayer2025.Utilities;

internal sealed record ExternalAudioBinding(string? AudioPath, int DelayMs = 0, int ReferenceOrdinal = 0, int? TargetOrdinal = null);
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
