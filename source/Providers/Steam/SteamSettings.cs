using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Steam-specific provider settings.
    /// </summary>
    public class SteamSettings : ProviderSettingsBase
    {
        private string _steamUserId;
        private bool _useSteamHuntersForCategories;
        private ObservableCollection<SteamIgnoredFriend> _ignoredFriends =
            new ObservableCollection<SteamIgnoredFriend>();

        /// <inheritdoc />
        public override string ProviderKey => "Steam";

        /// <summary>
        /// Gets or sets the last successfully probed Steam user ID.
        /// This is derived auth state, not user-editable configuration.
        /// </summary>
        public string SteamUserId
        {
            get => _steamUserId;
            set => SetValue(ref _steamUserId, value);
        }

        public bool UseSteamHuntersForCategories
        {
            get => _useSteamHuntersForCategories;
            set => SetValue(ref _useSteamHuntersForCategories, value);
        }

        public ObservableCollection<SteamIgnoredFriend> IgnoredFriends
        {
            get => _ignoredFriends;
            set => SetValue(
                ref _ignoredFriends,
                new ObservableCollection<SteamIgnoredFriend>(
                    (value ?? new ObservableCollection<SteamIgnoredFriend>())
                    .Where(friend => !string.IsNullOrWhiteSpace(friend?.SteamId))
                    .GroupBy(friend => friend.SteamId.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First().Normalize())));
        }

        public bool IsFriendIgnored(string steamId)
        {
            return !string.IsNullOrWhiteSpace(steamId) &&
                   IgnoredFriends.Any(friend =>
                       string.Equals(friend?.SteamId, steamId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public HashSet<string> GetIgnoredSteamIds()
        {
            return new HashSet<string>(
                IgnoredFriends
                    .Where(friend => !string.IsNullOrWhiteSpace(friend?.SteamId))
                    .Select(friend => friend.SteamId.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        public void AddIgnoredFriend(string steamId, string displayName, string avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return;
            }

            var normalizedId = steamId.Trim();
            var existing = IgnoredFriends.FirstOrDefault(friend =>
                string.Equals(friend?.SteamId, normalizedId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.DisplayName = string.IsNullOrWhiteSpace(displayName) ? existing.DisplayName : displayName.Trim();
                existing.AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? existing.AvatarUrl : avatarUrl.Trim();
                return;
            }

            IgnoredFriends.Add(new SteamIgnoredFriend
            {
                SteamId = normalizedId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedId : displayName.Trim(),
                AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim(),
                IgnoredUtc = DateTime.UtcNow
            });
            OnPropertyChanged(nameof(IgnoredFriends));
        }

        public bool RemoveIgnoredFriend(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return false;
            }

            var normalizedId = steamId.Trim();
            var removed = false;
            for (var i = IgnoredFriends.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(IgnoredFriends[i]?.SteamId, normalizedId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IgnoredFriends.RemoveAt(i);
                removed = true;
            }

            if (removed)
            {
                OnPropertyChanged(nameof(IgnoredFriends));
            }

            return removed;
        }
    }

    public sealed class SteamIgnoredFriend
    {
        public string SteamId { get; set; }
        public string DisplayName { get; set; }
        public string AvatarUrl { get; set; }
        public DateTime IgnoredUtc { get; set; }

        public DateTime IgnoredLocal => IgnoredUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(IgnoredUtc, DateTimeKind.Utc).ToLocalTime()
            : IgnoredUtc.ToUniversalTime().ToLocalTime();

        public SteamIgnoredFriend Normalize()
        {
            SteamId = SteamId?.Trim();
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? SteamId : DisplayName.Trim();
            AvatarUrl = string.IsNullOrWhiteSpace(AvatarUrl) ? null : AvatarUrl.Trim();
            if (IgnoredUtc == default)
            {
                IgnoredUtc = DateTime.UtcNow;
            }

            if (IgnoredUtc.Kind == DateTimeKind.Local)
            {
                IgnoredUtc = IgnoredUtc.ToUniversalTime();
            }

            return this;
        }
    }

}
