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
    /// finche' l'utente non lo chiude. In alto titolo e file, poi i tre numeri che cambiano mentre il
    /// film va (bitrate video, bitrate audio, fotogrammi persi) in grande, poi video, audio e sistema.
    /// Ogni riga ha il dato principale in evidenza e i dettagli accanto in tono piu' basso, separati
    /// dallo spazio: niente punti, niente righe senza dato, niente ripetizioni.
    /// Tutte le misure seguono la scala dello schermo.
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
        }

        private Stats _s;
        private Rectangle _closeRect = Rectangle.Empty;
        private bool _closeHot;
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
        private string L(string italian, string english) => UiEnglish ? english : italian;

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
        // I caratteri in punti seguono gia' la scala dello schermo: qui solo la parte in piu'.
        private float FontScale => UiScale / Math.Max(1f, DeviceDpi / 96f);
        private Font UiFont(string family, float points) => AppFonts.CreateForDpi(family, points, FontStyle.Regular, 96f * UiScale);
        private int D(float value) => (int)Math.Round(value * UiScale);
        private int Pad => D(22);
        private int RowH => D(28);
        private int SectionHeaderH => D(34);
        private int SectionGap => D(10);
        private int SectionPad => D(2);
        private int HeaderH => D(74);
        private int ChipsH => D(34);
        private int HeroH => D(84);

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

        private List<(string Label, string Value, string Detail)> Hero()
        {
            var hero = new List<(string, string, string)>();
            string videoNow = Rate(_s.VideoBitrateNow), videoAvg = Rate(_s.VideoBitrateAvg);
            if (videoNow.Length > 0 || videoAvg.Length > 0)
                hero.Add((L("Video", "Video"), videoNow.Length > 0 ? videoNow : videoAvg, videoNow.Length > 0 && videoAvg.Length > 0 ? L("media ", "average ") + videoAvg : ""));
            string audioNow = Rate(_s.AudioBitrateNow), audioAvg = Rate(_s.AudioBitrateAvg);
            if (audioNow.Length > 0 || audioAvg.Length > 0)
                hero.Add((L("Audio", "Audio"), audioNow.Length > 0 ? audioNow : audioAvg, audioNow.Length > 0 && audioAvg.Length > 0 && audioAvg != audioNow ? L("media ", "average ") + audioAvg : ""));
            int dropped = _s.DroppedFrames;
            string detail = "";
            if (HasValue(_s.VideoFrames))
            {
                var first = Regex.Match(_s.VideoFrames, @"\d[\d.]*");
                if (first.Success && int.TryParse(first.Value.Replace(".", ""), out int parsed)) dropped = parsed;
                detail = string.Join("   ", Parts(_s.VideoFrames).Skip(1));
            }
            if (dropped >= 0)
                hero.Add((L("Fotogrammi persi", "Dropped frames"), dropped.ToString("N0", CultureInfo.CurrentCulture), detail));
            return hero;
        }

        private IReadOnlyList<(string Key, List<string> Parts)> VideoRows()
        {
            var rows = new List<(string, List<string>)>();
            Add(rows, L("Sorgente", "Source"), Parts(_s.VideoIn));
            Add(rows, L("Uscita", "Output"), Without(Parts(_s.VideoOut), @"^schermo\b", @"^display\b"));
            // Primarie e curva; con l'HDR si dice anche come viene trattato.
            Add(rows, L("Colore", "Colour"), Without(Parts(_s.VideoPrimaries, _s.VideoTransfer, _s.HdrMode), @"^livelli\b", @"^levels\b", @"^YCbCr\b"));
            Add(rows, L("Decodifica", "Decoding"), Parts(_s.VideoDecode));
            return rows;
        }

        private IReadOnlyList<(string Key, List<string> Parts)> AudioRows()
        {
            var rows = new List<(string, List<string>)>();
            Add(rows, L("Traccia", "Track"), Parts(_s.AudioIn));
            var output = Parts(_s.AudioOut);
            string kind = _s.Bitstream ? "Bitstream" : "PCM";
            if (output.Count > 0 && !output[0].StartsWith(kind, StringComparison.OrdinalIgnoreCase)) output.Add(kind);
            Add(rows, L("Uscita", "Output"), output);
            // In bitstream la traccia passa intatta: la riga "decodifica" non direbbe nulla.
            if (!_s.Bitstream) Add(rows, L("Decodifica", "Decoding"), Parts(_s.AudioDecode));
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
            Add(rows, L("Dispositivo", "Device"), device);
            Add(rows, L("Sincronia", "Sync"), Parts(_s.AudioSync));
            return rows;
        }

        private IReadOnlyList<(string Key, List<string> Parts)> SystemRows()
        {
            var rows = new List<(string, List<string>)>();
            Add(rows, "Renderer", Parts(_s.Renderer));
            Add(rows, L("Elaborazione", "Processing"), Parts(_s.Upscaling ? "Upscaling" : "", _s.RtxHdr ? "RTX HDR" : "", _s.ProcessingDetail));
            Add(rows, L("Schermo", "Display"), Without(Parts(_s.Display), @"^\d+ bit$"));
            Add(rows, L("Origine", "Origin"), Parts(_s.Origin));
            Add(rows, "Buffer", Parts(_s.Buffer));
            return rows;
        }

        private static void Add(List<(string, List<string>)> rows, string key, List<string> parts)
        {
            if (parts.Count > 0) rows.Add((key, parts));
        }

        private IEnumerable<string> Chips()
        {
            foreach (var chip in new[] { _s.ResolutionTag, _s.DynamicRangeTag, _s.VideoCodec, _s.AudioTag, _s.Bitstream ? "Bitstream" : "" })
                if (HasValue(chip))
                    yield return chip.Trim();
        }

        private List<(string Title, IReadOnlyList<(string Key, List<string> Parts)> Rows)> Sections() => new()
        {
            ("Video", VideoRows()),
            ("Audio", AudioRows()),
            (L("Sistema", "System"), SystemRows())
        };

        private int _columns = 1;

        /// <summary>Una colonna, o due affiancate (video a sinistra, audio e sistema a destra)
        /// quando lo schermo e' basso e in una sola colonna il pannello non ci starebbe.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Columns
        {
            get => _columns;
            set { int next = value >= 2 ? 2 : 1; if (_columns == next) return; _columns = next; if (AutoHeight) AdjustHeightToContent(Width); Invalidate(); }
        }

        private int SectionHeight(IReadOnlyList<(string Key, List<string> Parts)> rows) =>
            rows.Count == 0 ? 0 : SectionGap + SectionHeaderH + rows.Count * RowH + SectionPad;

        public int PreferredHeightFor(int columns)
        {
            var sections = Sections();
            int body = columns >= 2
                ? Math.Max(SectionHeight(sections[0].Rows), SectionHeight(sections[1].Rows) + SectionHeight(sections[2].Rows))
                : sections.Sum(section => SectionHeight(section.Rows));
            return HeaderH + (Chips().Any() ? ChipsH : 0) + (Hero().Count > 0 ? HeroH : 0) + body + D(18);
        }

        private int CalcPreferredHeight() => PreferredHeightFor(_columns);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle card = new(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            if (card.Width <= 0 || card.Height <= 0)
                return;

            using (var path = RoundedRect(card, D(14)))
            using (var bg = new SolidBrush(Color.FromArgb(246, HUD.Theme.Panel)))
                g.FillPath(bg, path);

            int x = card.X + Pad;
            int w = card.Width - Pad * 2;
            int y = card.Y + D(18);

            using var titleFont = UiFont("Segoe UI Semibold", 13.5f);
            using var metaFont = UiFont("Segoe UI", 9f);
            using var chipFont = UiFont("Segoe UI Semibold", 9f);
            using var sectionFont = UiFont("Segoe UI Semibold", 10.5f);
            using var keyFont = UiFont("Segoe UI", 9.5f);
            using var valueFont = UiFont("Segoe UI Semibold", 9.5f);
            using var detailFont = UiFont("Segoe UI", 9.5f);
            using var heroFont = UiFont("Segoe UI Semibold", 16f);
            using var heroLabelFont = UiFont("Segoe UI", 8.75f);

            // ===== Intestazione: titolo e file =====
            const TextFormatFlags oneLine = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int closeSide = D(28);
            _closeRect = new Rectangle(card.Right - D(16) - closeSide, card.Top + D(16), closeSide, closeSide);
            HUD.Theme.DrawCloseButton(g, _closeRect, _closeHot);
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(_s.Title) ? "—" : _s.Title.Trim(), titleFont, new Rectangle(x, y, w - closeSide - D(8), D(28)), TextMain, oneLine);
            // Sotto il titolo solo il nome del file: grandezza e contenitore sono dettagli, stanno a destra.
            string size = string.Join("   ", new[] { _s.FileSize, _s.Container }.Where(HasValue).Select(v => v.Trim()).Where(v => !v.StartsWith("0 ")));
            int sizeWidth = size.Length == 0 ? 0 : TextRenderer.MeasureText(g, size, metaFont, Size.Empty, TextFormatFlags.NoPadding).Width + D(14);
            if (HasValue(_s.FileName))
                TextRenderer.DrawText(g, _s.FileName.Trim(), metaFont, new Rectangle(x, y + D(30), Math.Max(D(40), w - sizeWidth), D(20)), TextMuted, oneLine);
            if (size.Length > 0)
                TextRenderer.DrawText(g, size, metaFont, new Rectangle(x + w - sizeWidth, y + D(30), sizeWidth, D(20)), TextMuted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            y = card.Top + HeaderH;

            // ===== Etichette riassuntive: solo testo nel colore d'accento =====
            if (Chips().Any())
            {
                TextRenderer.DrawText(g, string.Join("     ", Chips()), chipFont, new Rectangle(x, y, w, D(24)),
                    HUD.Theme.IsLight ? ControlPaint.Dark(HUD.Theme.Accent, .1f) : ControlPaint.Light(HUD.Theme.Accent, .55f), oneLine);
                y += ChipsH;
            }

            // ===== I numeri che cambiano: grandi, in colonne uguali separate da barre verticali =====
            var hero = Hero();
            if (hero.Count > 0)
            {
                int column = w / hero.Count;
                using var bar = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 36 : 30, HUD.Theme.Text));
                for (int i = 0; i < hero.Count; i++)
                {
                    int left = x + i * column + (i > 0 ? D(16) : 0), room = column - D(10) - (i > 0 ? D(16) : 0);
                    if (i > 0) g.DrawLine(bar, x + i * column, y + D(8), x + i * column, y + D(66));
                    TextRenderer.DrawText(g, hero[i].Label, heroLabelFont, new Rectangle(left, y + D(4), room, D(18)), TextMuted, oneLine);
                    TextRenderer.DrawText(g, hero[i].Value, heroFont, new Rectangle(left, y + D(22), room, D(30)), TextMain, oneLine);
                    if (hero[i].Detail.Length > 0)
                        TextRenderer.DrawText(g, hero[i].Detail, heroLabelFont, new Rectangle(left, y + D(52), room, D(18)), TextMuted, oneLine);
                }
                y += HeroH;
            }

            var sections = Sections();
            if (_columns >= 2)
            {
                int gap = D(32), columnWidth = (w - gap) / 2, top = y;
                DrawSection(g, sectionFont, keyFont, valueFont, detailFont, sections[0].Title, ref y, x, columnWidth, sections[0].Rows);
                y = top;
                DrawSection(g, sectionFont, keyFont, valueFont, detailFont, sections[1].Title, ref y, x + columnWidth + gap, columnWidth, sections[1].Rows);
                DrawSection(g, sectionFont, keyFont, valueFont, detailFont, sections[2].Title, ref y, x + columnWidth + gap, columnWidth, sections[2].Rows);
                return;
            }
            foreach (var section in sections)
                DrawSection(g, sectionFont, keyFont, valueFont, detailFont, section.Title, ref y, x, w, section.Rows);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hot = _closeRect.Contains(e.Location);
            Cursor = hot ? Cursors.Hand : Cursors.Default;
            if (hot != _closeHot) { _closeHot = hot; Invalidate(Rectangle.Inflate(_closeRect, 2, 2)); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_closeHot) { _closeHot = false; Invalidate(Rectangle.Inflate(_closeRect, 2, 2)); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _closeRect.Contains(e.Location))
            {
                CloseRequested?.Invoke();
                return;
            }
            base.OnMouseUp(e);
        }

        // Nessun riquadro: ogni gruppo ha il suo titolo con un segno d'accento e le righe separate da un
        // filo sottilissimo; il dato principale in evidenza e i dettagli accanto in tono piu' basso.
        private void DrawSection(Graphics g, Font sectionFont, Font keyFont, Font valueFont, Font detailFont, string title,
            ref int y, int x, int width, IReadOnlyList<(string Key, List<string> Parts)> rows)
        {
            // Una sezione compare solo se ci stanno il titolo e almeno una riga intera.
            if (rows.Count == 0 || y + SectionGap + SectionHeaderH + RowH + SectionPad > Height - D(8))
                return;

            const TextFormatFlags line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            y += SectionGap;
            int shown = Math.Min(rows.Count, Math.Max(1, (Height - D(8) - y - SectionHeaderH - SectionPad) / RowH));
            using (var mark = new SolidBrush(HUD.Theme.Accent))
            using (var markPath = RoundedRect(new Rectangle(x, y + D(11), D(3), D(14)), D(2)))
                g.FillPath(mark, markPath);
            TextRenderer.DrawText(g, title, sectionFont, new Rectangle(x + D(11), y, width - D(11), SectionHeaderH), TextMain, line);
            y += SectionHeaderH;

            int labelW = Math.Min(D(112), Math.Max(D(92), width / 3));
            using var rule = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 20 : 15, HUD.Theme.Text));
            for (int index = 0; index < shown; index++)
            {
                var row = rows[index];
                if (index > 0) g.DrawLine(rule, x, y, x + width, y);
                TextRenderer.DrawText(g, row.Key, keyFont, new Rectangle(x, y, labelW, RowH), TextMuted, line);
                int cursor = x + labelW, right = x + width;
                for (int i = 0; i < row.Parts.Count && cursor < right - D(12); i++)
                {
                    Font font = i == 0 ? valueFont : detailFont;
                    int wanted = TextRenderer.MeasureText(g, row.Parts[i], font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                    TextRenderer.DrawText(g, row.Parts[i], font, new Rectangle(cursor, y, Math.Min(wanted + 2, right - cursor), RowH), i == 0 ? TextMain : TextSoft, line);
                    cursor += wanted + D(14);
                }
                y += RowH;
            }
            y += SectionPad;
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
