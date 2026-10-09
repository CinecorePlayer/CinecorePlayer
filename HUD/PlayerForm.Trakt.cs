#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private static int _traktDiarySyncRunning;

        private void ShowTraktDialog()
        {
            try
            {
                EnsureCinematicLibraryPageCreated();
                var library = _cinematicLibraryPage?.CollectTitlesForRecommendations() ?? Array.Empty<TraktLibrary.LibraryTitle>();
                string? selected;
                using var sheetLayout = SheetPresenter.Layout(this);
                using (var dialog = new TraktForm(library, UiEnglish))
                {
                    SheetPresenter.ShowDialog(dialog, this);
                    selected = dialog.SelectedLibraryPath;
                }
                if (selected == null) return;
                if (_engine != null)
                {
                    // Durante la riproduzione la libreria non e' a schermo.
                    _lblStatus.Text = Tx("È in libreria: ", "It is in your library: ") + BuildBestDisplayTitleForPath(selected);
                    ShowRemoteOsd(null, null, 2800, _lblStatus.Text);
                    return;
                }
                _cinematicLibraryPage?.OpenDetailsForPath(selected);
            }
            catch (Exception ex)
            {
                Dbg.Warn("Trakt dialog failed: " + ex.Message);
            }
        }

        // A riproduzione finita: se l'account e' collegato e l'utente lo vuole, i titoli finiti
        // dopo il collegamento vengono segnati su Trakt. Il diario precedente parte solo dal pulsante della scheda.
        private void ScheduleTraktDiarySync()
        {
            try
            {
                if (!TraktClient.IsConnected || !TraktClient.MarkWatched) return;
                if (Interlocked.Exchange(ref _traktDiarySyncRunning, 1) == 1) return;
                string language = UiEnglish ? "en" : "it";
                DateTime since = TraktClient.ConnectedAtUtc;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Il diario viene scritto in background alla chiusura: gli si lascia il tempo di finire.
                        await Task.Delay(5000).ConfigureAwait(false);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        await TraktLibrary.SendDiaryAsync(since, language, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) { Dbg.Warn("[TRAKT] diary sync: " + ex.Message); }
                    finally { Interlocked.Exchange(ref _traktDiarySyncRunning, 0); }
                });
            }
            catch (Exception ex) { Dbg.Warn("[TRAKT] " + ex.Message); }
        }
    }
}
