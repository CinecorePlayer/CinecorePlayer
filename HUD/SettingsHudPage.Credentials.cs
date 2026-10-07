#nullable enable
using System;
using System.Drawing;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025.HUD
{
    internal sealed partial class SettingsHudPage
    {
        private bool _tmdbFieldsExpanded, _spotifyFieldsExpanded, _tidalFieldsExpanded;
        private TextBox? _tmdbKeyInput, _spotifyIdInput, _spotifySecretInput, _tidalIdInput, _tidalSecretInput;
        private string _credentialMessageKey = string.Empty, _credentialMessage = string.Empty;
        private bool _credentialMessageIsError;
        private int _generalMeasuredHeight;
        public event Action? CredentialsSaved;

        public void ExpandCredentialFields(bool spotify, bool tidal = false)
        {
            TextBox Input(bool secret, string key)
            {
                var box = new TextBox { BorderStyle = BorderStyle.None, UseSystemPasswordChar = secret, Visible = false, Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.5f) };
                box.GotFocus += (_, _) => Invalidate();
                box.LostFocus += (_, _) => Invalidate();
                box.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter) { SaveCredentials(key); e.SuppressKeyPress = true; }
                    else if (e.KeyCode == Keys.Escape) { CollapseCredentials(key); e.SuppressKeyPress = true; }
                };
                Controls.Add(box);
                return box;
            }

            if (tidal)
            {
                if (_tidalIdInput == null)
                {
                    _tidalIdInput = Input(false, "tidal");
                    _tidalSecretInput = Input(true, "tidal");
                }
                _tidalIdInput.Text = TidalArtistArtworkService.ConfiguredClientId ?? "";
                _tidalSecretInput!.Clear();
                _tidalFieldsExpanded = !_tidalFieldsExpanded;
                if (_tidalFieldsExpanded) BeginInvokeFocus(_tidalIdInput);
            }
            else if (spotify)
            {
                if (_spotifyIdInput == null)
                {
                    _spotifyIdInput = Input(false, "spotify");
                    _spotifySecretInput = Input(true, "spotify");
                }
                _spotifyIdInput.Text = SpotifyArtistArtworkService.ConfiguredClientId ?? "";
                _spotifySecretInput!.Clear();
                _spotifyFieldsExpanded = !_spotifyFieldsExpanded;
                if (_spotifyFieldsExpanded) BeginInvokeFocus(_spotifyIdInput);
            }
            else
            {
                _tmdbKeyInput ??= Input(true, "tmdb");
                _tmdbKeyInput.Text = MovieMetadataService.GetUserTmdbApiKey() ?? "";
                _tmdbFieldsExpanded = !_tmdbFieldsExpanded;
                if (_tmdbFieldsExpanded) BeginInvokeFocus(_tmdbKeyInput);
            }
            SetCredentialMessage(string.Empty, string.Empty, false);
            Invalidate();
        }

        private void BeginInvokeFocus(Control control)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { if (control.Visible) control.Focus(); })); } catch { }
        }

        private void CollapseCredentials(string key)
        {
            if (key == "tidal") _tidalFieldsExpanded = false; else if (key == "spotify") _spotifyFieldsExpanded = false; else _tmdbFieldsExpanded = false;
            SetCredentialMessage(string.Empty, string.Empty, false);
            HideCredentialInputs();
            Focus();
            Invalidate();
        }

        private void SetCredentialMessage(string key, string text, bool error)
        {
            _credentialMessageKey = key;
            _credentialMessage = text;
            _credentialMessageIsError = error;
        }

        private void SaveCredentials(string key)
        {
            try
            {
                if (key == "tidal")
                {
                    string id = _tidalIdInput?.Text.Trim() ?? "";
                    string secret = _tidalSecretInput?.Text.Trim() ?? "";
                    if (id.Length == 0)
                    {
                        SetCredentialMessage(key, L("Inserisci il Client ID.", "Enter the Client ID."), true);
                        Invalidate();
                        return;
                    }
                    TidalArtistArtworkService.Configure(id, secret);
                    _tidalSecretInput?.Clear();
                    SetCredentialMessage(key, L("Credenziali salvate · verifica in corso…", "Credentials saved · checking…"), false);
                    // TIDAL dice subito se le accetta: senza questa prova un errore di battitura
                    // si scoprirebbe solo dalle foto che non arrivano.
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        bool accepted = await TidalArtistArtworkService.TestAsync().ConfigureAwait(false);
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                SetCredentialMessage("tidal", accepted ? L("Credenziali accettate da TIDAL.", "Credentials accepted by TIDAL.") : L("TIDAL non accetta queste credenziali.", "TIDAL rejects these credentials."), !accepted);
                                Invalidate();
                            }));
                        }
                        catch { }
                    });
                }
                else if (key == "spotify")
                {
                    string id = _spotifyIdInput?.Text.Trim() ?? "";
                    string secret = _spotifySecretInput?.Text.Trim() ?? "";
                    if (id.Length == 0)
                    {
                        SetCredentialMessage(key, L("Inserisci il Client ID.", "Enter the Client ID."), true);
                        Invalidate();
                        return;
                    }
                    SpotifyArtistArtworkService.Configure(id, secret);
                    _spotifySecretInput?.Clear();
                    SetCredentialMessage(key, L("Credenziali salvate.", "Credentials saved."), false);
                }
                else
                {
                    MovieMetadataService.SetUserTmdbApiKey(_tmdbKeyInput?.Text.Trim());
                    SetCredentialMessage(key, MovieMetadataService.HasCustomTmdbApiKey
                        ? L("Chiave personale salvata.", "Personal key saved.")
                        : L("In uso la chiave integrata.", "Using the built-in key."), false);
                }
                CredentialsSaved?.Invoke();
            }
            catch (ArgumentException)
            {
                SetCredentialMessage(key, L("Inserisci anche il Client Secret.", "Enter the Client Secret as well."), true);
            }
            catch
            {
                SetCredentialMessage(key, L("Salvataggio non riuscito.", "Could not save."), true);
            }
            Invalidate();
        }

        private void RemoveCredentials(string key)
        {
            try
            {
                if (key == "tidal")
                {
                    TidalArtistArtworkService.Configure(null, null);
                    if (_tidalIdInput != null) _tidalIdInput.Text = "";
                    _tidalSecretInput?.Clear();
                    SetCredentialMessage(key, L("Credenziali rimosse.", "Credentials removed."), false);
                }
                else if (key == "spotify")
                {
                    SpotifyArtistArtworkService.Configure(null, null);
                    if (_spotifyIdInput != null) _spotifyIdInput.Text = "";
                    _spotifySecretInput?.Clear();
                    SetCredentialMessage(key, L("Credenziali rimosse.", "Credentials removed."), false);
                }
                else
                {
                    MovieMetadataService.SetUserTmdbApiKey(null);
                    if (_tmdbKeyInput != null) _tmdbKeyInput.Text = "";
                    SetCredentialMessage(key, L("In uso la chiave integrata.", "Using the built-in key."), false);
                }
                CredentialsSaved?.Invoke();
            }
            catch
            {
                SetCredentialMessage(key, L("Operazione non riuscita.", "Operation failed."), true);
            }
            Invalidate();
        }

        /// <summary>
        /// One metadata service as a plain settings row (no card): name and description on the
        /// left, status text and a Configure/Close action on the right. When open, the fields
        /// follow as underlined inputs, like every other input in Settings.
        /// </summary>
        private int DrawCredentialCard(Graphics g, Rectangle area, string key, string title, string description,
            string status, bool active, bool expanded, (string Label, TextBox? Input, string Hint)[] fields, bool canRemove, string removeLabel)
        {
            using var label = UiFont("Segoe UI", 10.5f);
            using var caption = UiFont("Segoe UI", 9f);
            using var small = UiFont("Segoe UI Semibold", 8.4f);
            const int headerH = 66;
            int controlW = Math.Min(330, Math.Max(210, area.Width * 2 / 5));
            int controlX = area.Right - controlW;
            int top = area.Top;

            TextRenderer.DrawText(g, title, label, new Rectangle(area.Left, top, Math.Max(90, controlX - area.Left - 28), 30), TextColor, SettingsText);
            TextRenderer.DrawText(g, description, caption, new Rectangle(area.Left, top + 29, Math.Max(90, controlX - area.Left - 28), 23), Muted, SettingsText);

            // Right side: status (dot + text) and the action as a text link.
            var action = new Rectangle(area.Right - 104, top + 1, 104, 36);
            DrawTextAction(g, action, expanded ? L("Chiudi", "Close") : L("Configura", "Configure"), "");
            _hits.Add(key == "tidal"
                ? new Hit { Bounds = action, Kind = HitKind.GeneralChoice, Key = "tidal-credentials" }
                : new Hit { Bounds = action, Kind = key == "spotify" ? HitKind.SpotifyCredentials : HitKind.TmdbApiKey });
            Color statusColor = active ? Color.FromArgb(72, 199, 142) : Theme.Muted;
            Size statusSize = TextRenderer.MeasureText(g, status, caption, Size.Empty, TextFormatFlags.NoPadding);
            int statusRight = action.Left - 18;
            var statusRect = new Rectangle(statusRight - statusSize.Width, top + 1, statusSize.Width, 36);
            using (var dot = new SolidBrush(statusColor))
                g.FillEllipse(dot, statusRect.Left - 14, statusRect.Top + statusRect.Height / 2 - 3, 7, 7);
            TextRenderer.DrawText(g, status, caption, statusRect, active ? statusColor : Theme.SubtleText, SettingsText);

            if (!expanded) return headerH;

            int y = top + headerH;
            bool wide = area.Width >= 620 && fields.Length == 2;
            int gap = 28;
            int columnW = wide ? (area.Width - gap) / 2 : area.Width;
            for (int i = 0; i < fields.Length; i++)
            {
                int x = area.Left + (wide ? i * (columnW + gap) : 0);
                TextRenderer.DrawText(g, fields[i].Label.ToUpperInvariant(), small, new Rectangle(x, y, columnW, 18), Theme.SubtleText, SettingsText);
                PlaceCredentialInput(g, new Rectangle(x, y + 20, columnW, 36), fields[i].Input);
                TextRenderer.DrawText(g, fields[i].Hint, caption, new Rectangle(x, y + 60, columnW, 20), Muted, SettingsText);
                if (!wide || i == fields.Length - 1) y += 88;
            }

            var save = new Rectangle(area.Right - 112, y + 2, 112, 36);
            DrawActionButton(g, save, L("Salva", "Save"), "", primary: true);
            _hits.Add(new Hit { Bounds = save, Kind = HitKind.CredentialSave, Key = key });
            int messageRight = save.Left - 16;
            if (canRemove)
            {
                using var font = UiFont("Segoe UI Semibold", 9.6f);
                int w = TextRenderer.MeasureText(g, removeLabel, font, Size.Empty, TextFormatFlags.NoPadding).Width + 24;
                var remove = new Rectangle(save.Left - 12 - w, save.Top, w, 36);
                bool hover = remove.Contains(_lastMouse);
                TextRenderer.DrawText(g, removeLabel, font, remove, hover ? Theme.Danger : Theme.SubtleText, SettingsText | TextFormatFlags.HorizontalCenter);
                _hits.Add(new Hit { Bounds = remove, Kind = HitKind.CredentialRemove, Key = key });
                messageRight = remove.Left - 12;
            }
            if (_credentialMessageKey == key && _credentialMessage.Length > 0)
            {
                Color color = _credentialMessageIsError ? Theme.Danger : Color.FromArgb(72, 199, 142);
                TextRenderer.DrawText(g, _credentialMessage, caption, new Rectangle(area.Left, save.Top, Math.Max(1, messageRight - area.Left), 36), color, SettingsText);
            }
            return save.Bottom + 22 - top;
        }

        private void PlaceCredentialInput(Graphics g, Rectangle bounds, TextBox? control)
        {
            if (control == null) return;
            // Draw inside the bounds: a frame on the exact edge was clipped on the right.
            // Box-less input: a baseline only (accent while focused), text on the page colour.
            bool focused = control.Focused;
            using (var line = new Pen(focused ? Accent : Color.FromArgb(110, Border), focused ? 2f : 1f))
                g.DrawLine(line, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            var physical = bounds;
            physical.Offset(0, -_contentScroll);
            control.BackColor = Back;
            control.ForeColor = TextColor;
            control.Bounds = new Rectangle(physical.Left, physical.Bottom - control.PreferredHeight - 6,
                Math.Max(1, physical.Width), control.PreferredHeight);
            control.Visible = _contentViewport.Contains(physical);
            if (control.Visible) control.BringToFront();
        }

        private void HideCredentialInputs()
        {
            bool general = string.Equals(_tab, "Generali", StringComparison.OrdinalIgnoreCase);
            if (_tmdbKeyInput != null && (!general || !_tmdbFieldsExpanded)) _tmdbKeyInput.Visible = false;
            foreach (var control in new Control?[] { _spotifyIdInput, _spotifySecretInput })
                if (control != null && (!general || !_spotifyFieldsExpanded)) control.Visible = false;
            foreach (var control in new Control?[] { _tidalIdInput, _tidalSecretInput })
                if (control != null && (!general || !_tidalFieldsExpanded)) control.Visible = false;
        }
    }
}
