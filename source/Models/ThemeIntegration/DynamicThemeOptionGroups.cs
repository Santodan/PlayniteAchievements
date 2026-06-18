using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal static class DynamicThemeOptionGroups
    {
        public const string AllProviderKey = DynamicThemeViewKeys.All;
        public const string AchievementStatusGroup = "Status";
        public const string AchievementProgressGroup = "Progress";
        public const string AchievementVisibilityGroup = "Visibility";
        public const string AchievementNotesGroup = "Notes";
        public const string AchievementSpecialGroup = "Special";
        public const string AchievementRarityGroup = "Rarity";
        public const string AchievementTrophyGroup = "Trophy";
        public const string GameCompletionGroup = "Completion";
        public const string GameStartedGroup = "Started";
        public const string GameActivityGroup = "Activity";
        public const string GameLastUnlockGroup = "LastUnlock";

        public static readonly string[] AchievementCustomizationGroups =
        {
            AchievementVisibilityGroup,
            AchievementNotesGroup,
            AchievementSpecialGroup
        };

        public static readonly string[] GameProgressGroups =
        {
            GameCompletionGroup,
            GameStartedGroup
        };

        public static readonly string[] GameActivityGroups =
        {
            GameActivityGroup,
            GameLastUnlockGroup
        };

        public static readonly string[] AchievementStatusFilterKeys =
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.Unlocked,
            DynamicThemeViewKeys.Locked
        };

        public static readonly string[] AchievementProgressFilterKeys =
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.InProgress,
            DynamicThemeViewKeys.NoProgress
        };

        public static readonly string[] AchievementRarityFilterKeys =
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.Common,
            DynamicThemeViewKeys.Uncommon,
            DynamicThemeViewKeys.Rare,
            DynamicThemeViewKeys.UltraRare
        };

        public static readonly string[] AchievementTrophyFilterKeys =
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.Platinum,
            DynamicThemeViewKeys.Gold,
            DynamicThemeViewKeys.Silver,
            DynamicThemeViewKeys.Bronze
        };

        public static readonly string[] AchievementCustomizationFilterKeys =
        {
            DynamicThemeViewKeys.All,
            // DynamicThemeViewKeys.Visible,
            // DynamicThemeViewKeys.Hidden,
            DynamicThemeViewKeys.HasNotes,
            // DynamicThemeViewKeys.NoNotes,
            DynamicThemeViewKeys.Capstone
        };

        public static readonly string[] GameProgressFilterKeys =
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.Completed,
            DynamicThemeViewKeys.Incomplete,
            // DynamicThemeViewKeys.Started,
            DynamicThemeViewKeys.NotStarted
        };

        public static readonly string[] GameActivityFilterKeys =
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.Played,
            DynamicThemeViewKeys.Unplayed,
            // DynamicThemeViewKeys.HasLastUnlock,
            // DynamicThemeViewKeys.NoLastUnlock
        };

        public static readonly string[] AchievementFilterKeys = Merge(
            AchievementStatusFilterKeys,
            AchievementProgressFilterKeys,
            AchievementCustomizationFilterKeys,
            AchievementRarityFilterKeys,
            AchievementTrophyFilterKeys);

        public static readonly string[] GameSummaryFilterKeys = Merge(
            GameProgressFilterKeys,
            GameActivityFilterKeys);

        public static readonly string[] SelectedGameAchievementSortKeys =
        {
            DynamicThemeViewKeys.Default,
            DynamicThemeViewKeys.Name,
            DynamicThemeViewKeys.UnlockTime,
            DynamicThemeViewKeys.Rarity,
            DynamicThemeViewKeys.Status,
            DynamicThemeViewKeys.Progress,
            DynamicThemeViewKeys.Points,
            DynamicThemeViewKeys.CollectionScore,
            DynamicThemeViewKeys.PrestigeScore,
            DynamicThemeViewKeys.TrophyType,
            DynamicThemeViewKeys.CategoryType,
            DynamicThemeViewKeys.CategoryLabel,
            DynamicThemeViewKeys.Notes
        };

        public static readonly string[] LibraryAchievementSortKeys =
        {
            DynamicThemeViewKeys.Name,
            DynamicThemeViewKeys.Game,
            DynamicThemeViewKeys.Provider,
            DynamicThemeViewKeys.UnlockTime,
            DynamicThemeViewKeys.Rarity,
            DynamicThemeViewKeys.Status,
            DynamicThemeViewKeys.Progress,
            DynamicThemeViewKeys.Points,
            DynamicThemeViewKeys.CollectionScore,
            DynamicThemeViewKeys.PrestigeScore,
            DynamicThemeViewKeys.TrophyType,
            DynamicThemeViewKeys.CategoryType,
            DynamicThemeViewKeys.CategoryLabel,
            DynamicThemeViewKeys.Notes
        };

        public static readonly string[] GameSummarySortKeys =
        {
            DynamicThemeViewKeys.Name,
            DynamicThemeViewKeys.Provider,
            DynamicThemeViewKeys.Progress,
            DynamicThemeViewKeys.LastUnlock,
            DynamicThemeViewKeys.LastPlayed,
            DynamicThemeViewKeys.UnlockedCount,
            DynamicThemeViewKeys.AchievementCount
        };

        public static readonly string[] SortDirectionKeys =
        {
            DynamicThemeViewKeys.Descending,
            DynamicThemeViewKeys.Ascending
        };

        public static readonly string[] KnownProviderKeys =
        {
            "Steam",
            "Epic",
            "GOG",
            "BattleNet",
            "EA",
            "Ubisoft",
            "PSN",
            "Xbox",
            "GooglePlay",
            "Apple",
            "RetroAchievements",
            "RPCS3",
            "ShadPS4",
            "Xenia",
            "Manual",
            "Exophase",
            "Hoyoverse"
        };

        public static readonly IReadOnlyDictionary<string, string> AchievementFilterKeyMap =
            CreateCanonicalKeyMap(AchievementFilterKeys);

        public static readonly IReadOnlyDictionary<string, string> GameSummaryFilterKeyMap =
            CreateCanonicalKeyMap(GameSummaryFilterKeys);

        public static readonly IReadOnlyDictionary<string, string> SelectedGameAchievementSortKeyMap =
            CreateCanonicalKeyMap(SelectedGameAchievementSortKeys);

        public static readonly IReadOnlyDictionary<string, string> LibraryAchievementSortKeyMap =
            CreateCanonicalKeyMap(LibraryAchievementSortKeys);

        public static readonly IReadOnlyDictionary<string, string> GameSummarySortKeyMap =
            CreateCanonicalKeyMap(GameSummarySortKeys);

        public static readonly IReadOnlyDictionary<string, string> SortDirectionKeyMap =
            CreateCanonicalKeyMap(SortDirectionKeys);

        public static readonly IReadOnlyDictionary<string, string> AchievementFilterGroupMap =
            CreateGroupMap(
                Group(AchievementStatusGroup, DynamicThemeViewKeys.Unlocked, DynamicThemeViewKeys.Locked),
                Group(AchievementProgressGroup, DynamicThemeViewKeys.InProgress, DynamicThemeViewKeys.NoProgress),
                Group(AchievementVisibilityGroup, DynamicThemeViewKeys.Visible, DynamicThemeViewKeys.Hidden),
                Group(AchievementNotesGroup, DynamicThemeViewKeys.HasNotes, DynamicThemeViewKeys.NoNotes),
                Group(AchievementSpecialGroup, DynamicThemeViewKeys.Capstone),
                Group(AchievementRarityGroup, DynamicThemeViewKeys.Common, DynamicThemeViewKeys.Uncommon, DynamicThemeViewKeys.Rare, DynamicThemeViewKeys.UltraRare),
                Group(AchievementTrophyGroup, DynamicThemeViewKeys.Platinum, DynamicThemeViewKeys.Gold, DynamicThemeViewKeys.Silver, DynamicThemeViewKeys.Bronze));

        public static readonly IReadOnlyDictionary<string, string> GameSummaryFilterGroupMap =
            CreateGroupMap(
                Group(GameCompletionGroup, DynamicThemeViewKeys.Completed, DynamicThemeViewKeys.Incomplete),
                Group(GameStartedGroup, DynamicThemeViewKeys.Started, DynamicThemeViewKeys.NotStarted),
                Group(GameActivityGroup, DynamicThemeViewKeys.Played, DynamicThemeViewKeys.Unplayed),
                Group(GameLastUnlockGroup, DynamicThemeViewKeys.HasLastUnlock, DynamicThemeViewKeys.NoLastUnlock));

        public static string GetGroupSelection(
            string filterExpression,
            string groupKey,
            IReadOnlyDictionary<string, string> groupMap)
        {
            return GetGroupSelection(
                filterExpression,
                new[] { groupKey },
                groupMap);
        }

        public static string GetGroupSelection(
            string filterExpression,
            IEnumerable<string> groupKeys,
            IReadOnlyDictionary<string, string> groupMap)
        {
            var groupSet = CreateGroupSet(groupKeys);
            if (groupSet.Count == 0 || groupMap == null)
            {
                return DynamicThemeViewKeys.All;
            }

            return DynamicThemeFilterExpression.Enumerate(filterExpression)
                .FirstOrDefault(key =>
                    groupMap.TryGetValue(key, out var mappedGroup) &&
                    groupSet.Contains(mappedGroup)) ??
                DynamicThemeViewKeys.All;
        }

        public static string SetGroupSelection(
            string filterExpression,
            string groupKey,
            string selectedKey,
            IReadOnlyDictionary<string, string> keyMap,
            IReadOnlyDictionary<string, string> groupMap)
        {
            return SetGroupSelection(
                filterExpression,
                new[] { groupKey },
                selectedKey,
                keyMap,
                groupMap);
        }

        public static string SetGroupSelection(
            string filterExpression,
            IEnumerable<string> groupKeys,
            string selectedKey,
            IReadOnlyDictionary<string, string> keyMap,
            IReadOnlyDictionary<string, string> groupMap)
        {
            var groupSet = CreateGroupSet(groupKeys);
            if (groupSet.Count == 0 || keyMap == null || groupMap == null)
            {
                return filterExpression ?? DynamicThemeViewKeys.All;
            }

            if (!DynamicThemeFilterExpression.TryNormalizeOne(selectedKey, keyMap, out var normalizedSelection))
            {
                normalizedSelection = DynamicThemeViewKeys.All;
            }

            var keys = DynamicThemeFilterExpression.Enumerate(filterExpression)
                .Where(key =>
                    !groupMap.TryGetValue(key, out var mappedGroup) ||
                    !groupSet.Contains(mappedGroup))
                .ToList();

            if (!string.Equals(normalizedSelection, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(normalizedSelection);
            }

            return keys.Count == 0
                ? DynamicThemeViewKeys.All
                : string.Join("+", keys.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static HashSet<string> CreateGroupSet(IEnumerable<string> groupKeys)
        {
            return new HashSet<string>(
                (groupKeys ?? Enumerable.Empty<string>())
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.Ordinal);
        }

        private static string[] Merge(params IEnumerable<string>[] groups)
        {
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups ?? Array.Empty<IEnumerable<string>>())
            {
                foreach (var key in group ?? Enumerable.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    {
                        keys.Add(key);
                    }
                }
            }

            return keys.ToArray();
        }

        private static IReadOnlyDictionary<string, string> CreateCanonicalKeyMap(IEnumerable<string> keys)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    map[key] = key;
                }
            }

            return map;
        }

        private static IReadOnlyDictionary<string, string> CreateGroupMap(
            params (string GroupKey, string[] Keys)[] groups)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups ?? Array.Empty<(string GroupKey, string[] Keys)>())
            {
                foreach (var key in group.Keys ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        map[key] = group.GroupKey;
                    }
                }
            }

            return map;
        }

        private static (string GroupKey, string[] Keys) Group(string groupKey, params string[] keys)
        {
            return (groupKey, keys);
        }
    }
}
