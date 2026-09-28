using System.Collections.Generic;

namespace CinecorePlayer2025.Utilities
{
    internal sealed class PlaybackQueueViewItem
    {
        public string Path { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int Index { get; set; }
        public bool IsCurrent { get; set; }
    }

    internal sealed class RemoteLibraryCategoryView
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<RemoteLibraryItemView> Items { get; set; } = new();
    }

    internal sealed class RemoteLibraryItemView
    {
        public string Path { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string ArtPath { get; set; } = string.Empty;
        public string Kind { get; set; } = "item";
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public List<RemoteLibraryItemView> Children { get; set; } = new();
    }
}
