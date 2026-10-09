#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// One calm way to open a modal sheet over the main window: the window behind dims and
    /// the sheet fades in, in place. Replaces the system "pop" animation of plain dialogs,
    /// which looked abrupt and did not match the in-page sheets.
    /// </summary>
    internal static class SheetPresenter
    {
        private const double DurationMs = 150;
        private const double DimOpacity = 0.45;

        public static DialogResult ShowDialog(Form dialog, IWin32Window? owner)
        {
            Form? host = owner as Form ?? (owner is Control c ? c.FindForm() : null);
            long asked = Stopwatch.GetTimestamp(); double shownAfter = 0;
            Form? dim = null;
            if (host != null && host.Visible && host.WindowState != FormWindowState.Minimized && host.IsHandleCreated)
            {
                // The dimmer covers the main window: it is created in that window's scaling mode.
                InHostContext(() =>
                {
                    dim = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None,
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        BackColor = Color.Black,
                        Opacity = 0,
                        Bounds = host.RectangleToScreen(host.ClientRectangle),
                        ControlBox = false,
                        Text = string.Empty
                    };
                    Form created = dim;
                    created.HandleCreated += (_, _) => DisableTransitions(created.Handle);
                    created.MouseDown += (_, _) => { try { dialog.Activate(); } catch { } };
                    created.Show(host);
                });
            }

            PlaceSheet(dialog, host);
            dialog.Opacity = 0;
            if (dialog.IsHandleCreated) DisableTransitions(dialog.Handle);
            else dialog.HandleCreated += (_, _) => DisableTransitions(dialog.Handle);

            var timer = new System.Windows.Forms.Timer { Interval = 15 };
            var clock = new Stopwatch();
            int frames = 0; double worstGap = 0, lastTick = 0;
            timer.Tick += (_, _) =>
            {
                double now = clock.Elapsed.TotalMilliseconds;
                frames++; worstGap = Math.Max(worstGap, now - lastTick); lastTick = now;
                double t = Math.Clamp(now / DurationMs, 0, 1);
                double eased = 1 - Math.Pow(1 - t, 3);
                try
                {
                    if (!dialog.IsDisposed) dialog.Opacity = eased;
                    if (dim != null && !dim.IsDisposed) dim.Opacity = DimOpacity * eased;
                }
                catch { }
                if (t >= 1)
                {
                    timer.Stop();
                    Dbg.Log($"[SHEET] {dialog.GetType().Name} {dialog.ClientSize.Width}x{dialog.ClientSize.Height}: shown after {shownAfter:0} ms, fade frames={frames} in {now:0} ms, worst gap {worstGap:0} ms", Dbg.LogLevel.Info);
                    // Fully opaque: drop the layered style so the compositor treats the
                    // sheet as a normal window again (system corners, no alpha blending).
                    try { if (!dialog.IsDisposed) { dialog.Opacity = 1; dialog.AllowTransparency = false; } } catch { }
                }
            };
            // Il player a schermo intero e' "sempre in primo piano" e, con l'HUD visibile, viene riportato
            // in cima di continuo. La scheda non lo era: finiva sotto la finestra del player mentre lo
            // sfondo oscurato restava sopra (si vedeva il film scurito e nessuna scheda). Ora la scheda
            // sta nella stessa fascia del player e viene tenuta sopra finche' e' aperta.
            var keepAbove = new System.Windows.Forms.Timer { Interval = 250 };
            void RaiseSheet()
            {
                try
                {
                    if (dialog.IsDisposed || !dialog.IsHandleCreated || !dialog.Visible) return;
                    // La scheda che ne apre un'altra e' stata portata "sempre in primo piano" con SetWindowPos (la
                    // proprieta' TopMost resta falsa): si guarda lo stile vero, altrimenti la seconda scheda
                    // restava nella fascia normale, sotto la prima, e non si poteva usare.
                    bool top = host?.TopMost == true || (host != null && host.IsHandleCreated && (GetWindowLongPtr(host.Handle, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0);
                    // Solo se in cima non c'e' gia' la scheda o una finestra che le appartiene (un menu, una sotto-scheda).
                    IntPtr above = GetWindow(dialog.Handle, GW_HWNDPREV);
                    bool covered = false;
                    for (int guard = 0; above != IntPtr.Zero && guard < 64; guard++, above = GetWindow(above, GW_HWNDPREV))
                        if (host != null && (above == host.Handle || (dim != null && above == dim.Handle))) { covered = true; break; }
                    if (covered || (top && (GetWindowLongPtr(dialog.Handle, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) == 0))
                        SetWindowPos(dialog.Handle, top ? HWND_TOPMOST : HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch { }
            }
            keepAbove.Tick += (_, _) => RaiseSheet();
            dialog.Shown += (_, _) => { shownAfter = Stopwatch.GetElapsedTime(asked).TotalMilliseconds; clock.Start(); timer.Start(); RaiseSheet(); keepAbove.Start(); };

            _open++;
            // Un avviso a tutto schermo ancora aperto (il codice di abbinamento) coprirebbe la scheda: chi lo
            // mostra lo toglie quando sta per aprirsi una scheda.
            try { Opening?.Invoke(); } catch { }
            try
            {
                return dim != null ? dialog.ShowDialog(dim) : dialog.ShowDialog(owner);
            }
            finally
            {
                _open--;
                timer.Stop();
                timer.Dispose();
                keepAbove.Stop();
                keepAbove.Dispose();
                if (dim != null)
                {
                    try { dim.Close(); dim.Dispose(); } catch { }
                }
                try { host?.Activate(); } catch { }
            }
        }

        /// <summary>True while at least one sheet is on screen: the playback HUD must not wake underneath it.</summary>
        public static bool AnyOpen => _open > 0;
        /// <summary>Raised just before a sheet is shown.</summary>
        public static event Action? Opening;
        private static int _open;

        // Sheets always work in 100% coordinates (see Layout): nothing to multiply.
        public static float ScaleOf(Control control) => 1f;

        // The sheets are laid out in 100% pixels with fonts in points. Above 100% Windows
        // enlarged the fonts but not the layout: texts were cut and controls huddled in a corner
        // (175% made the audio sync sheet unusable). Every sheet is now created as a "GDI
        // scaled" window: it keeps working in 100% coordinates, with its fonts fixed in 100%
        // pixels, and Windows enlarges the whole sheet to the scale of the screen, drawing its
        // text and lines at full sharpness. One rule for every sheet, at any scale.
        //
        // WinForms fixes the scaling mode of a window when the object is constructed, so the
        // caller opens this scope before creating the sheet: using var _ = Layout(this);
        public static IDisposable Layout(Control owner)
        {
            float factor = 1f;
            try { factor = owner.DeviceDpi / 96f; } catch { }
            if (_scope != null || factor < 1.02f || AppFonts.InSheetContext) return NoScope.Instance;
            IntPtr previous;
            try { previous = SetThreadDpiAwarenessContext(DpiUnawareGdiScaled); } catch { return NoScope.Instance; }
            if (previous == IntPtr.Zero) return NoScope.Instance;
            return _scope = new SheetScope(previous, factor);
        }

        [ThreadStatic] private static SheetScope? _scope;

        private sealed class SheetScope : IDisposable
        {
            public readonly IntPtr Previous;
            public readonly float Factor;
            public SheetScope(IntPtr previous, float factor) { Previous = previous; Factor = factor; }
            public void Dispose()
            {
                if (!ReferenceEquals(_scope, this)) return;
                _scope = null;
                try { SetThreadDpiAwarenessContext(Previous); } catch { }
            }
        }

        private sealed class NoScope : IDisposable
        {
            public static readonly NoScope Instance = new();
            public void Dispose() { }
        }

        // Runs an action in the main window's own scaling mode (the dimmer behind the sheet).
        private static void InHostContext(Action action)
        {
            SheetScope? scope = _scope;
            if (scope == null) { action(); return; }
            IntPtr sheetContext = IntPtr.Zero;
            try { sheetContext = SetThreadDpiAwarenessContext(scope.Previous); } catch { }
            try { action(); }
            finally { if (sheetContext != IntPtr.Zero) { try { SetThreadDpiAwarenessContext(sheetContext); } catch { } } }
        }

        // A sheet built inside a Layout scope lives in 100% coordinates: centre it there.
        private static void PlaceSheet(Form dialog, Form? host)
        {
            SheetScope? scope = _scope;
            if (scope == null || host == null || dialog.IsHandleCreated) return;
            try
            {
                dialog.AutoScaleMode = AutoScaleMode.None;
                FixFonts(dialog);
                Rectangle area = Rectangle.Empty, monitor = Rectangle.Empty;
                InHostContext(() => { area = host.RectangleToScreen(host.ClientRectangle); monitor = Screen.FromControl(host).Bounds; });
                dialog.StartPosition = FormStartPosition.Manual;
                // Nelle coordinate al 100% uno schermo conserva la sua origine e si rimpicciolisce attorno a
                // quella: dividere e basta era giusto solo sullo schermo principale (origine 0,0). Su un
                // secondo schermo in scala la scheda finiva fuori posto e non veniva ingrandita.
                dialog.Location = new Point(
                    (int)Math.Round(monitor.Left + (area.Left + area.Width / 2f - monitor.Left) / scope.Factor - dialog.Width / 2f),
                    (int)Math.Round(monitor.Top + (area.Top + area.Height / 2f - monitor.Top) / scope.Factor - dialog.Height / 2f));
            }
            catch { }
        }

        // Fonts created before the sheet window exists are still in points: controls and the
        // Font fields of the sheet get the same font fixed in 100% pixels.
        private static void FixFonts(Control root)
        {
            try
            {
                if (root.Font.Unit == GraphicsUnit.Point) root.Font = AppFonts.ToSheetFont(root.Font);
                const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                for (Type? type = root.GetType(); type != null && type != typeof(Control) && type != typeof(Form) && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true; type = type.BaseType)
                    foreach (var field in type.GetFields(Flags))
                        if (field.FieldType == typeof(Font) && !field.IsInitOnly && field.GetValue(root) is Font kept && kept.Unit == GraphicsUnit.Point)
                            field.SetValue(root, AppFonts.ToSheetFont(kept));
                        else if (field.FieldType == typeof(Font) && field.IsInitOnly && field.GetValue(root) is Font fixedFont && fixedFont.Unit == GraphicsUnit.Point)
                            field.SetValue(root, AppFonts.ToSheetFont(fixedFont));
            }
            catch { }
            foreach (Control child in root.Controls) FixFonts(child);
        }

        private static readonly IntPtr DpiUnawareGdiScaled = new(-5);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        private static void DisableTransitions(IntPtr handle)
        {
            try
            {
                int disabled = 1;
                DwmSetWindowAttribute(handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
            }
            catch { }
        }

        private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        private const uint GW_HWNDPREV = 3, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOPMOST = 0x8;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new(-1);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
