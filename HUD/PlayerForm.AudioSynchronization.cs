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

    private void ShowAudioSynchronization()
    {
        if (_engine == null || string.IsNullOrEmpty(_currentPath) || _audioSyncDialogOpen) return;
        string video = _currentPath;
        var originalEngine = _engine;
        var binding = ExternalAudioStore.Get(video) ?? new ExternalAudioBinding(null);
        var streams = _engine.EnumerateStreams().Where(x => x.IsAudio && !x.IsExternal).ToList();
        int reference = Math.Clamp(binding.ReferenceOrdinal, 0, Math.Max(0, streams.Count - 1));
        int target = Math.Clamp(binding.TargetOrdinal ?? Math.Max(0, streams.FindIndex(x => x.Selected)), 0, Math.Max(0, streams.Count - 1));
        if (ExternalAudioStore.Get(video) == null && streams.Count > 1 && reference == target)
            reference = (target + 1) % streams.Count;
        string? external = binding.AudioPath;
        double duration = _engine.DurationSeconds;
        int delayMs = binding.DelayMs;
        ExternalAudioSynchronization.Result? proposal = null;
        using var lifetime = new CancellationTokenSource();
        using var dialog = new Form
        {
            Text = "Sincronizzazione audio", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(780, 640), BackColor = Theme.Panel,
            ForeColor = Theme.Text, Font = new Font("Segoe UI", 10f), ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None, KeyPreview = true,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96)
        };
        dialog.SizeChanged += (_, _) => { using var shape = ModalActionButton.Rounded(dialog.ClientRectangle, 12); var old = dialog.Region; dialog.Region = new Region(shape); old?.Dispose(); };
        using (var shape = ModalActionButton.Rounded(dialog.ClientRectangle, 12)) dialog.Region = new Region(shape);
        Label Caption(string text, int x, int y, int width, int height = 24)
        {
            var label = new Label { Text = text, Bounds = new Rectangle(x, y, width, height), ForeColor = Theme.Muted, AutoEllipsis = true };
            dialog.Controls.Add(label); return label;
        }
        ModalActionButton ActionButton(string text, int x, int y, int width, Action action)
        {
            var button = new ModalActionButton { Text = text, Bounds = new Rectangle(x, y, width, 40), BackColor = Theme.Panel, ForeColor = Theme.Text };
            button.Click += (_, _) => action(); dialog.Controls.Add(button); return button;
        }
        ChoiceField TrackField(int x)
        {
            var field = new ChoiceField { Bounds = new Rectangle(x, 150, 340, 42), Font = dialog.Font };
            field.Items.AddRange(streams.Select(x => (object)x.Name)); dialog.Controls.Add(field); return field;
        }
        var title = Caption("Sincronizzazione audio", 36, 28, 640, 40);
        title.Font = new Font("Segoe UI", 22f); title.ForeColor = Theme.Text;
        Caption("Confronta due tracce e regola quella che vuoi ascoltare.", 36, 80, 708);
        ActionButton("×", 704, 27, 40, dialog.Close).BackColor = dialog.BackColor;
        Caption("Riferimento", 36, 120, 340);
        Caption("Traccia da allineare", 404, 120, 340);
        var referenceField = TrackField(36); referenceField.SelectedIndex = streams.Count == 0 ? -1 : reference;
        var targetField = TrackField(404); targetField.SelectedIndex = streams.Count == 0 ? -1 : target;
        referenceField.SelectedIndexChanged += (_, _) => reference = Math.Max(0, referenceField.SelectedIndex);
        targetField.SelectedIndexChanged += (_, _) => target = Math.Max(0, targetField.SelectedIndex);
        var sourceLabel = Caption("", 36, 207, 708, 28); sourceLabel.ForeColor = Theme.Text;
        void UpdateSource()
        {
            sourceLabel.Text = external == null ? "Entrambe le tracce sono contenute nel file" : "Audio esterno · " + Path.GetFileName(external);
            targetField.Enabled = external == null && streams.Count > 0;
        }
        var choose = ActionButton("Scegli audio esterno…", 36, 246, 230, () =>
        {
            using var picker = new OpenFileDialog { Title = "Scegli la traccia da allineare", Filter = "Audio|*.mka;*.aac;*.ac3;*.eac3;*.dts;*.dtshd;*.thd;*.truehd;*.flac;*.wav;*.mp3;*.m4a;*.ogg;*.opus;*.mkv|Tutti i file|*.*" };
            if (picker.ShowDialog(dialog) == DialogResult.OK) { external = picker.FileName; UpdateSource(); }
        });
        var internalAudio = ActionButton("Usa traccia interna", 280, 246, 210, () => { external = null; UpdateSource(); });
        UpdateSource(); referenceField.Enabled = streams.Count > 0;
        Caption("Ritardo della traccia da allineare", 36, 311, 708);
        var delayFrame = new Panel { Bounds = new Rectangle(36, 343, 156, 42), BackColor = Theme.Panel };
        var delayBox = new TextBox { Bounds = new Rectangle(12, 9, 132, 24), Text = delayMs.ToString(), TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.None, BackColor = delayFrame.BackColor, ForeColor = Theme.Text, Font = new Font("Segoe UI", 12f) };
        delayFrame.Controls.Add(delayBox); dialog.Controls.Add(delayFrame);
        var status = Caption("L’analisi confronta più scene. Nessuna modifica viene salvata prima di Applica.", 36, 494, 708, 52);
        void SetDelay(int value) { delayMs = Math.Clamp(value, -600000, 600000); delayBox.Text = delayMs.ToString(); }
        bool ReadDelay()
        {
            if (!int.TryParse(delayBox.Text, out int value) || Math.Abs((long)value) > 600000) { status.Text = "Inserisci un ritardo fra −600000 e +600000 ms."; return false; }
            SetDelay(value); return true;
        }
        var minus = ActionButton("−50 ms", 208, 343, 100, () => { if (ReadDelay()) SetDelay(delayMs - 50); });
        var plus = ActionButton("+50 ms", 320, 343, 100, () => { if (ReadDelay()) SetDelay(delayMs + 50); });
        var reset = ActionButton("Azzera", 432, 343, 100, () => SetDelay(0));
        Caption("Millisecondi · + ritarda l’audio · − anticipa l’audio", 36, 398, 708);
        var speech = new CheckBox { Text = "Confronta anche la trascrizione locale", Bounds = new Rectangle(36, 443, 430, 28), AutoSize = false, ForeColor = Theme.Muted };
        dialog.Controls.Add(speech);
        CancellationTokenSource? analysis = null;
        var accept = ActionButton("Usa proposta", 548, 443, 196, () => { if (proposal != null) SetDelay(proposal.CorrectionMs); }); accept.Visible = false;
        var analyze = ActionButton("Allinea automaticamente", 36, 564, 230, () => { });
        var stop = ActionButton("Ferma analisi", 280, 564, 150, () => analysis?.Cancel()); stop.Visible = false;
        ActionButton("Annulla", 478, 564, 126, dialog.Close);
        var apply = ActionButton("Applica", 618, 564, 126, () =>
        {
            if (!ReadDelay()) return;
            if (!string.Equals(video, _currentPath, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(_engine, originalEngine)) { status.Text = "Il contenuto è cambiato: riapri il pannello."; return; }
            if (external != null && !File.Exists(external)) { status.Text = "La traccia esterna non è disponibile."; return; }
            try
            {
                var selected = new ExternalAudioBinding(external, delayMs, reference, external == null ? target : null);
                bool reload = originalEngine is not LibMpvPlaybackEngine || !string.Equals(external, binding.AudioPath, StringComparison.OrdinalIgnoreCase);
                if (!reload && originalEngine is LibMpvPlaybackEngine mpv)
                {
                    if (external == null && streams.Count > 0 && !mpv.EnableByGlobalIndex(streams[target].GlobalIndex)) throw new InvalidOperationException("La traccia interna non è disponibile.");
                    if (!mpv.SetAudioDelayMilliseconds(delayMs)) throw new InvalidOperationException("Il player non ha applicato il ritardo.");
                }
                ExternalAudioStore.Set(video, selected);
                if (reload) OpenPath(video, originalEngine.PositionSeconds, startPaused: _paused, allowPlaceholderGate: false);
                dialog.Close();
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }); apply.BackColor = Theme.Accent; apply.ForeColor = Color.White;
        Control[] inputs = { referenceField, targetField, choose, internalAudio, delayFrame, minus, plus, reset, speech, apply };
        void InvalidateProposal() { proposal = null; accept.Visible = false; }
        referenceField.SelectedIndexChanged += (_, _) => InvalidateProposal();
        targetField.SelectedIndexChanged += (_, _) => InvalidateProposal();
        choose.Click += (_, _) => InvalidateProposal(); internalAudio.Click += (_, _) => InvalidateProposal();
        delayFrame.Paint += (_, e) => { using var pen = new Pen(Theme.Accent); e.Graphics.DrawLine(pen, 0, delayFrame.Height - 1, delayFrame.Width, delayFrame.Height - 1); };
        using var tip = new ToolTip();
        analyze.Click += async (_, _) =>
        {
            if (analysis != null || !ReadDelay()) return;
            if (!File.Exists(video) || (external != null && !File.Exists(external))) { status.Text = "L’analisi automatica richiede file locali."; return; }
            if (streams.Count == 0) { status.Text = "Nessuna traccia interna disponibile come riferimento."; return; }
            if (external == null && reference == target) { status.Text = "Scegli due tracce interne diverse."; return; }
            analysis = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var run = analysis;
            string audio = external ?? video; int source = reference, targetOrdinal = external == null ? target : 0; bool useSpeech = speech.Checked;
            foreach (var input in inputs) input.Enabled = false;
            analyze.Enabled = false; stop.Visible = true; accept.Visible = false;
            var progress = new Progress<string>(text => { if (!dialog.IsDisposed && !lifetime.IsCancellationRequested) status.Text = text; });
            try
            {
                proposal = await ExternalAudioSynchronization.AnalyzeAsync(video, audio, source, duration, useSpeech, progress, run.Token, targetOrdinal);
                if (dialog.IsDisposed || run.IsCancellationRequested) return;
                status.Text = $"{proposal.CorrectionMs:+0;-0;0} ms · affidabilità {proposal.Confidence:P0} · {proposal.Detail}";
                tip.SetToolTip(status, proposal.Method + Environment.NewLine + proposal.Detail);
                if (proposal.Reliable) SetDelay(proposal.CorrectionMs); else accept.Visible = true;
            }
            catch (OperationCanceledException) { if (!dialog.IsDisposed) status.Text = "Analisi annullata."; }
            catch (Exception ex) { if (!dialog.IsDisposed) status.Text = "Analisi non riuscita: " + ex.Message; }
            finally
            {
                run.Dispose(); if (ReferenceEquals(analysis, run)) analysis = null;
                if (!dialog.IsDisposed) { foreach (var input in inputs) input.Enabled = true; UpdateSource(); referenceField.Enabled = streams.Count > 0; analyze.Enabled = true; stop.Visible = false; }
            }
        };
        dialog.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { dialog.Close(); e.Handled = true; } };
        dialog.FormClosing += (_, _) => lifetime.Cancel();
        _audioSyncDialogOpen = true;
        bool suppressedExclusive = false;
        try
        {
            HideExternalPlaybackOverlayHosts(); _videoVignette?.HideOverlay(); _hud.Visible = false;
            suppressedExclusive = originalEngine.TrySetMadVrExclusiveModeDisabled(true);
            dialog.TopMost = TopMost;
            dialog.ShowDialog(this);
        }
        finally
        {
            if (suppressedExclusive && ReferenceEquals(_engine, originalEngine)) try { originalEngine.TrySetMadVrExclusiveModeDisabled(false); } catch { }
            _audioSyncDialogOpen = false; if (!IsDisposed) { BringOverlaysToFront(); HudBump(2200, true, true); }
        }
    }
}
