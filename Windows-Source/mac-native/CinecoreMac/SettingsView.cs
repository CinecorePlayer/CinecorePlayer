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
/// Pagina Impostazioni, tradotta da HUD/SettingsHudPage*.cs. In alto il titolo, i menu "Player" e "Componenti"
/// (scelta della scheda) e la X. Sotto, la colonna centrata delle righe: sezioni, titolo e descrizione a sinistra,
/// controllo a destra. Le scelte valgono subito e si salvano da sole: non c'e' un tasto "Applica".
/// Misure in punti come i pixel di Windows al 100%. I caratteri Segoe UI diventano Inter, con lo stesso fattore di AppFonts.
/// </summary>
internal sealed partial class SettingsView : Control
{
    public event Action? CloseRequested;
    public event Action<string>? RendererChanged;
    /// <summary>Prova del ricevitore per un formato dentro il PCM (la suona la finestra principale).</summary>
    public event Action<string>? BitstreamTestRequested;
    private string _bitstreamTesting = "";
    public event Action? ThemeChanged;
    /// <summary>Fonte predefinita o server cambiati: la libreria si ricarica.</summary>
    public event Action? LibrarySourceChanged;
    /// <summary>Dimensioni dell'interfaccia cambiate (barra in alto): la libreria si ridisegna.</summary>
    public event Action? InterfaceChanged;
    /// <summary>Comando per il VideoHost (scelte di madVR applicate dal vivo).</summary>
    public event Action<string>? HostCommand;
    public Func<bool>? SystemUsesLightTheme;
    /// <summary>Dimensionamento dell'immagine a schermo intero (le opzioni le tiene il player).</summary>
    public Func<ImageSizingSettings>? ImageSizing;
    public Action? ImageSizingChanged;
    public event Action? KeyDbImportRequested;

    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface SemiBold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    private sealed record Hit(Rect Bounds, string Kind, string Key = "");
    private double _topBarCenter = 42;
    /// <summary>Centro della riga in alto (allineamento dei pulsanti della finestra).</summary>
    internal double TopBarCenter => _topBarCenter;
    internal bool HasHitAt(Point p) => _hits.Exists(h => h.Bounds.Contains(p));
    private readonly List<Hit> _hits = new();
    private Point _mouse = new(-1000, -1000);
    private string _tab = "Generali";
    private readonly SurfaceCrossfade _tabTransition;
    /// <summary>Cambio di tema, accento o sfondo in dissolvenza (come Windows 3.6): la pagina vecchia sfuma sulla nuova.</summary>
    private readonly SurfaceCrossfade _themeFade;
    private double _scroll, _scrollMax, _measured = 1060;
    private Rect _viewport;
    private double _fontScale = 1;

    private static readonly string[] PlayerTabs = { "Generali", "Sottotitoli", "Dispositivi", "Cinecore Audio" };
    private static readonly string[] ComponentTabs = { "MPV", "madVR", "LAV Video", "LAV Audio", "MPC Video Renderer", "MPC Audio Renderer", "Preset" };

    internal Func<List<MainWindow.ScreenChoice>>? ScreenChoices;
    internal Func<string>? StartScreenLabel;
    internal Action<string>? MoveToScreen;

