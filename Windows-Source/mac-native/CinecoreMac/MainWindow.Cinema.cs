using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Prima del film (modalita' cinema di Windows): la schermata con lo sfondo del film prima di aprirlo e in pausa, e la
/// demo prima del film. Il film si apre solo con "Avvia film": finche' la schermata e' a schermo non c'e' video sotto.
/// </summary>
public sealed partial class MainWindow
{
    private readonly CinemaGateView _gate = new(gate: true);
    private readonly CinemaGateView _pauseScreen = new(gate: false);
    private (string File, string? Title)? _gatePending, _afterDemo;
    private bool _gateBypass;
    private LibraryItem? _gateItem;

    private static string PlaceholderFolder => Path.Combine(LibraryStore.AppDataDir, "PausePlaceholders");
    private static string DemoFolder => Path.Combine(LibraryStore.AppDataDir, "Demos");
    private static bool CinemaGateEnabled { get => MacSettings.Get("cinemaGate", false); set => MacSettings.Set("cinemaGate", value); }
    private static bool CinemaGateTmdb { get => MacSettings.Get("cinemaGateTmdb", true); set => MacSettings.Set("cinemaGateTmdb", value); }
    private static bool PreRollEnabled { get => MacSettings.Get("preRoll", false); set => MacSettings.Set("preRoll", value); }

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".heic" };

    private static List<string> FolderFiles(string folder, Func<string, bool> accept)
    {
        try { Directory.CreateDirectory(folder); return Directory.EnumerateFiles(folder).Where(accept).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        catch { return new(); }
    }

    private void WireCinema()
    {
        _gate.StartRequested += ConfirmCinemaGate;
        _gate.CancelRequested += CancelCinemaGate;
        // Sfondo arrivato da TMDb mentre la schermata e' gia' a schermo: entra senza richiuderla.
        LibraryStore.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_gate.IsOpen && _gateItem != null) _gate.SetImage(GateImage(_gateItem.Path, _gateItem, queue: false));
        });
    }

    private static LibraryItem? FindLibraryItem(string file) =>
        LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series")).Concat(LibraryStore.Items("Videos"))
            .FirstOrDefault(i => string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase));

    /// <summary>L'immagine scelta nella cartella, altrimenti lo sfondo del film da TMDb (chiesto in sottofondo se manca).</summary>
    private static string? GateImage(string file, LibraryItem? item, bool queue)
    {
        string chosen = MacSettings.Get("cinemaGateImage", "");
        if (chosen.Length > 0 && File.Exists(chosen)) return chosen;
        if (!CinemaGateTmdb) return FolderFiles(PlaceholderFolder, f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).FirstOrDefault();
        string? backdrop = item?.WideArtPath;
        if (string.IsNullOrWhiteSpace(backdrop) || !File.Exists(backdrop))
            try { backdrop = MovieMetadataService.GetCachedBackdropPath(file); } catch { backdrop = null; }
        if ((backdrop == null || !File.Exists(backdrop)) && queue && item != null) LibraryStore.QueueArtwork(item, includeBackdrop: true);
        return backdrop != null && File.Exists(backdrop) ? backdrop : null;
    }

    /// <summary>
    /// All'apertura di un film dalla libreria: la schermata invece del film. La libreria resta sotto com'era (il chiamante
    /// l'aveva appena nascosta, senza ridisegni in mezzo): annullando non si ricompone.
    /// </summary>
    private bool InterceptForCinemaGate(string file, string? title)
    {
        if (_gateBypass) { _gateBypass = false; return false; }
        if (!CinemaGateEnabled || _afterDemo != null || _video.IsRunning) return false;
        if (!LibraryStore.IsVideoPath(file) && !IsStream(file)) return false;
        _gatePending = (file, title);
        _gateItem = IsStream(file) ? null : FindLibraryItem(file);
        _library.IsVisible = true; _hud.IsVisible = false;
        string shownTitle = _gateItem?.Title ?? title ?? Path.GetFileNameWithoutExtension(file);
        string renderer = Environment.GetEnvironmentVariable("CINECORE_RENDERER") ?? MacSettings.EffectiveRenderer;
        string subtitle = renderer.Equals("madvr", StringComparison.OrdinalIgnoreCase) ? "powered by madVR" : "";
        _gate.Show(shownTitle, subtitle, GateImage(file, _gateItem, queue: true));
        return true;
    }

    private void ConfirmCinemaGate()
    {
        if (_gatePending is not { } pending) return;
        double? resume = _resumeOnReady;
        _gatePending = null; _gateItem = null;
        _gate.Hide();
        _library.PauseMusic();
        // Demo prima del film: solo partendo dall'inizio (come Windows).
        if (PreRollEnabled && (resume ?? 0) <= 1 && PickDemo() is { } demo)
        {
            _afterDemo = (pending.File, pending.Title);
            _gateBypass = true;
            ShowLibrary(false);
            StartVideo(demo, "Demo");
            return;
        }
        _gateBypass = true;
        ShowLibrary(false);
        StartVideo(pending.File, pending.Title);
        _resumeOnReady = resume;
    }

    /// <summary>Torna alla libreria: niente era stato aperto, la pagina e' gia' li' sotto. La volta dopo la schermata ricompare.</summary>
    private void CancelCinemaGate()
    {
        _gatePending = null; _gateItem = null; _resumeOnReady = null;
        _gate.Hide();
        _library.Focus();
    }

    private static string? PickDemo()
    {
        var demos = FolderFiles(DemoFolder, f => LibraryStore.IsVideoPath(f));
        string chosen = MacSettings.Get("preRollDemo", "");
        if (chosen.Length > 0 && File.Exists(chosen)) return chosen;
        return demos.Count == 0 ? null : demos[Random.Shared.Next(demos.Count)];
    }

    /// <summary>Fine (o salto) della demo: parte il film che aspettava.</summary>
    private bool StartFilmAfterDemo()
    {
        if (_afterDemo is not { } film) return false;
        _afterDemo = null;
        _gateBypass = true;
        StartVideo(film.File, film.Title);
        return true;
    }

    /// <summary>In pausa: la schermata copre il film (l'HUD resta sopra e funziona).</summary>
    private void UpdatePauseScreen()
    {
        bool show = CinemaGateEnabled && _video.IsRunning && !_hud.IsPlaying && _file != null && _afterDemo == null && LibraryStore.IsVideoPath(_file);
        if (!show) { _pauseScreen.Hide(); return; }
        var item = IsStream(_file!) ? null : FindLibraryItem(_file!);
        _pauseScreen.Show(item?.Title ?? _hud.Title, "In pausa", GateImage(_file!, item, queue: false));
    }

    private MenuEntry BeforeFilmMenu()
    {
        string chosen = MacSettings.Get("cinemaGateImage", "");
        var images = new MenuEntry("Scegli la schermata pre-film", "photo") { Children = new() };
        images.Children.Add(new MenuEntry(CinemaGateTmdb ? "Sfondo del film (TMDb)" : "Prima della cartella", null, () => MacSettings.Set("cinemaGateImage", "")) { Checked = chosen.Length == 0 });
        foreach (string file in FolderFiles(PlaceholderFolder, f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            string path = file;
            images.Children.Add(new MenuEntry(Path.GetFileNameWithoutExtension(file), null, () => MacSettings.Set("cinemaGateImage", path)) { Checked = chosen == file });
        }
        string demoChosen = MacSettings.Get("preRollDemo", "");
        var demos = new MenuEntry("Scegli demo") { Children = new() { new MenuEntry("Casuale", null, () => MacSettings.Set("preRollDemo", "")) { Checked = demoChosen.Length == 0 } } };
        foreach (string file in FolderFiles(DemoFolder, f => LibraryStore.IsVideoPath(f)))
        {
            string path = file;
            demos.Children.Add(new MenuEntry(Path.GetFileNameWithoutExtension(file), null, () => MacSettings.Set("preRollDemo", path)) { Checked = demoChosen == file });
        }
        var preRoll = new MenuEntry("Demo prima del film", "play") { Children = new()
        {
            new MenuEntry("Abilita", null, () => PreRollEnabled = !PreRollEnabled) { Checked = PreRollEnabled },
            demos,
            new MenuEntry("Apri cartella demo…", "folder", () => MacFileActions.OpenFolder(DemoFolder)),
        } };
        return new MenuEntry("Prima del film", "cinema") { Children = new()
        {
            new MenuEntry("Schermata pre-film", "image", () => { CinemaGateEnabled = !CinemaGateEnabled; UpdatePauseScreen(); }) { Checked = CinemaGateEnabled },
            new MenuEntry("Usa lo sfondo automatico di TMDb (film e serie)", "movie", () => CinemaGateTmdb = !CinemaGateTmdb) { Checked = CinemaGateTmdb },
            images,
            new MenuEntry("Apri la cartella delle schermate pre-film…", "folder", () => MacFileActions.OpenFolder(PlaceholderFolder)),
            MenuEntry.Sep(),
            preRoll,
        } };
    }
}
