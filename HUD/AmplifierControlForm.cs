#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using NAudio.CoreAudioApi;

namespace CinecorePlayer2025;

/// <summary>
/// Network amplifier link: one clean column, captions above box-less fields, the status
/// as a single line with a coloured dot, secondary actions as text links.
/// </summary>
internal sealed class AmplifierControlForm : HudModalFormBase
{
    private readonly ChoiceField _devices = new() { Underline = true };
    private readonly ChoiceField _protocol = new() { Underline = true };
    private readonly TextBox _address = new() { BorderStyle = BorderStyle.None, PlaceholderText = "192.168.1.20" };
    private readonly TextBox _port = new() { BorderStyle = BorderStyle.None, Text = "0" };
    private readonly Label _status = new() { TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly CancellationTokenSource _lifetime = new();
    private AmplifierControl? _client;
    private AmplifierStatus? _state;
    private readonly ChoiceField _output = new() { Underline = true };
    private sealed record Endpoint(string Id, string Name) { public override string ToString() => Name; }
    private bool _busy;
    private bool _confirmedNoOutput;
    private readonly bool _english;
    private enum StatusKind { Idle, Busy, Ok, Error }
    private StatusKind _statusKind = StatusKind.Idle;
    private string L(string it, string en) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? en : it);

    public AmplifierControlForm(bool english)
    {
        _english = english;
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L("Amplificatore di rete", "Network amplifier");
        ClientSize = new Size(640, 560); MinimumSize = new Size(560, 560);
        Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10);
        BackColor = Theme.Sheet;

