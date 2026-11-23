using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace CinecorePlayer2025.Utilities;

/// <summary>
/// Sessione web di Genius su Windows (2026-11-22), l'equivalente della WKWebView del Mac (mac/MacWebSession.m): quando
/// genius.com risponde 403 alla richiesta diretta, una WebView2 nascosta con cookie persistenti legge la pagina come un
/// browser normale. Se il sito vuole una verifica, Strumenti › "Genius: verifica connessione…" mostra la stessa
/// WebView2 in una finestra: la verifica la fa l'utente, nessun cookie viene falsificato e nessuna sfida automatizzata.
/// Tutto gira sul thread dell'interfaccia (WebView2 vuole un thread STA con la coda dei messaggi, quello di Avalonia).
/// </summary>
internal static class GeniusWebSessionWin
{
    private const string ClassName = "CinecoreGeniusWeb";
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_POPUP = 0x80000000;
    private const int WM_SIZE = 0x0005, WM_CLOSE = 0x0010, SW_HIDE = 0, SW_SHOWNORMAL = 1;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProc Proc = WindowProc; // tenuto vivo: lo chiama Windows

    private static IntPtr _hwnd;
    private static CoreWebView2Controller? _controller;
    private static Task<bool>? _ready;
    private static bool _verifying;

    public static bool RuntimeInstalled
    {
        get
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch { return false; }
        }
    }

    /// <summary>Testo della pagina (HTML, o il JSON delle /api/) letto dalla WebView2; null se non arriva in tempo.</summary>
    public static Task<string?> GetTextAsync(string url, TimeSpan timeout, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (!await EnsureReadyAsync()) return null;
            var web = _controller!.CoreWebView2;
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => finished.TrySetResult(e.IsSuccess && e.HttpStatusCode is >= 200 and < 300);
            web.NavigationCompleted += OnDone;
            try
            {
                web.Navigate(url);
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timer.CancelAfter(timeout);
                bool ok;
                try { ok = await finished.Task.WaitAsync(timer.Token); }
                catch (OperationCanceledException) { ok = false; }
                if (!ok) return null;
                bool api = new Uri(url).AbsolutePath.StartsWith("/api/", StringComparison.Ordinal);
                string json = await web.ExecuteScriptAsync(api ? "document.body ? document.body.innerText : ''" : "document.documentElement.outerHTML");
                string? text = JsonSerializer.Deserialize<string>(json);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception ex) { Console.Error.WriteLine("[GENIUS] sessione web: " + ex.Message); return null; }
            finally { web.NavigationCompleted -= OnDone; }
        })!;

    /// <summary>Mostra genius.com in una finestra per la verifica; alla chiusura Genius riprova.</summary>
    public static void ShowVerification(Action closed)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (!await EnsureReadyAsync()) return;
            _onVerificationClosed = closed;
            _verifying = true;
            SetWindowTextW(_hwnd, "Genius: verifica connessione");
            SetWindowLongPtrW(_hwnd, -16, (IntPtr)(WS_OVERLAPPEDWINDOW | 0x10000000)); // GWL_STYLE, WS_VISIBLE
            double scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0); // la finestra e' in pixel fisici
            SetWindowPos(_hwnd, IntPtr.Zero, (int)(160 * scale), (int)(100 * scale), (int)(1000 * scale), (int)(760 * scale), 0x0020 | 0x0040); // FRAMECHANGED | SHOWWINDOW
            ShowWindow(_hwnd, SW_SHOWNORMAL);
            SetForegroundWindow(_hwnd);
            Resize();
            _controller!.IsVisible = true;
            _controller.CoreWebView2.Navigate("https://genius.com/");
        });
    }

    private static Action? _onVerificationClosed;

    private static Task<bool> EnsureReadyAsync() => _ready ??= CreateAsync();

    private static async Task<bool> CreateAsync()
    {
        try
        {
            var wc = new WNDCLASSW { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), hInstance = GetModuleHandleW(null), lpszClassName = ClassName };
            RegisterClassW(ref wc);
            _hwnd = CreateWindowExW(0x00000080 /* WS_EX_TOOLWINDOW: niente barra delle applicazioni */, ClassName, "Genius", WS_POPUP,
                0, 0, 1000, 760, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            // Dati del browser in Local (non Roaming: cache di diversi MB da non sincronizzare con il profilo), nella stessa
            // cartella del Cinecore precedente: la verifica di Genius gia' fatta resta valida.
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "GeniusBrowser");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            _controller.IsVisible = false;
            var settings = _controller.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            Resize();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[GENIUS] WebView2 non disponibile: " + ex.Message);
            return false;
        }
    }

    private static void Resize()
    {
        if (_controller == null || _hwnd == IntPtr.Zero) return;
        GetClientRect(_hwnd, out var r);
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SIZE) Resize();
        if (msg == WM_CLOSE)
        {
            // Chiusa la verifica: la finestra torna nascosta (la sessione e i cookie restano) e Genius riprova.
            if (_controller != null) _controller.IsVisible = false;
            ShowWindow(hwnd, SW_HIDE);
            if (_verifying) { _verifying = false; try { _onVerificationClosed?.Invoke(); } catch { } }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName;
    }
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
