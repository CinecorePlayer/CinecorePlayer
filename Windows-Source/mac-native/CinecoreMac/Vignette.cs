using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Immagini della vignettatura, calcolate pixel per pixel come CreateVideoVignette di
/// PlayerForm.VideoVignette.cs (stesse curve, stesso rumore di un livello contro le bande). Si preparano fuori dal
/// thread dell'interfaccia: finche' non sono pronte non si disegnano.
/// </summary>
internal static class Vignette
{
    public enum Kind { Normal, Cast }

    private static readonly Dictionary<Kind, (PixelSize Size, Bitmap? Bitmap)> Ready = new();
    private static readonly HashSet<(Kind, PixelSize)> Building = new();

    public static Bitmap? Get(Kind kind, PixelSize size, Action changed)
    {
        lock (Ready)
        {
            if (Ready.TryGetValue(kind, out var have) && have.Size == size) return have.Bitmap;
            if (!Building.Add((kind, size))) return have.Size == size ? have.Bitmap : null;
        }
        Task.Run(() =>
        {
            Bitmap? bmp = null;
            try { bmp = Build(kind, size); } catch { }
            lock (Ready)
            {
                Building.Remove((kind, size));
                if (bmp != null) Ready[kind] = (size, bmp); // la vecchia puo' essere ancora in disegno: la libera il GC
            }
            Dispatcher.UIThread.Post(changed);
        });
        // Intanto (ridimensionamento) resta quella vecchia, stirata: meglio di un fotogramma senza ombra.
        lock (Ready) return Ready.TryGetValue(kind, out var stale) ? stale.Bitmap : null;
    }

    // Pixel calcolati in memoria e poi un Bitmap normale (copia): una WriteableBitmap riempita fuori dal thread
    // dell'interfaccia, con Avalonia su Metal, arrivava a schermo vuota (niente vignettatura, ne' quella del Cast).
    private static unsafe Bitmap Build(Kind kind, PixelSize size)
    {
        int w = size.Width, h = size.Height, stride = w * 4;
        var pixels = new byte[stride * h];
        fixed (byte* basePtr = pixels)
        {
        for (int y = 0; y < h; y++)
        {
            byte* row = basePtr + (long)y * stride;
            float shadeRow;
            if (kind == Kind.Cast)
            {
                // CreateCastVignette: molto piu' scura nella meta' bassa (ritratti e nomi), appena in alto.
                float top = Math.Clamp(1f - y / Math.Max(1f, h * .3f), 0, 1);
                float bottom = Math.Clamp((y / (float)h - .24f) / .30f, 0, 1);
                shadeRow = .18f + Math.Max(.35f * top * top, .80f * bottom * bottom * (3 - 2 * bottom));
            }
            else
            {
                float top = Math.Clamp(1f - y / Math.Max(1f, h * .36f), 0, 1);
                float bottom = Math.Clamp((y / (float)h - .40f) / .60f, 0, 1);
                shadeRow = Math.Max(.68f * top * top * (3 - 2 * top), .9f * bottom * bottom * (3 - 2 * bottom));
            }
            for (int x = 0; x < w; x++)
            {
                float shade = shadeRow;
                if (kind == Kind.Normal)
                {
                    float side = Math.Clamp(1f - Math.Min(x, w - x - 1) / Math.Max(1f, w * .08f), 0, 1);
                    shade = shadeRow + .16f * side * side * (1 - shadeRow);
                }
                uint hash = unchecked((uint)(x * 374761393 + y * 668265263));
                float noise = ((hash ^ (hash >> 13)) & 255) / 255f - .5f;
                byte a = (byte)Math.Clamp((int)(shade * 255 + noise), 0, 255);
                // Nero premoltiplicato: colore 0, solo alfa.
                row[x * 4] = 0; row[x * 4 + 1] = 0; row[x * 4 + 2] = 0; row[x * 4 + 3] = a;
            }
        }
        // A 96 dpi: con i dpi dello schermo Avalonia (Metal) applicava la scala due volte e a schermo arrivava solo il
        // quarto in alto a sinistra, ingrandito (niente ombra in basso). DrawImage la stira comunque su tutta la finestra.
        return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (IntPtr)basePtr, size, new Vector(96, 96), stride);
        }
    }
}
