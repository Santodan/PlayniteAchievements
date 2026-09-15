using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// What a game's capstones say about finishing it.
    /// </summary>
    /// <remarks>
    /// One place for the rule so the game rollup, the category rollup and the summary SQL cannot
    /// drift apart. Callers pass achievements whose <see cref="AchievementDetail.IsCapstone"/> has
    /// already been resolved, which hydration does.
    ///
    /// Completion needs <em>every</em> capstone, not any one of them. A platinum earned while a DLC
    /// pack is still open does not finish the game, and before multiple capstones existed the two
    /// readings could not differ.
    /// </remarks>
    public static class CapstoneCompletion
    {
        public struct Counts
        {
            public Counts(
                int total,
                int unlocked,
                int achievementCount,
                int achievementsUnlocked,
                bool capstonesMatchPlatinums)
            {
                Total = total;
                Unlocked = unlocked;
                AchievementCount = achievementCount;
                AchievementsUnlocked = achievementsUnlocked;
                CapstonesMatchPlatinums = capstonesMatchPlatinums;
            }

            /// <summary>
            /// True when the game's capstones are exactly its platinum trophies, which is the
            /// ordinary PlayStation case, or when it has neither. The badge row shows the platinum
            /// in the capstone spot rather than twice when this holds.
            /// </summary>
            public bool CapstonesMatchPlatinums { get; }

            public int Total { get; }

            public int Unlocked { get; }

            public int AchievementCount { get; }

            public int AchievementsUnlocked { get; }

            public bool AllAchievementsUnlocked =>
                AchievementCount > 0 && AchievementsUnlocked >= AchievementCount;

            /// <summary>
            /// True when the game is finished: every achievement unlocked, or every capstone.
            /// </summary>
            public bool IsCompleted => AllAchievementsUnlocked || (Total > 0 && Unlocked >= Total);

            /// <summary>
            /// How many times this game has been finished. A game with capstones contributes one
            /// per capstone earned; a game without any contributes one for a clean 100%, so a
            /// platform that names no finish line still counts for something.
            /// </summary>
            public int Completions => Total > 0 ? Unlocked : (AllAchievementsUnlocked ? 1 : 0);
        }

        public static Counts Count(IEnumerable<AchievementDetail> achievements)
        {
            var total = 0;
            var unlocked = 0;
            var achievementCount = 0;
            var achievementsUnlocked = 0;

            // Identity, not tallies: one capstone and one platinum that are different achievements
            // must not read as the same thing.
            var capstonesThatAreNotPlatinum = 0;
            var platinumsThatAreNotCapstones = 0;

            if (achievements != null)
            {
                foreach (var achievement in achievements)
                {
                    if (achievement == null)
                    {
                        continue;
                    }

                    achievementCount++;
                    if (achievement.Unlocked)
                    {
                        achievementsUnlocked++;
                    }

                    var isPlatinum = string.Equals(
                        (achievement.TrophyType ?? string.Empty).Trim(),
                        "platinum",
                        StringComparison.OrdinalIgnoreCase);

                    if (achievement.IsCapstone)
                    {
                        total++;
                        if (achievement.Unlocked)
                        {
                            unlocked++;
                        }

                        if (!isPlatinum)
                        {
                            capstonesThatAreNotPlatinum++;
                        }
                    }
                    else if (isPlatinum)
                    {
                        platinumsThatAreNotCapstones++;
                    }
                }
            }

            return new Counts(
                total,
                unlocked,
                achievementCount,
                achievementsUnlocked,
                // A game with no capstones hands the spot to its platinum outright; one with
                // capstones only does so when they are exactly its platinums.
                capstonesThatAreNotPlatinum == 0 &&
                (total == 0 || platinumsThatAreNotCapstones == 0));
        }

        public static bool IsCompleted(IEnumerable<AchievementDetail> achievements)
        {
            return Count(achievements).IsCompleted;
        }
    }
}
