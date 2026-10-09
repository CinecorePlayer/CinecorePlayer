#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Collegamento a Last.fm (autorizzazione nel browser, nessuna password passa dal player) e a
    // ListenBrainz (il token del profilo). Due righe, un filo fra loro, Chiudi in basso a destra.
    internal sealed class ScrobbleForm : HudModalFormBase
    {
        private readonly bool _english;
        private readonly ModalActionButton _lastFmAction, _listenBrainzAction;
        private readonly UnderlineTextBox _token;
        private readonly Button _close;
        private static string? _lastFmPendingToken { get => MusicScrobbler.LastFmPendingToken; set => MusicScrobbler.LastFmPendingToken = value; }
        private string _lastFmNote = "", _listenBrainzNote = "";
        private const int Gutter = 30, Span = 540, LastFmTop = 118, ListenBrainzTop = 206;

        public ScrobbleForm(bool english)
        {
            _english = english;
            Text = T("Ascolti", "Scrobbling");
            ClientSize = new Size(600, 380);

            _lastFmAction = Link();
            _lastFmAction.Click += async (_, _) => await LastFmClicked();

            _listenBrainzAction = Link();
            _listenBrainzAction.Click += async (_, _) => await ListenBrainzClicked();

            _token = new UnderlineTextBox("", T("il token del tuo profilo ListenBrainz", "your ListenBrainz profile token"), password: true);
            _token.Box.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ListenBrainzClicked(); } };

            _close = CreateModalButton(T("Chiudi", "Close"), DialogResult.Cancel, primary: false);
            Controls.AddRange(new Control[] { _lastFmAction, _listenBrainzAction, _token, _close });
            CancelButton = _close;
            Arrange();
        }

        private string T(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);

        private ModalActionButton Link() => new()
        {
            BackColor = Theme.Sheet, ForeColor = Theme.Accent, TextOnly = true, TabStop = true,
            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point)
        };

        // La scheda e' alta quanto serve: il campo del token c'e' solo finche' ListenBrainz non e' collegato.
        private void Arrange()
        {
            bool listenBrainz = MusicScrobbler.ListenBrainzConnected;
            _lastFmAction.Visible = MusicScrobbler.LastFmAvailable;
            _lastFmAction.Text = MusicScrobbler.LastFmConnected ? T("Scollega", "Disconnect") : _lastFmPendingToken != null ? T("Ho autorizzato", "I have authorised") : T("Collega", "Connect");
            _listenBrainzAction.Text = listenBrainz ? T("Scollega", "Disconnect") : T("Collega", "Connect");
            _token.Visible = !listenBrainz;
            // Le azioni di solo testo sono larghe quanto la scritta, cosi' finiscono a filo del margine destro.
            int lastFmWidth = TextRenderer.MeasureText(_lastFmAction.Text, _lastFmAction.Font).Width + 4;
            int listenBrainzWidth = TextRenderer.MeasureText(_listenBrainzAction.Text, _listenBrainzAction.Font).Width + 4;
            _lastFmAction.SetBounds(Gutter + Span - lastFmWidth, LastFmTop + 6, lastFmWidth, 34);
            if (listenBrainz) _listenBrainzAction.SetBounds(Gutter + Span - listenBrainzWidth, ListenBrainzTop + 6, listenBrainzWidth, 34);
            else
            {
                _token.SetBounds(Gutter, ListenBrainzTop + 58, Span - listenBrainzWidth - 24, 36);
                _listenBrainzAction.SetBounds(Gutter + Span - listenBrainzWidth, ListenBrainzTop + 59, listenBrainzWidth, 34);
            }
            int wanted = ListenBrainzTop + (listenBrainz ? 58 : 108) + 20 + 78;
            if (ClientSize.Height != wanted)
            {
                MinimumSize = MaximumSize = Size.Empty;
                ClientSize = new Size(ClientSize.Width, wanted);
            }
            MinimumSize = MaximumSize = Size;
            _close.SetBounds(ClientSize.Width - 30 - 98, ClientSize.Height - 58, 98, 38);
            Invalidate();
        }

        private async Task LastFmClicked()
        {
            if (!_lastFmAction.Enabled) return;
            _lastFmAction.Enabled = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                if (MusicScrobbler.LastFmConnected)
                {
                    MusicScrobbler.LastFmDisconnect();
                    _lastFmNote = "";
                }
                else if (_lastFmPendingToken == null)
                {
                    var step = await MusicScrobbler.LastFmBeginAsync(timeout.Token);
                    if (step == null) _lastFmNote = T("Last.fm non risponde: riprova fra poco.", "Last.fm is not answering: try again shortly.");
                    else
                    {
                        _lastFmPendingToken = step.Value.Token;
                        try { Process.Start(new ProcessStartInfo(step.Value.Url) { UseShellExecute = true }); } catch { }
                        _lastFmNote = T("Autorizza Cinecore Player nella pagina che si è aperta, poi torna qui.", "Authorise Cinecore Player in the page that opened, then come back here.");
                    }
                }
                else
                {
                    bool connected = await MusicScrobbler.LastFmCompleteAsync(_lastFmPendingToken, timeout.Token);
                    if (connected) _lastFmPendingToken = null;
                    // Se nel browser non si e' ancora premuto "consenti" il gettone resta valido: basta riprovare.
                    _lastFmNote = connected ? ""
                        : _lastFmPendingToken != null ? T("Non ancora autorizzato: consenti l'accesso nel browser, poi premi di nuovo.", "Not authorised yet: allow access in the browser, then press again.")
                        : T("L'autorizzazione non risulta: riprova da Collega.", "The authorisation was not found: try Connect again.");
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("[SCROBBLE] Last.fm connect: " + ex.GetType().Name);
                _lastFmPendingToken = null;
                _lastFmNote = T("Collegamento non riuscito: controlla la rete.", "Connection failed: check the network.");
            }
            if (IsDisposed) return;
            _lastFmAction.Enabled = true;
            Arrange();
        }

        private async Task ListenBrainzClicked()
        {
            if (!_listenBrainzAction.Enabled) return;
            _listenBrainzAction.Enabled = false;
            try
            {
                if (MusicScrobbler.ListenBrainzConnected)
                {
                    MusicScrobbler.ListenBrainzDisconnect();
                    _listenBrainzNote = "";
                }
                else
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                    string? user = await MusicScrobbler.ListenBrainzConnectAsync(_token.Box.Text, timeout.Token);
                    _listenBrainzNote = user == null ? T("Token non valido.", "The token is not valid.") : "";
                    if (user != null) _token.Box.Text = "";
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("[SCROBBLE] ListenBrainz connect: " + ex.GetType().Name);
                _listenBrainzNote = T("Collegamento non riuscito: controlla la rete.", "Connection failed: check the network.");
            }
            if (IsDisposed) return;
            _listenBrainzAction.Enabled = true;
            Arrange();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 11f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            TextRenderer.DrawText(g, T("Ascolti", "Scrobbling"), titleFont, new Rectangle(Gutter, 14, Span, 42), Theme.Text, Line);
            TextRenderer.DrawText(g, T("I brani che ascolti finiscono nel tuo profilo. Partono solo artista, titolo, album e durata; se manca la rete vengono inviati più tardi.",
                    "The tracks you listen to go to your profile. Only artist, title, album and length are sent; without a network they are sent later."),
                bodyFont, new Rectangle(Gutter, 54, Span, 44), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            string lastFm = !MusicScrobbler.LastFmAvailable ? T("Non disponibile in questa copia del player.", "Not available in this copy of the player.")
                : _lastFmNote.Length > 0 ? _lastFmNote
                : MusicScrobbler.LastFmConnected ? T("Collegato come ", "Connected as ") + MusicScrobbler.LastFmUser
                : T("Non collegato", "Not connected");
            TextRenderer.DrawText(g, "Last.fm", nameFont, new Rectangle(Gutter, LastFmTop, 300, 24), Theme.Text, Line);
            TextRenderer.DrawText(g, lastFm, bodyFont, new Rectangle(Gutter, LastFmTop + 26, Span - 160, 20), Theme.Muted, Line);

            using (var hairline = new Pen(Color.FromArgb(34, Theme.Text)))
                g.DrawLine(hairline, Gutter, ListenBrainzTop - 18, Gutter + Span, ListenBrainzTop - 18);

            string listenBrainz = _listenBrainzNote.Length > 0 ? _listenBrainzNote
                : MusicScrobbler.ListenBrainzConnected ? T("Collegato come ", "Connected as ") + MusicScrobbler.ListenBrainzUser
                : T("Non collegato. Il token è nella pagina delle impostazioni del profilo, su listenbrainz.org.", "Not connected. The token is in your profile settings page on listenbrainz.org.");
            TextRenderer.DrawText(g, "ListenBrainz", nameFont, new Rectangle(Gutter, ListenBrainzTop, 300, 24), Theme.Text, Line);
            TextRenderer.DrawText(g, listenBrainz, bodyFont, new Rectangle(Gutter, ListenBrainzTop + 26, Span - (MusicScrobbler.ListenBrainzConnected ? 160 : 0), 20), Theme.Muted, Line);

            int queued = MusicScrobbler.QueuedCount;
            if (queued > 0)
                TextRenderer.DrawText(g, queued == 1 ? T("1 ascolto in attesa di invio", "1 listen waiting to be sent") : queued + T(" ascolti in attesa di invio", " listens waiting to be sent"),
                    bodyFont, new Rectangle(Gutter, ClientSize.Height - 50, Span - 130, 22), Theme.Muted, Line);
        }
    }
}
