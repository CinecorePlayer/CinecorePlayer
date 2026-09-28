#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {

        private void BuildMenu()
        {
            _menu = new TopMostContextMenuStrip();
            _menu.Font = new Font("Segoe UI", 10.2f);
            _menu.Padding = new Padding(6, 7, 6, 7);
            _menu.MinimumSize = new Size(210, 0);
            ApplyDarkMenuTheme(_menu);

            // comprime eventuali separatori doppi in base alla visibilità attuale
            void CollapseSeparators()
            {
                if (_menu == null) return;

                bool lastVisibleWasSep = false;

                foreach (ToolStripItem item in _menu.Items)
                {
                    if (item is ToolStripSeparator sep)
                    {
                        // se il precedente VISTO era già un separatore, questo lo nascondo
                        if (lastVisibleWasSep)
                        {
                            sep.Visible = false;
                        }
                        else
                        {
                            sep.Visible = true;
                            lastVisibleWasSep = true;
                        }
                    }
                    else
                    {
                        // reset solo se l’item è visibile
                        if (item.Visible)
                            lastVisibleWasSep = false;
                    }
                }
            }

            // --- FILE ---
            var mOpen = new ToolStripMenuItem(Tx("Apri file…", "Open file..."), null, (_, __) => OpenFileWithDialog());
            var mOpenLib = new ToolStripMenuItem(Tx("Libreria", "Library"), null, (_, __) => ShowDefaultLibrary());
            var mNetflixMode = new ToolStripMenuItem("Spotlight", null, (_, __) =>
            {
                if (_netflixModePage?.Visible == true)
                    HideNetflixMode(showHome: true);
                else
                    ShowNetflixMode();
            });

            // --- RIPRODUZIONE ---
            var mPlay = new ToolStripMenuItem(Tx("Play / pausa", "Play / pause"), null, (_, __) => TogglePlayPause());
            var mStop = new ToolStripMenuItem(Tx("Chiudi file", "Close file"), null, (_, __) => CloseCurrentToLibrary());
            var mQueue = new ToolStripMenuItem(Tx("Coda", "Queue"));
            _mQueueMenuItem = mQueue;
            mQueue.DropDownOpening += (_, __) => PopulatePlaybackQueueMenu(mQueue);
            var mLoopTrack = new ToolStripMenuItem(Tx("Loop brano", "Loop track"), null, (_, __) =>
            {
                SetSingleTrackLoop(_currentPath, !IsSingleTrackLoopEnabledForPath(_currentPath));
            });
            _mLoopTrack = mLoopTrack;
            var mFull = new ToolStripMenuItem(Tx("Schermo intero", "Fullscreen"), null, (_, __) => ToggleFullscreen());

            // --- IMMAGINE / HDR ---
            var mHdr = new ToolStripMenuItem(Tx("Immagine / HDR", "Picture / HDR"));

            var hAuto = new ToolStripMenuItem(Tx("Auto (gestito dal renderer)", "Auto (renderer managed)"), null, (_, __) =>
            {
                SetHdrProfile(HdrUiProfile.Auto, "context-menu");
            });

            var hRtx = new ToolStripMenuItem("RTX Video HDR (SDR → HDR10, MPCVR)", null, (_, __) =>
            {
                SetHdrProfile(HdrUiProfile.RtxVideoHdr, "context-menu");
            });

            var hPass = new ToolStripMenuItem(Tx("Passthrough HDR al display", "HDR passthrough to display"), null, (_, __) =>
            {
                SetHdrProfile(HdrUiProfile.Passthrough, "context-menu");
            });

            var hToneSdr = new ToolStripMenuItem(Tx("Tone-map HDR → SDR (pixel shaders)", "Tone-map HDR → SDR (pixel shaders)"), null, (_, __) =>
            {
                SetHdrProfile(HdrUiProfile.ToneMapSdr, "context-menu");
            });

            var hLutSdr = new ToolStripMenuItem(Tx("HDR → SDR (via 3DLUT) — avanzato…", "HDR → SDR (via 3DLUT) — advanced…"), null, (_, __) =>
            {
                if (!_lutWarned)
                {
                    _lutWarned = true;
                    MessageBox.Show(
                        Tx("Questo profilo richiede una 3DLUT HDR→SDR configurata in madVR (Devices → calibration).", "This profile requires an HDR→SDR 3DLUT configured in madVR (Devices → calibration)."),
                        Tx("3DLUT richiesta", "3DLUT required"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                SetHdrProfile(HdrUiProfile.LutSdr, "context-menu");
            });

            mHdr.DropDownItems.AddRange(new[] { hAuto, hRtx, hPass, hToneSdr, hLutSdr });

            mHdr.DropDownOpening += (_, __) =>
            {
                hAuto.Checked = _hdrProfile == HdrUiProfile.Auto;
                hRtx.Checked = _hdrProfile == HdrUiProfile.RtxVideoHdr;
                hRtx.Enabled = IsRtxVideoHdrAvailable();
                hPass.Checked = _hdrProfile == HdrUiProfile.Passthrough;
                hToneSdr.Checked = _hdrProfile == HdrUiProfile.ToneMapSdr;
                hLutSdr.Checked = _hdrProfile == HdrUiProfile.LutSdr;
            };

            // --- 3D ---
            var m3D = new ToolStripMenuItem("3D");

            var m3Auto = new ToolStripMenuItem(Tx("Auto (metadati + rilevamento)", "Auto (metadata + detection)"), null, (_, __) =>
            {
                Enable3DAuto("context-menu");
            });

            var m3Native = new ToolStripMenuItem(Tx("Nativo (immagine doppia)", "Native (double image)"), null, (_, __) =>
            {
                // niente conversione 3D→2D, immagine così com’è
                Disable3DRestoreRenderer();
            });

            var m3SBS = new ToolStripMenuItem(Tx("SBS → 2D (usa EVR)", "SBS → 2D (use EVR)"), null, (_, __) =>
            {
                Enable3D(Stereo3DMode.SBS);
            });

            var m3TAB = new ToolStripMenuItem(Tx("TAB → 2D (usa EVR)", "TAB → 2D (use EVR)"), null, (_, __) =>
            {
                Enable3D(Stereo3DMode.TAB);
            });

            m3D.DropDownItems.AddRange(new[] { m3Auto, m3Native, m3SBS, m3TAB });

            m3D.DropDownOpening += (_, __) =>
            {
                m3Auto.Checked = _stereoAutoEnabled;
                m3Native.Checked = !_stereoAutoEnabled && _stereo == Stereo3DMode.None;
                m3SBS.Checked = !_stereoAutoEnabled && _stereo == Stereo3DMode.SBS;
                m3TAB.Checked = !_stereoAutoEnabled && _stereo == Stereo3DMode.TAB;
            };

            // --- Upscaling (madVR) ---
            var mUpscale = new ToolStripMenuItem(Tx("Upscaling (oltre nativo)", "Upscaling (above native)"))
            {
                CheckOnClick = true,
                Checked = _enableUpscaling
            };

            mUpscale.Click += (_, __) =>
            {
                SetVideoUpscaling(mUpscale.Checked, "context-menu");
                mUpscale.Checked = _enableUpscaling;
                _hud.ShowOnce(1200);
            };

            // === Risoluzione per flussi web (YouTube) ===
            var mWebRes = new ToolStripMenuItem(Tx("Risoluzione web (YouTube)", "Web resolution (YouTube)"));

            void ApplyYtMax(int maxH, string msg)
            {
                WebMediaResolver.MaxYouTubeHeight = maxH;
                _lblStatus.Text = msg;
                if (IsCurrentYouTube())
                {
                    // defer: lascia chiudere il menu e riduce "impalli" percepiti
                    BeginInvoke(new Action(() => ReopenSame()));
                }
            }

            var qAuto = new ToolStripMenuItem(Tx("Auto (nessun limite)", "Auto (no limit)"), null, (_, __) => ApplyYtMax(0, "YouTube: Auto"));
            var q4320 = new ToolStripMenuItem(Tx("Limita a 4320p (8K)", "Limit to 4320p (8K)"), null, (_, __) => ApplyYtMax(4320, "YouTube: max 4320p"));
            var q2160 = new ToolStripMenuItem(Tx("Limita a 2160p (4K)", "Limit to 2160p (4K)"), null, (_, __) => ApplyYtMax(2160, "YouTube: max 2160p"));
            var q1440 = new ToolStripMenuItem(Tx("Limita a 1440p", "Limit to 1440p"), null, (_, __) => ApplyYtMax(1440, "YouTube: max 1440p"));
            var q1080 = new ToolStripMenuItem(Tx("Limita a 1080p", "Limit to 1080p"), null, (_, __) => ApplyYtMax(1080, "YouTube: max 1080p"));
            var q720 = new ToolStripMenuItem(Tx("Limita a 720p", "Limit to 720p"), null, (_, __) => ApplyYtMax(720, "YouTube: max 720p"));
            var q480 = new ToolStripMenuItem(Tx("Limita a 480p", "Limit to 480p"), null, (_, __) => ApplyYtMax(480, "YouTube: max 480p"));
            var q360 = new ToolStripMenuItem(Tx("Limita a 360p", "Limit to 360p"), null, (_, __) => ApplyYtMax(360, "YouTube: max 360p"));
            var q240 = new ToolStripMenuItem(Tx("Limita a 240p", "Limit to 240p"), null, (_, __) => ApplyYtMax(240, "YouTube: max 240p"));
            var q144 = new ToolStripMenuItem(Tx("Limita a 144p", "Limit to 144p"), null, (_, __) => ApplyYtMax(144, "YouTube: max 144p"));

            mWebRes.DropDownItems.AddRange(new ToolStripItem[]
            {
                qAuto,
                new ToolStripSeparator(),
                q4320,
                q2160,
                q1440,
                q1080,
                q720,
                q480,
                q360,
                q240,
                q144
            });

            mWebRes.DropDownOpening += (_, __) =>
            {
                int max = WebMediaResolver.MaxYouTubeHeight;
                qAuto.Checked = (max <= 0);
                q4320.Checked = (max == 4320);
                q2160.Checked = (max == 2160);
                q1440.Checked = (max == 1440);
                q1080.Checked = (max == 1080);
                q720.Checked = (max == 720);
                q480.Checked = (max == 480);
                q360.Checked = (max == 360);
                q240.Checked = (max == 240);
                q144.Checked = (max == 144);
            };

            // --- AUDIO: lingue / sottotitoli / uscita ---
            _mAudioLang = new ToolStripMenuItem(Tx("Traccia audio", "Audio track"));
            // placeholder per mostrare sempre il triangolino (menu popolato dinamicamente)
            _mAudioLang.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            _mAudioLang.DropDownOpening += (_, __) => PopulateAudioLangMenu();

            _mSubtitles = new ToolStripMenuItem(Tx("Sottotitoli", "Subtitles"));
            // placeholder per mostrare sempre il triangolino (menu popolato dinamicamente)
            _mSubtitles.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            _mSubtitles.DropDownOpening += (_, __) => PopulateSubtitlesMenu();

            _mAudioOut = new ToolStripMenuItem(Tx("Uscita audio", "Audio output"));
            // placeholder per mostrare sempre il triangolino (menu popolato dinamicamente)
            _mAudioOut.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            _mAudioOut.DropDownOpening += (_, __) => PopulateAudioOutputMenu(_mAudioOut);

            // --- Capitoli (submenu vero, con triangolino) ---
            _mChapters = new ToolStripMenuItem(Tx("Capitoli", "Chapters"));
            // placeholder per mostrare sempre il triangolino (menu popolato dinamicamente)
            _mChapters.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            _mChapters.DropDownOpening += (_, __) => PopulateChaptersMenu(_mChapters);

            // --- Info overlay ---
            var mShowInfo = new ToolStripMenuItem(Tx("Mostra / nascondi info", "Show / hide info"), null,
                (_, __) => { _infoOverlay.Visible = !_infoOverlay.Visible; });

            var mPip = new ToolStripMenuItem(Tx("Modalità PiP", "PiP mode"), null, (_, __) => TogglePipMode());
            _mPipMode = mPip;

            // --- EXTRA: cinema mode, WLED, placeholder pre-film + demo pre-film ---
            var mExtras = new ToolStripMenuItem(Tx("Extra", "Extras"));

            var mCinemaMode = new ToolStripMenuItem(Tx("Modalità cinema", "Cinema mode"))
            {
                CheckOnClick = true,
                Checked = _cinemaModeEnabled
            };
            _miCinemaMode = mCinemaMode;
            mCinemaMode.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;
                ApplyCinemaModeFromMenu(mCinemaMode.Checked);
            };

            var mWled = new ToolStripMenuItem("WLED");
            var miWledEnable = new ToolStripMenuItem(Tx("Abilita controllo LED", "Enable LED control"))
            {
                CheckOnClick = true,
                Checked = _wledEnabled
            };
            _miWledEnable = miWledEnable;
            miWledEnable.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;

                bool wasEnabled = _wledEnabled;
                _wledEnabled = miWledEnable.Checked;
                try { SaveExtrasConfig(); } catch { }
                RefreshCinemaModeMenuState();

                if (!_wledEnabled)
                {
                    CancelPendingWledPauseRestore();
                    CancelPendingWledTransition();
                    _ = RestoreWledInitialStateAsync(WLED_FADE_MS);
                }
                else if (!wasEnabled)
                {
                    ApplyAmbientLightingForCurrentState();
                }
            };

            var miWledConfigure = new ToolStripMenuItem(Tx("Configura dispositivo…", "Configure device…"), null,
                (_, __) => BeginInvoke(new Action(() => ConfigureWledFromMenu())));

            mWled.DropDownItems.AddRange(new ToolStripItem[]
            {
                miWledEnable,
                miWledConfigure
            });

            var miPausePlaceholder = new ToolStripMenuItem(Tx("Placeholder pre-film", "Pre-movie placeholder"))
            {
                CheckOnClick = true,
                Checked = _pausePlaceholderEnabled
            };
            _miPausePlaceholderEnable = miPausePlaceholder;
            miPausePlaceholder.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;

                _pausePlaceholderEnabled = miPausePlaceholder.Checked;
                try { SaveExtrasConfig(); } catch { }
                RefreshCinemaModeMenuState();

                // Nuovo comportamento: il placeholder si usa SOLO come gate pre-film.
                // Se lo disattivo mentre è attivo il gate, lo chiudo.
                try
                {
                    if (!_pausePlaceholderEnabled)
                        HidePreOpenPlaceholderGate(clearPending: true);
                }
                catch { }
            };

            var miPausePlaceholderBackdrop = new ToolStripMenuItem(Tx("Usa backdrop automatico TMDb (film/serie TV)", "Use automatic TMDb backdrop (movies/TV)"))
            {
                CheckOnClick = true,
                Checked = _pausePlaceholderUseTmdbBackdrop
            };
            _miPausePlaceholderUseTmdbBackdrop = miPausePlaceholderBackdrop;
            miPausePlaceholderBackdrop.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;
                _pausePlaceholderUseTmdbBackdrop = miPausePlaceholderBackdrop.Checked;
                try { SaveExtrasConfig(); } catch { }
            };

            var miChoosePausePlaceholder = new ToolStripMenuItem(Tx("Scegli placeholder", "Choose placeholder"))
            {
                // placeholder per triangolino
            };
            miChoosePausePlaceholder.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            miChoosePausePlaceholder.DropDownOpening += (_, __) => PopulatePausePlaceholderMenu(miChoosePausePlaceholder);

            var miOpenPauseFolder = new ToolStripMenuItem(Tx("Apri cartella placeholder…", "Open placeholder folder…"), null,
                (_, __) => OpenFolderInExplorer(_pausePlaceholderFolder));

            var mPreRoll = new ToolStripMenuItem(Tx("Demo pre-film", "Pre-movie demo"));
            var miPreRollEnable = new ToolStripMenuItem(Tx("Abilita", "Enable"))
            {
                CheckOnClick = true,
                Checked = _preRollEnabled
            };
            _miPreRollEnable = miPreRollEnable;
            miPreRollEnable.CheckedChanged += (_, __) =>
            {
                if (_syncingCinemaModeUi) return;
                _preRollEnabled = miPreRollEnable.Checked;
                try { SaveExtrasConfig(); } catch { }
                RefreshCinemaModeMenuState();
            };

            var miPreRollChoose = new ToolStripMenuItem(Tx("Scegli demo", "Choose demo"))
            {
                // placeholder per triangolino
            };
            miPreRollChoose.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            miPreRollChoose.DropDownOpening += (_, __) => PopulatePreRollDemoMenu(miPreRollChoose);

            var miOpenDemoFolder = new ToolStripMenuItem(Tx("Apri cartella demo…", "Open demo folder…"), null,
                (_, __) => OpenFolderInExplorer(_preRollDemoFolder));

            mPreRoll.DropDownItems.AddRange(new ToolStripItem[]
            {
                miPreRollEnable,
                miPreRollChoose,
                new ToolStripSeparator(),
                miOpenDemoFolder
            });

            mExtras.DropDownItems.AddRange(new ToolStripItem[]
            {
                mWled,
                new ToolStripSeparator(),
                miPausePlaceholder,
                miPausePlaceholderBackdrop,
                miChoosePausePlaceholder,
                miOpenPauseFolder,
                new ToolStripSeparator(),
                mPreRoll
            });

            // --- Renderer video ---
            var mRenderer = new ToolStripMenuItem(Tx("Motore / renderer video", "Video engine / renderer"));
            void SetRenderer(VRChoice? c)
            {
                if (_stereo != Stereo3DMode.None && c is not (VRChoice.EVR or VRChoice.MPCVR))
                {
                    _hasSavedRendererFor3D = true;
                    _savedRendererFor3D = c;
                    _lblStatus.Text = Tx("3D→2D attivo: EVR obbligatorio. Preferenza renderer memorizzata per dopo.", "3D→2D enabled: EVR is required. Renderer preference saved for later.");
                    _hud.ShowOnce(1400);
                    return;
                }

                _hasRuntimeRendererOverride = false;
                _runtimeRendererChoiceOverride = null;
                _manualRendererChoice = c;
                _lblStatus.Text = Tx("Motore video: ", "Video engine: ") + (c?.ToString() ?? "Auto");

                if (c.HasValue && c.Value != VRChoice.MADVR)
                {
                    _enableUpscaling = false;
                    _videoUpscalingBackend = "off";
                    try { _engine?.SetUpscaling(false); } catch { }
                }

                try { SaveExtrasConfig(); } catch { }
                ReopenSame();
            }

            var mPcmPref = new ToolStripMenuItem(Tx("Preferenza uscita audio", "Audio output preference"));
            var miPcmAuto = new ToolStripMenuItem(Tx("Auto (bitstream se conviene)", "Auto (bitstream when suitable)"), null, (_, __) =>
            {
                _audioOutPref = AudioOutPref.Auto;
                _lblStatus.Text = Tx("Uscita: Auto (bitstream se conviene)", "Output: Auto (bitstream when appropriate)");
                ReopenSame();
            });
            var miPcmForce = new ToolStripMenuItem(Tx("Forza PCM (disabilita bitstream)", "Force PCM (disable bitstream)"), null, (_, __) =>
            {
                _audioOutPref = AudioOutPref.ForcePcm;
                _lblStatus.Text = Tx("Uscita: Forza PCM", "Output: Force PCM");
                ReopenSame();
            });
            mPcmPref.DropDownItems.AddRange(new[] { miPcmAuto, miPcmForce });
            mPcmPref.DropDownOpening += (_, __) =>
            {
                miPcmAuto.Checked = _audioOutPref == AudioOutPref.Auto;
                miPcmForce.Checked = _audioOutPref == AudioOutPref.ForcePcm;
            };

            var miMadvr = new ToolStripMenuItem("madVR", null, (_, __) => SetRenderer(VRChoice.MADVR));
            var miMpcvr = new ToolStripMenuItem("MPCVR", null, (_, __) => SetRenderer(VRChoice.MPCVR));
            var miEvr = new ToolStripMenuItem("EVR", null, (_, __) => SetRenderer(VRChoice.EVR));
            var miMpv = new ToolStripMenuItem("MPV (libmpv)", null, (_, __) => SetRenderer(VRChoice.MPV));
            var miAuto = new ToolStripMenuItem(Tx("Auto (ordine preferito)", "Auto (preferred order)"), null, (_, __) => SetRenderer(null));
            var mMpvRuntime = new ToolStripMenuItem("Runtime MPV / libmpv");
            void SetMpvRuntime(MpvRuntimeChoice runtime)
            {
                _mpvRuntimeChoice = runtime == MpvRuntimeChoice.X64V3 ? MpvRuntimeChoice.X64V3 : MpvRuntimeChoice.X64;
                _mpvRuntimeV3Manual = _mpvRuntimeChoice == MpvRuntimeChoice.X64V3;
                _lblStatus.Text = runtime switch
                {
                    MpvRuntimeChoice.X64V3 => "Runtime MPV: V3 (experimental)",
                    _ => "Runtime MPV: Standard"
                };

                try { SaveExtrasConfig(); } catch { }
                if (_activeRendererChoice == VRChoice.MPV || _manualRendererChoice == VRChoice.MPV)
                    ReopenSame();
            }

            var miMpvRuntimeX64 = new ToolStripMenuItem("Standard", null, (_, __) => SetMpvRuntime(MpvRuntimeChoice.X64));
            var miMpvRuntimeX64V3 = new ToolStripMenuItem("V3 (experimental)", null, (_, __) => SetMpvRuntime(MpvRuntimeChoice.X64V3));
            mMpvRuntime.DropDownItems.AddRange(new ToolStripItem[] { miMpvRuntimeX64, miMpvRuntimeX64V3 });
            mMpvRuntime.DropDownOpening += (_, __) =>
            {
                miMpvRuntimeX64.Checked = _mpvRuntimeChoice != MpvRuntimeChoice.X64V3;
                miMpvRuntimeX64V3.Checked = _mpvRuntimeChoice == MpvRuntimeChoice.X64V3;
            };
            var mMpvTools = new ToolStripMenuItem("MPV / libmpv");
            var miMpvSettings = new ToolStripMenuItem(Tx("Impostazioni MPV...", "MPV settings..."), null, (_, __) => ShowSettingsHudPage("MPV"));
            mMpvTools.DropDownItems.AddRange(new ToolStripItem[]
            {
                mMpvRuntime,
                miMpvSettings
            });

            mRenderer.DropDownItems.AddRange(new ToolStripItem[]
            {
                miAuto,
                new ToolStripSeparator(),
                miMadvr,
                miMpcvr,
                miEvr,
                miMpv,
                new ToolStripSeparator(),
                mMpvTools
            });
            mRenderer.DropDownOpening += (_, __) =>
            {
                miMadvr.Checked = _manualRendererChoice == VideoRendererChoice.MADVR;
                miMpcvr.Checked = _manualRendererChoice == VideoRendererChoice.MPCVR;
                miEvr.Checked = _manualRendererChoice == VideoRendererChoice.EVR;
                miMpv.Checked = _manualRendererChoice == VideoRendererChoice.MPV;
                miAuto.Checked = _manualRendererChoice == null;
            };

            var mAudioRoot = new ToolStripMenuItem(Tx("Audio", "Audio"));
            mAudioRoot.DropDownItems.AddRange(new ToolStripItem[]
            {
                _mAudioLang,
                new ToolStripMenuItem(Tx("Audio esterno e sincronizzazione…", "External audio and synchronization…"), null, (_, _) => BeginInvoke(new Action(ShowAudioSynchronization))),
                _mSubtitles,
                new ToolStripSeparator(),
                _mAudioOut,
                mPcmPref
            });

            var mVideoRoot = new ToolStripMenuItem(Tx("Video", "Video")) { Tag = "menu-icon:video" };
            mVideoRoot.DropDownItems.AddRange(new ToolStripItem[]
            {
                mPip,
                mShowInfo,
                new ToolStripSeparator(),
                mRenderer,
                mHdr,
                m3D,
                mUpscale,
                new ToolStripSeparator(),
                mWebRes
            });

            // --- Telecomando web ---
            var mShowPin = new ToolStripMenuItem(Tx("Telecomando (mostra PIN)", "Remote control (show PIN)"), null, (_, __) =>
            {
                if (_pairBanner?.Visible == true)
                    HidePairingBanner();
                else if (_remote != null)
                    ShowPairingBanner(_remote.CurrentPin);
            });

            var mRemoteRoot = new ToolStripMenuItem(Tx("Telecomando", "Remote"));
            mRemoteRoot.DropDownItems.Add(mShowPin);

            var mAmplifier = new ToolStripMenuItem(Tx("Amplificatore via rete…", "Network amplifier…"), null, (_, __) => ShowAmplifierControl()) { Tag = "menu-icon:amplifier" };
            var mSettings = new ToolStripMenuItem(Tx("Impostazioni", "Settings"), null, (_, __) => ShowSettingsHudPage());
            var mExitApp = new ToolStripMenuItem(Tx("Esci", "Exit"), null, (_, __) => BeginInvoke(new Action(Close)));

            _menu.Items.AddRange(new ToolStripItem[]
            {
                mOpen,
                mOpenLib,
                mNetflixMode,
                new ToolStripSeparator(),

                mPlay,
                mStop,
                mQueue,
                _mChapters,
                mFull,
                mLoopTrack,
                new ToolStripSeparator(),

                mAudioRoot,
                mVideoRoot,
                mExtras,
                mRemoteRoot,
                mAmplifier,
                new ToolStripSeparator(),

                mSettings,
                mExitApp
            });

            // sincronia check stato upscaling + cleanup separatori
            _menu.Opening += (_, e) =>
            {
                if (!HandlePlaybackContextMenuOpening(e))
                    return;

                mUpscale.Checked = _enableUpscaling;

                bool hasEngine = _engine != null;
                bool canStartPending = _preOpenPlaceholderGateActive;

                mPlay.Text = canStartPending ? Tx("Avvia film", "Start movie") : (_paused ? Tx("Riprendi", "Resume") : Tx("Pausa", "Pause"));
                mStop.Text = canStartPending ? Tx("Annulla avvio", "Cancel start") : Tx("Chiudi file", "Close file");
                mPlay.Tag = "menu-icon:" + (canStartPending || _paused ? "play" : "pause");
                mStop.Tag = "menu-icon:" + (canStartPending ? "close" : "stop");
                mNetflixMode.Text = _netflixModePage?.Visible == true ? Tx("Chiudi Spotlight", "Close Spotlight") : "Spotlight";
                mFull.Text = FormBorderStyle == FormBorderStyle.None ? Tx("Esci da schermo intero", "Exit fullscreen") : Tx("Schermo intero", "Fullscreen");
                mFull.Tag = FormBorderStyle == FormBorderStyle.None ? "menu-icon:minimize" : "menu-icon:maximize";
                mShowInfo.Text = _infoOverlay.Visible ? Tx("Nascondi info", "Hide info") : Tx("Mostra info", "Show info");
                bool canUsePip = hasEngine && ShouldAllowPipForCurrentMedia();
                if (!canUsePip && _pipForm?.Visible == true)
                {
                    try { RestoreStandardFromPip(); } catch { }
                }
                mPip.Checked = _pipForm?.Visible == true;
                RefreshCinemaModeMenuState();

                // Azioni rapide: abilitate se c'è playback oppure placeholder gate attivo
                mPlay.Enabled = hasEngine || canStartPending;
                mStop.Enabled = hasEngine || canStartPending;
                mQueue.Enabled = true;
                mFull.Enabled = true;
                mPip.Enabled = canUsePip;
                mShowInfo.Enabled = hasEngine;

                bool canToggleTrackLoop = hasEngine && !_currentMediaHasVideo && !string.IsNullOrWhiteSpace(_currentPath);
                mLoopTrack.Visible = canToggleTrackLoop;
                mLoopTrack.Enabled = canToggleTrackLoop;
                mLoopTrack.Checked = canToggleTrackLoop && IsSingleTrackLoopEnabledForPath(_currentPath);

                bool isPhoto = IsPhotoMode;
                bool hasInfo = _info != null;
                bool isVideo = hasEngine && _currentMediaHasVideo && !isPhoto;
                bool isAudioOnly = hasEngine && !_currentMediaHasVideo;
                _mAudioLang.Enabled = isVideo || isAudioOnly;
                _mSubtitles.Enabled = isVideo;
                _mAudioOut.Enabled = isVideo || isAudioOnly;
                mPcmPref.Enabled = isVideo || isAudioOnly;
                mAudioRoot.Visible = isVideo || isAudioOnly;
                mAudioRoot.Enabled = isVideo || isAudioOnly;
                mVideoRoot.Visible = true;
                mVideoRoot.Enabled = true;
                m3D.Enabled = isVideo;
                mHdr.Enabled = isVideo;
                mUpscale.Enabled = isVideo;
                mWebRes.Enabled = true;
                mRenderer.Enabled = true;
                _mChapters.Visible = true;
                _mChapters.Enabled = isVideo && hasInfo && _info!.Chapters.Count > 0;

                // assicura visibilità corretta delle voci dipendenti dal media
                RefreshMenuVisibility();

                // e poi elimina eventuali linee doppie
                CollapseSeparators();
                PrepareDarkDropDown(_menu, 210, 360);
            };

            // Assicura che anche i dropdown (sottomenu) ereditino il tema scuro.
            try { ApplyDarkMenuThemeRecursive(_menu.Items); } catch { }
        }

        private void RebuildContextMenu()
        {
            ContextMenuStrip? old = _menu;
            try { old?.Close(); } catch { }
            BuildMenu();
            _menu.Opening += (_, e) =>
            {
                if (!HandlePlaybackContextMenuOpening(e))
                    return;
                RefreshMenuVisibility();
            };
            _menu.Opened += (_, __) =>
            {
                BeginContextMenuHudBlock();
            };
            _menu.Closed += (_, __) => EndContextMenuHudBlock();
            ContextMenuStrip = _menu;
            try { _stack.ContextMenuStrip = _menu; } catch { }
            try { _hud.ContextMenuStrip = _menu; } catch { }
            try { _infoOverlay.ContextMenuStrip = _menu; } catch { }
            try { _remoteOsd.ContextMenuStrip = _menu; } catch { }
            try { _videoHost.ContextMenuStrip = _menu; } catch { }
            try { _netflixModePage.ContextMenuStrip = _menu; } catch { }
            try { _settingsHudPage.ContextMenuStrip = _menu; } catch { }
            try { if (_cinematicLibraryPage != null) _cinematicLibraryPage.ContextMenuStrip = _menu; } catch { }
            try { _overlayHost.ContextMenuStrip = _menu; } catch { }
            try { _overlayHost.Surface.ContextMenuStrip = _menu; } catch { }
            try { _audioMetersHost.ContextMenuStrip = _menu; } catch { }
            AttachPlaybackContextMenuFallbacks();
            try { old?.Dispose(); } catch { }
        }

        private void RefreshMenuVisibility()
        {
            if (_menu == null) return;

            bool hasEngine = _engine != null;
            bool hasInfo = _info != null;
            bool isPhoto = IsPhotoMode;
            bool isVideo = hasEngine && _currentMediaHasVideo && !isPhoto;
            bool isAudioOnly = hasEngine && !_currentMediaHasVideo;

            if (_mAudioLang != null) _mAudioLang.Visible = isVideo || isAudioOnly;
            if (_mSubtitles != null) _mSubtitles.Visible = isVideo;

            if (_mChapters != null)
            {
                _mChapters.Visible = true;
                _mChapters.Enabled = isVideo && hasInfo && _info!.Chapters.Count > 0;
            }
            if (_mLoopTrack != null)
                _mLoopTrack.Visible = isAudioOnly && !string.IsNullOrWhiteSpace(_currentPath);

            foreach (ToolStripItem item in _menu.Items)
            {
                if (item is ToolStripMenuItem mi)
                {
                    if (string.Equals(mi.Text, Tx("Video", "Video"), StringComparison.OrdinalIgnoreCase))
                    {
                        mi.Visible = true;
                        mi.Enabled = true;
                    }
                    else if (string.Equals(mi.Text, Tx("Audio", "Audio"), StringComparison.OrdinalIgnoreCase))
                    {
                        mi.Visible = isVideo || isAudioOnly;
                        mi.Enabled = isVideo || isAudioOnly;
                    }
                }
            }
        }

        private static void MenuItem_MouseEnter(object? sender, EventArgs e)
        {
            try
            {
                if (sender is ToolStripItem item && item.Enabled)
                {
                    item.Select();
                    item.Owner?.Invalidate();
                }
            }
            catch { }
        }

        private void PopulateChaptersMenu(ToolStripMenuItem root)
        {
            root.DropDownItems.Clear();

            if (_info == null || _info.Chapters.Count == 0)
            {
                var empty = new ToolStripMenuItem(Tx("Nessun capitolo", "No chapters")) { Enabled = false };
                root.DropDownItems.Add(empty);
                return;
            }

            foreach (var (title, start) in _info.Chapters)
            {
                var text = $"{Fmt(start)}  {title}";
                double s = start;
                var it = new ToolStripMenuItem(text);
                it.Click += (_, __) =>
                {
                    if (_engine != null)
                    {
                        PreparePlaybackSeek(clearTimelinePreview: true, previewSeconds: s);
                        _engine.PositionSeconds = s;
                    }
                    _hud.ShowOnce(1200);
                };
                root.DropDownItems.Add(it);
            }
        }

        // ======= Uscita audio raggruppata =======
        private void PopulateAudioOutputMenu(ToolStripMenuItem root)
        {
            root.DropDownItems.Clear();

            List<DsDevice> all;
            try { all = DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory).ToList(); }
            catch { all = new List<DsDevice>(); }

            var grpDefault = new ToolStripMenuItem(Tx("Predefinito di sistema", "System default"));
            var grpWasapi = new ToolStripMenuItem("WASAPI");
            var grpDs = new ToolStripMenuItem("DirectSound");
            var grpMpc = new ToolStripMenuItem("MPC Audio Renderer");

            foreach (var dev in all.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ToolStripMenuItem(dev.Name)
                {
                    Checked = string.Equals(dev.Name, _selectedAudioRendererName, StringComparison.OrdinalIgnoreCase)
                };
                string captured = dev.Name;
                item.Click += (_, __) =>
                {
                    _selectedAudioRendererName = captured;
                    _selectedRendererLooksHdmi = LooksHdmi(captured);
                    _lblStatus.Text = Tx("Uscita audio: ", "Audio output: ") + captured;
                    ReopenSame();
                };

                var nl = dev.Name.ToLowerInvariant();
                if (nl.Contains("mpc audio renderer")) grpMpc.DropDownItems.Add(item);
                else if (nl.Contains("wasapi")) grpWasapi.DropDownItems.Add(item);
                else if (nl.Contains("directsound")) grpDs.DropDownItems.Add(item);
                else grpDefault.DropDownItems.Add(item);
            }

            void sortItems(ToolStripMenuItem m)
            {
                var list = m.DropDownItems.OfType<ToolStripMenuItem>()
                    .OrderBy(i => i.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
                m.DropDownItems.Clear();
                foreach (var it in list) m.DropDownItems.Add(it);
            }
            sortItems(grpDefault);
            sortItems(grpWasapi);
            sortItems(grpDs);
            sortItems(grpMpc);

            if (grpDefault.DropDownItems.Count == 0)
            {
                var miDefault = new ToolStripMenuItem(Tx("Usa dispositivo predefinito", "Use default device")) { Checked = string.IsNullOrWhiteSpace(_selectedAudioRendererName) };
                miDefault.Click += (_, __) =>
                {
                    _selectedAudioRendererName = null;
                    _selectedRendererLooksHdmi = false;
                    _lblStatus.Text = Tx("Uscita audio: predefinito di sistema", "Audio output: system default");
                    ReopenSame();
                };
                grpDefault.DropDownItems.Add(miDefault);
            }

            if (grpDefault.DropDownItems.Count > 0) root.DropDownItems.Add(grpDefault);
            if (grpWasapi.DropDownItems.Count > 0) root.DropDownItems.Add(grpWasapi);
            if (grpDs.DropDownItems.Count > 0) root.DropDownItems.Add(grpDs);
            if (grpMpc.DropDownItems.Count > 0) root.DropDownItems.Add(grpMpc);
        }

        private static bool LooksHdmi(string name)
        {
            return LooksBitstreamCapableAudioOutput(name);
        }

        private static bool LooksBitstreamCapableAudioOutput(string? name)
        {
            string n = (name ?? "").ToLowerInvariant();
            string[] tokens =
            {
                "hdmi", "display audio", "avr", "receiver", "a/v", "home theater",
                "spdif", "s/pdif", "optical", "toslink", "digital audio", "digital output",
                "denon", "marantz", "onkyo", "yamaha", "pioneer", "sony",
                "nvidia high definition audio", "intel(r) display audio", "amd high definition audio"
            };
            return tokens.Any(n.Contains);
        }

        private static string? ExtractOriginalVideoName(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;

            try
            {
                // File URI (file:///C:/...)
                if (Uri.TryCreate(requested, UriKind.Absolute, out var u) && u.IsFile)
                {
                    var lp = u.LocalPath;
                    return string.IsNullOrWhiteSpace(lp) ? requested : Path.GetFileName(lp);
                }
            }
            catch { }

            try
            {
                if (File.Exists(requested)) return Path.GetFileName(requested);
            }
            catch { }

            // Best-effort per YouTube: video id
            try
            {
                if (Uri.TryCreate(requested, UriKind.Absolute, out var u2))
                {
                    var host = (u2.Host ?? "").ToLowerInvariant();
                    if (host.Contains("youtu.be"))
                    {
                        var seg = u2.AbsolutePath.Trim('/');
                        if (!string.IsNullOrWhiteSpace(seg)) return seg;
                    }
                    if (host.Contains("youtube.com"))
                    {
                        var v = TryGetQueryParam(u2.Query, "v");
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                }
            }
            catch { }

            // fallback: l'ultima parte (o l'intera stringa)
            try
            {
                return Path.GetFileName(requested.TrimEnd('/'));
            }
            catch
            {
                return requested;
            }
        }

        private static string NormalizeDisplayTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string s = raw.Trim();

            // URL: evita di mostrare query/parametri. Per YouTube, meglio un id (se disponibile).
            try
            {
                if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    var host = (u.Host ?? string.Empty).Trim();
                    var h = host.ToLowerInvariant();
                    if (h.Contains("youtube.com") || h.Contains("youtu.be"))
                    {
                        var id = ExtractOriginalVideoName(s);
                        if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, "watch", StringComparison.OrdinalIgnoreCase))
                            return id.Trim();
                        return "YouTube";
                    }

                    if (!string.IsNullOrWhiteSpace(host))
                        return host;
                }
            }
            catch { }

            // File / path / nome file
            string name = s;
            try
            {
                if (Uri.TryCreate(s, UriKind.Absolute, out var uf) && uf.IsFile)
                    name = uf.LocalPath;

                // Rimuovi estensione, lascia solo nome
                name = Path.GetFileNameWithoutExtension(name);
            }
            catch { name = s; }

            if (string.IsNullOrWhiteSpace(name)) name = s;

            // Normalizzazione base: punti/underscore → spazi, collassa spazi multipli.
            name = name.Replace('_', ' ').Replace('.', ' ');

            try { name = Regex.Replace(name, @"\s+", " ").Trim(); }
            catch { name = name.Trim(); }

            return name;
        }

        private static string? TryGetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            if (query.Length > 0 && query[0] == '?') query = query.Substring(1);
            foreach (var part in query.Split('&'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                var kv = part.Split('=');
                if (kv.Length == 0) continue;
                var k = Uri.UnescapeDataString(kv[0] ?? "");
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                var val = kv.Length > 1 ? kv[1] : "";
                return Uri.UnescapeDataString(val ?? "");
            }
            return null;
        }
        private void PopulateAudioLangMenu()
        {
            _mAudioLang.DropDownItems.Clear();

            if (_engine == null)
            {
                var it = new ToolStripMenuItem(Tx("(nessuna traccia)", "(no tracks)")) { Enabled = false };
                _mAudioLang.DropDownItems.Add(it);
                return;
            }

            var streams = _engine.EnumerateStreams().Where(s => s.IsAudio).ToList();
            if (streams.Count == 0)
            {
                var it = new ToolStripMenuItem(Tx("(nessuna traccia)", "(no tracks)")) { Enabled = false };
                _mAudioLang.DropDownItems.Add(it);
                return;
            }

            int ordinal = 0;
            foreach (var s in streams)
            {
                ordinal++;
                var name = SubtitleNameNormalizer.NormalizeAudioTrackName(s.Name, ordinal);
                var it = new ToolStripMenuItem(name) { Checked = s.Selected };
                int idx = s.GlobalIndex;
                string? audioLangKey = SubtitleLanguage(s);

                it.Click += (_, __) =>
                {
                    if (_engine == null) return;

                    _engine.EnableByGlobalIndex(idx);

                    if (!string.IsNullOrWhiteSpace(audioLangKey))
                        _preferredSubtitleLangKey = audioLangKey;

                    if (_subtitleAutoForcedMode)
                    {
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (_engine == null) return;
                                    var latestSubs = _engine.EnumerateStreams().Where(x => x.IsSubtitle).ToList();
                                    if (!TrySelectAutoForcedSubtitles(_engine, latestSubs, ResolvePreferredAutoForcedLangKey(latestSubs)))
                                        _subtitleAutoForcedMode = false;
                                }
                                catch { }
                            }));
                        }
                        catch { }
                    }

                    _lblStatus.Text = $"Audio: {name}";
                    _hud.ShowOnce(1200);
                    if (_info != null)
                    {
                        UpdateInfoOverlay(ResolveRendererForInfo(_info.IsHdr), _info.IsHdr);
                    }
                };
                _mAudioLang.DropDownItems.Add(it);
            }
        }

        private void PopulateSubtitlesMenu()
        {
            _mSubtitles.DropDownItems.Clear();

            if (_engine == null)
            {
                var it = new ToolStripMenuItem(Tx("(nessuna traccia)", "(no tracks)")) { Enabled = false };
                _mSubtitles.DropDownItems.Add(it);
                return;
            }

            var streams = _engine.EnumerateStreams().Where(s => s.IsSubtitle).ToList();
            if (streams.Count == 0)
            {
                _subtitleAutoForcedMode = false;
                var it = new ToolStripMenuItem(Tx("(nessuna traccia)", "(no tracks)")) { Enabled = false };
                _mSubtitles.DropDownItems.Add(it);
                return;
            }

            bool hasSelected = streams.Any(s => s.Selected);
            bool selectedAutoForced = streams.Any(s => s.Selected && IsForcedSubtitle(s));
            _subtitleAutoForcedMode = selectedAutoForced;

            if (string.IsNullOrWhiteSpace(_preferredSubtitleLangKey))
                _preferredSubtitleLangKey = ResolvePreferredAutoForcedLangKey(streams);

            var off = new ToolStripMenuItem(Tx("Disattivati", "Off"))
            {
                Checked = !hasSelected || streams.Any(s => s.Selected && DirectShowUnifiedEngine.IsSubtitleOffOption(s.Name))
            };
            off.Click += (_, __) =>
            {
                if (_engine == null) return;
                if (!_engine.DisableSubtitlesIfPossible()) { _lblStatus.Text = Tx("Impossibile disattivare i sottotitoli.", "Unable to disable subtitles."); return; }
                _subtitleAutoForcedMode = false;
                _lblStatus.Text = Tx("Sottotitoli: disattivati", "Subtitles: off");
                _hud.ShowOnce(1200);
            };
            _mSubtitles.DropDownItems.Add(off);
            var forced = streams.Where(s => IsForcedSubtitle(s)).ToList();
            if (forced.Count > 0)
            {
                var automatic = new ToolStripMenuItem(Tx("Automatici (solo forzati)", "Automatic (forced only)"))
                {
                    Checked = selectedAutoForced
                };
                automatic.Click += (_, __) =>
                {
                    if (_engine == null) return;
                    string? preferred = ResolvePreferredAutoForcedLangKey(streams);
                    _subtitleAutoForcedMode = TrySelectAutoForcedSubtitles(_engine, streams, preferred);
                    if (_subtitleAutoForcedMode && !string.IsNullOrWhiteSpace(preferred))
                        _preferredSubtitleLangKey = preferred;
                    _lblStatus.Text = _subtitleAutoForcedMode
                        ? Tx("Sottotitoli: automatici", "Subtitles: automatic")
                        : Tx("Sottotitoli: disattivati", "Subtitles: off");
                    _hud.ShowOnce(1200);
                };
                _mSubtitles.DropDownItems.Add(automatic);
            }

            _mSubtitles.DropDownItems.Add(new ToolStripSeparator());

            int ordinal = 0;
            foreach (var stream in streams.Where(s => !DirectShowUnifiedEngine.IsSubtitleOffOption(s.Name)).OrderByDescending(s => s.Selected).ThenBy(s => s.GlobalIndex))
            {
                ordinal++;
                string label = SubtitleNameNormalizer.NormalizeSubtitleTrackName(stream.Name, ordinal).Trim();
                if (string.IsNullOrWhiteSpace(label))
                    label = Tx($"Traccia {ordinal}", $"Track {ordinal}");
                if (IsForcedSubtitle(stream) &&
                    !label.Contains("forced", StringComparison.OrdinalIgnoreCase) &&
                    !label.Contains("forzat", StringComparison.OrdinalIgnoreCase))
                    label += Tx(" · Forzati", " · Forced");

                int index = stream.GlobalIndex;
                string selectedLabel = label;
                string? languageKey = SubtitleLanguage(stream);
                var item = new ToolStripMenuItem(label) { Checked = stream.Selected };
                item.Click += (_, __) =>
                {
                    _engine?.EnableByGlobalIndex(index);
                    _subtitleAutoForcedMode = IsForcedSubtitle(stream);
                    if (!string.IsNullOrWhiteSpace(languageKey))
                        _preferredSubtitleLangKey = languageKey;
                    _lblStatus.Text = Tx($"Sottotitoli: {selectedLabel}", $"Subtitles: {selectedLabel}");
                    _hud.ShowOnce(1200);
                };
                _mSubtitles.DropDownItems.Add(item);
            }
        }

        // ===== Subtitle language and forced-track selection =====
        private static bool IsAutoForcedSubtitleName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return Regex.IsMatch(name, @"\bforced\b|\bforzat[oi]\b|\bforz\b", RegexOptions.IgnoreCase);
        }

        private static bool IsForcedSubtitle(DsStreamItem stream) => stream.IsForced || IsAutoForcedSubtitleName(stream.Name);
        private static string? SubtitleLanguage(DsStreamItem? stream) => stream?.LanguageKey ?? DetectLangKeyFromName(stream?.Name);

        private string? GetSelectedAudioLangKey()
        {
            try
            {
                if (_engine == null) return null;
                var sel = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
                return SubtitleLanguage(sel);
            }
            catch
            {
                return null;
            }
        }

        private string? ResolvePreferredAutoForcedLangKey(List<DsStreamItem>? subtitleStreams = null)
        {
            var audioKey = GetSelectedAudioLangKey();
            if (!string.IsNullOrWhiteSpace(audioKey))
                return audioKey;

            if (!string.IsNullOrWhiteSpace(_preferredSubtitleLangKey))
                return _preferredSubtitleLangKey;

            try
            {
                var streams = subtitleStreams ?? _engine?.EnumerateStreams().Where(s => s.IsSubtitle).ToList();
                var curSel = streams?.FirstOrDefault(s => s.Selected && !IsForcedSubtitle(s))
                          ?? streams?.FirstOrDefault(s => s.Selected);
                return SubtitleLanguage(curSel);
            }
            catch
            {
                return null;
            }
        }

        // Ritorna un key semplice per la lingua (it/en/…)
        private static string? DetectLangKeyFromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var normalized = SubtitleNameNormalizer.TryDetectLanguageKey(name);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;

            var s = name.ToLowerInvariant();

            if (Regex.IsMatch(s, @"\b(it|ita|italian|italiano)\b", RegexOptions.IgnoreCase)) return "it";
            if (Regex.IsMatch(s, @"\b(en|eng|english|inglese)\b", RegexOptions.IgnoreCase)) return "en";
            if (Regex.IsMatch(s, @"\b(es|spa|spanish|spagnolo|espanol|español)\b", RegexOptions.IgnoreCase)) return "es";
            if (Regex.IsMatch(s, @"\b(fr|fra|fre|french|francese|français|francais)\b", RegexOptions.IgnoreCase)) return "fr";
            if (Regex.IsMatch(s, @"\b(de|ger|deu|german|tedesco|deutsch)\b", RegexOptions.IgnoreCase)) return "de";
            if (Regex.IsMatch(s, @"\b(pt|por|portuguese|portoghese)\b", RegexOptions.IgnoreCase)) return "pt";
            if (Regex.IsMatch(s, @"\b(ru|rus|russian|russo)\b", RegexOptions.IgnoreCase)) return "ru";
            if (Regex.IsMatch(s, @"\b(ja|jpn|japanese|giapponese)\b", RegexOptions.IgnoreCase)) return "ja";
            if (Regex.IsMatch(s, @"\b(zh|chi|zho|chinese|cinese)\b", RegexOptions.IgnoreCase)) return "zh";
            return null;
        }

        private static string LanguageLabelFromKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "Sottotitoli";
            key = key.Trim().ToLowerInvariant();
            return key switch
            {
                "it" => "Italiano",
                "en" => "Inglese",
                "es" => "Spagnolo",
                "fr" => "Francese",
                "de" => "Tedesco",
                "pt" => "Portoghese",
                "ru" => "Russo",
                "ja" => "Giapponese",
                "zh" => "Cinese",
                _ => key.ToUpperInvariant()
            };
        }

        private static int SubtitleMenuScore(DsStreamItem s)
        {
            int score = 0;
            if (s.Selected) score += 10_000;
            var name = (s.Name ?? "").ToLowerInvariant();
            if (name.Contains("sdh") || name.Contains("hearing") || name.Contains("hi")) score -= 50;
            if (name.Contains("comment")) score -= 20;
            if (name.Contains("forced")) score -= 10;
            score -= Math.Min(200, name.Length / 2);
            return score;
        }

        private static bool TrySelectAutoForcedSubtitles(IPlaybackEngine engine, List<DsStreamItem> subtitleStreams, string? preferredLangKey)
        {
            try
            {
                var best = string.IsNullOrWhiteSpace(preferredLangKey) ? null : subtitleStreams.FirstOrDefault(s =>
                    IsForcedSubtitle(s) && string.Equals(SubtitleLanguage(s), preferredLangKey, StringComparison.OrdinalIgnoreCase));
                // LAV's automatic stream follows its audio-language rules. A forced
                // track explicitly labelled with another language must never win.
                best ??= subtitleStreams.FirstOrDefault(s => IsForcedSubtitle(s) &&
                    SubtitleLanguage(s) == null && Regex.IsMatch(s.Name ?? "", @"\bauto(?:matic)?\b", RegexOptions.IgnoreCase));
                if (best != null) return engine.EnableByGlobalIndex(best.GlobalIndex);
                engine.DisableSubtitlesIfPossible();
                return false;
            }
            catch { return false; }
        }

        // ===== Extras persistence =====
        private sealed class ExtrasConfig
        {
            public bool PausePlaceholderEnabled { get; set; }
            public string? PausePlaceholderFile { get; set; }
            public bool PausePlaceholderUseTmdbBackdrop { get; set; }
            public bool PreRollEnabled { get; set; }
            public string? PreRollDemoFile { get; set; }
            public bool WledEnabled { get; set; }
            public string? WledBaseUrl { get; set; }
            public bool CinemaModeEnabled { get; set; }
            public bool NetflixModeEnabled { get; set; }
            public string? Language { get; set; }
            public int? UiAccentArgb { get; set; }
            public int? UiAccentSoftArgb { get; set; }
            public int? UiSelectionArgb { get; set; }
            public int? UiBorderAccentArgb { get; set; }
            public int? UiPanelArgb { get; set; }
            public int? UiCardArgb { get; set; }
            public int? UiNavArgb { get; set; }
            public string? PreferredRenderer { get; set; }
            public string? VideoUpscalingBackend { get; set; }
            public int MpcvrSuperResolutionMode { get; set; } = 3;
            public int MadVrUpscalePreset { get; set; }
            public bool StereoAutoEnabled { get; set; }
            public string? MpvRuntime { get; set; }
            public bool MpvRuntimeV3Manual { get; set; }
            public string? MpvProfile { get; set; }
            public string? MpvHwdec { get; set; }
            public string? MpvVideoOutput { get; set; }
            public string? MpvGpuApi { get; set; }
            public string? MpvGpuContext { get; set; }
            public string? MpvVideoSync { get; set; }
            public bool MpvInterpolation { get; set; }
            public string? MpvToneMapping { get; set; }
            public bool MpvHdrComputePeak { get; set; } = true;
            public string? MpvTargetPrim { get; set; }
            public string? MpvTargetTrc { get; set; }
            public int MpvTargetPeak { get; set; }
            public string? MpvGamutMappingMode { get; set; }
            public bool MpvTargetColorspaceHint { get; set; } = true;
            public bool MpvIccProfileAuto { get; set; } = true;
            public string? MpvBlendSubtitles { get; set; }
            public string? MpvFboFormat { get; set; }
            public string? MpvVideoOutputLevels { get; set; }
            public string? MpvScale { get; set; }
            public string? MpvCScale { get; set; }
            public string? MpvDScale { get; set; }
            public string? MpvTScale { get; set; }
            public double MpvScaleAntiring { get; set; }
            public double MpvCScaleAntiring { get; set; }
            public double MpvDScaleAntiring { get; set; }
            public bool MpvCorrectDownscaling { get; set; } = true;
            public bool MpvLinearDownscaling { get; set; } = true;
            public bool MpvSigmoidUpscaling { get; set; } = true;
            public double MpvToneMappingParam { get; set; }
            public double MpvHdrContrastRecovery { get; set; }
            public double MpvHdrContrastSmoothness { get; set; }
            public bool MpvDeband { get; set; }
            public int MpvDebandIterations { get; set; } = 1;
            public int MpvDebandThreshold { get; set; } = 48;
            public int MpvDebandRange { get; set; } = 16;
            public int MpvDebandGrain { get; set; }
            public string? MpvDither { get; set; }
            public string? MpvDitherDepth { get; set; }
            public int MpvDitherSizeFruit { get; set; } = 6;
            public string? MpvErrorDiffusion { get; set; }
            public bool MpvTemporalDither { get; set; } = true;
            public bool MpvDeinterlace { get; set; }
            public double MpvInterpolationThreshold { get; set; }
            public string? MpvCache { get; set; }
            public int MpvDemuxerReadaheadSeconds { get; set; }
            public int MpvDemuxerMaxBytesMb { get; set; }
            public int MpvVideoThreads { get; set; }
            public bool MpvVideoLavcDr { get; set; } = true;
            public string? MpvSubAuto { get; set; }
            public string? MpvSubAssOverride { get; set; }
            public double MpvSubScale { get; set; } = 1.0;
            public int MpvSubFontSize { get; set; } = 55;
            public double MpvSubBorderSize { get; set; } = 3.0;
            public double MpvSubShadowOffset { get; set; }
            public bool MpvAudioExclusive { get; set; }
            public string? MpvAudioChannels { get; set; }
            public bool MpvAudioNormalizeDownmix { get; set; }
            public int MpvVolumeMax { get; set; } = 100;
            public string? MpvGaplessAudio { get; set; }
            public string? MpvExtraOptions { get; set; }
        }

        private string ExtrasConfigPath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
                return Path.Combine(dir, "extras.json");
            }
        }


    }
}
