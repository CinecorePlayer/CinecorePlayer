using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Mini player della musica (TogglePipMode di Windows con un brano audio): la finestra diventa piccola, resta sopra
/// le altre app e mostra il brano a schermo intero con la barra compatta. Lo stesso comando la riporta com'era.
/// 2026-11-21: su Windows senza la cornice di sistema (si sposta dai punti vuoti e si ridimensiona dai bordi), piu'
/// grande di prima (420 punti invece di 340) e con misura e posizione scelte dall'utente ricordate ("miniBounds").
/// Da massimizzata o a schermo intero prima torna in finestra, e all'uscita riprende lo stato di prima.
/// </summary>
public sealed partial class MainWindow
{
    private (PixelPoint Position, double Width, double Height, WindowState State)? _beforeMini;

    internal const double WindowMinWidth = 1024, WindowMinHeight = 660;
    private const double MiniDefaultWidth = 420, MiniMinWidth = 300;

    /// <summary>Altezza del contenuto per una larghezza (LayoutMiniPlayer: 30 sopra, copertina al 64%, brano, timeline, comandi, icone).</summary>
    private static double MiniHeightFor(double w) => 30 + Math.Round(w * .64) + 18 + 46 + 10 + 20 + 18 + 44 + 8 + 30 + 16;

    private void WireMiniPlayerChrome()
    {
#if CINECORE_WINDOWS
        LibraryView.MiniChromeless = true;
        _library.MiniPlayerRequested -= ToggleMiniPlayer;
        _library.MiniPlayerRequested += ToggleMiniPlayerAnimated;
        _library.MiniDragRequested += e => { if (_beforeMini != null) BeginMoveDrag(e); };
        _library.MiniResizeRequested += (e, edge) => { if (_beforeMini != null) BeginResizeDrag(edge, e); };
#endif
    }

    private void ToggleMiniPlayer()
    {
        if (_beforeMini is { } before)
        {
            SaveMiniBounds();
            Topmost = false;
#if CINECORE_WINDOWS
            SystemDecorations = SystemDecorations.Full;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
#endif
            // Prima la misura di prima, poi i minimi della finestra normale, e solo alla fine si torna a registrare la
            // misura: prima i minimi alzati sulla finestra ancora piccola la portavano a 1024x660 per un attimo, e quella
            // finiva salvata come misura della finestra (all'avvio il player si apriva minuscolo).
            Width = Math.Max(WindowMinWidth, before.Width); Height = Math.Max(WindowMinHeight, before.Height); Position = before.Position;
            MinWidth = WindowMinWidth; MinHeight = WindowMinHeight;
            _beforeMini = null;
            TrackWindowPlacement();
            _library.SetMiniPlayer(false);
            if (before.State == WindowState.Maximized) WindowState = WindowState.Maximized;
            else if (before.State == WindowState.FullScreen)
                DispatcherTimer.RunOnce(() => { if (_beforeMini == null) WindowState = WindowState.FullScreen; }, TimeSpan.FromMilliseconds(120));
            ApplyWindowButtonsAlpha();
            return;
        }
        var state = WindowState;
#if CINECORE_WINDOWS
        // Windows: da massimizzata o a schermo intero si passa in finestra e si entra subito (prima il primo comando
        // usciva soltanto dallo schermo intero).
        if (state != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
            Dispatcher.UIThread.Post(() => EnterMiniPlayer(state), DispatcherPriority.Background);
            return;
        }
#else
        // Dallo schermo intero prima si esce: una finestra piccola dentro lo Space a tutto schermo non avrebbe senso.
        if (IsFullscreen) { WindowState = WindowState.Normal; return; }
#endif
        EnterMiniPlayer(state);
    }

    private DispatcherTimer? _miniGuard;

    /// <summary>
    /// Il mini player non deve mai restare acceso quando non ha senso: musica fermata (anche dal tasto Stop del mini
    /// player), un video che parte, finestra ingrandita o a schermo intero (Win+Su, tasto F). Prima la finestra restava
    /// con misura e proporzioni del mini player e dentro tornava la libreria.
    /// </summary>
    private void CheckMiniGuard()
    {
        if (_beforeMini == null) { _miniGuard?.Stop(); return; }
#if CINECORE_WINDOWS
        if (_morphing) return;
#endif
        var now = WindowState;
        if (now == WindowState.Minimized) return;
        if (now != WindowState.Normal)
        {
            // Ingrandita mentre era piccola: si esce dal mini player e si tiene lo stato scelto.
            WindowState = WindowState.Normal;
            ToggleMiniPlayer();
            WindowState = now;
            return;
        }
        if (!_library.IsVisible) { ToggleMiniPlayer(); return; }
        if (!_library.HasMusicBar)
        {
#if CINECORE_WINDOWS
            ToggleMiniPlayerAnimated();
#else
            ToggleMiniPlayer();
#endif
        }
    }

    private void EnterMiniPlayer(WindowState state)
    {
        if (_beforeMini != null) return;
        _miniGuard ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => CheckMiniGuard());
        _miniGuard.Start();
        _beforeMini = (Position, Width, Height, state);
        double w = MiniDefaultWidth, h = MiniHeightFor(w);
        // Nell'angolo in basso a destra della finestra di prima: resta sullo stesso schermo (Position e' in punti).
        var old = Position;
        int x = old.X + (int)Math.Max(0, _beforeMini.Value.Width - w), y = old.Y + (int)Math.Max(0, _beforeMini.Value.Height - h);
        var pos = new PixelPoint(x, y);
        if (SavedMiniBounds() is { } saved) { pos = saved.Position; w = saved.Width; h = saved.Height; }
#if CINECORE_WINDOWS
        SystemDecorations = SystemDecorations.None;
        // Vetro vero di Windows (acrilico) dietro il mini player, come il vetro di macOS sul Mac.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent };
#endif
        // La misura minima della finestra normale non vale per il mini player.
        MinWidth = MiniMinWidth; MinHeight = MiniHeightFor(MiniMinWidth) * .8;
        Width = w; Height = h;
        Position = pos;
        Topmost = true;
        _library.SetMiniPlayer(true);
        ApplyWindowButtonsAlpha();
#if CINECORE_WINDOWS
        // Angoli tondi e ombra di Windows anche senza cornice, senza il filo del bordo.
        IntPtr hwnd = WinNative.Handle(this);
        if (hwnd != IntPtr.Zero)
        {
            int round = 2, border = unchecked((int)0xFFFFFFFE);
            WinNative.DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            WinNative.DwmSetWindowAttribute(hwnd, 34, ref border, 4);
        }
#endif
    }

    /// <summary>La misura e la posizione scelte l'ultima volta, se cadono ancora in uno schermo.</summary>
    private (PixelPoint Position, double Width, double Height)? SavedMiniBounds()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) return null;
        var parts = MacSettings.Get("miniBounds", "").Split(';');
        if (parts.Length != 4) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) return null;
        try
        {
            var probe = new PixelRect(x + 20, y + 20, 40, 40);
            if (!Screens.All.Any(s => s.WorkingArea.Intersects(probe))) return null;
        }
        catch { return null; }
        return (new PixelPoint(x, y), Math.Max(MiniMinWidth, w), Math.Max(200, h));
    }

    private void SaveMiniBounds()
    {
        if (_beforeMini == null || Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) return;
        if (Width < 100 || Height < 100) return;
        MacSettings.Set("miniBounds", string.Create(CultureInfo.InvariantCulture, $"{Position.X};{Position.Y};{Math.Round(Width)};{Math.Round(Height)}"));
    }
}
