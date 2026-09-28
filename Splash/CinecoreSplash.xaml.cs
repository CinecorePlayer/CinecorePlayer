using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CinecorePlayer2025.Splash;

public partial class CinecoreSplash : UserControl
{
    private static readonly (double Percent, string Text)[] Steps =
    [
        (0,   "Avvio di CinecorePlayer"),
        (14,  "Inizializzo il motore video"),
        (32,  "Carico decoder e codec"),
        (50,  "Rilevo display e uscita audio"),
        (68,  "Scansiono la libreria"),
        (86,  "Preparo l’interfaccia"),
        (100, "Pronto")
    ];

    // Stesso piano temporale dell'HTML: i tratti piatti sono intenzionali.
    private static readonly (double Milliseconds, double Percent)[] Plan =
    [
        (0, 0), (500, 14), (1300, 14), (2100, 38), (2800, 38),
        (3500, 57), (4300, 57), (5000, 79), (5900, 79),
        (6400, 93), (7200, 97), (7700, 100)
    ];

    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _finishCts;

    private double _target;
    private double _shown;
    private double _lastFrameMs;
    private int _step;
    private int _lastInt = -1;
    private bool _external;
    private bool _custom;
    private bool _done;
    private bool _loaded;
    private string _lastText = string.Empty;

    /// <summary>
    /// Equivalente del DEMO=true dell'HTML. Nell'app vera lascialo false.
    /// </summary>
    public bool AutoRestart { get; set; }

    // In Cinecore the last step is driven by PlayerForm.StartupReady.
    public bool UseSimulatedProgress { get; set; } = true;

    /// <summary>
    /// Scatta poco dopo il 100%,
    /// quando comincia l'animazione di uscita.
    /// </summary>
    public event EventHandler? Ready;

    public CinecoreSplash()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Restart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        CompositionTarget.Rendering -= OnRendering;
        _finishCts?.Cancel();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMetrics();

    private void UpdateLayoutMetrics()
    {
        // CSS: width: clamp(280px, 48vw, 780px)
        var width = Math.Clamp(ActualWidth * 0.48, 280.0, 780.0);

        // Se la finestra è molto bassa, evitiamo di uscire verticalmente.
        var maxByHeight = Math.Max(220.0, (ActualHeight - 120.0) * (1500.0 / 465.0));
        width = Math.Min(width, maxByHeight);

        LogoGrid.Width = width;
        LogoGrid.Height = width * 465.0 / 1500.0;

        // Keep status and renderer credit anchored to the lower edge while the
        // logo remains centered at every window size.
        var vmin = Math.Min(ActualWidth, ActualHeight);
        var bottomInset = Math.Clamp(vmin * 0.045, 24.0, 48.0);
        StatusText.Margin = new Thickness(28, 0, 28, bottomInset);
        MadVrCredit.Margin = new Thickness(0, 0, Math.Clamp(vmin * 0.03, 20.0, 36.0),
            Math.Max(12.0, bottomInset * 0.45));

        // CSS: font-size clamp(12px, 1.5vmin, 15px)
        StatusText.FontSize = Math.Clamp(vmin * 0.015, 12.0, 15.0);
    }

