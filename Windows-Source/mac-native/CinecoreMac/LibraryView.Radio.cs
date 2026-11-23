using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Radio (CinematicMediaLibraryPage.Radio di Windows): finita la coda, la si allunga con brani della libreria che stanno
/// bene dopo l'ultimo. Nessun servizio esterno: l'artista, chi ha suonato negli stessi dischi, gli anni vicini, la
/// stessa raccolta su disco, i preferiti e cio' che e' stato ascoltato fino in fondo; pesa contro cio' che e' appena
/// passato. Un po' di caso evita che la stessa canzone porti sempre agli stessi brani.
/// </summary>
internal sealed partial class LibraryView
{
    private static bool MusicRadio { get => MacSettings.Get("musicRadio", false); set => MacSettings.Set("musicRadio", value); }

    private List<LibraryItem> PickRadioTracks(LibraryItem seed, IEnumerable<LibraryItem> exclude, int count)
    {
        var result = new List<LibraryItem>();
        try
        {
            var tracks = _items.Where(i => i.Category == "Music" && !string.IsNullOrWhiteSpace(i.Path))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (tracks.Count < 2) return result;
            HashSet<string> ArtistsOf(LibraryItem t) => new[] { t.ArtistName, t.AlbumArtist, AlbumArtist(t) }
                .Where(a => !UnknownTag(a)).Select(a => MusicTextIdentity.ArtistKey(a!)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
            static string AlbumOf(LibraryItem t) => MusicArtistArtworkService.Identity(t.AlbumTitle) + "|" + (t.IsNetwork ? "" : (Path.GetDirectoryName(t.Path) ?? "").ToUpperInvariant());
            static string ShelfOf(LibraryItem t)
            {
                if (t.IsNetwork) return "";
                string? folder = Path.GetDirectoryName(t.Path);
                return (folder == null ? "" : Path.GetDirectoryName(folder) ?? folder).ToUpperInvariant();
            }
            var seedArtists = ArtistsOf(seed);
            string seedAlbum = AlbumOf(seed), seedShelf = ShelfOf(seed);
            var albumsWithSeedArtist = tracks.Where(t => ArtistsOf(t).Overlaps(seedArtists)).Select(AlbumOf).ToHashSet(StringComparer.Ordinal);
            var related = tracks.Where(t => albumsWithSeedArtist.Contains(AlbumOf(t))).SelectMany(ArtistsOf).ToHashSet(StringComparer.Ordinal);
            related.ExceptWith(seedArtists);
            var skip = new HashSet<string>(exclude.Select(e => e.Path), StringComparer.OrdinalIgnoreCase) { seed.Path };
            var history = MusicListeningStore.Snapshot();
            DateTime now = DateTime.UtcNow;
            var ranked = tracks.Where(t => !skip.Contains(t.Path)).Select(t =>
            {
                var artists = ArtistsOf(t);
                double score = Random.Shared.NextDouble() * 22;
                if (artists.Overlaps(seedArtists)) score += 60;
                else if (artists.Overlaps(related)) score += 30;
                if (AlbumOf(t) == seedAlbum) score += 12;
                else if (ShelfOf(t) == seedShelf && seedShelf.Length > 0) score += 10;
                if (seed.Year.HasValue && t.Year.HasValue) score += Math.Max(0, 15 - Math.Abs(seed.Year.Value - t.Year.Value) * 3);
                if (Favorites.Contains(t.Path)) score += 10;
                if (history.TryGetValue(t.Path, out var heard))
                {
                    if (heard.Duration > 0 && heard.Position >= heard.Duration * .9) score += 6;   // ascoltato fino in fondo
                    double hours = (now - heard.UpdatedUtc).TotalHours;
                    if (hours < 3) score -= 45; else if (hours < 24) score -= 15;                     // appena passato
                }
                return (Track: t, Score: score, Album: AlbumOf(t), Artist: artists.FirstOrDefault() ?? "");
            }).OrderByDescending(e => e.Score).ToList();
            // Varieta': al massimo due brani per album e quattro per artista (finche' c'e' scelta).
            var perAlbum = new Dictionary<string, int>(StringComparer.Ordinal);
            var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in ranked)
            {
                if (result.Count >= count) break;
                perAlbum.TryGetValue(e.Album, out int a); perArtist.TryGetValue(e.Artist, out int b);
                if (a >= 2 || b >= 4) continue;
                perAlbum[e.Album] = a + 1; perArtist[e.Artist] = b + 1;
                result.Add(e.Track);
            }
            foreach (var e in ranked) { if (result.Count >= count) break; if (!result.Contains(e.Track)) result.Add(e.Track); }
        }
        catch (Exception ex) { Console.Error.WriteLine("[RADIO] pick failed: " + ex.Message); }
        return result;
    }

    /// <summary>Fine della coda con la Radio accesa: si aggiungono dodici brani e si prosegue.</summary>
    private bool TryExtendQueueWithRadio()
    {
        if (!MusicRadio || !MusicBarVisible) return false;
        var picks = PickRadioTracks(_queue[_queueIndex], _queue, 12);
        if (picks.Count == 0) return false;
        _queue.AddRange(picks);
        _queueOriginal.AddRange(picks);
        Console.Error.WriteLine($"[RADIO] queue extended with {picks.Count} tracks");
        return true;
    }
}
