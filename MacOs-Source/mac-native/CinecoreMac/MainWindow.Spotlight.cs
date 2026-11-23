using System;
using System.Linq;

namespace CinecoreMac;

// Spotlight sopra la libreria: si apre da Altro › Spotlight (o all'avvio, se scelto nelle impostazioni).
public sealed partial class MainWindow
{
    private void WireSpotlight()
    {
        _library.SpotlightRequested += () => OpenSpotlight();
        _spotlight.CloseRequested += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _spotlight.PlayRequested += (item, resumeAt) =>
        {
            _spotlightReturnPath = item.Path;
            _spotlight.Close();
            _videoQueue.Clear(); _library.PauseMusic(); ShowLibrary(false);
            StartVideo(item.Path, item.Title);
            _resumeOnReady = resumeAt > 1 ? resumeAt : null;
        };
        // "In coda": parte dopo il titolo in corso (o subito dopo il prossimo scelto).
        // La rotella apre lo stesso menu della Home (prima andava dritta alle Impostazioni).
        _spotlight.SettingsRequested += anchor =>
        {
            var entries = _library.OptionsActions().Select(o => new MenuEntry(o.Label, null, o.Run)).ToList();
            _menu.Open(entries, new Avalonia.Point(Math.Max(0, anchor.Right - 220), anchor.Bottom + 8), null);
        };
        // Livelli sopra Spotlight: il vetro vero della ricerca (finestra figlia) si toglie quando la coprono.
        _spotlight.GlassBlocked = rect => _settings.IsOpen || _metadata.IsVisible || _report.IsVisible || _hdr.IsVisible || _components.IsVisible ||
            _web.IsVisible || _wled.IsVisible || _goTo.IsVisible || _youtube.IsVisible || _audioSync.IsVisible || _trakt.IsVisible || _scrobble.IsVisible || _update.IsVisible ||
            _photoEdit.IsVisible || _photoViewer.IsVisible || _pairing.IsVisible || _splash.IsVisible || (_menu.IsOpen && !_menu.HasNativeGlass && _menu.PanelBounds.Any(p => p.Inflate(24).Intersects(rect)));
        _spotlight.QueueRequested += item => { if (_videoQueue.All(q => q.Path != item.Path)) _videoQueue.Add(item); };
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

    private void OpenSpotlight(string? selectPath = null)
    {
        if (_menu.IsOpen) _menu.Close();
        ShowLibrary(true);
        _spotlight.Open(_library.SpotlightItems(), _library.SpotlightUsesNetwork, selectPath);
    }

    /// <summary>Film partito da Spotlight: alla chiusura si torna a Spotlight sullo stesso titolo, non alla Home.</summary>
    private string? _spotlightReturnPath;

    private bool ReturnToSpotlight()
    {
        if (_spotlightReturnPath is not { } path) return false;
        _spotlightReturnPath = null;
        OpenSpotlight(path);
        return true;
    }
}
