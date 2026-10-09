#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025
{
    /// <summary>
    /// UI WinForms + LiveCharts (reflection) con 4 pagine:
    /// 1) Levels: VU, Scope, Crest, Balance + Correlazione
    /// 2) Spectrum: spettro grande + info
    /// 3) Loudness: LUFS M/S/I, LRA, dBTP L/R, PSR/PLR
    /// 4) Stereo/Diag: Width, Corr storico, DC/Noise/SNR/ENOB
    /// </summary>
    internal sealed partial class AudioMetersLiveCharts : UserControl
    {
        private const int HISTORY_N = 180;
        private const int VISUAL_INTERVAL_MS = 33;
        // Stesso colore della pagina: nessun riquadro di tinta diversa dietro grafici e testi.
        private static Color MeterBack => HUD.Theme.Panel;
        private static Color PanelBack => MeterBack;
        private static Color PanelBorder => HUD.Theme.Border;
        private static Color TextMain => HUD.Theme.Text;
        private static Color TextMuted => HUD.Theme.Muted;
        private static readonly ToolTip ChartToolTip = new() { ShowAlways = true, AutomaticDelay = 320 };

        // === Reflection types ===
        private Type? _chartT, _axisT, _lineSeriesT, _solidPaintT, _iSeriesT, _legendPosT, _tipPosT;

        // ====== Charts/axes (Levels) ======
        private object? _vuChart, _scopeChart, _crestChart, _balanceChart, _corrChart1;
        private object? _vuRmsSeries, _vuPkSeries;
        private object? _scopeLSeries, _scopeRSeries;
        private object? _crestSeries, _crestRSeries;
        private object? _balanceSeries;
        private object? _corrSeries1;
        private object? _xAxisVu, _yAxisVu, _xAxisSc, _yAxisSc, _xAxisCr, _yAxisCr, _xAxisBal, _yAxisBal, _xAxisCorr1, _yAxisCorr1;

        // ====== Charts/axes (Spectrum) ======
        private object? _specChart;
        private object? _specSeries;
        private object? _xAxisSp, _yAxisSp;

        // ====== Charts/axes (Loudness) ======
        private object? _loudChart, _lraChart, _tpChart, _dynChart;
        private object? _loudSeries, _loudSSeries, _loudISeries, _lraSeries, _tpSeries, _tpRSeries, _dynSeries, _dynPlrSeries;
        private object? _xAxisLoud, _yAxisLoud, _xAxisLra, _yAxisLra, _xAxisTp, _yAxisTp, _xAxisDyn, _yAxisDyn;

        // ====== Charts/axes (Stereo/Diag) ======
        private object? _widthChart, _corrChart2;
        private object? _widthSeries, _corrSeries2;
        private object? _xAxisWidth, _yAxisWidth, _xAxisCorr2, _yAxisCorr2;

        // ====== Labels / badges (NO FLICKER) ======
        private readonly BufferedLabel _msg = new()
        {
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextMain,
            BackColor = MeterBack,
            Visible = false
        };

        private readonly BufferedLabel _badgesSpectrum = MakeBadge();
        private readonly BufferedLabel _badgesLoud = MakeBadge();
        private readonly BufferedLabel _badgesPeaks = MakeBadge();
        private readonly BufferedLabel _badgesDiag = MakeBadge();
        private readonly BufferedLabel _badgesStereo = MakeBadge();
        private readonly BufferedLabel _badgesLevels = MakeBadge();
        private readonly BufferedLabel _badgesDynamics = MakeBadge();

        // ====== Navigation (custom, niente TabControl) ======
        private readonly BufferedPanel _navBar = new() { Dock = DockStyle.Top, Height = 52, BackColor = MeterBack, Padding = new Padding(12, 9, 12, 8) };
        private readonly BufferedTable _navGrid = new()
        {
            Dock = DockStyle.None,
            BackColor = MeterBack,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0)
        };
        private readonly NavButton _btnLevels = new("Analisi audio");
        private readonly NavButton _btnSpectrum = new("Spettro");
        private readonly NavButton _btnLoud = new("Loudness");
        private readonly NavButton _btnStereo = new("Stereo");
        private readonly NavButton _btnLyrics = new("Lyrics");
        private readonly NavButton _btnToggle = new("Ascolto");
        private readonly BufferedLabel _nowPlayingHeader = new()
        {
            BackColor = MeterBack,
            ForeColor = TextMain,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 18f),
            Text = "Audio"
        };
        private readonly BufferedLabel _workspaceSubtitle = new() { BackColor = MeterBack, ForeColor = TextMuted, Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f), Visible = false };
        private readonly BufferedLabel _qualityHeader = new()
        {
            BackColor = MeterBack,
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.MiddleRight,
            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f),
            Text = "LIVE ANALYSIS"
        };

        private readonly BufferedPanel _content = new() { Dock = DockStyle.Fill, BackColor = MeterBack };
        private readonly BufferedLabel _syncStatus = new() { Dock = DockStyle.Bottom, Height = 40,
            BackColor = MeterBack, ForeColor = TextMuted, TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(24, 0, 24, 8), Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f), AutoEllipsis = true, Tag = "sync-status" };
        public void SetSynchronizationStatus(string status) => SetLabelTextNoFlicker(_syncStatus, status);
        private readonly AudioPlaceholderOverlay _placeholderOverlay = new();
        private readonly AudioArtworkCard _artworkCard = new();

        // ====== Pages ======
        private readonly TableLayoutPanel _pageLevels = NewPage(rows: 2);
        private readonly TableLayoutPanel _pageSpectrum = NewPage(rows: 2);
        private readonly TableLayoutPanel _pageLoud = NewPage(rows: 1);
        private readonly TableLayoutPanel _pageStereo = NewPage(rows: 2);
        private readonly TableLayoutPanel _pageLyrics = NewPage(rows: 1);
        private readonly BufferedLabel _lyricsTitle = new()
        {
            Dock = DockStyle.Top,
            Height = 42,
            ForeColor = Color.White,
            BackColor = MeterBack,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 12f)
        };
        private readonly BufferedLabel _lyricsStatus = new()
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            ForeColor = TextMuted,
            BackColor = MeterBack,
            TextAlign = ContentAlignment.MiddleCenter
        };
        private readonly LyricsDisplay _lyricsView = new() { Dock = DockStyle.Fill, BackColor = MeterBack };
        private readonly System.Windows.Forms.Timer _lyricsLoadingTimer = new() { Interval = 140 };
        private IReadOnlyList<MusicLyricsService.LyricsLine>? _syncedLyrics;
        private int _lastSyncedLyricsIndex = -1;
        private int _lastSyncedWordIndex = -2;

        // Storici / stati
        private readonly double[] _corrHist = new double[240];
        private int _corrW;
        private int _lastSpectrumSr, _lastSpectrumBins, _lastFftLen;
        private double _spYMin = -100;   // spettro (smoothed)
        private double _scVisAmp = 0.5; // scope (smoothed)
        private double _widthVis = 24;  // width half-range (smoothed)
        private bool _ok;
        private int _currentPage;
        private int _playbackInfoSection;
        private bool _graphsEnabled = true;
        private bool _english;
        private DateTime _lastVisualUtc = DateTime.MinValue;
        private string _trackTitle = "Audio";
        private string _trackSubtitle = string.Empty;
        private string _trackQuality = string.Empty;

        private readonly double[] _rmsLHist = MakeHistory(-90);
        private readonly double[] _rmsRHist = MakeHistory(-90);
        private readonly double[] _crestLHist = MakeHistory(0);
        private readonly double[] _crestRHist = MakeHistory(0);
        private readonly double[] _balanceHist = MakeHistory(0);
        private readonly double[] _loudMHist = MakeHistory(-70);
        private readonly double[] _loudSHist = MakeHistory(-70);
        private readonly double[] _loudIHist = MakeHistory(-70);
        private readonly double[] _lraHist = MakeHistory(0);
        private readonly double[] _tpLHist = MakeHistory(-12);
        private readonly double[] _tpRHist = MakeHistory(-12);
        private readonly double[] _psrHist = MakeHistory(0);
        private readonly double[] _plrHist = MakeHistory(0);
        private readonly double[] _widthHist = MakeHistory(0);

        // Skia charts own their buffered surfaces; WS_EX_COMPOSITED causes
        // recursive paints when the native player and charts share a window.
        private int _builtPlaybackInfoSection = -1;

        // ===== Bootstrap =====
        static AudioMetersLiveCharts()
        {
            try
            {
                AddNativeSearchPaths();
                AppDomain.CurrentDomain.AssemblyResolve += (_, a) =>
                {
                    var n = new AssemblyName(a.Name).Name + ".dll";
                    var b = AppContext.BaseDirectory;
                    string[] c = {
                        Path.Combine(b, n),
                        Path.Combine(b, "runtimes","win-x64","lib", n),
                        Path.Combine(b, "runtimes","win","lib", n)
                    };
                    foreach (var p in c) if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { } }
                    return null;
                };
                TryLoadManaged("LiveChartsCore");
                TryLoadManaged("LiveChartsCore.SkiaSharpView");
                TryLoadManaged("LiveChartsCore.SkiaSharpView.WinForms");
                TryLoadManaged("SkiaSharp");
                TryLoadManaged("SkiaSharp.Views.WindowsForms");
            }
            catch { }
        }

        public AudioMetersLiveCharts()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = MeterBack;

            Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Regular);
            _msg.Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Bold);

            // reflection types
            _chartT = Resolve("LiveChartsCore.SkiaSharpView.WinForms.CartesianChart, LiveChartsCore.SkiaSharpView.WinForms");
            _axisT = Resolve("LiveChartsCore.SkiaSharpView.Axis, LiveChartsCore.SkiaSharpView");
            _lineSeriesT = Resolve("LiveChartsCore.SkiaSharpView.LineSeries`1[[System.Double, System.Private.CoreLib]], LiveChartsCore.SkiaSharpView");
            _solidPaintT = Resolve("LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint, LiveChartsCore.SkiaSharpView");
            _iSeriesT = Resolve("LiveChartsCore.ISeries, LiveChartsCore");
            _legendPosT = Resolve("LiveChartsCore.Measure.LegendPosition, LiveChartsCore");
            _tipPosT = Resolve("LiveChartsCore.Measure.TooltipPosition, LiveChartsCore");

            // === NAV BAR: le tre viste principali del mockup. I grafici si scelgono nella rail di Playback Info. ===
            _navGrid.ColumnCount = 3;
            _navGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            _navGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26f));
            _navGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
            _navGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _navGrid.Controls.Add(_btnToggle, 0, 0);
            _navGrid.Controls.Add(_btnLyrics, 1, 0);
            _navGrid.Controls.Add(_btnLevels, 2, 0);
            _navBar.Controls.Add(_workspaceSubtitle);
            _navBar.Controls.Add(_nowPlayingHeader);
            _navBar.Controls.Add(_qualityHeader);
            _navBar.Controls.Add(_navGrid);

            _btnLevels.Click += (_, __) => SetPage(0);
            _btnLyrics.Click += (_, __) => SetPage(4);
            _btnToggle.Click += (_, __) => SetPage(5);

            Controls.Add(_content);
            Controls.Add(_navBar);
            Controls.Add(_syncStatus);
            Controls.Add(_msg); _msg.BringToFront();

            BuildLyricsPage();
            _lyricsLoadingTimer.Tick += (_, __) => { if (_pageLyrics.Visible) _lyricsView.Invalidate(); };

            // setup charts e pagine
            _ok = InitCharts();
            if (!_ok)
            {
                SetLabelTextNoFlicker(_msg, L("LiveCharts non trovate: copia LiveChartsCore*, SkiaSharp* e HarfBuzzSharp vicino all'eseguibile.", "LiveCharts was not found: copy LiveChartsCore*, SkiaSharp* and HarfBuzzSharp next to the executable."));
                _msg.Visible = true;
            }

            // pagina iniziale
            SetPage(5);
            ApplyResponsiveLayout();
            ApplyThemePalette();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lyricsLoadingTimer.Stop();
                _lyricsLoadingTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private string L(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(_english ? english : italian);

        public void ApplyThemePalette()
        {
            Color surface = MeterBack;
            foreach (Control control in new Control[]
            {
                this, _navBar, _navGrid, _content, _pageLevels, _pageSpectrum,
                _pageLoud, _pageStereo, _pageLyrics, _cleanOverview, _msg,
                _nowPlayingHeader, _workspaceSubtitle, _qualityHeader, _syncStatus,
                _lyricsTitle, _lyricsStatus, _lyricsView
            }) control.BackColor = surface;
            foreach (var rail in _pageLevels.Controls.OfType<MusicAnalysisTabs>())
                rail.BackColor = MeterBack;
            _nowPlayingHeader.ForeColor = HUD.Theme.Text;
            _workspaceSubtitle.ForeColor = HUD.Theme.Muted;
            _qualityHeader.ForeColor = HUD.Theme.Muted;
            Invalidate(true);
        }

        public void SetLanguage(string? language)
        {
            bool english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
            _english = english;
            _btnLevels.Text = L("Analisi audio", "Audio analysis");
            _btnSpectrum.Text = L("Spettro", "Spectrum");
            _btnLoud.Text = "Loudness";
            _btnStereo.Text = "Stereo";
            _btnLyrics.Text = L("Testi", "Lyrics");
            _btnToggle.Text = L("Ascolto", "Now playing");
            _qualityHeader.Text = string.IsNullOrWhiteSpace(_trackQuality)
                ? L("ANALISI LIVE", "LIVE ANALYSIS")
                : _trackQuality.ToUpperInvariant();

            if (_lyricsStatus.Text is "In attesa del brano." or "Waiting for a track.")
            {
                _lyricsStatus.Text = L("In attesa del brano.", "Waiting for a track.");
                _lyricsView.SetPlainText(L("I testi compariranno qui quando disponibili.", "Lyrics will appear here when available."));
            }
            else if (_lyricsStatus.Text is "Cerco testi..." or "Searching for lyrics...")
            {
                _lyricsStatus.Text = L("Cerco testi...", "Searching for lyrics...");
            }

            try { SetProp(_specSeries!, "Name", L("Spettro (dBFS)", "Spectrum (dBFS)")); } catch { }
            // Intestazione del pannello (Panoramica / Testo) e strumenti: stessa lingua subito.
            _workspaceHeader.English = english;
            _workspaceHeader.Invalidate();
            _vuBoard.English = english;
            _vuBoard.Invalidate();
            _nowPlayingHeader.Text = _currentPage == 4 ? L("Testo", "Lyrics") : L("Panoramica", "Overview");
            _workspaceSubtitle.Text = L("Analisi audio in tempo reale", "Real-time audio analysis");
            try { BuildPlaybackInfoPage(_playbackInfoSection); } catch { }
            Invalidate(true);
        }

        public bool IsPointerOverAnalysisArea(Point screenPoint)
        {
            try
            {
                if (!Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                    return false;

                Point local = PointToClient(screenPoint);
                int top = 0;
                int bottomWakeBand = 24;
                int bottom = Math.Max(top, ClientSize.Height - bottomWakeBand);
                var analysisArea = new Rectangle(0, top, ClientSize.Width, Math.Max(0, bottom - top));
                return analysisArea.Contains(local);
            }
            catch
            {
                return false;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 8 && Height > 8)
            {
                // Stesso colore della pagina: niente Region arrotondata (bordi a scalini).
                var old = Region; Region = null; old?.Dispose();
            }
            ApplyResponsiveLayout();
            if (!_graphsEnabled)
                _placeholderOverlay.Invalidate();
        }

        private void ApplyResponsiveLayout()
        {
            try
            {
                bool compact = Width < 820 || Height < 560;
                _navBar.Height = (int)Math.Round(124 * DeviceDpi / 96f);
                
                _navBar.Padding = compact ? new Padding(8, 6, 0, 6) : new Padding(18, 10, 0, 9);
                if (_navGrid.Visible) CenterNavGrid(compact); else LayoutWorkspaceHeading();
                Padding p = compact ? new Padding(14, 8, 14, 28) : new Padding(28, 12, 28, 36);
                _pageLevels.Padding = _navGrid.Visible ? p : Padding.Empty;
                _pageSpectrum.Padding = p;
                _pageLoud.Padding = p;
                _pageStereo.Padding = p;
                _pageLyrics.Padding = p;
            }
            catch { }
        }

        private void CenterNavGrid(bool compact)
        {
            int available = Math.Max(1, _navBar.ClientSize.Width - _navBar.Padding.Horizontal);
            int preferred = compact ? 420 : 540;
            int width = Math.Min(preferred, available);
            int height = Math.Max(26, _navBar.ClientSize.Height - _navBar.Padding.Vertical);
            int x = (_navBar.ClientSize.Width - width) / 2;
            int y = _navBar.Padding.Top;
            var bounds = new Rectangle(x, y, width, height);
            if (_navGrid.Bounds != bounds)
                _navGrid.Bounds = bounds;

            // Titolo, artista e qualità sono già presenti nella pagina Metadata e nel
            // player: ripeterli ai due lati della barra rendeva l'interfaccia pesante.
            bool showTrackChrome = false;
            _nowPlayingHeader.Visible = showTrackChrome;
            _qualityHeader.Visible = showTrackChrome;
            if (showTrackChrome)
            {
                int sideW = Math.Max(180, Math.Min(300, (available - width) / 2 - 18));
                _nowPlayingHeader.Bounds = new Rectangle(_navBar.Padding.Left, y, sideW, height);
                _qualityHeader.Bounds = new Rectangle(_navBar.ClientSize.Width - _navBar.Padding.Right - sideW, y, sideW, height);
            }
        }

        private void BuildLyricsPage()
        {
            _pageLyrics.Controls.Clear();
            _pageLyrics.RowStyles.Clear();
            _pageLyrics.ColumnStyles.Clear();
            _pageLyrics.ColumnCount = 1;
            _pageLyrics.RowCount = 1;
            _pageLyrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _pageLyrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var host = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = MeterBack,
                Padding = new Padding(18)
            };
            host.Controls.Add(_lyricsView);
            host.Controls.Add(_lyricsStatus);
            _lyricsTitle.Visible = false;
            _lyricsStatus.Text = L("In attesa del brano.", "Waiting for a track.");
            _lyricsView.SetPlainText(L("I testi compariranno qui quando disponibili.", "Lyrics will appear here when available."));
            _pageLyrics.Controls.Add(host, 0, 0);
        }

        // ======= PAGE SWITCH =======
        private void EnsureContentPagesAdded()
        {
            if (_pageLevels.Parent == _content &&
                _pageSpectrum.Parent == _content &&
                _pageLoud.Parent == _content &&
                _pageStereo.Parent == _content &&
                _pageLyrics.Parent == _content &&
                _placeholderOverlay.Parent == _content)
                return;

            _content.SuspendLayout();
            try
            {
                _content.Controls.Clear();
                _pageLevels.Dock = DockStyle.Fill;
                _pageSpectrum.Dock = DockStyle.Fill;
                _pageLoud.Dock = DockStyle.Fill;
                _pageStereo.Dock = DockStyle.Fill;
                _pageLyrics.Dock = DockStyle.Fill;

                _content.Controls.Add(_pageLevels);
                _content.Controls.Add(_pageSpectrum);
                _content.Controls.Add(_pageLoud);
                _content.Controls.Add(_pageStereo);
                _content.Controls.Add(_pageLyrics);
                _content.Controls.Add(_placeholderOverlay);
            }
            finally
            {
                _content.ResumeLayout(true);
            }
        }

        private void SetPage(int index)
        {
            EnsureContentPagesAdded();

            _currentPage = index;
            _graphsEnabled = index != 5;
            _btnLevels.Selected = index == 0;
            _btnSpectrum.Selected = index == 1;
            _btnLoud.Selected = index == 2;
            _btnStereo.Selected = index == 3;
            _btnLyrics.Selected = index == 4;
            _btnToggle.Selected = index == 5;
            SetGraphTabButtonsEnabled(true);
            if (index == 0)
                BuildPlaybackInfoPage(_playbackInfoSection);

            _content.SuspendLayout();
            Control page = index switch
            {
                1 => _pageSpectrum,
                2 => _pageLoud,
                3 => _pageStereo,
                4 => _pageLyrics,
                5 => _placeholderOverlay,
                _ => _pageLevels
            };

            _pageLevels.Visible = ReferenceEquals(page, _pageLevels);
            _pageSpectrum.Visible = ReferenceEquals(page, _pageSpectrum);
            _pageLoud.Visible = ReferenceEquals(page, _pageLoud);
            _pageStereo.Visible = ReferenceEquals(page, _pageStereo);
            _pageLyrics.Visible = ReferenceEquals(page, _pageLyrics);
            _placeholderOverlay.Visible = ReferenceEquals(page, _placeholderOverlay);

            page.BringToFront();

            _content.ResumeLayout();

            if (ReferenceEquals(page, _pageLevels))
                RefreshCurrentPageCharts();
            _content.Invalidate(true);
        }

        private void SetPlaybackInfoSection(int section)
        {
            section = Math.Clamp(section, 0, 7);
            if (_playbackInfoSection == section && _currentPage == 0)
                return;
            // Da una voce all'altra (panoramica, forma d'onda, spettro...) in dissolvenza.
            try { if (Visible && IsHandleCreated) global::CinecorePlayer2025.HUD.ViewFade.Begin(RectangleToScreen(ClientRectangle)); } catch { }

            _playbackInfoSection = section;
            if (_currentPage != 0)
            {
                SetPage(0);
                return;
            }

            BuildPlaybackInfoPage(_playbackInfoSection);
            RefreshCurrentPageCharts();
            _content.Invalidate(true);
        }

        public void ShowWorkspace(bool lyrics)
        {
            if (_currentPage != (lyrics ? 4 : 0)) SetPage(lyrics ? 4 : 0);
            _lastVisualUtc = DateTime.MinValue;
            _navGrid.Visible = false;
            _nowPlayingHeader.Visible = true;
            _qualityHeader.Visible = !lyrics;
            _nowPlayingHeader.Text = lyrics ? L("Testo", "Lyrics") : L("Panoramica", "Overview");
            _syncStatus.Visible = lyrics;
            _workspaceSubtitle.Visible = !lyrics;
            _workspaceSubtitle.Text = L("Analisi audio in tempo reale", "Real-time audio analysis");
            _pageLevels.Padding = Padding.Empty;
            LayoutWorkspaceHeading();
        }

        private void LayoutWorkspaceHeading()
        {
            if (_navGrid.Visible) return;
            _nowPlayingHeader.Visible = _workspaceSubtitle.Visible = _qualityHeader.Visible = false;
            if (_workspaceHeader.Parent != _navBar) _navBar.Controls.Add(_workspaceHeader);
            _workspaceHeader.Dock = DockStyle.Fill;
            _workspaceHeader.Visible = true;
            _workspaceHeader.Lyrics = _currentPage == 4;
            _workspaceHeader.English = _english;
            _workspaceHeader.BringToFront();
            _workspaceHeader.Invalidate();
        }

        private void SetGraphTabButtonsEnabled(bool enabled)
        {
            _btnLevels.Enabled = true;
            _btnSpectrum.Enabled = true;
            _btnLoud.Enabled = true;
            _btnStereo.Enabled = true;
            _btnLyrics.Enabled = true;
            _btnLevels.Cursor = Cursors.Hand;
            _btnSpectrum.Cursor = Cursors.Hand;
            _btnLoud.Cursor = Cursors.Hand;
            _btnStereo.Cursor = Cursors.Hand;
            _btnLyrics.Cursor = Cursors.Hand;
        }

        // ===== Public API =====
        public void SetPlaceholderArtwork(string? artworkPath)
        {
            try { _placeholderOverlay.SetArtworkPath(artworkPath); } catch { }
            try { _artworkCard.SetArtworkPath(artworkPath); _workspaceHeader.SetArtwork(artworkPath); } catch { }
        }

        public void SetTrackPresentation(string? title, string? subtitle, string? quality)
        {
            if (!string.Equals(_trackTitle, title?.Trim(), StringComparison.Ordinal) ||
                !string.Equals(_trackSubtitle, subtitle?.Trim(), StringComparison.Ordinal))
                _cleanOverview.ResetTrack();
                _vuBoard.ResetTrack();
            _trackTitle = string.IsNullOrWhiteSpace(title) ? L("Statistiche audio", "Audio statistics") : title.Trim();
            _trackSubtitle = string.IsNullOrWhiteSpace(subtitle) ? string.Empty : subtitle.Trim();
            _trackQuality = string.IsNullOrWhiteSpace(quality) ? string.Empty : quality.Trim();
            _nowPlayingHeader.Text = _currentPage == 4 ? L("Testo", "Lyrics") : L("Panoramica", "Overview");
            _qualityHeader.Text = string.IsNullOrWhiteSpace(_trackQuality)
                ? L("ANALISI LIVE", "LIVE ANALYSIS")
                : _trackQuality.ToUpperInvariant();
            _workspaceHeader.SetTrack(_trackTitle, _trackSubtitle, _trackQuality);
            try { _artworkCard.SetText(_trackTitle, _trackSubtitle, _trackQuality); } catch { }
            try { _placeholderOverlay.SetText(_trackTitle, _trackSubtitle, _trackQuality); } catch { }
            if (_currentPage == 0 && _playbackInfoSection == 6)
            {
                try { BuildPlaybackInfoPage(6); } catch { }
            }
        }

        public void PrepareForPlaybackExit()
        {
            try
            {
                _lyricsLoadingTimer.Stop();
                _lyricsView.SetLoading(false);
                ResetLyricsSync();
                _lyricsStatus.Text = L("In attesa del brano.", "Waiting for a track.");
                _lyricsView.SetPlainText(L("I testi compariranno qui quando disponibili.", "Lyrics will appear here when available."));
                _graphsEnabled = false;
                SetPage(5);
                _btnToggle.Text = L("Ascolto", "Now playing");
                _btnToggle.Selected = true;
                SetTrackPresentation(L("Statistiche audio", "Audio statistics"), string.Empty, L("Analisi in tempo reale", "Live analysis"));
            }
            catch { }
        }

        public void ResumeAfterHostRestore()
        {
            try
            {
                // Il controllo può essere rimasto invisibile mentre il form era
                // minimizzato per il PiP. Sblocca il throttling e forza LiveCharts
                // a ricreare il frame con le dimensioni del player ripristinato.
                _lastVisualUtc = DateTime.MinValue;
                _graphsEnabled = true;

                object?[] charts =
                {
                    _vuChart, _scopeChart, _crestChart, _balanceChart, _corrChart1,
                    _specChart, _loudChart, _lraChart, _tpChart, _dynChart,
                    _widthChart, _corrChart2
                };

                foreach (object? chart in charts)
                {
                    if (chart is not Control control || control.IsDisposed)
                        continue;

                    control.Invalidate(true);
                }

                PerformLayout();
                Invalidate(true);
            }
            catch { }
        }

        public void SetLyricsLoading(string? title)
        {
            try
            {
                ResetLyricsSync();
                _lyricsStatus.Text = L("Cerco testi...", "Searching for lyrics...");
                _lyricsView.SetPlainText(string.Empty);
                _lyricsView.SetLoading(true);
                _lyricsLoadingTimer.Start();
            }
            catch { }
        }

        public void SetLyricsResult(
            string? title,
            string? status,
            string? lyrics,
            IReadOnlyList<MusicLyricsService.LyricsLine>? syncedLines = null)
        {
            try
            {
                _lyricsLoadingTimer.Stop();
                _lyricsView.SetLoading(false);
                ResetLyricsSync();
                _lyricsStatus.Text = string.IsNullOrWhiteSpace(status) ? L("Testi non disponibili.", "Lyrics unavailable.") : status;
                Dbg.Log($"[LYRICS-UI] result received: status='{_lyricsStatus.Text}', syncedLines={syncedLines?.Count ?? 0}, plainChars={lyrics?.Length ?? 0}", Dbg.LogLevel.Info);

                if (syncedLines != null && syncedLines.Count > 0)
                {
                    ConfigureSyncedLyrics(syncedLines);
                    return;
                }

                _lyricsView.SetPlainText(string.IsNullOrWhiteSpace(lyrics) ? L("Nessun testo trovato per questo brano.", "No lyrics were found for this track.") : lyrics);
            }
            catch { }
        }

        /// <summary>True while time-synced lyrics are on screen and worth animating per frame.</summary>
        public bool WantsLyricsFrames
        {
            get
            {
                try { return _syncedLyrics is { Count: > 0 } && Visible && _lyricsView.Visible && _lyricsView.Width > 0; }
                catch { return false; }
            }
        }

        public void UpdateLyricsPosition(double positionSeconds)
        {
            try
            {
                var lines = _syncedLyrics;
                if (lines == null || lines.Count == 0)
                    return;

                double position = Math.Max(0, positionSeconds);
                int index = FindSyncedLyricsIndex(lines, position);
                int wordIndex = index >= 0 && index < lines.Count
                    ? FindSyncedWordIndex(lines[index], position)
                    : -1;
                bool changed = index != _lastSyncedLyricsIndex || wordIndex != _lastSyncedWordIndex;

                _lastSyncedLyricsIndex = index;
                _lastSyncedWordIndex = wordIndex;
                _lyricsView.SetActiveIndex(index);
                _lyricsView.SetPlaybackPosition(position);

                if (changed && index >= 0)
                {
                    var line = lines[index];
                    if (wordIndex >= 0 && line.Words != null && wordIndex < line.Words.Count)
                    {
                        var word = line.Words[wordIndex];
                        Dbg.Log($"[LYRICS-UI] playback={position:0.000}s line={index}/{lines.Count} word={wordIndex}/{line.Words.Count} '{word.Text}' {word.StartSeconds:0.000}-{word.EndSeconds:0.000}s", Dbg.LogLevel.Info);
                    }
                    else
                    {
                        Dbg.Log($"[LYRICS-UI] playback={position:0.000}s line={index}/{lines.Count} word=n/a", Dbg.LogLevel.Info);
                    }
                }
            }
            catch { }
        }

        private void ResetLyricsSync()
        {
            _syncedLyrics = null;
            _lastSyncedLyricsIndex = -1;
            _lastSyncedWordIndex = -2;
            _lyricsView.SetSyncedLines(Array.Empty<MusicLyricsService.LyricsLine>());
            _lyricsView.SetPlaybackPosition(0);
        }

        private void ConfigureSyncedLyrics(IReadOnlyList<MusicLyricsService.LyricsLine> lines)
        {
            var usable = lines
                .Where(x => double.IsFinite(x.TimeSeconds) && x.TimeSeconds >= 0)
                .OrderBy(x => x.TimeSeconds)
                .ToList();

            if (usable.Count == 0)
            {
                _lyricsView.SetPlainText(L("Nessun testo sincronizzato trovato per questo brano.", "No synchronized lyrics were found for this track."));
                return;
            }

            _syncedLyrics = usable;
            _lastSyncedLyricsIndex = -1;
            _lastSyncedWordIndex = -2;
            int timedWords = usable.Sum(x => x.Words?.Count ?? 0);
            Dbg.Log($"[LYRICS-UI] consuming synchronized objects: lines={usable.Count}, timedWords={timedWords}, first={usable[0].TimeSeconds:0.000}s, last={usable[^1].TimeSeconds:0.000}s", Dbg.LogLevel.Info);
            _lyricsView.SetSyncedLines(usable);
            _lyricsView.SetActiveIndex(-1);
            _lyricsView.SetPlaybackPosition(0);
        }

        private static int FindSyncedWordIndex(MusicLyricsService.LyricsLine line, double positionSeconds)
        {
            if (line.Words == null || line.Words.Count == 0)
                return -1;

            int best = -1;
            for (int i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                if (positionSeconds + 0.001 < word.StartSeconds)
                    break;
                best = i;
                if (positionSeconds <= word.EndSeconds)
                    break;
            }
            return best;
        }

        private static int FindSyncedLyricsIndex(IReadOnlyList<MusicLyricsService.LyricsLine> lines, double positionSeconds)
        {
            int lo = 0;
            int hi = lines.Count - 1;
            int best = -1;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (lines[mid].TimeSeconds <= positionSeconds)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return best;
        }

        private sealed class LyricsDisplay : Control
        {
            private readonly Font _plainFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 13f, FontStyle.Regular);
            private readonly Font _syncedFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 12.5f, FontStyle.Regular);
            private readonly Font _activeFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 18f, FontStyle.Regular);
            private IReadOnlyList<MusicLyricsService.LyricsLine> _syncedLines = Array.Empty<MusicLyricsService.LyricsLine>();
            private string _plainText = string.Empty;
            private bool _loading;
            private int _activeIndex = -1;
            private double _playbackPositionSeconds;

            public LyricsDisplay()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw, true);
                DoubleBuffered = true;
                TabStop = false;
            }

            public void SetPlainText(string? text)
            {
                _plainText = NormalizeText(text);
                _syncedLines = Array.Empty<MusicLyricsService.LyricsLine>();
                _activeIndex = -1;
                Invalidate();
            }

            public void SetLoading(bool loading)
            {
                if (_loading == loading) return;
                _loading = loading;
                Invalidate();
            }

            public void SetSyncedLines(IReadOnlyList<MusicLyricsService.LyricsLine>? lines)
            {
                _plainText = string.Empty;
                _syncedLines = lines ?? Array.Empty<MusicLyricsService.LyricsLine>();
                _activeIndex = -1;
                Invalidate();
            }

            public void SetActiveIndex(int index)
            {
                if (_syncedLines.Count == 0)
                    return;

                int clamped = Math.Clamp(index, -1, _syncedLines.Count - 1);
                if (clamped == _activeIndex)
                    return;

                _activeIndex = clamped;
                Invalidate();
            }

            public void SetPlaybackPosition(double positionSeconds)
            {
                double clamped = Math.Max(0, positionSeconds);
                if (Math.Abs(clamped - _playbackPositionSeconds) < 0.025)
                    return;

                _playbackPositionSeconds = clamped;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.Clear(MeterBack);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                var bounds = ClientRectangle;
                bounds.Inflate(-22, -18);
                if (bounds.Width <= 8 || bounds.Height <= 8)
                    return;

                if (_loading)
                {
                    int dot = Math.Max(6, (int)Math.Round(8 * DeviceDpi / 96f));
                    int gap = Math.Max(10, (int)Math.Round(16 * DeviceDpi / 96f));
                    int total = dot * 3 + gap * 2;
                    int x = bounds.Left + (bounds.Width - total) / 2;
                    int y = bounds.Top + (bounds.Height - dot) / 2;
                    int phase = (int)((Environment.TickCount64 / 140) % 3);
                    for (int i = 0; i < 3; i++)
                    {
                        using var brush = new SolidBrush(Color.FromArgb(i == phase ? 235 : 75, 54, 151, 255));
                        g.FillEllipse(brush, x + i * (dot + gap), y, dot, dot);
                    }
                }
                else if (_syncedLines.Count > 0)
                    DrawSyncedLyrics(g, bounds);
                else
                    DrawPlainLyrics(g, bounds);
            }

            private void DrawPlainLyrics(Graphics g, Rectangle bounds)
            {
                string text = string.IsNullOrWhiteSpace(_plainText) ? string.Empty : _plainText;
                using var brush = new SolidBrush(Color.Gainsboro);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisWord,
                    FormatFlags = StringFormatFlags.LineLimit
                };
                g.DrawString(text, _plainFont, brush, bounds, format);
            }

            private void DrawSyncedLyrics(Graphics g, Rectangle bounds)
            {
                int active = Math.Clamp(_activeIndex < 0 ? 0 : _activeIndex, 0, _syncedLines.Count - 1);
                int first = Math.Max(0, active - 3);
                int last = Math.Min(_syncedLines.Count - 1, active + 3);
                float centerY = bounds.Top + (bounds.Height / 2f);

                for (int i = first; i <= last; i++)
                {
                    int distance = i - active;
                    bool isActive = distance == 0 && _activeIndex >= 0;
                    var font = isActive ? _activeFont : _syncedFont;
                    float rowHeight = isActive ? 78f : 42f;
                    float y = centerY + (distance * 46f) - (rowHeight / 2f);
                    var lineBounds = new RectangleF(bounds.Left, y, bounds.Width, rowHeight);

                    int alpha = isActive ? 255 : Math.Max(55, 175 - (Math.Abs(distance) * 36));
                    Color baseColor = isActive ? Color.White : Color.FromArgb(185, 185, 192);
                    using var brush = new SolidBrush(Color.FromArgb(alpha, baseColor));
                    using var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisWord,
                        FormatFlags = StringFormatFlags.LineLimit
                    };

                    string text = _syncedLines[i].Text.Trim();
                    var words = _syncedLines[i].Words;
                    if (isActive && words != null && words.Count > 0)
                    {
                        DrawActiveLineWithWordProgress(g, lineBounds, text, words, _playbackPositionSeconds);
                    }
                    else
                    {
                        g.DrawString(text, font, brush, lineBounds, format);
                    }
                }
            }

            private void DrawActiveLineWithWordProgress(
                Graphics g,
                RectangleF bounds,
                string text,
                IReadOnlyList<MusicLyricsService.LyricsWord> words,
                double positionSeconds)
            {
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisWord,
                    FormatFlags = StringFormatFlags.LineLimit
                };
                using var futureBrush = new SolidBrush(Color.FromArgb(255, 214, 220, 232));
                using var sungBrush = new SolidBrush(Color.FromArgb(255, 82, 224, 255));

                SizeF measured = g.MeasureString(text, _activeFont, new SizeF(bounds.Width, bounds.Height), format);
                float drawWidth = Math.Min(bounds.Width, measured.Width + 2);
                float x = bounds.Left + ((bounds.Width - drawWidth) / 2f);
                var drawBounds = new RectangleF(x, bounds.Top, drawWidth, bounds.Height);
                g.DrawString(text, _activeFont, futureBrush, drawBounds, format);

                double totalUnits = words.Sum(word => Math.Max(1, word.Text?.Length ?? 0));
                double completedUnits = 0;
                double progress = 0;
                foreach (var word in words)
                {
                    double units = Math.Max(1, word.Text?.Length ?? 0);
                    if (positionSeconds >= word.EndSeconds)
                    {
                        completedUnits += units;
                        continue;
                    }

                    if (positionSeconds > word.StartSeconds && word.EndSeconds > word.StartSeconds)
                        completedUnits += units * Math.Clamp((positionSeconds - word.StartSeconds) / (word.EndSeconds - word.StartSeconds), 0, 1);
                    break;
                }
                progress = totalUnits <= 0 ? 0 : Math.Clamp(completedUnits / totalUnits, 0, 1);
                if (progress <= 0)
                    return;

                var oldClip = g.Clip;
                try
                {
                    g.SetClip(new RectangleF(drawBounds.Left, drawBounds.Top, drawBounds.Width * (float)progress, drawBounds.Height));
                    g.DrawString(text, _activeFont, sungBrush, drawBounds, format);
                }
                finally
                {
                    g.Clip = oldClip;
                }
            }

            private static string NormalizeText(string? text)
                => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _plainFont.Dispose();
                    _syncedFont.Dispose();
                    _activeFont.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        public void Update(LoopbackSampler.AudioMetrics m)
        {
            // Gli strumenti ricevono ogni misura: col limite a 33 ms e il timer a 33 ms
            // (quantizzato a ~31 ms) un aggiornamento su due veniva scartato e gli aghi scattavano.
            _vuBoard.UpdateMetrics(m);
            var nowUtc = DateTime.UtcNow;
            if (_lastVisualUtc != DateTime.MinValue &&
                (nowUtc - _lastVisualUtc).TotalMilliseconds < VISUAL_INTERVAL_MS)
                return;
            _lastVisualUtc = nowUtc;
            _cleanOverview.UpdateMetrics(m);
            if (!_ok || _currentPage is 0 or 4) return;

            // --- VU ---
            double dBL = ToDb(m.RmsL), dBR = ToDb(m.RmsR);
            double pHL = ToDb(m.PeakHoldL), pHR = ToDb(m.PeakHoldR);
            double correlation = Math.Clamp(SafeFinite(m.Correlation, 0), -1.0, 1.0);
            bool sil = m.IsSilent;

            double rmsLVis = sil ? -90 : Math.Clamp(dBL, -90, 0);
            double rmsRVis = sil ? -90 : Math.Clamp(dBR, -90, 0);

            // --- Scope ---
            if (m.ScopeL.Length > 0)
            {
                SetProp(_scopeLSeries!, "Values", m.ScopeL.Select(v => Math.Clamp(SafeFinite(v, 0), -1.0, 1.0)).ToArray());
                SetProp(_scopeRSeries!, "Values", m.ScopeR.Select(v => Math.Clamp(SafeFinite(v, 0), -1.0, 1.0)).ToArray());

                double maxAbs = Math.Max(
                    m.ScopeL.Length > 0 ? m.ScopeL.Select(v => Math.Abs((double)v)).Max() : 0.0,
                    m.ScopeR.Length > 0 ? m.ScopeR.Select(v => Math.Abs((double)v)).Max() : 0.0);
                double targetHalf = maxAbs <= .45 ? .5 : 1.0;
                // Allarga immediatamente l'asse sui nuovi picchi; soltanto il
                // restringimento resta smussato. In questo modo l'onda non supera
                // mai il riquadro mentre il range rincorre il segnale.
                _scVisAmp = targetHalf > _scVisAmp
                    ? targetHalf
                    : 0.92 * _scVisAmp + 0.08 * targetHalf;
                TrySet(_yAxisSc!, "MinLimit", -_scVisAmp);
                TrySet(_yAxisSc!, "MaxLimit", +_scVisAmp);
            }

            // --- Crest ---
            double crestLvis = m.IsSilent ? 0.0 : Math.Clamp(m.CrestL_dB, 0.0, 24.0);
            double crestRvis = m.IsSilent ? 0.0 : Math.Clamp(m.CrestR_dB, 0.0, 24.0);

            // --- Balance ---
            double balPct = Math.Clamp(SafeFinite(m.Balance, 0) * 100.0, -100.0, 100.0);
            PushHistory(_rmsLHist, rmsLVis);
            PushHistory(_rmsRHist, rmsRVis);
            PushHistory(_crestLHist, crestLvis);
            PushHistory(_crestRHist, crestRvis);
            PushHistory(_balanceHist, balPct);
            SetProp(_vuRmsSeries!, "Values", _rmsLHist.ToArray());
            SetProp(_vuPkSeries!, "Values", _rmsRHist.ToArray());
            SetProp(_crestSeries!, "Values", _crestLHist.ToArray());
            SetProp(_crestRSeries!, "Values", _crestRHist.ToArray());
            SetProp(_balanceSeries!, "Values", _balanceHist.ToArray());
            SetLabelTextNoFlicker(_badgesLevels,
                $"RMS L/R: {dBL:0.0} / {dBR:0.0} dBFS   {L("Picco mantenuto", "Peak hold")}: {pHL:0.0} / {pHR:0.0} dBFS   {L("Bilanciamento", "Balance")}: {balPct:+0.0;-0.0;0.0}%");
            SetLabelTextNoFlicker(_badgesDynamics,
                $"{L("Cresta", "Crest")} L/R: {crestLvis:0.0} / {crestRvis:0.0} dB   {L("Correlazione", "Correlation")}: {correlation:0.00}   Width: {SafeFinite(m.WidthDb, double.NaN):0.0} dB");

            // --- Correlazione (storico) su 2 pagine ---
            _corrHist[_corrW] = correlation;
            _corrW = (_corrW + 1) % _corrHist.Length;
            var corrVals = new double[_corrHist.Length];
            for (int i = 0; i < corrVals.Length; i++) corrVals[i] = _corrHist[(_corrW + i) % _corrHist.Length];
            SetProp(_corrSeries1!, "Values", corrVals);
            SetProp(_corrSeries2!, "Values", corrVals);

            // --- Spectrum ---
            if (m.SpectrumDb.Length > 0)
            {
                var binsAll = m.SpectrumDb.Length;
                int sourceFft = m.FftLength > 0 ? m.FftLength : (binsAll - 1) * 2;
                var binsDraw = Math.Max(1, Math.Min(binsAll - 1, m.SampleRate > 0 ? (int)Math.Ceiling(20000d * sourceFft / m.SampleRate) + 1 : binsAll - 1));
                var specDraw = new double[binsDraw];
                for (int i = 0; i < binsDraw; i++)
                    specDraw[i] = Math.Clamp(SafeFinite(m.SpectrumDb[i], -120), -120, 0);
                SetProp(_specSeries!, "Values", specDraw);

                if (m.SampleRate > 0)
                {
                    int fftN = (m.FftLength > 0) ? m.FftLength : (binsAll - 1) * 2;
                    if (_lastSpectrumSr != m.SampleRate || _lastSpectrumBins != binsDraw || _lastFftLen != fftN)
                    {
                        _lastSpectrumSr = m.SampleRate;
                        _lastSpectrumBins = binsDraw;
                        _lastFftLen = fftN;

                        TrySet(_xAxisSp!, "MinLimit", 0d);
                        TrySet(_xAxisSp!, "MaxLimit", (double)(binsDraw - 1));
                        TrySet(_xAxisSp!, "MinStep", Math.Max(1d, binsDraw / 6d));
                        TrySet(_xAxisSp!, "ForceStepToMin", true);
                        TrySet(_xAxisSp!, "CustomSeparators", Enumerable.Range(0, 6).Select(i => i * (binsDraw - 1) / 6d).ToArray());
                        double hzStep = m.SampleRate / (double)fftN;

                        TrySetLabeler(_xAxisSp!, x =>
                        {
                            double k = Math.Max(0, x);
                            double hz = k * hzStep;
                            return hz >= 1000 ? (hz / 1000.0).ToString("0.#") + " kHz" : hz.ToString("0") + " Hz";
                        });
                    }
                }

                double minDb = specDraw.Min();
                double targetMin = Math.Max(-120, Math.Min(-10, Math.Floor(minDb / 5.0) * 5.0));
                _spYMin = 0.85 * _spYMin + 0.15 * targetMin;
                TrySet(_yAxisSp!, "MinLimit", _spYMin);
                TrySet(_yAxisSp!, "MaxLimit", 0d);
                TrySet(_specSeries!, "Pivot", -120d);
            }

            // --- Loudness & Peaks ---
            {
                double mL = SafeFinite(m.LufsM, double.NegativeInfinity);
                double sL = SafeFinite(m.LufsS, double.NegativeInfinity);
                double iL = SafeFinite(m.LufsI, double.NegativeInfinity);

                double loudMVis = double.IsNegativeInfinity(mL) ? -70 : Math.Clamp(mL, -70, 0);
                double loudSVis = double.IsNegativeInfinity(sL) ? -70 : Math.Clamp(sL, -70, 0);
                double loudIVis = double.IsNegativeInfinity(iL) ? -70 : Math.Clamp(iL, -70, 0);
                PushHistory(_loudMHist, loudMVis);
                PushHistory(_loudSHist, loudSVis);
                PushHistory(_loudIHist, loudIVis);
                SetProp(_loudSeries!, "Values", _loudMHist.ToArray());
                SetProp(_loudSSeries!, "Values", _loudSHist.ToArray());
                SetProp(_loudISeries!, "Values", _loudIHist.ToArray());

                double lra = Math.Max(0, Math.Min(30, SafeFinite(m.Lra, 0)));
                PushHistory(_lraHist, lra);
                SetProp(_lraSeries!, "Values", _lraHist.ToArray());

                double tpL = SafeFinite(m.DbTpL, -120);
                double tpR = SafeFinite(m.DbTpR, -120);
                double minTp = Math.Min(tpL, tpR);
                double axisMin = (minTp < -6) ? Math.Floor(minTp / 3.0) * 3.0 : -6.0;
                TrySet(_yAxisTp!, "MinLimit", axisMin);
                double axisMax = Math.Clamp(Math.Ceiling(Math.Max(0, Math.Max(tpL, tpR))), 0, 6);
                TrySet(_yAxisTp!, "MaxLimit", axisMax);
                PushHistory(_tpLHist, Math.Clamp(tpL, -60, 6));
                PushHistory(_tpRHist, Math.Clamp(tpR, -60, 6));
                SetProp(_tpSeries!, "Values", _tpLHist.ToArray());
                SetProp(_tpRSeries!, "Values", _tpRHist.ToArray());
                double histTpMin = Math.Min(_tpLHist.Min(), _tpRHist.Min());
                axisMin = Math.Min(axisMin, Math.Floor(histTpMin / 3.0) * 3.0);
                TrySet(_yAxisTp!, "MinLimit", Math.Max(-60, axisMin));
                double histTpMax = Math.Max(_tpLHist.Max(), _tpRHist.Max());
                TrySet(_yAxisTp!, "MaxLimit", Math.Clamp(Math.Ceiling(Math.Max(0, histTpMax)), 0, 6));

                double psr = Math.Max(0, Math.Min(30, SafeFinite(m.PsrDb, 0)));
                double plr = Math.Max(0, Math.Min(30, SafeFinite(m.PlrDb, 0)));
                PushHistory(_psrHist, psr);
                PushHistory(_plrHist, plr);
                SetProp(_dynSeries!, "Values", _psrHist.ToArray());
                SetProp(_dynPlrSeries!, "Values", _plrHist.ToArray());

                SetLabelTextNoFlicker(_badgesLoud, $"M: {FmtLufs(m.LufsM)}   S: {FmtLufs(m.LufsS)}   I: {FmtLufs(m.LufsI)}");
                SetLabelTextNoFlicker(_badgesPeaks, $"LRA: {m.Lra:0.0} LU   dBTP L/R: {SafeFinite(m.DbTpL, double.NaN):0.0} / {SafeFinite(m.DbTpR, double.NaN):0.0}   " +
                                                    $"{L("Clip", "Clips")} TP: {m.ClipEvents}   {L("Clip", "Clips")} SP: {m.ClipSampleEvents}   " +
                                                    $"PSR: {SafeFinite(m.PsrDb, double.NaN):0.0} dB   PLR: {SafeFinite(m.PlrDb, double.NaN):0.0} dB");
            }

            // --- Stereo/Diag badges (no flicker) ---
            SetLabelTextNoFlicker(_badgesStereo, $"ρ: {correlation:0.00}   Width: {SafeFinite(m.WidthDb, double.NaN):0.0} dB");
            SetLabelTextNoFlicker(_badgesDiag,
                $"DC L/R: {SafeFinite(m.DcL, double.NaN):+0.000;-0.000;0.000} / {SafeFinite(m.DcR, double.NaN):+0.000;-0.000;0.000}   " +
                $"{L("Dominante", "Dominant")}: {SafeFinite(m.DominantHz, 0):0.#} Hz   {L("Centroide", "Centroid")}: {SafeFinite(m.SpectralCentroidHz, 0):0.#} Hz   " +
                $"Roll-off95: {SafeFinite(m.SpectralRollOffHz, 0):0.#} Hz   {L("Rumore di fondo", "Noise floor")}: {SafeFinite(m.NoiseFloorDb, double.NaN):0.0} dBFS   " +
                $"SNR: {SafeFinite(m.SnrDb, double.NaN):0.0} dB   ENOB: {SafeFinite(m.EnobBits, double.NaN):0.0} bit");

            // --- Width (asse adattivo) ---
            {
                double widthDb = SafeFinite(m.WidthDb, 0);
                widthDb = Math.Max(-30, Math.Min(+30, widthDb));
                PushHistory(_widthHist, widthDb);
                SetProp(_widthSeries!, "Values", _widthHist.ToArray());

                double histAbs = _widthHist.Select(v => Math.Abs(SafeFinite(v, 0))).DefaultIfEmpty(0).Max();
                double target = Math.Clamp(Math.Max(Math.Abs(widthDb), histAbs) * 1.35 + 2.0, 12.0, 36.0); // half-range
                _widthVis = 0.82 * _widthVis + 0.18 * target;
                TrySet(_yAxisWidth!, "MinLimit", -_widthVis);
                TrySet(_yAxisWidth!, "MaxLimit", +_widthVis);
            }

            // --- Spectrum badges (no flicker) ---
            SetLabelTextNoFlicker(_badgesSpectrum,
                $"DC off L/R: {SafeFinite(m.DcL, double.NaN):+0.000;-0.000;0.000} / {SafeFinite(m.DcR, double.NaN):+0.000;-0.000;0.000}   " +
                $"{L("Dominante", "Dominant")}: {SafeFinite(m.DominantHz, 0):0.#} Hz   {L("Centroide", "Centroid")}: {SafeFinite(m.SpectralCentroidHz, 0):0.#} Hz   " +
                $"Roll-off95: {SafeFinite(m.SpectralRollOffHz, 0):0.#} Hz");

            // Messaggio info (no flicker)
            SetLabelTextNoFlicker(_msg, m.IsSilent ? L("Silenzio / fondo", "Silence / floor") : "");
            _msg.Visible = m.IsSilent;

            RefreshCurrentPageCharts();
        }

        public void UpdateLevels(float rmsL, float rmsR, float peakHoldL, float peakHoldR, double[]? spectrumDb)
        {
            var m = new LoopbackSampler.AudioMetrics
            {
                RmsL = rmsL,
                RmsR = rmsR,
                PeakHoldL = peakHoldL,
                PeakHoldR = peakHoldR,
                PeakL = peakHoldL,
                PeakR = peakHoldR,
                SpectrumDb = spectrumDb ?? Array.Empty<double>(),
                Balance = (rmsL + rmsR) > 1e-9 ? (rmsR - rmsL) / (rmsL + rmsR) : 0.0,
                Correlation = 0.0,
                CrestL_dB = 0.0,
                CrestR_dB = 0.0,
                ScopeL = Array.Empty<float>(),
                ScopeR = Array.Empty<float>(),
                SampleRate = 0,
                FftLength = 0,
                IsSilent = (20.0 * Math.Log10(Math.Max(rmsL, rmsR) + 1e-12)) < -65.0
            };
            Update(m);
        }

        public void SetInfoMessage(string? msg)
        {
            SetLabelTextNoFlicker(_msg, string.IsNullOrWhiteSpace(msg) ? "" : msg);
            _msg.Visible = !string.IsNullOrWhiteSpace(msg);
            if (_msg.Visible) _msg.BringToFront();
        }

        // ===== Setup grafici & layout =====
        private bool InitCharts()
        {
            try
            {
                // Palette scura
                object GridPaint() => MakePaint(_solidPaintT!, 111, 141, 159, 24, 1.0);
                object AxisPaint() => MakePaint(_solidPaintT!, 214, 220, 226, 156);
                var vuLineL = MakePaint(_solidPaintT!, 15, 160, 255, 220, 1.2);
                var vuLineR = MakePaint(_solidPaintT!, 255, 120, 60, 220, 1.2);
                var spLine = MakePaint(_solidPaintT!, 0, 180, 255, 220, 1.2);
                var spFill = MakePaint(_solidPaintT!, 0, 180, 255, 18);
                var scLLine = MakePaint(_solidPaintT!, 0, 200, 255, 220, 1.1);
                var scRLine = MakePaint(_solidPaintT!, 255, 120, 60, 220, 1.1);
                var crestFi = MakePaint(_solidPaintT!, 40, 220, 140, 220, 1.2);
                var crestRLine = MakePaint(_solidPaintT!, 255, 180, 80, 220, 1.2);
                var balFill = MakePaint(_solidPaintT!, 200, 120, 255, 220, 1.2);
                var corrLin = MakePaint(_solidPaintT!, 0, 220, 120, 220, 1.2);
                var loudFi = MakePaint(_solidPaintT!, 0, 200, 200, 220, 1.2);
                var loudSLine = MakePaint(_solidPaintT!, 80, 180, 255, 220, 1.2);
                var loudILine = MakePaint(_solidPaintT!, 210, 210, 210, 210, 2.0);
                var lraFi = MakePaint(_solidPaintT!, 120, 200, 120, 220, 1.2);
                var tpFi = MakePaint(_solidPaintT!, 255, 160, 80, 220, 1.2);
                var tpRLine = MakePaint(_solidPaintT!, 255, 220, 0, 220, 1.2);
                var dynFi = MakePaint(_solidPaintT!, 100, 180, 255, 220, 1.2);
                var dynPlrLine = MakePaint(_solidPaintT!, 180, 150, 255, 220, 1.2);
                var widthFi = MakePaint(_solidPaintT!, 120, 220, 180, 220, 1.2);

                Control MakeChartHost(object chart, string title) => MakeTitledPanel(title, (Control)chart);

                void PrepChart(object chartControl)
                {
                    var c = (Control)chartControl;
                    c.BackColor = PanelBack;
                    c.Margin = new Padding(0);
                    c.Padding = new Padding(0);
                    c.Dock = DockStyle.Fill;
                    TrySet(chartControl, "DrawMarginFrame", null);
                    TrySet(chartControl, "Background", null);
                    c.GetType().GetProperty("BorderStyle")?.SetValue(c, BorderStyle.None);
                }

                // ===== PAGE 1: LEVELS =====
                _vuChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_vuChart);
                TrySetEnum(_vuChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_vuChart, "TooltipPosition", _tipPosT!, "Top");
                _vuRmsSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_vuRmsSeries, "Name", "RMS L");
                SetProp(_vuRmsSeries, "Values", _rmsLHist);
                TrySet(_vuRmsSeries, "GeometrySize", 0d);
                TrySet(_vuRmsSeries, "LineSmoothness", 0.2d);
                TrySet(_vuRmsSeries, "Stroke", vuLineL);
                TrySet(_vuRmsSeries, "Fill", null);
                _vuPkSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_vuPkSeries, "Name", "RMS R");
                SetProp(_vuPkSeries, "Values", _rmsRHist);
                TrySet(_vuPkSeries, "GeometrySize", 0d);
                TrySet(_vuPkSeries, "LineSmoothness", 0.2d);
                TrySet(_vuPkSeries, "Stroke", vuLineR);
                TrySet(_vuPkSeries, "Fill", null);
                _xAxisVu = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisVu, "LabelsPaint", null);
                TrySet(_xAxisVu, "SeparatorsPaint", null);
                TrySet(_xAxisVu, "TicksPaint", null);
                _yAxisVu = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisVu, "MinLimit", -70d); SetProp(_yAxisVu, "MaxLimit", 0d);
                TrySetLabeler(_yAxisVu, v => v.ToString("0") + " dB");
                TrySet(_yAxisVu, "LabelsPaint", AxisPaint());
                TrySet(_yAxisVu, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisVu, "TicksPaint", null);
                SetProp(_vuChart, "Series", CreateArray(_iSeriesT!, _vuRmsSeries, _vuPkSeries));
                SetProp(_vuChart, "XAxes", CreateArray(_axisT!, _xAxisVu));
                SetProp(_vuChart, "YAxes", CreateArray(_axisT!, _yAxisVu));

                _scopeChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_scopeChart);
                TrySetEnum(_scopeChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_scopeChart, "TooltipPosition", _tipPosT!, "Top");
                _scopeLSeries = Activator.CreateInstance(_lineSeriesT!)!;
                _scopeRSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_scopeLSeries, "Name", "L");
                SetProp(_scopeRSeries, "Name", "R");
                TrySet(_scopeLSeries, "GeometrySize", 0d);
                TrySet(_scopeRSeries, "GeometrySize", 0d);
                TrySet(_scopeLSeries, "LineSmoothness", 0d);
                TrySet(_scopeRSeries, "LineSmoothness", 0d);
                TrySet(_scopeLSeries, "Stroke", scLLine);
                TrySet(_scopeRSeries, "Stroke", scRLine);
                TrySet(_scopeLSeries, "Fill", null);
                TrySet(_scopeRSeries, "Fill", null);
                _xAxisSc = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisSc, "LabelsPaint", null);
                TrySet(_xAxisSc, "SeparatorsPaint", null);
                TrySet(_xAxisSc, "TicksPaint", null);
                _yAxisSc = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisSc, "MinLimit", -0.5d); SetProp(_yAxisSc, "MaxLimit", +0.5d);
                TrySet(_yAxisSc, "LabelsPaint", AxisPaint());
                TrySet(_yAxisSc, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisSc, "TicksPaint", null);
                SetProp(_scopeChart, "Series", CreateArray(_iSeriesT!, _scopeLSeries, _scopeRSeries));
                SetProp(_scopeChart, "XAxes", CreateArray(_axisT!, _xAxisSc));
                SetProp(_scopeChart, "YAxes", CreateArray(_axisT!, _yAxisSc));

                _crestChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_crestChart);
                TrySetEnum(_crestChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_crestChart, "TooltipPosition", _tipPosT!, "Top");
                _crestSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_crestSeries, "Name", "Crest L");
                SetProp(_crestSeries, "Values", _crestLHist);
                TrySet(_crestSeries, "GeometrySize", 0d);
                TrySet(_crestSeries, "LineSmoothness", 0.25d);
                TrySet(_crestSeries, "Stroke", crestFi);
                TrySet(_crestSeries, "Fill", null);
                _crestRSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_crestRSeries, "Name", "Crest R");
                SetProp(_crestRSeries, "Values", _crestRHist);
                TrySet(_crestRSeries, "GeometrySize", 0d);
                TrySet(_crestRSeries, "LineSmoothness", 0.25d);
                TrySet(_crestRSeries, "Stroke", crestRLine);
                TrySet(_crestRSeries, "Fill", null);
                _xAxisCr = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisCr, "LabelsPaint", null);
                TrySet(_xAxisCr, "SeparatorsPaint", null);
                TrySet(_xAxisCr, "TicksPaint", null);
                _yAxisCr = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisCr, "MinLimit", 0d); SetProp(_yAxisCr, "MaxLimit", 24d);
                TrySet(_yAxisCr, "LabelsPaint", AxisPaint());
                TrySet(_yAxisCr, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisCr, "TicksPaint", null);
                SetProp(_crestChart, "Series", CreateArray(_iSeriesT!, _crestSeries, _crestRSeries));
                SetProp(_crestChart, "XAxes", CreateArray(_axisT!, _xAxisCr));
                SetProp(_crestChart, "YAxes", CreateArray(_axisT!, _yAxisCr));

                _balanceChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_balanceChart);
                TrySetEnum(_balanceChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_balanceChart, "TooltipPosition", _tipPosT!, "Top");
                _balanceSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_balanceSeries, "Name", "Balance (%)");
                SetProp(_balanceSeries, "Values", _balanceHist);
                TrySet(_balanceSeries, "GeometrySize", 0d);
                TrySet(_balanceSeries, "LineSmoothness", 0.25d);
                TrySet(_balanceSeries, "Stroke", balFill);
                TrySet(_balanceSeries, "Fill", null);
                _xAxisBal = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisBal, "LabelsPaint", null);
                TrySet(_xAxisBal, "SeparatorsPaint", null);
                TrySet(_xAxisBal, "TicksPaint", null);
                _yAxisBal = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisBal, "MinLimit", -12d); SetProp(_yAxisBal, "MaxLimit", +12d);
                TrySet(_yAxisBal, "LabelsPaint", AxisPaint());
                TrySet(_yAxisBal, "SeparatorsPaint", GridPaint());
                TrySetLabeler(_yAxisBal, v => v.ToString("+0;-0;0") + "%");
                SetProp(_balanceChart, "Series", CreateArray(_iSeriesT!, _balanceSeries));
                SetProp(_balanceChart, "XAxes", CreateArray(_axisT!, _xAxisBal));
                SetProp(_balanceChart, "YAxes", CreateArray(_axisT!, _yAxisBal));

                _corrChart1 = Activator.CreateInstance(_chartT!)!;
                PrepChart(_corrChart1);
                TrySetEnum(_corrChart1, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_corrChart1, "TooltipPosition", _tipPosT!, "Top");
                _corrSeries1 = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_corrSeries1, "Name", "ρ (corr)");
                SetProp(_corrSeries1, "Values", Enumerable.Repeat(0.0, _corrHist.Length).ToArray());
                TrySet(_corrSeries1, "GeometrySize", 0d);
                TrySet(_corrSeries1, "LineSmoothness", 0d);
                TrySet(_corrSeries1, "Stroke", corrLin);
                TrySet(_corrSeries1, "Fill", null);
                _xAxisCorr1 = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisCorr1, "LabelsPaint", null);
                TrySet(_xAxisCorr1, "SeparatorsPaint", null);
                TrySet(_xAxisCorr1, "TicksPaint", null);
                _yAxisCorr1 = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisCorr1, "MinLimit", -1d); SetProp(_yAxisCorr1, "MaxLimit", +1d);
                TrySet(_yAxisCorr1, "LabelsPaint", AxisPaint());
                TrySet(_yAxisCorr1, "SeparatorsPaint", GridPaint());
                TrySetLabeler(_yAxisCorr1, v => v.ToString("0.0"));
                TrySet(_yAxisCorr1, "TicksPaint", null);
                SetProp(_corrChart1, "Series", CreateArray(_iSeriesT!, _corrSeries1));
                SetProp(_corrChart1, "XAxes", CreateArray(_axisT!, _xAxisCorr1));
                SetProp(_corrChart1, "YAxes", CreateArray(_axisT!, _yAxisCorr1));

                // Layout Overview
                _pageLevels.RowCount = 1;
                _pageLevels.RowStyles.Clear();
                _pageLevels.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                var overview = new BufferedTable
                {
                    Dock = DockStyle.Fill,
                    BackColor = MeterBack,
                    ColumnCount = 3,
                    RowCount = 1,
                    Padding = new Padding(0)
                };
                overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13));
                overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
                overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
                overview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                var levelsGrid = NewGrid(2, 2);
                levelsGrid.Controls.Add(MakeChartHost(_scopeChart, "Forma d'onda L/R"), 0, 0);
                levelsGrid.Controls.Add(MakeChartHost(_vuChart, "RMS L/R"), 1, 0);
                levelsGrid.Controls.Add(MakeChartHost(_crestChart, "Crest factor"), 0, 1);
                levelsGrid.Controls.Add(MakeChartHost(_balanceChart, "Balance"), 1, 1);
                _workspaceHeader.Section = 0;
                overview.Controls.Add(new MusicAnalysisTabs(0), 0, 0);
                overview.Controls.Add(levelsGrid, 1, 0);
                overview.Controls.Add(_artworkCard, 2, 0);
                _pageLevels.Controls.Add(overview, 0, 0);

                // ===== PAGE 2: SPECTRUM =====
                _specChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_specChart);
                TrySetEnum(_specChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_specChart, "TooltipPosition", _tipPosT!, "Top");
                _specSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_specSeries, "Name", "Spettro (dBFS)");
                SetProp(_specSeries, "Values", new double[] { -60, -60, -60 });
                TrySet(_specSeries, "GeometrySize", 0d);
                TrySet(_specSeries, "LineSmoothness", 0d);
                TrySet(_specSeries, "Stroke", spLine);
                TrySet(_specSeries, "Fill", spFill);
                _xAxisSp = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisSp, "LabelsPaint", AxisPaint());
                TrySet(_xAxisSp, "SeparatorsPaint", null);
                TrySet(_xAxisSp, "TicksPaint", null);
                _yAxisSp = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisSp, "MinLimit", -60d); SetProp(_yAxisSp, "MaxLimit", 0d);
                TrySet(_yAxisSp, "MinStep", 20d);
                TrySet(_yAxisSp, "LabelsPaint", AxisPaint());
                TrySet(_yAxisSp, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisSp, "TicksPaint", null);
                SetProp(_specChart, "Series", CreateArray(_iSeriesT!, _specSeries));
                SetProp(_specChart, "XAxes", CreateArray(_axisT!, _xAxisSp));
                SetProp(_specChart, "YAxes", CreateArray(_axisT!, _yAxisSp));

                _pageSpectrum.RowStyles.Clear();
                _pageSpectrum.RowStyles.Add(new RowStyle(SizeType.Percent, 78));
                _pageSpectrum.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
                _pageSpectrum.Controls.Add(MakeChartHost(_specChart, "Spettro — mid (L+R)/2 in dBFS"), 0, 0);
                _pageSpectrum.Controls.Add(MakeSingleBadgePanel("Spectrum — info", _badgesSpectrum), 0, 1);

                // ===== PAGE 3: LOUDNESS =====
                _loudChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_loudChart);
                TrySetEnum(_loudChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_loudChart, "TooltipPosition", _tipPosT!, "Top");
                _loudSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_loudSeries, "Name", "M");
                SetProp(_loudSeries, "Values", _loudMHist);
                TrySet(_loudSeries, "GeometrySize", 0d);
                TrySet(_loudSeries, "LineSmoothness", 0.25d);
                TrySet(_loudSeries, "Stroke", loudFi);
                TrySet(_loudSeries, "Fill", null);
                _loudSSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_loudSSeries, "Name", "S");
                SetProp(_loudSSeries, "Values", _loudSHist);
                TrySet(_loudSSeries, "GeometrySize", 0d);
                TrySet(_loudSSeries, "LineSmoothness", 0.25d);
                TrySet(_loudSSeries, "Stroke", loudSLine);
                TrySet(_loudSSeries, "Fill", null);
                _loudISeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_loudISeries, "Name", "I");
                SetProp(_loudISeries, "Values", _loudIHist);
                TrySet(_loudISeries, "GeometrySize", 0d);
                TrySet(_loudISeries, "LineSmoothness", 0.25d);
                TrySet(_loudISeries, "Stroke", loudILine);
                TrySet(_loudISeries, "Fill", null);
                _xAxisLoud = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisLoud, "LabelsPaint", null);
                TrySet(_xAxisLoud, "SeparatorsPaint", null);
                TrySet(_xAxisLoud, "TicksPaint", null);
                _yAxisLoud = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisLoud, "MinLimit", -70d); SetProp(_yAxisLoud, "MaxLimit", 0d);
                TrySetLabeler(_yAxisLoud, v => $"{v:0} LUFS");
                TrySet(_yAxisLoud, "LabelsPaint", AxisPaint());
                TrySet(_yAxisLoud, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisLoud, "TicksPaint", null);
                SetProp(_loudChart, "Series", CreateArray(_iSeriesT!, _loudSeries, _loudSSeries, _loudISeries));
                SetProp(_loudChart, "XAxes", CreateArray(_axisT!, _xAxisLoud));
                SetProp(_loudChart, "YAxes", CreateArray(_axisT!, _yAxisLoud));

                _lraChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_lraChart);
                TrySetEnum(_lraChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_lraChart, "TooltipPosition", _tipPosT!, "Top");
                _lraSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_lraSeries, "Name", "LRA");
                SetProp(_lraSeries, "Values", _lraHist);
                TrySet(_lraSeries, "GeometrySize", 0d);
                TrySet(_lraSeries, "LineSmoothness", 0.25d);
                TrySet(_lraSeries, "Stroke", lraFi);
                TrySet(_lraSeries, "Fill", null);
                _xAxisLra = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisLra, "LabelsPaint", null);
                TrySet(_xAxisLra, "SeparatorsPaint", null);
                TrySet(_xAxisLra, "TicksPaint", null);
                _yAxisLra = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisLra, "MinLimit", 0d); SetProp(_yAxisLra, "MaxLimit", 30d);
                TrySet(_yAxisLra, "LabelsPaint", AxisPaint());
                TrySet(_yAxisLra, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisLra, "TicksPaint", null);
                SetProp(_lraChart, "Series", CreateArray(_iSeriesT!, _lraSeries));
                SetProp(_lraChart, "XAxes", CreateArray(_axisT!, _xAxisLra));
                SetProp(_lraChart, "YAxes", CreateArray(_axisT!, _yAxisLra));

                _tpChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_tpChart);
                TrySetEnum(_tpChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_tpChart, "TooltipPosition", _tipPosT!, "Top");
                _tpSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_tpSeries, "Name", "TP L");
                SetProp(_tpSeries, "Values", _tpLHist);
                TrySet(_tpSeries, "GeometrySize", 0d);
                TrySet(_tpSeries, "LineSmoothness", 0.25d);
                TrySet(_tpSeries, "Stroke", tpFi);
                TrySet(_tpSeries, "Fill", null);
                _tpRSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_tpRSeries, "Name", "TP R");
                SetProp(_tpRSeries, "Values", _tpRHist);
                TrySet(_tpRSeries, "GeometrySize", 0d);
                TrySet(_tpRSeries, "LineSmoothness", 0.25d);
                TrySet(_tpRSeries, "Stroke", tpRLine);
                TrySet(_tpRSeries, "Fill", null);
                _xAxisTp = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisTp, "LabelsPaint", null);
                TrySet(_xAxisTp, "SeparatorsPaint", null);
                TrySet(_xAxisTp, "TicksPaint", null);
                _yAxisTp = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisTp, "MinLimit", -6d); SetProp(_yAxisTp, "MaxLimit", 0d);
                TrySet(_yAxisTp, "LabelsPaint", AxisPaint());
                TrySetLabeler(_yAxisTp, v => $"{v:0} dBTP");
                TrySet(_yAxisTp, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisTp, "TicksPaint", null);
                SetProp(_tpChart, "Series", CreateArray(_iSeriesT!, _tpSeries, _tpRSeries));
                SetProp(_tpChart, "XAxes", CreateArray(_axisT!, _xAxisTp));
                SetProp(_tpChart, "YAxes", CreateArray(_axisT!, _yAxisTp));

                _dynChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_dynChart);
                TrySetEnum(_dynChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_dynChart, "TooltipPosition", _tipPosT!, "Top");
                _dynSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_dynSeries, "Name", "PSR");
                SetProp(_dynSeries, "Values", _psrHist);
                TrySet(_dynSeries, "GeometrySize", 0d);
                TrySet(_dynSeries, "LineSmoothness", 0.25d);
                TrySet(_dynSeries, "Stroke", dynFi);
                TrySet(_dynSeries, "Fill", null);
                _dynPlrSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_dynPlrSeries, "Name", "PLR");
                SetProp(_dynPlrSeries, "Values", _plrHist);
                TrySet(_dynPlrSeries, "GeometrySize", 0d);
                TrySet(_dynPlrSeries, "LineSmoothness", 0.25d);
                TrySet(_dynPlrSeries, "Stroke", dynPlrLine);
                TrySet(_dynPlrSeries, "Fill", null);
                _xAxisDyn = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisDyn, "LabelsPaint", null);
                TrySet(_xAxisDyn, "SeparatorsPaint", null);
                TrySet(_xAxisDyn, "TicksPaint", null);
                _yAxisDyn = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisDyn, "MinLimit", 0d); SetProp(_yAxisDyn, "MaxLimit", 30d);
                TrySet(_yAxisDyn, "LabelsPaint", AxisPaint());
                TrySet(_yAxisDyn, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisDyn, "TicksPaint", null);
                SetProp(_dynChart, "Series", CreateArray(_iSeriesT!, _dynSeries, _dynPlrSeries));
                SetProp(_dynChart, "XAxes", CreateArray(_axisT!, _xAxisDyn));
                SetProp(_dynChart, "YAxes", CreateArray(_axisT!, _yAxisDyn));

                // Layout Loudness
                _pageLoud.RowCount = 2;
                _pageLoud.RowStyles.Clear();
                _pageLoud.RowStyles.Add(new RowStyle(SizeType.Percent, 76));
                _pageLoud.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
                var loudGrid = NewGrid(2, 2);
                loudGrid.Controls.Add(MakeChartHost(_loudChart, "LUFS M / S / I"), 0, 0);
                loudGrid.Controls.Add(MakeChartHost(_tpChart, "True peak L/R"), 1, 0);
                loudGrid.Controls.Add(MakeChartHost(_lraChart, "LRA"), 0, 1);
                loudGrid.Controls.Add(MakeChartHost(_dynChart, "PSR / PLR"), 1, 1);
                _pageLoud.Controls.Add(loudGrid, 0, 0);
                _pageLoud.Controls.Add(MakeBadgePanel("Loudness e picchi", _badgesLoud, _badgesPeaks), 0, 1);

                // ===== PAGE 4: STEREO/DIAG =====
                _widthChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_widthChart);
                TrySetEnum(_widthChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_widthChart, "TooltipPosition", _tipPosT!, "Top");
                _widthSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_widthSeries, "Name", "Width (Mid/Side, dB)");
                SetProp(_widthSeries, "Values", _widthHist);
                TrySet(_widthSeries, "GeometrySize", 0d);
                TrySet(_widthSeries, "LineSmoothness", 0.25d);
                TrySet(_widthSeries, "Stroke", widthFi);
                TrySet(_widthSeries, "Fill", null);
                _xAxisWidth = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisWidth, "LabelsPaint", null);
                TrySet(_xAxisWidth, "SeparatorsPaint", null);
                TrySet(_xAxisWidth, "TicksPaint", null);
                _yAxisWidth = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisWidth, "MinLimit", -36d); SetProp(_yAxisWidth, "MaxLimit", +36d);
                TrySet(_yAxisWidth, "LabelsPaint", AxisPaint());
                TrySetLabeler(_yAxisWidth, v => $"{v:0} dB");
                TrySet(_yAxisWidth, "SeparatorsPaint", GridPaint());
                TrySet(_yAxisWidth, "TicksPaint", null);
                SetProp(_widthChart, "Series", CreateArray(_iSeriesT!, _widthSeries));
                SetProp(_widthChart, "XAxes", CreateArray(_axisT!, _xAxisWidth));
                SetProp(_widthChart, "YAxes", CreateArray(_axisT!, _yAxisWidth));

                _corrChart2 = Activator.CreateInstance(_chartT!)!;
                PrepChart(_corrChart2);
                TrySetEnum(_corrChart2, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_corrChart2, "TooltipPosition", _tipPosT!, "Top");
                _corrSeries2 = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_corrSeries2, "Name", "ρ (corr)");
                SetProp(_corrSeries2, "Values", Enumerable.Repeat(0.0, _corrHist.Length).ToArray());
                TrySet(_corrSeries2, "GeometrySize", 0d);
                TrySet(_corrSeries2, "LineSmoothness", 0d);
                TrySet(_corrSeries2, "Stroke", corrLin);
                TrySet(_corrSeries2, "Fill", null);
                _xAxisCorr2 = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisCorr2, "LabelsPaint", null);
                TrySet(_xAxisCorr2, "SeparatorsPaint", null);
                TrySet(_xAxisCorr2, "TicksPaint", null);
                _yAxisCorr2 = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisCorr2, "MinLimit", -1d); SetProp(_yAxisCorr2, "MaxLimit", +1d);
                TrySet(_yAxisCorr2, "LabelsPaint", AxisPaint());
                TrySet(_yAxisCorr2, "SeparatorsPaint", GridPaint());
                TrySetLabeler(_yAxisCorr2, v => v.ToString("0.0"));
                TrySet(_yAxisCorr2, "TicksPaint", null);
                SetProp(_corrChart2, "Series", CreateArray(_iSeriesT!, _corrSeries2));
                SetProp(_corrChart2, "XAxes", CreateArray(_axisT!, _xAxisCorr2));
                SetProp(_corrChart2, "YAxes", CreateArray(_axisT!, _yAxisCorr2));

                _pageStereo.RowCount = 2;
                _pageStereo.RowStyles.Clear();
                _pageStereo.RowStyles.Add(new RowStyle(SizeType.Percent, 78));
                _pageStereo.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
                var stereoTop = NewPair(50, 50);
                stereoTop.Controls.Add(MakeChartHost(_corrChart2, "Correlazione stereo"), 0, 0);
                stereoTop.Controls.Add(MakeChartHost(_widthChart, "Width mid/side"), 1, 0);
                _pageStereo.Controls.Add(stereoTop, 0, 0);
                _pageStereo.Controls.Add(MakeBadgePanel("Diagnostica", _badgesStereo, _badgesDiag), 0, 1);

                foreach (var axis in new[] { _yAxisSc, _yAxisCr, _yAxisBal, _xAxisSp, _yAxisCorr1, _yAxisCorr2, _yAxisTp, _yAxisLra })
                { if (axis != null) TrySet(axis, "TextSize", 11d); }
                TrySet(_yAxisSc!, "CustomSeparators", new[] { -.5d, 0d, .5d });
                TrySet(_yAxisCr!, "CustomSeparators", new[] { 0d, 5d, 10d, 15d, 20d });
                TrySet(_yAxisBal!, "CustomSeparators", new[] { -10d, -5d, 0d, 5d, 10d });
                foreach (var axis in new[] { _yAxisVu, _yAxisLoud, _yAxisSp })
                { if (axis != null) { TrySet(axis, "MinStep", 10d); TrySet(axis, "ForceStepToMin", true); TrySet(axis, "TextSize", 11d); } }
                foreach (var axis in new[] { _xAxisVu, _xAxisCr, _xAxisBal, _xAxisLoud })
                { if (axis != null) { TrySet(axis, "MinLimit", 0d); TrySet(axis, "MaxLimit", (double)(HISTORY_N - 1)); } }
                BuildPlaybackInfoOverview();
                return true;
            }
            catch { return false; }
        }

        private void BuildPlaybackInfoOverview()
        {
            BuildPlaybackInfoPage(0);
        }

        private void BuildPlaybackInfoPage(int section)
        {
            section = Math.Clamp(section, 0, 7);
            _playbackInfoSection = section;

            if (_builtPlaybackInfoSection == section && _pageLevels.Controls.Count > 0) return;
            _builtPlaybackInfoSection = section;

            _pageLevels.SuspendLayout();
            try
            {
                foreach (Control child in _pageLevels.Controls.Cast<Control>().ToArray())
                {
                    _pageLevels.Controls.Remove(child);
                    if (child is MusicAnalysisTabs) child.Dispose();
                }
                _pageLevels.ColumnStyles.Clear();
                _pageLevels.RowStyles.Clear();
                _pageLevels.ColumnCount = 1;
                _pageLevels.RowCount = 2;
                _pageLevels.Padding = Padding.Empty;
                _pageLevels.Margin = Padding.Empty;
                _pageLevels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                _pageLevels.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)Math.Round(52 * DeviceDpi / 96f)));
                _pageLevels.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                _workspaceHeader.Section = section; _workspaceHeader.Invalidate();
                var rail = new MusicAnalysisTabs(section, _english);
                rail.SectionClicked += SetPlaybackInfoSection;
                _pageLevels.Controls.Add(rail, 0, 0);

                if (section == 7)
                {
                    // Strumenti: VU meter analogici, spie di clip ed esportazione dei dati.
                    _vuBoard.English = _english;
                    _pageLevels.Controls.Add(_vuBoard, 0, 1);
                }
                else
                {
                    _cleanOverview.Section = section;
                    _cleanOverview.English = _english;
                    _pageLevels.Controls.Add(_cleanOverview, 0, 1);
                }
            }
            finally
            {
                _pageLevels.ResumeLayout(true);
            }
        }

        // ===== Helpers =====
        private static TableLayoutPanel NewPage(int rows)
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = MeterBack,
                ColumnCount = 1,
                RowCount = rows,
                Padding = new Padding(12)
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return p;
        }

        private static TableLayoutPanel NewPair(int leftPct, int rightPct)
        {
            var pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = MeterBack, Padding = new Padding(0) };
            pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftPct));
            pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, rightPct));
            return pair;
        }

        private static TableLayoutPanel NewGrid(int columns, int rows)
        {
            var grid = new BufferedTable
            {
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = rows,
                BackColor = MeterBack,
                Padding = new Padding(0)
            };

            for (int i = 0; i < columns; i++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / Math.Max(1, columns)));
            for (int i = 0; i < rows; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / Math.Max(1, rows)));

            return grid;
        }

        private static double ToDb(double v) => Math.Clamp(20.0 * Math.Log10(Math.Max(v, 1e-12)), -120.0, 0.0);
        private static double SafeFinite(double v, double fallback) => (double.IsFinite(v) ? v : fallback);

        private static double[] MakeHistory(double initial)
            => Enumerable.Repeat(initial, HISTORY_N).ToArray();

        private static void PushHistory(double[] history, double value)
        {
            if (history.Length == 0) return;
            Array.Copy(history, 1, history, 0, history.Length - 1);
            if (!double.IsFinite(value))
                value = history.Length > 1 ? history[^2] : 0;
            if (history.Length > 1 && double.IsFinite(history[^2]))
                value = history[^2] + (value - history[^2]) * 0.35;
            history[^1] = value;
        }

        private void RefreshCurrentPageCharts()
        {
            if (!_ok) return;

            switch (_currentPage)
            {
                case 0:
                    _cleanOverview.Invalidate();
                    break;
                case 1:
                    CallMethod(_specChart!, "Update");
                    break;
                case 2:
                    CallMethod(_loudChart!, "Update");
                    CallMethod(_tpChart!, "Update");
                    CallMethod(_lraChart!, "Update");
                    CallMethod(_dynChart!, "Update");
                    break;
                case 3:
                    CallMethod(_corrChart2!, "Update");
                    CallMethod(_widthChart!, "Update");
                    break;
            }
        }

        private static string FmtLufs(double v)
        {
            if (double.IsNegativeInfinity(v)) return "−∞";
            if (!double.IsFinite(v)) return "n/a";
            return $"{v:0.0}";
        }

        private static BufferedLabel MakeBadge()
        {
            return new BufferedLabel
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(158, 205, 246),
                BackColor = PanelBack,
                Padding = new Padding(10, 6, 10, 6),
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Regular)
            };
        }

        private static Control MakeBadgePanel(string title, BufferedLabel row1, BufferedLabel row2)
        {
            var host = new MeterCardTable
            {
                Dock = DockStyle.Fill,
                BackColor = PanelBack,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(5),
                Padding = new Padding(10)
            };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            var lbl = new BufferedLabel
            {
                Text = title,
                Dock = DockStyle.Fill,
                ForeColor = TextMain,
                BackColor = PanelBack,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0),
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Bold)
            };
            row1.Margin = new Padding(0);
            row2.Margin = new Padding(0);
            host.Controls.Add(lbl, 0, 0);
            host.Controls.Add(row1, 0, 1);
            host.Controls.Add(row2, 0, 2);
            return host;
        }

        private static Control MakeSingleBadgePanel(string title, BufferedLabel row)
        {
            var host = new MeterCardTable
            {
                Dock = DockStyle.Fill,
                BackColor = PanelBack,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(5),
                Padding = new Padding(10)
            };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            host.Controls.Add(new BufferedLabel
            {
                Text = title,
                Dock = DockStyle.Fill,
                ForeColor = TextMain,
                BackColor = PanelBack,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0),
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Bold)
            }, 0, 0);
            row.Margin = new Padding(0);
            row.Tag = "metrics";
            host.Controls.Add(row, 0, 1);
            return host;
        }

        private static Control MakeTitledPanel(string title, Control inner)
        {
            var host = new MeterCardTable
            {
                Dock = DockStyle.Fill,
                BackColor = PanelBack,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(12, 8, 12, 14),
                Padding = new Padding(4)
            };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var header = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = PanelBack,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            var lbl = new BufferedLabel
            {
                Text = title,
                Dock = DockStyle.Fill,
                ForeColor = TextMain,
                BackColor = PanelBack,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10.3f, FontStyle.Regular)
            };
            header.Controls.Add(lbl);
            inner.Dock = DockStyle.Fill;
            if (inner is Control c) { c.Margin = new Padding(0); c.Padding = new Padding(0); }
            host.Controls.Add(header, 0, 0);
            host.Controls.Add(inner, 0, 1);
            return host;
        }

        private static void SetLabelTextNoFlicker(BufferedLabel lbl, string text)
        {
            if (!string.Equals(lbl.Text, text, StringComparison.Ordinal))
                lbl.Text = text;
        }

        private static void CallMethod(object target, string methodName)
        {
            try { target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)?.Invoke(target, null); }
            catch { }
        }

        private static void TrySetEnum(object? target, string prop, Type enumType, string member)
        {
            try { target?.GetType().GetProperty(prop)?.SetValue(target, Enum.Parse(enumType, member)); } catch { }
        }

        private static void TrySetLabeler(object axis, Func<double, string> f)
        {
            try { axis.GetType().GetProperty("Labeler", BindingFlags.Instance | BindingFlags.Public)?.SetValue(axis, f); }
            catch { }
        }

        private static void TrySet(object target, string prop, object? value)
        {
            try { SetProp(target, prop, value); } catch { }
        }

        private static void SetProp(object target, string propName, object? value)
        {
            var pi = target.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
            if (pi == null || !pi.CanWrite) return;

            var destT = pi.PropertyType; var v = value;
            if (v != null && !destT.IsInstanceOfType(v))
            {
                if (destT.IsArray && v is Array src)
                {
                    var elemT = destT.GetElementType()!;
                    var arr = Array.CreateInstance(elemT, src.Length);
                    for (int i = 0; i < src.Length; i++) arr.SetValue(src.GetValue(i), i);
                    v = arr;
                }
            }
            pi.SetValue(target, v);
        }

        private static Array CreateArray(Type elementType, params object?[] items)
        {
            var arr = Array.CreateInstance(elementType, items.Length);
            for (int i = 0; i < items.Length; i++) arr.SetValue(items[i], i);
            return arr;
        }

        private object MakePaint(Type solidPaintT, byte r, byte g, byte b, byte a, double strokeThickness = 0)
        {
            var skColorT = Resolve("SkiaSharp.SKColor, SkiaSharp")!;
            var color = Activator.CreateInstance(skColorT, new object[] { r, g, b, a })!;
            var paint = Activator.CreateInstance(solidPaintT, new object[] { color })!;
            TrySet(paint, "StrokeThickness", strokeThickness);
            return paint;
        }

        private static Type? Resolve(string? aqn)
        {
            if (aqn == null) return null;
            var t = Type.GetType(aqn, false);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(aqn.Split(',')[0], false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static void TryLoadManaged(string simple)
        {
            try
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                {
                    try { return string.Equals(a.GetName().Name, simple, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                })) return;

                var b = AppContext.BaseDirectory;
                foreach (var p in new[]
                {
                    Path.Combine(b, simple + ".dll"),
                    Path.Combine(b, "runtimes","win-x64","lib", simple + ".dll"),
                    Path.Combine(b, "runtimes","win","lib", simple + ".dll")
                })
                {
                    if (File.Exists(p)) { Assembly.LoadFrom(p); break; }
                }
            }
            catch { }
        }

        private static void AddNativeSearchPaths()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string[] nativeDirs =
                {
                    baseDir,
                    Path.Combine(baseDir, "runtimes","win-x64","native"),
                    Path.Combine(baseDir, "runtimes","win","native")
                };
                SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
                foreach (var d in nativeDirs.Distinct().Where(Directory.Exists))
                    AddDllDirectory(d);
            }
            catch { }
        }

        private const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetDefaultDllDirectories(int flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr AddDllDirectory(string path);

        // ================== NAV BUTTON (nuovo stile) ==================
        private sealed class ExpandButton : Control
        {
            private bool _hover;

            public ExpandButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = PanelBack;
                ForeColor = TextMuted;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left)
                    OnClick(EventArgs.Empty);
            }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
                using var br = new SolidBrush(BackColor);
                pevent.Graphics.FillRectangle(br, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(Width / 2 - 7, Height / 2 - 7, 14, 14);
                Color color = _hover ? HUD.Theme.Accent : TextMuted;
                using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

                e.Graphics.DrawLine(pen, r.Left, r.Top + 5, r.Left, r.Top);
                e.Graphics.DrawLine(pen, r.Left, r.Top, r.Left + 5, r.Top);
                e.Graphics.DrawLine(pen, r.Right, r.Top + 5, r.Right, r.Top);
                e.Graphics.DrawLine(pen, r.Right, r.Top, r.Right - 5, r.Top);
                e.Graphics.DrawLine(pen, r.Left, r.Bottom - 5, r.Left, r.Bottom);
                e.Graphics.DrawLine(pen, r.Left, r.Bottom, r.Left + 5, r.Bottom);
                e.Graphics.DrawLine(pen, r.Right, r.Bottom - 5, r.Right, r.Bottom);
                e.Graphics.DrawLine(pen, r.Right, r.Bottom, r.Right - 5, r.Bottom);
            }
        }

        private sealed class CloseButton : Control
        {
            private bool _hover;

            public CloseButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = PanelBack;
                ForeColor = TextMuted;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left)
                    OnClick(EventArgs.Empty);
            }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
                using var br = new SolidBrush(BackColor);
                pevent.Graphics.FillRectangle(br, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color color = _hover ? Color.White : TextMuted;
                using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                int cx = Width / 2;
                int cy = Height / 2;
                int d = 6;
                e.Graphics.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                e.Graphics.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
            }
        }

        private sealed class NavButton : Control
        {
            private bool _selected;
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool Selected
            {
                get => _selected;
                set { _selected = value; Invalidate(); }
            }

            private bool _hover;
            private readonly Font _bold = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Bold);
            private readonly Font _reg = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular);

            public NavButton(string text)
            {
                Text = text;
                Dock = DockStyle.Fill;
                Margin = new Padding(2, 0, 2, 0);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
                BackColor = MeterBack;
                ForeColor = TextMuted;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty); }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
                using var br = new SolidBrush(MeterBack);
                pevent.Graphics.FillRectangle(br, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var pill = new Rectangle(3, 3, Math.Max(1, Width - 6), Math.Max(1, Height - 6));
                using (var path = RoundedRect(pill, 3))
                using (var fill = new SolidBrush(_hover && Enabled
                    ? Color.FromArgb(26, 255, 255, 255)
                    : Color.Transparent))
                {
                    e.Graphics.FillPath(fill, path);
                }

                var rect = Rectangle.Inflate(pill, -6, 0);
                var textColor = !Enabled ? Color.FromArgb(90, 110, 116, 126) : (_selected ? TextMain : TextMuted);
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    _selected && Enabled ? _bold : _reg,
                    rect,
                    textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                if (_selected && Enabled)
                    using (var acc = new SolidBrush(Color.FromArgb(220, HUD.Theme.Accent)))
                        e.Graphics.FillRectangle(acc, pill.Left + 18, pill.Bottom - 2, Math.Max(18, pill.Width - 36), 2);
            }
        }

        // ===== Controls anti-flicker =====
        private class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var b = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        private sealed class AudioMetadataSummary : BufferedPanel
        {
            private readonly string _title;
            private readonly string _subtitle;
            private readonly string _quality;

            public AudioMetadataSummary(string? title, string? subtitle, string? quality)
            {
                Dock = DockStyle.Fill;
                Margin = new Padding(5);
                BackColor = MeterBack;
                _title = Clean(title);
                _subtitle = Clean(subtitle);
                _quality = Clean(quality);
            }

            private static string Clean(string? value)
                => string.IsNullOrWhiteSpace(value) ? "n/d" : value.Trim();

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                Rectangle content = Rectangle.Inflate(ClientRectangle, -10, -8);
                if (content.Width < 40 || content.Height < 40)
                    return;

                using var overlineFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 7.8f, FontStyle.Regular);
                using var valueFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 11.2f, FontStyle.Regular);
                using var qualityFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10.2f, FontStyle.Regular);
                using var linePen = new Pen(Color.FromArgb(54, 132, 151, 170), 1f);
                using var accentPen = new Pen(Color.FromArgb(210, HUD.Theme.Accent), 2f);

                e.Graphics.DrawLine(linePen, content.Left, content.Top, content.Right, content.Top);
                e.Graphics.DrawLine(accentPen, content.Left, content.Top, Math.Min(content.Right, content.Left + 42), content.Top);
                e.Graphics.DrawLine(linePen, content.Left, content.Bottom, content.Right, content.Bottom);

                int bodyTop = content.Top + 18;
                int bodyHeight = Math.Max(1, content.Height - 28);
                if (content.Width >= 720)
                {
                    int firstW = (int)Math.Round(content.Width * 0.40);
                    int secondW = (int)Math.Round(content.Width * 0.33);
                    Rectangle titleRect = new(content.Left, bodyTop, firstW, bodyHeight);
                    Rectangle artistRect = new(titleRect.Right + 1, bodyTop, secondW, bodyHeight);
                    Rectangle qualityRect = new(artistRect.Right + 1, bodyTop, Math.Max(1, content.Right - artistRect.Right - 1), bodyHeight);

                    e.Graphics.DrawLine(linePen, titleRect.Right, bodyTop + 8, titleRect.Right, content.Bottom - 8);
                    e.Graphics.DrawLine(linePen, artistRect.Right, bodyTop + 8, artistRect.Right, content.Bottom - 8);
                    DrawField(e.Graphics, titleRect, "TITOLO", _title, valueFont, TextMain);
                    DrawField(e.Graphics, artistRect, "ARTISTA / ALBUM", _subtitle, valueFont, TextMain);
                    DrawField(e.Graphics, qualityRect, "SEGNALE", _quality, qualityFont, Color.FromArgb(158, 205, 246));
                }
                else
                {
                    int rowH = Math.Max(1, bodyHeight / 3);
                    DrawCompactField(e.Graphics, new Rectangle(content.Left, bodyTop, content.Width, rowH), "TITOLO", _title, valueFont, TextMain);
                    DrawCompactField(e.Graphics, new Rectangle(content.Left, bodyTop + rowH, content.Width, rowH), "ARTISTA / ALBUM", _subtitle, valueFont, TextMain);
                    DrawCompactField(e.Graphics, new Rectangle(content.Left, bodyTop + rowH * 2, content.Width, bodyHeight - rowH * 2), "SEGNALE", _quality, qualityFont, Color.FromArgb(158, 205, 246));
                }

                void DrawField(Graphics g, Rectangle r, string label, string value, Font font, Color color)
                {
                    Rectangle padded = Rectangle.Inflate(r, -18, -4);
                    TextRenderer.DrawText(g, label, overlineFont,
                        new Rectangle(padded.Left, padded.Top, padded.Width, 22), TextMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, value, font,
                        new Rectangle(padded.Left, padded.Top + 27, padded.Width, Math.Max(22, padded.Height - 29)), color,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }

                void DrawCompactField(Graphics g, Rectangle r, string label, string value, Font font, Color color)
                {
                    int labelW = Math.Min(130, Math.Max(92, r.Width / 4));
                    TextRenderer.DrawText(g, label, overlineFont,
                        new Rectangle(r.Left + 6, r.Top, labelW - 8, r.Height), TextMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, value, font,
                        new Rectangle(r.Left + labelW, r.Top, Math.Max(1, r.Width - labelW - 6), r.Height), color,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }
        }

        private sealed class AudioArtworkCard : BufferedPanel
        {
            private Image? _image;
            private Image? _artwork;
            private string? _artworkKey;
            private string _title = "Audio";
            private string _subtitle = string.Empty;
            private string _quality = string.Empty;

            public AudioArtworkCard()
            {
                Dock = DockStyle.Fill;
                Margin = new Padding(5);
                BackColor = MeterBack;
                LoadImage();
            }

            public void SetText(string title, string subtitle, string quality)
            {
                _title = string.IsNullOrWhiteSpace(title) ? "Audio" : title;
                _subtitle = subtitle ?? string.Empty;
                _quality = quality ?? string.Empty;
                AccessibleName = string.Join(" - ", new[] { _title, _subtitle, _quality }.Where(v => !string.IsNullOrWhiteSpace(v)));
                Invalidate();
            }

            public void SetArtworkPath(string? artworkPath)
            {
                string key = string.IsNullOrWhiteSpace(artworkPath) ? string.Empty : artworkPath.Trim();
                if (string.Equals(_artworkKey, key, StringComparison.OrdinalIgnoreCase))
                    return;

                _artworkKey = key;
                try { _artwork?.Dispose(); } catch { }
                _artwork = null;

                if (!string.IsNullOrWhiteSpace(key) && File.Exists(key))
                {
                    try
                    {
                        using var src = Image.FromFile(key);
                        _artwork = new Bitmap(src);
                    }
                    catch { _artwork = null; }
                }

                Invalidate();
            }

            private void LoadImage()
            {
                try
                {
                    string baseDir = AppContext.BaseDirectory;
                    foreach (var p in new[]
                    {
                        Path.Combine(baseDir, "Assets", "audioOnly.png"),
                        Path.Combine(baseDir, "Assets", "AudioOnly.png"),
                        Path.Combine(baseDir, "Assets", "audioOnly.jpg")
                    })
                    {
                        if (!File.Exists(p)) continue;
                        using var src = Image.FromFile(p);
                        _image = new Bitmap(src);
                        return;
                    }
                }
                catch { }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                Rectangle card = Rectangle.Inflate(ClientRectangle, -6, -6);
                if (card.Width <= 30 || card.Height <= 30)
                    return;

                var image = _artwork ?? _image;
                Rectangle art = Rectangle.Inflate(card, -8, -8);
                if (image != null)
                    DrawContained(e.Graphics, image, art);
            }

            private static void DrawContained(Graphics g, Image image, Rectangle bounds)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0 || image.Width <= 0 || image.Height <= 0)
                    return;

                double scale = Math.Min(bounds.Width / (double)image.Width, bounds.Height / (double)image.Height);
                int w = Math.Max(1, (int)Math.Round(image.Width * scale));
                int h = Math.Max(1, (int)Math.Round(image.Height * scale));
                Rectangle dest = new(bounds.Left + (bounds.Width - w) / 2, bounds.Top + (bounds.Height - h) / 2, w, h);
                g.DrawImage(image, dest);
            }

            internal static void DrawCover(Graphics g, Image image, Rectangle bounds)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return;

                double scale = Math.Max(bounds.Width / (double)image.Width, bounds.Height / (double)image.Height);
                int w = Math.Max(1, (int)Math.Round(image.Width * scale));
                int h = Math.Max(1, (int)Math.Round(image.Height * scale));
                Rectangle dest = new(bounds.Left + (bounds.Width - w) / 2, bounds.Top + (bounds.Height - h) / 2, w, h);

                using Region old = g.Clip.Clone();
                using (var clip = RoundedRect(bounds, 3))
                    g.SetClip(clip, CombineMode.Intersect);
                g.DrawImage(image, dest);
                g.Clip = old;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _image?.Dispose(); } catch { }
                    try { _artwork?.Dispose(); } catch { }
                    _image = null;
                    _artwork = null;
                }

                base.Dispose(disposing);
            }
        }

        private sealed class AudioPlaceholderOverlay : BufferedPanel
        {
            private Image? _image;
            private Image? _artwork;
            private string? _artworkKey;
            private string _title = "Audio";
            private string _subtitle = string.Empty;
            private string _quality = string.Empty;

            public AudioPlaceholderOverlay()
            {
                Dock = DockStyle.Fill;
                BackColor = MeterBack;
                Enabled = false;
                TabStop = false;
                Visible = false;
                LoadImage();
            }

            private void LoadImage()
            {
                try
                {
                    string baseDir = AppContext.BaseDirectory;
                    foreach (var p in new[]
                    {
                        Path.Combine(baseDir, "Assets", "audioOnly.png"),
                        Path.Combine(baseDir, "Assets", "AudioOnly.png"),
                        Path.Combine(baseDir, "Assets", "audioOnly.jpg")
                    })
                    {
                        if (!File.Exists(p)) continue;
                        using var src = Image.FromFile(p);
                        _image = new Bitmap(src);
                        return;
                    }
                }
                catch { }
            }

            public void SetArtworkPath(string? artworkPath)
            {
                string key = string.IsNullOrWhiteSpace(artworkPath) ? string.Empty : artworkPath.Trim();
                if (string.Equals(_artworkKey, key, StringComparison.OrdinalIgnoreCase))
                    return;

                _artworkKey = key;
                try { _artwork?.Dispose(); } catch { }
                _artwork = null;

                if (!string.IsNullOrWhiteSpace(key) && File.Exists(key))
                {
                    try
                    {
                        using var src = Image.FromFile(key);
                        _artwork = new Bitmap(src);
                    }
                    catch { _artwork = null; }
                }

                Invalidate();
            }

            public void SetText(string title, string subtitle, string quality)
            {
                _title = string.IsNullOrWhiteSpace(title) ? "Audio" : title;
                _subtitle = subtitle ?? string.Empty;
                _quality = quality ?? string.Empty;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (Width < 60 || Height < 60) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                bool stacked = Width < 860;
                int maxWidth = Math.Min(1840, Width - Math.Clamp(Width / 12, 64, 180));
                int left = (Width - maxWidth) / 2;
                int size = Math.Max(72, Math.Min(stacked ? Math.Min(Width - 64, 360) : Math.Min(maxWidth * 46 / 100, 720), Height - (stacked ? 230 : 110)));
                int top = Math.Max(24, (Height - (stacked ? size + 190 : size)) / 2 - 16);
                var art = new Rectangle(stacked ? (Width - size) / 2 : left, top, size, size);
                using (var shape = RoundedRect(art, 12))
                using (var fill = new SolidBrush(PanelBack)) g.FillPath(fill, shape);
                if (_artwork != null) AudioArtworkCard.DrawCover(g, _artwork, art);
                else
                {
                    var logo = HUD.SettingsHudPage.LoadBrandLogo();
                    if (logo != null)
                        g.DrawImage(logo, new Rectangle(art.Left + size / 8, art.Top + size * 3 / 8, size * 3 / 4, size / 4));
                }
                int textX = stacked ? left : art.Right + 56;
                int textW = stacked ? maxWidth : left + maxWidth - textX;
                int textY = stacked ? art.Bottom + 22 : top + Math.Max(12, (size - 220) / 2);
                var align = stacked ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
                var flags = align | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                using var eyebrow = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 8.5f);
                using var title = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", stacked ? 23 : Math.Clamp(Width / 44f, 29, 43));
                using var body = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", Math.Clamp(Width / 135f, 12f, 17f));
                using var quality = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", Math.Clamp(Width / 170f, 9.5f, 13f));
                
                int titleH = Math.Min(title.Height * 2 + 4, TextRenderer.MeasureText(_title, title, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4);
                if (!stacked) textY = top + Math.Max(0, (size - titleH - 130) / 2);
                TextRenderer.DrawText(g, _title, title, new Rectangle(textX - 2, textY, textW, titleH), TextMain,
                    flags | TextFormatFlags.WordBreak);
                int subY = textY + titleH + 18;
                TextRenderer.DrawText(g, _subtitle, body, new Rectangle(textX, subY, textW, 34), TextMuted, flags);
                TextRenderer.DrawText(g, _quality, quality, new Rectangle(textX, subY + 48, textW, 48), TextMuted, flags | TextFormatFlags.WordBreak);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _image?.Dispose(); } catch { }
                    try { _artwork?.Dispose(); } catch { }
                    _image = null;
                    _artwork = null;
                }

                base.Dispose(disposing);
            }
        }

        private sealed class MeterPanel : BufferedPanel
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var r = ClientRectangle;
                r.Width -= 1;
                r.Height -= 1;
                if (r.Width <= 0 || r.Height <= 0) return;
                using var p = new Pen(PanelBorder);
                e.Graphics.DrawRectangle(p, r);
            }
        }

        private sealed class MeterCardTable : BufferedTable
        {
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(MeterBack);
                var r = Rectangle.Inflate(ClientRectangle, -1, -1);
                if (r.Width <= 4 || r.Height <= 4)
                    return;
                // La riga dei valori sta direttamente sulla pagina, senza riquadro.
                if (Tag as string == "metrics") return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundedRect(r, 4);
                using var fill = new SolidBrush(BackColor);
                e.Graphics.FillPath(fill, path);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var r = ClientRectangle;
                r.Inflate(-1, -1);
                if (r.Width <= 4 || r.Height <= 4)
                    return;

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundedRect(r, 4);
                // Charts share the page surface; navigation selects a larger view.
            }
        }

        private class BufferedTable : TableLayoutPanel
        {
            public BufferedTable()
            {
                DoubleBuffered = true;
                BackColor = MeterBack;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var b = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        private sealed class BufferedLabel : Label
        {
            public BufferedLabel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var b = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Tag as string == "sync-status")
                {
                    if (string.IsNullOrWhiteSpace(Text)) return;
                    int width = Math.Min(Width - Padding.Horizontal, TextRenderer.MeasureText(Text, Font).Width + 32);
                    if (width <= 0) return;
                    var bounds = new Rectangle((Width - width) / 2, 0, width, Math.Max(20, Height - Padding.Vertical));
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var shape = RoundedRect(bounds, 7);
                    using var fill = new SolidBrush(PanelBack);
                    e.Graphics.FillPath(fill, shape);
                    TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    return;
                }
                if (Tag as string == "metrics")
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(Text, @"\s{2,}").Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                    int columns = Width < 700 ? 2 : Math.Max(1, parts.Length);
                    int rows = Math.Max(1, (parts.Length + columns - 1) / columns);
                    using var labelFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f);
                    using var valueFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 12f);
                    for(int i=0;i<parts.Length;i++)
                    {
                        int colon=parts[i].IndexOf(':');
                        string label=colon>=0?parts[i].Substring(0,colon):string.Empty;
                        string value=colon>=0?parts[i].Substring(colon+1).Trim():parts[i];
                        var cell=new Rectangle(i%columns*Width/columns+18,i/columns*Height/rows,Width/columns-28,Height/rows);
                        if (i % columns > 0) { using var separator = new Pen(Color.FromArgb(70, TextMuted)); e.Graphics.DrawLine(separator, i % columns * Width / columns, cell.Top + 3, i % columns * Width / columns, cell.Top + Math.Min(cell.Height, 44)); }
                        TextRenderer.DrawText(e.Graphics,label,labelFont,new Rectangle(cell.Left,cell.Top,cell.Width,20),TextMuted,TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis);
                        TextRenderer.DrawText(e.Graphics,value,valueFont,new Rectangle(cell.Left,cell.Top+21,cell.Width,Math.Max(20,cell.Height-21)),TextMain,TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis);
                    }
                    return;
                }
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                flags |= TextAlign switch
                {
                    ContentAlignment.MiddleCenter => TextFormatFlags.HorizontalCenter,
                    ContentAlignment.MiddleRight => TextFormatFlags.Right,
                    _ => TextFormatFlags.Left
                };
                var textBounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ForeColor, flags);
            }
        }

        // ===== Utils grafici =====
        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
