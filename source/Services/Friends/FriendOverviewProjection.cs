using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace PlayniteAchievements.Services.Friends
{
    internal sealed class FriendOverviewProjection
    {
        public const string AllScopeKey = "All";
        public const string MergedProviderKey = "Merged";

        private readonly List<FriendSummaryItem> _friends;
        private readonly List<FriendGameSummaryItem> _aggregateGames;
        private readonly List<FriendAchievementDisplayItem> _recentUnlocks;
        private readonly List<FriendAchievementDisplayItem> _allAchievements;
        private readonly List<FriendAchievementDisplayItem> _allUnlockedAchievements;
        private readonly List<FriendGameLinkItem> _friendGameLinks;
        private readonly Dictionary<string, List<FriendGameSummaryItem>> _selectedFriendGamesByFriendKey;
        private readonly HashSet<string> _friendGameUnlockKeys;
        private readonly HashSet<string> _friendGameOwnershipKeys;

        public FriendOverviewProjection(FriendsOverviewData data, PersistedSettings settings = null)
        {
            data = ApplyMergeGroups(data ?? new FriendsOverviewData(), settings);
            _friends = data.Friends ?? new List<FriendSummaryItem>();
            _aggregateGames = data.Games ?? new List<FriendGameSummaryItem>();
            _recentUnlocks = data.RecentUnlocks ?? new List<FriendAchievementDisplayItem>();
            _allAchievements = ResolveAllAchievements(data);
            _allUnlockedAchievements = data.AllUnlockedAchievements ?? new List<FriendAchievementDisplayItem>();
            _friendGameLinks = data.FriendGameLinks ?? new List<FriendGameLinkItem>();
            _selectedFriendGamesByFriendKey = BuildSelectedFriendGameSummaries();
            _friendGameUnlockKeys = BuildFriendGameUnlockKeys();
            _friendGameOwnershipKeys = BuildFriendGameOwnershipKeys();
        }

        public IReadOnlyList<FriendSummaryItem> Friends => _friends;

        public IReadOnlyList<FriendGameSummaryItem> AggregateGames => _aggregateGames;

        public IReadOnlyList<FriendAchievementDisplayItem> RecentUnlocks => _recentUnlocks;

        public IReadOnlyList<FriendAchievementDisplayItem> AllAchievements => _allAchievements;

        public IReadOnlyList<FriendAchievementDisplayItem> AllUnlockedAchievements => _allUnlockedAchievements;

        public IReadOnlyList<FriendGameLinkItem> FriendGameLinks => _friendGameLinks;

        public bool HasData =>
            _friends.Count > 0 ||
            _aggregateGames.Count > 0 ||
            _recentUnlocks.Count > 0 ||
            _allAchievements.Count > 0 ||
            _allUnlockedAchievements.Count > 0;

        public IReadOnlyList<FriendGameSummaryItem> GetSelectedFriendGames(FriendSummaryItem friend)
        {
            var friendKey = GetFriendScopeKey(friend);
            if (!string.IsNullOrWhiteSpace(friendKey) &&
                _selectedFriendGamesByFriendKey.TryGetValue(friendKey, out var games))
            {
                return games;
            }

            return Array.Empty<FriendGameSummaryItem>();
        }

        public FriendSummaryItem FindFriend(string friendScopeKey)
        {
            if (IsAllScope(friendScopeKey))
            {
                return null;
            }

            return _friends.FirstOrDefault(friend =>
                string.Equals(GetFriendScopeKey(friend), friendScopeKey, StringComparison.OrdinalIgnoreCase));
        }

        public FriendGameSummaryItem FindGame(string gameScopeKey)
        {
            if (IsAllScope(gameScopeKey))
            {
                return null;
            }

            return _aggregateGames.FirstOrDefault(game =>
                string.Equals(GetGameScopeKey(game), gameScopeKey, StringComparison.OrdinalIgnoreCase));
        }

        // True when this friend+game pair has anything to show: unlocked achievement rows, or an
        // ownership link. The cache enforces that a provider-only game's ownership link only exists
        // once the friend has confirmed unlocks (refresh probe + schema v16 cleanup), so ownership
        // presence is sufficient here — the display layer needs no owned/unowned gating.
        public bool HasFriendGamePairData(FriendSummaryItem friend, FriendGameSummaryItem game)
        {
            if (friend == null || game == null)
            {
                return false;
            }

            var key = BuildFriendGameUnlockKey(
                friend.IsMergedFriend ? MergedProviderKey : friend.ProviderKey,
                friend.IsMergedFriend ? friend.MergedFriendId : friend.ExternalUserId,
                game.ProviderGameKey,
                game.AppId,
                game.PlayniteGameId);
            return !string.IsNullOrWhiteSpace(key) &&
                   (_friendGameUnlockKeys.Contains(key) || _friendGameOwnershipKeys.Contains(key));
        }

        public static bool IsAllScope(string scopeKey)
        {
            return string.IsNullOrWhiteSpace(scopeKey) ||
                   string.Equals(scopeKey, AllScopeKey, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetFriendScopeKey(FriendSummaryItem friend)
        {
            if (!string.IsNullOrWhiteSpace(friend?.MergedFriendId))
            {
                return BuildFriendKey(MergedProviderKey, friend.MergedFriendId) ?? AllScopeKey;
            }

            return BuildFriendKey(friend?.ProviderKey, friend?.ExternalUserId) ?? AllScopeKey;
        }

        public static string GetFriendScopeKey(FriendAchievementDisplayItem achievement)
        {
            if (!string.IsNullOrWhiteSpace(achievement?.FriendGroupId))
            {
                return BuildFriendKey(MergedProviderKey, achievement.FriendGroupId) ?? AllScopeKey;
            }

            return BuildFriendKey(achievement?.ProviderKey, achievement?.FriendExternalUserId) ?? AllScopeKey;
        }

        public static string GetGameScopeKey(FriendGameSummaryItem game)
        {
            return BuildGameUnlockKey(game?.ProviderKey, game?.ProviderGameKey, game?.AppId ?? 0, game?.PlayniteGameId) ?? AllScopeKey;
        }

        public static string GetFriendScopeKey(FriendGameLinkItem link)
        {
            if (!string.IsNullOrWhiteSpace(link?.FriendGroupId))
            {
                return BuildFriendKey(MergedProviderKey, link.FriendGroupId) ?? AllScopeKey;
            }

            return BuildFriendKey(link?.ProviderKey, link?.ExternalUserId) ?? AllScopeKey;
        }

        public static string BuildFriendKey(string providerKey, string externalUserId)
        {
            if (string.IsNullOrWhiteSpace(externalUserId))
            {
                return null;
            }

            var provider = string.IsNullOrWhiteSpace(providerKey)
                ? string.Empty
                : providerKey.Trim().ToLowerInvariant();
            return provider + "|" + externalUserId.Trim().ToLowerInvariant();
        }

        public static string BuildFriendGameUnlockKey(
            string providerKey,
            string externalUserId,
            string providerGameKey,
            int appId,
            Guid? playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(externalUserId))
            {
                return null;
            }

            var gameKey = BuildGameUnlockKey(providerKey, providerGameKey, appId, playniteGameId);
            return string.IsNullOrWhiteSpace(gameKey)
                ? null
                : externalUserId.Trim().ToLowerInvariant() + "|" + gameKey;
        }

        public static string BuildGameUnlockKey(string providerKey, string providerGameKey, int appId, Guid? playniteGameId)
        {
            var provider = string.IsNullOrWhiteSpace(providerKey)
                ? string.Empty
                : providerKey.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(providerGameKey))
            {
                return provider + "|key:" + providerGameKey.Trim().ToLowerInvariant();
            }

            if (appId > 0)
            {
                return provider + "|app:" + appId.ToString("D", CultureInfo.InvariantCulture);
            }

            return playniteGameId.HasValue && playniteGameId.Value != Guid.Empty
                ? provider + "|playnite:" + playniteGameId.Value.ToString("D")
                : null;
        }

        public static bool IsSameFriend(FriendSummaryItem left, FriendSummaryItem right)
        {
            return left != null &&
                   right != null &&
                   string.Equals(GetFriendScopeKey(left), GetFriendScopeKey(right), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSameFriend(FriendAchievementDisplayItem achievement, FriendSummaryItem friend)
        {
            return achievement != null &&
                   friend != null &&
                   string.Equals(GetFriendScopeKey(achievement), GetFriendScopeKey(friend), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSameFriend(FriendGameLinkItem link, FriendSummaryItem friend)
        {
            return link != null &&
                   friend != null &&
                   string.Equals(GetFriendScopeKey(link), GetFriendScopeKey(friend), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSameGame(FriendGameSummaryItem left, FriendGameSummaryItem right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (!string.Equals(left.ProviderKey, right.ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (left.AppId > 0 && right.AppId > 0)
            {
                return left.AppId == right.AppId;
            }

            if (!string.IsNullOrWhiteSpace(left.ProviderGameKey) &&
                !string.IsNullOrWhiteSpace(right.ProviderGameKey))
            {
                return string.Equals(left.ProviderGameKey.Trim(), right.ProviderGameKey.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return left.PlayniteGameId.HasValue &&
                   right.PlayniteGameId.HasValue &&
                   left.PlayniteGameId.Value == right.PlayniteGameId.Value;
        }

        public static bool IsSameGame(FriendAchievementDisplayItem achievement, FriendGameSummaryItem game)
        {
            if (achievement == null || game == null)
            {
                return false;
            }

            if (!string.Equals(achievement.ProviderKey, game.ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (achievement.AppId > 0 && game.AppId > 0)
            {
                return achievement.AppId == game.AppId;
            }

            if (!string.IsNullOrWhiteSpace(achievement.ProviderGameKey) &&
                !string.IsNullOrWhiteSpace(game.ProviderGameKey))
            {
                return string.Equals(achievement.ProviderGameKey.Trim(), game.ProviderGameKey.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return achievement.PlayniteGameId.HasValue &&
                   game.PlayniteGameId.HasValue &&
                   achievement.PlayniteGameId.Value == game.PlayniteGameId.Value;
        }

        public static bool IsSameGame(FriendGameLinkItem link, FriendGameSummaryItem game)
        {
            if (link == null || game == null)
            {
                return false;
            }

            if (!string.Equals(link.ProviderKey, game.ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (link.AppId > 0 && game.AppId > 0)
            {
                return link.AppId == game.AppId;
            }

            if (!string.IsNullOrWhiteSpace(link.ProviderGameKey) &&
                !string.IsNullOrWhiteSpace(game.ProviderGameKey))
            {
                return string.Equals(link.ProviderGameKey.Trim(), game.ProviderGameKey.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return link.PlayniteGameId.HasValue &&
                   game.PlayniteGameId.HasValue &&
                   link.PlayniteGameId.Value == game.PlayniteGameId.Value;
        }

        private static FriendsOverviewData ApplyMergeGroups(FriendsOverviewData data, PersistedSettings settings)
        {
            var groups = settings?.GetFriendMergeGroups() ?? new List<FriendMergeGroup>();
            if (groups.Count == 0)
            {
                ApplyIndividualNicknames(data, settings);
                return data;
            }

            var groupByAccount = new Dictionary<string, FriendMergeGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                foreach (var member in group.Members ?? new List<FriendAccountRef>())
                {
                    if (!string.IsNullOrWhiteSpace(member?.Key) && !groupByAccount.ContainsKey(member.Key))
                    {
                        groupByAccount[member.Key] = group;
                    }
                }
            }

            if (groupByAccount.Count == 0)
            {
                ApplyIndividualNicknames(data, settings);
                return data;
            }

            var settingsByAccount = (settings?.GetFriendSettings(includeIgnored: true) ?? new List<FriendSettingsEntry>())
                .GroupBy(entry => FriendAccountRef.BuildKey(entry.ProviderKey, entry.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var originalFriends = data.Friends ?? new List<FriendSummaryItem>();
            var friendByAccount = originalFriends
                .Where(friend => friend != null)
                .GroupBy(friend => FriendAccountRef.BuildKey(friend.ProviderKey, friend.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var achievement in EnumerateAchievements(data))
            {
                var accountKey = FriendAccountRef.BuildKey(achievement?.ProviderKey, achievement?.FriendExternalUserId);
                if (string.IsNullOrWhiteSpace(accountKey))
                {
                    continue;
                }

                if (groupByAccount.TryGetValue(accountKey, out var group))
                {
                    achievement.FriendGroupId = group.Id;
                    achievement.FriendName = ResolveMergedFriendName(group, friendByAccount, settingsByAccount);
                    achievement.FriendAvatarPath = ResolveMergedFriendAvatar(group, friendByAccount);
                }
                else if (settingsByAccount.TryGetValue(accountKey, out var setting) &&
                         !string.IsNullOrWhiteSpace(setting.Nickname))
                {
                    achievement.FriendName = setting.Nickname;
                }
            }

            foreach (var link in data.FriendGameLinks ?? new List<FriendGameLinkItem>())
            {
                var accountKey = FriendAccountRef.BuildKey(link?.ProviderKey, link?.ExternalUserId);
                if (!string.IsNullOrWhiteSpace(accountKey) &&
                    groupByAccount.TryGetValue(accountKey, out var group))
                {
                    link.FriendGroupId = group.Id;
                }
            }

            var groupedAccountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mergedFriends = new List<FriendSummaryItem>();
            foreach (var group in groups)
            {
                var members = (group.Members ?? new List<FriendAccountRef>())
                    .Select(member => friendByAccount.TryGetValue(member.Key, out var friend) ? friend : null)
                    .Where(friend => friend != null)
                    .ToList();
                if (members.Count == 0)
                {
                    continue;
                }

                foreach (var member in group.Members ?? new List<FriendAccountRef>())
                {
                    if (!string.IsNullOrWhiteSpace(member?.Key))
                    {
                        groupedAccountKeys.Add(member.Key);
                    }
                }

                mergedFriends.Add(BuildMergedFriendSummary(group, members, settingsByAccount));
            }

            var unmergedFriends = originalFriends
                .Where(friend => friend != null && !groupedAccountKeys.Contains(FriendAccountRef.BuildKey(friend.ProviderKey, friend.ExternalUserId)))
                .Select(friend => ApplyIndividualNickname(friend, settingsByAccount))
                .ToList();

            data.Friends = mergedFriends
                .Concat(unmergedFriends)
                .OrderByDescending(friend => friend.LastUnlockUtc ?? DateTime.MinValue)
                .ThenBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return data;
        }

        private static void ApplyIndividualNicknames(FriendsOverviewData data, PersistedSettings settings)
        {
            var settingsByAccount = (settings?.GetFriendSettings(includeIgnored: true) ?? new List<FriendSettingsEntry>())
                .GroupBy(entry => FriendAccountRef.BuildKey(entry.ProviderKey, entry.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            data.Friends = (data.Friends ?? new List<FriendSummaryItem>())
                .Select(friend => ApplyIndividualNickname(friend, settingsByAccount))
                .ToList();

            foreach (var achievement in EnumerateAchievements(data))
            {
                var accountKey = FriendAccountRef.BuildKey(achievement?.ProviderKey, achievement?.FriendExternalUserId);
                if (!string.IsNullOrWhiteSpace(accountKey) &&
                    settingsByAccount.TryGetValue(accountKey, out var setting) &&
                    !string.IsNullOrWhiteSpace(setting.Nickname))
                {
                    achievement.FriendName = setting.Nickname;
                }
            }
        }

        private static FriendSummaryItem ApplyIndividualNickname(
            FriendSummaryItem friend,
            Dictionary<string, FriendSettingsEntry> settingsByAccount)
        {
            if (friend == null)
            {
                return null;
            }

            var accountKey = FriendAccountRef.BuildKey(friend.ProviderKey, friend.ExternalUserId);
            if (!string.IsNullOrWhiteSpace(accountKey) &&
                settingsByAccount != null &&
                settingsByAccount.TryGetValue(accountKey, out var setting) &&
                !string.IsNullOrWhiteSpace(setting.Nickname))
            {
                friend.DisplayName = setting.Nickname;
            }

            friend.MemberAccounts = new List<FriendAccountRef>
            {
                FriendAccountRef.From(friend.ProviderKey, friend.ExternalUserId)
            };
            friend.MemberProviderKeys = new List<string> { friend.ProviderKey };
            return friend;
        }

        private static FriendSummaryItem BuildMergedFriendSummary(
            FriendMergeGroup group,
            IReadOnlyList<FriendSummaryItem> members,
            Dictionary<string, FriendSettingsEntry> settingsByAccount)
        {
            var avatar = ResolveMergedFriendAvatar(group, members
                .GroupBy(member => FriendAccountRef.BuildKey(member.ProviderKey, member.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(memberGroup => memberGroup.Key, memberGroup => memberGroup.First(), StringComparer.OrdinalIgnoreCase));
            var displayName = ResolveMergedFriendName(group, members
                .GroupBy(member => FriendAccountRef.BuildKey(member.ProviderKey, member.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(memberGroup => memberGroup.Key, memberGroup => memberGroup.First(), StringComparer.OrdinalIgnoreCase),
                settingsByAccount);

            return new FriendSummaryItem
            {
                ProviderKey = MergedProviderKey,
                ExternalUserId = group.Id,
                MergedFriendId = group.Id,
                DisplayName = displayName,
                AvatarPath = avatar,
                SharedGamesCount = members.Sum(member => Math.Max(0, member.SharedGamesCount)),
                GamesWithUnlocksCount = members.Sum(member => Math.Max(0, member.GamesWithUnlocksCount)),
                CompletedGamesCount = members.Sum(member => Math.Max(0, member.CompletedGamesCount)),
                UnlockedAchievementsCount = members.Sum(member => Math.Max(0, member.UnlockedAchievementsCount)),
                CollectionScore = members.Sum(member => Math.Max(0, member.CollectionScore)),
                PrestigeScore = members.Sum(member => Math.Max(0, member.PrestigeScore)),
                CollectionLevel = members.Select(member => Math.Max(0, member.CollectionLevel)).DefaultIfEmpty(0).Max(),
                PrestigeLevel = members.Select(member => Math.Max(0, member.PrestigeLevel)).DefaultIfEmpty(0).Max(),
                RecentUnlockCount = members.Sum(member => Math.Max(0, member.RecentUnlockCount)),
                CommonCount = members.Sum(member => Math.Max(0, member.CommonCount)),
                UncommonCount = members.Sum(member => Math.Max(0, member.UncommonCount)),
                RareCount = members.Sum(member => Math.Max(0, member.RareCount)),
                UltraRareCount = members.Sum(member => Math.Max(0, member.UltraRareCount)),
                TrophyPlatinumCount = members.Sum(member => Math.Max(0, member.TrophyPlatinumCount)),
                TrophyGoldCount = members.Sum(member => Math.Max(0, member.TrophyGoldCount)),
                TrophySilverCount = members.Sum(member => Math.Max(0, member.TrophySilverCount)),
                TrophyBronzeCount = members.Sum(member => Math.Max(0, member.TrophyBronzeCount)),
                LastUnlockUtc = members.Select(member => member.LastUnlockUtc).Where(value => value.HasValue).DefaultIfEmpty().Max(),
                LastRefreshedUtc = members.Select(member => member.LastRefreshedUtc).Where(value => value.HasValue).DefaultIfEmpty().Max(),
                TotalPlaytimeMinutes = members.Sum(member => Math.Max(0, member.TotalPlaytimeMinutes)),
                MemberAccounts = (group.Members ?? new List<FriendAccountRef>()).Select(member => member.Clone().Normalize()).ToList(),
                MemberProviderKeys = (group.Members ?? new List<FriendAccountRef>())
                    .Select(member => member.ProviderKey)
                    .Where(provider => !string.IsNullOrWhiteSpace(provider))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        private static string ResolveMergedFriendName(
            FriendMergeGroup group,
            Dictionary<string, FriendSummaryItem> friendByAccount,
            Dictionary<string, FriendSettingsEntry> settingsByAccount)
        {
            if (!string.IsNullOrWhiteSpace(group?.Nickname))
            {
                return group.Nickname;
            }

            foreach (var member in group?.Members ?? new List<FriendAccountRef>())
            {
                if (settingsByAccount != null &&
                    settingsByAccount.TryGetValue(member.Key, out var setting) &&
                    !string.IsNullOrWhiteSpace(setting.Nickname))
                {
                    return setting.Nickname;
                }
            }

            foreach (var member in group?.Members ?? new List<FriendAccountRef>())
            {
                if (friendByAccount != null &&
                    friendByAccount.TryGetValue(member.Key, out var friend) &&
                    !string.IsNullOrWhiteSpace(friend.DisplayName))
                {
                    return friend.DisplayName;
                }
            }

            return group?.Members?.FirstOrDefault()?.ExternalUserId ?? "Merged Friend";
        }

        private static string ResolveMergedFriendAvatar(
            FriendMergeGroup group,
            Dictionary<string, FriendSummaryItem> friendByAccount)
        {
            if (friendByAccount == null || friendByAccount.Count == 0)
            {
                return null;
            }

            var avatarAccountKey = group?.AvatarAccount?.Key;
            if (!string.IsNullOrWhiteSpace(avatarAccountKey) &&
                friendByAccount.TryGetValue(avatarAccountKey, out var avatarFriend) &&
                !string.IsNullOrWhiteSpace(avatarFriend.AvatarPath))
            {
                return avatarFriend.AvatarPath;
            }

            return (group?.Members ?? new List<FriendAccountRef>())
                .Select(member => friendByAccount.TryGetValue(member.Key, out var friend) ? friend?.AvatarPath : null)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ??
                   friendByAccount.Values.Select(friend => friend?.AvatarPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }

        private static IEnumerable<FriendAchievementDisplayItem> EnumerateAchievements(FriendsOverviewData data)
        {
            return (data?.RecentUnlocks ?? new List<FriendAchievementDisplayItem>())
                .Concat(data?.AllAchievements ?? new List<FriendAchievementDisplayItem>())
                .Concat(data?.AllUnlockedAchievements ?? new List<FriendAchievementDisplayItem>())
                .Where(achievement => achievement != null)
                .GroupBy(achievement => achievement, ReferenceEqualityComparer<FriendAchievementDisplayItem>.Instance)
                .Select(group => group.Key);
        }

        private static string GetFriendProviderKey(FriendAchievementDisplayItem achievement) =>
            string.IsNullOrWhiteSpace(achievement?.FriendGroupId) ? achievement?.ProviderKey : MergedProviderKey;

        private static string GetFriendExternalUserId(FriendAchievementDisplayItem achievement) =>
            string.IsNullOrWhiteSpace(achievement?.FriendGroupId) ? achievement?.FriendExternalUserId : achievement.FriendGroupId;

        private static string GetFriendProviderKey(FriendGameLinkItem link) =>
            string.IsNullOrWhiteSpace(link?.FriendGroupId) ? link?.ProviderKey : MergedProviderKey;

        private static string GetFriendExternalUserId(FriendGameLinkItem link) =>
            string.IsNullOrWhiteSpace(link?.FriendGroupId) ? link?.ExternalUserId : link.FriendGroupId;

        private Dictionary<string, List<FriendGameSummaryItem>> BuildSelectedFriendGameSummaries()
        {
            var next = new Dictionary<string, List<FriendGameSummaryItem>>(StringComparer.OrdinalIgnoreCase);
            var gamesByKey = new Dictionary<string, FriendGameSummaryItem>(StringComparer.OrdinalIgnoreCase);
            var linksByFriendGameKey = new Dictionary<string, FriendGameLinkItem>(StringComparer.OrdinalIgnoreCase);
            var emittedFriendGameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var game in _aggregateGames)
            {
                var gameKey = BuildGameUnlockKey(game?.ProviderKey, game?.ProviderGameKey, game?.AppId ?? 0, game?.PlayniteGameId);
                if (!string.IsNullOrWhiteSpace(gameKey) && !gamesByKey.ContainsKey(gameKey))
                {
                    gamesByKey[gameKey] = game;
                }
            }

            foreach (var link in _friendGameLinks)
            {
                var friendGameKey = BuildFriendGameUnlockKey(
                    GetFriendProviderKey(link),
                    GetFriendExternalUserId(link),
                    link?.ProviderGameKey,
                    link?.AppId ?? 0,
                    link?.PlayniteGameId);
                if (string.IsNullOrWhiteSpace(friendGameKey))
                {
                    continue;
                }

                if (!linksByFriendGameKey.ContainsKey(friendGameKey))
                {
                    linksByFriendGameKey[friendGameKey] = link;
                }
            }

            foreach (var group in _allAchievements
                .Where(achievement => achievement != null)
                .GroupBy(
                    achievement => BuildFriendGameUnlockKey(
                        GetFriendProviderKey(achievement),
                        GetFriendExternalUserId(achievement),
                        achievement.ProviderGameKey,
                        achievement.AppId,
                        achievement.PlayniteGameId),
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key)))
            {
                var first = group.FirstOrDefault();
                var friendKey = GetFriendScopeKey(first);
                var gameKey = BuildGameUnlockKey(first?.ProviderKey, first?.ProviderGameKey, first?.AppId ?? 0, first?.PlayniteGameId);
                if (string.IsNullOrWhiteSpace(friendKey) ||
                    string.IsNullOrWhiteSpace(gameKey) ||
                    !gamesByKey.TryGetValue(gameKey, out var baseGame))
                {
                    continue;
                }

                linksByFriendGameKey.TryGetValue(group.Key, out var link);
                var row = BuildSelectedFriendGameSummary(
                    baseGame,
                    group,
                    link);
                if (!next.TryGetValue(friendKey, out var rows))
                {
                    rows = new List<FriendGameSummaryItem>();
                    next[friendKey] = rows;
                }

                rows.Add(row);
                emittedFriendGameKeys.Add(group.Key);
            }

            // Ownership pass: a friend's games with no achievement rows yet (shared library games the
            // friend never played, or games scraped for ownership only) still get a per-friend row.
            // The cache guarantees provider-only links only exist once the friend has unlocks, so no
            // owned/unowned check is needed.
            foreach (var pair in linksByFriendGameKey)
            {
                if (emittedFriendGameKeys.Contains(pair.Key))
                {
                    continue;
                }

                var link = pair.Value;
                var friendKey = GetFriendScopeKey(link);
                var gameKey = BuildGameUnlockKey(link?.ProviderKey, link?.ProviderGameKey, link?.AppId ?? 0, link?.PlayniteGameId);
                if (string.IsNullOrWhiteSpace(friendKey) ||
                    string.IsNullOrWhiteSpace(gameKey) ||
                    !gamesByKey.TryGetValue(gameKey, out var baseGame))
                {
                    continue;
                }

                var row = BuildSelectedFriendGameSummary(
                    baseGame,
                    Enumerable.Empty<FriendAchievementDisplayItem>(),
                    link);
                if (!next.TryGetValue(friendKey, out var rows))
                {
                    rows = new List<FriendGameSummaryItem>();
                    next[friendKey] = rows;
                }

                rows.Add(row);
            }

            return next;
        }

        private HashSet<string> BuildFriendGameUnlockKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in _allAchievements)
            {
                // Locked rows exist for pair comparison views; only genuine unlocks count here.
                if (achievement?.Unlocked != true)
                {
                    continue;
                }

                var friendGameKey = BuildFriendGameUnlockKey(
                    GetFriendProviderKey(achievement),
                    GetFriendExternalUserId(achievement),
                    achievement.ProviderGameKey,
                    achievement.AppId,
                    achievement.PlayniteGameId);
                if (!string.IsNullOrWhiteSpace(friendGameKey))
                {
                    keys.Add(friendGameKey);
                }
            }

            return keys;
        }

        private HashSet<string> BuildFriendGameOwnershipKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in _friendGameLinks)
            {
                var friendGameKey = BuildFriendGameUnlockKey(
                    GetFriendProviderKey(link),
                    GetFriendExternalUserId(link),
                    link?.ProviderGameKey,
                    link?.AppId ?? 0,
                    link?.PlayniteGameId);
                if (!string.IsNullOrWhiteSpace(friendGameKey))
                {
                    keys.Add(friendGameKey);
                }
            }

            return keys;
        }

        private static FriendGameSummaryItem BuildSelectedFriendGameSummary(
            FriendGameSummaryItem source,
            IEnumerable<FriendAchievementDisplayItem> achievements,
            FriendGameLinkItem link)
        {
            var allAchievements = (achievements ?? Enumerable.Empty<FriendAchievementDisplayItem>())
                .Where(achievement => achievement != null)
                .ToList();
            var unlocked = allAchievements
                .Where(achievement => achievement.Unlocked)
                .GroupBy(achievement => achievement.ApiName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(achievement => achievement.UnlockTimeUtc ?? DateTime.MinValue)
                    .First())
                .ToList();

            var playtimeMinutes = Math.Max(0L, link?.PlaytimeForeverMinutes ?? 0L);
            var item = new FriendGameSummaryItem
            {
                ProviderKey = source.ProviderKey,
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
                LastPlayed = link?.LastPlayedUtc,
                TotalAchievements = source.TotalAchievements > 0
                    ? source.TotalAchievements
                    : allAchievements
                        .GroupBy(achievement => achievement.ApiName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .Count(),
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
                FriendCount = 1,
                FriendsWithUnlocksCount = unlocked.Count > 0 ? 1 : 0,
                FriendUnlockedAchievementsCount = unlocked.Count,
                UniqueFriendUnlockedAchievementsCount = unlocked.Count,
                LastFriendUnlockUtc = unlocked
                    .Select(achievement => achievement.UnlockTimeUtc)
                    .Where(unlockTime => unlockTime.HasValue)
                    .DefaultIfEmpty()
                    .Max(),
                TotalFriendPlaytimeMinutes = playtimeMinutes,
                AverageFriendPlaytimeMinutes = playtimeMinutes,
                LastFriendPlayedUtc = link?.LastPlayedUtc
            };

            var stats = AchievementStatsAccumulator.FromDisplayItems(
                unlocked,
                treatItemsAsUnlocked: true);
            item.CollectionScore = stats.CollectionScore;
            item.PrestigeScore = stats.PrestigeScore;
            item.Points = stats.Points;
            item.CommonCount = stats.CommonCount;
            item.UncommonCount = stats.UncommonCount;
            item.RareCount = stats.RareCount;
            item.UltraRareCount = stats.UltraRareCount;
            item.TrophyPlatinumCount = stats.TrophyPlatinumCount;
            item.TrophyGoldCount = stats.TrophyGoldCount;
            item.TrophySilverCount = stats.TrophySilverCount;
            item.TrophyBronzeCount = stats.TrophyBronzeCount;

            item.IsCompleted = item.TotalAchievements > 0 &&
                               item.UnlockedAchievements >= item.TotalAchievements;
            return item;
        }

        private static List<FriendAchievementDisplayItem> ResolveAllAchievements(FriendsOverviewData data)
        {
            var all = data?.AllAchievements;
            if (all != null && all.Count > 0)
            {
                return all;
            }

            return data?.AllUnlockedAchievements ?? new List<FriendAchievementDisplayItem>();
        }

        private static ulong ToPlaytimeSeconds(long minutes)
        {
            var normalized = Math.Max(0L, minutes);
            return normalized > (long)(ulong.MaxValue / 60UL)
                ? ulong.MaxValue
                : (ulong)normalized * 60UL;
        }

        private static void AccumulateRarity(RarityTier rarity, GameSummaryItem item)
        {
            if (item == null)
            {
                return;
            }

            switch (rarity)
            {
                case RarityTier.UltraRare:
                    item.UltraRareCount++;
                    break;
                case RarityTier.Rare:
                    item.RareCount++;
                    break;
                case RarityTier.Uncommon:
                    item.UncommonCount++;
                    break;
                default:
                    item.CommonCount++;
                    break;
            }
        }

        private static void AccumulateTrophy(string trophyType, GameSummaryItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(trophyType))
            {
                return;
            }

            if (string.Equals(trophyType, "platinum", StringComparison.OrdinalIgnoreCase))
            {
                item.TrophyPlatinumCount++;
            }
            else if (string.Equals(trophyType, "gold", StringComparison.OrdinalIgnoreCase))
            {
                item.TrophyGoldCount++;
            }
            else if (string.Equals(trophyType, "silver", StringComparison.OrdinalIgnoreCase))
            {
                item.TrophySilverCount++;
            }
            else if (string.Equals(trophyType, "bronze", StringComparison.OrdinalIgnoreCase))
            {
                item.TrophyBronzeCount++;
            }
        }

        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static ReferenceEqualityComparer<T> Instance { get; } = new ReferenceEqualityComparer<T>();

            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }
    }
}
