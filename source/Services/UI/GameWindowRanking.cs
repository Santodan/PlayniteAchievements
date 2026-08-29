using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// How strongly a window's owning process is tied to the game, strongest last. The tier is a
    /// property of the evidence, not of the order the classifier happened to test rules in: a pid
    /// that satisfies several rules takes the strongest one.
    /// </summary>
    internal enum GameWindowEvidence
    {
        /// <summary>
        /// The window belongs to the process Playnite started. For a launcher-wrapped title that
        /// process is the launcher, so its own window looks exactly like the game's from here —
        /// which is why this is the weakest tier rather than a match to act on.
        /// </summary>
        StartedProcess = 0,

        /// <summary>The owning pid descends from the started pid without being it.</summary>
        ProcessTree = 1,

        /// <summary>The owning process image lives under the game's install directory.</summary>
        InstallDirectory = 2,
    }

    /// <summary>One window competing to be "the game's window", with the evidence behind it.</summary>
    internal readonly struct GameWindowCandidate
    {
        public GameWindowCandidate(IntPtr hwnd, GameWindowEvidence evidence, bool isForeground, long clientArea)
        {
            Hwnd = hwnd;
            Evidence = evidence;
            IsForeground = isForeground;
            ClientArea = clientArea;
        }

        public IntPtr Hwnd { get; }

        public GameWindowEvidence Evidence { get; }

        /// <summary>Whether this window was the foreground window when it was observed.</summary>
        public bool IsForeground { get; }

        /// <summary>Client area in square physical pixels; the tiebreaker within a score.</summary>
        public long ClientArea { get; }

        public bool IsEmpty => Hwnd == IntPtr.Zero;

        public override string ToString()
        {
            return $"0x{Hwnd.ToInt64():X} {Evidence}{(IsForeground ? "+foreground" : string.Empty)} " +
                   $"area={ClientArea} score={GameWindowRanking.Score(this)}";
        }
    }

    /// <summary>
    /// Picks the game's window out of the several a game (or its launcher) has open, and decides
    /// when a newly observed window is good enough to replace the one already in use.
    ///
    /// Pure: the caller supplies already-measured candidates, so the ranking is testable without a
    /// desktop. See <see cref="ActiveGameWindowTracker"/> for the enumeration and filtering that
    /// produce them.
    /// </summary>
    internal static class GameWindowRanking
    {
        /// <summary>
        /// The score at which a window is trusted as the game's and the desktop is no longer
        /// re-scanned for a better one. Set so that a window of the started process can never
        /// reach it, even in the foreground: that is the tier a launcher's own window occupies,
        /// and latching onto it is the failure this ranking exists to prevent.
        /// </summary>
        public const int ConclusiveScore = 3;

        /// <summary>
        /// Evidence dominates, and being the foreground window adds one step within it. Foreground
        /// is a boost rather than a tier of its own so that alt-tabbing to the launcher mid-session
        /// cannot promote the launcher's window over the game's: a foreground launcher window
        /// (started-process evidence) still scores below a background window that lives in the
        /// game's install directory.
        /// </summary>
        public static int Score(GameWindowCandidate candidate)
        {
            return ((int)candidate.Evidence * 2) + (candidate.IsForeground ? 1 : 0);
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> should take over from <paramref name="current"/>.
        /// Strictly better only: an equal score keeps the incumbent, so a capture that is already
        /// running is never torn down to swap between two equally plausible windows. A stronger
        /// observation of the window already in use also passes — the handle does not change, so
        /// nothing is retargeted, but the record it is held under gets its better evidence.
        /// </summary>
        public static bool ShouldReplace(GameWindowCandidate current, GameWindowCandidate candidate)
        {
            if (candidate.IsEmpty)
            {
                return false;
            }

            return current.IsEmpty || Score(candidate) > Score(current);
        }

        /// <summary>
        /// The best of <paramref name="candidates"/> — highest score, largest client area to break a
        /// tie (the render window over a splash or helper window of the same process). An empty
        /// candidate for an empty input.
        /// </summary>
        public static GameWindowCandidate SelectBest(IEnumerable<GameWindowCandidate> candidates)
        {
            var best = default(GameWindowCandidate);
            if (candidates == null)
            {
                return best;
            }

            var bestScore = int.MinValue;
            foreach (var candidate in candidates)
            {
                if (candidate.IsEmpty)
                {
                    continue;
                }

                var score = Score(candidate);
                if (best.IsEmpty || score > bestScore ||
                    (score == bestScore && candidate.ClientArea > best.ClientArea))
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        /// <summary>
        /// Whether a learned window is strong enough to stop looking. Below this the answer was a
        /// best guess made while the game was still starting and the real window may have appeared
        /// since, so the caller keeps re-scanning. A game whose executable sits outside its install
        /// directory (an emulator, say) never reaches it and is re-scanned for as long as it runs;
        /// that costs one throttled window enumeration over cached classifications, and it is what
        /// stops a launcher window from owning the session.
        /// </summary>
        public static bool IsConclusive(GameWindowCandidate candidate)
        {
            return !candidate.IsEmpty && Score(candidate) >= ConclusiveScore;
        }
    }
}
