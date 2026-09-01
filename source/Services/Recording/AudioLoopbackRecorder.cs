using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        CancelGameReference
    }

    /// <summary>
    /// Best-effort rolling capture of audio into short WAV chunks written next to the video segments,
    /// so clip export can mux matching sound. Both settings record the actual default render
    /// endpoint. Full System keeps that mix apart from the verified removal of Playnite's own
    /// sounds so the chime can be re-timed onto the composited toast; Game Only subtracts a
    /// simultaneous non-game process reference after export verification. Both fall back to the
    /// audible endpoint mix. The optional
    /// microphone is mixed into either mode. Chunk names mirror the video
    /// convention (aud_yyyyMMdd-HHmmssfffffffZ.wav, UTC timeline) and rotate every
    /// <see cref="UnlockRecordingService.SegmentSeconds"/> seconds.
    ///
    /// A single pump thread reads the (optionally mixed) audio at a wall-clock pace and writes it,
    /// so silence — WASAPI loopback delivers no buffers during digital silence — still advances the
    /// chunk in real time (the buffers zero-fill). Any failure logs one warning and leaves the video
    /// pipeline untouched; NAudio types are confined to this file, ProcessLoopbackCapture and
    /// RenderEndpointScan.
    ///
    /// User-facing capture always comes from one real render endpoint, never the virtual
    /// all-process/all-endpoint loopback device. A separate controller actuator endpoint therefore
    /// cannot enter the recording. If the DualSense itself is the default output, its proven native
    /// layout is split and only the front L/R program channels are retained.
    ///
    /// The chime sidecar and its game-only cancellation reference are likewise written directly from
    /// packet stamps. They are independent process-loopback clients; sending either through a 50 ms
    /// pump caused millisecond alignment steps whenever the chime's render stream changed the graph.
    /// </summary>
    internal sealed class AudioLoopbackRecorder : IDisposable
    {
        // Wall-clock pump cadence and buffered-provider depth.
        private const int PumpIntervalMs = 50;

        // The ring has to absorb a maximal gap pad without evicting the audio around it. An
        // endpoint loopback delivers nothing while the endpoint is silent but its device clock
        // keeps running, so ProcessLoopbackCapture reads every silent passage as a dropout and
        // injects up to MaxGapSeconds of silence in one burst. At 5 s -- exactly MaxGapSeconds --
        // a single such burst filled the ring and DiscardOnBufferOverflow threw away everything
        // else in it, which is heard as continuous stutter. Field logs show the signature plainly:
        // discarded tracks padded almost exactly (981.1s vs 985.9s, 306.3 vs 311.2, 1446.0 vs
        // 1450.7), i.e. what overflowed WAS the padding, and it took the real audio with it.
        private const int BufferSeconds = 4 * ProcessLoopbackCapture.MaxGapSeconds;

        // How long to wait for the first stamped packet before anchoring to the wall clock
        // instead. Only reached when the source is silent from the moment capture starts.
        private const int AnchorTimeoutMs = 750;

        // Sparse process-loopback sidecars produce no packets while idle. Small holes inside an
        // active passage are padded; a larger one starts a new timestamped chunk.
        private const double MaxSparseGapPaddingSeconds = 1.0;

        private readonly string _bufferDirectory;
        private readonly ILogger _logger;
        private readonly RecordingAudioSource _source;
        private readonly bool _includeMicrophone;
        private readonly Func<int?> _gameProcessId;
        private readonly object _gate = new object();

        private IWaveIn _systemCapture;
        private IWaveIn _gameReferenceCapture;
        private IWaveIn _nonGameCapture;
        private IWaveIn _micCapture;
        private BufferedWaveProvider _systemBuffer;
        private BufferedWaveProvider _micBuffer;
        private ISampleProvider _mix;
        private WaveFormat _outputFormat;

        private WaveFileWriter _writer;
        private StampedAuxiliaryTrack _stampedChimeTrack;
        private StampedAuxiliaryTrack _stampedGameReferenceTrack;
        private StampedAuxiliaryTrack _stampedNonGameTrack;
        private long _chunkSamplesWritten;
        private long _chunkStartWallClockSamples;
        private DateTime _pumpStartUtc;
        private Thread _pumpThread;
        private volatile bool _running;
        private bool _failed;
        private bool _stopped;
        // Audio the ring buffer never accepted, in bytes of the capture format; see Append.
        private long _discardedBytes;
        // A clip export may ask for the chunk covering its window end to close now instead of at
        // its natural boundary; see FlushChunksThroughAsync. 0 = no request. Read/written with
        // Interlocked: the process is 32-bit, where a bare long read can tear.
        private long _flushThroughUtcTicks;
        // The same, for the stamped auxiliary (sidecar) chunks; see
        // FlushAuxiliaryChunksThroughAsync.
        private long _flushAuxThroughUtcTicks;

        // How far the wall clock must be past an auxiliary flush request before the covering
        // sidecar chunks close. Sidecar writes happen on packet arrival, so this absorbs capture
        // delivery latency; it replaces a fixed segment-length-plus-margin sleep at the reader.
        private const int AuxiliaryFlushMarginMs = 750;
        private bool _writeGameReference;
        private bool _removeNonGameFromSpeakerMix;
        private bool _extractControllerProgramAudio;
        private bool _hapticExclusionProven;
        private string _micName;

        /// <summary>
        /// Game-only mode records the speaker endpoint and removes this recorder's non-game
        /// sidecar at export. The main track is therefore haptic-free even if that cleanup fails.
        /// </summary>
        public bool RequiresNonGameCleanup => _removeNonGameFromSpeakerMix;

        public bool NonGameReferenceFailed => _stampedNonGameTrack?.Failed == true;

        /// <summary>Whether the gam_ cancellation reference failed and its chunks were deleted.</summary>
        public bool GameReferenceFailed => _stampedGameReferenceTrack?.Failed == true;

        /// <summary>On the chime sidecar instance: whether its chm_ track failed and was deleted.</summary>
        public bool ChimeReferenceFailed => _stampedChimeTrack?.Failed == true;

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

        // When true this instance is the chime sidecar: it records Playnite's process tree (where
        // UniPlaySong plays the unlock chimes) into chm_*.wav chunks. That tree can also contain a
        // game launched by Playnite; in that case the main recorder captures its game signal
        // to a reference WAV so the game can be cancelled before the chime is re-timed.
        private readonly bool _capturePlayniteChimes;

        /// <summary>
        /// Whether the chime sidecar track can exist on this machine (per-process loopback,
        /// Windows 10 19041+).
        /// </summary>
        public static bool IsChimeCaptureSupported => ProcessLoopbackCapture.IsSupported;

        /// <summary>
        /// Whether the Playnite-tree sidecar has a simultaneous game reference that must be
        /// cancelled before the isolated chime is re-timed.
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
                    var systemFormat = _extractControllerProgramAudio
                        ? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)
                        : _systemCapture.WaveFormat;
                    _systemBuffer = NewBuffer(systemFormat);
                    _systemCapture.DataAvailable += (s, e) => AppendSystem(e);

                    ISampleProvider systemSamples = _systemBuffer.ToSampleProvider();

                    if (_includeMicrophone)
                    {
                        try
                        {
                            // Not simply the default input: connecting a DualSense makes Windows
                            // switch the default to the pad's own microphone, which records the
                            // haptics acoustically — audible in the clip, and beyond the reach of
                            // any render-side cancellation. See MicrophoneSelector.
                            var micDevice = MicrophoneSelector.TryChoose(_logger);
                            if (micDevice == null)
                            {
                                _micName = "omitted-no-safe-input";
                                _mix = systemSamples;
                            }
                            else
                            {
                                _micName = micDevice.Describe();
                                _micCapture = ProcessLoopbackCapture.ForCaptureEndpoint(micDevice.Id);
                                _micBuffer = NewBuffer(_micCapture.WaveFormat);
                                _micCapture.DataAvailable += (s, e) => Append(_micBuffer, e);

                                var micSamples = MatchFormat(
                                    _micBuffer.ToSampleProvider(), systemSamples.WaveFormat);
                                _mix = new MixingSampleProvider(new[] { systemSamples, micSamples })
                                {
                                    ReadFully = true,
                                };
                            }
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
                    AttachTimestampedCancellationTracks();

                    _systemCapture.StartRecording();
                    StartOptionalCaptures();

                    // The timeline is anchored, and the first chunk opened, by the pump once it
                    // knows when the first packet's audio actually played -- see AwaitAnchor.
                    _running = true;
                    _pumpThread = new Thread(PumpLoop)
                    {
                        IsBackground = true,
                        Name = "PA-AudioPump",
                        // A late wake costs nothing but a larger read -- until the read is larger
                        // than the ring, at which point BufferedWaveProvider silently discards the
                        // excess (DiscardOnBufferOverflow) and the track loses that audio for good.
                        // The deadline is BufferSeconds, far slacker than the capture threads' 200
                        // ms, but BelowNormal under a CPU-saturating emulator was missing even
                        // that. Normal keeps it out of the way of the capture threads above it
                        // while still being scheduled against the game.
                        Priority = ThreadPriority.Normal,
                    };
                    _pumpThread.Start();

                    _logger?.Info(
                        $"[Recording] Audio capture started (source={CaptureSourceName()}, " +
                        $"mic={(_micCapture == null ? "False" : "'" + _micName + "'")}, " +
                        $"{_outputFormat}, haptics={(_hapticExclusionProven ? "excluded-by-endpoint" : "unproven-audio-retained")}).");
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
        /// Builds the required endpoint source and, for Game Only, its optional non-game sidecar.
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
            if (_source == RecordingAudioSource.GameOnly &&
                gamePid.HasValue && gamePid.Value > 0 &&
                ProcessLoopbackCapture.IsSupported)
            {
                try
                {
                    // The main source is deliberately the real speaker endpoint: controller
                    // endpoints can never enter it. Everything except the game tree is captured
                    // separately and removed offline; a failed separation retains this speaker mix,
                    // so failure means extra system audio rather than haptic buzz or silence.
                    _nonGameCapture = new ProcessLoopbackCapture(
                        gamePid.Value, includeProcessTree: false);
                    _removeNonGameFromSpeakerMix = true;

                    // Keep the existing re-timed chime path. The Playnite-tree sidecar can also
                    // contain a Playnite-launched game, so capture the game tree separately and
                    // require verified cancellation before that sidecar is composited.
                    try
                    {
                        _gameReferenceCapture = new ProcessLoopbackCapture(
                            gamePid.Value, includeProcessTree: true);
                        _writeGameReference = true;
                        ChimeCaptureMode = PlayniteChimeCaptureMode.CancelGameReference;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(
                            ex,
                            "[Recording] The game reference for chime re-timing could not start; " +
                            "Game Only isolation remains active with the live chime removed.");
                        DisposeCapture(ref _gameReferenceCapture);
                        _writeGameReference = false;
                        ChimeCaptureMode = PlayniteChimeCaptureMode.Unavailable;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn(
                        ex,
                        "[Recording] Game-only isolation reference could not start; " +
                        "recording haptic-free full-system speaker audio.");
                    DisposeCapture(ref _nonGameCapture);
                    _removeNonGameFromSpeakerMix = false;
                }
            }
            else if (_source == RecordingAudioSource.GameOnly)
            {
                _logger?.Info(
                    "[Recording] Game-only isolation unavailable (no pid or OS < 19041); " +
                    "recording haptic-free full-system speaker audio.");
            }
            else if (gamePid.HasValue && gamePid.Value > 0 && ProcessLoopbackCapture.IsSupported)
            {
                // Full System also re-times the chime onto the composited toast. The speaker mix
                // carries the live chime, so export first removes the Playnite-tree slice from it;
                // the game tree is captured alongside because a Playnite-launched game lives inside
                // both trees and must be cancelled out of that slice before it is subtracted.
                try
                {
                    _gameReferenceCapture = new ProcessLoopbackCapture(
                        gamePid.Value, includeProcessTree: true);
                    _writeGameReference = true;
                    ChimeCaptureMode = PlayniteChimeCaptureMode.CancelGameReference;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(
                        ex,
                        "[Recording] The game reference for chime re-timing could not start; " +
                        "the live chime stays in the speaker mix.");
                    DisposeCapture(ref _gameReferenceCapture);
                    _writeGameReference = false;
                }
            }
            else
            {
                _logger?.Info(
                    "[Recording] Chime re-timing unavailable (no pid or OS < 19041); " +
                    "the live chime stays in the speaker mix.");
            }

            // Both user-facing modes record the actual default render endpoint. A DualSense
            // actuator stream lives on its own endpoint and therefore never reaches aud_*.wav.
            if (!_writeGameReference)
            {
                // Without a game reference the Playnite-tree sidecar cannot be verified game-free,
                // so the live chime stays in the speaker mix where it played.
                ChimeCaptureMode = PlayniteChimeCaptureMode.Unavailable;
            }
            try
            {
                var speaker = AudioEndpointEnumerator.TryGetDefaultEndpoint(
                    AudioDataFlow.Render, AudioEndpointRole.Console);
                if (speaker == null || string.IsNullOrEmpty(speaker.Id))
                {
                    throw new InvalidOperationException("There is no default render endpoint.");
                }

                if (RenderEndpointScan.IsHapticEndpoint(speaker))
                {
                    ProcessLoopbackCapture native = null;
                    try
                    {
                        native = ProcessLoopbackCapture.ForEndpointNative(speaker.Id);
                        if (ProcessLoopbackCapture.IsDualSenseActuatorFormat(native.WaveFormat))
                        {
                            _extractControllerProgramAudio = true;
                            _hapticExclusionProven = true;
                            _logger?.Info(
                                "[Recording] The default output is a controller; recording its " +
                                "native front L/R channels and excluding actuator channels 2/3.");
                            return native;
                        }
                    }
                    finally
                    {
                        if (!_extractControllerProgramAudio)
                        {
                            try { native?.Dispose(); } catch { }
                        }
                    }

                    _logger?.Warn(
                        "[Recording] The default controller output did not expose the proven " +
                        "4-channel layout; retaining audible endpoint audio, which may include haptics.");
                }

                var endpoint = ProcessLoopbackCapture.ForEndpoint(speaker.Id);
                _hapticExclusionProven = true;
                return endpoint;
            }
            catch (Exception ex)
            {
                _logger?.Warn(
                    ex,
                    "[Recording] Timestamped speaker capture unavailable; using ordinary " +
                    "speaker loopback. Audio is retained, but haptic exclusion cannot be proven on " +
                    "this fallback.");
            }

            // The multimedia default, which on most machines is the same endpoint reached a second
            // way. Deliberately not NAudio's WasapiLoopbackCapture: it builds the same device
            // enumerator the primary path just failed on, so it could only ever rethrow — which is
            // what turned one endpoint failure into silent clips.
            var fallbackId = AudioEndpointEnumerator.TryGetDefaultEndpointId(
                AudioDataFlow.Render, AudioEndpointRole.Multimedia);
            if (string.IsNullOrEmpty(fallbackId))
            {
                throw new InvalidOperationException(
                    "No render endpoint could be resolved for speaker capture.");
            }

            return ProcessLoopbackCapture.ForEndpoint(fallbackId);
        }

        /// <summary>
        /// Writes the two tracks that participate in chime cancellation directly from their packet
        /// stamps. Sending them through independent wall-clock pumps re-timed each stream in 50 ms
        /// batches and produced 1-4 ms alignment steps inside one chime slice. Main clip audio stays
        /// pump-paced because it may contain a microphone and multiple sources; the chime sidecar
        /// and raw game reference are single process-loopback streams and need no such mixing.
        /// </summary>
        private void AttachTimestampedCancellationTracks()
        {
            if (_capturePlayniteChimes && _systemCapture is ProcessLoopbackCapture chimeCapture)
            {
                _stampedChimeTrack = new StampedAuxiliaryTrack(
                    RecordingPaths.ChimeChunkFilePrefix, chimeCapture.WaveFormat);
                chimeCapture.StampedDataAvailable +=
                    (s, e) => WriteStampedAuxiliaryPacket(_stampedChimeTrack, e);
            }

            if (_nonGameCapture is ProcessLoopbackCapture nonGameCapture)
            {
                _stampedNonGameTrack = new StampedAuxiliaryTrack(
                    RecordingPaths.NonGameReferenceChunkFilePrefix,
                    nonGameCapture.WaveFormat);
                nonGameCapture.StampedDataAvailable +=
                    (s, e) => WriteStampedAuxiliaryPacket(_stampedNonGameTrack, e);
                nonGameCapture.RecordingStopped += (s, e) =>
                {
                    lock (_gate)
                    {
                        if (!_stopped && e.Exception != null)
                        {
                            FailAuxiliaryTrackLocked(_stampedNonGameTrack);
                        }
                    }
                };
            }

            if (!_writeGameReference)
            {
                return;
            }

            var gameCapture = (_gameReferenceCapture ?? _systemCapture) as ProcessLoopbackCapture;
            if (gameCapture == null)
            {
                throw new InvalidOperationException(
                    "The game cancellation reference has no timestamped process-loopback source.");
            }

            _stampedGameReferenceTrack = new StampedAuxiliaryTrack(
                RecordingPaths.GameReferenceChunkFilePrefix, gameCapture.WaveFormat);
            gameCapture.StampedDataAvailable +=
                (s, e) => WriteStampedAuxiliaryPacket(_stampedGameReferenceTrack, e);
        }

        /// <summary>
        /// Starts sidecars independently of the required speaker capture. A broken GameOnly,
        /// chime, or microphone helper may reduce isolation, but it must never discard audible
        /// speaker audio that is already running.
        /// </summary>
        private void StartOptionalCaptures()
        {
            try
            {
                _gameReferenceCapture?.StartRecording();
            }
            catch (Exception ex)
            {
                _logger?.Warn(
                    ex,
                    "[Recording] The game reference for chime re-timing failed to start; " +
                    "speaker audio remains active.");
                DisposeCapture(ref _gameReferenceCapture);
                _writeGameReference = false;
                ChimeCaptureMode = PlayniteChimeCaptureMode.Unavailable;
                FailAuxiliaryTrackLocked(_stampedGameReferenceTrack);
            }

            try
            {
                _nonGameCapture?.StartRecording();
            }
            catch (Exception ex)
            {
                _logger?.Warn(
                    ex,
                    "[Recording] Game-only isolation failed to start; retaining haptic-free " +
                    "full-system speaker audio.");
                DisposeCapture(ref _nonGameCapture);
                _removeNonGameFromSpeakerMix = false;
                FailAuxiliaryTrackLocked(_stampedNonGameTrack);
            }

            try
            {
                _micCapture?.StartRecording();
            }
            catch (Exception ex)
            {
                _logger?.Warn(
                    ex,
                    "[Recording] Microphone capture failed to start; speaker audio remains active.");
                DisposeCapture(ref _micCapture);
                _micName = "omitted-start-failed";
            }
        }

        /// <summary>
        /// Places one continuous chime/game-reference packet at its own QPC-derived UTC position.
        /// Long idle spans start a sparse chunk; short holes are explicit silence; overlaps are
        /// trimmed. Nothing is re-paced by the main recorder's 50 ms pump.
        /// </summary>
        private void WriteStampedAuxiliaryPacket(
            StampedAuxiliaryTrack track,
            StampedPacketEventArgs packet)
        {
            if (track == null || packet == null || packet.Bytes <= 0)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (_stopped || _failed || track.Failed)
                    {
                        return;
                    }

                    if (!packet.CaptureUtc.HasValue)
                    {
                        track.UnstampedPackets++;
                        FailAuxiliaryTrackLocked(track);
                        return;
                    }

                    if (!track.OriginUtc.HasValue)
                    {
                        track.OriginUtc = packet.CaptureUtc.Value;
                    }

                    var rate = track.Format.SampleRate;
                    var target = RecordingPaths.AudioFrameAt(
                        track.OriginUtc.Value, packet.CaptureUtc.Value, rate);
                    var frames = packet.Bytes / track.BlockAlign;
                    var offset = 0;

                    if (track.Writer == null)
                    {
                        OpenAuxiliaryChunkLocked(track, target);
                    }

                    var drift = target - track.TimelineFrames;
                    if (drift > MaxSparseGapPaddingSeconds * rate)
                    {
                        OpenAuxiliaryChunkLocked(track, target);
                    }
                    else if (drift > 0)
                    {
                        WriteAuxiliarySilenceLocked(track, drift);
                    }
                    else if (drift < 0)
                    {
                        var trimFrames = (int)Math.Min(frames, -drift);
                        offset = trimFrames * track.BlockAlign;
                        frames -= trimFrames;
                    }

                    if (frames <= 0)
                    {
                        return;
                    }

                    WriteAuxiliaryFramesLocked(track, packet.Buffer, offset, frames);
                    track.StampedPackets++;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    FailAuxiliaryTrackLocked(track);
                }
                _logger?.Debug(
                    ex,
                    $"[Recording] Timestamped {track?.Prefix?.TrimEnd('_')} packet could not be written.");
            }
        }

        private void OpenAuxiliaryChunkLocked(StampedAuxiliaryTrack track, long startFrame)
        {
            CloseAuxiliaryChunkLocked(track);
            startFrame = Math.Max(0, startFrame);
            var startUtc = RecordingPaths.AudioFrameUtc(
                track.OriginUtc.Value, startFrame, track.Format.SampleRate);
            var name = RecordingPaths.BuildAudioChunkFileName(track.Prefix, startUtc);
            var path = Path.Combine(_bufferDirectory, name);
            track.Writer = new WaveFileWriter(path, track.Format);
            track.Paths.Add(path);
            track.ChunkStartFrame = startFrame;
            track.ChunkFramesWritten = 0;
            track.TimelineFrames = startFrame;
        }

        private void WriteAuxiliarySilenceLocked(StampedAuxiliaryTrack track, long frames)
        {
            var remaining = frames;
            var silence = new byte[Math.Min(remaining * track.BlockAlign, 64 * 1024)];
            while (remaining > 0 && silence.Length > 0)
            {
                var count = (int)Math.Min(silence.Length / track.BlockAlign, remaining);
                WriteAuxiliaryFramesLocked(track, silence, 0, count);
                remaining -= count;
            }
        }

        private void WriteAuxiliaryFramesLocked(
            StampedAuxiliaryTrack track,
            byte[] buffer,
            int offset,
            long frames)
        {
            var sourceOffset = offset;
            var remaining = frames;
            var chunkFrames = AuxiliaryChunkFrames(track.Format.SampleRate);
            while (remaining > 0)
            {
                var capacity = chunkFrames - track.ChunkFramesWritten;
                if (capacity <= 0)
                {
                    OpenAuxiliaryChunkLocked(track, track.TimelineFrames);
                    capacity = chunkFrames;
                }

                var writeFrames = Math.Min(remaining, capacity);
                var writeBytes = checked((int)writeFrames * track.BlockAlign);
                track.Writer.Write(buffer, sourceOffset, writeBytes);
                sourceOffset += writeBytes;
                remaining -= writeFrames;
                track.TimelineFrames += writeFrames;
                track.ChunkFramesWritten += writeFrames;

            }
        }

        private void CloseExpiredAuxiliaryChunksLocked(DateTime nowUtc)
        {
            foreach (var track in new[]
            {
                _stampedChimeTrack,
                _stampedGameReferenceTrack,
                _stampedNonGameTrack,
            })
            {
                if (track?.Writer == null || !track.OriginUtc.HasValue)
                {
                    continue;
                }

                var nowFrame = RecordingPaths.AudioFrameAt(
                    track.OriginUtc.Value, nowUtc, track.Format.SampleRate);
                if (nowFrame - track.ChunkStartFrame >= AuxiliaryChunkFrames(track.Format.SampleRate))
                {
                    CloseAuxiliaryChunkLocked(track);
                }
            }
        }

        private void CloseAuxiliaryTracksLocked()
        {
            CloseAuxiliaryChunkLocked(_stampedChimeTrack);
            CloseAuxiliaryChunkLocked(_stampedGameReferenceTrack);
            CloseAuxiliaryChunkLocked(_stampedNonGameTrack);
        }

        private static void CloseAuxiliaryChunkLocked(StampedAuxiliaryTrack track)
        {
            try { track?.Writer?.Dispose(); } catch { }
            if (track != null)
            {
                track.Writer = null;
                track.ChunkFramesWritten = 0;
            }
        }

        private static void FailAuxiliaryTrackLocked(StampedAuxiliaryTrack track)
        {
            if (track == null || track.Failed)
            {
                return;
            }

            track.Failed = true;
            CloseAuxiliaryChunkLocked(track);
            foreach (var path in track.Paths)
            {
                try { File.Delete(path); } catch { }
            }
            track.Paths.Clear();
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

        private static long AuxiliaryChunkFrames(int sampleRate)
        {
            return (long)UnlockRecordingService.SegmentSeconds * sampleRate;
        }

        private void AppendSystem(WaveInEventArgs packet)
        {
            if (!_extractControllerProgramAudio)
            {
                Append(_systemBuffer, packet);
                return;
            }

            var programAudio = ProcessLoopbackCapture.ExtractDualSenseProgramAudio(
                packet?.Buffer,
                packet?.BytesRecorded ?? 0,
                _systemCapture?.WaveFormat);
            if (programAudio == null)
            {
                return;
            }

            Append(
                _systemBuffer,
                new WaveInEventArgs(programAudio, programAudio.Length));
        }

        /// <summary>
        /// Reports whatever this track lost or stood in for. Silent when nothing did, so a line here
        /// always means the recorded audio does not represent an unbroken stretch of real time.
        /// </summary>
        /// <summary>
        /// Reports what the CLIP TRACK lost: audio the ring buffer refused, and engine dropouts the
        /// main capture padded with silence.
        /// <para>
        /// Only <paramref name="clipTrack"/> counts toward that. The sidecars were summed in here
        /// once, and they are sparse by design — a process-loopback client whose target is silent
        /// delivers no packets, so every idle span reads as a "dropout". That pushed the figures
        /// past the session length (a field log claimed 1446 s of loss in a 483 s session) and sent
        /// a starvation hunt looking at cancellation instead. Sidecar padding is reported
        /// separately, and without alarm.
        /// </para>
        /// </summary>
        private void LogTimelineNotices(IWaveIn clipTrack, IWaveIn micTrack, params IWaveIn[] sidecars)
        {
            var discarded = Interlocked.Read(ref _discardedBytes);
            var paddedFrames = (clipTrack as ProcessLoopbackCapture)?.PaddedGapFrames ?? 0;
            var micPaddedFrames = (micTrack as ProcessLoopbackCapture)?.PaddedGapFrames ?? 0;
            var bytesPerSecond = Math.Max(1, _outputFormat?.AverageBytesPerSecond ?? 1);
            var sampleRate = Math.Max(1, _outputFormat?.SampleRate ?? 1);

            if (discarded > 0 || paddedFrames > 0 || micPaddedFrames > 0)
            {
                // The mic mixes into the same clip, so its pads are audible there too.
                var mic = micPaddedFrames > 0
                    ? $", {micPaddedFrames / (double)sampleRate:0.###}s of microphone dropouts padded"
                    : string.Empty;
                _logger?.Warn(
                    $"[Recording] Audio track has gaps: {discarded / (double)bytesPerSecond:0.###}s dropped to " +
                    $"buffer overflow, {paddedFrames / (double)sampleRate:0.###}s of engine dropouts padded " +
                    $"with silence{mic}.");
            }

            // Silence a gap witness asked for beyond elapsed real time, which is impossible.
            // Reported on its own because it says something quite different from the line above:
            // not that audio was lost, but that a witness lied and was refused.
            var impossible = (clipTrack as ProcessLoopbackCapture)?.ImpossibleGapFrames ?? 0;
            if (impossible > 0)
            {
                _logger?.Warn(
                    $"[Recording] A gap witness asked for {impossible / (double)sampleRate:0.###}s more " +
                    "silence than the session was long; it was refused. Endpoint mix format: " +
                    ((clipTrack as ProcessLoopbackCapture)?.NativeMixFormat?.ToString() ?? "unknown") +
                    $"; capture format: {_outputFormat}.");
            }

            LogDevicePositionRate(clipTrack as ProcessLoopbackCapture, "clip");
            LogDevicePositionRate(micTrack as ProcessLoopbackCapture, "microphone");

            var sidecarFrames = 0L;
            foreach (var capture in sidecars ?? new IWaveIn[0])
            {
                sidecarFrames += (capture as ProcessLoopbackCapture)?.PaddedGapFrames ?? 0;
            }

            if (sidecarFrames > 0)
            {
                // Expected: these follow one process tree and pad whenever it is quiet.
                _logger?.Debug(
                    $"[Recording] Sidecar silence padding: {sidecarFrames / (double)sampleRate:0.###}s " +
                    "across the reference tracks (idle spans, not dropouts).");
            }
        }

        /// <summary>
        /// Names the unit the device position counter actually ticks in when it is not this
        /// capture's frames. Informational: gap sizing uses packet stamps, and a deviating rate
        /// with near-zero padding is a healthy capture on a non-48 kHz endpoint — but when a
        /// clip does have gaps, this is the number that explains the machine.
        /// </summary>
        private void LogDevicePositionRate(ProcessLoopbackCapture capture, string trackName)
        {
            var rate = capture?.MeasuredDevicePositionRate ?? 0;
            var captureRate = capture?.WaveFormat?.SampleRate ?? 0;
            if (rate <= 0 || captureRate <= 0 ||
                Math.Abs(rate - captureRate) <= captureRate * 0.01)
            {
                return;
            }

            _logger?.Info(
                $"[Recording] The {trackName} track's device position counter advances at " +
                $"~{rate:0}/s against its {captureRate}/s capture format (native mix: " +
                $"{capture.NativeMixFormat?.ToString() ?? "unknown"}). Gap sizing uses packet " +
                "timestamps, so this alone costs nothing.");
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
                        if (_writer == null && _stampedChimeTrack == null)
                        {
                            break;
                        }

                        // Frames (per channel) that should have been written by now, wall-clock paced.
                        var elapsed = (CaptureTimelineClock.UtcNow - _pumpStartUtc).TotalSeconds;
                        var targetFrames = (long)(elapsed * sampleRate);
                        CloseExpiredAuxiliaryChunksLocked(CaptureTimelineClock.UtcNow);
                        var writtenFrames = TotalFramesWritten();
                        var frames = (int)Math.Min(buffer.Length / channels, Math.Max(0, targetFrames - writtenFrames));
                        if (frames > 0)
                        {
                            var read = _mix.Read(buffer, 0, frames * channels);
                            if (read > 0)
                            {
                                _writer?.WriteSamples(buffer, 0, read);
                                _chunkSamplesWritten += read;
                            }

                            if (_chunkSamplesWritten / channels >= (long)UnlockRecordingService.SegmentSeconds * sampleRate)
                            {
                                CloseChunkLocked();
                                OpenChunkLocked();
                            }
                        }

                        SettleFlushRequestLocked();
                        SettleAuxiliaryFlushRequestLocked(CaptureTimelineClock.UtcNow);
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
            return _chunkStartWallClockSamples +
                _chunkSamplesWritten / Math.Max(1, _outputFormat.Channels);
        }

        /// <summary>Stops capture and closes the current chunk cleanly. Idempotent.</summary>
        public void Stop()
        {
            IWaveIn system, gameReference, nonGame, mic;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _running = false;
                system = _systemCapture;
                gameReference = _gameReferenceCapture;
                nonGame = _nonGameCapture;
                mic = _micCapture;
                LogAuxiliaryTracksLocked();
            }

            // Outside the gate: capture Dispose joins its thread, which may be delivering data.
            StopCapture(system);
            StopCapture(gameReference);
            StopCapture(nonGame);
            StopCapture(mic);

            lock (_gate)
            {
                CloseChunkLocked();
                CloseAuxiliaryTracksLocked();
                LogTimelineNotices(system, mic, gameReference, nonGame);
                _systemCapture = null;
                _gameReferenceCapture = null;
                _nonGameCapture = null;
                _micCapture = null;
            }
        }

        private void LogAuxiliaryTracksLocked()
        {
            var tracks = new List<string>();
            foreach (var track in new[]
            {
                _stampedChimeTrack,
                _stampedGameReferenceTrack,
                _stampedNonGameTrack,
            })
            {
                if (track == null)
                {
                    continue;
                }

                tracks.Add(
                    $"{track.Prefix.TrimEnd('_')}: stamped={track.StampedPackets} " +
                    $"unstamped={track.UnstampedPackets} chunks={track.Paths.Count} " +
                    $"failed={track.Failed}");
            }

            if (tracks.Count > 0)
            {
                _logger?.Info(
                    "[Recording] Timestamped cancellation tracks: " +
                    string.Join("; ", tracks.ToArray()) + ".");
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
            var startUtc = RecordingPaths.AudioFrameUtc(
                _pumpStartUtc,
                _chunkStartWallClockSamples,
                _outputFormat.SampleRate);
            var name = RecordingPaths.BuildAudioChunkFileName(prefix, startUtc);

            if (_stampedChimeTrack == null)
            {
                _writer = new WaveFileWriter(Path.Combine(_bufferDirectory, name), _outputFormat);
            }

            _chunkSamplesWritten = 0;
        }

        private void CloseChunkLocked()
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }

        /// <summary>
        /// Asks the pump to close the chunk covering <paramref name="utc"/> as soon as the audio
        /// written reaches that instant, instead of waiting for the chunk to fill to the segment
        /// length. Completes once the covering chunk's WAV is closed; callers bound the wait. A
        /// request that outlives its caller is harmless — the pump just rotates once, early.
        /// </summary>
        public async Task FlushChunksThroughAsync(DateTime utc)
        {
            // Keep the furthest-out request: a rotation past the maximum satisfies every earlier
            // one, while letting a later request overwrite an earlier one would leave the earlier
            // caller waiting on a rotation that never comes.
            long requested = utc.Ticks, seen;
            while ((seen = Interlocked.Read(ref _flushThroughUtcTicks)) < requested &&
                Interlocked.CompareExchange(ref _flushThroughUtcTicks, requested, seen) != seen)
            {
            }

            while (_running && Interlocked.Read(ref _flushThroughUtcTicks) != 0)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Closes the current chunk early once a flush request's instant is covered by the audio
        /// written so far, so an export can read it without waiting out the chunk length. The pump
        /// paces writes to the wall clock and the mix pads unfilled reads, so coverage arrives
        /// within a pump tick of the requested instant. Runs under the gate on the pump thread.
        /// </summary>
        private void SettleFlushRequestLocked()
        {
            var requested = Interlocked.Read(ref _flushThroughUtcTicks);
            if (requested == 0)
            {
                return;
            }

            var requestFrames = (long)Math.Ceiling(
                (new DateTime(requested, DateTimeKind.Utc) - _pumpStartUtc).TotalSeconds *
                _outputFormat.SampleRate);
            if (_writer == null || _chunkStartWallClockSamples >= requestFrames)
            {
                // Nothing open, or the covering chunk already rotated out and closed.
                Interlocked.CompareExchange(ref _flushThroughUtcTicks, 0, requested);
                return;
            }

            if (TotalFramesWritten() >= requestFrames)
            {
                CloseChunkLocked();
                OpenChunkLocked();
                Interlocked.CompareExchange(ref _flushThroughUtcTicks, 0, requested);
            }
        }

        /// <summary>
        /// Asks the pump to close the stamped sidecar chunks covering <paramref name="utc"/> once
        /// the wall clock is safely past it, instead of waiting out their natural chunk length.
        /// Also flushes the main chunk when this recorder writes its sidecar through the main
        /// writer (the unstamped chime mode). Completes once the covering chunks are closed;
        /// callers bound the wait.
        /// </summary>
        public async Task FlushAuxiliaryChunksThroughAsync(DateTime utc)
        {
            long requested = utc.Ticks, seen;
            while ((seen = Interlocked.Read(ref _flushAuxThroughUtcTicks)) < requested &&
                Interlocked.CompareExchange(ref _flushAuxThroughUtcTicks, requested, seen) != seen)
            {
            }

            var mainFlush = _capturePlayniteChimes ? FlushChunksThroughAsync(utc) : null;
            while (_running && Interlocked.Read(ref _flushAuxThroughUtcTicks) != 0)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            if (mainFlush != null)
            {
                await mainFlush.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Closes the sidecar chunks covering an auxiliary flush request once the wall clock is
        /// past the request by <see cref="AuxiliaryFlushMarginMs"/> (sidecar writes trail the
        /// audio by capture delivery latency). Sparse tracks reopen on their next packet, exactly
        /// as after a natural expiry. Runs under the gate on the pump thread.
        /// </summary>
        private void SettleAuxiliaryFlushRequestLocked(DateTime nowUtc)
        {
            var requested = Interlocked.Read(ref _flushAuxThroughUtcTicks);
            if (requested == 0)
            {
                return;
            }

            if (nowUtc.Ticks - requested < TimeSpan.FromMilliseconds(AuxiliaryFlushMarginMs).Ticks)
            {
                return;
            }

            var requestUtc = new DateTime(requested, DateTimeKind.Utc);
            foreach (var track in new[]
            {
                _stampedChimeTrack,
                _stampedGameReferenceTrack,
                _stampedNonGameTrack,
            })
            {
                if (track?.Writer == null || !track.OriginUtc.HasValue)
                {
                    continue;
                }

                var requestFrame = RecordingPaths.AudioFrameAt(
                    track.OriginUtc.Value, requestUtc, track.Format.SampleRate);
                if (track.ChunkStartFrame <= requestFrame)
                {
                    CloseAuxiliaryChunkLocked(track);
                }
            }

            Interlocked.CompareExchange(ref _flushAuxThroughUtcTicks, 0, requested);
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
            CloseAuxiliaryTracksLocked();
        }

        private void CleanupLocked()
        {
            _running = false;
            CloseChunkLocked();
            CloseAuxiliaryTracksLocked();
            DisposeCapture(ref _systemCapture);
            DisposeCapture(ref _gameReferenceCapture);
            DisposeCapture(ref _nonGameCapture);
            DisposeCapture(ref _micCapture);
            _systemBuffer = null;
            _micBuffer = null;
            _mix = null;
        }

        /// <summary>One directly timestamped chime, game, or non-game reference track.</summary>
        private sealed class StampedAuxiliaryTrack
        {
            public StampedAuxiliaryTrack(string prefix, WaveFormat format)
            {
                Prefix = prefix;
                Format = format;
                BlockAlign = Math.Max(1, format.BlockAlign);
            }

            public string Prefix;
            public WaveFormat Format;
            public int BlockAlign;
            public WaveFileWriter Writer;
            public DateTime? OriginUtc;
            public long TimelineFrames;
            public long ChunkStartFrame;
            public long ChunkFramesWritten;
            public long StampedPackets;
            public long UnstampedPackets;
            public bool Failed;
            public List<string> Paths = new List<string>();
        }
    }
}
