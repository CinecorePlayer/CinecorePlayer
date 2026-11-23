using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Impostazioni › Componenti › Preset: preset dei renderer creati dall'utente (RenderPresets). Elenco per renderer e,
/// aperto un preset, le sue righe con gli stessi elementi delle altre pagine: nome, renderer, attivazione, posizione,
/// condizioni e le voci del renderer (ognuna parte da "Come nelle Impostazioni").
/// </summary>
internal sealed partial class SettingsView
{
    private string? _presetEdit, _ruleEdit;
    private TextBox? _presetName;
    private Rect _presetNameRect;

    private RenderPresets.Preset? EditedPreset => RenderPresets.Find(_presetEdit);
    private RenderPresets.Rule? EditedRule => RenderPresets.FindRule(_ruleEdit);

    private double DrawPresetRows(DrawingContext ctx, Rect body)
    {
        double width = body.Width;
        double controlW = Math.Min(330, Math.Max(210, Math.Floor(width * 2 / 5)));
        double controlX = body.X + width - controlW;
        double textW = Math.Max(90, controlX - body.X - 28);
        double y = body.Y;
        _presetNameRect = default;

        void Section(string title)
        {
            ctx.DrawLine(new Pen(Brush(A(UiTheme.Border, 50)), 1), new Point(body.X, y + .5), new Point(body.X + width, y + .5));
            Text(ctx, title.ToUpperInvariant(), 9, true, new Rect(body.X, y + 20, width, 24), UiTheme.Muted);
            y += 56;
        }
        Rect Row(string title, string description = "")
        {
            Text(ctx, title, 10.5, false, new Rect(body.X, y, textW, 30), UiTheme.Text);
            double h = description.Length > 0 ? 66 : 56;
            if (description.Length > 0) h += RowDescription(ctx, title, description, new Rect(body.X, y + 29, textW, 23));
            var r = new Rect(controlX, y + 1, controlW, 36);
            y += h;
            return r;
        }
        void Chip(Rect r, string text, string key, bool emphasis = false) { DrawValueChip(ctx, r, text, Hover(r), emphasis); _hits.Add(new Hit(r, "preset-field", key)); }
        void Back()
        {
            var back = new Rect(body.X - 6, y, Measure("‹ Automazioni e preset", 9.8, true) + 12, 32);
            Text(ctx, "‹ Automazioni e preset", 9.8, true, back, Hover(back) ? UiTheme.Accent : A(UiTheme.Text, 215), TextAlignment.Center);
            _hits.Add(new Hit(back, "preset-back"));
            y += 48;
        }
        void Bottom(string deleteText)
        {
            y += 8;
            var done = new Rect(body.X + width - 140, y, 140, 38);
            DrawActionButton(ctx, done, "Fatto");
            _hits.Add(new Hit(done, "preset-back"));
            double delW = Measure(deleteText, 9.6, true) + 24;
            var delete = new Rect(done.X - 16 - delW, y, delW, 38);
            Text(ctx, deleteText, 9.6, true, delete, Hover(delete) ? Danger : UiTheme.SubtleText, TextAlignment.Center);
            _hits.Add(new Hit(delete, "preset-delete"));
            y += 60;
        }

        // ----- un'automazione aperta: QUANDO ... ALLORA ...
        if (EditedRule is { } rule)
        {
            Back();
            Section("Quando");
            Chip(Row("Risoluzione del film"), RenderPresets.Label(RenderPresets.ResolutionChoices, rule.Resolution), "resolution", rule.Resolution != "any");
            Chip(Row("HDR"), RenderPresets.Label(RenderPresets.HdrChoices, rule.Hdr), "hdr", rule.Hdr != "any");
            Chip(Row("Fotogrammi al secondo"), RenderPresets.Label(RenderPresets.FrameRateChoices, rule.FrameRate), "framerate", rule.FrameRate != "any");
            Chip(Row("Alimentazione"), RenderPresets.Label(RenderPresets.PowerChoices, rule.Power), "power", rule.Power != "any");
            Chip(Row("Schermo", "Quello su cui si trova la finestra all’apertura del film."), RenderPresets.Label(RenderPresets.ScreenChoices, rule.Screen), "screen", rule.Screen != "any");
            y += 12;
            Section("Allora");
            var target = RenderPresets.Find(rule.PresetId);
            string renderer = target?.Renderer ?? "madvr";
            Chip(Row("Renderer", "L’automazione vale per i film che si aprono con questo renderer."), RenderPresets.RendererLabel(renderer), "rule-renderer");
            Chip(Row("Usa il preset"), target?.Name ?? "Scegli un preset", "rule-preset", true);
            y += 12;
            Section("Automazione");
            var on = Row("Attiva");
            DrawSwitch(ctx, on, rule.Enabled ? "Attiva" : "Spenta", rule.Enabled);
            _hits.Add(new Hit(on, "preset-field", "rule-enabled"));
            int index = RenderPresets.Rules.ToList().IndexOf(rule);
            Chip(Row("Posizione nell’elenco", "Se valgono più automazioni, vince quella più in alto."), $"{index + 1} di {RenderPresets.Rules.Count}", "rule-position");
            Bottom("Elimina automazione");
            return y - body.Y + 40;
        }

        // ----- un preset aperto: nome, renderer, voci
        if (EditedPreset is { } preset)
        {
            Back();
            string tab = RenderPresets.Tab(preset.Renderer);
            Section("Preset");
            var name = Row("Nome");
            bool focused = _presetName?.IsFocused == true;
            ctx.DrawLine(new Pen(Brush(focused ? UiTheme.Accent : A(UiTheme.Border, 110)), focused ? 2 : 1), new Point(name.X, name.Bottom - 1), new Point(name.Right, name.Bottom - 1));
            _presetNameRect = name;
            Chip(Row("Renderer", "Cambiandolo si azzerano le voci del preset."), RenderPresets.RendererLabel(preset.Renderer), "renderer");
            y += 12;
            // Voci del renderer: come la sua pagina, con "Come nelle Impostazioni" finche' non si sceglie un valore.
            var sections = ComponentSettings.Sections(tab);
            var groups = sections.Select(x => x.Group).Where(g => g.Length > 0).Distinct().ToList();
            string groupKey = "preset:" + tab;
            if (groups.Count > 1)
            {
                string current = _nativeGroup.TryGetValue(groupKey, out var chosen) && groups.Contains(chosen) ? chosen : groups[0];
                DrawPresetPillTabs(ctx, new Rect(body.X, y, width, 36), groups, current, groupKey);
                y += 50;
                sections = sections.Where(x => x.Group == current).ToArray();
            }
            foreach (var section in sections)
            {
                Section(section.Title);
                foreach (var setting in section.Settings)
                {
                    var r = Row(setting.Label, setting.Description);
                    bool changed = preset.Values.TryGetValue(setting.Key, out var v);
                    string label = changed ? ComponentSettings.ValueLabel(setting, v!) : "Come nelle Impostazioni";
                    // Le voci cambiate dal preset in evidenza (colore accento), le altre come nelle Impostazioni.
                    DrawValueChip(ctx, r, label, Hover(r), changed);
                    _hits.Add(new Hit(r, "preset-value", setting.Key));
                }
                y += 12;
            }
            Bottom("Elimina preset");
            return y - body.Y + 40;
        }

        // ----- elenco: automazioni, poi i preset di ogni renderer
        _presetEdit = _ruleEdit = null;
        Text(ctx, "Le automazioni valgono con Personalizzato (menu del tasto destro del film: Video › Preset). Con Automatico sceglie il player.", 9, false, new Rect(body.X, y, width, 22), UiTheme.Muted);
        Text(ctx, "Dallo stesso menu si sceglie anche un preset a mano.", 9, false, new Rect(body.X, y + 20, width, 22), UiTheme.Muted);
        y += 58;
        Section("Automazioni");
        foreach (var r in RenderPresets.Rules)
        {
            double top = y;
            var control = Row(RenderPresets.When(r), L10n.T("Usa") + " " + RenderPresets.Then(r));
            var sw = new Rect(control.Right - 150, control.Y, 150, control.Height);
            DrawSwitch(ctx, sw, r.Enabled ? "Attiva" : "Spenta", r.Enabled);
            _hits.Add(new Hit(new Rect(body.X, top, Math.Max(1, sw.X - body.X - 12), y - top - 8), "preset-rule-open", r.Id));
            _hits.Add(new Hit(sw, "preset-rule-toggle", r.Id));
        }
        var addRule = Row(RenderPresets.Rules.Count == 0 ? "Nessuna automazione" : "");
        DeviceButtonKind(ctx, addRule, "Nuova automazione", "preset-rule-new", "");
        y += 6;
        foreach (var (renderer, label) in RenderPresets.Renderers)
        {
            Section("Preset " + label);
            foreach (var p in RenderPresets.All.Where(p => p.Renderer == renderer))
            {
                var rowTop = y;
                var action = Row(p.Name, RenderPresets.Summary(p, withRenderer: false));
                var edit = new Rect(action.Right - 104, action.Y, 104, action.Height);
                DrawTextAction(ctx, edit, "Modifica");
                _hits.Add(new Hit(new Rect(body.X, rowTop, width, y - rowTop - 8), "preset-open", p.Id));
            }
            var add = Row(RenderPresets.All.Any(p => p.Renderer == renderer) ? "" : "Nessun preset");
            DeviceButtonKind(ctx, add, "Nuovo preset", "preset-new", renderer);
            y += 6;
        }
        return y - body.Y + 40;
    }

