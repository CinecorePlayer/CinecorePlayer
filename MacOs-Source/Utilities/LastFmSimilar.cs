#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Artisti simili da Last.fm (artist.getSimilar), con i loro album piu' ascoltati (artist.getTopAlbums).
    /// Serve al "Potrebbe piacerti" della scheda album: i consigli vengono da chi ascolta davvero quella musica,
    /// non da un confronto di generi e anni nella libreria. Risposte in cache su disco per 30 giorni.
    /// </summary>
    internal static class LastFmSimilar
    {
        internal sealed record SimilarArtist(string Name, double Match);
        internal sealed record TopAlbum(string Artist, string Title, string? ImageUrl, string? Url);

        private const string Api = "https://ws.audioscrobbler.com/2.0/";
        private static readonly HttpClient Http = CreateClient();
        private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<SimilarArtist>>> Similar = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<TopAlbum>>> Albums = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan Fresh = TimeSpan.FromDays(30);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer/0.3");
            return client;
        }

        private static string CacheFolder => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "lastfm-similar");

        /// <summary>Gia' pronto in memoria (senza attesa): null se la risposta non e' ancora arrivata.</summary>
        public static IReadOnlyList<SimilarArtist>? TryGetSimilar(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return Array.Empty<SimilarArtist>();
            var task = Similar.GetOrAdd(artist.Trim(), name => Task.Run(() => LoadSimilar(name)));
            return task.IsCompletedSuccessfully ? task.Result : null;
        }

        public static Task<IReadOnlyList<SimilarArtist>> GetSimilarAsync(string artist) =>
            Similar.GetOrAdd(artist.Trim(), name => Task.Run(() => LoadSimilar(name)));

        public static IReadOnlyList<TopAlbum>? TryGetTopAlbums(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return Array.Empty<TopAlbum>();
            var task = Albums.GetOrAdd(artist.Trim(), name => Task.Run(() => LoadTopAlbums(name)));
            return task.IsCompletedSuccessfully ? task.Result : null;
        }

        private static IReadOnlyList<SimilarArtist> LoadSimilar(string artist)
        {
            try
            {
                string? json = Fetch("artist.getsimilar", artist, "&limit=40&autocorrect=1");
                if (json == null) return Array.Empty<SimilarArtist>();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("similarartists", out var root) || !root.TryGetProperty("artist", out var list) || list.ValueKind != JsonValueKind.Array)
                    return Array.Empty<SimilarArtist>();
                var result = new List<SimilarArtist>();
                foreach (var item in list.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    double match = 0;
                    if (item.TryGetProperty("match", out var m))
                        double.TryParse(m.ValueKind == JsonValueKind.String ? m.GetString() : m.GetRawText(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out match);
                    if (name.Length > 0) result.Add(new SimilarArtist(name, match));
                }
                return result;
            }
            catch (Exception ex) { Dbg.Warn("[LASTFM] similar: " + ex.Message); return Array.Empty<SimilarArtist>(); }
        }

        private static IReadOnlyList<TopAlbum> LoadTopAlbums(string artist)
        {
            try
            {
                string? json = Fetch("artist.gettopalbums", artist, "&limit=6&autocorrect=1");
                if (json == null) return Array.Empty<TopAlbum>();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("topalbums", out var root) || !root.TryGetProperty("album", out var list) || list.ValueKind != JsonValueKind.Array)
                    return Array.Empty<TopAlbum>();
                var result = new List<TopAlbum>();
                foreach (var item in list.EnumerateArray())
                {
                    string title = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (title.Length == 0 || title.Equals("(null)", StringComparison.OrdinalIgnoreCase)) continue;
                    string? image = null;
                    if (item.TryGetProperty("image", out var images) && images.ValueKind == JsonValueKind.Array)
                        foreach (var img in images.EnumerateArray())
                            if (img.TryGetProperty("#text", out var url) && url.GetString() is { Length: > 0 } u) image = u; // l'ultima e' la piu' grande
                    string? page = item.TryGetProperty("url", out var p) ? p.GetString() : null;
                    result.Add(new TopAlbum(artist, title, image, page));
                }
                return result;
            }
            catch (Exception ex) { Dbg.Warn("[LASTFM] top albums: " + ex.Message); return Array.Empty<TopAlbum>(); }
        }

        /// <summary>La copertina di un album di Last.fm scaricata in cache (null se non c'e' o non arriva).</summary>
        public static string? CachedImage(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            string file = Path.Combine(CacheFolder, "img-" + Hash(url) + ".jpg");
            if (File.Exists(file)) return file;
            if (ImageRequests.TryAdd(file, 0))
                _ = Task.Run(async () =>
                {
                    try
                    {
                        byte[] bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                        if (bytes.Length > 1000)
                        {
                            Directory.CreateDirectory(CacheFolder);
                            string temp = file + ".tmp";
                            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                            File.Move(temp, file, true);
                            ImageReady?.Invoke();
                        }
                    }
                    catch { }
                });
            return null;
        }

        private static readonly ConcurrentDictionary<string, byte> ImageRequests = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Connessione tornata: le risposte vuote (spesso dovute alla rete assente) e le immagini mancate si richiedono.</summary>
        public static void ForgetFailures()
        {
            foreach (var pair in Similar) if (pair.Value.IsCompleted && (!pair.Value.IsCompletedSuccessfully || pair.Value.Result.Count == 0)) Similar.TryRemove(pair);
            foreach (var pair in Albums) if (pair.Value.IsCompleted && (!pair.Value.IsCompletedSuccessfully || pair.Value.Result.Count == 0)) Albums.TryRemove(pair);
            ImageRequests.Clear();
            Unreachable = false;
            ImageReady?.Invoke();
        }

        /// <summary>Una risposta o un'immagine e' arrivata: la scheda si ridisegna.</summary>
        public static event Action? ImageReady;

        /// <summary>L'ultima richiesta a Last.fm non e' arrivata (niente rete o servizio irraggiungibile).</summary>
        public static bool Unreachable { get; private set; }

        private static string? Fetch(string method, string artist, string extra)
        {
            string key = SecretVault.BuiltIn("lastfm.key") ?? "";
            if (key.Length == 0) return null;
            string file = Path.Combine(CacheFolder, method + "-" + Hash(artist.ToLowerInvariant()) + ".json");
            try
            {
                if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Fresh) return File.ReadAllText(file);
            }
            catch { }
            try
            {
                string url = Api + "?method=" + method + "&artist=" + Uri.EscapeDataString(artist) + extra + "&api_key=" + key + "&format=json";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                string json = Http.GetStringAsync(url, timeout.Token).GetAwaiter().GetResult();
                if (json.Contains("\"error\"", StringComparison.Ordinal)) return null;
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(file, json);
                Unreachable = false;
                ImageReady?.Invoke();
                return json;
            }
            catch (Exception ex)
            {
                Dbg.Warn("[LASTFM] " + method + ": " + SecretVault.Redact(ex.Message));
                Unreachable = true;
                // Fuori linea: meglio la risposta vecchia che niente.
                try { if (File.Exists(file)) return File.ReadAllText(file); } catch { }
                return null;
            }
        }

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();
    }
}
