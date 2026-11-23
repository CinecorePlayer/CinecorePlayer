using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CinecoreMac;

internal sealed partial class LibraryView
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VolumeChanged(IntPtr path, int mounted);

    [DllImport("libCinecoreGlass.dylib")]
    private static extern IntPtr CinecoreObserveVolumes(VolumeChanged changed);
    [DllImport("libCinecoreGlass.dylib")]
    private static extern void CinecoreStopObservingVolumes(IntPtr observer);

    private IntPtr _volumeObserver;
    private VolumeChanged? _volumeChanged;
    private readonly CancellationTokenSource _volumeLifetime = new();

    internal void ObserveVolumes()
    {
        _volumeChanged = (path, mounted) =>
        {
            if (Marshal.PtrToStringUTF8(path) is { Length: > 0 } volume)
                Dispatcher.UIThread.Post(() => _ = RefreshVolumeAsync(volume, mounted != 0));
        };
        try { _volumeObserver = CinecoreObserveVolumes(_volumeChanged); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    internal void StopObservingVolumes()
    {
        _volumeLifetime.Cancel();
        if (_volumeObserver != IntPtr.Zero)
        {
            CinecoreStopObservingVolumes(_volumeObserver);
            _volumeObserver = IntPtr.Zero;
        }
        _volumeChanged = null;
    }

    private async Task RefreshVolumeAsync(string volume, bool mounted)
    {
        var ct = _volumeLifetime.Token;
        if (ct.IsCancellationRequested) return;
        string[] categories = LibraryStore.FolderCategories
            .Where(c => LibraryStore.GetRoots(c).Any(root => LibraryStore.OnVolume(root, volume)))
            .OrderByDescending(c => c == _category).ToArray();
        if (categories.Length == 0) return;
        try
        {
            foreach (string category in categories)
            {
                if (mounted)
                {
                    await ScanCategoryAsync(category, volume, ct);
                    continue;
                }
                if (ct.IsCancellationRequested) return;
                if (ActiveServer == null && (category == _category || category is "Movies" or "TV Series")) Reload();
                if (_foldersOpen && category == _foldersCategory) RefreshFolders();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
