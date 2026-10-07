#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private bool ShouldUseMpcvrPopupOverlay()
        {
            // MPCVR needs a native top-level overlay. The dedicated OverlayHostForm is a
            // layered owned popup; ToolStripDropDown proved unreliable over the VRWindow.
            return false;
        }

        private bool ShouldUseMpcvrRegionOverlayHost() => false;

        private bool HasInteractivePlaybackOverlay()
        {
            try
            {
                return (_hud?.RequestedVisible == true)
                    || IsInfoPanelShown()
                    || (_pausePlaceholder?.Visible == true)
                    || (_engine is not ImagePlaybackEngine && _photoHud?.Visible == true);
            }
            catch { return false; }
        }

        private Rectangle GetMpcvrOverlayScreenRect()
        {
            try
            {
                if (_videoHost != null && !_videoHost.IsDisposed && _videoHost.IsHandleCreated)
                {
                    var rc = _videoHost.ClientRectangle;
                    if (rc.Width > 0 && rc.Height > 0)
                        return _videoHost.RectangleToScreen(rc);
                }
            }
            catch { }

            return RectangleToScreen(ClientRectangle);
        }

        private Rectangle GetOverlayHostScreenRect()
        {
            if (IsMpcvrActive && _engine != null && _currentMediaHasVideo)
                return GetMpcvrOverlayScreenRect();

            return RectangleToScreen(ClientRectangle);
        }

        private bool ShouldForceMpcvrOverlayTopMost()
        {
            return !_useInlineOverlay
                && IsMpcvrActive
                && _engine != null
                && _currentMediaHasVideo;
        }

        private bool IsFullscreenOverlayMode()
        {
            return FormBorderStyle == FormBorderStyle.None || _mpcvrExclusiveMode;
        }

        private bool IsCinecoreForeground()
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                    return ContainsFocus;
                GetWindowThreadProcessId(foreground, out uint processId);
                return processId == (uint)Environment.ProcessId;
            }
            catch { return ContainsFocus; }
        }

        private void SuppressPlaybackWindowsWhenInactive()
        {
            _hudInputSurface?.Hide();
            try
            {
                if (FormBorderStyle != FormBorderStyle.None && TopMost)
                    TopMost = false;
                if (_overlayHost != null && !_overlayHost.IsDisposed)
                {
                    if (_overlayHost.TopMost)
                        _overlayHost.TopMost = false;
                    if (_overlayHost.Visible) _overlayHost.Hide();
                }
                try { _mpcvrOverlayPopup?.Hide(); } catch { }
                try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }
                try { _mpcvrChildOverlayHost?.Hide(); } catch { }
                try { _mpcvrOverlayStagingHost?.Hide(); } catch { }
                EnsureCursorVisible();
            }
            catch { }
        }

        private void RestorePlaybackWindowsWhenActive()
        {
            try
            {
                if (_closingForExit || _nativeChromeInteraction || !IsCinecoreForeground() || WindowState == FormWindowState.Minimized)
                    return;
                BringOverlaysToFront();
            }
            catch { }
        }

        private void ApplyOverlayTopMostPolicy()
        {
            try
            {
                if (_overlayHost == null || _overlayHost.IsDisposed)
                    return;

                bool fullscreenOrTopMost = IsCinecoreForeground() && (FormBorderStyle == FormBorderStyle.None || TopMost);
                bool desired = fullscreenOrTopMost && (ShouldForceMpcvrOverlayTopMost() || TopMost);
                if (_overlayHost.TopMost != desired)
                    _overlayHost.TopMost = desired;
            }
            catch { }
        }

        private void ShowOverlayHostOwned()
        {
            if (_overlayHost == null || _overlayHost.IsDisposed)
                return;

            // Questa e' l'ultima barriera prima di mostrare una finestra top-level.
            // Callback/timer del renderer possono arrivare dopo lo stop: in quel caso
            // una host trasparente sopra la libreria intercetterebbe tutti i click.
            if (ShouldSuppressExternalPlaybackOverlays())
            {
                HideExternalPlaybackOverlayHosts();
                return;
            }

            if (!IsCinecoreForeground())
            {
                try { _overlayHost.TopMost = false; if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                return;
            }

            ApplyOverlayTopMostPolicy();
            try { _overlayHost.Show(this); }
            catch { _overlayHost.Show(); }
        }

        private Control GetExternalOverlaySurface()
        {
            if (ShouldUseMpcvrDedicatedOverlay())
                return EnsureMpcvrOverlayStagingSurface();

            if (ShouldUseMpcvrPopupOverlay())
                return _mpcvrOverlayPopup.Surface;

            return _overlayHost.Surface;
        }

        private bool ShouldUseMpcvrDedicatedOverlay()
        {
            return false;
        }

        private Control EnsureMpcvrOverlayStagingSurface()
        {
            _mpcvrOverlayStagingHost ??= new MpcvrOverlayStagingForm();
            try { if (_mpcvrOverlayStagingHost.Owner != this) _mpcvrOverlayStagingHost.Owner = this; } catch { }
            _mpcvrOverlayStagingHost.EnsureReady(this, _videoHost?.ClientSize ?? ClientSize);
            try { _mpcvrOverlayStagingHost.Surface.ContextMenuStrip = _menu; } catch { }
            try { AttachPlaybackContextMenuFallback(_mpcvrOverlayStagingHost.Surface); } catch { }
            return _mpcvrOverlayStagingHost.Surface;
        }

        private bool SyncMpcvrDedicatedOverlay()
        {
            if (!ShouldUseMpcvrDedicatedOverlay() || IsPlaybackOverlayBlockedByLoading())
            {
                HideMpcvrDedicatedOverlay();
                return false;
            }

            var surface = EnsureMpcvrOverlayStagingSurface();
            MoveOverlayControlsTo(surface);

            try { _overlayHost.SetRegionOverlayMode(false); } catch { }
            try { _overlayHost.SetVisibleRegion(null); } catch { }
            try { _overlayHost.SetClickThrough(true); } catch { }
            try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
            try { _mpcvrOverlayPopup.SetClickThrough(true); } catch { }
            try { _mpcvrOverlayPopup.ClearVisibleRegion(); } catch { }
            try { _mpcvrOverlayPopup.Hide(); } catch { }
            HideMpcvrChildOverlayHost();

            bool hasContent = HasVisibleOverlayHostContent();
            if (hasContent)
            {
                try { LayoutIntroOutroPromptPanel(); } catch { }
                try { LayoutInfoOverlay(); } catch { }
                try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
            }
            else
            {
                try { ClearMpcvrMixerOverlayMirror(); } catch { }
            }

            return true;
        }

        private void HideMpcvrDedicatedOverlay()
        {
            try
            {
                if (_engine is DirectShowUnifiedEngine ds)
                    ds.ClearMpcvrOverlayBitmap();
            }
            catch { }

            try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }
            try { _mpcvrOverlayStagingHost?.HideStaging(); } catch { }
            _lastMpcvrMixerOverlayUpdateUtc = DateTime.MinValue;
            _mpcvrMixerOverlayRetryUtc = DateTime.MinValue;
            _mpcvrDedicatedLayeredClearedRenderer = false;
        }

        private bool ShouldUseMpcvrChildOverlayHost()
        {
            return false
                && !_useInlineOverlay
                && IsMpcvrActive
                && _engine != null
                && _currentMediaHasVideo
                && _videoHost != null
                && !_videoHost.IsDisposed;
        }

        private InlineOverlayPanel EnsureMpcvrChildOverlayHost()
        {
            if (_mpcvrChildOverlayHost == null || _mpcvrChildOverlayHost.IsDisposed)
            {
                _mpcvrChildOverlayHost = new InlineOverlayPanel
                {
                    Dock = DockStyle.Fill,
                    Visible = false,
                    BackColor = Color.Transparent,
                    ClickThrough = true
                };
                _mpcvrChildOverlayHost.Resize += (_, __) =>
                {
                    try { LayoutIntroOutroPromptPanel(); } catch { }
                    try { LayoutInfoOverlay(); } catch { }
                    try { RaiseMpcvrChildOverlayHost(); } catch { }
                };
                try { _mpcvrChildOverlayHost.ContextMenuStrip = _menu; } catch { }
                try { AttachPlaybackContextMenuFallback(_mpcvrChildOverlayHost); } catch { }
            }

            if (_mpcvrChildOverlayHost.Parent != _videoHost)
                _videoHost.Controls.Add(_mpcvrChildOverlayHost);

            try { _mpcvrChildOverlayHost.Bounds = _videoHost.ClientRectangle; } catch { }
            try { if (!_mpcvrChildOverlayHost.IsHandleCreated) _mpcvrChildOverlayHost.CreateControl(); } catch { }

            return _mpcvrChildOverlayHost;
        }

        private bool IsMpcvrChildOverlayWindow(IntPtr hwnd)
        {
            try
            {
                var overlay = _mpcvrChildOverlayHost;
                if (overlay == null || overlay.IsDisposed || !overlay.IsHandleCreated)
                    return false;

                IntPtr overlayHwnd = overlay.Handle;
                return hwnd == overlayHwnd || IsChild(overlayHwnd, hwnd);
            }
            catch { return false; }
        }

        private void RaiseMpcvrChildOverlayHost()
        {
            var overlay = _mpcvrChildOverlayHost;
            if (overlay == null || overlay.IsDisposed || !overlay.IsHandleCreated || !overlay.Visible)
                return;

            try { overlay.BringToFront(); } catch { }
            try
            {
                const uint SWP_NOACTIVATE = 0x0010;
                Win32.SetWindowPos(overlay.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_FRAMECHANGED | SWP_NOACTIVATE);
            }
            catch { }
        }

        private void HideMpcvrChildOverlayHost()
        {
            try
            {
                if (_mpcvrChildOverlayHost == null || _mpcvrChildOverlayHost.IsDisposed)
                {
                    try { _mpcvrChildOverlayRegion?.Dispose(); } catch { }
                    _mpcvrChildOverlayRegion = null;
                    return;
                }

                _mpcvrChildOverlayHost.ClickThrough = true;
                _mpcvrChildOverlayHost.Visible = false;
                _mpcvrChildOverlayHost.Region = null;
                try { _mpcvrChildOverlayRegion?.Dispose(); } catch { }
                _mpcvrChildOverlayRegion = null;
            }
            catch { }
        }

        private void UpdateMpcvrChildOverlayRegion(InlineOverlayPanel overlay)
        {
            try
            {
                using var path = new GraphicsPath();
                var clip = overlay.ClientRectangle;
                bool any = false;

                foreach (var raw in BuildMpcvrOverlayVisibleRegions(overlay))
                {
                    var rc = Rectangle.Intersect(clip, raw);
                    if (rc.Width <= 0 || rc.Height <= 0)
                        continue;

                    path.AddRectangle(rc);
                    any = true;
                }

                if (!any)
                    path.AddRectangle(new Rectangle(0, 0, 1, 1));

                var old = _mpcvrChildOverlayRegion;
                _mpcvrChildOverlayRegion = new Region(path);
                overlay.Region = _mpcvrChildOverlayRegion;
                try { old?.Dispose(); } catch { }
            }
            catch
            {
                try { overlay.Region = null; } catch { }
                try { _mpcvrChildOverlayRegion?.Dispose(); } catch { }
                _mpcvrChildOverlayRegion = null;
            }
        }

        private bool SyncMpcvrChildOverlayHost()
        {
            if (!ShouldUseMpcvrChildOverlayHost())
            {
                HideMpcvrChildOverlayHost();
                return false;
            }

            var overlay = EnsureMpcvrChildOverlayHost();
            MoveOverlayControlsTo(overlay);

            try { _overlayHost.SetRegionOverlayMode(false); } catch { }
            try { _overlayHost.SetVisibleRegion(null); } catch { }
            try { _overlayHost.SetClickThrough(true); } catch { }
            try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
            try { _mpcvrOverlayPopup.SetClickThrough(true); } catch { }
            try { _mpcvrOverlayPopup.ClearVisibleRegion(); } catch { }
            try { _mpcvrOverlayPopup.Hide(); } catch { }

            bool hasContent = HasVisibleOverlayHostContent();
            overlay.ClickThrough = true;

            if (hasContent)
            {
                try { overlay.Bounds = _videoHost.ClientRectangle; } catch { }
                try { LayoutIntroOutroPromptPanel(); } catch { }
                try { LayoutInfoOverlay(); } catch { }
                overlay.Visible = false;
                try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
            }
            else
            {
                overlay.Visible = false;
                try { overlay.Region = null; } catch { }
                try { _mpcvrChildOverlayRegion?.Dispose(); } catch { }
                _mpcvrChildOverlayRegion = null;
                try { ClearMpcvrMixerOverlayMirror(); } catch { }
            }

            if (_mpcvrChildOverlayLogCount < 8)
            {
                _mpcvrChildOverlayLogCount++;
                try
                {
                    string hwnd = overlay.IsHandleCreated ? $"0x{overlay.Handle.ToInt64():X}" : "none";
                    string parent = _videoHost.IsHandleCreated ? $"0x{_videoHost.Handle.ToInt64():X}" : "none";
                    Dbg.Log($"[VIDEO] MPCVR child overlay sync: visible={overlay.Visible}, content={hasContent}, hwnd={hwnd}, parent={parent}", Dbg.LogLevel.Info);
                }
                catch { }
            }

            return true;
        }

        private void MoveOverlayControlsTo(Control target)
        {
            try { if (_pausePlaceholder != null && _pausePlaceholder.Parent != target) _pausePlaceholder.Parent = target; } catch { }
            try { if (_hud != null && _hud.Parent != target) _hud.Parent = target; } catch { }
            try { if (_infoOverlay != null && _infoOverlay.Parent != target) _infoOverlay.Parent = target; } catch { }
            try { if (_remoteOsd != null && _remoteOsd.Parent != target) _remoteOsd.Parent = target; } catch { }
            try { if (_engine is not ImagePlaybackEngine && _photoHud != null && _photoHud.Parent != target) _photoHud.Parent = target; } catch { }
            try { LayoutIntroOutroPromptPanel(); } catch { }
            try { LayoutInfoOverlay(); } catch { }
        }

        private void UseOverlayInline(bool enable)
        {
            if (_overlayInlineHost == null)
            {
                _overlayInlineHost = new InlineOverlayPanel { Dock = DockStyle.Fill };
                _stack.Controls.Add(_overlayInlineHost);
                _overlayInlineHost.Visible = false;
                try { _overlayInlineHost.ContextMenuStrip = _menu; } catch { }
                try { AttachPlaybackContextMenuFallback(_overlayInlineHost); } catch { }
            }

            _useInlineOverlay = enable;
            Control target = enable ? _overlayInlineHost : GetExternalOverlaySurface();

            MoveOverlayControlsTo(target);
            bool useMpcvrDedicatedOverlay = !enable && ShouldUseMpcvrDedicatedOverlay();
            bool useMpcvrChildOverlay = !enable && ShouldUseMpcvrChildOverlayHost();
            try { _photoHud.BackColor = (_engine is ImagePlaybackEngine || enable || useMpcvrDedicatedOverlay || useMpcvrChildOverlay) ? Color.Transparent : ((ShouldUseMpcvrPopupOverlay() || ShouldUseMpcvrRegionOverlayHost()) ? Color.Black : _overlayHost.TransparencyKey); } catch { }

            try { _overlayInlineHost.ClickThrough = true; } catch { }

            if (enable)
            {
                HideMpcvrDedicatedOverlay();
                HideMpcvrChildOverlayHost();
                try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                try { _overlayHost.SetClickThrough(true); } catch { }
                try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                try { _mpcvrOverlayPopup.SetClickThrough(true); } catch { }
                try { _mpcvrOverlayPopup.Hide(); } catch { }
                _overlayInlineHost.Visible = HasVisibleOverlayHostContent();
                if (_overlayInlineHost.Visible)
                    _overlayInlineHost.BringToFront();
            }
            else
            {
                _overlayInlineHost.Visible = false;
                if (useMpcvrDedicatedOverlay)
                {
                    SyncMpcvrDedicatedOverlay();
                }
                else if (useMpcvrChildOverlay)
                {
                    SyncMpcvrChildOverlayHost();
                }
                else if (ShouldUseMpcvrPopupOverlay())
                {
                    HideMpcvrDedicatedOverlay();
                    HideMpcvrChildOverlayHost();
                    try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                    try { _overlayHost.SetClickThrough(true); } catch { }
                    try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                    try { _mpcvrOverlayPopup.SetClickThrough(false); } catch { }
                    SafeShowOverlayHost();
                    SyncOverlayToVideoRect();
                    try { _mpcvrOverlayPopup.BringToFront(); } catch { }
                }
                else if (ShouldUseMpcvrMixerOverlayMirror())
                {
                    HideMpcvrDedicatedOverlay();
                    HideMpcvrChildOverlayHost();
                    try { _mpcvrOverlayPopup.Hide(); } catch { }
                    try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                    try { _overlayHost.SetClickThrough(true); } catch { }
                    try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                    try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                }
                else
                {
                    HideMpcvrDedicatedOverlay();
                    HideMpcvrChildOverlayHost();
                    try { _mpcvrOverlayPopup.Hide(); } catch { }
                    try { UpdateMpcvrOverlayRegionMode(); } catch { }
                    if (!_overlayHost.Visible) SafeShowOverlayHost();
                    SyncOverlayToVideoRect();
                    try { _overlayHost.BringToFront(); } catch { }
                }
            }

            BringOverlaysToFront();
            UpdateVideoWindowForCurrentHost();
        }

        private void SetModalInputState(bool modalVisible)
        {
            try
            {
                if (_overlayInlineHost != null)
                    _overlayInlineHost.ClickThrough = !(modalVisible || HasVisibleOverlayHostContent());
            }
            catch { }

            try
            {
                if (_mpcvrChildOverlayHost != null && !_mpcvrChildOverlayHost.IsDisposed)
                {
                    bool hasContent = _mpcvrChildOverlayHost.Visible && HasVisibleOverlayHostContent();
                    _mpcvrChildOverlayHost.ClickThrough = !hasContent;
                    if (hasContent)
                        RaiseMpcvrChildOverlayHost();
                }
            }
            catch { }

            try
            {
                if (_overlayHost == null || _overlayHost.IsDisposed)
                    return;

                if (ShouldUseMpcvrPopupOverlay())
                {
                    _mpcvrOverlayPopup.SetInteractive(modalVisible);
                    bool popupHasContent = modalVisible || HasVisibleOverlayHostContent();
                    _mpcvrOverlayPopup.SetClickThrough(!popupHasContent);
                    if (!popupHasContent)
                        _mpcvrOverlayPopup.Hide();
                }

                if (modalVisible)
                {
                    _overlayHost.SetInteractive(true);
                    _overlayHost.SetClickThrough(false);
                }
                else
                {
                    bool interactive = HasInteractivePlaybackOverlay();
                    bool hasContent = HasVisibleOverlayHostContent();
                    _overlayHost.SetInteractive(interactive);
                    _overlayHost.SetClickThrough(!(interactive || hasContent));
                    if (!hasContent && _overlayHost.Visible)
                        _overlayHost.Hide();
                }
            }
            catch { }
        }

        private static void SetRedraw(Control? control, bool enabled)
        {
            const int WM_SETREDRAW = 0x000B;
            if (control == null || control.IsDisposed || !control.IsHandleCreated)
                return;

            try { SendMessage(control.Handle, WM_SETREDRAW, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        private bool _pendingShowOverlayOnHandleCreated;

        private bool HasVisibleOverlayHostContent()
        {
            try
            {
                return (_hud?.RequestedVisible == true)
                    || IsInfoPanelShown()
                    || (_remoteOsd?.RequestedVisible == true)
                    || (_pausePlaceholder?.Visible == true)
                    || (_engine is not ImagePlaybackEngine && _photoHud?.Visible == true);
            }
            catch { return true; }
        }

        private bool IsPlaybackOverlayBlockedByLoading()
        {
            try
            {
                return (_videoLoading?.Visible == true)
                    || IsAnyLibraryVisible()
                    || (_settingsHudPage?.Visible == true)
                    || (_netflixModePage?.Visible == true);
            }
            catch { return true; }
        }

        private bool ShouldKeepMpcvrOverlayHostHidden()
        {
            if (_useInlineOverlay || ShouldSuppressExternalPlaybackOverlays())
                return true;

            if (_engine != null && _currentMediaHasVideo)
                return IsPlaybackOverlayBlockedByLoading() || !HasVisibleOverlayHostContent();

            return false;
        }

        private bool ShouldSuppressExternalPlaybackOverlays()
        {
            try
            {
                if (_audioSyncDialogOpen) return true;
                if (_openingMusic || (HasMusicPlayback && _musicWorkspaceActive) || _videoLoading?.Visible == true) return true;
                if (_closingForExit || IsDisposed || Disposing || !Visible || WindowState == FormWindowState.Minimized)
                    return true;

                // Native provider pages live inside Settings. A playback owned window
                // shown later by a renderer callback can cover their controls and eat
                // clicks even though the page itself remains visible.
                if (_settingsHudPage?.Visible == true)
                    return true;

                // Le pagine di libreria/Spotlight sono superfici complete: non deve
                // esistere alcuna owned window del player sopra di loro.
                if (IsAnyLibraryVisible())
                    return true;

                // Il placeholder pre-film e' l'unico overlay valido prima che esista
                // un motore di riproduzione.
                return _engine == null && !_preOpenPlaceholderGateActive;
            }
            catch { return true; }
        }

        private bool HasVisibleStackOverlayAboveVideo()
        {
            try
            {
                return (_videoLoading?.Visible == true)
                    || (_netflixModePage?.Visible == true)
                    || (_audioOnlyBanner?.Visible == true)
                    || (_audioMetersHost?.Visible == true)
                    || (_settingsHudPage?.Visible == true)
                    || IsAnyLibraryVisible()
                    || (_pairBanner?.Visible == true);
            }
            catch { return true; }
        }

        private bool ShouldKeepMpcvrVideoHostInFront()
        {
            return false;
        }

        private void UpdateMpcvrOverlayRegionMode()
        {
            if (_overlayHost == null || _overlayHost.IsDisposed)
                return;

            if (ShouldUseMpcvrDedicatedOverlay())
            {
                MoveOverlayControlsTo(EnsureMpcvrOverlayStagingSurface());
                try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                try { _overlayHost.SetVisibleRegion(null); } catch { }
                try { _mpcvrOverlayPopup.ClearVisibleRegion(); } catch { }
                try { _mpcvrOverlayPopup.Hide(); } catch { }
                HideMpcvrChildOverlayHost();
                return;
            }

            if (ShouldUseMpcvrChildOverlayHost())
            {
                MoveOverlayControlsTo(EnsureMpcvrChildOverlayHost());
                try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                try { _overlayHost.SetVisibleRegion(null); } catch { }
                try { _mpcvrOverlayPopup.ClearVisibleRegion(); } catch { }
                try { _mpcvrOverlayPopup.Hide(); } catch { }
                return;
            }

            if (ShouldUseMpcvrMixerOverlayMirror())
            {
                MoveOverlayControlsTo(_overlayHost.Surface);
                try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                try { _overlayHost.SetVisibleRegion(null); } catch { }
                try { _overlayHost.SetClickThrough(true); } catch { }
                try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                try { _mpcvrOverlayPopup.ClearVisibleRegion(); } catch { }
                try { _mpcvrOverlayPopup.Hide(); } catch { }
                try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                return;
            }

            HideMpcvrChildOverlayHost();
            HideMpcvrDedicatedOverlay();

            bool usePopup = ShouldUseMpcvrPopupOverlay();
            bool useRegionHost = ShouldUseMpcvrRegionOverlayHost();
            try { _overlayHost.SetRegionOverlayMode(useRegionHost); } catch { }

            if (useRegionHost)
            {
                MoveOverlayControlsTo(_overlayHost.Surface);
                try { _overlayHost.SetInteractive(HasInteractivePlaybackOverlay()); } catch { }
                try { _overlayHost.SetClickThrough(false); } catch { }
                try { _overlayHost.SetVisibleRegion(BuildMpcvrOverlayVisibleRegions(_overlayHost.Surface)); } catch { }
                try { _mpcvrOverlayPopup.ClearVisibleRegion(); } catch { }
                try { _mpcvrOverlayPopup.Hide(); } catch { }
                return;
            }

            try { _overlayHost.SetVisibleRegion(null); } catch { }
            try { _overlayHost.SetInteractive(HasInteractivePlaybackOverlay()); } catch { }
            try { _overlayHost.SetClickThrough(!HasVisibleOverlayHostContent()); } catch { }

            if (!usePopup)
            {
                try { _mpcvrOverlayPopup.ClearVisibleRegion(); } catch { }
                return;
            }

            MoveOverlayControlsTo(_mpcvrOverlayPopup.Surface);
            try { _mpcvrOverlayPopup.SetVisibleRegion(BuildMpcvrOverlayVisibleRegions(_mpcvrOverlayPopup.Surface)); } catch { }
        }

        private List<Rectangle> BuildMpcvrOverlayVisibleRegions(Control coordinateHost)
        {
            var rects = new List<Rectangle>();

            if (coordinateHost == null || coordinateHost.IsDisposed)
                return rects;

            Rectangle hostClip;
            try { hostClip = coordinateHost.ClientRectangle; }
            catch { hostClip = Rectangle.Empty; }

            if (hostClip.Width <= 0 || hostClip.Height <= 0)
                return rects;

            void Add(Rectangle rc)
            {
                rc = Rectangle.Intersect(hostClip, rc);
                if (rc.Width > 0 && rc.Height > 0)
                    rects.Add(rc);
            }

            Rectangle ToHost(Control control, Rectangle local)
            {
                try
                {
                    var screen = control.RectangleToScreen(local);
                    var p = coordinateHost.PointToClient(screen.Location);
                    return new Rectangle(p, screen.Size);
                }
                catch
                {
                    return Rectangle.Empty;
                }
            }

            if (_pausePlaceholder?.Visible == true)
            {
                Add(hostClip);
                return rects;
            }

            if (_hud?.RequestedVisible == true && !_hud.IsDisposed)
            {
                try
                {
                    Add(ToHost(_hud, _hud.TopOverlayBounds));
                    Add(ToHost(_hud, _hud.BottomOverlayBounds));

                    if (_lastIntroOutroPromptSkipVisible || _lastIntroOutroPromptNextVisible)
                        Add(ToHost(_hud, _hud.PromptOverlayBounds));
                }
                catch { }
            }

            if (_infoOverlay?.Visible == true && !_infoOverlay.IsDisposed)
            {
                int cardW = Math.Max(0, _infoOverlay.Width - 16);
                int cardH = Math.Max(0, _infoOverlay.Height - 8);
                Add(ToHost(_infoOverlay, new Rectangle(8, 4, cardW, cardH)));
            }

            if (_remoteOsd?.Visible == true && !_remoteOsd.IsDisposed)
            {
                var osdBounds = ToHost(_remoteOsd, _remoteOsd.ClientRectangle);
                if (osdBounds.Width > 0 && osdBounds.Height > 0)
                {
                    int boxW = Math.Min(420, Math.Max(260, osdBounds.Width / 3 + 60));
                    int boxH = 190;
                    Add(new Rectangle(
                        osdBounds.Left + (osdBounds.Width - boxW) / 2,
                        osdBounds.Top + (osdBounds.Height - boxH) / 2,
                        boxW,
                        boxH));
                }
            }

            if (_photoHud?.Visible == true && !_photoHud.IsDisposed)
            {
                var photoBounds = ToHost(_photoHud, _photoHud.ClientRectangle);
                if (photoBounds.Width > 0 && photoBounds.Height > 0)
                {
                    int size = 96;
                    int pad = 14;
                    int y = photoBounds.Top + (photoBounds.Height - size) / 2;
                    Add(new Rectangle(photoBounds.Left + pad, y, size, size));
                    Add(new Rectangle(photoBounds.Right - pad - size, y, size, size));
                }
            }

            return rects;
        }

        private bool ShouldUseMpcvrMixerOverlayMirror()
        {
            // The mixer bitmap path is useful in theory, but with MPCVR it races the
            // normal HUD host and makes the lower overlay flicker or depend on Info.
            return false;
        }

        private void ClearMpcvrMixerOverlayMirror()
        {
            try
            {
                if (_engine is DirectShowUnifiedEngine ds)
                    ds.ClearMpcvrOverlayBitmap();
            }
            catch { }

            try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }
            _lastMpcvrMixerOverlayUpdateUtc = DateTime.MinValue;
            _mpcvrMixerOverlayRetryUtc = DateTime.MinValue;
            _mpcvrDedicatedLayeredClearedRenderer = false;
        }

        private void ShowMpcvrBitmapOverlayMirror(Bitmap bitmap)
        {
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
                return;

            try
            {
                if (_mpcvrBitmapOverlayHost == null || _mpcvrBitmapOverlayHost.IsDisposed)
                {
                    _mpcvrBitmapOverlayHost = new MpcvrBitmapOverlayForm();
                    _mpcvrBitmapOverlayHost.ContextMenuRequested += ShowMpcvrContextMenuFromOverlay;
                }

                Rectangle screenRect = GetMpcvrOverlayScreenRect();
                bool ok = _mpcvrBitmapOverlayHost.ShowBitmap(this, screenRect, bitmap, _mpcvrMixerOverlayKey, clickThrough: true);
                if (!ok)
                    _mpcvrBitmapOverlayHost.HideOverlay();
            }
            catch { }
        }

        private void ShowMpcvrContextMenuFromOverlay(Point screenPoint)
        {
            try
            {
                ShowPlayerContextMenuAtScreen(screenPoint);
            }
            catch { }
        }

        private void UpdateMpcvrMixerOverlayMirror(bool force = false)
        {
            if (_mpcvrMixerOverlayUpdateActive)
                return;

            if (!ShouldUseMpcvrMixerOverlayMirror())
            {
                bool hadMirror = _lastMpcvrMixerOverlayUpdateUtc != DateTime.MinValue
                    || (_mpcvrBitmapOverlayHost != null && !_mpcvrBitmapOverlayHost.IsDisposed && _mpcvrBitmapOverlayHost.Visible);
                if (hadMirror)
                    ClearMpcvrMixerOverlayMirror();
                return;
            }

            if (_engine is not DirectShowUnifiedEngine ds)
                return;

            if (!HasVisibleOverlayHostContent())
            {
                ClearMpcvrMixerOverlayMirror();
                return;
            }

            var now = DateTime.UtcNow;
            bool useDedicatedLayeredOverlay = ShouldUseMpcvrDedicatedOverlay();
            bool skipMixerAttempt = !force && now < _mpcvrMixerOverlayRetryUtc;

            double sinceLast = (now - _lastMpcvrMixerOverlayUpdateUtc).TotalMilliseconds;
            if (!force && sinceLast < 90)
            {
                // Il limite scartava l'ultimo passo della dissolvenza: senza altri ridisegni
                // (es. in pausa) l'HUD restava copiato a meta' opacita', cioe' oscurato.
                // Un aggiornamento finale parte comunque allo scadere della finestra.
                ScheduleTrailingMpcvrMirrorUpdate((int)Math.Ceiling(90 - sinceLast) + 1);
                return;
            }

            _mpcvrMixerOverlayUpdateActive = true;
            try
            {
                using var bmp = BuildMpcvrMixerOverlayBitmap();
                if (bmp == null)
                {
                    ClearMpcvrMixerOverlayMirror();
                    return;
                }

                if (useDedicatedLayeredOverlay)
                {
                    if (!_mpcvrDedicatedLayeredClearedRenderer)
                    {
                        try { ds.ClearMpcvrOverlayBitmap(); } catch { }
                        _mpcvrDedicatedLayeredClearedRenderer = true;
                    }

                    ShowMpcvrBitmapOverlayMirror(bmp);
                    _mpcvrMixerOverlayRetryUtc = DateTime.MinValue;
                    _lastMpcvrMixerOverlayUpdateUtc = now;
                }
                else if (!skipMixerAttempt && ds.TrySetMpcvrOverlayBitmap(bmp, _mpcvrMixerOverlayKey))
                {
                    try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }
                    _mpcvrDedicatedLayeredClearedRenderer = false;
                    _mpcvrMixerOverlayRetryUtc = DateTime.MinValue;
                    _lastMpcvrMixerOverlayUpdateUtc = now;
                }
                else
                {
                    ShowMpcvrBitmapOverlayMirror(bmp);
                    _mpcvrMixerOverlayRetryUtc = now.AddSeconds(2);
                    _lastMpcvrMixerOverlayUpdateUtc = now;
                }
            }
            catch { }
            finally
            {
                _mpcvrMixerOverlayUpdateActive = false;
            }
        }

        private System.Windows.Forms.Timer? _mpcvrMirrorTrailingTimer;

        private void ScheduleTrailingMpcvrMirrorUpdate(int delayMs)
        {
            if (_mpcvrMirrorTrailingTimer == null)
            {
                _mpcvrMirrorTrailingTimer = new System.Windows.Forms.Timer();
                _mpcvrMirrorTrailingTimer.Tick += (_, _) =>
                {
                    _mpcvrMirrorTrailingTimer?.Stop();
                    if (IsDisposed || _closingForExit) return;
                    try { UpdateMpcvrMixerOverlayMirror(); } catch { }
                };
                Disposed += (_, _) => { _mpcvrMirrorTrailingTimer?.Dispose(); _mpcvrMirrorTrailingTimer = null; };
            }
            if (_mpcvrMirrorTrailingTimer.Enabled) return; // gia' in programma: copiera' lo stato piu' recente
            _mpcvrMirrorTrailingTimer.Interval = Math.Clamp(delayMs, 1, 100);
            _mpcvrMirrorTrailingTimer.Start();
        }

        private Bitmap? BuildMpcvrMixerOverlayBitmap()
        {
            SyncHudInfoOverlayMode();

            Control? renderSurface = null;
            try
            {
                if (ShouldUseMpcvrDedicatedOverlay())
                    renderSurface = EnsureMpcvrOverlayStagingSurface();
            }
            catch { }

            renderSurface ??= _videoHost;

            if (renderSurface == null || renderSurface.IsDisposed)
                return null;

            int w = Math.Max(0, renderSurface.ClientSize.Width);
            int h = Math.Max(0, renderSurface.ClientSize.Height);
            if (w <= 0 || h <= 0)
                return null;

            var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(canvas);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(_mpcvrMixerOverlayKey);
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                RenderControlToMpcvrMixer(g, renderSurface, _pausePlaceholder);
                RenderControlToMpcvrMixer(g, renderSurface, _hud);
                RenderControlToMpcvrMixer(g, renderSurface, _infoOverlay);
                RenderControlToMpcvrMixer(g, renderSurface, _remoteOsd);
                RenderControlToMpcvrMixer(g, renderSurface, _photoHud);
                return canvas;
            }
            catch
            {
                canvas.Dispose();
                return null;
            }
        }

        private void RenderControlToMpcvrMixer(Graphics target, Control coordinateHost, Control? control)
        {
            if (control == null || control.IsDisposed || !control.Visible)
                return;

            if (control.Width <= 0 || control.Height <= 0)
                return;

            Rectangle dest;
            try
            {
                var screen = control.RectangleToScreen(control.ClientRectangle);
                var location = coordinateHost.PointToClient(screen.Location);
                dest = new Rectangle(location, screen.Size);
            }
            catch
            {
                return;
            }

            var clip = new Rectangle(0, 0, coordinateHost.ClientSize.Width, coordinateHost.ClientSize.Height);
            if (!clip.IntersectsWith(dest))
                return;

            using var layer = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(layer))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(_mpcvrMixerOverlayKey);
            }

            var parent = control.Parent;
            bool changedParentBack = false;
            Color oldParentBack = Color.Empty;

            try
            {
                if (parent != null && !parent.IsDisposed && parent.BackColor != _mpcvrMixerOverlayKey)
                {
                    oldParentBack = parent.BackColor;
                    SetRedraw(parent, false);
                    parent.BackColor = _mpcvrMixerOverlayKey;
                    changedParentBack = true;
                }

                control.DrawToBitmap(layer, new Rectangle(Point.Empty, control.Size));
            }
            catch
            {
                return;
            }
            finally
            {
                if (changedParentBack && parent != null && !parent.IsDisposed)
                {
                    try { parent.BackColor = oldParentBack; } catch { }
                    try { SetRedraw(parent, true); } catch { }
                }
            }

            try { layer.MakeTransparent(_mpcvrMixerOverlayKey); } catch { }
            target.DrawImageUnscaled(layer, dest.Location);
        }

        private void EnsureMpcvrVideoHostInFrontWhenIdle()
        {
            if (!ShouldKeepMpcvrVideoHostInFront()) return;

            try
            {
                if (_videoHost != null && !_videoHost.IsDisposed)
                {
                    _videoHost.Visible = true;
                    _videoHost.BringToFront();
                }
            }
            catch { }
        }

        private void ForceMpcvrChildWindowsVisible(string reason)
        {
            if (!IsMpcvrActive) return;
            if (_videoHost == null || _videoHost.IsDisposed || !_videoHost.IsHandleCreated) return;

            int w = Math.Max(2, _videoHost.ClientSize.Width);
            int h = Math.Max(2, _videoHost.ClientSize.Height);
            var details = new List<string>();

            try
            {
                EnumChildWindows(_videoHost.Handle, (hwnd, _) =>
                {
                    try
                    {
                        if (IsMpcvrChildOverlayWindow(hwnd))
                            return true;

                        bool wasVisible = IsWindowVisible(hwnd);
                        string cls = GetWindowClassName(hwnd);
                        long style = 0, exStyle = 0;
                        try { style = GetWindowLongPtrSafe(hwnd, GWL_STYLE).ToInt64(); } catch { }
                        try { exStyle = GetWindowLongPtrSafe(hwnd, GWL_EXSTYLE).ToInt64(); } catch { }

                        ShowWindow(hwnd, 5); // SW_SHOW
                        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h,
                            Win32.SWP_SHOWWINDOW |
                            Win32.SWP_NOZORDER |
                            Win32.SWP_FRAMECHANGED |
                            0x0010 /* SWP_NOACTIVATE */);

                        bool isVisible = IsWindowVisible(hwnd);
                        if (GetWindowRect(hwnd, out var r))
                        {
                            details.Add($"0x{hwnd.ToInt64():X}:{cls}, vis={wasVisible}->{isVisible}, rect={Math.Max(0, r.Right - r.Left)}x{Math.Max(0, r.Bottom - r.Top)}@{r.Left},{r.Top}, style=0x{style:X}, ex=0x{exStyle:X}");
                        }
                        else
                        {
                            details.Add($"0x{hwnd.ToInt64():X}:{cls}, vis={wasVisible}->{isVisible}, style=0x{style:X}, ex=0x{exStyle:X}");
                        }
                    }
                    catch { }

                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            try { RefreshMpcvrChildWindowHooks(); } catch { }

            if (_mpcvrChildWindowLogCount < 8)
            {
                _mpcvrChildWindowLogCount++;
                string msg = details.Count == 0
                    ? $"[VIDEO] MPCVR child hwnd force ({reason}): no child windows under videoHost=0x{_videoHost.Handle.ToInt64():X}"
                    : $"[VIDEO] MPCVR child hwnd force ({reason}): {string.Join(" | ", details)}";
                Dbg.Log(msg, Dbg.LogLevel.Info);
            }
        }

        private bool HasMpcvrVideoChildWindow()
        {
            if (_videoHost == null || _videoHost.IsDisposed || !_videoHost.IsHandleCreated)
                return false;

            bool found = false;
            try
            {
                EnumChildWindows(_videoHost.Handle, (hwnd, _) =>
                {
                    if (IsMpcvrChildOverlayWindow(hwnd) || !IsWindowVisible(hwnd) ||
                        !GetWindowClassName(hwnd).Contains("VRWindow", StringComparison.OrdinalIgnoreCase) ||
                        !GetWindowRect(hwnd, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
                        return true;
                    found = true;
                    return false;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        private void RefreshMpcvrChildWindowHooks()
        {
            if (_engine == null || !_currentMediaHasVideo || _closingForExit || _videoHost == null || _videoHost.IsDisposed || !_videoHost.IsHandleCreated)
            {
                DetachMpcvrChildWindowHooks();
                return;
            }

            var seen = new HashSet<IntPtr>();
            try
            {
                EnumChildWindows(_videoHost.Handle, (hwnd, _) =>
                {
                    try
                    {
                        if (hwnd == IntPtr.Zero || IsMpcvrChildOverlayWindow(hwnd))
                            return true;

                        seen.Add(hwnd);
                        AttachMpcvrChildWindowHook(hwnd);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            DetachMpcvrChildWindowHooks(staleOnly: true, liveChildren: seen);
        }

        private void AttachMpcvrChildWindowHook(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _mpcvrChildWndProcs.ContainsKey(hwnd))
                return;

            IntPtr proc = Marshal.GetFunctionPointerForDelegate(_mpcvrChildWndProc);
            IntPtr oldProc = IntPtr.Zero;
            try { oldProc = GetWindowLongPtrSafe(hwnd, GWL_WNDPROC); } catch { }
            if (oldProc == IntPtr.Zero || oldProc == proc)
                return;

            try
            {
                SetWindowLongPtrSafe(hwnd, GWL_WNDPROC, proc);
                IntPtr current = GetWindowLongPtrSafe(hwnd, GWL_WNDPROC);
                if (current == proc)
                    _mpcvrChildWndProcs[hwnd] = oldProc;
            }
            catch { }
        }

        private void DetachMpcvrChildWindowHooks(bool staleOnly = false, HashSet<IntPtr>? liveChildren = null)
        {
            if (_mpcvrChildWndProcs.Count == 0)
                return;

            IntPtr proc = IntPtr.Zero;
            try { proc = Marshal.GetFunctionPointerForDelegate(_mpcvrChildWndProc); } catch { }

            foreach (var kv in _mpcvrChildWndProcs.ToArray())
            {
                bool stale = true;
                try
                {
                    stale = !IsWindow(kv.Key)
                        || (_videoHost == null || _videoHost.IsDisposed || !_videoHost.IsHandleCreated)
                        || !IsChild(_videoHost.Handle, kv.Key)
                        || (liveChildren != null && !liveChildren.Contains(kv.Key));
                }
                catch { stale = true; }

                if (staleOnly && !stale)
                    continue;

                try
                {
                    if (proc != IntPtr.Zero && IsWindow(kv.Key) && GetWindowLongPtrSafe(kv.Key, GWL_WNDPROC) == proc)
                        SetWindowLongPtrSafe(kv.Key, GWL_WNDPROC, kv.Value);
                }
                catch { }

                _mpcvrChildWndProcs.Remove(kv.Key);
            }
        }

        private IntPtr MpcvrChildNativeWndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            const int WM_CONTEXTMENU = 0x007B;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_RBUTTONUP = 0x0205;
            const int WM_NCDESTROY = 0x0082;

            if (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_CONTEXTMENU)
            {
                try
                {
                    var screenPoint = GetNativeMouseScreenPoint(hWnd, msg, lParam);
                    var now = DateTime.UtcNow;
                    if ((_lastMpcvrContextMenuRequestUtc == DateTime.MinValue) ||
                        (now - _lastMpcvrContextMenuRequestUtc).TotalMilliseconds > 220)
                    {
                        _lastMpcvrContextMenuRequestUtc = now;
                        TryBeginInvokeOnUi(() => ShowPlayerContextMenuAtScreen(screenPoint));
                    }
                }
                catch { }
                return IntPtr.Zero;
            }

            IntPtr oldProc = IntPtr.Zero;
            try { _mpcvrChildWndProcs.TryGetValue(hWnd, out oldProc); } catch { }

            if (msg == WM_NCDESTROY)
            {
                try
                {
                    if (oldProc != IntPtr.Zero)
                        SetWindowLongPtrSafe(hWnd, GWL_WNDPROC, oldProc);
                }
                catch { }
                try { _mpcvrChildWndProcs.Remove(hWnd); } catch { }
            }

            if (oldProc != IntPtr.Zero)
            {
                try { return CallWindowProc(oldProc, hWnd, msg, wParam, lParam); } catch { }
            }

            return IntPtr.Zero;
        }

        private static Point GetNativeMouseScreenPoint(IntPtr hwnd, int msg, IntPtr lParam)
        {
            const int WM_CONTEXTMENU = 0x007B;

            try
            {
                long raw = lParam.ToInt64();
                if (msg == WM_CONTEXTMENU)
                {
                    if (raw == -1)
                        return Control.MousePosition;

                    int sx = unchecked((short)(raw & 0xFFFF));
                    int sy = unchecked((short)((raw >> 16) & 0xFFFF));
                    return new Point(sx, sy);
                }

                var pt = new NativePoint
                {
                    X = unchecked((short)(raw & 0xFFFF)),
                    Y = unchecked((short)((raw >> 16) & 0xFFFF))
                };
                if (ClientToScreen(hwnd, ref pt))
                    return new Point(pt.X, pt.Y);
            }
            catch { }

            return Control.MousePosition;
        }

        private void ForwardVideoHostOwnerMessageToRenderer(Message m)
        {
            if (!IsMpcvrActive) return;
            if (_engine is not DirectShowUnifiedEngine ds) return;

            const int WM_MOVE = 0x0003;
            const int WM_SIZE = 0x0005;
            const int WM_PAINT = 0x000F;
            const int WM_ERASEBKGND = 0x0014;
            const int WM_SHOWWINDOW = 0x0018;
            const int WM_WINDOWPOSCHANGED = 0x0047;
            const int WM_DISPLAYCHANGE = 0x007E;

            switch (m.Msg)
            {
                case WM_MOVE:
                case WM_SIZE:
                case WM_PAINT:
                case WM_ERASEBKGND:
                case WM_SHOWWINDOW:
                case WM_WINDOWPOSCHANGED:
                case WM_DISPLAYCHANGE:
                    ds.NotifyVideoOwnerMessage(m.HWnd, m.Msg, m.WParam, m.LParam);
                    break;
            }
        }

        private void HideMpcvrOverlayHostWhenIdle()
        {
            if (_overlayHost == null || _overlayHost.IsDisposed) return;
            if (!ShouldKeepMpcvrOverlayHostHidden()) return;

            HideMpcvrOverlaySurfaces();
        }

        private void HideMpcvrOverlaySurfaces()
        {
            HideMpcvrDedicatedOverlay();
            HideMpcvrChildOverlayHost();
            try { _overlayHost.SetClickThrough(true); } catch { }
            try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
            try { _mpcvrOverlayPopup.SetClickThrough(true); } catch { }
            try { _mpcvrOverlayPopup.Hide(); } catch { }
            try { ClearMpcvrMixerOverlayMirror(); } catch { }
        }

        private void HideExternalPlaybackOverlayHosts()
        {
            _hudInputSurface?.Hide();
            try { _videoVignette?.HideOverlay(); _vignetteOpacity = -1; } catch { }
            try { _overlayHost?.SetInteractive(false); } catch { }
            try { _overlayHost?.SetClickThrough(true); } catch { }
            try { if (_overlayHost?.Visible == true) _overlayHost.Hide(); } catch { }
            try { _mpcvrOverlayPopup?.SetInteractive(false); } catch { }
            try { _mpcvrOverlayPopup?.SetClickThrough(true); } catch { }
            try { _mpcvrOverlayPopup?.Hide(); } catch { }
            try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }
            try { _mpcvrOverlayStagingHost?.Hide(); } catch { }
            try { HideMpcvrDedicatedOverlay(); } catch { }
            try { HideMpcvrChildOverlayHost(); } catch { }
            try { ClearMpcvrMixerOverlayMirror(); } catch { }
        }

        private void ForceHidePlaybackOverlaySurfaces(bool preserveLoading = false)
        {
            if (!preserveLoading) try { CancelVideoLoadingUiImmediate(releasePointerCapture: true); } catch { }
            try { _hud.Visible = false; } catch { }
            try { _hud.TimelineVisible = false; } catch { }
            try { _infoOverlay.Visible = false; } catch { }
            try { _remoteOsd.Visible = false; } catch { }
            try { if (_pausePlaceholder != null) _pausePlaceholder.Visible = false; } catch { }
            try { if (_photoHud != null) _photoHud.Visible = false; } catch { }
            try
            {
                if (_overlayInlineHost != null)
                {
                    _overlayInlineHost.ClickThrough = true;
                    _overlayInlineHost.Visible = false;
                }
            }
            catch { }
            HideExternalPlaybackOverlayHosts();
            try { DetachVideoForPausePlaceholder(false); } catch { }
        }

        private void QuarantineRendererWindowBeforeStop(IPlaybackEngine engine)
        {
            try
            {
                // DirectShow espone una finestra nativa distinta dal controllo WinForms.
                // Sganciala immediatamente sul thread che l'ha creata.
                if (engine is DirectShowUnifiedEngine directShow)
                    directShow.DetachVideoWindowForUiTransition();
                else
                {
                    if (!_videoDetachHost.IsHandleCreated)
                        _videoDetachHost.CreateControl();
                    engine.UpdateVideoWindow(_videoDetachHost.Handle, new Rectangle(0, 0, 2, 2));
                }
            }
            catch { }

            // Nascondi anche eventuali child HWND creati dal renderer sotto videoHost.
            // Sono finestre native e possono sopravvivere per qualche frame al graph.
            try
            {
                if (_videoHost != null && !_videoHost.IsDisposed && _videoHost.IsHandleCreated)
                {
                    EnumChildWindows(_videoHost.Handle, (hwnd, _) =>
                    {
                        try { ShowWindow(hwnd, 0); } catch { }
                        return true;
                    }, IntPtr.Zero);
                    _videoHost.Visible = false;
                }
            }
            catch { }
        }

        private void HandleMpcvrSwitchFullscreen(bool exclusive)
        {
            if (!IsMpcvrActive)
                return;

            bool changed = _mpcvrExclusiveMode != exclusive;
            _mpcvrExclusiveMode = exclusive;

            try
            {
                Dbg.Log($"[VIDEO] MPCVR exclusive mode {(exclusive ? "entered" : "left")}.", Dbg.LogLevel.Info);
            }
            catch { }

            try { UpdateVideoWindowForCurrentHost(); } catch { }
            try { SyncOverlayToVideoRect(); } catch { }
            try { BringOverlaysToFront(); } catch { }

            if (changed)
            {
                try { ForceOverlayRepaint(); } catch { }
            }
        }

        private void EnsureStatsTimerRunning()
        {
            if (!_statsTimerInitialized)
            {
                _statsTimerInitialized = true;

                _statsTimer.Tick += (_, __) =>
                {
                    try
                    {
                        if (_engine == null)
                            return;

                        double pos = GetTimelinePositionForHud();
                        try { if (HasMusicPlayback && !_lyricsFrameTimer.Enabled) _audioMeters?.UpdateLyricsPosition(LyricsClockNow()); } catch { }
                        if (Environment.TickCount64 - _lastRemotePublishTick >= 450)
                            PublishRemoteState(pos);


                    }
                    catch { /* best-effort */ }
                };
            }

            if (!_statsTimer.Enabled)
                _statsTimer.Start();
        }

        private void SafeShowOverlayHost()
        {
            if (_overlayHost == null || _overlayHost.IsDisposed) return;
            if (ShouldSuppressExternalPlaybackOverlays())
            {
                HideExternalPlaybackOverlayHosts();
                return;
            }
            if (!IsCinecoreForeground())
            {
                SuppressPlaybackWindowsWhenInactive();
                return;
            }
            try { RefreshMpcvrChildWindowHooks(); } catch { }
            if (ShouldUseMpcvrDedicatedOverlay())
            {
                EnsureStatsTimerRunning();
                SyncMpcvrDedicatedOverlay();
                return;
            }

            if (ShouldUseMpcvrChildOverlayHost())
            {
                EnsureStatsTimerRunning();
                SyncMpcvrChildOverlayHost();
                return;
            }

            if (ShouldKeepMpcvrOverlayHostHidden())
            {
                HideMpcvrOverlayHostWhenIdle();
                return;
            }
            EnsureStatsTimerRunning();
            if (ShouldUseMpcvrPopupOverlay())
            {
                try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                try { _overlayHost.SetClickThrough(true); } catch { }
                try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                try
                {
                    MoveOverlayControlsTo(_mpcvrOverlayPopup.Surface);
                    Rectangle formClientScreen = GetMpcvrOverlayScreenRect();
                    _mpcvrOverlayPopup.SetClickThrough(false);
                    _mpcvrOverlayPopup.SyncToScreen(formClientScreen, showIfHidden: true);
                    _mpcvrOverlayPopup.SetVisibleRegion(BuildMpcvrOverlayVisibleRegions(_mpcvrOverlayPopup.Surface));
                    _mpcvrOverlayPopup.BringToFront();
                }
                catch { }
                return;
            }
            try { UpdateMpcvrOverlayRegionMode(); } catch { }
            if (_overlayHost.Visible)
            {
                try { SyncOverlayToVideoRect(); } catch { }
                try { UpdateMpcvrOverlayRegionMode(); } catch { }
                try { _overlayHost.RaiseAboveOwner(); } catch { }
                return;
            }

            if (!IsHandleCreated)
            {
                if (_pendingShowOverlayOnHandleCreated) return;
                _pendingShowOverlayOnHandleCreated = true;

                void Handler(object? s, EventArgs e)
                {
                    try { this.HandleCreated -= Handler; }
                    catch { }
                    finally
                    {
                        _pendingShowOverlayOnHandleCreated = false;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (ShouldSuppressExternalPlaybackOverlays())
                                    {
                                        HideExternalPlaybackOverlayHosts();
                                        return;
                                    }
                                    if (ShouldKeepMpcvrOverlayHostHidden())
                                    {
                                        HideMpcvrOverlayHostWhenIdle();
                                        return;
                                    }
                                    if (_overlayHost == null || _overlayHost.IsDisposed || _overlayHost.Visible) return;
                                    try { UpdateMpcvrOverlayRegionMode(); } catch { }
                                    ShowOverlayHostOwned();
                                    try { SyncOverlayToVideoRect(); } catch { }
                                    try { UpdateMpcvrOverlayRegionMode(); } catch { }
                                    try { _overlayHost.RaiseAboveOwner(); } catch { }
                                }
                                catch
                                {
                                    try
                                    {
                                        if (ShouldSuppressExternalPlaybackOverlays())
                                        {
                                            HideExternalPlaybackOverlayHosts();
                                            return;
                                        }
                                        if (_overlayHost != null && !_overlayHost.IsDisposed)
                                        {
                                            _overlayHost.Hide();
                                            ShowOverlayHostOwned();
                                            _overlayHost.RaiseAboveOwner();
                                        }
                                    }
                                    catch { }
                                }
                            }));
                        }
                        catch
                        {
                            try { if (_overlayHost != null && !_overlayHost.IsDisposed && !_overlayHost.Visible) ShowOverlayHostOwned(); } catch { }
                        }
                    }
                }

                this.HandleCreated += Handler;
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (ShouldSuppressExternalPlaybackOverlays())
                        {
                            HideExternalPlaybackOverlayHosts();
                            return;
                        }
                        if (ShouldKeepMpcvrOverlayHostHidden())
                        {
                            HideMpcvrOverlayHostWhenIdle();
                            return;
                        }
                        if (_overlayHost == null || _overlayHost.IsDisposed || _overlayHost.Visible) return;
                        try { UpdateMpcvrOverlayRegionMode(); } catch { }
                        ShowOverlayHostOwned();
                        try { SyncOverlayToVideoRect(); } catch { }
                        try { UpdateMpcvrOverlayRegionMode(); } catch { }
                        try { _overlayHost.RaiseAboveOwner(); } catch { }
                    }
                    catch (InvalidOperationException)
                    {
                        try
                        {
                            if (ShouldSuppressExternalPlaybackOverlays())
                                HideExternalPlaybackOverlayHosts();
                            else
                            {
                                _overlayHost.Hide();
                                ShowOverlayHostOwned();
                                _overlayHost.RaiseAboveOwner();
                            }
                        }
                        catch { }
                    }
                    catch { /* best-effort */ }
                }));
            }
            catch (InvalidOperationException)
            {
                SafeShowOverlayHost();
            }
        }

        private void LayoutInfoOverlay()
        {
            try
            {
                if (_infoOverlay == null || _infoOverlay.IsDisposed)
                    return;

                if (_infoOverlay.Parent == null)
                    return;

                var hostSize = _infoOverlay.Parent.ClientSize;
                if (hostSize.Width <= 0 || hostSize.Height <= 0)
                    return;

                bool compact = hostSize.Width < 900 || hostSize.Height < 700;
                int marginX = compact ? 24 : 54;
                int topOverlayBottom = 0;
                try
                {
                    Rectangle topOverlay = _hud?.TopOverlayBounds ?? Rectangle.Empty;
                    if (!topOverlay.IsEmpty)
                        topOverlayBottom = topOverlay.Bottom;
                }
                catch { }
                // Il pannello resta sempre sotto la fascia del titolo dell'HUD, anche quando l'HUD e'
                // nascosto: prima saliva e scendeva (e cambiava altezza) a ogni comparsa dell'HUD.
                _infoTopOverlayBottom = Math.Max(_infoTopOverlayBottom, topOverlayBottom);
                int marginY = Math.Max(compact ? 64 : 72, _infoTopOverlayBottom + 12);
                int reservedBottom = HUD_HOTZONE_H + (compact ? 16 : 28);
                int maxAllowedHeight = Math.Max(240, hostSize.Height - marginY - reservedBottom);
                // Il pannello disegna in punti: larghezza e altezza massime seguono la scala dello schermo.
                float infoScale = _infoOverlay.LayoutScale;
                int maxHeight = Math.Min((int)(840 * infoScale), maxAllowedHeight);
                // Schermo basso (per esempio un televisore a 768 righe): due colonne affiancate.
                bool twoColumns = hostSize.Width >= 1000 && _infoOverlay.PreferredHeightFor(1) > maxHeight;
                _infoOverlay.Columns = twoColumns ? 2 : 1;
                int panelWidth = twoColumns
                    ? Math.Min((int)(940 * infoScale), hostSize.Width - (marginX * 2))
                    : Math.Min((int)((compact ? 440 : 480) * infoScale), Math.Max(320, hostSize.Width - (marginX * 2)));

                _infoOverlay.MinCardHeight = Math.Min(200, maxHeight);
                _infoOverlay.MaxCardHeight = Math.Max(_infoOverlay.MinCardHeight, maxHeight);
                if (_infoOverlay.Width != panelWidth)
                    _infoOverlay.Width = panelWidth;
                _infoOverlay.AdjustHeightToContent(panelWidth);

                int panelHeight = Math.Min(_infoOverlay.Height, _infoOverlay.MaxCardHeight);
                var nextBounds = new Rectangle(marginX, marginY, panelWidth, panelHeight);
                if (_infoOverlay.Bounds != nextBounds)
                    _infoOverlay.Bounds = nextBounds;
                SyncHudInfoOverlayMode();
            }
            catch { }
        }

        private bool _infoRequested;
        private int _infoTopOverlayBottom = 140; // fascia del titolo dell'HUD: 140 px nelle misure fatte

        // Control.Visible diventa false quando l'host trasparente viene nascosto anche
        // solo per un istante: la richiesta dell'utente va ricordata a parte.
        private bool IsInfoPanelShown() => _infoRequested && _engine != null && !IsPhotoMode;

        private void SetInfoPanelRequested(bool requested)
        {
            _infoRequested = requested;
            try
            {
                if (requested)
                {
                    RefreshInfoOverlayNow();
                    _infoOverlay.Visible = true;
                    try { SafeShowOverlayHost(); } catch { }
                    BringOverlaysToFront();
                }
                else
                {
                    _infoOverlay.Visible = false;
                    SyncHudInfoOverlayMode();
                }
            }
            catch { }
        }

        private void SyncHudInfoOverlayMode()
        {
            try
            {
                if (_hud == null || _hud.IsDisposed)
                    return;

                bool enabled = _infoOverlay?.Visible == true && !IsPhotoMode;
                int reservedHeight = 0;
                if (enabled && _infoOverlay != null && !_infoOverlay.IsDisposed)
                {
                    try
                    {
                        reservedHeight = _infoOverlay.Bottom + 6;
                    }
                    catch { reservedHeight = 0; }
                }

                _hud.InfoOverlayReservedHeight = reservedHeight;
                _hud.InfoOverlayMode = enabled;
                _hud.Invalidate();
            }
            catch { }
        }

        private void BringOverlaysToFront()
        {
            if (_closingForExit || _nativeChromeInteraction || _audioSyncDialogOpen || _contextMenuActive || _contextMenuPending) return;
            UpdateVideoVignette();
            SyncHudInfoOverlayMode();

            if (!IsCinecoreForeground())
            {
                SuppressPlaybackWindowsWhenInactive();
                return;
            }

            // Le impostazioni sono una pagina opaca dello stack principale. Nessun
            // overlay di riproduzione deve riapparire sopra di essa (in particolare
            // l'HUD audio pinned), altrimenti il click sembra non avere effetto.
            if (_settingsHudPage?.Visible == true)
            {
                try { _hud.Visible = false; _hud.TimelineVisible = false; } catch { }
                try { _infoOverlay.Visible = false; } catch { }
                try { _remoteOsd.Visible = false; } catch { }
                try { HideExternalPlaybackOverlayHosts(); } catch { }
                try { _settingsHudPage.BringToFront(); } catch { }
                return;
            }

            // Rendi visibile l'HUD audio prima di decidere se l'host trasparente
            // contiene qualcosa. Farlo in fondo lasciava il controllo Visible=true
            // dentro un OverlayHost gia' nascosto.
            if (ShouldPinAudioOnlyHud())
            {
                UpdateMusicTransport();
            }

            if (ShouldKeepMpcvrVideoHostInFront())
                EnsureMpcvrVideoHostInFrontWhenIdle();
            else
                _videoHost.SendToBack();

            // In playback non vogliamo mai una focus-ring "appesa".
            // Se non siamo in una UI DPAD, nascondila.
            try
            {
                bool dpadUiVisible = IsAnyLibraryVisible();
                if (!dpadUiVisible)
                {
                    if (_focusRing != null && _focusRing.Visible)
                        _focusRing.Attach(null);
                }
            }
            catch { }

            // Ordine overlay sullo stack (sotto all'OverlayHost trasparente):
            // 1) Loading media (nero + spinner)
            if (_cinematicLibraryPage?.Visible == true) _cinematicLibraryPage.BringToFront();
            if (_netflixModePage?.Visible == true)
            {
                _netflixModePage.BringToFront();
                try { _hud.Visible = false; _hud.TimelineVisible = false; } catch { }
                try { _infoOverlay.Visible = false; } catch { }
                try { _remoteOsd.Visible = false; } catch { }
            }
            // Il loading deve essere l'ultimo layer dello stack principale. Prima veniva
            // portato davanti e subito ricoperto dalla libreria, quindi esisteva ma non
            // risultava visibile durante l'apertura di un film.
            if (_videoLoading?.Visible == true) _videoLoading.BringToFront();
            if (_videoLoading?.Visible == true)
            {
                try { _hud.Visible = false; _hud.TimelineVisible = false; } catch { }
                try { _infoOverlay.Visible = false; } catch { }
                try { _remoteOsd.Visible = false; } catch { }
                try { if (_photoHud != null) _photoHud.Visible = false; } catch { }
                // Il loading è già l'ultimo layer opaco dello stack. Non azzerare la
                // visibilità dei pannelli audio: al termine del caricamento non veniva
                // più ripristinata e i grafici (soprattutto Bitstream) restavano spenti.
                try { HideMpcvrDedicatedOverlay(); } catch { }
                try { HideMpcvrChildOverlayHost(); } catch { }
                try { _mpcvrOverlayPopup?.Hide(); } catch { }
                try { _mpcvrBitmapOverlayHost?.HideOverlay(); } catch { }
                try { if (_overlayHost?.Visible == true) _overlayHost.Hide(); } catch { }
                try { if (_overlayInlineHost != null) _overlayInlineHost.Visible = false; } catch { }
                _videoLoading.BringToFront();
                return;
            }
            if (_overlayHost != null)
            {
                if (_useInlineOverlay)
                {
                    HideMpcvrDedicatedOverlay();
                    HideMpcvrChildOverlayHost();
                    try { _overlayHost.SetClickThrough(true); } catch { }
                    try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                    try { _mpcvrOverlayPopup.SetClickThrough(true); } catch { }
                    try { _mpcvrOverlayPopup.Hide(); } catch { }
                }
                else if (ShouldUseMpcvrDedicatedOverlay())
                {
                    SyncMpcvrDedicatedOverlay();
                }
                else if (ShouldUseMpcvrChildOverlayHost())
                {
                    SyncMpcvrChildOverlayHost();
                }
                else if (ShouldKeepMpcvrOverlayHostHidden())
                {
                    HideMpcvrDedicatedOverlay();
                    HideMpcvrChildOverlayHost();
                    HideMpcvrOverlayHostWhenIdle();
                }
                else if (ShouldUseMpcvrPopupOverlay())
                {
                    HideMpcvrDedicatedOverlay();
                    HideMpcvrChildOverlayHost();
                    try { _overlayHost.SetRegionOverlayMode(false); } catch { }
                    try { _overlayHost.SetClickThrough(true); } catch { }
                    try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                    SafeShowOverlayHost();
                    try { _mpcvrOverlayPopup.BringToFront(); } catch { }
                }
                else
                {
                    HideMpcvrDedicatedOverlay();
                    HideMpcvrChildOverlayHost();
                    try { _mpcvrOverlayPopup.Hide(); } catch { }
                    if (!HasVisibleOverlayHostContent())
                    {
                        try { _overlayHost.SetClickThrough(true); } catch { }
                        try { if (_overlayHost.Visible) _overlayHost.Hide(); } catch { }
                    }
                    else
                    {
                        if (!_overlayHost.Visible) SafeShowOverlayHost();
                        try { _overlayHost.BringToFront(); } catch { }
                        try { _overlayHost.RaiseAboveOwner(); } catch { }
                    }
                }
            }

            if (_overlayInlineHost != null)
            {
                _overlayInlineHost.Visible = _useInlineOverlay && HasVisibleOverlayHostContent();
                if (_overlayInlineHost.Visible)
                    _overlayInlineHost.BringToFront();
            }

            SetModalInputState(false);

            // Info è un pannello indipendente dall'HUD: se l'utente l'ha aperto torna
            // visibile dopo caricamenti, fade dell'HUD o perdita temporanea dell'host.
            if (IsInfoPanelShown() && _infoOverlay != null && !_infoOverlay.Visible)
                _infoOverlay.Visible = true;
            if (_infoOverlay != null && _infoOverlay.Visible)
            {
                LayoutInfoOverlay();
                _infoOverlay.BringToFront();
            }

            // Placeholder in pausa: deve stare sopra il video ma sotto HUD/OSD
            if (_pausePlaceholder != null && _pausePlaceholder.Visible)
            {
                try
                {
                    _pausePlaceholder.BringToFront();
                    if (_infoOverlay != null && _infoOverlay.Visible)
                        _infoOverlay.BringToFront();
                }
                catch { }
            }

            // HUD: NON forzare la visibilità solo perché esiste un engine.
            // Deve comparire solo su interazione (mouse / scrub timeline da remoto / ecc.).
            bool hudAllowed = _engine != null && !IsPhotoMode;
            if (!hudAllowed)
            {
                _hud.Visible = false;
                _hud.TimelineVisible = false;
            }
            else
            {
                // In audio-only i grafici fanno parte della schermata principale:
                // i comandi restano presenti finche' Cinecore e' attivo.
                if (ShouldPinAudioOnlyHud())
                    _hud.Visible = false;
                _hud.TimelineVisible = _hud.Visible && GetTimelineDurationSeconds() > 0;
                if (_hud.Visible) _hud.BringToFront();
            }

            // OSD remoto: deve stare sopra tutto durante la riproduzione
            if (_remoteOsd != null && _remoteOsd.Visible)
                _remoteOsd.BringToFront();

            // In solo-audio: se i meters o il banner sono visibili, tienili sotto all'HUD
            if (_audioMetersHost?.Visible == true)
            {
                _audioMetersHost.BringToFront();
                if (_overlayInlineHost?.Visible == true) _overlayInlineHost.BringToFront();
                if (_hud.Visible) _hud.BringToFront();
                // Nell'area musica i grafici stanno nella pagina libreria: la coda aperta resta sopra.
                _cinematicLibraryPage?.KeepQueueEditorOnTop(refreshBackdrop: false);
            }
            else if (_audioOnlyBanner.Visible)
            {
                // Banner audio-only nello stack video: resta visibile anche quando l'HUD si apre.
                _audioOnlyBanner.BringToFront();
                if (_videoLoading?.Visible == true) _videoLoading.BringToFront();
                if (_overlayInlineHost?.Visible == true) _overlayInlineHost.BringToFront();
                if (_hud.Visible) _hud.BringToFront();
            }

            // --- Modalità foto: HUD classica OFF, solo PhotoHUD ---
            if (IsPhotoMode)
            {
                SetIntroOutroPromptVisibility(false, false);
                _hud.Visible = false;
                _hud.TimelineVisible = false;
                if (_photoHud != null)
                {
                    _photoHud.Visible = true;
                    _photoHud.BringToFront();
                }
            }
            else
            {
                if (_photoHud != null) _photoHud.Visible = false;
            }

            if (_pairBanner != null && !_pairBanner.IsDisposed)
            {
                try { _pairBanner.BringToFront(); } catch { }
            }

            // Ordine stabile: HUD sotto Info, OSD remoto in cima.
            if (_hud != null && _hud.Visible)
            {
                try { _hud.BringToFront(); } catch { }
            }

            if (_infoOverlay != null && _infoOverlay.Visible)
            {
                try
                {
                    LayoutInfoOverlay();
                    _infoOverlay.BringToFront();
                }
                catch { }
            }

            if (_remoteOsd != null && _remoteOsd.Visible)
            {
                try { _remoteOsd.BringToFront(); } catch { }
            }

            if (_netflixModePage != null && _netflixModePage.Visible)
            {
                try
                {
                    if (_hud != null)
                    {
                        _hud.Visible = false;
                        _hud.TimelineVisible = false;
                    }
                    if (_infoOverlay != null) _infoOverlay.Visible = false;
                    if (_remoteOsd != null) _remoteOsd.Visible = false;
                    _netflixModePage.BringToFront();
                }
                catch { }
            }

            try { UpdateMpcvrOverlayRegionMode(); } catch { }
            HideMpcvrOverlayHostWhenIdle();
            EnsureMpcvrVideoHostInFrontWhenIdle();
            try { SyncMpcvrDedicatedOverlay(); } catch { }
            try { UpdateMpcvrMixerOverlayMirror(); } catch { }
        }


        private void ForceOverlayRepaint()
        {
            try
            {
                if (_overlayHost != null && !_overlayHost.IsDisposed)
                {
                    _overlayHost.Surface.Invalidate(true);
                    _overlayHost.Surface.Update();
                }
            }
            catch { }

            try
            {
                if (_mpcvrOverlayPopup != null && !_mpcvrOverlayPopup.IsDisposed)
                {
                    _mpcvrOverlayPopup.Surface.Invalidate(true);
                    _mpcvrOverlayPopup.Surface.Update();
                }
            }
            catch { }

            try
            {
                if (_mpcvrChildOverlayHost != null && !_mpcvrChildOverlayHost.IsDisposed)
                {
                    _mpcvrChildOverlayHost.Invalidate(true);
                    _mpcvrChildOverlayHost.Update();
                    RaiseMpcvrChildOverlayHost();
                }
            }
            catch { }

            try
            {
                if (_mpcvrOverlayStagingHost != null && !_mpcvrOverlayStagingHost.IsDisposed)
                {
                    _mpcvrOverlayStagingHost.Surface.Invalidate(true);
                    _mpcvrOverlayStagingHost.Surface.Update();
                }
            }
            catch { }

            try
            {
                if (_hud != null && !_hud.IsDisposed)
                {
                    _hud.Invalidate(true);
                    _hud.Update();
                }
            }
            catch { }

            try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
        }


    }
}
