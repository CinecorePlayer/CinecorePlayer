#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CinecorePlayer2025.Utilities;
using Microsoft.Win32;

namespace CinecoreMac;

/// <summary>
/// Tipi di file e "Apri con" come la versione Windows (Utilities/WindowsOpenWithRegistration.cs): stesse ProgId, stesse
/// icone per video, musica e foto (Assets\Shell\*.ico) e lo stesso gestore di icone per file (CinecoreShellIcons.dll).
/// Non cambia l'app predefinita scelta dall'utente: Cinecore diventa solo una candidata per ogni formato.
/// </summary>
internal static class FileAssociations
{
    private const string ApplicationName = "CinecorePlayer2026.exe";
    private const string ProgId = "CinecorePlayer2025.Media";
    private const string CapabilitiesPath = @"Software\CinecorePlayer2025\Capabilities";
    private const string ShellIconHandlerClassId = "{9C570A1E-4A75-4D20-9106-7AC12D98BE31}";
    private static readonly string[] Photos = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };
    private static readonly string[] Extensions = new[] { ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m2ts", ".ts", ".iso" }
        .Concat(Photos).Concat(MediaFileKinds.AudioExtensions).ToArray();

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static string Kind(string ext) => MediaFileKinds.IsAudioExtension(ext) ? "music" : Photos.Contains(ext, StringComparer.OrdinalIgnoreCase) ? "photo" : "video";
    private static string ShellDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "shell");
    private static string AssetsShell => Path.Combine(AppContext.BaseDirectory, "Assets", "Shell");

    private static bool _changed;

    private static string? IconFile(string kind)
    {
        string source = Path.Combine(AssetsShell, kind + ".ico");
        if (!File.Exists(source)) return null;
        Directory.CreateDirectory(ShellDir);
        string target = Path.Combine(ShellDir, "cinecore-" + kind + "-v6.ico");
        if (!File.Exists(target) || File.GetLastWriteTimeUtc(target) < File.GetLastWriteTimeUtc(source)) { File.Copy(source, target, true); _changed = true; }
        return target;
    }

    private static string? HandlerFile()
    {
        string source = Path.Combine(AssetsShell, "CinecoreShellIcons.dll");
        if (!Environment.Is64BitProcess || !File.Exists(source)) return null;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)))[..16];
        Directory.CreateDirectory(ShellDir);
        // Esplora risorse puo' tenere caricata la DLL di prima: nome diverso per ogni versione.
        string target = Path.Combine(ShellDir, "cinecore-icons-" + hash + ".dll");
        if (!File.Exists(target)) File.Copy(source, target);
        return target;
    }

    private static void Set(string path, string? name, object value, ref bool changed, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        if (key == null) return;
        if (kind == RegistryValueKind.String && !string.Equals(key.GetValue(name ?? "") as string, value as string, StringComparison.Ordinal)) changed = true;
        key.SetValue(name ?? "", value, kind);
    }

    public static void Register()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || !File.Exists(exe) || !exe.EndsWith(ApplicationName, StringComparison.OrdinalIgnoreCase)) return;
            string command = "\"" + exe + "\" \"%1\"";
            string appRoot = @"Software\Classes\Applications\" + ApplicationName;
            bool changed = false;
            string fallbackIcon = IconFile("video") is { } v ? "\"" + v + "\"" : "\"" + exe + "\",0";
            foreach (string kind in new[] { "video", "music", "photo" })
            {
                string id = @"Software\Classes\CinecorePlayer2025." + kind;
                Set(id, null, "Cinecore " + kind, ref changed);
                Set(id + @"\shell\open\command", null, command, ref changed);
                Set(id + @"\DefaultIcon", null, IconFile(kind) is { } f ? "\"" + f + "\"" : fallbackIcon, ref changed);
            }
            string appIcon = fallbackIcon;
            if (HandlerFile() is { } handler)
            {
                string server = @"Software\Classes\CLSID\" + ShellIconHandlerClassId + @"\InprocServer32";
                Set(server, null, handler, ref changed);
                Set(server, "ThreadingModel", "Apartment", ref changed);
                foreach (string root in new[] { appRoot, @"Software\Classes\" + ProgId })
                    Set(root + @"\shellex\IconHandler", null, ShellIconHandlerClassId, ref changed);
                appIcon = "%1";
            }
            Set(appRoot, "FriendlyAppName", "Cinecore Player 2026", ref changed);
            Set(appRoot, "AppUserModelID", "Cinecore.Player.2025", ref changed);
            Set(appRoot + @"\shell\open\command", null, command, ref changed);
            Set(appRoot + @"\DefaultIcon", null, appIcon, ref changed);
            foreach (string ext in Extensions) Set(appRoot + @"\SupportedTypes", ext, "", ref changed);
            Set(@"Software\Classes\" + ProgId, null, "Cinecore media", ref changed);
            Set(@"Software\Classes\" + ProgId + @"\shell\open\command", null, command, ref changed);
            Set(@"Software\Classes\" + ProgId + @"\DefaultIcon", null, appIcon, ref changed);
            foreach (string ext in Extensions)
            {
                using var open = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids");
                open?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                open?.SetValue("CinecorePlayer2025." + Kind(ext), Array.Empty<byte>(), RegistryValueKind.None);
                Set(CapabilitiesPath + @"\FileAssociations", ext, "CinecorePlayer2025." + Kind(ext), ref changed);
            }
            Set(CapabilitiesPath, "ApplicationName", "Cinecore Player 2026", ref changed);
            Set(CapabilitiesPath, "ApplicationDescription", "Player multimediale Cinecore", ref changed);
            Set(@"Software\RegisteredApplications", "Cinecore Player 2025", CapabilitiesPath, ref changed);
            string appPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + ApplicationName;
            Set(appPath, null, exe, ref changed);
            Set(appPath, "Path", Path.GetDirectoryName(exe) ?? "", ref changed);
            if (changed || _changed) SHChangeNotify(0x08000000 /* ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { Console.Error.WriteLine("[file] registrazione tipi di file: " + ex.Message); }
    }
}
