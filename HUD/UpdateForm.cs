#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Scheda "Aggiornamento": cerca l'ultima release, mostra le novita' e, su richiesta,
    // scarica l'installer, lo verifica e lo avvia. Se l'installazione parte, InstallStarted e' true
    // e il player si chiude per lasciarsi sostituire.
    internal sealed class UpdateForm : HudModalFormBase
    {
        private readonly bool _english;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Button _install, _later, _skip;
        private AppUpdate.Release? _release;
        private AppUpdate.PreparedInstaller? _prepared;
        private bool _checked, _busy;
        private string _status = "", _notes = "";
        private bool? _statusOk;
        private double _progress = -1;
        // Le note si leggono per intero prima di scaricare: se non entrano, scorrono.
        private Rectangle _notesRect;
        private int _notesScroll, _notesHeight;

        public bool InstallStarted { get; private set; }


        /// <param name="release">Release gia' trovata dal controllo automatico; null per cercarla all'apertura.</param>
        public UpdateForm(bool english, AppUpdate.Release? release)
        {
            _english = english;
            Text = T("Aggiornamento", "Update");
            ClientSize = new Size(680, release != null && release.Version > AppUpdate.Current ? FullHeight : CompactHeight);
            _install = CreateModalButton(T("Aggiorna ora", "Update now"), DialogResult.None, primary: true);
            _later = CreateModalButton(T("Più tardi", "Later"), DialogResult.Cancel, primary: false);
            _skip = CreateModalButton(T("Salta questa versione", "Skip this version"), DialogResult.None, primary: false);
            _install.SetBounds(ClientSize.Width - 30 - 140, ClientSize.Height - 58, 140, 38);
            _later.SetBounds(_install.Left - 10 - 100, ClientSize.Height - 58, 100, 38);
            _skip.SetBounds(30, ClientSize.Height - 58, 190, 38);
            _install.Click += (_, _) => Install();
            _skip.Click += (_, _) =>
            {
                if (_release == null) return;
                AppUpdate.State.SkippedVersion = _release.Version.ToString();
                AppUpdate.SaveState();
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.AddRange(new Control[] { _install, _later, _skip });
            CancelButton = _later;
            if (release != null) Show(release);
            else Shown += (_, _) => Check();
            UpdateButtons();
        }

        private string T(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);

        private void Show(AppUpdate.Release? release)
        {
            _checked = true;
            _release = release != null && release.Version > AppUpdate.Current ? release : null;
            _notes = _release == null ? "" : PlainNotes(_release.Notes);
        }

        // Le note sono Markdown scritto da chi pubblica: qui si mostrano come testo semplice.
        private static string PlainNotes(string markdown)
        {
            string text = Regex.Replace(markdown.Replace("\r", ""), @"!\[[^\]]*\]\([^)]*\)", "");
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
            text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s*", "");
            text = Regex.Replace(text, @"(?m)^\s*[-*]\s+", "•  ");
            text = Regex.Replace(text, @"[*_`]{1,3}", "");
            text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
            return text.Length > 8000 ? text[..8000].TrimEnd() + "…" : text;
        }

        private void ScrollNotes(int pixels)
        {
            int next = Math.Clamp(_notesScroll + pixels, 0, Math.Max(0, _notesHeight - _notesRect.Height));
            if (next == _notesScroll) return;
            _notesScroll = next;
            Invalidate(Rectangle.Inflate(_notesRect, 20, 2));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollNotes(-e.Delta * 54 / 120);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.PageDown or Keys.PageUp)
            {
                ScrollNotes((e.KeyCode == Keys.PageDown ? 1 : -1) * Math.Max(40, _notesRect.Height - 40));
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        // Con un aggiornamento da leggere la scheda e' alta (note di versione); senza, resta bassa:
        // prima era una scheda grande e vuota con una riga al centro.
        private const int FullHeight = 580, CompactHeight = 196;

        private void FitHeight()
        {
            int wanted = _release != null ? FullHeight : CompactHeight;
            if (ClientSize.Height != wanted)
            {
                int grow = wanted - ClientSize.Height;
                MinimumSize = MaximumSize = Size.Empty;
                ClientSize = new Size(ClientSize.Width, wanted);
                if (IsHandleCreated) Top = Math.Max(0, Top - grow / 2);
            }
            MinimumSize = MaximumSize = Size;
            int y = ClientSize.Height - 58;
            _install.Top = _later.Top = _skip.Top = y;
        }

        private void UpdateButtons()
        {
            bool available = _release != null;
            FitHeight();
            _install.Visible = available;
            _skip.Visible = available && !_busy;
            _install.Enabled = !_busy;
            _later.Text = available ? T("Più tardi", "Later") : T("Chiudi", "Close");
            _later.Left = available ? _install.Left - 10 - _later.Width : ClientSize.Width - 30 - _later.Width;
            Invalidate();
        }

        private async void Check()
        {
            Say(T("Cerco l'ultima versione…", "Looking for the latest version…"), null);
            try
            {
                var token = _lifetime.Token;
                var release = await Task.Run(() => AppUpdate.LatestAsync(token), token);
                if (IsDisposed) return;
                AppUpdate.State.LastCheckUtc = DateTime.UtcNow;
                AppUpdate.SaveState();
                Show(release);
                Say("", null);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _checked = true;
                Say(T("GitHub non raggiungibile", "GitHub is unreachable") + ": " + ex.Message, false);
            }
            UpdateButtons();
        }

        private async void Install()
        {
            if (_busy || _release == null) return;
            _busy = true;
            _progress = 0;
            Say(T("Scarico l'aggiornamento…", "Downloading the update…"), null);
            UpdateButtons();
            try
            {
                var token = _lifetime.Token;
                var release = _release;
                long lastReport = 0;
                var progress = new Progress<(long Done, long Total)>(value =>
                {
                    if (IsDisposed || Environment.TickCount64 - lastReport < 80 && value.Done < value.Total) return;
                    lastReport = Environment.TickCount64;
                    _progress = value.Total > 0 ? value.Done / (double)value.Total : 0;
                    _status = string.Format(T("Scarico l'aggiornamento…  {0:0} di {1:0} MB", "Downloading the update…  {0:0} of {1:0} MB"), value.Done / 1048576.0, value.Total / 1048576.0);
                    Invalidate(new Rectangle(0, ClientSize.Height - 120, ClientSize.Width, 56));
                });
                _prepared = await Task.Run(() => AppUpdate.DownloadAsync(release, progress, token), token);
                if (IsDisposed) return;
                _progress = 1;
                Say(T("Verificato. Avvio l'installazione: Windows chiederà il consenso.", "Verified. Starting the installation: Windows will ask for consent."), true);
                if (AppUpdate.Launch(_prepared))
                {
                    InstallStarted = true;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                Say(T("Consenso negato: l'aggiornamento non è stato installato", "Consent denied: the update was not installed"), false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                Dbg.Warn("[UPDATE] " + ex.Message);
                Say(T("Aggiornamento non riuscito", "Update failed") + ": " + ex.Message, false);
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
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var leadFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Point);

            int width = ClientSize.Width, height = ClientSize.Height;
            TextRenderer.DrawText(g, T("Aggiornamento", "Update"), titleFont, new Rectangle(30, 14, width - 60, 42), Theme.Text, Line);
            string current = AppUpdate.Current.ToString(3);

            if (_release != null)
            {
                TextRenderer.DrawText(g, string.Format(T("Hai la versione {0}", "You have version {0}"), current), bodyFont, new Rectangle(30, 54, width - 60, 26), Theme.Muted, Line);
                string size = _release.AssetSize > 0 ? string.Format("     {0:0} MB", _release.AssetSize / 1048576.0) : "";
                TextRenderer.DrawText(g, string.Format(T("È disponibile Cinecore Player {0}", "Cinecore Player {0} is available"), _release.Version.ToString(3)) + size, leadFont, new Rectangle(30, 92, width - 60, 38), Theme.Text, Line);
                const TextFormatFlags Body = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                var notes = new Rectangle(30, 146, width - 60 - 14, height - 146 - 132);
                string text = _notes.Length > 0 ? _notes : T("Nessuna nota per questa versione.", "No notes for this version.");
                _notesRect = notes;
                _notesHeight = TextRenderer.MeasureText(g, text, bodyFont, new Size(notes.Width, int.MaxValue), Body).Height;
                _notesScroll = Math.Clamp(_notesScroll, 0, Math.Max(0, _notesHeight - notes.Height));
                var state = g.Save();
                g.SetClip(notes);
                TextRenderer.DrawText(g, text, bodyFont, new Rectangle(notes.Left, notes.Top - _notesScroll, notes.Width, _notesHeight + 4), Theme.SubtleText,
                    Body | TextFormatFlags.PreserveGraphicsClipping);
                g.Restore(state);
                if (_notesHeight > notes.Height)
                {
                    // Cursore sottile: dice che c'e' altro da leggere e dove si e'.
                    var track = new Rectangle(notes.Right + 9, notes.Top, 3, notes.Height);
                    int thumb = Math.Max(28, notes.Height * notes.Height / _notesHeight);
                    int top = track.Top + (track.Height - thumb) * _notesScroll / Math.Max(1, _notesHeight - notes.Height);
                    using var rail = new SolidBrush(Color.FromArgb(Theme.IsLight ? 30 : 24, Theme.Text));
                    using var grip = new SolidBrush(Color.FromArgb(Theme.IsLight ? 120 : 110, Theme.Text));
                    g.FillRectangle(rail, track);
                    g.FillRectangle(grip, track.Left, top, track.Width, thumb);
                }
            }
            else
            {
                string text = !_checked ? "" : _statusOk == false ? "" : string.Format(T("Hai già l'ultima versione ({0})", "You already have the latest version ({0})"), current);
                TextRenderer.DrawText(g, text, bodyFont, new Rectangle(30, 58, width - 60, 26), Theme.SubtleText, Line);
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
                // Se l'installazione e' partita il file resta bloccato finche' il player non si chiude.
                if (!InstallStarted) _prepared?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
