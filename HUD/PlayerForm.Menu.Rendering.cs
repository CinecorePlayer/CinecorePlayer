#nullable enable
using CinecorePlayer2025.Utilities;
using CinecorePlayer2025.HUD;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private static readonly ToolStripRenderer _darkMenuRenderer = new DarkMenuRenderer();
        internal static ToolStripRenderer CinecoreDarkMenuRenderer => _darkMenuRenderer;

        // The menu was laid out in fixed pixels while its text follows the display scale: at
        // 175% the rows stayed 30 pixels tall around much larger text. Every measure now
        // follows the scale of the screen the player is on.
        private static int MS(int value) => (int)Math.Round(value * MenuScale);

        // Scale of the menu: the display scale, or the size of the player window when that is
        // larger (the rest of the interface grows with the window: on a 4K screen a menu sized
        // only by the display scale looked tiny next to it).
        private static float MenuScale
        {
            get
            {
                try
                {
                    foreach (Form open in Application.OpenForms)
                        if (open is PlayerForm player)
                            return Math.Max(1f, Math.Max(player.DeviceDpi / 96f, player.ClientSize.Height / 1080f * 1.12f));
                }
                catch { }
                return 1f;
            }
        }

        private static Font MenuFont() => global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.8f * (96f / 72f) * MenuScale, FontStyle.Regular, GraphicsUnit.Pixel);

        private static void ApplyDarkMenuTheme(ContextMenuStrip? menu)
        {
            if (menu == null) return;
            try
            {
                menu.RenderMode = ToolStripRenderMode.Professional;
                menu.Renderer = _darkMenuRenderer;
                menu.BackColor = Color.FromArgb(4, 11, 18);
                menu.ForeColor = Color.FromArgb(232, 239, 245);
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = false;
                menu.DropShadowEnabled = false;
                menu.CanOverflow = false;
                menu.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
                menu.Padding = new Padding(0, 2, 0, 2);
                menu.MinimumSize = new Size(MS(210), 0);
                try { menu.Font = MenuFont(); } catch { }
                menu.AutoSize = true;
                menu.ItemAdded -= DarkMenu_ItemAdded;
                menu.ItemAdded += DarkMenu_ItemAdded;
                menu.SizeChanged -= DarkMenu_SizeChanged;
                menu.SizeChanged += DarkMenu_SizeChanged;
                menu.Opened -= DarkMenu_Opened;
                menu.Opened += DarkMenu_Opened;
                ClampDarkMenuRegion(menu);
            }
            catch { }
        }

        private static void ApplyDarkMenuTheme(ToolStripDropDownMenu? menu)
        {
            if (menu == null) return;
            try
            {
                menu.RenderMode = ToolStripRenderMode.Professional;
                menu.Renderer = _darkMenuRenderer;
                menu.BackColor = Color.FromArgb(4, 11, 18);
                menu.ForeColor = Color.FromArgb(232, 239, 245);
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = false;
                menu.DropShadowEnabled = false;
                menu.CanOverflow = false;
                menu.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
                menu.Padding = new Padding(0, 2, 0, 2);
                menu.MinimumSize = new Size(MS(210), 0);
                try { menu.Font = MenuFont(); } catch { }
                menu.AutoSize = true;
                menu.ItemAdded -= DarkMenu_ItemAdded;
                menu.ItemAdded += DarkMenu_ItemAdded;
                menu.SizeChanged -= DarkMenu_SizeChanged;
                menu.SizeChanged += DarkMenu_SizeChanged;
                menu.Opened -= DarkMenu_Opened;
                menu.Opened += DarkMenu_Opened;
                ClampDarkMenuRegion(menu);
            }
            catch { }
        }

        private static void DarkMenu_Opened(object? sender, EventArgs e)
        {
            if (sender is ToolStrip strip)
            {
                ClampDarkMenuRegion(strip);
                PromoteToolStripWindow(strip);
            }
        }

        private static void DarkMenu_SizeChanged(object? sender, EventArgs e)
        {
            if (sender is ToolStrip strip)
                ClampDarkMenuRegion(strip);
        }

        private static void ClampDarkMenuRegion(ToolStrip strip)
        {
            if (strip is ToolStripDropDown)
            {
                // Top-level popup: anti-aliased system corners instead of a jagged Region.
                WindowCorners.Apply(strip, 8, border: Theme.Border);
                if (WindowCorners.SystemRounded(strip)) return;
            }
            try
            {
                var old = strip.Region;
                strip.Region = null;
                old?.Dispose();

                if (strip.Width < 4 || strip.Height < 4)
                    return;

                using var path = BuildDarkMenuRoundRect(new Rectangle(0, 0, strip.Width, strip.Height), 8);
                strip.Region = new Region(path);
            }
            catch { }
        }

        private static GraphicsPath BuildDarkMenuRoundRect(Rectangle r, int radius)
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

        private static void DarkMenu_ItemAdded(object? sender, ToolStripItemEventArgs e)
        {
            try
            {
                if (e.Item == null)
                    return;
                StyleDarkMenuItem(e.Item);
                if (e.Item is ToolStripMenuItem mi && mi.DropDown is ToolStripDropDownMenu dd)
                    ApplyDarkMenuTheme(dd);
            }
            catch { }
        }

        private static void StyleDarkMenuItem(ToolStripItem item)
        {
            try
            {
                item.MouseEnter -= MenuItem_MouseEnter;
                item.MouseEnter += MenuItem_MouseEnter;
                item.Padding = item is ToolStripSeparator ? Padding.Empty : new Padding(MS(6), MS(2), MS(4), MS(2));
                item.Margin = item is ToolStripSeparator ? new Padding(8, 2, 8, 2) : Padding.Empty;
                if (item is ToolStripMenuItem menuItem)
                {
                    menuItem.DisplayStyle = ToolStripItemDisplayStyle.Text;
                    menuItem.AutoSize = true;
                    menuItem.Height = Math.Max(menuItem.Height, MS(30));
                    menuItem.DropDownDirection = ToolStripDropDownDirection.Right;
                    menuItem.DropDownOpening -= DarkMenuItem_DropDownOpening;
                    menuItem.DropDownOpening += DarkMenuItem_DropDownOpening;
                    menuItem.DropDownOpened -= DarkMenuItem_DropDownOpened;
                    menuItem.DropDownOpened += DarkMenuItem_DropDownOpened;
                    if (menuItem.DropDown is ToolStripDropDownMenu dd)
                    {
                        dd.Margin = Padding.Empty;
                        dd.Padding = new Padding(0, 2, 0, 2);
                        dd.DropShadowEnabled = false;
                        dd.CanOverflow = false;
                        dd.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
                        dd.MinimumSize = new Size(MS(210), 0);
                    }
                }
            }
            catch { }
        }

        private static void DarkMenuItem_DropDownOpening(object? sender, EventArgs e)
        {
            try
            {
                if (sender is ToolStripMenuItem item)
                    PrepareDarkSubMenu(item);
            }
            catch { }
        }

        private static void DarkMenuItem_DropDownOpened(object? sender, EventArgs e)
        {
            try
            {
                if (sender is not ToolStripMenuItem item)
                    return;

                PrepareDarkSubMenu(item);
                if (item.DropDown is ToolStrip strip)
                {
                    PlaceDarkSubMenu(item);
                    ClampDarkMenuRegion(strip);
                    PromoteToolStripWindow(strip);
                }
            }
            catch { }
        }

        private static void PlaceDarkSubMenu(ToolStripMenuItem item)
        {
            try
            {
                if (item.Owner == null || item.DropDown == null)
                    return;

                PrepareDarkSubMenu(item);
                Rectangle b = item.Bounds;
                int dropW = Math.Max(1, item.DropDown.Width);
                int dropH = Math.Max(1, item.DropDown.Height);
                const int gap = 4;
                Point itemScreen = item.Owner.PointToScreen(b.Location);
                Rectangle itemScreenBounds = new Rectangle(itemScreen, b.Size);
                Rectangle work = Screen.FromRectangle(itemScreenBounds).WorkingArea;

                int x = itemScreenBounds.Right + gap;
                if (x + dropW > work.Right - 2)
                {
                    x = Math.Max(work.Left + 2, itemScreenBounds.Left - dropW - gap);
                    item.DropDownDirection = ToolStripDropDownDirection.Left;
                }
                else
                {
                    item.DropDownDirection = ToolStripDropDownDirection.Right;
                }

                int y = Math.Max(work.Top + 2, itemScreenBounds.Top - 2);
                if (dropH > 0 && y + dropH > work.Bottom - 2)
                    y = Math.Max(work.Top + 2, Math.Min(itemScreenBounds.Top - 2, work.Bottom - dropH - 2));
                item.DropDown.Location = new Point(x, y);
            }
            catch { }
        }

        private static void PrepareDarkSubMenu(ToolStripMenuItem item)
        {
            try
            {
                if (item.DropDown == null)
                    return;

                if (item.DropDown is ToolStripDropDownMenu dd)
                    PrepareDarkDropDown(dd, MS(210), MS(380));
            }
            catch { }
        }

        private static void PrepareDarkDropDown(ToolStripDropDownMenu dd, int minWidth, int maxWidth)
        {
            try
            {
                dd.Margin = Padding.Empty;
                dd.Padding = new Padding(0, 2, 0, 2);
                dd.DropShadowEnabled = false;
                dd.CanOverflow = false;
                dd.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
                dd.AutoSize = false;
                dd.AutoScrollMinSize = Size.Empty;
                dd.MaximumSize = Size.Empty;
                try { dd.OverflowButton.Visible = false; dd.OverflowButton.Enabled = false; } catch { }

                int dropW = MeasureDarkDropDownWidth(dd, minWidth, maxWidth);
                int itemW = Math.Max(1, dropW - dd.Padding.Horizontal);
                int dropH = dd.Padding.Vertical;
                foreach (ToolStripItem child in dd.Items)
                {
                    child.Margin = Padding.Empty;
                    child.AutoSize = false;
                    int childH = child is ToolStripSeparator ? MS(8) : MS(31);
                    child.Size = new Size(itemW, childH);
                    if (child.Available)
                        dropH += childH;
                }

                dd.MinimumSize = new Size(minWidth, 0);
                dd.Size = new Size(Math.Max(minWidth, dropW), Math.Max(1, dropH));
                ClampDarkMenuRegion(dd);
            }
            catch { }
        }

        private static int MeasureDarkDropDownWidth(ToolStripDropDownMenu dd, int minWidth, int maxWidth)
        {
            try
            {
                int width = minWidth;
                using var font = MenuFont();
                foreach (ToolStripItem child in dd.Items)
                {
                    if (child is ToolStripSeparator || !child.Available)
                        continue;

                    string text = (child.Text ?? string.Empty).Replace("&", string.Empty);
                    int textW = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
                    bool hasDropDown = child is ToolStripMenuItem mi && mi.HasDropDownItems;
                    width = Math.Max(width, textW + MS(58) + (hasDropDown ? MS(26) : MS(10)));
                }
                return Math.Min(maxWidth, Math.Max(minWidth, width));
            }
            catch { return minWidth; }
        }

        private static void ApplyDarkMenuThemeRecursive(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                StyleDarkMenuItem(it);

                if (it is ToolStripMenuItem mi)
                {
                    try
                    {
                        if (mi.DropDown is ToolStripDropDownMenu dd)
                            ApplyDarkMenuTheme(dd);
                    }
                    catch { }

                    try
                    {
                        if (mi.HasDropDownItems)
                            ApplyDarkMenuThemeRecursive(mi.DropDownItems);
                    }
                    catch { }
                }
            }
        }

        private sealed class DarkMenuColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Color.FromArgb(4, 11, 18);

            public override Color MenuItemSelected => Color.FromArgb(52, Theme.Selection);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(52, Theme.Selection);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(38, Theme.AccentSoft);

            public override Color MenuItemBorder => Color.Transparent;
            public override Color SeparatorDark => Color.Transparent;
            public override Color SeparatorLight => Color.Transparent;

            public override Color ImageMarginGradientBegin => Color.FromArgb(4, 11, 18);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(4, 11, 18);
            public override Color ImageMarginGradientEnd => Color.FromArgb(4, 11, 18);
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            private static readonly Dictionary<(string Path, int Size, int Argb), Bitmap> SvgMenuIconCache = new();

            public DarkMenuRenderer() : base(new DarkMenuColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                try
                {
                    var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
                    // Con gli angoli di sistema (Windows 11) la finestra è già arrotondata con
                    // antialias: dipingere un secondo tracciato arrotondato lasciava spicchi neri
                    // fra le due curve. In quel caso si riempie tutto il rettangolo.
                    if (WindowCorners.SystemRounded(e.ToolStrip))
                    {
                        using var solid = new SolidBrush(Color.FromArgb(255, 5, 13, 22));
                        e.Graphics.FillRectangle(solid, r);
                        return;
                    }
                    r.Width -= 1;
                    r.Height -= 1;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var path = RoundRect(r, 8);
                    using var fill = new SolidBrush(Color.FromArgb(252, 5, 13, 22));
                    e.Graphics.FillPath(fill, path);
                }
                catch { base.OnRenderToolStripBackground(e); }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                try
                {
                    // Il bordo arrotondato lo disegna il sistema (colore impostato da WindowCorners).
                    if (WindowCorners.SystemRounded(e.ToolStrip)) return;
                    var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
                    r.Width -= 1;
                    r.Height -= 1;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var path = RoundRect(r, 8);
                    using var p = new Pen(Color.FromArgb(104, Theme.BorderAccent));
                    e.Graphics.DrawPath(p, path);
                }
                catch { base.OnRenderToolStripBorder(e); }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                try
                {
                    var r = new Rectangle(0, 0, Math.Max(1, e.Item.Width), Math.Max(1, e.Item.Height));
                    var mi = e.Item as ToolStripMenuItem;

                    bool selected = e.Item.Selected;
                    bool checkedItem = mi?.Checked == true;

                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    if (selected || checkedItem)
                    {
                        Theme.DrawHighlight(e.Graphics, Rectangle.Inflate(r, -MS(4), -MS(2)), selected: checkedItem);
                        if (selected && checkedItem)
                            Theme.DrawHighlight(e.Graphics, Rectangle.Inflate(r, -MS(4), -MS(2)), selected: false);
                    }
                }
                catch
                {
                    base.OnRenderMenuItemBackground(e);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                try
                {
                    if (e.Item is not ToolStripMenuItem item)
                    {
                        base.OnRenderItemText(e);
                        return;
                    }

                    e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    bool selected = item.Selected;
                    bool enabled = item.Enabled;
                    Color text = enabled
                        ? (selected ? Color.White : Color.FromArgb(236, 243, 249))
                        : Color.FromArgb(116, 129, 143);
                    Color icon = enabled
                        ? (selected ? Theme.Accent : Color.FromArgb(170, 190, 210))
                        : Color.FromArgb(84, 96, 110);

                    Rectangle bounds = new Rectangle(Point.Empty, e.Item.Size);
                    string itemText = item.Text ?? string.Empty;
                    Rectangle checkRect = new Rectangle(bounds.Left + MS(6), bounds.Top + (bounds.Height - MS(14)) / 2, MS(14), MS(14));
                    Rectangle iconRect = new Rectangle(bounds.Left + MS(24), bounds.Top + (bounds.Height - MS(15)) / 2, MS(15), MS(15));
                    if (item.Checked)
                        DrawMenuCheck(e.Graphics, checkRect, enabled ? Theme.Accent : Color.FromArgb(84, 96, 110));
                    DrawMenuIcon(e.Graphics, iconRect, MenuIconKey(item), icon);

                    using var textFont = MenuFont();
                    int rightReserve = item.HasDropDownItems ? MS(22) : MS(8);
                    Rectangle textRect = new Rectangle(bounds.Left + MS(44), bounds.Top, Math.Max(1, bounds.Width - MS(44) - rightReserve), bounds.Height);
                    TextRenderer.DrawText(e.Graphics, StripShortcutMarkers(itemText), textFont, textRect, text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    if (item.HasDropDownItems)
                    {
                        Rectangle arrow = new Rectangle(bounds.Right - MS(18), bounds.Top + (bounds.Height - MS(12)) / 2, MS(12), MS(12));
                        DrawMenuArrow(e.Graphics, arrow, enabled ? Color.FromArgb(204, 232, 246) : Color.FromArgb(118, 132, 146));
                    }
                }
                catch
                {
                    base.OnRenderItemText(e);
                }
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                try
                {
                    var r = e.ImageRectangle;
                    if (AssetIconService.DrawCustom(e.Graphics, r, "check", Theme.Accent)) return;
                    r.Inflate(-2, -2);
                    using var p = new Pen(Theme.Accent, 2f);
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    int y = r.Top + r.Height / 2;
                    e.Graphics.DrawLines(p, new[]
                    {
                        new Point(r.Left + 2, y),
                        new Point(r.Left + r.Width / 2 - 1, r.Bottom - 3),
                        new Point(r.Right - 2, r.Top + 3)
                    });
                }
                catch { base.OnRenderItemCheck(e); }
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                // Freccia nativa disabilitata: il submenu usa solo il triangolino custom
                // disegnato in OnRenderItemText, evitando l'icona tipo skip.
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                try
                {
                    int y = e.Item.Height / 2;
                    using var p = new Pen(Color.FromArgb(32, 150, 170, 190));
                    e.Graphics.DrawLine(p, 12, y, Math.Max(12, e.Item.Width - 12), y);
                }
                catch { }
            }

            private static string StripShortcutMarkers(string text)
                => (text ?? string.Empty).Replace("&", string.Empty);

            internal static string MenuIconKey(string text)
            {
                string value = StripShortcutMarkers(text).ToLowerInvariant();
                if (value.Contains("pip") || value.Contains("picture-in-picture")) return "pip";
                if (value.Contains("svuota") || value.Contains("clear queue") || value.Contains("rimuovi") || value.Contains("remove")) return "trash";
                if (value.Contains("impostazioni") || value.Contains("settings") || value.Contains("configura") || value.Contains("configure")) return "settings";
                if (value.Contains("sottotit") || value.Contains("subtitle")) return "subtitles";
                if (value.Contains("testi") || value.Contains("lyrics")) return "lyrics";
                if (value.Contains("grafici") || value.Contains("analysis")) return "spectrum";
                if (value.Contains("ferma") || value.Contains("stop") || value.Contains("interrompi")) return "stop";
                if (value.Contains("disattiva") || value.Contains("disable") || value.Contains("nessun") || value.Contains(" off")) return "power";
                if (value.Contains("amplificatore") || value.Contains("amplifier")) return "amplifier";
                if (value.Contains("traccia audio") || value.Contains("audio track")) return "audio";
                if (value.Contains("uscita audio") || value.Contains("audio output")) return "amplifier";
                if (value.Contains("bitstream") || value.Contains("passthrough audio")) return "passthrough";
                if (value.Contains("pcm")) return "wave";
                if (value.Contains("ritardo") || value.Contains("delay") || value.Contains("sync")) return "sync";
                if (value.Contains("velocit") || value.Contains("speed")) return "speed";
                if (value.Contains("bilanciamento") || value.Contains("balance")) return "balance";
                if (value.Contains("volume")) return "volume";
                if (value.Contains("disattiva") || value.Contains("disable") || value.Contains("nessun") || value.Contains(" off")) return "power";
                if (value.Contains("tone-map")) return "sun";
                if (value.Contains("3dlut")) return "palette";
                if (value.Contains("upscal") || value.Contains("rtx")) return "upscale";
                if (value.StartsWith("apri file") || value.StartsWith("open file")) return "folder";
                if (value.StartsWith("apri libreria") || value.StartsWith("open library") || value == "libreria" || value == "library") return "library";
                if (value.Contains("spotlight")) return "spotlight";
                if (value.Contains("riprendi") || value.Contains("resume")) return "play";
                if (value.Contains("pausa") || value.Contains("pause")) return "pause";
                if (value.Contains("chiudi file") || value.Contains("close file")) return "close";
                if (value.Contains("loop") || value.Contains("ripeti") || value.Contains("repeat")) return "repeat";
                if (value.Contains("coda") || value.Contains("queue")) return "queue";
                if (value == "video") return "video";
                if (value.Contains("audio")) return "audio";
                if (value.Contains("sottotit") || value.Contains("subtitle")) return "subtitles";
                if (value.Contains("capitol") || value.Contains("chapter")) return "chapters";
                if (value.Contains("immagine") || value.Contains("picture") || value.Contains("hdr")) return "image";
                if (value == "3d" || value.Contains("sbs") || value.Contains("tab →")) return "cube";
                if (value.Contains("youtube") || value.Contains("risoluzione web") || value.Contains("web resolution")) return "youtube";
                if (value.Contains("limita a") || value.Contains("limit to") || Regex.IsMatch(value, @"\b(8k|4k|4320p|2160p|1440p|1080p|720p|480p|360p|240p|144p)\b")) return "resolution";
                if (value.Contains("upscal")) return "renderer";
                if (value.Contains("renderer") || value.Contains("madvr") || value.Contains("mpcvr") || value.Contains("evr") || value.Contains("mpv")) return "renderer";
                if (value.Contains("predefinit") || value.Contains("default") || value == "auto" || value.StartsWith("auto (")) return "check";
                if (value.Contains("passthrough") || value.Contains("tone-map") || value.Contains("3dlut")) return "image";
                if (value.Contains("nativo") || value.Contains("native") || value.Contains("sbs") || value.Contains("tab")) return "cube";
                if (value.Contains("disattiva") || value.Contains("disable") || value.Contains("nessun sottotitolo") || value.Contains("subtitles off")) return "subtitles";
                if (value.Contains("lingua") || value.Contains("language")) return "language";
                if (value.Contains("wled") || value.Contains("led")) return "light";
                if (value.Contains("configura") || value.Contains("configure")) return "settings";
                if (value.Contains("placeholder")) return "image";
                if (value.Contains("demo")) return "play";
                if (value.StartsWith("apri cartella") || value.StartsWith("open folder")) return "folder";
                if (value.StartsWith("abilita") || value.StartsWith("enable")) return "check";
                if (value.Contains("schermo intero") || value.Contains("fullscreen")) return "maximize";
                if (value.Contains("pip") || value.Contains("picture-in-picture")) return "pip";
                if (value.Contains("cinema")) return "cinema";
                if (value.Contains("info")) return "info";
                if (value.Contains("extra")) return "plus";
                if (value.Contains("telecomando") || value.Contains("remote")) return "remote";
                if (value.Contains("impostazioni") || value.Contains("settings")) return "settings";
                if (value.Contains("esci") || value.Contains("exit")) return "exit";
                return "dot";
            }

            internal static string MenuIconKey(ToolStripItem item)
            {
                if (item?.Tag is string tag && tag.StartsWith("menu-icon:", StringComparison.OrdinalIgnoreCase))
                {
                    string explicitKey = tag.Substring("menu-icon:".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(explicitKey))
                        return explicitKey;
                }

                string own = MenuIconKey(item?.Text ?? string.Empty);
                if (!string.Equals(own, "dot", StringComparison.Ordinal))
                    return own;

                if (item is ToolStripMenuItem checkedItem && checkedItem.Checked)
                    return "check";

                try
                {
                    if (item?.Owner is ToolStripDropDown dropDown && dropDown.OwnerItem is ToolStripMenuItem parent)
                    {
                        string inherited = MenuIconKey(parent.Text ?? string.Empty);
                        if (!string.Equals(inherited, "dot", StringComparison.Ordinal))
                            return inherited;
                    }
                }
                catch { }

                return "list";
            }

            internal static void DrawMenuCheck(Graphics g, Rectangle r, Color color)
            {
                if (AssetIconService.DrawCustom(g, r, "check", color)) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var bg = new SolidBrush(Color.FromArgb(34, color));
                using var outline = new Pen(Color.FromArgb(160, color), 1.2f);
                g.FillEllipse(bg, r);
                g.DrawEllipse(outline, r);
                using var p = new Pen(color, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(p, new[]
                {
                    new Point(r.Left + 4, r.Top + r.Height / 2),
                    new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4),
                    new Point(r.Right - 3, r.Top + 4)
                });
            }

            internal static void DrawMenuArrow(Graphics g, Rectangle r, Color color)
            {
                if (AssetIconService.DrawCustom(g, r, "chevron-right", color)) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int midY = r.Top + r.Height / 2;
                int left = r.Left + Math.Max(3, r.Width / 3);
                int right = r.Right - 3;
                using var pen = new Pen(color, 1.8f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                g.DrawLines(pen, new[]
                {
                    new Point(left, midY - 5),
                    new Point(right, midY),
                    new Point(left, midY + 5)
                });
            }

            internal static void DrawMenuIcon(Graphics g, Rectangle r, string key, Color color)
            {
                if (TryDrawSvgMenuIcon(g, r, key, color))
                    return;

                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(color, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                using var brush = new SolidBrush(color);
                using var soft = new SolidBrush(Color.FromArgb(35, color));

                if (key == "netflix")
                {
                    using var red = new SolidBrush(Color.FromArgb(230, 20, 28));
                    Rectangle n = Rectangle.Inflate(r, -5, -2);
                    g.FillRectangle(red, new Rectangle(n.Left, n.Top, 5, n.Height));
                    g.FillRectangle(red, new Rectangle(n.Right - 5, n.Top, 5, n.Height));
                    Point[] diag =
                    {
                        new Point(n.Left + 4, n.Top),
                        new Point(n.Left + 10, n.Top),
                        new Point(n.Right - 4, n.Bottom),
                        new Point(n.Right - 10, n.Bottom)
                    };
                    g.FillPolygon(red, diag);
                    return;
                }

                if (key == "folder" || key == "folder-plus")
                {
                    Rectangle box = new Rectangle(r.Left + 1, r.Top + 7, r.Width - 2, r.Height - 10);
                    g.DrawLines(pen, new[]
                    {
                        new Point(box.Left, box.Top + 4),
                        new Point(box.Left, box.Bottom),
                        new Point(box.Right, box.Bottom),
                        new Point(box.Right, box.Top + 2),
                        new Point(box.Left + 12, box.Top + 2),
                        new Point(box.Left + 8, box.Top - 3),
                        new Point(box.Left, box.Top - 3),
                        new Point(box.Left, box.Top + 4)
                    });
                    if (key == "folder-plus")
                    {
                        int cx = r.Right - 4;
                        int cy = r.Bottom - 7;
                        g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                        g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
                    }
                    return;
                }

                if (key == "pause")
                {
                    g.FillRectangle(brush, new Rectangle(r.Left + 6, r.Top + 5, 5, r.Height - 10));
                    g.FillRectangle(brush, new Rectangle(r.Right - 11, r.Top + 5, 5, r.Height - 10));
                    return;
                }
                if (key == "stop")
                {
                    g.FillRectangle(brush, Rectangle.Inflate(r, -6, -6));
                    return;
                }
                if (key == "queue")
                {
                    g.DrawArc(pen, new Rectangle(r.Left + 3, r.Top + 4, r.Width - 8, r.Height - 8), 35, 250);
                    g.DrawLines(pen, new[] { new Point(r.Left + 4, r.Top + 14), new Point(r.Left + 1, r.Top + 22), new Point(r.Left + 10, r.Top + 21) });
                    return;
                }
                if (key == "monitor")
                {
                    Rectangle screen = new Rectangle(r.Left + 3, r.Top + 5, r.Width - 6, r.Height - 10);
                    g.DrawRectangle(pen, screen);
                    g.DrawLine(pen, r.Left + r.Width / 2, screen.Bottom, r.Left + r.Width / 2, r.Bottom - 2);
                    g.DrawLine(pen, r.Left + 8, r.Bottom - 2, r.Right - 8, r.Bottom - 2);
                    return;
                }
                if (key == "pip")
                {
                    g.DrawRectangle(pen, new Rectangle(r.Left + 2, r.Top + 5, r.Width - 7, r.Height - 10));
                    g.FillRectangle(brush, new Rectangle(r.Right - 12, r.Bottom - 12, 9, 7));
                    return;
                }
                if (key == "cinema")
                {
                    Rectangle clapper = new Rectangle(r.Left + 4, r.Top + 9, r.Width - 8, r.Height - 12);
                    g.FillRectangle(soft, clapper);
                    g.DrawRectangle(pen, clapper);
                    g.DrawLine(pen, clapper.Left + 2, clapper.Top + 7, clapper.Right - 2, clapper.Top + 7);
                    for (int i = 0; i < 4; i++)
                        g.DrawLine(pen, clapper.Left + 4 + i * 6, clapper.Top + 1, clapper.Left + 9 + i * 6, clapper.Top + 7);
                    return;
                }
                if (key == "info")
                {
                    g.DrawEllipse(pen, Rectangle.Inflate(r, -4, -4));
                    g.FillEllipse(brush, new Rectangle(r.Left + r.Width / 2 - 1, r.Top + 7, 3, 3));
                    g.DrawLine(pen, r.Left + r.Width / 2, r.Top + 13, r.Left + r.Width / 2, r.Bottom - 7);
                    return;
                }
                if (key == "plus")
                {
                    g.DrawLine(pen, r.Left + r.Width / 2, r.Top + 4, r.Left + r.Width / 2, r.Bottom - 4);
                    g.DrawLine(pen, r.Left + 4, r.Top + r.Height / 2, r.Right - 4, r.Top + r.Height / 2);
                    return;
                }
                if (key == "remote")
                {
                    Rectangle body = new Rectangle(r.Left + 8, r.Top + 3, r.Width - 16, r.Height - 6);
                    g.DrawRectangle(pen, body);
                    g.FillEllipse(brush, new Rectangle(body.Left + 5, body.Top + 5, 4, 4));
                    g.DrawLine(pen, body.Left + 5, body.Top + 14, body.Right - 5, body.Top + 14);
                    g.DrawLine(pen, body.Left + 5, body.Top + 20, body.Right - 5, body.Top + 20);
                    return;
                }
                if (key == "settings")
                {
                    g.DrawEllipse(pen, Rectangle.Inflate(r, -6, -6));
                    g.DrawEllipse(pen, Rectangle.Inflate(r, -11, -11));
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI * i / 4;
                        int cx = r.Left + r.Width / 2;
                        int cy = r.Top + r.Height / 2;
                        g.DrawLine(pen, cx + (int)(Math.Cos(a) * 10), cy + (int)(Math.Sin(a) * 10), cx + (int)(Math.Cos(a) * 13), cy + (int)(Math.Sin(a) * 13));
                    }
                    return;
                }
                if (key == "exit")
                {
                    g.DrawRectangle(pen, new Rectangle(r.Left + 4, r.Top + 5, r.Width - 13, r.Height - 10));
                    g.DrawLine(pen, r.Left + 13, r.Top + r.Height / 2, r.Right - 2, r.Top + r.Height / 2);
                    g.DrawLines(pen, new[] { new Point(r.Right - 8, r.Top + r.Height / 2 - 6), new Point(r.Right - 2, r.Top + r.Height / 2), new Point(r.Right - 8, r.Top + r.Height / 2 + 6) });
                    return;
                }

                g.FillEllipse(brush, Rectangle.Inflate(r, -10, -10));
            }

            private static bool TryDrawSvgMenuIcon(Graphics g, Rectangle r, string key, Color tint)
            {
                try
                {
                    string? path = ResolveMenuIconPath(key);
                    if (string.IsNullOrWhiteSpace(path))
                        return false;

                    int size = Math.Max(16, Math.Max(r.Width, r.Height));
                    int argb = tint.ToArgb();
                    var cacheKey = (path, size, argb);
                    if (!SvgMenuIconCache.TryGetValue(cacheKey, out var bmp) || bmp == null)
                    {
                        bmp = RenderSvgIcon(path, size, tint);
                        SvgMenuIconCache[cacheKey] = bmp;
                    }
                    if (bmp == null)
                        return false;

                    Rectangle dest = new Rectangle(r.Left + (r.Width - bmp.Width) / 2, r.Top + (r.Height - bmp.Height) / 2, bmp.Width, bmp.Height);
                    g.DrawImage(bmp, dest);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static string? ResolveMenuIconPath(string key)
            {
                return AssetIconService.Resolve(key);
            }

            private static Bitmap RenderSvgIcon(string svgPath, int targetPx, Color tint)
            {
                return AssetIconService.RenderSvg(svgPath, targetPx, tint);
            }

            private static GraphicsPath RoundRect(Rectangle r, int radius)
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
}
