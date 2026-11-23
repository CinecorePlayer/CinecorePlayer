using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;
using AudioMediaProbe = CinecorePlayer2025.Utilities.MediaProbe;

namespace CinecoreMac;

internal static partial class LibraryStore
{
    private sealed record MusicMetadata(long Length, DateTime Modified, AudioMediaProbe.AudioTags Tags);
    private static string MusicMetadataPath => Path.Combine(AppDataDir, "music-metadata.json");
    private static readonly Lazy<Dictionary<string, MusicMetadata>> SavedMusicMetadata =
        new(() => Load<Dictionary<string, MusicMetadata>>(MusicMetadataPath) ?? new());
    private static Dictionary<string, MusicMetadata>? _musicMetadata;
    private static Dictionary<string, MusicMetadata> CurrentMusicMetadata => Volatile.Read(ref _musicMetadata) ?? SavedMusicMetadata.Value;
    private static readonly Dictionary<string, (MusicMetadata? Metadata, LibraryItem Item)> MusicItems = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim MusicMetadataGate = new(1, 1);
    private static readonly SemaphoreSlim MusicArtworkGate = new(4, 4);

    private static void ScanMusicMetadata(List<string> paths, List<string> roots, CancellationToken ct)
    {
        var previous = CurrentMusicMetadata;
        var updated = new Dictionary<string, MusicMetadata>(StringComparer.Ordinal);
        var tracks = new List<(string Path, long Length, DateTime Modified, AudioMediaProbe.AudioTags Tags)>();
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            previous.TryGetValue(path, out var existing);
            if (existing != null) updated[path] = existing;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                long length = file.Length;
                DateTime modified = file.LastWriteTimeUtc;
                var tags = existing is { Tags.ReadFromFile: true } && existing.Tags.TagVersion >= AudioMediaProbe.CurrentTagVersion && existing.Length == length && existing.Modified == modified
                    ? existing.Tags : AudioMediaProbe.ReadAudioTags(path, ct);
                tracks.Add((path, length, modified, tags));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }

