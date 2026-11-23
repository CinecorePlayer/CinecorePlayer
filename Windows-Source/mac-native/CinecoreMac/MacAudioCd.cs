using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace CinecoreMac;

/// <summary>
/// CD audio (AudioCd di Windows). macOS li monta da solo (cddafs): un volume con le tracce in AIFF ("1 Audio Track.aiff")
/// e la tabella del disco in .TOC.plist. Dalla tabella si calcola l'impronta MusicBrainz (stesso calcolo di Windows) e si
/// leggono album, artista, anno, titoli e copertina; restano in cache per disco. Le tracce le suona il Cinecore Audio
/// Engine come un album.
/// </summary>
internal static class MacAudioCd
{
    public sealed record Track(int Number, string Path, string Title, string Artist, double Seconds);
    public sealed record Disc(string Root, string Id, string Album, string Artist, string Year, string? Cover, List<Track> Tracks);

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // MusicBrainz chiede un nome del programma (senza dati di chi lo usa).
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer/3.6 (+https://github.com/cinecore)");
        return client;
    }

    private static string CacheRoot => System.IO.Path.Combine(global::CinecoreMac.PlatformPaths.CinecoreCaches, "audio-cd");

    /// <summary>Un volume e' un CD audio se ha la tabella del disco di cddafs e tracce AIFF.</summary>
    public static bool IsAudioCd(string? root)
    {
        try { return root != null && File.Exists(Path.Combine(root, ".TOC.plist")) && Directory.EnumerateFiles(root, "*.aiff").Any(); }
        catch { return false; }
    }