    private void DeviceButtonKind(DrawingContext ctx, Rect r, string label, string kind, string key)
    {
        double w = Math.Min(r.Width, Measure(label, 9.8, true) + 40);
        var button = new Rect(r.Right - w, r.Y, w, r.Height);
        // Pulsante secondario: forma piena tenue, mai testo nudo.
        ctx.DrawRectangle(Brush(A(UiTheme.Text, Hover(button) ? 40 : 22)), null, button, button.Height / 2, button.Height / 2);
        Text(ctx, label, 9.8, true, button, UiTheme.Text, TextAlignment.Center);
        _hits.Add(new Hit(button, kind, key));
    }

    private void DrawPresetPillTabs(DrawingContext ctx, Rect row, IReadOnlyList<string> tabs, string current, string groupKey)
    {
        int first = _hits.Count;
        DrawPillTabs(ctx, row, tabs, current);
        // Le stesse pillole delle pagine dei componenti, ma il gruppo scelto si ricorda per l'editor dei preset.
        for (int i = first; i < _hits.Count; i++)
            if (_hits[i].Kind == "native-group") _hits[i] = _hits[i] with { Kind = "preset-group", Key = groupKey + "|" + _hits[i].Key };
    }

    private void PresetActivate(Hit hit, Point click)
    {
        switch (hit.Kind)
        {
            case "preset-new": OpenPreset(RenderPresets.Add(hit.Key).Id); return;
            case "preset-open": OpenPreset(hit.Key); return;
            case "preset-rule-new": OpenRule(RenderPresets.AddRule().Id); return;
            case "preset-rule-open": OpenRule(hit.Key); return;
            case "preset-rule-toggle" when RenderPresets.FindRule(hit.Key) is { } toggled:
                toggled.Enabled = !toggled.Enabled; RenderPresets.Save(); break;
            case "preset-back": ClosePreset(); return;
            case "preset-delete" when EditedRule is { } doomedRule:
                ShowChoices(hit.Bounds, new (string, bool, Action)[]
                {
                    ("Elimina automazione", false, () => { RenderPresets.RemoveRule(doomedRule); ClosePreset(); }),
                    ("Annulla", false, () => { }),
                });
                return;
            case "preset-delete" when EditedPreset is { } doomed:
                ShowChoices(hit.Bounds, new (string, bool, Action)[]
                {
                    ("Elimina “" + doomed.Name + "”", false, () => { RenderPresets.Remove(doomed); ClosePreset(); }),
                    ("Annulla", false, () => { }),
                });
                return;
            case "preset-group":
            {
                int bar = hit.Key.IndexOf('|');
                if (bar > 0) { _nativeGroup[hit.Key[..bar]] = hit.Key[(bar + 1)..]; }
                break;
            }
            case "preset-field" when EditedRule is { } r: RuleField(r, hit, click); return;
            case "preset-field" when EditedPreset is { } p: PresetField(p, hit, click); return;
            case "preset-value" when EditedPreset is { } p: PresetValue(p, hit, click); return;
        }
        InvalidateVisual();
    }

