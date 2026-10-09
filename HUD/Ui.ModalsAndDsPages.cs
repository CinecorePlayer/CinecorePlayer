#nullable enable
using CinecorePlayer2025;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DSFilterCategory = DirectShowLib.FilterCategory;
using Svg.Skia;
using SkiaSharp;

namespace CinecorePlayer2025.HUD
{
    // ===================== THEME =====================
    internal static class Theme
    {
        // Evidenziazione unica dell'app (voce selezionata o in hover), la stessa della
        // sidebar: fondo tenue e bordo sottile. Usarla ovunque per la continuita' visiva.
        // Sidebar e cornice delle pagine (libreria, impostazioni): il pannello un filo piu' scuro;
        // il contenuto sta su una superficie arrotondata del colore del pannello.
        internal static Color SidebarSurface => IsLight
            ? Color.FromArgb((int)(Panel.R * .9 + 120 * .1), (int)(Panel.G * .9 + 130 * .1), (int)(Panel.B * .9 + 145 * .1))
            : Color.FromArgb((int)(Panel.R * .58), (int)(Panel.G * .58), (int)(Panel.B * .58));

        // Superficie delle schede modali: la stessa delle schede in pagina (album, dettagli), non il
        // fondo piu' scuro della pagina. SheetRaised e' per cio' che sta sopra la scheda (pulsanti
        // secondari, campi), SheetHover per lo stesso elemento sotto il puntatore.
        internal static Color Sheet => Card;
        internal static Color SheetRaised => Mix(Card, Text, IsLight ? .07 : .075);
        internal static Color SheetHover => Mix(Card, Text, IsLight ? .13 : .14);
        private static Color Mix(Color a, Color b, double t) =>
            Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

