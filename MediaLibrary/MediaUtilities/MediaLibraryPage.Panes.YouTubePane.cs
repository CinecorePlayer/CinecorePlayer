using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using System;

#nullable enable

namespace CinecorePlayer2025
{
    internal sealed partial class MediaLibraryPage
    {
        // ------------ YouTube Pane (sorgente "YouTube") ------------
        //
        // Obiettivo:
        // - UI totalmente integrata in stile "libreria": card/griglia + navigazione DPAD.
        // - Ricerca integrata: SearchBox dell'header quando _selSrc == "YouTube".
        // - Esplora/Tendenze e "Per te" popolati senza mostrare la pagina web.
        // - Login/consenso/captcha: SOLO se necessario apriamo una overlay WebView2 IN-APP (stessa finestra).
        //
        // Nota:
        // - Il feed (Esplora/Per te) usa l'endpoint interno youtubei (Innertube) per evitare HTML variabile.
        // - Se WebView2 non è disponibile/inizializzabile, search + explore funzionano comunque (best-effort),
        //   ma login/consenso non possono essere risolti senza un browser moderno.
        private sealed class YouTubePane : Panel
        {
            private readonly Label _title;
            private readonly Label _status;
            private readonly FlowLayoutPanel _results;
            private readonly Action<string> _play;

            private CancellationTokenSource? _cts;
            private string _lastQuery = "";
            private bool _firstLoadDone;

            // Modalità: false=Esplora (pubblico), true=Per te (home)
            private bool _personalMode;

            // Throttle download thumbnails (evita picchi CPU/I/O)
            private static readonly SemaphoreSlim _thumbGate = new SemaphoreSlim(6, 6);

            // Cookie/sessione HTTP separata dal browser esterno.
            private static readonly CookieContainer _ytCookies = new CookieContainer();
            private static readonly HttpClient _ytHttp = CreateYouTubeHttpClient(_ytCookies);

            // Cache Innertube (apiKey + clientVersion + visitorData) (cambiano raramente)
            private static InnertubeBootstrap? _bootstrapCache;
            private static DateTime _bootstrapCacheAtUtc;
            private static readonly object _bootstrapLock = new object();

            // Auth (derivata dai cookie WebView2, se login riuscito)
            private volatile string? _authSapisid; // SAPISID o __Secure-3PAPISID

            // Overlay WebView2 integrata (solo per login/consenso/captcha)
            private Panel? _webOverlay;
            private Control? _webView; // WebView2 control via reflection
            private Label? _webTitle;
            private Button? _webCloseBtn;
            private string _webUserDataFolder = string.Empty;

            // Overlay sizing: questo pane vive dentro una FlowLayoutPanel (dock ignorato).
            // Quando apriamo l'overlay WebView2 dobbiamo espandere temporaneamente il pane
            // all'altezza del viewport, altrimenti l'area del WebView resta alta pochi pixel e sembra "vuota".
            private bool _overlayMode;
            private int _overlayPrevHeight;
            private Size _overlayPrevMinSize;

            public YouTubePane(Action<string> onPlay)
            {
                _play = onPlay;

                BackColor = Color.Black;
                TabStop = false; // il focus deve andare sulle card, non sul contenitore

                Padding = new Padding(16, 12, 16, 12);
                Margin = new Padding(0, 0, 0, 0);

                _title = new Label
                {
                    Text = "YouTube",
                    Dock = DockStyle.Top,
                    Height = 24,
                    Font = new Font("Segoe UI Semibold", 12f),
                    ForeColor = Theme.Text,
                    BackColor = Color.Black
                };

                _status = new Label
                {
                    Text = "Usa la barra di ricerca in alto per cercare su YouTube.",
                    Dock = DockStyle.Top,
                    AutoSize = false,
                    Height = 18,
                    Font = new Font("Segoe UI", 9.5f),
                    ForeColor = Theme.SubtleText,
                    BackColor = Color.Black
                };

                _results = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    AutoScroll = false,
                    WrapContents = true,
                    FlowDirection = FlowDirection.LeftToRight,
                    BackColor = Color.Black,
                    Padding = new Padding(0),
                    Margin = new Padding(0, 10, 0, 0)
                };

                Controls.Add(_results);
                Controls.Add(_status);
                Controls.Add(_title);

                // width/height: siamo inseriti dentro una FlowLayoutPanel (_grid).
                // La FlowLayoutPanel non rispetta Dock/Anchor, quindi ci adattiamo manualmente.
                ParentChanged += (_, __) => HookParentResize();
                SizeChanged += (_, __) =>
                {
                    UpdateInternalLayoutSizes();
                    EnsureOverlayBounds();
                };

                // Best-effort: pre-set cookie "consent" per evitare alcuni interstitial EU
                TrySeedConsentCookies();
            }

            public void CancelPending()
            {
                try { _cts?.Cancel(); } catch { }
            }

            /// <summary>
            /// Chiamata dall'host (MediaLibraryPage) quando l'utente preme "Esplora/Tendenze".
            /// </summary>
            public void HostShowTrending(bool force = false)
            {
                _personalMode = false;

                if (force)
                {
                    _firstLoadDone = false;
                    _lastQuery = Guid.NewGuid().ToString("N");
                }

                HostSetQuery(string.Empty);
            }

            /// <summary>
            /// Chiamata dall'host quando l'utente preme "Per te".
            /// </summary>
            public void HostShowPersonal()
            {
                _personalMode = true;

                // Se non siamo loggati, mostriamo comunque l'home pubblico ma segnaliamo che non è personalizzato.
                // Se YouTube richiede consenso/captcha, proponiamo overlay.
                HostSetQuery(string.Empty);
            }

            /// <summary>
            /// Chiamata dall'host quando l'utente preme "Accedi".
            /// </summary>
            public void HostShowLogin()
            {
                _personalMode = true;

                // Apri overlay WebView2 DENTRO il pane (stessa finestra).
                // Alla chiusura, sincronizza i cookie e ricarica Per te.
                ShowWebOverlay(BuildYtSignInUrl(), title: "YouTube · Accedi", clearProfileOnClose: false, afterClose: () =>
                {
                    // Ricarica home (Per te)
                    HostShowPersonal();
                });
            }

            // Back-compat: alcuni caller (Header) usano HostLogin()
            public void HostLogin() => HostShowLogin();

            /// <summary>
            /// Chiamata dall'host quando l'utente preme "Esci".
            /// </summary>
            public void HostLogout()
            {
                // Pulisce stato e profilo WebView2 e cookie HTTP locali
                try { _authSapisid = null; } catch { }

                try
                {
                    lock (_bootstrapLock)
                    {
                        // non resettiamo bootstrap: non è legato al login
                    }
                }
                catch { }

                try
                {
                    _ytCookies?.GetCookies(new Uri("https://www.youtube.com")).Cast<Cookie>().ToList().ForEach(c =>
                    {
                        try
                        {
                            c.Expired = true;
                        }
                        catch { }
                    });
                }
                catch { }

                // Clear WebView2 profile (se creato)
                if (!string.IsNullOrWhiteSpace(_webUserDataFolder))
                {
                    TryDeleteDir(_webUserDataFolder);
                }

                // chiude eventuale overlay
                HideWebOverlay();

                // torna a esplora
                HostShowTrending(force: true);
            }

            private static string BuildYtHomeUrl()
                => "https://www.youtube.com/?hl=it&gl=IT&persist_hl=1&persist_gl=1&ucbcb=1";

            private static string BuildYtExploreUrl()
                => "https://www.youtube.com/feed/explore?hl=it&gl=IT&ucbcb=1";

            private static string BuildYtSignInUrl()
                => "https://www.youtube.com/signin?next=%2F&hl=it&app=desktop&action_handle_signin=true&ucbcb=1";

            private static string BuildYtConsentUrl()
                => "https://consent.youtube.com/?continue=https%3A%2F%2Fwww.youtube.com%2F%3Fucbcb%3D1&hl=it";

