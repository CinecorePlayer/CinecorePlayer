using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Un testo gia' impaginato, pronto da disegnare: stesse misure di <see cref="FormattedText"/> (Width, Height,
/// WidthIncludingTrailingWhitespace, Baseline) e si disegna con <c>ctx.DrawText(testo, punto)</c> come prima.
/// Non si modifica: lo stesso oggetto serve a tutti i fotogrammi in cui compare la stessa scritta.
/// </summary>
internal sealed class CachedText
{
    private readonly TextLayout? _layout;
    private readonly FormattedText? _formatted;

    public double Width { get; }
    public double Height { get; }
    public double WidthIncludingTrailingWhitespace { get; }
    public double Baseline { get; }

    public CachedText(TextLayout layout)
    {
        _layout = layout;
        Width = layout.Width; Height = layout.Height;
        WidthIncludingTrailingWhitespace = layout.WidthIncludingTrailingWhitespace; Baseline = layout.Baseline;
    }

    public CachedText(FormattedText formatted)
    {
        _formatted = formatted;
        Width = formatted.Width; Height = formatted.Height;
        WidthIncludingTrailingWhitespace = formatted.WidthIncludingTrailingWhitespace; Baseline = formatted.Baseline;
    }

    public void Draw(DrawingContext ctx, Point origin)
    {
        if (_layout != null) _layout.Draw(ctx, origin);
        else ctx.DrawText(_formatted!, origin);
    }
}

internal static class CachedTextExtensions
{
    /// <summary>Stessa chiamata di <see cref="DrawingContext.DrawText(FormattedText, Point)"/>.</summary>
    public static void DrawText(this DrawingContext ctx, CachedText text, Point origin) => text.Draw(ctx, origin);
}

/// <summary>
/// Testi dell'interfaccia disegnata a mano, impaginati una volta sola e riusati (2026-11-21).
/// Prima ogni scritta creava un FormattedText nuovo a ogni fotogramma, che Avalonia impagina di nuovo (HarfBuzz) sia
/// per misurarlo sia per disegnarlo: con 200 titoli erano ~32 ms per fotogramma su un i7 recente, cioe' una libreria
/// che scorre sotto i 30 fps gia' su un PC veloce. Con TextLayout in cache: ~2,5 ms.
/// <para>
/// Le scritte su una riga usano TextLayout (identico al pixel a FormattedText, verificato con allineamento a sinistra,
/// centro, destra, larghezza libera, puntini e spazi finali). Quelle su piu' righe restano FormattedText, perche'
/// TextLayout mette i puntini dopo un'altra parola e sposta l'ultima riga centrata; in cache si risparmia comunque la
/// misura.
/// </para>
/// Cache a due generazioni: quando la corrente si riempie diventa la precedente e la piu' vecchia si butta. Le scritte
/// usate di continuo passano da una generazione all'altra; quelle di un'animazione (colori che sfumano) escono da sole.
/// Fuori dal thread dell'interfaccia non si usa la cache.
/// </summary>
internal static class UiText
{
    private readonly record struct Key(string Text, Typeface Face, double Size, uint Color, double MaxWidth, int Lines, TextAlignment Align, TextTrimming? Trimming, double LineHeight);

    private const int GenerationSize = 4000;
    private static Dictionary<Key, CachedText> _current = new(GenerationSize);
    private static Dictionary<Key, CachedText> _previous = new(GenerationSize);
    private static readonly Dictionary<uint, IImmutableSolidColorBrush> Brushes = new();

    /// <param name="text">testo gia' tradotto (L10n.T).</param>
    /// <param name="maxWidth">infinito = una riga senza limite.</param>
    /// <param name="lines">righe massime con maxWidth finito (0 = senza limite).</param>
    /// <param name="trimming">null = puntini sul carattere quando maxWidth e' finito, come i Format delle viste.</param>
    public static CachedText Get(string text, Typeface face, double size, Color color, double maxWidth = double.PositiveInfinity,
        int lines = 1, TextAlignment align = TextAlignment.Left, TextTrimming? trimming = null, double lineHeight = double.NaN)
    {
        bool bounded = !double.IsInfinity(maxWidth);
        if (bounded) maxWidth = Math.Max(1, maxWidth);
        else { lines = 1; trimming = null; }
        if (!Dispatcher.UIThread.CheckAccess()) return Build(text, face, size, color, maxWidth, lines, align, trimming, lineHeight, bounded);
        var key = new Key(text, face, size, color.ToUInt32(), maxWidth, lines, align, trimming, lineHeight);
        if (_current.TryGetValue(key, out var hit)) return hit;
        if (_previous.Remove(key, out hit)) { Store(key, hit); return hit; }
        var built = Build(text, face, size, color, maxWidth, lines, align, trimming, lineHeight, bounded);
        Store(key, built);
        return built;
    }

    /// <summary>Larghezza di una scritta su una riga (con gli spazi finali), dalla stessa cache.</summary>
    public static double Measure(string text, Typeface face, double size) =>
        Get(text, face, size, Colors.White).WidthIncludingTrailingWhitespace;

    private static void Store(Key key, CachedText value)
    {
        if (_current.Count >= GenerationSize)
        {
            (_previous, _current) = (_current, _previous);
            _current.Clear();
        }
        _current[key] = value;
    }

    private static CachedText Build(string text, Typeface face, double size, Color color, double maxWidth, int lines,
        TextAlignment align, TextTrimming? trimming, double lineHeight, bool bounded)
    {
        var brush = Brush(color);
        var trim = bounded ? trimming ?? TextTrimming.CharacterEllipsis : null;
        if (double.IsNaN(lineHeight) && (!bounded || (lines == 1 && ReferenceEquals(trim, TextTrimming.CharacterEllipsis))))
        {
            // Una riga: niente a capo (con l'a capo TextLayout taglierebbe alla parola invece che al carattere).
            // Solo i casi verificati identici al pixel a FormattedText; gli altri restano FormattedText.
            return new CachedText(new TextLayout(text, face, size, brush, align, TextWrapping.NoWrap, trim, null,
                FlowDirection.LeftToRight, maxWidth, double.PositiveInfinity, double.NaN, 0, 0));
        }
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush) { TextAlignment = align };
        if (!double.IsNaN(lineHeight)) ft.LineHeight = lineHeight;
        if (!bounded) return new CachedText(ft);
        ft.MaxTextWidth = maxWidth;
        if (lines > 0) ft.MaxLineCount = lines;
        ft.Trimming = trim!;
        return new CachedText(ft);
    }

    /// <summary>Pennelli pieni condivisi per colore (immutabili: si possono riusare in ogni fotogramma).</summary>
    public static IImmutableSolidColorBrush Brush(Color color)
    {
        uint argb = color.ToUInt32();
        if (!Dispatcher.UIThread.CheckAccess()) return new ImmutableSolidColorBrush(color);
        if (Brushes.TryGetValue(argb, out var brush)) return brush;
        if (Brushes.Count > 2048) Brushes.Clear();
        return Brushes[argb] = new ImmutableSolidColorBrush(color);
    }
}
