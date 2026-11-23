using System;
using System.Globalization;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Scan veloce (PlayerForm.RemoteControl.cs di Windows): pressione lunga su -10/+10 o comandi del telefono. Non cambia la
/// velocita' del motore: salti continui (10 s di film al secondo per x1) ogni 100 ms, quindi vale con tutti i renderer.
/// Ogni nuova pressione nella stessa direzione accelera (x0,5 → x1 → x2 → x4); play, -10/+10 brevi o i capitoli lo fermano.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly double[] ScanSpeeds = { 0.5, 1.0, 2.0, 4.0 };
    private const double ScanSecondsPerSecond = 10.0;
    private DispatcherTimer? _scanTimer;
    private int _scanDir, _scanSpeed;
    private double _scanPosition;

    private bool ScanActive => _scanTimer?.IsEnabled == true;

    private void StepScan(int dir, bool fromHud)
    {
        if (!_video.IsRunning || _hud.Duration <= 0) return;
        _scanTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => ScanTick());
        if (!_scanTimer.IsEnabled || _scanDir != dir)
        {
            _scanDir = dir; _scanSpeed = 0;
            _scanPosition = _hud.Position;
            _scanTimer.Start();
        }
        else if (_scanSpeed < ScanSpeeds.Length - 1) _scanSpeed++;
        string label = "x" + ScanSpeeds[_scanSpeed].ToString("0.#", MacTools.Italian);
        _hud.SetScanState(_scanDir, label);
        // Dall'HUD la velocita' si legge sopra il pulsante; dal telefono serve l'avviso a schermo.
        if (!fromHud) _osd.Show(label, 900);
        _hud.Poke();
    }

    private void StopScan()
    {
        if (_scanTimer == null) return;
        _scanTimer.Stop();
        _scanDir = 0; _scanSpeed = 0;
        _hud.SetScanState(0, null);
    }

    private void ScanTick()
    {
        if (!_video.IsRunning || _hud.Duration <= 0) { StopScan(); return; }
        // La posizione la tiene lo scan: quella del motore arriva in ritardo dopo ogni salto.
        _scanPosition = Math.Clamp(_scanPosition + ScanSecondsPerSecond * ScanSpeeds[_scanSpeed] * 0.1 * _scanDir, 0, _hud.Duration);
        SeekTo(_scanPosition);
        if (_scanPosition <= 0.0001 && _scanDir < 0 || _scanPosition >= _hud.Duration - 0.0001 && _scanDir > 0) StopScan();
    }
}
