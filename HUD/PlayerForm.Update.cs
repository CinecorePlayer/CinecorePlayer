#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Aggiornamento automatico: poco dopo l'avvio (al massimo una volta al giorno) il player chiede
    // a GitHub se c'e' una release piu' nuova e, se c'e', la propone quando non si sta guardando nulla.
    public sealed partial class PlayerForm
    {
        private AppUpdate.Release? _pendingUpdate;
        private System.Windows.Forms.Timer? _updateOfferTimer;
        private bool _updateDialogOpen;

        private void ScheduleStartupUpdateCheck()
        {
            try
            {
                var state = AppUpdate.State;
                // Solo la copia installata si aggiorna da sola: una build di sviluppo non va sostituita.
                if (!state.AutoCheck || !AppUpdate.IsInstalledCopy || DateTime.UtcNow - state.LastCheckUtc < TimeSpan.FromHours(20)) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                        var release = await AppUpdate.LatestAsync(CancellationToken.None).ConfigureAwait(false);
                        state.LastCheckUtc = DateTime.UtcNow;
                        AppUpdate.SaveState();
                        if (release == null || release.Version <= AppUpdate.Current || release.Version.ToString() == state.SkippedVersion) return;
                        Dbg.Log($"[UPDATE] available: {release.Tag} (running {AppUpdate.Current})", Dbg.LogLevel.Info);
                        TryBeginInvokeOnUi(() => OfferUpdateWhenIdle(release));
                    }
                    catch (Exception ex) { Dbg.Warn("[UPDATE] check: " + ex.Message); }
                });
            }
            catch (Exception ex) { Dbg.Warn("[UPDATE] " + ex.Message); }
        }

        // La proposta non interrompe un film: aspetta che il player sia fermo e in primo piano.
        private void OfferUpdateWhenIdle(AppUpdate.Release release)
        {
            if (IsDisposed || _closingForExit) return;
            _pendingUpdate = release;
            _updateOfferTimer?.Dispose();
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            _updateOfferTimer = timer;
            timer.Tick += (_, __) =>
            {
                if (IsDisposed || _closingForExit || _pendingUpdate == null) { timer.Stop(); timer.Dispose(); return; }
                if (_engine != null || _updateDialogOpen || Form.ActiveForm != this) return;
                timer.Stop();
                timer.Dispose();
                if (ReferenceEquals(_updateOfferTimer, timer)) _updateOfferTimer = null;
                var offered = _pendingUpdate;
                _pendingUpdate = null;
                ShowUpdateDialog(offered);
            };
            timer.Start();
        }

        private void ShowUpdateDialog(AppUpdate.Release? release = null)
        {
            if (_updateDialogOpen) return;
            _updateDialogOpen = true;
            try
            {
                bool started;
                using var sheetLayout = SheetPresenter.Layout(this);
                using (var dialog = new UpdateForm(UiEnglish, release))
                {
                    SheetPresenter.ShowDialog(dialog, this);
                    started = dialog.InstallStarted;
                }
                // L'installer sta per sostituire i file del programma: il player si chiude.
                if (started) BeginInvoke(new Action(Close));
            }
            catch (Exception ex) { Dbg.Warn("Update dialog failed: " + ex.Message); }
            finally { _updateDialogOpen = false; }
        }

        private void ToggleUpdateAutoCheck()
        {
            AppUpdate.State.AutoCheck = !AppUpdate.State.AutoCheck;
            AppUpdate.SaveState();
            _lblStatus.Text = AppUpdate.State.AutoCheck
                ? Tx("Aggiornamenti: controllo all'avvio attivo", "Updates: check at startup on")
                : Tx("Aggiornamenti: controllo all'avvio disattivato", "Updates: check at startup off");
        }
    }
}
