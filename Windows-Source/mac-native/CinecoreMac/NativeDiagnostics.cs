using System;
using System.IO;

namespace CinecoreMac;

internal static class NativeDiagnostics
{
    private static System.Threading.Timer? _pulse;
    private static int _pending;
    /// <summary>Che cosa stava facendo l'interfaccia (es. "serie: Nome"): accompagna le attese nel registro.</summary>
    public static volatile string Context = "";

    /// <summary>
    /// Attese del thread dell'interfaccia: nelle prove tutte quelle sopra 100 ms, nell'uso normale solo quelle sopra
    /// 250 ms (finiscono in app-*.log con quello che si stava facendo, per capire i rallentamenti segnalati).
    /// </summary>
    public static void WatchDispatcher()
    {
        if (_pulse != null) return;
        double threshold = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_QA_UI_MS"), out var qaMs) ? qaMs : Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") == null ? 250 : 100;
        _pulse = new System.Threading.Timer(_ =>
        {
            if (System.Threading.Interlocked.Exchange(ref _pending, 1) != 0) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (ms > threshold) Console.Error.WriteLine($"[ui] attesa dispatcher {ms:0} ms" + (Context.Length > 0 ? " (" + Context + ")" : ""));
                System.Threading.Volatile.Write(ref _pending, 0);
            }, Avalonia.Threading.DispatcherPriority.Input);
        }, null, 100, threshold < 100 ? 20 : 100);
    }
    public static void Crash(Exception error)
    {
        try
        {
            string directory = global::CinecoreMac.PlatformPaths.CinecoreLogs;
            Directory.CreateDirectory(directory);
            // Solo tipo, parametro e stack: i messaggi possono contenere indirizzi o credenziali.
            string details = DateTimeOffset.Now.ToString("O") + " " + error.GetType().FullName;
            if (error is ArgumentException argument) details += " parameter=" + argument.ParamName;
            details += Environment.NewLine + error.StackTrace + Environment.NewLine;
            File.AppendAllText(Path.Combine(directory, "native-crash.log"), details);
        }
        catch { }
    }
}
