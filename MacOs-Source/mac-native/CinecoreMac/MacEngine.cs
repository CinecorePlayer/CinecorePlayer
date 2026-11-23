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
/// dell'installer di Windows: il .dmg ha solo i pezzi nostri, il resto si scarica dai siti ufficiali al primo avvio.
///
/// Dai siti ufficiali (impronte SHA-256 fissate nel manifesto, Contents/Resources/Engine/engine-manifest.json):
///   - MetalSharp Wine 6.0 ARM64, dalla release v6.0 di github.com/metalsharp/MetalSharp-Wine;
///   - madVR da madshi.net, MPC Video Renderer, LAV Filters e XySubFilter dalle release GitHub degli autori.
/// Pezzi nostri, dentro l'app (Contents/Resources/Engine, mac/build-engine-overlay.sh): le librerie di Wine ricompilate
/// con le nostre patch (wined3d, winemac), quartz.dll, le impostazioni di madVR/LAV/MPC-VR/XySubFilter, il ponte
/// VideoToolbox. Il prefisso di Windows lo crea Wine stesso (wineboot) su questo Mac; poi si registrano i filtri.
/// Tutto in ~/Library/Application Support/Cinecore/engine.
///
/// In sviluppo (cartella del progetto con dist/mac/runtime-arm64) si usano le copie del progetto, come prima.
/// </summary>
internal static class MacEngine
{
    public sealed record Runtime(string Name, string Version, string Url, string Sha256, long Size, string App);
    public sealed record Component(string Name, string Version, string Url, string Sha256, long Size, List<ComponentFile> Files);
    public sealed record ComponentFile(string Zip, string Path, string Sha256, long Size);
    public sealed record OverlayFile(string Path, string Sha256, long Size);
    public sealed record Manifest(string Version, Runtime Runtime, List<Component> Components, List<OverlayFile> Overlay,
        List<string> Register, long InstalledSize);

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
            // Fuori dal pacchetto (prove dal progetto, anche con il motore installato): la copia del progetto.
            string project = Path.Combine(VideoHostClient.RepoRoot, "dist", "mac-native", "videohost-current", "VideoHost.exe");
            return Dev || File.Exists(project) ? project : Path.Combine(Dir, "videohost", "VideoHost.exe");
        }
    }

    /// <summary>I pezzi nostri del motore: nell'app, oppure (prove dal progetto) in dist/engine-pack/overlay.</summary>
    private static string OverlayDir
    {
        get
        {
            // Prove: CINECORE_ENGINE_OVERLAY = la cartella Engine di un pacchetto costruito.
            if (Environment.GetEnvironmentVariable("CINECORE_ENGINE_OVERLAY") is { Length: > 0 } forced) return forced;
            string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Engine"));
            return Directory.Exists(bundled) ? bundled : Path.Combine(VideoHostClient.RepoRoot, "dist", "engine-pack", "overlay");
        }
    }

    /// <summary>Un pezzo nostro nella cartella dei pezzi: nell'app la parte di Wine sta in "wine-overlay" (non ".app").</summary>
    private static string OverlaySource(string path)
    {
        string direct = Path.Combine(OverlayDir, path);
        if (File.Exists(direct) || !path.StartsWith(WineAppName + "/", StringComparison.Ordinal)) return direct;
        return Path.Combine(OverlayDir, "wine-overlay", path[(WineAppName.Length + 1)..]);
    }

    private static string FirstStartFlag => Path.Combine(Dir, "first-start");
    /// <summary>Il motore non ha ancora mostrato un fotogramma su questo Mac (prima apertura lenta).</summary>
    public static bool FirstStart => !Dev && File.Exists(FirstStartFlag);
    public static void FirstFrameShown() { if (FirstStart) try { File.Delete(FirstStartFlag); } catch (IOException) { } }

    public static bool Installed => File.Exists(Path.Combine(WineApp, "Contents", "MacOS", "wine")) && File.Exists(VideoHostExe) && Directory.Exists(Prefix);
    public static string? InstalledVersion => Dev ? "sviluppo" : File.Exists(Path.Combine(Dir, "engine-version.txt")) ? File.ReadAllText(Path.Combine(Dir, "engine-version.txt")).Trim() : null;

    private static bool Sha(string s) => Regex.IsMatch(s, "^[0-9a-f]{64}$");
    private static bool SafePath(string p) => p.Length > 0 && !p.StartsWith('/') && !p.Contains("..", StringComparison.Ordinal);

    /// <summary>Solo siti degli autori, in https, e solo percorsi dentro il motore.</summary>
    private static bool Valid(Manifest m) =>
        Uri.TryCreate(m.Runtime.Url, UriKind.Absolute, out var r) && r.Scheme == "https" && r.Host == "github.com" &&
        r.AbsolutePath.StartsWith("/metalsharp/MetalSharp-Wine/releases/download/", StringComparison.Ordinal) && Sha(m.Runtime.Sha256) &&
        Regex.IsMatch(m.Runtime.App, @"^[\w .\-]+\.app$") &&
        m.Components.TrueForAll(c => Uri.TryCreate(c.Url, UriKind.Absolute, out var u) && u.Scheme == "https" &&
            (u.Host is "www.madshi.net" or "madshi.net" || (u.Host == "github.com" && u.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal))) &&
            Sha(c.Sha256) && c.Files.Count > 0 &&
            c.Files.TrueForAll(f => f.Path.StartsWith("drive_c/Cinecore/", StringComparison.Ordinal) && SafePath(f.Path) && Sha(f.Sha256))) &&
        m.Overlay.TrueForAll(o => SafePath(o.Path) && Sha(o.Sha256)) &&
        m.Register.TrueForAll(p => p.StartsWith("drive_c/Cinecore/", StringComparison.Ordinal) && SafePath(p));

    private static Manifest? _manifest;
    private static bool _manifestRead;
    public static Manifest? Available
    {
        get
        {
            if (_manifestRead) return _manifest;
            _manifestRead = true;
            foreach (string path in new[] { Path.Combine(OverlayDir, "engine-manifest.json"), Path.Combine(VideoHostClient.RepoRoot, "mac", "engine-manifest.json") })
                try
                {
                    if (!File.Exists(path)) continue;
                    var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (m?.Runtime != null && m.Components != null && m.Overlay != null && m.Register != null && Valid(m)) { _manifest = m; break; }
                    Console.Error.WriteLine("[motore] manifesto non valido: " + path);
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            return _manifest;
        }
    }

    /// <summary>Si puo' installare (c'e' un manifesto per questa versione dell'app e non e' gia' installato quello).</summary>
    public static bool CanInstall => !Dev && Available is { } m && InstalledVersion != m.Version;
    public static bool Busy => _cts != null;
    public static string State { get; private set; } = "";
    public static double Progress { get; private set; }

    public static string SizeLabel(long bytes) => (bytes / 1e9).ToString(bytes >= 10e9 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture) + " GB";

    /// <summary>Riga delle Impostazioni.</summary>
    public static string Status =>
        Busy ? State
        : Dev ? "Installato e pronto: madVR, MPC Video Renderer e LAV."
        : Installed && !CanInstall ? $"Installato (versione {InstalledVersion}). Usa {SizeLabel(Available?.InstalledSize ?? 0)} sul disco."
        : Available is { } m ? (Installed ? $"Disponibile la versione {m.Version}. " : "") + $"Da scaricare dai siti ufficiali: {SizeLabel(DownloadSize(m))} ({SizeLabel(m.InstalledSize)} installato). Senza, i film usano mpv."
        : "Non disponibile per questa versione: i film usano mpv.";

    private static long DownloadSize(Manifest m)
    {
        long size = m.Runtime.Size;
        foreach (var c in m.Components) size += c.Size;
        return size;
    }

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
        // Wine, come Windows, non regge percorsi oltre ~240 caratteri per le sue DLL: wineboot fallisce e il prefisso resta
        // vuoto. Di serie (~/Library/Application Support/Cinecore/engine) sono circa 120.
        // Misurato il 20/11: cartella delle DLL di 223 caratteri ok, di 244 wineboot fallisce.
        if (Path.Combine(root, "engine-new", WineAppName, "Contents", "lib", "wine", "aarch64-windows").Length > 225)
            throw new IOException("la cartella del motore ha un percorso troppo lungo per Wine: " + root);
        // Spazio: archivi + installato, con margine.
        long free = new DriveInfo(root).AvailableFreeSpace;
        long need = DownloadSize(m) + m.InstalledSize + (1L << 30);
        if (free < need) throw new IOException($"spazio insufficiente: servono {SizeLabel(need)}, liberi {SizeLabel(free)}");

        // I pezzi nostri prima di tutto: se mancano o sono diversi dal manifesto non si scarica nulla.
        foreach (var o in m.Overlay)
        {
            string file = OverlaySource(o.Path);
            if (!File.Exists(file) || await Sha256Async(file, ct) != o.Sha256) throw new InvalidDataException("pezzo del motore mancante o diverso: " + o.Path);
        }

        string work = Path.Combine(root, "engine-download");
        Directory.CreateDirectory(work);
        long total = DownloadSize(m), done = 0;

        // 1. Wine (MetalSharp Wine, dalla sua release ufficiale) e i filtri, dai siti degli autori.
        string runtimeZip = await FetchAsync(m.Runtime.Name + " " + m.Runtime.Version, m.Runtime.Url, m.Runtime.Sha256, m.Runtime.Size, work, done, total, ct).ConfigureAwait(false);
        done += m.Runtime.Size;
        var archives = new Dictionary<Component, string>();
        foreach (var c in m.Components)
        {
            archives[c] = await FetchAsync(c.Name + " " + c.Version, c.Url, c.Sha256, c.Size, work, done, total, ct).ConfigureAwait(false);
            done += c.Size;
        }

        string staging = Path.Combine(root, "engine-new");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        // 2. Wine estratto e completato con le nostre librerie (le patch per madVR/MPC-VR), poi firmato di nuovo.
        Report("Preparo Wine…", 0.82);
        string unzip = Path.Combine(staging, "unzip");
        Run("/usr/bin/ditto", new[] { "-x", "-k", runtimeZip, unzip }, 30 * 60_000, ct, "estrazione di Wine");
        string app = Path.Combine(unzip, m.Runtime.App);
        if (!File.Exists(Path.Combine(app, "Contents", "MacOS", "wine"))) throw new InvalidDataException("archivio di Wine diverso dal previsto");
        string wineApp = Path.Combine(staging, WineAppName);
        Directory.Move(app, wineApp);
        Directory.Delete(unzip, true);
        string prefix = Path.Combine(staging, "prefix");
        foreach (var o in m.Overlay)
        {
            if (o.Path.StartsWith("prefix/", StringComparison.Ordinal) || o.Path.EndsWith(".reg", StringComparison.Ordinal)) continue;
            CopyChecked(OverlaySource(o.Path), Path.Combine(staging, o.Path), o.Sha256);
        }
        // Dieta (EngineSlim): via i simboli di debug delle DLL, Mono, Gecko e le parti di MetalSharp per i giochi.
        Report("Alleggerisco Wine…", 0.85);
        Console.Error.WriteLine($"[motore] Wine alleggerito di {SizeLabel(EngineSlim.SlimWine(wineApp))}");
        foreach (var o in m.Overlay)
            if (o.Path.StartsWith(WineAppName + "/", StringComparison.Ordinal) && o.Path.EndsWith(".so", StringComparison.Ordinal))
                Run("/usr/bin/codesign", new[] { "--force", "--sign", "-", Path.Combine(staging, o.Path) }, 120_000, ct, "firma di " + Path.GetFileName(o.Path));
        Run("/usr/bin/codesign", new[] { "--force", "--deep", "--sign", "-", wineApp }, 20 * 60_000, ct, "firma di Wine");

        // 3. Il prefisso di Windows lo crea Wine (niente Mono ne' Gecko: al VideoHost non servono).
        Report("Creo il prefisso di Windows…", 0.88);
        Wine(wineApp, prefix, ct, "wineboot", "-i");
        WaitWineServer(wineApp, prefix, ct);
        // Cartelle dell'utente (Scrivania, Documenti…) collegate al Mac: vuote, come nel motore provato.
        string users = Path.Combine(prefix, "drive_c", "users");
        if (Directory.Exists(users))
            foreach (string user in Directory.GetDirectories(users))
                foreach (string entry in Directory.GetFileSystemEntries(user))
                    if (new FileInfo(entry).LinkTarget != null) { File.Delete(entry); Directory.CreateDirectory(entry); }

        // 4. Filtri dagli archivi ufficiali e pezzi nostri del prefisso (quartz.dll, impostazioni di madVR).
        Report("Installo madVR, MPC Video Renderer, LAV e XySubFilter…", 0.93);
        foreach (var c in m.Components) PlaceComponent(c, archives[c], prefix);
        foreach (var o in m.Overlay)
            if (o.Path.StartsWith("prefix/", StringComparison.Ordinal))
                CopyChecked(OverlaySource(o.Path), Path.Combine(staging, o.Path), o.Sha256);

        // 5. Registrazione dei filtri e impostazioni (come nel motore provato).
        Report("Registro i filtri…", 0.96);
        foreach (string filter in m.Register)
            Wine(wineApp, prefix, ct, "regsvr32", "/s", "C:\\" + filter["drive_c/".Length..].Replace('/', '\\'));
        foreach (var o in m.Overlay)
            if (o.Path.EndsWith(".reg", StringComparison.Ordinal))
                Wine(wineApp, prefix, ct, "regedit", "/S", "Z:" + OverlaySource(o.Path).Replace('/', '\\'));
        WaitWineServer(wineApp, prefix, ct);
        // Le DLL che wineboot ha copiato nel prefisso, se identiche a quelle di Wine: un file solo sul disco.
        Console.Error.WriteLine($"[motore] prefisso: {SizeLabel(EngineSlim.LinkPrefixDlls(wineApp, prefix))} in collegamenti");
        File.WriteAllText(Path.Combine(staging, "engine-version.txt"), m.Version + "\n");

        // 6. Al posto del vecchio solo a lavoro finito. Il prefisso nato in engine-new: i percorsi diventano quelli finali.
        Report("Completo l'installazione…", 0.98);
        VideoHostClient.StopWine();
        RetargetPrefix(prefix, staging, Dir);
        if (Directory.Exists(Dir)) Directory.Move(Dir, Dir + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        Directory.Move(staging, Dir);
        try { Directory.Delete(work, true); } catch { }
        foreach (string old in Directory.GetDirectories(root, "engine.old-*")) try { Directory.Delete(old, true); } catch { }
        // Il primo film prepara anche gli shader di madVR: piu' tempo prima di dichiarare l'apertura fallita.
        File.WriteAllText(FirstStartFlag, DateTime.UtcNow.ToString("o"));
        Console.Error.WriteLine("[motore] installato " + m.Version + " in " + Dir);
    }

    /// <summary>Un archivio dal sito ufficiale, verificato con l'impronta del manifesto (ripreso se gia' scaricato e integro).</summary>
    private static async Task<string> FetchAsync(string label, string url, string sha256, long size, string work, long done, long total, CancellationToken ct)
    {
        string file = Path.Combine(work, sha256 + Path.GetExtension(new Uri(url).AbsolutePath));
        if (File.Exists(file) && await Sha256Async(file, ct) == sha256) return file;
        // Prove: CINECORE_ENGINE_SOURCE = cartella con gli archivi gia' scaricati (stesso nome del file nell'indirizzo);
        // l'impronta si controlla lo stesso.
        string? local = Environment.GetEnvironmentVariable("CINECORE_ENGINE_SOURCE") is { Length: > 0 } dir ? Path.Combine(dir, Path.GetFileName(new Uri(url).AbsolutePath)) : null;
        if (local != null && File.Exists(local))
        {
            Report($"Copio {label}…", done / (double)total * 0.8);
            File.Copy(local, file + ".tmp", overwrite: true);
        }
        else
        {
            Report($"Scarico {label} dal sito ufficiale…", done / (double)total * 0.8);
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(file + ".tmp");
            var buffer = new byte[1 << 20]; long got = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                got += read;
                if (got > size + (1 << 20)) throw new InvalidDataException(label + ": archivio piu' grande del previsto");
                if ((got & ((16L << 20) - 1)) < read)
                    Report($"Scarico {label}: {SizeLabel(done + got)} di {SizeLabel(total)}", (done + got) / (double)total * 0.8);
            }
        }
        if (await Sha256Async(file + ".tmp", ct) != sha256)
        {
            File.Delete(file + ".tmp");
            // L'autore ha pubblicato un archivio diverso allo stesso indirizzo: non si installa un file non verificato.
            throw new InvalidDataException($"{label}: l'archivio sul sito ufficiale e' cambiato (impronta diversa), serve un aggiornamento di Cinecore");
        }
        File.Move(file + ".tmp", file, overwrite: true);
        return file;
    }

    /// <summary>I file dell'archivio ufficiale al loro posto nel prefisso, ognuno con la sua impronta.</summary>
    private static void PlaceComponent(Component c, string archive, string prefix)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        foreach (var f in c.Files)
        {
            var entry = zip.GetEntry(f.Zip) ?? throw new InvalidDataException($"{c.Name}: manca {f.Zip} nell'archivio");
            string target = Inside(prefix, f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = File.Create(target))
                input.CopyTo(output);
            if (FileSha(target) != f.Sha256) throw new InvalidDataException($"{c.Name}: {f.Zip} diverso dal previsto");
        }
        Console.Error.WriteLine($"[motore] {c.Name} {c.Version}: {c.Files.Count} file dal sito ufficiale");
    }

    private static void CopyChecked(string source, string target, string sha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        if (FileSha(target) != sha256) throw new InvalidDataException("pezzo del motore diverso dal previsto: " + Path.GetFileName(target));
    }

    private static string Inside(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("percorso non valido: " + relative);
        return full;
    }

    private static void Run(string exe, IEnumerable<string> args, int timeoutMs, CancellationToken ct, string what)
    {
        var (exit, _, stderr) = ProcessLauncher.Run(exe, args, true, timeoutMs, ct);
        if (exit != 0) throw new IOException($"{what} non riuscita: {stderr.Trim()}");
    }

    /// <summary>Un comando di Wine sul prefisso nuovo (senza Mono ne' Gecko, messaggi spenti).</summary>
    private static void Wine(string wineApp, string prefix, CancellationToken ct, params string[] args)
    {
        var all = new List<string> { "WINEPREFIX=" + prefix, "WINEDEBUG=-all", "WINEDLLOVERRIDES=mscoree,mshtml=", Path.Combine(wineApp, "Contents", "MacOS", "wine") };
        all.AddRange(args);
        var (exit, _, stderr) = ProcessLauncher.Run("/usr/bin/env", all, true, 10 * 60_000, ct);
        if (exit != 0) throw new IOException($"wine {args[0]} non riuscito ({exit}): {stderr.Trim()[..Math.Min(300, stderr.Trim().Length)]}");
    }

    private static void WaitWineServer(string wineApp, string prefix, CancellationToken ct) =>
        ProcessLauncher.Run("/usr/bin/env", new[] { "WINEPREFIX=" + prefix, Path.Combine(wineApp, "Contents", "MacOS", "wineserver"), "-w" }, true, 5 * 60_000, ct);

    /// <summary>Il prefisso nasce in engine-new: dove il registro nomina quella cartella si mette quella finale.</summary>
    private static void RetargetPrefix(string prefix, string from, string to)
    {
        string winFrom = ("Z:" + from.Replace('/', '\\')).Replace("\\", "\\\\"), winTo = ("Z:" + to.Replace('/', '\\')).Replace("\\", "\\\\");
        foreach (string name in new[] { "system.reg", "user.reg", "userdef.reg" })
        {
            string file = Path.Combine(prefix, name);
            if (!File.Exists(file)) continue;
            string text = File.ReadAllText(file, Encoding.Latin1);
            string updated = text.Replace(winFrom, winTo).Replace(from, to);
            if (!ReferenceEquals(text, updated) && text != updated) File.WriteAllText(file, updated, Encoding.Latin1);
        }
    }

    private static string FileSha(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
