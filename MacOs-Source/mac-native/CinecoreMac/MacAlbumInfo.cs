using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;
using AudioMediaProbe = CinecorePlayer2025.Utilities.MediaProbe;

namespace CinecoreMac;

/// <summary>
/// Anno e genere di un album quando i tag dei file non li hanno (su Windows arrivano dal catalogo online, FetchMusicInfoAsync):
/// Spotify / TIDAL se configurati e messi davanti, altrimenti iTunes (negozio IT, poi US), con i cataloghi come riserva.
/// Si accetta solo un risultato con lo stesso album e lo stesso artista. Risposte e mancanze restano in album-info.json
/// (le mancanze si riprovano dopo una settimana).
/// </summary>
internal static class MacAlbumInfo
{
    public sealed record Info(int? Year, string? Genre, DateTime CheckedUtc, string? Album = null, string? Artist = null);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly object Gate = new();
    private static Dictionary<string, Info>? _cache;
    private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static string CachePath => Path.Combine(LibraryStore.AppDataDir, "album-info.json");

    /// <summary>Avvisa quando arriva una risposta (la scheda dell'album si ridisegna).</summary>
    public static event Action? Changed;
    public static event Action? TrackAlbumChanged;

    private sealed class LookupAttempt { public bool NetworkFailed; }
    private static readonly AsyncLocal<LookupAttempt?> Attempt = new();

    /// <summary>Connessione tornata: le voci "niente trovato" degli ultimi giorni (forse dovute alla rete) si richiedono.</summary>
    public static void ForgetRecentMisses()
    {
        lock (Gate)
        {
            if (_cache == null) return;
            var since = DateTime.UtcNow.AddDays(-2);
            foreach (var key in _cache.Where(p => p.Value.Album == null && p.Value.Year == null && p.Value.Genre == null && p.Value.CheckedUtc > since).Select(p => p.Key).ToList())
                _cache.Remove(key);
        }
        Dispatcher.UIThread.Post(() => { Changed?.Invoke(); TrackAlbumChanged?.Invoke(); });
    }

    private static string Key(string artist, string album) => Normalize(artist) + "\u001f" + Normalize(album);

    /// <summary>Senza le note d'edizione fra parentesi ("(Bonus Track Version)", "[Deluxe Edition]", "(Remastered 2015)").</summary>
    private static string NormalizeEdition(string? value) => Normalize(Regex.Replace(value ?? "",
        @"\s*[\(\[][^\)\]]*\b(deluxe|remaster(ed)?|bonus|edition|version|expanded|anniversary|special|reissue|edizione|rimasterizzat\w*)\b[^\)\]]*[\)\]]",
        "", RegexOptions.IgnoreCase));

