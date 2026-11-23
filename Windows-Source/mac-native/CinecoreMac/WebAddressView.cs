using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Apri › Indirizzo web… (pagina "URL" della libreria di Windows): un link di YouTube o di un altro sito, oppure un
/// flusso diretto. Se negli appunti c'e' gia' un indirizzo, la casella parte con quello.
/// </summary>
internal sealed class WebAddressView : Control
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);
    
    public event Action? Closed;
    public event Action<string>? AddressChosen;

    private readonly TextBox _box;
    private string _note = "";
    private Rect _sheet, _boxRect, _open, _close;
    private Point _mouse;
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private DateTime _openedAt;

    public WebAddressView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        _box = new TextBox { Watermark = "https://www.youtube.com/watch?v=…" };
        WizardSheet.StyleBox(_box);
        _box.GotFocus += (_, _) => InvalidateVisual();
        _box.LostFocus += (_, _) => InvalidateVisual();
        _box.TextChanged += (_, _) => { if (_note.Length > 0) { _note = ""; InvalidateVisual(); } };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Submit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        VisualChildren.Add(_box);
        LogicalChildren.Add(_box);
    }

    public bool IsOpen => IsVisible;

    public async void Open()
    {
        _note = "";
        _box.Text = "";
        _box.Foreground = B(UiTheme.Text); _box.CaretBrush = B(UiTheme.Text);
        _box.Resources["TextControlPlaceholderForeground"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
        _box.Resources["TextControlPlaceholderForegroundFocused"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
        _box.SelectionBrush = B(UiTheme.WithAlpha(UiTheme.Text, 70));
        _openedAt = DateTime.UtcNow;
        IsVisible = true;
        InvalidateMeasure(); InvalidateVisual();
        Dispatcher.UIThread.Post(() => _box.Focus(), DispatcherPriority.Input);
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.GetTextAsync() is { } text && MacWebResolver.IsWebAddress(text))
            {
                _box.Text = text.Trim();
                _box.CaretIndex = _box.Text.Length;
            }
        }
        catch { }
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Closed?.Invoke();
    }

    private void Submit()
    {
        string text = (_box.Text ?? "").Trim();
        if (!MacWebResolver.IsWebAddress(text)) { _note = "Scrivi un indirizzo che inizia con https://"; InvalidateVisual(); return; }
        Close();
        AddressChosen?.Invoke(text);
    }

    public void TestSubmit(string text) { _box.Text = text; Submit(); }

    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static double P(double pt) => pt * 96 / 72 * 0.92;

    private static void Line(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, P(pt), color, r.Width, 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private void LayoutRects() => LayoutRects(Bounds.Size);

    private double _u = 1, _controlTop;
    private Rect _column;

    // Dalla misura ricevuta (al primo posizionamento Bounds e' ancora vuoto: la casella usciva con larghezza negativa
    // e Avalonia andava in crash, "Apri link").
    private void LayoutRects(Size size)
    {
        if (size.Width < 200 || size.Height < 200) size = new Size(Math.Max(size.Width, 640), Math.Max(size.Height, 480));
        _u = WizardSheet.Scale(size);
        _column = WizardSheet.Column(size);
        _sheet = new Rect(size);
        _controlTop = _column.Y + 200 * _u;
        _boxRect = new Rect(_column.X, _controlTop, _column.Width, 64 * _u);
        (_open, _) = WizardSheet.Buttons(_column, _boxRect.Bottom + 48 * _u, _u, "Apri", null);
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
        LayoutRects();
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 380, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        byte alpha = (byte)Math.Round(255 * e);
        double slide = Math.Round(26 * _u * (1 - e));
        WizardSheet.DrawVeil(ctx, new Rect(Bounds.Size), e);
        WizardSheet.DrawCancel(ctx, _close, Hot(_close), _u);
        WizardSheet.DrawHeader(ctx, _column, _u, 0, 1, "Quale indirizzo apriamo?", "YouTube, un altro sito video o un flusso diretto (.m3u8, .mp4…).", alpha, slide);
        WizardSheet.DrawUnderline(ctx, _boxRect, _box.IsFocused, _note.Length > 0, alpha, _u);
        WizardSheet.DrawError(ctx, _boxRect, _note, _u);
        WizardSheet.DrawNext(ctx, _open, "Apri", Hot(_open), true, alpha, _u);
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        e.Handled = true;
        if (_open.Contains(p)) Submit();
        else if (_close.Contains(p)) Close();
        else if (_boxRect.Contains(p)) _box.Focus();
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
    }
}
