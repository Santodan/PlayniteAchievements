using PlayniteAchievements.Models.Achievements;
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
            public Counts(int total, int unlocked, int achievementCount, int achievementsUnlocked)
            {
                Total = total;
                Unlocked = unlocked;
                AchievementCount = achievementCount;
                AchievementsUnlocked = achievementsUnlocked;
            }

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

                    if (achievement.IsCapstone)
                    {
                        total++;
                        if (achievement.Unlocked)
                        {
                            unlocked++;
                        }
                    }
                }
            }

            return new Counts(total, unlocked, achievementCount, achievementsUnlocked);
        }

        public static bool IsCompleted(IEnumerable<AchievementDetail> achievements)
        {
            return Count(achievements).IsCompleted;
        }
    }
}
