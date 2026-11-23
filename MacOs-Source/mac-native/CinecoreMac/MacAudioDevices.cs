using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// Uscite audio di CoreAudio (menu Audio › Uscita audio, come l'elenco dei renderer di Windows). Il nome serve a
/// scegliere il "DirectSound: nome" di Wine per madVR/MPC-VR, l'UID a mpv (audio-device=coreaudio/UID).
/// </summary>
internal static class MacAudioDevices
{
    private const string CA = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)] private struct PropertyAddress { public uint Selector, Scope, Element; }
    [DllImport(CA)] private static extern int AudioObjectGetPropertyDataSize(uint id, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint size);
    [DllImport(CA)] private static extern int AudioObjectGetPropertyData(uint id, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint size, IntPtr data);
    [DllImport(CA)] private static extern int AudioObjectSetPropertyData(uint id, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, uint size, IntPtr data);
    [DllImport("libc")] private static extern int getpid();
    [DllImport(CF)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, long size, uint encoding);
    [DllImport(CF)] private static extern void CFRelease(IntPtr p);

    private static uint FourCC(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);
    private static readonly uint Global = FourCC("glob"), Output = FourCC("outp");
    private static readonly HashSet<uint> Ac3Formats = new() { FourCC("IAC3"), FourCC("iac3"), FourCC("cac3"), FourCC("ac-3") };
    private static readonly HashSet<uint> DolbyPlusFormats = new() { FourCC("cec3"), FourCC("cc+3"), FourCC("cd+3") };
    private static readonly HashSet<uint> TrueHdFormats = new() { FourCC("mtat"), FourCC("mtbt"), FourCC("mtb+"), FourCC("mtct"), FourCC("mtc+") };

    public sealed record Device(string Uid, string Name, bool IsDefault);

    public static string ConfiguredOutputUid
    {
        get
        {
            string selected = MacSettings.AudioDeviceUid;
            return selected.Length > 0 && Outputs().Exists(d => d.Uid == selected) ? selected : "";
        }
    }

    public static List<Device> Outputs()
    {
        var list = new List<Device>();
        try
        {
            uint defaultId = DefaultOutput();
            var addr = new PropertyAddress { Selector = FourCC("dev#"), Scope = Global, Element = 0 };
            if (AudioObjectGetPropertyDataSize(1, ref addr, 0, IntPtr.Zero, out uint size) != 0 || size == 0) return list;
            var ids = new uint[size / 4];
            var handle = GCHandle.Alloc(ids, GCHandleType.Pinned);
            try { if (AudioObjectGetPropertyData(1, ref addr, 0, IntPtr.Zero, ref size, handle.AddrOfPinnedObject()) != 0) return list; }
            finally { handle.Free(); }
            foreach (uint id in ids)
            {
                // Solo i dispositivi con flussi in uscita (niente microfoni).
                var streams = new PropertyAddress { Selector = FourCC("stm#"), Scope = Output, Element = 0 };
                if (AudioObjectGetPropertyDataSize(id, ref streams, 0, IntPtr.Zero, out uint streamBytes) != 0 || streamBytes == 0) continue;
                string name = StringProperty(id, "lnam"), uid = StringProperty(id, "uid ");
                if (name.Length > 0 && uid.Length > 0) list.Add(new Device(uid, name, id == defaultId));
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Verifica i formati fisici compressi riconosciuti da coreaudio_exclusive di mpv.
    /// </summary>
    public static bool SupportsCompressed(string? uid) => SpdifCodecs(uid).Length > 0;

    public static string SpdifCodecs(string? uid)
    {
        bool ac3 = false, dolbyPlus = false, trueHd = false;
        try
        {
            uint device = DeviceId(uid);
            if (device == 0) return "";
            var streamsAddr = new PropertyAddress { Selector = FourCC("stm#"), Scope = Output, Element = 0 };
            if (AudioObjectGetPropertyDataSize(device, ref streamsAddr, 0, IntPtr.Zero, out uint streamBytes) != 0 || streamBytes == 0) return "";
            var streams = new uint[streamBytes / 4];
            var sh = GCHandle.Alloc(streams, GCHandleType.Pinned);
            try { if (AudioObjectGetPropertyData(device, ref streamsAddr, 0, IntPtr.Zero, ref streamBytes, sh.AddrOfPinnedObject()) != 0) return ""; }
            finally { sh.Free(); }
            foreach (uint stream in streams)
            {
                // kAudioStreamPropertyAvailablePhysicalFormats: AudioStreamRangedDescription da 56 byte, il formato a +8.
                var formatsAddr = new PropertyAddress { Selector = FourCC("pfta"), Scope = Global, Element = 0 };
                if (AudioObjectGetPropertyDataSize(stream, ref formatsAddr, 0, IntPtr.Zero, out uint fb) != 0 || fb < 56) continue;
                IntPtr buffer = Marshal.AllocHGlobal((int)fb);
                try
                {
                    if (AudioObjectGetPropertyData(stream, ref formatsAddr, 0, IntPtr.Zero, ref fb, buffer) != 0) continue;
                    for (int offset = 0; offset + 56 <= fb; offset += 56)
                    {
                        uint format = (uint)Marshal.ReadInt32(buffer, offset + 8);
                        ac3 |= Ac3Formats.Contains(format);
                        dolbyPlus |= DolbyPlusFormats.Contains(format);
                        trueHd |= TrueHdFormats.Contains(format);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch { }
        return ((ac3 ? "ac3," : "") + (dolbyPlus ? "eac3," : "") + (trueHd ? "truehd" : "")).TrimEnd(',');
    }

    /// <summary>UID vero dell'uscita (vuoto = la predefinita del Mac in questo momento).</summary>
    public static string ResolveUid(string? uid)
    {
        if (!string.IsNullOrEmpty(uid)) return uid;
        uint id = DefaultOutput();
        return id == 0 ? "" : StringProperty(id, "uid ");
    }

    /// <summary>
    /// Uscita dove l'audio spaziale ha senso (stessa regola della patch coreaudio-spatial di libmpv): cuffie Bluetooth
    /// (AirPods), presa cuffie e altoparlanti del Mac. Vuoto = la predefinita del Mac.
    /// </summary>
    public static bool IsSpatialOutput(string? uid)
    {
        uint device = DeviceId(ResolveUid(uid));
        if (device == 0) return false;
        var addr = new PropertyAddress { Selector = FourCC("tran"), Scope = Global, Element = 0 };
        uint size = 4; IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            if (AudioObjectGetPropertyData(device, ref addr, 0, IntPtr.Zero, ref size, buffer) != 0) return false;
            uint transport = (uint)Marshal.ReadInt32(buffer);
            return transport == FourCC("blue") || transport == FourCC("blea") || transport == FourCC("bltn");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Uscita HDMI (kAudioDevicePropertyTransportType = 'hdmi').</summary>
    public static bool IsHdmi(string? uid)
    {
        uint device = DeviceId(uid);
        if (device == 0) return false;
        var addr = new PropertyAddress { Selector = FourCC("tran"), Scope = Global, Element = 0 };
        uint size = 4; IntPtr buffer = Marshal.AllocHGlobal(4);
        try { return AudioObjectGetPropertyData(device, ref addr, 0, IntPtr.Zero, ref size, buffer) == 0 && (uint)Marshal.ReadInt32(buffer) == FourCC("hdmi"); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Bitstream per l'uscita: i formati compressi che il dispositivo dichiara davvero a CoreAudio (con il Marantz via
    /// HDMI: Dolby Digital, Dolby Digital Plus/Atmos, TrueHD/Atmos in MAT). Il DTS su macOS non passa mai via HDMI:
    /// mpv lo decodifica (DTS-HD MA in PCM 7.1 senza perdite). Vuoto = audio decodificato in PCM.
    /// </summary>
    public static string Passthrough(string? uid)
    {
        if (!MacSettings.Bitstream) return "";
        string codecs = SpdifCodecs(uid);
        // TrueHD viaggia solo in HBR (MAT a 768 kHz). Provato il 20/11 sul Marantz: con il cavo USB-C/HDMI (convertitore
        // MegaChips, OUI 00-60-AD) macOS apre 'mtct'/'mtc+' ma il ricevitore non riceve nulla, mentre il PCM 8ch 192 kHz
        // passa. Il dispositivo lo dichiara lo stesso, quindi di serie si decodifica (PCM 7.1 senza perdite);
        // "bitstreamTrueHd" lo riaccende con un collegamento che porta l'HBR (porta HDMI vera o adattatore adatto).
        if (!MacSettings.Get("bitstreamTrueHd", false))
            codecs = string.Join(",", codecs.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(c => c != "truehd"));
        return codecs;
    }

    /// <summary>
    /// DTS in bitstream: macOS non ha un formato DTS sull'HDMI, ma il formato IEC 60958 a 48 kHz ('cac3') porta anche i
    /// pacchetti DTS (mpv issue 18783). Passa cosi' solo il DTS semplice (DTS, DTS-ES, DTS 96/24 = core); DTS-HD MA/HRA e
    /// DTS:X darebbero solo il core con perdita: si decodificano in LPCM 7.1 (senza perdite).
    /// </summary>
    public static bool CanCarryDts(string passthrough) => Array.IndexOf(passthrough.Split(','), "ac3") >= 0;

    public static bool IsPlainDts(string? codec, string? profile) =>
        string.Equals(codec, "dts", StringComparison.OrdinalIgnoreCase) && profile is { Length: > 0 } p &&
        !p.Contains("HD", StringComparison.OrdinalIgnoreCase) && !p.Contains("Express", StringComparison.OrdinalIgnoreCase) &&
        !p.Contains("DTS:X", StringComparison.OrdinalIgnoreCase);

    /// <summary>Chiave che cambia quando cambia il modo di aprire l'audio (riapertura necessaria).</summary>
    public static string PassthroughKey(string? uid) => Passthrough(uid);

    private static uint[] DeviceIds()
    {
        var addr = new PropertyAddress { Selector = FourCC("dev#"), Scope = Global, Element = 0 };
        if (AudioObjectGetPropertyDataSize(1, ref addr, 0, IntPtr.Zero, out uint size) != 0 || size == 0) return Array.Empty<uint>();
        var ids = new uint[size / 4];
        var handle = GCHandle.Alloc(ids, GCHandleType.Pinned);
        try { return AudioObjectGetPropertyData(1, ref addr, 0, IntPtr.Zero, ref size, handle.AddrOfPinnedObject()) == 0 ? ids : Array.Empty<uint>(); }
        finally { handle.Free(); }
    }

    /// <summary>Per le prove: il dispositivo sta suonando per qualcuno (kAudioDevicePropertyDeviceIsRunningSomewhere).</summary>
    public static bool IsRunning(string uid)
    {
        try
        {
            foreach (uint id in DeviceIds())
            {
                if (StringProperty(id, "uid ") != uid) continue;
                var addr = new PropertyAddress { Selector = FourCC("gone"), Scope = Global, Element = 0 };
                uint size = 4; IntPtr buffer = Marshal.AllocHGlobal(4);
                try { return AudioObjectGetPropertyData(id, ref addr, 0, IntPtr.Zero, ref size, buffer) == 0 && Marshal.ReadInt32(buffer) != 0; }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch { }
        return false;
    }

    private static uint DefaultOutput()
    {
        var addr = new PropertyAddress { Selector = FourCC("dOut"), Scope = Global, Element = 0 };
        uint size = 4;
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try { return AudioObjectGetPropertyData(1, ref addr, 0, IntPtr.Zero, ref size, buffer) == 0 ? (uint)Marshal.ReadInt32(buffer) : 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string StringProperty(uint id, string selector)
    {
        var addr = new PropertyAddress { Selector = FourCC(selector), Scope = Global, Element = 0 };
        uint size = (uint)IntPtr.Size;
        IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (AudioObjectGetPropertyData(id, ref addr, 0, IntPtr.Zero, ref size, buffer) != 0) return "";
            IntPtr cf = Marshal.ReadIntPtr(buffer);
            if (cf == IntPtr.Zero) return "";
            try
            {
                var bytes = new byte[1024];
                if (!CFStringGetCString(cf, bytes, bytes.Length, 0x08000100)) return "";
                int end = Array.IndexOf(bytes, (byte)0);
                return System.Text.Encoding.UTF8.GetString(bytes, 0, end >= 0 ? end : bytes.Length);
            }
            finally { CFRelease(cf); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ------------------------------------------------------------------ uscita esclusiva / bit perfect della musica
    /// <summary>ID CoreAudio dell'uscita con questo UID (vuoto = predefinita del Mac); 0 se non c'e'.</summary>
    public static uint DeviceId(string? uid)
    {
        try
        {
            if (string.IsNullOrEmpty(uid)) return DefaultOutput();
            foreach (uint id in DeviceIds()) if (StringProperty(id, "uid ") == uid) return id;
        }
        catch { }
        return 0;
    }

    /// <summary>Frequenza nominale del dispositivo (kAudioDevicePropertyNominalSampleRate), 0 se non si legge.</summary>
    public static double NominalRate(uint device)
    {
        if (device == 0) return 0;
        var addr = new PropertyAddress { Selector = FourCC("nsrt"), Scope = Global, Element = 0 };
        uint size = 8; IntPtr buffer = Marshal.AllocHGlobal(8);
        try { return AudioObjectGetPropertyData(device, ref addr, 0, IntPtr.Zero, ref size, buffer) == 0 ? BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer)) : 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static bool SetNominalRate(uint device, double rate)
    {
        if (device == 0 || rate <= 0) return false;
        if (Math.Abs(NominalRate(device) - rate) < 0.5) return true;
        var addr = new PropertyAddress { Selector = FourCC("nsrt"), Scope = Global, Element = 0 };
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try { Marshal.WriteInt64(buffer, BitConverter.DoubleToInt64Bits(rate)); return AudioObjectSetPropertyData(device, ref addr, 0, IntPtr.Zero, 8, buffer) == 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Frequenze che il dispositivo accetta (kAudioDevicePropertyAvailableNominalSampleRates, intervalli min-max).</summary>
    public static List<(double Min, double Max)> AvailableRates(uint device)
    {
        var list = new List<(double, double)>();
        if (device == 0) return list;
        var addr = new PropertyAddress { Selector = FourCC("nsr#"), Scope = Global, Element = 0 };
        if (AudioObjectGetPropertyDataSize(device, ref addr, 0, IntPtr.Zero, out uint size) != 0 || size < 16) return list;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(device, ref addr, 0, IntPtr.Zero, ref size, buffer) != 0) return list;
            for (int offset = 0; offset + 16 <= size; offset += 16)
                list.Add((BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, offset)), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, offset + 8))));
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    /// <summary>Processo che ha l'uso esclusivo del dispositivo (kAudioDevicePropertyHogMode), -1 = nessuno.</summary>
    private static int HogOwner(uint device)
    {
        var addr = new PropertyAddress { Selector = FourCC("oink"), Scope = Global, Element = 0 };
        uint size = 4; IntPtr buffer = Marshal.AllocHGlobal(4);
        try { return AudioObjectGetPropertyData(device, ref addr, 0, IntPtr.Zero, ref size, buffer) == 0 ? Marshal.ReadInt32(buffer) : -1; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Prende o lascia l'uso esclusivo del dispositivo: con l'app in esclusiva gli altri programmi non ci suonano
    /// (macOS lo rilascia da solo se l'app si chiude). Restituisce true se lo stato e' quello chiesto.
    /// </summary>
    public static bool SetHog(uint device, bool take)
    {
        if (device == 0) return false;
        int me = getpid(), owner = HogOwner(device);
        if (take ? owner == me : owner != me) return true;
        if (take && owner != -1) return false; // e' di un altro programma
        var addr = new PropertyAddress { Selector = FourCC("oink"), Scope = Global, Element = 0 };
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, take ? me : -1);
            AudioObjectSetPropertyData(device, ref addr, 0, IntPtr.Zero, 4, buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return take ? HogOwner(device) == me : HogOwner(device) != me;
    }

    // ------------------------------------------------------------------ formato fisico (bit perfect)
    /// <summary>Descrizione di un formato (AudioStreamBasicDescription, 40 byte).</summary>
    public readonly record struct PhysicalFormat(double Rate, uint FormatId, uint Flags, uint BytesPerPacket, uint FramesPerPacket, uint BytesPerFrame, uint Channels, uint Bits)
    {
        public bool IsIntegerPcm => FormatId == 0x6C70636D /* lpcm */ && (Flags & 1) == 0; // non float
        public override string ToString() => $"{Rate:0} Hz {Channels} ch {Bits} bit {((Flags & 1) != 0 ? "float" : "int")}";
    }

    private static PhysicalFormat ReadFormat(IntPtr p, int o) => new(
        BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, o)), (uint)Marshal.ReadInt32(p, o + 8), (uint)Marshal.ReadInt32(p, o + 12),
        (uint)Marshal.ReadInt32(p, o + 16), (uint)Marshal.ReadInt32(p, o + 20), (uint)Marshal.ReadInt32(p, o + 24),
        (uint)Marshal.ReadInt32(p, o + 28), (uint)Marshal.ReadInt32(p, o + 32));

    private static void WriteFormat(IntPtr p, PhysicalFormat f)
    {
        Marshal.WriteInt64(p, 0, BitConverter.DoubleToInt64Bits(f.Rate));
        Marshal.WriteInt32(p, 8, (int)f.FormatId); Marshal.WriteInt32(p, 12, (int)f.Flags);
        Marshal.WriteInt32(p, 16, (int)f.BytesPerPacket); Marshal.WriteInt32(p, 20, (int)f.FramesPerPacket);
        Marshal.WriteInt32(p, 24, (int)f.BytesPerFrame); Marshal.WriteInt32(p, 28, (int)f.Channels);
        Marshal.WriteInt32(p, 32, (int)f.Bits); Marshal.WriteInt32(p, 36, 0);
    }

    /// <summary>Primo flusso d'uscita del dispositivo (0 se non c'e').</summary>
    private static uint FirstOutputStream(uint device)
    {
        var addr = new PropertyAddress { Selector = FourCC("stm#"), Scope = Output, Element = 0 };
        if (device == 0 || AudioObjectGetPropertyDataSize(device, ref addr, 0, IntPtr.Zero, out uint bytes) != 0 || bytes < 4) return 0;
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try { return AudioObjectGetPropertyData(device, ref addr, 0, IntPtr.Zero, ref bytes, buffer) == 0 ? (uint)Marshal.ReadInt32(buffer) : 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Formato con cui il dispositivo lavora adesso (kAudioStreamPropertyPhysicalFormat).</summary>
    public static PhysicalFormat? CurrentPhysicalFormat(uint device)
    {
        uint stream = FirstOutputStream(device);
        if (stream == 0) return null;
        var addr = new PropertyAddress { Selector = FourCC("pft "), Scope = Global, Element = 0 };
        uint size = 40; IntPtr buffer = Marshal.AllocHGlobal(40);
        try { return AudioObjectGetPropertyData(stream, ref addr, 0, IntPtr.Zero, ref size, buffer) == 0 ? ReadFormat(buffer, 0) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Formati fisici che il dispositivo offre (kAudioStreamPropertyAvailablePhysicalFormats), con l'intervallo di frequenze.</summary>
    public static List<(PhysicalFormat Format, double MinRate, double MaxRate)> AvailablePhysicalFormats(uint device)
    {
        var list = new List<(PhysicalFormat, double, double)>();
        uint stream = FirstOutputStream(device);
        if (stream == 0) return list;
        var addr = new PropertyAddress { Selector = FourCC("pfta"), Scope = Global, Element = 0 };
        if (AudioObjectGetPropertyDataSize(stream, ref addr, 0, IntPtr.Zero, out uint size) != 0 || size < 56) return list;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(stream, ref addr, 0, IntPtr.Zero, ref size, buffer) != 0) return list;
            for (int o = 0; o + 56 <= size; o += 56)
                list.Add((ReadFormat(buffer, o), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, o + 40)), BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, o + 48))));
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    /// <summary>
    /// Bit perfect: il dispositivo lavora in PCM intero alla frequenza del brano con almeno <paramref name="minBits"/> bit
    /// (il piu' piccolo che basta; a parita' lo stesso numero di canali di adesso). Senza questo CoreAudio portava i
    /// campioni al formato in uso: un DAC lasciato a 16 bit troncava i brani a 24. Restituisce il formato scelto o null.
    /// </summary>
    public static PhysicalFormat? ChoosePhysicalFormat(uint device, double rate, int minBits)
    {
        var current = CurrentPhysicalFormat(device);
        var best = AvailablePhysicalFormats(device)
            // Interi con abbastanza bit, oppure float a 32 bit (24 di mantissa: esatto per i brani fino a 24 bit).
            .Where(f => f.Format.FormatId == 0x6C70636D && rate >= f.MinRate - 0.5 && rate <= f.MaxRate + 0.5 &&
                (f.Format.IsIntegerPcm ? f.Format.Bits >= minBits : f.Format.Bits == 32 && minBits <= 24))
            .OrderBy(f => f.Format.IsIntegerPcm ? 0 : 1)
            .ThenBy(f => current is { } c && f.Format.Channels == c.Channels ? 0 : 1)
            .ThenBy(f => f.Format.Bits)
            .ThenByDescending(f => f.Format.Channels == 2)
            .Select(f => (PhysicalFormat?)(f.Format with { Rate = rate }))
            .FirstOrDefault();
        return best;
    }

    public static bool SetPhysicalFormat(uint device, PhysicalFormat format)
    {
        uint stream = FirstOutputStream(device);
        if (stream == 0) return false;
        var addr = new PropertyAddress { Selector = FourCC("pft "), Scope = Global, Element = 0 };
        IntPtr buffer = Marshal.AllocHGlobal(40);
        try { WriteFormat(buffer, format); return AudioObjectSetPropertyData(stream, ref addr, 0, IntPtr.Zero, 40, buffer) == 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
