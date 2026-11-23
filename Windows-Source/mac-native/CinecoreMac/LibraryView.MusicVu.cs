using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// VU meter del brano a schermo intero, ridisegnati puliti e moderni (14/11, su richiesta: poi lo stesso su Windows).
// Misura e balistica restano quelle di HUD/MusicFullscreenView.Vu.cs: IEC 60268-17 (secondo ordine, 99% in ~300 ms con un
// filo di sovraelongazione), taratura automatica sulla loudness del brano, livello dall'analisi del motore (dopo il DSP,
// prima del volume: descrive il brano, non la manopola). Il disegno e' in fondo al file.
internal sealed partial class LibraryView
{
    private readonly double[] _vuRms = new double[2];
    private readonly double[] _vuPos = { 0, 0 }, _vuVel = new double[2];
    private double _vuEnergyL, _vuEnergyR, _vuEnergyFrames = -1;
    private double _vuReference = -14, _vuReferenceTarget = -14; // dBFS a 0 VU
    private bool _vuSilent = true;
    private long _vuLastDraw;
    private readonly long[] _vuPeakUntil = new long[2];


    private void FeedVu(LoopbackSampler.AudioMetrics m, long now)
    {
        // Media quadratica di tutto l'audio arrivato dall'ultima misura (non solo l'ultimo blocco).
        double frames = m.EnergyFrames - _vuEnergyFrames;
        if (_vuEnergyFrames >= 0 && frames > 0 && m.EnergyL >= _vuEnergyL && m.EnergyR >= _vuEnergyR)
        {
            _vuRms[0] = Math.Sqrt((m.EnergyL - _vuEnergyL) / frames);
            _vuRms[1] = Math.Sqrt((m.EnergyR - _vuEnergyR) / frames);
        }
        else { _vuRms[0] = Math.Max(0, m.RmsL); _vuRms[1] = Math.Max(0, m.RmsR); }
        _vuEnergyL = m.EnergyL; _vuEnergyR = m.EnergyR; _vuEnergyFrames = m.EnergyFrames;
        _vuSilent = m.IsSilent;
        // Media del brano circa a -3 VU: i passaggi forti arrivano a 0 / +1 come su un banco tarato a orecchio.
        double loud = double.IsFinite(m.LufsI) && m.LufsI > -70 ? m.LufsI : double.IsFinite(m.LufsS) && m.LufsS > -70 ? m.LufsS : double.NaN;
        if (double.IsFinite(loud)) _vuReferenceTarget = Math.Clamp(Math.Round(loud + 6), -34, -4);
        if (Db(m.PeakL) > -1 || (double.IsFinite(m.DbTpL) && m.DbTpL > -1)) _vuPeakUntil[0] = now + 1200;
        if (Db(m.PeakR) > -1 || (double.IsFinite(m.DbTpR) && m.DbTpR > -1)) _vuPeakUntil[1] = now + 1200;
    }

    /// <summary>Deflessione 0..1.08 per un livello in VU (1 = +3 VU, fondo scala).</summary>
    private static double VuDeflection(double vu) => Math.Clamp(Math.Pow(10, (vu - 3) / 20), 0, 1.08);

    private double VuTargetDeflection(int channel)
    {
        double rms = _vuRms[channel];
        if (_vuSilent || !_audio.IsPlaying || rms <= 1e-7) return 0;
        double vu = 20 * Math.Log10(rms) + 3.01 - _vuReference; // AES17: sinusoide a fondo scala = 0 dBFS
        return VuDeflection(vu);
    }

