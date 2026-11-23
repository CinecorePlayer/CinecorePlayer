using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

// Coda della musica: copia del cassetto di Windows (MediaLibrary/CinematicMediaLibraryPage.QueueAndPlaylists.cs).
// Pannello a tutta altezza sul lato destro, sopra la barra della musica, su vetro (quello vero di macOS quando c'e').
// In alto "Coda" con la X e il riepilogo (brani e durata); sotto l'elenco intero diviso in CRONOLOGIA / IN RIPRODUZIONE /
// IN ARRIVO: copertina, titolo (accento per il brano in corso e al passaggio), artista, la X per togliere. Barra di
// scorrimento sottile, rotella, trascinamento per riordinare, doppio clic per suonare. In fondo "Svuota coda" (solo
// testo, rosso al passaggio). Entra da destra (240 ms) ed esce sfumando (180 ms); un clic fuori la chiude.
internal sealed partial class LibraryView
{
    private bool _queueShownOpen;
    private long _queueAnimStart = -1;
    private int _queueScroll, _queueScrollMax, _queueVisibleRows = 1;
    private double _queueWheel;
    private int _queueDragIndex = -1, _queueDropIndex = -1;
    private Point _queueDragStart;
    private bool _queueDragging, _queueThumbDrag;
    private double _queueThumbGrab;
    private Rect _queueTrack, _queueThumb;

    private double QueueScale(Rect page) => Math.Clamp(Math.Min(page.Width / 1440, Math.Max(1, page.Height - MusicBarH) / 950), .86, 4);
    private static double QPx(double pt, double qs) => pt * 4 / 3 * qs; // punti di Windows (96 dpi) -> pixel

    /// <summary>Avanzamento dell'animazione (0 = fuori, 1 = aperta) e se sta ancora correndo.</summary>
    private (double Progress, bool Running) QueueAnimation()
    {
        if (_queueOpen != _queueShownOpen)
        {
            _queueShownOpen = _queueOpen;
            _queueAnimStart = Environment.TickCount64;
            if (_queueOpen) { _queueScroll = Math.Max(0, _queueIndex - 2); _queueDragIndex = -1; _queueDragging = false; }
        }
        if (_queueAnimStart < 0) return (_queueOpen ? 1 : 0, false);
        double t = Math.Clamp((Environment.TickCount64 - _queueAnimStart) / (_queueOpen ? 240.0 : 180.0), 0, 1);
        if (t >= 1) { _queueAnimStart = -1; return (_queueOpen ? 1 : 0, false); }
        return (_queueOpen ? 1 - Math.Pow(1 - t, 3) : 1 - t * t * t, true);
    }

    private Rect QueueDrawerBase(Rect page)
    {
        double qs = QueueScale(page), height = Math.Max(1, page.Height - MusicBarH);
        double width = Math.Max(1, Math.Min(page.Width - 32, Math.Round(420 * qs)));
        return new Rect(page.Width - width - Math.Round(28 * qs), Math.Round(28 * qs), width, Math.Max(1, height - Math.Round(56 * qs)));
    }

    /// <summary>Il cassetto dov'e' adesso (spostato durante l'animazione), null se chiuso.</summary>
    private Rect? QueueDrawerRect(Rect page)
    {
        if (!MusicBarVisible) return null;
        var (p, _) = QueueAnimation();
        if (p <= 0.001) return null;
        var panel = QueueDrawerBase(page);
        // Entra per una frazione della larghezza mentre compare (come Windows: 96 px).
        return panel.Translate(new Vector(Math.Round(Math.Min(panel.Width + 24, 96) * (1 - p)), 0));
    }

    private double QueueAlpha => Math.Min(1, QueueAnimation().Progress * 1.25);

    // ---------------------------------------------------------------- disposizione
    private sealed record QueueRowLayout(int Index, Rect Row, Rect Remove, string? Heading, Rect HeadingRect);
    private sealed record QueueLayout(Rect Panel, double Qs, Rect Close, Rect Title, Rect Summary, Rect List, Rect? Clear, Rect ClearHit, List<QueueRowLayout> Rows, Rect Track, Rect Thumb);