    private void OpenRule(string id)
    {
        _ruleEdit = id; _presetEdit = null;
        _scroll = 0;
        InvalidateVisual();
    }

    /// <summary>Prove (script QA): apre l'editor del preset con questo id, o del primo se l'id e' vuoto.</summary>
    public void TestOpenPreset(string id)
    {
        if (id.StartsWith("rule", StringComparison.Ordinal)) { OpenRule((RenderPresets.Rules.FirstOrDefault() ?? RenderPresets.AddRule()).Id); return; }
        OpenPreset(id.Length > 0 ? id : RenderPresets.All.FirstOrDefault()?.Id ?? "");
    }

    private void OpenPreset(string id)
    {
        _presetEdit = id; _ruleEdit = null;
        _scroll = 0;
        EnsurePresetName();
        if (_presetName != null) _presetName.Text = EditedPreset?.Name ?? "";
        InvalidateVisual();
    }

    private void ClosePreset()
    {
        CommitPresetName();
        _presetEdit = _ruleEdit = null;
        if (_presetName != null) _presetName.IsVisible = false;
        _scroll = 0;
        Focus();
        InvalidateVisual();
    }

    /// <summary>Scelta con le frecce ‹ › (scorre i valori) o con l'elenco (centro), come le altre righe.</summary>
    private void Step(Rect source, Point click, IReadOnlyList<(string Value, string Label)> choices, string current, Action<string> set)
    {
        int step = click.X < source.X + 34 ? -1 : click.X > source.Right - 34 ? 1 : 0;
        if (step != 0)
        {
            int index = choices.ToList().FindIndex(c => c.Value == current);
            if (index < 0) index = step > 0 ? -1 : 0;
            set(choices[(index + step + choices.Count) % choices.Count].Value);
            RenderPresets.Save();
            InvalidateVisual();
            return;
        }
        ShowChoices(source, choices.Select(c => (c.Label, c.Value == current, (Action)(() => { set(c.Value); RenderPresets.Save(); InvalidateVisual(); }))));
    }

