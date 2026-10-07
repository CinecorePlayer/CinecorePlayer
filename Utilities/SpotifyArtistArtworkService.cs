#nullable enable
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

internal static class SpotifyArtistArtworkService
{
    internal sealed record ArtistImage(string? Portrait, string? Landscape, string? ArtistUrl);
    private sealed record StoredCredentials(string ClientId, string ProtectedSecret);
    private sealed record Credentials(string ClientId, string ClientSecret);
    private static readonly object Sync = new();
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CinecorePlayer2025", "spotify.config.json");
    private static Credentials? _credentials = LoadCredentials();
    private static string? _accessToken;
    private static DateTime _tokenExpiresUtc;

    public static bool IsConfigured
    {
        get { lock (Sync) return _credentials != null; }
    }

    public static string? ConfiguredClientId
    {
        get { lock (Sync) return _credentials?.ClientId; }
    }

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
                if (string.IsNullOrWhiteSpace(clientSecret) &&
                    string.Equals(clientId, _credentials?.ClientId, StringComparison.Ordinal))
                    clientSecret = _credentials!.ClientSecret;
                if (string.IsNullOrWhiteSpace(clientSecret))
                    throw new ArgumentException("Spotify client secret is required.", nameof(clientSecret));

                string protectedSecret = Protect(clientSecret);
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                string temporary = ConfigPath + ".tmp";
                File.WriteAllText(temporary,
                    JsonSerializer.Serialize(new StoredCredentials(clientId, protectedSecret)), Encoding.UTF8);
                File.Move(temporary, ConfigPath, overwrite: true);
                _credentials = new Credentials(clientId, clientSecret);
            }
            _accessToken = null;
            _tokenExpiresUtc = DateTime.MinValue;
        }
        MusicArtistArtworkService.ResetProviderCache();
    }

    public static async Task<ArtistImage> ResolveAsync(string artist, string cacheDirectory)
    {
        Credentials? credentials;
        lock (Sync) credentials = _credentials;
        if (credentials == null || string.IsNullOrWhiteSpace(artist)) return new(null, null, null);
        try
        {
            string token = await GetAccessTokenAsync(credentials).ConfigureAwait(false);
            string query = Uri.EscapeDataString("artist:\"" + artist + "\"");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.spotify.com/v1/search?q=" + query + "&type=artist&limit=5");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("artists", out var artists) ||
                !artists.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return new(null, null, null);

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var name) ||
                    MusicArtistArtworkService.Identity(name.GetString()) != MusicArtistArtworkService.Identity(artist))
                    continue;

                string? artistUrl = null;
                if (item.TryGetProperty("external_urls", out var links) &&
                    links.TryGetProperty("spotify", out var link)) artistUrl = link.GetString();
                if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                    return new(null, null, artistUrl);

                var choices = images.EnumerateArray()
                    .Select(image => new
                    {
                        Url = image.TryGetProperty("url", out var url) ? url.GetString() : null,
                        Width = image.TryGetProperty("width", out var width) && width.TryGetInt32(out int w) ? w : 0,
                        Height = image.TryGetProperty("height", out var height) && height.TryGetInt32(out int h) ? h : 0
                    })
                    .Where(image => image.Url != null && image.Width > 0 && image.Height > 0)
                    .OrderByDescending(image => (long)image.Width * image.Height)
                    .ToList();
                var portrait = choices.FirstOrDefault();
                var landscape = choices.FirstOrDefault(image => MusicLandscapeArtworkService.IsLandscape(image.Width, image.Height));
                string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(MusicArtistArtworkService.Identity(artist))));
                string? portraitPath = portrait == null ? null : await FetchImageAsync(portrait.Url!,
                    Path.Combine(cacheDirectory, key + "-spotify-artist.jpg")).ConfigureAwait(false);
                string? landscapePath = landscape == null ? null :
                    string.Equals(landscape.Url, portrait?.Url, StringComparison.Ordinal) ? portraitPath :
                    await FetchImageAsync(landscape.Url!,
                        Path.Combine(cacheDirectory, key + "-spotify-wide.jpg")).ConfigureAwait(false);
                return new(portraitPath, landscapePath, artistUrl);
            }
        }
        catch (Exception ex)
        {
            Dbg.Warn("Spotify artist artwork unavailable: " + ex.Message);
        }
        return new(null, null, null);
    }

    /// <summary>Token for catalogue requests (covers, track data); null when not configured or rejected.</summary>
    internal static async Task<string?> AccessTokenAsync()
    {
        Credentials? credentials;
        lock (Sync) credentials = _credentials;
        if (credentials == null) return null;
        try { return await GetAccessTokenAsync(credentials).ConfigureAwait(false); }
        catch (Exception ex) { Dbg.Warn("Spotify token unavailable: " + ex.Message); return null; }
    }

    private static async Task<string?> FetchImageAsync(string url, string path)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !(uri.Host == "i.scdn.co" || uri.Host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase)))
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
            Dbg.Warn("Spotify image unavailable: " + ex.Message);
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
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(credentials.ClientId + ":" + credentials.ClientSecret)));
            request.Content = new FormUrlEncodedContent(new[] { new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "client_credentials") });
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            string token = doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidDataException("Spotify token missing.");
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
            return new(stored.ClientId, Unprotect(stored.ProtectedSecret));
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static string Protect(string value) => Convert.ToBase64String(TransformDpapi(Encoding.UTF8.GetBytes(value), true));
    private static string Unprotect(string value) => Encoding.UTF8.GetString(TransformDpapi(Convert.FromBase64String(value), false));
    private static byte[] TransformDpapi(byte[] input, bool protect)
    {
        var source = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Marshal.Copy(input, 0, source.Data, input.Length);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var protectedBlob)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out protectedBlob);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] result = new byte[protectedBlob.Length];
                Marshal.Copy(protectedBlob.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(protectedBlob.Data); }
        }
        finally { Marshal.FreeHGlobal(source.Data); }
    }
}
