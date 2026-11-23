using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// Stile delle schede che chiedono dati (PlaylistCreateSheet di Windows, come il tutorial iniziale): niente riquadro,
/// la pagina dietro velata quasi del tutto e vignettata ai bordi, una colonna a sinistra con "Passo n di m", titolo
/// grande, testo, la casella sottolineata e la pillola chiara "Avanti"; "Annulla" in alto a destra.
/// Le misure sono quelle di Windows su 1920×1080, in scala con la finestra.
/// </summary>
internal static class WizardSheet
{
    private static readonly FontFamily Display = new("avares://CinecoreMac/Assets/Fonts#Inter Display");
    public static readonly Typeface Regular = new(Display), Semibold = new(Display, FontStyle.Normal, FontWeight.SemiBold);

    public static double Scale(Size size) => Math.Clamp(Math.Min(size.Width / 1920, size.Height / 1080) * 1.18, .62, 2.0);

    /// <summary>Colonna del contenuto (a sinistra del centro, come su Windows).</summary>
    public static Rect Column(Size size)
    {
        double u = Scale(size), w = Math.Min(size.Width - 120 * u, 820 * u);
        return new Rect(Math.Round((size.Width - w) / 2), Math.Round(size.Height * 0.24), w, size.Height * 0.64);
    }

    public static Rect Cancel(Size size)
    {
        double u = Scale(size);
        return new Rect(size.Width - 64 * u - 120 * u, 34 * u + 18, 120 * u, 36 * u);
    }

    public static Color Back => UiTheme.Panel;

