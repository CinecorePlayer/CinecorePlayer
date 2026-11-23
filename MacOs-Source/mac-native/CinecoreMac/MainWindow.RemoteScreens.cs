using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CinecoreMac;

// Telecomando › Impostazioni › Schermo: dove si apre il player e spostamento immediato su un altro schermo
// (stessi comandi di Windows: "screen" e "start_screen").
public sealed partial class MainWindow
{
    /// <summary>"" = ultima posizione, "active" = schermo del mouse, altrimenti il nome dello schermo.</summary>
    private static string StartScreen
    {
        get => MacSettings.Get("startScreen", "");
        set => MacSettings.Set("startScreen", value ?? "");
    }

    private List<(string Key, string Label, bool Primary, bool Current)> ScreenChoices()
    {
        var all = MacScreens.All();
        string? here = ScreenOfWindow()?.Name;
        return all.Select((s, i) => (s.Name, $"{s.Name} · {s.Width:0}×{s.Height:0}", i == 0, s.Name == here)).ToList();
    }

    private static MacScreens.Screen? ScreenByKey(string key) =>
        key == "active" ? MacScreens.MouseScreen() : key.Length == 0 ? null : MacScreens.All().Find(s => s.Name == key);

    /// <summary>Sposta la finestra (anche a schermo intero: esce, si sposta, rientra) al centro dello schermo scelto.</summary>
    private void MoveToScreen(string key)
    {
        if (ScreenByKey(key) is not { } target || ScreenOfWindow()?.Name == target.Name) return;
        bool wasFull = IsFullscreen;
        void Move()
        {
            double w = Math.Min(Bounds.Width, target.VisibleWidth), h = Math.Min(Bounds.Height, target.VisibleHeight);
            Width = w; Height = h;
            Position = new PixelPoint((int)(target.VisibleX + (target.VisibleWidth - w) / 2), (int)(target.VisibleY + (target.VisibleHeight - h) / 2));
            if (wasFull) DispatcherTimer.RunOnce(() => { if (!IsFullscreen) WindowState = WindowState.FullScreen; }, TimeSpan.FromMilliseconds(150));
        }
        Console.Error.WriteLine($"[schermo] telecomando: finestra su \"{target.Name}\"");
        if (wasFull) { WindowState = WindowState.Normal; DispatcherTimer.RunOnce(Move, FullscreenAnimation); }
        else Move();
    }

    /// <summary>All'avvio: lo schermo scelto dal telefono, se c'e' (altrimenti resta l'ultima posizione).</summary>
    private void PlaceOnStartScreen()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCREEN") != null || ScreenByKey(StartScreen) is not { } target) return;
        double w = Math.Min(Width > 0 ? Width : 1280, target.VisibleWidth), h = Math.Min(Height > 0 ? Height : 760, target.VisibleHeight);
        Width = w; Height = h;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)(target.VisibleX + (target.VisibleWidth - w) / 2), (int)(target.VisibleY + (target.VisibleHeight - h) / 2));
    }

    /// <summary>Chiave dei metadati salvati: sul Mac e' il percorso stesso.</summary>
    private static string MetadataKey(string path) => path;
}
