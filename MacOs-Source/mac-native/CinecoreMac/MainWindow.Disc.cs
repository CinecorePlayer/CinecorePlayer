using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Dischi (PlayerForm.Disc.cs di Windows): Apri disco (dischi inseriti, cartella del disco, immagine ISO), Titoli del disco,
/// Espelli. Le due vie stanno in MacDisc: MPV (libbluray / VOB uniti) e madVR o MPC-VR (LAV Splitter dal VideoHost).
/// </summary>
public sealed partial class MainWindow
{
    private string? _discTitlesFor;
    private List<MacDisc.Title>? _discTitles;

    private MenuEntry OpenDiscMenu()
    {
        var root = new MenuEntry("Apri disco", "disc") { Children = new() };
        foreach (var (label, path) in MacDisc.Inserted())
            root.Children.Add(new MenuEntry(label, "disc", () => StartVideo(path)));
        foreach (var cd in MacAudioCd.Inserted())
            root.Children.Add(new MenuEntry("CD audio · " + MacDisc.LabelAsTitle(Path.GetFileName(cd)), "music", () => OpenAudioCd(cd)));
        if (root.Children.Count > 0) root.Children.Add(MenuEntry.Sep());
        root.Children.Add(new MenuEntry("Cartella del disco…", "folder", () => _ = ChooseDiscFolderAsync()));
        root.Children.Add(new MenuEntry("Immagine del disco (ISO)…", "folder", () => _ = ChooseDiscImageAsync()));
        return root;
    }

    private async Task ChooseDiscFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Cartella del disco (con BDMV o VIDEO_TS)", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (MacDisc.TryResolve(path, out _)) StartVideo(path);
        else _osd.Show("Nella cartella non c'è un disco (BDMV o VIDEO_TS)", 3000);
    }

    private async Task ChooseDiscImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Immagine del disco", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Immagine ISO") { Patterns = new[] { "*.iso" } } },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _osd.Show("Apro il disco…", 2000);
        // Il montaggio (hdiutil) richiede qualche secondo: fuori dal thread dell'interfaccia.
        bool ok = await Task.Run(() => MacDisc.TryResolve(path, out _));
        if (ok) StartVideo(path); else _osd.Show("L'immagine non contiene un DVD o un Blu-ray", 3000);
    }

    /// <summary>Titoli del disco in uso: si leggono una volta in sottofondo (le durate vengono da ffprobe).</summary>
    private MenuEntry? DiscTitlesMenu()
    {
        if (_file is not { } file || !MacDisc.TryResolve(file, out var disc)) return null;
        if (_discTitlesFor != file)
        {
            _discTitlesFor = file; _discTitles = null;
            _ = Task.Run(() => { var titles = MacDisc.Titles(disc); Dispatcher.UIThread.Post(() => { if (_discTitlesFor == file) _discTitles = titles; }); });
        }
        var root = new MenuEntry("Titoli del disco", "chapters") { Children = new() };
        if (_discTitles == null) { root.Children.Add(new MenuEntry("Leggo i titoli…") { Enabled = false }); return root; }
        string? chosen = MacDisc.ChosenTitle(disc);
        double longest = _discTitles.Count > 0 ? _discTitles.Max(t => t.Seconds) : 0;
        foreach (var title in _discTitles)
        {
            var t = title;
            bool main = chosen == null && t.Seconds == longest;
            string time = TimeSpan.FromSeconds(t.Seconds).ToString(t.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
            root.Children.Add(new MenuEntry(t.Label + (main ? "  (principale)" : ""), null, () =>
            {
                MacDisc.ChooseTitle(disc, t.Id);
                StartVideo(file, _titleOverride);
            }) { Checked = chosen == t.Id || main, Value = time });
        }
        return root;
    }

    /// <summary>Menu del DVD (navigatore): principale, titoli, scene, audio, sottotitoli; come il telecomando del lettore.</summary>
    private MenuEntry? DvdMenuEntry()
    {
        if (_video.DvdNav is not { } nav) return null;
        MenuEntry Item(string text, int menu) => new(text, null, () => ShowDvdMenu(nav, menu));
        return new MenuEntry("Menu del DVD", "disc").With(
            Item("Menu principale", MacDvdNav.MenuRoot),
            Item("Menu dei titoli", MacDvdNav.MenuTitle),
            Item("Scene", MacDvdNav.MenuChapters),
            Item("Audio", MacDvdNav.MenuAudio),
            Item("Sottotitoli", MacDvdNav.MenuSubtitle),
            MenuEntry.Sep(),
            new MenuEntry("Riprendi il film", null, () => nav.Menu(MacDvdNav.MenuEscape)) { Enabled = nav.InMenu && _video.DvdStreaming });
    }

    /// <summary>Un menu del disco. Se il film sta su madVR / MPC-VR, il navigatore si porta prima al punto del film
    /// (cosi' "Riprendi" del disco torna li') e i menu ripartono con MPV.</summary>
    private void ShowDvdMenu(MacDvdNav nav, int menu)
    {
        if (_video.DvdFeatureOn != null && _file is { } file)
        {
            nav.Seek(_hud.Position);
            if (!nav.Menu(menu)) { _osd.Show("Il disco non ha questo menu", 2000); return; }
            StartVideo(file, _titleOverride);
            return;
        }
        if (!nav.Menu(menu)) _osd.Show("Il disco non ha questo menu", 2000);
    }

    private void WireDvdHandOff()
    {
        // Menu con MPV, film col motore scelto (madVR / MPC-VR) e ritorno ai menu a fine film.
        _video.DvdFeatureStarted += () => PostVideoEvent(() => { if (_file is { } file) StartVideo(file, _titleOverride); });
        _video.DvdFeatureEnded += () => PostVideoEvent(() =>
        {
            if (_file is not { } file || _video.DvdNav is not { } nav) return;
            nav.Menu(MacDvdNav.MenuRoot);
            StartVideo(file, _titleOverride);
        });
    }

    private void WireDisc()
    {
        _library.DiscOpenRequested += path => { if (MacAudioCd.IsAudioCd(path)) { OpenAudioCd(path); return; } ShowLibrary(false); StartVideo(path); };
        WireDvdHandOff();
        _library.DiscEjectRequested += EjectDisc;
        _settings.KeyDbImportRequested += () => _ = ImportKeyDbAsync();
    }

    /// <summary>Blu-ray protetti: il KEYDB.cfg dell'utente (come "Importa KEYDB.cfg" di Windows), copiato per libaacs.</summary>
    private async Task ImportKeyDbAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "KEYDB.cfg per i Blu-ray protetti", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("KEYDB") { Patterns = new[] { "*.cfg", "KEYDB*" } } },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _osd.Show(MacDisc.ImportKeyDb(path, out string error) ? "Chiavi importate: " + MacDisc.ProtectionStatus() : "Chiavi non importate: " + error, 3600);
        _settings.InvalidateVisual();
    }

    private void EjectDisc(string root)
    {
        if (_file is { } file && MacDisc.TryResolve(file, out var playing) && playing.Root == root) CloseFile();
        _ = Task.Run(() => MacDisc.Eject(root));
        _osd.Show("Espello il disco…", 2000);
    }
}

