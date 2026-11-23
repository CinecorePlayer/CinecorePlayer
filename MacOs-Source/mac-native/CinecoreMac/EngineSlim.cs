using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CinecoreMac;

/// <summary>
/// Dieta del motore dopo l'estrazione di MetalSharp Wine (da 6,2 GB a ~2,5 GB, misurato il 23/11/2026):
///   1. via i simboli di debug dalle DLL di Wine (servono solo a chi sviluppa Wine: ~1,4 GB su 2,4 GB);
///   2. via le parti che a un lettore video non servono: Wine Mono e Gecko (il prefisso nasce gia' senza),
///      gli archivi e gli asset per i giochi di MetalSharp, il client di Steam;
///   3. le DLL che wineboot copia nel prefisso (system32, syswow64) diventano collegamenti fissi a quelle di Wine
///      quando sono identiche: un file solo sul disco.
/// Lo stesso identico procedimento e' in mac/slim-engine.py (motore di sviluppo) e mac/verify-engine.py lo usa per
/// confrontare: tenerli allineati.
/// </summary>
internal static class EngineSlim
{
    /// <summary>Cartelle e file del pacchetto di Wine che il motore non usa (relativi a Contents).</summary>
    public static readonly string[] Removed =
    {
        "share/wine/mono", "share/wine/gecko",
        "share/metalsharp/archives", "share/metalsharp/assets", "share/metalsharp/redist",
        "share/metalsharp/steam", "share/metalsharp/goldberg",
        "lib/wine/aarch64-windows/lsteamclient.dll", "lib/wine/i386-windows/lsteamclient.dll", "lib/wine/aarch64-unix/lsteamclient.so",
    };

    private static readonly string[] WindowsDirs = { "lib/wine/aarch64-windows", "lib/wine/i386-windows" };

