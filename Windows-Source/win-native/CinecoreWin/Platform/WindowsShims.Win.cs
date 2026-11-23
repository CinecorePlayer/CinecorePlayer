using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// Quello che i file condivisi con l'app Windows (Utilities/MovieMetadataService*.cs) si aspettano di trovare,
// versione Windows: registro dei messaggi, lingua, segreti con DPAPI, misura delle immagini.

namespace CinecorePlayer2025.Utilities
{
    internal static class Dbg
    {
        public enum LogLevel { Error = 0, Warn = 1, Info = 2, Verbose = 3 }
        public static LogLevel Level = LogLevel.Info;
        public static void Log(string message, LogLevel level = LogLevel.Info) { if (level <= Level) Console.Error.WriteLine(message); }
        public static void Warn(string message) => Log(message, LogLevel.Warn);
        public static void Error(string message) => Log(message, LogLevel.Error);
    }

    /// <summary>Testi dei file condivisi: la lingua dell'app Mac (L10n); lo spagnolo passa dall'inglese come su Windows.</summary>
    internal static class AppLanguage
    {
        public static string SystemDefault => "it";
        public static string Current { get; set; } = "it";
        public static bool English => Current is "en" or "es";
        public static bool Spanish => Current == "es";
        public static string T(string italian, string english) => Localize(English ? english : italian);
        public static string Localize(string text) => Spanish ? SpanishTexts.Get(text) : text;
        public static string TmdbLocale(string? language)
            => language != null && language.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "es-ES"
             : language != null && language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "it-IT";
    }

    /// <summary>
    /// SecretVault su Windows: come l'app Windows (Utilities/SecretVault.cs), valori dell'utente cifrati con DPAPI
    /// (solo questo utente su questo PC), stesso prefisso ed entropia: le chiavi salvate da Cinecore Player 2026
    /// valgono anche qui. Mai valori in chiaro su disco o nei registri.
    /// </summary>
    internal static partial class SecretVault
    {
        private const string UserPrefix = "dpapi:";
        private static readonly byte[] UserEntropy = Encoding.UTF8.GetBytes("CinecorePlayer2025/secrets/v1");

        public static bool IsProtected(string? stored) => stored != null && stored.StartsWith(UserPrefix, StringComparison.Ordinal);

        public static string Protect(string value) =>
            UserPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), UserEntropy, DataProtectionScope.CurrentUser));

        public static string? Unprotect(string? stored)
        {
            if (!IsProtected(stored) || Environment.GetEnvironmentVariable("CINECORE_QA_NO_ACCOUNTS") == "1") return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored![UserPrefix.Length..]), UserEntropy, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
    }

}

namespace CinecorePlayer2025.Utilities
{
    /// <summary>Durata e canali audio per il riallineamento dei sottotitoli (MediaProbe di Windows), con ffprobe del Mac.</summary>
    internal static class SubtitleMediaProbe
    {
        public sealed record Result(double Duration, int AudioChannels);

        public static Result Probe(string path)
        {
            string ffprobe = CinecoreMac.MacTools.Ffprobe ?? throw new FileNotFoundException("ffprobe");
            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "-v", "error", "-show_entries", "format=duration:stream=channels", "-select_streams", "a", "-of", "json", path }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            string json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            double duration = 0; int channels = 0;
            if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d))
                double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
                foreach (var s in streams.EnumerateArray()) if (s.TryGetProperty("channels", out var c)) channels = Math.Max(channels, c.GetInt32());
            return new Result(duration, channels);
        }
    }
}

namespace System.Drawing
{
    /// <summary>
    /// Il servizio metadati apre le immagini solo per controllarne la misura (locandine, sfondi, foto del cast):
    /// qui le decodifica Avalonia (Skia).
    /// </summary>
    internal sealed class Image : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        private Image(int w, int h) { Width = w; Height = h; }

        public static Image FromStream(Stream stream, bool useEmbeddedColorManagement = false, bool validateImageData = true)
        {
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
            return new Image(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        }

        public static Image FromFile(string path)
        {
            using var stream = File.OpenRead(path);
            return FromStream(stream);
        }

        public void Dispose() { }
    }
}
