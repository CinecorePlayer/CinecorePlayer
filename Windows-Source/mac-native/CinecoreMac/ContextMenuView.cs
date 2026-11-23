using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>Una voce del menu (ToolStripMenuItem su Windows): testo, icona, valore a destra, spunta, sottomenu.</summary>
public sealed class MenuEntry
{
    public string Text = "";
    /// <summary>Icona esplicita ("menu-icon:..." su Windows); senza, la si deduce dal testo come MenuIconKey.</summary>
    public string? Icon;
    public Action? Run;
    public bool Checked, Separator;
    public bool Enabled = true;
    /// <summary>Available su Windows: una voce non disponibile non compare affatto.</summary>
    public bool Visible = true;
    /// <summary>Valore corrente in grigio a destra (ShortcutKeyDisplayString su Windows).</summary>
    public string? Value;
    /// <summary>Esegue la scelta e lascia il menu aperto (interruttori, valori da scorrere).</summary>
    public bool KeepOpen;
    public List<MenuEntry>? Children;
    internal MenuEntry? Parent;

    public MenuEntry(string text, string? icon = null, Action? run = null) { Text = text; Icon = icon; Run = run; }
    public static MenuEntry Sep() => new("") { Separator = true };
    public MenuEntry With(params MenuEntry[] children) { Children = children.ToList(); return this; }
}

/// <summary>
/// Menu del tasto destro: traduzione di ContextOverlayMenuForm (HUD/PlayerForm.ContextMenu.cs). Pannelli in vetro
/// arrotondati, righe da 34 con icona a sinistra, valore e freccia a destra, sottomenu che si aprono di lato dopo
/// 240 ms sopra la voce. La voce sotto il puntatore non ha riquadri: testo e icona pieni, le altre piu' tenui.
/// Resta dentro la finestra del player (su Windows non sconfina sopra le altre applicazioni).
/// </summary>
internal sealed class ContextMenuView : Control, Avalonia.Rendering.ICustomHitTest
{
    // [2026-11-22] I clic fuori dai pannelli arrivano lo stesso (chiudono il menu) senza riempire tutta la finestra di
    // trasparente: quel riempimento rendeva "sporca" l'intera finestra a ogni passaggio sulle voci, e sotto si
    // ridisegnava tutta la libreria. Ora si ridisegnano solo i pannelli.
    bool Avalonia.Rendering.ICustomHitTest.HitTest(Point point) => IsOpen;
    private double _glassOpacity = 1;
    // Misure a scala 1, moltiplicate per la scala del menu.
    private double _menuScale = 1;
    private double Scaled(double v) => Math.Round(v * _menuScale);
    private double PanelPadding => Scaled(6);
    private double RowHeight => Scaled(34);
    private double SeparatorHeight => Scaled(9);
    private double IconWidth => Scaled(33);
    private double ArrowWidth => Scaled(18);
    private double MinPanelWidth => Scaled(232);
    private double MaxPanelWidth => Scaled(380);
    private double PanelRadius => Scaled(10);
    private const double PanelGap = 0;

