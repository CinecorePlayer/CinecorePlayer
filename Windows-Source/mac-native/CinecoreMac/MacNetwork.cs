#nullable enable
using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Stato della connessione del Mac. Quando la rete cade, le richieste di metadati (TMDb, copertine del server, testi,
/// informazioni degli album, Last.fm) falliscono e finivano ricordate come "nessun risultato": tornata la rete non si
/// riprovava piu'. <see cref="Restored"/> avvisa (sul thread dell'interfaccia) quando la connessione torna, cosi' chi
/// ha memorizzato un fallimento lo dimentica e richiede.
/// </summary>
internal static class MacNetwork
{
    private static bool _started, _online = true;
    private static DispatcherTimer? _settle, _poll;

    /// <summary>Quando e' caduta l'ultima volta (UTC); null se la rete c'e'.</summary>
    public static DateTime? OfflineSince { get; private set; }
    /// <summary>Ultimo ritorno della connessione (UTC).</summary>
    public static DateTime LastRestored { get; private set; } = DateTime.MinValue;

    public static bool IsOnline => _online;

    /// <summary>La connessione e' tornata (o e' cambiata la rete): richiedere cio' che era fallito.</summary>
    public static event Action? Restored;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _online = HasUsableInterface();
        if (!_online) OfflineSince = DateTime.UtcNow;
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.UIThread.Post(Changed);
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.UIThread.Post(Changed);
        }
        catch (Exception ex) { Console.Error.WriteLine("rete: avvisi non disponibili: " + ex.Message); }
        // Riserva: un controllo leggero ogni 20 s (se gli avvisi del sistema non arrivano, es. Wi-Fi che resta collegato
        // a un router senza internet). Legge solo le interfacce, nessuna richiesta in rete.
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Changed());
        _poll.Start();
    }

    /// <summary>Un servizio segnala un errore di rete: se le interfacce dicono che manca la rete, la si segna caduta.</summary>
    public static void ReportFailure()
    {
        if (!_online) return;
        Dispatcher.UIThread.Post(Changed);
    }

    // L'elenco delle interfacce si legge in sottofondo (~30 ms, di piu' con molti adattatori virtuali): prima ogni
    // controllo bloccava l'interfaccia, anche durante il film.
    private static int _checking;

    private static void Changed()
    {
        if (System.Threading.Interlocked.Exchange(ref _checking, 1) == 1) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            bool usable = HasUsableInterface();
            Dispatcher.UIThread.Post(() => { _checking = 0; Apply(usable); });
        });
    }

    private static void Apply(bool now)
    {
        if (!now)
        {
            if (_online) { _online = false; OfflineSince = DateTime.UtcNow; Console.Error.WriteLine("rete: connessione persa"); }
            _settle?.Stop();
            return;
        }
        if (_online && _settle == null) return;
        // La rete torna a pezzi (indirizzo, DNS, instradamento): si aspetta che si assesti prima di richiedere tutto.
        _settle ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => Settled());
        _settle.Stop(); _settle.Start();
        _online = true;
    }

    private static void Settled()
    {
        _settle?.Stop(); _settle = null;
        if (!HasUsableInterface()) { _online = false; OfflineSince ??= DateTime.UtcNow; return; }
        Console.Error.WriteLine("rete: connessione tornata");
        LastRestored = DateTime.UtcNow;
        try { Restored?.Invoke(); }
        finally { OfflineSince = null; }
    }

    private static bool HasUsableInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                !n.Name.StartsWith("utun", StringComparison.Ordinal) && !n.Name.StartsWith("awdl", StringComparison.Ordinal) &&
                !n.Name.StartsWith("llw", StringComparison.Ordinal) && !n.Name.StartsWith("bridge", StringComparison.Ordinal) &&
                n.GetIPProperties().UnicastAddresses.Any(a =>
                    (a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) ||
                    (a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)));
        }
        catch { return true; }
    }
}
