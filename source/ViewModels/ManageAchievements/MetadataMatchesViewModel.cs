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
                .Select(row => MatchOption.Create(row.ApiName, row.Name, row.Description, MetadataSourceSnapshotStore.LocalIconPath(row.Icon), row.List))
                .ToList();
            _trophiesByApiName = _trophies
                .GroupBy(trophy => trophy.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            Automatic = MatchOption.Special(null, ResourceProvider.GetString("LOCPlayAch_Common_Automatic"));
            None = MatchOption.Special(string.Empty, ResourceProvider.GetString("LOCPlayAch_Common_None"));

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
                .ToDictionary(group => group.Key, group => group.ToList());
            foreach (var trophy in _trophies)
            {
                if (pairedWith.TryGetValue(trophy, out var targets))
                {
                    trophy.SetPairedWith(string.Join(", ", targets.Select(target => target.Name)), targets[0].Icon);
                }
                else
                {
                    trophy.SetPairedWith(null, null);
                }
            }
        }
    }

    /// <summary>One of the game's achievements, what it is paired with, and its searchable dropdown.</summary>
    public sealed class MatchTargetItem : MatchPickerItem
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
            ? ResourceProvider.GetString("LOCPlayAch_Common_None")
            : string.Empty);

        /// <summary>Automatic (when the user set a pairing), None, then the source's rows matching the search.</summary>
        protected override IReadOnlyList<MatchOption> BuildOptions(string search)
        {
            var options = new List<MatchOption>();
            if (IsUserSet)
            {
                options.Add(_owner.Automatic);
            }

            options.Add(_owner.None);
            options.AddRange(_owner.Trophies.Where(trophy => MatchesSearch(trophy, search)));
            return options;
        }

        public override void Pick(MatchOption option)
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
}