    /// <summary>Un passo della balistica degli aghi (dal giro dei fotogrammi): true se serve un altro fotogramma.</summary>
    private bool StepVu()
    {
        if (_fsTab != TabVu) { _vuLastDraw = 0; return false; }
        FeedCharts();
        long now = Environment.TickCount64;
        double dt = _vuLastDraw > 0 ? Math.Clamp((now - _vuLastDraw) / 1000.0, 0, 0.1) : 0.016;
        _vuLastDraw = now;
        double maxMove = 6 * dt; // la taratura si sposta piano: nessun salto dell'ago fra i brani
        _vuReference += Math.Clamp(_vuReferenceTarget - _vuReference, -maxMove, maxMove);
        int steps = Math.Max(1, (int)Math.Round(dt * 1000));
        double h = dt / steps;
        for (int c = 0; c < 2; c++)
        {
            double target = VuTargetDeflection(c);
            for (int i = 0; i < steps; i++)
            {
                // Equipaggio mobile smorzato: un po' piu' svelto in salita che al ritorno, come gli strumenti veri.
                double omega = target > _vuPos[c] ? 23 : 17;
                _vuVel[c] += (omega * omega * (target - _vuPos[c]) - 2 * 0.78 * omega * _vuVel[c]) * h;
                _vuPos[c] = Math.Clamp(_vuPos[c] + _vuVel[c] * h, -0.02, 1.1);
            }
        }
        return _audio.IsPlaying || Math.Abs(_vuPos[0] - VuTargetDeflection(0)) > 0.002 || Math.Abs(_vuPos[1] - VuTargetDeflection(1)) > 0.002 ||
            Math.Abs(_vuVel[0]) > 0.01 || Math.Abs(_vuVel[1]) > 0.01 || Math.Abs(_vuReference - _vuReferenceTarget) > 0.05 ||
            now < Math.Max(_vuPeakUntil[0], _vuPeakUntil[1]) + 200;
    }

    // ---------------------------------------------------------------- disegno: VU veri, aspetto moderno
    // Strumenti ad ago come quelli di Windows (stessa scala VU -20 ... +3 in tensione, stessa balistica), ma senza la
    // stampa retro': quadrante di vetro chiaro appena accennato con la luce sui bordi, scala bianca sottile, zona rossa
    // sottile sopra lo zero, ago bianco con un'ombra leggera; canale, spia di picco e valore piccoli in basso.
    private const double VuSweep = 47; // gradi per lato
    private const double VuW = 400, VuH = 236, VuPivotX = 200, VuPivotY = 262, VuRadius = 200;
    private const double VuMin = -20, VuMax = 3;

    private static double VuFromDeflection(double d) => d <= 0.001 ? VuMin : Math.Clamp(20 * Math.Log10(d) + 3, VuMin, VuMax);

    private static Point VuPolar(double radius, double deflection)
    {
        double angle = (-VuSweep + Math.Clamp(deflection, -0.02, 1.1) * 2 * VuSweep) * Math.PI / 180;
        return new Point(VuPivotX + Math.Sin(angle) * radius, VuPivotY - Math.Cos(angle) * radius);
    }

