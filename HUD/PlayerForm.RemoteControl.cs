#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private readonly FocusAdorner _focusRing = new FocusAdorner();

        // helper per allargare rettangolo 
        private Control? _dpadRoot; // radice attuale per la navigazione DPAD
        private Control? _focused;

        private static bool IsDescendant(Control root, Control c)
        {
            for (var p = c; p != null; p = p.Parent)
                if (ReferenceEquals(p, root)) return true;
            return false;
        }

        private static bool IsCursorOnlyDpadContainer(Control c)
        {
            if (c == null)
                return false;

            // I contenitori generici con Cursor.Hand finiscono spesso per occupare tutto il canvas
            // e producono una focus ring enorme al primo input tastiera.
            // Se non sono selezionabili esplicitamente (TabStop) li trattiamo come layout, non come target DPAD.
            return c is Panel
                || c is UserControl
                || c is FlowLayoutPanel
                || c is TableLayoutPanel;
        }

        private static bool LooksLikeOversizedCursorOnlySurface(Control c)
        {
            try
            {
                if (c == null || c.Parent == null)
                    return false;

                if (c.TabStop || c is ButtonBase || c is CheckBox || c is ComboBox)
                    return false;

                int parentW = Math.Max(1, c.Parent.ClientSize.Width);
                int parentH = Math.Max(1, c.Parent.ClientSize.Height);
                if (parentW <= 1 || parentH <= 1 || c.Width <= 0 || c.Height <= 0)
                    return false;

                double coverage = (double)(c.Width * c.Height) / (double)(parentW * parentH);
                return coverage >= 0.50d;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDpadFocusable(Control c)
        {
            if (c == null) return false;
            if (!c.Visible || !c.Enabled) return false;

            // Explicit opt-out for DPAD navigation (e.g. pairing banner, decorative panels)
            try
            {
                if (c.Tag is string tag && string.Equals(tag, "nodpad", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch { }

            // evita roba "tecnica" che spacca il movimento (scrollbar, picturebox interne, adorners)
            if (c is FocusAdorner) return false;
            if (c is PictureBox) return false;

            string tn = c.GetType().Name;
            if (string.Equals(tn, "ThemedVScroll", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(tn, "SkinnedFlow", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(tn, "LoadingMask", StringComparison.OrdinalIgnoreCase)) return false;

            if (c.TabStop || c is ButtonBase || c is CheckBox || c is ComboBox)
                return true;

            if (c.Cursor == Cursors.Hand)
                return !IsCursorOnlyDpadContainer(c) && !LooksLikeOversizedCursorOnlySurface(c);

            return false;
        }

        private Control ResolveDpadRoot()
        {
            if (_cinematicLibraryPage?.Visible == true)
            {
                try { return _cinematicLibraryPage.GetRemoteFocusRoot(); }
                catch { return _cinematicLibraryPage; }
            }

            return this;
        }

        private bool IsAnyLibraryVisible()
        {
            return _cinematicLibraryPage != null && _cinematicLibraryPage.Visible;
        }

        private void EnsureDpadRoot()
        {
            var root = ResolveDpadRoot();
            if (!ReferenceEquals(root, _dpadRoot))
            {
                RemoteAttachRoot(root, forceReset: true);
            }
        }

        private void ResetLibraryRemoteActivation(bool clearFocusRing)
        {
            if (!clearFocusRing)
            {
                try
                {
                    if (_focused != null && (_focused.IsDisposed || (_cinematicLibraryPage != null && IsDescendant(_cinematicLibraryPage, _focused) && !_focused.Visible)))
                    {
                        _focused = null;
                        try { _focusRing.Attach(null); } catch { }
                    }
                }
                catch
                {
                    _focused = null;
                    try { _focusRing.Attach(null); } catch { }
                }
                return;
            }

            try
            {
                if (_focused != null && _cinematicLibraryPage != null && !_focused.IsDisposed && IsDescendant(_cinematicLibraryPage, _focused))
                    _focused = null;
            }
            catch { _focused = null; }

            try { _focusRing.Attach(null); } catch { }
        }

        private bool ConsumeLibraryActivationInputIfNeeded()
        {
            return false;
        }

        private void RemoteAttachRoot(Control root, bool forceReset = false)
        {
            if (root == null) return;

            bool keep = !forceReset
                        && ReferenceEquals(_dpadRoot, root)
                        && _focused != null
                        && !_focused.IsDisposed
                        && IsDescendant(root, _focused)
                        && IsDpadFocusable(_focused);

            _dpadRoot = root;

            if (!keep)
            {
                if (root is CinematicMediaLibraryPage cinematicLib)
                    _focused = cinematicLib.GetRemoteDefaultFocusTarget() ?? FindFirstFocusable(root) ?? root;
                else
                    _focused = FindFirstFocusable(root);
            }

            bool shouldAttach = root is not CinematicMediaLibraryPage;

            if (_focused != null && shouldAttach)
            {
                try { _focused.Focus(); } catch { }
                EnsureDpadVisible(_focused);
                _focusRing.Attach(_focused);
            }
            else
            {
                try { _focusRing.Attach(null); } catch { }
            }

            if (!shouldAttach)
                return;

            try
            {
                var f0 = _focused;
                BeginInvoke(new Action(() =>
                {
                    if (f0 == null || f0.IsDisposed) return;
                    if (!ReferenceEquals(_focused, f0)) return;
                    EnsureDpadVisible(f0);
                    _focusRing.Attach(f0);
                }));
            }
            catch { }
        }

        private void EnsureDpadVisible(Control target)
        {
            if (target == null || target.IsDisposed) return;
            if (!target.Visible || target.Width <= 0 || target.Height <= 0) return;
            try
            {
                if (target.FindForm() != this)
                    return;
            }
            catch { return; }

            bool handledByCustomScroll = false;

            // 1) Se siamo dentro un carosello con scroll manuale o una griglia custom,
            // chiedi al viewport di portarlo in vista. Se il parent gestisce già lo scroll,
            // NON chiamare anche ScrollControlIntoView perché produce salti/jitter.
            try
            {
                for (Control? p = target.Parent; p != null; p = p.Parent)
                {
                    var mi = p.GetType().GetMethod(
                        "EnsureChildVisible",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic,
                        binder: null,
                        types: new[] { typeof(Control) },
                        modifiers: null);

                    if (mi != null)
                    {
                        mi.Invoke(p, new object[] { target });
                        handledByCustomScroll = true;
                        break;
                    }
                }
            }
            catch { }

            if (handledByCustomScroll)
                return;

            // 2) AutoScroll containers: scrolla per far apparire il controllo
            try
            {
                for (Control? p = target.Parent; p != null; p = p.Parent)
                {
                    if (p is ScrollableControl sc && sc.AutoScroll)
                    {
                        var toShow = target;
                        while (toShow.Parent != null && toShow.Parent != sc) toShow = toShow.Parent;
                        sc.ScrollControlIntoView(toShow);
                        break;
                    }
                }
            }
            catch { }
        }

        private void RemoteMove(string dir)
        {
            EnsureDpadRoot();
            if (_dpadRoot == null) return;

            if (ConsumeLibraryActivationInputIfNeeded())
                return;

            if (_dpadRoot is CinematicMediaLibraryPage cinematicLib)
            {
                try { cinematicLib.SetDpadInputIsRemote(_lastDpadFromRemote); } catch { }

                var curLib = cinematicLib.CoerceRemoteFocus(_focused);
                if (curLib == null) return;

                if (cinematicLib.TryRemoteMove(curLib, dir, out var nextLib) && nextLib != null)
                {
                    var focusTarget = GetDpadFocusableAncestor(nextLib) ?? nextLib;
                    if (focusTarget == null || focusTarget.IsDisposed)
                        return;
                    _focused = focusTarget;
                    try { focusTarget.Focus(); } catch { }
                    EnsureDpadVisible(focusTarget);
                    _focusRing.Attach(null);
                }
                return;
            }

            var cur = (_focused != null && !_focused.IsDisposed && IsDescendant(_dpadRoot, _focused))
                ? _focused
                : FindFirstFocusable(_dpadRoot);

            if (cur == null) return;

            var next = FindNextByDirection(_dpadRoot, cur, dir);
            if (next != null)
            {
                if (ReferenceEquals(_focused, next))
                    return;
                _focused = next;
                try { next.Focus(); } catch { }
                EnsureDpadVisible(next);
                _focusRing.Attach(next);
            }
        }

        private void RemoteOk()
        {
            EnsureDpadRoot();
            if (ConsumeLibraryActivationInputIfNeeded())
                return;

            Control? t = _focused;
            if (_dpadRoot is CinematicMediaLibraryPage cinematicRoot)
            {
                try
                {
                    t = cinematicRoot.CoerceRemoteFocus(_focused);
                }
                catch
                {
                    t = _focused;
                }

                t = GetDpadFocusableAncestor(t) ?? t;
                if (t != null && !t.IsDisposed && !ReferenceEquals(_focused, t))
                {
                    _focused = t;
                    try { t.Focus(); } catch { }
                    EnsureDpadVisible(t);
                    try { _focusRing.Attach(null); } catch { }
                }
            }

            if (t == null || t.IsDisposed) return;

            // Propagate the latest DPAD input source to the library page (used e.g. to gate OSK).
            try
            {
                if (_dpadRoot is CinematicMediaLibraryPage cinematic0)
                    cinematic0.SetDpadInputIsRemote(_lastDpadFromRemote);
            }
            catch { }

            // CheckBox → toggle
            if (t is CheckBox cb)
            {
                cb.Checked = !cb.Checked;
                return;
            }

            // ComboBox → apri tendina
            if (t is ComboBox cmb)
            {
                try { cmb.DroppedDown = true; } catch { }
                return;
            }

            // Cinematic library: OK sul search (o componenti speciali) può essere gestito senza "click"
            try
            {

                if (_dpadRoot is CinematicMediaLibraryPage cinematic0 && cinematic0.TryRemoteOk(t, out var nextOkCinematic) && nextOkCinematic != null)
                {
                    var focusTarget = GetDpadFocusableAncestor(nextOkCinematic) ?? nextOkCinematic;
                    _focused = focusTarget;
                    try { focusTarget.Focus(); } catch { }
                    EnsureDpadVisible(focusTarget);
                    _focusRing.Attach(null);
                    return;
                }
            }
            catch { }

            // Qualsiasi altro controllo (inclusi i tuoi bottoni custom): invoca il Click protetto via reflection
            try
            {
                var mi = t.GetType().GetMethod(
                    "OnClick",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic
                );
                mi?.Invoke(t, new object[] { EventArgs.Empty });
            }
            catch { }

            // Cinematic library: dopo un click nel menu sinistro porta il focus ai contenuti (carosello/griglia)
            try
            {
                EnsureDpadRoot();
                if (_dpadRoot is CinematicMediaLibraryPage cinematicPre)
                {
                    try { cinematicPre.SetDpadInputIsRemote(_lastDpadFromRemote); } catch { }
                }
                if (_dpadRoot is CinematicMediaLibraryPage cinematic && cinematic.TryRemotePostOkFocus(t, out var postCinematic) && postCinematic != null)
                {
                    var focusTarget = GetDpadFocusableAncestor(postCinematic) ?? postCinematic;
                    _focused = focusTarget;
                    try { focusTarget.Focus(); } catch { }
                    EnsureDpadVisible(focusTarget);
                    _focusRing.Attach(null);
                }
            }
            catch { }
        }

        // Trova un primo controllo "sensato"
        private static Control? FindFirstFocusable(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (!IsDpadFocusable(c))
                {
                    var deep0 = FindFirstFocusable(c);
                    if (deep0 != null) return deep0;
                    continue;
                }

                return c;
            }
            return null;
        }

        // Heuristica DPAD: scegli il controllo più vicino nella direzione
        private static Control? FindNextByDirection(Control root, Control from, string dir)
        {
            var list = new List<Control>();

            void Collect(Control k)
            {
                foreach (Control c in k.Controls)
                {
                    if (!IsDpadFocusable(c))
                    {
                        Collect(c);
                        continue;
                    }

                    if (!ReferenceEquals(c, from))
                        list.Add(c);

                    Collect(c);
                }
            }

            Collect(root);

            if (list.Count == 0) return null;

            var src = from.RectangleToScreen(from.ClientRectangle);
            var sc = new Point(src.Left + src.Width / 2, src.Top + src.Height / 2);

            static bool Vertical(string d) => d == "up" || d == "down";
            static int Sign(string d) => (d == "right" || d == "down") ? +1 : -1;

            Control? best = null;
            double bestScore = double.MaxValue;

            foreach (var c in list)
            {
                var rc = c.RectangleToScreen(c.ClientRectangle);
                var cc = new Point(rc.Left + rc.Width / 2, rc.Top + rc.Height / 2);

                var dx = cc.X - sc.X;
                var dy = cc.Y - sc.Y;

                // vincolo direzione
                if (Vertical(dir))
                {
                    if (Sign(dir) * dy <= 0) continue; // deve stare "sopra" o "sotto"
                }
                else
                {
                    if (Sign(dir) * dx <= 0) continue; // deve stare "a sx" o "a dx"
                }

                // penalizza l’angolo
                double primary = Vertical(dir) ? Math.Abs(dy) : Math.Abs(dx);
                double secondary = Vertical(dir) ? Math.Abs(dx) : Math.Abs(dy);
                double score = primary * 1.0 + secondary * 0.6;

                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        // =========================
        // DPAD routing (Modal UI / HUD)
        // =========================
        private bool HandleDpadMove(string dir)
        {
            if (_netflixModePage?.Visible == true)
            {
                // Spotlight is driven from the sofa: all four directions navigate it.
                _netflixModePage.RemoteMove(dir);
                return true;
            }


            // UI navigabile (Libreria)
            if (IsAnyLibraryVisible())
            {
                RemoteMove(dir);
                return true;
            }

            return false;
        }

        private bool HandleDpadOk()
        {
            if (_netflixModePage?.Visible == true)
            {
                _netflixModePage.RemoteOk();
                return true;
            }


            // UI navigabile (Libreria)
            if (IsAnyLibraryVisible())
            {
                RemoteOk();
                return true;
            }

            // Playback: OK mostra solo HUD (niente selezione DPAD)
            if (_hud != null)
            {
                try { _hud.TimelineVisible = _duration > 0; } catch { }
                if (!_hud.Visible) _hud.Visible = true;
                try { if (_hud.DpadMode) _hud.DpadDeactivate(); } catch { }
                _hud.ShowOnce(2200);
                try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
                return true;
            }

            return false;
        }

        private bool HandleDpadBack()
        {
            if (_netflixModePage?.Visible == true)
            {
                if (!_netflixModePage.RemoteBack())
                    HideNetflixMode(showHome: true);
                return true;
            }

            // HUD: esci dalla modalità DPAD
            if (_hud?.DpadMode == true)
            {
                _hud.DpadDeactivate();
                return true;
            }

            // Overlay informativi: chiudili prima di uscire dal playback
            if (_infoOverlay?.Visible == true)
            {
                _infoOverlay.Visible = false;
                BringOverlaysToFront();
                return true;
            }

            // Libreria

            if (_cinematicLibraryPage?.Visible == true)
            {
                try
                {
                    try { _cinematicLibraryPage.SetDpadInputIsRemote(_lastDpadFromRemote); } catch { }

                    if (_cinematicLibraryPage.TryRemoteBack(_focused, out var next) && next != null)
                    {
                        _dpadRoot = ResolveDpadRoot();
                        _focused = next;
                        try { next.Focus(); } catch { }
                        EnsureDpadVisible(next);
                        _focusRing.Attach(next);
                        return true;
                    }
                }
                catch { }

                if (_engine == null && string.IsNullOrEmpty(_currentPath))
                    ShowDefaultLibrary(stopCurrent: false);
                else
                    HideCinematicLibrary();
                return true;
            }

            // Playback → libreria
            if (_engine != null)
            {
                CloseCurrentToLibrary();
                return true;
            }

            return false;
        }

        private void EnsureActive()
        {
            // Never take foreground ownership back from another application. Controls
            // receive focus naturally when the user clicks the player or uses its UI.
        }

        // =========================
        // Remote server → OSD centrale (solo durante la riproduzione)
        // =========================
        private bool CanShowRemotePlaybackOsd()
        {
            if (_remoteOsd == null || _remoteOsd.IsDisposed) return false;
            if (_engine == null) return false;

            // SOLO durante la riproduzione (non nel menu/modal)
            if (IsAnyLibraryVisible()) return false;

            // La richiesta era specifica per la riproduzione (HUD classica, non modalità foto)
            if (IsPhotoMode) return false;

            return true;
        }

        private void SuppressHudWakeForRemoteOsd(int ms)
        {
            try
            {
                var until = DateTime.UtcNow.AddMilliseconds(Math.Max(700, ms + 250));
                if (until > _suppressHudWakeUntilUtc)
                    _suppressHudWakeUntilUtc = until;

                _scrubActive = false;

                var old = Interlocked.Exchange(ref _thumbCts, null);
                try { old?.Cancel(); } catch { }
                try { old?.Dispose(); } catch { }

                if (_hud != null)
                {
                    if (ShouldPinAudioOnlyHud())
                    {
                        try { EnsureAudioOnlyHudPinned(); } catch { }
                    }
                    else
                    {
                        try { _hud.Visible = false; } catch { }
                        try { _hud.TimelineVisible = false; } catch { }
                    }
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

        private void ShowRemoteOsd(string? svgPath, float? bar01 = null, int ms = 900, string? text = null)
        {
            if (!CanShowRemotePlaybackOsd()) return;

            SuppressHudWakeForRemoteOsd(ms);

            try { _remoteOsd.Show(svgPath, bar01, ms, text); } catch { }
            try { SafeShowOverlayHost(); } catch { }
            try { SyncOverlayToVideoRect(); } catch { }
            try { UpdateMpcvrOverlayRegionMode(); } catch { }
            BringOverlaysToFront();
            try { UpdateMpcvrMixerOverlayMirror(force: true); } catch { }
        }

        private string? GetVolumeOsdSvg(float vol01, bool muted)
        {
            if (_hud == null) return null;

            if (muted) return _hud.SvgPathVolMute;
            if (vol01 <= 0.001f) return _hud.SvgPathVolZero;
            if (vol01 < 0.35f) return _hud.SvgPathVolLow;
            return _hud.SvgPathVolHigh;
        }

        // osd: l'overlay centrale e' la risposta al telecomando; la barra musica (mouse) non lo usa.
        private void RemoteSetVolume(float vol01, bool osd = true)
        {
            if (TryNetworkVolume(value: vol01, osd: osd)) return;
            if (IsBitstream()) return;
            if (_hud == null) return;

            vol01 = Math.Clamp(vol01, 0f, 1f);

            // Unmute se serve
            if (_hud.IsMuted && vol01 > 0.0001f)
                _hud.SetMuted(false);

            // aggiorna backup per unmute
            if (!_hud.IsMuted && vol01 > 0.0001f)
                _remoteVolBeforeMute = vol01;

            ApplyVolume(vol01);
            try { _hud.SetExternalVolume(vol01); } catch { }

            if (osd) ShowRemoteOsd(GetVolumeOsdSvg(vol01, _hud.IsMuted), bar01: _hud.IsMuted ? 0f : vol01, ms: 900);
        }

        private void RemoteAdjustVolume(float delta)
        {
            if (TryNetworkVolume(step: Math.Sign(delta), osd: true)) return;
            if (_hud == null) return;
            if (TryStepVolumeBoost(Math.Sign(delta))) return;

            float cur = _hud.IsMuted ? 0f : _hud.GetVolume();
            float next = Math.Clamp(cur + delta, 0f, 1f);
            RemoteSetVolume(next);
        }

        private void RemoteToggleMute()
        {
            if (TryNetworkVolume(toggleMute: true, osd: true)) return;
            if (IsBitstream()) return;
            if (_hud == null) return;

            if (_hud.IsMuted)
            {
                float v = Math.Clamp(_remoteVolBeforeMute, 0.05f, 1f);
                _hud.SetMuted(false);
                RemoteSetVolume(v);
            }
            else
            {
                float cur = _hud.GetVolume();
                if (cur > 0.0001f) _remoteVolBeforeMute = cur;

                _hud.SetMuted(true);
                ApplyVolume(0f);
                try { _hud.SetExternalVolume(0f); } catch { }

                ShowRemoteOsd(GetVolumeOsdSvg(0f, muted: true), bar01: 0f, ms: 900);
            }
        }

        private void PreparePlaybackSeek(bool clearTimelinePreview = true, double? previewSeconds = null)
        {
            try
            {
                var now = DateTime.UtcNow;
                _suppressPacketSamplesUntilUtc = now.AddMilliseconds(1400);
                _lastPktSample = now;
                _avgLastTs = DateTime.MinValue;
                _ioPrevWhen = DateTime.MinValue;
                if (previewSeconds.HasValue)
                    CaptureExplicitSeek(previewSeconds.Value);

                if (!clearTimelinePreview)
                    return;

                _scrubActive = false;
                _scrubPending = -1;

                var old = Interlocked.Exchange(ref _thumbCts, null);
                try { old?.Cancel(); } catch { }
                try { old?.Dispose(); } catch { }

                Interlocked.Increment(ref _previewReqSerial);
                try { _hud?.SetPreview(null, previewSeconds ?? _engine?.PositionSeconds ?? 0); } catch { }
            }
            catch { }
        }

        private void SeekRelative(double delta)
        {
            double duration = GetTimelineDurationSeconds();
            if (_engine == null || duration <= 0) return;
            double t = Math.Clamp(_engine.PositionSeconds + delta, 0, Math.Max(0.01, duration));
            SetTimelinePositionOverride(t);
            PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: t);
            _engine.PositionSeconds = t;
            try { PublishRemoteState(t); } catch { }
            try { _hud?.Invalidate(); } catch { }
        }

        // ---------------- Remote scan (long-press skip) ----------------
        private bool IsRemoteScanActive => _remoteScanTimer?.Enabled == true;

        private void StopRemoteScan()
        {
            try { _remoteScanTimer?.Stop(); } catch { }
            _remoteScanDir = 0;
            _remoteScanSpeedIdx = 0;
        }

        /// <summary>
        /// Avvia o incrementa lo "scan" (skip continuo) da telecomando.
        /// - Primo trigger: parte a x0,5
        /// - Trigger successivi: x1 -> x2 -> x4 (max)
        /// </summary>
        private void StepRemoteScan(int dir)
        {
            if (_engine == null || _duration <= 0) return;

            if (_remoteScanTimer == null)
            {
                _remoteScanTimer = new System.Windows.Forms.Timer { Interval = 100 };
                _remoteScanTimer.Tick += (_, __) => RemoteScanTick();
            }

            // se non era attivo o cambio direzione: riparti da x0,5
            if (!_remoteScanTimer.Enabled || _remoteScanDir != dir)
            {
                _remoteScanDir = dir;
                _remoteScanSpeedIdx = 0;
                _remoteScanTimer.Start();
            }
            else
            {
                // già in scan nella stessa direzione: step velocità
                if (_remoteScanSpeedIdx < REMOTE_SCAN_SPEEDS.Length - 1)
                    _remoteScanSpeedIdx++;
            }

            ShowRemoteScanOsd();
        }

        private void RemoteScanTick()
        {
            if (_engine == null || _duration <= 0)
            {
                StopRemoteScan();
                return;
            }

            int idx = Math.Clamp(_remoteScanSpeedIdx, 0, REMOTE_SCAN_SPEEDS.Length - 1);
            double speed = REMOTE_SCAN_SPEEDS[idx];
            double dt = (_remoteScanTimer?.Interval ?? 100) / 1000.0;
            double delta = REMOTE_SCAN_BASE_SECS_PER_SEC * speed * dt * _remoteScanDir;
            if (Math.Abs(delta) < 0.0001) return;

            SeekRelative(delta);

            // stop automatico ai bordi
            try
            {
                if (_engine.PositionSeconds <= 0.0001 && _remoteScanDir < 0)
                    StopRemoteScan();
                else if (_engine.PositionSeconds >= _duration - 0.0001 && _remoteScanDir > 0)
                    StopRemoteScan();
            }
            catch { }
        }

        private void ShowRemoteScanOsd()
        {
            if (_hud == null) return;

            int idx = Math.Clamp(_remoteScanSpeedIdx, 0, REMOTE_SCAN_SPEEDS.Length - 1);
            double speed = REMOTE_SCAN_SPEEDS[idx];
            string txt = speed == 0.5 ? "x0,5" : ("x" + speed.ToString("0.#")).Replace('.', ',');

            string? svg = _remoteScanDir < 0 ? _hud.SvgPathBack10 : _hud.SvgPathFwd10;
            ShowRemoteOsd(svg, bar01: null, ms: 900, text: txt);
        }

        private void SeekChapter(int dir)
        {
            if (_engine == null || _info == null || _info.Chapters.Count == 0) return;
            double cur = _engine.PositionSeconds;
            if (dir > 0)
            {
                var next = _info.Chapters.Select(c => c.start).FirstOrDefault(s => s > cur + 0.5);
                if (next > 0)
                {
                    double target = Math.Min(next, Math.Max(0.01, _duration));
                    PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: target);
                    _engine.PositionSeconds = target;
                }
            }
            else
            {
                var prev = _info.Chapters.Select(c => c.start).Where(s => s < cur - 0.5).DefaultIfEmpty(0).Max();
                double target = Math.Max(0, prev);
                PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: target);
                _engine.PositionSeconds = target;
            }
        }
        private void ShowNextImage(bool fromSlideshow = false)
        {
            if (!IsPhotoMode || _imageFiles.Count == 0) return;

            if (!fromSlideshow)
            {
                try { _photoHud?.Wake(); } catch { }
                RestartPhotoSlideshowInterval();
            }

            if (_imageIndex < 0 || _imageIndex >= _imageFiles.Count - 1)
                _imageIndex = 0;
            else
                _imageIndex++;

            OpenImage(_imageFiles[_imageIndex]);
        }

        private void ShowPrevImage()
        {
            if (!IsPhotoMode || _imageFiles.Count == 0) return;

            try { _photoHud?.Wake(); } catch { }
            RestartPhotoSlideshowInterval();

            if (_imageIndex <= 0)
                _imageIndex = _imageFiles.Count - 1;
            else
                _imageIndex--;

            OpenImage(_imageFiles[_imageIndex]);
        }

        private void Enable3D(Stereo3DMode mode)
        {
            if (mode == Stereo3DMode.None) return;

            _stereoAutoEnabled = false;
            _lastStereoAutoDetection = null;
            _stereo = mode;
            try { SaveExtrasConfig(); } catch { }

            // madVR, MPC Video Renderer, EVR e MPV convertono tutti in 2D da soli (vedi
            // RenderingEngine.Stereo / LibMpv): si applica dal vivo, senza cambiare motore.
            bool restored = RestoreRuntimeRendererAfter3D();
            if (_hasSavedRendererFor3D)
            {
                _manualRendererChoice = _savedRendererFor3D;
                _savedRendererFor3D = null;
                _hasSavedRendererFor3D = false;
                restored = true;
            }
            _lblStatus.Text = mode == Stereo3DMode.SBS ? Tx("3D Side-by-side → 2D", "3D Side-by-side → 2D") : Tx("3D Top/Bottom → 2D", "3D Top/Bottom → 2D");
            if (restored)
                ReopenSame();
            else
            {
                try
                {
                    _engine?.SetStereo3D(_stereo);
                    UpdateVideoWindowForCurrentHost();
                }
                catch { }
            }
            try { PublishRemoteState(); } catch { }
            _hud.ShowOnce(1200);
        }

        private void Disable3DRestoreRenderer()
        {
            _stereoAutoEnabled = false;
            _lastStereoAutoDetection = null;
            _stereo = Stereo3DMode.None;
            try { SaveExtrasConfig(); } catch { }

            bool restoredRenderer = RestoreRuntimeRendererAfter3D();
            if (_hasSavedRendererFor3D)
            {
                // Ripristina il renderer che avevamo prima di forzare EVR (anche Auto=null)
                _manualRendererChoice = _savedRendererFor3D;
                _savedRendererFor3D = null;
                _hasSavedRendererFor3D = false;
                restoredRenderer = true;
            }

            if (restoredRenderer)
            {
                _lblStatus.Text = Tx("3D disattivato: ripristino renderer precedente", "3D disabled: restoring the previous renderer");
                ReopenSame(); // ricrea il graph e torna all’immagine doppia
            }
            else
            {
                // Non avevamo forzato nulla: solo togli il 3D
                try
                {
                    _engine?.SetStereo3D(_stereo);
                    UpdateVideoWindowForCurrentHost();
                }
                catch { }
            }

            _hud.ShowOnce(1200);
        }

        // ===== Overlay "Audio Only" =====
        internal sealed class AudioOnlyOverlay : Control
        {
            private Image? _png;
            private Image? _artwork;
            private string? _artworkKey;
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string? ImagePath { get; set; }
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string Caption { get; set; } = "Audio Only";

            public AudioOnlyOverlay()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Color.Black);
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                var candidates = new[]
                {
                    ImagePath,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "AudioOnly.png"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "audioOnly.jpg"),
                }.Where(p => !string.IsNullOrWhiteSpace(p));

                string? found = candidates.FirstOrDefault(File.Exists);
                if (found != null)
                {
                    try
                    {
                        using var fs = new FileStream(found, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var bmp = Image.FromStream(fs);
                        _png = new Bitmap(bmp);
                        Dbg.Log("AudioOnlyOverlay: caricato PNG da " + found, Dbg.LogLevel.Info);
                    }
                    catch (Exception ex) { Dbg.Warn("AudioOnlyOverlay: errore caricando '" + found + "': " + ex.Message); }
                }
                else
                {
                    Dbg.Warn("AudioOnlyOverlay: PNG non trovato.");
                }
            }

            public void SetArtworkPath(string? artworkPath)
            {
                string key = string.IsNullOrWhiteSpace(artworkPath) ? string.Empty : artworkPath.Trim();
                if (string.Equals(_artworkKey, key, StringComparison.OrdinalIgnoreCase))
                    return;

                _artworkKey = key;
                try { _artwork?.Dispose(); } catch { }
                _artwork = null;

                if (!string.IsNullOrWhiteSpace(key) && File.Exists(key))
                {
                    try
                    {
                        using var fs = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var src = Image.FromStream(fs);
                        _artwork = new Bitmap(src);
                    }
                    catch { _artwork = null; }
                }

                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                var image = _artwork ?? _png;
                if (image != null)
                {
                    var maxW = (int)(Width * 0.42);
                    var maxH = (int)(Height * 0.48);
                    double s = Math.Min(maxW / (double)image.Width, maxH / (double)image.Height);
                    s = Math.Min(1.0, Math.Max(0.05, s));
                    int w = Math.Max(1, (int)Math.Round(image.Width * s));
                    int h = Math.Max(1, (int)Math.Round(image.Height * s));
                    int x = (Width - w) / 2;
                    int y = (Height - h) / 2 - 24;

                    using (var glow = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
                        g.FillEllipse(glow, x - w * 0.08f, y - h * 0.08f, w * 1.16f, h * 1.16f);

                    g.DrawImage(image, new Rectangle(x, y, w, h));
                }

                using var f = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 16, FontStyle.Bold);
                var sz = g.MeasureString(Caption, f);
                using var sh = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                using var fg = new SolidBrush(Color.FromArgb(230, 230, 230));
                float cx = (Width - sz.Width) / 2f;
                float cy = Height * 0.65f;
                g.DrawString(Caption, f, sh, cx + 1, cy + 1);
                g.DrawString(Caption, f, fg, cx, cy);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _png?.Dispose();
                    _artwork?.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        // ===== Overlay “Placeholder” (usato come gate pre-film) =====
        internal sealed class PausePlaceholderOverlay : Control
        {
            private readonly Random _rng = new();
            private Image? _img;
            private Image? _brandLogo;
            private string? _folder;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string Caption { get; set; } = "PAUSA";

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool DrawCaptionAlways { get; set; } = false;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string TitleText { get; set; } = string.Empty;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string SubtitleText { get; set; } = string.Empty;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool ShowBranding { get; set; } = false;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool UseCoverImage { get; set; } = false;

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool HasImage => _img != null;

            public PausePlaceholderOverlay()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            public void SetFolder(string folder)
            {
                _folder = folder;
            }

            public void SetBrandLogo(Image? logo)
            {
                try { _brandLogo?.Dispose(); } catch { }
                _brandLogo = null;

                if (logo != null)
                {
                    try { _brandLogo = new Bitmap(logo); } catch { _brandLogo = null; }
                }

                try { Invalidate(); } catch { }
            }

            public void ClearDisplayedImage()
            {
                ClearImage();
            }

            public void ShowPlaceholder(string? filePath)
            {
                // Se non specificato → random dalla cartella.
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    ShowRandomPlaceholder();
                    return;
                }

                try
                {
                    if (!File.Exists(filePath))
                    {
                        ShowRandomPlaceholder();
                        return;
                    }

                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var bmp = Image.FromStream(fs);
                    _img?.Dispose();
                    _img = new Bitmap(bmp);
                }
                catch
                {
                    // best-effort
                    ClearImage();
                }

                try { Invalidate(); } catch { }
            }

            public void ShowRandomPlaceholder()
            {
                try
                {
                    var folder = _folder;
                    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    {
                        ClearImage();
                        return;
                    }

                    var files = Directory.EnumerateFiles(folder)
                        .Where(f =>
                        {
                            var ext = Path.GetExtension(f)?.ToLowerInvariant();
                            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
                        })
                        .ToList();

                    if (files.Count == 0)
                    {
                        ClearImage();
                        return;
                    }

                    var pick = files[_rng.Next(files.Count)];
                    using var fs = new FileStream(pick, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var bmp = Image.FromStream(fs);
                    _img?.Dispose();
                    _img = new Bitmap(bmp);
                }
                catch
                {
                    // best-effort
                }
                try { Invalidate(); } catch { }
            }

            private void ClearImage()
            {
                try { _img?.Dispose(); } catch { }
                _img = null;
                try { Invalidate(); } catch { }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // L'OverlayHost usa il nero puro come color-key trasparente.
                // Un quasi-nero mantiene opaca anche la parte nera dei PNG.
                e.Graphics.Clear(Color.FromArgb(2, 3, 5));
            }

            private Bitmap? _backdropLayer;
            private (Image? Image, Size Size, bool Branding, bool Cover) _backdropLayerKey;

            private void DrawBackdropLayer(Graphics target)
            {
                if (Width <= 0 || Height <= 0) return;
                var key = (_img as Image, Size, ShowBranding, UseCoverImage);
                if (_backdropLayer == null || _backdropLayerKey != key)
                {
                    _backdropLayer?.Dispose();
                    _backdropLayer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    _backdropLayerKey = key;
                    using (var g = Graphics.FromImage(_backdropLayer))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Black);
                        if (_img != null)
                        {
                            double s = UseCoverImage
                                ? Math.Max(Width / (double)_img.Width, Height / (double)_img.Height)
                                : Math.Min(Width / (double)_img.Width, Height / (double)_img.Height);
                            int w = Math.Max(1, (int)Math.Round(_img.Width * s));
                            int h = Math.Max(1, (int)Math.Round(_img.Height * s));
                            g.DrawImage(_img, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
                        }
                    }
                    ComposeBackdrop(_backdropLayer, hasImage: _img != null, branding: ShowBranding);
                }
                var mode = target.CompositingMode;
                target.CompositingMode = CompositingMode.SourceCopy;
                target.DrawImageUnscaled(_backdropLayer, 0, 0);
                target.CompositingMode = mode;
            }

            /// <summary>
            /// Veil and plain background are computed in floating point and dithered at the
            /// moment they become 8 bit. Blending an 8-bit veil over an 8-bit picture first and
            /// adding noise afterwards left steps as wide as a hand on a 4K screen: a dark
            /// picture under a 90% veil has only a dozen levels left across two thousand pixels.
            /// </summary>
            private static unsafe void ComposeBackdrop(Bitmap layer, bool hasImage, bool branding)
            {
                int width = layer.Width, height = layer.Height;
                // Colonne e righe del velo: pieno dietro al titolo (in basso a sinistra), nullo a destra.
                var keepX = new float[width];
                var keepY = new float[height];
                for (int x = 0; x < width; x++)
                {
                    float u = Math.Clamp((x / (float)Math.Max(1, width - 1) - .08f) / .60f, 0f, 1f);
                    keepX[x] = branding ? 1f - .90f * (1f - u * u * (3f - 2f * u)) : 1f;
                }
                for (int y = 0; y < height; y++)
                {
                    float v = Math.Clamp((y / (float)Math.Max(1, height - 1) - .50f) / .50f, 0f, 1f);
                    keepY[y] = branding ? 1f - .62f * (v * v * (3f - 2f * v)) : 1f;
                }

                Color accent = Theme.Accent;
                float glowR = accent.R * .34f, glowG = accent.G * .34f, glowB = accent.B * .34f;
                float glowX = width * .78f, glowY = height * .20f, glowRadius = Math.Max(1f, height * .95f);

                var rect = new Rectangle(0, 0, width, height);
                var data = layer.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                        float ky = keepY[y];
                        float ny = y / (float)Math.Max(1, height - 1);
                        float dy = (y - glowY) / glowRadius;
                        for (int x = 0; x < width; x++)
                        {
                            byte* p = row + x * 4;
                            float b, gr, r;
                            if (hasImage) { b = p[0]; gr = p[1]; r = p[2]; }
                            else if (branding)
                            {
                                // Fondo neutro senza backdrop: grigio-blu che scende verso il nero, con un alone
                                // d'accento largo quanto lo schermo (le sue dimensioni seguono la risoluzione).
                                float t = Math.Clamp(x / (float)Math.Max(1, width - 1) * .35f + ny * .65f, 0f, 1f);
                                float dx = (x - glowX) / glowRadius;
                                float glow = Math.Max(0f, 1f - MathF.Sqrt(dx * dx + dy * dy));
                                glow = glow * glow * (3f - 2f * glow);
                                glow *= glow;
                                r = 17f - 11f * t + glowR * glow;
                                gr = 20f - 13f * t + glowG * glow;
                                b = 27f - 17f * t + glowB * glow;
                            }
                            else { b = 0f; gr = 0f; r = 0f; }

                            float keep = keepX[x] * ky;
                            // Rumore triangolare di un livello, uguale sui tre canali (niente puntini colorati).
                            uint hash = unchecked((uint)(x * 374761393 + y * 668265263));
                            hash ^= hash >> 15; hash *= 0x2c1b3c6du; hash ^= hash >> 12; hash *= 0x297a2d39u; hash ^= hash >> 15;
                            float noise = ((int)(hash & 0xFFFF) - (int)(hash >> 16)) / 65536f;
                            // Mai nero puro: l'OverlayHost lo usa come color-key e diventerebbe un buco.
                            const float Floor = 3f, Span = (255f - Floor) / 255f;
                            p[0] = (byte)Math.Clamp(Floor + b * keep * Span + noise + .5f, Floor, 255f);
                            p[1] = (byte)Math.Clamp(Floor + gr * keep * Span + noise + .5f, Floor, 255f);
                            p[2] = (byte)Math.Clamp(Floor + r * keep * Span + noise + .5f, Floor, 255f);
                            p[3] = 255;
                        }
                    }
                }
                finally { layer.UnlockBits(data); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                // Fondo, immagine e banda scura in una bitmap con dithering: le sfumature quasi
                // nere in 8 bit facevano gradini ben visibili. I testi restano sopra, nitidi.
                DrawBackdropLayer(g);

                if (_img != null)
                {
                    Rectangle dst;
                    if (UseCoverImage)
                    {
                        double s = Math.Max(Width / (double)_img.Width, Height / (double)_img.Height);
                        int w = Math.Max(1, (int)Math.Round(_img.Width * s));
                        int h = Math.Max(1, (int)Math.Round(_img.Height * s));
                        int x = (Width - w) / 2;
                        int y = (Height - h) / 2;
                        dst = new Rectangle(x, y, w, h);
                    }
                    else
                    {
                        double s = Math.Min(Width / (double)_img.Width, Height / (double)_img.Height);
                        int w = Math.Max(1, (int)Math.Round(_img.Width * s));
                        int h = Math.Max(1, (int)Math.Round(_img.Height * s));
                        int x = (Width - w) / 2;
                        int y = (Height - h) / 2;
                        dst = new Rectangle(x, y, w, h);
                    }


                    if (DrawCaptionAlways && !string.IsNullOrWhiteSpace(Caption))
                    {
                        using var f = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 18, FontStyle.Bold);
                        var txt = Caption;
                        var sz = g.MeasureString(txt, f);
                        using var sh = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                        using var fg = new SolidBrush(Color.FromArgb(235, 235, 235));
                        float cx = (Width - sz.Width) / 2f;
                        float cy = Height * 0.78f;
                        g.DrawString(txt, f, sh, cx + 2, cy + 2);
                        g.DrawString(txt, f, fg, cx, cy);
                    }
                }
                else if (!ShowBranding)
                {
                    using var f = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 22, FontStyle.Bold);
                    var txt = string.IsNullOrWhiteSpace(Caption) ? "PAUSA" : Caption;
                    var sz = g.MeasureString(txt, f);
                    using var sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                    using var fg = new SolidBrush(Color.FromArgb(235, 235, 235));
                    float cx = (Width - sz.Width) / 2f;
                    float cy = (Height - sz.Height) / 2f;
                    g.DrawString(txt, f, sh, cx + 2, cy + 2);
                    g.DrawString(txt, f, fg, cx, cy);
                }

                if (ShowBranding)
                {
                    // Scritte nette: antialias in scala di grigi (ClearType su questa superficie
                    // lasciava frange colorate) e nessuna ombra. Tutte le misure seguono l'altezza
                    // dello schermo: a 4K il logo restava di 48 pixel e il titolo si perdeva.
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    float margin = (float)Math.Round(Math.Max(40f, Width * 0.055f));
                    float maxTextWidth = Math.Max(240f, Width * 0.46f);
                    using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", Math.Max(24f, Height * 0.066f), FontStyle.Regular, GraphicsUnit.Pixel);
                    using var subFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", Math.Max(13f, Height * 0.0205f), FontStyle.Regular, GraphicsUnit.Pixel);

                    string title = string.IsNullOrWhiteSpace(TitleText) ? string.Empty : TitleText.Trim();
                    string subtitle = string.IsNullOrWhiteSpace(SubtitleText) ? string.Empty : SubtitleText.Trim();

                    // Marchio in alto a sinistra, da solo: sotto al titolo sembrava una nota a pie' di pagina.
                    if (_brandLogo != null)
                    {
                        float aspect = Math.Max(0.1f, _brandLogo.Width / (float)Math.Max(1, _brandLogo.Height));
                        float h = (float)Math.Round(Math.Max(24f, Height * 0.042f));
                        float w = (float)Math.Round(h * aspect);
                        g.DrawImage(_brandLogo, new RectangleF(margin, (float)Math.Round(Math.Max(28f, Height * 0.065f)), w, h));
                    }

                    // Titolo e riga secondaria ancorati in basso a sinistra, dove il velo e' pieno.
                    using var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };
                    float titleMax = titleFont.GetHeight(g) * 2.05f;
                    float titleHeight = title.Length == 0 ? 0f : Math.Min(titleMax, g.MeasureString(title, titleFont, new SizeF(maxTextWidth, Height), sf).Height);
                    float subtitleHeight = subtitle.Length == 0 ? 0f : g.MeasureString(subtitle, subFont, new SizeF(maxTextWidth, Height), sf).Height;
                    float gap = (title.Length > 0 && subtitle.Length > 0) ? (float)Math.Round(Height * 0.016f) : 0f;
                    float bottom = Height - (float)Math.Round(Math.Max(44f, Height * 0.105f));
                    float y = (float)Math.Round(bottom - titleHeight - gap - subtitleHeight);

                    using var titleBrush = new SolidBrush(Color.FromArgb(248, 249, 250));
                    using var subBrush = new SolidBrush(Color.FromArgb(176, 184, 194));

                    if (title.Length > 0)
                    {
                        g.DrawString(title, titleFont, titleBrush, new RectangleF(margin, y, maxTextWidth, titleHeight + 1f), sf);
                        y = (float)Math.Round(y + titleHeight + gap);
                    }

                    if (subtitle.Length > 0)
                        g.DrawString(subtitle, subFont, subBrush, new RectangleF(margin + (float)Math.Round(Height * 0.003f), y, maxTextWidth, subtitleHeight + 1f), sf);
                }
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _img?.Dispose(); } catch { }
                    try { _brandLogo?.Dispose(); } catch { }
                    try { _backdropLayer?.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }
        }

        // ===== Menu scuro (ContextMenuStrip) =====

    }
}
