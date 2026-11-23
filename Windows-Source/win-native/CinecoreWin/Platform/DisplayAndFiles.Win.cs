using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Frequenza dello schermo adatta al film (23,976 Hz per i film a 24000/1001): lo stesso commutatore CCD del player
/// Windows (RationalDisplayModeSwitcher), che conserva i rapporti esatti. Gli schermi si indicano con un numero
/// (indice + 1 nell'elenco dei monitor), come gli ID di CoreGraphics sul Mac.
/// </summary>
internal static class MacDisplayRate
{
    private static readonly RationalDisplayModeSwitcher Switcher = new();
    private static readonly List<string> Names = new();
    private static readonly object Gate = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public WinNative.RECT Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    public static bool Enabled { get => MacSettings.Get("matchRefresh", true); set => MacSettings.Set("matchRefresh", value); }

    /// <summary>Nome GDI (\\.\DISPLAYn) dello schermo con questo numero.</summary>
    public static string? DeviceName(uint display)
    {
        lock (Gate) return display >= 1 && display <= Names.Count ? Names[(int)display - 1] : null;
    }

    /// <summary>Schermo sotto il punto (pixel dello schermo su Windows).</summary>
    public static uint DisplayAt(double x, double y)
    {
        try
        {
            IntPtr monitor = WinNative.MonitorFromPoint(new WinNative.POINT { X = (int)x, Y = (int)y }, 2 /* NEAREST */);
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return 0;
            lock (Gate)
            {
                int index = Names.IndexOf(info.Device);
                if (index < 0) { Names.Add(info.Device); index = Names.Count - 1; }
                return (uint)(index + 1);
            }
        }
        catch { return 0; }
    }

    /// <summary>Schermo che contiene la finestra.</summary>
    public static uint DisplayOf(TopLevel top)
    {
        IntPtr hwnd = WinNative.Handle(top);
        if (hwnd == IntPtr.Zero) return 0;
        WinNative.GetWindowRect(hwnd, out var r);
        return DisplayAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    public static string? Match(uint display, double fps)
    {
        if (!Enabled || DeviceName(display) is not { } device) return null;
        if (RationalDisplayModeSwitcher.GetFrameRate(fps).Numerator == 0) return null;
        double before = RationalDisplayModeSwitcher.CurrentHz(device);
        if (RationalDisplayModeSwitcher.MatchesCadence(before, fps)) return null;
        bool ok = Switcher.SwitchToMatching(device, fps);
        double after = RationalDisplayModeSwitcher.CurrentHz(device);
        return ok ? $"schermo {device} da {before:0.###} a {after:0.###} Hz per {fps:0.###} fps"
            : $"nessuna frequenza adatta a {fps:0.###} fps (schermo {device} a {before:0.###} Hz)";
    }

    public static double CurrentHz(uint display) => DeviceName(display) is { } device ? RationalDisplayModeSwitcher.CurrentHz(device) : 0;

    public static string? Restore()
    {
        if (!Switcher.Changed) return null;
        Switcher.RestoreIfChanged();
        return "frequenza dello schermo ripristinata";
    }
}

/// <summary>Azioni sui file con gli strumenti di Windows (Esplora file, Cestino, Condividi, Apri con, stampa).</summary>
internal static class MacFileActions
{
    public static bool Share(TopLevel top, string path, Rect anchor)
    {
        try
        {
            IntPtr hwnd = WinNative.Handle(top);
            if (hwnd == IntPtr.Zero) return false;
            WindowsShare.ShowForFile(hwnd, path);
            return true;
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] share: " + ex.Message); return false; }
    }

    /// <summary>Negli appunti il file e l'immagine (si incolla sia in Esplora file sia nei programmi di immagini).</summary>
    public static bool CopyImage(string path)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
            package.SetStorageItems(new Windows.Storage.IStorageItem[] { file });
            package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            return true;
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] copy: " + ex.Message); return false; }
    }

    /// <summary>Nel Cestino di Windows (si recupera da li').</summary>
    public static bool Trash(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return !File.Exists(path);
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] trash: " + ex.Message); return false; }
    }

    /// <summary>Su Windows la scelta del programma la fa la finestra "Apri con" di sistema: una sola voce.</summary>
    public static List<(string Name, string AppPath)> OpenWithApps(string path) => new() { ("Scegli un'app…", "openas") };

    public static void OpenWith(string appPath, string file)
    {
        try
        {
            if (appPath == "openas")
                Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + Path.GetFullPath(file)) { UseShellExecute = false })?.Dispose();
            else
            {
                var psi = new ProcessStartInfo(appPath) { UseShellExecute = false };
                psi.ArgumentList.Add(Path.GetFullPath(file));
                Process.Start(psi)?.Dispose();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] open with: " + ex.Message); }
    }

    public static void OpenFolder(string folder)
    {
        try { Directory.CreateDirectory(folder); } catch { }
        try { Process.Start(new ProcessStartInfo(Path.GetFullPath(folder)) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] folder: " + ex.Message); }
    }

    public static void RevealInFinder(string file) => PlatformShell.Reveal(file);

    /// <summary>Il verbo "print" di Windows (procedura di stampa del programma predefinito).</summary>
    public static void Print(string file)
    {
        try { Process.Start(new ProcessStartInfo(Path.GetFullPath(file)) { UseShellExecute = true, Verb = "print" })?.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] print: " + ex.Message); }
    }
}
