using System;
using System.Threading;
using Playnite.SDK;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Removes the Windows.Graphics.Capture on-screen border (the colored capture indicator Windows
    /// draws around a captured window) for both the video recorder and the screenshot capture.
    ///
    /// Clearing <see cref="GraphicsCaptureSession.IsBorderRequired"/> only takes effect once the app
    /// has been granted "Borderless" capture access via
    /// <see cref="GraphicsCaptureAccess.RequestAccessAsync"/>; without that grant the setter is
    /// rejected and the border stays. The request is made once per process (the grant persists for
    /// later sessions) on the calling capture/pump thread — never the UI thread. The border is never
    /// in the captured pixels, so any failure (older Windows, access denied) is swallowed and the
    /// border simply remains on screen.
    /// </summary>
    internal static class WgcCaptureBorder
    {
        // Guards the one-time, process-wide Borderless access request.
        private static int _accessRequested;

        public static void Suppress(GraphicsCaptureSession session, ILogger logger = null)
        {
            if (session == null)
            {
                return;
            }

            EnsureBorderlessAccess(logger);

            try
            {
                session.IsBorderRequired = false;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[Recording] Could not clear the capture border (access denied); it stays on screen.");
            }
        }

        private static void EnsureBorderlessAccess(ILogger logger)
        {
            if (Interlocked.Exchange(ref _accessRequested, 1) != 0)
            {
                return;
            }

            try
            {
                // Wait on the IAsyncOperation directly (via IAsyncInfo, in the referenced contract)
                // rather than AsTask(), whose overloads drag in the union "Windows" facade metadata.
                var op = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                var info = (IAsyncInfo)op;
                var spins = 0;
                while (info.Status == AsyncStatus.Started && spins++ < 500)
                {
                    Thread.Sleep(10);
                }

                if (info.Status == AsyncStatus.Completed)
                {
                    AppCapabilityAccessStatus status = op.GetResults();
                    logger?.Info($"[Recording] Borderless capture access request returned: {status}.");
                }
                else
                {
                    logger?.Debug($"[Recording] Borderless capture access request did not complete (status={info.Status}); the border may stay.");
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[Recording] Borderless capture access request failed; the border may stay.");
            }
        }
    }
}
