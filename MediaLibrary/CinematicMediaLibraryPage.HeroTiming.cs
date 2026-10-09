#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private readonly ConcurrentDictionary<string, Task<double?>> _heroDurations = new(StringComparer.OrdinalIgnoreCase);
        private double? HeroDurationMinutes(LibraryItem item)
        {
            if (item.DurationMinutes is > 0) return item.DurationMinutes;
            var task = _heroDurations.GetOrAdd(item.Path, path => Task.Run(() =>
            {
                double? duration = TryProbeVideoQuality(path)?.DurationMinutes;
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(Invalidate)); } catch { }
                return duration;
            }));
            return task.IsCompletedSuccessfully ? task.Result : null;
        }
    }
}
