using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// "Prima del film" (PausePlaceholderOverlay di Windows): a tutta finestra lo sfondo del film (TMDb) o l'immagine scelta,
/// con il velo che scurisce a sinistra e in basso, il marchio in alto a sinistra e il titolo in basso a sinistra.
/// Due usi: prima del film (con "Avvia film" e "Torna alla libreria") e in pausa (solo la schermata; l'HUD sta sopra).
/// Rispetto a Windows: il film non e' ancora aperto mentre la schermata e' a schermo (niente nero uscendo dallo schermo
/// intero o tornando dalle Impostazioni), la libreria resta sotto intatta, e senza immagine c'e' sempre il fondo neutro.
/// </summary>
internal sealed class CinemaGateView : Control
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    public event Action? StartRequested, CancelRequested;

    private readonly bool _gate;
    private Bitmap? _image;
    private string? _imagePath;
    private string _title = "", _subtitle = "";
    private int _selected;
    private Point _mouse = new(-1, -1);
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private Rect _start, _back;
    private DateTime _shownAt;
    private static Bitmap? _logo;

    /// <param name="gate">true prima del film (scelte e tastiera), false in pausa (solo immagine, il mouse passa all'HUD).</param>
    public CinemaGateView(bool gate)
    {
        _gate = gate;
        IsVisible = false; Focusable = gate; IsHitTestVisible = gate; ClipToBounds = true;
    }

    public bool IsOpen => IsVisible;

    public void Show(string title, string subtitle, string? imagePath)
    {
        _title = title; _subtitle = subtitle; _selected = 0;
        SetImage(imagePath);
        if (!IsVisible) _shownAt = DateTime.UtcNow;
        IsVisible = true;
        if (_gate) Focus();
        InvalidateVisual();
    }

    public void Hide() { IsVisible = false; }

    /// <summary>Lo sfondo arrivato dopo (TMDb in sottofondo): entra senza chiudere la schermata.</summary>
    public void SetImage(string? path)
    {
        if (path == _imagePath) return;
        _imagePath = path;
        var old = _image; _image = null; old?.Dispose();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { InvalidateVisual(); return; }
        System.Threading.Tasks.Task.Run(() =>
        {
            Bitmap? loaded = null;
            try { using var fs = File.OpenRead(path); loaded = Bitmap.DecodeToWidth(fs, 2560, BitmapInterpolationMode.HighQuality); } catch { }
            Dispatcher.UIThread.Post(() => { if (_imagePath == path) { _image = loaded; InvalidateVisual(); } else loaded?.Dispose(); });
        });
    }

    private static Bitmap? Logo()
    {
        if (_logo != null) return _logo;
        try
        {
            string path = Path.Combine(MacTools.AssetsDirectory, "logo.png");
            if (File.Exists(path)) _logo = new Bitmap(path);
        }
        catch { }
        return _logo;
    }

    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static double Smooth(double t) => t * t * (3 - 2 * t);

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        var r = new Rect(Bounds.Size);
        if (r.Width < 50 || r.Height < 50) return;
        double fade = Math.Clamp((DateTime.UtcNow - _shownAt).TotalMilliseconds / 260, 0, 1);
        if (fade < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        using var opacity = ctx.PushOpacity(Smooth(fade));
        double w = r.Width, h = r.Height;
        if (_image is { } img)
        {
            ctx.FillRectangle(Brushes.Black, r);
            double s = Math.Max(w / img.Size.Width, h / img.Size.Height);
            double iw = img.Size.Width * s, ih = img.Size.Height * s;
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(img, new Rect((w - iw) / 2, (h - ih) / 2, iw, ih));
        }
        else
        {
            // Fondo neutro senza sfondo (anche senza rete): grigio-blu che scende verso il nero con un alone d'accento.
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(.35, 1, RelativeUnit.Relative),
                GradientStops = { new(Color.FromRgb(17, 20, 27), 0), new(Color.FromRgb(6, 7, 10), 1) },
            }, r);
            var accent = UiTheme.Accent;
            ctx.FillRectangle(new RadialGradientBrush
            {
                Center = new RelativePoint(.78, .20, RelativeUnit.Relative), GradientOrigin = new RelativePoint(.78, .20, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(h * .95 / w, RelativeUnit.Relative), RadiusY = new RelativeScalar(.95, RelativeUnit.Relative),
                GradientStops = { new(Color.FromArgb(87, accent.R, accent.G, accent.B), 0), new(Color.FromArgb(30, accent.R, accent.G, accent.B), .45), new(Color.FromArgb(0, accent.R, accent.G, accent.B), 1) },
            }, r);
        }
        // Velo: pieno dietro al titolo (in basso a sinistra), nullo a destra (ComposeBackdrop di Windows).
        var horizontal = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative) };
        var vertical = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0;
            double u = Math.Clamp((t - .08) / .60, 0, 1);
            horizontal.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * .90 * (1 - Smooth(u))), 0, 0, 0), t));
            double v = Math.Clamp((t - .50) / .50, 0, 1);
            vertical.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * .62 * Smooth(v)), 0, 0, 0), t));
        }
        ctx.FillRectangle(horizontal, r);
        ctx.FillRectangle(vertical, r);

        // Marchio in alto a sinistra (oltre i pulsanti della finestra), da solo.
        double margin = Math.Round(Math.Max(40, w * 0.055));
        if (Logo() is { } logo)
        {
            double lh = Math.Round(Math.Max(24, h * 0.042)), lw = Math.Round(lh * logo.Size.Width / Math.Max(1, logo.Size.Height));
            ctx.DrawImage(logo, new Rect(Math.Max(margin, 92), Math.Round(Math.Max(28, h * 0.065)), lw, lh));
        }
        // Titolo, riga secondaria e scelte ancorati in basso a sinistra, dove il velo e' pieno.
        double maxText = Math.Max(240, w * 0.46);
        var title = UiText.Get(L10n.T(string.Join(" ", _title.Split(' ', StringSplitOptions.RemoveEmptyEntries))), Semibold, Math.Max(24, h * 0.066), Color.FromRgb(248, 249, 250), maxText, 2, TextAlignment.Left, TextTrimming.WordEllipsis);
        var sub = UiText.Get(L10n.T(_subtitle), Regular, Math.Max(13, h * 0.0205), Color.FromRgb(176, 184, 194), maxText, 2, TextAlignment.Left, TextTrimming.WordEllipsis);
        double gap = _title.Length > 0 && _subtitle.Length > 0 ? Math.Round(h * 0.016) : 0;
        double choiceSize = Math.Max(15, h * 0.027), choiceH = _gate ? Math.Round(choiceSize * 1.3 * 2.1) : 0, choiceGap = _gate ? Math.Round(h * 0.034) : 0;
        double bottom = h - Math.Round(Math.Max(44, h * 0.105));
        double subH = _subtitle.Length > 0 ? sub.Height : 0;
        double y = Math.Round(bottom - (_title.Length > 0 ? title.Height : 0) - gap - subH - choiceGap - choiceH);
        if (_title.Length > 0) { ctx.DrawText(title, new Point(margin, y)); y += title.Height + gap; }
        if (_subtitle.Length > 0) { ctx.DrawText(sub, new Point(margin + Math.Round(h * 0.003), y)); y += subH; }
        _start = _back = default;
        if (!_gate) return;
        // Scelte con i pulsanti dell'hero della libreria: quella attiva bianca piena, l'altra in vetro chiaro.
        y = Math.Round(y + choiceGap);
        double x = margin;
        string[] choices = { "Avvia film", "Torna alla libreria" };
        for (int i = 0; i < choices.Length; i++)
        {
            bool selected = i == _selected;
            var text = UiText.Get(L10n.T(choices[i]), Semibold, choiceSize, selected ? Colors.Black : Colors.White);
            double padX = Math.Round(choiceH * .62), glyph = i == 0 ? Math.Round(choiceH * .30) : 0, glyphGap = i == 0 ? Math.Round(choiceH * .22) : 0;
            var rect = new Rect(x, y, padX * 2 + glyph + glyphGap + Math.Ceiling(text.Width), choiceH);
            bool hot = Hot(rect);
            ctx.DrawRectangle(B(selected ? Color.FromArgb((byte)(hot ? 230 : 255), 255, 255, 255) : Color.FromArgb((byte)(hot ? 70 : 46), 255, 255, 255)), null, rect, choiceH / 2, choiceH / 2);
            double cx = rect.X + padX;
            if (i == 0)
            {
                double gy = rect.Y + (rect.Height - glyph) / 2;
                ctx.DrawGeometry(B(selected ? Colors.Black : Colors.White), null, new PolylineGeometry(new[] { new Point(cx + 1, gy), new Point(cx + glyph, gy + glyph / 2), new Point(cx + 1, gy + glyph) }, true));
                cx += glyph + glyphGap;
            }
            ctx.DrawText(text, new Point(cx, rect.Y + (rect.Height - text.Height) / 2));
            if (i == 0) _start = rect; else _back = rect;
            x = rect.Right + Math.Round(choiceH * .32);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var now = e.GetPosition(this);
        bool changed = _zones.Changed(now);
        _mouse = now;
        Cursor = _start.Contains(_mouse) || _back.Contains(_mouse) ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (changed) InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        var p = e.GetPosition(this);
        if (_start.Contains(p)) StartRequested?.Invoke();
        else if (_back.Contains(p)) CancelRequested?.Invoke();
    }

    /// <summary>Frecce per scegliere, Invio o Spazio per confermare, Esc per tornare alla libreria.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: case Key.Up: _selected = 0; InvalidateVisual(); break;
            case Key.Right: case Key.Down: _selected = 1; InvalidateVisual(); break;
            case Key.Enter: case Key.Space: if (_selected == 0) StartRequested?.Invoke(); else CancelRequested?.Invoke(); break;
            case Key.Escape: CancelRequested?.Invoke(); break;
            default: return;
        }
        e.Handled = true;
    }
}
