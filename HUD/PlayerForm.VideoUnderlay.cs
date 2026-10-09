#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private bool _underlayWasCovered;
        // Passate di pulizia ancora da fare e momento della prossima.
        private int _underlayPasses;
        private long _underlayNextAt;

        /// <summary>
        /// Il segnaposto pre-film e la schermata di caricamento vengono disegnati nella stessa superficie
        /// della finestra su cui poi il renderer presenta il video. Il renderer non ridipinge quella
        /// superficie: l'ultima immagine (il segnaposto) vi restava sotto, e traspariva quando una finestra
        /// semitrasparente (HUD, vignettatura, schede) veniva composta sopra il video. Quando segnaposto e
        /// caricamento spariscono, la superficie sotto il video viene riempita di nero.
        /// </summary>
        private void TrackVideoUnderlay()
        {
            bool covered = false;
            try { covered = _pausePlaceholder?.Visible == true || _videoLoading?.Visible == true || _preOpenPlaceholderGateActive; } catch { }
            // Una passata sola non bastava: se in quell'istante il video non era ancora a schermo non si puliva
            // niente, e un ridisegno tardivo poteva rimettere l'immagine. Con la schermata di caricamento nera non si
            // notava; ora che mostra la locandina si'. Si riprova finche' riesce, poi ancora due volte.
            if (_underlayWasCovered && !covered) { _underlayPasses = 3; _underlayNextAt = 0; }
            if (covered) _underlayPasses = 0;
            _underlayWasCovered = covered;
            if (_underlayPasses > 0 && Environment.TickCount64 >= _underlayNextAt)
            {
                if (_engine == null || !_currentMediaHasVideo || IsPhotoMode) _underlayPasses = 0;
                else if (BlackenVideoUnderlay()) { _underlayPasses--; _underlayNextAt = Environment.TickCount64 + (_underlayPasses == 2 ? 450 : 1400); }
            }
        }

        private bool BlackenVideoUnderlay()
        {
            try
            {
                if (_closingForExit || _engine == null || !_currentMediaHasVideo || IsPhotoMode || IsAnyLibraryVisible()) return false;
                if (_videoHost == null || _videoHost.IsDisposed || !_videoHost.IsHandleCreated || !_videoHost.Visible) return false;
                // DC senza il ritaglio delle finestre figlie: si dipinge anche sotto la finestra del renderer.
                IntPtr dc = GetDCEx(_videoHost.Handle, IntPtr.Zero, DCX_CACHE);
                if (dc == IntPtr.Zero) return false;
                try
                {
                    var area = new UnderlayRect { Right = _videoHost.ClientSize.Width, Bottom = _videoHost.ClientSize.Height };
                    FillRect(dc, ref area, GetStockObject(BLACK_BRUSH));
                }
                finally { ReleaseUnderlayDC(_videoHost.Handle, dc); }
                // Il renderer ripresenta subito il fotogramma: per lui e' un normale ridisegno.
                foreach (Control child in _videoHost.Controls) child.Invalidate(true);
                return true;
            }
            catch (Exception ex) { Utilities.Dbg.Warn("[UNDERLAY] " + ex.Message); return false; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct UnderlayRect { public int Left, Top, Right, Bottom; }
        private const uint DCX_CACHE = 0x2;
        private const int BLACK_BRUSH = 4;
        [DllImport("user32.dll")] private static extern IntPtr GetDCEx(IntPtr hwnd, IntPtr region, uint flags);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref UnderlayRect rect, IntPtr brush);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
        [DllImport("user32.dll", EntryPoint = "ReleaseDC")] private static extern int ReleaseUnderlayDC(IntPtr hwnd, IntPtr dc);
    }
}
