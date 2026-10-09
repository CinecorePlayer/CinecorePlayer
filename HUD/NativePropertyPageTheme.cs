#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    /// <summary>
    /// Themes the native DirectShow property pages (LAV, MPC) embedded in Settings so they
    /// match the app instead of showing a grey Win32 dialog. Behaviour, keyboard navigation
    /// and accessibility stay native: only colours are supplied through the standard
    /// WM_CTLCOLOR* messages and system dark themes. Check boxes, radio buttons and group
    /// boxes are painted here because their themed text ignores those colours and was
    /// unreadable on a dark background. Only windows owned by this UI thread are touched.
    /// </summary>
    internal sealed class NativePropertyPageTheme : IDisposable
    {
        private readonly Dictionary<IntPtr, PageWindow> _windows = new();

        public void Apply(IntPtr root)
        {
            foreach (var key in new List<IntPtr>(_windows.Keys))
            {
                if (IsWindow(key) && _windows[key].Handle != IntPtr.Zero) continue;
                _windows[key].Dispose();
                _windows.Remove(key);
            }
            EnumChildWindows(root, (window, _) => { Attach(window); return true; }, IntPtr.Zero);
        }

        private void Attach(IntPtr handle)
        {
            if (_windows.ContainsKey(handle)) return;
            if (GetWindowThreadProcessId(handle, out uint pid) != GetCurrentThreadId() || pid != Environment.ProcessId) return;
            if (Control.FromHandle(handle) != null) return;
            try
            {
                var window = new PageWindow(handle);
                _windows.Add(handle, window);
                RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
            }
            catch { }
        }

        public void Clear()
        {
            foreach (var window in _windows.Values) window.Dispose();
            _windows.Clear();
        }

        public void Dispose() => Clear();

        private enum Kind { Other, Dialog, CheckBox, Radio, GroupBox, PushButton, ComboBox, Edit, ListView, TreeView, StaticText }

        private sealed class PageWindow : NativeWindow, IDisposable
        {
            private readonly IntPtr _panelBrush = CreateSolidBrush(Rgb(Theme.Panel));
            private readonly IntPtr _fieldBrush = CreateSolidBrush(Rgb(Theme.Card));
            private readonly Kind _kind;

            internal PageWindow(IntPtr handle)
            {
                _kind = Classify(handle);
                AssignHandle(handle);
                bool light = Theme.IsLight;
                switch (_kind)
                {
                    case Kind.ComboBox:
                    case Kind.Edit:
                        SetWindowTheme(handle, light ? "CFD" : "DarkMode_CFD", null);
                        break;
                    case Kind.ListView:
                        SetWindowTheme(handle, light ? "Explorer" : "DarkMode_Explorer", null);
                        SendMessage(handle, 0x1001, IntPtr.Zero, (IntPtr)Rgb(Theme.Card));   // LVM_SETBKCOLOR
                        SendMessage(handle, 0x1026, IntPtr.Zero, (IntPtr)Rgb(Theme.Card));   // LVM_SETTEXTBKCOLOR
                        SendMessage(handle, 0x1024, IntPtr.Zero, (IntPtr)Rgb(Theme.Text));   // LVM_SETTEXTCOLOR
                        IntPtr header = SendMessage(handle, 0x101F, IntPtr.Zero, IntPtr.Zero); // LVM_GETHEADER
                        if (header != IntPtr.Zero) SetWindowTheme(header, light ? "ItemsView" : "DarkMode_ItemsView", null);
                        break;
                    case Kind.TreeView:
                        SetWindowTheme(handle, light ? "Explorer" : "DarkMode_Explorer", null);
                        SendMessage(handle, 0x111D, IntPtr.Zero, (IntPtr)Rgb(Theme.Card));  // TVM_SETBKCOLOR
                        SendMessage(handle, 0x111E, IntPtr.Zero, (IntPtr)Rgb(Theme.Text));  // TVM_SETTEXTCOLOR
                        break;
                    case Kind.Dialog:
                    case Kind.StaticText:
                        break;
                    default:
                        // Push buttons, scroll bars, spin and track bars have native dark parts.
                        SetWindowTheme(handle, light ? "Explorer" : "DarkMode_Explorer", null);
                        break;
                }
            }

            private static Kind Classify(IntPtr handle)
            {
                var name = new StringBuilder(64);
                GetClassName(handle, name, name.Capacity);
                switch (name.ToString())
                {
                    case "#32770": return Kind.Dialog;
                    case "ComboBox": return Kind.ComboBox;
                    case "Edit": return Kind.Edit;
                    case "SysListView32": return Kind.ListView;
                    case "SysTreeView32": return Kind.TreeView;
                    case "Static":
                        // Text statics only (left/center/right/simple/no-wrap); icons, frames
                        // and bitmaps keep their native painting.
                        int ss = GetWindowLong(handle, GWL_STYLE) & 0x1F;
                        return ss is 0 or 1 or 2 or 0xB or 0xC ? Kind.StaticText : Kind.Other;
                    case "Button":
                        int type = GetWindowLong(handle, GWL_STYLE) & 0xF;
                        return type switch
                        {
                            2 or 3 or 5 or 6 => Kind.CheckBox,
                            4 or 9 => Kind.Radio,
                            7 => Kind.GroupBox,
                            0 or 1 => Kind.PushButton,
                            _ => Kind.Other // owner-drawn and split buttons keep their own painting
                        };
                    default: return Kind.Other;
                }
            }

            private bool CustomPainted => _kind is Kind.CheckBox or Kind.Radio or Kind.GroupBox;

            // A disabled static is drawn embossed by Windows, ignoring WM_CTLCOLORSTATIC:
            // on a dark page that reads as blurred bold text. Only that state is repainted.
            private bool PaintsDisabledStatic => _kind == Kind.StaticText && !IsWindowEnabled(Handle);

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case >= WM_CTLCOLORMSGBOX and <= WM_CTLCOLORSTATIC:
                        ProvideColors(ref m);
                        return;
                    case WM_ERASEBKGND when _kind == Kind.Dialog:
                        GetClientRect(Handle, out var rect);
                        FillRect(m.WParam, ref rect, _panelBrush);
                        m.Result = (IntPtr)1;
                        return;
                    case WM_ERASEBKGND when CustomPainted:
                        m.Result = (IntPtr)1;
                        return;
                    case WM_PAINT when CustomPainted:
                        IntPtr dc = BeginPaint(Handle, out var paint);
                        try { PaintButton(dc); }
                        finally { EndPaint(Handle, ref paint); }
                        return;
                    case WM_PRINTCLIENT when CustomPainted:
                        PaintButton(m.WParam);
                        return;
                    case WM_PAINT when PaintsDisabledStatic:
                        IntPtr sdc = BeginPaint(Handle, out var spaint);
                        try { PaintDisabledStatic(sdc); }
                        finally { EndPaint(Handle, ref spaint); }
                        return;
                }

                base.WndProc(ref m);

                // The native button paints state changes (hover, press, check, focus)
                // directly: repaint over them immediately with the themed rendering.
                if (CustomPainted && m.Msg is WM_ENABLE or WM_SETTEXT or WM_SETFOCUS or WM_KILLFOCUS or WM_LBUTTONDOWN
                        or WM_LBUTTONUP or WM_LBUTTONDBLCLK or WM_KEYDOWN or WM_KEYUP or WM_MOUSEMOVE or WM_MOUSELEAVE
                        or WM_UPDATEUISTATE or BM_SETCHECK or BM_SETSTATE or BM_SETSTYLE or WM_CAPTURECHANGED)
                    RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_NOERASE);
            }

            private void ProvideColors(ref Message m)
            {
                IntPtr control = m.LParam;
                bool enabled = IsWindowEnabled(control);
                bool field = m.Msg is WM_CTLCOLOREDIT or WM_CTLCOLORLISTBOX;
                if (m.Msg == WM_CTLCOLORSTATIC)
                {
                    // Read-only and disabled edits ask for static colours: keep them fields,
                    // except borderless read-only edits used as selectable labels.
                    var name = new StringBuilder(16);
                    GetClassName(control, name, name.Capacity);
                    bool bordered = (GetWindowLong(control, GWL_STYLE) & WS_BORDER) != 0 || (GetWindowLong(control, GWL_EXSTYLE) & WS_EX_CLIENTEDGE) != 0;
                    field = name.ToString() == "Edit" && bordered;
                }
                SetTextColor(m.WParam, Rgb(enabled ? Theme.Text : Theme.Muted));
                SetBkColor(m.WParam, Rgb(field ? Theme.Card : Theme.Panel));
                m.Result = field ? _fieldBrush : _panelBrush;
            }

            private void PaintButton(IntPtr dc)
            {
                GetClientRect(Handle, out var native);
                int width = native.Right - native.Left, height = native.Bottom - native.Top;
                if (width <= 0 || height <= 0) return;

                using var buffer = new Bitmap(width, height);
                using (var g = Graphics.FromImage(buffer))
                {
                    g.Clear(Theme.Panel);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var text = new StringBuilder(512);
                    GetWindowText(Handle, text, text.Capacity);
                    IntPtr fontHandle = SendMessage(Handle, WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
                    using var font = fontHandle != IntPtr.Zero ? Font.FromHfont(fontHandle) : global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f);
                    bool enabled = IsWindowEnabled(Handle);
                    float scale = Math.Max(1f, GetDpiForWindow(Handle) / 96f);
                    int style = GetWindowLong(Handle, GWL_STYLE);
                    bool showPrefix = (SendMessage(Handle, WM_QUERYUISTATE, IntPtr.Zero, IntPtr.Zero).ToInt64() & UISF_HIDEACCEL) == 0;
                    var prefix = showPrefix ? TextFormatFlags.Default : TextFormatFlags.HidePrefix;

                    if (_kind == Kind.GroupBox)
                    {
                        // Section title followed by a hairline: no box around the group.
                        Size title = TextRenderer.MeasureText(g, text.ToString(), font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                        int mid = Math.Max(1, title.Height / 2);
                        int textRight = 0;
                        if (title.Width > 0)
                        {
                            var labelRect = new Rectangle(0, 0, Math.Min(width, title.Width + 2), title.Height);
                            TextRenderer.DrawText(g, text.ToString(), font, labelRect, enabled ? Theme.SubtleText : Theme.Muted,
                                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | prefix);
                            textRight = labelRect.Right + (int)(8 * scale);
                        }
                        if (textRight < width)
                        {
                            using var pen = new Pen(Color.FromArgb(90, Theme.Border));
                            g.DrawLine(pen, textRight, mid, width, mid);
                        }
                    }
                    else
                    {
                        long state = SendMessage(Handle, BM_GETSTATE, IntPtr.Zero, IntPtr.Zero).ToInt64();
                        int check = (int)(state & 3);
                        bool hot = (state & BST_HOT) != 0, pushed = (state & BST_PUSHED) != 0, focused = (state & BST_FOCUS) != 0;
                        bool radio = _kind == Kind.Radio;
                        bool leftText = (style & BS_LEFTTEXT) != 0;

                        int size = Math.Min(height - 2, (int)Math.Round(12 * scale)); // native glyph + gap is ~15 px: never wider, or labels clip
                        int markX = leftText ? width - size - 1 : 1;
                        var mark = new Rectangle(markX, (height - size) / 2, size, size);
                        Color fillColor = check != 0 ? Theme.Accent : Theme.Card;
                        Color borderColor = check != 0 ? Theme.Accent : hot || pushed ? Theme.Muted : Theme.BorderAccent;
                        if (pushed && check == 0) fillColor = Theme.Selection;
                        int alpha = enabled ? 255 : 110;
                        using (var fill = new SolidBrush(Color.FromArgb(alpha, fillColor)))
                        using (var border = new Pen(Color.FromArgb(alpha, borderColor), Math.Max(1f, scale)))
                        {
                            if (radio)
                            {
                                g.FillEllipse(fill, mark);
                                g.DrawEllipse(border, mark);
                            }
                            else
                            {
                                using var path = RoundedRect(mark, Math.Max(2, (int)(3 * scale)));
                                g.FillPath(fill, path);
                                g.DrawPath(border, path);
                            }
                        }
                        if (check != 0)
                        {
                            using var ink = new Pen(Color.FromArgb(alpha, Color.White), 1.8f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                            using var dot = new SolidBrush(Color.FromArgb(alpha, Color.White));
                            float cx = mark.Left + mark.Width / 2f, cy = mark.Top + mark.Height / 2f;
                            if (radio) g.FillEllipse(dot, cx - size * 0.2f, cy - size * 0.2f, size * 0.4f, size * 0.4f);
                            else if (check == 2) g.DrawLine(ink, mark.Left + size * 0.28f, cy, mark.Right - size * 0.28f, cy);
                            else g.DrawLines(ink, new[] { new PointF(mark.Left + size * 0.25f, cy), new PointF(mark.Left + size * 0.43f, mark.Bottom - size * 0.3f), new PointF(mark.Right - size * 0.24f, mark.Top + size * 0.3f) });
                        }

                        int gap = (int)Math.Round(2 * scale);
                        var textBounds = leftText
                            ? new Rectangle(0, 0, Math.Max(0, mark.Left - gap), height)
                            : new Rectangle(mark.Right + gap, 0, Math.Max(0, width - mark.Right - gap), height);
                        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | prefix |
                                    ((style & BS_MULTILINE) != 0 ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine) |
                                    (leftText ? TextFormatFlags.Right : TextFormatFlags.Left);
                        TextRenderer.DrawText(g, text.ToString(), font, textBounds, enabled ? Theme.Text : Theme.Muted, flags);
                        if (focused && showPrefix)
                        {
                            Size measured = TextRenderer.MeasureText(g, text.ToString(), font, textBounds.Size, flags);
                            var focus = new Rectangle(textBounds.Left - 2, Math.Max(0, (height - measured.Height) / 2 - 1), Math.Min(textBounds.Width + 2, measured.Width + 4), Math.Min(height, measured.Height + 2));
                            using var pen = new Pen(Color.FromArgb(160, Theme.Accent)) { DashStyle = DashStyle.Dot };
                            g.DrawRectangle(pen, focus);
                        }
                    }
                }
                using var target = Graphics.FromHdc(dc);
                target.DrawImageUnscaled(buffer, 0, 0);
            }

            private void PaintDisabledStatic(IntPtr dc)
            {
                GetClientRect(Handle, out var native);
                int width = native.Right - native.Left, height = native.Bottom - native.Top;
                if (width <= 0 || height <= 0) return;
                using var g = Graphics.FromHdc(dc);
                using (var clear = new SolidBrush(Theme.Panel)) g.FillRectangle(clear, 0, 0, width, height);
                var text = new StringBuilder(1024);
                GetWindowText(Handle, text, text.Capacity);
                IntPtr fontHandle = SendMessage(Handle, WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
                using var font = fontHandle != IntPtr.Zero ? Font.FromHfont(fontHandle) : global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f);
                int style = GetWindowLong(Handle, GWL_STYLE);
                int ss = style & 0x1F;
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.Top |
                            (ss == 1 ? TextFormatFlags.HorizontalCenter : ss == 2 ? TextFormatFlags.Right : TextFormatFlags.Left) |
                            (ss is 0xB or 0xC ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak) |
                            ((style & SS_NOPREFIX) != 0 ? TextFormatFlags.NoPrefix : TextFormatFlags.Default) |
                            ((style & SS_CENTERIMAGE) != 0 ? TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine : 0);
                TextRenderer.DrawText(g, text.ToString(), font, new Rectangle(0, 0, width, height), Theme.Muted, flags);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    try { ReleaseHandle(); } catch { }
                }
                DeleteObject(_panelBrush);
                DeleteObject(_fieldBrush);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static int Rgb(Color c) => c.R | c.G << 8 | c.B << 16;

        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const int WS_BORDER = 0x00800000, WS_EX_CLIENTEDGE = 0x200;
        private const int SS_NOPREFIX = 0x80, SS_CENTERIMAGE = 0x200;
        private const int BS_LEFTTEXT = 0x20, BS_MULTILINE = 0x2000;
        private const int BST_PUSHED = 0x4, BST_FOCUS = 0x8, BST_HOT = 0x200;
        private const int UISF_HIDEACCEL = 0x2;
        private const int WM_ENABLE = 0x000A, WM_SETTEXT = 0x000C, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;
        private const int WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_GETFONT = 0x0031;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_UPDATEUISTATE = 0x0128, WM_QUERYUISTATE = 0x0129;
        private const int WM_CTLCOLORMSGBOX = 0x0132, WM_CTLCOLOREDIT = 0x0133, WM_CTLCOLORLISTBOX = 0x0134, WM_CTLCOLORSTATIC = 0x0138;
        private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_CAPTURECHANGED = 0x0215, WM_MOUSELEAVE = 0x02A3, WM_PRINTCLIENT = 0x0318;
        private const int BM_SETCHECK = 0x00F1, BM_GETSTATE = 0x00F2, BM_SETSTATE = 0x00F3, BM_SETSTYLE = 0x00F4;
        private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400, RDW_NOERASE = 0x20;

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct PaintStruct { public IntPtr Dc; public int Erase; public Rect Rect; public int Restore, IncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved; }
        private delegate bool EnumWindowProc(IntPtr window, IntPtr param);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr root, EnumWindowProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr handle, IntPtr rect, IntPtr region, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out Rect rect);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr handle, out PaintStruct paint);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr handle, ref PaintStruct paint);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int msg, IntPtr w, IntPtr l);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int color);
        [DllImport("gdi32.dll")] private static extern int SetBkColor(IntPtr dc, int color);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr handle, string app, string? id);
    }
}
