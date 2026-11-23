using System.Runtime.InteropServices;

namespace CinecoreVideoHost;

// LAVAudioSettings.h (0.80): anche i metodi non invocati occupano uno slot della vtable.
[ComImport, Guid("4158A22B-6553-45D0-8069-24716F8FF171"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ILavAudioSettings
{
    [PreserveSig] int SetRuntimeConfig(int runtime);
    [PreserveSig] int GetDRC(out int enabled, out int level);
    [PreserveSig] int SetDRC(int enabled, int level);
    [PreserveSig] int GetFormatConfiguration(int codec);
    [PreserveSig] int SetFormatConfiguration(int codec, int enabled);
    [PreserveSig] int GetBitstreamConfig(int codec);
    [PreserveSig] int SetBitstreamConfig(int codec, int enabled);
    [PreserveSig] int GetDTSHDFraming();
    [PreserveSig] int SetDTSHDFraming(int enabled);
    [PreserveSig] int GetAutoAVSync();
    [PreserveSig] int SetAutoAVSync(int enabled);
    [PreserveSig] int GetOutputStandardLayout();
    [PreserveSig] int SetOutputStandardLayout(int enabled);
    [PreserveSig] int GetExpandMono();
    [PreserveSig] int SetExpandMono(int enabled);
    [PreserveSig] int GetExpand61();
    [PreserveSig] int SetExpand61(int enabled);
    [PreserveSig] int GetAllowRawSPDIFInput();
    [PreserveSig] int SetAllowRawSPDIFInput(int enabled);
    [PreserveSig] int GetSampleFormat(int format);
    [PreserveSig] int SetSampleFormat(int format, int enabled);
    [PreserveSig] int GetAudioDelay(out int enabled, out int delay);
    [PreserveSig] int SetAudioDelay(int enabled, int delay);
}
