using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Menu del tasto destro › Vai a…: si scrive un minutaggio e il film salta li'.
/// Accetta 1:20:00, 80:00, 80 (minuti), 1h20, 1h 20m 5s, 90s, 50% e spostamenti relativi (+5m, -30s).
/// </summary>
internal sealed class GoToTimeView : Control
{
    private static readonly FontFamily Inter = new("avares://CinecoreMac/Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    public event Action? Closed;
    public event Action<double>? TimeChosen;

    private readonly TextBox _box;
    private string _note = "";
    private double _position, _duration;
    private Rect _sheet, _boxRect, _go, _close;
    private Point _mouse;
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private DateTime _openedAt;

    public GoToTimeView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        _box = new TextBox { Watermark = "1:20:00" };
        WizardSheet.StyleBox(_box);
        _box.GotFocus += (_, _) => InvalidateVisual();
        _box.LostFocus += (_, _) => InvalidateVisual();
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Submit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        _box.TextChanged += (_, _) => { if (_note.Length > 0) { _note = ""; InvalidateVisual(); } };
        VisualChildren.Add(_box);
        LogicalChildren.Add(_box);
    }

    public bool IsOpen => IsVisible;

    public void Open(double position, double duration)
    {
        _position = Math.Max(0, position); _duration = Math.Max(0, duration);
        _note = "";
        _box.Text = Format(_position, _duration);
        _box.Foreground = B(UiTheme.Text); _box.CaretBrush = B(UiTheme.Text);
        _box.Resources["TextControlPlaceholderForeground"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
        _box.Resources["TextControlPlaceholderForegroundFocused"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
        _box.SelectionBrush = B(UiTheme.WithAlpha(UiTheme.Text, 70));
        _openedAt = DateTime.UtcNow;
        IsVisible = true;
        InvalidateMeasure(); InvalidateVisual();
        // Il tempo attuale e' gia' selezionato: si scrive sopra.
        Dispatcher.UIThread.Post(() => { _box.Focus(); _box.SelectAll(); }, DispatcherPriority.Input);
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Closed?.Invoke();
    }

    private void Submit()
    {
        if (Parse(_box.Text ?? "", _position, _duration) is not { } seconds)
        {
            _note = "Non capisco: prova 1:20:00, 80 (minuti), 1h20 o 50%";
            InvalidateVisual();
            return;
        }
        if (_duration > 0 && seconds > _duration)
        {
            _note = "Il titolo dura " + Format(_duration, _duration);
            InvalidateVisual();
            return;
        }
        Close();
        TimeChosen?.Invoke(Math.Max(0, seconds));
    }

    public void TestSubmit(string text) { _box.Text = text; Submit(); }

    private static readonly Regex Units = new(@"^(?:(\d+(?:\.\d+)?)h)?(?:(\d+(?:\.\d+)?)m(?:in)?)?(?:(\d+(?:\.\d+)?)s)?$", RegexOptions.IgnoreCase);

    /// <summary>Secondi dal testo scritto; null se non si capisce.</summary>
    public static double? Parse(string text, double position, double duration)
    {
        string t = text.Trim().Replace(" ", "").Replace(',', '.');
        if (t.Length == 0) return null;
        int sign = 0;
        if (t[0] is '+' or '-') { sign = t[0] == '+' ? 1 : -1; t = t[1..]; if (t.Length == 0) return null; }
        double? value = null;
        var inv = CultureInfo.InvariantCulture;
        if (t.EndsWith('%'))
        {
            if (sign != 0 || duration <= 0 || !double.TryParse(t[..^1], NumberStyles.Float, inv, out double pct) || pct < 0 || pct > 100) return null;
            return duration * pct / 100;
        }
        if (t.Contains(':'))
        {
            // 1:20:00 = ore:minuti:secondi, 80:00 = minuti:secondi.
            string[] parts = t.Split(':');
            if (parts.Length > 3) return null;
            double total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, inv, out double v) || v < 0) return null;
                if (i > 0 && v >= 60) return null;
                total = total * 60 + v;
            }
            value = total;
        }
        else if (double.TryParse(t, NumberStyles.Float, inv, out double minutes) && minutes >= 0)
            value = minutes * 60; // un numero da solo sono minuti
        else if (Units.Match(t) is { Success: true } m && (m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success))
        {
            // "1h20" = 1 ora e 20 minuti: un numero senza unita' dopo le ore vale minuti.
            value = (m.Groups[1].Success ? double.Parse(m.Groups[1].Value, inv) * 3600 : 0)
                  + (m.Groups[2].Success ? double.Parse(m.Groups[2].Value, inv) * 60 : 0)
                  + (m.Groups[3].Success ? double.Parse(m.Groups[3].Value, inv) : 0);
        }
        else if (Regex.Match(t, @"^(\d+(?:\.\d+)?)h(\d+)$", RegexOptions.IgnoreCase) is { Success: true } hm)
            value = double.Parse(hm.Groups[1].Value, inv) * 3600 + double.Parse(hm.Groups[2].Value, inv) * 60;
        if (value is not { } v2) return null;
        return sign == 0 ? v2 : Math.Max(0, position + sign * v2);
    }

    private static string Format(double s, double duration)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(s)));
        return duration >= 3600 || ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static double P(double pt) => pt * 96 / 72 * 0.92;

    private static void Line(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, P(pt), color, r.Width, 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private void LayoutRects() => LayoutRects(Bounds.Size);

    private double _u = 1;
    private Rect _column;

    private void LayoutRects(Size size)
    {
        // Alla prima disposizione la misura puo' essere ancora zero: mai rettangoli negativi.
        if (size.Width < 200 || size.Height < 200) size = new Size(Math.Max(size.Width, 640), Math.Max(size.Height, 480));
        _u = WizardSheet.Scale(size);
        _column = WizardSheet.Column(size);
        _sheet = new Rect(size);
        _boxRect = new Rect(_column.X, _column.Y + 200 * _u, Math.Min(_column.Width, 420 * _u), 64 * _u);
        (_go, _) = WizardSheet.Buttons(_column, _boxRect.Bottom + 48 * _u, _u, "Vai", null);
        _close = WizardSheet.Cancel(size);
        _box.FontSize = 34 * _u;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _box.Measure(new Size(Math.Max(1, _boxRect.Width), Math.Max(1, _boxRect.Height)));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutRects(finalSize);
        _box.Arrange(WizardSheet.BoxIn(_boxRect, 34 * _u * 1.4, _u));
        return finalSize;
    }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        LayoutRects();
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 380, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        byte alpha = (byte)Math.Round(255 * e);
        double slide = Math.Round(26 * _u * (1 - e));
        WizardSheet.DrawVeil(ctx, new Rect(Bounds.Size), e);
        WizardSheet.DrawCancel(ctx, _close, Hot(_close), _u);
        string hint = _duration > 0 ? "Il titolo dura " + Format(_duration, _duration) + ". Va bene anche 80 (minuti), 1h20, 50% o +5m." : "Va bene anche 80 (minuti), 1h20, +5m o -30s.";
        WizardSheet.DrawHeader(ctx, _column, _u, 0, 1, "Dove andiamo?", hint, alpha, slide);
        WizardSheet.DrawUnderline(ctx, _boxRect, _box.IsFocused, _note.Length > 0, alpha, _u);
        WizardSheet.DrawError(ctx, _boxRect.WithWidth(_column.Width), _note, _u);
        WizardSheet.DrawNext(ctx, _go, "Vai", Hot(_go), true, alpha, _u);
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        e.Handled = true;
        if (_go.Contains(p)) Submit();
        else if (_close.Contains(p)) Close();
        else if (_boxRect.Contains(p)) _box.Focus();
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
    }
}
