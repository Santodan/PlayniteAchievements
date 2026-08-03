using System;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// One-slot handoff of the current on-screen notification overlay from the toast pipeline to the
    /// WGC video recorder, so the toast — a separate window WGC's per-window capture can't see — is
    /// composited into the clip. The toast service publishes the rendered toast (BGRA pixels) and its
    /// position relative to the game's client rect while a toast is on screen over a game; the
    /// recorder blends the latest published frame onto each captured video frame. A process-wide
    /// singleton because the two services are constructed independently; only one game records at a
    /// time, so a single slot suffices.
    /// </summary>
    internal static class VideoOverlaySink
    {
        private static readonly object Gate = new object();
        private static byte[] _bgra;
        private static int _width;
        private static int _height;
        private static double _clientX;
        private static double _clientY;
        private static double _clientWidth;
        private static double _clientHeight;
        private static long _version;

        /// <summary>
        /// Publishes the current toast overlay. <paramref name="bgra"/> is tightly-packed BGRA
        /// (stride = width*4). The client-rect fields are the game window's client area in the SAME
        /// physical-pixel space as <paramref name="clientX"/>/<paramref name="clientY"/> (the overlay's
        /// top-left within it), so the recorder can map it into the (any-sized) captured frame.
        /// </summary>
        public static void Publish(
            byte[] bgra, int width, int height,
            double clientX, double clientY, double clientWidth, double clientHeight)
        {
            lock (Gate)
            {
                _bgra = bgra;
                _width = width;
                _height = height;
                _clientX = clientX;
                _clientY = clientY;
                _clientWidth = clientWidth;
                _clientHeight = clientHeight;
                _version++;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                if (_bgra == null)
                {
                    return;
                }

                _bgra = null;
                _version++;
            }
        }

        /// <summary>
        /// The current overlay, or false when none. <paramref name="version"/> changes whenever the
        /// overlay is republished/cleared, so the recorder can skip re-uploading an unchanged frame.
        /// </summary>
        public static bool TryGet(
            out byte[] bgra, out int width, out int height,
            out double clientX, out double clientY, out double clientWidth, out double clientHeight,
            out long version)
        {
            lock (Gate)
            {
                bgra = _bgra;
                width = _width;
                height = _height;
                clientX = _clientX;
                clientY = _clientY;
                clientWidth = _clientWidth;
                clientHeight = _clientHeight;
                version = _version;
                return _bgra != null;
            }
        }
    }
}
