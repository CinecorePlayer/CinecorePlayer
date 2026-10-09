#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Scheda "Consigliati per te": i consigli personali di Trakt, divisi fra quelli gia' in
    // libreria (un clic apre la scheda del film) e quelli da recuperare (un clic apre la pagina su Trakt).
    internal sealed class TraktForm : HudModalFormBase
    {
        private const int Columns = 6, Gap = 16, Side = 30, ListTop = 98, TitleHeight = 24, RowGap = 18, HeaderHeight = 38;
        private static readonly (string Kind, string Italian, string English)[] Kinds = { ("movies", "Film", "Movies"), ("shows", "Serie", "Series") };

        private readonly bool _english;
        private readonly IReadOnlyList<TraktLibrary.LibraryTitle> _library;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Button _connect, _openPage, _send, _disconnect;
        private readonly Dictionary<string, List<TraktLibrary.Recommendation>?> _results = new();
        private readonly Dictionary<string, Bitmap?> _posters = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _posterLoading = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(Rectangle Bounds, TraktLibrary.Recommendation Item)> _hits = new();
        private readonly List<(Rectangle Bounds, string Kind)> _kindHits = new();
        private readonly List<int> _stops = new();   // inizio di ogni sezione e di ogni fila, nello spazio del contenuto
        private TraktClient.DeviceCode? _code;
        private string _kind = "movies", _status = "";
        private bool? _statusOk;
        private bool _closeHover, _busy;
        private int _scroll, _contentHeight, _pending;
        private Point _mouse = new(-1, -1);
        private Rectangle _markBounds;

        /// <summary>File della libreria scelto dall'utente: il player ne apre la scheda.</summary>
        public string? SelectedLibraryPath { get; private set; }

        private Rectangle CloseBounds => new(ClientSize.Width - Side - 32, 18, 32, 32);
        private Rectangle ListBounds => new(Side, ListTop, ClientSize.Width - Side * 2, ClientSize.Height - ListTop - 100);
        private int CardWidth => (ListBounds.Width - 14 - Gap * (Columns - 1)) / Columns;
        private int CardHeight => CardWidth * 3 / 2;

        public TraktForm(IReadOnlyList<TraktLibrary.LibraryTitle> library, bool english)
        {
            _library = library;
            _english = english;
            Text = T("Consigliati per te", "Recommended for you");
            // L'altezza dell'elenco contiene esattamente un titolo di sezione e due file di locandine.
            ClientSize = new Size(1000, 758);
            MinimumSize = Size;
            MaximumSize = Size;

            _connect = CreateModalButton(T("Collega Trakt", "Connect Trakt"), DialogResult.None, primary: true);
            _openPage = CreateModalButton(T("Apri la pagina", "Open the page"), DialogResult.None, primary: true);
            _send = CreateModalButton("", DialogResult.None, primary: false);
            _disconnect = CreateModalButton(T("Scollega", "Disconnect"), DialogResult.None, primary: false);
            _connect.SetBounds((ClientSize.Width - 170) / 2, 392, 170, 40);
            _openPage.SetBounds((ClientSize.Width - 170) / 2, 432, 170, 40);
            _disconnect.SetBounds(ClientSize.Width - Side - 110, ClientSize.Height - 58, 110, 38);
            _send.SetBounds(_disconnect.Left - 10 - 220, ClientSize.Height - 58, 220, 38);
            _connect.Click += (_, _) => Connect();
            _openPage.Click += (_, _) => { if (_code != null) OpenUrl(_code.VerificationUrl); };
            _send.Click += (_, _) => SendDiary();
            _disconnect.Click += (_, _) => Disconnect();
            Controls.AddRange(new Control[] { _connect, _openPage, _send, _disconnect });
            Shown += (_, _) => UpdateState(load: true);
        }

        private string T(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);
        private string Language => _english ? "en" : "it";

        private void UpdateState(bool load)
        {
            bool connected = TraktClient.IsConnected;
            _connect.Visible = !connected && _code == null && TraktClient.IsAvailable;
            _openPage.Visible = !connected && _code != null;
            _send.Visible = _disconnect.Visible = connected;
            if (connected)
            {
                _pending = TraktLibrary.PendingCount();
                _send.Text = _pending > 0 ? string.Format(T("Invia il diario  ·  {0}", "Send the diary  ·  {0}"), _pending) : T("Diario inviato", "Diary sent");
                _send.Enabled = _pending > 0 && !_busy;
                _disconnect.Enabled = !_busy;
                if (load) LoadKind(_kind);
            }
            Invalidate();
        }

        private static void OpenUrl(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            catch { }
        }

        private async void Connect()
        {
            _connect.Enabled = false;
            Say(T("Chiedo un codice a Trakt…", "Asking Trakt for a code…"), null);
            try
            {
                var token = _lifetime.Token;
                _code = await Task.Run(() => TraktClient.BeginConnectAsync(token), token);
                if (IsDisposed) return;
                try { Clipboard.SetText(_code.UserCode); } catch { }
                Say(T("Codice copiato negli appunti. Aspetto la conferma dal sito…", "Code copied to the clipboard. Waiting for the site's confirmation…"), null);
                UpdateState(load: false);
                var code = _code;
                bool connected = await Task.Run(() => TraktClient.WaitForConnectAsync(code, token), token);
                if (IsDisposed) return;
                _code = null;
                Say(connected ? T("Collegato", "Connected") : T("Codice scaduto o rifiutato: riprova", "Code expired or refused: try again"), connected);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _code = null;
                Say(T("Trakt non raggiungibile", "Trakt is unreachable") + "  ·  " + ex.Message, false);
            }
            if (IsDisposed) return;
            _connect.Enabled = true;
            UpdateState(load: true);
        }

        private async void Disconnect()
        {
            _busy = true;
            UpdateState(load: false);
            try { await Task.Run(() => TraktClient.DisconnectAsync(_lifetime.Token)); } catch { }
            if (IsDisposed) return;
            _busy = false;
            _results.Clear();
            _scroll = 0;
            Say(T("Scollegato", "Disconnected"), null);
            UpdateState(load: false);
        }

        private async void SendDiary()
        {
            if (_busy) return;
            _busy = true;
            Say(T("Riconosco i titoli del diario e li invio a Trakt…", "Recognising the diary's titles and sending them to Trakt…"), null);
            UpdateState(load: false);
            try
            {
                var token = _lifetime.Token;
                string language = Language;
                var result = await Task.Run(() => TraktLibrary.SendDiaryAsync(null, language, token), token);
                if (IsDisposed) return;
                Say(string.Format(T("Inviati a Trakt: {0} film, {1} episodi, {2} voti", "Sent to Trakt: {0} movies, {1} episodes, {2} ratings"), result.Movies, result.Episodes, result.Ratings), true);
                _results.Clear(); // con il diario su Trakt i consigli cambiano
                _busy = false;
                UpdateState(load: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _busy = false;
                Say(T("Invio non riuscito", "Sending failed") + "  ·  " + ex.Message, false);
                UpdateState(load: false);
            }
        }

        private void Say(string text, bool? ok)
        {
            _status = text;
            _statusOk = ok;
            Invalidate();
        }

        private async void LoadKind(string kind)
        {
            if (_results.ContainsKey(kind)) { Invalidate(); return; }
            _results[kind] = null;
            Invalidate();
            List<TraktLibrary.Recommendation>? loaded = null;
            try
            {
                var token = _lifetime.Token;
                string language = Language;
                // Prima l'elenco di Trakt (un attimo), mostrato subito; nomi italiani e locandine arrivano uno alla volta.
                var titles = await Task.Run(() => TraktClient.RecommendationsAsync(kind, 60, token), token);
                if (IsDisposed) return;
                var shown = TraktLibrary.Provisional(titles, _library);
                _results[kind] = shown;
                Invalidate();
                var progress = new Progress<TraktLibrary.Recommendation>(ready =>
                {
                    if (IsDisposed || !_results.TryGetValue(kind, out var current) || !ReferenceEquals(current, shown)) return;
                    int index = shown.FindIndex(item => item.Trakt.TmdbId == ready.Trakt.TmdbId);
                    if (index < 0) return;
                    shown[index] = ready;
                    if (kind == _kind) Invalidate(ListBounds);
                });
                loaded = await Task.Run(() => TraktLibrary.ResolveAsync(titles, _library, language, progress, token), token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Dbg.Warn("[TRAKT] recommendations: " + ex.Message);
                if (!IsDisposed) Say(T("Consigli non disponibili", "Recommendations unavailable") + "  ·  " + ex.Message, false);
            }
            if (IsDisposed) return;
            if (loaded == null) _results.Remove(kind);
            else _results[kind] = loaded;
            if (!TraktClient.IsConnected) UpdateState(load: false); // accesso revocato dal sito
            Invalidate();
        }

        // Le locandine si decodificano fuori dal thread dell'interfaccia, gia' alla misura della card.
        private Bitmap? Poster(string? file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            if (_posters.TryGetValue(file, out Bitmap? ready)) return ready;
            if (!_posterLoading.Add(file)) return null;
            int width = CardWidth, height = CardHeight;
            Task.Run(() =>
            {
                Bitmap? scaled = null;
                try
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(file));
                    using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                    scaled = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using var g = Graphics.FromImage(scaled);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // Riempie la card tagliando l'eccesso, senza deformare.
                    double scale = Math.Max(width / (double)source.Width, height / (double)source.Height);
                    int w = (int)Math.Ceiling(source.Width * scale), h = (int)Math.Ceiling(source.Height * scale);
                    using var attributes = new System.Drawing.Imaging.ImageAttributes();
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(source, new Rectangle((width - w) / 2, (height - h) / 2, w, h), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                }
                catch { scaled?.Dispose(); scaled = null; }
                try
                {
                    if (IsDisposed || !IsHandleCreated) { scaled?.Dispose(); return; }
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) { scaled?.Dispose(); return; }
                        _posters[file] = scaled;
                        Invalidate(ListBounds);
                    }));
                }
                catch { scaled?.Dispose(); }
            });
            return null;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // Lo scorrimento si ferma sempre all'inizio di un titolo di sezione o di una fila: niente locandine tagliate in alto.
            // L'ultima fermata e' la prima da cui si vede la fine dell'elenco.
            int max = Math.Max(0, _contentHeight - ListBounds.Height);
            var ordered = _stops.Distinct().OrderBy(stop => stop).ToList();
            if (ordered.Count == 0) return;
            int last = ordered.FirstOrDefault(stop => stop >= max, ordered[^1]);
            var stops = ordered.Where(stop => stop <= last).ToList();
            _scroll = e.Delta < 0
                ? stops.FirstOrDefault(stop => stop > _scroll + 1, last)
                : stops.LastOrDefault(stop => stop < _scroll - 1, 0);
            Invalidate(ListBounds);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            bool close = CloseBounds.Contains(e.Location);
            Cursor = close || _hits.Any(hit => hit.Bounds.Contains(e.Location)) || _kindHits.Any(hit => hit.Bounds.Contains(e.Location)) || _markBounds.Contains(e.Location)
                ? Cursors.Hand : Cursors.Default;
            if (close != _closeHover) { _closeHover = close; Invalidate(Rectangle.Inflate(CloseBounds, 2, 2)); }
            Invalidate(new Rectangle(0, 50, ClientSize.Width, ClientSize.Height - 50));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = new Point(-1, -1);
            _closeHover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (CloseBounds.Contains(e.Location)) { DialogResult = DialogResult.Cancel; Close(); return; }
                string? kind = _kindHits.FirstOrDefault(hit => hit.Bounds.Contains(e.Location)).Kind;
                if (kind != null && kind != _kind) { _kind = kind; _scroll = 0; LoadKind(kind); return; }
                if (_markBounds.Contains(e.Location)) { TraktClient.MarkWatched = !TraktClient.MarkWatched; Invalidate(); return; }
                var hit = _hits.FirstOrDefault(h => h.Bounds.Contains(e.Location)).Item;
                if (hit != null)
                {
                    if (hit.LibraryPath != null)
                    {
                        SelectedLibraryPath = hit.LibraryPath;
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else if (hit.Trakt.Slug is { } slug && Regex.IsMatch(slug, "^[a-z0-9-]+$"))
                        OpenUrl("https://trakt.tv/" + (hit.Trakt.Kind == "tv" ? "shows/" : "movies/") + slug);
                    return;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            const TextFormatFlags Centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var switchFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.6f, FontStyle.Regular, GraphicsUnit.Point);
            using var sectionFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var cardFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            using var codeFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 30f, FontStyle.Regular, GraphicsUnit.Point);
            using var leadFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Point);

            int width = ClientSize.Width, height = ClientSize.Height;
            TextRenderer.DrawText(g, T("Consigliati per te", "Recommended for you"), titleFont, new Rectangle(Side, 20, 400, 30), Theme.Text, Line);
            Theme.DrawCloseButton(g, CloseBounds, _closeHover);
            _hits.Clear();
            _kindHits.Clear();
            _markBounds = Rectangle.Empty;

            bool connected = TraktClient.IsConnected;
            if (!connected)
            {
                TextRenderer.DrawText(g, "Trakt", bodyFont, new Rectangle(Side, 54, 400, 22), Theme.Muted, Line);
                var block = new Rectangle(140, 230, width - 280, 150);
                if (!TraktClient.IsAvailable)
                    TextRenderer.DrawText(g, T("Questa copia del player non ha le chiavi di Trakt.", "This copy of the player has no Trakt keys."), bodyFont, block, Theme.Muted, Centered);
                else if (_code == null)
                {
                    TextRenderer.DrawText(g, T("Consigli basati su quello che hai visto", "Recommendations based on what you watched"), leadFont, new Rectangle(block.Left, 262, block.Width, 30), Theme.Text, Centered);
                    TextRenderer.DrawText(g, T("Collega il tuo account Trakt: il player gli manda i film che finisci e i voti del diario,\ne Trakt risponde con i titoli che fanno per te, a partire da quelli che hai già in libreria.",
                            "Connect your Trakt account: the player sends it the films you finish and your diary ratings,\nand Trakt answers with titles for you, starting from the ones already in your library."),
                        bodyFont, new Rectangle(block.Left, 304, block.Width, 60), Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
                else
                {
                    TextRenderer.DrawText(g, T("Apri trakt.tv/activate e inserisci questo codice", "Open trakt.tv/activate and enter this code"), bodyFont, new Rectangle(block.Left, 270, block.Width, 24), Theme.Muted, Centered);
                    TextRenderer.DrawText(g, string.Join(" ", _code.UserCode.ToCharArray()), codeFont, new Rectangle(block.Left, 312, block.Width, 70), Theme.Text, Centered);
                }
            }
            else
            {
                string user = TraktClient.UserName;
                TextRenderer.DrawText(g, user.Length > 0 ? "Trakt  ·  " + user : "Trakt", bodyFont, new Rectangle(Side, 54, width - 300, 22), Theme.Muted, Line);

                int x = width - Side;
                foreach (var (kind, italian, english) in Kinds.Reverse())
                {
                    string label = _english ? english : italian;
                    Size size = TextRenderer.MeasureText(g, label, switchFont, Size.Empty, TextFormatFlags.NoPadding);
                    x -= size.Width;
                    var bounds = new Rectangle(x, 52, size.Width, 24);
                    bool selected = kind == _kind;
                    TextRenderer.DrawText(g, label, switchFont, bounds, selected ? Theme.Text : bounds.Contains(_mouse) ? Theme.SubtleText : Theme.Muted, Line);
                    if (selected)
                        using (var underline = new Pen(Theme.Accent, 2f)) g.DrawLine(underline, bounds.Left, bounds.Bottom + 1, bounds.Right, bounds.Bottom + 1);
                    _kindHits.Add((Rectangle.Inflate(bounds, 8, 6), kind));
                    x -= 22;
                }
                DrawRecommendations(g, sectionFont, cardFont, bodyFont);

                // Interruttore: ogni film finito viene segnato su Trakt.
                bool mark = TraktClient.MarkWatched;
                string markText = T("Segna su Trakt quello che finisco", "Mark on Trakt what I finish");
                int markWidth = TextRenderer.MeasureText(g, markText, bodyFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                var track = new Rectangle(Side, height - 49, 34, 20);
                _markBounds = new Rectangle(track.Left - 4, track.Top - 8, track.Width + 12 + markWidth + 8, 36);
                using (var path = Rounded(track, 10))
                using (var fill = new SolidBrush(mark ? Theme.Accent : Color.FromArgb(Theme.IsLight ? 70 : 60, Theme.Text)))
                    g.FillPath(fill, path);
                using (var knob = new SolidBrush(Color.White))
                    g.FillEllipse(knob, mark ? track.Right - 18 : track.Left + 2, track.Top + 2, 16, 16);
                TextRenderer.DrawText(g, markText, bodyFont, new Rectangle(track.Right + 12, track.Top - 2, markWidth + 4, 24), _markBounds.Contains(_mouse) ? Theme.Text : Theme.SubtleText, Line);
            }

            if (_status.Length > 0)
            {
                var status = new Rectangle(Side, height - 92, width - Side * 2, 24);
                if (!connected)
                    TextRenderer.DrawText(g, _status, bodyFont, new Rectangle(status.Left, 500, status.Width, 24), Theme.SubtleText, Centered);
                else
                {
                    Color dot = _statusOk == true ? Color.FromArgb(62, 190, 120) : _statusOk == false ? Theme.Danger : Color.FromArgb(170, Theme.Muted);
                    using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, status.Left, status.Top + 8, 8, 8);
                    TextRenderer.DrawText(g, _status, bodyFont, new Rectangle(status.Left + 18, status.Top, status.Width - 18, status.Height), Theme.SubtleText, Line);
                }
            }
        }

        private void DrawRecommendations(Graphics g, Font sectionFont, Font cardFont, Font bodyFont)
        {
            const TextFormatFlags Clipped = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;
            Rectangle list = ListBounds;
            _results.TryGetValue(_kind, out var items);
            if (items == null || items.Count == 0)
            {
                string text = items == null
                    ? T("Chiedo i consigli a Trakt…", "Asking Trakt for recommendations…")
                    : T("Trakt non ha ancora consigli per te: invia il diario, o guarda qualcosa e torna qui.", "Trakt has no recommendations for you yet: send the diary, or watch something and come back.");
                TextRenderer.DrawText(g, text, bodyFont, new Rectangle(list.Left, list.Top + 140, list.Width, 30), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                _contentHeight = 0;
                return;
            }

            int cardW = CardWidth, cardH = CardHeight, rowH = cardH + 8 + TitleHeight + RowGap;
            var sections = new[]
            {
                (Title: T("Nella tua libreria", "In your library"), Items: items.Where(i => i.LibraryPath != null).ToList()),
                (Title: T("Da recuperare", "To track down"), Items: items.Where(i => i.LibraryPath == null).ToList())
            };
            var state = g.Save();
            g.SetClip(list);
            int y = list.Top - _scroll;
            _stops.Clear();
            foreach (var (title, sectionItems) in sections)
            {
                if (sectionItems.Count == 0) continue;
                _stops.Add(y + _scroll - list.Top);
                if (y >= list.Top && y < list.Bottom)
                {
                    int headingWidth = TextRenderer.MeasureText(g, title, sectionFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, title, sectionFont, new Rectangle(list.Left, y + 4, headingWidth + 4, 22), Theme.Text, Clipped);
                    TextRenderer.DrawText(g, sectionItems.Count.ToString(), sectionFont, new Rectangle(list.Left + headingWidth + 10, y + 4, 60, 22), Theme.Muted, Clipped);
                }
                y += HeaderHeight;
                for (int i = 0; i < sectionItems.Count; i++)
                {
                    int column = i % Columns;
                    if (i > 0 && column == 0) y += rowH;
                    if (column == 0) _stops.Add(y + _scroll - list.Top);
                    var card = new Rectangle(list.Left + column * (cardW + Gap), y, cardW, cardH);
                    // In alto lo scorrimento si ferma sempre su una fila intera; in basso la fila che
                    // non entra tutta sfuma nel fondo (vedi sotto) invece di essere tagliata di netto.
                    if (card.Top < list.Top || card.Top > list.Bottom) continue;
                    var item = sectionItems[i];
                    var hitBounds = Rectangle.Intersect(new Rectangle(card.Left, card.Top, card.Width, card.Height + 8 + TitleHeight), list);
                    bool hot = hitBounds.Contains(_mouse);
                    using (var shape = Rounded(card, 8))
                    {
                        Bitmap? poster = Poster(item.PosterFile);
                        if (poster != null)
                        {
                            using var texture = new TextureBrush(poster, WrapMode.Clamp);
                            texture.TranslateTransform(card.Left, card.Top);
                            g.FillPath(texture, shape);
                        }
                        else
                        {
                            using var empty = new SolidBrush(Color.FromArgb(Theme.IsLight ? 26 : 22, Theme.Text));
                            g.FillPath(empty, shape);
                            if (item.PosterFile == null)
                                TextRenderer.DrawText(g, item.DisplayTitle, cardFont, Rectangle.Inflate(card, -10, -10), Theme.Muted,
                                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
                        }
                        if (hot)
                            using (var ring = new Pen(Theme.Accent, 2f)) g.DrawPath(ring, shape);
                    }
                    TextRenderer.DrawText(g, item.DisplayTitle, cardFont, new Rectangle(card.Left, card.Bottom + 8, card.Width, TitleHeight - 4), hot ? Theme.Accent : Theme.SubtleText, Clipped);
                    _hits.Add((hitBounds, item));
                }
                y += rowH + 6;
            }
            g.Restore(state);
            _contentHeight = y + _scroll - list.Top;
            // C'e' altro sotto: l'ultima parte dell'elenco sfuma nel colore della scheda.
            if (_contentHeight - _scroll > list.Height + 4)
            {
                var fade = new Rectangle(list.Left - 2, list.Bottom - 64, list.Width + 4, 65);
                using var veil = new LinearGradientBrush(new Rectangle(fade.Left, fade.Top - 1, fade.Width, fade.Height + 2), Color.FromArgb(0, BackColor), BackColor, LinearGradientMode.Vertical);
                veil.SetSigmaBellShape(1f, 1f);
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                g.FillRectangle(veil, fade);
                g.SmoothingMode = smoothing;
            }
            if (_contentHeight > list.Height)
            {
                var trackBounds = new Rectangle(list.Right - 3, list.Top, 3, list.Height);
                int thumb = Math.Max(28, (int)(trackBounds.Height * (list.Height / (double)_contentHeight)));
                int top = trackBounds.Top + (int)((trackBounds.Height - thumb) * (_scroll / (double)Math.Max(1, _contentHeight - list.Height)));
                ScrollbarChrome.Draw(g, trackBounds, top, thumb);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _lifetime.Cancel(); } catch { }
                _lifetime.Dispose();
                foreach (var poster in _posters.Values) poster?.Dispose();
                _posters.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
