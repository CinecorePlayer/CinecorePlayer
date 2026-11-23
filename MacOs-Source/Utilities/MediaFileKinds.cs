#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Elenco unico delle estensioni audio. Prima ogni parte del lettore aveva il suo: .dtshd, .thd, .mka e simili
    /// non erano "audio" per il lettore, che li apriva come film (schermata pre-film in attesa di Play).
    /// </summary>
    internal static class MediaFileKinds
    {
        // Tutti formati che FFmpeg (motore Cinecore e mpv) legge come flussi audio puri.
        internal static readonly string[] AudioExtensions =
        {
            ".mp3", ".mp2", ".flac", ".wav", ".w64", ".aif", ".aiff", ".aifc", ".caf",
            ".ogg", ".oga", ".opus", ".spx", ".m4a", ".m4b", ".aac", ".wma", ".alac", ".ape", ".wv", ".tta", ".mpc",
            ".dsf", ".dff", ".mka",
            // Audio da cinema estratto: DTS / DTS-HD, Dolby Digital (Plus), TrueHD / MLP.
            ".dts", ".dtshd", ".dtsma", ".ac3", ".eac3", ".ec3", ".thd", ".truehd", ".mlp"
        };

        private static readonly HashSet<string> AudioSet = new(AudioExtensions, StringComparer.OrdinalIgnoreCase);

        internal static bool IsAudioExtension(string? extension) => !string.IsNullOrEmpty(extension) && AudioSet.Contains(extension);

        internal static bool IsAudioPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return IsAudioExtension(Path.GetExtension(path)); } catch { return false; }
        }
    }
}
