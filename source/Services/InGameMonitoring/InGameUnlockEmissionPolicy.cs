using System;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    /// <summary>
    /// The single decision of whether an observed unlock may notify, shared by both in-game prongs:
    /// the fast source (file watcher or feed) and the universal provider-refresh prong. Both prongs
    /// run concurrently for the same game, so they must agree — a gate that differs between them
    /// makes the same unlock notify or stay silent depending only on which prong observed it first.
    /// </summary>
    internal static class InGameUnlockEmissionPolicy
    {
        /// <summary>
        /// A read taken before the session baseline exists is silent, so a freshly cleared or
        /// never-refreshed game cannot surface its earned backlog. The provider-reported unlock time
        /// is the exception that keeps that guarantee from also discarding real unlocks: a backlog
        /// carries older timestamps, while an unlock stamped inside this session cannot be part of
        /// one. Without it, any mid-session re-baseline (a non-equivalent re-registration, or a
        /// prong reading first) silently drops unlocks the player just earned.
        /// </summary>
        /// <param name="primed">Whether a successful read has already established the session baseline.</param>
        /// <param name="sessionStartUtc">When the monitor began tracking this game.</param>
        /// <param name="unlockTimeUtc">Provider-reported unlock time, or null when it supplies none.</param>
        public static bool ShouldEmit(bool primed, DateTime sessionStartUtc, DateTime? unlockTimeUtc)
        {
            return primed ||
                (unlockTimeUtc.HasValue &&
                 unlockTimeUtc.Value.ToUniversalTime() >= sessionStartUtc);
        }
    }
}
