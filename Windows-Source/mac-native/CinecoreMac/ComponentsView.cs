using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>Versioni dei componenti e aggiornamento della singola riga, in base alla sua origine.</summary>
internal sealed class ComponentsView : Control
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private sealed class Row
    {
        public string Name = "", Formula = "", Origin = "";
        public string? Installed, Latest, Error;
        public bool Busy;
        // Build dell'app con patch proprie (mpv con CoreAudio Dolby): Homebrew la sostituirebbe con quella normale.
        public bool Patched;
        // Windows: componente incluso nell'app (third-parties) o installato nel sistema, senza aggiornamento da qui.
        public bool Local;
        public bool Wine => Formula.Length == 0;
        public bool YtDlp => Formula == "yt-dlp";
        public bool CanUpdate => Wine ? MacEngine.CanInstall : !Patched && (YtDlp || NativeComponentUpdates.Brew != null);
    }

    public event Action? Closed;
    private readonly List<Row> _rows = new();
    private readonly List<(Rect Rect, string Kind)> _hits = new();
    private Point _mouse = new(-1, -1);
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private DateTime _openedAt;
    private bool _collecting;
    private Row? _updating;
    private string _status = "";

    public ComponentsView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        MacEngine.Changed += () =>
        {
            if (_rows.FirstOrDefault(r => r.Wine && !r.Local) is not { } wine) return;
            wine.Installed = EngineVersion;
            if (IsOpen) InvalidateVisual();
        };
    }

    public bool IsOpen => IsVisible;
    internal string QaSummary => string.Join(" | ", _rows.Select(r =>
        $"{r.Name}={r.Installed ?? "-"} ({r.Origin}) ultima={r.Latest ?? "-"} azione={ActionLabel(r) ?? "-"}"));

    public void Open()
    {
        _openedAt = DateTime.UtcNow;
        IsVisible = true; Focus();
        InvalidateVisual();
        if (_collecting) return;
        if (_rows.Count == 0) _status = "";
        _collecting = true;
        _ = LoadRowsAsync();
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Closed?.Invoke();
    }

    private async Task LoadRowsAsync()
    {
        try
        {
            if (_rows.Count == 0) _rows.AddRange(await Task.Run(Collect));
            await Task.WhenAll(_rows.Where(r => !r.Wine).Select(CheckAsync));
        }
        catch (Exception ex) { Console.Error.WriteLine("[componenti] " + ex.Message); _status = "Impossibile leggere le versioni"; }
        finally { _collecting = false; InvalidateVisual(); }
    }

    private static string? EngineVersion => MacEngine.Dev ? MacEngine.Available?.Version : MacEngine.InstalledVersion;

    private static List<Row> Collect()
    {
#if CINECORE_WINDOWS
        return CollectWindows();
#endif
        string list = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "components.txt"));
        bool inBundle = File.Exists(list);
        // components.txt: "nome versione [(nota)]", per esempio "mpv 0.42.0 (CoreAudio Dolby)": la versione e' la seconda
        // parola (ReadVersions prende l'ultima, giusto per l'elenco di Homebrew, e qui dava "Dolby)").
        var bundled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var patched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (inBundle)
            foreach (var line in File.ReadAllLines(list))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                bundled[parts[0]] = parts[1];
                if (line.Contains('(')) patched.Add(parts[0]);
            }
        var brew = NativeComponentUpdates.InstalledVersions();
        Row Native(string name, string formula, bool prefersBrew)
        {
            bool homebrew = !inBundle || prefersBrew && brew.ContainsKey(formula);
            return new Row { Name = name, Formula = formula,
                Installed = (homebrew ? brew : bundled).GetValueOrDefault(formula),
                Origin = homebrew ? "Homebrew" : "nel pacchetto dell'app",
                Patched = !homebrew && patched.Contains(formula) };
        }
        string? ytdlp = MacYtDlp.Find();
        return new List<Row>
        {
            Native("FFmpeg", "ffmpeg", NativeComponentUpdates.UseBrewFfmpeg),
            Native("mpv (libmpv)", "mpv", NativeComponentUpdates.UseBrewMpv),
            Native("libplacebo", "libplacebo", NativeComponentUpdates.UseBrewMpv),
            Native("MoltenVK", "molten-vk", NativeComponentUpdates.UseBrewVulkan),
            new() { Name = "yt-dlp", Formula = "yt-dlp", Installed = MacYtDlp.Version(ytdlp), Origin = MacYtDlp.Origin(ytdlp) },
            new() { Name = "Motore madVR e MPC Video Renderer",
                Installed = EngineVersion, Latest = MacEngine.Available?.Version, Origin = "Wine" },
        };
    }

