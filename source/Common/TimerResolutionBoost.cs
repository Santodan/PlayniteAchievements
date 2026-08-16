using System;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Raises the process timer resolution to 1 ms for a bounded span, and opts the process out of
    /// Windows 11's background timer coarsening for that span.
    ///
    /// Why this exists: animated GIFs on the toast are paced by XamlAnimatedGif with plain
    /// <c>Task.Delay</c> per frame and no wall-clock compensation, so each frame delay rounds up
    /// to the current timer resolution. Timer resolution has been per-process since Windows 10
    /// 2004, and Windows 11 additionally coarsens it for processes whose windows are not
    /// foreground — which Playnite never is while a game runs. At the default 15.6 ms resolution a
    /// 30 ms GIF frame stretches to ~39 ms, a uniform ~25% slow-down, which is exactly the "GIF
    /// looks slightly too slow" a toast shows in-game while the settings preview (Playnite
    /// foreground) plays at authored speed.
    ///
    /// Begin/End are not refcounted: toast waves are sequential and this is scoped to one wave's
    /// display. Both directions swallow failures — a system that rejects either call simply keeps
    /// its current pacing.
    /// </summary>
    internal static class TimerResolutionBoost
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private const int ProcessPowerThrottlingInformation = 4;
        private const uint ProcessPowerThrottlingCurrentVersion = 1;
        private const uint IgnoreTimerResolution = 0x4;

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess, int processInformationClass,
            ref ProcessPowerThrottlingState processInformation, int processInformationSize);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryTimerResolution(
            out uint maximumResolution, out uint minimumResolution, out uint currentResolution);

        private static bool _active;

        /// <summary>The system timer resolution in milliseconds, or -1 when unreadable.</summary>
        public static double CurrentResolutionMs()
        {
            try
            {
                if (NtQueryTimerResolution(out _, out _, out var current) == 0)
                {
                    return current / 10_000.0;
                }
            }
            catch
            {
            }

            return -1;
        }

        public static void Begin()
        {
            if (_active)
            {
                return;
            }

            _active = true;
            try
            {
                timeBeginPeriod(1);
            }
            catch
            {
            }

            SetThrottling(ignoreCoarsening: true);
        }

        public static void End()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            try
            {
                timeEndPeriod(1);
            }
            catch
            {
            }

            SetThrottling(ignoreCoarsening: false);
        }

        // ControlMask carrying the flag with StateMask empty means "always honor timer resolution
        // requests"; both masks empty returns the policy to system-managed.
        private static void SetThrottling(bool ignoreCoarsening)
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ignoreCoarsening ? IgnoreTimerResolution : 0,
                StateMask = 0,
            };
            try
            {
                SetProcessInformation(
                    GetCurrentProcess(), ProcessPowerThrottlingInformation,
                    ref state, Marshal.SizeOf(typeof(ProcessPowerThrottlingState)));
            }
            catch
            {
            }
        }
    }
}