    public SettingsView()
    {
        _tabTransition = new SurfaceCrossfade(this);
        _themeFade = new SurfaceCrossfade(this);
        Focusable = true;
        ClipToBounds = true;
        IsVisible = false;
        _menuTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => StepMenu());
        UiTheme.Changed += InvalidateVisual;
    }

    public bool IsOpen => IsVisible;

    public void Open(string? tab = null)
    {
        if (tab != null) _tab = tab;
        _scroll = 0;
        RefreshBanner();
        IsVisible = true;
        Focus();
        InvalidateVisual();
    }

    public void Close()
    {
        if (!IsVisible) return;
        _menuItems = null;
        CommitPresetName();
        IsVisible = false;
        if (_presetName != null) _presetName.IsVisible = false;
        foreach (var box in _inputs) if (box != null) box.IsVisible = false;
        foreach (var box in new[] { _ampAddress, _ampPort }) if (box != null) box.IsVisible = false;
        CloseRequested?.Invoke();
    }

    // ------------------------------------------------------------------ testo e forme
    // AppFonts: Segoe UI in punti a 96 dpi, servito con Inter ridotto a 0,92.
    private double Px(double pt) => pt * 96 / 72 * 0.92 * _fontScale;

    // Impaginato una volta e riusato nei fotogrammi successivi (UiText).
    private static CachedText Format(string text, double size, Typeface face, Color color, double maxWidth, TextAlignment align = TextAlignment.Left) =>
        UiText.Get(L10n.T(text), face, size, color, maxWidth, 1, align);

    // Descrizioni delle righe: su una riga con "…"; un clic sul testo lo mostra tutto (e lo richiude).
    private readonly HashSet<string> _expandedRows = new(StringComparer.Ordinal);

    /// <summary>Disegna la descrizione di una riga; restituisce l'altezza in piu' quando e' aperta per intero.</summary>
    private double RowDescription(DrawingContext ctx, string title, string description, Rect line)
    {
        bool tooLong = Measure(description, 9, false) > line.Width;
        if (!tooLong) { Text(ctx, description, 9, false, line, UiTheme.Muted); return 0; }
        if (!_expandedRows.Contains(title))
        {
            Text(ctx, description, 9, false, line, UiTheme.Muted);
            _hits.Add(new Hit(line, "expand-desc", title));
            return 0;
        }
        var full = UiText.Get(L10n.T(description), Regular, Px(9), UiTheme.Muted, Math.Max(1, line.Width), int.MaxValue, TextAlignment.Left, TextTrimming.None);
        double top = line.Y + (line.Height - Format(description, Px(9), Regular, UiTheme.Muted, line.Width).Height) / 2;
        ctx.DrawText(full, new Point(line.X, top));
        var area = new Rect(line.X, top, line.Width, full.Height);
        _hits.Add(new Hit(area, "expand-desc", title));
        return Math.Max(0, Math.Ceiling(top + full.Height - (line.Y + line.Height)) + 4);
    }

    /// <summary>TextRenderer con VerticalCenter | EndEllipsis | NoPadding.</summary>
    private void Text(DrawingContext ctx, string text, double pt, bool semibold, Rect r, Color color, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width <= 1) return;
        var ft = Format(text, Px(pt), semibold ? SemiBold : Regular, color, r.Width, align);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private double Measure(string text, double pt, bool semibold) =>
        UiText.Get(L10n.T(text), semibold ? SemiBold : Regular, Px(pt), Colors.White).Width;

    private static IBrush Brush(Color c) => UiText.Brush(c);
    private static Color A(Color c, double alpha) => UiTheme.WithAlpha(c, alpha);

    private static void Chevron(DrawingContext ctx, double cx, double cy, double size, Color color, bool up, double thickness)
    {
        double dy = size / 2 * (up ? -1 : 1);
        var geo = new PolylineGeometry(new[] { new Point(cx - size, cy - dy), new Point(cx, cy + dy), new Point(cx + size, cy - dy) }, false);
        ctx.DrawGeometry(null, new Pen(Brush(color), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
    }

    // ------------------------------------------------------------------ disegno
    // Zone sensibili al mouse (HoverZones): si ridisegna solo quando il mouse entra o esce da una di esse.
    private readonly HoverZones _hoverZones = new();
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    // Dentro il contenuto che scorre il mouse e' spostato di _scroll: le zone si riportano nella pagina, ritagliate.
    private bool _hoverInScroll;
    private Rect _hoverScrollClip;

    /// <summary>Il mouse e' sopra <paramref name="r"/>; la zona si ricorda per sapere quando serve ridisegnare.</summary>
    private bool Hover(Rect r)
    {
        if (!_hoverInScroll) _hoverZones.Track(r);
        else if (_menuItems == null) _hoverZones.Track(r.Translate(new Vector(0, -_scroll)).Intersect(_hoverScrollClip));
        return r.Contains(_mouse);
    }

    public override void Render(DrawingContext ctx)
    {
        _hits.Clear();
        if (!_tabTransition.Capturing && !_themeFade.Capturing) _hoverZones.Begin(_mouse);
        var b = new Rect(Bounds.Size);
        if (b.Width <= 10 || b.Height <= 10) return;
        // Come la libreria nuova: un fondo solo, niente superficie sollevata ne' bordi.
        ctx.FillRectangle(Brush(UiTheme.Panel), b);
        DrawSettingsBanner(ctx, b);
        double s = Math.Clamp(Math.Min(b.Width / 1920, b.Height / 1080), .45, 4);
        double barH = Math.Round(84 * s);
        DrawTopMenus(ctx, new Rect(b.X, b.Y, b.Width, barH), s);
        _fontScale = Math.Clamp(Math.Min(b.Width / 1920, b.Height / 1080), 1, 1.28);
        try { DrawContent(ctx, new Rect(b.X, b.Y + barH, b.Width, Math.Max(1, b.Height - barH))); }
        finally { _fontScale = 1; }
        _tabTransition.Draw(ctx, new Rect(0, barH, b.Width, Math.Max(1, b.Height - barH)));
        _themeFade.Draw(ctx, b);
        if (_themeFade.Capturing) return;
        // La foto della scheda vecchia non contiene il menu a tendina da cui si e' scelta la nuova:
        // prima restava nella cattura e lampeggiava sopra la pagina mentre quello vero si chiudeva.
        if (_tabTransition.Capturing) return;
        DrawMenu(ctx, b);
        _picker?.Render(ctx, b);
        Dispatcher.UIThread.Post(PlaceInputs, DispatcherPriority.Render);
        Dispatcher.UIThread.Post(PlaceAmpInputs, DispatcherPriority.Render);
    }

    private Rect _playerAnchor, _componentAnchor;

    private void DrawTopMenus(DrawingContext ctx, Rect bar, double s)
    {
        double B(double v) => Math.Round(v * s);
        double pad = Math.Max(B(40), bar.Width / 40);
        // Titolo sulla stessa riga dei menu (centrato in altezza come su Windows) e allineato alla colonna del contenuto
        // (stessi calcoli di DrawContent); mai sotto i pulsanti della finestra (semafori di macOS).
        double sc = Math.Clamp(Math.Min(Bounds.Width / 1920, Bounds.Height / 1080), .85, 1.4);
        double columnW = Math.Min(Math.Round(1300 * sc), bar.Width);
        double columnX = bar.X + (bar.Width - columnW) / 2 + Math.Clamp(Math.Floor(columnW / 16), 16, 42);
        double titleX = Math.Max(columnX, bar.X + 92);
        Text(ctx, "Impostazioni", Math.Max(14, 17 * s), true, new Rect(titleX, bar.Y, B(320), bar.Height), UiTheme.Text);

        bool inPlayer = PlayerTabs.Contains(_tab);
        string playerLabel = inPlayer ? "Player: " + TabLabel(_tab) : "Player";
        string compLabel = !inPlayer ? "Componenti: " + TabLabel(_tab) : "Componenti avanzati";
        double pt = Math.Max(10, 11.4 * s), chevron = B(18), gap = B(44);
        double w1 = Measure(playerLabel, pt, true) + chevron, w2 = Measure(compLabel, pt, true) + chevron;
        double x = bar.X + (bar.Width - w1 - gap - w2) / 2;
        foreach (var (label, selected, key, width) in new[] { (playerLabel, inPlayer, "player", w1), (compLabel, !inPlayer, "components", w2) })
        {
            var r = new Rect(x, bar.Y, width, bar.Height);
            var hit = new Rect(r.X - gap / 2, r.Y + B(14), r.Width + gap, Math.Max(1, r.Height - 2 * B(14)));
            bool hover = (_menuItems == null && Hover(hit)) || _menuKey == key;
            Color ink = selected || hover ? UiTheme.Text : A(UiTheme.Text, 140);
            Text(ctx, label, pt, selected, new Rect(r.X, r.Y, width - chevron + 4, r.Height), ink);
            Chevron(ctx, r.Right - chevron / 2 + B(2), r.Y + r.Height / 2 + B(1), B(4.5), ink, _menuKey == key, Math.Max(1.3, B(4.5) / 3.2));
            var anchor = new Rect(r.X, r.Y + r.Height / 2 + B(14), width, 1);
            if (key == "player") _playerAnchor = anchor; else _componentAnchor = anchor;
            _hits.Add(new Hit(hit, "menu", key));
            x += width + gap;
        }

        // A destra solo la X: le modifiche valgono subito.
        double box = B(40);
        var close = new Rect(bar.Right - pad - box + B(8), bar.Y + (bar.Height - box) / 2, box, box);
        _topBarCenter = bar.Y + bar.Height / 2;
        bool hot = _menuItems == null && Hover(close);
        double c = close.X + close.Width / 2, m = close.Y + close.Height / 2, d = Math.Max(5, Math.Floor(close.Width * 21 / 100));
        var pen = new Pen(Brush(hot ? UiTheme.Text : A(UiTheme.Text, 170)), Math.Max(1.6, 2.2 * s), lineCap: PenLineCap.Round);
        ctx.DrawLine(pen, new Point(c - d, m - d), new Point(c + d, m + d));
        ctx.DrawLine(pen, new Point(c + d, m - d), new Point(c - d, m + d));
        _hits.Add(new Hit(close, "close"));
    }

    private void DrawContent(DrawingContext ctx, Rect r)
    {
        double sc = Math.Clamp(Math.Min(Bounds.Width / 1920, Bounds.Height / 1080), .85, 1.4);
        double width = Math.Min(Math.Round(1300 * sc), r.Width);
        r = new Rect(r.X + (r.Width - width) / 2, r.Y, width, r.Height);
        double contentPad = Math.Clamp(Math.Floor(r.Width / 16), 16, 42);
        var body = new Rect(r.X + contentPad, r.Y + 12, r.Width - contentPad * 2, Math.Max(1, r.Height - 28));
        _contentRight = Bounds.Width;
        if (_tab == "Generali") DrawScrollable(ctx, body, DrawGeneralRows);
        else if (_tab == "Dispositivi") DrawScrollable(ctx, body, DrawDevicesRows);
        else if (_tab == "Cinecore Audio") DrawScrollable(ctx, body, DrawAudioRows);
        else if (_tab == "Preset") DrawScrollable(ctx, body, DrawPresetRows);
        else if (ComponentSettings.Tabs.Contains(_tab)) DrawScrollable(ctx, body, DrawNativeRows);
        else DrawPlaceholder(ctx, body);
    }

    private double _contentRight;

    /// <summary>Le schede non ancora portate su Mac: come il riquadro d'attesa delle pagine dei componenti.</summary>
    private void DrawPlaceholder(DrawingContext ctx, Rect body)
    {
        _scrollMax = 0; _viewport = default;
        var mid = new Rect(body.X, body.Y + body.Height * .32, body.Width, 34);
        Text(ctx, TabLabel(_tab), 14.2, true, mid, UiTheme.Text, TextAlignment.Center);
        Text(ctx, "Questa scheda arriva con una delle prossime versioni per Mac.", 10.2, false,
            new Rect(body.X, mid.Bottom + 6, body.Width, 26), UiTheme.Muted, TextAlignment.Center);
    }

    private void DrawScrollable(DrawingContext ctx, Rect viewport, Func<DrawingContext, Rect, double> draw)
    {
        double virtualHeight = Math.Max(viewport.Height, _measured);
        _viewport = viewport;
        _scrollMax = Math.Max(0, virtualHeight - viewport.Height);
        _scroll = Math.Clamp(_scroll, 0, _scrollMax);
        int first = _hits.Count;
        var mouse = _mouse;
        using (ctx.PushClip(viewport))
        using (ctx.PushTransform(Matrix.CreateTranslation(0, -_scroll)))
        {
            _mouse = _menuItems == null && viewport.Contains(mouse) ? new Point(mouse.X, mouse.Y + _scroll) : new Point(-1000, -1000);
            _hoverInScroll = true; _hoverScrollClip = viewport;
            double measured;
            try { measured = draw(ctx, new Rect(viewport.X, viewport.Y, viewport.Width, virtualHeight)); }
            finally { _hoverInScroll = false; _mouse = mouse; }
            if (Math.Abs(measured - _measured) > 0.5) { _measured = measured; Dispatcher.UIThread.Post(InvalidateVisual); }
        }
        // I riquadri dei clic tornano nelle coordinate della pagina, solo la parte visibile.
        for (int i = _hits.Count - 1; i >= first; i--)
        {
            var moved = _hits[i].Bounds.Translate(new Vector(0, -_scroll));
            var visible = moved.Intersect(viewport);
            if (visible.Width <= 0 || visible.Height <= 0) _hits.RemoveAt(i);
            else _hits[i] = _hits[i] with { Bounds = visible };
        }
        DrawScrollbar(ctx, viewport);
    }

    private double _scrollbarLast = double.NaN;
    private long _scrollbarAt = -10000;

    private void DrawScrollbar(DrawingContext ctx, Rect viewport)
    {
        if (_scrollMax <= 0) return;
        // Accanto alla colonna del contenuto. ScrollbarChrome: proporzionale, fra 34 px e il 40% della pista.
        double trackX = Math.Min(viewport.Right + 28, _contentRight - 16);
        var track = new Rect(trackX, viewport.Y + 8, 3, Math.Max(1, viewport.Height - 16));
        double content = viewport.Height + _scrollMax;
        double min = Math.Min(track.Height, Math.Max(34, Math.Floor(track.Height / 14)));
        double thumbH = Math.Clamp(Math.Round(track.Height * viewport.Height / content), min, Math.Max(min, Math.Round(track.Height * .4)));
        double thumbY = track.Y + Math.Round((track.Height - thumbH) * (_scroll / _scrollMax));
        double w = Math.Min(4, track.Width), x = track.X + track.Width / 2;
        // Come le barre del Mac: la linea compare solo mentre si scorre (o col puntatore sopra) e poi sfuma.
        // Prima restava sempre a schermo e sembrava "una riga che segue la pagina a caso".
        long now = Environment.TickCount64;
        if (double.IsNaN(_scrollbarLast)) _scrollbarLast = _scroll;
        else if (Math.Abs(_scroll - _scrollbarLast) > 0.5) { _scrollbarLast = _scroll; _scrollbarAt = now; }
        var hitArea = new Rect(track.X - 8, track.Y, track.Width + 16, track.Height);
        if (Hover(hitArea) || _draggingScrollbar) _scrollbarAt = now;
        double idle = now - _scrollbarAt, show = idle < 900 ? 1 : Math.Clamp(1 - (idle - 900) / 300, 0, 1);
        if (show > 0)
        {
            ctx.DrawLine(new Pen(Brush(A(UiTheme.Text, 90 * show)), w, lineCap: PenLineCap.Round), new Point(x, thumbY + w / 2), new Point(x, thumbY + Math.Max(w / 2, thumbH - w / 2)));
            DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(idle < 900 ? 920 - idle : 33));
        }
        _hits.Add(new Hit(hitArea, "scrollbar"));
    }

    // ------------------------------------------------------------------ scheda Generali
    private static readonly Color[] AccentColors =
    {
        // [2026-11-23] Il primo e' l'accento di serie dell'app (prima l'azzurro della vecchia versione Windows).
        UiTheme.DefaultAccent, Color.FromRgb(81, 88, 201), Color.FromRgb(113, 89, 153),
        Color.FromRgb(183, 53, 111), Color.FromRgb(200, 103, 60), Color.FromRgb(24, 150, 149),
    };
    private static readonly Color[] BackgroundColors =
    {
        UiTheme.DefaultPanel, Color.FromRgb(11, 17, 32), Color.FromRgb(18, 14, 12), Color.FromRgb(22, 24, 28),
        Color.FromRgb(226, 230, 235), Color.FromRgb(246, 247, 249),
    };

    private double DrawGeneralRows(DrawingContext ctx, Rect body)
    {
        double width = body.Width;
        double controlW = Math.Min(330, Math.Max(210, Math.Floor(width * 2 / 5)));
        double controlX = body.X + width - controlW;
        double y = body.Y;
        double labelW = Math.Max(90, controlX - body.X - 28);

        void Section(string title)
        {
            _rec?.Section(title);
            ctx.DrawLine(new Pen(Brush(A(UiTheme.Border, 50)), 1), new Point(body.X, y + .5), new Point(body.X + width, y + .5));
            Text(ctx, title.ToUpperInvariant(), 9, true, new Rect(body.X, y + 20, width, 24), UiTheme.Muted);
            y += 56;
        }
        Rect Row(string title, string description = "")
        {
            _rec?.Row(title, description);
            double h = description.Length > 0 ? 66 : 56;
            Text(ctx, title, 10.5, false, new Rect(body.X, y, labelW, 30), UiTheme.Text);
            if (description.Length > 0) h += RowDescription(ctx, title, description, new Rect(body.X, y + 29, labelW, 23));
            var r = new Rect(controlX, y + 1, controlW, 36);
            y += h;
            return r;
        }
        void Choice(Rect r, string text, string key) { DrawValueChip(ctx, r, text, Hover(r)); _hits.Add(new Hit(r, "choice", key)); _rec?.Choice("choice", key, text); }
        void Toggle(Rect r, bool value, string key, string on = "Attivo", string off = "Disattivato")
        {
            DrawSwitch(ctx, r, value ? on : off, value);
            _hits.Add(new Hit(r, "toggle", key));
            _rec?.Toggle("toggle", key, value);
        }

        Section("Avvio");
#if CINECORE_WINDOWS
        Toggle(Row("Apri Cinecore all’avvio di Windows"), LoginItem.Enabled, "openAtLogin");
#else
        Toggle(Row("Apri Cinecore all’avvio del Mac", "Come in Impostazioni di Sistema › Elementi login."), LoginItem.Enabled, "openAtLogin");
#endif
        Toggle(Row("Avvia a schermo intero", "Il player si apre senza finestra, sullo schermo scelto qui sotto."), MacSettings.StartFullscreen, "startFullscreen");
        if (ScreenChoices != null)
        {
            Choice(Row("Schermo all’avvio", "Sempre lo stesso schermo, quello dove c’è il mouse o l’ultima posizione."), StartScreenLabel?.Invoke() ?? "Ultima posizione", "start-screen");
            var screens = ScreenChoices();
            if (screens.Count > 1)
                Choice(Row("Sposta il player", "Adesso, su un altro schermo (anche a schermo intero)."), screens.FirstOrDefault(c => c.Current)?.Label ?? "Scegli", "move-screen");
        }

        Section("Spotlight");
        Toggle(Row("Spotlight all’avvio", "All’apertura la sala a tutto schermo invece della libreria (Altro › Spotlight)."), MacSettings.Get("spotlightHome", false), "spotlightHome");
        string spotlightSource = MacSettings.Get("spotlightSource", MacSettings.Get("librarySource", "Library"));
        Choice(Row("Fonte Spotlight", PlatformText.AudioSystem("Scegli libreria del Mac o server di rete.", "Scegli libreria locale o server di rete.")), spotlightSource == "DLNA" ? "Rete" : "Libreria", "spotlight-source");
        if (spotlightSource == "DLNA")
            Choice(Row("Server di rete", "Seleziona Jellyfin, Emby o un server DLNA."),
                NetworkStore.Servers.FirstOrDefault(s => s.Key == MacSettings.Get("spotlightServer", ""))?.Name
                ?? (MacSettings.Get("spotlightServer", "").Length == 0 ? "Scegli un server" : "Server non trovato"), "spotlight-server");
        y += 12;

        Section("Libreria");
        bool libraryNetwork = MacSettings.Get("librarySource", "Library") == "DLNA";
        Choice(Row("Fonte predefinita", "La libreria si apre sul Mac o su un server di rete."), libraryNetwork ? "Server di rete" : "Mac", "library-source");
        if (libraryNetwork)
            Choice(Row("Server", "Jellyfin, Emby o DLNA: si collega da solo all’avvio."),
                NetworkStore.Servers.FirstOrDefault(s => s.Key == MacSettings.Get("libraryServer", ""))?.Name ?? "Scegli un server", "library-server");
        y += 12;

        Section("Riproduzione");
        string renderer = MacSettings.Renderer;
        Toggle(Row("Uscita audio", "Audio originale al dispositivo collegato, con tutti i motori. Tiene l’uscita in esclusiva durante il film."), MacSettings.Bitstream, "bitstream", "Bitstream / passthrough", "PCM");
        // Uscita HDMI che dichiara solo PCM (adattatori USB-C/HDMI che riscrivono l'EDID del ricevitore): il bitstream
        // passa dentro il PCM, formato per formato, dopo la prova (il ricevitore deve riconoscerlo da solo).
#if !CINECORE_WINDOWS
        // Solo Mac: su Windows il driver HDMI dichiara da se' i formati compressi (LAV Audio + MPC Audio Renderer).
        string bitstreamUid = MacAudioDevices.ConfiguredOutputUid;
        if (MacSettings.Bitstream && MacAudioDevices.LpcmOnlyHdmi(bitstreamUid))
        {
            var carrier = MacAudioDevices.CarrierEnabled(bitstreamUid);
            var note = Format("Questa uscita HDMI dichiara solo PCM: il bitstream passa dentro il PCM. Prova a volume basso e attiva i formati che il ricevitore mostra.",
                Px(9), Regular, UiTheme.Muted, width, TextAlignment.Left);
            ctx.DrawText(note, new Point(body.X, y));
            y += Math.Ceiling(note.Height) + 16;
            foreach (var (codec, name, hint) in MacAudioDevices.CarrierFormats)
            {
                var r = Row(name, MacBitstreamTest.Testable(codec) ? hint : hint + " · si prova con un film");
                bool on = carrier.Contains(codec);
                if (MacBitstreamTest.Testable(codec))
                {
                    var test = new Rect(r.X, r.Y + Math.Floor((r.Height - 30) / 2), 96, 30);
                    bool busy = MacBitstreamTest.Running && _bitstreamTesting == codec;
                    DrawActionButton(ctx, test, busy ? "In prova…" : "Prova");
                    _hits.Add(new Hit(test, "bitstream-test", codec));
                    r = new Rect(r.X + 94, r.Y, r.Width - 94, r.Height);
                }
                DrawSwitch(ctx, r, on ? "Bitstream" : "PCM", on);
                _hits.Add(new Hit(r, "bitstream-carrier", codec));
            }
        }
#endif
        Toggle(Row("DVD", "Come un lettore da tavolo: avvertenze, menu del disco e contenuti speciali. Altrimenti si va dritti al film."), MacDvdNav.Enabled, "dvdMenus", "Con i menu", "Dritto al film");
        Toggle(Row("Frequenza dello schermo", "Se lo schermo non è a un multiplo dei fotogrammi del film (24 fps a 60 Hz) passa alla frequenza adatta, e torna com’era alla chiusura."), MacDisplayRate.Enabled, "matchRefresh", "Adatta al film", "Non cambiare");
        Choice(Row("Motore video"), renderer.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "Automatico" : renderer == "MPCVR" ? "MPCVR" : renderer == "MPV" ? "MPV" : "MADVR", "renderer");
#if CINECORE_WINDOWS
        // Come il vecchio player: libmpv standard o la build x86-64-v3 (AVX2), piu' veloce ma sperimentale.
        if (MpvEngine.HasV3Runtime)
            Choice(Row("Versione MPV", "Vale dal prossimo avvio del player."), MpvEngine.UseV3Runtime ? "V3 sperimentale" : "Standard", "mpv-runtime");
#endif
        if (VideoHostClient.WineAvailable)
            Choice(Row("madVR in risparmio energetico", "In risparmio energetico la GPU rallenta e madVR, con le impostazioni più pesanti, può andare a scatti. Durante il film può chiederti se passare a MPV o MPC-VR, oppure usare un profilo leggero."),
                MacSettings.MadVrLowPower switch { "light" => "Profilo leggero", "asis" => "Non chiedere", _ => "Chiedi" }, "madvr-lowpower");
        // Dischi, come Windows: lettura dei DVD e Blu-ray protetti (chiavi dell'utente, mai scaricate dal player).
#if !CINECORE_WINDOWS
        Choice(Row("Lettura dei DVD", "Dritto al film, senza menu, con tutti i renderer. I menu del disco non sono ancora disponibili sul Mac."), "Diretta", "dvd-playback");
#endif
        Choice(Row("Blu-ray protetti", MacDisc.ProtectionStatus()), "KEYDB.cfg…", "disc-protection");
        Choice(Row("Qualità da Jellyfin ed Emby", "Con un limite il server converte il video al volo."), TranscodeLabel(JellyfinBitrate), "jellyfin-quality");
        y += 12;

        if (ImageSizing?.Invoke() is { } sizing)
        {
            Section("Immagine a schermo intero");
            Choice(Row("Dimensionamento", "Tasto Z durante il film. Riempi è il comportamento normale."), ImageSizingSettings.ModeName(sizing.Mode), "sizing-mode");
            if (sizing.Mode != ImageSizingMode.Fill)
            {
                var detect = Row("Bande nere nel fotogramma", "Misura il formato reale anche quando cambia a metà film.");
                DrawSwitch(ctx, detect, sizing.DetectBars ? "Attivo" : "Disattivato", sizing.DetectBars);
                _hits.Add(new Hit(detect, "choice", "sizing-detect"));
                _rec?.Toggle("choice", "sizing-detect", sizing.DetectBars);
                var expand = Row("Scene IMAX a tutto schermo", "Quando l’immagine si allarga in altezza rispetto al resto del film.");
                DrawSwitch(ctx, expand, sizing.ExpandTallScenes ? "Attivo" : "Disattivato", sizing.ExpandTallScenes);
                _hits.Add(new Hit(expand, "choice", "sizing-expand"));
                _rec?.Toggle("choice", "sizing-expand", sizing.ExpandTallScenes);
                Choice(Row("Transizione al cambio di formato"), sizing.TransitionMs <= 0 ? "Nessuna" : sizing.TransitionMs + " ms", "sizing-transition");
            }
            if (sizing.Mode == ImageSizingMode.Custom)
            {
                Choice(Row("Altezza costante ↔ area costante", "0% tutti i formati alla stessa altezza, 100% alla stessa area."), (int)Math.Round(sizing.Bias * 100) + "%", "sizing-bias");
                foreach (var preset in ImageSizingSettings.Presets)
                {
                    double value = sizing.Multiplier(preset.Key);
                    string name = preset.Key switch { "1.43" => "1,43 IMAX", "1.78" => "1,78 (16:9)", "1.33" => "1,33 (4:3)", "2.39" => "2,35 / 2,40", _ => preset.Key.Replace('.', ',') };
                    Choice(Row("Altezza del " + name), value <= 0 ? "Tutta l’area" : value.ToString("0.00", MacTools.Italian) + "×", "sizing-preset-" + preset.Key);
                }
            }
        }
        Section("Aspetto");
        string mode = MacSettings.ThemeMode;
        Choice(Row("Tema"), mode switch { "light" => "Chiaro", "dark" => "Scuro", _ => PlatformText.AudioSystem("Come macOS", "Come Windows") }, "theme");
        var palette = Row("Colore accento");
        Swatches(ctx, palette, AccentColors, c => c == UiTheme.Accent, "accent");
        var background = Row("Colore sfondo", "Superfici e barra laterale si adeguano da sole.");
        Swatches(ctx, background, BackgroundColors, c => c == UiTheme.Panel, "bg");
        y += 12;

        Section("Interfaccia");
        Choice(Row("Lingua"), L10n.Languages.First(l => l.Code == L10n.Language).Name, "language");
        Choice(Row("Barra in alto", "Grandezza di Home, Film, Serie TV… «Automatica» la ingrandisce sugli schermi piccoli."),
            MacSettings.Get("headerSize", "auto") switch { "normal" => "Normale", "large" => "Grande", "xlarge" => "Molto grande", _ => "Automatica" }, "header-size");
        string sub = MacSettings.SubtitleLanguage is { Length: > 0 } saved ? saved : "it";
        Choice(Row("Lingua dei sottotitoli", "Quella proposta per prima quando cerchi sottotitoli."), LanguageLabel(sub), "subtitle-language");
        y += 12;

        Section("Aggiornamenti");
        // Come Windows: la versione e un'azione vera per controllare (prima c'era la scritta "Anteprima").
#if CINECORE_WINDOWS
        Toggle(Row("Aggiornamenti automatici", MacUpdate.InstalledBundle != null ? "All’avvio cerca una nuova versione e la propone." : "Attivi solo nella copia installata con il setup."),
            MacUpdate.AutoCheck, "updateAutoCheck", "Attivi", "Disattivati");
#endif
        var version = Row(OperatingSystem.IsWindows() ? "Cinecore Player 2026" : "Cinecore Player per Mac", "Versione installata: " + MacUpdate.DisplayName);
        DeviceButton(ctx, version, "Controlla aggiornamenti", "update-app");
        var parts = Row("Componenti di terze parti", PlatformText.AudioSystem("FFmpeg, mpv, yt-dlp, madVR e MPC Video Renderer: versione installata e ultima pubblicata.",
            "yt-dlp, madVR, MPC Video Renderer, LAV Filters, mpv e FFmpeg: versioni e aggiornamenti."));
        DeviceButton(ctx, parts, "Controlla", "update-components");
#if !CINECORE_WINDOWS
        // Motore Windows (madVR, MPC-VR, LAV) scaricato a richiesta, come i componenti dell'installer di Windows.
        var engine = Row("Motore madVR e MPC Video Renderer", MacEngine.Status);
        if (MacEngine.Busy) DeviceButton(ctx, engine, "Annulla", "engine-cancel");
        else if (MacEngine.CanInstall) DeviceButton(ctx, engine, MacEngine.Installed ? "Aggiorna" : "Scarica", "engine-install");
#endif
        y += 12;

        Section("Metadati");
        bool customTmdb = CinecorePlayer2025.Utilities.MovieMetadataService.HasCustomTmdbApiKey;
        y += DrawCredentialCard(ctx, new Rect(body.X, y, width, 0), "tmdb", "TMDb",
            "Informazioni, locandine e cast di film e serie.", customTmdb ? "Configurato" : "Non configurato", customTmdb,
            new[] { new CredentialField("Chiave API (v3)", "Gratuita: themoviedb.org › Impostazioni › API. Senza, niente locandine né trame.", true) },
            customTmdb, controlX, controlW);
        y += 14;
        bool spotifyReady = CinecorePlayer2025.Utilities.SpotifyArtistArtworkService.IsConfigured;
        y += DrawCredentialCard(ctx, new Rect(body.X, y, width, 0), "spotify", "Spotify",
            "Foto degli artisti e sfondi degli album.", spotifyReady ? "Configurato" : "Non configurato", spotifyReady,
            new[]
            {
                new CredentialField("Client ID", "Da developer.spotify.com › Dashboard.", false),
                new CredentialField("Client Secret", spotifyReady ? "Vuoto: conserva il segreto salvato." : "Richiesto al primo salvataggio.", true),
            }, spotifyReady, controlX, controlW);
        y += 14;
        bool tidalReady = CinecorePlayer2025.Utilities.TidalArtistArtworkService.IsConfigured;
        y += DrawCredentialCard(ctx, new Rect(body.X, y, width, 0), "tidal", "TIDAL",
            "Foto degli artisti dal catalogo TIDAL (solo metadati, niente streaming).", tidalReady ? "Configurato" : "Non configurato", tidalReady,
            new[]
            {
                new CredentialField("Client ID", "Da developer.tidal.com › Dashboard.", false),
                new CredentialField("Client Secret", tidalReady ? "Vuoto: conserva il segreto salvato." : "Richiesto al primo salvataggio.", true),
            }, tidalReady, controlX, controlW);
        y += 14;
        string preferred = CinecorePlayer2025.Utilities.MusicArtistArtworkService.PreferredSource;
        Choice(Row("Fonte per la musica", "La prima a essere interrogata; le altre fanno da riserva."),
            preferred switch { "spotify" => "Spotify", "tidal" => "TIDAL", "deezer" => "Deezer", _ => "Automatica" }, "music-source");

        if (CinecorePlayer2025.Utilities.Keychain.AccessRequired)
        {
            Section("Accessi salvati");
            Choice(Row("Portachiavi", "Le credenziali sono salvate. Autorizza l’accesso solo quando vuoi usarle."),
                CinecorePlayer2025.Utilities.Keychain.Authorizing ? "In attesa…" : "Autorizza accesso", "keychain-authorize");
        }

        return y - body.Y + 40;
    }

    // ------------------------------------------------------------------ credenziali (SettingsHudPage.Credentials.cs)
    // Schede delle credenziali (TMDb, Spotify, TIDAL): una aperta alla volta, con uno o due campi veri sopra la pagina.
    private string? _credKey;
    private readonly Dictionary<string, (string Text, bool Error)> _credMessages = new();
    private readonly TextBox?[] _inputs = new TextBox?[2];
    private readonly Rect[] _inputRects = new Rect[2];
    private int _inputCount;
    private int _tidalVerification;
    private (string Text, bool Error)? _savedTidalStatus;
    /// <summary>Livello sopra la pagina per le caselle di testo vere (le mette la finestra).</summary>
    public Canvas? InputLayer { get; set; }

    private static readonly Color Green = Color.FromRgb(72, 199, 142);
    private static readonly Color Danger = Color.FromRgb(255, 76, 92);

    private sealed record CredentialField(string Label, string Hint, bool Secret);

    /// <summary>Un servizio come riga semplice: nome e descrizione a sinistra, stato e Configura/Chiudi a destra; aperto, i campi sottolineati, Salva e Rimuovi.</summary>
    private double DrawCredentialCard(DrawingContext ctx, Rect area, string key, string title, string description, string status, bool active,
        CredentialField[] fields, bool canRemove, double controlX, double controlW)
    {
        const double headerH = 66;
        // Telefono: solo lo stato (le chiavi si scrivono sul PC).
        if (_rec != null) { _rec.Row(title, description); _rec.Info(status); return headerH; }
        bool expanded = _credKey == key;
        double top = area.Y, textW = Math.Max(90, controlX - area.X - 28);
        Text(ctx, title, 10.5, false, new Rect(area.X, top, textW, 30), UiTheme.Text);
        Text(ctx, description, 9, false, new Rect(area.X, top + 29, textW, 23), UiTheme.Muted);
        var action = new Rect(area.Right - 104, top + 1, 104, 36);
        DrawTextAction(ctx, action, expanded ? "Chiudi" : "Configura");
        _hits.Add(new Hit(action, "credential-toggle", key));
        Color statusColor = active ? Green : UiTheme.Muted;
        double statusW = Measure(status, 9, false);
        var statusRect = new Rect(action.X - 18 - statusW, top + 1, statusW + 2, 36);
        ctx.DrawEllipse(Brush(statusColor), null, new Point(statusRect.X - 14 + 3.5, statusRect.Y + statusRect.Height / 2), 3.5, 3.5);
        Text(ctx, status, 9, false, statusRect, active ? statusColor : UiTheme.SubtleText);
        if (key == "tidal" && _savedTidalStatus is { } verification)
            Text(ctx, verification.Text, 9, false, new Rect(statusRect.Left - 14, top + 34, area.Right - statusRect.Left + 14, 23),
                verification.Error ? Danger : UiTheme.Muted, TextAlignment.Right);
        if (!expanded) return headerH;

        double y = top + headerH;
        _inputCount = fields.Length;
        for (int i = 0; i < fields.Length; i++)
        {
            Text(ctx, fields[i].Label.ToUpperInvariant(), 8.4, true, new Rect(area.X, y, area.Width, 18), UiTheme.SubtleText);
            var input = new Rect(area.X, y + 20, area.Width, 36);
            bool focused = _inputs[i]?.IsFocused == true;
            ctx.DrawLine(new Pen(Brush(focused ? UiTheme.Accent : A(UiTheme.Border, 110)), focused ? 2 : 1), new Point(input.X, input.Bottom - 1), new Point(input.Right, input.Bottom - 1));
            _inputRects[i] = input;
            Text(ctx, fields[i].Hint, 9, false, new Rect(area.X, y + 60, area.Width, 20), UiTheme.Muted);
            y += 88;
        }

        var save = new Rect(area.Right - 112, y + 2, 112, 36);
        DrawActionButton(ctx, save, "Salva");
        _hits.Add(new Hit(save, "credential-save", key));
        double messageRight = save.X - 16;
        if (canRemove)
        {
            double w = Measure("Rimuovi", 9.6, true) + 24;
            var remove = new Rect(save.X - 12 - w, save.Y, w, 36);
            Text(ctx, "Rimuovi", 9.6, true, remove, Hover(remove) ? Danger : UiTheme.SubtleText, TextAlignment.Center);
            _hits.Add(new Hit(remove, "credential-remove", key));
            messageRight = remove.X - 12;
        }
        if (_credMessages.TryGetValue(key, out var message) && message.Text.Length > 0)
            Text(ctx, message.Text, 9, false, new Rect(area.X, save.Y, Math.Max(1, messageRight - area.X), 36), message.Error ? Danger : Green);
        return save.Bottom + 22 - top;
    }

    private void DrawTextAction(DrawingContext ctx, Rect r, string label)
        => Text(ctx, label, 9.8, true, r, A(UiTheme.Text, Hover(r) ? 255 : 215), TextAlignment.Center);

    /// <summary>DrawActionButton primario: pillola piena chiara (nel tema scuro) con il testo scuro.</summary>
    private void DrawActionButton(DrawingContext ctx, Rect r, string text)
    {
        bool hover = Hover(r);
        Color fill = UiTheme.IsLight ? Color.FromRgb(24, 26, 30) : Color.FromRgb(245, 246, 248);
        ctx.DrawRectangle(Brush(hover ? A(fill, 225) : fill), null, r, r.Height / 2, r.Height / 2);
        Text(ctx, text, 9.8, true, r, UiTheme.IsLight ? Colors.White : Colors.Black, TextAlignment.Center);
    }

    private void ToggleCredentials(string key)
    {
        _credMessages.Remove(key);
        if (_credKey == key) { CollapseCredentials(); return; }
        _credKey = key;
        _inputCount = 0;
        foreach (var box in _inputs) if (box != null) box.IsVisible = false;
        EnsureInputs();
        string[] values = key switch
        {
            "spotify" => new[] { CinecorePlayer2025.Utilities.SpotifyArtistArtworkService.ConfiguredClientId ?? "", "" },
            "tidal" => new[] { CinecorePlayer2025.Utilities.TidalArtistArtworkService.ConfiguredClientId ?? "", "" },
            _ => new[] { CinecorePlayer2025.Utilities.MovieMetadataService.GetUserTmdbApiKey() ?? "" },
        };
        for (int i = 0; i < _inputs.Length; i++)
        {
            if (_inputs[i] is not { } box) continue;
            box.Text = i < values.Length ? values[i] : "";
            // Il Client ID si legge; chiavi e segreti restano nascosti.
            box.PasswordChar = key != "tmdb" && i == 0 ? '\0' : '•';
        }
        Dispatcher.UIThread.Post(() => _inputs[0]?.Focus(), DispatcherPriority.Background);
        InvalidateVisual();
    }

    private void CollapseCredentials()
    {
        _credKey = null;
        _inputCount = 0;
        foreach (var box in _inputs)
        {
            if (box == null) continue;
            box.Text = "";
            box.IsVisible = false;
        }
        Focus();
        InvalidateVisual();
    }

    private void EnsureInputs()
    {
        if (InputLayer == null) return;
        for (int i = 0; i < _inputs.Length; i++)
        {
            if (_inputs[i] != null) continue;
            var box = new TextBox
            {
                PasswordChar = '•', Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontFamily = Inter, FontSize = Px(10.5),
                Padding = new Thickness(0), MinHeight = 0, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = false,
            };
            // Senza riquadro: niente fondi e bordi del tema Fluent al passaggio o con il focus.
            foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                         "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
                box.Resources[k] = Brushes.Transparent;
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _credKey != null) { SaveCredentials(_credKey); e.Handled = true; }
                else if (e.Key == Key.Escape && _credKey != null) { ToggleCredentials(_credKey); e.Handled = true; }
            };
            box.GotFocus += (_, _) => InvalidateVisual();
            box.LostFocus += (_, _) => InvalidateVisual();
            InputLayer.Children.Add(box);
            _inputs[i] = box;
        }
    }

    /// <summary>Le caselle seguono il loro posto nella pagina (che scorre): fuori dalla vista si nascondono.</summary>
    private void PlaceInputs()
    {
        PlacePresetName();
        for (int i = 0; i < _inputs.Length; i++)
        {
            if (_inputs[i] is not { } box) continue;
            var r = _inputRects[i];
            bool show = IsVisible && _tab == "Generali" && _credKey != null && i < _inputCount && r.Width > 0 && _picker == null && _menuItems == null;
            if (show)
            {
                var onPage = r.Translate(new Vector(0, -_scroll));
                show = onPage.Y >= _viewport.Y && onPage.Bottom <= _viewport.Bottom;
                if (show)
                {
                    double h = Px(10.5) * 1.6;
                    Canvas.SetLeft(box, onPage.X);
                    Canvas.SetTop(box, onPage.Bottom - h - 6);
                    box.Width = onPage.Width;
                    box.Height = h;
                    box.Foreground = Brush(UiTheme.Text);
                    box.CaretBrush = Brush(UiTheme.Text);
                }
            }
            if (box.IsVisible != show) box.IsVisible = show;
            // Il campo sta sopra la pagina: segue la dissolvenza della scheda invece di comparire sopra quella vecchia.
            box.Opacity = _tabTransition.Reveal;
        }
    }

    private void SetMessage(string key, string text, bool error) { _credMessages[key] = (text, error); InvalidateVisual(); }

    private void SaveCredentials(string key)
    {
        if (_credKey != key) return;
        string first = _inputs[0]?.Text?.Trim() ?? "", second = _inputs[1]?.Text?.Trim() ?? "";
        try
        {
            if (key is "spotify" or "tidal")
            {
                if (first.Length == 0) { SetMessage(key, "Inserisci il Client ID.", true); return; }
                if (key == "tidal")
                {
                    CinecorePlayer2025.Utilities.TidalArtistArtworkService.Configure(first, second);
                    _savedTidalStatus = ("Verifica in corso…", false);
                    _ = VerifyTidalAsync(++_tidalVerification);
                }
                else
                {
                    CinecorePlayer2025.Utilities.SpotifyArtistArtworkService.Configure(first, second);
                }
            }
            else
            {
                CinecorePlayer2025.Utilities.MovieMetadataService.SetUserTmdbApiKey(first);
            }
        }
        catch (ArgumentException) { SetMessage(key, "Inserisci anche il Client Secret.", true); return; }
        catch { SetMessage(key, "Salvataggio non riuscito.", true); return; }
        _credMessages.Remove(key);
        CollapseCredentials();
        CredentialsSaved?.Invoke();
    }

    private async System.Threading.Tasks.Task VerifyTidalAsync(int verification)
    {
        bool accepted = await CinecorePlayer2025.Utilities.TidalArtistArtworkService.TestAsync();
        if (verification != _tidalVerification) return;
        _savedTidalStatus = accepted ? ("Credenziali accettate.", false) : ("Verifica TIDAL non riuscita.", true);
        InvalidateVisual();
    }

    private void RemoveCredentials(string key)
    {
        try
        {
            if (key == "tidal")
            {
                CinecorePlayer2025.Utilities.TidalArtistArtworkService.Configure(null, null);
                _tidalVerification++;
                _savedTidalStatus = null;
            }
            else if (key == "spotify") CinecorePlayer2025.Utilities.SpotifyArtistArtworkService.Configure(null, null);
            else CinecorePlayer2025.Utilities.MovieMetadataService.SetUserTmdbApiKey(null);
            foreach (var box in _inputs) if (box != null) box.Text = "";
            SetMessage(key, key == "tmdb" ? "Chiave rimossa." : "Credenziali rimosse.", false);
            CredentialsSaved?.Invoke();
        }
        catch { SetMessage(key, "Operazione non riuscita.", true); }
    }

    public event Action? CredentialsSaved;
    public event Action? KeychainAuthorizationRequested;

    /// <summary>I pallini pronti e, per ultimo, quello per un colore qualsiasi (anello di colori con il "+").</summary>
    private void Swatches(DrawingContext ctx, Rect row, Color[] colors, Func<Color, bool> chosen, string kind)
    {
        _rec?.Colors(kind, colors.Select(c => (c.ToString(), chosen(c))));
        int slots = colors.Length + 1;
        double left = row.X + Math.Floor((row.Width - (slots * 22 + (slots - 1) * 20)) / 2);
        bool preset = false;
        for (int i = 0; i < colors.Length; i++)
        {
            var sw = new Rect(left + i * 42, row.Y + 6, 22, 22);
            var center = sw.Center;
            ctx.DrawEllipse(Brush(colors[i]), new Pen(Brush(A(UiTheme.Text, 70)), 1), center, 11, 11);
            if (chosen(colors[i])) { preset = true; ctx.DrawEllipse(null, new Pen(Brush(UiTheme.Text), 1.5), center, 14, 14); }
            _hits.Add(new Hit(sw.Inflate(new Thickness(4, 5)), kind, colors[i].ToString()));
        }
        var custom = new Rect(left + colors.Length * 42, row.Y + 6, 22, 22);
        DrawCustomColorDot(ctx, custom, !preset);
        _hits.Add(new Hit(custom.Inflate(new Thickness(4, 5)), kind + "-custom"));
    }

    /// <summary>DrawCustomColorDot: sei spicchi di tinte, il foro con il "+", l'anello se il colore attuale non e' fra i pronti.</summary>
    private static void DrawCustomColorDot(DrawingContext ctx, Rect r, bool chosen)
    {
        Color[] hues = { Color.FromRgb(255, 72, 72), Color.FromRgb(255, 196, 40), Color.FromRgb(70, 210, 90), Color.FromRgb(40, 170, 255), Color.FromRgb(150, 90, 255), Color.FromRgb(255, 72, 160) };
        var c = r.Center; double rad = r.Width / 2;
        for (int i = 0; i < hues.Length; i++)
        {
            double a0 = (-90 + i * 60) * Math.PI / 180, a1 = (-90 + i * 60 + 60.5) * Math.PI / 180;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(c, true);
                g.LineTo(new Point(c.X + Math.Cos(a0) * rad, c.Y + Math.Sin(a0) * rad));
                g.ArcTo(new Point(c.X + Math.Cos(a1) * rad, c.Y + Math.Sin(a1) * rad), new Size(rad, rad), 0, false, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Brush(hues[i]), null, geo);
        }
        ctx.DrawEllipse(Brush(UiTheme.Card), null, c, rad - 5, rad - 5);
        var plus = new Pen(Brush(UiTheme.Text), 1.5, lineCap: PenLineCap.Round);
        ctx.DrawLine(plus, new Point(c.X - 3.5, c.Y), new Point(c.X + 3.5, c.Y));
        ctx.DrawLine(plus, new Point(c.X, c.Y - 3.5), new Point(c.X, c.Y + 3.5));
        if (chosen) ctx.DrawEllipse(null, new Pen(Brush(UiTheme.Text), 1.5), c, rad + 3, rad + 3);
    }

    private ColorPickerSheet? _picker;

    private void OpenPicker(string kind)
    {
        // La ruota colori di macOS; il selettore disegnato resta solo se il pannello di sistema non si apre.
        if (kind == "bg")
        {
            void Background(Color c)
            {
                UiTheme.ApplyBackground(c);
                MacSettings.Background = c.ToString();
                MacSettings.ThemeMode = UiTheme.IsLight ? "light" : "dark";
                ThemeChanged?.Invoke();
                InvalidateVisual();
            }
            if (!MacColorPanel.Show(UiTheme.Panel, "Colore sfondo", Background)) _picker = new ColorPickerSheet(UiTheme.Panel, "Colore sfondo", Background, BackgroundColors);
        }
        else
        {
            void Accent(Color c) { UiTheme.SetAccent(c); MacSettings.Accent = c.ToString(); ThemeChanged?.Invoke(); InvalidateVisual(); }
            if (!MacColorPanel.Show(UiTheme.Accent, "Colore accento", Accent)) _picker = new ColorPickerSheet(UiTheme.Accent, "Colore accento", Accent, AccentColors);
        }
        InvalidateVisual();
    }

    private void DrawValueChip(DrawingContext ctx, Rect r, string value, bool hover, bool emphasis = false)
    {
        ctx.DrawRectangle(Brush(A(UiTheme.Text, hover ? 40 : 22)), null, r, r.Height / 2, r.Height / 2);
        Text(ctx, value, 10, emphasis, new Rect(r.X + 29, r.Y, Math.Max(1, r.Width - 58), r.Height), emphasis ? UiTheme.Accent : UiTheme.Text, TextAlignment.Center);
        var pen = new Pen(Brush(UiTheme.Muted), 1.5);
        double cy = r.Y + r.Height / 2, left = r.X + 15, right = r.Right - 15;
        ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { new Point(left + 3, cy - 5), new Point(left - 2, cy), new Point(left + 3, cy + 5) }, false));
        ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { new Point(right - 3, cy - 5), new Point(right + 2, cy), new Point(right - 3, cy + 5) }, false));
    }

    /// <summary>DrawSettingsSwitch: pista sempre visibile, cambia solo il colore; lo stato scritto a sinistra.</summary>
    private void DrawSwitch(DrawingContext ctx, Rect r, string text, bool on)
    {
        var track = new Rect(r.Right - 44, r.Y + Math.Floor((r.Height - 22) / 2), 42, 22);
        bool light = UiTheme.IsLight;
        ctx.DrawRectangle(Brush(on ? UiTheme.Accent : A(UiTheme.Text, light ? 34 : 46)), new Pen(Brush(on ? UiTheme.Accent : A(UiTheme.Text, light ? 90 : 80)), 1), track, 11, 11);
        Color knob = on ? Colors.White : light ? Colors.White : A(UiTheme.Text, 230);
        ctx.DrawEllipse(Brush(knob), null, new Point(track.X + (on ? 23 : 3) + 8, track.Y + 3 + 8), 8, 8);
        Text(ctx, text, 10, false, new Rect(r.X, r.Y, Math.Max(1, track.X - r.X - 14), r.Height), UiTheme.Muted, TextAlignment.Right);
    }

    // ------------------------------------------------------------------ pagine dei componenti (DrawNativeProviderRows)
    private readonly Dictionary<string, string> _nativeGroup = new();

    private double DrawNativeRows(DrawingContext ctx, Rect body)
    {
        double width = body.Width;
        double controlW = Math.Min(330, Math.Max(210, Math.Floor(width * 2 / 5)));
        double controlX = body.X + width - controlW;
        double y = body.Y;
        double textW = Math.Max(90, controlX - body.X - 28);
        // Riga di stato (su Windows con "Pannello originale" a destra: sotto Wine le pagine originali non si aprono).
        Text(ctx, ComponentSettings.Status(_tab), 9, false, new Rect(body.X, y, width, 32), UiTheme.Muted);
        y += 44;

        var sections = ComponentSettings.Sections(_tab);
        var groups = sections.Select(x => x.Group).Where(g => g.Length > 0).Distinct().ToList();
        if (groups.Count > 1)
        {
            string current = _nativeGroup.TryGetValue(_tab, out var chosen) && groups.Contains(chosen) ? chosen : groups[0];
            DrawPillTabs(ctx, new Rect(body.X, y, width, 36), groups, current);
            y += 50;
            sections = sections.Where(x => x.Group == current).ToArray();
        }
        foreach (var section in sections)
        {
            ctx.DrawLine(new Pen(Brush(A(UiTheme.Border, 50)), 1), new Point(body.X, y + .5), new Point(body.X + width, y + .5));
            Text(ctx, section.Title.ToUpperInvariant(), 9, true, new Rect(body.X, y + 20, width, 24), UiTheme.Muted);
            y += 56;
            foreach (var setting in section.Settings)
            {
                Text(ctx, setting.Label, 10.5, false, new Rect(body.X, y, textW, 30), UiTheme.Text);
                if (setting.Description.Length > 0) Text(ctx, setting.Description, 9, false, new Rect(body.X, y + 29, textW, 23), UiTheme.Muted);
                var control = new Rect(controlX, y + 1, controlW, 36);
                string value = ComponentSettings.GetStored(_tab, setting);
                if (setting.Kind == ComponentSettings.Kind.Toggle)
                {
                    bool on = value == "1";
                    DrawSwitch(ctx, control, on ? "Attivo" : "Disattivato", on);
                    _hits.Add(new Hit(control, "native-toggle", setting.Key));
                }
                else
                {
                    var choice = setting.Choices?.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
                    DrawValueChip(ctx, control, choice?.Label ?? value, Hover(control));
                    _hits.Add(new Hit(control, "native-choice", setting.Key));
                }
                y += setting.Description.Length > 0 ? 66 : 56;
            }
            y += 12;
        }
        return y - body.Y + 40;
    }

    private void DrawPillTabs(DrawingContext ctx, Rect row, IReadOnlyList<string> tabs, string current)
    {
        double pad = 18, gap = 6;
        var widths = tabs.Select(t => Math.Ceiling(Measure(t, 10.2, true)) + pad * 2).ToList();
        double total = widths.Sum() + gap * Math.Max(0, tabs.Count - 1);
        double x = row.X + Math.Max(0, (row.Width - total) / 2);
        for (int i = 0; i < tabs.Count; i++)
        {
            var pill = new Rect(x, row.Y, widths[i], row.Height);
            bool selected = tabs[i] == current, hover = Hover(pill);
            if (selected || hover) ctx.DrawRectangle(Brush(A(UiTheme.Text, selected ? 40 : 16)), null, pill, pill.Height / 2, pill.Height / 2);
            Text(ctx, tabs[i], 10.2, selected, pill, selected || hover ? UiTheme.Text : A(UiTheme.Text, 150), TextAlignment.Center);
            _hits.Add(new Hit(pill, "native-group", tabs[i]));
            x += widths[i] + gap;
        }
    }

    private ComponentSettings.Setting? FindNative(string key) =>
        ComponentSettings.Sections(_tab).SelectMany(x => x.Settings).FirstOrDefault(x => x.Key == key);

    private void ApplyNative(ComponentSettings.Setting setting, string value)
    {
        foreach (var command in ComponentSettings.Set(_tab, setting, value)) HostCommand?.Invoke(command);
        InvalidateVisual();
    }

    /// <summary>ShowNativeChoice: le frecce ‹ › scorrono i valori, il centro apre l'elenco.</summary>
    private void ChooseNative(string key, Rect source, Point click)
    {
        if (FindNative(key) is not { Choices: { } choices } setting) return;
        string current = ComponentSettings.GetStored(_tab, setting);
        int step = click.X < source.X + 34 ? -1 : click.X > source.Right - 34 ? 1 : 0;
        if (step != 0)
        {
            int index = Array.FindIndex(choices, c => string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = step > 0 ? -1 : 0;
            ApplyNative(setting, choices[(index + step + choices.Length) % choices.Length].Value);
            return;
        }
        ShowChoices(source, choices.Select(c => (c.Label, string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase), (Action)(() => ApplyNative(setting, c.Value)))));
    }

    // ------------------------------------------------------------------ dati delle scelte
    private static readonly int[] TranscodeChoices = { 0, 40_000_000, 20_000_000, 10_000_000, 6_000_000, 3_000_000, 1_500_000 };
    // La stessa scelta del client Jellyfin di Windows (jellyfin-quality.txt).
    private static int JellyfinBitrate => CinecorePlayer2025.Utilities.JellyfinClient.TranscodeBitrate;
    private static string TranscodeLabel(int bitrate) =>
        bitrate <= 0 ? "File originale" : (bitrate / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " Mbit/s";

    private static readonly (string Code, string Italian)[] Languages =
    {
        ("it", "Italiano"), ("en", "Inglese"), ("fr", "Francese"), ("es", "Spagnolo"), ("de", "Tedesco"), ("pt-pt", "Portoghese"),
        ("pt-br", "Portoghese (Brasile)"), ("nl", "Olandese"), ("pl", "Polacco"), ("ru", "Russo"), ("uk", "Ucraino"), ("ja", "Giapponese"),
        ("ko", "Coreano"), ("zh-cn", "Cinese"), ("ar", "Arabo"), ("he", "Ebraico"), ("tr", "Turco"), ("el", "Greco"), ("sv", "Svedese"),
        ("no", "Norvegese"), ("da", "Danese"), ("fi", "Finlandese"), ("cs", "Ceco"), ("sk", "Slovacco"), ("hu", "Ungherese"),
        ("ro", "Rumeno"), ("bg", "Bulgaro"), ("hr", "Croato"), ("sr", "Serbo"), ("sl", "Sloveno"), ("hi", "Hindi"), ("th", "Thailandese"),
        ("vi", "Vietnamita"), ("id", "Indonesiano"),
    };
    private static string LanguageLabel(string code) => Languages.FirstOrDefault(l => l.Code == code).Italian ?? code;


    private static string TabLabel(string tab) => tab;

    // ------------------------------------------------------------------ azioni
    private void Activate(Hit hit, Point click)
    {
        if (hit.Kind.StartsWith("preset-", StringComparison.Ordinal)) { PresetActivate(hit, click); return; }
        switch (hit.Kind)
        {
            case "close": Close(); return;
            case "expand-desc":
                if (!_expandedRows.Remove(hit.Key)) _expandedRows.Add(hit.Key);
                InvalidateVisual();
                return;
            case "menu": OpenTabMenu(hit.Key); return;
            case "toggle" when hit.Key == "openAtLogin":
                // Si salva cio' che il sistema ha davvero fatto (fuori dall'app impacchettata non si puo').
                LoginItem.Set(!LoginItem.Enabled);
                MacSettings.Set("openAtLogin", LoginItem.Enabled);
                break;
            case "toggle" when hit.Key == "updateAutoCheck":
                MacUpdate.AutoCheck = !MacUpdate.AutoCheck;
                break;
            case "toggle" when hit.Key == "dvdMenus":
                MacDvdNav.Enabled = !MacDvdNav.Enabled;
                break;
            case "toggle" when hit.Key == "matchRefresh":
                MacDisplayRate.Enabled = !MacDisplayRate.Enabled;
                if (!MacDisplayRate.Enabled) System.Threading.Tasks.Task.Run(MacDisplayRate.Restore);
                break;
            case "toggle":
                bool now = !MacSettings.Get(hit.Key, false);
                MacSettings.Set(hit.Key, now);
                break;
            case "accent":
                if (Color.TryParse(hit.Key, out var accent)) { _themeFade.Begin(); UiTheme.SetAccent(accent); MacSettings.Accent = accent.ToString(); ThemeChanged?.Invoke(); }
                break;
            case "bg":
                if (Color.TryParse(hit.Key, out var bg))
                {
                    _themeFade.Begin();
                    UiTheme.ApplyBackground(bg);
                    MacSettings.Background = bg.ToString();
                    MacSettings.ThemeMode = UiTheme.IsLight ? "light" : "dark";
                    ThemeChanged?.Invoke();
                }
                break;
            case "choice": Choose(hit, click); break;
            case "bitstream-carrier":
            {
                string uid = MacAudioDevices.ConfiguredOutputUid;
                MacAudioDevices.SetCarrier(uid, hit.Key, !MacAudioDevices.CarrierEnabled(uid).Contains(hit.Key));
                break;
            }
            case "bitstream-test":
                if (!MacBitstreamTest.Running) { _bitstreamTesting = hit.Key; BitstreamTestRequested?.Invoke(hit.Key); }
                break;
            case "audio": ChooseAudio(hit); return;
            case "device": if (!DeviceAction(hit.Key, hit.Bounds)) DeviceServiceRequested?.Invoke(hit.Key); return;
            case "credential-toggle": ToggleCredentials(hit.Key); break;
            case "credential-save": SaveCredentials(hit.Key); return;
            case "credential-remove": RemoveCredentials(hit.Key); return;
            case "accent-custom": OpenPicker("accent"); return;
            case "bg-custom": OpenPicker("bg"); return;
            case "native-toggle":
                if (FindNative(hit.Key) is { } toggle) ApplyNative(toggle, ComponentSettings.GetStored(_tab, toggle) == "1" ? "0" : "1");
                break;
            case "native-choice": ChooseNative(hit.Key, hit.Bounds, click); break;
            case "native-group": _nativeGroup[_tab] = hit.Key; _scroll = 0; break;
        }
        InvalidateVisual();
    }

    private void Choose(Hit hit, Point click)
    {
        int direction = click.X < hit.Bounds.X + 30 ? -1 : 1;
        switch (hit.Key)
        {
            case "keychain-authorize":
                if (!CinecorePlayer2025.Utilities.Keychain.Authorizing) KeychainAuthorizationRequested?.Invoke();
                break;
            case "renderer":
            {
                // madVR e MPC Video Renderer sotto Wine, MPV nativo (libmpv).
                // App autonoma senza Wine: resta solo MPV.
                string[] values = !VideoHostClient.WineAvailable ? new[] { "Auto", "MPV" }
                    : MpvEngine.Available ? new[] { "Auto", "MADVR", "MPCVR", "MPV" } : new[] { "Auto", "MADVR", "MPCVR" };
                int i = Array.FindIndex(values, v => v.Equals(MacSettings.Renderer, StringComparison.OrdinalIgnoreCase));
                if (i < 0) i = direction > 0 ? -1 : 0;
                MacSettings.ChooseRenderer(values[(i + direction + values.Length) % values.Length]);
                RendererChanged?.Invoke(MacSettings.EffectiveRenderer);
                break;
            }
            case "madvr-lowpower":
            {
                string[] values = { "ask", "light", "asis" };
                int i = Array.IndexOf(values, MacSettings.MadVrLowPower);
                if (i < 0) i = direction > 0 ? -1 : 0;
                MacSettings.MadVrLowPower = values[(i + direction + values.Length) % values.Length];
                RendererChanged?.Invoke(MacSettings.EffectiveRenderer);
                break;
            }
            case "sizing-mode" or "sizing-detect" or "sizing-expand" or "sizing-transition" or "sizing-bias" when ImageSizing?.Invoke() is { } sizing:
            {
                switch (hit.Key)
                {
                    case "sizing-mode": sizing.Mode = (ImageSizingMode)(((int)sizing.Mode + direction + 4) % 4); break;
                    case "sizing-detect": sizing.DetectBars = !sizing.DetectBars; break;
                    case "sizing-expand": sizing.ExpandTallScenes = !sizing.ExpandTallScenes; break;
                    case "sizing-transition":
                    {
                        int[] steps = { 0, 200, 400, 700, 1000, 1500 };
                        int i = Array.IndexOf(steps, sizing.TransitionMs);
                        if (i < 0) i = 2;
                        sizing.TransitionMs = steps[Math.Clamp(i + direction, 0, steps.Length - 1)];
                        break;
                    }
                    case "sizing-bias": sizing.Bias = Math.Clamp(Math.Round(sizing.Bias * 10 + direction) / 10, 0, 1); break;
                }
                ImageSizingChanged?.Invoke();
                break;
            }
            case var presetKey when presetKey.StartsWith("sizing-preset-", StringComparison.Ordinal) && ImageSizing?.Invoke() is { } custom:
            {
                string key = presetKey["sizing-preset-".Length..];
                double value = custom.Multiplier(key);
                // 1,43 IMAX puo' scendere fino a "tutta l'area" (0); gli altri restano fra 0,80 e 1,50.
                double next = Math.Round(value + direction * 0.02, 2);
                if (key == "1.43" && next < 0.8) next = direction < 0 ? 0 : 0.8;
                if (key == "1.43" && value <= 0 && direction > 0) next = 0.8;
                custom.Multipliers[key] = key == "1.43" && next <= 0 ? 0 : Math.Clamp(next, 0.8, 1.5);
                ImageSizingChanged?.Invoke();
                break;
            }
            case "disc-protection": KeyDbImportRequested?.Invoke(); break;
            case "start-screen":
            {
                var options = new List<(string, bool, Action)>
                {
                    ("Ultima posizione", MainWindow.StartScreen == "", () => MainWindow.StartScreen = ""),
                    ("Schermo attivo (dove c’è il mouse)", MainWindow.StartScreen == "active", () => MainWindow.StartScreen = "active"),
                };
                foreach (var sc in ScreenChoices?.Invoke() ?? new())
                {
                    string key = sc.Key;
                    options.Add((sc.Label, MainWindow.StartScreen == key, () => MainWindow.StartScreen = key));
                }
                ShowChoices(hit.Bounds, options);
                break;
            }
            case "move-screen":
                ShowChoices(hit.Bounds, (ScreenChoices?.Invoke() ?? new()).Select(sc => (sc.Label, sc.Current, (Action)(() => MoveToScreen?.Invoke(sc.Key)))));
                break;
            case "dvd-playback": break; // per ora una sola modalita' (diretta)
#if CINECORE_WINDOWS
            case "mpv-runtime":
                MpvEngine.UseV3Runtime = !MpvEngine.UseV3Runtime;
                break;
#endif
            case "theme":
            {
                string[] values = { "system", "light", "dark" };
                int i = Array.IndexOf(values, MacSettings.ThemeMode);
                if (i < 0) i = direction > 0 ? -1 : 0;
                SetThemeMode(values[(i + direction + values.Length) % values.Length]);
                break;
            }
            case "spotlight-source":
            {
                bool network = MacSettings.Get("spotlightSource", MacSettings.Get("librarySource", "Library")) == "DLNA";
                ShowChoices(hit.Bounds, new[]
                {
                    ("Libreria", !network, (Action)(() => MacSettings.Set("spotlightSource", "Library"))),
                    ("Rete", network, () =>
                    {
                        MacSettings.Set("spotlightSource", "DLNA");
                        if (NetworkStore.Servers.All(s => s.Key != MacSettings.Get("spotlightServer", "")) && NetworkStore.Servers.FirstOrDefault(NetworkStore.IsMine) is { } first)
                            MacSettings.Set("spotlightServer", first.Key);
                    }),
                });
                break;
            }
            case "spotlight-server":
            {
                string current = MacSettings.Get("spotlightServer", "");
                var servers = NetworkStore.Servers.Where(s => NetworkStore.IsMine(s) || s.Available).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (servers.Count == 0) { ShowChoices(hit.Bounds, new[] { ("Nessun server trovato: aggiungilo da Rete", false, (Action)(() => { })) }); break; }
                ShowChoices(hit.Bounds, servers.Select(s => (s.Name + (s.IsJellyfin ? "" : "  (DLNA)"), s.Key == current, (Action)(() => MacSettings.Set("spotlightServer", s.Key)))));
                break;
            }
            case "music-source":
            {
                string current = CinecorePlayer2025.Utilities.MusicArtistArtworkService.PreferredSource;
                ShowChoices(hit.Bounds, new[] { ("auto", "Automatica (Spotify, TIDAL, Deezer)"), ("spotify", "Spotify"), ("tidal", "TIDAL"), ("deezer", "Deezer") }
                    .Select(o => (o.Item2, o.Item1 == current || (o.Item1 == "auto" && current is not ("spotify" or "tidal" or "deezer")),
                        (Action)(() => CinecorePlayer2025.Utilities.MusicArtistArtworkService.PreferredSource = o.Item1))));
                break;
            }
            case "header-size":
                ShowChoices(hit.Bounds, new[] { ("auto", "Automatica"), ("normal", "Normale"), ("large", "Grande"), ("xlarge", "Molto grande") }
                    .Select(o => (o.Item2, MacSettings.Get("headerSize", "auto") == o.Item1, (Action)(() => { MacSettings.Set("headerSize", o.Item1); InterfaceChanged?.Invoke(); }))));
                return;
            case "library-source":
                ShowChoices(hit.Bounds, new[]
                {
                    ("Mac", MacSettings.Get("librarySource", "Library") != "DLNA", (Action)(() => { MacSettings.Set("librarySource", "Library"); LibrarySourceChanged?.Invoke(); })),
                    ("Server di rete", MacSettings.Get("librarySource", "Library") == "DLNA", () =>
                    {
                        MacSettings.Set("librarySource", "DLNA");
                        // Senza un server scelto si prende il primo dei tuoi (con account o preferito).
                        if (NetworkStore.Servers.All(s => s.Key != MacSettings.Get("libraryServer", "")) && NetworkStore.Servers.FirstOrDefault(NetworkStore.IsMine) is { } first)
                            MacSettings.Set("libraryServer", first.Key);
                        LibrarySourceChanged?.Invoke();
                    }),
                });
                break;
            case "library-server":
            {
                // I tuoi server (con account o preferiti) e quelli trovati in rete, come il menu della fonte nella libreria.
                string current = MacSettings.Get("libraryServer", "");
                var servers = NetworkStore.Servers.Where(s => NetworkStore.IsMine(s) || s.Available).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (servers.Count == 0) { ShowChoices(hit.Bounds, new[] { ("Nessun server trovato: aggiungilo da Rete", false, (Action)(() => { })) }); break; }
                ShowChoices(hit.Bounds, servers.Select(s => (s.Name + (s.IsJellyfin ? "" : "  (DLNA)"), s.Key == current, (Action)(() =>
                {
                    MacSettings.Set("libraryServer", s.Key);
                    NetworkStore.ConnectedKey = s.Key;
                    LibrarySourceChanged?.Invoke();
                }))));
                break;
            }
            case "jellyfin-quality":
                ShowChoices(hit.Bounds, TranscodeChoices.Select(b => (TranscodeLabel(b), b == JellyfinBitrate,
                    (Action)(() => CinecorePlayer2025.Utilities.JellyfinClient.TranscodeBitrate = b))));
                break;
            case "language":
                // I nomi delle lingue restano nella loro lingua (non si traducono).
                ShowChoices(hit.Bounds, L10n.Languages.Select(l => (l.Name, l.Code == L10n.Language, (Action)(() => L10n.Set(l.Code)))));
                return;
            case "subtitle-language":
            {
                string current = MacSettings.SubtitleLanguage is { Length: > 0 } saved ? saved : "it";
                ShowChoices(hit.Bounds, Languages.Select(l => (l.Italian, l.Code == current, (Action)(() => MacSettings.SubtitleLanguage = l.Code))));
                break;
            }
        }
    }

    /// <summary>SetUiThemeMode: "Come macOS" segue il sistema; chiaro e scuro rimettono i colori pronti.</summary>
    private void SetThemeMode(string mode)
    {
        _themeFade.Begin();
        MacSettings.ThemeMode = mode;
        MacSettings.Background = "";
        MacSettings.Accent = "";
        bool light = mode == "system" ? SystemUsesLightTheme?.Invoke() ?? false : mode == "light";
        UiTheme.ApplyTone(light);
        ThemeChanged?.Invoke();
    }

    // ------------------------------------------------------------------ menu a discesa (TextMenuOverlay)
    private sealed record MenuItem(string Label, bool Selected, Action Select);
    private List<MenuItem>? _menuItems;
    private string? _menuKey;
    private Point _menuAnchor;
    private double _menuRow, _menuPt, _menuScroll;
    private DateTime _menuOpenAt, _menuCloseAt;
    private Action? _menuAfter;
    private int _menuHover = -1, _menuKeyIndex = -1;
    private readonly DispatcherTimer _menuTimer;
    private const double InMs = 190, OutMs = 150, Stagger = 25;

    private double PageScale => Math.Clamp(Math.Min(Bounds.Width / 1920, Bounds.Height / 1080), .45, 4);

    private void OpenTabMenu(string key)
    {
        var tabs = key == "player" ? PlayerTabs : ComponentTabs;
        var anchor = key == "player" ? _playerAnchor : _componentAnchor;
        double s = PageScale;
        OpenMenu(tabs.Select(t => new MenuItem(TabLabel(t), t == _tab, () => SelectTab(t))), anchor.TopLeft, Math.Round(42 * s), Math.Max(10, 11.4 * s), key);
    }

    private void ShowChoices(Rect source, IEnumerable<(string Label, bool Selected, Action Select)> options)
    {
        if (_capture != null) { _capture.AddRange(options); return; }
        double s = PageScale;
        OpenMenu(options.Select(o => new MenuItem(o.Label, o.Selected, () => { o.Select(); InvalidateVisual(); })),
            new Point(source.X, source.Bottom + 6), Math.Round(38 * s), Math.Max(9.6, 10.6 * s), null);
    }

    private void OpenMenu(IEnumerable<MenuItem> items, Point anchor, double row, double pt, string? key)
    {
        _menuItems = items.ToList();
        _menuKey = key;
        _menuRow = Math.Max(24, row);
        _menuPt = pt;
        _menuHover = _menuKeyIndex = -1;
        _menuAfter = null;
        _menuCloseAt = default;
        // Dentro la pagina: se le voci non stanno sotto il punto salgono, se non stanno a destra si spostano.
        double textW = _menuItems.Count == 0 ? 0 : _menuItems.Max(i => Measure(i.Label, pt, true));
        double margin = Math.Max(12, _menuRow / 2);
        double visible = Math.Min(_menuItems.Count, Math.Max(1, Math.Floor((Bounds.Height - 2 * margin) / _menuRow)));
        _menuAnchor = new Point(
            Math.Clamp(anchor.X, margin, Math.Max(margin, Bounds.Width - textW - margin)),
            Math.Clamp(anchor.Y, margin, Math.Max(margin, Bounds.Height - visible * _menuRow - margin)));
        int selected = _menuItems.FindIndex(i => i.Selected);
        _menuScroll = selected >= visible ? selected - visible + 1 : 0;
        _menuOpenAt = DateTime.UtcNow;
        _menuTimer.Start();
        InvalidateVisual();
    }

    private int MenuVisibleRows => _menuItems == null ? 0
        : (int)Math.Min(_menuItems.Count, Math.Max(1, Math.Floor((Bounds.Height - _menuAnchor.Y - Math.Max(12, _menuRow / 2)) / _menuRow)));

    private Rect MenuRow(int i)
    {
        double width = _menuItems!.Count == 0 ? 0 : _menuItems.Max(item => Measure(item.Label, _menuPt, true));
        return new Rect(_menuAnchor.X - 14, _menuAnchor.Y + (i - _menuScroll) * _menuRow, width + 28, _menuRow);
    }

    private double Since(DateTime t) => (DateTime.UtcNow - t).TotalMilliseconds;

    private double MenuProgress(int index)
    {
        if (_menuCloseAt != default) return 1 - Math.Clamp(Since(_menuCloseAt) / OutMs, 0, 1);
        return Math.Clamp((Since(_menuOpenAt) - index * Stagger) / InMs, 0, 1);
    }

    private void StepMenu()
    {
        InvalidateVisual();
        if (_menuItems == null) { _menuTimer.Stop(); return; }
        if (_menuCloseAt != default && Since(_menuCloseAt) >= OutMs)
        {
            _menuTimer.Stop();
            var after = _menuAfter;
            _menuItems = null; _menuKey = null; _menuAfter = null;
            after?.Invoke();
            InvalidateVisual();
        }
        else if (_menuCloseAt == default && Since(_menuOpenAt) >= InMs + _menuItems.Count * Stagger) _menuTimer.Stop();
    }

    private void CloseMenu(Action? after = null)
    {
        if (_menuItems == null || _menuCloseAt != default) return;
        _menuAfter = after;
        _menuCloseAt = DateTime.UtcNow;
        _menuTimer.Start();
    }

    private void DrawMenu(DrawingContext ctx, Rect page)
    {
        if (_menuItems == null) return;
        double veil = _menuCloseAt != default ? MenuProgress(0) : Math.Clamp(Since(_menuOpenAt) / InMs, 0, 1);
        veil = 1 - (1 - veil) * (1 - veil);
        ctx.FillRectangle(Brush(A(UiTheme.Panel, (UiTheme.IsLight ? 200 : 185) * veil)), page);
        int rows = MenuVisibleRows;
        for (int i = (int)_menuScroll; i < Math.Min(_menuItems.Count, _menuScroll + rows); i++)
        {
            double p = MenuProgress(i - (int)_menuScroll);
            double eased = 1 - Math.Pow(1 - p, 3);
            if (eased <= 0.01) continue;
            var r = MenuRow(i);
            double dy = Math.Round(10 * (1 - eased));
            bool strong = _menuItems[i].Selected || i == _menuHover || i == _menuKeyIndex;
            Text(ctx, _menuItems[i].Label, _menuPt, strong, new Rect(r.X + 14, r.Y + dy, r.Width, r.Height), A(UiTheme.Text, (strong ? 255 : 150) * eased));
        }
    }

    private int MenuHit(Point p)
    {
        if (_menuItems == null) return -1;
        int rows = MenuVisibleRows;
        for (int i = (int)_menuScroll; i < Math.Min(_menuItems.Count, _menuScroll + rows); i++)
            if (MenuRow(i).Contains(p)) return i;
        return -1;
    }

    private void SelectTab(string tab)
    {
        if (tab == _tab) return;
        _tabTransition.Begin();
        _tab = tab;
        _scroll = 0;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ input
    private void SetCursor(bool hand)
    {
        var cursor = hand ? HandCursor : Cursor.Default;
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        PointerMovedTo(e.GetPosition(this));
    }

    /// <summary>Solo per le prove (azione "settings-mouse" dello script QA).</summary>
    internal void TestMouse(Point p) => PointerMovedTo(p);
    /// <summary>Prove del selettore colore: "open" apre quello dell'accento, "hex" clicca nel codice; stato nel log.</summary>
    internal string TestPicker(string what)
    {
        if (what == "open") OpenPicker("accent");
        else if (what == "hex" && _picker != null) { Focus(); _picker.PointerPressed(_picker.QaHexPoint); }
        InvalidateVisual();
        return _picker == null ? "chiuso" : _picker.QaSummary;
    }
    internal int QaInvalidations { get; private set; }
    internal (bool Changed, bool Differs) QaHoverProbe(Point p) => HoverProbe.Run(this, _hoverZones, p, m => _mouse = m);

    private void PointerMovedTo(Point p)
    {
        _mouse = p;
        if (_eqDragBand >= 0) { UpdateEqDrag(_mouse); return; }
        if (_picker != null)
        {
            _picker.PointerMoved(_mouse);
            SetCursor(_picker.WantsHand(_mouse));
            InvalidateVisual();
            return;
        }
        bool menuChanged = false;
        if (_menuItems != null)
        {
            int hover = MenuHit(_mouse);
            if (hover != _menuHover) { _menuHover = hover; _menuKeyIndex = -1; menuChanged = true; }
            SetCursor(hover >= 0);
        }
        else
        {
            var hit = _hits.LastOrDefault(h => h.Bounds.Contains(_mouse) && h.Kind != "scrollbar");
            SetCursor(hit != null);
        }
        // Prima si ridisegnava tutta la pagina a ogni movimento del mouse.
        if (menuChanged || _hoverZones.Changed(_mouse)) { QaInvalidations++; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _mouse = new Point(-1000, -1000);
        InvalidateVisual();
    }

    private bool _draggingScrollbar;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        Focus();
        var p = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_picker != null)
        {
            _picker.PointerPressed(p);
            if (_picker.Done) _picker = null;
            e.Pointer.Capture(_picker != null ? this : null);
            InvalidateVisual();
            return;
        }
        if (_menuItems != null) return; // il menu decide al rilascio, come su Windows
        if (e.ClickCount == 2 && EqDoubleClick(p)) return;
        var hit = _hits.LastOrDefault(h => h.Bounds.Contains(p));
        if (hit == null) return;
        if (hit.Kind == "eqnode" && int.TryParse(hit.Key, out int eqBand)) { BeginEqDrag(eqBand, p); e.Pointer.Capture(this); return; }
        if (hit.Kind == "scrollbar") { _draggingScrollbar = true; DragScroll(p.Y); e.Pointer.Capture(this); return; }
        Activate(hit, p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_picker != null) { _picker.PointerReleased(); e.Pointer.Capture(null); return; }
        if (_draggingScrollbar) { _draggingScrollbar = false; e.Pointer.Capture(null); return; }
        if (EndEqDrag()) { e.Pointer.Capture(null); return; }
        if (_menuItems == null || _menuCloseAt != default) return;
        // Il rilascio del clic che ha aperto il menu non lo richiude.
        if (Since(_menuOpenAt) < 180) return;
        int i = MenuHit(e.GetPosition(this));
        if (i >= 0) { var item = _menuItems[i]; CloseMenu(item.Selected ? null : item.Select); }
        else CloseMenu();
    }

    private void DragScroll(double y)
    {
        if (_scrollMax <= 0) return;
        _scroll = Math.Clamp((y - _viewport.Y) / Math.Max(1, _viewport.Height) * (_viewport.Height + _scrollMax) - _viewport.Height / 2, 0, _scrollMax);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if (_picker != null) return;
        if (_menuItems != null)
        {
            int rows = MenuVisibleRows;
            _menuScroll = Math.Clamp(_menuScroll - Math.Sign(e.Delta.Y), 0, Math.Max(0, _menuItems.Count - rows));
            InvalidateVisual();
            return;
        }
        if (EqWheel(e.GetPosition(this), e.Delta.Y)) return; // rotella su un nodo del parametrico = Q
        // Trackpad: lo spostamento arriva gia' graduale, si segue a passi di 40 punti per unita'.
        _scroll = Math.Clamp(_scroll - e.Delta.Y * 40, 0, _scrollMax);
        InvalidateVisual();
    }

    public void HandleKey(KeyEventArgs e)
    {
        e.Handled = true;
        if (_picker != null)
        {
            // Incolla nel codice esadecimale.
            if (_picker.HexFocused && e.Key == Key.V && e.KeyModifiers.HasFlag(PlatformKeys.Command))
            {
                _ = PastePickerAsync(_picker);
                return;
            }
            _picker.Key(e);
            if (_picker.Done) _picker = null;
            // [2026-11-23] Solo i tasti di comando restano "gestiti": su Windows un KeyDown gestito toglie anche il
            // carattere (niente WM_CHAR) e nel codice esadecimale non si scriveva piu' niente.
            e.Handled = e.Key is Key.Escape or Key.Enter or Key.Back or Key.Delete;
            InvalidateVisual();
            return;
        }
        if (_menuItems != null)
        {
            if (_menuItems.Count == 0 || e.Key == Key.Escape) { CloseMenu(); return; }
            if (_menuKeyIndex < 0) _menuKeyIndex = Math.Max(0, _menuItems.FindIndex(i => i.Selected));
            if (e.Key == Key.Down) _menuKeyIndex = Math.Min(_menuItems.Count - 1, _menuKeyIndex + 1);
            else if (e.Key == Key.Up) _menuKeyIndex = Math.Max(0, _menuKeyIndex - 1);
            else if (e.Key == Key.Enter) { var item = _menuItems[_menuKeyIndex]; CloseMenu(item.Selected ? null : item.Select); }
            int rows = MenuVisibleRows;
            if (_menuKeyIndex < _menuScroll) _menuScroll = _menuKeyIndex;
            else if (_menuKeyIndex >= _menuScroll + rows) _menuScroll = _menuKeyIndex - rows + 1;
            _menuHover = -1;
            InvalidateVisual();
            return;
        }
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Down: _scroll = Math.Clamp(_scroll + 60, 0, _scrollMax); InvalidateVisual(); break;
            case Key.Up: _scroll = Math.Clamp(_scroll - 60, 0, _scrollMax); InvalidateVisual(); break;
            case Key.PageDown: _scroll = Math.Clamp(_scroll + _viewport.Height * .9, 0, _scrollMax); InvalidateVisual(); break;
            case Key.PageUp: _scroll = Math.Clamp(_scroll - _viewport.Height * .9, 0, _scrollMax); InvalidateVisual(); break;
            default: e.Handled = false; break;
        }
    }

    private async System.Threading.Tasks.Task PastePickerAsync(ColorPickerSheet picker)
    {
        try
        {
            string? text = TopLevel.GetTopLevel(this)?.Clipboard is { } clip ? await clip.GetTextAsync() : null;
            if (!string.IsNullOrWhiteSpace(text) && _picker == picker) { picker.Paste(text); InvalidateVisual(); }
        }
        catch { }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_picker == null || string.IsNullOrEmpty(e.Text)) return;
        _picker.TextInput(e.Text);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>Prova: apre una scheda o un menu senza mouse (CINECORE_SETTINGS).</summary>
    public void TestScroll(double value) { _scroll = value; InvalidateVisual(); }

    public void TestOpenMenu(string key)
    {
        // Dopo il primo disegno: la posizione delle voci del menu si conosce solo allora.
        if (key is "player" or "components") DispatcherTimer.RunOnce(() => OpenTabMenu(key), TimeSpan.FromMilliseconds(400));
        else if (key is "picker") OpenPicker("accent");
        else if (key is "tmdb" or "spotify" or "tidal") DispatcherTimer.RunOnce(() => { ToggleCredentials(key); _scroll = 1e6; InvalidateVisual(); }, TimeSpan.FromMilliseconds(500));
        else if (key.StartsWith("scroll:", StringComparison.Ordinal) && double.TryParse(key[7..], CultureInfo.InvariantCulture, out double y))
            DispatcherTimer.RunOnce(() => { _scroll = Math.Clamp(y, 0, _scrollMax); InvalidateVisual(); }, TimeSpan.FromMilliseconds(300));
        else SelectTab(key);
    }
}
