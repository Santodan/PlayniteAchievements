using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Friends
{
    /// <summary>
    /// The Friends Overview window's lists: friends merged and scored, the games grid, ownership
    /// links, and each friend's games, built from the cheap lists and the unlocked totals per
    /// friend account and provider game. Built off the UI thread; read-only afterwards.
    /// </summary>
    internal sealed class FriendsOverviewLists
    {
        public static readonly FriendsOverviewLists Empty =
            Build(new FriendsOverviewData(), Array.Empty<AchievementTotalsRecord>(), null);

        private readonly List<FriendSummaryItem> _identities;
        private readonly List<FriendGameLinkItem> _links;
        private readonly Dictionary<string, string> _scopeKeyByAccount;
        private readonly Dictionary<string, Dictionary<string, AchievementTotalsRecord>> _totalsByAccount;
        private readonly Dictionary<string, FriendGameSummaryItem> _gamesByKey;
        private readonly Dictionary<string, IReadOnlyList<FriendGameSummaryItem>> _gamesByFriend =
            new Dictionary<string, IReadOnlyList<FriendGameSummaryItem>>(StringComparer.OrdinalIgnoreCase);

        private FriendsOverviewLists(
            FriendOverviewProjection projection,
            List<FriendSummaryItem> identities,
            Dictionary<string, Dictionary<string, AchievementTotalsRecord>> totalsByAccount)
        {
            Projection = projection;
            _identities = identities;
            _totalsByAccount = totalsByAccount;
            _links = projection.FriendGameLinks.Where(link => link != null).ToList();
            _scopeKeyByAccount = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var friend in projection.Friends.Where(friend => friend != null))
            {
                foreach (var account in AccountsOf(friend))
                {
                    _scopeKeyByAccount[account.Key] = FriendOverviewProjection.GetFriendScopeKey(friend);
                }
            }

            var stored = identities.GroupBy(friend => FriendAccountRef.BuildKey(friend.ProviderKey, friend.ExternalUserId) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);
            FriendNames = projection.Friends.Where(friend => friend != null)
                .SelectMany(friend => AccountsOf(friend).Select(account => new KeyValuePair<FriendAccountRef, string>(account, friend.DisplayName)))
                .Where(name => name.Value != null && !(stored.TryGetValue(name.Key.Key, out var storedName) && string.Equals(storedName, name.Value, StringComparison.Ordinal)))
                .ToList();

            _gamesByKey = new Dictionary<string, FriendGameSummaryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in projection.AggregateGames)
            {
                var key = GameKey(game);
                if (key != null && !_gamesByKey.ContainsKey(key))
                {
                    _gamesByKey[key] = game;
                }
            }
        }

        /// <summary>Merged friends, the games grid and the ownership links.</summary>
        public FriendOverviewProjection Projection { get; }

        public IReadOnlyList<FriendSummaryItem> Friends => Projection.Friends;

        public IReadOnlyList<FriendGameSummaryItem> Games => Projection.AggregateGames;

        public bool HasData => Friends.Count > 0 || Games.Count > 0;

        /// <summary>
        /// The name each account's rows show where it is not the stored one: a nickname, or a
        /// merged friend's name, for <see cref="AchievementQuery.WithFriendNames"/>.
        /// </summary>
        public IReadOnlyList<KeyValuePair<FriendAccountRef, string>> FriendNames { get; }

        /// <summary>
        /// The lists of every friend, or with a Playnite game of the friends of that game and
        /// their counts and scores in it.
        /// </summary>
        public static FriendsOverviewLists Load(IFriendsOverviewReads reads, PersistedSettings settings, Guid? playniteGameId = null)
        {
            var unlocked = AchievementQuery.AllFriends.WhereUnlocked(true);
            if (playniteGameId.HasValue)
            {
                unlocked = unlocked.ForGames(new[] { playniteGameId.Value });
            }

            return Build(reads.LoadLists(playniteGameId), reads.Totals(unlocked, AchievementGroup.FriendGame), settings);
        }

        /// <summary>
        /// <paramref name="query"/> narrowed to the friend's accounts and to the game: its provider
        /// game, and its Playnite game, which also narrows which of the user's changes rerun it.
        /// </summary>
        public static AchievementQuery Scope(AchievementQuery query, FriendSummaryItem friend, FriendGameSummaryItem game)
        {
            if (friend != null)
            {
                query = query.ForFriendAccounts(AccountsOf(friend));
            }

            if (game == null)
            {
                return query;
            }

            var providerGame = game.AppId > 0 || !string.IsNullOrWhiteSpace(game.ProviderGameKey);
            if (providerGame)
            {
                query = query.ForProviderGame(game.ProviderKey, game.AppId, game.ProviderGameKey);
            }

            return game.PlayniteGameId.HasValue
                ? query.ForGames(new[] { game.PlayniteGameId.Value })
                : providerGame ? query : query.MatchNothing();
        }

        public FriendSummaryItem FindFriend(string friendScopeKey) => Projection.FindFriend(friendScopeKey);

        public FriendGameSummaryItem FindGame(string gameScopeKey) => Projection.FindGame(gameScopeKey);

        /// <summary>Whether the friend has unlocks in the game or owns it.</summary>
        public bool HasPairData(FriendSummaryItem friend, FriendGameSummaryItem game) =>
            game != null && GamesOf(friend).Any(candidate => FriendOverviewProjection.IsSameGame(candidate, game));

        /// <summary>
        /// The lists with each friend's scores, rarity and trophy counts from
        /// <paramref name="friendGameTotals"/> (unlocked rows of every friend, grouped by
        /// <see cref="AchievementGroup.FriendGame"/>), then merged as the settings say.
        /// </summary>
        public static FriendsOverviewLists Build(
            FriendsOverviewData lists,
            IReadOnlyList<AchievementTotalsRecord> friendGameTotals,
            PersistedSettings settings)
        {
            lists = lists ?? new FriendsOverviewData();
            var friends = (lists.Friends ?? new List<FriendSummaryItem>()).Where(friend => friend != null).ToList();

            // Copied before merging renames them, so rows are stamped from the stored names.
            var identities = friends.Select(CopyIdentity).ToList();

            var totalsByAccount = new Dictionary<string, Dictionary<string, AchievementTotalsRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in friendGameTotals ?? Array.Empty<AchievementTotalsRecord>())
            {
                var key = AchievementGroupKeys.FriendGame(record?.Key);
                var account = AccountKeyOfFriendKey(key.Key);
                var game = key.Value == null
                    ? null
                    : FriendOverviewProjection.BuildGameUnlockKey(key.Value.ProviderKey, key.Value.ProviderGameKey, key.Value.AppId, key.Value.PlayniteGameId);
                if (account == null || game == null)
                {
                    continue;
                }

                if (!totalsByAccount.TryGetValue(account, out var games))
                {
                    games = new Dictionary<string, AchievementTotalsRecord>(StringComparer.OrdinalIgnoreCase);
                    totalsByAccount[account] = games;
                }

                if (games.TryGetValue(game, out var existing))
                {
                    AchievementReadService.AddTotals(existing, record);
                }
                else
                {
                    games[game] = Copy(record);
                }
            }

            foreach (var friend in friends)
            {
                var sum = new AchievementTotalsRecord();
                if (totalsByAccount.TryGetValue(FriendAccountRef.BuildKey(friend.ProviderKey, friend.ExternalUserId) ?? string.Empty, out var games))
                {
                    foreach (var total in games.Values)
                    {
                        AchievementReadService.AddTotals(sum, total);
                    }
                }

                ApplyScores(friend, sum);
            }

            var projection = new FriendOverviewProjection(
                new FriendsOverviewData
                {
                    Friends = friends,
                    Games = lists.Games ?? new List<FriendGameSummaryItem>(),
                    FriendGameLinks = lists.FriendGameLinks ?? new List<FriendGameLinkItem>()
                },
                settings);
            return new FriendsOverviewLists(projection, identities, totalsByAccount);
        }

        /// <summary>The accounts a friend row stands for: a merged friend's members, or its own.</summary>
        public static IReadOnlyList<FriendAccountRef> AccountsOf(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                return Array.Empty<FriendAccountRef>();
            }

            var members = (friend.MemberAccounts ?? new List<FriendAccountRef>())
                .Where(account => !string.IsNullOrWhiteSpace(account?.ProviderKey) && !string.IsNullOrWhiteSpace(account.ExternalUserId))
                .Select(account => FriendAccountRef.From(account.ProviderKey, account.ExternalUserId))
                .ToList();
            if (members.Count > 0)
            {
                return members;
            }

            return friend.IsMergedFriend ||
                   string.IsNullOrWhiteSpace(friend.ProviderKey) ||
                   string.IsNullOrWhiteSpace(friend.ExternalUserId)
                ? (IReadOnlyList<FriendAccountRef>)Array.Empty<FriendAccountRef>()
                : new[] { FriendAccountRef.From(friend.ProviderKey, friend.ExternalUserId) };
        }

        /// <summary>The accounts of every favorite friend.</summary>
        public IReadOnlyList<FriendAccountRef> FavoriteAccounts() =>
            Friends.Where(friend => friend?.IsFavorite == true).SelectMany(AccountsOf).ToList();

        /// <summary>
        /// One friend's games: those they have unlocks in, by name, then those they only own, by
        /// name, each with their unlocks, scores and playtime.
        /// </summary>
        public IReadOnlyList<FriendGameSummaryItem> GamesOf(FriendSummaryItem friend)
        {
            var scopeKey = FriendOverviewProjection.GetFriendScopeKey(friend);
            if (friend == null || string.IsNullOrWhiteSpace(scopeKey))
            {
                return Array.Empty<FriendGameSummaryItem>();
            }

            if (!_gamesByFriend.TryGetValue(scopeKey, out var games))
            {
                var accounts = AccountsOf(friend);
                games = BuildGames(accounts, game => SumOf(accounts, game), aggregateOrder: false);
                _gamesByFriend[scopeKey] = games;
            }

            return games;
        }

        /// <summary>
        /// The games of a set of friends in games grid order: one row per game any of them has
        /// unlocks in or owns. <paramref name="uniqueTotals"/> are their unlocked rows grouped by
        /// <see cref="AchievementGroup.ProviderGame"/> with each achievement counted once.
        /// </summary>
        public IReadOnlyList<FriendGameSummaryItem> GamesOf(
            IReadOnlyList<FriendAccountRef> accounts,
            IReadOnlyList<AchievementTotalsRecord> uniqueTotals)
        {
            var byGame = new Dictionary<string, AchievementTotalsRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in uniqueTotals ?? Array.Empty<AchievementTotalsRecord>())
            {
                var identity = AchievementGroupKeys.ProviderGame(record?.Key);
                var key = identity == null
                    ? null
                    : FriendOverviewProjection.BuildGameUnlockKey(identity.ProviderKey, identity.ProviderGameKey, identity.AppId, identity.PlayniteGameId);
                if (key != null && !byGame.ContainsKey(key))
                {
                    byGame[key] = record;
                }
            }

            return BuildGames(accounts, game => byGame.TryGetValue(game, out var total) ? total : null, aggregateOrder: true);
        }

        /// <summary>
        /// Gives freshly read rows the friend identity the window shows: the merged friend's group,
        /// name and avatar, nicknames and favorites, as the settings say.
        /// </summary>
        public void StampIdentity(IReadOnlyList<FriendAchievementDisplayItem> rows, PersistedSettings settings)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            FriendOverviewProjection.ApplyMergeIdentity(
                new FriendsOverviewData
                {
                    Friends = _identities.Select(CopyIdentity).ToList(),
                    AllAchievements = rows as List<FriendAchievementDisplayItem> ?? rows.ToList()
                },
                settings);
        }

        private IReadOnlyList<FriendGameSummaryItem> BuildGames(
            IReadOnlyList<FriendAccountRef> accounts,
            Func<string, AchievementTotalsRecord> uniqueTotals,
            bool aggregateOrder)
        {
            var accountKeys = new HashSet<string>(accounts.Select(account => account.Key), StringComparer.OrdinalIgnoreCase);
            var friendUnlocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var friendsWithUnlocks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var friendsWithGame = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in accountKeys)
            {
                if (!_totalsByAccount.TryGetValue(account, out var games))
                {
                    continue;
                }

                var scope = ScopeKeyOf(account);
                foreach (var game in games)
                {
                    if (game.Value.UnlockedCount <= 0)
                    {
                        continue;
                    }

                    friendUnlocks[game.Key] = (friendUnlocks.TryGetValue(game.Key, out var count) ? count : 0) + (int)game.Value.UnlockedCount;
                    Add(friendsWithUnlocks, game.Key, scope);
                    Add(friendsWithGame, game.Key, scope);
                }
            }

            // One link per friend and game, as the merged friend's row takes it.
            var linksByGame = new Dictionary<string, List<FriendGameLinkItem>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in _links)
            {
                var account = FriendAccountRef.BuildKey(link.ProviderKey, link.ExternalUserId);
                var game = FriendOverviewProjection.BuildGameUnlockKey(link.ProviderKey, link.ProviderGameKey, link.AppId, link.PlayniteGameId);
                if (account == null || game == null || !accountKeys.Contains(account))
                {
                    continue;
                }

                var scope = ScopeKeyOf(account);
                if (!seen.Add(scope + "\u001e" + game))
                {
                    continue;
                }

                if (!linksByGame.TryGetValue(game, out var links))
                {
                    links = new List<FriendGameLinkItem>();
                    linksByGame[game] = links;
                }

                links.Add(link);
                Add(friendsWithGame, game, scope);
            }

            var withUnlocks = new List<FriendGameSummaryItem>();
            var ownedOnly = new List<FriendGameSummaryItem>();
            var built = new Dictionary<string, FriendGameSummaryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _gamesByKey)
            {
                var totals = uniqueTotals(pair.Key);
                linksByGame.TryGetValue(pair.Key, out var links);
                if ((totals == null || totals.UnlockedCount <= 0) && links == null)
                {
                    continue;
                }

                var item = BuildGame(
                    pair.Value,
                    totals,
                    links ?? (IReadOnlyList<FriendGameLinkItem>)Array.Empty<FriendGameLinkItem>(),
                    friendsWithUnlocks.TryGetValue(pair.Key, out var unlocking) ? unlocking.Count : 0,
                    friendUnlocks.TryGetValue(pair.Key, out var unlockCount) ? unlockCount : 0,
                    friendsWithGame.TryGetValue(pair.Key, out var having) ? having.Count : 0);
                (item.UniqueFriendUnlockedAchievementsCount > 0 ? withUnlocks : ownedOnly).Add(item);
                built[pair.Key] = item;
            }

            if (aggregateOrder)
            {
                return Games.Select(game => built.TryGetValue(GameKey(game) ?? string.Empty, out var item) ? item : null)
                    .Where(item => item != null)
                    .Distinct()
                    .ToList();
            }

            return withUnlocks.OrderBy(game => game.GameName, StringComparer.OrdinalIgnoreCase)
                .Concat(ownedOnly.OrderBy(game => game.GameName, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        private AchievementTotalsRecord SumOf(IReadOnlyList<FriendAccountRef> accounts, string game)
        {
            AchievementTotalsRecord sum = null;
            foreach (var account in accounts)
            {
                if (_totalsByAccount.TryGetValue(account.Key, out var games) && games.TryGetValue(game, out var total))
                {
                    if (sum == null)
                    {
                        sum = new AchievementTotalsRecord();
                    }

                    AchievementReadService.AddTotals(sum, total);
                }
            }

            return sum;
        }

        /// <summary>
        /// One game's row over a set of friends: unique unlocks and scores from
        /// <paramref name="totals"/>, the friend counts as given, and playtime from the links.
        /// </summary>
        private static FriendGameSummaryItem BuildGame(
            FriendGameSummaryItem source,
            AchievementTotalsRecord totals,
            IReadOnlyList<FriendGameLinkItem> links,
            int friendsWithUnlocks,
            int friendUnlockCount,
            int friendCount)
        {
            var unique = (int)Math.Min(int.MaxValue, totals?.UnlockedCount ?? 0);
            var playtimeMinutes = links.Sum(link => Math.Max(0L, link?.PlaytimeForeverMinutes ?? 0L));
            var lastPlayedUtc = links.Select(link => link?.LastPlayedUtc).Where(value => value.HasValue).DefaultIfEmpty().Max();
            var item = new FriendGameSummaryItem
            {
                ProviderKey = source.ProviderKey,
                DisplayProviderKey = source.DisplayProviderKey,
                Provider = source.Provider,
                ProviderIconKey = source.ProviderIconKey,
                ProviderColorHex = source.ProviderColorHex,
                AppId = source.AppId,
                ProviderGameKey = source.ProviderGameKey,
                PlayniteGameId = source.PlayniteGameId,
                GameName = source.GameName,
                SortingName = source.SortingName,
                GameLogo = source.GameLogo,
                GameCoverPath = source.GameCoverPath,
                PlatformText = source.PlatformText,
                Platforms = source.Platforms,
                RegionText = source.RegionText,
                PlaytimeSeconds = ToPlaytimeSeconds(playtimeMinutes),
                LastPlayed = lastPlayedUtc,
                Added = source.Added,
                ReleaseDate = source.ReleaseDate,
                TotalAchievements = source.TotalAchievements > 0 ? source.TotalAchievements : unique,
                CollectionScoreTotal = source.CollectionScoreTotal,
                PrestigeScoreTotal = source.PrestigeScoreTotal,
                TotalCommonPossible = source.TotalCommonPossible,
                TotalUncommonPossible = source.TotalUncommonPossible,
                TotalRarePossible = source.TotalRarePossible,
                TotalUltraRarePossible = source.TotalUltraRarePossible,
                TrophyPlatinumTotal = source.TrophyPlatinumTotal,
                TrophyGoldTotal = source.TrophyGoldTotal,
                TrophySilverTotal = source.TrophySilverTotal,
                TrophyBronzeTotal = source.TrophyBronzeTotal,
                FriendCount = Math.Max(friendCount, links.Count > 0 || unique > 0 ? 1 : 0),
                FriendsWithUnlocksCount = Math.Max(friendsWithUnlocks, unique > 0 ? 1 : 0),
                FriendUnlockedAchievementsCount = Math.Max(friendUnlockCount, unique),
                UniqueFriendUnlockedAchievementsCount = unique,
                LastFriendUnlockUtc = AchievementSqlFunctions.ParseUtc(totals?.LastUnlockUtc),
                TotalFriendPlaytimeMinutes = playtimeMinutes,
                AverageFriendPlaytimeMinutes = links.Count > 0 ? playtimeMinutes / links.Count : playtimeMinutes,
                LastFriendPlayedUtc = lastPlayedUtc,
                CollectionScore = Int(totals?.CollectionScore),
                PrestigeScore = Int(totals?.PrestigeScore),
                Points = Int(totals?.Points),
                CommonCount = Int(totals?.CommonUnlocked),
                UncommonCount = Int(totals?.UncommonUnlocked),
                RareCount = Int(totals?.RareUnlocked),
                UltraRareCount = Int(totals?.UltraRareUnlocked),
                TrophyPlatinumCount = Int(totals?.PlatinumUnlocked),
                TrophyGoldCount = Int(totals?.GoldUnlocked),
                TrophySilverCount = Int(totals?.SilverUnlocked),
                TrophyBronzeCount = Int(totals?.BronzeUnlocked)
            };

            item.IsCompleted = item.TotalAchievements > 0 && item.UnlockedAchievements >= item.TotalAchievements;
            return item;
        }

        private static void ApplyScores(FriendSummaryItem friend, AchievementTotalsRecord totals)
        {
            friend.CollectionScore = Int(totals.CollectionScore);
            friend.PrestigeScore = Int(totals.PrestigeScore);
            friend.CommonCount = Int(totals.CommonUnlocked);
            friend.UncommonCount = Int(totals.UncommonUnlocked);
            friend.RareCount = Int(totals.RareUnlocked);
            friend.UltraRareCount = Int(totals.UltraRareUnlocked);
            friend.TrophyPlatinumCount = Int(totals.PlatinumUnlocked);
            friend.TrophyGoldCount = Int(totals.GoldUnlocked);
            friend.TrophySilverCount = Int(totals.SilverUnlocked);
            friend.TrophyBronzeCount = Int(totals.BronzeUnlocked);
            friend.CollectionLevel = DisplayLevel(AchievementLevelCalculator.CalculateModern(friend.CollectionScore));
            friend.PrestigeLevel = DisplayLevel(AchievementLevelCalculator.CalculateModern(friend.PrestigeScore));
        }

        private static int DisplayLevel(AchievementLevelSnapshot snapshot) =>
            snapshot == null ? 0 : snapshot.DisplayLevel > 0 ? snapshot.DisplayLevel : snapshot.Level;

        private string ScopeKeyOf(string accountKey) =>
            _scopeKeyByAccount.TryGetValue(accountKey, out var scope) ? scope : accountKey;

        private static string GameKey(FriendGameSummaryItem game) =>
            FriendOverviewProjection.BuildGameUnlockKey(game?.ProviderKey, game?.ProviderGameKey, game?.AppId ?? 0, game?.PlayniteGameId);

        // The Friend group key spells the account as provider, unit separator, external id.
        private static string AccountKeyOfFriendKey(string friendKey)
        {
            var split = (friendKey ?? string.Empty).IndexOf('\u001f');
            return split < 0 ? null : FriendAccountRef.BuildKey(friendKey.Substring(0, split), friendKey.Substring(split + 1));
        }

        private static void Add(Dictionary<string, HashSet<string>> sets, string key, string value)
        {
            if (!sets.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sets[key] = set;
            }

            set.Add(value);
        }

        private static AchievementTotalsRecord Copy(AchievementTotalsRecord record)
        {
            var copy = new AchievementTotalsRecord { Key = record.Key };
            AchievementReadService.AddTotals(copy, record);
            return copy;
        }

        private static FriendSummaryItem CopyIdentity(FriendSummaryItem friend) => new FriendSummaryItem
        {
            ProviderKey = friend.ProviderKey,
            ExternalUserId = friend.ExternalUserId,
            DisplayName = friend.DisplayName,
            AvatarPath = friend.AvatarPath,
            LastUnlockUtc = friend.LastUnlockUtc
        };

        private static int Int(long? value) => (int)Math.Max(0, Math.Min(int.MaxValue, value ?? 0));

        private static ulong ToPlaytimeSeconds(long minutes)
        {
            var normalized = Math.Max(0L, minutes);
            return normalized > (long)(ulong.MaxValue / 60UL)
                ? ulong.MaxValue
                : (ulong)normalized * 60UL;
        }
    }
}
