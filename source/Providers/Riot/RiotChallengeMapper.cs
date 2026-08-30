using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Pure mapping from CommunityDragon challenge metadata plus a player's challenge state to
    /// <see cref="AchievementDetail"/>. Free of HTTP, WPF and Playnite dependencies so it can be
    /// unit tested against captured payloads.
    /// </summary>
    internal static class RiotChallengeMapper
    {
        /// <summary>
        /// CommunityDragon serves game assets from the default locale regardless of the locale the
        /// metadata itself was fetched in; art is not localized.
        /// </summary>
        private const string AssetRoot = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/";

        private const string AssetPathPrefix = "/lol-game-data/assets/";

        /// <summary>Category type applied to challenges that can no longer be progressed.</summary>
        private const string MissableCategoryType = "Missable";

        private const string IsCategoryTag = "isCategory";
        private const string ParentTag = "parent";

        public static CDragonChallengeFile ParseMetadata(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<CDragonChallengeFile>(json);
        }

        public static RiotPlayerInfoDto ParsePlayerData(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<RiotPlayerInfoDto>(json);
        }

        /// <summary>
        /// Parses the <c>challenges/percentiles</c> payload: challenge id to level name to percentile.
        /// </summary>
        public static IReadOnlyDictionary<long, IReadOnlyDictionary<string, double>> ParsePercentiles(string json)
        {
            var empty = new Dictionary<long, IReadOnlyDictionary<string, double>>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return empty;
            }

            var raw = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, double>>>(json);
            if (raw == null)
            {
                return empty;
            }

            var result = new Dictionary<long, IReadOnlyDictionary<string, double>>();
            foreach (var pair in raw)
            {
                if (!TryParseChallengeId(pair.Key, out var id) || pair.Value == null)
                {
                    continue;
                }

                result[id] = new Dictionary<string, double>(pair.Value, StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        /// <summary>
        /// Builds the achievement list. <paramref name="categoryDisplayNames"/> maps the five
        /// top-level category ids to localized labels, since CommunityDragon exposes them only as
        /// raw codes (IMAGINATION, EXPERTISE, ...).
        /// </summary>
        /// <param name="nowUtc">Reference time for deciding whether a challenge has retired.</param>
        public static List<AchievementDetail> BuildAchievements(
            CDragonChallengeFile metadata,
            RiotPlayerChallengeState playerState,
            IReadOnlyDictionary<string, string> categoryDisplayNames,
            DateTime nowUtc)
        {
            var results = new List<TierEntry>();
            if (metadata?.Challenges == null || metadata.Challenges.Count == 0)
            {
                return new List<AchievementDetail>();
            }

            var playerByChallenge = BuildPlayerIndex(playerState);
            var percentiles = playerState?.LevelPercentiles
                ?? new Dictionary<long, IReadOnlyDictionary<string, double>>();

            foreach (var entry in metadata.Challenges)
            {
                if (!TryParseChallengeId(entry.Key, out var challengeId) || entry.Value == null)
                {
                    continue;
                }

                var challenge = entry.Value;

                // Ids 0-5 are the crystal root and the five category point meters. They carry no
                // token art and are progress totals rather than objectives, so they are not
                // achievements.
                if (IsCategoryNode(challenge))
                {
                    continue;
                }

                playerByChallenge.TryGetValue(challengeId, out var playerInfo);
                percentiles.TryGetValue(challengeId, out var challengePercentiles);

                results.AddRange(BuildTierAchievements(
                    challengeId,
                    challenge,
                    playerInfo,
                    challengePercentiles,
                    metadata.Challenges,
                    categoryDisplayNames,
                    nowUtc));
            }

            // Group by category so the default (provider) order reads the way the League client
            // presents challenges, then keep a challenge's tiers together and in ladder order.
            return results
                .OrderBy(entry => entry.Achievement.Category ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.ChallengeSortName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.TierRank)
                .Select(entry => entry.Achievement)
                .ToList();
        }

        /// <summary>
        /// Carries the sort keys alongside the achievement so ordering does not have to re-parse
        /// the composed display name.
        /// </summary>
        private sealed class TierEntry
        {
            public AchievementDetail Achievement { get; set; }

            public string ChallengeSortName { get; set; }

            public int TierRank { get; set; }
        }

        /// <summary>
        /// One achievement per tier of a challenge. A challenge is earned again at each successive
        /// tier, so a tier is the only thing here that is actually all-or-nothing, and modelling it
        /// that way lets a tier climb be an ordinary locked-to-unlocked transition.
        /// </summary>
        private static IEnumerable<TierEntry> BuildTierAchievements(
            long challengeId,
            CDragonChallenge challenge,
            RiotChallengeInfoDto playerInfo,
            IReadOnlyDictionary<string, double> challengePercentiles,
            IReadOnlyDictionary<string, CDragonChallenge> allChallenges,
            IReadOnlyDictionary<string, string> categoryDisplayNames,
            DateTime nowUtc)
        {
            var ladder = BuildThresholdLadder(challenge);
            if (ladder.Count == 0)
            {
                yield break;
            }

            var playerRank = RiotChallengeLevels.GetRank(playerInfo?.Level);
            var value = playerInfo?.Value ?? 0d;
            var category = ResolveCategory(challenge, allChallenges, categoryDisplayNames);
            var categoryType = IsRetired(challenge, nowUtc) ? MissableCategoryType : null;
            var description = FirstNonBlank(challenge.Description, challenge.DescriptionShort);

            foreach (var tier in ladder)
            {
                var tierName = RiotChallengeLevels.Ascending[tier.Rank];
                var unlocked = playerRank >= tier.Rank;
                var iconUrl = ResolveIconUrl(challenge, tierName);
                var percent = ResolveTierPercent(challengePercentiles, tier.Rank);

                var achievement = new AchievementDetail
                {
                    ApiName = BuildTierApiName(challengeId, tierName),

                    // Every tier of a challenge shares its name; the tier's own token art is what
                    // tells the rows apart, so no tier label is composed into the name.
                    DisplayName = challenge.Name,
                    Description = description,
                    UnlockedIconPath = iconUrl,

                    // Challenge token art has no separate locked variant; the display layer derives
                    // the greyscale locked rendering from the same source.
                    LockedIconPath = iconUrl,

                    Unlocked = unlocked,

                    // Riot timestamps only the tier the player currently holds. Lower tiers are
                    // known to be earned but not when, and an invented timestamp would be a lie the
                    // unlock feed would then order by.
                    UnlockTimeUtc = tier.Rank == playerRank ? ToUtc(playerInfo?.AchievedTime) : null,

                    Category = category,
                    CategoryType = categoryType,
                    GlobalPercentUnlocked = percent
                };

                if (percent.HasValue)
                {
                    achievement.Rarity = PercentRarityHelper.GetRarityTier(percent.Value);
                }

                ApplyTierProgress(achievement, challenge, value, tier.Value);

                yield return new TierEntry
                {
                    Achievement = achievement,
                    ChallengeSortName = challenge.Name ?? string.Empty,
                    TierRank = tier.Rank
                };
            }
        }

        /// <summary>
        /// Stable per-tier identity. It keys the icon cache, overrides, goals and notes, so it must
        /// never shift with a rename or a locale change.
        /// </summary>
        internal static string BuildTierApiName(long challengeId, string tierName)
            => challengeId.ToString(CultureInfo.InvariantCulture) + ":" + RiotChallengeLevels.Normalize(tierName);

        /// <summary>
        /// Progress toward this tier's own threshold. An earned tier reads full; the tiers above it
        /// show how far the same running value has come, which is what a locked achievement's
        /// progress bar means everywhere else.
        /// </summary>
        private static void ApplyTierProgress(
            AchievementDetail achievement,
            CDragonChallenge challenge,
            double value,
            double threshold)
        {
            // A reverse-direction challenge counts down, so a rising bar would read backwards.
            if (challenge.ReverseDirection || threshold <= 0)
            {
                return;
            }

            achievement.ProgressNum = ToProgressInt(Math.Min(value, threshold));
            achievement.ProgressDenom = ToProgressInt(threshold);
        }

        private sealed class ThresholdTier
        {
            public int Rank { get; set; }

            public double Value { get; set; }
        }

        private static List<ThresholdTier> BuildThresholdLadder(CDragonChallenge challenge)
        {
            if (challenge.Thresholds == null || challenge.Thresholds.Count == 0)
            {
                return new List<ThresholdTier>();
            }

            return challenge.Thresholds
                .Where(pair => RiotChallengeLevels.GetRank(pair.Key) > 0 && pair.Value != null)
                .Select(pair => new ThresholdTier
                {
                    Rank = RiotChallengeLevels.GetRank(pair.Key),
                    Value = pair.Value.Value
                })
                .OrderBy(tier => tier.Rank)
                .ToList();
        }

        /// <summary>
        /// Riot reports percentiles as a 0..1 fraction of the player base at or above a tier, which
        /// is the same "share of players who have it" the rarity model expects once scaled to 0-100.
        ///
        /// Riot writes a literal 0.0 where it has no figure, which is not the same as "no players":
        /// live data shows tables such as IRON=0 with MASTER=0.029, and nobody can hold Master
        /// without holding Iron. Zeroes are therefore treated as absent, not as ultra-rare.
        /// </summary>
        private static double? ResolveTierPercent(
            IReadOnlyDictionary<string, double> challengePercentiles,
            int tierRank)
        {
            // Per-tier achievements read the table at their own tier, which is exactly the share of
            // players holding that tier - no estimating from the player's own figure needed.
            var nearest = FindNearestPercentile(challengePercentiles, Math.Max(tierRank, 1));

            // Nothing usable in the table leaves rarity unset rather than guessed; the display layer
            // treats an absent percentage as the common default.
            return nearest.HasValue ? ScalePercentile(nearest.Value) : (double?)null;
        }

        /// <summary>
        /// Finds the closest populated tier to <paramref name="targetRank"/>, searching downward
        /// first. Percentiles descend as tiers rise, so a lower tier bounds the target from above —
        /// erring toward "more common" rather than inflating rarity on a gap in Riot's data.
        /// </summary>
        private static double? FindNearestPercentile(
            IReadOnlyDictionary<string, double> challengePercentiles,
            int targetRank)
        {
            if (challengePercentiles == null || challengePercentiles.Count == 0)
            {
                return null;
            }

            for (var rank = targetRank; rank >= 1; rank--)
            {
                if (TryGetRealPercentile(challengePercentiles, rank, out var below))
                {
                    return below;
                }
            }

            for (var rank = targetRank + 1; rank < RiotChallengeLevels.Ascending.Length; rank++)
            {
                if (TryGetRealPercentile(challengePercentiles, rank, out var above))
                {
                    return above;
                }
            }

            return null;
        }

        private static bool TryGetRealPercentile(
            IReadOnlyDictionary<string, double> challengePercentiles,
            int rank,
            out double percentile)
        {
            percentile = 0d;

            if (!challengePercentiles.TryGetValue(RiotChallengeLevels.Ascending[rank], out var value) ||
                !IsRealPercentile(value))
            {
                return false;
            }

            percentile = value;
            return true;
        }

        private static bool IsRealPercentile(double? percentile)
            => percentile.HasValue && percentile.Value > 0d && !double.IsNaN(percentile.Value);

        private static double ScalePercentile(double percentile)
        {
            var scaled = percentile * 100d;
            if (scaled < 0d) return 0d;
            if (scaled > 100d) return 100d;
            return scaled;
        }

        /// <summary>
        /// Resolves the token art for the player's current tier, falling back to the lowest tier art
        /// available so locked challenges still render their own icon rather than a generic one.
        /// </summary>
        private static string ResolveIconUrl(CDragonChallenge challenge, string level)
        {
            if (challenge.LevelToIconPath == null || challenge.LevelToIconPath.Count == 0)
            {
                return null;
            }

            var byLevel = new Dictionary<string, string>(challenge.LevelToIconPath, StringComparer.OrdinalIgnoreCase);

            if (RiotChallengeLevels.IsUnlocked(level) &&
                byLevel.TryGetValue(level, out var exact) &&
                !string.IsNullOrWhiteSpace(exact))
            {
                return BuildAssetUrl(exact);
            }

            for (var rank = 1; rank < RiotChallengeLevels.Ascending.Length; rank++)
            {
                if (byLevel.TryGetValue(RiotChallengeLevels.Ascending[rank], out var path) &&
                    !string.IsNullOrWhiteSpace(path))
                {
                    return BuildAssetUrl(path);
                }
            }

            return null;
        }

        /// <summary>
        /// Applies CommunityDragon's documented rule: <c>/lol-game-data/assets/&lt;path&gt;</c> maps to
        /// <c>plugins/rcp-be-lol-game-data/global/default/&lt;lowercased path&gt;</c>.
        /// </summary>
        public static string BuildAssetUrl(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return null;
            }

            var trimmed = assetPath.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            var relative = trimmed.StartsWith(AssetPathPrefix, StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(AssetPathPrefix.Length)
                : trimmed.TrimStart('/');

            return AssetRoot + relative.ToLowerInvariant();
        }

        /// <summary>
        /// The owning group's display name: the parent capstone for a leaf challenge, or the
        /// localized top-level category for a capstone. Null when the challenge has no parent —
        /// providers must not invent a sentinel label.
        /// </summary>
        private static string ResolveCategory(
            CDragonChallenge challenge,
            IReadOnlyDictionary<string, CDragonChallenge> allChallenges,
            IReadOnlyDictionary<string, string> categoryDisplayNames)
        {
            var parentId = GetTag(challenge, ParentTag);
            if (string.IsNullOrWhiteSpace(parentId))
            {
                return null;
            }

            if (categoryDisplayNames != null &&
                categoryDisplayNames.TryGetValue(parentId, out var localized) &&
                !string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }

            if (allChallenges != null &&
                allChallenges.TryGetValue(parentId, out var parent) &&
                !string.IsNullOrWhiteSpace(parent?.Name))
            {
                return parent.Name;
            }

            return null;
        }

        private static bool IsRetired(CDragonChallenge challenge, DateTime nowUtc)
        {
            if (challenge.EndTimestamp <= 0)
            {
                return false;
            }

            var end = ToUtc(challenge.EndTimestamp);
            return end.HasValue && end.Value <= nowUtc;
        }

        private static bool IsCategoryNode(CDragonChallenge challenge)
        {
            var value = GetTag(challenge, IsCategoryTag);
            return !string.IsNullOrWhiteSpace(value) &&
                   !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetTag(CDragonChallenge challenge, string key)
        {
            if (challenge?.Tags == null)
            {
                return null;
            }

            return challenge.Tags.TryGetValue(key, out var value) ? value : null;
        }

        private static Dictionary<long, RiotChallengeInfoDto> BuildPlayerIndex(RiotPlayerChallengeState playerState)
        {
            var index = new Dictionary<long, RiotChallengeInfoDto>();
            if (playerState?.Challenges == null)
            {
                return index;
            }

            foreach (var info in playerState.Challenges)
            {
                if (info == null)
                {
                    continue;
                }

                // Riot has been observed to repeat a challenge id; the higher tier is authoritative.
                if (index.TryGetValue(info.ChallengeId, out var existing) &&
                    RiotChallengeLevels.GetRank(existing.Level) >= RiotChallengeLevels.GetRank(info.Level))
                {
                    continue;
                }

                index[info.ChallengeId] = info;
            }

            return index;
        }

        private static bool TryParseChallengeId(string raw, out long id)
            => long.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

        private static DateTime? ToUtc(long? epochMilliseconds)
        {
            if (!epochMilliseconds.HasValue || epochMilliseconds.Value <= 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds.Value).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static int ToProgressInt(double value)
        {
            if (double.IsNaN(value) || value <= 0d)
            {
                return 0;
            }

            if (value >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static string FirstNonBlank(params string[] candidates)
            => candidates?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
