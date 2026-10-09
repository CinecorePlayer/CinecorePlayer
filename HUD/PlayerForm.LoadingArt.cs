#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        // ----- Schermata di caricamento con immagine, titolo e barra di avanzamento -----
        // Vive dentro le superfici di caricamento (maschera e finestra animata): non usa il segnaposto della
        // pausa, quindi sparisce con loro e non puo' restare sotto il film.

        private string? _loadingArtFor;
        private string? _loadingArtPath;
        private string _loadingArtTitle = "";
        private double _loadingArtProgress;
        private string _loadingLastMessage = "";

        /// <summary>Titolo e sfondo per il caricamento di questo contenuto. Per la musica non c'e' nulla (tranne il CD, che lo imposta da se').</summary>
        private void PrepareLoadingArt(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (string.Equals(_loadingArtFor, path, StringComparison.OrdinalIgnoreCase)) return;
                if (AudioCd.IsStub(path)) return;
                _loadingArtFor = path;
                // Lo stesso caricamento puo' passare di qui piu' volte con il percorso scritto in modi diversi
                // (libreria, coda, flusso risolto): a schermata gia' visibile la barra non riparte.
                if (_videoLoading?.Visible != true) _loadingArtProgress = 0;
                _loadingArtPath = null;
                _loadingArtTitle = "";
                if (LooksLikePureAudioByExt(path!) || LooksLikeImageByExt(path!)) return;

                string title = BuildBestDisplayTitleForPath(path);
                if (string.IsNullOrWhiteSpace(title)) return;
                _loadingArtTitle = title;
                if (!ShouldUseMovieMetadataTitleForPath(path!)) return;
                string key = ResolveMetadataLookupKey(path);
                string? cached = TryGetCachedBackdropForNetflix(key, NormalizeMediaPathForDisplay(path!));
                if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached)) { _loadingArtPath = cached; return; }

                // Sfondo non ancora scaricato: lo si chiede adesso e arriva a caricamento in corso, se fa in tempo.
                string wanted = path!;
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var art = MovieMetadataService.ResolveTitleAndBackdrop(key, limit.Token);
                        if (string.IsNullOrWhiteSpace(art.localBackdropPath) || !File.Exists(art.localBackdropPath)) return;
                        BeginInvoke(new Action(() =>
                        {
                            if (!string.Equals(_loadingArtFor, wanted, StringComparison.OrdinalIgnoreCase)) return;
                            _loadingArtPath = art.localBackdropPath;
                            if (!string.IsNullOrWhiteSpace(art.normalizedTitle)) _loadingArtTitle = art.normalizedTitle!;
                            RefreshLoadingArt();
                        }));
                    }
                    catch { }
                });
            }
            catch (Exception ex) { Dbg.Warn("[LOADING] art: " + ex.Message); }
        }

        /// <summary>Il CD imposta da se' titolo e immagine (album e foto dell'artista), prima e dopo la lettura dell'indice.</summary>
        private void SetLoadingArt(string title, string? imagePath)
        {
            _loadingArtFor = null;
            _loadingArtTitle = title ?? "";
            _loadingArtPath = imagePath;
            RefreshLoadingArt();
        }

        private void RefreshLoadingArt()
        {
            try
            {
                if (_videoLoading == null || !_videoLoading.Visible) return;
                _videoLoading.SetArt(_loadingArtPath, _loadingArtTitle, _loadingArtProgress);
                _threadedVideoLoading.Show(Handle, _loadingLastMessage, _loadingArtPath, _loadingArtTitle, _loadingArtProgress);
            }
            catch { }
        }

        // ----- Il film in riproduzione visto dalla libreria: locandina, anno, durata, voto, trama, cast -----
        // Serve al pannello Info e alla scheda del cast. Si calcola una volta per contenuto (solo dati gia' in cache).
        private string? _filmIdentityFor;
        private (string Title, string Poster, string Meta, string Overview, string Cast, System.Collections.Generic.List<MovieMetadataService.RichCastMember> Members) _filmIdentity =
            ("", "", "", "", "", new System.Collections.Generic.List<MovieMetadataService.RichCastMember>());

        private (string Title, string Poster, string Meta, string Overview, string Cast, System.Collections.Generic.List<MovieMetadataService.RichCastMember> Members) CurrentFilmIdentity()
        {
            string path = _currentPath ?? "";
            // Finche' i dati non ci sono (appena aperto, metadati in arrivo) si riprova a ogni aggiornamento.
            if (string.Equals(_filmIdentityFor, path, StringComparison.OrdinalIgnoreCase) && (_filmIdentity.Overview.Length > 0 || _filmIdentity.Poster.Length > 0)) return _filmIdentity;
            _filmIdentityFor = path;
            _filmIdentity = ("", "", "", "", "", new System.Collections.Generic.List<MovieMetadataService.RichCastMember>());
            try
            {
                if (path.Length == 0 || !_currentMediaHasVideo || _cinematicLibraryPage == null || !ShouldUseMovieMetadataTitleForPath(path)) return _filmIdentity;
                var probe = new HUD.NetflixModeItem { Path = NormalizeMediaPathForDisplay(path), MetadataKey = ResolveMetadataLookupKey(path), Title = BuildBestDisplayTitleForPath(path) };
                _cinematicLibraryPage.PopulateSpotlightItemFromCache(probe);
                string poster = !string.IsNullOrWhiteSpace(probe.PosterPath) && File.Exists(probe.PosterPath) ? probe.PosterPath! : "";
                string overview = probe.MetadataLoaded || !string.IsNullOrWhiteSpace(probe.Overview) ? (probe.Overview ?? "") : "";
                // I testi provvisori ("Dettagli TMDb in caricamento") non sono una trama.
                if (overview.Contains("TMDb", StringComparison.OrdinalIgnoreCase) && overview.Length < 90) overview = "";
                var meta = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(probe.YearText)) meta.Add(probe.YearText!);
                double seconds = _duration > 0 ? _duration : probe.DurationSeconds;
                if (seconds > 60) meta.Add((int)(seconds / 3600) > 0 ? $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60):00}m" : $"{(int)(seconds / 60)}m");
                if (probe.Rating is > 0) meta.Add("TMDb " + probe.Rating.Value.ToString("0.0"));
                if (probe.Genres.Count > 0) meta.Add(string.Join(", ", probe.Genres.GetRange(0, Math.Min(2, probe.Genres.Count))));
                _filmIdentity = (probe.Title ?? "", poster, string.Join(" | ", meta), overview.Trim(), (probe.CastLine ?? "").Trim(), probe.CastMembers ?? new System.Collections.Generic.List<MovieMetadataService.RichCastMember>());
            }
            catch (Exception ex) { Dbg.Log("[INFO] film identity: " + ex.Message, Dbg.LogLevel.Info); }
            return _filmIdentity;
        }

        /// <summary>Il tasto del cast nell'overlay compare solo se del film in corso si conoscono gli interpreti.</summary>
        private void SyncCastButton()
        {
            try { if (_hud != null) _hud.CastAvailable = _engine != null && _currentMediaHasVideo && CurrentFilmIdentity().Members.Count > 0; } catch { }
        }

        private void ShowCastSheet()
        {
            // Come in Spotlight: una fascia in basso, al posto di timeline e comandi. Il tasto la apre e la richiude.
            try
            {
                if (_hud == null) return;
                if (_hud.CastOpen) { _hud.CloseCast(); return; }
                var identity = CurrentFilmIdentity();
                if (identity.Members.Count == 0) return;
                _hud.OpenCast(identity.Title.Length > 0 ? identity.Title : InfoTitleForCurrent(), identity.Meta.Replace(" | ", "   •   "), identity.Members);
                try { SafeShowOverlayHost(); SyncOverlayToVideoRect(); BringOverlaysToFront(); } catch { }
            }
            catch (Exception ex) { Dbg.Warn("[CAST] " + ex.Message); }
        }

        /// <summary>Porta la barra del caricamento fino in fondo e aspetta che ci sia arrivata; non fa nulla se la schermata con la barra non c'e'.</summary>
        private async Task FillLoadingBarAsync(System.Threading.CancellationToken ct)
        {
            try
            {
                if (_videoLoading == null || !_videoLoading.Visible || _loadingArtTitle.Length == 0) return;
                _loadingArtProgress = 1;
                _videoLoading.SetArt(_loadingArtPath, _loadingArtTitle, 1);
                _threadedVideoLoading.Show(Handle, _loadingLastMessage ?? Tx("Caricamento…", "Loading…"), _loadingArtPath, _loadingArtTitle, 1);
                await Task.Delay(480, ct);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private void ClearLoadingArt()
        {
            _loadingArtFor = null;
            _loadingArtPath = null;
            _loadingArtTitle = "";
            _loadingArtProgress = 0;
        }

        /// <summary>A che punto e' l'apertura quando compare questo messaggio. La barra non torna mai indietro.</summary>
        private static double LoadingStage(string message)
        {
            if (message.Contains("Apro il flusso", StringComparison.Ordinal) || message.Contains("Opening stream", StringComparison.Ordinal)) return .62;
            if (message.Contains("Leggo il CD", StringComparison.Ordinal) || message.Contains("Reading the CD", StringComparison.Ordinal)) return .40;
            if (message.Contains("Leggo il disco", StringComparison.Ordinal) || message.Contains("Reading the disc", StringComparison.Ordinal)) return .34;
            if (message.Contains("Preparo", StringComparison.Ordinal) || message.Contains("Preparing", StringComparison.Ordinal)) return .14;
            return .22;
        }

        /// <summary>Foto dell'artista in formato largo per il caricamento del CD; null se non si trova in tempo.</summary>
        private static async Task<string?> ResolveArtistPhotoForLoadingAsync(string artist, string album)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(artist)) return null;
                var request = MusicArtistArtworkService.ResolveAsync(artist, album);
                if (await Task.WhenAny(request, Task.Delay(6000)).ConfigureAwait(false) != request) return null;
                string? landscape = request.Result.Landscape;
                if (string.IsNullOrWhiteSpace(landscape)) return null;
                if (File.Exists(landscape)) return landscape;
                if (!Uri.TryCreate(landscape, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "loading-art");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, MusicArtistArtworkService.Identity(artist) + ".jpg");
                if (File.Exists(file) && new FileInfo(file).Length > 4000) return file;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                byte[] image = await http.GetByteArrayAsync(uri).ConfigureAwait(false);
                if (image.Length < 4000) return null;
                await File.WriteAllBytesAsync(file, image).ConfigureAwait(false);
                return file;
            }
            catch { return null; }
        }
    }
}
