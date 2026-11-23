using System;
using System.IO;

namespace CinecoreMac;

/// <summary>
/// Cartelle dell'app per sistema: sul Mac ~/Library/Application Support, Caches e Logs; su Windows %APPDATA% e
/// %LOCALAPPDATA%. Stessi nomi interni (Cinecore, CinecorePlayer2025) su entrambi.
/// </summary>
internal static class PlatformPaths
{
#if CINECORE_WINDOWS
    /// <summary>Impostazioni e archivi dell'app (%APPDATA%\Cinecore).</summary>
    public static string CinecoreData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cinecore");
    /// <summary>Dati condivisi con l'app Windows (%APPDATA%\CinecorePlayer2025).</summary>
    public static string LegacyData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
    /// <summary>Cache rigenerabili (%LOCALAPPDATA%\Cinecore\Cache).</summary>
    public static string CinecoreCaches => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinecore", "Cache");
    /// <summary>Registri delle sessioni (%LOCALAPPDATA%\Cinecore\Logs).</summary>
    public static string CinecoreLogs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinecore", "Logs");
    public static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
#else
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string CinecoreData => Path.Combine(Home, "Library", "Application Support", "Cinecore");
    public static string LegacyData => Path.Combine(Home, "Library", "Application Support", "CinecorePlayer2025");
    public static string CinecoreCaches => Path.Combine(Home, "Library", "Caches", "Cinecore");
    public static string CinecoreLogs => Path.Combine(Home, "Library", "Logs", "Cinecore");
    public static string Pictures => Path.Combine(Home, "Pictures");
#endif
}

/// <summary>Apre indirizzi e mostra file con il programma del sistema (Finder / Esplora file, browser predefinito).</summary>
internal static class PlatformShell
{
    public static void OpenUrl(string target)
    {
        try
        {
#if CINECORE_WINDOWS
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
#else
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            System.Diagnostics.Process.Start(psi)?.Dispose();
#endif
        }
        catch { }
    }

    /// <summary>Mostra il file selezionato nella sua cartella.</summary>
    public static void Reveal(string path)
    {
        try
        {
#if CINECORE_WINDOWS
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/select," + Path.GetFullPath(path));
            System.Diagnostics.Process.Start(psi)?.Dispose();
#else
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R"); psi.ArgumentList.Add(Path.GetFullPath(path));
            System.Diagnostics.Process.Start(psi)?.Dispose();
#endif
        }
        catch { }
    }
}

/// <summary>Nomi dell'uscita audio di libmpv per sistema: CoreAudio sul Mac, WASAPI su Windows.</summary>
internal static class MpvAudioNames
{
#if CINECORE_WINDOWS
    public const string DevicePrefix = "wasapi/";
    public const string ExclusiveAo = "wasapi";
    /// <summary>Uscita esclusiva per il bitstream: su Windows e' un'opzione a parte.</summary>
    public static void Exclusive(System.Action<string, string> opt) { opt("ao", "wasapi"); opt("audio-exclusive", "yes"); }
#else
    public const string DevicePrefix = "coreaudio/";
    public const string ExclusiveAo = "coreaudio_exclusive";
    public static void Exclusive(System.Action<string, string> opt) => opt("ao", "coreaudio_exclusive,coreaudio");
#endif
}

/// <summary>Confronto fra percorsi: "/" e maiuscole contano sul Mac; su Windows separatore "\" e maiuscole indifferenti.</summary>
internal static class PathText
{
#if CINECORE_WINDOWS
    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;
    /// <summary>La cartella con il separatore finale (prefisso dei file che contiene).</summary>
    public static string Prefix(string folder) => folder.TrimEnd('\\', '/') + "\\";
    public static string Trim(string folder) => folder.Length <= 3 && folder.EndsWith(":\\", StringComparison.Ordinal) ? folder : folder.TrimEnd('\\', '/');
#else
    public const StringComparison Comparison = StringComparison.Ordinal;
    public static string Prefix(string folder) => folder.TrimEnd('/') + "/";
    public static string Trim(string folder) => folder.TrimEnd('/');
#endif
    public static bool Within(string path, string folder) => path.StartsWith(Prefix(folder), Comparison);
}

/// <summary>Risorse incluse nell'app (caratteri, logo, testi): l'indirizzo avares porta il nome dell'assembly.</summary>
internal static class AppAssets
{
    // Letto dall'assembly stesso: rinominare l'eseguibile (AssemblyName) non deve piu' rompere logo, caratteri e testi.
    public static readonly string Root = "avares://" + typeof(AppAssets).Assembly.GetName().Name + "/";
}
