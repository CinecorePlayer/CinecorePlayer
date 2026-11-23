using System;

namespace CinecoreMac;

/// <summary>
/// DVD su Windows: il disco lo legge il DVD Navigator dentro il VideoHost (come il player Windows), qui solo lo stato
/// (dalle righe "dvd ...") e i comandi del telecomando: frecce, OK, Indietro, menu, clic e passaggio del mouse.
/// </summary>
internal sealed class WinDvd
{
    private readonly Action<string> _send;
    public WinDvd(Action<string> send) { _send = send; }

    public string Domain { get; private set; } = "start";
    public int Title { get; private set; }
    public int Chapter { get; private set; }
    public int Buttons { get; private set; }
    public bool InMenu => Domain is "menu" || Domain is not "title" && Buttons > 0;
    public bool InTitle => Domain == "title";

    /// <summary>Riga "dvd dominio titolo capitolo pulsanti gruppo" del VideoHost.</summary>
    public void Update(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 5 || p[1] == "clicked") return;
        Domain = p[1];
        int.TryParse(p[2], out int title); int.TryParse(p[3], out int chapter); int.TryParse(p[4], out int buttons);
        Title = title; Chapter = chapter; Buttons = buttons;
    }

    public void Menu(string which = "root") => _send("dvd menu " + which);
    public void Resume() => _send("dvd resume");
    public void Move(string direction) => _send("dvd move " + direction);
    public void Activate() => _send("dvd activate");
    public void Back() => _send("dvd back");
    public void StepChapter(int direction) => _send(direction < 0 ? "dvd chapter -1" : "dvd chapter +1");

    /// <summary>Clic sul video: il Navigator vuole la posizione sullo schermo (la mappa lui attraverso il renderer).</summary>
    public bool ClickAtCursor()
    {
        if (Buttons <= 0 || !WinNative.GetCursorPos(out var p)) return false;
        _send($"dvd click {p.X} {p.Y}");
        return true;
    }

    public void HoverAtCursor()
    {
        if (Buttons > 0 && WinNative.GetCursorPos(out var p)) _send($"dvd hover {p.X} {p.Y}");
    }
}
