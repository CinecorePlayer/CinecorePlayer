#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.Engines
{
    // Prime voci di IDirectVobSub (VSFilter / XySubFilter): bastano per caricare un file di
    // sottotitoli esterno e scegliere quale "lingua" mostrare. L'ordine e' quello dell'interfaccia nativa.
    [ComImport, Guid("EBE1FB08-3957-47ca-AF13-5827E5442E56"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirectVobSub
    {
        [PreserveSig] int get_FileName(IntPtr buffer);
        [PreserveSig] int put_FileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        [PreserveSig] int get_LanguageCount(out int count);
        [PreserveSig] int get_LanguageName(int index, out IntPtr name);
        [PreserveSig] int get_SelectedLanguage(out int index);
        [PreserveSig] int put_SelectedLanguage(int index);
        [PreserveSig] int get_HideSubtitles([MarshalAs(UnmanagedType.U1)] out bool hidden);
        [PreserveSig] int put_HideSubtitles([MarshalAs(UnmanagedType.U1)] bool hidden);
    }

    /// <summary>Sottotitoli esterni nel filtro XySubFilter (percorso madVR).</summary>
    internal static class XySubtitleBridge
    {
        /// <summary>Fa cercare al filtro i sottotitoli con lo stesso nome di <paramref name="videoLikePath"/>
        /// nella sua cartella: il file video indicato non deve esistere, conta solo il nome.</summary>
        public static bool Load(object? filter, string videoLikePath)
        {
            try { return filter is IDirectVobSub sub && sub.put_FileName(videoLikePath) == 0; }
            catch (Exception ex) { Dbg.Warn("[XYSUB] load: " + ex.Message); return false; }
        }

        public static List<string> Languages(object? filter)
        {
            var result = new List<string>();
            try
            {
                if (filter is not IDirectVobSub sub || sub.get_LanguageCount(out int count) < 0) return result;
                for (int i = 0; i < Math.Min(count, 64); i++)
                {
                    string name = "";
                    if (sub.get_LanguageName(i, out IntPtr text) >= 0 && text != IntPtr.Zero)
                    {
                        name = Marshal.PtrToStringUni(text) ?? "";
                        Marshal.FreeCoTaskMem(text);
                    }
                    result.Add(name);
                }
            }
            catch (Exception ex) { Dbg.Warn("[XYSUB] languages: " + ex.Message); }
            return result;
        }

        public static int Selected(object? filter)
        {
            try { return filter is IDirectVobSub sub && sub.get_SelectedLanguage(out int index) >= 0 ? index : -1; }
            catch { return -1; }
        }

        public static bool Hidden(object? filter)
        {
            try { return filter is IDirectVobSub sub && sub.get_HideSubtitles(out bool hidden) >= 0 && hidden; }
            catch { return false; }
        }

        public static bool Select(object? filter, int index)
        {
            try
            {
                if (filter is not IDirectVobSub sub) return false;
                sub.put_HideSubtitles(false);
                return sub.put_SelectedLanguage(index) >= 0;
            }
            catch (Exception ex) { Dbg.Warn("[XYSUB] select: " + ex.Message); return false; }
        }

        public static void SetHidden(object? filter, bool hidden)
        {
            try { if (filter is IDirectVobSub sub) sub.put_HideSubtitles(hidden); }
            catch (Exception ex) { Dbg.Warn("[XYSUB] hide: " + ex.Message); }
        }
    }
}
