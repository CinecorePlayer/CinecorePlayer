#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        private bool _nativeChromeInteraction;
        private IntPtr _staleCaptureCandidate;
        private long _staleCaptureObservedAt;

        private bool TryRouteCapturedClickToWindowChrome(ref Message message)
        {
            const int WM_LBUTTONDOWN = 0x0201;
            if (message.Msg != WM_LBUTTONDOWN || _nativeChromeInteraction || _closingForExit ||
                IsDisposed || !IsHandleCreated || FormBorderStyle == FormBorderStyle.None)
                return false;
            try
            {
                IntPtr capture = GetCapture();
                if (capture == IntPtr.Zero || capture != message.HWnd || IsPipInputHandle(capture)) return false;
                var control = Control.FromHandle(capture);
                var form = control?.FindForm();
                if (form != this && form != _overlayHost && !IsRendererMessageWindow(capture)) return false;

                return RouteCapturedClickToChrome(this, ref message);
            }
            catch { return false; }
        }

        private static bool RouteCapturedClickToChrome(Form owner, ref Message message)
        {
            // Capture turns clicks on X/caption/resize borders into client clicks
            // addressed to the old drag control. Recover before that control sees them.
            Point point = GetNativeMouseScreenPoint(message.HWnd, message.Msg, message.LParam);
            IntPtr screenPoint = new(unchecked((point.Y << 16) | (point.X & 0xFFFF)));
            long hit = SendMessage(owner.Handle, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, screenPoint).ToInt64();
            if (!IsNativeChromeHit(hit)) return false;
            var control = Control.FromHandle(message.HWnd);
            if (control != null) control.Capture = false;
            ReleaseCapture();
            if (hit == 20 /* HTCLOSE */)
            {
                // The matching mouse-up may still be delivered to the old captured
                // control. Close directly so one click on X is sufficient.
                owner.Close();
                return true;
            }
            SendMessage(owner.Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(hit), screenPoint);
            return true;
        }

        private static bool IsNativeChromeHit(long hit) => hit == 2 || hit == 3 || hit == 8 || hit == 9 ||
            (hit >= 10 && hit <= 17) || hit == 20 || hit == 21;

        private void HudBump(int ms, bool allowWhenRemote, bool showTimeline)
        {
            if (_closingForExit || _nativeChromeInteraction) return;
            if (HasMusicPlayback && _musicWorkspaceActive) { UpdateMusicTransport(); return; }
            try
            {
                if (_fullscreenTransitioning)
                    return;
                if (_audioSyncDialogOpen || _contextMenuActive || _contextMenuPending)
                    return;

                var now = DateTime.UtcNow;
                _lastHudActivityUtc = now;

                if (!allowWhenRemote && IsRemoteCommandActive)
                    return;

                if (_engine == null) return;
                if (IsPlaybackOverlayBlockedByLoading()) return;
                if (IsPhotoMode) return;

                bool wasHidden = !_hud.Visible;
                try
                {
                    double timelineDuration = GetTimelineDurationSeconds();
                    _hud.TimelineVisible = showTimeline && timelineDuration > 0;
                }
                catch { }
                _hud.Visible = true;
                _hud.ShowOnce(ms);
                SetModalInputState(false);

                if (wasHidden)
                {
                    // La finestra dell'HUD ricompare trasparente: mostra ancora l'ultimo fotogramma di prima.
                    try { if (_overlayHost?.Visible == false && !_useInlineOverlay && _overlayHost.SupportsLayerAlpha) _overlayHost.SetLayerAlpha(0); } catch { }
                    try { SafeShowOverlayHost(); } catch { }
                    try { SyncOverlayToVideoRect(); } catch { }
                    try { BringOverlaysToFront(); } catch { }
                    try { UpdateMpcvrOverlayRegionMode(); } catch { }
                    // Finche' la finestra dell'HUD non era mostrata, l'HUD risultava "non visibile" e le
                    // routine di allineamento qui sopra gli spegnevano la timeline: al primo fotogramma
                    // c'erano i pulsanti senza la barra, che arrivava un attimo dopo. Ora che la finestra
                    // c'e', la timeline si riconferma e la dissolvenza parte da qui.
                    try { _hud.TimelineVisible = showTimeline && GetTimelineDurationSeconds() > 0; } catch { }
                    try { _hud.RestartFadeIn(); } catch { }
                }

                try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
            }
            catch { }
        }

        private long _lastPointerBumpTick;

        // Info resta a schermo finche' non lo chiude l'utente. Diverse situazioni nascondono per un attimo
        // gli overlay (menu del tasto destro, cambio di finestra attiva, schede): quando il film e' di nuovo
        // in vista il pannello torna da solo.
        private void RestoreInfoPanelIfRequested()
        {
            try
            {
                if (!IsInfoPanelShown() || _infoOverlay == null || _infoOverlay.IsDisposed || _infoOverlay.Visible) return;
                if (_closingForExit || _settingsHudPage?.Visible == true || IsAnyLibraryVisible() || _videoLoading?.Visible == true ||
                    SheetPresenter.AnyOpen || WindowState == FormWindowState.Minimized || !IsCinecoreForeground()) return;
                _infoOverlay.Visible = true;
                try { SafeShowOverlayHost(); } catch { }
                try { LayoutInfoOverlay(); } catch { }
                try { _infoOverlay.BringToFront(); } catch { }
            }
            catch { }
        }

        // L'HUD puo' sfumare con l'alfa della sua finestra solo se nella finestra c'e' soltanto lui:
        // info, OSD del telecomando e gli altri pannelli devono restare a piena opacita'.
        private bool HudHostFadeAvailable()
        {
            try
            {
                if (_useInlineOverlay || _overlayHost == null || _overlayHost.IsDisposed || !_overlayHost.SupportsLayerAlpha) return false;
                if (!ReferenceEquals(_hud.Parent, _overlayHost.Surface)) return false;
                foreach (Control sibling in _overlayHost.Surface.Controls)
                    if (!ReferenceEquals(sibling, _hud) && sibling.Visible) return false;
                return true;
            }
            catch { return false; }
        }

        private void ApplyHudHostAlpha()
        {
            try
            {
                if (_overlayHost == null || _overlayHost.IsDisposed || !_overlayHost.Visible) return;
                float alpha = _hud.Visible && HudHostFadeAvailable() ? _hud.HostAlpha : 1f;
                _overlayHost.SetLayerAlpha((byte)Math.Clamp((int)Math.Round(alpha * 255), 0, 255));
                UpdateVideoVignette();
            }
            catch { }
        }

        private void WakeHudFromPointer()
        {
            // Un mouse in movimento chiama qui centinaia di volte al secondo. Il risveglio completo
            // (allineamento delle finestre, ridisegno) saturava il thread dell'interfaccia: in 4K l'HUD non
            // compariva finche' il mouse non si fermava. A HUD gia' visibile basta prolungarne la durata.
            long tick = Environment.TickCount64;
            try
            {
                if (_hud.RequestedVisible && tick - _lastPointerBumpTick < 150 &&
                    !(!_currentMediaHasVideo && !ShouldPinAudioOnlyHud()))
                {
                    _hud.KeepAlive(HUD_IDLE_HIDE_MS);
                    _hud.NotePhysicalPointer();
                    return;
                }
            }
            catch { }
            _lastPointerBumpTick = tick;
            HudBump(HUD_IDLE_HIDE_MS, allowWhenRemote: false, showTimeline: true);
            // L'HUD compare per un movimento vero del mouse: se il puntatore e' sui comandi li tiene aperti.
            try { _hud.NotePhysicalPointer(); } catch { }
        }

        private double GetTimelineDurationSeconds()
        {
            try
            {
                if (_duration > 0)
                    return _duration;

                double engineDuration = _engine?.DurationSeconds ?? 0;
                if (engineDuration > 0)
                    return engineDuration;

                double infoDuration = _info?.Duration ?? 0;
                if (infoDuration > 0)
                    return infoDuration;
            }
            catch { }

            return 0;
        }

        private void SyncHudTimelineAvailability()
        {
            try
            {
                double duration = GetTimelineDurationSeconds();
                if (duration > 0)
                    _duration = duration;
                if (_hud != null)
                    _hud.TimelineVisible = duration > 0 && !IsPhotoMode;
            }
            catch { }
        }

        private bool IsAudioOnlyPlaybackUi()
        {
            try
            {
                return _engine != null
                    && !_currentMediaHasVideo
                    && !IsPhotoMode
                    && !_pipModeActive
                    && _pipForm?.Visible != true
                    && _settingsHudPage?.Visible != true
                    && !IsAnyLibraryVisible()
                    && _videoLoading?.Visible != true;
            }
            catch { return false; }
        }

        private bool ShouldPinAudioOnlyHud()
        {
            return HasMusicPlayback && _musicWorkspaceActive && WindowState != FormWindowState.Minimized;
        }

        private void EnsureAudioOnlyHudPinned()
        {
            if (!ShouldPinAudioOnlyHud())
                return;

            UpdateMusicTransport();
        }

        private void TickUiIdle()
        {
            try
            {
                if (_closingForExit || _nativeChromeInteraction || IsDisposed || !IsHandleCreated) return;
                UpdateMusicTransport();
                TrackVideoUnderlay();
                RestoreInfoPanelIfRequested();
                ApplyHudHostAlpha();
                UpdateVideoVignette();
                UpdateHudInputSurface();
                ReleaseStaleCinecoreMouseCapture();
                if (WindowState == FormWindowState.Minimized || !Visible || !IsCinecoreForeground())
                {
                    SuppressPlaybackWindowsWhenInactive();
                    return;
                }
                if (_fullscreenTransitioning) return;
                EnsureAudioOnlyHudPinned();
                if (_audioSyncDialogOpen || _contextMenuActive || _contextMenuPending || SheetPresenter.AnyOpen)
                {
                    // Con una scheda aperta il cursore serve: prima spariva dopo tre secondi (Analisi HDR senza puntatore).
                    EnsureCursorVisible();
                    return;
                }

                // Applica l'auto-hide solo nel player (non in libreria / modals / photo mode)
                bool uiBlocked = IsAnyLibraryVisible()
                              || _settingsHudPage?.Visible == true
                              || IsPhotoMode;

                if (uiBlocked)
                {
                    EnsureCursorVisible();
                    return;
                }

                // Se non c'è playback né gate attivo, non nascondere nulla.
                if (_engine == null && !_preOpenPlaceholderGateActive)
                {
                    EnsureCursorVisible();
                    return;
                }

                var now = DateTime.UtcNow;
                var mouseIdleMs = (now - _lastMouseMoveUtc).TotalMilliseconds;
                var hudIdleMs = (now - _lastHudActivityUtc).TotalMilliseconds;
                bool audioOnlyPlayback = IsAudioOnlyPlaybackUi();
                bool pinAudioHud = ShouldPinAudioOnlyHud();

                if (audioOnlyPlayback && !pinAudioHud && _hud.Visible &&
                    !_contextMenuActive && !_contextMenuPending)
                {
                    _hud.Visible = false;
                    try { _hud.TimelineVisible = false; } catch { }
                    try { UpdateMpcvrOverlayRegionMode(); } catch { }
                    try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                }

                // Cursore: lo nascondiamo solo se il mouse è sopra la finestra.
                try
                {
                    var p = Control.MousePosition;
                    Rectangle screen;
                    try { screen = RectangleToScreen(ClientRectangle); }
                    catch { screen = Rectangle.Empty; }
                    bool mouseOverWindow = !screen.IsEmpty && screen.Contains(p);

                    if (mouseOverWindow)
                    {
                        if (!pinAudioHud && mouseIdleMs >= HUD_IDLE_HIDE_MS && !_hud.IsPointerInteractionActive) HideCursorNow();
                        else EnsureCursorVisible();
                    }
                    else
                    {
                        // Fuori dalla finestra: non forziamo il cursore invisibile.
                        EnsureCursorVisible();
                    }
                }
                catch { }

                if (!pinAudioHud && hudIdleMs >= HUD_IDLE_HIDE_MS && !_hud.IsPointerInteractionActive && !_hud.HasVisibilityLease)
                {
                    if (_hud.RequestedVisible)
                    {
                        _hud.BeginFadeOut();
                        if (_hud.RenderOpacity > .01f) return;
                        _hud.Visible = false;
                        try { _hud.TimelineVisible = false; } catch { }
                        try { UpdateMpcvrOverlayRegionMode(); } catch { }
                        try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                        try
                        {
                            _hudWakeAnchorPos = Control.MousePosition;
                            _hudWakeLastMousePos = _hudWakeAnchorPos;
                            _hudWakeNeedsIntentionalMove = true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void ReleaseStaleCinecoreMouseCapture(bool force = false)
        {
            try
            {
                if (_nativeChromeInteraction) return;
                if (!force && IsPhysicalPointerButtonDown())
                {
                    _staleCaptureCandidate = IntPtr.Zero;
                    return;
                }

                try { _hud?.ReleaseStalePointerCapture(); } catch { }

                IntPtr capture = GetCapture();
                if (capture == IntPtr.Zero)
                {
                    _staleCaptureCandidate = IntPtr.Zero;
                    return;
                }

                // Native property pages can legitimately hold capture while editing
                // sliders, lists and combo boxes. The playback recovery timer must
                // not take that capture away from Settings controls.
                if (_settingsHudPage?.Visible == true && _settingsHudPage.IsHandleCreated &&
                    (capture == _settingsHudPage.Handle || IsChild(_settingsHudPage.Handle, capture)))
                {
                    _staleCaptureCandidate = IntPtr.Zero;
                    return;
                }

                // A physical release can precede the queued WM_LBUTTONUP. Give
                // Windows time to complete that click before treating capture as stale.
                if (!force)
                {
                    if (_staleCaptureCandidate != capture)
                    {
                        _staleCaptureCandidate = capture;
                        _staleCaptureObservedAt = Environment.TickCount64;
                        return;
                    }
                    if (Environment.TickCount64 - _staleCaptureObservedAt < 300) return;
                }
                _staleCaptureCandidate = IntPtr.Zero;

                GetWindowThreadProcessId(capture, out uint processId);
                if (processId != (uint)Environment.ProcessId)
                    return;

                // Nessun pulsante e' fisicamente premuto: una cattura ancora attiva
                // appartiene a un drag/menu che ha perso WM_LBUTTONUP nel renderer.
                // Finche' resta agganciata intercetta anche X/minimizza/massimizza.
                string source = Control.FromHandle(capture)?.GetType().Name ?? "native window";
                try
                {
                    Control? capturedControl = Control.FromHandle(capture);
                    if (capturedControl != null)
                        capturedControl.Capture = false;
                }
                catch { }
                if (ReleaseCapture())
                    Dbg.Warn($"[INPUT] Released stale Cinecore mouse capture ({source}).");
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private static bool IsPhysicalPointerButtonDown()
        {
            const int VK_LBUTTON = 0x01;
            const int VK_RBUTTON = 0x02;
            const int VK_MBUTTON = 0x04;
            const int VK_XBUTTON1 = 0x05;
            const int VK_XBUTTON2 = 0x06;
            return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_XBUTTON1) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_XBUTTON2) & 0x8000) != 0;
        }

        private static readonly Cursor _invisibleCursor = CreateInvisibleCursor();

        private static Cursor CreateInvisibleCursor()
        {
            // Crea un cursore trasparente 16x16
            Bitmap bmp = new Bitmap(16, 16);
            IntPtr ptr = bmp.GetHicon();
            Cursor cur = new Cursor(ptr);
            return cur;
        }

        private void SetSystemCursorVisible(bool visible)
        {
            try
            {
                int guard = 0;
                if (visible)
                {
                    while (ShowCursor(true) < 0 && guard++ < 8) { }
                }
                else
                {
                    while (ShowCursor(false) >= 0 && guard++ < 8) { }
                }
            }
            catch { }
        }

        private void SuppressHudForProgrammaticTransition(int suppressMs = 900)
        {
            try
            {
                // In audio-only i controlli sono parte permanente della schermata,
                // non un HUD video temporaneo. Le transizioni e gli OSD non devono
                // mai nasconderli.
                if (ShouldPinAudioOnlyHud())
                {
                    EnsureAudioOnlyHudPinned();
                    return;
                }

                var until = DateTime.UtcNow.AddMilliseconds(Math.Max(0, suppressMs));
                if (until > _suppressHudWakeUntilUtc)
                    _suppressHudWakeUntilUtc = until;

                _lastHudActivityUtc = DateTime.UtcNow;

                if (_hud != null)
                {
                    try { _hud.Visible = false; } catch { }
                    try { _hud.TimelineVisible = false; } catch { }
                    try { _hud.ClearRemoteScrub(); } catch { }
                    try { _hud.SetPreview(null, _engine?.PositionSeconds ?? 0); } catch { }
                }

                try
                {
                    _hudWakeAnchorPos = Control.MousePosition;
                    _hudWakeLastMousePos = _hudWakeAnchorPos;
                    _hudWakeNeedsIntentionalMove = true;
                }
                catch { }
            }
            catch { }
        }

        private void HideCursorNow()
        {
            if (_cursorHidden) return;
            _cursorHidden = true;
            try { Cursor = _invisibleCursor; } catch { }
            try { _videoHost.Cursor = _invisibleCursor; } catch { }
            try { _audioMetersHost.Cursor = _invisibleCursor; } catch { }
            try { if (_overlayHost != null) { _overlayHost.Cursor = _invisibleCursor; _overlayHost.Surface.Cursor = _invisibleCursor; } } catch { }
            try { SetSystemCursorVisible(false); } catch { }
        }

        private void EnsureCursorVisible()
        {
            if (!_cursorHidden) return;
            _cursorHidden = false;
            try { Cursor = Cursors.Default; } catch { }
            try { _videoHost.Cursor = Cursors.Default; } catch { }
            try { _audioMetersHost.Cursor = Cursors.Default; } catch { }
            try { if (_overlayHost != null) { _overlayHost.Cursor = Cursors.Default; _overlayHost.Surface.Cursor = Cursors.Default; } } catch { }
            try { SetSystemCursorVisible(true); } catch { }
        }

        private sealed class VideoLoadingMask : Control
        {
            private string _message = global::CinecorePlayer2025.Utilities.AppLanguage.T("Caricamento…", "Loading…");

            public VideoLoadingMask()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw, true);

                BackColor = Color.Black;
                // Superficie esclusivamente visiva: non deve mai acquisire focus o
                // mouse capture mentre il renderer cambia finestra nativa.
                TabStop = false;
                Enabled = false;
                SetStyle(ControlStyles.Selectable, false);

            }

            // Caricamento con immagine: sfondo, titolo e avanzamento (vedi LoadingArt). Titolo vuoto = caricamento semplice.
            private string? _artPath;
            private string _artTitle = "";
            private double _artProgress;
            private Bitmap? _artBitmap;
            private string _artKey = "";
            private string _artPending = "";
            public void SetArt(string? imagePath, string title, double progress)
            {
                _artPath = imagePath; _artTitle = title ?? ""; _artProgress = progress;
                // L'immagine si prepara fuori dal thread dell'interfaccia (a 4K costa decine di millisecondi proprio
                // mentre parte il passaggio dalla libreria); finche' non e' pronta la schermata e' su nero.
                string key = LoadingArt.KeyFor(imagePath, ClientSize);
                if (_artTitle.Length > 0 && !string.IsNullOrEmpty(imagePath) && key != _artKey && key != _artPending && ClientSize.Width > 1 && ClientSize.Height > 1)
                {
                    _artPending = key;
                    Size size = ClientSize;
                    string? path = imagePath;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        Bitmap? ready = null;
                        try { ready = LoadingArt.Prepare(path, size); } catch { }
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (_artPending != key || IsDisposed) { ready?.Dispose(); return; }
                                try { _artBitmap?.Dispose(); } catch { }
                                _artBitmap = ready; _artKey = key; _artPending = "";
                                Invalidate();
                            }));
                        }
                        catch { ready?.Dispose(); }
                    });
                }
                Invalidate();
            }
            public void SetMessage(string message)
            {
                _message = string.IsNullOrWhiteSpace(message) ? global::CinecorePlayer2025.Utilities.AppLanguage.T("Caricamento…", "Loading…") : message;
                Invalidate();
            }

            // Per i primi istanti la maschera mostra cio' che c'era a schermo (la libreria): sopra, la
            // finestra di caricamento entra in dissolvenza. Senza, il nero compariva di colpo.
            private Bitmap? _backdrop;
            private long _backdropUntil;
            private System.Windows.Forms.Timer? _backdropTimer;

            public void SetBackdrop(Bitmap? frame, int holdMs)
            {
                try { _backdrop?.Dispose(); } catch { }
                _backdrop = frame;
                _backdropUntil = frame == null ? 0 : Environment.TickCount64 + holdMs;
                if (frame == null) return;
                _backdropTimer ??= new System.Windows.Forms.Timer();
                _backdropTimer.Interval = holdMs + 20;
                _backdropTimer.Tick -= BackdropExpired;
                _backdropTimer.Tick += BackdropExpired;
                _backdropTimer.Stop();
                _backdropTimer.Start();
            }

            private void BackdropExpired(object? sender, EventArgs e)
            {
                _backdropTimer?.Stop();
                try { _backdrop?.Dispose(); } catch { }
                _backdrop = null;
                if (!IsDisposed && Visible) Invalidate();
            }

            public void ApplyTheme()
            {
                BackColor = Color.Black;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                // Paint a complete still frame before DirectShow can block the UI.
                // The independent presenter animates above it while Cinecore is
                // foreground, but this fallback must not look like a black freeze.
                if (_backdrop != null && Environment.TickCount64 < _backdropUntil)
                {
                    try { e.Graphics.DrawImage(_backdrop, ClientRectangle); return; } catch { }
                }
                e.Graphics.Clear(Color.Black);
                int centerX = ClientSize.Width / 2;
                int centerY = ClientSize.Height / 2;
                if (LoadingArt.Paint(e.Graphics, ClientSize, ref _artBitmap, ref _artKey, _artPath, _artTitle, _message, _artProgress, buildIfMissing: false)) return;
                // Proporzionato alla finestra: a 4K i tre punti e la scritta restavano di 8 e 13 pixel.
                float k = Math.Max(1f, ClientSize.Height / 1080f) * 1.3f;
                int size = (int)Math.Round(8 * k), step = (int)Math.Round(19 * k);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var dot = new SolidBrush(Theme.Accent))
                {
                    for (int i = -1; i <= 1; i++)
                        e.Graphics.FillEllipse(dot, centerX + i * step - size / 2, centerY - size / 2, size, size);
                }
                using var font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
                TextRenderer.DrawText(e.Graphics, _message, font,
                    new Rectangle(24, centerY + (int)Math.Round(20 * k), Math.Max(1, ClientSize.Width - 48), (int)Math.Round(28 * k)),
                    Color.FromArgb(176, 190, 202),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

        }

        /// <summary>
        /// Presenta la maschera di caricamento su un message loop dedicato. La
        /// costruzione dei graph DirectShow e di alcuni renderer è sincrona: un
        /// normale Timer WinForms sul thread principale smette quindi di dipingere.
        /// Questa finestra resta animata, è completamente click-through e segue
        /// esclusivamente il client del PlayerForm (mai la barra del titolo).
        /// </summary>
        private sealed class ThreadedVideoLoadingPresenter : IDisposable
        {
            private readonly object _gate = new();
            private Thread? _thread;
            private LoadingWindow? _window;
            private bool _disposed;
            private IntPtr _requestedOwner;
            private string _requestedMessage = global::CinecorePlayer2025.Utilities.AppLanguage.T("Caricamento…", "Loading…");

            private string? _requestedArtPath;
            private string _requestedArtTitle = "";
            private double _requestedProgress;
            public void Show(IntPtr ownerHandle, string message, string? artPath = null, string artTitle = "", double progress = 0)
            {
                if (ownerHandle == IntPtr.Zero)
                    return;

                lock (_gate)
                {
                    if (_disposed) return;
                    _requestedOwner = ownerHandle;
                    _requestedMessage = message;
                    _requestedArtPath = artPath; _requestedArtTitle = artTitle ?? ""; _requestedProgress = progress;
                }
                EnsureThread();
                PostState();
            }

            public void Hide()
            {
                lock (_gate) _requestedOwner = IntPtr.Zero;
                PostState();
            }

            private void PostState()
            {
                LoadingWindow? window;
                lock (_gate) window = _window;
                if (window == null || window.IsDisposed) return;
                try { window.BeginInvoke(new Action(() => ApplyState(window))); } catch { }
            }

            private void ApplyState(LoadingWindow window)
            {
                IntPtr owner;
                string message;
                bool disposed;
                lock (_gate)
                {
                    owner = _requestedOwner;
                    message = _requestedMessage;
                    disposed = _disposed;
                }
                // Read current intent, never replay a stale Show queued before Hide.
                if (disposed)
                {
                    window.HidePresenter();
                    Application.ExitThread();
                }
                else if (owner == IntPtr.Zero) window.HidePresenter();
                else window.ShowFor(owner, message, _requestedArtPath, _requestedArtTitle, _requestedProgress);
            }

            private void EnsureThread()
            {
                lock (_gate)
                {
                    if (_disposed || _thread != null)
                        return;

                    _thread = new Thread(RunMessageLoop)
                    {
                        IsBackground = true,
                        Name = "Cinecore loading presenter"
                    };
                    _thread.SetApartmentState(ApartmentState.STA);
                    _thread.Start();
                }
            }

            private void RunMessageLoop()
            {
                try
                {
                    using var window = new LoadingWindow();
                    _ = window.Handle;
                    lock (_gate) _window = window;
                    PostState();
                    Application.Run();
                }
                catch (Exception ex) { Dbg.Warn("Loading presenter: " + ex.Message); }
                finally
                {
                    lock (_gate) _window = null;
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    _disposed = true;
                    _requestedOwner = IntPtr.Zero;
                }
                // Never join a thread whose native window can be messaging our UI.
                PostState();
            }

            private sealed class LoadingWindow : Form
            {
                private readonly System.Windows.Forms.Timer _animationTimer;
                private IntPtr _ownerHandle;
                private string _message = global::CinecorePlayer2025.Utilities.AppLanguage.T("Caricamento…", "Loading…");
                private int _angle;
                private bool _requestedVisible;
                // Dissolvenza: entra in 240 ms sopra la pagina, esce in 220 ms sul primo fotogramma del film.
                private long _fadeStarted;
                private double _fadeFrom, _fadeTo = 1;
                private const double FadeInMs = 240, FadeOutMs = 220;

                // Caricamento con immagine: la barra raggiunge con dolcezza il punto dichiarato e poi avanza
                // ancora un poco da sola, cosi' non sembra mai ferma mentre un passaggio lungo e' in corso.
                private string? _artPath;
                private string _artTitle = "";
                private double _targetProgress, _shownProgress, _creep;
                private bool _finishing;
                private int _fullTicks;
                private long _barClock, _fullSince;
                private Bitmap? _artBitmap;
                private string _artKey = "";
                private void StartFade(double to)
                {
                    double current = 1;
                    try { current = Visible ? Opacity : 0; } catch { }
                    _fadeFrom = current; _fadeTo = to;
                    _fadeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    _animationTimer.Interval = 15;
                }

                /// <summary>Avanza la dissolvenza; vero quando l'uscita e' finita e la finestra va nascosta.</summary>
                private bool StepFade()
                {
                    if (_fadeStarted == 0) return false;
                    double t = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_fadeStarted).TotalMilliseconds / (_fadeTo > 0 ? FadeInMs : FadeOutMs), 0, 1);
                    double eased = t * t * (3 - 2 * t);
                    try { Opacity = Math.Clamp(_fadeFrom + (_fadeTo - _fadeFrom) * eased, 0, 1); } catch { }
                    if (t < 1) return false;
                    _fadeStarted = 0;
                    _animationTimer.Interval = _artTitle.Length > 0 && _fadeTo > 0 ? 15 : 33;
                    return _fadeTo <= 0;
                }

                public LoadingWindow()
                {
                    FormBorderStyle = FormBorderStyle.None;
                    ShowInTaskbar = false;
                    StartPosition = FormStartPosition.Manual;
                    BackColor = Color.Black;
                    DoubleBuffered = true;
                    TabStop = false;

                    SetStyle(ControlStyles.AllPaintingInWmPaint |
                             ControlStyles.OptimizedDoubleBuffer |
                             ControlStyles.UserPaint |
                             ControlStyles.ResizeRedraw, true);

                    _animationTimer = new System.Windows.Forms.Timer { Interval = 33 };
                    _animationTimer.Tick += (_, __) =>
                    {
                        _angle = (_angle + 11) % 360;
                        if (_artTitle.Length > 0)
                        {
                            // Il movimento segue il tempo trascorso, non il numero di scatti del timer: gli scatti non
                            // arrivano a intervalli uguali e la barra avanzava a strappi.
                            long barNow = System.Diagnostics.Stopwatch.GetTimestamp();
                            double dt = _barClock == 0 ? .016 : Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_barClock, barNow).TotalSeconds, .001, .1);
                            _barClock = barNow;
                            if (_finishing || _targetProgress >= 1)
                            {
                                // Tutto pronto: la barra scorre fino in fondo in circa un terzo di secondo (il film parte
                                // solo dopo); alla chiusura resta piena un attimo e poi la schermata sfuma.
                                if (_shownProgress < 1) { _shownProgress = Math.Min(1, _shownProgress + Math.Max(.9 * dt, (1 - _shownProgress) * (1 - Math.Exp(-dt * 9)))); _fullSince = 0; }
                                else if (_finishing)
                                {
                                    if (_fullSince == 0) _fullSince = barNow;
                                    else if (System.Diagnostics.Stopwatch.GetElapsedTime(_fullSince, barNow).TotalMilliseconds >= 150) { _finishing = false; _fullSince = 0; StartFade(0); }
                                }
                            }
                            else
                            {
                                _creep = Math.Min(.16, _creep + .033 * dt);
                                _shownProgress += (Math.Min(.97, _targetProgress + _creep) - _shownProgress) * (1 - Math.Exp(-dt * 2.9));
                            }
                        }
                        if (StepFade()) { _animationTimer.Stop(); try { if (Visible) Hide(); } catch { } return; }
                        if (_requestedVisible) SyncToOwner();
                        if (Visible)
                            Invalidate(_artTitle.Length > 0
                                ? new Rectangle(0, ClientSize.Height * 80 / 100, ClientSize.Width, ClientSize.Height * 20 / 100)
                                : new Rectangle(ClientSize.Width / 2 - 36, ClientSize.Height / 2 - 36, 72, 72));
                    };

                }

                protected override bool ShowWithoutActivation => true;

                protected override CreateParams CreateParams
                {
                    get
                    {
                        const int WS_EX_TRANSPARENT = 0x00000020;
                        const int WS_EX_TOOLWINDOW = 0x00000080;
                        const int WS_EX_NOACTIVATE = 0x08000000;
                        var cp = base.CreateParams;
                        cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                        return cp;
                    }
                }

                public void ShowFor(IntPtr ownerHandle, string message, string? artPath = null, string artTitle = "", double progress = 0)
                {
                    if (ownerHandle == IntPtr.Zero || !IsWindow(ownerHandle))
                        return;

                    // Tie the independent loading HWND to the player so Windows
                    // keeps it on the player's virtual desktop. Its own STA loop
                    // still paints while renderer construction blocks the UI STA.
                    if (_ownerHandle != ownerHandle)
                    {
                        _ownerHandle = ownerHandle;
                        try { SetWindowLongPtr(Handle, GWLP_HWNDPARENT, ownerHandle); } catch { }
                    }
                    _message = string.IsNullOrWhiteSpace(message) ? global::CinecorePlayer2025.Utilities.AppLanguage.T("Caricamento…", "Loading…") : message;
                    // "fresh" = un caricamento nuovo. La finestra puo' anche essere solo nascosta per un attimo
                    // (Cinecore perde il primo piano mentre il renderer cambia modo video): in quel caso la barra
                    // riprende da dov'era, non riparte da zero.
                    bool fresh = !_requestedVisible;
                    bool appearing = fresh || !Visible;
                    bool otherTitle = _artTitle.Length > 0 && (artTitle ?? "").Length > 0 && !string.Equals(_artTitle, artTitle, StringComparison.Ordinal);
                    _finishing = false;
                    if (fresh || otherTitle && progress < _targetProgress) { _shownProgress = 0; _creep = 0; _targetProgress = 0; }
                    // Una richiesta senza titolo (per esempio dal cambio di schermo intero) non toglie immagine e barra.
                    if ((artTitle ?? "").Length == 0 && !fresh && _artTitle.Length > 0) { artTitle = _artTitle; artPath ??= _artPath; }
                    // La barra non torna mai indietro all'interno dello stesso caricamento.
                    progress = Math.Max(progress, _targetProgress);
                    if (progress > _targetProgress + .001 || fresh) _creep = 0;
                    _artPath = artPath; _artTitle = artTitle ?? ""; _targetProgress = progress;
                    _requestedVisible = true;
                    if (appearing) { try { Opacity = 0; } catch { } StartFade(1); }
                    if ((artTitle ?? "").Length > 0) _animationTimer.Interval = 15;
                    if (appearing) _barClock = 0;
                    _animationTimer.Start();
                    // Independent presenter: an HWND owner on the blocked renderer thread
                    // would postpone Show until graph construction has already finished.
                    SyncToOwner();
                    Invalidate();
                }

                public void HidePresenter()
                {
                    // Un secondo "nascondi" mentre la chiusura e' gia' in corso non la tronca: la barra finisce e la schermata sfuma.
                    if (!_requestedVisible && Visible && (_finishing || _fadeStarted != 0 && _fadeTo <= 0)) return;
                    bool wasShown = _requestedVisible && Visible;
                    _requestedVisible = false;
                    if (!wasShown) { _animationTimer.Stop(); try { if (Visible) Hide(); } catch { } return; }
                    // Non sparisce di colpo: con la barra, prima questa arriva in fondo; poi sfuma sul film e il timer la nasconde.
                    if (_artTitle.Length > 0) { _finishing = true; _fullSince = 0; _animationTimer.Interval = 15; }
                    else StartFade(0);
                    _animationTimer.Start();
                }

                private void SyncToOwner()
                {
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
                    if (!_requestedVisible || _ownerHandle == IntPtr.Zero ||
                        !IsWindow(_ownerHandle) || !IsWindowVisible(_ownerHandle) || IsIconic(_ownerHandle) ||
                        foregroundProcess != (uint)Environment.ProcessId)
                    {
                        try { if (Visible) Hide(); } catch { }
                        return;
                    }

                    try
                    {
                        if (!GetClientRect(_ownerHandle, out NativeRect client))
                            return;
                        var topLeft = new NativePoint { X = client.Left, Y = client.Top };
                        if (!ClientToScreen(_ownerHandle, ref topLeft))
                            return;

                        int width = Math.Max(1, client.Right - client.Left);
                        int height = Math.Max(1, client.Bottom - client.Top);
                        // Renderer child/top-level HWNDs can rise above the player while
                        // DirectShow builds its graph. Keep the independent loading
                        // window in front only while Cinecore owns the foreground.
                        PresentAt(new Rectangle(topLeft.X, topLeft.Y, width, height));
                    }
                    catch { }
                }

                private void PresentAt(Rectangle target)
                {
                    bool placementChanged = Bounds != target;
                    if (!Visible)
                    {
                        Bounds = target;
                        // Native SWP_SHOWWINDOW alone leaves Form.Visible false;
                        // HidePresenter would then skip the still-visible HWND.
                        Show();
                        placementChanged = true;
                    }
                    if (placementChanged)
                        SetWindowPos(Handle, HWND_TOPMOST, target.X, target.Y, target.Width, target.Height, SWP_NOACTIVATE);
                }

                protected override void WndProc(ref Message m)
                {
                    const int WM_NCHITTEST = 0x0084;
                    const int WM_MOUSEACTIVATE = 0x0021;
                    const int HTTRANSPARENT = -1;
                    const int MA_NOACTIVATE = 3;
                    if (m.Msg == WM_NCHITTEST)
                    {
                        m.Result = (IntPtr)HTTRANSPARENT;
                        return;
                    }
                    if (m.Msg == WM_MOUSEACTIVATE)
                    {
                        m.Result = (IntPtr)MA_NOACTIVATE;
                        return;
                    }
                    base.WndProc(ref m);
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    var g = e.Graphics;
                    g.Clear(Color.Black);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    int width = ClientSize.Width;
                    int height = ClientSize.Height;
                    if (width < 2 || height < 2)
                        return;

                    if (LoadingArt.Paint(g, ClientSize, ref _artBitmap, ref _artKey, _artPath, _artTitle, _message, _shownProgress)) return;
                    // Nessun tetto a 1,5: a 4K l'anello di caricamento restava minuscolo.
                    float scale = Math.Clamp(Math.Min(width / 1440f, height / 850f), .9f, 4f);
                    int size = (int)(32 * scale);
                    int centerX = width / 2;
                    int centerY = height / 2;
                    var arc = new Rectangle(centerX - size / 2, centerY - size / 2, size, size);
                    using (var pen = new Pen(Theme.Accent, Math.Max(3f, 4f * scale))
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    })
                    {
                        g.DrawArc(pen, arc, _angle, 290);
                    }

                    using var font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                    var textSize = TextRenderer.MeasureText(_message, font);
                    var textRect = new Rectangle(
                        centerX - textSize.Width / 2,
                        centerY + size / 2 + (int)(12 * scale),
                        textSize.Width,
                        textSize.Height);
                    TextRenderer.DrawText(g, _message, font, textRect,
                        Color.FromArgb(176, 190, 202),
                        TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
                }

                protected override void Dispose(bool disposing)
                {
                    if (disposing)
                    {
                        try { _animationTimer.Stop(); } catch { }
                        try { _animationTimer.Dispose(); } catch { }
                    }
                    base.Dispose(disposing);
                }

                [StructLayout(LayoutKind.Sequential)]
                private struct NativeRect { public int Left, Top, Right, Bottom; }

                [StructLayout(LayoutKind.Sequential)]
                private struct NativePoint { public int X, Y; }

                private static readonly IntPtr HWND_TOPMOST = new(-1);
                private const int GWLP_HWNDPARENT = -8;
                private const uint SWP_NOACTIVATE = 0x0010;

                [DllImport("user32.dll", SetLastError = true)]
                private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);
                [DllImport("user32.dll", SetLastError = true)]
                private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);
                [DllImport("user32.dll", SetLastError = true)]
                private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                    int x, int y, int cx, int cy, uint flags);
                [DllImport("user32.dll", SetLastError = true)]
                private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
                [DllImport("user32.dll")]
                private static extern bool IsWindow(IntPtr hWnd);
                [DllImport("user32.dll")]
                private static extern bool IsWindowVisible(IntPtr hWnd);
                [DllImport("user32.dll")]
                private static extern bool IsIconic(IntPtr hWnd);
            }
        }

        // Esegue chiamate su un thread STA con message-loop.
        // Serve per componenti browser/COM che possono richiedere STA (es. resolver YouTube).
        private sealed class StaInvoker : IDisposable
        {
            private readonly Thread _thread;
            private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Control? _invoker;

            public StaInvoker()
            {
                _thread = new Thread(ThreadMain)
                {
                    IsBackground = true,
                    Name = "Cinecore.STA.Invoker"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            private void ThreadMain()
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    _invoker = new Control();
                    _ = _invoker.Handle;
                    _ready.TrySetResult(true);
                    Application.Run();
                }
                catch (Exception ex)
                {
                    _ready.TrySetException(ex);
                }
            }

            public async Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct)
            {
                await _ready.Task.ConfigureAwait(false);

                var inv = _invoker;
                if (inv == null || inv.IsDisposed)
                    throw new ObjectDisposedException(nameof(StaInvoker));

                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

                void Run()
                {
                    try { tcs.TrySetResult(func()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }

                try { inv.BeginInvoke((Action)Run); }
                catch (Exception ex) { tcs.TrySetException(ex); }

                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }

            public void Dispose()
            {
                try
                {
                    var inv = _invoker;
                    if (inv != null && !inv.IsDisposed && inv.IsHandleCreated)
                    {
                        inv.BeginInvoke((Action)(() =>
                        {
                            try { inv.Dispose(); } catch { }
                            try { Application.ExitThread(); } catch { }
                        }));
                    }
                }
                catch { }
            }
        }

        // === Focus adorner (bordo visibile attorno al controllo a fuoco)
        private sealed class FocusAdorner : Control
        {
            // Un border overlay disegnato senza “coprire” il controllo sottostante.
            // Nota: usiamo una Region a “ciambella” per evitare il bug dove gli item
            // (carousel / griglia / menu sinistro) diventano neri quando evidenziati.
            private const int RingThickness = 3; // px

            private Control? _target;
            private Control? _host;
            private bool _repositionPending;
            private Region? _donutRegion;
            private readonly List<Control> _ancestors = new();

            public FocusAdorner()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;
                Margin = Padding.Empty;
                TabStop = false;
                Enabled = false;
                Visible = false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _donutRegion?.Dispose(); } catch { }
                    _donutRegion = null;
                }
                base.Dispose(disposing);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // Intenzionalmente vuoto: la Region “a ciambella” fa sì che
                // l’adorner NON copra l’interno del target.
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x84;
                const int HTTRANSPARENT = -1;
                if (m.Msg == WM_NCHITTEST)
                {
                    m.Result = (IntPtr)HTTRANSPARENT;
                    return;
                }
                base.WndProc(ref m);
            }

            private static bool IsBadHost(Control c)
                => c is FlowLayoutPanel
                   || c is TableLayoutPanel;

            private static Control? FindHost(Control target)
            {
                // Preferisci SEMPRE la Form che contiene il target: così l’adorner
                // non viene disposto insieme a pannelli transitori (es. Library chiusa).
                try
                {
                    var f = target.FindForm();
                    if (f != null && !f.IsDisposed)
                        return f;
                }
                catch { }

                // Fallback: risali al primo ancestor “neutro”.
                Control? p = target.Parent;
                while (p != null && IsBadHost(p))
                    p = p.Parent;

                return p ?? target.Parent;
            }

            public void Attach(Control? target)
            {
                if (IsDisposed) return;
                if (ReferenceEquals(_target, target)) return;

                Unwire();

                _target = target;
                _host = null;

                if (_target == null || _target.IsDisposed)
                {
                    HideAndClear();
                    return;
                }

                // NON mostrare il bordo sui bottoncini piccoli (es. frecce del carosello)
                if (_target is ButtonBase btn && btn.Width <= 40 && btn.Height <= 40)
                {
                    HideAndClear();
                    return;
                }

                var host = FindHost(_target);
                if (host == null || host.IsDisposed)
                {
                    HideAndClear();
                    return;
                }

                _host = host;

                if (Parent != _host)
                    _host.Controls.Add(this);

                Wire();

                Visible = true;
                Reposition();
            }

            private void HideAndClear()
            {
                var oldHost = _host;
                var oldBounds = Bounds;
                var oldTarget = _target;

                Visible = false;
                _target = null;
                _host = null;
                ClearRegion();

                // Evita artefatti: invalida in modo piu' mirato per non innescare repaint profondi
                // dell'intera UI, che visivamente fanno "spaghettificare" i pannelli durante i cambi focus.
                try
                {
                    if (oldTarget != null && !oldTarget.IsDisposed)
                        oldTarget.Invalidate();
                }
                catch { }

                try
                {
                    if (oldHost != null && !oldHost.IsDisposed && !oldBounds.IsEmpty)
                        oldHost.Invalidate(oldBounds, false);
                }
                catch { }
            }

            private void ClearRegion()
            {
                try { _donutRegion?.Dispose(); } catch { }
                _donutRegion = null;
                try { Region = null; } catch { }
            }

            private void Wire()
            {
                if (_target != null)
                {
                    _target.LocationChanged += TargetChanged;
                    _target.SizeChanged += TargetChanged;
                    _target.ParentChanged += TargetParentChanged;
                    _target.VisibleChanged += TargetChanged;
                    _target.HandleCreated += TargetChanged;
                    _target.HandleDestroyed += TargetChanged;
                    _target.Disposed += TargetDisposed;
                }

                if (_host != null)
                {
                    _host.LocationChanged += HostChanged;
                    _host.SizeChanged += HostChanged;
                    _host.ParentChanged += HostChanged;
                    _host.Layout += HostLayout;
                    if (_host is ScrollableControl sc)
                        sc.Scroll += HostScroll;
                }

                WireAncestors();
            }

            private void Unwire()
            {
                UnwireAncestors();

                if (_target != null)
                {
                    _target.LocationChanged -= TargetChanged;
                    _target.SizeChanged -= TargetChanged;
                    _target.ParentChanged -= TargetParentChanged;
                    _target.VisibleChanged -= TargetChanged;
                    _target.HandleCreated -= TargetChanged;
                    _target.HandleDestroyed -= TargetChanged;
                    _target.Disposed -= TargetDisposed;
                }

                if (_host != null)
                {
                    _host.LocationChanged -= HostChanged;
                    _host.SizeChanged -= HostChanged;
                    _host.ParentChanged -= HostChanged;
                    _host.Layout -= HostLayout;
                    if (_host is ScrollableControl sc)
                        sc.Scroll -= HostScroll;
                }
            }

            private void WireAncestors()
            {
                UnwireAncestors();

                if (_target == null || _target.IsDisposed) return;
                if (_host == null || _host.IsDisposed) return;

                // IMPORTANTISSIMO: quando un ancestor si sposta (es. CarouselViewport che scrolla
                // muovendo la FlowLayoutPanel), il target NON riceve LocationChanged, quindi
                // l'adorner rischia di rimanere "in aria" su un riquadro inesistente.
                try
                {
                    for (Control? p = _target.Parent; p != null && !ReferenceEquals(p, _host); p = p.Parent)
                    {
                        _ancestors.Add(p);
                        p.LocationChanged += AncestorChanged;
                        p.SizeChanged += AncestorChanged;
                        p.VisibleChanged += AncestorChanged;
                        p.Layout += AncestorLayout;

                        if (p is ScrollableControl sc)
                            sc.Scroll += AncestorScroll;
                    }
                }
                catch { }
            }

            private void UnwireAncestors()
            {
                if (_ancestors.Count == 0) return;
                foreach (var p in _ancestors)
                {
                    try
                    {
                        p.LocationChanged -= AncestorChanged;
                        p.SizeChanged -= AncestorChanged;
                        p.VisibleChanged -= AncestorChanged;
                        p.Layout -= AncestorLayout;

                        if (p is ScrollableControl sc)
                            sc.Scroll -= AncestorScroll;
                    }
                    catch { }
                }
                _ancestors.Clear();
            }

            private void AncestorChanged(object? sender, EventArgs e) => Reposition();
            private void AncestorLayout(object? sender, LayoutEventArgs e) => Reposition();
            private void AncestorScroll(object? sender, ScrollEventArgs e) => Reposition();

            private void TargetDisposed(object? sender, EventArgs e) => HideAndClear();

            private void TargetChanged(object? sender, EventArgs e) => Reposition();
            private void HostChanged(object? sender, EventArgs e) => Reposition();
            private void HostLayout(object? sender, LayoutEventArgs e) => Reposition();
            private void HostScroll(object? sender, ScrollEventArgs e) => Reposition();

            private void TargetParentChanged(object? sender, EventArgs e)
            {
                if (_target == null || _target.IsDisposed)
                {
                    HideAndClear();
                    return;
                }

                // chain cambiata → ri-aggancia gli ancestor
                UnwireAncestors();

                var newHost = FindHost(_target);
                if (newHost == null || newHost.IsDisposed)
                {
                    HideAndClear();
                    return;
                }

                if (!ReferenceEquals(_host, newHost))
                {
                    // sgancia eventi host precedente
                    if (_host != null)
                    {
                        _host.LocationChanged -= HostChanged;
                        _host.SizeChanged -= HostChanged;
                        _host.ParentChanged -= HostChanged;
                        _host.Layout -= HostLayout;
                        if (_host is ScrollableControl sc)
                            sc.Scroll -= HostScroll;
                    }

                    _host = newHost;
                    if (Parent != _host)
                        _host.Controls.Add(this);

                    _host.LocationChanged += HostChanged;
                    _host.SizeChanged += HostChanged;
                    _host.ParentChanged += HostChanged;
                    _host.Layout += HostLayout;
                    if (_host is ScrollableControl sc2)
                        sc2.Scroll += HostScroll;
                }

                Reposition();

                WireAncestors();
            }

            private void ScheduleReposition()
            {
                if (_repositionPending) return;
                if (_target == null || _target.IsDisposed) return;

                _repositionPending = true;
                try
                {
                    _target.BeginInvoke(new Action(() =>
                    {
                        _repositionPending = false;
                        Reposition();
                    }));
                }
                catch
                {
                    _repositionPending = false;
                }
            }

            private void Reposition()
            {
                if (_target == null || _target.IsDisposed || _host == null || _host.IsDisposed)
                {
                    Visible = false;
                    return;
                }

                // Se il target (o un suo parent) è invisibile: nascondi.
                if (!_target.Visible)
                {
                    Visible = false;
                    return;
                }

                // Su prime aperture può arrivare un Attach prima della creazione dell’handle.
                if (!_target.IsHandleCreated || !_host.IsHandleCreated)
                {
                    Visible = false;
                    ScheduleReposition();
                    return;
                }

                try
                {
                    var rc = _target.RectangleToScreen(_target.ClientRectangle);
                    var tl = _host.PointToClient(new Point(rc.Left, rc.Top));

                    if (Parent != _host)
                        _host.Controls.Add(this);

                    var old = Bounds;

                    Bounds = new Rectangle(tl, rc.Size);
                    UpdateDonutRegion();

                    Visible = true;

                    try
                    {
                        if (_target != null && !_target.IsDisposed)
                            _target.Invalidate();

                        // invalidazioni piu' leggere: il controllo target si ridisegna da solo,
                        // evitiamo di forzare repaint profondi dell'intero form ad ogni spostamento del focus.
                        if (!old.IsEmpty) _host.Invalidate(old, false);
                        _host.Invalidate(Bounds, false);
                    }
                    catch { }

                    BringToFront();
                    Invalidate();
                }
                catch
                {
                    Visible = false;
                }
            }

            private void UpdateDonutRegion()
            {
                int t = RingThickness;
                if (Width <= t * 2 || Height <= t * 2)
                {
                    ClearRegion();
                    return;
                }

                try
                {
                    var old = _donutRegion;

                    using var gp = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
                    gp.AddRectangle(new Rectangle(0, 0, Width, Height));
                    gp.AddRectangle(new Rectangle(t, t, Width - 2 * t, Height - 2 * t));

                    _donutRegion = new Region(gp);
                    Region = _donutRegion;

                    try { old?.Dispose(); } catch { }
                }
                catch
                {
                    // Meglio nessuna region che coprire tutto il target.
                    ClearRegion();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var p = new Pen(Color.FromArgb(220, 0, 140, 255), 2f);

                // disegna sul bordo esterno (dentro Bounds)
                var r = new Rectangle(1, 1, Width - 3, Height - 3);
                g.DrawRectangle(p, r);
            }
        }

        // === Remote OSD (indicatori centrali mostrati SOLO per input dal remote server) ===
        private sealed class RemoteOsdOverlay : Control
        {
            internal bool RequestedVisible { get; private set; }
            protected override void SetVisibleCore(bool value) { RequestedVisible = value; base.SetVisibleCore(value); }

            private const int MinShowMs = 250;

            private string? _svgPath;
            private float? _bar01;
            private string? _text;

            private DateTime _showUntil = DateTime.MinValue;
            private DateTime _fadeAt = DateTime.MinValue;
            private float _opacity = 0f;

            private readonly System.Windows.Forms.Timer _timer;

            private readonly Dictionary<(string path, int sizePx, int argb), Bitmap> _svgCache =
                new();

            public RemoteOsdOverlay()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;
                TabStop = false;
                Enabled = false;
                Visible = false;

                _timer = new System.Windows.Forms.Timer { Interval = 30 };
                _timer.Tick += (_, __) => Tick();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _timer.Stop(); } catch { }
                    try { _timer.Dispose(); } catch { }

                    foreach (var kv in _svgCache)
                    {
                        try { kv.Value?.Dispose(); } catch { }
                    }
                    _svgCache.Clear();
                }
                base.Dispose(disposing);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // Stesso trucco dell'HUD: pulisci con la TransparencyKey del form host.
                var host = FindForm();
                if (host != null && host.TransparencyKey != Color.Empty)
                {
                    e.Graphics.Clear(host.TransparencyKey);
                    return;
                }

                if (BackColor != Color.Transparent)
                    base.OnPaintBackground(e);
            }

            public void Show(string? svgPath, float? bar01 = null, int ms = 900, string? text = null)
            {
                if (IsDisposed) return;

                if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                    svgPath = null;

                _svgPath = svgPath;
                _text = text;

                if (string.IsNullOrWhiteSpace(_svgPath) && bar01 == null && string.IsNullOrWhiteSpace(_text))
                    return;
                _bar01 = bar01.HasValue ? Math.Clamp(bar01.Value, 0f, 1f) : (float?)null;

                var now = DateTime.UtcNow;
                int total = Math.Max(MinShowMs, ms);

                // fade finale (max 250ms, ~1/3 del totale)
                int fadeMs = Math.Min(250, Math.Max(120, total / 3));
                _showUntil = now.AddMilliseconds(total);
                _fadeAt = now.AddMilliseconds(total - fadeMs);

                _opacity = 1f;

                Visible = true;
                BringToFront();
                Invalidate();

                if (!_timer.Enabled) _timer.Start();
            }

            private void Tick()
            {
                var now = DateTime.UtcNow;

                if (now >= _showUntil)
                {
                    Visible = false;
                    _opacity = 0f;
                    try { _timer.Stop(); } catch { }
                    Invalidate();
                    return;
                }

                if (_fadeAt != DateTime.MinValue && now >= _fadeAt)
                {
                    double t = (now - _fadeAt).TotalMilliseconds / Math.Max(1, (_showUntil - _fadeAt).TotalMilliseconds);
                    _opacity = (float)(1.0 - Math.Clamp(t, 0, 1));
                    Invalidate();
                }
                else
                {
                    if (_opacity != 1f)
                    {
                        _opacity = 1f;
                        Invalidate();
                    }
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                if (!Visible || _opacity <= 0.01f) return;
                if (Width <= 0 || Height <= 0) return;

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // Una pillola compatta, proporzionata allo schermo (prima era un riquadro al centro con misure fisse,
                // minuscolo a 4K): icona, barra sottile e valore sulla stessa riga; in alto se c'e' una barra o un
                // testo, al centro per la sola icona (play, pausa).
                bool hasBar = _bar01.HasValue;
                bool hasText = !string.IsNullOrWhiteSpace(_text);
                bool hasIcon = !string.IsNullOrWhiteSpace(_svgPath) && File.Exists(_svgPath);
                float k = Math.Clamp(Height / 1080f, .85f, 3.2f);
                int alpha(int value) => (int)(value * _opacity);
                using var valueFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 17f * k, FontStyle.Regular, GraphicsUnit.Pixel);
                string value = hasText ? _text! : hasBar ? Math.Round((_bar01 ?? 0f) * 100) + "%" : "";
                int pad = (int)(22 * k), gap = (int)(16 * k), icon = hasIcon ? (int)(30 * k) : 0;
                int valueW = value.Length > 0 ? TextRenderer.MeasureText(g, value, valueFont, Size.Empty, TextFormatFlags.NoPadding).Width + (int)(4 * k) : 0;

                Rectangle pill;
                if (!hasBar && !hasText)
                {
                    int side = (int)(96 * k);
                    pill = new Rectangle((Width - side) / 2, (Height - side) / 2, side, side);
                    icon = (int)(46 * k);
                }
                else
                {
                    int height = (int)(58 * k);
                    int barW = hasBar ? (int)(260 * k) : 0;
                    int width = pad + (hasIcon ? icon + gap : 0) + (hasBar ? barW + gap : 0) + valueW + pad;
                    width = Math.Min(width, Math.Max(height, Width - (int)(80 * k)));
                    pill = new Rectangle((Width - width) / 2, (int)(Height * .075f), width, height);
                }

                using (var shape = RoundRect(pill, pill.Height / 2))
                using (var back = new SolidBrush(Color.FromArgb(alpha(196), 12, 14, 18)))
                using (var edge = new Pen(Color.FromArgb(alpha(46), 255, 255, 255), Math.Max(1f, k)))
                {
                    g.FillPath(back, shape);
                    g.DrawPath(edge, shape);
                }

                if (!hasBar && !hasText)
                {
                    if (hasIcon)
                        try { using var bmp = GetSvgBitmap(_svgPath!, icon, Color.White); DrawImageAlpha(g, bmp, new Rectangle(pill.X + (pill.Width - icon) / 2, pill.Y + (pill.Height - icon) / 2, icon, icon), _opacity); } catch { }
                    return;
                }

                int x = pill.Left + pad, centre = pill.Top + pill.Height / 2;
                if (hasIcon)
                {
                    try { using var bmp = GetSvgBitmap(_svgPath!, icon, Color.White); DrawImageAlpha(g, bmp, new Rectangle(x, centre - icon / 2, icon, icon), _opacity); } catch { }
                    x += icon + gap;
                }
                int valueLeft = pill.Right - pad - valueW;
                if (hasBar)
                {
                    int trackH = Math.Max(4, (int)(5 * k));
                    var track = new Rectangle(x, centre - trackH / 2, Math.Max(8, valueLeft - gap - x), trackH);
                    using (var shape = RoundRect(track, trackH / 2))
                    using (var brush = new SolidBrush(Color.FromArgb(alpha(64), 255, 255, 255)))
                        g.FillPath(brush, shape);
                    int fillW = (int)Math.Round(track.Width * (_bar01 ?? 0f));
                    if (fillW >= trackH)
                    {
                        using var shape = RoundRect(new Rectangle(track.X, track.Y, fillW, trackH), trackH / 2);
                        using var brush = new SolidBrush(Color.FromArgb(alpha(255), Theme.Accent));
                        g.FillPath(brush, shape);
                    }
                }
                if (value.Length > 0)
                {
                    var area = hasBar ? new Rectangle(valueLeft, pill.Top, valueW, pill.Height) : new Rectangle(x, pill.Top, Math.Max(1, pill.Right - pad - x), pill.Height);
                    TextRenderer.DrawText(g, value, valueFont, area, Color.FromArgb(alpha(245), 255, 255, 255),
                        (hasBar ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }

            private static void DrawImageAlpha(Graphics g, Image img, Rectangle dest, float opacity)
            {
                opacity = Math.Clamp(opacity, 0f, 1f);

                if (opacity >= 0.995f)
                {
                    g.DrawImage(img, dest);
                    return;
                }

                using var ia = new ImageAttributes();
                var cm = new ColorMatrix
                {
                    Matrix00 = 1f,
                    Matrix11 = 1f,
                    Matrix22 = 1f,
                    Matrix33 = opacity,
                    Matrix44 = 1f
                };
                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(img, dest, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }

            private static GraphicsPath RoundRect(Rectangle r, int radius)
            {
                int rr = Math.Max(2, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
                int d = rr * 2;

                var gp = new GraphicsPath();
                gp.AddArc(r.Left, r.Top, d, d, 180, 90);
                gp.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                gp.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                gp.CloseFigure();
                return gp;
            }

            private Bitmap GetSvgBitmap(string svgPath, int sizePx, Color tint)
            {
                int argb = tint.ToArgb();

                if (_svgCache.TryGetValue((svgPath, sizePx, argb), out var cached) && cached != null)
                    return (Bitmap)cached.Clone();

                Bitmap rendered = RenderSvgSkia(svgPath, sizePx, tint);

                try
                {
                    if (_svgCache.TryGetValue((svgPath, sizePx, argb), out var old) && old != null)
                        old.Dispose();
                    _svgCache[(svgPath, sizePx, argb)] = (Bitmap)rendered.Clone();
                }
                catch { }

                return rendered;
            }

            private static Bitmap RenderSvgSkia(string svgPath, int targetPx, Color tint)
            {
                var svg = new SKSvg();
                svg.Load(svgPath);
                if (svg.Picture == null) throw new InvalidOperationException("SVG Picture null: " + svgPath);

                var bounds = svg.Picture.CullRect;
                float srcW = bounds.Width;
                float srcH = bounds.Height;
                if (srcW <= 0 || srcH <= 0) throw new InvalidOperationException("SVG bounds invalid: " + svgPath);

                float scale = targetPx / Math.Max(srcW, srcH);
                int outW = Math.Max(1, (int)Math.Round(srcW * scale));
                int outH = Math.Max(1, (int)Math.Round(srcH * scale));

                using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(scale);

                using var paint = new SKPaint
                {
                    ColorFilter = SKColorFilter.CreateBlendMode(
                        new SKColor(tint.R, tint.G, tint.B, 255),
                        SKBlendMode.SrcIn)
                };
                canvas.SaveLayer(paint);
                canvas.DrawPicture(svg.Picture);
                canvas.Restore();
                canvas.Flush();

                using var img = surface.Snapshot();
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var ms = new MemoryStream(data.ToArray());
                using var tmp = Image.FromStream(ms);
                return new Bitmap(tmp);
            }
        }


    }
}
