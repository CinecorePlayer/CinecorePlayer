#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

/// <summary>
/// Album covers and track data from the catalogues that need the user's own application keys
/// (Spotify, TIDAL). Which one is asked first follows "Music source" in the settings; the other
/// is the fallback, and the key-less sources (iTunes, Deezer) remain behind both, or in front
/// when the user chose Deezer. Only titles and names are sent, never a local path.
/// </summary>
internal static class MusicCatalog
{
    internal sealed record Track(string Title, string Artist, string Album, string? ReleaseDate, int? TrackNumber, int? TrackCount,
        int? DiscNumber, double? DurationSeconds, string? Genre, string? CoverUrl, string Source);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Configured catalogues, in the order chosen by the user.</summary>
    public static IReadOnlyList<string> Providers()
    {
        string[] order = MusicArtistArtworkService.PreferredSource == "tidal" ? new[] { "Tidal", "Spotify" } : new[] { "Spotify", "Tidal" };
        return order.Where(name => name == "Spotify" ? SpotifyArtistArtworkService.IsConfigured : TidalArtistArtworkService.IsConfigured).ToList();
    }

    /// <summary>False when the user put Deezer first: the key-less sources are asked before these.</summary>
    public static bool AskedFirst => MusicArtistArtworkService.PreferredSource != "deezer";

    /// <summary>Changes when the set or the order of catalogues changes: "already tried" notes are tied to it.</summary>
    public static string Signature => string.Join(">", Providers());

    public static async Task<Track?> FindTrackAsync(string title, string? artist, string? album, double? durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        foreach (string provider in Providers())
        {
            try
            {
                Track? found = provider == "Tidal"
                    ? await TidalTrackAsync(title, artist, durationSeconds, ct).ConfigureAwait(false)
                    : await SpotifyTrackAsync(title, artist, album, durationSeconds, ct).ConfigureAwait(false);
                if (found != null) return found;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn($"[MUSIC] {provider} track lookup: {ex.Message}"); }
        }
        return null;
    }

    public static async Task<string?> FindAlbumCoverUrlAsync(string? artist, string? album, string? title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(album) && string.IsNullOrWhiteSpace(title)) return null;
        foreach (string provider in Providers())
        {
            try
            {
                string? url = provider == "Tidal"
                    ? await TidalCoverAsync(artist, album, title, ct).ConfigureAwait(false)
                    : await SpotifyCoverAsync(artist, album, title, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn($"[MUSIC] {provider} cover lookup: {ex.Message}"); }
        }
        return null;
    }

    private static string Id(string? value) => MusicArtistArtworkService.Identity(value);

