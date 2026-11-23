using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
/// "Consigliati per te" (TraktForm di Windows), a tutta finestra. Come la Home: in alto il banner con i primi consigli a
/// rotazione (prima quelli gia' in libreria), sotto due file di locandine a carosello, "Nella tua libreria" (un clic apre
/// la scheda del film) e "Da recuperare" (un clic apre la pagina su Trakt). Senza account: il collegamento con il codice
/// di trakt.tv/activate. Il diario va su Trakt da solo (StartAutoSync). Nel menu dei tre puntini: "segna su Trakt quello che finisco", scollega.
/// </summary>
internal sealed class TraktView : Control
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Color Back = Color.FromRgb(9, 11, 15);
    private static readonly Color TextInk = Color.FromRgb(242, 244, 247), MutedInk = Color.FromRgb(138, 146, 158), SubtleInk = Color.FromRgb(170, 178, 190);
    private static readonly (string Kind, string Label)[] Kinds = { ("movies", "Film"), ("shows", "Serie") };

    public event Action? Closed;
    /// <summary>Un film della libreria scelto: lo apre la finestra (scheda del titolo).</summary>
    public event Action<string>? LibraryItemChosen;
    /// <summary>Menu in vetro del player (voci, punto).</summary>
    public event Action<IReadOnlyList<MenuEntry>, Point>? MenuRequested;
    /// <summary>Come MenuRequested, ma il punto e' il bordo destro dell'elenco.</summary>
    public event Action<IReadOnlyList<MenuEntry>, Point>? RightMenuRequested;

    private IReadOnlyList<TraktLibrary.LibraryTitle> _library = Array.Empty<TraktLibrary.LibraryTitle>();
    private CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, List<TraktLibrary.Recommendation>?> _results = new();
    private TraktClient.DeviceCode? _code;
    private string _kind = "movies", _status = "";
    private bool _busy, _connecting;
    private int _pending;
    private Point _mouse = new(-1, -1);
    private readonly List<(Rect Bounds, string Kind, object? Tag)> _hits = new();
    private DispatcherTimer? _motion, _rotation;
    private DateTime _openedAt;

    // Scorrimento morbido e caroselli.
    private double _scroll, _scrollPos, _contentHeight;
    private readonly Dictionary<string, double> _rowPos = new();
    private readonly Dictionary<string, int> _rowTarget = new();
    private long _rowsRevealAt;

    public TraktView() { IsVisible = false; Focusable = true; ClipToBounds = true; }

    public bool IsOpen => IsVisible;

    // A tutta finestra: tutto in proporzione all'altezza (la scheda di Windows e' disegnata in pixel al 100%).
    private double K => Math.Max(0.75, Bounds.Height / 1080);
    private double U(double v) => Math.Round(v * K);
    private double Side => Math.Max(U(64), Bounds.Width / 28);
    private double Gap => U(22);
    private double TitleHeight => U(28);
    private int Columns2 => Math.Max(5, (int)((Bounds.Width - Side * 2 + Gap) / (U(196) + Gap)));
    private double CardWidth => Math.Floor((Bounds.Width - Side * 2 - Gap * (Columns2 - 1)) / Columns2);
    private double CardHeight => Math.Floor(CardWidth * 3 / 2);
    // Il banner occupa il 64% delle finestre basse e scende fino al 56% su quelle alte (schermo intero): li' restava
    // troppo vuoto fra l'intestazione e la prima fila.
    private double HeroHeight => HeroCandidates().Count > 0 ? HeroZone : 0;
    private double HeroZone => Math.Round(Bounds.Height * (0.64 - 0.08 * Math.Clamp((Bounds.Height - 800) / 200, 0, 1)));
    private Rect CloseBounds => new(Bounds.Width - Side - U(36), U(40), U(40), U(40));
    private Rect MoreBounds => new(CloseBounds.X - U(56), CloseBounds.Y, CloseBounds.Width, CloseBounds.Height);
    private Rect _kindAnchor;

    // ------------------------------------------------------------------ apertura e stato
    public void Open(IReadOnlyList<TraktLibrary.LibraryTitle> library)
    {
        _lifetime.Cancel();
        _lifetime = new CancellationTokenSource();
        _library = library;
        _results.Clear(); _rowPos.Clear(); _rowTarget.Clear();
        _scroll = _scrollPos = 0; _heroIndex = 0; _heroPrevIndex = -1; _heroIds.Clear(); _allRow = null; _status = ""; _code = null;
        _openedAt = DateTime.UtcNow;
        IsVisible = true; Focus();
        UpdateState(load: true);
        StartMotion();
    }

    public void Close()
    {
        if (!IsVisible) return;
        _lifetime.Cancel();
        _motion?.Stop(); _rotation?.Stop();
        IsVisible = false;
        Closed?.Invoke();
    }

    private void UpdateState(bool load)
    {
        if (TraktClient.IsConnected)
        {
            try { _pending = TraktLibrary.PendingCount(); } catch { _pending = 0; }
            // Il diario va su Trakt da solo, senza chiederlo: i consigli si ricaricano quando ha finito.
            if (_pending > 0 && !_busy && TraktClient.MarkWatched) { SendDiary(); return; }
            if (load) LoadKind(_kind);
        }
        InvalidateVisual();
    }

    private void Say(string text) { _status = text; InvalidateVisual(); }

    /// <summary>Conferma passeggera (segnato visto, scartato, collegato): sparisce da sola dopo 4 s.</summary>
    private void Flash(string text)
    {
        Say(text);
        DispatcherTimer.RunOnce(() => { if (_status == text) Say(""); }, TimeSpan.FromSeconds(4));
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            PlatformShell.OpenUrl(uri.AbsoluteUri);
        }
        catch { }
    }

    private async void Connect()
    {
        if (_connecting) return;
        _connecting = true;
        Say("Chiedo un codice a Trakt…");
        var token = _lifetime.Token;
        try
        {
            _code = await Task.Run(() => TraktClient.BeginConnectAsync(token), token);
            try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_code.UserCode); } catch { }
            Say("Codice copiato negli appunti. Aspetto la conferma dal sito…");
            var code = _code;
            bool connected = await Task.Run(() => TraktClient.WaitForConnectAsync(code, token), token);
            _code = null;
            Flash(connected ? "Collegato" : "Codice scaduto o rifiutato: riprova");
        }
        catch (OperationCanceledException) { _connecting = false; return; }
        catch (Exception ex) { _code = null; Say("Trakt non raggiungibile  ·  " + ex.Message); }
        _connecting = false;
        UpdateState(load: true);
    }

    private async void Disconnect()
    {
        _busy = true; InvalidateVisual();
        try { await Task.Run(() => TraktClient.DisconnectAsync(_lifetime.Token)); } catch { }
        _busy = false;
        _results.Clear(); _scroll = 0;
        Flash("Scollegato");
        UpdateState(load: false);
    }

    private async void SendDiary()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await SyncDiaryAsync(_lifetime.Token);
            _results.Clear(); // con il diario su Trakt i consigli cambiano
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Console.Error.WriteLine("[TRAKT] diario: " + ex.Message); }
        _busy = false;
        try { _pending = 0; } catch { }
        LoadKind(_kind);
        InvalidateVisual();
    }

    private static int _syncing;

    /// <summary>Invio del diario a Trakt (titoli nuovi e voti), uno alla volta. Senza messaggi: avviene da solo.</summary>
    private static async Task SyncDiaryAsync(System.Threading.CancellationToken token)
    {
        if (System.Threading.Interlocked.Exchange(ref _syncing, 1) == 1) return;
        try
        {
            var result = await Task.Run(() => TraktLibrary.SendDiaryAsync(null, "it", token), token);
            Console.Error.WriteLine($"[TRAKT] diario: {result.Movies} film, {result.Episodes} episodi, {result.Ratings} voti");
        }
        finally { System.Threading.Volatile.Write(ref _syncing, 0); }
    }

    /// <summary>
    /// Il diario arriva su Trakt da solo (account collegato): all'avvio e poi ogni dieci minuti, solo se c'e' qualcosa di
    /// nuovo (film finiti, voti cambiati).
    /// </summary>
    public static void StartAutoSync()
    {
        async void Tick()
        {
            try
            {
                if (!TraktClient.IsConnected || !TraktClient.MarkWatched) return;
                int pending = await Task.Run(TraktLibrary.PendingCount);
                if (pending > 0) await SyncDiaryAsync(System.Threading.CancellationToken.None);
            }
            catch (Exception ex) { Console.Error.WriteLine("[TRAKT] diario automatico: " + ex.Message); }
        }
        Avalonia.Threading.DispatcherTimer.RunOnce(Tick, TimeSpan.FromSeconds(20));
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    private async void LoadKind(string kind)
    {
        if (_results.ContainsKey(kind)) { InvalidateVisual(); return; }
        _results[kind] = null;
        InvalidateVisual();
        List<TraktLibrary.Recommendation>? loaded = null;
        var token = _lifetime.Token;
        try
        {
            // Prima l'elenco di Trakt, mostrato subito; nomi italiani e locandine arrivano uno alla volta.
            var titles = await Task.Run(() => TraktClient.RecommendationsAsync(kind, 60, token), token);
            var shown = TraktLibrary.Provisional(titles, _library);
            _results[kind] = shown;
            if (kind == _kind) { _rowsRevealAt = Environment.TickCount64; StartMotion(); }
            InvalidateVisual();
            var progress = new Progress<TraktLibrary.Recommendation>(ready =>
            {
                if (!_results.TryGetValue(kind, out var current) || !ReferenceEquals(current, shown)) return;
                int index = shown.FindIndex(item => item.Trakt.TmdbId == ready.Trakt.TmdbId);
                if (index < 0) return;
                shown[index] = ready;
                if (kind == _kind) InvalidateVisual();
            });
            loaded = await Task.Run(() => TraktLibrary.ResolveAsync(titles, _library, "it", progress, token), token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Console.Error.WriteLine("[TRAKT] recommendations: " + ex.Message); Say("Consigli non disponibili  ·  " + ex.Message); }
        if (loaded == null) _results.Remove(kind);
        else _results[kind] = loaded;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ banner a rotazione
    private readonly Dictionary<int, (string? Overview, Bitmap? Backdrop)> _heroInfo = new();
    private readonly HashSet<int> _heroLoading = new();
    private int _heroIndex, _heroPrevIndex = -1;
    private long _heroShownAt, _heroFadeAt;
    private const double HeroFadeMs = 520, HeroHoldMs = 9000;

    /// <summary>Titoli che possono andare nel banner (prima quelli in libreria); per ognuno si chiedono trama e foto.</summary>
    private List<TraktLibrary.Recommendation> HeroCandidates()
    {
        if (!TraktClient.IsConnected || !_results.TryGetValue(_kind, out var items) || items == null || items.Count == 0) return new();
        return items.Where(i => i.Trakt.TmdbId > 0).OrderBy(i => i.LibraryPath == null).Take(HeroCandidateCount).ToList();
    }

    private const int HeroCandidateCount = 12, HeroDeckSize = 6;
    /// <summary>
    /// Il mazzo del banner: solo titoli con la foto gia' arrivata, nell'ordine in cui sono arrivati. Prima il mazzo si
    /// ricalcolava a ogni titolo risolto (l'ordine cambiava e il banner saltava da un film all'altro) e conteneva
    /// titoli senza foto (banner nero).
    /// </summary>
    private readonly List<int> _heroIds = new();

    private List<TraktLibrary.Recommendation> HeroDeck()
    {
        var candidates = HeroCandidates();
        // Le foto gia' in memoria (si torna su un tipo gia' visto) entrano subito; le altre si chiedono adesso.
        foreach (var c in candidates) if (HeroInfo(c).Backdrop != null) HeroReady(c.Trakt.TmdbId);
        if (!_results.TryGetValue(_kind, out var items) || items == null) return new();
        return _heroIds.Select(id => items.FirstOrDefault(i => i.Trakt.TmdbId == id)).Where(i => i != null).Select(i => i!).ToList();
    }

    private TraktLibrary.Recommendation? HeroItem()
    {
        var deck = HeroDeck();
        return deck.Count == 0 ? null : deck[Math.Clamp(_heroIndex, 0, deck.Count - 1)];
    }

    /// <summary>Arrivata la foto di un candidato: entra in fondo al mazzo (il primo entra in dissolvenza).</summary>
    private void HeroReady(int id)
    {
        if (_heroIds.Contains(id) || _heroIds.Count >= HeroDeckSize) return;
        if (!HeroCandidates().Any(c => c.Trakt.TmdbId == id)) return;
        _heroIds.Add(id);
        if (_heroIds.Count == 1)
        {
            _heroIndex = 0; _heroPrevIndex = -1;
            if ((DateTime.UtcNow - _openedAt).TotalMilliseconds > 250) _heroFadeAt = Environment.TickCount64;
            _heroShownAt = Environment.TickCount64;
            StartMotion();
        }
    }

    private (string? Overview, Bitmap? Backdrop) HeroInfo(TraktLibrary.Recommendation item)
    {
        int id = item.Trakt.TmdbId;
        if (_heroInfo.TryGetValue(id, out var ready)) return ready;
        if (id <= 0 || !_heroLoading.Add(id)) return (null, null);
        string type = item.Trakt.Kind == "tv" ? "tv" : "movie";
        var token = _lifetime.Token;
        Task.Run(() =>
        {
            string? overview = null; Bitmap? backdrop = null;
            try
            {
                var (text, path) = MovieMetadataService.GetTmdbOverview(type, id, "it", token);
                overview = text;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    string file = Path.Combine(Path.GetTempPath(), "cinecore-trakt", type + "-" + id + ".jpg");
                    if (MovieMetadataService.TryDownloadTmdbBackdrop(path!, file, token))
                    {
                        using var fs = File.OpenRead(file);
                        backdrop = Bitmap.DecodeToWidth(fs, 2560, BitmapInterpolationMode.HighQuality);
                    }
                }
            }
            catch { }
            Dispatcher.UIThread.Post(() =>
            {
                _heroInfo[id] = (overview, backdrop);
                // Senza foto il titolo non entra nel banner (restava nero).
                if (backdrop != null) HeroReady(id);
                InvalidateVisual();
            });
        });
        return (null, null);
    }

    private void AdvanceHero(int to)
    {
        var deck = HeroDeck();
        if (deck.Count < 2) return;
        to = ((to % deck.Count) + deck.Count) % deck.Count;
        if (to == _heroIndex) return;
        _heroPrevIndex = _heroIndex;
        _heroIndex = to;
        _heroFadeAt = Environment.TickCount64;
        _heroShownAt = Environment.TickCount64;
        StartMotion();
    }

    private static string Tagline(string? overview)
    {
        string text = (overview ?? "").Trim();
        for (int i = 25; i < text.Length - 1; i++)
            if (text[i] is '.' or '!' or '?' && text[i + 1] == ' ') return i < 170 ? text[..(i + 1)] : text;
        return text;
    }

    /// <summary>La foto copre il banner da destra, con le sfumature della Home: forte a sinistra, velo in alto, chiusura in basso.</summary>
    private void DrawHeroPhoto(DrawingContext ctx, Bitmap backdrop, Rect r, double opacity)
    {
        using var fade = ctx.PushOpacity(opacity);
        using var clip = ctx.PushClip(r);
        double k = Math.Max(r.Width / backdrop.Size.Width, r.Height / backdrop.Size.Height);
        double w = backdrop.Size.Width * k, h = backdrop.Size.Height * k;
        ctx.DrawImage(backdrop, new Rect(r.Right - w, r.Y + (r.Height - h) * 0.3, w, h));
        static Color C(byte a) => Color.FromArgb(a, Back.R, Back.G, Back.B);
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new(C(255), 0), new(C(248), .18), new(C(210), .34), new(C(110), .52), new(C(28), .74), new(C(0), 1) },
        }, r);
        double topH = Math.Min(r.Height / 3, U(170));
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new(C(200), 0), new(C(0), 1) },
        }, new Rect(r.X, r.Y, r.Width, topH));
        double fadeH = Math.Round(r.Height * 0.5);
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new(C(0), 0), new(C(70), .35), new(C(190), .7), new(C(255), 1) },
        }, new Rect(r.X, r.Bottom - fadeH, r.Width, fadeH + 1));
    }

    private void DrawHero(DrawingContext ctx)
    {
        var item = HeroItem();
        if (item == null)
        {
            if (HeroHeight > 0) DrawHeroSkeleton(ctx, new Rect(0, -Math.Round(_scrollPos), Bounds.Width, HeroHeight));
            return;
        }
        var area = new Rect(0, -Math.Round(_scrollPos), Bounds.Width, HeroHeight);
        if (area.Bottom <= 0) return;
        var (overview, backdrop) = HeroInfo(item);
        double fade = _heroFadeAt > 0 ? Math.Clamp((Environment.TickCount64 - _heroFadeAt) / HeroFadeMs, 0, 1) : 1;
        fade = fade * fade * (3 - 2 * fade);
        var deck = HeroDeck();
        if (fade < 1 && _heroPrevIndex >= 0 && _heroPrevIndex < deck.Count && _heroInfo.TryGetValue(deck[_heroPrevIndex].Trakt.TmdbId, out var previous) && previous.Backdrop != null)
            DrawHeroPhoto(ctx, previous.Backdrop, area, 1);
        if (backdrop != null) DrawHeroPhoto(ctx, backdrop, area, fade);
        if (fade >= 1) _heroPrevIndex = -1;

        // Testo in basso a sinistra come la Home: etichetta piccola, titolo grande, una riga di trama, il pulsante.
        double x = Side, w = Math.Min(U(780), Bounds.Width * 0.46);
        double textAlpha = 0.35 + 0.65 * fade;
        string tagline = Tagline(overview);
        var titleText = UiText.Get(L10n.T(item.DisplayTitle), Semibold, U(56), UiTheme.WithAlpha(Colors.White, 255 * textAlpha), w, 2);
        double lineH = tagline.Length > 0 ? U(19) * 1.3 + U(4) : 0;
        // Il blocco (etichetta, titolo, trama, pulsante) sta al centro fra la testata "Consigliati" e il titolo della
        // fila sotto, come il testo della Home. Prima era a distanza fissa dal fondo del banner e, con finestre basse o
        // la barra della musica, finiva attaccato alla testata.
        double actionH = U(50);
        double blockH = U(32) + titleText.Height + (lineH > 0 ? U(14) + lineH : 0) + U(34) + actionH;
        double zoneTop = area.Y + U(100), zoneBottom = area.Bottom - U(96);
        double blockTop = Math.Max(zoneTop, Math.Round((zoneTop + zoneBottom) / 2 - blockH / 2));
        double actionTop = blockTop + blockH - actionH;
        double y = blockTop + U(32);
        string label = (item.LibraryPath != null ? "IN LIBRERIA" : "DA RECUPERARE") + (item.Year is int yr ? "  ·  " + yr : "");
        Px(ctx, label, U(14), Semibold, UiTheme.WithAlpha(Color.FromRgb(170, 182, 196), 255 * textAlpha), new Rect(x, y - U(32), w, U(22)));
        ctx.DrawText(titleText, new Point(x, y));
        y += titleText.Height;
        if (lineH > 0) Px(ctx, tagline, U(19), Regular, UiTheme.WithAlpha(Color.FromRgb(222, 228, 236), 255 * textAlpha), new Rect(x, y + U(14), w, lineH));
        string action = item.LibraryPath != null ? "Apri la scheda" : "Apri su Trakt";
        double aw = Measure(action, U(19), Regular) + U(56);
        var pill = new Rect(x, actionTop, aw, actionH);
        bool hot = Hover(pill);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(hot ? 228 : 255), 255, 255, 255)), null, pill, actionH / 2, actionH / 2);
        Px(ctx, action, U(19), Regular, Colors.Black, pill, TextAlignment.Center);
        _hits.Add((pill, "item", item));
        _hits.Add((area.Intersect(new Rect(Bounds.Size)), "hero", null));
    }

    // ------------------------------------------------------------------ locandine
    private static readonly ConcurrentDictionary<string, Bitmap?> Posters = new();
    private static readonly ConcurrentDictionary<string, byte> PosterLoading = new();
    private readonly Dictionary<string, long> _posterArrived = new();

    private Bitmap? Poster(string? file, double width)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        int px = (int)Math.Clamp(Math.Ceiling(width * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 2) / 64) * 64, 64, 1024);
        string key = file + "|" + px;
        if (Posters.TryGetValue(key, out var ready)) return ready;
        // Misura nuova (finestra ridimensionata, schermo intero): finche' non e' pronta resta quella gia' caricata.
        // Prima la fila restava di riquadri vuoti per un attimo e poi rientrava in dissolvenza.
        Bitmap? stand = null;
        foreach (var pair in Posters)
            if (pair.Value != null && pair.Key.StartsWith(file + "|", StringComparison.Ordinal) && (stand == null || pair.Value.PixelSize.Width > stand.PixelSize.Width))
                stand = pair.Value;
        if (!PosterLoading.TryAdd(key, 0)) return stand;
        Task.Run(() =>
        {
            Bitmap? bmp = null;
            try { using var fs = File.OpenRead(file); bmp = Bitmap.DecodeToWidth(fs, px, BitmapInterpolationMode.HighQuality); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                Posters[key] = bmp;
                // Arrivata a pagina gia' aperta: entra in dissolvenza (non se sostituisce la stessa locandina a un'altra misura).
                if (bmp != null && stand == null && (DateTime.UtcNow - _openedAt).TotalMilliseconds > 250) { _posterArrived[key] = Environment.TickCount64; StartMotion(); }
                InvalidateVisual();
            });
        });
        return stand;
    }

    private double PosterAlpha(string file, double width)
    {
        int px = (int)Math.Clamp(Math.Ceiling(width * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 2) / 64) * 64, 64, 1024);
        string key = file + "|" + px;
        if (!_posterArrived.TryGetValue(key, out long at)) return 1;
        double t = Math.Clamp((Environment.TickCount64 - at) / 280.0, 0, 1);
        if (t >= 1) _posterArrived.Remove(key);
        return t * t * (3 - 2 * t);
    }

    private double RowReveal(int row)
    {
        if (_rowsRevealAt <= 0) return 1;
        double t = Math.Clamp((Environment.TickCount64 - _rowsRevealAt - row * 110) / 460.0, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    // ------------------------------------------------------------------ animazioni
    private void StartMotion()
    {
        _motion ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => MotionTick());
        if (!_motion.IsEnabled) _motion.Start();
        _rotation ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            // Si ferma mentre il puntatore sta sul banner o la pagina e' scorsa.
            if (!IsVisible || HeroHeight <= 0) return;
            if (Hover(new Rect(0, -_scrollPos, Bounds.Width, HeroHeight)) || _scrollPos > HeroHeight / 2) return;
            if (Environment.TickCount64 - _heroShownAt < HeroHoldMs) return;
            AdvanceHero(_heroIndex + 1);
        });
        if (!_rotation.IsEnabled) { _heroShownAt = Environment.TickCount64; _rotation.Start(); }
    }

    private double MaxScroll => Math.Max(0, _contentHeight - Bounds.Height + U(48));

    private void MotionTick()
    {
        bool moving = false;
        double target = Math.Clamp(_scroll, 0, MaxScroll);
        if (Math.Abs(_scrollPos - target) > 0.5) { _scrollPos += (target - _scrollPos) * 0.22; moving = true; } else _scrollPos = target;
        foreach (var key in _rowTarget.Keys.ToList())
        {
            double pos = _rowPos.TryGetValue(key, out var p) ? p : 0, goal = _rowTarget[key];
            if (Math.Abs(pos - goal) > 0.004) { _rowPos[key] = pos + (goal - pos) * 0.2; moving = true; } else _rowPos[key] = goal;
        }
        if (_heroFadeAt > 0 && Environment.TickCount64 - _heroFadeAt < HeroFadeMs + 40) moving = true;
        if (_posterArrived.Count > 0) moving = true;
        if (_rowsRevealAt > 0) { if (Environment.TickCount64 - _rowsRevealAt < 460 + 220 + 40) moving = true; else _rowsRevealAt = 0; }
        if (_connecting || _busy || (_results.TryGetValue(_kind, out var r) && r == null)) moving = true;
        // Banner ancora in arrivo: il riflesso delle sagome continua.
        if (HeroCandidates().Count > 0 && _heroIds.Count == 0) moving = true;
        InvalidateVisual();
        if (!moving) _motion?.Stop();
    }

    // ------------------------------------------------------------------ disegno
    private static void Px(DrawingContext ctx, string text, double px, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, px, color, r.Width, 1, align);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private static double Measure(string text, double px, Typeface face) =>
        UiText.Get(L10n.T(text), face, px, Colors.White).Width;

    // Zone sensibili al mouse (HoverZones): si ridisegna solo quando il mouse entra o esce da una di esse.
    private readonly HoverZones _hoverZones = new();
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private bool Hover(Rect r)
    {
        _hoverZones.Track(r);
        return r.Contains(_mouse);
    }

    public override void Render(DrawingContext ctx)
    {
        _hoverZones.Begin(_mouse);
        _hits.Clear();
        var page = new Rect(Bounds.Size);
        if (page.Width < 200 || page.Height < 200) return;
        double open = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 200, 0, 1);
        if (open < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        using var fade = ctx.PushOpacity(open);
        ctx.FillRectangle(new SolidColorBrush(Back), page);
        _hits.Add((page, "page", null));
        bool connected = TraktClient.IsConnected;
        if (connected)
        {
            if (_allRow != null) DrawAllGrid(ctx);
            else { DrawHero(ctx); DrawRecommendations(ctx); }
            // Scorrendo, la pagina passa sotto l'intestazione: in alto un velo del colore di fondo.
            double veil = Math.Clamp(_scrollPos / U(120), 0, 1);
            if (veil > 0.01)
            {
                double solid = U(92), soft = U(56);
                ctx.FillRectangle(new SolidColorBrush(UiTheme.WithAlpha(Back, 255 * veil)), new Rect(0, 0, page.Width, solid));
                ctx.FillRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new(UiTheme.WithAlpha(Back, 255 * veil), 0), new(UiTheme.WithAlpha(Back, 0), 1) },
                }, new Rect(0, solid, page.Width, soft));
            }
        }

        // Intestazione: "Consigliati  Film ⌄" in alto a sinistra, allineata al banner e alle file (sotto i pulsanti della finestra).
        double headTop = Math.Max(U(34), 34), headX = Side;
        _kindAnchor = default;
        if (connected && _allRow != null)
        {
            // Griglia "Vedi tutti": la freccia e il nome della fila tornano alle file.
            string rowTitle = _allRow == "lib" ? "Nella tua libreria" : "Da recuperare";
            double rw = Measure(rowTitle, U(30), Semibold);
            var back = new Rect(headX - U(8), headTop, U(34) + rw + U(16), U(44));
            bool backHot = Hover(back);
            var backInk = UiTheme.WithAlpha(Colors.White, backHot ? 255 : 235);
            double bx = headX + U(6), by = back.Center.Y, bz = U(9);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(backInk), Math.Max(1.8, U(2.8)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                new PolylineGeometry(new[] { new Point(bx + bz / 2, by - bz), new Point(bx - bz / 2, by), new Point(bx + bz / 2, by + bz) }, false));
            Px(ctx, rowTitle, U(30), Semibold, backInk, new Rect(headX + U(30), headTop, rw + 4, U(44)));
            Px(ctx, RowCount(_allRow).ToString(CultureInfo.InvariantCulture), U(30), Semibold, UiTheme.WithAlpha(Colors.White, 120), new Rect(headX + U(30) + rw + U(14), headTop, U(90), U(44)));
            _hits.Add((back, "all-back", null));
        }
        double headingW = Measure("Consigliati", U(30), Semibold);
        if (!(connected && _allRow != null)) Px(ctx, "Consigliati", U(30), Semibold, Colors.White, new Rect(headX, headTop, headingW + 4, U(44)));
        if (connected && _allRow == null)
        {
            string kindLabel = Kinds.First(k => k.Kind == _kind).Label;
            double kw = Measure(kindLabel, U(30), Semibold);
            _kindAnchor = new Rect(headX + headingW + U(16), headTop, kw + U(34), U(44));
            bool hot = Hover(_kindAnchor);
            Color ink = UiTheme.WithAlpha(Colors.White, hot ? 255 : 165);
            Px(ctx, kindLabel, U(30), Semibold, ink, new Rect(_kindAnchor.X, _kindAnchor.Y, kw + 4, _kindAnchor.Height));
            double cx = _kindAnchor.X + kw + U(18), cy = _kindAnchor.Center.Y + U(2), z = U(6);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(ink), Math.Max(1.6, U(2.4)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                new PolylineGeometry(new[] { new Point(cx - z, cy - z / 2), new Point(cx, cy + z / 2), new Point(cx + z, cy - z / 2) }, false));
            _hits.Add((_kindAnchor, "kind", null));
            var dot = new SolidColorBrush(UiTheme.WithAlpha(Colors.White, Hover(MoreBounds) ? 255 : 175));
            for (int i = -1; i <= 1; i++) ctx.DrawEllipse(dot, null, new Point(MoreBounds.Center.X + i * U(8), MoreBounds.Center.Y), U(2.5), U(2.5));
            _hits.Add((MoreBounds, "more", null));
        }
        var xr = CloseBounds.Deflate(U(11));
        var xpen = new Pen(new SolidColorBrush(UiTheme.WithAlpha(Colors.White, Hover(CloseBounds) ? 255 : 175)), Math.Max(1.6, U(2.2)), lineCap: PenLineCap.Round);
        ctx.DrawLine(xpen, xr.TopLeft, xr.BottomRight); ctx.DrawLine(xpen, xr.TopRight, xr.BottomLeft);
        _hits.Add((CloseBounds, "close", null));

        if (!connected)
        {
            var block = new Rect(Side * 2, page.Height / 2 - U(160), page.Width - Side * 4, U(180));
            if (!TraktClient.IsAvailable)
                Px(ctx, "Questa copia del player non ha le chiavi di Trakt.", U(19), Regular, MutedInk, block, TextAlignment.Center);
            else if (_code == null)
            {
                Px(ctx, "Consigli basati su quello che hai visto", U(34), Semibold, TextInk, new Rect(block.X, block.Y, block.Width, U(48)), TextAlignment.Center);
                var body = UiText.Get(L10n.T("Collega il tuo account Trakt: il player gli manda i film che finisci e i voti del diario,\ne Trakt risponde con i titoli che fanno per te, a partire da quelli che hai già in libreria."),
                    Regular, U(19), MutedInk, block.Width, 0, TextAlignment.Center, TextTrimming.None);
                ctx.DrawText(body, new Point(block.X, block.Y + U(64)));
                var button = new Rect((page.Width - U(220)) / 2, page.Height / 2 + U(60), U(220), U(48));
                ctx.DrawRectangle(new SolidColorBrush(UiTheme.PrimaryFill(Hover(button) && !_connecting)), null, button, U(24), U(24));
                Px(ctx, "Collega Trakt", U(19), Semibold, UiTheme.OnPrimary, button, TextAlignment.Center);
                if (!_connecting) _hits.Add((button, "connect", null));
            }
            else
            {
                Px(ctx, "Apri trakt.tv/activate e inserisci questo codice", U(19), Regular, MutedInk, new Rect(block.X, block.Y, block.Width, U(30)), TextAlignment.Center);
                Px(ctx, string.Join(" ", _code.UserCode.ToCharArray()), U(52), Semibold, TextInk, new Rect(block.X, block.Y + U(44), block.Width, U(90)), TextAlignment.Center);
                var button = new Rect((page.Width - U(220)) / 2, page.Height / 2 + U(100), U(220), U(48));
                ctx.DrawRectangle(new SolidColorBrush(UiTheme.PrimaryFill(Hover(button))), null, button, U(24), U(24));
                Px(ctx, "Apri la pagina", U(19), Semibold, UiTheme.OnPrimary, button, TextAlignment.Center);
                _hits.Add((button, "activate", null));
            }
        }
        if (_status.Length > 0)
        {
            if (!connected) Px(ctx, _status, U(19), Regular, SubtleInk, new Rect(Side, page.Height / 2 + U(170), page.Width - Side * 2, U(28)), TextAlignment.Center);
            else Px(ctx, _status, U(19), Regular, SubtleInk, new Rect(Side, page.Height - U(44), page.Width - Side * 2, U(26)));
        }
    }

    private void DrawRecommendations(DrawingContext ctx)
    {
        double scroll = Math.Round(_scrollPos);
        _results.TryGetValue(_kind, out var items);
        if (items == null)
        {
            // In attesa di Trakt: le sagome del banner e della fila con un riflesso che scorre, al loro posto vero.
            DrawHeroSkeleton(ctx, new Rect(0, -scroll, Bounds.Width, HeroZone));
            DrawRowSkeleton(ctx, HeroZone - U(96) - scroll);
            _contentHeight = 0;
            return;
        }
        if (items.Count == 0)
        {
            string text = "Trakt non ha ancora consigli per te: guarda qualcosa e torna qui.";
            Px(ctx, text, U(19), Regular, MutedInk, new Rect(Side, Bounds.Height / 2 - U(15), Bounds.Width - Side * 2, U(30)), TextAlignment.Center);
            _contentHeight = 0;
            return;
        }
        double cardW = CardWidth, cardH = CardHeight;
        int visible = Columns2;
        var rows = new[]
        {
            (Key: "lib", Title: "Nella tua libreria", Items: items.Where(i => i.LibraryPath != null).ToList()),
            (Key: "out", Title: "Da recuperare", Items: items.Where(i => i.LibraryPath == null).ToList()),
        };
        // Le file partono sulla parte bassa del banner, come nella Home.
        double y = (HeroHeight > 0 ? HeroHeight - U(96) : U(130)) - scroll;
        int rowIndex = -1;
        foreach (var (key, title, rowItems) in rows)
        {
            if (rowItems.Count == 0) continue;
            rowIndex++;
            double reveal = RowReveal(rowIndex), lift = Math.Round((1 - reveal) * U(28));
            y += lift;
            double hw = Measure(title, U(24), Semibold);
            Px(ctx, title, U(24), Semibold, UiTheme.WithAlpha(TextInk, 255 * reveal), new Rect(Side, y, hw + 4, U(34)));
            Px(ctx, rowItems.Count.ToString(CultureInfo.InvariantCulture), U(24), Semibold, UiTheme.WithAlpha(MutedInk, 255 * reveal), new Rect(Side + hw + U(12), y, U(80), U(34)));
            // "Vedi tutti" a destra, come nella Home: apre la griglia completa della fila.
            if (rowItems.Count > visible)
            {
                double sw = Measure("Vedi tutti", U(17), Regular);
                var see = new Rect(Bounds.Width - Side - sw - U(22), y, sw + U(22), U(34));
                bool seeHot = Hover(see);
                var seeInk = UiTheme.WithAlpha(seeHot ? TextInk : MutedInk, 255 * reveal);
                Px(ctx, "Vedi tutti", U(17), Regular, seeInk, new Rect(see.X, see.Y, sw + 4, see.Height));
                double ax = see.Right - U(6), ay = see.Center.Y, az = U(5);
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(seeInk), Math.Max(1.4, U(2)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                    new PolylineGeometry(new[] { new Point(ax - az / 2, ay - az), new Point(ax + az / 2, ay), new Point(ax - az / 2, ay + az) }, false));
                _hits.Add((see, "see-all", key));
            }
            y += U(50);
            var band = new Rect(0, y, Bounds.Width, cardH + U(12) + TitleHeight);
            if (!_rowTarget.ContainsKey(key)) { _rowTarget[key] = 0; _rowPos[key] = 0; }
            _rowTarget[key] = Math.Clamp(_rowTarget[key], 0, Math.Max(0, rowItems.Count - visible));
            double pos = _rowPos.TryGetValue(key, out var p) ? p : 0;
            if (band.Bottom > 0 && band.Y < Bounds.Height)
            {
                var lane = new Rect(Side, band.Y, Bounds.Width - Side * 2, band.Height);
                using (ctx.PushClip(new Rect(Side - U(6), band.Y - U(8), Bounds.Width - Side * 2 + U(12), band.Height + U(16))))
                {
                    int first = Math.Max(0, (int)Math.Floor(pos) - 1), last = Math.Min(rowItems.Count - 1, (int)Math.Ceiling(pos) + visible);
                    for (int i = first; i <= last; i++)
                    {
                        double x = Side + Math.Round((i - pos) * (cardW + Gap));
                        var card = new Rect(x, band.Y, cardW, cardH);
                        var item = rowItems[i];
                        var hit = new Rect(card.X, card.Y, card.Width, card.Height + U(12) + TitleHeight).Intersect(lane);
                        bool hot = Hover(hit);
                        if (hot) card = card.Translate(new Vector(0, -U(4)));
                        using (ctx.PushGeometryClip(new RectangleGeometry(card, U(8), U(8))))
                        {
                            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(255 * reveal), 24, 27, 32)), card);
                            if (Poster(item.PosterFile, cardW) is { } poster)
                            {
                                double k = Math.Max(card.Width / poster.Size.Width, card.Height / poster.Size.Height);
                                double pw = poster.Size.Width * k, ph = poster.Size.Height * k;
                                using (ctx.PushOpacity(reveal * PosterAlpha(item.PosterFile!, cardW)))
                                    ctx.DrawImage(poster, new Rect(card.X + (card.Width - pw) / 2, card.Y + (card.Height - ph) / 2, pw, ph));
                            }
                            else if (item.PosterFile == null)
                            {
                                var ft = UiText.Get(L10n.T(item.DisplayTitle), Regular, U(16), MutedInk, card.Width - 20, 4, TextAlignment.Center, TextTrimming.WordEllipsis);
                                ctx.DrawText(ft, new Point(card.X + 10, card.Center.Y - ft.Height / 2));
                            }
                        }
                        Px(ctx, item.DisplayTitle, U(16), Regular, UiTheme.WithAlpha(TextInk, (hot ? 255 : 200) * reveal), new Rect(card.X, card.Bottom + U(10), card.Width, TitleHeight));
                        if (hit.Width > 0) _hits.Add((hit, "item", item));
                    }
                }
                // Frecce ai lati, nei margini, solo passando sulla fila.
                if (Hover(band))
                {
                    double cy = band.Y + cardH / 2;
                    if (_rowTarget[key] > 0) RowArrow(ctx, new Rect(Side / 2 - U(22), cy - U(22), U(44), U(44)), true, key);
                    if (_rowTarget[key] < rowItems.Count - visible) RowArrow(ctx, new Rect(Bounds.Width - Side / 2 - U(22), cy - U(22), U(44), U(44)), false, key);
                }
            }
            y = band.Bottom + U(44) - lift;
        }
        _contentHeight = y + scroll + U(20);
    }

    // ------------------------------------------------------------------ caricamento: sagome con riflesso
    private IBrush Shimmer()
    {
        // Una fascia chiara che attraversa la pagina ogni 1,6 s (coordinate della pagina: tutte le sagome insieme).
        double t = Environment.TickCount64 % 1600 / 1600.0, w = Bounds.Width, x = -w * 0.4 + t * w * 1.8;
        var baseInk = UiTheme.WithAlpha(Colors.White, 16);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(x - U(260), 0, RelativeUnit.Absolute), EndPoint = new RelativePoint(x + U(260), U(120), RelativeUnit.Absolute),
            GradientStops = { new(baseInk, 0), new(UiTheme.WithAlpha(Colors.White, 34), 0.5), new(baseInk, 1) },
        };
    }

    /// <summary>Etichetta, titolo, trama e pulsante del banner come sagome, centrati come il testo vero.</summary>
    private void DrawHeroSkeleton(DrawingContext ctx, Rect area)
    {
        if (area.Bottom <= 0) return;
        var ink = Shimmer();
        double blockH = U(32) + U(68) + U(14) + U(29) + U(34) + U(50);
        double zoneTop = area.Y + U(100), zoneBottom = area.Bottom - U(96);
        double y = Math.Max(zoneTop, Math.Round((zoneTop + zoneBottom) / 2 - blockH / 2));
        double x = Side, w = Math.Min(U(780), Bounds.Width * 0.46);
        ctx.DrawRectangle(ink, null, new Rect(x, y + U(6), U(170), U(14)), U(7), U(7));
        ctx.DrawRectangle(ink, null, new Rect(x, y + U(32) + U(8), w * 0.72, U(52)), U(12), U(12));
        ctx.DrawRectangle(ink, null, new Rect(x, y + U(32) + U(68) + U(14) + U(6), w * 0.9, U(16)), U(8), U(8));
        ctx.DrawRectangle(ink, null, new Rect(x, y + blockH - U(50), U(170), U(50)), U(25), U(25));
    }

    private void DrawRowSkeleton(DrawingContext ctx, double y)
    {
        var ink = Shimmer();
        ctx.DrawRectangle(ink, null, new Rect(Side, y + U(6), U(220), U(22)), U(11), U(11));
        y += U(50);
        for (int i = 0; i < Columns2; i++)
        {
            double x = Side + i * (CardWidth + Gap);
            ctx.DrawRectangle(ink, null, new Rect(x, y, CardWidth, CardHeight), U(8), U(8));
            ctx.DrawRectangle(ink, null, new Rect(x, y + CardHeight + U(14), CardWidth * 0.7, U(14)), U(7), U(7));
        }
    }

    // ------------------------------------------------------------------ "Vedi tutti": griglia completa di una fila
    private string? _allRow;

    /// <summary>
    /// "Vedi tutti": la griglia della pagina Film della libreria, con le stesse misure (colonne da ~176 punti, 20 di
    /// spazio, margini del 3,6%, locandina 2:3 con il nome sotto su due righe). Il titolo della fila sta nell'intestazione.
    /// </summary>
    private void DrawAllGrid(DrawingContext ctx)
    {
        double scroll = Math.Round(_scrollPos);
        _results.TryGetValue(_kind, out var items);
        var all = (items ?? new()).Where(i => _allRow == "lib" ? i.LibraryPath != null : i.LibraryPath == null).ToList();
        double ls = Math.Clamp(Bounds.Height / 1000, .45, 4);
        double L(double v) => Math.Max(1, Math.Round(v * ls));
        double pad = Math.Max(L(44), Math.Round(Bounds.Width * 0.036)), width = Math.Max(1, Bounds.Width - pad * 2);
        double gap = L(20), rowGap = L(28);
        int columns = (int)Math.Clamp(Math.Floor((width + gap) / Math.Max(1, L(176) + gap)), 4, 12);
        double cardW = Math.Max(1, Math.Floor((width - gap * (columns - 1)) / columns));
        double cardScale = Math.Clamp(cardW / 170, .78, 2.6);
        double capH = Math.Max(1, Math.Round(54 * cardScale)), posterH = Math.Round(cardW * 1.5), cardH = posterH + capH;
        double radius = Math.Max(1, Math.Round(6 * cardScale)), captionPx = Math.Max(9.4, 11.4 * cardScale) * 96 / 72 * 0.92;
        double top = L(156) - scroll;
        for (int i = 0; i < all.Count; i++)
        {
            int row = i / columns, col = i % columns;
            var r = new Rect(pad + col * (cardW + gap), top + row * (cardH + rowGap), cardW, cardH);
            if (r.Bottom < 0 || r.Y > Bounds.Height) continue;
            var item = all[i];
            bool hot = Hover(r);
            var poster = new Rect(r.X, r.Y, r.Width, posterH);
            using (ctx.PushGeometryClip(new RectangleGeometry(poster, radius, radius)))
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(24, 27, 32)), poster);
                if (Poster(item.PosterFile, cardW) is { } bmp)
                {
                    double k = Math.Max(poster.Width / bmp.Size.Width, poster.Height / bmp.Size.Height);
                    double pw = bmp.Size.Width * k, ph = bmp.Size.Height * k;
                    using (ctx.PushOpacity(PosterAlpha(item.PosterFile!, cardW)))
                        ctx.DrawImage(bmp, new Rect(poster.X + (poster.Width - pw) / 2, poster.Y + (poster.Height - ph) / 2, pw, ph));
                }
                else if (item.PosterFile == null)
                {
                    var ft = UiText.Get(L10n.T(item.DisplayTitle), Regular, U(16), MutedInk, poster.Width - 20, 4, TextAlignment.Center, TextTrimming.WordEllipsis);
                    ctx.DrawText(ft, new Point(poster.X + 10, poster.Center.Y - ft.Height / 2));
                }
            }
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(hot ? 150 : 40), 255, 255, 255)), hot ? 2 : 1), poster.Deflate(hot ? 1 : .5), radius, radius);
            var caption = UiText.Get(L10n.T(item.DisplayTitle), Regular, captionPx, TextInk, r.Width - 2, 2);
            ctx.DrawText(caption, new Point(r.X + 1, poster.Bottom + Math.Round(8 * cardScale)));
            _hits.Add((r, "item", item));
        }
        int rows = (all.Count + columns - 1) / Math.Max(1, columns);
        _contentHeight = L(156) + rows * (cardH + rowGap) - rowGap + L(24) + U(20);
    }

    private void ShowAll(string? row)
    {
        _allRow = row;
        _scroll = _scrollPos = 0;
        InvalidateVisual();
    }

    private void RowArrow(DrawingContext ctx, Rect r, bool left, string key)
    {
        double cx = r.Center.X, cy = r.Center.Y, z = U(9), d = left ? 1 : -1;
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(UiTheme.WithAlpha(TextInk, Hover(r) ? 255 : 190)), Math.Max(2, U(3)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
            new PolylineGeometry(new[] { new Point(cx + d * z / 2, cy - z), new Point(cx - d * z / 2, cy), new Point(cx + d * z / 2, cy + z) }, false));
        _hits.Add((r, left ? "row-left" : "row-right", key));
    }

    // ------------------------------------------------------------------ input
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouse = e.GetPosition(this);
        var hit = _hits.LastOrDefault(h => h.Bounds.Contains(_mouse));
        var cursor = hit.Kind is "item" or "close" or "more" or "kind" or "connect" or "activate" or "row-left" or "row-right" or "see-all" or "all-back" ? HandCursor : Cursor.Default;
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
        // Prima si ridisegnava tutta la pagina a ogni movimento del mouse.
        if (_hoverZones.Changed(_mouse)) InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _mouse = new Point(-1, -1); InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        var props = e.GetCurrentPoint(this).Properties;
        var p = e.GetPosition(this);
        var hit = _hits.LastOrDefault(h => h.Bounds.Contains(p));
        // Tasto destro (o Ctrl+clic) su un titolo: il menu dei titoli della libreria.
        if (props.IsRightButtonPressed || (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            if (hit.Kind == "item" && hit.Tag is TraktLibrary.Recommendation chosen) OpenItemMenu(chosen, p);
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        switch (hit.Kind)
        {
            case "close": Close(); break;
            case "connect": Connect(); break;
            case "activate": if (_code != null) OpenUrl(_code.VerificationUrl); break;
            case "more": OpenMoreMenu(); break;
            case "kind": OpenKindMenu(); break;
            case "see-all": ShowAll((string)hit.Tag!); break;
            case "all-back": ShowAll(null); break;
            case "row-left": case "row-right":
            {
                string key = (string)hit.Tag!;
                int visible = Columns2, count = RowCount(key);
                int current = _rowTarget.TryGetValue(key, out var t) ? t : 0;
                _rowTarget[key] = Math.Clamp(current + (hit.Kind == "row-left" ? -1 : 1) * Math.Max(1, visible - 1), 0, Math.Max(0, count - visible));
                StartMotion();
                break;
            }
            case "item" when hit.Tag is TraktLibrary.Recommendation item:
                if (item.LibraryPath != null) { string path = item.LibraryPath; Close(); LibraryItemChosen?.Invoke(path); }
                else if (item.Trakt.Slug is { } slug && Regex.IsMatch(slug, "^[a-z0-9-]+$"))
                    OpenUrl("https://trakt.tv/" + (item.Trakt.Kind == "tv" ? "shows/" : "movies/") + slug);
                break;
        }
    }

    private int RowCount(string row)
    {
        if (!_results.TryGetValue(_kind, out var items) || items == null) return 0;
        return row == "lib" ? items.Count(i => i.LibraryPath != null) : items.Count(i => i.LibraryPath == null);
    }

    /// <summary>Clic destro recuperato dalla finestra: il menu del titolo sotto il punto, se c'e'.</summary>
    public void OpenContextMenuAt(Point at)
    {
        var hit = _hits.LastOrDefault(h => h.Bounds.Contains(at));
        if (hit.Kind == "item" && hit.Tag is TraktLibrary.Recommendation item) OpenItemMenu(item, at);
    }

    private void OpenItemMenu(TraktLibrary.Recommendation item, Point at)
    {
        var entries = new List<MenuEntry>();
        if (item.LibraryPath != null)
            entries.Add(new MenuEntry("Apri la scheda", "info", () => { string path = item.LibraryPath; Close(); LibraryItemChosen?.Invoke(path); }));
        if (item.Trakt.Slug is { } slug && Regex.IsMatch(slug, "^[a-z0-9-]+$"))
            entries.Add(new MenuEntry("Apri su Trakt", "network", () => OpenUrl("https://trakt.tv/" + (item.Trakt.Kind == "tv" ? "shows/" : "movies/") + slug)));
        entries.Add(new MenuEntry(item.Trakt.Kind == "tv" ? "Segna la serie come già vista" : "Segna come già visto", "check", () => _ = MarkWatched(item)));
        if (!string.IsNullOrWhiteSpace(item.Trakt.Slug))
            entries.Add(new MenuEntry("Non mi interessa", "hide", () => _ = HideRecommendation(item)));
        MenuRequested?.Invoke(entries, at);
    }

    /// <summary>Toglie il titolo dai consigli mostrati (file, banner e griglia) dopo che Trakt ha confermato.</summary>
    private void DropRecommendation(TraktLibrary.Recommendation item)
    {
        foreach (var list in _results.Values)
            list?.RemoveAll(r => r.Trakt.TmdbId == item.Trakt.TmdbId && r.Trakt.Kind == item.Trakt.Kind);
        int at = _heroIds.IndexOf(item.Trakt.TmdbId);
        if (at >= 0)
        {
            _heroIds.RemoveAt(at);
            _heroPrevIndex = -1;
            if (_heroIndex >= _heroIds.Count) _heroIndex = 0;
            _heroFadeAt = Environment.TickCount64; _heroShownAt = Environment.TickCount64;
        }
        StartMotion();
        InvalidateVisual();
    }

    private async Task MarkWatched(TraktLibrary.Recommendation item)
    {
        try
        {
            await TraktClient.MarkWatchedAsync(item.Trakt, _lifetime.Token);
            DropRecommendation(item);
            Flash("“" + item.DisplayTitle + "” segnato come visto su Trakt");
        }
        catch (Exception ex) { Console.Error.WriteLine("[TRAKT] segna visto: " + ex.Message); Say("Non riesco a segnarlo su Trakt  ·  " + ex.Message); }
    }

    private async Task HideRecommendation(TraktLibrary.Recommendation item)
    {
        try
        {
            await TraktClient.HideRecommendationAsync(item.Trakt, _lifetime.Token);
            DropRecommendation(item);
            Flash("Trakt non consiglierà più “" + item.DisplayTitle + "”");
        }
        catch (Exception ex) { Console.Error.WriteLine("[TRAKT] non mi interessa: " + ex.Message); Say("Non riesco a scartarlo su Trakt  ·  " + ex.Message); }
    }

    private void OpenMoreMenu()
    {
        string user = TraktClient.UserName;
        var entries = new List<MenuEntry>();
        entries.Add(new MenuEntry("Segna su Trakt quello che finisci di guardare", "check", () => { TraktClient.MarkWatched = !TraktClient.MarkWatched; InvalidateVisual(); }) { Checked = TraktClient.MarkWatched });
        entries.Add(MenuEntry.Sep());
        entries.Add(new MenuEntry(user.Length > 0 ? "Scollega " + user : "Scollega Trakt", "exit", Disconnect));
        // Sotto i tre puntini, allineato al loro bordo destro (prima partiva da un punto fisso e cadeva a caso).
        RightMenuRequested?.Invoke(entries, new Point(MoreBounds.Right, MoreBounds.Bottom + U(10)));
    }

    private void OpenKindMenu()
    {
        var entries = Kinds.Select(k => new MenuEntry(k.Label, null, () =>
        {
            _kind = k.Kind; _allRow = null; _scroll = _scrollPos = 0; _heroIndex = 0; _heroPrevIndex = -1; _heroIds.Clear(); _heroShownAt = Environment.TickCount64; _rowTarget.Clear(); _rowPos.Clear();
            LoadKind(k.Kind); InvalidateVisual();
        }) { Checked = k.Kind == _kind }).ToList<MenuEntry>();
        MenuRequested?.Invoke(entries, new Point(_kindAnchor.X, _kindAnchor.Bottom + U(14)));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        _scroll = Math.Clamp(_scroll - e.Delta.Y * U(110), 0, MaxScroll);
        StartMotion();
    }

    public void HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: if (_allRow != null) ShowAll(null); else Close(); break;
            case Key.Down: case Key.PageDown: _scroll = Math.Clamp(_scroll + U(e.Key == Key.Down ? 220 : 600), 0, MaxScroll); StartMotion(); break;
            case Key.Up: case Key.PageUp: _scroll = Math.Clamp(_scroll - U(e.Key == Key.Up ? 220 : 600), 0, MaxScroll); StartMotion(); break;
            case Key.Home: _scroll = 0; StartMotion(); break;
            default: return;
        }
        e.Handled = true;
    }

    internal void QaMore() => OpenMoreMenu();
    internal void QaShowAll(string row) => ShowAll(row.Length == 0 ? null : row);
    internal string QaSummary => $"available={TraktClient.IsAvailable} connected={TraktClient.IsConnected} code={_code?.UserCode ?? "-"} status={_status}";
}
