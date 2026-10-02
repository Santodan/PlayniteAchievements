using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Providers.Hypixel
{
    /// <summary>
    /// Turns a parsed profile page into achievement rows, enriched from the keyless definition
    /// catalog with points, completion rates and stable identifiers.
    ///
    /// The profile page drives the list: it is the only source of the player's unlock state, and
    /// a game the page omits (Wool Games renders an empty panel) would otherwise be written as
    /// all locked.
    /// </summary>
    internal static class HypixelAchievementMapper
    {
        private const string UnobtainableCategoryType = "Unobtainable";

        public static List<AchievementDetail> BuildAchievements(
            HypixelProfile profile,
            HypixelAchievementsResponse catalog)
        {
            var details = new List<AchievementDetail>();
            if (profile?.Panels == null)
            {
                return details;
            }

            var usedApiNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var panel in profile.Panels)
            {
                if (panel == null || panel.Entries.Count == 0)
                {
                    continue;
                }

                var gameKey = HypixelParsing.ResolveCatalogGameKey(panel.Slug);
                HypixelGameDefinitions definitions = null;
                catalog?.Achievements?.TryGetValue(gameKey ?? string.Empty, out definitions);
                var lookup = new GameLookup(definitions);

                foreach (var entry in panel.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                    {
                        continue;
                    }

                    var detail = entry.IsTiered
                        ? BuildTiered(entry, gameKey, lookup)
                        : BuildOneTime(entry, gameKey, lookup);

                    detail.ApiName = MakeUnique(detail.ApiName, usedApiNames);
                    detail.Category = panel.DisplayName;
                    detail.UnlockedIconPath = panel.IconUrl;

                    // There is no separate locked variant; the display layer derives the greyscale
                    // locked rendering from the same source.
                    detail.LockedIconPath = panel.IconUrl;
                    details.Add(detail);
                }
            }

            return details;
        }

        /// <summary>
        /// Stable identity for a matched one-time achievement, in the lowercase
        /// <c>&lt;game&gt;_&lt;key&gt;</c> form Hypixel's own player data uses.
        /// </summary>
        internal static string BuildApiName(string gameKey, string definitionKey)
            => (gameKey + "_" + definitionKey).ToLowerInvariant();

        /// <summary>
        /// Per-tier identity. The tier number is the site's own, so it survives threshold retunes.
        /// </summary>
        internal static string BuildTierApiName(string baseApiName, int tier)
            => baseApiName + ":t" + tier.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Every tier of a ladder shares its name and icon, so the rows are told apart by the
        /// threshold, as Guild Wars 2 ladders are.
        /// </summary>
        internal static string BuildTierDisplayName(string name, long amount)
            => $"{name} ({amount.ToString("N0", CultureInfo.CurrentCulture)})";

        private static AchievementDetail BuildOneTime(HypixelProfileEntry entry, string gameKey, GameLookup lookup)
        {
            var match = lookup.FindOneTime(entry.Name, entry.Description);
            var definition = match?.Value;
            var percent = ClampPercent(definition?.GlobalPercentUnlocked);

            var detail = new AchievementDetail
            {
                ApiName = match.HasValue
                    ? BuildApiName(gameKey, match.Value.Key)
                    : BuildFallbackApiName(gameKey, entry.Name),
                DisplayName = entry.Name,
                Description = entry.Description,
                Points = definition?.Points,
                Unlocked = entry.Completed,

                // Neither the profile page nor the API records when an achievement was earned.
                UnlockTimeUtc = null,
                CategoryType = entry.Legacy || definition?.Legacy == true ? UnobtainableCategoryType : null,
                Hidden = false,
                GlobalPercentUnlocked = percent
            };

            // Without a percent the tier stays at its default, which the cache layer reads as
            // "unknown" and declines to overwrite a stored value with.
            if (percent.HasValue)
            {
                detail.Rarity = PercentRarityHelper.GetRarityTier(percent.Value);
            }

            return detail;
        }

        private static AchievementDetail BuildTiered(HypixelProfileEntry entry, string gameKey, GameLookup lookup)
        {
            var tier = entry.Tier.GetValueOrDefault();
            var match = lookup.FindTiered(entry.Name);
            var definition = match?.Value;
            var tierDefinition = definition?.Tiers?.FirstOrDefault(t => t != null && t.Tier == tier);
            var amount = entry.Amount ?? tierDefinition?.Amount;

            var baseApiName = match.HasValue
                ? BuildApiName(gameKey, match.Value.Key)
                : BuildFallbackApiName(gameKey, entry.Name);

            var detail = new AchievementDetail
            {
                ApiName = BuildTierApiName(baseApiName, tier),
                DisplayName = amount.HasValue ? BuildTierDisplayName(entry.Name, amount.Value) : entry.Name,
                Description = HypixelParsing.FormatTieredDescription(entry.Description, amount),
                Points = tierDefinition?.Points,
                Unlocked = entry.Completed,
                UnlockTimeUtc = null,
                CategoryType = entry.Legacy || definition?.Legacy == true ? UnobtainableCategoryType : null,
                Hidden = false,

                // The catalog publishes completion rates for one-time achievements only.
                GlobalPercentUnlocked = null
            };

            if (amount.HasValue && amount.Value > 0 && entry.Progress.HasValue)
            {
                var current = Math.Max(0, Math.Min(entry.Progress.Value, amount.Value));
                detail.ProgressNum = (int)Math.Min(current, int.MaxValue);
                detail.ProgressDenom = (int)Math.Min(amount.Value, int.MaxValue);
            }

            return detail;
        }

        /// <summary>
        /// Identity for a row the catalog does not list, so it is kept rather than dropped.
        /// </summary>
        private static string BuildFallbackApiName(string gameKey, string name)
            => (gameKey ?? string.Empty) + ":" + name.Trim();

        private static string MakeUnique(string apiName, HashSet<string> used)
        {
            if (used.Add(apiName))
            {
                return apiName;
            }

            for (var suffix = 2; ; suffix++)
            {
                var candidate = apiName + "#" + suffix.ToString(CultureInfo.InvariantCulture);
                if (used.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        private static double? ClampPercent(double? percent)
        {
            if (!percent.HasValue || double.IsNaN(percent.Value) || double.IsInfinity(percent.Value))
            {
                return null;
            }

            return Math.Max(0, Math.Min(100, percent.Value));
        }

        /// <summary>
        /// Name lookups into one game's definitions. Names are not unique: SkyWars has two
        /// one-time achievements called "No Chest Challenge", told apart only by description.
        /// </summary>
        private sealed class GameLookup
        {
            private readonly Dictionary<string, List<KeyValuePair<string, HypixelOneTimeDefinition>>> _oneTimeByName =
                new Dictionary<string, List<KeyValuePair<string, HypixelOneTimeDefinition>>>(StringComparer.OrdinalIgnoreCase);

            private readonly Dictionary<string, KeyValuePair<string, HypixelTieredDefinition>> _tieredByName =
                new Dictionary<string, KeyValuePair<string, HypixelTieredDefinition>>(StringComparer.OrdinalIgnoreCase);

            public GameLookup(HypixelGameDefinitions definitions)
            {
                if (definitions?.OneTime != null)
                {
                    foreach (var pair in definitions.OneTime.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        var name = pair.Value?.Name?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;

                        if (!_oneTimeByName.TryGetValue(name, out var list))
                        {
                            list = new List<KeyValuePair<string, HypixelOneTimeDefinition>>();
                            _oneTimeByName[name] = list;
                        }

                        list.Add(pair);
                    }
                }

                if (definitions?.Tiered != null)
                {
                    foreach (var pair in definitions.Tiered.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        var name = pair.Value?.Name?.Trim();
                        if (string.IsNullOrEmpty(name) || _tieredByName.ContainsKey(name)) continue;
                        _tieredByName[name] = pair;
                    }
                }
            }

            public KeyValuePair<string, HypixelOneTimeDefinition>? FindOneTime(string name, string description)
            {
                if (!_oneTimeByName.TryGetValue(name.Trim(), out var candidates) || candidates.Count == 0)
                {
                    return null;
                }

                if (candidates.Count == 1)
                {
                    return candidates[0];
                }

                var wanted = description?.Trim();
                foreach (var candidate in candidates)
                {
                    if (string.Equals(candidate.Value.Description?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }

                // Several definitions share the name and none matches the description: no key can
                // be chosen with confidence, so the row falls back to a name-based identity.
                return null;
            }

            public KeyValuePair<string, HypixelTieredDefinition>? FindTiered(string name)
            {
                return _tieredByName.TryGetValue(name.Trim(), out var match) ? match : (KeyValuePair<string, HypixelTieredDefinition>?)null;
            }
        }
    }
}
