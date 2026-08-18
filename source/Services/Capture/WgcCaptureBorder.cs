using System;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.Capture;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The Windows.Graphics.Capture session options the plugin sets on every capture it starts, for
    /// both the video recorder and screenshot capture: the on-screen border (the colored capture
    /// indicator Windows draws around a captured window), cursor compositing, and the producer's
    /// update rate. Each of these postdates the Windows 10 contract this project compiles against and
    /// is therefore reached by reflection, so an older Windows keeps capturing normally rather than
    /// failing on a missing property.
    /// </summary>
    internal static class WgcCaptureBorder
    {
        private static int _accessRequested;

        /// <summary>
        /// Asks newer WGC implementations not to produce frames faster than the recorder consumes
        /// them. MinUpdateInterval arrived after the Windows 10 contract this project compiles
        /// against, so reflection is deliberate: unsupported systems retain their existing capture
        /// path, while Windows 11 24H2+ avoids servicing a high-refresh game at monitor rate only to
        /// discard the excess frames in the fixed-rate encoder pump.
        /// </summary>
        public static bool LimitUpdateRate(GraphicsCaptureSession session, int fps)
        {
            if (session == null)
            {
                return false;
            }

            try
            {
                var property = session.GetType().GetProperty("MinUpdateInterval");
                if (property == null || !property.CanWrite || property.PropertyType != typeof(TimeSpan))
                {
                    return false;
                }

                property.SetValue(session, CaptureWorkloadPolicy.CaptureSourceInterval(fps));
                return true;
            }
            catch
            {
                // Best effort: failure must never stop capture on an older or unusual projection.
                return false;
            }
        }

        /// <summary>
        /// Stops WGC compositing the mouse cursor into captured frames. This defaults to on, and
        /// leaving it on is what makes the pointer itself feel heavy while a capture is live: the
        /// cursor normally rides the GPU's hardware cursor plane and moves at the rate the mouse
        /// reports, but a capture that has to include it forces it through composition instead, so it
        /// advances at frame cadence. Turning it off also keeps the desktop pointer out of clips and
        /// unlock screenshots, where it was never part of what the game drew.
        /// <para>
        /// IsCursorCaptureEnabled postdates the Windows 10 contract this project compiles against, so
        /// it is reached by reflection: older systems keep capturing exactly as before.
        /// </para>
        /// </summary>
        public static bool SuppressCursor(GraphicsCaptureSession session)
        {
            if (session == null)
            {
                return false;
            }

            try
            {
                var property = session.GetType().GetProperty("IsCursorCaptureEnabled");
                if (property == null || !property.CanWrite || property.PropertyType != typeof(bool))
                {
                    return false;
                }

                property.SetValue(session, false);
                return true;
            }
            catch
            {
                // Best effort: never stop a capture over a presentation option.
                return false;
            }
        }

        public static void Suppress(GraphicsCaptureSession session)
        {
            if (session == null)
            {
                return;
            }

            EnsureBorderlessAccess();

            try
            {
                var property = session.GetType().GetProperty("IsBorderRequired");
                if (property != null && property.CanWrite)
                {
                    property.SetValue(session, false);
                }
            }
            catch
            {
                // Best effort: keep capture working if this OS denies or lacks border suppression.
            }
        }

        private static void EnsureBorderlessAccess()
        {
            if (Interlocked.Exchange(ref _accessRequested, 1) != 0)
            {
                return;
            }

            try
            {
                var accessType = ResolveWinRtType("Windows.Graphics.Capture.GraphicsCaptureAccess");
                var kindType = ResolveWinRtType("Windows.Graphics.Capture.GraphicsCaptureAccessKind");
                var method = accessType?.GetMethod("RequestAccessAsync", new[] { kindType });
                if (method == null)
                {
                    return;
                }

                var operation = method.Invoke(null, new[] { Enum.Parse(kindType, "Borderless") });
                if (operation is IAsyncInfo info)
                {
                    for (var spins = 0; info.Status == AsyncStatus.Started && spins < 200; spins++)
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            catch
            {
                // Best effort: the border remains if access is unavailable or denied.
            }
        }

        private static Type ResolveWinRtType(string runtimeClassName)
        {
            return Type.GetType(runtimeClassName + ", Windows.Foundation.UniversalApiContract, ContentType=WindowsRuntime")
                ?? Type.GetType(runtimeClassName + ", Windows, ContentType=WindowsRuntime");
        }
    }
}
