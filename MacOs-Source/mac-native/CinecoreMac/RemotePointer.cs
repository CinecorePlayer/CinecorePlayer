using System;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// Trackpad del telecomando: muove il puntatore del PC, clic sinistro/destro e rotella (SendInput di Windows).
/// Sul Mac: eventi Quartz, col permesso Accessibilita' chiesto al primo uso.
/// </summary>
internal static class RemotePointer
{
#if CINECORE_WINDOWS
    public static bool Available => true;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public MouseInput Mouse; } // 40 byte a 64 bit, come INPUT
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

    private const uint Move_ = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, Wheel = 0x0800;

    private static void Send(params MouseInput[] events)
    {
        var inputs = new Input[events.Length];
        for (int i = 0; i < events.Length; i++) inputs[i] = new Input { Type = 0 /* INPUT_MOUSE */, Mouse = events[i] };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    public static void Move(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        int x = (int)Math.Round(Math.Clamp(dx, -400, 400)), y = (int)Math.Round(Math.Clamp(dy, -400, 400));
        if (x == 0 && y == 0) return;
        Send(new MouseInput { Dx = x, Dy = y, Flags = Move_ });
    }

    public static void Click(bool right, bool twice)
    {
        uint down = right ? RightDown : LeftDown, up = right ? RightUp : LeftUp;
        Send(new MouseInput { Flags = down }, new MouseInput { Flags = up });
        if (twice) Send(new MouseInput { Flags = down }, new MouseInput { Flags = up });
    }

    /// <summary>
    /// Due dita sul telefono, in pixel. Come sul Mac lo scorrimento e' "naturale" (la pagina segue le dita: dita in su,
    /// si va avanti nella pagina) e fine: rotella ad alta risoluzione, frazioni di scatto (120 = uno scatto ≈ 50 px di dita).
    /// </summary>
    public static void Scroll(double dy)
    {
        if (!double.IsFinite(dy) || dy == 0) return;
        _wheelRest += Math.Clamp(dy, -400, 400) * 2.4;
        int data = (int)Math.Truncate(_wheelRest);
        if (data == 0) return;
        _wheelRest -= data;
        Send(new MouseInput { Data = unchecked((uint)data), Flags = Wheel });
    }

    private static double _wheelRest;
#else
    // Mac: eventi Quartz (CGEventPost). macOS li fa passare solo se l'app ha il permesso Accessibilita'
    // ("Controllare il computer"): al primo uso del trackpad chiediamo il permesso una volta, poi macOS lo ricorda.
    public static bool Available => true;

    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }
    [DllImport(CG)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] private static extern CGPoint CGEventGetLocation(IntPtr ev);
    [DllImport(CG)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint type, CGPoint at, uint button);
    [DllImport(CG)] private static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint wheelCount, int w1, int w2, int w3);
    [DllImport(CG)] private static extern void CGEventSetIntegerValueField(IntPtr ev, uint field, long value);
    [DllImport(CG)] private static extern void CGEventPost(uint tap, IntPtr ev);
    [DllImport(CG)] private static extern int CGGetActiveDisplayList(uint max, uint[] ids, out uint count);
    [DllImport(CG)] private static extern CGRect CGDisplayBounds(uint id);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightPostEventAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestPostEventAccess();
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);

    private const uint MouseMoved = 5, LeftDown_ = 1, LeftUp_ = 2, RightDown_ = 3, RightUp_ = 4;
    private const uint FieldClickState = 1, FieldDeltaX = 4, FieldDeltaY = 5;
    private static readonly object Gate = new();
    private static bool _asked;
    private static double _scrollRest;
    private static DateTime _lastClick;
    private static CGPoint _lastClickAt;

    /// <summary>Senza permesso gli eventi vengono scartati in silenzio: lo chiediamo (una volta per avvio).</summary>
    private static void EnsureAccess()
    {
        if (_asked) return;
        _asked = true;
        try
        {
            if (!CGPreflightPostEventAccess())
            {
                Console.Error.WriteLine("[remote] trackpad: manca il permesso Accessibilita', lo chiedo a macOS");
                CGRequestPostEventAccess();
            }
        }
        catch { }
    }

    private static CGPoint Cursor()
    {
        IntPtr ev = CGEventCreate(IntPtr.Zero);
        if (ev == IntPtr.Zero) return default;
        try { return CGEventGetLocation(ev); } finally { CFRelease(ev); }
    }

    /// <summary>Tiene il puntatore dentro gli schermi: se il punto nuovo cade fuori da tutti resta sul bordo di quello di partenza.</summary>
    private static CGPoint Clamp(CGPoint from, CGPoint to)
    {
        var ids = new uint[16];
        if (CGGetActiveDisplayList(16, ids, out uint n) != 0 || n == 0) return to;
        CGRect? home = null;
        for (int i = 0; i < n; i++)
        {
            var r = CGDisplayBounds(ids[i]);
            if (to.X >= r.X && to.X < r.X + r.W && to.Y >= r.Y && to.Y < r.Y + r.H) return to;
            if (from.X >= r.X && from.X < r.X + r.W && from.Y >= r.Y && from.Y < r.Y + r.H) home = r;
        }
        var b = home ?? CGDisplayBounds(ids[0]);
        return new CGPoint { X = Math.Clamp(to.X, b.X, b.X + b.W - 1), Y = Math.Clamp(to.Y, b.Y, b.Y + b.H - 1) };
    }

    private static void Post(uint type, CGPoint at, long clickState = 0, int dx = 0, int dy = 0)
    {
        IntPtr ev = CGEventCreateMouseEvent(IntPtr.Zero, type, at, type is RightDown_ or RightUp_ ? 1u : 0u);
        if (ev == IntPtr.Zero) return;
        try
        {
            if (clickState > 0) CGEventSetIntegerValueField(ev, FieldClickState, clickState);
            if (dx != 0 || dy != 0) { CGEventSetIntegerValueField(ev, FieldDeltaX, dx); CGEventSetIntegerValueField(ev, FieldDeltaY, dy); }
            CGEventPost(0 /* kCGHIDEventTap */, ev);
        }
        finally { CFRelease(ev); }
    }

    public static void Move(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        int x = (int)Math.Round(Math.Clamp(dx, -400, 400)), y = (int)Math.Round(Math.Clamp(dy, -400, 400));
        if (x == 0 && y == 0) return;
        lock (Gate)
        {
            EnsureAccess();
            var from = Cursor();
            var to = Clamp(from, new CGPoint { X = from.X + x, Y = from.Y + y });
            Post(MouseMoved, to, 0, x, y);
        }
    }

    public static void Click(bool right, bool twice)
    {
        lock (Gate)
        {
            EnsureAccess();
            var at = Cursor();
            uint down = right ? RightDown_ : LeftDown_, up = right ? RightUp_ : LeftUp_;
            // Due tocchi ravvicinati nello stesso punto sono un doppio clic anche per macOS (clickState 2).
            bool near = Math.Abs(at.X - _lastClickAt.X) < 6 && Math.Abs(at.Y - _lastClickAt.Y) < 6;
            long state = !right && near && (DateTime.UtcNow - _lastClick).TotalMilliseconds < 450 ? 2 : 1;
            Post(down, at, state); Post(up, at, state);
            if (twice) { Post(down, at, 2); Post(up, at, 2); }
            _lastClick = DateTime.UtcNow; _lastClickAt = at;
        }
    }

    /// <summary>
    /// Due dita sul telefono, in pixel, scorrimento "naturale" come il trackpad del Mac: dita in su, si va avanti nella pagina.
    /// Evento a pixel (non a scatti), cosi' lo scorrimento e' fine.
    /// </summary>
    public static void Scroll(double dy)
    {
        if (!double.IsFinite(dy) || dy == 0) return;
        lock (Gate)
        {
            EnsureAccess();
            _scrollRest += Math.Clamp(dy, -400, 400) * 1.6;
            int px = (int)Math.Truncate(_scrollRest);
            if (px == 0) return;
            _scrollRest -= px;
            IntPtr ev = CGEventCreateScrollWheelEvent2(IntPtr.Zero, 0 /* kCGScrollEventUnitPixel */, 1, px, 0, 0);
            if (ev == IntPtr.Zero) return;
            try { CGEventPost(0, ev); } finally { CFRelease(ev); }
        }
    }
#endif
}
