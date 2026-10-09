#nullable enable
using System;
using System.Threading;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private int _startupReadyRaised;
        private DateTime _startupReadyAtUtc = DateTime.MinValue;
        private readonly DateTime _startupBeganUtc = DateTime.UtcNow;

        /// <summary>L'animazione di avvio e' finita (o non finira' piu'): si puo' aprire un contenuto senza fermarla a meta'.</summary>
        private bool StartupSplashFinished =>
            (_startupReadyAtUtc != DateTime.MinValue && (DateTime.UtcNow - _startupReadyAtUtc).TotalMilliseconds >= 900) ||
            (DateTime.UtcNow - _startupBeganUtc).TotalSeconds > 20;

        internal event EventHandler? StartupReady;

        private void SignalStartupReady()
        {
            if (Interlocked.Exchange(ref _startupReadyRaised, 1) != 0)
                return;
            _startupReadyAtUtc = DateTime.UtcNow;

            try { StartupReady?.Invoke(this, EventArgs.Empty); } catch { }
            ScheduleStartupUpdateCheck();
            ScheduleStartupComponentCheck();
            ScheduleFirstRunRemoteOffer();
        }
    }
}
