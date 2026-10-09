#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Aggiornamento dalle release di GitHub. Una release vale come aggiornamento se il suo tag e'
    /// una versione ("v0.3.0") piu' alta di quella in esecuzione e contiene l'installer.
    /// Dalla 0.3.1 la versione si legge anche da un tag con prefisso o dal nome della release (ParseRelease).
    /// L'installer scaricato viene eseguito solo se la sua impronta SHA-256 coincide con quella
    /// pubblicata da GitHub per quel file.
    /// </summary>
    internal static class AppUpdate
    {
        public const string Owner = "CinecorePlayer", Repository = "CinecorePlayer";
        // Identita' dell'installazione Inno Setup (AppId in installer\CinecorePlayer.iss).
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6F3C2B1E-8E4A-4C57-9B0D-3C1E2A7F5D21}_is1";
        private const long MaxInstallerBytes = 1L << 30;

        public sealed record Release(Version Version, string Tag, string Name, string Notes, string AssetName, string AssetUrl, long AssetSize, string? Sha256, string PageUrl);

        public sealed class Settings
        {
            public bool AutoCheck { get; set; } = true;
            public DateTime LastCheckUtc { get; set; }
            public string? SkippedVersion { get; set; }
        }

        /// <summary>Installer scaricato e verificato: il file resta bloccato in lettura finche' non parte.</summary>
        public sealed class PreparedInstaller : IDisposable
        {
            public string Path { get; }
            private readonly FileStream _lock;
            internal PreparedInstaller(string path, FileStream held) { Path = path; _lock = held; }
            public void Dispose() => _lock.Dispose();
        }

        private static readonly HttpClient Http = CreateClient();
        private static Settings? _settings;
        private static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
        private static string SettingsPath => System.IO.Path.Combine(Folder, "update.json");
        private static string DownloadFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "updates");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static Version Current
        {
            get
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            }
        }

        /// <summary>Nome della versione come lo vede l'utente: "Beta 3.1 (0.3.1)".</summary>
        public static string CurrentLabel
        {
            get
            {
                string name = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
                int build = name.IndexOf('+');
                if (build >= 0) name = name[..build];
                return name.Length > 0 && name != Current.ToString() ? name + " (" + Current + ")" : Current.ToString();
            }
        }

        /// <summary>Questa copia e' quella installata dal setup (non una build di sviluppo o una copia portatile).</summary>
        public static bool IsInstalledCopy
        {
            get
            {
                try
                {
                    foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
                    {
                        using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
                        using var key = root.OpenSubKey(UninstallKey);
                        if (key?.GetValue("InstallLocation") is string location && location.Length > 0 &&
                            string.Equals(System.IO.Path.GetFullPath(location).TrimEnd('\\'), System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
                return false;
            }
        }

        public static Settings State
        {
            get
            {
                if (_settings != null) return _settings;
                try { _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)); } catch { }
                return _settings ??= new Settings();
            }
        }

        public static void SaveState()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(State));
            }
            catch (Exception ex) { Dbg.Warn("[UPDATE] settings: " + ex.Message); }
        }

        /// <summary>"v0.3.0" o "v0.3" -> versione. Qualsiasi altro tag (per esempio "beta-v1.1") non e' una versione.</summary>
        public static Version? ParseTag(string? tag)
        {
            var match = Regex.Match(tag ?? "", @"^v(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success
                ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0)
                : null;
        }

        /// <summary>Versione di una release il cui tag non e' nella forma "v0.3.1": un tag con prefisso
        /// ("beta-v0.3.1") oppure, con un tag qualsiasi ("HOTFIX"), la versione scritta nel nome della
        /// release ("Cinecoreplayer2025-v0.3.1-beta-windows-11-x64"). Sempre tre numeri: "beta-v1.1" resta escluso.</summary>
        public static Version? ParseRelease(string? tag, string? name)
        {
            if (ParseTag(tag) is Version exact) return exact;
            const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var match = Regex.Match(tag ?? "", @"^[a-z]+-v(\d{1,5})\.(\d{1,5})\.(\d{1,5})$", Options);
            if (!match.Success) match = Regex.Match(name ?? "", @"(?<![\w.])v(\d{1,5})\.(\d{1,5})\.(\d{1,5})(?![.\d])", Options);
            return match.Success
                ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value))
                : null;
        }

        private static bool IsReleaseAssetUrl(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" &&
            uri.AbsolutePath.StartsWith("/" + Owner + "/" + Repository + "/releases/download/", StringComparison.Ordinal);

        /// <summary>La release con la versione piu' alta che contiene un installer; null se non ce ne sono.</summary>
        public static async Task<Release?> LatestAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=30", timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return Pick(doc.RootElement);
        }

        internal static Release? Pick(JsonElement releases)
        {
            Release? best = null;
            foreach (var release in releases.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                Version? version = ParseRelease(tag, release.TryGetProperty("name", out var title) ? title.GetString() : null);
                if (version == null || (best != null && version <= best.Version)) continue;
                if (!release.TryGetProperty("assets", out var assets)) continue;

                JsonElement? installer = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, @"^[\w.\-]+$"))
                    {
                        installer = asset;
                        break;
                    }
                }
                if (installer is not { } found) continue;
                string url = found.GetProperty("browser_download_url").GetString() ?? "";
                long size = found.GetProperty("size").GetInt64();
                if (!IsReleaseAssetUrl(url) || size <= 0 || size > MaxInstallerBytes) continue;
                string? digest = found.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(digest[7..], "^[0-9a-fA-F]{64}$") ? digest[7..].ToLowerInvariant() : null;
                best = new Release(version, tag,
                    release.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                    release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    found.GetProperty("name").GetString()!, url, size, sha,
                    $"https://github.com/{Owner}/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}");
            }
            return best;
        }

        /// <summary>Scarica l'installer e ne controlla dimensione e impronta. Lancia un'eccezione se qualcosa non torna.</summary>
        public static async Task<PreparedInstaller> DownloadAsync(Release release, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
        {
            if (release.Sha256 == null) throw new InvalidOperationException("no published checksum for the installer");
            if (!IsReleaseAssetUrl(release.AssetUrl)) throw new InvalidOperationException("unexpected download address");
            Directory.CreateDirectory(DownloadFolder);
            foreach (string old in Directory.GetFiles(DownloadFolder))
                try { File.Delete(old); } catch { }
            string path = System.IO.Path.Combine(DownloadFolder, System.IO.Path.GetFileName(release.AssetName));

            using (var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                byte[] buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > release.AssetSize) throw new InvalidDataException("the download is larger than the published file");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((done, release.AssetSize));
                }
                if (done != release.AssetSize) throw new InvalidDataException("the download is incomplete");
            }

            // Da qui il file e' aperto in sola lettura e nessun altro puo' modificarlo o sostituirlo:
            // l'impronta si calcola su questo stesso file bloccato, che resta bloccato fino all'avvio.
            var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            try
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(held, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(release.Sha256)))
                    throw new InvalidDataException("the installer does not match the published checksum");
                return new PreparedInstaller(path, held);
            }
            catch
            {
                held.Dispose();
                try { File.Delete(path); } catch { }
                throw;
            }
        }

        /// <summary>Avvia l'installer in modalita' aggiornamento: niente procedura guidata, niente installatori
        /// di terze parti, e a fine installazione il player riparte. Windows chiede il consenso (UAC).
        /// False se l'utente rifiuta il consenso.</summary>
        public static bool Launch(PreparedInstaller installer)
        {
            try
            {
                Process.Start(new ProcessStartInfo(installer.Path)
                {
                    UseShellExecute = true,
                    Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /MERGETASKS=\"!lav,!mpcadec,!mpcaren,!webview2\" /RELAUNCH=1"
                })?.Dispose();
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false; // consenso negato
            }
        }
    }
}
