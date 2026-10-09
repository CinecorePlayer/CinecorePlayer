#nullable enable
using System;
using System.IO;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>Choices read before the main window exists (the rest of the settings load after it is built).</summary>
    internal static class StartupPreferences
    {
        private static bool? _startFullscreen;
        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "start-fullscreen.txt");

        /// <summary>Open the player already full screen instead of in a window.</summary>
        public static bool StartFullscreen
        {
            get
            {
                if (_startFullscreen.HasValue) return _startFullscreen.Value;
                try { _startFullscreen = File.Exists(FilePath) && File.ReadAllText(FilePath).Trim() == "1"; }
                catch { _startFullscreen = false; }
                return _startFullscreen.Value;
            }
            set
            {
                _startFullscreen = value;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, value ? "1" : "0");
                }
                catch (Exception ex) { Dbg.Warn("[SETTINGS] start-fullscreen save failed: " + ex.Message); }
            }
        }
    }
}
