using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services
{
    public static class CustomAchievementProjectionService
    {
        public const string ProviderKey = "Custom";
        public const string ApiNamePrefix = "custom:";

        public static bool HasCustomAchievements(GameCustomDataFile data)
        {
            return data?.CustomAchievements != null &&
                   data.CustomAchievements.Any(definition =>
                       definition != null &&
                       !string.IsNullOrWhiteSpace(definition.DisplayName));
        }

        public static bool HasCustomAchievements(GameCustomDataPortableFile data)
        {
            return data?.CustomAchievements != null &&
                   data.CustomAchievements.Any(definition =>
                       definition != null &&
                       !string.IsNullOrWhiteSpace(definition.DisplayName));
        }

        public static string BuildApiName(string id)
        {
            var normalizedId = NormalizeId(id);
            return string.IsNullOrWhiteSpace(normalizedId) ? null : ApiNamePrefix + normalizedId;
        }

        public static bool IsCustomApiName(string apiName)
        {
            return !string.IsNullOrWhiteSpace(apiName) &&
                   apiName.Trim().StartsWith(ApiNamePrefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryGetCustomId(string apiName, out string id)
        {
            id = null;
            var normalized = NormalizeText(apiName);
            if (string.IsNullOrWhiteSpace(normalized) ||
                !normalized.StartsWith(ApiNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            id = NormalizeId(normalized.Substring(ApiNamePrefix.Length));
            return !string.IsNullOrWhiteSpace(id);
        }

        public static string NormalizeId(string id)
        {
            var normalized = NormalizeText(id);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (normalized.StartsWith(ApiNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(ApiNamePrefix.Length);
            }

            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        public static string GenerateId(string displayName, ISet<string> existingIds = null)
        {
            var baseId = Slugify(displayName);
            if (string.IsNullOrWhiteSpace(baseId))
            {
                baseId = "achievement";
            }

            existingIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidate = baseId;
            var suffix = 2;
            while (existingIds.Contains(candidate))
            {
                candidate = baseId + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            return candidate;
        }

        public static List<AchievementDetail> ProjectAchievements(
            Guid playniteGameId,
            IEnumerable<CustomAchievementDefinition> definitions,
            ManagedCustomIconService managedCustomIconService = null)
        {
            var projected = new List<AchievementDetail>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gameIdText = playniteGameId == Guid.Empty ? null : playniteGameId.ToString("D");

            foreach (var definition in definitions ?? Enumerable.Empty<CustomAchievementDefinition>())
            {
                var displayName = NormalizeText(definition?.DisplayName);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var id = NormalizeId(definition.Id);
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = GenerateId(displayName, usedIds);
                }

                if (!usedIds.Add(id))
                {
                    continue;
                }

                var unlocked = definition.Unlocked;
                var unlockTimeUtc = unlocked
                    ? NormalizeUtc(definition.UnlockTimeUtc)
                    : null;

                projected.Add(new AchievementDetail
                {
                    ApiName = BuildApiName(id),
                    DisplayName = displayName,
                    Description = NormalizeText(definition.Description),
                    Unlocked = unlocked,
                    UnlockTimeUtc = unlockTimeUtc,
                    UnlockedIconPath = ResolveIconPath(definition.UnlockedIconPath, gameIdText, managedCustomIconService),
                    LockedIconPath = ResolveIconPath(definition.LockedIconPath, gameIdText, managedCustomIconService),
                    Points = NormalizeNonNegative(definition.Points),
                    ScaledPoints = NormalizeNonNegative(definition.ScaledPoints),
                    Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(definition.Category),
                    CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(definition.CategoryType),
                    TrophyType = NormalizeTrophyType(definition.TrophyType),
                    Hidden = definition.Hidden,
                    IsCapstone = definition.IsCapstone,
                    Rarity = ParseRarity(definition.Rarity),
                    GlobalPercentUnlocked = NormalizePercent(definition.GlobalPercentUnlocked),
                    ProgressNum = NormalizeProgressNum(definition.ProgressNum, definition.ProgressDenom),
                    ProgressDenom = NormalizeProgressDenom(definition.ProgressNum, definition.ProgressDenom),
                    IsCustom = true,
                    ProviderKey = ProviderKey
                });
            }

            return projected;
        }

        /// <param name="providerPlatformKey">
        /// Display provider key for the game (a resolved <c>Custom:&lt;id&gt;</c> key), or null to
        /// display as plain Custom. Rides the same field aggregator-serviced games use, so every
        /// surface that reads <see cref="GameAchievementData.EffectiveProviderKey"/> shows it.
        /// </param>
        public static GameAchievementData CreateSyntheticGameData(
            Guid playniteGameId,
            Game game,
            IEnumerable<CustomAchievementDefinition> definitions,
            ManagedCustomIconService managedCustomIconService = null,
            string providerPlatformKey = null)
        {
            var achievements = ProjectAchievements(playniteGameId, definitions, managedCustomIconService);
            if (achievements.Count == 0)
            {
                return null;
            }

            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = ProviderKey,
                ProviderPlatformKey = NormalizeText(providerPlatformKey),
                LibrarySourceName = game?.Source?.Name,
                HasAchievements = true,
                GameName = game?.Name,
                PlayniteGameId = playniteGameId,
                Game = game,
                Achievements = achievements
            };
        }

        private static string ResolveIconPath(
            string value,
            string gameIdText,
            ManagedCustomIconService managedCustomIconService)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return !string.IsNullOrWhiteSpace(gameIdText)
                ? managedCustomIconService?.ResolveManagedDisplayPath(normalized, gameIdText) ?? normalized
                : normalized;
        }

        private static DateTime? NormalizeUtc(DateTime? value)
        {
            if (!value.HasValue || value.Value == DateTime.MinValue)
            {
                return null;
            }

            var date = value.Value;
            if (date.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }

            return date.ToUniversalTime();
        }

        private static int? NormalizeNonNegative(int? value)
        {
            return value.HasValue && value.Value >= 0 ? value : null;
        }

        private static double? NormalizePercent(double? value)
        {
            return value.HasValue && value.Value >= 0 && value.Value <= 100 ? value : null;
        }

        private static int? NormalizeProgressDenom(int? num, int? denom)
        {
            return denom.HasValue &&
                   denom.Value > 0 &&
                   (!num.HasValue || (num.Value >= 0 && num.Value <= denom.Value))
                ? denom
                : null;
        }

        private static int? NormalizeProgressNum(int? num, int? denom)
        {
            return denom.HasValue &&
                   denom.Value > 0 &&
                   num.HasValue &&
                   num.Value >= 0 &&
                   num.Value <= denom.Value
                ? num
                : null;
        }

        private static RarityTier ParseRarity(string value)
        {
            return RarityTierExtensions.TryParse(value, out var rarity)
                ? rarity
                : RarityTier.Common;
        }

        private static string NormalizeTrophyType(string value)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            switch (normalized.ToLowerInvariant())
            {
                case "bronze":
                case "silver":
                case "gold":
                case "platinum":
                    return normalized.ToLowerInvariant();
                default:
                    return null;
            }
        }

        private static string Slugify(string value)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var builder = new StringBuilder(normalized.Length);
            var previousSeparator = false;
            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    previousSeparator = false;
                }
                else if (!previousSeparator)
                {
                    builder.Append('-');
                    previousSeparator = true;
                }
            }

            return builder.ToString().Trim('-');
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
