#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Sfondi di TMDb "4K" che in realta' sono foto piccole ingrandite (es. Donnie Darko: 3840 px ma si contano i pixel).
    /// La risoluzione dichiarata non basta: si misura quanto dettaglio c'e' a piena risoluzione rispetto a meta'
    /// (in un'immagine ingrandita i bordi a piena risoluzione sono morbidi, il rapporto scende verso 0.5).
    /// Non si usa come soglia assoluta (i film d'animazione sono lisci di natura): si confrontano gli sfondi dello
    /// stesso titolo e si scarta quello impastato solo se ce n'e' uno nettamente piu' nitido.
    /// </summary>
    internal static partial class MovieMetadataService
    {
        private const double SharpEnough = 0.72;
        private const double SharperBy = 0.12;
        private const int SharpnessCandidates = 4;

        /// <summary>Rapporto fra dettaglio a 1920 px e a 960 px (circa 0.8 una foto vera, 0.55 una ingrandita).</summary>
        internal static double BackdropSharpness(string file)
        {
            try
            {
                double full = DetailAt(file, 1920), half = DetailAt(file, 960);
                return half < 0.05 ? 1 : full / half;
            }
            catch { return 1; }
        }

        private static double DetailAt(string file, int width)
        {
            using var fs = File.OpenRead(file);
            using var bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, width, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height, stride = w * 4;
            var buf = new byte[stride * h];
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { bmp.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), buf.Length, stride); }
            finally { pin.Free(); }
            var y = new float[w * h];
            for (int i = 0, p = 0; i < y.Length; i++, p += 4) y[i] = buf[p] * 0.114f + buf[p + 1] * 0.587f + buf[p + 2] * 0.299f;
            double dx = 0, dy = 0;
            for (int r = 0; r < h; r++)
            {
                int row = r * w;
                for (int c = 1; c < w; c++) dx += Math.Abs(y[row + c] - y[row + c - 1]);
                if (r > 0) for (int c = 0; c < w; c++) dy += Math.Abs(y[row + c] - y[row - w + c]);
            }
            return (dx / Math.Max(1, (w - 1) * h) + dy / Math.Max(1, w * (h - 1))) / 2;
        }

        /// <summary>
        /// Scarica i migliori sfondi in ordine di punteggio finche' ne trova uno nitido; tiene il primo a meno che un
        /// altro sia nettamente piu' nitido. Gli altri file scaricati si cancellano.
        /// </summary>
        private static bool DownloadSharpestBackdrop(IEnumerable<(double Score, string Path)> candidates, string hashPrefix, CancellationToken ct, out string? localBackdropPath)
        {
            localBackdropPath = null;
            string folder = GetBackdropFolder();
            string? chosen = null;
            double firstSharp = -1, chosenSharp = -1;
            var downloaded = new List<string>();
            foreach (var candidate in candidates.OrderByDescending(c => c.Score).Take(SharpnessCandidates))
            {
                ct.ThrowIfCancellationRequested();
                string fullPath = Path.Combine(folder, ComputeSha1(hashPrefix + candidate.Path) + ".jpg");
                try
                {
                    byte[] bytes = GetTmdbImageBytesForPath(candidate.Path, ct, "original", "w1280");
                    File.WriteAllBytes(fullPath, bytes);
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                downloaded.Add(fullPath);
                if (_backdropFloor.HasValue ? !IsBackdropAcceptable(fullPath) : !IsBackdropFullResolution(fullPath)) continue;
                double sharp = BackdropSharpness(fullPath);
                if (chosen == null) { chosen = fullPath; firstSharp = chosenSharp = sharp; }
                else if (sharp >= firstSharp + SharperBy && sharp > chosenSharp) { chosen = fullPath; chosenSharp = sharp; }
                if (chosenSharp >= SharpEnough) break;
            }
            foreach (string file in downloaded)
                if (!string.Equals(file, chosen, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(file); } catch { }
            if (chosen == null) return false;
            MarkBackdropChecked(chosen);
            localBackdropPath = chosen;
            return true;
        }

        // ------------------------------------------------------------- sfondi gia' in cache scelti col metodo vecchio
        private static readonly object CheckedLock = new();
        private static HashSet<string>? _checkedBackdrops;
        private static readonly HashSet<string> Upgrading = new(StringComparer.OrdinalIgnoreCase);

        private static string CheckedFile => Path.Combine(GetBackdropFolder(), "sharpness-checked.txt");

        private static HashSet<string> CheckedBackdrops()
        {
            if (_checkedBackdrops != null) return _checkedBackdrops;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(CheckedFile)) foreach (string line in File.ReadAllLines(CheckedFile)) if (line.Length > 0) set.Add(line.Trim()); } catch { }
            return _checkedBackdrops = set;
        }

        private static void MarkBackdropChecked(string file)
        {
            string name = Path.GetFileName(file);
            lock (CheckedLock)
            {
                if (!CheckedBackdrops().Add(name)) return;
                try { File.AppendAllText(CheckedFile, name + Environment.NewLine); } catch { }
            }
        }

        /// <summary>Vero se lo sfondo in cache va ancora controllato (una volta sola per file).</summary>
        public static bool BackdropNeedsSharpnessCheck(string? file)
        {
            if (string.IsNullOrWhiteSpace(file) || !HasCustomTmdbApiKey) return false;
            if (!string.Equals(Path.GetDirectoryName(file)?.TrimEnd('\\', '/'), GetBackdropFolder().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
            lock (CheckedLock) return !CheckedBackdrops().Contains(Path.GetFileName(file)) && !Upgrading.Contains(file);
        }

        /// <summary>
        /// Controlla uno sfondo scaricato in passato: se e' impastato lo si mette da parte e si riscarica col metodo
        /// nuovo (che confronta piu' sfondi). Restituisce il file da usare (lo stesso se andava bene o se non c'e' di meglio).
        /// </summary>
        public static string? UpgradeBlurryBackdrop(string mediaPath, string file, CancellationToken ct)
        {
            lock (CheckedLock) { if (!Upgrading.Add(file)) return file; }
            try
            {
                if (!File.Exists(file)) return file;
                double sharp = BackdropSharpness(file);
                if (sharp >= SharpEnough) { MarkBackdropChecked(file); return file; }
                string aside = file + ".blurry";
                try { File.Move(file, aside, true); } catch { MarkBackdropChecked(file); return file; }
                string? fresh = null;
                try { fresh = ResolveTitleAndBackdrop(mediaPath, null, ct).localBackdropPath; } catch { }
                if (string.IsNullOrWhiteSpace(fresh) || !File.Exists(fresh))
                {
                    // Niente di meglio (o TMDb non risponde): si rimette quello di prima.
                    try { if (!File.Exists(file)) File.Move(aside, file); } catch { }
                    MarkBackdropChecked(file);
                    return file;
                }
                try { File.Delete(aside); } catch { }
                MarkBackdropChecked(fresh);
                return fresh;
            }
            finally { lock (CheckedLock) Upgrading.Remove(file); }
        }
    }
}
