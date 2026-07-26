using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using NAudio.Wave;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Best-effort rolling capture of system audio (WASAPI loopback via NAudio) into short WAV
    /// chunks written next to the video segments, so clip export can mux matching sound. Chunk
    /// names mirror the video convention (aud_yyyyMMdd-HHmmss.wav, local wall-clock time) and
    /// rotate every <see cref="UnlockRecordingService.SegmentSeconds"/> seconds. WASAPI loopback
    /// delivers no callbacks during digital silence, so chunks are kept time-accurate by
    /// zero-padding up to the wall-clock elapsed length (before each append and on every close),
    /// with a timer driving rotation so silence alone still rotates and pads. Any failure logs
    /// one warning and leaves the video pipeline untouched; NAudio types are confined to this
    /// file so the rest of the feature stays testable without NAudio.
    /// </summary>
    internal sealed class AudioLoopbackRecorder : IDisposable
    {
        private const int RotationTimerMs = 1000;

        /// <summary>
        /// Wall-clock shortfall tolerated before zero-padding, so normal callback jitter
        /// (~100 ms WASAPI event cadence) never injects silence into continuous audio.
        /// </summary>
        private const double PadToleranceSeconds = 0.2;

        private readonly string _bufferDirectory;
        private readonly ILogger _logger;
        private readonly object _gate = new object();

        private WasapiLoopbackCapture _capture;
        private WaveFileWriter _writer;
        private Stopwatch _chunkClock;
        private long _chunkBytesWritten;
        private Timer _rotationTimer;
        private bool _failed;
        private bool _stopped;

        public AudioLoopbackRecorder(string bufferDirectory, ILogger logger)
        {
            _bufferDirectory = bufferDirectory;
            _logger = logger;
        }

        /// <summary>
        /// Starts the loopback capture and the first chunk. Returns false (after one Warn log)
        /// when audio capture is unavailable — no audio device, missing NAudio, COM errors —
        /// leaving the caller's video pipeline untouched.
        /// </summary>
        public bool Start()
        {
            lock (_gate)
            {
                if (_stopped || _capture != null)
                {
                    return false;
                }

                try
                {
                    _capture = new WasapiLoopbackCapture();
                    _capture.DataAvailable += OnDataAvailable;
                    OpenChunkLocked();
                    _capture.StartRecording();
                    _rotationTimer = new Timer(_ => RotationTick(), null, RotationTimerMs, RotationTimerMs);
                    _logger?.Info($"[Recording] Audio loopback capture started ({_capture.WaveFormat}).");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[Recording] Audio capture could not start; this session's clips will have no sound.");
                    _failed = true;
                    try
                    {
                        _writer?.Dispose();
                    }
                    catch
                    {
                    }

                    _writer = null;
                    try
                    {
                        // Safe to dispose under the gate here: the capture thread never ran, so
                        // it cannot be blocked in OnDataAvailable.
                        _capture?.Dispose();
                    }
                    catch
                    {
                    }

                    _capture = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// Stops the capture and closes the current chunk cleanly (padded to its wall-clock
        /// length) so pending clip exports can read it. Idempotent.
        /// </summary>
        public void Stop()
        {
            WasapiLoopbackCapture capture;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _rotationTimer?.Dispose();
                _rotationTimer = null;
                capture = _capture;
                _capture = null;
                CloseChunkLocked();
            }

            // Outside the gate: WasapiCapture.Dispose joins the capture thread, which may itself
            // be waiting on the gate in OnDataAvailable.
            if (capture != null)
            {
                try
                {
                    capture.DataAvailable -= OnDataAvailable;
                }
                catch
                {
                }

                try
                {
                    capture.StopRecording();
                }
                catch
                {
                }

                try
                {
                    capture.Dispose();
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            lock (_gate)
            {
                if (_writer == null || e == null || e.BytesRecorded <= 0)
                {
                    return;
                }

                try
                {
                    PadToWallClockLocked();
                    _writer.Write(e.Buffer, 0, e.BytesRecorded);
                    _chunkBytesWritten += e.BytesRecorded;
                }
                catch (Exception ex)
                {
                    FailLocked(ex, "[Recording] Audio chunk write failed; audio capture stopped for this session.");
                }
            }
        }

        /// <summary>
        /// Timer tick (1s): pads silence up to now so a fully silent chunk still grows, and
        /// rotates to a fresh chunk once the current one covers a full segment length.
        /// </summary>
        private void RotationTick()
        {
            lock (_gate)
            {
                if (_writer == null)
                {
                    return;
                }

                try
                {
                    PadToWallClockLocked();
                    if (_chunkClock.Elapsed.TotalSeconds >= UnlockRecordingService.SegmentSeconds)
                    {
                        CloseChunkLocked();
                        OpenChunkLocked();
                    }
                }
                catch (Exception ex)
                {
                    FailLocked(ex, "[Recording] Audio chunk rotation failed; audio capture stopped for this session.");
                }
            }
        }

        private void OpenChunkLocked()
        {
            var name = RecordingCommandBuilder.AudioChunkFilePrefix +
                       DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                       RecordingCommandBuilder.AudioChunkFileExtension;
            _writer = new WaveFileWriter(Path.Combine(_bufferDirectory, name), _capture.WaveFormat);
            _chunkBytesWritten = 0;
            _chunkClock = Stopwatch.StartNew();
        }

        private void CloseChunkLocked()
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                PadToWallClockLocked();
            }
            catch
            {
            }

            try
            {
                _writer.Dispose();
            }
            catch
            {
            }

            _writer = null;
        }

        /// <summary>
        /// Zero-pads the current chunk up to its wall-clock elapsed length (block-aligned) when
        /// it has fallen more than the tolerance behind — WASAPI loopback simply stops delivering
        /// buffers during digital silence, and without padding the chunk would play back shorter
        /// than the time span its filename claims. Zero bytes are silence in both integer PCM and
        /// IEEE-float formats.
        /// </summary>
        private void PadToWallClockLocked()
        {
            var format = _writer.WaveFormat;
            var blockAlign = Math.Max(1, format.BlockAlign);
            var expectedBytes = (long)(_chunkClock.Elapsed.TotalSeconds * format.AverageBytesPerSecond);
            expectedBytes -= expectedBytes % blockAlign;
            var missing = expectedBytes - _chunkBytesWritten;
            if (missing <= (long)(PadToleranceSeconds * format.AverageBytesPerSecond))
            {
                return;
            }

            missing -= missing % blockAlign;
            var zeros = new byte[8192];
            while (missing > 0)
            {
                var count = (int)Math.Min(zeros.Length, missing);
                _writer.Write(zeros, 0, count);
                _chunkBytesWritten += count;
                missing -= count;
            }
        }

        /// <summary>One Warn per session for operational failures, then the recorder goes inert.</summary>
        private void FailLocked(Exception ex, string message)
        {
            if (!_failed)
            {
                _failed = true;
                _logger?.Warn(ex, message);
            }

            _rotationTimer?.Dispose();
            _rotationTimer = null;
            try
            {
                _writer?.Dispose();
            }
            catch
            {
            }

            _writer = null;
        }
    }
}
