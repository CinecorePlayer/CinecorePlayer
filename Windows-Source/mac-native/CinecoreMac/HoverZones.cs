using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace CinecoreMac;

/// <summary>
/// Zone sensibili al mouse di una vista disegnata a mano (2026-11-21).
/// Le viste ridisegnavano tutta la loro superficie a ogni movimento del mouse, fino a 60 volte al secondo: su una
/// scheda video modesta bastava muovere il mouse per far scattare tutto. Ogni controllo "mouse sopra" del disegno
/// passa da <see cref="Track"/>; al movimento si ridisegna solo se il mouse entra o esce da una delle zone.
/// Le zone sono in coordinate della vista: chi disegna con il mouse spostato (contenuto che scorre) le riporta li'.
/// </summary>
internal sealed class HoverZones
{
    private const int Limit = 4096;
    private readonly List<Rect> _zones = new();
    private Point _rendered = new(-1e6, -1e6);
    private bool _overflow;

    /// <summary>Il mouse dell'ultimo disegno vero.</summary>
    public Point RenderedMouse => _rendered;

    /// <summary>All'inizio di ogni disegno vero (non delle foto della pagina), con il mouse di quel disegno.</summary>
    public void Begin(Point mouse)
    {
        _zones.Clear();
        _overflow = false;
        _rendered = mouse;
    }

    public void Track(Rect zone)
    {
        if (zone.Width <= 0 || zone.Height <= 0) return;
        if (_zones.Count < Limit) _zones.Add(zone);
        else _overflow = true;
    }

    /// <summary>Il mouse, passando in <paramref name="now"/>, e' entrato o uscito da almeno una zona.</summary>
    public bool Changed(Point now)
    {
        if (_overflow) return true;
        foreach (var zone in _zones)
            if (zone.Contains(now) != zone.Contains(_rendered)) return true;
        return false;
    }
}

/// <summary>
/// Solo per le prove: controlla che <see cref="HoverZones"/> non perda ridisegni. Disegna la vista con il mouse
/// dell'ultimo disegno e con quello nuovo: se le immagini differiscono ma le zone dicono "niente da ridisegnare",
/// e' un ridisegno mancato (un "mouse sopra" che non passa da Hover).
/// </summary>
internal static class HoverProbe
{
    /// <param name="setMouse">mette il mouse della vista.</param>
    /// <returns>changed: le zone chiedono un ridisegno; differs: il disegno cambia davvero.</returns>
    public static (bool Changed, bool Differs) Run(Control view, HoverZones zones, Point now, Action<Point> setMouse)
    {
        var rendered = zones.RenderedMouse;
        bool changed = zones.Changed(now);
        setMouse(rendered); var before = Snapshot(view);
        setMouse(now); var after = Snapshot(view);
        bool differs = !before.AsSpan().SequenceEqual(after);
        // Le zone tornano come le lascerebbe l'app: ridisegnata in now se c'era un cambio, altrimenti ferma.
        setMouse(changed ? now : rendered); Snapshot(view);
        setMouse(now);
        if (changed) view.InvalidateVisual();
        return (changed, differs);
    }

    private static byte[] Snapshot(Control view)
    {
        var size = new PixelSize(Math.Max(1, (int)view.Bounds.Width), Math.Max(1, (int)view.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var dc = bitmap.CreateDrawingContext()) view.Render(dc);
        var pixels = new byte[size.Width * size.Height * 4];
        unsafe { fixed (byte* p = pixels) bitmap.CopyPixels(new PixelRect(size), (IntPtr)p, pixels.Length, size.Width * 4); }
        return pixels;
    }
}
