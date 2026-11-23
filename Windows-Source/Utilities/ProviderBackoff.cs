#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Servizi in rete che falliscono di continuo (LRCLIB fuori servizio, Spotify che rifiuta la chiave): dopo un errore si
    /// fermano per un po', e la pausa raddoppia a ogni errore (2 minuti, poi fino a 6 ore; 24 ore se il servizio rifiuta
    /// l'accesso). Prima si richiamavano a ogni brano: migliaia di richieste inutili e attese a vuoto.
    /// </summary>
    internal static class ProviderBackoff
    {
        private sealed record State(DateTime PausedUntil, TimeSpan LastPause);
        private static readonly ConcurrentDictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsPaused(string provider)
            => States.TryGetValue(provider, out var state) && state.PausedUntil > DateTime.UtcNow;

        public static void Succeeded(string provider) => States.TryRemove(provider, out _);

        /// <summary>Connessione tornata: le pause dovute a errori di rete finiscono (restano quelle per accesso rifiutato).</summary>
        public static void ForgetNetworkFailures()
        {
            foreach (var pair in States)
                if (pair.Value.LastPause < TimeSpan.FromHours(24)) States.TryRemove(pair);
        }

        public static void Failed(string provider, Exception? error = null)
        {
            HttpStatusCode? status = (error as HttpRequestException)?.StatusCode;
            bool refused = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            States.AddOrUpdate(provider,
                _ => new State(DateTime.UtcNow + First(refused), First(refused)),
                (_, old) =>
                {
                    var next = refused ? TimeSpan.FromHours(24) : TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(6).Ticks, old.LastPause.Ticks * 2));
                    return new State(DateTime.UtcNow + next, next);
                });
            if (States.TryGetValue(provider, out var now))
                Dbg.Log($"[NET] {provider} paused for {now.LastPause.TotalMinutes:0} min after: {error?.Message ?? "failure"}", Dbg.LogLevel.Info);
        }

        private static TimeSpan First(bool refused) => refused ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(2);
    }
}