    private QueueLayout LayoutQueue(Rect panel, Rect page)
    {
        double qs = QueueScale(page);
        double Q(double v) => Math.Round(v * qs);
        double pad = Q(24);
        var close = new Rect(panel.Right - Q(16) - Q(36), panel.Y + Q(18), Q(36), Q(36));
        var title = new Rect(panel.X + pad, close.Y, close.X - panel.X - pad - Q(8), close.Height);
        var summary = new Rect(panel.X + pad, close.Bottom, panel.Width - pad * 2, Q(22));
        double bottom = panel.Bottom - Q(20);
        Rect? clear = null; Rect clearHit = default;
        if (_queue.Count > 0)
        {
            var c = new Rect(panel.X + pad, bottom - Q(44), panel.Width - pad * 2, Q(44));
            clear = c;
            double textW = TextW("Svuota coda", QPx(9.5, qs), DisplayRegular), hitW = textW + Q(32);
            clearHit = new Rect(c.X + (c.Width - hitW) / 2, c.Y, hitW, c.Height);
            bottom = c.Y - Q(8);
        }
        double listTop = summary.Bottom + Q(14);
        var list = new Rect(panel.X + pad - Q(8), listTop, panel.Width - pad - Q(10), Math.Max(1, bottom - listTop));
        // Righe visibili con le intestazioni delle sezioni (come DrawQueueRows di Windows).
        double rowH = Q(64), headingH = Q(32);
        // Righe che stanno davvero, contando le intestazioni che servono (Windows ne riservava sempre tre).
        int Fit(int from)
        {
            double yy = 0; string sec = ""; int n = 0;
            for (int k = from; k < _queue.Count; k++)
            {
                string sc = k < _queueIndex ? "c" : k == _queueIndex ? "r" : "a";
                double need = rowH + (sc != sec ? headingH : 0);
                if (yy + need > list.Height + 0.5) break;
                yy += need; sec = sc; n++;
            }
            return Math.Max(1, n);
        }
        int maxVisible = Fit(Math.Clamp(_queueScroll, 0, Math.Max(0, _queue.Count - 1)));
        _queueVisibleRows = maxVisible;
        // Fondo dell'elenco: l'ultima posizione da cui le righe rimaste riempiono il pannello.
        int lastStart = _queue.Count - 1;
        while (lastStart > 0 && Fit(lastStart - 1) >= _queue.Count - (lastStart - 1)) lastStart--;
        _queueScrollMax = Math.Max(0, lastStart);
        _queueScroll = Math.Clamp(_queueScroll, 0, _queueScrollMax);
        maxVisible = Fit(_queueScroll);
        var rows = new List<QueueRowLayout>();
        double y = list.Y;
        string last = "";
        for (int index = _queueScroll; index < Math.Min(_queue.Count, _queueScroll + maxVisible); index++)
        {
            string section = index < _queueIndex ? "Cronologia" : index == _queueIndex ? "In riproduzione" : "In arrivo";
            string? heading = null; Rect headingRect = default;
            if (section != last) { heading = section.ToUpperInvariant(); headingRect = new Rect(list.X + Q(8), y, list.Width, headingH); y += headingH; last = section; }
            var row = new Rect(list.X, y, list.Width - Q(10), rowH);
            var remove = new Rect(row.Right - Q(34), row.Y + (row.Height - Q(28)) / 2, Q(28), Q(28));
            rows.Add(new QueueRowLayout(index, row, remove, heading, headingRect));
            y += rowH;
        }
        // Barra di scorrimento sottile a destra dell'elenco (ScrollbarChrome).
        Rect track = default, thumb = default;
        var trackRect = new Rect(list.Right + Q(2), list.Y, Math.Max(3, Q(3)), list.Height);
        if (_queueScrollMax > 0 && trackRect.Height > Q(24))
        {
            double ratio = maxVisible / (double)Math.Max(1, _queue.Count);
            double min = Math.Min(trackRect.Height, Math.Max(34, Math.Floor(trackRect.Height / 14)));
            double thumbH = Math.Clamp(Math.Round(trackRect.Height * ratio), min, Math.Max(min, Math.Round(trackRect.Height * .4)));
            double thumbY = trackRect.Y + Math.Round((trackRect.Height - thumbH) * (_queueScroll / (double)Math.Max(1, _queueScrollMax)));
            track = trackRect; thumb = new Rect(trackRect.X, thumbY, trackRect.Width, thumbH);
        }
        _queueTrack = track; _queueThumb = thumb;
        return new QueueLayout(panel, qs, close, title, summary, list, clear, clearHit, rows, track, thumb);
    }

