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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "tidal.config.json");
    private static Credentials? _credentials = LoadCredentials();
    private static string? _accessToken;
    private static DateTime _tokenExpiresUtc;

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
        }
        MusicArtistArtworkService.ResetProviderCache();
    }

    public static async Task<SpotifyArtistArtworkService.ArtistImage> ResolveAsync(string artist, string cacheDirectory)
    {
        Credentials? credentials;
        lock (Sync) credentials = _credentials;
        var none = new SpotifyArtistArtworkService.ArtistImage(null, null, null);
        if (credentials == null || string.IsNullOrWhiteSpace(artist)) return none;
        try
        {
            string token = await GetAccessTokenAsync(credentials).ConfigureAwait(false);
            string country = MetadataCountry();
            // 1. Search: the artists matching the name, with their popularity.
            using var search = await GetAsync($"https://openapi.tidal.com/v2/searchResults/{Uri.EscapeDataString(artist)}/relationships/artists?countryCode={country}&include=artists", token).ConfigureAwait(false);
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
            var files = new List<(string Url, int Width, int Height)>();
            foreach (var artwork in included.EnumerateArray())
            {
                if (Text(artwork, "type") != "artworks" || !artwork.TryGetProperty("attributes", out var attributes)) continue;
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
            var landscape = ordered.FirstOrDefault(file => MusicLandscapeArtworkService.IsLandscape(file.Width, file.Height));
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(wanted)));
            string? portraitPath = await FetchImageAsync(portrait.Url, Path.Combine(cacheDirectory, key + "-tidal-artist.jpg")).ConfigureAwait(false);
            string? landscapePath = landscape.Url == null ? null
                : landscape.Url == portrait.Url ? portraitPath
                : await FetchImageAsync(landscape.Url, Path.Combine(cacheDirectory, key + "-tidal-wide.jpg")).ConfigureAwait(false);
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
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<string?> FetchImageAsync(string url, string path)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !(uri.Host == "tidal.com" || uri.Host.EndsWith(".tidal.com", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (File.Exists(path)) return path;
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length > 16 * 1024 * 1024) return null;
            using (var stream = new MemoryStream(bytes)) using (var image = Image.FromStream(stream))
                if (image.Width < 200 || image.Height < 200) return null;
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
