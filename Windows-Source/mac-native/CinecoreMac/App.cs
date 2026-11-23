using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.Linq;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;

namespace CinecoreMac;

public sealed class App : Application
{
    public override void Initialize()
    {
#if CINECORE_WINDOWS
        Name = "Cinecore Player 2026";
#else
        Name = "Cinecore Player";
#endif
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? file = desktop.Args is { Length: > 0 } a ? a[0] : null;
            var window = new MainWindow(file);
            desktop.MainWindow = window;
            // File aperti da Finder ("Apri con", trascinati sull'icona): su macOS arrivano come evento, non come argomento.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs f)
                    {
                        var paths = f.Files.Select(x => x.TryGetLocalPath()).OfType<string>().ToList();
                        if (paths.Count > 0) window.OpenFiles(paths);
                    }
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
