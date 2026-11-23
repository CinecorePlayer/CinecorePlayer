using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Telecomando dal telefono (RemoteServer di Windows, compilato in comune): la stessa pagina web, lo stesso abbinamento
// con PIN e codice QR, gli stessi comandi. Qui lo stato del player per il telefono e cosa fa ogni comando sul Mac.
public sealed partial class MainWindow
{
    private RemoteServer? _remote;
    private readonly PairingView _pairing = new();
    private volatile object _remoteCompanion = new { queue = Array.Empty<object>(), library = Array.Empty<object>() };
    private volatile bool _remoteCompanionRequested;
    private int _remoteCompanionBuilding;
    private bool _remoteLibraryNetwork;
    private long _remoteLibraryAt;
    private object _remoteLibrary = Array.Empty<object>();
    private string[]? _remoteRenderers;
    private string? _remotePlaybackRenderer;
    private List<(string Uid, string Name)> _remoteOutputs = new();
    private long _remoteOutputAt;
    // Il server legge lo stato dal suo thread: un'istantanea preparata sul thread grafico (come _remoteSnapshot su Windows).
    private volatile RemoteState _remoteSnapshot = new();
    private DispatcherTimer? _remoteSnapshotTimer;

    private void StartRemote()
    {
        if (_remote != null) return;
        Environment.SetEnvironmentVariable("CINECORE_ASSETS", MacTools.AssetsDirectory);
        _remoteSnapshot = BuildRemoteState();
        _remoteSnapshotTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (_closing) return;
            _remoteSnapshot = BuildRemoteState();
            if (_remoteCompanionRequested) RefreshRemoteCompanion();
        });
        _remoteSnapshotTimer.Start();
        // CINECORE_REMOTE_PORT: istanze di prova accanto al player vero (che tiene la 9234).
        int remotePort = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_REMOTE_PORT"), out int qaPort) && qaPort > 0 ? qaPort : 9234;
        _remote = new RemoteServer(remotePort, remotePort != 9234 ? Environment.GetEnvironmentVariable("CINECORE_QA_PIN") : null, RemotePlaybackState, (cmd, q) =>
        {
            // Trackpad: SendInput direttamente dal thread della richiesta. Prima ogni movimento passava dal thread
            // dell'interfaccia e ricostruiva tutto lo stato del telecomando: il puntatore restava indietro di secondi.
            if (cmd is "pointer_move" or "pointer_scroll" or "pointer_click")
            {
                double N(string k) => q.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
                try
                {
                    if (cmd == "pointer_move") RemotePointer.Move(N("dx"), N("dy"));
                    else if (cmd == "pointer_scroll") RemotePointer.Scroll(N("dy"));
                    else RemotePointer.Click(q.TryGetValue("button", out var b) && b == "right", q.TryGetValue("double", out var dbl) && dbl == "1");
                }
                catch { }
                return;
            }
            Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
            try
            {
                HandleRemoteCommand(cmd, q);
                if (_closing) return;
                _remoteSnapshot = BuildRemoteState();
                if (_remoteCompanionRequested) RefreshRemoteCompanion();
            }
            catch (Exception ex) { Console.Error.WriteLine($"telecomando '{cmd}': {ex.GetType().Name} {ex.Message}"); }
        });
        }, RemoteCompanion);
        // Componenti avanzati sul telefono: le stesse schede e gli stessi valori di Impostazioni › Componenti.
        _remote.SettingsProvider = () => Dispatcher.UIThread.Invoke(RemoteComponentSettings);
        _remote.SettingsWriter = (provider, key, value) => Dispatcher.UIThread.Invoke(() => ApplyRemoteComponentSetting(provider, key, value));
#if CINECORE_WINDOWS
        _remote.GetPcStats = PcMonitor.Snapshot;
#else
        _remote.GetPcStats = MacPcMonitor.Snapshot;
