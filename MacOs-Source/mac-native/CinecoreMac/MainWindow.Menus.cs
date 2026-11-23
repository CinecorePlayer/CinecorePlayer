using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CinecoreMac;

public sealed partial class MainWindow
{
    private MenuAmbient.Frame? CaptureLibraryBackground()
    {
        try
        {
            // Il vetro Cocoa è sotto la finestra Avalonia: non può sfocare le locandine disegnate sopra.
            // Come su Windows, per la libreria si prepara il vetro dalla pagina stessa, a bassa risoluzione.
            double scale = Math.Min(1, 480 / Math.Max(1, _library.Bounds.Width));
            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(_library.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(_library.Bounds.Height * scale)));
            // Non RenderTargetBitmap.Render: applica i DPI due volte (la pagina usciva rimpicciolita nell'angolo). Vedi SurfaceCrossfade.
            using var capture = new RenderTargetBitmap(size, new Vector(96, 96));
            using (var dc = capture.CreateDrawingContext())
            using (dc.PushTransform(Matrix.CreateScale(scale, scale)))
                _library.Render(dc);
            using var converted = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var pixels = converted.Lock();
            capture.CopyPixels(pixels, AlphaFormat.Premul);
            var data = new byte[size.Width * size.Height * 4];
            for (int y = 0; y < size.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(pixels.Address, y * pixels.RowBytes), data, y * size.Width * 4, size.Width * 4);
            return new MenuAmbient.Frame(data, size.Width, size.Height);
        }
        catch (Exception ex) { Console.Error.WriteLine("menu libreria: " + ex.Message); return null; }
    }

    private int _audioOffsetMs;
    private string? _externalSubtitle;

    private MenuEntry AudioDelayMenu()
    {
        var menu = new MenuEntry("Ritardo audio") { Value = _audioOffsetMs == 0 ? "Automatico" : $"{_audioOffsetMs:+0;-0;0} ms", Children = new() };
        foreach (int offset in new[] { -1000, -500, -200, -100, -50, 0, 50, 100, 200, 500, 1000 })
        {
            int value = offset;
            menu.Children.Add(new MenuEntry(value == 0 ? "Automatico (compensazione del ponte)" : $"{value:+0;-0;0} ms", null, () =>
            {
                _audioOffsetMs = value;
                _video.Send("audio-offset " + value.ToString(CultureInfo.InvariantCulture));
                SaveAudioDelay();
            }) { Checked = value == _audioOffsetMs });
        }
        return menu;
    }

    /// <summary>
    /// Video › Immagine / HDR (come Windows, per tutti i renderer): una scelta sola che imposta madVR (profilo HDR),
    /// MPC-VR (converti in SDR) e MPV (uscita HDR in EDR). madVR cambia dal vivo, MPV con la sua proprieta', MPC-VR
    /// riapre il film nello stesso punto. La 3DLUT resta di madVR.
    /// </summary>
    private MenuEntry PictureHdrMenu()
    {
        string current = MacSettings.Get("hdrUiProfile", "auto");
        MenuEntry Choice(string text, string value) => new(text, null, () => SetPictureHdr(value)) { Checked = current == value };
        var root = new MenuEntry("Immagine / HDR", "hdr") { Children = new()
        {
            Choice("Auto (gestito dal renderer)", "auto"),
            Choice("Passthrough HDR al display", "passthrough"),
            Choice("Tone-map HDR → SDR (pixel shaders)", "tonemap"),
        } };
        // 3DLUT HDR→SDR: funzione di madVR (come Windows). La prima volta ricorda che la 3DLUT va configurata in madVR.
        if (_renderer == "madvr")
            root.Children.Add(new MenuEntry("HDR → SDR (via 3DLUT) — avanzato…", null, () =>
            {
                SetPictureHdr("lut");
                if (MacSettings.Get("hdrLutWarned", "0") != "1")
                {
                    MacSettings.Set("hdrLutWarned", "1");
                    _osd.Show("Questo profilo richiede una 3DLUT HDR→SDR configurata in madVR (Devices → calibration)", 4200);
                }
            }) { Checked = current == "lut" });
        return root;
    }

    private void SetPictureHdr(string value)
    {
        MacSettings.Set("hdrUiProfile", value);
        void Store(string tab, string key, string v)
        {
            var setting = ComponentSettings.Sections(tab).SelectMany(s => s.Settings).FirstOrDefault(s => s.Key == key);
            if (setting == null) return;
            var commands = ComponentSettings.Set(tab, setting, v).ToList();
            if (tab == "madVR" && _renderer == "madvr") foreach (string command in commands) _video.Send(command);
        }
        Store("madVR", ComponentSettings.HdrProfileKey, value);
        Store("MPC Video Renderer", "ConvertToSdr", value == "passthrough" ? "0" : "1");
        Store("MPV", "TargetColorspaceHint", value is "tonemap" or "lut" ? "0" : "1");
        if (value != "lut" || MacSettings.Get("hdrLutWarned", "0") == "1")
            _osd.Show(value switch { "passthrough" => "HDR: passthrough al display", "tonemap" => "HDR: tone mapping → SDR", "lut" => "HDR: 3DLUT in SDR via madVR", _ => "HDR: automatico" }, 1800);
        if (!_video.IsRunning || _file is not { } file) return;
        if (Engine == "mpv") _video.Send("prop target-colorspace-hint " + (value is "tonemap" or "lut" ? "no" : "yes"));
        else if (_renderer == "mpcvr")
        {
            double at = _hud.Position;
            StartVideo(file, _titleOverride);
            _resumeOnReady = at > 1 ? at : null;
        }
    }

    private void ApplyMadVrSetting(ComponentSettings.Setting setting, string value)
    {
        foreach (string command in ComponentSettings.Set("madVR", setting, value)) _video.Send(command);
    }

    private async Task OpenSubtitleAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri sottotitoli", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Sottotitoli") { Patterns = new[] { "*.srt", "*.ass", "*.ssa", "*.sub", "*.idx", "*.sup", "*.vtt" } } },
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) LoadSubtitle(path);
    }

    private void LoadSubtitle(string path)
    {
        if (!_video.IsRunning || !File.Exists(path)) return;
        _externalSubtitle = path;
        _video.Send("subtitle " + VideoHostClient.ToWinePath(path));
    }

    /// <summary>Riallinea il sottotitolo esterno sull'audio (RealignExternalSubtitle di Windows).</summary>
    private void RealignExternalSubtitle()
    {
        if (_file is not { } video || _externalSubtitle is not { } subtitle) return;
        _hud.InfoLine = "Sottotitoli: allineo i tempi sull’audio…"; _hud.Poke();
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                var outcome = await CinecorePlayer2025.Utilities.SubtitleWorkflow.RealignAsync(video, subtitle, null, System.Threading.CancellationToken.None);
                message = CinecorePlayer2025.Utilities.SubtitleWorkflow.Describe(outcome, false);
                if (outcome.TimingChanged) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_file == video) LoadSubtitle(outcome.Path); });
            }
            catch (Exception ex) { message = "Riallineamento non riuscito: " + ex.Message; }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _hud.InfoLine = message; _hud.Poke(); });
        });
    }
}