    private void PresetField(RenderPresets.Preset p, Hit hit, Point click)
    {
        if (hit.Key == "renderer")
            Step(hit.Bounds, click, RenderPresets.Renderers, p.Renderer, v => { if (v != p.Renderer) { p.Renderer = v; p.Values.Clear(); } });
    }

    private void RuleField(RenderPresets.Rule r, Hit hit, Point click)
    {
        switch (hit.Key)
        {
            case "resolution": Step(hit.Bounds, click, RenderPresets.ResolutionChoices, r.Resolution, v => r.Resolution = v); break;
            case "hdr": Step(hit.Bounds, click, RenderPresets.HdrChoices, r.Hdr, v => r.Hdr = v); break;
            case "framerate": Step(hit.Bounds, click, RenderPresets.FrameRateChoices, r.FrameRate, v => r.FrameRate = v); break;
            case "power": Step(hit.Bounds, click, RenderPresets.PowerChoices, r.Power, v => r.Power = v); break;
            case "screen": Step(hit.Bounds, click, RenderPresets.ScreenChoices, r.Screen, v => r.Screen = v); break;
            case "rule-enabled": r.Enabled = !r.Enabled; RenderPresets.Save(); InvalidateVisual(); break;
            case "rule-renderer":
            {
                string current = RenderPresets.Find(r.PresetId)?.Renderer ?? "madvr";
                // Cambiando renderer si propone il suo preset "Alto" (o il primo che c'e').
                Step(hit.Bounds, click, RenderPresets.Renderers, current, v =>
                {
                    if (v == current) return;
                    r.PresetId = RenderPresets.All.FirstOrDefault(x => x.Renderer == v && x.Id == v + "-high")?.Id
                        ?? RenderPresets.All.FirstOrDefault(x => x.Renderer == v)?.Id ?? "";
                });
                break;
            }
            case "rule-preset":
            {
                string renderer = RenderPresets.Find(r.PresetId)?.Renderer ?? "madvr";
                var list = RenderPresets.All.Where(x => x.Renderer == renderer).Select(x => (x.Id, x.Name)).ToArray();
                if (list.Length > 0) Step(hit.Bounds, click, list, r.PresetId, v => r.PresetId = v);
                break;
            }
            case "rule-position":
            {
                int count = RenderPresets.Rules.Count, index = RenderPresets.Rules.ToList().IndexOf(r);
                var places = Enumerable.Range(1, count).Select(i => (i.ToString(), $"{i} di {count}")).ToArray();
                Step(hit.Bounds, click, places, (index + 1).ToString(), v => RenderPresets.MoveRule(r, int.Parse(v) - 1 - RenderPresets.Rules.ToList().IndexOf(r)));
                break;
            }
        }
    }

