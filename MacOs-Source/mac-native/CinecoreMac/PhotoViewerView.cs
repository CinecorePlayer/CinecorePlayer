using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CinecoreMac;

/// <summary>
/// Visualizzatore delle foto: la foto al centro su nero (zoom, spostamento, rotazione, riflesso e orientamento EXIF come
/// trasformazioni dell'immagine) e i comandi su vetro vero di macOS (Liquid Glass da macOS 26, come la barra della
/// musica): in alto a sinistra nome e data, in alto a destra preferito, informazioni e chiusura, in basso la capsula dei
/// comandi e le miniature, frecce ai lati, il pannello Info a destra. I comandi sono controlli di Avalonia (clic,
/// suggerimenti, stati) resi invisibili nella finestra e ridisegnati come immagine sopra il vetro (MacSurfaceGlass);
/// senza il vetro nativo restano visibili su capsule scure. La cornice sparisce da sola quando il mouse sta fermo.
/// </summary>
internal sealed class PhotoViewerView : Grid
{
    public event Action? Closed;
    public event Action<string>? EditRequested;
    public event Action<string>? Deleted;
    /// <summary>Menu di testo dell'app (Altro, conferma del Cestino); il punto e' il bordo destro dell'elenco.</summary>
    public event Action<IReadOnlyList<MenuEntry>, Point>? MenuRequested;

    private List<LibraryItem> _items = new();
    private int _index;
    private double _zoom = 1;
    private int _rotation, _exifTurns;
    private bool _flip;
    private Vector _pan;
    private Point? _drag;
    private readonly DispatcherTimer _slideshow, _idle;

