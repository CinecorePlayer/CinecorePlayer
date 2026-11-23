using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CinecoreMac;

/// <summary>
/// Sfondo "vetro" del menu contestuale (PlayerForm.MenuAmbient + GlassSurface.CreateLiquid su Windows). Sopra madVR la
/// cattura dello schermo non vede il film: il vetro nasce da un fotogramma del punto in riproduzione, letto a parte
/// (qui con FFmpeg per macOS) e convertito in SDR come le anteprime della timeline. Finche' non e' pronto si
/// usa un fondo scuro neutro, mai il desktop.
/// </summary>
internal static class MenuAmbient
{
    public sealed record Frame(byte[] Bgra, int Width, int Height);

    private static Frame? _frame;
    private static string? _path;
    private static double _seconds;
    private static int _busy;

    /// <summary>Un fotogramma recente dello stesso film (entro 45 s dal punto attuale), se c'e'.</summary>
    public static Frame? Current(string? path, double position) =>
        _frame != null && path != null && string.Equals(path, _path, StringComparison.Ordinal) && Math.Abs(position - _seconds) < 45 ? _frame : null;

    /// <summary>Legge in background il fotogramma del punto attuale; <paramref name="done"/> arriva sul thread che chiama Post.</summary>
    public static void Refresh(string? path, double seconds, ProbeData? probe, Action<Frame> done)
    {
        if (string.IsNullOrWhiteSpace(path) || probe is not { HasVideo: true, Width: > 0, Height: > 0 }) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            Frame? frame = null;
            try { frame = await GrabAsync(path, seconds, probe); }
            catch (Exception ex) { Console.Error.WriteLine("[MENU] ambient frame: " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
            if (frame == null) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _frame = frame; _path = path; _seconds = seconds;
                done(frame);
            });
        });
    }

    private static async Task<Frame?> GrabAsync(string file, double seconds, ProbeData probe)
    {
        // 480 di larghezza, come Thumbnailer.Get(..., 480): basta per un vetro sfocato.
        var frames = await GrabFramesAsync(file, new[] { seconds }, 480, probe, TimeSpan.FromSeconds(12), CancellationToken.None);
        return frames[0];
    }

    /// <summary>
    /// Fotogrammi del file ai tempi indicati, larghi <paramref name="width"/>, con un solo FFmpeg: un ingresso per ogni
    /// tempo e un'uscita su file per ciascuno.
    /// HDR e BT.2020 convertiti in SDR come il Thumbnailer di Windows.
    /// </summary>
    public static async Task<Frame?[]> GrabFramesAsync(string file, IReadOnlyList<double> times, int width, ProbeData probe, TimeSpan limit, CancellationToken ct, bool keyframes = false)
    {
        var result = new Frame?[times.Count];
        if (times.Count == 0 || probe is not { Width: > 0, Height: > 0 }) return result;
        int w = width, h = Math.Max(2, (int)Math.Round((double)width * probe.Height / probe.Width / 2) * 2);
        // Matrice e gamma dichiarate dal file: senza, swscale usa BT.601 limitato (colori sbagliati su HD/UHD).
        string matrix = probe.ColorSpace switch
        {
            "bt2020nc" or "bt2020c" => "bt2020",
            "bt709" => "bt709",
            "fcc" => "fcc",
            "smpte240m" => "smpte240m",
            "bt470bg" or "smpte170m" => "bt601",
            _ => probe.Primaries == "bt2020" ? "bt2020" : probe.Width >= 1280 ? "bt709" : "bt601",
        };
        string range = probe.ColorRange == "pc" ? "pc" : "tv";
        // FFmpeg per macOS (Apple Silicon): parte in pochi centesimi di secondo.
        if (MacTools.Ffmpeg is not { } exe) return result;
        Func<string, string> pathOf = x => x;
        var outputs = new string[times.Count];
        // Priorita' bassa: le anteprime non devono togliere CPU all'audio del film (sotto Wine saltava mentre si
        // scorreva la barra, segnalato il 12/11/2026).
#if CINECORE_WINDOWS
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
#else
        var psi = new ProcessStartInfo("/usr/bin/nice") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-n", "15", exe }) psi.ArgumentList.Add(a);
#endif
        foreach (var a in new[] { "-v", "quiet", "-nostdin", "-y", "-filter_threads", "1", "-filter_complex_threads", "1" }) psi.ArgumentList.Add(a);
        for (int i = 0; i < times.Count; i++)
        {
            // Le cinque anteprime non devono occupare tutti i core mentre il film sta decodificando.
            foreach (var a in new[] { "-threads", "1" }) psi.ArgumentList.Add(a);
            // Anteprime: il fotogramma chiave da cui parte il punto, senza decodificare fino al tempo esatto
            // (4K60 HEVC: 0,11 s invece di 0,75). VideoToolbox qui non aiuta: per un fotogramma pesa l'avvio.
            if (keyframes) foreach (var a in new[] { "-skip_frame", "nokey", "-noaccurate_seek" }) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(Math.Max(0, times[i]).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(pathOf(file));
        }
        for (int i = 0; i < times.Count; i++)
        {
            outputs[i] = Path.Combine(Path.GetTempPath(), $"cinecore-frame-{Environment.ProcessId}-{Guid.NewGuid():N}.raw");
            foreach (var a in new[] { "-map", i + ":v:0", "-frames:v", "1", "-an", "-sn",
                         "-vf", $"scale={w}:{h}:flags=bicubic:in_color_matrix={matrix}:in_range={range}:out_range=pc",
                         "-pix_fmt", "bgra", "-f", "rawvideo" }) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(pathOf(outputs[i]));
        }
        using var proc = Process.Start(psi);
        if (proc == null) return result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { proc.Kill(); } catch { } return result; }
            bool hdr = probe.Transfer is "smpte2084" or "arib-std-b67";
            bool wide = probe.Primaries == "bt2020";
            for (int i = 0; i < times.Count; i++)
            {
                if (!File.Exists(outputs[i])) continue;
                byte[] px = await File.ReadAllBytesAsync(outputs[i]);
                if (px.Length < w * h * 4) continue;
                if (hdr || wide) MapToSdr(px, w, h, probe.Transfer, wide);
                result[i] = new Frame(px, w, h);
            }
            return result;
        }
        finally { foreach (var o in outputs) if (o != null) try { File.Delete(o); } catch { } }
    }

    // --- Media.cs (Thumbnailer.MapToSdr): PQ -> luminanza assoluta -> tone mapping BT.2390-like, HLG -> OETF inversa,
    // poi gamut BT.2020 -> BT.709 e codifica sRGB.
    private static float[]? _pq, _hlg, _sdr;
    private static readonly byte[] LinearToSrgb = BuildLinearToSrgb();

    private static byte[] BuildLinearToSrgb()
    {
        var lut = new byte[4096];
        for (int i = 0; i < lut.Length; i++)
        {
            double l = i / (double)(lut.Length - 1);
            double v = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
            lut[i] = (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        }
        return lut;
    }

    private static float[] PqLut()
    {
        if (_pq != null) return _pq;
        const double m1 = 2610 / 16384.0, m2 = 2523 / 4096.0 * 128, c1 = 3424 / 4096.0, c2 = 2413 / 4096.0 * 32, c3 = 2392 / 4096.0 * 32;
        const double white = 4.0;
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double e = Math.Pow(i / 255.0, 1 / m2);
            double nits = 10000 * Math.Pow(Math.Max(e - c1, 0) / (c2 - c3 * e), 1 / m1);
            double x = nits / 160.0;
            lut[i] = (float)(x * (1 + x / (white * white)) / (1 + x));
        }
        return _pq = lut;
    }

    private static float[] HlgLut()
    {
        if (_hlg != null) return _hlg;
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double e = i / 255.0;
            double l = e <= 0.5 ? e * e / 3 : (Math.Exp((e - c) / a) + b) / 12;
            lut[i] = (float)Math.Min(1, Math.Pow(l, 1.2) * 1.6);
        }
        return _hlg = lut;
    }

    private static float[] SdrLut()
    {
        if (_sdr != null) return _sdr;
        var lut = new float[256];
        for (int i = 0; i < 256; i++) lut[i] = (float)Math.Pow(i / 255.0, 2.4);
        return _sdr = lut;
    }

    internal static void MapToSdr(byte[] px, int width, int height, string transfer, bool wideGamut)
    {
        float[] toLinear = transfer == "smpte2084" ? PqLut() : transfer == "arib-std-b67" ? HlgLut() : SdrLut();
        int top = LinearToSrgb.Length - 1;
        for (int i = 0; i < width * height * 4; i += 4)
        {
            float r = toLinear[px[i + 2]], g = toLinear[px[i + 1]], b = toLinear[px[i]];
            if (wideGamut)
            {
                float r2 = 1.6605f * r - 0.5876f * g - 0.0728f * b;
                float g2 = -0.1246f * r + 1.1329f * g - 0.0083f * b;
                float b2 = -0.0182f * r - 0.1006f * g + 1.1187f * b;
                r = r2; g = g2; b = b2;
            }
            px[i + 2] = LinearToSrgb[(int)(Math.Clamp(r, 0f, 1f) * top)];
            px[i + 1] = LinearToSrgb[(int)(Math.Clamp(g, 0f, 1f) * top)];
            px[i] = LinearToSrgb[(int)(Math.Clamp(b, 0f, 1f) * top)];
            px[i + 3] = 255;
        }
    }
}