#if CINECORE_WINDOWS
    /// <summary>Come la pagina "Componenti esterni" del vecchio player: cosa c'e' davvero su questo PC.</summary>
    private static List<Row> CollectWindows()
    {
        static string? FileVersion(string? path)
        {
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string? v = (info.ProductVersion ?? info.FileVersion)?.Trim();
                return string.IsNullOrEmpty(v) ? null : v.Split(' ')[0].Replace(", ", ".").Replace(',', '.');
            }
            catch { return null; }
        }
        static string? FolderVersion(string prefix)
        {
            foreach (string root in new[] { WinNative.ThirdParty() })
                if (root != null && Directory.Exists(root))
                    foreach (string dir in Directory.GetDirectories(root, prefix + "*"))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(dir), @"(\d+(?:\.\d+)+)");
                        if (m.Success) return m.Groups[1].Value;
                        if (prefix == "madVR" && System.Text.RegularExpressions.Regex.Match(Path.GetFileName(dir), @"madVR(\d)(\d{2})(\d{2})") is { Success: true } mv)
                            return $"{mv.Groups[1].Value}.{mv.Groups[2].Value}.{mv.Groups[3].Value}";
                    }
            return null;
        }
        static string? Registered(string clsid)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{" + clsid + @"}\InprocServer32");
                return FileVersion(key?.GetValue(null) as string);
            }
            catch { return null; }
        }
        static string? FfmpegVersion()
        {
            string? exe = WinNative.ThirdParty("ffmpeg", "win-x64", "ffmpeg.exe");
            if (exe == null) return null;
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-hide_banner -version")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                string first = p!.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(3000);
                var m = System.Text.RegularExpressions.Regex.Match(first, @"version\s+(\S+)");
                return m.Success ? m.Groups[1].Value : "presente";
            }
            catch { return "presente"; }
        }
        Row Local(string name, string? version, string origin) => new() { Name = name, Installed = version, Origin = origin, Local = true };
        string? ytdlp = MacYtDlp.Find();
        string? lavVideo = Registered("EE30215D-164F-4A92-A4EB-9D4C13390F9F");
        string? lavAudio = Registered("E8E73B6B-4CB3-44A4-BE99-4F7BCB96E491");
        return new List<Row>
        {
            new() { Name = "yt-dlp", Formula = "yt-dlp", Installed = MacYtDlp.Version(ytdlp), Origin = MacYtDlp.Origin(ytdlp) },
            Local("madVR", FileVersion(WinNative.ThirdParty("madVR09217", "madVR64.ax")) ?? FolderVersion("madVR"), "incluso"),
            Local("MPC Video Renderer", FolderVersion("MpcVideoRenderer"), "incluso"),
            Local("XySubFilter", FolderVersion("XySubFilter"), "incluso"),
            Local("LAV Video", lavVideo, lavVideo != null ? "installato" : "non installato"),
            Local("LAV Audio", lavAudio, lavAudio != null ? "installato" : "non installato"),
            Local("MPC Audio Renderer", Registered("601D2A2B-9CDE-40BD-8650-0485E3522727"), "facoltativo"),
            Local("mpv (libmpv)", FileVersion(MpvEngine.LibraryPath()) ?? (MpvEngine.Available ? "presente" : null), MpvEngine.UseV3Runtime ? "V3 sperimentale" : "standard"),
            Local("FFmpeg", FfmpegVersion(), "incluso"),
        };
    }