    private string QueueSummary()
    {
        if (_queue.Count == 0) return "Nessun brano in coda";
        string count = _queue.Count == 1 ? "1 brano" : $"{_queue.Count} brani";
        int total = (int)Math.Round(_queue.Sum(t => t.DurationMinutes ?? 0));
        if (total < 1) return count;
        int hours = total / 60, minutes = total % 60;
        string Minutes(int m) => m == 1 ? "1 minuto" : $"{m} minuti";
        string Hours(int h) => h == 1 ? "1 ora" : $"{h} ore";
        return count + "     " + (hours == 0 ? Minutes(minutes) : minutes == 0 ? Hours(hours) : Hours(hours) + " e " + Minutes(minutes));
    }

    // ---------------------------------------------------------------- disegno
    private void DrawQueuePanel(DrawingContext ctx, Rect page)
    {
        var (_, running) = QueueAnimation();
        if (running) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        if (QueueDrawerRect(page) is not { } panel) return;
        var layout = LayoutQueue(panel, page);
        _hitFloor = Math.Min(_hitFloor, _hits.Count);
        if (_queueOpen)
        {
            // Un clic fuori (sopra la barra della musica) chiude soltanto la coda.
            _hits.Add(new Hit(new Rect(0, 0, page.Width, Math.Max(1, page.Height - MusicBarH)), "queue-outside"));
            _hits.Add(new Hit(panel, "queue-panel"));
            _hits.Add(new Hit(layout.Close, "queue-close"));
            foreach (var row in layout.Rows)
            {
                _hits.Add(new Hit(row.Row, "queue-row", row.Index.ToString(CultureInfo.InvariantCulture)));
                if (Hover(row.Row)) _hits.Add(new Hit(row.Remove, "queue-remove", row.Index.ToString(CultureInfo.InvariantCulture)));
            }
            if (layout.Clear != null) _hits.Add(new Hit(layout.ClearHit, "queue-clear"));
            if (layout.Track.Width > 0) _hits.Add(new Hit(layout.Track.Inflate(new Thickness(8, 0)), "queue-scrollbar"));
        }
        double alpha = QueueAlpha;
        double radius = Math.Round(13 * layout.Qs);
        if (_nativeGlassOn && _surfaceGlass is { Active: true } glass && Avalonia.Controls.TopLevel.GetTopLevel(this) is { } top)
        {
            double scaling = top.RenderScaling;
            int hot = layout.Rows.FindIndex(r => Hover(r.Row));
            string key = string.Join("|", _queueIndex, _queue.Count, _queueScroll, string.Join(",", layout.Rows.Select(r => _queue[r.Index].Path)), hot,
                layout.Rows.Any(r => Hover(r.Remove)) ? layout.Rows.First(r => Hover(r.Remove)).Index : -1, Hover(layout.ClearHit),
                Hover(layout.Close), _queueDragging ? _queueDropIndex : -1, Math.Round(alpha, 2), panel.Size, UiTheme.IsLight, UiTheme.Accent, scaling,
                layout.Rows.Count(r => Image(_queue[r.Index].ArtPath, Math.Round(44 * layout.Qs)) != null));
            glass.SetForeground(1, key, new PixelSize((int)Math.Ceiling(panel.Width * scaling), (int)Math.Ceiling(panel.Height * scaling)), a =>
            {
                using (a.Context.PushTransform(Matrix.CreateTranslation(-panel.X, -panel.Y) * Matrix.CreateScale(scaling, scaling)))
                using (a.Context.PushOpacity(alpha))
                {
                    DrawGlassRim(a.Context, panel, radius, UiTheme.IsLight, scaling);
                    DrawQueueBody(a.Context, layout);
                }
            });
            return;
        }
        // Senza il vetro di macOS: vetro disegnato (velatura scura traslucida come il materiale di Windows).
#if CINECORE_WINDOWS
        // Il vetro liquid del vecchio player Windows (CinematicMediaLibraryPage.QueueAndPlaylists), dal vivo.
        if (!_glassCapture && !QaNoGlass) ctx.Custom(BackdropGlassOp.Liquid(panel, radius, alpha));
        else using (ctx.PushOpacity(alpha)) ctx.DrawRectangle(B(UiTheme.IsLight ? Color.FromArgb(236, 238, 242, 248) : Color.FromArgb(236, 30, 32, 36)), null, panel, radius, radius);
        using (ctx.PushOpacity(alpha)) DrawQueueBody(ctx, layout);
#else
        using (ctx.PushOpacity(alpha))
        {
            bool light = UiTheme.IsLight;
            ctx.DrawRectangle(B(light ? Color.FromArgb(232, 244, 246, 249) : Color.FromArgb(226, 20, 23, 29)), null, panel, radius, radius);
            DrawGlassRim(ctx, panel, radius, light);
            DrawQueueBody(ctx, layout);
        }
#endif
    }

