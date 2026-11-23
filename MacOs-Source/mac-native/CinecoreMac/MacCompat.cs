#if !CINECORE_WINDOWS
using System;

namespace CinecoreMac;

/// <summary>
/// Pezzi che il codice condiviso con la versione Windows (registro PER-MAC del 23/11/2026) usa anche fuori dai blocchi
/// solo-Windows. Sul Mac il vetro e' quello nativo di macOS: la cache del vetro di Windows qui non fa nulla.
/// </summary>
internal sealed class GlassCache : IDisposable
{
    public void Dispose() { }
}

/// <summary>Risorse dell'app (Windows: PlatformPaths.cs); letto dall'assembly, come su Windows.</summary>
internal static class AppAssets
{
    public static readonly string Root = "avares://" + typeof(AppAssets).Assembly.GetName().Name + "/";
}

/// <summary>Tasto dei comandi: Cmd sul Mac (Ctrl su Windows).</summary>
internal static class PlatformKeys
{
    public const Avalonia.Input.KeyModifiers Command = Avalonia.Input.KeyModifiers.Meta;
}
#endif