    /// <summary>Passi 1 e 2 sul pacchetto di Wine (prima della firma e di wineboot). Restituisce i byte risparmiati.</summary>
    public static long SlimWine(string wineApp)
    {
        string contents = Path.Combine(wineApp, "Contents");
        long saved = 0;
        foreach (string rel in Removed)
        {
            string p = Path.Combine(contents, rel);
            if (Directory.Exists(p)) { saved += DirSize(p); Directory.Delete(p, true); }
            else if (File.Exists(p)) { saved += new FileInfo(p).Length; File.Delete(p); }
        }
        foreach (string dir in WindowsDirs)
        {
            string d = Path.Combine(contents, dir);
            if (!Directory.Exists(d)) continue;
            foreach (string file in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).LinkTarget != null) continue;
                byte[] data = File.ReadAllBytes(file);
                if (StripDebug(data) is not { } stripped) continue;
                File.WriteAllBytes(file, stripped);
                saved += data.Length - stripped.Length;
            }
        }
        return saved;
    }

    /// <summary>
    /// Toglie le sezioni .debug* e la tabella dei simboli COFF da una DLL/EXE PE, come "strip --strip-debug".
    /// Solo nel caso semplice e sicuro (quello delle DLL di Wine compilate con mingw): sezioni di debug tutte in fondo,
    /// sia nella tabella sia nel file, dopo i dati delle altre sezioni; niente firma digitale; niente dati dopo la tabella
    /// delle stringhe (a parte il riempimento a zero). Altrimenti null (il file resta com'e').
    /// </summary>
    public static byte[]? StripDebug(byte[] b)
    {
        if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return null;
        int pe = BitConverter.ToInt32(b, 0x3c);
        if (pe <= 0 || pe + 24 > b.Length || BitConverter.ToUInt32(b, pe) != 0x00004550) return null;
        int n = BitConverter.ToUInt16(b, pe + 6);
        uint symtab = BitConverter.ToUInt32(b, pe + 12), nsym = BitConverter.ToUInt32(b, pe + 16);
        int optSize = BitConverter.ToUInt16(b, pe + 20);
        int opt = pe + 24;
        if (opt + optSize + n * 40 > b.Length || optSize < 96) return null;
        ushort magic = BitConverter.ToUInt16(b, opt);
        int dataDirs = opt + (magic == 0x10b ? 96 : magic == 0x20b ? 112 : -1);
        if (dataDirs < opt) return null;
        // Tabella dei certificati (firma Authenticode): punta a una posizione nel file, non si tocca.
        if (dataDirs + 5 * 8 <= opt + optSize && (BitConverter.ToUInt32(b, dataDirs + 32) != 0 || BitConverter.ToUInt32(b, dataDirs + 36) != 0)) return null;

        long strtab = symtab + (long)nsym * 18, end;
        if (symtab != 0)
        {
            if (strtab + 4 > b.Length) return null;
            end = strtab + BitConverter.ToUInt32(b, (int)strtab);
        }
        else end = -1;

        int secTable = opt + optSize, keep = 0;
        long dataEnd = 0, cut = long.MaxValue, debugEnd = 0, imageEnd = 0;
        bool inDebug = false;
        for (int i = 0; i < n; i++)
        {
            int o = secTable + i * 40;
            string name = Name(b, o, symtab != 0 ? strtab : -1);
            if (name.Length == 0) return null;
            uint vsize = BitConverter.ToUInt32(b, o + 8), va = BitConverter.ToUInt32(b, o + 12);
            uint rsize = BitConverter.ToUInt32(b, o + 16), rptr = BitConverter.ToUInt32(b, o + 20);
            bool debug = name.StartsWith(".debug", StringComparison.Ordinal);
            if (debug)
            {
                inDebug = true;
                if (rsize > 0) { cut = Math.Min(cut, rptr); debugEnd = Math.Max(debugEnd, (long)rptr + rsize); }
            }
            else
            {
                // Una sezione normale dopo una di debug, o con nome lungo (sta nella tabella delle stringhe che si toglie).
                if (inDebug || b[o] == '/') return null;
                keep++;
                if (rsize > 0) dataEnd = Math.Max(dataEnd, (long)rptr + rsize);
                imageEnd = Math.Max(imageEnd, (long)va + (vsize != 0 ? vsize : rsize));
            }
        }
        if (keep == n || cut == long.MaxValue || cut < dataEnd) return null;
        // Dopo la tabella delle stringhe solo il riempimento a zero fino all'allineamento del file.
        if (symtab != 0 ? symtab < debugEnd || end > b.Length || b.AsSpan((int)end).IndexOfAnyExcept((byte)0) >= 0 : debugEnd != b.Length) return null;

        var outp = new byte[cut];
        Buffer.BlockCopy(b, 0, outp, 0, (int)cut);
        Array.Clear(outp, secTable + keep * 40, (n - keep) * 40);
        BitConverter.TryWriteBytes(outp.AsSpan(pe + 6), (ushort)keep);
        BitConverter.TryWriteBytes(outp.AsSpan(pe + 12), 0u);
        BitConverter.TryWriteBytes(outp.AsSpan(pe + 16), 0u);
        uint align = BitConverter.ToUInt32(b, opt + 32);
        if (align == 0) return null;
        BitConverter.TryWriteBytes(outp.AsSpan(opt + 56), (uint)((imageEnd + align - 1) / align * align));
        return outp;
    }

    private static string Name(byte[] b, int o, long strtab)
    {
        int len = 0;
        while (len < 8 && b[o + len] != 0) len++;
        string raw = System.Text.Encoding.ASCII.GetString(b, o, len);
        if (!raw.StartsWith('/') || strtab < 0) return raw;
        if (!int.TryParse(raw.AsSpan(1), out int off)) return "";
        long s = strtab + off;
        if (s >= b.Length) return "";
        int e = (int)s;
        while (e < b.Length && b[e] != 0) e++;
        return System.Text.Encoding.ASCII.GetString(b, (int)s, e - (int)s);
    }

    /// <summary>
    /// Passo 3: le DLL del prefisso identiche a quelle di Wine diventano collegamenti fissi (stesso file sul disco).
    /// system32 = aarch64-windows, syswow64 = i386-windows. Restituisce i byte risparmiati.
    /// </summary>
    public static long LinkPrefixDlls(string wineApp, string prefix)
    {
        long saved = 0;
        string lib = Path.Combine(wineApp, "Contents", "lib", "wine");
        foreach (var (win, wine) in new[] { ("system32", "aarch64-windows"), ("syswow64", "i386-windows") })
        {
            string winDir = Path.Combine(prefix, "drive_c", "windows", win), wineDir = Path.Combine(lib, wine);
            if (!Directory.Exists(winDir) || !Directory.Exists(wineDir)) continue;
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.EnumerateFiles(wineDir)) byName[Path.GetFileName(f)] = f;
            foreach (string target in Directory.EnumerateFiles(winDir, "*", SearchOption.AllDirectories))
            {
                // Anche system32/drivers/*.sys: in Wine stanno tutti nella stessa cartella. Si collega solo se identico.
                if (!byName.TryGetValue(Path.GetFileName(target), out string? source)) continue;
                var ti = new FileInfo(target); var si = new FileInfo(source);
                if (ti.LinkTarget != null || si.LinkTarget != null || ti.Length != si.Length || ti.Length == 0) continue;
                if (SameInode(target, source) || !SameContent(target, source)) continue;
                string tmp = target + ".cinecore-link";
                if (link(source, tmp) != 0) continue;
                File.Move(tmp, target, overwrite: true);
                saved += ti.Length;
            }
        }
        return saved;
    }

    private static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    private static bool SameInode(string a, string b) =>
        stat(a, out var sa) == 0 && stat(b, out var sb) == 0 && sa.Dev == sb.Dev && sa.Ino == sb.Ino;

    private static long DirSize(string dir)
    {
        long size = 0;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            try { var fi = new FileInfo(f); if (fi.LinkTarget == null) size += fi.Length; } catch (IOException) { }
        return size;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existing, string newPath);

    // struct stat di macOS (64 bit): st_dev int32, st_mode uint16, st_nlink uint16, st_ino uint64, ...
    [StructLayout(LayoutKind.Sequential)]
    private struct StatBuf
    {
        public int Dev; public ushort Mode; public ushort Nlink; public ulong Ino;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Rest;
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int stat(string path, out StatBuf buf);
}
