using Playnite.SDK;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Display row for the Steam settings friends table. Represents a single friend (active or
    /// ignored) and exposes the per-friend ignore toggle managed from settings.
    /// </summary>
    public sealed class SteamFriendListItem : ObservableObject
    {
        private bool _isIgnored;

        public string SteamId { get; set; }

        public string DisplayName { get; set; }

        public string AvatarUrl { get; set; }

        public bool IsIgnored
        {
            get => _isIgnored;
            set => SetValue(ref _isIgnored, value, nameof(IsIgnored), nameof(IgnoreActionLabel));
        }

        /// <summary>
        /// Label for the ignore/unignore action button, flipping with <see cref="IsIgnored"/>.
        /// </summary>
        public string IgnoreActionLabel => IsIgnored
            ? (ResourceProvider.GetString("LOCPlayAch_Menu_UnignoreFriend"))
            : (ResourceProvider.GetString("LOCPlayAch_Menu_IgnoreFriend"));
    }
}