            // -------------------------
            // Query entrypoint (host)
            // -------------------------
            public void HostSetQuery(string? query)
            {
                query ??= string.Empty;
                query = query.Trim();

                // evita richieste duplicate
                if (_firstLoadDone && string.Equals(_lastQuery, query, StringComparison.Ordinal))
                    return;

                _lastQuery = query;
                _firstLoadDone = true;

                // cancella fetch precedente
                try { _cts?.Cancel(); } catch { }
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                // UI: stato "loading"
                try
                {
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        _status.Text = _personalMode
                            ? "Carico \"Per te\"…"
                            : "Carico Esplora…";
                    }
                    else
                    {
                        _status.Text = "Ricerca in corso…";
                    }

                    _results.Controls.Clear();
                    _status.Visible = true;
                    UpdateInternalLayoutSizes();
                }
                catch { }

                _ = LoadAsync(query, ct);
            }

            private async Task LoadAsync(string query, CancellationToken ct)
            {
                try
                {
                    List<YouTubeVideo> vids;

                    if (string.IsNullOrWhiteSpace(query))
                    {
                        if (_personalMode)
                        {
                            vids = await FetchHomeAsync(ct).ConfigureAwait(false);
                            if (ct.IsCancellationRequested) return;

                            ApplyResultsOnUiThread(
                                vids,
                                modeTitle: _authSapisid != null ? "YouTube · Per te" : "YouTube · Per te (pubblico)",
                                emptyHint: _authSapisid == null ? "Non sei loggato. Premi Accedi per personalizzare." : null
                            );
                        }
                        else
                        {
                            vids = await FetchExploreAsync(ct).ConfigureAwait(false);
                            if (ct.IsCancellationRequested) return;

                            ApplyResultsOnUiThread(vids, modeTitle: "YouTube · Esplora");
                        }
                    }
                    else
                    {
                        vids = await FetchSearchAsync(query, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;

                        ApplyResultsOnUiThread(vids, modeTitle: $"YouTube · Risultati per \"{query}\"");
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignorata
                }
                catch (YouTubeConsentOrCaptchaException ex)
                {
                    // YouTube ha chiesto consenso o ha bloccato (captcha/traffic).
                    try
                    {
                        if (!IsDisposed)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                _results.Controls.Clear();
                                _status.Text = ex.UserMessage;
                                _status.Visible = true;
                                RecomputeHeight();
                            }));
                        }
                    }
                    catch { }

