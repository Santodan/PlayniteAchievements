using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Summaries;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// One kind of stored customization. <see cref="Count"/> is null for a game-level setting,
    /// which is either present or not.
    /// </summary>
    internal sealed class ManageOverviewCustomizationCount
    {
        public ManageOverviewCustomizationCount(string labelKey, int? count)
        {
            LabelKey = labelKey;
            Count = count;
        }

        public string LabelKey { get; }

        public int? Count { get; }
    }

    /// <summary>The per-game numbers the Overview shows beyond completion.</summary>
    internal sealed class ManageOverviewBreakdown
    {
        public AchievementGameStats Stats { get; set; } = new AchievementGameStats();

        public int TotalPoints { get; set; }

        public int UnlockedPoints { get; set; }

        /// <summary>Achievements in a category other than the default one.</summary>
        public int CategorizedCount { get; set; }

        public int GoalCount { get; set; }

        public int UnlockedGoalCount { get; set; }

        public int CapstoneCount { get; set; }

        public int UnlockedCapstoneCount { get; set; }

        /// <summary>Achievements filtered from views or from summaries.</summary>
        public int FilteredCount { get; set; }

        public int NoteCount { get; set; }
    }

    /// <summary>
    /// Builds the Manage Achievements Overview's breakdowns and customization counts. Free of the
    /// view model so the counting rules can be tested on their own.
    /// </summary>
    internal static class ManageOverviewSummaryBuilder
    {
        public const string CustomAchievementsLabelKey = "LOCPlayAch_Common_Custom";
        public const string AutoCapstoneLabelKey = "LOCPlayAch_ManageAchievements_Custom_AutoCapstone";
        public const string OrderLabelKey = "LOCPlayAch_ManageAchievements_Tab_AchievementOrder";
        public const string CategoryArtLabelKey = "LOCPlayAch_Column_CategoryArt";
        public const string NotificationsLabelKey = "LOCPlayAch_Common_Notifications";
        public const string ProviderOverrideLabelKey = "LOCPlayAch_ManageAchievements_Overrides_ProviderHeader";
        public const string SeparateLockedIconsLabelKey = "LOCPlayAch_ManageAchievements_Overrides_LockedIconsHeader";

        public static ManageOverviewBreakdown BuildBreakdown(IEnumerable<AchievementDetail> achievements)
        {
            var list = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(a => a != null)
                .ToList();

            var breakdown = new ManageOverviewBreakdown
            {
                Stats = AchievementStatsAccumulator.FromAchievements(list)
            };

            foreach (var achievement in list)
            {
                var points = Math.Max(0, achievement.Points ?? 0);
                breakdown.TotalPoints = AchievementGameStats.AddClamped(breakdown.TotalPoints, points);

                if (achievement.Unlocked)
                {
                    breakdown.UnlockedPoints = AchievementGameStats.AddClamped(breakdown.UnlockedPoints, points);
                }

                if (achievement.IsGoal)
                {
                    breakdown.GoalCount++;
                    if (achievement.Unlocked)
                    {
                        breakdown.UnlockedGoalCount++;
                    }
                }

                if (achievement.IsCapstone)
                {
                    breakdown.CapstoneCount++;
                    if (achievement.Unlocked)
                    {
                        breakdown.UnlockedCapstoneCount++;
                    }
                }

                if (achievement.IsFiltered || achievement.IsFilteredFromSummaries)
                {
                    breakdown.FilteredCount++;
                }

                if (HasText(achievement.AchievementNote))
                {
                    breakdown.NoteCount++;
                }

                var label = CategoryPathHelper.NormalizePath(
                    AchievementCategoryHelper.NormalizeCategory(achievement.Category));
                if (!string.IsNullOrWhiteSpace(label) &&
                    !string.Equals(label, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
                {
                    breakdown.CategorizedCount++;
                }
            }

            return breakdown;
        }

        /// <summary>
        /// The game's capstones as (category, name) pairs, in the game's order. The category is
        /// null for a capstone in the default category, which stands for the whole game.
        /// </summary>
        public static IReadOnlyList<Tuple<string, string>> BuildCapstones(IEnumerable<AchievementDetail> achievements)
        {
            return (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(a => a != null && a.IsCapstone)
                .Select(a =>
                {
                    var category = CategoryPathHelper.NormalizePath(
                        AchievementCategoryHelper.NormalizeCategory(a.Category));
                    if (string.Equals(category, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        category = null;
                    }

                    var name = HasText(a.DisplayName) ? a.DisplayName.Trim() : (a.ApiName ?? string.Empty).Trim();
                    return Tuple.Create(string.IsNullOrWhiteSpace(category) ? null : category, name);
                })
                .ToList();
        }

        /// <summary>
        /// Counts what the game's stored custom data holds, kind by kind, in display order.
        /// Kinds with nothing stored are left out, so an empty result means "not customized".
        /// </summary>
        /// <remarks>
        /// Counts come from the stored record rather than from comparing against the provider, so
        /// they describe what an export of this game would carry. The schema-7 scalar maps are
        /// not read: they are folded into <see cref="GameCustomDataFile.AchievementOverrides"/> on
        /// migration.
        ///
        /// A custom achievement's own fields live on its definition rather than in an override
        /// record, so each per-achievement kind counts the achievements whose override or custom
        /// definition carries it, once each. Notes, goals and filters are ApiName-keyed for both
        /// kinds already.
        /// </remarks>
        public static IReadOnlyList<ManageOverviewCustomizationCount> BuildCustomizationCounts(GameCustomDataFile data)
        {
            var result = new List<ManageOverviewCustomizationCount>();
            if (data == null)
            {
                return result;
            }

            // Auto capstones are written by the plugin, not the user: one count of their own, and
            // left out of every other count, Custom included.
            var autoCapstones = (data.CustomAchievements ?? new List<CustomAchievementDefinition>())
                .Where(item => item?.IsAutoCapstone == true)
                .ToList();
            var autoCapstoneNames = new HashSet<string>(
                autoCapstones
                    .Select(item => CustomAchievementProjectionService.BuildApiName(item.Id))
                    .Where(HasText),
                StringComparer.OrdinalIgnoreCase);
            bool IsUserAchievement(string apiName) => !autoCapstoneNames.Contains(apiName?.Trim() ?? string.Empty);

            AddCount(
                result,
                CustomAchievementsLabelKey,
                data.CustomAchievements?.Count(item => item != null && !item.IsAutoCapstone) ?? 0);
            AddCount(result, AutoCapstoneLabelKey, autoCapstones.Count);

            var overrides = (data.AchievementOverrides ?? new Dictionary<string, AchievementOverride>())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null && IsUserAchievement(pair.Key))
                .ToList();
            var customs = (data.CustomAchievements ?? new List<CustomAchievementDefinition>())
                .Where(item => item != null && !item.IsAutoCapstone)
                .Select(item => new KeyValuePair<string, CustomAchievementDefinition>(
                    CustomAchievementProjectionService.BuildApiName(item.Id), item))
                .Where(pair => pair.Key != null)
                .ToList();
            int CountUserDistinct(IEnumerable<string> apiNames) =>
                CountDistinct((apiNames ?? Enumerable.Empty<string>()).Where(IsUserAchievement));

            int Count(Func<AchievementOverride, bool> inOverride, Func<CustomAchievementDefinition, bool> inCustom)
            {
                return CountDistinct(
                    overrides.Where(pair => inOverride(pair.Value)).Select(pair => pair.Key)
                        .Concat(customs.Where(pair => inCustom(pair.Value)).Select(pair => pair.Key)));
            }

            foreach (var entry in AchievementCustomizationFacetLabels.Ordered)
            {
                int count;
                switch (entry.Item1)
                {
                    case AchievementCustomizationFacet.DisplayName:
                        count = Count(o => HasText(o.DisplayName), c => HasText(c.DisplayName));
                        break;
                    case AchievementCustomizationFacet.Description:
                        count = Count(o => HasText(o.Description) || o.ClearDescription, c => HasText(c.Description));
                        break;
                    case AchievementCustomizationFacet.UnlockedIcon:
                        count = Count(o => HasText(o.UnlockedIconPath), c => HasText(c.UnlockedIconPath));
                        break;
                    case AchievementCustomizationFacet.LockedIcon:
                        count = Count(o => HasText(o.LockedIconPath), c => HasText(c.LockedIconPath));
                        break;
                    case AchievementCustomizationFacet.Points:
                        count = Count(o => o.Points.HasValue, c => c.Points.HasValue);
                        break;
                    case AchievementCustomizationFacet.TrophyType:
                        count = Count(o => HasText(o.TrophyType) || o.ClearTrophyType, c => HasText(c.TrophyType));
                        break;
                    case AchievementCustomizationFacet.UnlockTime:
                        count = Count(
                            o => o.UnlockTimeUtc.HasValue || o.ClearUnlockTime,
                            c => c.Unlocked && c.UnlockTimeUtc.HasValue);
                        break;
                    case AchievementCustomizationFacet.Category:
                        count = Count(o => HasText(o.Category), c => IsNamedCategory(c.Category));
                        break;
                    case AchievementCustomizationFacet.Tags:
                        count = Count(o => o.ClearTags || o.Tags != null, c => HasText(c.Tags));
                        break;
                    case AchievementCustomizationFacet.Hidden:
                        count = Count(o => o.Hidden.HasValue, c => c.Hidden);
                        break;
                    case AchievementCustomizationFacet.Note:
                        count = overrides.Count(pair => HasText(pair.Value.Note));
                        break;
                    case AchievementCustomizationFacet.FilterScope:
                        count = CountUserDistinct((data.FilteredAchievementApiNames ?? new List<string>())
                            .Concat(data.SummaryFilteredAchievementApiNames ?? new List<string>()));
                        break;
                    case AchievementCustomizationFacet.Goal:
                        count = CountUserDistinct(data.GoalAchievementApiNames);
                        break;
                    case AchievementCustomizationFacet.Capstone:
                        // A materialized set names every capstone, custom ones included; before
                        // that, a custom achievement's own flag is what makes it one.
                        count = data.CapstonesMaterialized
                            ? CountUserDistinct(data.Capstones?.Where(item => item != null).Select(item => item.ApiName))
                            : customs.Count(pair => pair.Value.IsCapstone);
                        break;
                    default:
                        continue;
                }

                AddCount(result, entry.Item2, count);
            }

            AddCount(
                result,
                CategoryArtLabelKey,
                data.AchievementCategoryImageOverrides?.Count(pair => HasText(pair.Value?.Art)) ?? 0);

            AddFlag(result, OrderLabelKey, data.AchievementOrder?.Count > 0 || data.AchievementCategoryOrder?.Count > 0);
            AddFlag(
                result,
                NotificationsLabelKey,
                data.NotificationAppearanceOverride != null ||
                data.UnlockSounds != null ||
                data.NotificationBehaviorOverride != null ||
                data.CaptureOverride != null);
            AddFlag(
                result,
                ProviderOverrideLabelKey,
                data.ProviderOverride != null ||
                data.RetroAchievementsGameIdOverride.HasValue ||
                HasText(data.XeniaTitleIdOverride) ||
                HasText(data.ShadPS4MatchIdOverride) ||
                data.ForceUseExophase == true ||
                HasText(data.ExophaseSlugOverride));
            AddFlag(result, SeparateLockedIconsLabelKey, data.UseSeparateLockedIconsOverride == true);

            return result;
        }

        private static void AddCount(List<ManageOverviewCustomizationCount> result, string labelKey, int count)
        {
            if (count > 0)
            {
                result.Add(new ManageOverviewCustomizationCount(labelKey, count));
            }
        }

        private static void AddFlag(List<ManageOverviewCustomizationCount> result, string labelKey, bool isSet)
        {
            if (isSet)
            {
                result.Add(new ManageOverviewCustomizationCount(labelKey, null));
            }
        }

        private static int CountDistinct(IEnumerable<string> apiNames)
        {
            return (apiNames ?? Enumerable.Empty<string>())
                .Where(HasText)
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);

        // A custom achievement left in the Default bucket has chosen
        // nothing; the projection files it there when the field is blank.
        private static bool IsNamedCategory(string category) =>
            HasText(category) &&
            !string.Equals(
                CategoryPathHelper.NormalizePath(category),
                AchievementCategoryHelper.DefaultCategoryLabel,
                StringComparison.OrdinalIgnoreCase);
    }
}
