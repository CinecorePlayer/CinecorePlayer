using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CinecoreMac;

/// <summary>
/// Dati di una foto letti da ImageIO di macOS (CGImageSourceCopyPropertiesAtIndex): misure, fotocamera, obiettivo,
/// esposizione, data dello scatto e posizione. Solo lettura dell'intestazione: la foto non viene decodificata.
/// </summary>
internal static class MacImageInfo
{
    public sealed record Info(int Width, int Height, long Bytes, string? Make, string? Model, string? Lens,
        double? Iso, double? FNumber, double? Exposure, double? FocalLength, string? Taken, double? Latitude, double? Longitude);

    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    [DllImport(CF)] private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr alloc, byte[] buffer, nint length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string text, uint encoding);
    [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CF)] private static extern nuint CFGetTypeID(IntPtr obj);
    [DllImport(CF)] private static extern nuint CFStringGetTypeID();
    [DllImport(CF)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CF)] private static extern nuint CFArrayGetTypeID();
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, nint type, out double value);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, nint size, uint encoding);
    [DllImport(CF)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
    [DllImport(IIO)] private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);
    [DllImport(IIO)] private static extern IntPtr CGImageSourceCopyPropertiesAtIndex(IntPtr source, nuint index, IntPtr options);

    private const uint Utf8 = 0x08000100;
    private const nint DoubleType = 13; // kCFNumberDoubleType

    public static Info? Read(string path)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(path)) return null;
        IntPtr url = IntPtr.Zero, source = IntPtr.Zero, props = IntPtr.Zero;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
            url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, false);
            if (url == IntPtr.Zero) return null;
            source = CGImageSourceCreateWithURL(url, IntPtr.Zero);
            if (source == IntPtr.Zero) return null;
            props = CGImageSourceCopyPropertiesAtIndex(source, 0, IntPtr.Zero);
            if (props == IntPtr.Zero) return null;
            IntPtr tiff = Dict(props, "{TIFF}"), exif = Dict(props, "{Exif}"), aux = Dict(props, "{ExifAux}"), gps = Dict(props, "{GPS}");
            double? lat = Num(gps, "Latitude"), lon = Num(gps, "Longitude");
            if (lat != null && Str(gps, "LatitudeRef") == "S") lat = -lat;
            if (lon != null && Str(gps, "LongitudeRef") == "W") lon = -lon;
            return new Info(
                (int)(Num(props, "PixelWidth") ?? 0), (int)(Num(props, "PixelHeight") ?? 0), new FileInfo(path).Length,
                Str(tiff, "Make")?.Trim(), Str(tiff, "Model")?.Trim(), (Str(exif, "LensModel") ?? Str(aux, "LensModel"))?.Trim(),
                Num(exif, "ISOSpeedRatings"), Num(exif, "FNumber"), Num(exif, "ExposureTime"), Num(exif, "FocalLength"),
                Str(exif, "DateTimeOriginal") ?? Str(tiff, "DateTime"), lat, lon);
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] info: " + ex.Message); return null; }
        finally
        {
            if (props != IntPtr.Zero) CFRelease(props);
            if (source != IntPtr.Zero) CFRelease(source);
            if (url != IntPtr.Zero) CFRelease(url);
        }
    }

    private static IntPtr Value(IntPtr dict, string key)
    {
        if (dict == IntPtr.Zero) return IntPtr.Zero;
        IntPtr k = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);
        try { return CFDictionaryGetValue(dict, k); }
        finally { CFRelease(k); }
    }

    private static IntPtr Dict(IntPtr dict, string key) => Value(dict, key);

    private static double? Num(IntPtr dict, string key)
    {
        IntPtr v = Value(dict, key);
        if (v == IntPtr.Zero) return null;
        nuint type = CFGetTypeID(v);
        // ISOSpeedRatings e' un elenco: vale il primo.
        if (type == CFArrayGetTypeID()) { if (CFArrayGetCount(v) < 1) return null; v = CFArrayGetValueAtIndex(v, 0); type = CFGetTypeID(v); }
        if (type != CFNumberGetTypeID()) return null;
        return CFNumberGetValue(v, DoubleType, out double d) && double.IsFinite(d) ? d : null;
    }

    private static string? Str(IntPtr dict, string key)
    {
        IntPtr v = Value(dict, key);
        if (v == IntPtr.Zero || CFGetTypeID(v) != CFStringGetTypeID()) return null;
        var buffer = new byte[1024];
        if (!CFStringGetCString(v, buffer, buffer.Length, Utf8)) return null;
        int end = Array.IndexOf(buffer, (byte)0);
        string s = Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        return s.Length == 0 ? null : s;
    }

    // ------------------------------------------------------------------ testi per il pannello Info
    public static string? CameraLine(Info i)
    {
        string? model = i.Model;
        // "Apple iPhone 15 Pro": la marca non si ripete se il modello la contiene gia'.
        if (i.Make != null && model != null && !model.StartsWith(i.Make, StringComparison.OrdinalIgnoreCase)) model = i.Make + " " + model;
        return model ?? i.Make;
    }

    public static string? ExposureLine(Info i)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (i.FNumber is > 0) parts.Add("ƒ/" + i.FNumber.Value.ToString(i.FNumber.Value % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture));
        if (i.Exposure is > 0)
            parts.Add(i.Exposure.Value >= 1 ? i.Exposure.Value.ToString("0.#", CultureInfo.InvariantCulture) + " s" : "1/" + Math.Round(1 / i.Exposure.Value).ToString(CultureInfo.InvariantCulture) + " s");
        if (i.Iso is > 0) parts.Add("ISO " + Math.Round(i.Iso.Value).ToString(CultureInfo.InvariantCulture));
        if (i.FocalLength is > 0) parts.Add(Math.Round(i.FocalLength.Value).ToString(CultureInfo.InvariantCulture) + " mm");
        return parts.Count == 0 ? null : string.Join("     ", parts);
    }

    public static string SizeLine(Info i)
    {
        string mp = i.Width > 0 && i.Height > 0 ? $"{i.Width} × {i.Height}" + (i.Width * (long)i.Height >= 1_000_000 ? $"     {(i.Width * (double)i.Height / 1e6).ToString("0.#", CultureInfo.InvariantCulture)} MP" : "") : "";
        string bytes = i.Bytes >= 1 << 20 ? (i.Bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : Math.Max(1, i.Bytes / 1024) + " KB";
        return mp.Length > 0 ? mp + "     " + bytes : bytes;
    }

    /// <summary>"2024:07:12 18:22:05" dell'EXIF in "12 luglio 2024, 18:22".</summary>
    public static string? TakenLine(Info i)
    {
        if (i.Taken is not { Length: >= 16 } t) return null;
        if (!DateTime.TryParseExact(t[..Math.Min(19, t.Length)], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        string[] months = { "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre" };
        return $"{d.Day} {L10n.T(months[d.Month - 1])} {d.Year}, {d.Hour:00}:{d.Minute:00}";
    }

    public static string? PlaceLine(Info i) => i.Latitude is { } lat && i.Longitude is { } lon
        ? $"{Math.Abs(lat).ToString("0.0000", CultureInfo.InvariantCulture)}° {(lat >= 0 ? "N" : "S")}, {Math.Abs(lon).ToString("0.0000", CultureInfo.InvariantCulture)}° {(lon >= 0 ? "E" : "O")}"
        : null;

    // ------------------------------------------------------------------ azioni
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, nuint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint SendCount(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendStr(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendSet(IntPtr obj, IntPtr sel, IntPtr url, IntPtr screen, IntPtr options, IntPtr error);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendOpen(IntPtr obj, IntPtr sel, IntPtr url);

    private static IntPtr S(string name) => sel_registerName(name);

    /// <summary>La foto come sfondo della scrivania su tutti gli schermi (NSWorkspace, senza permessi in piu').</summary>
    public static bool SetDesktopPicture(string path)
    {
        try
        {
            IntPtr nsPath = SendStr(objc_getClass("NSString"), S("stringWithUTF8String:"), Path.GetFullPath(path));
            IntPtr url = Send(objc_getClass("NSURL"), S("fileURLWithPath:"), nsPath);
            IntPtr workspace = Send(objc_getClass("NSWorkspace"), S("sharedWorkspace"));
            IntPtr options = Send(objc_getClass("NSDictionary"), S("dictionary"));
            IntPtr screens = Send(objc_getClass("NSScreen"), S("screens"));
            nuint count = SendCount(screens, S("count"));
            bool ok = count > 0;
            for (nuint i = 0; i < count; i++)
                ok &= SendSet(workspace, S("setDesktopImageURL:forScreen:options:error:"), url, Send(screens, S("objectAtIndex:"), i), options, IntPtr.Zero);
            return ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] sfondo: " + ex.Message); return false; }
    }

    /// <summary>Il punto dello scatto in Mappe.</summary>
    public static void OpenInMaps(double lat, double lon)
    {
        try
        {
            string link = $"https://maps.apple.com/?ll={lat.ToString("0.######", CultureInfo.InvariantCulture)},{lon.ToString("0.######", CultureInfo.InvariantCulture)}&q={Uri.EscapeDataString(L10n.T("Foto"))}";
            IntPtr url = Send(objc_getClass("NSURL"), S("URLWithString:"), SendStr(objc_getClass("NSString"), S("stringWithUTF8String:"), link));
            if (url != IntPtr.Zero) SendOpen(Send(objc_getClass("NSWorkspace"), S("sharedWorkspace")), S("openURL:"), url);
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] mappe: " + ex.Message); }
    }
}
