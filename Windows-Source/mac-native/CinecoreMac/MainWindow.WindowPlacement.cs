using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace CinecoreMac;

/// <summary>
/// Misura e posizione della finestra ricordate tra un avvio e l'altro (segnalazione GitHub: "chiudendo la finestra
/// la misura non resta, il player riparte sempre grande"). Si ricorda solo la finestra normale: mini player, PiP,
/// schermo intero ed esteso non contano (lo schermo intero all'avvio resta la scelta di Impostazioni › Generali).
/// Se lo schermo di allora non c'e' piu' (monitor staccato) si torna alla misura di serie.
/// </summary>
public sealed partial class MainWindow
{
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _normalKnown, _placementRestored;

    /// <summary>Nel costruttore, prima che la finestra compaia: rimette misura e posizione salvate.</summary>
    private void RestoreWindowPlacement()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCREEN") != null) return;
        string saved = MacSettings.Get("windowBounds", "");
        var parts = saved.Split(';');
        if (parts.Length != 4) return;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) return;
        w = Math.Max(WindowMinWidth, w); h = Math.Max(WindowMinHeight, h);
        try
        {
            // La barra del titolo (i primi ~80 punti) deve cadere dentro uno schermo che c'e' ancora, altrimenti la
            // finestra si aprirebbe dove non si puo' prenderla.
            var title = new PixelRect(x + 40, y, (int)Math.Max(1, Math.Min(w - 80, 400)), 40);
            var screen = Screens.All.FirstOrDefault(s => s.WorkingArea.Intersects(title));
            if (screen == null) return;
            var area = screen.WorkingArea;
            double scale = OperatingSystem.IsMacOS() ? 1 : Math.Max(1, screen.Scaling);
            w = Math.Min(w, area.Width / scale); h = Math.Min(h, area.Height / scale);
        }
        catch { return; }
        Width = w; Height = h;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(x, y);
        _normalPosition = Position; _normalSize = new Size(w, h); _normalKnown = true;
        _placementRestored = true;
    }

    /// <summary>All'apertura: era ingrandita (Windows) quando l'app e' stata chiusa.</summary>
    private void RestoreWindowMaximized()
    {
        if (_placementRestored && MacSettings.Get("windowMaximized", false) && !OperatingSystem.IsMacOS()) WindowState = WindowState.Maximized;
    }

    private bool PlacementTracked => WindowState == WindowState.Normal && _beforeMini == null && !_extendedFs && !_closing && IsVisible;

    /// <summary>Su ogni spostamento o cambio di misura: tiene l'ultima geometria della finestra normale.</summary>
    private void TrackWindowPlacement()
    {
        if (!PlacementTracked) return;
        _normalPosition = Position; _normalSize = new Size(Width, Height); _normalKnown = true;
    }

    /// <summary>Alla chiusura, prima di nascondere la finestra.</summary>
    private void SaveWindowPlacement()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) return;
        SaveMiniBounds();
        if (PlacementTracked) TrackWindowPlacement();
        if (!_normalKnown || _normalSize.Width < 100 || _normalSize.Height < 100) return;
        MacSettings.Set("windowBounds", string.Create(CultureInfo.InvariantCulture,
            $"{_normalPosition.X};{_normalPosition.Y};{Math.Round(_normalSize.Width)};{Math.Round(_normalSize.Height)}"));
        MacSettings.Set("windowMaximized", WindowState == WindowState.Maximized);
    }
}