    private void DrawQueueBody(DrawingContext ctx, QueueLayout l)
    {
        double qs = l.Qs;
        double Q(double v) => Math.Round(v * qs);
        var text = UiTheme.Text;
        var muted = A(text, 150);
        var accent = UiTheme.Accent;
        // X in alto a destra.
        bool closeHot = Hover(l.Close);
        if (closeHot) ctx.DrawEllipse(B(A(text, 26)), null, l.Close.Center, l.Close.Width / 2, l.Close.Height / 2);
        var xPen = new Pen(B(A(text, closeHot ? 255 : 200)), Math.Max(1.4, 1.8 * qs), lineCap: PenLineCap.Round);
        double c = Q(6);
        ctx.DrawLine(xPen, new Point(l.Close.Center.X - c, l.Close.Center.Y - c), new Point(l.Close.Center.X + c, l.Close.Center.Y + c));
        ctx.DrawLine(xPen, new Point(l.Close.Center.X + c, l.Close.Center.Y - c), new Point(l.Close.Center.X - c, l.Close.Center.Y + c));
        FsText(ctx, "Coda", QPx(18, qs), DisplaySemibold, text, l.Title, TextAlignment.Left);
        FsText(ctx, QueueSummary(), QPx(9.5, qs), DisplayRegular, muted, l.Summary, TextAlignment.Left);
        if (_queue.Count == 0)
        {
            FsText(ctx, "La coda è vuota", QPx(12, qs), DisplaySemibold, text, new Rect(l.List.X, l.List.Center.Y - Q(30), l.List.Width, Q(30)), TextAlignment.Center);
            FsText(ctx, "Aggiungi un brano alla coda.", QPx(9.5, qs), DisplayRegular, muted, new Rect(l.List.X, l.List.Center.Y + Q(2), l.List.Width, Q(24)), TextAlignment.Center);
        }
        foreach (var r in l.Rows)
        {
            if (r.Heading != null) FsText(ctx, r.Heading, QPx(8.4, qs), DisplaySemibold, muted, r.HeadingRect, TextAlignment.Left);
            var track = _queue[r.Index];
            var row = r.Row;
            bool current = r.Index == _queueIndex, hover = Hover(row) && !_queueDragging;
            double cover = Q(44);
            var art = new Rect(row.X + Q(8), row.Y + (row.Height - cover) / 2, cover, cover);
            using (ctx.PushGeometryClip(new RectangleGeometry(art, Q(2), Q(2))))
            {
                ctx.FillRectangle(B(A(text, current ? 60 : 40)), art);
                LibraryStore.QueueMusicArtwork(track);
                if (Image(track.ArtPath, art.Width) is { } bmp) DrawCover(ctx, bmp, art);
                else DrawMusicPlaceholder(ctx, art);
            }
            string artist = UnknownTag(track.ArtistName) ? "Artista sconosciuto" : track.ArtistName!;
            double x = art.Right + Q(12), width = Math.Max(30, row.Right - x - Q(40));
            FsText(ctx, TrackTitle(track), QPx(10, qs), DisplaySemibold, current || hover ? accent : text, new Rect(x, row.Y + Q(9), width, Q(23)), TextAlignment.Left);
            FsText(ctx, artist, QPx(9, qs), DisplayRegular, muted, new Rect(x, row.Y + Q(32), width, Q(21)), TextAlignment.Left);
            // Durata a destra; al passaggio sulla riga al suo posto la X per togliere il brano (meno X sempre accese).
            if (hover)
            {
                bool removeHot = Hover(r.Remove);
                var g = r.Remove.Deflate(Q(8));
                var pen = new Pen(B(removeHot ? accent : muted), Math.Max(1.3, 1.6 * qs), lineCap: PenLineCap.Round);
                ctx.DrawLine(pen, g.TopLeft, g.BottomRight);
                ctx.DrawLine(pen, g.TopRight, g.BottomLeft);
            }
            else if (track.DurationMinutes is double minutes && minutes > 0)
            {
                int total = (int)Math.Round(minutes * 60);
                FsText(ctx, $"{total / 60}:{total % 60:00}", QPx(9, qs), DisplayRegular, muted, new Rect(row.Right - Q(64), row.Y, Q(56), row.Height), TextAlignment.Right);
            }
            if (_queueDragging && _queueDropIndex == r.Index)
                ctx.DrawLine(new Pen(B(accent), 2), new Point(row.X, row.Y), new Point(row.Right, row.Y));
        }
        if (l.Thumb.Height > 0)
        {
            double w = Math.Min(4, l.Track.Width), x = l.Track.X + l.Track.Width / 2;
            ctx.DrawLine(new Pen(B(A(text, 90)), w, lineCap: PenLineCap.Round), new Point(x, l.Thumb.Y + w / 2), new Point(x, l.Thumb.Y + Math.Max(w / 2, l.Thumb.Height - w / 2)));
        }
        if (l.Clear is { } clear)
        {
            bool hot = Hover(l.ClearHit);
            FsText(ctx, "Svuota coda", QPx(9.5, qs), DisplayRegular, hot ? Color.FromRgb(255, 122, 132) : muted, clear, TextAlignment.Center);
        }
    }

