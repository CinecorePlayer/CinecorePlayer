using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CinecoreMac;

// Impostazioni › Riproduzione › Schermo dei film: il film si apre a schermo intero sullo schermo scelto (es. il TV o il
// proiettore) e alla chiusura la finestra torna dov'era, com'era.
public sealed partial class MainWindow
{
    private (PixelPoint Position, Size Size, bool Fullscreen)? _beforeFilmScreen;
    // Durata dell'animazione di macOS per entrare/uscire dallo schermo intero.
    private static readonly TimeSpan FullscreenAnimation = TimeSpan.FromMilliseconds(900);

    private MacScreens.Screen? ScreenOfWindow()
    {
        double cx = Position.X + Bounds.Width / 2, cy = Position.Y + Bounds.Height / 2;
        return MacScreens.All().Find(s => cx >= s.X && cx < s.X + s.Width && cy >= s.Y && cy < s.Y + s.Height);
    }

    private void MoveToFilmScreen()
    {
        if (MacScreens.FilmScreen() is not { } target) return;
        bool onTarget = ScreenOfWindow()?.Name == target.Name;
        if (onTarget && IsFullscreen) return;
        _beforeFilmScreen ??= (Position, new Size(Bounds.Width, Bounds.Height), IsFullscreen);
        Console.Error.WriteLine($"[schermo] film su \"{target.Name}\"");
        void MoveAndFill()
        {
            if (!onTarget)
            {
                double w = Math.Min(Bounds.Width, target.VisibleWidth), h = Math.Min(Bounds.Height, target.VisibleHeight);
                Width = w; Height = h;
                Position = new PixelPoint((int)(target.VisibleX + (target.VisibleWidth - w) / 2), (int)(target.VisibleY + (target.VisibleHeight - h) / 2));
            }
            DispatcherTimer.RunOnce(() => { if (_file != null && !IsFullscreen) { BeginFullscreenTransition(); WindowState = WindowState.FullScreen; } }, TimeSpan.FromMilliseconds(150));
        }
        if (IsFullscreen && !onTarget)
        {
            // Uno schermo intero non si sposta: si esce, si sposta la finestra, si rientra sull'altro schermo.
            WindowState = WindowState.Normal;
            DispatcherTimer.RunOnce(MoveAndFill, FullscreenAnimation);
        }
        else MoveAndFill();
    }

    private void RestoreFromFilmScreen()
    {
        if (_beforeFilmScreen is not { } before) return;
        _beforeFilmScreen = null;
        void Back()
        {
            Width = before.Size.Width; Height = before.Size.Height;
            Position = before.Position;
            if (before.Fullscreen) DispatcherTimer.RunOnce(() => { if (_file == null) WindowState = WindowState.FullScreen; }, TimeSpan.FromMilliseconds(150));
        }
        if (IsFullscreen) { WindowState = WindowState.Normal; DispatcherTimer.RunOnce(Back, FullscreenAnimation); }
        else Back();
    }
}