    private static string Normalize(string? value) =>
        Regex.Replace(MusicArtworkService.NormalizePublicationTitle(value ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", "");

    private static Dictionary<string, Info> Cache
    {
        get
        {
            if (_cache != null) return _cache;
            try { _cache = File.Exists(CachePath) ? JsonSerializer.Deserialize<Dictionary<string, Info>>(File.ReadAllText(CachePath)) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return _cache ??= new(StringComparer.Ordinal);
        }
    }

    /// <summary>Quello che si sa gia' (null se ancora niente); se manca, chiede in sottofondo.</summary>
    public static Info? Get(string artist, string album, string? firstTitle, double? firstSeconds)
    {
        if (string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(artist)) return null;
        return GetOrLookup(Key(artist, album), () => LookupAsync(artist, album, firstTitle, firstSeconds), Changed);
    }

    public static Info? GetTrackAlbum(string artist, string title, double? seconds)
    {
        string clean = MusicTextIdentity.Title(Regex.Replace(title,
            @"\s*[\[(](?:official\s+(?:(?:hd|4k|music)\s+)*video|(?:hd|4k)\s+video)[\])]\s*$", "", RegexOptions.IgnoreCase));
        var parts = clean.Split(new[] { " - ", " – " }, 2, StringSplitOptions.None);
        if (parts.Length == 2 && Normalize(parts[0]) == Normalize(artist)) clean = parts[1];
        if (Normalize(artist).Length == 0 || Normalize(clean).Length == 0) return null;
        return GetOrLookup("track\u001f" + Key(artist, clean), () => LookupTrackAlbumAsync(artist, clean, seconds), TrackAlbumChanged);
    }

    private static Info? GetOrLookup(string key, Func<Task<Info?>> lookup, Action? changed)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var known) && (known.Album != null || known.Year != null || known.Genre != null || DateTime.UtcNow - known.CheckedUtc < TimeSpan.FromDays(7)))
                return known;
            if (!Pending.Add(key)) return known;
        }
        _ = Task.Run(async () =>
        {
            Info? found = null;
            bool failed = false;
            await Slots.WaitAsync().ConfigureAwait(false);
            var attempt = Attempt.Value = new LookupAttempt();
            try { found = await lookup().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { failed = ex is not JsonException; }
            finally { Slots.Release(); }
            // Senza rete (o con il servizio irraggiungibile) il "niente" non e' una risposta: non si ricorda, si riprova.
            if (found == null && (failed || attempt.NetworkFailed || !MacNetwork.IsOnline))
            {
                lock (Gate) Pending.Remove(key);
                MacNetwork.ReportFailure();
                return;
            }
            lock (Gate)
            {
                Pending.Remove(key);
                Cache[key] = found ?? new Info(null, null, DateTime.UtcNow);
                try
                {
                    Directory.CreateDirectory(LibraryStore.AppDataDir);
                    File.WriteAllText(CachePath + ".tmp", JsonSerializer.Serialize(Cache));
                    File.Move(CachePath + ".tmp", CachePath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (found != null && changed != null) Dispatcher.UIThread.Post(changed);
        });
        return null;
    }

    private static async Task<Info?> LookupTrackAlbumAsync(string artist, string title, double? seconds)
    {
        string artistKey = Normalize(artist), titleKey = Normalize(title);
        foreach (string country in new[] { "IT", "US" })
        {
            string url = "https://itunes.apple.com/search?media=music&entity=song&limit=25&country=" + country
                + "&term=" + Uri.EscapeDataString(artist + " " + title);
            try
            {
                using var response = await Http.GetAsync(url).ConfigureAwait(false);
                if ((int)response.StatusCode == 429) break;
                if (!response.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("results", out var results) && PickTrackAlbum(results, artistKey, titleKey, seconds) is { } found)
                    return found;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (ex is not JsonException && Attempt.Value is { } attempt) attempt.NetworkFailed = true;
                break;
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var track = await MusicCatalog.FindTrackAsync(title, artist, null, seconds, cts.Token).ConfigureAwait(false);
        return track != null && Normalize(track.Title) == titleKey && Normalize(track.Artist) == artistKey &&
            !string.IsNullOrWhiteSpace(track.Album) && DurationDifference(seconds, track.DurationSeconds) <= 20
            ? new Info(AudioMediaProbe.YearOf(track.ReleaseDate), track.Genre, DateTime.UtcNow, track.Album, track.Artist) : null;
    }

    private static double DurationDifference(double? local, double? online) =>
        local is > 0 && online is > 0 ? Math.Abs(local.Value - online.Value) : 0;

    private static Info? PickTrackAlbum(JsonElement results, string artistKey, string titleKey, double? seconds)
    {
        if (results.ValueKind != JsonValueKind.Array) return null;
        var matches = new List<(Info Info, bool Album, double Difference)>();
        foreach (var row in results.EnumerateArray())
        {
            string? Read(string key) => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string? name = Read("collectionName"), by = Read("artistName");
            if (string.IsNullOrWhiteSpace(name) || Normalize(by) != artistKey || Normalize(Read("trackName")) != titleKey) continue;
            string? albumArtist = Read("collectionArtistName");
            if (!string.IsNullOrWhiteSpace(albumArtist) && Normalize(albumArtist) != artistKey) continue;
            double? length = row.TryGetProperty("trackTimeMillis", out var ms) && ms.TryGetDouble(out double value) ? value / 1000 : null;
            double difference = DurationDifference(seconds, length);
            if (difference > 20) continue;
            bool fullAlbum = row.TryGetProperty("trackCount", out var n) && n.TryGetInt32(out int count) && count >= 7;
            matches.Add((new Info(AudioMediaProbe.YearOf(Read("releaseDate")), Read("primaryGenreName"), DateTime.UtcNow, name, by), fullAlbum, difference));
        }
        return matches.OrderByDescending(m => m.Album).ThenBy(m => m.Info.Year ?? int.MaxValue)
            .ThenBy(m => m.Difference).Select(m => m.Info).FirstOrDefault();
    }

    private static async Task<Info?> LookupAsync(string artist, string album, string? firstTitle, double? firstSeconds)
    {
        bool catalogs = MusicCatalog.Providers().Count > 0 && !string.IsNullOrWhiteSpace(firstTitle);
        if (catalogs && MusicCatalog.AskedFirst && await CatalogAsync(artist, album, firstTitle!, firstSeconds).ConfigureAwait(false) is { } first) return first;
        if (await ItunesAsync(artist, album).ConfigureAwait(false) is { } itunes) return itunes;
        if (catalogs && !MusicCatalog.AskedFirst) return await CatalogAsync(artist, album, firstTitle!, firstSeconds).ConfigureAwait(false);
        return null;
    }

    private static async Task<Info?> CatalogAsync(string artist, string album, string title, double? seconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var track = await MusicCatalog.FindTrackAsync(title, artist, album, seconds, cts.Token).ConfigureAwait(false);
        if (track == null || Normalize(track.Album) != Normalize(album)) return null;
        int? year = AudioMediaProbe.YearOf(track.ReleaseDate);
        string? genre = string.IsNullOrWhiteSpace(track.Genre) ? null : track.Genre.Trim();
        return year == null && genre == null ? null : new Info(year, genre, DateTime.UtcNow);
    }

    private static async Task<Info?> ItunesAsync(string artist, string album)
    {
        string wantAlbum = Normalize(album), wantArtist = Normalize(artist), wantCore = NormalizeEdition(album);
        foreach (string country in new[] { "IT", "US" })
        {
            string url = "https://itunes.apple.com/search?media=music&entity=album&limit=15&country=" + country
                + "&term=" + Uri.EscapeDataString(artist + " " + MusicArtworkService.NormalizePublicationTitle(album));
            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            if ((int)response.StatusCode == 429) return null;
            if (!response.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
            // Lo stesso album, anche in un'altra edizione (deluxe, bonus, rimasterizzata): vale lo stesso titolo esatto,
            // altrimenti l'edizione piu' vecchia (la data dell'uscita originale, non quella della ristampa).
            Info? exact = null, edition = null;
            foreach (var r in results.EnumerateArray())
            {
                string name = r.TryGetProperty("collectionName", out var c) ? c.GetString() ?? "" : "";
                string by = r.TryGetProperty("artistName", out var a) ? a.GetString() ?? "" : "";
                string b = Normalize(by);
                if (b.Length == 0 || !(b == wantArtist || b.Contains(wantArtist) || wantArtist.Contains(b))) continue;
                bool same = Normalize(name) == wantAlbum;
                if (!same && NormalizeEdition(name) != wantCore) continue;
                int? year = AudioMediaProbe.YearOf(r.TryGetProperty("releaseDate", out var d) ? d.GetString() : null);
                string? genre = r.TryGetProperty("primaryGenreName", out var g) ? g.GetString()?.Trim() : null;
                if (year == null && string.IsNullOrWhiteSpace(genre)) continue;
                var info = new Info(year, string.IsNullOrWhiteSpace(genre) ? null : genre, DateTime.UtcNow);
                if (same) { exact ??= info; continue; }
                if (edition == null || edition.Year == null || year < edition.Year) edition = info;
            }
            if ((exact ?? edition) is { } hit) return hit;
        }
        return null;
    }
}
