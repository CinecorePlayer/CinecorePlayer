#nullable enable
using System;
using System.Threading;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private int _startupReadyRaised;

        internal event EventHandler? StartupReady;

        private void SignalStartupReady()
        {
            if (Interlocked.Exchange(ref _startupReadyRaised, 1) != 0)
                return;

            try { StartupReady?.Invoke(this, EventArgs.Empty); } catch { }
            ScheduleStartupUpdateCheck();
            ScheduleStartupComponentCheck();
            ScheduleFirstRunRemoteOffer();
        }
    }
}
