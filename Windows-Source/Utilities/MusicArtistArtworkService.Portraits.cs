#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

internal static partial class MusicArtistArtworkService
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> PortraitChecks = new();
    private static readonly SemaphoreSlim WikimediaGate = new(1, 1);
    private static long _wikimediaRetryAfter;

    // Profile-art relationships are not a guarantee: providers also put album
    // artwork there when the artist has no photograph. Check the decoded image
    // against the artist's catalogue, regardless of the provider or filename.
    private static Task<bool> IsCataloguePortraitAsync(string artist, string path)
    {
        string key = Identity(artist) + "|" + path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        return PortraitChecks.GetOrAdd(key, _ => new Lazy<Task<bool>>(() => CheckCataloguePortraitAsync(PrimaryArtist(artist) ?? artist, path))).Value;
    }

    private static async Task<bool> CheckCataloguePortraitAsync(string artist, string path)
    {
        try
        {
            using var search = JsonDocument.Parse(await Http.GetStringAsync("https://api.deezer.com/search/artist?limit=25&q=" + Uri.EscapeDataString(artist)).ConfigureAwait(false));
            if (!search.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return false;
            var ids = data.EnumerateArray().Where(a => a.TryGetProperty("name", out var name) && LooseIdentity(name.GetString()) == LooseIdentity(artist))
                .Select(a => a.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0).Where(id => id > 0).Distinct().ToArray();
            if (ids.Length == 0) return false;
            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            foreach (long id in ids)
                if (!await IsDeezerPortraitAsync(id, new Uri("https://example.invalid/portrait"), bytes).ConfigureAwait(false))
                { Dbg.Log("[ARTIST ART] catalogue rejected profile image for " + artist); return false; }
            // Artist discographies can omit records with shared credits. Search
            // album credits too (e.g. a soundtrack issued under two composers).
            using var albums = JsonDocument.Parse(await Http.GetStringAsync("https://api.deezer.com/search/album?limit=100&q=" + Uri.EscapeDataString("artist:\"" + artist + "\"")).ConfigureAwait(false));
            if (!albums.RootElement.TryGetProperty("data", out var entries)) return false;
            var covers = entries.EnumerateArray().Select(a => a.TryGetProperty("cover_small", out var cover) ? cover.GetString() : null)
                .Where(url => IsDeezerImage(url, "cover", out _)).Distinct().ToArray();
            byte[] pixels = PhotoPixels(bytes); int matched = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Parallel.ForEachAsync(covers, new ParallelOptions { MaxDegreeOfParallelism=4,CancellationToken=timeout.Token }, async (url, token) =>
            {
                if (Volatile.Read(ref matched) != 0) return;
                try { if (SamePhoto(pixels, PhotoPixels(await Http.GetByteArrayAsync(url!, token).ConfigureAwait(false)))) Interlocked.Exchange(ref matched,1); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }).ConfigureAwait(false);
            if (matched != 0) return false;
            return true;
        }
        catch { return false; }
    }

    // Wikidata P18 is an image of the identified person/group, not a release
    // thumbnail. Require an exact name/alias, MusicBrainz identity and person or
    // musical-group type; never accept an album or a fuzzy homonym.
    private static async Task<string?> WikimediaPortraitAsync(string artist, string directory, string key)
    {
        string cachedPath = Path.Combine(directory, key + "-wikimedia-portrait.jpg");
        bool CachedPhoto() => File.Exists(cachedPath) && File.Exists(cachedPath + ".attribution.json") &&
            new FileInfo(cachedPath).Length > 1024 && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachedPath) < TimeSpan.FromDays(30);
        if (CachedPhoto()) return cachedPath;
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _wikimediaRetryAfter)) return null;
        await WikimediaGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _wikimediaRetryAfter)) return null;
            if (CachedPhoto()) return cachedPath;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            string name = PrimaryArtist(artist) ?? artist;
            using var search = JsonDocument.Parse(await Http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&language=en&limit=5&maxlag=5&search=" + Uri.EscapeDataString(name), timeout.Token).ConfigureAwait(false));
            if (!search.RootElement.TryGetProperty("search", out var matches)) return null;
            foreach (var match in matches.EnumerateArray())
            {
                string? id = match.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (id == null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^Q[0-9]+$")) continue;
                using var detail = JsonDocument.Parse(await Http.GetStringAsync("https://www.wikidata.org/wiki/Special:EntityData/" + id + ".json", timeout.Token).ConfigureAwait(false));
                var entity = detail.RootElement.GetProperty("entities").GetProperty(id);
                bool Same(JsonElement entry) => entry.TryGetProperty("value", out var value) && LooseIdentity(value.GetString()) == LooseIdentity(name);
                bool named = entity.TryGetProperty("labels", out var labels) && labels.EnumerateObject().Any(p => Same(p.Value));
                if (!named && entity.TryGetProperty("aliases", out var aliases)) named = aliases.EnumerateObject().Any(p => p.Value.EnumerateArray().Any(Same));
                if (!named || !entity.TryGetProperty("claims", out var claims) || !claims.TryGetProperty("P434", out _)) continue;
                JsonElement[] Values(string property) => claims.TryGetProperty(property, out var list)
                    ? list.EnumerateArray().Where(c => c.TryGetProperty("mainsnak", out var s) && s.TryGetProperty("datavalue", out _))
                        .Select(c => c.GetProperty("mainsnak").GetProperty("datavalue").GetProperty("value")).ToArray() : Array.Empty<JsonElement>();
                if (!Values("P31").Any(v => v.TryGetProperty("id", out var type) && type.GetString() is "Q5" or "Q215380")) continue;
                string? file = Values("P18").Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()).FirstOrDefault();
                if (file == null) continue;
                using var imageInfo = JsonDocument.Parse(await Http.GetStringAsync("https://commons.wikimedia.org/w/api.php?action=query&format=json&prop=imageinfo&iiprop=url%7Cextmetadata&iiurlwidth=640&maxlag=5&titles=" + Uri.EscapeDataString("File:" + file), timeout.Token).ConfigureAwait(false));
                foreach (var page in imageInfo.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
                {
                    if (!page.Value.TryGetProperty("imageinfo", out var info) || info.GetArrayLength() == 0) continue;
                    var first = info[0];
                    string? url = first.TryGetProperty("thumburl", out var thumb) ? thumb.GetString() : null;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host is not ("upload.wikimedia.org" or "thumb.wikimedia.org")) continue;
                    byte[] bytes = await Http.GetByteArrayAsync(uri, timeout.Token).ConfigureAwait(false);
                    if (bytes.Length < 1024 || bytes.Length > 16 * 1024 * 1024) continue;
                    _ = PhotoPixels(bytes); // decode before caching
                    Directory.CreateDirectory(directory);
                    string path = cachedPath;
                    await File.WriteAllBytesAsync(path, bytes, timeout.Token).ConfigureAwait(false);
                    // Keep the original author, license and source with the cached asset.
                    await File.WriteAllTextAsync(path + ".attribution.json", first.GetRawText(), timeout.Token).ConfigureAwait(false);
                    return path;
                }
            }
        }
        catch (Exception ex) { Dbg.Log("[ARTIST ART] Wikimedia unavailable: " + ex.Message); Interlocked.Exchange(ref _wikimediaRetryAfter, DateTime.UtcNow.AddMinutes(15).Ticks); }
        finally { WikimediaGate.Release(); }
        return null;
    }
}
