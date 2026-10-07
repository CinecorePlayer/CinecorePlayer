#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.F4) || keyData == (Keys.Alt | Keys.Space))
                return base.ProcessCmdKey(ref msg, keyData);

            // Il placeholder pre-film è un gate a schermo intero quanto il loader:
            // Enter/Spazio devono avviare il media ed Esc deve annullarlo. Prima non
            // rientrava in questo ramo, quindi sembrava che l'intera HUD fosse bloccata.
            bool blockingOverlayVisible = (_videoLoading?.Visible == true) || _preOpenPlaceholderGateActive;

            // =========================
            // 0) Media / remote keys (validi ovunque, anche in UI)
            // =========================
            if (TryHandleMediaVirtualKey(keyData, "ProcessCmdKey"))
                return true;
            if (TryHandleFunctionRowMediaKey(keyData, "ProcessCmdKey"))
                return true;

            if (blockingOverlayVisible)
            {
                if ((keyData & Keys.Modifiers) == Keys.None)
                {
                    if (keyData == Keys.F)
                    {
                        ToggleFullscreen();
                        try { _hud.Pulse(HudOverlay.ButtonId.Fullscreen); } catch { }
                        return true;
                    }

                    if (keyData == Keys.Enter || keyData == Keys.Space)
                    {
                        if (_preOpenPlaceholderGateActive)
                        {
                            TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: false);
                            return true;
                        }
                        return true;
                    }

                    if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                    {
                        if (FormBorderStyle == FormBorderStyle.None)
                        {
                            ToggleFullscreen();
                            return true;
                        }

                        if (_preOpenPlaceholderGateActive)
                        {
                            TryConsumePreOpenPlaceholderGate(startNow: false, fromRemote: false);
                            return true;
                        }

                        return true;
                    }
                }

                return true;
            }

            if (_netflixModePage?.Visible == true)
            {
                if (_netflixModePage.HandleHudCommand(keyData)) return true;

                // Il campo cerca è un editor vero: mentre ha il focus non deve
                // perdere spazio, frecce o la lettera F a favore delle hotkey.
                if (_netflixModePage.IsSearchEditing)
                {
                    if ((keyData & Keys.Modifiers) == Keys.None &&
                        (keyData == Keys.Escape || keyData == Keys.BrowserBack))
                    {
                        if (_netflixModePage.TryHandleBackKey())
                            return true;
                    }
                    return base.ProcessCmdKey(ref msg, keyData);
                }

                if ((keyData & Keys.Modifiers) == Keys.None)
                {
                    if (keyData == Keys.Left) { _lastDpadFromRemote = false; _netflixModePage.MovePrevious(); return true; }
                    if (keyData == Keys.Right) { _lastDpadFromRemote = false; _netflixModePage.MoveNext(); return true; }
                    if (keyData == Keys.Enter || keyData == Keys.Space) { _lastDpadFromRemote = false; _netflixModePage.OpenSelected(); return true; }
                    if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                    {
                        _lastDpadFromRemote = false;
                        if (_netflixModePage.TryHandleBackKey())
                            return true;
                        // Spotlight is deliberately exited only from the remote or
                        // the context menu; keyboard Back/Escape merely dismisses input.
                        return true;
                    }
                    if (keyData == Keys.F)
                    {
                        ToggleFullscreen();
                        return true;
                    }
                }

                return base.ProcessCmdKey(ref msg, keyData);
            }

            if (_settingsHudPage?.Visible == true)
            {
                if ((keyData & Keys.Modifiers) == Keys.None)
                {
                    if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                    {
                        HideSettingsHudPage();
                        return true;
                    }
                    if (keyData == Keys.F)
                    {
                        ToggleFullscreen();
                        return true;
                    }
                }

                return base.ProcessCmdKey(ref msg, keyData);
            }

            // =========================
            // 1) Modalità foto: solo frecce + ESC
            // =========================
            if (IsPhotoMode)
            {
                if (keyData == Keys.F) { ToggleFullscreen(); return true; }
                if (_engine is ImagePlaybackEngine photo)
                {
                    if (keyData is Keys.Add or Keys.Oemplus) { photo.ZoomBy(1.2); _photoHud.Wake(2200); return true; }
                    if (keyData is Keys.Subtract or Keys.OemMinus) { photo.ZoomBy(1 / 1.2); _photoHud.Wake(2200); return true; }
                    if (keyData is Keys.D0 or Keys.NumPad0) { photo.ResetView(); _photoHud.Wake(2200); return true; }
                    if (keyData == Keys.R) { photo.RotateClockwise(); _photoHud.Wake(2200); return true; }
                    if (keyData == Keys.Space) { TogglePhotoSlideshow(); return true; }
                    if (keyData == Keys.Home && _imageFiles.Count > 0) { OpenImage(_imageFiles[0]); return true; }
                    if (keyData == Keys.End && _imageFiles.Count > 0) { OpenImage(_imageFiles[^1]); return true; }
                }
                if (keyData == Keys.Left) { ShowPrevImage(); return true; }
                if (keyData == Keys.Right) { ShowNextImage(); return true; }
                if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                {
                    StopPhotoSlideshow();
                    CloseCurrentToLibrary();
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            // =========================
            // 2) UI navigabile (Libreria)
            // =========================
            bool modalUi = IsAnyLibraryVisible();

            if (modalUi)
            {
                // Allow normal text-edit keys when a TextBox is focused (es. search header in Libreria)
                try
                {
                    Control? ac = this.ActiveControl;
                    while (ac is ContainerControl cc && cc.ActiveControl != null)
                        ac = cc.ActiveControl;

                    if (ac is TextBoxBase tb)
                    {
                        // URL/YouTube devono ricevere Enter direttamente. Prima il
                        // ProcessCmdKey del player lo trasformava in DPAD OK e apriva
                        // la card della libreria rimasta selezionata sotto la pagina.
                        try
                        {
                            if (_cinematicLibraryPage?.Visible == true &&
                                _cinematicLibraryPage.IsWebAddressEditor(tb))
                            {
                                return base.ProcessCmdKey(ref msg, keyData);
                            }
                        }
                        catch { }

                        // Libreria: se stiamo editando il campo di ricerca,
                        // ESC (e BrowserBack) deve solo uscire dall'editing (e NON chiudere la libreria).
                        try
                        {
                            if (_cinematicLibraryPage?.Visible == true && ResolveDpadRoot() is CinematicMediaLibraryPage cinematicLibX && cinematicLibX.IsSearchEditor(tb))
                            {
                                if (keyData == Keys.Escape || keyData == Keys.BrowserBack)
                                {
                                    if (cinematicLibX.TryRemoteExitSearchEdit(tb, out var next))
                                    {
                                        _dpadRoot = ResolveDpadRoot();
                                        if (next != null && !next.IsDisposed)
                                        {
                                            _focused = next;
                                            try { next.Focus(); } catch { }
                                            EnsureDpadVisible(next);
                                            _focusRing.Attach(next);
                                        }
                                        return true;
                                    }
                                }

                                return base.ProcessCmdKey(ref msg, keyData);
                            }
                        }
                        catch { }

                        // Altri TextBox: lascia almeno i tasti di testo base
                        var kc = keyData & Keys.KeyCode;
                        if (kc == Keys.Space || kc == Keys.Back || kc == Keys.Delete)
                            return base.ProcessCmdKey(ref msg, keyData);
                    }
                }
                catch { }

                // DPAD
                if (keyData == Keys.Left) { _lastDpadFromRemote = false; HandleDpadMove("left"); return true; }
                if (keyData == Keys.Right) { _lastDpadFromRemote = false; HandleDpadMove("right"); return true; }
                if (keyData == Keys.Up) { _lastDpadFromRemote = false; HandleDpadMove("up"); return true; }
                if (keyData == Keys.Down) { _lastDpadFromRemote = false; HandleDpadMove("down"); return true; }

                // OK
                if (keyData == Keys.Enter || keyData == Keys.Space)
                {
                    _lastDpadFromRemote = false;
                    HandleDpadOk();
                    return true;
                }

                // BACK
                if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                {
                    _lastDpadFromRemote = false;
                    HandleDpadBack();
                    return true;
                }

                return base.ProcessCmdKey(ref msg, keyData);
            }

            // =========================
            // 3) Playback hotkeys del player + passthrough madVR/MPCVR
            // =========================
            if (TryHandlePlaybackShortcut(keyData))
                return true;

            if (TryForwardKeyToRenderer(ref msg))
                return true;

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnAnyMouseActivity(IntPtr hwnd, int msg)
        {
            try
            {
                // Passaggio DPAD -> mouse: non facciamo invalidazioni globali (causano flicker)
                // e non resettiamo lo stato DPAD (causa "scorrimento rotto" / focus che salta).
                // Qui ci limitiamo a:
                //  - nascondere la focus ring (se visibile)
                //  - ricordare il controllo cliccato (così il remote riparte da lì)

                bool inDpadUi = IsAnyLibraryVisible();

                if (!inDpadUi)
                {
                    NotePlaybackPointerAction(msg);
                    return;
                }

                // Hide only the DPAD focus ring (l'adorner invalida da solo l'area precedente).
                try
                {
                    if (_focusRing != null && _focusRing.Visible)
                        _focusRing.Attach(null);
                }
                catch { }

                _lastDpadFromRemote = false;
                try { _cinematicLibraryPage?.SetDpadInputIsRemote(false); } catch { }

                // Se clicca, aggiorna il controllo "focused" (utile per riprendere con remote).
                const int WM_LBUTTONDOWN = 0x0201;
                const int WM_RBUTTONDOWN = 0x0204;
                const int WM_MBUTTONDOWN = 0x0207;
                const int WM_XBUTTONDOWN = 0x020B;
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN)
                {
                    try
                    {
                        var c = Control.FromHandle(hwnd);
                        var f = GetDpadFocusableAncestor(c);
                        if (f != null) _focused = f;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private bool IsPipInputHandle(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || _pipForm == null || _pipForm.IsDisposed)
                    return false;

                Control? control = Control.FromHandle(hwnd);
                Form? form = control?.FindForm();
                return ReferenceEquals(form, _pipForm) || hwnd == _pipForm.Handle;
            }
            catch { return false; }
        }

        private static Control? GetDpadFocusableAncestor(Control? c)
        {
            for (var p = c; p != null; p = p.Parent)
            {
                if (IsDpadFocusable(p)) return p;
            }
            return null;
        }

        private void AttachMouseAnchorTracking(Control? root)
        {
            if (root == null) return;

            try
            {
                root.MouseEnter -= MouseAnchorTracking_MouseEnter;
                root.MouseEnter += MouseAnchorTracking_MouseEnter;
                root.MouseDown -= MouseAnchorTracking_MouseDown;
                root.MouseDown += MouseAnchorTracking_MouseDown;
                root.ControlAdded -= MouseAnchorTracking_ControlAdded;
                root.ControlAdded += MouseAnchorTracking_ControlAdded;
            }
            catch { }

            try
            {
                root.MouseMove -= MouseAnchorTracking_MouseMove;
                root.MouseMove += MouseAnchorTracking_MouseMove;
            }
            catch { }

            try
            {
                foreach (Control child in root.Controls)
                    AttachMouseAnchorTracking(child);
            }
            catch { }
        }

        private void MouseAnchorTracking_ControlAdded(object? sender, ControlEventArgs e)
        {
            try { AttachMouseAnchorTracking(e.Control); } catch { }
        }

        private void SuspendLibraryHoverAnchorUntilMouseMove()
        {
            try
            {
                _suspendLibraryHoverAnchorUntilMouseMove = true;
                try { _libraryHoverAnchorWakePos = Cursor.Position; }
                catch { _libraryHoverAnchorWakePos = new Point(int.MinValue, int.MinValue); }
            }
            catch { }
        }

        private bool TryResumeLibraryHoverAnchorFromPointerIntent()
        {
            if (!_suspendLibraryHoverAnchorUntilMouseMove)
                return true;

            try
            {
                var currentPos = Cursor.Position;
                if (currentPos == _libraryHoverAnchorWakePos)
                    return false;
            }
            catch { }

            _suspendLibraryHoverAnchorUntilMouseMove = false;
            _libraryHoverAnchorWakePos = new Point(int.MinValue, int.MinValue);
            return true;
        }

        private void MouseAnchorTracking_MouseEnter(object? sender, EventArgs e)
        {
            if (_cinematicLibraryPage != null && _cinematicLibraryPage.Visible && !TryResumeLibraryHoverAnchorFromPointerIntent())
                return;

            UpdateMouseFocusAnchor(sender as Control);
        }

        private void MouseAnchorTracking_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!IsAnyLibraryVisible())
                return;

            if (!_suspendLibraryHoverAnchorUntilMouseMove)
                return;

            if (!TryResumeLibraryHoverAnchorFromPointerIntent())
                return;

            UpdateMouseFocusAnchor(sender as Control);
        }

        private void MouseAnchorTracking_MouseDown(object? sender, MouseEventArgs e)
        {
            if (IsAnyLibraryVisible())
            {
                _suspendLibraryHoverAnchorUntilMouseMove = false;
                _libraryHoverAnchorWakePos = new Point(int.MinValue, int.MinValue);
            }

            UpdateMouseFocusAnchor(sender as Control);
        }

        private void UpdateMouseFocusAnchor(Control? control)
        {
            try
            {
                bool inDpadUi = IsAnyLibraryVisible();
                if (!inDpadUi)
                    return;

                var focusTarget = GetDpadFocusableAncestor(control);
                if (focusTarget == null || focusTarget.IsDisposed)
                    return;

                try
                {
                    if (_focusRing != null && _focusRing.Visible)
                        _focusRing.Attach(null);
                }
                catch { }

                _focused = focusTarget;
                _lastDpadFromRemote = false;
                try { _cinematicLibraryPage?.SetDpadInputIsRemote(false); } catch { }
                try
                {
                    if (_cinematicLibraryPage != null && _cinematicLibraryPage.Visible)
                        _cinematicLibraryPage.SyncRemoteZoneFromExternalFocus(focusTarget);
                }
                catch { }
            }
            catch { }
        }

        private sealed class InputModeMessageFilter : IMessageFilter
        {
            private readonly PlayerForm _owner;
            private bool _libraryLeftButtonDownSeen;
            public InputModeMessageFilter(PlayerForm owner) { _owner = owner; }

            public bool PreFilterMessage(ref Message m)
            {
                // NOTE: non intercettiamo WM_MOUSEMOVE: su alcune macchine arrivano micro-movimenti
                // continui (touchpad/jitter) che facevano sparire il focus ring DPAD mentre si naviga.
                // Ci interessano solo azioni "intenzionali" del mouse: click e wheel.
                const int WM_LBUTTONDOWN = 0x0201;
                const int WM_LBUTTONUP = 0x0202;
                const int WM_RBUTTONDOWN = 0x0204;
                const int WM_RBUTTONUP = 0x0205;
                const int WM_CONTEXTMENU = 0x007B;
                const int WM_MBUTTONDOWN = 0x0207;
                const int WM_XBUTTONDOWN = 0x020B;
                const int WM_MOUSEWHEEL = 0x020A;
                const int WM_MOUSEHWHEEL = 0x020E;

                if (_owner.TryRouteCapturedClickToWindowChrome(ref m))
                    return true;

                // COM property pages and the embedded madVR window own their mouse
                // input. Playback's global click/menu recovery must not reroute it.
                if (_owner._settingsHudPage?.Visible == true)
                {
                    _libraryLeftButtonDownSeen = false;
                    return false;
                }

                if (m.Msg == WM_LBUTTONDOWN)
                    _libraryLeftButtonDownSeen = true;

                if (m.Msg == WM_LBUTTONUP)
                {
                    bool recoverLostDown = !_libraryLeftButtonDownSeen
                                           && !_owner._stopping
                                           && _owner.IsAnyLibraryVisible();
                    _libraryLeftButtonDownSeen = false;

                    if (recoverLostDown)
                    {
                        // Alcuni renderer lasciano la loro owned window attiva per un
                        // istante durante il teardown. In quel caso Windows continua a
                        // consegnare il mouse-up alla libreria ma mangia il mouse-down,
                        // rendendo inerti card, navigazione e chrome. Ricostruiamo solo
                        // il down realmente mancante, sul controllo che riceverà l'up.
                        try
                        {
                            _owner.Activate();
                            SetForegroundWindow(_owner.Handle);
                            SendMessage(m.HWnd, WM_LBUTTONDOWN, new IntPtr(1), m.LParam);
                        }
                        catch { }
                    }
                }

                if (_owner.TryDismissPlaybackContextMenuFromGlobalInput(m.Msg, m.WParam, m.HWnd))
                    return true;

                if (m.Msg == WM_LBUTTONUP && _owner.ConsumeContextMenuClickThrough(m.Msg))
                    return true;

                // Il mini-player è una superficie autonoma: i suoi controlli non
                // devono risvegliare HUD, menu o focus della finestra principale.
                if (_owner.IsPipInputHandle(m.HWnd))
                    return false;

                // Il menu custom è una superficie autonoma: non propagare il primo
                // click al gestore globale del player, che altrimenti riattiva l'HUD
                // o una card sottostante prima che la voce riceva MouseDown.
                if (_owner.IsContextOverlayInputHandle(m.HWnd))
                    return false;

                if ((m.Msg == WM_RBUTTONUP || m.Msg == WM_CONTEXTMENU) && _owner.IsPlaybackContextMenuVisible())
                    return true;

                if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_MBUTTONDOWN
                    || m.Msg == WM_XBUTTONDOWN || m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL)
                {
                    try { _owner.OnAnyMouseActivity(m.HWnd, m.Msg); } catch { }
                }

                if (m.Msg == WM_CONTEXTMENU)
                {
                    try
                    {
                        if (_owner.TryShowPlaybackContextMenuFromMouseFilter(Control.MousePosition))
                            return true;
                    }
                    catch { }
                }

                if (m.Msg == WM_RBUTTONUP)
                {
                    try
                    {
                        if (_owner.TryShowPlaybackContextMenuFromMouseFilter(Control.MousePosition))
                            return true;
                    }
                    catch { }
                }

                return false; // never swallow messages
            }
        }


        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_CHAR = 0x0102;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_SYSCHAR = 0x0106;
        private const int WM_APPCOMMAND = 0x0319;

        private const int APPCOMMAND_VOLUME_MUTE = 8;
        private const int APPCOMMAND_VOLUME_DOWN = 9;
        private const int APPCOMMAND_VOLUME_UP = 10;
        private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
        private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
        private const int APPCOMMAND_MEDIA_STOP = 13;
        private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
        private const int APPCOMMAND_MEDIA_PLAY = 46;
        private const int APPCOMMAND_MEDIA_PAUSE = 47;
        private const int APPCOMMAND_MEDIA_FAST_FORWARD = 49;
        private const int APPCOMMAND_MEDIA_REWIND = 50;
        private string _lastMediaCommandName = "";
        private string _lastMediaCommandSource = "";
        private DateTime _lastMediaCommandUtc = DateTime.MinValue;

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private List<IntPtr> GetRendererKeyboardTargets()
        {
            var targets = new List<IntPtr>();
            if (_videoHost == null || _videoHost.IsDisposed || !_videoHost.IsHandleCreated)
                return targets;

            try
            {
                var children = new List<(IntPtr Hwnd, long Area)>();
                EnumChildWindows(_videoHost.Handle, (hwnd, _) =>
                {
                    if (IsMpcvrChildOverlayWindow(hwnd)) return true;
                    if (!IsWindowVisible(hwnd)) return true;
                    long area = 0;
                    if (GetWindowRect(hwnd, out var r))
                    {
                        long w = Math.Max(0, r.Right - r.Left);
                        long h = Math.Max(0, r.Bottom - r.Top);
                        area = w * h;
                    }
                    children.Add((hwnd, area));
                    return true;
                }, IntPtr.Zero);

                foreach (var child in children.OrderByDescending(c => c.Area))
                {
                    if (child.Hwnd != IntPtr.Zero && !targets.Contains(child.Hwnd))
                        targets.Add(child.Hwnd);
                }
            }
            catch { }

            if (!targets.Contains(_videoHost.Handle))
                targets.Add(_videoHost.Handle);

            return targets;
        }

        private IntPtr FindBestRendererHwnd()
        {
            var targets = GetRendererKeyboardTargets();
            return targets.Count > 0 ? targets[0] : IntPtr.Zero;
        }

        private bool IsPlayerReservedKey(Keys keyData)
        {
            // Le hotkey del player (quelle che NON devono andare a madVR)
            if ((keyData & Keys.Modifiers) != Keys.None) return false;

            return keyData == Keys.Space ||
                   keyData == Keys.Enter ||
                   keyData == Keys.S ||
                   keyData == Keys.F ||
                   keyData == Keys.Z ||
                   keyData == Keys.O ||
                   keyData == Keys.Left ||
                   keyData == Keys.Right ||
                   keyData == Keys.Up ||
                   keyData == Keys.Down ||
                   keyData == Keys.PageUp ||
                   keyData == Keys.PageDown ||
                   keyData == Keys.BrowserBack ||
                   keyData == Keys.Back ||
                   keyData == Keys.VolumeUp ||
                   keyData == Keys.VolumeDown ||
                   keyData == Keys.VolumeMute ||
                   keyData == Keys.MediaPlayPause ||
                   keyData == Keys.MediaStop ||
                   keyData == Keys.MediaNextTrack ||
                   keyData == Keys.MediaPreviousTrack;
        }

        private static bool IsPureModifierKey(Keys keyData)
        {
            var keyCode = keyData & Keys.KeyCode;
            return keyCode == Keys.ControlKey ||
                   keyCode == Keys.ShiftKey ||
                   keyCode == Keys.Menu ||
                   keyCode == Keys.LControlKey ||
                   keyCode == Keys.RControlKey ||
                   keyCode == Keys.LShiftKey ||
                   keyCode == Keys.RShiftKey ||
                   keyCode == Keys.LMenu ||
                   keyCode == Keys.RMenu;
        }

        private static bool IsBareFunctionKey(Keys keyData)
        {
            if ((keyData & Keys.Modifiers) != Keys.None)
                return false;

            var keyCode = keyData & Keys.KeyCode;
            return keyCode >= Keys.F1 && keyCode <= Keys.F24;
        }

        private bool IsPlaybackKeyboardContext()
        {
            // Il gate vive nell'OverlayHost (una finestra owned separata) e può avere
            // il focus quando non esiste ancora alcun engine. Il filtro globale deve
            // comunque inoltrare i suoi comandi al PlayerForm.
            if (_preOpenPlaceholderGateActive) return true;
            if (_engine == null) return false;
            if (IsPhotoMode) return false;
            if (IsAnyLibraryVisible()) return false;
            if (_hud?.DpadMode == true) return false;
            return true;
        }

        private bool TryHandleMediaVirtualKey(Keys keyData, string source)
        {
            if ((keyData & Keys.Modifiers) != Keys.None)
                return false;

            try
            {
                switch (keyData)
                {
                    case Keys.VolumeUp:
                        if (!BeginMediaCommand("volume-up", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: VolumeUp -> player volume +5%");
                        _hud?.PerformVolumeDelta(+0.05f, ApplyVolume);
                        return true;

                    case Keys.VolumeDown:
                        if (!BeginMediaCommand("volume-down", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: VolumeDown -> player volume -5%");
                        _hud?.PerformVolumeDelta(-0.05f, ApplyVolume);
                        return true;

                    case Keys.VolumeMute:
                        if (!BeginMediaCommand("mute", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: VolumeMute -> player mute toggle");
                        try { _hud?.ToggleMuteFromUser(); } catch { }
                        return true;

                    case Keys.MediaPlayPause:
                        if (!BeginMediaCommand("play-pause", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: MediaPlayPause -> TogglePlayPause()");
                        TogglePlayPause();
                        return true;

                    case Keys.MediaStop:
                        if (!BeginMediaCommand("stop", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: MediaStop -> CloseCurrentToLibrary()");
                        CloseCurrentToLibrary();
                        return true;

                    case Keys.MediaNextTrack:
                        if (!BeginMediaCommand("forward", source)) return true;
                        HandleMediaBackForward(+1, source, "MediaNextTrack");
                        return true;

                    case Keys.MediaPreviousTrack:
                        if (!BeginMediaCommand("back", source)) return true;
                        HandleMediaBackForward(-1, source, "MediaPreviousTrack");
                        return true;
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[MEDIAKEY] {source}: virtual key {keyData} failed: {ex.Message}");
                return true;
            }

            return false;
        }

        private bool BeginMediaCommand(string commandName, string source)
        {
            DateTime now = DateTime.UtcNow;
            double ms = (now - _lastMediaCommandUtc).TotalMilliseconds;
            if (string.Equals(_lastMediaCommandName, commandName, StringComparison.Ordinal) &&
                !string.Equals(_lastMediaCommandSource, source, StringComparison.Ordinal) &&
                ms >= 0 && ms < 250)
            {
                Dbg.Log($"[MEDIAKEY] {source}: duplicate {commandName} suppressed after {_lastMediaCommandSource} ({ms:0} ms)", Dbg.LogLevel.Verbose);
                return false;
            }

            _lastMediaCommandName = commandName;
            _lastMediaCommandSource = source;
            _lastMediaCommandUtc = now;
            return true;
        }

        private void HandleMediaBackForward(int dir, string source, string label)
        {
            StopRemoteScan();

            if (TrySkipPlaybackQueue(dir))
            {
                Dbg.Log($"[MEDIAKEY] {source}: {label} -> playback queue {(dir > 0 ? "next" : "previous")}");
                return;
            }

            double delta = dir > 0 ? +10 : -10;
            Dbg.Log($"[MEDIAKEY] {source}: {label} -> SeekRelative({delta:+0;-0}s)");
            SeekRelative(delta);
            try
            {
                _hud?.Pulse(dir > 0 ? HudOverlay.ButtonId.Fwd10 : HudOverlay.ButtonId.Back10);
                _hud?.ShowOnce(900);
            }
            catch { }
        }

        private bool TryHandleFunctionRowMediaKey(Keys keyData, string source)
        {
            if (!IsPlaybackKeyboardContext())
                return false;
            if ((keyData & Keys.Modifiers) != Keys.None)
                return false;

            try
            {
                switch (keyData & Keys.KeyCode)
                {
                    // Fallback per tastiere che inviano Fn+F5/F6/... come F5/F6/...
                    // invece di VK_MEDIA_* o WM_APPCOMMAND.
                    case Keys.F5:
                        if (!BeginMediaCommand("back", source)) return true;
                        HandleMediaBackForward(-1, source, "F5 fallback");
                        return true;

                    case Keys.F6:
                        if (!BeginMediaCommand("forward", source)) return true;
                        HandleMediaBackForward(+1, source, "F6 fallback");
                        return true;

                    case Keys.F7:
                        if (!BeginMediaCommand("play-pause", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: F7 fallback -> TogglePlayPause()");
                        TogglePlayPause();
                        return true;

                    case Keys.F8:
                        if (!BeginMediaCommand("stop", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: F8 fallback -> CloseCurrentToLibrary()");
                        CloseCurrentToLibrary();
                        return true;

                    case Keys.F9:
                        if (!BeginMediaCommand("mute", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: F9 fallback -> mute toggle");
                        try { _hud?.ToggleMuteFromUser(); } catch { }
                        return true;

                    case Keys.F10:
                        if (!BeginMediaCommand("volume-down", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: F10 fallback -> player volume -5%");
                        _hud?.PerformVolumeDelta(-0.05f, ApplyVolume);
                        return true;

                    case Keys.F11:
                        if (!BeginMediaCommand("volume-up", source)) return true;
                        Dbg.Log($"[MEDIAKEY] {source}: F11 fallback -> player volume +5%");
                        _hud?.PerformVolumeDelta(+0.05f, ApplyVolume);
                        return true;
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[MEDIAKEY] {source}: function-row fallback {keyData} failed: {ex.Message}");
                return true;
            }

            return false;
        }

        private bool HandleAppCommand(int command)
        {
            try
            {
                switch (command)
                {
                    case APPCOMMAND_MEDIA_PLAY_PAUSE:
                        if (!BeginMediaCommand("play-pause", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: MEDIA_PLAY_PAUSE -> TogglePlayPause()");
                        TogglePlayPause();
                        return true;

                    case APPCOMMAND_MEDIA_PLAY:
                        if (!BeginMediaCommand("play", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: MEDIA_PLAY -> Play if paused");
                        if (_paused) TogglePlayPause();
                        return true;

                    case APPCOMMAND_MEDIA_PAUSE:
                        if (!BeginMediaCommand("pause", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: MEDIA_PAUSE -> Pause if playing");
                        if (!_paused) TogglePlayPause();
                        return true;

                    case APPCOMMAND_MEDIA_STOP:
                        if (!BeginMediaCommand("stop", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: MEDIA_STOP -> CloseCurrentToLibrary()");
                        CloseCurrentToLibrary();
                        return true;

                    case APPCOMMAND_MEDIA_NEXTTRACK:
                        if (!BeginMediaCommand("forward", "WM_APPCOMMAND")) return true;
                        HandleMediaBackForward(+1, "WM_APPCOMMAND", "MEDIA_NEXTTRACK");
                        return true;

                    case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                        if (!BeginMediaCommand("back", "WM_APPCOMMAND")) return true;
                        HandleMediaBackForward(-1, "WM_APPCOMMAND", "MEDIA_PREVIOUSTRACK");
                        return true;

                    case APPCOMMAND_MEDIA_FAST_FORWARD:
                        if (!BeginMediaCommand("forward", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: MEDIA_FAST_FORWARD -> SeekRelative(+10)");
                        SeekRelative(+10);
                        return true;

                    case APPCOMMAND_MEDIA_REWIND:
                        if (!BeginMediaCommand("back", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: MEDIA_REWIND -> SeekRelative(-10)");
                        SeekRelative(-10);
                        return true;

                    case APPCOMMAND_VOLUME_UP:
                        if (!BeginMediaCommand("volume-up", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: VOLUME_UP -> player volume +5%");
                        _hud?.PerformVolumeDelta(+0.05f, ApplyVolume);
                        return true;

                    case APPCOMMAND_VOLUME_DOWN:
                        if (!BeginMediaCommand("volume-down", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: VOLUME_DOWN -> player volume -5%");
                        _hud?.PerformVolumeDelta(-0.05f, ApplyVolume);
                        return true;

                    case APPCOMMAND_VOLUME_MUTE:
                        if (!BeginMediaCommand("mute", "WM_APPCOMMAND")) return true;
                        Dbg.Log("[MEDIAKEY] WM_APPCOMMAND: VOLUME_MUTE -> player mute toggle");
                        try { _hud?.ToggleMuteFromUser(); } catch { }
                        return true;

                    default:
                        Dbg.Log($"[MEDIAKEY] WM_APPCOMMAND: unhandled command={command}", Dbg.LogLevel.Verbose);
                        return false;
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[MEDIAKEY] WM_APPCOMMAND command={command} failed: {ex.Message}");
                return true;
            }
        }

        private bool IsRendererMessageWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || _videoHost == null || _videoHost.IsDisposed || !_videoHost.IsHandleCreated)
                    return false;
                return hwnd == _videoHost.Handle || IsChild(_videoHost.Handle, hwnd);
            }
            catch { return false; }
        }

        private bool TryHandlePlaybackShortcut(Keys keyData)
        {
            if (!IsPlaybackKeyboardContext()) return false;
            if ((keyData & Keys.Modifiers) != Keys.None) return false;

            if (_preOpenPlaceholderGateActive)
            {
                if (keyData == Keys.Enter || keyData == Keys.Space)
                    return TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: false);
                if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
                    return TryConsumePreOpenPlaceholderGate(startNow: false, fromRemote: false);
            }

            if (keyData == Keys.Enter)
            {
                if (_hud != null)
                {
                    try { _hud.TimelineVisible = _duration > 0; } catch { }
                    if (!_hud.Visible) _hud.Visible = true;
                    try { if (_hud.DpadMode) _hud.DpadDeactivate(); } catch { }
                    _hud.ShowOnce(2200);
                    try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                    return true;
                }
            }

            if (keyData == Keys.Space) { TogglePlayPause(); return true; }
            if (keyData == Keys.S) { CloseCurrentToLibrary(); return true; }
            if (keyData == Keys.F) { ToggleFullscreen(); try { _hud?.Pulse(HudOverlay.ButtonId.Fullscreen); } catch { } return true; }
            if (keyData == (Keys.Shift | Keys.F)) { ToggleExtendedFullscreen(); return true; }
            // Dimensionamento dell'immagine: Riempi -> Altezza costante -> Area costante -> Personalizzata.
            if (keyData == Keys.Z && _currentMediaHasVideo) { CycleImageSizingMode(); return true; }
            // Ritardo audio dal vivo: Ctrl +/- 10 ms, Ctrl+Maiusc +/- 100 ms, Ctrl+0 azzera.
            if (keyData is (Keys.Control | Keys.Oemplus) or (Keys.Control | Keys.Add)) { AdjustAudioDelayLive(+10); return true; }
            if (keyData is (Keys.Control | Keys.OemMinus) or (Keys.Control | Keys.Subtract)) { AdjustAudioDelayLive(-10); return true; }
            if (keyData is (Keys.Control | Keys.Shift | Keys.Oemplus) or (Keys.Control | Keys.Shift | Keys.Add)) { AdjustAudioDelayLive(+100); return true; }
            if (keyData is (Keys.Control | Keys.Shift | Keys.OemMinus) or (Keys.Control | Keys.Shift | Keys.Subtract)) { AdjustAudioDelayLive(-100); return true; }
            if (keyData is (Keys.Control | Keys.D0) or (Keys.Control | Keys.NumPad0)) { AdjustAudioDelayLive(0, reset: true); return true; }
            if (keyData == Keys.O) { OpenFile(); try { _hud?.Pulse(HudOverlay.ButtonId.Open); } catch { } return true; }
            if (keyData == Keys.Left) { SeekRelative(-10); try { _hud?.Pulse(HudOverlay.ButtonId.Back10); } catch { } return true; }
            if (keyData == Keys.Right) { SeekRelative(10); try { _hud?.Pulse(HudOverlay.ButtonId.Fwd10); } catch { } return true; }
            if (keyData == Keys.Up) { try { if (!TryStepVolumeBoost(+1)) { _hud?.PerformVolumeDelta(+0.05f, ApplyVolume); _hud?.ShowOnce(1200); } } catch { } return true; }
            if (keyData == Keys.Down) { try { if (!TryStepVolumeBoost(-1)) { _hud?.PerformVolumeDelta(-0.05f, ApplyVolume); _hud?.ShowOnce(1200); } } catch { } return true; }
            if (keyData == Keys.PageUp) { SeekChapter(+1); try { _hud?.Pulse(HudOverlay.ButtonId.NextChapter); } catch { } return true; }
            if (keyData == Keys.PageDown) { SeekChapter(-1); try { _hud?.Pulse(HudOverlay.ButtonId.PrevChapter); } catch { } return true; }
            if (keyData == Keys.Escape || keyData == Keys.BrowserBack || keyData == Keys.Back)
            {
                _lastDpadFromRemote = false;
                HandleDpadBack();
                return true;
            }

            return false;
        }

        private bool TryForwardKeyToRenderer(ref Message m)
        {
            // v2.7: forwarding ibrido per madVR/MPCVR.
            // - niente broadcast a tutti gli hwnd figli (causava eccezioni/spam/crash nativi)
            // - SendInput sul keydown con guard anti-ricorsione, perché madVR spesso ascolta input reale/focus, non solo messaggi postati.
            if (m.Msg == WM_CHAR || m.Msg == WM_SYSCHAR)
                return false;

            if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN)
                return false;

            if (System.Threading.Volatile.Read(ref _rendererSyntheticKeyForwarding) != 0)
                return false;

            if (!IsPlaybackKeyboardContext())
                return false;

            try
            {
                if (!(_engine?.HasDisplayControl() == true))
                    return false;
            }
            catch { return false; }

            Keys keyData = (Keys)m.WParam.ToInt32() | ModifierKeys;
            if (TryHandleFunctionRowMediaKey(keyData, "TryForwardKeyToRenderer"))
                return true;

            if (IsPureModifierKey(keyData))
                return false;

            if (IsBareFunctionKey(keyData))
            {
                Dbg.Log($"[HOTKEY] Bare function key {keyData} not forwarded to renderer; leaving it to application/OS.", Dbg.LogLevel.Verbose);
                return false;
            }

            if (IsPlayerReservedKey(keyData))
                return false;

            if (IsRendererMessageWindow(m.HWnd))
                return false;

            try
            {
                if (_engine?.TrySendRendererKey(keyData) == true)
                    return true;
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[HOTKEY] Renderer COM key path failed for {keyData}: {ex.Message}");
            }

            IntPtr hwnd = FindBestRendererHwnd();
            if (hwnd == IntPtr.Zero && _videoHost != null && !_videoHost.IsDisposed && _videoHost.IsHandleCreated)
                hwnd = _videoHost.Handle;

            if (hwnd == IntPtr.Zero || hwnd == Handle)
                return false;

            Dbg.Log($"[HOTKEY] Forwarding {keyData} to renderer hwnd=0x{hwnd.ToInt64():X}, class='{GetWindowClassName(hwnd)}', fullscreen={FormBorderStyle == FormBorderStyle.None}");
            bool posted = false;
            try
            {
                posted = PostMessage(hwnd, m.Msg, m.WParam, m.LParam);
                Dbg.Log($"[HOTKEY] PostMessage to renderer result={posted}, key={keyData}, hwnd=0x{hwnd.ToInt64():X}", Dbg.LogLevel.Info);
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[HOTKEY] PostMessage to renderer failed for {keyData}: {ex.Message}");
            }

            BeginRendererSyntheticKey(hwnd, keyData);

            return true;
        }

        private void BeginRendererSyntheticKey(IntPtr hwnd, Keys keyData)
        {
            var keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.None)
                return;

            if (System.Threading.Interlocked.Exchange(ref _rendererSyntheticKeyForwarding, 1) != 0)
                return;

            try
            {
                BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (hwnd != IntPtr.Zero)
                        {
                            try { SetForegroundWindow(Handle); } catch { }
                            try { SetFocus(hwnd); } catch { }
                        }

                        await Task.Delay(15).ConfigureAwait(true);
                        SendRendererKeyPress(keyData);
                    }
                    catch { }
                    finally
                    {
                        try
                        {
                            var releaseTimer = new System.Windows.Forms.Timer { Interval = 90 };
                            releaseTimer.Tick += (_, __) =>
                            {
                                try { releaseTimer.Stop(); releaseTimer.Dispose(); } catch { }
                                System.Threading.Interlocked.Exchange(ref _rendererSyntheticKeyForwarding, 0);
                            };
                            releaseTimer.Start();
                        }
                        catch
                        {
                            System.Threading.Interlocked.Exchange(ref _rendererSyntheticKeyForwarding, 0);
                        }
                    }
                }));
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref _rendererSyntheticKeyForwarding, 0);
            }
        }

        private static void SendRendererKeyPress(Keys keyData)
        {
            var keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.None)
                return;

            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;

            var inputs = new List<INPUT>(8);

            void Down(Keys key) => inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)key, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = IntPtr.Zero } }
            });

            void Up(Keys key) => inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)key, wScan = 0, dwFlags = KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero } }
            });

            if (ctrl) Down(Keys.ControlKey);
            if (alt) Down(Keys.Menu);
            if (shift) Down(Keys.ShiftKey);

            Down(keyCode);
            Up(keyCode);

            if (shift) Up(Keys.ShiftKey);
            if (alt) Up(Keys.Menu);
            if (ctrl) Up(Keys.ControlKey);

            try { SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()); } catch { }
        }



        private sealed class PlaybackKeyboardMessageFilter : IMessageFilter
        {
            private readonly PlayerForm _owner;
            public PlaybackKeyboardMessageFilter(PlayerForm owner) { _owner = owner; }

            private bool IsOwnedByModalDialogOrTextInput(IntPtr hwnd)
            {
                Control? target = Control.FromChildHandle(hwnd);
                if (target == null) return false;
                if (target is TextBoxBase || target is ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList })
                    return true;
                Form? form = target.FindForm();
                return form != null && !ReferenceEquals(form, _owner) && form.Modal;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg == WM_APPCOMMAND)
                {
                    try
                    {
                        long raw = m.LParam.ToInt64();
                        int command = (int)((raw >> 16) & 0x0FFF);
                        Dbg.Log($"[MEDIAKEY] IMessageFilter WM_APPCOMMAND raw=0x{raw:X}, command={command}", Dbg.LogLevel.Info);
                        return _owner.HandleAppCommand(command);
                    }
                    catch
                    {
                        return false;
                    }
                }

                if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN)
                    return false;

                try
                {
                    if (_owner == null || _owner.IsDisposed || !_owner.IsHandleCreated)
                        return false;
                    if (_owner._settingsHudPage?.Visible == true)
                        return false;
                    // Dialoghi modali (sincronizzazione audio, credenziali…) e campi di
                    // testo possiedono la tastiera: Backspace non deve chiudere il film,
                    // S non deve fermarlo e le cifre non devono arrivare a madVR.
                    if (IsOwnedByModalDialogOrTextInput(m.HWnd))
                        return false;
                    Keys keyData = (Keys)m.WParam.ToInt32() | Control.ModifierKeys;
                    if (keyData == (Keys.Alt | Keys.F4) || keyData == (Keys.Alt | Keys.Space))
                        return false;
                    // Media keys remain available while browsing music; ordinary
                    // typing and navigation keys still belong to the library.
                    if (_owner.HasMusicPlayback && _owner.TryHandleMediaVirtualKey(keyData, "IMessageFilter"))
                        return true;
                    if (!_owner.IsPlaybackKeyboardContext())
                        return false;
                    if (_owner.TryHandleMediaVirtualKey(keyData, "IMessageFilter"))
                        return true;
                    if (_owner.TryHandleFunctionRowMediaKey(keyData, "IMessageFilter"))
                        return true;

                    if (_owner.IsRendererMessageWindow(m.HWnd) && m.LParam == IntPtr.Zero)
                    {
                        Dbg.Log($"[HOTKEY] Renderer message with empty lParam left to renderer: key={(Keys)m.WParam.ToInt32()}, hwnd=0x{m.HWnd.ToInt64():X}", Dbg.LogLevel.Verbose);
                        return false;
                    }

                    if (_owner.IsPlayerReservedKey(keyData))
                        return _owner.TryHandlePlaybackShortcut(keyData);

                    // Le hotkey madVR passano da sole quando il focus è già sul renderer.
                    if (_owner.IsRendererMessageWindow(m.HWnd))
                        return false;

                    return _owner.TryForwardKeyToRenderer(ref m);
                }
                catch
                {
                    return false;
                }
            }
        }

        // Blocca tasto destro + ascolta hot-plug audio per ri-verifica PCM/Bitstream
        protected override void WndProc(ref Message m)
        {
            const int WM_NCLBUTTONDOWN = 0x00A1;
            const int WM_NCRBUTTONDOWN = 0x00A4;
            if (m.Msg == WM_NCLBUTTONDOWN || m.Msg == WM_NCRBUTTONDOWN)
            {
                ReleaseStaleCinecoreMouseCapture(force: true);
                // DefWindowProc runs a nested message loop while a caption button
                // is held. Timers must not release its capture or reorder overlays.
                bool previousChromeInteraction = _nativeChromeInteraction;
                _nativeChromeInteraction = true;
                try { base.WndProc(ref m); }
                finally { _nativeChromeInteraction = previousChromeInteraction; }
                return;
            }

            if (m.Msg == WM_APPCOMMAND)
            {
                long raw = 0;
                int command = 0;
                try
                {
                    raw = m.LParam.ToInt64();
                    command = (int)((raw >> 16) & 0x0FFF);
                    Dbg.Log($"[MEDIAKEY] WndProc WM_APPCOMMAND raw=0x{raw:X}, command={command}", Dbg.LogLevel.Info);
                }
                catch { }

                if (HandleAppCommand(command))
                {
                    m.Result = (IntPtr)1;
                    return;
                }
            }

            if (m.Msg == WM_MPCVR_SWITCH_FULLSCREEN && IsMpcvrActive)
            {
                HandleMpcvrSwitchFullscreen(m.WParam != IntPtr.Zero);
                m.Result = IntPtr.Zero;
                return;
            }

            // Forward sicuro verso madVR/MPCVR: solo keydown/keyup, mai WM_CHAR.
            if (m.Msg == WM_KEYDOWN || m.Msg == WM_KEYUP ||
                m.Msg == WM_SYSKEYDOWN || m.Msg == WM_SYSKEYUP)
            {
                // Le combinazioni della chrome appartengono a Windows, non al
                // renderer. In particolare Alt+F4 e Alt+Spazio devono continuare a
                // chiudere/aprire il menu di sistema anche durante il playback.
                Keys systemKey = (Keys)m.WParam.ToInt32() | ModifierKeys;
                if (systemKey == (Keys.Alt | Keys.F4) ||
                    systemKey == (Keys.Alt | Keys.Space))
                {
                    base.WndProc(ref m);
                    return;
                }

                if (TryForwardKeyToRenderer(ref m))
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
            }

            // DirectShow graph notify (EC_COMPLETE) → ritorno libreria affidabile
            if (m.Msg == WM_GRAPHNOTIFY)
            {
                try { DrainGraphEvents(); } catch { }
                m.Result = IntPtr.Zero;
                return;
            }

            const int WM_CONTEXTMENU = 0x007B;
            const int WM_DEVICECHANGE = 0x0219;
            const int DBT_DEVNODES_CHANGED = 0x0007;
            const int DBT_DEVICEARRIVAL = 0x8000;
            const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

            // Passaggio DPAD -> mouse:
            // Non sganciamo/azzera... su ogni mousemove o wheel (causava flicker e "sparizioni"),
            // ma SOLO quando l'utente clicca davvero con il mouse dentro UI DPAD.
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_RBUTTONUP = 0x0205;
            const int WM_MBUTTONDOWN = 0x0207;
            const int WM_XBUTTONDOWN = 0x020B;

            bool inDpadUi = IsAnyLibraryVisible();

            if (inDpadUi && (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_MBUTTONDOWN || m.Msg == WM_XBUTTONDOWN))
            {
                // L'utente sta usando il mouse: disattacca solo la ring (niente reset aggressivo).
                try { _focusRing.Attach(null); } catch { }

                // Aggiorna "focused" in base al controllo cliccato: quando riprendi il remote
                // riparti da li' e non da un riquadro vecchio.
                try { UpdateMouseFocusAnchor(Control.FromHandle(m.HWnd)); } catch { }
            }

            if (!inDpadUi && m.Msg == WM_RBUTTONDOWN)
                try { NotePlaybackPointerAction(m.Msg); } catch { }

            if (m.Msg == WM_CONTEXTMENU || m.Msg == WM_RBUTTONUP)
            {
                if (_videoLoading?.Visible == true && !IsAnyLibraryVisible()) { m.Result = IntPtr.Zero; return; }
                if (CanShowPlaybackContextMenu())
                {
                    Point screenPoint = GetNativeMouseScreenPoint(m.HWnd, m.Msg, m.LParam);
                    ShowPlayerContextMenuAtScreen(screenPoint);
                    m.Result = IntPtr.Zero;
                    return;
                }
            }

            if (m.Msg == WM_DEVICECHANGE)
            {
                int ev = m.WParam.ToInt32();
                if (ev == DBT_DEVNODES_CHANGED || ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { RecheckAudioNow(); } catch { }
                    }));
                }
            }

            base.WndProc(ref m);
        }


    }
}
