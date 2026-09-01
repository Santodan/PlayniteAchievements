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
using PlayniteAchievements.Services.GameCustomData;
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
    /// A configured capture delay moves that anchor to the moment the capture was taken instead,
    /// so the clip and the screenshot depict the same frame; a configured notification delay
    /// likewise moves it, since the wave itself (and so the capture) is held past the unlock.
    /// Subscribes to <see cref="PlayniteAchievementsPlugin.AchievementUnlocked"/> in parallel to
    /// the toast service, and to <see cref="ToastNotificationService.TracksCompleted"/> for the
    /// overlay tracks (<see cref="ToastNotificationService.WaveDisplayed"/> is a liveness bump for
    /// the track wait, and carries the display instant the delayed path anchors to).
    /// The toastless base clip always exists before the re-encode runs,
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
        // the cancellation's drift tracker must cover.
        private const double ChimeTailBeyondToastSeconds = 0.5;
        private const double ChimeMaxSliceSeconds = 4.0;
        private const double ChimeFadeOutSeconds = 0.15;

        // How far the chime onset precedes the card in the clip. Measured per clip from the two
        // stamps that bracket it live -- when the sound fired, and when the card's first frame
        // rendered -- because the composited card plays its own recorded animation from its first
        // frame, so that frame is what the chime must lead.
        //
        // The constant this replaces was 0.75s, derived as the sound-align delay plus the slide-in
        // duration, i.e. the distance to the SETTLED card. Subtracting it from the animation start
        // double-counted the slide and put the chime a slide-length early; on the fast path, whose
        // sound-align delay is 150ms rather than 450ms, it was early by twice that again. Both are
        // themeable or version-dependent, so they are read rather than modelled.
        private const double ChimeLeadFallbackSeconds = 0.45;
        private const double ChimeLeadMaxSeconds = 2.0;

        /// <summary>
        /// Stands in for the volume UniPlaySong played the chime at when that volume cannot be
        /// read from its settings, for both the composited chime and the removal reference.
        /// <para>
        /// A played volume, not a mix level: the composited chime is meant to land at the level
        /// the live one was heard at, and the removal reference has to approximate the amplitude
        /// actually captured, because the cancellation calibrates its global gain near unity
        /// against it. Neither wants a level trimmed to taste on top.
        /// </para>
        /// </summary>
        private const double ChimeUnknownVolumeGain = 0.4;

        // Half-second blocks at 48 kHz: PcmAudio's own pre-3.1.4 default, and the granularity the
        // non-game stage escalates to. Small enough that a ~2 s chime dominates the blocks it
        // occupies, so its removal is scored where it happened rather than diluted across a
        // 22 s clip window.
        private const int ChimeBlockFrames = 24000;
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
        // Whether any enabled provider can service a game. A delegate, since the plugin owns the
        // registry, so capture is never started for a game that can never report an unlock.
        private readonly Func<Playnite.SDK.Models.Game, bool> _isAnyProviderCapable;
        private readonly ToastNotificationService _toastNotifications;
        // Optional foreground tracker: supplies learned game window handles and drives capture
        // ownership switches when the user moves between running games.
        private readonly ActiveGameWindowTracker _windowTracker;
        private readonly UnlockScreenshotService _screenshotService;

        private readonly object _gate = new object();
        // Requests whose overlay track hasn't arrived yet (guarded by _gate).
        private readonly List<ClipRequest> _awaitingTrack = new List<ClipRequest>();
        // Every wave chime that fired, with its resolved sound file and played volume when known
        // (guarded by _gate). The file-based live-chime removal reads these to know which chimes
        // overlap a clip window and how loud each rendered.
        private readonly List<(DateTime Utc, string Path, double? Gain)> _firedChimes =
            new List<(DateTime, string, double?)>();
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
            ActiveGameWindowTracker windowTracker = null,
            Func<Playnite.SDK.Models.Game, bool> isAnyProviderCapable = null)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _pluginUserDataPath = pluginUserDataPath;
            _getGameProcessId = getGameProcessId;
            _toastNotifications = toastNotifications;
            _isProviderRecordingEnabled = isProviderRecordingEnabled;
            _isAnyProviderCapable = isAnyProviderCapable;
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
            /// The exact sound file the wave played (UniPlaySong 1.8.4+), snapshotted at fire
            /// time. With a path, the composited chime is mixed from this file directly; null
            /// falls back to reading the captured chime sidecar.
            /// </summary>
            public string OwnSoundFilePath;

            /// <summary>The volume the file played at (0..1), or null to use the fixed gain.</summary>
            public double? OwnSoundFileGain;

            /// <summary>
            /// Notification delay snapshotted at unlock. Non-zero means the wave itself is held
            /// this long past the unlock before it may show, so every toast/track wait extends its
            /// silence budget by it — a long hold must not read as a stalled queue.
            /// </summary>
            public double NotificationDelaySeconds;

            /// <summary>
            /// Capture delay snapshotted at unlock. Non-zero means this clip anchors on the moment
            /// its capture was taken rather than on the unlock, and that the clip's own window
            /// extends that much further past the (possibly already delayed) wave start.
            /// </summary>
            public double CaptureDelaySeconds;

            /// <summary>
            /// Both delays together: how far past the unlock this request's display instant and
            /// capture may legitimately sit, added to the toast/track wait budgets below.
            /// </summary>
            public double TotalDelaySeconds => NotificationDelaySeconds + CaptureDelaySeconds;

            /// <summary>
            /// Completed with the instant this request's wave captured its base surface, or null
            /// when the wave never reached the screen or the wait gave up — either way the window
            /// falls back to the unlock anchor. Only created when either delay is configured; null
            /// otherwise, so the default path never waits on a toast before cutting its clip.
            /// </summary>
            public TaskCompletionSource<DateTime?> DisplayTcs;

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

            if (!ShouldCaptureGame(game, persisted, logReason: true))
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
        /// Whether a game may be captured at all. Mirrors the gates the rest of the plugin applies
        /// before it acts on a game: a user exclusion, and at least one enabled provider able to
        /// service it. Capturing a game that can report no unlock is pure background cost - video,
        /// audio and a rolling buffer maintained for a clip that can never be requested.
        /// </summary>
        private bool ShouldCaptureGame(
            Playnite.SDK.Models.Game game, PersistedSettings persisted, bool logReason)
        {
            if (game == null)
            {
                return false;
            }

            if (GameCustomDataLookup.GetExcludedRefreshGameIds(persisted)?.Contains(game.Id) == true)
            {
                if (logReason)
                {
                    _logger?.Debug($"[Recording] Skipped: {game.Name} is excluded from refreshes.");
                }

                return false;
            }

            // No delegate (older wiring) fails open, capturing as before rather than silently
            // stopping: a missing capability check must never cost the user a clip.
            if (_isAnyProviderCapable != null && !_isAnyProviderCapable(game))
            {
                if (logReason)
                {
                    _logger?.Debug($"[Recording] Skipped: no effective provider for {game.Name}.");
                }

                return false;
            }

            return true;
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

                // Do not retarget onto a game we may not capture. The session stays with its
                // current owner, which is still running and can still unlock; the alternative
                // (stopping) would cost that game its buffer for the sake of an ineligible one.
                if (!ShouldCaptureGame(e.Game, _settings?.Persisted, logReason: true))
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
                    // the game is launching on). A window that appears later — on another monitor,
                    // or belonging to the game rather than the launcher that opened first — is
                    // picked up by the per-tick resolve in WgcVideoRecorder.PumpLoop, which the
                    // window tracker answers with a better candidate as one becomes available.
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
                        () => _getGameProcessId?.Invoke(session.OwnerGameId));
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

                var drained = true;
                if (inFlight.Length > 0)
                {
                    var all = Task.WhenAll(inFlight);
                    var finished = await Task.WhenAny(
                        all, Task.Delay(TimeSpan.FromSeconds(DrainTimeoutSeconds))).ConfigureAwait(false);
                    drained = ReferenceEquals(finished, all);
                }

                session.WgcRecorder?.Dispose();
                session.WgcRecorder = null;
                session.AudioRecorder?.Dispose();
                session.AudioRecorder = null;
                session.ChimeRecorder?.Dispose();
                session.ChimeRecorder = null;

                // Only once nothing is still using it. An export that outran the drain writes its
                // sink INTO this directory, so deleting on timeout pulled the path out from under
                // it: field log, session stopped 18:03:03, export failed 18:04:03 with
                // "CreateSinkWriterFromURL ... 0x80070003 The system cannot find the path
                // specified", and that clip was lost. A directory left behind is swept by
                // CleanupStaleBufferDirectories on the next session, so the cost of waiting is
                // bounded disk, while the cost of deleting is the clip.
                if (drained)
                {
                    TryDeleteDirectory(session.BufferDirectory);
                }
                else
                {
                    _logger?.Warn(
                        "[Recording] Clip work outran the shutdown drain; leaving " +
                        $"'{Path.GetFileName(session.BufferDirectory)}' for the next session's sweep " +
                        "rather than deleting a directory an export is still writing into.");
                }
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

        /// <summary>
        /// Decodes the frame at an unlock's video anchor out of the rolling buffer, so the unlock
        /// screenshot can depict the true unlock moment instead of the (possibly much later)
        /// instant the toast fired — a provider can surface an unlock tens of seconds after its
        /// reported time. The anchor is validated through the same
        /// <see cref="SegmentTimeline.ComputeClipWindow"/> rules the clip uses, so the screenshot
        /// and the clip always agree on the instant. Returns null whenever the buffer cannot
        /// answer — no active capture for this game, the covering segment is still being written
        /// or already pruned, or the decode fails — and the caller falls back to the live screen
        /// grab. The clip's rarity/provider gates deliberately do not apply: they decide whether
        /// a clip is produced, not whether buffered footage of this game exists. Pool thread.
        /// </summary>
        internal System.Drawing.Bitmap TryCaptureAnchorFrame(AchievementUnlockedEventArgs e, int capHeight)
        {
            if (_disposed || e == null || e.IsPreview || e.IsTestFire || e.IsFriendUnlock)
            {
                return null;
            }

            CaptureSession session;
            lock (_gate)
            {
                session = _session;
            }

            if (session == null || session.Stopping || session.WgcRecorder == null)
            {
                return null;
            }

            if (e.PlayniteGameId != Guid.Empty &&
                session.OwnerGameId != Guid.Empty &&
                e.PlayniteGameId != session.OwnerGameId)
            {
                return null;
            }

            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return null;
            }

            try
            {
                var observedUtc = e.ObservedUtc == default(DateTime)
                    ? CaptureTimelineClock.UtcNow
                    : AsUtc(e.ObservedUtc);
                var videoAnchorUtc = e.VideoAnchorUtc ?? e.UnlockTimeUtc;
                if (videoAnchorUtc.HasValue)
                {
                    videoAnchorUtc = AsUtc(videoAnchorUtc.Value);
                }

                // Zero slot and tail: only the validated ToastAnchorUtc is wanted; the pre-roll
                // and poll interval mirror the clip's call so the staleness bound is identical.
                var anchorUtc = SegmentTimeline.ComputeClipWindow(
                    videoAnchorUtc,
                    observedUtc,
                    session.CaptureStartUtc,
                    oldestSegmentStartUtc: null,
                    pollIntervalSeconds: Math.Max(10, persisted.InGamePollIntervalSeconds),
                    preRollSeconds: persisted.RecordingClipSeconds,
                    toastSlotSeconds: 0,
                    tailSeconds: 0).ToastAnchorUtc;

                // Suspend pruning back to the covering segment's earliest possible start while it
                // is read; the same list/guard the base-clip extraction uses.
                var guardUtc = anchorUtc.AddSeconds(-SegmentSeconds);
                lock (_outstandingGate)
                {
                    _outstandingWindowStarts.Add(guardUtc);
                }

                try
                {
                    var segments = SegmentTimeline.ParseSegments(
                        ListBufferFiles(
                            session.BufferDirectory,
                            RecordingPaths.SegmentFilePrefix,
                            session.SegmentExtension),
                        TimeZoneInfo.Local,
                        RecordingPaths.SegmentFilePrefix,
                        session.SegmentExtension);
                    if (segments.Count == 0)
                    {
                        return null;
                    }

                    SegmentTimeline.SegmentInfo covering = null;
                    foreach (var segment in segments)
                    {
                        if (segment.StartUtc <= anchorUtc)
                        {
                            covering = segment;
                        }
                        else
                        {
                            break;
                        }
                    }

                    // The newest segment is still being written (no moov atom yet) and cannot be
                    // read; an anchor far past the covering segment's span means the footage is
                    // simply not there (a capture gap), where the live grab is the honest answer.
                    if (covering == null ||
                        ReferenceEquals(covering, segments[segments.Count - 1]))
                    {
                        return null;
                    }

                    var offsetSeconds = (anchorUtc - covering.StartUtc).TotalSeconds;
                    if (offsetSeconds > SegmentSeconds + 2)
                    {
                        return null;
                    }

                    var frame = MediaFoundationFrameExtractor.ExtractFrame(
                        covering.Path, offsetSeconds, _logger);
                    if (frame == null)
                    {
                        return null;
                    }

                    _logger?.Info(
                        $"[Recording] Unlock screenshot for '{e.DisplayName}' uses the buffered frame at " +
                        $"{anchorUtc:HH:mm:ss.f} ({offsetSeconds.ToString("F2", CultureInfo.InvariantCulture)}s into its segment).");
                    return _screenshotService.ApplyResolutionCap(frame, capHeight);
                }
                finally
                {
                    lock (_outstandingGate)
                    {
                        _outstandingWindowStarts.Remove(guardUtc);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[Recording] Buffered screenshot frame failed for '{e.DisplayName}'.");
                return null;
            }
        }

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

            // A retrigger is never delayed, so it never waits on a display instant — its clip
            // anchors on the retrigger moment, which observation already is.
            var notificationDelaySeconds = e.IsTestFire
                ? 0
                : Math.Max(0, persisted.NotificationDelaySeconds);
            var captureDelaySeconds = e.IsTestFire
                ? 0
                : Math.Max(0, persisted.CaptureDelaySeconds);

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
                NotificationDelaySeconds = notificationDelaySeconds,
                CaptureDelaySeconds = captureDelaySeconds,
                TrackTcs = new TaskCompletionSource<ToastOverlayTrack>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
                DisplayTcs = notificationDelaySeconds + captureDelaySeconds > 0
                    ? new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously)
                    : null,
            };

            lock (_gate)
            {
                _awaitingTrack.Add(request);
            }

            // Production starts immediately. With no delay configured the clip window is
            // unlock-anchored, so nothing about it depends on when (or whether) the toast displays,
            // and only the overlay composite waits for the track — after the toastless base clip is
            // already safe. A delay makes the window itself depend on the display instant, so that
            // path waits for it first and falls back to the unlock anchor if it never arrives.
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
                    // Every fired chime, for the file-based live-chime removal: a clip window can
                    // contain other waves' chimes besides its own. A null path marks a chime that
                    // can only be handled by the capture-based fallback.
                    _firedChimes.Add((e.SoundPlayedUtc.Value, e.SoundFilePath, e.SoundFileGain));
                    if (_firedChimes.Count > 64)
                    {
                        _firedChimes.RemoveAt(0);
                    }
                }

                foreach (var vm in e.Wave)
                {
                    if (e.SoundPlayedUtc.HasValue)
                    {
                        var soundMatch = _awaitingTrack.FirstOrDefault(r =>
                            !r.OwnSoundUtc.HasValue &&
                            r.CaptureCorrelationId == vm.CaptureCorrelationId);
                        if (soundMatch != null)
                        {
                            soundMatch.OwnSoundUtc = e.SoundPlayedUtc;
                            soundMatch.OwnSoundFilePath = e.SoundFilePath;
                            soundMatch.OwnSoundFileGain = e.SoundFileGain;
                        }
                    }

                    // Release the delayed window computation. Completed even when the wave was
                    // never revealed (null instant), so that path falls back to the unlock anchor
                    // straight away instead of waiting out the silence budget for an instant that
                    // is never coming.
                    var displayMatch = _awaitingTrack.FirstOrDefault(r =>
                        r.DisplayTcs != null &&
                        !r.DisplayTcs.Task.IsCompleted &&
                        r.CaptureCorrelationId == vm.CaptureCorrelationId);
                    displayMatch?.DisplayTcs.TrySetResult(e.SurfaceCaptureUtc);
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
        /// The full per-request pipeline, base-first: compute the window, extract the toastless
        /// base clip from the buffer (after which the segments are prune-safe and the clip can no
        /// longer be lost), then wait for this achievement's overlay track and re-encode the toast
        /// in. Track missing or re-encode failed → the toastless base is saved instead.
        ///
        /// With a notification delay configured the window is anchored on the moment the card
        /// appeared, so that instant has to be waited for before the window exists — the one thing
        /// that runs ahead of the base extraction. It falls back to the unlock anchor rather than
        /// blocking indefinitely, so base-first still holds for every outcome.
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
                var displayAnchorUtc = await WaitForDisplayAsync(request).ConfigureAwait(false);
                var window = SegmentTimeline.ComputeClipWindow(
                    request.VideoAnchorUtc,
                    request.ObservedUtc,
                    session.CaptureStartUtc,
                    oldestSegmentStartUtc: null,
                    pollIntervalSeconds: pollInterval,
                    preRollSeconds: persisted.RecordingClipSeconds,
                    toastSlotSeconds: toastSlotSeconds,
                    tailSeconds: ToastTailSeconds,
                    displayAnchorUtc: displayAnchorUtc);

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

                    var moveTimer = Stopwatch.StartNew();
                    var savedPath = SaveClipToUniquePath(finalPath, outputPath, copy: false);
                    if (savedPath == null)
                    {
                        _logger?.Warn($"[Recording] Could not place unlock clip for '{request.AchievementName}' (destination in use).");
                        TryDeleteFile(finalPath);
                        return;
                    }

                    // A cross-volume move degrades to a full byte copy of the clip, so its cost is
                    // worth seeing per clip.
                    _logger?.Debug(
                        $"[RecordingTiming] Placing the clip took {moveTimer.ElapsedMilliseconds}ms.");
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
        /// Removes the request from the track-wait list and resolves both its waiters null, so an
        /// abandoned production can't strand the wave matcher, a later WaitForTrackAsync, or a
        /// delayed request still waiting on a display instant that will never be stamped.
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
            request.DisplayTcs?.TrySetResult(null);
        }

        /// <summary>
        /// Waits for the instant this achievement's wave aims its base capture at, so the clip can
        /// be built around the frame the screenshot depicts. Null — anchor on the unlock instead —
        /// when neither delay is configured, or when the wait gives up on the same silence
        /// budget the track wait uses.
        ///
        /// The wave reports that instant when it settles, as a scheduled target rather than an
        /// observation, so this does not wait for the capture itself — only for the wave to reach
        /// the screen. Both configured delays extend the budget: the notification delay withholds
        /// the wave itself, and the capture delay pushes the clip's own window that much further
        /// out; without that an uncapped delay longer than <see cref="ToastWaitTimeoutSeconds"/>
        /// could read as a stalled queue.
        /// </summary>
        private async Task<DateTime?> WaitForDisplayAsync(ClipRequest request)
        {
            if (request.DisplayTcs == null)
            {
                return null;
            }

            var silenceBudget = TimeSpan.FromSeconds(ToastWaitTimeoutSeconds + request.TotalDelaySeconds);
            var overallBudget = TimeSpan.FromSeconds(MaxToastWaitSeconds + request.TotalDelaySeconds);

            while (true)
            {
                var completed = await Task.WhenAny(
                        request.DisplayTcs.Task,
                        Task.Delay(TimeSpan.FromSeconds(ToastWaitPollSeconds)))
                    .ConfigureAwait(false);
                if (completed == request.DisplayTcs.Task)
                {
                    return await request.DisplayTcs.Task.ConfigureAwait(false);
                }

                DateTime lastActivity;
                lock (_gate)
                {
                    lastActivity = _lastToastActivityUtc;
                }

                var now = CaptureTimelineClock.UtcNow;
                var silenceAnchor = lastActivity > request.ObservedUtc ? lastActivity : request.ObservedUtc;
                if (_disposed ||
                    now - silenceAnchor >= silenceBudget ||
                    now - request.ObservedUtc >= overallBudget)
                {
                    _logger?.Debug(
                        $"[Recording] No notification display instant for '{request.AchievementName}' " +
                        $"({(now - request.ObservedUtc).TotalSeconds:F0}s since observation); " +
                        "anchoring the clip on the unlock instead.");
                    request.DisplayTcs.TrySetResult(null);
                    return await request.DisplayTcs.Task.ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Waits for this achievement's overlay track, giving up (null → toastless clip) only
        /// after <see cref="ToastWaitTimeoutSeconds"/> of toast SILENCE — measured from the last
        /// wave shown or track completed, not from detection — so a toast queued minutes behind
        /// other waves still gets composited. The configured notification and capture delays both
        /// extend that budget, since the wave is deliberately withheld for that long before it can
        /// display at all. Returns whatever won a give-up/late-track race.
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
                    now - silenceAnchor >= TimeSpan.FromSeconds(ToastWaitTimeoutSeconds + request.TotalDelaySeconds) ||
                    now - request.ObservedUtc >= TimeSpan.FromSeconds(MaxToastWaitSeconds + request.TotalDelaySeconds))
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
            // By default the card sits on the unlock itself, not on the moment the real notification
            // reached the screen. Those are far apart: a provider poll takes seconds to notice an unlock,
            // so the notification appeared 9.2s after the fact in one measured case. A clip is built
            // around the unlock — the pre-roll leads up to it and the tail follows it — so that is where
            // the card belongs, and placing it there means the clip shows the achievement popping at the
            // instant it was earned.
            //
            // A configured capture delay reverses that preference: the window is then built around the
            // moment the capture was taken, so the card still lands on its own window's anchor and the
            // clip shows the same frame the screenshot did.
            //
            // Either way the anchor comes from the window, never from the track's own
            // first-rendered-frame stamp. That stamp is still what the card's animation plays from, so
            // the composited card slides in exactly as it did live; only its position is the window's.
            var overlaySeconds = Math.Min(toastSlotSeconds, track.DurationSeconds) + PostFadeTailSeconds;
            var overlayStartUtc = window.ToastAnchorUtc;

            var clipOriginUtc = clipStartUtc == default(DateTime) ? window.StartUtc : clipStartUtc;
            var toastStartSeconds = videoLeadSeconds + (overlayStartUtc - clipOriginUtc).TotalSeconds;
            var endSeconds = toastStartSeconds + overlaySeconds;
            var chimeLeadSeconds = ResolveChimeLeadSeconds(request, track);

            // Where the card landed, and how far the real notification was from it. Unlock-anchored,
            // that gap is the provider's detection lag, and seeing it beside the placement makes an
            // odd-looking clip readable without reasoning backwards from the window. Display-anchored,
            // the gap should be near zero — a large one there means the card was placed away from the
            // frame the screenshot captured.
            _logger?.Info(
                $"[RecordingTiming] toast placed at {toastStartSeconds.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"on the {(window.AnchoredOnDisplay ? "notification" : "unlock")} ({Stamp(window.ToastAnchorUtc)}); " +
                $"the notification itself appeared " +
                $"{(track.StartUtc - window.ToastAnchorUtc).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s later " +
                $"({Stamp(track.StartUtc)}). lead={videoLeadSeconds.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"end={endSeconds.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"chimeLead={chimeLeadSeconds.ToString("F3", CultureInfo.InvariantCulture)}s");
            // The wave's own chime, read from the Playnite-only sidecar at its real time, mixed
            // in ahead of the composited card by the lead the two actually had live.
            var chimePcm = await TryReadChimePcmAsync(session, request).ConfigureAwait(false);
            var chimeStartSeconds = toastStartSeconds - chimeLeadSeconds;
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
            // Wait until the window has fully elapsed, then ask the recorders to close the segment
            // and audio chunk covering its end instead of sleeping out their natural boundaries
            // (previously a fixed K + margin here, ~7 s of mostly dead time per clip). The fixed
            // wait's release instant remains as the bound, so a wedged or stopped pump can never
            // make this slower than it used to be, and the concat still never reads a half-written
            // segment: the flush completes only after the covering files are closed.
            var readyAtUtc = window.EndUtc.AddSeconds(SegmentSeconds + 2);
            var legacyWaitMs = Math.Max(0, (readyAtUtc - CaptureTimelineClock.UtcNow).TotalMilliseconds);
            var readinessTimer = Stopwatch.StartNew();
            var untilWindowEnd = window.EndUtc - CaptureTimelineClock.UtcNow;
            if (untilWindowEnd > TimeSpan.Zero)
            {
                await Task.Delay(untilWindowEnd).ConfigureAwait(false);
            }

            await WaitForCoveringFilesAsync(session, window.EndUtc, readyAtUtc).ConfigureAwait(false);
            _logger?.Debug(
                $"[RecordingTiming] Export readiness took {readinessTimer.ElapsedMilliseconds}ms " +
                $"(the fixed wait would have been {legacyWaitMs:0}ms).");

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

            // Both modes start from the haptic-free speaker endpoint. Game Only verifiably removes
            // the simultaneous non-game reference; Full System removes only the Playnite-tree
            // slice so the chime can be re-timed onto the composited toast.
            var recordedAudioPlan = audioPlan;
            var cleanedAudioDirectory = (string)null;
            if (audioPlan != null)
            {
                var cleanupTimer = Stopwatch.StartNew();
                var selectedAudioPlan = TryRemoveNonGameAudio(
                    session, recordedAudioPlan, out cleanedAudioDirectory);
                _logger?.Debug(
                    $"[RecordingTiming] Clip-audio cleanup took {cleanupTimer.ElapsedMilliseconds}ms.");
                // Deliberately redundant with the cleanup's own fallback: no cleanup regression
                // may turn an existing speaker-endpoint plan into the no-audio sentinel.
                audioPlan = selectedAudioPlan ?? recordedAudioPlan;
                if (selectedAudioPlan == null)
                {
                    TryDeleteCleanedAudio(cleanedAudioDirectory);
                    cleanedAudioDirectory = null;
                    _logger?.Warn(
                        "[Recording] Clip-audio cleanup returned no usable plan; keeping the " +
                        "haptic-free speaker audio.");
                }
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
            var exportTimer = Stopwatch.StartNew();
            try
            {
                ok = await Task.Run(() => exporter.Export(plan, audioPlan, tempPath, out videoLeadSeconds))
                    .ConfigureAwait(false);
                if (!ok && cleanedAudioDirectory != null && recordedAudioPlan != null)
                {
                    // A verified PCM cleanup can still fail while its WAV is opened or muxed.
                    // Retry the already haptic-free speaker plan; isolation remains optional and
                    // can never cause a missing audio track.
                    _logger?.Warn(
                        "[Recording] Export with cleaned clip audio failed; retrying with the " +
                        "original recorded audio (the haptic-free full-system speaker mix).");
                    TryDeleteFile(tempPath);
                    videoLeadSeconds = 0;
                    ok = await Task.Run(() => exporter.Export(
                        plan, recordedAudioPlan, tempPath, out videoLeadSeconds)).ConfigureAwait(false);
                }
            }
            finally
            {
                _baseExportGate.Release();
                TryDeleteCleanedAudio(cleanedAudioDirectory);
                _logger?.Debug(
                    $"[RecordingTiming] Base clip export took {exportTimer.ElapsedMilliseconds}ms " +
                    $"({plan.Segments.Count} segment(s)).");
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
            var clipStartUtc = plan.StartUtc;
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
        /// Asks the live recorders to close the segment and audio chunk covering
        /// <paramref name="throughUtc"/> now and waits for them, bounded by
        /// <paramref name="deadlineUtc"/> — the instant the old fixed wait would have released the
        /// export anyway — so a wedged or stopped pump falls back to exactly the old behavior.
        /// </summary>
        private async Task WaitForCoveringFilesAsync(
            CaptureSession session, DateTime throughUtc, DateTime deadlineUtc)
        {
            var flushes = new List<Task>(2);
            var recorder = session.WgcRecorder;
            if (recorder != null)
            {
                flushes.Add(recorder.FlushSegmentsThroughAsync(throughUtc));
            }

            var audio = session.AudioRecorder;
            if (audio != null)
            {
                flushes.Add(audio.FlushChunksThroughAsync(throughUtc));
            }

            await WaitForFlushesAsync(flushes, deadlineUtc).ConfigureAwait(false);
        }

        private async Task WaitForFlushesAsync(List<Task> flushes, DateTime deadlineUtc)
        {
            if (flushes.Count == 0)
            {
                return;
            }

            var all = Task.WhenAll(flushes);
            var bound = deadlineUtc - CaptureTimelineClock.UtcNow;
            if (bound > TimeSpan.Zero)
            {
                await Task.WhenAny(all, Task.Delay(bound)).ConfigureAwait(false);
            }

            if (all.IsCompleted)
            {
                try
                {
                    await all.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(
                        ex, "[Recording] Closing the covering capture files failed; exporting what is on disk.");
                }

                return;
            }

            _logger?.Debug(
                "[Recording] Covering capture files did not close before the fixed-wait deadline; " +
                "exporting what is on disk.");
            // Observe a late fault so it never surfaces as an unobserved task exception.
            var ignored = all.ContinueWith(
                t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// How far the chime onset precedes the composited card, from the live stamps: the moment
        /// the wave sound fired, and the moment the card's first frame rendered. The card plays its
        /// recorded animation from that first frame, so reproducing this gap reproduces what the
        /// user heard and saw.
        /// <para>
        /// Falls back to the sound-align delay's default when either stamp is missing, or when
        /// their gap is not plausible — negative means the card beat its own sound, and a very
        /// large one means the sound belongs to a different wave. Neither should place a chime.
        /// </para>
        /// </summary>
        private double ResolveChimeLeadSeconds(ClipRequest request, ToastOverlayTrack track)
        {
            DateTime? ownSound;
            lock (_gate)
            {
                ownSound = request.OwnSoundUtc;
            }

            if (!ownSound.HasValue || track == null || track.StartUtc == default(DateTime))
            {
                return ChimeLeadFallbackSeconds;
            }

            var measured = (track.StartUtc - ownSound.Value).TotalSeconds;
            if (measured < 0 || measured > ChimeLeadMaxSeconds)
            {
                _logger?.Debug(
                    $"[Recording] Chime lead {measured.ToString("F3", CultureInfo.InvariantCulture)}s is " +
                    "outside the plausible range; using the default sound-align lead.");
                return ChimeLeadFallbackSeconds;
            }

            return measured;
        }

        /// <summary>
        /// Reads this request's chime from the Playnite-tree sidecar chunks at the moment its wave
        /// sound actually played. When the game is a Playnite descendant, its same-time game-only
        /// reference is aligned and cancelled first; this is what prevents the sidecar from adding
        /// a delayed second copy of emulator audio at the composited toast.
        ///
        /// Flushes the chunks covering the chime window closed first, the same way the base clip
        /// flushes its last video segment (bounded by the old fixed wait). A chunk still being
        /// written carries placeholder RIFF sizes, and Media Foundation rejects that outright
        /// (MF_E_UNSUPPORTED_BYTESTREAM_TYPE) — the chime window ends only a few seconds after the
        /// toast fires, so without closed chunks the newest one is essentially always mid-write
        /// and every clip silently lost its chime.
        /// </summary>
        private async Task<byte[]> TryReadChimePcmAsync(CaptureSession session, ClipRequest request)
        {
            DateTime? ownSound;
            string soundFilePath;
            double? soundFileGain;
            lock (_gate)
            {
                ownSound = request.OwnSoundUtc;
                soundFilePath = request.OwnSoundFilePath;
                soundFileGain = request.OwnSoundFileGain;
            }

            // Every fired chime is removed from the base audio best-effort at export; the wave's
            // own chime is then always composited at the toast. A wave that played no sound gets
            // none.
            if (!ownSound.HasValue)
            {
                return null;
            }

            var chimeSeconds =
                Math.Min(request.EffectiveToastSeconds, ChimeMaxSliceSeconds) +
                ChimeTailBeyondToastSeconds;
            if (soundFilePath != null)
            {
                // The exact file the wave played, mixed at the volume UniPlaySong played it at
                // (its jingles follow MusicVolume): no captured copy, no separation to verify,
                // and no wait for a sidecar chunk to close.
                var filePcm = ChimeSoundFile.TryReadPcm(
                    soundFilePath, chimeSeconds, soundFileGain ?? ChimeUnknownVolumeGain, _logger);
                if (filePcm != null)
                {
                    PcmAudio.FadeOutTail(filePcm, ChimeFadeOutSeconds);
                    return filePcm;
                }

                _logger?.Warn(
                    "[Recording] The resolved chime file could not be decoded; falling back to " +
                    "the captured chime sidecar.");
            }

            if (session.ChimeRecorder == null)
            {
                return null;
            }

            // Ask the sidecar recorders to close the chunks covering the chime window instead of
            // sleeping out their natural boundaries (previously a fixed K + margin here), bounded
            // by the old fixed wait's release instant as the fallback. The game-reference chunks
            // used for cancellation come from the main recorder's stamped track, so both recorders
            // flush.
            var chimeWindowEndUtc = ownSound.Value.AddSeconds(chimeSeconds);
            var chimeReadyAtUtc = chimeWindowEndUtc.AddSeconds(SegmentSeconds + 2);
            var untilChimeEnd = chimeWindowEndUtc - CaptureTimelineClock.UtcNow;
            if (untilChimeEnd > TimeSpan.Zero)
            {
                await Task.Delay(untilChimeEnd).ConfigureAwait(false);
            }

            var chimeTimer = Stopwatch.StartNew();
            var sidecarFlushes = new List<Task>(2)
            {
                session.ChimeRecorder.FlushAuxiliaryChunksThroughAsync(chimeWindowEndUtc),
            };
            if (session.AudioRecorder != null)
            {
                sidecarFlushes.Add(
                    session.AudioRecorder.FlushAuxiliaryChunksThroughAsync(chimeWindowEndUtc));
            }

            await WaitForFlushesAsync(sidecarFlushes, chimeReadyAtUtc).ConfigureAwait(false);
            _logger?.Debug(
                $"[RecordingTiming] Chime sidecar readiness took {chimeTimer.ElapsedMilliseconds}ms " +
                $"past the chime window (the fixed wait was {(SegmentSeconds + 2) * 1000}ms).");

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

                var outcome = CancelGameFromPlayniteSlice(pcm, referencePcm, out var cancellation);
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
                    $"suppression={cancellation.SuppressionDb:0.0}dB " +
                    $"fixedBlocks={cancellation.FixedFitBlocks} mutedBlocks={cancellation.MutedBlocks}.");
            }

            if (pcm != null)
            {
                PcmAudio.FadeOutTail(pcm, ChimeFadeOutSeconds);
            }

            return pcm;
        }

        /// <summary>
        /// Reads the Game Only "everything except the game tree" reference over the clip window.
        /// Returns null when there is nothing that may be subtracted.
        /// </summary>
        private byte[] TryReadNonGameReference(
            CaptureSession session,
            DateTime startUtc,
            DateTime endUtc)
        {
            if (session.AudioRecorder.NonGameReferenceFailed)
            {
                _logger?.Warn(
                    "[Recording] Game-only isolation reference failed; keeping the " +
                    "haptic-free full-system speaker mix.");
                return null;
            }

            var reference = TryReadAudioWindow(
                session.BufferDirectory,
                RecordingPaths.NonGameReferenceChunkFilePrefix,
                startUtc,
                endUtc,
                out var referenceCovered);
            if (reference == null)
            {
                if (referenceCovered)
                {
                    _logger?.Warn(
                        "[Recording] Non-game reference could not be decoded; keeping the " +
                        "haptic-free full-system speaker mix.");
                }
                else
                {
                    // Sparse process loopback delivers nothing during silence, so no coverage
                    // from a healthy reference means no non-game audio played in this window.
                    _logger?.Debug(
                        "[Recording] No non-game process audio covers this clip; the " +
                        "haptic-free speaker mix is already Game Only.");
                }

                return null;
            }

            // An audio-mirroring service (game streaming, casting) re-renders the whole mix from
            // its own process, so this "everything except the game tree" reference can carry a
            // delayed COPY of the game. Subtracting that copy would verifiably remove real game
            // audio from the clip. Purge everything game-correlated from the reference first; the
            // mirror's latency is unrelated to the capture clients', hence the wide search. A
            // silent (uncovered) game track cannot have been mirrored, so no purge is needed.
            var gameCovered = false;
            var gamePcm = session.AudioRecorder.GameReferenceFailed
                ? null
                : TryReadAudioWindow(
                    session.BufferDirectory,
                    RecordingPaths.GameReferenceChunkFilePrefix,
                    startUtc,
                    endUtc,
                    out gameCovered);
            if (gamePcm == null)
            {
                if (session.AudioRecorder.GameReferenceFailed || gameCovered)
                {
                    _logger?.Warn(
                        "[Recording] The game reference is unavailable, so the non-game " +
                        "reference cannot be verified free of a mirrored game copy; keeping " +
                        "the haptic-free full-system speaker mix.");
                    return null;
                }

                return reference;
            }

            var purgeOutcome = CancelGameFromPlayniteSlice(
                reference, gamePcm, out var purge, maxLagFrames: 12000);
            if (purgeOutcome == PcmCancellationOutcome.Unseparable || purge.MutedBlocks > 0)
            {
                _logger?.Warn(
                    "[Recording] The non-game reference could not be verified free of a " +
                    $"mirrored game copy (outcome={purgeOutcome} " +
                    $"correlation={purge.Correlation:0.000} gated={purge.MutedBlocks}); " +
                    "keeping the haptic-free full-system speaker mix.");
                return null;
            }

            return reference;
        }

        /// <summary>
        /// Whether any wave chime fired inside this window. Only a cheap check of the fire stamps:
        /// the chime is removed using the captured sidecar, so no file has to be decoded to find
        /// out whether there is anything to remove.
        /// </summary>
        private bool AnyChimeFiredIn(DateTime startUtc, DateTime endUtc)
        {
            var span = ChimeMaxSliceSeconds + ChimeTailBeyondToastSeconds;
            lock (_gate)
            {
                foreach (var chime in _firedChimes)
                {
                    if (chime.Utc < endUtc && chime.Utc.AddSeconds(span) > startUtc)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        /// <summary>
        /// Builds the CAPTURED live-chime removal reference for a clip window: the Playnite-tree
        /// slice with the game reference cancelled out of it (a Playnite-launched game lives
        /// inside both trees, and only what is verifiably not the game may be subtracted from the
        /// clip audio). Complementary to the file reference: this slice carries the chime exactly
        /// as it rendered — a cold player's time-warped onset included — which the pristine file
        /// cannot match, while the file is immune to the crossfeed and tears that can contaminate
        /// this capture. Returns null when the slice cannot be trusted.
        /// </summary>
        private byte[] TryReadCapturedChimeReference(
            CaptureSession session,
            DateTime startUtc,
            DateTime endUtc)
        {
            if (session.ChimeRecorder == null ||
                session.AudioRecorder?.ChimeCaptureMode !=
                    PlayniteChimeCaptureMode.CancelGameReference ||
                session.ChimeRecorder.ChimeReferenceFailed ||
                session.AudioRecorder.GameReferenceFailed)
            {
                _logger?.Debug(
                    "[Recording] No captured chime reference is available for this window.");
                return null;
            }

            var reference = TryReadAudioWindow(
                session.BufferDirectory,
                RecordingPaths.ChimeChunkFilePrefix,
                startUtc,
                endUtc,
                out var referenceCovered);
            if (reference == null)
            {
                // A chime is known to have fired inside this window, so a sidecar with nothing to
                // show for it cannot prove the speaker mix clean either way.
                _logger?.Warn(
                    referenceCovered
                        ? "[Recording] The Playnite-tree slice could not be decoded; the live " +
                          "chime stays in the speaker mix."
                        : "[Recording] The chime sidecar has no coverage over a window a chime " +
                          "fired in; the live chime stays in the speaker mix.");
                return null;
            }

            var gamePcm = TryReadAudioWindow(
                session.BufferDirectory,
                RecordingPaths.GameReferenceChunkFilePrefix,
                startUtc,
                endUtc,
                out var gameCovered);
            if (gamePcm == null && gameCovered)
            {
                _logger?.Warn(
                    "[Recording] The game reference could not be decoded; the live chime " +
                    "stays in the speaker mix.");
                return null;
            }

            if (gamePcm != null)
            {
                // Same contract as the sidecar read in TryReadChimePcmAsync. Unverified blocks are
                // muted in the REFERENCE, which merely means "subtract nothing there"; only
                // Unseparable leaves the reference possibly still carrying the game.
                var outcome = CancelGameFromPlayniteSlice(reference, gamePcm, out var cancellation);
                if (outcome == PcmCancellationOutcome.Unseparable || cancellation.MutedBlocks > 0)
                {
                    // A muted span is a hole in the reference: the speaker mix still carries the
                    // real Playnite audio there, but the subtraction would see silence and leave
                    // it — and a later composite would then double that fragment.
                    _logger?.Warn(
                        "[Recording] The Playnite-tree slice could not be verified game-free " +
                        $"(outcome={outcome} correlation={cancellation.Correlation:0.000} " +
                        $"gated={cancellation.MutedBlocks}); the live chime stays in the " +
                        "speaker mix.");
                    return null;
                }
            }

            return reference;
        }

        /// <summary>
        /// Turns the haptic-free speaker mix into the configured mode's clip audio. Game Only
        /// removes the simultaneously captured "everything except the game tree" reference; both
        /// modes then remove every fired live chime best-effort, because the wave's own chime is
        /// always composited back at the toast. Rejection keeps the speaker mix: that may contain
        /// another application or a live chime, but it can never contain a controller endpoint or
        /// become the exporter's no-audio sentinel.
        /// </summary>
        private SegmentTimeline.ClipPlan TryRemoveNonGameAudio(
            CaptureSession session,
            SegmentTimeline.ClipPlan audioPlan,
            out string cleanedDirectory)
        {
            cleanedDirectory = null;
            string candidateDirectory = null;
            var gameOnly = session.AudioRecorder?.RequiresNonGameCleanup == true;
            if (audioPlan?.Segments == null || audioPlan.Segments.Count == 0 ||
                session.AudioRecorder == null)
            {
                return audioPlan;
            }

            try
            {
                var startUtc = audioPlan.StartUtc;
                var endUtc = audioPlan.EndUtc;
                var mixture = TryReadAudioWindow(
                    session.BufferDirectory,
                    RecordingPaths.AudioChunkFilePrefix,
                    startUtc,
                    endUtc);
                if (mixture == null)
                {
                    return audioPlan;
                }

                var subtractedAnything = false;
                if (gameOnly)
                {
                    var reference = TryReadNonGameReference(session, startUtc, endUtc);
                    if (reference != null)
                    {
                        var recordedMixture = (byte[])mixture.Clone();
                        var outcome = SubtractNonGame(
                            mixture,
                            reference,
                            out var cancellation,
                            residualPass: false);
                        var fit = "one-full-clip-stereo-subtraction";
                        if (outcome != PcmCancellationOutcome.CancelledVerified)
                        {
                            mixture = recordedMixture;
                            outcome = SubtractNonGame(
                                mixture,
                                reference,
                                out cancellation,
                                residualPass: false,
                                blockFrames: 24000);
                            fit = "500ms-gain-fallback";
                        }
                        _logger?.Info(
                            $"[Recording] Game-only isolation: outcome={outcome} " +
                            $"lag={cancellation.StartLagMs:0.###}->{cancellation.EndLagMs:0.###}ms " +
                            $"correlation={cancellation.Correlation:0.000} " +
                            $"suppression={cancellation.SuppressionDb:0.0}dB " +
                            $"blocks={cancellation.SubtractedBlocks}/{cancellation.TotalBlocks} " +
                            $"restored={cancellation.RestoredBlocks} gated={cancellation.MutedBlocks} " +
                            $"fit={fit}.");
                        if (outcome == PcmCancellationOutcome.CancelledVerified)
                        {
                            subtractedAnything = true;
                            // A second full-clip fit can remove a low-level phase/latency residual
                            // left by the first endpoint/process transfer. Optional and
                            // transactional: rejection leaves the verified result intact.
                            for (var pass = 1; pass <= 3; pass++)
                            {
                                var residualOutcome = SubtractNonGame(
                                    mixture,
                                    reference,
                                    out var residual,
                                    residualPass: true);
                                _logger?.Debug(
                                    $"[Recording] Game-only isolation residual pass {pass}: " +
                                    $"outcome={residualOutcome} " +
                                    $"lag={residual.StartLagMs:0.###}->{residual.EndLagMs:0.###}ms " +
                                    $"correlation={residual.Correlation:0.000} " +
                                    $"suppression={residual.SuppressionDb:0.0}dB.");
                                if (residualOutcome != PcmCancellationOutcome.CancelledVerified)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }

                // Live chimes are ALWAYS removed, in both modes and regardless of whether the
                // non-game stage ran — file references first, the captured Playnite-tree slice as
                // fallback — and the wave's own chime is composited at the toast instead. The
                // chime-file reference sits at the sound LAUNCH stamp while the live chime starts
                // an out-of-process onset later (player spin-up, measured up to ~500 ms cold),
                // hence its wide search. A verified partial removal is kept: an attenuated
                // residue under a correctly placed chime beats a full-level live chime at the
                // wrong moment.
                //
                // Hybrid removal: the file pass first (pristine reference, immune to crossfeed
                // and capture tears), then a captured-slice mop-up on whatever remains (it
                // carries the chime exactly as rendered, so a cold player's time-warped onset —
                // which cannot match the file — still subtracts). Each pass commits only
                // held-out-verified work.
                PcmCancellationOutcome ChimePass(
                    byte[] chimeReference, string source, int maxLag)
                {
                    // Half-second blocks, which is what PcmAudio defaulted to through 3.1.3 and
                    // what this pass silently lost when the caller began overriding the block size
                    // with the whole window.
                    //
                    // A chime is ~2 s inside a ~22 s clip window, so it is a small fraction of the
                    // window's energy. Scored as ONE block, even a perfect cancellation moves
                    // window-wide suppression by only a few dB and can never clear the 10 dB keep
                    // gate -- so the block was always restored and nothing was ever removed. The
                    // field log shows exactly that: correlation 0.441 and lag 15.5 ms (the chime
                    // found, and matching) but suppression 6.5 dB, blocks=0/1, restored=1. The
                    // composite then added its copy on top, which is the double chime.
                    //
                    // Blocks score the chime where it actually is. A block the reference is silent
                    // through fits a gain under blockGainFloor and is left alone, so this cannot
                    // inject an inverted copy into game-only audio -- provided the ORDINARY floors
                    // are used, not the residual pass's (0.001 / 0.03), which do let a silent
                    // block fit noise. That distinction is why the earlier blocked attempt made
                    // the doubling worse.
                    var passOutcome = SubtractNonGame(
                        mixture,
                        chimeReference,
                        out var chimePass,
                        residualPass: false,
                        blockFrames: ChimeBlockFrames,
                        maxLagFrames: maxLag,
                        detectClean: true);
                    if (passOutcome == PcmCancellationOutcome.Unseparable ||
                        (passOutcome == PcmCancellationOutcome.CleanNoGameDetected &&
                            chimePass.SubtractedBlocks == 0))
                    {
                        // A residue between the clean ceiling and the ordinary entry gate is
                        // still worth an attempt at the residual pass's lower floors — every
                        // committed block still proves itself on held-out samples. The same goes
                        // for a "clean" verdict that did zero work: a chime is KNOWN to have
                        // fired in this window, and a quiet chime's true gain can sit under the
                        // ordinary fit floor (field: a 5% music volume read as clean and the
                        // composite then doubled it).
                        passOutcome = SubtractNonGame(
                            mixture,
                            chimeReference,
                            out chimePass,
                            residualPass: true,
                            maxLagFrames: maxLag,
                            detectClean: true);
                    }

                    _logger?.Info(
                        $"[Recording] Live-chime removal ({source}): outcome={passOutcome} " +
                        $"lag={chimePass.StartLagMs:0.###}ms " +
                        $"correlation={chimePass.Correlation:0.000} " +
                        $"suppression={chimePass.SuppressionDb:0.0}dB " +
                        $"blocks={chimePass.SubtractedBlocks}/{chimePass.TotalBlocks} " +
                        $"restored={chimePass.RestoredBlocks} gated={chimePass.MutedBlocks}.");
                    subtractedAnything |=
                        passOutcome == PcmCancellationOutcome.CancelledVerified &&
                        chimePass.SubtractedBlocks > 0;
                    return passOutcome;
                }

                // Only the captured sidecar is used as a removal reference. It is recorded on the
                // same capture clock, through the same engine, as the mix it is subtracted from,
                // so the chime sits at the same place in both and a narrow search finds it.
                //
                // The file reference cannot align by construction: it sits at the sound LAUNCH
                // stamp, which the real onset trails by a variable out-of-process spin-up, and it
                // has been through none of the resampling or level scaling the engine applied.
                // ChimeRoundTripProbe measured it against these exact parameters -- aligned it
                // removes 37.7 dB, 120 ms out 5.7 dB, 500 ms out nothing at all -- and at every
                // offset it damaged the game bed by ~16 dB doing it. A pass that leaves the chime
                // and eats the game is worse than no pass, so there is no fallback value in it.
                // 3.1.3 had no such pass and removed chimes correctly.
                //
                // The file stays the source of the COMPOSITED chime, where being pristine is
                // exactly what is wanted; see TryReadChimePcmAsync.
                if (AnyChimeFiredIn(startUtc, endUtc))
                {
                    var capturedChimeReference = TryReadCapturedChimeReference(
                        session, startUtc, endUtc);
                    if (capturedChimeReference != null)
                    {
                        ChimePass(capturedChimeReference, "capture", 12000);
                    }
                    else
                    {
                        _logger?.Info(
                            "[Recording] A chime fired inside this window but no captured " +
                            "reference exists; the live chime stays in the speaker mix.");
                    }
                }

                if (!subtractedAnything)
                {
                    return audioPlan;
                }

                candidateDirectory = Path.Combine(
                    session.BufferDirectory,
                    $"clean_{Guid.NewGuid():N}");
                Directory.CreateDirectory(candidateDirectory);
                var name = RecordingPaths.BuildAudioChunkFileName(
                    RecordingPaths.AudioChunkFilePrefix,
                    startUtc);
                PcmAudio.WriteWav(Path.Combine(candidateDirectory, name), mixture);
                var cleanedChunks = SegmentTimeline.ParseSegments(
                    ListBufferFiles(
                        candidateDirectory,
                        RecordingPaths.AudioChunkFilePrefix,
                        RecordingPaths.AudioChunkFileExtension),
                    TimeZoneInfo.Local,
                    RecordingPaths.AudioChunkFilePrefix,
                    RecordingPaths.AudioChunkFileExtension);
                var cleanedPlan = SegmentTimeline.PlanClip(
                    cleanedChunks,
                    startUtc,
                    endUtc,
                    Math.Max(SegmentSeconds, (int)Math.Ceiling(audioPlan.DurationSeconds) + 1));
                if (cleanedPlan == null)
                {
                    TryDeleteCleanedAudio(candidateDirectory);
                    return audioPlan;
                }

                cleanedDirectory = candidateDirectory;
                return cleanedPlan;
            }
            catch (Exception ex)
            {
                TryDeleteCleanedAudio(candidateDirectory);
                _logger?.Warn(
                    ex,
                    "[Recording] Clip-audio isolation failed; keeping the " +
                    "haptic-free full-system speaker mix.");
                return audioPlan;
            }
        }

        /// <summary>
        /// Removes the game reference from a Playnite-tree slice. One fixed-lag pass cannot remove
        /// a game that reaches both process captures through two engine paths — its speaker audio
        /// and a controller haptic stream sit at different capture latencies — so two best-effort
        /// peel passes first remove whatever proves itself at each path's own lag. The peels
        /// commit only held-out-verified blocks and restore everything else; the final strict pass
        /// still mutes anything unproven, so nothing unverified can ride into the chime.
        /// </summary>
        private static PcmCancellationOutcome CancelGameFromPlayniteSlice(
            byte[] slice,
            byte[] gameReference,
            out PcmCancellationDiagnostics diagnostics,
            int maxLagFrames = 2400)
        {
            for (var peel = 0; peel < 2; peel++)
            {
                var peelOutcome = PcmAudio.CancelCorrelated(
                    slice,
                    gameReference,
                    out _,
                    muteUnverifiedBlocks: false,
                    maxLagFrames: maxLagFrames,
                    commitVerifiedBlocksOnWeakPass: true,
                    preferEarlyAlignmentWindow: true,
                    verificationLagRadiusFrames: 480);
                if (peelOutcome != PcmCancellationOutcome.CancelledVerified)
                {
                    break;
                }
            }

            return PcmAudio.CancelCorrelated(
                slice,
                gameReference,
                out diagnostics,
                maxLagFrames: maxLagFrames,
                preferEarlyAlignmentWindow: true,
                verificationLagRadiusFrames: 480);
        }

        /// <summary>
        /// Cancels a known reference out of captured audio. The thresholds live in
        /// <see cref="ReferenceCancellationPolicy"/> so the capture harness can exercise the real
        /// ones rather than a copy; see tools/capture-harness/ChimeRoundTripProbe.
        /// </summary>
        private static PcmCancellationOutcome SubtractNonGame(
            byte[] mixture,
            byte[] reference,
            out PcmCancellationDiagnostics diagnostics,
            bool residualPass,
            int? blockFrames = null,
            int maxLagFrames = 12000,
            bool detectClean = false)
        {
            return ReferenceCancellationPolicy.Subtract(
                mixture,
                reference,
                out diagnostics,
                residualPass,
                blockFrames,
                maxLagFrames,
                detectClean);
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
            return TryReadAudioWindow(
                bufferDirectory, prefix, startUtc, endUtc, out _);
        }

        private byte[] TryReadAudioWindow(
            string bufferDirectory,
            string prefix,
            DateTime startUtc,
            DateTime endUtc,
            out bool windowCovered)
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
            windowCovered = plan != null;
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

                // A retrigger normally captures into the game's own folder, exactly like a genuine
                // unlock. Opting into the test folder diverts it to the shared "Test" subfolder
                // instead, matching the screenshot planner so a retrigger's clip and screenshot
                // never land in different places.
                if (request.IsTestFire && persisted.EnableCaptureTestFolder)
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
                var audioPrefixes = new List<string>
                {
                    RecordingPaths.AudioChunkFilePrefix,
                    RecordingPaths.ChimeChunkFilePrefix,
                    RecordingPaths.GameReferenceChunkFilePrefix,
                    RecordingPaths.NonGameReferenceChunkFilePrefix,
                };
                foreach (var prefix in audioPrefixes)
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
                var directory = new DirectoryInfo(bufferDirectory);
                if (!directory.Exists)
                {
                    return result;
                }

                // DirectoryInfo.GetFiles carries each entry's length from the directory enumeration
                // itself. Path-plus-FileInfo would re-stat every file, which the prune tick pays for
                // five prefixes across the whole buffer every thirty seconds for the whole session.
                foreach (var file in directory.GetFiles(prefix + "*" + extension))
                {
                    long size = 0;
                    try
                    {
                        size = file.Length;
                    }
                    catch
                    {
                    }

                    result.Add((file.FullName, size));
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
                request.DisplayTcs?.TrySetResult(null);
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
