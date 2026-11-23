using System;
using Avalonia;

namespace CinecoreMac;

internal static class Program
{
    // I file condivisi con Windows salvano in ApplicationData / LocalApplicationData: sul Mac .NET le fa
    // coincidere con ~/Library/Application Support (cartella CinecorePlayer2025, come su Windows).
    [System.STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) NativeDiagnostics.Crash(error);
        };
        // Chiusura con un segnale (kill, logout): senza questo Wine restava acceso e il VideoHost continuava a
        // decodificare il film in sottofondo, anche al 100% di CPU.
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ =>
        {
            VideoHostClient.ShuttingDown = true;
            CinecorePlayer2025.Utilities.IntroOutroScanService.Shutdown();
            VideoHostClient.StopWine();
        });
        StartSessionLog();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Uso normale (app aperta dal Finder): le righe di diagnosi dell'app e del VideoHost finiscono in
    /// ~/Library/Logs/Cinecore (ultime 12 sessioni), cosi' un problema visto dall'utente si puo' ricostruire dopo.
    /// Mai indirizzi con il token. Le prove impostano gia' i loro file; CINECORE_LOG=0 lo spegne.
    /// </summary>
    private static void StartSessionLog()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("CINECORE_LOG") == "0" || Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null
                || Environment.GetEnvironmentVariable("CINECORE_VIDEOHOST_LOG") != null) return;
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Cinecore");
            System.IO.Directory.CreateDirectory(dir);
            var old = new System.IO.DirectoryInfo(dir).GetFiles("*.log*");
            Array.Sort(old, (x, y) => y.LastWriteTimeUtc.CompareTo(x.LastWriteTimeUtc));
            for (int i = 33; i < old.Length; i++) try { old[i].Delete(); } catch { }
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Console.SetError(new SafeLog(System.IO.Path.Combine(dir, $"app-{stamp}.log")));
            Environment.SetEnvironmentVariable("CINECORE_VIDEOHOST_LOG", System.IO.Path.Combine(dir, $"videohost-{stamp}.log"));
            Environment.SetEnvironmentVariable("CINECORE_PRESENT_DEBUG", "1");
            Console.Error.WriteLine($"sessione {DateTime.Now:O} risparmio energetico={MacPower.LowPowerMode}");
        }
        catch { }
    }

    private sealed class SafeLog : System.IO.TextWriter
    {
        private readonly System.IO.StreamWriter _file;
        private readonly object _gate = new();
        public SafeLog(string path) { _file = new System.IO.StreamWriter(path, append: true) { AutoFlush = true }; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        private static string Clean(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"(api_key|X-Emby-Token|token)=[^&\s""]+", "$1=…", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        public override void Write(char value) { lock (_gate) _file.Write(value); }
        public override void Write(string? value) { if (value != null) lock (_gate) _file.Write(Clean(value)); }
        public override void WriteLine(string? value) { lock (_gate) _file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ", System.Globalization.CultureInfo.InvariantCulture) + Clean(value ?? "")); }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            // Metal per primo: Avalonia su macOS partirebbe da OpenGL, che Apple ha deprecato ed emula sopra Metal.
            // CINECORE_RENDER=opengl torna al vecchio percorso per un confronto.
            .With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = Environment.GetEnvironmentVariable("CINECORE_RENDER") == "opengl"
                    ? new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }
                    : new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software },
            })
            .LogToTrace();
}
