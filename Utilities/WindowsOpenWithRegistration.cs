#nullable enable
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace CinecorePlayer2025.Utilities
{
    internal static class WindowsOpenWithRegistration
    {
        private const string ApplicationName = "CinecorePlayer2025.exe";
        private const string ProgId = "CinecorePlayer2025.Media";
        private const string CapabilitiesPath = @"Software\CinecorePlayer2025\Capabilities";

        private static readonly string[] SupportedExtensions =
        {
            ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m2ts", ".ts", ".iso",
            ".mp3", ".flac", ".mka", ".aac", ".ogg", ".wav", ".wma", ".m4a", ".opus", ".dts", ".ac3", ".eac3",
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp"
        };

        public static void RegisterCurrentExecutable()
        {
            if (!OperatingSystem.IsWindows())
                return;

            string executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                return;

            string command = "\"" + executable + "\" \"%1\"";
            string? shellIcon = AssetIconService.EnsureShellIconFile();
            string iconRegistration = !string.IsNullOrWhiteSpace(shellIcon) && File.Exists(shellIcon)
                ? "\"" + shellIcon + "\""
                : "\"" + executable + "\",0";
            string applicationRoot = @"Software\Classes\Applications\" + ApplicationName;

            try
            {
                using (RegistryKey? app = Registry.CurrentUser.CreateSubKey(applicationRoot))
                {
                    app?.SetValue("FriendlyAppName", "Cinecore Player 2025", RegistryValueKind.String);
                    app?.SetValue("AppUserModelID", "Cinecore.Player.2025", RegistryValueKind.String);
                }

                using (RegistryKey? commandKey = Registry.CurrentUser.CreateSubKey(applicationRoot + @"\shell\open\command"))
                    commandKey?.SetValue(string.Empty, command, RegistryValueKind.String);

                using (RegistryKey? iconKey = Registry.CurrentUser.CreateSubKey(applicationRoot + @"\DefaultIcon"))
                    iconKey?.SetValue(string.Empty, iconRegistration, RegistryValueKind.String);

                using (RegistryKey? supported = Registry.CurrentUser.CreateSubKey(applicationRoot + @"\SupportedTypes"))
                {
                    foreach (string extension in SupportedExtensions)
                        supported?.SetValue(extension, string.Empty, RegistryValueKind.String);
                }

                using (RegistryKey? prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
                    prog?.SetValue(string.Empty, "Cinecore media", RegistryValueKind.String);
                using (RegistryKey? progCommand = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"))
                    progCommand?.SetValue(string.Empty, command, RegistryValueKind.String);
                using (RegistryKey? progIcon = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + @"\DefaultIcon"))
                    progIcon?.SetValue(string.Empty, iconRegistration, RegistryValueKind.String);

                // OpenWithProgids rende l'app una candidata esplicita per ogni formato,
                // senza sostituire l'app predefinita scelta dall'utente.
                foreach (string extension in SupportedExtensions)
                {
                    using RegistryKey? openWith = Registry.CurrentUser.CreateSubKey(
                        @"Software\Classes\" + extension + @"\OpenWithProgids");
                    openWith?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                }

                using (RegistryKey? capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
                {
                    capabilities?.SetValue("ApplicationName", "Cinecore Player 2025", RegistryValueKind.String);
                    capabilities?.SetValue("ApplicationDescription", "Player multimediale Cinecore", RegistryValueKind.String);
                }

                using (RegistryKey? associations = Registry.CurrentUser.CreateSubKey(CapabilitiesPath + @"\FileAssociations"))
                {
                    foreach (string extension in SupportedExtensions)
                        associations?.SetValue(extension, ProgId, RegistryValueKind.String);
                }

                using (RegistryKey? registeredApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                    registeredApps?.SetValue("Cinecore Player 2025", CapabilitiesPath, RegistryValueKind.String);

                using (RegistryKey? appPath = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + ApplicationName))
                {
                    appPath?.SetValue(string.Empty, executable, RegistryValueKind.String);
                    appPath?.SetValue("Path", Path.GetDirectoryName(executable) ?? string.Empty, RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("Windows Open With registration failed: " + ex.Message);
            }
        }
    }
}
