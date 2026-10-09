#nullable enable
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.Utilities
{
    // Le animazioni usavano Environment.TickCount64 (passo di ~16 ms) e timer da 15 ms:
    // anche con un buon timer il tempo avanzava a scatti. Qui un orologio preciso al
    // microsecondo e il timer di sistema a 1 ms per tutta la durata del player.
    internal static class AnimationClock
    {
        public static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        /// <summary>Intervallo dei timer durante un'animazione (~120 aggiornamenti/s).</summary>
        public const int FrameIntervalMs = 8;

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
        private static bool _highResolution;

        public static void EnableHighResolutionTimers()
        {
            if (_highResolution) return;
            try { _highResolution = timeBeginPeriod(1) == 0; } catch { }
        }

        public static void DisableHighResolutionTimers()
        {
            if (!_highResolution) return;
            try { timeEndPeriod(1); } catch { }
            _highResolution = false;
        }
    }
}
