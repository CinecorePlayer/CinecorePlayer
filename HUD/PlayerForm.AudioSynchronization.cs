#nullable enable
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025;

public sealed partial class PlayerForm
{
    private bool _audioSyncDialogOpen;

    /// <summary>Applica un ritardo audio senza riaprire il file (mpv o LAV Audio in DirectShow).</summary>
    private bool TryApplyLiveAudioDelay(int milliseconds, IPlaybackEngine? engine = null)
    {
        engine ??= _engine;
        try
        {
            return engine switch
            {
                LibMpvPlaybackEngine mpv => mpv.SetAudioDelayMilliseconds(milliseconds),
                DirectShowUnifiedEngine ds => ds.SetAudioDelayMilliseconds(milliseconds),
                _ => false
            };
        }
        catch { return false; }
    }

    private System.Windows.Forms.Timer? _audioDelayFollowTimer;
    private int _audioDelayFollowApplied = int.MinValue;

    /// <summary>
    /// A track synchronised in sections has no single delay: the saved curve says which one holds
    /// at each point of the film, and once a second the player moves to it (after a seek too).
    /// </summary>
    private void StartAudioDelayFollower()
    {
        _audioDelayFollowApplied = int.MinValue;
        if (_audioDelayFollowTimer != null) return;
        _audioDelayFollowTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _audioDelayFollowTimer.Tick += (_, _) =>
        {
            try
            {
                // While the sheet is open the delay heard is the one being tried there.
                if (_engine == null || _audioSyncDialogOpen || string.IsNullOrEmpty(_currentPath) || !_currentMediaHasVideo) return;
                if (ExternalAudioStore.Get(_currentPath) is not { HasCurve: true } binding) { _audioDelayFollowApplied = int.MinValue; return; }
                int wanted = binding.DelayAt(_engine.PositionSeconds);
                if (_audioDelayFollowApplied != int.MinValue && Math.Abs(wanted - _audioDelayFollowApplied) < 12) return;
                if (TryApplyLiveAudioDelay(wanted)) _audioDelayFollowApplied = wanted;
            }
            catch { }
        };
        _audioDelayFollowTimer.Start();
    }

    /// <summary>Regolazione rapida da tastiera durante la visione: Ctrl +/- (Maiusc: x10), Ctrl+0 azzera.</summary>
    private void AdjustAudioDelayLive(int stepMs, bool reset = false)
    {
        if (_engine == null || string.IsNullOrEmpty(_currentPath) || !_currentMediaHasVideo) return;
        var binding = ExternalAudioStore.Get(_currentPath);
        // Una traccia esterna con DirectShow non e' gestibile dal vivo (serve mpv).
        if (binding?.AudioPath != null && _engine is not LibMpvPlaybackEngine) return;
        int next = reset ? 0 : Math.Clamp((binding?.DelayMs ?? 0) + stepMs, -600000, 600000);
        if (!TryApplyLiveAudioDelay(next))
        {
            ShowRemoteOsd(null, null, 1600, Tx("Ritardo audio non regolabile con questo motore", "Audio delay not adjustable with this engine"));
            return;
        }
        // Azzerare toglie anche la curva; un ritocco la sposta tutta insieme.
        var updated = (binding ?? new ExternalAudioBinding(null)) with { DelayMs = next, Status = SyncStatus.Manual, CurveTimes = reset ? null : binding?.CurveTimes, CurveOffsets = reset ? null : binding?.CurveOffsets };
        if (updated.HasCurve) { TryApplyLiveAudioDelay(updated.DelayAt(_engine.PositionSeconds)); _audioDelayFollowApplied = int.MinValue; }
        // Nessun allineamento residuo: si torna alla sincronizzazione originale del file.
        ExternalAudioStore.Set(_currentPath, next == 0 && updated.AudioPath == null && updated.TargetOrdinal == null ? null : updated);
        string text = next == 0 ? Tx("Ritardo audio: 0 ms (originale)", "Audio delay: 0 ms (original)")
            : Tx($"Ritardo audio: {next:+0;-0} ms", $"Audio delay: {next:+0;-0} ms");
        ShowRemoteOsd(null, null, 1400, text);
        if (_infoOverlay?.Visible == true) RefreshInfoOverlayNow();
    }

    /// <summary>
    /// Automatic analysis (or the conversion of a track to the video's speed). It belongs to the
    /// player, not to the sheet: closing the sheet leaves it running and the result is announced
    /// on screen, or applied at once when that needs no reload.
    /// </summary>
    private sealed class AudioSyncJob
    {
        public string Video = "";
        public string? External;
        public int Reference, Target;
        public bool Conversion;
        public readonly CancellationTokenSource Cancel = new();
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public ExternalAudioSynchronization.Step Step;
        public ExternalAudioSynchronization.Result? Result;
        public string? ConvertedPath, Error;
        public bool Done, Cancelled, Consumed;
        public Action? Changed;
    }

    private AudioSyncJob? _audioSyncJob;

