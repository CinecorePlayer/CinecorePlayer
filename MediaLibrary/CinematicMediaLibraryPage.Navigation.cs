#nullable enable
using System;
using System.Drawing;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private readonly System.Windows.Forms.Timer _gridScrollAnimationTimer = new() { Interval = 16 };
        private int _gridScrollTarget;
        private int _gridScrollAnimationStart;
        private long _gridScrollAnimationStartedAt;
        private int _gridScrollAnimationDurationMs = 170;
        private Rectangle _hoverBounds = Rectangle.Empty;

        private void ConfigureNavigationAnimation()
        {
            _gridScrollAnimationTimer.Tick += (_, __) => AnimateGridScroll();
        }

        private void SetSmoothGridScrollTarget(int target)
        {
            int clamped = Math.Max(0, Math.Min(_gridScrollMax, target));
            if (clamped == _gridScrollTarget && _gridScrollAnimationTimer.Enabled)
                return;

            _gridScrollAnimationStart = _gridScroll;
            _gridScrollTarget = clamped;
            _gridScrollAnimationStartedAt = Environment.TickCount64;
            int distance = Math.Abs(_gridScrollTarget - _gridScrollAnimationStart);
            _gridScrollAnimationDurationMs = Math.Clamp(120 + distance / 6, 145, 230);
            if (_gridScrollTarget == _gridScroll)
            {
                _gridScrollAnimationTimer.Stop();
                return;
            }

            if (!_gridScrollAnimationTimer.Enabled)
                _gridScrollAnimationTimer.Start();
        }

        private void AnimateGridScroll()
        {
            _gridScrollTarget = Math.Max(0, Math.Min(_gridScrollMax, _gridScrollTarget));
            double elapsed = Math.Max(0, Environment.TickCount64 - _gridScrollAnimationStartedAt);
            double progress = Math.Min(1.0, elapsed / Math.Max(1, _gridScrollAnimationDurationMs));
            // Cubic ease-out: risposta immediata alla rotellina e arresto pulito.
            double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            _gridScroll = (int)Math.Round(_gridScrollAnimationStart +
                (_gridScrollTarget - _gridScrollAnimationStart) * eased);

            if (progress >= 1.0)
            {
                _gridScroll = _gridScrollTarget;
                _gridScrollAnimationTimer.Stop();
                Invalidate();
                return;
            }

            Invalidate();
        }

        private void ResetSmoothGridScroll()
        {
            _gridScrollAnimationTimer.Stop();
            _gridScroll = 0;
            _gridScrollTarget = 0;
            _gridScrollAnimationStart = 0;
            _gridScrollAnimationStartedAt = 0;
            _gridScrollMax = 0;
        }

        private void ClampSmoothGridScrollTarget()
        {
            _gridScroll = Math.Max(0, Math.Min(_gridScrollMax, _gridScroll));
            _gridScrollTarget = Math.Max(0, Math.Min(_gridScrollMax, _gridScrollTarget));
        }

        private void InvalidateHoverTransition(Rectangle previous, Rectangle current)
        {
            Rectangle dirty;
            if (previous.IsEmpty)
                dirty = current;
            else if (current.IsEmpty)
                dirty = previous;
            else
                dirty = Rectangle.Union(previous, current);

            if (dirty.IsEmpty)
                return;

            dirty.Inflate(10, 10);
            Invalidate(Rectangle.Intersect(ClientRectangle, dirty));
        }
    }
}
