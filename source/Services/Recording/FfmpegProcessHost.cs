using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Hosts one ffmpeg process with redirected stdin/stdout/stderr and no window. Keeps a small
    /// ring buffer of recent stderr lines for diagnostics, supports graceful shutdown via ffmpeg's
    /// stdin "q" command, and can be assigned to a <see cref="WindowsJobObject"/> so the process
    /// dies with Playnite.
    /// </summary>
    internal sealed class FfmpegProcessHost : IDisposable
    {
        private const int StdErrTailLines = 50;
        private const int StdOutMaxLines = 4096;

        private readonly ILogger _logger;
        private readonly string _ffmpegPath;
        private readonly string _arguments;
        private readonly bool _captureStdOut;
        private readonly object _outputLock = new object();
        private readonly Queue<string> _stdErrTail = new Queue<string>(StdErrTailLines);
        private readonly List<string> _stdOutLines = new List<string>();
        private readonly TaskCompletionSource<int> _exited =
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        private Process _process;
        private bool _disposed;

        public FfmpegProcessHost(string ffmpegPath, string arguments, ILogger logger, bool captureStdOut = false)
        {
            _ffmpegPath = ffmpegPath;
            _arguments = arguments;
            _logger = logger;
            _captureStdOut = captureStdOut;
        }

        /// <summary>
        /// Raised once when the process exits (for capture crash recovery). The event fires on a
        /// thread-pool thread.
        /// </summary>
        public event EventHandler Exited;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process == null || _process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public int? ExitCode => _exited.Task.IsCompleted ? _exited.Task.Result : (int?)null;

        /// <summary>
        /// The most recent stderr lines (ring buffer), newline-joined for logging.
        /// </summary>
        public string StdErrTail
        {
            get
            {
                lock (_outputLock)
                {
                    return string.Join(Environment.NewLine, _stdErrTail);
                }
            }
        }

        /// <summary>
        /// Captured stdout lines (probe output such as -version/-encoders). Empty unless the
        /// host was created with captureStdOut.
        /// </summary>
        public IReadOnlyList<string> StdOutLines
        {
            get
            {
                lock (_outputLock)
                {
                    return _stdOutLines.ToList();
                }
            }
        }

        /// <summary>
        /// Starts the process and optionally assigns it to the given job object. Returns false
        /// (with a debug log) when the process could not be started.
        /// </summary>
        public bool Start(WindowsJobObject jobObject = null)
        {
            if (_disposed || _process != null)
            {
                return false;
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = _arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            process.ErrorDataReceived += (s, e) => AppendStdErr(e.Data);
            process.OutputDataReceived += (s, e) => AppendStdOut(e.Data);
            process.Exited += (s, e) =>
            {
                try
                {
                    _exited.TrySetResult(SafeExitCode(process));
                }
                catch
                {
                    _exited.TrySetResult(-1);
                }

                Exited?.Invoke(this, EventArgs.Empty);
            };

            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    return false;
                }

                _process = process;
                jobObject?.TryAssign(process);
                try
                {
                    // Capture and trim encodes must never compete with the running game or
                    // Playnite's UI thread for CPU.
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to lower ffmpeg process priority.");
                }

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to start ffmpeg: {_ffmpegPath}");
                try
                {
                    process.Dispose();
                }
                catch
                {
                }

                return false;
            }
        }

        /// <summary>
        /// Waits for the process to exit within the timeout. Kills the process (and still waits
        /// briefly for the exit) when the timeout elapses. Returns the exit code, or null when
        /// the process never started or had to be killed without reporting one.
        /// </summary>
        public async Task<int?> WaitForExitAsync(TimeSpan timeout)
        {
            if (_process == null)
            {
                return null;
            }

            var completed = await Task.WhenAny(_exited.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed == _exited.Task)
            {
                return _exited.Task.Result;
            }

            Kill();
            var killed = await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            return killed == _exited.Task ? _exited.Task.Result : (int?)null;
        }

        /// <summary>
        /// Asks ffmpeg to stop by writing "q" to stdin (flushes buffered segments cleanly), waits
        /// for the grace period, then kills the process if it is still running.
        /// </summary>
        public async Task StopGracefullyAsync(TimeSpan grace)
        {
            if (_process == null || HasExited)
            {
                return;
            }

            try
            {
                _process.StandardInput.Write("q");
                _process.StandardInput.Flush();
            }
            catch
            {
                // stdin already closed — fall through to the kill path.
            }

            var completed = await Task.WhenAny(_exited.Task, Task.Delay(grace)).ConfigureAwait(false);
            if (completed != _exited.Task)
            {
                Kill();
            }
        }

        public void Kill()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch
            {
                // Already exited or inaccessible.
            }
        }

        private void AppendStdErr(string line)
        {
            if (line == null)
            {
                return;
            }

            lock (_outputLock)
            {
                _stdErrTail.Enqueue(line);
                while (_stdErrTail.Count > StdErrTailLines)
                {
                    _stdErrTail.Dequeue();
                }
            }
        }

        private void AppendStdOut(string line)
        {
            if (line == null || !_captureStdOut)
            {
                return;
            }

            lock (_outputLock)
            {
                if (_stdOutLines.Count < StdOutMaxLines)
                {
                    _stdOutLines.Add(line);
                }
            }
        }

        private static int SafeExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Kill();
            try
            {
                _process?.Dispose();
            }
            catch
            {
            }

            _process = null;
        }
    }
}
