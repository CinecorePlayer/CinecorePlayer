using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace CinecoreMac;

/// <summary>
/// Cornice della finestra su Windows: niente barra dei menu del Mac ne' Space. Le funzioni restano (stessa interfaccia)
/// ma fanno solo cio' che su Windows ha senso.
/// </summary>
internal static class MacWindowChrome
{
    public static void DeactivateApp() { }

    /// <summary>Icona della finestra: quella dell'eseguibile (Assets\cinecore.ico se c'e').</summary>
    public static void SetApplicationIcon() { }

    /// <summary>
    /// Barra del titolo di Windows con i colori dell'app (DWM): scura o chiara come il tema, fondo uguale al pannello,
    /// angoli arrotondati di Windows 11 sempre (anche con la finestra trasparente sopra il video).
    /// </summary>
    /// <summary>
    /// Forma della finestra su Windows: niente bordo ne' contorno di DWM (il riquadro del bordo di Windows 11 copriva il
    /// video con una fascia scura di 2 px), angoli arrotondati con una regione identica a quella delle finestre video,
    /// squadrati da ingrandita e a schermo intero. La finestra resta ridimensionabile e si aggancia ai lati.
    /// </summary>
    public static void MakeTitlebarClear(TopLevel top)
    {
        IntPtr hwnd = WinNative.Handle(top);
        if (hwnd == IntPtr.Zero) return;
        try
        {
            // Barra del titolo come tutte le app di Windows: chiara o scura secondo il tema di Windows
            // (Impostazioni > Personalizzazione > Colori), colori di sistema (prima era il colore del pannello dell'app).
            int dark = WindowsAppsDark() ? 1 : 0;
            WinNative.DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, 4);
            // Angoli arrotondati di Windows 11 (DWM, con antialiasing); il video sotto e' arrotondato in basso uguale.
            // A schermo intero (e ingrandita) angoli squadrati e nessun bordo: prima restavano arrotondati e col filo
            // grigio, e agli angoli dello schermo si vedeva cio' che c'era dietro.
            bool windowed = top is not Avalonia.Controls.Window w || w.WindowState == Avalonia.Controls.WindowState.Normal;
            int round = windowed ? 2 /* DWMWCP_ROUND */ : 1 /* DWMWCP_DONOTROUND */;
            WinNative.DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4);
            int systemColor = unchecked((int)0xFFFFFFFF) /* DWMWA_COLOR_DEFAULT */;
            // Bordo: un filo fisso e definito, uguale con la finestra attiva o no (senza bordo la finestra si confondeva
            // con cio' che c'e' dietro; quello di sistema cambia colore con lo stato attivo e sopra il video era una
            // fascia scura). Grigio scuro col tema scuro, grigio chiaro col tema chiaro.
            int border = !windowed ? unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */ : dark == 1 ? Colorref(Avalonia.Media.Color.FromRgb(64, 64, 66)) : Colorref(Avalonia.Media.Color.FromRgb(196, 196, 200));
            WinNative.DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, 4);
            WinNative.DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref systemColor, 4);
            WinNative.DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref systemColor, 4);
        }
        catch { }
    }

    /// <summary>Tema delle app di Windows (AppsUseLightTheme = 0: scuro).</summary>
    private static bool WindowsAppsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Margins { public int L, R, T, B; }
    [System.Runtime.InteropServices.DllImport("dwmapi")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);

    private static int Colorref(Avalonia.Media.Color c) => c.R | c.G << 8 | c.B << 16;

    private static bool _themeHooked;
    /// <summary>Dalla finestra principale all'apertura: colori ora e a ogni cambio di tema.</summary>
    public static void FollowTheme(TopLevel top)
    {
        MakeTitlebarClear(top);
        if (_themeHooked) return;
        _themeHooked = true;
        UiTheme.Changed += () => MakeTitlebarClear(top);
        // Ingrandita, schermo intero, ritorno in finestra: Windows puo' rimettere angoli e bordo di serie.
        if (top is Window w)
        {
            w.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Avalonia.Threading.Dispatcher.UIThread.Post(() => MakeTitlebarClear(top)); };
            w.Activated += (_, _) => MakeTitlebarClear(top);
        }
    }
    public static void SetWindowAlpha(TopLevel top, double alpha) { }
    internal static void SetWindowShadow(TopLevel top, bool visible) { }
    internal static bool IsNativeFullscreen(TopLevel top) => false;
    public static double FullscreenRevealHeight(TopLevel top) => 0;
    public static double VisibleFullscreenTitlebarBottom(TopLevel top, bool diagnostics = false, bool matchLibrary = false) => 0;

    /// <summary>Il mouse dal bordo in alto della finestra (unita' di Avalonia), anche fuori dalla finestra.</summary>
    public static double? PointerYFromTop(TopLevel top)
    {
        IntPtr hwnd = WinNative.Handle(top);
        if (hwnd == IntPtr.Zero || !WinNative.GetCursorPos(out var p)) return null;
        var client = WinNative.ClientScreenRect(hwnd);
        return (p.Y - client.Top) / Math.Max(0.1, top.RenderScaling);
    }

    public static void DumpViews(TopLevel top) { }
    internal static void SetWindowButtonsAlpha(TopLevel top, double alpha) => (top as MainWindow)?.SetCaptionAlpha(alpha);
    internal static double ClearFullscreenBar(TopLevel top) => 0;
    internal static string QaButtonState(TopLevel top) => "windows";
}

/// <summary>
/// Ordine delle finestre fra il player e il VideoHost (che su Windows e' una finestra di un altro processo):
/// il video sta sempre subito sotto il player, mai sopra.
/// </summary>
internal static class WineWindowOrder
{
    public static long WindowNumber(TopLevel top) => (long)WinNative.Handle(top);

