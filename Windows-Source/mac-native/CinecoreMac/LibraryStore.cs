using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CinecorePlayer2025.Utilities;
using AudioMediaProbe = CinecorePlayer2025.Utilities.MediaProbe;

namespace CinecoreMac;

/// <summary>Una voce della libreria (LibraryItem di CinematicMediaLibraryPage, la parte usata da Home e Film).</summary>
internal sealed class LibraryItem
{
    public string Path { get; init; } = "";
    public string Title { get; set; } = "";
    public string Category { get; init; } = "Movies";
    public int? Year { get; set; }
    // Dal client DLNA di Windows (musica e categoria di riproduzione).
    public string PlaybackCategory { get; set; } = "Film";
    public string? AlbumTitle { get; set; }
    public string? ArtistName { get; set; }
    public string? AlbumArtist { get; set; }
    public int? TrackNumber { get; set; }
    public DateTime SortDateUtc { get; init; }
    public string? ArtPath { get; set; }
    public string? WideArtPath { get; set; }
    public string? Overview { get; set; }
    public string? SeriesTitle { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    /// <summary>Da un server di rete (Jellyfin, Emby): Path e immagini sono indirizzi del server.</summary>
    public bool IsNetwork { get; set; }
    // Dati per la scheda dei dettagli (dal server, o da TMDb e dal file per i titoli locali).
    public double? DurationMinutes { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Tagline { get; set; }
    public double? Rating { get; set; }
    public string? ResolutionLabel { get; set; }
    public int VideoHeight { get; set; }
    public List<DlnaVideoResource>? DlnaVideoResources { get; set; }
    public bool Is4K { get; set; }
    public bool IsHdr { get; set; }
    public bool DolbyVision { get; set; }
    public string? AudioLabel { get; set; }
    public bool HasAtmos { get; set; }
    public long Bytes { get; set; }
    public string? FileName { get; set; }
    public string? Director { get; set; }
    public string? DirectorPhoto { get; set; }
    /// <summary>Serie: l'identificativo della serie sul server e la foto dell'episodio (still).</summary>
    public string? SeriesId { get; set; }
    public string? Still { get; set; }
    public List<(string Name, string Role, string? Photo)> Cast { get; set; } = new();
    /// <summary>Id TMDb dai metadati salvati (serve alla troupe).</summary>
    public int? TmdbId { get; set; }
    public double ResumeSeconds { get; set; }
    public double ResumeDuration { get; set; }
    public DateTime ResumeSavedAt { get; set; }
    internal AudioMediaProbe.AudioTags? MusicTags { get; set; }
    internal DateTime MusicArtworkRetryAfter { get; set; }
}

/// <summary>Preferiti (favorites.json come su Windows): i percorsi o gli indirizzi dei titoli con la stella.</summary>
internal static class Favorites
{
    private sealed class Model { public HashSet<string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
    private static string FilePath => Path.Combine(LibraryStore.AppDataDir, "favorites.json");
    private static Model? _model;
    private static Model State
    {
        get
        {
            if (_model != null) return _model;
            try { _model = File.Exists(FilePath) ? JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath)) : null; } catch { }
            return _model ??= new Model();
        }
    }
    public static bool Contains(string path) => State.Paths.Contains(path);
    public static List<string> RemotePaths() => State.Paths.ToList();
    public static void Toggle(string path)
    {
        if (!State.Paths.Remove(path)) State.Paths.Add(path);
        try { Directory.CreateDirectory(LibraryStore.AppDataDir); File.WriteAllText(FilePath, JsonSerializer.Serialize(State)); } catch { }
    }
}

/// <summary>
/// Cartelle, indice e voci della libreria. Stessi file e stesse regole di Windows (cinematicLibraryRoots.json,
/// cinematicLibraryIndex.json in Application Support/CinecorePlayer2025; estensioni, campioni e trailer ignorati,
/// episodi riconosciuti dal nome). Titoli, anni, locandine, sfondi e trame da MovieMetadataService (TMDb).
/// </summary>
internal static partial class LibraryStore
{
    // CINECORE_DATA_DIR: cartella separata per le prove automatiche (mai la libreria vera dell'utente).
    public static string AppDataDir { get; } = Environment.GetEnvironmentVariable("CINECORE_DATA_DIR") is { Length: > 0 } qa ? qa
        : Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
    private static string RootsPath => Path.Combine(AppDataDir, "cinematicLibraryRoots.json");
    private static string IndexPath => Path.Combine(AppDataDir, "cinematicLibraryIndex.json");

