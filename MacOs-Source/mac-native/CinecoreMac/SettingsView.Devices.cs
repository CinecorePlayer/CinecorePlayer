using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Impostazioni › Dispositivi come su Windows (SettingsHudPage.Devices.cs): l'amplificatore via rete (stato, ricerca,
// dispositivo, protocollo, indirizzo e porta, uscita audio collegata, scollega), poi Telefono e collegamenti (telefono,
// luci WLED) e Servizi. "Verifica" e la ricerca LEGGONO soltanto: da questa pagina non parte nessun comando di volume
// (regole del volume del ricevitore: amplifier-volume-safety).
internal sealed partial class SettingsView
{
    /// <summary>Servizi che sono schede a parte (telefono, WLED, Trakt, ascolti).</summary>
    public event Action<string>? DeviceServiceRequested;
    public event Action? AmplifierChanged;

    private AmplifierDevice? _amp;
    private bool _ampLoaded, _ampBusy;
    private string _ampStatus = "";
    private int _ampStatusKind; // 0 neutro, 1 in corso, 2 ok, 3 errore
    private readonly List<AmplifierDevice> _ampFound = new();
    private TextBox? _ampAddress, _ampPort;
    private bool _ampInputsQueued;
    private Rect _ampAddressRect, _ampPortRect;
    private CancellationTokenSource? _ampCts;

    private static readonly string[] AmpProtocolNames =
        { "Denon / Marantz", "Yamaha MusicCast", "Onkyo / Integra / Pioneer", "UPnP / DLNA", "NAD", "Anthem" };

    private void EnsureAmplifierLoaded()
    {
        if (_ampLoaded) return;
        _ampLoaded = true;
        _amp = AmplifierControl.Load();
        if (_amp != null) _ = VerifyAmplifierAsync();
        else SetAmpStatus("Nessun ricevitore collegato. Cercalo in rete o inserisci il suo indirizzo.", 0);
    }

    private void SetAmpStatus(string text, int kind)
    {
        _ampStatus = text;
        _ampStatusKind = kind;
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private void SaveAmplifier(AmplifierDevice device)
    {
        try
        {
            using var validation = new AmplifierControl(device);
            AmplifierControl.Save(device);
            _amp = device;
            SyncAmpInputs();
            AmplifierChanged?.Invoke();
            _ = VerifyAmplifierAsync();
        }
        catch (Exception ex) { SetAmpStatus(ex.Message, 3); }
    }

    private void RemoveAmplifier()
    {
        try { AmplifierControl.Clear(); } catch { }
        _amp = null;
        SyncAmpInputs();
        AmplifierChanged?.Invoke();
        SetAmpStatus("Ricevitore scollegato: il volume torna a quello del Mac.", 0);
    }

    private async Task VerifyAmplifierAsync()
    {
        var device = _amp;
        if (device == null || _ampBusy) return;
        _ampBusy = true;
        SetAmpStatus("Connessione in corso…", 1);
        _ampCts?.Cancel();
        var cts = _ampCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var client = new AmplifierControl(device);
            var state = await client.ReadAsync(cts.Token).ConfigureAwait(false);
            string output = string.IsNullOrEmpty(device.AudioEndpointId)
                ? " · scegli l’uscita audio collegata: altrimenti il volume del player non lo comanda."
                : "";
            SetAmpStatus($"Collegato · volume {state.Volume}{(state.Muted ? " · muto" : "")}" + output, output.Length == 0 ? 2 : 3);
        }
        catch (Exception ex)
        {
            SetAmpStatus(ex is OperationCanceledException
                ? "Nessuna risposta: controlla indirizzo, protocollo e che il controllo di rete sia attivo sul ricevitore."
                : ex.Message, 3);
        }
        finally { _ampBusy = false; Dispatcher.UIThread.Post(InvalidateVisual); }
    }

