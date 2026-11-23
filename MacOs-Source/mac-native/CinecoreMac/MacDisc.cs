using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CinecoreMac;

internal enum DiscKind { None, Dvd, Bluray }

/// <summary>
/// Dischi video (DiscMedia di Windows): cartella con BDMV o VIDEO_TS, disco inserito (/Volumes) o immagine ISO, montata da
/// macOS in sola lettura (hdiutil) cosi' entrambe le vie la leggono come una cartella. Doppia via, come chiesto:
/// - MPV: Blu-ray con libbluray (bd://), DVD con i VOB del titolo principale uniti (lavf://concat:, niente menu);
/// - madVR / MPC-VR (VideoHost): Blu-ray con LAV Splitter dal suo index.bdmv (come Windows), DVD con gli stessi VOB uniti.
/// </summary>
internal static class MacDisc
{
    public sealed record Source(DiscKind Kind, string Root, string? Image)
    {
        public string Label => LabelAsTitle(Image != null ? Path.GetFileNameWithoutExtension(Image) : Path.GetFileName(Root.TrimEnd('/')));
    }

    /// <summary>L'etichetta del disco scritta come un titolo, come su Windows: "THE_LEGO_MOVIE_2" diventa "The Lego Movie 2".</summary>
    public static string LabelAsTitle(string label)
    {
        label = label.Replace('_', ' ').Trim();
        while (label.Contains("  ")) label = label.Replace("  ", " ");
        if (label.Length > 0 && label == label.ToUpperInvariant())
            label = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label.ToLowerInvariant());
        return label;
    }

    // ISO montate da noi per questa sessione: immagine -> punto di montaggio.
    private static readonly Dictionary<string, string> Mounts = new(StringComparer.Ordinal);

    public static bool TryResolve(string? path, out Source source)
    {
        source = null!;
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal)) return false;
        try
        {
            if (Directory.Exists(path)) return FromFolder(path, null, out source);
            if (!File.Exists(path)) return false;
            string name = Path.GetFileName(path);
            string? folder = Path.GetDirectoryName(path);
            if (folder != null && (name.Equals("index.bdmv", StringComparison.OrdinalIgnoreCase) || name.Equals("MovieObject.bdmv", StringComparison.OrdinalIgnoreCase) ||
                                   name.Equals("VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase)))
                return FromFolder(folder, null, out source);
            if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && Mount(path) is { } mounted)
                return FromFolder(mounted, path, out source);
        }
        catch (Exception ex) { Console.Error.WriteLine("[DISC] " + ex.Message); }
        return false;
    }

    private static bool FromFolder(string folder, string? image, out Source source)
    {
        source = null!;
        string full = Path.GetFullPath(folder).TrimEnd('/');
        string leaf = Path.GetFileName(full);
        string root = (leaf.Equals("BDMV", StringComparison.OrdinalIgnoreCase) || leaf.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            && Path.GetDirectoryName(full) is { } parent ? parent : full;
        if (File.Exists(Path.Combine(root, "BDMV", "index.bdmv"))) { source = new Source(DiscKind.Bluray, root, image); return true; }
        if (File.Exists(Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO"))) { source = new Source(DiscKind.Dvd, root, image); return true; }
        return false;
    }

    /// <summary>L'immagine montata in sola lettura e senza comparire nel Finder (hdiutil attach -nobrowse).</summary>
    private static string? Mount(string iso)
    {
        lock (Mounts) if (Mounts.TryGetValue(iso, out var known) && Directory.Exists(known)) return known;
        var (exit, stdout, _) = ProcessLauncher.Run("/usr/bin/hdiutil", new[] { "attach", "-readonly", "-nobrowse", "-noverify", "-noautoopen", iso }, false, 60000, CancellationToken.None);
        if (exit != 0) return null;
        // Ultima colonna delle righe: il punto di montaggio (/Volumes/...).
        string? point = Encoding.UTF8.GetString(stdout).Split('\n').Select(l => l.Split('\t').Last().Trim()).LastOrDefault(l => l.StartsWith("/Volumes/", StringComparison.Ordinal));
        if (point != null) lock (Mounts) Mounts[iso] = point;
        return point;
    }

    /// <summary>Smonta le immagini montate da noi, tranne quella in uso.</summary>
    public static void ReleaseMounts(string? keepImage = null)
    {
        List<(string Image, string Point)> list;
        lock (Mounts) list = Mounts.Where(m => m.Key != keepImage).Select(m => (m.Key, m.Value)).ToList();
        foreach (var (image, point) in list)
        {
            MacDiscStream.CloseTitles(point);
            try { ProcessLauncher.Run("/usr/bin/hdiutil", new[] { "detach", point, "-quiet" }, false, 30000, CancellationToken.None); } catch { }
            lock (Mounts) Mounts.Remove(image);
        }
    }

    /// <summary>VOB del titolo principale: il gruppo VTS_NN con piu' dati (i _0 sono i menu).</summary>
    public static List<string> MainTitleVobs(Source source)
    {
        if (ChosenTitle(source) is { } chosen && VobGroups(source).TryGetValue(chosen, out var picked)) return picked;
        var folder = Path.Combine(source.Root, "VIDEO_TS");
        var groups = new Dictionary<string, List<FileInfo>>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.VOB", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
        {
            var m = Regex.Match(file.Name, @"^VTS_(\d\d)_([1-9])\.VOB$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            if (!groups.TryGetValue(m.Groups[1].Value, out var list)) groups[m.Groups[1].Value] = list = new();
            list.Add(file);
        }
        var best = groups.Values.OrderByDescending(g => g.Sum(f => f.Length)).FirstOrDefault() ?? new();
        return best.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => f.FullName).ToList();
    }

    // ----- Titoli del disco (menu "Titoli del disco" di Windows) -----
    public sealed record Title(string Id, string Label, double Seconds);
    // Titolo scelto per disco (radice -> id); senza scelta si usa il principale.
    private static readonly Dictionary<string, string> Chosen = new(StringComparer.Ordinal);

    public static void ChooseTitle(Source source, string? id) { lock (Chosen) { if (id == null) Chosen.Remove(source.Root); else Chosen[source.Root] = id; } }
    public static string? ChosenTitle(Source source) { lock (Chosen) return Chosen.TryGetValue(source.Root, out var id) ? id : null; }

    /// <summary>DVD: i gruppi VTS_NN (con la durata, da ffprobe). Blu-ray: le playlist .mpls con almeno un minuto.</summary>
    public static List<Title> Titles(Source source)
    {
        var list = new List<Title>();
        try
        {
            if (source.Kind == DiscKind.Dvd)
            {
                foreach (var group in VobGroups(source).OrderBy(g => g.Key))
                {
                    double seconds = ProbeSeconds("concat:" + string.Join("|", group.Value));
                    if (seconds < 20) continue; // menu, avvisi
                    list.Add(new Title(group.Key, $"Titolo {int.Parse(group.Key)}", seconds));
                }
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(source.Root, "BDMV", "PLAYLIST"), "*.mpls").OrderBy(f => f))
                {
                    double seconds = MplsSeconds(file);
                    if (seconds < 60) continue;
                    string id = Path.GetFileNameWithoutExtension(file);
                    list.Add(new Title(id, "Playlist " + id, seconds));
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[DISC] titoli: " + ex.Message); }
        return list;
    }

    private static Dictionary<string, List<string>> VobGroups(Source source)
    {
        var groups = new Dictionary<string, List<string>>();
        foreach (var file in new DirectoryInfo(Path.Combine(source.Root, "VIDEO_TS")).EnumerateFiles("*.VOB", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var m = Regex.Match(file.Name, @"^VTS_(\d\d)_([1-9])\.VOB$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            if (!groups.TryGetValue(m.Groups[1].Value, out var list)) groups[m.Groups[1].Value] = list = new();
            list.Add(file.FullName);
        }
        return groups;
    }

    private static double ProbeSeconds(string target)
    {
        if (MacTools.Ffprobe is not { } ffprobe) return 0;
        var (_, stdout, _) = ProcessLauncher.Run(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", target }, true, 20000, CancellationToken.None);
        return double.TryParse(Encoding.UTF8.GetString(stdout).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
    }

    /// <summary>Durata di una playlist Blu-ray: somma dei PlayItem (IN/OUT a 45 kHz), come in libbluray.</summary>
    private static double MplsSeconds(string file)
    {
        byte[] d = File.ReadAllBytes(file);
        if (d.Length < 20 || d[0] != 'M' || d[1] != 'P' || d[2] != 'L' || d[3] != 'S') return 0;
        int start = (d[8] << 24) | (d[9] << 16) | (d[10] << 8) | d[11];
        if (start + 10 > d.Length) return 0;
        int items = (d[start + 6] << 8) | d[start + 7], pos = start + 10;
        double total = 0;
        for (int i = 0; i < items && pos + 22 <= d.Length; i++)
        {
            int length = (d[pos] << 8) | d[pos + 1];
            uint inTime = (uint)((d[pos + 14] << 24) | (d[pos + 15] << 16) | (d[pos + 16] << 8) | d[pos + 17]);
            uint outTime = (uint)((d[pos + 18] << 24) | (d[pos + 19] << 16) | (d[pos + 20] << 8) | d[pos + 21]);
            if (outTime > inTime) total += (outTime - inTime) / 45000.0;
            pos += 2 + length;
        }
        return total;
    }

    public static string? LargestM2ts(Source source)
    {
        try { return new DirectoryInfo(Path.Combine(source.Root, "BDMV", "STREAM")).EnumerateFiles("*.m2ts").OrderByDescending(f => f.Length).FirstOrDefault()?.FullName; }
        catch { return null; }
    }

    /// <summary>Indirizzo per MPV: "bd://" con bluray-device (vedi MpvEngine) o i VOB uniti.</summary>
    public static string MpvUrl(Source source) => source.Kind == DiscKind.Bluray
        ? (ChosenTitle(source) is { } mpls ? "cinecore-bd:" + source.Root + "|mpls/" + int.Parse(mpls) : "cinecore-bd:" + source.Root)
        : DvdTitleUrl(source) ?? "lavf://concat:" + string.Join("|", MainTitleVobs(source));

    /// <summary>Il titolo scelto del DVD letto con libdvdread (anche protetto), servito in locale a tutti i motori.</summary>
    public static string? DvdTitleUrl(Source source)
    {
        if (source.Kind == DiscKind.Bluray) return null;
        var vobs = MainTitleVobs(source);
        // Solo per i dischi cifrati: aprendo il disco libdvdread recupera tutte le chiavi CSS (~4 s in piu' all'avvio).
        if (vobs.Count == 0 || !IsScrambled(vobs[0])) return null;
        return MacDiscStream.UrlForTitle(source.Root, MacDvdRead.TitleSetOf(vobs[0]));
    }

    private static readonly Dictionary<string, bool> ScrambledCache = new(StringComparer.Ordinal);

    /// <summary>DVD protetto (CSS)? Il bit di cifratura dei pacchetti MPEG in un tratto del primo VOB del titolo.</summary>
    public static bool IsScrambled(string vob)
    {
        lock (ScrambledCache) if (ScrambledCache.TryGetValue(vob, out bool known)) return known;
        bool scrambled = false;
        try
        {
            using var f = new FileStream(vob, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var sector = new byte[2048];
            // Tre punti del file: l'inizio a volte e' in chiaro anche sui dischi protetti.
            foreach (double at in new[] { 0.1, 0.4, 0.7 })
            {
                f.Position = (long)(f.Length * at) / 2048 * 2048;
                for (int i = 0; i < 32 && !scrambled; i++)
                {
                    if (f.Read(sector, 0, 2048) < 2048) break;
                    if (sector[0] == 0 && sector[1] == 0 && sector[2] == 1 && sector[3] == 0xBA && (sector[0x14] & 0x30) != 0) scrambled = true;
                }
                if (scrambled) break;
            }
        }
        // Un VOB che non si legge dal file system e' tipico dei dischi protetti non ancora autenticati.
        catch (IOException) { scrambled = true; }
        catch { }
        lock (ScrambledCache) ScrambledCache[vob] = scrambled;
        Console.Error.WriteLine($"[disco] {Path.GetFileName(vob)}: {(scrambled ? "protetto (CSS), lettura con libdvdread" : "in chiaro")}");
        return scrambled;
    }

    /// <summary>Indirizzo per il VideoHost (LAV Splitter): index.bdmv come Windows, o i VOB uniti dal protocollo concat.</summary>
    /// <summary>
    /// Indirizzo per il VideoHost (LAV Splitter): index.bdmv come Windows per il Blu-ray; per il DVD i VOB del titolo
    /// principale uniti da MacDiscStream (LAV apre un VOB alla volta e non accetta concat:).
    /// </summary>
    public static string VideoHostPath(Source source)
    {
        if (source.Kind == DiscKind.Bluray)
            return VideoHostClient.ToWinePath(ChosenTitle(source) is { } mpls
                ? Path.Combine(source.Root, "BDMV", "PLAYLIST", mpls + ".mpls")   // LAV apre anche una playlist precisa
                : Path.Combine(source.Root, "BDMV", "index.bdmv"));
        if (DvdTitleUrl(source) is { } title) return title;
        var vobs = MainTitleVobs(source);
        return vobs.Count switch
        {
            0 => VideoHostClient.ToWinePath(source.Root),
            1 => VideoHostClient.ToWinePath(vobs[0]),
            _ => MacDiscStream.UrlFor(vobs),
        };
    }

    /// <summary>
    /// Audio affiancato (mpv nativo) con madVR / MPC-VR: la stessa sorgente del VideoHost letta dal Mac. Blu-ray con
    /// libbluray (stessa playlist), DVD dallo stesso indirizzo locale o dallo stesso VOB. Serve all'audio spaziale
    /// (DTS:X 7.1.4, oggetti TrueHD Atmos) e al bitstream anche dai dischi: prima l'audio dei dischi restava a Wine.
    /// </summary>
    public static string? NativeAudioSource(Source source, string hostPath, int featureSet)
    {
        if (source.Kind == DiscKind.Bluray) return MpvUrl(source);
        if (hostPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return hostPath;
        var vobs = featureSet > 0 && VobGroups(source).TryGetValue(featureSet.ToString("00"), out var list) ? list : MainTitleVobs(source);
        return vobs.Count == 1 ? vobs[0] : null;
    }

    /// <summary>Per ffprobe (informazioni, durata): i VOB uniti o il flusso piu' grande del Blu-ray.</summary>
    /// <summary>Un gruppo di titoli del DVD (VTS_xx) per il VideoHost: il film scelto nei menu passa a madVR / MPC-VR.</summary>
    public static string VideoHostPathForTitleSet(Source source, int titleSet)
    {
        var vobs = VobGroups(source).TryGetValue(titleSet.ToString("00"), out var list) ? list : MainTitleVobs(source);
        if (vobs.Count > 0 && IsScrambled(vobs[0]) && MacDiscStream.UrlForTitle(source.Root, titleSet) is { } url) return url;
        return vobs.Count switch
        {
            0 => VideoHostClient.ToWinePath(source.Root),
            1 => VideoHostClient.ToWinePath(vobs[0]),
            _ => MacDiscStream.UrlFor(vobs),
        };
    }

    public static string ProbePath(Source source) => source.Kind == DiscKind.Bluray
        ? LargestM2ts(source) ?? source.Root
        : DvdTitleUrl(source) ?? "concat:" + string.Join("|", MainTitleVobs(source));

    /// <summary>Dischi video inseriti (volumi con BDMV o VIDEO_TS), come InsertedDiscs di Windows.</summary>
    /// <remarks>
    /// Si guarda solo il tipo di filesystem dei volumi montati (udf / cd9660 = disco ottico), senza entrare nel volume:
    /// leggere dentro un volume rimovibile fa chiedere a macOS il permesso "Volumi rimovibili" a ogni menu aperto (e
    /// per ogni chiavetta USB). Il contenuto si guarda quando il disco si apre davvero.
    /// </remarks>
    public static List<(string Label, string Root)> Inserted() =>
        OpticalVolumes().Where(v => v.Format is "udf" or "cd9660" && !Mounts.ContainsValue(v.Root))
            .Select(v => ("Disco · " + LabelAsTitle(Path.GetFileName(v.Root)), v.Root)).ToList();

    // getfsstat con MNT_NOWAIT: l'elenco dei montaggi gia' noto al sistema, senza interrogare i volumi (un disco di rete
    // lento o scollegato bloccava statfs, e quindi l'apertura dei menu). struct statfs a 64 bit: 2168 byte,
    // f_fstypename a 72 (16 byte), f_mntonname a 88 (1024 byte).
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)] private static extern int getfsstat(IntPtr buf, int bufsize, int mode);
    private const int StatfsSize = 2168, MntNowait = 2;

    /// <summary>Volumi montati in /Volumes con il loro filesystem (nessun accesso al contenuto, nessuna attesa).</summary>
#if CINECORE_WINDOWS
    // [2026-11-22] Su Windows leggere le unita' ottiche costa 10-40 ms (IsReady interroga il lettore) e il menu lo faceva
    // due volte a ogni apertura: si serve l'ultima lettura (al massimo 2 s fa) e si rilegge in sottofondo.
    private static readonly StaleCache<List<(string Root, string Format)>> Optical = new(ReadOpticalVolumes, TimeSpan.FromSeconds(2));
    public static List<(string Root, string Format)> OpticalVolumes() => new(Optical.Get());
    public static void RefreshOpticalVolumes() => Optical.Refresh();
    private static List<(string Root, string Format)> ReadOpticalVolumes()
#else
    public static void RefreshOpticalVolumes() { }
    public static List<(string Root, string Format)> OpticalVolumes()
#endif
    {
        var list = new List<(string, string)>();
        try
        {
            int count = getfsstat(IntPtr.Zero, 0, MntNowait);
            if (count <= 0) return list;
            int size = (count + 4) * StatfsSize;
            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                count = getfsstat(buffer, size, MntNowait);
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = buffer + i * StatfsSize;
                    string format = (System.Runtime.InteropServices.Marshal.PtrToStringUTF8(entry + 72) ?? "").ToLowerInvariant();
                    string root = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(entry + 88) ?? "";
                    if (root.StartsWith("/Volumes/", StringComparison.Ordinal) && format is "udf" or "cd9660" or "cddafs") list.Add((root, format));
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        }
        catch { }
        return list;
    }

    // ----- Blu-ray protetti (DiscProtection di Windows): libbluray + libaacs con il KEYDB.cfg dell'utente -----
    /// <summary>Dove libaacs del Mac cerca le chiavi (~/.config/aacs) e dove la cerca la libaacs di Windows sotto Wine.</summary>
    public static string MacKeyDb => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "aacs", "KEYDB.cfg");
    public static bool HasKeyDb => File.Exists(MacKeyDb) && new FileInfo(MacKeyDb).Length > 0;
    public static bool HasLibAacs => File.Exists("/opt/homebrew/lib/libaacs.dylib") || File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libaacs.dylib"));

    public static string ProtectionStatus() =>
        HasKeyDb && HasLibAacs ? "KEYDB.cfg e libaacs pronti" : HasKeyDb ? "KEYDB.cfg presente; manca libaacs (brew install libaacs)" : "Nessuna chiave: i Blu-ray protetti non si aprono";

    /// <summary>Copia il KEYDB.cfg scelto dove lo cercano libaacs del Mac e quella di Wine (cartella dati di Windows).</summary>
    public static bool ImportKeyDb(string source, out string error)
    {
        error = "";
        try
        {
            if (new FileInfo(source).Length < 16) { error = "File vuoto"; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(MacKeyDb)!);
            File.Copy(source, MacKeyDb, overwrite: true);
            if (VideoHostClient.WinePrefixAppData() is { } appData)
            {
                Directory.CreateDirectory(Path.Combine(appData, "aacs"));
                File.Copy(source, Path.Combine(appData, "aacs", "KEYDB.cfg"), overwrite: true);
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static void Eject(string root)
    {
        MacDiscStream.CloseTitles(root); // un lettore aperto terrebbe il disco occupato
        ProcessLauncher.Run("/usr/sbin/diskutil", new[] { "eject", root }, false, 30000, CancellationToken.None);
    }
}
