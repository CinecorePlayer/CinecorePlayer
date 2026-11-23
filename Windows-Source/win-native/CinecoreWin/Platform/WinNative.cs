using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace CinecoreMac;

/// <summary>
/// Chiamate Win32 usate dalle parti Windows dell'app Avalonia (al posto di AppKit / CoreGraphics del Mac).
/// </summary>
internal static class WinNative
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] public static extern int ShowCursor(bool show);
    [DllImport("user32")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder name, int max);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
        SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
    public const uint GW_HWNDPREV = 3;

    public static IntPtr Handle(TopLevel? top) => top?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    /// <summary>
    /// Il rettangolo della finestra come si vede (DWMWA_EXTENDED_FRAME_BOUNDS): senza i bordi invisibili per il
    /// ridimensionamento e senza scarti. Il video va messo qui, a filo: l'area client poteva partire 2 px piu' in la'.
    /// </summary>
    public static RECT VisibleRect(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0 && r.Height > 0) return r;
        return ClientScreenRect(hwnd);
    }

    /// <summary>Area client della finestra in pixel dello schermo.</summary>
    public static RECT ClientScreenRect(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var r);
        var p = new POINT();
        ClientToScreen(hwnd, ref p);
        return new RECT { Left = p.X, Top = p.Y, Right = p.X + r.Width, Bottom = p.Y + r.Height };
    }

    /// <summary>Finestre principali (visibili) di un processo.</summary>
    public static IntPtr TopWindowOf(int pid, string? titlePrefix = null)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner != pid || !IsWindowVisible(h)) return true;
            if (titlePrefix != null)
            {
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                if (!sb.ToString().StartsWith(titlePrefix, StringComparison.Ordinal)) return true;
            }
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Cartella dei dati dell'app su Windows (%APPDATA%\Cinecore): impostazioni, cassaforte, cache.</summary>
    public static string DataDir
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cinecore");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Cache locale (%LOCALAPPDATA%\Cinecore).</summary>
    public static string LocalDir
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinecore");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogDir
    {
        get
        {
            string dir = Path.Combine(LocalDir, "Logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Componenti di terze parti (mpv, ffmpeg, madVR, MPC-VR, XySubFilter, yt-dlp, rubberband): prima accanto all'app
    /// (third-parties\ copiata dal build), poi la cartella del progetto Windows (sviluppo), poi l'installazione di
    /// Cinecore Player 2026.
    /// </summary>
    public static string? ThirdParty(params string[] relative)
    {
        foreach (string root in ThirdPartyRoots())
        {
            string path = Path.Combine(root, Path.Combine(relative));
            if (File.Exists(path) || Directory.Exists(path)) return path;
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> ThirdPartyRoots()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_THIRD_PARTIES") is { Length: > 0 } forced) yield return forced;
        yield return Path.Combine(AppContext.BaseDirectory, "third-parties");
        // Sviluppo: risale fino al progetto Windows (CinecorePlayer2025_beta2\third-parties).
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "third-parties", "mpv")))
            {
                yield return Path.Combine(dir.FullName, "third-parties");
                break;
            }
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programs, "Cinecore Player 2026", "third-parties");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Cinecore Player 2026", "third-parties");
    }
}
