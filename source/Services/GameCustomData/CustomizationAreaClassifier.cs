using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>
    /// Sorts a game's stored customization into <see cref="CustomizationArea"/>s for the per-area
    /// tag pairs.
    /// </summary>
    /// <remarks>
    /// Automatic capstone generation writes a custom achievement, places it first, files it in a
    /// category and nominates it, none of which the user chose. Entries keyed by an auto
    /// capstone's ApiName are left out of every area, so generation alone tags a game as default
    /// throughout.
    /// </remarks>
    internal static class CustomizationAreaClassifier
    {
        public static CustomizationArea Classify(GameCustomDataFile data)
        {
            if (data == null)
            {
                return CustomizationArea.None;
            }

            var autoCapstoneNames = new HashSet<string>(
                (data.CustomAchievements ?? new List<CustomAchievementDefinition>())
                    .Where(item => item?.IsAutoCapstone == true)
                    .Select(item => CustomAchievementProjectionService.BuildApiName(item.Id))
                    .Where(HasText),
                StringComparer.OrdinalIgnoreCase);
            bool IsUser(string apiName) =>
                HasText(apiName) && !autoCapstoneNames.Contains(apiName.Trim());
            bool AnyUserKey<T>(IDictionary<string, T> map, Func<T, bool> predicate) =>
                map != null && map.Any(pair => IsUser(pair.Key) && predicate(pair.Value));
            bool AnyUserName(IEnumerable<string> apiNames) =>
                apiNames != null && apiNames.Any(IsUser);

            var overrides = data.AchievementOverrides;
            bool AnyOverride(Func<AchievementOverride, bool> predicate) =>
                AnyUserKey(overrides, o => o != null && predicate(o));

            var areas = CustomizationArea.None;

            if (data.UseSeparateLockedIconsOverride == true ||
                AnyUserKey(data.AchievementUnlockedIconOverrides, HasText) ||
                AnyUserKey(data.AchievementLockedIconOverrides, HasText) ||
                AnyOverride(o => HasText(o.UnlockedIconPath) || HasText(o.LockedIconPath)))
            {
                areas |= CustomizationArea.Icons;
            }

            if (AnyOverride(o => HasText(o.TrophyType) || o.ClearTrophyType))
            {
                areas |= CustomizationArea.TrophyTypes;
            }

            if (AnyOverride(o => o.Points.HasValue))
            {
                areas |= CustomizationArea.Points;
            }

            if (AnyUserKey(data.AchievementNotes, HasText) || AnyOverride(o => HasText(o.Note)))
            {
                areas |= CustomizationArea.Notes;
            }

            if (AnyUserKey(data.AchievementCategoryOverrides, HasText) ||
                AnyUserKey(data.AchievementCategoryTypeOverrides, HasText) ||
                data.AchievementCategoryOrder?.Count > 0 ||
                data.AchievementCategoryImageOverrides?.Count > 0 ||
                data.AchievementCategoryKinds?.Count > 0 ||
                data.AchievementTags?.Count > 0 ||
                data.GameSummaryCategory != null ||
                AnyOverride(o => HasText(o.Category) || HasText(o.LegacyCategoryType) || o.Tags != null || o.ClearTags))
            {
                areas |= CustomizationArea.Categories;
            }

            if (AnyUserName(data.AchievementOrder))
            {
                areas |= CustomizationArea.Order;
            }

            if (HasUserCapstones(data, IsUser))
            {
                areas |= CustomizationArea.Capstones;
            }

            if (AnyUserName(data.GoalAchievementApiNames))
            {
                areas |= CustomizationArea.Goals;
            }

            if (data.ProviderOverride != null ||
                HasText(data.ExophaseEnrichmentSlugOverride) ||
                data.RetroAchievementsGameIdOverride > 0 ||
                HasText(data.XeniaTitleIdOverride) ||
                HasText(data.ShadPS4MatchIdOverride) ||
                data.ForceUseExophase == true ||
                HasText(data.ExophaseSlugOverride) ||
                GameCustomDataNormalizer.HasUserMetadataLink(data.MetadataLinks) ||
                data.MetadataChoices?.IsEmpty() == false)
            {
                areas |= CustomizationArea.Source;
            }

            if (data.ManualLink != null ||
                HasText(data.CustomProviderId) ||
                data.CustomAchievements?.Any(item => item != null && !item.IsAutoCapstone) == true)
            {
                areas |= CustomizationArea.ManualTracking;
            }

            if (data.NotificationAppearanceOverride != null ||
                data.UnlockSounds != null ||
                data.NotificationBehaviorOverride != null ||
                data.CaptureOverride != null)
            {
                areas |= CustomizationArea.Notifications;
            }

            return areas;
        }

        /// <summary>
        /// A legacy manual capstone, or a materialized set the user shaped: one naming an
        /// achievement other than an auto capstone, or an empty one, which records that the
        /// provider's capstones were removed.
        /// </summary>
        private static bool HasUserCapstones(GameCustomDataFile data, Func<string, bool> isUser)
        {
            if (HasText(data.ManualCapstoneApiName))
            {
                return true;
            }

            if (!data.CapstonesMaterialized)
            {
                return false;
            }

            var names = (data.Capstones ?? new List<CapstoneAssignment>())
                .Where(item => item != null && HasText(item.ApiName))
                .Select(item => item.ApiName)
                .ToList();
            return names.Count == 0 || names.Any(isUser);
        }

        private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);
    }
}
