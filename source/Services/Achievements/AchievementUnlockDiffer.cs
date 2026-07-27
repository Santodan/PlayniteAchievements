using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;

namespace PlayniteAchievements.Services.Achievements
{
    internal sealed class AchievementUnlockDiffer
    {
        public IReadOnlyList<AchievementDetail> DiffUserUnlocks(
            GameAchievementData before,
            GameAchievementData after)
        {
            if (after?.Achievements == null || after.Achievements.Count == 0)
            {
                return Array.Empty<AchievementDetail>();
            }

            var beforeByKey = BuildUserLookup(before?.Achievements);
            var result = new List<AchievementDetail>();
            foreach (var current in after.Achievements.Where(a => a?.Unlocked == true))
            {
                var key = GetAchievementKey(current.ApiName, current.DisplayName);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                beforeByKey.TryGetValue(key, out var previous);
                if (previous == null || previous.Unlocked != true || HasStrictlyNewerUnlockTime(previous.UnlockTimeUtc, current.UnlockTimeUtc))
                {
                    result.Add(current);
                }
            }

            return result
                .OrderBy(a => NormalizeUnlockTime(a.UnlockTimeUtc) ?? DateTime.MaxValue)
                .ThenBy(a => a.Rarity)
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<FriendAchievementRow> DiffFriendSessionUnlocks(
            IEnumerable<FriendAchievementRow> rows,
            DateTime sessionStartUtc,
            ISet<string> alreadyToastedKeys)
        {
            var result = new List<FriendAchievementRow>();
            foreach (var row in rows ?? Enumerable.Empty<FriendAchievementRow>())
            {
                if (row?.Unlocked != true)
                {
                    continue;
                }

                var key = GetAchievementKey(row.ApiName, row.DisplayName);
                if (string.IsNullOrEmpty(key) || alreadyToastedKeys?.Contains(key) == true)
                {
                    continue;
                }

                var unlockTime = NormalizeUnlockTime(row.UnlockTimeUtc);
                if (!unlockTime.HasValue || unlockTime.Value < sessionStartUtc)
                {
                    continue;
                }

                result.Add(row);
                alreadyToastedKeys?.Add(key);
            }

            return result
                .OrderBy(row => NormalizeUnlockTime(row.UnlockTimeUtc) ?? DateTime.MaxValue)
                .ThenBy(row => row.Rarity ?? RarityTier.Common)
                .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<FriendAchievementRow> DiffFriendBaselineUnlocks(
            IEnumerable<FriendAchievementRow> baselineRows,
            IEnumerable<FriendAchievementRow> currentRows,
            ISet<string> alreadyToastedKeys)
        {
            var baseline = new HashSet<string>(
                (baselineRows ?? Enumerable.Empty<FriendAchievementRow>())
                .Where(row => row?.Unlocked == true)
                .Select(row => GetAchievementKey(row.ApiName, row.DisplayName))
                .Where(key => !string.IsNullOrEmpty(key)),
                StringComparer.OrdinalIgnoreCase);

            var result = new List<FriendAchievementRow>();
            foreach (var row in currentRows ?? Enumerable.Empty<FriendAchievementRow>())
            {
                if (row?.Unlocked != true)
                {
                    continue;
                }

                var key = GetAchievementKey(row.ApiName, row.DisplayName);
                if (string.IsNullOrEmpty(key) ||
                    baseline.Contains(key) ||
                    alreadyToastedKeys?.Contains(key) == true)
                {
                    continue;
                }

                result.Add(row);
                alreadyToastedKeys?.Add(key);
            }

            return result
                .OrderBy(row => row.Rarity ?? RarityTier.Common)
                .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static string GetAchievementKey(string apiName, string displayName)
        {
            var key = !string.IsNullOrWhiteSpace(apiName) ? apiName : displayName;
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        private static Dictionary<string, AchievementDetail> BuildUserLookup(IEnumerable<AchievementDetail> achievements)
        {
            var result = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in achievements ?? Enumerable.Empty<AchievementDetail>())
            {
                var key = GetAchievementKey(achievement?.ApiName, achievement?.DisplayName);
                if (!string.IsNullOrEmpty(key) && !result.ContainsKey(key))
                {
                    result[key] = achievement;
                }
            }

            return result;
        }

        private static bool HasStrictlyNewerUnlockTime(DateTime? previous, DateTime? current)
        {
            var previousUtc = NormalizeUnlockTime(previous);
            var currentUtc = NormalizeUnlockTime(current);
            return previousUtc.HasValue && currentUtc.HasValue && currentUtc.Value > previousUtc.Value;
        }

        private static DateTime? NormalizeUnlockTime(DateTime? value)
        {
            if (!value.HasValue || value.Value == DateTime.MinValue)
            {
                return null;
            }

            var utc = value.Value;
            if (utc.Kind == DateTimeKind.Local)
            {
                return utc.ToUniversalTime();
            }

            return utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                : utc;
        }
    }
}
