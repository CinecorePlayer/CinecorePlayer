#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Scheda "Sottotitoli": cerca per il titolo in riproduzione, scarica quello scelto, lo
    // riallinea sull'audio del film e lo salva accanto al video.
    internal sealed class SubtitleDownloadForm : HudModalFormBase
    {
        // Come le altre schede: titolo e contenuto allineati a sinistra, pulsanti insieme in basso a destra.
        private const int RowHeight = 44, ListTop = 136, ListLeft = 30;
        // "Altre…" mostra tutte le lingue dentro la scheda, in colonne: niente menu a tendina ne' casella di ricerca.
        private bool _pickingLanguage;
        private readonly List<(Rectangle Bounds, string Code)> _languageGrid = new();
        // In testata: la lingua predefinita (Impostazioni › Generali), la seconda piu' usata e
        // "Altre…" con l'elenco completo. Prima c'erano solo italiano e inglese.
        private const string MoreLanguages = "more";
        private string _primaryLanguage = "it";
        private IEnumerable<(string Code, string Italian, string English)> Languages
        {
            get
            {
                string second = _primaryLanguage == "en" ? "it" : "en";
                foreach (string code in new[] { _primaryLanguage, second })
                    yield return (code, SubtitleSources.LanguageLabel(code, english: false), SubtitleSources.LanguageLabel(code, english: true));
                yield return (MoreLanguages, "Altre…", "More…");
            }
        }

        private void ShowAllLanguages(Point at)
        {
            _pickingLanguage = !_pickingLanguage;
            Invalidate();
        }

        private readonly bool _english;
        private readonly string _videoPath, _title;
        private readonly Func<string, CancellationToken, Task<SubtitleSources.Query>> _query;
        private readonly Button _account, _close;
        private CancellationTokenSource _searchCts = new();
        private readonly CancellationTokenSource _lifetime = new();
        private List<SubtitleSources.Candidate>? _results;
        private string _language;
        private int _scroll;
        private bool _busy, _closeHover;
        private bool? _statusOk;
        private string _status = "";
        private Point _mouse = new(-1, -1);
        private readonly List<(Rectangle Bounds, string Code)> _languageHits = new();

        /// <summary>File salvato (se l'utente ne ha scaricato uno): il player riapre il video per caricarlo.</summary>
        public string? SavedPath { get; private set; }
        /// <summary>Il file salvato ne ha sostituito uno che il renderer poteva avere gia' caricato.</summary>
        public bool Replaced { get; private set; }

        private Rectangle CloseBounds => new(ClientSize.Width - 30 - 32, 18, 32, 32);
        private int ListBottom => ClientSize.Height - 86;
        private int VisibleRows => Math.Max(1, (ListBottom - ListTop) / RowHeight);

        public SubtitleDownloadForm(string videoPath, string title, bool english, Func<string, CancellationToken, Task<SubtitleSources.Query>> query)
        {
            _videoPath = videoPath;
            _title = title;
            _english = english;
            _query = query;
            _language = _primaryLanguage = SubtitleSources.DefaultLanguage is { Length: > 0 } chosen ? chosen : english ? "en" : "it";
            Text = T("Sottotitoli", "Subtitles");
            ClientSize = new Size(760, 600);
            MinimumSize = Size;
            MaximumSize = Size;

            _account = CreateModalButton("OpenSubtitles…", DialogResult.None, primary: false);
            _close = CreateModalButton(T("Chiudi", "Close"), DialogResult.Cancel, primary: false);
            // I due pulsanti stanno insieme al centro (prima erano uno per angolo).
            const int accountWidth = 160, closeWidth = 110, gap = 10;
            _close.SetBounds(ClientSize.Width - 30 - closeWidth, ClientSize.Height - 58, closeWidth, 38);
            _account.SetBounds(_close.Left - gap - accountWidth, ClientSize.Height - 58, accountWidth, 38);
            _account.Click += (_, _) =>
            {
                using var dialog = new OpenSubtitlesAccountForm(_english);
                if (SheetPresenter.ShowDialog(dialog, this) == DialogResult.OK) Search();
            };
            Controls.Add(_account);
            Controls.Add(_close);
            CancelButton = _close;
            Shown += (_, _) => { ActiveControl = _close; Search(); };
        }

        private string T(string italian, string english) => _english ? english : italian;

        private async void Search()
        {
            _searchCts.Cancel();
            _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _searchCts.Token;
            _results = null;
            _scroll = 0;
            _status = "";
            _statusOk = null;
            Invalidate();
            List<SubtitleSources.Candidate> found = new();
            try
            {
                string language = _language;
                found = await Task.Run(async () =>
                {
                    var query = await _query(language, token).ConfigureAwait(false);
                    return await SubtitleSources.SearchAsync(query, token).ConfigureAwait(false);
                }, token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Dbg.Warn("[SUBS] search: " + ex.Message); }
            if (IsDisposed || token.IsCancellationRequested) return;
            _results = found;
            Invalidate();
        }

        private async void Download(SubtitleSources.Candidate candidate)
        {
            if (_busy) return;
            _busy = true;
            _statusOk = null;
            _status = T("Scarico…", "Downloading…");
            Invalidate();
            try
            {
                var token = _lifetime.Token;
                string text = await Task.Run(() => SubtitleSources.DownloadAsync(candidate, token), token);
                if (IsDisposed) return;
                var progress = new Progress<double>(value =>
                {
                    if (IsDisposed) return;
                    _status = string.Format(T("Allineo i tempi sull'audio del film… {0}%", "Aligning the timings to the film's audio… {0}%"), (int)Math.Round(value * 100));
                    Invalidate(StatusBounds);
                });
                _status = T("Allineo i tempi sull'audio del film…", "Aligning the timings to the film's audio…");
                Invalidate();
                var outcome = await Task.Run(() => SubtitleWorkflow.SaveDownloadedAsync(_videoPath, candidate.Language, text, progress, token), token);
                if (IsDisposed) return;
                SavedPath = outcome.Path;
                _statusOk = true;
                Replaced = outcome.Replaced;
                // Cartella del film in sola lettura: il file sta nella cartella del player, che lo carica da li'.
                _status = (outcome.InPlayerFolder ? T("Salvato nel player", "Saved in the player") : T("Salvato accanto al film", "Saved next to the film"))
                    + "     " + SubtitleWorkflow.Describe(outcome, _english);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _statusOk = false;
                _status = T("Non riuscito", "Failed") + ": " + ex.Message;
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) Invalidate();
            }
        }

        private Rectangle StatusBounds => new(ListLeft, ClientSize.Height - 58, ClientSize.Width - ListLeft * 2 - 300, 38);
        private Rectangle RowBounds(int visibleIndex) => new(ListLeft, ListTop + visibleIndex * RowHeight, ClientSize.Width - ListLeft * 2, RowHeight);

        private int RowAt(Point point)
        {
            if (_results == null || point.Y < ListTop || point.Y >= ListTop + VisibleRows * RowHeight || point.X < ListLeft || point.X > ClientSize.Width - ListLeft) return -1;
            int index = _scroll + (point.Y - ListTop) / RowHeight;
            return index < _results.Count ? index : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            bool close = false; // la scheda si chiude con "Chiudi" o Esc: niente X
            Cursor = close || (!_pickingLanguage && RowAt(e.Location) >= 0) || _languageGrid.Any(hit => hit.Bounds.Contains(e.Location)) || _languageHits.Any(hit => hit.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
            if (close != _closeHover) { _closeHover = close; Invalidate(Rectangle.Inflate(CloseBounds, 2, 2)); }
            Invalidate(new Rectangle(0, 90, ClientSize.Width, ListBottom - 86));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = new Point(-1, -1);
            _closeHover = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta), 0, Math.Max(0, (_results?.Count ?? 0) - VisibleRows));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_pickingLanguage)
                {
                    string? picked = _languageGrid.FirstOrDefault(hit => hit.Bounds.Contains(e.Location)).Code;
                    if (picked != null && !_busy) { _pickingLanguage = false; _primaryLanguage = picked; _language = picked; Search(); return; }
                }
                string? code = _languageHits.FirstOrDefault(hit => hit.Bounds.Contains(e.Location)).Code;
                if (code == MoreLanguages) { ShowAllLanguages(e.Location); return; }
                if (code != null && !_busy) { _pickingLanguage = false; if (code != _language) { _language = code; Search(); } else Invalidate(); return; }
                int index = _pickingLanguage ? -1 : RowAt(e.Location);
                if (index >= 0 && _results != null) { Download(_results[index]); return; }
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var switchFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.6f, FontStyle.Regular, GraphicsUnit.Point);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            using var smallFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 8.6f, FontStyle.Regular, GraphicsUnit.Point);

            int width = ClientSize.Width;
            TextRenderer.DrawText(g, T("Sottotitoli", "Subtitles"), titleFont, new Rectangle(30, 14, width - 60, 42), Theme.Text, Line);
            TextRenderer.DrawText(g, _title, bodyFont, new Rectangle(30, 54, width - 60, 22), Theme.Muted, Line);

            // Lingua: due voci di testo, quella attiva sottolineata.
            _languageHits.Clear();
            const int languageGap = 34;
            var labels = Languages.Select(language => (language.Code, Label: _english ? language.English : language.Italian)).ToList();
            var widths = labels.Select(entry => TextRenderer.MeasureText(g, entry.Label, switchFont, Size.Empty, TextFormatFlags.NoPadding).Width).ToList();
            int x = 30;
            for (int language = 0; language < labels.Count; language++)
            {
                string code = labels[language].Code, label = labels[language].Label;
                var size = new Size(widths[language], 24);
                var bounds = new Rectangle(x, 92, size.Width, 24);
                bool selected = code == MoreLanguages ? _pickingLanguage : !_pickingLanguage && code == _language, hover = bounds.Contains(_mouse);
                TextRenderer.DrawText(g, label, switchFont, bounds, selected ? Theme.Text : hover ? Theme.SubtleText : Theme.Muted, Line);
                if (selected)
                    using (var underline = new Pen(Theme.Accent, 2f)) g.DrawLine(underline, bounds.Left, bounds.Bottom + 1, bounds.Right, bounds.Bottom + 1);
                _languageHits.Add((Rectangle.Inflate(bounds, 10, 6), code));
                x += size.Width + languageGap;
            }

            _languageGrid.Clear();
            if (_pickingLanguage)
            {
                // Tutte le lingue, in ordine alfabetico, su quattro colonne: un clic sceglie.
                var all = SubtitleSources.AllLanguages.Select(language => (language.Code, Label: _english ? language.English : language.Italian)).OrderBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
                const int columns = 4, rowHeight = 28;
                // Colonne della stessa altezza: le lingue si dividono in parti uguali invece di riempire le prime tre.
                int perColumn = Math.Clamp((all.Count + columns - 1) / columns, 1, Math.Max(1, (ListBottom - ListTop) / rowHeight)), columnWidth = (width - 60) / columns;
                for (int i = 0; i < all.Count && i < perColumn * columns; i++)
                {
                    var cell = new Rectangle(30 + i / perColumn * columnWidth, ListTop + i % perColumn * rowHeight, columnWidth - 12, rowHeight);
                    bool current = all[i].Code == _language;
                    TextRenderer.DrawText(g, all[i].Label, current ? switchFont : nameFont, cell, current ? Theme.Accent : cell.Contains(_mouse) ? Theme.Text : Theme.SubtleText, Line);
                    _languageGrid.Add((cell, all[i].Code));
                }
            }
            else if (_results == null)
                TextRenderer.DrawText(g, T("Cerco i sottotitoli per questo titolo…", "Looking for subtitles for this title…"), bodyFont, new Rectangle(ListLeft, ListTop + 4, width - ListLeft * 2, 26), Theme.Muted, Line);
            else if (_results.Count == 0)
                TextRenderer.DrawText(g, T("Nessun sottotitolo trovato in questa lingua.\nProva l'altra lingua, oppure collega il tuo account OpenSubtitles.",
                        "No subtitles found in this language.\nTry the other language, or connect your OpenSubtitles account."),
                    bodyFont, new Rectangle(ListLeft, ListTop + 4, width - ListLeft * 2, 60), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            else
            {
                int hover = _busy ? -1 : RowAt(_mouse);
                using var rule = new Pen(Color.FromArgb(Theme.IsLight ? 34 : 24, Theme.Text));
                for (int visible = 0; visible < VisibleRows && visible + _scroll < _results.Count; visible++)
                {
                    int index = visible + _scroll;
                    var item = _results[index];
                    Rectangle row = RowBounds(visible);
                    if (visible > 0) g.DrawLine(rule, row.Left, row.Top, row.Right, row.Top);
                    // Una riga sola: il nome a sinistra, fonte e voto a destra.
                    int detailWidth = Math.Min(row.Width * 2 / 5, 250);
                    TextRenderer.DrawText(g, item.Name, nameFont, new Rectangle(row.Left, row.Top, row.Width - detailWidth - 16, row.Height), index == hover ? Theme.Accent : _busy ? Theme.Muted : Theme.Text, Line);
                    TextRenderer.DrawText(g, (item.Source + "     " + item.Detail).Replace(" · ", "     "), smallFont, new Rectangle(row.Right - detailWidth, row.Top, detailWidth, row.Height), Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
                if (_results.Count > VisibleRows)
                {
                    var track = new Rectangle(width - 16, ListTop, 3, VisibleRows * RowHeight);
                    int thumb = Math.Max(24, track.Height * VisibleRows / _results.Count);
                    int top = track.Top + (track.Height - thumb) * _scroll / Math.Max(1, _results.Count - VisibleRows);
                    ScrollbarChrome.Draw(g, track, top, thumb);
                }
            }

            Rectangle status = StatusBounds;
            if (_status.Length > 0)
            {
                Color dot = _statusOk == true ? Color.FromArgb(62, 190, 120) : _statusOk == false ? Theme.Danger : Color.FromArgb(170, Theme.Muted);
                using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, status.Left, status.Top + status.Height / 2 - 4, 8, 8);
                TextRenderer.DrawText(g, _status, bodyFont, new Rectangle(status.Left + 18, status.Top, status.Width - 18, status.Height), Theme.SubtleText, Line);
            }
            else if (_results is { Count: > 0 } && !_pickingLanguage)
                TextRenderer.DrawText(g, T("Un clic lo scarica e lo riallinea sull'audio", "A click downloads it and aligns it to the audio"),
                    bodyFont, status, Theme.Muted, Line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _lifetime.Cancel(); } catch { }
                _searchCts.Dispose();
                _lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Account OpenSubtitles dell'utente: chiave API e, se vuole usare il proprio piano, nome e password.
    internal sealed class OpenSubtitlesAccountForm : HudModalFormBase
    {
        private readonly bool _english;
        private readonly UnderlineTextBox _key, _user, _password;

        public OpenSubtitlesAccountForm(bool english)
        {
            _english = english;
            var account = OpenSubtitlesAccount.Load();
            Text = "OpenSubtitles";
            ClientSize = new Size(600, 396);
            MinimumSize = Size;
            MaximumSize = Size;
            _key = new UnderlineTextBox(account?.ApiKey ?? "", T("la chiave API del tuo account", "your account's API key"));
            _user = new UnderlineTextBox(account?.User ?? "", T("facoltativo", "optional"));
            _password = new UnderlineTextBox(account?.Password ?? "", T("facoltativa", "optional"), password: true);
            _key.SetBounds(30, 136, 540, 36);
            _user.SetBounds(30, 206, 260, 36);
            _password.SetBounds(310, 206, 260, 36);
            Button cancel = CreateModalButton(T("Annulla", "Cancel"), DialogResult.Cancel, primary: false);
            Button save = CreateModalButton(T("Salva", "Save"), DialogResult.OK, primary: true);
            cancel.SetBounds(366, 338, 96, 38);
            save.SetBounds(472, 338, 98, 38);
            save.Click += (_, _) => OpenSubtitlesAccount.Save(_key.Box.Text, _user.Box.Text, _password.Box.Text);
            Controls.AddRange(new Control[] { _key, _user, _password, cancel, save });
            AcceptButton = save;
            CancelButton = cancel;
        }

        private string T(string italian, string english) => _english ? english : italian;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var labelFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 7.6f, FontStyle.Regular, GraphicsUnit.Point);
            TextRenderer.DrawText(g, "OpenSubtitles", titleFont, new Rectangle(30, 14, 540, 42), Theme.Text, Line);
            TextRenderer.DrawText(g, T("Facoltativo. Il player cerca già da fonti gratuite; con il tuo account si aggiunge il catalogo di OpenSubtitles e i download contano sul tuo piano.",
                    "Optional. The player already searches free sources; with your account the OpenSubtitles catalogue is added and downloads count against your plan."),
                bodyFont, new Rectangle(30, 54, 540, 44), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, T("CHIAVE API", "API KEY"), labelFont, new Rectangle(30, 118, 200, 16), Theme.Muted, Line);
            TextRenderer.DrawText(g, T("UTENTE", "USER"), labelFont, new Rectangle(30, 188, 200, 16), Theme.Muted, Line);
            TextRenderer.DrawText(g, "PASSWORD", labelFont, new Rectangle(310, 188, 200, 16), Theme.Muted, Line);
            TextRenderer.DrawText(g, T("La chiave si crea su opensubtitles.com, nella sezione \"API consumers\" del profilo. Chiave e password restano su questo PC, cifrate. Lascia vuota la chiave per scollegare l'account.",
                    "Create the key on opensubtitles.com, under \"API consumers\" in your profile. Key and password stay on this PC, encrypted. Leave the key empty to disconnect the account."),
                bodyFont, new Rectangle(30, 262, 540, 60), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }
}
