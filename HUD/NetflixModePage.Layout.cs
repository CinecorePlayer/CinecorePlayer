#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    internal sealed partial class NetflixModePage
    {
        private int D(int value) => Math.Max(1, (int)Math.Round(value * UiScale));
        private int HudMargin => Math.Clamp((int)(Width * .04), D(24), D(64));
        private int LibraryTop => Math.Min(Height - D(225), D(540));
        private Rectangle _stripRect;
        private string _keyboardZone = "play";
        private bool _keyboardFocus;
        private string? _queuedPath;
        private readonly ToolTip _hudTip = new() { InitialDelay = 550, ReshowDelay = 100, AutoPopDelay = 5000 };
        private Rectangle _lastSearchLayout;
        private bool Hot(string zone) => _hoverZone == zone || (_keyboardFocus && Focused && _keyboardZone == zone) || RemoteHot(zone);

        // Un solo punto evidenziato alla volta. Quando il telecomando o la tastiera sono su un
        // pulsante (Riproduci, Cast, Cerca...), la locandina scelta perde il contorno; e il
        // passaggio del mouse, rimasto fermo su un pulsante o una locandina, non conta piu'.
        private bool FocusOnControls => (_remoteFocus && _remoteRow != RemoteRow.Browse) || (_keyboardFocus && Focused && _keyboardZone.Length > 0);
        private Point _lastMouseLocation = new(-1, -1);
        private void ClearPointerHover()
        {
            _hoverItemIndex = -1;
            _hoverZone = string.Empty;
        }

        // Native child layout belongs to size/state changes, never to the paint loop.
        private void UpdateHudLayout()
        {
            if (_searchBox == null || Width < 4 || Height < 4) return;
            int margin = HudMargin;
            int searchWidth = Math.Min(D(550), Width - 2 * margin);
            _searchRect = new Rectangle(margin, LibraryTop + D(31), searchWidth, D(38));
            _filterRect = new Rectangle(_searchRect.Right - D(42), _searchRect.Top, D(42), _searchRect.Height);
            _sourceRect = new Rectangle(Width - margin - D(176), _searchRect.Top, D(176), _searchRect.Height);
            LayoutSearchBox();
        }

        private void LayoutSearchBox()
        {
            float size = Math.Max(8.5f, 9.3f * UiScale) * (96f / 72f); // pixel: vedi UiFont
            bool fontChanged = Math.Abs(_searchBox.Font.Size - global::CinecorePlayer2025.AppFonts.EffectiveSize("Segoe UI", size)) > .05f;
            if (fontChanged)
            {
                // Only dispose fonts created by this control, not the ambient default.
                global::CinecorePlayer2025.AppFonts.ReplaceFont(_searchBox, global::CinecorePlayer2025.AppFonts.Create("Segoe UI", size, GraphicsUnit.Pixel), disposeOld: _lastSearchLayout != Rectangle.Empty);
            }
            int inputHeight = _searchBox.Font.Height + D(3);
            var input = new Rectangle(_searchRect.Left + D(36), _searchRect.Top + (_searchRect.Height - inputHeight) / 2, _searchRect.Width - D(88), inputHeight);
            if (_lastSearchLayout != input || fontChanged)
            {
                _searchBox.Bounds = input;
                _lastSearchLayout = input;
            }
            _searchBox.PlaceholderText = L("Cerca titolo o cast", "Search title or cast");
            _searchBox.Visible = Visible && _entryProgress >= 1 && _browseProgress >= 1 && !_castSheetOpen && _loadingText == null;
        }

        private void DrawTopChrome(Graphics g)
        {
            using var brand = UiFont("Segoe UI Semibold", 10);
            var bounds = new Rectangle(HudMargin, D(20), D(280), D(32));
            TextRenderer.DrawText(g, "CINECORE", brand, bounds, Color.FromArgb(241, 246, 250), TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            int w = TextRenderer.MeasureText(g, "CINECORE", brand, Size.Empty, TextFormatFlags.NoPadding).Width;
            using var divider = new Pen(Color.FromArgb(65, 220, 233, 244));
            g.DrawLine(divider, bounds.Left + w + D(16), bounds.Top + D(9), bounds.Left + w + D(16), bounds.Bottom - D(9));
            TextRenderer.DrawText(g, "SPOTLIGHT", brand, new Rectangle(bounds.Left + w + D(32), bounds.Top, D(140), bounds.Height), Color.FromArgb(170, 191, 208), TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            string? metadataStatus = NetworkMetadataWarmupLabel;
            if (!string.IsNullOrWhiteSpace(metadataStatus))
            {
                using var statusFont = UiFont("Segoe UI", 8.5f);
                var statusBounds = new Rectangle(Math.Max(D(300), Width - HudMargin - D(300)), bounds.Top, D(300), bounds.Height);
                TextRenderer.DrawText(g, metadataStatus, statusFont, statusBounds, Color.FromArgb(170, 191, 208),
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            }
            _closeRect = Rectangle.Empty;
        }

        private void DrawHero(Graphics g, NetflixModeItem item)
        {
            int left = HudMargin;
            bool compact = Height < D(720);
            int width = Math.Min(D(660), (int)(Width * (Width < Height * 1.35 ? .84 : .55)));
            int top = D(82);
            int actionTop = LibraryTop - D(78);
            using var eyebrow = UiFont("Segoe UI Semibold", 8.6f);
            using var title = UiFont("Segoe UI Semibold", compact ? 28 : 34);
            using var small = UiFont("Segoe UI", 9.4f);
            using var body = UiFont("Segoe UI", 10.6f);
            string category = string.IsNullOrWhiteSpace(item.Category) ? L("Film", "Movie") : item.Category;
            string genres = string.Join(" · ", item.Genres.Take(2));
            TextRenderer.DrawText(g, (category + (genres.Length > 0 ? "  /  " + genres : "")).ToUpperInvariant(), eyebrow,
                new Rectangle(left, top, width, D(22)), Color.FromArgb(180, 201, 218), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

            // Fit up to two title lines; the content below follows its measured height.
            int titleTop = top + D(26);
            int titleHeight = D(compact ? 87 : 110);
            string raw = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(item.Path) : item.Title;
            int titleLineHeight = TextRenderer.MeasureText("Ag", title, Size.Empty, TextFormatFlags.NoPadding).Height;
            int measuredTitle = TextRenderer.MeasureText(raw, title, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            titleHeight = Math.Min(titleLineHeight * 2, measuredTitle);
            TextRenderer.DrawText(g, FitHudText(raw, title, new Size(width, titleHeight)), title, new Rectangle(left - D(2), titleTop, width, titleHeight), Color.White,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            int metaTop = titleTop + titleHeight + D(5);
            string metadata = string.Join("   ·   ", new[] {
                item.YearText, item.DurationSeconds > 0 ? DurationLabel(item.DurationSeconds) : null,
                item.Rating is > 0 ? $"TMDb {item.Rating:0.0}/10" : null
            }.Where(v => !string.IsNullOrWhiteSpace(v)));
            TextRenderer.DrawText(g, metadata, small, new Rectangle(left, metaTop, width, D(23)), Color.FromArgb(210, 222, 232), TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int pillY = metaTop + D(32), pillX = left;
            using var pillFont = UiFont("Segoe UI", 8.2f);
            // Audio gets the remaining row width instead of the homepage's old 150 px cap.
            _technicalHits.Clear();
            var pills = new[] { (item.QualityLabel, true), (item.FormatLabel, false), (item.AudioLabel, false) };
            foreach (var (text, accent) in pills)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                int remaining = left + width - pillX;
                if (remaining < D(48)) break;
                int w = Math.Min(remaining, TextRenderer.MeasureText(g, text, pillFont, Size.Empty, TextFormatFlags.NoPadding).Width + D(24));
                _technicalHits.Add((new Rectangle(pillX, pillY, w, D(25)), text));
                MediaTechnicalPill.Draw(g, new Rectangle(pillX, pillY, w, D(25)), text, accent ? Theme.Accent : Color.FromArgb(214, 223, 231), Theme.Accent, accent);
                pillX += w + D(8);
            }

            int bodyTop = pillY + D(39);
            int progressTop = actionTop - D(49);
            int castTop = progressTop - D(40);
            int bodyHeight = Math.Min(D(83), castTop - D(10) - bodyTop);
            int bodyLineHeight = TextRenderer.MeasureText("Ag", body, Size.Empty, TextFormatFlags.NoPadding).Height;
            bodyHeight = Math.Max(0, bodyHeight / bodyLineHeight * bodyLineHeight);
            int contentBottom = pillY + D(25);
            if (bodyHeight >= body.Height)
            {
                string overview = string.IsNullOrWhiteSpace(item.Overview)
                    ? (item.MetadataLoaded ? L("Sinossi non disponibile per questo titolo.", "No synopsis available for this title.") : L("Caricamento della sinossi…", "Loading synopsis…")) : item.Overview;
                string fitted = FitHudText(overview, body, new Size(width, bodyHeight));
                bodyHeight = Math.Min(bodyHeight, TextRenderer.MeasureText(fitted, body, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
                TextRenderer.DrawText(g, fitted, body, new Rectangle(left, bodyTop, width, bodyHeight), Color.FromArgb(216, 225, 233),
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                contentBottom = bodyTop + bodyHeight;
            }
            castTop = contentBottom + D(12);
            if (castTop + D(23) <= progressTop - D(10) && !string.IsNullOrWhiteSpace(item.CastLine))
            {
                TextRenderer.DrawText(g, "Cast  " + item.CastLine, small, new Rectangle(left, castTop, width, D(23)), Color.FromArgb(158, 179, 196), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                contentBottom = castTop + D(23);
            }
            actionTop = Math.Min(LibraryTop - D(64), contentBottom + D(24));
            DrawHeroActions(g, actionTop);
        }

        private static string DurationLabel(double seconds)
        {
            int minutes = Math.Max(0, (int)Math.Ceiling(seconds / 60));
            return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
        }

        private void DrawHeroActions(Graphics g, int top)
        {
            _playRect = new Rectangle(HudMargin, top, D(120), D(42));
            _restartRect = CurrentItem?.ResumePositionSeconds > 0 ? new Rectangle(_playRect.Right + D(12), top, D(120), D(42)) : Rectangle.Empty;
            _queueRect = new Rectangle((_restartRect.IsEmpty ? _playRect.Right : _restartRect.Right) + D(12), top, D(120), D(42));
            _moreRect = new Rectangle(_queueRect.Right + D(12), top, D(120), D(42));
            DrawHudButton(g, _playRect, CurrentItem?.ResumePositionSeconds > 0 ? L("Riprendi", "Resume") : L("Riproduci", "Play"), "play", true);
            if (!_restartRect.IsEmpty) DrawHudButton(g, _restartRect, L("Ricomincia", "Restart"), "restart", false);
            DrawHudButton(g, _queueRect, _queuedPath == CurrentItem?.Path ? L("✓ Aggiunto", "✓ Added") : L("+ In coda", "+ Queue"), "queue", false);
            DrawHudButton(g, _moreRect, "Cast", "more", false);
        }

        private void DrawHudButton(Graphics g, Rectangle rect, string text, string zone, bool primary)
        {
            bool hot = Hot(zone);
            using var path = Rounded(Rectangle.Inflate(rect, -1, -1), D(7));
            using var fill = new SolidBrush(primary ? (hot ? ControlPaint.Light(Theme.Accent, .15f) : Theme.Accent) : Color.FromArgb(hot ? 225 : 182, hot ? 29 : 12, hot ? 42 : 21, hot ? 55 : 30));
            using var border = new Pen(_keyboardFocus && Focused && _keyboardZone == zone ? Color.White : Color.FromArgb(hot ? 115 : 44, 211, 229, 242), Math.Max(1, UiScale));
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            using var font = UiFont("Segoe UI Semibold", 9.5f);
            var label = rect;
            if (primary)
            {
                using var ink = new SolidBrush(Color.White);
                int x = rect.Left + D(22), cy = rect.Top + rect.Height / 2;
                g.FillPolygon(ink, new[] { new Point(x, cy - D(6)), new Point(x, cy + D(6)), new Point(x + D(9), cy) });
                label.X += D(22); label.Width -= D(22);
            }
            TextRenderer.DrawText(g, text, font, label, Color.FromArgb(244, 248, 251), TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawLibrary(Graphics g)
        {
            int margin = HudMargin;
            using var title = UiFont("Segoe UI Semibold", 10.7f);
            using var small = UiFont("Segoe UI", 8.5f);
            TextRenderer.DrawText(g, FilterActive ? L("Risultati", "Results") : L("Film e serie TV", "Movies and TV shows"), title,
                new Rectangle(margin, LibraryTop, _searchRect.Width / 2, D(23)), Color.FromArgb(239, 244, 248), TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string filterName = _filterMode == 1 ? L(" · Film", " · Movies") : _filterMode == 2 ? L(" · Serie TV", " · TV shows") : "";
            TextRenderer.DrawText(g, L($"{FilteredCount} titoli", $"{FilteredCount} titles") + filterName, small,
                new Rectangle(margin + _searchRect.Width / 2, LibraryTop, _searchRect.Width / 2, D(23)), Color.FromArgb(151, 173, 192), TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            // Fonte: due voci di solo testo; un trattino colorato sotto quella attiva. Nessun riquadro.
            using (var sourceFont = UiFont("Segoe UI Semibold", 10.5f))
            for (int option = 0; option < 2; option++)
            {
                var half = new Rectangle(_sourceRect.Left + option * _sourceRect.Width / 2, _sourceRect.Top, _sourceRect.Width / 2, _sourceRect.Height);
                bool active = (option == 1) == _networkSource;
                bool hot = _hoverZone == (option == 0 ? "source-library" : "source-network");
                string text = option == 0 ? L("Libreria", "Library") : L("Rete", "Network");
                TextRenderer.DrawText(g, text, sourceFont, half, active ? Color.White : hot ? Color.FromArgb(214, 226, 236) : Color.FromArgb(151, 173, 192),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (active)
                {
                    int textWidth = TextRenderer.MeasureText(g, text, sourceFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    using var mark = new SolidBrush(Theme.Accent);
                    int thickness = Math.Max(2, D(2));
                    g.FillRectangle(mark, half.Left + (half.Width - textWidth) / 2, half.Bottom - D(7), textWidth, thickness);
                }
            }
            using (var path = Rounded(_searchRect, D(7)))
            using (var fill = new SolidBrush(Color.FromArgb(4, 10, 16)))
            using (var border = new Pen(_searchBox.Focused || Hot("search") ? Theme.Accent : Color.FromArgb(52, 190, 210, 228)))
            { g.FillPath(fill, path); g.DrawPath(border, path); }
            using (var pen = new Pen(Color.FromArgb(166, 189, 207), Math.Max(1.2f, UiScale)))
            {
                var lens = new Rectangle(_searchRect.Left + D(13), _searchRect.Top + D(11), D(12), D(12));
                g.DrawEllipse(pen, lens);
                g.DrawLine(pen, lens.Right, lens.Bottom, lens.Right + D(4), lens.Bottom + D(4));
            }
            using (var searchFont = UiFont("Segoe UI", 9.3f))
                TextRenderer.DrawText(g, string.IsNullOrEmpty(_query) ? L("Cerca titolo o cast", "Search title or cast") : _query, searchFont, _lastSearchLayout, string.IsNullOrEmpty(_query) ? Color.FromArgb(142, 163, 181) : Color.White, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (var separator = new Pen(Color.FromArgb(48, 176, 197, 213)))
                g.DrawLine(separator, _filterRect.Left, _filterRect.Top + D(9), _filterRect.Left, _filterRect.Bottom - D(9));
            if (Hot("filter"))
            {
                using var fill = new SolidBrush(Color.FromArgb(28, Theme.Accent));
                using var shape = Rounded(Rectangle.Inflate(_filterRect, -D(4), -D(4)), D(4));
                g.FillPath(fill, shape);
            }
            using (var pen = new Pen(_filterMode != 0 ? Theme.Accent : Color.FromArgb(192, 208, 221), Math.Max(1.2f, UiScale)))
            {
                for (int row = 0; row < 3; row++)
                {
                    int y = _filterRect.Top + D(12 + row * 7);
                    g.DrawLine(pen, _filterRect.Left + D(12), y, _filterRect.Right - D(12), y);
                    int x = _filterRect.Left + D(row == 1 ? 17 : 26);
                    using var fill = new SolidBrush(Color.FromArgb(4, 10, 16));
                    g.FillEllipse(fill, x - D(2), y - D(2), D(4), D(4));
                    g.DrawEllipse(pen, x - D(2), y - D(2), D(4), D(4));
                }
            }
            _cardHits.Clear();
            int columns = VisibleColumns(), gap = D(14);
            int cardW = (Width - 2 * margin - (columns - 1) * gap) / columns;
            int cardH = Math.Min(D(133), (int)Math.Round(cardW * 9d / 16));
            _stripRect = new Rectangle(margin, LibraryTop + D(86), Width - 2 * margin, cardH);
            if (FilteredCount <= 0)
            {
                TextRenderer.DrawText(g, L("Nessun risultato. Prova un altro titolo o cambia filtro.", "No results. Try another title or change the filter."), small, _stripRect, Color.FromArgb(188, 205, 220), TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            for (int slot = 0; slot < Math.Min(columns, FilteredCount); slot++)
            {
                var item = ItemAtFilteredPosition(_firstVisible + slot, out int index);
                if (item == null) continue;
                var rect = new Rectangle(margin + slot * (cardW + gap), _stripRect.Top, cardW, cardH);
                _cardHits.Add((rect, index));
                DrawCard(g, item, rect, index == _index, index == _hoverItemIndex);
            }
        }

        private void DrawNavigation(Graphics g)
        {
            int width = Math.Max(30, HudMargin - D(14));
            _leftRect = new Rectangle(D(4), _stripRect.Top, width, _stripRect.Height);
            _rightRect = new Rectangle(Width - width - D(4), _stripRect.Top, width, _stripRect.Height);
            DrawCinecoreChevron(g, _leftRect, false, FilteredCount > 1, Hot("left"));
            DrawCinecoreChevron(g, _rightRect, true, FilteredCount > 1, Hot("right"));
        }

        private void DrawEmpty(Graphics g)
        {
            _playRect = _queueRect = _moreRect = Rectangle.Empty;
            DrawTopChrome(g);
            using var title = UiFont("Segoe UI Semibold", 28);
            using var body = UiFont("Segoe UI", 11);
            TextRenderer.DrawText(g, FilterActive ? L("Nessun titolo trovato", "No titles found") : L("Il tuo cinema, qui.", "Your cinema, here."), title,
                new Rectangle(HudMargin, D(130), Width - 2 * HudMargin, D(60)), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            string bodyText = FilterActive
                ? L("Modifica la ricerca o seleziona tutti i titoli.", "Change your search or select all titles.")
                : _emptyMessageOverride ?? L("Aggiungi film e serie dalla libreria per esplorarli in Spotlight.", "Add movies and shows in the library to explore them in Spotlight.");
            TextRenderer.DrawText(g, bodyText, body,
                new Rectangle(HudMargin, D(204), Width - 2 * HudMargin, D(60)), Color.FromArgb(178, 199, 216), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            DrawLibrary(g);
            DrawNavigation(g);
        }

        internal bool HandleHudCommand(Keys key)
        {
            if (key == Keys.Tab || key == (Keys.Shift | Keys.Tab))
            {
                if (_castSheetOpen) { _keyboardFocus = true; _keyboardZone = "cast-close"; Focus(); Invalidate(); return true; }
                string[] zones = _restartRect.IsEmpty ? new[] { "play", "queue", "more", "search", "filter", "left", "right" } : new[] { "play", "restart", "queue", "more", "search", "filter", "left", "right" };
                int i = Array.IndexOf(zones, _searchBox.Focused ? "search" : _keyboardZone);
                int next = (i + ((key & Keys.Shift) != 0 ? zones.Length - 1 : 1)) % zones.Length;
                _keyboardZone = zones[next]; _keyboardFocus = true;
                ClearPointerHover();
                if (_keyboardZone == "search") _searchBox.Focus(); else Focus();
                Invalidate(); return true;
            }
            if (key == Keys.Escape || key == Keys.BrowserBack)
            {
                if (TryHandleBackKey()) return true;
                CloseRequested?.Invoke(); return true;
            }
            if ((key == Keys.Enter || key == Keys.Space) && !_searchBox.Focused)
            {
                if (_castSheetOpen) { CloseCastSheet(); return true; }
                switch (_keyboardFocus ? _keyboardZone : "play")
                {
                    case "restart": if (CurrentItem != null) RestartRequested?.Invoke(CurrentItem.Path); break;
                    case "queue": QueueCurrent(); break;
                    case "more": OpenCastSheet(); break;
                    case "filter": ShowFilterMenu(); break;
                    case "left": MovePrevious(); break;
                    case "right": MoveNext(); break;
                    case "close": CloseRequested?.Invoke(); break;
                    default: OpenSelected(); break;
                }
                return true;
            }
            return false;
        }

        private readonly System.Collections.Generic.Dictionary<(string Text, float Size, int Width, int Height), string> _fittedText = new();
        private readonly System.Collections.Generic.List<(Rectangle Bounds, string Text)> _technicalHits = new();
        private string _activeTip = string.Empty;

        private string FitHudText(string text, Font font, Size bounds)
        {
            var key = (text, font.Size, bounds.Width, bounds.Height);
            if (_fittedText.TryGetValue(key, out string? fitted)) return fitted;
            bool Fits(string value) => TextRenderer.MeasureText(value, font, new Size(bounds.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height <= bounds.Height;
            fitted = text;
            if (!Fits(text))
            {
                int lo = 0, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (Fits(text[..mid].TrimEnd() + "…")) lo = mid; else hi = mid - 1;
                }
                int cut = text.LastIndexOf(' ', Math.Max(0, lo - 1));
                if (cut > lo / 2) lo = cut;
                fitted = text[..lo].TrimEnd() + "…";
            }
            if (_fittedText.Count > 128) _fittedText.Clear();
            _fittedText[key] = fitted;
            return fitted;
        }

        private void ShowFilterMenu()
        {
            // The inline filter cycles all / films / series, as in the original HUD.
            _filterMode = (_filterMode + 1) % 3;
            RebuildFilter(keepCurrent: true);
        }

        private void UpdateHoverTip(Point location)
        {
            string tip = _filterRect.Contains(location) ? L("Filtro: tutti i titoli, film, serie TV", "Filter: all titles, movies, TV shows") : _technicalHits.FirstOrDefault(hit => hit.Bounds.Contains(location)).Text ?? string.Empty;
            if (tip.Length == 0)
            {
                var card = _cardHits.FirstOrDefault(hit => hit.Bounds.Contains(location));
                if (!card.Bounds.IsEmpty && card.ItemIndex >= 0 && card.ItemIndex < _items.Count) tip = _items[card.ItemIndex].Title;
            }
            if (tip == _activeTip) return;
            _activeTip = tip;
            _hudTip.SetToolTip(this, tip);
        }

        private void QueueCurrent()
        {
            if (CurrentItem == null) return;
            QueueRequested?.Invoke(CurrentItem.Path);
            _queuedPath = CurrentItem.Path;
            Invalidate(_queueRect);
        }
    }
}
