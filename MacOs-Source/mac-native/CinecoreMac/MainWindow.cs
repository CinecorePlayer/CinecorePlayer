using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Interactivity;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Finestra del player: trasparente e senza bordi, con l'HUD nativo. Sotto, il VideoHost (Wine) apre la sua finestra
/// video esattamente nella stessa posizione e la segue (spostamenti, ridimensionamenti, schermo intero).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly HudView _hud = new();
    private readonly VideoHostClient _video = new();
    private string? _file;
    private string _renderer = Environment.GetEnvironmentVariable("CINECORE_RENDERER") ?? MacSettings.EffectiveRenderer;
    private float _volume = 1f;
    private bool _muted;
    private PixelRect _lastRect;
    private readonly DispatcherTimer _follow;
    private ProbeData? _probe;
    private RuntimeInfo _runtime = new();
    private readonly DispatcherTimer _infoRefresh;
    private readonly ContextMenuView _menu = new();
    private readonly TextMenuLayer _textMenu = new();
    private readonly ManualMixView _manualMix = new();
    private readonly AutomationsView _automations = new();
    private readonly MacGlass _mouse;
    private readonly SettingsView _settings = new();
    private readonly LibraryView _library = new();
    private readonly SpotlightView _spotlight = new();
    private readonly SubtitleSearchView _subtitleSearch = new();
    private readonly LibraryReportView _report = new();
    private readonly HdrAnalysisView _hdr = new();
    private readonly ComponentsView _components = new();
    private readonly WebAddressView _web = new();
    private readonly WledView _wled = new();
    private readonly GoToTimeView _goTo = new();
    private readonly YouTubeView _youtube = new();
    /// <summary>Loop brano: il file che riparte a fine riproduzione (uno solo, come SetSingleTrackLoop di Windows).</summary>
    private string? _loopFile;
    /// <summary>Ritaglio dal menu Video (0 = nessuno): vale per il film in corso, come su Windows.</summary>
    private double _cropAspect;
    private static readonly (double Aspect, string Label)[] CropChoices =
        { (4 / 3.0, "4:3"), (16 / 9.0, "16:9"), (1.85, "1,85:1"), (2.0, "2,00:1"), (2.20, "2,20:1"), (2.35, "2,35:1"), (2.39, "2,39:1"), (2.76, "2,76:1") };

    /// <summary>Preferenza uscita audio (come Windows): vale da subito, il film riparte dallo stesso punto.</summary>
    private void SetBitstream(bool enabled)
    {
        if (MacSettings.Bitstream == enabled) return;
        MacSettings.Bitstream = enabled;
        _osd.Show(enabled ? "Uscita: Auto (bitstream se conviene)" : "Uscita: Forza PCM", 1800);
        // Si rifa' solo l'audio (il renderer resta acceso); il film si riapre solo se la sorgente non lo permette.
        if (_video.IsRunning && _file != null && _video.ReapplyAudioOutput()) { ApplyVolume(); return; }
        if (_video.IsRunning && _file is { } file)
        {
            double at = _hud.Position;
            StartVideo(file, _titleOverride);
            _resumeOnReady = at > 1 ? at : null;
        }
    }

    private void SetVideoCrop(double aspect)
    {
        _cropAspect = Math.Max(0, aspect);
        _video.Send("crop " + _cropAspect.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _sizingFile = null; // il formato del fotogramma e' cambiato: si rimisura
        ApplyImageSizing(animate: true);
        _osd.Show(_cropAspect <= 0 ? "Ritaglio disattivato" : "Ritaglio " + CropChoices.FirstOrDefault(c => Math.Abs(c.Aspect - _cropAspect) < 0.005).Label, 1600);
    }
    private readonly AudioSyncView _audioSync = new();
    private readonly TraktView _trakt = new();
    private readonly ScrobbleView _scrobble = new();
    private readonly UpdateView _update = new();
    private readonly PhotoEditView _photoEdit = new();
    private readonly PhotoViewerView _photoViewer = new();
    private readonly LoadingView _loading = new();
    private readonly OsdView _osd = new();
    private readonly DvdMenuView _dvdMenu = new();
    private readonly System.Diagnostics.Stopwatch _startClock = new();
    // Fine del caricamento solo quando il film e' davvero a schermo: madVR disegna il primo fotogramma ~250 ms dopo
    // "ready", e la schermata sfumava sul nero della finestra video (secondo lampo). Vedi FinishLoadingWhenDrawn.
    private bool _firstFrameSeen, _videoReady, _loadingFinishing, _raised;
    private int _loadToken;

    private void MoveToQaScreen()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCREEN") is not { Length: > 0 } wanted) return;
        try
        {
            // Per numero o per nome (es. "Cinecore Test", lo schermo virtuale delle prove: gli indici cambiano).
            var all = MacScreens.All();
            var screen = int.TryParse(wanted, out int n) ? (n >= 0 && n < all.Count ? all[n] : null)
                : all.Find(s => s.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (screen == null) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)(screen.X + screen.Width / 16), (int)(screen.Y + screen.Height / 16));
        }
        catch { }
    }

    private void SetLoadingRaised(bool on)
    {
        // Solo con l'app davanti e con madVR / MPC-VR: non deve mai galleggiare sopra le finestre di altre app.
        if (on && ((!IsActive && Environment.GetEnvironmentVariable("CINECORE_QA_RAISE") != "1") || Engine == "mpv")) on = false;
        if (_raised == on) return;
        _raised = on;
        WineWindowOrder.SetRaised(this, on);
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) Console.Error.WriteLine($"[tempi] {_startClock.ElapsedMilliseconds} ms finestra rialzata={on}");
    }

    private void FinishLoadingWhenDrawn()
    {
        if (!_videoReady || _loadingFinishing) return;
        // mpv manda ready dopo playback-restart; i renderer DirectShow mandano frame solo dopo aver disegnato.
        if (Engine == "mpv" || _firstFrameSeen) { FinishLoadingSoon(Engine == "mpv" ? 0 : 80); return; }
    }

    private void FinishLoadingSoon(int ms)
    {
        int token = _loadToken;
        void Done()
        {
            if (token != _loadToken || _closing || _loadingFinishing || !_loading.IsVisible) return;
            _loadingFinishing = true;
            _loading.Finish();
            WineWindowOrder.KeepAbove(this, _video.ProcessId);
        }
        if (ms <= 0) Done(); else DispatcherTimer.RunOnce(Done, TimeSpan.FromMilliseconds(ms));
    }
    private readonly TimelinePreview _timelinePreview;
    private readonly StartupSplashView _splash = new();
    private readonly MetadataEditView _metadata = new();
    private System.Collections.Generic.IReadOnlyList<(int Index, int Group, bool Selected, string Name)> _tracks = Array.Empty<(int, int, bool, string)>();
    private double? _resumeOnReady;
    private bool _pauseOnAudioReady;
    private double _initialPosition;
    private System.Collections.Generic.List<LibraryItem> _videoQueue = new();
    private bool _menuTestDone;

    public MainWindow(string? file)
    {
        _file = file;
        Title = "Cinecore Player";
        // Quasi tutto lo schermo (88%): la libreria e' pensata per lo schermo intero e in 1280×720 restava minuta.
        var screen = MainScreenPoints();
        Width = Math.Max(1280, Math.Round(screen.Width * 0.88)); Height = Math.Max(720, Math.Round(screen.Height * 0.88));
        // Sotto questa misura libreria, barra della musica, schede e HUD cominciavano a sovrapporsi: la finestra non
        // scende piu' (sta comodamente anche sullo schermo del MacBook Air 13", 1470x956 punti).
        MinWidth = WindowMinWidth; MinHeight = WindowMinHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Prove: CINECORE_QA_SCREEN=<n> apre la finestra sullo schermo n (es. quello virtuale del banco) e
        // CINECORE_QA_BACKGROUND=1 non la attiva, cosi' l'utente continua a lavorare sul suo schermo.
        if (Environment.GetEnvironmentVariable("CINECORE_QA_BACKGROUND") == "1")
        {
            ShowActivated = false;
            MacAppIcon.Restore();
            // macOS puo' attivare comunque l'app (schermo intero, chiusura dell'app che era davanti): la prova non
            // deve mai tenersi il primo piano.
            Activated += (_, _) => { Console.Error.WriteLine("[qa] attivata in secondo piano: primo piano restituito"); MacWindowChrome.DeactivateApp(); };
        }
        MoveToQaScreen();
        RestoreWindowPlacement();
        PlaceOnStartScreen();
        // Pulsanti di sistema (chiudi/riduci/schermo intero) come la cornice standard di Windows in finestra,
        // ma sopra il contenuto: la finestra resta trasparente sul video del VideoHost.
        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        // Il menu del tasto destro sta sopra l'HUD, nella stessa finestra (non sconfina fuori dal player).
        var settingsInputs = new Canvas();
        var libraryInputs = new Canvas();
        var metadataInputs = new Canvas();
        var spotlightInputs = new Canvas();
        _spotlight.InputLayer = spotlightInputs;
        _spotlight.Library = _library;
        _settings.InputLayer = settingsInputs;
        _metadata.InputLayer = metadataInputs;
        var surface = new Grid();
        foreach (var view in new Control[] { _dvdMenu, _pauseScreen, _hud, _loading, _subtitleSearch, _library, libraryInputs, _gate, _spotlight, spotlightInputs, _settings, settingsInputs, _metadata, metadataInputs, _report, _hdr, _components, _web, _wled, _goTo, _youtube, _audioSync, _trakt, _scrobble, _update, _photoViewer, _photoEdit, _manualMix, _automations, _textMenu, _menu, _pairing, _osd, _splash })
            surface.Children.Add(view);
        Content = surface;
        // Ascolti della musica letti in sottofondo (voce 36 di Windows): la prima apertura di Musica non li legge piu' sul thread dell'interfaccia.
        _ = System.Threading.Tasks.Task.Run(() => { try { CinecorePlayer2025.Utilities.MusicListeningStore.Preload(); } catch { } });
        // Livelli a pagina intera che lasciano libera la barra della musica (resta sempre visibile e usabile).
        _barAwareLayers = new Control[] { _subtitleSearch, _spotlight, spotlightInputs, _settings, settingsInputs, _metadata, metadataInputs, _report, _hdr, _components, _web, _wled, _goTo, _youtube, _audioSync, _trakt, _scrobble, _update, _manualMix, _automations, _pairing };
        _spotlight.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) spotlightInputs.IsVisible = _spotlight.IsVisible; };
        WireSpotlight();
        _subtitleSearch.VideoBehind = () => _hud.VideoVisible;
        _subtitleSearch.Downloaded += path => { LoadSubtitle(path); _hud.InfoLine = "Sottotitolo scaricato"; _hud.Poke(); };
        _subtitleSearch.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (_subtitleSearch.IsVisible) { _hud.ResetPanels(); _timelinePreview?.Dismiss(); _hud.IsVisible = false; }
            else if (!_library.IsVisible && !_settings.IsOpen) { _hud.IsVisible = true; _hud.Poke(); _hud.Focus(); }
        };
        // Libreria: senza un file da aprire l'app parte da qui; il pulsante libreria dell'HUD ci riporta.
        _library.IsVisible = file == null;
        _hud.IsVisible = file != null;
        _library.InputLayer = libraryInputs;
        libraryInputs.IsVisible = _library.IsVisible;
        _library.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) libraryInputs.IsVisible = _library.IsVisible; };
        _library.MetadataEditRequested += item => _metadata.Open(item);
        // Vetro di macOS della barra della musica: sta sopra la finestra, quindi si toglie quando un livello ci passa sopra.
        // I livelli che lasciano libera la barra (KeepMusicBarClear) non la bloccano, salvo che la tocchino davvero.
        _library.NativeGlassBlocked = rect =>
            _splash.IsVisible || _photoEdit.IsVisible || _photoViewer.IsVisible || _gate.IsVisible ||
            _barAwareLayers.Any(l => l.IsVisible && l is not Canvas && l.Bounds.Intersects(rect)) ||
            // Menu su vetro vero: sta in una finestra figlia sopra la barra, non serve togliere il vetro della barra
            // (prima la barra passava di colpo al vetro disegnato e sembrava ridisegnarsi e restringersi).
            (_menu.IsOpen && !_menu.HasNativeGlass && _menu.PanelBounds.Any(p => p.Inflate(24).Intersects(rect)));
        _library.FullscreenToggleRequested += ToggleFullscreen;
        WireAudioSync();
        WireCinema();
        _trakt.MenuRequested += (entries, at) => ShowTextMenu(_trakt, entries, at);
        _trakt.RightMenuRequested += (entries, at) => ShowTextMenu(_trakt, entries, at, alignRight: true);
        // Lingua cambiata (Impostazioni › Generali › Lingua): tutto si ridisegna con i testi nuovi.
        L10n.Changed += () =>
        {
            try { CinecorePlayer2025.Utilities.AppLanguage.Current = L10n.Language; } catch { }
            // [2026-11-22] Anche i dati di TMDb seguono la lingua (prima restavano sempre in italiano), aggiornati e non
            // rifatti da zero: vedi LibraryStore.LanguageChanged.
            try { MovieMetadataService.SetPreferredMetadataLanguage(L10n.Language); LibraryStore.LanguageChanged(); } catch { }
            foreach (var layer in ((Grid)Content!).Children) { layer.InvalidateMeasure(); layer.InvalidateVisual(); }
        };
        try { CinecorePlayer2025.Utilities.AppLanguage.Current = L10n.Language; } catch { }
        try { MovieMetadataService.SetPreferredMetadataLanguage(L10n.Language); LibraryStore.RefreshTitlesForLanguage(15000); } catch { }
        NativeDiagnostics.WatchDispatcher();
        MacSpaceGuard.Install();
        WireMediaKeys();
        WireManualMix();
        WireAutomations();
        WireAmplifier();
        _settings.AmplifierChanged += ReloadAmplifier;
        MacEngine.Changed += () =>
        {
            _settings.InvalidateVisual();
            _remoteRenderers = null;
            if (!MacEngine.Busy && MacEngine.Installed && MacEngine.State.Length == 0) _osd.Show("Motore madVR / MPC-VR installato", 3000);
        };
        _automations.Context = CurrentAutomationContext;
        _library.PhotoEditRequested += path => { if (!_photoEdit.Open(path)) _osd.Show("Questa foto non si può modificare", 2600); };
        // Visualizzatore delle foto con i controlli di Avalonia (prima disegnato a mano dentro la libreria).
        _library.PhotoViewerRequested += (items, index) => _photoViewer.Open(items, index);
        _photoViewer.EditRequested += path => { if (_photoEdit.Open(path)) _photoViewer.EditorOpened(); else _osd.Show("Questa foto non si può modificare", 2600); };
        _photoViewer.Deleted += _ => _library.PhotoDeletedOutside();
        _photoViewer.Closed += () => _library.Focus();
        _photoViewer.MenuRequested += (entries, at) => { ShowTextMenu(_photoViewer, entries, at, alignRight: true); if (!_textMenu.IsOpen) _photoViewer.MenuClosed(); };
        _textMenu.Closed += () => { if (_photoViewer.IsOpen) _photoViewer.MenuClosed(); };
        _photoEdit.Closed += saved => { if (_photoViewer.IsOpen) _photoViewer.PhotoEdited(saved); else _library.PhotoEdited(saved); };
        _scrobble.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _update.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _update.QuitRequested += Close;
        ScheduleUpdateCheck();
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") == null) TraktView.StartAutoSync();
        _trakt.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _trakt.LibraryItemChosen += path =>
        {
            // Durante la riproduzione la libreria non e' a schermo: solo l'avviso.
            if (_file != null && _video.IsRunning) { _osd.Show("È in libreria: " + Path.GetFileNameWithoutExtension(path), 2800); return; }
            ShowLibrary(true);
            _library.OpenDetailsForPath(path);
        };
        _report.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        WireHdrAnalysis();
        WireWeb();
        WireDisc();
        _components.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _library.MiniPlayerRequested += ToggleMiniPlayer;
        new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) =>
        {
            _library.IsWindowFullscreen = IsFullscreen;
            KeepMusicBarClear();
            _library.RefreshNativeGlass();
        }).Start();
        _metadata.Saved += _library.MetadataSaved;
        _metadata.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _metadata.MenuRequested += (entries, at) => ShowTextMenu(_metadata, entries, at);
        _library.VideoQueueRequested += items => { _videoQueue = items.Skip(1).ToList(); if (items.Count > 0) { _library.PauseMusic(); ShowLibrary(false); StartVideo(items[0].Path, items[0].Title); } };
        // "Metti in coda" dalla scheda (Windows): con un film in corso va in fondo alla coda, altrimenti parte subito.
        _library.Toast += text => _osd.Show(text, 2200);
        _library.QueueAddRequested += item =>
        {
            if (_video.IsRunning && _file != null) { _videoQueue.Add(item); _osd.Show("Aggiunto alla coda: " + item.Title, 2200); }
            else { _library.PauseMusic(); ShowLibrary(false); StartVideo(item.Path, item.Title); }
        };
        _library.PlayRequested += (item, resumeAt) => { _videoQueue.Clear(); _library.PauseMusic(); ShowLibrary(false); StartVideo(item.Path, item.Title); _resumeOnReady = resumeAt > 1 ? resumeAt : null; };
        _library.SettingsRequested += OpenSettings;
        _library.PairingRequested += RequestPairing;
        // Tasto destro su un punto vuoto della libreria: il menu del player, come su Windows.
        _library.MainMenuRequested += OpenLibraryMenu;
        _splash.Finished += () =>
        {
            if (_pendingPlaylist is { } pendingPlaylist) { _pendingPlaylist = null; DispatcherTimer.RunOnce(() => OpenFiles(new[] { pendingPlaylist }), TimeSpan.FromMilliseconds(300)); }
            if (_library.IsVisible)
            {
                _library.Focus(); _library.MaybeStartOnboarding();
                // Ascolti in attesa (Last.fm, ListenBrainz): si inviano appena c'e' rete.
                if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") == null) CinecorePlayer2025.Utilities.MusicScrobbler.Start();
                // Spotlight all'avvio (non sopra la guida del primo avvio).
                if (MacSettings.Get("spotlightHome", false) && !_library.OnboardingActive && Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") == null) OpenSpotlight();
            }
            else _hud.Focus();
        };
        _library.ThemeToggleRequested += light =>
        {
            MacSettings.ThemeMode = light ? "light" : "dark"; MacSettings.Background = ""; MacSettings.Accent = "";
            UiTheme.ApplyTone(light);
        };
        _library.PickFolder = async () =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Aggiungi cartella", AllowMultiple = false });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        };
        _settings.InterfaceChanged += () => _library.InvalidateVisual();
        _settings.CredentialsSaved += () => { LibraryStore.RefreshMetadataRequests(); _library.Reload(); };
        _settings.KeychainAuthorizationRequested += () => _ = AuthorizeSavedCredentialsAsync();
        _settings.LibrarySourceChanged += () => _library.SourceChangedFromSettings();
        _library.KeychainAuthorizationRequested += () => _ = AuthorizeSavedCredentialsAsync();
        ApplySavedTheme();
        _hud.SettingsClicked += OpenSettings;
        // Impostazioni › Dispositivi › Abbina il telefono: come su Windows le impostazioni si chiudono e compare il codice.
        _settings.DeviceServiceRequested += service =>
        {
            switch (service)
            {
                case "pair": _settings.Close(); RequestPairing(); break;
                case "wled": _wled.Open(); break;
                case "automations": _automations.Open(); break;
                case "trakt": _trakt.Open(LibraryView.CollectTitlesForRecommendations()); break;
                case "scrobble": _scrobble.Open(); break;
                case "update-app": _update.Open(); break;
                case "update-components": _components.Open(); break;
                case "engine-install": MacEngine.StartInstall(); break;
                case "engine-cancel": MacEngine.Cancel(); break;
            }
        };
        _wled.Closed += () => { if (_settings.IsOpen) _settings.Focus(); else if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        // Luci WLED: seguono lo stato del film (parte, pausa, fine) senza toccare il motore.
        new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
            (_, _) => MacWled.Update(_file != null && _video.IsRunning && _gatePending == null, _hud.IsPlaying)).Start();
        _hud.PrevChapterClicked += () => JumpChapter(-1);
        _hud.NextChapterClicked += () => JumpChapter(+1);
        // Anteprima sopra la barra del tempo: fotogrammi a 256 punti (a 512 pixel sugli schermi Retina).
        _timelinePreview = new TimelinePreview((tiles, sec) => _hud.SetPreview(tiles, sec));
        _hud.PreviewRequested += sec =>
        {
            _timelinePreview.FrameWidth = (int)Math.Round(256 * Math.Min(2, RenderScaling));
            _timelinePreview.SetSource(MediaPath(_file), _probe, _hud.Duration);
            _timelinePreview.Request(sec, _hud.Duration);
        };
        _hud.PreviewDismissed += () => _timelinePreview.Dismiss();
        _settings.CloseRequested += () => { if (_library.IsVisible) { _library.Focus(); return; } _hud.IsVisible = true; _hud.Poke(); _hud.Focus(); UpdateWindowButtons(); };
        _settings.ImageSizing = () => _imageSizing;
        _settings.RemoteBitstream = SetBitstream;
        _settings.ImageSizingChanged = ImageSizingChanged;
        _settings.RendererChanged += r => { if (Environment.GetEnvironmentVariable("CINECORE_RENDERER") == null) SetRenderer(r); };
        _settings.SystemUsesLightTheme = SystemUsesLightTheme;
        _settings.HostCommand += c => { if (_renderer == "madvr") _video.Send(c); };
        _settings.SpatialAudioChanged += () => { if (_file != null && _video.IsRunning) _video.SpatialAudioChanged(); };
        AddHandler(PointerPressedEvent, OpenPlaybackContextMenu, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (MacGlass.Available) _menu.Native = new MacGlass(this);
        _menu.SizeChanged += (_, _) => { if (_menu.IsOpen) _menu.Close(); };
        // Avvisi a scelta dell'HUD (risparmio energetico, ripresa del disco): con il menu aperto restano in sospeso e
        // ricompaiono alla chiusura, il menu sta sempre sopra.
        _menu.Opened += () => _hud.PromptsHeld = true;
        _menu.Closed += _ => _hud.PromptsHeld = false;
        _menu.Closed += p => { if (_metadata.IsOpen) _metadata.Focus(); else if (_library.IsVisible) _library.Focus(); else { _hud.MenuClosed(p); _hud.Focus(); } };
        // Un clic su un'altra applicazione chiude il menu; il solo cambio di focus no (la finestra di Wine lo prende
        // per un attimo all'avvio del video, e su Windows il menu ignora di proposito la disattivazione).
        _mouse = new MacGlass(this);
        _menu.MouseDownOutside = _mouse.MouseDownOutsideWindow;
        _video.Tracks += list => PostVideoEvent(() =>
        {
            _tracks = list;
            // Traccia del film scelta mentre c'era quella esterna: dopo la riapertura si sceglie quella.
            if (_audioOrdinalOnReady is int wanted && list.Where(t => t.Group == 1).ElementAtOrDefault(wanted) is { Index: var index, Selected: false } && list.Any(t => t.Group == 1))
            { _audioOrdinalOnReady = null; _video.Send("select " + index); return; }
            RestoreTrackChoice();
        });

        _hud.PlayPauseClicked += TogglePlay;
        // Menu dei DVD: clic e passaggio del mouse sui pulsanti del disco.
        _hud.VideoClick = p => _video.ActiveDvdNav is { Buttons: > 0 } nav && _dvdMenu.ToFrame(p) is { } f && nav.Click(f.X, f.Y);
        _hud.VideoHover = p => { if (_video.ActiveDvdNav is { Buttons: > 0 } nav && _dvdMenu.ToFrame(p) is { } f) nav.Hover(f.X, f.Y); };
        _hud.SkipClicked += s => { if (ScanActive) StepScan(Math.Sign(s), fromHud: true); else SeekTo(_hud.Position + s); };
        _hud.ScanStepRequested += dir => StepScan(dir, fromHud: true);
        _hud.SeekRequested += SeekTo;
        _hud.FullscreenClicked += ToggleFullscreen;
        _hud.OpacityChanged += _ => ApplyWindowButtonsAlpha();
        _hud.CloseClicked += () => Close();
        // Come Windows (ShowDefaultLibrary con stopCurrent): il tasto libreria ferma il film (punto di ripresa salvato) e
        // apre la libreria. Prima il film (anche YouTube) continuava a suonare sotto.
        _hud.OpenClicked += () =>
        {
            if (_file != null) { _spotlightReturnPath = null; CloseFile(); }
            else ShowLibrary(true);
        };
        _hud.MuteClicked += () => { if (TryAmplifierVolume(toggleMute: true)) return; _muted = !_muted; _hud.IsMuted = _muted; ApplyVolume(); };
        _hud.VolumeChanged += v => { if (TryAmplifierVolume(value: v)) return; _volume = v; _muted = false; ApplyVolume(); RememberFilmVolume(); };
        _hud.DragWindowRequested += e => { if (!IsFullscreen) BeginMoveDrag(e); };
        _library.WindowDragRequested += e => { if (!IsFullscreen) BeginMoveDrag(e); };
        // Scheda Info: il pulsante "i" la apre e la chiude, la freccia, Esc e su la chiudono.
        _hud.InfoClicked += () => { if (_hud.InfoOpen) _hud.CloseInfo(); else _hud.OpenInfo(); };
        _hud.InfoCloseRequested += () => _hud.CloseInfo();
        // Cast e qualita' nella fascia in alto, come su Windows (TopCast, TopQuality).
        _hud.ImageLoader = (path, width) => _library.LoadImage(path, width);
        LibraryView.ImageReady += () => { if (_hud.CastOpen) _hud.InvalidateVisual(); };
        _hud.CastClicked += ToggleCast;
        _hud.SkipIntroClicked += SkipDetectedIntro;
        _hud.RendererPromptChosen += OnRendererPromptChosen;
        _hud.NextEpisodeClicked += OpenNextEpisodeFromPrompt;
        _hud.QualityClicked += ShowQualityMenu;
        _hud.InfoProvider = BuildInfo;
        _infoRefresh = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { if (_hud.InfoOpen) RefreshInfoAsync(); });
        _infoRefresh.Start();
        _video.Media += line => PostVideoEvent(() => { _runtime.ParseMedia(line); RefreshQualityLabel(); _hud.InfoChanged(); });
        _video.Dropped += n => PostVideoEvent(() => { _runtime.Dropped = n; WatchMadVrStutter(n + (_qaDropsSim += _qaDropsPerSec)); });

        _video.Position += (pos, dur, playing) => PostVideoEvent(() =>
        {
            _lastKnownPos = pos;
            _hud.Position = pos; _hud.IsPlaying = playing;
            // La durata del VideoHost vale solo se c'e': prima dell'avvio manda 0, e la si conosce gia' (libreria, ffprobe).
            if (dur > 0) _hud.Duration = dur;
            // Menu dei DVD: niente timeline (la durata del film letta all'apertura non vale per il menu).
            bool dvdMenu = _video.ActiveDvdNav is { InMenu: true };
            if (dvdMenu) _hud.Duration = 0;
            UpdateIntroOutroPrompts(pos);
            JellyfinPlaybackProgress(pos, dur, playing);
            // Diario (WatchHistoryStore di Windows): il registro scrive al piu' ogni 20 s, e subito al 90%.
            if (playing && !dvdMenu && _file is { } watching && !IsWebVideo(watching) && pos >= 1) { string title = _hud.Title, identity = StreamIdentity(watching); _ = System.Threading.Tasks.Task.Run(() => CinecorePlayer2025.Engines.WatchHistoryStore.RecordProgress(identity, title, pos, dur)); }
        });
        _video.Ready += r => PostVideoEvent(() =>
        {
            if (_videoReady) return;
            _videoReady = true;
            _wineRecoveryTried = false;
            OfferDvdResume();
            ApplyVolume();
            BeginAmplifierSafeStart();
            if (_audioOffsetMs != 0) _video.Send("audio-offset " + _audioOffsetMs);
            if (_cropAspect > 0) _video.Send("crop " + _cropAspect.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _sentScale = 1;
            ApplyImageSizing();
            if (StereoAuto) DetectStereo(announce: false);
            else if (_stereo != StereoMode.None || _extendedFs) SendStereoOutput();
            MatchDisplayRate();
            // Ritardo salvato "a curva" (sincronizzazione a tratti): lo segue il timer.
            _delayFollowApplied = int.MinValue; StartDelayFollower();
            if (_externalSubtitle != null) LoadSubtitle(_externalSubtitle);
            _hud.InfoLine = r.StartsWith("madvr", StringComparison.Ordinal) ? "madVR" : r.StartsWith("mpcvr", StringComparison.Ordinal) ? "MPC Video Renderer" : r.StartsWith("mpv", StringComparison.Ordinal) ? "MPV" : r;
            _runtime.Renderer = r;
            // Cambio di renderer dal menu: si riprende dallo stesso punto.
            double startPosition = _initialPosition;
            _hud.IsPlaying = !_pauseOnAudioReady;
            if (_pauseOnAudioReady) _video.Pause();
            _pauseOnAudioReady = false;
            _hud.VideoVisible = true;
            FinishLoadingWhenDrawn();
            Console.Error.WriteLine($"[tempi] film pronto in {_startClock.ElapsedMilliseconds} ms");
            if (_file is { } playing && IsStream(playing)) JellyfinPlaybackOpened(startPosition);
            if (_file is { } started && !IsWebVideo(started)) { string startedTitle = _hud.Title, identity = StreamIdentity(started); _ = System.Threading.Tasks.Task.Run(() => CinecorePlayer2025.Engines.WatchHistoryStore.RecordStarted(identity, startedTitle)); }
            _hud.InvalidateVisual();
            SyncVideoRect();
            // La finestra di Wine compariva sopra: l'HUD torna davanti (ordine, senza rubare il primo piano ad altre app).
            WineWindowOrder.KeepAbove(this, _video.ProcessId);
            // Tornando attiva, AppKit rimette il materiale grigio della barra del titolo: la fascia "incollata" sopra
            // il film con i pulsanti della finestra. Lo si rinasconde subito e di nuovo poco dopo.
            RefreshWindowChrome();
            DispatcherTimer.RunOnce(RefreshWindowChrome, TimeSpan.FromMilliseconds(400));
            // Solo per le prove (catture dello schermo): la scheda Info si apre da sola.
            if (Environment.GetEnvironmentVariable("CINECORE_HUD_INFO") == "1")
                DispatcherTimer.RunOnce(() => _hud.OpenInfo(), TimeSpan.FromSeconds(4));
            if (Environment.GetEnvironmentVariable("CINECORE_HUD_PREVIEW") is { Length: > 0 } previewTest
                && double.TryParse(previewTest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double previewAt))
                DispatcherTimer.RunOnce(() => _hud.TestPreview(previewAt), TimeSpan.FromSeconds(4));
            // Solo per le prove: CINECORE_HUD_MENU="x,y[,Voce[,Voce]]" apre il menu in quel punto e i sottomenu indicati.
            if (!_menuTestDone && Environment.GetEnvironmentVariable("CINECORE_HUD_MENU") is { Length: > 0 } menuTest && (_menuTestDone = true))
                DispatcherTimer.RunOnce(() =>
                {
                    var f = menuTest.Split(',');
                    ShowContextMenu(new Point(double.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture)));
                    for (int i = 2; i < f.Length; i++) _menu.TestOpenSubmenu(f[i]);
                }, TimeSpan.FromSeconds(4));
        });
        _video.Exited += () => PostVideoEvent(OnVideoExited);
        _loading.Library = _library;
        // Il livello torna normale solo quando la dissolvenza e' terminata, senza esporre la finestra nera sotto.
        _loading.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && !_loading.IsVisible) { SetLoadingRaised(false); WineWindowOrder.KeepAbove(this, _video.ProcessId); } };
        // Cambiando app durante il caricamento Cinecore non deve restare sospeso sopra la nuova finestra.
        Deactivated += (_, _) => { SetLoadingRaised(false); WineWindowOrder.SetRaised(this, false); };
        // Letture lente del menu del tasto destro gia' pronte al primo clic.
        MacDisc.RefreshOpticalVolumes(); LocalAddresses.Refresh();
        Activated += (_, _) =>
        {
            _scrobble.AppActivated();
            MacDisc.RefreshOpticalVolumes(); // un disco inserito mentre la finestra era dietro: il menu lo vede subito
            LocalAddresses.Refresh();
            if (_loading.IsVisible) SetLoadingRaised(true);
            _videoNeedsOrder = true;
            _nextStackCheck = 0;
        };
        _video.FirstFrame += () => PostVideoEvent(() => { _firstFrameSeen = true; if (_renderer is "madvr" or "mpcvr") MacEngine.FirstFrameShown(); FinishLoadingWhenDrawn(); });
        // Fasi del caricamento dai messaggi veri del VideoHost (come LoadingStage su Windows).
        _video.Progress += line => PostVideoEvent(() =>
        {
            if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) Console.Error.WriteLine($"[tempi] {_startClock.ElapsedMilliseconds} ms {line[..Math.Min(60, line.Length)]}");
            // La finestra video di Wine nasce a meta' avvio e si metteva sopra la schermata di caricamento (schermo
            // nero fino al film): finche' si carica, la nostra torna davanti a ogni passo del VideoHost.
            if (line == "info stats madvr alla ripresa") { _osd.Show("Statistiche madVR: compaiono alla ripresa", 2600); return; }
            if (line == "replaced") { StreamReplaced(); return; }
            if (_loading.Active) WineWindowOrder.KeepAbove(this, _video.ProcessId);
            if (line.StartsWith("info LAV Splitter Source", StringComparison.Ordinal)) _loading.Stage(.55, "Apro il film…");
            else if (line.StartsWith("info pin ", StringComparison.Ordinal) || line.StartsWith("info XySubFilter", StringComparison.Ordinal)) _loading.Stage(.78, "Preparo l’immagine…");
            else if (line.StartsWith("info madVR", StringComparison.Ordinal) || line.StartsWith("info LAV Video", StringComparison.Ordinal)) _loading.Stage(.88, "Quasi pronto…");
            else _loading.Stage(.32, "Preparo la riproduzione…");
        });
        _video.Ended += () => PostVideoEvent(() =>
        {
            _hud.IsPlaying = false;
            if (_file is { } finished && !IsWebVideo(finished)) { string t = _hud.Title, identity = StreamIdentity(finished); double d = _hud.Duration; _ = System.Threading.Tasks.Task.Run(() => CinecorePlayer2025.Engines.WatchHistoryStore.RecordCompleted(identity, t, d)); }
            ScheduleTraktDiarySync();
            // Fine della demo: parte il film che aspettava.
            if (StartFilmAfterDemo()) return;
            // Loop brano: lo stesso file riparte dall'inizio (prima della coda, come su Windows).
            if (_loopFile != null && _file is { } looped && string.Equals(_loopFile, looped, StringComparison.Ordinal))
            {
                _resumeOnReady = null;
                StartVideo(looped, _titleOverride);
                return;
            }
            // Playlist di video: a fine film parte il successivo.
            if (_videoQueue.Count > 0) { var next = _videoQueue[0]; _videoQueue.RemoveAt(0); SaveResumePoint(); StartVideo(next.Path, next.Title); return; }
            CloseFile();
        });
        // Il film non si apre (file sparito dal server, formato non letto): lo si dice invece di restare sul nero.
        _video.Error += message => PostVideoEvent(() =>
        {
            if (_hud.VideoVisible) return;
            // Come Windows (SafeStop con ritorno alla home + messaggio a schermo): si torna alla libreria e la
            // capsula in alto al centro dice cosa e' successo, invece di una scritta nella barra del titolo.
            Console.Error.WriteLine("[video] apertura non riuscita: " + message);
            FailVideoStartup();
        });

        // La finestra di Wine segue la nostra: controllo leggero, il comando parte solo quando cambia qualcosa.
        MpvEngine.Owner = this;
        _follow = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            UpdateWindowButtons();
            SyncVideoRect();
            PollFullscreenContextClick();
            UpdateVideoStack();
        });
        // Anche MPV è una finestra separata. Durante l'animazione di AppKit può
        // seguire ogni cambio di geometria senza passare dalla coda di Wine.
        PositionChanged += (_, _) => { TrackWindowPlacement(); if (Engine == "mpv") SyncVideoRect(); };
        SizeChanged += (_, _) => { TrackWindowPlacement(); if (Engine == "mpv") SyncVideoRect(); };
        Opened += (_, _) =>
        {
            MoveToQaScreen();
            RestoreWindowMaximized();
            TrackWindowPlacement();
            _library.ObserveVolumes();
            StartAudioOutputWatch();
            if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null && Environment.GetEnvironmentVariable("CINECORE_QA_BACKGROUND") != "1") Activate();
            MacWindowChrome.SetApplicationIcon();
            if (Environment.GetEnvironmentVariable("CINECORE_QA_NO_REMOTE") != "1") StartRemote();
            _follow.Start();
            RefreshWindowChrome();
            if ((MacSettings.StartFullscreen || Environment.GetEnvironmentVariable("CINECORE_FULLSCREEN") == "1") && !IsFullscreen) ToggleFullscreen();
            // Playlist .wpl passata all'avvio (associazione dei file su Windows): prima la libreria, poi l'importazione.
            if (_file != null && WplPlaylist.IsPlaylistFile(_file)) { string wpl = _file; _file = null; _pendingPlaylist = wpl; }
            // Foto, musica, CD: lo stesso smistamento dei file aperti dopo (prima ogni file partiva come video e per un
            // attimo si vedeva il caricamento del player con il suo HUD in basso).
            if (_file is { } startup && !IsStream(startup) && (LibraryStore.IsPhotoPath(startup) || MacAudioCd.IsAudioCd(startup) ||
                CinecorePlayer2025.Utilities.MediaFileKinds.IsAudioExtension(Path.GetExtension(startup).ToLowerInvariant())))
            {
                _file = null;
                _splash.Dismiss();
                OpenFiles(new[] { startup });
            }
            else if (_file != null) StartVideo(_file);
            else if (!_openedFromOutside) _splash.Start(() => _library.StartupContentReady);
            RunQaScript();
            _hud.Focus();
            if (Environment.GetEnvironmentVariable("CINECORE_LIBRARY") is { Length: > 0 } libraryTest)
                foreach (var part in libraryTest.Split(',')) _library.TestNavigate(part);
            // Solo per le prove: CINECORE_SETTINGS_TAB="Scheda[,player|components]" apre le Impostazioni
            // (CINECORE_SETTINGS e' il file delle impostazioni di prova, vedi MacSettings).
            if (Environment.GetEnvironmentVariable("CINECORE_SETTINGS_TAB") is { Length: > 0 } settingsTest)
                DispatcherTimer.RunOnce(() =>
                {
                    OpenSettings();
                    foreach (var part in settingsTest.Split(',')) _settings.TestOpenMenu(part);
                }, TimeSpan.FromSeconds(_file != null ? 6 : 1));
        };
        IntroOutroScanService.StatusChanged += OnIntroOutroScanStatusChanged;
        Closing += async (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            SaveWindowPlacement();
            _remoteSnapshotTimer?.Stop();
            _audioOutputWatch?.Stop();
            StopRemoteAudioSync();
            _library.StopObservingVolumes();
            IntroOutroScanService.StatusChanged -= OnIntroOutroScanStatusChanged;
            var introOutroStopped = Task.Run(IntroOutroScanService.Shutdown);
            _manualMix.Close();
            try { CinecorePlayer2025.Utilities.MusicScrobbler.Stop(); } catch { }
            _closing = true; MacWled.RestoreOnExit(); MacDisplayRate.Restore(); System.Threading.Tasks.Task.Run(() => MacDisc.ReleaseMounts()); _menu.Close(); _splash.Dismiss(); _follow.Stop(); _revealTimerField?.Stop(); SaveResumePoint();
            _video.Dispose();
            Hide();
            // Prima lascia terminare il grafo e ripristinare il profilo temporaneo; uccidere subito wineserver
            // interrompeva anche questa pulizia. La finestra scompare subito, l'attesa non blocca il thread UI.
            await _video.Stopped;
            await introOutroStopped;
            await System.Threading.Tasks.Task.Run(VideoHostClient.StopWine);
            _remote?.Dispose();
            Close();
        };
        // Wine pronto prima che si scelga il film (vedi WarmUpWine), solo se servira': con mpv non parte affatto.
        if (Engine != "mpv") System.Threading.Tasks.Task.Run(VideoHostClient.WarmUpWine);
        System.Threading.Tasks.Task.Run(MpvEngine.WarmUpAudio);
        // madVR / MPC-VR: il VideoHost parte gia' ora e aspetta il film (vedi VideoHostClient.PrepareStandby).
        ScheduleStandby(6000);
        // Un file trascinato sulla finestra si apre subito.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is { Count: > 0 }) OpenFiles(paths);
        });
        KeyDown += OnKey;
        // Primo avvio dell'app installata dal .dmg: madVR, MPC Video Renderer e il loro motore si scaricano da soli, come
        // i componenti gia' spuntati dell'installer di Windows. Una volta sola: annullato, resta in Impostazioni › Aggiornamenti.
        DispatcherTimer.RunOnce(() =>
        {
            if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null || MacEngine.Installed || !MacEngine.CanInstall ||
                MacSettings.Get("engineAutoDownload", "") != "") return;
            MacSettings.Set("engineAutoDownload", "1");
            MacEngine.StartInstall();
            _osd.Show("Scarico madVR e MPC Video Renderer in sottofondo (Impostazioni › Aggiornamenti)", 5000);
        }, TimeSpan.FromSeconds(5));
    }

    /// <summary>Impostazioni sopra il player (ShowSettingsHudPage): l'HUD si nasconde, il film continua sotto.</summary>
    private void OpenSettings() => OpenSettings("Generali");

    private void OpenSettings(string tab)
    {
        _hud.ResetPanels();
        _timelinePreview.Dismiss();
        if (_menu.IsOpen) _menu.Close();
        if (_hud.InfoOpen) _hud.CloseInfo();
        _hud.IsVisible = false;
        _settings.Open(tab);
    }

    /// <summary>Il video arriva fino al bordo. Solo i comandi lasciano spazio ai pulsanti di sistema.</summary>
    private void UpdateWindowButtons()
    {
        _hud.IsFullscreen = IsFullscreen;
        if (_video.IsRunning) ApplyImageSizing(animate: true);
        ApplyWindowButtonsAlpha();
        if (!IsFullscreen)
        {
            _hud.TopChromeInset = 24;
            return;
        }
        // A schermo intero la barra di macOS scende sopra il film quando il puntatore tocca il bordo: la si rende
        // trasparente (solo i tre pulsanti) e la fascia alta dell'HUD scende dolcemente sotto di lei finche' resta
        // (12/11: la barra grigia copriva titolo e dati).
        if (_library.IsVisible) _hud.TopChromeInset = 0;
        _fullscreenBarTimer.Start();
    }

    private DispatcherTimer? _fullscreenBarTimerField;
    private DispatcherTimer _fullscreenBarTimer => _fullscreenBarTimerField ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => FullscreenBarTick());
    private void FullscreenBarTick()
    {
        if (!IsFullscreen || _closing) { _fullscreenBarTimer.Stop(); return; }
        if (_library.IsVisible || !_hud.IsVisible) return;
        double target = MacWindowChrome.ClearFullscreenBar(this) > 0 ? 24 : 0;
        double now = _hud.TopChromeInset;
        if (Math.Abs(target - now) > .01) _hud.TopChromeInset = Math.Abs(target - now) < .6 ? target : now + (target - now) * .32;
        ApplyWindowButtonsAlpha();
    }

    /// <summary>Durante il film i pulsanti della finestra seguono l'HUD; nei cambi di schermo intero spariscono.</summary>
    private void ApplyWindowButtonsAlpha()
    {
        bool page = _library.IsVisible || _settings.IsOpen || !_hud.IsVisible;
        // PiP: niente pulsanti di sistema sopra il video (il riquadro di Windows non li ha).
        // Mini player della musica: tutta la finestra e' vetro e i pulsanti di sistema ci finivano sotto, sfocati.
        MacWindowChrome.SetWindowButtonsAlpha(this, _fsTransition || _beforeMini != null ? 0 : page ? 1 : Math.Clamp(_hud.HudOpacity, 0f, 1f));
    }

    private void RefreshWindowChrome()
    {
        if (_closing) return;
        MacWindowChrome.MakeTitlebarClear(this);
        // WindowServer segue il canale alfa per l'ombra: la vignetta del
        // video creava un secondo contorno, anche attorno al centro trasparente.
        MacWindowChrome.SetWindowShadow(this, _library.IsVisible && !IsFullscreen);
    }

    /// <summary>La libreria sopra il player (il film, se c'e', continua sotto); chiudendola torna l'HUD.</summary>
    private void ShowLibrary(bool show)
    {
        if (show) { _spotlightReturnPath = null; if (_menu.IsOpen) _menu.Close(); _hud.ResetPanels(); _timelinePreview.Dismiss(); SaveResumePoint(); _library.Reload(); }
        _library.IsVisible = show;
        _hud.IsVisible = !show;
        if (show) _library.Focus(); else { _library.HideSearchSurface(); _hud.Poke(); _hud.Focus(); }
        if (show && IsFullscreen) _revealTimer.Start();
        MacWindowChrome.SetWindowShadow(this, show && !IsFullscreen);
        UpdateWindowButtons();
        ApplyWindowButtonsAlpha();
    }

    private bool SystemUsesLightTheme() =>
        PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light;

    /// <summary>Tema salvato: sfondo scelto, altrimenti chiaro, scuro o come macOS; poi l'accento.</summary>
    private void ApplySavedTheme()
    {
        if (MacSettings.Background.Length > 0 && Color.TryParse(MacSettings.Background, out var bg)) UiTheme.ApplyBackground(bg);
        else UiTheme.ApplyTone(MacSettings.ThemeMode switch { "light" => true, "dark" => false, _ => SystemUsesLightTheme() });
        if (MacSettings.Accent.Length > 0 && Color.TryParse(MacSettings.Accent, out var accent)) UiTheme.SetAccent(accent);
    }

    /// <summary>Il percorso da dare a FFmpeg (anteprime, vetro del menu): per un flusso l'indirizzo con il token, solo in memoria.</summary>
    private static string? MediaPath(string? file) => file != null && IsStream(file) ? CinecorePlayer2025.Utilities.JellyfinClient.WithAccess(file) : file;

    /// <summary>
    /// "Continua a guardare": il punto in cui ci si e' fermati. File locali nel registro delle riprese di Windows
    /// (PlaybackResumeStore, che toglie i titoli finiti); titoli Jellyfin al server, che tiene la ripresa per tutti i dispositivi.
    /// </summary>
    private void SaveResumePoint()
    {
        if (_file is not { } file || IsWebVideo(file)) return;
        file = StreamIdentity(file);
        double pos = _hud.Position, dur = _hud.Duration;
        if (dur <= 0 || pos <= 0) return;
        { string title = _hud.Title; _ = System.Threading.Tasks.Task.Run(() => CinecorePlayer2025.Engines.WatchHistoryStore.RecordProgress(file, title, pos, dur, force: true)); }
        try
        {
            if (SaveDvdResume(file, pos, dur)) return;
            if (!IsStream(file)) { CinecorePlayer2025.Engines.PlaybackResumeStore.SaveOrClear(file, pos, dur); return; }
            NetworkStore.NotePlayback(file, pos, dur);
            if (_jellyfinAccount != null && string.Equals(file, _jellyfinPath, StringComparison.OrdinalIgnoreCase))
            {
                _jellyfinPosition = pos; _jellyfinDuration = dur;
                JellyfinPlaybackStopped(waitBriefly: _closing);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("ripresa: " + ex.Message); }
    }


    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern uint CGMainDisplayID();
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern CGRect CGDisplayBounds(uint id);

    /// <summary>Misura in punti dello schermo principale (prima che la finestra esista).</summary>
    private static Size MainScreenPoints()
    {
        try { var b = CGDisplayBounds(CGMainDisplayID()); if (b.W > 100) return new Size(b.W, b.H - 60); } catch { }
        return new Size(1470, 896);
    }

    private static bool IsStream(string path) => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private double _lastKnownPos;
    private bool _closing;
    private int _restarts;

    /// <summary>
    /// Il VideoHost e' terminato senza che lo chiedessimo (raro crash all'avvio di Wine/DXMT): si riparte una volta
    /// dalla stessa posizione. Un secondo arresto lascia il player fermo.
    /// </summary>
    private void OnVideoExited()
    {
        _hud.IsPlaying = false;
        _hud.VideoVisible = false;
        _hud.InvalidateVisual();
        if (_closing || _file == null || _stopping || VideoHostClient.ShuttingDown) return;
        if (!_loadingFinishing && _loading.IsVisible)
        {
            // madVR / MPC-VR caduti in avvio (14/11: eccezione di AppKit nel driver Mac di Wine, ripetuta a ogni nuovo
            // tentativo finche' l'app non veniva riaperta): si riavvia il server di Wine e si riprova una volta.
            if (Engine != "mpv" && !_wineRecoveryTried && _file is { } retryFile)
            {
                _wineRecoveryTried = true;
                Console.Error.WriteLine($"[video] {_renderer} chiuso durante l'avvio: riavvio il server di Wine e riprovo");
                double retryAt = _resumeOnReady ?? _lastKnownPos;
                string? title = _titleOverride;
                int token = _loadToken;
                System.Threading.Tasks.Task.Run(() => { VideoHostClient.StopWine(); VideoHostClient.WarmUpWine(); })
                    .ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                    {
                        if (_closing || token != _loadToken || _file != retryFile) return;
                        StartVideo(retryFile, title);
                        if (retryAt > 1) _resumeOnReady = retryAt;
                    }));
                return;
            }
            FailVideoStartup();
            return;
        }
        _loading.Hide();
        SetLoadingRaised(false);
        Console.Error.WriteLine($"[video] il motore video si e' chiuso da solo ({_renderer}, posizione {_lastKnownPos:0.0} s): riavvio");
        if (_restarts++ >= 1) { _hud.InfoLine = "Video interrotto"; return; }
        double resume = _resumeOnReady ?? _lastKnownPos;
        StartVideo(_file, _titleOverride);
        if (resume > 1) _resumeOnReady = resume;
    }

    private bool _stopping;
    private bool _wineRecoveryTried;

    private void PostVideoEvent(Action action)
    {
        long session = _video.SessionId;
        Dispatcher.UIThread.Post(() => { if (!_closing && session == _video.SessionId) action(); });
    }

    private void ScheduleVideoAction(Action action, TimeSpan delay)
    {
        long session = _video.SessionId;
        DispatcherTimer.RunOnce(() => { if (!_closing && session == _video.SessionId) action(); }, delay);
    }

    /// <summary>La finestra sotto cui sta il video: sempre il player (il PiP del video non c'e' piu', 2026-11-22).</summary>
    internal Avalonia.Controls.TopLevel VideoOwner => this;

    private PixelRect VideoRect()
    {
        // Su macOS Avalonia da' la posizione in punti; Wine (modalita' Retina) lavora in pixel con una scala unica
        // per tutti gli schermi (2 se ce n'e' uno Retina). Con la scala dello schermo della finestra, su uno schermo
        // a 1x (proiettore, schermo virtuale) il video finiva a meta' delle coordinate, su un altro schermo.
        double s = WineScale();
        double width = ClientSize.Width;
        return new PixelRect((int)Math.Round(Position.X * s), (int)Math.Round(Position.Y * s),
            (int)Math.Round(width * s), (int)Math.Round(ClientSize.Height * s));
    }

    // La scala la chiede a macOS (NSScreen.backingScaleFactor): Screens.All di Avalonia la da' 1 per tutti gli schermi.
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr objc_getClass(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr sel_registerName(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr ObjcSend(IntPtr obj, IntPtr sel);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr ObjcSend(IntPtr obj, IntPtr sel, ulong index);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern double ObjcSendDouble(IntPtr obj, IntPtr sel);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern ulong ObjcSendCount(IntPtr obj, IntPtr sel);

    private double WineScale()
    {
        try
        {
            IntPtr screens = ObjcSend(objc_getClass("NSScreen"), sel_registerName("screens"));
            ulong count = ObjcSendCount(screens, sel_registerName("count"));
            double max = 0;
            for (ulong i = 0; i < count; i++)
                max = Math.Max(max, ObjcSendDouble(ObjcSend(screens, sel_registerName("objectAtIndex:"), i), sel_registerName("backingScaleFactor")));
            if (max >= 1) return max;
        }
        catch { }
        return RenderScaling;
    }

    private PixelRect _pendingVideoRect;
    private long _pendingVideoRectSince;
    private enum VideoStack { Unknown, Windowed, Fullscreen }
    private VideoStack _videoStack;
    private bool _videoNeedsOrder, _wasFullscreen;
    private long _lastStackRequest, _nextStackCheck;

    // Cambio di schermo intero con madVR o MPC-VR: la finestra video di Wine non segue l'animazione di macOS (scivola
    // via con lo Space vecchio e torna mezzo secondo dopo, ancora con la misura di prima). Per quel momento la si
    // rende invisibile e l'app mostra nero; torna quando ha la misura giusta e copre la finestra.
    private bool _fsTransition;
    private long _lastFullscreenToggle;
    private void BeginFullscreenTransition()
    {
        int pid = _video.ProcessId;
        long window = WineWindowOrder.WindowNumber(this);
        if (_fsTransition || Engine == "mpv" || pid <= 0 || !_videoReady || window <= 0 || !_hud.VideoVisible) return;
        _fsTransition = true;
        _hud.Blackout = true;
        ApplyWindowButtonsAlpha();
        _video.Send($"stack {window} transition");
        long start = Environment.TickCount64;
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) =>
        {
            long elapsed = Environment.TickCount64 - start;
            // L'animazione di macOS dura circa mezzo secondo; poi il video deve avere gia' ricevuto la misura finale.
            bool settled = elapsed >= 750 && _lastRect == VideoRect() && Environment.TickCount64 - _pendingVideoRectSince >= 150;
            if (!settled && elapsed < 1800 && _video.IsRunning) return;
            timer!.Stop();
            _fsTransition = false;
            _nextStackCheck = 0; _videoNeedsOrder = true;
            UpdateVideoStack();
            // Un attimo dopo: il primo fotogramma alla misura nuova deve essere gia' a schermo.
            DispatcherTimer.RunOnce(() => { _hud.Blackout = false; ApplyWindowButtonsAlpha(); }, TimeSpan.FromMilliseconds(120));
        });
        timer.Start();
    }

    private void SetVideoStack(long window, VideoStack mode, long now)
    {
        if (window <= 0 || _fsTransition) return;
        _videoStack = mode;
        _videoNeedsOrder = false;
        _lastStackRequest = now;
        _video.Send(mode == VideoStack.Fullscreen ? $"stack {window} fullscreen" : $"stack {window}");
    }

    private void UpdateVideoStack()
    {
        int pid = _video.ProcessId;
        if (Engine == "mpv" || pid <= 0 || !_videoReady) return;
        long now = Environment.TickCount64;
        if (now < _nextStackCheck) return;
        long window = WineWindowOrder.WindowNumber(this);
        // Tieni Wine visibile durante tutto il cambio di Space e abbassalo prima che AppKit lo riordini.
        var mode = IsFullscreen || MacWindowChrome.IsNativeFullscreen(this) ? VideoStack.Fullscreen : VideoStack.Windowed;
        _nextStackCheck = now + 250;
        if (mode != _videoStack || _videoNeedsOrder ||
            (IsActive && now - _lastStackRequest >= 250 && !WineWindowOrder.VideoCoversWindow(window, pid)))
            SetVideoStack(window, mode, now);
    }

    private void SyncVideoRect()
    {
        // Durante la costruzione del grafo il VideoHost non consuma comandi: evita di accodare i rettangoli
        // intermedi. Al ready basta mandare la geometria corrente.
        if (!_video.IsRunning || !_videoReady) return;
        var r = VideoRect();
        if (r.Width <= 0 || r.Height <= 0) return;
        if (r != _pendingVideoRect)
        {
            _pendingVideoRect = r;
            _pendingVideoRectSince = Environment.TickCount64;
        }
        if (r == _lastRect) return;
        // Evita di ricreare la swapchain di Wine per ogni misura intermedia.
        if (Engine != "mpv" && (r.Width != _lastRect.Width || r.Height != _lastRect.Height) &&
            Environment.TickCount64 - _pendingVideoRectSince < 80)
            return;
        _lastRect = r;
        _video.SetRect(r.X, r.Y, r.Width, r.Height);
        _videoNeedsOrder = Engine != "mpv" && IsActive;
        _nextStackCheck = 0;
    }

    private string? _titleOverride;

    private void StartVideo(string file, string? title = null, string? rendererOverride = null, bool replacingStream = false)
    {
        CancelWebResolve();
        _streamReplacing = false; _hud.Busy = false;
        if (!string.Equals(file, _file, StringComparison.Ordinal)) _remotePlaybackRenderer = null;
        if (InterceptForCinemaGate(file, title)) return;
        _pauseScreen.Hide();
        _hud.ResetPanels();
        _timelinePreview.Dismiss();
        _infoSnapshot = null;
        _splash.Dismiss();
        // Il motore si decide a ogni apertura: in risparmio energetico la scelta "madVR" puo' diventare MPC-VR o MPV.
        _renderer = rendererOverride ?? Environment.GetEnvironmentVariable("CINECORE_RENDERER") ??
            _remotePlaybackRenderer ?? MacSettings.EffectiveRenderer;
        WarnMadVrLowPower(file);
        if (_menu.IsOpen) _menu.Close();
        if (_settings.IsOpen) _settings.Close();
        ShowLibrary(false);
        // Il titolo di prima: si ricorda dove ci si e' fermati (non quando si riapre lo stesso, es. cambio di renderer).
        if (!replacingStream && _file != null && _file != file) SaveResumePoint();
        else JellyfinPlaybackStopped();
        _titleOverride = title;
        _stopping = true;
        _video.Dispose();
        _stopping = false;
        _videoStack = VideoStack.Unknown;
        if (file != _file) { _restarts = 0; MoveToFilmScreen(); }
        _file = file;
        SetStreamQualitySource(file);
        string identity = StreamIdentity(file);
        // Volume di questo film (PlayerForm.FilmVolume.cs): solo file sul Mac con video, se la voce del menu è attiva.
        _filmVolumeKey = null;
        if (MacSettings.RememberFilmVolume && !IsStream(file) && LibraryStore.IsVideoPath(file))
        {
            _filmVolumeKey = file;
            if (CinecorePlayer2025.Utilities.FilmVolumeStore.Get(file).Player is float remembered)
            {
                _volume = remembered; _hud.Volume = _volume;
                Console.Error.WriteLine($"[volume] ripreso {remembered * 100:0}% per {Path.GetFileName(file)}");
            }
        }
        _lastKnownPos = 0;
        _resumeOnReady = null;
        _pauseOnAudioReady = false;
        // Un flusso di rete non ha un nome di file: il titolo arriva dalla libreria.
        _hud.Title = title ?? _library.ItemFor(identity)?.Title ?? (MacDisc.TryResolve(file, out var titleDisc) ? titleDisc.Label : null) ?? (IsStream(file) ? CinecorePlayer2025.Utilities.JellyfinClient.RememberedFileName(file) ?? "Video di rete" : Path.GetFileNameWithoutExtension(file));
        _hud.Position = 0; _hud.InfoLine = ""; _hud.IsPlaying = false; _hud.VideoVisible = false;
        if (identity != _castFor) PrepareCast(identity);
        ResetIntroOutro(file);
        // Durata nota dall'inizio: dalla libreria o dal server (poi la conferma ffprobe e il VideoHost).
        _hud.Duration = _library.DurationFor(identity) ?? 0;
        // Ogni apertura ricomincia il caricamento: anche se si sceglie un secondo file durante la dissolvenza del primo.
        _loading.Show(_hud.Title, _library.BackdropFor(identity), _renderer == "madvr" ? "madVR" : _renderer == "mpcvr" ? "MPC Video Renderer" : "MPV");
        StopRemoteAudioSync();
        _loadToken++; _firstFrameSeen = false; _videoReady = false; _loadingFinishing = false;
        int loadingToken = _loadToken;
        DispatcherTimer.RunOnce(() =>
        {
            if (loadingToken != _loadToken || _closing || _loadingFinishing || !_loading.IsVisible) return;
            Console.Error.WriteLine($"[video] apertura scaduta ({_renderer}, pronto={_videoReady}, fotogramma={_firstFrameSeen})");
            FailVideoStartup();
        }, TimeSpan.FromSeconds(MacDisc.TryResolve(file, out _) ? 45 : _renderer is "madvr" or "mpcvr" && MacEngine.FirstStart ? 90
            // Con il cambio di frequenza l'avvio aspetta che lo schermo HDMI si stabilizzi (fino a ~8 s) prima di Wine.
            : _renderer is "madvr" or "mpcvr" && MacDisplayRate.Enabled ? 26 : IsStream(file) ? 18 : 15));
        SetLoadingRaised(true);
        _startClock.Restart();
        _runtime = new RuntimeInfo();
        _tracks = Array.Empty<(int, int, bool, string)>();
        // Un'altra immagine ISO o un file normale: le immagini montate prima si smontano.
        string? keepImage = file.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? file : null;
        System.Threading.Tasks.Task.Run(() => MacDisc.ReleaseMounts(keepImage));
        if (file != _probedFile) { _audioOffsetMs = SavedAudioDelay(file); _externalSubtitle = null; _cropAspect = 0; _stereo = StereoMode.None; }
        if (file != _probedFile)
        {
            // Analisi del file (ffprobe per macOS) in parallelo all'avvio del video.
            _probe = null; _probedFile = file;
            _probeTask = Task.Run(async () =>
            {
                // La sonda legge il file originale: chiedere PlaybackUrl qui creava un'altra transcodifica sul server.
                string probeTarget = MacDisc.TryResolve(file, out var disc) ? MacDisc.ProbePath(disc) : IsStream(file) ? CinecorePlayer2025.Utilities.JellyfinClient.WithAccess(file) : file;
                var probe = await MediaProbe.RunAsync(probeTarget);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_probedFile != file) return;
                    _probe = probe; RefreshQualityLabel(); _hud.InfoChanged();
                    MatchDisplayRate();
                    if (probe?.Duration > 0 && (_hud.Duration <= 0 || !_hud.VideoVisible)) _hud.Duration = probe.Duration;
                });
                return probe;
            });
        }
        RefreshQualityLabel();
        _hud.Poke();
        _ = StartVideoProcessAsync(file, _renderer, _loadToken);
    }

    private async Task StartVideoProcessAsync(string file, string renderer, int token)
    {
        try
        {
            await Task.Yield(); // le richieste di ripresa vengono assegnate dal chiamante dopo StartVideo
            await _video.Stopped;
            if (_closing || token != _loadToken) return;
            // Cambio frequenza PRIMA di avviare il motore: il VideoHost (madVR, MPC-VR) si sincronizza sulla frequenza
            // misurata all'avvio e con un cambio a film avviato madVR si fermava sul primo fotogramma.
            await MatchDisplayRateBeforeStart(file);
            if (_closing || token != _loadToken) return;
            VideoHostClient.DisplayHz = MacDisplayRate.CurrentHz(MacDisplayRate.DisplayAt(Position.X + ClientSize.Width / 2, Position.Y + ClientSize.Height / 2));
            _initialPosition = _resumeOnReady ?? 0;
            _resumeOnReady = null;
            _lastKnownPos = _initialPosition;
            _hud.Position = _initialPosition;
            UpdateWindowButtons();
            var preset = await ChoosePresetAsync(file, renderer);
            if (_closing || token != _loadToken) return;
            ApplyPreset(preset);
            var r = _lastRect = VideoRect();
            _video.ExternalAudio = WebAudioFor(file) ?? ExternalTrackFor(file);
            await _video.StartAsync(file, renderer, r.X, r.Y, r.Width, r.Height, _initialPosition);
            if (token != _loadToken || _closing) return;
            _dvdMenu.Attach(_video.ActiveDvdNav, () => _video.DvdEnginePosition);
        }
        catch (Exception ex)
        {
            if (token != _loadToken || _closing) return;
            Console.Error.WriteLine("[video] avvio non riuscito: " + ex.GetType().Name);
            FailVideoStartup();
        }
        ApplyVolume();
    }

    // Cambio frequenza (MacDisplayRate): una volta per film, quando si sanno i fotogrammi al secondo e il video e' pronto.
    private string? _rateFile;
    private Task<ProbeData?>? _probeTask;

    private async Task MatchDisplayRateBeforeStart(string file)
    {
        if (!MacDisplayRate.Enabled || _rateFile == file || _probeTask is not { } probeTask || _probedFile != file) return;
        if (await Task.WhenAny(probeTask, Task.Delay(2000)) != probeTask || probeTask.Result is not { Fps: > 0 } probe) return;
        _rateFile = file;
        uint display = MacDisplayRate.DisplayAt(Position.X + ClientSize.Width / 2, Position.Y + ClientSize.Height / 2);
        string? result = await Task.Run(() => MacDisplayRate.Match(display, probe.Fps));
        if (result != null) Console.Error.WriteLine("[frequenza] " + result + " (prima dell'avvio)");
        // Cambio fatto: il film parte solo quando schermo e uscita audio HDMI sono tornati stabili.
        if (result != null && MacDisplayRate.Settling)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string audioUid = MacSettings.AudioDeviceUid;
            bool ok = await Task.Run(() => MacDisplayRate.WaitSettled(display, MacDisplayRate.LastTargetHz, audioUid));
            Console.Error.WriteLine($"[frequenza] schermo {(ok ? "stabile" : "non ancora stabile")} dopo {clock.ElapsedMilliseconds} ms");
        }
    }

    private void MatchDisplayRate()
    {
        if (_file is not { } file || !_videoReady || _probe is not { Fps: > 0 } probe || _rateFile == file || !MacDisplayRate.Enabled) return;
        // A film avviato solo con MPV: madVR e MPC-VR sotto Wine non seguono un cambio di frequenza in corsa.
        if (Engine != "mpv") return;
        _rateFile = file;
        uint display = MacDisplayRate.DisplayAt(Position.X + ClientSize.Width / 2, Position.Y + ClientSize.Height / 2);
        double fps = probe.Fps;
        System.Threading.Tasks.Task.Run(() =>
        {
            string? result = MacDisplayRate.Match(display, fps);
            if (result != null) Console.Error.WriteLine("[frequenza] " + result);
        });
    }

    private void RestoreDisplayRate()
    {
        _rateFile = null;
        System.Threading.Tasks.Task.Run(() => { if (MacDisplayRate.Restore() is { } r) Console.Error.WriteLine("[frequenza] " + r); });
    }

    private void FailVideoStartup()
    {
        CloseFile();
        _osd.Show("Non riesco ad aprire questo titolo", 6500);
    }

    // madVR in risparmio energetico: avviso a schermo con la scelta di passare a MPV o MPC-VR (mai un cambio da solo).
    // Uno per sessione; "Resta su madVR" non lo ripropone.
    private bool _lowPowerWarned;
    private void WarnMadVrLowPower(string file)
    {
        _hud.HideRendererPrompt();
        _stutterPrompt = false;
        if (_lowPowerWarned || _renderer != "madvr" || !MacPower.LowPowerMode || MacSettings.MadVrLowPower != "ask") return;
        int token = _loadToken + 1; // StartVideo lo incrementa subito dopo
        DispatcherTimer.RunOnce(() =>
        {
            if (_lowPowerWarned || _file != file || _renderer != "madvr" || !MacPower.LowPowerMode || _library.IsVisible || token != _loadToken) return;
            // Un preset scelto da un'automazione o a mano decide gia' come far lavorare madVR: niente avviso.
            if (ComponentSettings.ActivePreset != null) return;
            _lowPowerWarned = true;
            var choices = new System.Collections.Generic.List<(string, string)>();
            if (MpvEngine.Available) choices.Add(("mpv", "Passa a MPV"));
            choices.Add(("mpcvr", "Passa a MPC-VR"));
            choices.Add(("madvr", "Resta su madVR"));
            choices.Add(("never", "Non mostrare più"));
            _hud.ShowRendererPrompt(choices.ToArray());
            HideLowPowerPromptLater(TimeSpan.FromSeconds(25));
        }, TimeSpan.FromSeconds(4));
    }

    // Sparisce da solo dopo 25 s, ma non mentre e' in sospeso sotto il menu: dopo la chiusura resta il tempo per sceglierlo.
    private void HideLowPowerPromptLater(TimeSpan after) => DispatcherTimer.RunOnce(() =>
    {
        if (!_hud.RendererPromptVisible) return;
        if (_hud.PromptsHeld) HideLowPowerPromptLater(TimeSpan.FromSeconds(8));
        else _hud.HideRendererPrompt();
    }, after);

    // madVR che perde fotogrammi di continuo (scheda video troppo debole per lui, es. una Radeon R5 220 con un 4K):
    // avviso a schermo con la scelta di un motore piu' leggero. Mai un cambio da solo; una volta per sessione e
    // "Resta su madVR" non lo ripropone piu'. Le statistiche arrivano una volta al secondo dal VideoHost.
    private readonly System.Collections.Generic.Queue<(long Tick, int Dropped)> _dropSamples = new();
    private int _dropToken = -1;
    private int _qaDropsPerSec, _qaDropsSim; // prova (QA "drops-sim:N"): N fotogrammi persi finti al secondo
    private bool _stutterWarned, _stutterPrompt;
    private const int StutterDrops = 30;        // fotogrammi persi...
    private const long StutterWindowMs = 10_000; // ...in 10 s di riproduzione continua

    private void WatchMadVrStutter(int dropped)
    {
        if (_stutterWarned || _renderer != "madvr" || !_hud.IsPlaying || !_videoReady || _library.IsVisible || _hud.RendererPromptVisible
            || _dropToken != _loadToken || MacSettings.Get("madvr-stutter-ask", "1") != "1")
        {
            _dropSamples.Clear(); _dropToken = _loadToken; return;
        }
        long now = Environment.TickCount64;
        _dropSamples.Enqueue((now, dropped));
        while (now - _dropSamples.Peek().Tick > StutterWindowMs) _dropSamples.Dequeue();
        var first = _dropSamples.Peek();
        if (now - first.Tick < StutterWindowMs - 2000 || dropped - first.Dropped < StutterDrops) return;
        _stutterWarned = _stutterPrompt = true;
        _dropSamples.Clear();
        Console.Error.WriteLine($"[motore] madVR perde fotogrammi: {dropped - first.Dropped} in {(now - first.Tick) / 1000.0:0.#} s");
        var choices = new System.Collections.Generic.List<(string, string)>();
        if (MpvEngine.Available) choices.Add(("mpv", "Passa a MPV"));
        choices.Add(("mpcvr", "Passa a MPC-VR"));
        choices.Add(("madvr", "Resta su madVR"));
        _hud.ShowChoicePrompt("madVR va a scatti",
            "La scheda video non sta al passo con madVR e perde fotogrammi. Vuoi passare a un motore più leggero? La scelta resta nelle impostazioni.",
            choices.ToArray());
        DispatcherTimer.RunOnce(() => { if (_stutterPrompt && _hud.RendererPromptVisible) { _stutterPrompt = false; _hud.HideRendererPrompt(); } }, TimeSpan.FromSeconds(25));
    }

    private void OnRendererPromptChosen(string renderer)
    {
        if (renderer.StartsWith("disc-", StringComparison.Ordinal)) { OnDiscResumeChosen(renderer); return; }
        if (_stutterPrompt)
        {
            _stutterPrompt = false;
            if (renderer == "madvr") { MacSettings.Set("madvr-stutter-ask", false); return; }
            MacSettings.ChooseRenderer(renderer == "mpv" ? "MPV" : "MPCVR");
            SetRenderer(MacSettings.EffectiveRenderer);
            return;
        }
        if (renderer == "never")
        {
            // Resta su madVR e l'avviso non compare piu' (Impostazioni › Generali › madVR in risparmio energetico: "Chiedi" lo riattiva).
            MacSettings.MadVrLowPower = "asis";
            _osd.Show("Avviso disattivato: si riattiva da Impostazioni › Generali", 2600);
            return;
        }
        if (renderer == "madvr" || _renderer != "madvr") return;
        // Vale finche' dura il risparmio energetico: la preferenza salvata resta madVR.
        _remotePlaybackRenderer = null;
        MacSettings.LowPowerSessionRenderer = renderer;
        SetRenderer(MacSettings.EffectiveRenderer);
    }

    public void OpenFile(string path) => OpenFiles(new[] { path });
    /// <summary>Un file dal Finder e' arrivato prima che partisse lo splash: lo splash non parte.</summary>
    private bool _openedFromOutside;

    private Control[] _barAwareLayers = Array.Empty<Control>();

    /// <summary>
    /// Con la musica in corso Spotlight, Impostazioni e gli altri livelli a pagina intera finiscono sopra la barra della
    /// musica: la barra (disegnata dalla libreria, sotto di loro) resta visibile e cliccabile, anche sul vetro vero.
    /// </summary>
    private void KeepMusicBarClear()
    {
        double reserve = _library.IsVisible ? _library.MusicBarReserve : 0;
        var margin = new Thickness(0, 0, 0, reserve);
        bool covered = false;
        foreach (var layer in _barAwareLayers)
        {
            var layerMargin = layer == _manualMix && ClientSize.Height - reserve < 480 ? default : margin;
            if (layer.Margin != layerMargin) layer.Margin = layerMargin;
            covered |= layer.IsVisible && layer is not Canvas;
        }
        covered |= _settings.IsOpen;
        _library.OverlayAboveBar = covered && reserve > 0;
    }

    /// <summary>CD audio: si legge il disco (e MusicBrainz) in sottofondo, poi le tracce suonano in Musica.</summary>
    private void OpenAudioCd(string root)
    {
        _osd.Show("CD audio: leggo il disco…", 2400);
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            MacAudioCd.Disc? disc = null;
            try { disc = MacAudioCd.Read(root, System.Threading.CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine("[CD] " + ex.Message); }
            Dispatcher.UIThread.Post(() =>
            {
                if (disc == null || disc.Tracks.Count == 0) { _osd.Show("Non riesco a leggere il CD", 3000); return; }
                if (_file != null) CloseFile();
                ShowLibrary(true);
                _library.PlayAudioCd(disc);
            });
        });
    }

    /// <summary>
    /// File dal Finder ("Apri con", doppio clic, trascinati sull'icona o sulla finestra), come ExternalOpen di Windows:
    /// la musica suona in Musica, le foto si aprono in Foto, i video (e i dischi) partono nel player e gli altri scelti
    /// insieme vanno in coda. Funziona anche con l'app gia' aperta.
    /// </summary>
    public void OpenFiles(System.Collections.Generic.IReadOnlyList<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;
        _spotlightReturnPath = null; // un file aperto da fuori non deve riportare a Spotlight chiudendo il film
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        string first = list[0];
        // Aperto dal Finder ad app appena avviata: niente splash (serve solo a coprire il caricamento della libreria),
        // si va dritti al contenuto. Le playlist .wpl aspettano la libreria (piu' sotto).
        if (!WplPlaylist.IsPlaylistFile(first)) { _openedFromOutside = true; if (_splash.IsVisible) _splash.Dismiss(); }
        if (MacAudioCd.IsAudioCd(first)) { OpenAudioCd(first); return; }
        // Playlist di Windows Media Player (.wpl): diventa una playlist della libreria e parte.
        if (WplPlaylist.IsPlaylistFile(first))
        {
            if (_splash.IsVisible) { _pendingPlaylist = first; return; }
            if (_file != null) CloseFile();
            ShowLibrary(true);
            _library.ImportPlaylistFile(first);
            return;
        }
        string ext = Path.GetExtension(first).ToLowerInvariant();
        if (CinecorePlayer2025.Utilities.MediaFileKinds.IsAudioExtension(ext))
        {
            if (_file != null) CloseFile();
            ShowLibrary(true);
            _library.PlayExternalMusic(list.Where(p => CinecorePlayer2025.Utilities.MediaFileKinds.IsAudioExtension(Path.GetExtension(p).ToLowerInvariant())).ToList());
            return;
        }
        if (LibraryStore.IsPhotoPath(first))
        {
            if (_beforeMini != null) ToggleMiniPlayer();
            if (_settings.IsOpen) _settings.Close();
            if (_spotlight.IsOpen) _spotlight.Close();
            if (_metadata.IsOpen) _metadata.Close();
            if (_report.IsVisible) _report.Close();
            if (_hdr.IsVisible) _hdr.Close();
            if (_trakt.IsOpen) _trakt.Close();
            if (_youtube.IsOpen) _youtube.Close();
            if (_goTo.IsOpen) _goTo.Close();
            if (_photoViewer.IsVisible) _photoViewer.Close();
            if (_menu.IsOpen) _menu.Close();
            if (_file != null) CloseFile();
            ShowLibrary(true);
            _library.OpenExternalPhotos(list.Where(LibraryStore.IsPhotoPath).ToList());
            return;
        }
        StartVideo(first);
        foreach (var more in list.Skip(1).Where(p => LibraryStore.IsVideoPath(p) || MacDisc.TryResolve(p, out _)))
            _videoQueue.Add(_library.ItemFor(more) ?? new LibraryItem { Path = more, Title = Path.GetFileNameWithoutExtension(more) });
    }

    /// <summary>"Chiudi file": il video si ferma e torna alla libreria, come su Windows.</summary>
    private void CloseFile() => CloseFile(skipDemo: true);

    private void CloseFile(bool skipDemo)
    {
        _streamReplacing = false; _hud.Busy = false;
        CancelWebResolve();
        _pauseScreen.Hide();
        // Chiudere la demo (Esc, S) la salta: parte il film.
        if (skipDemo && _afterDemo != null && StartFilmAfterDemo()) return;
        _afterDemo = null;
        _remotePlaybackRenderer = null;
        _hud.HideRendererPrompt();
        StopRemoteAudioSync();
        _loading.Hide();
        _loadToken++;
        SetLoadingRaised(false);
        if (_hud.InfoOpen) _hud.CloseInfo();
        if (_hud.CastOpen) _hud.CloseCast();
        _castFor = null;
        _hud.CastAvailable = false; _hud.QualityAvailable = false;
        SaveResumePoint();
        _dlnaItem = null;
        ScheduleTraktDiarySync();
        _dvdMenu.Attach(null, () => 0);
        _stopping = true;
        _video.Dispose();
        _stopping = false;
        _file = null; _probedFile = null; _probe = null;
        RestoreDisplayRate();
        _hud.Title = ""; _hud.InfoLine = ""; _hud.Position = 0; _hud.Duration = 0;
        _hud.IsPlaying = false; _hud.VideoVisible = false;
        StopImageSizing();
        _hud.Poke();
        _hud.InvalidateVisual();
        if (!ReturnToSpotlight()) ShowLibrary(true);
        RestoreFromFilmScreen();
        ScheduleStandby(2500);
    }

    private int _standbyToken;
    /// <summary>
    /// Processo video pronto per il prossimo film (madVR / MPC-VR), a libreria aperta: dall'apertura al primo fotogramma
    /// risparmia l'avvio di Wine e .NET, Direct3D e il caricamento dei filtri.
    /// </summary>
    private void ScheduleStandby(int delayMs)
    {
        if (!VideoHostClient.StandbyEnabled) return;
        int token = ++_standbyToken;
        DispatcherTimer.RunOnce(async () =>
        {
            if (token != _standbyToken || _closing || _file != null || Engine == "mpv") return;
            await _video.Stopped;
            if (token != _standbyToken || _closing || _file != null || Engine == "mpv") return;
            double hz = MacDisplayRate.CurrentHz(MacDisplayRate.DisplayAt(Position.X + ClientSize.Width / 2, Position.Y + ClientSize.Height / 2));
            string renderer = _renderer;
            _ = Task.Run(() => VideoHostClient.PrepareStandby(renderer, hz));
        }, TimeSpan.FromMilliseconds(delayMs));
    }

    /// <summary>SetRenderer: il grafo si ricostruisce con l'altro renderer e riparte dallo stesso punto.</summary>
    /// <summary>Motore che gira davvero: con i menu dei DVD MPV sostituisce madVR / MPC-VR finche' non parte il film.</summary>
    private string Engine => _video.DvdMenuOnMpv ? "mpv" : _renderer;

    private void SetRenderer(string renderer, bool restart = false)
    {
        bool alreadyRunning = !restart && renderer == _renderer && _video.IsRunning;
        _renderer = renderer;
        if (alreadyRunning) return;
        if (renderer != "mpv") System.Threading.Tasks.Task.Run(VideoHostClient.WarmUpWine);
        if (_file == null) { ScheduleStandby(800); return; }
        double position = _hud.Position;
        bool wasInfo = _hud.InfoOpen;
        // Le tracce scelte (audio, sottotitoli o "disattivati") valgono anche col motore nuovo.
        var subs = _tracks.Where(t => t.Group == 2).ToList();
        string? keepSub = _externalSubtitle != null || subs.Count == 0 ? null
            : subs.FirstOrDefault(t => t.Selected && !IsSubtitleOffOption(t.Name)).Name ?? "";
        string? keepAudio = _tracks.Where(t => t.Group == 1 && t.Selected).Select(t => t.Name).FirstOrDefault();
        StartVideo(_file, _titleOverride, rendererOverride: renderer);
        _keepSubtitle = keepSub; _keepAudio = keepAudio;
        _resumeOnReady = position > 1 ? position : null;
        if (wasInfo) _hud.OpenInfo();
    }

    // Cambio di motore: le tracce si ritrovano per nome (gli indici cambiano da un motore all'altro), poi per lingua.
    private string? _keepSubtitle, _keepAudio;
    private void RestoreTrackChoice()
    {
        if ((_keepSubtitle == null && _keepAudio == null) || _tracks.Count == 0) return;
        string? sub = _keepSubtitle, audio = _keepAudio;
        _keepSubtitle = _keepAudio = null;
        (int Index, int Group, bool Selected, string Name)? Find(int group, string name)
        {
            var list = _tracks.Where(t => t.Group == group && !(group == 2 && IsSubtitleOffOption(t.Name))).ToList();
            foreach (var t in list) if (t.Name == name) return t;
            string? lang = LanguageKey(name);
            if (lang != null) foreach (var t in list) if (LanguageKey(t.Name) == lang) return t;
            return null;
        }
        if (audio != null && _video.ExternalAudio == null && Find(1, audio) is { Selected: false } a) _video.Send("select " + a.Index);
        if (sub == null) return;
        if (sub.Length == 0) { if (_tracks.Any(t => t.Group == 2 && t.Selected && !IsSubtitleOffOption(t.Name))) _video.Send("subsoff"); }
        else if (Find(2, sub) is { Selected: false } st) _video.Send($"select {st.Index} sub");
    }

    /// <summary>Uscita audio (BuildAudioOutputMenu di Windows): in cima la predefinita del Mac, poi i dispositivi.</summary>
    private MenuEntry AudioOutputMenu()
    {
        var root = new MenuEntry("Uscita audio", "amplifier") { Children = new() };
        var devices = MacAudioDevices.Outputs();
        string current = MacSettings.AudioDeviceUid;
        string? systemName = devices.FirstOrDefault(d => d.IsDefault)?.Name;
        root.Children.Add(new MenuEntry("Usa dispositivo predefinito", "amplifier",
            () => SetAudioOutput("", ""))
        {
            Checked = current.Length == 0 || devices.All(d => d.Uid != current),
            Value = systemName
        });
        if (devices.Count > 0) root.Children.Add(MenuEntry.Sep());
        foreach (var device in devices)
        {
            var chosen = device;
            root.Children.Add(new MenuEntry(device.Name, null, () => SetAudioOutput(chosen.Uid, chosen.Name)) { Checked = current == device.Uid });
        }
        return root;
    }

    /// <summary>Riapre il file in corso nello stesso punto (ReopenSame di Windows: cambio di uscita audio).</summary>
    private void ReopenSame(bool preservePause = false)
    {
        if (_file == null || !_video.IsRunning) return;
        double position = _hud.Position;
        bool paused = preservePause && !_hud.IsPlaying;
        StartVideo(_file, _titleOverride);
        _pauseOnAudioReady = paused;
        _resumeOnReady = position > 1 ? position : null;
    }

    private static string Fmt(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    // ------------------------------------------------------------------ sottotitoli (PopulateSubtitleTracksMenu)

    private static bool IsSubtitleOffOption(string? name) =>
        !string.IsNullOrWhiteSpace(name) && System.Text.RegularExpressions.Regex.IsMatch(name.Trim(),
            @"^(?:S:\s*)?(?:\[|\()?\s*(?:no subtitles?|subtitles? off|off|none|disabled|nessun[oi] sottotitol[oi]|sottotitoli disattivati|disattivati)\s*(?:\]|\))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsForcedSubtitle(string name) =>
        name.Contains("[forced]", StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(name, @"\bforced\b|\bforzat[oi]\b|\bforz\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Codice della lingua fra parentesi quadre nel nome di LAV ("[ita]").</summary>
    private static string? LanguageKey(string name) =>
        System.Text.RegularExpressions.Regex.Match(name, @"\[([a-z]{2,3})\]") is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    private MenuEntry BuildSubtitlesMenu(bool isVideo)
    {
        var root = new MenuEntry("Sottotitoli") { Children = new(), Enabled = isVideo };
        var streams = _tracks.Where(t => t.Group == 2).ToList();
        bool hasSelected = streams.Any(s => s.Selected);
        bool selectedForced = streams.Any(s => s.Selected && IsForcedSubtitle(s.Name));
        root.Children.Add(new MenuEntry("Disattivati", null, () => { _externalSubtitle = null; _video.Send("subsoff"); _hud.Poke(); })
        {
            Checked = _externalSubtitle == null && (!hasSelected || streams.Any(s => s.Selected && IsSubtitleOffOption(s.Name))),
        });
        if (_externalSubtitle != null)
            root.Children.Add(new MenuEntry(Path.GetFileName(_externalSubtitle), "subtitles") { Checked = true, Enabled = false });
        var forced = streams.Where(s => IsForcedSubtitle(s.Name)).ToList();
        if (forced.Count > 0)
        {
            root.Children.Add(new MenuEntry("Automatici (solo forzati)", null, () =>
            {
                // I forzati nella lingua dell'audio scelto, altrimenti i primi forzati.
                string? audioLang = _tracks.Where(t => t.Group == 1 && t.Selected).Select(t => LanguageKey(t.Name)).FirstOrDefault();
                var pick = forced.FirstOrDefault(s => audioLang != null && LanguageKey(s.Name) == audioLang);
                if (pick.Name == null) pick = forced[0];
                _externalSubtitle = null;
                _video.Send($"select {pick.Index} sub");
                _hud.Poke();
            }) { Checked = _externalSubtitle == null && selectedForced });
        }
        root.Children.Add(MenuEntry.Sep());
        int ordinal = 0;
        foreach (var stream in streams.Where(s => !IsSubtitleOffOption(s.Name)).OrderByDescending(s => s.Selected).ThenBy(s => s.Index))
        {
            ordinal++;
            string raw = System.Text.RegularExpressions.Regex.Replace(stream.Name, @"^S:\s*", "");
            string label = CinecorePlayer2025.Utilities.SubtitleNameNormalizer.NormalizeSubtitleTrackName(raw, ordinal).Trim();
            if (string.IsNullOrWhiteSpace(label)) label = $"Traccia {ordinal}";
            if (IsForcedSubtitle(stream.Name) && !label.Contains("forced", StringComparison.OrdinalIgnoreCase) && !label.Contains("forzat", StringComparison.OrdinalIgnoreCase))
                label += " · Forzati";
            int index = stream.Index;
            root.Children.Add(new MenuEntry(label, null, () => { _externalSubtitle = null; _video.Send($"select {index} sub"); _hud.Poke(); }) { Checked = _externalSubtitle == null && stream.Selected });
        }
        return root;
    }

    // ------------------------------------------------------------------ menu del tasto destro (PlayerForm.Menu.cs)

    private void OpenPlaybackContextMenu(object? sender, PointerPressedEventArgs e)
    {
        var pressed = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        // Diagnostica: pressione con il tasto destro gia' "premuto" per Avalonia (un rilascio perso).
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && pressed != PointerUpdateKind.RightButtonPressed)
            Console.Error.WriteLine($"[menu] pressione con il tasto destro gia' premuto per Avalonia: {pressed}");
        if (pressed == PointerUpdateKind.RightButtonPressed ||
            (pressed == PointerUpdateKind.LeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            _lastSecondaryEventAt = Environment.TickCount64;
            _unhandledSecondaryAt = null;
        }
        else return;
        // In libreria (e nelle sue pagine) il menu lo apre la libreria stessa: qui non c'e' niente da segnalare.
        if (!CanOpenPlaybackMenu) { if (!_library.IsVisible) LogMenuBlocked("clic"); return; }
        Point at = e.GetPosition(_menu);
        if (!new Rect(_menu.Bounds.Size).Contains(at)) { Console.Error.WriteLine($"[menu] clic destro fuori dalla vista {at} {_menu.Bounds}"); return; }
        e.Handled = true;
        ShowContextMenu(at);
    }

    /// <summary>
    /// Mix manuale (ShowManualMixSheet di Windows): analisi e forme d'onda prima di aprire, poi la scheda; la scelta va in
    /// manual-mix.json e il motore della musica la usa al prossimo passaggio fra quei due brani.
    /// </summary>
    private bool _manualMixOpening;
    private void WireManualMix()
    {
        _manualMix.Closed += () => { _library.Covered = false; if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _manualMix.Saved += (from, to, mix) =>
        {
            if (mix == null) CinecorePlayer2025.Audio.ManualMixStore.Remove(from, to);
            else CinecorePlayer2025.Audio.ManualMixStore.Set(from, to, mix);
            _library.ManualMixChanged(from, to);
            _osd.Show(mix == null ? "Passaggio: automix" : "Passaggio salvato", 2200);
        };
        _library.ManualMixRequested += async (from, to) =>
        {
            if (_manualMixOpening) return;
            _manualMixOpening = true;
            try
            {
                var preparing = ManualMixView.PrepareAsync(from.Path, to.Path);
                if (!preparing.IsCompleted) _osd.Show("Preparo il passaggio…", 1500);
                var prepared = await preparing;
                _manualMix.Open(from.Path, to.Path, from.Title, to.Title, from.ArtistName ?? from.AlbumArtist ?? "", to.ArtistName ?? to.AlbumArtist ?? "",
                    from.ArtPath, to.ArtPath, () => _library.MusicPlayerVolume, _library.PauseMusic, prepared);
                _library.Covered = _manualMix.IsVisible;
            }
            catch (Exception ex) { Console.Error.WriteLine("[mix] apertura: " + ex.Message); _osd.Show("Non riesco a preparare il passaggio", 2600); }
            finally { _manualMixOpening = false; }
        };
    }

    /// <summary>Lista di testo (come i menu della libreria) per i livelli a pagina intera; poi il fuoco torna al livello.</summary>
    private void ShowTextMenu(Control owner, System.Collections.Generic.IReadOnlyList<MenuEntry> entries, Point at, bool alignRight = false)
    {
        if (_menu.IsOpen || _textMenu.IsOpen) return;
        var origin = owner.TranslatePoint(default, _textMenu) ?? default;
        void Refocus() { _textMenu.Closed -= Refocus; if (owner.IsVisible) owner.Focus(); }
        _textMenu.Closed += Refocus;
        _textMenu.Show(entries, at + origin, alignRight);
    }

    // Anche sopra Spotlight (che copre la libreria e lascia l'HUD nascosto): prima il clic destro li' non apriva niente.
    private bool CanOpenPlaybackMenu => !_menu.IsOpen && (_hud.IsVisible || _spotlight.IsOpen) && !_loading.IsVisible && !_pairing.IsVisible;

    /// <summary>
    /// macOS ha visto il clic destro sulla nostra finestra ma Avalonia no (succede col trackpad dopo Spotlight o lo
    /// schermo intero): il menu si apre lo stesso, dove sta il puntatore, come avrebbe fatto il clic vero.
    /// </summary>
    private void RecoverSecondaryClick()
    {
        if (_mouse.PointerInWindow() is not { } at) return;
        if (_trakt.IsVisible) { if (_trakt.TranslatePoint(default, this) is { } o) _trakt.OpenContextMenuAt(at - o); }
        else if (_library.IsVisible && !_spotlight.IsOpen && !_settings.IsOpen && !_metadata.IsOpen && !_photoViewer.IsOpen)
        { if (_library.TranslatePoint(default, this) is { } o) _library.OpenContextMenuAt(at - o); }
        else if (!_library.IsVisible && CanOpenPlaybackMenu) ShowContextMenu(at);
        else return;
        Console.Error.WriteLine("[menu] menu aperto dal clic recuperato");
    }

    /// <summary>Diagnostica: il menu del tasto destro non si e' aperto (segnalazione dell'utente, 18/11).</summary>
    private void LogMenuBlocked(string via) =>
        Console.Error.WriteLine($"[menu] clic destro ({via}) senza menu: aperto={_menu.IsOpen} hud={_hud.IsVisible} caricamento={_loading.IsVisible} abbinamento={_pairing.IsVisible} libreria={_library.IsVisible} attiva={IsActive} intero={IsFullscreen}");
    private uint _secondaryPresses = MacGlass.SecondaryPressCount();
    private long _lastSecondaryEventAt, _systemSecondaryAt;
    private Point? _unhandledSecondaryAt;

    private void PollFullscreenContextClick()
    {
        // WindowServer può far passare il clic attraverso un HUD quasi trasparente.
        // Il contatore rileva anche i clic già rilasciati fra due giri del timer.
        uint count = MacGlass.SecondaryPressCount();
        bool changed = count != _secondaryPresses;
        _secondaryPresses = count;
        // Diagnostica (trackpad?): il sistema ha visto un clic destro sulla finestra ma ad Avalonia non e' arrivato.
        long tick = Environment.TickCount64;
        if (changed && IsActive && _mouse.PointerInWindow() != null) _systemSecondaryAt = tick;
        if (_systemSecondaryAt > 0 && tick - _systemSecondaryAt > 400)
        {
            if (_lastSecondaryEventAt < _systemSecondaryAt - 150 && !_menu.IsOpen && !_textMenu.IsOpen)
            {
                var top = MacGlass.WindowOwnerAtMouse();
                Console.Error.WriteLine($"[menu] clic destro del sistema non arrivato all'app (sotto il puntatore: {top?.Owner ?? "?"}) libreria={_library.IsVisible} intero={IsFullscreen}");
                if (top?.Pid == Environment.ProcessId) RecoverSecondaryClick();
            }
            _systemSecondaryAt = 0;
        }
        // Anche in finestra: il clic destro sul video finiva alla finestra del video (mpv, Wine/VideoHost) e il menu
        // si apriva solo dopo vari tentativi. In finestra vale solo se sotto il puntatore c'e' davvero il player.
        if (!IsActive || !CanOpenPlaybackMenu || (!IsFullscreen && !(_video.IsRunning && _file != null)))
        {
            if (changed && Environment.TickCount64 - _lastSecondaryEventAt > 100 && !_menu.IsOpen && !_library.IsVisible && _mouse.PointerInWindow() != null)
                LogMenuBlocked("riserva");
            _unhandledSecondaryAt = null;
            return;
        }
        if (changed && Environment.TickCount64 - _lastSecondaryEventAt > 100)
        {
            _unhandledSecondaryAt = _mouse.PointerInWindow();
            if (!IsFullscreen && _unhandledSecondaryAt != null && !PointerOverPlayerWindow()) _unhandledSecondaryAt = null;
        }
        if (_unhandledSecondaryAt is not { } missed || MacGlass.SecondaryButtonDown()) return;
        _unhandledSecondaryAt = null;
        if (new Rect(_menu.Bounds.Size).Contains(missed)) ShowContextMenu(missed);
    }

    /// <summary>Tasti multimediali e "In riproduzione" di macOS (MacMediaKeys): film prima, poi la musica.</summary>
    private (string? Title, bool Playing, double Pos) _nowPlayingShown;
    private void WireMediaKeys()
    {
        MacMediaKeys.Start();
        MacNetwork.Start();
        MacMediaKeys.Command += command =>
        {
            Console.Error.WriteLine("[tasti] multimediale: " + command);
            if (_file != null && _video.IsRunning)
            {
                switch (command)
                {
                    case "toggle": TogglePlay(); break;
                    case "play": if (!_hud.IsPlaying) TogglePlay(); break;
                    case "pause": if (_hud.IsPlaying) TogglePlay(); break;
                    case "stop": CloseFile(); break;
                    // Come Windows (HandleMediaBackForward): la coda dei video se c'e', altrimenti 10 s.
                    case "next":
                        if (_videoQueue.Count > 0) { var next = _videoQueue[0]; _videoQueue.RemoveAt(0); SaveResumePoint(); StartVideo(next.Path, next.Title); }
                        else SeekTo(_hud.Position + 10);
                        break;
                    case "previous": SeekTo(_hud.Position - 10); break;
                    default:
                        if (command.StartsWith("seek:", StringComparison.Ordinal) && double.TryParse(command[5..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double at)) SeekTo(at);
                        break;
                }
                _hud.Poke();
                return;
            }
            _library.MediaKeyCommand(command);
        };
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            string? title = null, artist = null, art = null; double pos = 0, dur = 0; bool playing = false;
            if (_file != null && _video.IsRunning) { title = _hud.Title; pos = _lastKnownPos; dur = _hud.Duration; playing = _hud.IsPlaying; art = _library.ItemFor(_file)?.ArtPath; }
            else if (_library.RemoteMusicState is { } music) { title = music.Item.Title; artist = music.Item.ArtistName ?? music.Item.AlbumArtist; pos = music.Position; dur = music.Duration; playing = !music.Paused; art = music.Item.ArtPath; }
            // Immagini dei server: il file gia' in cache (niente download da qui).
            if (art != null && art.StartsWith("http", StringComparison.OrdinalIgnoreCase)) art = NetworkStore.CachedArtFileIfPresent(art);
            // Solo quando cambia qualcosa: macOS calcola da solo l'avanzamento finche' suona.
            double expected = _nowPlayingShown.Playing ? _nowPlayingShown.Pos + 1 : _nowPlayingShown.Pos;
            if (title == _nowPlayingShown.Title && playing == _nowPlayingShown.Playing && art == _nowPlayingArt && Math.Abs(pos - expected) < 2.5) { _nowPlayingShown.Pos = pos; return; }
            _nowPlayingShown = (title, playing, pos);
            _nowPlayingArt = art;
            MacMediaKeys.Update(title, artist, pos, dur, playing, art);
        }).Start();
    }

    private string? _nowPlayingArt;

    /// <summary>La finestra sotto il puntatore e' del player (nostra, del video di mpv o di Wine/VideoHost)?</summary>
    private static bool PointerOverPlayerWindow()
    {
        if (MacGlass.WindowOwnerAtMouse() is not { } owner) return false;
        if (owner.Pid == Environment.ProcessId) return true;
        string name = owner.Owner.ToLowerInvariant();
        return name.Contains("wine") || name.Contains("cinecore") || name.Contains("videohost") || name.Contains("mpv");
    }

    private static readonly bool _qaMenuTiming = Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null;

    // Indirizzi della rete per l'abbinamento del telefono: ~23 ms a leggerli (interfacce di rete), a ogni menu.
    private static readonly StaleCache<string[]> LocalAddresses = new(RemoteServer.LocalIPv4List, TimeSpan.FromSeconds(30));

    /// <summary>Menu del tasto destro sulla libreria (anche dall'azione di prova <c>lib-menu:x,y</c>).</summary>
    private void OpenLibraryMenu(Point at)
    {
        if (_menu.IsOpen) { LogMenuBlocked("libreria"); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var entries = BuildMenu();
        long tBuild = sw.ElapsedMilliseconds;
        _menu.Open(entries, at, null, legacyGlass: false, captureFallback: CaptureLibraryBackground);
        if (sw.ElapsedMilliseconds > 40 || _qaMenuTiming) Console.Error.WriteLine($"[menu] libreria: apertura {sw.ElapsedMilliseconds} ms (voci {tBuild} ms)");
    }

    private void ShowContextMenu(Point at)
    {
        // Gia' aperto: resta com'e' (su Windows lo si riporta solo in primo piano).
        if (_menu.IsOpen) return;
        _timelinePreview.Dismiss();
        _hud.FadeOutNow();
        bool hasVideo = _video.IsRunning && _file != null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var entries = BuildMenu();
        long tBuild = sw.ElapsedMilliseconds;
        var ambient = hasVideo ? MenuAmbient.Current(MediaPath(_file), _hud.Position) : null;
        long tAmbient = sw.ElapsedMilliseconds;
        _menu.Open(entries, at, ambient);
        if (sw.ElapsedMilliseconds > 40 || _qaMenuTiming) Console.Error.WriteLine($"[menu] apertura {sw.ElapsedMilliseconds} ms (voci {tBuild}, sfondo {tAmbient - tBuild}, disegno {sw.ElapsedMilliseconds - tAmbient})");
        if (!_menu.IsOpen) { _hud.MenuClosed(at); return; }
        // Il vetro nasce da un fotogramma del punto in riproduzione (sopra madVR la cattura dello schermo non vede il film).
        if (hasVideo && !_menu.HasNativeGlass && !IsStream(_file!))
            MenuAmbient.Refresh(MediaPath(_file), _hud.Position, _probe, frame => _menu.UpdateAmbient(frame));
    }

    /// <summary>
    /// Primo livello come su Windows: prima cio' che riguarda quello che si sta guardando, poi dove andare, poi il resto.
    /// Le voci delle funzioni non ancora portate sul Mac non compaiono (come le voci non disponibili su Windows).
    /// </summary>
    private System.Collections.Generic.List<MenuEntry> BuildMenu()
    {
        bool hasEngine = _video.IsRunning && _file != null;
        bool isVideo = hasEngine && _probe?.HasVideo != false;
        bool isAudioOnly = hasEngine && _probe is { HasVideo: false };
        bool fullscreen = IsFullscreen;
        bool paused = hasEngine && !_hud.IsPlaying;
        // Musica in corso (senza film): Pausa, Chiudi e Audio valgono per la musica, le voci da film restano bloccate.
        bool music = !hasEngine && _library.MusicActive;
        if (music) paused = !_library.MusicPlaying;

        var openRoot = new MenuEntry("Apri", "folder").With(
            new MenuEntry("Apri file…", "folder", () => _ = OpenAsync()),
            new MenuEntry("Apri playlist (.wpl)…", "playlist", () => _ = OpenPlaylistFileAsync()),
            OpenDiscMenu(),
            new MenuEntry("Indirizzo web (YouTube)…", "network", _web.Open));

        var play = new MenuEntry(paused ? "Riprendi" : "Pausa", paused ? "play" : "pause", music ? _library.MenuToggleMusic : TogglePlay) { Enabled = hasEngine || music };
        var full = new MenuEntry(fullscreen ? "Esci da schermo intero" : "Schermo intero", fullscreen ? "minimize" : "maximize", ToggleFullscreen);
        var stop = new MenuEntry(music ? "Interrompi la musica" : "Chiudi file", "stop", music ? _library.MenuStopMusic : CloseFile) { Enabled = hasEngine || music };

        var chapters = new MenuEntry("Capitoli", "chapters") { Children = new() };
        var list = _probe?.Chapters ?? new();
        // DVD con i menu: i capitoli del titolo che sta suonando, dal navigatore.
        if (_video.ActiveDvdNav is { InMenu: false } chapterNav)
        {
            var times = chapterNav.Chapters();
            list = times.Select((t, i) => (t, $"Capitolo {i + 1}")).ToList();
        }
        chapters.Enabled = isVideo && list.Count > 0;
        if (list.Count == 0) chapters.Children.Add(new MenuEntry("Nessun capitolo") { Enabled = false });
        int chapterIndex = 0;
        foreach (var (start, title) in list)
        {
            double s = start; int number = ++chapterIndex;
            chapters.Children.Add(new MenuEntry($"{Fmt(start)}  {title}", null, () =>
            {
                if (_video.ActiveDvdNav is { InMenu: false } nav) nav.PlayChapter(number); else SeekTo(s);
            }));
        }
        // Voci sempre al loro posto, bloccate finche' non si possono usare (richiesta dell'utente, 14/11).
        bool inDvdMenu = _video.ActiveDvdNav is { InMenu: true };
        var goTo = new MenuEntry("Vai a…", "time", OpenGoTo) { Enabled = hasEngine && !inDvdMenu };
        var loop = new MenuEntry("Loop brano", "repeat", () =>
        {
            _loopFile = _loopFile != null && _loopFile == _file ? null : _file;
            _osd.Show(_loopFile != null ? "Loop brano attivo" : "Loop brano disattivato", 1800);
        }) { Enabled = hasEngine && _file != null && !inDvdMenu, Checked = _loopFile != null && _loopFile == _file };
        var queue = new MenuEntry("Coda", "queue") { Enabled = hasEngine || music || _videoQueue.Count > 0, Children = new() };
        if (music)
        {
            // Musica in corso: la coda e' quella della barra in basso (prima la voce restava sempre spenta).
            queue.Children.Add(new MenuEntry("Gestisci coda", "queue", () => { ShowLibrary(true); _library.MenuOpenMusicQueue(); }));
            var upcoming = _library.RemoteMusicQueue.Select((q, i) => (q.Item, q.Current, i)).SkipWhile(q => !q.Current).Skip(1).Take(12).ToList();
            if (upcoming.Count > 0) queue.Children.Add(MenuEntry.Sep());
            foreach (var (track, _, index) in upcoming)
                queue.Children.Add(new MenuEntry(track.Title, "play", () => _library.MenuPlayQueueIndex(index)));
        }
        else if (_videoQueue.Count == 0)
            queue.Children.Add(new MenuEntry("Coda vuota") { Enabled = false });
        else
        {
            queue.Children.Add(new MenuEntry("Svuota coda", null, _videoQueue.Clear));
            queue.Children.Add(MenuEntry.Sep());
            foreach (var item in _videoQueue)
                queue.Children.Add(new MenuEntry(item.Title, "play", () => { _videoQueue.Remove(item); StartVideo(item.Path, item.Title); }));
        }

        // Traccia audio: i flussi audio di LAV Splitter, con i nomi ripuliti come su Windows.
        var audioLang = new MenuEntry("Traccia audio") { Children = new(), Enabled = isVideo || isAudioOnly };
        var audioStreams = _tracks.Where(t => t.Group == 1).ToList();
        if (audioStreams.Count == 0) audioLang.Children.Add(new MenuEntry("(nessuna traccia)") { Enabled = false });
        int ordinal = 0;
        foreach (var t in audioStreams)
        {
            ordinal++;
            string name = CinecorePlayer2025.Utilities.SubtitleNameNormalizer.NormalizeAudioTrackName(t.Name, ordinal);
            int index = t.Index;
            bool selected = t.Selected;
            audioLang.Children.Add(new MenuEntry(name, null, () => { if (!selected) SelectAudioTrack(index); _hud.Poke(); }) { Checked = t.Selected });
        }
        var subtitles = BuildSubtitlesMenu(isVideo);
        var audioDelay = AudioDelayMenu();
        audioDelay.Enabled = hasEngine && isVideo;
        var audioRoot = new MenuEntry("Audio") { Enabled = isVideo || isAudioOnly || music }.With(
            audioLang, AudioOutputMenu(),
            new MenuEntry("Preferenza uscita audio", "passthrough") { Enabled = !music, Children = new()
            {
                new MenuEntry("Auto (bitstream se conviene)", null, () => SetBitstream(true)) { Checked = MacSettings.Bitstream },
                new MenuEntry("Forza PCM (disabilita bitstream)", null, () => SetBitstream(false)) { Checked = !MacSettings.Bitstream },
            } },
            MenuEntry.Sep(),
            new MenuEntry("Audio esterno e sincronizzazione…", "sync", OpenAudioSync) { Enabled = isVideo && !inDvdMenu },
            audioDelay,
            new MenuEntry("Ricorda il volume di ogni film", "volume", () => MacSettings.RememberFilmVolume = !MacSettings.RememberFilmVolume)
                { Checked = MacSettings.RememberFilmVolume, Enabled = !music },
            music
                ? new MenuEntry(_library.MusicMuted ? "Riattiva audio" : "Disattiva audio", "volume", () => _library.MusicMuted = !_library.MusicMuted)
                : new MenuEntry(_muted ? "Riattiva audio" : "Disattiva audio", "volume", () => { if (TryAmplifierVolume(toggleMute: true)) return; _muted = !_muted; _hud.IsMuted = _muted; ApplyVolume(); }),
            music
                ? new MenuEntry("Impostazioni Cinecore Audio…", "settings", () => OpenSettings("Cinecore Audio"))
                : new MenuEntry(Engine == "mpv" ? "Impostazioni audio MPV…" : "Impostazioni LAV Audio…", "settings", () => OpenSettings(Engine == "mpv" ? "MPV" : "LAV Audio")));
        subtitles.Children ??= new();
        subtitles.Children.Add(MenuEntry.Sep());
        subtitles.Children.Add(new MenuEntry("Apri file di sottotitoli…", "folder", () => _ = OpenSubtitleAsync()));
        // Come su Windows: cerca, scarica e riallinea solo per i file sul Mac (non per DLNA, Jellyfin, Emby).
        bool localVideo = _file != null && !IsStream(_file);
        subtitles.Children.Add(new MenuEntry(localVideo ? "Scarica sottotitoli…" : "Scarica sottotitoli… (solo file sul Mac)", "search", () =>
        {
            if (_file is { } video) { _subtitleSearch.OverVideo = _hud.VideoVisible; _subtitleSearch.Open(video, _hud.Title, _hud.Duration > 0 ? _hud.Duration : null); }
        }) { Enabled = localVideo });
        if (_externalSubtitle != null && localVideo)
            subtitles.Children.Add(new MenuEntry("Riallinea sull’audio", "sync", RealignExternalSubtitle));
        subtitles.Children.Add(new MenuEntry("Impostazioni sottotitoli…", "settings", () => OpenSettings("Sottotitoli")));

        // Motore / renderer video: madVR e MPC Video Renderer sotto Wine, MPV nativo (libmpv) come su Windows.
        MenuEntry RendererChoice(string title, string preference, bool available = true) =>
            new(title, null, () => { MacSettings.ChooseRenderer(preference); SetRenderer(MacSettings.EffectiveRenderer); })
            {
                Enabled = available,
                // Con un film aperto si segna il motore che gira davvero (il risparmio energetico puo' averlo cambiato).
                Checked = _file != null && _video.IsRunning
                    ? preference != "Auto" && preference.Equals(_renderer, StringComparison.OrdinalIgnoreCase)
                    : MacSettings.Renderer.Equals(preference, StringComparison.OrdinalIgnoreCase)
            };
        // madVR e MPC-VR senza motore scaricato: la voce porta alla riga delle Impostazioni che lo scarica.
        MenuEntry EngineChoice(string title, string preference) => VideoHostClient.WineAvailable || !MacEngine.CanInstall
            ? RendererChoice(title, preference, VideoHostClient.WineAvailable)
            : new(title + " (da scaricare)", null, () =>
            {
                OpenSettings("Generali");
                _osd.Show(MacEngine.Busy ? MacEngine.State : "Scarica il motore madVR / MPC-VR da Impostazioni › Generali › Aggiornamenti", 4200);
            });
        var renderer = new MenuEntry("Motore / renderer video").With(
            EngineChoice("madVR", "MADVR"),
            EngineChoice("MPCVR", "MPCVR"),
            RendererChoice("MPV (libmpv)", "MPV", MpvEngine.Available),
            RendererChoice("Auto (ordine preferito)", "Auto"));
        var videoRoot = new MenuEntry("Video", "video").With(renderer, BuildPresetMenu(hasEngine));
        // Qualita' dei flussi Jellyfin ed Emby: la stessa scelta delle Impostazioni, quando c'e' un account.
        bool hasServerAccount = false;
        try { hasServerAccount = CinecorePlayer2025.Utilities.JellyfinClient.Accounts.Count > 0; } catch { }
        if (hasServerAccount)
            videoRoot.Children!.Add(new MenuEntry("Qualità Jellyfin ed Emby", "quality") { Children = BuildStreamQualityEntries() });
        // [2026-11-22] Niente piu' PiP del video (scelta dell'utente): resta solo il mini player della musica.
        if (isVideo)
        {
            var crop = new MenuEntry("Ritaglio", "fullscreen") { Children = new() { new MenuEntry("Nessuno", null, () => SetVideoCrop(0)) { Checked = _cropAspect <= 0 }, MenuEntry.Sep() } };
            foreach (var (aspect, label) in CropChoices)
            {
                double wanted = aspect;
                crop.Children.Add(new MenuEntry(label, null, () => SetVideoCrop(wanted)) { Checked = Math.Abs(aspect - _cropAspect) < 0.005 });
            }
            videoRoot.Children!.Add(crop);
        }
        // Upscaling (oltre nativo), come Windows: acceso chiede madVR (se il film usa un altro renderer si passa a madVR).
        if (VideoHostClient.WineAvailable && isVideo)
        {
            bool upscaling = MacSettings.Get("upscaling", "1") == "1";
            videoRoot.Children!.Add(new MenuEntry("Upscaling (oltre nativo)", "nativo", () =>
            {
                bool enable = !(upscaling && _renderer == "madvr");
                MacSettings.Set("upscaling", enable);
                if (enable && _renderer != "madvr") { MacSettings.ChooseRenderer("MADVR"); SetRenderer(MacSettings.EffectiveRenderer); }
                _osd.Show(enable ? "Upscaling madVR attivo" : "Upscaling disattivato", 2000);
            }) { Checked = upscaling && _renderer == "madvr" });
        }
        if (isVideo) { videoRoot.Children!.Add(PictureHdrMenu()); videoRoot.Children.Add(StereoMenu()); }
        if (_renderer == "madvr")
        {
            videoRoot.Children.Add(new MenuEntry("Statistiche madVR", "info", ToggleRendererStats) { Enabled = hasEngine });
            videoRoot.Children.Add(new MenuEntry("Azzera statistiche", null, () => _video.Send("key ctrl+r")) { Enabled = hasEngine });
        }
        if (_renderer == "mpcvr")
            videoRoot.Children!.Add(new MenuEntry("Statistiche MPC-VR", "info", () => _video.Send("key ctrl+j")) { Enabled = hasEngine });
        if (Engine == "mpv")
        {
            videoRoot.Children!.Add(new MenuEntry("Statistiche mpv", "info", () => _video.Send("key ctrl+j")) { Enabled = hasEngine });
            videoRoot.Children.Add(new MenuEntry("Impostazioni MPV…", "settings", () => OpenSettings("MPV")));
        }
        else
            videoRoot.Children!.Add(new MenuEntry("Impostazioni renderer…", "settings", () => OpenSettings(_renderer == "madvr" ? "madVR" : "MPC Video Renderer")));

        // Statistiche del motore in uso anche al primo livello (in Video restavano in fondo e non si trovavano).
        string statsName = Engine == "madvr" ? "madVR" : Engine == "mpcvr" ? "MPC-VR" : "MPV";
        var rendererStats = new MenuEntry("Statistiche " + statsName, "info", ToggleRendererStats) { Enabled = hasEngine && isVideo };
        var showInfo = new MenuEntry(_hud.InfoOpen ? "Nascondi info" : "Mostra info", "info",
            () => { if (_hud.InfoOpen) _hud.CloseInfo(); else _hud.OpenInfo(); }) { Enabled = hasEngine };
        var exit = new MenuEntry("Esci", "exit", () => Close());

        // Dispositivi (mDevices di Windows): per ora l'abbinamento del telefono.
        // "Il telefono non si collega? Cambia indirizzo": il codice con il nome del Mac o con uno degli IP.
        var addresses = new MenuEntry("Il telefono non si collega? Cambia indirizzo", "network") { Children = new() };
        addresses.Children.Add(new MenuEntry("Nome del Mac (consigliato)", null, () => { StartRemote(); _pairing.Hide(); ShowPairing(null); }));
        try
        {
            foreach (string ip in LocalAddresses.Get())
            {
                string host = ip;
                addresses.Children.Add(new MenuEntry(host, null, () => { StartRemote(); _pairing.Hide(); ShowPairing(host); }));
            }
        }
        catch { }
        var devices = new MenuEntry("Dispositivi", "network") { Children = new()
        {
            new MenuEntry("Abbina il telefono", "remote", RequestPairing),
            addresses,
            // Come Windows: il ricevitore apre la sua pagina (Impostazioni › Dispositivi), WLED ha interruttore e scheda.
            new MenuEntry("Amplificatore via rete…", "amplifier", () => OpenSettings("Dispositivi")),
            new MenuEntry("Dispositivi e automazioni…", "light", _automations.Open),
            new MenuEntry("WLED", "light") { Children = new()
            {
                new MenuEntry("Abilita controllo LED", null, () =>
                {
                    if (MacWled.Normalize(MacWled.Address) == null) { _wled.Open(); return; }
                    MacWled.Enabled = !MacWled.Enabled;
                    if (!MacWled.Enabled) MacWled.Disabled();
                    _osd.Show(MacWled.Enabled ? "Luci WLED: attive" : "Luci WLED: spente", 1600);
                }) { Checked = MacWled.Enabled },
                new MenuEntry("Configura dispositivo…", "settings", _wled.Open),
            } },
        } };
        var streamEntries = CurrentStreamQualityEntries();
        var streamQuality = new MenuEntry(IsYouTubeVideo ? "Risoluzione web (YouTube)" : "Qualità dello streaming", "stream-quality")
        {
            Visible = isVideo && streamEntries is { Count: > 0 },
            Children = streamEntries,
        };
        // Come Windows: Spotlight subito dopo Apri.
        var spotlight = new MenuEntry(_spotlight.IsOpen ? "Chiudi Spotlight" : "Spotlight", "spotlight", () => { if (_spotlight.IsOpen) _spotlight.Close(); else OpenSpotlight(); });
        // Primo livello corto (14/11, richiesta dell'utente): le voci della riproduzione stanno in "Riproduzione",
        // la qualita' dello streaming in Video.
        // Durante uno streaming la qualita' sta in cima a Video (altrimenti c'e' gia' "Qualita' Jellyfin ed Emby" piu' in basso).
        if (streamQuality.Visible) { videoRoot.Children ??= new(); videoRoot.Children.RemoveAll(c => c.Text == "Qualità Jellyfin ed Emby"); videoRoot.Children.Insert(0, streamQuality); videoRoot.Children.Insert(1, MenuEntry.Sep()); }
        var playback = new MenuEntry("Riproduzione", "play") { Children = new()
        {
            goTo, chapters, DvdMenuEntry() ?? new MenuEntry("") { Visible = false }, DiscTitlesMenu() ?? new MenuEntry("") { Visible = false },
            MenuEntry.Sep(), queue, loop, MenuEntry.Sep(), BeforeFilmMenu(),
        } };
        return new()
        {
            openRoot,
            spotlight,
            MenuEntry.Sep(),
            play, full, stop,
            MenuEntry.Sep(),
            playback, audioRoot, subtitles, videoRoot,
            MenuEntry.Sep(),
            showInfo, rendererStats,
            MenuEntry.Sep(),
            devices,
            new MenuEntry("Strumenti", "grid").With(
                new MenuEntry("Analisi HDR e SDR…", "spectrum", OpenHdrAnalysis) { Enabled = HdrCandidate() != null },
                new MenuEntry("Rapporto della libreria…", "list", () => _report.Open(LibraryReportView.CollectSources())),
                MenuEntry.Sep(),
                new MenuEntry("Consigliati per te (Trakt)…", "star", () => _trakt.Open(LibraryView.CollectTitlesForRecommendations())),
                new MenuEntry("Ascolti (Last.fm, ListenBrainz)…", "music", _scrobble.Open),
                MenuEntry.Sep(),
                new MenuEntry("Componenti esterni…", "cube", _components.Open),
                new MenuEntry("Genius: verifica connessione…", "music", CinecorePlayer2025.Utilities.GeniusBrowserSession.ShowVerification) { Visible = CinecorePlayer2025.Utilities.GeniusBrowserSession.Available },
                MenuEntry.Sep(),
                new MenuEntry("Controlla aggiornamenti…", "download", _update.Open),
                new MenuEntry("Cerca aggiornamenti all'avvio", null, () => MacUpdate.AutoCheck = !MacUpdate.AutoCheck) { Checked = MacUpdate.AutoCheck }),
            MenuEntry.Sep(),
            new MenuEntry("Impostazioni", "settings", OpenSettings),
            exit,
        };
    }

    private string? _probedFile;

    /// <summary>
    /// Controllo all'avvio (StartUpdateCheck di Windows): dopo qualche secondo, in sottofondo. La proposta non interrompe un
    /// film: aspetta che non ci sia niente in riproduzione e nessun'altra scheda aperta.
    /// </summary>
    private void ScheduleUpdateCheck()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) return;
        DispatcherTimer.RunOnce(async () =>
        {
            MacYtDlp.CheckAtStartup();
            var release = await MacUpdate.CheckAtStartupAsync();
            if (release == null) return;
            Console.Error.WriteLine($"[aggiornamenti] disponibile {release.Version} (in uso {MacUpdate.Current})");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                if (_closing) { timer.Stop(); return; }
                if (_video.IsRunning || _library.MusicPlaying || !IsActive || _menu.IsOpen || _settings.IsVisible || _metadata.IsVisible) return;
                timer.Stop();
                _update.Offer(release);
            };
            timer.Start();
        }, TimeSpan.FromSeconds(8));
    }

    private (string, string, System.Collections.Generic.IReadOnlyList<(string, System.Collections.Generic.IReadOnlyList<(string, string)>)>)? _infoSnapshot;
    private bool _infoBuilding;
    private (string, string, System.Collections.Generic.IReadOnlyList<(string, System.Collections.Generic.IReadOnlyList<(string, string)>)>)? BuildInfo()
    {
        if (_infoSnapshot == null) RefreshInfoAsync();
        return _infoSnapshot;
    }

    private void ToggleRendererStats()
    {
        if (_video.IsRunning) _video.Send("key ctrl+j");
    }

    private async void RefreshInfoAsync()
    {
        if (_infoBuilding || _file == null) return;
        _infoBuilding = true;
        string file = _file, title = _hud.Title;
        long session = _video.SessionId;
        var probe = _probe; var runtime = _runtime;
        double duration = _hud.Duration;
        var center = (Position.X + Width / 2, Position.Y + Height / 2);
        try
        {
            var data = await Task.Run(() => InfoBuilder.Build(title, file, probe, runtime, duration, center));
            if (session == _video.SessionId && !_closing) { _infoSnapshot = data; _hud.InfoChanged(); }
        }
        catch (Exception ex) { Console.Error.WriteLine("info: " + ex.GetType().Name); }
        finally { _infoBuilding = false; }
    }

    private async System.Threading.Tasks.Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri video",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Video") { Patterns = new[] { "*.mkv", "*.mp4", "*.m2ts", "*.ts", "*.mov", "*.avi", "*.webm", "*.iso", "*.vob", "*.mpg" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) StartVideo(path);
    }

    /// <summary>Apri › Apri playlist: playlist di Windows Media Player (.wpl).</summary>
    private async System.Threading.Tasks.Task OpenPlaylistFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri playlist",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Playlist di Windows Media Player") { Patterns = new[] { "*.wpl" } } },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) OpenFiles(new[] { path });
    }
    private string? _pendingPlaylist;

    private void TogglePlay()
    {
        // Durante lo scan, play lo ferma e il film riprende da dove e' arrivato (come Windows).
        if (ScanActive) { StopScan(); if (!_hud.IsPlaying) { _video.Play(); _hud.IsPlaying = true; } _hud.Poke(); return; }
        if (!_video.IsRunning) { if (_file != null) StartVideo(_file, _titleOverride); return; }
        if (_hud.IsPlaying) { _video.Pause(); _hud.IsPlaying = false; }
        else { _video.Play(); _hud.IsPlaying = true; }
        _hud.Poke();
        UpdatePauseScreen();
    }

    /// <summary>Menu › Vai a…: si scrive il minutaggio (GoToTimeView).</summary>
    private void OpenGoTo()
    {
        if (!_video.IsRunning || _file == null) return;
        _goTo.Open(_hud.Position, _hud.Duration);
    }

    private void SeekTo(double seconds)
    {
        if (_hud.Duration > 0) seconds = Math.Clamp(seconds, 0, _hud.Duration);
        _hud.Position = Math.Max(0, seconds);
        _dropSamples.Clear();
        _video.Seek(Math.Max(0, seconds));
        _hud.Poke();
    }

    private string? _filmVolumeKey;
    /// <summary>Volume cambiato durante un film: si ricorda per la prossima apertura (menu Audio › Ricorda il volume).</summary>
    private void RememberFilmVolume()
    {
        if (_filmVolumeKey is { } key && MacSettings.RememberFilmVolume)
            CinecorePlayer2025.Utilities.FilmVolumeStore.Set(key, amplifier: false, _volume);
    }

    // Amplificazione oltre il 100% (PlayerForm.AudioVisualization.cs di Windows): continuando ad alzare a fondo scala
    // ogni pressione aggiunge 1 dB fino a +12, con un limitatore. Con tutti i motori (madVR e MPC-VR passano dall'audio
    // nativo affiancato, VideoHostClient.SetBoost); vale per il film in corso.
    private int _volumeBoostDb;
    private string? _volumeBoostFile;

    private bool TryStepVolumeBoost(int direction)
    {
        if (!_video.IsRunning) return false;
        if (direction > 0)
        {
            if (_muted || _volume < 0.999f) return false;
            if (_video.BitstreamActive) { _osd.Show("Audio in bitstream: il volume lo decide l’amplificatore", 2400); return true; }
            if (_volumeBoostDb < 12 && !SetVolumeBoost(_volumeBoostDb + 1)) { _osd.Show("Oltre il 100% non è disponibile con questa sorgente", 2800); return true; }
        }
        else
        {
            if (_volumeBoostDb <= 0 || _volumeBoostFile != _file) return false;
            SetVolumeBoost(_volumeBoostDb - 1);
        }
        _osd.Show(_volumeBoostDb > 0 ? $"100% +{_volumeBoostDb} dB" : "100%", 1300);
        return true;
    }

    private bool SetVolumeBoost(int db)
    {
        db = Math.Clamp(db, 0, 12);
        if (!_video.SetBoost(db) && db > 0) return false;
        _volumeBoostDb = db;
        _volumeBoostFile = _file;
        return true;
    }

    private void ApplyVolume()
    {
        // L'amplificazione vale per il film in cui e' stata chiesta e solo a fondo scala.
        if (_volumeBoostDb > 0 && (_volume < 0.999f || _volumeBoostFile != _file)) SetVolumeBoost(0);
        else if (_volumeBoostDb > 0 && !_video.SetBoost(_volumeBoostDb)) _volumeBoostDb = 0; // motore riaperto sullo stesso film
        ApplyVolumeLevel();
    }

    // Durante l'analisi HDR/SDR il film resta muto (senza toccare il muto scelto dall'utente).
    private void ApplyVolumeLevel() => _video.SetVolume(Environment.GetEnvironmentVariable("CINECORE_QA_MUTE") == "1" || _muted || _hdrFile != null ? 0 : (int)Math.Round(_volume * 100));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        if (IsFullscreen == _wasFullscreen) return;
        _wasFullscreen = IsFullscreen;
        FullscreenChanged();
    }

    private (PixelPoint Position, double Width, double Height)? _sizeAfterFullscreen;

    private void FullscreenChanged()
    {
        // Mini player portato a schermo intero da macOS (pulsante verde, ⌃⌘F): a schermo intero va il player normale;
        // all'uscita la finestra torna alla misura di prima del mini player.
        if (IsFullscreen && _beforeMini is { } mini)
        {
            _beforeMini = null;
            Topmost = false;
            MinWidth = WindowMinWidth; MinHeight = WindowMinHeight;
            _library.SetMiniPlayer(false);
            _sizeAfterFullscreen = mini;
        }
        else if (!IsFullscreen && _sizeAfterFullscreen is { } restore)
        {
            _sizeAfterFullscreen = null;
            DispatcherTimer.RunOnce(() => { Width = restore.Width; Height = restore.Height; Position = restore.Position; }, TimeSpan.FromMilliseconds(100));
        }
        // I pannelli del menu sono posizionati nelle coordinate della finestra prima del passaggio di Space.
        if (_menu.IsOpen) _menu.Close();
        _nextStackCheck = 0;
        _videoNeedsOrder = _video.IsRunning && Engine != "mpv";
        BeginFullscreenTransition();
        UpdateVideoStack();
        _secondaryPresses = MacGlass.SecondaryPressCount();
        _unhandledSecondaryAt = null;
        _hud.CancelPointerInteraction();
        _hud.Poke();
        SetLoadingRaised(false);
        // Schermo intero di sistema (anche dal pulsante verde): la barra che scende dall'alto resta trasparente,
        // senza scritta e senza linea di separazione. Si ripassa a fine animazione, quando macOS ha creato la sua barra.
        Dispatcher.UIThread.Post(RefreshWindowChrome, DispatcherPriority.Background);
        UpdateWindowButtons();
        DispatcherTimer.RunOnce(RefreshWindowChrome, TimeSpan.FromMilliseconds(1200));
        _revealBand = IsFullscreen ? MacWindowChrome.FullscreenRevealHeight(this) : 0;
        if (IsFullscreen && _library.IsVisible) _revealTimer.Start();
    }

    internal bool IsFullscreen => WindowState == WindowState.FullScreen;

    // Solo la libreria segue la barra di sistema. L'HUD del film conserva la sua disposizione e l'autohide.
    private double _revealBand, _revealShift;
    private const double RevealNudge = 14;
    private bool _revealed;
    private DispatcherTimer? _revealTimerField;
    private DispatcherTimer _revealTimer => _revealTimerField ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => RevealTick());

    private void RevealTick()
    {
        if (!IsFullscreen || !_library.IsVisible || _revealBand <= 0) _revealed = false;
        else if (MacWindowChrome.PointerYFromTop(this) is { } y)
        {
            if (y is >= -2 and <= 2) _revealed = true;
            else if (y > _revealBand + 6) _revealed = false;
        }
        // Basta un piccolo scarto per non farsi toccare dalla barra (l'altezza della barra serve solo a capire quando risale).
        double target = _revealed ? RevealNudge : 0;
        _revealShift = Math.Abs(target - _revealShift) < .5 ? target : _revealShift + (target - _revealShift) * .3;
        _library.TopShift = _revealShift;
        if ((!IsFullscreen || !_library.IsVisible) && _revealShift == 0) _revealTimer.Stop();
    }

    private void ToggleFullscreen()
    {
        // Dal mini player: prima la finestra torna com'era, poi va a schermo intero quella (non la finestrella).
        if (_beforeMini != null)
        {
            ToggleMiniPlayer();
            DispatcherTimer.RunOnce(ToggleFullscreen, TimeSpan.FromMilliseconds(250));
            return;
        }
        // Un secondo comando mentre macOS sta ancora animando il cambio lasciava la finestra a meta' (ne' intera ne'
        // in finestra): si aspetta che il primo finisca.
        long nowToggle = Environment.TickCount64;
        if (nowToggle - _lastFullscreenToggle < 1100) return;
        _lastFullscreenToggle = nowToggle;
        // Con madVR o MPC-VR il video sparisce prima che parta l'animazione (vedi BeginFullscreenTransition).
        BeginFullscreenTransition();
        if (_fsTransition)
            DispatcherTimer.RunOnce(() => WindowState = IsFullscreen ? WindowState.Normal : WindowState.FullScreen, TimeSpan.FromMilliseconds(70));
        else WindowState = IsFullscreen ? WindowState.Normal : WindowState.FullScreen;
        _hud.Poke();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // Menu aperto: i tasti sono suoi (Esc, frecce, Invio) e non arrivano al player.
        if (_menu.IsOpen) { _menu.HandleKey(e.Key); e.Handled = true; return; }
        if (_metadata.IsOpen)
        {
            if (e.Key == Key.Escape || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { _metadata.Close(); e.Handled = true; }
            return;
        }
        if (_subtitleSearch.IsOpen) { _subtitleSearch.HandleKey(e); return; }
        if (_report.IsOpen) { _report.HandleKey(e); return; }
        if (_hdr.IsOpen && !_menu.IsOpen) { _hdr.HandleKey(e); return; }
        if (_components.IsOpen) { _components.HandleKey(e); return; }
        if (_web.IsOpen) { _web.HandleKey(e); return; }
        if (_wled.IsOpen) { _wled.HandleKey(e); return; }
        if (_goTo.IsOpen) { _goTo.HandleKey(e); return; }
        if (_youtube.IsOpen) { _youtube.HandleKey(e); return; }
        if (_gate.IsOpen && !_settings.IsOpen && !_menu.IsOpen) { _gate.HandleKey(e); if (e.Handled) return; }
        if (_audioSync.IsOpen) { _audioSync.HandleKey(e); return; }
        if (_scrobble.IsOpen) { _scrobble.HandleKey(e); return; }
        if (_update.IsOpen) { _update.HandleKey(e); return; }
        if (_photoEdit.IsOpen) { _photoEdit.HandleKey(e); return; }
        if (_photoViewer.IsOpen) { _photoViewer.HandleKey(e); return; }
        if (_trakt.IsOpen && !_menu.IsOpen) { _trakt.HandleKey(e); return; }
        if (_spotlight.IsOpen && !_settings.IsOpen)
        {
            if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta)) { Close(); e.Handled = true; return; }
            _spotlight.HandleKey(e);
            return;
        }
        if (_library.IsVisible && !_settings.IsOpen)
        {
            if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Meta)) { _ = OpenAsync(); e.Handled = true; return; }
            if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta)) { Close(); e.Handled = true; return; }
            _library.HandleKey(e);
            if (!e.Handled && IsFullscreen && e.Key == Key.Escape)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            return;
        }
        if (_settings.IsOpen)
        {
            if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta)) { Close(); e.Handled = true; return; }
            _settings.HandleKey(e);
            return;
        }
        if (_hud.IsVisible && !_hud.InfoOpen && !_hud.CastOpen && _hud.HandleChoicePromptKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        _hud.Poke();
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Escape when _hud.CastOpen: _hud.CloseCast(); break;
            case Key.Down when _hud.CastOpen: _hud.CloseCast(); break;
            case Key.Back when _hud.CastOpen: _hud.CloseCast(); break;
            case Key.Left when _hud.CastOpen: _hud.MoveCast(-1); break;
            case Key.Right when _hud.CastOpen: _hud.MoveCast(+1); break;
            case Key.Escape when _hud.InfoOpen: _hud.CloseInfo(); break;
            case Key.Up when _hud.InfoOpen: _hud.CloseInfo(); break;
            case Key.Back when _hud.InfoOpen: _hud.CloseInfo(); break;
            case Key.Up or Key.Down or Key.Left or Key.Right when _video.ActiveDvdNav is { Buttons: > 0 } dvdNav:
                dvdNav.Move(e.Key switch { Key.Up => "up", Key.Down => "down", Key.Left => "left", _ => "right" }); break;
            case Key.Enter or Key.Return when _video.ActiveDvdNav is { Buttons: > 0 } dvdNav: dvdNav.Activate(); break;
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekTo(_hud.Position - 10); break;
            case Key.Right: SeekTo(_hud.Position + 10); break;
            case Key.Up: if (TryAmplifierVolume(step: +1)) break; if (!TryStepVolumeBoost(+1)) { _volume = Math.Min(1, _volume + .05f); _hud.Volume = _volume; ApplyVolume(); RememberFilmVolume(); } break;
            case Key.Down: if (TryAmplifierVolume(step: -1)) break; if (!TryStepVolumeBoost(-1)) { _volume = Math.Max(0, _volume - .05f); _hud.Volume = _volume; ApplyVolume(); RememberFilmVolume(); } break;
            // DVD come su Windows: M porta al menu del disco, Indietro torna al menu precedente.
            case Key.M when _video.DvdNav is { } menuNav && !cmd: ShowDvdMenu(menuNav, MacDvdNav.MenuRoot); break;
            case Key.Back when _video.ActiveDvdNav is { InMenu: true } backNav: backNav.GoUp(); break;
            case Key.M: if (TryAmplifierVolume(toggleMute: true)) break; _muted = !_muted; _hud.IsMuted = _muted; ApplyVolume(); break;
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Shift): ToggleExtendedFullscreen(); break;
            case Key.F: if (_extendedFs) ExitExtendedFullscreen(); else ToggleFullscreen(); break;
            case Key.Escape when _extendedFs: ExitExtendedFullscreen(); break;
            case Key.Escape when IsFullscreen: ToggleFullscreen(); break;
            case Key.O when cmd: _ = OpenAsync(); break;
            case Key.W when cmd: Close(); break;
            // Statistiche di madVR, come Ctrl+J su Windows.
            case Key.J when e.KeyModifiers.HasFlag(KeyModifiers.Control): ToggleRendererStats(); break;
            case Key.R when e.KeyModifiers.HasFlag(KeyModifiers.Control): _video.Send("key ctrl+r"); break;
            case Key.Z when e.KeyModifiers == KeyModifiers.None && _video.IsRunning: CycleImageSizingMode(); break;
            default: if (!HandlePlayerShortcut(e)) return; break;
        }
        e.Handled = true;
    }
}
