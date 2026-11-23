using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CinecoreMac;

/// <summary>
/// Scheda "Cartelle" (AddSourceForCurrentCategory di Windows): "Cartelle: Film ˅" per la categoria, il riepilogo,
/// una card per cartella (foto dei titoli che si alternano e sfumano nella card, stato e spazio del disco, il numero
/// grande, la X per toglierla) e in fondo la card tratteggiata con il "+". Chiusa la scheda, le categorie cambiate si
/// rileggono.
/// </summary>
internal sealed partial class LibraryView
{
    private sealed class FolderStats
    {
        public int Count, Groups;
        public long Bytes = -1, Free = -1;
        public bool Online, Known;
        public string Kind = "Disco locale";
        public List<string> Slides = new();
    }

    private bool _foldersOpen, _slideWake;
    private string _foldersCategory = "Movies";
    private List<string> _folderRoots = new();
    private readonly Dictionary<string, FolderStats> _folderStats = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foldersChanged = new();
    private double _foldersScroll;
    private DateTime _foldersOpenedAt;
    private int _folderVersion;
    private readonly DateTime _slideClock = DateTime.UtcNow;
    private const double FolderRowH = 176, FolderListTop = 134, SlideEveryMs = 5200, SlideFadeMs = 900;
    private static readonly string[] FolderCategoryKeys = { "Movies", "TV Series", "Music", "Photos", "Videos" };

    private static string CategoryName(string key) => key switch
    {
        "Movies" => "Film", "TV Series" => "Serie TV", "Music" => "Musica", "Photos" => "Foto", "Videos" => "Video", _ => key,
    };

    private void OpenFolders()
    {
        // La categoria si sceglie nella scheda: dalla Home si parte da Film.
        _foldersCategory = _view == "Collection" && FolderCategoryKeys.Contains(_category) ? _category : "Movies";
        _foldersChanged.Clear();
        _foldersOpen = true;
        _foldersOpenedAt = DateTime.UtcNow;
        RefreshFolders();
        _frames.Start();
    }

    private async void CloseFolders()
    {
        _foldersOpen = false;
        InvalidateVisual();
        foreach (var c in _foldersChanged.ToList()) await LibraryStore.ScanAsync(c);
        if (_foldersChanged.Count > 0) { _foldersChanged.Clear(); _heroDeck.Clear(); Reload(); }
    }

    private void RefreshFolders()
    {
        _folderRoots = LibraryStore.GetRoots(_foldersCategory);
        var files = LibraryStore.IndexedFiles(_foldersCategory);
        var items = LibraryStore.Items(_foldersCategory);
        _folderStats.Clear();
        foreach (var root in _folderRoots)
        {
            string prefix = root.TrimEnd('/') + "/";
            var inFolder = items.Where(i => i.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            var st = new FolderStats
            {
                Count = files.Count(f => f.StartsWith(prefix, StringComparison.Ordinal)),
                Groups = _foldersCategory == "TV Series" ? inFolder.Select(i => i.SeriesTitle ?? i.Title).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() : 0,
                // Le foto larghe dei titoli aggiunti per ultimi; le locandine solo se non c'e' altro.
                Slides = inFolder.OrderByDescending(i => i.SortDateUtc).Select(i => i.WideArtPath ?? i.ArtPath).Where(p => p != null).Select(p => p!).Distinct().Take(12).ToList(),
            };
            _folderStats[root] = st;
        }
        // Prima le cartelle con contenuti, poi le altre.
        _folderRoots = _folderRoots.OrderBy(r => _folderStats[r].Count > 0 ? 0 : 1).ThenByDescending(r => _folderStats[r].Count).ToList();
        // Disco, spazio libero e dimensione (lenti su un disco di rete) arrivano dopo, in sottofondo.
        int version = ++_folderVersion;
        var roots = _folderRoots.Select(root => (Root: root, Stats: _folderStats[root])).ToList();
        _ = Task.Run(() =>
        {
            foreach (var entry in roots)
            {
                string root = entry.Root;
                var st = entry.Stats;
                bool online = false; long free = -1, bytes = 0; string kind = "Disco locale";
                try { online = Directory.Exists(root); } catch { }
                if (online)
                {
                    try
                    {
                        var drive = new DriveInfo(root);
                        free = drive.AvailableFreeSpace;
                        if (root.StartsWith("/Volumes/", StringComparison.Ordinal)) kind = drive.DriveType == DriveType.Network ? "Disco di rete" : "Disco esterno";
                        else if (drive.DriveType == DriveType.Network) kind = "Disco di rete";
                        // Dischi di rete montati con FUSE (rclone con macFUSE o FUSE-T), come "Disco di rete (rclone)" di Windows.
                        string format = drive.DriveFormat ?? "";
                        if (format.Contains("fuse", StringComparison.OrdinalIgnoreCase) || format.Contains("rclone", StringComparison.OrdinalIgnoreCase)
                            || IsFuseT(drive.Name)) kind = "Disco di rete (rclone)";
                    }
                    catch { }
                    foreach (var f in files.Where(f => f.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal)))
                        try { var fi = new FileInfo(f); bytes += fi.LinkTarget != null && fi.ResolveLinkTarget(true) is FileInfo target ? target.Length : fi.Length; } catch { }
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (version != _folderVersion) return;
                    (st.Online, st.Free, st.Kind, st.Bytes, st.Known) = (online, free, kind, bytes, true);
                    InvalidateVisual();
                });
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        });
        InvalidateVisual();
    }

