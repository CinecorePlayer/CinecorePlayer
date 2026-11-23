using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CinecoreMac;

/// <summary>
/// Titoli dei DVD letti con libdvdread (come VLC e mpv): un titolo e' un unico flusso di settori da 2048 byte, anche se
/// sul disco e' diviso in piu' VOB, e i DVD protetti (CSS) si decifrano da soli con libdvdcss. Il server locale
/// (MacDiscStream) lo serve a tutti i motori. Senza libdvdread si torna ai VOB uniti byte per byte.
/// </summary>
internal static class MacDvdRead
{
    public const int Block = 2048;
    private const int TitleVobs = 3; // DVD_READ_TITLE_VOBS

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate IntPtr OpenFn(string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseFn(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OpenFileFn(IntPtr dvd, int title, int domain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ReadBlocksFn(IntPtr file, int offset, nuint blocks, byte[] data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint FileSizeFn(IntPtr file);

    private static OpenFn? _open; private static CloseFn? _close, _closeFile;
    private static OpenFileFn? _openFile; private static ReadBlocksFn? _read; private static FileSizeFn? _size;
    private static readonly object LoadLock = new();

    private static string? LibraryPath()
    {
        foreach (string p in new[] {
            Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libdvdread.8.dylib"),
            "/opt/homebrew/lib/libdvdread.8.dylib", "/usr/local/lib/libdvdread.8.dylib" })
            if (File.Exists(p)) return Path.GetFullPath(p);
        return null;
    }
    public static bool Available => LibraryPath() != null;

    private static bool Load()
    {
        lock (LoadLock)
        {
            if (_open != null) return true;
            if (LibraryPath() is not { } path) return false;
            // libdvdcss la carica libdvdread da sola (dlopen): nel pacchetto sta accanto, in Frameworks.
            string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libdvdcss.2.dylib"));
            // libdvdread la apre per nome ("libdvdcss.2.dylib"): una gia' caricata con quel nome viene riusata.
            if (File.Exists(bundled)) try { NativeLibrary.Load(bundled); } catch { }
            IntPtr lib = NativeLibrary.Load(path);
            T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            _close = F<CloseFn>("DVDClose"); _closeFile = F<CloseFn>("DVDCloseFile");
            _openFile = F<OpenFileFn>("DVDOpenFile"); _read = F<ReadBlocksFn>("DVDReadBlocks"); _size = F<FileSizeFn>("DVDFileSize");
            _open = F<OpenFn>("DVDOpen");
            return true;
        }
    }

    /// <summary>Numero del gruppo di titoli (VTS_02_1.VOB -> 2).</summary>
    public static int TitleSetOf(string vob) =>
        Regex.Match(Path.GetFileName(vob), @"^VTS_(\d\d)_", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    /// <summary>Un titolo aperto: lettura per settori (un lettore per richiesta: libdvdread non e' rientrante).</summary>
    public sealed class TitleReader : IDisposable
    {
        private IntPtr _dvd, _file;
        public long Blocks { get; }
        public long Length => Blocks * Block;
        internal TitleReader(IntPtr dvd, IntPtr file) { _dvd = dvd; _file = file; Blocks = _size!(file); }
        public int Read(long block, byte[] buffer, int blocks)
        {
            nint n = _read!(_file, (int)block, (nuint)blocks, buffer);
            return n < 0 ? 0 : (int)n;
        }
        public void Dispose()
        {
            if (_file != IntPtr.Zero) { _closeFile!(_file); _file = IntPtr.Zero; }
            if (_dvd != IntPtr.Zero) { _close!(_dvd); _dvd = IntPtr.Zero; }
        }
    }

    public static TitleReader? Open(string root, int titleSet)
    {
        if (titleSet <= 0 || !Load()) return null;
        IntPtr dvd = _open!(root);
        if (dvd == IntPtr.Zero) return null;
        IntPtr file = _openFile!(dvd, titleSet, TitleVobs);
        if (file == IntPtr.Zero) { _close!(dvd); return null; }
        return new TitleReader(dvd, file);
    }
}
