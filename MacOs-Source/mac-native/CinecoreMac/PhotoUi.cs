using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace CinecoreMac;

/// <summary>
/// Pezzi comuni del visualizzatore e dell'editor delle foto, fatti con i controlli di Avalonia (non disegnati a mano):
/// pulsanti a icona su vetro scuro come le barre di Foto di macOS, pulsanti di testo, capsule e icone Lucide.
/// </summary>
internal static class PhotoUi
{
    public static readonly FontFamily Font = new("avares://CinecoreMac/Assets/Fonts#Inter");
    public static readonly IBrush Ink = new SolidColorBrush(Colors.White);
    public static readonly IBrush Soft = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
    public static readonly IBrush CapsuleFill = new SolidColorBrush(Color.FromArgb(214, 28, 30, 36));
    public static readonly IBrush CapsuleEdge = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    public static IBrush AccentBrush => new SolidColorBrush(UiTheme.Accent);
    public static IBrush AccentHover => new SolidColorBrush(Mix(UiTheme.Accent, Colors.White, 0.14));
    public static IBrush AccentPressed => new SolidColorBrush(Mix(UiTheme.Accent, Colors.Black, 0.18));
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(a.A, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Stili dei controlli delle foto: si aggiungono una volta allo stile della vista.</summary>
    public static Styles Styles()
    {
        var hover = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
        var pressed = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        var on = new SolidColorBrush(Color.FromArgb(58, 255, 255, 255));
        static Selector Presenter(Selector s) => s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
        var styles = new Styles
        {
            // Pulsante a icona come nel resto dell'app: niente fondo, al passaggio l'icona diventa piu' chiara.
            new Style(x => x.OfType<Button>().Class("ph"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)),
                    new Setter(Layoutable.WidthProperty, 40.0),
                    new Setter(Layoutable.HeightProperty, 36.0),
                    new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                },
            },
            // Come nel resto dell'app: il passaggio colora l'icona d'accento, niente dischi ne' riquadri; la scelta
            // corrente e' l'icona d'accento (solo colore, nessun fondo).
            new Style(x => Presenter(x.OfType<Button>().Class("ph").Class(":pointerover"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.ForegroundProperty, AccentBrush) } },
            new Style(x => Presenter(x.OfType<Button>().Class("ph").Class(":pressed"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.ForegroundProperty, AccentPressed) } },
            new Style(x => Presenter(x.OfType<Button>().Class("ph").Class("on"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.ForegroundProperty, AccentBrush) } },
            new Style(x => x.OfType<Button>().Class("ph").Class(":disabled")) { Setters = { new Setter(Visual.OpacityProperty, 0.35) } },
            // X di chiusura: come tutte le X dell'app, niente fondo; al passaggio solo piu' bianca.
            new Style(x => x.OfType<Button>().Class("phX"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.FromArgb(175, 255, 255, 255))),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(Layoutable.WidthProperty, 40.0),
                    new Setter(Layoutable.HeightProperty, 40.0),
                    new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                },
            },
            new Style(x => Presenter(x.OfType<Button>().Class("phX").Class(":pointerover"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.ForegroundProperty, Ink) } },
            new Style(x => Presenter(x.OfType<Button>().Class("phX").Class(":pressed"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.ForegroundProperty, Ink) } },
            new Style(x => Presenter(x.OfType<Button>().Class("ph").Class(":disabled"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) } },
            // Pulsante di testo (Annulla) e pulsante pieno (Salva copia), arrotondati come quelli di macOS.
            new Style(x => x.OfType<Button>().Class("phText"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.FromArgb(205, 255, 255, 255))),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(17)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(16, 7)),
                    new Setter(TemplatedControl.FontFamilyProperty, Font),
                    new Setter(TemplatedControl.FontSizeProperty, 13.5),
                    new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold),
                },
            },
            new Style(x => Presenter(x.OfType<Button>().Class("phText").Class(":pointerover"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.ForegroundProperty, AccentBrush) } },
            new Style(x => Presenter(x.OfType<Button>().Class("phText").Class(":pressed"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.ForegroundProperty, new SolidColorBrush(Color.FromArgb(170, 255, 255, 255))) } },
            // Pulsante secondario (Annulla accanto a Fine): fondo tenue fisso, al passaggio appena piu' chiaro.
            new Style(x => x.OfType<Button>().Class("phText").Class("secondary")) { Setters = { new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromArgb(38, 255, 255, 255))) } },
            new Style(x => Presenter(x.OfType<Button>().Class("phText").Class("secondary").Class(":pointerover"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(58, 255, 255, 255))), new Setter(ContentPresenter.ForegroundProperty, Ink) } },
            // Scheda scelta (Regola, Filtri…): pillola chiara come il controllo a segmenti di Foto.
            new Style(x => Presenter(x.OfType<Button>().Class("phText").Class("on"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(52, 255, 255, 255))) } },
            // Pulsante principale (Fine, Modifica) come nel resto dell'app: bianco con testo nero, mai l'accento.
            new Style(x => x.OfType<Button>().Class("phText").Class("primary")) { Setters = { new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(UiTheme.PrimaryFill(false))), new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(UiTheme.OnPrimary)) } },
            new Style(x => Presenter(x.OfType<Button>().Class("phText").Class("primary").Class(":pointerover"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(UiTheme.PrimaryFill(true))), new Setter(ContentPresenter.ForegroundProperty, new SolidColorBrush(UiTheme.OnPrimary)) } },
            new Style(x => Presenter(x.OfType<Button>().Class("phText").Class("primary").Class(":pressed"))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.FromRgb(200, 203, 208))), new Setter(ContentPresenter.ForegroundProperty, new SolidColorBrush(UiTheme.OnPrimary)) } },
            // Suggerimenti scuri e compatti.
            new Style(x => x.OfType<ToolTip>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromArgb(236, 24, 26, 32))),
                    new Setter(TemplatedControl.ForegroundProperty, Ink),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(7)),
                    new Setter(TemplatedControl.FontFamilyProperty, Font),
                    new Setter(TemplatedControl.FontSizeProperty, 12.0),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(9, 5)),
                },
            },
        };
        return styles;
    }

    /// <summary>Pulsante con un'icona Lucide e il suggerimento.</summary>
    public static Button IconButton(string icon, string tip, Action click, double size = 20)
    {
        var button = new Button { Content = new IconView(icon) { Width = size, Height = size } };
        button.Classes.Add("ph");
        ToolTip.SetTip(button, L10n.T(tip));
        button.Click += (_, _) => click();
        return button;
    }

    public static Button TextButton(string text, Action click, bool primary = false)
    {
        var button = new Button { Content = L10n.T(text) };
        button.Classes.Add("phText");
        if (primary) button.Classes.Add("primary");
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Capsula scura che galleggia sulla foto (barre degli strumenti).</summary>
    public static Border Capsule(Control child, double radius = 22) => new GlassCapsule
    {
        Background = CapsuleFill,
        BorderBrush = CapsuleEdge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(6),
        Child = child,
        BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 6, Blur = 24, Color = Color.FromArgb(90, 0, 0, 0) }),
    };

    public static TextBlock Label(string text, double size, FontWeight weight, IBrush brush) => new()
    {
        Text = L10n.T(text), FontFamily = Font, FontSize = size, FontWeight = weight, Foreground = brush,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Separatore verticale sottile fra gruppi di pulsanti.</summary>
    public static Border Divider() => new() { Width = 1, Height = 20, Margin = new Thickness(6, 0), Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
}

/// <summary>Un'icona Lucide (Assets/Icons.Lucide) nel colore del testo del pulsante che la contiene.</summary>
internal sealed class IconView : Control
{
    private string _name;
    public IconView(string name) { _name = name; IsHitTestVisible = false; }
    public string Name2 { get => _name; set { _name = value; InvalidateVisual(); } }
    private ContentPresenter? _presenter;

    // Il colore viene dal pulsante (passaggio, pressione, scelta): quando cambia l'icona va ridisegnata, altrimenti
    // le X fuori dal vetro restavano ferme al passaggio del mouse.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _presenter = this.FindAncestorOfType<ContentPresenter>();
        if (_presenter != null) _presenter.PropertyChanged += PresenterChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_presenter != null) _presenter.PropertyChanged -= PresenterChanged;
        _presenter = null;
    }

    private void PresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ContentPresenter.ForegroundProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var color = (this.FindAncestorOfType<ContentPresenter>()?.Foreground as ISolidColorBrush)?.Color ?? Colors.White;
        var r = new Rect(Bounds.Size);
        if (SvgIcon.Get("Icons.Lucide/" + _name + ".svg") is { } icon) icon.Draw(ctx, r, color);
        else WinSymbol.Draw(ctx, r, _name, color);
    }
}
