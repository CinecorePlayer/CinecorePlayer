using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CinecoreMac;

/// <summary>
/// Uscite audio di Windows (endpoint WASAPI attivi), al posto di quelle di CoreAudio. L'"UID" e' l'id dell'endpoint
/// (quello che mpv vuole come audio-device=wasapi/ID), il nome quello mostrato da Windows (lo stesso del renderer
/// DirectSound che madVR/MPC-VR usano nel VideoHost).
/// Bitstream: si chiede al driver, in modo esclusivo, se accetta i formati IEC 61937 (Dolby, DTS) come fa Windows.
/// </summary>
internal static class MacAudioDevices
{
    public sealed record Device(string Uid, string Name, bool IsDefault);

    // Gli id numerici che il motore musicale usa come "AudioDeviceID": indice + 1 in questo elenco.
    private static readonly List<string> Ids = new();
    private static readonly ConcurrentDictionary<string, (string Codecs, DateTime At)> CodecCache = new(StringComparer.OrdinalIgnoreCase);
    private static List<Device>? _outputs;
    private static DateTime _outputsAt;

    public static string ConfiguredOutputUid
    {
        get
        {
            string selected = MacSettings.AudioDeviceUid;
            // Nessuna uscita scelta: niente da chiedere ai driver (questa proprieta' si legge a ogni apertura).
            return selected.Length > 0 && Outputs().Exists(d => d.Uid == selected) ? selected : "";
        }
    }

    public static List<Device> Outputs()
    {
        // L'elenco si chiede spesso (menu, impostazioni, ogni apertura) e leggere i nomi dai driver costa 100-200 ms:
        // si risponde con l'ultimo elenco e, se ha piu' di 5 s, lo si rilegge in sottofondo. (Gli avvisi COM di Windows
        // sui cambi di uscita facevano cadere il runtime .NET: niente callback.)
        lock (Ids)
            if (_outputs != null)
            {
                if (DateTime.UtcNow - _outputsAt > TimeSpan.FromSeconds(5) && !_refreshing)
                {
                    _refreshing = true;
                    System.Threading.Tasks.Task.Run(() => { try { ReadOutputs(); } finally { _refreshing = false; } });
                }
                return new List<Device>(_outputs);
            }
        return ReadOutputs();
    }

    private static volatile bool _refreshing;

