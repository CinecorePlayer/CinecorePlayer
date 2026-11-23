using Avalonia.Input;

namespace CinecoreMac;

/// <summary>Il tasto delle scorciatoie: ⌘ sul Mac, Ctrl su Windows (dove Meta e' il tasto Windows).</summary>
internal static class PlatformKeys
{
#if CINECORE_WINDOWS
    public const KeyModifiers Command = KeyModifiers.Control;
#else
    public const KeyModifiers Command = KeyModifiers.Meta;
#endif
}

/// <summary>Testi che cambiano tra Mac e Windows.</summary>
internal static class PlatformText
{
#if CINECORE_WINDOWS
    public static string AudioSystem(string mac, string windows) => windows;
#else
    public static string AudioSystem(string mac, string windows) => mac;
#endif
}
