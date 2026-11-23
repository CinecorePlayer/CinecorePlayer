using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// La parte di rete della libreria: fonte attiva (Mac o un server), pagina Rete (NetworkPage.cs: banner con
/// "Aggiungi server" e "Cerca server", i server come schede larghe), accesso a Jellyfin ed Emby (JellyfinLoginForm,
/// con Quick Connect) e catalogo del server al posto delle cartelle.
/// </summary>
internal sealed partial class LibraryView
{
    /// <summary>Livello sopra la pagina per le caselle di testo vere (lo mette la finestra).</summary>
    public Canvas? InputLayer { get; set; }

    private bool _networkLoading, _discovering;
    private DateTime _lastDiscovery = DateTime.MinValue;
    private string _networkStatus = "";
    private CancellationTokenSource? _catalogCts;
    public event Action? KeychainAuthorizationRequested;

    public void RetrySavedAccess()
    {
        _networkStatus = "";
        Reload();
    }

    /// <summary>Il server in uso come fonte (Impostazioni › Libreria › Fonte predefinita), o null per il Mac.</summary>
    private NetworkServer? ActiveServer =>
        // Solo per le prove: CINECORE_LIBRARY_SOURCE=mac usa le cartelle del Mac senza toccare la scelta salvata.
        Environment.GetEnvironmentVariable("CINECORE_LIBRARY_SOURCE") == "mac" ? null :
        MacSettings.Get("librarySource", "Library") == "DLNA"
            ? NetworkStore.Servers.FirstOrDefault(s => s.Key == MacSettings.Get("libraryServer", "")) : null;

    /// <summary>Impostazioni › Libreria: fonte o server cambiati.</summary>
    public void SourceChangedFromSettings()
    {
        _catalogCts?.Cancel();
        _networkLoading = false; _networkStatus = "";
        _view = "Home"; _category = "Movies";
        _heroDeck.Clear();
        Reload();
    }

    private void SetSource(NetworkServer? server)
    {
        BeginPageTransition();
        _catalogCts?.Cancel();
        _networkLoading = false;
        _networkStatus = "";
        MacSettings.Set("librarySource", server == null ? "Library" : "DLNA");
        MacSettings.Set("libraryServer", server?.Key ?? "");
        if (server != null) NetworkStore.ConnectedKey = server.Key;
        _view = "Home"; _category = "Movies";
        _heroDeck.Clear();
        Reload();
    }

    /// <summary>
    /// Connessione tornata: si richiede tutto cio' che era fallito senza rete (copertine, testi, informazioni, consigli,
    /// catalogo del server) invece di lasciare la pagina senza metadati fino al riavvio.
    /// </summary>
    private void OnNetworkRestored()
    {
        LibraryStore.ForgetNetworkFailures();
        ImageRetry.Clear();
        if (_lyricLines.Count == 0 && _lyricPlain.Length == 0) _lyricsFor = "";
        _fsBackgroundFor = "";
        _albumRecsKey = null;
        if (ActiveServer is { } server)
        {
            NetworkStore.ForgetRefresh(server.Key);
            if (!_networkLoading)
            {
                bool hadError = _networkStatus.Length > 0;
                _networkStatus = "";
                _ = ConnectAsync(server, quiet: NetworkStore.Catalog(server.Key) != null && !hadError);
            }
        }
        if (MusicBarVisible)
        {
            if (_musicView == 4) LoadMusicLyricsIfNeeded();
            if (MusicFullscreenOpen) RequestMusicBackground();
        }
        InvalidateVisual();
    }

    /// <summary>Clic su un server: senza account la scheda di accesso, altrimenti diventa la fonte.</summary>
    private void OpenServer(NetworkServer server)
    {
        if (server.IsJellyfin && server.Account == null) { OpenSignIn(server); return; }
        SetSource(server);
    }

