using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>Owns the SR7010 EDID correction for the lifetime of native movie audio.</summary>
internal sealed class MacHdmiOverride : IAsyncDisposable
{
    private readonly Process _helper;
    private readonly FileStream _lock;
    private readonly string _uid;

    private MacHdmiOverride(Process helper, FileStream fileLock, string uid)
    {
        _helper = helper;
        _lock = fileLock;
        _uid = uid;
    }

    public static string? Target(string uid)
    {
        // Opening a Dolby carrier does not establish that the SR7010 decodes it.
        // Keep the synthetic EDID available for controlled HDMI experiments.
        if (Environment.GetEnvironmentVariable("CINECORE_HDMI_EDID_TEST") != "1") return null;
        if (!MacSettings.Bitstream || !MpvEngine.Available) return null;
        var device = MacAudioDevices.Outputs().Find(d => uid.Length == 0 ? d.IsDefault : d.Uid == uid);
        return device?.Name == "marantz-AVR" ? device.Uid : null;
    }

    public static async Task<MacHdmiOverride?> TryStartAsync(string uid)
    {
        if (Target(uid) is not { } target || MacAudioDevices.SupportsCompressed(target)) return null;
        string executable = Path.Combine(AppContext.BaseDirectory, "cinecore-hdmi-override");
        if (!File.Exists(executable)) return null;

        FileStream? fileLock = null;
        MacHdmiOverride? lease = null;
        try
        {
            fileLock = new FileStream(Path.Combine(Path.GetTempPath(), "cinecore-marantz-edid.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            var helper = Process.Start(start) ?? throw new IOException("processo EDID non avviato");
            lease = new MacHdmiOverride(helper, fileLock, target);
            fileLock = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (await helper.StandardOutput.ReadLineAsync(timeout.Token) == "ready")
            {
                while (!MacAudioDevices.SupportsCompressed(target))
                    await Task.Delay(120, timeout.Token);
                Console.Error.WriteLine("[audio] EDID HDMI Dolby attivo sul Marantz");
                var result = lease;
                lease = null;
                return result;
            }
            Console.Error.WriteLine("[audio] correzione EDID Marantz non disponibile");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("[audio] EDID Marantz: " + ex.Message);
        }
        finally
        {
            if (lease != null) await lease.DisposeAsync();
            fileLock?.Dispose();
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _helper.StandardInput.Close();
            await _helper.WaitForExitAsync();
            if (_helper.ExitCode != 0)
                Console.Error.WriteLine("[audio] processo EDID Marantz terminato con codice " + _helper.ExitCode);
            // CoreAudio can temporarily remove the HDMI device while the display
            // returns to its original EDID. Wait before opening the next renderer.
            int stable = 0;
            for (int attempt = 0; attempt < 25 && stable < 2; attempt++)
            {
                await Task.Delay(120);
                bool restored = MacAudioDevices.Outputs().Exists(d => d.Uid == _uid) &&
                                !MacAudioDevices.SupportsCompressed(_uid);
                stable = restored ? stable + 1 : 0;
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            _helper.Dispose();
            _lock.Dispose();
        }
    }
}
