using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.Logging
{
    // Opt-in process counters complement sampled stacks; exited threads may be missing.
    internal sealed class ProcessThreadCpuSampler : IDisposable
    {
        internal sealed class Snapshot
        {
            public long Qpc;
            public double ProcessCpuMs;
            public Dictionary<int, double> Threads = new Dictionary<int, double>();
            public int UnavailableThreads;
        }

        private readonly Func<Snapshot> _capture;
        private readonly Action<string> _log;
        private readonly Timer _timer;
        private Snapshot _previous;
        private int _busy;
        private int _disposed;

        public ProcessThreadCpuSampler(Action<string> log) : this(Capture, log, true) { }
        internal ProcessThreadCpuSampler(Func<Snapshot> capture, Action<string> log, bool startTimer)
        {
            _capture = capture;
            _log = log;
            _previous = capture();
            if (startTimer) _timer = new Timer(_ => SampleNow(), null, 1000, 1000);
        }

        internal void SampleNow()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _busy, 1) != 0) return;
            var overhead = Stopwatch.StartNew();
            try
            {
                var current = _capture();
                var previous = _previous;
                _previous = current;
                var elapsed = (current.Qpc - previous.Qpc) * 1000.0 / Stopwatch.Frequency;
                if (elapsed <= 0) return;
                var cpu = Math.Max(0, current.ProcessCpuMs - previous.ProcessCpuMs);
                var deltas = current.Threads.Select(pair =>
                {
                    // A new/reused OS thread starts with its own counter.
                    var old = previous.Threads.TryGetValue(pair.Key, out var value) && value <= pair.Value ? value : 0;
                    return new KeyValuePair<int, double>(pair.Key, pair.Value - old);
                }).Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value).ToList();
                string Number(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
                var message = "cpu.sample windowStartQpc=" + previous.Qpc + " windowEndQpc=" + current.Qpc +
                    " elapsedMs=" + Number(elapsed) + " processCpuMs=" + Number(cpu) +
                    " processCpuPercent=" + Number(cpu / elapsed * 100) +
                    " liveThreadCpuMs=" + Number(deltas.Sum(pair => pair.Value)) +
                    " unavailableThreads=" + current.UnavailableThreads +
                    " sampleOverheadMs=" + Number(overhead.Elapsed.TotalMilliseconds) +
                    " topThreads=" + string.Join(",", deltas.Take(8).Select(pair => pair.Key + ":" + Number(pair.Value)));
                if (Volatile.Read(ref _disposed) == 0) _log(message);
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _disposed) == 0) _log("cpu.sample.error type=" + ex.GetType().Name);
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        private static Snapshot Capture()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var snapshot = new Snapshot { Qpc = Stopwatch.GetTimestamp(), ProcessCpuMs = process.TotalProcessorTime.TotalMilliseconds };
                foreach (ProcessThread thread in process.Threads)
                {
                    using (thread)
                    {
                        try { snapshot.Threads[thread.Id] = thread.TotalProcessorTime.TotalMilliseconds; }
                        catch (InvalidOperationException) { snapshot.UnavailableThreads++; }
                        catch (System.ComponentModel.Win32Exception) { snapshot.UnavailableThreads++; }
                    }
                }
                return snapshot;
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _timer?.Dispose();
        }
    }
}
