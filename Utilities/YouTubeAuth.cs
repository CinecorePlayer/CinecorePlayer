#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    internal static class YouTubeAuth
    {
        private static readonly string _dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025");

        private static readonly string _cookieHeaderPath = Path.Combine(_dir, "yt_cookie_header.txt");
        private static readonly string _cookieFilePath = Path.Combine(_dir, "yt_cookies.txt");

        /// <summary>
        /// Cookie file (Netscape format) compatibile con yt-dlp (--cookies).
        /// </summary>
        public static string GetCookieFilePath()
        {
            try { Directory.CreateDirectory(_dir); } catch { }
            return _cookieFilePath;
        }

        public static bool HasYouTubeLoginCookies()
        {
            try
            {
                var hdr = LoadCookieHeaderBestEffort();
                if (string.IsNullOrWhiteSpace(hdr)) return false;

                // Marker tipici di sessione Google/YouTube.
                // (Non garantisce che l'account sia valido, ma evita falsi positivi grossolani.)
                return hdr.Contains("SAPISID=", StringComparison.OrdinalIgnoreCase) ||
                       hdr.Contains("__Secure-3PAPISID=", StringComparison.OrdinalIgnoreCase) ||
                       hdr.Contains("__Secure-1PSID=", StringComparison.OrdinalIgnoreCase) ||
                       hdr.Contains("__Secure-3PSID=", StringComparison.OrdinalIgnoreCase) ||
                       hdr.Contains("SID=", StringComparison.OrdinalIgnoreCase) ||
                       hdr.Contains("LOGIN_INFO=", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Header cookie per richieste HTTP (scraping YouTube HTML).
        /// Include un CONSENT best-effort per evitare la pagina "Before you continue".
        /// </summary>
        public static string BuildCookieHeader(bool includeConsent = true)
        {
            var sb = new StringBuilder();

            // best-effort per EU consent (non sempre basta, ma aiuta parecchio).
            if (includeConsent) sb.Append("CONSENT=YES+1; SOCS=CAI; ");
            var hdr = LoadCookieHeaderBestEffort();
            if (!string.IsNullOrWhiteSpace(hdr))
            {
                hdr = CleanCookieHeader(hdr);
                if (!string.IsNullOrWhiteSpace(hdr))
                    sb.Append(hdr.Trim().TrimEnd(';'));
            }

            return sb.ToString().Trim().TrimEnd(';', ' ');
        }

        /// <summary>
        /// Import cookie da WinINet (legacy WebBrowser/IE) e salvali nel cookie store.
        /// Utile come fallback se WebView2 non è disponibile.
        /// </summary>
        public static bool TryImportCookiesFromWinINet()
        {
            try
            {
                var cookie = GetWinINetCookieHeader("https://www.youtube.com/");
                if (string.IsNullOrWhiteSpace(cookie))
                    cookie = GetWinINetCookieHeader("https://accounts.google.com/");

                if (string.IsNullOrWhiteSpace(cookie))
                    return false;

                SaveCookieHeader(cookie);
                SaveNetscapeCookieFileFromHeader(cookie);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Esporta cookie da un controllo WebView2 (Edge) verso il cookie store (header + file Netscape per yt-dlp).
        /// Chiamalo periodicamente mentre l'utente effettua il login dentro al player.
        /// </summary>
        public static async Task<bool> TrySyncCookiesFromWebView2Async(object webView2Control)
        {
            try
            {
                if (webView2Control == null) return false;

                // Ensure CoreWebView2
                await EnsureCoreWebView2Async(webView2Control).ConfigureAwait(true);

                var t = webView2Control.GetType();
                var coreProp = t.GetProperty("CoreWebView2");
                var core = coreProp?.GetValue(webView2Control);
                if (core == null) return false;

                var cmProp = core.GetType().GetProperty("CookieManager");
                var cm = cmProp?.GetValue(core);
                if (cm == null) return false;

                var getCookiesMi = cm.GetType().GetMethod("GetCookiesAsync");
                if (getCookiesMi == null) return false;

                object? taskObj;

                // signature: GetCookiesAsync(string uri)
                if (getCookiesMi.GetParameters().Length == 1)
                    taskObj = getCookiesMi.Invoke(cm, new object?[] { "https://www.youtube.com/" });
                else
                    taskObj = getCookiesMi.Invoke(cm, new object?[] { new Uri("https://www.youtube.com/") });

                if (taskObj is not Task task) return false;
                await task.ConfigureAwait(true);

                var resultProp = taskObj.GetType().GetProperty("Result");
                var result = resultProp?.GetValue(taskObj);
                if (result == null) return false;

                var cookies = new List<object>();
                foreach (var c in (IEnumerable)result)
                    cookies.Add(c);

                if (cookies.Count == 0) return false;

                var header = BuildHeaderFromWebView2Cookies(cookies);
                if (string.IsNullOrWhiteSpace(header)) return false;

                SaveCookieHeader(header);
                SaveNetscapeCookieFileFromWebView2Cookies(cookies);

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ======================
        // Internals
        // ======================

        private static string? LoadCookieHeaderBestEffort()
        {
            // 1) Cookie store
            try
            {
                if (File.Exists(_cookieHeaderPath))
                {
                    var s = File.ReadAllText(_cookieHeaderPath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
            catch { }

            // 2) Legacy WinINet import (best-effort)
            try
            {
                if (TryImportCookiesFromWinINet() && File.Exists(_cookieHeaderPath))
                {
                    var s = File.ReadAllText(_cookieHeaderPath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
            catch { }

            return null;
        }

        private static void SaveCookieHeader(string header)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(_cookieHeaderPath, CleanCookieHeader(header), Encoding.UTF8);
            }
            catch { }
        }

        private static string CleanCookieHeader(string hdr)
        {
            // rimuove eventuale prefisso "Cookie:" e filtra CONSENT/SOCS (li aggiungiamo noi)
            hdr = hdr.Replace("Cookie:", "", StringComparison.OrdinalIgnoreCase).Trim();
            hdr = RemoveCookie(hdr, "CONSENT");
            hdr = RemoveCookie(hdr, "SOCS");
            return hdr.Trim().TrimEnd(';');
        }

        private static string RemoveCookie(string hdr, string name)
        {
            try
            {
                var parts = hdr.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                var keep = new List<string>(parts.Length);
                foreach (var p in parts)
                {
                    var t = p.Trim();
                    if (t.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    keep.Add(t);
                }
                return string.Join("; ", keep);
            }
            catch
            {
                return hdr;
            }
        }

        private static void SaveNetscapeCookieFileFromHeader(string header)
        {
            try
            {
                Directory.CreateDirectory(_dir);

                var sb = new StringBuilder();
                sb.AppendLine("# Netscape HTTP Cookie File");
                sb.AppendLine("# Generated by CinecorePlayer2025 (fallback header -> netscape)");
                sb.AppendLine();

                var parts = CleanCookieHeader(header).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                long exp = 2147483647; // 2038-ish, "quasi per sempre" per yt-dlp

                foreach (var part in parts)
                {
                    var t = part.Trim();
                    var eq = t.IndexOf('=');
                    if (eq <= 0) continue;

                    var name = t.Substring(0, eq).Trim();
                    var value = t.Substring(eq + 1).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // domain best-effort
                    sb.AppendLine($".youtube.com\tTRUE\t/\tFALSE\t{exp}\t{name}\t{value}");
                }

                File.WriteAllText(_cookieFilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static async Task EnsureCoreWebView2Async(object webView2Control)
        {
            try
            {
                var t = webView2Control.GetType();
                var mi = t.GetMethod("EnsureCoreWebView2Async");
                if (mi == null) return;

                object? taskObj;
                int pc = mi.GetParameters().Length;

                if (pc == 0)
                    taskObj = mi.Invoke(webView2Control, Array.Empty<object>());
                else if (pc == 1)
                    taskObj = mi.Invoke(webView2Control, new object?[] { null });
                else
                    taskObj = mi.Invoke(webView2Control, new object?[] { null, null });

                if (taskObj is Task task)
                    await task.ConfigureAwait(true);
            }
            catch { }
        }

        private static string BuildHeaderFromWebView2Cookies(List<object> cookies)
        {
            var sb = new StringBuilder();
            bool first = true;

            foreach (var c in cookies)
            {
                var name = ReadCookieStringProp(c, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var value = ReadCookieStringProp(c, "Value") ?? string.Empty;

                if (!first) sb.Append("; ");
                sb.Append(name).Append("=").Append(value);
                first = false;
            }

            return sb.ToString();
        }

        private static void SaveNetscapeCookieFileFromWebView2Cookies(List<object> cookies)
        {
            try
            {
                Directory.CreateDirectory(_dir);

                var sb = new StringBuilder();
                sb.AppendLine("# Netscape HTTP Cookie File");
                sb.AppendLine("# Generated by CinecorePlayer2025 (WebView2 export)");
                sb.AppendLine();

                foreach (var c in cookies)
                {
                    var name = ReadCookieStringProp(c, "Name");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var value = ReadCookieStringProp(c, "Value") ?? string.Empty;
                    var domain = ReadCookieStringProp(c, "Domain") ?? ".youtube.com";
                    var path = ReadCookieStringProp(c, "Path") ?? "/";

                    bool secure = ReadCookieBoolProp(c, "IsSecure");
                    bool includeSubdomains = domain.StartsWith(".", StringComparison.Ordinal);

                    long exp = ReadCookieExpiresUnix(c);

                    sb.Append(domain).Append('\t')
                      .Append(includeSubdomains ? "TRUE" : "FALSE").Append('\t')
                      .Append(path).Append('\t')
                      .Append(secure ? "TRUE" : "FALSE").Append('\t')
                      .Append(exp).Append('\t')
                      .Append(name).Append('\t')
                      .Append(value).AppendLine();
                }

                File.WriteAllText(_cookieFilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static long ReadCookieExpiresUnix(object cookie)
        {
            try
            {
                var p = cookie.GetType().GetProperty("Expires");
                if (p == null) return 0;

                var v = p.GetValue(cookie);
                if (v == null) return 0;

                // WebView2: DateTimeOffset
                if (v is DateTimeOffset dto)
                {
                    long unix = dto.ToUnixTimeSeconds();
                    return unix < 0 ? 0 : unix;
                }

                if (v is DateTime dt)
                {
                    long unix = new DateTimeOffset(dt).ToUnixTimeSeconds();
                    return unix < 0 ? 0 : unix;
                }
            }
            catch { }

            return 0;
        }

        private static string? ReadCookieStringProp(object cookie, string prop)
        {
            try
            {
                var p = cookie.GetType().GetProperty(prop);
                if (p == null) return null;
                var v = p.GetValue(cookie);
                return v?.ToString();
            }
            catch { return null; }
        }

        private static bool ReadCookieBoolProp(object cookie, string prop)
        {
            try
            {
                var p = cookie.GetType().GetProperty(prop);
                if (p == null) return false;
                var v = p.GetValue(cookie);
                return v is bool b && b;
            }
            catch { return false; }
        }

        // ======================
        // WinINet (legacy)
        // ======================

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool InternetGetCookieEx(string url, string? cookieName, StringBuilder cookieData,
            ref int size, int flags, IntPtr reserved);

        private const int INTERNET_COOKIE_HTTPONLY = 0x00002000;

        private static string? GetWinINetCookieHeader(string url)
        {
            try
            {
                int size = 4096;
                var sb = new StringBuilder(size);

                if (!InternetGetCookieEx(url, null, sb, ref size, INTERNET_COOKIE_HTTPONLY, IntPtr.Zero))
                {
                    if (size <= 0 || size > 1024 * 1024) return null;
                    sb = new StringBuilder(size);
                    if (!InternetGetCookieEx(url, null, sb, ref size, INTERNET_COOKIE_HTTPONLY, IntPtr.Zero))
                        return null;
                }

                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