        foreach (var folder in tracks.GroupBy(track => Path.GetDirectoryName(track.Path) ?? ""))
        {
            // Una cartella di inediti può contenere brani con tag e brani senza: usa l'artista dei primi
            // solo se tutti gli indizi espliciti della cartella identificano lo stesso artista.
            var artists = folder.Select(t => TaggedArtist(t.Tags) ?? FilenameArtist(t.Path))
                .OfType<string>().GroupBy(MusicTextIdentity.ArtistKey).Take(2).ToList();
            string? folderArtist = artists.Count == 1 && MusicArtworkService.IsLooseCollection(Path.GetFileName(folder.Key))
                ? MusicTextIdentity.PreferredArtistName(artists[0]) : null;
            foreach (var track in folder)
            {
                ct.ThrowIfCancellationRequested();
                string? root = artists.Count > 1 ? folder.Key : roots.Where(r => WithinFolder(track.Path, r)).MaxBy(r => r.Length);
                var tags = FillMusicTags(track.Path, track.Tags, folderArtist, root);
                updated[track.Path] = new(track.Length, track.Modified, tags);
            }
        }
        Volatile.Write(ref _musicMetadata, updated);
        Save(MusicMetadataPath, updated);
    }

    private static string? MusicTag(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('[') ? null : value.Trim();

    private static string? TaggedArtist(AudioMediaProbe.AudioTags tags) =>
        MusicTag(tags.FileArtist) ?? MusicTag(tags.AlbumArtist);

    private static string? FilenameArtist(string path)
    {
        string title = MusicTextIdentity.Title(Path.GetFileNameWithoutExtension(path), filename: true);
        return MusicLyricsService.InferArtistFromPath(path, ref title, Path.GetDirectoryName(path));
    }

    private static AudioMediaProbe.AudioTags FillMusicTags(string path, AudioMediaProbe.AudioTags tags, string? folderArtist, string? root)
    {
        string? taggedTitle = MusicTag(tags.FileTitle);
        string title = MusicTextIdentity.Title(taggedTitle ?? Path.GetFileNameWithoutExtension(path), filename: taggedTitle == null);
        string? fromFileName = MusicLyricsService.InferArtistFromPath(path, ref title, Path.GetDirectoryName(path));
        string? fromFolder = null;
        if (TaggedArtist(tags) == null && fromFileName == null && folderArtist == null)
        {
            string volumeBoundary = path.StartsWith("/Volumes/", StringComparison.Ordinal)
                ? string.Join("/", path.Split('/', 4).Take(3)) : "";
            string? boundary = root == null ? null : PathText.Trim(root);
            if (string.IsNullOrEmpty(boundary) || boundary.Length <= volumeBoundary.Length) boundary = volumeBoundary;
            fromFolder = MusicLyricsService.InferArtistFromPath(path, ref title, boundary);
        }
        return tags with
        {
            Title = title,
            Artist = MusicTextIdentity.Artist(TaggedArtist(tags) ?? fromFileName ?? folderArtist ?? fromFolder),
            AlbumArtist = MusicTextIdentity.Artist(tags.AlbumArtist),
            Album = MusicTag(tags.Album) ?? "",
        };
    }

    private static LibraryItem MusicItem(string path)
    {
        CurrentMusicMetadata.TryGetValue(path, out var metadata);
        if (MusicItems.TryGetValue(path, out var cached) && cached.Metadata == metadata) return cached.Item;
        var tags = metadata?.Tags ?? FillMusicTags(path, new AudioMediaProbe.AudioTags(), null, Path.GetDirectoryName(path));
        var item = BuildMusicItem(path, tags, metadata?.Modified ?? MusicModified(path));
        MusicItems[path] = (metadata, item);
        return item;
    }

    internal static LibraryItem ReadMusicItem(string path)
    {
        var tags = new AudioMediaProbe.AudioTags();
        try { tags = AudioMediaProbe.ReadAudioTags(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return BuildMusicItem(path, FillMusicTags(path, tags, null, Path.GetDirectoryName(path)), MusicModified(path));
    }

    private static DateTime MusicModified(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static LibraryItem BuildMusicItem(string path, AudioMediaProbe.AudioTags tags, DateTime modified) => new()
    {
        Path = path, Category = "Music", PlaybackCategory = "Musica",
        Title = tags.Title,
        ArtistName = MusicTag(tags.Artist),
        AlbumArtist = MusicTag(tags.AlbumArtist),
        AlbumTitle = MusicTag(tags.Album),
        TrackNumber = tags.TrackNumber,
        Year = tags.Year,
        Genres = MusicGenres(tags.Genre),
        DurationMinutes = tags.DurationMinutes,
        SortDateUtc = modified,
        MusicTags = tags,
    };

    /// <summary>Il genere del tag: piu' generi separati da ';' (ID3v2.4, Vorbis) diventano voci distinte.</summary>
    private static List<string> MusicGenres(string? genre) => string.IsNullOrWhiteSpace(genre) ? new()
        : genre.Split(new[] { ';', '\0' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Connessione tornata: copertine dei brani rimaste senza risultato, titoli/locandine TMDb e servizi messi in pausa
    /// per errori di rete si richiedono subito, invece di restare vuoti fino al riavvio.
    /// </summary>
    internal static void ForgetNetworkFailures()
    {
        RefreshMetadataRequests();
        foreach (var (_, item) in MusicItems.Values)
            if (item.ArtPath == null && item.MusicArtworkRetryAfter != DateTime.MaxValue) item.MusicArtworkRetryAfter = DateTime.MinValue;
        MusicArtworkService.ForgetRecentMisses();
        ProviderBackoff.ForgetNetworkFailures();
        LastFmSimilar.ForgetFailures();
        MacAlbumInfo.ForgetRecentMisses();
        Changed?.Invoke();
    }

    internal static bool QueueMusicArtwork(LibraryItem item)
    {
        if (item.MusicArtworkRetryAfter == DateTime.MaxValue) return true;
        if (item.MusicTags == null || item.ArtPath != null || DateTime.UtcNow < item.MusicArtworkRetryAfter ||
            !MusicArtworkGate.Wait(0)) return false;
        item.MusicArtworkRetryAfter = DateTime.MaxValue;
        var tags = item.MusicTags;
        _ = Task.Run(async () =>
        {
            string? art = null;
            try
            {
                art = MusicArtworkService.GetCachedArtworkPath(item.Path, tags) ??
                      await MusicArtworkService.ResolveArtworkAsync(item.Path, CancellationToken.None, tags).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
            finally
            {
                MusicArtworkGate.Release();
                Dispatcher.UIThread.Post(() =>
                {
                    // Album cambiato nel frattempo (trovato online): questa copertina e' del vecchio, si ricerca.
                    bool stale = !ReferenceEquals(tags, item.MusicTags);
                    item.ArtPath = stale ? null : art;
                    item.MusicArtworkRetryAfter = stale ? DateTime.MinValue : DateTime.UtcNow.AddMinutes(15);
                    Changed?.Invoke();
                });
            }
        });
        return true;
    }
}
