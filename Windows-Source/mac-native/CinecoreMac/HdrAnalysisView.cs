using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Analisi HDR (HdrAnalysisForm di Windows, vista nello stile della musica a schermo intero):
/// - in alto due scelte a discesa: il grafico e la modalita' (tempo reale / film intero), a destra Esporta e la X;
/// - in tempo reale a sinistra il fotogramma misurato, a destra lo strumento (forma d'onda, cromaticita',
///   vettorscopio, falsi colori, ultimo minuto); sul film intero la luminosita' lungo il film, la distribuzione e i colori;
/// - sotto i numeri grandi e la timeline del film in riproduzione.
/// Le misure vengono da HdrAnalyzer (ffmpeg a parte): la riproduzione non viene toccata.
/// </summary>
internal sealed class HdrAnalysisView : Control
{
    private const int SampleCount = 480;
    // Bianco di riferimento: 203 nit in HDR (ITU-R BT.2408), 100 nit in SDR (il bianco pieno del master).
    private double ReferenceWhite => _result is { IsHdr: false } ? HdrAnalyzer.SdrReferenceWhite : 203;
    private static readonly double[] NitTicks = { 1, 10, 100, 1000, 10000 };
    private static readonly double[] AxisTops = { 100, 200, 400, 1000, 2000, 4000, 10000 };
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    public event Action? Closed;
    public event Action<IReadOnlyList<MenuEntry>, Point>? MenuRequested;
    /// <summary>Posizione del film in riproduzione (null: il file analizzato non e' quello in riproduzione).</summary>
    public Func<double>? PositionProvider;
    public Func<double>? DurationProvider;
    public Func<bool>? IsPlayingProvider;
    public Action? TogglePlay;
    public Action<double>? Seek;

    private string _path = "", _title = "";
    private HdrAnalyzer.Result? _result;
    private bool _running;
    /// <summary>Analisi del film completo ferma: si e' nella scheda in tempo reale.</summary>
    private volatile bool _holdFull;
    private double _liveLastCostMs;
    private double _progress;
    private CancellationTokenSource? _cts;
    private bool _liveView;
    private int _liveChart, _filmChart;
    private DateTime _openedAt;
    private Point _mouse = new(-1, -1);
    private bool _exporting;
    private string? _exportNote;
    private DateTime _exportNoteAt;
    private IImage? _background;
    private bool _dragging;
    private double _dragSeconds = double.NaN;

    // Tempo reale.
    private HdrAnalyzer.LiveMeter? _live;
    private HdrAnalyzer.FrameStats? _liveStats;
    // Il fotogramma dopo si chiede al decoder mentre questo si misura: _liveBusy copre solo il decoder,
    // _liveMeasuring le misure partite (al massimo una in corso e una in attesa).
    private bool _liveBusy;
    private int _liveMeasuring;
    // La posizione arriva dal riproduttore a scatti (~4 al secondo): fra uno e l'altro si stima con l'orologio.
    private double _posValue = double.NaN;
    private long _posStamp;
    private double _liveRequested = double.NaN;
    private bool _liveRequestedExact;
    private DateTime _liveLastStart, _liveDue;
    private long _tickPaint;
    private float _liveHoldPeak;
    private readonly List<(DateTime Stamp, float Peak, float Average)> _history = new();
    private DispatcherTimer? _timer;
    // Misure al secondo in tempo reale scelte dall'utente (0 = automatico: secondo quanto costa la misura).
    private int _liveFps = int.TryParse(MacSettings.Get("hdrLiveFps", "0"), out int savedFps) ? Math.Clamp(savedFps, 0, 24) : 0;
    private static readonly int[] FpsChoices = { 0, 1, 2, 5, 10, 24 };
    private static string FpsShort(int fps) => fps == 0 ? "Auto fps" : fps + " fps";
    private static string FpsLabel(int fps) => fps == 0 ? "Automatico (secondo il carico)" : fps == 1 ? "1 fotogramma al secondo" : fps + " fotogrammi al secondo";
    private TimeSpan TickInterval => TimeSpan.FromMilliseconds(_liveFps > 0 ? Math.Clamp(1000.0 / _liveFps / 3, 10, 200) : 200);
    internal void TestLiveFps(int fps) => SetLiveFps(fps);
    private void SetLiveFps(int fps)
    {
        _liveFps = fps; MacSettings.Set("hdrLiveFps", fps.ToString(CultureInfo.InvariantCulture));
        if (_timer != null) _timer.Interval = TickInterval;
        _liveRequested = double.NaN;
        InvalidateVisual();
    }
    // Letto dai thread dell'analisi (le proprieta' di Avalonia vanno lette solo dal thread dell'interfaccia).
    private volatile bool _playingNow;
    private WriteableBitmap? _waveBitmap, _chromaBitmap, _vectorBitmap, _pictureBitmap, _viewBitmap;
    private Rect _pictureSource, _viewSource;
    private readonly int[] _wavePixels = new int[HdrAnalyzer.ScopeData.WaveColumns * HdrAnalyzer.ScopeData.WaveLevels];
    private readonly int[] _chromaPixels = new int[HdrAnalyzer.ScopeData.ChromaSize * HdrAnalyzer.ScopeData.ChromaSize];
    private readonly int[] _vectorPixels = new int[HdrAnalyzer.ScopeData.VectorSize * HdrAnalyzer.ScopeData.VectorSize];
    private readonly byte[] _waveTone = new byte[4096];
    private int _waveToneSamples = -1;
    private static int[]? _chromaColors, _vectorColors;

    // Disposizione (calcolata a ogni disegno).
    private Rect _vChartPicker, _vModePicker, _vFpsPicker, _vClose, _vExport, _vVideo, _vChart, _vPlot, _vNumbers, _vTimeline, _vPlay, _vTimeRow, _vFilmPlot;

    public HdrAnalysisView() { IsVisible = false; Focusable = true; ClipToBounds = true; }

    public bool IsOpen => IsVisible;
    public HdrAnalyzer.Result? Result => _result;
    public HdrAnalyzer.FrameStats? LiveStats => _liveStats;
    public bool Running => _running;
    public bool LiveView => _liveView;

