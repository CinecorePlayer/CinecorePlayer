using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// Aggiornamenti dell'app per Mac (AppUpdate di Windows). Il canale e' un indirizzo https, da mettere in
/// <see cref="DefaultFeed"/> (o nelle impostazioni, chiave "updateFeed"). Due forme accettate:
/// <list type="bullet">
/// <item>le release di GitHub (https://api.github.com/repos/PROPRIETARIO/REPO/releases): vale la versione piu' alta
/// ("v0.3.7", "beta-v0.3.7" o la versione nel nome) con un allegato .zip o .dmg per Mac; l'impronta e' quella che
/// pubblica GitHub ("digest");</item>
/// <item>un file JSON: { "version": "0.3.7", "url": "https://…/CinecorePlayere2026.zip", "sha256": "…", "notes": "…" }.</item>
/// </list>
/// Il pacchetto si installa solo se l'impronta SHA-256 coincide, contiene un'app con lo stesso identificativo e la
/// copia in uso e' un'app (non una build di sviluppo). Al riavvio la vecchia app resta in ~/Library/Caches/Cinecore/updates.
/// </summary>
internal static class MacUpdate
{
    /// <summary>
    /// Canale: le release di GitHub dello stesso progetto di Windows (CinecorePlayer/CinecorePlayer). Le release per Mac
    /// hanno il tag "beta-v0.1.0-Avalonia-MacOs" (quelle di Windows "beta-v0.4.0-Avalonia-Windows" o "beta-v0.3.6") e un
    /// allegato .zip/.dmg con "Mac" nel nome (mac/make-mac-release.sh lo prepara). Qui contano solo le release con "mac" nel
    /// tag: la versione di Windows segue un'altra numerazione e puo' avere anche uno zip.
    /// </summary>
    public const string DefaultFeed = "https://api.github.com/repos/CinecorePlayer/CinecorePlayer/releases?per_page=30";
    private const string BundleId = "com.cinecore.player.native";
    private const long MaxPackageBytes = 2L << 30;

    public sealed record Release(Version Version, string Name, string Notes, string Url, long Size, string Sha256, string? PageUrl);

    public static string Feed => MacSettings.Get("updateFeed", DefaultFeed).Trim();
    public static bool Configured => Uri.TryCreate(Feed, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    public static bool AutoCheck
    {
        get => MacSettings.Get("updateAutoCheck", "1") == "1";
        set => MacSettings.Set("updateAutoCheck", value ? "1" : "0");
    }

    public static string? SkippedVersion
    {
        get => MacSettings.Get("updateSkipped", "") is { Length: > 0 } v ? v : null;
        set => MacSettings.Set("updateSkipped", value ?? "");
    }

    private static DateTime LastCheckUtc
    {
        get => long.TryParse(MacSettings.Get("updateLastCheck", "0"), out long t) ? DateTime.UnixEpoch.AddSeconds(t) : DateTime.MinValue;
        set => MacSettings.Set("updateLastCheck", ((long)(value - DateTime.UnixEpoch).TotalSeconds).ToString());
    }

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Il nome della versione per le persone: "Beta 1".</summary>
    public static string DisplayName
    {
        get
        {
            string name = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = name.IndexOf('+');
            if (plus >= 0) name = name[..plus];
            return name.Length > 0 ? name : Current.ToString();
        }
    }

    /// <summary>"Beta 1 (0.1.0)".</summary>
    public static string CurrentLabel
    {
        get
        {
            string name = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = name.IndexOf('+');
            if (plus >= 0) name = name[..plus];
            return name.Length > 0 && name != Current.ToString() ? name + " (" + Current + ")" : Current.ToString();
        }
    }

    /// <summary>Il bundle .app in uso; null per le build di sviluppo (bin/…), che non si aggiornano da sole.</summary>
    public static string? InstalledBundle
    {
        get
        {
#if CINECORE_WINDOWS
            // Copia installata dal setup (Inno Setup lascia unins000.exe accanto all'eseguibile).
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")) ? AppContext.BaseDirectory : null;
#endif
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
            // …/Nome.app/Contents/MacOS
            if (dir.Name == "MacOS" && dir.Parent?.Name == "Contents" && dir.Parent.Parent is { } app && app.Name.EndsWith(".app", StringComparison.Ordinal))
                return app.FullName;
            return null;
        }
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer-Updater");
        return client;
    }

    private static string Folder => System.IO.Path.Combine(global::CinecoreMac.PlatformPaths.CinecoreCaches, "updates");

    /// <summary>Controllo all'avvio (come Windows: al massimo uno ogni 20 ore). Null se non c'e' niente da proporre.</summary>
    public static async Task<Release?> CheckAtStartupAsync()
    {
#if CINECORE_WINDOWS
        // Su Windows l'installazione la fa l'installer: niente offerta automatica all'avvio (si controlla dal menu).
        await Task.CompletedTask;
        return null;
#endif
        if (!AutoCheck || !Configured || InstalledBundle == null || DateTime.UtcNow - LastCheckUtc < TimeSpan.FromHours(20)) return null;
        try
        {
            var release = await LatestAsync(CancellationToken.None).ConfigureAwait(false);
            LastCheckUtc = DateTime.UtcNow;
            if (release == null || release.Version <= Current || release.Version.ToString() == SkippedVersion) return null;
            return release;
        }
        catch (Exception ex) { Console.Error.WriteLine("[aggiornamenti] " + ex.Message); return null; }
    }

    public static async Task<Release?> LatestAsync(CancellationToken ct)
    {
        if (!Configured) throw new InvalidOperationException("canale degli aggiornamenti non ancora configurato");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, Feed);
        if (new Uri(Feed).Host == "api.github.com") request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? PickGitHub(doc.RootElement) : PickManifest(doc.RootElement);
    }

