using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Friends;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// One selectable platform within a friend's platform popup. Raises the supplied callback when the
    /// user toggles it so the owning row can refresh its summary and persist.
    /// </summary>
    public sealed class ExophaseFriendPlatformToggle : PlayniteAchievements.Common.ObservableObject
    {
        private readonly Action _onChanged;
        private bool _isSelected;

        public ExophaseFriendPlatformToggle(string token, string label, bool isSelected, Action onChanged)
        {
            Token = token;
            Label = label;
            _isSelected = isSelected;
            _onChanged = onChanged;
        }

        public string Token { get; }

        public string Label { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetValueAndReturn(ref _isSelected, value))
                {
                    _onChanged?.Invoke();
                }
            }
        }
    }

    /// <summary>
    /// Display/edit row for the Exophase settings friends grid. Modeled on
    /// <see cref="PlayniteAchievements.Providers.Steam.SteamFriendListItem"/>: it wraps a single
    /// <see cref="ExophaseFriendSettings"/> and exposes the editable per-friend state (selected
    /// platforms) as observable properties. Editing a row mutates the view model in place (updating
    /// cells via binding) and invokes the change callback so the view can write the friend list back
    /// to settings — no ItemsSource resets or selection-driven rebuilds.
    /// </summary>
    public sealed class ExophaseFriendListItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly Action _onChanged;
        private readonly string _avatarUrl;
        private readonly DateTime _addedUtc;
        private readonly DateTime? _lastRefreshedUtc;
        private readonly DateTime? _lastProbedUtc;
        private readonly string _lastProbeStatus;

        public ExophaseFriendListItem(ExophaseFriendSettings friend, Action onChanged)
        {
            friend = friend ?? new ExophaseFriendSettings();
            _onChanged = onChanged;

            Username = friend.Username;
            DisplayName = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.Username : friend.DisplayName;
            AvatarPath = friend.AvatarPath;
            LastError = friend.LastError;

            _avatarUrl = friend.AvatarUrl;
            _addedUtc = friend.AddedUtc == default(DateTime) ? DateTime.UtcNow : friend.AddedUtc;
            _lastRefreshedUtc = friend.LastRefreshedUtc;
            _lastProbedUtc = friend.LastProbedUtc;
            _lastProbeStatus = friend.LastProbeStatus;

            var selected = new HashSet<string>(
                friend.SelectedPlatforms ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            Platforms = new ObservableCollection<ExophaseFriendPlatformToggle>(
                ExophaseFriendPlatformCatalog.Entries.Select(entry => new ExophaseFriendPlatformToggle(
                    entry.Token,
                    ResourceProvider.GetString(entry.LabelKey),
                    selected.Contains(entry.Token),
                    OnPlatformToggled)));
        }

        public string Username { get; }

        public string DisplayName { get; }

        public string AvatarPath { get; }

        public string LastError { get; }

        public ObservableCollection<ExophaseFriendPlatformToggle> Platforms { get; }

        public int SelectedPlatformCount => Platforms.Count(toggle => toggle.IsSelected);

        /// <summary>
        /// Label shown on the platforms popup button: a localized "Select platforms" prompt when none
        /// are selected, otherwise the localized "{0} selected" count.
        /// </summary>
        public string PlatformButtonLabel => SelectedPlatformCount == 0
            ? ResourceProvider.GetString("LOCPlayAch_Exophase_SelectPlatforms")
            : string.Format(
                ResourceProvider.GetString("LOCPlayAch_Common_SelectedCountFormat"),
                SelectedPlatformCount);

        /// <summary>
        /// Projects the current view model state back to a persisted <see cref="ExophaseFriendSettings"/>,
        /// preserving passthrough metadata (avatar, timestamps, probe status).
        /// </summary>
        public ExophaseFriendSettings ToModel()
        {
            return new ExophaseFriendSettings
            {
                Username = Username,
                DisplayName = DisplayName,
                AvatarUrl = _avatarUrl,
                AvatarPath = AvatarPath,
                SelectedPlatforms = Platforms.Where(toggle => toggle.IsSelected).Select(toggle => toggle.Token).ToList(),
                AddedUtc = _addedUtc,
                LastRefreshedUtc = _lastRefreshedUtc,
                LastProbedUtc = _lastProbedUtc,
                LastProbeStatus = _lastProbeStatus,
                LastError = LastError
            };
        }

        private void OnPlatformToggled()
        {
            OnPropertyChanged(nameof(SelectedPlatformCount));
            OnPropertyChanged(nameof(PlatformButtonLabel));
            _onChanged?.Invoke();
        }
    }
}
