using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Pillola di ricerca su vetro vero di macOS (Liquid Glass, MacSurfaceGlass), per la ricerca della libreria e quella di
/// Spotlight. Il vetro sta in una finestra figlia sopra quella dell'app: la casella vera (TextBox, per tastiera,
/// incolla e IME) resta sotto ed e' resa invisibile; lente, testo, scritta d'invito e cursore lampeggiante si disegnano
/// qui sopra il vetro. Se il vetro non c'e' (o qualcosa lo copre) chi la usa torna alla pillola disegnata.
/// </summary>
internal sealed class GlassSearchField
{
    private MacSurfaceGlass? _glass;
    private DispatcherTimer? _blink;
    private readonly Control _owner;

    public GlassSearchField(Control owner) { _owner = owner; }

    /// <summary>Vero se l'ultimo disegno e' andato sul vetro vero (la casella va resa invisibile).</summary>
    public bool On { get; private set; }

    /// <summary>Rettangolo locale del controllo in coordinate della finestra.</summary>
    public Rect? ToWindow(Rect local)
    {
        if (TopLevel.GetTopLevel(_owner) is not { } top) return null;
        var origin = _owner.TranslatePoint(default, top) ?? default;
        return local.Translate(new Vector(origin.X, origin.Y));
    }

    public void Hide()
    {
        _glass?.Hide();
        On = false;
        _blink?.Stop();
    }

    /// <summary>
    /// Mostra la pillola. <paramref name="box"/> e' la casella vera (per testo, cursore e fuoco); <paramref name="textRect"/>
    /// dove sta il testo; <paramref name="lens"/> dove va la lente (vuoto: niente lente).
    /// </summary>
    public bool Show(Rect pill, double alpha, TextBox? box, Rect textRect, Rect lens, string placeholder, FontFamily font, double fontSize,
        Color ink, Color soft)
    {
        if (!_owner.IsEffectivelyVisible || !MacSurfaceGlass.Supported || TopLevel.GetTopLevel(_owner) is not { } top ||
            ToWindow(pill) is not { } window) { Hide(); return false; }
        _glass ??= new MacSurfaceGlass(top);
        bool dark = !UiTheme.IsLight;
        if (!_glass.Show(new List<(Rect, double)> { (window, pill.Height / 2) }, dark, Math.Clamp(alpha, 0, 1))) { Hide(); return false; }
        On = true;
        string text = box?.Text ?? "";
        bool focused = box is { IsVisible: true, IsFocused: true };
        int caret = Math.Clamp(box?.CaretIndex ?? text.Length, 0, text.Length);
        bool caretOn = focused && Environment.TickCount64 / 530 % 2 == 0;
        // Il cursore lampeggia: un ridisegno ogni 530 ms solo mentre si scrive.
        if (focused) { _blink ??= new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) => _owner.InvalidateVisual()); if (!_blink.IsEnabled) _blink.Start(); }
        else _blink?.Stop();
        double scaling = top.RenderScaling;
        string key = string.Join("|", text, caret, caretOn, focused, Math.Round(alpha, 2), pill.Size, textRect, lens, dark, ink, scaling, placeholder);
        var size = new PixelSize((int)Math.Ceiling(pill.Width * scaling), (int)Math.Ceiling(pill.Height * scaling));
        _glass.SetForeground(0, key, size, a =>
        {
            var ctx = a.Context;
            using var transform = ctx.PushTransform(Matrix.CreateTranslation(-pill.X, -pill.Y) * Matrix.CreateScale(scaling, scaling));
            using var fade = ctx.PushOpacity(Math.Clamp(alpha, 0, 1));
            LibraryView.DrawGlassRim(ctx, pill, pill.Height / 2, !dark, scaling);
            if (lens.Width > 0) SvgIcon.Get("search")?.Draw(ctx, lens, Color.FromArgb(200, ink.R, ink.G, ink.B));
            using var clip = ctx.PushClip(textRect);
            var face = new Typeface(font);
            if (text.Length == 0)
            {
                if (!focused || !string.IsNullOrEmpty(placeholder))
                {
                    var ph = UiText.Get(L10n.T(placeholder), face, fontSize, soft);
                    ctx.DrawText(ph, new Point(textRect.X, textRect.Y + (textRect.Height - ph.Height) / 2));
                }
            }
            var ft = UiText.Get(L10n.T(text), face, fontSize, ink);
            var before = UiText.Get(L10n.T(text[..caret]), face, fontSize, Colors.White);
            // Testo piu' lungo della casella: si vede la parte col cursore (come scorre la casella vera).
            double shift = Math.Max(0, before.WidthIncludingTrailingWhitespace - textRect.Width + 2);
            double ty = textRect.Y + (textRect.Height - ft.Height) / 2;
            if (text.Length > 0) ctx.DrawText(ft, new Point(textRect.X - shift, ty));
            if (caretOn)
            {
                double cx = textRect.X - shift + before.WidthIncludingTrailingWhitespace + 0.5;
                ctx.DrawLine(new Pen(new SolidColorBrush(ink), 1.4), new Point(cx, ty + ft.Height * .12), new Point(cx, ty + ft.Height * .88));
            }
        });
        return true;
    }
}
