#nullable enable
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Monta un'immagine ISO come unita' di sola lettura (la stessa operazione di "Monta" in Esplora
    /// file, senza permessi di amministratore). Cosi' un Blu-ray in ISO diventa una cartella che
    /// tutti i renderer sanno aprire. L'unita' sparisce quando l'oggetto viene rilasciato.
    /// </summary>
    internal sealed class IsoMount : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct StorageType { public uint DeviceId; public Guid VendorId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceNumber { public uint DeviceType; public uint Number; public uint Partition; }

        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        private static extern uint OpenVirtualDisk(ref StorageType type, string path, uint accessMask, uint flags, IntPtr parameters, out SafeFileHandle handle);

        [DllImport("virtdisk.dll")]
        private static extern uint AttachVirtualDisk(SafeFileHandle handle, IntPtr securityDescriptor, uint flags, uint providerFlags, IntPtr parameters, IntPtr overlapped);

        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint sizeInBytes, StringBuilder path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, out DeviceNumber output, uint outputSize, out uint returned, IntPtr overlapped);

        private readonly SafeFileHandle _disk;
        public string IsoPath { get; }
        public string Root { get; }

        private IsoMount(SafeFileHandle disk, string isoPath, string root)
        {
            _disk = disk;
            IsoPath = isoPath;
            Root = root;
            DiscMedia.SetMount(isoPath, root);
        }

        /// <summary>null se Windows non riesce a montarla (per esempio da certi dischi di rete): si prosegue senza.</summary>
        public static IsoMount? TryMount(string isoPath)
        {
            SafeFileHandle? disk = null;
            try
            {
                isoPath = Path.GetFullPath(isoPath);
                var type = new StorageType { DeviceId = 1 /* ISO */, VendorId = new Guid("EC984AEC-A0F9-47e9-901F-71415A66345B") };
                uint error = OpenVirtualDisk(ref type, isoPath, 0x000D0000 /* lettura */, 0, IntPtr.Zero, out disk);
                if (error != 0) { Dbg.Warn("[DISC] ISO open failed: " + error); return null; }
                error = AttachVirtualDisk(disk, IntPtr.Zero, 0x1 /* sola lettura */, 0, IntPtr.Zero, IntPtr.Zero);
                if (error != 0) { Dbg.Warn("[DISC] ISO attach failed: " + error); disk.Dispose(); return null; }

                uint size = 520;
                var physical = new StringBuilder(260);
                error = GetVirtualDiskPhysicalPath(disk, ref size, physical);
                // "\\.\CDROM3" -> la lettera arriva qualche istante dopo.
                string device = physical.ToString();
                int digits = device.Length;
                while (digits > 0 && char.IsDigit(device[digits - 1])) digits--;
                if (error != 0 || digits == device.Length || !uint.TryParse(device[digits..], out uint number))
                {
                    Dbg.Warn("[DISC] ISO device not found: " + error + " '" + device + "'");
                    disk.Dispose();
                    return null;
                }
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    if (FindDrive(number) is string root)
                    {
                        Dbg.Log($"[DISC] ISO mounted at {root}", Dbg.LogLevel.Info);
                        return new IsoMount(disk, isoPath, root);
                    }
                    Thread.Sleep(100);
                }
                Dbg.Warn("[DISC] ISO mounted but no drive letter was assigned");
                disk.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                Dbg.Warn("[DISC] ISO mount: " + ex.Message);
                disk?.Dispose();
                return null;
            }
        }

        private static string? FindDrive(uint cdromNumber)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.CDRom) continue;
                using var volume = CreateFileW(@"\\.\" + drive.Name.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (volume.IsInvalid) continue;
                if (DeviceIoControl(volume, 0x002D1080 /* IOCTL_STORAGE_GET_DEVICE_NUMBER */, IntPtr.Zero, 0, out var info, (uint)Marshal.SizeOf<DeviceNumber>(), out _, IntPtr.Zero)
                    && info.DeviceType == 2 /* CD-ROM */ && info.Number == cdromNumber)
                    return drive.RootDirectory.FullName;
            }
            return null;
        }

        public void Dispose()
        {
            DiscMedia.SetMount(IsoPath, null);
            // Chiudere il riferimento smonta l'unita'.
            _disk.Dispose();
            Dbg.Log("[DISC] ISO unmounted: " + Root, Dbg.LogLevel.Info);
        }
    }
}
