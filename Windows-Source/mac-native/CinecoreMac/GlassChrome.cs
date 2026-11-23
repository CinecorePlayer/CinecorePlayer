using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CinecoreMac;

/// <summary>
/// Capsule di controlli Avalonia su vetro vero di macOS (MacSurfaceGlass: Liquid Glass da macOS 26). Le capsule restano
/// nella finestra quasi invisibili (1%: con opacita' 0 Avalonia non manda piu' i clic) e prendono clic, passaggi e
/// suggerimenti; il loro aspetto si ridisegna come immagine sopra il pannello di vetro corrispondente. L'immagine nasce
/// da un'unica resa della vista (senza le parti indicate, come la foto), ritagliata capsula per capsula: rendere le
/// capsule una per una spostava e ritagliava male testi e pulsanti. Senza vetro nativo le capsule restano scure.
/// </summary>
internal sealed class GlassChrome
{
    private sealed class Part
    {
        public required Border Host;
        public double Radius;
        public int Version, Drawn = -1, Slot = -1;
        public required Func<bool> Shown;
        public bool Chrome;
        public string Pointer = "";
    }

    private const double HiddenOpacity = 0.01;
    private static readonly bool Prof = Environment.GetEnvironmentVariable("CINECORE_GLASS_PROF") == "1";
    private readonly Panel _owner;
    private readonly Func<IEnumerable<Visual>> _hiddenInShot;
    private readonly List<Part> _parts = new();
    private readonly DispatcherTimer _timer;
    private MacSurfaceGlass? _glass;
    private string _shownSet = "";
    private long _lastFull;

    /// <summary>Opacita' della cornice (le parti "Chrome"), animata verso Target.</summary>
    public double Alpha { get; private set; } = 1;
    public double Target { get; set; } = 1;
    /// <summary>Un menu di testo dell'app e' aperto: il vetro (sopra la finestra) lo coprirebbe, si torna alle capsule scure.</summary>
    public bool MenuOpen { get; set; }
    public bool On { get; private set; }
    public event Action<double>? AlphaChanged;

    public GlassChrome(Panel owner, Func<IEnumerable<Visual>> hiddenInShot)
    {
        _owner = owner;
        _hiddenInShot = hiddenInShot;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Stop();
    }

    /// <summary>Una capsula: su vetro e' trasparente, altrimenti scura.</summary>
    public static Border Capsule(Control child, double radius, Thickness padding) => new GlassCapsule
    {
        Child = child, CornerRadius = new CornerRadius(radius), Padding = padding,
        Background = PhotoUi.CapsuleFill, BorderBrush = PhotoUi.CapsuleEdge, BorderThickness = new Thickness(1),
    };

    public void Add(Border host, double radius, Func<bool>? shown = null, bool chrome = true)
    {
        var part = new Part { Host = host, Radius = radius, Shown = shown ?? (() => host.IsVisible), Chrome = chrome };
        _parts.Add(part);
        host.SizeChanged += (_, _) => part.Version++;
    }

    public IEnumerable<Border> Hosts => _parts.Select(p => p.Host);
    public bool PointerOver => _parts.Any(p => p.Host.IsPointerOver);

    /// <summary>Contenuto cambiato: si ridisegna subito e dopo l'impaginazione (testi che cambiano misura).</summary>
    public void Dirty(Border host)
    {
        foreach (var p in _parts) if (ReferenceEquals(p.Host, host)) p.Version++;
        Dispatcher.UIThread.Post(() => { foreach (var p in _parts) if (ReferenceEquals(p.Host, host)) p.Version++; }, DispatcherPriority.Background);
    }

    public void DirtyAll() { foreach (var p in _parts) Dirty(p.Host); }

    public void Start()
    {
        if (!MacSurfaceGlass.Supported || TopLevel.GetTopLevel(_owner) is not { } top) { SetLook(false); return; }
        _glass ??= new MacSurfaceGlass(top);
        _timer.Start();
        Tick();
    }

    public void Stop()
    {
        _timer.Stop();
        _glass?.Hide();
        _shownSet = "";
        SetLook(false);
    }

    private void SetLook(bool on)
    {
        if (On == on) return;
        On = on;
        foreach (var p in _parts)
        {
            p.Host.Opacity = on ? HiddenOpacity : p.Chrome ? Alpha : 1;
            p.Host.Background = on ? null : PhotoUi.CapsuleFill;
            p.Host.BorderBrush = on ? null : PhotoUi.CapsuleEdge;
            p.Drawn = -1;
        }
    }

