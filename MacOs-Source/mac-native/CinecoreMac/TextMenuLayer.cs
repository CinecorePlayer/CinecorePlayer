using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// La lista di testo dei menu della libreria (TextMenuOverlay di Windows) per i livelli che non sono la libreria:
/// Trakt, Analisi HDR, Correggi titolo. Pagina velata, voci in colonna che entrano scalate, la scelta in pieno,
/// le altre tenui; una voce con sottomenu apre la sua lista al posto della prima. Niente menu a tendina in vetro.
/// </summary>
internal sealed class TextMenuLayer : Control
{
    private static readonly FontFamily Display = new("avares://CinecoreMac/Assets/Fonts#Inter Display");
    private static readonly Typeface Regular = new(Display, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface Semibold = new(Display, FontStyle.Normal, FontWeight.SemiBold);
    private const double InMs = 190, OutMs = 150, Stagger = 25;

    private sealed record Item(string Label, bool Selected, Action Run);
    private List<Item>? _items;
    private Point _anchor;
    /// <summary>Menu aperto a destra (tre puntini): le voci finiscono tutte sul bordo destro, sotto il pulsante.</summary>
    private bool _alignRight;
    private double _scroll;
    private int _hover = -1, _key = -1;
    private DateTime _openAt, _closeAt;
    private Action? _after;
    private readonly DispatcherTimer _frames;

    public event Action? Closed;
    public bool IsOpen => _items != null;

    public TextMenuLayer()
    {
        IsVisible = false;
        Focusable = true;
        ClipToBounds = true;
        _frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
    }

    /// <summary>La misura del livello; prima della prima impaginazione quella della finestra che lo contiene.</summary>
    private Size Area => Bounds.Width > 0 ? Bounds.Size : (Parent as Visual)?.Bounds.Size ?? Bounds.Size;
    private double Scale => Math.Clamp(Math.Min(Area.Width / 1920, Area.Height / 1080), .5, 3);
    private double Pt => Math.Max(10, 13.6 * Scale);
    private double RowH => Math.Round(Pt * 96 / 72 * 1.95);

    /// <param name="alignRight">Il punto e' il bordo destro dell'elenco (menu dei tre puntini in alto a destra).</param>
    public void Show(IReadOnlyList<MenuEntry> entries, Point at, bool alignRight = false)
    {
        var items = new List<Item>();
        Point origin = at; // i sottomenu (Apri con) si aprono nello stesso punto e con lo stesso allineamento
        foreach (var entry in entries)
        {
            if (entry.Separator || !entry.Visible || !entry.Enabled || string.IsNullOrEmpty(entry.Text)) continue;
            if (entry.Children is { Count: > 0 } children)
                items.Add(new Item(entry.Text + "  ›", entry.Checked, () => DispatcherTimer.RunOnce(() => Show(children, origin, alignRight), TimeSpan.FromMilliseconds(180))));
            else if (entry.Run is { } run)
                items.Add(new Item(entry.Text + (string.IsNullOrEmpty(entry.Value) ? "" : "   " + entry.Value), entry.Checked, run));
        }
        if (items.Count == 0) return;
        _items = items; _hover = -1; _key = -1; _after = null; _closeAt = default;
        double w = items.Max(i => Measure(i.Label, Semibold));
        double margin = Math.Max(12, RowH / 2);
        _alignRight = alignRight;
        if (alignRight) at = new Point(at.X - w, at.Y);
        int visible = VisibleRows(margin);
        // Prima della prima apertura il livello non e' mai stato impaginato (misura zero) e il menu finiva sempre
        // nell'angolo in alto a sinistra: vale la misura della finestra.
        var area = Area;
        _anchor = new Point(Math.Clamp(at.X, margin, Math.Max(margin, area.Width - w - margin)),
                            Math.Clamp(at.Y, margin, Math.Max(margin, area.Height - visible * RowH - margin)));
        int selected = items.FindIndex(i => i.Selected);
        _scroll = selected >= visible ? selected - visible + 1 : 0;
        _openAt = DateTime.UtcNow;
        IsVisible = true;
        Focus();
        _frames.Start();
        InvalidateVisual();
    }

    public void Close(Action? after = null)
    {
        if (_items == null || _closeAt != default) return;
        _after = after; _closeAt = DateTime.UtcNow;
        _frames.Start();
    }

    private int VisibleRows(double margin) => _items == null ? 0
        : (int)Math.Min(_items.Count, Math.Max(1, Math.Floor((Area.Height - 2 * margin) / RowH)));

    private int Rows => _items == null ? 0
        : (int)Math.Min(_items.Count, Math.Max(1, Math.Floor((Area.Height - _anchor.Y - Math.Max(12, RowH / 2)) / RowH)));

    private Rect Row(int i)
    {
        double w = _items!.Max(item => Measure(item.Label, Semibold));
        return new Rect(_anchor.X - 14, _anchor.Y + (i - _scroll) * RowH, w + 28, RowH);
    }

    private double Progress(int index)
    {
        if (_closeAt != default) return 1 - Math.Clamp((DateTime.UtcNow - _closeAt).TotalMilliseconds / OutMs, 0, 1);
        return Math.Clamp(((DateTime.UtcNow - _openAt).TotalMilliseconds - index * Stagger) / InMs, 0, 1);
    }

    private void Step()
    {
        InvalidateVisual();
        if (_items == null) { _frames.Stop(); return; }
        if (_closeAt != default && (DateTime.UtcNow - _closeAt).TotalMilliseconds >= OutMs)
        {
            _frames.Stop();
            var after = _after;
            _items = null; _after = null;
            IsVisible = false;
            Closed?.Invoke();
            after?.Invoke();
        }
        else if (_closeAt == default && (DateTime.UtcNow - _openAt).TotalMilliseconds >= InMs + _items.Count * Stagger) _frames.Stop();
    }

    public override void Render(DrawingContext ctx)
    {
        if (_items == null) return;
        var page = new Rect(Bounds.Size);
        double veil = _closeAt != default ? Progress(0) : Math.Clamp((DateTime.UtcNow - _openAt).TotalMilliseconds / InMs, 0, 1);
        veil = 1 - (1 - veil) * (1 - veil);
        ctx.FillRectangle(new SolidColorBrush(UiTheme.WithAlpha(UiTheme.Panel, (UiTheme.IsLight ? 228 : 222) * veil)), page);
        int rows = Rows;
        for (int i = (int)_scroll; i < Math.Min(_items.Count, _scroll + rows); i++)
        {
            double eased = 1 - Math.Pow(1 - Progress(i - (int)_scroll), 3);
            if (eased <= 0.01) continue;
            var r = Row(i);
            bool strong = _items[i].Selected || i == _hover || i == _key;
            var ft = Format(_items[i].Label, strong ? Semibold : Regular, UiTheme.WithAlpha(UiTheme.Text, (strong ? 255 : 150) * eased));
            ctx.DrawText(ft, new Point(_alignRight ? r.Right - 14 - ft.Width : r.X + 14, r.Y + Math.Round(10 * (1 - eased)) + (r.Height - ft.Height) / 2));
        }
        // Piu' voci di quante ne stanno: una freccia tenue dice che si scorre.
        if (_scroll + rows < _items.Count || _scroll > 0)
        {
            var hint = Format(_scroll + rows < _items.Count ? "⌄" : "⌃", Regular, UiTheme.WithAlpha(UiTheme.Text, 120));
            var last = Row((int)_scroll + rows - 1);
            ctx.DrawText(hint, new Point(last.X + 14, last.Bottom));
        }
    }

    private CachedText Format(string text, Typeface face, Color color) => UiText.Get(L10n.T(text), face, Pt * 96 / 72, color);

    private double Measure(string text, Typeface face) => Format(text, face, Colors.White).WidthIncludingTrailingWhitespace;

    private int Hit(Point p)
    {
        if (_items == null) return -1;
        int rows = Rows;
        for (int i = (int)_scroll; i < Math.Min(_items.Count, _scroll + rows); i++) if (Row(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int h = Hit(e.GetPosition(this));
        if (h != _hover) { _hover = h; _key = -1; InvalidateVisual(); }
        Cursor = h >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        if (_items == null || _closeAt != default) return;
        int i = Hit(e.GetPosition(this));
        if (i >= 0) { var run = _items[i].Run; Close(run); } else Close();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if (_items == null) return;
        _scroll = Math.Clamp(Math.Round(_scroll - e.Delta.Y), 0, Math.Max(0, _items.Count - Rows));
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items == null) return;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Down: case Key.Up:
                int start = _key >= 0 ? _key : _hover >= 0 ? _hover : _items.FindIndex(i => i.Selected);
                _key = Math.Clamp(start < 0 ? 0 : start + (e.Key == Key.Down ? 1 : -1), 0, _items.Count - 1);
                if (_key < _scroll) _scroll = _key;
                else if (_key >= _scroll + Rows) _scroll = _key - Rows + 1;
                InvalidateVisual();
                break;
            case Key.Enter: case Key.Space:
                if (_key >= 0) { var run = _items[_key].Run; Close(run); }
                break;
        }
    }
}
