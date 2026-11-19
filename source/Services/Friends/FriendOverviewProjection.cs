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
        private readonly List<FriendGameLinkItem> _friendGameLinks;

        /// <summary>The friends merged and named as the settings say, the games and the ownership links.</summary>
        public FriendOverviewProjection(FriendsOverviewData data, PersistedSettings settings = null)
        {
            data = ApplyMergeGroups(data ?? new FriendsOverviewData(), settings);
            _friends = data.Friends ?? new List<FriendSummaryItem>();
            _aggregateGames = data.Games ?? new List<FriendGameSummaryItem>();
            _friendGameLinks = data.FriendGameLinks ?? new List<FriendGameLinkItem>();
        }

        public IReadOnlyList<FriendSummaryItem> Friends => _friends;

        public IReadOnlyList<FriendGameSummaryItem> AggregateGames => _aggregateGames;

        public IReadOnlyList<FriendGameLinkItem> FriendGameLinks => _friendGameLinks;

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

        public static bool IsAllScope(string scopeKey)
        {
            return string.IsNullOrWhiteSpace(scopeKey) ||
                   string.Equals(scopeKey, AllScopeKey, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetFriendScopeKey(FriendSummaryItem friend) =>
            GetFriendScopeKey(friend?.MergedFriendId, friend?.ProviderKey, friend?.ExternalUserId);

        public static string GetFriendScopeKey(FriendAchievementDisplayItem achievement) =>
            GetFriendScopeKey(achievement?.FriendGroupId, achievement?.ProviderKey, achievement?.FriendExternalUserId);

        public static string GetGameScopeKey(FriendGameSummaryItem game)
        {
            return BuildGameUnlockKey(game?.ProviderKey, game?.ProviderGameKey, game?.AppId ?? 0, game?.PlayniteGameId) ?? AllScopeKey;
        }

        // A merged-friend group id wins over the row's own provider identity.
        private static string GetFriendScopeKey(string groupId, string providerKey, string externalUserId)
        {
            return (!string.IsNullOrWhiteSpace(groupId)
                       ? BuildFriendKey(MergedProviderKey, groupId)
                       : BuildFriendKey(providerKey, externalUserId)) ??
                   AllScopeKey;
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

        public static bool IsSameFriend(FriendSummaryItem left, FriendSummaryItem right) =>
            left != null && right != null && IsSameFriendKey(GetFriendScopeKey(left), GetFriendScopeKey(right));

        public static bool IsSameFriend(FriendAchievementDisplayItem achievement, FriendSummaryItem friend) =>
            achievement != null && friend != null && IsSameFriendKey(GetFriendScopeKey(achievement), GetFriendScopeKey(friend));

        private static bool IsSameFriendKey(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        public static bool IsSameGame(FriendGameSummaryItem left, FriendGameSummaryItem right) =>
            left != null &&
            right != null &&
            IsSameGame(
                left.ProviderKey, left.AppId, left.ProviderGameKey, left.PlayniteGameId,
                right.ProviderKey, right.AppId, right.ProviderGameKey, right.PlayniteGameId);

        public static bool IsSameGame(FriendAchievementDisplayItem achievement, FriendGameSummaryItem game) =>
            achievement != null &&
            game != null &&
            IsSameGame(
                achievement.ProviderKey, achievement.AppId, achievement.ProviderGameKey, achievement.PlayniteGameId,
                game.ProviderKey, game.AppId, game.ProviderGameKey, game.PlayniteGameId);

        // Same provider, then the strongest identity both sides carry: app id, provider game
        // key, then Playnite game id.
        private static bool IsSameGame(
            string leftProviderKey,
            int leftAppId,
            string leftProviderGameKey,
            Guid? leftPlayniteGameId,
            string rightProviderKey,
            int rightAppId,
            string rightProviderGameKey,
            Guid? rightPlayniteGameId)
        {
            if (!string.Equals(leftProviderKey, rightProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (leftAppId > 0 && rightAppId > 0)
            {
                return leftAppId == rightAppId;
            }

            if (!string.IsNullOrWhiteSpace(leftProviderGameKey) &&
                !string.IsNullOrWhiteSpace(rightProviderGameKey))
            {
                return string.Equals(leftProviderGameKey.Trim(), rightProviderGameKey.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return leftPlayniteGameId.HasValue &&
                   rightPlayniteGameId.HasValue &&
                   leftPlayniteGameId.Value == rightPlayniteGameId.Value;
        }

        /// <summary>
        /// Runs a freshly loaded data set through the same merge-group and nickname stamping the
        /// snapshot projection applies (FriendGroupId, merged name/avatar/favorite), so friend
        /// scope keys computed from these rows match merged-friend selections. Mutates the rows in
        /// place and returns <c>data.AllAchievements</c>. When the load carries no friend
        /// summaries (the game-definition-scoped load), name and avatar resolution falls back to
        /// stubs built from the rows themselves.
        /// </summary>
        public static List<FriendAchievementDisplayItem> ApplyMergeIdentity(
            FriendsOverviewData data,
            PersistedSettings settings)
        {
            data = data ?? new FriendsOverviewData();
            if (data.Friends == null || data.Friends.Count == 0)
            {
                data.Friends = BuildFriendStubsFromAchievements(data.AllAchievements);
            }

            ApplyMergeGroups(data, settings);
            return data.AllAchievements ?? new List<FriendAchievementDisplayItem>();
        }

        private static List<FriendSummaryItem> BuildFriendStubsFromAchievements(
            IEnumerable<FriendAchievementDisplayItem> achievements)
        {
            return (achievements ?? Enumerable.Empty<FriendAchievementDisplayItem>())
                .Where(achievement => achievement != null)
                .GroupBy(
                    achievement => FriendAccountRef.BuildKey(achievement.ProviderKey, achievement.FriendExternalUserId),
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => new FriendSummaryItem
                {
                    ProviderKey = group.First().ProviderKey,
                    ExternalUserId = group.First().FriendExternalUserId,
                    DisplayName = group.Select(achievement => achievement.FriendName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                    AvatarPath = group.Select(achievement => achievement.FriendAvatarPath)
                        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                })
                .ToList();
        }

        private static FriendsOverviewData ApplyMergeGroups(FriendsOverviewData data, PersistedSettings settings)
        {
            var nameMode = settings?.FriendNameDisplayMode ?? FriendNameDisplayMode.PersonaAndNickname;
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
                    achievement.FriendName = ResolveMergedFriendName(group, friendByAccount, settingsByAccount, nameMode);
                    achievement.FriendAvatarPath = ResolveMergedFriendAvatar(group, friendByAccount);
                    achievement.FriendIsFavorite = IsMergeGroupFavorite(group, settingsByAccount);
                }
                else if (settingsByAccount.TryGetValue(accountKey, out var setting))
                {
                    achievement.FriendIsFavorite = setting.IsFavorite;
                    achievement.FriendName = FriendDisplayNameResolver.Resolve(
                        setting.Nickname,
                        !string.IsNullOrWhiteSpace(achievement.FriendName) ? achievement.FriendName : setting.DisplayName,
                        setting.ProviderNickname,
                        nameMode,
                        achievement.FriendExternalUserId);
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

                mergedFriends.Add(BuildMergedFriendSummary(group, members, settingsByAccount, nameMode));
            }

            var unmergedFriends = originalFriends
                .Where(friend => friend != null && !groupedAccountKeys.Contains(FriendAccountRef.BuildKey(friend.ProviderKey, friend.ExternalUserId)))
                .Select(friend => ApplyIndividualNickname(friend, settingsByAccount, nameMode))
                .ToList();

            data.Friends = mergedFriends
                .Concat(unmergedFriends)
                .OrderByDescending(friend => friend.IsFavorite)
                .ThenByDescending(friend => friend.LastUnlockUtc ?? DateTime.MinValue)
                .ThenBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return data;
        }

        private static void ApplyIndividualNicknames(FriendsOverviewData data, PersistedSettings settings)
        {
            var nameMode = settings?.FriendNameDisplayMode ?? FriendNameDisplayMode.PersonaAndNickname;
            var settingsByAccount = (settings?.GetFriendSettings(includeIgnored: true) ?? new List<FriendSettingsEntry>())
                .GroupBy(entry => FriendAccountRef.BuildKey(entry.ProviderKey, entry.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            data.Friends = (data.Friends ?? new List<FriendSummaryItem>())
                .Select(friend => ApplyIndividualNickname(friend, settingsByAccount, nameMode))
                .ToList();

            foreach (var achievement in EnumerateAchievements(data))
            {
                var accountKey = FriendAccountRef.BuildKey(achievement?.ProviderKey, achievement?.FriendExternalUserId);
                if (!string.IsNullOrWhiteSpace(accountKey) &&
                    settingsByAccount.TryGetValue(accountKey, out var setting))
                {
                    achievement.FriendName = FriendDisplayNameResolver.Resolve(
                        setting.Nickname,
                        !string.IsNullOrWhiteSpace(achievement.FriendName) ? achievement.FriendName : setting.DisplayName,
                        setting.ProviderNickname,
                        nameMode,
                        achievement.FriendExternalUserId);
                }
            }
        }

        private static FriendSummaryItem ApplyIndividualNickname(
            FriendSummaryItem friend,
            Dictionary<string, FriendSettingsEntry> settingsByAccount,
            FriendNameDisplayMode nameMode)
        {
            if (friend == null)
            {
                return null;
            }

            var accountKey = FriendAccountRef.BuildKey(friend.ProviderKey, friend.ExternalUserId);
            if (!string.IsNullOrWhiteSpace(accountKey) &&
                settingsByAccount != null &&
                settingsByAccount.TryGetValue(accountKey, out var setting))
            {
                friend.DisplayName = FriendDisplayNameResolver.Resolve(
                    setting.Nickname,
                    !string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.DisplayName : setting.DisplayName,
                    setting.ProviderNickname,
                    nameMode,
                    friend.ExternalUserId);

                friend.IsFavorite = setting.IsFavorite;
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
            Dictionary<string, FriendSettingsEntry> settingsByAccount,
            FriendNameDisplayMode nameMode)
        {
            var avatar = ResolveMergedFriendAvatar(group, members
                .GroupBy(member => FriendAccountRef.BuildKey(member.ProviderKey, member.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(memberGroup => memberGroup.Key, memberGroup => memberGroup.First(), StringComparer.OrdinalIgnoreCase));
            var displayName = ResolveMergedFriendName(group, members
                .GroupBy(member => FriendAccountRef.BuildKey(member.ProviderKey, member.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(memberGroup => memberGroup.Key, memberGroup => memberGroup.First(), StringComparer.OrdinalIgnoreCase),
                settingsByAccount,
                nameMode);

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
                IsFavorite = IsMergeGroupFavorite(group, settingsByAccount),
                MemberAccounts = (group.Members ?? new List<FriendAccountRef>()).Select(member => member.Clone().Normalize()).ToList(),
                MemberProviderKeys = (group.Members ?? new List<FriendAccountRef>())
                    .Select(member => member.ProviderKey)
                    .Where(provider => !string.IsNullOrWhiteSpace(provider))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        private static bool IsMergeGroupFavorite(
            FriendMergeGroup group,
            Dictionary<string, FriendSettingsEntry> settingsByAccount)
        {
            return (group?.Members ?? new List<FriendAccountRef>())
                .Any(member => member != null &&
                               settingsByAccount != null &&
                               settingsByAccount.TryGetValue(member.Key, out var entry) &&
                               entry.IsFavorite);
        }

        private static string ResolveMergedFriendName(
            FriendMergeGroup group,
            Dictionary<string, FriendSummaryItem> friendByAccount,
            Dictionary<string, FriendSettingsEntry> settingsByAccount,
            FriendNameDisplayMode nameMode)
        {
            if (!string.IsNullOrWhiteSpace(group?.Nickname))
            {
                return group.Nickname;
            }

            var members = MembersPrimaryFirst(group);
            foreach (var member in members)
            {
                if (settingsByAccount != null &&
                    settingsByAccount.TryGetValue(member.Key, out var setting) &&
                    !string.IsNullOrWhiteSpace(setting.Nickname))
                {
                    return setting.Nickname;
                }
            }

            foreach (var member in members)
            {
                if (friendByAccount != null &&
                    friendByAccount.TryGetValue(member.Key, out var friend) &&
                    !string.IsNullOrWhiteSpace(friend.DisplayName))
                {
                    FriendSettingsEntry setting = null;
                    settingsByAccount?.TryGetValue(member.Key, out setting);
                    return FriendDisplayNameResolver.Resolve(
                        null,
                        friend.DisplayName,
                        setting?.ProviderNickname,
                        nameMode,
                        member.ExternalUserId);
                }
            }

            return group?.Members?.FirstOrDefault()?.ExternalUserId ?? "Merged Friend";
        }

        // The group's primary (avatar) account also supplies the merged display name, so name
        // resolution walks it first; the stored member order breaks ties.
        private static List<FriendAccountRef> MembersPrimaryFirst(FriendMergeGroup group)
        {
            var members = group?.Members ?? new List<FriendAccountRef>();
            var primaryKey = group?.AvatarAccount?.Key;
            if (string.IsNullOrWhiteSpace(primaryKey))
            {
                return members;
            }

            return members
                .OrderByDescending(member => string.Equals(member?.Key, primaryKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
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

        private static IEnumerable<FriendAchievementDisplayItem> EnumerateAchievements(FriendsOverviewData data) =>
            (data?.AllAchievements ?? new List<FriendAchievementDisplayItem>()).Where(achievement => achievement != null);
    }
}
