#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        /// <summary>
        /// Radio: brani della libreria che "stanno bene" dopo <paramref name="seedPath"/>.
        /// Nessun servizio esterno: contano l'artista, chi ha suonato negli stessi dischi, gli
        /// anni vicini, la stessa raccolta su disco, i preferiti e cio' che e' stato ascoltato
        /// fino in fondo; pesa contro cio' che e' appena passato. Un po' di caso evita che
        /// la stessa canzone porti sempre agli stessi dodici brani.
        /// </summary>
        public IReadOnlyList<string> PickRadioTracks(string? seedPath, IEnumerable<string>? exclude, int count)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(seedPath) || count <= 0) return result;
            try
            {
                var tracks = _items.Concat(_itemCache.Values)
                    .SelectMany(item => item.IsGroup ? item.Children.AsEnumerable() : Enumerable.Repeat(item, 1))
                    .Where(item => item != null && !item.IsGroup && string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Path))
                    .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                var seed = tracks.FirstOrDefault(track => string.Equals(track.Path, seedPath, StringComparison.OrdinalIgnoreCase));
                if (seed == null || tracks.Count < 2) return result;

                static string Key(string? value) => MusicArtistArtworkService.Identity(value);
                static HashSet<string> ArtistsOf(LibraryItem track) => MusicArtists(track.ArtistName, track.AlbumArtist)
                    .Concat(MusicArtists(track.AlbumArtist)).Select(Key).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
                static string AlbumOf(LibraryItem track) => Key(track.AlbumTitle) + "|" + (System.IO.Path.GetDirectoryName(track.Path) ?? "").ToUpperInvariant();
                static string ShelfOf(LibraryItem track)
                {
                    // Due livelli sopra il file: di solito la cartella dell'artista o della raccolta.
                    string? folder = System.IO.Path.GetDirectoryName(track.Path);
                    return (folder == null ? "" : System.IO.Path.GetDirectoryName(folder) ?? folder).ToUpperInvariant();
                }

                var seedArtists = ArtistsOf(seed);
                string seedAlbum = AlbumOf(seed), seedShelf = ShelfOf(seed);
                // Chi compare negli stessi album dell'artista di partenza (collaborazioni, colonne sonore).
                var albumsWithSeedArtist = tracks.Where(track => ArtistsOf(track).Overlaps(seedArtists)).Select(AlbumOf).ToHashSet(StringComparer.Ordinal);
                var related = tracks.Where(track => albumsWithSeedArtist.Contains(AlbumOf(track))).SelectMany(ArtistsOf).ToHashSet(StringComparer.Ordinal);
                related.ExceptWith(seedArtists);

                var skip = new HashSet<string>(exclude ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase) { seed.Path };
                var history = MusicListeningStore.Snapshot();
                var favorites = GetFavoritePathsSnapshot();
                DateTime now = DateTime.UtcNow;
                var random = Random.Shared;

                var ranked = tracks
                    .Where(track => !skip.Contains(track.Path))
                    .Select(track =>
                    {
                        var artists = ArtistsOf(track);
                        double score = random.NextDouble() * 22;
                        if (artists.Overlaps(seedArtists)) score += 60;
                        else if (artists.Overlaps(related)) score += 30;
                        if (AlbumOf(track) == seedAlbum) score += 12;
                        else if (ShelfOf(track) == seedShelf) score += 10;
                        if (seed.Year.HasValue && track.Year.HasValue) score += Math.Max(0, 15 - Math.Abs(seed.Year.Value - track.Year.Value) * 3);
                        if (favorites.Contains(track.Path)) score += 10;
                        if (history.TryGetValue(track.Path, out var heard))
                        {
                            if (heard.Duration > 0 && heard.Position >= heard.Duration * .9) score += 6; // ascoltato fino in fondo
                            double hours = (now - heard.UpdatedUtc).TotalHours;
                            if (hours < 3) score -= 45; else if (hours < 24) score -= 15;                  // appena passato
                        }
                        return (Track: track, Score: score, Album: AlbumOf(track), Artist: artists.FirstOrDefault() ?? "");
                    })
                    .OrderByDescending(entry => entry.Score)
                    .ToList();

                // Varieta': al massimo due brani per album e quattro per artista (finche' c'e' scelta).
                var perAlbum = new Dictionary<string, int>(StringComparer.Ordinal);
                var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var entry in ranked)
                {
                    if (result.Count >= count) break;
                    perAlbum.TryGetValue(entry.Album, out int albumCount);
                    perArtist.TryGetValue(entry.Artist, out int artistCount);
                    if (albumCount >= 2 || artistCount >= 4) continue;
                    perAlbum[entry.Album] = albumCount + 1;
                    perArtist[entry.Artist] = artistCount + 1;
                    result.Add(entry.Track.Path);
                }
                foreach (var entry in ranked)
                {
                    if (result.Count >= count) break;
                    if (!result.Contains(entry.Track.Path, StringComparer.OrdinalIgnoreCase)) result.Add(entry.Track.Path);
                }
            }
            catch (Exception ex) { Dbg.Warn("[RADIO] pick failed: " + ex.Message); }
            return result;
        }
    }
}
