#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (!AppInstanceCoordinator.TryBecomePrimary())
            {
                // Se l'istanza esistente risponde, le consegniamo il file. Se invece e'
                // bloccata prima di aver aperto la pipe, questa istanza continua comunque:
                // perdere la richiesta di Explorer sarebbe peggio di avere due processi.
                if (AppInstanceCoordinator.TryForwardToPrimary(args))
                    return;

                if (!AppInstanceCoordinator.TryTakeOverAfterForwardFailure())
                    Dbg.Warn("Primary instance is not accepting files; continuing in recovery mode.");
            }

            // Give DirectShow/madVR physical pixels. DPI-unaware windows make a
            // 4K monitor appear as 2194x1234 at 175%, then DWM scales video again.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            WindowsOpenWithRegistration.RegisterCurrentExecutable();

            var startupScreen = Screen.FromPoint(Cursor.Position);
            var startupArea = startupScreen.WorkingArea;
            using var splash = StartupSplashHost.Start(startupScreen.Bounds);
            splash.SetProgress(14, "Inizializzo CinecorePlayer");

            using var player = new PlayerForm(startupArea)
            {
                StartPosition = FormStartPosition.Manual,
                Bounds = startupArea,
                FormBorderStyle = FormBorderStyle.Sizable,
                WindowState = FormWindowState.Maximized,
                MaximizeBox = true
            };
            player.StartupReady += (_, __) => splash.Dismiss();
            player.FormClosed += (_, __) => splash.Dismiss();
            splash.SetProgress(50, "Preparo l'interfaccia");
            player.PrepareFirstFrame();
            splash.SetProgress(86, "Carico la libreria");
            player.Shown += (_, __) => splash.SetProgress(93, "Finalizzo l'avvio");

            void DispatchExternalArgs(string[]? receivedArgs)
            {
                if (receivedArgs == null || receivedArgs.Length == 0 || player.IsDisposed)
                    return;

                void Open() => player.OpenExternalCommandLineArgs(receivedArgs);

                try
                {
                    if (player.IsHandleCreated)
                    {
                        player.BeginInvoke((Action)Open);
                        return;
                    }

                    EventHandler? handler = null;
                    handler = (_, __) =>
                    {
                        try { player.HandleCreated -= handler; } catch { }
                        try { player.BeginInvoke((Action)Open); } catch { Open(); }
                    };
                    player.HandleCreated += handler;
                }
                catch (Exception ex)
                {
                    Dbg.Warn("Open request dispatch failed: " + ex.Message);
                }
            }

            AppInstanceCoordinator.StartServer(DispatchExternalArgs);

            // Gli argomenti del primo processo vengono consegnati direttamente. OnShown
            // rimane solo come fallback e la deduplicazione nel form evita doppie aperture.
            string[] startupMediaArgs = args.Where(arg => !string.IsNullOrWhiteSpace(arg)).ToArray();
            if (startupMediaArgs.Length > 0)
            {
                // With an Explorer/Open-with launch the playback loading surface takes
                // over after the first real frame; the library-ready event may never be
                // reached because a media graph is already being built.
                player.Shown += (_, __) =>
                {
                    var handoffTimer = new System.Windows.Forms.Timer { Interval = 650 };
                    handoffTimer.Tick += (_, __) =>
                    {
                        handoffTimer.Stop();
                        handoffTimer.Dispose();
                        splash.Dismiss();
                    };
                    handoffTimer.Start();
                };
                DispatchExternalArgs(startupMediaArgs);
            }

            try
            {
                Application.Run(player);
            }
            finally
            {
                AppInstanceCoordinator.Shutdown();
            }
        }
    }
}