    /// <summary>Same title once brackets ("(Remastered 2011)", "[Live]") and "feat." tails are ignored.</summary>
    private static bool SameTitle(string? a, string? b)
    {
        static string Core(string? value) => Id(System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s*[\(\[].*?[\)\]]|\s+-\s+.*$|\s+feat\.?.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        string x = Core(a), y = Core(b);
        return x.Length > 0 && x == y;
    }

    private static bool SameArtist(string? wanted, IEnumerable<string?> found)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return true;
        string want = Id(wanted);
        // "A, B" or "A feat. B" in the file against a single credited artist in the catalogue, and the other way round.
        return found.Select(Id).Any(name => name.Length > 0 && (name == want || want.Contains(name) || name.Contains(want)));
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, string token, string? accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (accept != null) request.Headers.Accept.ParseAdd(accept);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number > 0 ? number : null;

    // ---------------- Spotify ----------------

    private static string? SpotifyImage(JsonElement album)
    {
        if (!album.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        return images.EnumerateArray()
            .Select(image => (Url: Str(image, "url"), Width: Int(image, "width") ?? 0))
            .Where(image => image.Url != null)
            .OrderByDescending(image => image.Width)
            .Select(image => image.Url)
            .FirstOrDefault();
    }

    private static async Task<JsonElement[]> SpotifySearchAsync(string type, string query, CancellationToken ct)
    {
        string? token = await SpotifyArtistArtworkService.AccessTokenAsync().ConfigureAwait(false);
        if (token == null) return Array.Empty<JsonElement>();
        using var doc = await GetJsonAsync($"https://api.spotify.com/v1/search?type={type}&limit=8&q={Uri.EscapeDataString(query)}", token, null, ct).ConfigureAwait(false);
        if (doc == null || !doc.RootElement.TryGetProperty(type + "s", out var group) || !group.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<JsonElement>();
        return items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.Clone()).ToArray();
    }

    private static async Task<Track?> SpotifyTrackAsync(string title, string? artist, string? album, double? duration, CancellationToken ct)
    {
        string query = "track:\"" + title + "\"" + (string.IsNullOrWhiteSpace(artist) ? "" : " artist:\"" + artist + "\"");
        var items = await SpotifySearchAsync("track", query, ct).ConfigureAwait(false);
        // The field search is strict with punctuation: a plain search is the second try.
        if (items.Length == 0) items = await SpotifySearchAsync("track", (artist + " " + title).Trim(), ct).ConfigureAwait(false);
        Track? best = null;
        double bestScore = double.MinValue;
        foreach (var item in items)
        {
            string? name = Str(item, "name");
            var artists = item.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(entry => Str(entry, "name")).Where(entry => entry != null).ToList() : new List<string?>();
            if (!SameTitle(name, title) || !SameArtist(artist, artists)) continue;
            item.TryGetProperty("album", out var albumElement);
            string albumName = Str(albumElement, "name") ?? "";
            double? seconds = item.TryGetProperty("duration_ms", out var ms) && ms.TryGetInt64(out long value) && value > 0 ? value / 1000.0 : null;
            double score = (!string.IsNullOrWhiteSpace(album) && SameTitle(albumName, album) ? 3 : 0)
                         - (duration.HasValue && seconds.HasValue ? Math.Min(3, Math.Abs(duration.Value - seconds.Value) / 4) : 0);
            if (score <= bestScore) continue;
            bestScore = score;
            best = new Track(name!, string.Join(", ", artists), albumName, Str(albumElement, "release_date"), Int(item, "track_number"),
                Int(albumElement, "total_tracks"), Int(item, "disc_number"), seconds, null, SpotifyImage(albumElement), "Spotify");
        }
        return best;
    }

    private static async Task<string?> SpotifyCoverAsync(string? artist, string? album, string? title, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(album))
        {
            string query = "album:\"" + album + "\"" + (string.IsNullOrWhiteSpace(artist) ? "" : " artist:\"" + artist + "\"");
            var albums = await SpotifySearchAsync("album", query, ct).ConfigureAwait(false);
            if (albums.Length == 0) albums = await SpotifySearchAsync("album", (artist + " " + album).Trim(), ct).ConfigureAwait(false);
            foreach (var item in albums)
            {
                var artists = item.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(entry => Str(entry, "name")).ToList() : new List<string?>();
                if (SameTitle(Str(item, "name"), album) && SameArtist(artist, artists) && SpotifyImage(item) is { } url) return url;
            }
        }
        if (string.IsNullOrWhiteSpace(title)) return null;
        return (await SpotifyTrackAsync(title, artist, album, null, ct).ConfigureAwait(false))?.CoverUrl;
    }

    // ---------------- TIDAL ----------------

    private const string TidalRoot = "https://openapi.tidal.com/v2";
    private const string JsonApi = "application/vnd.api+json";

    private static string TidalCountry()
    {
        try
        {
            string region = new System.Globalization.RegionInfo(System.Globalization.CultureInfo.CurrentCulture.Name).TwoLetterISORegionName;
            return region.Length == 2 ? region.ToUpperInvariant() : "US";
        }
        catch { return "US"; }
    }

    private static IEnumerable<JsonElement> Included(JsonDocument? doc, string type)
    {
        if (doc == null || !doc.RootElement.TryGetProperty("included", out var included) || included.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in included.EnumerateArray())
            if (Str(item, "type") == type && item.TryGetProperty("attributes", out _)) yield return item;
    }

    private static string? TidalArtwork(JsonDocument? doc)
    {
        // The largest file up to 1280 px (the 3000 px originals are megabytes for a tile on screen).
        var files = Included(doc, "artworks")
            .Where(artwork => Str(artwork.GetProperty("attributes"), "mediaType") is null or "IMAGE")
            .SelectMany(artwork => artwork.GetProperty("attributes").TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : Array.Empty<JsonElement>())
            .Select(file => (Url: Str(file, "href"), Width: file.TryGetProperty("meta", out var meta) ? Int(meta, "width") ?? 0 : 0))
            .Where(file => file.Url != null && file.Width > 0)
            .ToList();
        if (files.Count == 0) return null;
        return files.Where(file => file.Width <= 1280).OrderByDescending(file => file.Width).Select(file => file.Url).FirstOrDefault()
            ?? files.OrderBy(file => file.Width).First().Url;
    }

    private static double? IsoSeconds(string? duration)
    {
        try { return string.IsNullOrWhiteSpace(duration) ? null : System.Xml.XmlConvert.ToTimeSpan(duration).TotalSeconds; }
        catch { return null; }
    }

    private static async Task<Track?> TidalTrackAsync(string title, string? artist, double? duration, CancellationToken ct)
    {
        string? token = await TidalArtistArtworkService.AccessTokenAsync().ConfigureAwait(false);
        if (token == null) return null;
        string country = TidalCountry();
        using var search = await GetJsonAsync($"{TidalRoot}/searchResults/{Uri.EscapeDataString((artist + " " + title).Trim())}/relationships/tracks?countryCode={country}&include=tracks", token, JsonApi, ct).ConfigureAwait(false);
        var candidates = Included(search, "tracks")
            .Where(track => SameTitle(Str(track.GetProperty("attributes"), "title"), title))
            .Select(track => (Id: Str(track, "id"), Seconds: IsoSeconds(Str(track.GetProperty("attributes"), "duration")), Title: Str(track.GetProperty("attributes"), "title")!))
            .Where(track => track.Id != null)
            .OrderBy(track => duration.HasValue && track.Seconds.HasValue ? Math.Abs(duration.Value - track.Seconds.Value) : 0)
            .Take(3)
            .ToList();
        foreach (var candidate in candidates)
        {
            // Album, artists and genre are separate resources linked to the track.
            using var detail = await GetJsonAsync($"{TidalRoot}/tracks/{candidate.Id}?countryCode={country}&include=albums,artists,genres", token, JsonApi, ct).ConfigureAwait(false);
            var artists = Included(detail, "artists").Select(entry => Str(entry.GetProperty("attributes"), "name")).ToList();
            if (!SameArtist(artist, artists)) continue;
            var album = Included(detail, "albums").FirstOrDefault();
            string albumName = "", albumId = "";
            string? release = null;
            int? count = null;
            if (album.ValueKind == JsonValueKind.Object)
            {
                var attributes = album.GetProperty("attributes");
                albumName = Str(attributes, "title") ?? "";
                release = Str(attributes, "releaseDate");
                count = Int(attributes, "numberOfItems");
                albumId = Str(album, "id") ?? "";
            }
            string? genre = Included(detail, "genres").Select(entry => Str(entry.GetProperty("attributes"), "genreName")).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            string? cover = null;
            if (albumId.Length > 0)
            {
                using var art = await GetJsonAsync($"{TidalRoot}/albums/{albumId}?countryCode={country}&include=coverArt", token, JsonApi, ct).ConfigureAwait(false);
                cover = TidalArtwork(art);
            }
            return new Track(candidate.Title, string.Join(", ", artists.Where(name => name != null)), albumName, release, null, count, null, candidate.Seconds, genre, cover, "TIDAL");
        }
        return null;
    }

    private static async Task<string?> TidalCoverAsync(string? artist, string? album, string? title, CancellationToken ct)
    {
        string? token = await TidalArtistArtworkService.AccessTokenAsync().ConfigureAwait(false);
        if (token == null) return null;
        string country = TidalCountry();
        if (!string.IsNullOrWhiteSpace(album))
        {
            using var search = await GetJsonAsync($"{TidalRoot}/searchResults/{Uri.EscapeDataString((artist + " " + album).Trim())}/relationships/albums?countryCode={country}&include=albums", token, JsonApi, ct).ConfigureAwait(false);
            foreach (string id in Included(search, "albums").Where(entry => SameTitle(Str(entry.GetProperty("attributes"), "title"), album)).Select(entry => Str(entry, "id")).Where(id => id != null).Select(id => id!).Take(3).ToList())
            {
                using var detail = await GetJsonAsync($"{TidalRoot}/albums/{id}?countryCode={country}&include=coverArt,artists", token, JsonApi, ct).ConfigureAwait(false);
                if (!SameArtist(artist, Included(detail, "artists").Select(entry => Str(entry.GetProperty("attributes"), "name")))) continue;
                if (TidalArtwork(detail) is { } url) return url;
            }
        }
        if (string.IsNullOrWhiteSpace(title)) return null;
        return (await TidalTrackAsync(title, artist, null, ct).ConfigureAwait(false))?.CoverUrl;
    }
}
