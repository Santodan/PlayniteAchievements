using Windows.Graphics.Capture;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Keeps the capture-border call sites explicit without requesting unsupported WinRT access.
    /// Borderless capture requires the <c>graphicsCaptureWithoutBorder</c> packaged-app capability;
    /// Playnite is an unpackaged desktop host and cannot declare that capability for an extension.
    /// The indicator is drawn by Windows outside the captured pixels, so leaving it enabled does not
    /// affect recordings or screenshots.
    /// </summary>
    internal static class WgcCaptureBorder
    {
        public static void Suppress(GraphicsCaptureSession session)
        {
            // Deliberately a no-op. See the class documentation.
        }
    }
}
