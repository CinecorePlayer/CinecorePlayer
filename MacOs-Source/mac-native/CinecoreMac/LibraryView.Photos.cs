using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CinecoreMac;

// Foto e Video (DrawTemporalGrid / DrawTemporalCard di Windows): sezioni per mese, schede 16:9 con l'immagine
// intera su nero e il nome sotto. Un clic su una foto apre il visualizzatore a tutta finestra (frecce, Esc).
internal sealed partial class LibraryView
{
    private static readonly string[] MonthNames = { "Gennaio", "Febbraio", "Marzo", "Aprile", "Maggio", "Giugno", "Luglio", "Agosto", "Settembre", "Ottobre", "Novembre", "Dicembre" };

    private static bool UsesTemporalSections(string category) => category is "Photos" or "Videos";

    /// <summary>BuildTemporalSections: per mese, dal piu' recente; "Senza data" in fondo.</summary>
    private List<(string Title, List<LibraryItem> Items)> TemporalSections(IReadOnlyList<LibraryItem> items)
    {
        // Come Windows: per nome o dimensione una sola sezione; "Meno recenti" tiene i mesi, dal piu' vecchio.
        if ((_category == "Photos" && _sortMode is 1 or 3) || (_category == "Videos" && _sortMode != 0))
            return new() { (_category == "Photos" ? "Tutte le foto" : "Tutti i video", items.ToList()) };
        bool oldest = _category == "Photos" && _sortMode == 2;
        var sections = items
            .GroupBy(i => i.SortDateUtc <= DateTime.MinValue.AddDays(1) ? DateTime.MinValue : new DateTime(i.SortDateUtc.ToLocalTime().Year, i.SortDateUtc.ToLocalTime().Month, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => (g.Key == DateTime.MinValue ? "Senza data" : $"{L10n.T(MonthNames[g.Key.Month - 1])} {g.Key.Year}",
                          g.OrderByDescending(i => i.SortDateUtc).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();
        if (oldest)
        {
            sections.Reverse();
            foreach (var section in sections) section.Item2.Reverse();
        }
        return sections;
    }

    private void DrawTemporalGrid(DrawingContext ctx, Rect viewport, IReadOnlyList<LibraryItem> visible)
    {
        var sections = TemporalSections(visible);
        bool photos = _category == "Photos";
        double gap = Math.Max(S(20), Math.Round(viewport.Width * 0.010));
        double rowGap = S(20), sectionGap = S(30), headerH = S(32);
        double targetCardW = S(248);
        int columns = (int)Math.Max(2, Math.Min(8, Math.Floor((viewport.Width + gap) / Math.Max(1, targetCardW + gap))));
        double cardW = Math.Max(1, Math.Floor((viewport.Width - gap * (columns - 1)) / columns));
        double mediaH = Math.Max(S(140), Math.Round(cardW * 9 / 16.0));
        double infoH = S(photos ? 46 : 60), cardH = mediaH + infoH;

        double contentH = 0;
        foreach (var section in sections)
        {
            int rows = (section.Items.Count + columns - 1) / columns;
            contentH += headerH + S(12) + rows * cardH + Math.Max(0, rows - 1) * rowGap + sectionGap;
        }
        contentH = Math.Max(0, contentH - sectionGap) + S(24);
        _scrollMax = Math.Max(0, contentH - viewport.Height);
        _scroll = Math.Clamp(_scroll, 0, _scrollMax); _scrollTarget = Math.Clamp(_scrollTarget, 0, _scrollMax);

        using (ctx.PushClip(new Rect(0, viewport.Y - S(6), Bounds.Width, viewport.Height + S(6))))
        {
            double y = viewport.Y - _scroll;
            foreach (var section in sections)
            {
                if (y + headerH >= viewport.Y && y <= viewport.Bottom)
                    TextAt(ctx, section.Title, Math.Max(10.5, 14 * _s), DisplaySemibold, Text, new Rect(viewport.X, y, viewport.Width, headerH));
                y += headerH + S(12);
                int rows = (section.Items.Count + columns - 1) / columns;
                for (int row = 0; row < rows; row++)
                {
                    if (y > viewport.Bottom) break;
                    if (y + cardH >= viewport.Y)
                        for (int col = 0; col < columns; col++)
                        {
                            int index = row * columns + col;
                            if (index >= section.Items.Count) break;
                            DrawTemporalCard(ctx, new Rect(viewport.X + col * (cardW + gap), y, cardW, cardH), mediaH, section.Items[index], photos, viewport);
                        }
                    y += cardH + rowGap;
                }
                y += sectionGap - rowGap;
                if (y > viewport.Bottom + S(400)) break;
            }
        }
    }

    private void DrawTemporalCard(DrawingContext ctx, Rect r, double mediaH, LibraryItem item, bool photo, Rect viewport)
    {
        var media = new Rect(r.X, r.Y, r.Width, mediaH);
        double radius = S(6);
        bool hover = Hover(r);
        string? art = photo ? item.ArtPath ?? item.Path : item.WideArtPath ?? item.ArtPath;
        var geometry = new RectangleGeometry(media, radius, radius);
        using (ctx.PushGeometryClip(geometry))
        {
            // PaintTemporalMedia: fondo nero e immagine intera (niente ritagli sulle foto verticali).
            ctx.FillRectangle(B(Color.FromRgb(10, 12, 16)), media);
            if (Image(art, media.Width) is { } bmp) DrawContain(ctx, bmp, media);
            else if (!photo) DrawGeneratedArt(ctx, media, item.Title, false, item.Category);
        }
        bool light = UiTheme.IsLight;
        var pen = hover ? new Pen(B(light ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255)), 2)
                        : new Pen(B(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255)), 1);
        ctx.DrawRectangle(null, pen, media.Deflate(hover ? 1 : .5), radius, radius);
        TextAt(ctx, item.Title, Math.Max(9.4, 11 * _s), DisplayRegular, Text, new Rect(r.X + 1, media.Bottom + S(8), r.Width - 2, S(24)));
        // Le zone di clic sopra la griglia (sotto la barra) non contano.
        var hit = r.Intersect(new Rect(0, viewport.Y, Bounds.Width, Bounds.Height));
        if (hit.Width > 0 && hit.Height > 0) _hits.Add(new Hit(hit, photo ? "photo" : "poster", item.Path, item));
    }

    private static void DrawContain(DrawingContext ctx, Bitmap bmp, Rect dest)
    {
        double sw = bmp.Size.Width, sh = bmp.Size.Height;
        if (sw <= 0 || sh <= 0) return;
        double k = Math.Min(dest.Width / sw, dest.Height / sh);
        double w = sw * k, h = sh * k;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(bmp, new Rect(0, 0, sw, sh), new Rect(dest.X + (dest.Width - w) / 2, dest.Y + (dest.Height - h) / 2, w, h));
    }

    // ----- visualizzatore -----
    private List<LibraryItem>? _photoList;
    private int _photoIndex;
    private DateTime _photoOpenedAt;
    private bool PhotoOpen => _photoList != null;
    // Zoom, rotazione (quarti di giro) e spostamento della foto ingrandita; presentazione ogni 5 secondi.
    private double _photoZoom = 1;
    private int _photoRotation;
    private Vector _photoPan;
    private Point? _photoDrag;
    private DispatcherTimer? _slideshow;

    private void ResetPhotoView() { _photoZoom = 1; _photoRotation = 0; _photoPan = default; _photoDrag = null; }

    private void ZoomPhoto(double factor)
    {
        _photoZoom = Math.Clamp(_photoZoom * factor, 1, 8);
        if (_photoZoom <= 1.001) { _photoZoom = 1; _photoPan = default; }
        InvalidateVisual();
    }

    private void ToggleSlideshow()
    {
        if (_slideshow?.IsEnabled == true) { _slideshow.Stop(); InvalidateVisual(); return; }
        _slideshow ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) =>
        {
            if (_photoList == null) { _slideshow!.Stop(); return; }
            if (_photoIndex >= _photoList.Count - 1) { _slideshow!.Stop(); InvalidateVisual(); return; }
            StepPhoto(1);
        });
        _slideshow.Start();
        InvalidateVisual();
    }

    private bool PhotoWheel(PointerWheelEventArgs e)
    {
        if (_photoList == null) return false;
        ZoomPhoto(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        return true;
    }

    /// <summary>Foto ingrandita: si trascina. True se il clic e' stato preso per lo spostamento.</summary>
    private bool PhotoPressed(Point p)
    {
        if (_photoList == null || _photoZoom <= 1) return false;
        if (HitAt(p) is { Kind: not "photo-viewer" } h && h.Kind.StartsWith("photo-")) return false;
        _photoDrag = p;
        return true;
    }

    private bool PhotoDragged(Point p, bool pressed)
    {
        if (_photoDrag is not { } from) return false;
        if (!pressed) { _photoDrag = null; return false; }
        _photoPan += p - from; _photoDrag = p;
        InvalidateVisual();
        return true;
    }

    /// <summary>La finestra apre il visualizzatore (PhotoViewerView, controlli di Avalonia).</summary>
    public event Action<IReadOnlyList<LibraryItem>, int>? PhotoViewerRequested;

    /// <summary>Una foto e' finita nel Cestino dal visualizzatore: la libreria si rilegge.</summary>
    public void PhotoDeletedOutside() =>
        _ = System.Threading.Tasks.Task.Run(async () => { await LibraryStore.ScanAsync("Photos"); Avalonia.Threading.Dispatcher.UIThread.Post(Reload); });

    private void OpenPhoto(LibraryItem item)
    {
        var list = TemporalSections(CollectionItems()).SelectMany(s => s.Items).ToList();
        int index = list.FindIndex(i => i.Path == item.Path);
        if (index < 0) { list = new() { item }; index = 0; }
        if (PhotoViewerRequested != null)
        {
            if (_searchBox?.IsFocused == true) SetSearchOpen(false);
            PhotoViewerRequested(list, index);
            return;
        }
        _photoList = list; _photoIndex = index; _photoOpenedAt = DateTime.UtcNow;
        // I tasti (frecce, Spazio, + - 0 R, Inizio/Fine) vanno alla foto: prima restavano alla casella di ricerca.
        if (_searchBox?.IsFocused == true) SetSearchOpen(false);
        Focus();
        _frames.Start();
        InvalidateVisual();
    }

    private void ClosePhoto() { _photoList = null; _slideshow?.Stop(); ResetPhotoView(); InvalidateVisual(); }

    private void StepPhoto(int delta)
    {
        if (_photoList == null) return;
        _photoIndex = Math.Clamp(_photoIndex + delta, 0, _photoList.Count - 1);
        ResetPhotoView();
        InvalidateVisual();
    }

    private bool HandlePhotoKey(KeyEventArgs e)
    {
        if (_photoList == null) return false;
        switch (e.Key)
        {
            case Key.Escape when _photoZoom > 1 || _photoRotation != 0: ResetPhotoView(); InvalidateVisual(); break;
            case Key.Escape: case Key.Back: ClosePhoto(); break;
            case Key.Left: StepPhoto(-1); break;
            case Key.Right: StepPhoto(1); break;
            // Come su Windows: Spazio presentazione, + - 0 zoom e vista iniziale, R rotazione.
            case Key.Space: ToggleSlideshow(); break;
            case Key.OemPlus: case Key.Add: ZoomPhoto(1.25); break;
            case Key.OemMinus: case Key.Subtract: ZoomPhoto(1 / 1.25); break;
            case Key.D0: case Key.NumPad0: ResetPhotoView(); InvalidateVisual(); break;
            case Key.R: _photoRotation = (_photoRotation + 1) % 4; _photoPan = default; InvalidateVisual(); break;
            case Key.Home: StepPhoto(-_photoIndex); break;
            case Key.End: StepPhoto(_photoList.Count); break;
        }
        e.Handled = true;
        return true;
    }

    private void DrawPhotoViewer(DrawingContext ctx, Rect page)
    {
        if (_photoList == null) return;
        double t = Math.Clamp((DateTime.UtcNow - _photoOpenedAt).TotalMilliseconds / 200, 0, 1);
        using var fade = ctx.PushOpacity(1 - Math.Pow(1 - t, 3));
        ctx.FillRectangle(Brushes.Black, page);
        _hitFloor = _hits.Count;
        var item = _photoList[_photoIndex];
        var area = page.Deflate(new Thickness(S(24), S(64), S(24), S(24)));
        // Prima la miniatura gia' pronta, poi la foto a piena risoluzione (Image sceglie la misura piu' vicina).
        if ((Image(item.Path, area.Width * Math.Min(_photoZoom, 2.5)) ?? Image(item.ArtPath, 512)) is { } bmp)
        {
            // Rotazione e zoom attorno al centro; con un quarto di giro la foto si adatta con i lati scambiati.
            bool quarter = _photoRotation % 2 == 1;
            var fit = quarter ? new Rect(0, 0, area.Height, area.Width) : new Rect(0, 0, area.Width, area.Height);
            var transform = Matrix.CreateTranslation(-fit.Width / 2, -fit.Height / 2) * Matrix.CreateScale(_photoZoom, _photoZoom)
                * Matrix.CreateRotation(Math.PI / 2 * _photoRotation) * Matrix.CreateTranslation(area.Center.X + _photoPan.X, area.Center.Y + _photoPan.Y);
            using (ctx.PushClip(page))
            using (ctx.PushTransform(transform))
                DrawContain(ctx, bmp, fit);
        }
        // Due pagine vicine pronte per le frecce.
        foreach (int near in new[] { _photoIndex - 1, _photoIndex + 1 })
            if (near >= 0 && near < _photoList.Count) Image(_photoList[near].Path, area.Width);

        string date = item.SortDateUtc > DateTime.MinValue.AddDays(1) ? item.SortDateUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";
        double pt = Math.Max(10, 12 * _s);
        TextAt(ctx, item.Title, pt, DisplaySemibold, Colors.White, new Rect(S(90), S(18) + TopShift, page.Width / 2, S(28)));
        TextAt(ctx, $"{_photoIndex + 1} di {_photoList.Count}" + (date.Length > 0 ? "   ·   " + date : ""), Math.Max(9, 10.5 * _s), DisplayRegular, Color.FromArgb(170, 255, 255, 255),
            new Rect(page.Width / 2, S(18) + TopShift, page.Width / 2 - S(70), S(28)), TextAlignment.Right);
        var close = new Rect(page.Width - S(56), S(14) + TopShift, S(36), S(36));
        double c = S(7);
        var pen = new Pen(B(Color.FromArgb(Hover(close) ? (byte)255 : (byte)190, 255, 255, 255)), Math.Max(1.5, S(2)), lineCap: PenLineCap.Round);
        ctx.DrawLine(pen, new Point(close.Center.X - c, close.Center.Y - c), new Point(close.Center.X + c, close.Center.Y + c));
        ctx.DrawLine(pen, new Point(close.Center.X + c, close.Center.Y - c), new Point(close.Center.X - c, close.Center.Y + c));
        _hits.Add(new Hit(close, "photo-close"));
        // Meta' sinistra/destra: foto precedente/successiva, con la freccia al passaggio del mouse.
        var prev = new Rect(0, S(64), page.Width * 0.3, page.Height - S(64));
        var next = new Rect(page.Width * 0.7, S(64), page.Width * 0.3, page.Height - S(64));
        if (_slideshow?.IsEnabled == true)
            TextAt(ctx, "Presentazione", Math.Max(9, 10.5 * _s), DisplayRegular, Color.FromArgb(150, 255, 255, 255), new Rect(0, page.Bottom - S(40), page.Width, S(24)), TextAlignment.Center);
        // Ingrandita: i lati servono a trascinare, non a cambiare foto.
        if (_photoZoom <= 1)
        {
            if (_photoIndex > 0)
            {
                _hits.Add(new Hit(prev, "photo-prev"));
                if (Hover(prev)) Chevron(ctx, S(44), page.Height / 2, S(12), Color.FromArgb(220, 255, 255, 255), Math.Max(2, S(2.5)), left: true);
            }
            if (_photoIndex < _photoList.Count - 1)
            {
                _hits.Add(new Hit(next, "photo-next"));
                if (Hover(next)) Chevron(ctx, page.Width - S(44), page.Height / 2, S(12), Color.FromArgb(220, 255, 255, 255), Math.Max(2, S(2.5)), right: true);
            }
        }
        DrawPhotoToolbar(ctx, page);
    }

    private bool PhotoActivate(Hit hit)
    {
        switch (hit.Kind)
        {
            case "photo": if (hit.Item != null) OpenPhoto(hit.Item); return true;
            case "photo-close": ClosePhoto(); return true;
            case "photo-prev": StepPhoto(-1); return true;
            case "photo-next": StepPhoto(1); return true;
        }
        return PhotoToolActivate(hit);
    }

    internal void TestSetSource(string name)
    {
        var server = name == "jellyfin" ? NetworkStore.Servers.FirstOrDefault(x => x.IsJellyfin && x.Account != null) : name == "dlna" ? NetworkStore.Servers.FirstOrDefault(x => !x.IsJellyfin && x.Available)
            : NetworkStore.Servers.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && (name.Length > 0));
        SetSource(server);
    }

    internal void TestOpenFirstPhoto()
    {
        if (CollectionItems().FirstOrDefault() is { } first) OpenPhoto(first);
    }

    internal void TestOpenPhotoFile(string path)
    {
        // Prove: tutte le foto della stessa cartella, cosi' ci sono frecce e miniature.
        var all = System.IO.Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(path) ?? ".")
            .Where(f => System.IO.Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".heic")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new LibraryItem { Path = f, Title = System.IO.Path.GetFileNameWithoutExtension(f), Category = "Photos", ArtPath = f, SortDateUtc = System.IO.File.GetLastWriteTimeUtc(f) }).ToList();
        int index = Math.Max(0, all.FindIndex(i => i.Path == path));
        if (PhotoViewerRequested != null && all.Count > 0) { PhotoViewerRequested(all, index); return; }
        OpenPhoto(new LibraryItem { Path = path, Title = System.IO.Path.GetFileNameWithoutExtension(path), Category = "Photos", ArtPath = path });
    }
    internal void TestPhotoView(string what)
    {
        if (what == "rotate") _photoRotation = (_photoRotation + 1) % 4;
        else if (what == "zoom") ZoomPhoto(2);
        else if (what == "edit") EditCurrentPhoto();
        else if (what == "more") ShowPhotoMenu(new Point(_photoMoreAnchor.Right, _photoMoreAnchor.Y - S(12)));
        InvalidateVisual();
    }
}
