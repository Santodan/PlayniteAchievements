using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Waits out sub-timer-tick intervals, for pacing a capture loop to a frame rate.
    /// <para>
    /// <see cref="Thread.Sleep(TimeSpan)"/> rounds up to the system timer interval — 15.6 ms unless
    /// something in the process has raised it — so a 60 fps pump asking for 16.67 ms gets 15.6 or
    /// 31.2 and never holds its rate. A high-resolution waitable timer (Windows 10 1803+) waits the
    /// interval it was asked for whatever the timer tick is. Where it is unavailable this falls back
    /// to <see cref="Thread.Sleep(TimeSpan)"/>, which is what the pump did before.
    /// </para>
    /// </summary>
    internal sealed class FramePacer : IDisposable
    {
        // synchapi.h / winnt.h: CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS, WAIT_FAILED.
        private const uint HighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x1F0003;
        private const uint WaitFailed = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(
            IntPtr timerAttributes, string timerName, uint flags, uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(
            IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine,
            IntPtr argToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private IntPtr _timer;

        public FramePacer()
        {
            try
            {
                // Returns NULL with ERROR_INVALID_PARAMETER where the high-resolution flag is not
                // supported, which the Wait fallback covers.
                _timer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, TimerAllAccess);
            }
            catch (Exception)
            {
                _timer = IntPtr.Zero;
            }
        }

        /// <summary>Whether a high-resolution timer is in use rather than the Thread.Sleep fallback.</summary>
        public bool IsHighResolution => _timer != IntPtr.Zero;

        /// <summary>Waits for <paramref name="delay"/>; returns at once for a non-positive delay.</summary>
        public void Wait(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            if (_timer == IntPtr.Zero)
            {
                Thread.Sleep(delay);
                return;
            }

            // A negative due time is relative, in 100-ns units.
            var dueTime = -delay.Ticks;
            if (!SetWaitableTimer(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                Thread.Sleep(delay);
                return;
            }

            // A timeout return needs no fallback — the interval has passed either way; only a failed
            // wait means we never waited at all.
            var timeoutMs = (uint)Math.Min(int.MaxValue, delay.TotalMilliseconds + 50);
            if (WaitForSingleObject(_timer, Math.Max(1, timeoutMs)) == WaitFailed)
            {
                Thread.Sleep(delay);
            }
        }

        public void Dispose()
        {
            var timer = _timer;
            _timer = IntPtr.Zero;
            if (timer != IntPtr.Zero)
            {
                CloseHandle(timer);
            }
        }
    }
}
