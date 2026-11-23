using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

internal sealed partial class LibraryView
{
    public event Action<LibraryItem>? MetadataEditRequested;
    // Anche i film del server: la correzione resta su questo Mac (NetworkStore.ApplyManual), Jellyfin non viene modificato.
    private static bool CanEditMetadata(LibraryItem item) => item.Category is "Movies" or "Videos";
    public bool StartupContentReady => !_networkLoading;
    private bool _contextPress;

    /// <summary>Lo stesso menu in vetro del player, anche per locandine e Continua a guardare.</summary>
    /// <summary>Clic destro recuperato dalla finestra (macOS l'ha visto, Avalonia no): stesso menu del clic vero.</summary>
    public void OpenContextMenuAt(Point at) { _contextPress = false; ShowItemContextMenu(at); }

    private void ShowItemContextMenu(Point at)
    {
        if (_menu != null || _signIn != null || _foldersOpen || _playlistCreate != null)
        {
            Console.Error.WriteLine($"[menu] clic destro in libreria ignorato: menu={_menu != null} accesso={_signIn != null} cartelle={_foldersOpen} playlist={_playlistCreate != null}");
            return;
        }
        var hit = HitAt(at);
        if (hit?.Item is not { } item) { if (!SheetOpen) MainMenuRequested?.Invoke(at); return; }
        var entries = new List<MenuEntry>();
        if (hit.Kind == "resume")
        {
            entries.Add(new MenuEntry("Rimuovi da questa riga", "trash", () =>
            {
                // Stessa regola di Windows: la ripresa resta, la scheda sparisce fino alla prossima visione.
                PlaybackResumeStore.Hide(item.Path);
                RefreshResume(); InvalidateVisual();
            }));
        }
        else if (hit.Kind is "poster" or "hero-info" or "detail-rec" or "series-episode")
        {
            var resume = ResumeOf(item);
            entries.Add(new MenuEntry(resume != null ? "Riprendi" : "Riproduci", "play",
                () => PlayRequested?.Invoke(item, ResumeOf(item)?.Position)));
            if (resume != null)
                entries.Add(new MenuEntry("Riproduci dall’inizio", "play", () => PlayRequested?.Invoke(item, null)));
            entries.Add(new MenuEntry("Altre info", "info", () =>
            {
                if (item.Category == "TV Series") OpenSeries(item); else OpenDetail(item);
            }));
            if (CanEditMetadata(item)) entries.Add(new MenuEntry("Correggi titolo e copertina…", "edit", () => MetadataEditRequested?.Invoke(item)));
            entries.Add(MenuEntry.Sep());
            entries.Add(AddToPlaylistMenu(new[] { item }));
            if (_category == "WatchHistory" && _view == "Collection")
                entries.Add(new MenuEntry("Rimuovi dal diario", "trash", () => RemoveFromDiary(item)));
            entries.Add(new MenuEntry(Favorites.Contains(item.Path) ? "Rimuovi dai preferiti" : "Aggiungi ai preferiti", "star", () =>
            {
                Favorites.Toggle(item.Path); Reload();
            }));
            if (!item.IsNetwork) entries.Add(new MenuEntry("Mostra nel Finder", "folder", () => RevealFile(item.Path)));
            // Anche per i titoli di rete (DLNA, Jellyfin, Emby, Plex): si nascondono solo qui, il server non cambia.
            entries.Add(new MenuEntry("Nascondi dalla libreria", "hide", () => HideItem(item, hit.Kind == "poster" && _category == "TV Series")));
        }
        else if (hit.Kind is "album" or "music-resume" && Albums(_items.Where(i => i.Category == "Music")).FirstOrDefault(a => a.Key == hit.Key) is { } album)
        {
            entries.Add(new MenuEntry("Riproduci", "play", () => PlayQueue(album.Tracks.ToList(), 0)));
            entries.Add(new MenuEntry("Aggiungi alla coda", "queue", () => AddToMusicQueue(album.Tracks)));
            entries.Add(new MenuEntry("Altre info", "info", () => OpenAlbum(album.Key)));
            entries.Add(MenuEntry.Sep());
            entries.Add(AddToPlaylistMenu(album.Tracks));
            if (AlbumHideEntry(album) is { } hide) entries.Add(hide);
        }
        else if (hit.Kind == "album-play" && _album != null && int.TryParse(hit.Key, out int track) && track >= 0 && track < _album.Tracks.Count)
        {
            var tracks = _album.Tracks.ToList();
            entries.Add(new MenuEntry("Riproduci da qui", "play", () => PlayQueue(tracks, track)));
            entries.Add(AddToPlaylistMenu(new[] { tracks[track] }));
        }
        if (entries.Count > 0) ShowTextMenu(entries, at);
    }

