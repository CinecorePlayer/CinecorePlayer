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
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Scheda "Sincronizzazione audio" (ShowAudioSynchronization di Windows), per le tracce interne del file:
/// riferimento, traccia da allineare, ritardo (si sente subito; Annulla rimette quello di prima), "Trova il ritardo
/// da solo" (ExternalAudioSynchronization condiviso: confronta musica ed effetti di piu' scene, anche a tratti lungo il
/// film) e Applica, che lo salva per il film. L'analisi continua anche a scheda chiusa e avvisa alla fine.
/// Non ancora sul Mac: traccia audio esterna e conversione di velocita' (caricano un file nel motore video).
/// </summary>
internal sealed class AudioSyncView : Control
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>Anteprima dal vivo del ritardo (ms gia' calcolati per il punto del film).</summary>
    public Action<int>? PreviewDelay;
    /// <summary>Applica: binding da salvare e traccia da attivare (ordinale fra le tracce audio).</summary>
    public Action<ExternalAudioBinding, int>? Apply;
    /// <summary>Chiusura senza Applica: si rimette il ritardo salvato.</summary>
    public Action? Cancelled;
    /// <summary>Esito di un'analisi finita a scheda chiusa (film, binding proposto, affidabile).</summary>
    public Action<string, ExternalAudioBinding, int, bool, string>? FinishedInBackground;
    public Func<double>? PositionProvider;
    public event Action? Closed;

    private string _video = "";
    private List<string> _tracks = new();
    private int _reference, _target, _initialTarget, _delay;
    private double _duration;
    private double[]? _curveTimes;
    private int[]? _curveOffsets;
    private ExternalAudioBinding _binding = new(null);
    /// <summary>Traccia in un file a parte (doppiaggio, commento...): quella da allineare al posto di una interna.</summary>
    private string? _external;
    private ExternalAudioSynchronization.Result? _proposal;
    private string _status = "";
    private string _typing = "";
    private bool _applied;
    private Point _mouse;
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private readonly List<(Rect Rect, string Kind)> _hits = new();
    private DateTime _openedAt;
    private DispatcherTimer? _jobTimer;

    // L'analisi appartiene al player, non alla scheda: chiudendo la scheda continua.
    private sealed class Job
    {
        public string Video = "";
        public int Reference, Target;
        public string? External, ConvertedPath;
        public bool Conversion;
        public readonly CancellationTokenSource Cancel = new();
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public ExternalAudioSynchronization.Step Step;
        public ExternalAudioSynchronization.Result? Result;
        public string? Error;
        public bool Done, Cancelled, Consumed;
    }
    private static Job? _job;

    public AudioSyncView() { IsVisible = false; Focusable = true; ClipToBounds = true; }

    public bool IsOpen => IsVisible;

    private bool HasCurve => new ExternalAudioBinding(null, _delay, CurveTimes: _curveTimes, CurveOffsets: _curveOffsets).HasCurve;

    public void Open(string video, IReadOnlyList<string> tracks, int selectedOrdinal, double duration)
    {
        _video = video; _tracks = tracks.ToList(); _duration = duration;
        _binding = ExternalAudioStore.Get(video) ?? new ExternalAudioBinding(null);
        _external = _binding.AudioPath != null && File.Exists(_binding.AudioPath) ? _binding.AudioPath : null;
        if (_binding.AudioPath != null && _external == null) _binding = new ExternalAudioBinding(null);
        int last = Math.Max(0, _tracks.Count - 1);
        _target = Math.Clamp(_binding.TargetOrdinal ?? Math.Max(0, selectedOrdinal), 0, last);
        _initialTarget = Math.Clamp(Math.Max(0, selectedOrdinal), 0, last);
        _reference = Math.Clamp(_binding.ReferenceOrdinal, 0, last);
        if (_tracks.Count > 1 && _reference == _target) _reference = (_target + 1) % _tracks.Count;
        _delay = _binding.DelayMs; _curveTimes = _binding.CurveTimes; _curveOffsets = _binding.CurveOffsets;
        _proposal = null; _applied = false; _typing = "";
        _status = !File.Exists(video) ? "Analisi automatica: solo per i file sul Mac. Il ritardo manuale funziona."
            : _tracks.Count < 2 && _external == null ? "Una sola traccia audio: si regola a mano, o scegli un file audio esterno." : "";
        if (_job is { Consumed: false, Cancelled: false } pending && pending.Video == video)
        {
            _reference = Math.Clamp(pending.Reference, 0, last);
            _target = Math.Clamp(pending.Target, 0, last);
            _external = pending.External;
            StartJobTimer();
        }
        _openedAt = DateTime.UtcNow;
        IsVisible = true; Focus();
        InvalidateVisual();
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        _jobTimer?.Stop();
        if (!_applied) Cancelled?.Invoke();
        if (_job is { Done: false }) _status = "";
        Closed?.Invoke();
    }

    private bool CanPreviewLive => _external == null ? _target == _initialTarget : _external == _binding.AudioPath;

    private void SetDelay(int value)
    {
        _delay = Math.Clamp(value, -600000, 600000);
        if (CanPreviewLive)
            PreviewDelay?.Invoke(new ExternalAudioBinding(null, _delay, CurveTimes: _curveTimes, CurveOffsets: _curveOffsets).DelayAt(PositionProvider?.Invoke() ?? 0));
        InvalidateVisual();
    }

    private void InvalidateProposal()
    {
        _proposal = null; _curveTimes = null; _curveOffsets = null;
        SetDelay(_delay);
    }

    private string StatusNow()
    {
        if (_proposal == null)
            return ExternalAudioStore.Get(_video) is { } saved && saved.DelayMs == _delay && saved.AudioPath == _external ? saved.Status : SyncStatus.Manual;
        if (Math.Abs(_proposal.SpeedRatio - 1) > 1e-6) return SyncStatus.Speed;
        if (!_proposal.Reliable) return _delay == _proposal.CorrectionMs ? SyncStatus.Uncertain : SyncStatus.Manual;
        return _delay == _proposal.CorrectionMs ? SyncStatus.Verified : SyncStatus.Manual;
    }

    private ExternalAudioBinding Pending() => new(_external, _delay, _reference, _external != null ? 0 : _target, StatusNow(),
        _proposal?.Confidence ?? _binding.Confidence, _proposal != null ? DateTime.UtcNow : _binding.CheckedUtc, _curveTimes, _curveOffsets,
        SubtitleNameNormalizer.TryDetectLanguageKey(_external != null ? Path.GetFileNameWithoutExtension(_external) : _tracks.ElementAtOrDefault(_target) ?? ""));

    private void DoApply()
    {
        if (!CommitTyping()) return;
        _applied = true;
        Apply?.Invoke(Pending(), _target);
        Close();
    }

    // ------------------------------------------------------------------ analisi
    private void StartAnalysis()
    {
        if (_job is { Done: false } || !CommitTyping()) return;
        if (!File.Exists(_video)) { _status = "L’analisi automatica richiede file sul Mac."; InvalidateVisual(); return; }
        if (_external == null && (_tracks.Count < 2 || _reference == _target)) { _status = "Scegli due tracce diverse."; InvalidateVisual(); return; }
        if (_tracks.Count == 0) { _status = "Il film non ha una traccia audio di riferimento."; InvalidateVisual(); return; }
        try { _job?.Cancel.Cancel(); } catch { }
        var job = new Job { Video = _video, Reference = _reference, Target = _target, External = _external };
        _job = job;
        var progress = new Progress<ExternalAudioSynchronization.Step>(step => job.Step = step);
        string video = _video; int reference = _reference, target = _target; double duration = _duration;
        string? external = _external;
        _ = Task.Run(async () =>
        {
            try { job.Result = await ExternalAudioSynchronization.AnalyzeAsync(video, external ?? video, reference, duration, false, progress, job.Cancel.Token, external != null ? 0 : target); }
            catch (OperationCanceledException) { job.Cancelled = true; }
            catch (Exception ex) { job.Error = ex.Message; Console.Error.WriteLine("[AUDIOSYNC] " + ex.Message); }
            job.Done = true; job.Clock.Stop();
            Dispatcher.UIThread.Post(() => JobFinished(job));
        });
        StartJobTimer();
    }

    /// <summary>Velocita' diversa (es. 25 contro 23,976): copia della traccia alla velocita' del video, poi nuova analisi.</summary>
    private void StartConversion()
    {
        if (_job is { Done: false } || _proposal is not { SpeedReliable: true } proposal) return;
        try { _job?.Cancel.Cancel(); } catch { }
        var job = new Job { Video = _video, Reference = _reference, Target = _target, External = _external, Conversion = true };
        _job = job;
        var progress = new Progress<ExternalAudioSynchronization.Step>(step => job.Step = step);
        string source = _external ?? _video; int ordinal = _external != null ? 0 : _target; double ratio = proposal.SpeedRatio;
        _ = Task.Run(async () =>
        {
            try { job.ConvertedPath = await ExternalAudioSynchronization.ConvertSpeedAsync(source, ordinal, ratio, progress, job.Cancel.Token); }
            catch (OperationCanceledException) { job.Cancelled = true; }
            catch (Exception ex) { job.Error = ex.Message; Console.Error.WriteLine("[AUDIOSYNC] conversione: " + ex.Message); }
            job.Done = true; job.Clock.Stop();
            Dispatcher.UIThread.Post(() => JobFinished(job));
        });
        StartJobTimer();
    }

    private async void ChooseExternal()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Traccia audio esterna", AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Audio") { Patterns = new[] { "*.ac3", "*.eac3", "*.dts", "*.thd", "*.mka", "*.flac", "*.wav", "*.m4a", "*.aac", "*.mp3", "*.opus", "*.ogg", "*.mkv", "*.mp4" } } },
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            _external = path;
            _proposal = null; _curveTimes = null; _curveOffsets = null;
            _status = "File scelto: premi “Trova il ritardo da solo” o regola a mano.";
            InvalidateVisual();
        }
        catch (Exception ex) { _status = "File non aperto: " + ex.Message; InvalidateVisual(); }
    }

    private void StartJobTimer()
    {
        _jobTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => InvalidateVisual());
        _jobTimer.Start();
        InvalidateVisual();
    }

    private static string Minutes(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private void JobFinished(Job job)
    {
        if (!ReferenceEquals(job, _job) || job.Cancelled && !IsVisible) return;
        if (!IsVisible || job.Video != _video)
        {
            // Scheda chiusa: l'esito arriva a schermo (e si applica se affidabile e non serve altro).
            if (job.Conversion) { FinishedInBackground?.Invoke(job.Video, new ExternalAudioBinding(null), job.Target, false, job.ConvertedPath != null ? "Traccia convertita: apri Sincronizzazione audio" : "Conversione della traccia non riuscita"); return; }
            if (job.Error != null || job.Result == null) { FinishedInBackground?.Invoke(job.Video, new ExternalAudioBinding(null), job.Target, false, "Analisi audio non riuscita"); return; }
            var r = job.Result;
            job.Consumed = true;
            var binding = new ExternalAudioBinding(job.External, r.CorrectionMs, job.Reference, job.Target, SyncStatus.Verified, r.Confidence, DateTime.UtcNow,
                r.Curve?.Select(p => p.Time).ToArray(), r.Curve?.Select(p => p.Ms - r.CorrectionMs).ToArray());
            bool speed = r.SpeedReliable && Math.Abs(r.SpeedRatio - 1) > 1e-6;
            FinishedInBackground?.Invoke(job.Video, binding, job.Target, r.Reliable && !speed,
                speed ? "Analisi audio: velocità diversa, serve una copia convertita"
                : r.Reliable ? (binding.HasCurve ? "Audio allineato: ritardo variabile, seguito durante la visione" : $"Audio allineato: {r.CorrectionMs:+0;-0;0} ms")
                : "Analisi audio: risultato incerto, apri Sincronizzazione audio");
            return;
        }
        _jobTimer?.Stop();
        job.Consumed = true;
        string took = Minutes(job.Clock.Elapsed.TotalSeconds);
        if (job.Cancelled) { _status = "Analisi fermata."; InvalidateVisual(); return; }
        if (job.Conversion)
        {
            if (job.Error != null || job.ConvertedPath == null) { _status = "Conversione non riuscita: " + (job.Error ?? "?"); InvalidateVisual(); return; }
            // La copia convertita diventa la traccia da allineare; un'analisi nuova trova il ritardo rimasto.
            _external = job.ConvertedPath;
            _proposal = null;
            StartAnalysis();
            return;
        }
        if (job.Error != null || job.Result == null) { _status = "Analisi non riuscita: " + (job.Error ?? "?"); InvalidateVisual(); return; }
        _proposal = job.Result;
        if (_proposal.SpeedReliable && Math.Abs(_proposal.SpeedRatio - 1) > 1e-6)
        {
            string name = ExternalAudioSynchronization.SpeedName(_proposal.SpeedRatio) is { } known ? " (" + known + ")" : "";
            _status = $"Velocità diversa: {Math.Abs(_proposal.SpeedRatio - 1) * 100:0.00}%{name}. Premi “Converti la traccia”.";
            InvalidateVisual();
            return;
        }
        _curveTimes = _proposal.Curve?.Select(p => p.Time).ToArray();
        _curveOffsets = _proposal.Curve?.Select(p => p.Ms - _proposal.CorrectionMs).ToArray();
        _status = _proposal.Reliable ? $"Analisi completata in {took}. Premi Applica." : "Risultato incerto. Controlla il ritardo prima di applicarlo.";
        SetDelay(_proposal.CorrectionMs);
    }

    // ------------------------------------------------------------------ disegno (come la scheda dei sottotitoli)
    private static double P(double pt) => pt * 96 / 72 * 0.92;
    private static IBrush B(Color c) => new SolidColorBrush(c);

    private static void Line(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, P(pt), color, r.Width, 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private void Chooser(DrawingContext ctx, Rect r, string text, string key, bool enabled)
    {
        ctx.DrawRectangle(B(UiTheme.WithAlpha(UiTheme.Text, enabled ? 18 : 10)), null, r, 14, 14);
        Line(ctx, text, 10, Regular, UiTheme.WithAlpha(UiTheme.Text, enabled ? 255 : 120), new Rect(r.X + 18, r.Y, r.Width - 18 - 96, r.Height));
        if (!enabled) return;
        foreach (var (side, dir) in new[] { (new Rect(r.Right - 88, r.Y, 44, r.Height), -1), (new Rect(r.Right - 44, r.Y, 44, r.Height), 1) })
        {
            double cx = side.Center.X, cy = side.Center.Y, z = 5;
            var pts = dir > 0 ? new[] { new Point(cx - z / 2, cy - z), new Point(cx + z / 2, cy), new Point(cx - z / 2, cy + z) }
                              : new[] { new Point(cx + z / 2, cy - z), new Point(cx - z / 2, cy), new Point(cx + z / 2, cy + z) };
            ctx.DrawGeometry(null, new Pen(B(UiTheme.WithAlpha(UiTheme.Text, Hot(side) ? 255 : 160)), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), new PolylineGeometry(pts, false));
            _hits.Add((side, key + (dir < 0 ? "-prev" : "-next")));
        }
    }

    private void Button(DrawingContext ctx, Rect r, string text, string kind, bool primary = false, bool enabled = true)
    {
        bool hot = enabled && Hot(r);
        Color fill = primary ? UiTheme.PrimaryFill(hot) : UiTheme.WithAlpha(UiTheme.Text, hot ? 40 : 24);
        ctx.DrawRectangle(B(enabled ? fill : UiTheme.WithAlpha(UiTheme.Text, 12)), null, r, r.Height / 2, r.Height / 2);
        Line(ctx, text, 9.5, Semibold, primary && enabled ? UiTheme.OnPrimary : UiTheme.WithAlpha(UiTheme.Text, enabled ? 255 : 110), r, TextAlignment.Center);
        if (enabled) _hits.Add((r, kind));
    }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        _hits.Clear();
        var page = new Rect(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 170, 0, 1);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        using var fade = ctx.PushOpacity(t);
        WizardSheet.DrawVeil(ctx, page, 1);
        _hits.Add((page, "outside"));
        const double W = 492, Pad = 36, Content = W - 2 * Pad;
        var job = _job is { Consumed: false } j && j.Video == _video ? j : null;
        bool running = job is { Done: false };
        double h = 560;
        var sheet = new Rect(Math.Round((page.Width - W) / 2), Math.Round((page.Height - h) / 2), W, h);
        // Stile delle schede nuove: niente riquadro, il contenuto sul velo, ingrandito con la finestra.
        WizardSheet.Fit(this, page.Size, new Size(W, h));
        _hits.Add((sheet, "sheet"));
        double L = sheet.X + Pad, T = sheet.Y;

        // Come le schede nuove (WizardSheet): titolo grande a sinistra con una riga di spiegazione, "Annulla" in alto a
        // destra solo testo, il pulsante principale a pillola in basso a sinistra.
        Line(ctx, "Sincronizzazione audio", 21, Semibold, UiTheme.Text, new Rect(L, T + 14, Content - 90, 44));
        Line(ctx, "Allinea una traccia al film, a mano o trovando il ritardo da solo.", 9.6, Regular, UiTheme.WithAlpha(UiTheme.Text, 158), new Rect(L, T + 56, Content, 22));
        var close = new Rect(sheet.Right - Pad - 80, T + 20, 80, 28);
        WizardSheet.DrawCancel(ctx, close, Hot(close), .75);
        _hits.Add((close, "cancel"));

        string Name(int i) => i >= 0 && i < _tracks.Count ? _tracks[i] : "—";
        bool choose = !running && _tracks.Count > 1;
        Line(ctx, "Riferimento audio", 9.5, Regular, UiTheme.Muted, new Rect(L, T + 100, Content, 24));
        Chooser(ctx, new Rect(L, T + 126, Content, 48), Name(_reference), "ref", choose);
        Line(ctx, "Traccia da allineare", 9.5, Regular, UiTheme.Muted, new Rect(L, T + 204, Content, 24));
        if (_external != null) Chooser(ctx, new Rect(L, T + 230, Content, 48), "File: " + Path.GetFileName(_external), "target", false);
        else Chooser(ctx, new Rect(L, T + 230, Content, 48), Name(_target), "target", !running && _tracks.Count > 1);
        if (!running)
        {
            string text = _external != null ? "Usa una traccia del film" : "File audio esterno…";
            var link = new Rect(L + Content - 200, T + 204, 200, 24);
            Line(ctx, text, 9.5, Semibold, Hot(link) ? UiTheme.Text : UiTheme.WithAlpha(UiTheme.Text, 170), link, TextAlignment.Right);
            _hits.Add((link, _external != null ? "internal" : "external"));
        }

        // Ritardo: valore centrale con unita' esterna, - e + ai lati (frecce: 10 ms, Maiusc: 100 ms).
        Line(ctx, "Ritardo audio", 9.5, Regular, UiTheme.Muted, new Rect(L, T + 308, Content, 24));
        bool variable = HasCurve;
        var box = new Rect(sheet.X + (W - 160) / 2, T + 340, 160, 54);
        ctx.DrawRectangle(B(UiTheme.WithAlpha(UiTheme.Text, _typing.Length > 0 ? 26 : 14)), _typing.Length > 0 ? new Pen(B(UiTheme.WithAlpha(UiTheme.Text, 46)), 1) : null, box, 14, 14);
        string value = variable ? "Variabile" : _typing.Length > 0 ? _typing : _delay.ToString("+0;-0;0", CultureInfo.InvariantCulture);
        Line(ctx, value, variable ? 14 : 18, Regular, UiTheme.Text, variable ? box : new Rect(box.X, box.Y, box.Width - 34, box.Height), TextAlignment.Center);
        if (!variable) Line(ctx, "ms", 10, Regular, UiTheme.Muted, new Rect(box.Right - 40, box.Y, 34, box.Height));
        if (!running)
        {
            Button(ctx, new Rect(box.X - 56, box.Y + 9, 40, 36), "−", "minus");
            Button(ctx, new Rect(box.Right + 16, box.Y + 9, 40, 36), "+", "plus");
        }
        Line(ctx, variable ? "Il ritardo cambia lungo il film: lo segue da solo." : _delay > 0 ? $"L’audio arriva {_delay} ms più tardi." : _delay < 0 ? $"L’audio arriva {-_delay} ms prima." : "Sincronizzazione originale del file.",
            8.6, Regular, UiTheme.Muted, new Rect(L, T + 398, Content, 20), TextAlignment.Center);

        // Analisi automatica.
        var action = new Rect(L, T + 428, Content, 34);
        if (running)
        {
            double fraction = Math.Clamp(job!.Step.Fraction, 0, 1), elapsed = job.Clock.Elapsed.TotalSeconds;
            string left = fraction >= 0.1 && fraction < 1 ? $", circa {Minutes(elapsed * (1 - fraction) / fraction)} rimanenti" : "";
            _status = $"{fraction * 100:0}%, {job.Step.Text}, {Minutes(elapsed)}{left}";
            ctx.FillRectangle(B(UiTheme.Border), new Rect(L, action.Y - 6, Content, 3));
            ctx.FillRectangle(B(UiTheme.Text), new Rect(L, action.Y - 6, Content * fraction, 3));
            Button(ctx, action, "Ferma", "stop");
        }
        else if (_proposal is { SpeedReliable: true } speedy && Math.Abs(speedy.SpeedRatio - 1) > 1e-6)
            Button(ctx, action, "Converti la traccia", "convert", primary: true);
        else Button(ctx, action, "Trova il ritardo da solo", "analyze", enabled: File.Exists(_video) && (_external != null ? _tracks.Count > 0 : _tracks.Count > 1));
        Line(ctx, _status, 8.8, Regular, UiTheme.SubtleText, new Rect(L, T + 468, Content, 22));

        Button(ctx, new Rect(L, T + 500, 128, 42), "Applica", "apply", primary: true, enabled: !running);
    }

    // ------------------------------------------------------------------ input
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        Focus();
        var hit = _hits.LastOrDefault(x => x.Rect.Contains(e.GetPosition(this)));
        int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 100 : 10;
        switch (hit.Kind)
        {
            case "outside": case "cancel": Close(); break;
            case "apply": DoApply(); break;
            case "analyze": StartAnalysis(); break;
            case "convert": StartConversion(); break;
            case "external": ChooseExternal(); break;
            case "internal": _external = null; InvalidateProposal(); _status = ""; break;
            case "stop": try { _job?.Cancel.Cancel(); } catch { } break;
            case "minus": CommitTyping(); _curveTimes = null; _curveOffsets = null; SetDelay(_delay - step); break;
            case "plus": CommitTyping(); _curveTimes = null; _curveOffsets = null; SetDelay(_delay + step); break;
            case "ref-prev": _reference = Step(_reference, -1, _target); InvalidateProposal(); break;
            case "ref-next": _reference = Step(_reference, 1, _target); InvalidateProposal(); break;
            case "target-prev": _target = Step(_target, -1, _reference); InvalidateProposal(); break;
            case "target-next": _target = Step(_target, 1, _reference); InvalidateProposal(); break;
        }
        InvalidateVisual();
    }

    private int Step(int value, int direction, int avoid)
    {
        int n = _tracks.Count;
        if (n == 0) return 0;
        int next = ((value + direction) % n + n) % n;
        if (next == avoid && n > 2) next = ((next + direction) % n + n) % n;
        return next;
    }

    private bool CommitTyping()
    {
        if (_typing.Length == 0) return true;
        string raw = _typing; _typing = "";
        if (raw is "-" or "+") return true;
        if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) || Math.Abs((long)value) > 600000)
        { _status = "Inserisci un ritardo fra −600000 e +600000 ms."; InvalidateVisual(); return false; }
        _curveTimes = null; _curveOffsets = null;
        SetDelay(value);
        return true;
    }

    /// <summary>Esc chiude, Invio applica, frecce ±10 ms (Maiusc ±100), cifre e segno scrivono il ritardo.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        bool running = _job is { Done: false, Consumed: false } j && j.Video == _video;
        int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 100 : 10;
        switch (e.Key)
        {
            case Key.Escape: if (_typing.Length > 0) _typing = ""; else Close(); break;
            case Key.Enter: if (!running) DoApply(); break;
            case Key.Up: case Key.Right: if (!running && CommitTyping()) { _curveTimes = null; _curveOffsets = null; SetDelay(_delay + step); } break;
            case Key.Down: case Key.Left: if (!running && CommitTyping()) { _curveTimes = null; _curveOffsets = null; SetDelay(_delay - step); } break;
            case Key.Back: if (_typing.Length > 0) _typing = _typing[..^1]; break;
            case Key.OemMinus: case Key.Subtract: if (!running && _typing.Length == 0) _typing = "-"; break;
            default:
                if (!running && e.Key >= Key.D0 && e.Key <= Key.D9 && _typing.Length < 8) _typing += (char)('0' + (e.Key - Key.D0));
                else if (!running && e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 && _typing.Length < 8) _typing += (char)('0' + (e.Key - Key.NumPad0));
                else return;
                break;
        }
        e.Handled = true;
        InvalidateVisual();
    }

    internal string QaSummary => $"ext={(_external == null ? "-" : Path.GetFileName(_external))} speed={_proposal?.SpeedRatio:0.#####} ref={_reference} target={_target} delay={_delay} curve={HasCurve} status={_status} job={(_job == null ? "-" : _job.Done ? "done" : "running")}";
    internal void TestAnalyze() => StartAnalysis();
    internal void TestExternal(string path) { _external = path; _proposal = null; _curveTimes = null; _curveOffsets = null; InvalidateVisual(); }
    internal void TestConvert() => StartConversion();
    internal void TestApply() => DoApply();
}