                    // FIX: se YouTube chiede consenso/captcha, apriamo l'overlay integrata per sbloccare
                    // (vale sia per Esplora/Tendenze che per Per te, e anche per la ricerca).
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) return;

                        string openUrl;
                        if (ex.UserMessage.IndexOf("consenso", StringComparison.OrdinalIgnoreCase) >= 0)
                            openUrl = BuildYtConsentUrl();
                        else if (_personalMode)
                            openUrl = BuildYtHomeUrl();
                        else
                            openUrl = BuildYtExploreUrl();

                        // In caso di ricerca, spesso il captcha si risolve su home.
                        if (!string.IsNullOrWhiteSpace(query))
                            openUrl = BuildYtHomeUrl();

                        BeginInvoke(new Action(() =>
                        {
                            // Evita di aprire più overlay in cascata
                            if (_webOverlay != null) return;

                            ShowWebOverlay(openUrl, title: "YouTube · Sblocco (captcha/consenso)", clearProfileOnClose: false, afterClose: () =>
                            {
                                // Dopo sblocco, riprova lo stesso contenuto
                                if (!string.IsNullOrWhiteSpace(query))
                                {
                                    HostSetQuery(query);
                                }
                                else if (_personalMode)
                                {
                                    HostShowPersonal();
                                }
                                else
                                {
                                    HostShowTrending(force: true);
                                }
                            });
                        }));
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (!IsDisposed)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                _status.Text = "Impossibile caricare YouTube (rete/consenso/captcha).";
                                _results.Controls.Clear();
                                _status.Visible = true;
                                RecomputeHeight();
                            }));
                        }
                    }
                    catch { }

                    Debug.WriteLine(ex);
                }
            }

            private void ApplyResultsOnUiThread(List<YouTubeVideo> videos, string modeTitle, string? emptyHint = null)
            {
                if (IsDisposed) return;

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;

                        _title.Text = modeTitle;
                        _results.Controls.Clear();

                        if (videos == null || videos.Count == 0)
                        {
                            _status.Text = emptyHint ?? "Nessun risultato.";
                            _status.Visible = true;
                            RecomputeHeight();
                            return;
                        }

                        _status.Text = $"{videos.Count} video";
                        _status.Visible = true;

                        // Render cards
                        foreach (var v in videos)
                        {
                            var card = new YouTubeCard(
                                v,
                                onOpen: () => _play(v.WatchUrl),
                                thumbLoader: (ct) => LoadThumbAsync(v.ThumbUrl, ct));

                            _results.Controls.Add(card);
                        }

                        UpdateInternalLayoutSizes();
                    }));
                }
                catch { }
            }

            // -------------------------
            // Layout helpers
            // -------------------------

            private void HookParentResize()
            {
                try
                {
                    if (Parent == null) return;
                    Parent.SizeChanged -= ParentOnSizeChanged;
                    Parent.SizeChanged += ParentOnSizeChanged;
                }
                catch { }

                UpdateInternalLayoutSizes();
            }

            private void ParentOnSizeChanged(object? sender, EventArgs e)
            {
                UpdateInternalLayoutSizes();
                EnsureOverlayBounds();
            }

            private void UpdateInternalLayoutSizes()
            {
                try
                {
                    // larghezza disponibile nella griglia (togli padding interno della griglia)
                    int parentW;
                    if (Parent is ScrollableControl sc)
                        parentW = sc.DisplayRectangle.Width;
                    else
                        parentW = Parent?.ClientSize.Width ?? Width;

                    parentW = Math.Max(320, parentW);

                    // lascia un piccolo margine per evitare "tagli" con scrollbar
                    int targetW = Math.Max(320, parentW - 6);
                    if (Width != targetW)
                        Width = targetW;

                    int innerW = Math.Max(320, Width - Padding.Horizontal);
                    _results.MinimumSize = new Size(innerW, 0);
                    _results.MaximumSize = new Size(innerW, 0);
                    _results.Width = innerW;

                    // ridimensiona l'altezza del pane in base ai contenuti
                    if (_overlayMode)
                    {
                        ForceOverlayHeight();
                        EnsureOverlayBounds();
                        return;
                    }

                    RecomputeHeight();
                }
                catch { }
            }

            private void RecomputeHeight()
            {
                try
                {
                    if (_overlayMode)
                    {
                        ForceOverlayHeight();
                        return;
                    }

                    int h = Padding.Top + Padding.Bottom;
                    h += _title.Height;
                    h += _status.Height;
                    h += 10; // gap

                    // preferita della flow risultati (dipende dalla width)
                    var pref = _results.GetPreferredSize(new Size(_results.Width, 0));
                    h += pref.Height;

                    // evita altezza 0 mentre carica
                    h = Math.Max(h, 160);

                    if (Height != h)
                        Height = h;
                }
                catch { }
            }

            // -------------------------
            // HTTP / Innertube
            // -------------------------

            private static HttpClient CreateYouTubeHttpClient(CookieContainer cookies)
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    UseCookies = true,
                    CookieContainer = cookies,
                    AllowAutoRedirect = true
                };

                // NB: dispose handler con client perché è static: non lo disponiamo mai esplicitamente.
                var http = new HttpClient(handler);
                http.Timeout = TimeSpan.FromSeconds(20);
                return http;
            }

            private void TrySeedConsentCookies()
            {
                try
                {
                    var u = new Uri("https://www.youtube.com/");
                    // Questi valori non garantiscono bypass al 100%, ma aiutano spesso in EU.
                    _ytCookies.Add(u, new Cookie("CONSENT", "YES+1", "/", ".youtube.com"));
                    _ytCookies.Add(u, new Cookie("SOCS", "CAI", "/", ".youtube.com"));
                    _ytCookies.Add(u, new Cookie("PREF", "hl=it&gl=IT", "/", ".youtube.com"));
                }
                catch { }
            }

            private static async Task<string> GetHtmlAsync(string url, CancellationToken ct)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);

                // user-agent "browser" (YouTube filtra in base a UA)
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept-Language", "it-IT,it;q=0.9,en-US;q=0.8,en;q=0.7");
                req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
                req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");

                using var resp = await _ytHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                // Captcha/traffic spesso ritorna 429 o 403
                if ((int)resp.StatusCode == 429 || resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new YouTubeConsentOrCaptchaException("YouTube ha bloccato le richieste (captcha/traffico). Premi Accedi per sbloccare.");

                resp.EnsureSuccessStatusCode();
                var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (LooksLikeConsent(html))
                    throw new YouTubeConsentOrCaptchaException("YouTube richiede il consenso cookie. Premi Accedi per accettare.");

                if (LooksLikeCaptcha(html))
                    throw new YouTubeConsentOrCaptchaException("YouTube richiede una verifica (captcha). Premi Accedi per completarla.");

                return html;
            }

            private static bool LooksLikeConsent(string html)
            {
                if (string.IsNullOrWhiteSpace(html)) return false;
                // EU consent interstitial tipico
                return html.IndexOf("consent.youtube.com", StringComparison.OrdinalIgnoreCase) >= 0
                    || html.IndexOf("Before you continue", StringComparison.OrdinalIgnoreCase) >= 0
                    || html.IndexOf("prima di continuare", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static bool LooksLikeCaptcha(string html)
            {
                if (string.IsNullOrWhiteSpace(html)) return false;
                return html.IndexOf("unusual traffic", StringComparison.OrdinalIgnoreCase) >= 0
                    || html.IndexOf("/sorry/index", StringComparison.OrdinalIgnoreCase) >= 0
                    || html.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static async Task<InnertubeBootstrap> GetInnertubeBootstrapAsync(CancellationToken ct)
            {
                lock (_bootstrapLock)
                {
                    if (_bootstrapCache != null && (DateTime.UtcNow - _bootstrapCacheAtUtc) < TimeSpan.FromMinutes(20))
                        return _bootstrapCache;
                }

                var html = await GetHtmlAsync(BuildYtHomeUrl(), ct).ConfigureAwait(false);

                var apiKey = RegexMatch1(html, "INNERTUBE_API_KEY\"\\s*:\\s*\"([^\"]+)\"");
                var clientVer = RegexMatch1(html, "INNERTUBE_CLIENT_VERSION\"\\s*:\\s*\"([^\"]+)\"");
                var clientNameNum = RegexMatch1(html, "INNERTUBE_CLIENT_NAME\"\\s*:\\s*(\\d+)");
                var visitor = RegexMatch1(html, "VISITOR_DATA\"\\s*:\\s*\"([^\"]+)\"");

                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(clientVer))
                {
                    // In alcune varianti la config è in ytcfg.set({...})
                    // fallback: prova a cercare "innertubeApiKey"
                    apiKey = apiKey ?? RegexMatch1(html, "innertubeApiKey\"\\s*:\\s*\"([^\"]+)\"");
                    clientVer = clientVer ?? RegexMatch1(html, "innertubeClientVersion\"\\s*:\\s*\"([^\"]+)\"");
                    clientNameNum = clientNameNum ?? RegexMatch1(html, "innertubeClientName\"\\s*:\\s*(\\d+)");
                }

                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(clientVer))
                    throw new YouTubeConsentOrCaptchaException("Impossibile leggere la configurazione YouTube (consenso/captcha). Premi Accedi.");

                var bs = new InnertubeBootstrap(
                    apiKey!,
                    clientVer!,
                    string.IsNullOrWhiteSpace(clientNameNum) ? "1" : clientNameNum!,
                    visitor ?? string.Empty
                );

                lock (_bootstrapLock)
                {
                    _bootstrapCache = bs;
                    _bootstrapCacheAtUtc = DateTime.UtcNow;
                }

                return bs;
            }

            private static string? RegexMatch1(string s, string pattern)
            {
                try
                {
                    var m = Regex.Match(s, pattern, RegexOptions.Singleline);
                    if (m.Success && m.Groups.Count > 1)
                        return m.Groups[1].Value;
                }
                catch { }
                return null;
            }

            private async Task<string> PostInnertubeAsync(InnertubeBootstrap bs, string endpoint, string jsonBody, CancellationToken ct, bool addAuth)
            {
                var url = $"https://www.youtube.com/youtubei/v1/{endpoint}?key={Uri.EscapeDataString(bs.ApiKey)}&prettyPrint=false";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);

                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("Accept-Language", "it-IT,it;q=0.9,en-US;q=0.8,en;q=0.7");
                req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                req.Headers.TryAddWithoutValidation("X-Youtube-Client-Name", bs.ClientNameNumeric);
                req.Headers.TryAddWithoutValidation("X-Youtube-Client-Version", bs.ClientVersion);
                if (!string.IsNullOrWhiteSpace(bs.VisitorData))
                    req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", bs.VisitorData);

                if (addAuth)
                {
                    var sapisid = _authSapisid;
                    if (!string.IsNullOrWhiteSpace(sapisid))
                    {
                        var auth = ComputeSapisidHash(sapisid!, "https://www.youtube.com");
                        req.Headers.TryAddWithoutValidation("Authorization", "SAPISIDHASH " + auth);
                        req.Headers.TryAddWithoutValidation("X-Origin", "https://www.youtube.com");
                    }
                }

                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var resp = await _ytHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                if ((int)resp.StatusCode == 429 || resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new YouTubeConsentOrCaptchaException("YouTube ha bloccato le richieste (captcha/traffico). Premi Accedi per sbloccare.");

                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // Alcuni blocchi vengono restituiti come JSON con pagina di "sorry"/consent in HTML dentro.
                if (json.IndexOf("consent.youtube.com", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new YouTubeConsentOrCaptchaException("YouTube richiede il consenso cookie. Premi Accedi per accettare.");

                if (json.IndexOf("unusual traffic", StringComparison.OrdinalIgnoreCase) >= 0 || json.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new YouTubeConsentOrCaptchaException("YouTube richiede una verifica (captcha). Premi Accedi.");

                return json;
            }

            private static string ComputeSapisidHash(string sapisid, string origin)
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string input = ts.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + sapisid + " " + origin;

                using var sha1 = SHA1.Create();
                var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));

                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));

                return ts.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + sb.ToString();
            }

            // -------------------------
            // Feed fetchers (cards)
            // -------------------------

            private async Task<List<YouTubeVideo>> FetchExploreAsync(CancellationToken ct)
            {
                // Preferito: Innertube browse FEexplore (robusto)
                try
                {
                    var bs = await GetInnertubeBootstrapAsync(ct).ConfigureAwait(false);
                    var body = BuildInnertubeBrowseBody(browseId: "FEexplore", clientVersion: bs.ClientVersion, visitorData: bs.VisitorData);
                    var json = await PostInnertubeAsync(bs, "browse", body, ct, addAuth: false).ConfigureAwait(false);
                    return ParseVideosFromJson(json, max: 60);
                }
                catch (YouTubeConsentOrCaptchaException) { throw; }
                catch
                {
                    // Fallback: HTML + scraping best-effort
                    var html = await GetHtmlAsync(BuildYtExploreUrl(), ct).ConfigureAwait(false);
                    if (TryExtractYtInitialData(html, out var initial))
                        return ParseVideosFromJson(initial, max: 60);

                    return ParseVideosFromLooseHtml(html, max: 48);
                }
            }

            private async Task<List<YouTubeVideo>> FetchHomeAsync(CancellationToken ct)
            {
                // Preferito: Innertube browse FEwhat_to_watch
                bool addAuth = !string.IsNullOrWhiteSpace(_authSapisid);

                try
                {
                    var bs = await GetInnertubeBootstrapAsync(ct).ConfigureAwait(false);
                    var body = BuildInnertubeBrowseBody(browseId: "FEwhat_to_watch", clientVersion: bs.ClientVersion, visitorData: bs.VisitorData);
                    var json = await PostInnertubeAsync(bs, "browse", body, ct, addAuth: addAuth).ConfigureAwait(false);
                    return ParseVideosFromJson(json, max: 60);
                }
                catch (YouTubeConsentOrCaptchaException) { throw; }
                catch
                {
                    // Fallback: HTML home
                    var html = await GetHtmlAsync(BuildYtHomeUrl(), ct).ConfigureAwait(false);
                    if (TryExtractYtInitialData(html, out var initial))
                        return ParseVideosFromJson(initial, max: 60);

                    return ParseVideosFromLooseHtml(html, max: 48);
                }
            }

            private static async Task<List<YouTubeVideo>> FetchSearchAsync(string query, CancellationToken ct)
            {
                var url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query) + "&hl=it&gl=IT&ucbcb=1";
                var html = await GetHtmlAsync(url, ct).ConfigureAwait(false);

                if (!TryExtractYtInitialData(html, out var json))
                    return ParseVideosFromLooseHtml(html, max: 60);

                return ParseVideosFromJson(json, max: 60);
            }

            private static string BuildInnertubeBrowseBody(string browseId, string clientVersion, string visitorData)
            {
                // Contesto WEB. clientVersion/visitorData vengono dalla bootstrap config (ytcfg).
                // visitorData spesso aiuta a stabilizzare le risposte lato server (anti-bot).
                var obj = new
                {
                    context = new
                    {
                        client = new
                        {
                            clientName = "WEB",
                            clientVersion = clientVersion,
                            hl = "it",
                            gl = "IT",
                            visitorData = visitorData
                        }
                    },
                    browseId = browseId
                };

                return JsonSerializer.Serialize(obj);
            }

            // -------------------------
            // JSON parsing
            // -------------------------

            private static List<YouTubeVideo> ParseVideosFromJson(string json, int max)
            {
                var outList = new List<YouTubeVideo>(capacity: Math.Max(8, max));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using var doc = JsonDocument.Parse(json);
                TraverseForVideoRenderers(doc.RootElement, outList, seen, max);
                return outList;
            }

            private static void TraverseForVideoRenderers(JsonElement el, List<YouTubeVideo> list, HashSet<string> seen, int max)
            {
                if (list.Count >= max)
                    return;

                switch (el.ValueKind)
                {
                    case JsonValueKind.Object:
                        {
                            // renderer comuni
                            string[] keys =
                            {
                                "videoRenderer",
                                "gridVideoRenderer",
                                "compactVideoRenderer",
                                "playlistVideoRenderer",
                                "videoWithContextRenderer",
                                "reelItemRenderer",
                                "shortsLockupViewModel"
                            };

                            foreach (var k in keys)
                            {
                                if (el.TryGetProperty(k, out var vr))
                                {
                                    var v = TryParseVideoLike(vr);
                                    if (v != null && seen.Add(v.VideoId))
                                    {
                                        list.Add(v);
                                        if (list.Count >= max) return;
                                    }
                                }
                            }

                            // Fallback: oggetto con videoId
                            if (el.TryGetProperty("videoId", out _))
                            {
                                var v2 = TryParseVideoLike(el);
                                if (v2 != null && seen.Add(v2.VideoId))
                                {
                                    list.Add(v2);
                                    if (list.Count >= max) return;
                                }
                            }

                            foreach (var p in el.EnumerateObject())
                            {
                                TraverseForVideoRenderers(p.Value, list, seen, max);
                                if (list.Count >= max) return;
                            }
                            return;
                        }

                    case JsonValueKind.Array:
                        foreach (var item in el.EnumerateArray())
                        {
                            TraverseForVideoRenderers(item, list, seen, max);
                            if (list.Count >= max) return;
                        }
                        return;
                }
            }

            private static YouTubeVideo? TryParseVideoLike(JsonElement vr)
            {
                // Supporta varianti: videoRenderer e reelItemRenderer, ecc.
                string? id = null;

                if (vr.TryGetProperty("videoId", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    id = idEl.GetString();

                // shortsLockupViewModel: spesso usa "entityId" o nested
                if (string.IsNullOrWhiteSpace(id))
                {
                    if (vr.TryGetProperty("onTap", out var onTap)
                        && onTap.ValueKind == JsonValueKind.Object
                        && onTap.TryGetProperty("innertubeCommand", out var cmd)
                        && cmd.ValueKind == JsonValueKind.Object
                        && cmd.TryGetProperty("watchEndpoint", out var we)
                        && we.ValueKind == JsonValueKind.Object
                        && we.TryGetProperty("videoId", out var vid2)
                        && vid2.ValueKind == JsonValueKind.String)
                    {
                        id = vid2.GetString();
                    }
                }

                if (string.IsNullOrWhiteSpace(id) || id!.Length != 11)
                    return null;

                string title = ExtractText(vr, "title");
                if (string.IsNullOrWhiteSpace(title))
                    title = ExtractText(vr, "headline");
                if (string.IsNullOrWhiteSpace(title))
                    title = TryExtractA11yLabel(vr);
                if (string.IsNullOrWhiteSpace(title))
                    title = "(senza titolo)";

                string channel = ExtractText(vr, "ownerText");
                if (string.IsNullOrWhiteSpace(channel))
                    channel = ExtractText(vr, "longBylineText");
                if (string.IsNullOrWhiteSpace(channel))
                    channel = ExtractText(vr, "shortBylineText");

                string duration = ExtractText(vr, "lengthText");
                if (string.IsNullOrWhiteSpace(duration))
                {
                    // reels spesso hanno "thumbnailOverlays" -> "thumbnailOverlayTimeStatusRenderer" -> text
                    duration = TryExtractDurationOverlay(vr);
                }

                string views = ExtractText(vr, "viewCountText");
                string published = ExtractText(vr, "publishedTimeText");

                // thumb stabile
                string thumb = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";

                return new YouTubeVideo
                {
                    VideoId = id,
                    Title = title,
                    Channel = channel,
                    Duration = duration,
                    Views = views,
                    Published = published,
                    ThumbUrl = thumb
                };
            }

            private static string TryExtractA11yLabel(JsonElement vr)
            {
                try
                {
                    // title.accessibility.accessibilityData.label
                    if (vr.TryGetProperty("title", out var titleObj)
                        && titleObj.ValueKind == JsonValueKind.Object
                        && titleObj.TryGetProperty("accessibility", out var acc)
                        && acc.ValueKind == JsonValueKind.Object
                        && acc.TryGetProperty("accessibilityData", out var ad)
                        && ad.ValueKind == JsonValueKind.Object
                        && ad.TryGetProperty("label", out var lbl)
                        && lbl.ValueKind == JsonValueKind.String)
                    {
                        var s = lbl.GetString() ?? "";
                        // spesso è "Titolo - canale - X visualizzazioni - ..."
                        // prendiamo la prima parte fino a " - " se presente
                        int cut = s.IndexOf(" - ", StringComparison.Ordinal);
                        if (cut > 0) s = s.Substring(0, cut);
                        return s.Trim();
                    }

                    if (vr.TryGetProperty("accessibilityLabel", out var al) && al.ValueKind == JsonValueKind.String)
                        return (al.GetString() ?? "").Trim();
                }
                catch { }
                return string.Empty;
            }

            private static string TryExtractDurationOverlay(JsonElement vr)
            {
                try
                {
                    if (!vr.TryGetProperty("thumbnailOverlays", out var overlays) || overlays.ValueKind != JsonValueKind.Array)
                        return string.Empty;

                    foreach (var item in overlays.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;

                        if (item.TryGetProperty("thumbnailOverlayTimeStatusRenderer", out var tsr)
                            && tsr.ValueKind == JsonValueKind.Object)
                        {
                            var t = ExtractText(tsr, "text");
                            if (!string.IsNullOrWhiteSpace(t))
                                return t.Trim();
                        }
                    }
                }
                catch { }
                return string.Empty;
            }

            private static string ExtractText(JsonElement parent, string prop)
            {
                try
                {
                    if (!parent.TryGetProperty(prop, out var el))
                        return string.Empty;

                    // Alcune varianti (rare) usano direttamente stringhe.
                    if (el.ValueKind == JsonValueKind.String)
                        return el.GetString() ?? string.Empty;

                    // { simpleText: "..." }
                    if (el.ValueKind == JsonValueKind.Object)
                    {
                        if (el.TryGetProperty("simpleText", out var st) && st.ValueKind == JsonValueKind.String)
                            return st.GetString() ?? string.Empty;

                        // { runs: [ { text: "..." }, ... ] }
                        if (el.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                        {
                            var sb = new StringBuilder();
                            foreach (var r in runs.EnumerateArray())
                            {
                                if (r.ValueKind != JsonValueKind.Object) continue;
                                if (r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                                    sb.Append(t.GetString());
                            }
                            return sb.ToString();
                        }
                    }

                    return string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            // -------------------------
            // ytInitialData extraction (HTML)
            // -------------------------

            private static bool TryExtractYtInitialData(string html, out string json)
            {
                json = string.Empty;
                if (string.IsNullOrWhiteSpace(html))
                    return false;

                string[] markers =
                {
                    "var ytInitialData",
                    "window[\"ytInitialData\"]",
                    "ytInitialData"
                };

                foreach (var m in markers)
                {
                    int idx = 0;
                    while (true)
                    {
                        idx = html.IndexOf(m, idx, StringComparison.Ordinal);
                        if (idx < 0) break;

                        if (TryParseYtInitialDataAssignment(html, idx, out json))
                            return true;

                        idx += m.Length;
                    }
                }

                return false;
            }

            private static bool TryParseYtInitialDataAssignment(string html, int markerIndex, out string json)
            {
                json = string.Empty;

                int eq = html.IndexOf('=', markerIndex);
                if (eq < 0) return false;

                int p = eq + 1;
                while (p < html.Length && char.IsWhiteSpace(html[p])) p++;
                if (p >= html.Length) return false;

                // Variante: JSON.parse('...')
                if (StartsWithAt(html, p, "JSON.parse", StringComparison.Ordinal))
                {
                    int lp = html.IndexOf('(', p);
                    if (lp < 0) return false;
                    int q = lp + 1;
                    while (q < html.Length && char.IsWhiteSpace(html[q])) q++;
                    if (q >= html.Length) return false;

                    char quote = html[q];
                    if (quote != '\'' && quote != '"') return false;

                    int endQ = SkipJsString(html, q);
                    if (endQ <= q || endQ >= html.Length) return false;

                    string arg = html.Substring(q + 1, endQ - q - 1);
                    string unescaped = UnescapeJsString(arg);

                    int firstBrace = unescaped.IndexOf('{');
                    if (firstBrace < 0) return false;

                    json = unescaped.Substring(firstBrace);
                    return true;
                }

                // Variante: oggetto JS literal {...}
                if (html[p] != '{') return false;

                int start = p;
                int depth = 0;
                for (int i = start; i < html.Length; i++)
                {
                    char ch = html[i];

                    if (ch == '"' || ch == '\'')
                    {
                        i = SkipJsString(html, i);
                        continue;
                    }

                    if (ch == '{') depth++;
                    else if (ch == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            json = html.Substring(start, i - start + 1);
                            return true;
                        }
                    }
                }

                return false;
            }

            private static bool StartsWithAt(string s, int index, string value, StringComparison cmp)
            {
                if (index < 0) return false;
                if (index + value.Length > s.Length) return false;
                return s.AsSpan(index, value.Length).Equals(value.AsSpan(), cmp);
            }

            private static int SkipJsString(string s, int startQuoteIndex)
            {
                char quote = s[startQuoteIndex];
                int i = startQuoteIndex + 1;
                while (i < s.Length)
                {
                    char ch = s[i];
                    if (ch == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (ch == quote)
                        return i;
                    i++;
                }
                return s.Length - 1;
            }

            private static string UnescapeJsString(string s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;

                var sb = new System.Text.StringBuilder(s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    char ch = s[i];
                    if (ch != '\\')
                    {
                        sb.Append(ch);
                        continue;
                    }

                    if (i == s.Length - 1) break;
                    char n = s[++i];
                    switch (n)
                    {
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length - 1)
                            {
                                string hex = s.Substring(i + 1, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        case 'x':
                            if (i + 2 <= s.Length - 1)
                            {
                                string hex = s.Substring(i + 1, 2);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int code))
                                {
                                    sb.Append((char)code);
                                    i += 2;
                                }
                            }
                            break;
                        default:
                            sb.Append(n);
                            break;
                    }
                }

                return sb.ToString();
            }

            // Fallback HTML scraping: se ytInitialData non è presente (o viene servita una variante)
            private static List<YouTubeVideo> ParseVideosFromLooseHtml(string html, int max)
            {
                var list = new List<YouTubeVideo>();
                if (string.IsNullOrWhiteSpace(html)) return list;

                var seen = new HashSet<string>(StringComparer.Ordinal);

                // Proviamo a prendere blocchi più ampi, non solo vicinissimo al match:
                // videoId":"XXXXXXXXXXX" spesso è presente anche nelle varianti "light".
                var rxId = new Regex("\\\"videoId\\\":\\\"([a-zA-Z0-9_-]{11})\\\"", RegexOptions.Compiled);
                foreach (Match m in rxId.Matches(html))
                {
                    if (!m.Success) continue;
                    string id = m.Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(id) || id.Length != 11) continue;
                    if (!seen.Add(id)) continue;

                    string title = TryExtractLooseTitle(html, m.Index) ?? "(senza titolo)";
                    list.Add(new YouTubeVideo
                    {
                        VideoId = id,
                        Title = title,
                        Channel = string.Empty,
                        Views = string.Empty,
                        Duration = string.Empty,
                        Published = string.Empty,
                        ThumbUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg"
                    });

                    if (list.Count >= max) break;
                }

                return list;
            }

            private static string? TryExtractLooseTitle(string html, int aroundIndex)
            {
                try
                {
                    // Guardiamo sia indietro che avanti, perché a volte il title è prima del videoId.
                    int start = Math.Max(0, aroundIndex - 1400);
                    int len = Math.Min(2800, Math.Max(0, html.Length - start));
                    if (len <= 0) return null;
                    string snip = html.Substring(start, len);

                    // title":{"runs":[{"text":"..."}]}
                    var rxRuns = new Regex("\\\"title\\\"\\s*:\\s*\\{\\s*\\\"runs\\\"\\s*:\\s*\\[\\s*\\{\\s*\\\"text\\\"\\s*:\\s*\\\"(.*?)\\\"", RegexOptions.Singleline);
                    var m1 = rxRuns.Match(snip);
                    if (m1.Success)
                        return UnescapeJsString(m1.Groups[1].Value).Trim();

                    // title":{"simpleText":"..."}
                    var rxSimple = new Regex("\\\"title\\\"\\s*:\\s*\\{\\s*\\\"simpleText\\\"\\s*:\\s*\\\"(.*?)\\\"", RegexOptions.Singleline);
                    var m2 = rxSimple.Match(snip);
                    if (m2.Success)
                        return UnescapeJsString(m2.Groups[1].Value).Trim();

                    // headline":{"simpleText":"..."} (shorts)
                    var rxHead = new Regex("\\\"headline\\\"\\s*:\\s*\\{\\s*\\\"simpleText\\\"\\s*:\\s*\\\"(.*?)\\\"", RegexOptions.Singleline);
                    var m3 = rxHead.Match(snip);
                    if (m3.Success)
                        return UnescapeJsString(m3.Groups[1].Value).Trim();
                }
                catch { }
                return null;
            }

            // -------------------------
            // Thumbs
            // -------------------------

            private static async Task<Bitmap?> LoadThumbAsync(string url, CancellationToken ct)
            {
                bool acquired = false;
                try
                {
                    await _thumbGate.WaitAsync(ct).ConfigureAwait(false);
                    acquired = true;

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

                    using var resp = await _ytHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();

                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    using var ms = new MemoryStream(bytes);
                    using var img = Image.FromStream(ms);
                    return new Bitmap(img);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    if (acquired)
                    {
                        try { _thumbGate.Release(); } catch { }
                    }
                }
            }

            // -------------------------
            // WebView2 overlay (integrata)
            // -------------------------

            private string GetYtUserDataFolder()
            {
                try
                {
                    string baseDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CinecorePlayer2025",
                        "WebView2");

                    string dir = Path.Combine(baseDir, "YouTube");
                    try { Directory.CreateDirectory(dir); } catch { }
                    return dir;
                }
                catch
                {
                    return string.Empty;
                }
            }

            private static void TryDeleteDir(string path)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                }
                catch { }
            }


            private void EnterOverlayMode()
            {
                try
                {
                    if (_overlayMode) return;

                    _overlayMode = true;
                    _overlayPrevHeight = Height;
                    _overlayPrevMinSize = MinimumSize;

                    ForceOverlayHeight();
                }
                catch { }
            }

            private void ExitOverlayMode()
            {
                try
                {
                    if (!_overlayMode) return;

                    _overlayMode = false;

                    try { MinimumSize = _overlayPrevMinSize; } catch { }
                    try { Height = _overlayPrevHeight; } catch { }

                    // Ricalcola layout normale
                    UpdateInternalLayoutSizes();
                }
                catch { }
            }

            private void ForceOverlayHeight()
            {
                try
                {
                    int viewportH = 0;

                    if (Parent is ScrollableControl sc)
                        viewportH = sc.ClientSize.Height;
                    else if (Parent != null)
                        viewportH = Parent.ClientSize.Height;

                    if (viewportH <= 0)
                        viewportH = Math.Max(320, Height);

                    // Alcuni container sottraggono un po' per scrollbar: teniamoci larghi
                    viewportH = Math.Max(viewportH, 420);

                    if (Height < viewportH)
                        Height = viewportH;

                    // Assicurati che il controllo non collassi
                    MinimumSize = new Size(Width, viewportH);
                }
                catch { }
            }

            private void EnsureOverlayBounds()
            {
                try
                {
                    if (_webOverlay == null || _webOverlay.IsDisposed) return;
                    if (_overlayMode) ForceOverlayHeight();
                    _webOverlay.Dock = DockStyle.Fill;
                    _webOverlay.BringToFront();
                }
                catch { }
            }

            private void HideWebOverlay()
            {
                try
                {
                    if (_webOverlay != null)
                    {
                        // Dispone WebView prima
                        try { _webView?.Dispose(); } catch { }
                        _webView = null;

                        var ov = _webOverlay;
                        _webOverlay = null;
                        try
                        {
                            ov.Parent = null;
                            ov.Dispose();
                        }
                        catch { }
                    }
                }
                catch { }

                // Ripristina altezza/layout normale (anche se l'overlay era già nullo)
                ExitOverlayMode();
            }

            private void ShowWebOverlay(string url, string title, bool clearProfileOnClose, Action? afterClose)
            {
                // Creiamo l'overlay e proviamo ad inizializzare WebView2.
                // Se fallisce, mostriamo un messaggio molto esplicativo nell'UI integrata.
                try
                {
                    // Chiudi overlay precedente
                    HideWebOverlay();

                    _webUserDataFolder = GetYtUserDataFolder();

                    // IMPORTANT: espandi il pane al viewport prima di creare l'overlay
                    EnterOverlayMode();

                    var overlay = new Panel
                    {
                        BackColor = Theme.Nav,
                        Dock = DockStyle.Fill
                    };

                    // Top bar
                    var top = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.PanelAlt };
                    var lbl = new Label
                    {
                        Text = title,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Padding = new Padding(12, 0, 0, 0),
                        ForeColor = Theme.Text,
                        BackColor = Theme.PanelAlt
                    };

                    var btnClose = new Button
                    {
                        Text = "Chiudi",
                        Dock = DockStyle.Right,
                        Width = 110,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Theme.PanelAlt,
                        ForeColor = Theme.Text
                    };
                    btnClose.FlatAppearance.BorderColor = Theme.Border;

                    top.Controls.Add(lbl);
                    top.Controls.Add(btnClose);

                    var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
                    overlay.Controls.Add(host);
                    overlay.Controls.Add(top);

                    Controls.Add(overlay);
                    overlay.BringToFront();

                    _webOverlay = overlay;
                    _webTitle = lbl;
                    _webCloseBtn = btnClose;

                    btnClose.Click += async (_, __) =>
                    {
                        // Sync cookies prima di chiudere
                        try
                        {
                            await TrySyncCookiesFromWebView2Async();
                        }
                        catch { }

                        // Chiudi overlay
                        BeginInvoke(new Action(() =>
                        {
                            HideWebOverlay();
                            if (clearProfileOnClose) TryDeleteDir(_webUserDataFolder);
                            afterClose?.Invoke();
                        }));
                    };

                    // Crea e init WebView2
                    _ = InitWebView2IntoHostAsync(host, url);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    try
                    {
                        _status.Text = "Impossibile mostrare YouTube (overlay).";
                        _status.Visible = true;
                        RecomputeHeight();
                    }
                    catch { }
                }
            }

            private async Task InitWebView2IntoHostAsync(Panel host, string url)
            {
                string err;
                if (!TryPreflightWebView2(out err))
                {
                    // mostra errore in overlay
                    ShowOverlayError(host, err);
                    return;
                }

                try
                {
                    var webViewType = Type.GetType("Microsoft.Web.WebView2.WinForms.WebView2, Microsoft.Web.WebView2.WinForms");
                    if (webViewType == null)
                    {
                        ShowOverlayError(host, "WebView2 WinForms non disponibile: aggiungi il pacchetto NuGet Microsoft.Web.WebView2.");
                        return;
                    }

                    var web = (Control)Activator.CreateInstance(webViewType)!;
                    web.Dock = DockStyle.Fill;
                    host.Controls.Add(web);
                    _webView = web;

                    // Persistenza profilo: user data folder dedicata
                    try
                    {
                        var cpProp = webViewType.GetProperty("CreationProperties");
                        if (cpProp != null && !string.IsNullOrEmpty(_webUserDataFolder))
                        {
                            var cp = Activator.CreateInstance(cpProp.PropertyType);
                            var udf = cpProp.PropertyType.GetProperty("UserDataFolder");
                            udf?.SetValue(cp, _webUserDataFolder);
                            cpProp.SetValue(web, cp);
                        }
                    }
                    catch { }                    // EnsureCoreWebView2Async: scegliamo l'overload giusto senza ambiguità
                    // (nelle versioni recenti esistono più overload: (), (env), (env, options)).
                    object? env = await TryCreateCoreWebView2EnvironmentAsync(_webUserDataFolder);

                    object? tObj = null;

                    var ensureCandidates = webViewType.GetMethods()
                        .Where(mm => mm.Name == "EnsureCoreWebView2Async")
                        .ToArray();

                    MethodInfo? m0 = ensureCandidates.FirstOrDefault(mm => mm.GetParameters().Length == 0);

                    MethodInfo? mEnv1 = ensureCandidates.FirstOrDefault(mm =>
                    {
                        var ps = mm.GetParameters();
                        return ps.Length == 1 && (ps[0].ParameterType.FullName?.Contains("CoreWebView2Environment") ?? false);
                    });

                    MethodInfo? mEnv2 = ensureCandidates.FirstOrDefault(mm =>
                    {
                        var ps = mm.GetParameters();
                        return ps.Length == 2 && (ps[0].ParameterType.FullName?.Contains("CoreWebView2Environment") ?? false);
                    });

                    MethodInfo? mAny1 = ensureCandidates.FirstOrDefault(mm => mm.GetParameters().Length == 1);
                    MethodInfo? mAny2 = ensureCandidates.FirstOrDefault(mm => mm.GetParameters().Length == 2);

                    // 1) Preferisci overload con env (se creato)
                    if (env != null)
                    {
                        if (mEnv1 != null) tObj = mEnv1.Invoke(web, new object?[] { env });
                        else if (mEnv2 != null) tObj = mEnv2.Invoke(web, new object?[] { env, null });
                    }

                    // 2) Overload senza argomenti
                    if (tObj == null && m0 != null)
                        tObj = m0.Invoke(web, null);

                    // 3) Fallback: env opzionale/null
                    if (tObj == null)
                    {
                        if (mEnv1 != null) tObj = mEnv1.Invoke(web, new object?[] { null });
                        else if (mEnv2 != null) tObj = mEnv2.Invoke(web, new object?[] { null, null });
                        else if (mAny1 != null) tObj = mAny1.Invoke(web, new object?[] { null });
                        else if (mAny2 != null) tObj = mAny2.Invoke(web, new object?[] { null, null });
                    }

                    if (tObj is Task t)
                        await t;

                    // Navigate
                    try
                    {
                        var src = webViewType.GetProperty("Source");
                        src?.SetValue(web, new Uri(url));
                    }
                    catch
                    {
                        var nav = webViewType.GetMethod("Navigate", new[] { typeof(string) });
                        nav?.Invoke(web, new object[] { url });
                    }
                }
                catch (Exception ex)
                {
                    var msg = BuildWebView2InitError(ex);
                    ShowOverlayError(host, msg);
                }
            }

            private static string BuildWebView2InitError(Exception ex)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("Impossibile inizializzare WebView2.\n\n");
                    sb.Append(ex.GetType().Name);
                    sb.Append(": ");
                    sb.Append(ex.Message);

                    // HResult utile
                    try
                    {
                        int hr = ex.HResult;
                        sb.Append("\nHResult: 0x");
                        sb.Append(hr.ToString("X8"));
                    }
                    catch { }

                    sb.Append("\n\nPossibili cause:\n");
                    sb.Append("• Runtime WebView2 non installato\n");
                    sb.Append("• NuGet Microsoft.Web.WebView2 mancante / versione vecchia\n");
                    sb.Append("• Cartella profilo non scrivibile (UserDataFolder)\n");
                    sb.Append("• Sistema/criteri che bloccano Edge WebView2\n");

                    sb.Append("\nCosa fare:\n");
                    sb.Append("1) Installa \"Microsoft Edge WebView2 Runtime\"\n");
                    sb.Append("2) Aggiungi/aggiorna il pacchetto NuGet \"Microsoft.Web.WebView2\"\n");
                    sb.Append("3) Verifica che l'app possa scrivere in %LOCALAPPDATA%\\CinecorePlayer2025\\WebView2\n");

                    return sb.ToString();
                }
                catch
                {
                    return "Impossibile inizializzare WebView2.";
                }
            }

            private void ShowOverlayError(Panel host, string message)
            {
                try
                {
                    host.Controls.Clear();
                    var tb = new TextBox
                    {
                        Multiline = true,
                        ReadOnly = true,
                        BorderStyle = BorderStyle.None,
                        Dock = DockStyle.Fill,
                        BackColor = Color.Black,
                        ForeColor = Theme.Text,
                        Font = new Font("Consolas", 10f),
                        Text = message,
                        ScrollBars = ScrollBars.Vertical
                    };
                    host.Controls.Add(tb);
                }
                catch { }
            }

            private static bool TryPreflightWebView2(out string error)
            {
                error = string.Empty;
                try
                {
                    // Se il core non è caricabile, non ha senso provare.
                    var envType = Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core");
                    if (envType == null)
                    {
                        error = "WebView2 Core non disponibile: aggiungi il pacchetto NuGet Microsoft.Web.WebView2.";
                        return false;
                    }

                    // Controllo runtime: GetAvailableBrowserVersionString()
                    var m = envType.GetMethod("GetAvailableBrowserVersionString", new[] { typeof(string) });
                    if (m != null)
                    {
                        try
                        {
                            _ = m.Invoke(null, new object?[] { null });
                            return true;
                        }
                        catch (TargetInvocationException tie)
                        {
                            error = BuildWebView2InitError(tie.InnerException ?? tie);
                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    error = BuildWebView2InitError(ex);
                    return false;
                }
            }

            private static async Task<object?> TryCreateCoreWebView2EnvironmentAsync(string userDataFolder)
            {
                try
                {
                    var envType = Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core");
                    if (envType == null) return null;

                    var optType = Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions, Microsoft.Web.WebView2.Core");

                    object? options = null;
                    if (optType != null)
                    {
                        options = Activator.CreateInstance(optType);
                        // A volte GPU crea problemi: puoi abilitarlo se serve.
                        // var argProp = optType.GetProperty("AdditionalBrowserArguments");
                        // argProp?.SetValue(options, "--disable-gpu");
                    }

                    // Fixed runtime support: se l'app ha una cartella "WebView2FixedRuntime" accanto all'exe,
                    // la usiamo come browserExecutableFolder (evita runtime installato).
                    string? fixedFolder = null;
                    try
                    {
                        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        var candidate = Path.Combine(baseDir, "WebView2FixedRuntime");
                        if (Directory.Exists(candidate)) fixedFolder = candidate;
                    }
                    catch { }

                    // CreateAsync(string browserExecutableFolder, string userDataFolder, CoreWebView2EnvironmentOptions options)
                    var create = envType.GetMethods()
                        .FirstOrDefault(mi =>
                        {
                            if (mi.Name != "CreateAsync") return false;
                            var ps = mi.GetParameters();
                            return ps.Length == 3;
                        });

                    if (create == null) return null;

                    var tObj = create.Invoke(null, new object?[] { fixedFolder, userDataFolder, options });
                    if (tObj is Task t)
                    {
                        await t;
                        var resProp = tObj.GetType().GetProperty("Result");
                        return resProp?.GetValue(tObj);
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            }

            private async Task TrySyncCookiesFromWebView2Async()
            {
                // Importa cookie da WebView2 (profilo interno) verso CookieContainer HTTP,
                // così i feed Innertube possono essere autenticati (Per te davvero personalizzato).
                var web = _webView;
                if (web == null) return;

                try
                {
                    var webType = web.GetType();
                    var coreProp = webType.GetProperty("CoreWebView2");
                    var core = coreProp?.GetValue(web);
                    if (core == null) return;

                    var cmProp = core.GetType().GetProperty("CookieManager");
                    var cm = cmProp?.GetValue(core);
                    if (cm == null) return;

                    var getCookiesAsync = cm.GetType().GetMethod("GetCookiesAsync", new[] { typeof(string) });
                    if (getCookiesAsync == null) return;

                    var tObj = getCookiesAsync.Invoke(cm, new object[] { "https://www.youtube.com" });
                    if (tObj is not Task t) return;

                    await t;

                    var resultProp = tObj.GetType().GetProperty("Result");
                    var result = resultProp?.GetValue(tObj) as System.Collections.IEnumerable;
                    if (result == null) return;

                    string? sapisid = null;

                    foreach (var c in result)
                    {
                        try
                        {
                            var ct = c!.GetType();
                            string name = (string)(ct.GetProperty("Name")?.GetValue(c) ?? "");
                            string value = (string)(ct.GetProperty("Value")?.GetValue(c) ?? "");
                            string domain = (string)(ct.GetProperty("Domain")?.GetValue(c) ?? "");
                            string path = (string)(ct.GetProperty("Path")?.GetValue(c) ?? "/");

                            if (string.IsNullOrWhiteSpace(name)) continue;

                            // CookieContainer vuole domain senza leading dot in alcuni casi; gestiamo entrambi.
                            var cookie = new Cookie(name, value, path, domain.StartsWith(".") ? domain.Substring(1) : domain)
                            {
                                Secure = true
                            };

                            try
                            {
                                _ytCookies.Add(new Uri("https://www.youtube.com/"), cookie);
                            }
                            catch
                            {
                                // fallback: prova su domain specifico
                                try
                                {
                                    _ytCookies.Add(new Uri("https://" + (domain.StartsWith(".") ? domain.Substring(1) : domain) + "/"), cookie);
                                }
                                catch { }
                            }

                            if (string.Equals(name, "SAPISID", StringComparison.OrdinalIgnoreCase))
                                sapisid = value;
                            else if (string.Equals(name, "__Secure-3PAPISID", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(sapisid))
                                sapisid = value;
                        }
                        catch { }
                    }

                    if (!string.IsNullOrWhiteSpace(sapisid))
                        _authSapisid = sapisid;
                }
                catch { }
            }

            // -------------------------
            // Dispose
            // -------------------------
            protected override void Dispose(bool disposing)
            {
                try { _cts?.Cancel(); } catch { }
                _cts = null;

                if (disposing)
                {
                    HideWebOverlay();

                    try
                    {
                        foreach (var c in _results.Controls.OfType<YouTubeCard>())
                        {
                            try { c.Dispose(); } catch { }
                        }
                    }
                    catch { }
                }

                base.Dispose(disposing);
            }

            // -------------------------
            // Types
            // -------------------------

            private sealed class InnertubeBootstrap
            {
                public string ApiKey { get; }
                public string ClientVersion { get; }
                public string ClientNameNumeric { get; }
                public string VisitorData { get; }

                public InnertubeBootstrap(string apiKey, string clientVersion, string clientNameNumeric, string visitorData)
                {
                    ApiKey = apiKey;
                    ClientVersion = clientVersion;
                    ClientNameNumeric = clientNameNumeric;
                    VisitorData = visitorData;
                }
            }

            private sealed class YouTubeConsentOrCaptchaException : Exception
            {
                public string UserMessage { get; }

                public YouTubeConsentOrCaptchaException(string userMessage) : base(userMessage)
                {
                    UserMessage = userMessage;
                }
            }
        }

        private sealed class YouTubeVideo
        {
            public string VideoId = string.Empty;
            public string Title = string.Empty;
            public string Channel = string.Empty;
            public string Duration = string.Empty;
            public string Views = string.Empty;
            public string Published = string.Empty;
            public string ThumbUrl = string.Empty;

            public string WatchUrl => "https://www.youtube.com/watch?v=" + VideoId;
        }

        // ------------ Card YouTube (focusabile + DPAD-friendly) ------------
        // Atomic: niente child controls (così il CollectGridFocusables non entra dentro).
        private sealed class YouTubeCard : Control
        {
            private readonly YouTubeVideo _v;
            private readonly Action _onOpen;
            private readonly Func<CancellationToken, Task<Bitmap?>> _thumbLoader;
            private CancellationTokenSource? _thumbCts;
            private Bitmap? _thumb;
            private bool _hover;

            // layout
            private readonly int _imgHeight = 180;

            public YouTubeCard(YouTubeVideo v, Action onOpen, Func<CancellationToken, Task<Bitmap?>> thumbLoader)
            {
                _v = v;
                _onOpen = onOpen;
                _thumbLoader = thumbLoader;

                Size = new Size(320, 250);
                Margin = new Padding(10, 6, 10, 6);

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);

                BackColor = Theme.Card;
                Cursor = Cursors.Hand;
                TabStop = true;

                Click += (_, __) => _onOpen();
                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; Invalidate(); };

                // start thumb load
                BeginThumbLoad();
            }

            private void BeginThumbLoad()
            {
                try { _thumbCts?.Cancel(); } catch { }
                _thumbCts = new CancellationTokenSource();
                var ct = _thumbCts.Token;

                Task.Run(async () =>
                {
                    Bitmap? bmp = null;
                    try
                    {
                        bmp = await _thumbLoader(ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested)
                        {
                            bmp?.Dispose();
                            return;
                        }

                        if (bmp == null) return;

                        if (IsDisposed)
                        {
                            bmp.Dispose();
                            return;
                        }

                        if (IsHandleCreated)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (IsDisposed)
                                {
                                    try { bmp.Dispose(); } catch { }
                                    return;
                                }

                                try { _thumb?.Dispose(); } catch { }
                                _thumb = bmp;
                                Invalidate();
                            }));
                        }
                        else
                        {
                            try { _thumb?.Dispose(); } catch { }
                            _thumb = bmp;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        bmp?.Dispose();
                    }
                    catch
                    {
                        bmp?.Dispose();
                    }
                }, ct);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);

                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    _onOpen();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                // card bg + border
                using (var bg = new SolidBrush(_hover ? Theme.PanelAlt : Theme.Card))
                    g.FillRectangle(bg, new Rectangle(0, 0, Width - 1, Height - 1));

                using (var penBorder = new Pen(Theme.Border))
                    g.DrawRectangle(penBorder, 0, 0, Width - 1, Height - 1);

                // thumb area
                var imgRect = new Rectangle(0, 0, Width, _imgHeight);
                DrawThumb(g, imgRect);

                // duration badge bottom-right over thumb
                if (!string.IsNullOrWhiteSpace(_v.Duration))
                {
                    using var badgeFont = new Font("Segoe UI Semibold", 8.5f);
                    var badge = " " + _v.Duration.Trim() + " ";
                    var sz = g.MeasureString(badge, badgeFont);

                    int bx = imgRect.Right - (int)sz.Width - 10;
                    int by = imgRect.Bottom - (int)sz.Height - 8;

                    using var brBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                    using var brFg = new SolidBrush(Color.White);

                    g.FillRectangle(brBg, new Rectangle(bx - 2, by - 1, (int)sz.Width + 4, (int)sz.Height + 2));
                    g.DrawString(badge, badgeFont, brFg, bx, by);
                }

                // footer text
                int footerY = _imgHeight;
                int footerH = Height - _imgHeight;
                var footerRect = new Rectangle(0, footerY, Width, footerH);

                using (var footerBg = new SolidBrush(_hover ? Theme.PanelAlt : Color.FromArgb(36, 36, 40)))
                    g.FillRectangle(footerBg, footerRect);

                // title (max 2 righe)
                using var titleFont = new Font("Segoe UI Semibold", 9.8f);
                var titleRect = new Rectangle(10, footerY + 8, Width - 20, 38);
                TextRenderer.DrawText(
                    g,
                    _v.Title,
                    titleFont,
                    titleRect,
                    Color.White,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

                // meta line
                using var metaFont = new Font("Segoe UI", 8.8f);
                string meta = BuildMetaLine();
                var metaRect = new Rectangle(10, footerY + 48, Width - 20, footerH - 54);
                TextRenderer.DrawText(
                    g,
                    meta,
                    metaFont,
                    metaRect,
                    Theme.SubtleText,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            }

            private string BuildMetaLine()
            {
                var parts = new List<string>(3);

                if (!string.IsNullOrWhiteSpace(_v.Channel))
                    parts.Add(_v.Channel.Trim());

                var vp = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(_v.Views))
                    vp.Add(_v.Views.Trim());
                if (!string.IsNullOrWhiteSpace(_v.Published))
                    vp.Add(_v.Published.Trim());
                if (vp.Count > 0)
                    parts.Add(string.Join(" · ", vp));

                return string.Join("  —  ", parts);
            }

            private void DrawThumb(Graphics g, Rectangle dest)
            {
                if (_thumb == null)
                {
                    // placeholder
                    using var br = new SolidBrush(Theme.PanelAlt);
                    g.FillRectangle(br, dest);

                    using var font = new Font("Segoe UI Semibold", 11f);
                    TextRenderer.DrawText(
                        g,
                        "YouTube",
                        font,
                        dest,
                        Theme.SubtleText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }

                try
                {
                    // cover crop (riempi tutto il rettangolo, centrato)
                    float srcW = _thumb.Width;
                    float srcH = _thumb.Height;
                    float dstW = dest.Width;
                    float dstH = dest.Height;

                    float dstAspect = dstW / dstH;
                    float srcAspect = srcW / srcH;

                    RectangleF src;
                    if (srcAspect > dstAspect)
                    {
                        float newW = srcH * dstAspect;
                        float x = (srcW - newW) / 2f;
                        src = new RectangleF(x, 0, newW, srcH);
                    }
                    else
                    {
                        float newH = srcW / dstAspect;
                        float y = (srcH - newH) / 2f;
                        src = new RectangleF(0, y, srcW, newH);
                    }

                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(_thumb, dest, src, GraphicsUnit.Pixel);
                }
                catch
                {
                    try { _thumb?.Dispose(); } catch { }
                    _thumb = null;
                }
            }

            protected override void Dispose(bool disposing)
            {
                try { _thumbCts?.Cancel(); } catch { }
                _thumbCts = null;

                if (disposing)
                {
                    try { _thumb?.Dispose(); } catch { }
                    _thumb = null;
                }

                base.Dispose(disposing);
            }
        }
    }
}
