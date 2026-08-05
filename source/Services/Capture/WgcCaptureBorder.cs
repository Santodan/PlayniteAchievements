using System;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.Capture;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Removes the Windows.Graphics.Capture on-screen border (the colored capture indicator Windows
    /// draws around a captured window) for both the video recorder and the screenshot capture.
    ///
    /// Setting <c>GraphicsCaptureSession.IsBorderRequired = false</c> only takes effect once the app
    /// has been granted "Borderless" capture access via
    /// <c>GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)</c>; without
    /// that grant the setter is rejected and the border stays. Both APIs are newer than the pinned
    /// WinRT contracts (Windows 11 22H2 / contract 22621), so the access request is made by reflection
    /// against the OS metadata and <c>IsBorderRequired</c> is set the same way. The border is never in
    /// the captured pixels — this only clears the live on-screen indicator — so any failure (older
    /// build, access denied) is swallowed and the border simply remains.
    /// </summary>
    internal static class WgcCaptureBorder
    {
        // Guards the one-time, process-wide Borderless access request.
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
                var prop = session.GetType().GetProperty("IsBorderRequired");
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(session, false);
                }
            }
            catch
            {
                // Older build or access denied; the border isn't in the captured pixels anyway.
            }
        }

        private static void EnsureBorderlessAccess()
        {
            if (Interlocked.Exchange(ref _accessRequested, 1) != 0)
            {
                return; // Requested once per process; the grant persists for later sessions.
            }

            try
            {
                var accessType = ResolveWinRtType("Windows.Graphics.Capture.GraphicsCaptureAccess");
                var kindType = ResolveWinRtType("Windows.Graphics.Capture.GraphicsCaptureAccessKind");
                if (accessType == null || kindType == null)
                {
                    return; // Pre-22H2: the border cannot be removed.
                }

                var method = accessType.GetMethod("RequestAccessAsync", new[] { kindType });
                if (method == null)
                {
                    return;
                }

                var borderless = Enum.Parse(kindType, "Borderless");
                var asyncOp = method.Invoke(null, new[] { borderless });

                // Block until the request resolves (Allowed/Denied) so IsBorderRequired is only set
                // after the grant is in place. Runs on the capture/pump thread, never the UI thread.
                if (asyncOp is IAsyncInfo info)
                {
                    var spins = 0;
                    while (info.Status == AsyncStatus.Started && spins++ < 200)
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            catch
            {
                // Best effort: if the request can't be made or is denied, the border stays.
            }
        }

        // Resolves a WinRT type from the OS metadata by its runtime-class name, tolerant of the
        // contract-assembly qualifier the running Windows build uses.
        private static Type ResolveWinRtType(string runtimeClassName)
        {
            return Type.GetType(runtimeClassName + ", Windows.Foundation.UniversalApiContract, ContentType=WindowsRuntime")
                ?? Type.GetType(runtimeClassName + ", Windows, ContentType=WindowsRuntime");
        }
    }
}