/// <summary>
/// Il materiale "liquid" di GlassSurface.CreateLiquid: sfocatura profonda (sigma 30 pixel dello schermo, fatta a 1/4
/// della risoluzione), saturazione +50%, luci compresse con una curva morbida, velatura grafite leggera. Con il vetro
/// "ambient" lo sfondo e' lo stesso per tutto il menu: si calcola una volta per l'intera area, a 1/4, e ogni pannello
/// ne disegna il suo ritaglio riportato a piena risoluzione con un filtro di qualita'.
/// </summary>
internal static class LiquidGlass
{
    private const int Reduction = 4;
    private const float BlurSigma = 30f;
    private const byte TintAlpha = 118;

    /// <summary>Vetro per un'area di <paramref name="device"/> pixel (l'intera finestra), dal fotogramma (o nulla).</summary>
    public static unsafe WriteableBitmap Build(MenuAmbient.Frame? ambient, PixelSize device)
    {
        int w = Math.Max(1, (device.Width + Reduction - 1) / Reduction), h = Math.Max(1, (device.Height + Reduction - 1) / Reduction);
        var r = new float[w * h]; var g = new float[w * h]; var b = new float[w * h];
        // Fondo: (14,16,20) e, sopra, il fotogramma steso su tutta l'area (bilineare).
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (ambient == null) { r[i] = 14; g[i] = 16; b[i] = 20; continue; }
                double sx = (x + .5) * ambient.Width / w - .5, sy = (y + .5) * ambient.Height / h - .5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, ambient.Width - 1), y0 = Math.Clamp((int)Math.Floor(sy), 0, ambient.Height - 1);
                int x1 = Math.Min(ambient.Width - 1, x0 + 1), y1 = Math.Min(ambient.Height - 1, y0 + 1);
                float fx = (float)Math.Clamp(sx - x0, 0, 1), fy = (float)Math.Clamp(sy - y0, 0, 1);
                float Sample(int c)
                {
                    var p = ambient.Bgra; int s = ambient.Width * 4;
                    float a0 = p[y0 * s + x0 * 4 + c] * (1 - fx) + p[y0 * s + x1 * 4 + c] * fx;
                    float a1 = p[y1 * s + x0 * 4 + c] * (1 - fx) + p[y1 * s + x1 * 4 + c] * fx;
                    return a0 * (1 - fy) + a1 * fy;
                }
                b[i] = Sample(0); g[i] = Sample(1); r[i] = Sample(2);
            }

        // Saturazione (stessa matrice di Skia, coefficienti di luminanza BT.709).
        const float s = 1.5f, lr = .2126f * (1 - s), lg = .7152f * (1 - s), lb = .0722f * (1 - s);
        for (int i = 0; i < r.Length; i++)
        {
            float R = r[i], G = g[i], B = b[i];
            r[i] = Math.Clamp((lr + s) * R + lg * G + lb * B, 0, 255);
            g[i] = Math.Clamp(lr * R + (lg + s) * G + lb * B, 0, 255);
            b[i] = Math.Clamp(lr * R + lg * G + (lb + s) * B, 0, 255);
        }

        // Gaussiana come somma di tre medie mobili (sigma in pixel ridotti), bordi bloccati come SKShaderTileMode.Clamp.
        foreach (int box in Boxes(BlurSigma / Reduction, 3))
            foreach (var plane in new[] { r, g, b }) BoxBlur(plane, w, h, (box - 1) / 2);

        // Curva delle luci (tema scuro): non oltre ~165, toni medi quasi intatti. Poi la velatura grafite.
        const double Limit = 165;
        var curve = new float[256];
        for (int v = 0; v < 256; v++) curve[v] = (float)(Limit * (1 - Math.Exp(-v / Limit)));
        const float ta = TintAlpha / 255f;
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)fb.Address + (long)y * fb.RowBytes;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float R = curve[(int)Math.Clamp(r[i], 0, 255)], G = curve[(int)Math.Clamp(g[i], 0, 255)], B = curve[(int)Math.Clamp(b[i], 0, 255)];
                    row[x * 4 + 2] = (byte)Math.Clamp(R * (1 - ta) + 40 * ta + .5f, 0, 255);
                    row[x * 4 + 1] = (byte)Math.Clamp(G * (1 - ta) + 42 * ta + .5f, 0, 255);
                    row[x * 4 + 0] = (byte)Math.Clamp(B * (1 - ta) + 46 * ta + .5f, 0, 255);
                    row[x * 4 + 3] = 255;
                }
            }
        }
        return bmp;
    }

    /// <summary>Dithering di +-2 livelli (VisualDither.AddNoise): una piastrella da ripetere sopra il vetro.</summary>
    public static unsafe WriteableBitmap NoiseTile()
    {
        const int n = 128;
        var bmp = new WriteableBitmap(new PixelSize(n, n), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        for (int y = 0; y < n; y++)
        {
            byte* row = (byte*)fb.Address + (long)y * fb.RowBytes;
            for (int x = 0; x < n; x++)
            {
                uint hash = unchecked((uint)(x * 374761393 + y * 668265263 + 1013904223));
                hash ^= hash >> 13; hash *= 1274126177; hash ^= hash >> 16;
                int d = (int)(hash % 5) - 2; // -2..2
                // Bianco o nero premoltiplicato: sul vetro scuro (~40-60) sposta il colore sotto di circa d livelli.
                byte a = d > 0 ? (byte)d : (byte)(-d * 5);
                byte c = d > 0 ? a : (byte)0;
                row[x * 4] = c; row[x * 4 + 1] = c; row[x * 4 + 2] = c; row[x * 4 + 3] = a;
            }
        }
        return bmp;
    }

    private static int[] Boxes(float sigma, int n)
    {
        double ideal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(ideal); if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double m = Math.Round((12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4));
        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = i < m ? wl : wu;
        return sizes;
    }

    private static void BoxBlur(float[] p, int w, int h, int radius)
    {
        if (radius <= 0) return;
        var tmp = new float[Math.Max(w, h)];
        float inv = 1f / (2 * radius + 1);
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            float acc = 0;
            for (int k = -radius; k <= radius; k++) acc += p[o + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[x] = acc * inv;
                acc += p[o + Math.Min(w - 1, x + radius + 1)] - p[o + Math.Max(0, x - radius)];
            }
            Array.Copy(tmp, 0, p, o, w);
        }
        for (int x = 0; x < w; x++)
        {
            float acc = 0;
            for (int k = -radius; k <= radius; k++) acc += p[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                tmp[y] = acc * inv;
                acc += p[Math.Min(h - 1, y + radius + 1) * w + x] - p[Math.Max(0, y - radius) * w + x];
            }
            for (int y = 0; y < h; y++) p[y * w + x] = tmp[y];
        }
    }
}
