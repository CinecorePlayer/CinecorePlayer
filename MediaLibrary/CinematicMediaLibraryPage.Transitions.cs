using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CinecorePlayer2025;

internal sealed partial class CinematicMediaLibraryPage
{
    private sealed class LibraryDialogForm : Form
    {
        private readonly Timer _entrance = new() { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
        private long _started;
        public LibraryDialogForm()
        {
            DoubleBuffered = true;
            if (SystemInformation.IsMenuAnimationEnabled) Opacity = .01;
            _entrance.Tick += (_, _) =>
            {
                Opacity = Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / 150, .01, 1);
                if (Opacity >= 1) _entrance.Stop();
            };
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _started = Stopwatch.GetTimestamp();
            if (Opacity < 1) _entrance.Start();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _entrance.Dispose();
            base.Dispose(disposing);
        }
    }

    private Timer? _surfaceEntranceTimer;
    private float _surfaceEntrance = 1;
    private Timer? _panelEntranceTimer;

    private void BeginSurfaceEntrance()
    {
        _surfaceEntranceTimer?.Dispose();
        if (!SystemInformation.IsMenuAnimationEnabled) { _surfaceEntrance = 1; return; }
        _surfaceEntrance = 0;
        long started = Stopwatch.GetTimestamp();
        _surfaceEntranceTimer = new Timer { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
        _surfaceEntranceTimer.Tick += (_, _) =>
        {
            _surfaceEntrance = Math.Clamp((float)(Stopwatch.GetElapsedTime(started).TotalMilliseconds / 170), 0, 1);
            if (_surfaceEntrance >= 1 || !Visible) _surfaceEntranceTimer.Stop();
            Invalidate();
        };
        _surfaceEntranceTimer.Start();
    }

    private void DrawSurfaceEntrance(Graphics g)
    {
        if (_surfaceEntrance >= 1 || (_detailItem == null && _activeGroup == null)) return;
        using var veil = new SolidBrush(Color.FromArgb((int)(100 * Math.Pow(1 - _surfaceEntrance, 2)), Back));
        g.FillRectangle(veil, ClientRectangle);
    }

    // ===== Cambio di pagina =====
    // Passando da una pagina all'altra (Home, categorie, Rete) la pagina nuova si vedeva
    // costruirsi: prima il fondo, poi il corpo, poi le locandine, e per un attimo la schermata
    // di caricamento. Ora la pagina vecchia resta a schermo finche' la nuova non e' pronta
    // (al massimo qualche decimo di secondo), poi sfuma su quella nuova. La barra laterale
    // non partecipa: la voce scelta si accende subito.
    private const double PageFadeMs = 170, PageHoldMaxMs = 450, PageHoldLimitMs = 1200;
    private CinecorePlayer2025.Utilities.GdiLayer? _pageFadeFrom, _pageFadeTo;
    private readonly System.Collections.Generic.List<HitZone> _pageFadeHits = new();
    private Rectangle _pageFadeArea, _pageFadeViewport, _lastMainArea;
    private long _pageFadeHeldSince, _pageFadeFirstPaint, _pageFadeStarted;
    private Timer? _pageFadeTimer;
    private bool _pageFadeBuilding;

    private void DropPageCrossfade()
    {
        _pageFadeTimer?.Stop();
        _pageFadeFrom?.Dispose(); _pageFadeFrom = null;
        _pageFadeTo?.Dispose(); _pageFadeTo = null;
        _pageFadeStarted = 0; _pageFadeFirstPaint = 0;
    }

    /// <summary>Da chiamare prima di cambiare pagina (o di sostituire la schermata di caricamento con il
    /// contenuto): fissa cio' che c'e' ora a schermo, che poi sfumera' sulla pagina nuova.</summary>
    private void BeginPageCrossfade()
    {
        // Il disegno della pagina qui sotto puo' a sua volta chiudere un caricamento e richiamare
        // questo metodo: senza la guardia il livello in costruzione veniva liberato mentre era in uso.
        if (_pageFadeBuilding || InvokeRequired) return;
        if (!SystemInformation.IsMenuAnimationEnabled || !Visible || !IsHandleCreated || Width < 2 || Height < 2 ||
            _detailItem != null || _activeGroup != null || _queueEditorVisible || _musicWorkspaceContent != null || _lastMainArea.Width < 2 || _lastMainArea.Height < 2)
        {
            DropPageCrossfade();
            return;
        }
        CinecorePlayer2025.Utilities.GdiLayer? layer = null;
        _pageFadeBuilding = true;
        try
        {
            layer = new CinecorePlayer2025.Utilities.GdiLayer(this, ClientSize);
            if (_pageFadeFrom != null && _pageFadeFrom.Size == ClientSize)
            {
                // Una dissolvenza e' gia' in corso (o la pagina vecchia e' ancora ferma a schermo): si
                // riparte esattamente da cio' che si vede ora, senza salti.
                double t = _pageFadeStarted == 0 || _pageFadeTo == null ? 0 : Math.Clamp(Stopwatch.GetElapsedTime(_pageFadeStarted).TotalMilliseconds / PageFadeMs, 0, 1);
                if (_pageFadeTo != null) _pageFadeTo.DrawTo(layer.Graphics, ClientRectangle, Point.Empty);
                else _pageFadeFrom.DrawTo(layer.Graphics, ClientRectangle, Point.Empty);
                _pageFadeFrom.DrawTo(layer.Graphics, _pageFadeArea, _pageFadeArea.Location, Math.Pow(1 - t, 2));
                DropPageCrossfade();
            }
            else
            {
                DropPageCrossfade();
                Rectangle clip = _paintClip;
                _paintClip = Rectangle.Empty;
                // La pagina com'e' adesso (nessun livello e' attivo mentre la si dipinge).
                try { PaintLibrary(layer.Graphics); }
                finally { _paintClip = clip; }
            }
            _pageFadeFrom = layer;
            layer = null;
            _pageFadeArea = Rectangle.Intersect(_lastMainArea, ClientRectangle);
            _pageFadeHeldSince = Stopwatch.GetTimestamp();
            if (_pageFadeTimer == null)
            {
                _pageFadeTimer = new Timer { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
                _pageFadeTimer.Tick += (_, _) => { if (_pageFadeFrom == null || !Visible) DropPageCrossfade(); Invalidate(); };
            }
            _pageFadeTimer.Start();
        }
        catch { DropPageCrossfade(); }
        finally { layer?.Dispose(); _pageFadeBuilding = false; }
    }

    /// <summary>Durante la dissolvenza la pagina nuova e' gia' in un livello: basta comporre. True se ha dipinto.</summary>
    private bool TryPaintPageCrossfadeFrame(Graphics g)
    {
        if (_pageFadeFrom == null || _pageFadeTo == null || _pageFadeStarted == 0) return false;
        if (_pageFadeFrom.Size != ClientSize || _detailItem != null || _activeGroup != null || _queueEditorVisible) { DropPageCrossfade(); return false; }
        double t = Stopwatch.GetElapsedTime(_pageFadeStarted).TotalMilliseconds / PageFadeMs;
        if (t >= 1) { DropPageCrossfade(); return false; }
        _hits.AddRange(_pageFadeHits);
        _gridViewport = _pageFadeViewport;
        _pageFadeTo.DrawTo(g, ClientRectangle, Point.Empty);
        _pageFadeFrom.DrawTo(g, _pageFadeArea, _pageFadeArea.Location, Math.Pow(1 - t, 2));
        return true;
    }

    /// <summary>A fine disegno della pagina nuova: finche' non e' pronta la copre la vecchia; quando lo e',
    /// la pagina appena dipinta viene fissata e parte la dissolvenza.</summary>
    private void HoldOrStartPageCrossfade(Graphics g, bool wholePage)
    {
        if (_pageFadeFrom == null || _pageFadeHeldSince == 0 || _pageFadeStarted != 0) return;
        // Con una scheda aperta (album, dettagli, coda) niente dissolvenza di pagina: la pagina vecchia tenuta in
        // attesa veniva ridisegnata SOPRA la scheda a ogni ridisegno parziale, e al clic su Riproduci compariva
        // sul tasto un riquadro con un pezzo della pagina sotto (copertina e titolo di un album).
        if (_pageFadeFrom.Size != ClientSize || _musicWorkspaceContent != null || _detailItem != null || _activeGroup != null || _queueEditorVisible) { DropPageCrossfade(); return; }
        // Mai una pagina vecchia ferma a schermo: se per qualunque motivo la dissolvenza non riesce a
        // partire (per esempio arrivano solo ridisegni parziali), dopo poco si mostra la pagina nuova e basta.
        if (Stopwatch.GetElapsedTime(_pageFadeHeldSince).TotalMilliseconds >= PageHoldLimitMs) { DropPageCrossfade(); return; }
        // Pronta = contenuto caricato e nessuna copertina ancora in decodifica: altrimenti la
        // dissolvenza finiva sui segnaposto e le immagini comparivano subito dopo, di colpo.
        // L'attesa si conta dal primo disegno della pagina nuova (che puo' essere lento di suo).
        if (_pageFadeFirstPaint == 0) _pageFadeFirstPaint = Stopwatch.GetTimestamp();
        bool imagesPending;
        lock (_imageCacheSync) imagesPending = _imageLoadRequests.Count > 0;
        imagesPending |= System.Threading.Volatile.Read(ref _imageUiRefreshQueued) != 0;
        bool ready = !_contentLoading && !_networkContentLoading && !imagesPending;
        bool waitedEnough = Stopwatch.GetElapsedTime(_pageFadeFirstPaint).TotalMilliseconds >= PageHoldMaxMs;
        if (wholePage && (ready || waitedEnough))
        {
            try
            {
                _pageFadeTo = new CinecorePlayer2025.Utilities.GdiLayer(this, ClientSize);
                _pageFadeTo.CopyFrom(g, ClientRectangle);
                _pageFadeHits.Clear();
                _pageFadeHits.AddRange(_hits);
                _pageFadeViewport = _gridViewport;
                _pageFadeStarted = Stopwatch.GetTimestamp();
            }
            catch { DropPageCrossfade(); return; }
        }
        _pageFadeFrom.DrawTo(g, _pageFadeArea, _pageFadeArea.Location);
    }

    // ===== Schede in pagina (dettagli film, scheda album/serie) =====
    // Entrata: la pagina si oscura gradualmente e il pannello sale di pochi pixel in
    // dissolvenza; uscita: lo stesso al contrario (prima sparivano di colpo). Il pannello si
    // dipinge una volta in un livello opaco (ClearType corretto) e durante l'animazione si
    // compone soltanto. Anche la pagina dietro si dipinge una volta sola per animazione.
    // I livelli sono contesti GDI (GdiLayer), non Bitmap di GDI+: su una Bitmap ogni scritta
    // copiava l'intera immagine e il primo fotogramma della scheda costava 200-450 ms, piu'
    // dell'animazione stessa (che quindi non si vedeva: la scheda compariva di scatto).
    private const double SheetEntranceMs = 220, SheetExitMs = 170;
    private Timer? _sheetTimer;
    private long _sheetStart, _sheetExitStart;
    private float _sheetEntrance = 1;
    private CinecorePlayer2025.Utilities.GdiLayer? _sheetLayer, _sheetExitLayer, _sheetPage;
    private readonly System.Collections.Generic.List<HitZone> _sheetPageHits = new();
    private Rectangle _sheetPageViewport;
    private long _sheetPageTick;
    private bool SheetAnimating => (_sheetEntrance < 1 && (_detailItem != null || _activeGroup != null)) || _sheetExitLayer != null;

    private void DropSheetPage()
    {
        _sheetPage?.Dispose();
        _sheetPage = null;
    }
    private Rectangle _sheetExitPanel;
    private readonly System.Collections.Generic.List<HitZone> _sheetLayerHits = new();

    /// <summary>Area della pagina a disposizione delle schede (senza la barra musica).</summary>
    private Rectangle SheetBounds(Rectangle page)
    {
        page.Height = Math.Max(1, page.Height - MusicTransportInset);
        return page;
    }

    private void StartSheetTimer()
    {
        if (_sheetTimer == null)
        {
            _sheetTimer = new Timer { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
            _sheetTimer.Tick += (_, _) => StepSheet();
        }
        _sheetTimer.Start();
    }

    private void StepSheet()
    {
        bool running = false;
        if (_sheetEntrance < 1)
        {
            _sheetEntrance = (float)Math.Clamp(Stopwatch.GetElapsedTime(_sheetStart).TotalMilliseconds / SheetEntranceMs, 0, 1);
            if (_sheetEntrance >= 1 || (_activeGroup == null && _detailItem == null) || !Visible) EndSheetEntrance();
            else running = true;
        }
        if (_sheetExitLayer != null)
        {
            if (Stopwatch.GetElapsedTime(_sheetExitStart).TotalMilliseconds >= SheetExitMs || !Visible) DropSheetExit();
            else running = true;
        }
        if (!running) _sheetTimer?.Stop();
        Invalidate();
    }

    private void BeginSheetEntrance()
    {
        EndSheetEntrance();
        DropSheetExit();
        DropSheetPage();
        if (!SystemInformation.IsMenuAnimationEnabled) return;
        _sheetEntrance = 0;
        _sheetStart = Stopwatch.GetTimestamp();
        StartSheetTimer();
    }

    private void EndSheetEntrance()
    {
        _sheetEntrance = 1;
        _sheetLayer?.Dispose();
        _sheetLayer = null;
    }

    private void DropSheetExit()
    {
        _sheetExitLayer?.Dispose();
        _sheetExitLayer = null;
    }

    /// <summary>Da chiamare prima di azzerare lo stato della scheda: la dipinge un'ultima volta
    /// in un livello che poi sfuma sopra la pagina.</summary>
    private void BeginSheetExit(Rectangle panel, Action<Graphics> draw)
    {
        EndSheetEntrance();
        DropSheetExit();
        DropSheetPage();
        if (!SystemInformation.IsMenuAnimationEnabled || !Visible || Width < 2 || Height < 2) return;
        int firstHit = _hits.Count;
        try
        {
            _sheetExitLayer = RenderSheetLayer(ClientRectangle, draw);
            _sheetExitPanel = Rectangle.Intersect(panel, ClientRectangle);
            _sheetExitStart = Stopwatch.GetTimestamp();
            StartSheetTimer();
        }
        catch { DropSheetExit(); }
        finally { if (_hits.Count > firstHit) _hits.RemoveRange(firstHit, _hits.Count - firstHit); }
    }

    private CinecorePlayer2025.Utilities.GdiLayer RenderSheetLayer(Rectangle page, Action<Graphics> draw)
    {
        var layer = new CinecorePlayer2025.Utilities.GdiLayer(this, page.Size);
        try
        {
            var lg = layer.Graphics;
            // Fondo come la pagina oscurata: gli angoli arrotondati del pannello si fondono.
            lg.Clear(Color.FromArgb(Back.R * 77 / 255, Back.G * 77 / 255, Back.B * 77 / 255));
            lg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            lg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            lg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            lg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            draw(lg);
            return layer;
        }
        catch { layer.Dispose(); throw; }
    }

    private void DrawSheetEntrance(Graphics g, Rectangle page, Rectangle panel, Action<Graphics> draw)
    {
        panel = Rectangle.Intersect(panel, page);
        if (panel.Width <= 0 || panel.Height <= 0) { draw(g); return; }
        if (_sheetLayer == null || _sheetLayer.Size != page.Size)
        {
            _sheetLayer?.Dispose();
            int firstHit = _hits.Count;
            _sheetLayer = RenderSheetLayer(page, draw);
            _sheetLayerHits.Clear();
            _sheetLayerHits.AddRange(_hits.GetRange(firstHit, _hits.Count - firstHit));
            // Il tempo parte da qui: preparare la scheda (primo fotogramma) costa piu' di un
            // fotogramma normale, e contandolo l'animazione si mostrava gia' a meta' o finita.
            _sheetStart = Stopwatch.GetTimestamp();
            _sheetEntrance = 0;
        }
        else
            _hits.AddRange(_sheetLayerHits); // le zone cliccabili restano quelle del pannello finale
        ComposeSheet(g, page, _sheetLayer, panel, 1 - Math.Pow(1 - _sheetEntrance, 3));
    }

    private void DrawSheetExit(Graphics g, Rectangle page)
    {
        if (_sheetExitLayer == null || _sheetExitLayer.Size != page.Size) return;
        double t = Math.Clamp(Stopwatch.GetElapsedTime(_sheetExitStart).TotalMilliseconds / SheetExitMs, 0, 1);
        ComposeSheet(g, page, _sheetExitLayer, _sheetExitPanel, Math.Pow(1 - t, 2));
    }

    private void ComposeSheet(Graphics g, Rectangle page, CinecorePlayer2025.Utilities.GdiLayer layer, Rectangle panel, double visible)
    {
        Rectangle bounds = SheetBounds(page);
        using (var dim = new SolidBrush(Color.FromArgb((int)Math.Round(178 * visible), 0, 0, 0)))
            g.FillRectangle(dim, bounds);
        if (panel.Width <= 0 || panel.Height <= 0) return;
        int rise = (int)Math.Round((1 - visible) * S(LayoutScale(bounds), 22));
        layer.DrawTo(g, panel, new Point(panel.X, panel.Y + rise), visible);
    }

    private void AnimatePanelEntrance(Control? panel)
    {
        _panelEntranceTimer?.Dispose();
        if (panel == null || !SystemInformation.IsMenuAnimationEnabled) return;
        Point target = panel.Location;
        long started = Stopwatch.GetTimestamp();
        panel.Top = target.Y + 10;
        _panelEntranceTimer = new Timer { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
        _panelEntranceTimer.Tick += (_, _) =>
        {
            if (panel.IsDisposed || !panel.Visible) { _panelEntranceTimer.Stop(); return; }
            double t = Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / 160, 0, 1);
            panel.Location = new Point(target.X, target.Y + (int)Math.Round(10 * Math.Pow(1 - t, 3)));
            if (t >= 1) _panelEntranceTimer.Stop();
        };
        _panelEntranceTimer.Start();
    }
}