    private readonly Panel _stage = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Border _vignetteTop, _vignetteBottom, _vignetteSides;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = RelativePoint.Center };
    private readonly TextBlock _title, _subtitle, _zoomText, _toast;
    private readonly Border _titleHost, _actionsHost, _closeHost, _toolsHost, _thumbsHost, _prevHost, _nextHost, _toastHost, _infoHost;
    private readonly Button _prev, _next, _playButton, _shareButton, _deleteButton, _moreButton, _favButton, _infoButton, _editButton, _closeButton;
    private Control _thumbsDivider = null!;
    private readonly StackPanel _thumbs = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly ScrollViewer _thumbScroll;
    private readonly StackPanel _infoRows = new() { Spacing = 14 };
    private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _thumbCache = new(StringComparer.Ordinal);
    private int _loadJob, _infoJob;
    private bool _infoOpen;

    public bool IsOpen => IsVisible;
    public string? CurrentPath => _items.Count > 0 ? _items[_index].Path : null;

    // Vetro vero per le capsule (GlassChrome, lo stesso dell'editor).
    private readonly GlassChrome _chrome;
    private bool _glassOn => _chrome.On;

    public PhotoViewerView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        Background = Brushes.Black;
        Styles.Add(PhotoUi.Styles());

        // Foto: occupa tutta la finestra, i comandi galleggiano sopra (come Foto di macOS).
        _stage.Children.Add(_image);
        _stage.Margin = new Thickness(0);
        Children.Add(_stage);
        // Vignettatura sopra e sotto: stacca comandi e bordi della foto dal nero e dice dove finisce la foto.
        _vignetteTop = new Border
        {
            Height = 150, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(170, 0, 0, 0), 0), new GradientStop(Color.FromArgb(70, 0, 0, 0), 0.45), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
            },
        };
        _vignetteBottom = new Border
        {
            Height = 230, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.5), new GradientStop(Color.FromArgb(190, 0, 0, 0), 1) },
            },
        };
        // Bordi laterali appena scuriti: la foto non si confonde col nero ai lati.
        _vignetteSides = new Border
        {
            IsHitTestVisible = false,
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55), new GradientStop(Color.FromArgb(120, 0, 0, 0), 1) },
            },
        };
        Children.Add(_vignetteSides); Children.Add(_vignetteTop); Children.Add(_vignetteBottom);

        // In alto a sinistra, sulla vignettatura e senza riquadro: nome e data (dopo i semafori della finestra).
        _title = PhotoUi.Label("", 15, FontWeight.SemiBold, PhotoUi.Ink);
        _subtitle = PhotoUi.Label("", 12.5, FontWeight.Normal, PhotoUi.Soft);
        _title.MaxWidth = 520; _subtitle.MaxWidth = 520;
        _titleHost = new Border
        {
            Child = new StackPanel { Spacing = 2, Children = { _title, _subtitle } },
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(88, 13, 0, 0), IsHitTestVisible = false,
        };

        // In alto a destra una sola capsula di vetro: preferito, informazioni, presentazione, condividi, altro e
        // il pulsante pieno Modifica. La X e' a parte, senza capsula, come le X del resto dell'app.
        _favButton = PhotoUi.IconButton("heart", "Aggiungi ai preferiti (F)", ToggleFavorite, 18);
        _infoButton = PhotoUi.IconButton("info", "Informazioni (I)", ToggleInfo, 19);
        _playButton = PhotoUi.IconButton("play", "Presentazione (Spazio)", ToggleSlideshow, 18);
        _shareButton = PhotoUi.IconButton("share", "Condividi", Share, 18);
        _moreButton = PhotoUi.IconButton("ellipsis", "Altro", ShowMore, 20);
        _deleteButton = PhotoUi.IconButton("trash-2", "Sposta nel Cestino (⌫)", ConfirmDelete, 18);
        _editButton = PhotoUi.TextButton("Modifica", Edit, primary: true);
        ToolTip.SetTip(_editButton, L10n.T("Modifica (E)"));
        _editButton.Height = 30; _editButton.Padding = new Thickness(15, 0); _editButton.CornerRadius = new CornerRadius(15);
        _editButton.VerticalContentAlignment = VerticalAlignment.Center; _editButton.Margin = new Thickness(6, 0, 2, 0);
        _actionsHost = Part(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _favButton, _infoButton, _playButton, _shareButton, PhotoUi.Divider(),
                PhotoUi.IconButton("rotate-ccw", "Ruota a sinistra (⇧R)", () => Rotate(-1), 18),
                PhotoUi.IconButton("rotate-cw", "Ruota a destra (R)", () => Rotate(1), 18),
                PhotoUi.IconButton("flip-horizontal-2", "Rifletti (H)", Flip, 18),
                _deleteButton, _moreButton, _editButton,
            },
        }, 22, new Thickness(6, 4, 6, 4));
        _closeButton = PhotoUi.IconButton("x", "Chiudi (Esc)", Close, 20);
        _closeButton.Classes.Remove("ph"); _closeButton.Classes.Add("phX");
        var topRight = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0), Children = { _actionsHost, _closeButton },
        };
        _closeHost = new Border();

        // Frecce ai lati come quelle delle file di Trakt: solo lo chevron, bianco tenue e pieno al passaggio (mai
        // d'accento), niente cerchio. Compaiono vicino ai bordi.
        _prev = ArrowButton("chevron-left", "Foto precedente (←)", () => Step(-1));
        _next = ArrowButton("chevron-right", "Foto successiva (→)", () => Step(1));
        _prevHost = new Border { Child = _prev, Opacity = 0, IsHitTestVisible = false };
        _nextHost = new Border { Child = _next, Opacity = 0, IsHitTestVisible = false };
        foreach (var h in new[] { _prevHost, _nextHost })
            h.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) } };
        _prevHost.HorizontalAlignment = HorizontalAlignment.Left; _nextHost.HorizontalAlignment = HorizontalAlignment.Right;
        _prevHost.VerticalAlignment = _nextHost.VerticalAlignment = VerticalAlignment.Center;
        _prevHost.Margin = new Thickness(12, 0, 0, 0); _nextHost.Margin = new Thickness(0, 0, 12, 0);

        // In basso soltanto le miniature su vetro; zoom, rotazione e Cestino stanno in tastiera, rotella e menu Altro.
        _zoomText = PhotoUi.Label("100%", 12.5, FontWeight.SemiBold, PhotoUi.Ink);
        _toolsHost = new Border();
        _thumbScroll = new ScrollViewer
        {
            Content = _thumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxWidth = 13 * 52 + 12 * 6 + 16,
        };
        _thumbScroll.ScrollChanged += (_, _) => Dirty(_thumbsHost!);
        // A sinistra delle miniature lo zoom: riduci, percentuale (clic = adatta), ingrandisci.
        _zoomText.Width = 46; _zoomText.TextAlignment = TextAlignment.Center;
        var zoomReset = new Button { Content = _zoomText, Width = 50, Height = 36 };
        zoomReset.Classes.Add("phText"); zoomReset.Padding = new Thickness(0); zoomReset.Click += (_, _) => ResetView();
        ToolTip.SetTip(zoomReset, L10n.T("Adatta alla finestra (0)"));
        var zoomGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Children = { PhotoUi.IconButton("zoom-out", "Riduci (−)", () => Zoom(1 / 1.25), 18), zoomReset, PhotoUi.IconButton("zoom-in", "Ingrandisci (+)", () => Zoom(1.25), 18) },
        };
        _thumbsDivider = PhotoUi.Divider();
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Children = { zoomGroup, _thumbsDivider, _thumbScroll } };
        _thumbsHost = Part(strip, 18, new Thickness(6, 7, 8, 7));
        _thumbsHost.HorizontalAlignment = HorizontalAlignment.Center; _thumbsHost.VerticalAlignment = VerticalAlignment.Bottom;
        _thumbsHost.Margin = new Thickness(0, 0, 0, 18);

        // Pannello Info a destra, sotto la capsula.
        _infoHost = Part(new StackPanel { Width = 272, Children = { _infoRows } }, 22, new Thickness(18, 16, 18, 16));
        _infoHost.HorizontalAlignment = HorizontalAlignment.Right; _infoHost.VerticalAlignment = VerticalAlignment.Top;
        _infoHost.Margin = new Thickness(0, 64, 12, 0);
        _infoHost.IsVisible = false;

        // Messaggio breve in basso al centro, sopra le miniature.
        _toast = PhotoUi.Label("", 13, FontWeight.SemiBold, PhotoUi.Ink);
        _toastHost = Part(_toast, 17, new Thickness(16, 8));
        _toastHost.HorizontalAlignment = HorizontalAlignment.Center; _toastHost.VerticalAlignment = VerticalAlignment.Bottom;
        _toastHost.Margin = new Thickness(0, 0, 0, 104);
        _toastHost.IsVisible = false; _toastHost.IsHitTestVisible = false;

        Children.Add(_prevHost); Children.Add(_nextHost);
        Children.Add(_titleHost); Children.Add(topRight); Children.Add(_thumbsHost); Children.Add(_infoHost); Children.Add(_toastHost);
        _chrome = new GlassChrome(this, () => new Visual[] { _stage, _vignetteTop, _vignetteBottom, _vignetteSides, _titleHost, _closeButton });
        _chrome.Add(_actionsHost, 22); _chrome.Add(_thumbsHost, 18);
        _chrome.Add(_infoHost, 22, () => _infoOpen, chrome: false); _chrome.Add(_toastHost, 17, () => _toastHost.IsVisible, chrome: false);
        // Testi e X senza vetro seguono la cornice; la vignettatura resta un poco anche a cornice nascosta (bordi della foto).
        _chrome.AlphaChanged += a =>
        {
            _vignetteTop.Opacity = _vignetteBottom.Opacity = a;
            _vignetteSides.Opacity = 0.6 + 0.4 * a;
            _titleHost.Opacity = _closeButton.Opacity = a;
            _closeButton.IsHitTestVisible = a > 0.5;
        };

        _slideshow = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) =>
        {
            if (_index >= _items.Count - 1) { StopSlideshow(); return; }
            Step(1);
        });
        _idle = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => SetChrome(false));
        // Questo costruttore di DispatcherTimer parte subito: la presentazione partiva da sola dopo 5 s.
        _slideshow.Stop(); _idle.Stop();
        _stage.SizeChanged += (_, _) => { ApplyTransform(); LoadCurrent(); };
    }

    private static Button ArrowButton(string icon, string tip, Action click)
    {
        var button = PhotoUi.IconButton(icon, tip, click, 34);
        button.Classes.Remove("ph"); button.Classes.Add("phX");
        button.Width = 56; button.Height = 72;
        return button;
    }

    /// <summary>Una capsula: su vetro vero e' trasparente (il vetro sta nella finestra figlia), altrimenti scura.</summary>
    private static Border Part(Control child, double radius, Thickness padding) => new GlassCapsule
    {
        Child = child, CornerRadius = new CornerRadius(radius), Padding = padding,
        Background = PhotoUi.CapsuleFill, BorderBrush = PhotoUi.CapsuleEdge, BorderThickness = new Thickness(1),
    };

    private void Dirty(Border host) => _chrome.Dirty(host);
    private void DirtyAll() => _chrome.DirtyAll();
    private void StartGlass() => _chrome.Start();
    private void StopGlass() => _chrome.Stop();

    // ------------------------------------------------------------------ apertura e navigazione
    public void Open(IReadOnlyList<LibraryItem> items, int index)
    {
        _items = items.ToList();
        _index = Math.Clamp(index, 0, Math.Max(0, _items.Count - 1));
        IsVisible = true;
        Opacity = 0;
        Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) } };
        Opacity = 1;
        Focus();
        // Il vetro dell'HUD (finestra nativa) compariva subito pieno mentre la foto entrava in 200 ms: lampeggiava.
        _chrome.SnapAlpha(0);
        _chrome.Target = 1;
        ShowCurrent();
        SetChrome(true);
        StartGlass();
    }

    public void Close()
    {
        if (!IsVisible) return;
        StopSlideshow();
        _idle.Stop();
        StopGlass();
        _infoOpen = false; _infoHost.IsVisible = false;
        IsVisible = false;
        // Prima si staccano tutte le Image (foto e miniature), poi si liberano le bitmap.
        _image.Source = null;
        _thumbs.Children.Clear();
        foreach (var b in _cache.Values) b.Dispose();
        _cache.Clear();
        foreach (var b in _thumbCache.Values) b.Dispose();
        _thumbCache.Clear();
        Closed?.Invoke();
    }

    private void Step(int delta)
    {
        if (_items.Count == 0) return;
        int next = Math.Clamp(_index + delta, 0, _items.Count - 1);
        if (next == _index) return;
        _index = next;
        ShowCurrent();
    }

    private void GoTo(int index)
    {
        if (index == _index || index < 0 || index >= _items.Count) return;
        _index = index;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        if (_items.Count == 0) { Close(); return; }
        var item = _items[_index];
        _zoom = 1; _rotation = 0; _flip = false; _pan = default;
        _exifTurns = ExifTurns(item.Path);
        _title.Text = item.Title;
        string date = "";
        if (item.SortDateUtc > DateTime.MinValue.AddDays(1))
        {
            // L'app gira senza dati di cultura (InvariantGlobalization): i mesi in italiano si scrivono qui.
            var d = item.SortDateUtc.ToLocalTime();
            string[] months = { "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre" };
            date = $"{d.Day} {L10n.T(months[d.Month - 1])} {d.Year}, {d.Hour:00}:{d.Minute:00}";
        }
        string count = _items.Count > 1 ? L10n.T($"{_index + 1} di {_items.Count}") : "";
        _subtitle.Text = date.Length > 0 && count.Length > 0 ? date + "      " + count : date + count;
        bool local = !item.Path.Contains("://", StringComparison.Ordinal);
        _shareButton.IsEnabled = _deleteButton.IsEnabled = _moreButton.IsEnabled = _editButton.IsEnabled = local;
        RefreshFavorite();
        RefreshArrows();
        ApplyTransform();
        BuildThumbs();
        LoadCurrent();
        if (_infoOpen) LoadInfo();
        DirtyAll();
    }

    /// <summary>Quarti di giro dall'EXIF (le foto del telefono salvate di lato): letti dall'intestazione, senza decodificare.</summary>
    private static int ExifTurns(string path)
    {
        try
        {
            if (path.Contains("://", StringComparison.Ordinal)) return 0;
            using var codec = SkiaSharp.SKCodec.Create(path);
            return codec?.EncodedOrigin switch
            {
                SkiaSharp.SKEncodedOrigin.RightTop => 1, SkiaSharp.SKEncodedOrigin.BottomRight => 2, SkiaSharp.SKEncodedOrigin.LeftBottom => 3, _ => 0,
            };
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ immagini (in sottofondo, alla misura dello schermo)
    private void LoadCurrent()
    {
        if (!IsVisible || _items.Count == 0) return;
        var item = _items[_index];
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 2;
        int width = (int)Math.Clamp(Math.Ceiling(Math.Max(_stage.Bounds.Width, _stage.Bounds.Height) * scaling * Math.Min(_zoom, 2.5) / 256) * 256, 512, 6144);
        string key = item.Path + "|" + width;
        // Subito la miniatura (se c'e'), poi la foto alla misura giusta.
        if (_cache.TryGetValue(key, out var ready)) { _image.Source = ready; PreloadNeighbours(width); return; }
        if (_thumbCache.TryGetValue(item.ArtPath ?? item.Path, out var thumb)) _image.Source = thumb;
        int job = ++_loadJob;
        _ = Task.Run(() => Decode(item.Path, width)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (t.Result is not { } bmp) return;
            if (!IsVisible) { bmp.Dispose(); return; }
            Remember(key, bmp);
            if (job == _loadJob) _image.Source = bmp;
            PreloadNeighbours(width);
        }));
    }

    private void PreloadNeighbours(int width)
    {
        foreach (int near in new[] { _index + 1, _index - 1 })
        {
            if (near < 0 || near >= _items.Count) continue;
            string path = _items[near].Path, key = path + "|" + width;
            if (_cache.ContainsKey(key)) continue;
            _ = Task.Run(() => Decode(path, width)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (t.Result is { } bmp) { if (IsVisible) Remember(key, bmp); else bmp.Dispose(); }
            }));
        }
    }

    private void Remember(string key, Bitmap bmp)
    {
        if (_cache.Remove(key, out var old) && !ReferenceEquals(old, bmp)) Release(old);
        _cache[key] = bmp;
        // Tre misure grandi al massimo (corrente e vicine): la memoria non cresce scorrendo centinaia di foto.
        // Mai la foto appena arrivata (diventa la Source subito dopo) ne' quella a schermo: liberata, l'Image la
        // misurava al layout seguente e l'app crashava (NullReference in Bitmap.Size) scorrendo con le frecce.
        while (_cache.Count > 4)
        {
            var drop = _cache.FirstOrDefault(p => !ReferenceEquals(p.Value, bmp) && !ReferenceEquals(p.Value, _image.Source));
            if (drop.Value == null) break;
            _cache.Remove(drop.Key);
            Release(drop.Value);
        }
    }

    /// <summary>
    /// Libera una bitmap uscita dalla cache dopo il passaggio di layout, e solo se nessuno la mostra piu'
    /// (foto grande o miniatura). Se e' ancora in uso la lascia al GC.
    /// </summary>
    private void Release(Bitmap bmp) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(_image.Source, bmp) || _cache.ContainsValue(bmp) || _thumbCache.ContainsValue(bmp)) return;
        bmp.Dispose();
    }, DispatcherPriority.Background);

    private static Bitmap? Decode(string path, int width)
    {
        try
        {
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                string? file = NetworkStore.CachedArtAsync(path).GetAwaiter().GetResult();
                if (file == null) return null;
                path = file;
            }
            using var fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, width, BitmapInterpolationMode.HighQuality);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ miniature
    private const int ThumbWindow = 15;

    private void BuildThumbs()
    {
        _thumbs.Children.Clear();
        _thumbScroll.IsVisible = _thumbsDivider.IsVisible = _items.Count > 1;
        if (_items.Count <= 1) return;
        int first = Math.Max(0, _index - ThumbWindow), last = Math.Min(_items.Count - 1, _index + ThumbWindow);
        Control? current = null;
        for (int i = first; i <= last; i++)
        {
            int index = i;
            var item = _items[i];
            var picture = new Image { Stretch = Stretch.UniformToFill, Width = 52, Height = 40 };
            var frame = new Border
            {
                Width = 52, Height = 40, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = picture,
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(index == _index ? 2 : 0), BorderBrush = Brushes.White,
                Opacity = index == _index ? 1 : 0.6, Cursor = new Cursor(StandardCursorType.Hand),
            };
            frame.PointerEntered += (_, _) => { if (index != _index) frame.Opacity = 0.95; };
            frame.PointerExited += (_, _) => { if (index != _index) frame.Opacity = 0.6; };
            frame.PointerPressed += (_, e) => { e.Handled = true; GoTo(index); };
            ToolTip.SetTip(frame, item.Title);
            _thumbs.Children.Add(frame);
            if (index == _index) current = frame;
            LoadThumb(item, picture);
        }
        // La miniatura corrente al centro della fila.
        Dispatcher.UIThread.Post(() =>
        {
            if (current == null) return;
            double x = current.Bounds.X + current.Bounds.Width / 2 - _thumbScroll.Viewport.Width / 2;
            _thumbScroll.Offset = new Vector(Math.Max(0, x), 0);
        }, DispatcherPriority.Loaded);
    }

    private void LoadThumb(LibraryItem item, Image target)
    {
        string source = item.ArtPath ?? item.Path;
        if (_thumbCache.TryGetValue(source, out var ready)) { target.Source = ready; return; }
        _ = Task.Run(() => Decode(source, 256)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (t.Result is not { } bmp) return;
            if (!IsVisible) { bmp.Dispose(); return; }
            if (_thumbCache.TryGetValue(source, out var other)) { bmp.Dispose(); target.Source = other; Dirty(_thumbsHost); return; }
            _thumbCache[source] = bmp;
            target.Source = bmp;
            Dirty(_thumbsHost);
            if (_thumbCache.Count > 200) { var drop = _thumbCache.First(); _thumbCache.Remove(drop.Key); }
        }));
    }

    // ------------------------------------------------------------------ zoom, rotazione, spostamento
    private void Zoom(double factor)
    {
        double before = _zoom;
        _zoom = Math.Clamp(_zoom * factor, 1, 8);
        if (_zoom <= 1.001) { _zoom = 1; _pan = default; }
        if (Math.Abs(before - _zoom) > 0.001) { ApplyTransform(); if (_zoom > 1.6) LoadCurrent(); }
    }

    private void Rotate(int direction) { _rotation = ((_rotation + direction) % 4 + 4) % 4; _pan = default; ApplyTransform(); }

    private void Flip() { _flip = !_flip; ApplyTransform(); }

    private void ResetView() { _zoom = 1; _rotation = 0; _flip = false; _pan = default; ApplyTransform(); }

    private void ApplyTransform()
    {
        int turns = (_rotation + _exifTurns) % 4;
        // Di lato la foto si adatta con i lati scambiati: l'immagine si dimensiona al riquadro "girato".
        var box = _stage.Bounds.Size;
        bool quarter = turns % 2 == 1;
        _image.Width = quarter ? box.Height : box.Width;
        _image.Height = quarter ? box.Width : box.Height;
        _image.HorizontalAlignment = HorizontalAlignment.Center; _image.VerticalAlignment = VerticalAlignment.Center;
        _image.Margin = new Thickness(quarter ? (box.Width - box.Height) / 2 : 0, quarter ? (box.Height - box.Width) / 2 : 0, quarter ? (box.Width - box.Height) / 2 : 0, quarter ? (box.Height - box.Width) / 2 : 0);
        _image.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(_flip ? -_zoom : _zoom, _zoom), new RotateTransform(90 * turns), new TranslateTransform(_pan.X, _pan.Y) },
        };
        _zoomText.Text = $"{Math.Round(_zoom * 100)}%";
        Dirty(_thumbsHost);
        _stage.Cursor = _zoom > 1 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        RefreshArrows();
    }

    private void RefreshArrows()
    {
        bool whole = _zoom <= 1;
        _prevHost.IsVisible = whole && _index > 0;
        _nextHost.IsVisible = whole && _index < _items.Count - 1;
    }

    // ------------------------------------------------------------------ preferiti e informazioni
    private void ToggleFavorite()
    {
        if (_items.Count == 0) return;
        string path = _items[_index].Path;
        Favorites.Toggle(path);
        RefreshFavorite();
        Toast(Favorites.Contains(path) ? "Aggiunta ai preferiti" : "Tolta dai preferiti");
    }

    private void RefreshFavorite()
    {
        bool on = _items.Count > 0 && Favorites.Contains(_items[_index].Path);
        if (on) _favButton.Classes.Add("on"); else _favButton.Classes.Remove("on");
        ToolTip.SetTip(_favButton, L10n.T(on ? "Togli dai preferiti (F)" : "Aggiungi ai preferiti (F)"));
        _favButton.Foreground = on ? new SolidColorBrush(Color.FromRgb(255, 94, 120)) : PhotoUi.Ink;
        Dirty(_actionsHost);
    }

    private void ToggleInfo()
    {
        _infoOpen = !_infoOpen;
        _infoHost.IsVisible = _infoOpen;
        if (_infoOpen) _infoButton.Classes.Add("on"); else _infoButton.Classes.Remove("on");
        if (_infoOpen) LoadInfo();
        Dirty(_actionsHost);
    }

    private void LoadInfo()
    {
        if (_items.Count == 0) return;
        var item = _items[_index];
        int job = ++_infoJob;
        _infoRows.Children.Clear();
        _infoRows.Children.Add(InfoRow("image", item.Title, Path.GetFileName(item.Path)));
        Dirty(_infoHost);
        if (item.Path.Contains("://", StringComparison.Ordinal)) return;
        _ = Task.Run(() => MacImageInfo.Read(item.Path)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (job != _infoJob || t.Result is not { } info) return;
            _infoRows.Children.Clear();
            _infoRows.Children.Add(InfoRow("image", Path.GetFileName(item.Path), MacImageInfo.SizeLine(info)));
            if (MacImageInfo.TakenLine(info) is { } taken) _infoRows.Children.Add(InfoRow("calendar", L10n.T("Scattata il"), taken));
            if (MacImageInfo.CameraLine(info) is { } camera) _infoRows.Children.Add(InfoRow("camera", camera, info.Lens));
            if (MacImageInfo.ExposureLine(info) is { } exposure) _infoRows.Children.Add(InfoRow("aperture", L10n.T("Esposizione"), exposure));
            if (MacImageInfo.PlaceLine(info) is { } place && info.Latitude is { } lat && info.Longitude is { } lon)
            {
                var row = InfoRow("map-pin", place, L10n.T("Apri in Mappe"));
                row.Cursor = new Cursor(StandardCursorType.Hand);
                row.PointerPressed += (_, e) => { e.Handled = true; MacImageInfo.OpenInMaps(lat, lon); };
                _infoRows.Children.Add(row);
            }
            var folder = InfoRow("folder-open", L10n.T("Mostra nel Finder"), Path.GetDirectoryName(item.Path));
            folder.Cursor = new Cursor(StandardCursorType.Hand);
            folder.PointerPressed += (_, e) => { e.Handled = true; MacFileActions.RevealInFinder(item.Path); };
            _infoRows.Children.Add(folder);
            Dirty(_infoHost);
        }));
    }

    private static Control InfoRow(string icon, string title, string? detail)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var t = PhotoUi.Label(title, 13, FontWeight.SemiBold, PhotoUi.Ink); t.TextWrapping = TextWrapping.Wrap; t.MaxLines = 2;
        text.Children.Add(t);
        if (!string.IsNullOrWhiteSpace(detail)) { var d = PhotoUi.Label(detail!, 12, FontWeight.Normal, PhotoUi.Soft); d.TextWrapping = TextWrapping.Wrap; d.MaxLines = 2; text.Children.Add(d); }
        var glyph = new IconView(icon) { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        var row = new DockPanel { Background = Brushes.Transparent };
        var box = new Border { Width = 30, Child = glyph };
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(text);
        return row;
    }

    // ------------------------------------------------------------------ presentazione
    private void ToggleSlideshow()
    {
        if (_slideshow.IsEnabled) { StopSlideshow(); return; }
        _slideshow.Start();
        ((IconView)_playButton.Content!).Name2 = "pause";
        ToolTip.SetTip(_playButton, L10n.T("Ferma la presentazione (Spazio)"));
        Dirty(_toolsHost);
        SetChrome(false);
    }

    private void StopSlideshow()
    {
        _slideshow.Stop();
        ((IconView)_playButton.Content!).Name2 = "play";
        ToolTip.SetTip(_playButton, L10n.T("Presentazione (Spazio)"));
        Dirty(_toolsHost);
    }

    // ------------------------------------------------------------------ azioni
    private string? LocalPath()
    {
        if (_items.Count == 0) return null;
        string path = _items[_index].Path;
        if (path.Contains("://", StringComparison.Ordinal) || !File.Exists(path)) { Toast("Disponibile solo per le foto salvate sul Mac"); return null; }
        return path;
    }

    private void Edit()
    {
        if (LocalPath() is not { } path) return;
        StopSlideshow();
        // L'editor sta sopra: il vetro del visualizzatore (una finestra sopra l'app) lo coprirebbe.
        StopGlass();
        EditRequested?.Invoke(path);
        if (!_editing) StartGlass();
    }

    private void Share()
    {
        if (LocalPath() is not { } path || TopLevel.GetTopLevel(this) is not { } top) return;
        StopSlideshow();
        var origin = _shareButton.TranslatePoint(default, top) ?? default;
        MacFileActions.Share(top, path, new Rect(origin, _shareButton.Bounds.Size));
    }

    /// <summary>Altro: il menu di testo dell'app, allineato a destra sopra il pulsante.</summary>
    private void ShowMore()
    {
        if (LocalPath() is not { } path) return;
        StopSlideshow();
        var openWith = new MenuEntry("Apri con") { Children = new() };
        foreach (var (name, app) in MacFileActions.OpenWithApps(path).Take(12))
            openWith.Children.Add(new MenuEntry(name, null, () => MacFileActions.OpenWith(app, path)));
        var entries = new List<MenuEntry>
        {
            new("Copia", "copy", () => Toast(MacFileActions.CopyImage(path) ? "Foto copiata negli appunti" : "Copia non riuscita")),
            new("Imposta come sfondo della scrivania", "monitor", () => Toast(MacImageInfo.SetDesktopPicture(path) ? "Sfondo della scrivania cambiato" : "Non è stato possibile cambiare lo sfondo")),
            new("Stampa…", "printer", () => MacFileActions.Print(path)),
            new("Mostra nel Finder", "folder", () => MacFileActions.RevealInFinder(path)),
        };
        if (openWith.Children.Count > 0) entries.Insert(1, openWith);
        OpenMenu(entries, _moreButton);
    }

    /// <summary>Conferma prima del Cestino, con lo stesso menu di testo.</summary>
    private void ConfirmDelete()
    {
        if (LocalPath() is not { } path) return;
        StopSlideshow();
        OpenMenu(new List<MenuEntry>
        {
            new("Sposta nel Cestino “" + Path.GetFileName(path) + "”", "trash", () => Delete(path)),
            new("Annulla", null, () => { }),
        }, _deleteButton);
    }

    private void OpenMenu(List<MenuEntry> entries, Control anchor)
    {
        if (anchor.TranslatePoint(new Point(anchor.Bounds.Width, 0), this) is not { } at) return;
        _chrome.MenuOpen = true;
        // Comandi in alto: l'elenco scende sotto la capsula; in basso sale sopra (altezza stimata).
        bool top = at.Y < Bounds.Height / 2;
        MenuRequested?.Invoke(entries, new Point(at.X, top ? at.Y + anchor.Bounds.Height + 14 : Math.Max(16, at.Y - 16 - entries.Count * 52)));
    }

    /// <summary>Il menu di testo si e' chiuso: torna il vetro.</summary>
    public void MenuClosed() { _chrome.MenuOpen = false; DirtyAll(); }

    private void Delete(string path)
    {
        if (!MacFileActions.Trash(path)) { Toast("Non è stato possibile eliminarla"); return; }
        int index = _items.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _items.RemoveAt(index);
        Deleted?.Invoke(path);
        if (_items.Count == 0) { Close(); return; }
        _index = Math.Clamp(_index, 0, _items.Count - 1);
        ShowCurrent();
        Toast("Foto spostata nel Cestino");
    }

    /// <summary>La copia modificata entra subito dopo l'originale e si mostra.</summary>
    private bool _editing;
    /// <summary>L'editor e' aperto sopra (MainWindow lo segnala quando parte).</summary>
    public void EditorOpened() { _editing = true; StopGlass(); }

    public void PhotoEdited(string? saved)
    {
        _editing = false;
        if (IsVisible) { DirtyAll(); StartGlass(); }
        Focus();
        if (saved == null || _items.Count == 0) return;
        if (string.Equals(Path.GetDirectoryName(saved), Path.GetDirectoryName(_items[_index].Path), StringComparison.OrdinalIgnoreCase))
        {
            _items.Insert(_index + 1, new LibraryItem { Path = saved, Title = Path.GetFileNameWithoutExtension(saved), Category = "Photos", ArtPath = saved, SortDateUtc = DateTime.UtcNow });
            _index++;
            ShowCurrent();
        }
        Toast("Copia salvata: " + Path.GetFileName(saved));
    }

    private DispatcherTimer? _toastTimer;
    private void Toast(string text)
    {
        _toast.Text = L10n.T(text);
        _toastHost.IsVisible = true;
        Dirty(_toastHost);
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(2400), DispatcherPriority.Background, (_, _) => { _toastHost.IsVisible = false; _toastTimer!.Stop(); });
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ cornice che si nasconde
    private bool _chromeShown = true;

    private bool PointerOverChrome => _chrome.PointerOver;

    private void SetChrome(bool show)
    {
        if (show)
        {
            _idle.Stop(); _idle.Start();
            if (_chromeShown) return;
            _chromeShown = true;
        }
        else
        {
            // Resta se il mouse e' sopra comandi, miniature o il pannello Info.
            if (PointerOverChrome || _chrome.MenuOpen) { _idle.Stop(); _idle.Start(); return; }
            _idle.Stop();
            if (!_chromeShown) return;
            _chromeShown = false;
        }
        // Niente animazioni sulle capsule: con il vetro le loro opacita' vengono cambiate a ogni ridisegno e le
        // transizioni le lasciavano visibili o trasparenti a meta' (tasti senza colore, vetro sparito).
        _chrome.Target = show ? 1 : 0;
        foreach (var h in _chrome.Hosts)
            if (!ReferenceEquals(h, _infoHost) && !ReferenceEquals(h, _toastHost)) h.IsHitTestVisible = show;
        Cursor = show ? Cursor.Default : new Cursor(StandardCursorType.None);
        if (!show) { _prevHost.Tag = _nextHost.Tag = false; ShowArrows(); }
    }

    // ------------------------------------------------------------------ mouse e tastiera
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        SetChrome(true);
        if (_drag is { } from && _zoom > 1)
        {
            _pan += p - from; _drag = p;
            ApplyTransform();
            return;
        }
        // Le frecce compaiono vicino ai lati.
        bool left = p.X < Bounds.Width * .2, right = p.X > Bounds.Width * .8;
        _prevHost.Tag = left; _nextHost.Tag = right;
        ShowArrows();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _prevHost.Tag = _nextHost.Tag = false;
        ShowArrows();
    }

    private void ShowArrows()
    {
        foreach (var h in new[] { _prevHost, _nextHost })
        {
            bool on = h.IsVisible && h.Tag is true;
            h.Opacity = on ? 1 : 0;
            h.IsHitTestVisible = on;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { if (_zoom > 1) ResetView(); else { _zoom = 2; ApplyTransform(); LoadCurrent(); } e.Handled = true; return; }
        if (_zoom > 1) { _drag = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true; }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag != null) { _drag = null; e.Pointer.Capture(null); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_thumbScroll.IsPointerOver) return;
        Zoom(e.Delta.Y > 0 ? 1.12 : 1 / 1.12);
        e.Handled = true;
    }

    public void HandleKey(KeyEventArgs e)
    {
        SetChrome(true);
        switch (e.Key)
        {
            case Key.Escape when _zoom > 1 || _rotation != 0: ResetView(); break;
            case Key.Escape: case Key.Back: Close(); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
            case Key.Space: ToggleSlideshow(); break;
            case Key.OemPlus: case Key.Add: Zoom(1.25); break;
            case Key.OemMinus: case Key.Subtract: Zoom(1 / 1.25); break;
            case Key.D0: case Key.NumPad0: ResetView(); break;
            case Key.R: Rotate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); break;
            case Key.H: Flip(); break;
            case Key.F: ToggleFavorite(); break;
            case Key.I: ToggleInfo(); break;
            case Key.E: Edit(); break;
            case Key.Delete: ConfirmDelete(); break;
            case Key.Home: GoTo(0); break;
            case Key.End: GoTo(_items.Count - 1); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (!e.Handled) HandleKey(e); }

    // ------------------------------------------------------------------ prove
    internal void QaAction(string what)
    {
        switch (what)
        {
            case "rotate": Rotate(1); break;
            case "flip": Flip(); break;
            case "info": ToggleInfo(); break;
            case "fav": ToggleFavorite(); break;
            case "prev": Step(-1); break;
            default:
                // "hit:x,y": quale controllo prende il clic in quel punto (prova dei comandi resi quasi invisibili).
                if (what.StartsWith("hit:"))
                {
                    var xy = what[4..].Split(',');
                    var at = new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
                    var hit = this.InputHitTest(at) as Visual;
                    var button = hit as Button ?? hit?.FindAncestorOfType<Button>();
                    Console.Error.WriteLine($"[qa] hit {at}: {hit?.GetType().Name ?? "niente"} pulsante={(button != null ? ToolTip.GetTip(button) : "-")}");
                    if (button != null) button.Command?.Execute(null);
                    button?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                }
                break;
            case "zoom": Zoom(2); break;
            case "next": Step(1); break;
            case "edit": Edit(); break;
            case "more": ShowMore(); break;
            case "delete": ConfirmDelete(); break;
            case "hide": SetChrome(false); break;
            case "show": SetChrome(true); break;
        }
    }
}