    /// <summary>
    /// Equivalente di CinecoreSplash.set(v, text) nell'HTML.
    /// </summary>
    public void SetProgress(double value, string? text = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetProgress(value, text));
            return;
        }

        _external = true;
        _target = Math.Clamp(value, 0.0, 100.0);
        if (_target >= 100.0)
            _shown = 100.0;

        if (text is not null)
        {
            _custom = true;
            if (!string.Equals(text, _lastText, StringComparison.Ordinal))
            {
                _lastText = text;
                Say(text);
            }
        }
    }

    /// <summary>
    /// Riavvia la splash. Equivalente di CinecoreSplash.restart().
    /// </summary>
    public void Restart()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Restart);
            return;
        }

        _finishCts?.Cancel();
        _finishCts = new CancellationTokenSource();

        CompositionTarget.Rendering -= OnRendering;
        StopAllAnimations();

        _target = 0;
        _shown = 0;
        _step = 0;
        _lastInt = -1;
        _done = false;
        _external = false;
        _custom = false;
        _lastText = string.Empty;

        LogoGrid.Opacity = 1;
        LogoScale.ScaleX = 0.96;
        LogoScale.ScaleY = 0.96;
        GhostImage.Opacity = 0;
        FlashImage.Opacity = 0;
        StatusText.Opacity = 0;
        StatusTranslate.Y = 0;
        StatusText.Text = Steps[0].Text;

        SetReveal(0);
        UpdateLayoutMetrics();
        BeginEntranceAnimations();

        _clock.Restart();
        _lastFrameMs = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAllAnimations()
    {
        LogoGrid.BeginAnimation(OpacityProperty, null);
        LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        GhostImage.BeginAnimation(OpacityProperty, null);
        FlashImage.BeginAnimation(OpacityProperty, null);
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusTranslate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void BeginEntranceAnimations()
    {
        AnimateSpline(LogoScale, ScaleTransform.ScaleXProperty, 0.96, 1.0,
            TimeSpan.FromSeconds(2.6), TimeSpan.FromSeconds(0.2), new KeySpline(0.2, 0.7, 0.2, 1));
        AnimateSpline(LogoScale, ScaleTransform.ScaleYProperty, 0.96, 1.0,
            TimeSpan.FromSeconds(2.6), TimeSpan.FromSeconds(0.2), new KeySpline(0.2, 0.7, 0.2, 1));

        var ghost = new DoubleAnimation(0, 0.16, TimeSpan.FromSeconds(1.6))
        {
            BeginTime = TimeSpan.FromSeconds(0.3),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        GhostImage.BeginAnimation(OpacityProperty, ghost);

        var status = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.8))
        {
            BeginTime = TimeSpan.FromSeconds(1.8),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        StatusText.BeginAnimation(OpacityProperty, status);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_loaded || _done) return;

        var nowMs = _clock.Elapsed.TotalMilliseconds;
        var deltaMs = _lastFrameMs <= 0 ? 16.6667 : Math.Max(0.1, nowMs - _lastFrameMs);
        _lastFrameMs = nowMs;

        if (!_external && UseSimulatedProgress)
        {
            // L'HTML imposta t0 = performance.now() + 2000.
            var simulationMs = nowMs - 2000.0;
            _target = simulationMs < 0 ? 0 : Interpolate(simulationMs);
        }

        // HTML originale: shown += (target - shown) * 0.09 ad ogni frame.
        // Qui la normalizziamo sul tempo per mantenere la stessa sensazione anche oltre/sotto i 60 Hz.
        var smoothing = 1.0 - Math.Pow(1.0 - 0.09, deltaMs / 16.6667);
        _shown += (_target - _shown) * smoothing;
        if (_target - _shown < 0.05)
            _shown = _target;

        var p = Math.Min(100.0, _shown);
        SetReveal(p);

        var n = (int)Math.Round(p);
        if (n != _lastInt)
            _lastInt = n;

        if (!_custom)
            SetStep(p);

        if (p >= 100 && !_done)
        {
            _done = true;
            CompositionTarget.Rendering -= OnRendering;
            _ = FinishAsync(_finishCts!.Token);
        }
    }

    private static double Interpolate(double t)
    {
        for (var i = 1; i < Plan.Length; i++)
        {
            if (t <= Plan[i].Milliseconds)
            {
                var (a, x) = Plan[i - 1];
                var (b, y) = Plan[i];
                return x + (y - x) * (t - a) / (b - a);
            }
        }
        return 100;
    }

    private void SetReveal(double progress)
    {
        // HTML: --e = 108 * p / 100; feather di circa 8%.
        var edge = 1.08 * Math.Clamp(progress, 0, 100) / 100.0;
        var solid = edge - 0.08;

        if (progress <= 0)
        {
            MaskStart.Offset = 0;
            MaskSolidEnd.Offset = 0;
            MaskFadeEnd.Offset = 0;
            MaskEnd.Offset = 1;
            return;
        }

        if (edge >= 1.0)
        {
            // A 100% il logo deve essere interamente visibile, senza coda trasparente a destra.
            MaskStart.Offset = 0;
            MaskSolidEnd.Offset = 1;
            MaskFadeEnd.Offset = 1;
            MaskEnd.Offset = 1;
            return;
        }

        MaskStart.Offset = 0;
        MaskSolidEnd.Offset = Math.Clamp(solid, 0, 1);
        MaskFadeEnd.Offset = Math.Clamp(edge, 0, 1);
        MaskEnd.Offset = 1;
    }

    private void SetStep(double progress)
    {
        var k = 0;
        for (var i = 0; i < Steps.Length; i++)
            if (progress >= Steps[i].Percent)
                k = i;

        if (k == _step) return;
        _step = k;
        Say(Steps[k].Text);
    }

    private void Say(string text)
    {
        StatusText.Text = text;

        StatusText.BeginAnimation(OpacityProperty, null);
        StatusTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        StatusText.Opacity = 0;
        StatusTranslate.Y = 4;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.45))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        var move = new DoubleAnimation(4, 0, TimeSpan.FromSeconds(0.45))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        StatusText.BeginAnimation(OpacityProperty, fade);
        StatusTranslate.BeginAnimation(TranslateTransform.YProperty, move);
    }

    private async Task FinishAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            token.ThrowIfCancellationRequested();

            BeginExitAnimations();
            Ready?.Invoke(this, EventArgs.Empty);

            if (AutoRestart)
            {
                await Task.Delay(2400, token);
                token.ThrowIfCancellationRequested();
                Restart();
            }
        }
        catch (OperationCanceledException)
        {
            // Restart o unload: intenzionale.
        }
    }

    private void BeginExitAnimations()
    {
        // CSS: exit .9s cubic-bezier(.5,0,.8,.4), scale 1 -> 1.04, opacity 1 -> 0.
        AnimateSpline(LogoGrid, OpacityProperty, 1, 0,
            TimeSpan.FromSeconds(0.9), TimeSpan.Zero, new KeySpline(0.5, 0, 0.8, 0.4));
        AnimateSpline(LogoScale, ScaleTransform.ScaleXProperty, 1, 1.04,
            TimeSpan.FromSeconds(0.9), TimeSpan.Zero, new KeySpline(0.5, 0, 0.8, 0.4));
        AnimateSpline(LogoScale, ScaleTransform.ScaleYProperty, 1, 1.04,
            TimeSpan.FromSeconds(0.9), TimeSpan.Zero, new KeySpline(0.5, 0, 0.8, 0.4));

        // Simula il brightness(1 -> 1.3) dell'HTML con un asset leggermente sovraesposto.
        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.9) };
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.72, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.27))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.9))));
        FlashImage.BeginAnimation(OpacityProperty, flash);

        var fadeStatus = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        StatusText.BeginAnimation(OpacityProperty, fadeStatus);
    }

    private static void AnimateSpline(
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        TimeSpan duration,
        TimeSpan beginTime,
        KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = beginTime,
            Duration = duration,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration), spline));

        switch (target)
        {
            case Animatable animatable:
                animatable.BeginAnimation(property, animation);
                break;
            case UIElement element:
                element.BeginAnimation(property, animation);
                break;
            default:
                throw new ArgumentException($"{target.GetType().Name} non supporta animazioni WPF.", nameof(target));
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Come nell'HTML demo: click = restart.
        if (AutoRestart) Restart();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (AutoRestart && e.Key == Key.R)
            Restart();
    }
}
