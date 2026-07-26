using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels.Items;
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
                    false,
                    new List<AchievementDetail>(),
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

            var game = data.Game;
            for (int i = 0; i < achievements.Count; i++)
            {
                if (achievements[i] != null)
                {
                    ApplyAchievementPresentation(achievements[i], data);
                }
            }

            var stats = AchievementStatsAccumulator.FromAchievements(achievements);
            var locked = stats.LockedAchievements;
            var percent = stats.ProgressPercent;
            var hasCustomOrder = data.AchievementOrder != null && data.AchievementOrder.Count > 0;
            var defaultOrder = hasCustomOrder
                ? AchievementOrderHelper.ApplyOrder(
                    achievements,
                    achievement => achievement?.ApiName,
                    data.AchievementOrder)
                : achievements.ToList();
            var all = hasCustomOrder
                ? defaultOrder
                : AchievementSortHelper.CreateDefaultSortedDetailList(achievements);
            var oldestFirst = AchievementSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.UnlockTime),
                ListSortDirection.Ascending);
            var newestFirst = AchievementSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.UnlockTime),
                ListSortDirection.Descending);
            var rarityAsc = AchievementSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.RaritySortValue),
                ListSortDirection.Ascending);
            var rarityDesc = AchievementSortHelper.CreateSortedDetailList(
                all,
                nameof(AchievementDisplayItem.RaritySortValue),
                ListSortDirection.Descending);

            var common = stats.CommonStats;
            var uncommon = stats.UncommonStats;
            var rare = stats.RareStats;
            var ultra = stats.UltraRareStats;
            var rareAndUltra = AchievementRarityStatsCombiner.Combine(rare, ultra);

            return new SelectedGameRuntimeState(
                gameId,
                data.LastUpdatedUtc,
                true,
                stats.TotalAchievements,
                stats.UnlockedAchievements,
                locked,
                percent,
                data.IsCompleted,
                hasCustomOrder,
                defaultOrder,
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

        private static void ApplyAchievementPresentation(
            AchievementDetail achievement,
            GameAchievementData data)
        {
            if (achievement == null)
            {
                return;
            }

            // Modern compact lists resolve tooltip game name from AchievementDetail.Game.
            // Ensure selected-game snapshots always carry this context.
            achievement.Game = data?.Game;
            achievement.ProviderKey = data?.EffectiveProviderKey;
            ApplyCategoryImagePresentation(achievement, data);
        }

        private static void ApplyCategoryImagePresentation(
            AchievementDetail achievement,
            GameAchievementData data)
        {
            if (achievement == null)
            {
                return;
            }

            CategoryImageOverrideData imageOverride = null;
            var category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category);
            achievement.CategoryOrderIndex =
                AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndex(category, data?.AchievementCategoryOrder);
            if (!string.IsNullOrWhiteSpace(category) &&
                data?.AchievementCategoryImageOverrides != null)
            {
                data.AchievementCategoryImageOverrides.TryGetValue(category, out imageOverride);
            }

            // Default images are keyed by the provider label; renames only change Category.
            var providerCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                achievement.ProviderCategory ?? achievement.Category);
            achievement.CategoryArtPath =
                NormalizeImageOverridePath(imageOverride?.Art) ??
                CategoryDefaultImageResolver.Resolve(data?.PlayniteGameId, providerCategory);
        }

        private static string NormalizeImageOverridePath(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

    }
}

