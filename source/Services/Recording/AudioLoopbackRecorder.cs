using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// convention (aud_yyyyMMdd-HHmmssfffffffZ.wav, UTC timeline) and rotate every
    /// <see cref="UnlockRecordingService.SegmentSeconds"/> seconds.
    ///
    /// A single pump thread reads the (optionally mixed) audio at a wall-clock pace and writes it,
    /// so silence — WASAPI loopback delivers no buffers during digital silence — still advances the
    /// chunk in real time (the buffers zero-fill). Any failure logs one warning and leaves the video
    /// pipeline untouched; NAudio types are confined to this file, ProcessLoopbackCapture and
    /// RenderEndpointScan.
    ///
    /// While a controller audio endpoint exists, everything rendered to it is captured in parallel
    /// into one hapN_ track per endpoint (<see cref="StartHapticReference"/>). Process loopback mixes
    /// every endpoint a process renders to, so a game's haptic waveform is inside the main track; the
    /// clip export cancels it out against those references.
    ///
    /// Those tracks are NOT pump-paced: each packet is written at the position its own capture stamp
    /// gives it on the pump's timeline (<see cref="WriteStampedHapticPacket"/>). An endpoint client
    /// runs on its own clock, so pacing its audio with ours reintroduced exactly what the export then
    /// had to search for — a fixed offset plus accumulating drift.
    ///
    /// The chime sidecar and its game-only cancellation reference are likewise written directly from
    /// packet stamps. They are independent process-loopback clients; sending either through a 50 ms
    /// pump caused millisecond alignment steps whenever the chime's render stream changed the graph.
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

        // A render endpoint produces no loopback packets while it is idle. Small holes inside one
        // active passage are padded, but a larger one starts a new timestamped sparse chunk instead
        // of writing minutes of silence (or, worse, collapsing the gap and moving the next rumble).
        private const double MaxHapticGapPaddingSeconds = 1.0;

        // Packet placement should stay close to the main pump even after a long endpoint silence.
        // Anything further away indicates an unusable driver timestamp or a stalled capture graph.
        private const double MaxHapticStampSkewSeconds = 2.0;

        // Distinct doubtful spans kept before collapsing them into one. Bounded so a pathological
        // session cannot grow this without limit.
        private const int MaxHapticHoles = 128;

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
        private readonly HashSet<string> _hapticFailedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Thread _hapticWatchThread;
        private BufferedWaveProvider _systemBuffer;
        private BufferedWaveProvider _restoredGameBuffer;
        private BufferedWaveProvider _micBuffer;
        private ISampleProvider _mix;
        private WaveFormat _outputFormat;

        private WaveFileWriter _writer;
        private StampedAuxiliaryTrack _stampedChimeTrack;
        private StampedAuxiliaryTrack _stampedGameReferenceTrack;
        private long _chunkSamplesWritten;
        private long _chunkStartWallClockSamples;
        private DateTime _pumpStartUtc;
        private Thread _pumpThread;
        private volatile bool _running;
        private bool _failed;
        private bool _stopped;
        private readonly List<HapticHole> _hapticHoles = new List<HapticHole>();

        // Audio the ring buffer never accepted, in bytes of the capture format; see Append.
        private long _discardedBytes;
        private bool _restoreGameIntoFullSystem;
        private bool _writeGameReference;
        private string _micName;

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
        /// Whether a controller reference may have a coverage or placement hole overlapping
        /// [<paramref name="windowStartUtc"/>, <paramref name="windowEndUtc"/>].
        /// <para>
        /// Scoped to when the hole happened rather than latched for the session: endpoint churn is
        /// normal on the hardware this feature exists for — a DualSense endpoint re-enumerates, and
        /// Windows moves the default output onto it — so a session-wide flag ended up set during
        /// ordinary play and every later clip inherited a doubt that had nothing to do with it.
        /// </para>
        /// </summary>
        public bool HasHapticHole(DateTime windowStartUtc, DateTime windowEndUtc)
        {
            lock (_gate)
            {
                foreach (var hole in _hapticHoles)
                {
                    if (hole.StartUtc < windowEndUtc && windowStartUtc < hole.EndUtc)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Records that the references cannot be trusted around now. The span reaches back one
        /// rescan interval because that is how stale the knowledge behind most of these calls is:
        /// the watcher only learns an endpoint appeared, vanished or failed at its next scan.
        /// </summary>
        private void MarkHapticCompromisedLocked()
        {
            var now = CaptureTimelineClock.UtcNow;
            var start = now.AddMilliseconds(-HapticRescanIntervalMs);
            if (_hapticHoles.Count > 0)
            {
                var last = _hapticHoles[_hapticHoles.Count - 1];
                if (last.EndUtc >= start)
                {
                    // Contiguous with the previous hole: widen it rather than accumulate entries.
                    last.EndUtc = now;
                    _hapticHoles[_hapticHoles.Count - 1] = last;
                    return;
                }
            }

            if (_hapticHoles.Count >= MaxHapticHoles)
            {
                // Churning this hard, the safe reading is that the whole session is doubtful.
                var first = _hapticHoles[0];
                _hapticHoles.Clear();
                _hapticHoles.Add(new HapticHole { StartUtc = first.StartUtc, EndUtc = now });
                return;
            }

            _hapticHoles.Add(new HapticHole { StartUtc = start, EndUtc = now });
        }

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
                            var gameSamples = _restoredGameBuffer.ToSampleProvider();
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
                                _micName = micDevice.FriendlyName;
                                _micCapture = new WasapiCapture(micDevice);
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
                        $"[Recording] Audio capture started (source={CaptureSourceName()}, " +
                        $"mic={(_micCapture == null ? "False" : "'" + _micName + "'")}, " +
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
                        // direction, so always capture the timestamped game reference and require verified
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
            // and captured as the sidecar-cancellation reference. Unknown relationships deliberately
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
                        // that game before the main WAV is written and capture its timestamped raw
                        // packets to gam_*.wav for sidecar cancellation.
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

            if (!_writeGameReference)
            {
                return;
            }

            var gameCapture = (_restoredGameCapture ?? _systemCapture) as ProcessLoopbackCapture;
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

            var attached = AttachHapticEndpoints();
            StartHapticWatcher();
            return attached.Count == 0 ? string.Empty : ", haptics=" + string.Join("+", attached.ToArray());
        }

        /// <summary>
        /// Opens a capture for every controller endpoint not already attached, closes endpoints that
        /// vanished, and returns the names of the ones opened. This runs on the dedicated watcher
        /// thread, so stopping a dead driver never blocks the audio pump.
        /// </summary>
        private List<string> AttachHapticEndpoints()
        {
            var opened = new List<string>();
            var endpoints = RenderEndpointScan.FindHapticEndpoints(
                _logger, out var scanComplete, out var hasDefaultHapticEndpoint);
            if (!scanComplete || hasDefaultHapticEndpoint)
            {
                lock (_gate)
                {
                    MarkHapticCompromisedLocked();
                }
            }

            // A controller audio endpoint can disappear and later return with the SAME id. Keeping
            // its old loopback client in the dictionary made the re-scan believe it was still being
            // captured, even though that client's poll thread was permanently attached to the dead
            // device. Remove vanished clients here, on the watcher thread, so the same id is opened
            // afresh as soon as Windows publishes it again.
            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in endpoints)
            {
                activeIds.Add(endpoint.DeviceId);
            }

            var vanished = new List<HapticEndpointCapture>();
            lock (_gate)
            {
                foreach (var pair in _hapticCaptures)
                {
                    if (!activeIds.Contains(pair.Key))
                    {
                        vanished.Add(pair.Value);
                    }
                }

                foreach (var entry in vanished)
                {
                    MarkHapticCompromisedLocked();
                    RemoveHapticCaptureLocked(entry);
                }
            }

            foreach (var entry in vanished)
            {
                StopCapture(entry.Capture);
                _logger?.Info(
                    $"[Recording] Controller endpoint disappeared; closed haptic reference " +
                    $"hap{entry.Index} for '{entry.Name}' so the same endpoint id can reconnect.");
            }

            foreach (var endpoint in endpoints)
            {
                var index = 0;
                lock (_gate)
                {
                    if (_stopped || _hapticCaptures.ContainsKey(endpoint.DeviceId))
                    {
                        continue;
                    }

                    index = NextHapticIndexLocked();
                    if (index < 0)
                    {
                        MarkHapticCompromisedLocked();
                        // Said once, because the rescan would otherwise repeat it forever.
                        if (_hapticFailedDeviceIds.Add(endpoint.DeviceId))
                        {
                            _logger?.Info(
                                $"[Recording] Already capturing {RecordingPaths.MaxHapticReferences} controller endpoints; " +
                                $"'{endpoint.Name}' is left out; clips overlapping this interval " +
                                "will retain their original audio and may contain controller buzz.");
                        }

                        continue;
                    }
                }

                // Activation and StartRecording are COM work, kept off the gate so the pump thread
                // is never blocked behind a driver.
                ProcessLoopbackCapture capture = null;
                try
                {
                    capture = ProcessLoopbackCapture.ForEndpoint(endpoint.DeviceId);
                    var entry = new HapticEndpointCapture
                    {
                        DeviceId = endpoint.DeviceId,
                        Name = endpoint.Name,
                        Index = index,
                        Capture = capture,
                        BlockAlign = Math.Max(1, capture.WaveFormat.BlockAlign),
                    };

                    // No ring buffer and no pacing: each packet is written where its own capture
                    // instant puts it on the pump's timeline. See WriteStampedHapticPacket.
                    capture.StampedDataAvailable += (s, e) => WriteStampedHapticPacket(entry, e);
                    capture.RecordingStopped += (s, e) => HapticCaptureStopped(entry, e);

                    lock (_gate)
                    {
                        _hapticCaptures[endpoint.DeviceId] = entry;
                        // The first main chunk installs startup captures. A mid-session endpoint is
                        // usable immediately instead of waiting through another recording chunk.
                        entry.Installed = _writer != null;
                        if (entry.Installed)
                        {
                            // The watcher can discover an endpoint up to one scan interval after it
                            // starts carrying audio. Its later packets are safe, but the gap cannot
                            // be reconstructed retrospectively.
                            MarkHapticCompromisedLocked();
                        }
                    }

                    capture.StartRecording();

                    opened.Add(endpoint.Name);
                    _hapticFailedDeviceIds.Remove(endpoint.DeviceId);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        MarkHapticCompromisedLocked();
                        if (_hapticCaptures.TryGetValue(endpoint.DeviceId, out var failed) &&
                            ReferenceEquals(failed.Capture, capture))
                        {
                            RemoveHapticCaptureLocked(failed);
                        }
                    }

                    try { capture?.Dispose(); } catch { }
                    capture = null;

                    // Retried on every later tick — an endpoint can be busy for a moment — but
                    // reported once per device, or a stuck one would fill the log at rescan cadence.
                    if (_hapticFailedDeviceIds.Add(endpoint.DeviceId))
                    {
                        _logger?.Warn(
                            ex,
                            $"[Recording] Controller endpoint '{endpoint.Name}' could not be captured; " +
                            "clips overlapping this interval will retain their original audio.");
                    }
                }
            }

            return opened;
        }

        /// <summary>
        /// Makes an endpoint whose poll loop died eligible for the next five-second re-scan even if
        /// Windows still reports the same device id as active. Disconnect/reconnect is not the only
        /// failure mode: a driver can invalidate an existing audio client without withdrawing the
        /// endpoint from enumeration.
        /// </summary>
        private void HapticCaptureStopped(HapticEndpointCapture entry, StoppedEventArgs stopped)
        {
            lock (_gate)
            {
                if (_stopped || _failed ||
                    !_hapticCaptures.TryGetValue(entry.DeviceId, out var attached) ||
                    !ReferenceEquals(attached, entry))
                {
                    return;
                }

                MarkHapticCompromisedLocked();
                RemoveHapticCaptureLocked(entry);
            }

            _logger?.Warn(
                stopped?.Exception,
                $"[Recording] Controller endpoint capture stopped unexpectedly for '{entry.Name}'; " +
                "the watcher will reopen it.");

            // This callback runs on the capture's own poll thread. Dispose eventually joins that
            // thread, so hand it to the pool instead of making the thread wait for itself.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { entry.Capture?.Dispose(); } catch { }
            });
        }

        private void RemoveHapticCaptureLocked(HapticEndpointCapture entry)
        {
            entry.Installed = false;
            CloseHapticChunkLocked(entry);
            _hapticCaptures.Remove(entry.DeviceId);
        }

        /// <summary>First reference prefix not owned by a currently attached endpoint.</summary>
        private int NextHapticIndexLocked()
        {
            for (var candidate = 0; candidate < RecordingPaths.MaxHapticReferences; candidate++)
            {
                if (_hapticCaptures.Values.All(entry => entry.Index != candidate))
                {
                    return candidate;
                }
            }

            return -1;
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
                        lock (_gate)
                        {
                            MarkHapticCompromisedLocked();
                        }
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
        /// Writes one captured packet at the position its own capture instant gives it on the pump's
        /// timeline. Endpoint loopback is sparse — it emits no packets at all between haptic
        /// passages — so long gaps become separate timestamped WAV chunks. Short packet holes are
        /// represented by silence and overlaps are trimmed from the packet's front.
        /// <para>
        /// The alternative — buffering the packets and reading them at the pump's pace — re-times an
        /// independently clocked stream onto our clock, which is where the reference's misalignment
        /// came from: a fixed offset from the endpoint client's own latency (31-47 ms in the field)
        /// plus drift from the two clocks running apart (~490 ppm observed, tens of ms across one
        /// clip). Placing each packet by its stamp removes both at the source, and leaves the
        /// export's correlation search as a safety net rather than the thing doing the work.
        /// Critically, a ten-minute silent gap is a ten-minute timestamp jump, not an implausible
        /// correction: QpcToUtcForPlacement already rejected stamps on a foreign timebase.
        /// </para>
        /// </summary>
        private void WriteStampedHapticPacket(HapticEndpointCapture entry, StampedPacketEventArgs packet)
        {
            if (packet == null || packet.Bytes <= 0)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (_stopped || _failed || !entry.Installed || _outputFormat == null)
                    {
                        return;
                    }

                    var rate = _outputFormat.SampleRate;
                    var frames = packet.Bytes / entry.BlockAlign;
                    var offset = 0;

                    if (packet.CaptureUtc.HasValue)
                    {
                        var target = RecordingPaths.AudioFrameAt(
                            _pumpStartUtc, packet.CaptureUtc.Value, rate);
                        var pumpFrame = TotalFramesWritten();
                        if (Math.Abs(target - pumpFrame) > MaxHapticStampSkewSeconds * rate)
                        {
                            MarkHapticCompromisedLocked();
                            target = Math.Max(0, pumpFrame);
                        }
                        if (target < 0)
                        {
                            var trimFrames = (int)Math.Min(frames, -target);
                            offset = trimFrames * entry.BlockAlign;
                            frames -= trimFrames;
                            target = 0;
                        }

                        if (frames <= 0)
                        {
                            return;
                        }

                        if (entry.Writer == null)
                        {
                            OpenHapticChunkLocked(entry, target);
                        }

                        var drift = target - entry.TimelineFrames;
                        if (drift > MaxHapticGapPaddingSeconds * rate ||
                            entry.ChunkFramesWritten + drift >= HapticChunkFrames(rate))
                        {
                            // The endpoint was idle. Do not materialise the silence or append this
                            // packet at the old position: a new filename places it exactly on UTC.
                            OpenHapticChunkLocked(entry, target);
                        }
                        else if (drift > 0)
                        {
                            WriteHapticSilenceLocked(entry, drift);
                        }
                        else if (drift < 0)
                        {
                            var trimFrames = (int)Math.Min(frames, -drift);
                            offset += trimFrames * entry.BlockAlign;
                            frames -= trimFrames;
                        }
                    }
                    else
                    {
                        entry.UnstampedPackets++;
                        MarkHapticCompromisedLocked();
                        // Rare driver fallback. Arrival time is less exact than a packet stamp, but
                        // it is still vastly safer than putting a mid-session packet at timeline zero.
                        if (entry.Writer == null)
                        {
                            OpenHapticChunkLocked(entry, Math.Max(0, TotalFramesWritten()));
                        }
                    }

                    if (frames <= 0)
                    {
                        return;
                    }

                    var bytes = (int)frames * entry.BlockAlign;
                    TrackHapticPeakLocked(entry, packet.Buffer, offset, bytes);
                    WriteHapticFramesLocked(entry, packet.Buffer, offset, frames);
                    entry.CapturedFrames += frames;
                    if (packet.CaptureUtc.HasValue)
                    {
                        entry.StampedPackets++;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    MarkHapticCompromisedLocked();
                }
                _logger?.Debug(ex, $"[Recording] A haptic reference packet from '{entry.Name}' was not written.");
            }
        }

        /// <summary>Stands silence in for a short packet hole, keeping positions true.</summary>
        private void WriteHapticSilenceLocked(HapticEndpointCapture entry, long frames)
        {
            var remaining = frames;
            var silence = new byte[Math.Min(remaining * entry.BlockAlign, 64 * 1024)];
            while (remaining > 0 && silence.Length > 0)
            {
                var chunk = (int)Math.Min(silence.Length / entry.BlockAlign, remaining);
                WriteHapticFramesLocked(entry, silence, 0, chunk);
                remaining -= chunk;
            }
        }

        /// <summary>
        /// Writes frames without crossing a haptic chunk boundary. Unlike the main pump's files,
        /// these chunks rotate on the endpoint's own stamped timeline, so no packet can be written
        /// into one file and then silently reappear at the start of the next.
        /// </summary>
        private void WriteHapticFramesLocked(
            HapticEndpointCapture entry, byte[] buffer, int offset, long frames)
        {
            var sourceOffset = offset;
            var remaining = frames;
            var chunkFrames = HapticChunkFrames(_outputFormat.SampleRate);
            while (remaining > 0)
            {
                if (entry.Writer == null)
                {
                    OpenHapticChunkLocked(entry, entry.TimelineFrames);
                }

                var capacity = chunkFrames - entry.ChunkFramesWritten;
                if (capacity <= 0)
                {
                    OpenHapticChunkLocked(entry, entry.TimelineFrames);
                    capacity = chunkFrames;
                }

                var writeFrames = Math.Min(remaining, capacity);
                var writeBytes = checked((int)writeFrames * entry.BlockAlign);
                entry.Writer.Write(buffer, sourceOffset, writeBytes);
                sourceOffset += writeBytes;
                remaining -= writeFrames;
                entry.TimelineFrames += writeFrames;
                entry.ChunkFramesWritten += writeFrames;
            }
        }

        private static long HapticChunkFrames(int sampleRate)
        {
            return (long)UnlockRecordingService.SegmentSeconds * sampleRate;
        }

        /// <summary>Starts a sparse reference chunk at one exact position on the pump timeline.</summary>
        private void OpenHapticChunkLocked(HapticEndpointCapture entry, long startFrame)
        {
            CloseHapticChunkLocked(entry);
            startFrame = Math.Max(0, startFrame);
            var startUtc = RecordingPaths.AudioFrameUtc(
                _pumpStartUtc, startFrame, _outputFormat.SampleRate);
            var name = RecordingPaths.BuildAudioChunkFileName(
                RecordingPaths.HapticReferenceChunkFilePrefix(entry.Index), startUtc);
            entry.Writer = new WaveFileWriter(Path.Combine(_bufferDirectory, name), _outputFormat);
            entry.ChunkStartFrame = startFrame;
            entry.ChunkFramesWritten = 0;
            entry.TimelineFrames = startFrame;
        }

        private void CloseHapticChunkLocked(HapticEndpointCapture entry)
        {
            try
            {
                entry?.Writer?.Dispose();
            }
            catch (Exception ex)
            {
                MarkHapticCompromisedLocked();
                _logger?.Debug(ex, "[Recording] A haptic reference chunk could not be finalized.");
            }
            if (entry != null)
            {
                entry.Writer = null;
                entry.ChunkFramesWritten = 0;
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
                    if (drift > MaxHapticGapPaddingSeconds * rate)
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
            var chunkFrames = HapticChunkFrames(track.Format.SampleRate);
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
            foreach (var track in new[] { _stampedChimeTrack, _stampedGameReferenceTrack })
            {
                if (track?.Writer == null || !track.OriginUtc.HasValue)
                {
                    continue;
                }

                var nowFrame = RecordingPaths.AudioFrameAt(
                    track.OriginUtc.Value, nowUtc, track.Format.SampleRate);
                if (nowFrame - track.ChunkStartFrame >= HapticChunkFrames(track.Format.SampleRate))
                {
                    CloseAuxiliaryChunkLocked(track);
                }
            }
        }

        private void CloseAuxiliaryTracksLocked()
        {
            CloseAuxiliaryChunkLocked(_stampedChimeTrack);
            CloseAuxiliaryChunkLocked(_stampedGameReferenceTrack);
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

        /// <summary>
        /// Closes a sparse file once wall time has moved past its nominal span. A haptic burst often
        /// ends without another packet, so waiting for the next packet to rotate would leave the
        /// RIFF header unfinished when clip export tries to read it.
        /// </summary>
        private void CloseExpiredHapticChunksLocked(long timelineFrame)
        {
            var chunkFrames = HapticChunkFrames(_outputFormat.SampleRate);
            foreach (var entry in _hapticCaptures.Values)
            {
                if (entry.Writer != null && timelineFrame - entry.ChunkStartFrame >= chunkFrames)
                {
                    CloseHapticChunkLocked(entry);
                }
            }
        }

        /// <summary>Loudest sample this reference has carried, for the per-endpoint stop summary.</summary>
        private static void TrackHapticPeakLocked(
            HapticEndpointCapture entry, byte[] buffer, int offset, int bytes)
        {
            for (var i = offset; i + 4 <= offset + bytes; i += 4)
            {
                var magnitude = Math.Abs(BitConverter.ToSingle(buffer, i));
                if (magnitude > entry.Peak)
                {
                    entry.Peak = magnitude;
                }
            }
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
                        CloseExpiredHapticChunksLocked(targetFrames);
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
            return _chunkStartWallClockSamples +
                _chunkSamplesWritten / Math.Max(1, _outputFormat.Channels);
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
                LogAuxiliaryTracksLocked();
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
                CloseHapticChunksLocked();
                CloseAuxiliaryTracksLocked();
                var tracked = new List<IWaveIn> { system, restoredGame };
                tracked.AddRange(haptics);
                LogTimelineNotices(tracked.ToArray());
                _systemCapture = null;
                _restoredGameCapture = null;
                _micCapture = null;
                _hapticCaptures.Clear();
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

            var rate = Math.Max(1, _outputFormat?.SampleRate ?? 1);
            var summaries = new List<string>();
            foreach (var entry in _hapticCaptures.Values)
            {
                summaries.Add(
                    $"hap{entry.Index} '{entry.Name}': " +
                    $"{(entry.CapturedFrames / (double)rate).ToString("0.0", CultureInfo.InvariantCulture)}s packets, " +
                    $"peak {entry.Peak.ToString("0.0000", CultureInfo.InvariantCulture)}, " +
                    $"stamped={entry.StampedPackets} unstamped={entry.UnstampedPackets}");
            }

            _logger?.Info(
                "[Recording] Haptic reference: " + string.Join("; ", summaries.ToArray()) + ".");
        }

        private void LogAuxiliaryTracksLocked()
        {
            var tracks = new List<string>();
            foreach (var track in new[] { _stampedChimeTrack, _stampedGameReferenceTrack })
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

        private void DisposeHapticCaptures()
        {
            foreach (var entry in _hapticCaptures.Values)
            {
                try { entry.Capture?.Dispose(); } catch { }
            }

            _hapticCaptures.Clear();
        }

        private void OpenChunkLocked()
        {
            // Initial endpoint captures were opened before the pump timestamp existed. The first
            // main chunk fixes that timestamp and makes their packet callbacks usable.
            foreach (var entry in _hapticCaptures.Values)
            {
                entry.Installed = true;
            }

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

        private void CloseHapticChunksLocked()
        {
            foreach (var entry in _hapticCaptures.Values)
            {
                CloseHapticChunkLocked(entry);
            }
        }

        private void FailLocked(Exception ex, string message)
        {
            if (!_failed)
            {
                _failed = true;
                _logger?.Warn(ex, message);
            }

            _running = false;
            if (_hapticCaptures.Count > 0)
            {
                MarkHapticCompromisedLocked();
            }
            CloseChunkLocked();
            CloseHapticChunksLocked();
            CloseAuxiliaryTracksLocked();
        }

        private void CleanupLocked()
        {
            _running = false;
            CloseChunkLocked();
            CloseHapticChunksLocked();
            CloseAuxiliaryTracksLocked();
            DisposeCapture(ref _systemCapture);
            DisposeCapture(ref _restoredGameCapture);
            DisposeCapture(ref _micCapture);
            DisposeHapticCaptures();
            _systemBuffer = null;
            _restoredGameBuffer = null;
            _micBuffer = null;
            _mix = null;
        }

        /// <summary>A span the haptic references may not cover or place correctly.</summary>
        private struct HapticHole
        {
            public DateTime StartUtc;
            public DateTime EndUtc;
        }

        /// <summary>One controller endpoint's capture and the reference track it writes.</summary>
        private sealed class HapticEndpointCapture
        {
            public string DeviceId;
            public string Name;
            public int Index;
            public ProcessLoopbackCapture Capture;
            public WaveFileWriter Writer;
            public bool Installed;
            public int BlockAlign;

            /// <summary>
            /// Frames this track holds, counted from the pump's timeline zero and carried across
            /// chunk rotations: it is what an incoming packet's own stamp is compared against.
            /// </summary>
            public long TimelineFrames;

            /// <summary>Global frame represented by sample zero of the current sparse WAV.</summary>
            public long ChunkStartFrame;

            public long ChunkFramesWritten;

            /// <summary>Actual endpoint packet frames, excluding silence used to bridge tiny holes.</summary>
            public long CapturedFrames;

            public long StampedPackets;

            public long UnstampedPackets;

            public float Peak;
        }

        /// <summary>One directly timestamped chime or game-reference track.</summary>
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
