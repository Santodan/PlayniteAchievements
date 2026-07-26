using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal static class RetroAchievementsAchievementMapper
    {
        private const string RetroAchievementsBaseUrl = "https://retroachievements.org";
        private const string BadgeBaseUrl = "https://i.retroachievements.org/Badge/";

        public static List<AchievementDetail> ParseAchievements(
            RaGameInfoUserProgress gameInfo,
            string rarityStats,
            string categoryLabel = null,
            bool enableAutomaticCapstoneAssignment = false,
            string setCategoryType = null)
        {
            var list = new List<AchievementDetail>();

            if (gameInfo?.Achievements == null)
            {
                return list;
            }

            // Locked achievements derive rarity from this global setting; unlocked
            // achievements instead follow their own unlock mode.
            var useHardcoreRarity = string.Equals(rarityStats, "hardcore", StringComparison.OrdinalIgnoreCase);

            var totalPlayers = RetroAchievementsRarityCalculator.ResolveTotalPlayers(
                gameInfo.NumDistinctPlayers,
                gameInfo.NumDistinctPlayersCasual,
                gameInfo.NumDistinctPlayersHardcore);

            foreach (var kvp in gameInfo.Achievements)
            {
                var achId = kvp.Key;
                var ach = kvp.Value;
                if (ach == null) continue;

                var title = ach.Title ?? achId;
                var desc = ach.Description ?? string.Empty;
                var badge = ach.BadgeName ?? string.Empty;

                // A hardcore unlock also records DateEarned, so hardcore takes precedence.
                var earnedInHardcore = !string.IsNullOrWhiteSpace(ach.DateEarnedHardcore);
                var earnedSoftcore = !earnedInHardcore && !string.IsNullOrWhiteSpace(ach.DateEarned);

                DateTime? unlockUtc = null;
                if (earnedInHardcore)
                {
                    unlockUtc = ParseRaUtcTimestamp(ach.DateEarnedHardcore);
                }
                else if (earnedSoftcore)
                {
                    unlockUtc = ParseRaUtcTimestamp(ach.DateEarned);
                }

                // Rarity matches the unlock mode: a hardcore unlock stores hardcore
                // rarity, a softcore-only unlock stores casual rarity. Locked
                // achievements fall back to the global RaRarityStats setting.
                var globalPercent = RetroAchievementsRarityCalculator.ComputePercent(
                    ach.NumAwarded,
                    ach.NumAwardedHardcore,
                    totalPlayers,
                    earnedInHardcore,
                    earnedSoftcore,
                    useHardcoreRarity);

                if (globalPercent.HasValue)
                {
                    globalPercent = Math.Max(0, Math.Min(100, globalPercent.Value));
                }

                var unlockedIcon = BuildBadgeUrl(badge, locked: false);
                var lockedIcon = BuildBadgeUrl(badge, locked: true);

                // Unlocked achievements are classified by the mode they were earned in;
                // locked achievements keep the default (null) mode. The set-membership type
                // (the base set -> "Base", a subset -> "Subset") is combined with the unlock
                // mode in canonical order (e.g. "Base|Hardcore", "Subset|Softcore").
                var unlockModeType = earnedInHardcore ? "Hardcore" : earnedSoftcore ? "Softcore" : null;
                var categoryType = string.IsNullOrWhiteSpace(setCategoryType)
                    ? unlockModeType
                    : AchievementCategoryTypeHelper.Combine(new[] { setCategoryType, unlockModeType });

                var detail = new AchievementDetail
                {
                    ApiName = achId,
                    DisplayName = title,
                    Description = desc,
                    UnlockedIconPath = unlockedIcon,
                    LockedIconPath = lockedIcon,
                    Points = ach.Points,
                    ScaledPoints = ach.TrueRatio,
                    Category = categoryLabel,
                    IsCapstone = enableAutomaticCapstoneAssignment &&
                                 string.Equals(ach.Type, "win_condition", StringComparison.OrdinalIgnoreCase),
                    CategoryType = categoryType,
                    UnlockTimeUtc = unlockUtc,
                    Hidden = false,
                    Rarity = globalPercent.HasValue
                        ? PercentRarityHelper.GetRarityTier(globalPercent.Value)
                        : RarityTier.Common,
                    GlobalPercentUnlocked = NormalizePercent(globalPercent)
                };

                list.Add(detail);
            }

            return list;
        }

        public static List<FriendAchievementRow> ToFriendRows(IEnumerable<AchievementDetail> achievements)
        {
            return (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(achievement => achievement != null)
                .Select(achievement =>
                {
                    var unlocked = achievement.UnlockTimeUtc.HasValue || achievement.Unlocked;
                    return new FriendAchievementRow
                    {
                        ApiName = achievement.ApiName,
                        DisplayName = achievement.DisplayName,
                        Description = achievement.Description,
                        IconUrl = unlocked ? achievement.UnlockedIconPath : achievement.LockedIconPath,
                        UnlockedIconUrl = achievement.UnlockedIconPath,
                        LockedIconUrl = achievement.LockedIconPath,
                        Points = achievement.Points,
                        ScaledPoints = achievement.ScaledPoints,
                        Category = achievement.Category,
                        CategoryType = achievement.CategoryType,
                        TrophyType = achievement.TrophyType,
                        Hidden = achievement.Hidden,
                        IsCapstone = achievement.IsCapstone,
                        GlobalPercentUnlocked = achievement.GlobalPercentUnlocked,
                        Rarity = achievement.Rarity,
                        Unlocked = unlocked,
                        UnlockTimeUtc = achievement.UnlockTimeUtc,
                        ProgressNum = achievement.ProgressNum,
                        ProgressDenom = achievement.ProgressDenom
                    };
                })
                .ToList();
        }

        public static string ExtractCategoryLabel(string subsetTitle)
        {
            if (string.IsNullOrWhiteSpace(subsetTitle))
                return null;

            // Try "[Subset - Label]" pattern first.
            var subsetStart = subsetTitle.IndexOf("[Subset - ", StringComparison.OrdinalIgnoreCase);
            if (subsetStart >= 0)
            {
                var labelStart = subsetStart + "[Subset - ".Length;
                var labelEnd = subsetTitle.IndexOf(']', labelStart);
                if (labelEnd > labelStart)
                {
                    return subsetTitle.Substring(labelStart, labelEnd - labelStart).Trim();
                }
            }

            // Try "[Bonus]", "[Hub]", etc. - single-word bracket label.
            var bracketStart = subsetTitle.IndexOf('[');
            if (bracketStart >= 0)
            {
                var bracketEnd = subsetTitle.IndexOf(']', bracketStart + 1);
                if (bracketEnd > bracketStart + 1)
                {
                    return subsetTitle.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim();
                }
            }

            // Parenthesized pattern: "(Subset - Label)".
            var parenStart = subsetTitle.IndexOf("(Subset - ", StringComparison.OrdinalIgnoreCase);
            if (parenStart >= 0)
            {
                var labelStart = parenStart + "(Subset - ".Length;
                var labelEnd = subsetTitle.IndexOf(')', labelStart);
                if (labelEnd > labelStart)
                {
                    return subsetTitle.Substring(labelStart, labelEnd - labelStart).Trim();
                }
            }

            var plainSubsetStart = subsetTitle.IndexOf(" Subset ", StringComparison.OrdinalIgnoreCase);
            if (plainSubsetStart > 0)
            {
                var label = subsetTitle.Substring(plainSubsetStart + " Subset ".Length).Trim();
                return string.IsNullOrWhiteSpace(label) ? null : label;
            }

            return null;
        }

        public static DateTime? ParseRaUtcTimestamp(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;

            var trimmed = s.Trim();
            if (DateTime.TryParseExact(
                trimmed,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
            {
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }

            if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
            {
                return dto.UtcDateTime;
            }

            return null;
        }

        public static string NormalizeImageUrl(string pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
            {
                return null;
            }

            var value = pathOrUrl.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                return absolute.ToString();
            }

            if (!value.StartsWith("/", StringComparison.Ordinal))
            {
                value = "/" + value;
            }

            return RetroAchievementsBaseUrl + value;
        }

        public static string BuildAvatarUrl(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            return NormalizeImageUrl("/UserPic/" + Uri.EscapeDataString(username.Trim()) + ".png");
        }

        private static string BuildBadgeUrl(string badge, bool locked)
        {
            if (string.IsNullOrWhiteSpace(badge))
            {
                return null;
            }

            return BadgeBaseUrl + badge.Trim() + (locked ? "_lock" : string.Empty) + ".png";
        }

        private static double? NormalizePercent(double? rawPercent)
        {
            if (!rawPercent.HasValue)
            {
                return null;
            }

            var value = rawPercent.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }
    }
}
