using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// I "semafori" di macOS su Windows, identici a quelli dell'app Mac: chiudi (rosso), riduci (giallo), schermo intero
/// (verde), in alto a sinistra. I simboli compaiono passando sul gruppo; a finestra non attiva diventano grigi.
/// </summary>
internal sealed class WinCaptionButtons : Control
{
    public const double ButtonHeight = 28;
    private const double Diameter = 12, Spacing = 20, FirstCenterX = 20, CenterY = 14;
    private readonly Window _window;
    private readonly Action _toggleFullscreen;
    private int _hot = -1, _pressed = -1;
    private bool _groupHot;

    public WinCaptionButtons(Window window, Action toggleFullscreen)
    {
        _window = window;
        _toggleFullscreen = toggleFullscreen;
        Width = FirstCenterX + Spacing * 2 + Diameter; Height = ButtonHeight;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Cursor = new Cursor(StandardCursorType.Arrow);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) IsVisible = window.WindowState != WindowState.FullScreen;
            if (e.Property == Window.IsActiveProperty) InvalidateVisual();
        };
    }

    private static Point Center(int i) => new(FirstCenterX + i * Spacing, CenterY);

    private static int ButtonAt(Point p)
    {
        for (int i = 0; i < 3; i++)
        {
            var c = Center(i);
            if (Math.Abs(p.X - c.X) <= Spacing / 2 && Math.Abs(p.Y - c.Y) <= Diameter) return i;
        }
        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        int hot = ButtonAt(p);
        bool group = p.X >= FirstCenterX - Diameter && p.X <= FirstCenterX + Spacing * 2 + Diameter && p.Y <= ButtonHeight;
        if (hot != _hot || group != _groupHot) { _hot = hot; _groupHot = group; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e) { _hot = -1; _pressed = -1; _groupHot = false; InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = ButtonAt(e.GetPosition(this)); InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Handled = true;
        int button = ButtonAt(e.GetPosition(this));
        int pressed = _pressed;
        _pressed = -1; InvalidateVisual();
        if (button != pressed || button < 0) return;
        switch (button)
        {
            case 0: _window.Close(); break;
            case 1: _window.WindowState = WindowState.Minimized; break;
            case 2: _toggleFullscreen(); break;
        }
    }

    // Colori di macOS (Big Sur e seguenti): riempimento e bordo appena piu' scuro.
    private static readonly (Color Fill, Color Edge, Color Glyph)[] Lights =
    {
        (Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xE0, 0x44, 0x3E), Color.FromRgb(0x4D, 0x00, 0x00)),
        (Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0xDE, 0xA1, 0x23), Color.FromRgb(0x99, 0x58, 0x00)),
        (Color.FromRgb(0x28, 0xC8, 0x40), Color.FromRgb(0x1A, 0xAB, 0x29), Color.FromRgb(0x00, 0x65, 0x00)),
    };

    public override void Render(DrawingContext ctx)
    {
        bool active = _window.IsActive || _groupHot;
        for (int i = 0; i < 3; i++)
        {
            var c = Center(i);
            var (fill, edge, glyph) = Lights[i];
            if (!active)
            {
                fill = UiTheme.IsLight ? Color.FromRgb(0xDD, 0xDD, 0xDD) : Color.FromRgb(0x4D, 0x4D, 0x4D);
                edge = UiTheme.IsLight ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x3E, 0x3E, 0x3E);
            }
            if (_pressed == i) fill = Color.FromRgb((byte)(fill.R * .82), (byte)(fill.G * .82), (byte)(fill.B * .82));
            ctx.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(edge), .6), c, Diameter / 2 - .3, Diameter / 2 - .3);
            if (!_groupHot) continue;
            var pen = new Pen(new SolidColorBrush(glyph), 1.1, lineCap: PenLineCap.Round);
            switch (i)
            {
                case 0:
                    ctx.DrawLine(pen, new Point(c.X - 2.6, c.Y - 2.6), new Point(c.X + 2.6, c.Y + 2.6));
                    ctx.DrawLine(pen, new Point(c.X + 2.6, c.Y - 2.6), new Point(c.X - 2.6, c.Y + 2.6));
                    break;
                case 1:
                    ctx.DrawLine(pen, new Point(c.X - 3.2, c.Y), new Point(c.X + 3.2, c.Y));
                    break;
                case 2:
                    // Due triangoli pieni in diagonale (schermo intero), come il pulsante verde del Mac.
                    var brush = new SolidColorBrush(glyph);
                    var a = new StreamGeometry();
                    using (var g = a.Open())
                    {
                        g.BeginFigure(new Point(c.X - 3.2, c.Y - 3.2), true); g.LineTo(new Point(c.X + 1.4, c.Y - 3.2)); g.LineTo(new Point(c.X - 3.2, c.Y + 1.4)); g.EndFigure(true);
                        g.BeginFigure(new Point(c.X + 3.2, c.Y + 3.2), true); g.LineTo(new Point(c.X - 1.4, c.Y + 3.2)); g.LineTo(new Point(c.X + 3.2, c.Y - 1.4)); g.EndFigure(true);
                    }
                    ctx.DrawGeometry(brush, null, a);
                    break;
            }
        }
    }
}

/// <summary>
/// Angoli della finestra durante il film: le finestre video hanno una regione un poco piu' arrotondata (senza
/// antialiasing); qui si copre di nero quella piccola differenza dentro la curva del player, che ha l'antialiasing.
/// </summary>
internal sealed class WinCornerFill : Control
{
    public const double Radius = 8, VideoRadius = 10;
    public bool Active { get => _active; set { if (_active != value) { _active = value; InvalidateVisual(); } } }
    private bool _active;

    public WinCornerFill() { IsHitTestVisible = false; }

    public override void Render(DrawingContext ctx)
    {
        if (!_active) return;
        var r = new Rect(Bounds.Size);
        // Solo in basso: in alto c'e' la barra del titolo di Windows e il video e' squadrato. La curva esterna la
        // ritaglia DWM (Windows 11, con antialiasing).
        var video = new RectangleGeometry(new Rect(0, -2 * VideoRadius, r.Width, r.Height + 2 * VideoRadius), VideoRadius, VideoRadius);
        var gap = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(r), video);
        ctx.DrawGeometry(Brushes.Black, null, gap);
    }
}
