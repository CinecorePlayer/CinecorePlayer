using CinecorePlayer2025.Utilities;
using DirectShowLib;
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.Engines
{
    // 3D -> 2D con madVR. madVR non ritaglia la sorgente via IBasicVideo (accetta la chiamata
    // ma la ignora), quindi la sua finestra viene messa dentro una finestra "ritaglio" grande
    // quanto l'area di un occhio: la finestra di madVR e' larga il doppio (SBS) o alta il
    // doppio (TAB), e la meta' con l'altro occhio resta fuori ed e' tagliata da Windows.
    // setArOverride fissa le proporzioni del fotogramma intero (necessario per Half-SBS/TAB).
    // Scaling, HDR e tutti i miglioramenti restano quelli di madVR.
    public sealed partial class DirectShowUnifiedEngine
    {
        private nint _stereoClipHwnd;
        private double _madVrArOverride;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(int exStyle, string className, string? windowName, int style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);

        private nint EnsureStereoClipWindow(nint ownerHwnd)
        {
            if (_stereoClipHwnd != 0 && IsWindow(_stereoClipHwnd) && GetParent(_stereoClipHwnd) == ownerHwnd)
                return _stereoClipHwnd;
            DestroyStereoClipWindow();
            const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_DISABLED = 0x08000000;
            const int SS_BLACKRECT = 0x4;
            // Disabilitata: i clic passano alla finestra del player come prima.
            _stereoClipHwnd = CreateWindowExW(0, "STATIC", null, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_DISABLED | SS_BLACKRECT,
                0, 0, 1, 1, ownerHwnd, 0, 0, 0);
            if (_stereoClipHwnd == 0)
                Dbg.Warn($"[3D] clip window creation failed err={Marshal.GetLastWin32Error()}");
            return _stereoClipHwnd;
        }

        private void DestroyStereoClipWindow()
        {
            if (_stereoClipHwnd != 0)
            {
                try { if (IsWindow(_stereoClipHwnd)) DestroyWindow(_stereoClipHwnd); } catch { }
                _stereoClipHwnd = 0;
            }
        }

        private void SetMadVrArOverride(double aspect)
        {
            if (Math.Abs(_madVrArOverride - aspect) < 0.0001) return;
            var command = TryGetMadVrCommandInterface();
            if (command == null) return;
            try
            {
                int hr = command.SendCommandDouble("setArOverride", aspect);
                if (hr >= 0) _madVrArOverride = aspect;
                else Dbg.Warn($"[3D] madVR setArOverride({aspect:0.###}) hr={HrText(hr)}");
            }
            catch (Exception ex) { Dbg.Warn("[3D] madVR setArOverride EX: " + ex.Message); }
        }

        /// <summary>
        /// Posiziona madVR per il 3D. Restituisce true se il posizionamento e' stato gestito
        /// qui (3D attivo); false per il percorso normale (dopo aver annullato il 3D).
        /// </summary>
        private bool TryPlaceMadVrStereo(nint ownerHwnd, int w, int h)
        {
            // Stessa tecnica per madVR e MPC Video Renderer: entrambi ignorano il ritaglio
            // della sorgente via IBasicVideo.
            if (_choice is not (VideoRendererChoice.MADVR or VideoRendererChoice.MPCVR) || _videoWindow == null)
                return false;

            if (_stereo != Stereo3DMode.None && _stereoKeepBothEyes)
            {
                // Entrambi gli occhi (schermo esteso): niente ritaglio, solo le proporzioni.
                if (_stereoClipHwnd != 0)
                {
                    try { _videoWindow.put_Owner(ownerHwnd); } catch { }
                    DestroyStereoClipWindow();
                    _configuredMadVrOwner = 0;
                }
                if (_choice == VideoRendererChoice.MADVR && TryGetVideoSizeForLayout(out int keepW, out int keepH))
                    SetMadVrArOverride(StereoDisplayAspect(keepW, keepH));
                return false;
            }

            if (_stereo == Stereo3DMode.None)
            {
                if (_stereoClipHwnd == 0 && _madVrArOverride == 0)
                    return false;
                try { _videoWindow.put_Owner(ownerHwnd); } catch { }
                if (_choice == VideoRendererChoice.MADVR) SetMadVrArOverride(0);
                DestroyStereoClipWindow();
                _configuredMadVrOwner = 0; // forza la riconfigurazione normale sul proprietario vero
                Dbg.Log("[3D] renderer back to the native frame.", Dbg.LogLevel.Info);
                return false;
            }

            nint clip = EnsureStereoClipWindow(ownerHwnd);
            if (clip == 0)
                return false;

            if (!TryGetVideoSizeForLayout(out int videoWidth, out int videoHeight) || videoWidth <= 0 || videoHeight <= 0)
            {
                videoWidth = 1920;
                videoHeight = 1080;
            }
            Rectangle eye = CalculateWindowedVideoDestination(w, h, videoWidth, videoHeight);
            const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
            SetWindowPos(clip, 0, eye.Left, eye.Top, eye.Width, eye.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

            if (_configuredMadVrOwner != clip || !ReferenceEquals(_configuredMadVrWindow, _videoWindow))
            {
                CheckWindowControlResult(_videoWindow.put_Owner(clip), "put_Owner(3D clip)");
                CheckWindowControlResult(_videoWindow.put_MessageDrain(ownerHwnd), "put_MessageDrain");
                const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
                CheckWindowControlResult(_videoWindow.put_WindowStyle((WindowStyle)(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN)), "put_WindowStyle");
                try { _videoWindow.put_BorderColor(0x000000); } catch { }
                try { _videoWindow.put_FullScreenMode(OABool.False); } catch { }
                try { _videoWindow.put_AutoShow(OABool.False); } catch { }
                _configuredMadVrWindow = _videoWindow;
                _configuredMadVrOwner = clip;
                _lastOwnerHwnd = ownerHwnd;
            }

            double eyeAspect = StereoEyeAspect(_stereo, videoWidth, videoHeight, SourceSampleAspect);
            int frameW = _stereo == Stereo3DMode.SBS ? eye.Width * 2 : eye.Width;
            int frameH = _stereo == Stereo3DMode.TAB ? eye.Height * 2 : eye.Height;
            if (_choice == VideoRendererChoice.MADVR)
                SetMadVrArOverride(_stereo == Stereo3DMode.SBS ? eyeAspect * 2 : eyeAspect / 2);
            CheckWindowControlResult(_videoWindow.SetWindowPosition(0, 0, Math.Max(2, frameW), Math.Max(2, frameH)), "SetWindowPosition(3D)");
            if (_choice == VideoRendererChoice.MPCVR && TryGetBasicVideoController() is { } basicVideo)
            {
                // MPCVR rispetta la destinazione alla lettera: il fotogramma intero riempie
                // la finestra doppia, cosi' l'occhio sinistro coincide con l'area visibile.
                try { basicVideo.SetDefaultSourcePosition(); } catch { }
                try { basicVideo.SetDestinationPosition(0, 0, Math.Max(2, frameW), Math.Max(2, frameH)); } catch { }
                const int WM_SIZE = 0x0005;
                try { _videoWindow.NotifyOwnerMessage(clip, WM_SIZE, nint.Zero, PackSizeLParam(Math.Max(2, frameW), Math.Max(2, frameH))); } catch { }
                try { RequestMpcvrRedraw(); } catch { }
            }
            CheckWindowControlResult(_videoWindow.put_Visible(OABool.True), "put_Visible");
            Dbg.Log($"[3D] {_choice} {_stereo} -> 2D: frame {videoWidth}x{videoHeight} sar={SourceSampleAspect:0.###}, eye {eye}, madVR window {frameW}x{frameH}, AR override {(_stereo == Stereo3DMode.SBS ? eyeAspect * 2 : eyeAspect / 2):0.###}", Dbg.LogLevel.Info);
            return true;
        }
    }
}
