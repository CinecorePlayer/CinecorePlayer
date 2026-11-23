using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace CinecoreMac;

/// <summary>
/// Playlist di Windows Media Player (.wpl, richiesta GitHub). Il file e' XML SMIL:
/// &lt;smil&gt;&lt;head&gt;&lt;title&gt;…&lt;/title&gt;&lt;/head&gt;&lt;body&gt;&lt;seq&gt;&lt;media src="..\Musica\brano.mp3"/&gt;…
/// I percorsi sono quelli di Windows (relativi alla playlist o assoluti con lettera di unita' / \\server\cartella):
/// si provano cosi' come sono, poi relativi alla cartella della playlist togliendo via via le cartelle iniziali
/// (playlist copiata insieme alla musica su un altro disco o sul Mac), infine si cercano in libreria per nome del
/// file e cartelle che lo contengono. Le voci introvabili restano fuori e si contano.
/// Le playlist automatiche di WMP (&lt;smartPlaylist&gt;, solo regole senza brani) non hanno nulla da importare.
/// </summary>
internal static class WplPlaylist
{
    internal sealed record Result(string Title, List<string> Paths, int Missing, int Total);

    internal static bool IsPlaylistFile(string path) =>
        path.EndsWith(".wpl", StringComparison.OrdinalIgnoreCase);

    /// <summary>Legge la playlist; <paramref name="known"/> sono i percorsi della libreria (anche dei server).</summary>
    internal static Result Read(string file, IEnumerable<string> known)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            doc.Load(reader);
        string title = doc.SelectSingleNode("//*[local-name()='head']/*[local-name()='title']")?.InnerText.Trim() ?? "";
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(file);

        var sources = new List<string>();
        foreach (XmlNode media in doc.SelectNodes("//*[local-name()='media']")!)
            if (media.Attributes?["src"]?.Value is { Length: > 0 } src) sources.Add(src.Trim());

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
        var index = BuildIndex(known);
        var paths = new List<string>();
        int missing = 0;
        foreach (string src in sources)
        {
            string? resolved = Resolve(src, baseDir, index);
            if (resolved == null) { missing++; continue; }
            paths.Add(resolved);
        }
        return new Result(title, paths, missing, sources.Count);
    }

    private static Dictionary<string, List<string>> BuildIndex(IEnumerable<string> known)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in known)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal)) continue;
            string name = Path.GetFileName(path);
            if (name.Length == 0) continue;
            if (!index.TryGetValue(name, out var list)) index[name] = list = new List<string>();
            list.Add(path);
        }
        return index;
    }

    private static string[] Segments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    internal static string? Resolve(string src, string baseDir, Dictionary<string, List<string>> index)
    {
        // Indirizzi web (radio, flussi): restano come sono.
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return src;
        if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(src, UriKind.Absolute, out var uri)) src = uri.LocalPath;

        bool windowsAbsolute = src.Length > 2 && src[1] == ':' || src.StartsWith(@"\\", StringComparison.Ordinal) || src.StartsWith("//", StringComparison.Ordinal);
        string native = src.Replace('\\', Path.DirectorySeparatorChar);
        // 1) cosi' com'e' (su Windows i percorsi assoluti valgono ancora).
        try
        {
            string direct = windowsAbsolute || Path.IsPathRooted(native) ? native : Path.GetFullPath(Path.Combine(baseDir, native));
            if (File.Exists(direct)) return direct;
        }
        catch { }

        var parts = Segments(src).Where(p => p != "." && p != "..").ToArray();
        if (windowsAbsolute && parts.Length > 0 && parts[0].EndsWith(':')) parts = parts[1..];
        if (parts.Length == 0) return null;

        // 2) relativo alla cartella della playlist (e alle cartelle sopra), togliendo le cartelle iniziali.
        // Solo il nome del file vale accanto alla playlist; nelle cartelle sopra (fino a tre) serve anche la cartella
        // che lo contiene, altrimenti un omonimo qualsiasi passerebbe per il brano giusto.
        string? dir = baseDir;
        for (int up = 0; up <= 3 && !string.IsNullOrEmpty(dir); up++, dir = Path.GetDirectoryName(dir))
        {
            for (int skip = 0; skip < parts.Length - (up == 0 ? 0 : 1); skip++)
            {
                string candidate = Path.Combine(new[] { dir }.Concat(parts.Skip(skip)).ToArray());
                if (File.Exists(candidate)) return candidate;
            }
            if (Path.GetPathRoot(dir) == dir) break;
        }

        // 3) in libreria per nome del file: vince chi ha piu' cartelle in comune partendo dal fondo.
        if (!index.TryGetValue(parts[^1], out var matches) || matches.Count == 0) return null;
        string? best = null; int bestScore = -1;
        foreach (string match in matches)
        {
            var mine = Segments(match);
            int score = 0;
            while (score < Math.Min(mine.Length, parts.Length) &&
                   string.Equals(mine[^(score + 1)], parts[^(score + 1)], StringComparison.OrdinalIgnoreCase)) score++;
            // A parita' di cartelle vince il file che c'e' davvero (un disco staccato resta in libreria).
            score = score * 2 + (File.Exists(match) ? 1 : 0);
            if (score > bestScore) { bestScore = score; best = match; }
        }
        return best;
    }
}