    // ---------------------------------------------------------------- input
    /// <summary>Pressione dentro la coda: riga (selezione, trascinamento, doppio clic suona) o barra di scorrimento.</summary>
    private bool QueuePointerPressed(Point p, int clicks)
    {
        if (!_queueOpen || HitAt(p) is not { } hit) return false;
        if (hit.Kind == "queue-scrollbar")
        {
            _queueThumbDrag = true;
            _queueThumbGrab = _queueThumb.Contains(p) ? p.Y - _queueThumb.Y : _queueThumb.Height / 2;
            QueueThumbTo(p.Y);
            return true;
        }
        if (hit.Kind != "queue-row" || !int.TryParse(hit.Key, out int index)) return false;
        if (clicks >= 2)
        {
            if (index != _queueIndex && index < _queue.Count) { _queueIndex = index; StartCurrentTrack(); }
            InvalidateVisual();
            return true;
        }
        _queueDragIndex = index; _queueDropIndex = index; _queueDragStart = p; _queueDragging = false;
        return true;
    }

    private void QueueThumbTo(double y)
    {
        if (_queueTrack.Height <= 0 || _queueScrollMax <= 0) return;
        double travel = Math.Max(1, _queueTrack.Height - _queueThumb.Height);
        _queueScroll = (int)Math.Round(Math.Clamp((y - _queueThumbGrab - _queueTrack.Y) / travel, 0, 1) * _queueScrollMax);
        InvalidateVisual();
    }

