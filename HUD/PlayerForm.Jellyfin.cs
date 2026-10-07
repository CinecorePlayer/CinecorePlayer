#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    // Jellyfin durante la riproduzione: il server viene informato di avvio, avanzamento, pausa e
    // fine, cosi' il punto di ripresa e lo stato "visto" sono gli stessi su tutti i dispositivi
    // (e la sessione compare nella dashboard di Jellyfin).
    public sealed partial class PlayerForm
    {
        private JellyfinClient.Account? _jellyfinAccount;
        private string _jellyfinPath = "", _jellyfinItemId = "", _jellyfinSourceId = "", _jellyfinSession = "";
        private double _jellyfinPosition, _jellyfinDuration;
        private bool _jellyfinPaused;
        private long _jellyfinLastReport;
        // Le segnalazioni partono in ordine: una "fine" non deve arrivare prima dell'ultimo "avanzamento".
        private Task _jellyfinReports = Task.CompletedTask;

        /// <summary>Si sta riproducendo un flusso di rete (DLNA, Jellyfin, indirizzo web) e non un file del PC.
        /// Le funzioni che leggono o scrivono il file (analisi HDR, sottotitoli da scaricare o riallineare,
        /// analisi automatica dell'audio) per questi contenuti sono disattivate, non nascoste: si vede perche'.</summary>
        private bool PlayingNetworkStream
        {
            get
            {
                if (_engine == null || _playingPreRoll || string.IsNullOrWhiteSpace(_currentPath)) return false;
                return Uri.TryCreate(_currentPath, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            }
        }

        // Nelle voci di menu, dove lo spazio e' poco.
        private string NetworkOnlyShort => Tx("solo file sul PC", "local files only");
        private string NetworkOnlyNote => Tx("solo per i file sul PC, non da DLNA o Jellyfin", "files on this PC only, not from DLNA or Jellyfin");

        private JellyfinClient.Account? ShowJellyfinSignIn(JellyfinClient.Server? server)
        {
            try
            {
                using var sheetLayout = SheetPresenter.Layout(this);
                using var dialog = new JellyfinLoginForm(server, UiEnglish);
                SheetPresenter.ShowDialog(dialog, this);
                return dialog.Account;
            }
            catch (Exception ex)
            {
                Dbg.Warn("Jellyfin sign-in failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Titoli degli elementi Jellyfin gia' visti nel catalogo: servono a dare un nome a cio' che
        /// si riprende da "Continua a guardare" o dalla cronologia prima di ricollegarsi al server.</summary>
        private static void RestoreJellyfinTitleHints()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var (url, title, category) in JellyfinClient.RememberedTitles())
                        PlaybackTitleHints.Set(url, title, category);
                }
                catch { }
            });
        }

        private void QueueJellyfinReport(JellyfinClient.Account account, string phase, string itemId, string sourceId, string session, double position, bool paused)
        {
            _jellyfinReports = _jellyfinReports.ContinueWith(async _ =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await JellyfinClient.ReportPlaybackAsync(account, phase, itemId, sourceId, session, position, paused, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { Dbg.Warn($"[JELLYFIN] report '{phase}' failed: {ex.Message}"); }
            }, TaskScheduler.Default).Unwrap();
        }

        /// <summary>Un file e' partito: se viene da un server Jellyfin collegato, comincia la sessione.</summary>
        private void JellyfinPlaybackOpened()
        {
            string? path = _currentPath;
            if (_jellyfinAccount != null && string.Equals(path, _jellyfinPath, StringComparison.OrdinalIgnoreCase)) return;
            JellyfinPlaybackStopped();
            if (_playingPreRoll || !JellyfinClient.TryParseStream(path, out var account, out string itemId, out string sourceId)) return;
            _jellyfinAccount = account;
            _jellyfinPath = path!;
            _jellyfinItemId = itemId;
            _jellyfinSourceId = sourceId;
            _jellyfinSession = Guid.NewGuid().ToString("N");
            _jellyfinPosition = Math.Max(0, _lastKnownPlaybackPosition);
            _jellyfinDuration = 0;
            _jellyfinPaused = _paused;
            _jellyfinLastReport = Environment.TickCount64;
            QueueJellyfinReport(account, "start", itemId, sourceId, _jellyfinSession, _jellyfinPosition, _jellyfinPaused);
        }

        /// <summary>Chiamato a ogni aggiornamento della posizione: al server va un avanzamento ogni dieci secondi,
        /// e subito quando cambia lo stato di pausa.</summary>
        private void JellyfinPlaybackProgress(double position, bool force = false)
        {
            var account = _jellyfinAccount;
            if (account == null) return;
            if (!string.Equals(_currentPath, _jellyfinPath, StringComparison.OrdinalIgnoreCase)) return;
            _jellyfinPosition = Math.Max(0, position);
            double duration = GetTimelineDurationSeconds();
            if (duration > 0) _jellyfinDuration = duration;
            bool pauseChanged = _paused != _jellyfinPaused;
            if (!force && !pauseChanged && Environment.TickCount64 - _jellyfinLastReport < 10000) return;
            _jellyfinPaused = _paused;
            _jellyfinLastReport = Environment.TickCount64;
            QueueJellyfinReport(account, "progress", _jellyfinItemId, _jellyfinSourceId, _jellyfinSession, _jellyfinPosition, _jellyfinPaused);
        }

        /// <summary>Fine della riproduzione (o passaggio a un altro file): il server salva il punto di ripresa
        /// e, se il film e' arrivato in fondo, lo segna come visto.</summary>
        private void JellyfinPlaybackStopped(bool waitBriefly = false)
        {
            var account = _jellyfinAccount;
            if (account == null) return;
            _jellyfinAccount = null;
            // La libreria mostra subito il nuovo punto di ripresa, senza rileggere il catalogo.
            CinematicMediaLibraryPage.NoteJellyfinPlayback(_jellyfinPath, _jellyfinPosition, _jellyfinDuration);
            _jellyfinPath = "";
            QueueJellyfinReport(account, "stop", _jellyfinItemId, _jellyfinSourceId, _jellyfinSession, _jellyfinPosition, false);
            _ = Task.Run(async () => { using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6)); await JellyfinClient.StopTranscodeAsync(account, limit.Token).ConfigureAwait(false); });
            // Alla chiusura del programma la segnalazione deve partire prima che il processo finisca.
            if (waitBriefly)
                try { _jellyfinReports.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
