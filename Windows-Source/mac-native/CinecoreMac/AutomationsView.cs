using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Dispositivi › Automazioni (AutomationsForm + AutomationRuleForm di Windows) nello stile delle schede nuove: l'elenco
/// delle regole con l'interruttore a destra (clic sulla riga per modificarla) e la scheda della regola: quando, cosa
/// inviare (HTTP POST/GET o MQTT), a chi, contenuto, utente e password (nel Portachiavi), solo con i video, Prova.
/// </summary>
internal sealed class AutomationsView : Control
{
    public event Action? Closed;
    /// <summary>Lista di testo per le scelte (Quando, Cosa inviare).</summary>
    public event Action<IReadOnlyList<MenuEntry>, Point>? MenuRequested;
    public Func<AutomationContext>? Context;

    private List<AutomationRule> _rules = new();
    private AutomationRule? _editing;   // regola aperta nella scheda (copia), null = elenco
    private bool _isNew, _videoOnly, _passwordTouched;
    private string _event = "start", _kind = "http-post";
    private string _status = "";
    private bool? _statusOk;
    private double _scroll;
    private Point _mouse;
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private DateTime _openedAt;
    private double _u = 1;
    private Rect _column, _close, _add, _save, _test, _delete, _toggle, _eventRect, _kindRect;
    private readonly List<(Rect Row, Rect Switch, int Index)> _rows = new();
    private CancellationTokenSource? _testCts;

    private readonly TextBox _name = Box("es. Luci del salotto"), _target = Box(""), _topic = Box("casa/salotto/scena"),
        _payload = Box("{\"scene\":\"cinema\"}"), _user = Box("facoltativo"), _password = Box("facoltativa");
    private TextBox[] Boxes => new[] { _name, _target, _topic, _payload, _user, _password };
    private readonly Dictionary<TextBox, Rect> _fields = new();

    private static TextBox Box(string watermark)
    {
        var box = new TextBox { Watermark = watermark, IsVisible = false };
        WizardSheet.StyleBox(box);
        return box;
    }

