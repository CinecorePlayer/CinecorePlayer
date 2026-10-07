#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Dischi (Blu-ray / DVD: unita' ottica, cartella, ISO) e componenti esterni.
    public sealed partial class PlayerForm
    {
        private bool _componentsDialogOpen;
        // ISO montata per la riproduzione in corso; l'unita' sparisce poco dopo la chiusura del film.
        private IsoMount? _discMount;

        private void ReleaseDiscMount()
        {
            var mount = _discMount;
            _discMount = null;
            try { mount?.Dispose(); } catch { }
        }

        // Dopo uno stop il grafo puo' essere ancora in chiusura: l'unita' si smonta qualche secondo
        // dopo, e solo se nel frattempo non e' ripartito nulla.
        private void ReleaseDiscMountLater()
        {
            var mount = _discMount;
            if (mount == null) return;
            if (_closingForExit) { ReleaseDiscMount(); return; }
            _ = Task.Delay(5000).ContinueWith(_ => TryBeginInvokeOnUi(() =>
            {
                if (ReferenceEquals(_discMount, mount) && _engine == null) ReleaseDiscMount();
            }));
        }

        private void PopulateOpenDiscMenu(ToolStripMenuItem menu)
        {
            menu.DropDownItems.Clear();
            foreach (var (label, path) in DiscMedia.InsertedDiscs())
                menu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, __) => OpenDisc(path)));
            if (menu.DropDownItems.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add(new ToolStripMenuItem(Tx("Cartella del disco…", "Disc folder…"), null, (_, __) => OpenDiscFolderWithDialog()));
            menu.DropDownItems.Add(new ToolStripMenuItem(Tx("Immagine ISO…", "ISO image…"), null, (_, __) => OpenDiscImageWithDialog()));
        }

        private void OpenDiscFolderWithDialog()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = Tx("Scegli la cartella del disco (quella che contiene BDMV o VIDEO_TS)", "Choose the disc folder (the one containing BDMV or VIDEO_TS)"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) OpenDisc(dialog.SelectedPath);
        }

        private void OpenDiscImageWithDialog()
        {
            using var dialog = new OpenFileDialog
            {
                Title = Tx("Apri immagine del disco", "Open disc image"),
                Filter = Tx("Immagini ISO", "ISO images") + "|*.iso",
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) OpenDisc(dialog.FileName);
        }

        private void OpenDisc(string path)
        {
            if (!DiscMedia.TryResolve(path, out _))
            {
                ShowRemoteOsd(null, null, 3200, Tx("Qui non c'è un Blu-ray né un DVD", "No Blu-ray or DVD found here"));
                return;
            }
            SkipLoadingIfActive();
            PlayLibraryRequestedPath(path, resumeSeconds: null);
        }

        private void ShowComponentUpdatesDialog()
        {
            if (_componentsDialogOpen) return;
            _componentsDialogOpen = true;
            try
            {
                using var sheetLayout = SheetPresenter.Layout(this);
                using var dialog = new ComponentUpdatesForm(UiEnglish, () => _engine != null);
                SheetPresenter.ShowDialog(dialog, this);
            }
            catch (Exception ex) { Dbg.Warn("Components dialog failed: " + ex.Message); }
            finally { _componentsDialogOpen = false; }
        }

        // Una volta al giorno, poco dopo l'avvio: yt-dlp si aggiorna da solo, per gli altri resta un
        // promemoria in Impostazioni › Aggiornamenti (un installatore non parte mai senza richiesta).
        /// <summary>
        /// Al primo avvio di sempre, a libreria visibile, propone di collegare il telefono come telecomando.
        /// Chi aggiorna da una versione precedente non lo vede: per lui non e' il primo avvio.
        /// </summary>
        private void ScheduleFirstRunRemoteOffer()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
                string marker = Path.Combine(folder, "remote-offer.txt");
                if (File.Exists(marker)) return;
                bool olderInstall = false;
                try
                {
                    foreach (string name in new[] { "extras.json", "cinematicLibraryRoots.json", "libraryRoots.json" })
                    {
                        string existing = Path.Combine(folder, name);
                        if (File.Exists(existing) && DateTime.UtcNow - File.GetCreationTimeUtc(existing) > TimeSpan.FromMinutes(20)) olderInstall = true;
                    }
                }
                catch { }
                try { Directory.CreateDirectory(folder); File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); } catch { }
                if (olderInstall) return;

                var timer = new System.Windows.Forms.Timer { Interval = 2500 };
                int tries = 0;
                timer.Tick += (_, _) =>
                {
                    if (IsDisposed || _closingForExit || ++tries > 60) { timer.Stop(); timer.Dispose(); return; }
                    // Si aspetta che la libreria sia a schermo e che non ci sia altro davanti.
                    if (_remote == null || _engine != null || _videoLoading?.Visible == true || SheetPresenter.AnyOpen ||
                        _cinematicLibraryPage?.Visible != true || !IsCinecoreForeground()) return;
                    timer.Stop(); timer.Dispose();
                    bool wanted;
                    using (SheetPresenter.Layout(this))
                    using (var offer = new RemoteOfferForm(UiEnglish))
                        wanted = SheetPresenter.ShowDialog(offer, this) == DialogResult.OK;
                    if (wanted && _remote != null) ShowPairingBanner(_remote.CurrentPin);
                };
                timer.Start();
            }
            catch (Exception ex) { Dbg.Warn("[REMOTE] first-run offer: " + ex.Message); }
        }

        private void ScheduleStartupComponentCheck()
        {
            try
            {
                if (!AppUpdate.State.AutoCheck || DateTime.UtcNow - ComponentUpdates.State.LastCheckUtc < TimeSpan.FromHours(20)) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                        var pending = await ComponentUpdates.RunStartupAsync(CancellationToken.None).ConfigureAwait(false);
                        if (pending.Count > 0) Dbg.Log("[COMPONENTS] available: " + string.Join(", ", pending), Dbg.LogLevel.Info);
                    }
                    catch (Exception ex) { Dbg.Warn("[COMPONENTS] check: " + ex.Message); }
                });
            }
            catch (Exception ex) { Dbg.Warn("[COMPONENTS] " + ex.Message); }
        }
    }
}
