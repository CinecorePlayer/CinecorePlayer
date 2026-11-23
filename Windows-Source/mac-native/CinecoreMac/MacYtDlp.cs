using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// yt-dlp sul Mac (ComponentUpdates di Windows, parte yt-dlp): cambia spesso perche' i siti cambiano, quindi si aggiorna
/// da solo, senza chiedere, in una cartella dell'utente (il pacchetto dell'app e' firmato e non si tocca).
/// Ordine: copia aggiornata dall'app, copia nel pacchetto (Contents/MacOS/yt-dlp), Homebrew.
/// Il file arriva solo dalle release di yt-dlp/yt-dlp su GitHub ("yt-dlp_macos", universale e autonomo) e si usa solo
/// se l'impronta SHA-256 pubblicata da GitHub coincide e se risponde a --version.
/// </summary>
internal static class MacYtDlp
{
#if CINECORE_WINDOWS
    private const string Asset = "yt-dlp.exe";
#else
    private const string Asset = "yt-dlp_macos";
#endif
    private const long MaxBytes = 200L << 20;
    private static readonly HttpClient Http = CreateClient();
    private static int _updating;

    public static string UserPath => Path.Combine(
        global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.LocalApplicationData),
        "CinecorePlayer2025", "components", "yt-dlp", "yt-dlp");

    public static string BundlePath => Path.Combine(AppContext.BaseDirectory, "yt-dlp");

#if CINECORE_WINDOWS
    public static string? Find() => new[] { UserPath + ".exe", BundlePath + ".exe", WinNative.ThirdParty("yt-dlp", "yt-dlp.exe") ?? "" }.FirstOrDefault(File.Exists);
#else
    public static string? Find() => new[] { UserPath, BundlePath, "/opt/homebrew/bin/yt-dlp", "/usr/local/bin/yt-dlp" }.FirstOrDefault(File.Exists);
#endif

    /// <summary>
    /// yt-dlp da usare; se non c'e' (Mac senza Homebrew, primo uso) lo scarica e lo verifica adesso. Solo da un thread di
    /// sottofondo: puo' durare decine di secondi.
    /// </summary>
    public static string? Ensure(CancellationToken ct)
    {
        if (Find() is { } found) return found;
        try { UpdateAsync(ct).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        return Find();
    }

    public const string MissingMessage = "yt-dlp non disponibile: controlla la connessione a internet";

    public static string Origin(string? path) =>
        path == null ? "non trovato" : path == UserPath ? "aggiornato da Cinecore" : path == BundlePath ? "nel pacchetto dell'app" : "Homebrew";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer-Updater");
        return client;
    }

    public static string? Version(string? path = null)
    {
        path ??= Find();
        if (path == null) return null;
        try
        {
            var (exit, stdout, _) = ProcessLauncher.Run(path, new[] { "--version" }, false, 30000, CancellationToken.None);
            string v = Encoding.UTF8.GetString(stdout).Trim();
            return exit == 0 && Regex.IsMatch(v, @"^\d{4}\.\d{2}\.\d{2}") ? v : null;
        }
        catch { return null; }
    }

    private static int Compare(string a, string b)
    {
        var pa = a.Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public sealed record Release(string Version, string Url, long Size, string Sha256);

    public static async Task<Release?> LatestAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != Asset) continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            long size = asset.GetProperty("size").GetInt64();
            string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.Host != "github.com" ||
                !u.AbsolutePath.StartsWith("/yt-dlp/yt-dlp/releases/download/", StringComparison.Ordinal) || size <= 0 || size > MaxBytes) return null;
            return new Release(tag.TrimStart('v'), url, size, digest[7..].ToLowerInvariant());
        }
        return null;
    }

    /// <summary>Controlla e, se c'e' una versione piu' nuova, la installa nella cartella dell'utente. Restituisce la versione in uso.</summary>
    public static async Task<(string? Installed, string? Latest, string? Error)> UpdateAsync(CancellationToken ct)
    {
        string? installed = Version();
        if (Interlocked.Exchange(ref _updating, 1) != 0) return (installed, null, "aggiornamento gia' in corso");
        try
        {
            var latest = await LatestAsync(ct).ConfigureAwait(false);
            if (latest == null) return (installed, null, "versione per Mac non trovata");
            if (installed != null && Compare(latest.Version, installed) <= 0) return (installed, latest.Version, null);
            string folder = Path.GetDirectoryName(UserPath)!;
            Directory.CreateDirectory(folder);
            string temp = UserPath + ".download";
            using (var response = await Http.GetAsync(latest.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(temp);
                var buffer = new byte[1 << 16]; long done = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if ((done += read) > MaxBytes) throw new InvalidOperationException("file troppo grande");
                }
            }
            string sha;
            await using (var check = File.OpenRead(temp)) sha = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (sha != latest.Sha256 || new FileInfo(temp).Length != latest.Size) { File.Delete(temp); return (installed, latest.Version, "impronta diversa: scartato"); }
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            if (Version(temp) is not { } fresh) { File.Delete(temp); return (installed, latest.Version, "il file scaricato non parte"); }
            File.Move(temp, UserPath, overwrite: true);
            Console.Error.WriteLine($"[componenti] yt-dlp aggiornato: {installed ?? "-"} -> {fresh}");
            return (fresh, latest.Version, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            return (installed, null, ex.Message);
        }
        finally { Interlocked.Exchange(ref _updating, 0); }
    }

    /// <summary>All'avvio, al massimo una volta al giorno, in sottofondo e senza chiedere (come Windows).</summary>
    public static void CheckAtStartup()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) return;
        if (long.TryParse(MacSettings.Get("ytDlpLastCheck", "0"), out long t) && DateTime.UtcNow - DateTime.UnixEpoch.AddSeconds(t) < TimeSpan.FromHours(20)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false); // a interfaccia libera
            var (_, _, error) = await UpdateAsync(CancellationToken.None).ConfigureAwait(false);
            string stamp = ((long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds).ToString();
            if (error == null) Avalonia.Threading.Dispatcher.UIThread.Post(() => MacSettings.Set("ytDlpLastCheck", stamp));
            else Console.Error.WriteLine("[componenti] yt-dlp: " + error);
        });
    }
}
