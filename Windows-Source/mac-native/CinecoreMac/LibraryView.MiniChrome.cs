using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// Mini player della musica senza cornice (Windows, 2026-11-21): niente barra del titolo, quindi la finestra si
/// sposta trascinando un punto vuoto e si ridimensiona dai bordi e dagli angoli. Sul Mac la finestra tiene la sua
/// cornice e qui non succede niente (<see cref="MiniChromeless"/> resta falso).
/// </summary>
internal sealed partial class LibraryView
{
    /// <summary>Vero su Windows: il mini player non ha la cornice di sistema.</summary>
    public static bool MiniChromeless;
    private const double MiniEdge = 7;

    /// <summary>C'e' un brano nella barra della musica (se no il mini player non ha niente da mostrare).</summary>
    public bool HasMusicBar => MusicBarVisible;
    private Rect _miniRowBox;

    /// <summary>La finestra ha davvero il vetro di Windows dietro (acrilico).</summary>
    private bool MiniAcrylic => MiniChromeless && _miniPlayer && TopLevel.GetTopLevel(this)?.ActualTransparencyLevel is { } level
        && (level == WindowTransparencyLevel.AcrylicBlur || level == WindowTransparencyLevel.Blur || level == WindowTransparencyLevel.Mica);

    /// <summary>
    /// Fondo del mini player su Windows: una velatura sopra il vetro vero (o un fondo pieno se il vetro non c'e'), il
    /// filo del bordo e il riquadro con gli angoli tondi attorno all'ultima fila di icone.
    /// </summary>
    private void DrawMiniWindowsSurface(DrawingContext ctx)
    {
        bool light = UiTheme.IsLight;
        var r = _barCard;
        double rad = CardRadius;
        var veil = MiniAcrylic
            ? (light ? Color.FromArgb(110, 250, 251, 253) : Color.FromArgb(105, 14, 16, 21))
            : (light ? Color.FromRgb(246, 247, 249) : Color.FromRgb(24, 27, 33));
        ctx.DrawRectangle(new SolidColorBrush(veil), new Pen(new SolidColorBrush(light ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(22, 255, 255, 255)), 1), r.Deflate(.5), rad, rad);
        if (_miniRowBox.Width > 0)
            ctx.DrawRectangle(new SolidColorBrush(light ? Color.FromArgb(16, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255)),
                new Pen(new SolidColorBrush(light ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255)), 1), _miniRowBox.Deflate(.5), 12, 12);
    }

    public event Action<PointerPressedEventArgs>? MiniDragRequested;
    public event Action<PointerPressedEventArgs, WindowEdge>? MiniResizeRequested;

    private WindowEdge? MiniEdgeAt(Point p)
    {
        if (!MiniChromeless || !_miniPlayer) return null;
        double w = Bounds.Width, h = Bounds.Height;
        bool l = p.X < MiniEdge, r = p.X > w - MiniEdge, t = p.Y < MiniEdge, b = p.Y > h - MiniEdge;
        return (l, r, t, b) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }

    /// <summary>Bordo: si ridimensiona. Ritorna vero se il clic e' stato preso.</summary>
    private bool MiniEdgePressed(Point p, PointerPressedEventArgs e)
    {
        if (MiniEdgeAt(p) is not { } edge) return false;
        MiniResizeRequested?.Invoke(e, edge);
        return true;
    }

    /// <summary>Punto vuoto del mini player (non un tasto, non la timeline): si sposta la finestra.</summary>
    private bool MiniDragPressed(Point p, PointerPressedEventArgs e)
    {
        if (!MiniChromeless || !_miniPlayer || BarTargetAt(p) != "") return false;
        MiniDragRequested?.Invoke(e);
        return true;
    }

    /// <summary>Il cursore dei bordi (frecce di ridimensionamento).</summary>
    private void MiniEdgeCursor(Point p)
    {
        if (MiniEdgeAt(p) is not { } edge) return;
        Cursor = new Cursor(edge switch
        {
            WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
            WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
            WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
            WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
            WindowEdge.West or WindowEdge.East => StandardCursorType.SizeWestEast,
            _ => StandardCursorType.SizeNorthSouth,
        });
    }
}