    private async Task ConnectAsync(NetworkServer server, bool quiet = false)
    {
        _catalogCts?.Cancel();
        var cts = _catalogCts = new CancellationTokenSource();
        _networkLoading = !quiet;
        _networkStatus = quiet ? "" : "Caricamento del catalogo…";
        InvalidateVisual();
        try
        {
            // Le letture automatiche del Portachiavi non mostrano finestre di autorizzazione.
            await Task.Run(() => NetworkStore.LoadCatalogAsync(server, partial => Dispatcher.UIThread.Post(() =>
            {
                // Aggiornamento in sottofondo: niente voci che arrivano a pezzi, si sostituisce tutto alla fine.
                if (cts.IsCancellationRequested || quiet) return;
                string cat = _category is "Movies" or "TV Series" or "Videos" or "Photos" or "Music" ? _category : "Movies";
                var next = partial.Where(i => i.Category == cat).ToList();
                // La schermata di caricamento sfuma sul catalogo invece di sparire di colpo (come su Windows).
                if (_items.Count == 0 && next.Count > 0) BeginPageTransition();
                _items = next;
                if (_heroDeck.Count == 0) BuildHeroDeck();
                InvalidateVisual();
            }), cts.Token), cts.Token).WaitAsync(TimeSpan.FromSeconds(server.IsJellyfin ? 20 : 150), cts.Token);
            // Il DLNA elenca cartella per cartella (migliaia di foto e brani): le voci compaiono man mano.
            _networkStatus = "";
        }
        catch (JellyfinClient.UnauthorizedException)
        {
            if (Keychain.AccessRequired)
                _networkStatus = "Le credenziali sono salvate. macOS deve autorizzare questa build ad accedervi.";
            else
            {
                _networkStatus = "Accesso richiesto";
                Dispatcher.UIThread.Post(() => OpenSignIn(server));
            }
        }
        catch (OperationCanceledException) { return; }
        catch (TimeoutException)
        {
            cts.Cancel();
            _networkStatus = "Il server non risponde. Riprova fra poco.";
        }
        catch (Exception ex) { _networkStatus = "Il server non risponde: " + ex.Message; }
        finally { if (_catalogCts == cts) _networkLoading = false; }
        if (quiet)
        {
            // Server spento o lento: resta il catalogo dal disco, senza messaggi e senza riprovare in continuazione.
            // Se la lettura e' rimasta a meta' (rete caduta) si rilegge al ritorno della connessione (OnNetworkRestored).
            NetworkStore.MarkRefreshed(server.Key);
            _networkStatus = "";
            // Stesso banner di prima, con le voci nuove del server (niente cambio di film all'arrivo del catalogo).
            if (NetworkStore.Catalog(server.Key) is { } fresh)
            {
                var byPath = fresh.GroupBy(i => i.Path).ToDictionary(g => g.Key, g => g.First());
                for (int i = 0; i < _heroDeck.Count; i++)
                    if (byPath.TryGetValue(_heroDeck[i].Path, out var same)) _heroDeck[i] = same;
            }
        }
        else _heroDeck.Clear();
        Reload();
    }

