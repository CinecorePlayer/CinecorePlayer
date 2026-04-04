using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
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
using System.Security;
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
        // ------------ padding condiviso fra carosello, sezioni e griglia ------------
        private void ApplyContentSidePad()
        {
            _grid.Padding = new Padding(_contentSidePad, 8, _gridRightPad, 8);

            _secRecenti.LeftMargin = _contentSidePad;
            _secAll.LeftMargin = _contentSidePad;

            _secRecenti.Invalidate();
            _secAll.Invalidate();
        }


        // ------------ CAROSELLO "Recenti" ------------
        private void BuildCarouselChrome()
        {
            _carPrev = new IconButton(IconButton.Kind.ChevronLeft);
            _carNext = new IconButton(IconButton.Kind.ChevronRight);

            // scorriamo di UNA card alla volta
            _carPrev.Click += (_, __) => _carouselViewport.StepItems(-1);
            _carNext.Click += (_, __) => _carouselViewport.StepItems(+1);

            _carouselHost.Controls.Add(_carPrev);
            _carouselHost.Controls.Add(_carNext);

            // label "nessun contenuto da riprendere"
            _resumeEmptyLabel = new Label
            {
                Text = "Nessun contenuto da riprendere.",
                AutoSize = true,
                ForeColor = Theme.SubtleText,
                BackColor = Color.Black,
                Visible = false
            };
            _carouselHost.Controls.Add(_resumeEmptyLabel);

            AlignCarouselViewport();
            LayoutCarouselArrows();
            LayoutResumeEmptyLabel();
        }
        private void LayoutResumeEmptyLabel()
        {
            if (_resumeEmptyLabel == null) return;
            if (!_resumeEmptyLabel.Visible) return;

            int x = (_carouselHost.ClientSize.Width - _resumeEmptyLabel.Width) / 2;
            int y = (_carouselHost.ClientSize.Height - _resumeEmptyLabel.Height) / 2;
            if (x < 0) x = 0;
            if (y < 0) y = 0;

            _resumeEmptyLabel.Location = new Point(x, y);
            _resumeEmptyLabel.BringToFront();
        }
        private void AlignCarouselViewport()
        {
            if (IsDisposed)
                return;

            if (!_carouselHost.Visible)
                return;

            int hostW = _carouselHost.ClientSize.Width;
            if (hostW <= 0)
                hostW = _carouselHost.Width;
            if (hostW <= 0 && _right != null)
                hostW = _right.ClientSize.Width - (_gridRightPad * 2);

            if (hostW <= 0)
                return;

            int itemOuter = _carouselViewport.GetItemOuterWidthEstimate();
            if (itemOuter <= 0)
                itemOuter = 320; // 300 + margini

            int cardsPerRow = EstimateCardsPerRowForCarousel();
            int desiredW = cardsPerRow * itemOuter;

            if (desiredW > hostW)
                desiredW = hostW;

            int x = (hostW - desiredW) / 2;
            if (x < 0) x = 0;

            _contentSidePad = x;
            ApplyContentSidePad();

            int desiredH = _carouselViewport.GetPreferredHeightEstimate();
            if (desiredH <= 0)
                desiredH = 236;

            _carouselViewport.Size = new Size(desiredW, desiredH);
            _carouselViewport.Location = new Point(x, 8);

            // PRIMA: controllavi anche l’overflow orizzontale → spesso rimanevano invisibili
            // bool needArrows =
            //     _carouselViewport.ItemsCount > 1 &&
            //     _carouselViewport.HasHorizontalOverflow();

            // ADESSO: se ci sono almeno 2 card, mostrami sempre le frecce
            bool needArrows = _carouselViewport.ItemsCount > 1;

            _carPrev.Visible = needArrows;
            _carNext.Visible = needArrows;

            LayoutCarouselArrows();
            LayoutResumeEmptyLabel();
        }
        private void LayoutCarouselArrows()
        {
            if (_carPrev == null || _carNext == null) return;

            var vp = _carouselViewport;
            int y = vp.Top + (vp.Height - 42) / 2;
            if (y < 0) y = 0;

            int prevX = Math.Max(8, vp.Left - 50);
            int nextX = Math.Min(Math.Max(8, _carouselHost.ClientSize.Width - 50), vp.Right + 8);

            _carPrev.Bounds = new Rectangle(prevX, y, 42, 42);
            _carNext.Bounds = new Rectangle(nextX, y, 42, 42);

            _carPrev.BringToFront();
            _carNext.BringToFront();
        }

        private bool HasConfiguredLocalRootsForCurrentCategory()
        {
            if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!IsLocalLibraryCategory(_selCat))
                return true;

            try
            {
                return AllRootsForCategory(_selCat)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => NormalizeRootPath(path))
                    .Any(path => !string.IsNullOrWhiteSpace(path));
            }
            catch
            {
                return false;
            }
        }

        private bool ShouldShowRecentCarouselForCurrentState()
        {
            bool isPlaylist = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPreferiti = string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);
            bool isFoto = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase);
            bool isUrlSrc = string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase);
            bool isYtSrc = string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);
            bool isDlnaSrc = string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase);

            if (isPlaylist || isPreferiti || isFoto || isUrlSrc || isYtSrc || isDlnaSrc)
                return false;

            return HasConfiguredLocalRootsForCurrentCategory();
        }

        private void ResetRecentCarouselVisualState(string? emptyMessage = null)
        {
            try
            {
                _carouselViewport.ResetItems(
                    new List<string>(),
                    GetOrNewThumbCts().Token,
                    _ => { },
                    (_, __) => { });
            }
            catch { }

            try { _carPrev.Visible = false; } catch { }
            try { _carNext.Visible = false; } catch { }
            try
            {
                if (_resumeEmptyLabel != null)
                {
                    if (!string.IsNullOrWhiteSpace(emptyMessage))
                        _resumeEmptyLabel.Text = emptyMessage;
                    _resumeEmptyLabel.Visible = !string.IsNullOrWhiteSpace(emptyMessage);
                }
            }
            catch { }

            try { AlignCarouselViewport(); } catch { }
        }

        private void LoadRecentsCarouselImmediate()
        {
            if (string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
            {
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;
                return;
            }

            bool showCarousel = ShouldShowRecentCarouselForCurrentState();

            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;
            if (!showCarousel)
            {
                ResetRecentCarouselVisualState();
                return;
            }

            // NEW: per la categoria Musica il carosello è "recenti" slegato dai minutaggi
            if (string.Equals(_selCat, "Musica", StringComparison.OrdinalIgnoreCase))
            {
                LoadMusicRecentsCarousel();
                return;
            }

            // carica tutti i punti di ripresa dal JSON
            var all = PlaybackResumeStore.LoadAll();

            // filtra solo quelli che:
            // - esistono ancora sul disco
            // - sono compatibili con la categoria corrente (film/video/foto/musica)
            var perCat = all
                .Where(e => !string.IsNullOrWhiteSpace(e.MediaPath)
                            && File.Exists(e.MediaPath)
                            && ResumeEntryBelongsToCurrentSource(e)
                            && ResumeEntryMatchesCurrentCategory(e)
                            && !IsResumeEntryEffectivelyCompleted(e))
                .OrderByDescending(e => e.SavedAt)
                .Take(30)
                .ToList();

            if (perCat.Count == 0)
            {
                ResetRecentCarouselVisualState("Nessun contenuto da riprendere.");
                return;
            }

            _resumeEmptyLabel.Text = "Nessun contenuto da riprendere.";
            _resumeEmptyLabel.Visible = false;

            var token = GetOrNewThumbCts().Token;

            // mappa path → entry per progress bar e start position
            var byPath = perCat.ToDictionary(e => e.MediaPath, e => e, StringComparer.OrdinalIgnoreCase);

            var paths = perCat.Select(e => e.MediaPath).ToList();

            _carouselViewport.ResetItems(
                paths,
                token,
                path =>
                {
                    if (byPath.TryGetValue(path, out var entry))
                        SafeOpen(path, entry.PositionSeconds); // RIPARTI DA QUI
                    else
                        SafeOpen(path);
                },
                (path, card) =>
                {
                    // placeholder subito
                    var cat = CategoryFromExt((Path.GetExtension(path) ?? "").ToLowerInvariant());
                    var phBmp = GetCategoryPlaceholder(cat, 520);
                    card.SetInitialPlaceholder(phBmp);

                    if (string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var displayTitle = MovieMetadataService.GetBestKnownDisplayTitle(path);
                            if (!string.IsNullOrWhiteSpace(displayTitle))
                                card.SetDisplayName(displayTitle);
                        }
                        catch { }
                    }

                    // progress bar (pos/dur)
                    if (byPath.TryGetValue(path, out var entry) && entry.DurationSeconds > 0)
                    {
                        double progress = Math.Max(0.0,
                            Math.Min(1.0, entry.PositionSeconds / entry.DurationSeconds));
                        card.SetProgress(progress);
                    }

                    // thumb async poi (con poster online per Film)
                    QueueLibraryThumbLoadForFileCard(card, path, token);
                });
            AlignCarouselViewport();
        }

        private void LoadMusicRecentsCarousel()
        {
            var all = _musicRecents.All()
                .Where(p => !string.IsNullOrWhiteSpace(p) && IsMusicFilePath(p))
                .Where(p =>
                {
                    // se è URL http/https lo accettiamo sempre,
                    // se è path locale verifichiamo che il file esista ancora
                    if (Uri.TryCreate(p, UriKind.Absolute, out var u) &&
                        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                        return true;

                    return File.Exists(p);
                })
                .Take(30)
                .ToList();

            if (all.Count == 0)
            {
                ResetRecentCarouselVisualState("Nessun brano riprodotto di recente.");
                return;
            }

            _resumeEmptyLabel.Text = "Nessun contenuto da riprendere.";
            _resumeEmptyLabel.Visible = false;

            var token = GetOrNewThumbCts().Token;
            var paths = all;

            _carouselViewport.ResetItems(
                paths,
                token,
                path => SafeOpen(path),              // NO ripresa, apertura semplice
                (path, card) =>
                {
                    // placeholder categoria musica
                    var phBmp = GetCategoryPlaceholder("musica", 520);
                    card.SetInitialPlaceholder(phBmp);

                    // niente progress bar (non chiamiamo SetProgress)

                    // thumb async
                    QueueLibraryThumbLoadForFileCard(card, path, token);
                });
            AlignCarouselViewport();
        }

        private bool ResumeEntryMatchesCurrentCategory(PlaybackResumeStore.Entry e)
        {
            if (string.IsNullOrWhiteSpace(e.MediaPath))
                return false;

            // niente resume in Playlist/Preferiti
            if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
                return false;

            var ext = (Path.GetExtension(e.MediaPath) ?? "").ToLowerInvariant();
            string catLower = _selCat.ToLowerInvariant();

            bool isFilmCat = catLower == "film";
            bool isVideoCat = catLower == "video";

            if (isFilmCat || isVideoCat)
            {
                double? mins = e.DurationSeconds > 0 ? e.DurationSeconds / 60.0 : (double?)null;
                return isFilmCat
                    ? BelongsToMovieOrSeriesCategory(e.MediaPath, mins)
                    : BelongsToShortVideoCategory(e.MediaPath, mins);
            }

            // Foto / Musica: usa la stessa logica di ExtsForCategory
            var allowed = new HashSet<string>(ExtsForCategory(_selCat), StringComparer.OrdinalIgnoreCase);
            return allowed.Contains(ext);
        }

        private bool ResumeEntryBelongsToCurrentSource(PlaybackResumeStore.Entry e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.MediaPath))
                return false;

            if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!IsLocalLibraryCategory(_selCat))
                return true;

            var roots = AllRootsForCategory(_selCat)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 0)
                return false;

            foreach (var root in roots)
            {
                if (IsPathUnderRoot(e.MediaPath, root))
                    return true;
            }

            return false;
        }

        private static bool IsPathUnderRoot(string candidatePath, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
                return false;

            try
            {
                string fullFile = Path.GetFullPath(candidatePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string fullRoot = NormalizeRootPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.IsNullOrWhiteSpace(fullFile) || string.IsNullOrWhiteSpace(fullRoot))
                    return false;

                if (string.Equals(fullFile, fullRoot, StringComparison.OrdinalIgnoreCase))
                    return true;

                string rootedPrefix = fullRoot + Path.DirectorySeparatorChar;
                return fullFile.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }



        private static bool IsResumeEntryEffectivelyCompleted(PlaybackResumeStore.Entry e)
        {
            try
            {
                if (e.DurationSeconds <= 0) return false;

                double remaining = e.DurationSeconds - e.PositionSeconds;

                // Consideriamo "finito" se mancano pochissimi secondi:
                // - 3% della durata, con clamp 5..12s (evita che clip brevi spariscano troppo presto)
                double threshold = Math.Min(12.0, Math.Max(5.0, e.DurationSeconds * 0.03));

                return remaining <= threshold;
            }
            catch { return false; }
        }
        private void UpdateRecentsFromScanFor(string category, List<FileInfo> scanned)
        {
            var paths = scanned
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Select(fi => fi.FullName)
                .Take(200)
                .ToList();

            _recents.Set(category, paths);
        }


    }
}
