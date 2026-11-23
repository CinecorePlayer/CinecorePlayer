using System;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// Trackpad del telecomando: muove il puntatore del PC, clic sinistro/destro e rotella (SendInput di Windows).
/// Sul Mac non c'e' (servirebbe il permesso Accessibilita'): la pagina nasconde il trackpad.
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
    public static bool Available => false;
    public static void Move(double dx, double dy) { }
    public static void Click(bool right, bool twice) { }
    public static void Scroll(double dy) { }
#endif
}