        Label Caption(string text) => new() { Text = text.Length > 1 ? char.ToUpper(text[0]) + text[1..].ToLowerInvariant().Replace("ip", "IP").Replace("url", "URL").Replace("upnp", "UPnP") : text, AutoSize = false, ForeColor = Theme.Muted, Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f), TextAlign = ContentAlignment.BottomLeft };
        Label Link(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Accent, Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f), Cursor = Cursors.Hand };

        var title = new Label { Text = L("Amplificatore di rete", "Network amplifier"), AutoSize = false, ForeColor = Theme.Text, Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f) };
        var subtitle = new Label { Text = L("Volume e mute del ricevitore collegato, direttamente dal player.", "Volume and mute of the connected receiver, right from the player."), AutoSize = false, ForeColor = Theme.Muted };
        var deviceCaption = Caption(L("Dispositivo", "Device"));
        var scan = Link(L("Cerca in rete", "Search network"));
        var protocolCaption = Caption(L("Protocollo di controllo", "Control protocol"));
        var addressCaption = Caption(L("Indirizzo IP o URL UPnP", "IP address or UPnP URL"));
        var portCaption = Caption(L("Porta, 0 = auto", "Port, 0 = auto"));
        var outputCaption = Caption(L("Uscita audio associata", "Linked audio output"));
        var verify = Link(L("Verifica connessione", "Test connection"));
        var close = CreateModalButton(L("Annulla", "Cancel"), DialogResult.Cancel, false);
        var save = CreateModalButton(L("Salva", "Save"), DialogResult.None, true);
        var addressField = Underlined(_address);
        var portField = Underlined(_port);

        _protocol.Items.AddRange(new object[] { "Denon / Marantz", "Yamaha MusicCast", "Onkyo / Integra / Pioneer (eISCP)", "UPnP / DLNA RenderingControl", "NAD (Ethernet V2)", "Anthem (MRX SLM)" }); _protocol.SelectedIndex = 0;
        _output.Items.Add(new Endpoint("", L("Nessuna, controllo disattivato", "None, control disabled")));
        _output.SelectedIndex = 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (endpoint) _output.Items.Add(new Endpoint(endpoint.ID, endpoint.FriendlyName));
        }
        catch { }

        foreach (Control control in new Control[] { _devices, _protocol, _output, _address, _port })
        { control.BackColor = Theme.Sheet; control.ForeColor = Theme.Text; control.Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.5f); }
        _status.ForeColor = Theme.Muted;
        _status.Paint += (_, e) =>
        {
            // Status dot drawn in the label's left padding.
            Color dot = _statusKind switch { StatusKind.Ok => Color.FromArgb(72, 199, 142), StatusKind.Error => Theme.Danger, StatusKind.Busy => Theme.Accent, _ => Theme.Muted };
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(dot);
            int d = (int)(8 * DeviceDpi / 96f);
            e.Graphics.FillEllipse(brush, 0, (_status.Height - d) / 2, d, d);
        };
        _status.Padding = new Padding(18, 0, 0, 0);

        Controls.AddRange(new Control[] { title, subtitle, deviceCaption, scan, _devices, protocolCaption, _protocol, addressCaption, addressField, portCaption, portField, outputCaption, _output, _status, verify, close, save });
        CancelButton = close;

        void LayoutAll()
        {
            float k = DeviceDpi / 96f;
            int U(int v) => (int)Math.Round(v * k);
            int pad = U(40), w = ClientSize.Width - pad * 2, y = U(34);
            title.SetBounds(pad, y, w - U(40), U(36)); y += U(38);
            subtitle.SetBounds(pad, y, w, U(22)); y += U(44);
            void Field(Label caption, Control field, int x, int width)
            {
                caption.SetBounds(x, y, width, U(18));
                field.SetBounds(x, y + U(22), width, U(34));
            }
            Field(deviceCaption, _devices, pad, w);
            scan.Location = new Point(pad + w - scan.Width, y - U(1));
            // La didascalia occupava tutta la riga e stava sopra al link: il clic su "Cerca in rete" finiva su di lei.
            deviceCaption.Width = Math.Max(U(60), scan.Left - pad - U(12));
            scan.BringToFront();
            y += U(76);
            Field(protocolCaption, _protocol, pad, w); y += U(76);
            int portW = U(130), gap = U(24);
            Field(addressCaption, addressField, pad, w - portW - gap);
            Field(portCaption, portField, pad + w - portW, portW); y += U(76);
            Field(outputCaption, _output, pad, w); y += U(78);
            _status.SetBounds(pad, y, w, U(40));
            int footer = ClientSize.Height - U(30) - U(38);
            save.SetBounds(pad + w - U(112), footer, U(112), U(38));
            close.SetBounds(save.Left - U(10) - U(112), footer, U(112), U(38));
            verify.Location = new Point(pad, footer + (U(38) - verify.Height) / 2);
        }
        Layout += (_, _) => LayoutAll();
        DpiChanged += (_, _) => LayoutAll();

        void SetStatus(string text, StatusKind kind) { _status.Text = text; _statusKind = kind; _status.Invalidate(); }
        SetStatus(L("Cerca il ricevitore in rete oppure inserisci IP e protocollo.", "Search for the receiver or enter its IP and protocol."), StatusKind.Idle);

        void ResetConnection() { if (_busy) return; _client?.Dispose(); _client = null; _state = null; SetStatus(L("Verifica la connessione per leggere lo stato del dispositivo.", "Test the connection to read the device status."), StatusKind.Idle); }
        _address.TextChanged += (_, _) => ResetConnection(); _port.TextChanged += (_, _) => ResetConnection(); _protocol.SelectedIndexChanged += (_, _) => ResetConnection();
        _devices.SelectedIndexChanged += (_, _) => { if (_devices.SelectedItem is AmplifierDevice device) LoadDevice(device); };

        async Task Verify()
        {
            if (_busy) return; _busy = true;
            _address.Enabled = _protocol.Enabled = _port.Enabled = _devices.Enabled = false;
            SetStatus(L("Connessione in corso…", "Connecting…"), StatusKind.Busy);
            try
            {
                _client?.Dispose(); _client = new AmplifierControl(Current());
                _state = await _client.ReadAsync(_lifetime.Token);
                if (!IsDisposed)
                    SetStatus(string.IsNullOrEmpty((_output.SelectedItem as Endpoint)?.Id)
                        ? L($"Connesso, volume {_state.Volume}. Associa l’uscita audio e salva.", $"Connected, volume {_state.Volume}. Link the audio output and save.")
                        : L($"Connesso, volume {_state.Volume}{(_state.Muted ? ", mute" : "")}.", $"Connected, volume {_state.Volume}{(_state.Muted ? ", muted" : "")}."), StatusKind.Ok);
            }
            catch (Exception ex)
            {
                _state = null;
                if (!IsDisposed) SetStatus(ex is OperationCanceledException ? L("Nessuna risposta: controlla IP, protocollo e controllo di rete.", "No response: check IP, protocol and network control.") : ex.Message, StatusKind.Error);
            }
            finally { _busy = false; if (!IsDisposed) _address.Enabled = _protocol.Enabled = _port.Enabled = _devices.Enabled = true; }
        }
        verify.Click += async (_, _) => await Verify();
        AcceptButton = save;
        save.Click += (_, _) =>
        {
            if (_busy) return;
            try
            {
                var device = Current();
                using var validation = new AmplifierControl(device);
                // Senza uscita associata il controllo resta spento: si salva solo dopo una conferma esplicita.
                if (string.IsNullOrEmpty(device.AudioEndpointId) && !_confirmedNoOutput)
                {
                    _confirmedNoOutput = true;
                    SetStatus(L("Scegli l’uscita audio collegata all’ampli: senza, il volume del player non lo comanda. Salva di nuovo per lasciarla vuota.", "Choose the audio output wired to the receiver: without it the player's volume does not control it. Save again to leave it empty."), StatusKind.Error);
                    return;
                }
                AmplifierControl.Save(device); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
        };
        async Task Discover()
        {
            if (_busy) return; _busy = true; SetStatus(L("Ricerca in rete…", "Searching the network…"), StatusKind.Busy);
            try
            {
                var devices = await AmplifierControl.DiscoverAsync(_lifetime.Token);
                if (IsDisposed) return;
                _devices.Items.Clear();
                foreach (var device in devices) _devices.Items.Add(device);
                if (devices.Count > 0)
                {
                    // Un ricevitore gia' salvato resta selezionato, con la sua porta e la sua uscita audio.
                    var known = AmplifierControl.Load();
                    int index = known == null ? 0 : Math.Max(0, devices.FindIndex(d => string.Equals(d.Address, known.Address, StringComparison.OrdinalIgnoreCase)));
                    var chosen = known != null && string.Equals(devices[index].Address, known.Address, StringComparison.OrdinalIgnoreCase)
                        ? devices[index] with { Port = known.Port, AudioEndpointId = known.AudioEndpointId }
                        : devices[index];
                    _devices.Items[index] = chosen;
                    _devices.SelectedIndex = index;
                    LoadDevice(chosen);
                    SetStatus(L($"{devices.Count} dispositivi trovati. Verifica quello selezionato.", $"{devices.Count} devices found. Test the selected one."), StatusKind.Idle);
                }
                else
                    SetStatus(L("Nessun ricevitore trovato: attiva il controllo di rete o inserisci l’IP.", "No receiver found: enable network control or enter the IP."), StatusKind.Error);
            }
            catch (Exception ex) { if (!IsDisposed) SetStatus(ex is OperationCanceledException ? L("Ricerca terminata.", "Search ended.") : ex.Message, StatusKind.Error); }
            finally { _busy = false; }
        }
        scan.Click += async (_, _) => await Discover();
        if (AmplifierControl.Load() is { } saved)
        {
            // Il ricevitore salvato compare nel campo Dispositivo (prima restava vuoto e sembrava
            // non memorizzato) e lo stato si legge subito, senza dover premere Verifica.
            _devices.Items.Add(saved);
            _devices.SelectedIndex = 0;
            LoadDevice(saved);
            Shown += (_, _) => BeginInvoke(new Action(async () => await Verify()));
        }
        else
            Shown += (_, _) => BeginInvoke(new Action(async () => await Discover()));
        FormClosed += (_, _) => { _lifetime.Cancel(); _client?.Dispose(); };
    }

    private Panel Underlined(TextBox text)
    {
        var panel = new Panel { BackColor = Theme.Sheet };
        panel.Controls.Add(text); text.AutoSize = false;
        // Campo: una superficie morbida arrotondata, senza sottolineature ne' bordi; un filo d'accento solo con il fuoco.
        text.BackColor = Theme.SheetRaised;
        panel.HandleCreated += (_, _) => text.BackColor = Theme.SheetRaised; // il colore del campo viene impostato anche altrove, prima
        void LayoutInput() { text.SetBounds(12, Math.Max(0, (panel.Height - text.Font.Height) / 2), Math.Max(1, panel.Width - 24), text.Font.Height + 1); }
        panel.Resize += (_, _) => LayoutInput(); text.FontChanged += (_, _) => LayoutInput();
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var shape = ModalActionButton.Rounded(new Rectangle(0, 0, panel.Width - 1, panel.Height - 1), 8);
            using var fill = new SolidBrush(Theme.SheetRaised); e.Graphics.FillPath(fill, shape);
            if (text.Focused) { using var ring = new Pen(Color.FromArgb(150, Theme.Accent)); e.Graphics.DrawPath(ring, shape); }
        };
        text.GotFocus += (_, _) => panel.Invalidate(); text.LostFocus += (_, _) => panel.Invalidate();
        panel.Click += (_, _) => text.Focus();
        return panel;
    }

    private AmplifierDevice Current()
    {
        if (!int.TryParse(_port.Text, out int port) || port < 0 || port > 65535) throw new ArgumentException(L("Porta non valida (0–65535).", "Invalid port (0–65535)."));
        return new(_devices.SelectedItem is AmplifierDevice d ? d.Name : _address.Text.Trim(), _address.Text.Trim(), (AmplifierProtocol)_protocol.SelectedIndex, port, (_output.SelectedItem as Endpoint)?.Id);
    }

    private void LoadDevice(AmplifierDevice d)
    {
        _protocol.SelectedIndex = (int)d.Protocol; _address.Text = d.Address; _port.Text = d.Port.ToString();
        for (int i = 0; i < _output.Items.Count; i++) if (_output.Items[i] is Endpoint endpoint && endpoint.Id == d.AudioEndpointId) _output.SelectedIndex = i;
        if (!string.IsNullOrEmpty(d.AudioEndpointId)) return;
        // Nessuna uscita scelta: si propone quella che porta il nome del ricevitore ("marantz-AVR (NVIDIA…)").
        string[] brands = d.Protocol switch
        {
            AmplifierProtocol.DenonMarantz => new[] { "denon", "marantz" },
            AmplifierProtocol.YamahaMusicCast => new[] { "yamaha" },
            AmplifierProtocol.OnkyoIntegraPioneer => new[] { "onkyo", "integra", "pioneer" },
            AmplifierProtocol.Anthem => new[] { "anthem" },
            _ => Array.Empty<string>()
        };
        var words = brands.Concat(d.Name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 5 && !System.Net.IPAddress.TryParse(word, out _))).ToArray();
        for (int i = 0; i < _output.Items.Count; i++)
            if (_output.Items[i] is Endpoint candidate && candidate.Id.Length > 0 && words.Any(word => candidate.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            { _output.SelectedIndex = i; break; }
    }
}
