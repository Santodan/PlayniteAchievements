using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// A row with a searchable pairing dropdown: its open state, its search text and the options
    /// the search leaves. Shared by the Achievement matches window and the Link File window.
    /// </summary>
    public abstract class MatchPickerItem : ObservableObject
    {
        private string _search = string.Empty;
        private bool _isPickerOpen;
        private IReadOnlyList<MatchOption> _options;

        public bool IsPickerOpen
        {
            get => _isPickerOpen;
            set
            {
                if (_isPickerOpen == value)
                {
                    return;
                }

                _isPickerOpen = value;
                if (value)
                {
                    _search = string.Empty;
                    OnPropertyChanged(nameof(Search));
                    RefreshOptions();
                }

                OnPropertyChanged();
            }
        }

        public string Search
        {
            get => _search;
            set
            {
                _search = value ?? string.Empty;
                OnPropertyChanged();
                RefreshOptions();
            }
        }

        /// <summary>
        /// The options for the current search. Built once per opening and search, so the
        /// dropdown keeps one list to scroll.
        /// </summary>
        public IReadOnlyList<MatchOption> Options => _options ?? (_options = BuildOptions(_search.Trim()));

        /// <summary>Pairs the row with the option and closes the dropdown.</summary>
        public abstract void Pick(MatchOption option);

        /// <summary>The special choices first, then every option the search matches.</summary>
        protected abstract IReadOnlyList<MatchOption> BuildOptions(string search);

        protected void RefreshOptions()
        {
            _options = null;
            OnPropertyChanged(nameof(Options));
        }

        /// <summary>Whether the option's name, description or list contains the search.</summary>
        protected static bool MatchesSearch(MatchOption option, string search)
        {
            return search.Length == 0 ||
                   (option.Name ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   (option.Description ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   (option.List ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// A choice in a pairing dropdown: a metadata source's row or one of the game's achievements,
    /// or the None (empty key) and Automatic (null key) choices.
    /// </summary>
    public sealed class MatchOption : ObservableObject
    {
        private readonly string _ownIcon;
        private string _pairedWith;
        private string _pairedIcon;

        private MatchOption(string key, string name, string description, string ownIcon, string list, bool isSpecial)
        {
            Key = key;
            Name = name;
            Description = description;
            _ownIcon = ownIcon;
            List = list;
            IsSpecial = isSpecial;
        }

        internal static MatchOption Create(string key, string name, string description, string icon, string list) =>
            new MatchOption(key, name, description, icon, list, isSpecial: false);

        internal static MatchOption ForAchievement(AchievementDetail achievement) =>
            new MatchOption(
                achievement.ApiName,
                string.IsNullOrWhiteSpace(achievement.DisplayName) ? achievement.ApiName : achievement.DisplayName,
                achievement.Description,
                string.IsNullOrWhiteSpace(achievement.UnlockedIconPath) ? achievement.LockedIconPath : achievement.UnlockedIconPath,
                list: null,
                isSpecial: false);

        internal static MatchOption Special(string key, string name) =>
            new MatchOption(key, name, null, null, null, isSpecial: true);

        /// <summary>The list it comes from when the link names several (a collection's game), else null.</summary>
        public string List { get; }

        public bool HasList => !string.IsNullOrEmpty(List);

        public string Key { get; }

        public string Name { get; }

        public string Description { get; }

        public bool IsSpecial { get; }

        /// <summary>What it is paired with now, or null.</summary>
        public string PairedWith => _pairedWith;

        public bool IsPaired => _pairedWith != null;

        /// <summary>
        /// The icon of the first achievement paired with it, so both sides of a pairing show the
        /// same icon; while unpaired, its own icon when the source has one on disk, else null.
        /// </summary>
        public string Icon => _pairedIcon ?? _ownIcon;

        internal void SetPairedWith(string names, string icon)
        {
            if (string.Equals(_pairedWith, names, StringComparison.Ordinal) &&
                string.Equals(_pairedIcon, icon, StringComparison.Ordinal))
            {
                return;
            }

            _pairedWith = names;
            _pairedIcon = string.IsNullOrWhiteSpace(icon) ? null : icon;
            OnPropertyChanged(nameof(PairedWith));
            OnPropertyChanged(nameof(IsPaired));
            OnPropertyChanged(nameof(Icon));
        }
    }
}
