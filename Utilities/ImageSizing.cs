#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CinecorePlayer2025.Utilities
{
    internal enum ImageSizingMode { Fill, ConstantHeight, ConstantArea, Custom }

    /// <summary>
    /// Dimensionamento dell'immagine a schermo intero. "Riempi" e' il comportamento di sempre: ogni
    /// film occupa tutto lo schermo che puo', quindi un 2.39 risulta piu' basso di un 16:9.
    /// Ad altezza costante (CIH) tutti i formati hanno la stessa altezza e cambia solo la larghezza;
    /// ad area costante (CIA) tutti occupano la stessa superficie. "Personalizzata" sta in mezzo
    /// (Bias) e ha un moltiplicatore di altezza per ciascun formato.
    /// </summary>
    internal sealed class ImageSizingSettings
    {
        /// <summary>Formati di riferimento, dal piu' stretto al piu' largo. 1.43 e' l'IMAX pieno.</summary>
        public static readonly (string Key, double Aspect, double Default)[] Presets =
        {
            ("1.33", 4 / 3.0, 1.00),
            ("1.43", 1.43, 0),      // 0 = tutta l'area disponibile
            ("1.78", 16 / 9.0, 1.00),
            ("1.85", 1.85, 1.04),
            ("2.00", 2.00, 1.06),
            ("2.39", 2.39, 1.10),
        };

        public ImageSizingMode Mode { get; set; } = ImageSizingMode.Fill;
        /// <summary>Solo in Personalizzata: 0 = altezza costante, 1 = area costante.</summary>
        public double Bias { get; set; } = 0;
        /// <summary>Solo in Personalizzata: altezza relativa per formato (0 = tutta l'area).</summary>
        public Dictionary<string, double> Multipliers { get; set; } = new();
        /// <summary>Durata del passaggio quando il formato cambia a meta' film; 0 = cambio secco.</summary>
        public int TransitionMs { get; set; } = 400;
        /// <summary>Cerca le bande nere dentro il fotogramma (film 2.39 codificati in 16:9, scene IMAX).</summary>
        public bool DetectBars { get; set; } = true;
        /// <summary>Le scene che si allargano in altezza rispetto al resto del film (IMAX) usano tutto lo schermo.</summary>
        public bool ExpandTallScenes { get; set; } = true;

        public double Multiplier(string key)
        {
            if (Multipliers.TryGetValue(key, out double value)) return value;
            return Presets.First(preset => preset.Key == key).Default;
        }

        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "image-sizing.json");

        public static ImageSizingSettings Load()
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<ImageSizingSettings>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    loaded.Bias = Math.Clamp(loaded.Bias, 0, 1);
                    loaded.TransitionMs = Math.Clamp(loaded.TransitionMs, 0, 3000);
                    loaded.Multipliers ??= new();
                    return loaded;
                }
            }
            catch { }
            return new ImageSizingSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            }
            catch (Exception ex) { Dbg.Warn("[SIZING] save: " + ex.Message); }
        }
    }

    /// <summary>Calcolo puro: nessuna finestra, nessun renderer. Tutti i rettangoli sono centrati.</summary>
    internal static class ImageSizing
    {
        private const double ReferenceAspect = 16 / 9.0;

        /// <summary>Il rettangolo piu' grande con quelle proporzioni che entra nello schermo.</summary>
        public static RectangleF Fit(SizeF screen, double aspect)
        {
            if (screen.Width <= 0 || screen.Height <= 0 || !(aspect > 0)) return RectangleF.Empty;
            double width = screen.Width, height = width / aspect;
            if (height > screen.Height) { height = screen.Height; width = height * aspect; }
            return Centered(screen, width, height);
        }

        /// <summary>
        /// Rettangolo dell'immagine (la parte attiva, senza bande) per uno schermo, un formato e una
        /// modalita'. Le proporzioni sono sempre quelle della sorgente: se l'altezza voluta non entra
        /// in larghezza l'immagine si riduce, non si deforma.
        /// </summary>
        /// <param name="expanded">La scena e' piu' alta del resto del film (IMAX): tutta l'area, se l'opzione e' attiva.</param>
        public static RectangleF Compute(SizeF screen, double aspect, ImageSizingSettings settings, bool expanded = false)
        {
            RectangleF fit = Fit(screen, aspect);
            if (fit.IsEmpty || settings.Mode == ImageSizingMode.Fill || (expanded && settings.ExpandTallScenes)) return fit;
            double relative = RelativeHeight(aspect, settings);
            if (relative <= 0) return fit;

            double height = Math.Min(relative * BaseHeight(screen, settings), screen.Height);
            double width = height * aspect;
            if (width > screen.Width) { width = screen.Width; height = width / aspect; }
            return Centered(screen, width, height);
        }

        /// <summary>Altezza di un formato rispetto al 16:9 (1 = uguale). 0 = tutta l'area.</summary>
        public static double RelativeHeight(double aspect, ImageSizingSettings settings)
        {
            double area = Math.Sqrt(ReferenceAspect / aspect);
            return settings.Mode switch
            {
                ImageSizingMode.ConstantHeight => 1,
                ImageSizingMode.ConstantArea => area,
                ImageSizingMode.Custom => ((1 - settings.Bias) + settings.Bias * area) * Multiplier(aspect, settings),
                _ => 0
            };
        }

        /// <summary>
        /// Altezza del 16:9: la piu' grande per cui tutti i formati di riferimento entrano nello
        /// schermo con la loro altezza relativa. Su uno schermo 16:9 la decide il 2.39 (che arriva
        /// a tutta larghezza), su uno schermo 21:9 l'altezza dello schermo.
        /// </summary>
        public static double BaseHeight(SizeF screen, ImageSizingSettings settings)
        {
            double best = double.MaxValue;
            foreach (var preset in ImageSizingSettings.Presets)
            {
                double relative = RelativeHeight(preset.Aspect, settings);
                if (relative <= 0) continue;
                best = Math.Min(best, Math.Min(screen.Height / relative, screen.Width / (preset.Aspect * relative)));
            }
            return best == double.MaxValue ? screen.Height : best;
        }

        // Moltiplicatore per un formato qualsiasi: quello del riferimento piu' vicino se e' entro il 3%,
        // altrimenti interpolato tra i due vicini (cosi' un 1.66 o un 2.20 non fanno un salto).
        private static double Multiplier(double aspect, ImageSizingSettings settings)
        {
            var presets = ImageSizingSettings.Presets;
            foreach (var preset in presets)
                if (Math.Abs(aspect / preset.Aspect - 1) <= 0.03) return settings.Multiplier(preset.Key);

            var sized = presets.Where(preset => settings.Multiplier(preset.Key) > 0).ToArray();
            if (sized.Length == 0) return 0;
            if (aspect <= sized[0].Aspect) return settings.Multiplier(sized[0].Key);
            if (aspect >= sized[^1].Aspect) return settings.Multiplier(sized[^1].Key);
            for (int i = 1; i < sized.Length; i++)
            {
                if (aspect > sized[i].Aspect) continue;
                double t = Math.Log(aspect / sized[i - 1].Aspect) / Math.Log(sized[i].Aspect / sized[i - 1].Aspect);
                return settings.Multiplier(sized[i - 1].Key) * (1 - t) + settings.Multiplier(sized[i].Key) * t;
            }
            return 1;
        }

        /// <summary>
        /// Di quanto ridurre (o ingrandire) il fotogramma intero rispetto a "Riempi" perche' la sua
        /// parte attiva occupi <paramref name="active"/>. Con bande codificate nel file il fotogramma
        /// e' piu' grande dell'immagine che contiene.
        /// </summary>
        public static double FrameScale(SizeF screen, double frameAspect, double activeAspect, RectangleF active)
        {
            RectangleF fit = Fit(screen, frameAspect);
            if (fit.IsEmpty || active.IsEmpty) return 1;
            double frameHeight = activeAspect >= frameAspect ? active.Width / frameAspect : active.Height;
            return frameHeight / fit.Height;
        }

        private static RectangleF Centered(SizeF screen, double width, double height) =>
            new((float)((screen.Width - width) / 2), (float)((screen.Height - height) / 2), (float)width, (float)height);

        // ---- formato attivo ----

        private static readonly double[] KnownAspects = { 1.333, 1.375, 1.43, 1.66, 1.778, 1.85, 1.90, 2.00, 2.20, 2.35, 2.39, 2.55, 2.76 };

        /// <summary>Avvicina una misura al formato noto piu' vicino, se e' entro il 2%.</summary>
        public static double Snap(double aspect)
        {
            double nearest = KnownAspects.OrderBy(known => Math.Abs(known - aspect)).First();
            // 2.35 e 2.39/2.40 sono lo stesso formato misurato in modi diversi.
            if (nearest is 2.35) nearest = 2.39;
            return Math.Abs(aspect / nearest - 1) <= 0.02 ? nearest : aspect;
        }

        /// <summary>
        /// Formato della parte non nera di un fotogramma. null quando non si puo' dire: scena buia,
        /// bande asimmetriche (titoli, sottotitoli impressi), immagine quasi tutta nera.
        /// </summary>
        public static double? DetectActiveAspect(Bitmap frame, double frameAspect)
        {
            int width = frame.Width, height = frame.Height;
            if (width < 32 || height < 32 || !(frameAspect > 0)) return null;
            var rows = new bool[height];
            var columns = new int[width];
            var data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* line = (byte*)data.Scan0 + (long)y * data.Stride;
                        int bright = 0;
                        for (int x = 0; x < width; x++)
                        {
                            byte* pixel = line + x * 4;
                            int luma = (pixel[2] * 54 + pixel[1] * 183 + pixel[0] * 19) >> 8;
                            if (luma > 24) { bright++; columns[x]++; }
                        }
                        // Qualche pixel acceso (rumore di compressione) non fa una riga d'immagine.
                        rows[y] = bright > width / 50;
                    }
                }
            }
            finally { frame.UnlockBits(data); }

            int top = 0, bottom = 0, left = 0, right = 0;
            while (top < height && !rows[top]) top++;
            if (top >= height) return null;
            while (!rows[height - 1 - bottom]) bottom++;
            while (left < width && columns[left] <= height / 50) left++;
            if (left >= width) return null;
            while (columns[width - 1 - right] <= height / 50) right++;

            int activeHeight = height - top - bottom, activeWidth = width - left - right;
            if (activeHeight < height * 0.35 || activeWidth < width * 0.45) return null;
            if (Math.Abs(top - bottom) > Math.Max(2, height * 0.025) || Math.Abs(left - right) > Math.Max(2, width * 0.025)) return null;
            // Meno dell'1,5% per lato e' il bordo irregolare di una codifica, non una banda.
            if (top + bottom < height * 0.03) activeHeight = height;
            if (left + right < width * 0.03) activeWidth = width;
            return Snap(frameAspect * (activeWidth / (double)width) / (activeHeight / (double)height));
        }
    }

    /// <summary>
    /// Il rilevamento oscilla (scene buie, dissolvenze): un formato nuovo vale solo dopo due misure
    /// consecutive concordi e diverse da quello in uso di almeno il 3%. Tiene anche il conto del
    /// formato in cui il film passa piu' tempo, per riconoscere le scene che si allargano in altezza
    /// (IMAX) senza scambiare per tali un intero film 16:9 che si apre con un logo in 2.39.
    /// </summary>
    internal sealed class AspectStabilizer
    {
        public const int SamplesToKnowTheFilm = 30;
        private readonly Dictionary<int, int> _time = new();
        private double _candidate;
        private int _agreeing;

        public double Current { get; private set; }
        /// <summary>Formato prevalente finora.</summary>
        public double Usual { get; private set; }
        /// <summary>La scena in corso e' piu' alta del formato prevalente del film.</summary>
        public bool Expanded => _time.TryGetValue(Key(Usual), out int samples) && samples >= SamplesToKnowTheFilm && Current < Usual * 0.93;

        public AspectStabilizer(double initial) { Current = Usual = initial; }

        private static int Key(double aspect) => (int)Math.Round(aspect * 100);

        /// <param name="sample">Misura di questo istante; null se non si e' potuto misurare.</param>
        /// <returns>true quando il formato in uso e' cambiato.</returns>
        public bool Offer(double? sample)
        {
            bool changed = false;
            if (sample is double aspect && aspect > 0)
            {
                if (Math.Abs(aspect / Current - 1) < 0.03) _agreeing = 0;
                else
                {
                    if (_agreeing > 0 && Math.Abs(aspect / _candidate - 1) < 0.02) _agreeing++;
                    else { _candidate = aspect; _agreeing = 1; }
                    if (_agreeing >= 2) { Current = _candidate; _agreeing = 0; changed = true; }
                }
            }
            int key = Key(Current);
            _time[key] = _time.GetValueOrDefault(key) + 1;
            if (_time[key] > _time.GetValueOrDefault(Key(Usual))) Usual = Current;
            return changed;
        }
    }
}
