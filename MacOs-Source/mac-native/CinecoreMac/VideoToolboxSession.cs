using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

internal sealed class VideoToolboxSession(Process? decoder, DirectoryInfo? directory) : IDisposable
{
    private Process? _decoder = decoder;

    internal static async Task<VideoToolboxSession?> TryStartAsync(ProcessStartInfo wine, string root, string renderer)
    {
        // Predefinito con FEX: HEVC decodificato da VideoToolbox (Interstellar 4K HDR10: ~70% CPU contro ~235%).
        // CINECORE_FEX_VIDEOTOOLBOX=0 torna alla decodifica software di LAV.
        if (renderer is not ("madvr" or "mpcvr") || Environment.GetEnvironmentVariable("CINECORE_FEX_VIDEOTOOLBOX") == "0"
            || VideoHostClient.Arm64Bundle == null) return null;

        string helper = Path.Combine(AppContext.BaseDirectory, "cinecore-vt-decode");
        string bridge = MacEngine.VtBridge;
        if (!File.Exists(bridge)) return null;
        // Decoder nello stesso processo del VideoHost (cinecorevt.so, caricata dal ponte con le chiamate native di Wine):
        // niente programma separato, niente socket via wineserver, niente copia del fotogramma. Misurato il 23/11/2026,
        // 3 coppie alternate 4K: CPU totale (VideoHost + wineserver + decoder) ~180% -> ~160%, immagine uguale.
        // CINECORE_VT_INPROCESS=0 torna al programma separato.
        string unixLib = Path.Combine(AppContext.BaseDirectory, "cinecorevt.so");
        if (File.Exists(unixLib) && Environment.GetEnvironmentVariable("CINECORE_VT_INPROCESS") != "0" && BridgeSupportsUnixLib(bridge))
        {
            wine.Environment["CINECORE_VT_UNIXLIB"] = @"\??\" + VideoHostClient.ToWinePath(unixLib);
            wine.Environment["CINECORE_VT_DLL"] = VideoHostClient.ToWinePath(bridge);
            return new VideoToolboxSession(null, null);
        }
        if (!File.Exists(helper)) return null;

        DirectoryInfo? temp = null;
        VideoToolboxSession? session = null;
        try
        {
            temp = Directory.CreateTempSubdirectory("cinecore-vt-");
            string mapping = Path.Combine(temp.FullName, "frames");
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true };
            start.ArgumentList.Add(mapping);
            start.Environment["CINECORE_VT_TOKEN"] = token;
            var process = Process.Start(start) ?? throw new InvalidOperationException("VideoToolbox non avviato");
            session = new VideoToolboxSession(process, temp);
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (!ushort.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) || port == 0)
                throw new InvalidOperationException("Canale VideoToolbox non disponibile");
            wine.Environment["CINECORE_VT_PORT"] = port.ToString(CultureInfo.InvariantCulture);
            wine.Environment["CINECORE_VT_TOKEN"] = token;
            wine.Environment["CINECORE_VT_MAP"] = VideoHostClient.ToWinePath(mapping);
            wine.Environment["CINECORE_VT_DLL"] = VideoHostClient.ToWinePath(bridge);
            return session;
        }
        catch (Exception ex)
        {
            if (session != null) session.Dispose();
            else DeleteDirectory(temp);
            Console.Error.WriteLine("[video] VideoToolbox non disponibile: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Le DLL del ponte di un motore installato prima del 23/11 non conoscono la libreria nello stesso processo.</summary>
    private static bool BridgeSupportsUnixLib(string bridge)
    {
        try
        {
            byte[] data = File.ReadAllBytes(bridge);
            return data.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("CINECORE_VT_UNIXLIB")) >= 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        var process = Interlocked.Exchange(ref _decoder, null);
        if (process == null) return;
        try
        {
            if (!process.WaitForExit(500))
            {
                process.Kill();
                process.WaitForExit(500);
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            process.Dispose();
            DeleteDirectory(directory);
        }
    }

    private static void DeleteDirectory(DirectoryInfo? temp)
    {
        try { temp?.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
