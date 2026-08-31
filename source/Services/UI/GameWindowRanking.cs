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
        public GameWindowCandidate(
            IntPtr hwnd,
            GameWindowEvidence evidence,
            bool isManagedUiShell,
            DateTime processStartUtc,
            DateTime firstSeenUtc,
            long clientArea)
        {
            Hwnd = hwnd;
            Evidence = evidence;
            IsManagedUiShell = isManagedUiShell;
            ProcessStartUtc = processStartUtc;
            FirstSeenUtc = firstSeenUtc;
            ClientArea = clientArea;
        }

        public IntPtr Hwnd { get; }

        public GameWindowEvidence Evidence { get; }

        /// <summary>
        /// Whether the window is drawn by a managed UI toolkit — its class marks it as WinForms or
        /// WPF. No game renders its picture through one, so such a window is a launcher, picker,
        /// settings dialog or crash reporter rather than the surface being played.
        /// </summary>
        public bool IsManagedUiShell { get; }

        /// <summary>
        /// When the owning process started, or <see cref="DateTime.MinValue"/> when it could not be
        /// read. A launcher starts the game, so the game's process is always the younger one.
        /// </summary>
        public DateTime ProcessStartUtc { get; }

        /// <summary>When this window was first seen, for ordering windows of one process.</summary>
        public DateTime FirstSeenUtc { get; }

        /// <summary>Client area in square physical pixels. The last-resort tiebreak.</summary>
        public long ClientArea { get; }

        public bool IsEmpty => Hwnd == IntPtr.Zero;

        public override string ToString()
        {
            return $"0x{Hwnd.ToInt64():X} {Evidence}" +
                   $"{(IsManagedUiShell ? " managedUiShell" : string.Empty)} " +
                   $"procStart={Stamp(ProcessStartUtc)} firstSeen={Stamp(FirstSeenUtc)} " +
                   $"area={ClientArea}";
        }

        private static string Stamp(DateTime value)
        {
            return value > DateTime.MinValue ? value.ToString("HH:mm:ss.fff") : "?";
        }
    }

    /// <summary>
    /// Picks the game's window out of the several a game (or its launcher) has open, and decides
    /// when a newly observed window is good enough to replace the one already in use.
    ///
    /// The signals, in the order they decide:
    ///
    /// 1. Evidence tying the owning process to the game. This separates a store client's window
    ///    from the game's, because a launcher-wrapped title reaches Playnite as the launcher's
    ///    process while the game's executable lives under the install directory.
    /// 2. Whether the window is a managed UI shell. Evidence cannot separate two windows that share
    ///    a process or an install folder — Shenmue I &amp; II ships its game-picker launcher and both
    ///    games' executables under one folder — but the picker is a WinForms window and a game's
    ///    render surface never is.
    /// 3. Which process started later. A launcher's whole job is to start the game, so the game's
    ///    process is younger than the launcher's. This settles the launcher question the moment the
    ///    game's window exists, without waiting for it to be focused or drawn — which matters
    ///    because focus is not a reliable signal at all: a launcher holds it while the game starts,
    ///    and an overlay such as Lossless Scaling can hold it for the whole session.
    /// 4. Which window appeared later, for two windows of one process — a splash and the render
    ///    window that supersedes it.
    /// 5. Client area, as a last resort only.
    ///
    /// Pure: the caller supplies already-measured candidates, so the ranking is testable without a
    /// desktop. See <see cref="ActiveGameWindowTracker"/> for the enumeration, classification and
    /// measurement that produce them.
    /// </summary>
    internal static class GameWindowRanking
    {
        /// <summary>
        /// How much larger a window must be before size decides between two otherwise
        /// indistinguishable ones: half again the other's client area. Below that they count as
        /// comparably sized, so a window being dragged or a game changing resolution cannot make
        /// the target drift.
        /// </summary>
        private const int AreaMarginNumerator = 3;
        private const int AreaMarginDenominator = 2;

        /// <summary>
        /// Whether <paramref name="candidate"/> should take over from <paramref name="current"/>:
        /// better on the first signal that separates them, in the order documented on this class.
        /// Size alone moves the target only by the margin above, which gives the decision
        /// hysteresis so two comparable windows cannot trade a running capture back and forth.
        ///
        /// A fresh observation of the window already in use can pass, which retargets nothing (the
        /// handle is unchanged) and refreshes the record it is held under.
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

            if (candidate.IsManagedUiShell != current.IsManagedUiShell)
            {
                return current.IsManagedUiShell;
            }

            if (candidate.ProcessStartUtc != current.ProcessStartUtc)
            {
                return candidate.ProcessStartUtc > current.ProcessStartUtc;
            }

            if (candidate.FirstSeenUtc != current.FirstSeenUtc)
            {
                return candidate.FirstSeenUtc > current.FirstSeenUtc;
            }

            return IsSubstantiallyLarger(candidate, current);
        }

        /// <summary>
        /// The best of <paramref name="candidates"/> on the same signals, ordered strictly — no size
        /// margin — because this ranks one snapshot of the desktop against itself rather than
        /// deciding whether to move a running capture. The margin belongs to
        /// <see cref="ShouldReplace"/>, which the result is then put through. An empty candidate for
        /// an empty input.
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
                if (!candidate.IsEmpty && (best.IsEmpty || IsBetterInSnapshot(candidate, best)))
                {
                    best = candidate;
                }
            }

            return best;
        }

        private static bool IsBetterInSnapshot(GameWindowCandidate candidate, GameWindowCandidate best)
        {
            if (candidate.Evidence != best.Evidence)
            {
                return candidate.Evidence > best.Evidence;
            }

            if (candidate.IsManagedUiShell != best.IsManagedUiShell)
            {
                return best.IsManagedUiShell;
            }

            if (candidate.ProcessStartUtc != best.ProcessStartUtc)
            {
                return candidate.ProcessStartUtc > best.ProcessStartUtc;
            }

            if (candidate.FirstSeenUtc != best.FirstSeenUtc)
            {
                return candidate.FirstSeenUtc > best.FirstSeenUtc;
            }

            return candidate.ClientArea > best.ClientArea;
        }

        private static bool IsSubstantiallyLarger(GameWindowCandidate a, GameWindowCandidate b)
        {
            return a.ClientArea * AreaMarginDenominator > b.ClientArea * AreaMarginNumerator;
        }
    }
}
