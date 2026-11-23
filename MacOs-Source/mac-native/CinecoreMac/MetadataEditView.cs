using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>MetadataEditForm di Windows: titolo/anno, ricerca TMDb, due file di copertine e immagine locale.</summary>
internal sealed class MetadataEditView : Control
{
    public Canvas? InputLayer { get; set; }
    public event Action<string>? Saved;
    public event Action? Closed;
    public event Action<IReadOnlyList<MenuEntry>, Point>? MenuRequested;
    private readonly TextBox _title = Field(), _year = Field();
    private static readonly FontFamily Inter = new("avares://CinecoreMac/Assets/Fonts#Inter");
    private LibraryItem? _item;
    private readonly Dictionary<string, Bitmap> _thumbs = new();
    private List<MovieMetadataService.TmdbMatch> _matches = new();
    private List<string> _posters = new();
    private CancellationTokenSource? _lifetime, _search, _images;
    private int _match = -1, _poster = -1, _generation;
    private string? _localPoster;
    private string _status = "";
    private bool _saving;
    private Point _mouse = new(-1000, -1000);
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(Local(_mouse)); }
    private Rect _sheet;
    private double _scale;
    private readonly List<(Rect Rect, string Key, int Index)> _hits = new();
    private bool _closing;
    public bool IsOpen => IsVisible;
    internal string QaSummary => $"matches={_matches.Count} posters={_posters.Count} previews={_thumbs.Count}";

    public MetadataEditView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        Opacity = 0;
        Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(170) } };
        PropertyChanged += (_, e) => { if (e.Property == OpacityProperty && InputLayer != null) InputLayer.Opacity = Opacity; };
        _year.MaxLength = 4;
        _title.KeyDown += FieldKey; _year.KeyDown += FieldKey;
        foreach (var box in new[] { _title, _year }) { box.GotFocus += (_, _) => InvalidateVisual(); box.LostFocus += (_, _) => InvalidateVisual(); }
        UiTheme.Changed += InvalidateVisual;
    }

    private static TextBox Field()
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(14, 3),
            FontFamily = new FontFamily("avares://CinecoreMac/Assets/Fonts#Inter"), MinHeight = 0,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[key] = Brushes.Transparent;
        // Niente anello di fuoco di Avalonia (blu di sistema, sulla misura della casella invisibile): il bordo lo disegna
        // la scheda sul campo, col colore d'accento; anche la selezione prende l'accento.
        box.FocusAdorner = null;
        box.SelectionBrush = new SolidColorBrush(UiTheme.WithAlpha(UiTheme.Text, 70));
        return box;
    }

    private void FieldKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _ = SearchAsync(); e.Handled = true; }
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    public void Open(LibraryItem item)
    {
        if (_saving) return;
        CancelRequests(); _generation++; _closing = false;
        foreach (var bitmap in _thumbs.Values) bitmap.Dispose();
        _thumbs.Clear(); _matches.Clear(); _posters.Clear();
        _item = item; _title.Text = item.Title; _year.Text = item.Year?.ToString(CultureInfo.InvariantCulture) ?? "";
        _localPoster = null; _match = _poster = -1; _status = "";
        _lifetime = new CancellationTokenSource();
        if (_title.Parent == null) { InputLayer?.Children.Add(_title); InputLayer?.Children.Add(_year); }
        if (InputLayer != null) { InputLayer.Opacity = 0; InputLayer.IsHitTestVisible = true; }
        IsVisible = true; InvalidateVisual();
        Dispatcher.UIThread.Post(() => { Opacity = 1; PlaceInputs(); _title.Focus(); _title.SelectAll(); });
        _ = SearchAsync();
    }

    private void CancelRequests()
    {
        _search?.Cancel(); _images?.Cancel(); _lifetime?.Cancel();
    }

    public void Close()
    {
        if (_saving || _closing || !IsVisible) return;
        _closing = true;
        CancelRequests(); _generation++;
        int generation = _generation;
        if (InputLayer != null) InputLayer.IsHitTestVisible = false;
        Opacity = 0;
        DispatcherTimer.RunOnce(() => { if (generation != _generation) return; IsVisible = _title.IsVisible = _year.IsVisible = false; Closed?.Invoke(); }, TimeSpan.FromMilliseconds(170));
    }

    private int? YearValue => int.TryParse(_year.Text, out int year) && year is >= 1870 and <= 2200 ? year : null;

    private async Task SearchAsync()
    {
        if (_saving || _lifetime == null) return;
        string title = (_title.Text ?? "").Trim();
        if (title.Length == 0) { _status = "Scrivi il titolo"; InvalidateVisual(); return; }
        if (!string.IsNullOrWhiteSpace(_year.Text) && YearValue == null) { _status = "Scrivi un anno valido"; InvalidateVisual(); return; }
        _search?.Cancel(); _images?.Cancel();
        var search = _search = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        int generation = _generation; int? year = YearValue;
        _status = "Cerco su TMDb…"; InvalidateVisual();
        try
        {
            var result = await Task.Run(() =>
            {
                bool configured = MovieMetadataService.HasCustomTmdbApiKey;
                var found = configured ? MovieMetadataService.SearchTmdbMovies(title, year, "it", search.Token) : new List<MovieMetadataService.TmdbMatch>();
                if (configured && found.Count == 0 && year.HasValue) found = MovieMetadataService.SearchTmdbMovies(title, null, "it", search.Token);
                return (configured, found);
            }, search.Token);
            if (!IsVisible || search.IsCancellationRequested || generation != _generation) return;
            _matches = result.found; _match = _poster = -1; _posters.Clear();
            _status = !result.configured ? "TMDb non configurato: puoi comunque salvare titolo, anno e una copertina dal computer."
                : _matches.Count == 0 ? "Nessun film con questo titolo" : "";
            if (_matches.Count > 0) await SelectMatchAsync(0, fillFields: false);
        }
        catch (OperationCanceledException) { }
        catch { if (generation == _generation) _status = "Ricerca non riuscita. Riprova."; }
        InvalidateVisual();
    }

    private async Task SelectMatchAsync(int index, bool fillFields)
    {
        if (_saving || _lifetime == null || index < 0 || index >= _matches.Count) return;
        _images?.Cancel();
        var images = _images = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        int generation = _generation;
        _match = index; _poster = -1; _localPoster = null; _posters.Clear();
        var match = _matches[index];
        if (fillFields) { _title.Text = match.Title; _year.Text = match.Year?.ToString() ?? ""; }
        InvalidateVisual();
        try
        {
            var posters = await Task.Run(() => MovieMetadataService.ListTmdbPosters(match.Id, "it", images.Token), images.Token);
            if (images.IsCancellationRequested || generation != _generation) return;
            _posters = posters.Take(12).ToList(); InvalidateVisual();
            // Le prime immagini compaiono subito, senza aspettare il completamento dell'intera griglia.
            foreach (string poster in _posters.ToArray())
            {
                if (_thumbs.ContainsKey(poster)) continue;
                string file = PosterFile(poster, "w185");
                Bitmap? bitmap = await Task.Run(() =>
                {
                    if (!MovieMetadataService.DownloadTmdbImage(poster, "w185", file, images.Token)) return null;
                    using var input = File.OpenRead(file);
                    return Bitmap.DecodeToWidth(input, 246);
                }, images.Token);
                if (images.IsCancellationRequested || generation != _generation) { bitmap?.Dispose(); return; }
                if (bitmap != null) { _thumbs[poster] = bitmap; InvalidateVisual(); }
            }
        }
        catch (OperationCanceledException) { }
        catch { if (generation == _generation) { _status = "Non riesco a caricare le copertine"; InvalidateVisual(); } }
    }

    private static string PosterFile(string poster, string size) => Path.Combine(Path.GetTempPath(), "cinecore-covers", size + "_" + poster.Trim('/').Replace('/', '_'));

    private async Task PickFileAsync()
    {
        if (_saving || TopLevel.GetTopLevel(this) is not { } top) return;
        int generation = _generation;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Scegli la copertina", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Immagini") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp" } } },
        });
        if (generation != _generation || !IsVisible) return;
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) SetLocalPoster(path);
    }

    internal void SetLocalPoster(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(input, 246);
            if (bitmap.PixelSize.Width < 80 || bitmap.Size.Height < 80) { bitmap.Dispose(); _status = "Immagine troppo piccola"; }
            else
            {
                if (_thumbs.Remove("local", out var previous)) previous.Dispose();
                _thumbs["local"] = bitmap; _localPoster = path; _poster = -1;
                _status = "Copertina: " + Path.GetFileName(path);
            }
        }
        catch { _status = "Immagine non utilizzabile"; }
        InvalidateVisual();
    }

    private async Task SaveAsync()
    {
        if (_saving || _item == null || _lifetime == null) return;
        string title = (_title.Text ?? "").Trim(), path = _item.Path;
        if (title.Length == 0) { _status = "Scrivi il titolo"; InvalidateVisual(); return; }
        if (!string.IsNullOrWhiteSpace(_year.Text) && YearValue == null) { _status = "Scrivi un anno valido"; InvalidateVisual(); return; }
        _saving = true; _search?.Cancel(); _images?.Cancel();
        string? poster = _poster >= 0 && _poster < _posters.Count ? _posters[_poster] : null;
        string? local = _localPoster; int? year = YearValue; var token = _lifetime.Token;
        _status = "Salvo…"; InvalidateVisual();
        LibraryStore.InvalidateMetadata(path);
        try
        {
            await Task.Run(() =>
            {
                string? file = local;
                if (poster != null)
                {
                    file = PosterFile(poster, "w780");
                    if (!MovieMetadataService.DownloadTmdbImage(poster, "w780", file, token)) throw new IOException();
                }
                token.ThrowIfCancellationRequested();
                MovieMetadataService.SetManualTitle(path, title, year);
                if (file != null && !MovieMetadataService.SetManualPoster(path, file)) throw new IOException();
            }, token);
            _saving = false; Saved?.Invoke(path); Close();
        }
        catch { _status = "Salvataggio non riuscito. Riprova."; _saving = false; InvalidateVisual(); }
    }

    private async Task ResetAsync()
    {
        if (_saving || _item == null) return;
        _saving = true; string path = _item.Path;
        LibraryStore.InvalidateMetadata(path);
        try { await Task.Run(() => MovieMetadataService.ClearManual(path)); Saved?.Invoke(path); _saving = false; Close(); }
        catch { _saving = false; _status = "Ripristino non riuscito"; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(Local(_mouse));
        if (!IsVisible || Bounds.Width < 20 || Bounds.Height < 20) return;
        _hits.Clear();
        // Come le altre schede che chiedono dati (Nuova playlist, tutorial): niente riquadro, la pagina velata e
        // vignettata, il contenuto direttamente sopra; il pulsante pieno e' bianco.
        _scale = SheetScale();
        _sheet = new Rect((Bounds.Width - 880 * _scale) / 2, (Bounds.Height - 728 * _scale) / 2, 880 * _scale, 728 * _scale);
        // I campi di testo seguono il foglio (finestra ridimensionata, o prima apertura con la vista non ancora misurata).
        if (_sheet != _placedSheet) { _placedSheet = _sheet; Dispatcher.UIThread.Post(PlaceInputs); }
        WizardSheet.DrawVeil(ctx, new Rect(Bounds.Size), 1);
        using var transform = ctx.PushTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_sheet.X, _sheet.Y));
        Text(ctx, "Correggi titolo e copertina", new Rect(36, 18, 700, 52), 34, true);
        Text(ctx, "Annulla", new Rect(744, 26, 100, 32), 14, false, "close", TextAlignment.Right);
        Text(ctx, "Titolo", new Rect(36, 76, 548, 22), 12, false, null, TextAlignment.Left, true);
        Text(ctx, "Anno", new Rect(600, 76, 244, 22), 12, false, null, TextAlignment.Left, true);
        foreach (var (box, rect) in new[] { (_title, new Rect(36, 100, 548, 42)), (_year, new Rect(600, 100, 244, 42)) })
        {
            WizardSheet.DrawUnderline(ctx, rect, box.IsFocused, false, 255, 1);
        }
        Text(ctx, "Cerca su TMDb", new Rect(36, 156, 200, 28), 15, false, "search");
        Text(ctx, _matches.Count > 0 ? "Risultati per “" + (_title.Text ?? "") + "”" : _status, new Rect(36, 196, 808, 28), 13);
        if (_matches.Count > 1 && _match >= 0)
            Text(ctx, _matches[_match].Title + "  " + _matches[_match].Year + "  ˅", new Rect(36, 228, 808, 28), 15, true, "matches");
        double gridTop = _matches.Count > 1 ? 270 : 232;
        for (int i = 0; i < Math.Min(_posters.Count, 12); i++)
        {
            var card = new Rect(36 + i % 6 * 137, gridTop + i / 6 * 196, 123, 182);
            ctx.DrawRectangle(new SolidColorBrush(UiTheme.WithAlpha(UiTheme.Text, 14)), null, card, 5, 5);
            if (_thumbs.TryGetValue(_posters[i], out var bitmap)) using (ctx.PushClip(new RoundedRect(card, 5))) ctx.DrawImage(bitmap, card);
            if (i == _poster) ctx.DrawRectangle(null, new Pen(new SolidColorBrush(UiTheme.Text), 3), card.Deflate(1.5), 5, 5);
            _hits.Add((card, "poster", i));
        }
        if (_localPoster != null && _thumbs.TryGetValue("local", out var local))
        {
            var card = new Rect(36, gridTop, 123, 182);
            using (ctx.PushClip(new RoundedRect(card, 5))) ctx.DrawImage(local, card);
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(UiTheme.Text), 3), card.Deflate(1.5), 5, 5);
        }
        if (_matches.Count > 0 && _status.Length > 0) Text(ctx, _status, new Rect(36, 630, 808, 24), 12);
        Text(ctx, "Immagine dal computer", new Rect(36, 656, 210, 42), 14, false, "file");
        if (_item != null && MovieMetadataService.HasManualCorrection(_item.Path)) Text(ctx, "Ripristina", new Rect(262, 656, 110, 42), 14, false, "reset");
        Button(ctx, _saving ? "Salvo…" : "Salva", new Rect(716, 652, 128, 48), "save", true);
        Dispatcher.UIThread.Post(PlaceInputs, DispatcherPriority.Render);
    }

    private void Text(DrawingContext ctx, string text, Rect r, double size, bool bold = false, string? key = null, TextAlignment align = TextAlignment.Left, bool soft = false)
    {
        bool hot = key != null && Hot(r);
        // Le voci cliccabili sono testo tenue che si accende al passaggio (niente piu' blu).
        Color ink = key != null ? (hot ? UiTheme.Text : UiTheme.WithAlpha(UiTheme.Text, 170)) : soft ? UiTheme.WithAlpha(UiTheme.Text, 150) : UiTheme.Text;
        var ft = UiText.Get(L10n.T(text), new Typeface(Inter, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, ink, r.Width, 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
        if (key != null) _hits.Add((r, key, -1));
    }

    private void Button(DrawingContext ctx, string label, Rect r, string key, bool primary)
    {
        ctx.DrawRectangle(new SolidColorBrush(primary ? UiTheme.PrimaryFill(Hot(r)) : UiTheme.WithAlpha(UiTheme.Text, 30)), null, r, r.Height / 2, r.Height / 2);
        var ft = UiText.Get(L10n.T(label), new Typeface(Inter, FontStyle.Normal, FontWeight.SemiBold), 15, primary ? UiTheme.OnPrimary : UiTheme.Text);
        ctx.DrawText(ft, new Point(r.Center.X - ft.Width / 2, r.Center.Y - ft.Height / 2));
        _hits.Add((r, key, -1));
    }

    private Point Local(Point p) => new((p.X - _sheet.X) / Math.Max(.1, _scale), (p.Y - _sheet.Y) / Math.Max(.1, _scale));
    /// <summary>Scala del foglio per la misura della vista (al massimo 1,3, mai sotto 0,1).</summary>
    private Rect _placedSheet;
    private double SheetScale() => Math.Max(.1, Math.Min(1.3, Math.Min((Bounds.Width - 36) / 880, (Bounds.Height - 36) / 728)));

    private void PlaceInputs()
    {
        // [2026-11-22] Chiamata (Dispatcher.Post in Open) anche prima del primo disegno, quando _scale era ancora 0:
        // FontSize = 0 fa un'eccezione che chiudeva l'app (aprendo "Modifica metadati" durante un film).
        if (_scale <= 0) { _scale = SheetScale(); _sheet = new Rect((Bounds.Width - 880 * _scale) / 2, (Bounds.Height - 728 * _scale) / 2, 880 * _scale, 728 * _scale); }
        foreach (var pair in new[] { (_title, new Rect(36, 100, 548, 42)), (_year, new Rect(600, 100, 244, 42)) })
        {
            pair.Item1.IsVisible = IsVisible;
            pair.Item1.IsEnabled = !_saving;
            if (!IsVisible) continue;
            Canvas.SetLeft(pair.Item1, _sheet.X + pair.Item2.X * _scale); Canvas.SetTop(pair.Item1, _sheet.Y + pair.Item2.Y * _scale);
            pair.Item1.Width = pair.Item2.Width * _scale; pair.Item1.Height = pair.Item2.Height * _scale;
            pair.Item1.FontSize = 20 * _scale; pair.Item1.Foreground = new SolidColorBrush(UiTheme.Text); pair.Item1.CaretBrush = pair.Item1.Foreground;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(Local(now)); _mouse = now; if (changed) InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); e.Handled = true;
        if (_saving || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = Local(e.GetPosition(this));
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(p));
        switch (hit.Key)
        {
            case "close": case "cancel": Close(); break;
            case "search": _ = SearchAsync(); break;
            case "file": _ = PickFileAsync(); break;
            case "reset": _ = ResetAsync(); break;
            case "save": _ = SaveAsync(); break;
            case "poster": _poster = hit.Index; _localPoster = null; _status = ""; InvalidateVisual(); break;
            case "matches":
                var at = new Point(_sheet.X + 36 * _scale, _sheet.Y + 260 * _scale);
                MenuRequested?.Invoke(_matches.Select((m, i) => new MenuEntry(m.Title + "  " + m.Year, null, () => _ = SelectMatchAsync(i, true)) { Checked = i == _match }).ToList(), at);
                break;
        }
    }

    internal void TestFields(string title, string year) { _title.Text = title; _year.Text = year; }
    internal Task TestSaveAsync() => SaveAsync();
    internal Task TestSearchAsync() => SearchAsync();
}
