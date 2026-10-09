#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CinecorePlayer2025.Engines
{
    public sealed partial class DirectShowUnifiedEngine
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MadVrSize
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MadVrRect
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [ComImport, Guid("8FAB7F31-06EF-444C-A798-10314E185532"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMadVRInfo
        {
            [PreserveSig] int GetBool([MarshalAs(UnmanagedType.LPStr)] string field, [MarshalAs(UnmanagedType.I1)] out bool value);
            [PreserveSig] int GetInt([MarshalAs(UnmanagedType.LPStr)] string field, out int value);
            [PreserveSig] int GetSize([MarshalAs(UnmanagedType.LPStr)] string field, out MadVrSize value);
            [PreserveSig] int GetRect([MarshalAs(UnmanagedType.LPStr)] string field, out MadVrRect value);
            [PreserveSig] int GetUlonglong([MarshalAs(UnmanagedType.LPStr)] string field, out ulong value);
            [PreserveSig] int GetDouble([MarshalAs(UnmanagedType.LPStr)] string field, out double value);
            [PreserveSig] int GetString([MarshalAs(UnmanagedType.LPStr)] string field, out IntPtr value, out int chars);
            [PreserveSig] int GetBin([MarshalAs(UnmanagedType.LPStr)] string field, out IntPtr value, out int size);
        }

        [ComImport, Guid("5E9599D1-C5DB-4A84-98A9-09BC5F8F1B79"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMadVRCommand
        {
            [PreserveSig] int SendCommand([MarshalAs(UnmanagedType.LPStr)] string command);
            [PreserveSig] int SendCommandBool([MarshalAs(UnmanagedType.LPStr)] string command, [MarshalAs(UnmanagedType.I1)] bool parameter);
            [PreserveSig] int SendCommandInt([MarshalAs(UnmanagedType.LPStr)] string command, int parameter);
            [PreserveSig] int SendCommandSize([MarshalAs(UnmanagedType.LPStr)] string command, MadVrSize parameter);
            [PreserveSig] int SendCommandRect([MarshalAs(UnmanagedType.LPStr)] string command, MadVrRect parameter);
            [PreserveSig] int SendCommandUlonglong([MarshalAs(UnmanagedType.LPStr)] string command, ulong parameter);
            [PreserveSig] int SendCommandDouble([MarshalAs(UnmanagedType.LPStr)] string command, double parameter);
            [PreserveSig] int SendCommandString([MarshalAs(UnmanagedType.LPStr)] string command, [MarshalAs(UnmanagedType.LPWStr)] string parameter);
            [PreserveSig] int SendCommandBin([MarshalAs(UnmanagedType.LPStr)] string command, IntPtr parameter, int size);
        }

        // madVR SDK: these methods return Win32 BOOL, not HRESULT.
        [ComImport, Guid("6F8A566C-4E19-439E-8F07-20E46ED06DEE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMadVRSettings
        {
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetRevision(out long revision);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsExport(out IntPtr buffer, out int size);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsImport(IntPtr buffer, int size);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetString([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetInteger([MarshalAs(UnmanagedType.LPWStr)] string path, int value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsSetBoolean([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetString([MarshalAs(UnmanagedType.LPWStr)] string path, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, ref int length);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetInteger([MarshalAs(UnmanagedType.LPWStr)] string path, out int value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetBoolean([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool SettingsGetBinary([MarshalAs(UnmanagedType.LPWStr)] string path, out IntPtr value, out int size);
        }

        [ComImport, Guid("1CC2385F-36FA-41B1-9942-5024CE0235DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ILavVideoStatus
        {
            [PreserveSig] IntPtr GetActiveDecoderName();
            [PreserveSig] int GetHWAccelActiveDevice([MarshalAs(UnmanagedType.BStr)] out string device);
        }

        private string GetVideoDecoderDiagnosticSnapshot()
        {
            try
            {
                if (_lavVideo is not ILavVideoStatus status) return "decoder=unavailable";
                string decoder = Marshal.PtrToStringUni(status.GetActiveDecoderName()) ?? "inactive";
                int hr = status.GetHWAccelActiveDevice(out string device);
                return $"decoder={decoder}, decoderDevice={(hr >= 0 ? device : "unavailable")}";
            }
            catch { return "decoder=unavailable"; }
        }

        public (int Drawn, int Dropped)? GetMadVrFrameCounters()
        {
            try
            {
                if (_choice != VideoRendererChoice.MADVR || _videoRenderer is not IQualProp quality) return null;
                if (quality.get_FramesDrawn(out int drawn) < 0 || quality.get_FramesDroppedInRenderer(out int dropped) < 0) return null;
                return (drawn, dropped);
            }
            catch { return null; }
        }

        public double GetMadVrPresentedFrameRate()
        {
            try
            {
                // madVR SDK: frameRate is the post-deinterlacing frame duration
                // in 100 ns units, not an FPS value or the display refresh rate.
                var info = TryGetMadVrInfoInterface();
                if (info != null && info.GetUlonglong("frameRate", out ulong duration) == 0 && duration > 0)
                {
                    double fps = 10_000_000d / duration;
                    if (fps >= 1 && fps <= 240) return fps;
                }
            }
            catch { }
            return 0;
        }

        public string GetMadVrTimingDiagnostic()
        {
            try
            {
                if (_videoRenderer is not IQualProp quality) return "timing=unavailable";
                string jitter = quality.get_Jitter(out int j) >= 0 ? j.ToString() : "n/d";
                string offset = quality.get_AvgSyncOffset(out int o) >= 0 ? o.ToString() : "n/d";
                return $"sourceFps={GetMadVrPresentedFrameRate():0.######}, jitterMs={jitter}, syncOffsetMs={offset}";
            }
            catch { return "timing=unavailable"; }
        }

        private const string SmoothEnabledPath = "rendering\\smoothMotion\\smoothMotionEnabled";
        private const string SmoothModePath = "rendering\\smoothMotion\\smoothMotionMode";
        private bool? _madVrOriginalSmoothEnabled;
        private string? _madVrOriginalSmoothMode;

        private IMadVRSettings? TryGetMadVrSettings()
        {
            if (_choice != VideoRendererChoice.MADVR || _videoRenderer == null) return null;
            try { return (IMadVRSettings)_videoRenderer; } catch { return null; }
        }

        private static string? ReadMadVrSettingString(IMadVRSettings settings, string path)
        {
            var text = new StringBuilder(512);
            int length = text.Capacity;
            return settings.SettingsGetString(path, text, ref length) ? text.ToString() : null;
        }

        // Le build precedenti forzavano smooth motion su "always" (e cambiavano la frequenza
        // dello schermo): su un pannello a 144/165 Hz madVR fonde ogni fotogramma con il
        // successivo = ghosting, e il carico GPU fa perdere frame. Il valore è rimasto salvato
        // in settings.bin/registro: lo riportiamo una sola volta al default di madVR (spento).
        private static readonly string MadVrRepairMarker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "madvr-smoothmotion-repair-v1");

        public void RepairMadVrForcedSmoothMotion()
        {
            try
            {
                if (File.Exists(MadVrRepairMarker)) return;
                var settings = TryGetMadVrSettings();
                if (settings == null || !settings.SettingsGetBoolean(SmoothEnabledPath, out bool enabled)) return;
                string? mode = ReadMadVrSettingString(settings, SmoothModePath);
                if (mode == "always")
                {
                    settings.SettingsSetString(SmoothModePath, "avoidJudder");
                    if (enabled) settings.SettingsSetBoolean(SmoothEnabledPath, false);
                    Dbg.Log("[madVR] Forced smooth motion left by an older build has been reset (off, only when needed).");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(MadVrRepairMarker)!);
                File.WriteAllText(MadVrRepairMarker, DateTime.Now.ToString("O"));
            }
            catch (Exception ex) { Dbg.Warn("[madVR] Smooth motion repair: " + ex.Message); }
        }

        public void RestoreMadVrCadenceSettings()
        {
            if (_madVrOriginalSmoothEnabled is not bool enabled) return;
            try
            {
                var settings = TryGetMadVrSettings();
                if (settings == null) return;
                // Restore only our override; preserve explicit edits made during playback.
                bool modeIsOurs = ReadMadVrSettingString(settings, SmoothModePath) == "always";
                if (modeIsOurs)
                    settings.SettingsSetString(SmoothModePath, _madVrOriginalSmoothMode!);
                if (modeIsOurs && settings.SettingsGetBoolean(SmoothEnabledPath, out bool current) && current)
                    settings.SettingsSetBoolean(SmoothEnabledPath, enabled);
                Dbg.Log("[madVR] Original smooth motion settings restored.");
            }
            catch (Exception ex) { Dbg.Warn("[madVR] Restore cadence settings: " + ex.Message); }
            finally { _madVrOriginalSmoothEnabled = null; _madVrOriginalSmoothMode = null; }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>
        /// madVR, se ha il rilevamento delle bande nere attivo, espone il ritaglio che ha trovato:
        /// e' una misura fatta sul fotogramma in uscita, piu' affidabile di un campionamento esterno.
        /// </summary>
        public double? RendererActiveAspect()
        {
            if (_choice != VideoRendererChoice.MADVR) return null;
            try
            {
                var info = TryGetMadVrInfoInterface();
                if (info == null) return null;
                if (info.GetRect("videoCropRect", out var crop) != 0 || info.GetSize("originalVideoSize", out var original) != 0) return null;
                int cropW = crop.right - crop.left, cropH = crop.bottom - crop.top;
                if (original.cx <= 0 || original.cy <= 0 || cropW <= 0 || cropH <= 0) return null;
                if (cropW > original.cx * 0.97 && cropH > original.cy * 0.97) return null;
                double pixelAspect = 1;
                if (info.GetSize("arAdjustedVideoSize", out var adjusted) == 0 && adjusted.cx > 0 && adjusted.cy > 0)
                    pixelAspect = (adjusted.cx / (double)original.cx) / (adjusted.cy / (double)original.cy);
                return ImageSizing.Snap(cropW * pixelAspect / cropH);
            }
            catch { return null; }
        }

        private IMadVRInfo? TryGetMadVrInfoInterface()
        {
            if (_choice != VideoRendererChoice.MADVR || _videoRenderer == null)
                return null;

            try { return (IMadVRInfo)_videoRenderer; }
            catch { return null; }
        }

        private IMadVRCommand? TryGetMadVrCommandInterface()
        {
            if (_choice != VideoRendererChoice.MADVR || _videoRenderer == null)
                return null;

            try { return (IMadVRCommand)_videoRenderer; }
            catch { return null; }
        }

        private static string HrText(int hr) => $"0x{hr:X8}";

        private static string ReadMadVrString(IMadVRInfo info, string field)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                int hr = info.GetString(field, out ptr, out int chars);
                if (hr != 0 || ptr == IntPtr.Zero || chars <= 0)
                    return $"n/d(hr={HrText(hr)})";

                return Marshal.PtrToStringUni(ptr, chars)?.TrimEnd('\0') ?? "n/d";
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                {
                    try { LocalFree(ptr); } catch { }
                }
            }
        }

        private static string ReadMadVrBool(IMadVRInfo info, string field)
        {
            try
            {
                int hr = info.GetBool(field, out bool value);
                return hr == 0 ? value.ToString() : $"n/d(hr={HrText(hr)})";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        private static string ReadMadVrDouble(IMadVRInfo info, string field)
        {
            try
            {
                int hr = info.GetDouble(field, out double value);
                return hr == 0 ? value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : $"n/d(hr={HrText(hr)})";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        private static string ReadMadVrSize(IMadVRInfo info, string field)
        {
            try
            {
                int hr = info.GetSize(field, out var value);
                return hr == 0 ? $"{value.cx}x{value.cy}" : $"n/d(hr={HrText(hr)})";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        public string GetRendererDiagnosticSnapshot()
        {
            if (_choice == VideoRendererChoice.MPCVR)
            {
                var cf = _cachedFmt;
                int dstW = Math.Max(0, _lastDest.right - _lastDest.left);
                int dstH = Math.Max(0, _lastDest.bottom - _lastDest.top);
                string display = _mfDisplay != null ? "IMFVideoDisplayControl" :
                                 _videoWindow != null ? "IVideoWindow" :
                                 _videoRenderer != null ? "renderer-attached-pending-window" : "unavailable";
                string fmt = cf.W > 0 && cf.H > 0 ? $"{cf.W}x{cf.H} {cf.Sub}" : "n/d";
                var config = TryGetMpcvrConfig();
                string cfg = config != null
                    ? $", version={ReadMpcvrInt64(config, "version")}, renderType={ReadMpcvrInt(config, "renderType")}, playbackState={ReadMpcvrInt(config, "playbackState")}, stats={ReadMpcvrBool(config, "statsEnable")}"
                    : ", config=unavailable";
                return $"renderer=MPCVR, display={display}, dest={dstW}x{dstH}, fmt={fmt}, upscaling={_mpcvrUpscalingStatus}{cfg}";
            }

            if (_choice != VideoRendererChoice.MADVR)
                return $"renderer={_choice}";

            var info = TryGetMadVrInfoInterface();
            if (info == null)
                return "renderer=MADVR, madVRInfo=unavailable";

            return "renderer=MADVR"
                   + $", version={ReadMadVrString(info, "version")}"
                   + $", {GetVideoDecoderDiagnosticSnapshot()}"
                   + $", originalVideoSize={ReadMadVrSize(info, "originalVideoSize")}"
                   + $", hdrOutput={ReadMadVrBool(info, "hdrOutput")}"
                   + $", exclusiveModeActive={ReadMadVrBool(info, "exclusiveModeActive")}"
                   + $", madVRSeekbarEnabled={ReadMadVrBool(info, "madVRSeekbarEnabled")}"
                   + $", displayModeSize={ReadMadVrSize(info, "displayModeSize")}"
                   + $", refreshRate={ReadMadVrDouble(info, "refreshRate")}"
                   + $", {GetMadVrTimingDiagnostic()}"
                   + $", yuvMatrix={ReadMadVrString(info, "yuvMatrix")}";
        }

        public bool TrySetMadVrExclusiveModeDisabled(bool disabled)
        {
            var command = TryGetMadVrCommandInterface();
            if (command == null)
            {
                Dbg.Warn($"[madVR] IMadVRCommand unavailable; cannot set disableExclusiveMode={disabled}. {GetRendererDiagnosticSnapshot()}");
                return false;
            }

            try
            {
                int hr = command.SendCommandBool("disableExclusiveMode", disabled);
                if (hr != 0)
                {
                    Dbg.Warn($"[madVR] SendCommandBool(disableExclusiveMode,{disabled}) failed hr={HrText(hr)}. {GetRendererDiagnosticSnapshot()}");
                    return false;
                }

                if (disabled)
                {
                    try { command.SendCommand("restoreDisplayModeNow"); } catch { }
                }

                Dbg.Log($"[madVR] disableExclusiveMode={disabled} applied. {GetRendererDiagnosticSnapshot()}");
                return true;
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[madVR] disableExclusiveMode={disabled} EX: {ex.Message}");
                return false;
            }
        }

        public bool TrySendRendererKey(Keys keyData)
        {
            if (_choice != VideoRendererChoice.MADVR)
                return false;

            var command = TryGetMadVrCommandInterface();
            if (command == null)
            {
                Dbg.Warn($"[HOTKEY] IMadVRCommand unavailable for key={keyData}; falling back to HWND/SendInput.");
                return false;
            }

            var keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.None)
                return false;

            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;

            int packed = ((int)keyCode & 0xFF)
                       | ((shift ? 1 : 0) << 8)
                       | ((ctrl ? 1 : 0) << 16)
                       | ((alt ? 1 : 0) << 24);

            try
            {
                int hr = command.SendCommandInt("keyPress", packed);
                if (hr != 0)
                {
                    Dbg.Warn($"[HOTKEY] IMadVRCommand keyPress failed key={keyData}, packed=0x{packed:X8}, hr={HrText(hr)}");
                    return false;
                }

                Dbg.Log($"[HOTKEY] IMadVRCommand keyPress delivered key={keyData}, packed=0x{packed:X8}. {GetRendererDiagnosticSnapshot()}");
                return true;
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[HOTKEY] IMadVRCommand keyPress EX key={keyData}: {ex.Message}");
                return false;
            }
        }


        public Rectangle GetLastDestRectAsClient(Rectangle ownerClient)
        {
            try
            {
                int l = _lastDest.left, t = _lastDest.top;
                int w = Math.Max(0, _lastDest.right - _lastDest.left);
                int h = Math.Max(0, _lastDest.bottom - _lastDest.top);
                if (w <= 0 || h <= 0) return ownerClient;
                return new Rectangle(l, t, w, h);
            }
            catch { return ownerClient; }
        }

        private void SafeStatus(string s)
        {
            try { OnStatus?.Invoke(s); }
            catch (Exception ex) { Dbg.Warn("OnStatus handler EX: " + ex.Message); }
        }

        // ======== Anteprima overlay via Thumbnailer ========
        public Bitmap? GetPreviewFrame(double seconds, int maxW = 360)
        {
            try
            {
                if (!_hasVideo)
                    return null;

                // lazy-reopen nel caso _thumb sia stato rilasciato/chiuso
                if (_thumb == null && !string.IsNullOrEmpty(_currentMediaPath))
                {
                    try
                    {
                        _thumb = new Thumbnailer();
                        _thumb.Open(_currentMediaPath!);
                        Dbg.Log("Thumbnailer riaperto lazy.");
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn("GetPreviewFrame lazy Open EX: " + ex.Message);
                        return null;
                    }
                }

                if (_thumb == null) return null;

                double dur = DurationSeconds;
                if (dur > 0) seconds = Math.Max(0, Math.Min(seconds, Math.Max(0, dur - 0.05)));

                var bmp = _thumb.Get(seconds, maxW, realtime: true);
                if (bmp != null) return bmp;

                // One accurate retry is bounded and avoids the old five-seek stall.
                return _thumb.Get(seconds, maxW, realtime: false);
            }
            catch (Exception ex)
            {
                Dbg.Warn("GetPreviewFrame EX: " + ex.Message);
            }
            return null;
        }

        // ======== HOTKEY BRIDGE (SendInput) ========
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo;
        }
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
        [DllImport("user32.dll")] private static extern nint SetFocus(nint hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow); // SW_RESTORE=9
                                                                                                  // --- madVR hotkeys forward ---
        private nint _madvrHwnd = nint.Zero;


        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(nint hWndParent, EnumChildProc lpEnumFunc, nint lParam);
        private delegate bool EnumChildProc(nint hWnd, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(nint hWnd, int Msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(nint hWnd);

        private static string GetWindowClassSafe(nint hWnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                int n = GetClassName(hWnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string GetWindowTextSafe(nint hWnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                int n = GetWindowText(hWnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private nint ResolveMadVrHotkeyWindow()
        {
            try
            {
                if (_madvrHwnd != nint.Zero && IsWindow(_madvrHwnd))
                    return _madvrHwnd;

                nint owner = _lastOwnerHwnd;
                nint found = nint.Zero;

                if (owner != nint.Zero && IsWindow(owner))
                {
                    try
                    {
                        EnumChildWindows(owner, (h, _) =>
                        {
                            string hay = (GetWindowClassSafe(h) + " " + GetWindowTextSafe(h)).ToLowerInvariant();
                            if (hay.Contains("madvr") || hay.Contains("madshi"))
                            {
                                found = h;
                                return false;
                            }
                            return true;
                        }, nint.Zero);
                    }
                    catch { }

                    if (found == nint.Zero)
                        found = owner;
                }

                _madvrHwnd = found;
                return found;
            }
            catch { return nint.Zero; }
        }

        private static string FormatChord(bool ctrl, bool alt, bool shift, Keys key)
        {
            var parts = new List<string>(4);
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        private void SendKey(ushort vk, bool down)
        {
            var inp = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = down ? 0u : KEYEVENTF_KEYUP, time = 0, dwExtraInfo = nint.Zero }
                }
            };
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
        }
        private void SendChord(bool ctrl, bool alt, bool shift, Keys key)
        {
            Dbg.Log($"[HOTKEY] SendChord request: renderer={_choice}, chord={FormatChord(ctrl, alt, shift, key)}, owner=0x{_lastOwnerHwnd.ToInt64():X}", Dbg.LogLevel.Info);

            if (_choice == VideoRendererChoice.MADVR)
            {
                Keys keyData = key;
                if (ctrl) keyData |= Keys.Control;
                if (alt) keyData |= Keys.Alt;
                if (shift) keyData |= Keys.Shift;

                if (TrySendRendererKey(keyData))
                {
                    Dbg.Log($"[HOTKEY] SendChord delivered through IMadVRCommand: {FormatChord(ctrl, alt, shift, key)}", Dbg.LogLevel.Info);
                    return;
                }
            }

            nint madvrHotkeyWindow = nint.Zero;
            if (_choice == VideoRendererChoice.MADVR)
            {
                try
                {
                    madvrHotkeyWindow = ResolveMadVrHotkeyWindow();
                    Dbg.Log($"[HOTKEY] SendChord target resolved: hwnd=0x{madvrHotkeyWindow.ToInt64():X}, class='{GetWindowClassSafe(madvrHotkeyWindow)}', text='{GetWindowTextSafe(madvrHotkeyWindow)}'", Dbg.LogLevel.Info);
                }
                catch (Exception ex) { Dbg.Warn("madVR hotkey target resolve EX: " + ex.Message); }
            }

            if (_lastOwnerHwnd != nint.Zero)
            {
                if (IsIconic(_lastOwnerHwnd)) ShowWindow(_lastOwnerHwnd, 9);
                SetForegroundWindow(_lastOwnerHwnd);
                try { Thread.Sleep(25); } catch { }
            }

            if (madvrHotkeyWindow != nint.Zero && IsWindow(madvrHotkeyWindow))
            {
                try
                {
                    SetFocus(madvrHotkeyWindow);
                    Dbg.Log($"[HOTKEY] Focus set before SendInput: hwnd=0x{madvrHotkeyWindow.ToInt64():X}", Dbg.LogLevel.Info);
                    Thread.Sleep(15);
                }
                catch (Exception ex)
                {
                    Dbg.Warn("madVR SetFocus before SendInput EX: " + ex.Message);
                }
            }

            if (ctrl) SendKey((ushort)Keys.ControlKey, true);
            if (alt) SendKey((ushort)Keys.Menu, true);
            if (shift) SendKey((ushort)Keys.ShiftKey, true);

            try { Thread.Sleep(8); } catch { }
            SendKey((ushort)key, true);
            try { Thread.Sleep(8); } catch { }
            SendKey((ushort)key, false);
            try { Thread.Sleep(8); } catch { }

            if (shift) SendKey((ushort)Keys.ShiftKey, false);
            if (alt) SendKey((ushort)Keys.Menu, false);
            if (ctrl) SendKey((ushort)Keys.ControlKey, false);
            Dbg.Log($"[HOTKEY] SendChord delivered through focused SendInput: {FormatChord(ctrl, alt, shift, key)}, target=0x{madvrHotkeyWindow.ToInt64():X}", Dbg.LogLevel.Info);
        }

        // ======== API pubbliche: mapping → scorciatoie madVR ========
        public void SetMadVrChroma(MadVrCategoryPreset preset)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrChroma: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (preset)
            {
                case MadVrCategoryPreset.RendererDefault: SendChord(true, true, false, Keys.C); break;
                case MadVrCategoryPreset.Profile1: SendChord(true, true, false, Keys.F1); break;
                case MadVrCategoryPreset.Profile2: SendChord(true, true, false, Keys.F2); break;
                case MadVrCategoryPreset.Profile3: SendChord(true, true, false, Keys.F3); break;
                case MadVrCategoryPreset.Profile4: SendChord(true, true, false, Keys.F4); break;
                case MadVrCategoryPreset.Profile5: SendChord(true, true, false, Keys.F5); break;
                case MadVrCategoryPreset.Profile6: SendChord(true, true, false, Keys.F6); break;
            }
            Dbg.Log($"madVR Chroma preset → {preset}");
        }

        public void SetMadVrImageUpscale(MadVrCategoryPreset preset)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrImageUpscale: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (preset)
            {
                case MadVrCategoryPreset.RendererDefault: SendChord(true, true, false, Keys.U); break;
                case MadVrCategoryPreset.Profile1: SendChord(true, true, false, Keys.F7); break;
                case MadVrCategoryPreset.Profile2: SendChord(true, true, false, Keys.F8); break;
                case MadVrCategoryPreset.Profile3: SendChord(true, true, false, Keys.F9); break;
                case MadVrCategoryPreset.Profile4: SendChord(true, true, false, Keys.F10); break;
                case MadVrCategoryPreset.Profile5: SendChord(true, true, false, Keys.F11); break;
                case MadVrCategoryPreset.Profile6: SendChord(true, true, false, Keys.F12); break;
            }
            Dbg.Log($"madVR ImageUpscale preset → {preset}");
        }

        public void SetMadVrImageDownscale(MadVrCategoryPreset preset)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrImageDownscale: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (preset)
            {
                case MadVrCategoryPreset.RendererDefault: SendChord(true, true, false, Keys.D); break;
                case MadVrCategoryPreset.Profile1: SendChord(true, true, false, Keys.D1); break;
                case MadVrCategoryPreset.Profile2: SendChord(true, true, false, Keys.D2); break;
                case MadVrCategoryPreset.Profile3: SendChord(true, true, false, Keys.D3); break;
                case MadVrCategoryPreset.Profile4: SendChord(true, true, false, Keys.D4); break;
                case MadVrCategoryPreset.Profile5: SendChord(true, true, false, Keys.D5); break;
                case MadVrCategoryPreset.Profile6: SendChord(true, true, false, Keys.D6); break;
            }
            Dbg.Log($"madVR Downscale preset → {preset}");
        }

        public void SetMadVrRefinement(MadVrCategoryPreset preset)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrRefinement: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (preset)
            {
                case MadVrCategoryPreset.RendererDefault: SendChord(true, true, false, Keys.R); break;
                case MadVrCategoryPreset.Profile1: SendChord(true, true, true, Keys.D1); break; // Ctrl+Alt+Shift+1
                case MadVrCategoryPreset.Profile2: SendChord(true, true, true, Keys.D2); break;
                case MadVrCategoryPreset.Profile3: SendChord(true, true, true, Keys.D3); break;
                case MadVrCategoryPreset.Profile4: SendChord(true, true, true, Keys.D4); break;
                case MadVrCategoryPreset.Profile5: SendChord(true, true, true, Keys.D5); break;
                case MadVrCategoryPreset.Profile6: SendChord(true, true, true, Keys.D6); break;
            }
            Dbg.Log($"madVR Refinement preset → {preset}");
        }

        public void SetMadVrFps(MadVrFpsChoice choice)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrFps: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (choice)
            {
                // assegna in madVR questi tre comandi ai relativi hotkey
                case MadVrFpsChoice.Adapt: SendChord(true, true, false, Keys.NumPad7); break;
                case MadVrFpsChoice.Force60: SendChord(true, true, false, Keys.NumPad8); break;
                case MadVrFpsChoice.Force24: SendChord(true, true, false, Keys.NumPad9); break;
            }
            Dbg.Log($"madVR FPS choice → {choice}");
        }

        public void SetMadVrHdrMode(MadVrHdrMode mode)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrHdrMode: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            Dbg.Log($"[HDR] SetMadVrHdrMode requested: mode={mode}, owner=0x{_lastOwnerHwnd.ToInt64():X}");
            // Le vecchie scorciatoie (Ctrl+Alt+H/S/L…) non erano associate a nulla in madVR:
            // scriviamo direttamente la modalità HDR nell'istanza che sta riproducendo.
            string profile = mode switch
            {
                MadVrHdrMode.PassthroughHdr => "passthrough",
                MadVrHdrMode.ToneMapHdrToSdr => "tonemap",
                MadVrHdrMode.LutHdrToSdr => "lut",
                _ => "auto"
            };
            if (!HUD.SettingsHudPage.WriteMadVrHdrProfile(profile))
                Dbg.Warn($"[HDR] madVR refused HDR profile {profile}.");
            Dbg.Log($"madVR HDR mode → {mode}");
        }
    }
}