    private static string SizeText(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString(u >= 3 ? "0.#" : "0", CultureInfo.InvariantCulture).Replace('.', ',') + " " + units[u];
    }

    private string ItemsLabel(int n) => _foldersCategory switch
    {
        "Movies" => n == 1 ? "1 film" : $"{n} film",
        "TV Series" => n == 1 ? "1 episodio" : $"{n} episodi",
        "Music" => n == 1 ? "1 brano" : $"{n} brani",
        "Photos" => n == 1 ? "1 foto" : $"{n} foto",
        _ => n == 1 ? "1 elemento" : $"{n} elementi",
    };

    private Rect _foldersSheet, _foldersTitleAnchor;

    private void DrawFolders(DrawingContext ctx, Rect page)
    {
        if (!_foldersOpen) return;
        double t = Math.Clamp((DateTime.UtcNow - _foldersOpenedAt).TotalMilliseconds / 200, 0, 1);
        ctx.FillRectangle(B(UiTheme.IsLight ? Color.FromArgb((byte)(138 * t), 175, 186, 197) : Color.FromArgb((byte)(210 * t), 0, 2, 6)), page);
        _hits.Add(new Hit(page, "folders-outside"));
        using var fade = ctx.PushOpacity(1 - (1 - t) * (1 - t));
        // Disegnata a misure fisse (come su Windows al 100%); se la finestra e' piu' stretta si riduce tutta.
        int rows = Math.Clamp(_folderRoots.Count + 1, 1, 4);
        double w = 1240, h = FolderListTop + rows * FolderRowH + 28;
        // Coordinate originali Windows, convertite in una scheda compatta sul Mac (non quasi tutta la finestra).
        double k = Math.Max(.1, Math.Min(.75, Math.Min(Math.Min(930, page.Width * .84) / w, page.Height * .82 / h)));
        var sheet = _foldersSheet = new Rect(Math.Round((page.Width - w * k) / 2), Math.Round((page.Height - h * k) / 2), w * k, h * k);
        using var sheetTransform = ctx.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(sheet.X, sheet.Y));
        var local = new Rect(0, 0, w, h);
        Color back = UiTheme.Card, edge = UiTheme.Mix(back, UiTheme.IsLight ? Colors.Black : Colors.White, .10);
        ctx.DrawRectangle(B(back), new Pen(B(edge), 1), local.Deflate(.5), 12, 12);
        var mouse = new Point((_mouse.X - sheet.X) / k, (_mouse.Y - sheet.Y) / k);
        void Hit(Rect r, string kind, string key = "") => _hits.Add(new Hit(new Rect(sheet.X + r.X * k, sheet.Y + r.Y * k, r.Width * k, r.Height * k), kind, key));
        _hits.Add(new Hit(sheet, "folders-sheet"));

