using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Avvio su Windows: registro della sessione in %LOCALAPPDATA%\Cinecore\Logs, una sola istanza (un file aperto da
/// Esplora file mentre il player e' gia' aperto arriva a quello aperto), rendering di Avalonia con la composizione
/// di Windows (serve alla finestra trasparente sopra il video).
/// </summary>
internal static class Program
{
    private const string MutexName = "Cinecore.Avalonia.SingleInstance";
    private const string PipeName = "Cinecore.Avalonia.Open";

    [STAThread]
    public static int Main(string[] args)
    {
        // Timer di Windows a 1 ms: le animazioni (DispatcherTimer da 16 ms) scattavano a passi di 15,6 ms, cioe' un
        // fotogramma si e uno no a 31 ms. Sul Mac i timer sono precisi: da qui "meno fluido che su Mac".
        try { TimeBeginPeriod(1); } catch { }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) NativeDiagnostics.Crash(error);
        };
        // Un'istanza sola (le prove con CINECORE_QA_* possono girare accanto a quella dell'utente).
        bool qa = Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null || Environment.GetEnvironmentVariable("CINECORE_MULTI") == "1";
        Mutex? mutex = null;
        if (!qa)
        {
            mutex = new Mutex(true, MutexName, out bool first);
            if (!first) { ForwardAndExit(mutex, args); return 0; }
        }
        StartSessionLog();
        // Blu-ray protetti: libbluray (dentro libmpv e LAV) apre libaacs.dll per nome, dai percorsi di ricerca.
        try
        {
            if (WinNative.ThirdParty("libaacs") is { } aacs)
                Environment.SetEnvironmentVariable("PATH", aacs + ";" + Environment.GetEnvironmentVariable("PATH"));
        }
        catch { }
        if (!qa) ListenForFiles();
        // Tipi di file e icone come la versione Windows (in sottofondo, solo per CinecorePlayer2026.exe).
        if (!qa) System.Threading.Tasks.Task.Run(FileAssociations.Register);
        if (Environment.GetEnvironmentVariable("CINECORE_QA_STALL") == "1") WatchUiThread();
        // La libreria si legge mentre Avalonia prepara grafica e finestra (prima era sul thread dell'interfaccia).
        try { LibraryStore.Prewarm(); } catch { }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally
        {
            VideoHostClient.ShuttingDown = true;
            try { CinecorePlayer2025.Utilities.IntroOutroScanService.Shutdown(); } catch { }
            try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    private static void ForwardAndExit(Mutex mutex, string[] args)
    {
        mutex.Dispose();
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(args.Length == 0 ? "" : string.Join("|", args.Select(Path.GetFullPath)));
            writer.Flush();
        }
        catch { }
    }

    private static void ListenForFiles()
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string line = reader.ReadLine() ?? "";
                    var paths = line.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window }) return;
                        if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                        window.Activate();
                        if (paths.Count > 0) window.OpenFiles(paths);
                    });
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "istanza unica" }.Start();
    }

    /// <summary>Prove: segnala ogni volta che il thread dell'interfaccia resta occupato piu' di 50 ms.</summary>
    private static void WatchUiThread()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        new Thread(() =>
        {
            // Il dispatcher va creato dal thread principale: aspetta che esista la finestra.
            while (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: not null }) Thread.Sleep(5);
            Console.Error.WriteLine($"[stall] sentinella attiva da t={clock.ElapsedMilliseconds} ms");
            while (true)
            {
                Thread.Sleep(8);
                long sent = clock.ElapsedMilliseconds;
                using var done = new ManualResetEventSlim();
                try { Dispatcher.UIThread.Post(() => done.Set(), DispatcherPriority.Send); } catch { continue; }
                done.Wait();
                long wait = clock.ElapsedMilliseconds - sent;
                if (wait > 50) Console.Error.WriteLine($"[stall] {wait} ms da t={sent} ms ({DateTime.Now.AddMilliseconds(-wait):HH:mm:ss.fff})");
            }
        }) { IsBackground = true, Name = "sentinella UI" }.Start();
    }

    private static void StartSessionLog()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("CINECORE_LOG") == "0" || Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null
                || Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST_LOG") != null) return;
            string dir = PlatformPaths.CinecoreLogs;
            Directory.CreateDirectory(dir);
            var old = new DirectoryInfo(dir).GetFiles("*.log*");
            Array.Sort(old, (x, y) => y.LastWriteTimeUtc.CompareTo(x.LastWriteTimeUtc));
            for (int i = 33; i < old.Length; i++) try { old[i].Delete(); } catch { }
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Console.SetError(new SafeLog(Path.Combine(dir, $"app-{stamp}.log")));
            Environment.SetEnvironmentVariable("CINECORE_VIDEOHOST_LOG", Path.Combine(dir, $"videohost-{stamp}.log"));
            Console.Error.WriteLine($"sessione {DateTime.Now:O} Windows {Environment.OSVersion.Version} risparmio energetico={MacPower.LowPowerMode}");
        }
        catch { }
    }

    private sealed class SafeLog : TextWriter
    {
        private readonly StreamWriter _file;
        private readonly object _gate = new();
        public SafeLog(string path) { _file = new StreamWriter(path, append: true) { AutoFlush = true }; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        private static string Clean(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"(api_key|X-Emby-Token|token)=[^&\s""]+", "$1=…", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        public override void Write(char value) { lock (_gate) _file.Write(value); }
        public override void Write(string? value) { if (value != null) lock (_gate) _file.Write(Clean(value)); }
        public override void WriteLine(string? value) { lock (_gate) _file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ", System.Globalization.CultureInfo.InvariantCulture) + Clean(value ?? "")); }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            // La finestra del player e' trasparente sopra il video: serve la composizione di Windows (DirectComposition).
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[] { Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface },
            })
            .LogToTrace();
}
