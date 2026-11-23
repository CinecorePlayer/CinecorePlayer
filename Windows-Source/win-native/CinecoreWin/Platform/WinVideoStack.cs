using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace CinecoreMac;

/// <summary>
/// Il video sta in una finestra a parte, SOTTO quella del player (che e' trasparente dove si vede il film): come sul Mac.
/// Qui le due cose che servono su Windows:
/// - <see cref="Surface"/>: finestra Win32 senza bordi del nostro processo in cui libmpv disegna (--wid);
/// - <see cref="KeepBelow"/>: tiene una finestra video (la nostra o quella del VideoHost) subito sotto il player,
///   senza mai attivarla. Con le chiamate asincrone un VideoHost bloccato non ferma l'interfaccia.
/// </summary>
internal static class WinVideoStack
{
    private const int WS_POPUP = unchecked((int)0x80000000), WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;
    private const uint WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21, WM_ERASEBKGND = 0x14;
    private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size; public uint Style; public IntPtr Proc; public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? Menu; public string Class; public IntPtr IconSmall;
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WndClassEx c);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("gdi32")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private static WndProc? _proc;
    private static bool _registered;
    private const string ClassName = "CinecoreVideoSurface";

    private static IntPtr SurfaceProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        // Il mouse non si ferma mai qui (sta sotto il player), e un clic non la attiva.
        if (msg == WM_NCHITTEST) return HTTRANSPARENT;
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        if (msg == WM_ERASEBKGND) return (IntPtr)1;
        return DefWindowProc(hwnd, msg, w, l);
    }

    /// <summary>Crea la finestra per libmpv (nera, senza bordi, fuori dalla barra delle applicazioni). Dal thread dell'interfaccia.</summary>
    public static IntPtr Surface(string title)
    {
        if (!_registered)
        {
            _proc = SurfaceProc;
            var c = new WndClassEx
            {
                Size = Marshal.SizeOf<WndClassEx>(), Proc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = GetModuleHandle(null),
                Background = GetStockObject(4 /* BLACK_BRUSH */), Class = ClassName,
            };
            RegisterClassEx(ref c);
            _registered = true;
        }
        return CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT, ClassName, title,
            WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, -32000, -32000, 16, 16, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
    }

    public static void Destroy(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) try { DestroyWindow(hwnd); } catch { }
    }

    /// <summary>
    /// Porta <paramref name="video"/> subito sotto la finestra del player. Con <paramref name="cover"/> le da' anche
    /// la misura dell'area client del player (libmpv; il VideoHost si misura da solo con "rect").
    /// </summary>
    public static void KeepBelow(TopLevel owner, IntPtr video, bool cover, bool async)
    {
        IntPtr main = WinNative.Handle(owner);
        if (main == IntPtr.Zero || video == IntPtr.Zero) return;
        uint flags = WinNative.SWP_NOACTIVATE | WinNative.SWP_NOOWNERZORDER | (async ? WinNative.SWP_ASYNCWINDOWPOS : 0);
        if (WinNative.IsIconic(main) || !WinNative.IsWindowVisible(main))
        {
            if (WinNative.IsWindowVisible(video)) ShowWindowAsync(video, 0 /* SW_HIDE */);
            return;
        }
        if (cover)
        {
            var r = WinNative.VisibleRect(main);
            // Gia' a posto (sotto il player, visibile, stessa misura): nessuna chiamata.
            if (WinNative.IsWindowVisible(video) && WinNative.GetWindow(video, WinNative.GW_HWNDPREV) == main
                && WinNative.GetWindowRect(video, out var now) && now.Left == r.Left && now.Top == r.Top && now.Width == r.Width && now.Height == r.Height)
                return;
            WinNative.SetWindowPos(video, main, r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height), flags | WinNative.SWP_SHOWWINDOW);
            SetCorners(video, RoundedFor(owner));
        }
        else
            WinNative.SetWindowPos(video, main, 0, 0, 0, 0, flags | WinNative.SWP_NOMOVE | WinNative.SWP_NOSIZE | WinNative.SWP_SHOWWINDOW);
    }

    private static readonly System.Collections.Generic.Dictionary<IntPtr, bool> Corners = new();

    /// <summary>Angoli del video come quelli della finestra del player: arrotondati solo con la finestra normale.</summary>
    public static bool RoundedFor(TopLevel owner) => owner is Window { WindowState: WindowState.Normal };

    [DllImport("gdi32")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private static readonly System.Collections.Generic.Dictionary<IntPtr, (int W, int H)> CornerSizes = new();

    /// <summary>Forma del video come la finestra del player (regione con angoli di 8 px per la scala), senza ombra propria.</summary>
    public static void SetCorners(IntPtr video, bool rounded)
    {
        WinNative.GetWindowRect(video, out var r);
        if (Corners.TryGetValue(video, out bool now) && now == rounded && CornerSizes.TryGetValue(video, out var size) && size == (r.Width, r.Height)) return;
        Corners[video] = rounded; CornerSizes[video] = (r.Width, r.Height);
        try
        {
            // Stessa forma della finestra del player: angoli di Windows 11 arrotondati da DWM (antialiasing).
            SetWindowRgn(video, IntPtr.Zero, true);
            int corner = rounded ? 2 /* DWMWCP_ROUND */ : 1 /* DWMWCP_DONOTROUND */;
            WinNative.DwmSetWindowAttribute(video, 33, ref corner, 4);
            int none = unchecked((int)0xFFFFFFFE);
            WinNative.DwmSetWindowAttribute(video, 34 /* DWMWA_BORDER_COLOR */, ref none, 4);
        }
        catch { }
    }

    /// <summary>La finestra video e' gia' subito sotto il player (nessun'altra finestra in mezzo).</summary>
    public static bool IsDirectlyBelow(TopLevel owner, IntPtr video)
    {
        IntPtr main = WinNative.Handle(owner);
        return main != IntPtr.Zero && video != IntPtr.Zero && WinNative.GetWindow(video, WinNative.GW_HWNDPREV) == main;
    }
}
