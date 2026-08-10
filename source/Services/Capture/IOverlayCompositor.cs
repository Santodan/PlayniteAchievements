using System;
using System.Drawing;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Blends a recorded toast card into one decoded video frame at clip export, returning a fresh
    /// sample — a decoded sample's buffer may be a detached copy, so mutating it in place is not
    /// reliable. Where the pixels are touched (system memory or a D3D11 texture) is the implementation's
    /// business; the geometry and the overlay bytes are worked out once by the caller either way.
    /// </summary>
    internal interface IOverlayCompositor : IDisposable
    {
        /// <summary>
        /// Returns <paramref name="source"/> with the premultiplied-BGRA <paramref name="overlay"/>
        /// blended in at <paramref name="destRect"/>, or null when this compositor cannot handle the
        /// sample — the caller then passes the frame through without a card rather than failing the
        /// clip. Sample time and duration are stamped by the caller.
        /// </summary>
        Sample Compose(Sample source, byte[] overlay, int overlayW, int overlayH, Rectangle destRect);
    }
}
