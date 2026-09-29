using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Works out which of a game's achievements are capstones, and which capstone stands for each
    /// category.
    /// </summary>
    /// <remarks>
    /// A provider-supplied capstone and one the user nominated are the same thing here: there is no
    /// precedence rule between them. Providers seed the set, and once the user edits it the stored
    /// set is the whole truth for that game.
    ///
    /// Kept apart from the cache and the store, like <see cref="AutoCapstoneCalculator"/>, so the
    /// rules can be read and tested without a game behind them.
    /// </remarks>
    public sealed class CapstoneResolver
    {
        private readonly Dictionary<string, string> _byCategory =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _effectiveApiNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private CapstoneResolver()
        {
        }

        /// <summary>Every achievement that is a capstone.</summary>
        public IReadOnlyCollection<string> EffectiveApiNames => _effectiveApiNames;

        public int Count => _effectiveApiNames.Count;

        public bool IsCapstone(string apiName)
        {
            return !string.IsNullOrWhiteSpace(apiName) && _effectiveApiNames.Contains(apiName.Trim());
        }

        /// <summary>
        /// The capstone standing for a category: its own, failing that the nearest ancestor's.
        /// </summary>
        /// <remarks>
        /// Deliberately no whole-game fallback. Completion counts the capstones a category actually
        /// holds, so reporting an unrelated capstone as covering it would say one thing while the
        /// rollup did another.
        /// </remarks>
        public string ResolveForCategory(string categoryPath)
        {
            // Root-first from the helper, walked backwards: the nearest ancestor with a capstone
            // wins, so a subcategory only inherits what nothing closer already answers.
            var candidates = CategoryPathHelper.EnumerateSelfAndAncestors(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryPath));
            for (var i = candidates.Count - 1; i >= 0; i--)
            {
                if (_byCategory.TryGetValue(candidates[i], out var apiName))
                {
                    return apiName;
                }
            }

            return null;
        }

        /// <summary>True when a category has a capstone of its own rather than an inherited one.</summary>
        public bool HasOwnCapstone(string categoryPath)
        {
            return _byCategory.ContainsKey(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryPath));
        }

        /// <summary>
        /// Resolves a game's capstones. Achievements must already carry their final categories:
        /// a capstone is filed by its own achievement's category, so resolving before user category
        /// overrides are applied files it under the provider's label instead.
        /// </summary>
        /// <param name="assignments">
        /// The stored set, used only when <paramref name="materialized"/> is true.
        /// </param>
        /// <param name="materialized">
        /// True once the user has edited this game's capstones, at which point provider capstone
        /// flags no longer apply to it and an empty set means the game has none.
        /// </param>
        public static CapstoneResolver Resolve(
            IEnumerable<AchievementDetail> achievements,
            IEnumerable<CapstoneAssignment> assignments,
            bool materialized)
        {
            var resolver = new CapstoneResolver();
            var byApiName = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in achievements ?? Enumerable.Empty<AchievementDetail>())
            {
                var apiName = NormalizeApiName(achievement?.ApiName);
                if (!string.IsNullOrWhiteSpace(apiName) && !byApiName.ContainsKey(apiName))
                {
                    byApiName[apiName] = achievement;
                }
            }

            var entries = materialized
                ? EnumerateStored(assignments)
                : SeedFromProviders(byApiName.Values);

            foreach (var entry in entries)
            {
                if (!byApiName.TryGetValue(entry, out var achievement))
                {
                    // A capstone whose achievement the provider no longer sends. Keeping it in the
                    // stored set lets it come back if the provider does; counting it here would
                    // leave the game permanently one capstone short of complete.
                    continue;
                }

                resolver._effectiveApiNames.Add(entry);

                // Later entries win, so re-nominating within a category moves the capstone rather
                // than duplicating it.
                resolver._byCategory[
                    AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category)] = entry;
            }

            return resolver;
        }

        private static IEnumerable<string> EnumerateStored(IEnumerable<CapstoneAssignment> assignments)
        {
            foreach (var assignment in assignments ?? Enumerable.Empty<CapstoneAssignment>())
            {
                var apiName = NormalizeApiName(assignment?.ApiName);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    yield return apiName;
                }
            }
        }

        private static IEnumerable<string> SeedFromProviders(IEnumerable<AchievementDetail> achievements)
        {
            foreach (var achievement in achievements)
            {
                if (achievement?.IsCapstone != true)
                {
                    continue;
                }

                var apiName = NormalizeApiName(achievement.ApiName);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    yield return apiName;
                }
            }
        }

        /// <summary>
        /// The full set as it would be stored the moment the user first edits it: the provider seed,
        /// which is what makes a seeded capstone and a nominated one indistinguishable afterwards.
        /// </summary>
        public static List<CapstoneAssignment> Materialize(IEnumerable<AchievementDetail> achievements)
        {
            return SeedFromProviders(
                    (achievements ?? Enumerable.Empty<AchievementDetail>()).Where(a => a != null))
                .Select(apiName => new CapstoneAssignment { ApiName = apiName })
                .ToList();
        }

        private static string NormalizeApiName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
