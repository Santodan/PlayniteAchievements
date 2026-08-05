using System;
using System.Globalization;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Best-effort rolling capture of audio into short WAV chunks written next to the video segments,
    /// so clip export can mux matching sound. The source is chosen by settings: all system audio
    /// (WASAPI loopback on the default render endpoint) or just the game process's audio (per-process
    /// loopback, <see cref="ProcessLoopbackCapture"/>, degrading to full system on failure or older
    /// Windows), optionally with the default microphone mixed in. Chunk names mirror the video
    /// convention (aud_yyyyMMdd-HHmmss.wav, local wall-clock) and rotate every
    /// <see cref="UnlockRecordingService.SegmentSeconds"/> seconds.
    ///
    /// A single pump thread reads the (optionally mixed) audio at a wall-clock pace and writes it,
    /// so silence — WASAPI loopback delivers no buffers during digital silence — still advances the
    /// chunk in real time (the buffers zero-fill). Any failure logs one warning and leaves the video
    /// pipeline untouched; NAudio types are confined to this file and ProcessLoopbackCapture.
    /// </summary>
    internal sealed class AudioLoopbackRecorder : IDisposable
    {
        // Wall-clock pump cadence and buffered-provider depth.
        private const int PumpIntervalMs = 50;
        private const int BufferSeconds = 5;

        private readonly string _bufferDirectory;
        private readonly ILogger _logger;
        private readonly RecordingAudioSource _source;
        private readonly bool _includeMicrophone;
        private readonly Func<int?> _gameProcessId;
        private readonly object _gate = new object();

        private IWaveIn _systemCapture;
        private IWaveIn _micCapture;
        private BufferedWaveProvider _systemBuffer;
        private BufferedWaveProvider _micBuffer;
        private ISampleProvider _mix;
        private WaveFormat _outputFormat;

        private WaveFileWriter _writer;
        private long _chunkSamplesWritten;
        private double _chunkStartWallClockSamples;
        private DateTime _pumpStartUtc;
        private Thread _pumpThread;
        private volatile bool _running;
        private bool _failed;
        private bool _stopped;

        public AudioLoopbackRecorder(
            string bufferDirectory,
            ILogger logger,
            RecordingAudioSource source = RecordingAudioSource.FullSystem,
            bool includeMicrophone = false,
            Func<int?> gameProcessId = null,
            bool capturePlayniteChimes = false)
        {
            _bufferDirectory = bufferDirectory;
            _logger = logger;
            _source = source;
            _includeMicrophone = includeMicrophone;
            _gameProcessId = gameProcessId;
            _capturePlayniteChimes = capturePlayniteChimes;
        }

        // When true this instance is the chime sidecar: it records ONLY Playnite's process tree
        // (where UniPlaySong plays the unlock chimes) into chm_*.wav chunks. The main track
        // excludes that same tree, so the clip re-encode can mix exactly this wave's chime back
        // in at the composited toast without other waves' chimes or any game-audio damage.
        private readonly bool _capturePlayniteChimes;

        /// <summary>
        /// Whether the chime sidecar track can exist on this machine (per-process loopback,
        /// Windows 10 19041+).
        /// </summary>
        public static bool IsChimeCaptureSupported => ProcessLoopbackCapture.IsSupported;

        /// <summary>
        /// Builds the capture graph and starts the pump. Returns false (after one Warn log) when audio
        /// capture is unavailable, leaving the caller's video pipeline untouched.
        /// </summary>
        public bool Start()
        {
            lock (_gate)
            {
                if (_stopped || _systemCapture != null)
                {
                    return false;
                }

                try
                {
                    _systemCapture = CreateSystemCapture();
                    _systemBuffer = NewBuffer(_systemCapture.WaveFormat);
                    _systemCapture.DataAvailable += (s, e) => Append(_systemBuffer, e);

                    ISampleProvider systemSamples = _systemBuffer.ToSampleProvider();

                    if (_includeMicrophone)
                    {
                        try
                        {
                            _micCapture = new WasapiCapture(); // default capture endpoint (microphone)
                            _micBuffer = NewBuffer(_micCapture.WaveFormat);
                            _micCapture.DataAvailable += (s, e) => Append(_micBuffer, e);

                            var micSamples = MatchFormat(_micBuffer.ToSampleProvider(), systemSamples.WaveFormat);
                            var mixer = new MixingSampleProvider(new[] { systemSamples, micSamples })
                            {
                                ReadFully = true,
                            };
                            _mix = mixer;
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn(ex, "[Recording] Microphone capture could not start; recording system audio only.");
                            DisposeCapture(ref _micCapture);
                            _micBuffer = null;
                            _mix = systemSamples;
                        }
                    }
                    else
                    {
                        _mix = systemSamples;
                    }

                    _outputFormat = _mix.WaveFormat;

                    OpenChunkLocked();
                    _systemCapture.StartRecording();
                    _micCapture?.StartRecording();

                    _pumpStartUtc = DateTime.UtcNow;
                    _running = true;
                    _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "PA-AudioPump" };
                    _pumpThread.Start();

                    _logger?.Info(
                        $"[Recording] Audio capture started (source={(_capturePlayniteChimes ? "PlayniteChimes" : _source.ToString())}, mic={_includeMicrophone}, {_outputFormat}).");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[Recording] Audio capture could not start; this session's clips will have no sound.");
                    _failed = true;
                    CleanupLocked();
                    return false;
                }
            }
        }

        /// <summary>
        /// Builds the system-audio source from the configured mode. Game-only uses per-process
        /// loopback scoped to the resolved game pid, degrading to full-system loopback (with one log
        /// line) when the pid is unknown, the OS is too old, or activation fails.
        /// </summary>
        private IWaveIn CreateSystemCapture()
        {
            if (_capturePlayniteChimes)
            {
                // No fallback: a full-system fallback here would duplicate the main track.
                return new ProcessLoopbackCapture(
                    System.Diagnostics.Process.GetCurrentProcess().Id, includeProcessTree: true);
            }

            if (_source == RecordingAudioSource.GameOnly)
            {
                var pid = _gameProcessId?.Invoke();
                if (pid.HasValue && pid.Value > 0 && ProcessLoopbackCapture.IsSupported)
                {
                    try
                    {
                        return new ProcessLoopbackCapture(pid.Value, includeProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, "[Recording] Per-process (game-only) audio capture failed; using full system audio.");
                    }
                }
                else
                {
                    _logger?.Info("[Recording] Game-only audio unavailable (no pid or OS < 19041); using full system audio.");
                }
            }

            // Full system audio minus Playnite's own process tree: the plugin's unlock chimes
            // (UniPlaySong plays inside Playnite) never land in clip audio — clips composite
            // their toast at the unlock moment, so the real chime rarely aligns with the card
            // and other waves' chimes would pollute the clip. Game and desktop audio are
            // untouched. Degrades to plain loopback on older Windows or activation failure.
            if (ProcessLoopbackCapture.IsSupported)
            {
                try
                {
                    return new ProcessLoopbackCapture(
                        System.Diagnostics.Process.GetCurrentProcess().Id, includeProcessTree: false);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[Recording] Playnite-excluded audio capture failed; using full system audio.");
                }
            }

            return new WasapiLoopbackCapture();
        }

        private static BufferedWaveProvider NewBuffer(WaveFormat format)
        {
            return new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(BufferSeconds),
                DiscardOnBufferOverflow = true,
                ReadFully = true, // zero-fill on underrun so the mix stays continuous in real time
            };
        }

        /// <summary>Resamples/rechannels a source to match the target format (both IEEE float here).</summary>
        private static ISampleProvider MatchFormat(ISampleProvider source, WaveFormat target)
        {
            if (source.WaveFormat.SampleRate != target.SampleRate)
            {
                source = new WdlResamplingSampleProvider(source, target.SampleRate);
            }

            if (source.WaveFormat.Channels == 1 && target.Channels == 2)
            {
                source = new MonoToStereoSampleProvider(source);
            }
            else if (source.WaveFormat.Channels == 2 && target.Channels == 1)
            {
                source = new StereoToMonoSampleProvider(source);
            }

            return source;
        }

        private void Append(BufferedWaveProvider buffer, WaveInEventArgs e)
        {
            if (e == null || e.BytesRecorded <= 0)
            {
                return;
            }

            try
            {
                buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
            catch
            {
                // Overflow is discarded by configuration; ignore transient add failures.
            }
        }

        /// <summary>
        /// Reads the (optionally mixed) audio at a wall-clock pace and writes it, rotating chunks on
        /// the segment interval. Pacing to elapsed wall time keeps chunks time-accurate through
        /// silence, so their filenames' timestamps match their true span for clip windowing.
        /// </summary>
        private void PumpLoop()
        {
            var channels = _outputFormat.Channels;
            var sampleRate = _outputFormat.SampleRate;
            var buffer = new float[sampleRate * channels]; // up to 1s per read

            try
            {
                while (_running)
                {
                    lock (_gate)
                    {
                        if (_writer == null)
                        {
                            break;
                        }

                        // Frames (per channel) that should have been written by now, wall-clock paced.
                        var elapsed = (DateTime.UtcNow - _pumpStartUtc).TotalSeconds;
                        var targetFrames = (long)(elapsed * sampleRate);
                        var writtenFrames = TotalFramesWritten();
                        var frames = (int)Math.Min(buffer.Length / channels, Math.Max(0, targetFrames - writtenFrames));
                        if (frames > 0)
                        {
                            var read = _mix.Read(buffer, 0, frames * channels);
                            if (read > 0)
                            {
                                _writer.WriteSamples(buffer, 0, read);
                                _chunkSamplesWritten += read;
                            }

                            if (_chunkSamplesWritten / channels >= (long)UnlockRecordingService.SegmentSeconds * sampleRate)
                            {
                                CloseChunkLocked();
                                OpenChunkLocked();
                            }
                        }
                    }

                    Thread.Sleep(PumpIntervalMs);
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    FailLocked(ex, "[Recording] Audio pump failed; audio capture stopped for this session.");
                }
            }
        }

        // Total per-channel frames written across the whole session (chunk base + current chunk).
        private long TotalFramesWritten()
        {
            return (long)_chunkStartWallClockSamples + _chunkSamplesWritten / Math.Max(1, _outputFormat.Channels);
        }

        /// <summary>Stops capture and closes the current chunk cleanly. Idempotent.</summary>
        public void Stop()
        {
            IWaveIn system, mic;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _running = false;
                system = _systemCapture;
                mic = _micCapture;
            }

            // Outside the gate: capture Dispose joins its thread, which may be delivering data.
            StopCapture(system);
            StopCapture(mic);

            lock (_gate)
            {
                CloseChunkLocked();
                _systemCapture = null;
                _micCapture = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static void StopCapture(IWaveIn capture)
        {
            if (capture == null)
            {
                return;
            }

            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
        }

        private static void DisposeCapture(ref IWaveIn capture)
        {
            try { capture?.Dispose(); } catch { }
            capture = null;
        }

        private void OpenChunkLocked()
        {
            var prefix = _capturePlayniteChimes
                ? RecordingPaths.ChimeChunkFilePrefix
                : RecordingPaths.AudioChunkFilePrefix;
            var name = prefix +
                       DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                       RecordingPaths.AudioChunkFileExtension;
            _chunkStartWallClockSamples = TotalFramesWritten();
            _writer = new WaveFileWriter(Path.Combine(_bufferDirectory, name), _outputFormat);
            _chunkSamplesWritten = 0;
        }

        private void CloseChunkLocked()
        {
            if (_writer == null)
            {
                return;
            }

            try { _writer.Dispose(); } catch { }
            _writer = null;
        }

        private void FailLocked(Exception ex, string message)
        {
            if (!_failed)
            {
                _failed = true;
                _logger?.Warn(ex, message);
            }

            _running = false;
            CloseChunkLocked();
        }

        private void CleanupLocked()
        {
            _running = false;
            CloseChunkLocked();
            DisposeCapture(ref _systemCapture);
            DisposeCapture(ref _micCapture);
            _systemBuffer = null;
            _micBuffer = null;
            _mix = null;
        }
    }
}
