using System;
using System.Net;
using System.Threading;

namespace CinecorePlayer2025.Utilities;

// Shared by lyrics and artwork, so one blocked search does not stall every track.
internal static class GeniusAvailability
{
    private static long _retryAfterTicks;
    internal static bool CanRequest => DateTime.UtcNow.Ticks >= Interlocked.Read(ref _retryAfterTicks);

    private static int _failures;

    internal static void Reset() { Interlocked.Exchange(ref _retryAfterTicks, 0); Interlocked.Exchange(ref _failures, 0); }

    internal static bool Observe(HttpStatusCode status)
    {
        if (status is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)) return false;
        // La pausa raddoppia a ogni rifiuto (2, 4, 8 minuti... fino a 6 ore): prima ogni due minuti si riprovava per sempre.
        int failures = Interlocked.Increment(ref _failures);
        double minutes = Math.Min(360, 2 * Math.Pow(2, Math.Min(10, failures - 1)));
        Interlocked.Exchange(ref _retryAfterTicks, DateTime.UtcNow.AddMinutes(minutes).Ticks);
        Dbg.Warn($"[GENIUS] HTTP {(int)status}: provider unavailable, retry in {minutes:0} minutes.");
        return true;
    }

    /// <summary>Genius chiede la verifica nel browser: inutile riprovare finche' l'utente non la fa (o per 6 ore).</summary>
    internal static void PauseForVerification()
    {
        Interlocked.Exchange(ref _retryAfterTicks, DateTime.UtcNow.AddHours(6).Ticks);
    }
}
