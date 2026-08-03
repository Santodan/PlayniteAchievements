using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Records unlock video clips via a user-supplied ffmpeg. While a game runs, ffmpeg captures
    /// the game's monitor into a rolling buffer of short MPEG-TS segments under the plugin's user
    /// data path; on each own-unlock a clip covering BOTH the unlock moment and the toast
    /// appearing on screen is trimmed out of the buffer (see <see cref="SegmentTimeline"/>).
    /// Subscribes to <see cref="PlayniteAchievementsPlugin.AchievementUnlocked"/> in parallel to
    /// the toast service, and to <see cref="ToastNotificationService.WaveDisplayed"/> for the
    /// clip end anchor. Per-unlock failures are silent-but-logged; configuration failures
    /// (invalid ffmpeg, low disk, repeated capture crashes) raise one notification per session.
    /// </summary>
    internal sealed class UnlockRecordingService : IDisposable
    {
        /// <summary>Rolling capture segment length in seconds (K).</summary>
        internal const int SegmentSeconds = 5;

        private const string BufferRootFolderName = "RecordingBuffer";
        private const long MinFreeBytesToStart = 2L * 1024 * 1024 * 1024;
        private const long MinFreeBytesToContinue = 500L * 1024 * 1024;
        private const long MaxBufferBytes = 2L * 1024 * 1024 * 1024;
        private const int WindowResolveTimeoutSeconds = 60;
        // How long to hold off the capture start waiting for the started process's main window.
        // Kept short: unlocks that fire before the capture is live can never be clipped, so a
        // slow-launching game must not leave a long dead window (observed: a launcher-style
        // process with no main window stalled the old 60s wait while the first poll tick's
        // unlocks all got dropped). After the grace we start on the best-guess monitor and
        // correct later if the game window appears somewhere else.
        private const int WindowResolveGraceSeconds = 15;
        private const int WindowResolvePollMs = 2000;
        private const int ToastWaitTimeoutSeconds = 30;
        private const int ToastWaitPollSeconds = 5;
        private const int MaxCaptureRestarts = 3;
        private const int RestartBackoffSeconds = 5;
        // Freeze recovery (distinct from crash restarts): a frozen-but-alive capture is detected by
        // the health watchdog / clip freeze probe, which kill the capture to route it through the
        // restart path. The fast (GPU-resident) path is retried in place first so a transient device
        // hiccup costs nothing; only after ResidentFreezeRetryMax does it drop to the copy-through
        // path. FreezeRestartCount is budgeted separately from crash restarts so spaced-out freezes
        // never disable recording, and both freeze counters reset after HealthyResetMinutes of health.
        private const int ResidentFreezeRetryMax = 2;
        private const int MaxFreezeRestarts = 6;
        private const int FreezeRecoveryCooldownSeconds = 20;
        private const int HealthyResetMinutes = 5;
        private const int PruneIntervalSeconds = 30;
        private const int StopGraceSeconds = 3;
        private const int DrainTimeoutSeconds = 45;
        // Fallbacks matching the PersistedSettings defaults, used when settings are unavailable.
        private const int DefaultPollIntervalSeconds = 15;
        private const int DefaultPreRollSeconds = 15;
        // Sentinel poll interval handed to SelectPrunable to suspend age-based pruning while clips
        // are outstanding; large enough that the retention depth keeps every buffered segment.
        private const int AgePruneSuspendedInterval = 3600;
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
        private readonly List<ClipRequest> _pending = new List<ClipRequest>();
        private readonly Dictionary<string, Task<string>> _inFlightByWindow =
            new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        private readonly HashSet<Task> _inFlightTasks = new HashSet<Task>();
        // Buffer directories owned by a live or still-draining session (guarded by _gate). A new
        // session's stale-buffer cleanup must not delete a previous session's buffer while its
        // pending clips are still being produced.
        private readonly HashSet<string> _liveBufferDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private CaptureSession _session;
        private bool _sessionNotified;
        private bool _disposed;
        // Last time any toast wave went on screen (guarded by _gate). Extends the toast-wait
        // fallback so queued waves far beyond the base timeout still anchor their clips.
        private DateTime _lastWaveDisplayedUtc;

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
            public AudioLoopbackRecorder AudioRecorder;
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
            public string ProviderKey;
            public string GameName;
            public string AchievementName;
            public int AchievementNumber;
            public int TotalCount;
            public DateTime? UnlockTimeUtc;
            public DateTime DetectionUtc;
            public bool IsTestFire;
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
        /// Waits (2s polls, up to 60s) for the game window to become resolvable so the capture is
        /// scoped to the game's monitor, then spawns the rolling ffmpeg capture. Monitor capture
        /// (not window) is deliberate: ffmpeg can't follow a moving window.
        /// </summary>
        private async Task StartCaptureWhenWindowResolvesAsync(CaptureSession session)
        {
            try
            {
                // Crash cleanup off the game-started event thread: deleting a large leftover
                // buffer can take a moment and must not delay game launch handling.
                CleanupStaleBufferDirectories(Path.GetDirectoryName(session.BufferDirectory));

                var token = session.Cts.Token;
                var deadline = DateTime.UtcNow.AddSeconds(WindowResolveTimeoutSeconds);
                var graceDeadline = DateTime.UtcNow.AddSeconds(WindowResolveGraceSeconds);
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
                                         DateTime.UtcNow < graceDeadline;
                    if (!stillLaunching)
                    {
                        bounds = _screenshotService.TryGetGameMonitorBounds(trackedHwnd, processId);
                        if (bounds.HasValue || DateTime.UtcNow >= deadline)
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
                    // Best-effort: a recorder that fails to start is dropped and the clips stay
                    // video-only (the recorder logs its own warning). Game-only audio resolves the
                    // session's live owner pid so a foreground switch retargets on the next session.
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
                    persisted.RecordingResolution, _logger);
                if (!recorder.Start())
                {
                    recorder.Dispose();
                    return false;
                }

                session.WgcRecorder = recorder;
                session.SegmentExtension = RecordingPaths.SegmentFileExtension;
                session.CaptureStartUtc = DateTime.UtcNow;
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

                // Close the current audio chunk before pending clips read the buffer.
                session.AudioRecorder?.Stop();

                // Toasts queued for this session were just cleared; produce any still-pending
                // clips with the no-toast end anchor before the buffer goes away.
                List<ClipRequest> pending;
                lock (_gate)
                {
                    pending = _pending.Where(r => ReferenceEquals(r.Session, session)).ToList();
                    _pending.RemoveAll(r => ReferenceEquals(r.Session, session));
                }

                foreach (var request in pending)
                {
                    _logger?.Debug($"[Recording] Game stopped before a toast for '{request.AchievementName}'; using the detection-anchored clip end.");
                    StartClipProduction(request, toastShownUtc: null);
                }

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
                lock (_gate)
                {
                    // Only this session's dedup entries (keys are prefixed with the session's
                    // buffer dir): a handoff session may already be producing its own clips.
                    var stale = _inFlightByWindow.Keys
                        .Where(key => key.StartsWith(session.BufferDirectory + "|", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var key in stale)
                    {
                        _inFlightByWindow.Remove(key);
                    }
                }

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

        private void OnAchievementUnlocked(object sender, AchievementUnlockedEventArgs e)
        {
            if (_disposed || e == null || e.IsPreview || e.IsFriendUnlock)
            {
                return;
            }

            if (_settings?.Persisted?.EnableUnlockRecordings != true)
            {
                return;
            }

            CaptureSession session;
            lock (_gate)
            {
                session = _session;
            }

            // Active means the WGC-MF recorder is running for this session.
            var captureActive = session != null && !session.Stopping && session.WgcRecorder != null;
            if (!captureActive)
            {
                _logger?.Debug(
                    $"[Recording] Unlock '{e.DisplayName}' ignored; capture is not active (session={(session == null ? "none" : session.Stopping ? "stopping" : "no capture")}).");
                return;
            }

            if (_isProviderRecordingEnabled?.Invoke(e.ProviderKey) == false)
            {
                return;
            }

            var persisted = _settings.Persisted;
            if (!UnlockCaptureRarityFilter.ShouldCapture(
                    e,
                    persisted.UnlockRecordingRarities,
                    persisted.UnlockRecordingAlwaysCaptureCompletion))
            {
                _logger?.Debug(
                    $"[Recording] Unlock '{e.DisplayName}' is below the minimum recording rarity; no clip.");
                return;
            }

            // The buffer only contains the owner game's monitor; an unlock from another running
            // game still gets its toast and screenshot, but a clip of the wrong game is useless.
            if (e.PlayniteGameId != Guid.Empty &&
                session.OwnerGameId != Guid.Empty &&
                e.PlayniteGameId != session.OwnerGameId)
            {
                _logger?.Debug(
                    $"[Recording] Unlock '{e.DisplayName}' is from '{e.GameName}' but the capture follows '{session.GameName}'; toast/screenshot only, no clip.");
                return;
            }

            // A stale timestamp (before this capture session) can't anchor the clip; the timing
            // math falls back to detection-anchored footage so every unlock still gets a clip.
            if (e.UnlockTimeUtc.HasValue && e.UnlockTimeUtc.Value < session.CaptureStartUtc.AddSeconds(-60))
            {
                _logger?.Debug(
                    $"[Recording] Unlock '{e.DisplayName}' has a pre-session timestamp ({e.UnlockTimeUtc.Value:u}); clip will anchor on detection time.");
            }

            var request = new ClipRequest
            {
                Session = session,
                ProviderKey = e.ProviderKey,
                GameName = e.GameName,
                // Resolved through the shared helper so completion notifications (no
                // DisplayName) get the same name the toast wave reports, letting the clip
                // match its wave and carry a sensible filename.
                AchievementName = ViewModels.AchievementToastViewModel.ResolveAchievementName(e),
                AchievementNumber = e.AchievementNumber,
                TotalCount = e.TotalCount,
                UnlockTimeUtc = e.UnlockTimeUtc,
                DetectionUtc = DateTime.UtcNow,
                IsTestFire = e.IsTestFire
            };

            lock (_gate)
            {
                _pending.Add(request);
            }

            _ = Task.Run(() => ToastWaitFallbackAsync(request));
        }

        /// <summary>
        /// End-anchor fallback: the clip is produced detection-anchored only after 30s of toast
        /// SILENCE (no wave displayed at all), not 30s after detection. A burst of unlocks queues
        /// many waves that display far beyond 30s; as long as waves keep appearing, later
        /// requests keep waiting so their clip tail stretches to include their own toast popping.
        /// </summary>
        private async Task ToastWaitFallbackAsync(ClipRequest request)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(ToastWaitPollSeconds), request.Session.Cts.Token)
                        .ConfigureAwait(false);

                    bool pending;
                    DateTime lastWaveUtc;
                    lock (_gate)
                    {
                        pending = _pending.Contains(request);
                        lastWaveUtc = _lastWaveDisplayedUtc;
                    }

                    if (!pending)
                    {
                        return;
                    }

                    var silenceAnchor = lastWaveUtc > request.DetectionUtc ? lastWaveUtc : request.DetectionUtc;
                    if ((DateTime.UtcNow - silenceAnchor).TotalSeconds >= ToastWaitTimeoutSeconds)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Session shut down; ShutdownSessionAsync already drained pending requests.
            }

            bool stillPending;
            lock (_gate)
            {
                stillPending = _pending.Remove(request);
            }

            if (!stillPending)
            {
                return;
            }

            _logger?.Debug(
                $"[Recording] No toast after {ToastWaitTimeoutSeconds}s of toast silence for '{request.AchievementName}'; using the detection-anchored clip end.");
            StartClipProduction(request, toastShownUtc: null);
        }

        private void OnToastWaveDisplayed(object sender, ToastWaveDisplayedEventArgs e)
        {
            if (_disposed || e?.Wave == null || e.Wave.Count == 0)
            {
                return;
            }

            var matches = new List<ClipRequest>();
            lock (_gate)
            {
                _lastWaveDisplayedUtc = DateTime.UtcNow;
                foreach (var vm in e.Wave)
                {
                    var match = _pending.FirstOrDefault(r =>
                        string.Equals(r.ProviderKey, vm.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.AchievementName, vm.AchievementName, StringComparison.Ordinal));
                    if (match != null)
                    {
                        _pending.Remove(match);
                        matches.Add(match);
                    }
                }
            }

            foreach (var request in matches)
            {
                StartClipProduction(request, e.ShownUtc);
            }
        }

        // === Clip production ===

        private void StartClipProduction(ClipRequest request, DateTime? toastShownUtc)
        {
            // The clip end follows the observed toast however late it appears (queued behind a
            // burst of other waves, or held until the game regains focus), so multi-wave unlocks
            // still capture their own toast. ToastWaitFallbackAsync supplies a detection-anchored
            // end only when no toast ever shows.
            var task = Task.Run(() => ProduceClipAsync(request, toastShownUtc));
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

        private async Task ProduceClipAsync(ClipRequest request, DateTime? toastShownUtc)
        {
            try
            {
                var session = request.Session;
                var persisted = _settings?.Persisted;
                if (persisted == null)
                {
                    return;
                }

                var pollInterval = Math.Max(10, persisted.InGamePollIntervalSeconds);
                var (windowStart, windowEnd) = SegmentTimeline.ComputeClipWindow(
                    request.UnlockTimeUtc,
                    request.DetectionUtc,
                    toastShownUtc,
                    session.CaptureStartUtc,
                    oldestSegmentStartUtc: null,
                    pollIntervalSeconds: pollInterval,
                    preRollSeconds: persisted.RecordingClipSeconds,
                    toastVisibleSeconds: Math.Max(2, persisted.ToastDurationSeconds));

                if ((windowEnd - windowStart).TotalSeconds < SegmentTimeline.MinimumWindowSeconds)
                {
                    _logger?.Debug(
                        $"[Recording] Clip window for '{request.AchievementName}' collapsed below {SegmentTimeline.MinimumWindowSeconds}s; skipping.");
                    return;
                }

                var outputPath = BuildOutputPath(persisted, request);
                if (outputPath == null)
                {
                    return;
                }

                // One encode per distinct clip window: a burst of unlocks in one wave shares one
                // ffmpeg run and the duplicates copy the finished file.
                var key = BuildWindowKey(session, windowStart, windowEnd);
                Task<string> producer = null;
                TaskCompletionSource<string> owner = null;
                lock (_gate)
                {
                    if (!_inFlightByWindow.TryGetValue(key, out producer))
                    {
                        owner = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _inFlightByWindow[key] = owner.Task;
                    }
                }

                if (owner == null)
                {
                    var producedPath = await producer.ConfigureAwait(false);
                    if (producedPath != null && SafeFileExists(producedPath))
                    {
                        var copied = SaveClipToUniquePath(producedPath, outputPath, copy: true);
                        if (copied != null)
                        {
                            _logger?.Info($"[Recording] Saved unlock clip (shared window copy): {copied}");
                        }
                    }

                    return;
                }

                string result = null;
                try
                {
                    result = await EncodeClipAsync(session, request, toastShownUtc, windowStart, windowEnd, outputPath)
                        .ConfigureAwait(false);
                }
                finally
                {
                    owner.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[Recording] Clip production failed for '{request?.AchievementName}'.");
            }
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

        private async Task<string> EncodeClipAsync(
            CaptureSession session,
            ClipRequest request,
            DateTime? toastShownUtc,
            DateTime windowStart,
            DateTime windowEnd,
            string outputPath)
        {
            // Wait until the segment covering the window end has closed (K + margin) so the
            // concat never reads a half-written segment.
            var readyAtUtc = windowEnd.AddSeconds(SegmentSeconds + 2);
            var wait = readyAtUtc - DateTime.UtcNow;
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
            var plan = SegmentTimeline.PlanClip(segments, windowStart, windowEnd, SegmentSeconds);
            if (plan == null)
            {
                _logger?.Debug($"[Recording] No buffered segments overlap the clip window for '{request.AchievementName}'; skipping.");
                return null;
            }

            // Audio rides the same window: plan the loopback WAV chunks over it and fall back to
            // video-only whenever the recorder never ran or no chunk overlaps.
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
                audioPlan = SegmentTimeline.PlanClip(audioChunks, windowStart, windowEnd, SegmentSeconds);
            }

            LogRecordingTiming(session, request, toastShownUtc, windowStart, windowEnd, plan.Segments.Count, audioPlan != null);

            var tempPath = Path.Combine(session.BufferDirectory, $"clip_{Guid.NewGuid():N}.mp4");
            try
            {
                // Concatenate + trim the buffered segments and mux the loopback audio with Media
                // Foundation (stream-copy video, PCM->AAC audio) — no ffmpeg. WGC already captures the
                // client area at the target resolution, so no crop/re-encode is needed.
                var exporter = new MediaFoundationClipExporter(_logger);
                var ok = await Task.Run(() => exporter.Export(plan, audioPlan, tempPath)).ConfigureAwait(false);
                if (!ok)
                {
                    _logger?.Warn($"[Recording] Clip export failed for '{request.AchievementName}'.");
                    return null;
                }

                var savedPath = SaveClipToUniquePath(tempPath, outputPath, copy: false);
                if (savedPath == null)
                {
                    _logger?.Warn($"[Recording] Could not place unlock clip for '{request.AchievementName}' (destination in use).");
                    return null;
                }

                _logger?.Info($"[Recording] Saved unlock clip: {savedPath}");
                // Drop the cached capture scan for this game so an already-open grid picks up the
                // new clip on its next rebuild.
                PlayniteAchievementsPlugin.Instance?.CaptureLibraryService?.Invalidate(request.GameName);
                return savedPath;
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        /// <summary>
        /// The per-clip timing line (Info) that makes refresh-latency-driven clip stretching
        /// visible in the plugin log.
        /// </summary>
        private void LogRecordingTiming(
            CaptureSession session,
            ClipRequest request,
            DateTime? toastShownUtc,
            DateTime windowStart,
            DateTime windowEnd,
            int segmentCount,
            bool hasAudio)
        {
            try
            {
                var precise = SegmentTimeline.IsPreciseUnlockTime(
                    request.UnlockTimeUtc, session.CaptureStartUtc, request.DetectionUtc);
                var unlockText = precise ? Stamp(request.UnlockTimeUtc.Value) : "coarse";
                var unlockToDetect = precise
                    ? (request.DetectionUtc - request.UnlockTimeUtc.Value).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
                    : "?";
                var toastText = toastShownUtc.HasValue ? Stamp(toastShownUtc.Value) : "none";
                var detectToToast = toastShownUtc.HasValue
                    ? (toastShownUtc.Value - request.DetectionUtc).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
                    : "?";
                _logger?.Info(
                    $"[RecordingTiming] unlock={unlockText} detected={Stamp(request.DetectionUtc)} " +
                    $"(unlock→detect {unlockToDetect}s) toastShown={toastText} (detect→toast {detectToToast}s) " +
                    $"window=[{Stamp(windowStart)}..{Stamp(windowEnd)}] ({(windowEnd - windowStart).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s) " +
                    $"segments={segmentCount} audio={(hasAudio ? "yes" : "no")}");

                // Verification: the toast's full display time must sit inside the clip window.
                var toastVisible = Math.Max(2, _settings?.Persisted?.ToastDurationSeconds ?? 6);
                if (toastShownUtc.HasValue && toastShownUtc.Value.AddSeconds(toastVisible) > windowEnd)
                {
                    _logger?.Debug("[RecordingTiming] toast display extends past the window end; the toast may be cut off in the clip.");
                }
            }
            catch
            {
            }
        }

        private static string Stamp(DateTime utc)
        {
            return utc.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
        }

        private static string BuildWindowKey(CaptureSession session, DateTime start, DateTime end)
        {
            // Rounded to 2s so a burst of unlocks detected milliseconds apart shares one encode.
            const long twoSeconds = 2 * TimeSpan.TicksPerSecond;
            var s = (long)Math.Round(start.Ticks / (double)twoSeconds);
            var e = (long)Math.Round(end.Ticks / (double)twoSeconds);
            return $"{session.BufferDirectory}|{s}|{e}";
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
                // While clip requests are waiting (e.g. many toast waves queued) or encodes are
                // running, age-based pruning would delete the very segments those clips need —
                // a late wave's window reaches back to its detection time. Pause the age policy
                // and keep only the byte cap until the pipeline is idle again.
                bool clipsOutstanding;
                lock (_gate)
                {
                    clipsOutstanding = _pending.Count > 0 || _inFlightByWindow.Count > 0;
                }

                var persisted = _settings?.Persisted;
                var pollInterval = Math.Max(10, persisted?.InGamePollIntervalSeconds ?? DefaultPollIntervalSeconds);
                var preRoll = persisted?.RecordingClipSeconds ?? DefaultPreRollSeconds;
                // Suspend age-based pruning while clips are outstanding: a clip waiting for a late
                // toast reaches back to its pre-roll and forward to that toast, so the segments it
                // needs must survive. The byte cap still applies. Video and audio share the policy.
                var retentionInterval = clipsOutstanding ? AgePruneSuspendedInterval : pollInterval;

                var segments = SegmentTimeline.ParseSegments(
                    ListBufferFiles(
                        session.BufferDirectory,
                        RecordingPaths.SegmentFilePrefix,
                        session.SegmentExtension),
                    TimeZoneInfo.Local,
                    RecordingPaths.SegmentFilePrefix,
                    session.SegmentExtension);
                LogCaptureHealth(session, segments);
                foreach (var segment in SegmentTimeline.SelectPrunable(
                             segments, retentionInterval, preRoll, SegmentSeconds, MaxBufferBytes))
                {
                    TryDeleteFile(segment.Path);
                }

                // Audio chunks share the retention policy (their bytes are negligible next to the
                // video's, so reusing the same cap is safe).
                var audioChunks = SegmentTimeline.ParseSegments(
                    ListBufferFiles(
                        session.BufferDirectory,
                        RecordingPaths.AudioChunkFilePrefix,
                        RecordingPaths.AudioChunkFileExtension),
                    TimeZoneInfo.Local,
                    RecordingPaths.AudioChunkFilePrefix,
                    RecordingPaths.AudioChunkFileExtension);
                foreach (var chunk in SegmentTimeline.SelectPrunable(
                             audioChunks, retentionInterval, preRoll, SegmentSeconds, MaxBufferBytes))
                {
                    TryDeleteFile(chunk.Path);
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
        /// Diagnostic only: a per-prune-tick capture-health line. Warns when the recorder has stopped
        /// opening new segments — a stalled capture that leaves an unlock with no footage ("no
        /// buffered segments overlap the clip window"). The WGC recorder duplicates the last frame at
        /// a constant rate, so segments should always advance; a stall here means the recorder itself
        /// wedged. Never throws.
        /// </summary>
        private void LogCaptureHealth(CaptureSession session, IReadOnlyList<SegmentTimeline.SegmentInfo> segments)
        {
            try
            {
                if (session == null || session.Stopping || session.WgcRecorder == null)
                {
                    return;
                }

                var now = DateTime.UtcNow;
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

                var line =
                    $"[RecordingHealth] '{session.GameName}': segments={segments.Count} " +
                    $"newestAge={(now - newest.StartUtc).TotalSeconds:F0}s sinceNewSegment={sinceNewSegment:F0}s " +
                    $"lastClosed={(lastClosed?.SizeBytes ?? 0) / 1024}KB peak={session.MaxSegmentBytes / 1024}KB";

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

        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
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
            }

            if (_windowTracker != null)
            {
                _windowTracker.StableForegroundGameChanged -= OnStableForegroundGameChanged;
            }

            CaptureSession session;
            lock (_gate)
            {
                session = _session;
                _session = null;
                _pending.Clear();
                _inFlightByWindow.Clear();
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
            }
        }
    }
}
