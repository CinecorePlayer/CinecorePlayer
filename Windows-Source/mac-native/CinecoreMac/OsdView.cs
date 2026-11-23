using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Messaggi a schermo (RemoteOsd di Windows, PlayerForm.HudInfrastructure.cs): una capsula scura piena, centrata in
/// alto (7% dell'altezza), con icona facoltativa e testo bianco. Scende dall'alto, resta il tempo chiesto e risale.
/// Si usa per "Non riesco ad aprire questo titolo" e per le risposte al telecomando.
/// </summary>
internal sealed class OsdView : Control
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Color Capsule = Color.FromRgb(18, 21, 26);
    private const double EnterMs = 260, ExitMs = 220;

    private string _text = "";
    private string? _icon;
    private readonly Stopwatch _clock = new();
    private double _showMs, _exitAt = -1;
    private readonly DispatcherTimer _frames;

    public OsdView()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        _frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
        _frames.Stop(); // parte con Show
    }

    public void Show(string text, int ms = 2500, string? icon = null)
    {
        _text = text; _icon = icon; _showMs = ms; _exitAt = -1;
        _clock.Restart();
        IsVisible = true;
        _frames.Start();
        InvalidateVisual();
    }

    private void Step()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_exitAt < 0 && now >= EnterMs + _showMs) _exitAt = now;
        if (_exitAt >= 0 && now - _exitAt >= ExitMs) { IsVisible = false; _frames.Stop(); return; }
        if (now < EnterMs + 20 || _exitAt >= 0) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (_text.Length == 0 && _icon == null) return;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 10) return;
        // Come Windows: misure in pixel scalate su 1080 righe, qui riportate in punti.
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double k = Math.Clamp(h * scaling / 1080, .85, 3.2) / scaling;
        double now = _clock.Elapsed.TotalMilliseconds;
        double enter = Math.Clamp(now / EnterMs, 0, 1), exit = _exitAt < 0 ? 0 : Math.Clamp((now - _exitAt) / ExitMs, 0, 1);
        double easeOut = 1 - Math.Pow(1 - enter, 3), easeIn = exit * exit * exit;

        var text = UiText.Get(L10n.T(_text), Semibold, 16 * k, Colors.White, Math.Max(40, w - 140 * k));
        double height = Math.Round(54 * k), pad = Math.Round(24 * k), gap = Math.Round(16 * k);
        var icon = _icon != null ? SvgIcon.Get(_icon) : null;
        double iconSize = icon != null ? Math.Round(24 * k) : 0;
        double width = pad + (icon != null ? iconSize + gap : 0) + text.Width + pad;
        width = Math.Min(width, Math.Max(height, w - 80 * k));
        var pill = new Rect(Math.Round((w - width) / 2), Math.Round(h * .07), width, height);
        double hidden = -pill.Bottom - 12 * k;
        double slide = Math.Round(hidden * (1 - easeOut) + hidden * easeIn);
        pill = pill.Translate(new Vector(0, slide));
        if (pill.Bottom <= 0) return;
        ctx.DrawRectangle(new SolidColorBrush(Capsule), null, pill, height / 2, height / 2);
        double x = pill.X + pad, centre = pill.Y + pill.Height / 2;
        if (icon != null)
        {
            icon.Draw(ctx, new Rect(x, centre - iconSize / 2, iconSize, iconSize), Colors.White);
            x += iconSize + gap;
        }
        ctx.DrawText(text, new Point(x, centre - text.Height / 2));
    }
}
