using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Records unlock video clips. While a game runs, WGC + Media Foundation captures the game
    /// window into a rolling buffer of short .mp4 segments (clean game footage — no toast) under
    /// the plugin's user data path; on each own-unlock a clip window anchored purely on the
    /// unlock moment is trimmed out of the buffer (see <see cref="SegmentTimeline"/>), and that
    /// achievement's recorded toast animation (<see cref="Capture.ToastOverlayTrack"/>) is
    /// composited into the clip by an export-time re-encode — so every clip shows exactly its own
    /// toast, at the unlock moment, regardless of how the on-screen wave stacked or queued, and
    /// whether or not that toast was ever shown: the toast pipeline renders an unrevealed wave for
    /// clip-worthy unlocks (see <see cref="WouldRequestClip"/>), and such clips carry no chime.
    /// Subscribes to <see cref="PlayniteAchievementsPlugin.AchievementUnlocked"/> in parallel to
    /// the toast service, and to <see cref="ToastNotificationService.TracksCompleted"/> for the
    /// overlay tracks (<see cref="ToastNotificationService.WaveDisplayed"/> is only a liveness
    /// bump for the track wait). The toastless base clip always exists before the re-encode runs,
    /// so a re-encode failure degrades to a toastless clip, never a lost one. Per-unlock failures
    /// are silent-but-logged; configuration failures (low disk, repeated capture crashes) raise
    /// one notification per session.
    /// </summary>
    internal sealed class UnlockRecordingService : IDisposable
    {
        /// <summary>Rolling capture segment length in seconds (K).</summary>
        internal const int SegmentSeconds = 5;

        private const string BufferRootFolderName = "RecordingBuffer";
        private const long MinFreeBytesToStart = 2L * 1024 * 1024 * 1024;
        private const long MinFreeBytesToContinue = 500L * 1024 * 1024;
        /// <summary>
        /// Disk the rolling buffer may use. This is the buffer's size, not its duration: how far
        /// back it reaches is whatever the budget buys at the current capture settings, which is
        /// why one number serves every resolution. 2 GB is the smallest figure that still holds
        /// more than two minutes at the encoder's bitrate ceiling — roughly 26 minutes at 1080p30,
        /// 4.6 at 4K60, 2.3 at the cap — so the buffer can always reach back past a platform that
        /// reports an unlock minutes before the player sees it. Only what is actually written is
        /// occupied; the budget is a ceiling, and it is clamped further when the drive is short.
        /// </summary>
        private const long BufferBudgetBytes = 2L * 1024 * 1024 * 1024;
        // Toast-slot allowance used only by the prune floor, which must keep a clip window's worth
        // of footage whatever the budget says. Generous enough to cover any toast-duration setting.
        private const double MaxToastSlotAllowanceSeconds = 30.0;
        private const int WindowResolveTimeoutSeconds = 60;
        // How long to hold off the capture start waiting for the started process's main window.
        // Kept short: unlocks that fire before the capture is live can never be clipped, so a
        // slow-launching game must not leave a long dead window (observed: a launcher-style
        // process with no main window stalled the old 60s wait while the first poll tick's
        // unlocks all got dropped). After the grace we start on the best-guess monitor and
        // correct later if the game window appears somewhere else.
        private const int WindowResolveGraceSeconds = 15;
        private const int WindowResolvePollMs = 2000;
        // The overlay-track wait gives up after this much toast SILENCE (no wave settled — visible
        // or unrevealed — and no track completed), not this long after detection, so a burst of
        // queued waves keeps later requests waiting for their own toast. A give-up saves the
        // toastless base clip. Now that clip-worthy unlocks always produce a wave, reaching this
        // timeout means a genuine failure: a minimized game holding the queue, or a wave that
        // threw or was cleared.
        private const int ToastWaitTimeoutSeconds = 30;
        private const int ToastWaitPollSeconds = 5;
        // Unrelated waves can keep the global activity clock moving forever. This absolute bound
        // guarantees a lost/mismatched track eventually degrades to a toastless clip.
        private const int MaxToastWaitSeconds = 5 * 60;
        // The clip's toast slot: the effective display duration plus an allowance for the
        // slide-in delay (~0.75s to the snap) and the slide-out, plus a short tail after it.
        // The slot sizes the base window (worst case, before the track exists); the composited
        // clip is then cut PostFadeTailSeconds after the recorded fade, so the audio tail never
        // reaches into the next wave's unlock sound.
        private const double SlideAllowanceSeconds = 2.0;
        private const double ToastTailSeconds = 1.0;
        private const double PostFadeTailSeconds = 0.5;
        // The chime mix: the sidecar read spans the toast display duration plus this tail — long
        // chimes ring for as long as their toast shows — but is hard-capped at
        // ChimeMaxSliceSeconds. The cap keeps the NEXT sequential wave's chime (which fires
        // ~duration+1s after this one) out of the window with real margin, and shortens the span
        // the cancellation's drift tracker must cover. ChimeLeadBeforeToastSeconds is how far the
        // chime onset precedes the toast reveal in the clip (sound fires, then the 450ms
        // sound-align delay plus ~300ms of slide-in precede the settled card).
        private const double ChimeTailBeyondToastSeconds = 0.5;
        private const double ChimeMaxSliceSeconds = 4.0;
        private const double ChimeFadeOutSeconds = 0.15;
        private const double ChimeLeadBeforeToastSeconds = 0.75;
        private const int PruneIntervalSeconds = 30;
        private const int DrainTimeoutSeconds = 45;
        // Fallbacks matching the PersistedSettings defaults, used when settings are unavailable.
        private const int DefaultPollIntervalSeconds = 15;
        private const int DefaultPreRollSeconds = 15;
        private const int DefaultRecordingFps = 30;
        private const string UnavailableNotificationId = "PlayAch-RecordingUnavailable";

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly string _pluginUserDataPath;
        // Resolves the started process id for a game (null game id: most recently started game).
        private readonly Func<Guid?, int?> _getGameProcessId;
        private readonly Func<string, bool> _isProviderRecordingEnabled;
        private readonly ToastNotificationService _toastNotifications;
        // Optional foreground tracker: supplies learned game window handles and drives capture
        // ownership switches when the user moves between running games.
        private readonly ActiveGameWindowTracker _windowTracker;
        private readonly UnlockScreenshotService _screenshotService;

        private readonly object _gate = new object();
        // Requests whose overlay track hasn't arrived yet (guarded by _gate).
        private readonly List<ClipRequest> _awaitingTrack = new List<ClipRequest>();
        private readonly HashSet<Task> _inFlightTasks = new HashSet<Task>();
        // One overlay re-encode at a time so a burst wave doesn't saturate the encoder while the
        // game is running.
        private readonly SemaphoreSlim _reencodeGate = new SemaphoreSlim(1, 1);
        // Base extractions run per request and were otherwise unbounded: a burst of unlocks put
        // one Media Foundation concat/mux per achievement on the thread pool at once, which
        // competes for CPU with the toast sampler's rasterization on the UI thread and shows up as
        // the live notification stuttering while clips are written. Bounded rather than serialized
        // because each one also waits on file I/O, and because a clip's base must still land
        // promptly — the segment buffer suspends pruning for every outstanding window until it does.
        private readonly SemaphoreSlim _baseExportGate = new SemaphoreSlim(MaxConcurrentBaseExports);

        /// <summary>
        /// Concurrent base extractions allowed. Two, plus the single re-encode, keeps heavy media
        /// work to three operations while a wave is composing.
        /// </summary>
        private const int MaxConcurrentBaseExports = 2;
        // Buffer directories owned by a live or still-draining session (guarded by _gate). A new
        // session's stale-buffer cleanup must not delete a previous session's buffer while its
        // pending clips are still being produced.
        private readonly HashSet<string> _liveBufferDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private CaptureSession _session;
        private bool _sessionNotified;
        private bool _disposed;
        // Last time the toast pipeline showed a wave or completed a track (guarded by _gate).
        // Extends the track wait so queued waves far beyond the base timeout still get their
        // toast composited.
        private DateTime _lastToastActivityUtc;
        // Requests between window computation and base-clip extraction: while any exist, the
        // buffered segments they need must survive age-based pruning.
        // Window starts of clips still between window computation and base extraction. Those clips
        // read the buffer, so the pruner must not cut back past the oldest of them even when the
        // budget is exceeded — once a base clip exists, its request no longer reads the buffer.
        private readonly object _outstandingGate = new object();
        private readonly List<DateTime> _outstandingWindowStarts = new List<DateTime>();

        public UnlockRecordingService(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            string pluginUserDataPath,
            Func<Guid?, int?> getGameProcessId,
            ToastNotificationService toastNotifications = null,
            Func<string, bool> isProviderRecordingEnabled = null,
            ActiveGameWindowTracker windowTracker = null)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _pluginUserDataPath = pluginUserDataPath;
            _getGameProcessId = getGameProcessId;
            _toastNotifications = toastNotifications;
            _isProviderRecordingEnabled = isProviderRecordingEnabled;
            _windowTracker = windowTracker;
            _screenshotService = new UnlockScreenshotService(logger);

            PlayniteAchievementsPlugin.AchievementUnlocked += OnAchievementUnlocked;
            if (_toastNotifications != null)
            {
                _toastNotifications.WaveDisplayed += OnToastWaveDisplayed;
                _toastNotifications.TracksCompleted += OnToastTracksCompleted;
            }

            if (_windowTracker != null)
            {
                _windowTracker.StableForegroundGameChanged += OnStableForegroundGameChanged;
            }
        }

        private sealed class CaptureSession
        {
            public string BufferDirectory;
            public Guid OwnerGameId;
            public string GameName;
            public DateTime CaptureStartUtc;
            // The WGC + Media Foundation capture engine: occlusion-independent, HDR-correct,
            // GPU-resident. Writes .mp4 segments the Media Foundation export/prune consume.
            public WgcVideoRecorder WgcRecorder;
            // Segment file extension for the capture engine (.mp4 for WGC-MF). Threaded into segment
            // discovery/prune/export.
            public string SegmentExtension = RecordingPaths.SegmentFileExtension;
            // Bytes the buffer currently occupies, refreshed each prune tick.
            public long LastKnownBufferBytes;
            public bool BufferBudgetClampLogged;
            public AudioLoopbackRecorder AudioRecorder;
            // The Playnite process-tree sidecar (chm_*.wav). For a Playnite-launched game this
            // overlaps the game's audio, so the main recorder's tee (gam_*.wav), or the game-only
            // main track itself, is cancelled from it before the per-clip chime mix.
            public AudioLoopbackRecorder ChimeRecorder;
            public CancellationTokenSource Cts;
            public Timer PruneTimer;
            public volatile bool Stopping;
            // Capture-health watchdog state (diagnostic): the newest segment file seen and when it
            // last advanced (to detect a capture that stops opening segments), plus the largest
            // closed segment seen this session as a healthy-size reference in the log.
            public string LastSegmentPath;
            public DateTime LastSegmentAdvanceUtc;
            public long MaxSegmentBytes;
        }

        private sealed class ClipRequest
        {
            public CaptureSession Session;
            public Guid CaptureCorrelationId;
            public string ProviderKey;
            public string GameName;
            public string AchievementName;
            public int AchievementNumber;
            public int TotalCount;
            public DateTime? ReportedUnlockUtc;
            public DateTime? VideoAnchorUtc;
            public UnlockVideoAnchorSource VideoAnchorSource;
            public DateTime ObservedUtc;
            public bool IsTestFire;

            /// <summary>Toast display duration snapshotted at unlock (theme override included).</summary>
            public int EffectiveToastSeconds;

            /// <summary>When this request's own wave chime played — where the chime mix reads from.</summary>
            public DateTime? OwnSoundUtc;

            /// <summary>
            /// Completed with this achievement's overlay track when its wave finishes, or null
            /// (toastless clip) on timeout/shutdown.
            /// </summary>
            public TaskCompletionSource<ToastOverlayTrack> TrackTcs;
        }

        // === Session lifecycle ===

        public void OnGameStarted(Playnite.SDK.Models.Game game)
        {
            if (_disposed)
            {
                return;
            }

            _sessionNotified = false;
            // A single capture session exists at a time; the most recently started game owns it.
            StopCurrentSession();

            var persisted = _settings?.Persisted;
            if (persisted?.EnableUnlockRecordings != true)
            {
                return;
            }

            var outputDir = ResolveOutputDirectory(persisted);
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                _logger?.Warn("[Recording] Unlock recordings are enabled but the output folder is missing/invalid; skipping this session.");
                NotifyRecordingUnavailableOnce();
                return;
            }

            var bufferRoot = Path.Combine(_pluginUserDataPath, BufferRootFolderName);
            if (!HasFreeSpace(bufferRoot, MinFreeBytesToStart))
            {
                _logger?.Warn("[Recording] Less than 2 GB free on the buffer drive; skipping this session.");
                NotifyRecordingUnavailableOnce();
                return;
            }

            var session = new CaptureSession
            {
                // The unique suffix keeps a same-second stop-then-handoff from colliding with the
                // previous session's still-draining buffer directory.
                BufferDirectory = Path.Combine(
                    bufferRoot,
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                        "-" + Guid.NewGuid().ToString("N").Substring(0, 8)),
                OwnerGameId = game?.Id ?? Guid.Empty,
                GameName = game?.Name,
                Cts = new CancellationTokenSource()
            };

            lock (_gate)
            {
                _session = session;
                _liveBufferDirs.Add(session.BufferDirectory);
            }

            _ = Task.Run(() => StartCaptureWhenWindowResolvesAsync(session));
        }

        /// <summary>
        /// Owner-aware stop: ends the capture session only when the stopped game owns it, then
        /// adopts <paramref name="handoffGame"/> (the still-running game that should be captured
        /// next) with a fresh session and buffer. A stop for a non-owner game is a no-op so the
        /// owner's capture keeps running.
        /// </summary>
        public void OnGameStopped(
            Playnite.SDK.Models.Game stoppedGame,
            Playnite.SDK.Models.Game handoffGame = null)
        {
            CaptureSession observed;
            lock (_gate)
            {
                observed = _session;
                if (observed != null &&
                    stoppedGame != null &&
                    observed.OwnerGameId != Guid.Empty &&
                    observed.OwnerGameId != stoppedGame.Id)
                {
                    _logger?.Debug(
                        $"[Recording] '{stoppedGame.Name}' stopped but '{observed.GameName}' owns the capture; session continues.");
                    return;
                }
            }

            // Stop only the session the owner check saw: a concurrent start or foreground switch
            // may already have swapped in a session for a still-running game, which must survive.
            if (observed != null)
            {
                StopSession(observed);
            }

            if (handoffGame != null && !_disposed)
            {
                OnGameStarted(handoffGame);
            }
        }

        /// <summary>
        /// Follows the user's attention between running games. WGC captures per-window and the
        /// recorder resolves the session's live owner each tick, so switching to another running
        /// game is a cheap owner flip — no restart, works across monitors — after which clip gating
        /// and the capture both target the new game. The tracker debounces, so alt-tab flicker never
        /// lands here.
        /// </summary>
        private void OnStableForegroundGameChanged(object sender, StableForegroundGameChangedEventArgs e)
        {
            try
            {
                if (_disposed || e?.Game == null)
                {
                    return;
                }

                CaptureSession session;
                lock (_gate)
                {
                    session = _session;
                }

                if (session == null || session.Stopping || session.OwnerGameId == e.Game.Id)
                {
                    return;
                }

                lock (_gate)
                {
                    if (!ReferenceEquals(_session, session) || session.Stopping)
                    {
                        return;
                    }

                    session.OwnerGameId = e.Game.Id;
                    session.GameName = e.Game.Name;
                }

                _logger?.Info($"[Recording] Capture owner switched to '{e.Game.Name}' (WGC follows the window, no restart).");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Foreground-switch handling failed.");
            }
        }

        private void StopCurrentSession()
        {
            StopSession(expected: null);
        }

        private void StopSession(CaptureSession expected)
        {
            CaptureSession session;
            lock (_gate)
            {
                if (_session == null || (expected != null && !ReferenceEquals(_session, expected)))
                {
                    return;
                }

                session = _session;
                _session = null;
            }

            session.Stopping = true;
            try
            {
                session.Cts.Cancel();
            }
            catch
            {
            }

            session.PruneTimer?.Dispose();
            session.PruneTimer = null;
            _ = Task.Run(() => ShutdownSessionAsync(session));
        }

        /// <summary>
        /// Waits (2s polls, up to 60s) for the game window to become resolvable, then starts the
        /// rolling WGC per-window capture (the recorder re-resolves the owner's window each tick,
        /// so it follows moves and foreground switches without a restart).
        /// </summary>
        private async Task StartCaptureWhenWindowResolvesAsync(CaptureSession session)
        {
            try
            {
                // Crash cleanup off the game-started event thread: deleting a large leftover
                // buffer can take a moment and must not delay game launch handling.
                CleanupStaleBufferDirectories(Path.GetDirectoryName(session.BufferDirectory));

                var token = session.Cts.Token;
                var deadline = CaptureTimelineClock.UtcNow.AddSeconds(WindowResolveTimeoutSeconds);
                var graceDeadline = CaptureTimelineClock.UtcNow.AddSeconds(WindowResolveGraceSeconds);
                var mainWindowResolved = false;
                System.Drawing.Rectangle? bounds = null;
                while (!token.IsCancellationRequested)
                {
                    var trackedHwnd = _windowTracker?.TryGetWindowHandle(session.OwnerGameId) ?? IntPtr.Zero;
                    var processId = _getGameProcessId?.Invoke(session.OwnerGameId);
                    mainWindowResolved = trackedHwnd != IntPtr.Zero ||
                                         (processId.HasValue && ProcessHasMainWindow(processId.Value));
                    // Give the started process a short grace to open its main window before
                    // falling back to the foreground window's monitor (usually the same monitor
                    // the game is launching on). A later-appearing game window on a different
                    // monitor is handled by the correction watcher below.
                    var stillLaunching = processId.HasValue &&
                                         !mainWindowResolved &&
                                         CaptureTimelineClock.UtcNow < graceDeadline;
                    if (!stillLaunching)
                    {
                        bounds = _screenshotService.TryGetGameMonitorBounds(trackedHwnd, processId);
                        if (bounds.HasValue || CaptureTimelineClock.UtcNow >= deadline)
                        {
                            break;
                        }
                    }

                    await Task.Delay(WindowResolvePollMs, token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (!bounds.HasValue)
                {
                    _logger?.Warn("[Recording] No game window/monitor resolved within 60s; recording skipped for this session.");
                    return;
                }

                var persisted = _settings?.Persisted;
                if (persisted == null)
                {
                    return;
                }

                Directory.CreateDirectory(session.BufferDirectory);

                // WGC + Media Foundation capture: occlusion-independent, HDR-correct, GPU-resident,
                // no external binary. Unavailable only on Windows N/KN without the H.264 MFT or
                // pre-1903; there is no fallback, so recording is skipped with one notification.
                if (!TryStartWgcCapture(session, persisted))
                {
                    _logger?.Warn("[Recording] WGC + Media Foundation capture is unavailable on this machine; recording skipped for this session.");
                    NotifyRecordingUnavailableOnce();
                    return;
                }

                if (session.Stopping)
                {
                    session.WgcRecorder?.Stop();
                    return;
                }

                if (persisted.RecordingIncludeAudio)
                {
                    var recorder = new AudioLoopbackRecorder(
                        session.BufferDirectory,
                        _logger,
                        persisted.RecordingAudioSource,
                        persisted.RecordingIncludeMicrophone,
                        () => _getGameProcessId?.Invoke(session.OwnerGameId),
                        pid => _windowTracker?.IsInPlayniteProcessTree(pid));
                    if (recorder.Start())
                    {
                        session.AudioRecorder = recorder;
                    }
                    else
                    {
                        recorder.Dispose();
                    }

                    var chimeMode = session.AudioRecorder?.ChimeCaptureMode ?? PlayniteChimeCaptureMode.Unavailable;
                    if (session.AudioRecorder != null &&
                        chimeMode != PlayniteChimeCaptureMode.Unavailable &&
                        AudioLoopbackRecorder.IsChimeCaptureSupported)
                    {
                        var chimeRecorder = new AudioLoopbackRecorder(
                            session.BufferDirectory,
                            _logger,
                            capturePlayniteChimes: true);
                        if (chimeRecorder.Start())
                        {
                            session.ChimeRecorder = chimeRecorder;
                        }
                        else
                        {
                            chimeRecorder.Dispose();
                        }
                    }
                }

                session.PruneTimer = new Timer(
                    _ => PruneTick(session),
                    null,
                    TimeSpan.FromSeconds(PruneIntervalSeconds),
                    TimeSpan.FromSeconds(PruneIntervalSeconds));
                _logger?.Info(
                    $"[Recording] Capture started for '{session.GameName}' (WGC+MediaFoundation), buffer={session.BufferDirectory}.");
            }
            catch (OperationCanceledException)
            {
                // Game stopped while waiting for the window.
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Failed to start capture session.");
            }
        }

        /// <summary>
        /// Starts the WGC + Media Foundation capture (occlusion-independent, HDR-correct, GPU-resident)
        /// for the session's game window, writing .mp4 segments into the buffer directory. Returns
        /// false — leaving nothing running — when WGC-MF isn't usable (pre-1903, Windows N/KN without
        /// the H.264 MFT), so the caller skips recording for the session.
        /// </summary>
        private bool TryStartWgcCapture(CaptureSession session, PersistedSettings persisted)
        {
            try
            {
                if (!WgcVideoRecorder.IsSupported || !MediaFoundationH264Encoder.IsAvailable())
                {
                    return false;
                }

                // Resolve the LEARNED window of the session's CURRENT owner each tick (read live, not
                // a captured snapshot, so a foreground switch to another running game redirects the
                // per-window capture without a restart) — never a foreground fallback, so it follows
                // the actual game once known instead of whatever window is on top at capture start.
                Func<IntPtr> resolveHwnd = () => _windowTracker?.TryGetWindowHandle(session.OwnerGameId) ?? IntPtr.Zero;

                var recorder = new WgcVideoRecorder(
                    resolveHwnd, session.BufferDirectory, persisted.RecordingFps, SegmentSeconds,
                    persisted.RecordingResolution, persisted.RecordingQuality, _logger);
                if (!recorder.Start())
                {
                    recorder.Dispose();
                    return false;
                }

                session.WgcRecorder = recorder;
                session.SegmentExtension = RecordingPaths.SegmentFileExtension;
                session.CaptureStartUtc = CaptureTimelineClock.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] WGC + Media Foundation capture could not start.");
                return false;
            }
        }


        private async Task ShutdownSessionAsync(CaptureSession session)
        {
            try
            {
                // Stop the WGC-MF capture and finalize its current segment before pending clips read
                // the buffer (an unfinalized mp4 segment is not decodable).
                session.WgcRecorder?.Stop();

                // Close the current audio chunks before pending clips read the buffer.
                session.AudioRecorder?.Stop();
                session.ChimeRecorder?.Stop();

                // An active wave can finish after the game exits. Keep its pending track alive
                // while this session's clip tasks drain so a last-second unlock/test fire still
                // gets composited; a queued wave that was cleared times out normally.
                Task[] inFlight;
                lock (_gate)
                {
                    inFlight = _inFlightTasks.ToArray();
                }

                if (inFlight.Length > 0)
                {
                    await Task.WhenAny(Task.WhenAll(inFlight), Task.Delay(TimeSpan.FromSeconds(DrainTimeoutSeconds)))
                        .ConfigureAwait(false);
                }

                session.WgcRecorder?.Dispose();
                session.WgcRecorder = null;
                session.AudioRecorder?.Dispose();
                session.AudioRecorder = null;
                session.ChimeRecorder?.Dispose();
                session.ChimeRecorder = null;

                TryDeleteDirectory(session.BufferDirectory);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Session shutdown failed.");
            }
            finally
            {
                lock (_gate)
                {
                    _liveBufferDirs.Remove(session.BufferDirectory);
                }
            }
        }

        // === Unlock handling ===

        /// <summary>
        /// Why an unlock does or does not produce a clip. Split out of
        /// <see cref="OnAchievementUnlocked"/> so the decision itself is side-effect free and can
        /// be asked twice: once here, and once by the toast pipeline deciding whether an unlock
        /// owes an overlay track.
        /// </summary>
        private enum ClipEligibility
        {
            Eligible,

            /// <summary>Disposed, a preview, a friend unlock, or recordings are turned off.</summary>
            NotRecordable,
            CaptureInactive,
            ProviderDisabled,
            BelowRarity,
            DifferentGame,
        }

        /// <summary>
        /// Resolves whether this unlock would be cut into a clip right now, and the session it
        /// would be cut from. Logs nothing and changes no state — the caller decides what to say.
        /// </summary>
        private ClipEligibility EvaluateClipEligibility(
            AchievementUnlockedEventArgs e, out CaptureSession session)
        {
            session = null;
            if (_disposed || e == null || e.IsPreview || e.IsFriendUnlock)
            {
                return ClipEligibility.NotRecordable;
            }

            if (_settings?.Persisted?.EnableUnlockRecordings != true)
            {
                return ClipEligibility.NotRecordable;
            }

            lock (_gate)
            {
                session = _session;
            }

            // Active means the WGC-MF recorder is running for this session.
            if (session == null || session.Stopping || session.WgcRecorder == null)
            {
                return ClipEligibility.CaptureInactive;
            }

            if (_isProviderRecordingEnabled?.Invoke(e.ProviderKey) == false)
            {
                return ClipEligibility.ProviderDisabled;
            }

            var persisted = _settings.Persisted;
            if (!UnlockCaptureRarityFilter.ShouldCapture(
                    e,
                    persisted.UnlockRecordingRarities,
                    persisted.UnlockRecordingAlwaysCaptureCompletion))
            {
                return ClipEligibility.BelowRarity;
            }

            // The buffer only contains the owner game's monitor; an unlock from another running
            // game still gets its toast and screenshot, but a clip of the wrong game is useless.
            if (e.PlayniteGameId != Guid.Empty &&
                session.OwnerGameId != Guid.Empty &&
                e.PlayniteGameId != session.OwnerGameId)
            {
                return ClipEligibility.DifferentGame;
            }

            return ClipEligibility.Eligible;
        }

        /// <summary>
        /// Whether this unlock would produce a clip right now, so the toast pipeline can decide
        /// that it owes an overlay track. Both sides read the same state through
        /// <see cref="EvaluateClipEligibility"/>. It is evaluated in the toast service's unlock
        /// handler, which runs before this service's own: a capture session starting or stopping
        /// in that instant can make the two disagree, which costs at most a wasted unrevealed wave
        /// or a track wait that times out as it already would.
        /// </summary>
        internal bool WouldRequestClip(AchievementUnlockedEventArgs e) =>
            EvaluateClipEligibility(e, out _) == ClipEligibility.Eligible;

        private void OnAchievementUnlocked(object sender, AchievementUnlockedEventArgs e)
        {
            switch (EvaluateClipEligibility(e, out var session))
            {
                case ClipEligibility.Eligible:
                    break;

                case ClipEligibility.CaptureInactive:
                    _logger?.Debug(
                        $"[Recording] Unlock '{e.DisplayName}' ignored; capture is not active (session={(session == null ? "none" : session.Stopping ? "stopping" : "no capture")}).");
                    return;

                case ClipEligibility.BelowRarity:
                    _logger?.Debug(
                        $"[Recording] Unlock '{e.DisplayName}' is below the minimum recording rarity; no clip.");
                    return;

                case ClipEligibility.DifferentGame:
                    _logger?.Debug(
                        $"[Recording] Unlock '{e.DisplayName}' is from '{e.GameName}' but the capture follows '{session.GameName}'; toast/screenshot only, no clip.");
                    return;

                default:
                    return;
            }

            var persisted = _settings.Persisted;

            var handlerUtc = CaptureTimelineClock.UtcNow;
            var observedUtc = e.ObservedUtc == default(DateTime) ? handlerUtc : AsUtc(e.ObservedUtc);
            var videoAnchorUtc = e.VideoAnchorUtc ?? e.UnlockTimeUtc;
            if (videoAnchorUtc.HasValue)
            {
                videoAnchorUtc = AsUtc(videoAnchorUtc.Value);
            }

            // A stale selected anchor (before this capture session) can't anchor the clip; the timing
            // math falls back to observation-anchored footage so every unlock still gets a clip.
            if (videoAnchorUtc.HasValue && videoAnchorUtc.Value < session.CaptureStartUtc.AddSeconds(-60))
            {
                _logger?.Debug(
                    $"[Recording] Unlock '{e.DisplayName}' has a pre-session video anchor ({videoAnchorUtc.Value:u}); clip will anchor on observation time.");
            }

            var request = new ClipRequest
            {
                Session = session,
                CaptureCorrelationId = e.CaptureCorrelationId,
                ProviderKey = e.ProviderKey,
                GameName = e.GameName,
                // Resolved through the shared helper so completion notifications (no
                // DisplayName) get the same name the toast wave reports, letting the clip
                // match its wave's overlay track and carry a sensible filename.
                AchievementName = ViewModels.AchievementToastViewModel.ResolveAchievementName(e),
                AchievementNumber = e.AchievementNumber,
                TotalCount = e.TotalCount,
                ReportedUnlockUtc = e.UnlockTimeUtc,
                VideoAnchorUtc = videoAnchorUtc,
                VideoAnchorSource = e.VideoAnchorSource,
                ObservedUtc = observedUtc,
                IsTestFire = e.IsTestFire,
                EffectiveToastSeconds = _toastNotifications?.GetEffectiveToastDurationSecondsSafe()
                    ?? Math.Max(2, persisted.ToastDurationSeconds),
                TrackTcs = new TaskCompletionSource<ToastOverlayTrack>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };

            lock (_gate)
            {
                _awaitingTrack.Add(request);
            }

            // Production starts immediately: the clip window is unlock-anchored, so nothing about
            // it depends on when (or whether) the toast displays. Only the overlay composite waits
            // for the track, after the toastless base clip is already safe.
            StartClipProduction(request);
        }

        /// <summary>
        /// A wave settling proves the toast queue is draining; bump the activity clock so requests
        /// queued behind long waves keep waiting for their own track instead of timing out (track
        /// completions alone can be a full display duration apart). Also stamps the wave's chime
        /// time on its still-waiting requests so the re-encode can read the chime from the sidecar
        /// track — an unrevealed wave reports no chime time, so its clips are mixed without one.
        /// </summary>
        private void OnToastWaveDisplayed(object sender, ToastWaveDisplayedEventArgs e)
        {
            if (_disposed || e?.Wave == null || e.Wave.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                _lastToastActivityUtc = CaptureTimelineClock.UtcNow;
                if (e.SoundPlayedUtc.HasValue)
                {
                    foreach (var vm in e.Wave)
                    {
                        var match = _awaitingTrack.FirstOrDefault(r =>
                            !r.OwnSoundUtc.HasValue &&
                            r.CaptureCorrelationId == vm.CaptureCorrelationId);
                        if (match != null)
                        {
                            match.OwnSoundUtc = e.SoundPlayedUtc;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Hands each completed overlay track to its correlation-id request. Unmatched tracks are
        /// from items that toasted but requested no clip.
        /// </summary>
        private void OnToastTracksCompleted(object sender, ToastTracksCompletedEventArgs e)
        {
            if (_disposed || e?.Tracks == null || e.Tracks.Count == 0)
            {
                return;
            }

            var matches = new List<(ClipRequest Request, ToastOverlayTrack Track)>();
            lock (_gate)
            {
                _lastToastActivityUtc = CaptureTimelineClock.UtcNow;
                foreach (var track in e.Tracks)
                {
                    var match = _awaitingTrack.FirstOrDefault(r =>
                        r.CaptureCorrelationId == track.CaptureCorrelationId);
                    if (match != null)
                    {
                        _awaitingTrack.Remove(match);
                        matches.Add((match, track));
                    }
                }
            }

            foreach (var (request, track) in matches)
            {
                request.TrackTcs?.TrySetResult(track);
            }
        }

        // === Clip production ===

        private void StartClipProduction(ClipRequest request)
        {
            var task = Task.Run(() => ProduceClipAsync(request));
            lock (_gate)
            {
                _inFlightTasks.Add(task);
            }

            task.ContinueWith(
                t =>
                {
                    // Surface a faulted producer (e.g. a native corrupted-state exception from the
                    // Media Foundation exporter that ProduceClipAsync's managed catch never sees).
                    if (t.IsFaulted)
                    {
                        _logger?.Warn(t.Exception, $"[Recording] Clip production task faulted for '{request?.AchievementName}'.");
                    }

                    lock (_gate)
                    {
                        _inFlightTasks.Remove(t);
                    }
                },
                TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// The full per-request pipeline, base-first: compute the unlock-anchored window, extract
        /// the toastless base clip from the buffer (after which the segments are prune-safe and
        /// the clip can no longer be lost), then wait for this achievement's overlay track and
        /// re-encode the toast in. Track missing or re-encode failed → the toastless base is
        /// saved instead.
        /// </summary>
        private async Task ProduceClipAsync(ClipRequest request)
        {
            try
            {
                var session = request.Session;
                var persisted = _settings?.Persisted;
                if (persisted == null)
                {
                    AbandonTrackWait(request);
                    return;
                }

                var pollInterval = Math.Max(10, persisted.InGamePollIntervalSeconds);
                var toastSlotSeconds = request.EffectiveToastSeconds + SlideAllowanceSeconds;
                var window = SegmentTimeline.ComputeClipWindow(
                    request.VideoAnchorUtc,
                    request.ObservedUtc,
                    session.CaptureStartUtc,
                    oldestSegmentStartUtc: null,
                    pollIntervalSeconds: pollInterval,
                    preRollSeconds: persisted.RecordingClipSeconds,
                    toastSlotSeconds: toastSlotSeconds,
                    tailSeconds: ToastTailSeconds);

                if ((window.EndUtc - window.StartUtc).TotalSeconds < SegmentTimeline.MinimumWindowSeconds)
                {
                    _logger?.Debug(
                        $"[Recording] Clip window for '{request.AchievementName}' collapsed below {SegmentTimeline.MinimumWindowSeconds}s; skipping.");
                    AbandonTrackWait(request);
                    return;
                }

                var outputPath = BuildOutputPath(persisted, request);
                if (outputPath == null)
                {
                    AbandonTrackWait(request);
                    return;
                }

                // Base extraction: prune suspension covers only this span — once the base exists,
                // the buffer no longer owes this clip anything, even if its toast is queued far
                // behind other waves.
                string basePath;
                double videoLeadSeconds;
                DateTime clipStartUtc;
                lock (_outstandingGate)
                {
                    _outstandingWindowStarts.Add(window.StartUtc);
                }

                try
                {
                    (basePath, videoLeadSeconds, clipStartUtc) = await ExportBaseClipAsync(session, request, window)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_outstandingGate)
                    {
                        _outstandingWindowStarts.Remove(window.StartUtc);
                    }
                }

                if (basePath == null)
                {
                    AbandonTrackWait(request);
                    return;
                }

                try
                {
                    var track = await WaitForTrackAsync(request).ConfigureAwait(false);
                    var finalPath = basePath;
                    if (track != null)
                    {
                        var composited = await ReencodeWithTrackAsync(
                                session, request, basePath, track, window, toastSlotSeconds, videoLeadSeconds,
                                clipStartUtc)
                            .ConfigureAwait(false);
                        if (composited != null)
                        {
                            finalPath = composited;
                        }
                        else
                        {
                            _logger?.Warn(
                                $"[Recording] Toast composite failed for '{request.AchievementName}'; saving the clip without a toast.");
                        }
                    }

                    var savedPath = SaveClipToUniquePath(finalPath, outputPath, copy: false);
                    if (savedPath == null)
                    {
                        _logger?.Warn($"[Recording] Could not place unlock clip for '{request.AchievementName}' (destination in use).");
                        TryDeleteFile(finalPath);
                        return;
                    }

                    _logger?.Info($"[Recording] Saved unlock clip: {savedPath}");
                    // Drop the cached capture scan for this game. This also raises CapturesChanged,
                    // so grids that are already open re-stamp their rows for the new clip.
                    PlayniteAchievementsPlugin.Instance?.CaptureLibraryService?.Invalidate(request.GameName);
                }
                finally
                {
                    TryDeleteFile(basePath);
                }
            }
            catch (Exception ex)
            {
                AbandonTrackWait(request);
                _logger?.Debug(ex, $"[Recording] Clip production failed for '{request?.AchievementName}'.");
            }
        }

        /// <summary>
        /// Removes the request from the track-wait list and resolves its waiter null, so an
        /// abandoned production can't strand the wave matcher or a later WaitForTrackAsync.
        /// </summary>
        private void AbandonTrackWait(ClipRequest request)
        {
            if (request == null)
            {
                return;
            }

            lock (_gate)
            {
                _awaitingTrack.Remove(request);
            }

            request.TrackTcs?.TrySetResult(null);
        }

        /// <summary>
        /// Waits for this achievement's overlay track, giving up (null → toastless clip) only
        /// after <see cref="ToastWaitTimeoutSeconds"/> of toast SILENCE — measured from the last
        /// wave shown or track completed, not from detection — so a toast queued minutes behind
        /// other waves still gets composited. Returns whatever won a give-up/late-track race.
        /// </summary>
        private async Task<ToastOverlayTrack> WaitForTrackAsync(ClipRequest request)
        {
            while (true)
            {
                var completed = await Task.WhenAny(
                        request.TrackTcs.Task,
                        Task.Delay(TimeSpan.FromSeconds(ToastWaitPollSeconds)))
                    .ConfigureAwait(false);
                if (completed == request.TrackTcs.Task)
                {
                    return await request.TrackTcs.Task.ConfigureAwait(false);
                }

                DateTime lastActivity;
                lock (_gate)
                {
                    lastActivity = _lastToastActivityUtc;
                }

                var now = CaptureTimelineClock.UtcNow;
                var silenceAnchor = lastActivity > request.ObservedUtc ? lastActivity : request.ObservedUtc;
                if (_disposed ||
                    now - silenceAnchor >= TimeSpan.FromSeconds(ToastWaitTimeoutSeconds) ||
                    now - request.ObservedUtc >= TimeSpan.FromSeconds(MaxToastWaitSeconds))
                {
                    _logger?.Debug(
                        $"[Recording] No matching toast track for '{request.AchievementName}' " +
                        $"({(now - request.ObservedUtc).TotalSeconds:F0}s since observation); saving the clip without a toast.");
                    AbandonTrackWait(request);
                    return await request.TrackTcs.Task.ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Re-encodes the base clip with the overlay track composited in, one at a time across
        /// the service. The output is cut shortly after the recorded fade — the base window is
        /// sized for the worst case before the track exists, and running it out would put the
        /// next wave's unlock sound in the audio tail. Returns the composited temp path, or null
        /// on failure (base clip stands).
        /// </summary>
        private async Task<string> ReencodeWithTrackAsync(
            CaptureSession session, ClipRequest request, string basePath, ToastOverlayTrack track,
            SegmentTimeline.ClipWindow window, double toastSlotSeconds, double videoLeadSeconds,
            DateTime clipStartUtc)
        {
            // Toast position within the BASE clip's timeline: the base starts `videoLeadSeconds`
            // before the clip's own start (keyframe snap), and the overlay sits inside the window.
            //
            // Measured from where the clip actually begins, not from where the window wanted to begin.
            // The two differ whenever the buffer could not reach back the full pre-roll, and measuring
            // from the window then put the card that much too early against the footage.
            //
            // The card sits on the unlock itself, not on the moment the real notification reached the
            // screen. Those are far apart: a provider poll takes seconds to notice an unlock, so the
            // notification appeared 9.2s after the fact in one measured case. A clip is built around the
            // unlock — the pre-roll leads up to it and the tail follows it — so that is where the card
            // belongs, and placing it there means the clip shows the achievement popping at the instant it
            // was earned.
            //
            // The track's own first-rendered-frame stamp is deliberately not used for placement. It is
            // still what the card's animation plays from, so the composited card slides in exactly as it
            // did live; only its position in the clip comes from the unlock.
            var overlaySeconds = Math.Min(toastSlotSeconds, track.DurationSeconds) + PostFadeTailSeconds;
            var overlayStartUtc = window.ToastAnchorUtc;

            var clipOriginUtc = clipStartUtc == default(DateTime) ? window.StartUtc : clipStartUtc;
            var toastStartSeconds = videoLeadSeconds + (overlayStartUtc - clipOriginUtc).TotalSeconds;
            var endSeconds = toastStartSeconds + overlaySeconds;

            // Where the card landed, and how far the real notification was from it — the gap is the
            // provider's detection lag, and seeing it beside the placement makes an odd-looking clip
            // readable without reasoning backwards from the window.
            _logger?.Info(
                $"[RecordingTiming] toast placed at {toastStartSeconds.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"on the unlock ({Stamp(window.ToastAnchorUtc)}); the notification itself appeared " +
                $"{(track.StartUtc - window.ToastAnchorUtc).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s later " +
                $"({Stamp(track.StartUtc)}). lead={videoLeadSeconds.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"end={endSeconds.ToString("F2", CultureInfo.InvariantCulture)}s");
            // The wave's own chime, read from the Playnite-only sidecar at its real time, mixed
            // in slightly before the composited toast (matching the live sound-to-reveal lead).
            var chimePcm = await TryReadChimePcmAsync(session, request).ConfigureAwait(false);
            var chimeStartSeconds = toastStartSeconds - ChimeLeadBeforeToastSeconds;
            var tempPath = Path.Combine(session.BufferDirectory, $"clipovl_{Guid.NewGuid():N}.mp4");
            await _reencodeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var reencoder = new MediaFoundationOverlayReencoder(_logger);
                // The rate the segments were captured at, so a base clip whose media type declares no
                // frame rate is re-encoded as what it actually is. Falls back to the setting's own
                // default, which is what a capture with unreachable settings would have used.
                var capturedFps = _settings?.Persisted?.RecordingFps ?? DefaultRecordingFps;
                // Re-encode at the quality the segments were captured at, so compositing the toast
                // does not quietly change the clip's bitrate.
                var capturedQuality = _settings?.Persisted?.RecordingQuality ?? RecordingQuality.Native;
                var ok = await Task.Run(() => reencoder.Export(
                        basePath, track, toastStartSeconds, toastSlotSeconds, videoLeadSeconds,
                        endSeconds, chimePcm, chimeStartSeconds, tempPath, capturedFps, capturedQuality))
                    .ConfigureAwait(false);
                if (ok)
                {
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Overlay re-encode task failed.");
            }
            finally
            {
                _reencodeGate.Release();
            }

            TryDeleteFile(tempPath);
            return null;
        }

        /// <summary>
        /// Moves (or copies) the produced clip to a unique path under <paramref name="desiredPath"/>,
        /// re-resolving uniqueness immediately before each attempt and retrying on a collision.
        /// <see cref="BuildOutputPath"/> resolves a unique name when the clip is requested, but the
        /// write happens much later — concurrent productions (e.g. a rapid test-fire burst) can each
        /// resolve the same free name before either writes it, so File.Move/Copy would throw
        /// "file already exists". Returns the final path, or null if it can't be placed.
        /// </summary>
        private static string SaveClipToUniquePath(string sourcePath, string desiredPath, bool copy)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var candidate = UnlockScreenshotService.EnsureUniquePath(desiredPath);
                try
                {
                    if (copy)
                    {
                        File.Copy(sourcePath, candidate);
                    }
                    else
                    {
                        File.Move(sourcePath, candidate);
                    }

                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Another clip production won the race for this name; resolve a fresh one.
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts the toastless base clip: waits for the segment covering the window end to
        /// close, plans video + audio over the window, and stream-copy exports to a temp file in
        /// the buffer directory. Returns the temp path plus the keyframe lead (seconds the base
        /// starts before the window; the re-encode trims it back off), or (null, 0) on failure.
        /// </summary>
        private async Task<(string TempPath, double VideoLeadSeconds, DateTime ClipStartUtc)> ExportBaseClipAsync(
            CaptureSession session,
            ClipRequest request,
            SegmentTimeline.ClipWindow window)
        {
            // Wait until the segment covering the window end has closed (K + margin) so the
            // concat never reads a half-written segment.
            var readyAtUtc = window.EndUtc.AddSeconds(SegmentSeconds + 2);
            var wait = readyAtUtc - CaptureTimelineClock.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait).ConfigureAwait(false);
            }

            var segments = SegmentTimeline.ParseSegments(
                ListBufferFiles(
                    session.BufferDirectory,
                    RecordingPaths.SegmentFilePrefix,
                    session.SegmentExtension),
                TimeZoneInfo.Local,
                RecordingPaths.SegmentFilePrefix,
                session.SegmentExtension);
            // The anchor keeps the unlock itself in frame if a mid-session capture rebuild splits
            // the window into runs of differing dimensions; the plan then covers only that run.
            var plan = SegmentTimeline.PlanClip(
                segments, window.StartUtc, window.EndUtc, SegmentSeconds, window.ToastAnchorUtc);
            if (plan == null)
            {
                _logger?.Debug($"[Recording] No buffered segments overlap the clip window for '{request.AchievementName}'; skipping.");
                return (null, 0, default(DateTime));
            }

            if (plan.TruncatedByResize)
            {
                _logger?.Info(
                    $"[Recording] Clip window for '{request.AchievementName}' spans a capture resize; " +
                    $"keeping the {plan.Segments.Count} segment(s) around the unlock " +
                    $"({plan.DurationSeconds:0.0}s at {plan.Segments[0].Width}x{plan.Segments[0].Height}).");
            }

            // Audio rides the same window: plan the loopback WAV chunks over it and fall back to
            // video-only whenever the recorder never ran or no chunk overlaps. Clamped to the
            // video's end so a resize-shortened clip never carries an audio tail past its picture.
            SegmentTimeline.ClipPlan audioPlan = null;
            if (session.AudioRecorder != null)
            {
                var audioChunks = SegmentTimeline.ParseSegments(
                    ListBufferFiles(
                        session.BufferDirectory,
                        RecordingPaths.AudioChunkFilePrefix,
                        RecordingPaths.AudioChunkFileExtension),
                    TimeZoneInfo.Local,
                    RecordingPaths.AudioChunkFilePrefix,
                    RecordingPaths.AudioChunkFileExtension);
                audioPlan = SegmentTimeline.PlanClip(audioChunks, window.StartUtc, plan.EndUtc, SegmentSeconds);
            }

            // Remove any controller haptic waveform the process-loopback capture swept up along with
            // the game's audio. Replaces the plan with one over a single cleaned chunk, so the
            // exporter's concatenation and A/V alignment are untouched either way.
            var cleanedAudioDirectory = (string)null;
            if (audioPlan != null)
            {
                audioPlan = TryRemoveHapticAudio(session, audioPlan, out cleanedAudioDirectory);
            }

            LogRecordingTiming(session, request, window, plan.Segments.Count, audioPlan != null);

            var tempPath = Path.Combine(session.BufferDirectory, $"clip_{Guid.NewGuid():N}.mp4");
            // Concatenate + trim the buffered segments and mux the loopback audio with Media
            // Foundation (stream-copy video, PCM->AAC audio). WGC already captures the client
            // area at the target resolution, so no crop is needed here; the toast composite (if
            // any) re-encodes in a separate pass.
            var exporter = new MediaFoundationClipExporter(_logger);
            double videoLeadSeconds = 0;
            bool ok;
            await _baseExportGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ok = await Task.Run(() => exporter.Export(plan, audioPlan, tempPath, out videoLeadSeconds))
                    .ConfigureAwait(false);
            }
            finally
            {
                _baseExportGate.Release();
                TryDeleteCleanedAudio(cleanedAudioDirectory);
            }

            if (!ok)
            {
                _logger?.Warn($"[Recording] Clip export failed for '{request.AchievementName}'.");
                TryDeleteFile(tempPath);
                return (null, 0, default(DateTime));
            }

            // The instant the finished clip actually begins. PlanClip starts at the later of the window
            // start and the oldest segment it can use, so a buffer that does not reach back far enough —
            // a young session, a pruned buffer, a run cut short by a resize — makes the clip begin after
            // the window did. Anything positioned inside the clip has to measure from here rather than
            // from the window, or it lands early by the difference.
            var clipStartUtc = plan.Segments[0].StartUtc.AddSeconds(plan.StartOffsetSeconds);
            if (clipStartUtc > window.StartUtc.AddMilliseconds(250))
            {
                _logger?.Info(
                    $"[RecordingTiming] clip begins {(clipStartUtc - window.StartUtc).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s " +
                    $"after the window start — the buffer reached back only to {Stamp(plan.Segments[0].StartUtc)}; " +
                    "positions inside the clip are measured from the clip's own start.");
            }

            return (tempPath, videoLeadSeconds, clipStartUtc);
        }

        /// <summary>
        /// Reads this request's chime from the Playnite-tree sidecar chunks at the moment its wave
        /// sound actually played. When the game is a Playnite descendant, its same-time game-only
        /// reference is aligned and cancelled first; this is what prevents the sidecar from adding
        /// a delayed second copy of emulator audio at the composited toast.
        ///
        /// Waits for the chunk covering the end of the chime window to close first, the same way
        /// the base clip waits for its last video segment. A chunk still being written carries
        /// placeholder RIFF sizes, and Media Foundation rejects that outright
        /// (MF_E_UNSUPPORTED_BYTESTREAM_TYPE) — the chime window ends only a few seconds after the
        /// toast fires, so without the wait the newest chunk is essentially always mid-write and
        /// every clip silently lost its chime.
        /// </summary>
        private async Task<byte[]> TryReadChimePcmAsync(CaptureSession session, ClipRequest request)
        {
            DateTime? ownSound;
            lock (_gate)
            {
                ownSound = request.OwnSoundUtc;
            }

            if (!ownSound.HasValue || session.ChimeRecorder == null)
            {
                return null;
            }

            var chimeWindowEndUtc = ownSound.Value.AddSeconds(
                Math.Min(request.EffectiveToastSeconds, ChimeMaxSliceSeconds) + ChimeTailBeyondToastSeconds);
            var wait = chimeWindowEndUtc.AddSeconds(SegmentSeconds + 2) - CaptureTimelineClock.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait).ConfigureAwait(false);
            }

            var pcm = TryReadAudioWindow(
                session.BufferDirectory,
                RecordingPaths.ChimeChunkFilePrefix,
                ownSound.Value,
                chimeWindowEndUtc);
            if (pcm != null &&
                session.AudioRecorder?.ChimeCaptureMode == PlayniteChimeCaptureMode.CancelGameReference)
            {
                var referencePcm = TryReadAudioWindow(
                    session.BufferDirectory,
                    RecordingPaths.GameReferenceChunkFilePrefix,
                    ownSound.Value,
                    chimeWindowEndUtc);

                if (referencePcm == null)
                {
                    _logger?.Warn(
                        "[Recording] Chime sidecar could not be separated from the game reference; " +
                        "the clip keeps its game audio without a re-timed chime.");
                    return null;
                }

                var outcome = PcmAudio.CancelCorrelated(pcm, referencePcm, out var cancellation);
                if (outcome == PcmCancellationOutcome.Unseparable)
                {
                    _logger?.Warn(
                        "[Recording] Chime sidecar could not be verifiably separated from the game " +
                        $"reference (correlation={cancellation.Correlation:0.000} " +
                        $"gain={cancellation.Gain:0.00} suppression={cancellation.SuppressionDb:0.0}dB); " +
                        "the clip is mixed without a re-timed chime.");
                    return null;
                }

                _logger?.Debug(
                    $"[Recording] Chime game-audio cancellation: outcome={outcome} " +
                    $"lag={cancellation.StartLagMs:0.###}->{cancellation.EndLagMs:0.###}ms " +
                    $"gain={cancellation.Gain:0.00} correlation={cancellation.Correlation:0.000} " +
                    $"suppression={cancellation.SuppressionDb:0.0}dB mutedBlocks={cancellation.MutedBlocks}.");
            }

            if (pcm != null)
            {
                PcmAudio.FadeOutTail(pcm, ChimeFadeOutSeconds);
            }

            return pcm;
        }

        /// <summary>
        /// Removes a controller's haptic waveform from the clip's own audio, returning the plan the
        /// exporter should use: one over a single cleaned chunk when the removal was verified, and
        /// the original plan in every other case.
        /// <para>
        /// A DualSense plays haptics as audio through its own render endpoint, and process loopback
        /// mixes every endpoint the game renders to, so the buzz is inside <c>aud_</c>. The recorder
        /// captures that endpoint into <c>hap_</c> over the same paced writes; cancelling the one
        /// from the other is the only way to separate them, because Windows offers no way to scope a
        /// process capture to a device.
        /// </para>
        /// <para>
        /// Fail-open throughout: no reference, no verified subtraction, or any error at all leaves
        /// the recorded audio exactly as captured. Unverified blocks are kept rather than silenced —
        /// a hole in the clip's own audio is worse than a little residual buzz.
        /// </para>
        /// </summary>
        private SegmentTimeline.ClipPlan TryRemoveHapticAudio(
            CaptureSession session,
            SegmentTimeline.ClipPlan audioPlan,
            out string cleanedDirectory)
        {
            cleanedDirectory = null;
            if (audioPlan?.Segments == null || audioPlan.Segments.Count == 0)
            {
                return audioPlan;
            }

            try
            {
                var startUtc = audioPlan.Segments[0].StartUtc.AddSeconds(audioPlan.StartOffsetSeconds);
                var endUtc = startUtc.AddSeconds(audioPlan.DurationSeconds);
                var reference = TryReadAudioWindow(
                    session.BufferDirectory,
                    RecordingPaths.HapticReferenceChunkFilePrefix,
                    startUtc,
                    endUtc);
                if (reference == null)
                {
                    // No controller endpoint existed while this window was recorded. Said out loud:
                    // silence here reads exactly like a cancellation that ran and did nothing, and
                    // the two have completely different causes.
                    _logger?.Debug(
                        "[Recording] No haptic reference track covers this clip's audio window; " +
                        "nothing to remove (see the render-endpoint line from capture start).");
                    return audioPlan;
                }

                var mixture = TryReadAudioWindow(
                    session.BufferDirectory,
                    RecordingPaths.AudioChunkFilePrefix,
                    startUtc,
                    endUtc);
                if (mixture == null)
                {
                    return audioPlan;
                }

                var outcome = PcmAudio.CancelCorrelated(
                    mixture, reference, out var cancellation, muteUnverifiedBlocks: false);
                _logger?.Debug(
                    $"[Recording] Haptic cancellation: outcome={outcome} " +
                    $"lag={cancellation.StartLagMs:0.###}->{cancellation.EndLagMs:0.###}ms " +
                    $"gain={cancellation.Gain:0.00} correlation={cancellation.Correlation:0.000} " +
                    $"suppression={cancellation.SuppressionDb:0.0}dB.");
                if (outcome != PcmCancellationOutcome.CancelledVerified)
                {
                    return audioPlan;
                }

                var directory = Path.Combine(session.BufferDirectory, $"clean_{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                var name = RecordingPaths.BuildAudioChunkFileName(
                    RecordingPaths.AudioChunkFilePrefix, startUtc);
                PcmAudio.WriteWav(Path.Combine(directory, name), mixture);

                var cleanedChunks = SegmentTimeline.ParseSegments(
                    ListBufferFiles(
                        directory,
                        RecordingPaths.AudioChunkFilePrefix,
                        RecordingPaths.AudioChunkFileExtension),
                    TimeZoneInfo.Local,
                    RecordingPaths.AudioChunkFilePrefix,
                    RecordingPaths.AudioChunkFileExtension);

                // The cleaned window is one chunk spanning the whole clip, so the implied chunk
                // length has to cover it rather than the recorder's rotation interval.
                var cleanedPlan = SegmentTimeline.PlanClip(
                    cleanedChunks,
                    startUtc,
                    endUtc,
                    Math.Max(SegmentSeconds, (int)Math.Ceiling(audioPlan.DurationSeconds) + 1));
                if (cleanedPlan == null)
                {
                    TryDeleteCleanedAudio(directory);
                    return audioPlan;
                }

                cleanedDirectory = directory;
                return cleanedPlan;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Haptic audio could not be removed; the clip keeps its recorded audio.");
                return audioPlan;
            }
        }

        /// <summary>Removes the temporary cleaned-audio chunk once the exporter has read it.</summary>
        private void TryDeleteCleanedAudio(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] A cleaned-audio temp directory could not be removed.");
            }
        }

        private byte[] TryReadAudioWindow(
            string bufferDirectory,
            string prefix,
            DateTime startUtc,
            DateTime endUtc)
        {
            var chunks = SegmentTimeline.ParseSegments(
                ListBufferFiles(
                    bufferDirectory,
                    prefix,
                    RecordingPaths.AudioChunkFileExtension),
                TimeZoneInfo.Local,
                prefix,
                RecordingPaths.AudioChunkFileExtension);
            var plan = SegmentTimeline.PlanClip(chunks, startUtc, endUtc, SegmentSeconds);
            return plan == null
                ? null
                : MediaFoundationClipExporter.TryReadPcmWindow(
                    plan, startUtc, endUtc, _logger);
        }

        /// <summary>
        /// The per-clip timing line (Info) that makes refresh-latency-driven clip anchoring
        /// visible in the plugin log.
        /// </summary>
        private void LogRecordingTiming(
            CaptureSession session,
            ClipRequest request,
            SegmentTimeline.ClipWindow window,
            int segmentCount,
            bool hasAudio)
        {
            try
            {
                var reportedText = request.ReportedUnlockUtc.HasValue
                    ? Stamp(AsUtc(request.ReportedUnlockUtc.Value))
                    : "none";
                var reportedToObserved = request.ReportedUnlockUtc.HasValue
                    ? (request.ObservedUtc - AsUtc(request.ReportedUnlockUtc.Value)).TotalSeconds
                        .ToString("F1", CultureInfo.InvariantCulture)
                    : "?";
                var selectedAnchorText = request.VideoAnchorUtc.HasValue
                    ? Stamp(request.VideoAnchorUtc.Value)
                    : "none";
                _logger?.Info(
                    $"[RecordingTiming] reported={reportedText} observed={Stamp(request.ObservedUtc)} " +
                    $"(reported→observed {reportedToObserved}s) selected={selectedAnchorText} " +
                    $"source={request.VideoAnchorSource} toastAnchor={Stamp(window.ToastAnchorUtc)} " +
                    $"window=[{Stamp(window.StartUtc)}..{Stamp(window.EndUtc)}] ({(window.EndUtc - window.StartUtc).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s) " +
                    $"segments={segmentCount} audio={(hasAudio ? "yes" : "no")}");
            }
            catch
            {
            }
        }

        private static string Stamp(DateTime utc)
        {
            return utc.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
        }

        private static DateTime AsUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            return value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private string BuildOutputPath(PersistedSettings persisted, ClipRequest request)
        {
            try
            {
                var baseDir = ResolveOutputDirectory(persisted);
                if (string.IsNullOrWhiteSpace(baseDir))
                {
                    return null;
                }

                // A manual test fire lands in a separate "Test" subfolder, matching the screenshot
                // planner, so test clips never mix with a game's genuine unlock captures.
                if (request.IsTestFire)
                {
                    baseDir = Path.Combine(baseDir, UnlockScreenshotService.TestFolderName);
                }

                var relative = UnlockScreenshotService.BuildRelativePath(
                    request.ProviderKey,
                    request.GameName,
                    request.AchievementName,
                    request.AchievementNumber,
                    request.TotalCount,
                    variantSuffix: null,
                    extension: ".mp4");
                var folder = Path.Combine(baseDir, relative.Folder);
                Directory.CreateDirectory(folder);
                return UnlockScreenshotService.EnsureUniquePath(Path.Combine(folder, relative.FileName));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Failed to build clip output path.");
                return null;
            }
        }

        // === Buffer maintenance ===

        /// <summary>
        /// Every 30s: prunes segments beyond the rolling depth/byte cap and stops the capture
        /// when the buffer drive drops below 500 MB free.
        /// </summary>
        private void PruneTick(CaptureSession session)
        {
            if (_disposed || session.Stopping)
            {
                return;
            }

            try
            {
                var persisted = _settings?.Persisted;
                var preRoll = persisted?.RecordingClipSeconds ?? DefaultPreRollSeconds;

                var segments = SegmentTimeline.ParseSegments(
                    ListBufferFiles(
                        session.BufferDirectory,
                        RecordingPaths.SegmentFilePrefix,
                        session.SegmentExtension),
                    TimeZoneInfo.Local,
                    RecordingPaths.SegmentFilePrefix,
                    session.SegmentExtension);

                // Audio rides the same retention span as the video: a clip needs picture and sound
                // over one window, and both count against the user's budget, so the cutoff is
                // resolved once over every file in the buffer.
                var audioByPrefix = new Dictionary<string, List<SegmentTimeline.SegmentInfo>>();
                foreach (var prefix in new[]
                {
                    RecordingPaths.AudioChunkFilePrefix,
                    RecordingPaths.ChimeChunkFilePrefix,
                    RecordingPaths.GameReferenceChunkFilePrefix,
                    RecordingPaths.HapticReferenceChunkFilePrefix
                })
                {
                    audioByPrefix[prefix] = SegmentTimeline.ParseSegments(
                        ListBufferFiles(
                            session.BufferDirectory,
                            prefix,
                            RecordingPaths.AudioChunkFileExtension),
                        TimeZoneInfo.Local,
                        prefix,
                        RecordingPaths.AudioChunkFileExtension);
                }

                var allFiles = new List<SegmentTimeline.SegmentInfo>(segments);
                foreach (var chunks in audioByPrefix.Values)
                {
                    allFiles.AddRange(chunks);
                }

                var cutoff = SegmentTimeline.ResolveBudgetCutoffUtc(
                    allFiles,
                    ResolveBufferBudgetBytes(session),
                    ResolveMinimumKeepFromUtc(preRoll));

                LogCaptureHealth(session, segments, allFiles, cutoff);
                foreach (var segment in SegmentTimeline.SelectPrunable(segments, cutoff))
                {
                    TryDeleteFile(segment.Path);
                }

                foreach (var pair in audioByPrefix)
                {
                    foreach (var chunk in SegmentTimeline.SelectPrunable(pair.Value, cutoff))
                    {
                        TryDeleteFile(chunk.Path);
                    }
                }

                if (!HasFreeSpace(session.BufferDirectory, MinFreeBytesToContinue))
                {
                    _logger?.Warn("[Recording] Less than 500 MB free on the buffer drive; stopping the capture for this session.");
                    session.Stopping = true;
                    session.PruneTimer?.Dispose();
                    session.PruneTimer = null;
                    NotifyRecordingUnavailableOnce();
                    session.WgcRecorder?.Stop();
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Prune tick failed.");
            }
        }

        /// <summary>
        /// The buffer's storage budget in bytes, clamped down so it can never exceed what the drive
        /// can actually give (leaving the stop-capture reserve free). Logged once per session when
        /// the clamp bites, since the buffer then reaches back less far than <see cref="BufferBudgetBytes"/>.
        /// </summary>
        private long ResolveBufferBudgetBytes(CaptureSession session)
        {
            const long requested = BufferBudgetBytes;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(session.BufferDirectory));
                if (string.IsNullOrEmpty(root))
                {
                    return requested;
                }

                // What the buffer already occupies is available to it, so the headroom is the free
                // space plus the current buffer, less the reserve that stops capture outright.
                var free = new DriveInfo(root).AvailableFreeSpace;
                var affordable = free + session.LastKnownBufferBytes - MinFreeBytesToContinue;
                if (affordable >= requested || affordable <= 0)
                {
                    return requested;
                }

                if (!session.BufferBudgetClampLogged)
                {
                    session.BufferBudgetClampLogged = true;
                    _logger?.Warn(
                        $"[Recording] Buffer budget reduced from {requested / (1024 * 1024)}MB to " +
                        $"{affordable / (1024 * 1024)}MB: not enough free space on the buffer drive.");
                }

                return affordable;
            }
            catch (Exception ex)
            {
                // Unknown drives (UNC quirks) fail open, matching HasFreeSpace.
                _logger?.Debug(ex, "[Recording] Buffer budget free-space clamp failed.");
                return requested;
            }
        }

        /// <summary>
        /// The newest moment the pruner may cut back to, whatever the budget says. Covers one clip
        /// window (pre-roll plus the toast slot and tail, and a segment of slack), and reaches
        /// further back while clip requests are still between window computation and base
        /// extraction — those clips read the buffer, so their footage must survive even if the
        /// budget is exceeded.
        /// </summary>
        private DateTime ResolveMinimumKeepFromUtc(int preRoll)
        {
            var floor = CaptureTimelineClock.UtcNow.AddSeconds(
                -(preRoll + MaxToastSlotAllowanceSeconds + ToastTailSeconds + SegmentSeconds));

            DateTime? oldestOutstanding;
            lock (_outstandingGate)
            {
                oldestOutstanding = _outstandingWindowStarts.Count == 0
                    ? (DateTime?)null
                    : _outstandingWindowStarts.Min();
            }

            return oldestOutstanding.HasValue && oldestOutstanding.Value < floor
                ? oldestOutstanding.Value
                : floor;
        }

        /// <summary>
        /// Diagnostic only: a per-prune-tick capture-health line. Warns when the recorder has stopped
        /// opening new segments — a stalled capture that leaves an unlock with no footage ("no
        /// buffered segments overlap the clip window"). The WGC recorder duplicates the last frame at
        /// a constant rate, so segments should always advance; a stall here means the recorder itself
        /// wedged. Never throws.
        /// </summary>
        private void LogCaptureHealth(
            CaptureSession session,
            IReadOnlyList<SegmentTimeline.SegmentInfo> segments,
            IReadOnlyList<SegmentTimeline.SegmentInfo> allFiles,
            DateTime cutoffUtc)
        {
            try
            {
                // Tracked even when the health line is skipped: the budget's free-space clamp reads
                // it to know how much of the drive the buffer already holds.
                session.LastKnownBufferBytes = allFiles?.Sum(file => Math.Max(0, file.SizeBytes)) ?? 0;

                if (session == null || session.Stopping || session.WgcRecorder == null)
                {
                    return;
                }

                var now = CaptureTimelineClock.UtcNow;
                if (segments == null || segments.Count == 0)
                {
                    if ((now - session.CaptureStartUtc).TotalSeconds > SegmentSeconds * 3)
                    {
                        _logger?.Warn(
                            $"[RecordingHealth] '{session.GameName}': capture alive but no segments on disk " +
                            $"{(now - session.CaptureStartUtc).TotalSeconds:F0}s after start.");
                    }

                    return;
                }

                var newest = segments[segments.Count - 1];
                if (!string.Equals(newest.Path, session.LastSegmentPath, StringComparison.OrdinalIgnoreCase))
                {
                    session.LastSegmentPath = newest.Path;
                    session.LastSegmentAdvanceUtc = now;
                }

                var sinceNewSegment = (now - session.LastSegmentAdvanceUtc).TotalSeconds;
                // The still-open newest segment grows as it records; the one before it is the most
                // recent closed segment and the fair size sample.
                var lastClosed = segments.Count >= 2 ? segments[segments.Count - 2] : null;
                if (lastClosed != null && lastClosed.SizeBytes > session.MaxSegmentBytes)
                {
                    session.MaxSegmentBytes = lastClosed.SizeBytes;
                }

                // The retained span is what a clip can actually reach back to, so it is the number
                // that explains "the unlock had no footage": compare it against anchor->observation.
                var oldestKept = segments[0].StartUtc > cutoffUtc ? segments[0].StartUtc : cutoffUtc;
                var line =
                    $"[RecordingHealth] '{session.GameName}': segments={segments.Count} " +
                    $"newestAge={(now - newest.StartUtc).TotalSeconds:F0}s sinceNewSegment={sinceNewSegment:F0}s " +
                    $"lastClosed={(lastClosed?.SizeBytes ?? 0) / 1024}KB peak={session.MaxSegmentBytes / 1024}KB " +
                    $"used={session.LastKnownBufferBytes / (1024 * 1024)}MB span={(now - oldestKept).TotalSeconds:F0}s";

                // A new segment should open every SegmentSeconds; several periods without one means
                // the capture has stalled.
                if (sinceNewSegment > SegmentSeconds * 3 + 2)
                {
                    _logger?.Warn(
                        $"{line} -- STALLED: no new segment for {sinceNewSegment:F0}s (capture wedged).");
                }
                else
                {
                    _logger?.Debug(line);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[RecordingHealth] Health check failed.");
            }
        }

        private static IEnumerable<(string Path, long SizeBytes)> ListBufferFiles(
            string bufferDirectory,
            string prefix,
            string extension)
        {
            var result = new List<(string, long)>();
            try
            {
                if (!Directory.Exists(bufferDirectory))
                {
                    return result;
                }

                foreach (var file in Directory.GetFiles(bufferDirectory, prefix + "*" + extension))
                {
                    long size = 0;
                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch
                    {
                    }

                    result.Add((file, size));
                }
            }
            catch
            {
            }

            return result;
        }

        /// <summary>
        /// Deletes leftover buffer directories from crashed sessions at game start. Directories
        /// owned by the current session or a previous session still draining its clips are kept.
        /// </summary>
        private void CleanupStaleBufferDirectories(string bufferRoot)
        {
            try
            {
                if (!Directory.Exists(bufferRoot))
                {
                    return;
                }

                foreach (var directory in Directory.GetDirectories(bufferRoot))
                {
                    lock (_gate)
                    {
                        if (_liveBufferDirs.Contains(directory))
                        {
                            continue;
                        }
                    }

                    _logger?.Debug($"[Recording] Removing stale recording buffer: {directory}");
                    TryDeleteDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Stale buffer cleanup failed.");
            }
        }

        // === Helpers ===

        private static string ResolveOutputDirectory(PersistedSettings persisted)
        {
            var directory = persisted?.UnlockRecordingDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = persisted?.UnlockScreenshotDirectory;
            }

            return string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        }

        private static bool ProcessHasMainWindow(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.MainWindowHandle != IntPtr.Zero;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool HasFreeSpace(string path, long minimumBytes)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root))
                {
                    return true;
                }

                return new DriveInfo(root).AvailableFreeSpace >= minimumBytes;
            }
            catch (Exception ex)
            {
                // Unknown drives (UNC quirks) fail open: recording is best-effort.
                _logger?.Debug(ex, "[Recording] Free-space check failed.");
                return true;
            }
        }

        private void NotifyRecordingUnavailableOnce(string stderrTail = null)
        {
            if (_sessionNotified)
            {
                return;
            }

            _sessionNotified = true;
            try
            {
                var title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
                var message = ResourceProvider.GetString("LOCPlayAch_Notification_RecordingUnavailable");
                // Append the ffmpeg stderr tail (the actual driver/encoder error) so the cause is
                // visible in the notification instead of only in the plugin log.
                if (!string.IsNullOrWhiteSpace(stderrTail))
                {
                    message = $"{message}\n{stderrTail.Trim()}";
                }

                _api?.Notifications?.Add(new NotificationMessage(
                    UnavailableNotificationId,
                    $"{title}\n{message}",
                    NotificationType.Error));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Failed to show the recording-unavailable notification.");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            PlayniteAchievementsPlugin.AchievementUnlocked -= OnAchievementUnlocked;
            if (_toastNotifications != null)
            {
                _toastNotifications.WaveDisplayed -= OnToastWaveDisplayed;
                _toastNotifications.TracksCompleted -= OnToastTracksCompleted;
            }

            if (_windowTracker != null)
            {
                _windowTracker.StableForegroundGameChanged -= OnStableForegroundGameChanged;
            }

            CaptureSession session;
            List<ClipRequest> awaiting;
            lock (_gate)
            {
                session = _session;
                _session = null;
                awaiting = _awaitingTrack.ToList();
                _awaitingTrack.Clear();
            }

            foreach (var request in awaiting)
            {
                request.TrackTcs?.TrySetResult(null);
            }

            if (session != null)
            {
                session.Stopping = true;
                try
                {
                    session.Cts.Cancel();
                }
                catch
                {
                }

                session.PruneTimer?.Dispose();
                session.WgcRecorder?.Dispose();
                session.AudioRecorder?.Dispose();
                session.ChimeRecorder?.Dispose();
            }
        }
    }
}