        // X di chiusura unica per tutte le schede: cerchio con velo tenue, piu' marcato in hover.
        internal static void DrawCloseButton(System.Drawing.Graphics g, System.Drawing.Rectangle r, bool hover, bool onDark = false)
        {
            // Su superfici sempre scure (scheda dettagli, player) la X resta chiara anche nel tema chiaro.
            var ink = onDark ? System.Drawing.Color.White : Text;
            bool light = IsLight && !onDark;
            var previous = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int d = Math.Min(r.Width, r.Height);
            var circle = new System.Drawing.Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(hover ? (light ? 34 : 30) : (light ? 18 : 14), ink)))
                g.FillEllipse(fill, circle);
            int inset = Math.Max(4, (int)Math.Round(d * .31));
            MusicTransportBar.DrawSymbol(g, System.Drawing.Rectangle.Inflate(circle, -inset, -inset), "close", System.Drawing.Color.FromArgb(hover ? 255 : 220, ink));
            g.SmoothingMode = previous;
        }

        internal static void DrawHighlight(System.Drawing.Graphics g, System.Drawing.Rectangle r, bool selected, int radius = 3)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            var previous = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var box = new System.Drawing.RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1);
            float d = Math.Max(10, radius * 2);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(box.Left, box.Top, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Top, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.Left, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            // Nessun contorno grigio: selezionato = velo d'accento con un filo d'accento,
            // hover = solo un velo leggerissimo.
            using var fill = new System.Drawing.SolidBrush(selected ? System.Drawing.Color.FromArgb(IsLight ? 34 : 44, Accent) : System.Drawing.Color.FromArgb(IsLight ? 16 : 14, Text));
            g.FillPath(fill, path);
            if (selected)
            {
                using var border = new System.Drawing.Pen(System.Drawing.Color.FromArgb(IsLight ? 110 : 96, Accent));
                g.DrawPath(border, path);
            }
            g.SmoothingMode = previous;
        }

        public static readonly Color DefaultBackdropDim = Color.FromArgb(210, 0, 2, 6);
        public static readonly Color DefaultPanel = Color.FromArgb(7, 12, 18);
        public static readonly Color DefaultCard = Color.FromArgb(14, 21, 29);
        public static readonly Color DefaultNav = Color.FromArgb(9, 15, 22);
        public static readonly Color DefaultPanelAlt = Color.FromArgb(14, 35, 61);
        public static readonly Color DefaultBorder = Color.FromArgb(42, 54, 66);
        public static readonly Color DefaultText = Color.White;
        public static readonly Color DefaultSubtleText = Color.FromArgb(190, 204, 220);
        public static readonly Color DefaultMuted = Color.FromArgb(142, 160, 180);
        public static readonly Color DefaultAccent = Color.FromArgb(50, 128, 235);
        public static readonly Color DefaultAccentSoft = Color.FromArgb(24, 69, 132);
        public static readonly Color DefaultSelection = Color.FromArgb(39, 96, 174);
        public static readonly Color DefaultBorderAccent = Color.FromArgb(74, 94, 114);
        public static Color BackdropDim { get; private set; } = DefaultBackdropDim;
        public static Color Panel { get; private set; } = DefaultPanel;
        public static Color Card { get; private set; } = DefaultCard;
        public static Color Nav { get; private set; } = DefaultNav;
        public static Color PanelAlt { get; private set; } = DefaultPanelAlt;
        public static Color Border { get; private set; } = DefaultBorder;
        public static Color Text { get; private set; } = DefaultText;
        public static Color SubtleText { get; private set; } = DefaultSubtleText;
        public static Color Muted { get; private set; } = DefaultMuted;
        public static Color Accent { get; private set; } = DefaultAccent;
        public static Color AccentSoft { get; private set; } = DefaultAccentSoft;
        public static Color Selection { get; private set; } = DefaultSelection;
        public static Color BorderAccent { get; private set; } = DefaultBorderAccent;
        public static readonly Color Danger = Color.FromArgb(255, 76, 92);
        // A restrained slate light theme is intentionally darker than a white UI.
        // Detect it as light early enough to switch typography to dark ink.
        public static bool IsLight => RelativeLuminance(Panel) >= 0.30f;

        public static void SetAccent(Color color)
        {
            Accent = Normalize(color);
            AccentSoft = Darken(Accent, 0.34f);
            Selection = Accent;
            BorderAccent = Blend(DefaultBorderAccent, Accent, 0.36f);
        }

        public static void SetPalette(Color accent, Color accentSoft, Color selection, Color borderAccent)
        {
            Accent = Normalize(accent);
            AccentSoft = Normalize(accentSoft);
            Selection = Normalize(selection);
            BorderAccent = Normalize(borderAccent);
        }

        public static void SetSurfacePalette(Color panel, Color card, Color nav)
        {
            Panel = Normalize(panel);
            Card = Normalize(card);
            Nav = Normalize(nav);
            PanelAlt = Blend(Panel, Selection, 0.42f);
            Border = Blend(DefaultBorder, BorderAccent, 0.28f);
            BackdropDim = Color.FromArgb(DefaultBackdropDim.A, Darken(Panel, 0.42f));
            if (IsLight)
            {
                Text = Color.FromArgb(23, 32, 42);
                SubtleText = Color.FromArgb(54, 68, 82);
                Muted = Color.FromArgb(78, 94, 109);
                Border = Blend(Color.FromArgb(132, 148, 163), BorderAccent, 0.20f);
                BackdropDim = Color.FromArgb(138, 175, 186, 197);
            }
            else
            {
                Text = DefaultText;
                SubtleText = DefaultSubtleText;
                Muted = DefaultMuted;
            }
        }

        public static void SetFullPalette(
            Color accent,
            Color accentSoft,
            Color selection,
            Color borderAccent,
            Color panel,
            Color card,
            Color nav)
        {
            SetPalette(accent, accentSoft, selection, borderAccent);
            SetSurfacePalette(panel, card, nav);
        }

        public static void ResetPalette()
        {
            BackdropDim = DefaultBackdropDim;
            Panel = DefaultPanel;
            Card = DefaultCard;
            Nav = DefaultNav;
            PanelAlt = DefaultPanelAlt;
            Border = DefaultBorder;
            Text = DefaultText;
            SubtleText = DefaultSubtleText;
            Muted = DefaultMuted;
            Accent = DefaultAccent;
            AccentSoft = DefaultAccentSoft;
            Selection = DefaultSelection;
            BorderAccent = DefaultBorderAccent;
        }

        private static Color Normalize(Color color)
            => Color.FromArgb(255, color.R, color.G, color.B);

        private static float RelativeLuminance(Color color)
            => (0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B) / 255f;

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            float inv = 1f - amount;
            return Color.FromArgb(
                255,
                Math.Max(0, Math.Min(255, (int)Math.Round(from.R * inv + to.R * amount))),
                Math.Max(0, Math.Min(255, (int)Math.Round(from.G * inv + to.G * amount))),
                Math.Max(0, Math.Min(255, (int)Math.Round(from.B * inv + to.B * amount))));
        }

        private static Color Darken(Color color, float amount)
        {
            amount = Math.Max(0f, Math.Min(0.85f, amount));
            float factor = 1f - amount;
            return Color.FromArgb(
                255,
                Math.Max(0, Math.Min(255, (int)Math.Round(color.R * factor))),
                Math.Max(0, Math.Min(255, (int)Math.Round(color.G * factor))),
                Math.Max(0, Math.Min(255, (int)Math.Round(color.B * factor))));
        }
    }

    // ===================== GRAFICA BASE =====================
    internal static class DrawHelpers
    {
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var gp = new GraphicsPath();
            gp.AddArc(r.Left, r.Top, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
    }

    // pannello con bordo 1px Theme.Border
    internal sealed class OutlinePanel : Panel
    {
        public OutlinePanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = DrawHelpers.RoundRect(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 8);
            using var p = new Pen(Color.FromArgb(42, 70, 94, 118), 1f);
            e.Graphics.DrawPath(p, path);
        }
    }

    // pannello "card" centrale della modale, con bordo 1px Theme.Border
    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = DrawHelpers.RoundRect(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 8);
            using var p = new Pen(Color.FromArgb(42, 70, 94, 118), 1f);
            e.Graphics.DrawPath(p, path);
        }
    }

    // ===================== HUD OVERLAY (player OSD) =====================
    internal static class DsHelpers
    {
        private static readonly Guid CLSID_XySubFilter = new("2DFCB782-EC20-4A7C-B530-4577ADB33F21");
        private static readonly Guid CLSID_MadVr = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");
        public static readonly Guid CLSID_MpcVideoRenderer = new("71F080AA-8661-4093-B15E-4F6903E77D0A");
        private static readonly object FilterDllLoadSync = new();
        private static readonly Dictionary<string, IntPtr> LoadedFilterDlls = new(StringComparer.OrdinalIgnoreCase);

        public static IBaseFilter? CreateFilterByFriendlyName(string? friendlyName)
        {
            if (string.IsNullOrWhiteSpace(friendlyName)) return null;
            if (friendlyName.Contains("MPC Video Renderer", StringComparison.OrdinalIgnoreCase) ||
                friendlyName.Equals("MPCVR", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer the exact build bundled with the player. A globally registered
                // MPCVR can be older and expose a different set of property pages.
                var bundled = CreateBundledMpcVideoRendererFilter();
                if (bundled != null)
                    return bundled;
            }
            Guid[] cats = {
                DSFilterCategory.LegacyAmFilterCategory,
                DSFilterCategory.AudioRendererCategory,
                DSFilterCategory.AudioCompressorCategory,
                DSFilterCategory.VideoCompressorCategory,
                DSFilterCategory.VideoInputDevice,
                DSFilterCategory.AudioInputDevice
            };
            foreach (var cat in cats)
            {
                foreach (var d in DsDevice.GetDevicesOfCat(cat))
                {
                    bool match =
                        d.Name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Contains(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        friendlyName.Contains(d.Name, StringComparison.OrdinalIgnoreCase);
                    if (match)
                    {
                        try
                        {
                            var iid = typeof(IBaseFilter).GUID;
                            d.Mon.BindToObject(null!, null, ref iid, out object obj);
                            return (IBaseFilter)obj;
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (IsXySubFilterName(friendlyName))
                return CreateBundledXySubFilter();
            if (friendlyName.Contains("madVR", StringComparison.OrdinalIgnoreCase))
                return CreateBundledMadVrFilter();
            if (friendlyName.Contains("MPC Video Renderer", StringComparison.OrdinalIgnoreCase) ||
                friendlyName.Equals("MPCVR", StringComparison.OrdinalIgnoreCase))
                return CreateBundledMpcVideoRendererFilter();

            return null;
        }

        public static IBaseFilter? CreateFilterByClsid(Guid clsid)
        {
            if (clsid == CLSID_MpcVideoRenderer)
            {
                var bundled = CreateBundledMpcVideoRendererFilter();
                if (bundled != null)
                    return bundled;
            }

            try
            {
                var t = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
                var obj = Activator.CreateInstance(t);
                return obj as IBaseFilter;
            }
            catch
            {
                if (clsid == CLSID_XySubFilter)
                    return CreateBundledXySubFilter();
                if (clsid == CLSID_MadVr)
                    return CreateBundledMadVrFilter();
                if (clsid == CLSID_MpcVideoRenderer)
                    return CreateBundledMpcVideoRendererFilter();
                return null;
            }
        }

        public static IBaseFilter? CreateBundledXySubFilter()
        {
            return CreateBundledXySubComObject(CLSID_XySubFilter, typeof(IBaseFilter).GUID) as IBaseFilter;
        }

        public static IBaseFilter? CreateBundledMadVrFilter()
        {
            return CreateBundledMadVrComObject(CLSID_MadVr, typeof(IBaseFilter).GUID) as IBaseFilter;
        }

        public static IBaseFilter? CreateBundledMpcVideoRendererFilter()
        {
            return CreateBundledMpcVideoRendererComObject(CLSID_MpcVideoRenderer, typeof(IBaseFilter).GUID) as IBaseFilter;
        }

        public static object? CreateBundledXySubComObject(Guid clsid, Guid iid)
        {
            foreach (var path in EnumerateBundledXySubFilterDlls())
            {
                if (!File.Exists(path))
                    continue;

                var obj = CreateComObjectFromDll(path, clsid, iid);
                if (obj != null)
                    return obj;
            }

            return null;
        }

        public static object? CreateBundledMadVrComObject(Guid clsid, Guid iid)
        {
            foreach (var path in EnumerateBundledMadVrComDlls())
            {
                if (!File.Exists(path))
                    continue;

                var obj = CreateComObjectFromDll(path, clsid, iid);
                if (obj != null)
                    return obj;
            }

            return null;
        }

        public static object? CreateBundledMpcVideoRendererComObject(Guid clsid, Guid iid)
        {
            foreach (var path in EnumerateBundledMpcVideoRendererDlls())
            {
                if (!File.Exists(path))
                    continue;

                var obj = CreateComObjectFromDll(path, clsid, iid);
                if (obj != null)
                    return obj;
            }

            return null;
        }

        public static object? CreateBundledPropertyPageComObject(Guid clsid, Guid iid)
        {
            foreach (var path in EnumerateBundledXySubFilterDlls()
                         .Concat(EnumerateBundledMadVrComDlls())
                         .Concat(EnumerateBundledMpcVideoRendererDlls()))
            {
                if (!File.Exists(path))
                    continue;

                var obj = CreateComObjectFromDll(path, clsid, iid);
                if (obj != null)
                    return obj;
            }

            return null;
        }

        private static bool IsXySubFilterName(string friendlyName)
        {
            return friendlyName.Contains("XySubFilter", StringComparison.OrdinalIgnoreCase)
                || friendlyName.Contains("xy-SubFilter", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> EnumerateBundledXySubFilterDlls()
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in EnumerateBundledSearchRoots())
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(root, "third-parties", "XySubFilter_3.1.0.752_x64", "XySubFilter.dll"),
                    Path.Combine(root, "XySubFilter_3.1.0.752_x64", "XySubFilter.dll")
                })
                {
                    if (emitted.Add(candidate))
                        yield return candidate;
                }

                string thirdParties = Path.Combine(root, "third-parties");
                IEnumerable<string> xyDirs = Array.Empty<string>();
                try
                {
                    if (Directory.Exists(thirdParties))
                        xyDirs = Directory.EnumerateDirectories(thirdParties, "XySubFilter*", SearchOption.TopDirectoryOnly).ToList();
                }
                catch { }

                foreach (string dir in xyDirs)
                {
                    string candidate = Path.Combine(dir, "XySubFilter.dll");
                    if (emitted.Add(candidate))
                        yield return candidate;
                }
            }
        }

        private static IEnumerable<string> EnumerateBundledMadVrComDlls()
        {
            string axName = Environment.Is64BitProcess ? "madVR64.ax" : "madVR.ax";
            string settingsName = Environment.Is64BitProcess ? "mvrSettings64.dll" : "mvrSettings32.dll";
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rootBase in EnumerateBundledSearchRoots())
            {
                var folders = new List<string>
                {
                    Path.Combine(rootBase, "third-parties", "madVR09217"),
                    Path.Combine(rootBase, "madVR09217")
                };

                string thirdParties = Path.Combine(rootBase, "third-parties");
                try
                {
                    if (Directory.Exists(thirdParties))
                        folders.AddRange(Directory.EnumerateDirectories(thirdParties, "madVR*", SearchOption.TopDirectoryOnly));
                }
                catch { }

                foreach (string root in folders)
                {
                    foreach (string candidate in new[] { Path.Combine(root, axName), Path.Combine(root, settingsName), Path.Combine(root, "madVR.ax"), Path.Combine(root, "madVR64.ax"), Path.Combine(root, "mvrSettings32.dll"), Path.Combine(root, "mvrSettings64.dll") })
                    {
                        if (emitted.Add(candidate))
                            yield return candidate;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateBundledMpcVideoRendererDlls()
        {
            string axName = Environment.Is64BitProcess ? "MpcVideoRenderer64.ax" : "MpcVideoRenderer.ax";
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rootBase in EnumerateBundledSearchRoots())
            {
                var folders = new List<string>
                {
                    Path.Combine(rootBase, "third-parties", "MpcVideoRenderer-0.9.7.2387"),
                    Path.Combine(rootBase, "third-parties", "MpcVideoRenderer"),
                    Path.Combine(rootBase, "MpcVideoRenderer-0.9.7.2387"),
                    Path.Combine(rootBase, "MpcVideoRenderer")
                };

                string thirdParties = Path.Combine(rootBase, "third-parties");
                try
                {
                    if (Directory.Exists(thirdParties))
                        folders.AddRange(Directory.EnumerateDirectories(thirdParties, "MpcVideoRenderer*", SearchOption.TopDirectoryOnly));
                }
                catch { }

                foreach (string folder in folders)
                {
                    foreach (string candidate in new[]
                    {
                        Path.Combine(folder, axName),
                        Path.Combine(folder, "MpcVideoRenderer64.ax"),
                        Path.Combine(folder, "MpcVideoRenderer.ax")
                    })
                    {
                        if (emitted.Add(candidate))
                            yield return candidate;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateBundledSearchRoots()
        {
            var roots = new List<string>();
            void AddRoot(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;
                try { roots.Add(Path.GetFullPath(path)); } catch { }
            }

            AddRoot(AppContext.BaseDirectory);
            AddRoot(Environment.CurrentDirectory);
            AddRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")));

            string? parent = null;
            try { parent = Directory.GetParent(AppContext.BaseDirectory)?.FullName; } catch { }
            int guard = 0;
            while (!string.IsNullOrWhiteSpace(parent) && guard++ < 8)
            {
                AddRoot(parent);
                try { parent = Directory.GetParent(parent)?.FullName; } catch { parent = null; }
            }

            foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                yield return root;
        }

        private static object? CreateComObjectFromDll(string dllPath, Guid clsid, Guid iid)
        {
            try
            {
                IntPtr lib;
                lock (FilterDllLoadSync)
                {
                    if (!LoadedFilterDlls.TryGetValue(dllPath, out lib))
                    {
                        lib = NativeLibrary.Load(dllPath);
                        LoadedFilterDlls[dllPath] = lib;
                    }
                }

                IntPtr proc = NativeLibrary.GetExport(lib, "DllGetClassObject");
                var getClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(proc);

                Guid cls = clsid;
                Guid factoryIid = typeof(IClassFactory).GUID;
                int hr = getClassObject(ref cls, ref factoryIid, out object? factoryObj);
                if (hr != 0 || factoryObj is not IClassFactory factory)
                    return null;

                try
                {
                    Guid requested = iid;
                    hr = factory.CreateInstance(null, ref requested, out object? obj);
                    if (hr != 0)
                    {
                        if (obj != null && Marshal.IsComObject(obj))
                            try { Marshal.ReleaseComObject(obj); } catch { }
                        return null;
                    }

                    return obj;
                }
                finally
                {
                    try { if (Marshal.IsComObject(factoryObj)) Marshal.ReleaseComObject(factoryObj); } catch { }
                }
            }
            catch
            {
                return null;
            }
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000001-0000-0000-C000-000000000046")]
        private interface IClassFactory
        {
            [PreserveSig]
            int CreateInstance(
                [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
                ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);

            [PreserveSig]
            int LockServer(bool fLock);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DllGetClassObjectDelegate(
            ref Guid rclsid,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    }

    // ===================== Host property pages di DirectShow =====================
    /// <summary>Page switcher for multi-page filter settings (LAV Audio: Settings, Mixing, Formats…).</summary>
    internal sealed class PageTabStrip : Control
    {
        private readonly List<Rectangle> _tabs = new();
        private int _selected = -1, _hover = -1;
        public List<string> Items { get; } = new();
        public event EventHandler? SelectedIndexChanged;

        public PageTabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                int next = Items.Count == 0 ? -1 : Math.Clamp(value, 0, Items.Count - 1);
                if (next == _selected) return;
                _selected = next;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            _tabs.Clear();
            float scale = DeviceDpi / 96f;
            int gap = (int)(10 * scale), padX = (int)(12 * scale), x = -padX; // first label aligns with the page
            int h = Math.Max(1, Height - (int)(4 * scale));
            for (int i = 0; i < Items.Count; i++)
            {
                Size text = TextRenderer.MeasureText(g, Items[i], Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                var tab = new Rectangle(x, 0, text.Width + padX * 2, h);
                _tabs.Add(tab);
                bool selected = i == _selected, hover = i == _hover;
                TextRenderer.DrawText(g, Items[i], Font, tab, selected || hover ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (selected)
                {
                    using var accent = new SolidBrush(Theme.Accent);
                    g.FillRectangle(accent, tab.Left + padX, tab.Bottom - (int)(2 * scale), tab.Width - padX * 2, Math.Max(2, (int)(2 * scale)));
                }
                if (selected && Focused && ShowFocusCues)
                {
                    using var pen = new Pen(Color.FromArgb(150, Theme.Accent)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                    g.DrawRectangle(pen, Rectangle.Inflate(tab, -3, -3));
                }
                x = tab.Right + gap;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hover = _tabs.FindIndex(tab => tab.Contains(e.Location));
            if (hover != _hover) { _hover = hover; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int hit = _tabs.FindIndex(tab => tab.Contains(e.Location));
            if (hit >= 0) SelectedIndex = hit;
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left && _selected > 0) SelectedIndex = _selected - 1;
            else if (e.KeyCode == Keys.Right && _selected < Items.Count - 1) SelectedIndex = _selected + 1;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    internal sealed class DsPropPageHost : Panel, IPropertyPageSite, IMessageFilter, IDisposable
    {
        private readonly NativePropertyPageTheme _nativeTheme = new();
        private readonly System.Windows.Forms.Timer _themeTick = new() { Interval = 600 };
        private readonly Panel _toolbar = new()
        {
            Height = 46,
            BackColor = Theme.Panel,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 0, 6),
            Visible = false
        };
        private readonly PageTabStrip _pages = new() { Dock = DockStyle.Fill };
        private readonly Panel _viewport = new()
        {
            BackColor = Theme.Panel,
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
            AutoScroll = true
        };
        private readonly Panel _pageCanvas = new()
        {
            BackColor = Theme.Panel,
            Location = Point.Empty,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        private object? _filter;
        private IPropertyPage[] _pp = Array.Empty<IPropertyPage>();
        private Size[] _pageSizes = Array.Empty<Size>();
        private int _active = -1;
        private bool _suspendNav;
        private bool _translatingAccelerator;
        private bool _ownsFilter;

        private IFilterGraph2? _tempGraph;
        private bool _addedToGraph;
        private bool _messageFilterInstalled;
        private bool _english;

        public int ExtraTopPaddingDpiLogical = 0;

        private string L(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);

        public void SetLanguage(string? language)
        {
            _english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        }

        public DsPropPageHost()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Panel;
            Padding = new Padding(0, 4, 0, 0);

            Controls.Add(_viewport);
            Controls.Add(_toolbar);

            _viewport.Controls.Add(_pageCanvas);
            _toolbar.Controls.Add(_pages);
            _pages.SelectedIndexChanged += (_, __) =>
            {
                if (!_suspendNav) ActivateIndex(_pages.SelectedIndex);
            };

            Resize += (_, __) => RefitActivePage();
            _viewport.Resize += (_, __) => RefitActivePage();
            _themeTick.Tick += (_, __) => { if (Visible && _active >= 0) _nativeTheme.Apply(_pageCanvas.Handle); };
            VisibleChanged += (_, __) => { if (Visible) _themeTick.Start(); else _themeTick.Stop(); };
            InstallMessageFilter();
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            // The card and the native page share one surface colour.
            try { _viewport.BackColor = _pageCanvas.BackColor = _toolbar.BackColor = _pages.BackColor = Theme.Panel; } catch { }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // No card: the vendor page sits directly on the settings surface.
            using var fill = new SolidBrush(Theme.Panel);
            e.Graphics.FillRectangle(fill, ClientRectangle);
        }

        private void InstallMessageFilter()
        {
            if (_messageFilterInstalled)
                return;

            try
            {
                Application.AddMessageFilter(this);
                _messageFilterInstalled = true;
            }
            catch { }
        }

        public void LoadFromFriendlyName(string friendlyName)
        {
            Clear();
            var f = DsHelpers.CreateFilterByFriendlyName(friendlyName);
            if (f == null) throw new ApplicationException(L($"Filtro \"{friendlyName}\" non trovato.", $"Filter \"{friendlyName}\" was not found."));
            _filter = f;
            _ownsFilter = true;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }
        public void LoadFromClsid(Guid clsid)
        {
            Clear();
            var f = DsHelpers.CreateFilterByClsid(clsid);
            if (f == null) throw new ApplicationException(L($"CLSID {clsid} non trovato/instanziabile.", $"CLSID {clsid} was not found or could not be instantiated."));
            _filter = f;
            _ownsFilter = true;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }
        public void LoadFromFilter(IBaseFilter filter)
        {
            Clear();
            _filter = filter;
            _ownsFilter = false;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }

        private void AttachToTempGraphIfNeeded()
        {
            try
            {
                if (!_ownsFilter || _filter is not IBaseFilter bf) return;
                _tempGraph = (IFilterGraph2)new FilterGraph();
                int hr = _tempGraph.AddFilter(bf, "CfgTarget");
                _addedToGraph = hr >= 0;
            }
            catch
            {
                _addedToGraph = false;
                try { if (_tempGraph != null) Marshal.ReleaseComObject(_tempGraph); } catch { }
                _tempGraph = null;
            }
        }

        public void Apply()
        {
            foreach (var p in _pp)
            {
                try
                {
                    int hr = p.IsPageDirty();
                    if (hr != 0) // != S_OK
                        continue;

                    p.Apply();
                }
                catch
                {
                }
            }
        }

        public void Clear()
        {
            _themeTick.Stop();
            _nativeTheme.Clear();
            _active = -1;
            try
            {
                for (int i = 0; i < _pp.Length; i++)
                {
                    try { _pp[i].Show(0); } catch { }
                    try { _pp[i].Deactivate(); } catch { }
                    try { Marshal.ReleaseComObject(_pp[i]); } catch { }
                }
            }
            catch { }

            _pp = Array.Empty<IPropertyPage>();
            _pageSizes = Array.Empty<Size>();
            _pages.Items.Clear();
            ClearViewportControls(keepCanvas: true);
            _pageCanvas.Controls.Clear();
            _active = -1;

            try
            {
                if (_addedToGraph && _tempGraph != null && _filter is IBaseFilter bf)
                {
                    try { _tempGraph.RemoveFilter(bf); } catch { }
                }
            }
            catch { }

            if (_ownsFilter && _filter != null && Marshal.IsComObject(_filter))
                try { Marshal.ReleaseComObject(_filter); } catch { }
            _filter = null;

            if (_tempGraph != null)
            {
                try { Marshal.ReleaseComObject(_tempGraph); } catch { }
                _tempGraph = null;
            }
            _addedToGraph = false;
        }

        private void BuildPages()
        {
            if (_filter == null) return;

            _suspendNav = true;
            _pages.Items.Clear();
            _pp = Array.Empty<IPropertyPage>();
            _pageSizes = Array.Empty<Size>();

            try
            {
                var spp = _filter as ISpecifyPropertyPages
                    ?? throw new ApplicationException(L("Il filtro non espone pagine di proprietà.", "The filter does not expose property pages."));

                spp.GetPages(out var cauuid);
                try
                {
                    var okPages = new List<IPropertyPage>();
                    var okTitles = new List<string>();
                    var okSizes = new List<Size>();

                    if (cauuid.cElems > 0 && cauuid.pElems != nint.Zero)
                    {
                        for (int i = 0; i < cauuid.cElems; i++)
                        {
                            Guid clsid = Marshal.PtrToStructure<Guid>(
                                nint.Add(cauuid.pElems, i * Marshal.SizeOf<Guid>()));

                            IPropertyPage? page = null;
                            try
                            {
                                object? pageObj = DsHelpers.CreateBundledPropertyPageComObject(clsid, typeof(IPropertyPage).GUID);
                                try
                                {
                                    var type = Type.GetTypeFromCLSID(clsid, true)!;
                                    pageObj ??= Activator.CreateInstance(type);
                                }
                                catch
                                {
                                    pageObj = DsHelpers.CreateBundledPropertyPageComObject(clsid, typeof(IPropertyPage).GUID);
                                }

                                if (pageObj is not IPropertyPage createdPage)
                                {
                                    if (pageObj != null && Marshal.IsComObject(pageObj))
                                        try { Marshal.ReleaseComObject(pageObj); } catch { }
                                    continue;
                                }

                                page = createdPage;

                                object unk = _filter!;
                                page.SetObjects(1, ref unk);
                                page.SetPageSite(this);
                                page.GetPageInfo(out var info);

                                string title = info.pszTitle != nint.Zero
                                    ? Marshal.PtrToStringUni(info.pszTitle) ?? $"{L("Pagina", "Page")} {okPages.Count + 1}"
                                    : $"{L("Pagina", "Page")} {okPages.Count + 1}";

                                okPages.Add(page);
                                okTitles.Add(title);
                                okSizes.Add(new Size(
                                    Math.Max(1, info.size.Width),
                                    Math.Max(1, info.size.Height)));
                                page = null;
                            }
                            catch
                            {
                                try { if (page != null) Marshal.ReleaseComObject(page); } catch { }
                            }
                        }
                    }

                    if (okPages.Count == 0)
                    {
                        _pp = Array.Empty<IPropertyPage>();
                        _pageSizes = Array.Empty<Size>();
                        _pages.Items.Add(L("(Nessuna pagina delle proprietà disponibile)", "(No property page available)"));
                        _pages.SelectedIndex = 0;
                        ShowPlaceholder(L("(Nessuna pagina delle proprietà disponibile)", "(No property page available)"));
                    }
                    else
                    {
                        _pp = okPages.ToArray();
                        _pageSizes = okSizes.ToArray();
                        foreach (var t in okTitles) _pages.Items.Add(t);
                        _pages.SelectedIndex = 0;
                    }
                }
                finally
                {
                    if (cauuid.pElems != nint.Zero) Marshal.FreeCoTaskMem(cauuid.pElems);
                }
            }
            catch (Exception ex)
            {
                _pp = Array.Empty<IPropertyPage>();
                _pageSizes = Array.Empty<Size>();
                _pages.Items.Add(L("Errore: ", "Error: ") + ex.Message);
                _pages.SelectedIndex = 0;
                ShowPlaceholder(L("Errore: ", "Error: ") + ex.Message);
            }
            finally
            {
                _suspendNav = false;
            }

            _toolbar.Visible = _pp.Length > 1;
            EnsureFirstPageActivated();
        }

        private void EnsureFirstPageActivated()
        {
            if (_pp.Length > 0)
                ActivateIndex(0);
        }

        private void ShowPlaceholder(string text)
        {
            ClearViewportControls(keepCanvas: false);
            _pageCanvas.Controls.Clear();
            var lbl = new Label
            {
                Text = text,
                ForeColor = Theme.Muted,
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _viewport.Controls.Add(lbl);
        }

        private void ClearViewportControls(bool keepCanvas)
        {
            try
            {
                foreach (Control child in _viewport.Controls.Cast<Control>().ToArray())
                {
                    if (keepCanvas && ReferenceEquals(child, _pageCanvas))
                        continue;

                    _viewport.Controls.Remove(child);
                    if (!ReferenceEquals(child, _pageCanvas))
                    {
                        try { child.Dispose(); } catch { }
                    }
                }

                if (keepCanvas && !_viewport.Controls.Contains(_pageCanvas))
                    _viewport.Controls.Add(_pageCanvas);
            }
            catch { }
        }

        private int TopEmbedOffset
        {
            get
            {
                float scale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
                int logical = ExtraTopPaddingDpiLogical;
                int px = (int)Math.Round(logical * scale);
                return Math.Max(0, px);
            }
        }

        private RECT CalcRectToFit()
        {
            // Keep the vendor dialog at its preferred size; scroll the host when
            // needed instead of stretching fixed native controls across the page.
            EnsurePageCanvasSize();

            var view = _pageCanvas.ClientRectangle;
            if (view.Width < 1 || view.Height < 1) view = new Rectangle(0, 0, 1, 1);

            return new RECT
            {
                left = 0,
                top = TopEmbedOffset,
                right = Math.Max(1, view.Width),
                bottom = Math.Max(1, view.Height)
            };
        }

        private void EnsurePageCanvasSize()
        {
            try
            {
                var view = _viewport.ClientRectangle;
                int availableWidth = Math.Max(1, view.Width);
                int w = availableWidth;
                int h = Math.Max(1, view.Height);

                if (_active >= 0 && _active < _pageSizes.Length)
                {
                    var preferred = _pageSizes[_active];
                    // Native property pages use fixed dialog units. Center the real page
                    // rather than giving it the entire host width and leaving one side empty.
                    w = Math.Max(1, preferred.Width);
                    h = Math.Max(1, preferred.Height + TopEmbedOffset);
                }

                if (_pageCanvas.Width != w || _pageCanvas.Height != h)
                    _pageCanvas.Size = new Size(w, h);

                _viewport.AutoScrollMinSize = new Size(w, h);
                Point scroll = _viewport.AutoScrollPosition;
                // Left-aligned with the page heading, like every other settings section.
                _pageCanvas.Location = new Point(scroll.X, scroll.Y);
            }
            catch { }
        }

        private void ActivateIndex(int index)
        {
            if (_pp.Length == 0)
            {
                ShowPlaceholder(L("(Nessuna pagina delle proprietà disponibile)", "(No property page available)"));
                _active = -1;
                return;
            }
            if (index < 0 || index >= _pp.Length)
            {
                ShowPlaceholder(L("(Indice pagina non valido)", "(Invalid page index)"));
                _active = -1;
                return;
            }

            var next = _pp[index];
            if (next == null)
            {
                ShowPlaceholder(L("(Pagina non disponibile)", "(Page unavailable)"));
                _active = -1;
                return;
            }

            _nativeTheme.Clear();
            if (_active >= 0 && _active < _pp.Length && _pp[_active] != null)
            {
                try { _pp[_active].Show(0); } catch { }
                try { _pp[_active].Deactivate(); } catch { }
            }

            ClearViewportControls(keepCanvas: true);
            _pageCanvas.Controls.Clear();
            _active = index;
            _viewport.AutoScrollPosition = Point.Empty;
            _pageCanvas.Location = Point.Empty;
            EnsurePageCanvasSize();
            try { if (!_pageCanvas.IsHandleCreated) _pageCanvas.CreateControl(); } catch { }

            var rc = CalcRectToFit();
            try
            {
                next.Activate(_pageCanvas.Handle, ref rc, 0);
                next.Show(5);
                MeasureNativePage();
                _nativeTheme.Apply(_pageCanvas.Handle);
                RefitActivePage();
            }
            catch (Exception ex)
            {
                ShowPlaceholder(L("Errore nell'attivazione della pagina:\r\n", "Could not activate the page:\r\n") + ex.Message);
            }
        }

        private void RefitActivePage()
        {
            if (_active < 0 || _active >= _pp.Length) return;
            EnsurePageCanvasSize();
            var rc = CalcRectToFit();
            try { _pp[_active].Move(ref rc); } catch { }
        }

        // Vendor pages can report their unscaled resource size through GetPageInfo.
        // Measure the actual HWND controls after activation, including children that
        // extend beyond that rectangle, before deciding the scrollable canvas size.
        private void MeasureNativePage()
        {
            if (_active < 0 || !_pageCanvas.IsHandleCreated) return;
            Size size = _pageSizes[_active];
            Point origin = _pageCanvas.PointToScreen(Point.Empty);
            EnumPageChildren(_pageCanvas.Handle, (window, _) =>
            {
                // The top-level dialog already has the size supplied to Activate;
                // including it plus a margin would grow the page on every reopening.
                if (GetPageParent(window) == _pageCanvas.Handle) return true;
                if (GetPageWindowRect(window, out var rect))
                {
                    size.Width = Math.Max(size.Width, rect.Right - origin.X + 12);
                    size.Height = Math.Max(size.Height, rect.Bottom - origin.Y + 12 - TopEmbedOffset);
                }
                return true;
            }, IntPtr.Zero);
            _pageSizes[_active] = size;
        }

        private delegate bool PageEnumProc(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)]
        private struct PageWindowRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
        private static extern bool EnumPageChildren(IntPtr parent, PageEnumProc callback, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
        private static extern bool GetPageWindowRect(IntPtr window, out PageWindowRect rect);
        [DllImport("user32.dll", EntryPoint = "GetParent")]
        private static extern IntPtr GetPageParent(IntPtr window);

        // IPropertyPageSite
        int IPropertyPageSite.OnStatusChange(int dwFlags) => 0;
        int IPropertyPageSite.GetLocaleID(out int pLocaleID) { pLocaleID = _english ? 0x0409 : 0x0410; return 0; }
        int IPropertyPageSite.GetPageContainer(out object ppUnk) { ppUnk = this; return 0; }
        int IPropertyPageSite.TranslateAccelerator(ref MSG pMsg) => 1;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_translatingAccelerator) return false;
            if (_active >= 0 && _active < _pp.Length)
            {
                try
                {
                    var m = new MSG
                    {
                        hWnd = msg.HWnd,
                        message = (uint)msg.Msg,
                        wParam = msg.WParam,
                        lParam = msg.LParam,
                        time = 0,
                        pt = Point.Empty
                    };
                    int hr;
                    _translatingAccelerator = true;
                    try { hr = _pp[_active].TranslateAccelerator(ref m); }
                    finally { _translatingAccelerator = false; }
                    if (hr == 0)
                        return true;
                }
                catch { }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        public bool PreFilterMessage(ref Message msg)
        {
            if (_translatingAccelerator) return false;
            if (_active < 0 || _active >= _pp.Length)
                return false;
            if (!Visible || IsDisposed || !IsHandleCreated || !_viewport.IsHandleCreated)
                return false;

            const int WM_KEYFIRST = 0x0100;
            const int WM_KEYLAST = 0x0109;
            if (msg.Msg < WM_KEYFIRST || msg.Msg > WM_KEYLAST)
                return false;

            try
            {
                if (msg.HWnd != _viewport.Handle && !IsChild(_viewport.Handle, msg.HWnd))
                    return false;

                var m = new MSG
                {
                    hWnd = msg.HWnd,
                    message = (uint)msg.Msg,
                    wParam = msg.WParam,
                    lParam = msg.LParam,
                    time = 0,
                    pt = Point.Empty
                };
                int hr;
                _translatingAccelerator = true;
                try { hr = _pp[_active].TranslateAccelerator(ref m); }
                finally { _translatingAccelerator = false; }
                if (hr == 0)
                    return true;
            }
            catch { }

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_messageFilterInstalled)
                {
                    try { Application.RemoveMessageFilter(this); } catch { }
                    _messageFilterInstalled = false;
                }
                try { Clear(); } catch { }
                _themeTick.Dispose();
                _nativeTheme.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsChild(nint hWndParent, nint hWnd);
    }

    // ===================== madVR settings embedder =====================
    internal sealed class MadVrSettingsEmbedder : Panel
    {
        private readonly NativePropertyPageTheme _nativeTheme = new();
        private readonly Panel _viewport = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel };
        private readonly Panel _canvas = new() { BackColor = Theme.Panel, Visible = false };
        private Size _nativeSize;
        private Process? _proc;
        private nint _wnd = nint.Zero;

        private readonly System.Windows.Forms.Timer _tick;
        private bool _embedded;
        private bool _started;
        private DateTime _lastKickUtc = DateTime.MinValue;
        private int _kickAttempts;

        private nint _oldParent = nint.Zero;
        private int _oldStyle = 0;
        private bool _savedOld = false;

        private uint _ctrlPid;
        private Panel? _pendingSettingsHost;
        private IPropertyPage? _pendingSettingsPage;
        private object? _pendingSettingsFilter;
        private System.Windows.Forms.Timer? _pendingSettingsCleanup;

        private readonly Panel _placeholder = new() { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        private readonly Label _btnReopen = new()
        {
            Text = "Apri impostazioni madVR",
            Width = 260,
            Height = 34,
            BackColor = Color.Transparent,
            ForeColor = Theme.Accent,
            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10f),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            AccessibleRole = AccessibleRole.Link
        };
        private readonly Label _placeholderText = new() { ForeColor = Theme.Muted, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        private bool _english;

        private static readonly Guid CLSID_madVR = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");

        public MadVrSettingsEmbedder()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;

            _placeholderText.Text = "Le impostazioni madVR vengono aperte e agganciate qui automaticamente.";

            _btnReopen.Visible = false;

            _placeholder.Controls.Add(_btnReopen);
            _placeholder.Controls.Add(_placeholderText);
            _viewport.Controls.Add(_canvas);
            Controls.Add(_viewport);
            Controls.Add(_placeholder);

            Layout += (_, __) =>
            {
                _placeholderText.Bounds = new Rectangle(24, Math.Max(20, Height / 2 - 68), Math.Max(1, Width - 48), 42);
                _btnReopen.Left = Math.Max(0, (Width - _btnReopen.Width) / 2);
                _btnReopen.Top = Math.Max(62, Height / 2 - 10);
            };

            _btnReopen.MouseEnter += (_, __) => _btnReopen.ForeColor = ControlPaint.Light(Theme.Accent, .18f);
            _btnReopen.MouseLeave += (_, __) => _btnReopen.ForeColor = Theme.Accent;
            _btnReopen.Click += (_, __) =>
            {
                _kickAttempts = 0;
                _lastKickUtc = DateTime.MinValue;
                _btnReopen.Visible = false;
                EnsureStarted();
            };
            MouseDown += (_, __) => FocusEmbeddedSettingsWindow();

            _tick = new System.Windows.Forms.Timer { Interval = 350 };
            _tick.Tick += (_, __) =>
            {
                if (!_started) return;
                if (!Visible || !IsHandleCreated) return;

                if (!_embedded)
                {
                    TryFindAndEmbed();
                }
                else
                {
                    if (_wnd == nint.Zero || !IsWindow(_wnd) || GetParent(_wnd) != _canvas.Handle)
                    {
                        OnChildClosedOrLost();
                    }
                    else _nativeTheme.Apply(_wnd);
                }

                if (_started && !_embedded && (_wnd == nint.Zero || !IsWindow(_wnd)))
                    TryKickOpenSettingsThrottled();
            };

            HandleDestroyed += (_, __) => Cleanup(true);
            _viewport.Resize += (_, __) => LayoutEmbeddedWindow();
            VisibleChanged += (_, __) =>
            {
                if (Visible && _started) _tick.Start();
                else _tick.Stop();
            };
        }

        private void LayoutEmbeddedWindow()
        {
            if (!_embedded || _nativeSize.IsEmpty) return;
            _viewport.AutoScrollMinSize = _nativeSize;
            Point scroll = _viewport.AutoScrollPosition;
            // Left-aligned with the page heading, like the other filter pages.
            _canvas.Bounds = new Rectangle(scroll.X, scroll.Y, _nativeSize.Width, _nativeSize.Height);
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsStarted => _started;

        private string L(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);

        public void SetLanguage(string? language)
        {
            _english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
            _btnReopen.Text = L("Apri impostazioni madVR", "Open madVR settings");
            if (!_started)
                ShowPlaceholder(L("Le impostazioni madVR vengono aperte e agganciate qui automaticamente.", "madVR settings are opened and embedded here automatically."));
        }

        public void EnsureStarted()
        {
            if (!_started)
            {
                _started = true;
                Start();
            }
            else
            {
                if (_wnd == nint.Zero || !IsWindow(_wnd) || !_embedded)
                {
                    TryKickOpenSettingsThrottled(force: true);
                    TryFindAndEmbed();
                }
                if (!_tick.Enabled) _tick.Start();
            }
        }

        private void Start()
        {
            string? folder = GetMadVrFolder();
            if (folder == null)
            {
                ShowPlaceholder(L("madVR non installato/registrato.", "madVR is not installed or registered."));
                return;
            }

            string exe = Path.Combine(folder, "madHcCtrl.exe");
            if (!File.Exists(exe))
            {
                ShowPlaceholder(L($"madHcCtrl.exe non trovato in:\r\n{folder}", $"madHcCtrl.exe was not found in:\r\n{folder}"));
                return;
            }

            try
            {
                var already = Process.GetProcessesByName("madHcCtrl").FirstOrDefault();
                if (already == null)
                {
                    _proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = folder,
                        UseShellExecute = false
                    });
                    _ctrlPid = (uint)(_proc?.Id ?? 0);
                }
                else
                {
                    _ctrlPid = (uint)already.Id;
                }

                _kickAttempts = 0;
                _lastKickUtc = DateTime.MinValue;
                TryKickOpenSettingsThrottled(force: true);
                _tick.Start();
            }
            catch (Exception ex)
            {
                ShowPlaceholder(L("Errore avvio madVR: ", "Could not start madVR: ") + ex.Message);
            }
        }

        private void TryFindAndEmbed()
        {
            if (_embedded) return;

            nint w = FindSettingsWindow();
            if (w == nint.Zero) return;

            _wnd = w;
            if (GetClientRect(_wnd, out var client))
                _nativeSize = new Size(Math.Max(1, client.right - client.left), Math.Max(1, client.bottom - client.top));
            else
                _nativeSize = new Size(800, 600);

            if (!_savedOld)
            {
                _oldParent = GetParent(_wnd);
                _oldStyle = GetWindowLong(_wnd, GWL_STYLE);
                _savedOld = true;
            }

            int style = _oldStyle;
            style = (style | WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS) &
                    ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX |
                      WS_MAXIMIZEBOX | WS_SYSMENU | WS_POPUP);
            SetWindowLong(_wnd, GWL_STYLE, style);
            _canvas.Size = _nativeSize;
            SetParent(_wnd, _canvas.Handle);
            MoveWindow(_wnd, 0, 0, _nativeSize.Width, _nativeSize.Height, true);
            ShowWindow(_wnd, SW_SHOW);
            EnableWindow(_wnd, true);
            _nativeTheme.Apply(_wnd);

            _embedded = true;
            _canvas.Visible = true;
            LayoutEmbeddedWindow();
            _placeholder.Visible = false;

            FocusEmbeddedSettingsWindow();
        }

        private void FocusEmbeddedSettingsWindow()
        {
            try
            {
                if (_wnd == nint.Zero || !IsWindow(_wnd))
                    return;

                SetForegroundWindow(FindForm()?.Handle ?? Handle);
                SetFocus(_wnd);
            }
            catch { }
        }

        private void OnChildClosedOrLost()
        {
            _tick.Stop();
            _started = false;
            _canvas.Visible = false;
            _embedded = false;
            _wnd = nint.Zero;
            _savedOld = false;
            _oldParent = nint.Zero;
            _oldStyle = 0;
            ShowPlaceholder(L("Impostazioni madVR chiuse.", "madVR settings closed."));
            _btnReopen.Visible = true;
        }

        private void TryKickOpenSettingsThrottled(bool force = false)
        {
            try
            {
                if (!Visible || !IsHandleCreated)
                    return;

                var now = DateTime.UtcNow;
                if (!force && _kickAttempts >= 2)
                {
                    ShowPlaceholder(L("madVR non ha aperto automaticamente la finestra delle impostazioni.", "madVR did not open its settings window automatically."));
                    return;
                }

                if (!force && (now - _lastKickUtc).TotalMilliseconds < 2500)
                    return;

                _lastKickUtc = now;
                _kickAttempts++;
                if (_kickAttempts > 4)
                {
                    ShowPlaceholder(L("madVR non ha aperto la finestra delle impostazioni.", "madVR did not open its settings window."));
                    return;
                }
                KickOpenSettingsViaPropPage();
            }
            catch { }
        }

        private void KickOpenSettingsViaPropPage()
        {
            Panel? host = null;
            IPropertyPage? pendingPage = null;
            object? pendingFilter = null;
            try
            {
                var filter = DsHelpers.CreateFilterByClsid(CLSID_madVR);
                if (filter == null) { Dbg.Warn("madVR settings: renderer COM object unavailable."); return; }
                pendingFilter = filter;

                if (filter is not ISpecifyPropertyPages spp) { Dbg.Warn("madVR settings: property pages unavailable."); return; }

                spp.GetPages(out var cauuid);
                try
                {
                    if (cauuid.cElems <= 0 || cauuid.pElems == nint.Zero) { Dbg.Warn("madVR settings: no property page CLSID."); return; }

                    Guid pageClsid = Marshal.PtrToStructure<Guid>(cauuid.pElems);
                    object? pageObj = null;
                    try
                    {
                        var type = Type.GetTypeFromCLSID(pageClsid, true)!;
                        pageObj = Activator.CreateInstance(type);
                    }
                    catch
                    {
                        pageObj = DsHelpers.CreateBundledPropertyPageComObject(pageClsid, typeof(IPropertyPage).GUID);
                    }

                    if (pageObj is not IPropertyPage page)
                    {
                        Release(pageObj);
                        return;
                    }
                    pendingPage = page;

                    object unk = filter;
                    page.SetObjects(1, ref unk);
                    page.SetPageSite(new DummySite());

                    host = new Panel
                    {
                        Visible = true,
                        Width = 5,
                        Height = 5,
                        Left = -10000,
                        Top = -10000
                    };
                    Controls.Add(host);
                    host.CreateControl();

                    var rc = new RECT { left = 0, top = 0, right = 320, bottom = 200 };
                    page.Activate(host.Handle, ref rc, 0);
                    page.Show(5);

                    nint btn = nint.Zero;
                    EnumChildWindows(host.Handle, (h, l) =>
                    {
                        if (!string.Equals(GetClass(h), "BUTTON", StringComparison.OrdinalIgnoreCase)) return true;
                        string txt = GetText(h).ToLowerInvariant();
                        if (txt.Contains("setting") || txt.Contains("impostaz") || txt.Contains("config"))
                        { btn = h; return false; }
                        return true;
                    }, nint.Zero);

                    if (btn != nint.Zero)
                    {
                        Dbg.Log("madVR settings launcher button: " + GetText(btn));
                        // The control process creates its settings window after the click.
                        // Keep the COM page and its hidden host alive until that message runs.
                        CleanupPendingSettingsLauncher();
                        _pendingSettingsHost = host;
                        _pendingSettingsPage = page;
                        _pendingSettingsFilter = filter;
                        host = null;
                        pendingPage = null;
                        pendingFilter = null;
                        _pendingSettingsCleanup = new System.Windows.Forms.Timer { Interval = 1600 };
                        _pendingSettingsCleanup.Tick += (_, __) =>
                        {
                            try { TryFindAndEmbed(); } catch { }
                            CleanupPendingSettingsLauncher();
                        };
                        _pendingSettingsCleanup.Start();
                        SendMessage(btn, BM_CLICK, nint.Zero, nint.Zero);
                    }
                    else Dbg.Warn("madVR settings: configuration button not found in property page.");
                }
                finally
                {
                    if (cauuid.pElems != nint.Zero) Marshal.FreeCoTaskMem(cauuid.pElems);
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("madVR settings: property page launch failed: " + ex.Message);
            }
            finally
            {
                if (pendingPage != null)
                {
                    try { pendingPage.Show(0); } catch { }
                    try { pendingPage.Deactivate(); } catch { }
                    Release(pendingPage);
                }
                Release(pendingFilter);
                if (host != null)
                {
                    try { Controls.Remove(host); } catch { }
                    try { host.Dispose(); } catch { }
                }
            }
        }

        private void CleanupPendingSettingsLauncher()
        {
            try { _pendingSettingsCleanup?.Stop(); _pendingSettingsCleanup?.Dispose(); } catch { }
            _pendingSettingsCleanup = null;
            if (_pendingSettingsPage != null)
            {
                try { _pendingSettingsPage.Show(0); } catch { }
                try { _pendingSettingsPage.Deactivate(); } catch { }
                Release(_pendingSettingsPage);
                _pendingSettingsPage = null;
            }
            Release(_pendingSettingsFilter);
            _pendingSettingsFilter = null;
            if (_pendingSettingsHost != null)
            {
                try { Controls.Remove(_pendingSettingsHost); } catch { }
                try { _pendingSettingsHost.Dispose(); } catch { }
                _pendingSettingsHost = null;
            }
        }

        private static void Release(object? o)
        {
            try
            {
                if (o == null) return;
                if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
            catch { }
        }

        private nint FindSettingsWindow()
        {
            nint found = nint.Zero;
            var controllerPids = new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName("madHcCtrl"))
            {
                try { controllerPids.Add((uint)process.Id); } catch { }
                finally { process.Dispose(); }
            }
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                var title = GetText(h).ToLowerInvariant();
                if (title.Length == 0) return true;

                bool looksLike = title.Contains("madvr") &&
                                 (title.Contains("setting") || title.Contains("impostaz"));
                // The vendor also uses the bare title "madVR" for its settings window.
                // Its tray/controller windows with that title are hidden, so visibility
                // plus the controller PID identifies the usable dialog.
                bool sameProc = (_ctrlPid != 0 && pid == _ctrlPid || controllerPids.Contains(pid)) &&
                                (title.Contains("setting") || title == "madvr");

                if (looksLike || sameProc)
                {
                    found = h;
                    return false;
                }
                return true;
            }, nint.Zero);

            return found;
        }

        private void ShowPlaceholder(string text)
        {
            _placeholderText.Text = text;
            _btnReopen.Visible = _started &&
                !text.Contains("non installato", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
            _placeholder.Visible = true;
            // Both panels fill the host; the placeholder must be in front of the empty
            // viewport or every status message (loading, missing madVR) is invisible.
            _placeholder.BringToFront();
        }

        public void CloseSettingsWindow()
        {
            try
            {
                Cleanup(true);
                nint w = FindSettingsWindow();
                if (w != nint.Zero && IsWindow(w))
                {
                    PostMessage(w, WM_CLOSE, nint.Zero, nint.Zero);
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cleanup(true);
                _tick.Dispose();
            }
            base.Dispose(disposing);
        }

        private void Cleanup(bool disposingControl)
        {
            _nativeTheme.Clear();
            try { _tick.Stop(); } catch { }
            CleanupPendingSettingsLauncher();

            try
            {
                if (_wnd != nint.Zero && IsWindow(_wnd))
                {
                    if (GetParent(_wnd) == _canvas.Handle)
                    {
                        PostMessage(_wnd, WM_CLOSE, nint.Zero, nint.Zero);
                    }

                    if (_savedOld)
                    {
                        try { SetParent(_wnd, _oldParent); } catch { }
                        try { SetWindowLong(_wnd, GWL_STYLE, _oldStyle); } catch { }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _wnd = nint.Zero;
                _embedded = false;
                _canvas.Visible = false;
                _savedOld = false;
                _oldParent = nint.Zero;
                _oldStyle = 0;
                _started = false;
                _lastKickUtc = DateTime.MinValue;
                _kickAttempts = 0;
            }
        }

        private sealed class DummySite : IPropertyPageSite
        {
            public int OnStatusChange(int dwFlags) => 0;
            public int GetLocaleID(out int pLocaleID) { pLocaleID = 0x0400; return 0; }
            public int GetPageContainer(out object ppUnk) { ppUnk = this; return 0; }
            public int TranslateAccelerator(ref MSG pMsg) => 1;
        }

        // P/Invoke
        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int SW_SHOW = 5;
        private const int BM_CLICK = 0x00F5;
        private const int WM_CLOSE = 0x0010;


        private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
        private delegate bool EnumChildProc(nint hWnd, nint lParam);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint hWnd, out RECT rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumChildWindows(nint hWndParent, EnumChildProc lpEnumFunc, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint hWndChild, nint hWndNewParent);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint GetParent(nint hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(nint hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(nint hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(nint hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(nint hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessage(nint hWnd, int Msg, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hWnd, int Msg, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnableWindow(nint hWnd, bool bEnable);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetFocus(nint hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(nint hWnd);

        private static string GetText(nint hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        private static string GetClass(nint hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        private static string? GetMadVrFolder()
        {
            static string? ReadRegDefault(RegistryView view)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                    using var key = baseKey.OpenSubKey(@"CLSID\{E1A8B82A-32CE-4B0D-BE0D-AA68C772E423}\InprocServer32");
                    return key?.GetValue(null) as string;
                }
                catch { return null; }
            }

            static IEnumerable<string> CandidateFolders()
            {
                foreach (var axPath in new[] { ReadRegDefault(RegistryView.Registry64), ReadRegDefault(RegistryView.Registry32) })
                {
                    if (string.IsNullOrWhiteSpace(axPath))
                        continue;

                    string cleaned = axPath.Trim().Trim('"');
                    string? dir = null;
                    try { dir = Path.GetDirectoryName(cleaned); } catch { }
                    if (!string.IsNullOrWhiteSpace(dir))
                        yield return dir;
                }

                string baseDir = AppContext.BaseDirectory;
                yield return Path.Combine(baseDir, "third-parties", "madVR09217");
                yield return Path.Combine(baseDir, "madVR09217");

                string? parent = null;
                try { parent = Directory.GetParent(baseDir)?.FullName; } catch { }

                while (!string.IsNullOrWhiteSpace(parent))
                {
                    yield return Path.Combine(parent, "third-parties", "madVR09217");
                    yield return Path.Combine(parent, "madVR09217");
                    try { parent = Directory.GetParent(parent)?.FullName; } catch { parent = null; }
                }
            }

            foreach (var folder in CandidateFolders().Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(folder))
                        continue;

                    bool hasCtrl = File.Exists(Path.Combine(folder, "madHcCtrl.exe"));
                    bool hasAx = File.Exists(Path.Combine(folder, Environment.Is64BitProcess ? "madVR64.ax" : "madVR.ax")) ||
                                 File.Exists(Path.Combine(folder, "madVR.ax")) ||
                                 File.Exists(Path.Combine(folder, "madVR64.ax"));
                    bool hasSettings = File.Exists(Path.Combine(folder, Environment.Is64BitProcess ? "mvrSettings64.dll" : "mvrSettings32.dll")) ||
                                       File.Exists(Path.Combine(folder, "mvrSettings64.dll")) ||
                                       File.Exists(Path.Combine(folder, "mvrSettings32.dll"));

                    if (hasCtrl && (hasAx || hasSettings))
                        return folder;
                }
                catch { }
            }

            return null;
        }
    }

    // ===================== CONTESTO VIDEO SETTINGS / ENUM =====================
    public enum MadVrHdrMode { Auto = 0, PassthroughHdr, ToneMapHdrToSdr, LutHdrToSdr }
    public enum MadVrCategoryPreset { RendererDefault = 0, Profile1, Profile2, Profile3, Profile4, Profile5, Profile6 }
    public enum MadVrFpsChoice { Adapt = 0, Force60 = 60, Force24 = 24 }

    public sealed class VideoSettings
    {
        public int TargetFps { get; set; }
        public bool AllowUpscaling { get; set; }
        public bool PreferBitstream { get; set; }
        public global::CinecorePlayer2025.Utilities.VideoRendererChoice? PreferredRenderer { get; set; }
        public global::CinecorePlayer2025.Utilities.MpvRuntimeChoice MpvRuntime { get; set; } =
            global::CinecorePlayer2025.Utilities.MpvRuntimeChoice.X64;
        public global::CinecorePlayer2025.Utilities.MpvPlaybackSettings MpvSettings { get; set; } = new();
        public string Language { get; set; } = global::CinecorePlayer2025.Utilities.AppLanguage.SystemDefault;
        public bool NetflixModeEnabled { get; set; }

        public MadVrHdrMode HdrMode { get; set; } = MadVrHdrMode.Auto;
        public MadVrCategoryPreset ChromaPreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset ImageUpscalePreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset ImageDownscalePreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset RefinementPreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrFpsChoice FpsChoice { get; set; } = MadVrFpsChoice.Adapt;
    }

    // ===================== UI KIT (bottoni stile screenshot) =====================
    internal static class UiKit
    {
        // bottone stile outline rettangolare 1px chiaro (sidebar e footer)
        public static Button MakeOutlineButton(string text, bool leftAlign = false, bool useNavBg = false)
        {
            var baseBack = useNavBg ? Theme.Nav : Theme.Card;
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = baseBack,
                ForeColor = Theme.Text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10.0f, FontStyle.Regular),
                Height = 42,
                Dock = DockStyle.Top,
                TextAlign = leftAlign ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter,
                Padding = leftAlign ? new Padding(18, 0, 12, 0) : new Padding(10, 0, 10, 0),
                Margin = new Padding(0, 0, 0, 9),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(34, 54, 76);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(12, 36, 60);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(7, 24, 42);
            return b;
        }

        // label titolo gruppo
        public static Label MakeGroupHeader(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Theme.Text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };
        }

        // label descrizione gruppo
        public static Label MakeGroupSub(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Theme.Muted,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 8.5f, FontStyle.Regular),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
                MaximumSize = new Size(1000, 0)
            };
        }

        public static RadioButton MakeRadio(string txt)
        {
            return new RadioButton
            {
                Text = txt,
                AutoSize = true,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 2)
            };
        }

        public static CheckBox MakeCheck(string txt)
        {
            return new CheckBox
            {
                Text = txt,
                AutoSize = true,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 2)
            };
        }

        public static ChoiceField MakePresetCombo()
        {
            var cb = new ChoiceField
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.System,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                MinimumSize = new Size(200, 0),
                Margin = new Padding(0, 2, 0, 8)
            };
            cb.Items.Add("Default (renderer)");
            cb.Items.Add("Profile 1");
            cb.Items.Add("Profile 2");
            cb.Items.Add("Profile 3");
            cb.Items.Add("Profile 4");
            cb.Items.Add("Profile 5");
            cb.Items.Add("Profile 6");
            cb.SelectedIndex = 0;
            return cb;
        }

        public static MadVrCategoryPreset ComboToPreset(ChoiceField cb)
        {
            return cb.SelectedIndex switch
            {
                0 => MadVrCategoryPreset.RendererDefault,
                1 => MadVrCategoryPreset.Profile1,
                2 => MadVrCategoryPreset.Profile2,
                3 => MadVrCategoryPreset.Profile3,
                4 => MadVrCategoryPreset.Profile4,
                5 => MadVrCategoryPreset.Profile5,
                6 => MadVrCategoryPreset.Profile6,
                _ => MadVrCategoryPreset.RendererDefault
            };
        }
    }

    // ===================== SWITCH FREQUENZA MONITOR =====================
    internal sealed class DisplayModeSwitcher
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, nint hwnd, int dwflags, nint lParam);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int EDS_RAWMODE = 0x00000002;
        private const int CDS_FULLSCREEN = 0x00000004;
        private string? _device;
        private DEVMODE? _original;

        public bool SwitchToNearest(Screen screen, double desiredFps)
        {
            try
            {
                _device = screen.DeviceName;
                var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettingsEx(_device, ENUM_CURRENT_SETTINGS, ref cur, 0))
                    return false;
                _original = cur;

                var best = cur;
                double target = desiredFps <= 0 ? 60d : desiredFps;

                // DEVMODE reports fractional refresh families as integers: the Windows/NVIDIA
                // "23 Hz" mode normally represents 23.976 Hz, while "24 Hz" represents
                // true 24.000 Hz. Do NOT score 23 and 24 as equivalent: that made the result
                // depend on enumeration order and could select 24.000 for a 23.976 fps movie.
                int primaryHz = target < 23.99 ? 23 : (int)Math.Round(target);
                int alternateHz = primaryHz == 23 ? 24 : (primaryHz == 24 ? 23 : primaryHz);
                int bestHz = cur.dmDisplayFrequency;
                double bestScore = double.MaxValue;

                for (int i = 0; ; i++)
                {
                    var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    if (!EnumDisplaySettingsEx(_device, i, ref mode, EDS_RAWMODE))
                        break;

                    if (mode.dmPelsWidth != cur.dmPelsWidth || mode.dmPelsHeight != cur.dmPelsHeight)
                        continue;

                    int hz = mode.dmDisplayFrequency;
                    if (hz <= 0)
                        continue;

                    // Exact primary wins. The 23<->24 alternate is only a fallback, never a tie.
                    double score = hz == primaryHz
                        ? 0.0
                        : hz == alternateHz
                            ? 100.0
                            : 1000.0 + Math.Abs(hz - primaryHz);

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = mode;
                        bestHz = hz;
                    }
                }

                if (bestHz == cur.dmDisplayFrequency) return true;
                int r = ChangeDisplaySettingsEx(_device, ref best, nint.Zero,
                    CDS_FULLSCREEN, nint.Zero);
                return r == 0;
            }
            catch { return false; }
        }

        public void RestoreIfChanged()
        {
            try
            {
                if (_device == null || _original == null) return;
                var orig = _original.Value;
                ChangeDisplaySettingsEx(_device, ref orig, nint.Zero,
                    CDS_FULLSCREEN, nint.Zero);
            }
            catch { }
            finally
            {
                _original = null;
                _device = null;
            }
        }
    }

    // ===================== WIN32 helper =====================
    internal static class Win32
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            nint hWnd,
            nint hWndInsertAfter,
            int X, int Y, int cx, int cy,
            uint uFlags);

        public static readonly nint HWND_TOPMOST = new nint(-1);
        public static readonly nint HWND_NOTOPMOST = new nint(-2);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
    }
}
