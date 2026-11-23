using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Fotogrammi dell'anteprima sopra la barra del tempo (PlayerForm.TimelinePreview.cs): cinque punti, quello sotto il
/// puntatore e 10 e 20 secondi prima e dopo, a passi di 2 secondi, con una cache per file. Prima il fotogramma
/// tutti con un solo FFmpeg per macOS.
/// </summary>
internal sealed class TimelinePreview
{
    private double Quantum = 2.0;
    /// <summary>
    /// Disco di rete o FUSE (rclone): un solo fotogramma, quello sotto il puntatore, a passi di 10 s (PreviewSourceIsRemote
    /// di Windows). Cinque salti per posizione nello stesso file remoto che il film sta leggendo arrivavano tardi e
    /// toglievano banda al film.
    /// </summary>
    public bool Single { get; private set; }
    /// <summary>
    /// Windows, scheda video e rclone: un riquadro solo (banda del film) ma a passi di 2 s e con l'attesa breve. A passi
    /// di 10 s, trascinando piano, usciva un fotogramma al secondo; dal decoder sempre aperto uno costa ~100 ms.
    /// </summary>
    private bool _fastSingle;
    private const int CacheCapacity = 240;
    private readonly Action<Bitmap?[]?, double> _show;
    private readonly Dictionary<long, Bitmap> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private readonly HashSet<long> _failed = new();
    private readonly HashSet<long> _shown = new();
    private string? _file;
    private ProbeData? _probe;
    private double _duration;
    private double _slot = -1;
    private int _serial;
    private bool _active, _running, _cardShown;
    private CancellationTokenSource? _cts;

    public int FrameWidth { get; set; } = 256;

    public TimelinePreview(Action<Bitmap?[]?, double> show) { _show = show; }

    public void SetSource(string? file, ProbeData? probe, double duration)
    {
        if (!string.Equals(file, _file, StringComparison.Ordinal))
        {
            Dismiss();
            foreach (var b in _cache.Values) b.Dispose();
            _cache.Clear(); _lru.Clear(); _failed.Clear(); _shown.Clear();
        }
        _file = file; _probe = probe; _duration = duration;
        Single = MacStorage.IsSlowMount(file);
#if CINECORE_WINDOWS
        // Con la scheda video il film resta aperto in un solo decoder e ogni salto legge solo il fotogramma chiave: anche
        // da una cartella condivisa del NAS la striscia intera, come sul Mac. Un fotogramma solo resta per link e rclone.
        if (Single && GpuFramePreview.Shared.Usable && file != null && !file.Contains("://", StringComparison.Ordinal)
            && !MacStorage.IsRcloneMount(file)) Single = false;
        _fastSingle = Single && GpuFramePreview.Shared.Usable && file != null && !file.Contains("://", StringComparison.Ordinal);
#endif
        Quantum = Single && !_fastSingle ? 10.0 : 2.0;
    }

    private double SlotTime(double center, int index)
    {
        double end = Math.Max(0, _duration - .05);
        double s = Math.Max(0, center + (index - 2) * 10.0);
        return end > 0 ? Math.Min(s, end) : s;
    }

    private long Key(double seconds) => (long)Math.Round(seconds / Quantum);

    public void Request(double seconds, double duration)
    {
        _duration = duration;
        if (_file == null || _probe is not { HasVideo: true }) { _show(null, seconds); return; }
        double slot = Math.Floor(Math.Max(0, seconds) / Quantum) * Quantum;
        _active = true;
        if (Math.Abs(slot - _slot) < .001) return;
        _slot = slot;
        _serial++;
        _cts?.Cancel();
        _show(Tiles(slot), slot);
        if (!_running) _ = RunAsync();
    }

    public void Dismiss()
    {
        _active = false;
        _cardShown = false;
        _slot = -1;
        _cts?.Cancel();
        _show(null, 0);
    }

    /// <summary>I cinque riquadri dalla cache (null dove non c'e' ancora), o null se non ce n'e' nessuno.</summary>
    private Bitmap?[]? Tiles(double slot)
    {
        var tiles = new Bitmap?[5];
        bool any = false;
        _shown.Clear();
        for (int i = 0; i < 5; i++)
        {
            long key = Key(SlotTime(slot, i));
            if (!_cache.TryGetValue(key, out var b))
            {
                // Mentre arriva il fotogramma del punto nuovo resta il piu' vicino gia' pronto (entro 6 s): prima il
                // riquadro restava vuoto ~0,2 s e poi tornava spesso lo stesso fotogramma chiave ("si ricarica").
                long near = -1;
                for (int d = 1; d <= (Single && !_fastSingle ? 1 : 3) && near < 0; d++)
                    foreach (long k in new[] { key - d, key + d })
                        if (_cache.ContainsKey(k)) { near = k; break; }
                if (near < 0 || !_cache.TryGetValue(near, out b)) continue;
                key = near;
            }
            tiles[i] = b; any = true;
            _shown.Add(key);
            // Usato adesso: torna in testa, cosi' non e' il primo a uscire dalla cache.
            if (_lru.Find(key) is { } node) { _lru.Remove(node); _lru.AddFirst(node); }
        }
        // Una volta comparsa, la scheda resta della stessa misura per tutto il passaggio sulla timeline (riquadri in
        // arrivo coi puntini): prima, se nessun riquadro del punto nuovo era pronto, collassava nell'orario e ricompariva.
        if (any) _cardShown = true;
        if (Single) return tiles[2] is { } center ? new[] { center } : _cardShown ? new Bitmap?[1] : null;
        return any || _cardShown ? tiles : null;
    }

