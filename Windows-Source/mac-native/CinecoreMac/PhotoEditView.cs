using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tool = CinecoreMac.PhotoEditCanvas.Tool;

namespace CinecoreMac;

/// <summary>
/// Modifica di una foto. La foto sta al centro della finestra sulla sua copia sfocata; in alto, centrate sulla
/// finestra, le schede (Regola, Filtri, Ritaglia, Disegna) come voci di testo; a sinistra annulla/ripeti, a destra
/// Annulla e Fine come testo. I comandi della scheda stanno in una sola capsula di vetro vero in basso, grande quanto
/// serve (niente colonna laterale mezza vuota): Regola = fila di icone e un cursore, Filtri = striscia di anteprime,
/// Ritaglia = tre icone, Disegna = strumenti, colori e spessori in una riga. PhotoEditCanvas fa il lavoro e salva una
/// copia accanto all'originale (l'originale non si tocca).
/// </summary>
internal sealed class PhotoEditView : Grid
{
    public event Action<string?>? Closed;

    private enum Tab { Adjust, Filters, Crop, Draw }
    private readonly PhotoEditCanvas _canvas = new();
    private readonly Dictionary<Tab, Border> _tabs = new();
    private readonly Dictionary<Tab, Border> _tabMarks = new();
    private readonly Image _backdrop;
    private readonly Border _backdropShade;
    private readonly Dictionary<Tool, Button> _toolButtons = new();
    private readonly Button _undo, _redo;
    private Button? _picker;
    private readonly List<(Color Ink, Button Button, Ellipse Ring)> _swatches = new();
    private Ellipse? _customRing, _customFill;
    private IBrush? _rainbow;
    private readonly List<(Button Button, Ellipse Dot)> _sizes = new();
    private readonly List<Image> _filterPreviews = new();
    private readonly List<TextBlock> _filterNames = new();
    private readonly List<Border> _filterFrames = new();
    private readonly List<Button> _adjustButtons = new();
    private readonly PhotoSlider _adjustSlider = new() { Width = 460, Height = 30 };
    private readonly TextBlock _adjustName, _adjustValue;
    private readonly Control _adjustPage, _filterPage, _cropPage, _drawPage;
    private readonly Border _barHost;
    private readonly ScrollViewer _filterScroll = new();
    private readonly GlassChrome _chrome;
    private readonly Grid _prompt;
    private readonly List<TextBlock> _adjustNumbers = new();
    private Tool _lastDrawTool = Tool.Pen;
    private int _adjustIndex;
    private bool _syncing;

    public bool IsOpen => IsVisible;

    // Icone delle regolazioni, nell'ordine di PhotoEditCanvas.Adjustments.
    private static readonly string[] AdjustIcons =
    {
        "adj-exposure", "adj-brilliance", "adj-highlights", "adj-shadows", "adj-contrast", "sun-medium", "adj-black",
        "adj-saturation", "adj-vibrance", "thermometer", "adj-tint",
        "adj-sharpness", "adj-definition", "adj-vignette",
    };

