#nullable enable
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using NAudio.CoreAudioApi;

namespace CinecorePlayer2025;

internal sealed class AmplifierControlForm : HudModalFormBase
{
    private readonly ChoiceField _devices = new() { Dock = DockStyle.Fill };
    private readonly ChoiceField _protocol = new() { Dock = DockStyle.Fill };
    private readonly TextBox _address = new() { BorderStyle = BorderStyle.None, PlaceholderText = "192.168.1.20" };
    private readonly TextBox _port = new() { BorderStyle = BorderStyle.None, Text = "0" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly CancellationTokenSource _lifetime = new();
    private AmplifierControl? _client;
    private AmplifierStatus? _state;
    private readonly ChoiceField _output = new() { Dock = DockStyle.Fill };
    private sealed record Endpoint(string Id, string Name) { public override string ToString() => Name; }
    private bool _busy;
    private readonly bool _english;
    private string L(string it, string en) => _english ? en : it;
    public AmplifierControlForm(bool english)
    {
        _english = english;
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L("Configura controllo IP", "Configure IP control");
        ClientSize = new Size(720, 510); MinimumSize = new Size(620, 510);
        Font = new Font("Segoe UI", 10);
        BackColor = Theme.Panel;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(32), ColumnCount = 3, RowCount = 8 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        foreach (int h in new[] { 54, 48, 48, 48, 48, 60, 54, 48 }) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        Label Label(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = new Font("Segoe UI", 10) };
        var title = Label(Text); title.ForeColor = Theme.Text; title.Font = new Font("Segoe UI Semibold", 17); grid.Controls.Add(title, 0, 0); grid.SetColumnSpan(title, 3);
        grid.Controls.Add(Label(L("Dispositivi", "Devices")), 0, 1); grid.Controls.Add(_devices, 1, 1);
        var scan = CreateModalButton(L("Cerca", "Discover"), DialogResult.None, false); scan.Dock = DockStyle.Fill; grid.Controls.Add(scan, 2, 1);
        grid.Controls.Add(Label(L("Protocollo", "Protocol")), 0, 2); grid.Controls.Add(_protocol, 1, 2); grid.SetColumnSpan(_protocol, 2);
        _protocol.Items.AddRange(new object[] { "Denon / Marantz", "Yamaha MusicCast", "Onkyo / Integra / Pioneer (eISCP)", "UPnP / DLNA RenderingControl", "NAD (Ethernet V2)", "Anthem (MRX SLM)" }); _protocol.SelectedIndex = 0;
        Panel Input(TextBox text)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = new Padding(4, 5, 4, 5) };
            panel.Controls.Add(text); text.AutoSize = false;
            void LayoutInput() { text.SetBounds(12, Math.Max(0, (panel.Height - text.Font.Height - 1) / 2), Math.Max(1, panel.Width - 24), text.Font.Height + 1); }
            panel.Resize += (_, _) => LayoutInput(); text.FontChanged += (_, _) => LayoutInput();
            panel.Paint += (_, e) => { using var pen = new Pen(text.Focused ? Theme.Accent : Theme.Border); e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1); };
            text.GotFocus += (_, _) => panel.Invalidate(); text.LostFocus += (_, _) => panel.Invalidate(); return panel;
        }
        grid.Controls.Add(Label(L("IP / URL UPnP", "IP / UPnP URL")), 0, 3); var addressPanel = Input(_address); grid.Controls.Add(addressPanel, 1, 3); grid.SetColumnSpan(addressPanel, 2);
        grid.Controls.Add(Label(L("Porta (0 = auto)", "Port (0 = auto)")), 0, 4); grid.Controls.Add(Input(_port), 1, 4);
        var connect = CreateModalButton(L("Verifica", "Connect"), DialogResult.None, true); connect.Dock = DockStyle.Fill; grid.Controls.Add(connect, 2, 4);
        _status.Text = L("I comandi volume e mute agiscono sul dispositivo solo quando il player usa l’uscita associata.", "Volume and mute control the device only while the player uses the linked output."); _status.ForeColor = Theme.Muted;
        grid.Controls.Add(_status, 0, 5); grid.SetColumnSpan(_status, 3);
        grid.Controls.Add(Label(L("Uscita audio", "Audio output")), 0, 6);
        grid.Controls.Add(_output, 1, 6); grid.SetColumnSpan(_output, 2);
        _output.Items.Add(new Endpoint("", L("Non associata · controllo disattivato", "Unlinked · control disabled")));
        _output.SelectedIndex = 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (endpoint) _output.Items.Add(new Endpoint(endpoint.ID, endpoint.FriendlyName));
        }
        catch { }
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = CreateModalButton(L("Chiudi", "Close"), DialogResult.Cancel, false); close.Size = new Size(108, 38);
        var save = CreateModalButton(L("Salva", "Save"), DialogResult.None, true); save.Size = new Size(108, 38);
        footer.Controls.Add(close); footer.Controls.Add(save); grid.Controls.Add(footer, 0, 7); grid.SetColumnSpan(footer, 3);
        Controls.Add(grid); CancelButton = close; AcceptButton = connect;
        foreach (Control control in new Control[] { _devices, _protocol, _address, _port, _output }) { control.BackColor = Theme.Panel; control.ForeColor = Theme.Text; control.Font = new Font("Segoe UI", 10); control.Margin = new Padding(4, 6, 4, 6); }
        void ResetConnection() { if (_busy) return; _client?.Dispose(); _client = null; _state = null;  _status.Text = L("Premi Verifica per leggere lo stato del dispositivo.", "Connect to read the device status."); }
        _address.TextChanged += (_, _) => ResetConnection(); _port.TextChanged += (_, _) => ResetConnection(); _protocol.SelectedIndexChanged += (_, _) => ResetConnection();
        _devices.SelectedIndexChanged += (_, _) => { if (_devices.SelectedItem is AmplifierDevice device) LoadDevice(device); };
        connect.Click += async (_, _) => await Run(async () => { _client?.Dispose(); _client = new AmplifierControl(Current()); return await _client.ReadAsync(_lifetime.Token); });
        save.Click += (_, _) => { if (_busy) return; try { using var validation = new AmplifierControl(Current()); AmplifierControl.Save(Current()); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { _status.Text = ex.Message; } };
        scan.Click += async (_, _) =>
        {
            if (_busy) return; _busy = true; grid.Enabled = false; _status.Text = L("Ricerca in rete…", "Searching the network…");
            try { var devices = await AmplifierControl.DiscoverAsync(_lifetime.Token); if (IsDisposed) return; _devices.Items.Clear(); foreach (var device in devices) _devices.Items.Add(device); _status.Text = devices.Count == 0 ? L("Nessun dispositivo rilevato. Puoi inserire l’IP manualmente.", "No device found. You can enter its IP manually.") : L("Scegli un dispositivo, poi premi Verifica.", "Select a device, then connect."); }
            catch (Exception ex) { if (!IsDisposed) _status.Text = ex is OperationCanceledException ? L("Ricerca terminata.", "Search ended.") : ex.Message; }
            finally { _busy = false; if (!IsDisposed) grid.Enabled = true; }
        };
        if (AmplifierControl.Load() is { } saved) LoadDevice(saved);
        FormClosed += (_, _) => { _lifetime.Cancel(); _client?.Dispose(); };
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
    }
    private async Task Run(Func<Task<AmplifierStatus>> action)
    {
        if (_busy) return; _busy = true; 
        _address.Enabled = _protocol.Enabled = _port.Enabled = _devices.Enabled = false;
        try { _state = await action(); if (!IsDisposed) _status.Text = L("Connessione verificata. Associa l’uscita audio e salva: volume e mute si controllano dal player.", "Connection verified. Link the audio output and save to control volume and mute from the player."); }
        catch (Exception ex) { _state = null; if (!IsDisposed) _status.Text = ex is OperationCanceledException ? L("Nessuna risposta. Controlla IP, protocollo e controllo di rete.", "No response. Check IP, protocol and network control.") : ex.Message; }
        finally { _busy = false; if (!IsDisposed) {  _address.Enabled = _protocol.Enabled = _port.Enabled = _devices.Enabled = true; } }
    }
}
