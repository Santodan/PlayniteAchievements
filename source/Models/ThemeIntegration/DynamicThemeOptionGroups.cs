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
        public const string AchievementCategoryTypeGroup = "CategoryTypeFilter";
        public const string GameCompletionGroup = "Completion";
        public const string GameStartedGroup = "Started";
        public const string GameActivityGroup = "Activity";
        public const string GameLastUnlockGroup = "LastUnlock";
        public const string FriendLastUnlockGroup = "FriendLastUnlock";

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

        // The kinds and built-in tags, plus the derived unlock modes themes could always filter by.
        // User tags are not listed but still filter: the group map recognizes any tag key.
        private static readonly string[] CategoryTypeKeys =
            Services.Achievements.CategoryKindHelper.AssignableKinds
                .Concat(Services.Achievements.AchievementTagHelper.BuiltInTagKeys)
                .Concat(new[] { Services.Achievements.AchievementTagHelper.Softcore, Services.Achievements.AchievementTagHelper.Hardcore })
                .ToArray();

        public static readonly string[] AchievementCategoryTypeFilterKeys =
            new[] { DynamicThemeViewKeys.All }.Concat(CategoryTypeKeys).ToArray();

        /// <summary>
        /// The type filter options for <paramref name="gameId"/> (every game when null): All, the
        /// kinds, the tags available there (user tags included), then the unlock modes.
        /// </summary>
        public static IReadOnlyList<string> GetAchievementCategoryTypeFilterKeys(Guid? gameId = null) =>
            new[] { DynamicThemeViewKeys.All }
                .Concat(Services.Achievements.AchievementClassificationFilter.GetFilterKeys(gameId))
                .Concat(new[] { Services.Achievements.AchievementTagHelper.Softcore, Services.Achievements.AchievementTagHelper.Hardcore })
                .ToList();

        /// <summary>
        /// <see cref="GetAchievementCategoryTypeFilterKeys"/> for every game, read afresh on each use,
        /// for option bindings built once.
        /// </summary>
        public static readonly IReadOnlyList<string> LiveAchievementCategoryTypeFilterKeys =
            new LiveKeyList(() => GetAchievementCategoryTypeFilterKeys());

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
            AchievementTrophyFilterKeys,
            AchievementCategoryTypeFilterKeys);

        public static readonly string[] GameSummaryFilterKeys = Merge(
            GameProgressFilterKeys,
            GameActivityFilterKeys);

        public static readonly string[] FriendSummaryFilterKeys =
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.HasLastUnlock,
            DynamicThemeViewKeys.NoLastUnlock
        };

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

        public static readonly string[] CategorySummaryFilterKeys =
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.Completed,
            DynamicThemeViewKeys.Incomplete
        };

        // Default preserves the builder's order (the game's custom category order).
        public static readonly string[] CategorySummarySortKeys =
        {
            DynamicThemeViewKeys.Default,
            DynamicThemeViewKeys.Name,
            DynamicThemeViewKeys.Progress
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

        public static readonly string[] FriendSummarySortKeys =
        {
            DynamicThemeViewKeys.Name,
            DynamicThemeViewKeys.Provider,
            DynamicThemeViewKeys.LastUnlock,
            DynamicThemeViewKeys.UnlockedCount,
            DynamicThemeViewKeys.SharedGamesCount
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
            "Vita3K",
            "Xenia",
            "Minecraft",
            "Manual",
            "Exophase",
            "Hoyoverse"
        };

        public static readonly IReadOnlyDictionary<string, string> AchievementFilterKeyMap =
            CreateCanonicalKeyMap(AchievementFilterKeys);

        public static readonly IReadOnlyDictionary<string, string> GameSummaryFilterKeyMap =
            CreateCanonicalKeyMap(GameSummaryFilterKeys);

        public static readonly IReadOnlyDictionary<string, string> CategorySummaryFilterKeyMap =
            CreateCanonicalKeyMap(CategorySummaryFilterKeys);

        public static readonly IReadOnlyDictionary<string, string> CategorySummarySortKeyMap =
            CreateCanonicalKeyMap(CategorySummarySortKeys);

        public static readonly IReadOnlyDictionary<string, string> FriendSummaryFilterKeyMap =
            CreateCanonicalKeyMap(FriendSummaryFilterKeys);

        public static readonly IReadOnlyDictionary<string, string> SelectedGameAchievementSortKeyMap =
            CreateCanonicalKeyMap(SelectedGameAchievementSortKeys);

        public static readonly IReadOnlyDictionary<string, string> LibraryAchievementSortKeyMap =
            CreateCanonicalKeyMap(LibraryAchievementSortKeys);

        public static readonly IReadOnlyDictionary<string, string> GameSummarySortKeyMap =
            CreateCanonicalKeyMap(GameSummarySortKeys);

        public static readonly IReadOnlyDictionary<string, string> FriendSummarySortKeyMap =
            CreateFriendSummarySortKeyMap();

        public static readonly IReadOnlyDictionary<string, string> SortDirectionKeyMap =
            CreateCanonicalKeyMap(SortDirectionKeys);

        public static readonly IReadOnlyDictionary<string, string> AchievementFilterGroupMap =
            new ClassificationAwareGroupMap(CreateGroupMap(
                Group(AchievementStatusGroup, DynamicThemeViewKeys.Unlocked, DynamicThemeViewKeys.Locked),
                Group(AchievementProgressGroup, DynamicThemeViewKeys.InProgress, DynamicThemeViewKeys.NoProgress),
                Group(AchievementVisibilityGroup, DynamicThemeViewKeys.Visible, DynamicThemeViewKeys.Hidden),
                Group(AchievementNotesGroup, DynamicThemeViewKeys.HasNotes, DynamicThemeViewKeys.NoNotes),
                Group(AchievementSpecialGroup, DynamicThemeViewKeys.Capstone),
                Group(AchievementRarityGroup, DynamicThemeViewKeys.Common, DynamicThemeViewKeys.Uncommon, DynamicThemeViewKeys.Rare, DynamicThemeViewKeys.UltraRare),
                Group(AchievementTrophyGroup, DynamicThemeViewKeys.Platinum, DynamicThemeViewKeys.Gold, DynamicThemeViewKeys.Silver, DynamicThemeViewKeys.Bronze),
                Group(AchievementCategoryTypeGroup, CategoryTypeKeys)));

        public static readonly IReadOnlyDictionary<string, string> GameSummaryFilterGroupMap =
            CreateGroupMap(
                Group(GameCompletionGroup, DynamicThemeViewKeys.Completed, DynamicThemeViewKeys.Incomplete),
                Group(GameStartedGroup, DynamicThemeViewKeys.Started, DynamicThemeViewKeys.NotStarted),
                Group(GameActivityGroup, DynamicThemeViewKeys.Played, DynamicThemeViewKeys.Unplayed),
                Group(GameLastUnlockGroup, DynamicThemeViewKeys.HasLastUnlock, DynamicThemeViewKeys.NoLastUnlock));

        public static readonly IReadOnlyDictionary<string, string> FriendSummaryFilterGroupMap =
            CreateGroupMap(
                Group(FriendLastUnlockGroup, DynamicThemeViewKeys.HasLastUnlock, DynamicThemeViewKeys.NoLastUnlock));

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

        private static IReadOnlyDictionary<string, string> CreateFriendSummarySortKeyMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in FriendSummarySortKeys)
            {
                map[key] = key;
            }

            map[DynamicThemeViewKeys.AchievementCount] = DynamicThemeViewKeys.SharedGamesCount;
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

        private sealed class LiveKeyList : IReadOnlyList<string>
        {
            private readonly Func<IReadOnlyList<string>> _read;

            public LiveKeyList(Func<IReadOnlyList<string>> read)
            {
                _read = read;
            }

            public string this[int index] => _read()[index];

            public int Count => _read().Count;

            public IEnumerator<string> GetEnumerator() => _read().GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static (string GroupKey, string[] Keys) Group(string groupKey, params string[] keys)
        {
            return (groupKey, keys);
        }

        /// <summary>
        /// The achievement filter group map, which also places any kind or tag key outside the
        /// listed vocabulary (a user tag, a renamed key a theme still names) in the type group.
        /// </summary>
        private sealed class ClassificationAwareGroupMap : IReadOnlyDictionary<string, string>
        {
            private readonly IReadOnlyDictionary<string, string> _inner;

            public ClassificationAwareGroupMap(IReadOnlyDictionary<string, string> inner)
            {
                _inner = inner;
            }

            public string this[string key] =>
                TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

            public IEnumerable<string> Keys => _inner.Keys;

            public IEnumerable<string> Values => _inner.Values;

            public int Count => _inner.Count;

            public bool ContainsKey(string key) => TryGetValue(key, out _);

            public bool TryGetValue(string key, out string value)
            {
                if (key != null && _inner.TryGetValue(key, out value))
                {
                    return true;
                }

                if (Services.Achievements.AchievementClassificationFilter.IsFilterKey(key))
                {
                    value = AchievementCategoryTypeGroup;
                    return true;
                }

                value = null;
                return false;
            }

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