#endif
        _remote.Paired += _ => Dispatcher.UIThread.Post(_pairing.Hide);
        // Un telefono gia' abbinato che si ricollega (pagina aperta che si aggiorna) non chiude il codice: prima lo
        // faceva sparire dopo pochi secondi mentre lo si stava inquadrando. Lo chiude solo chi entra con il codice.
        // Un telefono non abbinato bussa: il codice compare solo se si e' in libreria (mai sopra un film).
        _remote.PairingRequested += pin => Dispatcher.UIThread.Post(() => { if (_library.IsVisible && _file == null) ShowPairing(); });
        _remoteLibraryNetwork = _library.IsNetworkSource;
        _ = Task.Run(() => { try { _remote.Start(); } catch (Exception ex) { Console.Error.WriteLine("telecomando: " + ex.Message); } });
    }

    private void RequestPairing()
    {
        StartRemote();
        ShowPairing();
    }

    /// <summary>Codice QR per abbinare il telefono.</summary>
    private void ShowPairing() => ShowPairing(null);

    /// <summary>Codice QR per un indirizzo preciso: se il nome del Mac non arriva al telefono si prova con l'IP.</summary>
    private void ShowPairing(string? host)
    {
        if (_remote == null) return;
        string url = _remote.PairingUrlForHost(host);
        try { _pairing.Show(QrCode.Encode(url), url, _remote.CurrentPin); } catch { }
    }

    // ----- stato per /api/state -----
    private RemoteState BuildRemoteState()
    {
        static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        RefreshRemoteOutputs();
        bool video = _file != null || _gatePending != null;
        var track = video ? null : _library.RemoteMusicState;
        bool music = track != null;
        var film = video ? RemoteFilm() : null;
        var renderers = _remoteRenderers ??= AvailableRemoteRenderers();
        return new RemoteState
        {
            Title = video ? film?.Title ?? _gatePending?.Title ?? _hud.Title : track?.Item.Title,
            Subtitle = film?.Meta ?? track?.Item.ArtistName,
            ArtPath = film?.Artwork ?? RemoteArt(track?.Item.ArtPath),
            HasPlayback = video || music,
            Position = video ? _hud.Position : track?.Position ?? 0,
            Duration = video ? _hud.Duration : track?.Duration ?? 0,
            Paused = video ? _gatePending != null || !_hud.IsPlaying : track?.Paused ?? true,
            Volume = track?.Volume ?? _volume,
            Muted = track?.Muted ?? _muted,
            Renderer = _renderer == "madvr" ? "madVR" : _renderer == "mpv" ? "MPV" : "MPC Video Renderer",
            RendererSelection = video ? _renderer.ToUpperInvariant() : MacSettings.Renderer,
            DefaultRenderer = MacSettings.Renderer,
            RendererChoices = renderers,
            MadVrAvailable = renderers.Contains("MADVR"),
            HdrAvailable = true,
            HdrChoices = (_renderer == "madvr" ? new[] { "auto", "passthrough", "tonemap", "lut" } : new[] { "auto", "passthrough", "tonemap" })
                .Concat(RtxAvailable ? new[] { "rtx" } : Array.Empty<string>()).ToArray(),
            HdrMode = _renderer == "mpcvr" && ComponentSettings.RtxVideoHdr ? "rtx" : MacSettings.Get("hdrUiProfile", "auto"),
            RtxHdrAvailable = RtxAvailable,
            MpcvrNvidiaUpscalingAvailable = RtxAvailable,
            OutputHdr = IsHdrProbe(_probe) && MacSettings.Get("hdrUiProfile", "auto") is not ("tonemap" or "lut"),
            Is3D = _stereo != StereoMode.None,
            StereoMode = StereoAuto ? "auto" : _stereo == StereoMode.Sbs ? "sbs" : _stereo == StereoMode.Tab ? "tab" : "off",
            StereoAvailable = true,
            ExtendedFullscreenAvailable = CanExtendFullscreen || _extendedFs,
            Fullscreen = MacWindowChrome.IsNativeFullscreen(this) || _extendedFs,
            UpscalingAvailable = renderers.Contains("MADVR") || RtxAvailable,
            Upscaling = _renderer == "madvr" && MacSettings.Get("upscaling", "1") == "1" || _renderer == "mpcvr" && ComponentSettings.RtxSuperResolution > 0,
            UpscalingBackend = _renderer == "madvr" && MacSettings.Get("upscaling", "1") == "1" ? "madvr"
                : _renderer == "mpcvr" && ComponentSettings.RtxSuperResolution > 0 ? "mpcvr" : "off",
            Accent = Hex(UiTheme.Accent),
            AccentSoft = Hex(UiTheme.Mix(UiTheme.Accent, Colors.Black, .35)),
            Panel = Hex(UiTheme.Panel), Card = Hex(UiTheme.Card), Nav = Hex(UiTheme.Panel),
            Text = Hex(UiTheme.Text), MutedText = Hex(UiTheme.Muted),
            LightTheme = UiTheme.IsLight,
            Language = L10n.Language,
            Bitstream = _video.BitstreamActive,
            Spotlight = _spotlight.IsOpen,
            AudioDelayMs = _audioOffsetMs,
            AudioDelayAvailable = _file != null,
            SubtitleDownloadAvailable = _file != null && !IsStream(_file),
            SubtitleRealignAvailable = _file != null && !IsStream(_file) && _externalSubtitle != null,
            ChaptersAvailable = _file != null && RemoteChapters().Count > 0,
            CastAvailable = _file != null && _hud.CastAvailable,
            HdrAnalysisAvailable = HdrCandidate() != null,
            AudioDevices = _remoteOutputs.Select(d => d.Name).ToArray(),
            AudioDevice = _remoteOutputs.FirstOrDefault(d => d.Uid == MacSettings.AudioDeviceUid).Name ?? "",
            AudioDelayCurve = RemoteAudioBinding()?.HasCurve == true,
            AudioAutoSyncAvailable = CanRemoteAudioSync(),
            AudioAutoSyncRunning = _remoteAudioSync != null,
            AudioAutoSyncProgress = _remoteAudioProgress,
            AudioAutoSyncNote = _file != null && _file == _remoteAudioFile ? _remoteAudioNote ?? "" : "",
        };
    }

    // ----- libreria, tracce e capitoli per /api/companion -----
    private RemoteState RemotePlaybackState()
    {
        _remoteStateRequested = true;
        return _remoteSnapshot;
    }

    private volatile bool _remoteStateRequested;

    private object RemoteCompanion()
    {
        if (!_remoteCompanionRequested && Interlocked.Exchange(ref _remoteCompanionBuilding, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                try { _remoteCompanionRequested = true; RefreshRemoteCompanion(); }
                finally { Interlocked.Exchange(ref _remoteCompanionBuilding, 0); }
            });
        return _remoteCompanion;
    }

    private void RefreshRemoteCompanion()
    {
        try
        {
            long now = Environment.TickCount64;
            if (_remoteLibraryAt == 0 || now - _remoteLibraryAt >= 10000)
            {
                _remoteLibrary = BuildRemoteLibrary();
                _remoteLibraryAt = now;
            }
            _remoteCompanion = BuildRemoteCompanion();
        }
        catch (Exception ex) { Console.Error.WriteLine("telecomando: " + ex.Message); }
    }

    private object BuildRemoteLibrary()
    {
        var items = _library.RemoteItems(_remoteLibraryNetwork);
        object Item(LibraryItem i, string kind, string? subtitle = null, IEnumerable<object>? children = null) => new
        {
            path = i.Path,
            title = i.Category == "TV Series" && kind == "series" ? i.SeriesTitle ?? i.Title : i.Title,
            subtitle = subtitle ?? i.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
            artPath = RemoteArt(i.ArtPath ?? i.WideArtPath) ?? "",
            kind,
            seasonNumber = i.SeasonNumber,
            episodeNumber = i.EpisodeNumber,
            children = (children ?? Array.Empty<object>()).ToList(),
        };
        var movies = items.Where(i => i.Category == "Movies").OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).Select(i => Item(i, "movie")).ToList();
        var series = items.Where(i => i.Category == "TV Series")
            .GroupBy(i => (i.SeriesTitle ?? i.Title).Trim(), StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var episodes = g.OrderBy(e => e.SeasonNumber ?? 0).ThenBy(e => e.EpisodeNumber ?? 0).ToList();
                return Item(episodes[0], "series", episodes.Count == 1 ? "1 episodio" : $"{episodes.Count} episodi",
                    episodes.Select(e => Item(e, "episode", e.SeasonNumber.HasValue && e.EpisodeNumber.HasValue ? $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00}" : "")));
            }).ToList();
        var music = LibraryView.Albums(items.Where(i => i.Category == "Music"))
            .OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new { path = a.Tracks[0].Path, title = a.Title, subtitle = a.Artist,
                artPath = RemoteArt(RemoteAlbumArt(a)) ?? "",
                kind = "album", children = a.Tracks.Select(t => Item(t, "music", t.ArtistName ?? "")).ToList() }).ToList();
        var library = new List<object>();
        if (movies.Count > 0) library.Add(new { key = "Movies", label = "Film", count = movies.Count, items = movies });
        if (series.Count > 0) library.Add(new { key = "TV Series", label = "Serie TV", count = series.Count, items = series });
        if (music.Count > 0) library.Add(new { key = "Music", label = "Musica", count = music.Count, items = music });
        return library;
    }

    private object BuildRemoteCompanion()
    {
        var audioTracks = _tracks.Where(t => t.Group == 1).Select((t, n) => new { id = t.Index, label = SubtitleNameNormalizer.NormalizeAudioTrackName(t.Name, n + 1), selected = t.Selected }).ToList();
        var subtitleTracks = _tracks.Where(t => t.Group == 2 && !IsSubtitleOffOption(t.Name)).Select((t, n) => new { id = t.Index, label = SubtitleNameNormalizer.NormalizeSubtitleTrackName(t.Name, n + 1), selected = t.Selected }).ToList();
        var chapterList = RemoteChapters();
        double here = _hud.Position;
        var chapters = chapterList.Select((c, n) => new
        {
            id = n,
            label = Fmt(c.Start) + "   " + (string.IsNullOrWhiteSpace(c.Title) ? "Capitolo " + (n + 1) : c.Title),
            selected = here >= c.Start && (n + 1 >= chapterList.Count || here < chapterList[n + 1].Start),
        }).ToList();
        var film = RemoteFilm();
        var queue = _file == null ? _library.RemoteMusicQueue.Select((t, n) => (object)new
        {
            // La copertina dell'album quando il brano non ne ha una sua (prima si vedevano solo quelle dei pochi brani con ArtPath).
            path = t.Item.Path, title = t.Item.Title, current = t.Current, kind = "music", index = n, artPath = RemoteArt(_library.TrackArtFor(t.Item)) ?? ""
        }).ToList() : new List<object> { new { path = _file, title = _hud.Title, current = true, kind = "movie", index = 0, artPath = film?.Poster ?? "" } };
        // Film in coda: la locandina dalla libreria se l'elemento (per esempio da una playlist) non la porta con se'.
        queue.AddRange(_videoQueue.Select((v, n) => (object)new
        {
            path = v.Path, title = v.Title, current = false, kind = "movie", index = n + 1,
            artPath = RemoteArt(v.ArtPath ?? _library.ItemFor(v.Path)?.ArtPath ?? v.WideArtPath) ?? "",
        }));
        return new
        {
            queue, library = _remoteLibrary,
            librarySource = _remoteLibraryNetwork ? "network" : "local",
            // Schermi: dove si apre il player e dove si trova adesso (Impostazioni › Schermo del telefono).
            screens = ScreenChoices().Select(c => new { key = c.Key, label = c.Label, primary = c.Primary, current = c.Current }).ToList(),
            startScreen = StartScreen,
            pointer = RemotePointer.Available,
            // Libreria del telefono: playlist dove aggiungere e preferiti (stessi file della libreria).
            playlists = _library.RemotePlaylists(),
            favorites = Favorites.RemotePaths(),
            audioTracks, subtitleTracks,
            subtitlesOff = !subtitleTracks.Any(t => t.selected),
            chapters,
            film = film == null ? null : new
            {
                title = film.Title, meta = film.Meta, overview = film.Overview,
                poster = film.Poster, backdrop = film.Backdrop, preview = film.Preview, cast = film.Cast, tech = film.Tech, hdr = film.Hdr
            },
        };
    }

    /// <summary>
    /// Copertina di un album per il telefono. Il player la cerca solo quando disegna l'album: senza averlo mostrato
    /// il telefono la riceveva vuota. Qui si prende quella gia' in cache e, se manca, si avvia la ricerca (arriva al
    /// prossimo aggiornamento della libreria, ogni 10 s).
    /// </summary>
    private static string? RemoteAlbumArt(LibraryView.Album album)
    {
        if (album.Art is { } art) return art; // Art avvia anche la ricerca se manca
        foreach (var track in album.Tracks)
        {
            if (track.MusicTags is not { } tags) continue;
            try { if (CinecorePlayer2025.Utilities.MusicArtworkService.GetCachedArtworkPath(track.Path, tags) is { } cached && File.Exists(cached)) return cached; }
            catch { }
            break;
        }
        return null;
    }

    /// <summary>Il telefono riceve solo file d'immagine locali (/api/art): le copertine dei server si danno dalla cache.</summary>
    private static string? RemoteArt(string? art)
    {
        if (string.IsNullOrWhiteSpace(art)) return null;
        if (!art.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return File.Exists(art) ? art : null;
        try
        {
            string cached = NetworkStore.CachedArtFileIfPresent(art) ?? "";
            if (!File.Exists(cached)) return null;
            string dir = Path.Combine(LibraryStore.AppDataDir, "remoteArt");
            string target = Path.Combine(dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(art))).ToLowerInvariant() + ".jpg");
            if (!File.Exists(target)) { Directory.CreateDirectory(dir); File.Copy(cached, target, true); }
            return target;
        }
        catch { return null; }
    }

    // ----- comandi -----
    private void HandleRemoteCommand(string cmd, Dictionary<string, string> q)
    {
        bool playing = _file != null;
        string Value(string key) => q.GetValueOrDefault(key) ?? "";
        double Num(string key) => double.TryParse(Value(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : double.NaN;
        if (!playing && _gatePending == null && _library.MusicActive &&
            cmd is "toggle" or "play" or "pause" or "seek" or "back10" or "fwd10" or "prev" or "next" or "vol" or "volup" or "voldown" or "mute")
        {
            double value = cmd == "vol" ? Num("v") : cmd == "seek" ? Num("pos") : 0;
            if (double.IsFinite(value)) _library.ControlMusicFromRemote(cmd, value);
            return;
        }
        switch (cmd)
        {
            case "toggle": case "play":
                if (_gatePending != null) ConfirmCinemaGate();
                else if (playing && (cmd == "toggle" || !_hud.IsPlaying)) TogglePlay();
                break;
            case "pause": if (playing && _hud.IsPlaying) TogglePlay(); break;
            case "stop":
                StopScan();
                StopRemoteAudioSync();
                _timelinePreview.Dismiss();
                if (_gatePending != null) CancelCinemaGate();
                else if (playing) CloseFile(skipDemo: false);
                else _library.MenuStopMusic();
                break;
            case "poweroff":
                StopScan();
                StopRemoteAudioSync();
                Close();
                break;
            case "back10": if (playing) { if (ScanActive) StepScan(-1, false); else SeekTo(_hud.Position - 10); } break;
            case "fwd10": if (playing) { if (ScanActive) StepScan(+1, false); else SeekTo(_hud.Position + 10); } break;
            case "scan_back": if (playing) StepScan(-1, false); break;
            case "scan_fwd": if (playing) StepScan(+1, false); break;
            case "scrub":
                if (playing && double.IsFinite(Num("pos"))) PreviewRemotePosition(Num("pos"));
                break;
            case "seek":
                _remoteScrubVersion++; _timelinePreview.Dismiss(); StopScan();
                if (playing && double.IsFinite(Num("pos"))) SeekTo(Num("pos"));
                break;
            case "prev": if (playing) { StopScan(); JumpChapter(-1); } break;
            case "next": if (playing) { StopScan(); JumpChapter(+1); } break;
            case "chapter":
                if (playing && int.TryParse(Value("id"), out int ci) && ci >= 0 && ci < RemoteChapters().Count)
                {
                    StopScan();
                    if (_video.ActiveDvdNav is { InMenu: false } dvd) dvd.PlayChapter(ci + 1);
                    else SeekTo(RemoteChapters()[ci].Start);
                }
                break;
            case "full":
                if (_extendedFs) ExitExtendedFullscreen(); else ToggleFullscreen();
                break;
            case "fullscreen_extended": if (CanExtendFullscreen || _extendedFs) ToggleExtendedFullscreen(); break;
            case "vol": if (!double.IsNaN(Num("v")) && TryAmplifierVolume(value: (float)Math.Clamp(Num("v"), 0, 1))) break; if (!double.IsNaN(Num("v"))) { _volume = (float)Math.Clamp(Num("v"), 0, 1); _hud.Volume = _volume; ApplyVolume(); } break;
            case "volup": if (TryAmplifierVolume(step: +1)) break; if (!TryStepVolumeBoost(+1)) { _volume = Math.Min(1, _volume + .05f); _hud.Volume = _volume; ApplyVolume(); } break;
            case "voldown": if (TryAmplifierVolume(step: -1)) break; if (!TryStepVolumeBoost(-1)) { _volume = Math.Max(0, _volume - .05f); _hud.Volume = _volume; ApplyVolume(); } break;
            case "mute": if (TryAmplifierVolume(toggleMute: true)) break; _muted = !_muted; _hud.IsMuted = _muted; ApplyVolume(); break;
            case "info": if (playing) { if (_hud.InfoOpen) _hud.CloseInfo(); else _hud.OpenInfo(); } break;
            case "cast": if (playing) ToggleCast(); break;
            case "hdr_analysis": if (HdrCandidate() != null) OpenHdrAnalysis(); break;
            case "avdelay":
                if (!playing) break;
                if (Value("auto") == "1") StartRemoteAudioSync();
                else if (Value("stop") == "1") StopRemoteAudioSync();
                else if (Value("reset") == "1") ShiftAudioDelay(-_audioOffsetMs);
                else if (double.IsFinite(Num("step"))) ShiftAudioDelay((int)Math.Clamp(Num("step"), -600000, 600000));
                else if (double.IsFinite(Num("set"))) ShiftAudioDelay((int)Math.Clamp(Num("set"), -600000, 600000) - _audioOffsetMs);
                break;
            case "audio_device":
                if (!q.ContainsKey("value")) break;
                if (Value("value").Length == 0) SetAudioOutput("", "");
                else if (_remoteOutputs.FirstOrDefault(d => d.Name == Value("value")) is { Uid.Length: > 0 } output)
                    SetAudioOutput(output.Uid, output.Name);
                break;
            case "hdr":
                if (Value("mode") == "rtx") { SetRemoteRtxHdr(true); break; }
                if (Value("mode") is "auto" or "passthrough" or "tonemap" || Value("mode") == "lut" && _renderer == "madvr")
                {
                    if (ComponentSettings.RtxVideoHdr) SetRemoteRtxHdr(false);
                    SetPictureHdr(Value("mode"));
                }
                break;
            case "stereo":
                switch (Value("mode"))
                {
                    case "auto": StereoAuto = true; DetectStereo(announce: true); break;
                    case "off": SetStereo(StereoMode.None, manual: true, "3D: nativo"); break;
                    case "sbs": SetStereo(StereoMode.Sbs, manual: true, "3D: Side-by-side → 2D"); break;
                    case "tab": SetStereo(StereoMode.Tab, manual: true, "3D: Top/Bottom → 2D"); break;
                }
                break;
            case "renderer_current": case "renderer_default": ChooseRemoteRenderer(Value("value"), cmd == "renderer_default"); break;
            case "upscaling": case "upscaling_profile": SetRemoteUpscaling(Value("backend"), Value("preset")); break;
            case "audio_track":
                if (playing && int.TryParse(Value("id"), out int ai) && _tracks.Any(t => t.Group == 1 && t.Index == ai))
                    SelectAudioTrack(ai);
                break;
            case "subtitle_track":
                if (playing && int.TryParse(Value("id"), out int si) && (si == -1 || _tracks.Any(t => t.Group == 2 && t.Index == si)))
                {
                    if (si < 0) { _externalSubtitle = null; _video.Send("subsoff"); }
                    else { _externalSubtitle = null; _video.Send($"select {si} sub"); }
                }
                break;
            case "subtitle_download": if (_file is { } video && !IsStream(video)) { _subtitleSearch.OverVideo = _hud.VideoVisible; _subtitleSearch.Open(video, _hud.Title, _hud.Duration > 0 ? _hud.Duration : null); } break;
            case "subtitle_realign": RealignExternalSubtitle(); break;
            case "library_source":
                _remoteLibraryNetwork = q.TryGetValue("value", out var source) && source == "network";
                _remoteLibraryAt = 0;
                break;
            case "open":
                if (q.TryGetValue("url", out var url) && !string.IsNullOrWhiteSpace(url))
                {
                    q.TryGetValue("title", out var title);
                    var item = _library.RemoteItems(_remoteLibraryNetwork).FirstOrDefault(i => i.Path == url);
                    if (item?.Category == "Music") { ShowLibrary(true); _library.PlayMusicFromRemote(item); }
                    else
                    {
                        _spotlight.Close(); _videoQueue.Clear(); _library.PauseMusic(); ShowLibrary(false);
                        StartVideo(url, item?.Title ?? title);
                        double? resume = item != null ? _library.ResumeFor(item)?.Position : null;
                        _resumeOnReady = resume > 1 ? resume : null;
                    }
                }
                break;
            // Frecce, OK e Indietro: come la tastiera sulla schermata che c'e' (libreria, Spotlight, player).
            case "left": case "right": case "up": case "down": case "ok": case "back":
                // Nei menu dei DVD "Indietro" torna al menu precedente del disco.
                if (cmd == "back" && _video.ActiveDvdNav is { InMenu: true } backNav) { backNav.GoUp(); break; }
                var key = cmd switch { "left" => Key.Left, "right" => Key.Right, "up" => Key.Up, "down" => Key.Down, "ok" => Key.Enter, _ => Key.Escape };
                OnKey(this, new KeyEventArgs { Key = key, RoutedEvent = KeyDownEvent });
                break;
            case "home": _spotlight.Close(); ShowLibrary(true); break;
            // Schermo: spostamento adesso e schermo dell'avvio ("" ultima posizione, "active" quello del mouse).
            case "screen": MoveToScreen(Value("key")); break;
            case "start_screen": StartScreen = Value("key"); break;
            // Trackpad del telefono: il puntatore del PC (movimento relativo, clic, rotella).
            // Libreria del telefono: coda, preferiti, playlist ("paths" = elenco JSON dei percorsi).
            case "queue_add": RemoteQueueAdd(RemotePaths(Value("paths"), Value("path"))); break;
            case "favorite":
                foreach (string path in RemotePaths(Value("paths"), Value("path"))) Favorites.Toggle(path);
                _library.InvalidateVisual();
                break;
            case "playlist_add": _osd.Show(_library.RemoteAddToPlaylist(Value("key"), RemotePaths(Value("paths"), Value("path"))), 2200); break;
            case "pointer_move": RemotePointer.Move(Num("dx"), Num("dy")); break;
            case "pointer_click": RemotePointer.Click(Value("button") == "right", Value("double") == "1"); break;
            case "pointer_scroll": RemotePointer.Scroll(Num("dy")); break;
            case "spotlight": if (_spotlight.IsOpen) _spotlight.Close(); else OpenSpotlight(); break;
            // Lingua dell'interfaccia dal telefono (come Windows: italiano -> inglese -> spagnolo).
            case "language": L10n.Set(Value("value") is "en" or "es" ? Value("value") : "it"); break;
            // Vecchi client del telefono: libreria (con la sezione) e sottotitoli acceso/spento.
            case "library":
                ShowLibrary(true);
                if (Value("category") is { Length: > 0 } category) _library.TestNavigate(category);
                break;
            case "subtitles":
                if (playing)
                {
                    var subs = _tracks.Where(t => t.Group == 2).ToList();
                    if (subs.Any(t => t.Selected) || _externalSubtitle != null) { _externalSubtitle = null; _video.Send("subsoff"); }
                    else if (subs.Count > 0) _video.Send($"select {subs[0].Index} sub");
                }
                break;
            case "theme":
                if (Value("mode") is "light" or "dark")
                {
                    MacSettings.ThemeMode = Value("mode"); MacSettings.Background = ""; MacSettings.Accent = "";
                    ApplySavedTheme();
                }
                break;
            case "theme_color":
                if (Value("value").Length == 7 && Color.TryParse(Value("value"), out var color))
                {
                    if (Value("key") == "accent") { MacSettings.Accent = color.ToString(); UiTheme.SetAccent(color); }
                    else if (Value("key") == "background") { MacSettings.Background = color.ToString(); UiTheme.ApplyBackground(color); }
                }
                break;
            case "text":
                if (_settings.IsOpen || _pairing.IsVisible) break;
                if (FocusManager?.GetFocusedElement() is TextBox { PasswordChar: '\0', IsReadOnly: false } box)
                    box.Text = Value("value");
                else if (_spotlight.IsOpen) _spotlight.ApplyRemoteSearch(Value("value"));
                else if (_library.IsVisible && !_settings.IsOpen) _library.ApplyRemoteSearch(Value("value"));
                break;
            case "keyenter":
                if (_settings.IsOpen || _pairing.IsVisible) break;
                if (FocusManager?.GetFocusedElement() is TextBox { PasswordChar: '\0' } field)
                    field.RaiseEvent(new KeyEventArgs { Key = Key.Enter, RoutedEvent = KeyDownEvent });
                else if (FocusManager?.GetFocusedElement() is not TextBox)
                    OnKey(this, new KeyEventArgs { Key = Key.Enter, RoutedEvent = KeyDownEvent });
                break;
        }
    }

    private List<(double Start, string Title)> RemoteChapters()
    {
        if (_file == null) return new();
        if (_video.ActiveDvdNav is not { } dvd) return _probe?.Chapters ?? new();
        return dvd.InMenu ? new() : dvd.Chapters().Select((time, i) => (time, $"Capitolo {i + 1}")).ToList();
    }

    private void PreviewRemotePosition(double seconds)
    {
        if (_hud.Duration <= 0 || _probe?.HasVideo != true || _file == null || IsStream(_file)) return;
        StopScan();
        _timelinePreview.SetSource(_file, _probe, _hud.Duration);
        _timelinePreview.Request(Math.Clamp(seconds, 0, _hud.Duration), _hud.Duration);
        _hud.Poke();
        int version = ++_remoteScrubVersion;
        DispatcherTimer.RunOnce(() => { if (version == _remoteScrubVersion) _timelinePreview.Dismiss(); }, TimeSpan.FromSeconds(2.5));
    }

    private int _remoteScrubVersion;

    private static string[] AvailableRemoteRenderers()
    {
        var choices = new List<string> { "Auto" };
        if (VideoHostClient.WineAvailable) choices.AddRange(new[] { "MPCVR", "MADVR" });
        if (MpvEngine.Available) choices.Add("MPV");
        return choices.ToArray();
    }

    private void ChooseRemoteRenderer(string value, bool save)
    {
        var choice = (_remoteRenderers ??= AvailableRemoteRenderers()).FirstOrDefault(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (choice == null) return;
        if (save)
        {
            MacSettings.ChooseRenderer(choice);
            if (_file == null) SetRenderer(MacSettings.EffectiveRenderer);
        }
        else
        {
            _hud.HideRendererPrompt();
            string renderer = choice == "Auto" ? VideoHostClient.WineAvailable ? "madvr" : "mpv" : choice.ToLowerInvariant();
            if (_file != null) _remotePlaybackRenderer = renderer;
            SetRenderer(renderer);
        }
    }

    private void SetRemoteUpscaling(string backend, string preset = "")
    {
        if (backend == "mpcvr")
        {
            // NVIDIA RTX Video Super Resolution con MPC-VR (preset = soglia di MPC-VR, 1 SD … 4 1440p; 3 = fino a 1080p).
            if (!RtxAvailable) return;
            ComponentSettings.RtxSuperResolution = int.TryParse(preset, out int level) && level is >= 1 and <= 4 ? level : 3;
            ApplyMpcVrRtx();
            return;
        }
        if (backend is not ("madvr" or "off") || (backend == "madvr" && !VideoHostClient.WineAvailable)) return;
        bool enable = backend == "madvr";
        bool hadRtx = ComponentSettings.RtxSuperResolution > 0;
        ComponentSettings.RtxSuperResolution = 0;
        MacSettings.Set("upscaling", enable);
        if (enable && _renderer != "madvr") ChooseRemoteRenderer("madvr", save: false);
        else if (hadRtx && _renderer == "mpcvr" && _file != null) SetRenderer("mpcvr", restart: true);
    }

    /// <summary>RTX HDR scelto (o tolto) dal telecomando: vale per MPC-VR, che lo legge all'apertura.</summary>
    private void SetRemoteRtxHdr(bool on)
    {
        if (on && !RtxAvailable) return;
        bool was = ComponentSettings.RtxVideoHdr;
        ComponentSettings.RtxVideoHdr = on;
        if (on) ApplyMpcVrRtx();
        else if (was && _renderer == "mpcvr" && _file != null) SetRenderer("mpcvr", restart: true);
    }

    /// <summary>Le funzioni RTX sono di MPC-VR: si passa a MPC-VR (o si riapre il film, che le legge all'apertura).</summary>
    private void ApplyMpcVrRtx()
    {
        if (_file == null) return;
        if (_renderer != "mpcvr") ChooseRemoteRenderer("mpcvr", save: false);
        else SetRenderer("mpcvr", restart: true);
    }

    private static bool? _rtxGpu;

    /// <summary>GPU NVIDIA RTX (driver video con "RTX" nel nome) e MPC-VR disponibile: le due funzioni dei driver NVIDIA.</summary>
    private static bool RtxAvailable
    {
        get
        {
#if CINECORE_WINDOWS
            if (!VideoHostClient.WineAvailable) return false;
            return _rtxGpu ??= DetectRtxGpu();
#else
            return false;
#endif
        }
    }

#if CINECORE_WINDOWS
    private static bool DetectRtxGpu()
    {
        try
        {
            // Classe "Display" dei driver: DriverDesc e' il nome della scheda (es. "NVIDIA GeForce RTX 4080").
            using var display = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display == null) return false;
            foreach (string sub in display.GetSubKeyNames())
            {
                try
                {
                    using var adapter = display.OpenSubKey(sub);
                    if (adapter?.GetValue("DriverDesc") is string name && name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("RTX", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { } // "Properties" e simili non si aprono senza privilegi
            }
        }
        catch { }
        return false;
    }
#endif

    private void RefreshRemoteOutputs()
    {
        long now = Environment.TickCount64;
        if (_remoteOutputAt != 0 && now - _remoteOutputAt < 5000) return;
        _remoteOutputAt = now;
        _remoteOutputs = MacAudioDevices.Outputs().GroupBy(d => d.Name)
            .SelectMany(g => g.Select((d, i) => (d.Uid, g.Count() == 1 ? d.Name : $"{d.Name} ({i + 1})"))).ToList();
    }
}

/// <summary>
/// Codice QR a tutta finestra su una vignettatura scura, con la X in alto a destra (ShowReadyPairingBanner di Windows):
/// entra in dissolvenza in 260 ms; un clic o Esc chiude.
/// </summary>
internal sealed class PairingView : Control
{
    private QrCode? _code;
    private DateTime _shownAt;
    private readonly DispatcherTimer _fade;
    private Point _mouse;
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }

    public PairingView()
    {
        IsVisible = false; Focusable = true;
        _fade = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) =>
        {
            InvalidateVisual();
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds > 280) _fade!.Stop();
        });
    }

    public void Show(QrCode code, string url, string pin)
    {
        if (IsVisible) return;
        _code = code; _shownAt = DateTime.UtcNow;
        IsVisible = true; Focus(); _fade.Start(); InvalidateVisual();
    }

    public void Hide() { IsVisible = false; _fade.Stop(); }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        if (_code is not { } code) return;
        var page = new Rect(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _shownAt).TotalMilliseconds / 260, 0, 1);
        using var fade = ctx.PushOpacity(1 - Math.Pow(1 - t, 3));
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), page);
        // Vignettatura: quasi nera dietro il codice, piu' leggera verso i bordi.
        double reach = Math.Min(page.Width, page.Height) * .62;
        ctx.FillRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(.5, .5, RelativeUnit.Relative), GradientOrigin = new RelativePoint(.5, .5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(reach, RelativeUnit.Absolute), RadiusY = new RelativeScalar(reach, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromArgb(246, 0, 0, 0), .46), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
        }, page);
        // Il codice: moduli bianchi direttamente sulla vignettatura, a pixel interi.
        double side = Math.Min(page.Width, page.Height) * .5;
        double module = Math.Max(2, Math.Floor(side / code.Size));
        side = module * code.Size;
        double left = Math.Round((page.Width - side) / 2), top = Math.Round((page.Height - side) / 2);
        for (int y = 0; y < code.Size; y++)
            for (int x = 0; x < code.Size; x++)
                if (code[x, y]) ctx.FillRectangle(Brushes.White, new Rect(left + x * module, top + y * module, module, module));
        // La X di Windows (DrawCloseButton): solo i due bracci, piu' chiari al passaggio.
        var close = new Rect(page.Width - 28 - 40, 28, 40, 40);
        double arm = Math.Max(8, 40 * .42) / 2, stroke = Math.Max(1.7, 40 * .062);
        var pen = new Pen(new SolidColorBrush(Hot(close) ? Colors.White : Color.FromArgb(165, 255, 255, 255)), stroke, lineCap: PenLineCap.Round);
        ctx.DrawLine(pen, new Point(close.Center.X - arm, close.Center.Y - arm), new Point(close.Center.X + arm, close.Center.Y + arm));
        ctx.DrawLine(pen, new Point(close.Center.X + arm, close.Center.Y - arm), new Point(close.Center.X - arm, close.Center.Y + arm));
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); e.Handled = true; }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); e.Handled = true; Hide(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape) { Hide(); e.Handled = true; } }
}