    private async Task DiscoverAsync()
    {
        if (_discovering) return;
        _discovering = true;
        InvalidateVisual();
        try { await NetworkStore.DiscoverAsync(TimeSpan.FromSeconds(3)); }
        catch { }
        _discovering = false;
        _lastDiscovery = DateTime.UtcNow;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ pagina Rete
    private static Bitmap? _networkHero;
    private static bool _networkHeroTried;

    private static Bitmap? NetworkHeroPhoto()
    {
        if (_networkHero != null || _networkHeroTried) return _networkHero;
        _networkHeroTried = true;
        try { _networkHero = new Bitmap(Path.Combine(MacTools.AssetsDirectory, "Network", "network-hero.jpg")); } catch { }
        return _networkHero;
    }

    private void DrawNetworkPage(DrawingContext ctx, Rect page)
    {
        double s = _s, bar = BarH, left = page.X + PadX, width = Math.Max(1, page.Width - PadX * 2);
        double viewBottom = Math.Max(bar + 1, page.Bottom - S(16));
        double heroH = Math.Clamp(Math.Round(viewBottom * 0.46), S(300), S(520));
        var hero = new Rect(0, 0, page.Width, heroH);
        double textW = Math.Min(S(640), Math.Max(S(320), width / 2));
        // Sempre la stessa foto del server (il NAS) a destra, con le sfumature del banner della Home.
        using (ctx.PushClip(hero))
        {
            ctx.FillRectangle(B(Back), hero);
            if (NetworkHeroPhoto() is { } photo)
            {
                double k = Math.Max(hero.Width / photo.Size.Width, hero.Height / photo.Size.Height);
                double w = photo.Size.Width * k, h = photo.Size.Height * k;
                using (ctx.PushOpacity(UiTheme.IsLight ? 0.55 : 1))
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(photo, new Rect(hero.Right - w, (hero.Height - h) * 0.3, w, h));
            }
            ctx.FillRectangle(Horizontal(Back, (0, 255), (0.18, 248), (0.34, 210), (0.52, 110), (0.74, 28), (1, 0)), hero);
            double topH = Math.Max(1, Math.Min(hero.Height / 3, 160));
            ctx.FillRectangle(Vertical(Back, (0, 190), (1, 0)), new Rect(0, 0, hero.Width, topH));
            double bottomH = Math.Round(hero.Height * 0.48);
            ctx.FillRectangle(Vertical(Back, (0, 0), (.35, 80), (.7, 200), (1, 255)), new Rect(0, hero.Height - bottomH, hero.Width, bottomH + 1));
        }

        double actionH = S(48), actionTop = hero.Bottom - S(78) - actionH;
        var title = Format("Rete", Math.Max(22, 46 * s), DisplayMedium, Text, textW);
        var line = Format("DLNA e server multimediali", Math.Max(10, 14 * s), DisplayRegular, A(Text, UiTheme.IsLight ? 200 : 215), textW);
        double y = Math.Max(bar + S(12), actionTop - S(34) - (line.Height + S(2)) - S(10) - title.Height);
        ctx.DrawText(title, new Point(left, y));
        y += title.Height + S(10);
        ctx.DrawText(line, new Point(left, y));
        double bx = DrawNetworkHeroButton(ctx, left, actionTop, actionH, "Aggiungi server", "add", true, !_networkLoading, "net-add") + S(14);
        DrawNetworkHeroButton(ctx, bx, actionTop, actionH, _discovering ? "Ricerca…" : "Cerca server", "scan", false, !_discovering, "net-scan");
        string status = _networkLoading ? "Caricamento del catalogo…" : _discovering ? "Cerco i server nella tua rete…"
            : _networkStatus.Length > 0 ? _networkStatus
            : _lastDiscovery == DateTime.MinValue ? "" : "Ultima ricerca alle " + _lastDiscovery.ToLocalTime().ToString("HH:mm");
        if (status.Length > 0) TextAt(ctx, status, Math.Max(8.4, 10 * s), DisplayRegular, UiTheme.Muted, new Rect(left, actionTop + actionH + S(14), width / 2, S(22)));

        DrawServerRows(ctx, new Rect(left, hero.Bottom - S(6), width, Math.Max(1, viewBottom - hero.Bottom + S(6))));
    }

    private double DrawNetworkHeroButton(DrawingContext ctx, double x, double y, double h, string text, string icon, bool primary, bool enabled, string kind)
    {
        bool light = UiTheme.IsLight;
        double pt = Math.Max(9.4, 11.8 * _s);
        double tw = Measure(text, pt, DisplaySemibold), glyph = S(18), glyphGap = S(10), pad = S(26);
        var r = new Rect(x, y, pad * 2 + glyph + glyphGap + tw, h);
        bool hover = enabled && _menu == null && _signIn == null && Hover(r);
        DrawHeroButtonShape(ctx, r, primary, hover);
        Color ink = primary ? (light ? Colors.White : Colors.Black) : Text;
        if (!enabled) ink = A(ink, 120);
        SvgIcon.Get(icon)?.Draw(ctx, new Rect(r.X + pad, r.Y + (r.Height - glyph) / 2, glyph, glyph), ink);
        TextAt(ctx, text, pt, DisplaySemibold, ink, new Rect(r.X + pad + glyph + glyphGap, r.Y, tw + 2, r.Height));
        if (enabled) _hits.Add(new Hit(r, kind));
        return r.Right;
    }

    /// <summary>"I tuoi server" e "Trovati in rete", schede 16:9 come "Continua a guardare".</summary>
    private void DrawServerRows(DrawingContext ctx, Rect area)
    {
        double gap = S(20), titleH = S(34), titleGap = S(14), rowGap = S(30);
        int cols = (int)Math.Clamp(Math.Floor((area.Width + gap) / Math.Max(1, S(300) + gap)), 3, 6);
        double cardW = Math.Max(1, Math.Floor((area.Width - gap * (cols - 1)) / cols)), cardH = Math.Round(cardW * 9 / 16);
        var mine = NetworkStore.Servers.Where(NetworkStore.IsMine).OrderBy(x => x.KindOrder).ToList();
        var found = NetworkStore.Servers.Where(x => !NetworkStore.IsMine(x)).OrderBy(x => x.KindOrder).ToList();
        var sections = new List<(string Title, List<NetworkServer> Items)>();
        if (mine.Count > 0) sections.Add(("I tuoi server", mine));
        if (found.Count > 0) sections.Add((mine.Count > 0 ? "Trovati in rete" : "Server disponibili", found));
        if (sections.Count == 0)
        {
            TextAt(ctx, _discovering ? "Ricerca in corso" : "Nessun server trovato", 16 * _s, DisplaySemibold, Text, new Rect(area.X, area.Y, area.Width, titleH));
            TextAt(ctx, _discovering ? "Cerco server DLNA, Jellyfin, Emby e Plex nella tua rete." : "Accendi un server sulla stessa rete, oppure aggiungi un server Jellyfin o Emby.",
                Math.Max(9, 11 * _s), DisplayRegular, UiTheme.Muted, new Rect(area.X, area.Y + titleH + S(4), area.Width, S(26)));
            return;
        }
        double y = area.Y;
        using (ctx.PushClip(area.Inflate(S(4))))
            foreach (var (title, items) in sections)
            {
                TextAt(ctx, title, 16 * _s, DisplaySemibold, Text, new Rect(area.X, y, area.Width, titleH));
                y += titleH + titleGap;
                for (int i = 0; i < items.Count; i += cols)
                {
                    for (int c = 0; c < cols && i + c < items.Count; c++)
                        DrawServerCard(ctx, new Rect(area.X + c * (cardW + gap), y, cardW, cardH), items[i + c]);
                    y += cardH + rowGap;
                }
            }
    }

    private void DrawServerCard(DrawingContext ctx, Rect r, NetworkServer server)
    {
        bool connected = ActiveServer?.Key == server.Key && !_networkLoading;
        bool loadingThis = _networkLoading && ActiveServer?.Key == server.Key;
        bool hover = _menu == null && _signIn == null && Hover(r);
        double scale = Math.Max(0.8, Math.Min(2.6, r.Height / 160));
        double radius = Math.Max(1, Math.Round(6 * scale));
        using (ctx.PushClip(new RoundedRect(r, radius)))
        {
            // Senza una foto del catalogo: il colore del servizio come un bagliore in alto a sinistra.
            ctx.FillRectangle(B(UiTheme.IsLight ? Color.FromRgb(34, 38, 46) : Color.FromRgb(16, 20, 26)), r);
            Color tint = server.IsPlex ? Color.FromRgb(229, 160, 13) : server.Product == JellyfinClient.Emby ? Color.FromRgb(82, 181, 75) : Color.FromRgb(170, 92, 195);
            ctx.FillRectangle(new RadialGradientBrush
            {
                Center = new RelativePoint(0.3, 0.3, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative), RadiusY = new RelativeScalar(1.0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(A(tint, 120), 0), new GradientStop(A(tint, 0), 1) },
            }, r);
            double glyph = Math.Max(24, Math.Round(r.Height * 0.26));
            SvgIcon.Get("computer")?.Draw(ctx, new Rect(r.X + (r.Width - glyph) / 2, r.Y + Math.Round(r.Height * 0.24), glyph, glyph), Color.FromArgb(150, 255, 255, 255));
            var lower = new Rect(r.X, r.Y + r.Height / 3, r.Width, r.Height - r.Height / 3 + 1);
            ctx.FillRectangle(Vertical(Color.FromRgb(6, 10, 16), (0, 0), (1, 200)), lower);
        }
        var ring = hover ? new Pen(B(Color.FromArgb(215, 255, 255, 255)), 2) : new Pen(B(Color.FromArgb(18, 255, 255, 255)), 1);
        ctx.DrawRectangle(null, ring, r.Deflate(hover ? 1 : .5), radius, radius);

        double pad = Math.Round(16 * scale);
        TextAt(ctx, (server.IsJellyfin ? server.Product : server.IsPlex ? "Plex" : "DLNA").ToUpperInvariant(), Math.Max(7, 7.8 * scale), DisplaySemibold, Color.FromArgb(190, 255, 255, 255),
            new Rect(r.X + pad, r.Y + Math.Round(12 * scale), r.Width / 2, Math.Round(18 * scale)));
        TextAt(ctx, server.Name, Math.Max(8.4, 10.4 * scale), DisplaySemibold, Colors.White, new Rect(r.X + pad, r.Bottom - Math.Round(52 * scale), r.Width - pad * 2, Math.Round(24 * scale)));
        bool signIn = server.IsJellyfin && server.Account == null;
        string state = loadingThis ? "Caricamento…" : connected ? "Collegato" : !server.Available ? "Non raggiungibile" : signIn ? "Accesso richiesto" : "Disponibile";
        double dot = Math.Max(5, Math.Round(6 * scale)), metaY = r.Bottom - Math.Round(28 * scale), metaH = Math.Round(18 * scale);
        Color dotColor = connected || loadingThis ? Color.FromRgb(110, 210, 160) : server.Available ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(110, 255, 255, 255);
        ctx.DrawEllipse(B(dotColor), null, new Point(r.X + pad + dot / 2, metaY + metaH / 2), dot / 2, dot / 2);
        TextAt(ctx, state + "  ·  " + server.Host, Math.Max(7.6, 8.8 * scale), DisplayRegular, Color.FromArgb(205, 255, 255, 255),
            new Rect(r.X + pad + dot + Math.Round(8 * scale), metaY, r.Width - pad * 2 - dot, metaH));
        _hits.Add(new Hit(r, "net-open", server.Key));
        // Al passaggio: stella (preferito) e cestino in alto a destra.
        bool favorite = NetworkStore.IsFavorite(server);
        if (hover || favorite)
        {
            double icon = Math.Max(16, Math.Round(18 * scale)), hitPad = Math.Round(8 * scale);
            var trash = new Rect(r.Right - pad - icon, r.Y + Math.Round(12 * scale), icon, icon);
            var star = new Rect(trash.X - Math.Round(14 * scale) - icon, trash.Y, icon, icon);
            if (hover)
            {
                var trashHit = trash.Inflate(hitPad);
                SvgIcon.Get("trash")?.Draw(ctx, trash, Hover(trashHit) ? Color.FromRgb(235, 95, 95) : Color.FromArgb(200, 255, 255, 255));
                _hits.Add(new Hit(trashHit, "net-remove", server.Key));
            }
            var starHit = star.Inflate(hitPad);
            SvgIcon.Get("star")?.Draw(ctx, star, favorite || Hover(starHit) ? Colors.White : Color.FromArgb(200, 255, 255, 255));
            if (hover) _hits.Add(new Hit(starHit, "net-fav", server.Key));
        }
    }

    // ------------------------------------------------------------------ accesso (JellyfinLoginForm)
    private sealed class SignInState
    {
        public JellyfinClient.Server? Server;
        public TextBox Address = null!, User = null!, Password = null!;
        public string Status = "";
        public bool? StatusOk;
        public bool Busy, QuickMode;
        public JellyfinClient.QuickConnectRequest? Request;
        public DispatcherTimer? Poll;
        public CancellationTokenSource Lifetime = new();
        public Rect Sheet, AddressRect, UserRect, PasswordRect, Quick, Cancel, SignIn;
    }
    private SignInState? _signIn;

    private TextBox Input(bool password)
    {
        var box = new TextBox
        {
            PasswordChar = password ? '•' : default,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily(AppAssets.Root + "Assets/Fonts#Inter"),
            FontSize = Px(10.5),
            Padding = new Thickness(0),
            MinHeight = 0,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[k] = Brushes.Transparent;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = SignInAsync(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseSignIn(null); e.Handled = true; }
        };
        box.GotFocus += (_, _) => InvalidateVisual();
        box.LostFocus += (_, _) => InvalidateVisual();
        InputLayer?.Children.Add(box);
        return box;
    }

    /// <param name="server">Server gia' noto (trovato in rete); null per chiedere l'indirizzo.</param>
    private void OpenSignIn(NetworkServer? server)
    {
        if (_signIn != null || InputLayer == null) return;
        var known = server == null ? null : new JellyfinClient.Server(server.ServerId, server.Name, server.Location,
            server.Model.Replace("Jellyfin", "", StringComparison.OrdinalIgnoreCase).Replace("Emby", "", StringComparison.OrdinalIgnoreCase).Trim(), server.Product);
        var st = new SignInState { Server = known };
        st.Address = Input(false); st.User = Input(false); st.Password = Input(true);
        st.Address.Text = known?.Address ?? "";
        st.Address.Watermark = "192.168.1.10  ·  nas:8096  ·  https://…";
        // Con un indirizzo diverso il server va riconosciuto di nuovo.
        st.Address.TextChanged += (_, _) =>
        {
            if (st.Server != null && !string.Equals(JellyfinClient.NormalizeAddress(st.Address.Text ?? ""), st.Server.Address, StringComparison.OrdinalIgnoreCase)) st.Server = null;
        };
        _signIn = st;
        InvalidateVisual();
        Dispatcher.UIThread.Post(() => (known == null ? st.Address : st.User).Focus(), DispatcherPriority.Background);
    }

    private void CloseSignIn(JellyfinClient.Account? account)
    {
        if (_signIn is not { } st) return;
        st.Poll?.Stop();
        st.Lifetime.Cancel();
        foreach (var box in new[] { st.Address, st.User, st.Password }) InputLayer?.Children.Remove(box);
        _signIn = null;
        Focus();
        InvalidateVisual();
        if (account == null) return;
        // Dopo l'accesso il server diventa la fonte e si legge il catalogo.
        var item = NetworkStore.FromJellyfin(new JellyfinClient.Server(account.ServerId, account.ServerName, account.Address, st.Server?.Version ?? "", account.Product), available: true);
        NetworkStore.Upsert(item);
        SetSource(item);
    }

    private void Say(string text, bool? ok) { if (_signIn == null) return; _signIn.Status = text; _signIn.StatusOk = ok; InvalidateVisual(); }

    private async Task<JellyfinClient.Server?> ResolveServerAsync(SignInState st)
    {
        if (st.Server != null) return st.Server;
        string address = JellyfinClient.NormalizeAddress(st.Address.Text ?? "");
        if (address.Length == 0) { Say("Inserisci l'indirizzo del server", false); return null; }
        Say("Cerco il server…", null);
        try { return st.Server = await JellyfinClient.GetServerAsync(address, st.Lifetime.Token); }
        catch (OperationCanceledException) { return null; }
        catch { Say("A questo indirizzo non risponde un server Jellyfin o Emby", false); return null; }
    }

    private async Task SignInAsync()
    {
        if (_signIn is not { } st || st.Busy || st.QuickMode) return;
        string user = (st.User.Text ?? "").Trim();
        if (user.Length == 0) { Say("Inserisci il nome utente", false); st.User.Focus(); return; }
        st.Busy = true;
        JellyfinClient.Account? account = null;
        try
        {
            var server = await ResolveServerAsync(st);
            if (server == null || _signIn != st) return;
            Say("Accesso in corso…", null);
            account = await JellyfinClient.LoginAsync(server, user, st.Password.Text ?? "", st.Lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (JellyfinClient.UnauthorizedException) { Say("Nome utente o password non corretti", false); st.Password.SelectAll(); }
        catch (System.Net.Http.HttpRequestException) { Say("Il server non risponde", false); }
        catch (Exception ex) { Say("Accesso non riuscito  ·  " + ex.Message, false); }
        finally { st.Busy = false; }
        if (account != null && _signIn == st) CloseSignIn(account);
    }

    private async Task QuickConnectAsync()
    {
        if (_signIn is not { } st || st.Busy) return;
        if (st.QuickMode) { LeaveQuickConnect(st); return; }
        st.Busy = true;
        try
        {
            var server = await ResolveServerAsync(st);
            if (server == null || _signIn != st) return;
            if (!await JellyfinClient.QuickConnectEnabledAsync(server, st.Lifetime.Token)) { Say("Quick Connect non è attivo su questo server", false); return; }
            st.Request = await JellyfinClient.StartQuickConnectAsync(server, st.Lifetime.Token);
            st.QuickMode = true;
            Say("In attesa dell'approvazione…", null);
            st.Poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await PollQuickConnectAsync(st));
            st.Poll.Start();
        }
        catch (OperationCanceledException) { }
        catch { Say("Quick Connect non disponibile", false); }
        finally { st.Busy = false; InvalidateVisual(); }
    }

    private void LeaveQuickConnect(SignInState st)
    {
        st.Poll?.Stop();
        st.QuickMode = false; st.Request = null;
        Say("", null);
        st.User.Focus();
    }

    private bool _polling;
    private async Task PollQuickConnectAsync(SignInState st)
    {
        if (_polling || !st.QuickMode || st.Request == null || st.Server == null) return;
        _polling = true;
        try
        {
            var account = await JellyfinClient.TryFinishQuickConnectAsync(st.Server, st.Request, st.Lifetime.Token);
            if (account != null && _signIn == st) CloseSignIn(account);
        }
        catch (JellyfinClient.UnauthorizedException) { LeaveQuickConnect(st); Say("Codice scaduto: riprova", false); }
        catch { /* rete assente per un attimo: si riprova al prossimo giro */ }
        finally { _polling = false; }
    }

    internal void TestSignIn() => OpenSignIn(null);

    private void SignInPressed(Point p)
    {
        if (_signIn is not { } st) return;
        if (st.SignIn.Contains(p)) _ = SignInAsync();
        else if (st.Cancel.Contains(p)) CloseSignIn(null);
        else if (st.Quick.Contains(p)) _ = QuickConnectAsync();
        else if (st.AddressRect.Contains(p)) st.Address.Focus();
        else if (!st.QuickMode && st.UserRect.Contains(p)) st.User.Focus();
        else if (!st.QuickMode && st.PasswordRect.Contains(p)) st.Password.Focus();
    }

    /// <summary>Scheda 560×452 al centro, come JellyfinLoginForm (superficie Sheet, pagina velata dietro).</summary>
    private void DrawSignIn(DrawingContext ctx, Rect page)
    {
        if (_signIn is not { } st) return;
        // Come le altre schede che chiedono dati (Nuova playlist): pagina velata, colonna a sinistra, pillola chiara.
        WizardSheet.DrawVeil(ctx, page, 1);
        st.Sheet = page;
        double u = WizardSheet.Scale(page.Size);
        var column = WizardSheet.Column(page.Size).Translate(new Vector(page.X, page.Y));
        st.Cancel = WizardSheet.Cancel(page.Size).Translate(new Vector(page.X, page.Y));
        WizardSheet.DrawCancel(ctx, st.Cancel, Hover(st.Cancel), u);
        void Line(string text, double px, Typeface face, Color color, Rect r)
        {
            var ft = WizardSheet.Text(text, px, face, color, r.Width);
            ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
        }
        string product = string.IsNullOrEmpty(st.Server?.Product) ? "Jellyfin o Emby" : st.Server!.Product;
        string subtitle = st.Server != null
            ? st.Server.Name + (string.IsNullOrWhiteSpace(st.Server.Version) ? "" : "  ·  " + st.Server.Product + " " + st.Server.Version)
            : "Librerie, locandine e punto di ripresa arrivano dal tuo server.";
        double top = WizardSheet.DrawHeader(ctx, column, u, 0, 1, "Accedi a " + product, subtitle, 255, 0) - 10 * u;
        double rowGap = 96 * u, fieldH = 52 * u;
        st.AddressRect = new Rect(column.X, top + 24 * u, column.Width, fieldH);
        st.UserRect = new Rect(column.X, st.AddressRect.Y + rowGap, column.Width, fieldH);
        st.PasswordRect = new Rect(column.X, st.UserRect.Y + rowGap, column.Width, fieldH);
        void Field(Rect bounds, string label, TextBox box)
        {
            Line(label, 13 * u, WizardSheet.Semibold, WizardSheet.Soft(255), new Rect(bounds.X + 4 * u, bounds.Y - 24 * u, bounds.Width, 20 * u));
            WizardSheet.DrawUnderline(ctx, bounds, box.IsFocused, false, 255, u);
        }
        Field(st.AddressRect, "INDIRIZZO DEL SERVER", st.Address);
        if (st.QuickMode && st.Request != null)
        {
            // Il codice, grande e spaziato: si legge dal divano e si copia senza errori.
            Line("CODICE", 13 * u, WizardSheet.Semibold, WizardSheet.Soft(255), new Rect(column.X, st.UserRect.Y - 22 * u, 300 * u, 20 * u));
            Line(string.Join(" ", st.Request.Code.ToCharArray()), 44 * u, WizardSheet.Semibold, UiTheme.Text, new Rect(column.X, st.UserRect.Y, column.Width, 64 * u));
            var help = WizardSheet.Text("Su un dispositivo già collegato a Jellyfin apri il menu utente, Quick Connect, e inserisci il codice.", 17 * u, WizardSheet.Regular, WizardSheet.Soft(255), column.Width, 3);
            ctx.DrawText(help, new Point(column.X, st.UserRect.Y + 76 * u));
        }
        else
        {
            Field(st.UserRect, "NOME UTENTE", st.User);
            Field(st.PasswordRect, "PASSWORD", st.Password);
        }
        if (st.Status.Length > 0)
        {
            Color dot = st.StatusOk == true ? Color.FromRgb(62, 190, 120) : st.StatusOk == false ? Color.FromRgb(255, 76, 92) : A(UiTheme.Muted, 170);
            ctx.DrawEllipse(B(dot), null, new Point(column.X + 4 * u, st.PasswordRect.Bottom + 30 * u), 4 * u, 4 * u);
            Line(st.Status, 16 * u, WizardSheet.Regular, WizardSheet.Soft(255), new Rect(column.X + 18 * u, st.PasswordRect.Bottom + 18 * u, column.Width - 18 * u, 24 * u));
        }
        // Accedi (pillola chiara) e accanto Quick Connect / Usa la password, solo testo.
        string quick = st.QuickMode ? "Usa la password" : "Quick Connect";
        (st.SignIn, st.Quick) = WizardSheet.Buttons(column, st.PasswordRect.Bottom + 64 * u, u, "Accedi", quick);
        WizardSheet.DrawNext(ctx, st.SignIn, "Accedi", Hover(st.SignIn), !st.Busy && !st.QuickMode, 255, u);
        if (!st.Busy) WizardSheet.DrawBack(ctx, st.Quick, quick, Hover(st.Quick), 255, u);
    }

    /// <summary>Le caselle vere seguono i loro posti nella scheda.</summary>
    private void PlaceSignInInputs()
    {
        if (_signIn is not { } st || st.Sheet.Width <= 0) return;
        void Place(TextBox box, Rect r, bool show)
        {
            box.IsVisible = show;
            if (!show) return;
            double u = WizardSheet.Scale(Bounds.Size);
            box.FontSize = 22 * u;
            double h = 22 * u * 1.5;
            var inner = WizardSheet.BoxIn(r, h, u);
            Canvas.SetLeft(box, inner.X); Canvas.SetTop(box, inner.Y);
            box.Width = inner.Width; box.Height = inner.Height;
            box.Foreground = B(UiTheme.Text); box.CaretBrush = B(UiTheme.Text);
            box.SelectionBrush = B(A(UiTheme.Text, 70));
        }
        Place(st.Address, st.AddressRect, true);
        Place(st.User, st.UserRect, !st.QuickMode);
        Place(st.Password, st.PasswordRect, !st.QuickMode);
    }
}
