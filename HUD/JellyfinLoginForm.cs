#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Scheda "Accedi a Jellyfin": indirizzo del server (gia' compilato se il server e' stato
    // trovato in rete), nome utente e password. In alternativa Quick Connect: il player mostra
    // un codice da inserire in Jellyfin su un dispositivo gia' collegato, e nessuna password
    // passa dal player. La password non viene salvata: resta solo il token, cifrato.
    internal sealed class JellyfinLoginForm : HudModalFormBase
    {
        private readonly bool _english;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TextBox _address, _user, _password;
        private readonly Panel _addressShell, _userShell, _passwordShell;
        private readonly Button _signIn, _quick;
        private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
        private JellyfinClient.Server? _server;
        private JellyfinClient.QuickConnectRequest? _request;
        private string _status = "";
        private bool? _statusOk;
        private bool _busy, _quickMode, _polling;

        private static readonly Rectangle AddressBounds = new(30, 122, 500, 42);
        private static readonly Rectangle UserBounds = new(30, 206, 500, 42);
        private static readonly Rectangle PasswordBounds = new(30, 290, 500, 42);

        public JellyfinClient.Account? Account { get; private set; }

        /// <param name="server">Server gia' noto (trovato in rete); null per chiedere l'indirizzo.</param>
        public JellyfinLoginForm(JellyfinClient.Server? server, bool english)
        {
            _server = server;
            _english = english;
            Text = server?.Product == JellyfinClient.Emby ? "Emby" : "Jellyfin";
            ClientSize = new Size(560, 452);
            MinimumSize = Size;
            MaximumSize = Size;

            (_addressShell, _address) = Input(AddressBounds, server?.Address ?? "", "192.168.1.10  ·  nas:8096  ·  https://…", password: false);
            (_userShell, _user) = Input(UserBounds, "", "", password: false);
            (_passwordShell, _password) = Input(PasswordBounds, "", "", password: true);
            // Con un indirizzo diverso il server va riconosciuto di nuovo.
            _address.TextChanged += (_, _) => { if (_server != null && !string.Equals(JellyfinClient.NormalizeAddress(_address.Text), _server.Address, StringComparison.OrdinalIgnoreCase)) _server = null; };

            _quick = CreateModalButton("Quick Connect", DialogResult.None, primary: false);
            Button cancel = CreateModalButton(T("Annulla", "Cancel"), DialogResult.Cancel, primary: false);
            _signIn = CreateModalButton(T("Accedi", "Sign in"), DialogResult.None, primary: true);
            _quick.ForeColor = cancel.ForeColor = Theme.Text;
            _quick.SetBounds(30, ClientSize.Height - 58, 150, 38);
            cancel.SetBounds(ClientSize.Width - 30 - 104 - 10 - 96, ClientSize.Height - 58, 96, 38);
            _signIn.SetBounds(ClientSize.Width - 30 - 104, ClientSize.Height - 58, 104, 38);
            _signIn.Click += (_, _) => SignIn();
            _quick.Click += (_, _) => { if (_quickMode) LeaveQuickConnect(); else StartQuickConnect(); };

            Controls.AddRange(new Control[] { _addressShell, _userShell, _passwordShell, _quick, cancel, _signIn });
            AcceptButton = _signIn;
            CancelButton = cancel;
            _poll.Tick += (_, _) => PollQuickConnect();
            Shown += (_, _) => { try { (server == null ? _address : _user).Focus(); } catch { } };
        }

        private string T(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);

        private (Panel Shell, TextBox Box) Input(Rectangle bounds, string text, string placeholder, bool password)
        {
            var shell = new Panel
            {
                BackColor = Theme.Sheet,
                Location = new Point(bounds.Left + 1, bounds.Top + 1),
                Size = new Size(bounds.Width - 2, bounds.Height - 2),
                Padding = new Padding(2, 10, 2, 8)
            };
            var box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = shell.BackColor,
                ForeColor = Theme.Text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
                Dock = DockStyle.Fill,
                Text = text,
                PlaceholderText = placeholder,
                UseSystemPasswordChar = password
            };
            box.GotFocus += (_, _) => Invalidate();
            box.LostFocus += (_, _) => Invalidate();
            shell.Controls.Add(box);
            return (shell, box);
        }

        private void Say(string text, bool? ok)
        {
            _status = text;
            _statusOk = ok;
            Invalidate();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _signIn.Enabled = !busy && !_quickMode;
            _quick.Enabled = !busy;
            _address.Enabled = _user.Enabled = _password.Enabled = !busy;
        }

        /// <summary>Il server all'indirizzo scritto: quello gia' noto, oppure chiesto ora.</summary>
        private async Task<JellyfinClient.Server?> ResolveServerAsync()
        {
            if (_server != null) return _server;
            string address = JellyfinClient.NormalizeAddress(_address.Text);
            if (address.Length == 0)
            {
                Say(T("Inserisci l'indirizzo del server", "Enter the server address"), false);
                return null;
            }
            Say(T("Cerco il server…", "Looking for the server…"), null);
            try
            {
                _server = await JellyfinClient.GetServerAsync(address, _lifetime.Token);
                return _server;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return null; }
            catch
            {
                if (!IsDisposed) Say(T("A questo indirizzo non risponde un server Jellyfin o Emby", "No Jellyfin or Emby server answers at this address"), false);
                return null;
            }
        }

        private async void SignIn()
        {
            if (_busy || _quickMode) return;
            string user = _user.Text.Trim();
            if (user.Length == 0) { Say(T("Inserisci il nome utente", "Enter the user name"), false); _user.Focus(); return; }
            SetBusy(true);
            try
            {
                var server = await ResolveServerAsync();
                if (server == null || IsDisposed) return;
                Say(T("Accesso in corso…", "Signing in…"), null);
                Account = await JellyfinClient.LoginAsync(server, user, _password.Text, _lifetime.Token);
                if (IsDisposed) return;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException) { }
            catch (JellyfinClient.UnauthorizedException)
            {
                if (!IsDisposed) { Say(T("Nome utente o password non corretti", "Wrong user name or password"), false); _password.SelectAll(); }
            }
            catch (HttpRequestException)
            {
                if (!IsDisposed) Say(T("Il server non risponde", "The server does not answer"), false);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Say(T("Accesso non riuscito", "Sign-in failed") + "  ·  " + ex.Message, false);
            }
            finally
            {
                if (!IsDisposed) { SetBusy(false); if (Account == null) _password.Focus(); }
            }
        }

        private async void StartQuickConnect()
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                var server = await ResolveServerAsync();
                if (server == null || IsDisposed) return;
                if (!await JellyfinClient.QuickConnectEnabledAsync(server, _lifetime.Token))
                {
                    if (!IsDisposed) Say(T("Quick Connect non è attivo su questo server", "Quick Connect is not enabled on this server"), false);
                    return;
                }
                _request = await JellyfinClient.StartQuickConnectAsync(server, _lifetime.Token);
                if (IsDisposed) return;
                _quickMode = true;
                _quick.Text = T("Usa la password", "Use the password");
                _userShell.Visible = _passwordShell.Visible = false;
                Say(T("In attesa dell'approvazione…", "Waiting for approval…"), null);
                _poll.Start();
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (!IsDisposed) Say(T("Quick Connect non disponibile", "Quick Connect is not available"), false);
            }
            finally
            {
                if (!IsDisposed) SetBusy(false);
            }
        }

        private void LeaveQuickConnect()
        {
            _poll.Stop();
            _quickMode = false;
            _request = null;
            _quick.Text = "Quick Connect";
            _userShell.Visible = _passwordShell.Visible = true;
            _signIn.Enabled = true;
            Say("", null);
            try { _user.Focus(); } catch { }
        }

        private async void PollQuickConnect()
        {
            if (_polling || !_quickMode || _request == null || _server == null) return;
            _polling = true;
            try
            {
                var account = await JellyfinClient.TryFinishQuickConnectAsync(_server, _request, _lifetime.Token);
                if (IsDisposed || account == null) return;
                _poll.Stop();
                Account = account;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException) { }
            catch (JellyfinClient.UnauthorizedException)
            {
                // Codice scaduto o rifiutato.
                if (!IsDisposed) { LeaveQuickConnect(); Say(T("Codice scaduto: riprova", "The code expired: try again"), false); }
            }
            catch { /* rete assente per un attimo: si riprova al prossimo giro */ }
            finally { _polling = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var labelFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 7.6f, FontStyle.Regular, GraphicsUnit.Point);
            int width = ClientSize.Width;

            string product = string.IsNullOrEmpty(_server?.Product) ? T("Jellyfin o Emby", "Jellyfin or Emby") : _server!.Product;
            TextRenderer.DrawText(g, T("Accedi a ", "Sign in to ") + product, titleFont, new Rectangle(30, 14, width - 60, 42), Theme.Text, Line);
            string subtitle = _server != null
                ? _server.Name + (string.IsNullOrWhiteSpace(_server.Version) ? "" : "  ·  " + (string.IsNullOrEmpty(_server.Product) ? "" : _server.Product + " ") + _server.Version)
                : T("Librerie, locandine e punto di ripresa arrivano dal tuo server.", "Libraries, artwork and resume points come from your server.");
            TextRenderer.DrawText(g, subtitle, bodyFont, new Rectangle(30, 54, width - 60, 22), Theme.Muted, Line);

            void Field(Rectangle bounds, string label, TextBox box)
            {
                TextRenderer.DrawText(g, label, labelFont, new Rectangle(bounds.Left, bounds.Top - 24, 300, 18), Theme.Muted, Line);
                using var underline = new Pen(box.Focused ? Theme.Accent : Color.FromArgb(Theme.IsLight ? 90 : 70, Theme.Text), box.Focused ? 2f : 1f);
                g.DrawLine(underline, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
            }

            Field(AddressBounds, T("INDIRIZZO DEL SERVER", "SERVER ADDRESS"), _address);
            if (_quickMode && _request != null)
            {
                // Il codice, grande e spaziato: si legge dal divano e si copia senza errori.
                using var codeFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 30f, FontStyle.Regular, GraphicsUnit.Point);
                TextRenderer.DrawText(g, T("CODICE", "CODE"), labelFont, new Rectangle(30, UserBounds.Top - 24, 300, 18), Theme.Muted, Line);
                TextRenderer.DrawText(g, string.Join(" ", _request.Code.ToCharArray()), codeFont, new Rectangle(28, UserBounds.Top - 4, width - 60, 64), Theme.Text, Line);
                TextRenderer.DrawText(g, T("Su un dispositivo già collegato a Jellyfin apri il menu utente, Quick Connect, e inserisci il codice.",
                        "On a device already signed in to Jellyfin open the user menu, Quick Connect, and enter the code."),
                    bodyFont, new Rectangle(30, UserBounds.Top + 70, width - 60, 44), Theme.SubtleText,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            else
            {
                Field(UserBounds, T("NOME UTENTE", "USER NAME"), _user);
                Field(PasswordBounds, "PASSWORD", _password);
            }

            if (_status.Length > 0)
            {
                Color dot = _statusOk == true ? Color.FromArgb(62, 190, 120) : _statusOk == false ? Theme.Danger : Color.FromArgb(170, Theme.Muted);
                using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, 30, PasswordBounds.Bottom + 28, 8, 8);
                TextRenderer.DrawText(g, _status, bodyFont, new Rectangle(48, PasswordBounds.Bottom + 20, width - 78, 24), Theme.SubtleText, Line);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _lifetime.Cancel(); } catch { }
                _poll.Dispose();
                _lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
