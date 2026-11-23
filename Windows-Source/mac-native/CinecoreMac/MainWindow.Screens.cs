using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Schermo del player: dove si apre all'avvio (Impostazioni › Generali e telecomando) e spostamento al volo su un
/// altro schermo, anche a schermo intero. "startScreen": "" = ultima posizione, "active" = schermo del mouse,
/// altrimenti la chiave di uno schermo (nome|larghezza|altezza: i numeri DISPLAYn di Windows cambiano).
/// </summary>
public sealed partial class MainWindow
{
    internal sealed record ScreenChoice(string Key, string Label, bool Primary, bool Current);

    internal static string ScreenKey(Screen s) => (s.DisplayName ?? "") + "|" + s.Bounds.Width + "|" + s.Bounds.Height;

    internal List<ScreenChoice> ScreenChoices()
    {
        var list = new List<ScreenChoice>();
        try
        {
            var current = Screens.ScreenFromWindow(this);
            int n = 0;
            foreach (var s in Screens.All)
            {
                n++;
                string name = string.IsNullOrWhiteSpace(s.DisplayName) || s.DisplayName!.StartsWith(@"\\.\", StringComparison.Ordinal) ? L10n.T("Schermo") + " " + n : s.DisplayName!;
                list.Add(new ScreenChoice(ScreenKey(s), $"{name} · {s.Bounds.Width}×{s.Bounds.Height}" + (s.IsPrimary ? " · " + L10n.T("principale") : ""), s.IsPrimary,
                    current != null && current.Bounds == s.Bounds));
            }
        }
        catch { }
        return list;
    }

    internal static string StartScreen
    {
        get => MacSettings.Get("startScreen", "");
        set => MacSettings.Set("startScreen", value);
    }

    internal string StartScreenLabel => StartScreen switch
    {
        "" => "Ultima posizione",
        "active" => "Schermo attivo",
        string key => ScreenChoices().FirstOrDefault(c => c.Key == key)?.Label ?? "Schermo non collegato",
    };

    private Screen? FindScreen(string key)
    {
        var all = Screens.All.ToList();
        return all.FirstOrDefault(s => ScreenKey(s) == key)
            // Nome cambiato (driver) ma stessa risoluzione: meglio quello che la posizione di ieri.
            ?? all.FirstOrDefault(s => key.EndsWith("|" + s.Bounds.Width + "|" + s.Bounds.Height, StringComparison.Ordinal));
    }

    /// <summary>Nel costruttore, dopo la posizione salvata: lo schermo scelto per l'avvio vince.</summary>
    private void ApplyStartScreen()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCREEN") != null || Environment.GetEnvironmentVariable("CINECORE_QA_RECT") != null) return;
        string choice = StartScreen;
        if (choice.Length == 0) return;
        Screen? target = null;
        try
        {
            if (choice == "active")
            {
#if CINECORE_WINDOWS
                if (WinNative.GetCursorPos(out var p)) target = Screens.All.FirstOrDefault(s => s.Bounds.Contains(new PixelPoint(p.X, p.Y)));
#endif
            }
            else target = FindScreen(choice);
        }
        catch { }
        if (target != null) PlaceOnScreen(target, center: true);
    }

    private void PlaceOnScreen(Screen screen, bool center)
    {
        var area = screen.WorkingArea;
        double scale = OperatingSystem.IsMacOS() ? 1 : Math.Max(1, screen.Scaling);
        double w = Math.Min(Math.Max(WindowMinWidth, Width > 0 ? Width : 1280), area.Width / scale * .92);
        double h = Math.Min(Math.Max(WindowMinHeight, Height > 0 ? Height : 720), area.Height / scale * .92);
        Width = w; Height = h;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(area.X + (int)((area.Width - w * scale) / 2), area.Y + (int)((area.Height - h * scale) / 2));
    }

    /// <summary>Sposta il player sullo schermo scelto; a schermo intero ci resta (esce, si sposta, rientra).</summary>
    internal bool MoveToScreen(string key)
    {
        var target = FindScreen(key);
        if (target == null) return false;
        var now = Screens.ScreenFromWindow(this);
        if (now != null && now.Bounds == target.Bounds) return true;
        bool full = IsFullscreen, maximized = WindowState == WindowState.Maximized;
        if (full || maximized) WindowState = WindowState.Normal;
        Dispatcher.UIThread.Post(() =>
        {
            PlaceOnScreen(target, center: true);
            if (full) DispatcherTimer.RunOnce(() => WindowState = WindowState.FullScreen, TimeSpan.FromMilliseconds(120));
            else if (maximized) DispatcherTimer.RunOnce(() => WindowState = WindowState.Maximized, TimeSpan.FromMilliseconds(120));
            _hud.Poke();
        }, DispatcherPriority.Background);
        return true;
    }
}
