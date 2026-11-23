using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace CinecoreMac;

/// <summary>
/// Avvia programmi esterni (ffmpeg, ffprobe…) senza duplicare l'app a ogni avvio. Process.Start fa un fork del
/// processo di Cinecore, che contiene mpv: con il film in riproduzione 10 avvii al secondo lo rallentavano del 18%
/// (anche con un programma che non fa nulla). Qui pochi /bin/sh di servizio, aperti una volta sola, avviano i
/// programmi al posto nostro: il fork costa a loro, non alla riproduzione. Uscita e messaggi passano da file temporanei.
/// </summary>
internal static class ProcessLauncher
{
    private const string Done = "__cinecore_done__";

    private sealed class Shell
    {
        public Process Process = null!;
        public StreamWriter Input = null!;
        public StreamReader Output = null!;
    }

    private static readonly ConcurrentBag<Shell> Idle = new();
    private static int _counter;
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "cinecore-run-" + Environment.ProcessId);

    private static Shell? NewShell()
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh", "-s") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var process = Process.Start(psi);
            if (process == null) return null;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return new Shell { Process = process, Input = process.StandardInput, Output = process.StandardOutput };
        }
        catch { return null; }
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Esegue <paramref name="exe"/> e restituisce codice d'uscita, uscita standard e messaggi. <paramref name="background"/>:
    /// priorita' di sottofondo di macOS (core ad alta efficienza, disco a bassa priorita'), per non togliere nulla al film.
    /// </summary>
    public static (int Exit, byte[] Stdout, string Stderr) Run(string exe, IEnumerable<string> args, bool background,
        int timeoutMs, CancellationToken ct, bool adjustPriority = true)
    {
        Directory.CreateDirectory(TempDir);
        int id = Interlocked.Increment(ref _counter);
        string outFile = Path.Combine(TempDir, id + ".out"), errFile = Path.Combine(TempDir, id + ".err");
        var line = new StringBuilder();
        if (adjustPriority)
        {
            if (background && File.Exists("/usr/sbin/taskpolicy")) line.Append("/usr/sbin/taskpolicy -b ");
            else line.Append("/usr/bin/nice -n 10 ");
        }
        line.Append(Quote(exe));
        foreach (var a in args) line.Append(' ').Append(Quote(a));
        line.Append(" </dev/null >").Append(Quote(outFile)).Append(" 2>").Append(Quote(errFile)).Append("; echo \"").Append(Done).Append(" $?\"");

        if (!Idle.TryTake(out var shell) || shell.Process.HasExited) shell = NewShell();
        if (shell == null) return (-1, Array.Empty<byte>(), "shell");
        bool healthy = false;
        int exit = -1;
        // Lettura bloccante sul thread di chi chiama: prima ReadLineAsync(...).Wait() teneva fermo un thread del pool e ne
        // chiedeva un altro per finire la lettura; con molti comandi insieme (analisi HDR) il pool si esauriva e con lui
        // tutto cio' che ne dipende. Scadenza e annullamento chiudono la shell, e la lettura torna subito.
        bool timedOut = false;
        using (var timer = new Timer(_ => { timedOut = true; try { shell.Process.Kill(true); } catch { } }, null, timeoutMs, Timeout.Infinite))
        using (ct.Register(() => { try { shell.Process.Kill(true); } catch { } }))
        {
            try
            {
                shell.Input.WriteLine(line.ToString());
                shell.Input.Flush();
                while (true)
                {
                    string? got = shell.Output.ReadLine();
                    if (got == null) break; // la shell e' morta (o chiusa per scadenza/annullamento)
                    if (got.StartsWith(Done, StringComparison.Ordinal))
                    {
                        int.TryParse(got.AsSpan(Done.Length).Trim(), out exit);
                        healthy = !timedOut;
                        break;
                    }
                }
            }
            catch { }
        }
        if (healthy && !ct.IsCancellationRequested) Idle.Add(shell);
        else { try { shell.Process.Kill(true); } catch { } try { shell.Process.Dispose(); } catch { } }

        byte[] stdout = Array.Empty<byte>();
        string stderr = "";
        try { if (File.Exists(outFile)) stdout = File.ReadAllBytes(outFile); } catch { }
        try { if (File.Exists(errFile)) stderr = File.ReadAllText(errFile); } catch { }
        try { File.Delete(outFile); } catch { }
        try { File.Delete(errFile); } catch { }
        ct.ThrowIfCancellationRequested();
        return (healthy ? exit : -1, stdout, stderr);
    }
}
