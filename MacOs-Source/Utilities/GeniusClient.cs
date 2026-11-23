#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

// One session and one request at a time for both artwork and lyrics.
internal static class GeniusClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
#if CINECORE_MAC
        // I cookie della verifica vengono conservati dalla sessione WebKit.
        UseCookies = false
#else
        CookieContainer = new CookieContainer()
#endif
    }) { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, (string Text, DateTime Expires)> Cache = new();
    private static DateTime _useBrowserUntil;
    private static string CacheDirectory { get; set; } = Path.Combine(
        global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "GeniusResponses-v1");
    private sealed record SavedResponse(string Url, string Text);

    private static bool IsUsable(string url, string text)
    {
        if (!new Uri(url).AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            return text.Contains("data-lyrics-container", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("og:image", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static string ResponsePath(string url) => Path.Combine(CacheDirectory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".json");

    private static bool HasSearchHits(string url, string text)
    {
        if (!new Uri(url).AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)) return true;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("response").TryGetProperty("sections", out var sections) &&
            sections.ValueKind == JsonValueKind.Array && sections.EnumerateArray().Any(section =>
                section.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array && hits.GetArrayLength() > 0);
    }

    private static string? ReadSavedResponse(string url)
    {
        try
        {
            string path = ResponsePath(url);
            if (!File.Exists(path) || new FileInfo(path).Length > 10 * 1024 * 1024) return null;
            TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < TimeSpan.Zero || age > TimeSpan.FromDays(url.Contains("/api/", StringComparison.Ordinal) ? 1 : 7)) return null;
            var saved = JsonSerializer.Deserialize<SavedResponse>(File.ReadAllText(path));
            return saved?.Url == url && IsUsable(url, saved.Text) && HasSearchHits(url, saved.Text) ? saved.Text : null;
        }
        catch { return null; }
    }

    private static void SaveResponse(string url, string text)
    {
        string? temporary = null;
        try
        {
            if (text.Length > 2 * 1024 * 1024 || !IsUsable(url, text) || !HasSearchHits(url, text)) return;
            Directory.CreateDirectory(CacheDirectory);
            string path = ResponsePath(url);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedResponse(url, text)));
            File.Move(temporary, path, overwrite: true);
            foreach (var old in new DirectoryInfo(CacheDirectory).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(128))
                old.Delete();
        }
        catch { /* A cache failure must not discard a successful online lookup. */ }
        finally { if (temporary != null) try { File.Delete(temporary); } catch { } }
    }

    internal static async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "genius.com")
            return null;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(url, out var cached) && cached.Expires > DateTime.UtcNow) return cached.Text;
            string? saved = ReadSavedResponse(url);
            if (saved != null)
            {
                if (Cache.Count >= 128) Cache.Clear();
                Cache[url] = (saved, DateTime.UtcNow.AddMinutes(10));
                return saved;
            }
            if (!GeniusAvailability.CanRequest) return null;
            string? text = null;
            if (_useBrowserUntil <= DateTime.UtcNow)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("CinecorePlayer2025/1.0");
                request.Headers.Accept.ParseAdd(uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) ? "application/json" : "text/html");
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // A normal browser session can present the site's verification to the user.
                    // Do not forge clearance cookies or automate interactive challenges.
                    _useBrowserUntil = DateTime.UtcNow.AddMinutes(30);
                }
                else
                {
                    GeniusAvailability.Observe(response.StatusCode);
                    Dbg.Warn($"[GENIUS] HTTP {(int)response.StatusCode}: {uri.AbsolutePath}");
                    return null;
                }
            }
            if (text == null)
            {
                text = await GeniusBrowserSession.GetTextAsync(url, ct).ConfigureAwait(false);
                if (text == null)
                {
                    GeniusAvailability.Observe(HttpStatusCode.Forbidden);
                    return null;
                }
            }
            if (!IsUsable(url, text)) return null;
            SaveResponse(url, text);
            if (Cache.Count >= 128) Cache.Clear();
            Cache[url] = (text, DateTime.UtcNow.AddMinutes(10));
            return text;
        }
        finally { Gate.Release(); }
    }
}