    // Nomi gia' letti, per id. Il nome (FriendlyName) e' la parte cara: apre l'archivio delle proprieta' del driver.
    // La rilettura ogni 5 s (l'app controlla se l'uscita e' cambiata) costava ~5% di un core a riposo; ora legge solo
    // id e predefinita e chiede il nome ai driver solo per un dispositivo nuovo.
    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.Ordinal);

    private static List<Device> ReadOutputs()
    {
        var list = new List<Device>();
        try
        {
            lock (Names)
            {
                // Un enumeratore per lettura: l'elenco si legge da thread diversi (anche quello dell'interfaccia, STA).
                using var enumerator = new MMDeviceEnumerator();
                string defaultId = "";
                try { using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = def.ID; } catch { }
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        string id = d.ID;
                        if (!Names.TryGetValue(id, out var name)) Names[id] = name = d.FriendlyName;
                        list.Add(new Device(id, name, id == defaultId));
                    }
                    catch { }
                    finally { d.Dispose(); }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[audio] uscite non lette: " + ex.Message); }
        lock (Ids) { _outputs = list; _outputsAt = DateTime.UtcNow; }
        return new List<Device>(list);
    }


    /// <summary>Legge uscite e formati compressi in sottofondo all'avvio, cosi' la prima apertura non aspetta i driver.</summary>
    public static void Prewarm() => System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            Outputs();
            string uid = ConfiguredOutputUid;
            ResolveUid(uid);
            SpdifCodecs(uid);
        }
        catch { }
    });

    private static string? _defaultId;
    private static DateTime _defaultAt;

    private static MMDevice? Open(string? uid)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return string.IsNullOrEmpty(uid) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(uid);
        }
        catch { return null; }
    }

    public static bool SupportsCompressed(string? uid) => SpdifCodecs(uid).Length > 0;

    // KSDATAFORMAT_SUBTYPE_IEC61937_*: formati compressi che il driver accetta in uscita esclusiva.
    private static readonly (string Codec, Guid SubFormat, int Rate, int Channels)[] Iec61937 =
    {
        ("ac3", new Guid("00000092-0000-0010-8000-00aa00389b71"), 48000, 2),
        ("dts", new Guid("00000008-0000-0010-8000-00aa00389b71"), 48000, 2),
        ("eac3", new Guid("0000000a-0cea-0010-8000-00aa00389b71"), 192000, 2),
        ("truehd", new Guid("0000000c-0cea-0010-8000-00aa00389b71"), 192000, 8),
        ("dts-hd", new Guid("0000000b-0cea-0010-8000-00aa00389b71"), 192000, 8),
    };

    public static string SpdifCodecs(string? uid)
    {
        string key = ResolveUid(uid);
        if (key.Length == 0) return "";
        if (CodecCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5)) return hit.Codecs;
        var found = new List<string>();
        using (var device = Open(key))
        {
            if (device != null)
                foreach (var (codec, sub, rate, channels) in Iec61937)
                {
                    try
                    {
                        var format = new WaveFormatExtensible(rate, 16, channels);
                        typeof(WaveFormatExtensible).GetField("subFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(format, sub);
                        if (device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, format)) found.Add(codec);
                    }
                    catch { }
                }
        }
        string result = string.Join(",", found);
        CodecCache[key] = (result, DateTime.UtcNow);
        return result;
    }

    // Su Windows il "bitstream dentro il PCM" del Mac non serve: il driver HDMI dichiara da se' i formati compressi.
    public static readonly (string Codec, string Name, string Hint)[] CarrierFormats =
    {
        ("ac3", "Dolby Digital", "48 kHz, 2 canali PCM"),
        ("dts", "DTS", "48 kHz, 2 canali PCM"),
        ("eac3", "Dolby Digital Plus / Atmos", "192 kHz, 2 canali PCM"),
        ("truehd", "Dolby TrueHD / Atmos", "192 kHz, 8 canali PCM"),
        ("dts-hd", "DTS-HD MA / DTS:X", "192 kHz, 8 canali PCM"),
    };

    public static string ResolveUid(string? uid)
    {
        if (!string.IsNullOrEmpty(uid)) return uid;
        if (_defaultId is { } known && DateTime.UtcNow - _defaultAt < TimeSpan.FromSeconds(5)) return known;
        using var device = Open(null);
        string id = device?.ID ?? "";
        if (id.Length > 0) { _defaultId = id; _defaultAt = DateTime.UtcNow; }
        return id;
    }

    /// <summary>Uscita HDMI / display (fattore di forma dell'endpoint).</summary>
    public static bool IsHdmi(string? uid)
    {
        try
        {
            using var device = Open(uid);
            if (device == null) return false;
            // PKEY_AudioEndpoint_FormFactor: 8 = SPDIF, 9 = DigitalAudioDisplayDevice (HDMI/DisplayPort).
            var key = new NAudio.CoreAudioApi.PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
            var props = device.Properties;
            if (props.Contains(key) && props[key].Value is uint form) return form is 8 or 9;
            string name = device.FriendlyName;
            return name.Contains("HDMI", StringComparison.OrdinalIgnoreCase) || name.Contains("NVIDIA High Definition", StringComparison.OrdinalIgnoreCase)
                || name.Contains("AMD High Definition", StringComparison.OrdinalIgnoreCase) || name.Contains("Intel(R) Display", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool LpcmOnlyHdmi(string? uid) => false;
    public static HashSet<string> CarrierEnabled(string? uid) => new(StringComparer.Ordinal);
    public static void SetCarrier(string? uid, string codec, bool enabled) { }

    /// <summary>Bitstream per l'uscita (codec per audio-spdif di mpv). Vuoto = audio decodificato in PCM.</summary>
    public static (string Codecs, bool Carrier) Passthrough(string? uid)
    {
        if (!MacSettings.Bitstream) return ("", false);
        return (SpdifCodecs(uid), false);
    }

    public static string PassthroughKey(string? uid)
    {
        var (codecs, _) = Passthrough(uid);
        return codecs;
    }

    public static bool IsRunning(string uid)
    {
        try
        {
            using var device = Open(uid);
            return device != null && device.AudioMeterInformation.MasterPeakValue > 0;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ uscita esclusiva / bit perfect della musica
    public static uint DeviceId(string? uid)
    {
        string id = ResolveUid(uid);
        if (id.Length == 0) return 0;
        lock (Ids)
        {
            int index = Ids.IndexOf(id);
            if (index < 0) { Ids.Add(id); index = Ids.Count - 1; }
            return (uint)(index + 1);
        }
    }

    private static string? IdOf(uint device) { lock (Ids) return device >= 1 && device <= Ids.Count ? Ids[(int)device - 1] : null; }

    /// <summary>Frequenza del formato condiviso del dispositivo (quella del mixer di Windows).</summary>
    public static double NominalRate(uint device)
    {
        try { using var d = Open(IdOf(device)); return d?.AudioClient.MixFormat.SampleRate ?? 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Su Windows la frequenza la sceglie l'uscita esclusiva quando si apre (WinAudioQueue): in condiviso il mixer
    /// converte. Si accetta sempre.
    /// </summary>
    public static bool SetNominalRate(uint device, double rate) => true;

    /// <summary>Frequenze che il driver accetta in esclusivo (float o intero 24 bit stereo).</summary>
    public static List<(double Min, double Max)> AvailableRates(uint device)
    {
        var list = new List<(double, double)>();
        try
        {
            using var d = Open(IdOf(device));
            if (d == null) return list;
            foreach (int rate in new[] { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000 })
            {
                bool ok = false;
                try { ok = d.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, new WaveFormatExtensible(rate, 24, 2)); } catch { }
                if (!ok) try { ok = d.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)); } catch { }
                if (ok) list.Add((rate, rate));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Uso esclusivo: lo applica WinAudioQueue all'apertura dell'uscita (WASAPI esclusivo).</summary>
    public static bool SetHog(uint device, bool take)
    {
        if (IdOf(device) is not { } id) return false;
        lock (WinAudioQueue.ExclusiveDevices) { if (take) WinAudioQueue.ExclusiveDevices.Add(id); else WinAudioQueue.ExclusiveDevices.Remove(id); }
        return true;
    }

    public readonly record struct PhysicalFormat(double Rate, uint FormatId, uint Flags, uint BytesPerPacket, uint FramesPerPacket, uint BytesPerFrame, uint Channels, uint Bits)
    {
        public bool IsIntegerPcm => FormatId == 0x6C70636D /* lpcm */ && (Flags & 1) == 0;
        public override string ToString() => $"{Rate:0} Hz {Channels} ch {Bits} bit {((Flags & 1) != 0 ? "float" : "int")}";
    }

    private static PhysicalFormat Describe(int rate, int bits, int channels, bool isFloat) =>
        new(rate, 0x6C70636D, isFloat ? 1u | 8u : 4u | 8u, (uint)(bits / 8 * channels), 1, (uint)(bits / 8 * channels), (uint)channels, (uint)bits);

    public static PhysicalFormat? CurrentPhysicalFormat(uint device)
    {
        try
        {
            using var d = Open(IdOf(device));
            if (d == null) return null;
            var mix = d.AudioClient.MixFormat;
            string? id = IdOf(device);
            int bits = 32;
            lock (WinAudioQueue.ExclusiveDevices) if (id != null && WinAudioQueue.DeviceBits.TryGetValue(id, out int b)) bits = b;
            return Describe(mix.SampleRate, bits, mix.Channels, bits == 32);
        }
        catch { return null; }
    }

    public static List<(PhysicalFormat Format, double MinRate, double MaxRate)> AvailablePhysicalFormats(uint device)
    {
        var list = new List<(PhysicalFormat, double, double)>();
        try
        {
            using var d = Open(IdOf(device));
            if (d == null) return list;
            foreach (var (rate, _) in AvailableRates(device))
                foreach (int bits in new[] { 16, 24, 32 })
                {
                    bool ok = false;
                    try { ok = d.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, new WaveFormatExtensible((int)rate, bits, 2)); } catch { }
                    if (ok) list.Add((Describe((int)rate, bits, 2, false), rate, rate));
                }
        }
        catch { }
        return list;
    }

    public static PhysicalFormat? ChoosePhysicalFormat(uint device, double rate, int minBits)
    {
        PhysicalFormat? best = null;
        foreach (var (f, min, max) in AvailablePhysicalFormats(device))
        {
            if (rate < min - .5 || rate > max + .5 || f.Bits < minBits) continue;
            if (best == null || f.Bits < best.Value.Bits) best = f with { Rate = rate };
        }
        return best;
    }

    /// <summary>Bit perfect: i bit chiesti valgono alla prossima apertura dell'uscita esclusiva.</summary>
    public static bool SetPhysicalFormat(uint device, PhysicalFormat format)
    {
        if (IdOf(device) is not { } id) return false;
        lock (WinAudioQueue.ExclusiveDevices)
        {
            if (format.IsIntegerPcm) WinAudioQueue.DeviceBits[id] = (int)format.Bits;
            else WinAudioQueue.DeviceBits.Remove(id);
        }
        return true;
    }
}
