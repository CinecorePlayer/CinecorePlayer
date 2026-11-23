using System;
using System.IO;

namespace CinecoreMac;

/// <summary>
/// Programmi nativi per macOS usati dall'app (FFmpeg, ffprobe): prima quelli nel pacchetto (Contents/MacOS), poi
/// quelli di Homebrew. Dove esiste un componente per Mac non si usano le versioni Windows in Wine.
/// </summary>
internal static class MacTools
{
    /// <summary>
    /// Numeri all'italiana (virgola decimale). L'app gira con InvariantGlobalization: CultureInfo.GetCultureInfo("it-IT")
    /// lancia un'eccezione (era il crash del Diario), quindi il formato si costruisce a mano.
    /// </summary>
    public static readonly System.Globalization.NumberFormatInfo Italian = new()
    {
        NumberDecimalSeparator = ",", NumberGroupSeparator = ".", PercentDecimalSeparator = ",", PercentGroupSeparator = ".",
        CurrencyDecimalSeparator = ",", CurrencyGroupSeparator = ".", NegativeSign = "-",
    };

    public static string AssetsDirectory { get; } = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Assets"))
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Assets"))
        : Path.Combine(AppContext.BaseDirectory, "Assets");
    public static string? Ffmpeg { get; } = Find("ffmpeg");
    public static string? Ffprobe { get; } = Find("ffprobe");

    private static string? Find(string name)
    {
        if (NativeComponentUpdates.UseBrewFfmpeg && NativeComponentUpdates.BrewFile("opt/ffmpeg/bin/" + name) is { } updated)
            return updated;
        foreach (var dir in new[] { AppContext.BaseDirectory, "/opt/homebrew/bin", "/usr/local/bin" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        Console.Error.WriteLine($"{name} per macOS non trovato (brew install ffmpeg)");
        return null;
    }
}
