#nullable enable
using System;
using System.Diagnostics;

namespace CinecorePlayer2025
{
    /// <summary>
    /// Playback clock for lyrics. Engine positions arrive every ~250 ms from WinForms
    /// timers, with jitter; driving lyrics from them directly made lines switch up to a
    /// quarter of a second late and word highlighting advance in visible steps (and jump
    /// back whenever a sample landed behind the extrapolation). The clock free-runs on the
    /// high-resolution timer, is slewed gently towards each sample, snaps on seeks, and is
    /// presented at display rate only while synced lyrics are visible.
    /// </summary>
    public sealed partial class PlayerForm
    {
        private const double LyricsClockSnapSeconds = 0.30;
        private const double LyricsClockSlew = 0.25;

        private readonly System.Windows.Forms.Timer _lyricsFrameTimer = new() { Interval = 15 };
        private bool _lyricsFrameTimerHooked;
        private double _lyricsClockBase;
        private long _lyricsClockStamp;
        private bool _lyricsClockValid;
        private bool _lyricsClockPaused;

        private double LyricsClockNow()
        {
            long now = Stopwatch.GetTimestamp();
            if (!_lyricsClockValid) return Math.Max(0, _lastKnownPlaybackPosition);
            if (_paused != _lyricsClockPaused)
            {
                // Freeze or resume from the value shown right now, never from a stale stamp.
                _lyricsClockBase = _lyricsClockPaused ? _lyricsClockBase : _lyricsClockBase + Stopwatch.GetElapsedTime(_lyricsClockStamp, now).TotalSeconds;
                _lyricsClockStamp = now;
                _lyricsClockPaused = _paused;
            }
            if (_paused) return _lyricsClockBase;
            // Bounded free-run: a stalled engine must not let the lyrics race ahead.
            double elapsed = Math.Min(1.0, Stopwatch.GetElapsedTime(_lyricsClockStamp, now).TotalSeconds);
            return Math.Max(0, _lyricsClockBase + elapsed);
        }

        /// <summary>Feeds one engine position sample (seconds).</summary>
        private void FeedLyricsClock(double sample)
        {
            if (!double.IsFinite(sample) || sample < 0) return;
            long now = Stopwatch.GetTimestamp();
            if (_timelineUiPositionOverride >= 0 && DateTime.UtcNow < _timelineUiOverrideUntilUtc)
                sample = _timelineUiPositionOverride; // a seek is in flight: follow its target

            double predicted = LyricsClockNow();
            double error = sample - predicted;
            if (!_lyricsClockValid || _paused || Math.Abs(error) > LyricsClockSnapSeconds)
                _lyricsClockBase = sample;
            else
                _lyricsClockBase = predicted + error * LyricsClockSlew;
            _lyricsClockStamp = now;
            _lyricsClockPaused = _paused;
            _lyricsClockValid = true;
            EnsureLyricsFrameTimer();
        }

        private void ResetLyricsClock(double position)
        {
            _lyricsClockValid = false;
            FeedLyricsClock(position);
        }

        private void EnsureLyricsFrameTimer()
        {
            if (!_lyricsFrameTimerHooked)
            {
                _lyricsFrameTimerHooked = true;
                _lyricsFrameTimer.Tick += (_, __) => OnLyricsFrame();
            }
            if (!_lyricsFrameTimer.Enabled && HasMusicPlayback && _audioMeters?.WantsLyricsFrames == true)
                _lyricsFrameTimer.Start();
        }

        private void OnLyricsFrame()
        {
            try
            {
                if (IsDisposed || !HasMusicPlayback || _audioMeters?.WantsLyricsFrames != true)
                {
                    _lyricsFrameTimer.Stop(); // restarted by the next engine sample
                    return;
                }
                _audioMeters.UpdateLyricsPosition(LyricsClockNow());
            }
            catch { _lyricsFrameTimer.Stop(); }
        }
    }
}