public sealed partial class MainWindow
{
    private static readonly string[] RemoteComponentOrder = { "MPV", "madVR", "LAV Video", "LAV Audio", "MPC Video Renderer", "Sottotitoli" };

    private object RemoteComponentSettings()
    {
        var providers = RemoteComponentOrder.Where(t => ComponentSettings.Tabs.Contains(t)).Select(tab => (object)new
        {
            id = tab,
            title = tab,
            note = L10n.T(ComponentSettings.Status(tab)),
            available = true,
            sections = ComponentSettings.Sections(tab).Select(section => (object)new
            {
                group = L10n.T(section.Group),
                title = L10n.T(section.Title),
                settings = section.Settings.Select(setting => new
                {
                    key = setting.Key,
                    label = L10n.T(setting.Label),
                    description = L10n.T(setting.Description),
                    kind = setting.Kind == ComponentSettings.Kind.Toggle ? "toggle" : "choice",
                    action = (string?)null,
                    reload = false,
                    value = ComponentSettings.GetStored(tab, setting),
                    choices = setting.Choices?.Select(c => new { value = c.Value, label = L10n.T(c.Label) }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        }).ToList();
        // Le schede del player (Generali, Dispositivi, Cinecore Audio) prima dei componenti: tutte le voci vere.
        foreach (string tab in SettingsView.RemoteTabs.Reverse())
            try { providers.Insert(0, _settings.RemoteProvider(tab)); }
            catch (Exception ex) { Console.Error.WriteLine($"[telecomando] scheda {tab}: {ex.Message}"); }
        return new { language = L10n.Language, providers };
    }

    private object ApplyRemoteComponentSetting(string tab, string key, string value)
    {
        if (SettingsView.RemoteTabs.Contains(tab)) return _settings.RemoteApply(tab, key, value);
        var setting = ComponentSettings.Sections(tab).SelectMany(s => s.Settings).FirstOrDefault(s => s.Key == key);
        if (setting == null) return new { ok = false, error = "unknown setting" };
        if (setting.Kind == ComponentSettings.Kind.Toggle) value = value == "1" ? "1" : "0";
        else if (setting.Choices is { Length: > 0 } choices && !choices.Any(c => c.Value == value))
            return new { ok = false, value = ComponentSettings.GetStored(tab, setting), error = L10n.T("Valore non accettato.") };
        foreach (string command in ComponentSettings.Set(tab, setting, value).ToList())
            if (_renderer == "madvr") _video.Send(command);
        _settings.InvalidateVisual();
        return new { ok = true, value = ComponentSettings.GetStored(tab, setting), error = (string?)null };
    }

    private static List<string> RemotePaths(string json, string single)
    {
        var list = new List<string>();
        if (json.Length > 0)
            try { list.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new()); } catch { }
        if (single.Length > 0) list.Add(single);
        return list.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"In coda" dal telefono: i brani nella coda della musica, film ed episodi dopo quello in corso
    /// (senza niente in corso parte il primo).</summary>
    private void RemoteQueueAdd(List<string> paths)
    {
        var items = paths.Select(p => _library.ItemFor(p) ?? new LibraryItem { Path = p, Title = Path.GetFileNameWithoutExtension(p) }).ToList();
        if (items.Count == 0) return;
        var music = items.Where(i => i.Category == "Music" || (!LibraryStore.IsVideoPath(i.Path) && !i.Path.Contains("://", StringComparison.Ordinal))).ToList();
        if (music.Count > 0) _library.RemoteAddMusicToQueue(music);
        var video = items.Except(music).ToList();
        if (video.Count == 0) return;
        if (_video.IsRunning && _file != null)
        {
            foreach (var v in video) if (_videoQueue.All(q => q.Path != v.Path)) _videoQueue.Add(v);
            _osd.Show(video.Count == 1 ? "Aggiunto alla coda: " + video[0].Title : $"{video.Count} titoli aggiunti alla coda", 2200);
        }
        else
        {
            _videoQueue = video.Skip(1).ToList();
            _library.PauseMusic(); ShowLibrary(false); StartVideo(video[0].Path, video[0].Title);
        }
    }
}