    private static Version? ParseVersion(string? text)
    {
        var m = Regex.Match(text ?? "", @"(?<![\w.])v?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?(?![.\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;
    }

    private static bool IsHttps(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
#if CINECORE_WINDOWS
    // Windows: l'installer (…-Setup.exe) allegato alla release "beta-vX.Y.Z-Avalonia-Windows".
    private static bool IsPackage(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, @"^[\w.\-]+$");
#else
    private static bool IsPackage(string name) =>
        (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)) && Regex.IsMatch(name, @"^[\w.\-]+$");
#endif

    private static Release? PickManifest(JsonElement root)
    {
        string? S(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var version = ParseVersion(S("version"));
        string? url = S("url"), sha = S("sha256")?.ToLowerInvariant();
        if (version == null || !IsHttps(url) || sha == null || !Regex.IsMatch(sha, "^[0-9a-f]{64}$")) return null;
        long size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
        return new Release(version, S("name") ?? "Cinecore Player " + version, S("notes") ?? "", url!, size, sha, S("page"));
    }

    /// <summary>Con un canale GitHub il pacchetto deve arrivare dalle release dello stesso repository.</summary>
    private static bool FromFeedRepository(string url)
    {
        if (!Uri.TryCreate(Feed, UriKind.Absolute, out var feed) || feed.Host != "api.github.com") return true;
        var m = Regex.Match(feed.AbsolutePath, @"^/repos/([\w.\-]+)/([\w.\-]+)/releases");
        if (!m.Success) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com" &&
               u.AbsolutePath.StartsWith("/" + m.Groups[1].Value + "/" + m.Groups[2].Value + "/releases/download/", StringComparison.Ordinal);
    }

    /// <summary>"beta-v0.1.0-Avalonia-MacOs", e anche il vecchio "mac-v0.1.1": "mac" come parola del tag.</summary>
#if CINECORE_WINDOWS
    internal static bool IsMacTag(string tag) => Regex.IsMatch(tag, @"(?:^|[-_.])avalonia[-_.]windows(?:$|[-_.])", RegexOptions.IgnoreCase);
#else
    internal static bool IsMacTag(string tag) => Regex.IsMatch(tag, @"(?:^|[-_.])mac(?:os)?(?:$|[-_.])", RegexOptions.IgnoreCase);
#endif

    private static Release? PickGitHub(JsonElement releases)
    {
        Release? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string title = release.TryGetProperty("name", out var nm) ? nm.GetString() ?? tag : tag;
            if (!IsMacTag(tag)) continue;
            // "Cinecore Player Mac Beta 1 (0.1.0) - Release note (COMING SOON)": nella finestra basta la parte prima del trattino.
            title = Regex.Replace(title, @"\s+-\s+Release note.*$", "", RegexOptions.IgnoreCase).Trim();
            var version = ParseVersion(tag) ?? ParseVersion(title);
            if (version == null || (best != null && version <= best.Version) || !release.TryGetProperty("assets", out var assets)) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                // Le release possono avere anche l'installer di Windows: si prende solo il pacchetto per Mac.
#if CINECORE_WINDOWS
                if (!IsPackage(name)) continue;
#else
                if (!IsPackage(name) || !(name.Contains("mac", StringComparison.OrdinalIgnoreCase) || name.Contains("2026", StringComparison.Ordinal) || name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))) continue;
#endif
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                long size = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(digest[7..], "^[0-9a-fA-F]{64}$") ? digest[7..].ToLowerInvariant() : null;
                if (!IsHttps(url) || sha == null || size <= 0 || size > MaxPackageBytes || !FromFeedRepository(url)) continue;
                best = new Release(version, title, release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", url, size, sha,
                    release.TryGetProperty("html_url", out var page) ? page.GetString() : null);
                break;
            }
        }
        return best;
    }

    /// <summary>
    /// Scarica, controlla l'impronta, estrae l'app e prepara lo scambio: restituisce lo script che, dopo l'uscita di
    /// Cinecore, mette la nuova app al posto della vecchia e la riapre. Lancia un'eccezione se qualcosa non torna.
    /// </summary>
    public static async Task<string> PrepareAsync(Release release, IProgress<double>? progress, CancellationToken ct)
    {
        string bundle = InstalledBundle ?? throw new InvalidOperationException("questa copia non e' un'app installata");
        Directory.CreateDirectory(Folder);
        string work = Path.Combine(Folder, "v" + release.Version);
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        Directory.CreateDirectory(work);
        string package = Path.Combine(work, Path.GetFileName(new Uri(release.Url).AbsolutePath));

        using (var response = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;
            if (total > MaxPackageBytes) throw new InvalidOperationException("pacchetto troppo grande");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(package);
            var buffer = new byte[1 << 16];
            long done = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (done > MaxPackageBytes) throw new InvalidOperationException("pacchetto troppo grande");
                if (total > 0) progress?.Report(Math.Min(1, done / (double)total) * 0.9);
            }
        }
        if (release.Size > 0 && new FileInfo(package).Length != release.Size) throw new InvalidOperationException("dimensione del pacchetto diversa da quella pubblicata");
        await using (var check = File.OpenRead(package))
        {
            string sha = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (sha != release.Sha256) throw new InvalidOperationException("impronta SHA-256 diversa: pacchetto scartato");
        }

