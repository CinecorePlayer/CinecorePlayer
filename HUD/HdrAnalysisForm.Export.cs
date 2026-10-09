#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Esportazione dell'analisi HDR:
    // - rapporto HTML: un solo file con tutti i numeri, i grafici (le due viste della scheda,
    //   incorporate come immagini) e la tabella dei campioni;
    // - immagine PNG della vista aperta;
    // - CSV dei campioni (tempo, picco, media) per un foglio di calcolo;
    // - JSON con tutte le misure, istogramma compreso.
    internal sealed partial class HdrAnalysisForm
    {
        private bool _exportHover, _exporting;
        private string? _exportNote;
        private long _exportNoteTick;

        private Rectangle ExportBounds => new((TabsVisible ? TabsBounds.Left : CloseBounds.Left - 8) - 12 - 104, 20, 104, 30);
        private bool CanExport => _result is { IsHdr: true, Error: null, BaseLayerNotMeasurable: false } result && (result.Samples.Count > 0 || _liveStats != null);

        private void DrawExportButton(Graphics g, Font font)
        {
            if (!CanExport) return;
            Rectangle box = ExportBounds;
            using (var shape = Rounded(box, 8))
            using (var fill = new SolidBrush(_exportHover ? Theme.Nav : Theme.SheetRaised))
                g.FillPath(fill, shape);
            TextRenderer.DrawText(g, T("Esporta…", "Export…"), font, box, _exportHover ? Theme.Text : Theme.SubtleText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawExportNote(Graphics g, Font font)
        {
            if (_exportNote == null || Environment.TickCount64 - _exportNoteTick > 8000) return;
            TextRenderer.DrawText(g, _exportNote, font, new Rectangle(30, ClientSize.Height - 26, ClientSize.Width / 2 - 40, 18), Theme.SubtleText, Line | TextFormatFlags.PathEllipsis);
        }

        /// <summary>La scheda cosi' com'e', senza pulsanti ne' etichette al passaggio del mouse.</summary>
        private Bitmap CaptureView(bool live)
        {
            bool view = _liveView;
            Point mouse = _mouse;
            _exporting = true;
            _liveView = live;
            _mouse = new Point(-1, -1);
            try
            {
                if (live) _live?.Read(RenderScopes);
                var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
                DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                return bitmap;
            }
            finally
            {
                _exporting = false;
                _liveView = view;
                _mouse = mouse;
                Invalidate();
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
            name = name.Trim();
            return name.Length == 0 ? "HDR" : name.Length > 80 ? name[..80].Trim() : name;
        }

        private void ExportResults()
        {
            var result = _result;
            if (result == null || !CanExport) return;
            using var dialog = new SaveFileDialog
            {
                Title = T("Esporta l'analisi HDR", "Export the HDR analysis"),
                FileName = SafeFileName(_title) + " - " + T("analisi HDR", "HDR analysis"),
                Filter = T("Rapporto con grafici (*.html)|*.html|Immagine della vista (*.png)|*.png|Campioni (*.csv)|*.csv|Tutte le misure (*.json)|*.json",
                    "Report with charts (*.html)|*.html|Picture of this view (*.png)|*.png|Samples (*.csv)|*.csv|All measurements (*.json)|*.json"),
                DefaultExt = "html",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string path = dialog.FileName;
            try
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".png":
                        using (var picture = CaptureView(_liveView)) picture.Save(path, ImageFormat.Png);
                        break;
                    case ".csv":
                        File.WriteAllText(path, BuildCsv(result), new UTF8Encoding(true));
                        break;
                    case ".json":
                        File.WriteAllText(path, BuildJson(result), new UTF8Encoding(false));
                        break;
                    default:
                        File.WriteAllText(path, BuildHtml(result), new UTF8Encoding(false));
                        break;
                }
                _exportNote = T("Salvato in ", "Saved to ") + path;
            }
            catch (Exception ex)
            {
                Dbg.Warn("HDR export failed: " + ex.Message);
                _exportNote = T("Non sono riuscito a salvare il file: ", "The file could not be saved: ") + ex.Message;
            }
            _exportNoteTick = Environment.TickCount64;
            Invalidate();
        }

        private static string Number(double value, string format = "0.####") => value.ToString(format, CultureInfo.InvariantCulture);

        private static string BuildCsv(HdrAnalyzer.Result result)
        {
            var text = new StringBuilder("time_s,time,peak_nits,average_nits\r\n");
            foreach (var sample in result.Samples)
                text.Append(Number(sample.Time, "0.###")).Append(',').Append(Clock(sample.Time)).Append(',')
                    .Append(Number(sample.PeakNits, "0.##")).Append(',').Append(Number(sample.AverageNits, "0.###")).Append("\r\n");
            return text.ToString();
        }

        private string BuildJson(HdrAnalyzer.Result result)
        {
            var export = new Dictionary<string, object?>
            {
                ["title"] = _title,
                ["file"] = Path.GetFileName(_path),
                ["exported"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                ["analysisComplete"] = !_running,
                ["film"] = result,
                ["currentFrame"] = _liveStats
            };
            return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        }

        private static string Embedded(Bitmap bitmap)
        {
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
        }

        private string BuildHtml(HdrAnalyzer.Result result)
        {
            static string E(string text) => WebUtility.HtmlEncode(text);
            string none = "—";
            var html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"").Append(_english ? "en" : "it").Append("\"><head><meta charset=\"utf-8\"><title>")
                .Append(E(T("Analisi HDR", "HDR analysis") + " – " + _title)).Append("</title><style>")
                .Append("body{font-family:Segoe UI,system-ui,sans-serif;background:#0b1118;color:#e8ecf0;margin:32px auto;max-width:1220px;padding:0 20px}")
                .Append("h1{font-size:24px;margin:0 0 4px}h2{font-size:15px;margin:34px 0 10px}p.sub{color:#93a0ae;margin:0 0 6px}")
                .Append("table{border-collapse:collapse;font-size:13px}td,th{padding:5px 22px 5px 0;text-align:left;border-bottom:1px solid #1b2530}th{color:#93a0ae;font-weight:600}")
                .Append("td.n{text-align:right;font-variant-numeric:tabular-nums}img{max-width:100%;border-radius:12px;display:block}")
                .Append("details{margin-top:8px}summary{cursor:pointer;color:#93a0ae}.grid{display:grid;grid-template-columns:1fr 1fr;gap:0 48px}</style></head><body>");
            html.Append("<h1>").Append(E(T("Analisi HDR", "HDR analysis"))).Append("</h1><p class=\"sub\">").Append(E(_title)).Append("</p><p class=\"sub\">")
                .Append(E(Path.GetFileName(_path))).Append(" · ").Append(E(FormatLine(result))).Append("</p><p class=\"sub\">")
                .Append(E(DateTime.Now.ToString("g", CultureInfo.CurrentCulture)))
                .Append(_running ? E(" · " + string.Format(T("analisi ancora in corso ({0}%)", "analysis still running ({0}%)"), (int)Math.Round(_progress * 100))) : "").Append("</p>");

            void Table(string heading, IEnumerable<(string Name, string Value)> rows)
            {
                html.Append("<div><h2>").Append(E(heading)).Append("</h2><table>");
                foreach (var (name, value) in rows) html.Append("<tr><th>").Append(E(name)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
                html.Append("</table></div>");
            }

            html.Append("<div class=\"grid\">");
            Table(T("Dichiarato dal file", "Declared by the file"), new[]
            {
                ("MaxCLL", result.DeclaredMaxCll is int cll ? Nits(cll) + " nit" : none),
                ("MaxFALL", result.DeclaredMaxFall is int fall ? Nits(fall) + " nit" : none),
                (T("Schermo di mastering, massimo", "Mastering display, maximum"), result.MasteringMaxNits is double max ? Nits(max) + " nit" : none),
                (T("Schermo di mastering, nero", "Mastering display, black"), result.MasteringMinNits is double min ? min.ToString("0.####", CultureInfo.CurrentCulture) + " nit" : none),
                ("Dolby Vision", result.DolbyVisionProfile is int profile ? T("profilo ", "profile ") + profile + (result.DolbyVisionCompatibility is int compat and > 0 ? "." + compat : "") : none),
                ("HDR10+", result.Hdr10Plus ? T("sì", "yes") : T("no", "no")),
                (T("Immagine", "Picture"), result.Width + "×" + result.Height + (result.BitDepth > 0 ? ", " + result.BitDepth + " bit" : "") + (result.Primaries.Length > 0 ? ", " + result.Primaries : "")),
                (T("Durata", "Duration"), Clock(result.Duration))
            });
            if (result.Samples.Count > 0)
                Table(string.Format(T("Misurato sul film ({0} fotogrammi)", "Measured on the film ({0} frames)"), result.Samples.Count), new[]
                {
                    (T("Picco (99,99° percentile)", "Peak (99.99th percentile)"), Nits(result.MeasuredPeakNits) + " nit, " + T("a ", "at ") + Clock(result.MeasuredPeakTime)),
                    (T("Massimo assoluto", "Absolute maximum"), Nits(result.MeasuredMaxNits) + " nit, " + T("a ", "at ") + Clock(result.MeasuredMaxTime)),
                    (T("Picco tipico di un fotogramma", "Typical frame peak"), Nits(result.MedianPeakNits) + " nit"),
                    (T("Media più alta di un fotogramma", "Highest frame average"), Nits(result.MeasuredMaxAverageNits) + " nit"),
                    (T("Media dell'intero film", "Whole-film average"), Nits(result.OverallAverageNits) + " nit"),
                    (T("Luminanza media (Y)", "Mean luminance (Y)"), Nits(result.LuminanceAverageNits) + " nit"),
                    (T("Mediana / 90° / 99° percentile", "Median / 90th / 99th percentile"), Nits(result.MedianNits) + " / " + Nits(result.P90Nits) + " / " + Nits(result.P99Nits) + " nit"),
                    (T("Nero (0,1° percentile)", "Black (0.1st percentile)"), Black(result.BlackNits) + " nit"),
                    (T("Gamma dinamica", "Dynamic range"), Math.Log2(Math.Max(1e-6, result.MeasuredPeakNits) / Math.Max(BlackFloor, result.BlackNits)).ToString("0.0", CultureInfo.CurrentCulture) + " stop"),
                    (T("Sopra 100 / 400 / 1000 nit", "Above 100 / 400 / 1000 nit"), Share(result.ShareAbove100) + " / " + Share(result.ShareAbove400) + " / " + Share(result.ShareAbove1000)),
                    (T("Colori: Rec.709 / P3 / BT.2020", "Colours: Rec.709 / P3 / BT.2020"), result.GamutMeasured ? Share(result.GamutRec709) + " / " + Share(result.GamutP3) + " / " + Share(result.GamutRec2020) : none),
                    (T("Area attiva", "Active area"), result.ActiveWidth > 0 ? result.ActiveWidth + "×" + result.ActiveHeight + " (" + (result.ActiveWidth / (double)Math.Max(1, result.ActiveHeight)).ToString("0.00", CultureInfo.CurrentCulture) + ":1)" : none)
                });
            html.Append("</div>");

            if (result.Samples.Count > 0)
            {
                using var film = CaptureView(false);
                html.Append("<h2>").Append(E(T("Film intero", "Whole film"))).Append("</h2><img alt=\"\" src=\"").Append(Embedded(film)).Append("\">");
            }
            if (_liveStats is { } live)
            {
                using var scopes = CaptureView(true);
                html.Append("<h2>").Append(E(T("Fotogramma a ", "Frame at ") + Clock(live.Time))).Append("</h2><img alt=\"\" src=\"").Append(Embedded(scopes)).Append("\">");
            }

            if (result.Samples.Count > 0)
            {
                html.Append("<h2>").Append(E(T("Campioni", "Samples"))).Append("</h2><details><summary>")
                    .Append(E(string.Format(T("{0} fotogrammi: tempo, picco e media", "{0} frames: time, peak and average"), result.Samples.Count)))
                    .Append("</summary><table><tr><th>").Append(E(T("Tempo", "Time"))).Append("</th><th>").Append(E(T("Picco (nit)", "Peak (nit)"))).Append("</th><th>")
                    .Append(E(T("Media (nit)", "Average (nit)"))).Append("</th></tr>");
                foreach (var sample in result.Samples)
                    html.Append("<tr><td>").Append(Clock(sample.Time)).Append("</td><td class=\"n\">").Append(E(Nits(sample.PeakNits))).Append("</td><td class=\"n\">")
                        .Append(E(Nits(sample.AverageNits))).Append("</td></tr>");
                html.Append("</table></details>");

                html.Append("<h2>").Append(E(T("Distribuzione della luminosità", "Luminance distribution"))).Append("</h2><details><summary>")
                    .Append(E(T("Quanta parte dell'immagine cade in ogni fascia", "Share of the picture in each band"))).Append("</summary><table><tr><th>nit</th><th>%</th></tr>");
                for (int i = 0; i < result.Histogram.Length; i++)
                {
                    if (result.Histogram[i] <= 0) continue;
                    double from = HdrAnalyzer.PqToNits(i / (double)result.Histogram.Length), to = HdrAnalyzer.PqToNits((i + 1) / (double)result.Histogram.Length);
                    html.Append("<tr><td>").Append(E(Nits(from) + " – " + Nits(to))).Append("</td><td class=\"n\">")
                        .Append((result.Histogram[i] * 100).ToString("0.0000", CultureInfo.CurrentCulture)).Append("</td></tr>");
                }
                html.Append("</table></details>");
            }
            html.Append("</body></html>");
            return html.ToString();
        }
    }
}
