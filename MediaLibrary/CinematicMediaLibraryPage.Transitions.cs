using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CinecorePlayer2025;

internal sealed partial class CinematicMediaLibraryPage
{
    private sealed class LibraryDialogForm : Form
    {
        private readonly Timer _entrance = new() { Interval = 16 };
        private long _started;
        public LibraryDialogForm()
        {
            DoubleBuffered = true;
            if (SystemInformation.IsMenuAnimationEnabled) Opacity = .01;
            _entrance.Tick += (_, _) =>
            {
                Opacity = Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / 150, .01, 1);
                if (Opacity >= 1) _entrance.Stop();
            };
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _started = Stopwatch.GetTimestamp();
            if (Opacity < 1) _entrance.Start();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _entrance.Dispose();
            base.Dispose(disposing);
        }
    }

    private Timer? _surfaceEntranceTimer;
    private float _surfaceEntrance = 1;
    private Timer? _panelEntranceTimer;

    private void BeginSurfaceEntrance()
    {
        _surfaceEntranceTimer?.Dispose();
        if (!SystemInformation.IsMenuAnimationEnabled) { _surfaceEntrance = 1; return; }
        _surfaceEntrance = 0;
        long started = Stopwatch.GetTimestamp();
        _surfaceEntranceTimer = new Timer { Interval = 16 };
        _surfaceEntranceTimer.Tick += (_, _) =>
        {
            _surfaceEntrance = Math.Clamp((float)(Stopwatch.GetElapsedTime(started).TotalMilliseconds / 170), 0, 1);
            if (_surfaceEntrance >= 1 || !Visible) _surfaceEntranceTimer.Stop();
            Invalidate();
        };
        _surfaceEntranceTimer.Start();
    }

    private void DrawSurfaceEntrance(Graphics g)
    {
        if (_surfaceEntrance >= 1 || (_detailItem == null && _activeGroup == null)) return;
        using var veil = new SolidBrush(Color.FromArgb((int)(100 * Math.Pow(1 - _surfaceEntrance, 2)), Back));
        g.FillRectangle(veil, ClientRectangle);
    }

    private void AnimatePanelEntrance(Control? panel)
    {
        _panelEntranceTimer?.Dispose();
        if (panel == null || !SystemInformation.IsMenuAnimationEnabled) return;
        Point target = panel.Location;
        long started = Stopwatch.GetTimestamp();
        panel.Top = target.Y + 10;
        _panelEntranceTimer = new Timer { Interval = 16 };
        _panelEntranceTimer.Tick += (_, _) =>
        {
            if (panel.IsDisposed || !panel.Visible) { _panelEntranceTimer.Stop(); return; }
            double t = Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / 160, 0, 1);
            panel.Location = new Point(target.X, target.Y + (int)Math.Round(10 * Math.Pow(1 - t, 3)));
            if (t >= 1) _panelEntranceTimer.Stop();
        };
        _panelEntranceTimer.Start();
    }
}
