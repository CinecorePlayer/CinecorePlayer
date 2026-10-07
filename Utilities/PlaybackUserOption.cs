#nullable enable
using System;
using System.IO;
using System.Text.Json;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Piccolo store JSON per opzioni utente legate alla riproduzione.
    /// - Loop playback
    /// - Stretch fullscreen (ignora aspect ratio)
    /// 
    /// Nota: è volutamente indipendente da UI/Engine, così può essere letto
    /// sia dalla libreria (menu tasto destro) sia dall'engine (render/loop).
    /// </summary>
    internal static class PlaybackUserOptions
    {
        private sealed class Envelope
        {
            public bool LoopPlayback { get; set; } = false;
            public bool StretchFillFullscreen { get; set; } = false;
        }

        private static readonly object _lock = new();
        private static bool _loaded;
        private static bool _loop;
        private static bool _stretch;

        private static string OptionsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CinecorePlayer2025",
                "user_options.json");

        public static bool LoopPlayback
        {
            get
            {
                EnsureLoaded();
                lock (_lock) return _loop;
            }
            set
            {
                EnsureLoaded();
                lock (_lock)
                {
                    if (_loop == value) return;
                    _loop = value;
                    SaveNoLock();
                }
            }
        }

        public static bool StretchFillFullscreen
        {
            get
            {
                EnsureLoaded();
                lock (_lock) return _stretch;
            }
            set
            {
                EnsureLoaded();
                lock (_lock)
                {
                    if (_stretch == value) return;
                    _stretch = value;
                    SaveNoLock();
                }
            }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                LoadNoLock();
                _loaded = true;
            }
        }

        public static void Reload()
        {
            lock (_lock)
            {
                _loaded = false;
                _loop = false;
                _stretch = false;
                LoadNoLock();
                _loaded = true;
            }
        }

        private static void LoadNoLock()
        {
            try
            {
                if (!File.Exists(OptionsPath))
                    return;

                var json = File.ReadAllText(OptionsPath);
                var env = JsonSerializer.Deserialize<Envelope>(json);
                if (env == null) return;

                _loop = env.LoopPlayback;
                _stretch = env.StretchFillFullscreen;
            }
            catch
            {
                // best effort
                _loop = false;
                _stretch = false;
            }
        }

        private static void SaveNoLock()
        {
            try
            {
                var dir = Path.GetDirectoryName(OptionsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var env = new Envelope { LoopPlayback = _loop, StretchFillFullscreen = _stretch };
                var json = JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(OptionsPath, json);
            }
            catch
            {
                // best effort
            }
        }
    }
}
