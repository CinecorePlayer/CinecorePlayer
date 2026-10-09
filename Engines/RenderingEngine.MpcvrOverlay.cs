#nullable enable
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecorePlayer2025.Engines
{
    public sealed partial class DirectShowUnifiedEngine
    {
        private sealed class MpcvrMixerBitmapOverlay : IDisposable
        {
            private readonly IMFVideoMixerBitmap _mixer;
            private nint _hdc;
            private nint _oldBitmap;
            private nint _bitmap;
            private bool _disposed;
            private bool _hasActiveBitmap;
            private int _setFailureLogCount;

            public MpcvrMixerBitmapOverlay(IMFVideoMixerBitmap mixer)
            {
                _mixer = mixer;
            }

            public bool SetBitmap(Bitmap source, Color transparentKey)
            {
                if (_disposed || source.Width <= 0 || source.Height <= 0)
                    return false;

                EnsureDc();

                nint nextBitmap = nint.Zero;
                try
                {
                    using var prepared = PreparePremultipliedBitmap(source, transparentKey);
                    nextBitmap = CreateDibSectionFromBitmap(prepared);
                    nint previous = SelectObject(_hdc, nextBitmap);
                    if (_oldBitmap == nint.Zero)
                        _oldBitmap = previous;

                    if (_bitmap != nint.Zero)
                        DeleteObject(_bitmap);

                    _bitmap = nextBitmap;
                    nextBitmap = nint.Zero;

                    int colorRef = transparentKey.R | (transparentKey.G << 8) | (transparentKey.B << 16);
                    var alpha = new MFVideoAlphaBitmap
                    {
                        GetBitmapFromDC = true,
                        bitmap = _hdc,
                        @params = new MFVideoAlphaBitmapParams
                        {
                            dwFlags = (int)(MFVideoAlphaBitmapFlags.SrcColorKey |
                                            MFVideoAlphaBitmapFlags.SrcRect |
                                            MFVideoAlphaBitmapFlags.DestRect |
                                            MFVideoAlphaBitmapFlags.Alpha),
                            clrSrcKey = colorRef,
                            rcSrc = new MFRect(0, 0, source.Width, source.Height),
                            nrcDest = new MFVideoNormalizedRect(0f, 0f, 1f, 1f),
                            fAlpha = 1f,
                            dwFilterMode = 0
                        }
                    };

                    int hr = _mixer.SetAlphaBitmap(ref alpha);
                    _hasActiveBitmap = hr >= 0;
                    if (hr < 0 && _setFailureLogCount < 6)
                    {
                        _setFailureLogCount++;
                        Dbg.Warn($"[VIDEO] MPCVR mixer SetAlphaBitmap failed hr=0x{hr:X8}, size={source.Width}x{source.Height}.");
                    }
                    return hr >= 0;
                }
                catch (Exception ex)
                {
                    if (_setFailureLogCount < 6)
                    {
                        _setFailureLogCount++;
                        Dbg.Warn("[VIDEO] MPCVR mixer SetBitmap EX: " + ex.Message);
                    }
                    return false;
                }
                finally
                {
                    if (nextBitmap != nint.Zero)
                        DeleteObject(nextBitmap);
                }
            }

            public void Clear()
            {
                if (_disposed || !_hasActiveBitmap)
                    return;

                try { _mixer.ClearAlphaBitmap(); } catch { }
                _hasActiveBitmap = false;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                Clear();
                _disposed = true;

                try
                {
                    if (_hdc != nint.Zero && _oldBitmap != nint.Zero)
                        SelectObject(_hdc, _oldBitmap);
                }
                catch { }

                try { if (_bitmap != nint.Zero) DeleteObject(_bitmap); } catch { }
                try { if (_hdc != nint.Zero) DeleteDC(_hdc); } catch { }

                _bitmap = nint.Zero;
                _oldBitmap = nint.Zero;
                _hdc = nint.Zero;
            }

            private void EnsureDc()
            {
                if (_hdc != nint.Zero)
                    return;

                _hdc = CreateCompatibleDC(nint.Zero);
                if (_hdc == nint.Zero)
                    throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            private static Bitmap PreparePremultipliedBitmap(Bitmap source, Color transparentKey)
            {
                using var keyed = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(keyed))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(source, 0, 0);
                }

                keyed.MakeTransparent(transparentKey);

                var prepared = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(prepared))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.Clear(Color.Transparent);
                    g.DrawImageUnscaled(keyed, 0, 0);
                }

                return prepared;
            }

            private static nint CreateDibSectionFromBitmap(Bitmap bitmap)
            {
                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = bitmap.Width,
                        biHeight = -bitmap.Height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB,
                        biSizeImage = (uint)(bitmap.Width * bitmap.Height * 4)
                    }
                };

                nint bits;
                nint hBitmap = CreateDIBSection(nint.Zero, ref bmi, DIB_RGB_COLORS, out bits, nint.Zero, 0);
                if (hBitmap == nint.Zero || bits == nint.Zero)
                    throw new InvalidOperationException("CreateDIBSection failed.");

                var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    int srcStride = data.Stride;
                    int dstStride = bitmap.Width * 4;
                    int bytes = dstStride * bitmap.Height;
                    if (srcStride == dstStride)
                    {
                        CopyMemory(bits, data.Scan0, (nuint)bytes);
                    }
                    else
                    {
                        for (int y = 0; y < bitmap.Height; y++)
                        {
                            nint src = data.Scan0 + y * srcStride;
                            nint dst = bits + y * dstStride;
                            CopyMemory(dst, src, (nuint)dstStride);
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                return hBitmap;
            }

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern nint CreateCompatibleDC(nint hdc);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern bool DeleteDC(nint hdc);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern nint SelectObject(nint hdc, nint hgdiobj);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern bool DeleteObject(nint hObject);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

            [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
            private static extern void CopyMemory(nint dest, nint src, nuint count);

            private const uint BI_RGB = 0;
            private const uint DIB_RGB_COLORS = 0;

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFOHEADER
            {
                public uint biSize;
                public int biWidth;
                public int biHeight;
                public ushort biPlanes;
                public ushort biBitCount;
                public uint biCompression;
                public uint biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public uint biClrUsed;
                public uint biClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFO
            {
                public BITMAPINFOHEADER bmiHeader;
                public uint bmiColors;
            }
        }

        private sealed class MpcvrRegistryOverride : IDisposable
        {
            private const string KeyPath = @"Software\MPC-BE Filters\MPC Video Renderer";
            private readonly Dictionary<string, ValueSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
            private readonly bool _keyExisted;
            private bool _disposed;

            private MpcvrRegistryOverride(bool keyExisted)
            {
                _keyExisted = keyExisted;
            }

            public static MpcvrRegistryOverride ApplyForHostedPlayback(bool enableRtxVideoHdr, int superResolutionMode)
            {
                superResolutionMode = Math.Clamp(superResolutionMode, 0, 4);
                bool enableSuperResolution = superResolutionMode > 0;
                bool needsVideoProcessor = enableRtxVideoHdr || enableSuperResolution;
                bool keyExisted = false;
                try
                {
                    using var existing = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
                    keyExisted = existing != null;
                }
                catch { }

                var scope = new MpcvrRegistryOverride(keyExisted);

                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
                    if (key != null)
                    {
                        // RTX Video HDR is implemented by MPCVR's D3D11 video processor.
                        // Keep the conservative hosted path for normal playback; when the
                        // Cinecore RTX profile is selected, enable only its real prerequisites.
                        scope.SetDword(key, "ExclusiveFullscreen", 0);
                        scope.SetDword(key, "HdrToggleDisplay", 0);
                        // Leave the user's working presentation and pixel-format defaults
                        // intact. Forcing D3D11 off and every VP format off made ordinary
                        // playback depend on the selected hardware/driver combination.
                        if (needsVideoProcessor)
                        {
                            scope.SetDword(key, "UseD3D11", 1);
                            scope.SetDword(key, "VPEnableNV12", 1);
                            scope.SetDword(key, "VPEnableP01x", 1);
                            scope.SetDword(key, "VPEnableYUY2", 1);
                            scope.SetDword(key, "VPEnableOther", 1);
                        }
                        if (enableRtxVideoHdr)
                        {
                            scope.SetDword(key, "HdrPassthrough", 1);
                            scope.SetDword(key, "TextureFormat", 0); // Auto; 8-bit disables RTX HDR.
                            scope.SetDword(key, "VPRTXVideoHDR", 1);
                        }
                        if (enableSuperResolution)
                        {
                            scope.SetDword(key, "VPScaling", 1);
                            // MPCVR enum: 0 off, 1 SD, 2 720p, 3 1080p, 4 1440p.
                            scope.SetDword(key, "VPSuperResolution", superResolutionMode);
                        }

                        Dbg.Log(needsVideoProcessor
                            ? $"[VIDEO] MPCVR hosted profile: RTX HDR={(enableRtxVideoHdr ? "on" : "off")}, Super Resolution={superResolutionMode}, D3D11=on."
                            : "[VIDEO] MPCVR hosted profile: RTX Video HDR=off, Super Resolution=off, exclusive=off.",
                            Dbg.LogLevel.Info);
                    }
                    else
                    {
                        Dbg.Warn("[VIDEO] MPCVR hosted registry overrides skipped: CreateSubKey returned null.");
                    }
                }
                catch (Exception ex)
                {
                    Dbg.Warn("[VIDEO] MPCVR hosted registry overrides failed: " + ex.Message);
                }

                return scope;
            }

            private void SetDword(RegistryKey key, string name, int value)
            {
                if (!_snapshots.ContainsKey(name))
                    _snapshots[name] = ValueSnapshot.Capture(key, name);

                key.SetValue(name, value, RegistryValueKind.DWord);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;

                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                    if (key == null)
                        return;

                    foreach (var kvp in _snapshots)
                    {
                        var snap = kvp.Value;
                        try
                        {
                            if (!snap.Exists)
                            {
                                try { key.DeleteValue(kvp.Key, throwOnMissingValue: false); } catch { }
                                continue;
                            }

                            key.SetValue(kvp.Key, snap.Value!, snap.Kind);
                        }
                        catch { }
                    }
                }
                catch { }

                try
                {
                    if (!_keyExisted)
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
                        if (key != null && key.GetValueNames().Length == 0 && key.GetSubKeyNames().Length == 0)
                            Registry.CurrentUser.DeleteSubKey(KeyPath, throwOnMissingSubKey: false);
                    }
                }
                catch { }

                Dbg.Log("[VIDEO] MPCVR hosted registry defaults restored.", Dbg.LogLevel.Verbose);
            }

            private readonly struct ValueSnapshot
            {
                public readonly bool Exists;
                public readonly object? Value;
                public readonly RegistryValueKind Kind;

                private ValueSnapshot(bool exists, object? value, RegistryValueKind kind)
                {
                    Exists = exists;
                    Value = value;
                    Kind = kind;
                }

                public static ValueSnapshot Capture(RegistryKey key, string name)
                {
                    object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null)
                        return new ValueSnapshot(false, null, RegistryValueKind.Unknown);

                    RegistryValueKind kind;
                    try { kind = key.GetValueKind(name); }
                    catch { kind = RegistryValueKind.Unknown; }

                    return new ValueSnapshot(true, value, kind);
                }
            }
        }

    }
}
