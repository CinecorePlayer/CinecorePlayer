using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// Barra delle foto (PhotoHudOverlay e PlayerForm.PhotoTools di Windows): in fondo la fila delle miniature vicine, sopra
/// adatta, zoom −/%/+, ruota, modifica, presentazione, condividi, elimina, e "…" con copia, apri con, stampa e Finder.
/// Le azioni usano macOS (pannello Condividi, appunti, Launch Services, Cestino).
/// </summary>
internal sealed partial class LibraryView
{
    private Rect _photoMoreAnchor, _photoShareAnchor, _photoDeleteAnchor;
    private string? _photoToolPressed;

    private string? CurrentLocalPhoto()
    {
        if (_photoList == null) return null;
        string path = _photoList[_photoIndex].Path;
        if (path.Contains("://", StringComparison.Ordinal) || !File.Exists(path)) { PhotoToast = "Disponibile solo per le foto salvate sul Mac"; return null; }
        return path;
    }

    /// <summary>Messaggio breve in basso (al posto della capsula del player di Windows).</summary>
    private string _photoToast = "";
    private DateTime _photoToastAt;
    private string PhotoToast { set { _photoToast = value; _photoToastAt = DateTime.UtcNow; _frames.Start(); InvalidateVisual(); } }

    private void DrawPhotoToolbar(DrawingContext ctx, Rect page)
    {
        if (_photoList == null) return;
        double m = S(24);
        // Vignettatura in basso: stacca icone e miniature dalla foto.
        ctx.FillRectangle(Vertical(Colors.Black, (0, 0), (1, 170)), new Rect(0, page.Bottom - S(200), page.Width, S(200)));
        // Miniature vicine (al massimo nove).
        double thumbH = S(58), thumbW = Math.Round(thumbH * 16 / 10), thumbGap = S(8), pad = S(8);
        double barBottom = page.Bottom - m;
        if (_photoList.Count > 1)
        {
            int fit = Math.Max(1, (int)((page.Width - m * 2 - pad * 2 + thumbGap) / (thumbW + thumbGap)));
            int count = Math.Min(Math.Min(_photoList.Count, fit), 9);
            int first = Math.Clamp(_photoIndex - count / 2, 0, Math.Max(0, _photoList.Count - count));
            double stripW = pad * 2 + count * thumbW + (count - 1) * thumbGap;
            var strip = new Rect((page.Width - stripW) / 2, page.Bottom - m - thumbH - pad * 2, stripW, thumbH + pad * 2);
            for (int i = 0; i < count; i++)
            {
                int index = first + i;
                var r = new Rect(strip.X + pad + i * (thumbW + thumbGap), strip.Y + pad, thumbW, thumbH);
                using (ctx.PushGeometryClip(new RectangleGeometry(r, S(6), S(6))))
                {
                    var item = _photoList[index];
                    if (Image(item.ArtPath ?? item.Path, thumbW) is { } bmp) DrawCover(ctx, bmp, r);
                    else ctx.FillRectangle(B(Color.FromRgb(30, 34, 40)), r);
                    if (index != _photoIndex && !Hover(r)) ctx.FillRectangle(B(Color.FromArgb(110, 0, 0, 0)), r);
                }
                if (index == _photoIndex) ctx.DrawRectangle(null, new Pen(Brushes.White, Math.Max(1.5, S(2))), r.Deflate(1), S(6), S(6));
                _hits.Add(new Hit(r, "photo-thumb", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            barBottom = strip.Y - S(12);
        }
        double barH = S(48), icon = S(44), zoomW = S(64), sep = S(22);
        double barW = S(10) * 2 + icon * 9 + zoomW + sep;
        double x = (page.Width - barW) / 2 + S(10), top = barBottom - barH + (barH - icon) / 2;
        Rect Next(double w) { var r = new Rect(x, top, w, icon); x += w; return r; }
        void Tool(Rect r, string name, string kind)
        {
            double g = S(22);
            var glyph = new Rect(r.Center.X - g / 2, r.Center.Y - g / 2, g, g);
            // Riscontro al passaggio e alla pressione: un disco tenue dietro l'icona (prima cambiava solo la luminosita').
            if (Hover(r))
            {
                double d = Math.Min(r.Width, r.Height) - S(4);
                ctx.DrawEllipse(B(Color.FromArgb((byte)(_photoToolPressed == kind ? 70 : 38), 255, 255, 255)), null, r.Center, d / 2, d / 2);
            }
            WinSymbol.Draw(ctx, glyph.Translate(new Vector(0, Math.Max(1, S(1)))), name, Color.FromArgb(100, 0, 0, 0));
            WinSymbol.Draw(ctx, glyph, name, Color.FromArgb((byte)(Hover(r) ? 255 : 205), 255, 255, 255));
            _hits.Add(new Hit(r, kind));
        }
        Tool(Next(icon), "scan", "photo-fit");
        Tool(Next(icon), "zoom-out", "photo-zoom-out");
        var zoomText = Next(zoomW);
        TextAt(ctx, $"{Math.Round(_photoZoom * 100)}%", Math.Max(8.6, 9.5 * _s), DisplaySemibold, Color.FromArgb(235, 255, 255, 255), zoomText, TextAlignment.Center);
        Tool(Next(icon), "zoom-in", "photo-zoom-in");
        ctx.FillRectangle(B(Color.FromArgb(60, 255, 255, 255)), new Rect(x + sep / 2, barBottom - barH + S(14), 1, barH - S(28)));
        x += sep;
        Tool(Next(icon), "rotate-cw", "photo-rotate");
        Tool(Next(icon), "edit", "photo-edit");
        Tool(Next(icon), _slideshow?.IsEnabled == true ? "pause" : "play", "photo-slideshow");
        _photoShareAnchor = Next(icon); Tool(_photoShareAnchor, "share", "photo-share");
        _photoDeleteAnchor = Next(icon); Tool(_photoDeleteAnchor, "trash", "photo-delete");
        _photoMoreAnchor = Next(icon); Tool(_photoMoreAnchor, "more", "photo-more");

        if (_photoToast.Length > 0)
        {
            double age = (DateTime.UtcNow - _photoToastAt).TotalMilliseconds;
            if (age > 2600) _photoToast = "";
            else
            {
                double a = Math.Clamp(Math.Min(age / 150, (2600 - age) / 300), 0, 1);
                double pt = Math.Max(9, 10.5 * _s), w = Measure(_photoToast, pt, DisplaySemibold) + S(40);
                var pill = new Rect((page.Width - w) / 2, S(76), w, S(40));
                ctx.DrawRectangle(B(Color.FromArgb((byte)(200 * a), 20, 22, 28)), null, pill, pill.Height / 2, pill.Height / 2);
                TextAt(ctx, _photoToast, pt, DisplaySemibold, Color.FromArgb((byte)(255 * a), 255, 255, 255), pill, TextAlignment.Center);
            }
        }
    }

    private bool PhotoToolActivate(Hit hit)
    {
        if (hit.Kind.StartsWith("photo-", StringComparison.Ordinal) && hit.Kind != "photo-thumb")
        {
            _photoToolPressed = hit.Kind; InvalidateVisual();
            Avalonia.Threading.DispatcherTimer.RunOnce(() => { _photoToolPressed = null; InvalidateVisual(); }, TimeSpan.FromMilliseconds(160));
        }
        if (_photoList == null) return false;
        switch (hit.Kind)
        {
            case "photo-thumb": if (int.TryParse(hit.Key, out int i)) { _photoIndex = Math.Clamp(i, 0, _photoList.Count - 1); ResetPhotoView(); InvalidateVisual(); } return true;
            case "photo-fit": ResetPhotoView(); InvalidateVisual(); return true;
            case "photo-zoom-out": ZoomPhoto(1 / 1.25); return true;
            case "photo-zoom-in": ZoomPhoto(1.25); return true;
            case "photo-rotate": _photoRotation = (_photoRotation + 1) % 4; _photoPan = default; InvalidateVisual(); return true;
            case "photo-slideshow": ToggleSlideshow(); return true;
            case "photo-edit": EditCurrentPhoto(); return true;
            case "photo-share":
                _slideshow?.Stop();
                if (CurrentLocalPhoto() is { } share && TopLevel.GetTopLevel(this) is { } top)
                {
                    var origin = this.TranslatePoint(default, top) ?? default;
                    MacFileActions.Share(top, share, _photoShareAnchor.Translate(new Vector(origin.X, origin.Y)));
                }
                return true;
            case "photo-delete": ConfirmDeletePhoto(); return true;
            case "photo-more": ShowPhotoMenu(new Point(_photoMoreAnchor.Right, _photoMoreAnchor.Y - S(12))); return true;
        }
        return false;
    }

    /// <summary>Copia, apri con, stampa, Finder: le azioni dirette della barra di Windows.</summary>
    private void ShowPhotoMenu(Point at)
    {
        if (CurrentLocalPhoto() is not { } path) return;
        _slideshow?.Stop();
        var openWith = new MenuEntry("Apri con", "openwith") { Children = new() };
        foreach (var (name, app) in MacFileActions.OpenWithApps(path).Take(12))
            openWith.Children.Add(new MenuEntry(name, null, () => MacFileActions.OpenWith(app, path)));
        if (openWith.Children.Count == 0) openWith.Children.Add(new MenuEntry("Nessuna app") { Enabled = false });
        var entries = new List<MenuEntry>
        {
            new("Copia", "copy", () => PhotoToast = MacFileActions.CopyImage(path) ? "Foto copiata negli appunti" : "Copia non riuscita"),
            openWith,
            new("Stampa…", "print", () => MacFileActions.Print(path)),
            new("Mostra nel Finder", "folder", () => MacFileActions.RevealInFinder(path)),
            MenuEntry.Sep(),
            new("Modifica…", "edit", EditCurrentPhoto),
            new("Sposta nel Cestino", "trash", ConfirmDeletePhoto),
        };
        ShowTextMenu(entries, at, above: true);
    }

    /// <summary>Conferma prima del Cestino (MessageBox di Windows): un menu con la scelta, il no evidenziato per secondo.</summary>
    private void ConfirmDeletePhoto()
    {
        if (CurrentLocalPhoto() is not { } path) return;
        _slideshow?.Stop();
        var entries = new List<MenuEntry>
        {
            new("Sposta “" + Path.GetFileName(path) + "” nel Cestino", "trash", () => DeletePhoto(path)),
            new("Annulla", "close", () => { }),
        };
        ShowTextMenu(entries, new Point(_photoDeleteAnchor.Right, _photoDeleteAnchor.Y - S(12)), above: true);
    }

    private void DeletePhoto(string path)
    {
        if (_photoList == null) return;
        int index = _photoList.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (!MacFileActions.Trash(path)) { PhotoToast = "Non è stato possibile eliminarla"; return; }
        if (index >= 0) _photoList.RemoveAt(index);
        if (_photoList.Count == 0) { ClosePhoto(); return; }
        _photoIndex = Math.Clamp(_photoIndex, 0, _photoList.Count - 1);
        ResetPhotoView();
        PhotoToast = "Foto spostata nel Cestino";
        _ = System.Threading.Tasks.Task.Run(async () => { await LibraryStore.ScanAsync("Photos"); Avalonia.Threading.Dispatcher.UIThread.Post(Reload); });
    }

    /// <summary>La finestra apre l'editor (PhotoEditView) sopra il visualizzatore.</summary>
    public event Action<string>? PhotoEditRequested;

    private void EditCurrentPhoto()
    {
        if (CurrentLocalPhoto() is not { } path) return;
        _slideshow?.Stop();
        PhotoEditRequested?.Invoke(path);
    }

    /// <summary>La copia modificata entra nella serie subito dopo l'originale e viene mostrata.</summary>
    public void PhotoEdited(string? saved)
    {
        Focus();
        if (saved == null || _photoList == null) return;
        if (string.Equals(System.IO.Path.GetDirectoryName(saved), System.IO.Path.GetDirectoryName(_photoList[_photoIndex].Path), StringComparison.OrdinalIgnoreCase))
        {
            _photoList.Insert(_photoIndex + 1, new LibraryItem { Path = saved, Title = System.IO.Path.GetFileNameWithoutExtension(saved), Category = "Photos", ArtPath = saved, SortDateUtc = DateTime.UtcNow });
            _photoIndex++;
            ResetPhotoView();
        }
        PhotoToast = "Copia salvata: " + System.IO.Path.GetFileName(saved);
    }
}
