#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Azioni sulla foto aperta: modifica, elimina, condividi, stampa, apri con un'altra app.
    public sealed partial class PlayerForm
    {
        private string? CurrentLocalPhoto()
        {
            string? path = _currentPath;
            if (!IsPhotoMode || string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal) || !File.Exists(path))
            {
                ShowRemoteOsd(null, null, 2600, Tx("Disponibile solo per le foto salvate sul computer", "Only available for photos stored on this computer"));
                return null;
            }
            return path;
        }

        private void EditCurrentPhoto()
        {
            if (CurrentLocalPhoto() is not string path) return;
            StopPhotoSlideshow();
            string? saved = null;
            try
            {
                // A tutta finestra, sopra il visualizzatore: stessa area, stessa scala dello schermo.
                using var editor = new PhotoEditForm(path, UiEnglish, RectangleToScreen(ClientRectangle)) { TopMost = TopMost };
                editor.ShowDialog(this);
                saved = editor.SavedPath;
            }
            catch (Exception ex) { Dbg.Warn("[PHOTO] edit: " + ex.Message); ShowRemoteOsd(null, null, 3200, ex.Message); }
            try { Activate(); } catch { }
            if (saved == null) return;
            // La copia modificata entra nella serie subito dopo l'originale e viene mostrata.
            if (string.Equals(Path.GetDirectoryName(saved), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase) && _imageIndex >= 0 && _imageIndex < _imageFiles.Count)
                _imageFiles.Insert(_imageIndex + 1, saved);
            ShowRemoteOsd(null, null, 3200, Tx("Copia salvata: ", "Copy saved: ") + Path.GetFileName(saved));
            if (_imageFiles.Contains(saved)) ShowNextImage();
        }

        private void DeleteCurrentPhoto()
        {
            if (CurrentLocalPhoto() is not string path) return;
            StopPhotoSlideshow();
            var answer = MessageBox.Show(this,
                Tx("Spostare questa foto nel Cestino?\n\n", "Move this photo to the Recycle Bin?\n\n") + Path.GetFileName(path),
                "Cinecore Player", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;

            int index = _imageFiles.FindIndex(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
            bool last = _imageFiles.Count <= 1;
            // Prima si passa a un'altra foto (o si chiude): quella aperta e' in uso.
            if (last) CloseCurrentToLibrary();
            else ShowNextImage();
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                if (index >= 0 && index < _imageFiles.Count && string.Equals(_imageFiles[index], path, StringComparison.OrdinalIgnoreCase))
                {
                    _imageFiles.RemoveAt(index);
                    if (_imageIndex > index) _imageIndex--;
                }
                ShowRemoteOsd(null, null, 2400, Tx("Foto spostata nel Cestino", "Photo moved to the Recycle Bin"));
                try { _cinematicLibraryPage?.Invalidate(); } catch { }
            }
            catch (Exception ex)
            {
                Dbg.Warn("[PHOTO] delete: " + ex.Message);
                ShowRemoteOsd(null, null, 4200, Tx("Non è stato possibile eliminarla: ", "It could not be deleted: ") + ex.Message);
            }
        }

        // Le azioni stanno direttamente nella barra delle foto, come icone.
        private void RunPhotoBarAction(string key)
        {
            if (CurrentLocalPhoto() is not string path) return;
            RunPhotoAction(() =>
            {
                switch (key)
                {
                    case "share": SharePhoto(path); break;
                    case "openwith": Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path) { UseShellExecute = false })?.Dispose(); break;
                    case "print": Process.Start(new ProcessStartInfo(path) { Verb = "print", UseShellExecute = true })?.Dispose(); break;
                    case "folder": Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false })?.Dispose(); break;
                    case "copy":
                        var data = new DataObject();
                        data.SetFileDropList(new StringCollection { path });
                        using (var image = Image.FromFile(path)) data.SetImage(new Bitmap(image));
                        Clipboard.SetDataObject(data, copy: true);
                        ShowRemoteOsd(null, null, 2200, Tx("Foto copiata negli appunti", "Photo copied to the clipboard"));
                        break;
                }
            });
        }

        /// <summary>
        /// Il pannello "Condividi" di Windows, lo stesso che si apre da Esplora file: contatti,
        /// dispositivi vicini e app che accettano un'immagine.
        /// </summary>
        private void SharePhoto(string path) => WindowsShare.ShowForFile(Handle, path);

        private void RunPhotoAction(Action action)
        {
            StopPhotoSlideshow();
            try { action(); }
            catch (Exception ex)
            {
                Dbg.Warn("[PHOTO] action: " + ex.Message);
                ShowRemoteOsd(null, null, 3600, Tx("Non riuscito: ", "Failed: ") + ex.Message);
            }
        }
    }
}
