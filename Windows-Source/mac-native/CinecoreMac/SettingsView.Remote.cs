using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CinecoreMac;

/// <summary>
/// Le schede del player (Generali, Dispositivi, Cinecore Audio) sul telefono. Non c'e' una seconda copia delle voci:
/// la scheda si "disegna" in una bitmap minuscola e intanto annota sezioni, righe e controlli; un tocco sul telefono
/// esegue lo stesso clic del PC (i menu a scelta si leggono senza aprirli). Una voce nuova nel PC arriva da sola.
/// </summary>
internal sealed partial class SettingsView
{
    internal static readonly string[] RemoteTabs = { "Generali", "Dispositivi", "Cinecore Audio" };

    /// <summary>Scelte che sul PC si cambiano a passi (frecce ai lati): sul telefono "‹ valore ›".</summary>
    private static readonly HashSet<string> StepKeys = new(StringComparer.Ordinal)
    {
        "renderer", "madvr-lowpower", "theme", "mpv-runtime", "sizing-mode", "sizing-transition", "sizing-bias",
    };
    /// <summary>Scelte che aprono una finestra sul PC: sul telefono un'azione.</summary>
    private static readonly HashSet<string> PcActionKeys = new(StringComparer.Ordinal) { "disc-protection", "keychain-authorize" };

    private sealed class RemoteItem
    {
        public string Hit = "", Key = "", Kind = "", Label = "", Description = "", Value = "", Text = "";
        public List<(string Value, string Label)>? Choices;
        public string Id => Hit + "|" + Key;
    }

    private sealed class RemoteRecorder
    {
        public readonly List<(string Title, List<RemoteItem> Items)> Sections = new();
        private string _title = "", _description = "";
        private List<RemoteItem> Current
        {
            get { if (Sections.Count == 0) Sections.Add(("", new List<RemoteItem>())); return Sections[^1].Items; }
        }
        public void Section(string title) => Sections.Add((title, new List<RemoteItem>()));
        public void Row(string title, string description) { _title = title; _description = description; }
        private void Add(RemoteItem item) { item.Label = _title; item.Description = _description; Current.Add(item); }
        public void Choice(string hit, string key, string text)
        {
            string kind = StepKeys.Contains(key) || key.StartsWith("sizing-preset-", StringComparison.Ordinal) ? "step"
                : PcActionKeys.Contains(key) ? "action" : key == "dvd-playback" ? "info" : "choice";
            Add(new RemoteItem { Hit = hit, Key = key, Kind = kind, Value = text, Text = text });
        }
        public void Toggle(string hit, string key, bool value) => Add(new RemoteItem { Hit = hit, Key = key, Kind = "toggle", Value = value ? "1" : "0" });
        public void Action(string hit, string key, string label) => Add(new RemoteItem { Hit = hit, Key = key, Kind = "action", Text = label });
        public void Info(string text) => Add(new RemoteItem { Kind = "info", Key = "info" + Current.Count, Text = text });
        public void Colors(string hit, IEnumerable<(string Color, bool Chosen)> colors)
        {
            var list = colors.ToList();
            // Etichetta = colore per il browser (#rrggbb: "#ffrrggbb" di Avalonia il CSS lo legge come RGBA, colori sbagliati).
            static string Css(string value) => Avalonia.Media.Color.TryParse(value, out var c) ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : value;
            Add(new RemoteItem { Hit = hit, Key = hit, Kind = "colors", Value = list.FirstOrDefault(c => c.Chosen).Color ?? "", Choices = list.Select(c => (c.Color, Css(c.Color))).ToList() });
        }
    }

    private RemoteRecorder? _rec;
    private List<(string Label, bool Selected, Action Select)>? _capture;

    private List<(string Title, List<RemoteItem> Items)> RecordTab(string tab)
    {
        var saved = _hits.ToList();
        var (address, port) = (_ampAddressRect, _ampPortRect);
        var rec = new RemoteRecorder();
        _rec = rec;
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(4, 4));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                var body = new Rect(0, 0, 900, 100000);
                if (tab == "Generali") DrawGeneralRows(ctx, body);
                else if (tab == "Dispositivi") DrawDevicesRows(ctx, body);
                else if (tab == "Cinecore Audio") DrawAudioRows(ctx, body);
            }
        }
        finally
        {
            _rec = null;
            _hits.Clear(); _hits.AddRange(saved);
            (_ampAddressRect, _ampPortRect) = (address, port);
        }
        // Le scelte a menu: le stesse voci del menu del PC, lette senza aprirlo.
        foreach (var item in rec.Sections.SelectMany(s => s.Items).Where(i => i.Kind == "choice"))
        {
            var options = Capture(item);
            item.Choices = options.Select((o, i) => (i.ToString(CultureInfo.InvariantCulture), o.Label)).ToList();
            int chosen = options.FindIndex(o => o.Selected);
            item.Value = chosen >= 0 ? chosen.ToString(CultureInfo.InvariantCulture) : "";
            if (options.Count == 0) item.Kind = "info";
        }
        return rec.Sections.Where(s => s.Items.Count > 0).ToList();
    }

    private List<(string Label, bool Selected, Action Select)> Capture(RemoteItem item)
    {
        var list = new List<(string Label, bool Selected, Action Select)>();
        _capture = list;
        try { Activate(new Hit(new Rect(0, 0, 200, 36), item.Hit, item.Key), new Point(150, 10)); }
        finally { _capture = null; }
        return list;
    }

    /// <summary>La scheda per il telefono, nello stesso formato dei componenti avanzati.</summary>
    internal object RemoteProvider(string tab) => new
    {
        id = tab,
        title = L10n.T(tab),
        note = "",
        available = true,
        kind = "player",
        sections = RecordTab(tab).Select(section => (object)new
        {
            group = "",
            title = L10n.T(section.Title),
            settings = section.Items.Select(i => new
            {
                key = i.Id,
                label = L10n.T(i.Label),
                description = L10n.T(i.Description),
                kind = i.Kind,
                action = i.Kind is "action" or "step" or "info" ? L10n.T(i.Text) : null,
                reload = true,
                value = i.Value,
                choices = i.Choices?.Select(c => new { value = c.Value, label = i.Kind == "colors" ? c.Label : L10n.T(c.Label) }).ToArray(),
            }).ToArray(),
        }).ToArray(),
    };

    /// <summary>Tocco dal telefono: lo stesso clic della scheda del PC.</summary>
    internal object RemoteApply(string tab, string id, string value)
    {
        var item = RecordTab(tab).SelectMany(s => s.Items).FirstOrDefault(i => i.Id == id);
        if (item == null) return new { ok = false, error = "unknown setting", reload = true };
        var hit = new Hit(new Rect(0, 0, 200, 36), item.Hit, item.Key);
        switch (item.Kind)
        {
            case "toggle":
                if ((item.Value == "1") != (value == "1")) Activate(hit, new Point(150, 10));
                break;
            case "choice":
                var options = Capture(item);
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= options.Count)
                    return new { ok = false, error = "bad choice", reload = true };
                options[index].Select();
                break;
            case "step":
                Activate(hit, new Point(value == "-1" ? 5 : 150, 10));
                break;
            case "action":
                Activate(hit, new Point(150, 10));
                break;
            case "colors":
                if (item.Choices?.Any(c => c.Value == value) != true) return new { ok = false, error = "bad colour", reload = true };
                Activate(new Hit(default, item.Hit, value), default);
                break;
            default:
                return new { ok = false, error = "read only", reload = true };
        }
        InvalidateVisual();
        return new { ok = true, value, reload = true, error = (string?)null };
    }
}
