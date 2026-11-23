using System;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CinecoreMac;

/// <summary>
/// Selettore di colore (AccentColorPickerForm di Windows): ruota per tinta e saturazione, barra della luminosita',
/// confronto tra il colore di prima e il nuovo, codice esadecimale scrivibile e alcuni colori pronti. Disegnato dalla
/// pagina che lo ospita direttamente su una vignettatura scura (2026-11-23: prima stava in una scheda al centro).
/// </summary>
internal sealed class ColorPickerSheet
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface SemiBold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    // [2026-11-23] Gli stessi colori della riga nelle Impostazioni (accento o sfondo): prima erano sempre quelli
    // dell'accento della vecchia versione Windows, anche per lo sfondo.
    private readonly Color[] Presets;
    private readonly Action<Color> _apply;
    private readonly string _title;
    private readonly Color _initial;
    private Color _selected;
    private double _hue, _sat, _val;
    private string _hex = "";
    private bool _hexFocused, _hexReplace, _dragWheel, _dragSlider;
    private Rect _sheet, _wheel, _compare, _slider, _hexRect, _swatches, _ok, _cancel;
    private readonly Rect[] _swatchRects;
    private WriteableBitmap? _wheelBitmap;
    private double _wheelValue = -1;
    private Point _mouse;

    public ColorPickerSheet(Color initial, string title, Action<Color> apply, Color[] presets)
    {
        Presets = presets;
        _swatchRects = new Rect[presets.Length];
        _initial = Color.FromRgb(initial.R, initial.G, initial.B);
        _selected = _initial;
        _title = title;
        _apply = apply;
        RgbToHsv(_selected, out _hue, out _sat, out _val);
        SyncHex();
    }

    public bool Done { get; private set; }

    private static double Px(double pt) => pt * 96 / 72 * 0.92;

    private static void Label(DrawingContext ctx, string text, Rect r, Color color, double pt = 9, bool semibold = false, TextAlignment align = TextAlignment.Left)
    {
        var ft = UiText.Get(L10n.T(text), semibold ? SemiBold : Regular, Px(pt), color, Math.Max(1, r.Width), 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    // Sulla vignettatura il fondo e' sempre scuro: testi bianchi in tema chiaro e scuro.
    private static readonly Color Ink = Colors.White, InkMuted = Color.FromArgb(170, 255, 255, 255);

    /// <summary>Il campo del codice e' attivo: la pagina gli passa anche l'incolla.</summary>
    public bool HexFocused => _hexFocused;
    internal Point QaHexPoint => _hexRect.Center;
    internal string QaSummary => $"hex={_hex} focus={_hexFocused} sel={_hexReplace} colore=#{_selected.R:X2}{_selected.G:X2}{_selected.B:X2}";

    private void Layout(Rect page)
    {
        double w = Math.Min(620, page.Width - 24), h = Math.Min(480, page.Height - 24);
        _sheet = new Rect(page.X + Math.Round((page.Width - w) / 2), page.Y + Math.Round((page.Height - h) / 2), w, h);
        double L = _sheet.X, T = _sheet.Y;
        const double pad = 32;
        double wheel = Math.Min(280, Math.Max(180, h - 200));
        _wheel = new Rect(L + pad, T + 92, wheel, wheel);
        double x = _wheel.Right + 40, cw = Math.Max(160, _sheet.Right - pad - x);
        _compare = new Rect(x, _wheel.Y, cw, 56);
        _slider = new Rect(x + 9, _compare.Bottom + 62, cw - 18, 12);
        _hexRect = new Rect(x, _slider.Bottom + 44, Math.Min(150, cw), 36);
        _swatches = new Rect(x, _hexRect.Bottom + 44, cw, 24);
        double step = Math.Max(26, Math.Min(32, Math.Floor((cw + 8) / Presets.Length)));
        for (int i = 0; i < Presets.Length; i++) _swatchRects[i] = new Rect(x + 2 + i * step, _swatches.Y, 20, 20);
        double y = _sheet.Bottom - pad - 38;
        _ok = new Rect(_sheet.Right - pad - 104, y, 104, 38);
        _cancel = new Rect(_ok.X - 10 - 104, y, 104, 38);
    }

    public void Render(DrawingContext ctx, Rect page)
    {
        Layout(page);
        // Vignettatura come l'abbinamento del telecomando: velo, poi quasi nero dietro i comandi e piu' leggero ai bordi.
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), page);
        double reach = Math.Max(_sheet.Width, _sheet.Height) * .95;
        ctx.FillRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(_sheet.Center, RelativeUnit.Absolute), GradientOrigin = new RelativePoint(_sheet.Center, RelativeUnit.Absolute),
            RadiusX = new RelativeScalar(reach, RelativeUnit.Absolute), RadiusY = new RelativeScalar(reach, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromArgb(240, 0, 0, 0), .5), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
        }, page);
        Color muted = InkMuted;
        Label(ctx, _title, new Rect(_sheet.X + 32, _sheet.Y + 22, _sheet.Width - 64, 30), Ink, 14, true);
        Label(ctx, "Trascina sulla ruota per tinta e saturazione.", new Rect(_sheet.X + 32, _sheet.Y + 52, _sheet.Width - 64, 20), muted);

        DrawWheel(ctx);

        // Prima e nuovo, affiancati: si vede subito cosa cambia.
        double half = Math.Floor(_compare.Width / 2);
        using (ctx.PushClip(new RoundedRect(_compare, 12)))
        {
            ctx.FillRectangle(new SolidColorBrush(_initial), new Rect(_compare.X, _compare.Y, half, _compare.Height));
            ctx.FillRectangle(new SolidColorBrush(_selected), new Rect(_compare.X + half, _compare.Y, _compare.Width - half, _compare.Height));
        }
        Label(ctx, "Prima", new Rect(_compare.X, _compare.Bottom + 6, half, 18), muted, 8.5);
        Label(ctx, "Nuovo", new Rect(_compare.X + half, _compare.Bottom + 6, half, 18), muted, 8.5);

        Label(ctx, "Luminosità", new Rect(_compare.X, _slider.Y - 28, _compare.Width, 18), muted, 8.5);
        var full = FromHsv(_hue, _sat, 1);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(full, 1) },
        };
        ctx.DrawRectangle(gradient, null, _slider, _slider.Height / 2, _slider.Height / 2);
        Knob(ctx, new Point(_slider.X + Math.Round(_val * _slider.Width), _slider.Y + _slider.Height / 2), _selected);

        Label(ctx, "Codice", new Rect(_hexRect.X, _hexRect.Y - 26, _hexRect.Width, 18), muted, 8.5);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), new Pen(new SolidColorBrush(_hexFocused ? UiTheme.Accent : Color.FromArgb(60, 255, 255, 255)), 1), _hexRect, 8, 8);
        var hexText = UiText.Get(_hex, SemiBold, Px(11), Ink, Math.Max(1, _hexRect.Width - 24), 1, TextAlignment.Left, TextTrimming.None);
        var hexAt = new Point(_hexRect.X + 12, _hexRect.Y + (_hexRect.Height - hexText.Height) / 2);
        // Appena cliccato il codice e' tutto selezionato: il primo carattere scritto lo sostituisce.
        if (_hexFocused && _hexReplace && _hex.Length > 0)
            ctx.DrawRectangle(new SolidColorBrush(UiTheme.WithAlpha(UiTheme.Accent, 150)), null, new Rect(hexAt.X - 3, hexAt.Y, hexText.Width + 6, hexText.Height), 3, 3);
        ctx.DrawText(hexText, hexAt);
        if (_hexFocused && !_hexReplace)
            ctx.DrawLine(new Pen(new SolidColorBrush(Ink), 1.4), new Point(hexAt.X + hexText.Width + 2, hexAt.Y + 2), new Point(hexAt.X + hexText.Width + 2, hexAt.Y + hexText.Height - 2));

        Label(ctx, "Colori pronti", new Rect(_swatches.X, _swatches.Y - 26, _swatches.Width, 18), muted, 8.5);
        for (int i = 0; i < Presets.Length; i++)
        {
            var c = _swatchRects[i].Center;
            ctx.DrawEllipse(new SolidColorBrush(Presets[i]), null, c, 10, 10);
            if (Presets[i] == _selected) ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Ink), 1.6), c, 13, 13);
        }

        // Annulla: pillola appena velata. Applica: piena del colore scelto, testo chiaro o scuro secondo la luminosita'.
        bool cancelHot = _cancel.Contains(_mouse), okHot = _ok.Contains(_mouse);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(cancelHot ? 52 : 30), 255, 255, 255)), null, _cancel, 19, 19);
        Label(ctx, "Annulla", _cancel, Ink, 9.5, true, TextAlignment.Center);
        ctx.DrawRectangle(new SolidColorBrush(_selected), null, _ok, 19, 19);
        if (okHot) ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)), null, _ok, 19, 19);
        Label(ctx, "Applica", _ok, UiTheme.Luminance(_selected) > 0.62 ? Color.FromRgb(14, 18, 24) : Colors.White, 9.5, true, TextAlignment.Center);
    }

    private void DrawWheel(DrawingContext ctx)
    {
        int size = (int)_wheel.Width;
        if (_wheelBitmap == null || _wheelBitmap.PixelSize.Width != size * 2 || Math.Abs(_wheelValue - _val) > 0.001)
        {
            _wheelBitmap?.Dispose();
            _wheelBitmap = BuildWheel(size * 2, _val);
            _wheelValue = _val;
        }
        ctx.DrawImage(_wheelBitmap, new Rect(_wheelBitmap.Size), _wheel);
        double cx = _wheel.Width / 2, radius = cx, angle = _hue * Math.PI / 180;
        Knob(ctx, new Point(_wheel.X + cx + Math.Cos(angle) * _sat * radius, _wheel.Y + cx + Math.Sin(angle) * _sat * radius), _selected);
    }

    private static void Knob(DrawingContext ctx, Point c, Color fill)
    {
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Point(c.X, c.Y + 1), 9, 9);
        ctx.DrawEllipse(new SolidColorBrush(fill), new Pen(Brushes.White, 2.4), c, 9, 9);
    }

    private static unsafe WriteableBitmap BuildWheel(int size, double value)
    {
        var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        double c = size / 2.0, radius = c - 0.5;
        for (int y = 0; y < size; y++)
        {
            uint* row = (uint*)(fb.Address + y * fb.RowBytes);
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - c, dy = y + 0.5 - c, dist = Math.Sqrt(dx * dx + dy * dy);
                // Bordo morbido di un pixel: niente scalini attorno alla ruota.
                double alpha = Math.Clamp(radius - dist + 0.5, 0, 1);
                if (alpha <= 0) { row[x] = 0; continue; }
                double hue = Math.Atan2(dy, dx) * 180 / Math.PI;
                if (hue < 0) hue += 360;
                var col = FromHsv(hue, Math.Min(1, dist / radius), value);
                uint a = (uint)Math.Round(alpha * 255);
                row[x] = (a << 24) | ((uint)(col.R * a / 255) << 16) | ((uint)(col.G * a / 255) << 8) | (uint)(col.B * a / 255);
            }
        }
        return bmp;
    }

    // ------------------------------------------------------------------ input (la pagina inoltra tutto alla scheda)
    private bool InsideWheel(Point p)
    {
        double cx = _wheel.X + _wheel.Width / 2, cy = _wheel.Y + _wheel.Height / 2, dx = p.X - cx, dy = p.Y - cy;
        return dx * dx + dy * dy <= Math.Pow(_wheel.Width / 2 + 6, 2);
    }

    public bool WantsHand(Point p) => InsideWheel(p) || _slider.Inflate(new Thickness(0, 10)).Contains(p) || _ok.Contains(p) || _cancel.Contains(p)
        || Array.Exists(_swatchRects, r => r.Inflate(4).Contains(p)) || _dragWheel || _dragSlider;

    public void PointerPressed(Point p)
    {
        _mouse = p;
        bool wasFocused = _hexFocused;
        _hexFocused = _hexRect.Contains(p);
        // Primo clic: tutto selezionato (si scrive sopra); un altro clic toglie la selezione e mette il cursore in fondo.
        if (_hexFocused) _hexReplace = !wasFocused || !_hexReplace;
        if (_ok.Contains(p)) { Done = true; _apply(_selected); return; }
        if (_cancel.Contains(p)) { Done = true; return; }
        if (InsideWheel(p)) { _dragWheel = true; UpdateWheel(p); return; }
        if (_slider.Inflate(new Thickness(0, 10)).Contains(p)) { _dragSlider = true; UpdateSlider(p); return; }
        for (int i = 0; i < _swatchRects.Length; i++)
            if (_swatchRects[i].Inflate(4).Contains(p)) { SetColor(Presets[i]); return; }
    }

    public void PointerMoved(Point p)
    {
        _mouse = p;
        if (_dragWheel) UpdateWheel(p);
        else if (_dragSlider) UpdateSlider(p);
    }

    public void PointerReleased() { _dragWheel = _dragSlider = false; }

    /// <summary>Esc annulla, Invio applica; con il codice attivo si scrive l'esadecimale.</summary>
    public void Key(KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape) { Done = true; return; }
        if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return) { Done = true; _apply(_selected); return; }
        if (!_hexFocused) return;
        if (e.Key is Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete)
        {
            if (_hexReplace) _hex = "";
            else if (e.Key == Avalonia.Input.Key.Back && _hex.Length > 0) _hex = _hex[..^1];
            _hexReplace = false;
            HexEdited();
        }
    }

    /// <summary>Incolla nel codice (Ctrl+V / Cmd+V): sostituisce tutto, si tengono solo '#' e le cifre esadecimali.</summary>
    public void Paste(string text)
    {
        if (!_hexFocused) return;
        _hex = "";
        _hexReplace = false;
        TextInput(text.Trim());
    }

    public void TextInput(string text)
    {
        if (!_hexFocused) return;
        if (_hexReplace) { _hex = ""; _hexReplace = false; }
        foreach (char ch in text)
        {
            if (ch == '#' && _hex.Length == 0) _hex = "#";
            else if (Uri.IsHexDigit(ch) && _hex.TrimStart('#').Length < 6) _hex = (_hex.StartsWith('#') ? _hex : "#" + _hex) + char.ToUpperInvariant(ch);
        }
        HexEdited();
    }

    private void HexEdited()
    {
        string text = _hex.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return;
        _selected = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        RgbToHsv(_selected, out _hue, out _sat, out _val);
    }

    private void SyncHex() { if (!_hexFocused) _hex = $"#{_selected.R:X2}{_selected.G:X2}{_selected.B:X2}"; }

    private void UpdateWheel(Point p)
    {
        double cx = _wheel.X + _wheel.Width / 2, cy = _wheel.Y + _wheel.Height / 2, radius = _wheel.Width / 2, dx = p.X - cx, dy = p.Y - cy;
        _hue = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (_hue < 0) _hue += 360;
        _sat = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / radius, 0, 1);
        if (_val < 0.05) _val = 1; // dal nero la ruota non mostrerebbe niente
        UpdateSelected();
    }

    private void UpdateSlider(Point p)
    {
        _val = Math.Clamp((p.X - _slider.X) / Math.Max(1, _slider.Width), 0, 1);
        UpdateSelected();
    }

    private void SetColor(Color c) { RgbToHsv(c, out _hue, out _sat, out _val); UpdateSelected(); }

    private void UpdateSelected() { _selected = FromHsv(_hue, _sat, _val); _hexFocused = false; SyncHex(); }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue %= 360; if (hue < 0) hue += 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1) * 255;
        int hi = (int)Math.Floor(hue / 60) % 6;
        double f = hue / 60 - Math.Floor(hue / 60);
        byte v = (byte)Math.Round(value), p = (byte)Math.Round(value * (1 - saturation)),
             q = (byte)Math.Round(value * (1 - f * saturation)), t = (byte)Math.Round(value * (1 - (1 - f) * saturation));
        return hi switch
        {
            0 => Color.FromRgb(v, t, p), 1 => Color.FromRgb(q, v, p), 2 => Color.FromRgb(p, v, t),
            3 => Color.FromRgb(p, q, v), 4 => Color.FromRgb(t, p, v), _ => Color.FromRgb(v, p, q),
        };
    }

    private static void RgbToHsv(Color c, out double hue, out double saturation, out double value)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        hue = d <= 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (hue < 0) hue += 360;
        saturation = max <= 0 ? 0 : d / max;
        value = max;
    }
}