    private AudioSyncJob StartAudioSyncJob(string video, string? external, int reference, int target, double duration, bool speech, ExternalAudioSynchronization.Result? convert)
    {
        try { _audioSyncJob?.Cancel.Cancel(); } catch { }
        var job = new AudioSyncJob { Video = video, External = external, Reference = reference, Target = target, Conversion = convert != null };
        _audioSyncJob = job;
        _ = RunAudioSyncJobAsync(job, duration, speech, convert);
        return job;
    }

    private async System.Threading.Tasks.Task RunAudioSyncJobAsync(AudioSyncJob job, double duration, bool speech, ExternalAudioSynchronization.Result? convert)
    {
        // Created on the interface thread: every report comes back here.
        var progress = new Progress<ExternalAudioSynchronization.Step>(step => { if (job.Done) return; job.Step = step; job.Changed?.Invoke(); });
        string audio = job.External ?? job.Video;
        int targetOrdinal = job.External == null ? job.Target : 0;
        try
        {
            if (convert != null)
            {
                job.ConvertedPath = await ExternalAudioSynchronization.ConvertSpeedAsync(audio, targetOrdinal, convert.SpeedRatio, progress, job.Cancel.Token);
                job.Result = new(convert.CorrectionMs, convert.Confidence, Tx("Traccia convertita", "Track converted"),
                    Tx("Copia alla velocità del video: ", "Copy at the video's speed: ") + job.ConvertedPath, true, Note: Tx("velocità corretta", "speed corrected"));
            }
            else
                job.Result = await ExternalAudioSynchronization.AnalyzeAsync(job.Video, audio, job.Reference, duration, speech, progress, job.Cancel.Token, targetOrdinal);
        }
        catch (OperationCanceledException) { job.Cancelled = true; }
        catch (Exception ex) { job.Error = ex.Message; Dbg.Warn("[AUDIOSYNC] " + ex.Message); }
        job.Done = true;
        job.Clock.Stop();
        if (IsDisposed) return;
        if (job.Changed != null) job.Changed();
        else AudioSyncJobFinishedInBackground(job);
    }