    private void Put(double time, MenuAmbient.Frame frame)
    {
        long key = Key(time);
        if (_cache.ContainsKey(key)) return;
        var bmp = ToBitmap(frame);
        _cache[key] = bmp;
        _lru.AddFirst(key);
        // I riquadri sulla striscia in questo momento non si liberano mai: l'HUD li sta disegnando e un'immagine gia'
        // liberata dava fotogrammi sporchi scorrendo la barra di un film lungo (cache piena).
        int guard = _lru.Count;
        while (_lru.Count > CacheCapacity && guard-- > 0)
        {
            long old = _lru.Last!.Value;
            _lru.RemoveLast();
            if (_shown.Contains(old)) { _lru.AddFirst(old); continue; }
            if (_cache.Remove(old, out var gone)) gone.Dispose();
        }
    }

    private static unsafe Bitmap ToBitmap(MenuAmbient.Frame f)
    {
        var wb = new WriteableBitmap(new PixelSize(f.Width, f.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = wb.Lock();
        fixed (byte* src = f.Bgra)
            for (int y = 0; y < f.Height; y++)
                Buffer.MemoryCopy(src + y * f.Width * 4, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, f.Width * 4);
        return wb;
    }

    private async Task RunAsync()
    {
        _running = true;
        try
        {
            while (_active && _file is { } file && _probe is { } probe)
            {
                int serial = _serial;
                double slot = _slot;
                _cts = new CancellationTokenSource();
                // Aspetta che il puntatore si fermi, evitando cinque letture di rete a ogni passaggio sulla timeline.
                // Con la scheda video (Windows) un fotogramma costa poco: basta una pausa breve.
#if CINECORE_WINDOWS
                await Task.Delay(GpuFramePreview.Shared.Usable && (!Single || _fastSingle) ? 60 : 180);
#else
                await Task.Delay(180);
#endif
                if (!_active) break;
                if (serial != _serial) continue;
                // Tutti e cinque insieme (0,15 s anche in 4K60).
                var order = Single ? new[] { 2 } : new[] { 2, 1, 3, 0, 4 };
                var center = Array.Empty<int>();
                var others = order.Where(i => !_cache.ContainsKey(Key(SlotTime(slot, i))))
                    .GroupBy(i => Key(SlotTime(slot, i))).Select(g => g.First()).ToArray();
#if CINECORE_WINDOWS
                // Scheda video: un fotogramma alla volta, prima quello sotto il puntatore, e ognuno compare appena pronto.
                // Quelli che non escono passano a FFmpeg qui sotto.
                if (GpuFramePreview.Shared.Usable && others.Length > 0)
                {
                    var token = _cts.Token;
                    foreach (int i in others)
                    {
                        if (serial != _serial || !_active || !GpuFramePreview.Shared.Usable) break;
                        double t = SlotTime(slot, i);
                        if (_failed.Contains(Key(t))) continue;
                        var frame = await GpuFramePreview.Shared.GrabAsync(file, t, FrameWidth, probe, token);
                        if (!string.Equals(file, _file, StringComparison.Ordinal)) return;
                        if (frame == null) continue;
                        Put(t, frame);
                        if (_active) _show(Tiles(_slot), _slot);
                    }
                    if (serial != _serial) continue;
                    others = others.Where(i => !_cache.ContainsKey(Key(SlotTime(slot, i)))).ToArray();
                }
#endif
                foreach (var batch in new[] { center, others })
                {
                    if (batch.Length == 0) continue;
                    if (serial != _serial || !_active) break;
                    var times = batch.Select(i => SlotTime(slot, i)).Where(t => !_failed.Contains(Key(t))).ToArray();
                    if (times.Length == 0) continue;
                    MenuAmbient.Frame?[] frames;
                    try { frames = await Task.Run(() => MenuAmbient.GrabFramesAsync(file, times, FrameWidth, probe, TimeSpan.FromSeconds(10), _cts.Token, keyframes: true)); }
                    catch { break; }
                    if (!string.Equals(file, _file, StringComparison.Ordinal)) return;
                    for (int k = 0; k < times.Length; k++) if (frames[k] is { } f) Put(times[k], f);
                    // Dal solo fotogramma chiave non sempre esce un'immagine (GOP aperti, fine del file): quei punti si
                    // rifanno con la ricerca esatta; se nemmeno cosi', non si riprovano piu'.
                    var missing = Enumerable.Range(0, times.Length).Where(k => frames[k] == null).Select(k => times[k]).ToArray();
                    if (missing.Length > 0 && serial == _serial && _active)
                    {
                        if (_active) _show(Tiles(_slot), _slot);
                        MenuAmbient.Frame?[] exact;
                        try { exact = await Task.Run(() => MenuAmbient.GrabFramesAsync(file, missing, FrameWidth, probe, TimeSpan.FromSeconds(10), _cts.Token)); }
                        catch { break; }
                        if (!string.Equals(file, _file, StringComparison.Ordinal)) return;
                        for (int k = 0; k < missing.Length; k++)
                            if (exact[k] is { } f) Put(missing[k], f);
                            else if (!_cts.IsCancellationRequested) _failed.Add(Key(missing[k]));
                    }
                    // Il puntatore puo' essersi spostato: si mostra la striscia del punto attuale con quel che c'e'.
                    if (_active) _show(Tiles(_slot), _slot);
                }
                if (serial == _serial) break;
            }
        }
        finally { _running = false; }
    }
}
