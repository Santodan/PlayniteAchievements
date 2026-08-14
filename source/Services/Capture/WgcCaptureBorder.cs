using System;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.Capture;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Removes the Windows.Graphics.Capture on-screen border (the colored capture indicator Windows
    /// draws around a captured window) for both the video recorder and screenshot capture.
    /// Newer borderless-capture APIs are accessed by reflection so older Windows versions keep
    /// capturing normally when those APIs are unavailable.
    /// </summary>
    internal static class WgcCaptureBorder
    {
        private static int _accessRequested;

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