    private void AudioSyncJobFinishedInBackground(AudioSyncJob job)
    {
        if (job.Cancelled || !ReferenceEquals(job, _audioSyncJob)) return;
        // Another film is playing: the result stays for when this one is opened again.
        if (_engine == null || !string.Equals(job.Video, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        if (job.Error != null || job.Result == null)
        {
            ShowRemoteOsd(null, null, 3400, Tx("Analisi audio non riuscita", "Audio analysis failed"));
            return;
        }
        var result = job.Result;
        if (!job.Conversion && result.Reliable)
        {
            // Applied at once only when nothing has to be reopened: same source, same track, live delay available.
            var binding = ExternalAudioStore.Get(job.Video) ?? new ExternalAudioBinding(null);
            var streams = _engine.EnumerateStreams().Where(x => x.IsAudio && !x.IsExternal).ToList();
            bool sameSource = string.Equals(binding.AudioPath, job.External, StringComparison.OrdinalIgnoreCase);
            bool sameTrack = job.External != null ? _engine is LibMpvPlaybackEngine : streams.FindIndex(x => x.Selected) == job.Target;
            if (sameSource && sameTrack && TryApplyLiveAudioDelay(result.CorrectionMs))
            {
                var applied = new ExternalAudioBinding(job.External, result.CorrectionMs, job.Reference, job.External == null ? job.Target : null, SyncStatus.Verified, result.Confidence, DateTime.UtcNow,
                    result.Curve?.Select(point => point.Time).ToArray(), result.Curve?.Select(point => point.Ms - result.CorrectionMs).ToArray());
                ExternalAudioStore.Set(job.Video, applied);
                if (applied.HasCurve) { TryApplyLiveAudioDelay(applied.DelayAt(_engine.PositionSeconds)); StartAudioDelayFollower(); }
                job.Consumed = true;
                ShowRemoteOsd(null, null, 3600, applied.HasCurve
                    ? Tx("Audio allineato: ritardo variabile, seguito durante la visione", "Audio aligned: variable delay, followed while watching")
                    : Tx($"Audio allineato: {result.CorrectionMs:+0;-0;0} ms", $"Audio aligned: {result.CorrectionMs:+0;-0;0} ms"));
                if (_infoOverlay?.Visible == true) RefreshInfoOverlayNow();
                return;
            }
        }
        ShowRemoteOsd(null, null, 4200, job.Conversion
            ? Tx("Traccia audio convertita, apri Sincronizzazione audio per usarla", "Audio track converted, open Audio synchronization to use it")
            : result.SpeedReliable
                ? Tx("Analisi audio: velocità diversa, apri Sincronizzazione audio", "Audio analysis: different speed, open Audio synchronization")
                : Tx("Analisi audio pronta, apri Sincronizzazione audio", "Audio analysis ready, open Audio synchronization"));
    }

    private void ShowAudioSynchronization()
    {
        if (_engine == null || string.IsNullOrEmpty(_currentPath) || _audioSyncDialogOpen) return;
        string video = _currentPath;
        var originalEngine = _engine;
        var binding = ExternalAudioStore.Get(video) ?? new ExternalAudioBinding(null);
        var streams = _engine.EnumerateStreams().Where(x => x.IsAudio && !x.IsExternal).ToList();
        int reference = Math.Clamp(binding.ReferenceOrdinal, 0, Math.Max(0, streams.Count - 1));
        int target = Math.Clamp(binding.TargetOrdinal ?? Math.Max(0, streams.FindIndex(x => x.Selected)), 0, Math.Max(0, streams.Count - 1));
        int initialTarget = target;
        if (ExternalAudioStore.Get(video) == null && streams.Count > 1 && reference == target)
            reference = (target + 1) % streams.Count;
        string? external = binding.AudioPath;
        // Un'analisi di questo film ancora in corso, o finita mentre la scheda era chiusa: si riprende da li'.
        AudioSyncJob? job = _audioSyncJob is { Consumed: false, Cancelled: false } pending && string.Equals(pending.Video, video, StringComparison.OrdinalIgnoreCase) ? pending : null;
        if (job != null)
        {
            reference = Math.Clamp(job.Reference, 0, Math.Max(0, streams.Count - 1));
            if (job.External == null) target = Math.Clamp(job.Target, 0, Math.Max(0, streams.Count - 1));
            external = job.External;
        }
        double duration = _engine.DurationSeconds;
        int delayMs = binding.DelayMs;
        // Curva del ritardo (scarti dal valore centrale lungo il film), quando l'analisi ne ha trovata una.
        double[]? curveTimes = binding.CurveTimes;
        int[]? curveOffsets = binding.CurveOffsets;
        bool applied = false;
        ExternalAudioSynchronization.Result? proposal = null;
        using var lifetime = new CancellationTokenSource();

        const int W = 600, Pad = 44, Content = W - 2 * Pad;
        using var sheetLayout = SheetPresenter.Layout(this);
        // Stessa base delle altre schede: doppio buffer (la barra di avanzamento disegnata sulla finestra
        // sfarfallava a ogni aggiornamento), angoli, Esc e la X di chiusura standard.
        using var dialog = new PlainSheetForm
        {
            Text = Tx("Sincronizzazione audio", "Audio synchronization"),
            ClientSize = new Size(W, 652),
            // Si chiude con Annulla o Esc: niente X.
            ShowCloseButton = false,
            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f),
            // Disegnata a 100%: finestra e controlli li scala insieme SheetPresenter.
            AutoScaleMode = AutoScaleMode.None
        };

        Label Caption(string text, int x, int y, int width, int height = 24)
        {
            var label = new Label { Text = text, Bounds = new Rectangle(x, y, width, height), ForeColor = Theme.Muted, AutoEllipsis = true };
            dialog.Controls.Add(label); return label;
        }
        ModalActionButton ActionButton(string text, int x, int y, int width, Action action, int height = 40)
        {
            var button = new ModalActionButton { Text = text, Bounds = new Rectangle(x, y, width, height), BackColor = Theme.SheetRaised, ForeColor = Theme.Text };
            button.Click += (_, _) => action(); dialog.Controls.Add(button); return button;
        }
        ChoiceField TrackField(int x, int y)
        {
            var field = new ChoiceField { Bounds = new Rectangle(x, y, Content, 40), Font = dialog.Font, Underline = true, BackColor = Theme.Sheet, ForeColor = Theme.Text };
            // Nome leggibile ("Italiano, AAC stereo") invece della riga tecnica del filtro ("A: Italian [ita] (aac lc, 48000 Hz, stereo)").
            field.Items.AddRange(streams.Select((s, index) => (object)SubtitleNameNormalizer.NormalizeAudioTrackName(s.Name, index + 1).Replace(" • ", ", ").Replace(" · ", ", "))); dialog.Controls.Add(field); return field;
        }
        // Una colonna, allineata a sinistra come le altre schede: le due tracce, il ritardo (valore
        // grande e passi accanto), il pulsante che lo trova da solo, e in basso a destra Annulla / Applica.
        // Nessun filetto, nessun titolo di sezione: bastano gli spazi.
        var separators = new System.Collections.Generic.List<int>();
        // Azione secondaria: solo testo colorato, senza riquadro.
        ModalActionButton LinkButton(string text, int right, int y, int width, Action action)
        {
            var button = ActionButton(text, right - width, y, width, action, 34);
            button.BackColor = Theme.Sheet; button.ForeColor = Theme.Accent; button.TextOnly = true;
            return button;
        }
        dialog.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(Theme.IsLight ? 60 : 46, Theme.Text));
            float k = SheetPresenter.ScaleOf(dialog);
            foreach (int y in separators) e.Graphics.DrawLine(pen, Pad * k, y * k, (W - Pad) * k, y * k);
        };

