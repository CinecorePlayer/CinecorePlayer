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
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly FontFamily Display = new(AppAssets.Root + "Assets/Fonts#Inter Display");
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
    // Solo la rotellina al centro (cambio di risoluzione di un flusso): niente foto, titolo ne' barra.
    private bool _spinner;

    /// <summary>Caricamento semplice: fondo nero e la rotellina che gira, finche' il flusso nuovo non e' pronto.</summary>
    public void ShowSpinner()
    {
        _spinner = true; _title = ""; _art = null; _message = ""; _renderer = "";
        _target = .08; _shown = 0; _finishing = false; _fadeStartMs = -1;
        _clock.Restart();
        IsVisible = true; _frames.Start(); InvalidateVisual();
    }

    /// <summary>Messaggio della fase prima che il motore parta (es. "Risolvo YouTube…").</summary>
    public void SetMessage(string message) { if (IsVisible) { _message = message; InvalidateVisual(); } }
    private double _artAt = -1;

    public void Show(string title, string? art, string renderer)
    {
        // Caricamento gia' in corso (es. YouTube: prima si risolve l'indirizzo, poi parte il motore): un solo
        // caricamento, la barra prosegue da dove era invece di ripartire da zero.
        bool continuing = Active && !_spinner && !_finishing;
        if (!IsVisible || !string.Equals(art, _art, StringComparison.OrdinalIgnoreCase)) _artAt = -1;
        else if (_artAt >= 0) _artAt = -1000; // stessa foto gia' a schermo (secondo Show della stessa apertura): resta piena
        _spinner = false;
        _title = title; _art = art; _renderer = renderer; _message = "Avvio di " + renderer + "…";
        _finishing = false; _fadeStartMs = -1;
        if (continuing) { _target = Math.Max(_target, .08); }
        else { _target = .08; _shown = 0; _clock.Restart(); }
        IsVisible = true; _frames.Start(); InvalidateVisual();
    }

    /// <summary>Titolo e sfondo arrivati dopo l'apertura (DVD: TMDb cercato dall'etichetta del disco).</summary>
    public void UpdateArt(string? title, string? art)
    {
        if (!IsVisible) return;
        if (!string.IsNullOrWhiteSpace(title)) _title = title;
        if (!string.IsNullOrWhiteSpace(art) && art != _art) { _art = art; _artAt = -1; }
        InvalidateVisual();
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

    public void Hide() { IsVisible = false; _spinner = false; _frames.Stop(); }

    private void Step()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        // La barra insegue la fase raggiunta con un passo costante (scorre in modo uniforme); in attesa avanza appena,
        // senza mai superare la fase successiva.
        double speed = _finishing ? .045 : .012;
        if (_shown < _target) _shown = Math.Min(_target, _shown + speed);
        else if (!_finishing && _shown < _target + .06) _shown += .0004;
        if (_spinner && _finishing && _fadeStartMs < 0) _fadeStartMs = now;
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
        if (_spinner)
        {
            // Rotellina classica: un arco che gira (un giro ogni 0,9 s).
            double r = Math.Max(14, Math.Min(w, h) * .028), a0 = _clock.Elapsed.TotalMilliseconds / 900.0 * Math.PI * 2;
            var c = new Point(w / 2, h / 2);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), Math.Max(2.5, r * .16)), c, r, r);
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false);
                double a1 = a0 + Math.PI * .6;
                g.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(240, 242, 245)), Math.Max(2.5, r * .16), lineCap: PenLineCap.Round), arc);
            return;
        }
        if (Library?.LoadImage(_art, w) is { } bmp)
        {
            // Un quarto dall'alto: nelle foto e negli sfondi i volti stanno piu' su del centro.
            double sw = bmp.Size.Width, sh = bmp.Size.Height, k = Math.Max(w / sw, h / sh);
            double dw = sw * k, dh = sh * k;
            // La foto arrivata dopo l'apertura entra in dissolvenza (prima compariva di colpo sul nero).
            double now = _clock.Elapsed.TotalMilliseconds;
            if (_artAt < 0) _artAt = now < 60 ? -1000 : now;
            double artFade = Math.Clamp((now - _artAt) / 260, 0, 1);
            using (ctx.PushOpacity(artFade))
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