    private static string PointerState(Border host)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in host.GetVisualDescendants().OfType<InputElement>())
            if (c is Button || c is Slider || c.Cursor != null)
                sb.Append(c is Button { IsPressed: true } ? '2' : c.IsPointerOver ? '1' : '0');
        return sb.ToString();
    }

    private void Tick()
    {
        if (!_owner.IsVisible || _glass == null || TopLevel.GetTopLevel(_owner) is not { } top) return;
        // Durante la dissolvenza d'apertura la vista e' semitrasparente: le immagini dei comandi prese adesso
        // restavano sbiadite sul vetro ("parti spente") finche' qualcosa non cambiava. Si aspetta l'opacita' piena.
        double opacity = 1;
        for (Visual? v = _owner; v != null; v = v.GetVisualParent()) opacity *= v.Opacity;
        if (opacity < 0.995) { _glass.Hide(); _shownSet = ""; foreach (var p in _parts) p.Drawn = -1; return; }
        double before = Alpha;
        Alpha += (Target - Alpha) * 0.28;
        if (Math.Abs(Target - Alpha) < 0.02) Alpha = Target;
        if (Math.Abs(before - Alpha) > 0.0001) AlphaChanged?.Invoke(Alpha);
        if (MenuOpen) { _glass.Hide(); _shownSet = ""; SetLook(false); FallbackAlpha(); return; }
        double scaling = top.RenderScaling;
        var panels = new List<(Rect Bounds, double Radius)>();
        var shown = new List<Part>();
        foreach (var p in _parts)
        {
            if (!p.Shown() || !p.Host.IsEffectivelyVisible || p.Host.Bounds.Width < 2) continue;
            if (p.Chrome && Alpha < 0.02) continue;
            if (p.Host.TranslatePoint(default, top) is not { } origin) continue;
            panels.Add((new Rect(origin, p.Host.Bounds.Size), p.Radius));
            shown.Add(p);
        }
        // Il vetro svuotato (cornice nascosta) o pannelli diversi: al ritorno si ridisegna tutto.
        string set = string.Join(",", shown.Select(p => _parts.IndexOf(p)));
        if (set != _shownSet) { _shownSet = set; foreach (var p in _parts) p.Drawn = -1; }
        bool anySolid = shown.Any(p => !p.Chrome);
        bool ok = _glass.Show(panels, dark: true, alpha: panels.Count == 0 ? 0 : Math.Max(Alpha, anySolid ? 1 : 0));
        SetLook(ok);
        if (!ok) { FallbackAlpha(); return; }
        foreach (var p in shown)
        {
            string state = PointerState(p.Host);
            if (state != p.Pointer) { p.Pointer = state; p.Version++; }
        }
        // Rete di sicurezza: ogni secondo tutto si ridisegna comunque (un cambiamento non segnalato lasciava una capsula
        // con l'immagine vecchia finche' non ci si passava sopra).
        if (Environment.TickCount64 - _lastFull > 1000) { _lastFull = Environment.TickCount64; foreach (var p in shown) p.Version++; }
        bool dirty = false;
        for (int i = 0; i < shown.Count; i++) dirty |= shown[i].Drawn != shown[i].Version || shown[i].Slot != i;
        if (!dirty) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using var shot = Render(scaling);
        double renderMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        for (int i = 0; i < shown.Count; i++)
        {
            var p = shown[i];
            if (p.Drawn == p.Version && p.Slot == i) continue;
            p.Drawn = p.Version; p.Slot = i;
            if (p.Host.TranslatePoint(default, _owner) is not { } at) continue;
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(p.Host.Bounds.Width * scaling)), Math.Max(1, (int)Math.Ceiling(p.Host.Bounds.Height * scaling)));
            var source = new Rect(at.X * scaling, at.Y * scaling, pixels.Width, pixels.Height);
            _glass.SetForeground(i, p.GetHashCode() + "|" + p.Version + "|" + pixels, pixels, d => d.Context.DrawImage(shot, source, new Rect(0, 0, pixels.Width, pixels.Height)));
        }
        if (Prof) Console.Error.WriteLine($"[vetro] resa {renderMs:0.0} ms, totale {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0} ms");
    }

    private void FallbackAlpha()
    {
        foreach (var p in _parts) if (p.Chrome) p.Host.Opacity = p.Shown() ? Alpha : 0;
    }

    private RenderTargetBitmap Render(double scaling)
    {
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(_owner.Bounds.Width * scaling)), Math.Max(1, (int)Math.Ceiling(_owner.Bounds.Height * scaling)));
        var shot = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        var background = _owner.Background;
        var hidden = _hiddenInShot().ToList();
        var hiddenOpacity = hidden.Select(v => v.Opacity).ToList();
        var opacities = _parts.Select(p => p.Host.Opacity).ToList();
        _owner.Background = null;
        foreach (var v in hidden) v.Opacity = 0;
        foreach (var p in _parts) p.Host.Opacity = 1;
        try { shot.Render(_owner); }
        finally
        {
            _owner.Background = background;
            for (int i = 0; i < hidden.Count; i++) hidden[i].Opacity = hiddenOpacity[i];
            for (int i = 0; i < _parts.Count; i++) _parts[i].Host.Opacity = opacities[i];
        }
        return shot;
    }
}