    private async Task DiscoverAmplifiersAsync()
    {
        if (_ampBusy) return;
        _ampBusy = true;
        SetAmpStatus("Cerco i ricevitori nella rete…", 1);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var found = await AmplifierControl.DiscoverAsync(cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => { _ampFound.Clear(); _ampFound.AddRange(found); });
            SetAmpStatus(found.Count == 0 ? "Nessun ricevitore trovato: attiva il controllo di rete sul ricevitore o inserisci l’indirizzo."
                : found.Count == 1 ? "1 ricevitore trovato: sceglilo in Dispositivo."
                : $"{found.Count} ricevitori trovati: scegline uno in Dispositivo.", found.Count == 0 ? 3 : 0);
        }
        catch (Exception ex) { SetAmpStatus(ex is OperationCanceledException ? "Ricerca terminata senza risultati." : ex.Message, 3); }
        finally { _ampBusy = false; Dispatcher.UIThread.Post(InvalidateVisual); }
    }

    // ----- caselle di indirizzo e porta (TextBox vere sopra la pagina, come le credenziali) -----
    private TextBox AmpInput(string watermark)
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontFamily = Inter, FontSize = Px(10.5),
            Padding = new Thickness(0), MinHeight = 0, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = false,
            Watermark = watermark,
        };
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[k] = Brushes.Transparent;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitAmplifierText(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SyncAmpInputs(force: true); Focus(); e.Handled = true; }
        };
        box.GotFocus += (_, _) => InvalidateVisual();
        box.LostFocus += (_, _) => { CommitAmplifierText(); InvalidateVisual(); };
        InputLayer?.Children.Add(box);
        return box;
    }

    private void SyncAmpInputs(bool force = false)
    {
        if (_ampAddress != null && (force || !_ampAddress.IsFocused)) _ampAddress.Text = _amp?.Address ?? "";
        if (_ampPort != null && (force || !_ampPort.IsFocused)) _ampPort.Text = (_amp?.Port ?? 0) == 0 ? "" : _amp!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void CommitAmplifierText()
    {
        string address = _ampAddress?.Text?.Trim() ?? "";
        string portText = _ampPort?.Text?.Trim() ?? "";
        int port = 0;
        if (portText.Length > 0 && (!int.TryParse(portText, out port) || port < 0 || port > 65535))
        {
            SetAmpStatus("Porta non valida (0–65535). Vuota = automatica.", 3);
            return;
        }
        if (address.Length == 0) return;
        var current = _amp ?? new AmplifierDevice(address, address, AmplifierProtocol.DenonMarantz);
        if (_amp != null && string.Equals(current.Address, address, StringComparison.OrdinalIgnoreCase) && current.Port == port) return;
        SaveAmplifier(current with
        {
            Name = string.Equals(current.Address, address, StringComparison.OrdinalIgnoreCase) ? current.Name : address,
            Address = address, Port = port,
        });
    }

    private void PlaceAmpInputs()
    {
        bool page = IsVisible && _tab == "Dispositivi" && _picker == null && _menuItems == null;
        foreach (var (box, rect) in new[] { (_ampAddress, _ampAddressRect), (_ampPort, _ampPortRect) })
        {
            if (box == null) continue;
            bool show = page && rect.Width > 0;
            if (show)
            {
                var onPage = rect.Translate(new Vector(0, -_scroll));
                show = onPage.Y >= _viewport.Y && onPage.Bottom <= _viewport.Bottom;
                if (show)
                {
                    double h = Px(10.5) * 1.6;
                    Canvas.SetLeft(box, onPage.X);
                    Canvas.SetTop(box, onPage.Bottom - h - 6);
                    box.Width = onPage.Width;
                    box.Height = h;
                    box.Foreground = Brush(UiTheme.Text);
                    box.CaretBrush = Brush(UiTheme.Text);
                    box.Resources["TextControlPlaceholderForeground"] = Brush(A(UiTheme.Muted, 120));
                    box.Resources["TextControlPlaceholderForegroundFocused"] = Brush(A(UiTheme.Muted, 120));
                }
            }
            if (box.IsVisible != show) box.IsVisible = show;
            box.Opacity = _tabTransition.Reveal;
        }
    }

    // ----- disegno -----
    private double DrawDevicesRows(DrawingContext ctx, Rect body)
    {
        EnsureAmplifierLoaded();
        double width = body.Width;
        double controlW = Math.Min(330, Math.Max(210, Math.Floor(width * 2 / 5)));
        double controlX = body.X + width - controlW;
        double y = body.Y;
        double labelW = Math.Max(90, controlX - body.X - 28);
        void Section(string title)
        {
            ctx.DrawLine(new Pen(Brush(A(UiTheme.Border, 50)), 1), new Point(body.X, y + .5), new Point(body.X + width, y + .5));
            Text(ctx, title.ToUpperInvariant(), 9, true, new Rect(body.X, y + 20, width, 24), UiTheme.Muted);
            y += 56;
        }
        Rect Row(string title, string description = "")
        {
            double h = description.Length > 0 ? 66 : 56;
            Text(ctx, title, 10.5, false, new Rect(body.X, y, labelW, 30), UiTheme.Text);
            if (description.Length > 0) h += RowDescription(ctx, title, description, new Rect(body.X, y + 29, labelW, 23));
            var r = new Rect(controlX, y + 1, controlW, 36);
            y += h;
            return r;
        }
        void Choice(Rect r, string text, string key)
        {
            DrawValueChip(ctx, r, text, Hover(r));
            _hits.Add(new Hit(r, "device", key));
        }
        void Action(Rect r, string label, string key)
        {
            double w = Math.Min(r.Width, Measure(label, 9.8, true) + 40);
            var button = new Rect(r.Right - w, r.Y, w, r.Height);
            DrawTextAction(ctx, button, label);
            _hits.Add(new Hit(button, "device", key));
        }

        // ----- Amplificatore -----
        Section("Amplificatore via rete");
        {
            // Stato come le altre righe: titolo, una riga di spiegazione sotto (rossa se c'e' un problema), azione a destra.
            string title = _ampBusy ? (_amp != null && _ampStatusKind == 1 && _ampStatus.StartsWith("Connessione", StringComparison.Ordinal) ? "Verifica in corso…" : "Ricerca in corso…")
                : _amp == null ? "Nessun ricevitore collegato" : _ampStatusKind == 2 ? "Collegato" : _ampStatusKind == 3 ? "Non raggiungibile" : _amp.Name;
            string detail = _ampBusy ? (_ampStatus.StartsWith("Cerco", StringComparison.Ordinal) ? "Fino a 12 secondi: Denon, Marantz, Yamaha, Onkyo, NAD, Anthem e UPnP." : _amp?.Address ?? "")
                : _ampStatusKind == 2 && _ampStatus.StartsWith("Collegato · ", StringComparison.Ordinal) ? _ampStatus["Collegato · ".Length..] : _ampStatus;
            Text(ctx, title, 10.5, false, new Rect(body.X, y, labelW, 30), _ampStatusKind == 3 && !_ampBusy ? Danger : UiTheme.Text);
            ctx.DrawText(Format(detail, Px(9), Regular, _ampStatusKind == 3 && !_ampBusy ? A(Danger, 220) : UiTheme.Muted, labelW), new Point(body.X, y + 33));
            var r = new Rect(controlX, y + 1, controlW, 36);
            if (_ampBusy) Text(ctx, "Attendi…", 9.8, true, new Rect(r.Right - 120, r.Y, 120, r.Height), A(UiTheme.Text, 120), TextAlignment.Right);
            else Action(r, _amp != null ? "Verifica connessione" : "Cerca in rete", _amp != null ? "amp-verify" : "amp-scan");
            double detailH = Format(detail, Px(9), Regular, UiTheme.Muted, labelW).Height;
            y += Math.Max(66, 40 + detailH);
        }
        Choice(Row("Dispositivo", "I ricevitori trovati nella rete."), _amp?.Name is { Length: > 0 } name ? name : "Scegli o cerca", "amp-device");
        Choice(Row("Protocollo", "Come il player parla con il ricevitore."), AmpProtocolNames[(int)(_amp?.Protocol ?? AmplifierProtocol.DenonMarantz)], "amp-protocol");

        // Le caselle vere si creano fuori dal disegno (cambiare controlli dentro Render fa cadere Avalonia).
        if (_ampAddress == null && InputLayer != null && !_ampInputsQueued)
        {
            _ampInputsQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _ampAddress ??= AmpInput("192.168.1.20");
                _ampPort ??= AmpInput("Porta");
                SyncAmpInputs(force: true);
                PlaceAmpInputs();
            });
        }
        {
            Text(ctx, "Indirizzo", 10.5, false, new Rect(body.X, y, labelW, 30), UiTheme.Text);
            Text(ctx, "IP del ricevitore o URL UPnP. Porta vuota = automatica.", 9, false, new Rect(body.X, y + 29, labelW, 23), UiTheme.Muted);
            double portW = 84, gap = 14;
            _ampAddressRect = new Rect(controlX, y, controlW - portW - gap, 38);
            _ampPortRect = new Rect(controlX + controlW - portW, y, portW, 38);
            foreach (var (rect, box) in new[] { (_ampAddressRect, _ampAddress), (_ampPortRect, _ampPort) })
            {
                bool focused = box?.IsFocused == true;
                ctx.DrawLine(new Pen(Brush(focused ? UiTheme.Accent : A(UiTheme.Border, 110)), focused ? 2 : 1), new Point(rect.X, rect.Bottom - 1), new Point(rect.Right, rect.Bottom - 1));
            }
            y += 66;
        }
        string outputName = string.IsNullOrEmpty(_amp?.AudioEndpointId) ? "Nessuna"
            : MacAudioDevices.Outputs().FirstOrDefault(o => o.Uid == _amp!.AudioEndpointId)?.Name ?? "Uscita non collegata";
        Choice(Row("Uscita audio collegata", "Quando suona da qui, il volume del player comanda il ricevitore."), outputName, "amp-output");
        if (_amp != null)
        {
            Action(Row("Scollega il ricevitore", "Il volume torna a quello del Mac."), "Scollega", "amp-remove");
            Action(Row("Altri ricevitori"), _ampBusy ? "Attendi…" : "Cerca in rete", "amp-scan");
        }
        y += 12;

        // ----- Collegamenti -----
        Section("Telefono e collegamenti");
        Action(Row("Telefono come telecomando", "Inquadra il codice con il telefono sulla stessa rete."), "Abbina il telefono", "pair");
        Action(Row("Dispositivi e automazioni", "Luci, prese e scene che seguono il film."), "Configura", "automations");
        Action(Row("Luci WLED", "Si spengono quando parte il film e tornano com’erano in pausa e alla fine."), "Configura", "wled");
        y += 12;
        Section("Servizi");
        Action(Row("Trakt", "Cronologia e consigli per te."), "Apri", "trakt");
        Action(Row("Last.fm e ListenBrainz", "Registra gli ascolti della musica."), "Apri", "scrobble");
        return y - body.Y + 40;
    }

    /// <summary>Azione di testo a destra della riga (come "Configura"): apre una scheda della finestra.</summary>
    private void DeviceButton(DrawingContext ctx, Rect r, string label, string key)
    {
        double w = Math.Min(r.Width, Measure(label, 9.8, true) + 40);
        var button = new Rect(r.Right - w, r.Y, w, r.Height);
        DrawTextAction(ctx, button, label);
        _hits.Add(new Hit(button, "device", key));
    }

    /// <summary>Le azioni del ricevitore; false se la chiave e' di un servizio (la apre la finestra).</summary>
    private bool DeviceAction(string key, Rect source)
    {
        switch (key)
        {
            case "amp-verify": _ = VerifyAmplifierAsync(); return true;
            case "amp-scan": _ = DiscoverAmplifiersAsync(); return true;
            case "amp-remove": RemoveAmplifier(); return true;
            case "amp-device":
            {
                var list = _ampFound.ToList();
                if (_amp != null && !list.Any(d => string.Equals(d.Address, _amp.Address, StringComparison.OrdinalIgnoreCase))) list.Insert(0, _amp);
                var options = list.Select(device => (device.Name + "  ·  " + device.Address,
                    _amp != null && string.Equals(device.Address, _amp.Address, StringComparison.OrdinalIgnoreCase),
                    (Action)(() =>
                    {
                        // Uscita e porta gia' scelte restano se e' lo stesso ricevitore.
                        var keep = _amp != null && string.Equals(_amp.Address, device.Address, StringComparison.OrdinalIgnoreCase) ? _amp : null;
                        SaveAmplifier(device with { Port = keep?.Port ?? device.Port, AudioEndpointId = keep?.AudioEndpointId ?? device.AudioEndpointId ?? GuessAudioOutput(device) });
                    }))).ToList();
                options.Add(("Cerca in rete", false, () => _ = DiscoverAmplifiersAsync()));
                ShowChoices(source, options);
                return true;
            }
            case "amp-protocol":
                ShowChoices(source, AmpProtocolNames.Select((label, i) => (label, _amp?.Protocol == (AmplifierProtocol)i, (Action)(() =>
                {
                    if (_amp == null) { SetAmpStatus("Prima scegli un ricevitore o scrivi il suo indirizzo.", 3); return; }
                    SaveAmplifier(_amp with { Protocol = (AmplifierProtocol)i });
                }))));
                return true;
            case "amp-output":
            {
                var options = new List<(string, bool, Action)>
                {
                    ("Nessuna", string.IsNullOrEmpty(_amp?.AudioEndpointId), () => { if (_amp != null) SaveAmplifier(_amp with { AudioEndpointId = null }); }),
                };
                foreach (var output in MacAudioDevices.Outputs())
                {
                    string uid = output.Uid;
                    options.Add((output.Name, _amp?.AudioEndpointId == uid, () =>
                    {
                        if (_amp == null) { SetAmpStatus("Prima scegli un ricevitore o scrivi il suo indirizzo.", 3); return; }
                        SaveAmplifier(_amp with { AudioEndpointId = uid });
                    }));
                }
                ShowChoices(source, options);
                return true;
            }
        }
        return false;
    }

    /// <summary>Uscita audio con il nome del ricevitore ("Marantz SR7010"), se c'e'.</summary>
    private static string? GuessAudioOutput(AmplifierDevice d)
    {
        string[] brands = d.Protocol switch
        {
            AmplifierProtocol.DenonMarantz => new[] { "denon", "marantz" },
            AmplifierProtocol.YamahaMusicCast => new[] { "yamaha" },
            AmplifierProtocol.OnkyoIntegraPioneer => new[] { "onkyo", "integra", "pioneer" },
            AmplifierProtocol.Anthem => new[] { "anthem" },
            _ => Array.Empty<string>(),
        };
        var words = brands.Concat(d.Name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 5 && !System.Net.IPAddress.TryParse(word, out _))).ToArray();
        return MacAudioDevices.Outputs().FirstOrDefault(o => words.Any(word => o.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))?.Uid;
    }
}
