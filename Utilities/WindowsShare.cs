#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Pannello "Condividi" di Windows per un file, da un programma desktop: il gestore della
    /// condivisione si chiede per la finestra (non esiste quello "dell'app" come nelle app dello Store)
    /// e il file si consegna quando il pannello lo domanda.
    /// </summary>
    internal static class WindowsShare
    {
        [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow(IntPtr appWindow, in Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }

        private static readonly Guid DataTransferManagerId = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
        // Una registrazione per finestra: il gestore resta vivo e sa quale file consegnare.
        private static DataTransferManager? _manager;
        private static IntPtr _window;
        private static string? _path;

        public static void ShowForFile(IntPtr window, string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            if (_manager == null || _window != window)
            {
                _manager = DataTransferManager.FromAbi(interop.GetForWindow(window, DataTransferManagerId));
                _window = window;
                _manager.DataRequested += async (_, e) =>
                {
                    string? file = _path;
                    if (file == null) return;
                    var deferral = e.Request.GetDeferral();
                    try
                    {
                        e.Request.Data.Properties.Title = Path.GetFileName(file);
                        e.Request.Data.SetStorageItems(new IStorageItem[] { await StorageFile.GetFileFromPathAsync(file) });
                    }
                    catch (Exception ex) { e.Request.FailWithDisplayText(ex.Message); }
                    finally { deferral.Complete(); }
                };
            }
            _path = path;
            interop.ShowShareUIForWindow(window);
        }
    }
}
