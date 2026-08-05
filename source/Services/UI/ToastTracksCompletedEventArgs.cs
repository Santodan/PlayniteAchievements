using System;
using System.Collections.Generic;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Raised once per wave after the slide-out finishes, carrying the recorded overlay track of
    /// every toasted item. The recording service matches tracks to pending clip requests and
    /// composites each into its achievement's clip at export.
    /// </summary>
    internal sealed class ToastTracksCompletedEventArgs : EventArgs
    {
        public ToastTracksCompletedEventArgs(IReadOnlyList<ToastOverlayTrack> tracks)
        {
            Tracks = tracks;
        }

        public IReadOnlyList<ToastOverlayTrack> Tracks { get; }
    }
}
