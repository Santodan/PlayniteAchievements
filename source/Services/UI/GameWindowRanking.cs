using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// How a window's owning process was tied to the game, strongest last. The tier is a property of
    /// the evidence, not of the order the classifier happened to test rules in: a process that
    /// satisfies several rules takes the strongest one.
    /// </summary>
    internal enum GameWindowEvidence
    {
        /// <summary>
        /// The window belongs to a process Playnite started, or to one already attributed to this
        /// game. For a launcher-wrapped title the started process is the launcher, so its own window
        /// qualifies here — which is why this is the weakest tier rather than a match to act on.
        /// </summary>
        AttributedProcess = 0,

        /// <summary>The owning process image lives under the game's install directory.</summary>
        InstallDirectory = 1,
    }

    /// <summary>One window competing to be "the game's window", with the facts behind it.</summary>
    internal readonly struct GameWindowCandidate
    {
        public GameWindowCandidate(
            IntPtr hwnd,
            int processTreeDepth,
            GameWindowEvidence evidence,
            DateTime processStartUtc,
            DateTime firstSeenUtc)
        {
            Hwnd = hwnd;
            ProcessTreeDepth = processTreeDepth;
            Evidence = evidence;
            ProcessStartUtc = processStartUtc;
            FirstSeenUtc = firstSeenUtc;
        }

        public IntPtr Hwnd { get; }

        /// <summary>
        /// How many process-creation steps separate the owning process from the one Playnite
        /// started: 0 for that process itself, 1 for something it started, and so on. A launcher
        /// exists to start the game, so the game sits deeper in the tree than the launcher does.
        /// </summary>
        public int ProcessTreeDepth { get; }

        public GameWindowEvidence Evidence { get; }

        /// <summary>
        /// When the owning process started, or <see cref="DateTime.MinValue"/> when it could not be
        /// read. Also what validates a parent-child link, since Windows reuses process ids.
        /// </summary>
        public DateTime ProcessStartUtc { get; }

        /// <summary>When this window was first seen, for ordering windows of one process.</summary>
        public DateTime FirstSeenUtc { get; }

        public bool IsEmpty => Hwnd == IntPtr.Zero;

        public override string ToString()
        {
            return $"0x{Hwnd.ToInt64():X} depth={ProcessTreeDepth} {Evidence} " +
                   $"procStart={Stamp(ProcessStartUtc)} firstSeen={Stamp(FirstSeenUtc)}";
        }

        private static string Stamp(DateTime value)
        {
            return value > DateTime.MinValue ? value.ToString("HH:mm:ss.fff") : "?";
        }
    }

    /// <summary>
    /// Picks the game's window out of the several a game and its launcher have open.
    ///
    /// Every signal is a fact about process creation, so the ranking is deterministic: the same set
    /// of processes always produces the same answer, and nothing depends on how a window is painted,
    /// how large it is, whether it fills a display, or which one has focus.
    ///
    /// 1. Evidence: a process whose image sits inside the game's install directory outranks one
    ///    merely attributed to the game's process tree. This has to come first, because a store
    ///    client's own helper processes live in that tree too and can sit deeper in it than the game
    ///    does — Steam renders its UI from a steamwebhelper two levels below steam.exe, while a game
    ///    steam.exe launches is one level below it. Only the install directory separates those.
    /// 2. Depth in the process tree, deepest first, as the tiebreak within a tier. Playnite starts a
    ///    launcher; the launcher starts the game. Where both live in the game's install folder — as
    ///    Shenmue I &amp; II's game-picker and its two games do — this is what separates them, and a
    ///    launcher can never outrank a game it started.
    /// 3. Which process started later — for two processes at the same depth, such as a launcher that
    ///    replaces itself.
    /// 4. Which window appeared later, for two windows of one process: a splash and the render window
    ///    that supersedes it.
    ///
    /// Pure: the caller supplies already-attributed candidates, so the ranking is testable without a
    /// desktop. See <see cref="ActiveGameWindowTracker"/> for the enumeration and process
    /// attribution that produce them.
    /// </summary>
    internal static class GameWindowRanking
    {
        /// <summary>
        /// Whether <paramref name="candidate"/> should take over from <paramref name="current"/>:
        /// better on the first signal that separates them. Two candidates nothing separates leave
        /// the incumbent in place, so a running capture is never torn down for a lateral move.
        /// </summary>
        public static bool ShouldReplace(GameWindowCandidate current, GameWindowCandidate candidate)
        {
            if (candidate.IsEmpty)
            {
                return false;
            }

            if (current.IsEmpty)
            {
                return true;
            }

            if (candidate.Evidence != current.Evidence)
            {
                return candidate.Evidence > current.Evidence;
            }

            if (candidate.ProcessTreeDepth != current.ProcessTreeDepth)
            {
                return candidate.ProcessTreeDepth > current.ProcessTreeDepth;
            }

            if (candidate.ProcessStartUtc != current.ProcessStartUtc)
            {
                return candidate.ProcessStartUtc > current.ProcessStartUtc;
            }

            return candidate.FirstSeenUtc > current.FirstSeenUtc;
        }

        /// <summary>
        /// The best of <paramref name="candidates"/> on the same signals. An empty candidate for an
        /// empty input.
        /// </summary>
        public static GameWindowCandidate SelectBest(IEnumerable<GameWindowCandidate> candidates)
        {
            var best = default(GameWindowCandidate);
            if (candidates == null)
            {
                return best;
            }

            foreach (var candidate in candidates)
            {
                if (!candidate.IsEmpty && (best.IsEmpty || ShouldReplace(best, candidate)))
                {
                    best = candidate;
                }
            }

            return best;
        }
    }
}
