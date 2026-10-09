#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025
{
    /// <summary>
    /// App typeface. The UI was written against "Segoe UI" / "Segoe UI Semibold"; those
    /// requests are served with the bundled Manrope (Assets/Fonts, SIL OFL), registered
    /// privately for GDI+ (DrawString, measuring) and GDI (TextRenderer). Other families
    /// (symbol and icon fonts) pass through unchanged, and if the files are missing the
    /// original font is used.
    /// </summary>
    internal static class AppFonts
    {
        // Manrope runs wider than Segoe UI: this keeps existing layouts from truncating.
        private const float SizeFactor = 0.94f;

        private static readonly object Sync = new();
        private static PrivateFontCollection? _collection;
        private static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Segoe UI"] = "Manrope",
            ["Segoe UI Variable"] = "Manrope",
            ["Segoe UI Variable Text"] = "Manrope",
            ["Segoe UI Variable Display"] = "Manrope",
            ["Segoe UI Semibold"] = "Manrope SemiBold",
            ["Segoe UI Variable Display Semib"] = "Manrope SemiBold",
            ["Segoe UI Light"] = "Manrope Light",
            ["Segoe UI Semilight"] = "Manrope Light",
            ["Segoe UI Black"] = "Manrope ExtraBold",
            ["Segoe UI Bold"] = "Manrope",
        };

        public static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
                    if (!Directory.Exists(dir)) return;
                    var collection = new PrivateFontCollection();
                    foreach (string file in Directory.GetFiles(dir, "Manrope-*.ttf"))
                    {
                        collection.AddFontFile(file);
                        AddFontResourceEx(file, FR_PRIVATE, IntPtr.Zero); // GDI / TextRenderer
                    }
                    foreach (var family in collection.Families)
                        Families[family.Name] = family;
                    _collection = collection;
                }
                catch { Families.Clear(); }
            }
        }

        public static Font Create(string family, float size) => Create(family, size, FontStyle.Regular, GraphicsUnit.Point);
        public static Font Create(string family, float size, FontStyle style) => Create(family, size, style, GraphicsUnit.Point);
        public static Font Create(string family, float size, GraphicsUnit unit) => Create(family, size, FontStyle.Regular, unit);

        // Windows 11: Segoe UI Variable, pensato per il rendering di Windows e molto piu' nitido
        // di Manrope in GDI (Manrope a piccole dimensioni usciva sottile e frastagliato).
        // Fattore di dimensione tarato per mantenere le larghezze degli impaginati esistenti.
        private const float SystemSizeFactor = 0.95f;
        private static bool? _systemVariable;
        private static bool SystemVariableAvailable
        {
            get
            {
                if (_systemVariable is bool known) return known;
                bool found = false;
                try
                {
                    using var installed = new InstalledFontCollection();
                    foreach (var f in installed.Families)
                        if (string.Equals(f.Name, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                }
                catch { }
                _systemVariable = found;
                return found;
            }
        }

        private static string? SystemVariableFamily(string family, float size, ref FontStyle style)
        {
            if (!SystemVariableAvailable) return null;
            bool display = size >= 15f; // titoli: ottica "Display"
            switch (family.ToLowerInvariant())
            {
                case "segoe ui":
                case "segoe ui variable":
                case "segoe ui variable text":
                case "segoe ui variable display":
                    return display ? "Segoe UI Variable Display" : "Segoe UI Variable Text";
                case "segoe ui semibold":
                case "segoe ui variable display semib":
                    style &= ~FontStyle.Bold;
                    return display ? "Segoe UI Variable Display Semib" : "Segoe UI Variable Text Semibold";
                case "segoe ui light":
                case "segoe ui semilight":
                    return display ? "Segoe UI Variable Display Light" : "Segoe UI Variable Text Light";
                case "segoe ui bold":
                case "segoe ui black":
                    style |= FontStyle.Bold;
                    return display ? "Segoe UI Variable Display" : "Segoe UI Variable Text";
                default:
                    return null;
            }
        }

        /// <summary>
        /// True while the calling thread is working for a modal sheet. Sheets are laid out in
        /// 100% pixels and enlarged as a whole by Windows (see SheetPresenter): inside them a
        /// size in points must not grow with the display scale a second time.
        /// </summary>
        internal static bool InSheetContext
        {
            get
            {
                try { return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) == 0; }
                catch { return false; }
            }
        }

        /// <summary>The same font with its size fixed in 100% pixels (points no longer follow the display scale).</summary>
        internal static Font ToSheetFont(Font font)
        {
            if (font.Unit != GraphicsUnit.Point) return font;
            try { return new Font(font.FontFamily, Math.Max(1f, font.SizeInPoints * (96f / 72f)), font.Style, GraphicsUnit.Pixel); }
            catch { return font; }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

        /// <summary>
        /// A font of the given point size for a window at the given DPI, fixed in pixels. Fonts in points
        /// are drawn at the DPI the program started with: after moving the window to a screen with another
        /// scale they stayed at the old size. Controls that draw themselves rebuild their fonts with this
        /// when the DPI of their window changes.
        /// </summary>
        public static Font CreateForDpi(string family, float points, FontStyle style, float dpi)
        {
            using Font probe = Create(family, points, style, GraphicsUnit.Point);
            if (probe.Unit != GraphicsUnit.Point) return (Font)probe.Clone();
            return new Font(probe.FontFamily, Math.Max(1f, probe.SizeInPoints * Math.Max(48f, dpi) / 72f), probe.Style, GraphicsUnit.Pixel);
        }

        /// <summary>
        /// Carattere per i testi lunghi e piccoli (trame, riga del cast): il Segoe UI classico, disegnato per
        /// restare nitido a corpo ridotto. Manrope e Segoe UI Variable, belli nei titoli, da piccoli risultano
        /// sottili e poco leggibili.
        /// </summary>
        public const string Reading = "Cinecore Reading";

        public static Font Create(string family, float size, FontStyle style, GraphicsUnit unit)
        {
            EnsureLoaded();
            if (unit == GraphicsUnit.Point && InSheetContext)
            {
                size *= 96f / 72f;
                unit = GraphicsUnit.Pixel;
            }
            if (string.Equals(family, Reading, StringComparison.OrdinalIgnoreCase))
            {
                try { return new Font("Segoe UI", Math.Max(1f, size), style, unit); }
                catch { family = "Segoe UI"; }
            }
            FontStyle systemStyle = style;
            if (SystemVariableFamily(family, size, ref systemStyle) is string system)
            {
                try { return new Font(system, Math.Max(1f, size * SystemSizeFactor), systemStyle, unit); }
                catch { }
            }
            if (Map.TryGetValue(family, out string? mapped))
            {
                // Bold on a single-weight family (e.g. "Semibold" + Bold) uses the bold face.
                if ((style & FontStyle.Bold) != 0 && !string.Equals(mapped, "Manrope", StringComparison.OrdinalIgnoreCase))
                    mapped = "Manrope";
                if (Families.TryGetValue(mapped, out FontFamily? ff))
                {
                    FontStyle usable = style;
                    if (!ff.IsStyleAvailable(usable)) usable &= ~FontStyle.Italic;
                    if (!ff.IsStyleAvailable(usable)) usable = FontStyle.Regular;
                    if (ff.IsStyleAvailable(usable))
                    {
                        try { return new Font(ff, Math.Max(1f, size * SizeFactor), usable, unit); }
                        catch { }
                    }
                }
            }
            return new Font(family, size, style, unit);
        }

        /// <summary>
        /// The size a font requested as <paramref name="family"/> at <paramref name="size"/>
        /// actually gets. Code that checks "has the size changed?" must compare with this,
        /// or it would rebuild (and dispose) the font on every layout.
        /// </summary>
        public static float EffectiveSize(string family, float size)
        {
            EnsureLoaded();
            FontStyle ignored = FontStyle.Regular;
            if (SystemVariableFamily(family, size, ref ignored) != null) return Math.Max(1f, size * SystemSizeFactor);
            return Map.TryGetValue(family, out string? mapped) && Families.ContainsKey(mapped)
                ? Math.Max(1f, size * SizeFactor)
                : size;
        }

        /// <summary>
        /// Assigns <paramref name="next"/> to <paramref name="control"/> and disposes whichever
        /// font is no longer used. Control.Font ignores a value equal to the current font, so
        /// disposing the "old" one unconditionally could dispose the font still in use.
        /// </summary>
        public static void ReplaceFont(System.Windows.Forms.Control control, Font next, bool disposeOld = true)
        {
            Font old = control.Font;
            control.Font = next;
            if (!ReferenceEquals(control.Font, next))
                next.Dispose();
            else if (disposeOld && !ReferenceEquals(old, next))
                old.Dispose();
        }

        private const uint FR_PRIVATE = 0x10;

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);
    }
}