    /// <summary>
    /// Menu dei titoli come su Windows (ShowLibraryItemMenu → TextMenuOverlay): la stessa lista di testo dei menu in alto,
    /// nel punto del clic, con la pagina velata; non il menu a tendina. Una voce con sottomenu (Aggiungi a playlist)
    /// apre la sua lista al posto della prima.
    /// </summary>
    private void ShowTextMenu(IReadOnlyList<MenuEntry> entries, Point at, string key = "Item", bool above = false, bool showDisabled = false)
    {
        var items = new List<MenuItem>();
        foreach (var entry in entries)
        {
            if (entry.Separator || !entry.Visible || string.IsNullOrEmpty(entry.Text)) continue;
            if (!entry.Enabled)
            {
                if (showDisabled) items.Add(new MenuItem(entry.Text, false, null));
                continue;
            }
            if (entry.Children is { Count: > 0 } children)
                items.Add(new MenuItem(entry.Text + "  ›", entry.Checked, () => Avalonia.Threading.DispatcherTimer.RunOnce(() => ShowTextMenu(children, at, key, above), TimeSpan.FromMilliseconds(180))));
            else if (entry.Run is { } run)
                items.Add(new MenuItem(entry.Text, entry.Checked, run));
        }
        if (items.Count == 0) return;
        double menuTop = BarH + Math.Round(TopShift) + S(12);
        double menuHeight = items.Count * MenuRowH;
        // Sopra una barra in basso (foto): l'elenco finisce sul punto e si allinea a destra del tasto.
        if (above)
        {
            double w = items.Max(i => Measure(i.Label, MenuPt, DisplaySemibold));
            at = new Point(at.X - w, Math.Max(menuTop, at.Y - menuHeight));
        }
        else if (key == "Item")
            at = new Point(at.X, Math.Clamp(at.Y, menuTop, Math.Max(menuTop, Bounds.Height - MusicBarH - S(8) - menuHeight)));
        OpenMenu(key, items, at);
    }

