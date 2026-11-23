using System;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// [2026-11-22] Un valore lento da leggere (unita' ottiche, indirizzi di rete) servito subito dall'ultima lettura e
/// rinfrescato in sottofondo quando e' piu' vecchio di <c>maxAge</c>. La prima lettura e' sincrona. Nato per il menu del
/// tasto destro, che rifaceva queste letture a ogni apertura (~65 ms su 80 a costruire le voci).
/// </summary>
internal sealed class StaleCache<T>
{
    private readonly Func<T> _read;
    private readonly TimeSpan _maxAge;
    private T? _value;
    private bool _has;
    private long _readAt;
    private int _refreshing;
    private readonly object _gate = new();

    public StaleCache(Func<T> read, TimeSpan maxAge) { _read = read; _maxAge = maxAge; }

    public T Get()
    {
        lock (_gate)
            if (_has)
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _readAt) > _maxAge.TotalMilliseconds) Refresh();
                return _value!;
            }
        var fresh = _read();
        lock (_gate) { _value = fresh; _has = true; _readAt = Environment.TickCount64; }
        return fresh;
    }

    /// <summary>Rilegge in sottofondo (es. quando la finestra torna in primo piano: un disco puo' essere stato inserito).</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        Task.Run(() =>
        {
            try { var fresh = _read(); lock (_gate) { _value = fresh; _has = true; _readAt = Environment.TickCount64; } }
            catch { }
            finally { Volatile.Write(ref _refreshing, 0); }
        });
    }
}
