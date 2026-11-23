using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Tests.TestSupport
{
    /// <summary>
    /// <see cref="IFriendsOverviewReads"/> over in-memory lists and friend rows, answering the
    /// query features the Friends Overview window uses as the SQL does, and recording each read.
    /// Every read hands out fresh copies, as SQL does.
    /// </summary>
    internal sealed class InMemoryFriendsOverviewReads : IFriendsOverviewReads
    {
        public InMemoryFriendsOverviewReads(FriendsOverviewData data)
        {
            Data = data;
            Feed = new AchievementDataFeed(null, null);
        }

        public FriendsOverviewData Data { get; set; }

        public AchievementDataFeed Feed { get; }

        public int ListLoads { get; private set; }

        public List<AchievementQuery> RowReads { get; } = new List<AchievementQuery>();

        public List<AchievementQuery> KeyReads { get; } = new List<AchievementQuery>();

        /// <summary>The Playnite game of each lists load; null for every game.</summary>
        public List<Guid?> ListGames { get; } = new List<Guid?>();

        public List<KeyValuePair<AchievementQuery, AchievementGroup>> TotalsReads { get; } =
            new List<KeyValuePair<AchievementQuery, AchievementGroup>>();

        /// <summary>The friend rows: <c>AllAchievements</c>.</summary>
        private List<FriendAchievementDisplayItem> Source =>
            (Data.AllAchievements ?? new List<FriendAchievementDisplayItem>()).Where(row => row != null).ToList();

        public FriendsOverviewData LoadLists(Guid? playniteGameId = null)
        {
            ListLoads++;
            ListGames.Add(playniteGameId);
            return new FriendsOverviewData
            {
                Friends = (Data.Friends ?? new List<FriendSummaryItem>()).Select(CopyFriend).ToList(),
                Games = Data.Games ?? new List<FriendGameSummaryItem>(),
                FriendGameLinks = (Data.FriendGameLinks ?? new List<FriendGameLinkItem>()).Select(CopyLink).ToList()
            };
        }

        /// <summary>Each key names a row by its index in the source rows.</summary>
        public IReadOnlyList<AchievementQuerySql.RowKey> SortedKeys(AchievementQuery query)
        {
            KeyReads.Add(query);
            var source = Source;
            var rows = Sorted(Matching(query), query).AsEnumerable();
            if (query.Limit > 0)
            {
                rows = rows.Take(query.Limit);
            }

            return rows.Select(row => new AchievementQuerySql.RowKey { DefinitionId = source.IndexOf(row) }).ToList();
        }

        public IReadOnlyList<FriendAchievementDisplayItem> Rows(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys = null)
        {
            RowReads.Add(query);
            if (keys != null)
            {
                var source = Source;
                return keys.Where(key => key.DefinitionId >= 0 && key.DefinitionId < source.Count)
                    .Select(key => source[(int)key.DefinitionId])
                    .Where(row => Matches(query, row))
                    .Select(row => Copy(row, query))
                    .ToList();
            }

            var rows = Sorted(Matching(query), query).AsEnumerable();
            if (query.Offset > 0)
            {
                rows = rows.Skip(query.Offset);
            }

            if (query.Limit > 0)
            {
                rows = rows.Take(query.Limit);
            }

            return rows.Select(row => Copy(row, query)).ToList();
        }

        public IReadOnlyList<AchievementTotalsRecord> Totals(AchievementQuery query, AchievementGroup group)
        {
            TotalsReads.Add(new KeyValuePair<AchievementQuery, AchievementGroup>(query, group));
            var rows = Matching(query);
            var order = Source;
            if (query.DistinctAchievements)
            {
                rows = rows.GroupBy(row => ProviderGameKey(row) + "\u001e" + (row.ApiName ?? row.DisplayName))
                    .Select(set => set.OrderByDescending(row => row.Unlocked).ThenByDescending(row => row.UnlockTimeUtc).First())
                    .ToList();
            }

            var totals = new List<AchievementTotalsRecord>();
            foreach (var set in rows.GroupBy(row => KeyOf(row, group)))
            {
                var total = new AchievementTotalsRecord { Key = set.Key };
                foreach (var row in set)
                {
                    total.Count++;
                    var trophy = (row.TrophyType ?? string.Empty).Trim().ToLowerInvariant();
                    total.PlatinumCount += trophy == "platinum" ? 1 : 0;
                    total.GoldCount += trophy == "gold" ? 1 : 0;
                    total.SilverCount += trophy == "silver" ? 1 : 0;
                    total.BronzeCount += trophy == "bronze" ? 1 : 0;
                    total.HiddenLockedCount += row.Hidden && !row.Unlocked ? 1 : 0;
                    if (row.CategoryOrderIndex < int.MaxValue && (!total.MinCategoryOrder.HasValue || row.CategoryOrderIndex < total.MinCategoryOrder))
                    {
                        total.MinCategoryOrder = row.CategoryOrderIndex;
                    }

                    var position = order.IndexOf(row).ToString("D10", CultureInfo.InvariantCulture);
                    if (group == AchievementGroup.Category && (total.FirstDisplayOrder == null || string.CompareOrdinal(position, total.FirstDisplayOrder) < 0))
                    {
                        total.FirstDisplayOrder = position;
                    }

                    if (!row.Unlocked)
                    {
                        continue;
                    }

                    total.UnlockedCount++;
                    total.CollectionScore += AchievementScoreCalculator.GetCollectionValue(row.Rarity);
                    total.PrestigeScore += AchievementScoreCalculator.GetPrestigeValue(row.GlobalPercentUnlocked, row.Rarity);
                    total.Points += Math.Max(0, row.Points);
                    total.CommonUnlocked += row.Rarity == RarityTier.Common ? 1 : 0;
                    total.UncommonUnlocked += row.Rarity == RarityTier.Uncommon ? 1 : 0;
                    total.RareUnlocked += row.Rarity == RarityTier.Rare ? 1 : 0;
                    total.UltraRareUnlocked += row.Rarity == RarityTier.UltraRare ? 1 : 0;
                    total.PlatinumUnlocked += trophy == "platinum" ? 1 : 0;
                    total.GoldUnlocked += trophy == "gold" ? 1 : 0;
                    total.SilverUnlocked += trophy == "silver" ? 1 : 0;
                    total.BronzeUnlocked += trophy == "bronze" ? 1 : 0;
                    var time = Stamp(row.UnlockTimeUtc);
                    if (time != null && (total.LastUnlockUtc == null || string.CompareOrdinal(time, total.LastUnlockUtc) > 0))
                    {
                        total.LastUnlockUtc = time;
                    }
                }

                totals.Add(total);
            }

            return group == AchievementGroup.Tag
                ? totals.SelectMany(total => AchievementTagHelper.GetComponents(total.Key).Select(tag => new AchievementTotalsRecord { Key = tag, Count = total.Count }))
                    .ToList()
                : totals;
        }

        private List<FriendAchievementDisplayItem> Matching(AchievementQuery query) =>
            Source.Where(row => Matches(query, row)).ToList();

        private bool Matches(AchievementQuery query, FriendAchievementDisplayItem row)
        {
            if (query.MatchesNothing || !query.Friends)
            {
                return false;
            }

            if (query.FriendAccounts != null &&
                !query.FriendAccounts.Any(account => Same(account.ProviderKey, row.ProviderKey) && Same(account.ExternalUserId, row.FriendExternalUserId)))
            {
                return false;
            }

            if (query.GameIds != null && !(row.PlayniteGameId.HasValue && query.GameIds.Contains(row.PlayniteGameId.Value)))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.GameProviderKey) &&
                !(Same(query.GameProviderKey, row.ProviderKey) &&
                  ((query.AppId > 0 && row.AppId == query.AppId) ||
                   (!string.IsNullOrWhiteSpace(query.ProviderGameKey) && Same(query.ProviderGameKey, row.ProviderGameKey)))))
            {
                return false;
            }

            if (query.Unlocked.HasValue && row.Unlocked != query.Unlocked.Value)
            {
                return false;
            }

            if (query.RequireUnlockTime && !(row.Unlocked && row.UnlockTimeUtc.HasValue))
            {
                return false;
            }

            if (query.HideHiddenLocked && row.Hidden && !row.Unlocked)
            {
                return false;
            }

            if (query.CapstonesOnly && !row.IsCapstone)
            {
                return false;
            }

            if (query.Rarities != null && !query.Rarities.Contains(row.Rarity))
            {
                return false;
            }

            if (query.TrophyTypes != null &&
                !query.TrophyTypes.Any(type => Same(type?.Trim(), row.TrophyType?.Trim())))
            {
                return false;
            }

            if (query.ClassificationKeys != null &&
                !query.ClassificationKeys.Any(key => AchievementClassificationFilter.Matches(row.CategoryKind, row.Tags, key)))
            {
                return false;
            }

            if (query.CategoryLabels != null &&
                !query.CategoryLabels.Select(AchievementCategoryHelper.NormalizeCategoryOrDefault)
                    .Contains(AchievementCategoryHelper.NormalizeCategoryOrDefault(row.CategoryLabel), StringComparer.Ordinal))
            {
                return false;
            }

            if (query.ProviderKey != null && !Same(query.ProviderKey, row.ProviderKey))
            {
                return false;
            }

            if (query.KeyGroups != null && !query.KeyGroups.All(group => group.Any(key => MatchesKey(key, row))))
            {
                return false;
            }

            var search = SearchQuery.From(query.SearchText);
            return !search.HasValue ||
                   search.Matches(SearchTextBuilder.FromValues(NameOf(query, row), row.GameName, row.DisplayName, row.Description, row.CategoryLabel, row.ProviderKey));
        }

        // As AchievementQuerySql matches one key of AchievementQuery.KeyGroups.
        private static bool MatchesKey(string key, FriendAchievementDisplayItem row)
        {
            switch (key)
            {
                case "Unlocked":
                    return row.Unlocked;
                case "Locked":
                    return !row.Unlocked;
                case "Capstone":
                    return row.IsCapstone;
                case "Common":
                case "Uncommon":
                case "Rare":
                case "UltraRare":
                    return row.Rarity.ToString() == key;
                case "Platinum":
                case "Gold":
                case "Silver":
                case "Bronze":
                    return Same(row.TrophyType?.Trim(), key);
                default:
                    return AchievementClassificationFilter.Matches(row.CategoryKind, row.Tags, key);
            }
        }

        private List<FriendAchievementDisplayItem> Sorted(List<FriendAchievementDisplayItem> rows, AchievementQuery query)
        {
            var order = Source;
            int Index(FriendAchievementDisplayItem row) => order.IndexOf(row);
            IOrderedEnumerable<FriendAchievementDisplayItem> sorted;
            switch (query.Sort)
            {
                case AchievementSort.UnlockTime:
                    var bucketed = rows.OrderBy(row => row.Unlocked && row.UnlockTimeUtc.HasValue ? 0 : row.Unlocked ? 1 : 2);
                    sorted = query.Descending
                        ? bucketed.ThenByDescending(row => row.UnlockTimeUtc)
                        : bucketed.ThenBy(row => row.UnlockTimeUtc);
                    break;
                case AchievementSort.Name:
                    sorted = query.Descending
                        ? rows.OrderByDescending(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                        : rows.OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase);
                    break;
                case AchievementSort.Friend:
                    sorted = query.Descending
                        ? rows.OrderByDescending(row => NameOf(query, row), StringComparer.OrdinalIgnoreCase)
                        : rows.OrderBy(row => NameOf(query, row), StringComparer.OrdinalIgnoreCase);
                    break;
                case AchievementSort.Rarity:
                    sorted = query.Descending
                        ? rows.OrderByDescending(row => row.RaritySortValue)
                        : rows.OrderBy(row => row.RaritySortValue);
                    break;
                default:
                    // Friend name, game name, then the order the rows were given in.
                    sorted = rows.OrderBy(row => row.FriendName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(row => row.GameName, StringComparer.OrdinalIgnoreCase);
                    break;
            }

            return sorted.ThenBy(Index).ToList();
        }

        private FriendAchievementDisplayItem Copy(FriendAchievementDisplayItem row, AchievementQuery query)
        {
            var copy = new FriendAchievementDisplayItem
            {
                ApiName = row.ApiName ?? row.DisplayName,
                ProviderKey = row.ProviderKey,
                FriendExternalUserId = row.FriendExternalUserId,
                FriendName = row.FriendName,
                FriendAvatarPath = row.FriendAvatarPath,
                AppId = row.AppId,
                ProviderGameKey = row.ProviderGameKey,
                PlayniteGameId = row.PlayniteGameId,
                GameName = row.GameName,
                SortingName = row.SortingName,
                DisplayName = row.DisplayName,
                Description = row.Description,
                CategoryKind = row.CategoryKind,
                Tags = row.Tags,
                CategoryLabel = row.CategoryLabel,
                UnlockTimeUtc = row.UnlockTimeUtc,
                Unlocked = row.Unlocked,
                Hidden = row.Hidden,
                IsCapstone = row.IsCapstone,
                Rarity = row.Rarity,
                GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                TrophyType = row.TrophyType,
                UnlockedBySelf = row.UnlockedBySelf,
                SelfUnlockTimeUtc = row.SelfUnlockTimeUtc,
                SelfHasGameData = row.SelfHasGameData,
                FriendOwnsGame = row.FriendOwnsGame
            };

            if (query.CompareAccounts != null)
            {
                var compare = Source.Where(other =>
                        query.CompareAccounts.Any(account => Same(account.ProviderKey, other.ProviderKey) && Same(account.ExternalUserId, other.FriendExternalUserId)) &&
                        ProviderGameKey(other) == ProviderGameKey(row))
                    .ToList();
                var unlocked = compare.FirstOrDefault(other => other.Unlocked && Same(other.ApiName ?? other.DisplayName, copy.ApiName));
                var hasGame = compare.Any(other => other.Unlocked) ||
                              (Data.FriendGameLinks ?? new List<FriendGameLinkItem>()).Any(link =>
                                  link.OwnershipConfirmed &&
                                  query.CompareAccounts.Any(account => Same(account.ProviderKey, link.ProviderKey) && Same(account.ExternalUserId, link.ExternalUserId)) &&
                                  Same(link.ProviderKey, row.ProviderKey) && link.AppId == row.AppId);
                copy.ApplyComparison(null, null, unlocked?.UnlockTimeUtc, unlocked != null, applicable: hasGame);
            }

            return copy;
        }

        // The name the query gives the row's account, else the stored one, as the SQL joins it.
        private static string NameOf(AchievementQuery query, FriendAchievementDisplayItem row) =>
            query.FriendNames?.FirstOrDefault(name => Same(name.Key.ProviderKey, row.ProviderKey) && Same(name.Key.ExternalUserId, row.FriendExternalUserId)).Value
            ?? row.FriendName;

        private static string KeyOf(FriendAchievementDisplayItem row, AchievementGroup group)
        {
            switch (group)
            {
                case AchievementGroup.Friend:
                    return row.ProviderKey + "\u001f" + row.FriendExternalUserId;
                case AchievementGroup.FriendGame:
                    return row.ProviderKey + "\u001f" + row.FriendExternalUserId + "\u001e" + ProviderGameKey(row);
                case AchievementGroup.ProviderGame:
                    return ProviderGameKey(row);
                case AchievementGroup.Category:
                    return row.CategoryLabel;
                case AchievementGroup.Tag:
                    return row.Tags;
                case AchievementGroup.Game:
                    return row.PlayniteGameId?.ToString("D");
                default:
                    throw new NotSupportedException(group.ToString());
            }
        }

        // As AchievementQuerySql spells the ProviderGame key.
        private static string ProviderGameKey(FriendAchievementDisplayItem row) =>
            row.ProviderKey + "\u001f" + (row.ProviderGameKey ?? string.Empty) + "\u001f" +
            row.AppId.ToString(CultureInfo.InvariantCulture) + "\u001f" + (row.PlayniteGameId?.ToString("D") ?? string.Empty);

        private static string Stamp(DateTime? time) =>
            time?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        private static bool Same(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private static FriendSummaryItem CopyFriend(FriendSummaryItem friend) => new FriendSummaryItem
        {
            ProviderKey = friend.ProviderKey,
            ExternalUserId = friend.ExternalUserId,
            DisplayName = friend.DisplayName,
            AvatarPath = friend.AvatarPath,
            SharedGamesCount = friend.SharedGamesCount,
            GamesWithUnlocksCount = friend.GamesWithUnlocksCount,
            CompletedGamesCount = friend.CompletedGamesCount,
            UnlockedAchievementsCount = friend.UnlockedAchievementsCount,
            RecentUnlockCount = friend.RecentUnlockCount,
            LastUnlockUtc = friend.LastUnlockUtc,
            LastRefreshedUtc = friend.LastRefreshedUtc,
            TotalPlaytimeMinutes = friend.TotalPlaytimeMinutes
        };

        private static FriendGameLinkItem CopyLink(FriendGameLinkItem link) => new FriendGameLinkItem
        {
            ProviderKey = link.ProviderKey,
            ExternalUserId = link.ExternalUserId,
            AppId = link.AppId,
            ProviderGameKey = link.ProviderGameKey,
            PlayniteGameId = link.PlayniteGameId,
            PlaytimeForeverMinutes = link.PlaytimeForeverMinutes,
            LastPlayedUtc = link.LastPlayedUtc,
            OwnershipConfirmed = link.OwnershipConfirmed
        };
    }
}