/// <summary>
/// [2026-11-22] La capsula delle foto (visore, editor). Sul Mac il vetro vero sta nella finestra figlia (GlassChrome); su
/// Windows era una lastra scura piena: ora, finche' ha il fondo scuro delle capsule, disegna il vetro dal vivo
/// (BackdropGlassOp.Liquid) con la foto sotto sfocata. L'opacita' dei genitori si passa a mano: PushOpacity non vale
/// per le operazioni Custom.
/// </summary>
internal sealed class GlassCapsule : Border, Avalonia.Rendering.ICustomHitTest
{
    public bool HitTest(Point point) => Background != null && new Rect(Bounds.Size).Contains(point);

#if CINECORE_WINDOWS
    // Border.Render e' sigillato: il vetro sta in un visuale figlio messo per primo, sotto il contenuto. Il fondo scuro
    // delle capsule (PhotoUi.CapsuleFill, anche quando GlassChrome lo rimette) diventa trasparente e accende il vetro.
    private readonly Back _back;
    public GlassCapsule()
    {
        _back = new Back(this);
        VisualChildren.Insert(0, _back);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BackgroundProperty && ReferenceEquals(change.NewValue, PhotoUi.CapsuleFill))
        { _back.On = true; Background = Brushes.Transparent; _back.InvalidateVisual(); }
        else if (change.Property == BackgroundProperty && change.NewValue == null) { _back.On = false; _back.InvalidateVisual(); }
        else if (change.Property == BorderBrushProperty && change.NewValue is IBrush edge && !ReferenceEquals(edge, Brushes.Transparent))
        { _back.Edge = edge; BorderBrush = Brushes.Transparent; }
        else if (change.Property == OpacityProperty || change.Property == CornerRadiusProperty) _back.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) { _back.Measure(availableSize); return base.MeasureOverride(availableSize); }
    protected override Size ArrangeOverride(Size finalSize) { var size = base.ArrangeOverride(finalSize); _back.Arrange(new Rect(finalSize)); return size; }

    private sealed class Back : Control
    {
        private readonly GlassCapsule _owner;
        public bool On;
        public IBrush? Edge;
        public Back(GlassCapsule owner) { _owner = owner; IsHitTestVisible = false; }

        // Il vetro riceve l'opacita' a mano: quando un antenato sfuma (il visore entra da 0, i comandi si nascondono)
        // va ridisegnato, altrimenti resta l'opacita' del primo disegno (0: capsule invisibili).
        private readonly List<Visual> _watched = new();
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            for (Visual? v = this.GetVisualParent(); v != null && v is not TopLevel; v = v.GetVisualParent())
            { v.PropertyChanged += AncestorChanged; _watched.Add(v); }
        }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            foreach (var v in _watched) v.PropertyChanged -= AncestorChanged;
            _watched.Clear();
        }
        private void AncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == OpacityProperty) InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (!On) return;
            double opacity = 1;
            for (Visual? v = this; v != null && v is not TopLevel; v = v.GetVisualParent()) opacity *= v.Opacity;
            if (opacity <= 0.01) return;
            var rect = new Rect(Bounds.Size);
            double radius = _owner.CornerRadius.TopLeft;
            context.Custom(BackdropGlassOp.Liquid(rect, radius, opacity, 0));
            if (Edge != null) context.DrawRectangle(null, new Pen(Edge, 1), rect.Deflate(.5), radius, radius);
        }
    }
#endif
}
