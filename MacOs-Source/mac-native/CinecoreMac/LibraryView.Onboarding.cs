using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

// Guida rapida del primo avvio (CinematicMediaLibraryPage.Onboarding.cs): quattro passi sopra la libreria, nello stile
// della barra (solo testo, una pillola chiara per l'azione). Benvenuto, cartelle, chiave TMDb, funzioni in piu'.
// "Salta" sempre disponibile; Esc salta, Invio/Spazio/destra avanti, sinistra indietro. Si riapre da Opzioni.
internal sealed partial class LibraryView
{
    private int _onboardingStep = -1;
    private DateTime _onboardingStepAt;
    private const int OnboardingSteps = 5;
    private DispatcherTimer? _onboardingTimer;

    public bool OnboardingActive => _onboardingStep >= 0;

    private static string OnboardingMarker => Path.Combine(LibraryStore.AppDataDir, "onboarding.txt");

    /// <summary>Primo avvio (PlayerForm.StartupReady): niente guida se gia' vista o se l'installazione non e' nuova.</summary>
    public void MaybeStartOnboarding()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") is { Length: > 0 } && Environment.GetEnvironmentVariable("CINECORE_QA_ONBOARDING") != "1") return;
        try
        {
            if (File.Exists(OnboardingMarker)) return;
            bool olderInstall = new[] { "extras.json", "cinematicLibraryRoots.json", "libraryRoots.json" }
                .Select(name => Path.Combine(LibraryStore.AppDataDir, name))
                .Any(p => File.Exists(p) && DateTime.UtcNow - File.GetCreationTimeUtc(p) > TimeSpan.FromMinutes(20));
            if (olderInstall && Environment.GetEnvironmentVariable("CINECORE_QA_ONBOARDING") != "1") { MarkOnboardingSeen(); return; }
        }
        catch { return; }
        StartOnboarding();
    }

    private static void MarkOnboardingSeen()
    {
        try { Directory.CreateDirectory(LibraryStore.AppDataDir); File.WriteAllText(OnboardingMarker, DateTime.UtcNow.ToString("O")); } catch { }
    }

    public void StartOnboarding()
    {
        CloseMenu();
        SetOnboardingStep(0);
    }

    private void SetOnboardingStep(int step)
    {
        _onboardingStep = step;
        _onboardingStepAt = DateTime.UtcNow;
        _onboardingTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            InvalidateVisual();
            if (_onboardingStep < 0 || (DateTime.UtcNow - _onboardingStepAt).TotalMilliseconds > 420) _onboardingTimer!.Stop();
        });
        _onboardingTimer.Start();
        InvalidateVisual();
    }

    private void FinishOnboarding()
    {
        if (_onboardingStep < 0) return;
        _onboardingStep = -1;
        MarkOnboardingSeen();
        InvalidateVisual();
    }

    internal void TestOnboardingNext() => OnboardingNext();

    private void OnboardingNext()
    {
        if (_onboardingStep >= OnboardingSteps - 1) FinishOnboarding();
        else SetOnboardingStep(_onboardingStep + 1);
    }

    private bool HandleOnboardingKey(KeyEventArgs e)
    {
        if (_onboardingStep < 0) return false;
        if (e.Key == Key.Escape) FinishOnboarding();
        else if (e.Key is Key.Enter or Key.Right or Key.Space) OnboardingNext();
        else if (e.Key == Key.Left && _onboardingStep > 0) SetOnboardingStep(_onboardingStep - 1);
        e.Handled = true;
        return true;
    }

    private bool OnboardingActivate(Hit hit)
    {
        if (_onboardingStep < 0) return false;
        switch (hit.Key)
        {
            case "next": OnboardingNext(); break;
            case "skip": FinishOnboarding(); break;
            // L'azione apre la sua finestra; la guida resta al suo passo, come su Windows.
            case "add": OpenFolders(); break;
            case "settings": SettingsRequested?.Invoke(); break;
        }
        return true;
    }

    private void DrawOnboarding(DrawingContext ctx, Rect page)
    {
        if (_onboardingStep < 0) return;
        double t = Math.Clamp((DateTime.UtcNow - _onboardingStepAt).TotalMilliseconds / 380.0, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);
        bool light = UiTheme.IsLight;
        // La libreria resta appena visibile dietro: la guida parla di lei.
        ctx.FillRectangle(B(A(Back, light ? 242 : 244)), page);
        // Durante la guida conta solo lei.
        _hitFloor = _hits.Count;

        double width = Math.Min(page.Width - S(80), S(720));
        double left = page.X + (page.Width - width) / 2;
        (string Title, string Body, string? Action, string? ActionKey) step = _onboardingStep switch
        {
            0 => ("Benvenuto in Cinecore", "Film, serie, musica e foto in un posto solo. Quattro passi veloci e sei pronto.", null, null),
            1 => ("Aggiungi le tue cartelle",
                  "Scegli dove tieni film, serie TV e musica. Cinecore riconosce i file da solo e li ordina; puoi aggiungerne altre quando vuoi da Opzioni in alto.",
                  "Aggiungi cartella", "add"),
            2 => ("Copertine e trame da TMDb",
                  "Con una chiave gratuita di TMDb arrivano locandine, sfondi, trame e cast. Crea un account su themoviedb.org, genera la chiave API dalle impostazioni del tuo profilo e incollala in Impostazioni › Generali › Metadati.",
                  "Apri le impostazioni", "settings"),
            3 => ("Il menu del tasto destro",
                  "Quasi tutto passa da qui. Clic destro (o due dita sul trackpad) in un punto vuoto: apri file, dischi e link, e durante la riproduzione audio, sottotitoli, video, strumenti e dispositivi. Su un film, una serie o un brano: riproduci, metti in coda, aggiungi a una playlist, correggi titolo e copertina.",
                  null, null),
            _ => ("Altre cose che puoi fare", "", null, null),
        };

        bool last = _onboardingStep == OnboardingSteps - 1;
        double y = page.Y + Math.Max(S(100), (page.Height - S(last ? 560 : 420)) / 2);
        double slide = Math.Round(S(14) * (1 - e));
        byte alpha = (byte)Math.Round(255 * e);
        Color ink = A(Text, alpha), soft = A(Text, (byte)(alpha * 0.72));

        // Punti del passo, in alto.
        double dot = S(7), stepGap = S(16);
        double dotsX = page.X + (page.Width - (OnboardingSteps * stepGap - (stepGap - dot))) / 2;
        for (int i = 0; i < OnboardingSteps; i++)
            ctx.DrawEllipse(B(A(Text, i == _onboardingStep ? 235 : 70)), null, new Rect(dotsX + i * stepGap, y, dot, dot));
        y += S(40);

        var title = Format(step.Title, Math.Max(20, 30 * _s), DisplaySemibold, ink, width, 2, TextAlignment.Center);
        ctx.DrawText(title, new Point(left, y + slide));
        y += S(64);
        double bodyPt = Math.Max(10.5, 13 * _s);
        if (!last)
        {
            var body = Format(step.Body, bodyPt, DisplayRegular, soft, width, 5, TextAlignment.Center);
            ctx.DrawText(body, new Point(left, y + slide));
            y += body.Height + S(40);
        }
        else
        {
            // Le funzioni meno evidenti, una per riga: nome a sinistra, cosa fa a destra.
            var rows = new (string Name, string What)[]
            {
                ("Rete", "Jellyfin, Emby e server DLNA: scegli la fonte dal menu in alto"),
                ("Ricerca", "La lente in alto cerca in tutta la libreria"),
                ("Copertine", "Tasto destro su un film › Correggi titolo e copertina"),
                ("Riprendi", "Continua a guardare riparte da dove avevi lasciato"),
                ("Schermo intero", "Il pulsante verde in alto a sinistra della finestra"),
            };
            double namePt = Math.Max(10.5, 13 * _s);
            double nameW = rows.Max(r => Measure(r.Name, namePt, DisplaySemibold)) + S(36);
            double rowH = S(44);
            foreach (var row in rows)
            {
                TextAt(ctx, row.Name, namePt, DisplaySemibold, ink, new Rect(left, y + slide, nameW, rowH));
                TextAt(ctx, row.What, bodyPt, DisplayRegular, soft, new Rect(left + nameW, y + slide, width - nameW, rowH));
                y += rowH;
            }
            y += S(32);
        }

        // Pulsanti: l'azione del passo (pillola chiara) e "Avanti"/"Inizia"; o solo "Avanti" pieno se non c'e' azione.
        double h = S(48), buttonPt = Math.Max(9.4, 11.8 * _s);
        string nextText = last ? "Inizia" : "Avanti";
        double Width(string text) => Measure(text, buttonPt, DisplaySemibold) + S(56);
        Color onPrimary = light ? Colors.White : Colors.Black;
        if (step.Action != null)
        {
            double aw = Width(step.Action), nw = Width(nextText), total = aw + S(16) + nw;
            var action = new Rect(page.X + (page.Width - total) / 2, y, aw, h);
            var next = new Rect(action.Right + S(16), y, nw, h);
            DrawHeroButtonShape(ctx, action, true, Hover(action));
            TextAt(ctx, step.Action, buttonPt, DisplaySemibold, onPrimary, action, TextAlignment.Center);
            DrawHeroButtonShape(ctx, next, false, Hover(next));
            TextAt(ctx, nextText, buttonPt, DisplaySemibold, Text, next, TextAlignment.Center);
            _hits.Add(new Hit(action, "onboarding", step.ActionKey!));
            _hits.Add(new Hit(next, "onboarding", "next"));
        }
        else
        {
            double nw = Width(nextText);
            var next = new Rect(page.X + (page.Width - nw) / 2, y, nw, h);
            DrawHeroButtonShape(ctx, next, true, Hover(next));
            TextAt(ctx, nextText, buttonPt, DisplaySemibold, onPrimary, next, TextAlignment.Center);
            _hits.Add(new Hit(next, "onboarding", "next"));
        }

        // "Salta" in alto a destra, sempre (sotto la barra dei pulsanti della finestra).
        if (!last)
        {
            double smallPt = Math.Max(9.5, 11.4 * _s);
            double sw = Measure("Salta", smallPt, DisplayRegular);
            var skip = new Rect(page.Right - PadX - sw, page.Y + S(26) + Math.Round(TopShift), sw, S(28));
            var skipHit = skip.Inflate(new Thickness(S(12), S(8)));
            TextAt(ctx, "Salta", smallPt, DisplayRegular, A(Text, Hover(skipHit) ? (byte)255 : (byte)165), skip);
            _hits.Add(new Hit(skipHit, "onboarding", "skip"));
        }
    }
}
