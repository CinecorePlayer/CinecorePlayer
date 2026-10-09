#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Dischi (Blu-ray / DVD: unita' ottica, cartella, ISO) e componenti esterni.
    public sealed partial class PlayerForm
    {
        /// <summary>L'etichetta del disco scritta come un titolo ("THE_LEGO_MOVIE_2" diventa "The Lego Movie 2"); vuota se non c'e'.</summary>
        private static string DiscLabelAsTitle(string path)
        {
            string label = (DiscMedia.DisplayName(path) ?? "").Replace('_', ' ').Trim();
            if (label.Length > 0 && label == label.ToUpperInvariant())
                label = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label.ToLowerInvariant());
            return label.Length > 0 && !label.EndsWith(":\\", StringComparison.Ordinal) ? label : "";
        }

        /// <summary>Un disco video non e' in libreria: lo si presenta come un film, con il titolo dell'etichetta, cosi'
        /// valgono anche per lui la schermata prima del film e la ricerca della locandina.</summary>
        private static void HintDiscAsFilm(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !DiscMedia.TryResolve(path!, out _)) return;
                if (!string.IsNullOrWhiteSpace(PlaybackTitleHints.GetCategory(path))) return;
                string label = DiscLabelAsTitle(path!);
                PlaybackTitleHints.Set(path, label.Length > 0 ? label : null, "Film");
            }
            catch { }
        }

        // ----- DVD con il motore di Windows: menu, capitoli e tempo arrivano dal disco -----

        /// <summary>Il motore in corso, se sta leggendo un DVD come un lettore da tavolo (menu compresi).</summary>
        private Engines.DirectShowUnifiedEngine? DvdEngine => _engine is Engines.DirectShowUnifiedEngine { DvdMode: true } engine ? engine : null;

        private int _dvdTitleSeen = -1;

        /// <summary>A ogni aggiornamento della posizione: la durata e' quella del titolo in corso (avvisi, film, extra), zero nei menu.</summary>
        private void SyncDvdState()
        {
            var dvd = DvdEngine;
            if (dvd == null) { _dvdTitleSeen = -1; return; }
            try
            {
                // Durante un salto o un cambio di scena il disco per un attimo non dichiara la durata: si tiene
                // l'ultima buona, altrimenti la timeline spariva e non tornava piu'. Zero solo dentro un menu.
                double length = dvd.DurationSeconds;
                if (length > 0 || dvd.DvdMenuActive) _duration = length;
                bool timeline = _duration > 0 && !IsPhotoMode;
                if (_hud != null && _hud.TimelineVisible != timeline) { _hud.TimelineVisible = timeline; _hud.Invalidate(); }
                int title = dvd.DvdLocation().Title;
                if (title == _dvdTitleSeen) return;
                _dvdTitleSeen = title;
                _info = dvd.DescribeDvd();
                // Il riquadro del video dipende dal formato del titolo (16:9 o 4:3 anamorfico): va ricalcolato.
                try { ApplyImageSizing(animate: false); } catch { }
                SyncHudTimelineAvailability();
                try { _hud?.Invalidate(); } catch { }
            }
            catch { }
        }

        /// <summary>Tasti e telecomando dentro un menu del disco. Vero se il comando e' andato al disco.</summary>
        private bool DvdMenuCommand(string command)
        {
            var dvd = DvdEngine;
            if (dvd == null || !dvd.DvdMenuActive) return false;
            switch (command)
            {
                case "left": dvd.DvdMoveSelection(-1, 0); break;
                case "right": dvd.DvdMoveSelection(+1, 0); break;
                case "up": dvd.DvdMoveSelection(0, -1); break;
                case "down": dvd.DvdMoveSelection(0, +1); break;
                case "ok": dvd.DvdActivate(); break;
                case "back": if (!dvd.DvdBack()) dvd.DvdResumeFromMenu(); break;
                default: return false;
            }
            return true;
        }

        // Ripresa con il motore di Windows: titolo e secondi. Il disco rifiuta i salti finche' mostra i suoi
        // avvisi iniziali, quindi si riprova per un po' (di solito accetta appena arriva al menu).
        private (int Title, double Seconds) _pendingDvdResume;

        private void TryPendingDvdResume()
        {
            var wanted = _pendingDvdResume;
            if (wanted.Title <= 0 || wanted.Seconds <= 0) return;
            int attempts = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, __) =>
            {
                var dvd = DvdEngine;
                bool done = dvd == null || ++attempts > 60;
                if (!done && dvd!.DvdPlayTitleAt(wanted.Title, wanted.Seconds))
                {
                    done = true;
                    Dbg.Log($"[DVD] resumed title {wanted.Title} at {wanted.Seconds:0}s", Dbg.LogLevel.Info);
                }
                if (!done) return;
                timer.Stop(); timer.Dispose();
                _pendingDvdResume = default;
            };
            timer.Start();
        }

        private void ShowDvdRootMenu()
        {
            var dvd = DvdEngine;
            if (dvd == null) return;
            if (!dvd.DvdShowRootMenu()) ShowRemoteOsd(null, null, 2400, Tx("Il disco non lo permette in questo momento", "The disc does not allow it right now"));
        }

        // ----- Dischi aperti con mpv: capitoli, durata e titoli arrivano dal motore, dopo il caricamento -----

        /// <summary>Per un disco letto da mpv: capitoli, durata e formato presi dal motore (ffmpeg non lo analizza prima).</summary>
        private void RefreshDiscInfoFromEngine(bool titleChanged = false)
        {
            try
            {
                if (DvdEngine is { } dvd)
                {
                    _info = dvd.DescribeDvd();
                    _duration = dvd.DurationSeconds;
                    try { ApplyImageSizing(animate: false); } catch { }
                    if (_pendingDiscResume > 0)
                    {
                        // Salvato da mpv (senza numero di titolo): il film e' quasi sempre il titolo 1.
                        _pendingDvdResume = (Math.Max(1, SavedDiscTitle(_currentPath ?? "")), _pendingDiscResume);
                        _pendingDiscResume = 0;
                        TryPendingDvdResume();
                    }
                    Dbg.Log($"[DVD] playing with the Windows DVD engine: {_info.Width}x{_info.Height}, menu={dvd.DvdMenuActive}", Dbg.LogLevel.Info);
                    return;
                }
                if (_engine is not Engines.LibMpvPlaybackEngine mpv || !DiscMedia.TryResolve(_currentPath ?? string.Empty, out _)) return;
                var info = mpv.DescribeLoadedMedia();
                // Alla prima lettura mpv puo' non avere ancora i capitoli: non si sostituisce un elenco buono con uno
                // vuoto. Dopo un cambio di titolo invece vale quello che dice il titolo nuovo, anche senza capitoli.
                if (!titleChanged && _info != null && _info.Chapters.Count > 0 && info.Chapters.Count == 0) return;
                _info = info;
                if (info.Duration > 0) _duration = info.Duration;
                Dbg.Log($"[DISC] loaded: {info.Width}x{info.Height}, {info.Duration:0}s, {info.Chapters.Count} chapters, {mpv.DiscTitles().Count} titles", Dbg.LogLevel.Info);
                if (!titleChanged && _pendingDiscResume > 0 && info.Duration > _pendingDiscResume + 30)
                {
                    double target = _pendingDiscResume;
                    Dbg.Log($"[DISC] resuming at {target:0}s", Dbg.LogLevel.Info);
                    PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: target);
                    mpv.PositionSeconds = target;
                }
                _pendingDiscResume = 0;
                try { _hud?.Invalidate(); } catch { }
            }
            catch (Exception ex) { Dbg.Warn("[DISC] info: " + ex.Message); }
        }

        /// <summary>Voci del menu "Titoli del disco": il film e gli altri titoli (contenuti speciali) con la durata.</summary>
        private void PopulateDiscTitlesMenu(ToolStripMenuItem root)
        {
            root.DropDownItems.Clear();
            if (_engine is not Engines.LibMpvPlaybackEngine mpv) return;
            foreach (var (index, seconds, current) in mpv.DiscTitles())
            {
                // Loghi e avvisi di pochi secondi non sono titoli da scegliere.
                if (seconds < 30 && !current) continue;
                // I titoli di pochi secondi sono loghi e avvisi: restano nell'elenco, ma in fondo si riconoscono dalla durata.
                string label = Tx("Titolo ", "Title ") + (index + 1) + "     " + (seconds > 0 ? Fmt(seconds) : "");
                int chosen = index;
                var item = new ToolStripMenuItem(label) { Checked = current };
                item.Click += (_, __) =>
                {
                    if (_engine is not Engines.LibMpvPlaybackEngine engine || !engine.SelectDiscTitle(chosen)) return;
                    // Il titolo nuovo ha la sua durata e i suoi capitoli: si rileggono quando mpv lo ha caricato.
                    RefreshDiscInfoWhenTitleLoaded();
                    _hud.ShowOnce(1500);
                };
                root.DropDownItems.Add(item);
            }
        }

        // Il titolo nuovo ha la sua durata e i suoi capitoli; il lettore impiega qualche secondo a caricarlo.
        private void RefreshDiscInfoWhenTitleLoaded()
        {
            int attempts = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 600 };
            timer.Tick += (_, __) =>
            {
                bool loaded = false;
                try { loaded = _engine is Engines.LibMpvPlaybackEngine mpv && mpv.DescribeLoadedMedia() is { Duration: > 0, Width: > 0 }; } catch { }
                if (!loaded && ++attempts < 50 && _engine != null) return;
                timer.Stop(); timer.Dispose();
                if (loaded) RefreshDiscInfoFromEngine(titleChanged: true);
            };
            timer.Start();
        }

        // ----- Ripresa: il punto in cui si e' fermato questo disco (non un altro messo nello stesso lettore) -----
        private double _pendingDiscResume;

        private static double SavedDiscPosition(string path)
        {
            string identity = DiscIdentity(path);
            if (identity.Length == 0) return 0;
            try
            {
                if (!File.Exists(DiscIdentityPath)) return 0;
                foreach (string line in File.ReadAllLines(DiscIdentityPath))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2 && parts[0] == identity && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                        return seconds;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Il titolo del DVD a cui appartiene il punto salvato (0 se salvato da mpv, che legge solo il film).</summary>
        private static int SavedDiscTitle(string path)
        {
            string identity = DiscIdentity(path);
            try
            {
                if (identity.Length == 0 || !File.Exists(DiscIdentityPath)) return 0;
                foreach (string line in File.ReadAllLines(DiscIdentityPath))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length == 3 && parts[0] == identity && int.TryParse(parts[2], out int title)) return title;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Alla chiusura: salva dove si e' arrivati nel film del disco (non nei contenuti speciali, non a film finito).</summary>
        private void SaveDiscPosition()
        {
            try
            {
                string? path = _currentPath;
                if (string.IsNullOrWhiteSpace(path)) return;
                if (DvdEngine is { } dvd)
                {
                    // Solo dentro un film (non un avviso o un extra di pochi minuti), e non a film finito.
                    string disc = DiscIdentity(path!);
                    var where = dvd.DvdLocation();
                    double at = dvd.PositionSeconds, length = dvd.DurationSeconds;
                    if (disc.Length == 0 || where.Title <= 0 || length < 1200) return;
                    WriteDiscLine(path!, disc, at > 60 && at < length - 180 ? at : 0, where.Title);
                    return;
                }
                if (_engine is not Engines.LibMpvPlaybackEngine mpv) return;
                string identity = DiscIdentity(path!);
                if (identity.Length == 0) return;
                var titles = mpv.DiscTitles();
                bool mainTitle = titles.Count == 0 || titles.Any(t => t.Current && t.Seconds >= titles.Max(x => x.Seconds) - 1);
                if (!mainTitle) return;
                double position = _lastKnownPlaybackPosition, duration = _duration;
                bool worthIt = position > 60 && duration > 0 && position < duration - 180;
                WriteDiscLine(path!, identity, worthIt ? position : 0);
            }
            catch (Exception ex) { Dbg.Warn("[DISC] resume save: " + ex.Message); }
        }

        // Una riga per lettore: identita', tabulazione, secondi.
        private static void WriteDiscLine(string path, string identity, double position, int title = 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiscIdentityPath)!);
            var lines = File.Exists(DiscIdentityPath)
                ? System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(File.ReadAllLines(DiscIdentityPath), line => !line.StartsWith(path.ToUpperInvariant() + "|", StringComparison.Ordinal)))
                : new System.Collections.Generic.List<string>();
            lines.Add(identity + "\t" + position.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + (title > 0 ? "\t" + title : ""));
            File.WriteAllLines(DiscIdentityPath, lines);
        }

        private static bool IsDiscPath(string? path) => !string.IsNullOrWhiteSpace(path) && DiscMedia.TryResolve(path!, out _);

        private static string DiscIdentityPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "disc-identity.txt");

        // Etichetta e dimensione distinguono un disco dall'altro quanto basta per non riprendere il film sbagliato.
        private static string DiscIdentity(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path) ?? "";
                if (root.Length == 0 || !string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "";
                var drive = new DriveInfo(root);
                return drive.DriveType == DriveType.CDRom && drive.IsReady ? path.ToUpperInvariant() + "|" + drive.VolumeLabel + "|" + drive.TotalSize : "";
            }
            catch { return ""; }
        }

        private static bool DiscResumeMatches(string path)
        {
            string identity = DiscIdentity(path);
            if (identity.Length == 0) return true; // cartella o immagine: il percorso identifica gia' il contenuto
            try { return File.Exists(DiscIdentityPath) && System.Linq.Enumerable.Any(File.ReadAllLines(DiscIdentityPath), line => line.Split('\t')[0] == identity); }
            catch { return false; }
        }

        private static void RememberDiscIdentity(string path)
        {
            string identity = DiscIdentity(path);
            if (identity.Length == 0) return;
            // Stesso disco di prima: la riga (con il suo punto di ripresa) resta. Disco diverso: riparte da zero.
            try { if (!DiscResumeMatches(path)) WriteDiscLine(path, identity, 0); }
            catch (Exception ex) { Dbg.Warn("[DISC] identity: " + ex.Message); }
        }

        private bool _componentsDialogOpen;
        // ISO montata per la riproduzione in corso; l'unita' sparisce poco dopo la chiusura del film.
        private IsoMount? _discMount;

        private void ReleaseDiscMount()
        {
            var mount = _discMount;
            _discMount = null;
            try { mount?.Dispose(); } catch { }
        }

        // Dopo uno stop il grafo puo' essere ancora in chiusura: l'unita' si smonta qualche secondo
        // dopo, e solo se nel frattempo non e' ripartito nulla.
        private void ReleaseDiscMountLater()
        {
            var mount = _discMount;
            if (mount == null) return;
            if (_closingForExit) { ReleaseDiscMount(); return; }
            _ = Task.Delay(5000).ContinueWith(_ => TryBeginInvokeOnUi(() =>
            {
                if (ReferenceEquals(_discMount, mount) && _engine == null) ReleaseDiscMount();
            }));
        }

        private void PopulateOpenDiscMenu(ToolStripMenuItem menu)
        {
            menu.DropDownItems.Clear();
            foreach (var (label, path) in DiscMedia.InsertedDiscs())
                menu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, __) => OpenDisc(path)));
            if (menu.DropDownItems.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add(new ToolStripMenuItem(Tx("Cartella del disco…", "Disc folder…"), null, (_, __) => OpenDiscFolderWithDialog()));
            menu.DropDownItems.Add(new ToolStripMenuItem(Tx("Immagine ISO…", "ISO image…"), null, (_, __) => OpenDiscImageWithDialog()));
        }

        private void OpenDiscFolderWithDialog()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = Tx("Scegli la cartella del disco (quella che contiene BDMV o VIDEO_TS)", "Choose the disc folder (the one containing BDMV or VIDEO_TS)"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) OpenDisc(dialog.SelectedPath);
        }

        private void OpenDiscImageWithDialog()
        {
            using var dialog = new OpenFileDialog
            {
                Title = Tx("Apri immagine del disco", "Open disc image"),
                Filter = Tx("Immagini ISO", "ISO images") + "|*.iso",
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) OpenDisc(dialog.FileName);
        }

        // ----- CD audio -----

        private int _audioCdOpenSerial;
        // La schermata "Leggo il CD…" resta finche' il primo brano suona davvero (il lettore impiega qualche secondo a partire).
        private bool _audioCdLoadingPending;

        /// <summary>Apre il CD nel lettore come un album: legge l'indice, cerca titoli e copertina, mette le tracce in coda.</summary>
        private async void OpenAudioCd(char drive, int trackNumber)
        {
            int serial = ++_audioCdOpenSerial;
            try
            {
                // Indice, titoli e copertina richiedono qualche secondo (il lettore deve partire): schermata di caricamento.
                _audioCdLoadingPending = true;
                ClearLoadingArt();
                _loadingArtTitle = Tx("CD audio", "Audio CD");
                ShowVideoLoading(Tx("Leggo il CD…", "Reading the CD…"));
                _videoLoading?.Update();
                System.Collections.Generic.IReadOnlyList<string> tracks;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    tracks = await Task.Run(() => AudioCd.Prepare(drive, cts.Token));
                if (serial != _audioCdOpenSerial || IsDisposed) return;
                if (tracks.Count == 0)
                {
                    _audioCdLoadingPending = false;
                    HideVideoLoading();
                    ShowRemoteOsd(null, null, 3200, Tx("Non riesco a leggere questo CD", "This CD cannot be read"));
                    return;
                }
                // "Track03.cda" apre quella traccia; il disco intero parte dalla prima.
                int start = trackNumber > 0 ? Math.Max(0, tracks.ToList().FindIndex(p => Path.GetFileName(p).StartsWith(trackNumber.ToString("00") + " ", StringComparison.Ordinal))) : 0;
                _audioCdLoadingPending = true;
                // Album e artista ora si conoscono: titolo dell'album e, se si trova in tempo, la foto larga dell'artista.
                try
                {
                    var first = MediaProbe.ReadAudioTags(tracks[start]);
                    string album = string.IsNullOrWhiteSpace(first.Album) ? Tx("CD audio", "Audio CD") : first.Album;
                    string cover = Path.Combine(Path.GetDirectoryName(tracks[start]) ?? "", "cover.jpg");
                    // Subito la copertina dell'album; la foto larga dell'artista la sostituisce se arriva.
                    SetLoadingArt(string.IsNullOrWhiteSpace(first.Artist) ? album : first.Artist + " | " + album, File.Exists(cover) ? cover : null);
                    string artistName = first.Artist ?? "";
                    _ = ResolveArtistPhotoForLoadingAsync(artistName, album).ContinueWith(task =>
                    {
                        string? photo = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                        Dbg.Log("[CD] artist photo for the loading screen: " + (photo == null ? "none" : "found"), Dbg.LogLevel.Info);
                        if (photo == null) return;
                        try { BeginInvoke(new Action(() => { if (_audioCdLoadingPending && serial == _audioCdOpenSerial) { _loadingArtPath = photo; RefreshLoadingArt(); } })); } catch { }
                    }, TaskScheduler.Default);
                }
                catch { }
                ShowVideoLoading(Tx("Leggo il CD…", "Reading the CD…"));
                StartPlaybackQueue(tracks, start, shuffle: false);
                // Rete di sicurezza: se il brano non parte, la schermata non resta li'.
                var guard = new System.Windows.Forms.Timer { Interval = 20000 };
                guard.Tick += (_, __) => { guard.Stop(); guard.Dispose(); if (_audioCdLoadingPending) { _audioCdLoadingPending = false; HideVideoLoading(); } };
                guard.Start();
            }
            catch (Exception ex)
            {
                Dbg.Warn("[CD] open: " + ex.Message);
                _audioCdLoadingPending = false;
                try { HideVideoLoading(); } catch { }
                ShowRemoteOsd(null, null, 3200, Tx("Non riesco a leggere questo CD", "This CD cannot be read"));
            }
        }

        private void OpenDisc(string path)
        {
            if (AudioCd.IsAudioCdPath(path, out _, out _)) { OpenPath(path); return; }
            if (!DiscMedia.TryResolve(path, out _))
            {
                ShowRemoteOsd(null, null, 3200, Tx("Qui non c'è un Blu-ray né un DVD", "No Blu-ray or DVD found here"));
                return;
            }
            SkipLoadingIfActive();
            PlayLibraryRequestedPath(path, resumeSeconds: null);
        }

        private void ShowComponentUpdatesDialog()
        {
            if (_componentsDialogOpen) return;
            _componentsDialogOpen = true;
            try
            {
                using var sheetLayout = SheetPresenter.Layout(this);
                using var dialog = new ComponentUpdatesForm(UiEnglish, () => _engine != null);
                SheetPresenter.ShowDialog(dialog, this);
            }
            catch (Exception ex) { Dbg.Warn("Components dialog failed: " + ex.Message); }
            finally { _componentsDialogOpen = false; }
        }

        // Una volta al giorno, poco dopo l'avvio: yt-dlp si aggiorna da solo, per gli altri resta un
        // promemoria in Impostazioni › Aggiornamenti (un installatore non parte mai senza richiesta).
        /// <summary>
        /// Al primo avvio di sempre, a libreria visibile, propone di collegare il telefono come telecomando.
        /// Chi aggiorna da una versione precedente non lo vede: per lui non e' il primo avvio.
        /// </summary>
        private void ScheduleFirstRunRemoteOffer()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
                string marker = Path.Combine(folder, "remote-offer.txt");
                if (File.Exists(marker)) return;
                bool olderInstall = false;
                try
                {
                    foreach (string name in new[] { "extras.json", "cinematicLibraryRoots.json", "libraryRoots.json" })
                    {
                        string existing = Path.Combine(folder, name);
                        if (File.Exists(existing) && DateTime.UtcNow - File.GetCreationTimeUtc(existing) > TimeSpan.FromMinutes(20)) olderInstall = true;
                    }
                }
                catch { }
                try { Directory.CreateDirectory(folder); File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); } catch { }
                if (olderInstall) return;

                var timer = new System.Windows.Forms.Timer { Interval = 2500 };
                int tries = 0;
                timer.Tick += (_, _) =>
                {
                    if (IsDisposed || _closingForExit || ++tries > 60) { timer.Stop(); timer.Dispose(); return; }
                    // Si aspetta che la libreria sia a schermo e che non ci sia altro davanti.
                    if (_remote == null || _engine != null || _videoLoading?.Visible == true || SheetPresenter.AnyOpen ||
                        _cinematicLibraryPage?.Visible != true || !IsCinecoreForeground()) return;
                    timer.Stop(); timer.Dispose();
                    bool wanted;
                    using (SheetPresenter.Layout(this))
                    using (var offer = new RemoteOfferForm(UiEnglish))
                        wanted = SheetPresenter.ShowDialog(offer, this) == DialogResult.OK;
                    if (wanted && _remote != null) ShowPairingBanner(_remote.CurrentPin);
                };
                timer.Start();
            }
            catch (Exception ex) { Dbg.Warn("[REMOTE] first-run offer: " + ex.Message); }
        }

        private void ScheduleStartupComponentCheck()
        {
            try
            {
                if (!AppUpdate.State.AutoCheck || DateTime.UtcNow - ComponentUpdates.State.LastCheckUtc < TimeSpan.FromHours(20)) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                        var pending = await ComponentUpdates.RunStartupAsync(CancellationToken.None).ConfigureAwait(false);
                        if (pending.Count > 0) Dbg.Log("[COMPONENTS] available: " + string.Join(", ", pending), Dbg.LogLevel.Info);
                    }
                    catch (Exception ex) { Dbg.Warn("[COMPONENTS] check: " + ex.Message); }
                });
            }
            catch (Exception ex) { Dbg.Warn("[COMPONENTS] " + ex.Message); }
        }
    }
}
