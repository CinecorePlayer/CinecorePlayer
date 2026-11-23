using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CinecorePlayer2025.Audio;

namespace CinecoreMac;

/// <summary>Ricorda se la guida del mix manuale e' gia' stata vista (MixHelpStore di Windows).</summary>
internal static class MixHelpStore
{
    private static string Flag => Path.Combine(LibraryStore.AppDataDir, "mix-help-seen.txt");
    public static bool Seen { get { try { return File.Exists(Flag); } catch { return true; } } }
    public static void MarkSeen() { try { if (!Seen) { Directory.CreateDirectory(Path.GetDirectoryName(Flag)!); File.WriteAllText(Flag, "1"); } } catch { } }
}

/// <summary>
/// Editor del passaggio fra il brano in corso e il successivo (ManualMixForm di Windows, sul modello di "Modifica
/// transizione" di Spotify): in alto i due brani interi uno dopo l'altro (con il tratto mostrato sotto), poi il brano che
/// esce, l'editor con le due forme d'onda (bassi e resto in due colori), le battute e le curve di volume, EQ e filtri, il
/// brano che entra, i preset e i quattro selettori. Si trascina un brano per spostarlo, i bordi del riquadro per la durata.
/// Un clic in una forma d'onda sceglie da dove ascoltarla; ▶ accanto a un brano lo fa sentire da solo, ▶ al centro fa
/// sentire il passaggio.
/// </summary>
internal sealed partial class ManualMixView : Control
{
    private static readonly FontFamily Display = new("avares://CinecoreMac/Assets/Fonts#Inter Display");
    private static readonly FontFamily Inter = new("avares://CinecoreMac/Assets/Fonts#Inter");
    private static readonly Typeface DisplaySemibold = new(Display, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface DisplayRegular = new(Display, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface UiRegular = new(Inter, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface UiSemibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface UiBold = new(Inter, FontStyle.Normal, FontWeight.Bold);

    public sealed record Prepared(TrackMixProfile? From, TrackMixProfile? To, (float[] Full, float[] Low) FromWave, (float[] Full, float[] Low) ToWave);

    /// <summary>Scelta salvata: null = la coppia torna all'automix.</summary>
    public event Action<string, string, ManualMix?>? Saved;
    public event Action? Closed;

    private string _fromPath = "", _toPath = "", _fromTitle = "", _toTitle = "", _fromArtist = "", _toArtist = "";
    private Func<float> _volume = () => 0.5f;
    private Action? _pauseMain;
    private Bitmap? _fromCover, _toCover;
    private TrackMixProfile? _from, _to;
    private (float[] Full, float[] Low)? _fromWave, _toWave;
    private const double WavePointsPerSecond = 40;
    private double _fromDuration, _toDuration;
    private double _outAt, _inAt, _fade = 8;
    private int _vol = TransitionCurves.VolumeFade, _eq = TransitionCurves.EqBassMid, _filter = TransitionCurves.FilterNone, _fx = TransitionCurves.FxNone;
    private bool _auto, _ready, _snap = true;
    // L'utente ha cambiato qualcosa a mano (le curve disegnate lo segnano, ManualMixView.Lanes.cs).
    private bool _userChanged;
    private double _margin = 8;
    private double _viewOffset;
    // Tempo del brano che entra, come il pitch fader di un DJ: null = Auto (agganciato al tempo di quello che esce).
    private const double TempoMin = 0.80, TempoMax = 1.08;
    private MixPreviewPlayer.Lane? _tempoResume;
    private double? _incomingSpeed;
    /// <summary>Velocita' del brano che finisce durante il passaggio (1 = la sua).</summary>
    private double _outgoingSpeed = 1;
    private double OutSpeed => _vol == TransitionCurves.VolumeNone && !_auto ? 1 : _outgoingSpeed;
    private double? _cursorA, _cursorB;
    private (double? From, double? To) _heard;
    private readonly MixPreviewPlayer _preview = new();
    private readonly DispatcherTimer _playhead;
    private DateTime _openedAt;

    private Rect _overview, _rowA, _rowB, _editor, _laneA, _laneB, _playMix, _playA, _playB, _close, _help, _header;
    private Rect _tempoB, _tempoTrack;
    private bool _helpOpen;
    private readonly Rect[] _cardPrev = new Rect[4], _cardNext = new Rect[4], _cards = new Rect[4];
    private readonly List<(Rect Bounds, int Index)> _presetHits = new();
    private string _hover = "";
    private string? _drag;
    private Point _dragStart;
    private double _dragOut, _dragIn, _dragFade, _dragScale, _dragViewStart;
    private bool _dragMoved;
    private string? _previewError;

    private static readonly (string Name, int Vol, int Eq, int Filter, int Fx)[] Presets =
    {
        ("Auto", -1, -1, -1, 0),
        ("Dissolvenza", TransitionCurves.VolumeFade, TransitionCurves.EqBassMid, TransitionCurves.FilterNone, 0),
        ("Ascendente", TransitionCurves.VolumeOverlap, TransitionCurves.EqBassEnd, TransitionCurves.FilterRise, 0),
        ("Sfumatura", TransitionCurves.VolumeSlow, TransitionCurves.EqBassMid, TransitionCurves.FilterHighPass, 0),
        ("Onda", TransitionCurves.VolumeOverlap, TransitionCurves.EqBassMid, TransitionCurves.FilterLowPass, 0),
        ("Fusione", TransitionCurves.VolumeOverlap, TransitionCurves.EqThreeBand, TransitionCurves.FilterNone, 0),
        ("Brusco", TransitionCurves.VolumeCut, TransitionCurves.EqNone, TransitionCurves.FilterNone, 0),
        ("Nessuna", TransitionCurves.VolumeNone, TransitionCurves.EqNone, TransitionCurves.FilterNone, 0),
        ("Costruzione", TransitionCurves.VolumeFade, TransitionCurves.EqBassMid, TransitionCurves.FilterNone, TransitionCurves.FxBuild),
        ("Loop roll", TransitionCurves.VolumeSlow, TransitionCurves.EqBassMid, TransitionCurves.FilterRise, TransitionCurves.FxRoll),
        ("Eco", TransitionCurves.VolumeCut, TransitionCurves.EqNone, TransitionCurves.FilterNone, TransitionCurves.FxEcho),
        ("Riverbero", TransitionCurves.VolumeFade, TransitionCurves.EqNone, TransitionCurves.FilterHighPass, TransitionCurves.FxReverb),
        ("Loop", TransitionCurves.VolumeFade, TransitionCurves.EqBassMid, TransitionCurves.FilterNone, TransitionCurves.FxLoop),
        ("Gate", TransitionCurves.VolumeFade, TransitionCurves.EqBassMid, TransitionCurves.FilterNone, TransitionCurves.FxGate),
        ("Riser", TransitionCurves.VolumeSlow, TransitionCurves.EqBassEnd, TransitionCurves.FilterRise, TransitionCurves.FxRiser),
        ("Brake", TransitionCurves.VolumeSlow, TransitionCurves.EqNone, TransitionCurves.FilterNone, TransitionCurves.FxBrake),
        ("Spinback", TransitionCurves.VolumeSlow, TransitionCurves.EqNone, TransitionCurves.FilterNone, TransitionCurves.FxSpin),
    };
    private const int FirstDjPreset = 8;
    private static readonly string[] FxNames = { "Nessuno", "Eco in uscita", "Riverbero", "Loop di una battuta", "Loop roll", "Brake", "Spinback", "Gate", "Riser", "Costruzione" };
    private static readonly string[] VolumeNames = { "Sovrapposizione", "Dissolvenza", "Lineare", "Lenta", "Taglio al centro", "Nessuna" };
    private static readonly string[] EqNames = { "Nessuno", "Bassi a metà", "Bassi alla fine", "Tre bande" };
    private static readonly string[] FilterNames = { "Nessuno", "Si apre e si chiude", "Passa alto", "Passa basso" };

    private static Color Hi => UiTheme.Text;
    private static Color VolumeInk => Hi;
    private static Color EqInk => UiTheme.WithAlpha(UiTheme.Text, 170);
    private static Color FilterInk => UiTheme.WithAlpha(UiTheme.Text, 110);
    private static Color Sheet => UiTheme.Panel;

    private double K => Math.Clamp(Math.Min(Bounds.Width / 1500, Bounds.Height / 860), 0.54, 1.8);
    private double P(double v) => Math.Round(v * K);

    public bool IsOpen => IsVisible;

    public ManualMixView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        _playhead = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) =>
        {
            _heard = _preview.Position;
            if (_preview.Playing) FollowPreview();
            InvalidateVisual();
            if (!_preview.Playing) { RememberPreviewPosition(); _playhead!.Stop(); }
        });
        _playhead.Stop(); // parte con l'anteprima
    }