    private sealed class RootModel { public Dictionary<string, List<string>> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
    private sealed class IndexModel { public Dictionary<string, List<string>> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
    private static readonly SemaphoreSlim ScanGate = new(1, 1);

    public static readonly string[] FolderCategories = { "Movies", "TV Series", "Music", "Photos", "Videos" };

    /// <summary>Un titolo ha ora locandina, sfondo o trama (dal lavoro in sottofondo).</summary>
    public static event Action? Changed;

    private static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static void Save<T>(string path, T model)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private static IEnumerable<string> RootKeys(string category)
    {
        yield return category;
        if (category == "Music") yield return "Musica";
        else if (category == "Photos") yield return "Foto";
        else if (category == "Videos") yield return "Video";
        else if (category == "Movies") yield return "Film";
    }

    public static List<string> GetRoots(string category)
    {
        var model = Load<RootModel>(RootsPath) ?? new RootModel();
        var all = new List<string>();
        foreach (var key in RootKeys(category))
            if (model.Roots.TryGetValue(key, out var roots) && roots != null) all.AddRange(roots);
        return all.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
    }

    public static void AddRoot(string category, string folder)
    {
        var model = Load<RootModel>(RootsPath) ?? new RootModel();
        if (!model.Roots.TryGetValue(category, out var roots)) model.Roots[category] = roots = new List<string>();
        if (!roots.Contains(folder, StringComparer.Ordinal)) roots.Add(folder);
        Save(RootsPath, model);
    }

    public static void RemoveRoot(string category, string folder)
    {
        var model = Load<RootModel>(RootsPath) ?? new RootModel();
        foreach (var key in RootKeys(category))
            if (model.Roots.TryGetValue(key, out var roots)) { roots.RemoveAll(r => r == folder); if (roots.Count == 0) model.Roots.Remove(key); }
        Save(RootsPath, model);
    }

    // ------------------------------------------------------------------ classificazione (come Windows)
    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m2ts", ".mts", ".m2v", ".ts", ".mpg", ".mpeg", ".vob", ".3gp", ".ogv", ".iso" };

    private static readonly Regex TvEpisodeRegex = new(
        @"(?:\bS\d{1,2}E\d{1,3}\b)|(?:\b\d{1,2}x\d{1,3}\b)|(?:\b(?:season|stagione)\s*\d{1,2}\b)|(?:\b(?:episode|episodio|ep)\s*\d{1,3}\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsVideoPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4" or ".m4v" or ".mov" or ".avi" or ".wmv" or ".webm" or ".flv" or ".m2ts" or ".ts" or ".iso";

    public static bool IsTvEpisodePath(string path)
    {
        string sample = path.Replace('/', ' ').Replace('\\', ' ').Replace('.', ' ').Replace('_', ' ');
        return TvEpisodeRegex.IsMatch(sample);
    }

    private static bool BelongsTo(string path, string category) => category switch
    {
        "TV Series" => IsVideoPath(path) && IsTvEpisodePath(path),
        "Movies" => IsVideoPath(path) && !IsTvEpisodePath(path),
        "Videos" => IsVideoPath(path),
        "Photos" => IsPhotoPath(path),
        "Music" => MediaFileKinds.IsAudioExtension(Path.GetExtension(path).ToLowerInvariant()),
        _ => false,
    };

    private static bool ShouldIgnore(string path)
    {
        try
        {
            if (Regex.IsMatch(path, @"(?i)/(?:sample(?:,screens)?|samples|screens?|screen ?caps?|trailers?|teasers?|extras?|featurettes?|proofs?)/")) return true;
            string name = Path.GetFileNameWithoutExtension(path);
            if (Regex.IsMatch(name, @"(?ix)(?:^|[\s._\-\(\[])(sample|trailer|teaser|promo|clip|demo\s*disc)(?:$|[\s._\-\)\]])")) return true;
            if (name.StartsWith("._", StringComparison.Ordinal)) return true; // file nascosti di macOS
        }
        catch { }
        return false;
    }

    private static readonly string[] PhotoExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic" };
    public static bool IsPhotoPath(string path) => PhotoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool WantedFile(string path, string category) => category switch
    {
        "Photos" => IsPhotoPath(path),
        "Music" => MediaFileKinds.IsAudioExtension(Path.GetExtension(path).ToLowerInvariant()),
        _ => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()),
    };

