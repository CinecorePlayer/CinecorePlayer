#nullable enable
using System;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Un programma a finestra non ha uscite standard. Le librerie native che scrivono i propri avvisi
    /// su stderr (libdvdnav e libdvdread dentro libmpv lo fanno a ogni DVD) chiamano allora il runtime C
    /// con un flusso non valido, e il runtime chiude il processo: il player spariva appena inserito un DVD.
    /// Qui stdout e stderr del runtime C vengono collegati al dispositivo nullo.
    ///
    /// Secondo guaio, quello che chiudeva davvero il player con un DVD: libdvdnav, quando non riesce ad aprire
    /// il lettore come file per leggerne il nome (su Windows non riesce mai), chiama comunque close() sul
    /// descrittore non valido. Il runtime C di Windows tratta un argomento non valido come errore fatale e
    /// termina il processo, a meno che il programma non installi un proprio gestore: qui se ne installa uno
    /// che lascia proseguire, cosi' la funzione torna con il suo normale codice d'errore.
    /// </summary>
    internal static class NativeStdio
    {
        private static bool _done;
        private static readonly object Gate = new();

        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr __acrt_iob_func(uint index);

        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _fileno(IntPtr stream);

        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr freopen(string path, string mode, IntPtr stream);

        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _setmode(int fd, int mode);

        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);

        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe IntPtr _set_invalid_parameter_handler(delegate* unmanaged[Cdecl]<char*, char*, char*, uint, nuint, void> handler);

        private static int _invalidParameterReports;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static unsafe void OnInvalidParameter(char* expression, char* function, char* file, uint line, nuint reserved)
        {
            // Chiamato da thread nativi: niente che possa lanciare eccezioni. Solo i primi casi finiscono nel registro.
            try
            {
                if (System.Threading.Interlocked.Increment(ref _invalidParameterReports) <= 3)
                    Dbg.Warn("[STDIO] a native library passed an invalid argument to the C runtime (ignored)");
            }
            catch { }
        }

        private static unsafe void InstallInvalidParameterHandler()
        {
            try { _set_invalid_parameter_handler(&OnInvalidParameter); }
            catch (Exception ex) { Dbg.Warn("[STDIO] invalid-parameter handler: " + ex.Message); }
        }

        private const int TextMode = 0x4000; // _O_TEXT: i printf "stretti" sono validi solo su un flusso non Unicode

        /// <summary>Stato dei due flussi, per i registri: numero del descrittore, tipo della maniglia di Windows.</summary>
        public static string Describe()
        {
            try
            {
                string One(uint index, int which)
                {
                    IntPtr stream = __acrt_iob_func(index);
                    int fd = stream == IntPtr.Zero ? -99 : _fileno(stream);
                    return $"fd={fd} handleType={GetFileType(GetStdHandle(which))}";
                }
                return "stdout " + One(1, -11) + ", stderr " + One(2, -12);
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static void EnsureUsable(string? reason = null)
        {
            lock (Gate)
            {
                if (!_done) InstallInvalidParameterHandler();
                try
                {
                    string before = Describe();
                    foreach (uint index in new uint[] { 1, 2 })
                    {
                        IntPtr stream = __acrt_iob_func(index);
                        if (stream == IntPtr.Zero) continue;
                        int fd = _fileno(stream);
                        // Senza descrittore, o con una maniglia che Windows non riconosce: si collega al dispositivo nullo.
                        if (fd < 0)
                        {
                            // Diagnostica: con CINECORE_NATIVE_LOG gli avvisi delle librerie native finiscono in quel file.
                            string target = index == 2 && Environment.GetEnvironmentVariable("CINECORE_NATIVE_LOG") is { Length: > 0 } file ? file : "NUL";
                            if (freopen(target, "w", stream) == IntPtr.Zero)
                                Dbg.Warn("[STDIO] could not attach the C runtime stream " + index + " to NUL");
                            fd = _fileno(stream);
                        }
                        int previous = fd >= 0 ? _setmode(fd, TextMode) : -1;
                        if (previous != TextMode && previous != -1) Dbg.Warn($"[STDIO] stream {index} was in mode 0x{previous:X}: set back to text");
                    }
                    if (!_done) Dbg.Log($"[STDIO] {reason}: {before} -> {Describe()}", Dbg.LogLevel.Info);
                    _done = true;
                }
                catch (Exception ex) { Dbg.Warn("[STDIO] " + ex.Message); }
            }
        }
    }
}
