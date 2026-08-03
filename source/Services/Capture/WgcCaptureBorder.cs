using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Playnite.SDK;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Removes the Windows.Graphics.Capture on-screen border (the colored capture indicator Windows
    /// draws around a captured window) for both the video recorder and the screenshot capture.
    ///
    /// <see cref="GraphicsCaptureSession.IsBorderRequired"/> and
    /// <see cref="GraphicsCaptureAccess.RequestAccessAsync"/> are Windows 11 22H2+ (build 22621) APIs.
    /// The plugin compiles against newer WinRT metadata than the OSes it can run on, so these APIs
    /// must be used defensively: an OS that lacks them throws MissingMethod/TypeLoad when the CLR
    /// binds the method that references them, and that failure happens at the call site — it cannot
    /// be caught inside the same method. So the presence is checked at runtime via
    /// <see cref="ApiInformation"/> first, the newer-API calls live in separate non-inlined methods,
    /// and the whole thing is wrapped in try/catch. On an older OS (or if the OS denies the grant)
    /// the border simply stays — it is never in the captured pixels — and, critically, capture is
    /// never interrupted.
    /// </summary>
    internal static class WgcCaptureBorder
    {
        // Guards the one-time, process-wide Borderless access request.
        private static int _accessRequested;
        // Set once the border APIs prove unavailable/denied, so we stop attempting on every capture.
        private static volatile bool _unsupported;

        public static void Suppress(GraphicsCaptureSession session, ILogger logger = null)
        {
            if (session == null || _unsupported)
            {
                return;
            }

            // Must gate BEFORE calling into the newer-API methods: an OS without them would fail to
            // JIT-bind those methods, and that exception surfaces at the call site here (not inside
            // them), so only call them once the OS is confirmed to have them.
            if (!BorderApiAvailable())
            {
                _unsupported = true;
                return;
            }

            try
            {
                EnsureBorderlessAccess(logger);
                SetBorderNotRequired(session);
            }
            catch (Exception ex)
            {
                // Guards the residual JIT-bind risk and any access denial; leave the border, keep capturing.
                _unsupported = true;
                logger?.Debug(ex, "[Recording] Capture border suppression unavailable; leaving the border on screen.");
            }
        }

        // True only on builds that actually expose both APIs we use (Windows 11 22H2+), so the direct
        // calls below are never JIT-bound on an OS that lacks them.
        private static bool BorderApiAvailable()
        {
            try
            {
                return ApiInformation.IsPropertyPresent(
                           "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired")
                       && ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess");
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetBorderNotRequired(GraphicsCaptureSession session)
        {
            session.IsBorderRequired = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureBorderlessAccess(ILogger logger)
        {
            if (Interlocked.Exchange(ref _accessRequested, 1) != 0)
            {
                return; // Requested once per process; the grant persists for later sessions.
            }

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
    }
}
