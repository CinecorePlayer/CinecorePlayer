#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private CancellationTokenSource? _subtitleRealignCts;
        private System.Windows.Forms.Timer? _subtitleSelectTimer;

        // Il video locale in riproduzione, oppure null (flussi web, pre-film, solo audio).
        private string? CurrentLocalVideoForSubtitles()
        {
            if (_engine == null || !_currentMediaHasVideo || _playingPreRoll) return null;
            string local = NormalizeMediaPathForDisplay(_currentPath ?? string.Empty);
            return !string.IsNullOrWhiteSpace(local) && File.Exists(local) ? local : null;
        }

        // Voci in fondo al menu Sottotitoli: valgono anche quando il file non ha tracce.
        private void AppendSubtitleToolsMenu()
        {
            string? video = CurrentLocalVideoForSubtitles();
            if (video == null)
            {
                // Scaricare e riallineare richiedono il file (si salva accanto al video e se ne ascolta l'audio).
                if (!PlayingNetworkStream || !_currentMediaHasVideo) return;
                _mSubtitles.DropDownItems.Add(new ToolStripSeparator());
                _mSubtitles.DropDownItems.Add(new ToolStripMenuItem(Tx("Scarica sottotitoli…", "Download subtitles…") + "  (" + NetworkOnlyShort + ")") { Enabled = false });
                return;
            }
            _mSubtitles.DropDownItems.Add(new ToolStripSeparator());
            var download = new ToolStripMenuItem(Tx("Scarica sottotitoli…", "Download subtitles…"));
            download.Click += (_, __) => ShowSubtitleDownload();
            _mSubtitles.DropDownItems.Add(download);

            var external = SubtitleFile.FindNextTo(video);
            if (external.Count == 0) return;
            bool running = _subtitleRealignCts != null;
            string prefix = Tx("Riallinea sull'audio", "Realign to the audio");
            foreach (string file in external.Take(4))
            {
                string path = file;
                string label = external.Count == 1 ? prefix : prefix + "  ·  " + SubtitleSuffix(video, path);
                var item = new ToolStripMenuItem(label) { Enabled = !running };
                item.Click += (_, __) => RealignExternalSubtitle(video, path);
                _mSubtitles.DropDownItems.Add(item);
            }
        }

        // "Film.it.srt" -> "it"; senza suffisso il nome del file.
        private static string SubtitleSuffix(string video, string subtitle)
        {
            string name = Path.GetFileNameWithoutExtension(subtitle);
            string stem = Path.GetFileNameWithoutExtension(video);
            string suffix = name.Length > stem.Length ? name[(stem.Length + 1)..] : string.Empty;
            return suffix.Length > 0 ? suffix : Path.GetFileName(subtitle);
        }

        private void ShowSubtitleDownload()
        {
            try
            {
                string? video = CurrentLocalVideoForSubtitles();
                if (video == null)
                {
                    _lblStatus.Text = PlayingNetworkStream
                        ? Tx("Sottotitoli da scaricare: ", "Subtitle download: ") + NetworkOnlyNote
                        : Tx("Sottotitoli: apri prima un video salvato sul PC", "Subtitles: open a local video first");
                    ShowRemoteOsd(null, null, 2400, _lblStatus.Text);
                    return;
                }

                double? duration = _engine != null && _engine.DurationSeconds > 1 ? _engine.DurationSeconds : null;
                string metadataLanguage = UiEnglish ? "en" : "it";
                string? saved;
                bool replaced;
                using var sheetLayout = SheetPresenter.Layout(this);
                using (var dialog = new SubtitleDownloadForm(video, BuildBestDisplayTitleForPath(video), UiEnglish,
                    (language, token) => Task.Run(() => BuildSubtitleQuery(video, duration, metadataLanguage, language, token), token)))
                {
                    SheetPresenter.ShowDialog(dialog, this);
                    saved = dialog.SavedPath;
                    replaced = dialog.Replaced;
                }
                if (saved != null) ShowExternalSubtitle(video, saved, replaced);
            }
            catch (Exception ex)
            {
                Dbg.Warn("Subtitle download failed: " + ex.Message);
            }
        }

        // Ai servizi di sottotitoli vanno solo titolo, anno e identificativi pubblici: mai il percorso del file.
        private static SubtitleSources.Query BuildSubtitleQuery(string video, double? duration, string metadataLanguage, string language, CancellationToken token)
        {
            MovieMetadataService.MediaTitleInfo parsed;
            try { parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(video); }
            catch { parsed = new MovieMetadataService.MediaTitleInfo { NormalizedTitle = Path.GetFileNameWithoutExtension(video) }; }
            MovieMetadataService.RichMetadata? rich = null;
            try { rich = MovieMetadataService.ResolveRichMetadata(video, duration, metadataLanguage, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[SUBS] metadata: " + ex.Message); }
            bool episode = parsed.IsTvEpisode || string.Equals(rich?.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            string title = !string.IsNullOrWhiteSpace(parsed.NormalizedTitle) ? parsed.NormalizedTitle : rich?.Title ?? Path.GetFileNameWithoutExtension(video);
            return new SubtitleSources.Query(title, rich?.Year ?? parsed.Year, rich?.ImdbId, rich?.TmdbId, episode,
                !string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.SeriesTitle : title, parsed.SeasonNumber, parsed.EpisodeNumber, language);
        }

        private void RealignExternalSubtitle(string video, string subtitle)
        {
            if (_subtitleRealignCts != null) return;
            var cts = new CancellationTokenSource();
            _subtitleRealignCts = cts;
            _lblStatus.Text = Tx("Sottotitoli: allineo i tempi sull'audio…", "Subtitles: aligning the timings to the audio…");
            ShowRemoteOsd(null, null, 2400, _lblStatus.Text);
            var progress = new Progress<double>(value =>
            {
                if (IsDisposed || !ReferenceEquals(_subtitleRealignCts, cts)) return;
                _lblStatus.Text = Tx($"Sottotitoli: allineo i tempi sull'audio… {value * 100:0}%", $"Subtitles: aligning the timings to the audio… {value * 100:0}%");
            });
            Task.Run(() => SubtitleWorkflow.RealignAsync(video, subtitle, progress, cts.Token)).ContinueWith(task =>
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_subtitleRealignCts, cts)) _subtitleRealignCts = null;
                    cts.Dispose();
                    if (task.IsCanceled) return;
                    if (task.IsFaulted)
                    {
                        Exception? error = task.Exception?.GetBaseException();
                        Dbg.Warn("[SUBS] realign: " + error?.Message);
                        _lblStatus.Text = error is UnauthorizedAccessException
                            ? Tx("Sottotitoli: la cartella del film non è scrivibile", "Subtitles: the film's folder is not writable")
                            : Tx("Sottotitoli: riallineamento non riuscito", "Subtitles: realignment failed");
                    }
                    else
                    {
                        _lblStatus.Text = Tx("Sottotitoli: ", "Subtitles: ") + SubtitleWorkflow.Describe(task.Result, UiEnglish);
                        if (task.Result.TimingChanged) ShowExternalSubtitle(video, subtitle, replaced: true);
                    }
                    ShowRemoteOsd(null, null, 3200, _lblStatus.Text);
                }));
            }, TaskScheduler.Default);
        }

        // Mostra il sottotitolo appena salvato e lo ricorda come scelta per questo film.
        // mpv lo carica al volo; gli altri motori leggono i file esterni solo all'apertura,
        // quindi il video si riapre allo stesso punto (e all'apertura la scelta ricordata viene ripresa).
        private void ShowExternalSubtitle(string video, string subtitle, bool replaced)
        {
            string suffix = SubtitleSuffix(video, subtitle).ToLowerInvariant();
            if (suffix.EndsWith(".srt", StringComparison.Ordinal)) suffix = string.Empty;
            ExternalSubtitleChoice.Set(video, suffix);
            if (!string.Equals(CurrentLocalVideoForSubtitles(), video, StringComparison.OrdinalIgnoreCase)) return;
            if (!replaced && _engine is Engines.LibMpvPlaybackEngine mpv && mpv.AddSubtitleFile(subtitle, suffix.Length == 2 ? suffix : null))
            {
                _subtitleAutoForcedMode = false;
                return;
            }
            ReopenSame();
        }

        /// <summary>All'apertura: se per questo film l'utente aveva scelto un sottotitolo esterno, lo riattiva.</summary>
        private bool TrySelectRememberedExternalSubtitle(bool retryLater)
        {
            string? video = CurrentLocalVideoForSubtitles();
            string? suffix = video != null ? ExternalSubtitleChoice.Get(video) : null;
            if (video == null || suffix == null || _engine == null) return false;
            try
            {
                var external = _engine.EnumerateStreams().Where(s => s.IsSubtitle && s.IsExternal).ToList();
                var pick = external.FirstOrDefault(s => suffix.Length == 2 && SubtitleLanguage(s)?.StartsWith(suffix, StringComparison.OrdinalIgnoreCase) == true)
                    ?? (suffix.Length != 2 || external.Count == 1 ? external.FirstOrDefault() : null);
                if (pick != null)
                {
                    if (!pick.Selected) _engine.EnableByGlobalIndex(pick.GlobalIndex);
                    _subtitleAutoForcedMode = false;
                    return true;
                }
            }
            catch (Exception ex) { Dbg.Warn("[SUBS] select: " + ex.Message); }
            if (!retryLater) return false;

            // Alcuni motori elencano i file esterni un attimo dopo l'apertura: un solo secondo tentativo.
            _subtitleSelectTimer?.Dispose();
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            _subtitleSelectTimer = timer;
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                timer.Dispose();
                if (ReferenceEquals(_subtitleSelectTimer, timer)) _subtitleSelectTimer = null;
                if (string.Equals(CurrentLocalVideoForSubtitles(), video, StringComparison.OrdinalIgnoreCase))
                    TrySelectRememberedExternalSubtitle(retryLater: false);
            };
            timer.Start();
            return false;
        }

        /// <summary>Scelta fatta dal menu: un esterno viene ricordato per il film, qualsiasi altra scelta lo dimentica.</summary>
        private void NoteSubtitleMenuChoice(Engines.DsStreamItem? stream)
        {
            string? video = CurrentLocalVideoForSubtitles();
            if (video == null) return;
            if (stream is { IsExternal: true })
            {
                string? language = SubtitleLanguage(stream);
                ExternalSubtitleChoice.Set(video, language is { Length: >= 2 } ? language[..2].ToLowerInvariant() : string.Empty);
            }
            else ExternalSubtitleChoice.Set(video, null);
        }
    }
}
