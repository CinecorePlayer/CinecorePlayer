using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// StorageKind di Windows sul Mac: archivi lenti da leggere a salti, cioe' dischi di rete (smb, nfs, afp, webdav) e
/// montaggi FUSE (rclone con macFUSE o FUSE-T, sshfs). Si guarda il montaggio che contiene il file con getfsstat
/// (MNT_NOWAIT: nessuna domanda al volume, un disco di rete fermo non blocca) e si tiene in cache per 30 secondi.
/// </summary>
internal static class MacStorage
{
    [DllImport("libc", SetLastError = true)] private static extern int getfsstat(IntPtr buf, int bufsize, int mode);
    // struct statfs a 64 bit: 2168 byte; f_flags a 64, f_fstypename a 72 (16), f_mntonname a 88, f_mntfromname a 1112.
    private const int StatfsSize = 2168, MntNowait = 2, MntLocal = 0x1000;
    private static readonly object Gate = new();
    private static List<(string Root, bool Slow)> _mounts = new();
    private static DateTime _readAt;

    public static bool IsSlowMount(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("://", StringComparison.Ordinal)) return true;
        List<(string Root, bool Slow)> mounts;
        lock (Gate)
        {
            if (DateTime.UtcNow - _readAt > TimeSpan.FromSeconds(30)) { _mounts = ReadMounts(); _readAt = DateTime.UtcNow; }
            mounts = _mounts;
        }
        string best = ""; bool slow = false;
        foreach (var (root, isSlow) in mounts)
        {
            string prefix = root == "/" ? "/" : root.TrimEnd('/') + "/";
            if ((path.StartsWith(prefix, StringComparison.Ordinal) || path == root) && root.Length > best.Length) { best = root; slow = isSlow; }
        }
        return slow;
    }

    private static List<(string, bool)> ReadMounts()
    {
        var list = new List<(string, bool)>();
        try
        {
            int count = getfsstat(IntPtr.Zero, 0, MntNowait);
            if (count <= 0) return list;
            int size = (count + 4) * StatfsSize;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                count = getfsstat(buffer, size, MntNowait);
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = buffer + i * StatfsSize;
                    uint flags = (uint)Marshal.ReadInt32(entry + 64);
                    string format = (Marshal.PtrToStringUTF8(entry + 72) ?? "").ToLowerInvariant();
                    string root = Marshal.PtrToStringUTF8(entry + 88) ?? "";
                    string from = (Marshal.PtrToStringUTF8(entry + 1112) ?? "").ToLowerInvariant();
                    // Non locale = rete (anche FUSE-T, che si monta come nfs da localhost); FUSE e rclone anche se locali.
                    bool slow = (flags & MntLocal) == 0 && format is not ("devfs" or "autofs") ||
                                format.Contains("fuse") || format is "smbfs" or "nfs" or "afpfs" or "webdav" or "sshfs" ||
                                from.Contains("rclone") || from.Contains("sshfs");
                    if (root.Length > 0) list.Add((root, slow));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { }
        return list;
    }

    /// <summary>Prove: i montaggi con il loro giudizio.</summary>
    public static string Describe()
    {
        lock (Gate) { _mounts = ReadMounts(); _readAt = DateTime.UtcNow; return string.Join("; ", _mounts.ConvertAll(m => m.Root + (m.Slow ? " (lento)" : ""))); }
    }
}
