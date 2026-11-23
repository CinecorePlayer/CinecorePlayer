using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// Livello figlio di una vista disegnata a mano (2026-11-21): ha un suo elenco di disegno, quindi invalidarlo
/// ridisegna solo la sua zona e non tutta la vista. La libreria ne usa due: la barra della musica (avanzamento, mouse
/// sulle icone, volume) e le barrette del brano in corso negli elenchi. Prima ognuno di questi aggiornamenti
/// ridisegnava tutta la libreria (banner, griglie, vetro): su una scheda video modesta la musica faceva scattare tutto.
/// Lo stato e il disegno restano nella vista; il livello chiama solo <c>paint</c>.
/// </summary>
internal sealed class PaintLayer : Control
{
    private readonly Action<DrawingContext> _paint;

    public PaintLayer(Action<DrawingContext> paint)
    {
        _paint = paint;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext ctx) => _paint(ctx);
}