    /// <summary>
    /// Schede a misure fisse (Sottotitoli, Sincronizzazione audio) nello stile nuovo: niente riquadro, velo della
    /// pagina e contenuto ingrandito con la finestra (scala dell'intero controllo: anche i clic seguono).
    /// </summary>
    public static void Fit(Control control, Size page, Size content)
    {
        double k = Math.Clamp(Math.Min(Scale(page) * 1.3, Math.Min((page.Width - 80) / content.Width, (page.Height - 60) / content.Height)), 1, 2.4);
        double current = control.RenderTransform is ScaleTransform st ? st.ScaleX : 1;
        if (Math.Abs(current - k) < 0.01) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            control.RenderTransformOrigin = RelativePoint.Center;
            control.RenderTransform = new ScaleTransform(k, k);
        });
    }
    public static Color Ink(byte alpha) => UiTheme.WithAlpha(UiTheme.Text, alpha);
    public static Color Soft(byte alpha) => UiTheme.WithAlpha(UiTheme.Text, (byte)(alpha * 0.62));

    /// <summary>Velo e vignettatura sulla pagina; <paramref name="eased"/> e' l'entrata (0 → 1).</summary>
    public static void DrawVeil(DrawingContext ctx, Rect page, double eased)
    {
        bool light = UiTheme.IsLight;
        ctx.FillRectangle(new SolidColorBrush(UiTheme.WithAlpha(Back, (byte)Math.Round((light ? 242 : 244) * eased))), page);
        var edge = light ? Color.FromArgb((byte)(120 * eased), 150, 160, 172) : Color.FromArgb((byte)(235 * eased), 2, 3, 5);
        var vignette = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.70, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            // Trasparente dello stesso colore: con Colors.Transparent (bianco) il passaggio schiariva i bordi.
            GradientStops = { new GradientStop(Color.FromArgb(0, edge.R, edge.G, edge.B), 0.55), new GradientStop(edge, 1) },
        };
        ctx.FillRectangle(vignette, page);
    }

    public static CachedText Text(string text, double px, Typeface face, Color color, double maxWidth, int maxLines = 1, TextAlignment align = TextAlignment.Left) =>
        UiText.Get(L10n.T(text), face, Math.Max(1, px), color, maxWidth, maxLines, align);

    public static double Measure(string text, double px, Typeface face) =>
        UiText.Measure(L10n.T(text), face, Math.Max(1, px));

    /// <summary>Punti del passo (se piu' di uno), "Passo n di m", titolo e testo. Restituisce dove iniziano i controlli.</summary>
    public static double DrawHeader(DrawingContext ctx, Rect column, double u, int step, int steps, string title, string body, byte alpha, double slide)
    {
        if (steps > 1)
        {
            double dot = 8 * u, gap = 18 * u;
            for (int i = 0; i < steps; i++)
                ctx.DrawEllipse(new SolidColorBrush(Ink((byte)(i == step ? 240 : i < step ? 140 : 60))), null, new Rect(column.X + i * gap, column.Y, dot, dot));
        }
        double x = column.X + slide, y = column.Y + 40 * u;
        if (steps > 1)
        {
            var s = Text($"Passo {step + 1} di {steps}", 15 * u, Semibold, Soft(alpha), column.Width);
            ctx.DrawText(s, new Point(x, y));
        }
        y += 36 * u;
        var t = Text(title, 44 * u, Semibold, Ink(alpha), column.Width, 2);
        ctx.DrawText(t, new Point(x, y));
        y += Math.Max(68 * u, t.Height + 12 * u);
        if (body.Length > 0)
        {
            var b = Text(body, 19 * u, Regular, Soft(alpha), column.Width, 3);
            ctx.DrawText(b, new Point(x, y));
            y += Math.Max(56 * u, b.Height + 8 * u);
        }
        return column.Y + Math.Max(200 * u, y - column.Y + 18 * u);
    }

    /// <summary>Margine interno del campo (la casella vera sta dentro, a questa distanza dal bordo).</summary>
    public static double FieldInset(double u) => 18 * u;

    /// <summary>
    /// Il campo: un riquadro arrotondato tenue (niente riga sotto), un po' piu' chiaro con il fuoco e con un filo
    /// rosso se c'e' un errore.
    /// </summary>
    public static void DrawUnderline(DrawingContext ctx, Rect shell, bool focused, bool error, byte alpha, double u)
    {
        double radius = Math.Min(shell.Height / 2, 14 * u);
        byte fill = (byte)((focused ? 26 : 14) * alpha / 255);
        IPen? pen = error ? new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * alpha / 255), 255, 110, 120)), Math.Max(1, 1.5 * u))
            : focused ? new Pen(new SolidColorBrush(UiTheme.WithAlpha(UiTheme.Text, (byte)(46 * alpha / 255))), 1) : null;
        ctx.DrawRectangle(new SolidColorBrush(UiTheme.WithAlpha(UiTheme.Text, fill)), pen, shell, radius, radius);
    }

    /// <summary>Dove mettere la casella vera dentro il campo: centrata in altezza, con il margine ai lati.</summary>
    public static Rect BoxIn(Rect shell, double boxHeight, double u) =>
        new(shell.X + FieldInset(u), shell.Y + (shell.Height - boxHeight) / 2, Math.Max(1, shell.Width - 2 * FieldInset(u)), boxHeight);

    public static void DrawError(DrawingContext ctx, Rect shell, string text, double u)
    {
        if (text.Length == 0) return;
        var ft = Text(text, 15 * u, Regular, Color.FromRgb(255, 120, 128), shell.Width);
        ctx.DrawText(ft, new Point(shell.X, shell.Bottom + 8 * u));
    }

    /// <summary>"Avanti" (pillola chiara) e accanto "Indietro" solo testo; restituisce i due rettangoli.</summary>
    public static (Rect Next, Rect Back) Buttons(Rect column, double top, double u, string next, string? back)
    {
        double nw = Measure(next, 18 * u, Semibold) + 64 * u;
        var n = new Rect(column.X, top, nw, 54 * u);
        var b = back == null ? default : new Rect(n.Right + 14 * u, n.Y, Measure(back, 18 * u, Semibold) + 40 * u, n.Height);
        return (n, b);
    }

    public static void DrawNext(DrawingContext ctx, Rect r, string label, bool hover, bool enabled, byte alpha, double u)
    {
        Color fill = enabled ? UiTheme.PrimaryFill(hover) : UiTheme.WithAlpha(UiTheme.Text, 30);
        ctx.DrawRectangle(new SolidColorBrush(UiTheme.WithAlpha(fill, (byte)(fill.A * alpha / 255))), null, r, r.Height / 2, r.Height / 2);
        var ft = Text(label, 18 * u, Semibold, enabled ? UiTheme.WithAlpha(UiTheme.OnPrimary, alpha) : Soft(alpha), r.Width, 1, TextAlignment.Center);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    public static void DrawBack(DrawingContext ctx, Rect r, string label, bool hover, byte alpha, double u)
    {
        if (r.Width <= 0) return;
        var ft = Text(label, 18 * u, Semibold, hover ? Ink(alpha) : Soft(alpha), r.Width, 1, TextAlignment.Center);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    public static void DrawCancel(DrawingContext ctx, Rect r, bool hover, double u, string label = "Annulla")
    {
        var ft = Text(label, 17 * u, Regular, hover ? UiTheme.Text : Soft(255), r.Width, 1, TextAlignment.Right);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    /// <summary>Casella di testo senza fondo ne' bordi (la riga la disegna la scheda).</summary>
    public static void StyleBox(TextBox box)
    {
        box.Background = Brushes.Transparent; box.BorderThickness = new Thickness(0); box.Padding = new Thickness(0); box.MinHeight = 0;
        box.FontFamily = Display;
        box.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[k] = Brushes.Transparent;
        box.FocusAdorner = null;
    }
}
