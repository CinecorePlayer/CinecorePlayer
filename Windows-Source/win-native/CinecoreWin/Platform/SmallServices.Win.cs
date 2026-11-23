using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// "Portachiavi" su Windows: voci cifrate con DPAPI (utente corrente) in %APPDATA%\Cinecore\credentials.json.
    /// Nessun consenso da chiedere: AccessRequired resta sempre falso.
    /// </summary>
    internal static class Keychain
    {
        private static readonly KeychainSession Session = new(new WindowsCredentialBackend());
        public static bool AccessRequired => Session.AccessRequired;
        public static bool Authorizing => Session.Authorizing;
        public static string? Get(string account) => Environment.GetEnvironmentVariable("CINECORE_QA_NO_ACCOUNTS") == "1" ? null : Session.Get(account);
        public static bool Set(string account, string value) => Session.Set(account, value);
        public static void Delete(string account) => Session.Delete(account);
        public static bool AuthorizePending() => Session.AuthorizePending();
    }

    internal sealed class WindowsCredentialBackend : IKeychainBackend
    {
        private const int NotFound = -25300;
        private static readonly object Sync = new();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Cinecore/keychain/v1");
        private static string FilePath => Path.Combine(CinecoreMac.WinNative.DataDir, "credentials.json");

        private static Dictionary<string, string> Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new(); }
            catch { }
            return new();
        }

        private static void Save(Dictionary<string, string> values)
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(values));
            File.Move(tmp, FilePath, true);
        }

        public KeychainRead Read(string account, bool interactive)
        {
            lock (Sync)
            {
                if (!Load().TryGetValue(account, out string? blob)) return new(NotFound, null);
                try { return new(0, Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser))); }
                catch { return new(NotFound, null); }
            }
        }

        public int Write(string account, string value)
        {
            lock (Sync)
            {
                var values = Load();
                byte[] plain = Encoding.UTF8.GetBytes(value);
                try { values[account] = Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Save(values);
                return 0;
            }
        }

        public int Delete(string account)
        {
            lock (Sync)
            {
                var values = Load();
                if (!values.Remove(account)) return NotFound;
                Save(values);
                return 0;
            }
        }
    }
}

namespace CinecoreMac
{
    /// <summary>"Avvia con Windows": valore nella chiave Run dell'utente.</summary>
    internal static class LoginItem
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Cinecore Player (Avalonia)";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is string s && s.Length > 0;
                }
                catch { return false; }
            }
        }

        public static bool Set(bool enabled)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled) key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
                else key.DeleteValue(ValueName, false);
                return Enabled == enabled;
            }
            catch { return false; }
        }
    }

    /// <summary>Su Windows l'icona dell'app e' quella dell'eseguibile: libmpv non la cambia.</summary>
    internal static class MacAppIcon
    {
        public static void RestoreSoon() { }
        public static void Restore() { }
    }

    /// <summary>
    /// Puntatore nascosto durante il film. ShowCursor conta le chiamate: si tiene uno stato solo (nascosto o no) cosi'
    /// le chiamate restano in pari. La posizione si legge dal sistema (GetCursorPos), in pixel dello schermo.
    /// </summary>
    internal static class MacCursor
    {
        private static bool _hidden;
        public static bool Hidden => _hidden;

        public static void SetHidden(bool hidden)
        {
            if (hidden == _hidden) return;
            _hidden = hidden;
            // Il cursore della finestra lo decide anche Avalonia (Cursor = None nell'HUD): qui solo il contatore di sistema.
            try { WinNative.ShowCursor(!hidden); } catch { }
        }

        public static Point? ScreenLocation()
        {
            try { return WinNative.GetCursorPos(out var p) ? new Point(p.X, p.Y) : null; }
            catch { return null; }
        }

        /// <summary>La posizione dentro la finestra (in unita' di Avalonia, origine in alto a sinistra) o null se fuori.</summary>
        public static Point? InWindow(TopLevel top, Point screen)
        {
            try
            {
                IntPtr hwnd = WinNative.Handle(top);
                if (hwnd == IntPtr.Zero) return null;
                var p = new WinNative.POINT { X = (int)screen.X, Y = (int)screen.Y };
                WinNative.ScreenToClient(hwnd, ref p);
                double s = Math.Max(0.1, top.RenderScaling);
                double x = p.X / s, y = p.Y / s;
                return x >= 0 && y >= 0 && x <= top.ClientSize.Width && y <= top.ClientSize.Height ? new Point(x, y) : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Niente pannello Colori di sistema su Windows: si usa il selettore disegnato dall'app (ColorPickerSheet).</summary>
    internal static class MacColorPanel
    {
        public static bool Available => false;
        public static bool Show(Color initial, string title, Action<Color> apply) => false;
        internal static void TestPick(Color c) { }
        internal static string TestRoundTrip(Color c) => "non disponibile su Windows";
        internal static void TestClose() { }
    }

    /// <summary>Nessuna "Space" di macOS su Windows.</summary>
    internal static class MacSpaceGuard
    {
        public static void Install() { }
    }
}
