using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Edits to a game's custom data that touch only the achievements they name, for a writer that
    /// does not hold the game's other rows.
    /// </summary>
    /// <remarks>
    /// The per-game editor rewrites a game's category map, tag map, filter lists and icon maps whole
    /// from its rows. The library editor lists a page of rows across games, so a whole rewrite from
    /// it would drop every entry of the rows it has not read. Each patch here changes the named
    /// entries and keeps every other entry as stored. Kept apart from the store so the rules can be
    /// tested without a game behind them.
    /// </remarks>
    public static class GameCustomDataPatches
    {
        /// <summary>
        /// Sets one string field on the named achievements' override records. A null or blank
        /// value clears the field, so the achievement takes the provider's value again; records
        /// left with nothing on them are dropped.
        /// </summary>
        public static void SetOverrideField(
            GameCustomDataFile customData,
            IReadOnlyDictionary<string, string> valuesByApiName,
            Action<AchievementOverride, string> apply)
        {
            if (customData == null || apply == null || valuesByApiName == null || valuesByApiName.Count == 0)
            {
                return;
            }

            var overrides = CloneOverrides(customData);
            foreach (var pair in valuesByApiName)
            {
                var apiName = NormalizeApiName(pair.Key);
                if (apiName == null)
                {
                    continue;
                }

                var value = string.IsNullOrWhiteSpace(pair.Value) ? null : pair.Value.Trim();
                if (!overrides.TryGetValue(apiName, out var entry) || entry == null)
                {
                    if (value == null)
                    {
                        continue;
                    }

                    entry = new AchievementOverride();
                    overrides[apiName] = entry;
                }

                apply(entry, value);
            }

            StoreOverrides(customData, overrides);
        }

        /// <summary>
        /// Sets the named achievements' tag overrides. A null value takes the provider's tags; an
        /// empty one removes every tag.
        /// </summary>
        public static void SetTagOverrides(
            GameCustomDataFile customData,
            IReadOnlyDictionary<string, string> tagsByApiName)
        {
            if (customData == null || tagsByApiName == null || tagsByApiName.Count == 0)
            {
                return;
            }

            var overrides = CloneOverrides(customData);
            foreach (var pair in tagsByApiName)
            {
                var apiName = NormalizeApiName(pair.Key);
                if (apiName == null)
                {
                    continue;
                }

                if (!overrides.TryGetValue(apiName, out var entry) || entry == null)
                {
                    if (pair.Value == null)
                    {
                        continue;
                    }

                    entry = new AchievementOverride();
                    overrides[apiName] = entry;
                }

                if (pair.Value == null)
                {
                    entry.Tags = null;
                    entry.ClearTags = false;
                    continue;
                }

                var tags = AchievementTagHelper.StripDerived(pair.Value);
                entry.Tags = tags;
                entry.ClearTags = tags == null;
            }

            StoreOverrides(customData, overrides);
        }

        /// <summary>Drops the named achievements' override records whole.</summary>
        public static void RemoveOverrides(GameCustomDataFile customData, IEnumerable<string> apiNames)
        {
            if (customData?.AchievementOverrides == null || apiNames == null)
            {
                return;
            }

            var overrides = CloneOverrides(customData);
            var removed = false;
            foreach (var name in apiNames)
            {
                var apiName = NormalizeApiName(name);
                removed |= apiName != null && overrides.Remove(apiName);
            }

            if (removed)
            {
                StoreOverrides(customData, overrides);
            }
        }

        /// <summary>
        /// A list of ApiNames with the named achievements added or removed: an achievement mapped
        /// to true is added at the end when absent, one mapped to false is removed. Entries not
        /// named keep their places. Null when the list ends up empty.
        /// </summary>
        public static List<string> SetMembership(
            IEnumerable<string> apiNames,
            IReadOnlyDictionary<string, bool> membershipByApiName)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var membership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in membershipByApiName ?? new Dictionary<string, bool>())
            {
                var apiName = NormalizeApiName(pair.Key);
                if (apiName != null)
                {
                    membership[apiName] = pair.Value;
                }
            }

            foreach (var name in apiNames ?? Enumerable.Empty<string>())
            {
                var apiName = NormalizeApiName(name);
                if (apiName == null ||
                    (membership.TryGetValue(apiName, out var member) && !member) ||
                    !seen.Add(apiName))
                {
                    continue;
                }

                result.Add(apiName);
            }

            foreach (var pair in membership)
            {
                if (pair.Value && seen.Add(pair.Key))
                {
                    result.Add(pair.Key);
                }
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>Copies the override records, so a mutation leaves the stored ones alone until stored.</summary>
        internal static Dictionary<string, AchievementOverride> CloneOverrides(GameCustomDataFile customData)
        {
            var clone = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            if (customData?.AchievementOverrides == null)
            {
                return clone;
            }

            foreach (var pair in customData.AchievementOverrides)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                {
                    clone[pair.Key] = pair.Value.Clone();
                }
            }

            return clone;
        }

        /// <summary>Stores the override records, dropping those with nothing left on them.</summary>
        internal static void StoreOverrides(
            GameCustomDataFile customData,
            Dictionary<string, AchievementOverride> overrides)
        {
            var pruned = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in overrides)
            {
                if (pair.Value != null && !pair.Value.IsEmpty)
                {
                    pruned[pair.Key] = pair.Value;
                }
            }

            customData.AchievementOverrides = pruned.Count > 0 ? pruned : null;
        }

        private static string NormalizeApiName(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
