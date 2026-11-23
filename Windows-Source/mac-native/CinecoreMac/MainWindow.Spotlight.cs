using System;
using System.Linq;
using Avalonia;

namespace CinecoreMac;

// Spotlight sopra la libreria: si apre da Altro › Spotlight (o all'avvio, se scelto nelle impostazioni).
public sealed partial class MainWindow
{
    private void WireSpotlight()
    {
        _library.SpotlightRequested += OpenSpotlight;
        _spotlight.CloseRequested += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _spotlight.PlayRequested += (item, resumeAt) =>
        {
            _spotlight.Close();
            _videoQueue.Clear(); _library.PauseMusic(); ShowLibrary(false);
            StartVideo(item.Path, item.Title);
            _spotlightReturnPath = item.Path;
            _resumeOnReady = resumeAt > 1 ? resumeAt : null;
        };
        // "In coda": parte dopo il titolo in corso (o subito dopo il prossimo scelto).
        // Tasto Impostazioni in alto: lo stesso menu del tasto Impostazioni della Home (tema, cartelle, impostazioni...).
        _spotlight.SettingsRequested += () =>
        {
            var gear = _spotlight.SettingsButtonBounds;
            ShowTextMenu(_spotlight, _library.OptionsMenuEntries(), new Point(gear.Center.X + 10, gear.Bottom + 10), alignRight: true);
        };
        // Livelli sopra Spotlight: il vetro vero della ricerca (finestra figlia) si toglie quando la coprono.
        _spotlight.GlassBlocked = rect => _settings.IsOpen || _metadata.IsVisible || _report.IsVisible || _hdr.IsVisible || _components.IsVisible ||
            _web.IsVisible || _wled.IsVisible || _goTo.IsVisible || _youtube.IsVisible || _audioSync.IsVisible || _trakt.IsVisible || _scrobble.IsVisible || _update.IsVisible ||
            _photoEdit.IsVisible || _photoViewer.IsVisible || _pairing.IsVisible || _splash.IsVisible || (_menu.IsOpen && !_menu.HasNativeGlass && _menu.PanelBounds.Any(p => p.Inflate(24).Intersects(rect)));
        _spotlight.QueueRequested += item => { if (_videoQueue.All(q => q.Path != item.Path)) _videoQueue.Add(item); };
        // Tasto destro: lo stesso menu del tasto destro della libreria (prima si bloccava; non un menu nuovo).
        _spotlight.ContextMenuRequested += (item, at) =>
        {
            if (_menu.IsOpen || _textMenu.IsOpen) return;
            var origin = _spotlight.TranslatePoint(default, _menu) ?? default;
            _menu.Open(BuildMenu(), at + origin, null, legacyGlass: false, captureFallback: CaptureLibraryBackground);
        };
        _spotlight.SourceRequested += network =>
        {
            MacSettings.Set("librarySource", network ? "DLNA" : "Library");
            // L'interruttore di Spotlight cambia anche la sua fonte salvata (come Windows).
            MacSettings.Set("spotlightSource", network ? "DLNA" : "Library");
            if (network && NetworkStore.Servers.All(s => s.Key != MacSettings.Get("libraryServer", "")) && NetworkStore.Servers.FirstOrDefault(NetworkStore.IsMine) is { } first)
                MacSettings.Set("libraryServer", first.Key);
            _library.SourceChangedFromSettings();
            _spotlight.Replace(_library.SpotlightItems(), _library.SpotlightUsesNetwork);
        };
    }

    private string? _spotlightReturnPath;

    private void OpenSpotlight() => OpenSpotlight(null);

    private void OpenSpotlight(string? selectPath)
    {
        if (_menu.IsOpen) _menu.Close();
        ShowLibrary(true);
        _spotlight.Open(_library.SpotlightItems(), _library.SpotlightUsesNetwork);
        if (selectPath != null) _spotlight.SelectPath(selectPath);
    }
}