    public AutomationsView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        _password.PasswordChar = '•';
        _password.TextChanged += (_, _) => _passwordTouched = true;
        foreach (var box in Boxes)
        {
            box.GotFocus += (_, _) => InvalidateVisual();
            box.LostFocus += (_, _) => InvalidateVisual();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && box != _payload) { e.Handled = true; Save(); }
                else if (e.Key == Key.Escape) { e.Handled = true; BackToList(); }
            };
            VisualChildren.Add(box);
            LogicalChildren.Add(box);
        }
    }

    public bool IsOpen => IsVisible;

    public void Open()
    {
        _rules = DeviceAutomations.Rules.ToList();
        _editing = null; _scroll = 0;
        foreach (var box in Boxes)
        {
            box.Foreground = B(UiTheme.Text); box.CaretBrush = B(UiTheme.Text);
            box.Resources["TextControlPlaceholderForeground"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
            box.Resources["TextControlPlaceholderForegroundFocused"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
            box.SelectionBrush = B(UiTheme.WithAlpha(UiTheme.Text, 70));
        }
        ShowBoxes(false);
        _openedAt = DateTime.UtcNow;
        IsVisible = true;
        Focus();
        InvalidateMeasure(); InvalidateVisual();
    }

    public void Close()
    {
        if (!IsVisible) return;
        _testCts?.Cancel();
        ShowBoxes(false);
        IsVisible = false;
        Closed?.Invoke();
    }

    private void ShowBoxes(bool show)
    {
        _target.Watermark = _kind == "mqtt" ? "192.168.1.10:1883" : "http://homeassistant.local:8123/api/webhook/cinema";
        foreach (var box in Boxes) box.IsVisible = show && (box != _topic || _kind == "mqtt");
    }

    private void Edit(AutomationRule? rule)
    {
        _isNew = rule == null;
        _editing = rule?.Clone() ?? new AutomationRule();
        _event = _editing.Event; _kind = _editing.Kind; _videoOnly = _editing.VideoOnly;
        _name.Text = _editing.Name; _target.Text = _editing.Target; _topic.Text = _editing.Topic;
        _payload.Text = _editing.Payload; _user.Text = _editing.User;
        _password.Text = "";
        _password.Watermark = string.IsNullOrEmpty(_editing.ProtectedPassword) ? "facoltativa" : "salvata nel Portachiavi";
        _passwordTouched = false;
        _status = ""; _statusOk = null;
        ShowBoxes(true);
        _openedAt = DateTime.UtcNow;
        InvalidateMeasure(); InvalidateVisual();
        Dispatcher.UIThread.Post(() => _name.Focus(), DispatcherPriority.Input);
    }

    private void BackToList()
    {
        if (_editing == null) { Close(); return; }
        _testCts?.Cancel();
        _editing = null;
        ShowBoxes(false);
        Focus();
        InvalidateVisual();
    }

    private AutomationRule Collect()
    {
        var rule = _editing!.Clone();
        rule.Name = (_name.Text ?? "").Trim();
        rule.Event = _event; rule.Kind = _kind;
        rule.Target = (_target.Text ?? "").Trim();
        rule.Topic = (_topic.Text ?? "").Trim();
        rule.Payload = _payload.Text ?? "";
        rule.User = (_user.Text ?? "").Trim();
        rule.VideoOnly = _videoOnly;
        return rule;
    }

    private void Save()
    {
        if (_editing == null) return;
        var rule = Collect();
        bool missing = string.IsNullOrWhiteSpace(rule.Target) || (rule.Kind == "mqtt" && string.IsNullOrWhiteSpace(rule.Topic));
        if (missing)
        {
            _statusOk = false;
            _status = rule.Kind == "mqtt" ? "Servono il broker e il topic" : "Serve l’indirizzo";
            InvalidateVisual();
            return;
        }
        if (_passwordTouched)
        {
            DeviceAutomations.Forget(rule.ProtectedPassword);
            rule.ProtectedPassword = DeviceAutomations.Protect(_password.Text ?? "");
        }
        int index = _rules.FindIndex(r => r.Id == rule.Id);
        if (index >= 0) _rules[index] = rule; else _rules.Add(rule);
        DeviceAutomations.Save(_rules);
        BackToList();
    }

    private void Delete()
    {
        if (_editing == null || _isNew) return;
        DeviceAutomations.Forget(_editing.ProtectedPassword);
        _rules.RemoveAll(r => r.Id == _editing.Id);
        DeviceAutomations.Save(_rules);
        BackToList();
    }

    private async void Test()
    {
        if (_editing == null) return;
        var rule = Collect();
        // La password nuova (non ancora salvata) si prova con una copia temporanea nel Portachiavi.
        string? temporary = null;
        if (_passwordTouched && (_password.Text ?? "").Length > 0) { temporary = DeviceAutomations.Protect(_password.Text ?? ""); rule.ProtectedPassword = temporary; }
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _status = "Invio in corso…"; _statusOk = null; InvalidateVisual();
        (bool ok, string detail) result;
        try { result = await DeviceAutomations.RunAsync(rule, Context?.Invoke() ?? new AutomationContext("Cinecore Player", true, 0, 0), cts.Token); }
        catch (Exception ex) { result = (false, ex.Message); }
        if (temporary != null) DeviceAutomations.Forget(temporary);
        if (!ReferenceEquals(_testCts, cts) || _editing == null) return;
        _statusOk = result.ok;
        _status = (result.ok ? "Inviato: " : "Non riuscito: ") + result.detail;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ testi (AutomationText di Windows)
    private static string EventText(string key) => key switch
    {
        "start" => "Quando parte la riproduzione", "pause" => "In pausa", "resume" => "Quando riprende",
        "stop" => "Alla fine o allo stop", "credits" => "Ai titoli di coda", _ => key,
    };

    private static string KindText(string key) => key switch
    {
        "http-post" => "Richiesta HTTP (POST)", "http-get" => "Richiesta HTTP (GET)", "mqtt" => "Messaggio MQTT", _ => key,
    };

    private static string Summary(AutomationRule rule)
    {
        string target = (rule.Kind == "mqtt" ? rule.Topic : rule.Target).Replace("https://", "").Replace("http://", "");
        return EventText(rule.Event) + "   →   " + (rule.Kind == "mqtt" ? "MQTT" : rule.Kind == "http-get" ? "GET" : "POST") + "  " + target;
    }

    // ------------------------------------------------------------------ disegno
    private static IBrush B(Color c) => new SolidColorBrush(c);

    private static void Line(DrawingContext ctx, string text, double px, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, Math.Max(1, px), color, r.Width, 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private void LayoutRects(Size size)
    {
        if (size.Width < 200 || size.Height < 200) size = new Size(Math.Max(size.Width, 640), Math.Max(size.Height, 480));
        _u = WizardSheet.Scale(size);
        var column = WizardSheet.Column(size);
        _close = WizardSheet.Cancel(size);
        _fields.Clear();
        if (_editing == null) { _column = column; return; }
        // La scheda della regola ha molti campi: parte piu' in alto, due colonne.
        double top = Math.Max(70 * _u, size.Height * 0.10);
        _column = new Rect(column.X, top, column.Width, size.Height - top);
        double y = top + 130 * _u, gap = 22 * _u, colW = (_column.Width - gap) / 2, fieldH = 50 * _u, rowH = fieldH + 44 * _u;
        Rect Cell(int row, int col) => new(_column.X + col * (colW + gap), y + row * rowH + 26 * _u, colW, fieldH);
        _fields[_name] = Cell(0, 0);
        _eventRect = Cell(0, 1);
        _kindRect = Cell(1, 0);
        _fields[_target] = Cell(1, 1);
        if (_kind == "mqtt") { _fields[_topic] = Cell(2, 0); _fields[_payload] = Cell(2, 1); }
        else _fields[_payload] = new Rect(_column.X, Cell(2, 0).Y, _column.Width, fieldH);
        _fields[_user] = Cell(3, 0);
        _fields[_password] = Cell(3, 1);
        _toggle = new Rect(_column.X, y + 4 * rowH + 10 * _u, _column.Width, 44 * _u);
        double by = _toggle.Bottom + 50 * _u;
        (_save, _test) = WizardSheet.Buttons(_column, by, _u, "Salva", "Prova");
        double dw = WizardSheet.Measure("Elimina", 18 * _u, WizardSheet.Semibold) + 20 * _u;
        _delete = _isNew ? default : new Rect(_column.Right - dw, by, dw, _save.Height);
        foreach (var box in Boxes) box.FontSize = 17 * _u;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var box in Boxes) box.Measure(new Size(Math.Max(1, 600 * _u), Math.Max(1, 40 * _u)));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutRects(finalSize);
        foreach (var box in Boxes)
            box.Arrange(_fields.TryGetValue(box, out var r) ? WizardSheet.BoxIn(r, 17 * _u * 1.6, _u) : new Rect(-1000, -1000, 1, 1));
        return finalSize;
    }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        LayoutRects(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 380, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        byte alpha = (byte)Math.Round(255 * e);
        double slide = Math.Round(26 * _u * (1 - e));
        WizardSheet.DrawVeil(ctx, new Rect(Bounds.Size), 1);
        WizardSheet.DrawCancel(ctx, _close, Hot(_close), _u, _editing == null ? "Chiudi" : "Annulla");
        if (_editing == null) RenderList(ctx, alpha, slide); else RenderEditor(ctx, alpha, slide);
    }

    private void RenderList(DrawingContext ctx, byte alpha, double slide)
    {
        double top = WizardSheet.DrawHeader(ctx, _column, _u, 0, 1, "Automazioni",
            "Quando nel player succede qualcosa, invia un comando a un dispositivo di casa: una richiesta HTTP (Home Assistant, Shelly, Hue…) o un messaggio MQTT.", alpha, slide);
        _rows.Clear();
        double rowH = 70 * _u, bottom = Bounds.Height - 130 * _u;
        if (_rules.Count == 0)
        {
            var ft = WizardSheet.Text("Nessuna automazione. Un esempio: all’avvio del film, una richiesta a Home Assistant che abbassa le luci e chiude le tapparelle.",
                17 * _u, WizardSheet.Regular, WizardSheet.Soft(alpha), _column.Width, 3);
            ctx.DrawText(ft, new Point(_column.X + slide, top));
            top += ft.Height + 30 * _u;
        }
        else
        {
            int visible = Math.Max(1, (int)((bottom - top) / rowH));
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rules.Count - visible));
            for (int v = 0; v < visible && v + (int)_scroll < _rules.Count; v++)
            {
                int i = v + (int)_scroll;
                var rule = _rules[i];
                var row = new Rect(_column.X, top + v * rowH, _column.Width, rowH);
                bool hot = Hot(row);
                string name = string.IsNullOrWhiteSpace(rule.Name) ? KindText(rule.Kind) : rule.Name;
                Color ink = !rule.Enabled ? WizardSheet.Soft(alpha) : hot ? WizardSheet.Ink(alpha) : UiTheme.WithAlpha(UiTheme.Text, alpha * 225 / 255.0);
                Line(ctx, name, 19 * _u, WizardSheet.Semibold, ink, new Rect(row.X + slide, row.Y + 8 * _u, row.Width - 90 * _u, 28 * _u));
                Line(ctx, Summary(rule), 15 * _u, WizardSheet.Regular, WizardSheet.Soft(alpha), new Rect(row.X + slide, row.Y + 36 * _u, row.Width - 90 * _u, 24 * _u));
                double tw = 52 * _u, th = 30 * _u;
                var track = new Rect(row.Right - tw, row.Center.Y - th / 2, tw, th);
                DrawSwitch(ctx, track, rule.Enabled);
                _rows.Add((row, track.Inflate(new Thickness(10 * _u, 12 * _u)), i));
            }
            top += Math.Min(_rules.Count, visible) * rowH + 30 * _u;
        }
        (_add, _) = WizardSheet.Buttons(_column, Math.Min(top, bottom + 30 * _u), _u, "Aggiungi", null);
        WizardSheet.DrawNext(ctx, _add, "Aggiungi", Hot(_add), true, alpha, _u);
    }

    private void DrawSwitch(DrawingContext ctx, Rect track, bool on)
    {
        double th = track.Height;
        Color onFill = UiTheme.IsLight ? Color.FromRgb(24, 26, 30) : Color.FromRgb(245, 246, 248);
        ctx.DrawRectangle(B(on ? onFill : UiTheme.WithAlpha(UiTheme.Text, 50)), null, track, th / 2, th / 2);
        double knob = th - 6 * _u;
        var knobRect = new Rect(on ? track.Right - knob - 3 * _u : track.X + 3 * _u, track.Y + 3 * _u, knob, knob);
        ctx.DrawEllipse(B(on ? (UiTheme.IsLight ? Colors.White : Color.FromRgb(20, 22, 26)) : Colors.White), null, knobRect);
    }

    private void RenderEditor(DrawingContext ctx, byte alpha, double slide)
    {
        var title = WizardSheet.Text(_isNew ? "Nuova automazione" : "Modifica automazione", 40 * _u, WizardSheet.Semibold, WizardSheet.Ink(alpha), _column.Width);
        ctx.DrawText(title, new Point(_column.X + slide, _column.Y + 20 * _u));
        void Label(Rect field, string text) => Line(ctx, text, 15 * _u, WizardSheet.Semibold, WizardSheet.Soft(alpha), new Rect(field.X, field.Y - 30 * _u, field.Width, 24 * _u));
        string targetLabel = _kind == "mqtt" ? "Broker (indirizzo:porta)" : "Indirizzo";
        var labels = new Dictionary<TextBox, string>
        {
            [_name] = "Nome", [_target] = targetLabel, [_topic] = "Topic", [_payload] = "Contenuto (facoltativo: {title} {event} {position} {duration})",
            [_user] = "Utente", [_password] = "Password",
        };
        foreach (var (box, r) in _fields)
        {
            Label(r, labels[box]);
            bool error = _statusOk == false && _status.StartsWith("Serv", StringComparison.Ordinal) && (box == _target || (box == _topic && _kind == "mqtt")) && string.IsNullOrWhiteSpace(box.Text);
            WizardSheet.DrawUnderline(ctx, r, box.IsFocused, error, alpha, _u);
        }
        void Choice(Rect r, string label, string value)
        {
            Label(r, label);
            WizardSheet.DrawUnderline(ctx, r, Hot(r), false, alpha, _u);
            Line(ctx, value, 17 * _u, WizardSheet.Regular, WizardSheet.Ink(alpha), new Rect(r.X + WizardSheet.FieldInset(_u), r.Y, r.Width - WizardSheet.FieldInset(_u) * 2 - 20 * _u, r.Height));
            Line(ctx, "›", 20 * _u, WizardSheet.Regular, WizardSheet.Soft(alpha), new Rect(r.Right - 34 * _u, r.Y, 20 * _u, r.Height), TextAlignment.Right);
        }
        Choice(_eventRect, "Quando", EventText(_event));
        Choice(_kindRect, "Cosa inviare", KindText(_kind));
        Line(ctx, "Solo con i video (non con la musica)", 18 * _u, WizardSheet.Regular, WizardSheet.Ink(alpha), new Rect(_toggle.X, _toggle.Y, _toggle.Width - 80 * _u, _toggle.Height));
        DrawSwitch(ctx, new Rect(_toggle.Right - 52 * _u, _toggle.Center.Y - 15 * _u, 52 * _u, 30 * _u), _videoOnly);
        if (_status.Length > 0)
        {
            Color ink = _statusOk == true ? Color.FromRgb(72, 199, 142) : _statusOk == false ? Color.FromRgb(255, 120, 128) : WizardSheet.Soft(alpha);
            Line(ctx, _status, 15 * _u, WizardSheet.Regular, ink, new Rect(_column.X, _toggle.Bottom + 10 * _u, _column.Width, 26 * _u));
        }
        WizardSheet.DrawNext(ctx, _save, "Salva", Hot(_save), true, alpha, _u);
        WizardSheet.DrawBack(ctx, _test, "Prova", Hot(_test), alpha, _u);
        if (_delete.Width > 0)
            Line(ctx, "Elimina", 18 * _u, WizardSheet.Semibold, Color.FromArgb((byte)(Hot(_delete) ? 255 : 200), 255, 110, 120), _delete, TextAlignment.Right);
    }

    // ------------------------------------------------------------------ input
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var now = e.GetPosition(this);
        bool changed = _zones.Changed(now);
        _mouse = now;
        bool hand = _close.Contains(_mouse) || (_editing == null ? _add.Contains(_mouse) || _rows.Any(r => r.Row.Contains(_mouse))
            : _save.Contains(_mouse) || _test.Contains(_mouse) || _delete.Contains(_mouse) || _toggle.Contains(_mouse) || _eventRect.Contains(_mouse) || _kindRect.Contains(_mouse));
        Cursor = hand ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (changed) InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        e.Handled = true;
        if (_close.Contains(p)) { BackToList(); return; }
        if (_editing == null)
        {
            if (_add.Contains(p)) { Edit(null); return; }
            foreach (var (row, sw, index) in _rows)
            {
                if (sw.Contains(p)) { _rules[index].Enabled = !_rules[index].Enabled; DeviceAutomations.Save(_rules); InvalidateVisual(); return; }
                if (row.Contains(p)) { Edit(_rules[index]); return; }
            }
            return;
        }
        if (_save.Contains(p)) Save();
        else if (_test.Contains(p)) Test();
        else if (_delete.Contains(p)) Delete();
        else if (_toggle.Contains(p)) { _videoOnly = !_videoOnly; InvalidateVisual(); }
        else if (_eventRect.Contains(p))
            MenuRequested?.Invoke(DeviceAutomations.Events.Select(k => new MenuEntry(EventText(k), null, () => { _event = k; InvalidateVisual(); }) { Checked = k == _event }).ToList(),
                new Point(_eventRect.X + WizardSheet.FieldInset(_u), _eventRect.Bottom + 8 * _u));
        else if (_kindRect.Contains(p))
            MenuRequested?.Invoke(DeviceAutomations.Kinds.Select(k => new MenuEntry(KindText(k), null, () => { _kind = k; ShowBoxes(true); InvalidateMeasure(); InvalidateArrange(); InvalidateVisual(); }) { Checked = k == _kind }).ToList(),
                new Point(_kindRect.X + WizardSheet.FieldInset(_u), _kindRect.Bottom + 8 * _u));
        else foreach (var (box, r) in _fields) if (r.Contains(p)) { box.Focus(); break; }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editing != null) return;
        _scroll = Math.Max(0, _scroll - Math.Sign(e.Delta.Y));
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { BackToList(); e.Handled = true; }
        else if (e.Key == Key.Enter && _editing != null) { Save(); e.Handled = true; }
    }

    // ------------------------------------------------------------------ prove
    internal string QaState => $"regole={_rules.Count} scheda={(_editing == null ? "elenco" : _isNew ? "nuova" : "modifica")} evento={_event} tipo={_kind} stato={_status}";
    internal void QaNew(string kind, string target, string topic, string payload, bool music = false)
    {
        Edit(null);
        _videoOnly = !music;
        _kind = kind; _target.Text = target; _topic.Text = topic; _payload.Text = payload; _name.Text = "Prova " + kind;
        ShowBoxes(true); InvalidateMeasure(); InvalidateArrange();
    }
    internal void QaTest() => Test();
    internal void QaSave() => Save();
    internal void QaDeleteAll()
    {
        foreach (var r in _rules) DeviceAutomations.Forget(r.ProtectedPassword);
        _rules.Clear(); DeviceAutomations.Save(_rules); InvalidateVisual();
    }
}
