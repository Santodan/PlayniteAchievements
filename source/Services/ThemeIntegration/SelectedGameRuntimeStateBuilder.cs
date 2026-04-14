using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Models.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
                return new SelectedGameRuntimeState(
                    gameId,
                    data.LastUpdatedUtc,
                    false,
                    0,
                    0,
                    0,
                    0,
                    false,
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
                    achievements[i].ProviderKey = data.EffectiveProviderKey;
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
            var oldestFirst = AchievementGridSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.UnlockTime),
                ListSortDirection.Ascending);
            var newestFirst = AchievementGridSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.UnlockTime),
                ListSortDirection.Descending);
            var rarityAsc = AchievementGridSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.RaritySortValue),
                ListSortDirection.Ascending);
            var rarityDesc = AchievementGridSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.RaritySortValue),
                ListSortDirection.Descending);

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
