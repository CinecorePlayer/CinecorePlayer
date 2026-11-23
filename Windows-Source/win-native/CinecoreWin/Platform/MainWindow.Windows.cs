using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Parte Windows della finestra del player: icona e aggancio ai messaggi della finestra, perche' il video (finestra di
/// libmpv o del VideoHost) segua subito il player quando si sposta, si ridimensiona, si riduce a icona o torna davanti.
/// </summary>
public sealed partial class MainWindow
{
    private const uint WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGED = 0x0047, WM_DISPLAYCHANGE = 0x007E;
    private bool _syncPosted, _videoRounded;
    private WinCaptionButtons? _caption;


    /// <summary>
    /// Il contenuto della finestra con gli angoli arrotondati di Windows 11 disegnati dall'app (antialiasing), i semafori
    /// in alto a sinistra e il trascinamento dalla riga in alto della libreria e delle Impostazioni.
    /// </summary>
    private Avalonia.Controls.Control AddWindowsCaption(Avalonia.Controls.Grid surface)
    {
        // Cornice e pulsanti di Windows: niente semafori ne' angoli disegnati dall'app.
        WindowsCaptionHost = this;
        // Nascosta per DWM (cloak) gia' prima della prima apparizione: cornice e contenuto compaiono insieme
        // (RevealWhenDrawn la mostra dopo il primo fotogramma completo).
        try
        {
            if (TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
            {
                int on = 1;
                _cloakedEarly = WinNative.DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref on, 4) == 0;
            }
        }
        catch { }
        Avalonia.Threading.DispatcherTimer.Run(() =>
        {
            SyncVideoCorners();
            SyncVideoInset();
            return !_closing;
        }, TimeSpan.FromMilliseconds(100));
        return surface;
    }

    private bool _cloakedEarly;
    private int _sentInset = -1;
    private long _insetSession = -1;

    /// <summary>Altezza della barra del titolo di Windows sopra il video (0 a schermo intero): il video si disegna sotto.</summary>
    private void SyncVideoInset()
    {
        if (!_video.IsRunning || !_videoReady) return;
        IntPtr hwnd = WinNative.Handle(this);
        if (hwnd == IntPtr.Zero) return;
        int inset = 0;
        if (WindowState != WindowState.FullScreen)
        {
            var frame = WinNative.VisibleRect(hwnd);
            var client = WinNative.ClientScreenRect(hwnd);
            inset = Math.Max(0, client.Top - frame.Top);
        }
        if (inset == _sentInset && _insetSession == _video.SessionId) return;
        _sentInset = inset; _insetSession = _video.SessionId;
        _video.Send("inset " + inset.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static MainWindow? WindowsCaptionHost;

    /// <summary>Angoli della finestra del VideoHost uguali a quelli del player (anche appena parte il film).</summary>
    private void SyncVideoCorners()
    {
        if (Engine == "mpv") return;
        int pid = _video.ProcessId;
        if (pid <= 0 || !_videoReady) return;
        bool rounded = WinVideoStack.RoundedFor(this);
        if (rounded == _videoRounded && pid == _videoRoundedPid) return;
        _videoRounded = rounded; _videoRoundedPid = pid;
        _video.Send("corners " + (rounded ? "1" : "0"));
    }

    // Semafori come sul Mac: nella libreria e nelle Impostazioni sempre visibili; durante il film compaiono quando il
    // puntatore va verso l'angolo in alto a sinistra (non coprono il titolo dell'HUD).
    private double _captionHudAlpha = 1, _captionShown = 1;

    private void ApplyCaptionOpacity()
    {
        if (_caption == null) return;
        double alpha = Math.Min(_captionShown, _captionHudAlpha);
        _caption.Opacity = alpha;
        _caption.IsHitTestVisible = alpha > .05;
    }

    /// <summary>Opacita' massima dei pulsanti (0 nei cambi di schermo intero, PiP e mini player).</summary>
    internal void SetCaptionAlpha(double alpha)
    {
        // Durante il film l'HUD nascosto non deve nascondere i pulsanti se il puntatore e' in alto: conta solo lo 0.
        _captionHudAlpha = alpha <= .01 ? 0 : 1;
        ApplyCaptionOpacity();
    }

    private int _videoRoundedPid;

    /// <summary>
    /// La finestra compariva a pezzi e lo splash scattava: il primo fotogramma costa 150-250 ms (shader della GPU
    /// compilati da Skia la prima volta). Windows la tiene nascosta (cloak di DWM) mentre disegna davvero; appare solo
    /// quando Avalonia conferma che due fotogrammi completi sono stati presentati, e lo splash parte da li'.
    /// </summary>
    private async void RevealWhenDrawn()
    {
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        int on = 1;
        bool cloaked = _cloakedEarly || hwnd != IntPtr.Zero && WinNative.DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref on, 4) == 0;
        // Con il cloak il contenuto si disegna subito per intero; senza, resta trasparente fino alla fine dell'attesa.
        if (cloaked) Opacity = 1;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
                for (int i = 0; i < 2; i++)
                {
                    var rendered = compositor.RequestCompositionBatchCommitAsync().Rendered;
                    if (await System.Threading.Tasks.Task.WhenAny(rendered, System.Threading.Tasks.Task.Delay(1500)) != rendered) break;
                }
        }
        catch (Exception ex) { Console.Error.WriteLine("[windows] attesa del primo fotogramma: " + ex.Message); }
        Opacity = 1;
        if (cloaked)
        {
            int off = 0;
            WinNative.DwmSetWindowAttribute(hwnd, 13, ref off, 4);
        }
        _splash.Restart();
        Console.Error.WriteLine($"[tempi] finestra mostrata dopo {started.ElapsedMilliseconds} ms dal primo disegno (cloak={cloaked})");
    }

