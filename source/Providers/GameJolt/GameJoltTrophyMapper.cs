using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Providers.GameJolt
{
    /// <summary>
    /// Pure parse/merge logic for GameJolt site-api responses, kept free of any WebView or
    /// I/O dependency so it can be unit tested against captured JSON payloads.
    /// </summary>
    internal static class GameJoltTrophyMapper
    {
        /// <summary>
        /// Reads the username from a <c>/web/profile/@{user}</c> response. Returns null when the
        /// payload has no user (unauthenticated or unknown profile).
        /// </summary>
        public static string ParseUsername(string profileJson)
        {
            return ParseProfile(profileJson)?.Payload?.User?.Username;
        }

        /// <summary>
        /// Ensures a username carries the leading '@' the site-api profile/trophy endpoints require.
        /// </summary>
        public static string FormatUser(string username)
        {
            var trimmed = (username ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return trimmed;
            }

            return trimmed.StartsWith("@", StringComparison.Ordinal) ? trimmed : "@" + trimmed;
        }

        /// <summary>
        /// Scrapes the logged-in username from the post-login page. GameJolt renders the account menu as
        /// a "-username" element containing "Hey @username". Brittle by nature; a failed scrape just means
        /// the login is not detected and the user can retry.
        /// </summary>
        public static string ExtractUsernameFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var match = Regex.Match(
                html,
                "class=\"-username\"[^>]*>(?:\\s*Hey\\s*)?@?([^<]+)<",
                RegexOptions.IgnoreCase);
            if (!match.Success || match.Groups.Count <= 1)
            {
                return null;
            }

            var username = match.Groups[1].Value
                .Replace("Hey @", string.Empty)
                .Replace("Hey", string.Empty)
                .Trim()
                .TrimStart('@')
                .Trim();

            return string.IsNullOrWhiteSpace(username) ? null : username;
        }

        public static GameJoltProfileResponse ParseProfile(string profileJson)
        {
            if (string.IsNullOrWhiteSpace(profileJson))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<GameJoltProfileResponse>(profileJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Builds the locked achievement schema from a trophy-definitions response, filtered to the
        /// requested game id. Every achievement is keyed by the trophy id (as <see cref="AchievementDetail.ApiName"/>)
        /// so the per-user unlock pass can match it against <c>game_trophy_id</c>.
        /// </summary>
        public static List<AchievementDetail> BuildDefinitions(string trophiesJson, string gameId)
        {
            var result = new List<AchievementDetail>();
            if (string.IsNullOrWhiteSpace(trophiesJson))
            {
                return result;
            }

            GameJoltTrophiesResponse response;
            try
            {
                response = JsonConvert.DeserializeObject<GameJoltTrophiesResponse>(trophiesJson);
            }
            catch (JsonException)
            {
                return result;
            }

            var trophies = response?.Payload?.Trophies;
            if (trophies == null)
            {
                return result;
            }

            foreach (var trophy in trophies)
            {
                if (trophy == null || !MatchesGame(trophy.GameId, gameId))
                {
                    continue;
                }

                var icon = NormalizeIconUrl(trophy.ImgThumbnail);
                result.Add(new AchievementDetail
                {
                    ApiName = trophy.Id.ToString(CultureInfo.InvariantCulture),
                    DisplayName = trophy.Title?.Trim() ?? string.Empty,
                    Description = trophy.Description?.Trim() ?? string.Empty,
                    UnlockedIconPath = icon,
                    LockedIconPath = icon,
                    Points = trophy.Experience,
                    TrophyType = MapDifficultyName(trophy.Difficulty),
                    Hidden = trophy.Secret,
                    GlobalPercentUnlocked = null,
                    Rarity = ResolveRarity(trophy.Difficulty),
                    Unlocked = false
                });
            }

            return result;
        }

        /// <summary>
        /// Merges the per-user unlock status from a profile-trophies response into a schema list built
        /// by <see cref="BuildDefinitions"/>. A trophy present in the response is marked unlocked; its
        /// <c>logged_on</c> (Unix epoch milliseconds) becomes the unlock time, or null when the server
        /// reports no date (still unlocked). Trophies absent from the response stay locked.
        /// </summary>
        public static void ApplyUnlocks(
            IList<AchievementDetail> achievements,
            string profileTrophiesJson,
            string gameId)
        {
            if (achievements == null || achievements.Count == 0 || string.IsNullOrWhiteSpace(profileTrophiesJson))
            {
                return;
            }

            GameJoltProfileTrophiesResponse response;
            try
            {
                response = JsonConvert.DeserializeObject<GameJoltProfileTrophiesResponse>(profileTrophiesJson);
            }
            catch (JsonException)
            {
                return;
            }

            var unlocks = response?.Payload?.Trophies;
            if (unlocks == null)
            {
                return;
            }

            var byApiName = new Dictionary<string, AchievementDetail>(StringComparer.Ordinal);
            foreach (var achievement in achievements)
            {
                if (achievement?.ApiName != null && !byApiName.ContainsKey(achievement.ApiName))
                {
                    byApiName[achievement.ApiName] = achievement;
                }
            }

            foreach (var unlock in unlocks)
            {
                if (unlock == null || !MatchesGame(unlock.GameId, gameId))
                {
                    continue;
                }

                var key = unlock.GameTrophyId.ToString(CultureInfo.InvariantCulture);
                if (!byApiName.TryGetValue(key, out var achievement))
                {
                    continue;
                }

                achievement.Unlocked = true;
                achievement.UnlockTimeUtc = EpochMillisToUtc(unlock.LoggedOn);

                var unlockedIcon = NormalizeIconUrl(unlock.GameTrophy?.ImgThumbnail);
                if (!string.IsNullOrWhiteSpace(unlockedIcon))
                {
                    achievement.UnlockedIconPath = unlockedIcon;
                }
            }
        }

        /// <summary>
        /// Reads the global completion percentage from a game-trophy-percentage response, or null when
        /// the payload has no percentage.
        /// </summary>
        public static double? ParsePercentage(string percentageJson)
        {
            if (string.IsNullOrWhiteSpace(percentageJson))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<GameJoltPercentageResponse>(percentageJson)?.Payload?.Percentage;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Applies a global unlock percentage to an achievement: stores it as
        /// <see cref="AchievementDetail.GlobalPercentUnlocked"/> and derives the rarity tier from it
        /// (same percent-based rarity used by Steam/Exophase), overriding the difficulty-based fallback.
        /// A null percentage leaves the difficulty-based rarity in place.
        /// </summary>
        public static void ApplyPercentage(AchievementDetail achievement, double? percentage)
        {
            if (achievement == null || !percentage.HasValue)
            {
                return;
            }

            var pct = percentage.Value;
            if (double.IsNaN(pct) || double.IsInfinity(pct))
            {
                return;
            }

            if (pct < 0)
            {
                pct = 0;
            }
            else if (pct > 100)
            {
                pct = 100;
            }

            achievement.GlobalPercentUnlocked = pct;
            achievement.Rarity = PercentRarityHelper.GetRarityTier(pct);
        }

        /// <summary>
        /// Converts a nullable Unix epoch in milliseconds to a UTC <see cref="DateTime"/>. Null (server
        /// reported an unlock with no timestamp) maps to null so the achievement is unlocked-without-date.
        /// </summary>
        public static DateTime? EpochMillisToUtc(long? epochMillis)
        {
            if (!epochMillis.HasValue || epochMillis.Value <= 0)
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(epochMillis.Value).UtcDateTime;
        }

        private static bool MatchesGame(long trophyGameId, string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return true;
            }

            return string.Equals(
                trophyGameId.ToString(CultureInfo.InvariantCulture),
                gameId.Trim(),
                StringComparison.Ordinal);
        }

        internal static string NormalizeIconUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var trimmed = url.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + trimmed;
            }

            return trimmed;
        }

        /// <summary>
        /// Maps GameJolt's numeric trophy difficulty (1=Bronze, 2=Silver, 3=Gold, 4=Platinum) to the
        /// plugin's rarity tiers. Unknown values fall back to Common.
        /// </summary>
        internal static RarityTier ResolveRarity(int difficulty)
        {
            switch (difficulty)
            {
                case 4:
                    return RarityTier.UltraRare;
                case 3:
                    return RarityTier.Rare;
                case 2:
                    return RarityTier.Uncommon;
                default:
                    return RarityTier.Common;
            }
        }

        /// <summary>
        /// Maps GameJolt's numeric trophy difficulty to a trophy-type label so trophy-type breakdowns
        /// render meaningfully (bronze/silver/gold/platinum).
        /// </summary>
        internal static string MapDifficultyName(int difficulty)
        {
            switch (difficulty)
            {
                case 4:
                    return "platinum";
                case 3:
                    return "gold";
                case 2:
                    return "silver";
                default:
                    return "bronze";
            }
        }
    }
}
