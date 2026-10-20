using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>What a write to a game's custom data means for the rest of the plugin.</summary>
    public readonly struct CustomDataTransitionEffects
    {
        public CustomDataTransitionEffects(bool requiresRefresh, bool forceIconRefresh)
        {
            RequiresRefresh = requiresRefresh;
            ForceIconRefresh = forceIconRefresh;
        }

        /// <summary>The game's achievements must be refetched (provider matching or icons changed).</summary>
        public bool RequiresRefresh { get; }

        /// <summary>Icon overrides changed, so cached icons must be rebuilt during that refresh.</summary>
        public bool ForceIconRefresh { get; }
    }

    /// <summary>
    /// Decides what has to happen after a game's custom data is replaced or merged: the Manage
    /// Achievements window and the Workshop installer both write custom data and both must
    /// trigger the same follow-up, so the rule lives here once.
    /// </summary>
    public static class CustomDataTransition
    {
        public static CustomDataTransitionEffects Analyze(GameCustomDataFile previousData, GameCustomDataFile currentData)
        {
            var forceIconRefresh = HaveIconOverridesChanged(previousData, currentData);
            return new CustomDataTransitionEffects(
                StoredDataRequiresRefresh(previousData) ||
                StoredDataRequiresRefresh(currentData) ||
                forceIconRefresh,
                forceIconRefresh);
        }

        /// <summary>True when the record carries settings that change which provider data is fetched.</summary>
        public static bool StoredDataRequiresRefresh(GameCustomDataFile data)
        {
            return data?.ManualLink != null ||
                   data?.ProviderOverride != null ||
                   data?.RetroAchievementsGameIdOverride.HasValue == true ||
                   !string.IsNullOrWhiteSpace(data?.XeniaTitleIdOverride) ||
                   !string.IsNullOrWhiteSpace(data?.ShadPS4MatchIdOverride) ||
                   data?.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data?.ExophaseSlugOverride) ||
                   !string.IsNullOrWhiteSpace(data?.ExophaseEnrichmentSlugOverride) ||
                   data?.MetadataLinks?.Count > 0 ||
                   data?.MetadataChoices?.IsEmpty() == false;
        }

        private static bool HaveIconOverridesChanged(GameCustomDataFile previousData, GameCustomDataFile currentData)
        {
            return !AreStringMapsEqual(
                       previousData?.AchievementUnlockedIconOverrides,
                       currentData?.AchievementUnlockedIconOverrides) ||
                   !AreStringMapsEqual(
                       previousData?.AchievementLockedIconOverrides,
                       currentData?.AchievementLockedIconOverrides);
        }

        private static bool AreStringMapsEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        {
            var normalizedLeft = NormalizeStringMap(left);
            var normalizedRight = NormalizeStringMap(right);
            if (normalizedLeft.Count != normalizedRight.Count)
            {
                return false;
            }

            foreach (var pair in normalizedLeft)
            {
                if (!normalizedRight.TryGetValue(pair.Key, out var value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<string, string> NormalizeStringMap(IReadOnlyDictionary<string, string> source)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return normalized;
            }

            foreach (var pair in source)
            {
                var key = NormalizeValue(pair.Key);
                var value = NormalizeValue(pair.Value);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                normalized[key] = value;
            }

            return normalized;
        }

        private static string NormalizeValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
