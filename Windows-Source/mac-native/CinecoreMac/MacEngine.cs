using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Il "motore Windows" (madVR, MPC Video Renderer, LAV Filters, XySubFilter sotto Wine ARM64), come i componenti
/// dell'installer di Windows ma scaricato a richiesta: il .dmg resta leggero (mpv basta per film, musica e streaming).
///
/// Il pacchetto (mac/build-engine-pack.sh) sta nelle release GitHub del progetto, in parti da 1,9 GB; le impronte SHA-256
/// sono fissate nell'app (Contents/Resources/engine-manifest.json), quindi si installa solo cio' che e' stato preparato
/// con questa versione. Installato in ~/Library/Application Support/Cinecore/engine: runtime Wine, prefisso (scrivibile:
/// madVR vi salva le impostazioni), VideoHost e ponte VideoToolbox. Il prefisso arriva ripulito: al primo avvio si
/// mettono il nome dell'utente e il percorso del motore al posto dei segnaposto.
///
/// In sviluppo (cartella del progetto con dist/mac/runtime-arm64) si usano le copie del progetto, come prima.
/// </summary>
internal static class MacEngine
{
    public sealed record Part(string Name, long Size, string Sha256);
    public sealed record Manifest(string Version, string BaseUrl, string ArchiveSha256, long ArchiveSize, long InstalledSize, List<Part> Parts);

    private const string WineAppName = "MetalSharp-Wine-IOSurface.app";
    private static readonly HttpClient Http = CreateClient();
    private static CancellationTokenSource? _cts;

    public static event Action? Changed;

