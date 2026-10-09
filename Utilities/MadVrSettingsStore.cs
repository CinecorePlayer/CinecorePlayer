#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CinecorePlayer2025.Utilities
{
    // Lettura/scrittura delle impostazioni di madVR senza il suo pannello: IMadVRSettings
    // accetta i nomi brevi delle voci (es. "chromaUp") e rifiuta i valori non validi.
    // Durante la riproduzione si scrive sull'istanza che sta disegnando (le modifiche fatte
    // su un'altra istanza non la raggiungono subito); altrimenti su un'istanza privata,
    // che salva comunque su settings.bin.
    internal static class MadVrSettingsStore
    {
        [ComImport, Guid("6F8A566C-4E19-439E-8F07-20E46ED06DEE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMadVRSettings
        {
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetRevision(out long revision);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsExport(out IntPtr buffer, out int size);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsImport(IntPtr buffer, int size);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetString([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetInteger([MarshalAs(UnmanagedType.LPWStr)] string path, int value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetBoolean([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetString([MarshalAs(UnmanagedType.LPWStr)] string path, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, ref int length);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetInteger([MarshalAs(UnmanagedType.LPWStr)] string path, out int value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetBoolean([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetBinary([MarshalAs(UnmanagedType.LPWStr)] string path, out IntPtr value, out int size);
        }

        private static object? _standalone;
        private static bool _standaloneFailed;

        public static object? LiveRenderer { get; set; }
        public static bool IsLive => LiveRenderer != null;

        private static IMadVRSettings? Settings()
        {
            try { if (LiveRenderer is IMadVRSettings live) return live; } catch { }
            if (_standalone == null && !_standaloneFailed)
            {
                try { _standalone = Engines.DirectShowUnifiedEngine.CreateBundledMadVrRenderer(); }
                catch (Exception ex) { Dbg.Warn("[madVR] Settings store: " + ex.Message); }
                _standaloneFailed = _standalone == null;
            }
            try { return _standalone as IMadVRSettings; } catch { return null; }
        }

        public static bool Available => Settings() != null;

        public static bool? GetBool(string path)
        {
            try { return Settings() is { } s && s.SettingsGetBoolean(path, out bool v) ? v : null; } catch { return null; }
        }

        public static int? GetInt(string path)
        {
            try { return Settings() is { } s && s.SettingsGetInteger(path, out int v) ? v : null; } catch { return null; }
        }

        public static string? GetString(string path)
        {
            try
            {
                if (Settings() is not { } s) return null;
                var text = new StringBuilder(512); int length = text.Capacity;
                return s.SettingsGetString(path, text, ref length) ? text.ToString() : null;
            }
            catch { return null; }
        }

        public static bool SetBool(string path, bool value)
        {
            bool ok;
            try { ok = Settings() is { } s && s.SettingsSetBoolean(path, value) && GetBool(path) == value; } catch { ok = false; }
            return Commit(ok, path);
        }

        public static bool SetInt(string path, int value)
        {
            bool ok;
            try { ok = Settings() is { } s && s.SettingsSetInteger(path, value) && GetInt(path) == value; } catch { ok = false; }
            return Commit(ok, path);
        }

        public static bool SetString(string path, string value)
        {
            bool ok;
            try { ok = Settings() is { } s && s.SettingsSetString(path, value) && GetString(path) == value; } catch { ok = false; }
            return Commit(ok, path);
        }

        // Un'istanza madVR rende visibili (e salva) le proprie modifiche alle altre istanze
        // solo quando viene rilasciata. Il rilascio avviene dopo una breve pausa senza
        // modifiche (ricrearla a ogni clic costerebbe 1-2 s) e comunque prima di creare il
        // renderer per un video (FlushPending), cosi' il video vede sempre le modifiche.
        private static System.Windows.Forms.Timer? _flushTimer;

        private static bool Commit(bool ok, string path)
        {
            if (!ok) Dbg.Warn("[madVR] Settings store: write refused for " + path);
            if (LiveRenderer == null && _standalone != null)
            {
                _flushTimer ??= CreateFlushTimer();
                _flushTimer.Stop();
                _flushTimer.Start();
            }
            return ok;
        }

        private static System.Windows.Forms.Timer CreateFlushTimer()
        {
            var timer = new System.Windows.Forms.Timer { Interval = 900 };
            timer.Tick += (_, _) => FlushPending();
            return timer;
        }

        public static void FlushPending()
        {
            _flushTimer?.Stop();
            if (_standalone == null) return;
            try { Marshal.FinalReleaseComObject(_standalone); } catch { }
            _standalone = null;
        }
    }
}
