using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class SelectedGameRuntimeStateBuilder
    {
        public static SelectedGameRuntimeState Build(
            Guid gameId,
            GameAchievementData data)
        {
            if (data == null || !data.HasAchievements)
            {
                return SelectedGameRuntimeState.Empty;
            }

            var achievements = data.Achievements ?? new List<AchievementDetail>();
            if (achievements.Count == 0)
            {
                var totalCount = data.AchievementCount;
                var unlockedCount = data.UnlockedCount;
                var lockedCount = Math.Max(0, totalCount - unlockedCount);
                var progressPercent = AchievementCompletionPercentCalculator.ComputeRoundedPercent(unlockedCount, totalCount);

                return new SelectedGameRuntimeState(
                    gameId,
                    data.LastUpdatedUtc,
                    totalCount > 0,
                    totalCount,
                    unlockedCount,
                    lockedCount,
                    progressPercent,
                    data.IsCompleted,
                    new List<AchievementDetail>(),
                    new List<AchievementDetail>(),
                    new List<AchievementDetail>(),
                    new List<AchievementDetail>(),
                    new List<AchievementDetail>(),
                    new AchievementRarityStats(),
                    new AchievementRarityStats(),
                    new AchievementRarityStats(),
                    new AchievementRarityStats(),
                    new AchievementRarityStats());
            }

            var total = achievements.Count;
            var game = data.Game;
            for (int i = 0; i < achievements.Count; i++)
            {
                if (achievements[i] != null)
                {
                    // Modern compact lists resolve tooltip game name from AchievementDetail.Game.
                    // Ensure selected-game snapshots always carry this context.
                    achievements[i].Game = game;
                }
            }

            var unlocked = 0;
            for (int i = 0; i < achievements.Count; i++)
            {
                if (achievements[i]?.Unlocked == true)
                {
                    unlocked++;
                }
            }

            var locked = total - unlocked;
            var percent = AchievementCompletionPercentCalculator.ComputeRoundedPercent(unlocked, total);
            var all = achievements.ToList();
            var oldestFirst = all
                .OrderBy(a => a?.UnlockTimeUtc)
                .ThenBy(a => a?.DisplayName)
                .ToList();
            var newestFirst = all
                .OrderByDescending(a => a?.UnlockTimeUtc)
                .ThenBy(a => a?.DisplayName)
                .ToList();
            var rarityAsc = all
                .OrderBy(a => a?.RaritySortValue ?? double.MaxValue)
                .ThenByDescending(a => a?.Points ?? 0)
                .ThenBy(a => a?.DisplayName)
                .ToList();
            var rarityDesc = all
                .OrderByDescending(a => a?.RaritySortValue ?? double.MinValue)
                .ThenByDescending(a => a?.Points ?? 0)
                .ThenBy(a => a?.DisplayName)
                .ToList();

            var common = new AchievementRarityStats();
            var uncommon = new AchievementRarityStats();
            var rare = new AchievementRarityStats();
            var ultra = new AchievementRarityStats();

            for (int i = 0; i < all.Count; i++)
            {
                var achievement = all[i];
                if (achievement == null)
                {
                    continue;
                }

                var target = achievement.Rarity switch
                {
                    RarityTier.UltraRare => ultra,
                    RarityTier.Rare => rare,
                    RarityTier.Uncommon => uncommon,
                    _ => common
                };

                target.Total++;
                if (achievement.Unlocked)
                {
                    target.Unlocked++;
                }
                else
                {
                    target.Locked++;
                }
            }

            var rareAndUltra = AchievementRarityStatsCombiner.Combine(rare, ultra);

            return new SelectedGameRuntimeState(
                gameId,
                data.LastUpdatedUtc,
                true,
                total,
                unlocked,
                locked,
                percent,
                data.IsCompleted,
                all,
                oldestFirst,
                newestFirst,
                rarityAsc,
                rarityDesc,
                common,
                uncommon,
                rare,
                ultra,
                rareAndUltra);
        }

    }
}