    // Tema scuro (Theme.Default*): testo bianco, SubtleText (190,204,220), Muted (142,160,180).
    // Tema chiaro sul vetro vero di macOS: vetro chiaro e voci scure (prima restava un vetro grigio con testo bianco).
    private bool LightGlass => UiTheme.IsLight && _nativeOn && Native?.HasLiquid == true;
    private Color Text => LightGlass ? Color.FromRgb(18, 20, 24) : Colors.White;
    private Color PanelBack => LightGlass ? Color.FromRgb(246, 247, 249) : Color.FromRgb(27, 31, 38);
    private static readonly Color PanelBorder = Color.FromArgb(55, 190, 204, 220);
    private Color TextDisabled => LightGlass ? Color.FromArgb(105, 60, 66, 76) : Color.FromArgb(110, 200, 206, 214); // ben piu' spenta delle voci a riposo
    private Color Muted => LightGlass ? Color.FromRgb(92, 100, 112) : Color.FromRgb(176, 190, 206);
    private Color SeparatorColor => LightGlass ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255);
    // Sul vetro chiaro di macOS il grigio di Windows (150,158,168) si leggeva male: voci a riposo quasi bianche.
    private Color DimText => LightGlass ? Color.FromRgb(44, 48, 56) : Color.FromRgb(222, 226, 232);
    private Color IconDisabled => LightGlass ? Color.FromArgb(90, 60, 66, 76) : Color.FromArgb(90, 200, 206, 214);
    private Color CheckedText => LightGlass ? Color.FromRgb(30, 34, 40) : Color.FromRgb((byte)((255 * 3 + 150) / 4), (byte)((255 * 3 + 158) / 4), (byte)((255 * 3 + 168) / 4));
    private Color TextNormal => LightGlass ? Color.FromArgb(200, 18, 20, 24) : Color.FromArgb(200, 255, 255, 255);

    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface MenuFace = new(Inter, FontStyle.Normal, FontWeight.Normal);
    private double FontSize => 9.5 * 96 / 72 * _menuScale; // Segoe UI 9.5 pt

    private sealed class PanelState
    {
        public int Depth;
        public MenuEntry? Source;
        public Rect Bounds;
        public readonly List<(MenuEntry Item, Rect Bounds)> Rows = new();
        /// <summary>Non c'era posto accanto al padre: sta sopra di lui e ha un fondo pieno (sotto non si legge nulla).</summary>
        public bool Overlaps;
        /// <summary>Aperto a sinistra del padre: i suoi sottomenu proseguono verso sinistra.</summary>
        public bool OpensLeft;
        /// <summary>Il vetro dal vivo del pannello, calcolato una volta e riusato al passaggio sulle voci (Windows).</summary>
        public readonly GlassCache Glass = new();

        public void Shift(double dx)
        {
            Bounds = Bounds.Translate(new Vector(dx, 0));
            for (int i = 0; i < Rows.Count; i++) Rows[i] = (Rows[i].Item, Rows[i].Bounds.Translate(new Vector(dx, 0)));
        }
    }

    private readonly List<PanelState> _panels = new();
    private MenuEntry? _hot, _pressed;
    private (PanelState Panel, MenuEntry Item, Rect Row)? _pendingHover;
    private readonly DispatcherTimer _submenuTimer, _outsideClickTimer;
    private DateTime _openedUtc;
    /// <summary>Stato globale del mouse (fuori dalla finestra); null = non disponibile.</summary>
    public Func<bool>? MouseDownOutside { get; set; }

    private void CloseIfClickLandedOutside()
    {
        if (!IsOpen || (DateTime.UtcNow - _openedUtc).TotalMilliseconds < 220) return;
        try { if (MouseDownOutside?.Invoke() == true) Close(); } catch { }
    }
    private Point _pointer;
    // "Mira" verso il sottomenu aperto: andando in diagonale verso di lui le voci attraversate non lo cambiano.
    private Point _lastMove;
    private DateTime _aimUntil;
    private Rect _screenBounds;

    private MenuAmbient.Frame? _ambient;
    private WriteableBitmap? _glass;
    private PixelSize _glassFor;
    private static WriteableBitmap? _noise;

    public bool IsOpen { get; private set; }
    public bool HasNativeGlass => _nativeOn;
    /// <summary>I pannelli aperti (coordinate della vista), per chi deve sapere cosa copre il menu.</summary>
    public IEnumerable<Rect> PanelBounds => _panels.Select(p => p.Bounds);
    /// <summary>Vetro nativo di macOS sotto i pannelli; senza, il vetro calcolato dal fotogramma.</summary>
    public MacGlass? Native { get; set; }
    private bool _nativeOn;
    private bool _legacyGlass;

    /// <summary>
    /// Il vetro di AppKit va aggiornato nello stesso passaggio in cui Avalonia ridisegna testo ed evidenziazione:
    /// cambiato subito, compariva (o spariva) prima delle righe, e per qualche fotogramma si vedevano un pannello vuoto
    /// o il vetro spostato rispetto al testo (il menu "sdoppiato"). Qui si segna soltanto; lo applica Render.
    /// </summary>
    private void SyncNative()
    {
        if (Native == null) return;
        _nativeDirty = true;
        InvalidateVisual();
    }

    private void ApplyNative()
    {
        if (Native == null || !_nativeDirty) return;
        _nativeDirty = false;
        _foregroundKeys.Clear();
        try
        {
            if (IsOpen && _panels.Count > 0) _nativeOn = Native.Show(_panels.Select(p => p.Bounds).ToList(), PanelRadius, _legacyGlass);
            else { Native.Clear(); _nativeOn = false; }
        }
        catch (Exception ex) { Console.Error.WriteLine("menu native glass: " + ex.Message); Native.Clear(); _nativeOn = false; }
    }
    private bool _nativeDirty;
    /// <summary>Il menu si e' chiuso; il punto e' l'ultima posizione del puntatore (per il risveglio dell'HUD).</summary>
    public event Action<Point>? Closed;
    /// <summary>Il menu si e' aperto (l'HUD tiene in sospeso i suoi avvisi finche' resta aperto).</summary>
    public event Action? Opened;

    public ContextMenuView()
    {
        IsHitTestVisible = false;
        Cursor = Cursor.Default;
        // [2026-11-22] 120 ms (erano 240): fermandosi su una voce il sottomenu arriva subito. La diagonale verso un
        // sottomenu gia' aperto resta protetta da _aimUntil.
        _submenuTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Input, (_, _) => SubmenuTick());
        _outsideClickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(45), DispatcherPriority.Input, (_, _) => CloseIfClickLandedOutside());
        // Il costruttore con il gestore avvia gia' i timer: si accendono solo all'apertura.
        _submenuTimer.Stop(); _outsideClickTimer.Stop();
    }

    // ------------------------------------------------------------------ apertura / chiusura

    // Entrata e uscita del menu disegnato dall'app (Windows): 170 ms di dissolvenza con un lieve ingrandimento dal
    // punto del clic, 130 ms di uscita.
    // [2026-11-22] Entrata e uscita piu' brevi (erano 170 e 130 ms): il menu e' leggibile prima.
    private const double EnterMs = 110, ExitMs = 90;
    private List<PanelState>? _ghostPanels;
    private WriteableBitmap? _ghostGlass;
    private DateTime _ghostAt;
    private Point _openPoint;

    /// <summary>Il menu appena chiuso sta ancora sfumando.</summary>
    public bool HasGhost => _ghostPanels != null;

    private void DropGhost()
    {
        _ghostGlass?.Dispose();
        if (_ghostPanels != null) foreach (var p in _ghostPanels) p.Glass.Dispose();
        _ghostGlass = null; _ghostPanels = null;
    }

    public void Open(IReadOnlyList<MenuEntry> root, Point at, MenuAmbient.Frame? ambient,
        bool legacyGlass = true, Func<MenuAmbient.Frame?>? captureFallback = null)
    {
        DropGhost();
        _legacyGlass = legacyGlass;
        _panels.Clear();
        _hot = _pressed = null;
        _pendingHover = null;
        _submenuTimer.Stop();
        Link(root, null);
        // La scala e' quella dello schermo (gia' nei punti di macOS); l'altezza della finestra aggiunge al massimo un quarto.
        _menuScale = Math.Clamp(Bounds.Height / 1080, 1, 1.25);
        _screenBounds = new Rect(Bounds.Size);
        _ambient = ambient;
        _glass?.Dispose();
        _glass = null;
        var rootPanel = BuildPanel(root, at, 0, null, null);
        if (Debug) Console.Error.WriteLine($"[menu] open at {at} bounds {Bounds} rows {rootPanel.Rows.Count} panel {rootPanel.Bounds}");
        if (!rootPanel.Rows.Any(r => !r.Item.Separator)) return;
        // Un solo lato per tutti i sottomenu, deciso qui sul più largo: prima ognuno sceglieva per conto suo
        // (Audio a destra, Sottotitoli a sinistra) e il menu sembrava aprirsi a caso.
        double widest = root.Where(HasSubItems).Select(i => MeasurePanelWidth(Renderable(i.Children!))).DefaultIfEmpty(0).Max();
        double roomRight = _screenBounds.Right - (rootPanel.Bounds.Right + PanelGap), roomLeft = rootPanel.Bounds.Left - PanelGap - _screenBounds.Left;
        rootPanel.OpensLeft = widest > 0 && roomRight < widest && roomLeft > roomRight;
        _panels.Add(rootPanel);
        _pointer = at; _openPoint = at; _lastMove = default; _aimUntil = default;
        _hot = HitTest(at)?.Item;
        IsOpen = true;
        IsHitTestVisible = true;
        _openedUtc = DateTime.UtcNow;
        _outsideClickTimer.Start();
        SyncNative();
        // Il vetro arriva al primo disegno; per le scelte di adesso basta sapere se ci sara'.
        _nativeOn = Native != null && (_legacyGlass || OperatingSystem.IsMacOSVersionAtLeast(26));
#if CINECORE_WINDOWS
        // Su Windows il vetro e' dal vivo (BackdropGlassOp): niente fotografia della pagina (~100 ms all'apertura).
        _ = captureFallback;
#else
        if (!_nativeOn && captureFallback != null) _ambient = captureFallback();
#endif
        InvalidateVisual();
        Opened?.Invoke();
    }

    /// <summary>E' arrivato il fotogramma del punto in riproduzione: il vetro si rifa' su quello.</summary>
    public void UpdateAmbient(MenuAmbient.Frame frame)
    {
        if (!IsOpen) return;
        _ambient = frame;
        _glass?.Dispose();
        _glass = null;
        InvalidateVisual();
    }

    private static readonly bool Debug = Environment.GetEnvironmentVariable("CINECORE_MENU_DEBUG") == "1";
    private static readonly bool QaTiming = Environment.GetEnvironmentVariable("CINECORE_QA_FRAMES") == "1";

    /// <summary>Chiude senza la dissolvenza: prima di fotografare la finestra (schermo intero, PiP, mini player).</summary>
    public void CloseNow()
    {
        Close();
        DropGhost();
        InvalidateVisual();
    }

    public void Close()
    {
        if (!IsOpen) return;
        if (Debug) Console.Error.WriteLine("[menu] close\n" + Environment.StackTrace);
        IsOpen = false;
        IsHitTestVisible = false;
        _submenuTimer.Stop();
        _outsideClickTimer.Stop();
        _pendingHover = null;
        // Senza il vetro di sistema (Windows) il menu esce in dissolvenza come sul Mac, invece di sparire di colpo.
        DropGhost();
        if (!_nativeOn && _panels.Count > 0) { _ghostPanels = _panels.ToList(); _ghostGlass = _glass; _ghostAt = DateTime.UtcNow; _glass = null; }
        _panels.Clear();
        _hot = _pressed = null;
        _ambient = null;
        _glass?.Dispose();
        _glass = null;
        SyncNative();
        InvalidateVisual();
        Closed?.Invoke(_pointer);
    }

    private static void Link(IEnumerable<MenuEntry> items, MenuEntry? parent)
    {
        foreach (var item in items)
        {
            item.Parent = parent;
            if (item.Children != null) Link(item.Children, item);
        }
    }

    private void Perform(MenuEntry item)
    {
        if (item.KeepOpen)
        {
            // Interruttori e valori: la scelta si applica subito e il menu resta aperto per la prossima.
            try { item.Run?.Invoke(); } catch (Exception ex) { Console.Error.WriteLine("menu: " + ex.Message); }
            InvalidateVisual();
            return;
        }
        Close();
        try { item.Run?.Invoke(); } catch (Exception ex) { Console.Error.WriteLine("menu: " + ex.Message); }
    }

    // ------------------------------------------------------------------ pannelli

    private static List<MenuEntry> Renderable(IEnumerable<MenuEntry> items)
    {
        var result = new List<MenuEntry>();
        bool lastWasSeparator = true;
        foreach (var item in items)
        {
            if (!item.Visible) continue;
            if (item.Separator)
            {
                if (!lastWasSeparator) { result.Add(item); lastWasSeparator = true; }
                continue;
            }
            result.Add(item);
            lastWasSeparator = false;
        }
        while (result.Count > 0 && result[^1].Separator) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static bool HasSubItems(MenuEntry item) => item.Children?.Any(c => c.Visible) == true;

    private CachedText Format(string text, Color color, double maxWidth = double.PositiveInfinity) =>
        UiText.Get(L10n.T(text), MenuFace, FontSize, color, maxWidth);

    private double MeasurePanelWidth(List<MenuEntry> items)
    {
        double width = MinPanelWidth;
        foreach (var item in items)
        {
            if (item.Separator) continue;
            double textWidth = Math.Ceiling(Format(item.Text, Text).WidthIncludingTrailingWhitespace);
            if (!string.IsNullOrEmpty(item.Value)) textWidth += Scaled(18) + Math.Ceiling(Format(item.Value, Text).WidthIncludingTrailingWhitespace);
            width = Math.Max(width, PanelPadding * 2 + IconWidth + ArrowWidth + Scaled(12) + textWidth);
        }
        return Math.Min(MaxPanelWidth, width);
    }

    private PanelState BuildPanel(IEnumerable<MenuEntry> items, Point requested, int depth, MenuEntry? source, Rect? parentRow, bool preferLeft = false)
    {
        var render = Renderable(items);
        double width = MeasurePanelWidth(render);
        // Il menu appartiene al player: non sconfina fuori dalla finestra.
        var work = _screenBounds;
        int normalRows = render.Count(i => !i.Separator);
        int separatorRows = render.Count - normalRows;
        double rowHeight = RowHeight;
        if (normalRows > 0)
        {
            double available = work.Height - 8 - PanelPadding * 2 - separatorRows * SeparatorHeight;
            rowHeight = Math.Clamp(Math.Floor(available / normalRows), 20, RowHeight);
        }
        double height = Math.Max(PanelPadding * 2 + rowHeight, PanelPadding * 2 + separatorRows * SeparatorHeight + normalRows * rowHeight);

        double x = Math.Round(requested.X), y = Math.Round(requested.Y);
        bool opensLeft = false;
        if (parentRow is { } parent)
        {
            // Si prosegue nella direzione del padre; se di la' non c'e' posto, dall'altra parte.
            double right = parent.Right + PanelGap, left = parent.Left - width - PanelGap;
            bool rightFits = right + width <= work.Right, leftFits = left >= work.Left;
            opensLeft = preferLeft ? leftFits || !rightFits : !rightFits && leftFits;
            if (!rightFits && !leftFits) opensLeft = preferLeft ? parent.Left - work.Left >= (work.Right - parent.Right) * 0.6 : parent.Left - work.Left > (work.Right - parent.Right) / 0.6;
            x = opensLeft ? left : right;
            y = parent.Top - PanelPadding;
        }
        else
        {
            // Menu principale come su macOS: dal puntatore verso destra e in basso; se non ci sta, tutto a sinistra del
            // puntatore (o verso l'alto), non spinto indietro di quel tanto che serve (sembrava aprirsi a caso).
            if (x + width > work.Right - 2 && x - width >= work.Left + 2) x -= width;
            if (y + height > work.Bottom - 2 && y - height >= work.Top + 2) y -= height;
        }
        x = Math.Clamp(x, work.Left + 2, Math.Max(work.Left + 2, work.Right - width - 2));
        y = Math.Clamp(y, work.Top + 2, Math.Max(work.Top + 2, work.Bottom - height - 2));

        var panel = new PanelState { Depth = depth, Source = source, Bounds = new Rect(x, y, width, height), OpensLeft = opensLeft };
        double rowY = y + PanelPadding;
        foreach (var item in render)
        {
            double h = item.Separator ? SeparatorHeight : rowHeight;
            panel.Rows.Add((item, new Rect(x + PanelPadding, rowY, width - PanelPadding * 2, h)));
            rowY += h;
        }
        return panel;
    }

    private void OpenSubMenu(PanelState parentPanel, Rect parentRow, MenuEntry item)
    {
        if (!HasSubItems(item)) { TrimPanelsAfter(parentPanel.Depth); return; }
        int depth = parentPanel.Depth + 1;
        if (_panels.Any(p => p.Depth == depth && ReferenceEquals(p.Source, item))) return;
        TrimPanelsAfter(parentPanel.Depth);
        var child = BuildPanel(item.Children!, new Point(parentPanel.Bounds.Right + PanelGap, parentRow.Top - PanelPadding), depth, item,
            new Rect(parentPanel.Bounds.Left, parentRow.Top, parentPanel.Bounds.Width, parentRow.Height), parentPanel.OpensLeft);
        // Nessun lato libero dentro la finestra: il sottomenu resta dal suo lato e copre in parte gli altri, con il fondo
        // pieno. I pannelli già aperti non si spostano più (prima scorrevano di lato e il menu "saltava").
        if (_panels.Any(p => p.Bounds.Intersects(child.Bounds))) child.Overlaps = true;
        _panels.Add(child);
        SyncNative();
        InvalidateVisual();
    }

    /// <summary>Il puntatore va dal padre verso il sottomenu aperto (dentro il triangolo fra il punto di prima e il bordo vicino del sottomenu)?</summary>
    private bool HeadingToChild(PanelState panel, Point from, Point to)
    {
        var child = _panels.FirstOrDefault(p => p.Depth == panel.Depth + 1);
        if (child == null || from == default) return false;
        bool right = child.Bounds.Left >= panel.Bounds.Center.X;
        double edge = right ? child.Bounds.Left : child.Bounds.Right;
        double dx = to.X - from.X;
        if (right ? dx <= 0 : dx >= 0) return false;
        var a = from; var b = new Point(edge, child.Bounds.Top - 20); var c = new Point(edge, child.Bounds.Bottom + 20);
        double Sign(Point p1, Point p2, Point p3) => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
        double d1 = Sign(to, a, b), d2 = Sign(to, b, c), d3 = Sign(to, c, a);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private void TrimPanelsAfter(int depth)
    {
        if (_panels.RemoveAll(p => { if (p.Depth <= depth) return false; p.Glass.Dispose(); return true; }) > 0) { SyncNative(); InvalidateVisual(); }
    }

    private (PanelState Panel, MenuEntry Item, Rect Row)? HitTest(Point p)
    {
        for (int i = _panels.Count - 1; i >= 0; i--)
        {
            var panel = _panels[i];
            if (!panel.Bounds.Contains(p)) continue;
            foreach (var (item, bounds) in panel.Rows)
                if (bounds.Contains(p)) return (panel, item, bounds);
        }
        return null;
    }

    private void SubmenuTick()
    {
        _submenuTimer.Stop();
        var pending = _pendingHover;
        _pendingHover = null;
        var hit = HitTest(_pointer);
        if (pending == null || hit == null || !ReferenceEquals(hit.Value.Item, pending.Value.Item)) return;
        if (DateTime.UtcNow < _aimUntil) { _pendingHover = pending; _submenuTimer.Start(); return; }
        if (hit.Value.Item.Enabled && HasSubItems(hit.Value.Item)) OpenSubMenu(hit.Value.Panel, hit.Value.Row, hit.Value.Item);
        else TrimPanelsAfter(hit.Value.Panel.Depth);
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsOpen) return;
        e.Handled = true;
        var previous = _lastMove;
        _pointer = _lastMove = e.GetPosition(this);
        var hit = HitTest(_pointer);
        if (hit == null)
        {
            if (_hot != null) { _hot = null; InvalidateVisual(); }
            return;
        }
        if (HeadingToChild(hit.Value.Panel, previous, _pointer)) _aimUntil = DateTime.UtcNow.AddMilliseconds(220);
        if (!ReferenceEquals(_hot, hit.Value.Item)) { _hot = hit.Value.Item; InvalidateVisual(); }
        if (!ReferenceEquals(_pendingHover?.Item, hit.Value.Item))
        {
            _submenuTimer.Stop();
            _pendingHover = hit;
            _submenuTimer.Start();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsOpen) return;
        e.Handled = true;
        _submenuTimer.Stop();
        _pendingHover = null;
        _pointer = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        // Un altro tasto (o un clic fuori dai pannelli) chiude il menu; il clic non arriva al player sotto.
        if (!props.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Close(); return; }
        var hit = HitTest(_pointer);
        if (hit == null) { Close(); return; }
        var item = hit.Value.Item;
        if (item.Separator || !item.Enabled) return;
        if (HasSubItems(item))
        {
            _pressed = item;
            InvalidateVisual();
            OpenSubMenu(hit.Value.Panel, hit.Value.Row, item);
            return;
        }
        // Le voci foglia partono sul primo clic (come su Windows, dove il mouse-up poteva non arrivare).
        _pressed = null;
        Perform(item);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsOpen) return;
        e.Handled = true;
        _pressed = null;
        InvalidateVisual();
    }

    /// <summary>Solo per le prove (catture dello schermo): apre il sottomenu della voce con questo testo e la evidenzia.</summary>
    public void TestOpenSubmenu(string text)
    {
        if (!IsOpen) return;
        var panel = _panels.Last();
        var row = panel.Rows.FirstOrDefault(r => r.Item.Text == text);
        if (row.Item == null) return;
        _hot = row.Item;
        OpenSubMenu(panel, row.Bounds, row.Item);
    }

    internal bool TestActivatePath(string path)
    {
        if (!IsOpen || _panels.Count == 0) return false;
        IEnumerable<MenuEntry> entries = _panels[0].Rows.Select(r => r.Item);
        MenuEntry? found = null;
        foreach (string label in path.Split('|'))
        {
            found = entries.FirstOrDefault(e => e.Text == label && e.Enabled && e.Visible);
            if (found == null) return false;
            entries = found.Children ?? Enumerable.Empty<MenuEntry>();
        }
        if (found?.Run == null) return false;
        Perform(found);
        return true;
    }

    /// <summary>Tastiera (OnKeyDown di ContextOverlayMenuForm): Esc, su/giu', sinistra, destra/Invio.</summary>
    public void HandleKey(Key key)
    {
        if (!IsOpen) return;
        _submenuTimer.Stop();
        _pendingHover = null;
        if (key == Key.Escape) { Close(); return; }
        var panel = _panels.LastOrDefault(p => p.Rows.Any(r => ReferenceEquals(r.Item, _hot))) ?? _panels.Last();
        var selectable = panel.Rows.Where(r => !r.Item.Separator && r.Item.Enabled).ToList();
        if (selectable.Count > 0 && key is Key.Up or Key.Down)
        {
            int current = selectable.FindIndex(r => ReferenceEquals(r.Item, _hot));
            int delta = key == Key.Down ? 1 : -1;
            int next = current < 0 ? (delta > 0 ? 0 : selectable.Count - 1) : (current + delta + selectable.Count) % selectable.Count;
            _hot = selectable[next].Item;
            InvalidateVisual();
            return;
        }
        if (key == Key.Left && panel.Depth > 0)
        {
            TrimPanelsAfter(panel.Depth - 1);
            _hot = panel.Source;
            InvalidateVisual();
            return;
        }
        var hotRow = panel.Rows.FirstOrDefault(r => ReferenceEquals(r.Item, _hot));
        if (hotRow.Item is { Separator: false, Enabled: true } hot && key is Key.Right or Key.Enter or Key.Return)
        {
            if (HasSubItems(hot))
            {
                OpenSubMenu(panel, hotRow.Bounds, hot);
                var child = _panels.Last();
                _hot = child.Rows.FirstOrDefault(r => !r.Item.Separator && r.Item.Enabled).Item;
                InvalidateVisual();
            }
            else if (key != Key.Right) Perform(hot);
        }
    }

    // ------------------------------------------------------------------ disegno

    private int _qaRenders; private double _qaRenderMs; private long _qaRenderSecond;
    public override void Render(DrawingContext ctx)
    {
        if (!QaTiming) { RenderCore(ctx); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RenderCore(ctx);
        _qaRenders++; _qaRenderMs += sw.Elapsed.TotalMilliseconds;
        long sec = Environment.TickCount64 / 1000;
        if (sec != _qaRenderSecond) { if (_qaRenders > 1) Console.Error.WriteLine($"[qa-menu] {_qaRenders} disegni/s, medio {_qaRenderMs / _qaRenders:0.00} ms"); _qaRenderSecond = sec; _qaRenders = 0; _qaRenderMs = 0; }
    }

    private void RenderCore(DrawingContext ctx)
    {
        ApplyNative();
        if (!IsOpen)
        {
            if (_ghostPanels == null) return;
            double q = (DateTime.UtcNow - _ghostAt).TotalMilliseconds / ExitMs;
            if (q >= 1) { DropGhost(); return; }
            double ghostScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var keep = _glass; _glass = _ghostGlass;
            _glassOpacity = 1 - q * q;
            try
            {
                using (ctx.PushOpacity(1 - q * q))
                    foreach (var panel in _ghostPanels)
                        try { DrawPanel(ctx, panel, ghostScaling); } catch { }
            }
            finally { _glass = keep; }
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            return;
        }
        // I clic fuori dai pannelli li raccoglie ICustomHitTest (in cima alla classe).
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (Native is { HasLiquid: true } native)
        {
            try
            {
                for (int i = 0; i < _panels.Count; i++) DrawNativeForeground(native, i, scaling);
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("menu native foreground: " + ex.Message);
                native.Clear();
                _nativeOn = false;
            }
        }
        var device = new PixelSize(Math.Max(1, (int)Math.Round(Bounds.Width * scaling)), Math.Max(1, (int)Math.Round(Bounds.Height * scaling)));
        if (!_nativeOn && _ambient != null && (_glass == null || _glassFor != device))
        {
            var swGlass = System.Diagnostics.Stopwatch.StartNew();
            try { _glass?.Dispose(); _glass = LiquidGlass.Build(_ambient, device); _glassFor = device; }
            catch (Exception ex) { Console.Error.WriteLine("menu glass: " + ex.Message); _glass = null; }
            if (QaTiming) Console.Error.WriteLine($"[menu] vetro {swGlass.ElapsedMilliseconds} ms per {device}");
        }
        _noise ??= LiquidGlass.NoiseTile();
        double enter = Math.Clamp((DateTime.UtcNow - _openedUtc).TotalMilliseconds / EnterMs, 0, 1);
        double eased = 1 - Math.Pow(1 - enter, 3);
        if (enter < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        var origin = _openPoint;
        _glassOpacity = eased;
        double grow = .965 + .035 * eased;
        using (ctx.PushOpacity(eased))
        using (ctx.PushTransform(Matrix.CreateTranslation(-origin.X, -origin.Y) * Matrix.CreateScale(grow, grow) * Matrix.CreateTranslation(origin.X, origin.Y)))
            foreach (var panel in _panels)
            {
                // Un errore in un pannello non deve fermare il disegno di tutta la finestra.
                try { DrawPanel(ctx, panel, scaling); }
                catch (Exception ex) { Console.Error.WriteLine("menu render: " + ex); }
            }
    }

    // Primo piano gia' consegnato al vetro, per pannello: passando sulle voci si ridisegna (e si rilegge dalla GPU) solo
    // il pannello che cambia, non tutti. Sotto madVR la GPU e' piena e ogni rilettura in piu' si sente.
    private readonly Dictionary<int, string> _foregroundKeys = new();

    private string ForegroundKey(PanelState panel, double scaling)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(panel)).Append('|').Append(panel.Bounds).Append('|').Append(scaling)
          .Append('|').Append(panel.Overlaps).Append('|').Append(Text).Append('|').Append(_menuScale);
        for (int i = 0; i < panel.Rows.Count; i++)
        {
            var item = panel.Rows[i].Item;
            bool hot = ReferenceEquals(_hot, item) || _panels.Any(p => ReferenceEquals(p.Source, item));
            if (hot) sb.Append("|h").Append(i);
            if (ReferenceEquals(_pressed, item)) sb.Append("|p").Append(i);
            sb.Append(item.Enabled ? 'e' : 'd').Append(item.Checked ? 'c' : '-').Append(item.Text).Append(item.Value);
        }
        return sb.ToString();
    }

    private void DrawNativeForeground(MacGlass native, int index, double scaling)
    {
        var panel = _panels[index];
        string key = ForegroundKey(panel, scaling);
        if (_foregroundKeys.TryGetValue(index, out var drawn) && drawn == key) return;
        var bounds = panel.Bounds;
        var size = new PixelSize((int)Math.Ceiling(bounds.Width * scaling), (int)Math.Ceiling(bounds.Height * scaling));
        var dpi = new Vector(96, 96);
        using var bitmap = new RenderTargetBitmap(size, dpi);
        using (var ctx = bitmap.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateTranslation(-bounds.X, -bounds.Y) * Matrix.CreateScale(scaling, scaling)))
        {
            DrawOverlap(ctx, panel);
            // Luce sui bordi come la barra della musica (il bordo del vetro che prende la luce, alla Apple).
            if (!panel.Overlaps) LibraryView.DrawGlassRim(ctx, panel.Bounds, PanelRadius, UiTheme.IsLight, scaling);
            DrawPanelForeground(ctx, panel, scaling);
        }
        using var foreground = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var pixels = foreground.Lock();
        bitmap.CopyPixels(pixels, AlphaFormat.Premul);
        native.SetForeground(index, pixels);
        _foregroundKeys[index] = key;
    }

    private static Geometry RoundRect(Rect r, double radius)
    {
        double rad = Math.Max(1, Math.Min(radius, Math.Min(Math.Max(1, r.Width), Math.Max(1, r.Height)) / 2));
        return new RectangleGeometry(r, rad, rad);
    }

    private void DrawPanel(DrawingContext ctx, PanelState panel, double scaling)
    {
        var r = panel.Bounds;
        double hair = 1 / scaling; // un pixel dello schermo, come la penna da 1 di GDI+
        // La sagoma della finestra segue la curva del pannello, un pixel piu' larga del disegno.
        if (!_nativeOn)
        using (ctx.PushGeometryClip(RoundRect(r, PanelRadius + 1)))
        {
#if CINECORE_WINDOWS
            // Vetro dal vivo, come la barra e la coda: sfoca la pagina sotto il pannello nello stesso fotogramma.
            // Con un fotogramma del film (madVR non e' nella superficie) resta il vetro costruito da quello.
            if (_glass == null && _ambient == null) ctx.Custom(BackdropGlassOp.Liquid(r, PanelRadius + 1, _glassOpacity, 0, cache: panel.Glass));
            else
#endif
            ctx.FillRectangle(new SolidColorBrush(PanelBack), r);
            if (_glass != null)
            {
                // Vetro "ambient": tutto il rettangolo e' vetro, la curva la da' la sagoma.
                double k = scaling / 4.0;
                var src = new Rect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(_glass, src, r);
                if (_noise != null)
                {
                    var tile = new ImageBrush(_noise)
                    {
                        TileMode = TileMode.Tile,
                        Stretch = Stretch.Fill,
                        DestinationRect = new RelativeRect(0, 0, 128 / scaling, 128 / scaling, RelativeUnit.Absolute),
                    };
                    ctx.FillRectangle(tile, r);
                }
            }
        }
        DrawOverlap(ctx, panel);
        var border = new Rect(r.X + hair / 2, r.Y + hair / 2, r.Width - hair, r.Height - hair);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(PanelBorder), hair), RoundRect(border, PanelRadius));
        DrawPanelForeground(ctx, panel, scaling);
    }

    private void DrawPanelForeground(DrawingContext ctx, PanelState panel, double scaling)
    {
        foreach (var (item, bounds) in panel.Rows) DrawRow(ctx, item, bounds, scaling);
    }

    private void DrawOverlap(DrawingContext ctx, PanelState panel)
    {
        if (panel.Overlaps)
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(246, PanelBack.R, PanelBack.G, PanelBack.B)), null,
                RoundRect(panel.Bounds, PanelRadius));
    }

    private void DrawRow(DrawingContext ctx, MenuEntry item, Rect local, double scaling)
    {
        if (item.Separator)
        {
            double hair = 1 / scaling;
            double y = Math.Floor(local.Top + local.Height / 2) + hair / 2;
            ctx.DrawLine(new Pen(new SolidColorBrush(SeparatorColor), hair), new Point(local.Left + 8, y), new Point(local.Right - 8, y));
            return;
        }

        bool hot = ReferenceEquals(_hot, item) || _panels.Any(p => ReferenceEquals(p.Source, item));
        bool pressed = ReferenceEquals(_pressed, item);
        bool enabled = item.Enabled;
        // Niente riquadro: la voce sotto il puntatore e' piena, le altre restano chiaramente piu' tenui.
        Color iconColor = enabled ? (hot || pressed ? Text : DimText) : IconDisabled;
        var iconRect = new Rect(local.Left + Scaled(9), local.Top + Math.Floor((local.Height - Scaled(18)) / 2), Scaled(18), Scaled(18));
        // Voce attiva: la spunta prende il posto dell'icona.
        if (item.Checked)
            MenuIcon("check")?.Draw(ctx, iconRect.Deflate(1), enabled ? Text : TextDisabled);
        else
            (MenuIcon(MenuIconKey(item)) ?? MenuIcon("dot"))?.Draw(ctx, iconRect, iconColor);

        var textRect = new Rect(local.Left + IconWidth + Scaled(5), local.Top, Math.Max(20, local.Width - IconWidth - ArrowWidth - Scaled(12)), local.Height);
        // Valore corrente (es. "Automix") in grigio a destra, prima della freccia del sottomenu.
        if (!string.IsNullOrEmpty(item.Value))
        {
            var value = Format(item.Value, enabled ? Muted : TextDisabled);
            double valueW = Math.Ceiling(value.WidthIncludingTrailingWhitespace);
            ctx.DrawText(value, new Point(textRect.Right - valueW, local.Top + (local.Height - value.Height) / 2));
            textRect = textRect.WithWidth(Math.Max(20, textRect.Width - valueW - Scaled(12)));
        }
        var color = !enabled ? TextDisabled : hot || pressed ? Text : item.Checked ? CheckedText : DimText;
        var text = Format(item.Text, color, textRect.Width);
        ctx.DrawText(text, new Point(textRect.X, local.Top + (local.Height - text.Height) / 2));

        if (enabled && HasSubItems(item))
        {
            var arrowRect = new Rect(local.Right - ArrowWidth - Scaled(2), local.Top + Math.Floor((local.Height - Scaled(18)) / 2), ArrowWidth, Scaled(18));
            MenuIcon("chevron-right")?.Draw(ctx, arrowRect, hot ? Text : TextNormal);
        }
    }

    // ------------------------------------------------------------------ icone (DarkMenuRenderer.MenuIconKey + AssetIconService)

    private static readonly Dictionary<string, SvgIcon?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> IconAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home-2"] = "home", ["movies"] = "movie", ["film"] = "movie", ["films"] = "movie", ["videos"] = "video",
        ["monitor"] = "tv", ["series"] = "tv", ["tv-series"] = "tv", ["tvseries"] = "tv", ["serie-tv"] = "tv",
        ["photos"] = "photo", ["image"] = "photo", ["favorite"] = "star", ["favorites"] = "star", ["favourite"] = "star",
        ["favourites"] = "star", ["playlists"] = "playlist", ["web"] = "youtube", ["plus"] = "add", ["gear"] = "settings",
        ["translate"] = "language", ["globe"] = "language", ["year"] = "calendar", ["date"] = "calendar", ["clock"] = "time",
        ["duration"] = "time", ["length"] = "time", ["player-play"] = "play", ["prev"] = "previous", ["arrow-back"] = "back",
        ["cc"] = "subtitles", ["loop"] = "repeat", ["x"] = "close", ["chevron"] = "chevron-right", ["fullscreen"] = "maximize",
        ["system"] = "computer", ["cast"] = "person", ["profile"] = "person", ["artist"] = "person",
        // Voci che non avevano icona (Lucide in Icons.Lucide): disco, HDR, nascondi, immagine, upscaling, apri con.
        ["disc"] = "disc-3", ["hdr"] = "sun-medium", ["hide"] = "eye-off", ["nativo"] = "scaling", ["openwith"] = "square-arrow-out-up-right",
    };

    /// <summary>Icons.Custom prima, poi il set uniforme Icons.Uniform (lo stesso ordine di AssetIconService.Resolve).</summary>
    private static SvgIcon? MenuIcon(string key)
    {
        key = Regex.Replace(key.ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
        if (IconAliases.TryGetValue(key, out var canonical)) key = canonical;
        lock (Icons)
        {
            if (Icons.TryGetValue(key, out var cached)) return cached;
            SvgIcon? icon = null;
            foreach (var dir in new[] { "Icons.Custom", "Icons.Uniform", "Icons.Lucide" })
            {
                string rel = dir + "/" + key + ".svg";
                if (File.Exists(Path.Combine(MacTools.AssetsDirectory, rel))) { icon = SvgIcon.Get(rel); if (icon != null) break; }
            }
            return Icons[key] = icon;
        }
    }

    private static string MenuIconKey(MenuEntry item)
    {
        if (!string.IsNullOrWhiteSpace(item.Icon)) return item.Icon!;
        string own = MenuIconKey(item.Text);
        if (own != "dot") return own;
        if (item.Checked) return "check";
        if (item.Parent != null)
        {
            string inherited = MenuIconKey(item.Parent.Text);
            if (inherited != "dot") return inherited;
        }
        return "list";
    }

    private static string MenuIconKey(string text)
    {
        string value = (text ?? "").Replace("&", "").ToLowerInvariant();
        if (value.Contains("pip") || value.Contains("picture-in-picture")) return "pip";
        if (value.Contains("svuota") || value.Contains("clear queue") || value.Contains("rimuovi") || value.Contains("remove")) return "trash";
        if (value.Contains("impostazioni") || value.Contains("settings") || value.Contains("configura") || value.Contains("configure")) return "settings";
        if (value.Contains("sottotit") || value.Contains("subtitle")) return "subtitles";
        if (value.Contains("testi") || value.Contains("lyrics")) return "lyrics";
        if (value.Contains("grafici") || value.Contains("analysis")) return "spectrum";
        if (value.Contains("ferma") || value.Contains("stop") || value.Contains("interrompi")) return "stop";
        if (value.Contains("disattiva") || value.Contains("disable") || value.Contains("nessun") || value.Contains(" off")) return "power";
        if (value.Contains("amplificatore") || value.Contains("amplifier")) return "amplifier";
        if (value.Contains("traccia audio") || value.Contains("audio track")) return "audio";
        if (value.Contains("uscita audio") || value.Contains("audio output")) return "amplifier";
        if (value.Contains("bitstream") || value.Contains("passthrough audio")) return "passthrough";
        if (value.Contains("pcm")) return "wave";
        if (value.Contains("ritardo") || value.Contains("delay") || value.Contains("sync")) return "sync";
        if (value.Contains("velocit") || value.Contains("speed")) return "speed";
        if (value.Contains("bilanciamento") || value.Contains("balance")) return "balance";
        if (value.Contains("volume")) return "volume";
        if (value.Contains("tone-map")) return "sun";
        if (value.Contains("3dlut")) return "palette";
        if (value.Contains("upscal") || value.Contains("rtx")) return "upscale";
        if (value.StartsWith("apri file") || value.StartsWith("open file")) return "folder";
        if (value.StartsWith("apri libreria") || value.StartsWith("open library") || value == "libreria" || value == "library") return "library";
        if (value.Contains("spotlight")) return "spotlight";
        if (value.Contains("riprendi") || value.Contains("resume")) return "play";
        if (value.Contains("pausa") || value.Contains("pause")) return "pause";
        if (value.Contains("chiudi file") || value.Contains("close file")) return "close";
        if (value.Contains("loop") || value.Contains("ripeti") || value.Contains("repeat")) return "repeat";
        if (value.Contains("coda") || value.Contains("queue")) return "queue";
        if (value == "video") return "video";
        if (value.Contains("audio")) return "audio";
        if (value.Contains("capitol") || value.Contains("chapter")) return "chapters";
        if (value.Contains("immagine") || value.Contains("picture") || value.Contains("hdr")) return "image";
        if (value == "3d" || value.Contains("sbs") || value.Contains("tab →")) return "cube";
        if (value.Contains("youtube") || value.Contains("risoluzione web") || value.Contains("web resolution")) return "youtube";
        if (value.Contains("limita a") || value.Contains("limit to") || Regex.IsMatch(value, @"\b(8k|4k|4320p|2160p|1440p|1080p|720p|480p|360p|240p|144p)\b")) return "resolution";
        if (value.Contains("renderer") || value.Contains("madvr") || value.Contains("mpcvr") || value.Contains("mpv")) return "renderer";
        if (value.Contains("predefinit") || value.Contains("default") || value == "auto" || value.StartsWith("auto (")) return "check";
        if (value.Contains("passthrough") || value.Contains("tone-map") || value.Contains("3dlut")) return "image";
        if (value.Contains("nativo") || value.Contains("native") || value.Contains("sbs") || value.Contains("tab")) return "cube";
        if (value.Contains("lingua") || value.Contains("language")) return "language";
        if (value.Contains("wled") || value.Contains("led")) return "light";
        if (value.Contains("placeholder")) return "image";
        if (value.Contains("demo")) return "play";
        if (value.StartsWith("apri cartella") || value.StartsWith("open folder")) return "folder";
        if (value.StartsWith("abilita") || value.StartsWith("enable")) return "check";
        if (value.Contains("schermo intero") || value.Contains("fullscreen")) return "maximize";
        if (value.Contains("cinema")) return "cinema";
        if (value.Contains("info")) return "info";
        if (value.Contains("extra")) return "plus";
        if (value.Contains("telecomando") || value.Contains("remote")) return "remote";
        if (value.Contains("esci") || value.Contains("exit")) return "exit";
        return "dot";
    }
}
