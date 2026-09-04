using System;
using System.Windows;
using System.Windows.Interop;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Realizes a plugin window's HWND under Per-Monitor-V2 when its monitor's true scale differs
    /// from the scale WPF renders at, so Windows presents it natively instead of bitmap-stretching
    /// (and thereby blurring) it. A window's DPI awareness is fixed at HWND creation, so this has to
    /// run before the window is shown.
    ///
    /// The mismatch guard is deliberate and mirrors ToastNotificationService: when the scales
    /// already agree Windows never virtualizes the window, and forcing a per-monitor HWND anyway
    /// routes WM_DPICHANGED through WPF's shared DPI state in this system-aware host process,
    /// which has been observed to rescale sibling windows and hard-crash the process on
    /// single-monitor high-DPI setups.
    ///
    /// A Per-Monitor-V2 window in a system-aware process needs its messages handled under a matching
    /// thread context, otherwise WPF's cursor lookups resolve in the wrong coordinate space and
    /// buttons stop registering clicks (reported after switching from 4K monitors to a TV). The
    /// realized window is therefore also subclassed via <see cref="PerMonitorWindowMessageScope"/>.
    ///
    /// The monitor is probed from the owner rather than the window, which has no HWND yet. That is
    /// exact for the CenterOwner default and an approximation when a persisted placement puts the
    /// window on another monitor; in the case this targets, a process whose latched system DPI is
    /// stale so every monitor mismatches it, the probe agrees either way.
    /// </summary>
    internal static class PerMonitorWindowRealizer
    {
        // Matches ToastNotificationService.DpiSettleTolerance: below this the two scales are the
        // same scale read through two different APIs, not a real mismatch.
        private const double ScaleTolerance = 0.01;

        /// <summary>
        /// Applies the per-monitor realization to <paramref name="window"/> when needed. Logs one
        /// line per call so a report can be diagnosed from the plugin log: the no-mismatch case is
        /// what distinguishes a DPI-virtualization report from an image-resampling one. Never throws.
        /// </summary>
        public static void Apply(Window window, Window owner, ILogger logger, string logLabel)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                var ownerHandle = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
                var monitorScale = ToastWindowPlacer.ResolveMonitorScale(ownerHandle);
                var systemScale = ToastWindowPlacer.SystemScale();
                var needsPerMonitorWindow = systemScale > 0 &&
                    Math.Abs(monitorScale - systemScale) >= ScaleTolerance;

                var messageScope = false;
                if (needsPerMonitorWindow)
                {
                    // Attach inside the same scope so the first messages after creation are covered.
                    using (DpiAwarenessScope.PerMonitorV2())
                    {
                        new WindowInteropHelper(window).EnsureHandle();
                        messageScope = PerMonitorWindowMessageScope.Attach(window);
                    }
                }

                logger?.Info(
                    $"[Dpi] {logLabel} '{window.Title}': monitorScale={monitorScale:0.###}, " +
                    $"systemScale={systemScale:0.###}, perMonitorWindow={needsPerMonitorWindow}, " +
                    $"messageScope={messageScope}, threadContext={DpiAwarenessScope.DescribeThreadContext()}");
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"Failed to apply per-monitor DPI awareness to plugin window '{logLabel}'.");
            }
        }
    }
}
