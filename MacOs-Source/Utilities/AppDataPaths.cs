using System;

namespace CinecorePlayer2025.Utilities;

/// <summary>
/// Cartelle dei dati per utente (ApplicationData / LocalApplicationData). Di norma sono quelle di sistema; per le prove
/// automatiche CINECORE_APPDATA_ROOT le sposta altrove. Serve sul Mac: .NET le risolve con le API di macOS
/// (~/Library/Application Support dell'utente vero) e ignora HOME, quindi una HOME separata non bastava a tenere le
/// prove fuori dagli archivi dell'utente.
/// </summary>
internal static class AppDataPaths
{
    private static readonly string? Root =
        Environment.GetEnvironmentVariable("CINECORE_APPDATA_ROOT") is { Length: > 0 } root ? root : null;

    public static string Get(Environment.SpecialFolder folder) => Root ?? Environment.GetFolderPath(folder);
}
