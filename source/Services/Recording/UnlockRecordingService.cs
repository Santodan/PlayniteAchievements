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
using PlayniteAchievements.Models.Settings;
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
        // Longest detection-to-toast gap a clip's end anchor may honor; later toasts fall back to
        // the detection anchor so queued/held toast waves can't stretch clips indefinitely.
        private const int MaxToastAnchorDelaySeconds = 30;
        private const int MaxCaptureRestarts = 3;
        private const int RestartBackoffSeconds = 5;
        private const int PruneIntervalSeconds = 30;
        private const int StopGraceSeconds = 3;
        private const int DrainTimeoutSeconds = 45;
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
        private readonly FfmpegValidationService _validation;

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
        private WindowsJobObject _jobObject;
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
            _validation = new FfmpegValidationService(logger);

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
            public string FfmpegPath;
            public string CaptureArguments;
            public string EncoderArguments;
            public RecordingCaptureBackend Backend;
            /// <summary>True when Backend came from the Auto setting, allowing the ddagrab
            /// crash path to downgrade to gdigrab instead of retrying an explicit choice.</summary>
            public bool BackendAutoResolved;
            public System.Drawing.Rectangle MonitorBounds;
            public Guid OwnerGameId;
            public string GameName;
            public DateTime CaptureStartUtc;
            public FfmpegProcessHost CaptureHost;
            public AudioLoopbackRecorder AudioRecorder;
            public CancellationTokenSource Cts;
            public Timer PruneTimer;
            public int RestartCount;
            public volatile bool Stopping;
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

            var ffmpegPath = persisted.FfmpegPath?.Trim();
            var outputDir = ResolveOutputDirectory(persisted);
            if (string.IsNullOrEmpty(ffmpegPath) || !SafeFileExists(ffmpegPath) || string.IsNullOrWhiteSpace(outputDir))
            {
                _logger?.Warn("[Recording] Unlock recordings are enabled but the ffmpeg path or output folder is missing/invalid; skipping this session.");
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
                FfmpegPath = ffmpegPath,
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
        /// Follows the user's attention between running games. Same monitor as the current
        /// capture: cheap switch — the ffmpeg process and rolling buffer are kept and only the
        /// session's owner flips, so clip gating and cropping immediately target the new game.
        /// Different monitor: full handoff (fresh session and buffer) since the buffer footage
        /// shows the wrong screen. The tracker debounces, so alt-tab flicker never lands here.
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

                var hwnd = _windowTracker?.TryGetWindowHandle(e.Game.Id) ?? IntPtr.Zero;
                var newBounds = _screenshotService.TryGetGameMonitorBounds(
                    hwnd,
                    _getGameProcessId?.Invoke(e.Game.Id));

                if (session.CaptureHost != null &&
                    newBounds.HasValue &&
                    newBounds.Value != session.MonitorBounds)
                {
                    lock (_gate)
                    {
                        // A concurrent start/stop may have replaced the session since it was
                        // observed; restarting on top of the replacement would kill it.
                        if (!ReferenceEquals(_session, session) || session.Stopping)
                        {
                            return;
                        }
                    }

                    _logger?.Info(
                        $"[Recording] Foreground moved to '{e.Game.Name}' on monitor {newBounds.Value}; restarting capture there.");
                    OnGameStarted(e.Game);
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

                _logger?.Info($"[Recording] Capture owner switched to '{e.Game.Name}' (same monitor, no restart).");
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

                var encoderArgs = await ResolveEncoderArgumentsAsync(session.FfmpegPath, persisted.RecordingEncoder)
                    .ConfigureAwait(false);
                var backend = await ResolveBackendAsync(session.FfmpegPath, persisted.RecordingCaptureBackend)
                    .ConfigureAwait(false);

                session.EncoderArguments = encoderArgs;
                session.Backend = backend;
                session.BackendAutoResolved = persisted.RecordingCaptureBackend == RecordingCaptureBackend.Auto;
                session.MonitorBounds = bounds.Value;
                session.CaptureArguments = BuildCaptureArgumentsFor(session, persisted, bounds.Value);

                Directory.CreateDirectory(session.BufferDirectory);
                if (session.Stopping || !SpawnCapture(session))
                {
                    return;
                }

                if (persisted.RecordingIncludeAudio)
                {
                    // Best-effort: a recorder that fails to start is dropped and the clips stay
                    // video-only (the recorder logs its own warning).
                    var recorder = new AudioLoopbackRecorder(session.BufferDirectory, _logger);
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
                    $"[Recording] Capture started for '{session.GameName}' on monitor {bounds.Value} ({backend}, {encoderArgs}), buffer={session.BufferDirectory}.");

                // Capture started from the fallback (foreground) monitor before the game window
                // existed: keep watching, and if the game window appears on a different monitor,
                // restart the capture there.
                if (!mainWindowResolved)
                {
                    _ = Task.Run(() => CorrectMonitorWhenWindowAppearsAsync(session, bounds.Value, deadline));
                }
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

        private string BuildCaptureArgumentsFor(
            CaptureSession session,
            PersistedSettings persisted,
            System.Drawing.Rectangle bounds)
        {
            return RecordingCommandBuilder.BuildCaptureArguments(
                new RecordingCommandBuilder.CaptureOptions
                {
                    Backend = session.Backend,
                    Fps = persisted.RecordingFps,
                    MonitorX = bounds.X,
                    MonitorY = bounds.Y,
                    MonitorWidth = bounds.Width,
                    MonitorHeight = bounds.Height,
                    MonitorIndex = ResolveMonitorIndex(bounds),
                    Resolution = persisted.RecordingResolution,
                    EncoderArguments = session.EncoderArguments,
                    SegmentSeconds = SegmentSeconds,
                    BufferDirectory = session.BufferDirectory
                });
        }

        /// <summary>
        /// Runs after a capture that started before the game window existed. If the game's main
        /// window appears (within the resolve deadline) on a different monitor than the one being
        /// captured, the ffmpeg capture is restarted on the correct monitor. Segments recorded on
        /// the wrong monitor age out of the buffer naturally.
        /// </summary>
        private async Task CorrectMonitorWhenWindowAppearsAsync(
            CaptureSession session,
            System.Drawing.Rectangle capturedBounds,
            DateTime deadlineUtc)
        {
            try
            {
                var token = session.Cts.Token;
                while (!token.IsCancellationRequested && DateTime.UtcNow < deadlineUtc)
                {
                    await Task.Delay(WindowResolvePollMs, token).ConfigureAwait(false);

                    var trackedHwnd = _windowTracker?.TryGetWindowHandle(session.OwnerGameId) ?? IntPtr.Zero;
                    var processId = _getGameProcessId?.Invoke(session.OwnerGameId);
                    if (trackedHwnd == IntPtr.Zero &&
                        (!processId.HasValue || !ProcessHasMainWindow(processId.Value)))
                    {
                        continue;
                    }

                    var resolved = _screenshotService.TryGetGameMonitorBounds(trackedHwnd, processId);
                    if (!resolved.HasValue || resolved.Value == capturedBounds)
                    {
                        return;
                    }

                    var persisted = _settings?.Persisted;
                    if (persisted == null || session.Stopping)
                    {
                        return;
                    }

                    _logger?.Info(
                        $"[Recording] Game window appeared on {resolved.Value}; restarting capture from fallback monitor {capturedBounds}.");

                    // Detach the old host first so its Exited handler doesn't count the swap as a
                    // crash, then kill it and spawn on the correct monitor.
                    var oldHost = session.CaptureHost;
                    session.CaptureHost = null;
                    try
                    {
                        oldHost?.Dispose();
                    }
                    catch
                    {
                    }

                    session.MonitorBounds = resolved.Value;
                    session.CaptureArguments = BuildCaptureArgumentsFor(session, persisted, resolved.Value);
                    if (!session.Stopping)
                    {
                        SpawnCapture(session);
                    }

                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Game stopped while watching.
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Monitor correction watcher failed.");
            }
        }

        private bool SpawnCapture(CaptureSession session)
        {
            var host = new FfmpegProcessHost(session.FfmpegPath, session.CaptureArguments, _logger);
            host.Exited += (s, e) => OnCaptureExited(session, host);
            if (!host.Start(EnsureJobObject()))
            {
                host.Dispose();
                _logger?.Warn("[Recording] ffmpeg capture process failed to start.");
                return false;
            }

            session.CaptureStartUtc = DateTime.UtcNow;
            session.CaptureHost = host;
            return true;
        }

        /// <summary>
        /// Capture crash recovery: up to 3 restarts with a 5s backoff, then the session is
        /// disabled with one notification. The stderr tail is logged for diagnosis.
        /// </summary>
        private void OnCaptureExited(CaptureSession session, FfmpegProcessHost host)
        {
            if (_disposed || session.Stopping || !ReferenceEquals(session.CaptureHost, host))
            {
                return;
            }

            var tail = host.StdErrTail;
            session.RestartCount++;
            if (session.RestartCount > MaxCaptureRestarts)
            {
                _logger?.Warn(
                    $"[Recording] ffmpeg capture exited {session.RestartCount} times; disabling recording for this session. stderr tail:\n{tail}");
                session.Stopping = true;
                NotifyRecordingUnavailableOnce();
                return;
            }

            // An Auto-resolved ddagrab capture that dies (RDP session, hybrid GPU, driver
            // quirks) restarts as gdigrab instead of retrying the same failing backend; an
            // explicit Ddagrab selection keeps retrying the user's choice.
            if (session.Backend == RecordingCaptureBackend.Ddagrab && session.BackendAutoResolved)
            {
                var persisted = _settings?.Persisted;
                if (persisted != null)
                {
                    session.Backend = RecordingCaptureBackend.Gdigrab;
                    session.CaptureArguments = BuildCaptureArgumentsFor(session, persisted, session.MonitorBounds);
                    _logger?.Info("[Recording] ddagrab capture failed; falling back to gdigrab for this session.");
                }
            }

            _logger?.Warn(
                $"[Recording] ffmpeg capture exited unexpectedly (exit={host.ExitCode}); restart {session.RestartCount}/{MaxCaptureRestarts} in {RestartBackoffSeconds}s. stderr tail:\n{tail}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(RestartBackoffSeconds), session.Cts.Token).ConfigureAwait(false);
                    if (!_disposed && !session.Stopping)
                    {
                        host.Dispose();
                        SpawnCapture(session);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[Recording] Capture restart failed.");
                }
            });
        }

        private async Task ShutdownSessionAsync(CaptureSession session)
        {
            try
            {
                var host = session.CaptureHost;
                if (host != null)
                {
                    await host.StopGracefullyAsync(TimeSpan.FromSeconds(StopGraceSeconds)).ConfigureAwait(false);
                }

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

                host?.Dispose();
                session.CaptureHost = null;
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

            if (session == null || session.Stopping || session.CaptureHost == null || session.CaptureHost.HasExited)
            {
                _logger?.Debug(
                    $"[Recording] Unlock '{e.DisplayName}' ignored; capture is not active (session={(session == null ? "none" : session.Stopping ? "stopping" : "no capture process")}).");
                return;
            }

            if (_isProviderRecordingEnabled?.Invoke(e.ProviderKey) == false)
            {
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
                DetectionUtc = DateTime.UtcNow
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
            // A toast can display long after detection (queued behind a burst of other waves, or
            // held until the game regains focus). Footage between detection and such a late toast
            // is unrelated gameplay that only bloats the clip, so a too-late toast falls back to
            // the detection anchor instead of stretching the clip to a minute or more.
            if (toastShownUtc.HasValue &&
                (toastShownUtc.Value - request.DetectionUtc).TotalSeconds > MaxToastAnchorDelaySeconds)
            {
                _logger?.Debug(
                    $"[Recording] Toast for '{request.AchievementName}' displayed {(toastShownUtc.Value - request.DetectionUtc).TotalSeconds:F0}s after detection; anchoring the clip on detection instead.");
                toastShownUtc = null;
            }

            var task = Task.Run(() => ProduceClipAsync(request, toastShownUtc));
            lock (_gate)
            {
                _inFlightTasks.Add(task);
            }

            task.ContinueWith(
                t =>
                {
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
                        File.Copy(producedPath, outputPath);
                        _logger?.Info($"[Recording] Saved unlock clip (shared window copy): {outputPath}");
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
                    RecordingCommandBuilder.SegmentFilePrefix,
                    RecordingCommandBuilder.SegmentFileExtension),
                TimeZoneInfo.Local);
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
                        RecordingCommandBuilder.AudioChunkFilePrefix,
                        RecordingCommandBuilder.AudioChunkFileExtension),
                    TimeZoneInfo.Local,
                    RecordingCommandBuilder.AudioChunkFilePrefix,
                    RecordingCommandBuilder.AudioChunkFileExtension);
                audioPlan = SegmentTimeline.PlanClip(audioChunks, windowStart, windowEnd, SegmentSeconds);
            }

            LogRecordingTiming(session, request, toastShownUtc, windowStart, windowEnd, plan.Segments.Count, audioPlan != null);

            var listPath = Path.Combine(session.BufferDirectory, $"clip_{Guid.NewGuid():N}.txt");
            var audioListPath = audioPlan != null
                ? Path.Combine(session.BufferDirectory, $"clipaud_{Guid.NewGuid():N}.txt")
                : null;
            var tempPath = Path.Combine(session.BufferDirectory, $"clip_{Guid.NewGuid():N}.mp4");
            try
            {
                File.WriteAllText(
                    listPath,
                    RecordingCommandBuilder.BuildConcatListContent(plan.Segments.Select(s => s.Path)));
                if (audioPlan != null)
                {
                    File.WriteAllText(
                        audioListPath,
                        RecordingCommandBuilder.BuildConcatListContent(audioPlan.Segments.Select(s => s.Path)));
                }

                // Crop the export to the game window's client area (like screenshots) when the
                // window doesn't already fill the monitor. Cropping forces a re-encode, so a
                // fullscreen game keeps the cheap stream copy.
                var crop = ResolveCropRectangle(session);

                // Ladder: preferred form first, then degrade — re-encode retry, then uncropped,
                // then (below) audio-less. A worse clip always beats no clip.
                var ok = await RunTrimAsync(session, listPath, plan, audioListPath, audioPlan, tempPath, reencode: false, crop)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    TryDeleteFile(tempPath);
                    ok = await RunTrimAsync(session, listPath, plan, audioListPath, audioPlan, tempPath, reencode: true, crop)
                        .ConfigureAwait(false);
                }

                if (!ok && crop.HasValue)
                {
                    _logger?.Debug($"[Recording] Cropped export failed for '{request.AchievementName}'; retrying uncropped.");
                    TryDeleteFile(tempPath);
                    crop = null;
                    ok = await RunTrimAsync(session, listPath, plan, audioListPath, audioPlan, tempPath, reencode: false, crop)
                        .ConfigureAwait(false);
                    if (!ok)
                    {
                        TryDeleteFile(tempPath);
                        ok = await RunTrimAsync(session, listPath, plan, audioListPath, audioPlan, tempPath, reencode: true, crop)
                            .ConfigureAwait(false);
                    }
                }

                if (!ok && audioPlan != null)
                {
                    // Audio must never cost the clip: retry the whole ladder without it.
                    _logger?.Debug($"[Recording] Clip export with audio failed for '{request.AchievementName}'; retrying video-only.");
                    TryDeleteFile(tempPath);
                    ok = await RunTrimAsync(session, listPath, plan, null, null, tempPath, reencode: false, crop)
                        .ConfigureAwait(false);
                    if (!ok)
                    {
                        TryDeleteFile(tempPath);
                        ok = await RunTrimAsync(session, listPath, plan, null, null, tempPath, reencode: true, crop)
                            .ConfigureAwait(false);
                    }
                }

                if (!ok)
                {
                    _logger?.Warn($"[Recording] Clip export failed for '{request.AchievementName}' (copy and re-encode).");
                    return null;
                }

                File.Move(tempPath, outputPath);
                _logger?.Info($"[Recording] Saved unlock clip: {outputPath}");
                return outputPath;
            }
            finally
            {
                TryDeleteFile(listPath);
                if (audioListPath != null)
                {
                    TryDeleteFile(audioListPath);
                }

                TryDeleteFile(tempPath);
            }
        }

        /// <summary>
        /// Crop region for exports: the game window's client rect (same Win32 resolution the
        /// screenshots use) mapped into the captured video's pixel space. Null when the window
        /// can't be resolved, fills the monitor, or the game already exited.
        /// </summary>
        private System.Drawing.Rectangle? ResolveCropRectangle(CaptureSession session)
        {
            try
            {
                var trackedHwnd = _windowTracker?.TryGetWindowHandle(session.OwnerGameId) ?? IntPtr.Zero;
                var processId = _getGameProcessId?.Invoke(session.OwnerGameId);
                var client = _screenshotService.TryGetGameWindowBounds(trackedHwnd, processId);
                if (client == null)
                {
                    return null;
                }

                return RecordingCommandBuilder.ComputeCropRectangle(
                    session.MonitorBounds,
                    client.Value,
                    _settings?.Persisted?.RecordingResolution ?? RecordingResolution.Native);
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> RunTrimAsync(
            CaptureSession session,
            string listPath,
            SegmentTimeline.ClipPlan plan,
            string audioListPath,
            SegmentTimeline.ClipPlan audioPlan,
            string tempPath,
            bool reencode,
            System.Drawing.Rectangle? crop = null)
        {
            var arguments = RecordingCommandBuilder.BuildTrimArguments(
                listPath,
                plan.StartOffsetSeconds,
                plan.DurationSeconds,
                tempPath,
                reencode,
                audioListPath,
                audioPlan?.StartOffsetSeconds ?? 0,
                crop,
                session.EncoderArguments);
            using (var host = new FfmpegProcessHost(session.FfmpegPath, arguments, _logger))
            {
                if (!host.Start(EnsureJobObject()))
                {
                    return false;
                }

                var timeout = TimeSpan.FromSeconds(Math.Max(30, plan.DurationSeconds) + 60);
                var exitCode = await host.WaitForExitAsync(timeout).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    _logger?.Debug($"[Recording] ffmpeg trim exited with {exitCode} (reencode={reencode}): {host.StdErrTail}");
                    return false;
                }
            }

            try
            {
                var info = new FileInfo(tempPath);
                return info.Exists && info.Length > 0;
            }
            catch
            {
                return false;
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
                var pollInterval = Math.Max(10, persisted?.InGamePollIntervalSeconds ?? 15);
                var segments = SegmentTimeline.ParseSegments(
                    ListBufferFiles(
                        session.BufferDirectory,
                        RecordingCommandBuilder.SegmentFilePrefix,
                        RecordingCommandBuilder.SegmentFileExtension),
                    TimeZoneInfo.Local);
                // 3600 stands in for "don't age-prune" without overflowing the 3x depth math;
                // the byte cap below still applies.
                var prunable = SegmentTimeline.SelectPrunable(
                    segments,
                    clipsOutstanding ? 3600 : pollInterval,
                    persisted?.RecordingClipSeconds ?? 15,
                    SegmentSeconds,
                    MaxBufferBytes);
                foreach (var segment in prunable)
                {
                    TryDeleteFile(segment.Path);
                }

                // Audio chunks share the retention policy (their bytes are negligible next to
                // the video's, so reusing the same cap is safe).
                var audioChunks = SegmentTimeline.ParseSegments(
                    ListBufferFiles(
                        session.BufferDirectory,
                        RecordingCommandBuilder.AudioChunkFilePrefix,
                        RecordingCommandBuilder.AudioChunkFileExtension),
                    TimeZoneInfo.Local,
                    RecordingCommandBuilder.AudioChunkFilePrefix,
                    RecordingCommandBuilder.AudioChunkFileExtension);
                foreach (var chunk in SegmentTimeline.SelectPrunable(
                             audioChunks,
                             pollInterval,
                             persisted?.RecordingClipSeconds ?? 15,
                             SegmentSeconds,
                             MaxBufferBytes))
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
                    var host = session.CaptureHost;
                    if (host != null)
                    {
                        _ = Task.Run(() => host.StopGracefullyAsync(TimeSpan.FromSeconds(StopGraceSeconds)));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Prune tick failed.");
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

        private async Task<string> ResolveEncoderArgumentsAsync(string ffmpegPath, RecordingEncoder encoder)
        {
            if (encoder != RecordingEncoder.Auto)
            {
                return RecordingCommandBuilder.BuildEncoderArguments(encoder, null);
            }

            try
            {
                var result = await _validation.ValidateAsync(ffmpegPath).ConfigureAwait(false);
                return RecordingCommandBuilder.BuildEncoderArguments(RecordingEncoder.Auto, result?.AvailableEncoders);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Encoder probe failed; defaulting to libx264.");
                return RecordingCommandBuilder.BuildEncoderArguments(RecordingEncoder.Auto, null);
            }
        }

        private async Task<RecordingCaptureBackend> ResolveBackendAsync(string ffmpegPath, RecordingCaptureBackend configured)
        {
            if (configured != RecordingCaptureBackend.Auto)
            {
                return configured;
            }

            try
            {
                // Cached per path, so this shares the probe run by the encoder resolution.
                var result = await _validation.ValidateAsync(ffmpegPath).ConfigureAwait(false);
                return RecordingCommandBuilder.ResolveBackend(configured, result?.SupportsDdagrab == true);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Recording] Capture backend probe failed; defaulting to gdigrab.");
                return RecordingCaptureBackend.Gdigrab;
            }
        }

        private static string ResolveOutputDirectory(PersistedSettings persisted)
        {
            var directory = persisted?.UnlockRecordingDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = persisted?.UnlockScreenshotDirectory;
            }

            return string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        }

        private WindowsJobObject EnsureJobObject()
        {
            lock (_gate)
            {
                return _jobObject ?? (_jobObject = new WindowsJobObject());
            }
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

        private static int ResolveMonitorIndex(System.Drawing.Rectangle monitorBounds)
        {
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                for (var i = 0; i < screens.Length; i++)
                {
                    if (screens[i].Bounds == monitorBounds)
                    {
                        return i;
                    }
                }
            }
            catch
            {
            }

            return 0;
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

        private void NotifyRecordingUnavailableOnce()
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
                session.CaptureHost?.Dispose();
                session.AudioRecorder?.Dispose();
            }

            // Closing the job object kills any ffmpeg process that somehow survived disposal.
            _jobObject?.Dispose();
            _jobObject = null;
        }
    }
}