#if CINECORE_WINDOWS
        progress?.Report(1);
        // Windows: il setup verificato si avvia dopo l'uscita del player (Launch).
        return package;
#endif
        string extracted = Path.Combine(work, "app");
        Directory.CreateDirectory(extracted);
        if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            Run("/usr/bin/ditto", "-x", "-k", package, extracted);
        else
        {
            string mount = Path.Combine(work, "mnt");
            Directory.CreateDirectory(mount);
            Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, package);
            try
            {
                foreach (string app in Directory.EnumerateDirectories(mount, "*.app"))
                    Run("/usr/bin/ditto", app, Path.Combine(extracted, Path.GetFileName(app)));
            }
            finally { try { Run("/usr/bin/hdiutil", "detach", "-quiet", mount); } catch { } }
        }
        string? newApp = null;
        foreach (string app in Directory.EnumerateDirectories(extracted, "*.app")) { newApp = app; break; }
        if (newApp == null) throw new InvalidOperationException("il pacchetto non contiene un'app");
        string id = Run("/usr/bin/plutil", "-extract", "CFBundleIdentifier", "raw", Path.Combine(newApp, "Contents", "Info.plist")).Trim();
        if (id != BundleId) throw new InvalidOperationException("il pacchetto contiene un'altra app (" + id + ")");
        progress?.Report(1);

        string backup = Path.Combine(work, "precedente.app");
        string script = Path.Combine(work, "installa.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            # Cinecore: sostituzione dell'app dopo l'uscita (preparato da MacUpdate).
            while kill -0 {{Environment.ProcessId}} 2>/dev/null; do sleep 0.3; done
            mv {{Q(bundle)}} {{Q(backup)}} || exit 1
            if ! mv {{Q(newApp)}} {{Q(bundle)}}; then mv {{Q(backup)}} {{Q(bundle)}}; exit 1; fi
            xattr -dr com.apple.quarantine {{Q(bundle)}} 2>/dev/null
            open {{Q(bundle)}}
            """);
        return script;
    }

    /// <summary>Avvia lo script di sostituzione (aspetta l'uscita di Cinecore): subito dopo l'app si chiude.</summary>
    public static void Launch(string script)
    {
#if CINECORE_WINDOWS
        // Il setup chiude da se' le copie aperte (Inno Setup); Cinecore esce subito dopo.
        Process.Start(new ProcessStartInfo(script) { UseShellExecute = true });
        return;
#endif
        var psi = new ProcessStartInfo("/bin/sh", new[] { script }) { UseShellExecute = false, CreateNoWindow = true };
        Process.Start(psi);
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string Run(string exe, params string[] args)
    {
        var (exit, stdout, stderr) = ProcessLauncher.Run(exe, args, false, 300_000, CancellationToken.None);
        if (exit != 0) throw new InvalidOperationException(Path.GetFileName(exe) + ": " + stderr.Trim());
        return System.Text.Encoding.UTF8.GetString(stdout);
    }
}
