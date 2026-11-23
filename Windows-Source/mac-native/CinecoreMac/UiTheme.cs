using System;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// Colori dell'interfaccia, come Theme di Windows (HUD/Ui.ModalsAndDsPages.cs): pannello, superfici, testo e accento,
/// con il tema chiaro e quello scuro e gli sfondi personalizzati (superfici e navigazione ne derivano).
/// </summary>
internal static class UiTheme
{
#if CINECORE_WINDOWS
    // [2026-11-22] Su Windows piu' scuri: il blu notte del Mac (7,12,18) sui pannelli IPS e VA dei PC si vede grigio-azzurro,
    // mentre sullo schermo del Mac sembra quasi nero. Stessa tinta, circa meta' della luce.
    public static readonly Color DefaultPanel = Color.FromRgb(3, 5, 9);
    public static readonly Color DefaultCard = Color.FromRgb(10, 14, 20);
    public static readonly Color DefaultNav = Color.FromRgb(5, 8, 12);
#else
    public static readonly Color DefaultPanel = Color.FromRgb(7, 12, 18);
    public static readonly Color DefaultCard = Color.FromRgb(14, 21, 29);
    public static readonly Color DefaultNav = Color.FromRgb(9, 15, 22);
#endif
    public static readonly Color DefaultBorder = Color.FromRgb(42, 54, 66);
    public static readonly Color DefaultText = Colors.White;
    public static readonly Color DefaultSubtleText = Color.FromRgb(190, 204, 220);
    public static readonly Color DefaultMuted = Color.FromRgb(142, 160, 180);
    public static readonly Color DefaultAccent = Color.FromRgb(50, 128, 235);
    public static readonly Color DefaultAccentSoft = Color.FromRgb(24, 69, 132);
    public static readonly Color DefaultSelection = Color.FromRgb(39, 96, 174);
    public static readonly Color DefaultBorderAccent = Color.FromRgb(74, 94, 114);

    public static Color Panel { get; private set; } = DefaultPanel;
    public static Color Card { get; private set; } = DefaultCard;
    public static Color Nav { get; private set; } = DefaultNav;
    public static Color Border { get; private set; } = DefaultBorder;
    public static Color Text { get; private set; } = DefaultText;
    public static Color SubtleText { get; private set; } = DefaultSubtleText;
    public static Color Muted { get; private set; } = DefaultMuted;
    public static Color Accent { get; private set; } = DefaultAccent;
    public static Color AccentSoft { get; private set; } = DefaultAccentSoft;
    public static Color Selection { get; private set; } = DefaultSelection;
    public static Color BorderAccent { get; private set; } = DefaultBorderAccent;

    public static event Action? Changed;

    public static bool IsLight => Luminance(Panel) >= 0.30;

    /// <summary>Pulsante principale come su Windows: bianco con testo nero (nel tema chiaro scuro con testo bianco), mai l'accento.</summary>
    public static Color PrimaryFill(bool hover) => IsLight ? Color.FromArgb(255, (byte)(hover ? 60 : 20), 24, 30) : Color.FromArgb((byte)(hover ? 228 : 255), 255, 255, 255);
    public static Color OnPrimary => IsLight ? Colors.White : Colors.Black;

    public static void SetAccent(Color color)
    {
        Accent = Opaque(color);
        AccentSoft = Darken(Accent, 0.34f);
        Selection = Accent;
        BorderAccent = Blend(DefaultBorderAccent, Accent, 0.36f);
        Changed?.Invoke();
    }

    public static void SetSurfacePalette(Color panel, Color card, Color nav)
    {
        Panel = Opaque(panel); Card = Opaque(card); Nav = Opaque(nav);
        Border = Blend(DefaultBorder, BorderAccent, 0.28f);
        if (IsLight)
        {
            Text = Color.FromRgb(23, 32, 42);
            SubtleText = Color.FromRgb(54, 68, 82);
            Muted = Color.FromRgb(78, 94, 109);
            Border = Blend(Mix(Panel, Text, .13), BorderAccent, 0.08f);
        }
        else
        {
            Text = DefaultText; SubtleText = DefaultSubtleText; Muted = DefaultMuted;
        }
        Changed?.Invoke();
    }

    /// <summary>ApplyUiToneCore: i colori pronti del tema chiaro o di quello scuro.</summary>
    public static void ApplyTone(bool light)
    {
        if (light)
        {
            Accent = Color.FromRgb(28, 95, 186); AccentSoft = Color.FromRgb(72, 118, 178);
            Selection = Color.FromRgb(59, 119, 198); BorderAccent = Color.FromRgb(156, 168, 181);
            SetSurfacePalette(Color.FromRgb(226, 230, 235), Color.FromRgb(246, 247, 249), Color.FromRgb(211, 217, 224));
        }
        else
        {
            Accent = DefaultAccent; AccentSoft = DefaultAccentSoft; Selection = DefaultSelection; BorderAccent = DefaultBorderAccent;
            SetSurfacePalette(DefaultPanel, DefaultCard, DefaultNav);
        }
    }

    /// <summary>ApplyUiBackgroundColor: uno sfondo scelto; superfici e navigazione ne derivano, e decide chiaro o scuro.</summary>
    public static void ApplyBackground(Color background)
    {
        background = Opaque(background);
        bool light = Luminance(background) >= 0.5;
        SetSurfacePalette(background,
            light ? Mix(background, Colors.White, .62) : Mix(background, Colors.White, .055),
            light ? Mix(background, Colors.Black, .06) : Mix(background, Colors.Black, .22));
    }

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(Math.Round(a), 0, 255), c.R, c.G, c.B);
    public static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    private static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);
    private static Color Blend(Color from, Color to, float amount) => Mix(from, to, Math.Clamp(amount, 0f, 1f));
    private static Color Darken(Color c, float amount)
    {
        double f = 1 - Math.Clamp(amount, 0f, 0.85f);
        return Color.FromRgb((byte)Math.Round(c.R * f), (byte)Math.Round(c.G * f), (byte)Math.Round(c.B * f));
    }
}
