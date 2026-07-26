using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;

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
    /// Maps the foreground window to a running Playnite game so screenshots, toasts, and video
    /// capture follow the game the user is actually playing.
    ///
    /// The source of truth is one synchronous question — "which tracked game owns the current
    /// foreground window?" — answered on demand by <see cref="IsGameForeground"/> (a
    /// GetForegroundWindow syscall plus a cached pid lookup). Classification of an unseen
    /// process id (started-pid match, executable path under the game's install directory,
    /// bounded parent-process walk) runs once per pid and is cached until the tracked set
    /// changes.
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

        private sealed class TrackedGame
        {
            public Game Game;
            public int? StartedProcessId;
            public int? LearnedProcessId;
            public IntPtr LearnedHwnd;
            public string NormalizedInstallDirectory;
        }

        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<Guid, TrackedGame> _tracked = new Dictionary<Guid, TrackedGame>();
        // Foreground pid -> owning game id (null: classified as not a tracked game). Cleared
        // whenever the tracked set changes so stale attributions never outlive a session. Only
        // conclusive classifications are cached (see ClassifyProcessLocked).
        private readonly Dictionary<int, Guid?> _pidGameCache = new Dictionary<int, Guid?>();

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
        /// Best window handle for a tracked game: the foreground-learned handle when still valid
        /// (correct for launcher-wrapped titles whose started process is a dead bootstrapper),
        /// otherwise the started process's main window. IntPtr.Zero when neither resolves.
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

                if (tracked.LearnedHwnd != IntPtr.Zero && IsWindow(tracked.LearnedHwnd))
                {
                    return tracked.LearnedHwnd;
                }

                tracked.LearnedHwnd = IntPtr.Zero;
                pid = tracked.LearnedProcessId ?? tracked.StartedProcessId;
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

        // === Foreground resolution ===

        /// <summary>
        /// Resolves and classifies the current foreground window. Learns the owning game's
        /// hwnd/pid on success. Null when the foreground isn't a tracked game.
        /// </summary>
        private Guid? QueryForegroundGame()
        {
            try
            {
                var hwnd = GetForegroundWindow();
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

                    var gameId = ClassifyProcessLocked((int)pid);
                    if (gameId.HasValue && _tracked.TryGetValue(gameId.Value, out var tracked))
                    {
                        tracked.LearnedHwnd = hwnd;
                        tracked.LearnedProcessId = (int)pid;
                    }

                    return gameId;
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

        private Guid? ClassifyProcessLocked(int pid)
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

        private Guid? ClassifyProcessCore(int pid, out bool conclusive)
        {
            conclusive = true;

            // 1. Direct pid match against started or previously learned pids (direct-exe games
            //    and emulators, where Playnite started the process itself).
            foreach (var entry in _tracked)
            {
                if (entry.Value.StartedProcessId == pid || entry.Value.LearnedProcessId == pid)
                {
                    return entry.Key;
                }
            }

            // 2. Executable path under a tracked game's install directory (launcher-wrapped
            //    titles whose started process is a dead bootstrapper).
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
                        return entry.Key;
                    }
                }
            }
            else
            {
                conclusive = false;
            }

            // 3. Parent chain up to a tracked started pid (launcher alive with the game exe
            //    outside the install directory).
            var ancestorGame = ClassifyByParentChain(pid);
            if (ancestorGame.HasValue)
            {
                conclusive = true;
                return ancestorGame;
            }

            return null;
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
            }
        }

        // === P/Invoke ===

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

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
