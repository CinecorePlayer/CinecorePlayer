using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Cambio di pagina come CinematicMediaLibraryPage.Transitions su Windows: la pagina vecchia resta ferma a schermo
/// finche' la nuova non e' pronta (dati caricati, copertine decodificate; al massimo 200 ms, 650 se mancano solo
/// immagini), poi sfuma in 170 ms. Senza l'attesa sotto la dissolvenza si vedeva la pagina nuova costruirsi:
/// segnaposto, "Caricamento…" e locandine che comparivano di colpo.
/// La cattura non contiene barra in alto, menu e schede (il proprietario li salta quando <see cref="Capturing"/>):
/// prima il menu da cui si era scelta la pagina restava nella foto e lampeggiava mentre quello vero si chiudeva.
/// </summary>
internal sealed class SurfaceCrossfade
{
    private const double FadeMs = 170, HoldMaxMs = 200, HoldImagesMs = 650, HoldLimitMs = 1100;
    private readonly Control _owner;
    private readonly DispatcherTimer _frames;
    private readonly Stopwatch _held = new(), _sinceFirstPaint = new(), _fade = new();
    private RenderTargetBitmap? _from;
    private Size _size;
    private bool _fading;

    /// <summary>Vero mentre si fotografa la pagina: il proprietario non disegna barra, menu e schede.</summary>
    public bool Capturing { get; private set; }
    public bool Active => _from != null;
    /// <summary>Quanto si vede della pagina nuova (0 in attesa, 1 a riposo): per i campi di testo, che stanno sopra.</summary>
    public double Reveal => _from == null ? 1 : !_fading ? 0 : 1 - Opacity;

    private double Opacity => !_fading ? 1 : Math.Pow(1 - Math.Clamp(_fade.Elapsed.TotalMilliseconds / FadeMs, 0, 1), 2);

    public SurfaceCrossfade(Control owner)
    {
        _owner = owner;
        _frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (!owner.IsVisible || (_fading && _fade.Elapsed.TotalMilliseconds >= FadeMs) || (!_fading && _held.Elapsed.TotalMilliseconds >= HoldLimitMs)) Clear();
            owner.InvalidateVisual();
        });
        // Il costruttore con il gestore avvia gia' il timer: prima ridisegnava la libreria a 60 fps dall'avvio fino al
        // primo cambio di pagina (anche a riposo sulla Home).
        _frames.Stop();
        owner.DetachedFromVisualTree += (_, _) => Clear();
    }

    /// <summary>Da chiamare prima di cambiare pagina: fissa cio' che si vede ora (anche a meta' di un'altra dissolvenza).</summary>
    public void Begin()
    {
        if (Capturing || !_owner.IsVisible || _owner.Bounds.Width < 10 || _owner.Bounds.Height < 10) return;
        RenderTargetBitmap? snapshot = null;
        try
        {
            Capturing = true;
            double scale = Math.Min(2, TopLevel.GetTopLevel(_owner)?.RenderScaling ?? 1);
            snapshot = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_owner.Bounds.Width * scale), (int)Math.Ceiling(_owner.Bounds.Height * scale)), new Vector(96, 96));
            // Non RenderTargetBitmap.Render: su questo Avalonia applica i DPI della foto due volte e la pagina finiva
            // ingrandita 4x (il lampo con il titolo gigante). Le viste si disegnano tutte nel proprio Render: lo si chiama qui.
            using (var dc = snapshot.CreateDrawingContext())
            using (dc.PushTransform(Matrix.CreateScale(scale, scale)))
                _owner.Render(dc);
            if (Environment.GetEnvironmentVariable("CINECORE_QA_SNAPSHOT") is { Length: > 0 } qaPath) snapshot.Save(qaPath);
            Clear();
            _from = snapshot; snapshot = null;
            _size = _owner.Bounds.Size;
            _fading = false;
            _held.Restart(); _sinceFirstPaint.Reset();
            _frames.Start();
        }
        catch { snapshot?.Dispose(); Clear(); }
        finally { Capturing = false; }
    }

    /// <param name="ready">La pagina nuova ha dati e immagini: la dissolvenza puo' partire.</param>
    /// <param name="onlyImages">Dati pronti, mancano solo copertine in decodifica: si aspetta un po' di piu'.</param>
    public void Draw(DrawingContext ctx, Rect area, bool ready = true, bool onlyImages = false)
    {
        if (_from == null) return;
        if (_size != _owner.Bounds.Size) { Dispatcher.UIThread.Post(Clear); return; }
        if (!Capturing && !_fading)
        {
            if (!_sinceFirstPaint.IsRunning) _sinceFirstPaint.Start();
            if (ready || _sinceFirstPaint.Elapsed.TotalMilliseconds >= (onlyImages ? HoldImagesMs : HoldMaxMs)) { _fading = true; _fade.Restart(); }
        }
        double alpha = Opacity;
        if (alpha <= 0.002) return;
        using var clip = ctx.PushClip(area);
        using var fade = ctx.PushOpacity(alpha);
        ctx.DrawImage(_from, new Rect(_from.Size), new Rect(_size));
    }

    private void Clear()
    {
        _frames.Stop(); _from?.Dispose(); _from = null; _fading = false;
    }
}