    private static StreamGeometry VuArc(double radius, double from, double to)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(to - from) * 120));
        g.BeginFigure(VuPolar(radius, from), false);
        for (int i = 1; i <= n; i++) g.LineTo(VuPolar(radius, from + (to - from) * i / n));
        g.EndFigure(false);
        return geo;
    }

    /// <summary>Due strumenti affiancati (impilati se l'area e' stretta), centrati nell'area.</summary>
    private void DrawVuPair(DrawingContext ctx, Rect r, double alpha)
    {
        const double Aspect = VuW / VuH;
        double gap = FU(28);
        bool sideBySide = r.Width >= r.Height * 1.4;
        double w, h;
        if (sideBySide) { w = Math.Min((r.Width - gap) / 2, r.Height * 0.8 * Aspect); h = w / Aspect; }
        else { h = Math.Min((r.Height - gap) / 2, r.Width * .9 / Aspect); w = h * Aspect; }
        if (w < 40 || h < 24) return;
        double totalW = sideBySide ? w * 2 + gap : w, totalH = sideBySide ? h : h * 2 + gap;
        double x0 = r.X + (r.Width - totalW) / 2, y0 = r.Y + Math.Max(0, (r.Height - totalH) / 2);
        for (int c = 0; c < 2; c++)
        {
            var face = sideBySide ? new Rect(x0 + c * (w + gap), y0, w, h) : new Rect(x0, y0 + c * (h + gap), w, h);
            DrawVuMeter(ctx, face, c, alpha);
        }
    }

    private void DrawVuMeter(DrawingContext ctx, Rect face, int channel, double alpha)
    {
        byte A(double v) => (byte)Math.Clamp(Math.Round(v * alpha), 0, 255);
        double u = face.Width / VuW;
        long now = Environment.TickCount64;
        var hot = Color.FromRgb(255, 105, 95);
        double radius = 22 * u;
        // Quadrante: vetro chiaro appena accennato, luce sui bordi.
        ctx.DrawRectangle(B(W(A(UiTheme.IsLight ? 60 : 14))), null, face, radius, radius);
        using (ctx.PushOpacity(alpha)) DrawGlassRim(ctx, face, radius, UiTheme.IsLight);
        using var clip = ctx.PushGeometryClip(new RectangleGeometry(face, radius, radius));
        using var transform = ctx.PushTransform(Matrix.CreateScale(u, u) * Matrix.CreateTranslation(face.X, face.Y));
        double zero = VuDeflection(0);
        // Scala: arco bianco fino allo zero, rosso sopra; sotto, una banda rossa tenue.
        ctx.DrawGeometry(null, new Pen(B(Color.FromArgb(A(60), hot.R, hot.G, hot.B)), 7), VuArc(VuRadius + 6, zero, 1.0));
        ctx.DrawGeometry(null, new Pen(B(W(A(200))), 1.4), VuArc(VuRadius, 0, zero));
        ctx.DrawGeometry(null, new Pen(B(Color.FromArgb(A(235), hot.R, hot.G, hot.B)), 1.4), VuArc(VuRadius, zero, 1.0));
        double[] major = { -20, -10, -7, -5, -3, -2, -1, 0, 1, 2, 3 };
        double[] minor = { -15, -9, -8, -6, -4, -2.5, -1.5, -0.5, 0.5, 1.5, 2.5 };
        foreach (double vu in minor)
        {
            double d = VuDeflection(vu);
            ctx.DrawLine(new Pen(B(vu > 0 ? Color.FromArgb(A(180), hot.R, hot.G, hot.B) : W(A(110))), 1), VuPolar(VuRadius, d), VuPolar(VuRadius + 7, d));
        }
        foreach (double vu in major)
        {
            double d = VuDeflection(vu);
            ctx.DrawLine(new Pen(B(vu > 0 ? Color.FromArgb(A(240), hot.R, hot.G, hot.B) : W(A(220))), 1.6), VuPolar(VuRadius, d), VuPolar(VuRadius + 13, d));
            string text = vu == -20 ? "−20" : vu == 3 ? "+3" : Math.Abs(vu).ToString(CultureInfo.InvariantCulture);
            var ft = Ft(text, 15, DisplayRegular, vu > 0 ? Color.FromArgb(A(240), hot.R, hot.G, hot.B) : W(A(vu == 0 ? 240 : 170)));
            var p = VuPolar(VuRadius + 28, d);
            ctx.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
        }
        var unit = Ft("VU", 20, DisplaySemibold, W(A(150)));
        ctx.DrawText(unit, new Point(VuPivotX - unit.Width / 2, 128));
        // Ago: ombra leggera, poi la lancetta; nasce sotto il bordo inferiore (perno nascosto).
        double pos = _vuPos[channel];
        var tip = VuPolar(VuRadius + 10, pos);
        var root = VuPolar(52, pos);
        ctx.DrawLine(new Pen(B(Color.FromArgb(A(70), 0, 0, 0)), 3), new Point(root.X + 2, root.Y + 1.5), new Point(tip.X + 2, tip.Y + 1.5));
        ctx.DrawLine(new Pen(B(W(A(250))), 1.8, lineCap: PenLineCap.Round), root, tip);
        // In basso: canale a sinistra, valore a destra, spia di picco accanto al canale.
        var name = Ft(channel == 0 ? "L" : "R", 16, DisplaySemibold, W(A(190)));
        ctx.DrawText(name, new Point(22, VuH - 20 - name.Height / 2));
        bool lit = now < _vuPeakUntil[channel];
        ctx.DrawEllipse(B(lit ? Color.FromArgb(A(255), hot.R, hot.G, hot.B) : W(A(40))), null, new Point(22 + name.Width + 12, VuH - 20), 4, 4);
        double vuNow = VuFromDeflection(pos);
        string value = pos <= 0.002 ? "—" : vuNow.ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture) + " VU";
        var vt = Ft(value, 14, DisplayRegular, vuNow > 0 && pos > 0.002 ? Color.FromArgb(A(240), hot.R, hot.G, hot.B) : W(A(150)));
        ctx.DrawText(vt, new Point(VuW - 22 - vt.Width, VuH - 20 - vt.Height / 2));
    }
}
