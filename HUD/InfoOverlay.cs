#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025
{
    /// <summary>
    /// Pannello info di riproduzione: vive accanto all'HUD ma non ne segue il fade, e resta a schermo
    /// finche' l'utente non lo chiude. Tre schede (Generale, Video, Audio); in ogni scheda i gruppi hanno
    /// un'icona e un titolo, e i dati stanno su due colonne con l'etichetta sopra e il valore sotto.
    /// Niente righe senza dato. Tutte le misure seguono la scala dello schermo.
    /// </summary>
    internal sealed class InfoOverlay : Control
    {
        public event Action? CloseRequested;

        public struct Stats
        {
            public string Title;
            public string VideoIn, VideoOut, VideoCodec, VideoPrimaries, VideoTransfer, VideoBitrateNow, VideoBitrateAvg;
            public string AudioIn, AudioOut, AudioBitrateNow, AudioBitrateAvg;
            public string AudioSync; // stato della sincronizzazione audio (vuoto = originale del file)
            public string Renderer, HdrMode;
            public bool Upscaling, Bitstream, RtxHdr;
            // Dati aggiuntivi
            public string FileName, FileSize, Container, Display;
            public string ResolutionTag, DynamicRangeTag, AudioTag;
            public double PositionSec, DurationSec;
            public int DroppedFrames; // -1 = non disponibile
            // Letti dai componenti (LAV, madVR, MPC Video Renderer, mpv), non stimati da noi.
            public string VideoDecode, VideoFrames, VideoMatrix, AudioDecode, AudioDevice, Origin, Buffer, ProcessingDetail;
            // Il film, non solo il file (usati dalla scheda del cast e dal telecomando, non piu' da questo pannello).
            public string PosterPath, MetaLine, Overview, CastLine;
        }

        private sealed record Cell(string Label, string Value, bool Wide = false);
        private sealed record Group(string Icon, string Title, List<Cell> Cells);

        private Stats _s;
        private Rectangle _closeRect = Rectangle.Empty;
        private readonly Rectangle[] _tabRects = new Rectangle[3];
        private bool _closeHot;
        private int _tab, _tabHot = -1;
        private string _uiLanguage = "it";

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool AutoHeight { get; set; } = true;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MinCardHeight { get; set; } = 420;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MaxCardHeight { get; set; } = 700;

        private static Color TextMain => HUD.Theme.Text;
        private static Color TextSoft => HUD.Theme.SubtleText;
        private static Color TextMuted => HUD.Theme.Muted;
        private bool UiEnglish => string.Equals(_uiLanguage, "en", StringComparison.OrdinalIgnoreCase);
        private string L(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(UiEnglish ? english : italian);

        // Misure al 100%: D() le porta alla scala dello schermo (i caratteri, in punti, la seguono da soli).
        // Sugli schermi grandi il pannello cresce con la finestra (come l'HUD): a 1440 righe e' un terzo piu' grande.
        private float UiScale
        {
            get
            {
                float dpi = Math.Max(1f, DeviceDpi / 96f);
                Size host = Parent?.ClientSize ?? Size.Empty;
                float window = host.Width > 0 && host.Height > 0 ? Math.Min(host.Width / 1920f, host.Height / 1080f) : 1f;
                return Math.Max(dpi, Math.Min(window, 2.4f));
            }
        }
        /// <summary>La scala usata dal pannello: chi lo dispone gli da' larghezza e altezza massime in proporzione.</summary>
        [Browsable(false)]
        public float LayoutScale => UiScale;
        private Font UiFont(string family, float points) => AppFonts.CreateForDpi(family, points, FontStyle.Regular, 96f * UiScale);
        private int D(float value) => (int)Math.Round(value * UiScale);
        private int Pad => D(22);
        private int TabsH => D(62);
        private int GroupHeaderH => D(40);
        private int CellH => D(46);
        private int WideCellH => D(46);
        private int GroupGap => D(10);
        private int Indent => D(44);
        /// <summary>Il pannello non mostra piu' locandina e trama: la larghezza e' sempre quella stretta.</summary>
        public bool HasIdentity => false;

        public InfoOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = MinCardHeight;
            Cursor = Cursors.Default;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var host = FindForm();
            if (host != null && host.TransparencyKey != Color.Empty)
            {
                e.Graphics.Clear(host.TransparencyKey);
                return;
            }

            if (BackColor != Color.Transparent)
                base.OnPaintBackground(e);
        }

        public void SetStats(Stats s)
        {
            _s = s;
            if (AutoHeight) AdjustHeightToContent(Width);
            Invalidate();
        }

        public void SetLanguage(string? language)
        {
            _uiLanguage = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (AutoHeight) AdjustHeightToContent(Width);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            if (AutoHeight) AdjustHeightToContent(Width);
            Invalidate();
        }

        public void AdjustHeightToContent(int availableWidth)
        {
            if (!AutoHeight || availableWidth <= 0) return;
            int next = Math.Min(MaxCardHeight, CalcPreferredHeight());
            if (Math.Abs(Height - next) > 2)
                Height = next;
        }

        // ===== Dati =====
        private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "n/d", StringComparison.OrdinalIgnoreCase);

        /// <summary>Un dato tecnico diviso nelle sue parti: la prima e' quella che conta, le altre sono dettagli.</summary>
        private static List<string> Parts(params string?[] values)
        {
            var parts = new List<string>();
            foreach (string? value in values)
            {
                if (!HasValue(value)) continue;
                foreach (string raw in value!.Split(new[] { " · ", " • ", "·", "•" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string part = Regex.Replace(raw.Trim(), @"(\d{3,4})x(\d{3,4})", "$1×$2");
                    part = Regex.Replace(part, @"\b(\d{1,2})-bit\b", "$1 bit", RegexOptions.IgnoreCase);
                    if (part.Length > 0 && !string.Equals(part, "n/d", StringComparison.OrdinalIgnoreCase) &&
                        !parts.Contains(part, StringComparer.OrdinalIgnoreCase))
                        parts.Add(part);
                }
            }
            return parts;
        }

        private static List<string> Without(List<string> parts, params string[] patterns)
        {
            parts.RemoveAll(part => patterns.Any(pattern => Regex.IsMatch(part, pattern, RegexOptions.IgnoreCase)));
            return parts;
        }

        private static string Join(List<string> parts) => string.Join("   ", parts);
        private static string First(List<string> parts) => parts.Count > 0 ? parts[0] : "";
        private static string Find(string pattern, params string?[] sources)
        {
            foreach (string? source in sources)
            {
                if (!HasValue(source)) continue;
                var match = Regex.Match(source!, pattern, RegexOptions.IgnoreCase);
                if (match.Success) return match.Value.Trim();
            }
            return "";
        }

        /// <summary>"22.574 kbps" diventa "22,6 Mbps": un numero leggibile invece di cinque cifre.</summary>
        private string Rate(string? value)
        {
            if (!HasValue(value)) return "";
            var match = Regex.Match(value!, @"([\d.,\s]+)\s*(kbps|kb/s|mbps|mb/s)", RegexOptions.IgnoreCase);
            if (!match.Success) return value!.Trim();
            string digits = Regex.Replace(match.Groups[1].Value, @"[^\d]", "");
            if (!double.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out double number)) return value!.Trim();
            bool mega = match.Groups[2].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase);
            // Con i megabit le cifre dopo il separatore sono decimali: si rilegge il numero com'e'.
            if (mega) return value!.Trim();
            var culture = UiEnglish ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("it-IT");
            return number >= 1000 ? (number / 1000d).ToString("0.0", culture) + " Mbps" : number.ToString("0", culture) + " kbps";
        }

        private string Duration()
        {
            double seconds = _s.DurationSec;
            if (!(seconds > 1)) return "";
            int hours = (int)(seconds / 3600), minutes = (int)(seconds % 3600 / 60), rest = (int)(seconds % 60);
            return hours > 0 ? $"{hours}h {minutes:00}m" : $"{minutes}m {rest:00}s";
        }

        private string Resolution()
        {
            string found = Find(@"\d{3,4}\s*[x×]\s*\d{3,4}", _s.VideoIn).Replace("x", "×").Replace(" ", "");
            string tag = HasValue(_s.ResolutionTag) ? _s.ResolutionTag.Trim() : "";
            if (found.Length == 0) return tag;
            return tag.Length > 0 && !found.Contains(tag, StringComparison.OrdinalIgnoreCase) ? $"{found} ({tag})" : found;
        }

        private (string Dropped, string Detail) Frames()
        {
            int dropped = _s.DroppedFrames;
            string detail = "";
            if (HasValue(_s.VideoFrames))
            {
                // "12.345 · persi 0 · 23,976 fps reali": i persi sono la parte che lo dice, non il primo numero.
                var lost = Regex.Match(_s.VideoFrames, @"(?:persi|dropped)\s*(\d[\d.]*)", RegexOptions.IgnoreCase);
                if (lost.Success && int.TryParse(lost.Groups[1].Value.Replace(".", ""), out int parsed)) dropped = parsed;
                detail = Join(Without(Parts(_s.VideoFrames), @"^(?:persi|dropped)\b"));
            }
            return (dropped >= 0 ? dropped.ToString("N0", CultureInfo.CurrentCulture) : "", detail);
        }

        private string AudioOutput()
        {
            var output = Parts(_s.AudioOut);
            string kind = _s.Bitstream ? "Bitstream" : "PCM";
            if (output.Count > 0 && !output[0].StartsWith(kind, StringComparison.OrdinalIgnoreCase)) output.Add(kind);
            return Join(output);
        }

        private string AudioDevice()
        {
            var device = Parts(_s.AudioDevice);
            if (device.Count > 0)
            {
                // "DirectSound: marantz-AVR (NVIDIA High Definition Audio)": prima il dispositivo, poi come ci si arriva.
                var named = Regex.Match(device[0], @"^(?<api>[A-Za-z ]+):\s*(?<name>.+?)(?:\s*\((?<adapter>[^)]*)\))?$");
                if (named.Success)
                {
                    device[0] = named.Groups["name"].Value.Trim();
                    if (named.Groups["adapter"].Success && named.Groups["adapter"].Value.Length > 0) device.Add(named.Groups["adapter"].Value.Trim());
                }
            }
            return Join(device);
        }

        private string Processing() => Join(Parts(_s.Upscaling ? "Upscaling" : "", _s.RtxHdr ? "RTX HDR" : "", _s.ProcessingDetail));
        private string Colour() => Join(Without(Parts(_s.VideoPrimaries, _s.VideoTransfer, _s.HdrMode), @"^livelli\b", @"^levels\b", @"^YCbCr\b"));

        private static Group Make(string icon, string title, params Cell[] cells) =>
            new(icon, title, cells.Where(cell => HasValue(cell.Value)).ToList());

        private List<Group> Groups()
        {
            string fps = Find(@"\d+(?:[.,]\d+)?\s*fps", _s.VideoIn, _s.VideoOut);
            string channels = Find(@"\d+(?:\.\d)?\s*(?:ch\b|canali|channels)|\b[1-9]\.[0-2](?:\.\d)?\b|\bstereo\b|\bmono\b", _s.AudioIn, _s.AudioTag);
            string sampling = Find(@"\d+(?:[.,]\d+)?\s*k?Hz", _s.AudioIn, _s.AudioOut);
            var frames = Frames();
            var groups = new List<Group>();
            if (_tab == 1)
            {
                groups.Add(Make("movie", L("Flusso", "Stream"),
                    new Cell("Codec", _s.VideoCodec ?? ""),
                    new Cell(L("Risoluzione", "Resolution"), Resolution()),
                    new Cell(L("Frequenza fotogrammi", "Frame rate"), fps),
                    new Cell("HDR", HasValue(_s.DynamicRangeTag) ? _s.DynamicRangeTag.Trim() : ""),
                    new Cell(L("Bitrate attuale", "Current bitrate"), Rate(_s.VideoBitrateNow)),
                    new Cell(L("Bitrate medio", "Average bitrate"), Rate(_s.VideoBitrateAvg)),
                    new Cell(L("Sorgente", "Source"), Join(Parts(_s.VideoIn)), true),
                    new Cell(L("Colore", "Colour"), Colour(), true)));
                groups.Add(Make("renderer", L("Resa", "Rendering"),
                    new Cell(L("Decodifica", "Decoding"), Join(Parts(_s.VideoDecode)), true),
                    new Cell(L("Uscita", "Output"), Join(Without(Parts(_s.VideoOut), @"^schermo\b", @"^display\b")), true),
                    new Cell(L("Fotogrammi persi", "Dropped frames"), frames.Dropped),
                    new Cell(L("Fotogrammi", "Frames"), frames.Detail, true),
                    new Cell("Renderer", Join(Parts(_s.Renderer)), true),
                    new Cell(L("Elaborazione", "Processing"), Processing())));
            }
            else if (_tab == 2)
            {
                groups.Add(Make("music", L("Traccia", "Track"),
                    new Cell(L("Sorgente", "Source"), Join(Parts(_s.AudioIn)), true),
                    new Cell(L("Canali", "Channels"), channels),
                    new Cell(L("Frequenza di campionamento", "Sample rate"), sampling),
                    new Cell(L("Bitrate attuale", "Current bitrate"), Rate(_s.AudioBitrateNow)),
                    new Cell(L("Bitrate medio", "Average bitrate"), Rate(_s.AudioBitrateAvg))));
                groups.Add(Make("amplifier", L("Uscita", "Output"),
                    new Cell(L("Uscita", "Output"), AudioOutput(), true),
                    // In bitstream la traccia passa intatta: la riga "decodifica" non direbbe nulla.
                    new Cell(L("Decodifica", "Decoding"), _s.Bitstream ? "" : Join(Parts(_s.AudioDecode)), true),
                    new Cell(L("Dispositivo", "Device"), AudioDevice(), true),
                    new Cell(L("Sincronia", "Sync"), Join(Parts(_s.AudioSync)), true)));
            }
            else
            {
                string size = HasValue(_s.FileSize) && !_s.FileSize.Trim().StartsWith("0 ") ? _s.FileSize.Trim() : "";
                groups.Add(Make("folder", "File",
                    new Cell(L("Formato contenitore", "Container"), _s.Container ?? ""),
                    new Cell(L("Dimensione", "Size"), size),
                    new Cell(L("Durata", "Duration"), Duration()),
                    new Cell(L("Bitrate medio", "Average bitrate"), Rate(_s.VideoBitrateAvg))));
                groups.Add(Make("movie", "Video",
                    new Cell("Codec", _s.VideoCodec ?? ""),
                    new Cell(L("Risoluzione", "Resolution"), Resolution()),
                    new Cell(L("Frequenza fotogrammi", "Frame rate"), fps),
                    new Cell(L("Spazio colore", "Colour space"), First(Parts(_s.VideoPrimaries))),
                    new Cell("HDR", HasValue(_s.DynamicRangeTag) ? _s.DynamicRangeTag.Trim() : ""),
                    new Cell("Bitrate", Rate(_s.VideoBitrateNow))));
                groups.Add(Make("volume-high", "Audio",
                    new Cell(L("Traccia", "Track"), First(Parts(_s.AudioIn))),
                    new Cell(L("Canali", "Channels"), channels),
                    new Cell(L("Uscita", "Output"), First(Parts(_s.AudioOut)).Length > 0 ? First(Parts(_s.AudioOut)) : (_s.Bitstream ? "Bitstream" : "")),
                    new Cell(L("Frequenza di campionamento", "Sample rate"), sampling),
                    new Cell(L("Decodifica", "Decoding"), _s.Bitstream ? "Bitstream" : First(Parts(_s.AudioDecode))),
                    new Cell("Bitrate", Rate(_s.AudioBitrateNow))));
                groups.Add(Make("settings", L("Sistema", "System"),
                    new Cell("Renderer", First(Parts(_s.Renderer))),
                    new Cell(L("Elaborazione", "Processing"), First(Parts(_s.Upscaling ? "Upscaling" : "", _s.RtxHdr ? "RTX HDR" : "", _s.ProcessingDetail))),
                    new Cell(L("Schermo", "Display"), Join(Without(Parts(_s.Display), @"^\d+ bit$"))),
                    new Cell(L("Origine", "Origin"), First(Parts(_s.Origin))),
                    new Cell("Buffer", First(Parts(_s.Buffer)))));
            }
            groups.RemoveAll(group => group.Cells.Count == 0);
            return groups;
        }

        /// <summary>Le celle di un gruppo disposte in righe: due per riga, quelle larghe da sole.</summary>
        private static List<(Cell Left, Cell? Right)> Rows(Group group)
        {
            var rows = new List<(Cell, Cell?)>();
            Cell? pending = null;
            foreach (Cell cell in group.Cells)
            {
                if (cell.Wide)
                {
                    if (pending != null) { rows.Add((pending, null)); pending = null; }
                    rows.Add((cell, null));
                }
                else if (pending == null) pending = cell;
                else { rows.Add((pending, cell)); pending = null; }
            }
            if (pending != null) rows.Add((pending, null));
            return rows;
        }

        private int RowHeight((Cell Left, Cell? Right) row) => row.Left.Wide ? WideCellH : CellH;
        private int GroupHeight(Group group) => GroupGap + GroupHeaderH + Rows(group).Sum(RowHeight) + GroupGap;

        /// <summary>Il pannello e' sempre a una colonna di gruppi (le celle sono gia' affiancate a due a due).</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Columns { get; set; } = 1;

        public int PreferredHeightFor(int columns) => TabsH + Groups().Sum(GroupHeight) + D(10);

        private int CalcPreferredHeight() => PreferredHeightFor(1);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle card = new(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            if (card.Width <= 0 || card.Height <= 0)
                return;

            using (var path = RoundedRect(card, D(18)))
            using (var bg = new SolidBrush(Color.FromArgb(240, HUD.Theme.IsLight ? HUD.Theme.Panel : Color.FromArgb(10, 13, 18))))
            using (var edge = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 30 : 22, HUD.Theme.Text)))
            {
                g.FillPath(bg, path);
                g.DrawPath(edge, path);
            }

            int x = card.X + Pad;
            int w = card.Width - Pad * 2;

            using var tabFont = UiFont("Segoe UI Semibold", 10.5f);
            using var groupFont = UiFont("Segoe UI Semibold", 10.5f);
            using var labelFont = UiFont("Segoe UI", 8.5f);
            using var valueFont = UiFont("Segoe UI", 10f);
            using var rule = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 26 : 20, HUD.Theme.Text));
            const TextFormatFlags oneLine = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

            // ===== Schede =====
            int closeSide = D(28);
            _closeRect = new Rectangle(card.Right - D(16) - closeSide, card.Top + (TabsH - closeSide) / 2, closeSide, closeSide);
            HUD.Theme.DrawCloseButton(g, _closeRect, _closeHot);
            string[] tabs = { L("Generale", "General"), "Video", "Audio" };
            string[] tabIcons = { "info", "video", "audio" };
            int tx = x, icon = D(20);
            for (int i = 0; i < tabs.Length; i++)
            {
                int textWidth = TextRenderer.MeasureText(g, tabs[i], tabFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                var rect = new Rectangle(tx, card.Top, D(6) + icon + D(8) + textWidth + D(14), TabsH);
                _tabRects[i] = rect;
                bool active = i == _tab;
                Color tone = active ? TextMain : i == _tabHot ? TextSoft : TextMuted;
                DrawIcon(g, tabIcons[i], new Rectangle(rect.Left + D(6), rect.Top + (TabsH - icon) / 2, icon, icon), active ? HUD.Theme.Accent : tone);
                TextRenderer.DrawText(g, tabs[i], tabFont, new Rectangle(rect.Left + D(6) + icon + D(8), rect.Top, textWidth + 2, TabsH), tone, oneLine);
                if (active)
                {
                    using var mark = new Pen(HUD.Theme.Accent, Math.Max(2f, D(2)));
                    g.DrawLine(mark, rect.Left, card.Top + TabsH - 1, rect.Right, card.Top + TabsH - 1);
                }
                tx = rect.Right + D(12);
            }
            g.DrawLine(rule, x, card.Top + TabsH, x + w, card.Top + TabsH);

            // ===== Gruppi =====
            int y = card.Top + TabsH;
            int valueLeft = x + Indent, columnWidth = (w - Indent) / 2;
            var groups = Groups();
            for (int index = 0; index < groups.Count; index++)
            {
                Group group = groups[index];
                var rows = Rows(group);
                // Un gruppo compare solo se ci stanno il titolo e almeno una riga intera.
                if (y + GroupGap + GroupHeaderH + RowHeight(rows[0]) > Height - D(6)) break;
                if (index > 0) g.DrawLine(rule, x, y, x + w, y);
                y += GroupGap;
                int groupIcon = D(22);
                DrawIcon(g, group.Icon, new Rectangle(x + D(4), y + (GroupHeaderH - groupIcon) / 2, groupIcon, groupIcon), TextMain);
                TextRenderer.DrawText(g, group.Title, groupFont, new Rectangle(valueLeft, y, w - Indent, GroupHeaderH), TextMain, oneLine);
                y += GroupHeaderH;
                foreach (var row in rows)
                {
                    int height = RowHeight(row);
                    if (y + height > Height - D(6)) break;
                    DrawCell(g, labelFont, valueFont, row.Left, new Rectangle(valueLeft, y, row.Left.Wide ? w - Indent : columnWidth - D(10), height));
                    if (row.Right != null)
                        DrawCell(g, labelFont, valueFont, row.Right, new Rectangle(valueLeft + columnWidth, y, columnWidth, height));
                    y += height;
                }
                y += GroupGap;
            }
        }

        /// <summary>Etichetta piccola in tono basso, valore sotto; una cella larga occupa tutta la riga.</summary>
        private void DrawCell(Graphics g, Font labelFont, Font valueFont, Cell cell, Rectangle rect)
        {
            const TextFormatFlags oneLine = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, cell.Label, labelFont, new Rectangle(rect.Left, rect.Top, rect.Width, D(18)), TextMuted, oneLine);
            TextRenderer.DrawText(g, cell.Value, valueFont, new Rectangle(rect.Left, rect.Top + D(18), rect.Width, D(22)), TextMain, oneLine);
        }

        // Le icone sono quelle dell'app (Assets/Icons.Uniform), colorate al momento e tenute in memoria.
        private readonly Dictionary<(string Key, int Size, int Tint), Bitmap?> _icons = new();

        private void DrawIcon(Graphics g, string key, Rectangle r, Color color)
        {
            var id = (key, r.Width, color.ToArgb());
            if (!_icons.TryGetValue(id, out Bitmap? icon))
            {
                try
                {
                    string? file = AssetIconService.Resolve(key);
                    icon = file == null ? null : AssetIconService.RenderSvg(file, r.Width, color);
                }
                catch { icon = null; }
                _icons[id] = icon;
            }
            if (icon != null) g.DrawImage(icon, r);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Bitmap? icon in _icons.Values) { try { icon?.Dispose(); } catch { } }
                _icons.Clear();
            }
            base.Dispose(disposing);
        }

        private int TabAt(Point p)
        {
            for (int i = 0; i < _tabRects.Length; i++)
                if (_tabRects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hot = _closeRect.Contains(e.Location);
            int tab = TabAt(e.Location);
            Cursor = hot || tab >= 0 ? Cursors.Hand : Cursors.Default;
            if (hot != _closeHot) { _closeHot = hot; Invalidate(Rectangle.Inflate(_closeRect, 2, 2)); }
            if (tab != _tabHot) { _tabHot = tab; Invalidate(new Rectangle(0, 0, Width, TabsH + 2)); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_closeHot) { _closeHot = false; Invalidate(Rectangle.Inflate(_closeRect, 2, 2)); }
            if (_tabHot >= 0) { _tabHot = -1; Invalidate(new Rectangle(0, 0, Width, TabsH + 2)); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _closeRect.Contains(e.Location))
            {
                CloseRequested?.Invoke();
                return;
            }
            if (e.Button == MouseButtons.Left && TabAt(e.Location) is int tab && tab >= 0 && tab != _tab)
            {
                _tab = tab;
                if (AutoHeight) AdjustHeightToContent(Width);
                Invalidate();
                return;
            }
            base.OnMouseUp(e);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int rad = Math.Max(1, Math.Min(radius, Math.Min(Math.Max(1, r.Width), Math.Max(1, r.Height)) / 2));
            int d = rad * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
