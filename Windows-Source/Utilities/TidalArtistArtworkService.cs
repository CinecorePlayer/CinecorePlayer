#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

/// <summary>
/// Artist photos from TIDAL's public catalogue API (openapi.tidal.com/v2, JSON:API). The API
/// gives catalogue data only, with an application's own client id and secret (client
/// credentials): nothing of the listener's account is used and no audio is streamed.
/// </summary>
internal static class TidalArtistArtworkService
{
    private sealed record StoredCredentials(string ClientId, string ProtectedSecret);
    private sealed record Credentials(string ClientId, string ClientSecret);
    private static readonly object Sync = new();
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string ConfigPath = Path.Combine(
        global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "tidal.config.json");
    private static Credentials? _credentials = LoadCredentials();
    private static string? _accessToken;
    private static DateTime _tokenExpiresUtc;
    private static long _artworkRetryAfter;

    public static bool IsConfigured { get { lock (Sync) return _credentials != null; } }
    public static string? ConfiguredClientId { get { lock (Sync) return _credentials?.ClientId; } }

    public static void Configure(string? clientId, string? clientSecret)
    {
        clientId = clientId?.Trim();
        clientSecret = clientSecret?.Trim();
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                _credentials = null;
                if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(clientSecret) && string.Equals(clientId, _credentials?.ClientId, StringComparison.Ordinal))
                    clientSecret = _credentials!.ClientSecret;
                if (string.IsNullOrWhiteSpace(clientSecret))
                    throw new ArgumentException("TIDAL client secret is required.", nameof(clientSecret));
                // The secret is stored encrypted for this Windows user, never in clear.
                string protectedSecret = SecretVault.Protect(clientSecret) ?? throw new InvalidOperationException("Cannot protect the secret.");
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                string temporary = ConfigPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new StoredCredentials(clientId, protectedSecret)), Encoding.UTF8);
                File.Move(temporary, ConfigPath, overwrite: true);
                _credentials = new Credentials(clientId, clientSecret);
            }
            _accessToken = null;
            _tokenExpiresUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _artworkRetryAfter, 0);
        }
        MusicArtistArtworkService.ResetProviderCache();
    }

    public static async Task<SpotifyArtistArtworkService.ArtistImage> ResolveAsync(string artist, string cacheDirectory)
    {
        Credentials? credentials;
        lock (Sync) credentials = _credentials;
        var none = new SpotifyArtistArtworkService.ArtistImage(null, null, null);
        if (credentials == null || string.IsNullOrWhiteSpace(artist) || DateTime.UtcNow.Ticks < Interlocked.Read(ref _artworkRetryAfter)) return none;
        try
        {
            string token = await GetAccessTokenAsync(credentials).ConfigureAwait(false);
            string country = MetadataCountry();
            // 1. Search: the artists matching the name, with their popularity.
            // TIDAL v2 now accepts searches through filter[query], not a query segment in the path.
            using var search = await GetAsync($"https://openapi.tidal.com/v2/searchResults?filter%5Bquery%5D={Uri.EscapeDataString(artist)}&countryCode={country}&include=artists", token).ConfigureAwait(false);
            if (search == null || !search.RootElement.TryGetProperty("included", out var found) || found.ValueKind != JsonValueKind.Array) return none;
            string wanted = MusicArtistArtworkService.Identity(artist);
            var match = found.EnumerateArray()
                .Where(item => Text(item, "type") == "artists" && item.TryGetProperty("attributes", out var attributes) &&
                               MusicArtistArtworkService.Identity(Text(attributes, "name")) == wanted)
                .OrderByDescending(item => item.GetProperty("attributes").TryGetProperty("popularity", out var popularity) && popularity.TryGetDouble(out double value) ? value : 0)
                .Select(item => Text(item, "id"))
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
            if (match == null) return none;

            // 2. The artist with its profile pictures.
            using var detail = await GetAsync($"https://openapi.tidal.com/v2/artists/{Uri.EscapeDataString(match)}?countryCode={country}&include=profileArt", token).ConfigureAwait(false);
            string artistUrl = "https://tidal.com/artist/" + match;
            if (detail == null || !detail.RootElement.TryGetProperty("included", out var included) || included.ValueKind != JsonValueKind.Array)
                return new(null, null, artistUrl);
            if (!detail.RootElement.TryGetProperty("data", out var artistData) ||
                !artistData.TryGetProperty("relationships", out var relationships) ||
                !relationships.TryGetProperty("profileArt", out var profileArt) ||
                !profileArt.TryGetProperty("data", out var profileData)) return new(null, null, artistUrl);
            var profileIds = new HashSet<string>(StringComparer.Ordinal);
            if (profileData.ValueKind == JsonValueKind.Array)
                foreach (var profile in profileData.EnumerateArray()) { if (Text(profile, "id") is { } id) profileIds.Add(id); }
            else if (profileData.ValueKind == JsonValueKind.Object && Text(profileData, "id") is { } profileId) profileIds.Add(profileId);
            var files = new List<(string Url, int Width, int Height)>();
            foreach (var artwork in included.EnumerateArray())
            {
                if (Text(artwork, "type") != "artworks" || !profileIds.Contains(Text(artwork, "id") ?? "") || !artwork.TryGetProperty("attributes", out var attributes)) continue;
                if (Text(attributes, "mediaType") is { Length: > 0 } media && media != "IMAGE") continue;
                if (!attributes.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var file in list.EnumerateArray())
                {
                    string? href = Text(file, "href");
                    if (href == null || !file.TryGetProperty("meta", out var meta)) continue;
                    int width = meta.TryGetProperty("width", out var w) && w.TryGetInt32(out int wv) ? wv : 0;
                    int height = meta.TryGetProperty("height", out var h) && h.TryGetInt32(out int hv) ? hv : 0;
                    if (width > 0 && height > 0) files.Add((href, width, height));
                }
            }
            if (files.Count == 0) return new(null, null, artistUrl);
            var ordered = files.OrderByDescending(file => (long)file.Width * file.Height).ToList();
            var portrait = ordered[0];
            if (!await ProfileIsNotAlbumCoverAsync(match, country, token, profileIds, portrait.Url).ConfigureAwait(false))
            { Dbg.Log("[ARTIST ART] TIDAL album fallback rejected for " + artist); return new(null, null, artistUrl); }
            var landscape = ordered.FirstOrDefault(file => MusicLandscapeArtworkService.IsLandscape(file.Width, file.Height));
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(wanted)));
            string? portraitPath = await FetchImageAsync(portrait.Url, Path.Combine(cacheDirectory, key + "-tidal-artist.jpg")).ConfigureAwait(false);
            string? landscapePath = landscape.Url == null ? null
                : landscape.Url == portrait.Url ? (MusicLandscapeArtworkService.IsLandscapeFile(portraitPath) ? portraitPath : null)
                : await FetchImageAsync(landscape.Url, Path.Combine(cacheDirectory, key + "-tidal-wide-1080p.jpg"), requireLandscape: true).ConfigureAwait(false);
            return new(portraitPath, landscapePath, artistUrl);
        }
        catch (Exception ex)
        {
            Dbg.Warn("TIDAL artist artwork unavailable: " + ex.Message);
            return none;
        }
    }

    /// <summary>Checks the saved credentials against TIDAL (used by the settings page to say "they work").</summary>
    public static async Task<bool> TestAsync()
    {
        Credentials? credentials;
        lock (Sync) credentials = _credentials;
        if (credentials == null) return false;
        try { return !string.IsNullOrEmpty(await GetAccessTokenAsync(credentials).ConfigureAwait(false)); }
        catch (Exception ex) { Dbg.Warn("TIDAL credentials rejected: " + ex.Message); return false; }
    }

    /// <summary>Token for catalogue requests (covers, track data); null when not configured or rejected.</summary>
    internal static async Task<string?> AccessTokenAsync()
    {
        Credentials? credentials;
        lock (Sync) credentials = _credentials;
        if (credentials == null) return null;
        try { return await GetAccessTokenAsync(credentials).ConfigureAwait(false); }
        catch (Exception ex) { Dbg.Warn("TIDAL token unavailable: " + ex.Message); return null; }
    }

    private static string MetadataCountry()
    {
        try
        {
            string region = new System.Globalization.RegionInfo(System.Globalization.CultureInfo.CurrentCulture.Name).TwoLetterISORegionName;
            return region.Length == 2 ? region.ToUpperInvariant() : "US";
        }
        catch { return "US"; }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<JsonDocument?> GetAsync(string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.api+json");
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            Interlocked.Exchange(ref _artworkRetryAfter, DateTime.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(15)).Ticks);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static string ImageIdentity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        // TIDAL serves all sizes of one artwork from the same UUID directory.
        return uri.Host + uri.AbsolutePath[..(uri.AbsolutePath.LastIndexOf('/') + 1)];
    }

    private static async Task<bool> ProfileIsNotAlbumCoverAsync(string artistId, string country, string token, HashSet<string> profileIds, string profileUrl)
    {
        string route = "/v2/artists/" + Uri.EscapeDataString(artistId) + "/relationships/albums";
        string? url = "https://openapi.tidal.com" + route + "?countryCode=" + country + "&include=albums.coverArt";
        for (int page = 0; page < 8; page++)
        {
            using var albums = await GetAsync(url!, token).ConfigureAwait(false);
            if (albums == null) return false;
            if (albums.RootElement.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
            {
                var coverIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var album in included.EnumerateArray())
                {
                    if (Text(album,"type") != "albums" || !album.TryGetProperty("relationships",out var rel) ||
                        !rel.TryGetProperty("coverArt",out var cover) || !cover.TryGetProperty("data",out var data)) continue;
                    if (data.ValueKind == JsonValueKind.Array) foreach (var item in data.EnumerateArray()) { if (Text(item,"id") is string id) coverIds.Add(id); }
                    else if (data.ValueKind == JsonValueKind.Object && Text(data,"id") is string id) coverIds.Add(id);
                }
                if (coverIds.Overlaps(profileIds)) return false;
                foreach (var artwork in included.EnumerateArray())
                {
                    if (Text(artwork,"type") != "artworks" || !coverIds.Contains(Text(artwork,"id") ?? "") ||
                        !artwork.TryGetProperty("attributes",out var attr) || !attr.TryGetProperty("files",out var files)) continue;
                    if (files.EnumerateArray().Any(f => Text(f,"href") is string href && ImageIdentity(href) == ImageIdentity(profileUrl))) return false;
                }
            }
            string? next = null;
            if (albums.RootElement.TryGetProperty("links",out var links) && links.TryGetProperty("next",out var n))
                next = n.ValueKind == JsonValueKind.String ? n.GetString() : n.ValueKind == JsonValueKind.Object ? Text(n,"href") : null;
            if (string.IsNullOrWhiteSpace(next)) return true;
            var nextUri = new Uri(new Uri(url!), next);
            if (nextUri.Scheme != "https" || nextUri.Host != "openapi.tidal.com" || nextUri.AbsolutePath != route) return false;
            url = nextUri.AbsoluteUri;
        }
        return false; // Do not certify an incomplete catalogue.
    }

    private static async Task<string?> FetchImageAsync(string url, string path, bool requireLandscape = false)
    {
        try
        {
            // The profile URL can change independently of the artist identity.
            string version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
            path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + version + Path.GetExtension(path));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !(uri.Host == "tidal.com" || uri.Host.EndsWith(".tidal.com", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (File.Exists(path))
                return !requireLandscape || MusicLandscapeArtworkService.IsLandscapeFile(path) ? path : null;
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length > 16 * 1024 * 1024) return null;
            using (var stream = new MemoryStream(bytes)) using (var image = Image.FromStream(stream))
                if (requireLandscape
                    ? !MusicLandscapeArtworkService.IsLandscape(image.Width, image.Height)
                    : image.Width < 200 || image.Height < 200) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex)
        {
            Dbg.Warn("TIDAL image unavailable: " + ex.Message);
            return null;
        }
    }

    private static async Task<string> GetAccessTokenAsync(Credentials credentials)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiresUtc) return _accessToken;
        await TokenGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiresUtc) return _accessToken;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.tidal.com/v1/oauth2/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(credentials.ClientId + ":" + credentials.ClientSecret)));
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            string token = doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidDataException("TIDAL token missing.");
            int seconds = doc.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out int value) ? value : 3600;
            _accessToken = token;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(30, seconds - 90));
            return token;
        }
        finally { TokenGate.Release(); }
    }

    private static Credentials? LoadCredentials()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var stored = JsonSerializer.Deserialize<StoredCredentials>(File.ReadAllText(ConfigPath, Encoding.UTF8));
            if (string.IsNullOrWhiteSpace(stored?.ClientId) || string.IsNullOrWhiteSpace(stored.ProtectedSecret)) return null;
            string? secret = SecretVault.Unprotect(stored.ProtectedSecret);
            return string.IsNullOrEmpty(secret) ? null : new(stored.ClientId, secret);
        }
        catch { return null; }
    }
}
