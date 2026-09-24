using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
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
    }

    /// <summary>
    /// Builds the Manage Achievements Overview's breakdowns and customization counts. Free of the
    /// view model so the counting rules can be tested on their own.
    /// </summary>
    internal static class ManageOverviewSummaryBuilder
    {
        public const string CustomAchievementsLabelKey = "LOCPlayAch_ManageAchievements_Tab_Custom";
        public const string OrderLabelKey = "LOCPlayAch_ManageAchievements_Tab_AchievementOrder";
        public const string CategoryArtLabelKey = "LOCPlayAch_Column_CategoryArt";
        public const string NotificationsLabelKey = "LOCPlayAch_Settings_TabNotifications";
        public const string ProviderOverrideLabelKey = "LOCPlayAch_ManageAchievements_Overrides_ProviderHeader";
        public const string ExophaseEnrichmentLabelKey = "LOCPlayAch_ManageAchievements_Overrides_ExophaseEnrichmentHeader";
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

                var label = CategoryPathHelper.NormalizePath(
                    AchievementCategoryTypeHelper.NormalizeCategory(achievement.Category));
                if (!string.IsNullOrWhiteSpace(label) &&
                    !string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
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
                        AchievementCategoryTypeHelper.NormalizeCategory(a.Category));
                    if (string.Equals(category, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
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
        /// </remarks>
        public static IReadOnlyList<ManageOverviewCustomizationCount> BuildCustomizationCounts(GameCustomDataFile data)
        {
            var result = new List<ManageOverviewCustomizationCount>();
            if (data == null)
            {
                return result;
            }

            AddCount(result, CustomAchievementsLabelKey, data.CustomAchievements?.Count(item => item != null) ?? 0);

            var overrides = (data.AchievementOverrides ?? new Dictionary<string, AchievementOverride>())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                .Select(pair => pair.Value)
                .ToList();

            foreach (var entry in AchievementCustomizationFacetLabels.Ordered)
            {
                int count;
                switch (entry.Item1)
                {
                    case AchievementCustomizationFacet.DisplayName:
                        count = overrides.Count(o => HasText(o.DisplayName));
                        break;
                    case AchievementCustomizationFacet.Description:
                        count = overrides.Count(o => HasText(o.Description));
                        break;
                    case AchievementCustomizationFacet.UnlockedIcon:
                        count = overrides.Count(o => HasText(o.UnlockedIconPath));
                        break;
                    case AchievementCustomizationFacet.LockedIcon:
                        count = overrides.Count(o => HasText(o.LockedIconPath));
                        break;
                    case AchievementCustomizationFacet.Points:
                        count = overrides.Count(o => o.Points.HasValue);
                        break;
                    case AchievementCustomizationFacet.TrophyType:
                        count = overrides.Count(o => HasText(o.TrophyType));
                        break;
                    case AchievementCustomizationFacet.UnlockTime:
                        count = overrides.Count(o => o.UnlockTimeUtc.HasValue || o.ClearUnlockTime);
                        break;
                    case AchievementCustomizationFacet.Category:
                        count = overrides.Count(o => HasText(o.Category));
                        break;
                    case AchievementCustomizationFacet.CategoryType:
                        count = overrides.Count(o => HasText(o.CategoryType));
                        break;
                    case AchievementCustomizationFacet.Hidden:
                        count = overrides.Count(o => o.Hidden.HasValue);
                        break;
                    case AchievementCustomizationFacet.Note:
                        count = overrides.Count(o => HasText(o.Note));
                        break;
                    case AchievementCustomizationFacet.FilterScope:
                        count = CountDistinct((data.FilteredAchievementApiNames ?? new List<string>())
                            .Concat(data.SummaryFilteredAchievementApiNames ?? new List<string>()));
                        break;
                    case AchievementCustomizationFacet.Goal:
                        count = CountDistinct(data.GoalAchievementApiNames);
                        break;
                    case AchievementCustomizationFacet.Capstone:
                        count = data.CapstonesMaterialized
                            ? data.Capstones?.Count(item => item != null) ?? 0
                            : 0;
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
            AddFlag(result, NotificationsLabelKey, data.NotificationAppearanceOverride != null);
            AddFlag(
                result,
                ProviderOverrideLabelKey,
                data.ProviderOverride != null ||
                data.RetroAchievementsGameIdOverride.HasValue ||
                HasText(data.XeniaTitleIdOverride) ||
                HasText(data.ShadPS4MatchIdOverride) ||
                data.ForceUseExophase == true ||
                HasText(data.ExophaseSlugOverride));
            AddFlag(result, ExophaseEnrichmentLabelKey, HasText(data.ExophaseEnrichmentSlugOverride));
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
    }
}
