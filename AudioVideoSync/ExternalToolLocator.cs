using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Cinecore.AudioVideoSync;

internal static class ExternalToolLocator
{
    public static string FindFfmpeg(string? explicitPath)
        => Find("ffmpeg.exe", explicitPath, Path.Combine("third-parties", "ffmpeg", "win-x64", "ffmpeg.exe"));

    public static string FindMkvMerge(string? explicitPath)
        => Find("mkvmerge.exe", explicitPath, Path.Combine("MKVToolNix", "mkvmerge.exe"));

    private static string Find(string executableName, string? explicitPath, string repoRelativeCandidate)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full))
                throw new FileNotFoundException($"Eseguibile non trovato: {full}", full);
            return full;
        }

        foreach (string root in EnumerateAncestorDirectories(AppContext.BaseDirectory)
                     .Concat(EnumerateAncestorDirectories(Environment.CurrentDirectory))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(root, repoRelativeCandidate);
            if (File.Exists(candidate))
                return candidate;
        }

        // ProcessStartInfo risolverà l'eseguibile attraverso PATH. La presenza
        // effettiva viene verificata dal primo avvio e produce un errore leggibile.
        return executableName;
    }

    private static IEnumerable<string> EnumerateAncestorDirectories(string start)
    {
        DirectoryInfo? current = new(Path.GetFullPath(start));
        for (int i = 0; current != null && i < 8; i++, current = current.Parent)
            yield return current.FullName;
    }
}
