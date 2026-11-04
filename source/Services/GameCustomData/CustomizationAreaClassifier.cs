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
    /// category and nominates it. It counts toward Capstones only: entries keyed by an auto
    /// capstone's ApiName are left out of every other area, so generation alone tags a game
    /// Custom Capstones and default everywhere else.
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

            // A custom achievement's own values are the user's, like an override on a platform
            // achievement.
            var customs = (data.CustomAchievements ?? new List<CustomAchievementDefinition>())
                .Where(item => item != null && !item.IsAutoCapstone)
                .ToList();
            bool AnyCustom(Func<CustomAchievementDefinition, bool> predicate) => customs.Any(predicate);
            bool Named(string category) => Metadata.DataLayers.NamedCategory(category) != null;

            var areas = CustomizationArea.None;

            if (data.UseSeparateLockedIconsOverride == true ||
                AnyUserKey(data.AchievementUnlockedIconOverrides, HasText) ||
                AnyUserKey(data.AchievementLockedIconOverrides, HasText) ||
                AnyOverride(o => HasText(o.UnlockedIconPath) || HasText(o.LockedIconPath)) ||
                AnyCustom(c => HasText(c.UnlockedIconPath) || HasText(c.LockedIconPath)))
            {
                areas |= CustomizationArea.Icons;
            }

            if (AnyOverride(o => HasText(o.TrophyType) || o.ClearTrophyType) ||
                AnyCustom(c => HasText(c.TrophyType)))
            {
                areas |= CustomizationArea.TrophyTypes;
            }

            if (AnyOverride(o => o.Points.HasValue) ||
                AnyCustom(c => c.Points.HasValue))
            {
                areas |= CustomizationArea.Points;
            }

            // A custom achievement's name and description are always the user's.
            if (AnyOverride(o => HasText(o.DisplayName) || o.Description != null || o.ClearDescription) ||
                customs.Count > 0)
            {
                areas |= CustomizationArea.Text;
            }

            if (AnyUserKey(data.AchievementNotes, HasText) || AnyOverride(o => HasText(o.Note)))
            {
                areas |= CustomizationArea.Notes;
            }

            // Only a named category counts: an achievement filed under Default has none.
            if (AnyUserKey(data.AchievementCategoryOverrides, Named) ||
                AnyUserKey(data.AchievementCategoryTypeOverrides, HasText) ||
                data.AchievementCategoryOrder?.Any(Named) == true ||
                data.AchievementCategoryImageOverrides?.Keys.Any(Named) == true ||
                data.AchievementCategoryKinds?.Keys.Any(Named) == true ||
                data.GameSummaryCategory != null ||
                AnyOverride(o => Named(o.Category) || HasText(o.LegacyCategoryType)) ||
                AnyCustom(c => Named(c.Category)))
            {
                areas |= CustomizationArea.Categories;
            }

            if (data.AchievementTags?.Count > 0 ||
                AnyOverride(o => o.Tags != null || o.ClearTags) ||
                AnyCustom(c => HasText(c.Tags)))
            {
                areas |= CustomizationArea.Tags;
            }

            if (AnyUserName(data.AchievementOrder))
            {
                areas |= CustomizationArea.Order;
            }

            if (HasText(data.ManualCapstoneApiName) ||
                data.CapstonesMaterialized ||
                autoCapstoneNames.Count > 0 ||
                AnyCustom(c => c.IsCapstone))
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

        private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);
    }
}
