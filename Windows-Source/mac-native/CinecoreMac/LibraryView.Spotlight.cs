using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;

namespace CinecoreMac;

// Quello che serve a Spotlight dalla libreria: titoli della fonte in uso, immagini dalla stessa cache, ripresa, scheda.
internal sealed partial class LibraryView
{
    /// <summary>Un'immagine della cache e' pronta (anche Spotlight ridisegna).</summary>
    public static event Action? ImageReady;
    /// <summary>Altro › Spotlight.</summary>
    public event Action? SpotlightRequested;
    public event Action? OpenLinkRequested;
    public event Action<string>? DiscOpenRequested, DiscEjectRequested;
    internal static void RaiseImageReady() => ImageReady?.Invoke();

    public Bitmap? LoadImage(string? path, double widthPoints) => Image(path, widthPoints);
    public (double Position, double Duration, DateTime SavedAt)? ResumeFor(LibraryItem item) => ResumeOf(item);
    public void ShowDetail(LibraryItem item) { if (item.Category == "TV Series") OpenSeries(item); else OpenDetail(item); }
    public bool IsNetworkSource => ActiveServer != null;

    /// <summary>
    /// Film e serie della fonte in uso (una scheda per serie), come Spotlight su Windows: prima i titoli iniziati e da
    /// riprendere, dal piu' recente, poi gli altri dal piu' nuovo.
    /// </summary>
    /// <summary>Fonte di Spotlight (Impostazioni › Spotlight): la libreria del Mac o un server; senza scelta segue la libreria.</summary>
    public bool SpotlightUsesNetwork => MacSettings.Get("spotlightSource", "") switch { "DLNA" => SpotlightServerKey() != null, "Library" => false, _ => IsNetworkSource };

    private string? SpotlightServerKey()
    {
        string key = MacSettings.Get("spotlightServer", "");
        if (NetworkStore.Catalog(key) != null) return key;
        return ActiveServer?.Key ?? NetworkStore.Servers.FirstOrDefault(s => NetworkStore.IsMine(s) && NetworkStore.Catalog(s.Key) != null)?.Key;
    }

    public List<LibraryItem> SpotlightItems()
    {
        IEnumerable<LibraryItem> all;
        string source = MacSettings.Get("spotlightSource", "");
        string? serverKey = source == "DLNA" ? SpotlightServerKey() : source == "Library" ? null : ActiveServer?.Key;
        if (serverKey != null) all = NetworkStore.Catalog(serverKey) ?? (IReadOnlyList<LibraryItem>)new List<LibraryItem>();
        else all = LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series"));
        var movies = all.Where(i => i.Category == "Movies");
        var series = all.Where(i => i.Category == "TV Series")
            .GroupBy(i => (i.SeriesTitle ?? i.Title).Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.OrderBy(e => e.SeasonNumber ?? 0).ThenBy(e => e.EpisodeNumber ?? 0).First());
        var list = movies.Concat(series).ToList();
        var started = list.Where(i => ResumeOf(i) != null).OrderByDescending(i => ResumeOf(i)!.Value.SavedAt).ToList();
        return started.Concat(list.Except(started).OrderByDescending(i => i.SortDateUtc)).ToList();
    }

    /// <summary>Titoli per il telefono: film, episodi e brani della fonte scelta sul telefono (Mac o server in uso).</summary>
    public List<LibraryItem> RemoteItems(bool network)
    {
        if (network && ActiveServer is { } server) return NetworkStore.Catalog(server.Key)?.ToList() ?? new();
        if (network && NetworkStore.Servers.FirstOrDefault(s => NetworkStore.Catalog(s.Key) != null) is { } any) return NetworkStore.Catalog(any.Key)!.ToList();
        return LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series")).Concat(LibraryStore.Items("Music")).ToList();
    }

    /// <summary>Un brano scelto dal telefono: suona con il suo album in coda.</summary>
    public void PlayMusicFromRemote(LibraryItem track)
    {
        var album = Albums(RemoteItems(track.IsNetwork).Where(i => i.Category == "Music")).FirstOrDefault(a => a.Tracks.Any(t => t.Path == track.Path));
        if (album == null) { PlayQueue(new List<LibraryItem> { track }, 0); return; }
        PlayQueue(album.Tracks.ToList(), Math.Max(0, album.Tracks.FindIndex(t => t.Path == track.Path)));
    }

    /// <summary>Lo sfondo del film per la schermata di caricamento (libreria del Mac o catalogo del server).</summary>
    public string? BackdropFor(string path)
    {
        var item = ItemFor(path);
        if (item == null) return null;
        // Un episodio: lo sfondo della serie se l'episodio non ne ha uno suo.
        return item.WideArtPath ?? item.ArtPath;
    }

    /// <summary>Durata gia' nota di un titolo (secondi): libreria del Mac o catalogo del server.</summary>
    public double? DurationFor(string path)
    {
        var item = ItemFor(path);
        return item?.DurationMinutes is > 0 ? item.DurationMinutes * 60 : null;
    }

    /// <summary>Il titolo della libreria del Mac o del catalogo del server con questo percorso.</summary>
    // Il titolo per un percorso, ricordato per 5 s: il timer dei tasti multimediali (ogni secondo) e lo stato del
    // telecomando lo chiedono di continuo durante il film, e per un file fuori dalla libreria caricata ogni volta si
    // rileggevano dal disco gli indici di Film e Serie (25 ms su SSD, molto di piu' su un disco meccanico).
    private readonly Dictionary<string, (LibraryItem? Item, long At)> _itemForCache = new();

    public LibraryItem? ItemFor(string path)
    {
        long now = Environment.TickCount64;
        lock (_itemForCache)
            if (_itemForCache.TryGetValue(path, out var hit) && now - hit.At < 5000) return hit.Item;
        var item = ItemForCore(path);
        lock (_itemForCache)
        {
            if (_itemForCache.Count > 256) _itemForCache.Clear();
            _itemForCache[path] = (item, now);
        }
        return item;
    }

    private LibraryItem? ItemForCore(string path) =>
        _items.FirstOrDefault(i => i.Path == path)
        ?? NetworkStore.Servers.Select(s => NetworkStore.Catalog(s.Key)).Where(c => c != null).SelectMany(c => c!).FirstOrDefault(i => i.Path == path)
        ?? LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series")).FirstOrDefault(i => i.Path == path);
}
