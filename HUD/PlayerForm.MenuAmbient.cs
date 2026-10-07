#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    // Sfondo "vetro" del menu contestuale sopra un video che la cattura dello schermo non vede.
    // Con madVR a schermo intero (modalita' esclusiva, o uscita HDR) il video non passa dalla composizione
    // normale di Windows: leggendo lo schermo si ottiene il desktop che sta sotto, non il film. Il menu sfocava quindi lo
    // sfondo sbagliato (di solito il blu-grigio del desktop) e i suoi angoli, riempiti con quella
    // stessa cattura, non combaciavano con l'immagine. In questo caso il vetro si costruisce da
    // un fotogramma del punto in riproduzione, letto con un decodificatore a parte (come le
    // anteprime della timeline); finche' non e' pronto si usa l'immagine di sfondo del film.
    public sealed partial class PlayerForm
    {
        private Thumbnailer? _ambientThumb;
        private Bitmap? _ambientFrame;
        private string? _ambientPath;
        private double _ambientSeconds;
        private int _ambientBusy;

        /// <summary>La cattura dello schermo mostra davvero cio' che c'e' sotto al menu. Con madVR a schermo
        /// intero no: il renderer passa alla modalita' esclusiva (e in HDR non usa la composizione di Windows),
        /// e leggendo lo schermo si ottiene il desktop. In finestra, e con gli altri renderer, la cattura e' fedele.</summary>
        private bool ScreenCaptureSeesVideo =>
            !(_engine != null && _currentMediaHasVideo && !IsPhotoMode && _activeRendererChoice == VRChoice.MADVR &&
              (FormBorderStyle == System.Windows.Forms.FormBorderStyle.None || IsRequestedOutputHdr()));

        /// <summary>Il meglio disponibile subito: un fotogramma recente dello stesso film, altrimenti l'immagine di sfondo del titolo.</summary>
        private Bitmap? CurrentMenuAmbient()
        {
            try
            {
                string? path = _currentPath;
                if (_ambientFrame != null && string.Equals(path, _ambientPath, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(GetTimelinePositionForHud() - _ambientSeconds) < 45)
                    return new Bitmap(_ambientFrame);

                string? art = ResolveRemoteArtworkPath(path);
                // Film aperto da Spotlight o dal telecomando: la libreria puo' non avere ancora
                // l'immagine del titolo. Senza nulla il menu usciva nero opaco invece che di vetro,
                // quindi si prova anche con lo sfondo e la locandina gia' in cache.
                if (string.IsNullOrWhiteSpace(art) || !File.Exists(art))
                {
                    string key = ResolveMetadataLookupKey(path ?? string.Empty);
                    foreach (string candidate in new[] { path ?? string.Empty, key })
                    {
                        if (string.IsNullOrWhiteSpace(candidate)) continue;
                        art = MovieMetadataService.GetCachedBackdropPath(candidate) ?? MovieMetadataService.GetCachedPosterPath(candidate);
                        if (!string.IsNullOrWhiteSpace(art) && File.Exists(art)) break;
                    }
                }
                if (!string.IsNullOrWhiteSpace(art) && File.Exists(art))
                {
                    using var image = Image.FromFile(art);
                    int width = Math.Min(480, image.Width), height = Math.Max(1, image.Height * width / Math.Max(1, image.Width));
                    var small = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                    using var g = Graphics.FromImage(small);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(image, 0, 0, width, height);
                    return small;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Legge in background il fotogramma del punto attuale e lo passa al menu, se e' ancora aperto.</summary>
        private void RefreshMenuAmbient(ContextOverlayMenuForm overlay)
        {
            string? path = _currentPath;
            if (string.IsNullOrWhiteSpace(path) || Volatile.Read(ref _scrubActive)) return;
            if (Interlocked.CompareExchange(ref _ambientBusy, 1, 0) != 0) return;
            double seconds = GetTimelinePositionForHud();
            _ = Task.Run(() =>
            {
                Bitmap? frame = null;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var thumb = _ambientThumb ??= new Thumbnailer();
                    thumb.Open(path);
                    frame = thumb.Get(seconds, 480, timeout.Token, realtime: true, storyboard: true);
                }
                catch (Exception ex) { Dbg.Warn("[MENU] ambient frame: " + ex.Message); }
                finally { Interlocked.Exchange(ref _ambientBusy, 0); }
                if (frame == null) return;
                if (!TryBeginInvokeOnUi(() =>
                {
                    if (!string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)) { frame.Dispose(); return; }
                    _ambientFrame?.Dispose();
                    _ambientFrame = frame;
                    _ambientPath = path;
                    _ambientSeconds = seconds;
                    if (ReferenceEquals(_contextOverlayMenu, overlay) && !overlay.IsDisposed)
                        overlay.UpdateAmbient(frame);
                })) frame.Dispose();
            });
        }

        private void ReleaseMenuAmbient()
        {
            try { _ambientFrame?.Dispose(); } catch { }
            _ambientFrame = null;
            _ambientPath = null;
            var thumb = Interlocked.Exchange(ref _ambientThumb, null);
            if (thumb != null) _ = Task.Run(() => { try { thumb.Dispose(); } catch { } });
        }
    }
}
