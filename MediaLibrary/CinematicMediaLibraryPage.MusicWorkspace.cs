#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private Control? _musicWorkspaceContent;
        public event Action? MusicWorkspaceNavigationRequested;
        public void SetMusicWorkspaceContent(Control? content)
        {
            if (_musicWorkspaceContent != null && _musicWorkspaceContent != content)
                _musicWorkspaceContent.Visible = false;
            _musicWorkspaceContent = content;
            if (content != null)
            {
                _detailItem = null;
                CloseGroupPicker(invalidate: false);
                content.Parent = this;
                content.Dock = DockStyle.None;
                LayoutMusicWorkspaceContent();
                content.Visible = true;
                content.BringToFront();
                if (_queueEditorVisible) _queueOverlay?.BringToFront();
            }
            Invalidate();
        }

        public (int, int) GetMusicTrackPosition(string path)
        {
            foreach (var album in _items.Where(item => item.IsGroup))
            {
                var tracks = album.Children.OrderBy(item => item.TrackNumber ?? int.MaxValue).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
                int index = tracks.FindIndex(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) return (index+1, tracks.Count);
            }
            return (0, 0);
        }

        private void LayoutMusicWorkspaceContent()
        {
            if (_musicWorkspaceContent == null) return;
            int sidebar = SidebarWidth(ClientRectangle);
            var main = new Rectangle(sidebar, 0, Width - sidebar, Height);
            float scale = LayoutScale(main);
            int pad = Math.Max(S(scale, 32), (int)Math.Round(main.Width * .024));
            int top = S(scale, 78);
            _musicWorkspaceContent.Bounds = new Rectangle(sidebar + pad, top,
                Math.Max(1, main.Width - pad * 2), Math.Max(1, Height - MusicTransportInset - top - S(scale, 20)));
        }
    }
}
