#nullable enable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    /// <summary>Primo avvio: propone di collegare il telefono come telecomando. Una domanda, due pulsanti.</summary>
    internal sealed class RemoteOfferForm : HudModalFormBase
    {
        private readonly bool _english;

        public RemoteOfferForm(bool english)
        {
            _english = english;
            Text = T("Telecomando sul telefono", "Remote on your phone");
            ClientSize = new Size(560, 206);
            MinimumSize = MaximumSize = Size;
            Button later = CreateModalButton(T("Più tardi", "Later"), DialogResult.Cancel, primary: false);
            Button show = CreateModalButton(T("Mostra il codice", "Show the code"), DialogResult.OK, primary: true);
            show.SetBounds(ClientSize.Width - 30 - 160, ClientSize.Height - 58, 160, 38);
            later.SetBounds(show.Left - 10 - 110, ClientSize.Height - 58, 110, 38);
            Controls.Add(later);
            Controls.Add(show);
            AcceptButton = show;
            CancelButton = later;
        }

        private string T(string italian, string english) => _english ? english : italian;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            TextRenderer.DrawText(g, Text, titleFont, new Rectangle(30, 14, ClientSize.Width - 60, 42), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, T("Inquadra un codice con il telefono e lo usi per scegliere i film, mettere in pausa e regolare il volume. Lo trovi sempre nel menu Telecomando.",
                    "Scan a code with your phone and use it to pick films, pause and set the volume. It is always in the Remote menu."),
                bodyFont, new Rectangle(30, 62, ClientSize.Width - 60, 64), Theme.SubtleText,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }
}