        // "Cartelle: Film ˅": la categoria si sceglie accanto al titolo, con il solito menu a discesa.
        var titleFace = DisplaySemibold;
        string prefix = "Cartelle:", cat = CategoryName(_foldersCategory);
        double titlePt = 32 / (96.0 / 72 * 0.92);
        double pw = Measure(prefix, titlePt, titleFace), cw = Measure(cat, titlePt, titleFace);
        TextAt(ctx, prefix, titlePt, titleFace, UiTheme.Text, new Rect(40, 30, pw + 4, 48));
        var catRect = new Rect(40 + pw + 12, 30, cw + 30, 48);
        bool catHot = catRect.Contains(mouse) || _menuKey == "FolderCategory";
        TextAt(ctx, cat, titlePt, titleFace, catHot ? UiTheme.Text : A(UiTheme.Text, 200), new Rect(catRect.X, catRect.Y, cw + 4, 48));
        Chevron(ctx, catRect.X + cw + 16, catRect.Y + 26, 6, catHot ? UiTheme.Text : A(UiTheme.Text, 200), 2, _menuKey == "FolderCategory");
        _foldersTitleAnchor = new Rect(sheet.X + catRect.X * k, sheet.Y + catRect.Bottom * k, catRect.Width * k, 1);
        Hit(catRect, "folders-category");

        // X di chiusura in alto a destra.
        var close = new Rect(w - 72, 30, 40, 40);
        bool closeHot = close.Contains(mouse);
        double arm = 8.4, ccx = close.Center.X, ccy = close.Center.Y;
        var xPen = new Pen(B(closeHot ? UiTheme.Text : A(UiTheme.Text, 165)), 2.2, lineCap: PenLineCap.Round);
        ctx.DrawLine(xPen, new Point(ccx - arm, ccy - arm), new Point(ccx + arm, ccy + arm));
        ctx.DrawLine(xPen, new Point(ccx + arm, ccy - arm), new Point(ccx - arm, ccy + arm));
        Hit(close, "folders-close");

        int total = _folderStats.Values.Sum(s => s.Count);
        long totalBytes = _folderStats.Values.Where(s => s.Bytes > 0).Sum(s => s.Bytes);
        string summary = _folderRoots.Count == 0 ? "I contenuti delle cartelle aggiunte appariranno nella libreria."
            : (_folderRoots.Count == 1 ? "1 cartella" : $"{_folderRoots.Count} cartelle") + ", " + ItemsLabel(total) + (totalBytes > 0 ? ", " + SizeText(totalBytes) : "");
        TextAt(ctx, summary, 18 / (96.0 / 72 * 0.92), DisplayRegular, UiTheme.Muted, new Rect(41, 86, 760, 26));