    // Pacchetti di macOS: per il Finder sono un file solo (app, librerie di Foto/Musica, progetti).
    private static readonly string[] PackageExtensions = { ".app", ".bundle", ".framework", ".plugin", ".kext", ".xpc", ".appex",
        ".photoslibrary", ".musiclibrary", ".tvlibrary", ".imovielibrary", ".fcpbundle", ".xcodeproj", ".xcworkspace", ".pkg", ".lproj" };

    /// <summary>
    /// Tutti i file voluti sotto la cartella. Come Spotlight e la ricerca del Finder non entra nelle cartelle che sono
    /// collegamenti (i collegamenti a singoli file valgono): nei prefissi di Wine dosdevices/z: punta a "/" e
    /// drive_c/users/* alla home, e la scansione di ~/Downloads (con dentro il progetto) girava all'infinito a ogni avvio.
    /// </summary>
    private static bool WithinFolder(string path, string folder) => PathText.Within(path, folder);

    public static bool OnVolume(string root, string volume) =>
        string.Equals(PathText.Trim(root), PathText.Trim(volume), PathText.Comparison) ||
        WithinFolder(root, volume) || WithinFolder(volume, root);

    private static IEnumerable<string> EnumerateFiles(string root, string category, List<string> unavailable, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            if (ct.IsCancellationRequested) yield break;
            string dir = pending.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unavailable.Add(dir);
                continue;
            }
            foreach (var f in files)
                if (WantedFile(f, category)) yield return f;
            foreach (var d in dirs)
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith('.') || PackageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant())) continue;
                try { if (new DirectoryInfo(d).LinkTarget != null) continue; }
                catch { continue; }
                pending.Push(d);
            }
        }
    }

    /// <summary>ScanCategory: tutti i file delle cartelle della categoria nell'indice.</summary>
    public static Task ScanAsync(string category, CancellationToken ct = default, string? volume = null,
        Action? indexUpdated = null) => Task.Run(async () =>
    {
        await ScanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = new HashSet<string>(StringComparer.Ordinal);
            var unavailable = new List<string>();
            foreach (var root in GetRoots(category))
            {
                if (volume != null && !OnVolume(root, volume))
                {
                    unavailable.Add(root);
                    continue;
                }
                foreach (var f in EnumerateFiles(root, category, unavailable, ct))
                    if (!ShouldIgnore(f) && BelongsTo(f, category)) files.Add(f);
            }
            ct.ThrowIfCancellationRequested();
            var index = Load<IndexModel>(IndexPath) ?? new IndexModel();
            var roots = GetRoots(category);
            if (index.Categories.TryGetValue(category, out var previous))
                foreach (string path in previous)
                    if (unavailable.Any(dir => WithinFolder(path, dir))) files.Add(path);
            var indexedFiles = files.Where(p => roots.Any(root => WithinFolder(p, root))).ToList();
            index.Categories[category] = indexedFiles;
            Save(IndexPath, index);
            indexUpdated?.Invoke();
            if (category == "TV Series")
                QueueIntroOutroScans(indexedFiles.Where(p => !unavailable.Any(dir => WithinFolder(p, dir))), ct);
        }
        finally { ScanGate.Release(); }
        if (category == "Music")
        {
            await MusicMetadataGate.WaitAsync(ct).ConfigureAwait(false);
            try { ScanMusicMetadata(ReadIndexPaths(category), GetRoots(category), ct); }
            finally { MusicMetadataGate.Release(); }
        }
    }, ct);

    private static void QueueIntroOutroScans(IEnumerable<string> files, CancellationToken ct)
    {
        var seasons = files
            .Where(path => !Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Info: MovieMetadataService.ExtractMediaTitleInfoFromPath(path)))
            .Where(entry => entry.Info is { SeriesTitle.Length: > 0, SeasonNumber: not null, EpisodeNumber: not null })
            .GroupBy(entry => (Folder: Path.GetDirectoryName(entry.Path),
                Series: entry.Info.SeriesTitle!.ToUpperInvariant(), entry.Info.SeasonNumber));

        foreach (var season in seasons)
        {
            if (ct.IsCancellationRequested) break;
            var episodes = season.OrderBy(entry => entry.Info.EpisodeNumber).ToArray();
            if (episodes.Length < 2) continue;
            IntroOutroScanService.QueueSeasonScan(episodes[0].Info.SeriesTitle, season.Key.SeasonNumber,
                episodes.Select(entry => entry.Path));
        }
    }

    private static List<string> ReadIndexPaths(string category)
    {
        var index = Load<IndexModel>(IndexPath);
        return index != null && index.Categories.TryGetValue(category, out var paths) ? paths : new();
    }

    public static List<string> IndexedFiles(string category)
    {
        // Dischi di rete e rclone (FUSE): niente controllo file per file a ogni apertura (ogni domanda va in rete);
        // li verifica una volta per sessione un giro in sottofondo, come Windows (StartMissingRemoteSweep).
        var all = ReadIndexPaths(category).Where(p => !HiddenLibraryStore.Contains(p) && !MissingRemote.ContainsKey(p)).ToList();
        var remote = all.Where(MacStorage.IsSlowMount).ToHashSet(StringComparer.Ordinal);
        if (remote.Count > 0) StartMissingRemoteSweep(category, remote.ToList());
        var present = ExistingFiles(all.Where(p => !remote.Contains(p)));
        return all.Where(p => remote.Contains(p) || present.Contains(p)).ToList();
    }

    /// <summary>
    /// [2026-11-22] Quali file esistono ancora, una cartella alla volta: chi ha molti file nella stessa cartella (le foto,
    /// gli album) la elenca una volta sola invece di chiedere ogni file. 1885 foto in OneDrive: ~130 ms a ogni Reload
    /// (cambio di sezione, chiusura delle Impostazioni), sul thread dell'interfaccia.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ListedWriteTimes = new(StringComparer.Ordinal);

    private static DateTime WriteTimeUtc(string path)
    {
        if (ListedWriteTimes.TryGetValue(path, out var t)) return t;
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    private static HashSet<string> ExistingFiles(IEnumerable<string> paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var present = new HashSet<string>(comparer);
        foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(p) ?? "", comparer))
        {
            var files = group.ToList();
            if (files.Count >= 6 && group.Key.Length > 0)
            {
                try
                {
                    // Per nome: i percorsi dell'indice possono avere separatori diversi da quelli dell'elenco.
                    // Con la data di modifica, che l'elenco della cartella porta gia' con se': Build non la richiede
                    // file per file (1885 foto in OneDrive: 158 ms alla prima apertura di Foto, ora pochi ms).
                    var listed = new Dictionary<string, DateTime>(comparer);
                    foreach (var fi in new DirectoryInfo(group.Key).EnumerateFiles()) listed[fi.Name] = fi.LastWriteTimeUtc;
                    foreach (var f in files)
                        if (listed.TryGetValue(Path.GetFileName(f), out var written)) { present.Add(f); ListedWriteTimes[f] = written; }
                    continue;
                }
                catch { } // cartella sparita o illeggibile: si controlla file per file
            }
            foreach (var f in files) if (File.Exists(f)) present.Add(f);
        }
        return present;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MissingRemote = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RemoteSwept = new(StringComparer.Ordinal);

    /// <summary>File spariti da un disco di rete o rclone: la libreria si ricarica senza di loro.</summary>
    public static event Action? RemoteFilesMissing;

    private static void StartMissingRemoteSweep(string category, List<string> paths)
    {
        if (!RemoteSwept.TryAdd(category, 0)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false); // dopo l'avvio, a interfaccia libera
            int missing = 0;
            foreach (string path in paths)
            {
                // Disco smontato o scollegato: il file non e' "sparito", resta (il montaggio torna locale e non e' piu' lento).
                if (!MacStorage.IsSlowMount(path)) continue;
                try { if (!File.Exists(path) && MissingRemote.TryAdd(path, 0)) missing++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                await Task.Delay(15).ConfigureAwait(false);
            }
            if (missing > 0)
            {
                Console.Error.WriteLine($"[libreria] {missing} file non piu' presenti sui dischi remoti: tolti dalla libreria");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RemoteFilesMissing?.Invoke());
            }
        });
    }

    // ------------------------------------------------------------------ voci
    private static readonly Dictionary<string, LibraryItem> Cache = new(StringComparer.Ordinal);

    private static Task? _prewarm;

    /// <summary>
    /// Avvio: legge in sottofondo le voci dei film (date dei file e metadati salvati: su un disco di rete o rclone sono
    /// centinaia di millisecondi) mentre la finestra si prepara. La prima Items() aspetta solo cio' che manca.
    /// </summary>
    public static void Prewarm(string category = "Movies") => _prewarm = Task.Run(() =>
    {
        try { Items(category, fromPrewarm: true); }
        catch (Exception ex) { Console.Error.WriteLine("[libreria] lettura anticipata non riuscita: " + ex.Message); }
        try { MusicListeningStore.Preload(); } catch { }
    });

    public static List<LibraryItem> Items(string category) => Items(category, fromPrewarm: false);

    private static List<LibraryItem> Items(string category, bool fromPrewarm)
    {
        if (!fromPrewarm && _prewarm is { } warm) { _prewarm = null; try { warm.Wait(); } catch { } }
        var result = new List<LibraryItem>();
        foreach (var path in IndexedFiles(category))
        {
            if (category == "Music") { result.Add(MusicItem(path)); continue; }
            if (!Cache.TryGetValue(path, out var item)) Cache[path] = item = Build(path, category);
            result.Add(item);
        }
        return result;
    }

    public static LibraryItem? ResumeItem(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path))
            return null;
        return Build(path, IsTvEpisodePath(path) ? "TV Series" : "Movies");
    }

    private static LibraryItem Build(string path, string category)
    {
        // Foto: la foto e' la sua copertina; niente titoli TMDb.
        if (category == "Photos")
        {
            DateTime taken = WriteTimeUtc(path);
            return new LibraryItem { Path = path, Title = Path.GetFileNameWithoutExtension(path), Category = "Photos", SortDateUtc = taken, ArtPath = path, WideArtPath = path };
        }
        string title = Path.GetFileNameWithoutExtension(path);
        int? year = null;
        MovieMetadataService.MediaTitleInfo? info = null;
        try
        {
            info = MovieMetadataService.ExtractMediaTitleInfoFromPath(path);
            if (!string.IsNullOrWhiteSpace(info.NormalizedTitle)) title = info.NormalizedTitle;
            year = info.Year;
        }
        catch { }
        try { string best = MovieMetadataService.GetBestKnownDisplayTitle(path); if (!string.IsNullOrWhiteSpace(best)) title = best; } catch { }
        try { year = MovieMetadataService.GetCachedYear(path) ?? year; } catch { }
        DateTime date = WriteTimeUtc(path);
        var item = new LibraryItem
        {
            Path = path,
            Title = Regex.Replace(title.Trim(), @"\s+", " "),
            Category = category,
            Year = year,
            SortDateUtc = date,
            SeriesTitle = info?.SeriesTitle,
            SeasonNumber = info?.SeasonNumber,
            EpisodeNumber = info?.EpisodeNumber,
        };
        RefreshCached(item);
        return item;
    }

    private static void RefreshCached(LibraryItem item)
    {
        try { var p = MovieMetadataService.GetCachedPosterPath(item.Path); if (MovieMetadataService.IsUsablePosterImage(p)) item.ArtPath = p; } catch { }
        try { var b = MovieMetadataService.GetCachedBackdropPath(item.Path); if (!string.IsNullOrWhiteSpace(b) && File.Exists(b)) item.WideArtPath = b; } catch { }
        try
        {
            var rich = MovieMetadataService.TryGetSavedRichMetadata(item.Path, AppLanguage.TmdbLocale(L10n.Language));
            if (rich != null) ApplyRichMetadata(item, rich);
        }
        catch { }
    }

    private static void ApplyRichMetadata(LibraryItem item, MovieMetadataService.RichMetadata rich)
    {
        if (!string.IsNullOrWhiteSpace(rich.Overview)) item.Overview = rich.Overview.Trim();
        if (!string.IsNullOrWhiteSpace(rich.Director)) item.Director = rich.Director;
        if (rich.TmdbId is int tmdb && tmdb > 0) item.TmdbId = tmdb;
        if (rich.Genres.Count > 0) item.Genres = rich.Genres.ToList();
        if (rich.CastMembers.Count > 0)
            item.Cast = rich.CastMembers.Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => (c.Name, c.Character, c.ProfilePath)).ToList();
        else if (item.Cast.Count == 0 && rich.Cast.Count > 0)
            item.Cast = rich.Cast.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => (c, "", (string?)null)).ToList();
    }

    // ------------------------------------------------------------------ TMDb in sottofondo (QueueTmdbArtworkResolve)
    private static readonly Dictionary<string, CancellationTokenSource> Requests = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> RetryAfter = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    public static void RefreshMetadataRequests()
    {
        lock (Sync)
        {
            foreach (var pending in Requests.Values) pending.Cancel();
            Requests.Clear(); RetryAfter.Clear();
        }
    }

    /// <summary>
    /// Lingua dell'app cambiata (2026-11-22): trama, generi, cast e regista tornano a leggersi dalla cache di TMDb nella
    /// lingua nuova (una per lingua su disco, si scarica solo quello che manca). I titoli si aggiornano da soli
    /// (MovieMetadataService chiede solo il titolo tradotto e tiene copertine e sfondi). Le correzioni a mano (titolo,
    /// anno, copertina) non si toccano: vincono sempre.
    /// </summary>
    /// <summary>Prove: percorso e titolo degli elementi che contengono il testo.</summary>
    internal static IEnumerable<string> QaTitles(string filter) =>
        Cache.Values.Where(i => i.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) || i.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"{i.Title} | {i.Year} | {System.IO.Path.GetFileName(i.Path)} | trama: {(i.Overview ?? "").Substring(0, Math.Min(60, (i.Overview ?? "").Length))}").ToList();

    public static void LanguageChanged()
    {
        lock (Sync)
        {
            foreach (var pending in Requests.Values) pending.Cancel();
            Requests.Clear();
            RetryAfter.Clear();
        }
        string locale = AppLanguage.TmdbLocale(L10n.Language);
        foreach (var item in Cache.Values)
        {
            if (item.Category is "Photos" or "Music") continue;
            item.Overview = null; item.Director = null;
            item.Genres = new(); item.Cast = new();
            try
            {
                if (MovieMetadataService.TryGetSavedRichMetadata(item.Path, locale) is { } rich) ApplyRichMetadata(item, rich);
            }
            catch { }
        }
        Changed?.Invoke();
        RefreshTitlesForLanguage();
    }

    private static CancellationTokenSource? _titleSweep;

    /// <summary>
    /// Titoli nella lingua dell'app per tutta la libreria, non solo per le locandine a schermo (2026-11-22): dopo un
    /// cambio di lingua, e una volta all'avvio per quelli rimasti in un'altra lingua. Due alla volta, in sottofondo;
    /// solo il titolo (copertine e sfondi restano), mai sopra una correzione a mano. Un nuovo cambio annulla il giro.
    /// </summary>
    public static void RefreshTitlesForLanguage(int delayMs = 0)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _titleSweep, cts)?.Cancel();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, token);
                if (!MovieMetadataService.HasCustomTmdbApiKey) return;
                List<LibraryItem> items;
                items = Cache.Values.Where(i => !i.IsNetwork && i.Category is "Movies" or "TV Series" or "Videos").ToList();
                using var gate = new SemaphoreSlim(2);
                var work = items.Select(async item =>
                {
                    await gate.WaitAsync(token);
                    try
                    {
                        if (MovieMetadataService.TryGetManualTitle(item.Path, out _, out _) || MovieMetadataService.IsCachedTitleResolved(item.Path)) return;
                        if (!MovieMetadataService.IsUsablePosterImage(item.ArtPath)) return; // senza copertina ci pensa la coda normale
                        var r = MovieMetadataService.ResolveTitleAndPoster(item.Path, null, token);
                        if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(r.normalizedTitle)) return;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            item.Title = r.normalizedTitle!;
                            if (r.year.HasValue) item.Year = r.year;
                            Changed?.Invoke();
                        });
                    }
                    catch { }
                    finally { gate.Release(); }
                }).ToList();
                await Task.WhenAll(work);
            }
            catch { }
        });
    }

    public static void InvalidateMetadata(string path)
    {
        lock (Sync)
        {
            if (Requests.Remove(path, out var pending)) pending.Cancel();
            RetryAfter.Remove(path + "|b"); RetryAfter.Remove(path + "|p");
        }
        Cache.Remove(path);
        Changed?.Invoke();
    }

    /// <summary>Quattro titoli insieme; una sola richiesta per file. Il disegno non legge dischi o servizi TMDb.</summary>
    private static readonly HashSet<string> SharpnessQueued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim SharpnessGate = new(1);

    /// <summary>
    /// Sfondo scaricato col metodo vecchio: si controlla una volta se e' una foto ingrandita (impastata) e in quel caso
    /// si cerca uno sfondo nitido dello stesso titolo. Quello vecchio resta a schermo finche' arriva il nuovo.
    /// </summary>
    private static void QueueSharpnessCheck(LibraryItem item)
    {
        string? file = item.WideArtPath;
        if (file == null || !MovieMetadataService.BackdropNeedsSharpnessCheck(file)) return;
        lock (Sync) { if (!SharpnessQueued.Add(file)) return; }
        string lookup = item.IsNetwork ? (item.Year.HasValue ? $"{item.Title} ({item.Year})" : item.Title) : item.Path;
        _ = Task.Run(async () =>
        {
            await SharpnessGate.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                string? fresh = MovieMetadataService.UpgradeBlurryBackdrop(lookup, file, cts.Token);
                if (!string.IsNullOrWhiteSpace(fresh) && !string.Equals(fresh, file, StringComparison.OrdinalIgnoreCase))
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (string.Equals(item.WideArtPath, file, StringComparison.OrdinalIgnoreCase)) { item.WideArtPath = fresh; Changed?.Invoke(); }
                    });
            }
            catch { }
            finally { SharpnessGate.Release(); }
        });
    }

    public static void QueueArtwork(LibraryItem item, bool includeBackdrop)
    {
        if (includeBackdrop && item.WideArtPath != null) QueueSharpnessCheck(item);
        if (item.IsNetwork) { QueueNetworkArtwork(item, includeBackdrop); return; }
        string key = item.Path + (includeBackdrop ? "|b" : "|p");
        CancellationTokenSource cts;
        lock (Sync)
        {
            if (RetryAfter.TryGetValue(key, out var after) && after > DateTime.UtcNow) return;
            if (Requests.Count >= 4 || Requests.ContainsKey(item.Path)) return;
            cts = new CancellationTokenSource(TimeSpan.FromSeconds(28));
            Requests[item.Path] = cts;
        }
        void Apply(Action update) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cts.IsCancellationRequested) return;
            update(); Changed?.Invoke();
        });
        _ = Task.Run(() =>
        {
            bool ok = true;
            try
            {
                if (!MovieMetadataService.HasCustomTmdbApiKey) return;
                bool needsPoster = !MovieMetadataService.IsUsablePosterImage(item.ArtPath);
                bool needsTitle = !MovieMetadataService.TryGetManualTitle(item.Path, out _, out _) && !MovieMetadataService.IsCachedTitleResolved(item.Path);
                if (needsPoster || needsTitle)
                {
                    var r = MovieMetadataService.ResolveTitleAndPoster(item.Path, null, cts.Token);
                    bool posterOk = MovieMetadataService.IsUsablePosterImage(r.localPosterPath);
                    if (!posterOk) ok = false;
                    Apply(() =>
                    {
                        if (posterOk) item.ArtPath = r.localPosterPath;
                        if (!string.IsNullOrWhiteSpace(r.normalizedTitle)) item.Title = r.normalizedTitle!;
                        if (r.year.HasValue) item.Year = r.year;
                    });
                }
                if (includeBackdrop && string.IsNullOrWhiteSpace(item.WideArtPath))
                {
                    var r = MovieMetadataService.ResolveTitleAndBackdrop(item.Path, null, cts.Token);
                    if (!string.IsNullOrWhiteSpace(r.localBackdropPath) && File.Exists(r.localBackdropPath)) Apply(() => item.WideArtPath = r.localBackdropPath);
                    else ok = false;
                }
                if (includeBackdrop && (string.IsNullOrWhiteSpace(item.Overview) || item.Cast.Count == 0))
                {
                    var rich = MovieMetadataService.ResolveRichMetadata(item.Path, null, AppLanguage.TmdbLocale(L10n.Language), cts.Token, includeReviews: false);
                    Apply(() => ApplyRichMetadata(item, rich));
                }
            }
            catch (OperationCanceledException) { ok = false; }
            catch (Exception) { ok = false; Console.Error.WriteLine("TMDb: caricamento non riuscito"); }
            finally
            {
                lock (Sync)
                {
                    if (Requests.TryGetValue(item.Path, out var current) && ReferenceEquals(current, cts))
                    {
                        Requests.Remove(item.Path);
                        RetryAfter[key] = DateTime.UtcNow.AddSeconds(ok ? 30 : 60);
                    }
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());
            }
        });
    }

    /// <summary>
    /// Titoli del server senza immagini (Jellyfin non ha locandina ne' sfondo): come su Windows le si chiede a TMDb
    /// con titolo e anno del server. Il titolo di Jellyfin non si cambia; le correzioni manuali hanno la precedenza.
    /// </summary>
    private static void QueueNetworkArtwork(LibraryItem item, bool includeBackdrop)
    {
        if (item.Category != "Movies" || MovieMetadataService.HasManualCorrection(item.Path)) return;
        bool needsPoster = item.ArtPath == null, needsBackdrop = includeBackdrop && item.WideArtPath == null;
        if (!needsPoster && !needsBackdrop) return;
        string lookup = item.Year.HasValue ? $"{item.Title} ({item.Year})" : item.Title;
        string key = "net:" + lookup.ToUpperInvariant() + (needsBackdrop ? "|b" : "|p");
        CancellationTokenSource cts;
        lock (Sync)
        {
            if (RetryAfter.TryGetValue(key, out var after) && after > DateTime.UtcNow) return;
            if (Requests.Count >= 4 || Requests.ContainsKey(key)) return;
            cts = new CancellationTokenSource(TimeSpan.FromSeconds(28));
            Requests[key] = cts;
        }
        _ = Task.Run(() =>
        {
            bool ok = true;
            try
            {
                if (!MovieMetadataService.HasCustomTmdbApiKey) { ok = false; return; }
                if (needsPoster)
                {
                    var r = MovieMetadataService.ResolveTitleAndPoster(lookup, null, cts.Token);
                    if (MovieMetadataService.IsUsablePosterImage(r.localPosterPath))
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => { item.ArtPath ??= r.localPosterPath; item.WideArtPath ??= r.localPosterPath; Changed?.Invoke(); });
                    else ok = false;
                }
                if (needsBackdrop)
                {
                    var r = MovieMetadataService.ResolveTitleAndBackdrop(lookup, null, cts.Token);
                    if (!string.IsNullOrWhiteSpace(r.localBackdropPath) && File.Exists(r.localBackdropPath))
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (item.WideArtPath == null || item.WideArtPath == item.ArtPath) item.WideArtPath = r.localBackdropPath; Changed?.Invoke(); });
                }
            }
            catch (OperationCanceledException) { ok = false; }
            catch (Exception) { ok = false; Console.Error.WriteLine("TMDb: caricamento non riuscito"); }
            finally
            {
                lock (Sync)
                {
                    if (Requests.TryGetValue(key, out var current) && ReferenceEquals(current, cts)) Requests.Remove(key);
                    // Senza chiave o senza risultato non si riprova a ogni fotogramma.
                    RetryAfter[key] = DateTime.UtcNow.AddSeconds(ok ? 30 : 120);
                }
            }
        });
    }
}
