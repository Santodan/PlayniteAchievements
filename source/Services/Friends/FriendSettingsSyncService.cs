using Playnite.SDK;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Friends
{
    internal static class FriendSettingsSyncService
    {
        public static int SyncConfiguredFriendsToCache(
            PersistedSettings settings,
            IFriendCacheManager friendCache,
            ILogger logger = null,
            string providerKey = null)
        {
            if (settings == null || friendCache == null)
            {
                return 0;
            }

            var providerKeys = settings.GetFriendSettings(providerKey)
                .Select(friend => friend.ProviderKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!string.IsNullOrWhiteSpace(providerKey) &&
                !providerKeys.Contains(providerKey.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                providerKeys.Add(providerKey.Trim());
            }

            var written = 0;
            foreach (var key in providerKeys)
            {
                try
                {
                    var identities = settings.GetActiveFriendIdentities(key);
                    var result = friendCache.SaveFriendList(key, identities);
                    if (result?.Success == true)
                    {
                        written += result.WrittenCount;
                    }
                    else
                    {
                        logger?.Warn($"Failed to sync configured friends for {key}: {result?.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"Failed to sync configured friends for {key}.");
                }
            }

            return written;
        }

        public static bool MergeCachedFriends(
            PersistedSettings settings,
            IFriendCacheManager friendCache,
            string providerKey,
            FriendSettingsSource source = FriendSettingsSource.AutoDiscovered)
        {
            if (settings == null || friendCache == null || string.IsNullOrWhiteSpace(providerKey))
            {
                return false;
            }

            var changed = false;
            foreach (var identity in friendCache.LoadFriendIdentities(providerKey) ?? new List<FriendIdentity>())
            {
                var existing = settings.GetFriendSetting(identity.ProviderKey, identity.ExternalUserId);
                settings.AddOrUpdateFriend(identity, source);
                changed |= existing == null;
            }

            return changed;
        }
    }
}
