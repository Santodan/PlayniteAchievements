using System;
using System.Collections.Generic;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services
{
    public sealed class AchievementProjectionOptions
    {
        public bool ShowHiddenIcon { get; set; }
        public bool ShowHiddenTitle { get; set; }
        public bool ShowHiddenDescription { get; set; }
        public bool ShowHiddenSuffix { get; set; } = true;
        public bool ShowLockedIcon { get; set; } = true;
        public bool ShowRarityGlow { get; set; } = true;
        public bool UseScaledPoints { get; set; }
        public ISet<string> RevealedKeys { get; set; }
    }

    public static class AchievementProjectionService
    {
        public static AchievementProjectionOptions CreateOptions(
            PlayniteAchievementsSettings settings,
            GameAchievementData gameData,
            ISet<string> revealedKeys = null)
        {
            return new AchievementProjectionOptions
            {
                ShowHiddenIcon = settings?.Persisted?.ShowHiddenIcon ?? false,
                ShowHiddenTitle = settings?.Persisted?.ShowHiddenTitle ?? false,
                ShowHiddenDescription = settings?.Persisted?.ShowHiddenDescription ?? false,
                ShowHiddenSuffix = settings?.Persisted?.ShowHiddenSuffix ?? true,
                ShowLockedIcon = settings?.Persisted?.ShowLockedIcon ?? true,
                ShowRarityGlow = settings?.Persisted?.ShowRarityGlow ?? true,
                UseScaledPoints = (ProviderRegistry.Settings<RetroAchievementsSettings>().RaPointsMode == "scaled") &&
                                  string.Equals(gameData?.ProviderKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase),
                RevealedKeys = revealedKeys
            };
        }

        public static AchievementDisplayItem CreateDisplayItem(
            GameAchievementData gameData,
            AchievementDetail achievement,
            AchievementProjectionOptions options,
            Guid? playniteGameIdOverride = null)
        {
            if (achievement == null)
            {
                return null;
            }

            var iconPath = !string.IsNullOrWhiteSpace(achievement.UnlockedIconPath)
                ? achievement.UnlockedIconPath
                : achievement.LockedIconPath;
            var gameId = playniteGameIdOverride ?? gameData?.PlayniteGameId;
            var item = new AchievementDisplayItem
            {
                ProviderKey = achievement.ProviderKey ?? gameData?.ProviderKey,
                GameName = gameData?.GameName ?? "Unknown",
                SortingName = gameData?.SortingName ?? gameData?.GameName ?? "Unknown",
                PlayniteGameId = gameId,
                DisplayName = achievement.DisplayName ?? achievement.ApiName ?? "Unknown",
                Description = achievement.Description ?? string.Empty,
                IconPath = iconPath,
                UnlockTimeUtc = achievement.UnlockTimeUtc,
                GlobalPercentUnlocked = achievement.Percent,
                Unlocked = achievement.Unlocked,
                Hidden = achievement.Hidden,
                ApiName = achievement.ApiName,
                ProgressNum = achievement.ProgressNum,
                ProgressDenom = achievement.ProgressDenom,
                PointsValue = ResolvePoints(achievement, options),
                TrophyType = achievement.TrophyType,
                CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(achievement.CategoryType),
                CategoryLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category),
                IsRevealed = IsRevealed(gameData, achievement, options, gameId)
            };

            ApplyAppearanceSettings(item, options);
            return item;
        }

        public static AchievementDisplayItem CreateRecentItem(
            GameAchievementData gameData,
            AchievementDetail achievement,
            AchievementProjectionOptions options,
            string gameIconPath,
            string gameCoverPath)
        {
            if (achievement == null || !achievement.Unlocked || !achievement.UnlockTimeUtc.HasValue)
            {
                return null;
            }

            var iconPath = !string.IsNullOrWhiteSpace(achievement.UnlockedIconPath)
                ? achievement.UnlockedIconPath
                : achievement.LockedIconPath;
            var item = new AchievementDisplayItem
            {
                ProviderKey = achievement.ProviderKey ?? gameData?.ProviderKey,
                ApiName = achievement.ApiName,
                PlayniteGameId = gameData?.PlayniteGameId,
                DisplayName = achievement.DisplayName ?? achievement.ApiName ?? "Unknown",
                Description = achievement.Description ?? string.Empty,
                GameName = gameData?.GameName ?? "Unknown",
                SortingName = gameData?.SortingName ?? gameData?.GameName ?? "Unknown",
                IconPath = iconPath,
                UnlockTimeUtc = achievement.UnlockTimeUtc.Value,
                GlobalPercentUnlocked = achievement.Percent,
                PointsValue = ResolvePoints(achievement, options),
                ProgressNum = achievement.ProgressNum,
                ProgressDenom = achievement.ProgressDenom,
                GameIconPath = gameIconPath,
                GameCoverPath = gameCoverPath,
                Hidden = achievement.Hidden,
                Unlocked = true, // Recent achievements are always unlocked by definition
                TrophyType = achievement.TrophyType,
                CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(achievement.CategoryType),
                CategoryLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category)
            };

            ApplyAppearanceSettings(item, options);
            return item;
        }

        public static void ApplyAppearanceSettings(AchievementDisplayItem item, AchievementProjectionOptions options)
        {
            if (item == null)
            {
                return;
            }

            item.ShowHiddenIcon = options?.ShowHiddenIcon ?? false;
            item.ShowHiddenTitle = options?.ShowHiddenTitle ?? false;
            item.ShowHiddenDescription = options?.ShowHiddenDescription ?? false;
            item.ShowHiddenSuffix = options?.ShowHiddenSuffix ?? true;
            item.ShowLockedIcon = options?.ShowLockedIcon ?? true;
            item.ShowRarityGlow = options?.ShowRarityGlow ?? true;
        }

        public static bool IsAppearanceSettingPropertyName(string propertyName)
        {
            switch (NormalizePersistedPropertyName(propertyName))
            {
                case nameof(PersistedSettings.ShowHiddenIcon):
                case nameof(PersistedSettings.ShowHiddenTitle):
                case nameof(PersistedSettings.ShowHiddenDescription):
                case nameof(PersistedSettings.ShowHiddenSuffix):
                case nameof(PersistedSettings.ShowLockedIcon):
                case nameof(PersistedSettings.ShowRarityGlow):
                    return true;
                default:
                    return false;
            }
        }

        public static void AccumulateRarity(AchievementDetail achievement, ref int common, ref int uncommon, ref int rare, ref int ultraRare)
        {
            var tier = achievement?.Rarity;
            if (!tier.HasValue)
            {
                return;
            }

            switch (tier.Value)
            {
                case RarityTier.UltraRare:
                    ultraRare++;
                    break;
                case RarityTier.Rare:
                    rare++;
                    break;
                case RarityTier.Uncommon:
                    uncommon++;
                    break;
                default:
                    common++;
                    break;
            }
        }

        public static void AccumulateTrophy(AchievementDetail achievement, ref int platinum, ref int gold, ref int silver, ref int bronze)
        {
            AccumulateTrophy(achievement?.TrophyType, ref platinum, ref gold, ref silver, ref bronze);
        }

        public static void AccumulateTrophy(string trophyType, ref int platinum, ref int gold, ref int silver, ref int bronze)
        {
            if (string.IsNullOrWhiteSpace(trophyType))
            {
                return;
            }

            switch (trophyType.Trim().ToLowerInvariant())
            {
                case "platinum":
                    platinum++;
                    break;
                case "gold":
                    gold++;
                    break;
                case "silver":
                    silver++;
                    break;
                case "bronze":
                    bronze++;
                    break;
            }
        }

        public static string MakeRevealKey(Guid? playniteGameId, string apiName, string gameName)
        {
            var gamePart = playniteGameId?.ToString() ?? (gameName ?? string.Empty);
            return $"{gamePart}\u001f{apiName ?? string.Empty}";
        }

        private static int? ResolvePoints(AchievementDetail achievement, AchievementProjectionOptions options)
        {
            if (achievement == null)
            {
                return null;
            }

            if (options?.UseScaledPoints == true)
            {
                return achievement.ScaledPoints ?? achievement.Points;
            }

            return achievement.Points;
        }

        private static bool IsRevealed(
            GameAchievementData gameData,
            AchievementDetail achievement,
            AchievementProjectionOptions options,
            Guid? gameId)
        {
            if (achievement == null || !achievement.Hidden || achievement.Unlocked)
            {
                return false;
            }

            var hidesAny = !(options?.ShowHiddenIcon ?? false) ||
                           !(options?.ShowHiddenTitle ?? false) ||
                           !(options?.ShowHiddenDescription ?? false);
            if (!hidesAny || options?.RevealedKeys == null || options.RevealedKeys.Count == 0)
            {
                return false;
            }

            var key = MakeRevealKey(gameId, achievement.ApiName, gameData?.GameName);
            return options.RevealedKeys.Contains(key);
        }

        private static string NormalizePersistedPropertyName(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return string.Empty;
            }

            const string persistedPrefix = "Persisted.";
            return propertyName.StartsWith(persistedPrefix, StringComparison.Ordinal)
                ? propertyName.Substring(persistedPrefix.Length)
                : propertyName;
        }
    }
}




