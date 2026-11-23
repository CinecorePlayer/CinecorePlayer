using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// Quello che i file condivisi con l'app Windows (Utilities/MovieMetadataService*.cs) si aspettano di trovare,
// rifatto per macOS: registro dei messaggi, lingua, segreti nel Portachiavi, misura delle immagini.

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
    /// SecretVault di Windows (DPAPI) sul Mac: archivio dell'app (~/Library/Application Support/Cinecore/vault.json,
    /// leggibile solo dall'utente, valori cifrati AES-GCM con una chiave locale), come fanno i client Jellyfin.
    /// Prima stava nel Portachiavi: con la firma ad hoc macOS chiedeva la password del Mac a ogni build/aggiornamento.
    /// I riferimenti salvati ("keychain:" + impronta) restano gli stessi: se un valore non e' ancora nell'archivio
    /// si legge un'ultima volta dal Portachiavi e lo si copia qui.
    /// </summary>
    internal static partial class SecretVault
    {
        private const string Prefix = "keychain:";
        private static readonly object Sync = new();
        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Cinecore");
        private static string VaultPath => Path.Combine(Folder, "vault.json");
        private static string KeyPath => Path.Combine(Folder, ".vault-key");
        private static System.Collections.Generic.Dictionary<string, string>? _vault;

        public static bool IsProtected(string? stored) => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

        public static string Protect(string value)
        {
            string account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cinecore|" + value)))[..24].ToLowerInvariant();
            lock (Sync)
            {
                Vault()[account] = Encrypt(value);
                Save();
            }
            return Prefix + account;
        }

        public static string? Unprotect(string? stored)
        {
            if (!IsProtected(stored) || Environment.GetEnvironmentVariable("CINECORE_QA_NO_ACCOUNTS") == "1") return null;
            string account = stored![Prefix.Length..];
            lock (Sync)
                if (Vault().TryGetValue(account, out string? blob) && Decrypt(blob) is { } value) return value;
            // Vecchio salvataggio nel Portachiavi: ultima lettura, poi resta nell'archivio.
            string? legacy = Keychain.Get(account);
            if (legacy != null)
                lock (Sync) { Vault()[account] = Encrypt(legacy); Save(); }
            return legacy;
        }

        private static System.Collections.Generic.Dictionary<string, string> Vault()
        {
            if (_vault != null) return _vault;
            try { if (File.Exists(VaultPath)) _vault = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(VaultPath)); }
            catch { }
            return _vault ??= new();
        }

        private static void Save()
        {
            Directory.CreateDirectory(Folder);
            string temp = VaultPath + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(Vault()));
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, VaultPath, true);
        }

        private static byte[] Key()
        {
            if (File.Exists(KeyPath)) { var existing = File.ReadAllBytes(KeyPath); if (existing.Length == 32) return existing; }
            Directory.CreateDirectory(Folder);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(KeyPath, key);
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }

        private static string Encrypt(string value)
        {
            byte[] plain = Encoding.UTF8.GetBytes(value), nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
            using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, plain, cipher, tag);
            CryptographicOperations.ZeroMemory(plain);
            return Convert.ToBase64String(nonce) + "." + Convert.ToBase64String(tag) + "." + Convert.ToBase64String(cipher);
        }

        private static string? Decrypt(string blob)
        {
            try
            {
                var parts = blob.Split('.');
                byte[] nonce = Convert.FromBase64String(parts[0]), tag = Convert.FromBase64String(parts[1]), cipher = Convert.FromBase64String(parts[2]), plain = new byte[cipher.Length];
                using (var aes = new AesGcm(Key(), 16)) aes.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
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
            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, UseShellExecute = false };
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
