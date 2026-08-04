using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Toggles the persisted favorite flag for a friend row. Shared by the Friends Overview
    /// and View Friends' Achievements row context menus so the settings write, persistence,
    /// and theme update happen through one path.
    /// </summary>
    internal static class FriendFavoriteToggleHelper
    {
        /// <summary>
        /// Resolves the provider accounts a friend-row action applies to. Merged friends
        /// yield every member account; single-account rows yield one ref.
        /// </summary>
        public static IEnumerable<FriendAccountRef> GetConfigurableFriendAccounts(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                yield break;
            }

            if (friend.IsMergedFriend)
            {
                foreach (var account in friend.MemberAccounts ?? new List<FriendAccountRef>())
                {
                    if (!string.IsNullOrWhiteSpace(account?.ProviderKey) &&
                        !string.IsNullOrWhiteSpace(account.ExternalUserId))
                    {
                        yield return account;
                    }
                }

                yield break;
            }

            if (!string.IsNullOrWhiteSpace(friend.ProviderKey) &&
                !string.IsNullOrWhiteSpace(friend.ExternalUserId))
            {
                yield return FriendAccountRef.From(friend.ProviderKey, friend.ExternalUserId);
            }
        }

        public static bool HasConfigurableAccounts(FriendSummaryItem friend)
        {
            return GetConfigurableFriendAccounts(friend).Any();
        }

        /// <summary>
        /// Sets IsFavorite to the opposite of the row's current state on every configurable
        /// account (merged friends toggle as a unit), persists settings, and requests a theme
        /// update. Returns true when the toggle was applied so callers know to reload; the
        /// projected row state only updates on a view-model reload.
        /// </summary>
        public static bool ToggleFavorite(FriendSummaryItem friend, ILogger logger)
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            var persisted = plugin?.Settings?.Persisted;
            if (friend == null || plugin == null || persisted == null)
            {
                return false;
            }

            var accounts = GetConfigurableFriendAccounts(friend).ToList();
            if (accounts.Count == 0)
            {
                return false;
            }

            var target = !friend.IsFavorite;
            try
            {
                foreach (var account in accounts)
                {
                    // AddOrUpdateFriend rather than SetFriendFavorite so auto-discovered
                    // friends without a settings entry can still be favorited. DisplayName is
                    // omitted: friend.DisplayName is the resolved display value (nickname/mode
                    // formatting applied) and must not overwrite the stored provider name.
                    var entry = persisted.AddOrUpdateFriend(
                        account.ProviderKey,
                        account.ExternalUserId,
                        null,
                        friend.AvatarPath,
                        null,
                        FriendSettingsSource.AutoDiscovered);
                    if (entry != null)
                    {
                        entry.IsFavorite = target;
                    }
                }

                plugin.PersistSettingsForUi();
                plugin.ThemeIntegrationService?.RequestUpdate(null, forceRefresh: true);
                return true;
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"Failed to toggle favorite for friend {friend.ProviderKey}/{friend.ExternalUserId}.");
                return false;
            }
        }
    }
}
