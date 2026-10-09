#nullable enable
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    /// <summary>
    /// Dissolvenza tra due viste: la vista com'era (fotografata dallo schermo un attimo prima del cambio) resta
    /// sopra quella nuova in una finestra a parte e svanisce. La finestra vive su un thread suo: mentre
    /// l'interfaccia costruisce e disegna la vista nuova (grafici, pagine pesanti, un CD che fa aspettare) la
    /// dissolvenza continua a scorrere regolare. Non riceve clic e non prende il fuoco.
    /// </summary>
    internal static class ViewFade
    {
        private static Cover? _current;
        // Momento in cui l'interfaccia e' tornata libera dopo il cambio di vista (0 = non ancora).
        private static long _uiReadyAt;
        private static int _idleSeen;
        private static bool _idleHooked;

        /// <summary>Da chiamare, dal thread dell'interfaccia, subito prima del cambio di vista. L'area e' in coordinate dello schermo.</summary>
        public static void Begin(Rectangle screenArea, double ms = 200)
        {
            try
            {
                if (!SystemInformation.IsMenuAnimationEnabled || screenArea.Width < 16 || screenArea.Height < 16) return;
                // Con un'altra applicazione davanti si fotograferebbe quella.
                if (Form.ActiveForm == null) return;
                var shot = new Bitmap(screenArea.Width, screenArea.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(screenArea.Location, Point.Empty, screenArea.Size);

                Interlocked.Exchange(ref _uiReadyAt, 0);
                _idleSeen = 0;
                if (!_idleHooked) { _idleHooked = true; Application.Idle += OnUiIdle; }

                var previous = Interlocked.Exchange(ref _current, null);
                previous?.RequestClose();
                using var shown = new ManualResetEventSlim(false);
                var thread = new Thread(() =>
                {
                    try
                    {
                        using var cover = new Cover(shot, screenArea, ms);
                        _current = cover;
                        // Compare gia' disegnata: mostrata trasparente, dipinta, e solo dopo resa visibile
                        // (mostrarla subito opaca dava un fotogramma nero: il lampo).
                        cover.Opacity = 0;
                        cover.Show();
                        cover.Refresh();
                        cover.Opacity = 1;
                        shown.Set();
                        Application.Run(cover);
                    }
                    catch { }
                    finally { try { shown.Set(); } catch { } }
                }) { IsBackground = true, Name = "Cinecore view fade" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                // La copertura deve essere a schermo prima che la vista sotto cambi.
                shown.Wait(250);
            }
            catch { }
        }

        // Due pause consecutive dell'interfaccia: la vista nuova e' stata costruita e disegnata.
        private static void OnUiIdle(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref _uiReadyAt) != 0) return;
            if (++_idleSeen >= 2) Interlocked.Exchange(ref _uiReadyAt, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        private sealed class Cover : Form
        {
            private readonly Bitmap _shot;
            private readonly System.Windows.Forms.Timer _timer;
            private readonly double _ms;
            private readonly long _created;
            private long _started;
            private volatile bool _closeRequested;

            public Cover(Bitmap shot, Rectangle area, double ms)
            {
                _shot = shot;
                _ms = Math.Max(60, ms);
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                Bounds = area;
                TopMost = true;
                BackColor = Color.Black;
                DoubleBuffered = true;
                _created = System.Diagnostics.Stopwatch.GetTimestamp();
                _timer = new System.Windows.Forms.Timer { Interval = 10 };
                _timer.Tick += (_, _) => Step();
                _timer.Start();
            }

            public void RequestClose() => _closeRequested = true;

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            private void Step()
            {
                if (_closeRequested) { Close(); return; }
                if (_started == 0)
                {
                    // Parte appena la vista nuova e' pronta; se l'interfaccia resta occupata, dopo 450 ms parte comunque.
                    long ready = Volatile.Read(ref _uiReadyAt);
                    if (ready == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_created).TotalMilliseconds < 450) return;
                    _started = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                double t = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds / _ms;
                if (t >= 1) { Close(); return; }
                double eased = t * t * (3 - 2 * t);
                try { Opacity = Math.Clamp(1 - eased, 0, 1); } catch { Close(); }
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e) => e.Graphics.DrawImageUnscaled(_shot, 0, 0);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Interlocked.CompareExchange(ref _current, null, this);
                    try { _timer.Stop(); _timer.Dispose(); } catch { }
                    try { _shot.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }
        }
    }
}
