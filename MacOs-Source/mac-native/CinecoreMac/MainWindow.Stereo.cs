using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Video › 3D (PlayerForm.VideoFeatures.cs di Windows): Auto riconosce SBS e Top/Bottom (nome, metadati, formato),
/// Nativo lascia l'immagine doppia, SBS → 2D e Top/Bottom → 2D mostrano un occhio solo. La scelta vale per il film in corso;
/// Auto resta acceso fra un film e l'altro. "Schermo intero esteso" in MainWindow.ExtendedFullscreen.cs.
/// </summary>
public sealed partial class MainWindow
{
    private StereoMode _stereo;
    private static bool StereoAuto { get => MacSettings.Get("stereoAuto", true); set => MacSettings.Set("stereoAuto", value); }

    private MenuEntry StereoMenu() => new("3D", "3d")
    {
        Children = new()
        {
            new MenuEntry("Auto (metadati + rilevamento)", null, () => { StereoAuto = true; DetectStereo(announce: true); }) { Checked = StereoAuto },
            new MenuEntry("Nativo (immagine doppia)", null, () => SetStereo(StereoMode.None, manual: true, "3D: nativo")) { Checked = !StereoAuto && _stereo == StereoMode.None },
            new MenuEntry("Side-by-side → 2D", null, () => SetStereo(StereoMode.Sbs, manual: true, "3D: Side-by-side → 2D")) { Checked = !StereoAuto && _stereo == StereoMode.Sbs },
            new MenuEntry("Top/Bottom → 2D", null, () => SetStereo(StereoMode.Tab, manual: true, "3D: Top/Bottom → 2D")) { Checked = !StereoAuto && _stereo == StereoMode.Tab },
            MenuEntry.Sep(),
            new MenuEntry("Schermo intero esteso (un occhio per schermo)", null, ToggleExtendedFullscreen) { Checked = _extendedFs, Enabled = CanExtendFullscreen || _extendedFs, Value = "Maiusc+F" },
        },
    };

    private void SetStereo(StereoMode mode, bool manual, string? announce)
    {
        if (manual) StereoAuto = false;
        _stereo = mode;
        // Vale con tutti i renderer: madVR compreso (il VideoHost ingrandisce il fotogramma e mostra un occhio).
        SendStereoOutput();
        _sizingFile = null; // il formato dell'immagine e' cambiato
        ApplyImageSizing(animate: true);
        if (announce != null) _osd.Show(announce, 2000);
    }

    /// <summary>All'apertura (con Auto acceso) o dal menu: rilevamento fuori dal thread dell'interfaccia.</summary>
    private void DetectStereo(bool announce)
    {
        if (_file is not { } file || !_video.IsRunning) return;
        int w = _probe?.Width ?? 0, h = _probe?.Height ?? 0;
        _ = Task.Run(() =>
        {
            var (mode, reason) = MacStereo.Detect(file, w, h);
            Dispatcher.UIThread.Post(() =>
            {
                if (_file != file || !StereoAuto) return;
                Console.Error.WriteLine($"[3D] auto: {mode} ({reason})");
                // Stesso modo di prima (es. dopo il cambio di renderer): basta ridarlo al motore nuovo.
                if (mode == _stereo && !announce) { if (mode != StereoMode.None || _extendedFs) SendStereoOutput(); return; }
                if (true)
                    SetStereo(mode, manual: false, announce || mode != StereoMode.None
                        ? "3D Auto: " + (mode switch { StereoMode.Sbs => "Side-by-side → 2D", StereoMode.Tab => "Top/Bottom → 2D", _ => "nessun 3D" }) + " (" + reason + ")" : null);
            });
        });
    }
}
