using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>Lo splash di Windows: stessi asset, maschera progressiva, scala e dissolvenza finale.</summary>
internal sealed class StartupSplashView : Control
{
    private readonly Bitmap _logo = Load("logo.png"), _ghost = Load("logo_ghost.png"), _bright = Load("logo_bright.png");
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _frames;
    private Func<bool>? _ready;
    private double _shown, _lastMs, _finishAt = -1;
    private string _status = "Avvio di CinecorePlayer";
    public event Action? Finished;

    public StartupSplashView()
    {
        IsVisible = false;
        _frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
        DetachedFromVisualTree += (_, _) => _frames.Stop();
    }

    private static Bitmap Load(string name) => new(AssetLoader.Open(new Uri("avares://CinecoreMac/Splash/" + name)));

    public void Start(Func<bool> ready)
    {
        _ready = ready; _shown = 0; _lastMs = 0; _finishAt = -1;
        IsVisible = true; _clock.Restart(); _frames.Start();
    }

    public void Dismiss()
    {
        _frames.Stop(); _clock.Stop(); IsVisible = false;
    }

    private void Step()
    {
        double now = _clock.Elapsed.TotalMilliseconds, delta = Math.Clamp(now - _lastMs, 1, 100);
        _lastMs = now;
        // Il catalogo locale/rete si prepara dietro allo splash. Nessuna attesa artificiale di decoder non avviati.
        bool ready = _ready?.Invoke() == true;
        double target = ready ? 100 : 65;
        _status = ready ? "Pronto" : "Carico la libreria";
        // Come su Windows, la sfumatura attraversa anche l'ultima lettera. Limite per un server irraggiungibile.
        if (now >= 10000) { target = 100; _status = "Apro la libreria"; }
        _shown += (target - _shown) * (1 - Math.Pow(1 - (target == 100 ? .16 : .09), delta / 16.6667));
        if (Math.Abs(target - _shown) < .05) _shown = target;
        if (_shown >= 100 && now >= 1800 && _finishAt < 0) _finishAt = now + 250;
        if (_finishAt >= 0 && now - _finishAt >= 900)
        {
            Dismiss(); Finished?.Invoke(); return;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (!IsVisible || Bounds.Width < 1 || Bounds.Height < 1) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        double exit = _finishAt < 0 ? 0 : Math.Clamp((now - _finishAt) / 900, 0, 1);
        // Lo sfondo lascia emergere la libreria durante l'uscita, invece di un taglio al nero.
        using var opacity = ctx.PushOpacity(1 - exit * exit);
        ctx.FillRectangle(new SolidColorBrush(UiTheme.DefaultPanel), new Rect(Bounds.Size));
        double width = Math.Min(Math.Clamp(Bounds.Width * .48, 280, 780), Math.Max(220, (Bounds.Height - 120) * 1500 / 465));
        double entrance = 1 - Math.Pow(1 - Math.Clamp((now - 200) / 2600, 0, 1), 3);
        width *= .96 + .04 * entrance + .04 * exit * exit;
        var logo = new Rect((Bounds.Width - width) / 2, (Bounds.Height - width * 465 / 1500) / 2, width, width * 465 / 1500);
        using (ctx.PushOpacity(.16 * Math.Clamp((now - 300) / 1600, 0, 1))) ctx.DrawImage(_ghost, logo);
        double edge = 1.08 * _shown / 100;
        var mask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
            GradientStops = {
                new GradientStop(Colors.White, 0), new GradientStop(Colors.White, Math.Max(0, edge - .08)),
                new GradientStop(Colors.Transparent, edge), new GradientStop(Colors.Transparent, Math.Max(1, edge + .001)) }
        };
        using (ctx.PushOpacityMask(mask, logo)) ctx.DrawImage(_logo, logo);
        if (exit > 0)
            using (ctx.PushOpacity(.72 * Math.Sin(Math.PI * exit))) ctx.DrawImage(_bright, logo);
        double vmin = Math.Min(Bounds.Width, Bounds.Height), inset = Math.Clamp(vmin * .045, 24, 48);
        using (ctx.PushOpacity(Math.Clamp((now - 1800) / 800, 0, 1) * (1 - exit)))
            DrawText(ctx, _status, Math.Clamp(vmin * .015, 12, 15), Color.FromRgb(125, 139, 160), new Rect(28, Bounds.Height - inset - 24, Math.Max(1, Bounds.Width - 56), 24), TextAlignment.Center);
        DrawText(ctx, "Powered by madVR", 12, Color.FromArgb(204, 100, 114, 134),
            new Rect(20, Bounds.Height - Math.Max(12, inset * .45) - 18, Math.Max(1, Bounds.Width - Math.Clamp(vmin * .03, 20, 36) - 20), 18), TextAlignment.Right);
    }

    private static readonly Typeface SplashFace = new(new FontFamily("avares://CinecoreMac/Assets/Fonts#Inter Display"));

    private static void DrawText(DrawingContext ctx, string text, double size, Color color, Rect bounds, TextAlignment alignment)
    {
        // Prima il carattere si ricreava a ogni fotogramma dell'animazione.
        var ft = UiText.Get(L10n.T(text), SplashFace, size, color, bounds.Width, 0, alignment, TextTrimming.None);
        ctx.DrawText(ft, bounds.TopLeft);
    }
}