// Ripresa dei DVD (SaveDiscPosition di Windows): titolo e punto del film, per disco; alla riapertura "Riprendi / Dal menu".
public sealed partial class MainWindow
{
    private string? _dvdResumeOffered;
    private (int Title, double Seconds)? _dvdResumePending;

    /// <summary>Etichetta e misure degli IFO: distinguono un disco dall'altro quanto basta per non riprendere il film sbagliato.</summary>
    private static string DvdIdentity(MacDisc.Source disc)
    {
        try
        {
            long size = new DirectoryInfo(Path.Combine(disc.Root, "VIDEO_TS")).EnumerateFiles("*.IFO", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Sum(f => f.Length);
            return disc.Label + "|" + size.ToString(CultureInfo.InvariantCulture);
        }
        catch { return ""; }
    }

    /// <summary>Alla chiusura del DVD: solo dentro il film (non avvisi o extra brevi) e non a film finito. True se era un DVD.</summary>
    private bool SaveDvdResume(string file, double pos, double dur)
    {
        if (!MacDisc.TryResolve(file, out var disc) || disc.Kind != DiscKind.Dvd) return false;
        if (_video.DvdNav is not { } nav) return true;
        int title = nav.Title;
        string id = DvdIdentity(disc);
        if (id.Length == 0 || title <= 0 || dur < 1200) return true;
        string key = "dvdResume:" + id;
        if (pos > 60 && pos < dur - 180) MacSettings.Set(key, title.ToString(CultureInfo.InvariantCulture) + "|" + pos.ToString("0.0", CultureInfo.InvariantCulture));
        else MacSettings.Set(key, "");
        return true;
    }

    /// <summary>Disco con un punto salvato: appena parte, la scheda "Riprendi / Dal menu" (una volta per apertura).</summary>
    private void OfferDvdResume()
    {
        if (_file is not { } file || _dvdResumeOffered == file || _video.ActiveDvdNav is null) return;
        if (!MacDisc.TryResolve(file, out var disc) || disc.Kind != DiscKind.Dvd) return;
        _dvdResumeOffered = file;
        string saved = MacSettings.Get("dvdResume:" + DvdIdentity(disc), "");
        var parts = saved.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int title) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) return;
        _dvdResumePending = (title, seconds);
        _hud.ShowChoicePrompt("Riprendi il film", $"Eri arrivato a {Fmt(seconds)}. Riprendi da lì o guarda il disco dal menu.",
            ("disc-resume", "Riprendi"), ("disc-menu", "Dal menu"));
        DispatcherTimer.RunOnce(() => { if (_hud.RendererPromptVisible && _dvdResumePending != null) { _hud.HideRendererPrompt(); _dvdResumePending = null; } }, TimeSpan.FromSeconds(30));
    }

    private void OnDiscResumeChosen(string key)
    {
        var pending = _dvdResumePending;
        _dvdResumePending = null;
        if (key != "disc-resume" || pending is not { } p || _video.ActiveDvdNav is not { } nav) return;
        if (!nav.PlayTitleAt(p.Title, p.Seconds)) _osd.Show("Non riesco a riprendere il disco", 2400);
    }
}
