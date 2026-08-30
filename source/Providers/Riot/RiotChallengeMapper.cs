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
            var results = new List<AchievementDetail>();
            if (metadata?.Challenges == null || metadata.Challenges.Count == 0)
            {
                return results;
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

                results.Add(BuildAchievement(
                    challengeId,
                    challenge,
                    playerInfo,
                    challengePercentiles,
                    metadata.Challenges,
                    categoryDisplayNames,
                    nowUtc));
            }

            // Group by category so the default (provider) order reads the way the League client
            // presents challenges, with a stable tiebreak on name.
            return results
                .OrderBy(a => a.Category ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.DisplayName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static AchievementDetail BuildAchievement(
            long challengeId,
            CDragonChallenge challenge,
            RiotChallengeInfoDto playerInfo,
            IReadOnlyDictionary<string, double> challengePercentiles,
            IReadOnlyDictionary<string, CDragonChallenge> allChallenges,
            IReadOnlyDictionary<string, string> categoryDisplayNames,
            DateTime nowUtc)
        {
            var level = RiotChallengeLevels.Normalize(playerInfo?.Level);
            var unlocked = RiotChallengeLevels.IsUnlocked(level);
            var iconUrl = ResolveIconUrl(challenge, level);
            var percent = ResolveGlobalPercent(playerInfo, challengePercentiles, unlocked);

            var achievement = new AchievementDetail
            {
                ApiName = challengeId.ToString(CultureInfo.InvariantCulture),
                DisplayName = challenge.Name,
                Description = FirstNonBlank(challenge.Description, challenge.DescriptionShort),
                UnlockedIconPath = iconUrl,

                // Challenge token art has no separate locked variant; the display layer derives the
                // greyscale locked rendering from the same source.
                LockedIconPath = iconUrl,

                Unlocked = unlocked,
                UnlockTimeUtc = unlocked ? ToUtc(playerInfo?.AchievedTime) : null,
                TrophyType = RiotChallengeLevels.ToTrophyType(level),
                Category = ResolveCategory(challenge, allChallenges, categoryDisplayNames),
                CategoryType = IsRetired(challenge, nowUtc) ? MissableCategoryType : null,
                GlobalPercentUnlocked = percent
            };

            if (percent.HasValue)
            {
                achievement.Rarity = PercentRarityHelper.GetRarityTier(percent.Value);
            }

            ApplyProgress(achievement, challenge, playerInfo, level);
            return achievement;
        }

        /// <summary>
        /// Fills <c>ProgressNum</c>/<c>ProgressDenom</c> from the player's raw value and the next
        /// unreached tier threshold. Skipped for reverse-direction challenges (lower is better),
        /// where a rising progress bar would read backwards.
        /// </summary>
        private static void ApplyProgress(
            AchievementDetail achievement,
            CDragonChallenge challenge,
            RiotChallengeInfoDto playerInfo,
            string level)
        {
            if (challenge.ReverseDirection || challenge.Thresholds == null || challenge.Thresholds.Count == 0)
            {
                return;
            }

            var ladder = challenge.Thresholds
                .Where(pair => RiotChallengeLevels.GetRank(pair.Key) > 0 && pair.Value != null)
                .Select(pair => new { Rank = RiotChallengeLevels.GetRank(pair.Key), pair.Value.Value })
                .OrderBy(item => item.Rank)
                .ToList();

            if (ladder.Count == 0)
            {
                return;
            }

            var currentRank = RiotChallengeLevels.GetRank(level);
            var value = playerInfo?.Value ?? 0d;

            var next = ladder.FirstOrDefault(item => item.Rank > currentRank);
            var target = next?.Value ?? ladder[ladder.Count - 1].Value;

            if (target <= 0)
            {
                return;
            }

            // The value can legitimately pass the next threshold without the tier being awarded:
            // 181 of 400 challenges gate Grandmaster and Challenger on a top-N leaderboard place
            // rather than on a number. Pinning the bar full says "the number is met" without
            // rendering past 100%.
            achievement.ProgressNum = ToProgressInt(Math.Min(value, target));
            achievement.ProgressDenom = ToProgressInt(target);
        }

        /// <summary>
        /// Riot reports percentiles as a 0..1 fraction of the player base at or above a tier, which
        /// is the same "share of players who have it" the rarity model expects once scaled to 0-100.
        ///
        /// Riot writes a literal 0.0 where it has no figure, which is not the same as "no players":
        /// live data shows tables such as IRON=0 with MASTER=0.029, and nobody can hold Master
        /// without holding Iron. Zeroes are therefore treated as absent, not as ultra-rare.
        /// </summary>
        private static double? ResolveGlobalPercent(
            RiotChallengeInfoDto playerInfo,
            IReadOnlyDictionary<string, double> challengePercentiles,
            bool unlocked)
        {
            // Riot computes the player's own percentile for the tier they hold, so it beats the
            // shared table whenever it carries a figure.
            if (unlocked && IsRealPercentile(playerInfo?.Percentile))
            {
                return ScalePercentile(playerInfo.Percentile.Value);
            }

            // An unlocked challenge is measured at the tier the player holds; a locked one at the
            // first tier, which is the share of players who have it at all.
            var targetRank = unlocked
                ? RiotChallengeLevels.GetRank(playerInfo?.Level)
                : RiotChallengeLevels.GetRank(RiotChallengeLevels.Iron);

            var nearest = FindNearestPercentile(challengePercentiles, Math.Max(targetRank, 1));

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