    public void Open(string path, string title, string? backdropPath)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _path = path; _title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title;
        _result = null; _running = true; _progress = 0; _exportNote = null;
        _live?.Dispose(); _live = null; _liveStats = null; _liveBusy = false; _liveMeasuring = 0; _liveRequested = double.NaN; _liveHoldPeak = 0; _history.Clear();
        _viewBitmap = _waveBitmap = _chromaBitmap = _vectorBitmap = _pictureBitmap = null;
        _liveView = PositionProvider != null;
        _holdFull = _liveView;
        _openedAt = DateTime.UtcNow;
        _background = LoadBackground(backdropPath);
        IsVisible = true; Focus();
        InvalidateVisual();
        // Priorita' normale: in Background, con la scheda che si ridisegna di continuo, scattava una volta su sei e "24 fps"
        // davano ~16 misure al secondo. Il lavoro del timer e' leggero (le misure vanno in sottofondo).
        _timer ??= new DispatcherTimer(TickInterval, DispatcherPriority.Normal, (_, _) => Tick());
        _timer.Start();
        _playingNow = Playing();
        HdrAnalyzer.IsPlaybackRunning = () => _playingNow;
        _ = Task.Run(() =>
        {
            try
            {
                var declared = HdrAnalyzer.ReadDeclared(path);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_cts != cts) return;
                    _result ??= declared;
                    if (!declared.BaseLayerNotMeasurable && declared.Error == null && PositionProvider != null)
                        _live = new HdrAnalyzer.LiveMeter(path, declared);
                    InvalidateVisual();
                });
                if (declared.Error != null || declared.BaseLayerNotMeasurable)
                {
                    Dispatcher.UIThread.Post(() => { if (_cts == cts) { _result = declared; _running = false; InvalidateVisual(); } });
                    return;
                }
                var cached = HdrAnalyzer.TryLoadCached(path, SampleCount);
                if (cached != null)
                {
                    Dispatcher.UIThread.Post(() => { if (_cts == cts) { _result = cached; _running = false; InvalidateVisual(); } });
                    return;
                }
                // Il film completo si misura solo nella sua scheda: in tempo reale resta in pausa (Held).
                var final = HdrAnalyzer.Analyze(path, SampleCount, (p, partial) => Dispatcher.UIThread.Post(() =>
                {
                    if (_cts != cts) return;
                    _progress = p; _result = partial; InvalidateVisual();
                }), cts.Token, () => _holdFull);
                Dispatcher.UIThread.Post(() => { if (_cts == cts) { _result = final; _running = false; _progress = 1; InvalidateVisual(); } });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[HDR] " + ex.Message);
                Dispatcher.UIThread.Post(() => { if (_cts == cts) { _running = false; InvalidateVisual(); } });
            }
        });
    }

    public void Close()
    {
        if (!IsVisible) return;
        _cts?.Cancel();
        _timer?.Stop();
        _playingNow = false;
        // Il decoder degli strumenti si chiude dopo l'ultima misura gia' partita (Dispose aspetta il suo lucchetto).
        var live = _live; _live = null; _liveStats = null;
        if (live != null) _ = Task.Run(live.Dispose);
        IsVisible = false;
        Closed?.Invoke();
    }

    public void SetChart(int index)
    {
        if (_liveView) _liveChart = Math.Clamp(index, 0, LiveCharts.Length - 1); else _filmChart = Math.Clamp(index, 0, FilmCharts.Length - 1);
        if (_liveView) RenderScopes();
        InvalidateVisual();
    }

    public void SetMode(bool live)
    {
        if (live && PositionProvider == null) return;
        _liveView = live;
        _holdFull = live;
        if (live) { _liveRequested = double.NaN; RenderScopes(); }
        InvalidateVisual();
    }

    private static IImage? LoadBackground(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            // Piccolissima: allargata diventa una sfocatura morbida (come BuildBackground di Windows).
            return Bitmap.DecodeToWidth(stream, 48, BitmapInterpolationMode.HighQuality);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ tempo reale

    private double Duration()
    {
        double d = 0;
        try { d = DurationProvider?.Invoke() ?? 0; } catch { }
        if (!(d > 0)) d = _result?.Duration ?? 0;
        return d;
    }

    private double Now()
    {
        if (!double.IsNaN(_dragSeconds)) return _dragSeconds;
        double p;
        try { p = PositionProvider?.Invoke() ?? 0; } catch { return 0; }
        long ts = System.Diagnostics.Stopwatch.GetTimestamp();
        if (p != _posValue || !_playingNow) { _posValue = p; _posStamp = ts; return p; }
        // Stessa posizione di prima con il film che scorre: avanza con l'orologio, mai oltre un secondo.
        return p + Math.Min(1, System.Diagnostics.Stopwatch.GetElapsedTime(_posStamp, ts).TotalSeconds);
    }

    private bool Playing() { try { return IsPlayingProvider?.Invoke() ?? false; } catch { return false; } }

    private void Tick()
    {
        if (!IsVisible) { _timer?.Stop(); _playingNow = false; return; }
        _playingNow = Playing();
        // Timeline e linea della posizione: al massimo 30 volte al secondo (il timer scatta piu' spesso ai ritmi alti).
        if (PositionProvider != null && Environment.TickCount64 - _tickPaint >= 33) { _tickPaint = Environment.TickCount64; InvalidateVisual(); }
        if (!_liveView || _live == null) return;
        if (_liveBusy || _liveMeasuring >= 2) return;
        double now = Now();
        bool playing = Playing() && double.IsNaN(_dragSeconds);
        // In pausa il fotogramma esatto, una volta; in riproduzione il fotogramma chiave (automatico) o quello esatto
        // al ritmo scelto dall'utente.
        bool exact = !playing || _liveFps > 0;
        double step = _liveFps > 0 ? 0.5 / _liveFps : 0.3;
        // Con un ritmo scelto e il film che scorre decide solo la cadenza (sotto): la posizione stimata si corregge a
        // scatti e questo controllo saltava una misura su cinque.
        bool cadence = playing && _liveFps > 0;
        if (!cadence && !double.IsNaN(_liveRequested) && Math.Abs(now - _liveRequested) < (playing ? step : 0.04) && _liveRequestedExact == exact) return;
        // Automatico: la misura dopo non prima del doppio di quanto e' costata l'ultima (4K HEVC decodificato in
        // software mentre il film scorre: a ritmo fisso le misure si accodavano e pesavano sulla riproduzione).
        // Con un ritmo scelto, una misura ogni 1/fps secondi (mai due insieme: _liveBusy).
        double gap = _liveFps > 0 ? 1000.0 / _liveFps : Math.Max(_liveLastCostMs < 150 ? 250 : 700, _liveLastCostMs * 2);
        // Con un ritmo scelto le misure partono su una cadenza fissa (il ritardo del timer non si somma a ogni misura:
        // prima a "24 fps" se ne facevano ~18).
        var utcNow = DateTime.UtcNow;
        // Il timer scatta a passi di ~15 ms: si parte anche con meno di un passo d'anticipo (la cadenza resta sull'ora
        // ideale, l'anticipo non si somma).
        if (playing && _liveFps > 0 && utcNow < _liveDue.AddMilliseconds(-15)) return;
        if (playing && _liveFps == 0 && (utcNow - _liveLastStart).TotalMilliseconds < gap) return;
        _liveDue = utcNow - _liveDue > TimeSpan.FromMilliseconds(gap) ? utcNow.AddMilliseconds(gap) : _liveDue.AddMilliseconds(gap);
        _liveBusy = true; _liveMeasuring++; _liveRequested = now; _liveRequestedExact = exact; _liveLastStart = DateTime.UtcNow;
        var live = _live;
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() =>
        {
            HdrAnalyzer.FrameStats? stats = null;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            HdrAnalyzer.Grab? grab = null;
            try { grab = live.Fetch(now, exact, token); } catch { }
            double grabMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // Il decoder e' libero: la scheda puo' chiedere il fotogramma dopo mentre questo si misura.
            Dispatcher.UIThread.Post(() => _liveBusy = false);
            try { if (grab != null) stats = live.Analyze(grab); } catch { }
            _liveLastCostMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (QaFrames) Console.Error.WriteLine($"[hdr dal vivo] {now:0.00} s esatto={exact} in riproduzione={playing}: {(stats != null ? "ok" : "niente")} in {_liveLastCostMs:0} ms (decoder {grabMs:0})");
            Dispatcher.UIThread.Post(() =>
            {
                _liveMeasuring = Math.Max(0, _liveMeasuring - 1);
                if (live != _live || stats == null) { if (live != _live) _ = Task.Run(live.Dispose); return; }
                // Una misura piu' vecchia arrivata dopo una piu' nuova non torna indietro.
                if (_liveStats != null && playing && stats.Time < _liveStats.Time && _liveStats.Time - stats.Time < 2) return;
                _liveStats = stats;
                _liveHoldPeak = Math.Max(_liveHoldPeak, stats.PeakNits);
                if (playing)
                {
                    _history.Add((DateTime.UtcNow, stats.PeakNits, stats.AverageNits));
                    _history.RemoveAll(h => (DateTime.UtcNow - h.Stamp).TotalSeconds > 62);
                }
                RenderScopes();
                InvalidateVisual();
            });
        });
    }

    private void RenderScopes()
    {
        var live = _live;
        if (live == null || _liveStats == null) return;
        var data = live.Scopes;
        lock (data)
        {
            RenderView(data);
            switch (_liveChart)
            {
                case 0: RenderWave(data); break;
                case 1: RenderChroma(data); break;
                case 2: RenderVector(data); break;
                case 3: RenderPicture(data); break;
            }
            if (_exporting) { RenderWave(data); RenderChroma(data); RenderVector(data); RenderPicture(data); }
        }
    }

    private static void Write(ref WriteableBitmap? bitmap, int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (bitmap == null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
            bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bitmap.Lock();
        int row = width * 4;
        unsafe
        {
            for (int y = 0; y < height; y++)
                bgra.Slice(y * row, row).CopyTo(new Span<byte>((byte*)fb.Address + (long)y * fb.RowBytes, row));
        }
    }

    private static void Write(ref WriteableBitmap? bitmap, int width, int height, int[] argb) =>
        Write(ref bitmap, width, height, MemoryMarshal.AsBytes(argb.AsSpan()));

    private void RenderView(HdrAnalyzer.ScopeData data)
    {
        if (data.PictureWidth <= 0 || data.Picture.Length != data.PictureWidth * data.PictureHeight * 4) return;
        Write(ref _viewBitmap, data.PictureWidth, data.PictureHeight, data.Picture);
        _viewSource = new Rect(data.PictureActiveX, data.PictureActiveY, Math.Max(1, data.PictureActiveWidth), Math.Max(1, data.PictureActiveHeight));
    }

    private void RenderPicture(HdrAnalyzer.ScopeData data)
    {
        if (data.PictureWidth <= 0 || data.FalseColor.Length != data.PictureWidth * data.PictureHeight * 4) return;
        Write(ref _pictureBitmap, data.PictureWidth, data.PictureHeight, data.FalseColor);
        _pictureSource = new Rect(data.PictureActiveX, data.PictureActiveY, Math.Max(1, data.PictureActiveWidth), Math.Max(1, data.PictureActiveHeight));
    }

    // Un punto della forma d'onda: il canale si accende sul fondo scuro, l'opacita' e' la quantita'.
    private static int WavePixel(int r, int g, int b)
    {
        int a = Math.Max(r, Math.Max(g, b));
        if (a <= 0) return 0;
        return (a << 24) | ((r * 255 / a) << 16) | ((g * 255 / a) << 8) | (b * 255 / a);
    }

    private static int Blend(int color, double amount) => ((int)Math.Clamp(amount * 255, 0, 255) << 24) | (color & 0xFFFFFF);

    private void RenderWave(HdrAnalyzer.ScopeData data)
    {
        const int columns = HdrAnalyzer.ScopeData.WaveColumns, levels = HdrAnalyzer.ScopeData.WaveLevels;
        if (_waveToneSamples != data.SamplesPerColumn)
        {
            // Intensita': radice del numero di pixel, cosi' si vedono sia le zone piene sia le poche alte luci.
            _waveToneSamples = data.SamplesPerColumn;
            double gain = 20.0 / Math.Max(1, data.SamplesPerColumn);
            for (int i = 0; i < _waveTone.Length; i++) _waveTone[i] = (byte)(255 * Math.Min(1, Math.Sqrt(i * gain)));
        }
        for (int column = 0; column < columns; column++)
        {
            int source = column * levels;
            for (int level = 0; level < levels; level++, source++)
                _wavePixels[(levels - 1 - level) * columns + column] = WavePixel(_waveTone[Math.Min(4095, data.WaveR[source])], _waveTone[Math.Min(4095, data.WaveG[source])], _waveTone[Math.Min(4095, data.WaveB[source])]);
        }
        Write(ref _waveBitmap, columns, levels, _wavePixels);
    }

    private static int[] ChromaColors()
    {
        if (_chromaColors != null) return _chromaColors;
        const int n = HdrAnalyzer.ScopeData.ChromaSize;
        var colors = new int[n * n];
        for (int cy = 0; cy < n; cy++)
            for (int cx = 0; cx < n; cx++)
            {
                double x = (cx + .5) / n * HdrAnalyzer.ScopeData.ChromaMaxX, y = Math.Max(1e-4, (cy + .5) / n * HdrAnalyzer.ScopeData.ChromaMaxY);
                double bigX = x / y, bigZ = (1 - x - y) / y;
                double r = 3.2406 * bigX - 1.5372 - 0.4986 * bigZ, g = -0.9689 * bigX + 1.8758 + 0.0415 * bigZ, b = 0.0557 * bigX - 0.2040 + 1.0570 * bigZ;
                r = Math.Max(0, r); g = Math.Max(0, g); b = Math.Max(0, b);
                double max = Math.Max(1e-6, Math.Max(r, Math.Max(g, b)));
                int Channel(double v) => (int)(255 * Math.Pow(0.18 + 0.82 * v / max, 1 / 2.2));
                colors[(n - 1 - cy) * n + cx] = (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
            }
        return _chromaColors = colors;
    }

    private static int[] VectorColors()
    {
        if (_vectorColors != null) return _vectorColors;
        const int n = HdrAnalyzer.ScopeData.VectorSize;
        const double kr = 0.2627, kb = 0.0593, kg = 1 - kr - kb, range = HdrAnalyzer.ScopeData.VectorRange;
        var colors = new int[n * n];
        for (int vy = 0; vy < n; vy++)
            for (int vx = 0; vx < n; vx++)
            {
                double cb = ((vx + .5) / n * 2 - 1) * range, cr = (1 - (vy + .5) / n * 2) * range;
                double length = Math.Max(1e-6, Math.Sqrt(cb * cb + cr * cr)), scale = Math.Min(1, length / range * 2.2) * 0.33 / length;
                cb *= scale; cr *= scale;
                double luma = 0.62, r = luma + cr * 2 * (1 - kr), b = luma + cb * 2 * (1 - kb), g = (luma - kr * r - kb * b) / kg;
                int Channel(double v) => (int)(255 * Math.Clamp(v, 0, 1));
                colors[vy * n + vx] = (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
            }
        return _vectorColors = colors;
    }

    private void RenderChroma(HdrAnalyzer.ScopeData data)
    {
        const int n = HdrAnalyzer.ScopeData.ChromaSize;
        int[] colors = ChromaColors();
        double gain = 400.0 / Math.Max(1, data.ChromaPixels);
        for (int cy = 0; cy < n; cy++)
            for (int cx = 0; cx < n; cx++)
            {
                int count = data.Chroma[cy * n + cx], target = (n - 1 - cy) * n + cx;
                _chromaPixels[target] = count == 0 ? 0 : Blend(colors[target], Math.Min(1, Math.Pow(count * gain, 0.4)));
            }
        Write(ref _chromaBitmap, n, n, _chromaPixels);
    }

    private void RenderVector(HdrAnalyzer.ScopeData data)
    {
        const int n = HdrAnalyzer.ScopeData.VectorSize;
        int[] colors = VectorColors();
        double gain = 300.0 / Math.Max(1, data.Pixels);
        for (int i = 0; i < data.Vector.Length; i++)
            _vectorPixels[i] = data.Vector[i] == 0 ? 0 : Blend(colors[i], Math.Min(1, Math.Pow(data.Vector[i] * gain, 0.4)));
        Write(ref _vectorBitmap, n, n, _vectorPixels);
    }

    // ------------------------------------------------------------------ testo e numeri

    private static string[] LiveCharts => new[] { "Forma d'onda", "Cromaticità", "Vettorscopio", "Falsi colori", "Ultimo minuto" };
    private static string[] FilmCharts => new[] { "Luminosità nel film", "Distribuzione", "Colori" };

    private double U(double v) => v * Math.Max(0.7, Bounds.Height / 1080);
    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static Color Ink(int alpha) => Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), 255, 255, 255);
    private double _alpha = 1;
    private int A(int v) => (int)Math.Round(v * _alpha);

    private CachedText Text(string text, double px, Typeface face, Color color, double maxWidth = double.PositiveInfinity) =>
        UiText.Get(L10n.T(text), face, Math.Max(1, px), color, maxWidth);

    private double PickerPx => Math.Max(16, U(30));
    private double BigPx => Math.Max(18, U(40));
    private double MediumPx => Math.Max(13, U(24));
    private double SmallPx => Math.Max(11, U(17));
    private double LabelPx => Math.Max(11, U(19));
    private double LineH(double px) => Math.Round(px * 1.25);

    private void Say(DrawingContext ctx, string text, double px, Typeface face, int alpha, double x, double y)
    {
        if (string.IsNullOrEmpty(text)) return;
        ctx.DrawText(Text(text, px, face, Ink(alpha)), new Point(x, y));
    }

    private void SayIn(DrawingContext ctx, string text, double px, Typeface face, int alpha, Rect r)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = Text(text, px, face, Ink(alpha), r.Width);
        ctx.DrawText(ft, r.TopLeft);
    }

    private double Measure(string text, double px, Typeface face) => Text(text, px, face, Colors.White).WidthIncludingTrailingWhitespace;

    private static string Nits(double value) =>
        value >= 100 ? value.ToString("N0", CultureInfo.CurrentCulture)
        : value >= 10 ? value.ToString("0", CultureInfo.CurrentCulture)
        : value >= 1 ? value.ToString("0.#", CultureInfo.CurrentCulture)
        : value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string Share(double value) =>
        value <= 0 ? "0%" : value < 0.0005 ? "<0,1%"
        : value >= 0.9995 ? "100%"
        : (value * 100).ToString(value < 0.1 || value > 0.9 ? "0.#" : "0", CultureInfo.CurrentCulture) + "%";

    private double BlackFloor => _result?.MasteringMinNits is double min && min > 0 ? min : 0.005;
    private string Black(double nits) => nits < 0.001 ? "< " + 0.001.ToString("0.###", CultureInfo.CurrentCulture) : Nits(nits);

    // Gamma dinamica del contenuto: sotto 0,005 nit (o il nero di mastering) il nero non distingue piu' nulla.
    private double DynamicRangeStops(double peak, double black) => peak > 0 ? Math.Log2(peak / Math.Max(Math.Max(0.005, BlackFloor), black)) : 0;

    private double AxisTopNits()
    {
        double need = Math.Max(_result?.MeasuredPeakNits ?? 0, _result?.DeclaredMaxCll ?? 0) * 1.04;
        return AxisTops.FirstOrDefault(top => top >= need, 10000);
    }

    private double LiveTopNits()
    {
        double need = Math.Max(Math.Max(_result?.MeasuredPeakNits ?? 0, _result?.DeclaredMaxCll ?? 0), _liveHoldPeak) * 1.04;
        return need <= 0 ? 1000 : AxisTops.FirstOrDefault(top => top >= need, 10000);
    }

    private static double YOf(Rect plot, double nits, double topPq) =>
        plot.Bottom - Math.Round(Math.Clamp(HdrAnalyzer.NitsToPq(nits) / topPq, 0, 1) * plot.Height);

    private static IEnumerable<string> FormatTags(HdrAnalyzer.Result result)
    {
        yield return !result.IsHdr ? "SDR" : result.Transfer == "HLG" ? "HLG" : "HDR10";
        if (result.DolbyVisionProfile is int profile)
            yield return "Dolby Vision " + profile + (result.DolbyVisionCompatibility is int compat and > 0 ? "." + compat : "");
        if (result.Hdr10Plus) yield return "HDR10+";
        if (result.Primaries.Length > 0) yield return result.Primaries;
        if (result.BitDepth > 0) yield return result.BitDepth + " bit";
        if (result.Width > 0) yield return result.Width + "×" + result.Height;
    }

    private static string FormatLine(HdrAnalyzer.Result result) => string.Join("   ", FormatTags(result));

    private static readonly (float X, float Y)[] SpectralLocus =
    {
        (.1741f, .0050f), (.1733f, .0048f), (.1714f, .0051f), (.1644f, .0109f), (.1566f, .0177f), (.1440f, .0297f), (.1241f, .0578f), (.0913f, .1327f),
        (.0687f, .2007f), (.0454f, .2950f), (.0235f, .4127f), (.0082f, .5384f), (.0039f, .6548f), (.0139f, .7502f), (.0389f, .8120f), (.0743f, .8338f),
        (.1142f, .8262f), (.1547f, .8059f), (.1929f, .7816f), (.2296f, .7543f), (.2658f, .7243f), (.3016f, .6923f), (.3373f, .6589f), (.3731f, .6245f),
        (.4087f, .5896f), (.4441f, .5547f), (.4788f, .5202f), (.5125f, .4866f), (.5448f, .4544f), (.5752f, .4242f), (.6029f, .3965f), (.6270f, .3725f),
        (.6658f, .3340f), (.6915f, .3083f), (.7079f, .2920f), (.7190f, .2809f), (.7260f, .2740f), (.7300f, .2700f), (.7334f, .2666f), (.7347f, .2653f)
    };

    private static readonly (string Name, (float X, float Y)[] Points)[] GamutTriangles =
    {
        ("Rec.709", new[] { (.640f, .330f), (.300f, .600f), (.150f, .060f) }),
        ("P3", new[] { (.680f, .320f), (.265f, .690f), (.150f, .060f) }),
        ("BT.2020", new[] { (.708f, .292f), (.170f, .797f), (.131f, .046f) })
    };

    private static readonly Color[] GamutColors = { Color.FromRgb(205, 210, 216), Color.FromRgb(226, 150, 60), Color.FromRgb(80, 150, 245) };

    // ------------------------------------------------------------------ disposizione

    private bool CanPick => _result is { Error: null, BaseLayerNotMeasurable: false };
    private bool CanExport => CanPick && (_result!.Samples.Count > 0 || _liveStats != null);

    private void LayoutView()
    {
        double w = Bounds.Width, h = Bounds.Height, m = U(64), top = U(40);
        string chart = _liveView ? LiveCharts[_liveChart] : FilmCharts[_filmChart];
        string mode = _liveView ? "Tempo reale" : "Film intero";
        double chevron = U(30), pickerH = LineH(PickerPx) + U(6);
        _vChartPicker = new Rect(m, top, Measure(chart, PickerPx, Semibold) + chevron, pickerH);
        _vModePicker = new Rect(_vChartPicker.Right + U(34), top, Measure(mode, PickerPx, Semibold) + chevron, pickerH);
        // Terzo menu, accanto a Tempo reale: quante misure al secondo (solo in tempo reale, con il film in riproduzione).
        _vFpsPicker = _liveView && PositionProvider != null && !_exporting
            ? new Rect(_vModePicker.Right + U(34), top, Measure(FpsShort(_liveFps), PickerPx, Semibold) + chevron, pickerH)
            : default;
        double box = U(40);
        _vClose = new Rect(w - m - box + U(8), top + (pickerH - box) / 2, box, box);
        double ew = Measure("Esporta", LabelPx, Regular) + U(4);
        _vExport = CanExport ? new Rect(_vClose.Left - U(30) - ew, top + (pickerH - LineH(LabelPx)) / 2, ew, LineH(LabelPx)) : default;

        bool timeline = PositionProvider != null && !_exporting;
        _vTimeRow = timeline ? new Rect(m, h - U(78), w - m * 2, U(40)) : new Rect(m, h - U(20), w - m * 2, 0);
        _vPlay = new Rect(m - U(8), _vTimeRow.Top, U(40), U(40));
        double timeW = Measure("00:00:00", SmallPx, Regular) + U(10);
        _vTimeline = new Rect(new Point(_vPlay.Right + U(14) + timeW + U(18), _vTimeRow.Top), new Point(_vTimeRow.Right - timeW - U(18), _vTimeRow.Bottom));
        double numbersH = LineH(BigPx) + LineH(SmallPx) + U(6);
        _vNumbers = new Rect(m, _vTimeRow.Top - U(timeline ? 48 : 30) - numbersH, w - m * 2, numbersH);

        double contentTop = top + pickerH + U(70), contentBottom = _vNumbers.Top - U(56), gap = U(56);
        double videoW = (w - m * 2 - gap) * 0.46, videoH = videoW * 9 / 16;
        if (videoH > contentBottom - contentTop) { videoH = Math.Max(U(120), contentBottom - contentTop); videoW = videoH * 16 / 9; }
        double y = contentTop + Math.Max(0, (contentBottom - contentTop - videoH) / 2);
        _vVideo = new Rect(m, y, videoW, videoH);
        _vChart = new Rect(_vVideo.Right + gap, y, Math.Max(10, w - m - (_vVideo.Right + gap)), videoH);
        if (!_liveView)
        {
            _vVideo = default;
            _vChart = new Rect(m, contentTop, w - m * 2, Math.Max(40, contentBottom - contentTop));
        }
    }

    // ------------------------------------------------------------------ disegno

    private static readonly bool QaFrames = Environment.GetEnvironmentVariable("CINECORE_QA_FRAMES") == "1";
    private long _qaFrameCount, _qaFrameReport = Environment.TickCount64; private double _qaFrameMs, _qaFrameMax;

    public override void Render(DrawingContext ctx)
    {
        if (!QaFrames) { RenderCore(ctx); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderCore(ctx);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _qaFrameCount++; _qaFrameMs += ms; _qaFrameMax = Math.Max(_qaFrameMax, ms);
        if (Environment.TickCount64 - _qaFrameReport >= 2000)
        {
            Console.Error.WriteLine($"[qa-frame] hdr {_qaFrameCount / 2.0:0} disegni/s, medio {_qaFrameMs / Math.Max(1, _qaFrameCount):0.0} ms, max {_qaFrameMax:0.0} ms, analisi {(_running ? "in corso" : "ferma")}, ultima misura dal vivo {_liveLastCostMs:0} ms");
            _qaFrameReport = Environment.TickCount64; _qaFrameCount = 0; _qaFrameMs = 0; _qaFrameMax = 0;
        }
    }

    private void RenderCore(DrawingContext ctx)
    {
        var page = new Rect(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 260, 0, 1);
        if (t < 1 && !_exporting) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        using var fade = ctx.PushOpacity(_exporting ? 1 : 1 - Math.Pow(1 - t, 3));
        ctx.FillRectangle(B(Color.FromRgb(8, 11, 15)), page);
        if (_background != null)
        {
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                var size = _background.Size;
                double cover = Math.Max(page.Width / size.Width, page.Height / size.Height);
                double bw = size.Width * cover, bh = size.Height * cover;
                ctx.DrawImage(_background, new Rect(size), new Rect((page.Width - bw) / 2, (page.Height - bh) / 2, bw, bh));
            }
            ctx.FillRectangle(B(Color.FromArgb(200, 6, 9, 13)), page);
        }
        _alpha = 1;
        LayoutView();
        var result = _result;

        string chart = _liveView ? LiveCharts[_liveChart] : FilmCharts[_filmChart];
        string mode = _liveView ? "Tempo reale" : "Film intero";
        if (CanPick)
        {
            DrawPicker(ctx, _vChartPicker, chart, A(_vChartPicker.Contains(_mouse) ? 255 : 245), !_exporting);
            if (PositionProvider != null && !_exporting) DrawPicker(ctx, _vModePicker, mode, A(_vModePicker.Contains(_mouse) ? 235 : 140), true);
            if (_vFpsPicker.Width > 0) DrawPicker(ctx, _vFpsPicker, FpsShort(_liveFps), A(_vFpsPicker.Contains(_mouse) ? 235 : 140), true);
            else Say(ctx, mode, PickerPx, Semibold, A(140), _vModePicker.Left, _vModePicker.Top);
        }
        else Say(ctx, "Analisi HDR", PickerPx, Semibold, A(245), _vChartPicker.Left, _vChartPicker.Top);
        if (!_exporting)
        {
            bool closeHot = _vClose.Contains(_mouse);
            var c = _vClose.Center; double d = Math.Max(6, _vClose.Width * 0.21);
            var pen = new Pen(B(Ink(A(closeHot ? 255 : 170))), Math.Max(1.6, U(2.4)), lineCap: PenLineCap.Round);
            ctx.DrawLine(pen, new Point(c.X - d, c.Y - d), new Point(c.X + d, c.Y + d));
            ctx.DrawLine(pen, new Point(c.X + d, c.Y - d), new Point(c.X - d, c.Y + d));
            if (_vExport.Width > 0) Say(ctx, "Esporta", LabelPx, Regular, A(_vExport.Contains(_mouse) ? 255 : 150), _vExport.Left, _vExport.Top);
        }
        string sub = _title + (result != null ? "   ·   " + FormatLine(result) : "   ·   Leggo il file…");
        SayIn(ctx, sub, SmallPx, Regular, A(135), new Rect(_vChartPicker.Left, _vChartPicker.Bottom + U(10), Bounds.Width - _vChartPicker.Left - U(64), LineH(SmallPx) + 4));

        // Niente barra di avanzamento sotto i menu (l'utente: "non serve a niente"): l'analisi del film intero
        // si vede nel suo grafico ("Analizzo il film…").
        if (false && _running && result is { Error: null } && !_exporting)
        {
            var track = new Rect(_vChartPicker.Left, _vChartPicker.Bottom + U(44), U(220), Math.Max(2, U(3)));
            ctx.FillRectangle(B(Ink(A(40))), track);
            ctx.FillRectangle(B(Ink(A(220))), new Rect(track.X, track.Y, track.Width * Math.Clamp(_progress, 0, 1), track.Height));
            string state = _holdFull ? "Analisi del film in pausa · {0}% · riprende nella scheda del film" : "Analisi del film {0}%";
            Say(ctx, string.Format(CultureInfo.CurrentCulture, state, (int)Math.Round(_progress * 100)), SmallPx, Regular, A(130), track.Right + U(14), track.Top - LineH(SmallPx) / 2);
        }

        if (result != null && (result.Error != null || result.BaseLayerNotMeasurable))
        {
            string message = result.Error != null ? "Non sono riuscito a leggere i fotogrammi di questo file."
                : "Dolby Vision profilo 5: l'immagine di base non è HDR10 e i nit non si possono misurare da qui.";
            SayIn(ctx, message, LabelPx, Regular, A(190), new Rect(_vChartPicker.Left, Bounds.Height / 2 - U(20), Bounds.Width - U(128), U(40)));
            return;
        }

        if (_liveView) DrawVideo(ctx);
        DrawChartPanel(ctx, result);
        DrawNumbers(ctx, result);
        if (PositionProvider != null && !_exporting) DrawTimeline(ctx);
        if (_exportNote != null && (DateTime.UtcNow - _exportNoteAt).TotalSeconds < 8 && !_exporting)
            Say(ctx, _exportNote, SmallPx, Regular, A(150), _vChartPicker.Left, Bounds.Height - U(30));
    }

    private void DrawPicker(DrawingContext ctx, Rect r, string text, int alpha, bool chevron)
    {
        Say(ctx, text, PickerPx, Semibold, alpha, r.Left, r.Top);
        if (!chevron) return;
        double cx = r.Right - U(12), cy = r.Top + LineH(PickerPx) / 2 + U(2), z = Math.Max(4, U(7));
        var pen = new Pen(B(Ink(alpha)), Math.Max(1.6, U(2.4)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(cx - z, cy - z / 2), false);
            g.LineTo(new Point(cx, cy + z / 2));
            g.LineTo(new Point(cx + z, cy - z / 2));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private void DrawImage(DrawingContext ctx, IImage image, Rect source, Rect target)
    {
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(image, source, target);
    }

    private void DrawVideo(DrawingContext ctx)
    {
        var r = _vVideo;
        using (ctx.PushClip(new RoundedRect(r, U(12))))
        {
            ctx.FillRectangle(B(Color.FromRgb(4, 5, 7)), r);
            if (_viewBitmap != null)
            {
                double aspect = _viewSource.Width / Math.Max(1, _viewSource.Height);
                double w = r.Width, h = w / aspect;
                if (h > r.Height) { h = r.Height; w = h * aspect; }
                DrawImage(ctx, _viewBitmap, _viewSource, new Rect(r.Left + (r.Width - w) / 2, r.Top + (r.Height - h) / 2, w, h));
            }
            else SayIn(ctx, "In attesa del fotogramma…", SmallPx, Regular, A(120), new Rect(r.Left + U(20), r.Top + r.Height / 2 - U(10), r.Width - U(40), U(24)));
        }
    }

    private void DrawLegendRow(DrawingContext ctx, (string Label, int Kind)[] items, string? value = null, string? valueLabel = null)
    {
        double baseline = _vChart.Top + LineH(MediumPx);
        double smallTop = baseline - LineH(SmallPx), x = _vChart.Left;
        foreach (var (text, kind) in items.Where(item => item.Kind >= 10))
        {
            double sw = U(22), sy = smallTop + LineH(SmallPx) * .55;
            ctx.DrawLine(new Pen(B(GamutColors[kind - 10]), Math.Max(2, U(3))), new Point(x, sy), new Point(x + sw, sy));
            x += sw + U(10);
            Say(ctx, text, SmallPx, Regular, A(165), x, smallTop);
            x += Measure(text, SmallPx, Regular) + U(30);
        }
        if (!string.IsNullOrEmpty(value))
        {
            double vw = Measure(value!, MediumPx, Semibold), vx = _vChart.Right - vw;
            Say(ctx, value!, MediumPx, Semibold, A(245), vx, baseline - LineH(MediumPx));
            if (!string.IsNullOrEmpty(valueLabel))
                Say(ctx, valueLabel!, SmallPx, Regular, A(165), vx - U(12) - Measure(valueLabel!, SmallPx, Regular), smallTop);
        }
        _vPlot = new Rect(new Point(_vChart.Left, baseline + U(26)), new Point(_vChart.Right, Math.Max(baseline + U(40), _vChart.Bottom)));
    }

    private static Pen Dashed(Color color, double thickness) => new(B(color), thickness, new DashStyle(new double[] { 4, 3 }, 0));

    private void DrawRightScale(DrawingContext ctx, Rect plot, double top, double topPq, double labelsLeft)
    {
        var guide = Dashed(Ink(A(36)), 1);
        foreach (double tick in NitTicks.Where(t => t < top).Append(top))
        {
            double y = YOf(plot, tick, topPq);
            if (tick < top && y - plot.Top < U(18)) continue;
            ctx.DrawLine(guide, new Point(plot.Left, y), new Point(plot.Right, y));
            Say(ctx, Nits(tick) + (tick == top ? " nit" : ""), SmallPx, Regular, A(130), labelsLeft, y - LineH(SmallPx) / 2);
        }
    }

    private void DrawChartPanel(DrawingContext ctx, HdrAnalyzer.Result? result)
    {
        double gutter = U(96);
        if (_liveView)
        {
            var live = _liveStats;
            if (live == null)
            {
                SayIn(ctx, _live == null ? "Preparo gli strumenti…" : "Misuro il fotogramma…", LabelPx, Regular, A(150),
                    new Rect(_vChart.Left, _vChart.Top + _vChart.Height / 2 - U(14), _vChart.Width, U(30)));
                return;
            }
            double top = LiveTopNits(), topPq = HdrAnalyzer.NitsToPq(top);
            switch (_liveChart)
            {
                case 0:
                {
                    DrawLegendRow(ctx, new[] { ("rosso, verde e blu", 0) }, Nits(live.PeakNits) + " nit", "picco");
                    var plot = new Rect(new Point(_vPlot.Left, _vPlot.Top), new Point(_vPlot.Right - gutter, _vPlot.Bottom));
                    if (_waveBitmap != null)
                    {
                        double shown = topPq * HdrAnalyzer.ScopeData.WaveLevels;
                        DrawImage(ctx, _waveBitmap, new Rect(0, HdrAnalyzer.ScopeData.WaveLevels - shown, HdrAnalyzer.ScopeData.WaveColumns, shown), plot);
                    }
                    DrawRightScale(ctx, plot, top, topPq, plot.Right + U(14));
                    if (_result?.DeclaredMaxCll is int declared && declared < top)
                    {
                        double y = YOf(plot, declared, topPq);
                        ctx.DrawLine(Dashed(Ink(A(150)), Math.Max(1, U(1.4))), new Point(plot.Left, y), new Point(plot.Right, y));
                        Say(ctx, "MaxCLL", SmallPx, Regular, A(150), plot.Left + U(6), y - LineH(SmallPx) - U(2));
                    }
                    break;
                }
                case 1:
                {
                    string beyond = live.GamutMeasured ? Share(1 - live.GamutRec709) : "—";
                    DrawLegendRow(ctx, new[] { ("Rec.709", 10), ("P3", 11), ("BT.2020", 12) }, beyond, "oltre Rec.709");
                    double height = _vPlot.Height, width = Math.Round(height * HdrAnalyzer.ScopeData.ChromaMaxX / HdrAnalyzer.ScopeData.ChromaMaxY);
                    if (width > _vPlot.Width) { width = _vPlot.Width; height = Math.Round(width * HdrAnalyzer.ScopeData.ChromaMaxY / HdrAnalyzer.ScopeData.ChromaMaxX); }
                    DrawChromaPlot(ctx, new Rect(_vPlot.Left + (_vPlot.Width - width) / 2, _vPlot.Top, width, height));
                    break;
                }
                case 2:
                {
                    DrawLegendRow(ctx, new[] { ("tinta e saturazione", 0) });
                    double d = Math.Min(_vPlot.Width, _vPlot.Height) - U(40);
                    DrawVectorPlot(ctx, new Rect(_vPlot.Left + (_vPlot.Width - d) / 2, _vPlot.Top + U(20), d, d));
                    break;
                }
                case 3:
                    DrawLegendRow(ctx, new[] { ("fasce di luminosità", 0) });
                    DrawFalseColorPlot(ctx, _vPlot);
                    break;
                default:
                {
                    DrawLegendRow(ctx, new[] { ("media", 0), ("picco", 1) }, Nits(live.AverageNits) + " nit", "media ora");
                    var plot = new Rect(new Point(_vPlot.Left, _vPlot.Top), new Point(_vPlot.Right - gutter, _vPlot.Bottom - U(30)));
                    DrawRightScale(ctx, plot, top, topPq, plot.Right + U(14));
                    Say(ctx, "60 s fa", SmallPx, Regular, A(130), plot.Left, plot.Bottom + U(8));
                    Say(ctx, "ora", SmallPx, Regular, A(130), plot.Right - Measure("ora", SmallPx, Regular), plot.Bottom + U(8));
                    if (_history.Count >= 2)
                    {
                        var stamp = _history[^1].Stamp;
                        var peak = new List<Point>(); var average = new List<Point>();
                        foreach (var entry in _history)
                        {
                            double age = (stamp - entry.Stamp).TotalSeconds;
                            if (age > 60) continue;
                            double x = plot.Right - age / 60 * plot.Width;
                            peak.Add(new Point(x, YOf(plot, entry.Peak, topPq)));
                            average.Add(new Point(x, YOf(plot, entry.Average, topPq)));
                        }
                        DrawCurves(ctx, plot, average, peak);
                        CurveLabel(ctx, plot, average, "media", 230);
                        CurveLabel(ctx, plot, peak, "picco", 140);
                    }
                    else SayIn(ctx, "Avvia il film: qui scorre l'ultimo minuto.", SmallPx, Regular, A(120), new Rect(plot.Left, plot.Top + plot.Height / 2 - U(10), plot.Width, U(24)));
                    break;
                }
            }
            return;
        }

        if (result == null || result.Samples.Count == 0)
        {
            SayIn(ctx, "Analizzo il film…", LabelPx, Regular, A(150), new Rect(_vChart.Left, _vChart.Top + _vChart.Height / 2 - U(14), _vChart.Width, U(30)));
            return;
        }
        switch (_filmChart)
        {
            case 0: DrawFilmTime(ctx, result, gutter); break;
            case 1: DrawFilmHistogram(ctx, result); break;
            default: DrawFilmColours(ctx, result); break;
        }
    }

    private static StreamGeometry Polyline(IReadOnlyList<Point> points, bool closeToBottom = false, double bottom = 0)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(points[0], closeToBottom);
        for (int i = 1; i < points.Count; i++) g.LineTo(points[i]);
        if (closeToBottom) { g.LineTo(new Point(points[^1].X, bottom)); g.LineTo(new Point(points[0].X, bottom)); }
        g.EndFigure(closeToBottom);
        return geo;
    }

    /// <summary>Due curve come nei grafici della musica: la principale piena con l'area sfumata, la seconda tenue.</summary>
    private void DrawCurves(DrawingContext ctx, Rect plot, IReadOnlyList<Point> main, IReadOnlyList<Point> faint)
    {
        _frameStamp++;
        if (main.Count < 2) return;
        using (ctx.PushClip(plot.Inflate(new Thickness(0, 2))))
        {
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Ink(A(90)), 0), new GradientStop(Ink(A(4)), 1) }
            };
            ctx.DrawGeometry(fill, null, Polyline(main, true, plot.Bottom));
            if (faint.Count > 1) ctx.DrawGeometry(null, new Pen(B(Ink(A(110))), Math.Max(1, U(1.4)), lineJoin: PenLineJoin.Round), Polyline(faint));
            ctx.DrawGeometry(null, new Pen(B(Ink(A(240))), Math.Max(1.5, U(2.4)), lineJoin: PenLineJoin.Round), Polyline(main));
        }
    }

    private void CurveLabel(DrawingContext ctx, Rect plot, IReadOnlyList<Point> curve, string text, int alpha)
    {
        if (curve.Count == 0) return;
        var end = curve[^1];
        double tw = Measure(text, SmallPx, Regular);
        double y = Math.Clamp(end.Y - LineH(SmallPx) - U(6), plot.Top, Math.Max(plot.Top, plot.Bottom - LineH(SmallPx)));
        // "media" e "picco" vicini (film SDR, curve quasi sovrapposte) finivano uno sull'altro: il secondo si sposta.
        double h = LineH(SmallPx) + U(2);
        if (_labelFrame == _frameStamp && Math.Abs(y - _lastLabelY) < h)
            y = _lastLabelY - h >= plot.Top ? _lastLabelY - h : _lastLabelY + h;
        _labelFrame = _frameStamp; _lastLabelY = y;
        Say(ctx, text, SmallPx, Regular, A(alpha), plot.Right - tw, y);
    }
    private long _frameStamp, _labelFrame = -1;
    private double _lastLabelY;

    private void DrawFilmTime(DrawingContext ctx, HdrAnalyzer.Result result, double gutter)
    {
        DrawLegendRow(ctx, new[] { ("media del fotogramma", 0), ("picco del fotogramma", 1) }, Nits(result.MeasuredPeakNits) + " nit", "picco a " + Clock(result.MeasuredPeakTime));
        var plot = new Rect(new Point(_vPlot.Left, _vPlot.Top), new Point(_vPlot.Right - gutter, _vPlot.Bottom - U(30)));
        _vFilmPlot = plot;
        double top = AxisTopNits(), topPq = HdrAnalyzer.NitsToPq(top);
        double duration = Math.Max(1, result.Duration);
        DrawRightScale(ctx, plot, top, topPq, plot.Right + U(14));
        double step = duration > 5400 ? 1800 : duration > 2400 ? 900 : duration > 600 ? 300 : duration > 120 ? 60 : 10;
        for (double t = 0; t < duration - step * .3; t += step)
        {
            string text = Clock(t);
            double x = plot.Left + t / duration * plot.Width, tw = Measure(text, SmallPx, Regular);
            Say(ctx, text, SmallPx, Regular, A(130), Math.Clamp(x - tw / 2, plot.Left, plot.Right - tw), plot.Bottom + U(8));
        }
        if (ReferenceWhite < top)
        {
            double y = YOf(plot, ReferenceWhite, topPq);
            ctx.DrawLine(Dashed(Ink(A(120)), Math.Max(1, U(1.2))), new Point(plot.Left, y), new Point(plot.Right, y));
        }
        var samples = result.Samples;
        var peak = new Point[samples.Count];
        var average = new Point[samples.Count];
        // Curve ammorbidite (media mobile su pochi campioni, sulla scala PQ): i 480 punti grezzi facevano un pettine.
        int radius = Math.Max(1, samples.Count / 160);
        for (int i = 0; i < samples.Count; i++)
        {
            double x = plot.Left + samples[i].Time / duration * plot.Width;
            double sp = 0, sa = 0; int n = 0;
            for (int k = Math.Max(0, i - radius); k <= Math.Min(samples.Count - 1, i + radius); k++)
            { sp += HdrAnalyzer.NitsToPq(samples[k].PeakNits); sa += HdrAnalyzer.NitsToPq(samples[k].AverageNits); n++; }
            peak[i] = new Point(x, plot.Bottom - Math.Clamp(sp / n / topPq, 0, 1) * plot.Height);
            average[i] = new Point(x, plot.Bottom - Math.Clamp(sa / n / topPq, 0, 1) * plot.Height);
        }
        DrawCurves(ctx, plot, average, peak);
        CurveLabel(ctx, plot, average, "media", 230);
        CurveLabel(ctx, plot, peak, "picco", 140);
        if (ReferenceWhite < top) Say(ctx, "bianco di riferimento", SmallPx, Regular, A(110), plot.Left + U(6), YOf(plot, ReferenceWhite, topPq) - LineH(SmallPx) - U(2));

        double now = PositionProvider != null ? Now() : 0;
        if (now > 0 && now <= duration && !_exporting)
        {
            double x = plot.Left + now / duration * plot.Width;
            ctx.DrawLine(new Pen(B(UiTheme.WithAlpha(UiTheme.Accent, (byte)A(230))), Math.Max(1.5, U(2))), new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
        if (plot.Contains(_mouse) && samples.Count > 0 && !_exporting)
        {
            double hover = (_mouse.X - plot.Left) / plot.Width * duration;
            int nearest = 0;
            for (int i = 1; i < samples.Count; i++) if (Math.Abs(samples[i].Time - hover) < Math.Abs(samples[nearest].Time - hover)) nearest = i;
            double x = average[nearest].X;
            ctx.DrawLine(new Pen(B(Ink(A(120))), 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            foreach (var p in new[] { average[nearest], peak[nearest] }) ctx.DrawEllipse(B(Ink(A(255))), null, p, U(4), U(4));
            string text = Clock(samples[nearest].Time) + "   picco " + Nits(samples[nearest].PeakNits) + "   media " + Nits(samples[nearest].AverageNits) + " nit";
            double tw = Measure(text, SmallPx, Regular);
            double tx = x + U(12) + tw > plot.Right ? x - U(12) - tw : x + U(12);
            Say(ctx, text, SmallPx, Regular, A(230), tx, plot.Top + U(4));
        }
    }

    private void DrawFilmHistogram(DrawingContext ctx, HdrAnalyzer.Result result)
    {
        var items = new List<(string, int)> { ("film intero", 0) };
        DrawLegendRow(ctx, items.ToArray(), Share(result.ShareAbove100), "sopra 100 nit");
        var plot = new Rect(new Point(_vPlot.Left, _vPlot.Top), new Point(_vPlot.Right, _vPlot.Bottom - U(30)));
        double top = AxisTopNits(), topPq = HdrAnalyzer.NitsToPq(top);
        ctx.DrawLine(new Pen(B(Ink(A(50))), 1), plot.BottomLeft, plot.BottomRight);
        foreach (double tick in NitTicks.Where(t => t < top).Append(top))
        {
            double x = plot.Left + HdrAnalyzer.NitsToPq(tick) / topPq * plot.Width;
            string text = Nits(tick) + (tick == top ? " nit" : "");
            double tw = Measure(text, SmallPx, Regular);
            Say(ctx, text, SmallPx, Regular, A(130), Math.Clamp(x - tw / 2, plot.Left, plot.Right - tw), plot.Bottom + U(8));
        }
        var histogram = result.Histogram;
        double max = histogram.Max();
        if (max <= 0) return;
        int bins = histogram.Length;
        double binsShown = topPq * bins, barWidth = plot.Width / binsShown;
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, plot.Top, RelativeUnit.Absolute), EndPoint = new RelativePoint(0, plot.Bottom, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Ink(A(235)), 0), new GradientStop(Ink(A(70)), 1) }
        };
        for (int i = 0; i < bins && i < Math.Ceiling(binsShown); i++)
        {
            if (histogram[i] <= 0) continue;
            double height = Math.Max(U(2), Math.Sqrt(histogram[i] / max) * plot.Height);
            double x = plot.Left + i * barWidth + U(1), right = Math.Min(plot.Right, x + barWidth - U(2));
            if (right - x < 1) continue;
            ctx.FillRectangle(fill, new Rect(x, plot.Bottom - height, right - x, height));
        }
        if (plot.Contains(_mouse) && !_exporting)
        {
            int bin = Math.Clamp((int)((_mouse.X - plot.Left) / barWidth), 0, bins - 1);
            double from = HdrAnalyzer.PqToNits(bin / (double)bins), to = HdrAnalyzer.PqToNits((bin + 1) / (double)bins);
            string text = Nits(from) + " – " + Nits(to) + " nit   " + Share(histogram[bin]) + " dell'immagine";
            double tw = Measure(text, SmallPx, Regular), x = _mouse.X + U(12) + tw > plot.Right ? _mouse.X - U(12) - tw : _mouse.X + U(12);
            Say(ctx, text, SmallPx, Regular, A(230), x, plot.Top + U(4));
        }
    }

    private void DrawFilmColours(DrawingContext ctx, HdrAnalyzer.Result result)
    {
        DrawLegendRow(ctx, new[] { ("parte dei colori in ciascuno spazio", 0) });
        if (!result.GamutMeasured)
        {
            SayIn(ctx, "Gamut non misurato per questo formato", LabelPx, Regular, A(150), new Rect(_vPlot.Left, _vPlot.Top + U(20), _vPlot.Width, U(30)));
            return;
        }
        string[] names = { "Rec.709", "P3", "BT.2020" };
        string[] notes = { "i colori di un normale video", "oltre Rec.709, dentro P3", "oltre P3" };
        double[] shares = { result.GamutRec709, result.GamutP3, result.GamutRec2020 };
        double rowH = Math.Min(U(110), _vPlot.Height / 3);
        for (int i = 0; i < 3; i++)
        {
            double y = _vPlot.Top + i * rowH;
            Say(ctx, Share(shares[i]), BigPx, Semibold, A(245), _vPlot.Left, y);
            double vx = _vPlot.Left + U(170);
            Say(ctx, names[i], LabelPx, Semibold, A(235), vx, y + U(4));
            Say(ctx, notes[i], SmallPx, Regular, A(140), vx, y + U(4) + LineH(LabelPx) + U(2));
            var bar = new Rect(vx, y + LineH(BigPx) + U(6), Math.Max(1, _vPlot.Right - vx), Math.Max(3, U(5)));
            ctx.FillRectangle(B(Ink(A(30))), bar);
            ctx.FillRectangle(B(Color.FromArgb((byte)A(240), GamutColors[i].R, GamutColors[i].G, GamutColors[i].B)), new Rect(bar.X, bar.Y, bar.Width * Math.Clamp(shares[i], 0, 1), bar.Height));
        }
    }

    private void DrawChromaPlot(DrawingContext ctx, Rect plot)
    {
        if (_chromaBitmap != null) DrawImage(ctx, _chromaBitmap, new Rect(_chromaBitmap.Size), plot);
        Point At((float X, float Y) p) => new(plot.Left + p.X / HdrAnalyzer.ScopeData.ChromaMaxX * plot.Width, plot.Bottom - p.Y / HdrAnalyzer.ScopeData.ChromaMaxY * plot.Height);
        StreamGeometry Closed(IEnumerable<(float, float)> pts)
        {
            var list = pts.Select(At).ToList();
            var geo = new StreamGeometry();
            using var g = geo.Open();
            g.BeginFigure(list[0], false);
            foreach (var p in list.Skip(1)) g.LineTo(p);
            g.EndFigure(true);
            return geo;
        }
        ctx.DrawGeometry(null, new Pen(B(Ink(A(110))), Math.Max(1, U(1.2)), lineJoin: PenLineJoin.Round), Closed(SpectralLocus));
        for (int i = 0; i < GamutTriangles.Length; i++)
        {
            var c = GamutColors[i];
            ctx.DrawGeometry(null, new Pen(B(Color.FromArgb((byte)A(i == 0 ? 170 : 230), c.R, c.G, c.B)), Math.Max(1.2, U(i == 2 ? 2 : 1.6)), lineJoin: PenLineJoin.Round), Closed(GamutTriangles[i].Points));
        }
        ctx.DrawEllipse(null, new Pen(B(Ink(A(230))), Math.Max(1, U(1.4))), At((.3127f, .3290f)), U(4), U(4));
    }

    private void DrawVectorPlot(DrawingContext ctx, Rect circle)
    {
        var center = circle.Center;
        double radius = circle.Width / 2;
        if (_vectorBitmap != null)
            using (ctx.PushGeometryClip(new EllipseGeometry(circle)))
                DrawImage(ctx, _vectorBitmap, new Rect(_vectorBitmap.Size), circle);
        var line = new Pen(B(Ink(A(34))), 1);
        ctx.DrawEllipse(null, new Pen(B(Ink(A(90))), Math.Max(1, U(1.2))), center, radius, radius);
        ctx.DrawEllipse(null, line, center, radius / 2, radius / 2);
        ctx.DrawLine(line, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        ctx.DrawLine(line, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));
        const double kr = 0.2627, kb = 0.0593, kg = 1 - kr - kb;
        foreach (var (name, r, gg, b) in new[] { ("R", 1.0, 0.0, 0.0), ("Y", 1.0, 1.0, 0.0), ("G", 0.0, 1.0, 0.0), ("C", 0.0, 1.0, 1.0), ("B", 0.0, 0.0, 1.0), ("M", 1.0, 0.0, 1.0) })
        {
            double luma = kr * r + kg * gg + kb * b, cb = (b - luma) / (2 * (1 - kb)), cr = (r - luma) / (2 * (1 - kr));
            double length = Math.Max(1e-6, Math.Sqrt(cb * cb + cr * cr));
            double dx = cb / length, dy = -cr / length, tw = Measure(name, SmallPx, Regular);
            Say(ctx, name, SmallPx, Regular, A(150), center.X + dx * (radius + U(18)) - tw / 2, center.Y + dy * (radius + U(18)) - LineH(SmallPx) / 2);
        }
    }

    private void DrawFalseColorPlot(DrawingContext ctx, Rect area)
    {
        var bands = HdrAnalyzer.FalseColorBands;
        double legendH = LineH(SmallPx) + U(26);
        var picture = new Rect(area.Left, area.Top, area.Width, Math.Max(10, area.Height - legendH));
        if (_pictureBitmap != null)
        {
            double aspect = _pictureSource.Width / Math.Max(1, _pictureSource.Height);
            double w = picture.Width, h = w / aspect;
            if (h > picture.Height) { h = picture.Height; w = h * aspect; }
            var target = new Rect(picture.Left, picture.Top, w, h);
            using (ctx.PushClip(new RoundedRect(target, U(10)))) DrawImage(ctx, _pictureBitmap, _pictureSource, target);
        }
        double x = area.Left, y = area.Bottom - LineH(SmallPx);
        for (int i = 1; i < bands.Length; i++)
        {
            string text = Nits(bands[i].FromNits);
            double tw = Measure(text, SmallPx, Regular);
            uint argb = bands[i].Argb;
            ctx.FillRectangle(B(Color.FromArgb((byte)A(255), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)), new Rect(x, y + LineH(SmallPx) * .45 - U(2), U(16), Math.Max(3, U(5))));
            Say(ctx, text, SmallPx, Regular, A(150), x + U(22), y);
            x += U(22) + tw + U(22);
            if (x > area.Right - U(60)) break;
        }
    }

    private void DrawNumbers(DrawingContext ctx, HdrAnalyzer.Result? result)
    {
        var numbers = new List<(string Value, string Label)>();
        if (_liveView && _liveStats is { } live)
        {
            numbers.Add((Nits(live.PeakNits), "picco (nit)"));
            numbers.Add((Nits(live.AverageNits), "media (nit)"));
            numbers.Add((Nits(live.MedianNits), "luminosità tipica (nit)"));
            numbers.Add((Black(live.BlackNits), "nero (nit)"));
            numbers.Add((DynamicRangeStops(live.PeakNits, live.BlackNits).ToString("0.0", CultureInfo.CurrentCulture), "gamma dinamica (stop)"));
        }
        else if (!_liveView && result != null && result.Samples.Count > 0)
        {
            numbers.Add((Nits(result.MeasuredPeakNits), "picco misurato (nit)"));
            numbers.Add((Nits(result.MeasuredMaxAverageNits), "media più alta (nit)"));
            numbers.Add((Nits(result.MedianNits), "luminosità tipica (nit)"));
            numbers.Add((DynamicRangeStops(result.MeasuredPeakNits, result.BlackNits).ToString("0.0", CultureInfo.CurrentCulture), "gamma dinamica (stop)"));
            numbers.Add((result.DeclaredMaxCll is int cll ? Nits(cll) : "—", "MaxCLL dichiarato (nit)"));
        }
        if (numbers.Count == 0) return;
        var widths = numbers.Select(n => Math.Max(Measure(n.Value, BigPx, Semibold), Measure(n.Label, SmallPx, Regular))).ToArray();
        double spare = Math.Max(0, _vNumbers.Width - widths.Sum()) / Math.Max(1, numbers.Count - 1);
        double x = _vNumbers.Left;
        for (int i = 0; i < numbers.Count; i++)
        {
            Say(ctx, numbers[i].Value, BigPx, Semibold, A(245), x, _vNumbers.Top);
            Say(ctx, numbers[i].Label, SmallPx, Regular, A(150), x, _vNumbers.Top + LineH(BigPx) + U(4));
            x += widths[i] + spare;
        }
    }

    private void DrawTimeline(DrawingContext ctx)
    {
        double duration = Duration();
        if (duration <= 0) return;
        double now = Math.Clamp(Now(), 0, duration);
        // Play / pausa.
        var c = _vPlay.Center;
        bool hotPlay = _vPlay.Contains(_mouse);
        var ink = B(Ink(A(hotPlay ? 255 : 210)));
        double s = U(9);
        if (Playing())
        {
            ctx.FillRectangle(ink, new Rect(c.X - s * .8, c.Y - s, s * .55, s * 2), (float)U(1.5));
            ctx.FillRectangle(ink, new Rect(c.X + s * .25, c.Y - s, s * .55, s * 2), (float)U(1.5));
        }
        else
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open()) { g.BeginFigure(new Point(c.X - s * .7, c.Y - s), true); g.LineTo(new Point(c.X + s, c.Y)); g.LineTo(new Point(c.X - s * .7, c.Y + s)); g.EndFigure(true); }
            ctx.DrawGeometry(ink, null, geo);
        }
        double mid = _vTimeRow.Top + _vTimeRow.Height / 2;
        string left = Clock(now), right = Clock(duration);
        Say(ctx, left, SmallPx, Regular, A(170), _vPlay.Right + U(14), mid - LineH(SmallPx) / 2);
        Say(ctx, right, SmallPx, Regular, A(170), _vTimeline.Right + U(18), mid - LineH(SmallPx) / 2);
        bool hot = _vTimeline.Inflate(new Thickness(0, U(6))).Contains(_mouse) || _dragging;
        double thick = hot ? Math.Max(4, U(6)) : Math.Max(2, U(3));
        var track = new Rect(_vTimeline.Left, mid - thick / 2, _vTimeline.Width, thick);
        ctx.FillRectangle(B(Ink(A(45))), track, (float)(thick / 2));
        double fillW = track.Width * now / duration;
        ctx.FillRectangle(hot ? B(UiTheme.WithAlpha(UiTheme.Accent, (byte)A(255))) : B(Ink(A(230))), new Rect(track.X, track.Y, fillW, thick), (float)(thick / 2));
        if (hot) ctx.DrawEllipse(B(Ink(A(255))), null, new Point(track.X + fillW, mid), U(8), U(8));
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouse = e.GetPosition(this);
        if (_dragging) { _dragSeconds = SecondsAt(_mouse.X); }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _mouse = new Point(-1, -1); InvalidateVisual(); }

    private double SecondsAt(double x) => Math.Clamp((x - _vTimeline.Left) / Math.Max(1, _vTimeline.Width), 0, 1) * Duration();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        _mouse = p;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (_vClose.Contains(p)) { Close(); return; }
        if (_vExport.Width > 0 && _vExport.Inflate(U(6)).Contains(p)) { Export(); return; }
        if (CanPick && _vChartPicker.Contains(p))
        {
            var names = _liveView ? LiveCharts : FilmCharts;
            int current = _liveView ? _liveChart : _filmChart;
            var entries = names.Select((n, i) => (MenuEntry)new MenuEntry(n, null, () => SetChart(i)) { Checked = i == current }).ToList();
            MenuRequested?.Invoke(entries, new Point(_vChartPicker.Left, _vChartPicker.Bottom + U(6)));
            return;
        }
        if (CanPick && _vFpsPicker.Width > 0 && _vFpsPicker.Contains(p))
        {
            var entries = FpsChoices.Select(fps => (MenuEntry)new MenuEntry(FpsLabel(fps), null, () => SetLiveFps(fps)) { Checked = _liveFps == fps }).ToList();
            MenuRequested?.Invoke(entries, new Point(_vFpsPicker.Left, _vFpsPicker.Bottom + U(6)));
            return;
        }
        if (CanPick && PositionProvider != null && _vModePicker.Contains(p))
        {
            var entries = new List<MenuEntry>
            {
                new("Tempo reale", null, () => SetMode(true)) { Checked = _liveView },
                new("Film intero", null, () => SetMode(false)) { Checked = !_liveView },
            };
            MenuRequested?.Invoke(entries, new Point(_vModePicker.Left, _vModePicker.Bottom + U(6)));
            return;
        }
        if (PositionProvider != null && _vPlay.Contains(p)) { TogglePlay?.Invoke(); InvalidateVisual(); return; }
        if (PositionProvider != null && _vTimeline.Inflate(new Thickness(0, U(10))).Contains(p))
        {
            _dragging = true; _dragSeconds = SecondsAt(p.X);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }
        // Clic sul grafico del film intero: si va a quel punto del film.
        if (!_liveView && _filmChart == 0 && PositionProvider != null && _vFilmPlot.Contains(p) && _result is { Duration: > 0 } r)
            Seek?.Invoke((p.X - _vFilmPlot.Left) / _vFilmPlot.Width * r.Duration);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        double target = _dragSeconds;
        _dragSeconds = double.NaN;
        e.Pointer.Capture(null);
        if (!double.IsNaN(target)) Seek?.Invoke(target);
        _liveRequested = double.NaN;
        InvalidateVisual();
    }

    public void HandleKey(KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Space: TogglePlay?.Invoke(); break;
            case Key.Tab: if (PositionProvider != null && CanPick) SetMode(!_liveView); break;
            case Key.Left: case Key.Right:
                if (PositionProvider != null) Seek?.Invoke(Math.Max(0, Now() + (e.Key == Key.Left ? -5 : 5)));
                else if (CanPick) SetChart(((_liveView ? _liveChart : _filmChart) + (e.Key == Key.Left ? -1 : 1) + (_liveView ? LiveCharts.Length : FilmCharts.Length)) % (_liveView ? LiveCharts.Length : FilmCharts.Length));
                break;
            case Key.Up: case Key.Down:
                if (CanPick)
                {
                    int n = _liveView ? LiveCharts.Length : FilmCharts.Length;
                    SetChart(((_liveView ? _liveChart : _filmChart) + (e.Key == Key.Up ? -1 : 1) + n) % n);
                }
                break;
            default: e.Handled = false; break;
        }
    }

    // ------------------------------------------------------------------ esportazione

    private static string SafeFileName(string text)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, ' ');
        return text.Replace(':', ' ').Trim();
    }

    private async void Export()
    {
        var result = _result;
        if (result == null || !CanExport) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Esporta l'analisi HDR",
                SuggestedFileName = SafeFileName(_title) + " - analisi HDR",
                DefaultExtension = "html",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Rapporto con grafici") { Patterns = new[] { "*.html" } },
                    new FilePickerFileType("Immagine della vista") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("Campioni") { Patterns = new[] { "*.csv" } },
                    new FilePickerFileType("Tutte le misure") { Patterns = new[] { "*.json" } },
                }
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            ExportTo(path);
        }
        catch (Exception ex)
        {
            _exportNote = "Non sono riuscito a salvare il file: " + ex.Message;
            _exportNoteAt = DateTime.UtcNow;
            InvalidateVisual();
        }
    }

    /// <summary>Scrive l'esportazione nel formato dell'estensione (html, png, csv, json).</summary>
    public void ExportTo(string path)
    {
        var result = _result;
        if (result == null) return;
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": File.WriteAllBytes(path, CapturePng(_liveView, _liveView ? _liveChart : _filmChart)); break;
                case ".csv": File.WriteAllText(path, BuildCsv(result), new UTF8Encoding(true)); break;
                case ".json": File.WriteAllText(path, BuildJson(result), new UTF8Encoding(false)); break;
                default: File.WriteAllText(path, BuildHtml(result), new UTF8Encoding(false)); break;
            }
            _exportNote = "Salvato in " + path;
        }
        catch (Exception ex) { _exportNote = "Non sono riuscito a salvare il file: " + ex.Message; }
        _exportNoteAt = DateTime.UtcNow;
        InvalidateVisual();
    }

    /// <summary>La vista disegnata in un'immagine (senza menu, X e timeline), alla densita' dello schermo.</summary>
    private byte[] CapturePng(bool live, int chart)
    {
        bool view = _liveView; int liveChart = _liveChart, filmChart = _filmChart;
        _exporting = true; _liveView = live;
        if (live) _liveChart = chart; else _filmChart = chart;
        if (live) RenderScopes();
        try
        {
            double scale = Math.Max(1, TopLevel.GetTopLevel(this)?.RenderScaling ?? 2);
            var size = new PixelSize((int)Math.Ceiling(Bounds.Width * scale), (int)Math.Ceiling(Bounds.Height * scale));
            using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
                Render(ctx);
            using var ms = new MemoryStream();
            rtb.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            _exporting = false; _liveView = view; _liveChart = liveChart; _filmChart = filmChart;
            if (_liveView) RenderScopes();
        }
    }

    private static string Number(double value, string format = "0.####") => value.ToString(format, CultureInfo.InvariantCulture);

    private static string BuildCsv(HdrAnalyzer.Result result)
    {
        var text = new StringBuilder("time_s,time,peak_nits,average_nits\r\n");
        foreach (var sample in result.Samples)
            text.Append(Number(sample.Time, "0.###")).Append(',').Append(Clock(sample.Time)).Append(',')
                .Append(Number(sample.PeakNits, "0.##")).Append(',').Append(Number(sample.AverageNits, "0.###")).Append("\r\n");
        return text.ToString();
    }

    private string BuildJson(HdrAnalyzer.Result result)
    {
        var export = new Dictionary<string, object?>
        {
            ["title"] = _title,
            ["file"] = Path.GetFileName(_path),
            ["exported"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["analysisComplete"] = !_running,
            ["film"] = result,
            ["currentFrame"] = _liveStats
        };
        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    private string BuildHtml(HdrAnalyzer.Result result)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        static string Png(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
        string none = "—";
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"it\"><head><meta charset=\"utf-8\"><title>").Append(E("Analisi HDR – " + _title)).Append("</title><style>")
            .Append("body{font-family:-apple-system,system-ui,sans-serif;background:#0b1118;color:#e8ecf0;margin:32px auto;max-width:1220px;padding:0 20px}")
            .Append("h1{font-size:24px;margin:0 0 4px}h2{font-size:15px;margin:34px 0 10px}p.sub{color:#93a0ae;margin:0 0 6px}")
            .Append("table{border-collapse:collapse;font-size:13px}td,th{padding:5px 22px 5px 0;text-align:left;border-bottom:1px solid #1b2530}th{color:#93a0ae;font-weight:600}")
            .Append("td.n{text-align:right;font-variant-numeric:tabular-nums}img{max-width:100%;border-radius:12px;display:block}")
            .Append("details{margin-top:8px}summary{cursor:pointer;color:#93a0ae}.grid{display:grid;grid-template-columns:1fr 1fr;gap:0 48px}</style></head><body>");
        html.Append("<h1>Analisi HDR</h1><p class=\"sub\">").Append(E(_title)).Append("</p><p class=\"sub\">")
            .Append(E(Path.GetFileName(_path))).Append(" · ").Append(E(FormatLine(result))).Append("</p><p class=\"sub\">")
            .Append(E(DateTime.Now.ToString("g", CultureInfo.CurrentCulture)))
            .Append(_running ? E(" · " + string.Format(CultureInfo.CurrentCulture, "analisi ancora in corso ({0}%)", (int)Math.Round(_progress * 100))) : "").Append("</p>");

        void Table(string heading, IEnumerable<(string Name, string Value)> rows)
        {
            html.Append("<div><h2>").Append(E(heading)).Append("</h2><table>");
            foreach (var (name, value) in rows) html.Append("<tr><th>").Append(E(name)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
            html.Append("</table></div>");
        }

        html.Append("<div class=\"grid\">");
        Table("Dichiarato dal file", new[]
        {
            ("MaxCLL", result.DeclaredMaxCll is int cll ? Nits(cll) + " nit" : none),
            ("MaxFALL", result.DeclaredMaxFall is int fall ? Nits(fall) + " nit" : none),
            ("Schermo di mastering, massimo", result.MasteringMaxNits is double max ? Nits(max) + " nit" : none),
            ("Schermo di mastering, nero", result.MasteringMinNits is double min ? min.ToString("0.####", CultureInfo.CurrentCulture) + " nit" : none),
            ("Dolby Vision", result.DolbyVisionProfile is int profile ? "profilo " + profile + (result.DolbyVisionCompatibility is int compat and > 0 ? "." + compat : "") : none),
            ("HDR10+", result.Hdr10Plus ? "sì" : "no"),
            ("Immagine", result.Width + "×" + result.Height + (result.BitDepth > 0 ? ", " + result.BitDepth + " bit" : "") + (result.Primaries.Length > 0 ? ", " + result.Primaries : "")),
            ("Durata", Clock(result.Duration))
        });
        if (result.Samples.Count > 0)
            Table(string.Format(CultureInfo.CurrentCulture, "Misurato sul film ({0} fotogrammi)", result.Samples.Count), new[]
            {
                ("Picco (99,99° percentile)", Nits(result.MeasuredPeakNits) + " nit, a " + Clock(result.MeasuredPeakTime)),
                ("Massimo assoluto", Nits(result.MeasuredMaxNits) + " nit, a " + Clock(result.MeasuredMaxTime)),
                ("Picco tipico di un fotogramma", Nits(result.MedianPeakNits) + " nit"),
                ("Media più alta di un fotogramma", Nits(result.MeasuredMaxAverageNits) + " nit"),
                ("Media dell'intero film", Nits(result.OverallAverageNits) + " nit"),
                ("Luminanza media (Y)", Nits(result.LuminanceAverageNits) + " nit"),
                ("Mediana / 90° / 99° percentile", Nits(result.MedianNits) + " / " + Nits(result.P90Nits) + " / " + Nits(result.P99Nits) + " nit"),
                ("Nero (0,1° percentile)", Black(result.BlackNits) + " nit"),
                ("Gamma dinamica", DynamicRangeStops(result.MeasuredPeakNits, result.BlackNits).ToString("0.0", CultureInfo.CurrentCulture) + " stop"),
                ("Sopra 100 / 400 / 1000 nit", Share(result.ShareAbove100) + " / " + Share(result.ShareAbove400) + " / " + Share(result.ShareAbove1000)),
                ("Colori: Rec.709 / P3 / BT.2020", result.GamutMeasured ? Share(result.GamutRec709) + " / " + Share(result.GamutP3) + " / " + Share(result.GamutRec2020) : none),
                ("Area attiva", result.ActiveWidth > 0 ? result.ActiveWidth + "×" + result.ActiveHeight + " (" + (result.ActiveWidth / (double)Math.Max(1, result.ActiveHeight)).ToString("0.00", CultureInfo.CurrentCulture) + ":1)" : none)
            });
        html.Append("</div>");

        if (result.Samples.Count > 0)
            for (int chart = 0; chart < FilmCharts.Length; chart++)
                html.Append("<h2>").Append(E(FilmCharts[chart])).Append("</h2><img alt=\"\" src=\"").Append(Png(CapturePng(false, chart))).Append("\">");
        if (_liveStats is { } live && _live != null)
            for (int chart = 0; chart < LiveCharts.Length - 1; chart++)
                html.Append("<h2>").Append(E(LiveCharts[chart] + " · fotogramma a " + Clock(live.Time))).Append("</h2><img alt=\"\" src=\"").Append(Png(CapturePng(true, chart))).Append("\">");

        if (result.Samples.Count > 0)
        {
            html.Append("<h2>Campioni</h2><details><summary>").Append(E(string.Format(CultureInfo.CurrentCulture, "{0} fotogrammi: tempo, picco e media", result.Samples.Count)))
                .Append("</summary><table><tr><th>Tempo</th><th>Picco (nit)</th><th>Media (nit)</th></tr>");
            foreach (var sample in result.Samples)
                html.Append("<tr><td>").Append(Clock(sample.Time)).Append("</td><td class=\"n\">").Append(E(Nits(sample.PeakNits))).Append("</td><td class=\"n\">")
                    .Append(E(Nits(sample.AverageNits))).Append("</td></tr>");
            html.Append("</table></details>");
            html.Append("<h2>Distribuzione della luminosità</h2><details><summary>Quanta parte dell'immagine cade in ogni fascia</summary><table><tr><th>nit</th><th>%</th></tr>");
            for (int i = 0; i < result.Histogram.Length; i++)
            {
                if (result.Histogram[i] <= 0) continue;
                double from = HdrAnalyzer.PqToNits(i / (double)result.Histogram.Length), to = HdrAnalyzer.PqToNits((i + 1) / (double)result.Histogram.Length);
                html.Append("<tr><td>").Append(E(Nits(from) + " – " + Nits(to))).Append("</td><td class=\"n\">")
                    .Append((result.Histogram[i] * 100).ToString("0.0000", CultureInfo.CurrentCulture)).Append("</td></tr>");
            }
            html.Append("</table></details>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }
}
