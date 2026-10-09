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
            Utilities.AnimationClock.EnableHighResolutionTimers();
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
            // App typeface (Manrope) for every control that does not set its own font.
            try { Application.SetDefaultFont(AppFonts.Create("Segoe UI", 9f)); } catch { }
            WindowsOpenWithRegistration.RegisterCurrentExecutable();

            // La lingua salvata serve gia' alla splash: si legge prima di mostrarla.
            try
            {
                string extras = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "extras.json");
                if (System.IO.File.Exists(extras))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(extras));
                    if (doc.RootElement.TryGetProperty("Language", out var lang) && lang.ValueKind == System.Text.Json.JsonValueKind.String)
                        global::CinecorePlayer2025.Utilities.AppLanguage.Current = global::CinecorePlayer2025.Utilities.AppLanguage.Normalize(lang.GetString()?.ToLowerInvariant());
                }
            }
            catch { }

            var startupScreen = Screen.FromPoint(Cursor.Position);
            var startupArea = startupScreen.WorkingArea;
            using var splash = StartupSplashHost.Start(startupScreen.Bounds);
            splash.SetProgress(14, global::CinecorePlayer2025.Utilities.AppLanguage.T("Inizializzo CinecorePlayer", "Starting CinecorePlayer"));

            // "Avvia a schermo intero": senza bordi fin dalla creazione (PlayerForm la porta a tutto
            // schermo quando nasce la finestra), invece che massimizzata con barra del titolo.
            bool startFullscreen = global::CinecorePlayer2025.Utilities.StartupPreferences.StartFullscreen;
            using var player = new PlayerForm(startupArea)
            {
                StartPosition = FormStartPosition.Manual,
                Bounds = startFullscreen ? startupScreen.Bounds : startupArea,
                FormBorderStyle = startFullscreen ? FormBorderStyle.None : FormBorderStyle.Sizable,
                WindowState = startFullscreen ? FormWindowState.Normal : FormWindowState.Maximized,
                TopMost = startFullscreen,
                MaximizeBox = true
            };
            player.StartupReady += (_, __) => splash.Dismiss();
            player.FormClosed += (_, __) => splash.Dismiss();
            splash.SetProgress(50, global::CinecorePlayer2025.Utilities.AppLanguage.T("Preparo l'interfaccia", "Preparing the interface"));
            player.PrepareFirstFrame();
            splash.SetProgress(86, global::CinecorePlayer2025.Utilities.AppLanguage.T("Carico la libreria", "Loading the library"));
            player.Shown += (_, __) => splash.SetProgress(93, global::CinecorePlayer2025.Utilities.AppLanguage.T("Finalizzo l'avvio", "Finishing startup"));

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
            string[] startupMediaArgs = args.Where(arg => !string.IsNullOrWhiteSpace(arg) &&
                                                          !string.Equals(arg, "--spotlight", StringComparison.OrdinalIgnoreCase)).ToArray();
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