    public PhotoEditView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(14, 15, 18));
        Styles.Add(PhotoUi.Styles());

        // Sfondo: la foto stessa, sfocata e scurita: il vetro ha qualcosa da rifrangere.
        _backdrop = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        RenderOptions.SetBitmapInterpolationMode(_backdrop, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        _backdropShade = new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(215, 8, 9, 12), 0), new GradientStop(Color.FromArgb(150, 8, 9, 12), 0.25), new GradientStop(Color.FromArgb(175, 8, 9, 12), 1) },
            },
        };
        Children.Add(_backdrop); Children.Add(_backdropShade);

        // La foto al centro, fra le schede e la capsula in basso (spazio fisso: la foto non salta cambiando scheda).
        _canvas.Margin = new Thickness(28, 58, 28, 150);
        Children.Add(_canvas);

        // In alto, centrate sulla finestra: le schede di testo, la scelta bianca con il trattino sotto.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (tab, label) in new[] { (Tab.Adjust, "Regola"), (Tab.Filters, "Filtri"), (Tab.Crop, "Ritaglia"), (Tab.Draw, "Disegna") })
        {
            var text = new TextBlock { Text = L10n.T(label), FontFamily = PhotoUi.Font, FontSize = 15, Foreground = PhotoUi.Soft, HorizontalAlignment = HorizontalAlignment.Center };
            var mark = new Border { Width = 18, Height = 2.5, CornerRadius = new CornerRadius(1.25), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Opacity = 0, IsVisible = false };
            var hit = new Border { Child = new StackPanel { Children = { text, mark } }, Background = Brushes.Transparent, Padding = new Thickness(2, 8, 2, 4), Cursor = new Cursor(StandardCursorType.Hand) };
            hit.PointerEntered += (_, _) => RefreshTabs();
            hit.PointerExited += (_, _) => RefreshTabs();
            hit.PointerPressed += (_, e) => { e.Handled = true; ShowTab(tab); };
            tabs.Children.Add(hit);
            _tabs[tab] = hit; _tabMarks[tab] = mark;
        }

        // A destra la X di sempre, con annulla e ripeti accanto (solo quando servono):
        // se c'e' qualcosa da salvare, la domanda compare a schermo sulla vignettatura.
        _undo = PhotoUi.IconButton("undo", "Annulla l’ultima modifica (⌘Z)", () => _canvas.Choose(Tool.Undo), 19);
        _redo = PhotoUi.IconButton("redo", "Ripeti (⇧⌘Z)", () => _canvas.Choose(Tool.Redo), 19);
        var close = PhotoUi.IconButton("x", "Chiudi (Esc)", RequestClose, 20);
        close.Classes.Remove("ph"); close.Classes.Add("phX");
        // In alto a destra, accanto alla X: annulla e ripeti (compaiono solo quando servono).
        var history = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 12, 0), Children = { _undo, _redo, new Border { Width = 6 }, close } };
        _prompt = BuildPrompt();

        // I comandi della scheda: una capsula di vetro in basso al centro.
        _adjustName = PhotoUi.Label("", 13, FontWeight.SemiBold, PhotoUi.Ink);
        _adjustValue = PhotoUi.Label("", 13, FontWeight.Normal, PhotoUi.Soft);
        _adjustPage = BuildAdjustPage();
        _filterPage = BuildFilterPage();
        _cropPage = BuildCropPage();
        _drawPage = BuildDrawPage();
        var pages = new Grid { Children = { _adjustPage, _filterPage, _cropPage, _drawPage } };
        _barHost = GlassChrome.Capsule(pages, 24, new Thickness(14, 10, 14, 10));
        _barHost.HorizontalAlignment = HorizontalAlignment.Center; _barHost.VerticalAlignment = VerticalAlignment.Bottom;
        _barHost.Margin = new Thickness(24, 0, 24, 18);

        Children.Add(_barHost); Children.Add(tabs); Children.Add(history); Children.Add(_prompt);
        _chrome = new GlassChrome(this, () => new Visual[] { _canvas, _backdrop, _backdropShade, tabs, history, _prompt });
        _chrome.Add(_barHost, 24);

        _canvas.StateChanged += Refresh;
        _canvas.CloseRequested += RequestClose;
        _canvas.Loaded += () => { _backdrop.Source = _canvas.Backdrop; LoadFilterPreviews(); _chrome.DirtyAll(); };
        _canvas.Closed += saved => { _chrome.Stop(); IsVisible = false; Closed?.Invoke(saved); };
    }

    private static Button BarIcon(string icon, string tip, Action run, double size = 20)
    {
        var b = PhotoUi.IconButton(icon, tip, run, size);
        b.Width = 40; b.Height = 38;
        return b;
    }

    private static Border Divider() => new() { Width = 1, Height = 22, Margin = new Thickness(10, 0), Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };

    // ------------------------------------------------------------------ Regola: icone + un cursore (come Foto su iPhone)
    private Control BuildAdjustPage()
    {
        // Ogni icona ha sotto il suo valore (vuoto a zero); sopra il nome della scelta e il righello.
        Control Item(Button b, TextBlock? number)
        {
            var col = new StackPanel { Spacing = 0, Children = { b } };
            if (number != null) col.Children.Add(number);
            else col.Children.Add(new Border { Height = 14 });
            return col;
        }
        var auto = BarIcon("wand-sparkles", "Migliora automaticamente: corregge solo ciò che serve", () => { _canvas.AutoEnhance(); SyncSliders(); });
        var icons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { Item(auto, null), Divider() } };
        for (int i = 0; i < PhotoEditCanvas.Adjustments.Length; i++)
        {
            int which = i;
            // Un divisore fra i gruppi di Foto (Luce, Colore, Dettagli).
            if (i > 0 && PhotoEditCanvas.Adjustments[i].Section != PhotoEditCanvas.Adjustments[i - 1].Section) icons.Children.Add(Divider());
            var b = BarIcon(AdjustIcons[i], PhotoEditCanvas.AdjustNames[i], () => SelectAdjust(which), 20);
            b.Width = 42;
            var number = new TextBlock { FontFamily = PhotoUi.Font, FontSize = 10.5, Foreground = PhotoUi.Soft, Height = 14, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
            icons.Children.Add(Item(b, number));
            _adjustButtons.Add(b); _adjustNumbers.Add(number);
        }
        _adjustSlider.ValueChanged += v =>
        {
            if (_syncing) return;
            _canvas.SetAdjust(_adjustIndex, v / 100);
            ShowAdjustValue();
            _chrome.Dirty(_barHost);
        };
        ToolTip.SetTip(_adjustSlider, L10n.T("Trascina per regolare, doppio clic per tornare a zero"));
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { _adjustName, _adjustValue } };
        _adjustSlider.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel { Spacing = 2, Children = { title, _adjustSlider, icons } };
    }

    private void SelectAdjust(int which)
    {
        _adjustIndex = which;
        _syncing = true;
        _adjustSlider.Value = Math.Round(_canvas.GetAdjust(which) * 100);
        _syncing = false;
        _adjustName.Text = L10n.T(PhotoEditCanvas.AdjustNames[which]);
        ShowAdjustValue();
        for (int i = 0; i < _adjustButtons.Count; i++) _adjustButtons[i].Classes.Set("on", i == which);
        _chrome.Dirty(_barHost);
    }

    private static string Signed(double v) => v == 0 ? "" : (v > 0 ? "+" : "") + v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    private void ShowAdjustValue()
    {
        double v = Math.Round(_canvas.GetAdjust(_adjustIndex) * 100) + 0.0; // + 0.0: niente "-0"
        _adjustValue.Text = v == 0 ? "0" : Signed(v);
        for (int i = 0; i < _adjustNumbers.Count; i++) _adjustNumbers[i].Text = Signed(Math.Round(_canvas.GetAdjust(i) * 100) + 0.0);
    }

    private void SyncSliders() => SelectAdjust(_adjustIndex);

    // ------------------------------------------------------------------ Filtri: striscia di anteprime
    private Control BuildFilterPage()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (int i = 0; i < PhotoEditCanvas.Filters.Length; i++)
        {
            int index = i;
            var preview = new Image { Stretch = Stretch.UniformToFill };
            // L'anteprima ritagliata dentro un anello separato (2 px di stacco): un bordo sulla stessa Border finiva
            // sotto l'immagine e si vedeva a pezzi.
            var picture = new Border { Width = 116, Height = 78, CornerRadius = new CornerRadius(9), ClipToBounds = true, Child = preview, Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) };
            var frame = new Border { CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2), Padding = new Thickness(2), BorderBrush = Brushes.Transparent, Child = picture };
            var name = PhotoUi.Label(PhotoEditCanvas.Filters[i].Name, 11.5, FontWeight.Normal, PhotoUi.Soft);
            name.HorizontalAlignment = HorizontalAlignment.Center; name.MaxWidth = 124; name.FontSize = 12; name.Margin = new Thickness(0, 3, 0, 0);
            var tile = new StackPanel { Width = 126, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Children = { frame, name } };
            tile.PointerEntered += (_, _) => { name.Foreground = PhotoUi.AccentBrush; _chrome.Dirty(_barHost); };
            tile.PointerExited += (_, _) => { RefreshFilters(); _chrome.Dirty(_barHost); };
            tile.PointerPressed += (_, e) => { e.Handled = true; _canvas.SetFilter(index); };
            row.Children.Add(tile);
            _filterPreviews.Add(preview); _filterNames.Add(name); _filterFrames.Add(frame);
        }
        _filterScroll.Content = row;
        _filterScroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        _filterScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        _filterScroll.ScrollChanged += (_, _) => _chrome.Dirty(_barHost);
        // La rotella verticale scorre la striscia.
        _filterScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            // Il trackpad in orizzontale scorre da se' (fluido); la rotella verticale si gira in orizzontale.
            if (Math.Abs(e.Delta.X) >= Math.Abs(e.Delta.Y)) return;
            double max = Math.Max(0, _filterScroll.Extent.Width - _filterScroll.Viewport.Width);
            _filterScroll.Offset = new Vector(Math.Clamp(_filterScroll.Offset.X - e.Delta.Y * 50, 0, max), 0);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return _filterScroll;
    }

    private void RefreshFilters()
    {
        for (int i = 0; i < _filterFrames.Count; i++)
        {
            bool on = i == _canvas.CurrentFilter;
            _filterFrames[i].BorderBrush = on ? Brushes.White : Brushes.Transparent;
            _filterNames[i].Foreground = on ? PhotoUi.Ink : PhotoUi.Soft;
            _filterNames[i].FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private void LoadFilterPreviews()
    {
        for (int i = 0; i < _filterPreviews.Count; i++)
        {
            int index = i;
            _filterPreviews[i].Source = null;
            Dispatcher.UIThread.Post(() => { if (IsVisible) { _filterPreviews[index].Source = _canvas.FilterPreview(index, 200); _chrome.Dirty(_barHost); } }, DispatcherPriority.Background);
        }
    }

    // ------------------------------------------------------------------ Ritaglia
    private Control BuildCropPage()
    {
        var reset = PhotoUi.TextButton("Ripristina", () => _canvas.ResetCrop());
        reset.Padding = new Thickness(8, 6); reset.FontWeight = FontWeight.Normal;
        ToolTip.SetTip(reset, L10n.T("Torna alla foto intera"));
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                BarIcon("rotate-ccw-square", "Ruota a sinistra", () => { _canvas.Choose(Tool.Rotate); _canvas.Choose(Tool.Rotate); _canvas.Choose(Tool.Rotate); _canvas.Choose(Tool.Crop); }),
                BarIcon("rotate-cw-square", "Ruota a destra", () => { _canvas.Choose(Tool.Rotate); _canvas.Choose(Tool.Crop); }),
                BarIcon("flip-horizontal-2", "Rifletti", () => _canvas.Flip()),
                Divider(), reset,
            },
        };
    }

    // ------------------------------------------------------------------ Disegna: strumenti | colori | spessori
    private Control BuildDrawPage()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var t in new[] { Tool.Pen, Tool.Marker, Tool.Line, Tool.Arrow, Tool.Box, Tool.Ellipse, Tool.Text, Tool.Pixelate, Tool.Eraser })
        {
            var tool = t;
            var b = BarIcon(Icon(tool), PhotoEditCanvas.ToolName(tool), () => { _lastDrawTool = tool; _canvas.Choose(tool); });
            bar.Children.Add(b);
            _toolButtons[tool] = b;
        }
        bar.Children.Add(Divider());
        foreach (var ink in PhotoEditCanvas.Inks)
        {
            var ring = new Ellipse { Width = 28, Height = 28, Stroke = Brushes.White, StrokeThickness = 1.5, IsVisible = false };
            var dot = new Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(ink), Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), StrokeThickness = 1 };
            var b = new Button { Content = new Grid { Children = { ring, dot } } };
            b.Classes.Add("ph"); b.Width = 32; b.Height = 38;
            var chosen = ink;
            b.Click += (_, _) => _canvas.SetColor(chosen);
            bar.Children.Add(b);
            _swatches.Add((ink, b, ring));
        }
        _customRing = new Ellipse { Width = 28, Height = 28, Stroke = Brushes.White, StrokeThickness = 1.5, IsVisible = false };
        _customFill = new Ellipse
        {
            Width = 20, Height = 20,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new(Color.FromRgb(255, 80, 80), 0), new(Color.FromRgb(255, 210, 60), .25), new(Color.FromRgb(70, 200, 120), .5), new(Color.FromRgb(70, 140, 255), .75), new(Color.FromRgb(190, 90, 230), 1) },
            },
        };
        var custom = new Button { Content = new Grid { Children = { _customRing, _customFill } } };
        custom.Classes.Add("ph"); custom.Width = 32; custom.Height = 38;
        ToolTip.SetTip(custom, L10n.T("Altri colori…"));
        custom.Click += (_, _) => MacColorPanel.Show(_canvas.CurrentColor, "Colore", c => _canvas.SetColor(c));
        bar.Children.Add(custom);
        _picker = BarIcon("pipette", PhotoEditCanvas.ToolName(Tool.Picker), () => _canvas.Choose(Tool.Picker), 18);
        bar.Children.Add(_picker);
        bar.Children.Add(Divider());
        for (int i = 0; i < 3; i++)
        {
            double d = new[] { 5.0, 9.0, 14.0 }[i];
            var dot = new Ellipse { Width = d, Height = d, Fill = PhotoUi.Soft };
            var b = new Button { Content = dot };
            b.Classes.Add("ph"); b.Width = 32; b.Height = 38;
            ToolTip.SetTip(b, L10n.T(new[] { "Sottile", "Medio", "Spesso" }[i]));
            int size = i;
            b.Click += (_, _) => _canvas.SetSize(size);
            b.PointerEntered += (_, _) => { dot.Fill = PhotoUi.AccentBrush; _chrome.Dirty(_barHost); };
            b.PointerExited += (_, _) => Refresh();
            bar.Children.Add(b);
            _sizes.Add((b, dot));
        }
        return bar;
    }

    private static string Icon(Tool tool) => tool switch
    {
        Tool.Pen => "pen-tool", Tool.Marker => "highlighter", Tool.Line => "slash", Tool.Arrow => "move-up-right", Tool.Box => "square",
        Tool.Ellipse => "circle", Tool.Pixelate => "grid-3x3", Tool.Eraser => "eraser", _ => "type",
    };

    // ------------------------------------------------------------------ schede e stato
    private void ShowTab(Tab tab)
    {
        _canvas.Choose(tab switch { Tab.Adjust => Tool.Adjust, Tab.Filters => Tool.Filter, Tab.Crop => Tool.Crop, _ => _lastDrawTool });
    }

    private void RefreshTabs()
    {
        var current = CurrentTab;
        foreach (var (t, hit) in _tabs)
        {
            var text = (TextBlock)((StackPanel)hit.Child!).Children[0];
            bool on = t == current;
            text.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
            text.Foreground = hit.IsPointerOver && !on ? PhotoUi.AccentBrush : on ? PhotoUi.Ink : PhotoUi.Soft;
        }
    }

    private Tab CurrentTab => _canvas.CurrentTool switch { Tool.Adjust => Tab.Adjust, Tool.Filter => Tab.Filters, Tool.Crop => Tab.Crop, _ => Tab.Draw };

    private void Refresh()
    {
        var tool = _canvas.CurrentTool;
        var tab = CurrentTab;
        RefreshTabs();
        _adjustPage.IsVisible = tab == Tab.Adjust; _filterPage.IsVisible = tab == Tab.Filters; _cropPage.IsVisible = tab == Tab.Crop; _drawPage.IsVisible = tab == Tab.Draw;
        foreach (var (t, b) in _toolButtons) b.Classes.Set("on", t == tool);
        if (_picker != null) _picker.Classes.Set("on", tool == Tool.Picker);
        _undo.IsVisible = _canvas.CanUndo || _canvas.CanRedo; _undo.IsEnabled = _canvas.CanUndo;
        _redo.IsVisible = _canvas.CanRedo;
        bool preset = false;
        foreach (var (ink, _, ring) in _swatches) { ring.IsVisible = ink == _canvas.CurrentColor; preset |= ring.IsVisible; }
        if (_customRing != null && _customFill != null)
        {
            _customRing.IsVisible = !preset;
            _rainbow ??= _customFill.Fill;
            _customFill.Fill = preset ? _rainbow : new SolidColorBrush(_canvas.CurrentColor);
        }
        for (int i = 0; i < _sizes.Count; i++) _sizes[i].Dot.Fill = i == _canvas.CurrentSize ? PhotoUi.Ink : new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
        RefreshFilters();
        _chrome.DirtyAll();
    }

    // La striscia dei filtri occupa al massimo la larghezza della finestra, le altre capsule solo il necessario.
    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); _filterScroll.MaxWidth = Math.Max(300, e.NewSize.Width - 100); }

    // ------------------------------------------------------------------ apertura e tasti
    public bool Open(string path)
    {
        if (!_canvas.Open(path)) return false;
        IsVisible = true;
        _lastDrawTool = Tool.Pen;
        foreach (var p in _filterPreviews) p.Source = null;
        _backdrop.Source = null;
        _adjustIndex = 0;
        SyncSliders();
        // Come Foto: si parte da Regola.
        _canvas.Choose(Tool.Adjust);
        Refresh();
        _canvas.Focus();
        _chrome.Start();
        return true;
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (_prompt.IsVisible)
        {
            if (e.Key == Key.Escape) HidePrompt();
            else if (e.Key is Key.Enter or Key.Return) { HidePrompt(); _canvas.Save(); }
            else return;
            e.Handled = true;
            return;
        }
        _canvas.HandleKey(e);
    }

    // ------------------------------------------------------------------ chiusura: salvare o scartare?
    private void RequestClose()
    {
        if (!_canvas.HasChanges) { _canvas.Cancel(); return; }
        _prompt.IsVisible = true;
        // Il vetro sta in una finestra sopra l'app: coprirebbe la domanda. Torna capsula scura sotto la vignettatura.
        _chrome.MenuOpen = true;
    }

    private void HidePrompt() { _prompt.IsVisible = false; _chrome.MenuOpen = false; _chrome.DirtyAll(); _canvas.Focus(); }

    private Grid BuildPrompt()
    {
        var title = PhotoUi.Label("Salvare le modifiche?", 22, FontWeight.SemiBold, PhotoUi.Ink);
        var detail = PhotoUi.Label("Viene salvata una copia accanto all’originale, che resta com’è.", 13.5, FontWeight.Normal, PhotoUi.Soft);
        title.HorizontalAlignment = detail.HorizontalAlignment = HorizontalAlignment.Center;
        var discard = PhotoUi.TextButton("Scarta modifiche", () => { HidePrompt(); _canvas.Cancel(); });
        discard.Classes.Add("secondary");
        var save = PhotoUi.TextButton("Salva una copia", () => { HidePrompt(); _canvas.Save(); }, primary: true);
        foreach (var b in new[] { discard, save }) { b.Height = 38; b.MinWidth = 160; b.CornerRadius = new CornerRadius(19); b.HorizontalContentAlignment = HorizontalAlignment.Center; b.VerticalContentAlignment = VerticalAlignment.Center; }
        var keep = PhotoUi.TextButton("Continua a modificare", HidePrompt);
        keep.FontWeight = FontWeight.Normal; keep.HorizontalAlignment = HorizontalAlignment.Center;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), Children = { discard, save } };
        var column = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { title, detail, buttons, keep } };
        var grid = new Grid
        {
            IsVisible = false,
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(205, 0, 0, 0), 0), new GradientStop(Color.FromArgb(150, 0, 0, 0), 1) },
            },
            Children = { column },
        };
        grid.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, grid)) { e.Handled = true; HidePrompt(); } };
        return grid;
    }

    internal void QaTool(string tool)
    {
        if (tool == "auto") { _canvas.AutoEnhance(); SyncSliders(); return; }
        if (tool == "close") { RequestClose(); return; }
        if (tool.StartsWith("crop:")) { var v = tool[5..].Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); _canvas.QaCrop(v[0], v[1], v[2], v[3]); return; }
        if (tool.StartsWith("adj:")) { SelectAdjust(int.Parse(tool[4..])); return; }
        if (tool == "scroll")
        {
            int n = 0;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (s, _) =>
            {
                _filterScroll.Offset = new Vector(_filterScroll.Offset.X + 12, 0);
                if (++n >= 120) ((DispatcherTimer)s!).Stop();
            });
            timer.Start();
            return;
        }
        if (Enum.TryParse<Tool>(tool, true, out var t)) _canvas.Choose(t);
    }
}