    private static void RevealFile(string path)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("-R"); start.ArgumentList.Add(path);
            Process.Start(start)?.Dispose();
        }
        catch { }
    }

    private void HideItem(LibraryItem item, bool wholeSeries)
    {
        IEnumerable<LibraryItem> series = item.IsNetwork && ActiveServer is { } server && NetworkStore.Catalog(server.Key) is { } catalog
            ? catalog.Where(e => e.Category == "TV Series") : LibraryStore.Items("TV Series");
        var paths = wholeSeries
            ? series.Where(e => string.Equals(SeriesKey(e), SeriesKey(item), StringComparison.CurrentCultureIgnoreCase)).Select(e => e.Path).ToList()
            : new List<string> { item.Path };
        HiddenLibraryStore.Hide(paths, wholeSeries ? SeriesKey(item) : item.Title);
        if (_detail != null && paths.Contains(_detail.Path)) CloseDetail();
        if (_seriesName != null && wholeSeries) CloseSeries();
        Reload();
    }

    private MenuEntry? AlbumHideEntry(Album album)
    {
        var paths = album.Tracks.Select(t => t.Path).ToArray();
        if (paths.Length == 0) return null;
        return new MenuEntry("Nascondi dalla libreria", "hide", () =>
        {
            HiddenLibraryStore.Hide(paths, album.Title);
            if (_album?.Key == album.Key) CloseAlbum();
            Reload();
        });
    }

    private void ShowHiddenItems()
    {
        var groups = HiddenLibraryStore.Groups();
        if (groups.Count == 0) return;
        var entries = new List<MenuEntry>
        {
            new("Rimetti tutti in libreria", "show", () => { HiddenLibraryStore.Show(groups.SelectMany(g => g.Paths)); Reload(); }),
            MenuEntry.Sep(),
        };
        // Il menu resta navigabile anche con molti titoli: gruppi alfabetici se non stanno in un pannello.
        var items = groups.Select(g => new MenuEntry(g.Title, null, () => { HiddenLibraryStore.Show(g.Paths); Reload(); })).ToList();
        int limit = Math.Clamp((int)(Bounds.Height / 34) - 5, 4, 14);
        while (items.Count > limit)
        {
            var branches = new List<MenuEntry>();
            for (int i = 0; i < items.Count; i += limit)
            {
                var chunk = items.Skip(i).Take(limit).ToList();
                branches.Add(new MenuEntry($"{chunk[0].Text} – {chunk[^1].Text}") { Children = chunk });
            }
            items = branches;
        }
        entries.AddRange(items);
        ShowTextMenu(entries, new Point(_optionsAnchor.Right - S(230), _optionsAnchor.Bottom + S(10)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (QueuePointerReleased()) return;
        if (MusicBarPointerReleased(e.GetPosition(this), e.Pointer)) return;
        if (!_contextPress) return;
        _contextPress = false;
        e.Handled = true;
        ShowItemContextMenu(e.GetPosition(this));
    }

    // Apertura ripetibile per le prove, senza muovere il puntatore dell'utente.
    internal void TestItemContextMenu(string kind)
    {
        var hit = _hits.FirstOrDefault(h => h.Kind == kind && h.Item != null);
        if (hit != null) ShowItemContextMenu(hit.Bounds.Center);
    }

    internal string TestTextMenu(string label)
    {
        if (_menu == null) return "chiuso";
        if (label.Length == 0) return string.Join(" | ", _menu.Select(i => i.Label + (i.Run == null ? " (disabilitata)" : "")));
        var choice = _menu.FirstOrDefault(i => i.Label == label);
        if (choice?.Run is not { } run) return "voce indisponibile: " + label;
        CloseMenu(run);
        return "scelta: " + label;
    }

    public void MetadataSaved(string path)
    {
        LibraryStore.InvalidateMetadata(path);
        NetworkStore.ManualChanged(path);
        Reload();
        if (_detail?.Path == path) _detail = _items.FirstOrDefault(i => i.Path == path);
    }

    /// <summary>Prova: titoli del server senza copertina e download di quelle dichiarate (niente segreti nel registro).</summary>
    internal async System.Threading.Tasks.Task TestNetworkArtAsync()
    {
        if (ActiveServer is not { } server || NetworkStore.Catalog(server.Key) is not { } catalog) { Console.Error.WriteLine("[qa] net-art: nessun catalogo"); return; }
        foreach (var group in catalog.GroupBy(i => i.Category))
            Console.Error.WriteLine($"[qa] net-art {group.Key}: {group.Count()} titoli, senza ArtPath {group.Count(i => i.ArtPath == null)}, senza WideArt {group.Count(i => i.WideArtPath == null)}");
        foreach (var item in catalog.Where(i => i.ArtPath == null && i.Category == "Movies").Take(15))
            Console.Error.WriteLine($"[qa] net-art senza copertina: {item.Title}");
        int ok = 0, fail = 0;
        foreach (var item in catalog.Where(i => i.ArtPath != null && i.ArtPath.StartsWith("http")).Take(40))
        {
            var file = await NetworkStore.CachedArtAsync(item.ArtPath!);
            if (file != null) ok++; else { fail++; Console.Error.WriteLine($"[qa] net-art download fallito: {item.Title}"); }
        }
        Console.Error.WriteLine($"[qa] net-art download ok={ok} falliti={fail}");
    }

    internal void TestEditFirst()
    {
        if (_items.FirstOrDefault(CanEditMetadata) is { } item) MetadataEditRequested?.Invoke(item);
    }
}