    /// <summary>Prove: segnala i fotogrammi dell'interfaccia arrivati con piu' di 40 ms di distanza (scatti visibili).</summary>
    private void WatchFrames()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = -1;
        void Tick(TimeSpan _)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (last >= 0 && now - last > 40) Console.Error.WriteLine($"[frame] buco {now - last:0} ms a {DateTime.Now:HH:mm:ss.fff}");
            last = now;
            RequestAnimationFrame(Tick);
        }
        RequestAnimationFrame(Tick);
    }

    private void InitWindowsPlatform()
    {
        try
        {
            string icon = Path.Combine(AppContext.BaseDirectory, "cinecore.ico");
            if (File.Exists(icon)) Icon = new WindowIcon(icon);
        }
        catch { }
        MacWindowChrome.FollowTheme(this);
        MacAudioDevices.Prewarm();
        if (Environment.GetEnvironmentVariable("CINECORE_QA_STALL") == "1") WatchFrames();
        if (Environment.GetEnvironmentVariable("CINECORE_QA_FPS") == "1")
            RendererDiagnostics.DebugOverlays = Avalonia.Rendering.RendererDebugOverlays.Fps | Avalonia.Rendering.RendererDebugOverlays.RenderTimeGraph | Avalonia.Rendering.RendererDebugOverlays.LayoutTimeGraph;
        RevealWhenDrawn();
        try { Win32Properties.AddWndProcHookCallback(this, WindowsHook); }
        catch (Exception ex) { Console.Error.WriteLine("[windows] aggancio ai messaggi non riuscito: " + ex.Message); }
    }

    private IntPtr WindowsHook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Tasto "ingrandisci" della barra del titolo (e doppio clic sulla barra): schermo intero vero, come il
        // pulsante verde del Mac. Prima la finestra si ingrandiva soltanto e restavano barra del titolo e taskbar.
        if (msg == 0x0112 /* WM_SYSCOMMAND */ && ((long)wParam & 0xFFF0) == 0xF030 /* SC_MAXIMIZE */)
        {
            handled = true;
            Dispatcher.UIThread.Post(() => { if (!IsFullscreen) ToggleFullscreen(); });
            return IntPtr.Zero;
        }
        if (msg is WM_WINDOWPOSCHANGED or WM_SIZE or WM_ACTIVATE or WM_DISPLAYCHANGE && !_syncPosted)
        {
            _syncPosted = true;
            // Dopo il messaggio (la finestra ha gia' la posizione nuova), mai dentro la procedura della finestra.
            Dispatcher.UIThread.Post(SyncVideoWindows, DispatcherPriority.Send);
        }
        return IntPtr.Zero;
    }

    /// <summary>Video subito sotto il player e nascosto quando il player e' ridotto a icona.</summary>
    private void SyncVideoWindows()
    {
        _syncPosted = false;
        if (_closing) return;
        try
        {
            if (Engine == "mpv") { SyncVideoRect(); return; }
            int pid = _video.ProcessId;
            if (pid <= 0) return;
            IntPtr video = WinNative.TopWindowOf(pid, "Cinecore Video");
            if (video == IntPtr.Zero)
            {
                // Nascosta (ridotta a icona): si cerca anche fra le finestre non visibili.
                video = HiddenVideoWindow(pid);
                if (video == IntPtr.Zero) return;
            }
            WinVideoStack.KeepBelow(VideoOwner, video, cover: false, async: true);
            bool rounded = WinVideoStack.RoundedFor(this);
            if (rounded != _videoRounded || pid != _videoRoundedPid) { _videoRounded = rounded; _videoRoundedPid = pid; _video.Send("corners " + (rounded ? "1" : "0")); }
            SyncVideoRect();
        }
        catch { }
    }

    private static IntPtr HiddenVideoWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        WinNative.EnumWindows((h, _) =>
        {
            WinNative.GetWindowThreadProcessId(h, out uint owner);
            if (owner != pid) return true;
            var sb = new System.Text.StringBuilder(64);
            WinNative.GetWindowText(h, sb, sb.Capacity);
            if (!sb.ToString().StartsWith("Cinecore Video", StringComparison.Ordinal)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