        // Elenco: una card per cartella, poi quella con il "+".
        var list = new Rect(40, FolderListTop, w - 80, rows * FolderRowH);
        int count = _folderRoots.Count + 1;
        double maxScroll = Math.Max(0, count * FolderRowH - list.Height);
        _foldersScroll = Math.Clamp(_foldersScroll, 0, maxScroll);
        using (ctx.PushClip(list.Inflate(new Thickness(2, 0))))
            for (int i = 0; i < count; i++)
            {
                double y = list.Y + i * FolderRowH - _foldersScroll;
                if (y > list.Bottom || y + FolderRowH < list.Y) continue;
                var row = new Rect(list.X, y, list.Width, FolderRowH);
                var card = new Rect(row.X, row.Y + 6, row.Width - 1, row.Height - 18);
                bool hover = card.Contains(mouse) && list.Contains(mouse);
                if (i == _folderRoots.Count) { DrawAddCard(ctx, card, hover, back); Hit(card, "folders-add"); continue; }
                DrawFolderCard(ctx, card, _folderRoots[i], hover, back, mouse, i, Hit);
            }
    }

    private void DrawAddCard(DrawingContext ctx, Rect card, bool hover, Color back)
    {
        Color lift = UiTheme.IsLight ? Colors.Black : Colors.White;
        if (hover) ctx.DrawRectangle(B(UiTheme.Mix(back, lift, .03)), null, card, 16, 16);
        var dash = new Pen(B(A(UiTheme.Text, hover ? 80 : 46)), 1.4) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        ctx.DrawRectangle(null, dash, card.Deflate(.7), 16, 16);
        double cx = card.Center.X, cy = card.Center.Y, arm = 16;
        var pen = new Pen(B(A(UiTheme.Text, hover ? 255 : 190)), 2.4, lineCap: PenLineCap.Round);
        ctx.DrawLine(pen, new Point(cx, cy - arm), new Point(cx, cy + arm));
        ctx.DrawLine(pen, new Point(cx - arm, cy), new Point(cx + arm, cy));
    }

    private void DrawFolderCard(DrawingContext ctx, Rect card, string root, bool hover, Color back, Point mouse, int index, Action<Rect, string, string> hit)
    {
        var st = _folderStats.GetValueOrDefault(root) ?? new FolderStats();
        Color lift = UiTheme.IsLight ? Colors.Black : Colors.White;
        Color surface = UiTheme.Mix(back, lift, UiTheme.IsLight ? .05 : .042);
        var thumb = new Rect(card.X, card.Y, Math.Round(card.Height * 16 / 9), card.Height);
        double mid = card.Y + card.Height / 2;
        using (ctx.PushClip(new RoundedRect(card, 16)))
        {
            ctx.FillRectangle(B(surface), card);
            var ready = st.Slides.Select(p => Image(p, thumb.Width)).Where(b => b != null).Select(b => b!).ToList();
            if (ready.Count > 0)
            {
                // Le immagini si alternano come il banner della Home: ogni riga col suo ritmo.
                double ms = (DateTime.UtcNow - _slideClock).TotalMilliseconds + index * 1700;
                int at = (int)(ms / SlideEveryMs);
                double phase = ms % SlideEveryMs;
                var current = ready[at % ready.Count];
                Bitmap? previous = ready.Count > 1 && at > 0 ? ready[(at - 1) % ready.Count] : null;
                double p = previous == null || phase >= SlideFadeMs ? 1 : phase / SlideFadeMs;
                p = p * p * (3 - 2 * p);
                using (ctx.PushClip(thumb))
                {
                    if (p < 1 && previous != null) DrawCover(ctx, previous, thumb);
                    using (ctx.PushOpacity(p)) DrawCover(ctx, current, thumb);
                }
                // La foto sfuma nella card verso destra, e appena in alto e in basso.
                ctx.FillRectangle(Horizontal(surface, (0, 0), (0.22, 0), (0.45, 40), (0.7, 160), (0.9, 240), (1, 255)), thumb);
                ctx.FillRectangle(Vertical(surface, (0, 90), (0.18, 0), (0.82, 0), (1, 90)), thumb);
                var folderIcon = new Rect(thumb.X + 18, thumb.Y + 16, 26, 26);
                SvgIcon.Get("folder")?.Draw(ctx, folderIcon.Translate(new Vector(1, 1)), Color.FromArgb(120, 0, 0, 0));
                SvgIcon.Get("folder")?.Draw(ctx, folderIcon, Color.FromArgb(235, 245, 247, 250));
                // Fotogrammi solo durante la dissolvenza; fra un cambio e l'altro un solo risveglio.
                if (ready.Count > 1)
                {
                    if (phase < SlideFadeMs + 40) _frames.Start();
                    else if (!_slideWake) { _slideWake = true; Avalonia.Threading.DispatcherTimer.RunOnce(() => { _slideWake = false; InvalidateVisual(); }, TimeSpan.FromMilliseconds(Math.Max(16, SlideEveryMs - phase))); }
                }
            }
            else SvgIcon.Get("folder")?.Draw(ctx, new Rect(thumb.X + (thumb.Width - 36) / 2, mid - 18, 36, 36), A(UiTheme.Text, 120));
        }
        ctx.DrawRectangle(null, new Pen(B(A(UiTheme.Text, UiTheme.IsLight ? 26 : hover ? 34 : 18)), 1), card.Deflate(.5), 16, 16);

        // Testi: nome, percorso, disco; a destra il numero grande e sotto la parola.
        var remove = new Rect(card.Right - 64, mid - 20, 40, 40);
        double textLeft = thumb.Right + 24, countRight = remove.X - 28;
        string name = Path.GetFileName(root.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(name)) name = "Disco " + root;
        bool reachable = !st.Known || st.Online || st.Count > 0;
        string Noun(int n) => _foldersCategory switch
        {
            "Movies" => "film", "TV Series" => n == 1 ? "episodio" : "episodi", "Music" => n == 1 ? "brano" : "brani",
            "Photos" => "foto", _ => n == 1 ? "elemento" : "elementi",
        };
        bool groups = st.Groups > 0;
        string number = !reachable ? "—" : (groups ? st.Groups : st.Count).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
        string label = !reachable ? "non raggiungibile" : groups ? "serie · " + st.Count + " " + Noun(st.Count) : Noun(st.Count);
        double px = 96.0 / 72 * 0.92;
        double numberPt = 40 / px, notePt = 15 / px, namePt = 22 / px;
        double colW = Math.Max(Measure(number, numberPt, DisplaySemibold), Measure(label, notePt, DisplayRegular));
        double colLeft = countRight - colW;
        var numberText = Format(number, numberPt, DisplaySemibold, A(UiTheme.Text, reachable ? 245 : 150));
        ctx.DrawText(numberText, new Point(countRight - numberText.WidthIncludingTrailingWhitespace, mid - numberText.Height + 6));
        var labelText = Format(label, notePt, DisplayRegular, A(UiTheme.Text, 150));
        ctx.DrawText(labelText, new Point(countRight - labelText.WidthIncludingTrailingWhitespace, mid + 10));
        double dot = 8, nameLeft = textLeft + dot + 14, textW = Math.Max(1, colLeft - 32 - nameLeft);
        if (st.Known) ctx.DrawEllipse(B(st.Online ? Color.FromRgb(52, 199, 112) : Color.FromRgb(130, 136, 146)), null, new Point(textLeft + dot / 2, mid - 40 + 15), dot / 2, dot / 2);
        ctx.DrawText(Format(name, namePt, DisplaySemibold, A(UiTheme.Text, 250), textW), new Point(nameLeft, mid - 40));
        ctx.DrawText(Format(root, notePt, DisplayRegular, A(UiTheme.Text, 150), textW), new Point(nameLeft, mid - 4));
        if (st.Known)
            ctx.DrawText(Format(st.Kind + (st.Online && st.Free >= 0 ? "  ·  " + SizeText(st.Free) + " liberi" : ""), notePt, DisplayRegular, A(UiTheme.Text, 150), textW), new Point(nameLeft, mid + 20));
        // Rimuovi: una X sottile, tenue a riposo, piena sulla card, rossa sotto il puntatore.
        bool hot = remove.Contains(mouse);
        Color ink = hot ? Color.FromRgb(255, 120, 120) : A(UiTheme.Text, hover ? 200 : 90);
        var pen = new Pen(B(ink), 1.8, lineCap: PenLineCap.Round);
        double rcx = remove.Center.X, rcy = remove.Center.Y, arm = 7.5;
        ctx.DrawLine(pen, new Point(rcx - arm, rcy - arm), new Point(rcx + arm, rcy + arm));
        ctx.DrawLine(pen, new Point(rcx + arm, rcy - arm), new Point(rcx - arm, rcy + arm));
        hit(remove, "folders-remove", root);
    }

    private async Task AddFolderFromSheetAsync()
    {
        if (PickFolder == null) return;
        string? folder = await PickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;
        LibraryStore.AddRoot(_foldersCategory, folder);
        _foldersChanged.Add(_foldersCategory);
        // Prima la scansione di questa categoria, cosi' la card mostra subito quanti titoli ha trovato.
        await ScanCategoryAsync(_foldersCategory);
    }

    private bool FoldersActivate(Hit hit)
    {
        if (!_foldersOpen) return false;
        switch (hit.Kind)
        {
            case "folders-close": case "folders-outside": CloseFolders(); break;
            case "folders-add": _ = AddFolderFromSheetAsync(); break;
            case "folders-remove":
                LibraryStore.RemoveRoot(_foldersCategory, hit.Key);
                _foldersChanged.Add(_foldersCategory);
                RefreshFolders();
                break;
            case "folders-category":
                OpenMenu("FolderCategory", FolderCategoryKeys.Select(key => new MenuItem(CategoryName(key), key == _foldersCategory, () => { _foldersCategory = key; _foldersScroll = 0; RefreshFolders(); })),
                    new Point(_foldersTitleAnchor.X, _foldersTitleAnchor.Y + S(8)));
                break;
        }
        return true;
    }

    /// <summary>FUSE-T monta i dischi come NFS locale (localhost): li riconosce il punto di montaggio.</summary>
    private static bool IsFuseT(string mountPoint)
    {
        try
        {
            var (exit, stdout, _) = ProcessLauncher.Run("/sbin/mount", Array.Empty<string>(), true, 3000, System.Threading.CancellationToken.None);
            if (exit != 0) return false;
            foreach (var line in System.Text.Encoding.UTF8.GetString(stdout).Split('\n'))
                if (line.Contains(" on " + mountPoint.TrimEnd('/') + " ", StringComparison.Ordinal) && (line.StartsWith("fuse-t:", StringComparison.Ordinal) || line.Contains("(fuse", StringComparison.Ordinal) || line.Contains("macfuse", StringComparison.Ordinal)))
                    return true;
        }
        catch { }
        return false;
    }
}