    public static bool IsWindowShown(ulong window) => window != 0 && WinNative.IsWindowVisible((IntPtr)(long)window);

    private static IntPtr VideoWindow(int hostPid) => hostPid <= 0 ? IntPtr.Zero : WinNative.TopWindowOf(hostPid, "Cinecore Video");

    /// <summary>Il video del VideoHost sta subito sotto il player; se no lo rimette li' (vero se era gia' a posto).</summary>
    public static bool VideoCoversWindow(long ours, int hostPid)
    {
        IntPtr video = VideoWindow(hostPid);
        if (video == IntPtr.Zero || ours == 0) return true;
        if (WinNative.GetWindow(video, WinNative.GW_HWNDPREV) == (IntPtr)ours) return true;
        WinNative.SetWindowPos(video, (IntPtr)ours, 0, 0, 0, 0,
            WinNative.SWP_NOMOVE | WinNative.SWP_NOSIZE | WinNative.SWP_NOACTIVATE | WinNative.SWP_NOOWNERZORDER | WinNative.SWP_ASYNCWINDOWPOS);
        return false;
    }

    /// <summary>Sul Mac alzava il player sopra le finestre normali durante il caricamento: su Windows basta KeepAbove.</summary>
    public static void SetRaised(TopLevel top, bool raised) { }

    /// <summary>Se la finestra del VideoHost e' sopra la nostra, la si porta subito sotto. Vero se l'ha fatto.</summary>
    public static bool KeepAbove(TopLevel top, int hostPid)
    {
        IntPtr video = VideoWindow(hostPid);
        if (video == IntPtr.Zero) return false;
        if (WinVideoStack.IsDirectlyBelow(top, video)) return false;
        WinVideoStack.KeepBelow(top, video, cover: false, async: true);
        return true;
    }
}

/// <summary>
/// Il vetro vero di macOS (NSVisualEffectView / Liquid Glass) non c'e' su Windows: i menu e le barre usano il vetro
/// disegnato dall'app. Restano gli aiuti sul mouse (clic fuori dalla finestra, finestra sotto il puntatore).
/// </summary>
internal sealed class MacGlass
{
    private readonly TopLevel _top;
    public MacGlass(TopLevel top) { _top = top; }

    public static bool Available => false;
    public bool HasLiquid => false;
    public bool Show(IReadOnlyList<Rect> panels, double radius, bool legacyGlass) => false;
    public void SetForeground(int index, ILockedFramebuffer pixels) { }

    /// <summary>Un tasto del mouse premuto fuori dalla nostra finestra (chiude i menu, come ContextOverlayMenuForm).</summary>
    public bool MouseDownOutsideWindow()
    {
        bool down = (WinNative.GetAsyncKeyState(0x01) & 0x8000) != 0 || (WinNative.GetAsyncKeyState(0x02) & 0x8000) != 0 || (WinNative.GetAsyncKeyState(0x04) & 0x8000) != 0;
        if (!down) return false;
        IntPtr hwnd = WinNative.Handle(_top);
        if (hwnd == IntPtr.Zero || !WinNative.GetCursorPos(out var p)) return false;
        WinNative.GetWindowRect(hwnd, out var r);
        return p.X < r.Left || p.Y < r.Top || p.X >= r.Right || p.Y >= r.Bottom;
    }

    /// <summary>Processo e nome del programma della finestra sotto il puntatore.</summary>
    public static (long Pid, string Owner)? WindowOwnerAtMouse()
    {
        try
        {
            if (!WinNative.GetCursorPos(out var p)) return null;
            IntPtr hwnd = WinNative.WindowFromPoint(p);
            if (hwnd == IntPtr.Zero) return null;
            hwnd = WinNative.GetAncestor(hwnd, 2 /* GA_ROOT */);
            WinNative.GetWindowThreadProcessId(hwnd, out uint pid);
            string name = "";
            try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; } catch { }
            return (pid, name);
        }
        catch { return null; }
    }

    /// <summary>I clic destri "persi" del Mac (finiti sul video di Wine) su Windows non succedono: contatore fermo.</summary>
    public static uint SecondaryPressCount() => 0;

    public static bool SecondaryButtonDown() => (WinNative.GetAsyncKeyState(0x02) & 0x8000) != 0;

    public Point? PointerInWindow()
    {
        IntPtr hwnd = WinNative.Handle(_top);
        if (hwnd == IntPtr.Zero || !WinNative.GetCursorPos(out var p)) return null;
        var client = WinNative.ClientScreenRect(hwnd);
        double s = Math.Max(0.1, _top.RenderScaling);
        var at = new Point((p.X - client.Left) / s, (p.Y - client.Top) / s);
        return new Rect(0, 0, client.Width / s, client.Height / s).Contains(at) ? at : null;
    }

    public void Clear() { }
}

/// <summary>Liquid Glass del Mac: non disponibile su Windows (le capsule usano il vetro disegnato).</summary>
internal sealed class MacSurfaceGlass : IDisposable
{
    public MacSurfaceGlass(TopLevel top) { }
    public bool Active => false;
    public static bool Supported => false;
    public bool Show(IReadOnlyList<(Rect Bounds, double Radius)> panels, bool dark, double alpha) => false;
    public void Hide() { }
    public void SetForeground(int index, string key, PixelSize size, Action<DrawingContextAction> draw) { }
    public void Dispose() { }
}

internal readonly record struct DrawingContextAction(Avalonia.Media.DrawingContext Context);
