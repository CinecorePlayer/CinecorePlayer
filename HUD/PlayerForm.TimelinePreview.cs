#nullable enable
using System;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private void OnPreviewRequested(double seconds, Point _)
        {
            if (!_hud.Visible)
            {
                EndTimelinePreview();
                return;
            }

            _scrubActive = true;
            _hud.Visible = true;
            _hud.BringToFront();
            if (!_previewOverlayInitialized)
            {
                _previewOverlayInitialized = true;
                try
                {
                    SafeShowOverlayHost();
                    SyncOverlayToVideoRect();
                    BringOverlaysToFront();
                    UpdateMpcvrOverlayRegionMode();
                    if (!_useInlineOverlay)
                        _overlayHost?.RaiseAboveOwner();
                }
                catch { }
            }

            if (string.IsNullOrEmpty(_currentPath) || (!_currentMediaHasVideo && _info?.HasVideo != true))
            {
                _hud.SetPreview(null, seconds);
                return;
            }

            // Keep one decode session alive while scrubbing. Cancelling every mouse
            // move used to discard all work before a 4K decoder produced a frame.
            // Da un server di rete ogni fotogramma costa una richiesta e qualche megabyte letti in
            // concorrenza con il film: passi piu' larghi (10 s), cosi' muovendo il puntatore si
            // riusa quello gia' arrivato invece di chiederne uno nuovo ogni due secondi.
            double quantum = PreviewSourceIsRemote(_currentPath) ? 10.0 : TIMELINE_CACHE_QUANTUM_SEC;
            double slot = Math.Floor(Math.Max(0, seconds) / quantum) * quantum;
            if (_thumbCts?.IsCancellationRequested == true)
            {
                _thumbCts.Dispose();
                _thumbCts = null;
            }
            if (_thumbCts != null && Math.Abs(slot - _previewReqSeconds) < .001)
                return;
            _thumbCts ??= new CancellationTokenSource();
            _previewReqSeconds = slot;
            Interlocked.Increment(ref _previewReqSerial);
            var cachedStrip = BuildPreviewStrip(slot);
            if (cachedStrip != null) _hud.SetPreview(cachedStrip, slot);
            QueuePreviewWorker();
        }

        private static bool PreviewSourceIsRemote(string? path) =>
            !string.IsNullOrEmpty(path) && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        private Bitmap? BuildPreviewStrip(double seconds)
        {
            // In rete un solo fotogramma, quello sotto il puntatore: la striscia da cinque voleva
            // cinque salti nel file remoto e non arrivava mai prima che il puntatore si spostasse.
            if (PreviewSourceIsRemote(_currentPath))
                return _previewCache.TryGet(_currentPath, PreviewSlotTime(seconds, 2), out Bitmap? single) ? single : null;
            var frames = new Bitmap?[5];
            try
            {
                bool any = false;
                for (int i = 0; i < frames.Length; i++)
                {
                    double time = PreviewSlotTime(seconds, i);
                    any |= _previewCache.TryGet(_currentPath, time, out frames[i]);
                }
                if (!any) return null;
                const int gap = 2;
                int height = TIMELINE_PREVIEW_W * 9 / 16;
                var strip = new Bitmap(TIMELINE_PREVIEW_W * 5 + gap * 6, height);
                using var g = Graphics.FromImage(strip);
                g.Clear(Color.Black);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                for (int i = 0; i < frames.Length; i++)
                {
                    var tile = new Rectangle(gap + i * (TIMELINE_PREVIEW_W + gap), 0, TIMELINE_PREVIEW_W, height);
                    if (frames[i] is Bitmap frame)
                    {
                        // A 16:9 tile is filled without stretching. Crop only the
                        // excess aspect ratio, instead of adding wide empty gutters.
                        double ratio = tile.Width / (double)tile.Height;
                        float cropW = frame.Width, cropH = frame.Height;
                        if (cropW / cropH > ratio) cropW = (float)(cropH * ratio);
                        else cropH = (float)(cropW / ratio);
                        g.DrawImage(frame, tile, (frame.Width-cropW)/2, (frame.Height-cropH)/2,
                            cropW, cropH, GraphicsUnit.Pixel);
                    }
                    else
                    {
                        using var loading = new SolidBrush(Color.FromArgb(130, 150, 170));
                        for (int dot = 0; dot < 3; dot++) g.FillEllipse(loading, tile.Left + tile.Width / 2 - 12 + dot * 10, tile.Top + tile.Height / 2, 4, 4);
                    }

                }
                return strip;
            }
            finally { foreach (var frame in frames) frame?.Dispose(); }
        }

        private double PreviewSlotTime(double center, int index)
        {
            double end = Math.Max(0, _duration - .05);
            double seconds = Math.Max(0, center + (index - 2) * 10.0);
            return end > 0 ? Math.Min(seconds, end) : seconds;
        }

        private void QueuePreviewWorker()
        {
            if (Interlocked.CompareExchange(ref _previewWorkerRunning, 1, 0) == 0)
                _ = Task.Run(PreviewWorkerLoop);
        }

        private void PreviewWorkerLoop()
        {
            int completedReq = -1;
            var session = _thumbCts;
            if (session == null) { Interlocked.Exchange(ref _previewWorkerRunning, 0); return; }
            try
            {
                var token = session.Token;
                string? source = _currentPath;
                if (string.IsNullOrWhiteSpace(source)) { completedReq = Volatile.Read(ref _previewReqSerial); return; }
                _thumb.Open(source);
                while (Volatile.Read(ref _scrubActive) && !token.IsCancellationRequested)
                {
                    int req = Volatile.Read(ref _previewReqSerial);
                    double seconds = Volatile.Read(ref _previewReqSeconds);
                    foreach (int index in PreviewSourceIsRemote(source) ? new[] { 2 } : new[] { 2, 1, 3, 0, 4 })
                    {
                        if (token.IsCancellationRequested || req != Volatile.Read(ref _previewReqSerial)) break;
                        double time = PreviewSlotTime(seconds, index);
                        if (_previewCache.Contains(source, time)) continue;
                        using var frame = _thumb.Get(time, TIMELINE_PREVIEW_W, token, realtime: true, storyboard: true);
                        if (token.IsCancellationRequested) break;
                        // A worker still decoding the previous file must not seed the
                        // cache of the newly opened one.
                        if (!string.Equals(source, _currentPath, StringComparison.OrdinalIgnoreCase)) break;
                        if (frame != null) _previewCache.Put(source, time, frame);
                        var strip = BuildPreviewStrip(seconds);
                        if (strip == null) continue;
                        if (!TryBeginInvokeOnUi(() =>
                        {
                            if (!_scrubActive || !_hud.Visible || session != _thumbCts || req != Volatile.Read(ref _previewReqSerial))
                            { strip.Dispose(); return; }
                            _hud.SetPreview(strip, seconds);
                        })) strip.Dispose();
                    }
                    if (req != Volatile.Read(ref _previewReqSerial)) continue;
                    completedReq = req;
                    break;
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Dbg.Warn("Timeline preview: " + ex.Message); completedReq = Volatile.Read(ref _previewReqSerial); }
            finally
            {
                Interlocked.Exchange(ref _previewWorkerRunning, 0);
                if (Volatile.Read(ref _scrubActive) && _thumbCts is { IsCancellationRequested: false } &&
                    Volatile.Read(ref _previewReqSerial) != completedReq)
                    QueuePreviewWorker();
            }
        }

        private sealed class PreviewCache : IDisposable
        {
            private sealed class Entry
            {
                public long Key;
                public Bitmap Bmp = null!;
            }

            private readonly object _lock = new();
            private readonly int _capacity;
            private readonly double _quantumSec;
            private readonly Dictionary<long, LinkedListNode<Entry>> _map = new();
            private readonly LinkedList<Entry> _lru = new();
            private string? _source;

            public PreviewCache(int capacity, double quantumSec)
            {
                _capacity = Math.Max(8, capacity);
                _quantumSec = Math.Max(0.0001, quantumSec);
            }

            private long KeyOf(double seconds)
                => (long)Math.Round(seconds / _quantumSec);

            private bool SameSource(string? source)
                => string.Equals(_source, source, StringComparison.OrdinalIgnoreCase);

            public bool Contains(string? source, double seconds)
            {
                lock (_lock) return SameSource(source) && _map.ContainsKey(KeyOf(seconds));
            }

            /// <summary>Returns a private deep copy: callers on other threads may draw it freely.</summary>
            public bool TryGet(string? source, double seconds, out Bitmap? bmp)
            {
                long key = KeyOf(seconds);
                lock (_lock)
                {
                    if (SameSource(source) && _map.TryGetValue(key, out var node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        bmp = DeepCopy(node.Value.Bmp);
                        return true;
                    }
                }

                bmp = null;
                return false;
            }

            public void Put(string? source, double seconds, Bitmap bmp)
            {
                long key = KeyOf(seconds);
                var copy = DeepCopy(bmp);
                lock (_lock)
                {
                    if (!SameSource(source))
                    {
                        ClearLocked();
                        _source = source;
                    }

                    if (_map.TryGetValue(key, out var node))
                    {
                        try { node.Value.Bmp.Dispose(); } catch { }
                        node.Value.Bmp = copy;
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        return;
                    }

                    var entry = new Entry { Key = key, Bmp = copy };
                    var newNode = new LinkedListNode<Entry>(entry);
                    _lru.AddFirst(newNode);
                    _map[key] = newNode;

                    while (_map.Count > _capacity)
                    {
                        var last = _lru.Last;
                        if (last == null) break;
                        _lru.RemoveLast();
                        _map.Remove(last.Value.Key);
                        try { last.Value.Bmp.Dispose(); } catch { }
                    }
                }
            }

            // Bitmap.Clone() lets GDI+ share the pixel buffer copy-on-write between
            // clones, and GDI+ objects are not thread-safe: the worker and the UI thread
            // drawing clones of one cached frame produced torn preview tiles.
            private static Bitmap DeepCopy(Bitmap source)
            {
                var rect = new Rectangle(0, 0, source.Width, source.Height);
                var format = System.Drawing.Imaging.PixelFormat.Format32bppArgb;
                var copy = new Bitmap(source.Width, source.Height, format);
                var from = source.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, format);
                try
                {
                    var to = copy.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, format);
                    try
                    {
                        unsafe
                        {
                            long bytes = (long)Math.Abs(from.Stride) * source.Height;
                            if (from.Stride == to.Stride)
                                Buffer.MemoryCopy((void*)from.Scan0, (void*)to.Scan0, bytes, bytes);
                            else
                                for (int y = 0; y < source.Height; y++)
                                    Buffer.MemoryCopy((byte*)from.Scan0 + (long)y * from.Stride, (byte*)to.Scan0 + (long)y * to.Stride, to.Stride, source.Width * 4L);
                        }
                    }
                    finally { copy.UnlockBits(to); }
                }
                finally { source.UnlockBits(from); }
                return copy;
            }

            private void ClearLocked()
            {
                foreach (var e in _lru)
                {
                    try { e.Bmp.Dispose(); } catch { }
                }
                _lru.Clear();
                _map.Clear();
            }

            public void Clear()
            {
                lock (_lock)
                {
                    ClearLocked();
                    _source = null;
                }
            }

            public void Dispose() => Clear();
        }
    }
}
