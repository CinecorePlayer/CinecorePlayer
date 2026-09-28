#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ScheduleRemoteServicesAfterFirstFrame();

            var startupArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
            bool hasStartupMedia = NormalizeCommandLineMediaPaths(startupArgs).Count > 0;

            if (hasStartupMedia)
            {
                // Program.Main consegna anche gli argomenti della prima istanza tramite
                // l'unico dispatcher esterno. Non accodarli una seconda volta da OnShown:
                // era la causa dei loader multipli e delle superfici ricostruite due volte.
                return;
            }

            // Fallback difensivo: se per qualche motivo la libreria non è stata creata nel costruttore,
            // mostrala subito, senza maschere o caricamenti intermedi.
            try
            {
                if (_engine == null && !IsAnyLibraryVisible())
                    ShowDefaultLibrary(stopCurrent: false);

                if (_engine == null && _cinematicLibraryPage?.Visible == true)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { _cinematicLibraryPage?.CompleteInitialContentAfterFirstFrame(); } catch { }
                        try { WarmNetflixModeItemsInBackground(); } catch { }
                        try
                        {
                            if (_cinematicLibraryPage?.Visible == true)
                            {
                                ActiveControl = _cinematicLibraryPage;
                                _cinematicLibraryPage.FocusLibrarySurface();
                            }
                        }
                        catch { }
                    }));
                }

                // Non chiamare ToggleFullscreen() in OnShown: provoca un flash nero/bianco
                // dopo il primo paint. La finestra parte già borderless fullscreen dal costruttore.
            }
            catch { }

            try { EnsureActive(); } catch { }
        }

        internal void OpenExternalCommandLineArgs(IEnumerable<string>? args)
        {
            try
            {
                var paths = NormalizeCommandLineMediaPaths(args);
                if (paths.Count == 0)
                    return;

                _pendingExternalOpenPaths.AddRange(paths);

                _externalOpenBatchTimer ??= new System.Windows.Forms.Timer { Interval = 450 };
                _externalOpenBatchTimer.Stop();
                _externalOpenBatchTimer.Tick -= ExternalOpenBatchTimerTick;
                _externalOpenBatchTimer.Tick += ExternalOpenBatchTimerTick;
                _externalOpenBatchTimer.Start();
            }
            catch { }
        }

        private List<string> NormalizeCommandLineMediaPaths(IEnumerable<string>? args)
        {
            var result = new List<string>();
            if (args == null)
                return result;

            var parts = args
                .Where(arg => !string.IsNullOrWhiteSpace(arg))
                .Select(arg => arg.Trim())
                .Where(arg => arg.Length > 0)
                .ToList();

            void AddIfPlayable(string? raw)
            {
                var candidate = NormalizeCommandLinePathCandidate(raw);
                if (string.IsNullOrWhiteSpace(candidate))
                    return;

                if (!IsQueuePlayablePathInternal(candidate))
                    return;

                if (!result.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
                    result.Add(candidate);
            }

            foreach (var raw in parts)
                AddIfPlayable(raw);

            // Alcune registrazioni "Apri con" non quotano %1: i path con spazi arrivano
            // spezzati in piu' argomenti. Ricostruiamo finestre contigue e accettiamo solo
            // quelle che puntano a un media esistente/playable.
            for (int start = 0; start < parts.Count; start++)
            {
                string combined = string.Empty;
                for (int end = start; end < parts.Count; end++)
                {
                    combined = string.IsNullOrEmpty(combined)
                        ? parts[end]
                        : combined + " " + parts[end];

                    if (end > start)
                        AddIfPlayable(combined);
                }
            }

            return result;
        }

        private static string NormalizeCommandLinePathCandidate(string? raw)
        {
            var candidate = raw?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(candidate))
                return string.Empty;

            try
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
                    candidate = uri.LocalPath;
            }
            catch { }

            try
            {
                if (Path.IsPathRooted(candidate))
                    candidate = Path.GetFullPath(candidate);
            }
            catch { }

            return candidate.Trim();
        }

        private void ExternalOpenBatchTimerTick(object? sender, EventArgs e)
        {
            try { _externalOpenBatchTimer?.Stop(); } catch { }
            FlushExternalOpenBatch();
        }

        private void FlushExternalOpenBatch()
        {
            List<string> batch;
            try
            {
                batch = NormalizePlaybackQueuePaths(_pendingExternalOpenPaths);
                _pendingExternalOpenPaths.Clear();
            }
            catch
            {
                return;
            }

            if (batch.Count == 0)
                return;

            SkipLoadingIfActive();

            if (batch.Count == 1)
            {
                // Usa esattamente la stessa transizione della libreria: una sola cover,
                // un solo stop e un solo OpenPath, anche per Explorer / "Apri con".
                PlayLibraryRequestedPath(batch[0], resumeSeconds: null);
                return;
            }

            StartPlaybackQueue(batch, startIndex: 0, shuffle: false);
        }

        private void SkipLoadingIfActive()
        {
            try
            {
                CancelVideoLoadingUiImmediate(releasePointerCapture: true);
                BringOverlaysToFront();
            }
            catch { }
        }

        private void CancelVideoLoadingUiImmediate(bool releasePointerCapture)
        {
            // Clear the requested state even when an ancestor is hidden. Visible
            // reports effective visibility and cannot tell us whether a mask is pending.
            try { _threadedVideoLoading.Hide(); } catch { }

            try
            {
                if (_videoLoading != null)
                {
                    _videoLoading.Capture = false;
                    _videoLoading.Visible = false;
                    _videoLoading.Invalidate();
                }
            }
            catch { }

            try { HidePlaybackTransitionCover(); } catch { }
            if (releasePointerCapture)
            {
                try { ReleaseStaleCinecoreMouseCapture(force: true); } catch { }
            }
        }

        private void ShowVideoLoading(string? message = null)
        {
            if (_openingMusic)
            {
                CancelVideoLoadingUiImmediate(releasePointerCapture: false);
                return;
            }
            try
            {
                message ??= Tx("Caricamento…", "Loading…");

                // Passaggio demo → film: niente overlay di caricamento (effetto “cinema”).
                if (_suppressVideoLoadingSerial != 0 && _suppressVideoLoadingSerial == _openSerial)
                {
                    CancelVideoLoadingUiImmediate(releasePointerCapture: false);
                    return;
                }

                try
                {
                    _hud.Visible = false;
                    _hud.TimelineVisible = false;
                    _infoOverlay.Visible = false;
                    _remoteOsd.Visible = false;
                    if (_pausePlaceholder != null)
                        _pausePlaceholder.Visible = false;
                    ClearMpcvrMixerOverlayMirror();
                    HideExternalPlaybackOverlayHosts();
                }
                catch { }

                if (_videoLoading != null)
                {
                    _videoLoading.SetMessage(message);
                    _videoLoading.Visible = true;
                    _videoLoading.BringToFront();
                    // È una superficie opaca e leggera: dipingerla qui garantisce
                    // che copra il frame della libreria prima di stop/probe/renderer.
                    _videoLoading.Refresh();
                    try { _threadedVideoLoading.Show(Handle, message); } catch { }
                }
            }
            catch { }

            // Il mask vive nello stack principale ed e' gia' in primo piano. Non
            // rialzare qui le owned window del playback mentre il graph si costruisce.
        }

        private void HideVideoLoading()
        {
            // Preroll already completed in OpenPath. A second WM_TIMER delayed
            // hiding both surfaces and could be starved by native window updates.
            CancelVideoLoadingUiImmediate(releasePointerCapture: false);
            if (_suppressVideoLoadingSerial == _openSerial)
                _suppressVideoLoadingSerial = 0;
            try { SyncHudTimelineAvailability(); } catch { }
            try { BringOverlaysToFront(); } catch { }
        }


    }
}
