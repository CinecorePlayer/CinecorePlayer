using System;
using Avalonia;
using Avalonia.Controls;

namespace CinecoreMac;

/// <summary>
/// Mini player della musica (TogglePipMode di Windows con un brano audio): la finestra diventa piccola, resta sopra
/// le altre app e mostra il brano a schermo intero con la barra compatta. Lo stesso comando la riporta com'era.
/// </summary>
public sealed partial class MainWindow
{
    private (PixelPoint Position, double Width, double Height)? _beforeMini;

    internal const double WindowMinWidth = 1024, WindowMinHeight = 660;

    private void ToggleMiniPlayer()
    {
        if (_beforeMini is { } before)
        {
            _beforeMini = null;
            Topmost = false;
            MinWidth = WindowMinWidth; MinHeight = WindowMinHeight;
            Width = before.Width; Height = before.Height; Position = before.Position;
            _library.SetMiniPlayer(false);
            ApplyWindowButtonsAlpha();
            return;
        }
        // Dallo schermo intero prima si esce: una finestra piccola dentro lo Space a tutto schermo non avrebbe senso.
        if (IsFullscreen) { WindowState = WindowState.Normal; return; }
        _beforeMini = (Position, Width, Height);
        // Nell'angolo in basso a destra della finestra di prima: resta sullo stesso schermo (Position e' in punti).
        // Altezza = contenuto del mini player (LayoutMiniPlayer: 30 sopra, copertina al 64%, brano, timeline, comandi, icone).
        double w = 340, h = 30 + Math.Round(w * .64) + 18 + 46 + 10 + 20 + 18 + 44 + 8 + 30 + 16;
        var old = Position;
        int x = old.X + (int)Math.Max(0, _beforeMini.Value.Width - w), y = old.Y + (int)Math.Max(0, _beforeMini.Value.Height - h);
        // La misura minima della finestra normale (640) non vale per il mini player.
        MinWidth = 320; MinHeight = 0;
        Width = w; Height = h;
        Position = new PixelPoint(x, y);
        Topmost = true;
        _library.SetMiniPlayer(true);
        ApplyWindowButtonsAlpha();
    }
}
