#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Scheda "Componenti esterni": versione installata e ultima disponibile di yt-dlp, LAV Filters e
    // MakeMKV. yt-dlp si aggiorna sul posto; gli altri scaricano l'installatore originale e lo avviano.
    internal sealed class ComponentUpdatesForm : HudModalFormBase
    {
        private readonly bool _english;
        private readonly Func<bool> _playbackActive;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Button _close;
        private readonly Dictionary<string, Button> _actions = new();
        private readonly string[] _order = { ComponentUpdates.YtDlp, ComponentUpdates.Lav, ComponentUpdates.MakeMkv };
        private List<ComponentUpdates.Info> _infos = new();
        private bool _checked, _busy;
        private string _status = "";
        private bool? _statusOk;
        private double _progress = -1;

        private const int RowTop = 76, RowHeight = 84;

        public ComponentUpdatesForm(bool english, Func<bool> playbackActive)
        {
            _english = english;
            _playbackActive = playbackActive;
            Text = T("Componenti esterni", "External components");
            ClientSize = new Size(680, RowTop + RowHeight * _order.Length + 130);
            MinimumSize = Size;
            MaximumSize = Size;
            _close = CreateModalButton(T("Chiudi", "Close"), DialogResult.Cancel, primary: false);
            _close.SetBounds(ClientSize.Width - 30 - 100, ClientSize.Height - 58, 100, 38);
            Controls.Add(_close);
            for (int i = 0; i < _order.Length; i++)
            {
                string id = _order[i];
                var button = CreateModalButton(T("Aggiorna", "Update"), DialogResult.None, primary: true);
                button.SetBounds(ClientSize.Width - 30 - 130, RowTop + i * RowHeight + 14, 130, 36);
                button.Visible = false;
                button.Click += (_, _) => Apply(id);
                _actions[id] = button;
                Controls.Add(button);
            }
            CancelButton = _close;
            Shown += (_, _) => Check();
        }

        private string T(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);

        private string Description(string id) => id switch
        {
            ComponentUpdates.YtDlp => T("YouTube e video dal web. Si aggiorna da solo.", "YouTube and web video. Updates itself."),
            ComponentUpdates.Lav => T("Decoder audio e video per madVR, MPC VR ed EVR.", "Audio and video decoders for madVR, MPC VR and EVR."),
            _ => T("Blu-ray protetti: il player usa la sua libreria se è installato.", "Protected Blu-rays: the player uses its library when installed.")
        };

        private async void Check()
        {
            Say(T("Cerco le ultime versioni…", "Looking for the latest versions…"), null);
            try
            {
                var token = _lifetime.Token;
                var infos = await Task.Run(() => ComponentUpdates.CheckAllAsync(token), token);
                if (IsDisposed) return;
                _infos = infos;
                _checked = true;
                int failed = infos.Count(info => info.Latest == null);
                Say(failed == infos.Count ? T("Nessun sito raggiungibile", "No site could be reached") : "", failed == infos.Count ? false : null);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _checked = true;
                Say(ex.Message, false);
            }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            foreach (string id in _order)
            {
                var info = _infos.FirstOrDefault(entry => entry.Id == id);
                var button = _actions[id];
                button.Visible = info is { UpdateAvailable: true };
                button.Enabled = !_busy;
                button.Text = info?.Installed == null ? T("Installa", "Install") : T("Aggiorna", "Update");
            }
            Invalidate();
        }

        private async void Apply(string id)
        {
            var info = _infos.FirstOrDefault(entry => entry.Id == id);
            if (_busy || info == null || !info.UpdateAvailable) return;
            // Un installatore non puo' sostituire filtri che stanno riproducendo.
            if (!info.Silent && _playbackActive())
            {
                Say(T("Chiudi il film o la musica prima di installare", "Close the film or the music before installing"), false);
                return;
            }
            _busy = true;
            _progress = 0;
            Say(string.Format(T("Scarico {0} {1}…", "Downloading {0} {1}…"), info.Name, info.Latest), null);
            UpdateButtons();
            try
            {
                var token = _lifetime.Token;
                long lastReport = 0;
                var progress = new Progress<(long Done, long Total)>(value =>
                {
                    if (IsDisposed || Environment.TickCount64 - lastReport < 80 && value.Done < value.Total) return;
                    lastReport = Environment.TickCount64;
                    _progress = value.Total > 0 ? value.Done / (double)value.Total : 0;
                    Invalidate(new Rectangle(0, ClientSize.Height - 120, ClientSize.Width, 56));
                });
                var outcome = await Task.Run(() => ComponentUpdates.ApplyAsync(info, progress, token), token);
                if (IsDisposed) return;
                switch (outcome)
                {
                    case ComponentUpdates.Outcome.Updated:
                        Say(string.Format(T("{0} aggiornato alla versione {1}", "{0} updated to version {1}"), info.Name, info.Latest), true);
                        _infos = _infos.Select(entry => entry.Id == id ? entry with { Installed = info.Latest } : entry).ToList();
                        break;
                    case ComponentUpdates.Outcome.InstallerStarted:
                        Say(string.Format(T("Verificato. Segui l'installazione di {0}.", "Verified. Follow the {0} installer."), info.Name), true);
                        ComponentUpdates.State.Pending.RemoveAll(entry => entry.StartsWith(info.Name, StringComparison.Ordinal));
                        ComponentUpdates.SaveState();
                        break;
                    default:
                        Say(T("Consenso negato: nulla è stato installato", "Consent denied: nothing was installed"), false);
                        break;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                Dbg.Warn("[COMPONENTS] " + ex.Message);
                Say(T("Non riuscito", "Failed") + ": " + ex.Message, false);
            }
            _busy = false;
            _progress = -1;
            UpdateButtons();
        }

        private void Say(string text, bool? ok)
        {
            _status = text;
            _statusOk = ok;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 11f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

            int width = ClientSize.Width, height = ClientSize.Height;
            TextRenderer.DrawText(g, T("Componenti esterni", "External components"), titleFont, new Rectangle(30, 14, width - 60, 42), Theme.Text, Line);

            for (int i = 0; i < _order.Length; i++)
            {
                string id = _order[i];
                var info = _infos.FirstOrDefault(entry => entry.Id == id);
                int top = RowTop + i * RowHeight;
                int textWidth = width - 60 - 150;
                string name = info?.Name ?? id switch { ComponentUpdates.YtDlp => "yt-dlp", ComponentUpdates.Lav => "LAV Filters", _ => "MakeMKV" };
                string version = !_checked ? ""
                    : info == null ? ""
                    : info.Installed == null ? T("Non installato", "Not installed")
                    : info.Installed;
                if (_checked && info != null)
                {
                    if (info.UpdateAvailable) version += "  →  " + info.Latest;
                    else if (info.Latest != null && info.Installed != null) version += "     " + T("aggiornato", "up to date");
                    else if (info.Latest == null) version += "     " + T("controllo non riuscito", "check failed");
                }
                TextRenderer.DrawText(g, name, nameFont, new Rectangle(30, top, textWidth, 26), Theme.Text, Line);
                TextRenderer.DrawText(g, version, bodyFont, new Rectangle(30, top + 25, textWidth, 20), info is { UpdateAvailable: true } ? Theme.Text : Theme.SubtleText, Line);
                TextRenderer.DrawText(g, Description(id), bodyFont, new Rectangle(30, top + 44, textWidth, 20), Theme.Muted, Line);
            }

            if (_progress >= 0)
            {
                var track = new Rectangle(30, height - 80, width - 60, 2);
                using (var back = new SolidBrush(Color.FromArgb(Theme.IsLight ? 40 : 34, Theme.Text))) g.FillRectangle(back, track);
                using (var fill = new SolidBrush(Theme.Accent)) g.FillRectangle(fill, track.Left, track.Top, (int)(track.Width * Math.Clamp(_progress, 0, 1)), track.Height);
            }
            if (_status.Length > 0)
            {
                var status = new Rectangle(30, height - 112, width - 60, 24);
                Color dot = _statusOk == true ? Color.FromArgb(62, 190, 120) : _statusOk == false ? Theme.Danger : Color.FromArgb(170, Theme.Muted);
                using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, status.Left, status.Top + 8, 8, 8);
                TextRenderer.DrawText(g, _status, bodyFont, new Rectangle(status.Left + 18, status.Top, status.Width - 18, status.Height), Theme.SubtleText, Line);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _lifetime.Cancel(); } catch { }
                _lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
