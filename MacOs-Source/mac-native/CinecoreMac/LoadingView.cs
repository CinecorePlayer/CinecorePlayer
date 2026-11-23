using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Schermata di caricamento del film (LoadingArt / VideoLoadingMask di Windows): lo sfondo del film con un velo da
/// sinistra e dal basso, il titolo grande in basso a sinistra, un messaggio e una barra a tutta larghezza che avanza
/// per fasi vere (Wine avviato, file aperto, renderer collegato) e arriva in fondo prima che il film parta.
/// </summary>
internal sealed class LoadingView : Control
{
    private static readonly FontFamily Inter = new("avares://CinecoreMac/Assets/Fonts#Inter");
    private static readonly FontFamily Display = new("avares://CinecoreMac/Assets/Fonts#Inter Display");
    private static readonly Typeface TitleFace = new(Display, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface TextFace = new(Inter);

    public LibraryView? Library { get; set; }
    private string _title = "", _message = "", _renderer = "";
    private string? _art;
    private double _target, _shown;
    private bool _finishing;
    private readonly Stopwatch _clock = new();
    private double _fadeStartMs = -1;
    private readonly DispatcherTimer _frames;

    public LoadingView()
    {
        IsVisible = false; IsHitTestVisible = false;
        _frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
        _frames.Stop(); // parte con Show
        LibraryView.ImageReady += () => { if (IsVisible) InvalidateVisual(); };
    }

    public bool Active => IsVisible && _fadeStartMs < 0;

    public void Show(string title, string? art, string renderer)
    {
        // Gia' in corso (indirizzo web appena risolto): si continua dalla barra e dal messaggio a cui si era, senza
        // ricominciare da capo (prima sembrava che tornasse al caricamento).
        if (Active && !_finishing && _continuing)
        {
            _continuing = false;
            _title = title; _art = art ?? _art; _renderer = renderer;
            _message = "Avvio di " + renderer + "…";
            if (_target < .2) _target = .2;
            InvalidateVisual();
            return;
        }
        _continuing = false;
        _title = title; _art = art; _renderer = renderer; _message = "Avvio di " + renderer + "…";
        _target = .08; _shown = 0; _finishing = false; _fadeStartMs = -1;
        _clock.Restart();
        IsVisible = true; _frames.Start(); InvalidateVisual();
    }

    private bool _continuing;

    /// <summary>Immagine arrivata dopo (miniatura di YouTube): entra se la schermata e' ancora quella.</summary>
    public void SetArt(string art) { if (IsVisible) { _art = art; InvalidateVisual(); } }

    /// <summary>
    /// Prima del film: un indirizzo web da risolvere (YouTube). La schermata c'e' subito; il Show che segue continua da qui.
    /// </summary>
    public void ShowResolving(string title, string message)
    {
        Show(title, null, "YouTube");
        _message = message;
        _target = .12;
        _continuing = true;
    }

    /// <summary>Una fase raggiunta: la barra non torna mai indietro.</summary>
    public void Stage(double progress, string message)
    {
        if (!IsVisible || _finishing) return;
        progress = Math.Clamp(progress, 0, .96);
        if (progress <= _target) return;
        _target = progress;
        if (message.Length > 0) _message = _renderer + ": " + L10n.T(message); // tradotto qui: "madVR: …" non si trova intero
    }

    /// <summary>Il film e' pronto: la barra corre in fondo, poi la schermata sfuma via.</summary>
    public void Finish() { if (IsVisible) { _finishing = true; _target = 1; _message = _renderer + " pronto"; } }

    public void Hide() { IsVisible = false; _frames.Stop(); }

    private void Step()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        // La barra insegue la fase raggiunta con un passo costante (scorre in modo uniforme); in attesa avanza appena,
        // senza mai superare la fase successiva.
        double speed = _finishing ? .045 : .012;
        if (_shown < _target) _shown = Math.Min(_target, _shown + speed);
        else if (!_finishing && _shown < _target + .06) _shown += .0004;
        if (_finishing && _shown >= .999 && _fadeStartMs < 0) _fadeStartMs = now;
        if (_fadeStartMs >= 0 && now - _fadeStartMs > 220) { Hide(); return; }
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;
        double opacity = _fadeStartMs < 0 ? 1 : 1 - Math.Clamp((_clock.Elapsed.TotalMilliseconds - _fadeStartMs) / 220, 0, 1);
        using var fade = ctx.PushOpacity(opacity);
        var page = new Rect(size);
        ctx.FillRectangle(Brushes.Black, page);
        double w = size.Width, h = size.Height;
        if (Library?.LoadImage(_art, w) is { } bmp)
        {
            // Un quarto dall'alto: nelle foto e negli sfondi i volti stanno piu' su del centro.
            double sw = bmp.Size.Width, sh = bmp.Size.Height, k = Math.Max(w / sw, h / sh);
            double dw = sw * k, dh = sh * k;
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(bmp, new Rect(0, 0, sw, sh), new Rect((w - dw) / 2, (h - dh) * .28, dw, dh));
            PaintVeil(ctx, size);
        }
        if (_title.Length == 0) return;
        // Titolo, messaggio e barra in basso a sinistra (PaintCaption), con le stesse proporzioni di Windows.
        double margin = Math.Round(Math.Max(40, w * .055)), maxText = Math.Max(240, w * .5);
        var title = UiText.Get(L10n.T(string.Join(" ", _title.Split(' ', StringSplitOptions.RemoveEmptyEntries))), TitleFace, Math.Max(24, h * .066),
            Color.FromRgb(248, 249, 250), maxText, 2, trimming: TextTrimming.WordEllipsis);
        var sub = UiText.Get(L10n.T(_message), TextFace, Math.Max(13, h * .0205), Color.FromRgb(176, 184, 194), maxText);
        double barH = Math.Max(3, Math.Round(h * .0042)), barW = Math.Max(120, w - 2 * margin - Math.Round(h * .003));
        double bottom = h - Math.Round(Math.Max(44, h * .105)), barY = bottom - barH;
        double subY = barY - Math.Round(h * .022) - sub.Height, titleY = subY - Math.Round(h * .014) - title.Height;
        ctx.DrawText(title, new Point(margin, titleY));
        double x = margin + Math.Round(h * .003);
        ctx.DrawText(sub, new Point(x, subY));
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(52, 255, 255, 255)), new Rect(x, barY, barW, barH), (float)(barH / 2));
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(248, 249, 250)), new Rect(x, barY, barW * Math.Clamp(_shown, .02, 1), barH), (float)(barH / 2));
    }

    /// <summary>Il velo scuro da sinistra e dal basso su cui si leggono titolo e barra (PaintVeil).</summary>
    private static void PaintVeil(DrawingContext ctx, Size size)
    {
        Color t = Color.FromRgb(4, 6, 9);
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(222, t.R, t.G, t.B), 0), new GradientStop(Color.FromArgb(0, t.R, t.G, t.B), 1) },
        }, new Rect(0, 0, size.Width * .78, size.Height));
        double low = size.Height * .58;
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0, t.R, t.G, t.B), 0), new GradientStop(Color.FromArgb(214, t.R, t.G, t.B), 1) },
        }, new Rect(0, size.Height - low, size.Width, low));
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(40, t.R, t.G, t.B)), new Rect(size));
    }
}
