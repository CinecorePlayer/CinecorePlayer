using System;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Impostazioni › Dispositivi › Luci WLED (WledSettingsForm di Windows) nello stile delle schede nuove (WizardSheet):
/// indirizzo con lo stato del collegamento verificato dal vivo mentre si scrive, interruttore, Salva / Annulla.
/// </summary>
internal sealed class WledView : Control
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    public event Action? Closed;

    private readonly TextBox _box;
    private readonly DispatcherTimer _typing;
    private CancellationTokenSource? _probeCts;
    private enum Link { Empty, Checking, Connected, Unreachable }
    private Link _link = Link.Empty;
    private MacWled.Probe _last;
    private bool _enabled;
    private Rect _boxRect, _save, _close, _toggle, _test, _column;
    private Point _mouse;
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private DateTime _openedAt;
    private double _u = 1;

    public WledView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        _box = new TextBox { Watermark = "wled.local   ·   192.168.1.50" };
        WizardSheet.StyleBox(_box);
        _typing = new DispatcherTimer(TimeSpan.FromMilliseconds(550), DispatcherPriority.Background, (_, _) => { _typing!.Stop(); StartProbe(); });
        _box.GotFocus += (_, _) => InvalidateVisual();
        _box.LostFocus += (_, _) => InvalidateVisual();
        _box.TextChanged += (_, _) =>
        {
            _link = (_box.Text ?? "").Trim().Length == 0 ? Link.Empty : Link.Checking;
            _typing.Stop(); _typing.Start();
            InvalidateVisual();
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Save(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        VisualChildren.Add(_box);
        LogicalChildren.Add(_box);
    }

    public bool IsOpen => IsVisible;

    public void Open()
    {
        _enabled = MacWled.Enabled;
        _box.Text = MacWled.Address;
        _box.Foreground = B(UiTheme.Text); _box.CaretBrush = B(UiTheme.Text);
        _box.Resources["TextControlPlaceholderForeground"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
        _box.Resources["TextControlPlaceholderForegroundFocused"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
        _box.SelectionBrush = B(UiTheme.WithAlpha(UiTheme.Text, 70));
        _openedAt = DateTime.UtcNow;
        IsVisible = true;
        InvalidateMeasure(); InvalidateVisual();
        Dispatcher.UIThread.Post(() => { _box.Focus(); _box.CaretIndex = (_box.Text ?? "").Length; }, DispatcherPriority.Input);
        StartProbe();
    }

    public void Close()
    {
        if (!IsVisible) return;
        _typing.Stop();
        _probeCts?.Cancel();
        IsVisible = false;
        Closed?.Invoke();
    }

    private void Save()
    {
        string address = (_box.Text ?? "").Trim();
        if (address.Length > 0 && MacWled.Normalize(address) == null) { _link = Link.Unreachable; InvalidateVisual(); return; }
        bool wasEnabled = MacWled.Enabled;
        MacWled.Address = address;
        MacWled.Enabled = _enabled && address.Length > 0;
        if (wasEnabled && !MacWled.Enabled) MacWled.Disabled();
        Close();
    }

    private async void StartProbe()
    {
        _probeCts?.Cancel();
        string address = (_box.Text ?? "").Trim();
        if (address.Length == 0) { _link = Link.Empty; InvalidateVisual(); return; }
        var cts = _probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        _link = Link.Checking;
        InvalidateVisual();
        MacWled.Probe result = default;
        try { result = await MacWled.ProbeAsync(address, cts.Token); } catch { }
        if (!ReferenceEquals(_probeCts, cts) || !IsVisible) return;
        _last = result;
        _link = result.Reachable ? Link.Connected : Link.Unreachable;
        InvalidateVisual();
    }

    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static double P(double pt) => pt * 96 / 72 * 0.92;

    private static void Line(DrawingContext ctx, string text, double px, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, px, color, r.Width, 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private void LayoutRects(Size size)
    {
        if (size.Width < 200 || size.Height < 200) size = new Size(Math.Max(size.Width, 640), Math.Max(size.Height, 480));
        _u = WizardSheet.Scale(size);
        _column = WizardSheet.Column(size);
        double top = _column.Y + 200 * _u;
        _boxRect = new Rect(_column.X, top, _column.Width, 64 * _u);
        _test = new Rect(_column.X, _boxRect.Bottom + 14 * _u, _column.Width, 30 * _u);
        _toggle = new Rect(_column.X, _test.Bottom + 34 * _u, _column.Width, 56 * _u);
        (_save, _) = WizardSheet.Buttons(_column, _toggle.Bottom + 40 * _u, _u, "Salva", null);
        _close = WizardSheet.Cancel(size);
        _box.FontSize = 26 * _u;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _box.Measure(new Size(Math.Max(1, _boxRect.Width), Math.Max(1, _boxRect.Height)));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutRects(finalSize);
        _box.Arrange(WizardSheet.BoxIn(_boxRect, 26 * _u * 1.5, _u));
        return finalSize;
    }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        LayoutRects(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 380, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        byte alpha = (byte)Math.Round(255 * e);
        double slide = Math.Round(26 * _u * (1 - e));
        WizardSheet.DrawVeil(ctx, new Rect(Bounds.Size), e);
        WizardSheet.DrawCancel(ctx, _close, Hot(_close), _u);
        WizardSheet.DrawHeader(ctx, _column, _u, 0, 1, "Luci WLED",
            "Si spengono quando parte il film e tornano com’erano in pausa e alla fine.", alpha, slide);
        WizardSheet.DrawUnderline(ctx, _boxRect, _box.IsFocused, _link == Link.Unreachable, alpha, _u);

        // Stato del collegamento: punto colorato e una riga; a destra "Prova di nuovo".
        (Color dot, string status) = _link switch
        {
            Link.Checking => (UiTheme.Accent, "Verifica in corso…"),
            Link.Connected => (Color.FromRgb(72, 199, 142), _last.On
                ? $"Collegato · luci accese al {Math.Round(_last.Brightness * 100 / 255.0)}%"
                : "Collegato · luci spente"),
            Link.Unreachable => (Color.FromRgb(255, 76, 92), "Nessuna risposta: controlla l’indirizzo e che il WLED sia acceso sulla stessa rete."),
            _ => (UiTheme.Muted, "Scrivi l’indirizzo del controller WLED."),
        };
        double d = 8 * _u;
        ctx.DrawEllipse(B(UiTheme.WithAlpha(dot, alpha)), null, new Rect(_test.X, _test.Center.Y - d / 2, d, d));
        string retry = "Prova di nuovo";
        double retryW = WizardSheet.Measure(retry, 15 * _u, WizardSheet.Semibold) + 8;
        var retryRect = new Rect(_test.Right - retryW, _test.Y, retryW, _test.Height);
        Line(ctx, status, 15 * _u, WizardSheet.Regular, WizardSheet.Soft(alpha), new Rect(_test.X + d + 12 * _u, _test.Y, _test.Width - retryW - d - 24 * _u, _test.Height));
        if (_link != Link.Empty)
            Line(ctx, retry, 15 * _u, WizardSheet.Semibold, Hot(retryRect) ? WizardSheet.Ink(alpha) : WizardSheet.Soft(alpha), retryRect, TextAlignment.Right);
        _retry = _link != Link.Empty ? retryRect : default;

        // Interruttore come nelle Impostazioni.
        Line(ctx, "Spegni le luci durante il film", 20 * _u, WizardSheet.Regular, WizardSheet.Ink(alpha), new Rect(_toggle.X, _toggle.Y, _toggle.Width - 80 * _u, _toggle.Height));
        double tw = 52 * _u, th = 30 * _u;
        var track = new Rect(_toggle.Right - tw, _toggle.Center.Y - th / 2, tw, th);
        Color on = UiTheme.IsLight ? Color.FromRgb(24, 26, 30) : Color.FromRgb(245, 246, 248);
        ctx.DrawRectangle(B(_enabled ? on : UiTheme.WithAlpha(UiTheme.Text, 50)), null, track, th / 2, th / 2);
        double knob = th - 6 * _u;
        var knobRect = new Rect(_enabled ? track.Right - knob - 3 * _u : track.X + 3 * _u, track.Y + 3 * _u, knob, knob);
        ctx.DrawEllipse(B(_enabled ? (UiTheme.IsLight ? Colors.White : Color.FromRgb(20, 22, 26)) : Colors.White), null, knobRect);

        WizardSheet.DrawNext(ctx, _save, "Salva", Hot(_save), true, alpha, _u);
    }

    private Rect _retry;

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        e.Handled = true;
        if (_save.Contains(p)) Save();
        else if (_close.Contains(p)) Close();
        else if (_retry.Contains(p)) StartProbe();
        else if (_toggle.Contains(p)) { _enabled = !_enabled; InvalidateVisual(); }
        else if (_boxRect.Contains(p)) _box.Focus();
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Save(); e.Handled = true; }
    }
}
