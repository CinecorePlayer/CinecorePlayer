#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    // Keep the native settings behaviour, keyboard navigation and accessibility.
    // Only windows owned by this process can safely receive our window procedure.
    internal sealed class NativePropertyPageTheme : IDisposable
    {
        private readonly Dictionary<IntPtr, PageWindow> _windows = new();
        public void Apply(IntPtr root)
        {
            // External renderer/filter dialogs own their drawing and input.
            Clear();
        }
        private void Attach(IntPtr handle)
        {
            GetWindowThreadProcessId(handle, out uint pid);
            if (GetWindowThreadProcessId(handle, out _) != GetCurrentThreadId() || pid != Environment.ProcessId || Control.FromHandle(handle) != null || _windows.ContainsKey(handle)) return;
            try { _windows.Add(handle, new PageWindow(handle)); InvalidateRect(handle, IntPtr.Zero, true); } catch { }
        }
        public void Clear() { foreach (var window in _windows.Values) window.Dispose(); _windows.Clear(); }
        public void Dispose() => Clear();

        private sealed class PageWindow : NativeWindow, IDisposable
        {
            private readonly IntPtr _background = CreateSolidBrush(Rgb(Theme.Panel));
            private readonly IntPtr _field = CreateSolidBrush(Rgb(Theme.Card));
            private readonly string _class;
            internal PageWindow(IntPtr handle)
            {
                var name = new StringBuilder(128);
                GetClassName(handle, name, name.Capacity);
                _class = name.ToString();
                AssignHandle(handle);
                SetWindowTheme(handle, Theme.IsLight ? "Explorer" : "DarkMode_Explorer", null);
                if (_class == "SysTreeView32")
                {
                    SendMessage(handle, 0x111D, IntPtr.Zero, (IntPtr)Rgb(Theme.Panel));
                    SendMessage(handle, 0x111E, IntPtr.Zero, (IntPtr)Rgb(Theme.Text));
                    // Remove the native inset frame around the navigation tree.
                    SetWindowLong(handle, -20, GetWindowLong(handle, -20) & ~0x200);
                    SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
                }
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg is >= 0x0132 and <= 0x0138)
                {
                    bool field = message.Msg is 0x0133 or 0x0134;
                    SetTextColor(message.WParam, Rgb(IsWindowEnabled(message.LParam) ? Theme.Text : Theme.Muted));
                    SetBkColor(message.WParam, Rgb(field ? Theme.Card : Theme.Panel));
                    message.Result = field ? _field : _background;
                    return;
                }
                if (message.Msg == 0x0014 && (_class == "#32770" || _class == "SysTabControl32"))
                {
                    GetClientRect(Handle, out var rect);
                    FillRect(message.WParam, ref rect, _background);
                    message.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref message);
            }
            private void PaintButton()
            {
                IntPtr dc = BeginPaint(Handle, out var paint);
                try
                {
                    using var g = Graphics.FromHdc(dc);
                    GetClientRect(Handle, out var native);
                    var bounds = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
                    g.Clear(Theme.Panel);
                    int type = GetWindowLong(Handle, -16) & 15;
                    var text = new StringBuilder(1024); GetWindowText(Handle, text, text.Capacity);
                    IntPtr fontHandle = SendMessage(Handle, 0x0031, IntPtr.Zero, IntPtr.Zero);
                    using var font = fontHandle != IntPtr.Zero ? Font.FromHfont(fontHandle) : new Font("Segoe UI", 9f);
                    Color ink = IsWindowEnabled(Handle) ? Theme.Text : Theme.Muted;
                    var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
                    if (type is 2 or 3 or 4 or 5 or 6 or 9)
                    {
                        int size = Math.Min(13, Math.Max(9, bounds.Height - 3));
                        var mark = new Rectangle(1, (bounds.Height - size) / 2, size, size);
                        bool radio = type is 4 or 9;
                        int check = (int)SendMessage(Handle, 0x00F0, IntPtr.Zero, IntPtr.Zero);
                        using var fill = new SolidBrush(check != 0 ? Theme.Accent : Theme.Card);
                        using var border = new Pen(check != 0 ? Theme.Accent : Theme.BorderAccent);
                        if (radio) { g.FillEllipse(fill, mark); g.DrawEllipse(border, mark); }
                        else { g.FillRectangle(fill, mark); g.DrawRectangle(border, mark); }
                        if (check != 0)
                        {
                            if (radio) { using var dot = new SolidBrush(Color.White); g.FillEllipse(dot, Rectangle.Inflate(mark, -5, -5)); }
                            else if (check == 2) { using var dash = new Pen(Color.White, 2); g.DrawLine(dash, mark.Left + 4, mark.Top + size / 2, mark.Right - 4, mark.Top + size / 2); }
                            else { using var tick = new Pen(Color.White, 2); g.DrawLines(tick, new[] { new Point(mark.Left + 3, mark.Top + size / 2), new Point(mark.Left + size / 2 - 1, mark.Bottom - 4), new Point(mark.Right - 3, mark.Top + 4) }); }
                        }
                        bounds.X += size + 4; bounds.Width = Math.Max(0, bounds.Width - size - 4);
                    }
                    else if (type == 7)
                    {
                        // Group titles match the surrounding settings; no nested frames.
                        ink = Theme.Muted; flags = TextFormatFlags.Top | TextFormatFlags.NoPadding;
                        bounds.X += 2;
                    }
                    else
                    {
                        using var fill = new SolidBrush((SendMessage(Handle, 0x00F2, IntPtr.Zero, IntPtr.Zero).ToInt64() & 4) != 0 ? Theme.Selection : Theme.Card);
                        g.FillRectangle(fill, Rectangle.Inflate(bounds, -1, -1));
                        flags |= TextFormatFlags.HorizontalCenter;
                    }
                    TextRenderer.DrawText(g, text.ToString(), font, bounds, ink, flags);
                    if (GetFocus() == Handle) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(bounds, -2, -2), Theme.Muted, Theme.Panel);
                }
                finally { EndPaint(Handle, ref paint); }
            }
            public void Dispose()
            {
                if (Handle != IntPtr.Zero) ReleaseHandle();
                DeleteObject(_background); DeleteObject(_field);
            }
        }
        private static int Rgb(Color c) => c.R | c.G << 8 | c.B << 16;
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct PaintStruct { public IntPtr Dc; public int Erase; public Rect Rect; public int Restore, IncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved; }
        private delegate bool EnumWindowProc(IntPtr window, IntPtr param);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr root, EnumWindowProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out Rect rect);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr handle, out PaintStruct paint);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr handle, ref PaintStruct paint);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int msg, IntPtr w, IntPtr l);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int color);
        [DllImport("gdi32.dll")] private static extern int SetBkColor(IntPtr dc, int color);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr handle, string app, string? id);
    }
}