    /// <summary>Cartella del motore installato.</summary>
    public static string Dir => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.LocalApplicationData), "Cinecore", "engine");

    private static string DevRuntime => Path.Combine(VideoHostClient.RepoRoot, "dist", "mac", "runtime-arm64");
    /// <summary>Cartella del progetto con il runtime accanto: si usano quelle copie (sviluppo).</summary>
    public static bool Dev => Environment.GetEnvironmentVariable("CINECORE_ENGINE_DEV") != "0" &&
        File.Exists(Path.Combine(DevRuntime, WineAppName, "Contents", "MacOS", "wine"));

    public static string WineApp => Dev ? Path.Combine(DevRuntime, WineAppName) : Path.Combine(Dir, WineAppName);
    public static string Prefix => Dev ? Path.Combine(DevRuntime, "prefix-iosurface") : Path.Combine(Dir, "prefix");
    public static string VtBridge => Dev ? Path.Combine(DevRuntime, "CinecoreVideoToolbox.dll") : Path.Combine(Dir, "CinecoreVideoToolbox.dll");
    public static string VideoHostExe
    {
        get
        {
            string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "VideoHost", "VideoHost.exe"));
            if (File.Exists(bundled)) return bundled;
            return Dev ? Path.Combine(VideoHostClient.RepoRoot, "dist", "mac-native", "videohost-current", "VideoHost.exe")
                : Path.Combine(Dir, "videohost", "VideoHost.exe");
        }
    }

    private static string FirstStartFlag => Path.Combine(Dir, "first-start");
    /// <summary>Il motore non ha ancora mostrato un fotogramma su questo Mac (prima apertura lenta).</summary>
    public static bool FirstStart => !Dev && File.Exists(FirstStartFlag);
    public static void FirstFrameShown() { if (FirstStart) try { File.Delete(FirstStartFlag); } catch (IOException) { } }

    public static bool Installed => File.Exists(Path.Combine(WineApp, "Contents", "MacOS", "wine")) && File.Exists(VideoHostExe) && Directory.Exists(Prefix);
    public static string? InstalledVersion => Dev ? "sviluppo" : File.Exists(Path.Combine(Dir, "engine-version.txt")) ? File.ReadAllText(Path.Combine(Dir, "engine-version.txt")).Trim() : null;

    private static Manifest? _manifest;
    private static bool _manifestRead;
    public static Manifest? Available
    {
        get
        {
            if (_manifestRead) return _manifest;
            _manifestRead = true;
            foreach (string path in new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "engine-manifest.json")),
                Path.Combine(VideoHostClient.RepoRoot, "mac", "engine-manifest.json"),
            })
                try
                {
                    if (!File.Exists(path)) continue;
                    var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (m is { Parts.Count: > 0 } && m.BaseUrl.StartsWith("https://github.com/", StringComparison.Ordinal) &&
                        m.Parts.TrueForAll(p => Regex.IsMatch(p.Sha256, "^[0-9a-f]{64}$") && Regex.IsMatch(p.Name, @"^[\w.\-]+$")))
                    { _manifest = m; break; }
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            return _manifest;
        }
    }

    /// <summary>Si puo' scaricare (c'e' un pacchetto per questa versione dell'app e non e' gia' installato quello).</summary>
    public static bool CanInstall => !Dev && Available is { } m && InstalledVersion != m.Version;
    public static bool Busy => _cts != null;
    public static string State { get; private set; } = "";
    public static double Progress { get; private set; }

    public static string SizeLabel(long bytes) => (bytes / 1e9).ToString(bytes >= 10e9 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture) + " GB";

    /// <summary>Riga delle Impostazioni.</summary>
    public static string Status =>
        Busy ? State
        : Dev ? "Copia del progetto (sviluppo)."
        : Installed && !CanInstall ? $"Installato (versione {InstalledVersion}). Usa {SizeLabel(Available?.InstalledSize ?? 0)} sul disco."
        : Available is { } m ? (Installed ? $"Disponibile la versione {m.Version}. " : "") + $"Da scaricare: {SizeLabel(m.ArchiveSize)} ({SizeLabel(m.InstalledSize)} installato). Senza, i film usano mpv."
        : "Non disponibile per questa versione: i film usano mpv.";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer-Updater");
        return client;
    }

    private static void Report(string state, double progress)
    {
        State = state; Progress = progress;
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    public static void Cancel() => _cts?.Cancel();

    /// <summary>Scarica, verifica e installa il motore in sottofondo (lo stato arriva con <see cref="Changed"/>).</summary>
    public static void StartInstall()
    {
        if (Busy || Available is not { } m || Dev) return;
        var cts = _cts = new CancellationTokenSource();
        Report("Preparazione…", 0);
        _ = Task.Run(async () =>
        {
            string? error = null;
            try { await InstallAsync(m, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { error = "Scaricamento annullato."; }
            catch (Exception ex) { error = "Non riuscito: " + ex.Message; Console.Error.WriteLine("[motore] " + ex); }
            finally
            {
                _cts = null;
                Report(error ?? "", error == null ? 1 : 0);
                if (error != null) Dispatcher.UIThread.Post(() => { State = error; Changed?.Invoke(); });
            }
        });
    }

    private static async Task InstallAsync(Manifest m, CancellationToken ct)
    {
        string root = Path.GetDirectoryName(Dir)!;
        Directory.CreateDirectory(root);
        // Spazio: archivio + installato, con margine.
        long free = new DriveInfo(root).AvailableFreeSpace;
        long need = m.ArchiveSize + m.InstalledSize + (1L << 30);
        if (free < need) throw new IOException($"spazio insufficiente: servono {SizeLabel(need)}, liberi {SizeLabel(free)}");

        string work = Path.Combine(root, "engine-download");
        Directory.CreateDirectory(work);
        string archive = Path.Combine(work, "engine.aar");
        long total = 0; foreach (var p in m.Parts) total += p.Size;
        long done = 0;
        // Parti gia' scaricate e integre (scaricamento ripreso dopo un'interruzione) si tengono.
        foreach (var part in m.Parts)
        {
            ct.ThrowIfCancellationRequested();
            string file = Path.Combine(work, part.Name);
            // Prove: CINECORE_ENGINE_SOURCE = cartella con le parti (al posto di GitHub); le impronte si controllano lo stesso.
            if (Environment.GetEnvironmentVariable("CINECORE_ENGINE_SOURCE") is { Length: > 0 } local && !File.Exists(file))
            {
                Report($"Copio il motore: {SizeLabel(done)} di {SizeLabel(total)}", done / (double)total * 0.8);
                File.Copy(Path.Combine(local, part.Name), file + ".tmp", overwrite: true);
                if (await Sha256Async(file + ".tmp", ct) != part.Sha256) { File.Delete(file + ".tmp"); throw new InvalidDataException("impronta di " + part.Name + " diversa: scartata"); }
                File.Move(file + ".tmp", file);
            }
            if (!(File.Exists(file) && new FileInfo(file).Length == part.Size && await Sha256Async(file, ct) == part.Sha256))
            {
                using var response = await Http.GetAsync(m.BaseUrl + part.Name, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = File.Create(file + ".tmp"))
                {
                    var buffer = new byte[1 << 20]; long got = 0; int read;
                    while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        got += read;
                        if (got > part.Size) throw new InvalidDataException("parte piu' grande del previsto");
                        if ((got & ((16L << 20) - 1)) < read) Report($"Scarico il motore: {SizeLabel(done + got)} di {SizeLabel(total)}", (done + got) / (double)total * 0.8);
                    }
                }
                if (await Sha256Async(file + ".tmp", ct) != part.Sha256) { File.Delete(file + ".tmp"); throw new InvalidDataException("impronta di " + part.Name + " diversa: scartata"); }
                File.Move(file + ".tmp", file, overwrite: true);
            }
            done += part.Size;
        }

        Report("Verifico il pacchetto…", 0.82);
        await using (var output = File.Create(archive))
            foreach (var part in m.Parts)
            {
                await using var input = File.OpenRead(Path.Combine(work, part.Name));
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        if (await Sha256Async(archive, ct) != m.ArchiveSha256) throw new InvalidDataException("impronta del pacchetto diversa");

        Report("Installo il motore…", 0.88);
        string staging = Path.Combine(root, "engine-new");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var (exit, _, stderr) = ProcessLauncher.Run("/usr/bin/aa", new[] { "extract", "-i", archive, "-d", staging }, true, 30 * 60_000, ct);
        if (exit != 0) throw new IOException("estrazione non riuscita: " + stderr.Trim());
        string extracted = Path.Combine(staging, "engine");
        if (!File.Exists(Path.Combine(extracted, WineAppName, "Contents", "MacOS", "wine"))) throw new InvalidDataException("pacchetto incompleto");

        Report("Adatto il motore a questo Mac…", 0.95);
        Personalize(extracted, Dir);
        // Al posto del vecchio solo a lavoro finito; il vecchio prefisso (impostazioni di madVR) non si conserva fra versioni diverse.
        VideoHostClient.StopWine();
        if (Directory.Exists(Dir)) Directory.Move(Dir, Dir + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        Directory.Move(extracted, Dir);
        try { Directory.Delete(staging, true); } catch { }
        try { Directory.Delete(work, true); } catch { }
        foreach (string old in Directory.GetDirectories(root, "engine.old-*")) try { Directory.Delete(old, true); } catch { }
        // Primo avvio di Wine sul prefisso nuovo (registro, servizi, font): subito, non al primo film.
        Report("Preparo il motore (primo avvio di Wine)…", 0.98);
        try
        {
            var warm = VideoHostClient.WineStartInfo();
            warm.RedirectStandardOutput = warm.RedirectStandardError = false;
            foreach (string a in new[] { "cmd", "/c", "exit" }) warm.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(warm);
            p?.WaitForExit(180_000);
        }
        catch (Exception ex) { Console.Error.WriteLine("[motore] primo avvio di Wine: " + ex.Message); }
        // Il primo film prepara anche gli shader di madVR: piu' tempo prima di dichiarare l'apertura fallita.
        File.WriteAllText(FirstStartFlag, DateTime.UtcNow.ToString("o"));
        Console.Error.WriteLine("[motore] installato " + m.Version + " in " + Dir);
    }

    /// <summary>
    /// Segnaposto del prefisso ripulito: @@USER@@ -> utente del Mac (profilo di Windows), @@ENGINE@@ -> cartella del motore
    /// vista da Wine (Z:\Users\…\engine). Il motore va installato in <paramref name="finalDir"/>: i percorsi puntano li'.
    /// </summary>
    internal static void Personalize(string engineDir, string finalDir)
    {
        string prefix = Path.Combine(engineDir, "prefix");
        string user = Regex.Replace(Environment.UserName, @"[^\w.\-]", "_");
        if (user.Length == 0) user = "cinecore";
        string users = Path.Combine(prefix, "drive_c", "users");
        if (Directory.Exists(Path.Combine(users, "@@USER@@")) && !Directory.Exists(Path.Combine(users, user)))
            Directory.Move(Path.Combine(users, "@@USER@@"), Path.Combine(users, user));
        string wineEngine = ("Z:" + finalDir.Replace('/', '\\')).Replace("\\", "\\\\");
        foreach (string name in new[] { "system.reg", "user.reg", "userdef.reg" })
        {
            string file = Path.Combine(prefix, name);
            if (!File.Exists(file)) continue;
            // Testo del registro di Wine: si lavora sui byte come Latin-1 per non toccare il resto.
            string text = File.ReadAllText(file, Encoding.Latin1);
            text = text.Replace("@@ENGINE@@", wineEngine).Replace("@@USER@@", user);
            File.WriteAllText(file, text, Encoding.Latin1);
        }
    }

    private static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
