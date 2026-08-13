// Measures Thread.Sleep vs a high-resolution waitable timer at a 60 fps interval, using the same
// P/Invokes as FramePacer. Compile with csc against net462 and run.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class PacerProbe
{
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

    private static void Main()
    {
        var interval = TimeSpan.FromSeconds(1.0 / 60);
        Console.WriteLine("target interval: " + interval.TotalMilliseconds.ToString("0.000") + " ms (60 fps)");

        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, TimerAllAccess);
        Console.WriteLine("high-resolution timer: " +
            (timer != IntPtr.Zero ? "created" : "UNAVAILABLE (err " + Marshal.GetLastWin32Error() + ")"));

        Report("Thread.Sleep", interval, d => Thread.Sleep(d));

        if (timer != IntPtr.Zero)
        {
            Report("waitable timer", interval, d =>
            {
                var due = -d.Ticks;
                if (!SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    Thread.Sleep(d);
                    return;
                }

                if (WaitForSingleObject(timer, (uint)(d.TotalMilliseconds + 50)) == WaitFailed)
                {
                    Thread.Sleep(d);
                }
            });

            CloseHandle(timer);
        }
    }

    private static void Report(string label, TimeSpan interval, Action<TimeSpan> wait)
    {
        // Same drift-corrected shape as the pump: aim at absolute deadlines, not fixed sleeps.
        const int Ticks = 120;
        var start = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        var worst = 0.0;

        for (var i = 0; i < Ticks; i++)
        {
            next += interval;
            var remaining = next - start.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                wait(remaining);
            }

            var late = (start.Elapsed - next).TotalMilliseconds;
            if (late > worst)
            {
                worst = late;
            }
        }

        var elapsed = start.Elapsed.TotalMilliseconds;
        var ideal = interval.TotalMilliseconds * Ticks;
        Console.WriteLine(
            label.PadRight(16) + " " + Ticks + " ticks in " + elapsed.ToString("0.0") + " ms" +
            " (ideal " + ideal.ToString("0.0") + ") => effective " +
            (Ticks / (elapsed / 1000.0)).ToString("0.00") + " fps, worst tick late by " +
            worst.ToString("0.00") + " ms");
    }
}
