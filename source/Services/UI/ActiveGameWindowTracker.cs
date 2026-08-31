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
    /// consumers that restart an ffmpeg capture on switch. With a single game running there is
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
        }

        /// <summary>
        /// A pid's owning game, the strength of the evidence that tied them, and when the process
        /// started. The start time rides along because it is read from the same process handle and
        /// is cached with the rest, and because it is what orders a launcher against the game it
        /// launched.
        /// </summary>
        private readonly struct PidClassification
        {
            public PidClassification(Guid? gameId, GameWindowEvidence evidence, DateTime startTimeUtc)
            {
                GameId = gameId;
                Evidence = evidence;
                StartTimeUtc = startTimeUtc;
            }

            public Guid? GameId { get; }

            public GameWindowEvidence Evidence { get; }

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
                _tracked[game.Id] = new TrackedGame
                {
                    Game = game,
                    StartedProcessId = startedProcessId,
                    NormalizedInstallDirectory = NormalizeDirectory(game.InstallDirectory)
                };
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

                    GameWindowEvidence evidence;
                    DateTime processStartUtc;
                    DateTime firstSeenUtc;
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

                        evidence = classification.Evidence;
                        processStartUtc = classification.StartTimeUtc;
                        firstSeenUtc = NoteFirstSeenLocked(hwnd);
                    }

                    // Measured only for the game's own windows: every rect is read inside a DPI
                    // awareness scope, and paying that for each of the desktop's windows on every
                    // scan would not be worth what it buys.
                    if (TryMeasureCandidateArea(hwnd, out var clientArea))
                    {
                        candidates.Add(new GameWindowCandidate(
                            hwnd,
                            evidence,
                            IsManagedUiShell(hwnd),
                            processStartUtc,
                            firstSeenUtc,
                            clientArea));
                    }

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

            return !IsCloaked(hwnd);
        }

        /// <summary>
        /// The window's capture area in square physical pixels, or false when it is too small to
        /// be a game's picture. Measured through <see cref="WindowRectangles"/> — the one place
        /// window rects are read — so the area is Per-Monitor-V2 correct and comparable across
        /// monitors of different scale.
        /// </summary>
        private static bool TryMeasureCandidateArea(IntPtr hwnd, out long clientArea)
        {
            clientArea = 0;
            var area = WindowRectangles.Measure(hwnd).PreferredCaptureArea;
            if (area.Width < MinCandidateDimension || area.Height < MinCandidateDimension)
            {
                return false;
            }

            clientArea = (long)area.Width * area.Height;
            return true;
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

        /// <summary>
        /// Whether the window's class marks it as drawn by a managed UI toolkit — WinForms
        /// (<c>WindowsForms10.Window...</c>) or WPF (<c>HwndWrapper[...]</c>). A game's picture is
        /// never presented through one, so these are launchers, pickers, settings dialogs and crash
        /// reporters. Treated as a demotion rather than an exclusion: if such a window is the only
        /// one a game has, it is still the best answer available.
        /// </summary>
        private static bool IsManagedUiShell(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(256);
                if (GetClassName(hwnd, builder, builder.Capacity) <= 0)
                {
                    return false;
                }

                var className = builder.ToString();
                return className.StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase) ||
                       className.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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
        /// Ties a pid to a tracked game and reports how strong the tie is. The rules are tried
        /// strongest-evidence first — not in the order they are cheapest — so a pid that satisfies
        /// several is described by its best one: the process Playnite started is also, for a
        /// launcher-wrapped title, the launcher, and calling that a match on equal terms with the
        /// game's own executable is what let a launcher window be captured for a whole session.
        /// </summary>
        private PidClassification ClassifyProcessCore(int pid, out bool conclusive)
        {
            conclusive = true;
            var startTimeUtc = TryGetProcessStartTimeUtc(pid);

            // 1. Executable path under a tracked game's install directory: the game's own process,
            //    whether Playnite started it or a launcher did.
            var exePath = TryGetProcessImagePath(pid);
            if (!string.IsNullOrEmpty(exePath))
            {
                foreach (var entry in _tracked)
                {
                    var installDir = entry.Value.NormalizedInstallDirectory;
                    if (!string.IsNullOrEmpty(installDir) &&
                        exePath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.Debug(
                            $"[WindowTracker] pid {pid} classified as '{entry.Value.Game?.Name}' via install directory.");
                        return new PidClassification(
                            entry.Key, GameWindowEvidence.InstallDirectory, startTimeUtc);
                    }
                }
            }
            else
            {
                conclusive = false;
            }

            // 2. Parent chain up to a tracked started pid: something the launcher spawned, so more
            //    likely the game than the launcher itself — but the chain cannot prove which.
            var ancestorGame = ClassifyByParentChain(pid);
            if (ancestorGame.HasValue)
            {
                conclusive = true;
                return new PidClassification(ancestorGame, GameWindowEvidence.ProcessTree, startTimeUtc);
            }

            // 3. The process Playnite started, or one already learned to own a window of this
            //    game. Weakest: for a launcher-wrapped title this is the launcher.
            foreach (var entry in _tracked)
            {
                if (entry.Value.StartedProcessId == pid || entry.Value.LearnedProcessId == pid)
                {
                    return new PidClassification(
                        entry.Key, GameWindowEvidence.StartedProcess, startTimeUtc);
                }
            }

            return default(PidClassification);
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

        private Guid? ClassifyByParentChain(int pid)
        {
            var startedPids = new Dictionary<int, Guid>();
            foreach (var entry in _tracked)
            {
                if (entry.Value.StartedProcessId is int startedPid && startedPid > 0)
                {
                    startedPids[startedPid] = entry.Key;
                }
            }

            if (startedPids.Count == 0)
            {
                return null;
            }

            var parentByPid = SnapshotParentMap();
            if (parentByPid == null)
            {
                return null;
            }

            var current = pid;
            for (var depth = 0; depth < MaxParentChainDepth; depth++)
            {
                if (!parentByPid.TryGetValue(current, out var parent) || parent <= 0 || parent == current)
                {
                    return null;
                }

                if (startedPids.TryGetValue(parent, out var gameId))
                {
                    // The snapshot contains the child, so the ancestor pid is still a live process
                    // in the same snapshot; a reused pid would not appear as this chain's parent.
                    _logger?.Debug($"[WindowTracker] pid {pid} classified via parent chain (ancestor {parent}).");
                    return gameId;
                }

                current = parent;
            }

            return null;
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
