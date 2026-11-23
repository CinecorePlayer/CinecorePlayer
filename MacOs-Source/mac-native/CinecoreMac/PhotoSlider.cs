using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// Righello delle regolazioni delle foto (al posto del cursore di Fluent, con il pomello enorme). Valori da -100 a 100;
/// l'indicatore resta fermo al centro e si trascina la scala; rotella e trackpad la fanno scorrere; doppio clic torna a zero.
/// </summary>
internal sealed class PhotoSlider : Control
{
    private double _value;
    private bool _dragging;
    public event Action<double>? ValueChanged;

    public PhotoSlider() { Height = 22; Cursor = new Cursor(StandardCursorType.SizeWestEast); Focusable = false; }

    public double Value
    {
        get => _value;
        set { value = Math.Clamp(Math.Round(value), -100, 100); if (value == _value) return; _value = value; InvalidateVisual(); }
    }

    private Point _dragFrom;
    private double _dragStartValue;
    /// <summary>Punti dello schermo per un'unita': si vedono circa 60 unita' per lato.</summary>
    private double PxPerUnit => Math.Max(1, Bounds.Width / 2 / 60);

    // Righello come Foto su iPhone: l'indicatore resta fermo al centro e la scala scorre sotto. Una tacca ogni 5,
    // piu' lunghe ogni 25 e allo zero; le tacche fra lo zero e il valore sono bianche, le altre tenui e sfumano ai bordi.
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, cy = Bounds.Height / 2, center = w / 2, k = PxPerUnit;
        if (w < 20) return;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // tutta l'area prende il mouse
        for (int t = -100; t <= 100; t += 5)
        {
            double x = center + (t - _value) * k;
            if (x < 0 || x > w) continue;
            double edge = Math.Clamp(Math.Min(x, w - x) / (w * 0.18), 0, 1); // sfuma verso i bordi
            bool on = _value != 0 && Math.Sign(t) == Math.Sign(_value) && Math.Abs(t) <= Math.Abs(_value);
            double alpha = (on || t == 0 ? 235 : 80) * edge;
            double h = t == 0 ? 13 : t % 25 == 0 ? 9 : 5;
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)alpha, 255, 255, 255)), new Rect(x - 0.6, cy - h / 2 + 2, 1.2, h));
        }
        double mh = Bounds.Height - 2;
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), new Rect(center - 2, 1.5, 4, mh), 2f);
        ctx.FillRectangle(Brushes.White, new Rect(center - 1.25, 1, 2.5, mh), 1.25f);
    }

    private void SetValue(double v)
    {
        // Calamita sullo zero: tornarci a mano e' facile.
        if (Math.Abs(v) < 2) v = 0;
        double before = _value;
        Value = v;
        if (_value != before) ValueChanged?.Invoke(_value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) { _dragging = false; SetValue(0); return; }
        _dragging = true;
        _dragFrom = e.GetPosition(this); _dragStartValue = _value;
        e.Pointer.Capture(this);
    }

    // Si trascina la scala: verso sinistra il valore sale (come su iPhone).
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) SetValue(_dragStartValue - (e.GetPosition(this).X - _dragFrom.X) / PxPerUnit);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double d = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : e.Delta.Y;
        SetValue(_value + d * 4);
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); InvalidateVisual(); }
}
