using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Services.UI
{
    internal sealed class StableForegroundGameChangedEventArgs : EventArgs
    {
        public StableForegroundGameChangedEventArgs(Game game)
        {
            Game = game;
        }

        public Game Game { get; }
    }

    /// <summary>
    /// Maps windows to running Playnite games so screenshots, toasts, and video capture follow the
    /// game the user is actually playing.
    ///
    /// The source of truth is one synchronous question — "which tracked game owns the current
    /// foreground window?" — answered on demand by <see cref="IsGameForeground"/> (a
    /// GetForegroundWindow syscall plus a cached pid lookup). Classification of an unseen
    /// process id (started-pid match, executable path under the game's install directory,
    /// bounded parent-process walk) runs once per pid and is cached until the tracked set
    /// changes.
    ///
    /// Which window is "the game's" is a separate question, and a game rarely has only one
    /// candidate: for a launcher-wrapped title the process Playnite started is the launcher, so the
    /// launcher's own window classifies as the game just as the game's does, and a title such as
    /// Shenmue I &amp; II ships its game-picker launcher inside the same install folder as the games.
    /// Candidates are therefore ranked (see <see cref="GameWindowRanking"/>) rather than taken
    /// first-found, and the desktop is re-scanned for as long as the game runs, so a window
    /// resolved before the game had drawn anything is a starting point rather than the answer for
    /// the whole session. Nothing is treated as settled, because every signal available at launch
    /// can favour a launcher window and only stop doing so once the game's own window exists.
    ///
    /// Only when two or more games run at once does a light poll (every
    /// <see cref="MultiGamePollMs"/> ms) watch for the foreground moving between games, raising
    /// <see cref="StableForegroundGameChanged"/> after the same game holds focus for
    /// <see cref="StableConfirmationPolls"/> consecutive polls so alt-tab flicker never thrashes
    /// consumers that restart video capture on switch. With a single game running there is
    /// no background work at all.
    /// </summary>
    internal sealed class ActiveGameWindowTracker : IDisposable
    {
        private const int MultiGamePollMs = 3000;
        private const int StableConfirmationPolls = 2;
        private const int MaxParentChainDepth = 10;
        // How often the desktop is re-scanned for a better window while the learned one is not yet
        // conclusive. Each scan is one EnumWindows pass over cached pid classifications, so this is
        // cheap; it is throttled because the recorder asks for the handle every second.
        private const int RediscoverIntervalMs = 3000;
        // Below this, in either dimension, a window is a stub — a minimized window parked off
        // screen, a message-only helper, a splash sliver — not a surface anything is played on.
        private const int MinCandidateDimension = 120;

        private sealed class TrackedGame
        {
            public Game Game;
            public int? StartedProcessId;
            public int? LearnedProcessId;
            public GameWindowCandidate Learned;
            public DateTime LastDiscoveryUtc;
            public string NormalizedInstallDirectory;

            /// <summary>
            /// Every process attributed to this game so far, seeded with the one Playnite started.
            /// Grow-only and per-session: it is what lets a chain still reach the game after the
            /// launcher that started it has exited, taking its process id with it.
            /// </summary>
            public readonly HashSet<int> AttributedPids = new HashSet<int>();
        }

        /// <summary>
        /// A pid's owning game, the strength of the evidence that tied them, and when the process
        /// started. The start time rides along because it is read from the same process handle and
        /// is cached with the rest, and because it is what orders a launcher against the game it
        /// launched.
        /// </summary>
        private readonly struct PidClassification
        {
            public PidClassification(
                Guid? gameId,
                GameWindowEvidence evidence,
                int treeDepth,
                DateTime startTimeUtc)
            {
                GameId = gameId;
                Evidence = evidence;
                TreeDepth = treeDepth;
                StartTimeUtc = startTimeUtc;
            }

            public Guid? GameId { get; }

            public GameWindowEvidence Evidence { get; }

            /// <summary>Process-creation steps from the process Playnite started.</summary>
            public int TreeDepth { get; }

            public DateTime StartTimeUtc { get; }
        }

        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<Guid, TrackedGame> _tracked = new Dictionary<Guid, TrackedGame>();
        // pid -> owning game and evidence strength (a null game id: classified as not a tracked
        // game). Cleared whenever the tracked set changes so stale attributions never outlive a
        // session. Only conclusive classifications are cached (see ClassifyProcessLocked).
        private readonly Dictionary<int, PidClassification> _pidGameCache =
            new Dictionary<int, PidClassification>();

        // When each of a tracked game's windows was first seen. Only windows that classify as a
        // tracked game are recorded, so this stays a handful of entries, and it is cleared with the
        // pid cache whenever the tracked set changes. It separates windows of one process that no
        // other signal can tell apart — a splash from the render window that replaces it.
        private readonly Dictionary<IntPtr, DateTime> _firstSeenUtc = new Dictionary<IntPtr, DateTime>();

        private Timer _pollTimer;
        private Guid? _stableForegroundGameId;
        private Guid? _pendingStableGameId;
        private int _pendingStreak;
        private bool _disposed;

        public ActiveGameWindowTracker(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Raised (on a timer thread) after the foreground has stayed on a different tracked
        /// game for <see cref="StableConfirmationPolls"/> consecutive multi-game polls.
        /// </summary>
        public event EventHandler<StableForegroundGameChangedEventArgs> StableForegroundGameChanged;

        /// <summary>
        /// The game whose focus has been confirmed stable. Seeded to the most recently started
        /// game and does not decay when no game is foreground.
        /// </summary>
        public Guid? StableForegroundGameId
        {
            get
            {
                lock (_sync)
                {
                    return _stableForegroundGameId;
                }
            }
        }

        /// <summary>Whether the game is currently tracked as running.</summary>
        public bool IsTracked(Guid gameId)
        {
            lock (_sync)
            {
                return _tracked.ContainsKey(gameId);
            }
        }

        /// <summary>
        /// Live check: does the game own the CURRENT foreground window? Also learns the game's
        /// window handle and pid as a side effect, keeping later handle lookups fresh.
        /// </summary>
        public bool IsGameForeground(Guid gameId)
        {
            return QueryForegroundGame() == gameId;
        }

        /// <summary>
        /// True when the game has a resolvable window that is not minimized — i.e. there is a
        /// visible game surface to place a notification over and to capture. Focus and occlusion do
        /// NOT matter (WGC captures the window, and the toast is z-ordered above it, regardless);
        /// only a minimized window has no surface, so that is the sole condition that holds a wave.
        /// </summary>
        public bool IsGameWindowVisible(Guid gameId)
        {
            // Foreground is the common case (the player is in the game) and, crucially, learns the
            // window handle as a side effect — which the not-foreground branch and capture rely on.
            // Without this, a foreground game whose handle was never learned would be treated as not
            // visible and its wave held forever.
            if (IsGameForeground(gameId))
            {
                return true;
            }

            var hwnd = TryGetWindowHandle(gameId);
            return hwnd != IntPtr.Zero && IsWindow(hwnd) && !IsIconic(hwnd);
        }

        /// <summary>
        /// Diagnostic only: a compact description of the current foreground window — its owning
        /// process image name, pid, window title, and whether that process classifies as a tracked
        /// game. Logged when a notification wave is held for focus so the window actually holding
        /// it (another app, an overlay, or the game itself misclassified) is identifiable after the
        /// fact. Classification runs through the same cached path as <see cref="IsGameForeground"/>;
        /// never throws.
        /// </summary>
        public string DescribeForegroundWindow()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    return "foreground=none (hwnd=0)";
                }

                GetWindowThreadProcessId(hwnd, out var pid);
                var title = TryGetWindowTitle(hwnd);
                var exe = pid != 0 ? TryGetProcessImagePath((int)pid) : null;
                var exeName = string.IsNullOrEmpty(exe) ? "?" : Path.GetFileName(exe);

                string classified;
                lock (_sync)
                {
                    var gameId = pid != 0 && !_disposed && _tracked.Count > 0
                        ? ClassifyProcessLocked((int)pid).GameId
                        : null;
                    classified = gameId.HasValue && _tracked.TryGetValue(gameId.Value, out var tracked)
                        ? $"trackedGame='{tracked.Game?.Name}'"
                        : "notTrackedGame";
                }

                return $"foreground=exe:{exeName} pid:{pid} title:'{title}' {classified}";
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[WindowTracker] Foreground description failed.");
                return "foreground=unavailable";
            }
        }

        public void OnGameStarted(Game game, int? startedProcessId)
        {
            if (_disposed || game == null || game.Id == Guid.Empty)
            {
                return;
            }

            lock (_sync)
            {
                var tracked = new TrackedGame
                {
                    Game = game,
                    StartedProcessId = startedProcessId,
                    NormalizedInstallDirectory = NormalizeDirectory(game.InstallDirectory)
                };

                // The root of this game's process tree, and the only one known before anything has
                // been observed. Everything the game goes on to start is measured from here.
                if (startedProcessId is int startedPid && startedPid > 0)
                {
                    tracked.AttributedPids.Add(startedPid);
                }

                _tracked[game.Id] = tracked;
                _pidGameCache.Clear();
                _firstSeenUtc.Clear();

                // A game that just started is what the user is about to play; seed the stable
                // owner so consumers don't wait a full confirmation cycle for the obvious answer.
                _stableForegroundGameId = game.Id;
                _pendingStableGameId = null;
                _pendingStreak = 0;

                UpdatePollTimerLocked();
            }
        }

        public void OnGameStopped(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            lock (_sync)
            {
                if (!_tracked.Remove(gameId))
                {
                    return;
                }

                _pidGameCache.Clear();
                _firstSeenUtc.Clear();
                if (_stableForegroundGameId == gameId)
                {
                    _stableForegroundGameId = null;
                }

                if (_pendingStableGameId == gameId)
                {
                    _pendingStableGameId = null;
                    _pendingStreak = 0;
                }

                UpdatePollTimerLocked();
            }
        }

        /// <summary>
        /// The durable window target for a tracked game: the best-ranked window learned so far
        /// while it is still valid, otherwise the started process's main window. IntPtr.Zero when
        /// neither resolves. Focus is deliberately not part of the answer — see
        /// <see cref="TryGetFocusedWindowHandle"/> for the at-this-instant question a still capture
        /// asks instead.
        ///
        /// The desktop is re-scanned on a throttle for as long as the game runs, and a better
        /// candidate is promoted (see <see cref="GameWindowRanking"/>) — that is how a target
        /// resolved during launch, when a launcher's window may be the only one open, stops being
        /// the answer once the game itself has a window. Nothing is ever treated as settled: a
        /// game's picker or configuration window can outrank its render window on every signal
        /// available at launch and only lose once that render window exists. Callers that poll (the
        /// video recorder asks once a second) therefore follow the game without restarting.
        /// </summary>
        public IntPtr TryGetWindowHandle(Guid gameId)
        {
            int? pid;
            lock (_sync)
            {
                if (!_tracked.TryGetValue(gameId, out var tracked))
                {
                    return IntPtr.Zero;
                }

                if (!tracked.Learned.IsEmpty && !IsWindow(tracked.Learned.Hwnd))
                {
                    // The window we were following is gone (a game recreating its window during a
                    // loading screen, a launcher closing behind the game). Drop it and scan now
                    // rather than at the next throttle tick.
                    tracked.Learned = default(GameWindowCandidate);
                    tracked.LastDiscoveryUtc = DateTime.MinValue;
                }
                else if (!tracked.Learned.IsEmpty && !IsRediscoveryDueLocked(tracked))
                {
                    return tracked.Learned.Hwnd;
                }

                pid = tracked.LearnedProcessId ?? tracked.StartedProcessId;
            }

            // Proactively find the game's window by classifying each eligible top-level window's
            // owning process, so a backgrounded game's window is resolvable before it has ever been
            // foreground — the first hotkey no longer needs the game focused first, and the video
            // recorder finds the window immediately.
            var discovered = DiscoverGameWindow(gameId);
            if (discovered != IntPtr.Zero)
            {
                return discovered;
            }

            if (!pid.HasValue || pid.Value <= 0)
            {
                return IntPtr.Zero;
            }

            try
            {
                using (var process = Process.GetProcessById(pid.Value))
                {
                    return process.MainWindowHandle;
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// The game window to capture *at this instant*: the foreground window when it belongs to
        /// this game, else <see cref="TryGetWindowHandle"/>.
        ///
        /// This is what a still capture wants, and it is a different question from the one
        /// <see cref="TryGetWindowHandle"/> answers. A screenshot is taken at the moment of an
        /// unlock, when the player is in the game, so the window they are looking at is by
        /// definition the one to photograph — and this path has always been right in the field for
        /// exactly that reason. A rolling video capture cannot use it: it has to choose a target
        /// during launch, when the foreground window is whatever the launcher put there, and hold
        /// it while the player alt-tabs away and back.
        /// </summary>
        public IntPtr TryGetFocusedWindowHandle(Guid gameId)
        {
            var hwnd = GetAncestor(TryGetForegroundWindow(), GA_ROOT);
            if (hwnd != IntPtr.Zero && IsGameForeground(gameId) && IsEligibleWindow(hwnd))
            {
                return hwnd;
            }

            return TryGetWindowHandle(gameId);
        }

        private bool IsRediscoveryDueLocked(TrackedGame tracked)
        {
            return (DateTime.UtcNow - tracked.LastDiscoveryUtc).TotalMilliseconds >= RediscoverIntervalMs;
        }

        /// <summary>
        /// Scores every eligible top-level window whose owning process classifies as
        /// <paramref name="gameId"/> and learns the best of them, keeping the one already in use
        /// unless a candidate beats it. Returns the learned handle, or IntPtr.Zero when nothing
        /// was found and nothing was known. Classification is pid-cached, so repeat scans cost
        /// little more than the enumeration itself.
        /// </summary>
        private IntPtr DiscoverGameWindow(Guid gameId)
        {
            var candidates = new List<GameWindowCandidate>();
            try
            {
                EnumWindows((hwnd, _) =>
                {
                    if (!IsEligibleWindow(hwnd))
                    {
                        return true;
                    }

                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == 0)
                    {
                        return true;
                    }

                    GameWindowCandidate candidate;
                    lock (_sync)
                    {
                        if (_disposed || _tracked.Count == 0)
                        {
                            return false;
                        }

                        var classification = ClassifyProcessLocked((int)pid);
                        if (classification.GameId != gameId)
                        {
                            return true;
                        }

                        candidate = new GameWindowCandidate(
                            hwnd,
                            classification.TreeDepth,
                            classification.Evidence,
                            classification.StartTimeUtc,
                            NoteFirstSeenLocked(hwnd));
                    }

                    candidates.Add(candidate);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[WindowTracker] Game window discovery failed.");
            }

            var best = GameWindowRanking.SelectBest(candidates);
            lock (_sync)
            {
                if (_disposed || !_tracked.TryGetValue(gameId, out var tracked))
                {
                    return IntPtr.Zero;
                }

                tracked.LastDiscoveryUtc = DateTime.UtcNow;

                // Re-describe the window in use from this same scan before ranking against it. Its
                // stored description was true when it was learned, and the part that goes stale is
                // the one that decides close calls: a launcher window learned while it had focus
                // would keep that advantage for the rest of the session and outrank the game window
                // that took focus from it.
                RefreshLearnedFromScanLocked(tracked, candidates);
                var previousHwnd = tracked.Learned.Hwnd;
                LearnCandidateLocked(tracked, best);

                // Log the whole field, not just the winner, the first time a game is resolved and
                // whenever the target moves. Which window won is only half of a diagnosis; the other
                // half is what it beat and on which signal — and without that, a report of "the clip
                // shows the launcher" can only be guessed at.
                if (candidates.Count > 1 && tracked.Learned.Hwnd != previousHwnd)
                {
                    LogCandidateField(tracked, candidates);
                }

                return tracked.Learned.Hwnd;
            }
        }

        /// <summary>
        /// Writes every candidate window with the signals it was ranked on, marking the winner. This
        /// is the record that makes a mis-targeted capture diagnosable from the log alone, instead of
        /// from assumptions about how a particular game launches.
        /// </summary>
        private void LogCandidateField(TrackedGame tracked, List<GameWindowCandidate> candidates)
        {
            var builder = new StringBuilder();
            builder.Append("[WindowTracker] '").Append(tracked.Game?.Name).Append("' ranked ")
                   .Append(candidates.Count).Append(" candidate windows:");
            foreach (var candidate in candidates)
            {
                builder.Append(candidate.Hwnd == tracked.Learned.Hwnd ? "\n  CHOSEN  " : "\n          ")
                       .Append(candidate)
                       .Append(' ')
                       .Append(DescribeCandidateProcess(candidate.Hwnd));
            }

            _logger?.Info(builder.ToString());
        }

        private static string DescribeCandidateProcess(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return "exe:? pid:0";
            }

            var exe = TryGetProcessImagePath((int)pid);
            return $"exe:{(string.IsNullOrEmpty(exe) ? "?" : Path.GetFileName(exe))} pid:{pid} " +
                   $"class:'{TryGetWindowClass(hwnd)}' title:'{TryGetWindowTitle(hwnd)}'";
        }

        private static string TryGetWindowClass(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(256);
                return GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void RefreshLearnedFromScanLocked(
            TrackedGame tracked,
            List<GameWindowCandidate> candidates)
        {
            if (tracked.Learned.IsEmpty)
            {
                return;
            }

            foreach (var candidate in candidates)
            {
                if (candidate.Hwnd == tracked.Learned.Hwnd)
                {
                    tracked.Learned = candidate;
                    return;
                }
            }
        }

        /// <summary>
        /// Whether a window could be the surface a game is played on, judged by style alone.
        /// Rejects what a capture can never use or a player never sees: minimized windows (parked
        /// off screen at a stub size with an empty client area), windows cloaked by DWM (a
        /// suspended store app, a window on another virtual desktop), tool windows, and owned
        /// windows — the dialogs, splashes and tooltips that belong to a real window.
        /// </summary>
        private static bool IsEligibleWindow(IntPtr hwnd)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return false;
            }

            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            {
                return false;
            }

            if ((GetWindowLong(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
            {
                return false;
            }

            return !IsCloaked(hwnd) && !IsStubSized(hwnd);
        }

        /// <summary>
        /// Whether the window is too small for a capture to use at all. Not a judgement about which
        /// window is the game: a stub-sized window is what a minimized window parked off screen
        /// looks like, and the H.264 encoder refuses its dimensions outright. Measured through
        /// <see cref="WindowRectangles"/>, the one place window rects are read.
        /// </summary>
        private static bool IsStubSized(IntPtr hwnd)
        {
            var area = WindowRectangles.Measure(hwnd).PreferredCaptureArea;
            return area.Width < MinCandidateDimension || area.Height < MinCandidateDimension;
        }

        /// <summary>
        /// The first time this window was seen, recorded on first sight. Windows of one process are
        /// otherwise indistinguishable, and a render window that replaces a splash is the later of
        /// the two.
        /// </summary>
        private DateTime NoteFirstSeenLocked(IntPtr hwnd)
        {
            if (_firstSeenUtc.TryGetValue(hwnd, out var seen))
            {
                return seen;
            }

            seen = DateTime.UtcNow;
            _firstSeenUtc[hwnd] = seen;
            return seen;
        }

        private static bool IsCloaked(IntPtr hwnd)
        {
            try
            {
                return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 &&
                       cloaked != 0;
            }
            catch
            {
                // Not a reason to discard a window: an unavailable DWM says nothing about it.
                return false;
            }
        }

        private static IntPtr TryGetForegroundWindow()
        {
            try
            {
                return GetForegroundWindow();
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Adopts <paramref name="candidate"/> as the game's window when it beats what is already
        /// known. A change of handle is logged: it is the only record of which window a clip or
        /// screenshot was taken from, and the answer a report of "it captured the launcher" needs.
        /// </summary>
        private void LearnCandidateLocked(TrackedGame tracked, GameWindowCandidate candidate)
        {
            if (!GameWindowRanking.ShouldReplace(tracked.Learned, candidate))
            {
                return;
            }

            var previous = tracked.Learned;
            tracked.Learned = candidate;

            GetWindowThreadProcessId(candidate.Hwnd, out var pid);
            if (pid != 0)
            {
                tracked.LearnedProcessId = (int)pid;
            }

            if (previous.Hwnd == candidate.Hwnd)
            {
                return;
            }

            var exe = pid != 0 ? TryGetProcessImagePath((int)pid) : null;
            _logger?.Info(
                $"[WindowTracker] '{tracked.Game?.Name}' window " +
                $"{(previous.IsEmpty ? "resolved" : "promoted")} to {candidate} " +
                $"exe:{(string.IsNullOrEmpty(exe) ? "?" : Path.GetFileName(exe))} " +
                $"title:'{TryGetWindowTitle(candidate.Hwnd)}'" +
                $"{(previous.IsEmpty ? string.Empty : $" (was {previous})")}.");
        }

        /// <summary>
        /// Best process id for a tracked game: the foreground-learned pid when available (the
        /// process that actually owns the game window), else the started pid.
        /// </summary>
        public int? TryGetProcessId(Guid gameId)
        {
            lock (_sync)
            {
                return _tracked.TryGetValue(gameId, out var tracked)
                    ? tracked.LearnedProcessId ?? tracked.StartedProcessId
                    : null;
            }
        }

        /// <summary>
        /// Whether a process belongs to this Playnite instance's child tree. Null means the live
        /// process snapshot could not establish the relationship safely.
        /// </summary>
        public bool? IsInPlayniteProcessTree(int processId)
        {
            var parents = SnapshotParentMap();
            if (processId <= 0 || parents == null)
            {
                return null;
            }

            var playniteProcessId = Process.GetCurrentProcess().Id;
            var current = processId;
            for (var depth = 0; depth < MaxParentChainDepth; depth++)
            {
                if (current == playniteProcessId)
                {
                    return true;
                }

                if (!parents.TryGetValue(current, out var parent) || parent == current)
                {
                    return null;
                }

                if (parent <= 0)
                {
                    return false;
                }

                current = parent;
            }

            return null;
        }

        // === Foreground resolution ===

        /// <summary>
        /// Resolves and classifies the current foreground window. Learns the owning game's
        /// hwnd/pid on success. Null when the foreground isn't a tracked game.
        /// </summary>
        private Guid? QueryForegroundGame()
        {
            try
            {
                // Normalised to the root window, because that is what EnumWindows ranks and what a
                // capture can target: focus can sit on a child of the game's frame.
                var hwnd = GetAncestor(TryGetForegroundWindow(), GA_ROOT);
                if (hwnd == IntPtr.Zero)
                {
                    return null;
                }

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0)
                {
                    return null;
                }

                lock (_sync)
                {
                    if (_disposed || _tracked.Count == 0)
                    {
                        return null;
                    }

                    var classification = ClassifyProcessLocked((int)pid);
                    if (classification.GameId.HasValue &&
                        _tracked.TryGetValue(classification.GameId.Value, out var tracked))
                    {
                        // The pid is learned (the audio capture and the screenshot fallback want
                        // the process that actually owns the game's window), but the durable window
                        // target deliberately is NOT: see TryGetWindowHandle. Focus is not evidence
                        // of which window is the game — a launcher holds it precisely while the
                        // game is starting.
                        tracked.LearnedProcessId = (int)pid;
                    }

                    return classification.GameId;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[WindowTracker] Foreground resolution failed.");
                return null;
            }
        }

        // Runs only while 2+ games are tracked: watches for the user's focus settling on a
        // different running game and promotes it to the stable owner.
        private void PollTick(object state)
        {
            try
            {
                var foreground = QueryForegroundGame();
                Game switchedTo = null;
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (!foreground.HasValue || foreground == _stableForegroundGameId)
                    {
                        // Non-game foreground never decays the stable owner; it only resets any
                        // switch in progress.
                        _pendingStableGameId = null;
                        _pendingStreak = 0;
                        return;
                    }

                    if (_pendingStableGameId == foreground)
                    {
                        _pendingStreak++;
                    }
                    else
                    {
                        _pendingStableGameId = foreground;
                        _pendingStreak = 1;
                    }

                    if (_pendingStreak < StableConfirmationPolls)
                    {
                        return;
                    }

                    _pendingStableGameId = null;
                    _pendingStreak = 0;
                    _stableForegroundGameId = foreground;
                    if (_tracked.TryGetValue(foreground.Value, out var tracked))
                    {
                        switchedTo = tracked.Game;
                    }
                }

                if (switchedTo != null)
                {
                    _logger?.Info($"[WindowTracker] Stable foreground game: {switchedTo.Name}.");
                    StableForegroundGameChanged?.Invoke(
                        this,
                        new StableForegroundGameChangedEventArgs(switchedTo));
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[WindowTracker] Foreground poll failed.");
            }
        }

        private void UpdatePollTimerLocked()
        {
            var shouldRun = !_disposed && _tracked.Count >= 2;
            if (shouldRun)
            {
                if (_pollTimer == null)
                {
                    _pollTimer = new Timer(PollTick, null, MultiGamePollMs, MultiGamePollMs);
                }
                else
                {
                    _pollTimer.Change(MultiGamePollMs, MultiGamePollMs);
                }
            }
            else
            {
                _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _pendingStableGameId = null;
                _pendingStreak = 0;
            }
        }

        // === pid -> game classification ===

        private PidClassification ClassifyProcessLocked(int pid)
        {
            if (_pidGameCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }

            var result = ClassifyProcessCore(pid, out var conclusive);
            // A transient inspection failure (process still initializing, access denied for one
            // moment) must not poison the pid for the whole session; only conclusive answers are
            // cached, inconclusive ones are re-tried on the next lookup.
            if (conclusive)
            {
                _pidGameCache[pid] = result;
            }

            return result;
        }

        /// <summary>
        /// Ties a process to a tracked game, and reports how far down that game's process tree it
        /// sits. Depth is the point: a launcher exists to start the game, so the game is deeper than
        /// the launcher, and reporting only *that* a process belongs to the game — which is all this
        /// used to do — throws away the one fact that separates the two.
        ///
        /// Attribution roots on the game's <see cref="TrackedGame.AttributedPids"/> rather than on
        /// its started pid alone. That set only grows, so a chain still resolves after the process
        /// Playnite started has exited, which for a launcher-wrapped title it routinely does — and
        /// with the started pid gone, rooting on it alone leaves the game's own process attributed
        /// to nothing and its window absent from the candidate list entirely.
        /// </summary>
        private PidClassification ClassifyProcessCore(int pid, out bool conclusive)
        {
            conclusive = true;
            var startTimeUtc = TryGetProcessStartTimeUtc(pid);
            var exePath = TryGetProcessImagePath(pid);
            if (string.IsNullOrEmpty(exePath))
            {
                // A process too young or too protected to inspect may still become attributable, so
                // this answer must not be cached.
                conclusive = false;
            }

            var tree = SnapshotProcessTree();

            // Install directory is the strongest tie but a fragile one: it compares the process
            // image path against the path Playnite recorded, and a library behind a junction or on a
            // remapped drive spells the same folder two ways. It therefore decides the evidence
            // tier, never whether the process belongs to the game.
            foreach (var entry in _tracked)
            {
                var tracked = entry.Value;
                var installDirMatch = !string.IsNullOrEmpty(exePath) &&
                                      !string.IsNullOrEmpty(tracked.NormalizedInstallDirectory) &&
                                      exePath.StartsWith(
                                          tracked.NormalizedInstallDirectory, StringComparison.OrdinalIgnoreCase);

                var depth = ResolveTreeDepth(pid, startTimeUtc, tracked, tree);
                if (depth < 0 && !installDirMatch)
                {
                    continue;
                }

                // An install-directory process with a broken chain still belongs to the game; it is
                // simply at an unknown remove from the launcher, which depth 0 represents.
                var resolvedDepth = depth < 0 ? 0 : depth;
                var evidence = installDirMatch
                    ? GameWindowEvidence.InstallDirectory
                    : GameWindowEvidence.AttributedProcess;

                if (AttributePidLocked(tracked, pid))
                {
                    _logger?.Debug(
                        $"[WindowTracker] pid {pid} attributed to '{tracked.Game?.Name}' " +
                        $"at depth {resolvedDepth} via {evidence} " +
                        $"(exe:{(string.IsNullOrEmpty(exePath) ? "?" : Path.GetFileName(exePath))}).");
                }

                return new PidClassification(entry.Key, evidence, resolvedDepth, startTimeUtc);
            }

            return default(PidClassification);
        }

        /// <summary>
        /// Records a pid as belonging to the game so its descendants remain attributable after the
        /// launcher that spawned them exits. Returns whether this was new. Growing the set can make
        /// a previously unattributable process attributable, so the classification cache is dropped
        /// — otherwise a "not this game" answer cached during launch would outlive the reason for it.
        /// </summary>
        private bool AttributePidLocked(TrackedGame tracked, int pid)
        {
            if (pid <= 0 || !tracked.AttributedPids.Add(pid))
            {
                return false;
            }

            _pidGameCache.Clear();
            return true;
        }

        /// <summary>
        /// How many process-creation steps separate <paramref name="pid"/> from the nearest process
        /// already attributed to the game, or -1 when the chain does not reach one.
        ///
        /// Every step is validated against process start times: a parent cannot have started after
        /// its child. Windows reuses process ids, so without that check a recycled parent id can
        /// graft an unrelated process onto the game's tree — and the previous version of this walk
        /// relied on liveness in a single snapshot, which does not rule that out.
        /// </summary>
        private static int ResolveTreeDepth(
            int pid,
            DateTime startTimeUtc,
            TrackedGame tracked,
            Dictionary<int, ProcessNode> tree)
        {
            if (tracked.AttributedPids.Contains(pid))
            {
                return 0;
            }

            if (tree == null)
            {
                return -1;
            }

            var current = pid;
            var currentStartUtc = startTimeUtc;
            for (var depth = 1; depth <= MaxParentChainDepth; depth++)
            {
                if (!tree.TryGetValue(current, out var node) || node.ParentPid <= 0 ||
                    node.ParentPid == current)
                {
                    return -1;
                }

                if (!tree.TryGetValue(node.ParentPid, out var parent))
                {
                    return -1;
                }

                // A parent that started after its supposed child is a recycled process id, not an
                // ancestor. Unknown times (an unreadable process) are not treated as a violation.
                if (currentStartUtc > DateTime.MinValue && parent.StartUtc > DateTime.MinValue &&
                    parent.StartUtc > currentStartUtc)
                {
                    return -1;
                }

                if (tracked.AttributedPids.Contains(node.ParentPid))
                {
                    return depth;
                }

                current = node.ParentPid;
                currentStartUtc = parent.StartUtc;
            }

            return -1;
        }

        /// <summary>
        /// When the process started, or <see cref="DateTime.MinValue"/> when it cannot be read.
        /// Read through the same limited-information handle as the image path rather than
        /// <c>Process.StartTime</c>, which needs broader rights and throws for processes this one
        /// cannot fully open.
        /// </summary>
        private static DateTime TryGetProcessStartTimeUtc(int pid)
        {
            if (pid <= 0)
            {
                return DateTime.MinValue;
            }

            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                return DateTime.MinValue;
            }

            try
            {
                return GetProcessTimes(handle, out var creation, out _, out _, out _)
                    ? DateTime.FromFileTimeUtc(creation)
                    : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>
        /// One live process: its parent and when it started. The start time is what makes a
        /// parent-child link checkable, since a process id on its own can have been recycled.
        /// </summary>
        private readonly struct ProcessNode
        {
            public ProcessNode(int parentPid, DateTime startUtc)
            {
                ParentPid = parentPid;
                StartUtc = startUtc;
            }

            public int ParentPid { get; }

            public DateTime StartUtc { get; }
        }

        /// <summary>
        /// Every live process by id, with its parent and start time. Null when the snapshot could not
        /// be taken. Start times are read lazily per entry and cost one limited-information handle
        /// each, so this is the expensive part of a scan; it is taken once per classification rather
        /// than once per chain step.
        /// </summary>
        private Dictionary<int, ProcessNode> SnapshotProcessTree()
        {
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
            {
                return null;
            }

            try
            {
                var map = new Dictionary<int, ProcessNode>();
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snapshot, ref entry))
                {
                    return null;
                }

                do
                {
                    var pid = (int)entry.th32ProcessID;
                    map[pid] = new ProcessNode(
                        (int)entry.th32ParentProcessID, TryGetProcessStartTimeUtc(pid));
                }
                while (Process32Next(snapshot, ref entry));

                return map;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[WindowTracker] Process snapshot failed.");
                return null;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        private static string TryGetWindowTitle(IntPtr hwnd)
        {
            try
            {
                var length = GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder(length + 1);
                GetWindowText(hwnd, builder, builder.Capacity);
                return builder.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryGetProcessImagePath(int pid)
        {
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var builder = new StringBuilder(1024);
                var size = (uint)builder.Capacity;
                return QueryFullProcessImageName(handle, 0, builder, ref size)
                    ? builder.ToString()
                    : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private Dictionary<int, int> SnapshotParentMap()
        {
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
            {
                return null;
            }

            try
            {
                var map = new Dictionary<int, int>();
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snapshot, ref entry))
                {
                    return null;
                }

                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                }
                while (Process32Next(snapshot, ref entry));

                return map;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[WindowTracker] Process snapshot failed.");
                return null;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        private static string NormalizeDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            try
            {
                var full = Path.GetFullPath(directory.Trim());
                return full.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? full
                    : full + Path.DirectorySeparatorChar;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_sync)
            {
                _disposed = true;
                _pollTimer?.Dispose();
                _pollTimer = null;
                _tracked.Clear();
                _pidGameCache.Clear();
                _firstSeenUtc.Clear();
            }
        }

        // === P/Invoke ===

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_CLOAKED = 14;
        private const uint GA_ROOT = 2;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        // GetWindowLongPtrW does not exist in 32-bit user32; the 32-bit entry point is the only one
        // exported there, so the pointer size decides which to call.
        private static IntPtr GetWindowLong(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr hWnd,
            int dwAttribute,
            out int pvAttribute,
            int cbAttribute);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(
            IntPtr hProcess,
            out long lpCreationTime,
            out long lpExitTime,
            out long lpKernelTime,
            out long lpUserTime);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            uint dwFlags,
            StringBuilder lpExeName,
            ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }
    }
}
