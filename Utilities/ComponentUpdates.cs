#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Aggiornamento dei componenti esterni che hanno una vita propria rispetto al player:
    ///  - yt-dlp: un solo eseguibile, cambia spesso; si aggiorna da solo in una cartella dell'utente
    ///    (la cartella del programma non e' scrivibile senza permessi di amministratore);
    ///  - LAV Filters e MakeMKV: installatori originali, scaricati dal sito del produttore,
    ///    verificati e avviati solo su richiesta (Windows chiede il consenso).
    /// madVR, MPC Video Renderer, XySubFilter, libmpv e ffmpeg vivono nella cartella del programma
    /// e arrivano con l'aggiornamento del player.
    /// </summary>
    internal static class ComponentUpdates
    {
        public const string YtDlp = "yt-dlp", Lav = "lav", MakeMkv = "makemkv";
        private const long MaxDownloadBytes = 400L << 20;

        public sealed record Info(string Id, string Name, string? Installed, string? Latest, string? Url, long Size, string? Sha256, string? Error)
        {
            public bool UpdateAvailable => Latest != null && Url != null && Sha256 != null && (Installed == null || Compare(Latest, Installed) > 0);
            /// <summary>Si aggiorna senza chiedere nulla (nessun installatore, nessun permesso).</summary>
            public bool Silent => Id == YtDlp;
        }

        public enum Outcome { Updated, InstallerStarted, ConsentDenied }

        public sealed class Settings
        {
            public DateTime LastCheckUtc { get; set; }
            /// <summary>Aggiornamenti trovati dall'ultimo controllo e non ancora installati ("LAV Filters 0.82").</summary>
            public List<string> Pending { get; set; } = new();
        }

        private static readonly HttpClient Http = CreateClient();
        private static Settings? _settings;
        private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "components.json");
        private static string LocalFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025");
        private static string DownloadFolder => Path.Combine(LocalFolder, "updates", "components");
        public static string UserYtDlpPath => Path.Combine(LocalFolder, "components", "yt-dlp", "yt-dlp.exe");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer-Updater");
            return client;
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
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(State));
            }
            catch (Exception ex) { Dbg.Warn("[COMPONENTS] settings: " + ex.Message); }
        }

        /// <summary>"2025.09.26", "v1.18.3", "0.81.0-12" -> numeri confrontabili.</summary>
        public static int Compare(string? left, string? right)
        {
            int[] a = Numbers(left), b = Numbers(right);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static int[] Numbers(string? version) =>
            Regex.Matches(version ?? "", @"\d{1,9}").Take(4).Select(match => int.Parse(match.Value)).ToArray();

        // ---- versioni installate ----

        public static string? YtDlpVersion(string? path = null)
        {
            try
            {
                path ??= YtDlpLocator.Find();
                if (path == null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
                var info = FileVersionInfo.GetVersionInfo(path);
                // "2026.08.19 on Python 3.10.11": solo la data.
                string version = Regex.Match((info.ProductVersion ?? "") + " " + (info.FileVersion ?? ""), @"\d{4}\.\d{1,2}\.\d{1,2}(\.\d+)?").Value;
                return version.Length > 0 ? version : null;
            }
            catch { return null; }
        }

        public static string? LavVersion()
        {
            try
            {
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                {
                    using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\lavfilters_is1");
                    if (key?.GetValue("DisplayVersion") is string version && version.Length > 0) return version;
                }
            }
            catch { }
            return null;
        }

        // ---- ultime versioni ----

        public static async Task<List<Info>> CheckAllAsync(CancellationToken ct)
        {
            var tasks = new[] { CheckAsync(YtDlp, ct), CheckAsync(Lav, ct), CheckAsync(MakeMkv, ct) };
            var result = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
            State.LastCheckUtc = DateTime.UtcNow;
            State.Pending = result.Where(info => info.UpdateAvailable && info.Installed != null && !info.Silent).Select(info => info.Name + " " + info.Latest).ToList();
            SaveState();
            return result;
        }

        public static async Task<Info> CheckAsync(string id, CancellationToken ct)
        {
            string name = id switch { YtDlp => "yt-dlp", Lav => "LAV Filters", _ => "MakeMKV" };
            string? installed = id switch { YtDlp => YtDlpVersion(), Lav => LavVersion(), _ => DiscProtection.MakeMkvVersion() };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                if (id == MakeMkv)
                {
                    // Nessuna API: la pagina di download elenca il setup con la versione nel nome.
                    string page = await Http.GetStringAsync("https://www.makemkv.com/download/", timeout.Token).ConfigureAwait(false);
                    string? latest = Regex.Matches(page, @"Setup_MakeMKV_v(\d{1,3}\.\d{1,3}\.\d{1,3})\.exe").Select(match => match.Groups[1].Value)
                        .OrderByDescending(version => version, Comparer<string>.Create(Compare)).FirstOrDefault();
                    if (latest == null) return new Info(id, name, installed, null, null, 0, null, "version not found");
                    // Il setup non ha firma digitale; il produttore pubblica le impronte SHA-256 in un file a parte.
                    string sums = await Http.GetStringAsync("https://www.makemkv.com/download/makemkv-sha-" + latest + ".txt", timeout.Token).ConfigureAwait(false);
                    var sum = Regex.Match(sums, @"(?m)^([0-9a-fA-F]{64})\s+Setup_MakeMKV_v" + Regex.Escape(latest) + @"\.exe\s*$");
                    return new Info(id, name, installed, latest, "https://www.makemkv.com/download/Setup_MakeMKV_v" + latest + ".exe", 0,
                        sum.Success ? sum.Groups[1].Value.ToLowerInvariant() : null, sum.Success ? null : "no published checksum");
                }

                string repository = id == YtDlp ? "yt-dlp/yt-dlp" : "Nevcairiel/LAVFilters";
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + repository + "/releases/latest");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string file = asset.GetProperty("name").GetString() ?? "";
                    bool wanted = id == YtDlp ? file == "yt-dlp.exe" : Regex.IsMatch(file, @"^LAVFilters-[\d.]+-Installer\.exe$");
                    if (!wanted) continue;
                    string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    long size = asset.GetProperty("size").GetInt64();
                    string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    string? sha = digest != null && Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$") ? digest[7..].ToLowerInvariant() : null;
                    if (!IsAllowedUrl(id, url) || size <= 0 || size > MaxDownloadBytes) break;
                    return new Info(id, name, installed, tag.TrimStart('v'), url, size, sha, sha == null ? "no published checksum" : null);
                }
                return new Info(id, name, installed, null, null, 0, null, "download not found");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new Info(id, name, installed, null, null, 0, null, ex.Message);
            }
        }

        private static bool IsAllowedUrl(string id, string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            return id switch
            {
                YtDlp => uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/yt-dlp/yt-dlp/releases/download/", StringComparison.Ordinal),
                Lav => uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/Nevcairiel/LAVFilters/releases/download/", StringComparison.Ordinal),
                MakeMkv => uri.Host == "www.makemkv.com" && uri.AbsolutePath.StartsWith("/download/Setup_MakeMKV_v", StringComparison.Ordinal),
                _ => false
            };
        }

        // ---- installazione ----

        /// <summary>
        /// Scarica e applica. Il file deve coincidere con l'impronta SHA-256 pubblicata: da GitHub per
        /// yt-dlp e LAV Filters, dal sito del produttore per MakeMKV.
        /// </summary>
        public static async Task<Outcome> ApplyAsync(Info info, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
        {
            if (info.Url == null || !IsAllowedUrl(info.Id, info.Url)) throw new InvalidOperationException("unexpected download address");
            if (info.Sha256 == null) throw new InvalidOperationException("no published checksum");

            string folder = info.Id == YtDlp ? Path.GetDirectoryName(UserYtDlpPath)! : DownloadFolder;
            Directory.CreateDirectory(folder);
            string fileName = Path.GetFileName(new Uri(info.Url).AbsolutePath);
            if (!Regex.IsMatch(fileName, @"^[\w.\-]+\.exe$")) throw new InvalidOperationException("unexpected file name");
            string path = Path.Combine(folder, info.Id == YtDlp ? "yt-dlp.exe.download" : fileName);

            using (var response = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = info.Size > 0 ? info.Size : response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                byte[] buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxDownloadBytes || (info.Size > 0 && done > info.Size)) throw new InvalidDataException("the download is larger than expected");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((done, total));
                }
                if (info.Size > 0 && done != info.Size) throw new InvalidDataException("the download is incomplete");
            }

            // Il file resta aperto in sola lettura da qui all'avvio: nessuno puo' sostituirlo dopo la verifica.
            var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            try
            {
                {
                    string actual = Convert.ToHexString(await SHA256.HashDataAsync(held, ct).ConfigureAwait(false)).ToLowerInvariant();
                    if (actual != info.Sha256) throw new InvalidDataException("the file does not match the published checksum");
                }

                if (info.Id == YtDlp)
                {
                    held.Dispose();
                    File.Move(path, UserYtDlpPath, overwrite: true);
                    Dbg.Log("[COMPONENTS] yt-dlp updated to " + info.Latest, Dbg.LogLevel.Info);
                    return Outcome.Updated;
                }

                try
                {
                    // Installatore originale, visibile: l'utente ne segue le opzioni e la licenza.
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                    return Outcome.InstallerStarted;
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return Outcome.ConsentDenied;
                }
            }
            catch
            {
                held.Dispose();
                try { File.Delete(path); } catch { }
                throw;
            }
            finally
            {
                // Lascia all'installatore il tempo di partire prima di rilasciare il file.
                _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => { try { held.Dispose(); } catch { } });
            }
        }

        /// <summary>Controllo all'avvio, al massimo una volta al giorno: aggiorna yt-dlp e ricorda il resto.</summary>
        public static async Task<List<string>> RunStartupAsync(CancellationToken ct)
        {
            var infos = await CheckAllAsync(ct).ConfigureAwait(false);
            foreach (var info in infos.Where(info => info.Silent && info.UpdateAvailable && info.Sha256 != null))
            {
                try { await ApplyAsync(info, null, ct).ConfigureAwait(false); }
                catch (Exception ex) { Dbg.Warn("[COMPONENTS] " + info.Name + ": " + ex.Message); }
            }
            return State.Pending;
        }
    }
}
