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
    /// precedence rule between them, only between scopes. Providers seed the set, and once the user
    /// edits it the stored set is the whole truth for that game.
    ///
    /// Kept apart from the cache and the store, like <see cref="AutoCapstoneCalculator"/>, so the
    /// rules can be read and tested without a game behind them.
    /// </remarks>
    public sealed class CapstoneResolver
    {
        /// <summary>
        /// The group types that mean an achievement belongs to something the base game does not
        /// cover, and so gets a finish line of its own rather than the game's.
        /// </summary>
        private static readonly string[] CategoryScopedGroupTypes = { "DLC", "Subset" };

        private readonly Dictionary<string, string> _byCategory =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _effectiveApiNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _gameWideApiName;

        private CapstoneResolver()
        {
        }

        /// <summary>The capstone standing for the whole game, or null when none does.</summary>
        public string GameWideApiName => _gameWideApiName;

        /// <summary>
        /// Every achievement that is a capstone, deduplicated: a game-wide capstone covering three
        /// empty categories is still one capstone.
        /// </summary>
        public IReadOnlyCollection<string> EffectiveApiNames => _effectiveApiNames;

        public int Count => _effectiveApiNames.Count;

        public bool IsCapstone(string apiName)
        {
            return !string.IsNullOrWhiteSpace(apiName) && _effectiveApiNames.Contains(apiName.Trim());
        }

        /// <summary>
        /// The capstone standing for a category: its own, failing that the nearest ancestor's,
        /// failing that the game-wide one.
        /// </summary>
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

            return _gameWideApiName;
        }

        /// <summary>True when a category has a capstone of its own rather than an inherited one.</summary>
        public bool HasOwnCapstone(string categoryPath)
        {
            return _byCategory.ContainsKey(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryPath));
        }

        /// <summary>
        /// Resolves a game's capstones. Achievements must already carry their final categories:
        /// a category-scoped capstone is filed by its own achievement's category, so resolving
        /// before user category overrides are applied files it under the provider's label instead.
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
                if (!byApiName.TryGetValue(entry.Key, out var achievement))
                {
                    // A capstone whose achievement the provider no longer sends. Keeping it in the
                    // stored set lets it come back if the provider does; counting it here would
                    // leave the game permanently one capstone short of complete.
                    continue;
                }

                resolver._effectiveApiNames.Add(entry.Key);
                if (entry.Value)
                {
                    resolver._gameWideApiName = entry.Key;
                }

                // A capstone always claims its own category, game-wide or not. Later entries win,
                // so re-nominating within a category moves the capstone rather than duplicating it.
                resolver._byCategory[
                    AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category)] = entry.Key;
            }

            return resolver;
        }

        private static IEnumerable<KeyValuePair<string, bool>> EnumerateStored(
            IEnumerable<CapstoneAssignment> assignments)
        {
            foreach (var assignment in assignments ?? Enumerable.Empty<CapstoneAssignment>())
            {
                var apiName = NormalizeApiName(assignment?.ApiName);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    yield return new KeyValuePair<string, bool>(apiName, assignment.IsGameWide);
                }
            }
        }

        /// <summary>
        /// The provider's own capstones, scoped by what their achievements belong to: a platinum or
        /// a base-set mastery stands for the game, a DLC or subset mastery for its own category.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, bool>> SeedFromProviders(
            IEnumerable<AchievementDetail> achievements)
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
                    yield return new KeyValuePair<string, bool>(apiName, IsGameWideScope(achievement));
                }
            }
        }

        /// <summary>
        /// The scope a provider capstone is seeded with. Anything not marked as belonging to a DLC
        /// or a subset stands for the game as a whole.
        /// </summary>
        public static bool IsGameWideScope(AchievementDetail achievement)
        {
            var groups = AchievementCategoryTypeHelper.GetGroupTypeComponents(achievement?.CategoryType);
            return !groups.Any(group =>
                CategoryScopedGroupTypes.Any(scoped =>
                    string.Equals(group, scoped, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// The full set as it would be stored the moment the user first edits it: the provider seed,
        /// which is what makes a seeded capstone and a nominated one indistinguishable afterwards.
        /// </summary>
        public static List<CapstoneAssignment> Materialize(IEnumerable<AchievementDetail> achievements)
        {
            return SeedFromProviders(
                    (achievements ?? Enumerable.Empty<AchievementDetail>()).Where(a => a != null))
                .Select(entry => new CapstoneAssignment { ApiName = entry.Key, IsGameWide = entry.Value })
                .ToList();
        }

        private static string NormalizeApiName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
