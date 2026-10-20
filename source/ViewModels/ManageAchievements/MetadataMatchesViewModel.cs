using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Metadata;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The Achievement matches window for one game and one metadata source: the game's
    /// achievements on one side and the source's rows on the other, each searchable. Pairings are
    /// staged here and handed back together by <see cref="GetChanges"/> when the user saves.
    /// </summary>
    public sealed class MetadataMatchesViewModel : ObservableObject
    {
        private readonly Dictionary<string, MatchTrophyItem> _trophiesByApiName;
        private string _targetSearch = string.Empty;
        private string _trophySearch = string.Empty;
        private bool _unmatchedOnly;
        private MatchTargetItem _selectedTarget;
        private MatchTrophyItem _selectedTrophy;

        internal MetadataMatchesViewModel(
            string sourceName,
            IReadOnlyList<AchievementDetail> targets,
            MetadataSourceSnapshot snapshot,
            IReadOnlyDictionary<string, string> pairings)
        {
            SourceName = sourceName;
            var trophies = (snapshot?.Rows ?? new List<MetadataSourceRow>())
                .Select(row => new MatchTrophyItem(row))
                .ToList();
            _trophiesByApiName = trophies
                .GroupBy(trophy => trophy.ApiName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var matches = snapshot?.Matches ?? new Dictionary<string, string>();
            pairings = pairings ?? new Dictionary<string, string>();
            var targetItems = (targets ?? Array.Empty<AchievementDetail>())
                .Select(target =>
                {
                    matches.TryGetValue(target.ApiName, out var automatic);
                    var hasPairing = pairings.TryGetValue(target.ApiName, out var paired);
                    return new MatchTargetItem(this, target, automatic, hasPairing ? paired ?? string.Empty : null);
                })
                .OrderBy(target => target.IsMatched ? 1 : 0)
                .ToList();

            Trophies = CollectionViewSource.GetDefaultView(trophies);
            Trophies.Filter = item => Matches(((MatchTrophyItem)item).SearchText, _trophySearch);
            TargetItems = targetItems;
            Targets = CollectionViewSource.GetDefaultView(targetItems);
            Targets.Filter = item =>
            {
                var target = (MatchTargetItem)item;
                return (!_unmatchedOnly || !target.IsMatched) && Matches(target.SearchText, _targetSearch);
            };
            _unmatchedOnly = targetItems.Any(target => !target.IsMatched);
            Targets.Refresh();
            RefreshPairedWith();

            PairCommand = new RelayCommand(_ => Stage(SelectedTrophy?.ApiName), _ => SelectedTarget != null && SelectedTrophy != null);
            NoneCommand = new RelayCommand(_ => Stage(string.Empty), _ => SelectedTarget != null);
            AutomaticCommand = new RelayCommand(_ => Stage(null), _ => SelectedTarget?.IsUserSet == true);
        }

        public string SourceName { get; }

        public ICollectionView Targets { get; }

        public ICollectionView Trophies { get; }

        internal IReadOnlyList<MatchTargetItem> TargetItems { get; }

        public string TargetSearch
        {
            get => _targetSearch;
            set
            {
                _targetSearch = value ?? string.Empty;
                OnPropertyChanged();
                Targets.Refresh();
            }
        }

        public string TrophySearch
        {
            get => _trophySearch;
            set
            {
                _trophySearch = value ?? string.Empty;
                OnPropertyChanged();
                Trophies.Refresh();
            }
        }

        /// <summary>Lists only the achievements nothing is paired with; on when any are.</summary>
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

        public MatchTargetItem SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                _selectedTarget = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public MatchTrophyItem SelectedTrophy
        {
            get => _selectedTrophy;
            set
            {
                _selectedTrophy = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand PairCommand { get; }

        public ICommand NoneCommand { get; }

        public ICommand AutomaticCommand { get; }

        /// <summary>The pairings that differ from the saved ones: a source ApiName, "" for none, or null to leave to matching.</summary>
        public IReadOnlyDictionary<string, string> GetChanges()
        {
            return TargetItems
                .Where(target => target.IsChanged)
                .ToDictionary(target => target.ApiName, target => target.Staged, StringComparer.OrdinalIgnoreCase);
        }

        internal MatchTrophyItem FindTrophy(string apiName) =>
            !string.IsNullOrEmpty(apiName) && _trophiesByApiName.TryGetValue(apiName, out var trophy) ? trophy : null;

        /// <summary>Pairs the selected achievement, so a double-clicked trophy pairs at once.</summary>
        internal void PairSelected(MatchTrophyItem trophy)
        {
            if (SelectedTarget != null && trophy != null)
            {
                SelectedTrophy = trophy;
                Stage(trophy.ApiName);
            }
        }

        private void Stage(string value)
        {
            if (SelectedTarget == null)
            {
                return;
            }

            SelectedTarget.Stage(value);
            RefreshPairedWith();
            CommandManager.InvalidateRequerySuggested();
        }

        private void RefreshPairedWith()
        {
            var pairedWith = TargetItems
                .Where(target => target.Trophy != null)
                .GroupBy(target => target.Trophy)
                .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(target => target.Name)));
            foreach (var trophy in _trophiesByApiName.Values)
            {
                trophy.SetPairedWith(pairedWith.TryGetValue(trophy, out var names) ? names : null);
            }
        }

        private static bool Matches(string text, string search)
        {
            return string.IsNullOrWhiteSpace(search) ||
                   (text ?? string.Empty).IndexOf(search.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }

    /// <summary>One of the game's achievements and what it is paired with.</summary>
    public sealed class MatchTargetItem : ObservableObject
    {
        private readonly MetadataMatchesViewModel _owner;
        private readonly string _automatic;
        private readonly string _saved;
        private string _staged;

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
        }

        public string ApiName { get; }

        public string Name { get; }

        public string Description { get; }

        public string Icon { get; }

        internal string SearchText => Name + " " + Description;

        /// <summary>The user's pairing as it stands in the window: a source ApiName, "" for none, or null for matching.</summary>
        internal string Staged => _staged;

        public bool IsUserSet => _staged != null;

        public bool IsChanged => !string.Equals(_staged, _saved, StringComparison.Ordinal);

        /// <summary>The trophy it is paired with now, by the user or by matching.</summary>
        public MatchTrophyItem Trophy => _owner.FindTrophy(_staged ?? _automatic);

        public bool IsMatched => Trophy != null;

        public bool IsNone => _staged == string.Empty;

        public string PairingText => Trophy?.Name ?? (IsNone
            ? ResourceProvider.GetString("LOCNone")
            : ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Overview_MetadataMatches_NotMatched"));

        internal void Stage(string value)
        {
            _staged = value;
            OnPropertyChanged(nameof(IsUserSet));
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Trophy));
            OnPropertyChanged(nameof(IsMatched));
            OnPropertyChanged(nameof(IsNone));
            OnPropertyChanged(nameof(PairingText));
        }
    }

    /// <summary>One of the source's rows.</summary>
    public sealed class MatchTrophyItem : ObservableObject
    {
        private string _pairedWith;

        internal MatchTrophyItem(MetadataSourceRow row)
        {
            ApiName = row.ApiName;
            Name = row.Name;
            Description = row.Description;
            Icon = row.Icon;
        }

        public string ApiName { get; }

        public string Name { get; }

        public string Description { get; }

        public string Icon { get; }

        internal string SearchText => Name + " " + Description;

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
