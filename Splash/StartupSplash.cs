#nullable enable
using CinecorePlayer2025.Splash;
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace CinecorePlayer2025
{
    /// <summary>
    /// Keeps the WPF splash responsive while the WinForms player prepares its first frame.
    /// </summary>
    internal sealed class StartupSplashHost : IDisposable
    {
        private readonly object _sync = new();
        private readonly ManualResetEventSlim _shown = new(false);
        private readonly Thread _thread;
        private readonly Rectangle _startupBounds;
        private readonly System.Threading.Timer _failsafe;
        private StartupSplashForm? _form;
        private int _dismissRequested;

        private StartupSplashHost(Rectangle startupBounds)
        {
            _startupBounds = startupBounds;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Cinecore startup splash"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            // Let the player surface become visible even if its ready event never fires.
            _failsafe = new System.Threading.Timer(_ => Dismiss(), null, 30000, Timeout.Infinite);
        }

        public static StartupSplashHost Start(Rectangle startupBounds)
        {
            var host = new StartupSplashHost(startupBounds);
            try { host._shown.Wait(1800); } catch { }
            return host;
        }

        private void SignalShown()
        {
            try { _shown.Set(); } catch (ObjectDisposedException) { }
        }

        private void Run()
        {
            try
            {
                using var form = new StartupSplashForm(_startupBounds);
                lock (_sync)
                    _form = form;

                form.Shown += (_, __) =>
                {
                    SignalShown();
                    if (Volatile.Read(ref _dismissRequested) != 0)
                        form.BeginDismiss();
                };
                form.FormClosed += (_, __) => SignalShown();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                Dbg.Warn("Startup splash failed: " + ex.Message);
                SignalShown();
            }
            finally
            {
                lock (_sync)
                    _form = null;
            }
        }

        public void SetProgress(double value, string text)
        {
            if (Volatile.Read(ref _dismissRequested) != 0)
                return;

            StartupSplashForm? form;
            lock (_sync)
                form = _form;

            if (form == null || form.IsDisposed)
                return;

            try
            {
                if (form.IsHandleCreated)
                    form.BeginInvoke(new Action(() =>
                    {
                        if (Volatile.Read(ref _dismissRequested) == 0 && !form.IsDisposed)
                            form.SetProgress(value, text);
                    }));
            }
            catch { }
        }

        public void Dismiss()
        {
            if (Interlocked.Exchange(ref _dismissRequested, 1) != 0)
                return;

            StartupSplashForm? form;
            lock (_sync)
                form = _form;

            if (form == null || form.IsDisposed)
                return;

            try
            {
                if (form.IsHandleCreated)
                    form.BeginInvoke(new Action(form.BeginDismiss));
            }
            catch { }
        }

        public void Dispose()
        {
            Dismiss();
            try { _failsafe.Dispose(); } catch { }
            try
            {
                if (_thread.IsAlive && Thread.CurrentThread != _thread)
                    _thread.Join(1600);
            }
            catch { }
            if (!_thread.IsAlive)
                _shown.Dispose();
        }
    }

    internal sealed class StartupSplashForm : Form
    {
        private readonly CinecoreSplash _splash;
        private readonly System.Windows.Forms.Timer _fade = new() { Interval = 25 };
        private bool _dismissing;

        public StartupSplashForm(Rectangle startupBounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = startupBounds;
            BackColor = Color.FromArgb(7, 12, 18);
            TopMost = true;

            _splash = new CinecoreSplash
            {
                AutoRestart = false,
                UseSimulatedProgress = false
            };
            Controls.Add(new ElementHost { Dock = DockStyle.Fill, Child = _splash });

            _splash.Ready += (_, __) => _fade.Start();
            _fade.Tick += (_, __) =>
            {
                Opacity = Math.Max(0, Opacity - 0.07);
                if (Opacity <= 0.02)
                {
                    _fade.Stop();
                    Close();
                }
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void SetProgress(double value, string text) => _splash.SetProgress(value, text);

        public void BeginDismiss()
        {
            if (_dismissing || IsDisposed)
                return;

            _dismissing = true;
            _splash.SetProgress(100, "Pronto");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fade.Stop();
                _fade.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
