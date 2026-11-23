using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Entrata e uscita delle schede (Impostazioni, Metadati, Componenti, Trakt...) e dei livelli grandi (libreria,
/// Spotlight, editor delle foto, PiP). Prima comparivano e sparivano di colpo (IsVisible).
/// Entrando la scheda sale in opacita' e, se puo', si allarga dal 98% (transizioni di Avalonia, sul thread di
/// composizione); uscendo se ne disegna l'ultima immagine in questo livello e la si fa sfumare rimpicciolendo appena,
/// mentre sotto si vede gia' la pagina di prima. Le schede che si scalano da sole (WizardSheet.Fit) e i livelli con
/// caselle di testo a parte entrano solo in dissolvenza (2026-11-21: prima solo dissolvenza per tutte).
/// </summary>
internal sealed class LayerFades : Control
{
    private const double ExitMs = 190, ExitScale = .985, EnterScale = .98;
    private static readonly TimeSpan Enter = TimeSpan.FromMilliseconds(240);
    private static readonly TransformOperations Small = TransformOperations.Parse($"scale({EnterScale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    private static readonly TransformOperations Full = TransformOperations.Parse("scale(1)");
    private readonly List<(RenderTargetBitmap Image, Rect At, DateTime Start, bool Zoom)> _ghosts = new();
    private readonly Dictionary<Control, Control> _companions = new();

    public LayerFades() { IsHitTestVisible = false; }

    /// <summary>Le schede indicate entrano ed escono in dissolvenza, con un leggero ingrandimento.</summary>
    public void Watch(params Control[] layers) => Watch(true, layers);

    /// <summary>Solo dissolvenza (livelli grandi, o che hanno gia' una loro scala).</summary>
    public void WatchFadeOnly(params Control[] layers) => Watch(false, layers);

    /// <summary>
    /// Solo l'entrata in dissolvenza. Per la libreria: quando parte un film l'avvio del renderer blocca l'interfaccia per
    /// oltre un secondo e la sua ultima immagine restava mezza trasparente sopra il caricamento.
    /// </summary>
    public void WatchEnterOnly(params Control[] layers) => Watch(false, layers, exit: false);

    /// <summary>
    /// <paramref name="companion"/> (le caselle di testo della scheda, in un livello a parte) segue l'ingrandimento
    /// d'entrata di <paramref name="sheet"/>: senza, per un attimo non starebbero sopra i loro riquadri.
    /// </summary>
    public void Follow(Control sheet, Control companion)
    {
        _companions[sheet] = companion;
        companion.RenderTransformOrigin = RelativePoint.Center;
        companion.Transitions ??= new Transitions();
        companion.Transitions.Add(new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Enter, Easing = new CubicEaseOut() });
    }

    private void Watch(bool zoom, Control[] layers, bool exit = true)
    {
        foreach (var layer in layers)
        {
            var target = layer;
            target.Transitions ??= new Transitions();
            target.Transitions.Add(new DoubleTransition { Property = OpacityProperty, Duration = Enter, Easing = new CubicEaseOut() });
            if (zoom)
            {
                target.RenderTransformOrigin = RelativePoint.Center;
                target.Transitions.Add(new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Enter, Easing = new CubicEaseOut() });
            }
            target.PropertyChanged += (_, e) =>
            {
                if (e.Property != IsVisibleProperty) return;
                if (target.IsVisible)
                {
                    _companions.TryGetValue(target, out var companion);
                    // Il punto di partenza va impostato senza transizione, poi si va a 1 con quella di Avalonia.
                    var saved = target.Transitions;
                    target.Transitions = null;
                    target.Opacity = 0;
                    if (zoom) target.RenderTransform = Small;
                    target.Transitions = saved;
                    if (zoom && companion != null)
                    {
                        var savedCompanion = companion.Transitions;
                        companion.Transitions = null;
                        companion.RenderTransform = Small;
                        companion.Transitions = savedCompanion;
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!target.IsVisible) return;
                        target.Opacity = 1;
                        if (zoom) { target.RenderTransform = Full; if (companion != null) companion.RenderTransform = Full; }
                    }, DispatcherPriority.Render);
                }
                else
                {
                    if (exit) Capture(target, zoom);
                    // Alla prossima apertura si riparte da fermi, non da meta' dell'animazione.
                    if (zoom)
                    {
                        var saved = target.Transitions;
                        target.Transitions = null;
                        target.RenderTransform = null;
                        target.Transitions = saved;
                        if (_companions.TryGetValue(target, out var companion))
                        {
                            var savedCompanion = companion.Transitions;
                            companion.Transitions = null;
                            companion.RenderTransform = null;
                            companion.Transitions = savedCompanion;
                        }
                    }
                }
            };
        }
    }

    private void Capture(Control layer, bool zoom)
    {
        try
        {
            var size = layer.Bounds.Size;
            if (size.Width < 4 || size.Height < 4 || !IsEffectivelyVisible) return;
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var at = layer.TranslatePoint(default, this) ?? default;
            var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling)), new Vector(96, 96));
            // Non RenderTargetBitmap.Render: applica i DPI due volte (vedi SurfaceCrossfade).
            using (var dc = image.CreateDrawingContext())
            using (dc.PushTransform(Matrix.CreateScale(scaling, scaling)))
            using (dc.PushOpacity(Math.Clamp(layer.Opacity, 0, 1)))
                layer.Render(dc);
            _ghosts.Add((image, new Rect(at, size), DateTime.UtcNow, zoom));
            InvalidateVisual();
        }
        catch (Exception ex) { Console.Error.WriteLine("[dissolvenza] " + ex.Message); }
    }

    public override void Render(DrawingContext ctx)
    {
        if (_ghosts.Count == 0) return;
        var now = DateTime.UtcNow;
        for (int i = _ghosts.Count - 1; i >= 0; i--)
        {
            var (image, at, start, zoom) = _ghosts[i];
            double q = (now - start).TotalMilliseconds / ExitMs;
            if (q >= 1) { _ghosts.RemoveAt(i); image.Dispose(); continue; }
            // Uscita: accelera verso la fine (ease-in), cosi' i primi fotogrammi restano leggibili.
            double eased = q * q;
            double alpha = 1 - eased;
            var dest = at;
            if (zoom)
            {
                double k = 1 - (1 - ExitScale) * eased;
                dest = new Rect(at.Center.X - at.Width * k / 2, at.Center.Y - at.Height * k / 2, at.Width * k, at.Height * k);
            }
            // Un solo disegno (l'immagine) per ombra: l'opacita' non fa trasparire pezzi sovrapposti.
            using (ctx.PushOpacity(alpha))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(image, new Rect(image.Size), dest);
        }
        if (_ghosts.Count > 0) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }
}
