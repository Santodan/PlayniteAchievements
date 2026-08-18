using System;
using System.Collections.Generic;
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
    internal enum PlayniteChimeCaptureMode
    {
        Unavailable,
        Clean,
        CancelGameReference
    }

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
    /// pipeline untouched; NAudio types are confined to this file, ProcessLoopbackCapture and
    /// RenderEndpointScan.
    ///
    /// While a controller audio endpoint exists, everything rendered to it is captured in parallel
    /// into hap_ chunks on the same paced writes (<see cref="StartHapticReference"/>). Process
    /// loopback mixes every endpoint a process renders to, so a game's haptic waveform is inside the
    /// main track; the clip export cancels it out against this reference.
    /// </summary>
    internal sealed class AudioLoopbackRecorder : IDisposable
    {
        // Wall-clock pump cadence and buffered-provider depth.
        private const int PumpIntervalMs = 50;
        private const int BufferSeconds = 5;

        // How long to wait for the first stamped packet before anchoring to the wall clock
        // instead. Only reached when the source is silent from the moment capture starts.
        private const int AnchorTimeoutMs = 750;

        // How often to look for a controller audio endpoint that was not there at capture start.
        private const int HapticRescanIntervalMs = 5000;

        private readonly string _bufferDirectory;
        private readonly ILogger _logger;
        private readonly RecordingAudioSource _source;
        private readonly bool _includeMicrophone;
        private readonly Func<int?> _gameProcessId;
        private readonly Func<int, bool?> _isGameInPlayniteTree;
        private readonly object _gate = new object();

        private IWaveIn _systemCapture;
        private IWaveIn _restoredGameCapture;
        private IWaveIn _micCapture;
        private readonly Dictionary<string, HapticEndpointCapture> _hapticCaptures =
            new Dictionary<string, HapticEndpointCapture>(StringComparer.OrdinalIgnoreCase);
        private readonly List<HapticEndpointCapture> _hapticPendingInstall = new List<HapticEndpointCapture>();
        private readonly HashSet<string> _hapticFailedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private MixingSampleProvider _hapticMixer;
        private Thread _hapticWatchThread;
        private long _hapticFramesWritten;
        private float _hapticPeak;
        private BufferedWaveProvider _systemBuffer;
        private BufferedWaveProvider _restoredGameBuffer;
        private BufferedWaveProvider _micBuffer;
        private ISampleProvider _mix;
        private ISampleProvider _hapticSamples;
        private WaveFormat _outputFormat;

        private WaveFileWriter _writer;
        private WaveFileWriter _gameReferenceWriter;
        private WaveFileWriter _hapticReferenceWriter;
        private long _chunkSamplesWritten;
        private double _chunkStartWallClockSamples;
        private DateTime _pumpStartUtc;
        private Thread _pumpThread;
        private volatile bool _running;
        private bool _failed;
        private bool _stopped;

        // Audio the ring buffer never accepted, in bytes of the capture format; see Append.
        private long _discardedBytes;
        private bool _restoreGameIntoFullSystem;
        private bool _writeGameReference;
        private bool _writeHapticReference;

        public AudioLoopbackRecorder(
            string bufferDirectory,
            ILogger logger,
            RecordingAudioSource source = RecordingAudioSource.FullSystem,
            bool includeMicrophone = false,
            Func<int?> gameProcessId = null,
            Func<int, bool?> isGameInPlayniteTree = null,
            bool capturePlayniteChimes = false)
        {
            _bufferDirectory = bufferDirectory;
            _logger = logger;
            _source = source;
            _includeMicrophone = includeMicrophone;
            _gameProcessId = gameProcessId;
            _isGameInPlayniteTree = isGameInPlayniteTree;
            _capturePlayniteChimes = capturePlayniteChimes;
        }

        // When true this instance is the chime sidecar: it records Playnite's process tree (where
        // UniPlaySong plays the unlock chimes) into chm_*.wav chunks. That tree can also contain a
        // game launched by Playnite; in that case the main recorder tees its restored game signal
        // to a reference WAV so the game can be cancelled before the chime is re-timed.
        private readonly bool _capturePlayniteChimes;

        /// <summary>
        /// Whether the chime sidecar track can exist on this machine (per-process loopback,
        /// Windows 10 19041+).
        /// </summary>
        public static bool IsChimeCaptureSupported => ProcessLoopbackCapture.IsSupported;

        /// <summary>
        /// How the Playnite-tree sidecar can be made into a chime-only signal for this main track.
        /// A game launched beneath Playnite requires a simultaneous game-only reference to be
        /// cancelled from that sidecar; a separate process tree is already clean.
        /// </summary>
        public PlayniteChimeCaptureMode ChimeCaptureMode { get; private set; }

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

                    if (_restoreGameIntoFullSystem)
                    {
                        try
                        {
                            var pid = _gameProcessId?.Invoke();
                            if (!pid.HasValue || pid.Value <= 0)
                            {
                                throw new InvalidOperationException("No game process is available for audio restoration.");
                            }

                            _restoredGameCapture = new ProcessLoopbackCapture(pid.Value, includeProcessTree: true);
                            _restoredGameBuffer = NewBuffer(_restoredGameCapture.WaveFormat);
                            _restoredGameCapture.DataAvailable += (s, e) => Append(_restoredGameBuffer, e);
                            var gameSamples = new ReferenceTeeSampleProvider(
                                _restoredGameBuffer.ToSampleProvider(),
                                WriteGameReferenceSamples);
                            systemSamples = new MixingSampleProvider(new[] { systemSamples, gameSamples })
                            {
                                ReadFully = true,
                            };
                        }
                        catch (Exception ex)
                        {
                            // An excluded Playnite-tree track without the game restored is worse than
                            // no isolation: it silently removes the very audio the user asked to keep.
                            // Fall back to ordinary system loopback and leave its live chime alone.
                            _logger?.Warn(
                                ex,
                                "[Recording] Game audio could not be restored into full-system capture; " +
                                "using plain full-system audio without chime re-timing.");
                            DisposeCapture(ref _restoredGameCapture);
                            _restoredGameBuffer = null;
                            DisposeCapture(ref _systemCapture);
                            _systemCapture = new WasapiLoopbackCapture();
                            _systemBuffer = NewBuffer(_systemCapture.WaveFormat);
                            _systemCapture.DataAvailable += (s, e) => Append(_systemBuffer, e);
                            systemSamples = _systemBuffer.ToSampleProvider();
                            _restoreGameIntoFullSystem = false;
                            _writeGameReference = false;
                            ChimeCaptureMode = PlayniteChimeCaptureMode.Unavailable;
                        }
                    }
                    else if (_writeGameReference)
                    {
                        // Tap the raw game signal before microphone mixing. Using the finished aud_
                        // track here would also subtract the microphone from the chime sidecar.
                        systemSamples = new ReferenceTeeSampleProvider(
                            systemSamples,
                            WriteGameReferenceSamples);
                    }

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
                    var hapticEndpoints = StartHapticReference();

                    // Haptic captures start as they are opened, in AttachHapticEndpoints.
                    _systemCapture.StartRecording();
                    _restoredGameCapture?.StartRecording();
                    _micCapture?.StartRecording();

                    // The timeline is anchored, and the first chunk opened, by the pump once it
                    // knows when the first packet's audio actually played -- see AwaitAnchor.
                    _running = true;
                    _pumpThread = new Thread(PumpLoop)
                    {
                        IsBackground = true,
                        Name = "PA-AudioPump",
                        // Background capture work: yield to the game and the shell. The pump is
                        // wall-clock paced and reads whatever accumulated, so a late wake costs
                        // nothing but a slightly larger read.
                        Priority = ThreadPriority.BelowNormal,
                    };
                    _pumpThread.Start();

                    _logger?.Info(
                        $"[Recording] Audio capture started (source={CaptureSourceName()}, mic={_includeMicrophone}, " +
                        $"{_outputFormat}{hapticEndpoints}).");
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

            var gamePid = _gameProcessId?.Invoke();
            var playnitePid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var gameInPlayniteTree = gamePid.HasValue && gamePid.Value > 0
                ? _isGameInPlayniteTree?.Invoke(gamePid.Value)
                : null;

            if (_source == RecordingAudioSource.GameOnly)
            {
                if (gamePid.HasValue && gamePid.Value > 0 && ProcessLoopbackCapture.IsSupported)
                {
                    try
                    {
                        // Scoped to the game's tree, so Playnite's chimes are outside it. The
                        // Playnite-tree sidecar can still contain the game (an emulator Playnite
                        // launched is inside both trees), and the tree probe can be wrong in either
                        // direction, so always tee the game reference and require verified
                        // cancellation instead of trusting the probe: a genuinely clean sidecar
                        // passes through as CleanNoGameDetected.
                        var gameOnly = new ProcessLoopbackCapture(gamePid.Value, includeProcessTree: true);
                        ChimeCaptureMode = PlayniteChimeCaptureMode.CancelGameReference;
                        _writeGameReference = true;

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
            // and other waves' chimes would pollute the clip. When the game is inside Playnite's
            // tree, its game-only process signal is restored into the main mix before it is written
            // and tee'd as the sidecar-cancellation reference. Unknown relationships deliberately
            // keep plain full-system audio and forego re-timing rather than risk removing the game.
            if (ProcessLoopbackCapture.IsSupported && gameInPlayniteTree.HasValue)
            {
                try
                {
                    var excluded = new ProcessLoopbackCapture(
                        playnitePid, includeProcessTree: false);
                    if (gameInPlayniteTree.Value)
                    {
                        // Excluding Playnite's tree also excludes a Playnite-launched game. Restore
                        // that game before the main WAV is written and tee the exact same samples to
                        // gam_*.wav for sidecar cancellation.
                        _restoreGameIntoFullSystem = true;
                        _writeGameReference = true;
                        ChimeCaptureMode = PlayniteChimeCaptureMode.CancelGameReference;
                    }
                    else
                    {
                        ChimeCaptureMode = PlayniteChimeCaptureMode.Clean;
                    }

                    return excluded;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[Recording] Playnite-excluded audio capture failed; using full system audio.");
                }
            }
            else if (ProcessLoopbackCapture.IsSupported)
            {
                _logger?.Info(
                    "[Recording] The game/Playnite process-tree relationship is unknown; " +
                    "using plain full-system audio so excluding Playnite cannot remove the game.");
            }

            // Plain system loopback is the last resort after process-scope setup fails. It carries
            // the live chime and has no lossless pre-encode way to remove it, so leave that audio
            // untouched rather than re-time a second copy.
            ChimeCaptureMode = PlayniteChimeCaptureMode.Unavailable;
            return new WasapiLoopbackCapture();
        }

        /// <summary>
        /// Starts a loopback capture of every controller audio endpoint on the machine, written
        /// alongside the main track as hap_ chunks. A DualSense plays its haptics as audio through
        /// its own endpoint, and process loopback mixes every endpoint the game renders to, so that
        /// waveform is inside the recorded audio; this is the copy the clip export cancels it with.
        /// <para>
        /// Returns the text appended to the capture-started log line, empty when no such endpoint
        /// exists — which is the case on any machine without a wired pad, including a Bluetooth
        /// DualSense (Bluetooth exposes no controller audio device).
        /// </para>
        /// </summary>
        private string StartHapticReference()
        {
            if (_capturePlayniteChimes)
            {
                // The sidecar is cancelled against the game reference, which carries the same
                // haptics; cleaning it separately would subtract them twice.
                return string.Empty;
            }

            // An empty mixer that reads as silence, so an endpoint arriving later can simply be
            // added to it: the reference track's format and identity never change mid-session.
            _hapticMixer = new MixingSampleProvider(_outputFormat) { ReadFully = true };
            _hapticSamples = _hapticMixer;

            var attached = AttachHapticEndpoints();
            StartHapticWatcher();
            return attached.Count == 0 ? string.Empty : ", haptics=" + string.Join("+", attached.ToArray());
        }

        /// <summary>
        /// Opens a capture for every controller endpoint not already attached, and returns the names
        /// of the ones opened. Endpoints that vanish are deliberately left attached: a dead capture
        /// contributes silence, which is harmless, while disposing one means joining its poll thread
        /// on whichever thread noticed.
        /// </summary>
        private List<string> AttachHapticEndpoints()
        {
            var opened = new List<string>();
            foreach (var endpoint in RenderEndpointScan.FindHapticEndpoints(_logger))
            {
                lock (_gate)
                {
                    if (_stopped || _hapticCaptures.ContainsKey(endpoint.DeviceId))
                    {
                        continue;
                    }
                }

                // Activation and StartRecording are COM work, kept off the gate so the pump thread
                // is never blocked behind a driver.
                IWaveIn capture = null;
                try
                {
                    capture = ProcessLoopbackCapture.ForEndpoint(endpoint.DeviceId);
                    var buffer = NewBuffer(capture.WaveFormat);
                    capture.DataAvailable += (s, e) => Append(buffer, e);
                    var entry = new HapticEndpointCapture
                    {
                        DeviceId = endpoint.DeviceId,
                        Name = endpoint.Name,
                        Capture = capture,
                        Buffer = buffer,
                        Provider = MatchFormat(buffer.ToSampleProvider(), _outputFormat),
                    };

                    capture.StartRecording();
                    lock (_gate)
                    {
                        _hapticCaptures[endpoint.DeviceId] = entry;
                        _hapticPendingInstall.Add(entry);
                    }

                    opened.Add(endpoint.Name);
                }
                catch (Exception ex)
                {
                    DisposeCapture(ref capture);

                    // Retried on every later tick — an endpoint can be busy for a moment — but
                    // reported once per device, or a stuck one would fill the log at rescan cadence.
                    if (_hapticFailedDeviceIds.Add(endpoint.DeviceId))
                    {
                        _logger?.Warn(
                            ex,
                            $"[Recording] Controller endpoint '{endpoint.Name}' could not be captured; " +
                            "its haptics will stay in this session's clip audio.");
                    }
                }
            }

            return opened;
        }

        /// <summary>
        /// Re-checks for controller endpoints for the life of the session. Detecting once at capture
        /// start is not enough: a pad connected after the game launched has no endpoint yet, and a
        /// pad that re-enumerates (Windows names the new instance "2-", "3-", …) leaves the original
        /// endpoint id dead while the game renders its haptics to the new one.
        /// </summary>
        private void StartHapticWatcher()
        {
            _hapticWatchThread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(HapticRescanIntervalMs);
                    lock (_gate)
                    {
                        if (_stopped || _failed)
                        {
                            return;
                        }
                    }

                    try
                    {
                        var opened = AttachHapticEndpoints();
                        if (opened.Count > 0)
                        {
                            _logger?.Info(
                                "[Recording] Controller endpoint appeared mid-session; capturing " +
                                string.Join("+", opened.ToArray()) + " as a haptic reference.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[Recording] A haptic endpoint re-scan failed.");
                    }
                }
            })
            {
                IsBackground = true,
                Name = "PA-HapticWatch",
            };
            _hapticWatchThread.Start();
        }

        /// <summary>
        /// Adds newly opened endpoints to the reference mix, at a chunk boundary so the reference
        /// track's sample positions keep matching the main track's. Their buffers are dropped first:
        /// whatever accumulated since activation belongs before this chunk, and mixing it in here
        /// would place the reference ahead of the audio it has to be subtracted from.
        /// </summary>
        private void InstallPendingHapticCapturesLocked()
        {
            if (_hapticPendingInstall.Count == 0)
            {
                return;
            }

            foreach (var entry in _hapticPendingInstall)
            {
                try
                {
                    entry.Buffer.ClearBuffer();
                    _hapticMixer.AddMixerInput(entry.Provider);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"[Recording] Controller endpoint '{entry.Name}' could not join the haptic reference.");
                }
            }

            _hapticPendingInstall.Clear();
            _writeHapticReference = true;
        }

        private string CaptureSourceName()
        {
            if (_capturePlayniteChimes)
            {
                return "PlayniteChimes";
            }

            return _source.ToString();
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
        private void LogTimelineNotices(params IWaveIn[] captures)
        {
            var discarded = Interlocked.Read(ref _discardedBytes);
            var paddedFrames = 0L;
            foreach (var capture in captures ?? new IWaveIn[0])
            {
                paddedFrames += (capture as ProcessLoopbackCapture)?.PaddedGapFrames ?? 0;
            }
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
            // Allocated whether or not an endpoint is attached yet: one can appear mid-session, and
            // the pump must be able to write its reference the moment it joins.
            var hapticBuffer = _capturePlayniteChimes ? null : new float[buffer.Length];

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
                                WriteHapticReferenceLocked(hapticBuffer, read);
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
            var restoredGame = _restoredGameCapture as ProcessLoopbackCapture;
            var deadline = CaptureTimelineClock.UtcNow.AddMilliseconds(AnchorTimeoutMs);

            while (_running)
            {
                var primaryUtc = stamped?.FirstPacketCaptureUtc;
                var gameUtc = restoredGame?.FirstPacketCaptureUtc;
                var packetUtc = primaryUtc.HasValue && gameUtc.HasValue
                    ? (primaryUtc.Value <= gameUtc.Value ? primaryUtc : gameUtc)
                    : primaryUtc ?? gameUtc;
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
            IWaveIn system, restoredGame, mic;
            IWaveIn[] haptics;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _running = false;
                system = _systemCapture;
                restoredGame = _restoredGameCapture;
                mic = _micCapture;
                haptics = HapticCapturesLocked();
                LogHapticReferenceLocked();
            }

            // Outside the gate: capture Dispose joins its thread, which may be delivering data.
            StopCapture(system);
            StopCapture(restoredGame);
            StopCapture(mic);
            foreach (var capture in haptics)
            {
                StopCapture(capture);
            }

            lock (_gate)
            {
                CloseChunkLocked();
                var tracked = new List<IWaveIn> { system, restoredGame };
                tracked.AddRange(haptics);
                LogTimelineNotices(tracked.ToArray());
                _systemCapture = null;
                _restoredGameCapture = null;
                _micCapture = null;
                _hapticCaptures.Clear();
                _hapticPendingInstall.Clear();
                _hapticMixer = null;
                _hapticSamples = null;
            }
        }

        private IWaveIn[] HapticCapturesLocked()
        {
            var captures = new IWaveIn[_hapticCaptures.Count];
            var index = 0;
            foreach (var entry in _hapticCaptures.Values)
            {
                captures[index++] = entry.Capture;
            }

            return captures;
        }

        /// <summary>
        /// Reports what the haptic reference actually recorded. A track that ran for the whole
        /// session but peaked at zero is the difference between "no controller endpoint" and "the
        /// endpoint we captured was not the one the game plays haptics to" — a distinction no other
        /// line in the log can make, and the one that decides where to look next.
        /// </summary>
        private void LogHapticReferenceLocked()
        {
            if (_hapticCaptures.Count == 0)
            {
                return;
            }

            var seconds = _hapticFramesWritten / (double)Math.Max(1, _outputFormat?.SampleRate ?? 1);
            var names = new List<string>();
            foreach (var entry in _hapticCaptures.Values)
            {
                names.Add(entry.Name);
            }

            _logger?.Info(
                $"[Recording] Haptic reference: {seconds.ToString("0.0", CultureInfo.InvariantCulture)}s from " +
                string.Join("+", names.ToArray()) +
                $", peak {_hapticPeak.ToString("0.0000", CultureInfo.InvariantCulture)}" +
                (_hapticPeak <= 0 ? " (silent — nothing was rendered to it)" : string.Empty) + ".");
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

        private void DisposeHapticCaptures()
        {
            foreach (var entry in _hapticCaptures.Values)
            {
                try { entry.Capture?.Dispose(); } catch { }
            }

            _hapticCaptures.Clear();
            _hapticPendingInstall.Clear();
            _hapticMixer = null;
            _hapticSamples = null;
        }

        private void OpenChunkLocked()
        {
            // A chunk boundary is the only place a new endpoint may join the reference, so the two
            // tracks stay sample-aligned; see InstallPendingHapticCapturesLocked.
            InstallPendingHapticCapturesLocked();

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
            if (_writeGameReference)
            {
                var referenceName = RecordingPaths.BuildAudioChunkFileName(
                    RecordingPaths.GameReferenceChunkFilePrefix, startUtc);
                _gameReferenceWriter = new WaveFileWriter(
                    Path.Combine(_bufferDirectory, referenceName), _outputFormat);
            }

            if (_writeHapticReference)
            {
                var hapticName = RecordingPaths.BuildAudioChunkFileName(
                    RecordingPaths.HapticReferenceChunkFilePrefix, startUtc);
                _hapticReferenceWriter = new WaveFileWriter(
                    Path.Combine(_bufferDirectory, hapticName), _outputFormat);
            }

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
            try { _gameReferenceWriter?.Dispose(); } catch { }
            _gameReferenceWriter = null;
            try { _hapticReferenceWriter?.Dispose(); } catch { }
            _hapticReferenceWriter = null;
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
            DisposeCapture(ref _restoredGameCapture);
            DisposeCapture(ref _micCapture);
            DisposeHapticCaptures();
            _systemBuffer = null;
            _restoredGameBuffer = null;
            _micBuffer = null;
            _mix = null;
            _hapticSamples = null;
        }

        private void WriteGameReferenceSamples(float[] samples, int offset, int count)
        {
            _gameReferenceWriter?.WriteSamples(samples, offset, count);
        }

        /// <summary>
        /// Writes exactly as many haptic-reference samples as the main track just took, zero-filling
        /// any shortfall. Matching the counts sample for sample is what keeps the two tracks on one
        /// timeline: the export aligns them by chunk name and position, so a reference that ran short
        /// would drag every later sample out of step with the audio it has to be subtracted from.
        /// </summary>
        private void WriteHapticReferenceLocked(float[] hapticBuffer, int samples)
        {
            if (_hapticReferenceWriter == null || _hapticSamples == null || hapticBuffer == null)
            {
                return;
            }

            var read = _hapticSamples.Read(hapticBuffer, 0, samples);
            if (read < samples)
            {
                Array.Clear(hapticBuffer, Math.Max(0, read), samples - Math.Max(0, read));
            }

            for (var i = 0; i < samples; i++)
            {
                var magnitude = Math.Abs(hapticBuffer[i]);
                if (magnitude > _hapticPeak)
                {
                    _hapticPeak = magnitude;
                }
            }

            _hapticReferenceWriter.WriteSamples(hapticBuffer, 0, samples);
            _hapticFramesWritten += samples / Math.Max(1, _outputFormat.Channels);
        }

        /// <summary>One controller endpoint's capture and its place in the reference mix.</summary>
        private sealed class HapticEndpointCapture
        {
            public string DeviceId;
            public string Name;
            public IWaveIn Capture;
            public BufferedWaveProvider Buffer;
            public ISampleProvider Provider;
        }

        /// <summary>
        /// Copies exactly the raw game samples consumed by the main mixer into the reference WAV.
        /// Tapping the same read, rather than running another pump, keeps microphone and unrelated
        /// system audio out of the cancellation reference.
        /// </summary>
        private sealed class ReferenceTeeSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly Action<float[], int, int> _tap;

            public ReferenceTeeSampleProvider(ISampleProvider source, Action<float[], int, int> tap)
            {
                _source = source;
                _tap = tap;
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                var read = _source.Read(buffer, offset, count);
                if (read > 0)
                {
                    _tap(buffer, offset, read);
                }

                return read;
            }
        }
    }
}
