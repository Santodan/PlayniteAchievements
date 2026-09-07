using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.Sound
{
    /// <summary>
    /// Owns the sound host process: starts it hidden with redirected stdio, feeds it protocol lines
    /// from a background writer (never the caller's thread, which is usually the WPF UI thread),
    /// reads its events, and relaunches it with backoff when it dies. The PID is exposed so the
    /// recorder can capture the host's render as a process-loopback reference.
    /// </summary>
    internal sealed class UnlockSoundHost : IDisposable
    {
        private const int MaxQueuedLines = 8;
        private static readonly TimeSpan WriteStallLimit = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan QuitGrace = TimeSpan.FromMilliseconds(500);

        private readonly string _executablePath;
        private readonly ILogger _logger;
        private readonly object _gate = new object();
        private readonly Dictionary<int, long> _sentQpcById = new Dictionary<int, long>();
        // The measured audible onset of each recent sound, by play id, for the recorder's
        // composite placement. Bounded like _sentQpcById.
        private readonly Dictionary<int, DateTime> _audibleOnsetUtcById = new Dictionary<int, DateTime>();
        private Process _process;
        private BlockingCollection<string> _outbox;
        private long _writeStartedTicks;
        private long _runStartedTicks;
        private int _consecutiveFailures;
        private int _nextPlayId;
        private IReadOnlyList<string> _preloadSet = Array.Empty<string>();
        private bool _disposed;

        public UnlockSoundHost(string executablePath, ILogger logger)
        {
            _executablePath = executablePath;
            _logger = logger;
        }

        /// <summary>The live host's process id, or null while it is not running.</summary>
        public int? ProcessId
        {
            get
            {
                lock (_gate)
                {
                    try
                    {
                        return _process != null && !_process.HasExited ? _process.Id : (int?)null;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }
        }

        /// <summary>Starts the host if it is not running. False when the exe is missing or start failed.</summary>
        public bool TryStart()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                if (ProcessId != null)
                {
                    return true;
                }

                if (string.IsNullOrWhiteSpace(_executablePath) || !File.Exists(_executablePath))
                {
                    _logger?.Warn($"[SoundHost] Executable not found: '{_executablePath ?? "<null>"}'. Unlock sounds are unavailable.");
                    return false;
                }

                try
                {
                    ReleaseProcessLocked();
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = _executablePath,
                            Arguments = SoundHostProtocol.RoleArgument + " --parent " + Process.GetCurrentProcess().Id,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            StandardOutputEncoding = new UTF8Encoding(false),
                            StandardErrorEncoding = new UTF8Encoding(false),
                        },
                        EnableRaisingEvents = true,
                    };
                    process.Exited += OnProcessExited;
                    process.Start();

                    _process = process;
                    _runStartedTicks = Stopwatch.GetTimestamp();
                    var outbox = new BlockingCollection<string>();
                    _outbox = outbox;
                    // Process.StandardInput would use the console's OEM code page; wrap the raw pipe
                    // in UTF-8 so non-ASCII paths survive.
                    var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                    StartThread("SoundHost.Write", () => WriteLoop(outbox, writer));
                    StartThread("SoundHost.Read", () => ReadLoop(process));
                    _logger?.Info($"[SoundHost] Started pid={process.Id}.");

                    if (_preloadSet.Count > 0)
                    {
                        outbox.Add(SoundHostProtocol.EncodePreload(_preloadSet));
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[SoundHost] Could not start the sound host.");
                    ReleaseProcessLocked();
                    return false;
                }
            }
        }

        /// <summary>Remembers the set (re-sent after every start) and sends it if the host is running.</summary>
        public void Preload(IReadOnlyList<string> paths)
        {
            var set = paths ?? Array.Empty<string>();
            lock (_gate)
            {
                _preloadSet = set;
                Enqueue(SoundHostProtocol.EncodePreload(set));
            }
        }

        /// <summary>
        /// Queues a play and returns the moment it was asked for (null when the host is not running
        /// and could not be started). The stamp is the send time, not the audible onset; the
        /// caller adds its alignment constant, and the host's started event logs the real lag.
        /// </summary>
        public DateTime? Play(string path, double gain, double maxSeconds, out int id)
        {
            lock (_gate)
            {
                id = 0;
                if (!TryStart())
                {
                    return null;
                }

                id = ++_nextPlayId;
                if (_sentQpcById.Count > 64)
                {
                    _sentQpcById.Clear();
                }

                if (_audibleOnsetUtcById.Count > 64)
                {
                    _audibleOnsetUtcById.Clear();
                }

                _sentQpcById[id] = Stopwatch.GetTimestamp();
                Enqueue(SoundHostProtocol.EncodePlay(id, path, gain, maxSeconds));
                return CaptureTimelineClock.UtcNow;
            }
        }

        /// <summary>
        /// When the sound with this play id became audible, as measured by the host (render-thread
        /// start plus queued buffer plus endpoint latency) and projected onto the capture timeline.
        /// Null until the host reports it, or when the host did not report a delay.
        /// </summary>
        public DateTime? TryGetAudibleOnsetUtc(int id)
        {
            lock (_gate)
            {
                return _audibleOnsetUtcById.TryGetValue(id, out var onset) ? onset : (DateTime?)null;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                Enqueue(SoundHostProtocol.StopVerb);
            }
        }

        /// <summary>
        /// Ends the host process (quit, then kill after a short grace) while leaving this object
        /// usable: the next <see cref="TryStart"/> launches it again. Used when the user turns
        /// unlock sounds off, so a disabled setting leaves no helper running.
        /// </summary>
        public void Shutdown()
        {
            Process process;
            lock (_gate)
            {
                process = _process;
                if (process == null)
                {
                    return;
                }

                // Detach first: this exit is intended and must not schedule a restart.
                process.Exited -= OnProcessExited;
                Enqueue(SoundHostProtocol.QuitVerb);
                _outbox?.CompleteAdding();
                _consecutiveFailures = 0;
            }

            try
            {
                if (!process.WaitForExit((int)QuitGrace.TotalMilliseconds))
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
            }

            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    ReleaseProcessLocked();
                }
            }

            _logger?.Info("[SoundHost] Stopped.");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Shutdown();
        }

        private void Enqueue(string line)
        {
            var outbox = _outbox;
            if (outbox == null || outbox.IsAddingCompleted)
            {
                return;
            }

            var stalledTicks = Interlocked.Read(ref _writeStartedTicks);
            var stalled = stalledTicks != 0
                && (Stopwatch.GetTimestamp() - stalledTicks) > WriteStallLimit.TotalSeconds * Stopwatch.Frequency;
            if (stalled || outbox.Count >= MaxQueuedLines)
            {
                _logger?.Warn("[SoundHost] The sound host stopped reading its pipe; killing it so the restart policy can recover.");
                KillLocked();
                return;
            }

            try
            {
                outbox.Add(line);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void WriteLoop(BlockingCollection<string> outbox, StreamWriter writer)
        {
            try
            {
                foreach (var line in outbox.GetConsumingEnumerable())
                {
                    Interlocked.Exchange(ref _writeStartedTicks, Stopwatch.GetTimestamp());
                    writer.WriteLine(line);
                    Interlocked.Exchange(ref _writeStartedTicks, 0);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SoundHost] Pipe write failed; the process exit handler recovers.");
            }
            finally
            {
                Interlocked.Exchange(ref _writeStartedTicks, 0);
                try
                {
                    writer.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private void ReadLoop(Process process)
        {
            try
            {
                var reader = process.StandardOutput;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!SoundHostProtocol.TryParse(line, out var message))
                    {
                        continue;
                    }

                    switch (message.Verb)
                    {
                        case SoundHostProtocol.StartedVerb:
                            LogStarted(message);
                            break;
                        case SoundHostProtocol.ErrorVerb:
                            _logger?.Warn($"[SoundHost] id={message.Id}: {message.Text}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SoundHost] Pipe read ended.");
            }
        }

        private void LogStarted(SoundHostMessage message)
        {
            long sentQpc;
            lock (_gate)
            {
                if (!_sentQpcById.TryGetValue(message.Id, out sentQpc))
                {
                    return;
                }

                _sentQpcById.Remove(message.Id);
            }

            var lagMs = (message.Qpc - sentQpc) * 1000.0 / Stopwatch.Frequency;
            if (message.AudibleDelayMs.HasValue)
            {
                // The helper's QPC is the same system clock as ours, so its render-start stamp
                // projects straight onto the capture timeline.
                var started = CaptureTimelineClock.FromQpc100ns(
                    CaptureTimelineClock.TimestampTo100ns(message.Qpc, Stopwatch.Frequency), out _);
                var onset = started.AddMilliseconds(message.AudibleDelayMs.Value);
                lock (_gate)
                {
                    _audibleOnsetUtcById[message.Id] = onset;
                }
            }

            _logger?.Info(
                $"[SoundHost] Sound id={message.Id} started {lagMs:0.0} ms after it was requested" +
                (message.AudibleDelayMs.HasValue
                    ? $", audible {message.AudibleDelayMs.Value:0.0} ms after that."
                    : "."));
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            TimeSpan? delay;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(sender, _process))
                {
                    return;
                }

                var runLength = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _runStartedTicks) / (double)Stopwatch.Frequency);
                int exitCode;
                try
                {
                    exitCode = _process.ExitCode;
                }
                catch (Exception)
                {
                    exitCode = -1;
                }

                _consecutiveFailures = SoundHostRestartPolicy.NextFailureCount(_consecutiveFailures, runLength);
                delay = SoundHostRestartPolicy.NextDelay(_consecutiveFailures);
                ReleaseProcessLocked();
                _logger?.Warn(
                    $"[SoundHost] Exited with code {exitCode} after {runLength.TotalSeconds:0.0} s; " +
                    (delay.HasValue ? $"restarting in {delay.Value.TotalSeconds:0.0} s." : "giving up until the next explicit start."));
            }

            if (delay.HasValue)
            {
                Task.Delay(delay.Value).ContinueWith(_ => TryStart(), TaskScheduler.Default);
            }
        }

        private void KillLocked()
        {
            try
            {
                _process?.Kill();
            }
            catch (Exception)
            {
            }
        }

        private void ReleaseProcessLocked()
        {
            var outbox = _outbox;
            _outbox = null;
            try
            {
                outbox?.CompleteAdding();
            }
            catch (Exception)
            {
            }

            var process = _process;
            _process = null;
            if (process == null)
            {
                return;
            }

            try
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private static void StartThread(string name, Action body)
        {
            new Thread(() => body()) { IsBackground = true, Name = name }.Start();
        }
    }
}