    /// <summary>Analisi e forme d'onda dei due brani, pronte prima di aprire: la scheda si apre intera, non a pezzi.</summary>
    public static async Task<Prepared> PrepareAsync(string fromPath, string toPath)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // I due brani insieme (analisi e forma d'onda di ognuno in parallelo): prima uno dopo l'altro, ~4 s all'apertura.
        var fromTask = AutomixAnalyzer.PrepareAsync(fromPath);
        var toTask = AutomixAnalyzer.PrepareAsync(toPath);
        var fromWave = Task.Run(async () => Wave(fromPath, (await fromTask.ConfigureAwait(false))?.Duration ?? 0));
        var toWave = Task.Run(async () => Wave(toPath, (await toTask.ConfigureAwait(false))?.Duration ?? 0));
        await Task.WhenAll(fromWave, toWave).ConfigureAwait(false);
        Console.Error.WriteLine($"[mix] editor pronto in {clock.ElapsedMilliseconds} ms");
        return new Prepared(await fromTask, await toTask, fromWave.Result, toWave.Result);
    }

    // Forme d'onda gia' lette (per percorso e data del file): riaprire l'editor o il passaggio successivo non rilegge tutto il brano.
    private static readonly Dictionary<string, (float[] Full, float[] Low)> WaveCache = new(StringComparer.Ordinal);

    private static (float[] Full, float[] Low) Wave(string path, double duration)
    {
        string key = path + "|" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
        lock (WaveCache) if (WaveCache.TryGetValue(key, out var cached)) return cached;
        var wave = Normalise(AutomixAnalyzerEnvelope.ReadBands(path, 0, duration, (int)Math.Max(200, duration * WavePointsPerSecond)));
        lock (WaveCache) { if (WaveCache.Count > 24) WaveCache.Clear(); WaveCache[key] = wave; }
        return wave;
    }

    public void Open(string fromPath, string toPath, string fromTitle, string toTitle, string fromArtist, string toArtist,
        string? fromCover, string? toCover, Func<float> volume, Action? pauseMain, Prepared prepared)
    {
        _preview.Stop();
        _fromPath = fromPath; _toPath = toPath; _fromTitle = fromTitle; _toTitle = toTitle;
        _fromArtist = fromArtist; _toArtist = toArtist; _volume = volume; _pauseMain = pauseMain;
        _fromCover?.Dispose(); _toCover?.Dispose();
        _fromCover = LoadCover(fromCover); _toCover = LoadCover(toCover);
        _tintA = CoverTint(fromCover); _tintB = CoverTint(toCover);
        _cursorA = _cursorB = null; _drag = null; _hover = ""; _previewError = null;
        _heard = (null, null); _incomingSpeed = null; _outgoingSpeed = 1;
        _phaseJob++;
        _vol = TransitionCurves.VolumeFade; _eq = TransitionCurves.EqBassMid; _filter = TransitionCurves.FilterNone; _fx = TransitionCurves.FxNone;
        _auto = false;
        _lanes.Clear(); _editLane = null; _lanePoint = false;
        _helpOpen = !MixHelpStore.Seen;
        Apply(prepared);
        _confirm = false;
        _openedState = StateKey();
        _openedAt = DateTime.UtcNow;
        IsVisible = true;
        Focus();
        InvalidateVisual();
    }

    // ----- chiusura (come l'editor delle foto): la X chiude; con modifiche, Salva o Elimina sulla vignettatura -----
    private bool _confirm;
    private string _openedState = "";
    private Rect _confirmSave, _confirmDiscard, _confirmKeep;

    private string StateKey() => string.Create(CultureInfo.InvariantCulture,
        $"{_auto}|{_outAt:0.000}|{_inAt:0.000}|{_fade:0.00}|{_vol}|{_eq}|{_filter}|{_fx}|{_incomingSpeed:0.0000}|{_outgoingSpeed:0.0000}");

    private void RequestClose()
    {
        StopPreview();
        if (StateKey() == _openedState) { Close(); return; }
        _confirm = true; _tipKey = "";
        InvalidateVisual();
    }

    private void DrawConfirm(DrawingContext ctx)
    {
        var page = new Rect(Bounds.Size);
        ctx.FillRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(215, 0, 0, 0), 0), new GradientStop(Color.FromArgb(160, 0, 0, 0), 1) },
        }, page);
        double cx = page.Width / 2, cy = page.Height / 2;
        TextIn(ctx, "Salvare il passaggio?", P(22), DisplaySemibold, Colors.White, new Rect(0, cy - P(78), page.Width, P(32)), TextAlignment.Center);
        TextIn(ctx, "La prossima volta che questi due brani si susseguono il passaggio sarà così.", P(13.5), UiRegular, Color.FromArgb(185, 255, 255, 255),
            new Rect(0, cy - P(40), page.Width, P(22)), TextAlignment.Center);
        double bw = P(170), bh = P(38), gap = P(12), by = cy;
        _confirmDiscard = new Rect(cx - gap / 2 - bw, by, bw, bh);
        _confirmSave = new Rect(cx + gap / 2, by, bw, bh);
        bool hd = _hover == "c-discard", hs = _hover == "c-save", hk = _hover == "c-keep";
        ctx.DrawRectangle(B(Color.FromArgb((byte)(hd ? 60 : 38), 255, 255, 255)), null, _confirmDiscard, bh / 2, bh / 2);
        TextIn(ctx, "Elimina modifiche", P(14), UiSemibold, Colors.White, _confirmDiscard, TextAlignment.Center);
        ctx.DrawRectangle(B(hs ? Color.FromRgb(232, 232, 236) : Colors.White), null, _confirmSave, bh / 2, bh / 2);
        TextIn(ctx, "Salva", P(14), UiSemibold, Color.FromRgb(14, 16, 20), _confirmSave, TextAlignment.Center);
        double kw = Measure("Continua a modificare", P(13.5), UiRegular) + P(20);
        _confirmKeep = new Rect(cx - kw / 2, by + bh + P(14), kw, P(28));
        TextIn(ctx, "Continua a modificare", P(13.5), UiRegular, Color.FromArgb((byte)(hk ? 255 : 190), 255, 255, 255), _confirmKeep, TextAlignment.Center);
    }

    public void Close()
    {
        if (!IsVisible) return;
        StopPreview();
        IsVisible = false;
        Closed?.Invoke();
    }

    private static Bitmap? LoadCover(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 160, BitmapInterpolationMode.HighQuality);
        }
        catch { return null; }
    }

    private void Apply(Prepared prepared)
    {
        _from = prepared.From; _to = prepared.To;
        _fromDuration = _from?.Duration ?? 0; _toDuration = _to?.Duration ?? 0;
        double end = _from?.End ?? _fromDuration;
        var saved = ManualMixStore.Get(_fromPath, _toPath);
        if (saved != null)
        {
            _outAt = saved.OutAt; _inAt = saved.InAt; _fade = Math.Max(0.5, saved.Fade);
            if (saved.IncomingSpeed is double speed && double.IsFinite(speed)) _incomingSpeed = Math.Clamp(speed, TempoMin, TempoMax);
            if (saved.OutgoingSpeed is double outSpeed && double.IsFinite(outSpeed)) _outgoingSpeed = Math.Clamp(outSpeed, TempoMin, TempoMax);
            LoadLanes(saved.HasStyle ? saved.Lanes : null);
            if (saved.HasStyle) { _vol = saved.Volume; _eq = Math.Max(0, saved.Eq); _filter = Math.Max(0, saved.Filter); _fx = Math.Clamp(saved.Fx, 0, TransitionCurves.FxCount - 1); }
        }
        else
        {
            _auto = true;
            _fade = Math.Clamp(_from?.FadeSeconds ?? 8, 4, 16);
            if (_from?.Tail is { Reliable: true } tail) _fade = Math.Max(4, Math.Round(_fade / (tail.Period * 4)) * tail.Period * 4);
            _inAt = _to?.LeadIn ?? 0;
            _outAt = Math.Max(0, end - _fade);
        }
        _snap = _from?.Tail.Reliable == true && _to?.Head.Reliable == true;
        FitView();
        _fromWave = prepared.FromWave; _toWave = prepared.ToWave;
        _ready = _fromDuration > 0 && _toDuration > 0;
        if (saved == null) RefinePhaseAsync();
    }

    private static (float[] Full, float[] Low) Normalise((float[] Full, float[] Low) w)
    {
        static float Clean(float v) => float.IsFinite(v) && v > 0 ? v : 0;
        var full = (w.Full ?? Array.Empty<float>()).Select(Clean).ToArray();
        var low = (w.Low ?? Array.Empty<float>()).Select(Clean).ToArray();
        if (low.Length != full.Length) Array.Resize(ref low, full.Length);
        float peak = Math.Max(0.05f, full.DefaultIfEmpty(0).Max());
        return (full.Select(v => Math.Min(1, v / peak)).ToArray(), low.Select(v => Math.Min(1, v / peak)).ToArray());
    }

    private void FitView()
    {
        _margin = Math.Clamp(Math.Max(6, _fade * 0.55), 6, 40);
        _viewOffset = 0;
    }

    // ------------------------------------------------------------------ layout

    private void LayoutZones()
    {
        double w = Bounds.Width, h = Bounds.Height, pad = P(40);
        _close = new Rect(w - P(24) - P(36), P(20), P(36), P(36));
        _header = new Rect(pad, P(22), w - pad * 2, P(62));
        _overview = new Rect(pad, _header.Bottom + P(14), w - pad * 2, P(40));
        _rowA = new Rect(pad, _overview.Bottom + P(20), w - pad * 2, P(46));
        // Parte bassa, dall'alto: brano che entra, riga dei preset (due gruppi), i quattro selettori, i pulsanti a destra.
        double groupH = P(18), tileH = P(52), cardsH = P(56), buttonsH = P(40);
        double bottomBlock = P(8) + P(46) + P(22) + groupH + P(4) + tileH + P(18) + cardsH + P(16) + buttonsH + P(20);
        _editor = new Rect(pad, _rowA.Bottom + P(8), w - pad * 2, Math.Max(P(124), h - (_rowA.Bottom + P(8)) - bottomBlock));
        double transportH = P(44), laneH = (_editor.Height - transportH) / 2;
        _laneA = new Rect(_editor.Left, _editor.Top, _editor.Width, laneH);
        _laneB = new Rect(_editor.Left, _laneA.Bottom + transportH, _editor.Width, laneH);
        _playMix = new Rect(_editor.Center.X - P(18), _laneA.Bottom + (transportH - P(36)) / 2, P(36), P(36));
        _rowB = new Rect(pad, _editor.Bottom + P(8), w - pad * 2, P(46));
        _playA = new Rect(_rowA.Right - P(30), _rowA.Top + (_rowA.Height - P(30)) / 2, P(30), P(30));
        _playB = new Rect(_rowB.Right - P(30), _rowB.Top + (_rowB.Height - P(30)) / 2, P(30), P(30));
        _tempoB = new Rect(_playB.Left - P(24) - TempoColumn, _rowB.Top, TempoColumn, _rowB.Height);
        _tempoTrack = new Rect(_tempoB.Left + P(8), _rowB.Top + _rowB.Height * .72 - P(1), _tempoB.Width - P(16), P(2));
        _help = new Rect(_close.Left - P(8) - P(36), _close.Top, P(36), P(36));

        // Preset: una riga sola, "Transizioni" (8) e "Tecniche DJ" (9) separati da uno spazio, tutti della stessa misura.
        _presetHits.Clear();
        _groupsTop = _rowB.Bottom + P(22);
        double tilesTop = _groupsTop + groupH + P(4), gap = P(6), groupGap = P(28);
        int count = Presets.Length;
        double tileW = (w - pad * 2 - groupGap - (count - 2) * gap) / count;
        double x = pad;
        for (int i = 0; i < count; i++)
        {
            if (i == FirstDjPreset) x += groupGap - gap;
            _presetHits.Add((new Rect(x, tilesTop, tileW, tileH), i));
            x += tileW + gap;
        }
        // I quattro selettori sulla stessa griglia, sotto.
        double cardsTop = tilesTop + tileH + P(18);
        double cardW = (w - pad * 2) / 4;
        for (int i = 0; i < 4; i++) _cards[i] = new Rect(pad + i * cardW, cardsTop, cardW, cardsH);
        _divider = cardsTop + cardsH + P(8);
    }

    private double _groupsTop, _divider;
    // Colonne a destra delle righe dei brani (uguali per i due): tonalita', BPM, posizione, tempo, ▶.
    private double TempoColumn => P(210);
    private double TimeColumn => P(120);
    private double BpmColumn => P(76);
    private double KeyColumn => P(58);

    private double EffectiveSpeed
    {
        get
        {
            if (_vol == TransitionCurves.VolumeNone && !_auto) return 1;
            if (!_auto && _incomingSpeed is double speed) return speed;
            // Auto: il brano che entra prende il tempo effettivo di quello che finisce (anche se e' stato accelerato o
            // rallentato), come fa il motore.
            if (!CinecoreAudioSettings.Current.AutomixTempoSync || _from?.Tail is not { Reliable: true } tail ||
                _to?.Head is not { Reliable: true, Bpm: > 0 } head) return 1;
            double ratio = tail.Bpm / head.Bpm;
            return Math.Abs(ratio - 1) is >= .002 and <= .06 ? ratio : 1;
        }
    }
    private double ViewStart => _viewOffset - _margin;
    private double ViewSpan => _fade + _margin * 2;
    private double X(double tau) => _editor.Left + (tau - ViewStart) / ViewSpan * _editor.Width;
    private double Tau(double x) => ViewStart + (x - _editor.Left) / Math.Max(1, _editor.Width) * ViewSpan;
    private double SecondsPerPixel => ViewSpan / Math.Max(1, _editor.Width);
    private double TrackTime(bool outgoing, double tau) => outgoing ? _outAt + (tau > 0 ? tau * OutSpeed : tau) : _inAt + tau * EffectiveSpeed;
    private double TimelineTime(bool outgoing, double seconds) => outgoing
        ? (seconds > _outAt ? (seconds - _outAt) / OutSpeed : seconds - _outAt) : (seconds - _inAt) / EffectiveSpeed;
    private double OX(double seconds, double duration) => _overview.Left + seconds / Math.Max(1, duration) * _overview.Width;
    private bool MixingPreview => _preview.Playing && _preview.Mode == MixPreviewPlayer.Lane.None;
    private double PreviewFade => Math.Min(_vol == TransitionCurves.VolumeNone && !_auto ? .08 : _fade, Math.Max(.08, _fromDuration - _outAt));

    private TransitionCurves Curves => _auto || _vol < 0 || _vol == TransitionCurves.VolumeNone || _lanes.Count == 0 ? BaseCurves
        : BaseCurves.WithLanes(LanesForSave(), _from?.Tail is { Reliable: true } la ? la.Period : 0.5, _to?.Head is { Reliable: true } lb ? lb.Period : 0.5);
    /// <summary>Le curve dei selettori, senza le corsie disegnate a mano.</summary>
    private TransitionCurves BaseCurves => _auto || _vol < 0 ? TransitionCurves.Compose(TransitionCurves.VolumeFade, TransitionCurves.EqBassMid, 0, Bars)
        : TransitionCurves.Compose(_vol == TransitionCurves.VolumeNone ? TransitionCurves.VolumeCut : _vol, _eq, _filter, Bars)
            .ApplyFx(_vol == TransitionCurves.VolumeNone ? 0 : _fx, Bars, _from?.Tail is { Reliable: true } beat ? beat.Period : 0.5,
                _to?.Head is { Reliable: true } head ? head.Period : _from?.Tail is { Reliable: true } b2 ? b2.Period : 0.5);
    private int Bars => _from?.Tail is { Reliable: true } t && t.Period > 0 ? Math.Clamp((int)Math.Round(_fade / (t.Period * 4)), 1, 64) : 8;

    // ------------------------------------------------------------------ colori

    private Color? _tintA, _tintB;
    private Color TintA => ForTheme(_tintA ?? Hi);
    private Color TintB => ForTheme(_tintB ?? Hi);
    private static Color ForTheme(Color c) => UiTheme.IsLight && Lightness(c) > .45 ? UiTheme.Mix(c, Colors.Black, .22) : c;

    private static (double H, double S, double L) Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > .5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }
    private static double Lightness(Color c) => Hsl(c).L;

    /// <summary>Colore del brano preso dalla copertina (saturato, mai spento): tinge lo sfondo e la sua forma d'onda.</summary>
    private static Color? CoverTint(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var full = SkiaSharp.SKBitmap.Decode(path);
            if (full == null) return null;
            using var bmp = full.Resize(new SkiaSharp.SKImageInfo(48, 48), SkiaSharp.SKFilterQuality.Medium);
            if (bmp == null) return null;
            double r = 0, g = 0, b = 0, wsum = 0;
            for (int y = 0; y < bmp.Height; y += 3)
                for (int x = 0; x < bmp.Width; x += 3)
                {
                    var p = bmp.GetPixel(x, y);
                    var (_, s, v) = Hsl(Color.FromRgb(p.Red, p.Green, p.Blue));
                    double weight = 0.05 + s * s * (v > .12 && v < .92 ? 1 : .2);
                    r += p.Red * weight; g += p.Green * weight; b += p.Blue * weight; wsum += weight;
                }
            if (wsum <= 0) return null;
            var avg = Color.FromRgb((byte)(r / wsum), (byte)(g / wsum), (byte)(b / wsum));
            var hsl = Hsl(avg);
            if (hsl.S < .12) return null;
            return FromHsv(hsl.H, Math.Clamp(hsl.S * 1.25, .45, .85), .92);
        }
        catch { return null; }
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x) : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static Color A(Color c, double a) => UiTheme.WithAlpha(c, a);
    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static double Px(double size) => size; // misure gia' in pixel logici (come i GraphicsUnit.Pixel di Windows)

    private static CachedText Ft(string text, double size, Typeface face, Color color, double maxWidth = double.PositiveInfinity, TextAlignment align = TextAlignment.Left, int lines = 1) =>
        UiText.Get(L10n.T(text), face, Math.Max(1, Px(size)), color, maxWidth, lines, align);

    private static void TextIn(DrawingContext ctx, string text, double size, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = Ft(text, size, face, color, r.Width, align);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private static double Measure(string text, double size, Typeface face) => Ft(text, size, face, Colors.White).WidthIncludingTrailingWhitespace;

    // ------------------------------------------------------------------ disegno

    public override void Render(DrawingContext ctx)
    {
        LayoutZones();
        var page = new Rect(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 260, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        using var fadeIn = ctx.PushOpacity(e);
        DrawAmbient(ctx, page);
        double k = K;
        double eyebrow = 11.5 * k, label = 15 * k, small = 12.5 * k, value = 16 * k, tile = 12.5 * k;

        // Intestazione: i due titoli con una freccia in mezzo.
        {
            double titleSize = 24 * k;
            string arrow = "  →  ";
            double wa = Measure(_fromTitle, titleSize, DisplaySemibold), wb = Measure(_toTitle, titleSize, DisplaySemibold), wr = Measure(arrow, titleSize, DisplaySemibold);
            double maxSide = (_header.Width - P(140) - wr) / 2;
            wa = Math.Min(wa, maxSide); wb = Math.Min(wb, maxSide);
            double x = _header.Left + (_header.Width - wa - wr - wb) / 2, y = _header.Top + P(12), hh = P(38);
            TextIn(ctx, _fromTitle, titleSize, DisplaySemibold, UiTheme.Text, new Rect(x, y, wa + 2, hh));
            TextIn(ctx, arrow, titleSize, DisplaySemibold, UiTheme.Muted, new Rect(x + wa, y, wr + 2, hh));
            TextIn(ctx, _toTitle, titleSize, DisplaySemibold, UiTheme.Text, new Rect(x + wa + wr, y, wb + 2, hh));
        }
        DrawClose(ctx, _close, _hover == "close");
        TextIn(ctx, "?", 19 * k, DisplaySemibold, A(UiTheme.Text, _hover == "help" || _helpOpen ? 255 : 160), _help, TextAlignment.Center);

        DrawOverview(ctx);
        DrawTrackRow(ctx, _rowA, _playA, _fromCover, _fromTitle, _fromArtist, _from, tail: true,
            playing: _preview.Playing && _preview.Mode == MixPreviewPlayer.Lane.From, hot: _hover == "playA", label, small, value);
        DrawEditor(ctx, small);
        DrawTrackRow(ctx, _rowB, _playB, _toCover, _toTitle, _toArtist, _to, tail: false,
            playing: _preview.Playing && _preview.Mode == MixPreviewPlayer.Lane.To, hot: _hover == "playB", label, small, value);

        // Preset: una riga, due gruppi con il nome sopra (stessa colonna sinistra delle tessere).
        int active = ActivePreset();
        if (_presetHits.Count > 0)
        {
            var first = _presetHits[0].Bounds; var dj = _presetHits.First(p => p.Index == FirstDjPreset).Bounds;
            var lastT = _presetHits.First(p => p.Index == FirstDjPreset - 1).Bounds; var lastD = _presetHits[^1].Bounds;
            TextIn(ctx, "Transizioni", eyebrow, UiSemibold, UiTheme.Muted, new Rect(first.Left, _groupsTop, lastT.Right - first.Left, P(18)));
            TextIn(ctx, "Tecniche DJ", eyebrow, UiSemibold, UiTheme.Muted, new Rect(dj.Left, _groupsTop, lastD.Right - dj.Left, P(18)));
            var line = new Pen(B(A(UiTheme.Text, UiTheme.IsLight ? 22 : 26)), 1);
            double sep = (lastT.Right + dj.Left) / 2;
            ctx.DrawLine(line, new Point(sep, first.Top + P(4)), new Point(sep, first.Bottom - P(4)));
        }
        foreach (var (r, i) in _presetHits)
        {
            bool sel = i == active, hot = _hover == "preset" + i;
            double inset = Math.Min(P(15), r.Width * .22);
            var curveRect = new Rect(r.Left + inset, r.Top + P(3), r.Width - inset * 2, r.Height - P(22));
            DrawPresetCurve(ctx, curveRect, i, sel ? Hi : hot ? A(Hi, 200) : A(UiTheme.Text, UiTheme.IsLight ? 100 : 80), sel);
            Color ink = sel ? Hi : hot ? UiTheme.Text : UiTheme.SubtleText;
            TextIn(ctx, Presets[i].Name, tile, sel ? UiBold : UiRegular, ink, new Rect(r.Left - P(2), r.Bottom - P(19), r.Width + P(4), P(19)), TextAlignment.Center);
        }

        // I quattro selettori: nome piccolo sopra, valore sotto con le frecce ai lati.
        string[] names = { "Volume", "EQ", "Filtro", "Effetto" };
        string[] values =
        {
            _auto ? "Auto" : VolumeNames[Math.Clamp(_vol, 0, 5)],
            _auto ? "Auto" : EqNames[Math.Clamp(_eq, 0, 3)],
            _auto ? "Auto" : FilterNames[Math.Clamp(_filter, 0, 3)],
            _auto ? "Auto" : FxNames[Math.Clamp(_fx, 0, TransitionCurves.FxCount - 1)],
        };
        for (int i = 0; i < 4; i++)
        {
            var r = _cards[i];
            bool disabled = _vol == TransitionCurves.VolumeNone && i > 0 && !_auto;
            TextIn(ctx, names[i], eyebrow, UiSemibold, UiTheme.Muted, new Rect(r.Left, r.Top + P(2), r.Width, P(18)), TextAlignment.Center);
            double tw = Math.Min(r.Width - P(80), Measure(values[i], value, UiBold));
            double cx = r.Left + r.Width / 2, ay = r.Top + P(22);
            _cardPrev[i] = new Rect(cx - tw / 2 - P(10) - P(28), ay, P(28), P(34));
            _cardNext[i] = new Rect(cx + tw / 2 + P(10), ay, P(28), P(34));
            TextIn(ctx, values[i], value, UiBold, disabled ? UiTheme.Muted : _hover == "card" + i ? Hi : A(UiTheme.Text, 215), new Rect(cx - tw / 2 - 2, ay, tw + 4, P(34)), TextAlignment.Center);
            DrawChevron(ctx, _cardPrev[i], left: true, _hover == "prev" + i ? Hi : UiTheme.SubtleText);
            DrawChevron(ctx, _cardNext[i], left: false, _hover == "next" + i ? Hi : UiTheme.SubtleText);
        }

        if (_previewError != null)
            TextIn(ctx, "Anteprima non disponibile: " + _previewError, small, UiRegular, UiTheme.Muted, new Rect(_editor.Left, _playMix.Bottom + P(4), _editor.Width, P(20)), TextAlignment.Center);
        if (_helpOpen) DrawHelp(ctx);
        if (_confirm) DrawConfirm(ctx);
        else DrawTip(ctx);
    }

    // Sfondo: la tinta della scheda con due aloni morbidi (copertina di A in alto a sinistra, di B in basso a destra).
    private void DrawAmbient(DrawingContext ctx, Rect page)
    {
        ctx.FillRectangle(B(Sheet), page);
        double strength = UiTheme.IsLight ? .10 : .17;
        void Glow(Color tint, double fx, double fy)
        {
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(fx, fy, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(fx, fy, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(.55, RelativeUnit.Relative), RadiusY = new RelativeScalar(.65, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(A(tint, 255 * strength), 0),
                    new GradientStop(A(tint, 255 * strength * .5), .45),
                    new GradientStop(A(tint, 0), 1),
                },
            };
            ctx.FillRectangle(brush, page);
        }
        if (_tintA is { } a) Glow(ForTheme(a), .12, .05);
        if (_tintB is { } b) Glow(ForTheme(b), .90, .80);
    }

    private void DrawClose(DrawingContext ctx, Rect r, bool hot)
    {
        var pen = new Pen(B(A(UiTheme.Text, hot ? 255 : 170)), Math.Max(1.6, 2 * K), lineCap: PenLineCap.Round);
        double s = r.Width * .22, cx = r.Center.X, cy = r.Center.Y;
        ctx.DrawLine(pen, new Point(cx - s, cy - s), new Point(cx + s, cy + s));
        ctx.DrawLine(pen, new Point(cx + s, cy - s), new Point(cx - s, cy + s));
    }

    private void DrawHelp(DrawingContext ctx)
    {
        ctx.FillRectangle(B(A(Sheet, 250)), new Rect(Bounds.Size));
        double k = K;
        var steps = new (string T, string B)[]
        {
            ("Sposta i brani", "Trascina la forma d'onda in alto (il brano che finisce) o quella in basso (il brano che entra) per scegliere dove si incontrano. Le battute si agganciano da sole."),
            ("Allunga o accorcia", "Tira i due bordi del riquadro chiaro: è la durata del passaggio, in battute. La rotellina o il trackpad avvicinano o allontanano la vista."),
            ("Scegli lo stile", "Le curve sotto sono le transizioni pronte e le tecniche da DJ. Volume, EQ, Filtro ed Effetto le regolano una per una."),
            ("Ascolta e salva", "Il tasto al centro (o Spazio) fa ascoltare il passaggio; quelli a destra dei brani li fanno ascoltare da soli. Salva (o Invio) lo usa la prossima volta."),
        };
        double w = Math.Min(Bounds.Width - P(160), P(980)), x = (Bounds.Width - w) / 2;
        double colW = (w - P(60)) / 2, rowH = P(150), top = (Bounds.Height - (P(70) + rowH * 2)) / 2;
        ctx.DrawText(Ft("Come si fa un mix", 26 * k, DisplaySemibold, UiTheme.Text, w), new Point(x, top));
        for (int i = 0; i < steps.Length; i++)
        {
            double cx = x + (i % 2) * (colW + P(60)), cy = top + P(70) + (i / 2) * rowH;
            ctx.DrawText(Ft((i + 1).ToString(CultureInfo.InvariantCulture), 30 * k, DisplayRegular, A(UiTheme.Text, 120)), new Point(cx, cy - P(4)));
            ctx.DrawText(Ft(steps[i].T, 16.5 * k, DisplaySemibold, UiTheme.Text, colW - P(44)), new Point(cx + P(44), cy));
            ctx.DrawText(Ft(steps[i].B, 14 * k, DisplayRegular, UiTheme.Muted, colW - P(44), lines: 5), new Point(cx + P(44), cy + P(30)));
        }
        ctx.DrawText(Ft("Clic in un punto qualsiasi per tornare al mix", 12.5 * k, UiRegular, A(UiTheme.Text, 150), w), new Point(x, top + P(70) + rowH * 2 + P(10)));
        DrawClose(ctx, _close, _hover == "close");
    }

    private void DrawChevron(DrawingContext ctx, Rect r, bool left, Color ink)
    {
        double cx = r.Center.X, cy = r.Center.Y, s = P(5), d = left ? 1 : -1;
        var pen = new Pen(B(ink), Math.Max(1.6, 2 * K), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx + s * .5 * d, cy - s), false);
            c.LineTo(new Point(cx - s * .5 * d, cy));
            c.LineTo(new Point(cx + s * .5 * d, cy + s));
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    private static Geometry Polyline(IReadOnlyList<Point> pts, bool closed = false)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        if (pts.Count == 0) return g;
        c.BeginFigure(pts[0], closed);
        for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i]);
        c.EndFigure(closed);
        return g;
    }

    private static readonly Dictionary<int, (double[] Out, double[] In)> PresetCurveCache = new();
    private void DrawPresetCurve(DrawingContext ctx, Rect r, int index, Color ink, bool selected)
    {
        if (r.Width < 4 || r.Height < 4) return;
        if (!PresetCurveCache.TryGetValue(index, out var c))
        {
            const int n = 48;
            var p = Presets[index];
            TransitionCurves curves = p.Vol < 0
                ? TransitionCurves.Compose(TransitionCurves.VolumeFade, TransitionCurves.EqBassMid, 0, 8)
                : TransitionCurves.Compose(p.Vol == TransitionCurves.VolumeNone ? TransitionCurves.VolumeCut : p.Vol, p.Eq, p.Filter, 8).ApplyFx(p.Fx, 8, .5, .5);
            var o = new double[n + 1]; var iN = new double[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                double vo = curves.Out.Volume.ValueAt(t), vi = curves.In.Volume.ValueAt(t);
                o[i] = double.IsFinite(vo) ? Math.Clamp(vo, 0, 1) : 0; iN[i] = double.IsFinite(vi) ? Math.Clamp(vi, 0, 1) : 0;
            }
            if (p.Vol == TransitionCurves.VolumeNone) for (int i = 0; i <= n; i++) { o[i] = i <= n / 2 ? 1 : 0; iN[i] = i <= n / 2 ? 0 : 1; }
            c = (o, iN); PresetCurveCache[index] = c;
        }
        List<Point> Pts(double[] v) => v.Select((y, i) => new Point(r.Left + r.Width * i / (v.Length - 1), r.Bottom - y * r.Height)).ToList();
        double alpha = selected ? 255 : ink.A;
        ctx.DrawGeometry(null, new Pen(B(A(ink, Math.Max(50, alpha * 3 / 5))), Math.Max(1.3, (selected ? 2 : 1.6) * K), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Polyline(Pts(c.In)));
        ctx.DrawGeometry(null, new Pen(B(A(ink, alpha)), Math.Max(1.5, (selected ? 2.4 : 1.8) * K), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Polyline(Pts(c.Out)));
    }

    private void DrawButton(DrawingContext ctx, Rect r, string text, bool primary, bool hot, double size)
    {
        if (primary) ctx.DrawRectangle(B(UiTheme.PrimaryFill(hot)), null, r, r.Height / 2, r.Height / 2);
        TextIn(ctx, text, size, UiSemibold, primary ? UiTheme.OnPrimary : hot ? Hi : A(UiTheme.Text, 200), r, TextAlignment.Center);
    }

    private void DrawOverview(DrawingContext ctx)
    {
        if (!_ready) return;
        using var clip = ctx.PushClip(new RoundedRect(_overview, P(8)));
        double half = _overview.Height / 2;
        var top = new Rect(_overview.Left, _overview.Top + 2, _overview.Width, half - 3);
        var bottom = new Rect(_overview.Left, _overview.Top + half + 1, _overview.Width, half - 3);
        DrawWave(ctx, top, _fromWave, t => OX(t, _fromDuration), _fromDuration, double.MinValue, double.MaxValue, center: false, TintA, flip: true);
        DrawWave(ctx, bottom, _toWave, t => OX(t, _toDuration), _toDuration, double.MinValue, double.MaxValue, center: false, TintB, flip: false);
        void Window(Rect row, bool outgoing, double duration, double cue, double? position)
        {
            using var rowClip = ctx.PushClip(row);
            double start = OX(TrackTime(outgoing, ViewStart), duration), end = OX(TrackTime(outgoing, ViewStart + ViewSpan), duration);
            ctx.FillRectangle(B(A(Hi, UiTheme.IsLight ? 34 : 40)), new Rect(start, row.Top, end - start, row.Height));
            double x = OX(position ?? cue, duration);
            ctx.DrawLine(new Pen(B(A(Hi, 220)), Math.Max(1, 1.4 * K)), new Point(x, row.Top), new Point(x, row.Bottom));
        }
        Window(top, true, _fromDuration, MixingPreview ? Math.Min(_fromDuration, _outAt + PreviewFade) : _cursorA ?? _outAt, _heard.From);
        Window(bottom, false, _toDuration, MixingPreview ? _inAt : _cursorB ?? _inAt, _heard.To);
    }

    // Forma d'onda piena e morbida: involucro del segnale intero in tinta neutra, sopra quello dei bassi nella tinta del
    // brano, sfumati verso i bordi. Fuori dalla parte che si sente resta in trasparenza.
    private void DrawWave(DrawingContext ctx, Rect r, (float[] Full, float[] Low)? wave, Func<double, double> xOf, double duration,
        double audibleFrom, double audibleTo, bool center, Color tint, bool flip)
    {
        if (wave is not { } w || w.Full.Length == 0 || duration <= 0 || r.Width <= 2 || r.Height <= 2) return;
        int cols = (int)r.Width;
        var full = new float[cols]; var low = new float[cols]; var seen = new bool[cols];
        double perPoint = duration / w.Full.Length;
        for (int i = 0; i < w.Full.Length; i++)
        {
            double fxv = xOf(i * perPoint);
            if (!double.IsFinite(fxv)) continue;
            int c = (int)(fxv - r.Left);
            if (c < 0 || c >= cols) continue;
            seen[c] = true;
            full[c] = Math.Max(full[c], w.Full[i]); low[c] = Math.Max(low[c], w.Low[i]);
        }
        int lastSeen = -1;
        for (int c = 0; c < cols; c++) { if (seen[c]) lastSeen = c; else if (lastSeen >= 0 && c - lastSeen < 12) { full[c] = full[lastSeen]; low[c] = low[lastSeen]; seen[c] = true; } }
        static float[] Smooth(float[] v) { var o = new float[v.Length]; for (int c = 0; c < v.Length; c++) { float a = v[Math.Max(0, c - 1)], b = v[c], d = v[Math.Min(v.Length - 1, c + 1)]; o[c] = Math.Max(b * .7f, (a + b * 2 + d) / 4f); } return o; }
        full = Smooth(full); low = Smooth(low);
        int first = Array.IndexOf(seen, true), last = Array.LastIndexOf(seen, true);
        if (first < 0 || last <= first) return;

        Geometry Envelope(float[] v, double scale)
        {
            var topPts = new List<Point>(); var botPts = new List<Point>();
            double mid = r.Top + r.Height / 2;
            for (int c = first; c <= last; c += 2)
            {
                double x = r.Left + c, a = Math.Clamp(v[c], 0, 1) * scale;
                if (center) { double hgt = Math.Max(.6, a * (r.Height / 2 - 1)); topPts.Add(new Point(x, mid - hgt)); botPts.Add(new Point(x, mid + hgt)); }
                else if (flip) { double hgt = Math.Max(.6, a * (r.Height - 1)); topPts.Add(new Point(x, r.Bottom - hgt)); botPts.Add(new Point(x, r.Bottom)); }
                else { double hgt = Math.Max(.6, a * (r.Height - 1)); topPts.Add(new Point(x, r.Top)); botPts.Add(new Point(x, r.Top + hgt)); }
            }
            botPts.Reverse();
            return Polyline(topPts.Concat(botPts).ToList(), closed: true);
        }

        var fullPath = Envelope(full, 1);
        var lowPath = Envelope(low, .96);
        Color neutral = UiTheme.Mix(UiTheme.Text, tint, UiTheme.IsLight ? .25 : .18);
        IBrush Ramp(Color ink, double peak, double edge)
        {
            var (s0, s1, s2) = center ? (edge, peak, edge) : flip ? (edge, (edge + peak) / 2, peak) : (peak, (edge + peak) / 2, edge);
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(A(ink, s0), 0), new GradientStop(A(ink, s1), .5), new GradientStop(A(ink, s2), 1) },
            };
        }
        void Paint(double fullPeak, double fullEdge, double lowPeak, double lowEdge)
        {
            ctx.DrawGeometry(Ramp(neutral, fullPeak, fullEdge), null, fullPath);
            ctx.DrawGeometry(Ramp(tint, lowPeak, lowEdge), null, lowPath);
        }
        Paint(UiTheme.IsLight ? 44 : 50, 16, UiTheme.IsLight ? 64 : 66, 22);
        double a0 = Math.Max(r.Left, audibleFrom), a1 = Math.Min(r.Right, audibleTo);
        if (a1 > a0)
            using (ctx.PushClip(new Rect(new Point(a0, r.Top - 2), new Point(a1, r.Bottom + 2))))
                Paint(UiTheme.IsLight ? 150 : 170, UiTheme.IsLight ? 60 : 56, 250, UiTheme.IsLight ? 130 : 120);
    }

    private void DrawTrackRow(DrawingContext ctx, Rect row, Rect play, Bitmap? cover, string title, string artist, TrackMixProfile? profile, bool tail, bool playing, bool hot, double label, double small, double value)
    {
        DrawPlayGlyph(ctx, play, playing, hot, small: true);
        double side = row.Height - P(4);
        var art = new Rect(row.Left, row.Top + (row.Height - side) / 2, side, side);
        using (ctx.PushClip(new RoundedRect(art, P(6))))
        {
            if (cover != null)
            {
                double scale = Math.Max(art.Width / cover.Size.Width, art.Height / cover.Size.Height);
                var size = new Size(cover.Size.Width * scale, cover.Size.Height * scale);
                ctx.DrawImage(cover, new Rect(art.Center.X - size.Width / 2, art.Center.Y - size.Height / 2, size.Width, size.Height));
            }
            else
            {
                ctx.FillRectangle(B(UiTheme.Card), art);
                TextIn(ctx, title.Length > 0 ? title[..1].ToUpperInvariant() : "♪", side * .42, DisplaySemibold, A(UiTheme.Text, 150), art, TextAlignment.Center);
            }
        }
        double x = art.Right + P(14), midY = row.Top + row.Height / 2;
        // Colonne fisse, uguali nelle due righe: valore sopra, nome sotto, tutto a destra.
        var tempoCol = new Rect(play.Left - P(24) - TempoColumn, row.Top, TempoColumn, row.Height);
        var timeCol = new Rect(tempoCol.Left - P(20) - TimeColumn, row.Top, TimeColumn, row.Height);
        var bpmCol = new Rect(timeCol.Left - P(20) - BpmColumn, row.Top, BpmColumn, row.Height);
        var keyCol = new Rect(bpmCol.Left - P(20) - KeyColumn, row.Top, KeyColumn, row.Height);
        Rect Top(Rect c) => new(c.Left, midY - P(21), c.Width, P(22));
        Rect Bottom(Rect c) => new(c.Left, midY + P(1), c.Width, P(20));
        double textW = Math.Max(40, keyCol.Left - x - P(12));
        TextIn(ctx, title, label, UiSemibold, UiTheme.Text, new Rect(x, midY - P(21), textW, P(22)));
        if (artist.Length > 0) TextIn(ctx, artist, small, UiRegular, UiTheme.Muted, new Rect(x, midY + P(1), textW, P(20)));
        double duration = tail ? _fromDuration : _toDuration;
        double current = tail
            ? _heard.From ?? (MixingPreview ? _outAt + PreviewFade : _cursorA ?? _outAt)
            : _heard.To ?? (MixingPreview ? _inAt : _cursorB ?? _inAt);
        TextIn(ctx, TimeText(Math.Clamp(current, 0, duration)), value, UiBold, UiTheme.Text, Top(timeCol), TextAlignment.Right);
        TextIn(ctx, "di " + TimeText(duration), small, UiRegular, UiTheme.Muted, Bottom(timeCol), TextAlignment.Right);
        if (!tail) DrawTempo(ctx, tempoCol, value, small);
        else DrawFixedTempo(ctx, tempoCol, value, small);
        if (profile == null) return;
        var grid = tail ? profile.Tail : profile.Head;
        double speed = tail ? 1 : EffectiveSpeed;
        string bpm = grid.Reliable ? (grid.Bpm * speed).ToString("0.0", CultureInfo.CurrentCulture) : "—";
        TextIn(ctx, bpm, value, UiBold, UiTheme.Text, Top(bpmCol), TextAlignment.Right);
        TextIn(ctx, grid.Reliable ? "BPM" : "tempo libero", small, UiRegular, UiTheme.Muted, Bottom(bpmCol), TextAlignment.Right);
        string key = profile.Camelot;
        TextIn(ctx, key.Length > 0 ? key : "—", value, UiBold, key.Length > 0 ? KeyInk(profile.Key) : UiTheme.Muted, Top(keyCol), TextAlignment.Right);
        TextIn(ctx, "tonalità", small, UiRegular, UiTheme.Muted, Bottom(keyCol), TextAlignment.Right);
    }

    /// <summary>
    /// Tempo del brano che entra, come il pitch fader di un DJ: valore sopra (Auto o la variazione in %), cursore sotto con
    /// la tacca dello 0. Trascinare cambia il tempo, la rotella di 0,1%, il doppio clic torna ad Auto.
    /// </summary>
    private void DrawTempo(DrawingContext ctx, Rect col, double value, double small)
    {
        double midY = col.Top + col.Height / 2;
        bool hot = _hover == "tempoB" || _drag == "tempoB";
        double speed = EffectiveSpeed;
        string text = !_auto && _incomingSpeed != null ? Percent(speed) : "Auto";
        string note = !_auto && _incomingSpeed != null ? "tempo" : Math.Abs(speed - 1) >= .0005 ? "tempo · " + Percent(speed) : "tempo";
        double tw = Measure(text, value, UiBold);
        TextIn(ctx, text, value, UiBold, hot ? Hi : UiTheme.Text, new Rect(col.Left + P(8), midY - P(21), tw + 4, P(22)));
        TextIn(ctx, note, small, UiRegular, UiTheme.Muted, new Rect(col.Left + P(8) + tw + P(8), midY - P(20), col.Width - tw - P(24), P(22)));
        var track = _tempoTrack;
        double Xs(double v) => track.Left + (v - TempoMin) / (TempoMax - TempoMin) * track.Width;
        ctx.DrawRectangle(B(A(UiTheme.Text, UiTheme.IsLight ? 38 : 46)), null, track, track.Height / 2, track.Height / 2);
        double zero = Xs(1);
        ctx.DrawLine(new Pen(B(A(UiTheme.Text, 150)), Math.Max(1, K)), new Point(zero, track.Top - P(5)), new Point(zero, track.Bottom + P(5)));
        double knobX = Xs(Math.Clamp(speed, TempoMin, TempoMax));
        var fill = new Rect(Math.Min(zero, knobX), track.Top, Math.Abs(knobX - zero), track.Height);
        if (fill.Width > .5) ctx.DrawRectangle(B(hot ? Hi : A(UiTheme.Text, 200)), null, fill, track.Height / 2, track.Height / 2);
        double r = hot ? P(8) : P(7);
        ctx.DrawEllipse(B(UiTheme.IsLight ? Colors.White : UiTheme.Mix(Hi, Sheet, .1)), new Pen(B(A(UiTheme.Text, hot ? 200 : 110)), Math.Max(1, K)), new Point(knobX, track.Center.Y), r, r);
    }

    /// <summary>
    /// Il brano che finisce suona alla sua velocita': stessa colonna e stesso disegno di quella del brano che entra
    /// (valore e nota sulla stessa riga, cursore sotto alla stessa altezza), con il cursore spento fermo a 0.
    /// </summary>
    /// <summary>
    /// Velocita' del brano che finisce (solo durante il passaggio, poi il brano e' finito): stesso disegno e stessa
    /// colonna del cursore del brano che entra. Doppio clic: torna alla sua.
    /// </summary>
    private void DrawFixedTempo(DrawingContext ctx, Rect col, double value, double small)
    {
        double midY = col.Top + col.Height / 2;
        bool hot = _hover == "tempoA" || _drag == "tempoA";
        bool changed = Math.Abs(_outgoingSpeed - 1) >= .0005;
        string text = changed ? Percent(_outgoingSpeed) : "Originale";
        double tw = Measure(text, value, UiBold);
        TextIn(ctx, text, value, UiBold, hot ? Hi : UiTheme.Text, new Rect(col.Left + P(8), midY - P(21), tw + 4, P(22)));
        TextIn(ctx, "tempo nel passaggio", small, UiRegular, UiTheme.Muted, new Rect(col.Left + P(8) + tw + P(8), midY - P(20), col.Width - tw - P(24), P(22)));
        var track = _tempoTrackA = new Rect(col.Left + P(8), col.Top + col.Height * .72 - P(1), col.Width - P(16), P(2));
        _tempoA = col;
        double Xs(double v) => track.Left + (v - TempoMin) / (TempoMax - TempoMin) * track.Width;
        ctx.DrawRectangle(B(A(UiTheme.Text, UiTheme.IsLight ? 38 : 46)), null, track, track.Height / 2, track.Height / 2);
        double zero = Xs(1);
        ctx.DrawLine(new Pen(B(A(UiTheme.Text, 150)), Math.Max(1, K)), new Point(zero, track.Top - P(5)), new Point(zero, track.Bottom + P(5)));
        double knobX = Xs(Math.Clamp(_outgoingSpeed, TempoMin, TempoMax));
        var fill = new Rect(Math.Min(zero, knobX), track.Top, Math.Abs(knobX - zero), track.Height);
        if (fill.Width > .5) ctx.DrawRectangle(B(hot ? Hi : A(UiTheme.Text, 200)), null, fill, track.Height / 2, track.Height / 2);
        double r = hot ? P(8) : P(7);
        ctx.DrawEllipse(B(UiTheme.IsLight ? Colors.White : UiTheme.Mix(Hi, Sheet, .1)), new Pen(B(A(UiTheme.Text, hot ? 200 : 110)), Math.Max(1, K)), new Point(knobX, track.Center.Y), r, r);
    }

    private Rect _tempoTrackA;

    private void SetOutTempoFromX(double x)
    {
        double f = Math.Clamp((x - _tempoTrackA.Left) / Math.Max(1, _tempoTrackA.Width), 0, 1);
        double v = TempoMin + f * (TempoMax - TempoMin);
        if (Math.Abs(v - 1) < .004) v = 1;
        SetOutTempo(Math.Round(v, 3));
    }

    private void SetOutTempo(double speed)
    {
        bool restart = _preview.Playing;
        var mode = _preview.Mode;
        if (restart) StopPreview();
        _outgoingSpeed = Math.Clamp(speed, TempoMin, TempoMax);
        if (Math.Abs(_outgoingSpeed - 1) >= .002) { _auto = false; if (_vol == TransitionCurves.VolumeNone) _vol = TransitionCurves.VolumeFade; }
        _phaseJob++;
        if (restart && _drag == null) PlayPreview(mode);
        InvalidateVisual();
    }

    private Rect _tempoA;

    private static string Percent(double speed)
    {
        double pct = (speed - 1) * 100;
        return (pct >= 0 ? "+" : "−") + Math.Abs(pct).ToString("0.0", CultureInfo.CurrentCulture) + " %";
    }

    private void SetTempoFromX(double x)
    {
        double f = Math.Clamp((x - _tempoTrack.Left) / Math.Max(1, _tempoTrack.Width), 0, 1);
        double v = TempoMin + f * (TempoMax - TempoMin);
        if (Math.Abs(v - 1) < .004) v = 1; // lo 0 "aggancia" come la tacca centrale di un mixer
        SetTempo(Math.Round(v, 3));
    }

    private void SetTempo(double? speed)
    {
        bool restart = _preview.Playing;
        var mode = _preview.Mode;
        if (restart) StopPreview();
        _incomingSpeed = speed is double v ? Math.Clamp(v, TempoMin, TempoMax) : null;
        if (speed != null) { _auto = false; if (_vol == TransitionCurves.VolumeNone) _vol = TransitionCurves.VolumeFade; }
        _phaseJob++;
        if (restart && _drag == null) PlayPreview(mode);
        InvalidateVisual();
    }

    private static string TimeText(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    private static Color KeyInk(int key)
    {
        var c = KeyColor(key);
        return UiTheme.IsLight ? UiTheme.Mix(c, Colors.Black, .35) : c;
    }

    private static Color KeyColor(int key)
    {
        if (key < 0) return Colors.Gray;
        string camelot = KeyDetector.CamelotText(key);
        int number = int.TryParse(new string(camelot.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 1;
        return FromHsv((number - 1) / 12.0 * 360, 0.55, camelot.EndsWith("B") ? 0.98 : 0.86);
    }

    private void DrawPlayGlyph(DrawingContext ctx, Rect r, bool playing, bool hot, bool small)
    {
        Color ink = hot ? Hi : A(UiTheme.Text, 200);
        if (!small)
        {
            ctx.DrawEllipse(B(UiTheme.PrimaryFill(hot)), null, r.Center, r.Width / 2, r.Height / 2);
            ink = UiTheme.OnPrimary;
        }
        double cx = r.Center.X, cy = r.Center.Y, s = r.Width * (small ? .26 : .2);
        if (playing)
        {
            double rad = Math.Max(1, P(2));
            ctx.DrawRectangle(B(ink), null, new Rect(cx - s, cy - s, s * .75, s * 2), rad, rad);
            ctx.DrawRectangle(B(ink), null, new Rect(cx + s * .25, cy - s, s * .75, s * 2), rad, rad);
        }
        else
        {
            double o = small ? 0 : s * .12;
            ctx.DrawGeometry(B(ink), null, Polyline(new[] { new Point(cx - s * .7 + o, cy - s * 1.1), new Point(cx + s * 1.2 + o, cy), new Point(cx - s * .7 + o, cy + s * 1.1) }, closed: true));
        }
    }

    private void DrawEditor(DrawingContext ctx, double small)
    {
        if (!_ready)
        {
            TextIn(ctx, "Non riesco ad analizzare uno dei due brani", small, UiRegular, UiTheme.Muted, _editor, TextAlignment.Center);
            return;
        }
        double fx0 = X(0), fx1 = X(_vol == TransitionCurves.VolumeNone && !_auto ? 0.05 : _fade);
        var zone = new Rect(new Point(fx0, _laneA.Top - P(4)), new Point(Math.Max(fx0 + 1, fx1), _laneB.Bottom + P(4)));
        using (ctx.PushClip(new RoundedRect(_editor, P(12))))
        {
            double peak = UiTheme.IsLight ? 30 : 38, edge = UiTheme.IsLight ? 8 : 10;
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(A(Hi, edge), 0), new GradientStop(A(Hi, peak), .5), new GradientStop(A(Hi, edge), 1) },
            }, zone);
            // Battute del brano che esce: brevi tacche ai bordi delle corsie, piu' lunghe ogni quattro.
            if (_from?.Tail is { Reliable: true } grid && grid.Period > 0)
            {
                double bar = grid.Period * 4;
                double firstBar = grid.FirstBeat + Math.Ceiling((TrackTime(true, ViewStart) - grid.FirstBeat) / bar) * bar;
                var thin = new Pen(B(A(UiTheme.Text, UiTheme.IsLight ? 40 : 36)), Math.Max(1, K));
                var thick = new Pen(B(A(UiTheme.Text, UiTheme.IsLight ? 90 : 80)), Math.Max(1, 1.4 * K));
                for (double t = firstBar; t < TrackTime(true, ViewStart + ViewSpan); t += bar)
                {
                    double x = X(TimelineTime(true, t));
                    long barNumber = (long)Math.Round((t - grid.FirstBeat) / bar);
                    bool major = barNumber % 4 == 0;
                    double len = major ? P(10) : P(5);
                    ctx.DrawLine(major ? thick : thin, new Point(x, _laneA.Top), new Point(x, _laneA.Top + len));
                    ctx.DrawLine(major ? thick : thin, new Point(x, _laneB.Bottom - len), new Point(x, _laneB.Bottom));
                }
            }
            bool soloA = _preview.Playing && _preview.Mode == MixPreviewPlayer.Lane.From;
            bool soloB = _preview.Playing && _preview.Mode == MixPreviewPlayer.Lane.To;
            double speed = EffectiveSpeed;
            DrawWave(ctx, _laneA, _fromWave, t => X(TimelineTime(true, t)), _fromDuration, double.MinValue, soloA ? double.MaxValue : X(_fade), center: true, TintA, flip: false);
            DrawWave(ctx, _laneB, _toWave, t => X((t - _inAt) / speed), _toDuration, soloB ? double.MinValue : X(0), double.MaxValue, center: true, TintB, flip: false);
            DrawCurves(ctx);
            DrawLane(ctx, true); DrawLane(ctx, false);
            bool hotL = _hover == "edgeL" || _drag == "edgeL", hotR = _hover == "edgeR" || _drag == "edgeR";
            void Edge(double x, bool hot)
            {
                ctx.DrawLine(new Pen(B(A(Hi, hot ? 255 : 170)), hot ? Math.Max(1.6, 2 * K) : Math.Max(1, 1.2 * K)), new Point(x, zone.Top), new Point(x, zone.Bottom));
                foreach (var lane in new[] { _laneA, _laneB })
                    ctx.DrawRectangle(B(hot ? Hi : UiTheme.Mix(Hi, Sheet, .25)), null, new Rect(x - P(3), lane.Top + lane.Height / 2 - P(13), P(6), P(26)), P(3), P(3));
            }
            Edge(fx0, hotL); Edge(fx1, hotR);
            var cursorPen = new Pen(B(A(UiTheme.Text, 200)), Math.Max(1, 1.2 * K), DashStyle.Dot);
            if (!MixingPreview && _cursorA is double ca && _heard.From == null) { double x = X(TimelineTime(true, ca)); ctx.DrawLine(cursorPen, new Point(x, _laneA.Top), new Point(x, _laneA.Bottom)); }
            if (!MixingPreview && _cursorB is double cb && _heard.To == null) { double x = X(TimelineTime(false, cb)); ctx.DrawLine(cursorPen, new Point(x, _laneB.Top), new Point(x, _laneB.Bottom)); }
            void Playhead(Rect lane, double? seconds, bool outgoing)
            {
                if (seconds == null) return;
                double x = X(TimelineTime(outgoing, seconds.Value));
                if (x < lane.Left || x > lane.Right) return;
                var pen = new Pen(B(Hi), Math.Max(1.5, 2 * K));
                ctx.DrawLine(pen, new Point(x, lane.Top), new Point(x, lane.Bottom));
            }
            Playhead(_laneA, _heard.From, true);
            Playhead(_laneB, _heard.To, false);
        }
        DrawPlayGlyph(ctx, _playMix, _preview.Playing && _preview.Mode == MixPreviewPlayer.Lane.None, _hover == "playMix", small: false);
        DrawLaneChips(ctx);
        string length = _vol == TransitionCurves.VolumeNone && !_auto ? "Nessuna"
            : _from?.Tail is { Reliable: true } t2 && t2.Period > 0 ? string.Format(CultureInfo.CurrentCulture, "{0} battute", Math.Max(1, (int)Math.Round(_fade / (t2.Period * 4))))
            : _fade.ToString("0.0", CultureInfo.CurrentCulture) + " s";
        TextIn(ctx, length, small, UiSemibold, UiTheme.Muted, new Rect(_playMix.Right + P(12), _playMix.Top, P(100), _playMix.Height));
    }

    private void DrawCurves(DrawingContext ctx)
    {
        if (_vol == TransitionCurves.VolumeNone && !_auto) return;
        if (_editLane != null && LanesAvailable) return; // si vede solo la corsia che si sta disegnando
        var curves = Curves;
        DrawDeck(ctx, _laneA, curves.Out);
        DrawDeck(ctx, _laneB, curves.In);
    }

    private void DrawDeck(DrawingContext ctx, Rect lane, DeckCurves deck)
    {
        double top = lane.Top + P(8), bottom = lane.Bottom - P(8);
        double Y(double v) => bottom - Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1) * (bottom - top);
        double x0 = X(0), x1 = X(_fade);
        if (!double.IsFinite(x0) || !double.IsFinite(x1) || x1 - x0 < 2) return;
        const int steps = 96;
        List<Point> Points(Func<double, double> value)
        {
            var pts = new List<Point>(steps + 1);
            for (int i = 0; i <= steps; i++) { double p = i / (double)steps; pts.Add(new Point(x0 + (x1 - x0) * p, Y(value(p)))); }
            return pts;
        }
        if (deck.UsesFilters)
        {
            double Open(double p)
            {
                double hp = deck.HighPassHz.ValueAt(p), lp = deck.LowPassHz.ValueAt(p);
                double a = hp >= 20 ? 1 - Math.Log(hp / 20) / Math.Log(1000) : 1;
                double b = lp >= 20 && lp < 20000 ? Math.Log(lp / 20) / Math.Log(1000) : 1;
                return Math.Clamp(Math.Min(a, b), 0, 1);
            }
            ctx.DrawGeometry(null, new Pen(B(FilterInk), Math.Max(1.2, 1.4 * K), DashStyle.Dash, lineJoin: PenLineJoin.Round), Polyline(Points(Open)));
        }
        if (deck.UsesEq)
        {
            double Low(double p) => Math.Clamp((deck.LowDb.ValueAt(p) - TransitionCurves.BassKillDb) / -TransitionCurves.BassKillDb, 0, 1) * .9 + .05;
            ctx.DrawGeometry(null, new Pen(B(EqInk), Math.Max(1.2, 1.5 * K), DashStyle.Dot, lineJoin: PenLineJoin.Round), Polyline(Points(Low)));
        }
        var volume = Points(p => deck.Volume.ValueAt(p));
        var area = volume.Concat(new[] { new Point(x1, bottom), new Point(x0, bottom) }).ToList();
        ctx.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(A(VolumeInk, UiTheme.IsLight ? 46 : 56), 0), new GradientStop(A(VolumeInk, 0), 1) },
        }, null, Polyline(area, closed: true));
        var line = Polyline(volume);
        ctx.DrawGeometry(null, new Pen(B(A(VolumeInk, UiTheme.IsLight ? 40 : 60)), Math.Max(4, 7 * K), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        ctx.DrawGeometry(null, new Pen(B(VolumeInk), Math.Max(2, 2.6 * K), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }

    private int ActivePreset()
    {
        if (_auto) return 0;
        if (_lanes.Count > 0) return -1; // ritoccato a mano: nessun preset
        for (int i = 1; i < Presets.Length; i++)
            if (Presets[i].Vol == _vol && (_vol == TransitionCurves.VolumeNone || Presets[i].Eq == _eq && Presets[i].Filter == _filter && Presets[i].Fx == _fx)) return i;
        return -1;
    }

    // ------------------------------------------------------------------ input

    private string HitTest(Point p)
    {
        if (_confirm) return _confirmSave.Contains(p) ? "c-save" : _confirmDiscard.Contains(p) ? "c-discard" : _confirmKeep.Contains(p) ? "c-keep" : "c-outside";
        if (_helpOpen) return _close.Contains(p) ? "close" : _help.Contains(p) ? "help" : "helpbody";
        for (int i = 0; i < 4; i++) { if (_cardPrev[i].Contains(p)) return "prev" + i; if (_cardNext[i].Contains(p)) return "next" + i; }
        if (_close.Contains(p)) return "close";
        if (_help.Contains(p)) return "help";
        if (_tempoB.Contains(p)) return "tempoB";
        if (_tempoA.Contains(p)) return "tempoA";
        if (_playA.Contains(p)) return "playA";
        if (_playB.Contains(p)) return "playB";
        if (_playMix.Contains(p)) return "playMix";
        if (LaneHit(p) is { } laneHit) return laneHit;
        foreach (var (r, i) in _presetHits) if (r.Contains(p)) return "preset" + i;
        for (int i = 0; i < 4; i++) if (_cards[i].Contains(p)) return "card" + i;
        if (_overview.Contains(p)) return p.Y < _overview.Top + _overview.Height / 2 ? "overA" : "overB";
        if (_ready && !_preview.Playing && (_laneA.Contains(p) || _laneB.Contains(p)) && !(_vol == TransitionCurves.VolumeNone && !_auto))
        {
            if (Math.Abs(p.X - X(0)) <= 7) return "edgeL";
            if (Math.Abs(p.X - X(_fade)) <= 7) return "edgeR";
        }
        if (_laneA.Contains(p)) return "laneA";
        if (_laneB.Contains(p)) return "laneB";
        return "";
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_drag != null && _ready)
        {
            double dx = pos.X - _dragStart.X;
            if (Math.Abs(dx) > 3 || Math.Abs(pos.Y - _dragStart.Y) > 3) _dragMoved = true;
            if (_drag == "lanePoint") { _laneFree = e.KeyModifiers.HasFlag(KeyModifiers.Shift); LaneDragged(pos); return; }
            if (!_dragMoved) return;
            if (_preview.Playing && _drag is "laneA" or "laneB" or "overA" or "overB") return;
            double ds = dx * _dragScale;
            switch (_drag)
            {
                case "tempoB": SetTempoFromX(pos.X); return;
                case "tempoA": SetOutTempoFromX(pos.X); return;
                case "laneA": _outAt = Math.Clamp(_dragOut - ds, 0, Math.Max(0, _fromDuration - 0.5)); break;
                case "laneB": _inAt = Math.Clamp(_dragIn - ds * EffectiveSpeed, 0, Math.Max(0, _toDuration - 1)); break;
                case "edgeR": _fade = Math.Clamp(_dragFade + ds, 0.5, 60); break;
                case "edgeL":
                    double speed = EffectiveSpeed;
                    double shift = Math.Clamp(ds, -Math.Min(_dragOut, _dragIn / speed), _dragFade - 0.5);
                    _outAt = _dragOut + shift; _inAt = _dragIn + shift * speed; _fade = _dragFade - shift; break;
                case "overA": SetFromOverview(pos, true); break;
                case "overB": SetFromOverview(pos, false); break;
            }
            InvalidateVisual();
            return;
        }
        string hover = HitTest(pos);
        _mouse = pos;
        if (hover != _hover) { _hover = hover; InvalidateVisual(); }
        // Spiegazione dopo un po' fermi sopra un elemento; muovendosi sullo stesso, la si sposta senza ricominciare.
        if (hover != _tipKey)
        {
            _tipKey = hover; _tipSince = DateTime.UtcNow;
            if (TipFor(hover) != null) Avalonia.Threading.DispatcherTimer.RunOnce(() => { if (_tipKey == hover) InvalidateVisual(); }, TimeSpan.FromMilliseconds(TipDelayMs + 20));
        }
        else if (TipFor(hover) != null && (DateTime.UtcNow - _tipSince).TotalMilliseconds >= TipDelayMs) InvalidateVisual();
        Cursor = hover switch
        {
            "edgeL" or "edgeR" => new Cursor(StandardCursorType.SizeWestEast),
            "addA" or "addB" => new Cursor(StandardCursorType.Cross),
            "" or "helpbody" => Cursor.Default,
            _ => new Cursor(StandardCursorType.Hand),
        };
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _tipKey = ""; if (_hover != "") { _hover = ""; InvalidateVisual(); } }

    // ------------------------------------------------------------------ spiegazioni al passaggio del mouse
    private const int TipDelayMs = 650;
    private string _tipKey = "";
    private DateTime _tipSince;
    private Point _mouse;

    private static readonly string[] PresetTips =
    {
        "Sceglie Cinecore il passaggio adatto ai due brani, guardando tempo, tonalità e struttura (come l’automix).",
        "Il primo brano scende mentre il secondo sale; i bassi si danno il cambio a metà, così non si impastano.",
        "Il secondo brano entra con un filtro che si apre e si richiude, una spinta crescente; i bassi passano alla fine.",
        "Passaggio lungo e morbido: il primo brano perde i bassi (passa alto) mentre il secondo cresce piano.",
        "I due brani suonano insieme e il primo si chiude con un passa basso, come se si allontanasse.",
        "Mix a tre bande da DJ: bassi, medi e alti passano dal primo al secondo brano uno alla volta.",
        "Taglio netto al centro del passaggio: si salta da un brano all’altro sul battere.",
        "Nessun mix: il secondo brano parte quando il primo è finito.",
        "Costruzione: il primo brano ripete e accelera una battuta in crescendo, poi esplode nel secondo.",
        "Loop roll: l’ultima battuta del primo brano si ripete sempre più fitta, poi entra il secondo.",
        "Eco: il primo brano si interrompe e lascia un’eco a tempo che si spegne; il secondo parte subito.",
        "Riverbero: il primo brano si dissolve in un riverbero lungo mentre il secondo entra.",
        "Loop: una battuta del primo brano gira in loop mentre il secondo sale sotto.",
        "Gate: il primo brano viene spezzettato a tempo mentre sfuma, il secondo prende il posto.",
        "Riser: un fruscio che sale fino all’ingresso del secondo brano, come un build-up.",
        "Brake: il primo brano rallenta fino a fermarsi, come un giradischi frenato, poi parte il secondo.",
        "Spinback: il primo brano gira all’indietro come un disco riavvolto a mano, poi parte il secondo.",
    };
    private static readonly string[] CardTips =
    {
        "Volume: come si scambiano i volumi dei due brani durante il passaggio. Le frecce (o il clic destro) cambiano scelta.",
        "EQ: come passano i bassi (e le altre bande) da un brano all’altro, perché non suonino due bassi insieme.",
        "Filtro: un filtro che colora il passaggio, sul brano che esce o su quello che entra.",
        "Effetto: un effetto da DJ sul brano che esce (eco, loop, brake…).",
    };
    private static readonly string[][] CardValueTips =
    {
        new[] { "i due brani restano pieni insieme per buona parte del passaggio", "uno scende mentre l’altro sale", "salita e discesa costanti, senza curve", "il primo scende piano e il secondo sale piano", "a metà del passaggio si passa di colpo", "nessuna sovrapposizione" },
        new[] { "nessuna equalizzazione", "i bassi si danno il cambio a metà", "il brano che entra tiene i bassi spenti fino alla fine", "bassi, medi e alti passano uno alla volta" },
        new[] { "nessun filtro", "sul brano che entra si apre e si richiude", "toglie i bassi al brano che esce", "toglie gli alti al brano che esce, che sembra allontanarsi" },
        new[] { "nessun effetto", "il brano che esce lascia un’eco a tempo", "il brano che esce si perde in un riverbero", "una battuta del brano che esce gira in loop", "l’ultima battuta si ripete sempre più fitta", "il brano che esce rallenta fino a fermarsi", "il brano che esce gira all’indietro", "il brano che esce viene spezzettato a tempo", "un fruscio sale fino all’ingresso", "una battuta accelera in crescendo" },
    };

    private string? TipFor(string key)
    {
        if (LaneTip(key) is { } laneTip) return laneTip;
        if (key.StartsWith("preset", StringComparison.Ordinal) && int.TryParse(key.AsSpan(6), out int pi) && pi >= 0 && pi < PresetTips.Length)
            return PresetTips[pi];
        if ((key.StartsWith("card", StringComparison.Ordinal) || key.StartsWith("prev", StringComparison.Ordinal) || key.StartsWith("next", StringComparison.Ordinal)) &&
            int.TryParse(key.AsSpan(4), out int ci) && ci is >= 0 and < 4)
        {
            if (_auto) return CardTips[ci] + " Ora: Auto, sceglie Cinecore.";
            int value = ci switch { 0 => Math.Clamp(_vol, 0, 5), 1 => Math.Clamp(_eq, 0, 3), 2 => Math.Clamp(_filter, 0, 3), _ => Math.Clamp(_fx, 0, CardValueTips[3].Length - 1) };
            return CardTips[ci] + " Ora: " + CardValueTips[ci][Math.Min(value, CardValueTips[ci].Length - 1)] + ".";
        }
        return key switch
        {
            "overA" or "overB" => "Panoramica dei due brani interi (sopra quello che finisce, sotto quello che entra): clicca o trascina per scegliere dove si incontrano.",
            "laneA" => "Il brano che finisce: trascinalo per scegliere da quale punto esce. Le battute si agganciano da sole.",
            "laneB" => "Il brano che entra: trascinalo per scegliere da quale punto parte.",
            "edgeL" or "edgeR" => "Bordo del passaggio: tiralo per allungare o accorciare il mix (in battute).",
            "playMix" => "Ascolta il passaggio dall’inizio (Spazio).",
            "playA" => "Ascolta il brano che finisce dal punto scelto.",
            "playB" => "Ascolta il brano che entra dal punto scelto.",
            "tempoB" => "Velocità del brano che entra. Auto la aggancia al tempo del primo brano; trascina per sceglierla a mano, doppio clic torna ad Auto.",
            "tempoA" => "Velocità del brano che finisce, solo durante il passaggio: ci arriva in modo morbido dal punto del mix. Il brano che entra in Auto si aggancia al suo tempo. Doppio clic torna alla velocità originale.",
            "help" => "Come si fa un mix: la guida.",
            "close" => "Chiudi (Esc). Se hai cambiato qualcosa ti chiede se salvare.",
            _ => null,
        };
    }

    private void DrawTip(DrawingContext ctx)
    {
        if (_drag != null || (DateTime.UtcNow - _tipSince).TotalMilliseconds < TipDelayMs || TipFor(_tipKey) is not { } text) return;
        double size = P(12.5), maxW = P(320), pad = P(12);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, UiRegular, size, B(Colors.White)) { MaxTextWidth = maxW };
        double w = Math.Min(maxW, ft.WidthIncludingTrailingWhitespace) + pad * 2, h = ft.Height + pad * 1.5;
        double x = Math.Clamp(_mouse.X + P(14), P(8), Bounds.Width - w - P(8));
        double y = _mouse.Y + P(22);
        if (y + h > Bounds.Height - P(8)) y = _mouse.Y - h - P(12);
        var box = new Rect(x, y, w, h);
        ctx.DrawRectangle(B(Color.FromArgb(235, 22, 24, 30)), new Pen(B(Color.FromArgb(40, 255, 255, 255)), 1), box, P(9), P(9));
        ctx.DrawText(ft, new Point(x + pad, y + pad * .75));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        Focus();
        var pos = e.GetPosition(this);
        string hit = HitTest(pos);
        var kind = e.GetCurrentPoint(this).Properties;
        if (kind.IsRightButtonPressed && LanePressed(hit, pos, true, e.Pointer)) return;
        if (kind.IsRightButtonPressed && hit.StartsWith("card", StringComparison.Ordinal)) { CycleCard(hit[4] - '0', -1); return; }
        if (hit == "tempoB" && kind.IsLeftButtonPressed && e.ClickCount >= 2) { SetTempo(null); return; }
        if (hit == "tempoA" && kind.IsLeftButtonPressed && e.ClickCount >= 2) { SetOutTempo(1); return; }
        if (!kind.IsLeftButtonPressed || hit == "") return;
        switch (hit)
        {
            case "help": case "helpbody": _helpOpen = !_helpOpen; MixHelpStore.MarkSeen(); InvalidateVisual(); return;
            case "close": RequestClose(); return;
            case "c-save": _confirm = false; Commit(); return;
            case "c-discard": _confirm = false; Close(); return;
            case "c-keep": case "c-outside": _confirm = false; InvalidateVisual(); return;
            case "playA": TogglePreview(MixPreviewPlayer.Lane.From); return;
            case "playB": TogglePreview(MixPreviewPlayer.Lane.To); return;
            case "playMix": TogglePreview(MixPreviewPlayer.Lane.None); return;
            case "tempoA":
                _tempoResume = _preview.Playing ? _preview.Mode : null;
                _drag = "tempoA"; _dragStart = pos; _dragMoved = true;
                SetOutTempoFromX(pos.X);
                e.Pointer.Capture(this);
                return;
            case "tempoB":
                _tempoResume = _preview.Playing ? _preview.Mode : null;
                _drag = "tempoB"; _dragStart = pos; _dragMoved = true;
                SetTempoFromX(pos.X);
                e.Pointer.Capture(this);
                return;
        }
        // Curve disegnate a mano (voce 23 di Windows): Maiusc lascia il punto libero dalle battute.
        _laneFree = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (LanePressed(hit, pos, false, e.Pointer)) return;
        if (hit.StartsWith("preset", StringComparison.Ordinal) || hit.StartsWith("card", StringComparison.Ordinal) || hit.StartsWith("prev", StringComparison.Ordinal) || hit.StartsWith("next", StringComparison.Ordinal)) _userChanged = true;
        if (hit.StartsWith("preset", StringComparison.Ordinal)) { ApplyPreset(int.Parse(hit[6..], CultureInfo.InvariantCulture)); return; }
        if (hit.StartsWith("card", StringComparison.Ordinal)) { CycleCard(hit[4] - '0', 1); return; }
        if (hit.StartsWith("prev", StringComparison.Ordinal)) { CycleCard(hit[4] - '0', -1); return; }
        if (hit.StartsWith("next", StringComparison.Ordinal)) { CycleCard(hit[4] - '0', 1); return; }
        if (!_ready) return;
        _drag = hit; _dragStart = pos; _dragMoved = false;
        _dragOut = _outAt; _dragIn = _inAt; _dragFade = _fade;
        _dragScale = SecondsPerPixel; _dragViewStart = ViewStart;
        _phaseJob++;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == null) return;
        string drag = _drag; _drag = null;
        e.Pointer.Capture(null);
        if (drag == "lanePoint") { LaneReleased(); return; }
        if (drag is "tempoB" or "tempoA")
        {
            // Lasciato il cursore: l'anteprima che suonava riparte con il tempo nuovo e le casse si ricontrollano.
            if (_tempoResume is { } resume) { _tempoResume = null; PlayPreview(resume); }
            RefinePhaseAsync();
            InvalidateVisual();
            return;
        }
        var pos = e.GetPosition(this);
        bool outgoing = drag is "laneA" or "overA";
        bool browse = drag is "overA" or "overB";
        if (_preview.Playing && (browse || drag is "laneA" or "laneB"))
        {
            double time = browse ? OverviewTime(pos, outgoing) : TrackTime(outgoing, _dragViewStart + (pos.X - _editor.Left) * _dragScale);
            SeekPreview(outgoing, time);
            return;
        }
        if (browse) { SetFromOverview(pos, outgoing); _dragMoved = true; }
        if (!_dragMoved)
        {
            // Clic senza trascinare: da qui si ascolta quel brano da solo.
            if (drag == "laneA") _cursorA = Math.Clamp(TrackTime(true, Tau(pos.X)), 0, _fromDuration);
            if (drag == "laneB") _cursorB = Math.Clamp(TrackTime(false, Tau(pos.X)), 0, _toDuration);
            InvalidateVisual();
            return;
        }
        _auto = false;
        Snap(drag);
        if (outgoing) _cursorA = null;
        if (drag is "laneB" or "overB") _cursorB = null;
        FitView();
        InvalidateVisual();
        if (drag is "laneB" or "overB") RefinePhaseAsync();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = null;
    }

    private int _phaseJob;
    /// <summary>Con l'aggancio attivo: le casse dei due brani si controllano sul segnale e il brano nuovo si sposta di
    /// quanto serve (la griglia stimata puo' sbagliare di mezzo battito).</summary>
    private void RefinePhaseAsync()
    {
        if (!_snap || _from?.Tail is not { Reliable: true } tail || _to?.Head.Reliable != true) return;
        int job = ++_phaseJob;
        double outAt = _outAt, inAt = _inAt, period = tail.Period, fade = _fade;
        string from = _fromPath, to = _toPath;
        double speed = EffectiveSpeed;
        Task.Run(() => AutomixPhaseAligner.MeasureLag(from, to, outAt, inAt, period, speed, Math.Clamp(fade + 4, 8, 14))).ContinueWith(task =>
        {
            if (task.IsFaulted || task.Result is not double lag || Math.Abs(lag) < 0.02) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || job != _phaseJob || _outAt != outAt || _inAt != inAt) return;
                _inAt = Math.Clamp(_inAt + lag, 0, Math.Max(0, _toDuration - 1));
                InvalidateVisual();
            });
        });
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if (_tempoB.Contains(e.GetPosition(this)) && Math.Abs(e.Delta.Y) >= 0.01)
        {
            SetTempo(Math.Round(EffectiveSpeed + (e.Delta.Y > 0 ? .001 : -.001), 3));
            return;
        }
        if (!_editor.Contains(e.GetPosition(this)) || Math.Abs(e.Delta.Y) < 0.01) return;
        _margin = Math.Clamp(_margin * (e.Delta.Y > 0 ? 0.8 : 1.25), 2, 90);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Space: TogglePreview(MixPreviewPlayer.Lane.None); break;
            case Key.Enter when _confirm: _confirm = false; Commit(); break;
            case Key.Enter: RequestClose(); break;
            case Key.Escape when _confirm: _confirm = false; InvalidateVisual(); break;
            case Key.Escape when _helpOpen: _helpOpen = false; MixHelpStore.MarkSeen(); InvalidateVisual(); break;
            case Key.Escape: RequestClose(); break;
            default: e.Handled = false; break;
        }
    }

    private double OverviewTime(Point p, bool outgoing)
    {
        double fraction = Math.Clamp((p.X - _overview.Left) / Math.Max(1, _overview.Width), 0, 1);
        return fraction * (outgoing ? _fromDuration : _toDuration);
    }

    private void SetFromOverview(Point p, bool outgoing)
    {
        double time = OverviewTime(p, outgoing);
        if (outgoing) _outAt = Math.Clamp(time, 0, Math.Max(0, _fromDuration - .5));
        else _inAt = Math.Clamp(time, 0, Math.Max(0, _toDuration - 1));
    }

    private void ApplyPreset(int index)
    {
        StopPreview();
        var preset = Presets[index];
        _lanes.Clear();
        if (preset.Vol < 0) _auto = true;
        else { _auto = false; _vol = preset.Vol; _eq = preset.Eq; _filter = preset.Filter; _fx = preset.Fx; }
        InvalidateVisual();
    }

    private void CycleCard(int card, int step)
    {
        StopPreview();
        _auto = false;
        ClearLanes(card switch { 0 => "vol", 1 => "bass", 2 => "filter", _ => "echo" });
        switch (card)
        {
            case 0: _vol = (_vol + step + 6) % 6; break;
            case 1: if (_vol != TransitionCurves.VolumeNone) _eq = (_eq + step + 4) % 4; break;
            case 2: if (_vol != TransitionCurves.VolumeNone) _filter = (_filter + step + 4) % 4; break;
            case 3: if (_vol != TransitionCurves.VolumeNone) _fx = (_fx + step + TransitionCurves.FxCount) % TransitionCurves.FxCount; break;
        }
        InvalidateVisual();
    }

    /// <summary>Con battute affidabili: primo battito del brano nuovo sul battito del vecchio, durata in battute intere.</summary>
    private void Snap(string drag)
    {
        if (!_snap || _from == null || _to == null || !_from.Tail.Reliable || !_to.Head.Reliable) return;
        if (drag is "laneA" or "overA")
            _outAt = Math.Clamp(_from.Tail.NearestBeat(_outAt), 0, Math.Max(0, _fromDuration - .5));
        else if (drag is "laneB" or "overB")
            _inAt = Math.Clamp(_to.Head.NearestBeat(_inAt), 0, Math.Max(0, _toDuration - 1));
        double bar = _from.Tail.Period * 4;
        if (bar > 0 && drag is "edgeL" or "edgeR") _fade = Math.Clamp(Math.Round(_fade / bar) * bar, Math.Min(bar, 60), 60);
    }

    // ------------------------------------------------------------------ anteprima

    private void FollowPreview()
    {
        if (_drag != null) return;
        bool outgoing = _heard.To == null;
        double? seconds = outgoing ? _heard.From : _heard.To;
        if (seconds == null) return;
        double tau = TimelineTime(outgoing, seconds.Value);
        double left = ViewStart + ViewSpan * .14, right = ViewStart + ViewSpan * .76;
        if (tau < left) _viewOffset += tau - left;
        else if (tau > right) _viewOffset += tau - right;
    }

    private void RememberPreviewPosition()
    {
        if (_preview.Mode == MixPreviewPlayer.Lane.From && _heard.From is double a) _cursorA = Math.Clamp(a, 0, _fromDuration);
        if (_preview.Mode == MixPreviewPlayer.Lane.To && _heard.To is double b) _cursorB = Math.Clamp(b, 0, _toDuration);
        _heard = (null, null);
    }

    private void SeekPreview(bool outgoing, double position)
    {
        StopPreview();
        if (outgoing) _cursorA = Math.Clamp(position, 0, Math.Max(0, _fromDuration - .05));
        else _cursorB = Math.Clamp(position, 0, Math.Max(0, _toDuration - .05));
        PlayPreview(outgoing ? MixPreviewPlayer.Lane.From : MixPreviewPlayer.Lane.To);
    }

    private void TogglePreview(MixPreviewPlayer.Lane lane)
    {
        if (_preview.Playing && _preview.Mode == lane) { StopPreview(); return; }
        StopPreview();
        PlayPreview(lane);
    }

    private void PlayPreview(MixPreviewPlayer.Lane lane)
    {
        if (!_ready) return;
        _phaseJob++;
        _pauseMain?.Invoke();
        float volume = Math.Clamp(_volume(), 0, 1);
        bool ok;
        if (lane == MixPreviewPlayer.Lane.From)
            ok = _preview.PlaySolo(_fromPath, _cursorA is double a && a < _fromDuration - .05 ? a : _cursorA.HasValue ? 0 : Math.Max(0, _outAt - 6), lane, volume, 1);
        else if (lane == MixPreviewPlayer.Lane.To)
            ok = _preview.PlaySolo(_toPath, _cursorB is double b && b < _toDuration - .05 ? b : _cursorB.HasValue ? 0 : _inAt, lane, volume, EffectiveSpeed);
        else
        {
            _viewOffset = 0;
            double start = Math.Max(0, _outAt - 6);
            bool none = _vol == TransitionCurves.VolumeNone && !_auto;
            ok = _preview.PlayTransition(_fromPath, _toPath, start, _outAt, _inAt, PreviewFade, none ? null : Curves, volume, EffectiveSpeed, OutSpeed);
        }
        _previewError = ok ? null : _preview.LastError ?? "uscita audio";
        if (ok) { _heard = _preview.Position; FollowPreview(); _playhead.Start(); }
        InvalidateVisual();
    }

    private void StopPreview()
    {
        if (_preview.Playing) { _heard = _preview.Position; RememberPreviewPosition(); }
        _preview.Stop();
        _playhead.Stop();
        InvalidateVisual();
    }

    private void Commit()
    {
        StopPreview();
        if (_vol == TransitionCurves.VolumeNone) { _eq = 0; _filter = 0; _fx = 0; }
        var result = _auto ? null : new ManualMix(Math.Round(_outAt, 3), Math.Round(_inAt, 3), Math.Round(_fade, 2), _vol, _eq, _filter, _fx)
            { IncomingSpeed = _vol == TransitionCurves.VolumeNone ? null : _incomingSpeed,
              OutgoingSpeed = _vol == TransitionCurves.VolumeNone || Math.Abs(_outgoingSpeed - 1) < .002 ? null : _outgoingSpeed,
              Lanes = _vol == TransitionCurves.VolumeNone ? null : LanesForSave() };
        string from = _fromPath, to = _toPath;
        Close();
        Saved?.Invoke(from, to, result);
    }

    // ------------------------------------------------------------------ prove
    internal string QaState => $"pronto={_ready} out={_outAt:0.00} in={_inAt:0.00} fade={_fade:0.00} auto={_auto} vol={_vol} eq={_eq} filtro={_filter} fx={_fx} corsie={string.Join(",", _lanes.Keys)} anteprima={_preview.Playing}/{_preview.Mode} A={_heard.From:0.00}/{_fromDuration:0.00} B={_heard.To:0.00}/{_toDuration:0.00} vista={ViewStart:0.00} tempo={EffectiveSpeed:0.000}{(_incomingSpeed == null ? " auto" : "")} errore={_previewError}";
    internal void QaPreset(int index) { _helpOpen = false; ApplyPreset(index); }
    internal void QaTip(string key, double x, double y) { _tipKey = key; _hover = key; _mouse = new Point(x, y); _tipSince = DateTime.UtcNow.AddSeconds(-2); InvalidateVisual(); }
    internal void QaClose() => RequestClose();
    internal void QaPreview(string lane) => TogglePreview(lane == "a" ? MixPreviewPlayer.Lane.From : lane == "b" ? MixPreviewPlayer.Lane.To : MixPreviewPlayer.Lane.None);
    internal void QaSpeed(string value) => SetTempo(value == "auto" ? null : double.Parse(value, CultureInfo.InvariantCulture));
    internal void QaOverview(string lane, double fraction)
    {
        bool outgoing = lane == "a";
        var p = new Point(_overview.Left + _overview.Width * fraction, _overview.Center.Y);
        if (_preview.Playing) SeekPreview(outgoing, OverviewTime(p, outgoing));
        else { SetFromOverview(p, outgoing); FitView(); InvalidateVisual(); }
    }
    internal void QaSave() => Commit();
    internal void QaOutSpeed(string value) => SetOutTempo(double.Parse(value, CultureInfo.InvariantCulture));
}
