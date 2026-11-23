using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CinecoreMac;

/// <summary>
/// Dati di una foto letti dal sistema delle proprieta' di Windows: misure, fotocamera, obiettivo,
/// esposizione, data dello scatto e posizione. Solo lettura dell'intestazione: la foto non viene decodificata.
/// </summary>
internal static class MacImageInfo
{
    public sealed record Info(int Width, int Height, long Bytes, string? Make, string? Model, string? Lens,
        double? Iso, double? FNumber, double? Exposure, double? FocalLength, string? Taken, double? Latitude, double? Longitude);

    /// <summary>Dati della foto letti dalle proprieta' di Windows (System.Photo.*, System.GPS.*), senza decodificarla.</summary>
    public static Info? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
            var image = file.Properties.GetImagePropertiesAsync().AsTask().GetAwaiter().GetResult();
            string[] keys = { "System.Photo.LensModel", "System.Photo.ISOSpeed", "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.FocalLength" };
            var extra = file.Properties.RetrievePropertiesAsync(keys).AsTask().GetAwaiter().GetResult();
            double? D(string k) { try { return extra.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : null; } catch { return null; } }
            string? taken = image.DateTaken.Year > 1601 ? image.DateTaken.LocalDateTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture) : null;
            return new Info((int)image.Width, (int)image.Height, new FileInfo(path).Length,
                string.IsNullOrWhiteSpace(image.CameraManufacturer) ? null : image.CameraManufacturer.Trim(),
                string.IsNullOrWhiteSpace(image.CameraModel) ? null : image.CameraModel.Trim(),
                extra.TryGetValue("System.Photo.LensModel", out var lens) ? (lens as string)?.Trim() : null,
                D("System.Photo.ISOSpeed"), D("System.Photo.FNumber"), D("System.Photo.ExposureTime"), D("System.Photo.FocalLength"),
                taken, image.Latitude, image.Longitude);
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] info: " + ex.Message); return null; }
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
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);

    /// <summary>La foto come sfondo del desktop (SPI_SETDESKWALLPAPER, salvato nel profilo).</summary>
    public static bool SetDesktopPicture(string path)
    {
        try { return SystemParametersInfo(0x0014, 0, Path.GetFullPath(path), 0x01 | 0x02); }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] sfondo: " + ex.Message); return false; }
    }

    /// <summary>Il punto dello scatto in Mappe (browser).</summary>
    public static void OpenInMaps(double lat, double lon)
        => PlatformShell.OpenUrl($"https://www.google.com/maps/search/?api=1&query={lat.ToString("0.######", CultureInfo.InvariantCulture)},{lon.ToString("0.######", CultureInfo.InvariantCulture)}");
}
