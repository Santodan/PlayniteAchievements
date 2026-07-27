using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Resolves a manual achievement's unlock state from a <see cref="ManualAchievementLink"/>
    /// using flexible, multi-key matching. Shared by <see cref="ManualAchievementsProvider"/>
    /// (which writes the cache during refresh) and the manual-tracking window view model
    /// (which drives the editor) so both agree on which stored key unlocks an achievement.
    ///
    /// Matching must tolerate Exophase's ApiName scheme change (legacy "exophase_&lt;name&gt;" to
    /// stable "exophase:&lt;id&gt;"): a link stored under a legacy key still resolves against a
    /// freshly fetched achievement whose ApiName is the stable form, via the DisplayName and
    /// normalized fallbacks in <see cref="BuildUnlockLookupKeys"/>.
    /// </summary>
    public sealed class ManualUnlockResolver
    {
        private readonly ManualAchievementLink _link;
        private readonly Dictionary<string, bool> _stateLookup;
        private readonly Dictionary<string, DateTime?> _timeLookup;

        public ManualUnlockResolver(ManualAchievementLink link)
        {
            _link = link;
            _stateLookup = BuildCaseInsensitiveLookup(link?.UnlockStates);
            _timeLookup = BuildCaseInsensitiveLookup(link?.UnlockTimes);
        }

        /// <summary>
        /// Resolves whether <paramref name="detail"/> is unlocked per the link, and its unlock time.
        /// Unlock state takes precedence when stored; otherwise a stored unlock time implies unlocked.
        /// Returns the resolved unlocked flag.
        /// </summary>
        public bool TryResolveUnlock(AchievementDetail detail, out bool isUnlocked, out DateTime? unlockTimeUtc)
        {
            isUnlocked = false;
            unlockTimeUtc = null;

            if (detail == null || string.IsNullOrWhiteSpace(detail.ApiName))
            {
                return false;
            }

            var keys = BuildUnlockLookupKeys(_link, detail);

            var hasState = TryGetLookupValue(_stateLookup, keys, out var storedState);
            var hasTime = TryGetLookupValue(_timeLookup, keys, out var storedTime);

            isUnlocked = hasState
                ? storedState
                : (hasTime && storedTime.HasValue);

            if (isUnlocked)
            {
                unlockTimeUtc = hasTime && storedTime.HasValue
                    ? storedTime
                    : null;
            }

            return isUnlocked;
        }

        /// <summary>
        /// Applies resolved unlock state onto each achievement in place. Only sets unlocked
        /// achievements; leaves locked ones untouched (matching provider refresh behavior).
        /// </summary>
        public void ApplyUnlockState(IEnumerable<AchievementDetail> achievements)
        {
            if (achievements == null)
            {
                return;
            }

            foreach (var detail in achievements)
            {
                if (TryResolveUnlock(detail, out _, out var unlockTimeUtc))
                {
                    detail.Unlocked = true;
                    detail.UnlockTimeUtc = unlockTimeUtc;
                }
            }
        }

        private static Dictionary<string, T> BuildCaseInsensitiveLookup<T>(IReadOnlyDictionary<string, T> source)
        {
            if (source == null || source.Count == 0)
            {
                return null;
            }

            var lookup = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                var key = pair.Key?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                lookup[key] = pair.Value;
            }

            return lookup;
        }

        private static bool TryGetLookupValue<T>(
            IDictionary<string, T> lookup,
            IReadOnlyList<string> keys,
            out T value)
        {
            value = default(T);
            if (lookup == null || keys == null || keys.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < keys.Count; i++)
            {
                if (lookup.TryGetValue(keys[i], out value))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> BuildUnlockLookupKeys(ManualAchievementLink link, AchievementDetail detail)
        {
            var keys = new List<string>(5);
            AddLookupKey(keys, detail?.ApiName);

            if (string.Equals(link?.SourceKey, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                AddLookupKey(keys, ExophaseApiClient.NormalizeLegacyManualApiName(detail?.ApiName));
                AddLookupKey(keys, detail?.DisplayName);
                AddLookupKey(keys, ExophaseApiClient.NormalizeLegacyManualApiName(detail?.DisplayName));

                var apiName = detail?.ApiName?.Trim();
                if (!string.IsNullOrWhiteSpace(apiName) &&
                    apiName.StartsWith(ExophaseApiClient.ExophaseApiNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    AddLookupKey(keys, apiName.Substring(ExophaseApiClient.ExophaseApiNamePrefix.Length));
                }
            }

            return keys;
        }

        private static void AddLookupKey(IList<string> keys, string value)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            for (var i = 0; i < keys.Count; i++)
            {
                if (string.Equals(keys[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            keys.Add(normalized);
        }
    }
}
