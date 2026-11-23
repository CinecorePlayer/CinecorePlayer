using System;
using System.Threading;
using System.Threading.Tasks;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

public sealed partial class MainWindow
{
    private static int _traktDiarySyncRunning;

    /// <summary>
    /// A riproduzione finita (ScheduleTraktDiarySync di Windows): con l'account collegato e la scelta attiva, i titoli
    /// finiti dopo il collegamento vengono segnati su Trakt. Il resto del diario lo manda TraktView.StartAutoSync, da solo.
    /// </summary>
    private static void ScheduleTraktDiarySync()
    {
        try
        {
            if (!TraktClient.IsConnected || !TraktClient.MarkWatched) return;
            if (Interlocked.Exchange(ref _traktDiarySyncRunning, 1) == 1) return;
            DateTime since = TraktClient.ConnectedAtUtc;
            _ = Task.Run(async () =>
            {
                try
                {
                    // Il diario viene scritto in sottofondo alla chiusura: gli si lascia il tempo di finire.
                    await Task.Delay(5000).ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await TraktLibrary.SendDiaryAsync(since, "it", timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { Console.Error.WriteLine("[TRAKT] diary sync: " + ex.Message); }
                finally { Interlocked.Exchange(ref _traktDiarySyncRunning, 0); }
            });
        }
        catch (Exception ex) { Console.Error.WriteLine("[TRAKT] " + ex.Message); }
    }
}
