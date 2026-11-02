using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Services.Logging
{
    // Opt-in elapsed-time diagnostics; CPU attribution still comes from PerfView.
    internal static class PollingDebugLog
    {
        private static readonly object Sync = new object();
        private static readonly AsyncLocal<long> Poll = new AsyncLocal<long>();
        private static readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static FileLogger _logger;
        private static long _nextPoll;
        private static long _nextStage;
        private static readonly AsyncLocal<long> CurrentStage = new AsyncLocal<long>();
        private static readonly Func<long> AllocatedBytes = ResolveAllocationCounter();

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);

        private static Func<long> ResolveAllocationCounter()
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            return method == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
        }

        private static double CpuMilliseconds(bool process)
        {
            long created, exited, kernel, user;
            var ok = process
                ? GetProcessTimes(GetCurrentProcess(), out created, out exited, out kernel, out user)
                : GetThreadTimes(GetCurrentThread(), out created, out exited, out kernel, out user);
            return ok ? (kernel + user) / 10000.0 : double.NaN;
        }

        private static string Number(double value) => double.IsNaN(value) ? "unavailable" : value.ToString("F2", CultureInfo.InvariantCulture);

        public static void Configure(string directory, bool enabled)
        {
            lock (Sync)
            {
                if (!enabled)
                {
                    // FileLogger may skip its final queued entry during shutdown. Keep
                    // a terminal marker after the diagnostic measurements being drained.
                    _logger?.Info("Polling diagnostics stopping.");
                    _logger?.Dispose();
                    _logger = null;
                    Files.Clear();
                    return;
                }
                if (_logger != null) return;
                _logger = new FileLogger(directory, "PollingDebug.log");
                _logger.Info("Polling diagnostics enabled. pid=" + Process.GetCurrentProcess().Id +
                    " processors=" + Environment.ProcessorCount + " runtime=" + Environment.Version +
                    " utc=" + DateTime.UtcNow.ToString("O") + " qpc=" + Stopwatch.GetTimestamp() +
                    " qpcFrequency=" + Stopwatch.Frequency);
                _logger.Info("Elapsed, process CPU, GC and heap deltas cover the entire measurement window. Thread CPU/allocation deltas include other work on that thread and are unavailable if the stage switches threads. Nested stages overlap; do not sum them. File metadata changes are not content hashes. Optional ETW provider: PlayniteAchievements-Polling.");
            }
        }

        public static IDisposable BeginPoll(string games)
        {
            lock (Sync) { if (_logger == null) return null; }
            var previous = Poll.Value;
            Poll.Value = Interlocked.Increment(ref _nextPoll);
            ThreadPool.GetAvailableThreads(out var workers, out var io);
            Write("threadPoolAvailable workers=" + workers + " io=" + io);
            Write("poll.begin games=" + games);
            return new Timing("poll.total", () => Poll.Value = previous);
        }

        public static IDisposable Stage(string name)
        {
            if (Poll.Value == 0) return null;
            lock (Sync) { if (_logger == null) return null; }
            return new Timing(name, null);
        }

        public static void Write(string message)
        {
            if (Poll.Value == 0) return;
            lock (Sync) { _logger?.Info("poll=" + Poll.Value + " stageId=" + CurrentStage.Value +
                " managedTid=" + Thread.CurrentThread.ManagedThreadId + " osTid=" + GetCurrentThreadId() +
                " qpc=" + Stopwatch.GetTimestamp() + " " + message); }
        }

        public static void ObserveFile(string path)
        {
            if (Poll.Value == 0 || string.IsNullOrWhiteSpace(path)) return;
            lock (Sync)
            {
                if (_logger == null) return;
                try
                {
                    var file = new FileInfo(path);
                    var signature = file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing";
                    var known = Files.TryGetValue(path, out var previous);
                    _logger.Info("poll=" + Poll.Value + " file=" + path + " signature=" + signature +
                        " metadataChanged=" + (!known ? "unknown" : (signature != previous).ToString()));
                    if (Files.Count >= 128 && !known) Files.Clear();
                    Files[path] = signature;
                }
                catch (Exception ex) { _logger.Debug(ex, "Could not inspect polling file metadata."); }
            }
        }

        private sealed class Timing : IDisposable
        {
            private readonly string _name;
            private readonly Action _finish;
            private readonly Stopwatch _timer = Stopwatch.StartNew();
            private readonly long _id = Interlocked.Increment(ref _nextStage);
            private readonly long _parent = CurrentStage.Value;
            private readonly uint _startThread = GetCurrentThreadId();
            private readonly int _startManagedThread = Thread.CurrentThread.ManagedThreadId;
            private readonly double _threadCpu = CpuMilliseconds(false);
            private readonly double _processCpu;
            private readonly long _allocated = AllocatedBytes?.Invoke() ?? -1;
            private readonly long _heap = GC.GetTotalMemory(false);
            private readonly int _gen0 = GC.CollectionCount(0), _gen1 = GC.CollectionCount(1), _gen2 = GC.CollectionCount(2);
            private bool _disposed;
            public Timing(string name, Action finish)
            {
                _name = name;
                _finish = finish;
                _processCpu = name == "poll.total" ? CpuMilliseconds(true) : double.NaN;
                CurrentStage.Value = _id;
                Write("stage.begin name=" + name + " parentStageId=" + _parent);
                PollingDebugEvents.Log.StageBegin(Poll.Value, _id, name, (int)_startThread);
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                var endThread = GetCurrentThreadId();
                var elapsed = _timer.Elapsed.TotalMilliseconds;
                var threadCpu = endThread == _startThread ? CpuMilliseconds(false) - _threadCpu : double.NaN;
                var allocated = endThread == _startThread && _allocated >= 0 ? (AllocatedBytes?.Invoke() ?? _allocated) - _allocated : -1;
                var processCpu = _name == "poll.total" ? CpuMilliseconds(true) - _processCpu : double.NaN;
                Write("stage=" + _name + " elapsedMs=" + Number(elapsed) + " parentStageId=" + _parent +
                    " startManagedTid=" + _startManagedThread + " startOsTid=" + _startThread + " endOsTid=" + endThread +
                    " threadSwitched=" + (endThread != _startThread) + " threadWindowCpuMs=" + Number(threadCpu) +
                    " threadWindowAllocatedBytes=" + (allocated < 0 ? "unavailable" : allocated.ToString(CultureInfo.InvariantCulture)) +
                    " processWindowCpuMs=" + Number(processCpu) + " managedHeapDeltaBytes=" + (GC.GetTotalMemory(false) - _heap) +
                    " gc0=" + (GC.CollectionCount(0) - _gen0) + " gc1=" + (GC.CollectionCount(1) - _gen1) +
                    " gc2=" + (GC.CollectionCount(2) - _gen2));
                PollingDebugEvents.Log.StageEnd(Poll.Value, _id, _name, (int)endThread, elapsed);
                CurrentStage.Value = _parent;
                _finish?.Invoke();
            }
        }
    }

    [EventSource(Name = "PlayniteAchievements-Polling")]
    internal sealed class PollingDebugEvents : EventSource
    {
        internal static readonly PollingDebugEvents Log = new PollingDebugEvents();
        [Event(1, Level = EventLevel.Informational)]
        public void StageBegin(long poll, long stage, string name, int osThread)
        { if (IsEnabled()) WriteEvent(1, new object[] { poll, stage, name, osThread }); }
        [Event(2, Level = EventLevel.Informational)]
        public void StageEnd(long poll, long stage, string name, int osThread, double elapsedMs)
        { if (IsEnabled()) WriteEvent(2, new object[] { poll, stage, name, osThread, elapsedMs }); }
    }
}
