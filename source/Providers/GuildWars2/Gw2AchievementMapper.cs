using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Pure mapping from the Guild Wars 2 catalog plus an account's progress to
    /// <see cref="AchievementDetail"/>. Free of HTTP, WPF and Playnite dependencies so it can be
    /// unit tested against captured payloads.
    /// </summary>
    internal static class Gw2AchievementMapper
    {
        /// <summary>Flag marking an achievement the game hides until it is earned.</summary>
        private const string HiddenFlag = "Hidden";

        /// <summary>
        /// Group ids that carry a canonical category classification. Group *names* are localized -
        /// "Historical" is served as "Historisch" to a German client - so the mapping keys on the
        /// group GUIDs, which are the same in every language.
        /// </summary>
        private static readonly Dictionary<string, string> CategoryTypeByGroupId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Historical: content that has been retired and can no longer be earned.
                ["A9F7378E-9C8A-48CC-9505-3094E661D5F6"] = "Missable",

                // Collections.
                ["45410F60-AB66-4146-A0F7-CE99250C4CB0"] = "Collectable",

                // Player vs. Player and World vs. World.
                ["BE8B9954-5B55-4FCB-9022-B871AD00EAAB"] = "Multiplayer",
                ["9B66605F-6589-4CBD-8E19-5368C9F33338"] = "Multiplayer"
            };

        /// <summary>
        /// Builds the achievement list in the order the in-game panel presents it: group order, then
        /// category order, then the category's own achievement order, then up the tier ladder.
        /// </summary>
        public static List<AchievementDetail> BuildAchievements(
            Gw2Catalog catalog,
            IReadOnlyDictionary<int, Gw2AccountAchievement> accountProgress)
        {
            var results = new List<AchievementDetail>();
            if (catalog == null || !catalog.IsUsable)
            {
                return results;
            }

            var achievementsById = BuildAchievementIndex(catalog.Achievements);
            var categoriesById = BuildCategoryIndex(catalog.Categories);
            var progressById = accountProgress ?? new Dictionary<int, Gw2AccountAchievement>();

            // An achievement can be listed by more than one category. The first group and category
            // to claim it in display order wins, so its identity and category path stay stable.
            var claimed = new HashSet<int>();

            foreach (var group in catalog.Groups.Where(g => g != null).OrderBy(g => g.Order))
            {
                foreach (var category in ResolveCategories(group, categoriesById))
                {
                    if (category.Achievements == null)
                    {
                        continue;
                    }

                    var categoryPath = CategoryPathHelper.JoinRaw(group.Name, category.Name);
                    var categoryType = ResolveCategoryType(group.Id);

                    foreach (var achievementId in category.Achievements)
                    {
                        if (!claimed.Add(achievementId) ||
                            !achievementsById.TryGetValue(achievementId, out var achievement))
                        {
                            continue;
                        }

                        progressById.TryGetValue(achievementId, out var progress);

                        results.AddRange(BuildTierAchievements(
                            achievement,
                            category,
                            categoryPath,
                            categoryType,
                            progress));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// One achievement per tier of a ladder. A Guild Wars 2 achievement is earned again at each
        /// successive tier, so a tier is the only thing here that is actually all-or-nothing, and
        /// modelling it that way lets a tier climb be an ordinary locked-to-unlocked transition.
        /// </summary>
        private static IEnumerable<AchievementDetail> BuildTierAchievements(
            Gw2Achievement achievement,
            Gw2Category category,
            string categoryPath,
            string categoryType,
            Gw2AccountAchievement progress)
        {
            var tiers = NormalizeTiers(achievement.Tiers);
            var current = progress?.Current ?? 0;

            // "done" covers a finished ladder, where the API stops reporting a running total, and
            // "repeated" covers a repeatable achievement that has been round-tripped at least once.
            var completed = progress != null && (progress.Done || (progress.Repeated ?? 0) > 0);

            // Only 9% of achievements carry their own art; the category icon covers the rest, and
            // every one of the 355 categories has one.
            var icon = FirstNonBlank(achievement.Icon, category?.Icon);
            var description = FirstNonBlank(achievement.Description, achievement.Requirement);
            var hidden = achievement.Flags?.Contains(HiddenFlag) == true;
            var multiTier = tiers.Count > 1;

            for (var index = 0; index < tiers.Count; index++)
            {
                var tier = tiers[index];

                var detail = new AchievementDetail
                {
                    ApiName = BuildTierApiName(achievement.Id, index + 1),

                    // Unlike Riot's challenge ladders, every tier of a Guild Wars 2 achievement
                    // shares one icon, so without the threshold the rows would be indistinguishable
                    // from each other in both name and art.
                    DisplayName = multiTier
                        ? BuildTierDisplayName(achievement.Name, tier.Count)
                        : achievement.Name,

                    Description = description,
                    UnlockedIconPath = icon,

                    // There is no separate locked variant; the display layer derives the greyscale
                    // locked rendering from the same source.
                    LockedIconPath = icon,

                    Points = tier.Points,
                    Unlocked = completed || current >= tier.Count,

                    // The Guild Wars 2 API records no unlock time anywhere, for any achievement.
                    // An invented timestamp would be a lie the unlock feed would then order by.
                    UnlockTimeUtc = null,

                    Category = categoryPath,
                    CategoryType = categoryType,
                    Hidden = hidden,

                    // No global completion rate is published, and no third-party source carries one.
                    // Leaving the percent null and the tier at its default is how the cache layer
                    // recognizes "unknown" and declines to overwrite a stored value.
                    GlobalPercentUnlocked = null
                };

                ApplyTierProgress(detail, current, tier.Count);

                yield return detail;
            }
        }

        /// <summary>
        /// Stable per-tier identity. It keys the icon cache, overrides, goals and notes, so it must
        /// never shift with a rename or a locale change. The tier index is used rather than its
        /// threshold because ArenaNet retunes thresholds on existing ladders but appends new tiers
        /// to the end, so an index survives the commoner of the two changes.
        /// </summary>
        internal static string BuildTierApiName(int achievementId, int tierNumber)
            => achievementId.ToString(CultureInfo.InvariantCulture) + ":t" +
               tierNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Distinguishes the rows of a ladder by the running total the tier is earned at, which is
        /// the same number the in-game panel shows on its progress bar.
        /// </summary>
        internal static string BuildTierDisplayName(string name, int tierCount)
            => $"{name} ({tierCount.ToString("N0", CultureInfo.CurrentCulture)})";

        /// <summary>
        /// Progress toward this tier's own threshold. An earned tier reads full; the tiers above it
        /// show how far the same running value has come, which is what a locked achievement's
        /// progress bar means everywhere else.
        /// </summary>
        private static void ApplyTierProgress(AchievementDetail detail, int current, int threshold)
        {
            if (threshold <= 0)
            {
                return;
            }

            detail.ProgressNum = Math.Max(0, Math.Min(current, threshold));
            detail.ProgressDenom = threshold;
        }

        /// <summary>
        /// The ladder in ascending threshold order. An achievement with no tiers still yields one
        /// row, so a definition change upstream cannot make it vanish from the list.
        /// </summary>
        private static List<Gw2Tier> NormalizeTiers(List<Gw2Tier> tiers)
        {
            var usable = tiers?.Where(t => t != null).OrderBy(t => t.Count).ToList();
            if (usable == null || usable.Count == 0)
            {
                return new List<Gw2Tier> { new Gw2Tier { Count = 1, Points = 0 } };
            }

            return usable;
        }

        /// <summary>
        /// The group's categories in display order. A category id a group lists but the catalog does
        /// not carry is skipped rather than faulting the whole build.
        /// </summary>
        private static IEnumerable<Gw2Category> ResolveCategories(
            Gw2Group group,
            IReadOnlyDictionary<int, Gw2Category> categoriesById)
        {
            if (group?.Categories == null)
            {
                return Enumerable.Empty<Gw2Category>();
            }

            return group.Categories
                .Select(id => categoriesById.TryGetValue(id, out var category) ? category : null)
                .Where(category => category != null)
                .OrderBy(category => category.Order);
        }

        private static string ResolveCategoryType(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return null;
            }

            // Null rather than a sentinel label: the hydrator supplies the default classification,
            // and a hardcoded English string would bypass it and render untranslated.
            return CategoryTypeByGroupId.TryGetValue(groupId.Trim(), out var type) ? type : null;
        }

        private static Dictionary<int, Gw2Achievement> BuildAchievementIndex(List<Gw2Achievement> achievements)
        {
            var index = new Dictionary<int, Gw2Achievement>();
            if (achievements == null)
            {
                return index;
            }

            foreach (var achievement in achievements)
            {
                if (achievement != null)
                {
                    index[achievement.Id] = achievement;
                }
            }

            return index;
        }

        private static Dictionary<int, Gw2Category> BuildCategoryIndex(List<Gw2Category> categories)
        {
            var index = new Dictionary<int, Gw2Category>();
            if (categories == null)
            {
                return index;
            }

            foreach (var category in categories)
            {
                if (category != null)
                {
                    index[category.Id] = category;
                }
            }

            return index;
        }

        /// <summary>
        /// The distinct achievement ids the categories reference, in category order. An achievement
        /// can be listed by more than one category, so duplicates are dropped before paging.
        /// </summary>
        public static List<int> CollectAchievementIds(IReadOnlyList<Gw2Category> categories)
        {
            var ids = new List<int>();
            var seen = new HashSet<int>();

            if (categories == null)
            {
                return ids;
            }

            foreach (var category in categories.Where(c => c != null).OrderBy(c => c.Order))
            {
                if (category.Achievements == null)
                {
                    continue;
                }

                foreach (var id in category.Achievements)
                {
                    if (id > 0 && seen.Add(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }

        /// <summary>Indexes the account's progress by achievement id, tolerating repeated ids.</summary>
        public static Dictionary<int, Gw2AccountAchievement> BuildProgressIndex(
            IReadOnlyList<Gw2AccountAchievement> accountAchievements)
        {
            var index = new Dictionary<int, Gw2AccountAchievement>();
            if (accountAchievements == null)
            {
                return index;
            }

            foreach (var entry in accountAchievements)
            {
                if (entry != null)
                {
                    index[entry.Id] = entry;
                }
            }

            return index;
        }

        private static string FirstNonBlank(params string[] candidates)
            => candidates?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
