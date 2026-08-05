using System;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Scopes the calling thread to Per-Monitor-V2 DPI awareness for the duration of a <c>using</c>
    /// block, restoring the previous context on dispose. A window's DPI awareness is fixed to the
    /// thread context that is active when its HWND is created, so realizing an HWND inside this scope
    /// makes just that window per-monitor aware while the host process stays system-aware. That stops
    /// Windows from bitmap-rescaling (and thereby blurring) the window on a monitor whose effective
    /// DPI differs from the process's system DPI. Also used to make <c>GetDpiForMonitor</c> return a
    /// monitor's true effective DPI (in a system-aware thread context it returns the system DPI).
    ///
    /// Requires Windows 10 1703+ (SetThreadDpiAwarenessContext plus the PER_MONITOR_AWARE_V2 context).
    /// On older systems <see cref="PerMonitorV2"/> returns a no-op scope, leaving behavior unchanged.
    /// Every call is wrapped so it can never throw into the caller.
    /// </summary>
    internal static class DpiAwarenessScope
    {
        // DPI_AWARENESS_CONTEXT pseudo-handles (winuser.h), passed by value as IntPtr.
        private static readonly IntPtr PerMonitorAwareV2Context = new IntPtr(-4);
        private static readonly IntPtr PerMonitorAwareContext = new IntPtr(-3);
        private static readonly IntPtr SystemAwareContext = new IntPtr(-2);
        private static readonly IntPtr UnawareContext = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

        private static readonly IDisposable NoOp = new NoOpScope();
        private static bool? _supported;

        /// <summary>
        /// Switches the current thread to Per-Monitor-V2 awareness until the returned scope is
        /// disposed. Returns a no-op scope when the API is unavailable (pre-1703) or the call fails,
        /// so callers can always wrap HWND creation / monitor-DPI queries in
        /// <c>using (DpiAwarenessScope.PerMonitorV2())</c> without a platform check.
        /// </summary>
        public static IDisposable PerMonitorV2()
        {
            if (!IsSupported())
            {
                return NoOp;
            }

            try
            {
                var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2Context);
                if (previous == IntPtr.Zero)
                {
                    // The context is unsupported on this OS (pre-1703); nothing was changed.
                    return NoOp;
                }

                return new ContextScope(previous);
            }
            catch
            {
                return NoOp;
            }
        }

        /// <summary>
        /// A human-readable label for the calling thread's current DPI awareness context, for
        /// diagnostics. Returns "unavailable" when the API is not present.
        /// </summary>
        public static string DescribeThreadContext()
        {
            if (!IsSupported())
            {
                return "unavailable";
            }

            try
            {
                var ctx = GetThreadDpiAwarenessContext();
                if (AreDpiAwarenessContextsEqual(ctx, PerMonitorAwareV2Context))
                {
                    return "PerMonitorAwareV2";
                }

                if (AreDpiAwarenessContextsEqual(ctx, PerMonitorAwareContext))
                {
                    return "PerMonitorAware";
                }

                if (AreDpiAwarenessContextsEqual(ctx, SystemAwareContext))
                {
                    return "SystemAware";
                }

                if (AreDpiAwarenessContextsEqual(ctx, UnawareContext))
                {
                    return "Unaware";
                }

                return "other";
            }
            catch
            {
                return "unknown";
            }
        }

        private static bool IsSupported()
        {
            if (_supported.HasValue)
            {
                return _supported.Value;
            }

            try
            {
                // Probes for the 1607+ thread-DPI API; throws DllNotFoundException /
                // EntryPointNotFoundException on older systems, which we cache as unsupported.
                GetThreadDpiAwarenessContext();
                _supported = true;
            }
            catch
            {
                _supported = false;
            }

            return _supported.Value;
        }

        private sealed class ContextScope : IDisposable
        {
            private readonly IntPtr _previous;
            private bool _disposed;

            public ContextScope(IntPtr previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    SetThreadDpiAwarenessContext(_previous);
                }
                catch
                {
                    // Best-effort restore; there is nothing actionable if it fails.
                }
            }
        }

        private sealed class NoOpScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
