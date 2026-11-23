using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CinecoreMac;

// File aperti dal Finder ("Apri con", doppio clic, trascinati sull'icona) che non sono video (PlayerForm.ExternalOpen di
// Windows): la musica suona dentro la libreria, in Musica; le foto si aprono nel visualizzatore, in Foto.
internal sealed partial class LibraryView
{
    /// <summary>Brani dal Finder: la libreria va in Musica e li suona in coda (quelli gia' in libreria con i loro dati).</summary>
    public void PlayExternalMusic(IReadOnlyList<string> paths)
    {
        var indexed = LibraryStore.Items("Music").ToDictionary(i => i.Path, StringComparer.Ordinal);
        var tracks = paths.Where(File.Exists)
            .Select(p => indexed.TryGetValue(p, out var item) && item.MusicTags?.ReadFromFile == true
                ? item : LibraryStore.ReadMusicItem(p)).ToList();
        if (tracks.Count == 0) return;
        Navigate("Music");
        PlayQueue(tracks, 0);
    }

    /// <summary>Foto dal Finder: Foto, con il visualizzatore sulla prima (le altre scelte insieme le segue con le frecce).</summary>
    public void OpenExternalPhotos(IReadOnlyList<string> paths)
    {
        var photos = paths.Where(File.Exists).Select(p => _items.FirstOrDefault(i => i.Path == p) ?? new LibraryItem
        {
            Path = p, Title = Path.GetFileNameWithoutExtension(p), Category = "Photos", SortDateUtc = File.GetLastWriteTimeUtc(p),
        }).ToList();
        if (photos.Count == 0) return;
        Navigate("Photos");
        _photoList = photos; _photoIndex = 0; _photoOpenedAt = DateTime.UtcNow;
        _frames.Start();
        InvalidateVisual();
    }

    /// <summary>CD audio: le tracce come un album (titoli, artista e copertina da MusicBrainz se ci sono).</summary>
    public void PlayAudioCd(MacAudioCd.Disc disc)
    {
        var tracks = disc.Tracks.Select(t => new LibraryItem
        {
            Path = t.Path, Category = "Music", PlaybackCategory = "Musica", Title = t.Title,
            ArtistName = t.Artist.Length > 0 ? t.Artist : null, AlbumArtist = disc.Artist.Length > 0 ? disc.Artist : null,
            AlbumTitle = disc.Album, TrackNumber = t.Number, ArtPath = disc.Cover,
            DurationMinutes = t.Seconds > 0 ? t.Seconds / 60 : null,
            Year = int.TryParse(disc.Year, out int y) ? y : null,
        }).ToList();
        if (tracks.Count == 0) return;
        Navigate("Music");
        PlayQueue(tracks, 0);
    }

}

internal sealed partial class LibraryView
{
    /// <summary>Solo per le prove: apre il menu dell'ordinamento.</summary>
    public void TestOpenSortMenu() => OpenSortMenu();
}
