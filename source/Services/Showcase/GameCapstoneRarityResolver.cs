using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// Picks the global unlock percent a game cover's rarity bar shows: the game's capstone, or
    /// with no capstone at all, its rarest achievement.
    /// </summary>
    /// <remarks>
    /// Several capstones means DLC or subset sets alongside the base game's. The Base-typed one
    /// stands for the game, as in <see cref="AutoCapstoneTemplate.SelectPlatinum{T}"/>; when none
    /// is typed, the rarest capstone does. Only unlocked achievements with a real percent count:
    /// the bar describes what was earned, and a tier alone has no percent to draw. A 0 percent
    /// reads as absent, as in AutoCapstoneCalculator.
    /// </remarks>
    public static class GameCapstoneRarityResolver
    {
        /// <summary>The per-game percents one pass over the unlocked achievements yields.</summary>
        public sealed class Candidates
        {
            public double? BaseCapstone { get; internal set; }
            public double? RarestCapstone { get; internal set; }
            public double? RarestAchievement { get; internal set; }
        }

        /// <summary>Groups the unlocks with a percent by their Playnite game.</summary>
        public static Dictionary<Guid, Candidates> Index(IEnumerable<UnlockFact> unlocks)
        {
            var index = new Dictionary<Guid, Candidates>();
            foreach (var fact in unlocks ?? Enumerable.Empty<UnlockFact>())
            {
                if (fact?.MinPercent == null || fact.GameId == Guid.Empty)
                {
                    continue;
                }

                if (!index.TryGetValue(fact.GameId, out var candidates))
                {
                    candidates = new Candidates();
                    index[fact.GameId] = candidates;
                }

                candidates.RarestAchievement = Min(candidates.RarestAchievement, fact.MinPercent);
                if (fact.Tally.IsCapstone)
                {
                    candidates.RarestCapstone = Min(candidates.RarestCapstone, fact.MinPercent);
                    candidates.BaseCapstone = Min(candidates.BaseCapstone, fact.MinBasePercent);
                }
            }

            return index;
        }

        /// <summary>
        /// The percent to show for a game, or null for no bar. A game with capstones shows only a
        /// capstone's percent; one without falls back to its rarest achievement.
        /// </summary>
        public static double? Resolve(Candidates candidates, bool hasCapstones)
        {
            if (candidates == null)
            {
                return null;
            }

            return hasCapstones
                ? candidates.BaseCapstone ?? candidates.RarestCapstone
                : candidates.RarestAchievement;
        }

        private static double? Min(double? current, double? value) =>
            current.HasValue && value.HasValue ? Math.Min(current.Value, value.Value) : current ?? value;
    }
}
