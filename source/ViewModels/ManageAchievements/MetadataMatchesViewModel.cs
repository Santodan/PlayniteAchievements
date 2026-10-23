using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Metadata;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The Achievement matches window for one game and one metadata source: one row per
    /// achievement, each with a searchable dropdown of the source's rows. Pairings are staged here
    /// and handed back together by <see cref="GetChanges"/> when the user saves.
    /// </summary>
    public sealed class MetadataMatchesViewModel : ObservableObject
    {
        private readonly Dictionary<string, MatchOption> _trophiesByApiName;
        private readonly List<MatchOption> _trophies;
        private bool _unmatchedOnly;

        internal MetadataMatchesViewModel(
            string sourceName,
            IReadOnlyList<AchievementDetail> targets,
            MetadataSourceSnapshot snapshot,
            IReadOnlyDictionary<string, string> pairings)
        {
            SourceName = sourceName;
            _trophies = (snapshot?.Rows ?? new List<MetadataSourceRow>())
                .Select(MatchOption.ForRow)
                .ToList();
            _trophiesByApiName = _trophies
                .GroupBy(trophy => trophy.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            Automatic = MatchOption.Special(null, ResourceProvider.GetString("LOCAutomatic"));
            None = MatchOption.Special(string.Empty, ResourceProvider.GetString("LOCNone"));

            var matches = snapshot?.Matches ?? new Dictionary<string, string>();
            pairings = pairings ?? new Dictionary<string, string>();
            var rows = (targets ?? Array.Empty<AchievementDetail>())
                .Select(target =>
                {
                    matches.TryGetValue(target.ApiName, out var automatic);
                    var hasPairing = pairings.TryGetValue(target.ApiName, out var paired);
                    return new MatchTargetItem(this, target, automatic, hasPairing ? paired ?? string.Empty : null);
                })
                .OrderBy(target => target.IsMatched ? 1 : 0)
                .ToList();

            Rows = rows;
            Targets = CollectionViewSource.GetDefaultView(rows);
            _unmatchedOnly = rows.Any(target => !target.IsMatched);
            Targets.Filter = item => !_unmatchedOnly || !((MatchTargetItem)item).WasMatched;
            RefreshPairedWith();
        }

        public string SourceName { get; }

        public ICollectionView Targets { get; }

        internal IReadOnlyList<MatchTargetItem> Rows { get; }

        internal MatchOption Automatic { get; }

        internal MatchOption None { get; }

        internal IReadOnlyList<MatchOption> Trophies => _trophies;

        /// <summary>
        /// Lists only the achievements that were unmatched when the window opened, so one paired
        /// here stays in view; on when any were.
        /// </summary>
        public bool UnmatchedOnly
        {
            get => _unmatchedOnly;
            set
            {
                _unmatchedOnly = value;
                OnPropertyChanged();
                Targets.Refresh();
            }
        }

        /// <summary>The pairings that differ from the saved ones: a source ApiName, "" for none, or null to leave to matching.</summary>
        public IReadOnlyDictionary<string, string> GetChanges()
        {
            return Rows
                .Where(target => target.IsChanged)
                .ToDictionary(target => target.ApiName, target => target.Staged, StringComparer.OrdinalIgnoreCase);
        }

        internal MatchOption FindTrophy(string apiName) =>
            !string.IsNullOrEmpty(apiName) && _trophiesByApiName.TryGetValue(apiName, out var trophy) ? trophy : null;

        internal void RefreshPairedWith()
        {
            var pairedWith = Rows
                .Where(target => target.Trophy != null)
                .GroupBy(target => target.Trophy)
                .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(target => target.Name)));
            foreach (var trophy in _trophies)
            {
                trophy.SetPairedWith(pairedWith.TryGetValue(trophy, out var names) ? names : null);
            }
        }
    }

    /// <summary>One of the game's achievements, what it is paired with, and its searchable dropdown.</summary>
    public sealed class MatchTargetItem : ObservableObject
    {
        private readonly MetadataMatchesViewModel _owner;
        private readonly string _automatic;
        private readonly string _saved;
        private string _staged;
        private string _search = string.Empty;
        private bool _isPickerOpen;

        internal MatchTargetItem(MetadataMatchesViewModel owner, AchievementDetail target, string automatic, string saved)
        {
            _owner = owner;
            _automatic = string.IsNullOrWhiteSpace(automatic) ? null : automatic;
            _saved = saved;
            _staged = saved;
            ApiName = target.ApiName;
            Name = string.IsNullOrWhiteSpace(target.DisplayName) ? target.ApiName : target.DisplayName;
            Description = target.Description;
            Icon = string.IsNullOrWhiteSpace(target.UnlockedIconPath) ? target.LockedIconPath : target.UnlockedIconPath;
            WasMatched = IsMatched;
        }

        public string ApiName { get; }

        public string Name { get; }

        public string Description { get; }

        public string Icon { get; }

        /// <summary>The user's pairing as it stands in the window: a source ApiName, "" for none, or null for matching.</summary>
        internal string Staged => _staged;

        /// <summary>Whether it was matched when the window opened; the Unmatched only filter keys off this.</summary>
        internal bool WasMatched { get; }

        public bool IsUserSet => _staged != null;

        public bool IsChanged => !string.Equals(_staged, _saved, StringComparison.Ordinal);

        /// <summary>The trophy it is paired with now, by the user or by matching.</summary>
        public MatchOption Trophy => _owner.FindTrophy(_staged ?? _automatic);

        public bool IsMatched => Trophy != null;

        /// <summary>The paired trophy's name, None, or blank while unmatched.</summary>
        public string PairingText => Trophy?.Name ?? (_staged == string.Empty
            ? ResourceProvider.GetString("LOCNone")
            : string.Empty);

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

        private IReadOnlyList<MatchOption> _options;

        /// <summary>
        /// Automatic (when the user set a pairing), None, then the source's rows matching the
        /// search. Built once per opening and search, so the dropdown keeps one list to scroll.
        /// </summary>
        public IReadOnlyList<MatchOption> Options => _options ?? (_options = BuildOptions());

        private void RefreshOptions()
        {
            _options = null;
            OnPropertyChanged(nameof(Options));
        }

        private IReadOnlyList<MatchOption> BuildOptions()
        {
            var options = new List<MatchOption>();
            if (IsUserSet)
            {
                options.Add(_owner.Automatic);
            }

            options.Add(_owner.None);
            var search = _search.Trim();
            options.AddRange(_owner.Trophies.Where(trophy =>
                search.Length == 0 ||
                (trophy.Name ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                (trophy.Description ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                (trophy.List ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0));
            return options;
        }

        internal void Pick(MatchOption option)
        {
            if (option == null)
            {
                return;
            }

            _staged = option.Key;
            IsPickerOpen = false;
            OnPropertyChanged(nameof(IsUserSet));
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Trophy));
            OnPropertyChanged(nameof(IsMatched));
            OnPropertyChanged(nameof(PairingText));
            _owner.RefreshPairedWith();
        }
    }

    /// <summary>
    /// A choice in a pairing dropdown: one of the source's rows, or the None (empty key) and
    /// Automatic (null key) choices.
    /// </summary>
    public sealed class MatchOption : ObservableObject
    {
        private string _pairedWith;

        private MatchOption(string key, string name, string description, string icon, string list, bool isSpecial)
        {
            Key = key;
            Name = name;
            Description = description;
            Icon = icon;
            List = list;
            IsSpecial = isSpecial;
        }

        internal static MatchOption ForRow(MetadataSourceRow row) =>
            new MatchOption(row.ApiName, row.Name, row.Description, MetadataSourceSnapshotStore.LocalIconPath(row.Icon), row.List, isSpecial: false);

        internal static MatchOption Special(string key, string name) =>
            new MatchOption(key, name, null, null, null, isSpecial: true);

        /// <summary>The list it comes from when the link names several (a collection's game), else null.</summary>
        public string List { get; }

        public bool HasList => !string.IsNullOrEmpty(List);

        public string Key { get; }

        public string Name { get; }

        public string Description { get; }

        public string Icon { get; }

        public bool IsSpecial { get; }

        /// <summary>The achievements paired with it, or null.</summary>
        public string PairedWith => _pairedWith;

        public bool IsPaired => _pairedWith != null;

        internal void SetPairedWith(string names)
        {
            if (string.Equals(_pairedWith, names, StringComparison.Ordinal))
            {
                return;
            }

            _pairedWith = names;
            OnPropertyChanged(nameof(PairedWith));
            OnPropertyChanged(nameof(IsPaired));
        }
    }
}