        // ===== Intestazione =====
        var title = Caption(Tx("Sincronizzazione audio", "Audio synchronization"), Pad, 30, Content, 38);
        title.Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f); title.ForeColor = Theme.Text;

        // Stato della sincronizzazione: verificata dall'analisi, manuale, da verificare o velocita' diversa.
        ExternalAudioBinding Pending() => new(external, delayMs, reference, external == null ? target : null, StatusNow(),
            proposal?.Confidence ?? binding.Confidence, proposal != null ? DateTime.UtcNow : binding.CheckedUtc, curveTimes, curveOffsets);
        string StatusNow()
        {
            if (proposal == null)
                return ExternalAudioStore.Get(video) is { } saved && saved.DelayMs == delayMs && string.Equals(saved.AudioPath, external, StringComparison.OrdinalIgnoreCase) ? saved.Status : SyncStatus.Manual;
            if (Math.Abs(proposal.SpeedRatio - 1) > 1e-6) return SyncStatus.Speed;
            if (!proposal.Reliable) return delayMs == proposal.CorrectionMs ? SyncStatus.Uncertain : SyncStatus.Manual;
            return delayMs == proposal.CorrectionMs ? SyncStatus.Verified : SyncStatus.Manual;
        }
        // Lo stato sta sotto il titolo, centrato: un punto colorato e una riga di testo.
        var statusPill = new Panel { Bounds = new Rectangle(Pad, 70, Content, 24), BackColor = Theme.Sheet };
        statusPill.Paint += (_, e) =>
        {
            var (text, color) = SyncStatus.Describe(ExternalAudioStore.Get(video) == null && proposal == null && delayMs == 0 && external == null ? null : Pending());
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f);
            int textWidth = statusPill.Width - 20, left = 1;
            using (var dot = new SolidBrush(color)) g.FillEllipse(dot, left, statusPill.Height / 2 - 4, 8, 8);
            TextRenderer.DrawText(g, text, font, new Rectangle(left + 16, 0, textWidth + 4, statusPill.Height), Theme.SubtleText, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        };
        dialog.Controls.Add(statusPill);

        // ===== Tracce =====
        // Una sola colonna: le due tracce una sotto l'altra, larghe quanto la scheda.
        Caption(Tx("Riferimento", "Reference"), Pad, 118, 300);
        Caption(Tx("Traccia da allineare", "Track to align"), Pad, 198, 240);
        var referenceField = TrackField(Pad, 142); referenceField.SelectedIndex = streams.Count == 0 ? -1 : reference;
        var targetField = TrackField(Pad, 222); targetField.SelectedIndex = streams.Count == 0 ? -1 : target;
        referenceField.SelectedIndexChanged += (_, _) => reference = Math.Max(0, referenceField.SelectedIndex);
        targetField.SelectedIndexChanged += (_, _) => target = Math.Max(0, targetField.SelectedIndex);
        // Il file esterno, quando c'e', sta scritto sotto il campo; la scelta e' un'azione di testo sulla riga dell'etichetta.
        var sourceLabel = Caption("", Pad, 266, Content, 20);
        var internalAudio = LinkButton(Tx("Traccia interna", "Internal track"), W - Pad - 138, 190, 130, () => { });
        var choose = LinkButton(Tx("Audio esterno…", "External audio…"), W - Pad, 190, 132, () => { });

        // ===== Ritardo =====
        // Il valore al centro, i passi ai due lati: −100 −10 [ 0 ms ] +10 +100.
        // Il valore e' il centro della scheda: grande, senza riquadro, con un filo sotto.
        var delayFrame = new Panel { Bounds = new Rectangle(Pad, 312, 176, 58), BackColor = Theme.Sheet };
        var delayBox = new TextBox
        {
            Bounds = new Rectangle(0, 6, 122, 44), Text = delayMs.ToString(), TextAlign = HorizontalAlignment.Right,
            BorderStyle = BorderStyle.None, BackColor = delayFrame.BackColor, ForeColor = Theme.Text,
            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 24f), MaxLength = 7
        };
        var unit = new Label { Text = "ms", Bounds = new Rectangle(128, 22, 40, 24), ForeColor = Theme.Muted, BackColor = delayFrame.BackColor };
        delayFrame.Controls.Add(delayBox); delayFrame.Controls.Add(unit); dialog.Controls.Add(delayFrame);
        delayFrame.Paint += (_, e) =>
        {
            using var pen = new Pen(delayBox.Focused ? Theme.Accent : Theme.Border, 2);
            e.Graphics.DrawLine(pen, 0, delayFrame.Height - 1, delayFrame.Width, delayFrame.Height - 1);
        };
        delayBox.GotFocus += (_, _) => delayFrame.Invalidate();
        delayBox.LostFocus += (_, _) => delayFrame.Invalidate();
        // Accanto al titolo: cosa fa il valore scritto ("l'audio arriva 120 ms piu' tardi").
        var hint = Caption("", Pad, 382, Content, 22);
        // Solo quando Applica deve riaprire il file: negli altri casi non c'e' nulla da dire.
        var previewNote = Caption("", Pad, 536, Content, 20);
        var status = Caption("", Pad, 500, Content, 22);

        // Anteprima dal vivo: con mpv e la stessa sorgente il ritardo si sente subito;
        // chiudere senza Applica ripristina il valore salvato.
        bool CanPreviewLive() =>
            ReferenceEquals(_engine, originalEngine) &&
            (originalEngine is LibMpvPlaybackEngine || (originalEngine is DirectShowUnifiedEngine && external == null)) &&
            string.Equals(external, binding.AudioPath, StringComparison.OrdinalIgnoreCase) &&
            (external != null || target == initialTarget);
        void PreviewDelay(int value)
        {
            if (!CanPreviewLive()) return;
            // Con una curva si sente il ritardo di questo punto del film.
            TryApplyLiveAudioDelay(new ExternalAudioBinding(null, value, CurveTimes: curveTimes, CurveOffsets: curveOffsets).DelayAt(originalEngine.PositionSeconds), originalEngine);
        }
        void UpdatePreviewNote()
        {
            previewNote.Text = CanPreviewLive()
                ? Tx("Si sente subito; Annulla rimette il valore di prima. Durante la visione: Ctrl + / Ctrl −.", "You hear it right away; Cancel puts the previous value back. While watching: Ctrl + / Ctrl −.")
                : external != null && originalEngine is not LibMpvPlaybackEngine
                    ? Tx("Applica riaprirà il file con mpv dalla posizione attuale: l’audio esterno richiede mpv.", "Apply reopens the file with mpv at the current position: external audio requires mpv.")
                    : Tx("Applica riaprirà il file dalla posizione attuale per cambiare traccia.", "Apply reopens the file at the current position to switch track.");
            previewNote.ForeColor = Theme.Muted;
            previewNote.Visible = !CanPreviewLive();
        }
        void UpdateSource()
        {
            sourceLabel.Text = external == null ? "" : Path.GetFileName(external);
            targetField.Enabled = external == null && streams.Count > 0;
            internalAudio.Visible = external != null;

            UpdatePreviewNote();
        }
        internalAudio.Click += (_, _) => { external = null; UpdateSource(); };
        choose.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = Tx("Scegli la traccia da allineare", "Choose the track to align"), Filter = "Audio|*.mka;*.aac;*.ac3;*.eac3;*.dts;*.dtshd;*.thd;*.truehd;*.flac;*.wav;*.mp3;*.m4a;*.ogg;*.opus;*.mkv|" + Tx("Tutti i file", "All files") + "|*.*" };
            if (picker.ShowDialog(dialog) == DialogResult.OK) { external = picker.FileName; UpdateSource(); }
        };
        ModalActionButton? resetLink = null;
        void SetDelay(int value)
        {
            delayMs = Math.Clamp(value, -600000, 600000);
            string text = delayMs.ToString();
            if (delayBox.Text != text) { delayBox.Text = text; delayBox.SelectionStart = delayBox.Text.Length; }
            hint.Text = delayMs == 0 ? Tx("Nessuno spostamento", "No offset") : delayMs > 0
                ? Tx($"L’audio arriva {FormatDelay(delayMs)} più tardi", $"Audio arrives {FormatDelay(delayMs)} later")
                : Tx($"L’audio arriva {FormatDelay(-delayMs)} prima", $"Audio arrives {FormatDelay(-delayMs)} earlier");
            PreviewDelay(delayMs);
            if (resetLink != null) resetLink.Visible = delayMs != 0;
            statusPill.Invalidate();
        }
        static string FormatDelay(int ms) => ms >= 1000 ? $"{ms / 1000d:0.###} s" : $"{ms} ms";
        bool ReadDelay()
        {
            string raw = delayBox.Text.Trim().Replace('−', '-');
            if (raw is "" or "-") raw = "0";
            if (!int.TryParse(raw, out int value) || Math.Abs((long)value) > 600000) { status.Text = Tx("Inserisci un ritardo fra −600000 e +600000 ms.", "Enter a delay between −600000 and +600000 ms."); return false; }
            SetDelay(value); return true;
        }
        delayBox.KeyPress += (_, e) =>
        {
            if (char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar)) return;
            if (e.KeyChar is '-' or '−' && delayBox.SelectionStart == 0 && !delayBox.Text.Contains('-')) { if (e.KeyChar == '−') { e.Handled = true; delayBox.SelectedText = "-"; } return; }
            e.Handled = true;
        };
        delayBox.TextChanged += (_, _) =>
        {
            string raw = delayBox.Text.Trim();
            if (raw is "" or "-") return;
            if (int.TryParse(raw, out int value) && Math.Abs((long)value) <= 600000 && value != delayMs) SetDelay(value);
        };

        // I quattro passi stanno attaccati, come un solo controllo; "Azzera" e' un'azione di testo.
        // I passi sono testo ai lati del valore, non riquadri: −100 −10 a sinistra, +10 +100 a destra.
        foreach (var (label, step, x) in new[] { ("−100", -100, Pad + 196), ("−10", -10, Pad + 258), ("+10", 10, Pad + 320), ("+100", 100, Pad + 382) })
        {
            var stepper = ActionButton(label, x, 322, 58, () => { if (ReadDelay()) SetDelay(delayMs + step); }, 38);
            stepper.BackColor = Theme.Sheet; stepper.ForeColor = Theme.SubtleText; stepper.TextOnly = true;
            stepper.Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10.5f);
        }
        // "Azzera" compare solo quando c'e' qualcosa da azzerare, sotto il valore.
        var reset = LinkButton(Tx("Azzera", "Reset"), W - Pad, 324, 70, () => SetDelay(0));
        resetLink = reset; reset.Visible = delayMs != 0;
        delayBox.KeyDown += (_, e) =>
        {
            int step = e.Shift ? 100 : 10;
            if (e.KeyCode == Keys.Up) { if (ReadDelay()) SetDelay(delayMs + step); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { if (ReadDelay()) SetDelay(delayMs - step); e.Handled = e.SuppressKeyPress = true; }
        };

        // ===== Analisi automatica =====
        var speech = new CheckBox { Text = Tx("Confronta anche la trascrizione locale", "Also compare the local transcription"), Bounds = new Rectangle(Pad, 530, 380, 28), AutoSize = false, ForeColor = Theme.SubtleText };
        dialog.Controls.Add(speech);
        speech.Visible = false;
        // L'azione principale della scheda: l'unico pulsante pieno del contenuto.
        var analyze = ActionButton(Tx("Trova il ritardo da solo", "Find the delay automatically"), Pad, 446, Content, () => { }, 46);
        analyze.BackColor = Theme.SheetHover;
        var stop = ActionButton(Tx("Ferma", "Stop"), Pad, 446, Content, () => { try { job?.Cancel.Cancel(); } catch { } }, 46); stop.Visible = false;
        // Sotto il pulsante: prima cosa fa, poi (ad analisi finita) cosa ha trovato.
        // Sotto il pulsante: lo stato (avanzamento o esito) e, ad analisi finita, il dettaglio di cosa ha trovato.
        var analysisNote = Caption("", Pad + 40, 496, Content - 80, 38);
        analysisNote.Visible = false; // il dettaglio dell'analisi e' nel suggerimento della riga di stato
        var accept = ActionButton(Tx("Usa proposta", "Use suggestion"), W - Pad - 190, 526, 190, () => { if (proposal != null) SetDelay(proposal.CorrectionMs); }, 36); accept.Visible = false;
        var convert = ActionButton(Tx("Converti traccia", "Convert track"), W - Pad - 190, 526, 190, () => { }, 36); convert.Visible = false;
        // Avanzamento: una barra sottile, larga quanto il pulsante.
        var progressBar = new Rectangle(Pad, 494, Content, 3);
        dialog.Paint += (_, e) =>
        {
            if (job is not { Done: false }) return;
            using var track = new SolidBrush(Theme.Border);
            using var fill = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(track, progressBar);
            e.Graphics.FillRectangle(fill, progressBar.X, progressBar.Y, (int)Math.Round(progressBar.Width * Math.Clamp(job.Step.Fraction, 0, 1)), progressBar.Height);
        };

        // ===== Azioni =====
        ActionButton(Tx("Annulla", "Cancel"), W - Pad - 136 - 10 - 120, 580, 120, dialog.Close, 40);
        var apply = ActionButton(Tx("Applica", "Apply"), W - Pad - 136, 580, 136, () =>
        {
            if (!ReadDelay()) return;
            if (!string.Equals(video, _currentPath, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(_engine, originalEngine)) { status.Text = Tx("Il contenuto è cambiato: riapri il pannello.", "The content changed: reopen the panel."); return; }
            if (external != null && !File.Exists(external)) { status.Text = Tx("La traccia esterna non è disponibile.", "The external track is not available."); return; }
            try
            {
                var selected = Pending();
                // Si riapre solo per l'audio esterno (serve mpv) o se cambia la sorgente esterna.
                bool reload = !string.Equals(external, binding.AudioPath, StringComparison.OrdinalIgnoreCase)
                    || (external != null && originalEngine is not LibMpvPlaybackEngine);
                if (!reload)
                {
                    if (external == null && streams.Count > 0 && !originalEngine.EnableByGlobalIndex(streams[target].GlobalIndex)) throw new InvalidOperationException(Tx("La traccia interna non è disponibile.", "The internal track is not available."));
                    if (!TryApplyLiveAudioDelay(selected.DelayAt(originalEngine.PositionSeconds), originalEngine)) throw new InvalidOperationException(Tx("Il player non ha applicato il ritardo.", "The player did not apply the delay."));
                    if (selected.HasCurve) StartAudioDelayFollower();
                }
                ExternalAudioStore.Set(video, selected);
                applied = true;
                dialog.Close();
                // La riapertura parte dopo la chiusura del foglio: nessun tasto o
                // focus del dialogo può più interferire con il nuovo avvio.
                if (reload)
                {
                    double position = originalEngine.PositionSeconds; bool paused = _paused;
                    BeginInvoke(new Action(() => { if (string.Equals(video, _currentPath, StringComparison.OrdinalIgnoreCase)) OpenPath(video, position, startPaused: paused, allowPlaceholderGate: false); }));
                }
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }); apply.BackColor = Theme.Accent; apply.ForeColor = Color.White; apply.Height = 40;

        Control[] inputs = { referenceField, targetField, choose, internalAudio, delayFrame, reset, speech, apply };
        void InvalidateProposal() { proposal = null; curveTimes = null; curveOffsets = null; accept.Visible = false; convert.Visible = false; UpdatePreviewNote(); statusPill.Invalidate(); }
        referenceField.SelectedIndexChanged += (_, _) => InvalidateProposal();
        targetField.SelectedIndexChanged += (_, _) => InvalidateProposal();
        choose.Click += (_, _) => InvalidateProposal(); internalAudio.Click += (_, _) => InvalidateProposal();
        using var tip = new ToolTip();
        tip.SetToolTip(analyze, Tx("Confronta musica ed effetti di più scene. Puoi chiudere la scheda: continua da solo e ti avvisa alla fine.", "Compares music and effects of several scenes. You can close the sheet: it carries on and tells you at the end."));
        tip.SetToolTip(delayBox, Tx("↑ ↓: ±10 ms, Maiusc: ±100 ms, + ritarda l’audio, − lo anticipa", "↑ ↓: ±10 ms, Shift: ±100 ms, + delays the audio, − advances it"));
        // L'analisi automatica ascolta le tracce dal file: da un flusso di rete resta il ritardo manuale.
        if (!File.Exists(video))
        {
            analyze.Enabled = false;
            status.Text = Tx("Analisi automatica: ", "Automatic analysis: ") + NetworkOnlyNote + Tx(". Il ritardo manuale funziona.", ". The manual delay works.");
        }
        var jobTimer = new System.Windows.Forms.Timer { Interval = 400 };
        static string Minutes(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        // Stato del lavoro in corso (percentuale, tempo trascorso e stimato) o, alla fine, il suo esito.
        void ShowJob()
        {
            if (dialog.IsDisposed || job == null) return;
            if (!job.Done)
            {
                double fraction = Math.Clamp(job.Step.Fraction, 0, 1), elapsed = job.Clock.Elapsed.TotalSeconds;
                string left = fraction >= 0.1 && fraction < 1 ? Tx($", circa {Minutes(elapsed * (1 - fraction) / fraction)} rimanenti", $", about {Minutes(elapsed * (1 - fraction) / fraction)} left") : "";
                status.Text = $"{fraction:P0}, {job.Step.Text}, {Minutes(elapsed)}{left}";
                foreach (var input in inputs) input.Enabled = false;
                analyze.Visible = false; stop.Visible = true; accept.Visible = false; convert.Visible = false;
                analysisNote.Text = "";
                if (!jobTimer.Enabled) jobTimer.Start();
                dialog.Invalidate(new Rectangle(progressBar.X, progressBar.Y - 1, progressBar.Width, progressBar.Height + 2));
                return;
            }
            jobTimer.Stop();
            var finished = job;
            finished.Changed = null; finished.Consumed = true; job = null;
            foreach (var input in inputs) input.Enabled = true;
            UpdateSource(); referenceField.Enabled = streams.Count > 0; analyze.Visible = true; stop.Visible = false;
            dialog.Invalidate(new Rectangle(progressBar.X, progressBar.Y - 1, progressBar.Width, progressBar.Height + 2));
            string took = Minutes(finished.Clock.Elapsed.TotalSeconds);
            if (finished.Cancelled) { status.Text = Tx("Analisi fermata.", "Analysis stopped."); return; }
            if (finished.Error != null || finished.Result == null) { status.Text = Tx("Analisi non riuscita: ", "Analysis failed: ") + (finished.Error ?? "?"); return; }
            proposal = finished.Result;
            curveTimes = proposal.Curve?.Select(point => point.Time).ToArray();
            curveOffsets = proposal.Curve?.Select(point => point.Ms - proposal.CorrectionMs).ToArray();
            tip.SetToolTip(status, proposal.Method + Environment.NewLine + proposal.Detail);
            tip.SetToolTip(analysisNote, proposal.Detail);
            analysisNote.Height = 38; // due righe: l'esito e' piu' lungo della spiegazione iniziale
            analysisNote.Text = proposal.Detail;
            if (finished.Conversion && finished.ConvertedPath != null)
            {
                // La copia corretta diventa la traccia esterna; resta solo il ritardo residuo.
                external = finished.ConvertedPath;
                UpdateSource();
                SetDelay(proposal.CorrectionMs);
                status.Text = Tx($"Traccia convertita in {took}, ritardo {proposal.CorrectionMs:+0;-0;0} ms, premi Applica", $"Track converted in {took}, delay {proposal.CorrectionMs:+0;-0;0} ms, press Apply");
            }
            else if (proposal.SpeedReliable && Math.Abs(proposal.SpeedRatio - 1) > 1e-6)
            {
                string name = ExternalAudioSynchronization.SpeedName(proposal.SpeedRatio) is { } known ? " (" + known + ")" : "";
                status.Text = Tx($"Velocità diversa: {Math.Abs(proposal.SpeedRatio - 1) * 100:0.00}%{name}, serve una copia convertita", $"Different speed: {Math.Abs(proposal.SpeedRatio - 1) * 100:0.00}%{name}, a converted copy is needed");
                convert.Visible = true;
            }
            else
            {
                string note = proposal.Note.Length > 0 ? ", " + proposal.Note : "";
                status.Text = proposal.Reliable
                    ? Tx($"Trovato {proposal.CorrectionMs:+0;-0;0} ms, affidabilità {proposal.Confidence:P0}{note}, {took}", $"Found {proposal.CorrectionMs:+0;-0;0} ms, confidence {proposal.Confidence:P0}{note}, {took}")
                    : Tx($"Risultato incerto ({proposal.CorrectionMs:+0;-0;0} ms), {took}", $"Uncertain result ({proposal.CorrectionMs:+0;-0;0} ms), {took}");
                if (proposal.Reliable) SetDelay(proposal.CorrectionMs); else accept.Visible = proposal.CorrectionMs != 0;
            }
            statusPill.Invalidate();
        }
        jobTimer.Tick += (_, _) => ShowJob();
        analyze.Click += (_, _) =>
        {
            if (job is { Done: false } || !ReadDelay()) return;
            if (!File.Exists(video) || (external != null && !File.Exists(external))) { status.Text = Tx("L’analisi automatica richiede file locali.", "Automatic analysis needs local files."); return; }
            if (streams.Count == 0) { status.Text = Tx("Nessuna traccia interna disponibile come riferimento.", "No internal track available as reference."); return; }
            if (external == null && reference == target) { status.Text = Tx("Scegli due tracce interne diverse.", "Choose two different internal tracks."); return; }
            job = StartAudioSyncJob(video, external, reference, target, duration, speech.Checked, null);
            job.Changed = ShowJob;
            ShowJob();
        };
        convert.Click += (_, _) =>
        {
            if (job is { Done: false } || proposal is not { SpeedReliable: true } measured) return;
            if (external != null && !File.Exists(external)) { status.Text = Tx("La traccia esterna non è disponibile.", "The external track is not available."); return; }
            job = StartAudioSyncJob(video, external, reference, target, duration, false, measured);
            job.Changed = ShowJob;
            ShowJob();
        };
        if (job != null)
        {
            job.Changed = ShowJob;
            dialog.Shown += (_, _) => ShowJob();
        }
        dialog.FormClosing += (_, _) =>
        {
            jobTimer.Stop(); jobTimer.Dispose();
            if (job == null) return;
            // La scheda si chiude, il lavoro no: l'esito arriva a schermo.
            job.Changed = null;
            if (!job.Done) BeginInvoke(new Action(() => ShowRemoteOsd(null, null, 3200, Tx("L’analisi audio continua in background", "Audio analysis continues in the background"))));
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { dialog.Close(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter && delayBox.Focused) { apply.PerformClick(); e.Handled = e.SuppressKeyPress = true; }
        };
        dialog.FormClosing += (_, _) =>
        {
            lifetime.Cancel();
            if (!applied && ReferenceEquals(_engine, originalEngine))
                TryApplyLiveAudioDelay(binding.DelayAt(originalEngine.PositionSeconds), originalEngine);
            _audioDelayFollowApplied = int.MinValue;
        };
        UpdateSource(); referenceField.Enabled = streams.Count > 0;
        SetDelay(delayMs);
        // Nessun valore evidenziato all'apertura: il fuoco sta sul pulsante principale.
        dialog.Shown += (_, _) => { delayBox.SelectionLength = 0; dialog.ActiveControl = apply; };

        _audioSyncDialogOpen = true;
        bool suppressedExclusive = false;
        try
        {
            HideExternalPlaybackOverlayHosts(); _videoVignette?.HideOverlay(); _hud.Visible = false;
            suppressedExclusive = originalEngine.TrySetMadVrExclusiveModeDisabled(true);
            dialog.TopMost = TopMost;
            SheetPresenter.ShowDialog(dialog, this);
        }
        finally
        {
            if (suppressedExclusive && ReferenceEquals(_engine, originalEngine)) try { originalEngine.TrySetMadVrExclusiveModeDisabled(false); } catch { }
            _audioSyncDialogOpen = false; if (!IsDisposed) { BringOverlaysToFront(); HudBump(2200, true, true); }
        }
    }
}