#endif

    private async Task CheckAsync(Row row)
    {
        if (row.Busy) return;
        row.Busy = true; row.Error = null; row.Latest = null; InvalidateVisual();
        try
        {
            if (row.YtDlp) row.Latest = (await MacYtDlp.LatestAsync(CancellationToken.None))?.Version;
            else
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://formulae.brew.sh/api/formula/" + row.Formula + ".json"));
                if (doc.RootElement.TryGetProperty("versions", out var versions) && versions.TryGetProperty("stable", out var stable))
                    row.Latest = stable.GetString();
                if (row.Latest != null && doc.RootElement.TryGetProperty("revision", out var rev) && rev.TryGetInt32(out int r) && r > 0)
                    row.Latest += "_" + r;
            }
            if (row.Latest == null) row.Error = "Versione non disponibile";
        }
        catch (Exception ex) { Console.Error.WriteLine("[componenti] " + row.Name + ": " + ex.Message); row.Error = "Verifica non riuscita"; }
        finally { row.Busy = false; InvalidateVisual(); }
    }

    private static bool IsOutdated(Row row)
    {
        if (row.Latest == null) return false;
        if (row.Installed == null) return true;
        static int[] Parts(string version) => System.Text.RegularExpressions.Regex.Matches(version, @"\d+")
            .Select(m => int.TryParse(m.Value, out int n) ? n : 0).ToArray();
        // Per una build propria conta solo la versione di mpv: "_1" e' una ricompilazione di Homebrew, non una versione nuova.
        string latest = row.Patched ? System.Text.RegularExpressions.Regex.Replace(row.Latest, @"_\d+$", "") : row.Latest;
        var a = Parts(row.Installed); var b = Parts(latest);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x < y;
        }
        return false;
    }

    private static string? ActionLabel(Row row)
    {
        if (row.Wine) return MacEngine.Busy ? "Annulla" : MacEngine.CanInstall ? "Aggiorna" : null;
        return !row.Busy && row.CanUpdate && IsOutdated(row) ? "Aggiorna" : null;
    }

    private async Task RunActionAsync(Row row)
    {
        if (row.Wine)
        {
            if (MacEngine.Busy) MacEngine.Cancel();
            else if (MacEngine.CanInstall) MacEngine.StartInstall();
            return;
        }
        if (_updating != null || row.Busy || !row.CanUpdate || !IsOutdated(row)) return;
        _updating = row; row.Busy = true; row.Error = null; _status = ""; InvalidateVisual();
        try
        {
            if (row.YtDlp)
            {
                var result = await Task.Run(() => MacYtDlp.UpdateAsync(CancellationToken.None));
                row.Installed = result.Installed ?? row.Installed;
                row.Latest = result.Latest ?? row.Latest;
                row.Origin = MacYtDlp.Origin(MacYtDlp.Find());
                row.Error = result.Error;
            }
            else
            {
                var result = await Task.Run(() => NativeComponentUpdates.Update(row.Formula));
                row.Error = result.Error;
                if (result.ActivationKey is { } key && result.Version is { } version)
                {
                    MacSettings.Set(key, true);
                    row.Installed = version;
                    row.Origin = "Homebrew al riavvio";
                    _status = "Riavvia Cinecore per usare la nuova versione.";
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[componenti] " + row.Name + ": " + ex.Message); row.Error = "Aggiornamento non riuscito"; }
        finally
        {
            if (row.Error != null) _status = row.Name + " – " + row.Error;
            _updating = null; row.Busy = false; InvalidateVisual();
        }
    }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        _hits.Clear();
        var page = new Rect(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 380, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        // Come le altre schede (tutorial, Nuova playlist): pagina velata e vignettata, niente riquadro. Qui il velo e' pieno:
        // la libreria che traspariva fra le righe sporcava la tabella.
        // Fondo pieno sotto, poi velo e vignettatura come le altre schede.
        ctx.FillRectangle(new SolidColorBrush(UiTheme.WithAlpha(WizardSheet.Back, (byte)Math.Round(255 * e))), page);
        WizardSheet.DrawVeil(ctx, page, e);
        _hits.Add((page, "sheet"));
        byte alpha = (byte)Math.Round(255 * e);
        double u = WizardSheet.Scale(page.Size), slide = Math.Round(26 * u * (1 - e));
        var cancel = WizardSheet.Cancel(page.Size);
        WizardSheet.DrawCancel(ctx, cancel, Hot(cancel), u, "Chiudi");
        _hits.Add((cancel, "close"));

        var ink = WizardSheet.Ink(alpha);
        var soft = WizardSheet.Soft(alpha);
        void Txt(string text, double px, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
        {
            var ft = WizardSheet.Text(text, px, face, color, r.Width, 1, align);
            ctx.DrawText(ft, new Point(r.X + slide, r.Y + (r.Height - ft.Height) / 2));
        }

        // Titolo e tabella come un blocco solo, centrato in altezza nella pagina.
        var column = WizardSheet.Column(page.Size);
        double titleH = 64 * u, titleGap = 36 * u, headingH = 30 * u, rowH = 62 * u;
        double tableH = _rows.Count == 0 ? 30 * u : headingH + _rows.Count * rowH + 52 * u;
        double top = Math.Max(cancel.Bottom + 24 * u, Math.Round((page.Height - titleH - titleGap - tableH) / 2));
        Txt("Componenti esterni", 44 * u, WizardSheet.Semibold, ink, new Rect(column.X, top, column.Width, titleH));
        double y = top + titleH + titleGap;
        if (_rows.Count == 0)
        {
            Txt(_status.Length > 0 ? _status : "Leggo le versioni…", 17 * u, WizardSheet.Regular, soft, new Rect(column.X, y, column.Width, 30 * u));
            return;
        }

        double left = column.X, right = column.Right, versionLeft = left + column.Width * .48;
        var hairline = new SolidColorBrush(UiTheme.WithAlpha(UiTheme.Text, (byte)Math.Round(22 * e)));
        Txt("Componente", 14 * u, WizardSheet.Semibold, soft, new Rect(left, y, versionLeft - left, headingH));
        Txt("Versione", 14 * u, WizardSheet.Semibold, soft, new Rect(versionLeft, y, right - versionLeft, headingH));
        Txt("Stato", 14 * u, WizardSheet.Semibold, soft, new Rect(versionLeft, y, right - versionLeft, headingH), TextAlignment.Right);
        y += headingH;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            double ry = y + i * rowH;
            ctx.FillRectangle(hairline, new Rect(left + slide, ry, column.Width, 1));
            var line = new Rect(left, ry + 1, column.Width, rowH - 1);
            Txt(row.Name, 17 * u, WizardSheet.Semibold, ink, new Rect(left, line.Y, versionLeft - left - 16 * u, line.Height));

            // A destra lo stato, oppure il pulsante: la colonna della versione finisce dove comincia uno dei due.
            bool update = IsOutdated(row) && row.Latest != null;
            string? action = ActionLabel(row);
            double stateW;
            if (action != null)
            {
                double bw = WizardSheet.Measure(action, 15 * u, WizardSheet.Semibold) + 36 * u, bh = 34 * u;
                var button = new Rect(right - bw, line.Y + (line.Height - bh) / 2, bw, bh);
                var hit = button.Translate(new Vector(slide, 0));
                bool enabled = _updating == null || row.Wine;
                bool hover = enabled && Hot(hit);
                Color fill = enabled ? UiTheme.PrimaryFill(hover) : UiTheme.WithAlpha(UiTheme.Text, 30);
                ctx.DrawRectangle(new SolidColorBrush(UiTheme.WithAlpha(fill, (byte)(fill.A * e))), null, hit, bh / 2, bh / 2);
                Txt(action, 15 * u, WizardSheet.Semibold, enabled ? UiTheme.WithAlpha(UiTheme.OnPrimary, alpha) : soft, button, TextAlignment.Center);
                if (enabled) _hits.Add((hit, "row:" + i));
                stateW = bw;
            }
            else
            {
                string state = row.Local ? (row.Installed == null ? (row.Origin == "facoltativo" ? "Non installato (facoltativo)" : "Non trovato") : row.Origin is "installato" or "facoltativo" ? "Installato nel sistema" : "Incluso nell’app") :
                    row.Busy ? (row == _updating ? "Aggiornamento in corso…" : "Verifica in corso…") :
                    row.Error ?? (row.Origin == "Homebrew al riavvio" ? "Al prossimo avvio" :
                        row.Latest == null ? "Non verificabile" :
                        update ? row.Patched ? "Nuova versione da ricompilare" : "Homebrew richiesto" : "Aggiornato");
                stateW = Math.Min(WizardSheet.Measure(state, 15 * u, WizardSheet.Regular) + 2, right - versionLeft);
                Txt(state, 15 * u, WizardSheet.Regular, soft, new Rect(right - stateW, line.Y, stateW, line.Height), TextAlignment.Right);
            }

            // Versione installata e, se c'e', la nuova sulla stessa riga: "0.42.0  → 0.42.0_1".
            double versionW = Math.Max(1, right - stateW - 16 * u - versionLeft);
            string installed = row.Installed ?? "—";
            Txt(installed, 16 * u, WizardSheet.Semibold, ink, new Rect(versionLeft, line.Y, versionW, line.Height));
            if (update)
            {
                double iw = WizardSheet.Measure(installed, 16 * u, WizardSheet.Semibold) + 10 * u;
                Txt("→ " + row.Latest, 16 * u, WizardSheet.Regular, soft, new Rect(versionLeft + iw, line.Y, Math.Max(1, versionW - iw), line.Height));
            }
        }
        ctx.FillRectangle(hairline, new Rect(left + slide, y + _rows.Count * rowH, column.Width, 1));
        string footer = MacEngine.State.Length > 0 ? MacEngine.State : _status;
        if (footer.Length > 0) Txt(footer, 15 * u, WizardSheet.Regular, soft, new Rect(left, y + _rows.Count * rowH + 20 * u, column.Width, 24 * u));
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        e.Handled = true;
        foreach (var (rect, kind) in _hits.AsEnumerable().Reverse())
        {
            if (!rect.Contains(p)) continue;
            if (kind == "close") Close();
            else if (kind.StartsWith("row:", StringComparison.Ordinal) && int.TryParse(kind.AsSpan(4), out int index))
                _ = RunActionAsync(_rows[index]);
            return;
        }
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
