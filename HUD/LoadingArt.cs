#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CinecorePlayer2025.HUD
{
    /// <summary>
    /// Schermata di caricamento con immagine: lo sfondo del film (o la foto dell'artista), il titolo in basso a
    /// sinistra come nella schermata prima del film, il messaggio e una barra di avanzamento. La disegnano sia la
    /// maschera del player sia la finestra animata su un altro thread: ciascuna tiene la propria copia dell'immagine,
    /// perche' un Bitmap di GDI+ non si puo' usare da due thread insieme.
    /// </summary>
    internal static class LoadingArt
    {
        /// <returns>Falso se non c'e' un titolo da mostrare: chi chiama disegna il caricamento semplice.</returns>
        public static string KeyFor(string? imagePath, Size size) => (imagePath ?? "") + "|" + size.Width + "x" + size.Height;

        /// <summary>L'immagine pronta per una superficie di questa misura (da chiamare fuori dal thread dell'interfaccia).</summary>
        public static Bitmap? Prepare(string? imagePath, Size size) => Build(imagePath, size);

        /// <param name="buildIfMissing">Falso per chi prepara l'immagine altrove: finche' non arriva si disegna sul nero.</param>
        public static bool Paint(Graphics g, Size size, ref Bitmap? cache, ref string cacheKey, string? imagePath, string title, string message, double progress, bool buildIfMissing = true)
        {
            if (string.IsNullOrWhiteSpace(title) || size.Width < 2 || size.Height < 2) return false;
            string key = KeyFor(imagePath, size);
            if (key != cacheKey && !buildIfMissing)
            {
                g.Clear(Color.Black);
                cache = null;
            }
            else if (key != cacheKey)
            {
                try { cache?.Dispose(); } catch { }
                cache = Build(imagePath, size);
                cacheKey = key;
            }
            if (cache != null && key == cacheKey) g.DrawImageUnscaled(cache, 0, 0);
            else g.Clear(Color.Black);

            int w = size.Width, h = size.Height;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            float margin = (float)Math.Round(Math.Max(40f, w * 0.055f));
            float maxText = Math.Max(240f, w * 0.5f);
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", Math.Max(24f, h * 0.066f), FontStyle.Regular, GraphicsUnit.Pixel);
            using var subFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", Math.Max(13f, h * 0.0205f), FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };
            string shown = string.Join(" ", title.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            float titleHeight = Math.Min(titleFont.GetHeight(g) * 2.05f, g.MeasureString(shown, titleFont, new SizeF(maxText, h), format).Height);
            float subHeight = subFont.GetHeight(g);
            float barHeight = Math.Max(3f, (float)Math.Round(h * 0.0042f));
            // A tutta larghezza, da margine a margine.
            float barWidth = Math.Max(120f, w - 2f * margin - (float)Math.Round(h * 0.003f));
            float bottom = h - (float)Math.Round(Math.Max(44f, h * 0.105f));
            float barY = bottom - barHeight;
            float subY = barY - (float)Math.Round(h * 0.022f) - subHeight;
            float titleY = subY - (float)Math.Round(h * 0.014f) - titleHeight;

            using (var ink = new SolidBrush(Color.FromArgb(248, 249, 250)))
                g.DrawString(shown, titleFont, ink, new RectangleF(margin, titleY, maxText, titleHeight + 1f), format);
            using (var soft = new SolidBrush(Color.FromArgb(176, 184, 194)))
                g.DrawString(message ?? "", subFont, soft, new RectangleF(margin + (float)Math.Round(h * 0.003f), subY, maxText, subHeight + 2f), format);

            float x = margin + (float)Math.Round(h * 0.003f);
            using (var track = new SolidBrush(Color.FromArgb(52, 255, 255, 255)))
                FillPill(g, track, x, barY, barWidth, barHeight);
            float filled = barWidth * (float)Math.Clamp(progress, 0.02, 1);
            using (var fill = new SolidBrush(Theme.Accent))
                FillPill(g, fill, x, barY, filled, barHeight);
            return true;
        }

        private static void FillPill(Graphics g, Brush brush, float x, float y, float width, float height)
        {
            if (width <= 0) return;
            if (width <= height) { g.FillEllipse(brush, x, y, height, height); return; }
            using var path = new GraphicsPath();
            path.AddArc(x, y, height, height, 90, 180);
            path.AddArc(x + width - height, y, height, height, 270, 180);
            path.CloseFigure();
            g.FillPath(brush, path);
        }

        /// <summary>L'immagine a tutto schermo, gia' ritagliata e velata: a ogni fotogramma basta copiarla.</summary>
        private static Bitmap? Build(string? imagePath, Size size)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;
                using var source = Image.FromFile(imagePath);
                var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(result);
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                double scale = Math.Max(size.Width / (double)source.Width, size.Height / (double)source.Height);
                int dw = (int)Math.Ceiling(source.Width * scale), dh = (int)Math.Ceiling(source.Height * scale);
                // Un quarto dall'alto: nelle foto e negli sfondi i volti stanno piu' su del centro.
                g.DrawImage(source, new Rectangle((size.Width - dw) / 2, (int)((size.Height - dh) * 0.28), dw, dh));
                var all = new Rectangle(0, 0, size.Width, size.Height);
                using (var side = new LinearGradientBrush(new Rectangle(-1, 0, (int)(size.Width * 0.78) + 2, size.Height), Color.FromArgb(222, 4, 6, 9), Color.FromArgb(0, 4, 6, 9), LinearGradientMode.Horizontal))
                    g.FillRectangle(side, new Rectangle(0, 0, (int)(size.Width * 0.78), size.Height));
                int low = (int)(size.Height * 0.58);
                using (var foot = new LinearGradientBrush(new Rectangle(0, size.Height - low - 1, size.Width, low + 2), Color.FromArgb(0, 4, 6, 9), Color.FromArgb(214, 4, 6, 9), LinearGradientMode.Vertical))
                    g.FillRectangle(foot, new Rectangle(0, size.Height - low, size.Width, low));
                using (var veil = new SolidBrush(Color.FromArgb(40, 4, 6, 9)))
                    g.FillRectangle(veil, all);
                return result;
            }
            catch { return null; }
        }
    }
}