    private bool QueuePointerMoved(Point p)
    {
        if (_queueThumbDrag) { QueueThumbTo(p.Y); return true; }
        if (_queueDragIndex < 0) return false;
        if (!_queueDragging && Math.Abs(p.X - _queueDragStart.X) + Math.Abs(p.Y - _queueDragStart.Y) >= 8) _queueDragging = true;
        if (!_queueDragging) return false;
        // Riga piu' vicina al puntatore (QueueIndexAtPoint di Windows).
        var rows = _hits.Where(h => h.Kind == "queue-row").ToList();
        if (rows.Count > 0)
            _queueDropIndex = int.Parse(rows.OrderBy(h => Math.Abs(h.Bounds.Center.Y - p.Y)).First().Key, CultureInfo.InvariantCulture);
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll);
        InvalidateVisual();
        return true;
    }

    private bool QueuePointerReleased()
    {
        if (_queueThumbDrag) { _queueThumbDrag = false; return true; }
        if (_queueDragIndex < 0) return false;
        try
        {
            if (_queueDragging && _queueDropIndex >= 0 && _queueDropIndex != _queueDragIndex) MoveQueueItem(_queueDragIndex, _queueDropIndex);
        }
        finally
        {
            _queueDragIndex = -1; _queueDropIndex = -1; _queueDragging = false;
            Cursor = Avalonia.Input.Cursor.Default;
            InvalidateVisual();
        }
        return true;
    }

    private bool _mixOrderRunning;

    /// <summary>
    /// Come "Ordina la coda per il mix" di Windows (PlayerForm.PlaybackQueue): i brani dopo quello in corso si
    /// riordinano perche' ognuno sia seguito dal piu' vicino per tempo (anche doppio o meta'), tonalita' sulla ruota
    /// di Camelot ed energia. I brani non ancora misurati si misurano prima, in sottofondo; quelli senza battito
    /// chiaro e quelli dal server restano in fondo, nel loro ordine.
    /// </summary>
    private async void OrderQueueForMixing()
    {
        if (_mixOrderRunning || !MusicBarVisible || _queueIndex + 2 >= _queue.Count) return;
        _mixOrderRunning = true;
        try
        {
            var anchorItem = _queue[_queueIndex];
            var upcoming = _queue.Skip(_queueIndex + 1).Where(t => !t.IsNetwork && File.Exists(t.Path)).Take(80).ToList();
            Toast?.Invoke($"Analizzo {upcoming.Count} brani per il mix…");
            // Anche il brano in corso: e' da li' che parte la catena (su Windows mancava e il primo brano era a caso).
            foreach (var track in upcoming.Prepend(anchorItem).Where(t => !t.IsNetwork && File.Exists(t.Path)))
                await Task.Run(() => CinecorePlayer2025.Audio.AutomixAnalyzer.PrepareAsync(track.Path));
            // La coda e' cambiata nel frattempo (altro brano, coda svuotata): non si tocca.
            if (!MusicBarVisible || !ReferenceEquals(_queue[_queueIndex], anchorItem)) return;
            var profiles = upcoming.ToDictionary(t => t, t => CinecorePlayer2025.Audio.AutomixAnalyzer.TryGet(t.Path));
            var rhythmic = upcoming.Where(t => profiles[t] is { Bpm: > 0 }).ToList();
            Console.Error.WriteLine($"[automix] ordine della coda: {upcoming.Count} brani, {profiles.Count(p => p.Value != null)} misurati, {rhythmic.Count} con battito");
            var ordered = new List<LibraryItem>();
            var last = anchorItem.IsNetwork ? null : CinecorePlayer2025.Audio.AutomixAnalyzer.TryGet(anchorItem.Path);
            while (rhythmic.Count > 0)
            {
                var best = rhythmic.OrderBy(t => MixDistance(last, profiles[t]!)).First();
                ordered.Add(best); rhythmic.Remove(best); last = profiles[best];
            }
            var placed = new HashSet<LibraryItem>(ordered);
            ordered.AddRange(_queue.Skip(_queueIndex + 1).Where(t => !placed.Contains(t)));
            _queue = _queue.Take(_queueIndex + 1).Concat(ordered).ToList();
            // Ordine voluto: il casuale si spegne (come Windows) e l'ordine diventa quello di riferimento.
            _shuffle = false;
            _queueOriginal = _queue.ToList();
            UpdateNextSource();
            Toast?.Invoke("Coda ordinata per il mix");
            InvalidateVisual();
        }
        catch (Exception ex) { Console.Error.WriteLine("[automix] ordine della coda: " + ex.Message); }
        finally { _mixOrderRunning = false; }
    }

    /// <summary>Quanto e' difficile passare da un brano all'altro (0 = ideale): tempo, tonalita', energia (MixDistance di Windows).</summary>
    private static double MixDistance(CinecorePlayer2025.Audio.TrackMixProfile? from, CinecorePlayer2025.Audio.TrackMixProfile to)
    {
        if (from == null || from.Bpm <= 0) return 0;
        double ratio = to.Bpm / from.Bpm;
        double tempo = new[] { 1.0, 2.0, 0.5 }.Min(m => Math.Abs(Math.Log(ratio / m)));
        int harmony = CinecorePlayer2025.Audio.KeyDetector.Compatibility(from.KeyConfidence >= 0.55 ? from.Key : -1, to.KeyConfidence >= 0.55 ? to.Key : -1);
        double key = harmony == 2 ? 0 : harmony == 1 ? 0.5 : 1;
        double energy = Math.Abs(from.BodyDb - to.BodyDb) / 6;
        return tempo / 0.04 + key * 0.8 + energy * 0.3;
    }

    private void MoveQueueItem(int from, int to)
    {
        if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count) return;
        var current = _queue[_queueIndex];
        var list = _queue.ToList();
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        _queue = list;
        _queueIndex = Math.Max(0, _queue.IndexOf(current));
        if (!_shuffle) _queueOriginal = _queue.ToList();
        UpdateNextSource();
    }

    private void RemoveQueueItem(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        var removed = _queue[index];
        bool current = index == _queueIndex;
        _queue = _queue.Where((_, i) => i != index).ToList();
        _queueOriginal.Remove(removed);
        if (index < _queueIndex) _queueIndex--;
        if (current)
        {
            // Tolto il brano in corso: parte quello che lo seguiva (se c'e').
            if (_queueIndex < _queue.Count) StartCurrentTrack();
            else { _queueIndex = Math.Max(0, _queue.Count - 1); if (_queue.Count == 0) { _queueOpen = false; StopMusicAnimated(); return; } _audio.Pause(); }
        }
        UpdateNextSource();
    }

    private bool QueueWheel(double delta)
    {
        if (!_queueOpen || QueueDrawerRect(new Rect(Bounds.Size)) is not { } panel || !panel.Contains(_mouse)) return false;
        _queueWheel -= delta;
        int steps = (int)Math.Truncate(_queueWheel);
        if (steps != 0) { _queueWheel -= steps; _queueScroll = Math.Clamp(_queueScroll + steps, 0, _queueScrollMax); InvalidateVisual(); }
        return true;
    }

    private bool QueueActivate(Hit hit)
    {
        switch (hit.Kind)
        {
            case "queue-outside": case "queue-close": _queueOpen = false; InvalidateVisual(); return true;
            case "queue-panel": case "queue-row": case "queue-scrollbar": return true;
            case "queue-remove":
                if (int.TryParse(hit.Key, out int index)) RemoveQueueItem(index);
                InvalidateVisual();
                return true;
            case "queue-clear":
                // Restano il brano in corso e quelli gia' ascoltati; quelli in arrivo vanno via.
                _queue = _queue.Take(_queueIndex + 1).ToList();
                _queueOriginal = _queue.ToList();
                UpdateNextSource();
                InvalidateVisual();
                return true;
        }
        return false;
    }
}
