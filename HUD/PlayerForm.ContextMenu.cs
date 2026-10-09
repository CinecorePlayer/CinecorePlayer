#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private DateTime _suppressContextMenuMouseUpUntilUtc = DateTime.MinValue;

        private bool ConsumeContextMenuClickThrough(int message)
        {
            if (message is not (0x0202 or 0x0205 or 0x0208 or 0x020C) || DateTime.UtcNow > _suppressContextMenuMouseUpUntilUtc)
                return false;
            _suppressContextMenuMouseUpUntilUtc = DateTime.MinValue;
            return true;
        }

        private bool TryDismissPlaybackContextMenuFromGlobalInput(int message, IntPtr keyValue, IntPtr hwnd)
        {
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_MBUTTONDOWN = 0x0207;
            const int WM_XBUTTONDOWN = 0x020B;

            if (!IsPlaybackContextMenuVisible())
                return false;

            if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) &&
                keyValue.ToInt32() == (int)Keys.Escape)
            {
                ResetPlaybackContextMenuState();
                return true;
            }

            // A dismissal gesture belongs to the popup, not the underlying player.
            if ((message == WM_LBUTTONDOWN || message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN) &&
                !IsContextOverlayInputHandle(hwnd))
            {
                ResetPlaybackContextMenuState();
                _suppressContextMenuMouseUpUntilUtc = DateTime.UtcNow.AddMilliseconds(420);
                return true;
            }

            return false;
        }

        private void MarkContextMenuPending()
        {
            _contextMenuPending = true;
            _lastHudActivityUtc = DateTime.UtcNow;
            _lastMouseMoveUtc = DateTime.UtcNow;
            // Keep the existing HUD/vignette surfaces and opacity during popup open.
            _suppressHudWakeUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
            try { EnsureCursorVisible(); } catch { }
            QueueContextMenuStuckReset(900);
        }

        private bool HandlePlaybackContextMenuOpening(CancelEventArgs e)
        {
            try
            {
                if (e.Cancel)
                    return false;

                if (!_preparingCustomContextMenu)
                {
                    e.Cancel = true;
                    if (!IsPlaybackContextMenuVisible() && CanShowPlaybackContextMenu())
                    {
                        Point screenPoint = Control.MousePosition;
                        try { TryBeginInvokeOnUi(() => ShowPlayerContextMenuAtScreen(screenPoint)); } catch { }
                    }
                    return false;
                }

                if (!CanShowPlaybackContextMenu())
                {
                    EndContextMenuHudBlock();
                    e.Cancel = true;
                    return false;
                }

                if (!_contextMenuPending && !_contextMenuActive)
                {
                    if (IsAnyLibraryVisible())
                    {
                        BeginContextMenuHudBlock();
                        return true;
                    }

                    e.Cancel = true;
                    Point screenPoint = Control.MousePosition;
                    try { TryBeginInvokeOnUi(() => ShowPlayerContextMenuAtScreen(screenPoint)); } catch { }
                    return false;
                }

                BeginContextMenuHudBlock();
                return true;
            }
            catch
            {
                e.Cancel = true;
                return false;
            }
        }

        private void AttachPlaybackContextMenuFallbacks()
        {
            try
            {
                AttachPlaybackContextMenuFallback(_videoHost);
                AttachPlaybackContextMenuFallback(_hud);
                AttachPlaybackContextMenuFallback(_infoOverlay);
                AttachPlaybackContextMenuFallback(_remoteOsd);
                AttachPlaybackContextMenuFallback(_overlayHost);
                AttachPlaybackContextMenuFallback(_overlayHost?.Surface);
                AttachPlaybackContextMenuFallback(_overlayInlineHost);
                AttachPlaybackContextMenuFallback(_mpcvrOverlayPopup?.Surface);
                AttachPlaybackContextMenuFallback(_mpcvrChildOverlayHost);
                AttachPlaybackContextMenuFallback(_mpcvrOverlayStagingHost?.Surface);
                AttachPlaybackContextMenuFallback(_pausePlaceholder);
                AttachPlaybackContextMenuFallback(_photoHud);
                AttachPlaybackContextMenuFallback(_audioOnlyBanner);
                AttachPlaybackContextMenuFallback(_netflixModePage);
                AttachPlaybackContextMenuFallback(_cinematicLibraryPage);
            }
            catch { }
        }

        private void AttachPlaybackContextMenuFallback(Control? root)
        {
            if (root == null || root.IsDisposed)
                return;

            try
            {
                // A right click must open exactly once. Opening on MouseDown and again
                // on MouseUp used to recreate the overlay underneath the user's click,
                // which looked like flicker and made rows appear unresponsive.
                root.MouseDown -= PlaybackContextMenuFallback_MouseDown;
                root.MouseUp -= PlaybackContextMenuFallback_MouseUp;
                root.MouseUp += PlaybackContextMenuFallback_MouseUp;
                root.ControlAdded -= PlaybackContextMenuFallback_ControlAdded;
                root.ControlAdded += PlaybackContextMenuFallback_ControlAdded;

                foreach (Control child in root.Controls)
                    AttachPlaybackContextMenuFallback(child);
            }
            catch { }
        }

        private void PlaybackContextMenuFallback_ControlAdded(object? sender, ControlEventArgs e)
        {
            try { AttachPlaybackContextMenuFallback(e.Control); } catch { }
        }

        private void PlaybackContextMenuFallback_MouseDown(object? sender, MouseEventArgs e)
        {
            TryShowPlaybackContextMenuFromMouseEvent(sender, e);
        }

        private void PlaybackContextMenuFallback_MouseUp(object? sender, MouseEventArgs e)
        {
            TryShowPlaybackContextMenuFromMouseEvent(sender, e);
        }

        private void TryShowPlaybackContextMenuFromMouseEvent(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;

            try
            {
                if (!CanShowPlaybackContextMenu())
                    return;

                var control = sender as Control;
                Point screenPoint = control != null ? control.PointToScreen(e.Location) : Control.MousePosition;
                ShowPlayerContextMenuAtScreen(screenPoint);
            }
            catch { }
        }

        private void ShowPlayerContextMenuAtScreen(Point screenPoint)
        {
            // In libreria alcuni elementi hanno un loro menu sul tasto destro: quello del player non gli va sopra.
            try { if (_engine == null && _cinematicLibraryPage?.OwnsRightClickAtCursor() == true) return; } catch { }
            try
            {
                // Il tasto destro e' anche un punto di auto-ripristino: se un drag
                // precedente ha perso il rilascio, libera la cattura prima del popup.
                try { ReleaseStaleCinecoreMouseCapture(); } catch { }

                if (_menu == null || _menu.IsDisposed || !CanShowPlaybackContextMenu())
                    return;

                DateTime now = DateTime.UtcNow;
                if (now - _lastPlaybackContextMenuShowUtc < TimeSpan.FromMilliseconds(450))
                    return;

                if (_contextOverlayMenu != null && !_contextOverlayMenu.IsDisposed)
                {
                    if (_contextOverlayMenu.Visible)
                    {
                        try { _contextOverlayMenu.PromoteToTop(); } catch { }
                        return;
                    }
                    try { _contextOverlayMenu.Close(); } catch { }
                    _contextOverlayMenu = null;
                }

                _lastPlaybackContextMenuShowUtc = now;

                if (_menu.Visible)
                {
                    try { _menu.Close(ToolStripDropDownCloseReason.CloseCalled); } catch { }
                    ResetPlaybackContextMenuState();
                }

                MarkContextMenuPending();
                bool libraryContext = IsAnyLibraryVisible();
                // The fullscreen library window can itself be top-most. Demote it for
                // the lifetime of the popup as well, otherwise the menu is painted but
                // mouse messages still land on the library surface underneath it.
                try { SuspendFullscreenTopMostForContextMenu(); } catch { }
                if (!libraryContext)
                {
                    try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }
                    PreparePlaybackContextMenuOverlay();
                }

                if (!PrepareContextMenuForCustomOpen())
                {
                    ResetPlaybackContextMenuState();
                    return;
                }

                BeginContextMenuHudBlock();
                ShowCustomContextMenuOverlay(screenPoint, libraryContext);
                if (!IsCustomContextMenuVisible())
                    ResetPlaybackContextMenuState();
            }
            catch (Exception ex)
            {
                Dbg.Warn("Context menu open failed: " + ex.Message);
                ResetPlaybackContextMenuState();
            }
        }

        private bool PrepareContextMenuForCustomOpen()
        {
            bool oldPreparing = _preparingCustomContextMenu;
            try
            {
                _preparingCustomContextMenu = true;
                if (_menu is TopMostContextMenuStrip topMostMenu)
                    return topMostMenu.RaiseOpeningForCustomDisplay();

                RefreshMenuVisibility();
                ApplyDarkMenuThemeRecursive(_menu.Items);
                return true;
            }
            catch
            {
                return true;
            }
            finally
            {
                _preparingCustomContextMenu = oldPreparing;
            }
        }

        private ContextMenuStrip? _libraryItemMenu;

        /// <summary>
        /// Menu del tasto destro per un elemento della libreria (rimuovi, correggi): lo stesso menu in
        /// vetro del player, con le sole voci che servono. Prima era un riquadro diverso, senza vetro.
        /// </summary>
        internal void ShowLibraryItemMenu(Point screenPoint, IReadOnlyList<(string Text, string? Icon, Action Run)> entries)
        {
            try
            {
                if (entries == null || entries.Count == 0 || _closingForExit) return;
                try { if (_contextOverlayMenu != null && !_contextOverlayMenu.IsDisposed) _contextOverlayMenu.Close(); } catch { }
                try { _libraryItemMenu?.Dispose(); } catch { }
                var strip = new ContextMenuStrip();
                foreach (var entry in entries)
                {
                    Action run = entry.Run;
                    var item = new ToolStripMenuItem(entry.Text, null, (_, __) => { try { BeginInvoke(new Action(run)); } catch { } });
                    if (!string.IsNullOrWhiteSpace(entry.Icon)) item.Tag = "menu-icon:" + entry.Icon;
                    strip.Items.Add(item);
                }
                _libraryItemMenu = strip;

                MarkContextMenuPending();
                try { SuspendFullscreenTopMostForContextMenu(); } catch { }
                BeginContextMenuHudBlock();
                var overlay = new ContextOverlayMenuForm(this, strip.Items, screenPoint);
                if (!overlay.HasRenderableRows)
                {
                    try { overlay.Dispose(); } catch { }
                    ResetPlaybackContextMenuState();
                    return;
                }
                _contextOverlayMenu = overlay;
                overlay.FormClosed += (_, __) =>
                {
                    if (ReferenceEquals(_contextOverlayMenu, overlay)) _contextOverlayMenu = null;
                    EndContextMenuHudBlock();
                };
                overlay.PrepareFadeIn();
                overlay.Show(this);
                overlay.PromoteToTop();
                try { overlay.Activate(); } catch { }
                try { overlay.Focus(); } catch { }
                if (!overlay.Visible)
                {
                    _contextOverlayMenu = null;
                    ResetPlaybackContextMenuState();
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("Library item menu failed: " + ex.Message);
                ResetPlaybackContextMenuState();
            }
        }

        private void ShowCustomContextMenuOverlay(Point screenPoint, bool libraryContext)
        {
            try
            {
                if (_menu == null || _menu.IsDisposed)
                    return;

                // Sopra un video che la cattura dello schermo non vede, il vetro nasce da un fotogramma del film.
                bool ambientGlass = !libraryContext && !ScreenCaptureSeesVideo;
                var overlay = new ContextOverlayMenuForm(this, _menu.Items, screenPoint, ambientGlass, ambientGlass ? CurrentMenuAmbient() : null);
                if (!overlay.HasRenderableRows)
                {
                    try { overlay.Dispose(); } catch { }
                    ResetPlaybackContextMenuState();
                    return;
                }

                _contextOverlayMenu = overlay;
                overlay.FormClosed += (_, __) =>
                {
                    if (ReferenceEquals(_contextOverlayMenu, overlay))
                        _contextOverlayMenu = null;
                    EndContextMenuHudBlock();
                };

                // Un popup owned resta sempre sopra al player senza dover competere
                // con la sua finestra top-most. Mostrandolo indipendente poteva perdere
                // l'attivazione fra MouseDown e MouseUp e il clic finiva sul player.
                if (libraryContext) overlay.PrepareFadeIn();
                if (ambientGlass) RefreshMenuAmbient(overlay);
                overlay.Show(this);
                overlay.PromoteToTop();
                try { overlay.Activate(); } catch { }
                try { overlay.Focus(); } catch { }

                if (!overlay.Visible)
                {
                    _contextOverlayMenu = null;
                    ResetPlaybackContextMenuState();
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("Custom context overlay failed: " + ex.Message);
                ResetPlaybackContextMenuState();
            }
        }

        private static void PromoteToolStripWindow(ToolStrip strip)
        {
            try
            {
                if (strip == null || strip.IsDisposed || !strip.IsHandleCreated)
                    return;

                var bounds = strip.Bounds;
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    Win32.SetWindowPos(strip.Handle, Win32.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                        Win32.SWP_SHOWWINDOW | Win32.SWP_FRAMECHANGED);
                }
                else
                {
                    Win32.SetWindowPos(strip.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW | Win32.SWP_FRAMECHANGED);
                }
            }
            catch { }
        }

        private bool TryBeginInvokeOnUi(Action action)
        {
            try
            {
                if (_closingForExit || IsDisposed || Disposing || !IsHandleCreated)
                    return false;

                BeginInvoke(action);
                return true;
            }
            catch { return false; }
        }

        private void QueueContextMenuStuckReset(int delayMs)
        {
            try
            {
                _ = Task.Delay(delayMs).ContinueWith(_ =>
                {
                    try
                    {
                        TryBeginInvokeOnUi(() =>
                        {
                            try
                            {
                                bool menuVisible = _menu != null && !_menu.IsDisposed && _menu.Visible;
                                bool overlayVisible = IsCustomContextMenuVisible();
                                if (!menuVisible && !overlayVisible && (_contextMenuActive || _contextMenuPending))
                                    ResetPlaybackContextMenuState();
                            }
                            catch { }
                        });
                    }
                    catch { }
                }, TaskScheduler.Default);
            }
            catch { }
        }

        private void PreparePlaybackContextMenuOverlay()
        {
            if (_closingForExit)
                return;

            // Il menu custom e' gia' una finestra TOPMOST indipendente. Smontare qui
            // gli overlay video faceva sparire l'HUD per un frame all'apertura e lo
            // ricostruiva alla chiusura, causando il flicker denunciato.
            _mpcvrOverlayDemotedForContextMenu = false;
        }

        private void SuspendFullscreenTopMostForContextMenu()
        {
            if (IsMpcvrActive)
                return;

            if (_contextMenuTopMostSuspended || !IsFullscreenOverlayMode())
                return;

            // The custom popup is itself a top-most tool window. Toggling the player
            // between TOPMOST/NOTOPMOST forced native video renderers to rebuild their
            // z-order and produced a very visible flash on every right click.
            // Keeping the playback window stable is both sufficient and flicker-free.
        }

        private void RestoreFullscreenTopMostAfterContextMenu()
        {
            if (!_contextMenuTopMostSuspended)
                return;

            bool restoreForm = _contextMenuRestoreFormTopMost && IsFullscreenOverlayMode() && !_closingForExit;
            _contextMenuTopMostSuspended = false;
            _contextMenuRestoreFormTopMost = false;

            try
            {
                if (restoreForm)
                {
                    TopMost = true;
                    Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_FRAMECHANGED);
                }
            }
            catch { }

            try { ApplyOverlayTopMostPolicy(); } catch { }
        }

        private bool TryShowPlaybackContextMenuFromMouseFilter(Point screenPoint)
        {
            try
            {
                if (_menu == null || _menu.IsDisposed || _menu.Visible || IsCustomContextMenuVisible() || !CanShowPlaybackContextMenu())
                    return false;

                ShowPlayerContextMenuAtScreen(screenPoint);
                return IsPlaybackContextMenuVisible();
            }
            catch { return false; }
        }

        private bool IsPlaybackContextMenuVisible()
        {
            try { return (_menu != null && !_menu.IsDisposed && _menu.Visible) || IsCustomContextMenuVisible(); }
            catch { return false; }
        }

        private bool IsCustomContextMenuVisible()
        {
            try { return _contextOverlayMenu != null && !_contextOverlayMenu.IsDisposed && _contextOverlayMenu.Visible; }
            catch { return false; }
        }

        private bool IsContextOverlayInputHandle(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || _contextOverlayMenu == null || _contextOverlayMenu.IsDisposed)
                    return false;
                Control? control = Control.FromHandle(hwnd);
                return control != null && (ReferenceEquals(control, _contextOverlayMenu) || _contextOverlayMenu.Contains(control));
            }
            catch { return false; }
        }

        private bool CanShowPlaybackContextMenu()
        {
            return !_closingForExit && !IsDisposed && !Disposing;
        }

        private void RestoreLibraryContextMenuAfterPlayback()
        {
            ResetPlaybackContextMenuState();
            _lastPlaybackContextMenuShowUtc = DateTime.MinValue;
            _lastMpcvrContextMenuRequestUtc = DateTime.MinValue;

            if (_cinematicLibraryPage != null && !_cinematicLibraryPage.IsDisposed)
            {
                _cinematicLibraryPage.ContextMenuStrip = _menu;
                AttachPlaybackContextMenuFallback(_cinematicLibraryPage);
            }
            if (_netflixModePage != null && !_netflixModePage.IsDisposed)
            {
                _netflixModePage.ContextMenuStrip = _menu;
                AttachPlaybackContextMenuFallback(_netflixModePage);
            }
            // Host e figli possono essere ricreati durante lo stop del renderer.
            // Riaggancia tutto il grafo dei controlli, non soltanto le due librerie.
            AttachPlaybackContextMenuFallbacks();
        }

        private void ResetPlaybackContextMenuState()
        {
            try { if (_menu != null && !_menu.IsDisposed) _menu.AutoClose = true; } catch { }
            try { if (_contextOverlayMenu != null && !_contextOverlayMenu.IsDisposed) _contextOverlayMenu.Close(); } catch { }
            try { ReleaseStaleCinecoreMouseCapture(); } catch { }
            try { RestoreFullscreenTopMostAfterContextMenu(); } catch { }
            _contextMenuPending = false;
            _contextMenuActive = false;
            _suppressHudWakeUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
        }

        private void BeginContextMenuHudBlock()
        {
            _contextMenuPending = false;
            _contextMenuActive = true;
            try { if (_netflixModePage?.Visible == true) _netflixModePage.SetBackgroundPaused(true); } catch { }
            _lastHudActivityUtc = DateTime.UtcNow;
            _lastMouseMoveUtc = DateTime.UtcNow;
            _suppressHudWakeUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
            try { EnsureCursorVisible(); } catch { }
            try
            {
                // Do not tear down/recreate the overlay surface at menu-open time: on
                // native renderers that was another source of black-frame flicker.
                // _contextMenuActive already blocks HUD input and auto-hide updates.
                _remoteOsd.Visible = false;
            }
            catch { }

            try
            {
                if (!IsAnyLibraryVisible())
                    PreparePlaybackContextMenuOverlay();
            }
            catch { }
        }

        private bool _endingContextMenu;
        private void EndContextMenuHudBlock()
        {
            if (_endingContextMenu) return;
            _endingContextMenu = true;
            try
            {
            try { if (_contextOverlayMenu != null && !_contextOverlayMenu.IsDisposed) _contextOverlayMenu.Close(); } catch { }
            _contextMenuPending = false;
            _contextMenuActive = false;
            _lastHudActivityUtc = DateTime.UtcNow;
            _lastMouseMoveUtc = DateTime.UtcNow;
            _suppressHudWakeUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            if (_hud.RequestedVisible) _hud.ShowOnce(1200);
            try
            {
                _hudWakeAnchorPos = Control.MousePosition;
                _hudWakeLastMousePos = _hudWakeAnchorPos;
                _hudWakeNeedsIntentionalMove = true;
            }
            catch { }

            try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
            try { if (_netflixModePage?.Visible == true) _netflixModePage.SetBackgroundPaused(false); } catch { }
            try { if (_menu != null && !_menu.IsDisposed) _menu.AutoClose = true; } catch { }
            try { RestoreFullscreenTopMostAfterContextMenu(); } catch { }

            try
            {
                if (_mpcvrOverlayDemotedForContextMenu && IsMpcvrActive && !_closingForExit && HasVisibleOverlayHostContent())
                {
                    _mpcvrOverlayDemotedForContextMenu = false;
                    try { ApplyOverlayTopMostPolicy(); } catch { }
                    SafeShowOverlayHost();
                    SyncOverlayToVideoRect();
                    BringOverlaysToFront();
                }
                else
                {
                    _mpcvrOverlayDemotedForContextMenu = false;
                }
            }
            catch { }
            }
            finally { _endingContextMenu = false; }
        }

        private void PrepareCustomContextSubMenu(ToolStripMenuItem item)
        {
            try
            {
                InvokeToolStripMenuItemDropDownOpening(item);

                ApplyDarkMenuThemeRecursive(item.DropDownItems);
            }
            catch { }
        }

        private static void InvokeToolStripMenuItemDropDownOpening(ToolStripMenuItem item)
        {
            try
            {
                var method = typeof(ToolStripMenuItem).GetMethod("OnDropDownShow", BindingFlags.Instance | BindingFlags.NonPublic);
                method?.Invoke(item, new object[] { EventArgs.Empty });
            }
            catch { }
        }

        private void PerformCustomContextMenuClick(ToolStripMenuItem item)
        {
            try
            {
                // Il renderer riceve altrimenti il mouse-up dopo la chiusura del
                // popup e può attivare la card sottostante. Il filtro globale consuma
                // esclusivamente quel rilascio, per una finestra molto breve.
                _suppressContextMenuMouseUpUntilUtc = DateTime.UtcNow.AddMilliseconds(420);
                var overlay = _contextOverlayMenu;
                _contextOverlayMenu = null;
                try { if (overlay != null && !overlay.IsDisposed) overlay.Close(); } catch { }
                try { if (_menu != null && !_menu.IsDisposed && _menu.Visible) _menu.Close(ToolStripDropDownCloseReason.ItemClicked); } catch { }

                // Il popup possiede temporaneamente l'attivazione. Se viene chiuso e
                // l'azione parte mentre il focus e' ancora in transizione, Windows puo'
                // riportare davanti l'applicazione usata in precedenza e lasciare
                // Cinecore sotto. Restituiamo l'attivazione al proprietario PRIMA del
                // comando: eventuali dialog aperti da PerformClick la erediteranno poi
                // normalmente, senza rendere il player permanentemente top-most.
                RestoreOwnerActivationForContextCommand();
                item.PerformClick();
            }
            catch (Exception ex)
            {
                try { Dbg.Warn("Context menu item click EX: " + ex.Message); } catch { }
            }
        }

        private void RestoreOwnerActivationForContextCommand()
        {
            try
            {
                if (_closingForExit || IsDisposed || Disposing || !Visible ||
                    WindowState == FormWindowState.Minimized || !IsHandleCreated)
                    return;

                Activate();
                SetForegroundWindow(Handle);
            }
            catch { }
        }

        private sealed class ContextOverlayMenuForm : Form
        {
            // Misure a scala 1, moltiplicate per la scala del menu: quella dello schermo, oppure
            // quella della finestra del player quando e' piu' grande (a 4K il resto
            // dell'interfaccia cresce con la finestra e il menu, fermo ai pixel fissi, sembrava minuscolo).
            private static float _menuScale = 1f;
            private static int Scaled(int value) => (int)Math.Round(value * _menuScale);
            private static int PanelPadding => Scaled(6);
            private static int RowHeight => Scaled(34);
            private static int SeparatorHeight => Scaled(9);
            private static int IconWidth => Scaled(33);
            private static int ArrowWidth => Scaled(18);
            private const int PanelGap = 0;
            private static int MinPanelWidth => Scaled(232);
            private static int MaxPanelWidth => Scaled(380);
            private static int PanelRadius => Scaled(10);

            private static Color PanelBack => Theme.IsLight ? Theme.Card : Color.FromArgb(27, 31, 38);
            private static Color PanelBorder => Color.FromArgb(55, Theme.SubtleText);
            private static Color RowHot => Color.FromArgb(24, Theme.Accent);
            private static Color RowPressed => Color.FromArgb(40, Theme.Accent);
            private static Color TextNormal => Theme.Text;
            private static Color TextDisabled => Theme.Muted;
            private static Color SeparatorColor => Color.FromArgb(22, Theme.Text);

            private Bitmap? _glassBackdrop;
            private readonly Dictionary<Rectangle, Bitmap> _glassPanels = new();
            // Il vetro non viene dalla cattura dello schermo ma da un'immagine del film (vedi PlayerForm.MenuAmbient):
            // gli angoli del pannello non si riempiono con la cattura, li ritaglia la finestra.
            private readonly bool _ambientGlass;

            private readonly PlayerForm _owner;
            private readonly List<MenuPanelState> _panels = new();
            private readonly HashSet<ToolStripMenuItem> _preparedSubMenus = new();
            private readonly Rectangle _screenBounds;
            private readonly Font _menuFont;
            private readonly System.Windows.Forms.Timer _outsideClickTimer;
            private readonly DateTime _createdUtc = DateTime.UtcNow;
            private ToolStripItem? _hotItem;
            private ToolStripItem? _pressedItem;
            private readonly System.Windows.Forms.Timer _submenuTimer;
            private HitInfo? _pendingHover;

            public bool HasRenderableRows => _panels.Any(panel => panel.Rows.Any(row => row.Item is not ToolStripSeparator));

            public ContextOverlayMenuForm(PlayerForm owner, ToolStripItemCollection rootItems, Point screenPoint, bool ambientGlass = false, Bitmap? ambient = null)
            {
                _owner = owner;
                _ambientGlass = ambientGlass;
                // La scala e' quella dello schermo (100%, 175%...). L'altezza della finestra aggiunge al
                // massimo un quarto: prima contava da sola, e un 4K al 100% dava un menu grande piu' del doppio.
                try
                {
                    float dpi = Math.Max(1f, owner.DeviceDpi / 96f);
                    _menuScale = dpi * Math.Clamp(owner.ClientSize.Height / (1080f * dpi), 1f, 1.25f);
                }
                catch { _menuScale = 1f; }
                _menuFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f * (96f / 72f) * _menuScale, FontStyle.Regular, GraphicsUnit.Pixel);

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                // Essendo una finestra owned resta gia' sopra al player. Renderla
                // top-most anche quando Cinecore e' in finestra alterava l'ordine
                // globale delle applicazioni e, alla chiusura, il player finiva sotto.
                TopMost = owner.TopMost;
                KeyPreview = true;
                DoubleBuffered = true;
                BackColor = PanelBack;
                ForeColor = TextNormal;

                Rectangle workingArea = Screen.FromPoint(screenPoint).WorkingArea;
                try
                {
                    Rectangle ownerClient = owner.RectangleToScreen(owner.ClientRectangle);
                    Rectangle boundedOwner = Rectangle.Intersect(ownerClient, workingArea);
                    _screenBounds = boundedOwner.Width >= MinPanelWidth && boundedOwner.Height >= 180
                        ? boundedOwner
                        : workingArea;
                }
                catch
                {
                    _screenBounds = workingArea;
                }

                if (_ambientGlass)
                {
                    using (ambient) SetAmbientBackdrop(ambient);
                }
                else
                {
                    try
                    {
                        _glassBackdrop = new Bitmap(_screenBounds.Width, _screenBounds.Height);
                        using var capture = Graphics.FromImage(_glassBackdrop);
                        capture.CopyFromScreen(_screenBounds.Location, Point.Empty, _screenBounds.Size);
                    }
                    catch { _glassBackdrop?.Dispose(); _glassBackdrop = null; }
                }

                var rootPanel = BuildPanel(rootItems, screenPoint, 0, null, null);
                _panels.Add(rootPanel);
                RepositionWindow();
                _hotItem = HitTest(Control.MousePosition)?.Row.Item;

                _submenuTimer = new System.Windows.Forms.Timer { Interval = 240 };
                _submenuTimer.Tick += (_, __) =>
                {
                    _submenuTimer.Stop();
                    var pending = _pendingHover;
                    _pendingHover = null;
                    var hit = HitTest(Control.MousePosition);
                    if (pending == null || hit == null || !ReferenceEquals(hit.Row.Item, pending.Row.Item)) return;
                    if (hit.Row.Item is ToolStripMenuItem mi && mi.Enabled && HasRenderableSubItems(mi))
                        OpenSubMenu(hit.Panel, hit.Row, mi);
                    else
                        TrimPanelsAfter(hit.Panel.Depth);
                };
                _outsideClickTimer = new System.Windows.Forms.Timer { Interval = 45 };
                _outsideClickTimer.Tick += (_, __) => CloseIfClickLandedOutside();
                _outsideClickTimer.Start();
            }

            // Sfondo del vetro da un'immagine del film, stesa su tutta l'area del menu (verra' sfocata).
            // Senza immagine, un fondo scuro neutro: mai il desktop che sta sotto al video.
            private void SetAmbientBackdrop(Bitmap? ambient)
            {
                try
                {
                    var backdrop = new Bitmap(Math.Max(1, _screenBounds.Width), Math.Max(1, _screenBounds.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(backdrop))
                    {
                        g.Clear(Color.FromArgb(14, 16, 20));
                        if (ambient != null)
                        {
                            g.InterpolationMode = InterpolationMode.Bilinear;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(ambient, new Rectangle(Point.Empty, backdrop.Size));
                        }
                    }
                    _glassBackdrop?.Dispose();
                    _glassBackdrop = backdrop;
                    foreach (var panel in _glassPanels.Values) panel.Dispose();
                    _glassPanels.Clear();
                }
                catch { }
            }

            /// <summary>E' arrivato il fotogramma del punto in riproduzione: il vetro si rifa' su quello.</summary>
            public void UpdateAmbient(Bitmap frame)
            {
                if (!_ambientGlass || IsDisposed) return;
                SetAmbientBackdrop(frame);
                Invalidate();
            }

            // Breve dissolvenza all'apertura, come le altre schede. Solo sopra la libreria: sopra
            // un renderer video nativo una finestra a strati puo' far lampeggiare l'immagine.
            private System.Windows.Forms.Timer? _fadeTimer;
            public void PrepareFadeIn()
            {
                if (!SystemInformation.IsMenuAnimationEnabled) return;
                try { Opacity = 0; } catch { return; }
                long start = 0;
                _fadeTimer = new System.Windows.Forms.Timer { Interval = AnimationClock.FrameIntervalMs };
                _fadeTimer.Tick += (_, _) =>
                {
                    double t = Math.Clamp((AnimationClock.NowMs - start) / 110.0, 0, 1);
                    try { if (!IsDisposed) Opacity = 1 - Math.Pow(1 - t, 3); } catch { }
                    if (t >= 1 || IsDisposed) _fadeTimer?.Stop();
                };
                Shown += (_, _) => { start = AnimationClock.NowMs; _fadeTimer?.Start(); };
            }

            protected override bool ShowWithoutActivation => false;

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _glassBackdrop?.Dispose();
                    foreach (var panel in _glassPanels.Values) panel.Dispose();
                    _glassPanels.Clear();
                    try { Capture = false; } catch { }
                    try { _outsideClickTimer.Stop(); } catch { }
                    try { _outsideClickTimer.Dispose(); } catch { }
                    try { _submenuTimer.Stop(); _submenuTimer.Dispose(); } catch { }
                    try { _fadeTimer?.Stop(); _fadeTimer?.Dispose(); } catch { }
                    try { _menuFont.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }

            public void PromoteToTop()
            {
                try
                {
                    TopMost = _owner.TopMost;
                    if (TopMost)
                    {
                        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height,
                            Win32.SWP_SHOWWINDOW);
                    }
                    else
                    {
                        BringToFront();
                    }
                    Activate();
                    Focus();
                }
                catch { }
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                try { Capture = false; } catch { }
                base.OnFormClosed(e);
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_MOUSEACTIVATE = 0x0021;
                const int MA_ACTIVATE = 1;
                const int MA_ACTIVATEANDEAT = 2;
                const int WM_LBUTTONDOWN = 0x0201;
                if (m.Msg == WM_MOUSEACTIVATE)
                {
                    int mouseMessage = unchecked((int)(((long)m.LParam >> 16) & 0xFFFF));
                    if (mouseMessage == WM_LBUTTONDOWN)
                    {
                        // Alcuni renderer/host Win32 non consegnano il primo
                        // WM_LBUTTONDOWN dopo l'attivazione. Risolvi la voce già nel
                        // WM_MOUSEACTIVATE e consuma quel click: l'azione parte una
                        // volta sola e non richiede più il secondo click.
                        HitInfo? hit = HitTest(Control.MousePosition);
                        if (hit?.Row.Item is ToolStripMenuItem leaf && leaf.Enabled && !HasRenderableSubItems(leaf))
                        {
                            try { BeginInvoke(new Action(() => _owner.PerformCustomContextMenuClick(leaf))); } catch { }
                            m.Result = (IntPtr)MA_ACTIVATEANDEAT;
                            return;
                        }
                    }

                    m.Result = (IntPtr)MA_ACTIVATE;
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                try { if (!Focused) Focus(); } catch { }
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                // Non chiudere sul solo cambio di focus: renderer nativi e overlay
                // possono riattivare per un istante il proprietario proprio mentre si
                // clicca una voce. Il timer esterno, Esc e il clic sulla voce gestiscono
                // già tutte le chiusure intenzionali in modo affidabile.
            }

            private void CloseIfClickLandedOutside()
            {
                try
                {
                    if ((DateTime.UtcNow - _createdUtc).TotalMilliseconds < 220)
                        return;

                    var buttons = Control.MouseButtons;
                    if ((buttons & (MouseButtons.Left | MouseButtons.Right | MouseButtons.Middle)) == 0)
                        return;

                    Point mouse = Control.MousePosition;
                    if (_panels.Any(panel => panel.BoundsScreen.Contains(mouse)))
                        return;

                    _owner._suppressContextMenuMouseUpUntilUtc = DateTime.UtcNow.AddMilliseconds(420);
                    Close();
                }
                catch { }
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                _submenuTimer.Stop();
                _pendingHover = null;
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    try { Close(); } catch { }
                    return;
                }

                MenuPanelState panel = _panels.LastOrDefault(p => p.Rows.Any(row => ReferenceEquals(row.Item, _hotItem)))
                    ?? _panels.Last();
                var selectable = panel.Rows
                    .Where(row => row.Item is ToolStripMenuItem menu && menu.Enabled)
                    .ToList();

                if (selectable.Count > 0 && e.KeyCode is Keys.Up or Keys.Down)
                {
                    int current = selectable.FindIndex(row => ReferenceEquals(row.Item, _hotItem));
                    int delta = e.KeyCode == Keys.Down ? 1 : -1;
                    int next = current < 0 ? (delta > 0 ? 0 : selectable.Count - 1) : (current + delta + selectable.Count) % selectable.Count;
                    _hotItem = selectable[next].Item;
                    e.Handled = true;
                    Invalidate();
                    return;
                }

                var hotRow = panel.Rows.FirstOrDefault(row => ReferenceEquals(row.Item, _hotItem));
                if (e.KeyCode == Keys.Left && panel.Depth > 0)
                {
                    TrimPanelsAfter(panel.Depth - 1);
                    _hotItem = panel.SourceItem;
                    e.Handled = true;
                    return;
                }

                if (hotRow?.Item is ToolStripMenuItem hotMenu && hotMenu.Enabled && e.KeyCode is Keys.Right or Keys.Enter)
                {
                    if (HasRenderableSubItems(hotMenu))
                    {
                        OpenSubMenu(panel, hotRow, hotMenu);
                        MenuPanelState child = _panels.Last();
                        _hotItem = child.Rows.FirstOrDefault(row => row.Item is ToolStripMenuItem childMenu && childMenu.Enabled)?.Item;
                        Invalidate();
                    }
                    else if (e.KeyCode == Keys.Enter)
                    {
                        _owner.PerformCustomContextMenuClick(hotMenu);
                    }

                    e.Handled = true;
                    return;
                }

                base.OnKeyDown(e);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var screenPoint = PointToScreen(e.Location);
                var hit = HitTest(screenPoint);
                if (hit == null)
                {
                    if (_hotItem != null)
                    {
                        _hotItem = null;
                        Invalidate();
                    }
                    return;
                }

                if (!ReferenceEquals(_hotItem, hit.Row.Item))
                {
                    _hotItem = hit.Row.Item;
                    Invalidate();
                }

                if (!ReferenceEquals(_pendingHover?.Row.Item, hit.Row.Item))
                {
                    _submenuTimer.Stop();
                    _pendingHover = hit;
                    _submenuTimer.Start();
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                _submenuTimer.Stop();
                _pendingHover = null;

                if (e.Button != MouseButtons.Left)
                {
                    try { Close(); } catch { }
                    return;
                }

                var screenPoint = PointToScreen(e.Location);
                var hit = HitTest(screenPoint);
                if (hit == null)
                {
                    try { Close(); } catch { }
                    return;
                }

                if (hit.Row.Item is not ToolStripMenuItem mi || !mi.Enabled)
                    return;

                if (HasRenderableSubItems(mi))
                {
                    _pressedItem = mi;
                    Invalidate();
                    OpenSubMenu(hit.Panel, hit.Row, mi);
                    return;
                }

                // Esegui le voci foglia direttamente sul primo mouse-down. Alcuni
                // renderer video cambiano il focus nativo prima del mouse-up e il
                // vecchio percorso finiva per richiedere un secondo click.
                _pressedItem = null;
                _owner.PerformCustomContextMenuClick(mi);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                ToolStripItem? pressed = _pressedItem;
                _pressedItem = null;
                Invalidate();

                if (e.Button != MouseButtons.Left || pressed is not ToolStripMenuItem menuItem || !menuItem.Enabled)
                    return;

                var hit = HitTest(PointToScreen(e.Location));
                if (hit == null || !ReferenceEquals(hit.Row.Item, pressed) || HasRenderableSubItems(menuItem))
                    return;

                _owner.PerformCustomContextMenuClick(menuItem);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.Clear(PanelBack);

                foreach (var panel in _panels)
                    DrawPanel(e.Graphics, panel);
            }

            private MenuPanelState BuildPanel(ToolStripItemCollection items, Point requestedScreenPoint, int depth, ToolStripMenuItem? sourceItem, Rectangle? parentRowScreen)
            {
                var renderItems = GetRenderableItems(items);
                int width = MeasurePanelWidth(renderItems);
                // Il menu appartiene a Cinecore: non deve sconfinare sopra altre
                // applicazioni quando la finestra non occupa tutto il monitor.
                Rectangle work = _screenBounds;
                int normalRows = renderItems.Count(item => item is not ToolStripSeparator);
                int separatorRows = renderItems.Count - normalRows;
                int rowHeight = RowHeight;
                if (normalRows > 0)
                {
                    int available = work.Height - 8 - PanelPadding * 2 - separatorRows * SeparatorHeight;
                    rowHeight = Math.Clamp(available / normalRows, 28, RowHeight);
                }
                int height = PanelPadding * 2 + separatorRows * SeparatorHeight + normalRows * rowHeight;
                height = Math.Max(PanelPadding * 2 + rowHeight, height);

                int x = requestedScreenPoint.X;
                int y = requestedScreenPoint.Y;

                if (parentRowScreen.HasValue)
                {
                    Rectangle parent = parentRowScreen.Value;
                    x = parent.Right + PanelGap;
                    if (x + width > work.Right)
                        x = parent.Left - width - PanelGap;
                    y = parent.Top - PanelPadding;
                }

                x = Math.Clamp(x, work.Left + 2, Math.Max(work.Left + 2, work.Right - width - 2));
                y = Math.Clamp(y, work.Top + 2, Math.Max(work.Top + 2, work.Bottom - height - 2));

                var panel = new MenuPanelState
                {
                    Depth = depth,
                    SourceItem = sourceItem,
                    BoundsScreen = new Rectangle(x, y, width, height)
                };

                int rowY = y + PanelPadding;
                foreach (var item in renderItems)
                {
                    int rowH = item is ToolStripSeparator ? SeparatorHeight : rowHeight;
                    panel.Rows.Add(new MenuRowState(item, new Rectangle(x + PanelPadding, rowY, width - PanelPadding * 2, rowH)));
                    rowY += rowH;
                }

                return panel;
            }

            private int MeasurePanelWidth(List<ToolStripItem> items)
            {
                int width = MinPanelWidth;
                using var g = CreateGraphics();
                foreach (var item in items)
                {
                    if (item is ToolStripSeparator)
                        continue;

                    string text = CleanMenuText(item.Text);
                    int textWidth = TextRenderer.MeasureText(g, text, _menuFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    int candidate = PanelPadding * 2 + IconWidth + ArrowWidth + Scaled(12) + textWidth;
                    width = Math.Max(width, candidate);
                }

                return Math.Min(MaxPanelWidth, width);
            }

            private static List<ToolStripItem> GetRenderableItems(ToolStripItemCollection items)
            {
                var result = new List<ToolStripItem>();
                bool lastWasSeparator = true;

                foreach (ToolStripItem item in items)
                {
                    // Visible dipende anche dalla visibilità del ContextMenuStrip padre.
                    // Nel renderer custom il ToolStrip nativo è volutamente chiuso, quindi
                    // Visible sarebbe false per ogni voce e produrrebbe un menu vuoto.
                    if (!item.Available)
                        continue;

                    if (item is ToolStripSeparator)
                    {
                        if (!lastWasSeparator)
                        {
                            result.Add(item);
                            lastWasSeparator = true;
                        }
                        continue;
                    }

                    result.Add(item);
                    lastWasSeparator = false;
                }

                while (result.Count > 0 && result[^1] is ToolStripSeparator)
                    result.RemoveAt(result.Count - 1);

                return result;
            }

            private bool HasRenderableSubItems(ToolStripMenuItem item)
            {
                // Paint/hit-testing must never query COM or rebuild a ToolStrip.
                // Dynamic menus already carry placeholders until they are opened.
                return item.DropDownItems.Cast<ToolStripItem>().Any(child => child.Available);
            }

            private void PrepareSubMenu(ToolStripMenuItem item)
            {
                if (_preparedSubMenus.Contains(item))
                    return;

                _preparedSubMenus.Add(item);
                _owner.PrepareCustomContextSubMenu(item);
            }

            private void OpenSubMenu(MenuPanelState parentPanel, MenuRowState parentRow, ToolStripMenuItem item)
            {
                PrepareSubMenu(item);
                var renderItems = GetRenderableItems(item.DropDownItems);
                if (renderItems.Count == 0)
                {
                    TrimPanelsAfter(parentPanel.Depth);
                    return;
                }

                int targetDepth = parentPanel.Depth + 1;
                var existing = _panels.FirstOrDefault(p => p.Depth == targetDepth && ReferenceEquals(p.SourceItem, item));
                if (existing != null) return;

                TrimPanelsAfter(parentPanel.Depth);
                var child = BuildPanel(item.DropDownItems, new Point(parentPanel.BoundsScreen.Right + PanelGap, parentRow.BoundsScreen.Top - PanelPadding), targetDepth, item, new Rectangle(parentPanel.BoundsScreen.Left, parentRow.BoundsScreen.Top, parentPanel.BoundsScreen.Width, parentRow.BoundsScreen.Height));
                _panels.Add(child);
                RepositionWindow();
                Invalidate();
            }

            private void TrimPanelsAfter(int depth)
            {
                int removed = _panels.RemoveAll(panel => panel.Depth > depth);
                if (removed > 0)
                {
                    RepositionWindow();
                    Invalidate();
                }
            }

            private HitInfo? HitTest(Point screenPoint)
            {
                for (int i = _panels.Count - 1; i >= 0; i--)
                {
                    var panel = _panels[i];
                    if (!panel.BoundsScreen.Contains(screenPoint))
                        continue;

                    foreach (var row in panel.Rows)
                    {
                        if (row.BoundsScreen.Contains(screenPoint))
                            return new HitInfo(panel, row);
                    }
                }

                return null;
            }

            private void RepositionWindow()
            {
                if (_panels.Count == 0)
                    return;

                // Keep a stable native window while submenus open. Resizing/moving the
                // top-level menu over a renderer caused the surface below to flash.
                // The Region still limits painting and hit-testing to visible panels.
                if (Bounds != _screenBounds)
                    Bounds = _screenBounds;
                ApplyPanelRegion();
            }

            private void ApplyPanelRegion()
            {
                try
                {
                    using var region = new Region();
                    region.MakeEmpty();

                    // La finestra segue la curva del pannello (un pixel piu' larga del disegno, che
                    // resta con antialias): fuori dalla curva si vede davvero cio' che c'e' sotto.
                    // Con i rettangoli pieni gli angoli mostravano la cattura dello schermo fatta
                    // all'apertura: appena sotto cambiava qualcosa (video, pagina che scorre)
                    // restavano quattro spigoli fermi attorno agli angoli arrotondati.
                    foreach (var panel in _panels)
                    {
                        // Con la cattura dello schermo negli angoli la sagoma sta appena FUORI dalla curva disegnata
                        // (raggio piu' piccolo): prima la tagliava dentro e troncava i pixel sfumati, angoli seghettati.
                        using var shape = BuildDarkMenuRoundRect(ToLocal(panel.BoundsScreen), _glassBackdrop != null && !_ambientGlass ? Math.Max(1, PanelRadius - 2) : PanelRadius + 1);
                        region.Union(shape);
                    }

                    var old = Region;
                    Region = region.Clone();
                    old?.Dispose();
                }
                catch { }
            }

            private void DrawPanel(Graphics g, MenuPanelState panel)
            {
                Rectangle panelLocal = ToLocal(panel.BoundsScreen);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_glassBackdrop != null && !_ambientGlass)
                {
                    // Angoli: cio' che c'e' sullo schermo dietro al menu.
                    var screenCrop = panel.BoundsScreen; screenCrop.Offset(-_screenBounds.X, -_screenBounds.Y);
                    // Copia esatta, pixel per pixel: l'interpolazione predefinita sfumava i bordi.
                    var copyState = g.Save();
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(_glassBackdrop, panelLocal, screenCrop, GraphicsUnit.Pixel);
                    g.Restore(copyState);
                }
                using (var path = BuildDarkMenuRoundRect(new Rectangle(panelLocal.X, panelLocal.Y, panelLocal.Width - 1, panelLocal.Height - 1), PanelRadius))
                using (var back = new SolidBrush(PanelBack))
                using (var border = new Pen(PanelBorder, 1f))
                {
                    g.FillPath(back, path);
                    if (_glassBackdrop != null)
                    {
                        if (!_glassPanels.TryGetValue(panel.BoundsScreen, out var glass))
                        {
                            var crop = panel.BoundsScreen; crop.Offset(-_screenBounds.X, -_screenBounds.Y);
                            glass = GlassSurface.CreateLiquid(_glassBackdrop, crop, hairline: false);
                            _glassPanels[panel.BoundsScreen] = glass;
                        }
                        // Nessun velo in piu': la velatura e la compressione delle luci sono gia' nel
                        // materiale, lo stesso della barra musicale e della coda.
                        using (var glassBrush = new TextureBrush(glass, WrapMode.Clamp))
                        {
                            glassBrush.TranslateTransform(panelLocal.X, panelLocal.Y);
                            if (_ambientGlass)
                            {
                                // Tutto il rettangolo e' vetro: la curva la da' la finestra, e negli
                                // angoli non resta nessun colore estraneo.
                                g.FillRectangle(glassBrush, panelLocal);
                            }
                            else
                            {
                                g.FillPath(glassBrush, path);
                            }
                        }
                    }
                    g.DrawPath(border, path);
                }

                foreach (var row in panel.Rows)
                    DrawRow(g, panel, row);
            }

            private void DrawRow(Graphics g, MenuPanelState panel, MenuRowState row)
            {
                Rectangle local = ToLocal(row.BoundsScreen);
                if (row.Item is ToolStripSeparator)
                {
                    int y = local.Top + local.Height / 2;
                    using var pen = new Pen(SeparatorColor, 1f);
                    g.DrawLine(pen, local.Left + 8, y, local.Right - 8, y);
                    return;
                }

                bool hot = ReferenceEquals(_hotItem, row.Item) || _panels.Any(child => ReferenceEquals(child.SourceItem, row.Item));
                bool pressed = ReferenceEquals(_pressedItem, row.Item);
                bool enabled = row.Item.Enabled;
                if ((hot || pressed) && enabled)
                {
                    using var brush = new SolidBrush(pressed ? RowPressed : RowHot);
                    using var path = BuildDarkMenuRoundRect(new Rectangle(local.X + 2, local.Y + 1, local.Width - 4, local.Height - 2), 6);
                    g.FillPath(brush, path);
                }

                Color iconColor = enabled
                    ? (hot || pressed ? Theme.Accent : Theme.SubtleText)
                    : Color.FromArgb(78, 91, 105);
                Rectangle iconRect = new Rectangle(local.Left + Scaled(9), local.Top + (local.Height - Scaled(18)) / 2, Scaled(18), Scaled(18));
                // Voce attiva: il segno di spunta prende il posto dell'icona e il testo passa al
                // colore d'accento. Prima la spunta era piccola e a destra, in mezzo a righe con
                // la stessa icona: negli elenchi (es. uscite audio) non si capiva quale fosse attiva.
                bool isChecked = row.Item is ToolStripMenuItem checkable && checkable.Checked;
                if (isChecked)
                    DarkMenuRenderer.DrawMenuCheck(g, Rectangle.Inflate(iconRect, -1, -1), enabled ? Theme.Accent : TextDisabled);
                else
                {
                    string iconKey = DarkMenuRenderer.MenuIconKey(row.Item);
                    // Use the same uniform SVG set as the rest of the application.
                    // The legacy hand-drawn fallback assumed a 32px canvas and looked
                    // distorted when squeezed into these menu rows.
                    if (!AssetIconService.DrawCustom(g, iconRect, iconKey, iconColor))
                        AssetIconService.DrawCustom(g, iconRect, "dot", iconColor);
                }

                Rectangle textRect = new Rectangle(local.Left + IconWidth + Scaled(5), local.Top, Math.Max(20, local.Width - IconWidth - ArrowWidth - Scaled(12)), local.Height);
                TextRenderer.DrawText(g, CleanMenuText(row.Item.Text), _menuFont, textRect, !enabled ? TextDisabled : isChecked ? Theme.Accent : TextNormal,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                if (row.Item is ToolStripMenuItem menuItem && menuItem.Enabled && HasRenderableSubItems(menuItem))
                {
                    Rectangle arrowRect = new Rectangle(local.Right - ArrowWidth - Scaled(2), local.Top + (local.Height - Scaled(18)) / 2, ArrowWidth, Scaled(18));
                    DarkMenuRenderer.DrawMenuArrow(g, arrowRect, enabled ? TextNormal : TextDisabled);
                }
            }

            private Rectangle ToLocal(Rectangle screenRect)
                => new Rectangle(screenRect.X - Left, screenRect.Y - Top, screenRect.Width, screenRect.Height);

            private static string CleanMenuText(string? text)
            {
                if (string.IsNullOrEmpty(text))
                    return string.Empty;

                return text.Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&");
            }

            private sealed class MenuPanelState
            {
                public int Depth;
                public ToolStripMenuItem? SourceItem;
                public Rectangle BoundsScreen;
                public readonly List<MenuRowState> Rows = new();
            }

            private sealed class MenuRowState
            {
                public MenuRowState(ToolStripItem item, Rectangle boundsScreen)
                {
                    Item = item;
                    BoundsScreen = boundsScreen;
                }

                public ToolStripItem Item { get; }
                public Rectangle BoundsScreen { get; }
            }

            private sealed record HitInfo(MenuPanelState Panel, MenuRowState Row);
        }

        private sealed class TopMostContextMenuStrip : ContextMenuStrip
        {
            public bool RaiseOpeningForCustomDisplay()
            {
                var e = new CancelEventArgs();
                OnOpening(e);
                return !e.Cancel;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOPMOST = 0x00000008;
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOPMOST;
                    return cp;
                }
            }

            protected override void OnOpened(EventArgs e)
            {
                base.OnOpened(e);
                try { PromoteToolStripWindow(this); } catch { }
            }
        }

        // Overlay nero + spinner per il caricamento del media (al posto della schermata iniziale)

    }
}