    /// <summary>Il file sta su un CD audio montato (/Volumes/&lt;disco&gt;/N titolo.aiff).</summary>
    public static bool IsOnAudioCd(string? path)
    {
        if (path == null || !path.StartsWith("/Volumes/", StringComparison.Ordinal) || !path.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase)) return false;
        string? root = Path.GetDirectoryName(path);
        return root != null && Path.GetDirectoryName(root) == "/Volumes" && File.Exists(Path.Combine(root, ".TOC.plist"));
    }

    /// <summary>CD audio montati (filesystem cddafs), senza leggere dentro il volume (vedi MacDisc.Inserted).</summary>
    public static IEnumerable<string> Inserted() => MacDisc.OpticalVolumes().Where(v => v.Format == "cddafs").Select(v => v.Root);

    /// <summary>Tracce e dati del disco (rete al primo ascolto, poi la cache). Mai sul thread dell'interfaccia.</summary>
    public static Disc Read(string root, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(root, "*.aiff")
            .Select(f => (Number: int.TryParse(Regex.Match(Path.GetFileName(f), @"^\d+").Value, out int n) ? n : 0, Path: f))
            .Where(t => t.Number > 0).OrderBy(t => t.Number).ToList();
        var toc = ReadToc(root);
        string id = toc is { } t ? DiscId(t.First, t.Last, t.LeadOut, t.Starts) : "";
        string folder = Path.Combine(CacheRoot, id.Length > 0 ? id : Sanitize(Path.GetFileName(root)));
        Directory.CreateDirectory(folder);
        var saved = LoadSaved(folder);
        if (saved == null && toc != null && id.Length > 0)
        {
            try { saved = Identify(id, toc.Value, files.Count, folder, ct); } catch (Exception ex) { Console.Error.WriteLine("[CD] MusicBrainz: " + ex.Message); }
        }
        string album = saved?.Album is { Length: > 0 } a ? a : Path.GetFileName(root) is var label && label != "Audio CD" ? label : "CD audio";
        string artist = saved?.Artist ?? "";
        var tracks = files.Select((f, i) =>
        {
            double seconds = 0;
            if (toc is { } tt && f.Number - tt.First is int k && k >= 0 && k < tt.Starts.Length)
                seconds = ((k + 1 < tt.Starts.Length ? tt.Starts[k + 1] : tt.LeadOut) - tt.Starts[k]) / 75.0;
            string title = saved?.Titles is { } titles && i < titles.Length && titles[i].Length > 0 ? titles[i] : "Traccia " + f.Number;
            string by = saved?.Artists is { } artists && i < artists.Length && artists[i].Length > 0 ? artists[i] : artist;
            return new Track(f.Number, f.Path, title, by, seconds);
        }).ToList();
        string? cover = File.Exists(Path.Combine(folder, "cover.jpg")) ? Path.Combine(folder, "cover.jpg") : null;
        var disc = new Disc(root, id, album, artist, saved?.Year ?? "", cover, tracks);
        lock (Known) foreach (var known in tracks) Known[known.Path] = (known, disc);
        return disc;
    }

    // Tracce dei dischi letti: titolo, artista e durata per chi guarda il file (i file AIFF del CD non hanno tag).
    private static readonly Dictionary<string, (Track Track, Disc Disc)> Known = new(StringComparer.Ordinal);

    /// <summary>Dati MusicBrainz della traccia di un CD gia' letto (null se il file non e' di un CD noto).</summary>
    public static (string Title, string Artist, string Album, double Seconds)? TrackInfo(string? path)
    {
        if (path == null) return null;
        lock (Known)
            return Known.TryGetValue(path, out var k) ? (k.Track.Title, k.Track.Artist.Length > 0 ? k.Track.Artist : k.Disc.Artist, k.Disc.Album, k.Track.Seconds) : null;
    }

    private static string Sanitize(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ----- tabella del disco (.TOC.plist di cddafs) -----
    private readonly record struct Toc(int First, int Last, int LeadOut, int[] Starts);

    private static Toc? ReadToc(string root)
    {
        try
        {
            // Solo "Sessions": il resto del file ha campi binari ("Format 0x02 TOC Data") che plutil non converte in JSON
            // (su un CD vero la conversione intera falliva e il disco restava senza nome).
            var (exit, stdout, _) = ProcessLauncher.Run("/usr/bin/plutil", new[] { "-extract", "Sessions", "json", "-o", "-", Path.Combine(root, ".TOC.plist") }, false, 5000, CancellationToken.None);
            if (exit != 0 || stdout.Length == 0) { Console.Error.WriteLine("[CD] tabella del disco illeggibile"); return null; }
            using var doc = JsonDocument.Parse(stdout);
            var sessions = doc.RootElement;
            if (sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() == 0) return null;
            var session = sessions[0];
            var points = new List<(int Point, int Start, bool Data)>();
            foreach (var t in session.GetProperty("Track Array").EnumerateArray())
                points.Add((t.GetProperty("Point").GetInt32(), t.GetProperty("Start Block").GetInt32(), t.TryGetProperty("Data", out var d) && d.ValueKind == JsonValueKind.True));
            var audio = points.Where(p => !p.Data).OrderBy(p => p.Point).ToList();
            if (audio.Count == 0) return null;
            int leadOut = session.GetProperty("Leadout Block").GetInt32();
            // CD con una sessione dati in coda: l'audio finisce 11400 settori prima (come Windows).
            if (points.Any(p => p.Data && p.Point > audio[^1].Point)) leadOut = points.First(p => p.Data && p.Point > audio[^1].Point).Start - 11400;
            // cddafs da' i blocchi come LBA (la prima traccia a 0); se partono da 150 sono gia' assoluti.
            int shift = audio[0].Start >= 150 ? -150 : 0;
            return new Toc(audio[0].Point, audio[^1].Point, leadOut + shift, audio.Select(p => p.Start + shift).ToArray());
        }
        catch (Exception ex) { Console.Error.WriteLine("[CD] tabella del disco: " + ex.Message); return null; }
    }

    /// <summary>Impronta MusicBrainz: SHA-1 di prima traccia, ultima, fine disco e 99 posizioni (+150), in esadecimale.</summary>
    public static string DiscId(int first, int last, int leadOutLba, IReadOnlyList<int> startsLba)
    {
        var text = new StringBuilder();
        text.Append(first.ToString("X2")).Append(last.ToString("X2")).Append((leadOutLba + 150).ToString("X8"));
        for (int number = 1; number <= 99; number++)
        {
            int index = number - first;
            text.Append((index >= 0 && index < startsLba.Count ? startsLba[index] + 150 : 0).ToString("X8"));
        }
        return Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(text.ToString()))).Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }

    // ----- MusicBrainz e Cover Art Archive (Identify di Windows) -----
    private sealed class Saved
    {
        public string? Album { get; set; }
        public string? Artist { get; set; }
        public string? Year { get; set; }
        public string[]? Titles { get; set; }
        public string[]? Artists { get; set; }
    }

    private static Saved? LoadSaved(string folder)
    {
        try
        {
            string file = Path.Combine(folder, "disc.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) is { Titles.Length: > 0 } s ? s : null : null;
        }
        catch { return null; }
    }

    private static Saved? Identify(string id, Toc toc, int trackCount, string folder, CancellationToken ct)
    {
        string query = toc.First + "+" + toc.Last + "+" + (toc.LeadOut + 150) + "+" + string.Join("+", toc.Starts.Select(s => s + 150));
        using var response = Http.GetAsync("https://musicbrainz.org/ws/2/discid/" + Uri.EscapeDataString(id) + "?fmt=json&inc=artist-credits+recordings&toc=" + query, ct).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
        if (!doc.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return null;
        foreach (var release in releases.EnumerateArray())
        {
            if (!release.TryGetProperty("media", out var media)) continue;
            JsonElement? chosen = null;
            foreach (var medium in media.EnumerateArray())
            {
                bool sameDisc = medium.TryGetProperty("discs", out var discs) && discs.EnumerateArray().Any(d => d.TryGetProperty("id", out var did) && did.GetString() == id);
                bool sameCount = medium.TryGetProperty("tracks", out var tracks) && tracks.GetArrayLength() == trackCount;
                if (sameDisc && sameCount) { chosen = medium; break; }
                if (sameCount) chosen ??= medium;
            }
            if (chosen is not { } found) continue;
            string date = release.TryGetProperty("date", out var d2) ? d2.GetString() ?? "" : "";
            var saved = new Saved
            {
                Album = release.TryGetProperty("title", out var title) ? title.GetString() : "",
                Artist = Credit(release),
                Year = date.Length >= 4 ? date[..4] : "",
                Titles = found.GetProperty("tracks").EnumerateArray().Select(t => t.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "").ToArray(),
                Artists = found.GetProperty("tracks").EnumerateArray().Select(t =>
                {
                    string by = Credit(t);
                    return by.Length == 0 && t.TryGetProperty("recording", out var recording) ? Credit(recording) : by;
                }).ToArray(),
            };
            try { File.WriteAllText(Path.Combine(folder, "disc.json"), JsonSerializer.Serialize(saved)); } catch { }
            if (release.TryGetProperty("id", out var rid) && rid.GetString() is { } releaseId)
            {
                try
                {
                    using var cover = Http.GetAsync("https://coverartarchive.org/release/" + releaseId + "/front-500", ct).GetAwaiter().GetResult();
                    if (cover.IsSuccessStatusCode && cover.Content.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult() is { Length: > 2000 } image)
                        File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), image);
                }
                catch { }
            }
            return saved;
        }
        return null;
    }

    private static string Credit(JsonElement owner)
    {
        if (!owner.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var part in credit.EnumerateArray())
        {
            text.Append(part.TryGetProperty("name", out var name) ? name.GetString() : "");
            text.Append(part.TryGetProperty("joinphrase", out var join) ? join.GetString() : "");
        }
        return text.ToString().Trim();
    }
}
