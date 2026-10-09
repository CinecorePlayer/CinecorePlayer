#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025.Utilities
{
    // DEVMODE rounds refresh rates to integers. CCD retains 24000/1001 versus 24/1.
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setdisplayconfig
    internal sealed class RationalDisplayModeSwitcher
    {
        private const uint ActivePaths = 2, UseConfig = 0x20, Validate = 0x40, Apply = 0x80;
        private const uint InvalidMode = uint.MaxValue;
        private TargetInfo? _originalTarget;
        private ModeInfo _originalMode;
        private SourceMode _originalSource;
        private double _appliedHz;
        private string? _failedDevice;
        private double _failedFps, _failedHz;

        internal static (uint Numerator, uint Denominator) GetFrameRate(double fps)
        {
            foreach (var rate in new[] { (24000u, 1001u), (24u, 1u), (25u, 1u), (30000u, 1001u),
                (30u, 1u), (50u, 1u), (60000u, 1001u), (60u, 1u), (120000u, 1001u), (120u, 1u) })
                if (Math.Abs(fps - rate.Item1 / (double)rate.Item2) < .002)
                    return rate;
            return (0, 0); // VFR/unknown: let the renderer handle it.
        }

        internal static bool MatchesCadence(double hz, double fps)
        {
            if (!double.IsFinite(hz) || !double.IsFinite(fps) || hz <= 0 || fps <= 0) return false;
            double multiple = Math.Round(hz / fps);
            return multiple >= 1 && Math.Abs(hz / multiple - fps) < .002;
        }

        internal static IEnumerable<(uint Numerator, uint Denominator)> GetCandidates(double fps, double currentHz)
        {
            var rate = GetFrameRate(fps);
            if (rate.Numerator == 0 || !double.IsFinite(currentHz) || currentHz <= 0) yield break;
            double exactFps = rate.Numerator / (double)rate.Denominator;
            // Prefer a high matching multiple, without raising the desktop refresh rate.
            int maximum = (int)Math.Clamp(Math.Floor((currentHz + .1) / exactFps), 1, 10);
            for (int multiple = maximum; multiple >= 1; multiple--)
                yield return (rate.Numerator * (uint)multiple, rate.Denominator);
        }

        public bool HasMatchingCadence(Screen screen, double fps)
        {
            try
            {
                if (!TryRead(out var paths, out _)) return false;
                int index = FindScreen(paths, screen.DeviceName);
                return index >= 0 && MatchesCadence(paths[index].Target.Refresh.Hz, fps);
            }
            catch (Exception ex) { Dbg.Warn("[madVR] Read display cadence: " + ex.Message); return false; }
        }

        public bool SwitchToMatching(Screen screen, double fps)
        {
            try
            {
                if (GetFrameRate(fps).Numerator == 0 || !TryRead(out var paths, out var modes)) return false;
                int index = FindScreen(paths, screen.DeviceName);
                if (index < 0) return false;
                var original = paths[index].Target;
                if (_originalTarget is TargetInfo saved &&
                    (saved.Id != original.Id || !saved.Adapter.Equals(original.Adapter)))
                {
                    RestoreIfChanged();
                    return SwitchToMatching(screen, fps);
                }
                double hz = original.Refresh.Hz;
                if (MatchesCadence(hz, fps)) return true;
                if (_failedDevice == screen.DeviceName && Math.Abs(_failedFps - fps) < .001 && Math.Abs(_failedHz - hz) < .001)
                    return false;
                if (original.ModeIndex >= modes.Length || paths[index].Source.ModeIndex >= modes.Length) return false;
                var originalMode = modes[original.ModeIndex];
                var originalSource = modes[paths[index].Source.ModeIndex].Source;
                // Do not alter a cloned source's other display.
                if (paths.Count(p => p.Source.Adapter.Equals(paths[index].Source.Adapter) &&
                    p.Source.Id == paths[index].Source.Id) != 1) return false;

                foreach (var rate in GetCandidates(fps, hz))
                {
                    var candidate = (PathInfo[])paths.Clone();
                    candidate[index].Target.ModeIndex = InvalidMode;
                    candidate[index].Target.Refresh = new Rational { Numerator = rate.Numerator, Denominator = rate.Denominator };
                    candidate[index].Target.Scanline = 1; // progressive
                    int validation = SetDisplayConfig((uint)candidate.Length, candidate, (uint)modes.Length, modes, UseConfig | Validate);
                    if (validation == 0)
                    {
                        if (SetDisplayConfig((uint)candidate.Length, candidate, (uint)modes.Length, modes, UseConfig | Apply) != 0)
                            continue;
                    }
                    else
                    {
                        // NVIDIA can reject a CCD fraction even when its matching GDI
                        // mode exists. Use only an enumerated/tested mode and verify the
                        // resulting CCD fraction; the integer label alone is never success.
                        if (!TryApplyEnumeratedMode(screen.DeviceName, rate.Numerator, rate.Denominator))
                            continue;
                    }
                    double actualHz = rate.Numerator / (double)rate.Denominator;
                    if (TryRead(out var actualPaths, out var actualModes))
                    {
                        int actualIndex = FindTarget(actualPaths, original);
                        if (actualIndex >= 0)
                        {
                            var actual = actualPaths[actualIndex];
                            actualHz = actual.Target.Refresh.Hz;
                            if (MatchesCadence(actualHz, fps) && PreservesGeometry(paths, modes, actualPaths, actualModes, index))
                            {
                                if (_originalTarget == null)
                                {
                                    _originalTarget = original; _originalMode = originalMode; _originalSource = originalSource;
                                }
                                _appliedHz = actualHz;
                                Dbg.Log($"[madVR] Exact display cadence applied: {screen.DeviceName}, fps={fps:0.######}, {hz:0.######} -> {_appliedHz:0.######} Hz.");
                                return true;
                            }
                        }
                    }
                    // Immediately roll back a driver fallback to the wrong rate/resolution.
                    int rollback = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, UseConfig | Apply);
                    Dbg.Warn($"[madVR] Driver fallback rejected: requested={fps:0.######}, actual={actualHz:0.######}, rollback={rollback}.");
                    _failedDevice = screen.DeviceName; _failedFps = fps; _failedHz = hz;
                    return false;
                }
                Dbg.Warn($"[madVR] No supported matching refresh at current resolution: {screen.DeviceName}, fps={fps:0.######}, refresh={hz:0.######}.");
                _failedDevice = screen.DeviceName; _failedFps = fps; _failedHz = hz;
            }
            catch (Exception ex) { Dbg.Warn("[madVR] Exact refresh selection: " + ex.Message); }
            return false;
        }

        private static bool TryApplyEnumeratedMode(string device, uint numerator, uint denominator)
        {
            var current = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsEx(device, -1, ref current, 0)) return false;
            int label = (int)(numerator / denominator);
            bool available = false;
            DevMode selected = default;
            for (int i = 0; ; i++)
            {
                var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
                if (!EnumDisplaySettingsEx(device, i, ref mode, 0)) break;
                if (mode.Width == current.Width && mode.Height == current.Height && mode.Bits == current.Bits &&
                    mode.Orientation == current.Orientation && mode.DisplayFlags == current.DisplayFlags && mode.Frequency == label)
                { available = true; selected = mode; break; }
            }
            if (!available) return false;
            selected.X = current.X; selected.Y = current.Y;
            selected.Fields |= 0x00400020; // DM_DISPLAYFREQUENCY | DM_POSITION
            var test = selected;
            if (ChangeDisplaySettingsEx(device, ref test, IntPtr.Zero, 2 /* CDS_TEST */, IntPtr.Zero) != 0) return false;
            return ChangeDisplaySettingsEx(device, ref selected, IntPtr.Zero, 4 /* CDS_FULLSCREEN */, IntPtr.Zero) == 0;
        }

        private static bool PreservesGeometry(PathInfo[] before, ModeInfo[] beforeModes,
            PathInfo[] after, ModeInfo[] afterModes, int changedIndex)
        {
            if (before.Length != after.Length) return false;
            for (int i = 0; i < before.Length; i++)
            {
                int index = FindTarget(after, before[i].Target);
                if (index < 0) return false;
                var old = before[i]; var current = after[index];
                if (!old.Source.Adapter.Equals(current.Source.Adapter) || old.Source.Id != current.Source.Id ||
                    old.Source.ModeIndex >= beforeModes.Length || current.Source.ModeIndex >= afterModes.Length ||
                    old.Target.ModeIndex >= beforeModes.Length || current.Target.ModeIndex >= afterModes.Length ||
                    !beforeModes[old.Source.ModeIndex].Source.Equals(afterModes[current.Source.ModeIndex].Source) ||
                    !beforeModes[old.Target.ModeIndex].Signal.Active.Equals(afterModes[current.Target.ModeIndex].Signal.Active) ||
                    old.Target.Rotation != current.Target.Rotation || old.Target.Scaling != current.Target.Scaling ||
                    (i != changedIndex && Math.Abs(old.Target.Refresh.Hz - current.Target.Refresh.Hz) > .001)) return false;
            }
            return true;
        }

        public void RestoreIfChanged()
        {
            _failedDevice = null;
            var original = _originalTarget;
            if (original == null) return;
            try
            {
                if (!TryRead(out var paths, out var modes)) return;
                int index = FindTarget(paths, original.Value);
                if (index < 0 || Math.Abs(paths[index].Target.Refresh.Hz - _appliedHz) > .005 ||
                    paths[index].Source.ModeIndex >= modes.Length) return;
                // Respect resolution/layout/refresh changes made by the user during playback.
                var source = modes[paths[index].Source.ModeIndex].Source;
                if (!source.Equals(_originalSource)) return;
                uint restoredIndex = (uint)modes.Length;
                Array.Resize(ref modes, modes.Length + 1);
                modes[restoredIndex] = _originalMode;
                paths[index].Target = original.Value;
                paths[index].Target.ModeIndex = restoredIndex;
                int result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, UseConfig | Apply);
                Dbg.Log($"[madVR] Restore display refresh: {original.Value.Refresh.Hz:0.######} Hz, result={result}.");
            }
            catch (Exception ex) { Dbg.Warn("[madVR] Restore display refresh: " + ex.Message); }
            finally { _originalTarget = null; _appliedHz = 0; }
        }

        private static int FindTarget(PathInfo[] paths, TargetInfo target) => Array.FindIndex(paths,
            p => p.Target.Id == target.Id && p.Target.Adapter.Equals(target.Adapter));

        private static int FindScreen(PathInfo[] paths, string name)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                var request = new SourceName { Header = new DeviceHeader { Type = 1,
                    Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = paths[i].Source.Adapter, Id = paths[i].Source.Id } };
                if (DisplayConfigGetDeviceInfo(ref request) == 0 && string.Equals(request.Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static bool TryRead(out PathInfo[] paths, out ModeInfo[] modes)
        {
            paths = Array.Empty<PathInfo>(); modes = Array.Empty<ModeInfo>();
            // Topology can change between sizing and querying the buffers.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(ActivePaths, out uint pathCount, out uint modeCount) != 0) return false;
                paths = new PathInfo[pathCount]; modes = new ModeInfo[modeCount];
                int result = QueryDisplayConfig(ActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result == 122) continue;
                if (result != 0) return false;
                Array.Resize(ref paths, (int)pathCount); Array.Resize(ref modes, (int)modeCount);
                return true;
            }
            return false;
        }

        [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] private struct Rational
        {
            public uint Numerator, Denominator;
            public readonly double Hz => Denominator == 0 ? 0 : Numerator / (double)Denominator;
        }
        [StructLayout(LayoutKind.Sequential)] private struct SourceInfo { public Luid Adapter; public uint Id, ModeIndex, Status; }
        [StructLayout(LayoutKind.Sequential)] private struct TargetInfo
        {
            public Luid Adapter; public uint Id, ModeIndex, Technology, Rotation, Scaling;
            public Rational Refresh; public uint Scanline; public int Available; public uint Status;
        }
        [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct SizeInfo { public uint Width, Height; }
        [StructLayout(LayoutKind.Sequential)] private struct SignalInfo
        {
            public ulong PixelRate; public Rational HSync, VSync; public SizeInfo Active, Total; public uint Standard, Scanline;
        }
        [StructLayout(LayoutKind.Sequential)] private struct SourceMode { public uint Width, Height, PixelFormat; public int X, Y; }
        [StructLayout(LayoutKind.Explicit, Size = 64)] private struct ModeInfo
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public Luid Adapter;
            [FieldOffset(16)] public SignalInfo Signal;
            [FieldOffset(16)] public SourceMode Source;
        }
        [StructLayout(LayoutKind.Sequential)] private struct DeviceHeader { public uint Type, Size; public Luid Adapter; public uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
        {
            public DeviceHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion, DriverVersion, Size, DriverExtra;
            public int Fields, X, Y, Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int Bits, Width, Height, DisplayFlags, Frequency;
            public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths,
            [Out] PathInfo[] pathArray, ref uint modes, [Out] ModeInfo[] modeArray, IntPtr topology);
        [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint paths, [In] PathInfo[] pathArray,
            uint modes, [In] ModeInfo[] modeArray, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string device, int mode, ref DevMode value, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref DevMode value, IntPtr hwnd, uint flags, IntPtr parameter);
    }
}
