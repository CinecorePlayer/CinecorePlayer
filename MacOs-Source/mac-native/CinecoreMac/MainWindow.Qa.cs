using System;
using System.Linq;
using Avalonia;
using Avalonia.Threading;

namespace CinecoreMac;

public sealed partial class MainWindow
{
    // Prove della finestra reale senza input simulato o puntatore dell'utente.
    // CINECORE_QA_SCRIPT="ms:azione:argomento;…". Inattivo negli avvii normali.
    private void RunQaScript()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") is not { Length: > 0 } script) return;
        Console.Error.WriteLine($"[qa] begin utc-ms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        NativeDiagnostics.WatchDispatcher();
        foreach (string step in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = step.Split(':', 3);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int milliseconds) || milliseconds < 0) continue;
            string action = parts[1], argument = parts.Length == 3 ? parts[2] : "";
            DispatcherTimer.RunOnce(() =>
            {
                if (_closing) return;
                try
                {
                    switch (action)
                    {
                        case "state":
                            Console.Error.WriteLine($"[qa] nativeFullscreen={MacWindowChrome.IsNativeFullscreen(this)} resumeCards={_library.QaVisibleResumeCards} topChromeInset={_hud.TopChromeInset:0}");
                            if (IsFullscreen) MacWindowChrome.VisibleFullscreenTitlebarBottom(this, diagnostics: true);
                            break;
                        case "open": OpenFile(argument); break;
                        case "subtitle": LoadSubtitle(argument); break;
                        case "pause": _video.Pause(); break;
                        case "send": _video.Send(argument); break;
                        case "vignette-dump":
                            foreach (var k in new[] { Vignette.Kind.Normal, Vignette.Kind.Cast })
                                Vignette.Get(k, new PixelSize(400, 240), () => { })?.Save(System.IO.Path.Combine(argument, k + ".png"));
                            break;
                        case "step": FrameStep(argument == "-1" ? -1 : 1); break;
                        case "chapter": JumpChapter(argument == "-1" ? -1 : 1); break;
                        case "play": _video.Play(); break;
                        case "seek": SeekTo(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)); break;
                        case "closefile": CloseFile(); break;
                        case "fullscreen": ToggleFullscreen(); break;
                        case "cast": ToggleCast(); Console.Error.WriteLine("[qa] cast available=" + _hud.CastAvailable + " open=" + _hud.CastOpen + " count=" + _castList.Count); break;
                        case "cast-fake":
                            // Solo per le prove (server spento): interpreti di esempio, uno con fotografia.
                            string? photo = System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(LibraryStore.AppDataDir, "posters")).FirstOrDefault();
                            _castList = new() { ("Matthew McConaughey", "Cooper", photo), ("Anne Hathaway", "Brand", null), ("Jessica Chastain", "Murph", photo), ("Michael Caine", "Professor Brand", null), ("Casey Affleck", "Tom", null), ("Mackenzie Foy", "Murph da bambina", photo), ("Matt Damon", "Mann", null), ("Wes Bentley", "Doyle", null), ("David Gyasi", "Romilly", null) };
                            _hud.CastAvailable = true; _hud.InvalidateVisual(); break;
                        case "cast-move": _hud.MoveCast(argument == "-1" ? -1 : 1); break;
                        case "quality": ShowQualityMenu(new Point(Bounds.Width - 60, 60)); break;
                        case "quality-state":
                            static string Describe(MenuEntry entry) =>
                                entry.Text + (entry.Checked ? "*" : "") +
                                (entry.Children is { Count: > 0 } children ? " [" + string.Join(", ", children.Select(Describe)) + "]" : "");
                            Console.Error.WriteLine($"[qa] quality position={_hud.Position:0.00} playing={_hud.IsPlaying} height={_probe?.Height} label={_hud.QualityLabel} search={_library.SearchSurfaceState} choices={string.Join(" | ", (CurrentStreamQualityEntries() ?? new()).Select(Describe))}");
                            break;
                        case "hudpoke": _hud.Poke(); break;
                        // Prove: finestra dell'app trasparente del tutto (quanto costa a WindowServer comporla sul video).
                        case "overlay-alpha": MacWindowChrome.SetWindowAlpha(this, double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)); break;
                        case "prompts": _introOutro = new CinecorePlayer2025.Utilities.IntroOutroEpisodeMarkers { Intro = new() { StartSeconds = 0, EndSeconds = 30 }, Outro = new() { StartSeconds = 0, EndSeconds = 45 } }; _videoQueue.Add(new LibraryItem { Path = _file ?? "", Title = "Episodio dopo" }); break;
                        case "renderer": SetRenderer(argument); break;
                        case "windows": Console.Error.WriteLine("[qa] windows renderer=" + _renderer + " running=" + _video.IsRunning + " scalaWine=" + WineScale() + " finestra=" + RenderScaling + " schermi=" + string.Join(",", Screens.All.Select(sc => sc.Bounds + "@" + sc.Scaling)) + " rect=" + VideoRect()); break;
                        case "menu": ShowContextMenu(new Point(160, 100)); break;
                        // Prove: tutte le voci del menu, a ogni livello, passate dalla traduzione (con CINECORE_QA_L10N scrive le mancanti).
                        case "menu-dump":
                        {
                            void Walk(System.Collections.Generic.IEnumerable<MenuEntry> list, string path)
                            {
                                foreach (var e in list)
                                {
                                    if (e.Separator || e.Text.Length == 0) continue;
                                    Console.Error.WriteLine("[menu-voce] " + path + e.Text + " => " + L10n.T(e.Text) + (e.Value is { Length: > 0 } v ? "  [" + L10n.T(v) + "]" : ""));
                                    if (e.Children != null) Walk(e.Children, path + e.Text + " › ");
                                }
                            }
                            Walk(BuildMenu(), "");
                            break;
                        }
                        case "menu-parts":
                        {
                            void T(string n, Action a) { var w = System.Diagnostics.Stopwatch.StartNew(); try { a(); } catch (Exception ex) { Console.Error.WriteLine(n + " errore " + ex.Message); } Console.Error.WriteLine($"[menu-parti] {n} {w.Elapsed.TotalMilliseconds:0.0} ms"); }
                            for (int round = 0; round < 2; round++)
                            {
                                T("apri-disco", () => OpenDiscMenu());
                                T("uscite-audio", () => AudioOutputMenu());
                                T("sottotitoli", () => BuildSubtitlesMenu(false));
                                T("ip", () => RemoteServer.LocalIPv4List().ToList());
                                T("wine", () => _ = VideoHostClient.WineAvailable);
                                T("mpv", () => _ = MpvEngine.Available);
                                T("jellyfin", () => _ = CinecorePlayer2025.Utilities.JellyfinClient.Accounts.Count);
                                T("streaming", () => CurrentStreamQualityEntries());
                                T("dvd", () => DvdMenuEntry());
                                T("titoli-disco", () => DiscTitlesMenu());
                                T("prima-del-film", () => BeforeFilmMenu());
                                T("hdr", () => HdrCandidate());
                                T("tutto", () => BuildMenu());
                            }
                            break;
                        }
                        case "lib-menu": { var xy = argument.Split(','); OpenLibraryMenu(new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture))); break; }
                        case "menu-at": { var xy = argument.Split(','); ShowContextMenu(new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture))); break; }
                        case "submenu": foreach (string label in argument.Split('|')) _menu.TestOpenSubmenu(label); break;
                        case "choose": Console.Error.WriteLine("[qa] choose=" + _menu.TestActivatePath(argument)); break;
                        case "library": ShowLibrary(true); _library.TestNavigate(argument.Length == 0 ? "Movies" : argument); break;
                        case "mouse": { var xy = argument.Split(','); _library.TestMouse(new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture))); break; }
                        case "settings-mouse": { var xy = argument.Split(','); _settings.TestMouse(new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture))); break; }
                        case "settings-mouse-stats": Console.Error.WriteLine("[qa] ridisegni impostazioni da mouse=" + _settings.QaInvalidations); break;
                        case "hover-probe":
                            {
                                // hover-probe:libreria|impostazioni:x,y
                                var parts2 = argument.Split(':'); var xy = parts2[1].Split(',');
                                var at = new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
                                var (changed, differs) = parts2[0] == "impostazioni" ? _settings.QaHoverProbe(at) : _library.QaHoverProbe(at);
                                Console.Error.WriteLine($"[qa] hover-probe {parts2[1]} zone={changed} disegno={differs}" + (differs && !changed ? " MANCATO" : changed && !differs ? " superfluo" : ""));
                            }
                            break;
                        case "mouse-stats": Console.Error.WriteLine("[qa] ridisegni da mouse=" + _library.QaInvalidations); break;
                        case "scroll": _library.ScrollCollection(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)); break;
                        case "fixture-home": ShowLibrary(true); _library.TestHome(); break;
                        case "hero-next": _library.TestNextHero(); break;
                        case "fixture": ShowLibrary(true); _library.TestArtworkGrid(); break;
                        case "metadata": _library.TestEditFirst(); break;
                        case "metadata-test": _metadata.Open(new LibraryItem { Path = argument, Title = "Verifica metadati", Category = "Movies", Year = 2020 }); break;
                        case "metadata-close": _metadata.Close(); break;
                        case "metadata-fields":
                            var values = argument.Split('|'); _metadata.TestFields(values[0], values.Length > 1 ? values[1] : ""); break;
                        case "metadata-poster": _metadata.SetLocalPoster(argument); break;
                        case "metadata-save": _ = _metadata.TestSaveAsync(); break;
                        case "metadata-search": _ = _metadata.TestSearchAsync(); break;
                        case "metadata-status": Console.Error.WriteLine("[qa] " + _metadata.QaSummary); break;
                        case "settings": OpenSettings(argument.Length == 0 ? "Generali" : argument); break;
                        case "settings-preset": _settings.TestOpenPreset(argument); break;
                        case "spotlight-gear": _spotlight.TestSettings(); break;
                        case "settings-tab": _settings.TestOpenMenu(argument); break;
                        case "settings-scroll": _settings.TestScroll(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)); break;
                        case "settings-close": _settings.Close(); break;
                        case "color-picker": Console.Error.WriteLine("[qa] picker " + _settings.TestPicker(argument)); break;
                        case "keychain-status": Console.Error.WriteLine("[qa] keychain authorizationRequired=" + CinecorePlayer2025.Utilities.Keychain.AccessRequired + " authorizing=" + CinecorePlayer2025.Utilities.Keychain.Authorizing); break;
                        case "item-menu": _library.TestItemContextMenu(argument); break;
                        case "text-menu": Console.Error.WriteLine("[qa] testo " + _library.TestTextMenu(argument)); break;
                        case "closemenu": _menu.Close(); break;
                        case "info": _hud.OpenInfo(); break;
                        case "renderer-stats": ToggleRendererStats(); break;
                        case "info-close": _hud.CloseInfo(); break;
                        case "cast-close": _hud.CloseCast(); break;
                        case "preview": _hud.TestPreview(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)); break;
                        case "preview-close": _timelinePreview.Dismiss(); _hud.ResetPanels(); break;
                        case "size":
                            var dimensions = argument.Split(',');
                            Width = int.Parse(dimensions[0]); Height = int.Parse(dimensions[1]);
                            break;
                        case "net-discover":
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                await NetworkStore.DiscoverAsync(TimeSpan.FromSeconds(4));
                                foreach (var server in NetworkStore.Servers)
                                    Console.Error.WriteLine($"[qa] server {server.Protocol} | {server.Name} | {server.Model} | {server.Host} | disponibile={server.Available} | cd={(server.ContentDirectoryControlUrl.Length > 0)}");
                                foreach (var server in NetworkStore.Servers.Where(s => !s.IsJellyfin && s.Available).Take(2))
                                {
                                    try
                                    {
                                        var items = await NetworkStore.LoadCatalogAsync(server, null, new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
                                        Console.Error.WriteLine($"[qa] dlna {server.Name}: {items.Count} voci; " + string.Join(", ", items.GroupBy(i => i.Category).Select(g => g.Key + "=" + g.Count())));
                                        foreach (var item in items.Take(5)) Console.Error.WriteLine($"[qa]   {item.Category} | {item.Title} | art={(item.ArtPath != null)}");
                                    }
                                    catch (Exception ex) { Console.Error.WriteLine("[qa] dlna errore " + ex); }
                                }
                            });
                            break;
                        case "onboarding": _library.StartOnboarding(); break;
                        case "album-first": _library.TestOpenFirstAlbum(argument == "play"); break;
                        case "music-ui": _library.TestMusicUi(argument); break;
                        case "music-file": _library.TestPlayMusicFile(argument); break;
                        case "music-files": _library.TestPlayMusicFiles(argument); break;
                        case "color-panel": Console.Error.WriteLine("[qa] colori disponibile=" + MacColorPanel.Available + " aperto=" + MacColorPanel.Show(Avalonia.Media.Color.Parse("#336699"), "Prova", c => Console.Error.WriteLine("[qa] colore scelto " + c)) + " lettura=" + MacColorPanel.TestRoundTrip(Avalonia.Media.Color.Parse("#c08040"))); break;
                        case "vol-up":
                            if (!TryStepVolumeBoost(+1)) { _volume = Math.Min(1, _volume + .05f); _hud.Volume = _volume; ApplyVolume(); }
                            Console.Error.WriteLine($"[qa] volume {_volume:0.00} amplificazione +{_volumeBoostDb} dB motore={Engine} audio nativo={_video.NativeAudioState}");
                            break;
                        case "vol-down":
                            if (!TryStepVolumeBoost(-1)) { _volume = Math.Max(0, _volume - .05f); _hud.Volume = _volume; ApplyVolume(); }
                            Console.Error.WriteLine($"[qa] volume {_volume:0.00} amplificazione +{_volumeBoostDb} dB motore={Engine} audio nativo={_video.NativeAudioState}");
                            break;
                        case "engine-install": MacEngine.StartInstall(); break;
                        case "engine-state": Console.Error.WriteLine($"[qa] motore dev={MacEngine.Dev} installato={MacEngine.Installed} versione={MacEngine.InstalledVersion ?? "-"} occupato={MacEngine.Busy} stato={MacEngine.Status} wine={VideoHostClient.WineAvailable} host={MacEngine.VideoHostExe}"); break;
                        case "storage": Console.Error.WriteLine("[qa] montaggi: " + MacStorage.Describe() + " | lento(" + argument + ")=" + MacStorage.IsSlowMount(argument)); break;
                        case "eq-drag": Console.Error.WriteLine("[qa] eq " + _settings.TestEqDrag(argument)); break;
                        case "album-open": _library.TestOpenAlbum(argument); break;
                        case "recommendations-state": Console.Error.WriteLine("[qa] recommendations " + _library.RecommendationsState); break;
                        case "amp-state": Console.Error.WriteLine("[qa] ricevitore " + AmplifierQa); break;
                        case "amp-vol": Console.Error.WriteLine("[qa] ricevitore gesto=" + TryAmplifierVolume(value: float.Parse(argument, System.Globalization.CultureInfo.InvariantCulture))); break;
                        case "amp-step": Console.Error.WriteLine("[qa] ricevitore passo=" + TryAmplifierVolume(step: int.Parse(argument))); break;
                        case "window-at-mouse": Console.Error.WriteLine("[qa] finestra sotto il puntatore: " + (MacGlass.WindowOwnerAtMouse() is { } w ? w.Pid + " " + w.Owner + " (giocatore=" + PointerOverPlayerWindow() + ")" : "nessuna")); break;
                        case "media-key": MacMediaKeys.TestCommand(argument); break;
                        case "lang": L10n.Set(argument); break;
                        case "cast-demo": _library.TestCastDemo(); break;
                        case "director-demo": { ShowLibrary(true); var dd = argument.Split('|'); _library.TestDirectorDemo(int.Parse(dd[0], System.Globalization.CultureInfo.InvariantCulture), dd[1]); break; }
                        case "auto-open": _automations.Open(); break;
                        case "auto-new": { var a = argument.Split('|'); _automations.QaNew(a[0], a[1], a.Length > 2 ? a[2] : "", a.Length > 3 ? a[3] : "", a.Length > 4 && a[4] == "musica"); break; }
                        case "auto-test": _automations.QaTest(); break;
                        case "auto-save": _automations.QaSave(); break;
                        case "auto-clear": _automations.QaDeleteAll(); break;
                        case "auto-state": Console.Error.WriteLine("[qa] automazioni " + _automations.QaState); break;
                        case "album-play": _library.TestPlayOpenAlbum(int.Parse(argument)); break;
                        case "mix-open": _library.TestManualMix(); break;
                        case "mix-state": Console.Error.WriteLine("[qa] mix " + _manualMix.QaState + " salvato=" + (CinecorePlayer2025.Audio.ManualMixStore.Get(_library.TestMusicPaths.From, _library.TestMusicPaths.To) is { } savedMix ? savedMix.ToString() : "-")); break;
                        case "mix-tip": { var t = argument.Split(','); _manualMix.QaTip(t[0], double.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(t[2], System.Globalization.CultureInfo.InvariantCulture)); break; }
                        case "mix-close": _manualMix.QaClose(); break;
                        case "mix-preset": _manualMix.QaPreset(int.Parse(argument)); break;
                        case "mix-preview": _manualMix.QaPreview(argument); break;
                        case "mix-speed": _manualMix.QaSpeed(argument); break;
                        case "mix-outspeed": _manualMix.QaOutSpeed(argument); break;
                        case "mix-lane": _manualMix.QaLane(argument); break;
                        case "mix-overview":
                            var scrub = argument.Split('|');
                            _manualMix.QaOverview(scrub[0], double.Parse(scrub[1], System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        case "mix-save": _manualMix.QaSave(); break;
                        case "mix-seek": _library.TestMusicSeekBeforeMix(); break;
                        case "cd-open": OpenAudioCd(argument); break;
                        case "lib-search": _library.TestSearch(argument); break;
                        case "spot-search": _spotlight.TestSearch(argument); break;
                        case "spot-step": _spotlight.TestStep(int.Parse(argument)); break;
                        case "open-link": _web.Open(); break;
                        case "diary": _library.TestDiary(argument); break;
                        // Come Audio › Uscita audio del menu (argomento: UID del dispositivo, vuoto = predefinito del Mac).
                        case "yt-quality": if (IsYouTubeVideo) OpenWebAddress(_webPage!, _titleOverride, resume: true, requestedHeight: int.Parse(argument)); break;
                        case "hud-library": _hud.TestLibraryButton(); break;
                        case "replace-stream": RestartStream(argument, null, _hud.Position, !_hud.IsPlaying); break;
                        case "bitstream": SetBitstream(argument == "1"); break;
                        case "audio-out": MacSettings.AudioDeviceUid = argument; MacSettings.AudioDeviceName = argument; _library.AudioOutputChanged(); if (_file != null) { bool live = _video.ChangeAudioDevice(); Console.Error.WriteLine("[qa] audio-out dal vivo=" + live); if (!live) ReopenSame(); } break;
                        case "audio-devices": foreach (var dev in MacAudioDevices.Outputs()) Console.Error.WriteLine($"[qa] uscita {dev.Uid} | {dev.Name} | predefinita={dev.IsDefault} | attiva={MacAudioDevices.IsRunning(dev.Uid)}"); break;
                        case "report": _report.Open(LibraryReportView.CollectSources()); break;
                        case "report-state": Console.Error.WriteLine("[qa] report " + _report.QaSummary); break;
                        case "audiosync": OpenAudioSync(); break;
                        case "audiosync-analyze": _audioSync.TestAnalyze(); break;
                        case "audiosync-apply": _audioSync.TestApply(); break;
                        case "audiosync-ext": _audioSync.TestExternal(argument); break;
                        case "audiosync-convert": _audioSync.TestConvert(); break;
                        case "audiosync-state": Console.Error.WriteLine("[qa] audiosync " + _audioSync.QaSummary + " offset=" + _audioOffsetMs); break;
                        case "trakt": _trakt.Open(LibraryView.CollectTitlesForRecommendations()); break;
                        case "photos-open":
                        {
                            var shots = System.IO.Directory.GetFiles(argument, "*.jpg").OrderBy(f => f)
                                .Select(f => new LibraryItem { Path = f, Title = System.IO.Path.GetFileNameWithoutExtension(f), Category = "Photos", ArtPath = f, SortDateUtc = System.IO.File.GetLastWriteTimeUtc(f) }).ToList();
                            _photoViewer.Open(shots, 0);
                            break;
                        }
                        case "trakt-all": _trakt.QaShowAll(argument); break;
                        case "trakt-more": _trakt.QaMore(); break;
                        case "trakt-state": Console.Error.WriteLine("[qa] trakt " + _trakt.QaSummary); break;
                        case "scrobble": _scrobble.Open(); break;
                        case "update-latest": _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                try { var r = await MacUpdate.LatestAsync(System.Threading.CancellationToken.None); Console.Error.WriteLine("[qa] update latest=" + (r == null ? "nessuna con pacchetto" : r.Version + " " + r.Name + " " + r.Url)); }
                                catch (Exception ex) { Console.Error.WriteLine("[qa] update errore " + ex.Message); }
                                foreach (string tag in new[] { "beta-v0.1.0-Avalonia-MacOs", "beta-v0.4.0-Avalonia-Windows", "beta-v0.3.6", "mac-v0.1.1" })
                                    Console.Error.WriteLine("[qa] update tag " + tag + " mac=" + MacUpdate.IsMacTag(tag));
                            }); break;
                        case "update": if (argument == "fake") _update.Offer(new MacUpdate.Release(new Version(0, 3, 7), "Cinecore Player 0.3.7", "Novità di prova: correzioni e miglioramenti.", "https://example.invalid/x.zip", 1, new string((char)48, 64), null)); else _update.Open(); break;
                        case "scan": _ = System.Threading.Tasks.Task.Run(async () => { await LibraryStore.ScanAsync(argument); Avalonia.Threading.Dispatcher.UIThread.Post(_library.Reload); }); break;
                        case "review-read": _library.TestReadReview(int.Parse(argument)); break;
                        case "detail-view": ShowLibrary(true); _library.TestDetailView(argument); break;
                        case "gate-start": ConfirmCinemaGate(); break;
                        case "gate-cancel": CancelCinemaGate(); break;
                        case "hdr": if (argument.Length > 0) OpenHdrAnalysis(argument, null); else OpenHdrAnalysis(); break;
                        case "spawn-test":
                            _ = System.Threading.Tasks.Task.Run(() =>
                            {
                                var parts = argument.Split(',');
                                int perSecond = int.Parse(parts[0]); string exe = parts.Length > 1 ? parts[1] : "/usr/bin/true";
                                var end = DateTime.UtcNow.AddSeconds(25);
                                while (DateTime.UtcNow < end)
                                {
                                    using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })) p?.WaitForExit();
                                    System.Threading.Thread.Sleep(1000 / perSecond);
                                }
                            });
                            break;
                        case "components": _components.Open(); break;
                        case "hero-state": Console.Error.WriteLine("[qa] hero " + _library.QaHeroDeck); break;
                        // comp-set:Scheda|chiave|valore: come una scelta in Impostazioni > Componenti (madVR anche dal vivo).
                        case "comp-set":
                        {
                            var part = argument.Split('|', 3);
                            var setting = part.Length == 3 ? ComponentSettings.Sections(part[0]).SelectMany(x => x.Settings).FirstOrDefault(x => x.Key == part[1]) : null;
                            if (setting == null) { Console.Error.WriteLine("[qa] comp-set: voce non trovata " + argument); break; }
                            foreach (var command in ComponentSettings.Set(part[0], setting, part[2])) if (_renderer == "madvr") _video.Send(command);
                            Console.Error.WriteLine($"[qa] comp-set {part[0]} {part[1]}={part[2]}");
                            break;
                        }
                        case "genius-web":
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                string? page = await CinecorePlayer2025.Utilities.GeniusBrowserSession.GetTextAsync(argument, System.Threading.CancellationToken.None);
                                var m = page == null ? null : System.Text.RegularExpressions.Regex.Match(page, "<title>([^<]*)</title>");
                                Console.Error.WriteLine($"[qa-genius] disponibile={CinecorePlayer2025.Utilities.GeniusBrowserSession.Available} {sw.ElapsedMilliseconds} ms, {(page?.Length ?? 0)} caratteri, titolo: {m?.Groups[1].Value}");
                            });
                            break;
                        case "genius-verify": CinecorePlayer2025.Utilities.GeniusBrowserSession.ShowVerification(); break;
                        case "titles": foreach (string line in LibraryStore.QaTitles(argument)) Console.Error.WriteLine("[qa-titolo] " + line); break;
                        case "components-state": Console.Error.WriteLine("[qa] components " + _components.QaSummary); break;
                        case "loop": _loopFile = argument == "off" ? null : _file; break;
                        case "web": OpenWebAddress(argument); break;
                        case "web-sheet": _web.Open(); break;
                        case "wled-sheet": _wled.Open(); break;
                        case "lib-filters": _library.TestFilters(); break;
                        case "bar-click": Console.Error.WriteLine("[qa] bar-click " + argument + ": " + _library.TestClickBar(argument) + " | " + _library.TestMusicState); break;
                        case "signin": _library.TestSignIn(); break;
                        case "mini": ToggleMiniPlayer(); break;
                        case "photo-edit-tool": _photoEdit.QaTool(argument); break;
                        case "photo-editor": _photoEdit.Open(argument); break;
                        case "audio-formats":
                        {
                            // Sola lettura: formati fisici dell'uscita e scelta del bit perfect (nessun flusso aperto).
                            uint dev = MacAudioDevices.DeviceId(MacAudioDevices.ConfiguredOutputUid);
                            Console.Error.WriteLine($"[qa] uscita {dev}: adesso {MacAudioDevices.CurrentPhysicalFormat(dev)?.ToString() ?? "-"}");
                            foreach (var f in MacAudioDevices.AvailablePhysicalFormats(dev).Select(x => $"{x.Format} ({x.MinRate:0}-{x.MaxRate:0})").Distinct())
                                Console.Error.WriteLine("[qa]   offre " + f);
                            foreach (var (rate, bits) in new[] { (44100.0, 16), (44100.0, 24), (96000.0, 24) })
                                Console.Error.WriteLine($"[qa]   bit perfect {rate:0} Hz {bits} bit -> {MacAudioDevices.ChoosePhysicalFormat(dev, rate, bits)?.ToString() ?? "nessuno"}");
                            break;
                        }
                        case "disc-titles": { var m = DiscTitlesMenu(); Console.Error.WriteLine("[qa] titoli " + (m == null ? "nessun disco" : string.Join(" | ", m.Children!.Select(c => c.Text + "=" + c.Value + (c.Checked ? "*" : ""))))); break; }
                        case "fastscan": StepScan(int.Parse(argument), false); break;
                        case "stereo": SetStereo(Enum.Parse<StereoMode>(argument), manual: true, null); break;
                        case "genius-fetch":
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                string? text = await CinecorePlayer2025.Utilities.GeniusClient.GetTextAsync(argument, System.Threading.CancellationToken.None);
                                Console.Error.WriteLine($"[qa] genius {(text == null ? "nessun testo" : text.Length + " caratteri, testo=" + text.Contains("data-lyrics-container") + " json=" + text.TrimStart().StartsWith('{'))}");
                            });
                            break;
                        case "audio-caps": Console.Error.WriteLine("[qa] audio-caps " + string.Join(" | ", MacAudioDevices.Outputs().Select(d => d.Name + "=" + MacAudioDevices.SupportsCompressed(d.Uid) + " hdmi=" + MacAudioDevices.IsHdmi(d.Uid) + " passthrough=" + MacAudioDevices.PassthroughKey(d.Uid)))); break;
                        case "youtube": ShowLibrary(true); _library.OpenYouTube(); if (argument.Length > 0) _library.YouTubeSearch(argument); break;
                        case "youtube-state": Console.Error.WriteLine("[qa] youtube " + _library.YouTubeQa); break;
                        case "youtube-play": _library.TestYouTubeChoose(int.Parse(argument)); break;
                        case "sizing": _imageSizing.Mode = Enum.Parse<ImageSizingMode>(argument); ImageSizingChanged(); break;
                        case "sizing-state": Console.Error.WriteLine($"[qa] sizing modo={_imageSizing.Mode} scala={_sizingScale:0.###} formato={_sizingAspect?.Current:0.##} applica={ImageSizingApplies()}"); break;
                        case "audio-select": { var a = _tracks.Where(t => t.Group == 1).ElementAtOrDefault(int.Parse(argument)); if (a.Name != null) SelectAudioTrack(a.Index); break; }
                        case "audio-tracks": Console.Error.WriteLine("[qa] audio-tracks " + string.Join(" | ", _tracks.Where(t => t.Group == 1).Select(t => (t.Selected ? "*" : "") + t.Name)) + " esterno=" + (_video.ExternalAudio ?? "-")); break;
                        case "goto": OpenGoTo(); if (argument.Length > 0) _goTo.TestSubmit(argument); Console.Error.WriteLine($"[qa] goto '{argument}' -> {_hud.Position:0.0} aperta={_goTo.IsOpen}"); break;
                        case "renderer-prompt": Console.Error.WriteLine($"[qa] avviso motore visibile={_hud.RendererPromptVisible}"); if (argument.Length > 0 && _hud.RendererPromptVisible) { _hud.HideRendererPrompt(); OnRendererPromptChosen(argument); Console.Error.WriteLine($"[qa] scelto {argument} -> motore={_renderer}"); } break;
                        case "audio-sync": Console.Error.WriteLine($"[qa] audio nativo {_video.NativeAudioState} pos={_hud.Position:0.00}"); break;
                        case "extended": ToggleExtendedFullscreen(); break;
                        case "dvd-key": if (_video.ActiveDvdNav is { } qn) { if (argument == "enter") qn.Activate(); else if (argument.StartsWith("menu")) qn.Menu(argument == "menu-title" ? MacDvdNav.MenuTitle : MacDvdNav.MenuRoot); else qn.Move(argument); } break;
                        case "dvd-click": { var xy = argument.Split(','); var pt = new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)); _hud.VideoHover?.Invoke(pt); Console.Error.WriteLine($"[qa] dvd click {pt} -> {_hud.VideoClick?.Invoke(pt)}"); break; }
                        case "cd-discid": Console.Error.WriteLine("[qa] discid " + MacAudioCd.DiscId(1, 6, 95462 - 150, new[] { 150, 15363, 32314, 46592, 63414, 80489 }.Select(x => x - 150).ToList())); break;
                        case "lib-sortmenu": _library.TestOpenSortMenu(); break;
                        case "open-files": OpenFiles(argument.Split('|')); break;
                        case "dvd-menu": if (_video.DvdNav is { } mn) ShowDvdMenu(mn, MacDvdNav.MenuRoot); break;
                        case "dvd-mode": Console.Error.WriteLine($"[qa] dvd modo streaming={_video.DvdStreaming} menuSuMpv={_video.DvdMenuOnMpv} film={_video.DvdFeatureOn ?? "-"} pos={_hud.Position:0.0} durata={_hud.Duration:0}"); break;
                        case "dvd-state": Console.Error.WriteLine(_video.ActiveDvdNav is { } sn ? $"[qa] dvd menu={sn.InMenu} pulsanti={sn.Buttons} scelto={sn.SelectedButton} titolo={sn.Title} capitolo={sn.Chapter} durata={sn.Duration:0} pos={_hud.Position:0.0} motore={_video.DvdEnginePosition:0.0} overlay={sn.OverlayAt(_video.DvdEnginePosition) is not null}" : "[qa] dvd nessun navigatore"); break;
                        case "drops-sim": _qaDropsPerSec = int.Parse(argument); break;
                        case "prompt-choose": _hud.TestChoosePrompt(int.Parse(argument)); break;
                        case "renderer-prompt-show": _hud.ShowRendererPrompt(("mpv", "Passa a MPV"), ("mpcvr", "Passa a MPC-VR"), ("madvr", "Resta su madVR")); break;
                        case "goto-close": _goTo.Close(); break;
                        case "renderer-choose": MacSettings.ChooseRenderer(argument); SetRenderer(MacSettings.EffectiveRenderer); Console.Error.WriteLine($"[qa] renderer scelto={argument} motore={_renderer}"); break;
                        case "web-max": MacWebResolver.MaxHeight = int.Parse(argument); break;
                        case "hdr-fps": _hdr.TestFps(int.Parse(argument)); break;
                        case "hdr-chart": _hdr.SetChart(int.Parse(argument)); break;
                        case "hdr-mode": _hdr.SetMode(argument == "live"); break;
                        case "hdr-export": _hdr.ExportTo(argument); break;
                        case "hdr-state":
                        {
                            var r = _hdr.Result; var l = _hdr.LiveStats;
                            Console.Error.WriteLine("[qa] hdr " + (r == null ? "nessun risultato" :
                                $"hdr={r.IsHdr} err={r.Error} {r.Transfer} {r.Primaries} {r.Width}x{r.Height} {r.BitDepth}bit cll={r.DeclaredMaxCll} fall={r.DeclaredMaxFall} master={r.MasteringMaxNits}/{r.MasteringMinNits} hdr10+={r.Hdr10Plus} " +
                                $"campioni={r.Samples.Count}/{r.RequestedSamples} picco={r.MeasuredPeakNits:0}@{r.MeasuredPeakTime:0.0} max={r.MeasuredMaxNits:0} mediaMax={r.MeasuredMaxAverageNits:0} mediana={r.MedianNits:0.0} nero={r.BlackNits:0.0000} " +
                                $"gamut={r.GamutRec709:P1}/{r.GamutP3:P1}/{r.GamutRec2020:P1} attiva={r.ActiveWidth}x{r.ActiveHeight} inCorso={_hdr.Running}") +
                                (l == null ? " live=-" : $" live t={l.Time:0.00} picco={l.PeakNits:0} media={l.AverageNits:0.0} nero={l.BlackNits:0.0000}"));
                            break;
                        }
                        case "music-cmd": _library.TestMusicCommand(argument); break;
                        case "pos": { var xy = argument.Split(','); Position = new PixelPoint(int.Parse(xy[0]), int.Parse(xy[1])); break; }
                        case "glass-state": Console.Error.WriteLine("[qa] native glass=" + _library.NativeGlassOn + " supported=" + MacSurfaceGlass.Supported); break;
                        case "bar-targets": Console.Error.WriteLine("[qa] bar " + _library.TestBarTargets + " livello=" + _library.QaBarLayerRenders); break;
                        case "music-state": Console.Error.WriteLine("[qa] music " + _library.TestMusicState); break;
                        case "source": _library.TestSetSource(argument); break;
                        case "photo-file": _library.TestOpenPhotoFile(argument); break;
                        case "photo-view": if (_photoViewer.IsOpen) _photoViewer.QaAction(argument); else _library.TestPhotoView(argument); break;
                        case "diary-debug":
                            foreach (var en in CinecorePlayer2025.Engines.WatchHistoryStore.LoadAll())
                                Console.Error.WriteLine($"[qa] diary {en.MediaPath} cat={en.MediaCategory} eligible={CinecorePlayer2025.Utilities.DiaryMediaPolicy.IsEligible(en.MediaPath, en.MediaCategory)} indexed={CinecorePlayer2025.Utilities.DiaryMediaPolicy.IndexedCategory(en.MediaPath)}");
                            break;
                        case "playlist-sheet": _library.TestPlaylistSheet(); break;
                        case "playlist-next": _library.TestPlaylistNext(); break;
                        case "playlist-make": _library.TestMakePlaylist(argument); break;
                        case "playlist-open": _library.TestOpenPlaylist(argument); break;
                        case "spotlight": OpenSpotlight(); break;
                        case "subs-search": _subtitleSearch.OverVideo = _hud.VideoVisible; _subtitleSearch.Open(argument, System.IO.Path.GetFileNameWithoutExtension(argument), 3000); break;
                        case "subs-lang": _subtitleSearch.TestLanguage(argument); break;
                        case "subs-download": _subtitleSearch.TestDownloadFirst(); break;
                        case "subs-query": _subtitleSearch.TestQuery(argument); break;
                        case "subs-langmenu": _subtitleSearch.TestLanguageMenu(argument != "0"); break;
                        case "subs-state": Console.Error.WriteLine("[qa] subs " + _subtitleSearch.QaSummary); break;
                        case "remote-key": HandleRemoteCommand(argument, new System.Collections.Generic.Dictionary<string, string>()); break;
                        case "remote-open": HandleRemoteCommand("open", new System.Collections.Generic.Dictionary<string, string> { ["url"] = argument }); break;
                        case "icons":
                            foreach (string k in argument.Split(','))
                                Console.Error.WriteLine($"[qa] icon {k} -> {CinecorePlayer2025.Utilities.AssetIconService.Resolve(k)}");
                            break;
                        case "views": MacWindowChrome.DumpViews(this); break;
                        case "photo-state": Console.Error.WriteLine($"[qa] foto viewer={_photoViewer.IsVisible} spotlight={_spotlight.IsOpen} settings={_settings.IsOpen} libreria={_library.IsVisible}"); break;
                        case "screens":
                            Console.Error.WriteLine("[qa] schermi: " + string.Join(" | ", MacScreens.All().Select(s => $"{s.Name} {s.X:0},{s.Y:0} {s.Width:0}x{s.Height:0}")) +
                                $" finestra={Position.X},{Position.Y} su={ScreenOfWindow()?.Name ?? "-"} film={MacScreens.FilmScreen()?.Name ?? "-"}");
                            break;
                        case "remote-pin": Console.Error.WriteLine("[qa] remote pin=" + _remote?.CurrentPin); break;
                        case "spotlight-source": _spotlight.TestSource(argument == "network"); Console.Error.WriteLine("[qa] spot " + _spotlight.QaSummary); break;
                        case "spotlight-state": Console.Error.WriteLine("[qa] spot " + _spotlight.QaSummary + " open=" + _spotlight.IsOpen + " film=" + (_file ?? "-")); break;
                        case "spotlight-cast": _spotlight.TestOpenCast(); break;
                        case "spotlight-key":
                            _spotlight.HandleKey(new Avalonia.Input.KeyEventArgs { Key = argument switch { "left" => Avalonia.Input.Key.Left, "up" => Avalonia.Input.Key.Up, "down" => Avalonia.Input.Key.Down, "enter" => Avalonia.Input.Key.Enter, "escape" => Avalonia.Input.Key.Escape, _ => Avalonia.Input.Key.Right } });
                            Console.Error.WriteLine("[qa] spot " + _spotlight.QaSummary + " open=" + _spotlight.IsOpen);
                            break;
                        case "photo-first": _library.TestOpenFirstPhoto(); break;
                        case "onboarding-next": _library.TestOnboardingNext(); break;
                        case "net-art": _ = _library.TestNetworkArtAsync(); break;
                        case "vault-test":
                            string reference = CinecorePlayer2025.Utilities.SecretVault.Protect("qa-" + argument);
                            Console.Error.WriteLine("[qa] vault roundtrip=" + (CinecorePlayer2025.Utilities.SecretVault.Unprotect(reference) == "qa-" + argument));
                            break;
                        case "quit": Close(); return;
                    }
                    Console.Error.WriteLine(FormattableString.Invariant($"[qa] {action}: library={_library.IsVisible} splash={_splash.IsVisible} hud={_hud.IsVisible} video={_hud.VideoVisible} playing={_hud.IsPlaying} position={_hud.Position:0.00} duration={_hud.Duration:0.00} menu={_menu.IsOpen} settings={_settings.IsOpen} metadata={_metadata.IsOpen} loading={_loading.IsVisible} buttons={MacWindowChrome.QaButtonState(this)} info={_hud.InfoOpen} cast={_hud.CastOpen}"));
                }
                catch (Exception ex) { Console.Error.WriteLine("[qa] failed " + action + ": " + ex.GetType().Name); }
            }, TimeSpan.FromMilliseconds(milliseconds));
        }
    }
}
