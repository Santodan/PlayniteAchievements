using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Friends;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Projects active cached Steam friends plus persisted per-friend settings into settings table rows.
    /// </summary>
    public static class SteamFriendListBuilder
    {
        public static List<SteamFriendListItem> BuildItems(
            SteamSettings settings,
            IEnumerable<FriendIdentity> activeFriends)
        {
            if (settings == null)
            {
                return new List<SteamFriendListItem>();
            }

            var ignoredIds = settings.GetIgnoredSteamIds();
            var itemsById = new Dictionary<string, SteamFriendListItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var friend in activeFriends ?? Enumerable.Empty<FriendIdentity>())
            {
                var id = friend?.ExternalUserId?.Trim();
                if (string.IsNullOrWhiteSpace(id) || ignoredIds.Contains(id))
                {
                    continue;
                }

                itemsById[id] = new SteamFriendListItem
                {
                    SteamId = id,
                    DisplayName = string.IsNullOrWhiteSpace(friend.DisplayName) ? id : friend.DisplayName,
                    AvatarUrl = ResolveAvatarSource(friend.AvatarPath, friend.AvatarUrl),
                    IsIgnored = false
                };
            }

            foreach (var ignored in settings.IgnoredFriends ?? Enumerable.Empty<SteamIgnoredFriend>())
            {
                var id = ignored?.SteamId?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                itemsById[id] = new SteamFriendListItem
                {
                    SteamId = id,
                    DisplayName = string.IsNullOrWhiteSpace(ignored.DisplayName) ? id : ignored.DisplayName,
                    AvatarUrl = ignored.AvatarUrl,
                    IsIgnored = true
                };
            }

            return itemsById.Values
                .OrderBy(item => item.IsIgnored)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string ResolveAvatarSource(string avatarPath, string avatarUrl)
        {
            return !string.IsNullOrWhiteSpace(avatarPath)
                ? avatarPath
                : avatarUrl;
        }
    }
}
