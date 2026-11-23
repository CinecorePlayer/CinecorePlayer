using System;
using System.Threading;
using System.Threading.Tasks;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// PlayerForm.Jellyfin.cs: il server viene informato di avvio, avanzamento, pausa e fine, cosi' il punto di ripresa
// e lo stato "visto" sono gli stessi su tutti i dispositivi (e la sessione compare nella dashboard di Jellyfin).
public sealed partial class MainWindow
{
    private JellyfinClient.Account? _jellyfinAccount;
    private string _jellyfinPath = "", _jellyfinItemId = "", _jellyfinSourceId = "", _jellyfinSession = "";
    private double _jellyfinPosition, _jellyfinDuration;
    private bool _jellyfinPaused;
    private long _jellyfinLastReport;
    // Le segnalazioni partono in ordine: una "fine" non deve arrivare prima dell'ultimo "avanzamento".
    private Task _jellyfinReports = Task.CompletedTask;

    private void QueueJellyfinReport(JellyfinClient.Account account, string phase, string itemId, string sourceId, string session, double position, bool paused)
    {
        // Le prove automatiche non toccano il "Continua a guardare" dell'utente sul server.
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") is { Length: > 0 }) return;
        _jellyfinReports = _jellyfinReports.ContinueWith(async _ =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await JellyfinClient.ReportPlaybackAsync(account, phase, itemId, sourceId, session, position, paused, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[JELLYFIN] report '{phase}' failed: {ex.GetType().Name}"); }
        }, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Un file e' partito: se viene da un server Jellyfin collegato, comincia la sessione.</summary>
    private void JellyfinPlaybackOpened(double startPosition)
    {
        string? path = _file;
        if (_jellyfinAccount != null && string.Equals(path, _jellyfinPath, StringComparison.OrdinalIgnoreCase)) return;
        JellyfinPlaybackStopped();
        if (!JellyfinClient.TryParseStream(path, out var account, out string itemId, out string sourceId)) return;
        _jellyfinAccount = account;
        _jellyfinPath = path!;
        _jellyfinItemId = itemId;
        _jellyfinSourceId = sourceId;
        _jellyfinSession = Guid.NewGuid().ToString("N");
        _jellyfinPosition = Math.Max(0, startPosition);
        _jellyfinDuration = 0;
        _jellyfinPaused = false;
        _jellyfinLastReport = Environment.TickCount64;
        QueueJellyfinReport(account, "start", itemId, sourceId, _jellyfinSession, _jellyfinPosition, false);
    }

    /// <summary>A ogni posizione: un avanzamento ogni dieci secondi, e subito quando cambia lo stato di pausa.</summary>
    private void JellyfinPlaybackProgress(double position, double duration, bool playing)
    {
        var account = _jellyfinAccount;
        if (account == null || !string.Equals(_file, _jellyfinPath, StringComparison.OrdinalIgnoreCase)) return;
        _jellyfinPosition = Math.Max(0, position);
        if (duration > 0) _jellyfinDuration = duration;
        bool paused = !playing;
        if (paused == _jellyfinPaused && Environment.TickCount64 - _jellyfinLastReport < 10000) return;
        _jellyfinPaused = paused;
        _jellyfinLastReport = Environment.TickCount64;
        QueueJellyfinReport(account, "progress", _jellyfinItemId, _jellyfinSourceId, _jellyfinSession, _jellyfinPosition, paused);
    }

    /// <summary>Fine della riproduzione (o altro file): il server salva la ripresa e, arrivati in fondo, segna "visto".</summary>
    private void JellyfinPlaybackStopped(bool waitBriefly = false)
    {
        var account = _jellyfinAccount;
        if (account == null) return;
        _jellyfinAccount = null;
        // La libreria mostra subito il nuovo punto di ripresa, senza rileggere il catalogo.
        NetworkStore.NotePlayback(_jellyfinPath, _jellyfinPosition, _jellyfinDuration);
        _jellyfinPath = "";
        QueueJellyfinReport(account, "stop", _jellyfinItemId, _jellyfinSourceId, _jellyfinSession, _jellyfinPosition, false);
        // Chiamata avviata subito: cattura e azzera la sessione vecchia prima che il nuovo motore ne crei un'altra.
        // Farla partire dentro Task.Run poteva invece leggere e fermare la conversione appena avviata.
        var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        _ = JellyfinClient.StopTranscodeAsync(account, limit.Token).ContinueWith(_ => limit.Dispose(), TaskScheduler.Default);
        // Alla chiusura del programma la segnalazione deve partire prima che il processo finisca.
        if (waitBriefly)
            try { _jellyfinReports.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