    private void PresetValue(RenderPresets.Preset p, Hit hit, Point click)
    {
        string tab = RenderPresets.Tab(p.Renderer);
        if (ComponentSettings.Sections(tab).SelectMany(x => x.Settings).FirstOrDefault(x => x.Key == hit.Key) is not { } setting) return;
        string stored = ComponentSettings.GetStored(tab, setting);
        var choices = new List<(string Value, string Label)> { ("", "Come nelle Impostazioni (" + ComponentSettings.ValueLabel(setting, stored) + ")") };
        if (setting.Kind == ComponentSettings.Kind.Toggle) choices.AddRange(new[] { ("1", "Attivo"), ("0", "Disattivato") });
        else if (setting.Choices != null) choices.AddRange(setting.Choices.Select(c => (c.Value, c.Label)));
        string current = p.Values.TryGetValue(setting.Key, out var v) ? v : "";
        Step(hit.Bounds, click, choices, current, value =>
        {
            if (value.Length == 0) p.Values.Remove(setting.Key); else p.Values[setting.Key] = value;
        });
    }

    private void EnsurePresetName()
    {
        if (_presetName != null || InputLayer == null) return;
        var box = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontFamily = Inter, FontSize = Px(10.5),
            Padding = new Thickness(0), MinHeight = 0, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = false,
            MaxLength = 40,
        };
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[k] = Brushes.Transparent;
        box.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Escape) { CommitPresetName(); Focus(); e.Handled = true; } };
        box.GotFocus += (_, _) => InvalidateVisual();
        box.LostFocus += (_, _) => { CommitPresetName(); InvalidateVisual(); };
        InputLayer.Children.Add(box);
        _presetName = box;
    }

    private void CommitPresetName()
    {
        if (_presetName == null || EditedPreset is not { } p) return;
        string name = (_presetName.Text ?? "").Trim();
        if (name.Length == 0 || name == p.Name) return;
        p.Name = name;
        RenderPresets.Save();
    }

    /// <summary>Il campo del nome segue il suo posto nella pagina (che scorre): fuori dalla vista si nasconde.</summary>
    private void PlacePresetName()
    {
        if (_presetName is not { } box) return;
        var r = _presetNameRect;
        bool show = IsVisible && _tab == "Preset" && _presetEdit != null && r.Width > 0 && _picker == null && _menuItems == null;
        if (show)
        {
            var onPage = r.Translate(new Vector(0, -_scroll));
            show = onPage.Y >= _viewport.Y && onPage.Bottom <= _viewport.Bottom;
            if (show)
            {
                double h = Px(10.5) * 1.6;
                Canvas.SetLeft(box, onPage.X + 2);
                Canvas.SetTop(box, onPage.Bottom - h - 6);
                box.Width = onPage.Width - 4;
                box.Height = h;
                box.Foreground = Brush(UiTheme.Text);
                box.CaretBrush = Brush(UiTheme.Text);
            }
        }
        if (box.IsVisible != show) box.IsVisible = show;
        box.Opacity = _tabTransition.Reveal;
    }
}
