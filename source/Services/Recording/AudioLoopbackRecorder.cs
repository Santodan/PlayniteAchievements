using System;
using System.Globalization;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Best-effort rolling capture of audio into short WAV chunks written next to the video segments,
    /// so clip export can mux matching sound. The source is chosen by settings: all system audio
    /// (WASAPI loopback on the default render endpoint) or just the game process's audio (per-process
    /// loopback, <see cref="ProcessLoopbackCapture"/>, degrading to full system on failure or older
    /// Windows), optionally with the default microphone mixed in. Chunk names mirror the video
    /// convention (aud_yyyyMMdd-HHmmssfffZ.wav, UTC timeline) and rotate every
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

        // How long to wait for the first stamped packet before anchoring to the wall clock
        // instead. Only reached when the source is silent from the moment capture starts.
        private const int AnchorTimeoutMs = 750;

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

        // Audio the ring buffer never accepted, in bytes of the capture format; see Append.
        private long _discardedBytes;

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
        /// Whether this track is guaranteed to carry none of Playnite's own audio — true when the
        /// source is per-process loopback scoped to the game, or full system with Playnite's tree
        /// excluded; false when activation failed and the capture degraded to plain system
        /// loopback, which includes the unlock chimes. Only meaningful after <see cref="Start"/>.
        /// <para>
        /// The chime sidecar may only run alongside a track that excludes Playnite. Mixing the
        /// sidecar's chime into a clip whose main track already contains that chime plays it
        /// twice — once where it really sounded, once re-timed to the composited toast.
        /// </para>
        /// </summary>
        public bool ExcludesPlayniteAudio { get; private set; }

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

                    _systemCapture.StartRecording();
                    _micCapture?.StartRecording();

                    // The timeline is anchored, and the first chunk opened, by the pump once it
                    // knows when the first packet's audio actually played -- see AwaitAnchor.
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
                        // Scoped to the game's tree, so Playnite's chimes are outside it.
                        var gameOnly = new ProcessLoopbackCapture(pid.Value, includeProcessTree: true);
                        ExcludesPlayniteAudio = true;
                        return gameOnly;
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
                    var excluded = new ProcessLoopbackCapture(
                        System.Diagnostics.Process.GetCurrentProcess().Id, includeProcessTree: false);
                    ExcludesPlayniteAudio = true;
                    return excluded;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[Recording] Playnite-excluded audio capture failed; using full system audio.");
                }
            }

            // Plain system loopback carries Playnite's chimes, so ExcludesPlayniteAudio stays
            // false and the caller must not run the chime sidecar against this track.
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
            if (buffer == null || e == null || e.BytesRecorded <= 0)
            {
                return;
            }

            try
            {
                // DiscardOnBufferOverflow drops the excess without telling anyone, and a drop shifts
                // everything after it against picture. Count it so a report of "the audio drifts" can
                // be told apart from a timeline bug.
                var free = buffer.BufferLength - buffer.BufferedBytes;
                if (e.BytesRecorded > free)
                {
                    Interlocked.Add(ref _discardedBytes, e.BytesRecorded - Math.Max(0, free));
                }

                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
            catch
            {
                Interlocked.Add(ref _discardedBytes, e.BytesRecorded);
            }
        }

        /// <summary>
        /// Reports whatever this track lost or stood in for. Silent when nothing did, so a line here
        /// always means the recorded audio does not represent an unbroken stretch of real time.
        /// </summary>
        private void LogTimelineNotices(IWaveIn systemCapture)
        {
            var discarded = Interlocked.Read(ref _discardedBytes);
            var paddedFrames = (systemCapture as ProcessLoopbackCapture)?.PaddedGapFrames ?? 0;
            if (discarded == 0 && paddedFrames == 0)
            {
                return;
            }

            var bytesPerSecond = Math.Max(1, _outputFormat?.AverageBytesPerSecond ?? 1);
            var sampleRate = Math.Max(1, _outputFormat?.SampleRate ?? 1);
            _logger?.Warn(
                $"[Recording] Audio track has gaps: {discarded / (double)bytesPerSecond:0.###}s dropped to " +
                $"buffer overflow, {paddedFrames / (double)sampleRate:0.###}s of engine dropouts padded " +
                "with silence.");
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
                if (!AwaitAnchor())
                {
                    return;
                }

                while (_running)
                {
                    lock (_gate)
                    {
                        if (_writer == null)
                        {
                            break;
                        }

                        // Frames (per channel) that should have been written by now, wall-clock paced.
                        var elapsed = (CaptureTimelineClock.UtcNow - _pumpStartUtc).TotalSeconds;
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

        /// <summary>
        /// Fixes the instant that WAV position zero represents, then opens the first chunk.
        /// Returns false when the recorder stopped before that happened.
        /// <para>
        /// Packets arrive later than the audio they carry. Anchoring to the moment capture started
        /// would zero-fill that delay and, because the pump then drains at exactly real time, the
        /// backlog never clears -- every sample stays late by the delay for the whole session,
        /// which is audio lagging video in every clip. Anchoring to when the first packet's audio
        /// actually played removes the offset instead of carrying it.
        /// </para>
        /// <para>
        /// A source that reports no stamp (the plain-loopback fallback) anchors immediately, as
        /// before. A process-loopback source that is silent at startup delivers no packets at all,
        /// so the wait is bounded and falls back to the same behavior; only the pre-roll between
        /// here and the timeout is given up, and the buffer is many seconds deep.
        /// </para>
        /// </summary>
        private bool AwaitAnchor()
        {
            var stamped = _systemCapture as ProcessLoopbackCapture;
            var deadline = CaptureTimelineClock.UtcNow.AddMilliseconds(AnchorTimeoutMs);

            while (_running)
            {
                var packetUtc = stamped?.FirstPacketCaptureUtc;
                if (stamped == null || packetUtc.HasValue || CaptureTimelineClock.UtcNow >= deadline)
                {
                    lock (_gate)
                    {
                        if (!_running || _stopped)
                        {
                            return false;
                        }

                        _pumpStartUtc = packetUtc ?? CaptureTimelineClock.UtcNow;
                        OpenChunkLocked();
                    }

                    return true;
                }

                Thread.Sleep(PumpIntervalMs);
            }

            return false;
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
                LogTimelineNotices(system);
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
            _chunkStartWallClockSamples = TotalFramesWritten();

            // Stamp from the pump's own timeline rather than the wall clock at rotation. Clip
            // planning maps these names onto sample positions, so the name has to say where in the
            // timeline the chunk begins, not when the rotation happened to run -- the two differ by
            // however far past the segment length the last write pushed the chunk.
            var startUtc = _pumpStartUtc.AddSeconds(
                _chunkStartWallClockSamples / (double)_outputFormat.SampleRate);
            var name = RecordingPaths.BuildAudioChunkFileName(prefix, startUtc);

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
