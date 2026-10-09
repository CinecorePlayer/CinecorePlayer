#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private void ShowLibraryReport()
        {
            try
            {
                EnsureCinematicLibraryPageCreated();
                var sources = _cinematicLibraryPage?.CollectVideoFilesForReport() ?? Array.Empty<LibraryReport.Source>();
                using var sheetLayout = SheetPresenter.Layout(this);
                using var dialog = new LibraryReportForm(sources, UiEnglish);
                SheetPresenter.ShowDialog(dialog, this);
            }
            catch (Exception ex)
            {
                Dbg.Warn("Library report failed: " + ex.Message);
            }
        }

        // Analisi HDR del video in riproduzione: la scheda legge il file con un decodificatore
        // proprio, quindi la riproduzione continua; un clic sul grafico porta a quel punto.
        private void ShowHdrAnalysis()
        {
            try
            {
                string? path = _currentPath;
                if (_playingPreRoll && !string.IsNullOrWhiteSpace(_pendingMainPathAfterPreRoll))
                    path = _pendingMainPathAfterPreRoll;
                string local = NormalizeMediaPathForDisplay(path ?? string.Empty);
                if (_engine == null || !_currentMediaHasVideo || string.IsNullOrWhiteSpace(local) || !File.Exists(local))
                {
                    _lblStatus.Text = PlayingNetworkStream
                        ? Tx("Analisi HDR: ", "HDR analysis: ") + NetworkOnlyNote
                        : Tx("Analisi HDR: apri prima un video salvato sul PC", "HDR analysis: open a local video first");
                    ShowRemoteOsd(null, null, 2400, _lblStatus.Text);
                    return;
                }

                using var sheetLayout = SheetPresenter.Layout(this);
                using var dialog = new HdrAnalysisForm(local, BuildBestDisplayTitleForPath(path), UiEnglish,
                    position: () => _engine != null && string.Equals(NormalizeMediaPathForDisplay(_currentPath ?? string.Empty), local, StringComparison.OrdinalIgnoreCase)
                        ? GetTimelinePositionForHud() : 0,
                    seek: seconds =>
                    {
                        if (_engine == null || !string.Equals(NormalizeMediaPathForDisplay(_currentPath ?? string.Empty), local, StringComparison.OrdinalIgnoreCase))
                            return;
                        PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: seconds);
                        _engine.PositionSeconds = seconds;
                    });
                SheetPresenter.ShowDialog(dialog, this);
            }
            catch (Exception ex)
            {
                Dbg.Warn("HDR analysis failed: " + ex.Message);
            }
        }
    }
}
